using JobLens.Core.Configuration;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Resume;

/// <summary>
/// The one application path that writes a persisted TailoredDraft to Rezi's configured mutable
/// "for edit" workspace. It never calls the tailoring model and never mutates the draft's stored
/// content; only its export status/timestamp change after a successful Rezi write.
/// </summary>
public class TailoredDraftExporter(
    ITailoredDraftStore draftStore,
    IResumeClient resumeClient,
    IOptions<ReziOptions> options)
{
    /// <summary>Null means no stored draft for draftId - the caller turns that into a 404.</summary>
    public async Task<TailoredDraft?> ExportAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var draft = await draftStore.GetByIdAsync(draftId, cancellationToken);
        if (draft is null)
            return null;

        var forEditId = ValidateWriteDestination();
        ValidateStoredContent(draft);

        var payload = ResumeWritePayloadBuilder.Build(draft.Summary, draft.Experience, draft.Skills);
        await resumeClient.WriteResumeAsync(forEditId, payload, cancellationToken);

        // The exported state is recorded only after Rezi accepted the write. If the write throws,
        // this line is never reached and the draft remains Draft (or keeps its prior export state).
        return await draftStore.MarkExportedAsync(draft.Id, cancellationToken)
            ?? throw new InvalidOperationException("The tailored draft disappeared after it was exported.");
    }

    private string ValidateWriteDestination()
    {
        var forEditId = options.Value.ForEditResumeId?.Trim() ?? "";
        if (forEditId.Length == 0)
            throw new ResumeWriteConfigurationException("Rezi:ForEditResumeId is not configured; refusing to write.");

        var matchesBaseId = options.Value.BaseResumes.Any(b =>
            string.Equals(b.Id?.Trim(), forEditId, StringComparison.OrdinalIgnoreCase));
        if (matchesBaseId)
            throw new ResumeWriteConfigurationException(
                "Rezi:ForEditResumeId matches a configured base resume id. Refusing to write - " +
                "a base must never be writable via /tailor.");

        return forEditId;
    }

    /// <summary>
    /// Re-validates the content loaded from PostgreSQL immediately before export. The persisted
    /// representation does not retain ValidatedTailoredResume's original-id sets, so it cannot
    /// repeat the original/base completeness comparison; it can and does reject blank content and
    /// duplicate ids before any Rezi write. The stricter original-id integrity check already ran
    /// before this content was first persisted.
    /// </summary>
    private static void ValidateStoredContent(TailoredDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Summary))
            throw new ResumeTailoringValidationException("Stored tailored draft has a blank summary.");

        if (string.IsNullOrWhiteSpace(draft.RewriteRationale))
            throw new ResumeTailoringValidationException("Stored tailored draft has a blank rewrite rationale.");

        ValidateSection(
            draft.Experience,
            item => item.ItemId,
            item => item.Description,
            "experience");
        ValidateSection(
            draft.Skills,
            item => item.ItemId,
            item => item.Skill,
            "skills");
    }

    private static void ValidateSection<T>(
        IReadOnlyList<T> items,
        Func<T, string> idSelector,
        Func<T, string> textSelector,
        string sectionName)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemId = idSelector(item);
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ResumeTailoringValidationException($"Stored tailored draft {sectionName} item has a blank id.");

            if (!seenIds.Add(itemId))
                throw new ResumeTailoringValidationException($"Stored tailored draft {sectionName} has a duplicate id.");

            if (string.IsNullOrWhiteSpace(textSelector(item)))
                throw new ResumeTailoringValidationException($"Stored tailored draft {sectionName} item has blank text.");
        }
    }
}
