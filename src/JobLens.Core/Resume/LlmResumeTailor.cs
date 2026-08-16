using System.Text.Json;
using System.Text.Json.Nodes;
using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Llm;
using JobLens.Core.Parsing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Resume;

/// <summary>
/// IResumeTailor over a role-specific chat client + IResumeClient. Bases are read-only: this class only ever calls
/// IResumeClient.ReadResumeAsync, never WriteResumeAsync - see Phase 3 for the write step.
///
/// Model output never crosses this boundary trusted: the rewrite is parsed into UntrustedRewrite
/// (every field nullable), and the only way to turn that into the ValidatedTailoredResume this
/// class returns is ResumeTailoringValidator - the sole place that decides a model response is
/// usable.
/// </summary>
public class LlmResumeTailor(
    IResumeClient resumeClient,
    ITailoringChatClient tailoringChatClient,
    IOptions<ReziOptions> options,
    ILogger<LlmResumeTailor> logger) : IResumeTailor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private IChatClient ChatClient => tailoringChatClient.ChatClient;

    private readonly SemaphoreSlim _baseSummariesLock = new(1, 1);
    private IReadOnlyList<BaseResumeSummary>? _baseSummaries;

    public async Task<ValidatedTailoredResume> TailorAsync(JobPosting posting, CancellationToken cancellationToken = default)
    {
        var bases = await GetBaseSummariesAsync(cancellationToken);
        var selection = await SelectBaseAsync(posting, bases, cancellationToken);

        var baseResume = await resumeClient.ReadResumeAsync(selection.BaseResumeId, cancellationToken)
            ?? throw new InvalidOperationException($"Base resume '{selection.BaseResumeName}' ({selection.BaseResumeId}) not found.");

        var snapshot = new BaseResumeSnapshot(
            selection,
            baseResume["data"]?["summary"]?["summary"]?.GetValue<string>() ?? "",
            ExtractItems(baseResume, "experience", "description"),
            ExtractItems(baseResume, "skills", "skill"));

        var rewrite = await RewriteAsync(posting, snapshot, cancellationToken);

        // The only place a ValidatedTailoredResume can be produced: Validate() throws
        // ResumeTailoringValidationException rather than let a malformed or dishonest rewrite
        // cross into a trusted result.
        return ResumeTailoringValidator.Validate(snapshot, rewrite);
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

        UntrustedSelection? parsed = null;
        for (var attempt = 1; attempt <= 2 && parsed is null; attempt++)
        {
            var response = await GetChatResponseAsync(messages, chatOptions, cancellationToken);
            var candidate = TryParse<UntrustedSelection>(response.Text);
            if (candidate?.Index is null ||
                candidate.Index < 0 ||
                candidate.Index >= bases.Count ||
                string.IsNullOrWhiteSpace(candidate.Rationale))
            {
                logger.LogWarning("Base selection response invalid on attempt {Attempt}/2.", attempt);
                continue;
            }

            parsed = candidate;
        }

        if (parsed?.Index is not int selectedIndex || string.IsNullOrWhiteSpace(parsed.Rationale))
            throw new TailoringOutputInvalidException("Tailoring model did not return a usable base selection after 2 attempts.");

        var chosen = bases[selectedIndex];
        return new BaseSelection(chosen.Id, chosen.Name, parsed.Rationale.Trim());
    }

    private async Task<UntrustedRewrite> RewriteAsync(
        JobPosting posting, BaseResumeSnapshot snapshot, CancellationToken cancellationToken)
    {
        var experienceText = string.Join("\n", snapshot.Experience.Select(i => $"[{i.ItemId}] {i.Text}"));
        var skillsText = string.Join("\n", snapshot.Skills.Select(i => $"[{i.ItemId}] {i.Text}"));

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

                Candidate's current resume (base: {snapshot.BaseSelection.BaseResumeName}):
                Summary: {snapshot.Summary}

                Experience:
                {experienceText}

                Skills:
                {skillsText}
                """),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        UntrustedRewrite? parsed = null;
        for (var attempt = 1; attempt <= 2 && parsed is null; attempt++)
        {
            var response = await GetChatResponseAsync(messages, chatOptions, cancellationToken);
            parsed = TryParse<UntrustedRewrite>(response.Text);
            if (parsed is null)
                logger.LogWarning("Resume rewrite response was not valid JSON on attempt {Attempt}/2.", attempt);
        }

        if (parsed is null)
            throw new TailoringOutputInvalidException("Tailoring model did not return a usable resume rewrite after 2 attempts.");

        return parsed;
    }

    /// <summary>
    /// Wraps every tailoring chat call so a transport/model-level failure (auth, network,
    /// non-2xx, timeout) surfaces as TailoringModelUnavailableException instead of a raw SDK
    /// exception whose message may embed upstream response text. Caller cancellation is rethrown
    /// as-is, never wrapped.
    /// </summary>
    private async Task<ChatResponse> GetChatResponseAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions chatOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await ChatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Tailoring chat client request failed with {ExceptionType}.", ex.GetType().Name);
            throw new TailoringModelUnavailableException($"Tailoring chat client request failed ({ex.GetType().Name}).");
        }
    }

    private static IReadOnlyList<ResumeItemSnapshot> ExtractItems(JsonNode resume, string section, string textField)
    {
        if (resume["data"]?[section] is not JsonObject items)
            return [];

        return items
            .Where(kvp => kvp.Value is not null)
            .Select(kvp => new ResumeItemSnapshot(kvp.Key, kvp.Value?[textField]?.GetValue<string>() ?? ""))
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
}
