using JobLens.Core.Configuration;
using JobLens.Core.Llm;
using JobLens.Core.Parsing;
using JobLens.Core.Scoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Scoring;

public class LlmRelevanceScorerTests
{
    private static JobPosting MakePosting(string title) =>
        new(title, "Acme", "Tel Aviv", "Software", $"https://example.com/{title}", "- test requirement");

    private static (string Id, JobPosting Posting, float[] Embedding) MakeCandidate(string title, float[] embedding) =>
        ($"id-{title}", MakePosting(title), embedding);

    private static LlmRelevanceScorer CreateScorer(FakeChatClient chatClient, int scoringTopK = 10) =>
        new(new ScoringChatClient(chatClient),
            Options.Create(new JobLensOptions { Profile = "test profile", ScoringTopK = scoringTopK }),
            NullLogger<LlmRelevanceScorer>.Instance);

    [Fact]
    public async Task ScoreAsync_HappyPath_ReturnsScoresAndReasoningSortedDescending()
    {
        // Ranked shortlist order is cosine-to-profile descending, so index 0 is
        // whichever candidate is closest to the profile embedding - Strong Match here.
        var chatClient = new FakeChatClient("""
            [{"index":0,"score":90,"reasoning":"Strong match on C# and Postgres."},
             {"index":1,"score":40,"reasoning":"Some overlap with the profile."}]
            """);
        var scorer = CreateScorer(chatClient);

        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("Strong Match", [0f, 1f, 0f]),
            MakeCandidate("Weak Match", [1f, 0f, 0f]),
        };

        var results = await scorer.ScoreAsync(candidates, [0f, 1f, 0f]);

        Assert.Equal(2, results.Count);
        Assert.Equal("id-Strong Match", results[0].Id);
        Assert.Equal("Strong Match", results[0].Posting.Title);
        Assert.Equal(90, results[0].Score);
        Assert.Equal("Weak Match", results[1].Posting.Title);
        Assert.Equal(40, results[1].Score);
        Assert.Equal(1, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_SetsJsonResponseFormat()
    {
        var chatClient = new FakeChatClient("[]");
        var scorer = CreateScorer(chatClient);

        await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        var options = Assert.Single(chatClient.Options);
        Assert.Same(ChatResponseFormat.Json, options?.ResponseFormat);
    }

    [Fact]
    public async Task ScoreAsync_NoCandidates_DoesNotCallModel()
    {
        var chatClient = new FakeChatClient();
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([], [1f, 0f]);

        Assert.Empty(results);
        Assert.Equal(0, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_MoreCandidatesThanTopK_OnlySendsTopKByCosineSimilarity()
    {
        var chatClient = new FakeChatClient(
            """[{"index":0,"score":80,"reasoning":"Good fit."},{"index":1,"score":70,"reasoning":"Decent fit."}]""");
        var scorer = CreateScorer(chatClient, scoringTopK: 2);

        var profileEmbedding = new float[] { 1f, 0f, 0f };
        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("Best Match", [1f, 0f, 0f]),       // cosine 1.0
            MakeCandidate("Second Best", [0.9f, 0.1f, 0f]),  // cosine ~0.99
            MakeCandidate("Worst Match", [0f, 1f, 0f]),      // cosine 0.0
        };

        await scorer.ScoreAsync(candidates, profileEmbedding);

        Assert.Equal(1, chatClient.CallCount);
        var promptText = string.Join(" ", chatClient.Requests[0].Select(m => m.Text));
        Assert.Contains("Best Match", promptText);
        Assert.Contains("Second Best", promptText);
        Assert.DoesNotContain("Worst Match", promptText);
    }

    [Fact]
    public async Task ScoreAsync_MarkdownFencedJson_ParsesSuccessfully()
    {
        var chatClient = new FakeChatClient("""
            ```json
            [{"index":0,"score":75,"reasoning":"Fits the profile."}]
            ```
            """);
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        Assert.Single(results);
        Assert.Equal(75, results[0].Score);
        Assert.Equal(1, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_InvalidJsonThenValid_RetriesOnceAndSucceeds()
    {
        var chatClient = new FakeChatClient(
            "this is not json",
            """[{"index":0,"score":75,"reasoning":"Fits the backend/QA profile well."}]""");
        var scorer = CreateScorer(chatClient);

        var candidates = new List<(string, JobPosting, float[])> { MakeCandidate("Only Candidate", [1f, 0f, 0f]) };

        var results = await scorer.ScoreAsync(candidates, [1f, 0f, 0f]);

        Assert.Single(results);
        Assert.Equal(75, results[0].Score);
        Assert.Equal(2, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_InvalidJsonTwice_ReturnsEmptyWithoutThrowing()
    {
        var chatClient = new FakeChatClient("not json", "still not json");
        var scorer = CreateScorer(chatClient);

        var candidates = new List<(string, JobPosting, float[])> { MakeCandidate("Only Candidate", [1f, 0f, 0f]) };

        var results = await scorer.ScoreAsync(candidates, [1f, 0f, 0f]);

        Assert.Empty(results);
        Assert.Equal(2, chatClient.CallCount); // retried once, then gave up - no third attempt
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ScoreAsync_InvalidIndex_SkipsItKeepsValidItem(int invalidIndex)
    {
        var chatClient = new FakeChatClient($$"""
            [{"index":{{invalidIndex}},"score":80,"reasoning":"Invalid index."},
             {"index":0,"score":60,"reasoning":"Valid entry, kept."}]
            """);
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        var result = Assert.Single(results);
        Assert.Equal(60, result.Score);
    }

    [Fact]
    public async Task ScoreAsync_DuplicateIndex_SkipsLaterOccurrence()
    {
        var chatClient = new FakeChatClient("""
            [{"index":0,"score":80,"reasoning":"First occurrence."},
             {"index":0,"score":90,"reasoning":"Duplicate occurrence."}]
            """);
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        var result = Assert.Single(results);
        Assert.Equal(80, result.Score);
        Assert.Equal("First occurrence.", result.Reasoning);
    }

    [Fact]
    public async Task ScoreAsync_InvalidFirstDuplicate_StillClaimsIndex()
    {
        var chatClient = new FakeChatClient("""
            [{"index":0,"score":150,"reasoning":"Invalid score."},
             {"index":0,"score":90,"reasoning":"Otherwise valid duplicate."}]
            """);
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ScoreAsync_OutOfRangeScore_SkipsItKeepsValidItem(int invalidScore)
    {
        var chatClient = new FakeChatClient($$"""
            [{"index":0,"score":{{invalidScore}},"reasoning":"Invalid score."},
             {"index":1,"score":60,"reasoning":"Valid entry, kept."}]
            """);
        var scorer = CreateScorer(chatClient);
        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("First Item", [1f, 0f]),
            MakeCandidate("Second Item", [0f, 1f]),
        };

        var results = await scorer.ScoreAsync(candidates, [1f, 0f]);

        var result = Assert.Single(results);
        Assert.Equal("Second Item", result.Posting.Title);
        Assert.Equal(60, result.Score);
    }

    [Fact]
    public async Task ScoreAsync_BlankReasoning_SkipsItKeepsValidItem()
    {
        var chatClient = new FakeChatClient("""
            [{"index":0,"score":80,"reasoning":"   "},
             {"index":1,"score":60,"reasoning":"Valid entry, kept."}]
            """);
        var scorer = CreateScorer(chatClient);
        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("First Item", [1f, 0f]),
            MakeCandidate("Second Item", [0f, 1f]),
        };

        var results = await scorer.ScoreAsync(candidates, [1f, 0f]);

        var result = Assert.Single(results);
        Assert.Equal("Second Item", result.Posting.Title);
    }

    [Fact]
    public async Task ScoreAsync_TransportFailure_ReturnsEmptyWithoutRetrying()
    {
        var chatClient = new FakeChatClient(new HttpRequestException("OmniRoute unavailable"));
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f]);

        Assert.Empty(results);
        Assert.Equal(1, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_CallerCancellation_Propagates()
    {
        var chatClient = new FakeChatClient();
        var scorer = CreateScorer(chatClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f])], [1f, 0f], cancellation.Token));

        Assert.Equal(1, chatClient.CallCount);
    }
}
