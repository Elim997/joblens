using System.Text.Json;
using System.Text.Json.Nodes;
using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Resume;

/// <summary>
/// IResumeTailor over Gemini + IResumeClient. Bases are read-only: this class only ever calls
/// IResumeClient.ReadResumeAsync, never WriteResumeAsync - see Phase 3 for the write step.
/// </summary>
public class GeminiResumeTailor(
    IResumeClient resumeClient,
    IChatClient chatClient,
    IOptions<ReziOptions> options,
    ILogger<GeminiResumeTailor> logger) : IResumeTailor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _baseSummariesLock = new(1, 1);
    private IReadOnlyList<BaseResumeSummary>? _baseSummaries;

    public async Task<TailoredResume> TailorAsync(JobPosting posting, CancellationToken cancellationToken = default)
    {
        var bases = await GetBaseSummariesAsync(cancellationToken);
        var selection = await SelectBaseAsync(posting, bases, cancellationToken);

        var baseResume = await resumeClient.ReadResumeAsync(selection.BaseResumeId, cancellationToken)
            ?? throw new InvalidOperationException($"Base resume '{selection.BaseResumeName}' ({selection.BaseResumeId}) not found.");

        var originalSummary = baseResume["data"]?["summary"]?["summary"]?.GetValue<string>() ?? "";
        var originalExperience = ExtractItems(baseResume, "experience", "description");
        var originalSkills = ExtractItems(baseResume, "skills", "skill");

        var rewrite = await RewriteAsync(posting, selection, originalSummary, originalExperience, originalSkills, cancellationToken);

        return new TailoredResume(
            selection,
            string.IsNullOrWhiteSpace(rewrite.Summary) ? originalSummary : rewrite.Summary,
            MergeItems(originalExperience, rewrite.Experience, "experience").Select(i => new TailoredExperienceItem(i.ItemId, i.Text)).ToList(),
            MergeItems(originalSkills, rewrite.Skills, "skills").Select(i => new TailoredSkillItem(i.ItemId, i.Text)).ToList(),
            rewrite.Rationale);
    }

    private async Task<IReadOnlyList<BaseResumeSummary>> GetBaseSummariesAsync(CancellationToken cancellationToken)
    {
        if (_baseSummaries is not null)
            return _baseSummaries;

        await _baseSummariesLock.WaitAsync(cancellationToken);
        try
        {
            if (_baseSummaries is not null)
                return _baseSummaries;

            if (options.Value.BaseResumes.Count == 0)
                throw new InvalidOperationException("No base resumes configured (Rezi:BaseResumes).");

            var summaries = new List<BaseResumeSummary>();
            foreach (var b in options.Value.BaseResumes)
            {
                var resume = await resumeClient.ReadResumeAsync(b.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Base resume '{b.Name}' ({b.Id}) not found.");
                var summary = resume["data"]?["summary"]?["summary"]?.GetValue<string>() ?? "";
                summaries.Add(new BaseResumeSummary(b.Id, b.Name, summary));
            }

            _baseSummaries = summaries;
            return _baseSummaries;
        }
        finally
        {
            _baseSummariesLock.Release();
        }
    }

    private async Task<BaseSelection> SelectBaseAsync(
        JobPosting posting, IReadOnlyList<BaseResumeSummary> bases, CancellationToken cancellationToken)
    {
        var basesText = string.Join("\n\n", bases.Select((b, i) => $"[{i}] {b.Name}: {b.Summary}"));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, """
                You pick which of a candidate's resume bases best fits a job posting.
                Respond with ONLY JSON, no markdown fences, no extra text:
                {"index": 0, "rationale": "one short sentence"}
                """),
            new(ChatRole.User, $"Job posting:\n{JobPostingTextNormalizer.ToEmbeddingText(posting)}\n\nBases:\n{basesText}"),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        SelectionResponseDto? parsed = null;
        for (var attempt = 1; attempt <= 2 && parsed is null; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            parsed = TryParse<SelectionResponseDto>(response.Text);
            if (parsed is null || parsed.Index < 0 || parsed.Index >= bases.Count)
            {
                logger.LogWarning("Base selection response invalid on attempt {Attempt}/2: {Text}", attempt, response.Text);
                parsed = null;
            }
        }

        if (parsed is null)
            throw new InvalidOperationException("Gemini did not return a usable base selection after 2 attempts.");

        var chosen = bases[parsed.Index];
        return new BaseSelection(chosen.Id, chosen.Name, parsed.Rationale ?? "");
    }

    private async Task<RewriteResponseDto> RewriteAsync(
        JobPosting posting,
        BaseSelection selection,
        string originalSummary,
        IReadOnlyList<Item> originalExperience,
        IReadOnlyList<Item> originalSkills,
        CancellationToken cancellationToken)
    {
        var experienceText = string.Join("\n", originalExperience.Select(i => $"[{i.ItemId}] {i.Text}"));
        var skillsText = string.Join("\n", originalSkills.Select(i => $"[{i.ItemId}] {i.Text}"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, """
                You rewrite a candidate's resume content to fit a specific job posting.

                HONESTY RULE - this is a hard constraint, not a suggestion:
                - Never invent a skill or experience the candidate doesn't have.
                - Where the candidate is light on something the posting wants (e.g. it
                  asks for "1+ years" of something they've only touched briefly), you may
                  emphasize the experience they do have and stay vague about duration or
                  depth - do not state a number or level of experience that isn't true.
                - If the posting needs something the candidate genuinely lacks, leave it
                  out rather than fake it. Omission is always safer than fabrication.

                Rewrite the summary and, for each existing experience/skill item id given,
                its text - to emphasize genuine fit with the posting. Reword and reorder
                within what's already true; do not add facts. You may omit an item id from
                your response if it needs no change (the original text is kept).

                Respond with ONLY JSON, no markdown fences, no extra text:
                {
                  "summary": "...",
                  "experience": [{"itemId": "...", "description": "..."}],
                  "skills": [{"itemId": "...", "skill": "..."}],
                  "rationale": "1-2 sentences on what you emphasized and why"
                }
                Use only the item ids given below - never invent a new one.
                """),
            new(ChatRole.User, $"""
                Job posting:
                {JobPostingTextNormalizer.ToEmbeddingText(posting)}

                Candidate's current resume (base: {selection.BaseResumeName}):
                Summary: {originalSummary}

                Experience:
                {experienceText}

                Skills:
                {skillsText}
                """),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        RewriteResponseDto? parsed = null;
        for (var attempt = 1; attempt <= 2 && parsed is null; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            parsed = TryParse<RewriteResponseDto>(response.Text);
            if (parsed is null)
                logger.LogWarning("Resume rewrite response was not valid JSON on attempt {Attempt}/2.", attempt);
        }

        if (parsed is null)
            throw new InvalidOperationException("Gemini did not return a usable resume rewrite after 2 attempts.");

        return parsed;
    }

    private record Item(string ItemId, string Text);

    private static IReadOnlyList<Item> ExtractItems(JsonNode resume, string section, string textField)
    {
        if (resume["data"]?[section] is not JsonObject items)
            return [];

        return items
            .Where(kvp => kvp.Value is not null)
            .Select(kvp => new Item(kvp.Key, kvp.Value![textField]?.GetValue<string>() ?? ""))
            .ToList();
    }

    /// <summary>
    /// Keeps the base's exact set of item ids (never adds or drops one - that's what "keeps
    /// structure" means here). An id the model rewrote gets the new text; anything it left out,
    /// or any id it invented that isn't actually in the base, is handled defensively: missing ->
    /// original text kept, invented -> dropped with a warning.
    /// </summary>
    private IReadOnlyList<Item> MergeItems(
        IReadOnlyList<Item> original, IReadOnlyList<RewriteItemDto> rewritten, string section)
    {
        var knownIds = original.Select(i => i.ItemId).ToHashSet();
        var rewrittenById = new Dictionary<string, string>();
        foreach (var item in rewritten)
        {
            if (!knownIds.Contains(item.ItemId))
            {
                logger.LogWarning("Rewrite response invented a {Section} item id {ItemId}; ignored.", section, item.ItemId);
                continue;
            }

            rewrittenById[item.ItemId] = item.Text;
        }

        return original
            .Select(o => new Item(o.ItemId, rewrittenById.GetValueOrDefault(o.ItemId, o.Text)))
            .ToList();
    }

    private static T? TryParse<T>(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return default;

        var trimmed = StripMarkdownFences(text.Trim());
        try
        {
            return JsonSerializer.Deserialize<T>(trimmed, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
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

    private record SelectionResponseDto(int Index, string? Rationale);

    private record RewriteResponseDto(
        string Summary, List<RewriteItemDto> Experience, List<RewriteItemDto> Skills, string Rationale);

    private record RewriteItemDto(string ItemId, string? Description, string? Skill)
    {
        public string Text => Description ?? Skill ?? "";
    }
}
