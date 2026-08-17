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

    private static LlmRelevanceScorer CreateScorer(
        FakeChatClient chatClient,
        int scoringTopK = 10,
        FakeTemplateCatalog? templateCatalog = null) =>
        new(new ScoringChatClient(chatClient),
            templateCatalog ?? new FakeTemplateCatalog(
                new ScoringTemplate("General", "test profile", [1f, 0f, 0f])),
            Options.Create(new JobLensOptions { ScoringTopK = scoringTopK }),
            NullLogger<LlmRelevanceScorer>.Instance);

    [Fact]
    public async Task ScoreAsync_HappyPath_ReturnsScoresAndReasoningSortedDescending()
    {
        // Ranked shortlist order is cosine-to-template descending, so index 0 is
        // whichever candidate is closest to the selected template - Strong Match here.
        var chatClient = new FakeChatClient("""
            [{"index":0,"score":90,"reasoning":"Strong match on C# and Postgres."},
             {"index":1,"score":40,"reasoning":"Some overlap with the profile."}]
            """);
        var scorer = CreateScorer(chatClient, templateCatalog: new FakeTemplateCatalog(
            new ScoringTemplate("General", "test profile", [0f, 1f, 0f])));

        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("Strong Match", [0f, 1f, 0f]),
            MakeCandidate("Weak Match", [1f, 0f, 0f]),
        };

        var results = await scorer.ScoreAsync(candidates);

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

        await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

        var options = Assert.Single(chatClient.Options);
        Assert.Same(ChatResponseFormat.Json, options?.ResponseFormat);
    }

    [Fact]
    public async Task ScoreAsync_NoCandidates_DoesNotCallModel()
    {
        var chatClient = new FakeChatClient();
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([]);

        Assert.Empty(results);
        Assert.Equal(0, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_MoreCandidatesThanTopK_OnlySendsTopKByCosineSimilarity()
    {
        var chatClient = new FakeChatClient(
            """[{"index":0,"score":80,"reasoning":"Good fit."},{"index":1,"score":70,"reasoning":"Decent fit."}]""");
        var scorer = CreateScorer(chatClient, scoringTopK: 2);

        var candidates = new List<(string, JobPosting, float[])>
        {
            MakeCandidate("Best Match", [1f, 0f, 0f]),       // cosine 1.0
            MakeCandidate("Second Best", [0.9f, 0.1f, 0f]),  // cosine ~0.99
            MakeCandidate("Worst Match", [0f, 1f, 0f]),      // cosine 0.0
        };

        await scorer.ScoreAsync(candidates);

        Assert.Equal(1, chatClient.CallCount);
        var promptText = string.Join(" ", chatClient.Requests[0].Select(m => m.Text));
        Assert.Contains("Best Match", promptText);
        Assert.Contains("Second Best", promptText);
        Assert.DoesNotContain("Worst Match", promptText);
    }

    [Fact]
    public async Task ScoreAsync_RoutesToBestTemplate_GroupsPromptsAndPersistsTrustedTemplateName()
    {
        var chatClient = new FakeChatClient(
            """[{"index":0,"score":82,"reasoning":"Backend fit."}]""",
            """[{"index":0,"score":91,"reasoning":"QA fit."}]""");
        var catalog = new FakeTemplateCatalog(
            new ScoringTemplate("Backend", "backend-profile-marker", [1f, 0f, 0f]),
            new ScoringTemplate("QA", "qa-profile-marker", [0f, 1f, 0f]));
        var scorer = CreateScorer(chatClient, templateCatalog: catalog);
        var backend = MakeCandidate("Backend Job", [0.9f, 0.1f, 0f]);
        var qa = MakeCandidate("QA Job", [0.1f, 0.9f, 0f]);

        var results = await scorer.ScoreAsync([backend, qa]);

        Assert.Equal(2, chatClient.CallCount);
        var backendPrompt = string.Join(" ", chatClient.Requests[0].Select(m => m.Text));
        Assert.Contains("backend-profile-marker", backendPrompt);
        Assert.Contains("Backend Job", backendPrompt);
        Assert.DoesNotContain("qa-profile-marker", backendPrompt);
        Assert.DoesNotContain("QA Job", backendPrompt);
        var qaPrompt = string.Join(" ", chatClient.Requests[1].Select(m => m.Text));
        Assert.Contains("qa-profile-marker", qaPrompt);
        Assert.Contains("QA Job", qaPrompt);
        Assert.DoesNotContain("backend-profile-marker", qaPrompt);
        Assert.DoesNotContain("Backend Job", qaPrompt);

        Assert.Equal("QA", results.Single(r => r.Id == qa.Id).TemplateName);
        Assert.Equal("Backend", results.Single(r => r.Id == backend.Id).TemplateName);
    }

    [Fact]
    public async Task ScoreAsync_GlobalTopK_AppliesAcrossTemplatesBeforeGrouping()
    {
        var chatClient = new FakeChatClient(
            """[{"index":0,"score":90,"reasoning":"Best backend fit."}]""",
            """[{"index":0,"score":80,"reasoning":"Best QA fit."}]""");
        var catalog = new FakeTemplateCatalog(
            new ScoringTemplate("Backend", "backend profile", [1f, 0f, 0f]),
            new ScoringTemplate("QA", "qa profile", [0f, 1f, 0f]));
        var scorer = CreateScorer(chatClient, scoringTopK: 2, templateCatalog: catalog);
        var bestBackend = MakeCandidate("Best Backend", [1f, 0f, 0f]);
        var weakerBackend = MakeCandidate("Weaker Backend", [0.8f, 0.3f, 0f]);
        var bestQa = MakeCandidate("Best QA", [0f, 1f, 0f]);

        var results = await scorer.ScoreAsync([bestBackend, weakerBackend, bestQa]);

        Assert.Equal(2, results.Count);
        Assert.Equal([bestBackend.Id, bestQa.Id], results.Select(r => r.Id).OrderBy(id => id));
        Assert.DoesNotContain(results, r => r.Id == weakerBackend.Id);
        var combinedPrompts = string.Join(" ", chatClient.Requests.SelectMany(r => r).Select(m => m.Text));
        Assert.DoesNotContain("Weaker Backend", combinedPrompts);
    }

    [Fact]
    public async Task ScoreAsync_OneTemplateGroupFailsSoft_OtherGroupStillReturnsScores()
    {
        var chatClient = new FakeChatClient(
            new HttpRequestException("Backend provider failure"),
            """[{"index":0,"score":88,"reasoning":"QA fit survives."}]""");
        var catalog = new FakeTemplateCatalog(
            new ScoringTemplate("Backend", "backend profile", [1f, 0f, 0f]),
            new ScoringTemplate("QA", "qa profile", [0f, 1f, 0f]));
        var scorer = CreateScorer(chatClient, templateCatalog: catalog);
        var backend = MakeCandidate("Backend Job", [1f, 0f, 0f]);
        var qa = MakeCandidate("QA Job", [0f, 1f, 0f]);

        var results = await scorer.ScoreAsync([backend, qa]);

        var result = Assert.Single(results);
        Assert.Equal(qa.Id, result.Id);
        Assert.Equal("QA", result.TemplateName);
        Assert.Equal(2, chatClient.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_NoTemplates_DoesNotCallModel()
    {
        var chatClient = new FakeChatClient();
        var scorer = CreateScorer(chatClient, templateCatalog: new FakeTemplateCatalog());

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

        Assert.Empty(results);
        Assert.Equal(0, chatClient.CallCount);
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

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

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

        var results = await scorer.ScoreAsync(candidates);

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

        var results = await scorer.ScoreAsync(candidates);

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

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

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

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

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

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

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
            MakeCandidate("First Item", [1f, 0f, 0f]),
            MakeCandidate("Second Item", [0f, 1f, 0f]),
        };

        var results = await scorer.ScoreAsync(candidates);

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
            MakeCandidate("First Item", [1f, 0f, 0f]),
            MakeCandidate("Second Item", [0f, 1f, 0f]),
        };

        var results = await scorer.ScoreAsync(candidates);

        var result = Assert.Single(results);
        Assert.Equal("Second Item", result.Posting.Title);
    }

    [Fact]
    public async Task ScoreAsync_TransportFailure_ReturnsEmptyWithoutRetrying()
    {
        var chatClient = new FakeChatClient(new HttpRequestException("OmniRoute unavailable"));
        var scorer = CreateScorer(chatClient);

        var results = await scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])]);

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
            scorer.ScoreAsync([MakeCandidate("Candidate", [1f, 0f, 0f])], cancellation.Token));

        Assert.Equal(0, chatClient.CallCount);
    }
}
