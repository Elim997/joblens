using System.Text.Json;
using JobLens.Core.Configuration;
using JobLens.Core.Llm;
using JobLens.Core.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Scoring;

public class LlmRelevanceScorer(
    IScoringChatClient scoringChatClient,
    ITemplateCatalog templateCatalog,
    IOptions<JobLensOptions> options,
    ILogger<LlmRelevanceScorer> logger) : IRelevanceScorer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private IChatClient ChatClient => scoringChatClient.ChatClient;

    public async Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return [];

        var templates = await templateCatalog.GetTemplatesAsync(cancellationToken);
        if (templates.Count == 0)
        {
            logger.LogWarning("No scoring templates configured; returning no matches for this batch of {Count}.", candidates.Count);
            return [];
        }

        // Route each candidate to whichever template's profile it's closest to - local
        // cosine similarity only, no model call, no Rezi read. The single global
        // ScoringTopK cutoff is then applied once across ALL candidates by that
        // best-template similarity, exactly as it was previously applied against one
        // profile embedding, so PipelineRunner's batch-size semantics are unchanged.
        var routed = candidates
            .Select(c =>
            {
                var (template, similarity) = templates
                    .Select(t => (Template: t, Similarity: VectorMath.CosineSimilarity(c.Embedding, t.Embedding)))
                    .MaxBy(x => x.Similarity);
                return (c.Id, c.Posting, Template: template, Similarity: similarity);
            })
            .OrderByDescending(r => r.Similarity)
            .Take(options.Value.ScoringTopK)
            .ToList();

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        // Grouped by the locally-selected template and scored with the existing
        // OmniRoute prompt, reused verbatim per group - the model sees only that
        // group's shortlist and its template's profile text, and is never asked to
        // choose or return a template/resume id itself (LlmResumeTailor's existing
        // "trusted code resolves the id" precedent, applied here without a model call
        // at all). Each group fails soft independently: one group's malformed response
        // or transport failure doesn't lose another group's results.
        var results = new List<ScoredPosting>();
        foreach (var group in routed.GroupBy(r => r.Template))
        {
            var shortlist = group.Select(g => (g.Id, g.Posting)).ToList();
            var groupResults = await ScoreGroupAsync(group.Key.Name, group.Key.Profile, shortlist, chatOptions, cancellationToken);
            results.AddRange(groupResults);
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private async Task<List<ScoredPosting>> ScoreGroupAsync(
        string templateName,
        string profile,
        IReadOnlyList<(string Id, JobPosting Posting)> shortlist,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var messages = BuildPrompt(profile, shortlist.Select(s => s.Posting).ToList());

        // Structural failure (invalid JSON): retry the identical call once, then give
        // up on this template's group rather than throw - a bad model response never
        // crashes the run, it just means nothing gets scored for this group this time.
        List<ScoreResponseItem>? items = null;
        try
        {
            for (var attempt = 1; attempt <= 2 && items is null; attempt++)
            {
                var response = await ChatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
                items = TryParse(response.Text);
                if (items is null)
                    logger.LogWarning("Scoring response for template '{Template}' was not valid JSON on attempt {Attempt}/2.", templateName, attempt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Scoring model request failed with {ExceptionType} for template '{Template}'; returning no matches for this group of {Count}.",
                ex.GetType().Name,
                templateName,
                shortlist.Count);
            return [];
        }

        if (items is null)
        {
            logger.LogWarning(
                "Scoring gave up after 2 attempts for template '{Template}'; returning no matches for this group of {Count}.",
                templateName,
                shortlist.Count);
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

            results.Add(new ScoredPosting(shortlist[item.Index].Id, shortlist[item.Index].Posting, item.Score, item.Reasoning, templateName));
        }

        return results;
    }

    private static IReadOnlyList<ChatMessage> BuildPrompt(string profile, IReadOnlyList<JobPosting> shortlist)
    {
        const string systemPrompt = """
            You score job postings for fit against a candidate profile. For each
            posting, return a score from 0 to 100 (0 = no fit, 100 = perfect fit) and a
            1-2 sentence reasoning.

            Treat stated experience requirements as soft signals, not hard filters. A
            junior candidate open to stretch roles may still be a strong match for a
            posting asking for 1-3 years if the tech stack and domain fit. Do not
            heavily penalize a good stack/domain match solely because the posting lists
            a couple years of experience. Reserve low scores for genuine mismatches:
            wrong domain (hardware, biomedical, research), wrong discipline, or
            senior/lead roles.

            Respond with ONLY a JSON array, no markdown fences,
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
