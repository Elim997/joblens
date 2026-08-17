using JobLens.Core.Configuration;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Resume;

/// <summary>
/// Backs POST /tailor: turns a scored posting into a persisted TailoredDraft. Never writes to
/// Rezi - it only ever reads exactly one configured base resume (via IResumeTailor) and persists
/// the validated result. Writing to Rezi is TailoredDraftExporter's job exclusively; this class
/// has no dependency on it.
///
/// The scoring-selected template is the only template-selection source: no model call ever
/// chooses a base resume here, and no other Rezi base is ever read to compare among options.
/// </summary>
public class TailoredDraftService(
    IDatastore datastore,
    IResumeTailor tailor,
    ITailoredDraftStore draftStore,
    IOptions<ReziOptions> options)
{
    /// <summary>Null means no stored posting for messageId - the caller turns that into a 404.</summary>
    public async Task<TailoredDraft?> CreateOrGetAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var snapshot = await datastore.GetScoredPostingByMessageIdAsync(messageId, cancellationToken);
        if (snapshot is null)
            return null;

        // A legacy row scored before multi-template routing, or a posting never scored at all,
        // has no trusted template to draft from - fail closed rather than guess a base.
        if (string.IsNullOrWhiteSpace(snapshot.SelectedTemplate) || snapshot.Score is not int score)
            throw new PostingNotScoredException(messageId);

        var selectedTemplate = snapshot.SelectedTemplate;
        var (baseResumeId, baseResumeName) = ResolveBaseResume(selectedTemplate);

        // Idempotency boundary: a prior draft for this exact (posting, template, base) tuple is
        // returned as-is, with zero model calls - a double-click or repeated request never spends
        // another tailoring call or creates a duplicate row.
        var existing = await draftStore.FindAsync(messageId, selectedTemplate, baseResumeId, cancellationToken);
        if (existing is not null)
            return existing;

        var tailored = await tailor.TailorAsync(snapshot.Posting, baseResumeId, baseResumeName, cancellationToken);

        // Defense in depth: re-check the validator-produced result immediately before it is
        // persisted, even though TailorAsync can only ever return an already-validated result.
        ResumeTailoringValidator.ValidateComplete(tailored);

        var newDraft = new NewTailoredDraft(
            messageId,
            score,
            selectedTemplate,
            baseResumeId,
            baseResumeName,
            tailored.Summary,
            tailored.Experience,
            tailored.Skills,
            tailored.RewriteRationale);

        // CreateOrGetAsync is itself idempotent at the database level (see
        // PgvectorTailoredDraftStore), so a concurrent duplicate request that raced past the
        // FindAsync check above still converges on a single row here, without a second write.
        return await draftStore.CreateOrGetAsync(newDraft, cancellationToken);
    }

    /// <summary>
    /// The 1:1 name convention between a scoring template and a configured Rezi base resume,
    /// resolved from config only - never from model output. Fails closed if the persisted
    /// template no longer matches any configured base, instead of silently remapping a historical
    /// selection to a different base.
    /// </summary>
    private (string Id, string Name) ResolveBaseResume(string selectedTemplate)
    {
        var trimmed = selectedTemplate.Trim();
        var match = options.Value.BaseResumes.FirstOrDefault(b =>
            string.Equals(b.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

        if (match is null || string.IsNullOrWhiteSpace(match.Id))
            throw new ScoringTemplateResumeMappingException(selectedTemplate);

        return (match.Id.Trim(), match.Name.Trim());
    }
}
