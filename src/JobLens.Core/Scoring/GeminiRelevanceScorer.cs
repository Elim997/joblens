using System.Text.Json;
using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Scoring;

public class GeminiRelevanceScorer(
    IChatClient chatClient,
    IOptions<JobLensOptions> options,
    ILogger<GeminiRelevanceScorer> logger) : IRelevanceScorer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        float[] profileEmbedding,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return [];

        var shortlist = candidates
            .OrderByDescending(c => VectorMath.CosineSimilarity(c.Embedding, profileEmbedding))
            .Take(options.Value.ScoringTopK)
            .Select(c => (c.Id, c.Posting))
            .ToList();

        var messages = BuildPrompt(options.Value.Profile, shortlist.Select(s => s.Posting).ToList());
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        // Structural failure (invalid JSON): retry the identical call once, then give
        // up on the whole batch rather than throw - a bad model response never crashes
        // the run, it just means nothing gets scored this time.
        List<ScoreResponseItem>? items = null;
        for (var attempt = 1; attempt <= 2 && items is null; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            items = TryParse(response.Text);
            if (items is null)
                logger.LogWarning("Scoring response was not valid JSON on attempt {Attempt}/2.", attempt);
        }

        if (items is null)
        {
            logger.LogWarning("Scoring gave up after 2 attempts; returning no matches for this batch of {Count}.", shortlist.Count);
            return [];
        }

        // Item-level failure (out-of-range score/index, duplicate index, empty
        // reasoning): skip only that item, keep the rest of a structurally valid batch.
        var results = new List<ScoredPosting>();
        var seenIndexes = new HashSet<int>();
        foreach (var item in items)
        {
            if (item.Index < 0 || item.Index >= shortlist.Count)
            {
                logger.LogWarning("Scoring response referenced out-of-range index {Index}; skipped.", item.Index);
                continue;
            }

            if (!seenIndexes.Add(item.Index))
            {
                logger.LogWarning("Scoring response had a duplicate index {Index}; skipped.", item.Index);
                continue;
            }

            if (item.Score < 0 || item.Score > 100)
            {
                logger.LogWarning("Scoring response had an out-of-range score {Score} for index {Index}; skipped.", item.Score, item.Index);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Reasoning))
            {
                logger.LogWarning("Scoring response had empty reasoning for index {Index}; skipped.", item.Index);
                continue;
            }

            results.Add(new ScoredPosting(shortlist[item.Index].Id, shortlist[item.Index].Posting, item.Score, item.Reasoning));
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static IReadOnlyList<ChatMessage> BuildPrompt(string profile, IReadOnlyList<JobPosting> shortlist)
    {
        const string systemPrompt = """
            You score job postings for fit against a candidate profile. For each
            posting, return a score from 0 to 100 (0 = no fit, 100 = perfect fit) and a
            1-2 sentence reasoning. Respond with ONLY a JSON array, no markdown fences,
            no extra text: [{"index": 0, "score": 85, "reasoning": "..."}, ...]
            Exactly one entry per posting, using the given index.
            """;

        var postingsText = string.Join("\n\n", shortlist.Select((p, i) =>
            $"[{i}] {p.Title} at {p.Company} ({p.Location}, {p.Category})\n{p.Description}"));

        var userPrompt = $"Candidate profile:\n{profile}\n\nPostings:\n{postingsText}";

        return
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt),
        ];
    }

    private static List<ScoreResponseItem>? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = StripMarkdownFences(text.Trim());

        try
        {
            return JsonSerializer.Deserialize<List<ScoreResponseItem>>(trimmed, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? text[(firstNewline + 1)..lastFence].Trim()
            : text;
    }

    private record ScoreResponseItem(int Index, int Score, string Reasoning);
}
