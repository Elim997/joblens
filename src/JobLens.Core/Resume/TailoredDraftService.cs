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
    /// <summary>
    /// Resolves a persisted scoring-template name to its configured Rezi base resume without
    /// reading a posting, calling a model or Rezi, or touching the draft store. Returns false for
    /// missing templates and configuration drift so automatic runs can skip before attempting;
    /// CreateOrGetAsync continues to fail closed for the manual endpoint.
    /// </summary>
    public bool TryResolveBaseResume(
        string? selectedTemplate,
        out string baseResumeId,
        out string baseResumeName)
    {
        baseResumeId = string.Empty;
        baseResumeName = string.Empty;

        if (string.IsNullOrWhiteSpace(selectedTemplate))
            return false;

        var trimmed = selectedTemplate.Trim();
        var match = options.Value.BaseResumes.FirstOrDefault(b =>
            string.Equals(b.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

        if (match is null || string.IsNullOrWhiteSpace(match.Id))
            return false;

        baseResumeId = match.Id.Trim();
        baseResumeName = match.Name.Trim();
        return true;
    }

    /// <summary>Null means no stored posting for messageId - the caller turns that into a 404.</summary>
    public async Task<TailoredDraftResult?> CreateOrGetAsync(string messageId, CancellationToken cancellationToken = default)
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
            return new TailoredDraftResult(existing, WasCreated: false);

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
        // In that rare race the row already existed at the DB layer, but WasCreated is still
        // reported true here - acceptable imprecision for a metrics counter: TailorAsync
        // genuinely ran and FindAsync had reported "not found" moments earlier.
        var created = await draftStore.CreateOrGetAsync(newDraft, cancellationToken);
        return new TailoredDraftResult(created, WasCreated: true);
    }

    /// <summary>
    /// The 1:1 name convention between a scoring template and a configured Rezi base resume,
    /// resolved from config only - never from model output. Fails closed if the persisted
    /// template no longer matches any configured base, instead of silently remapping a historical
    /// selection to a different base.
    /// </summary>
    private (string Id, string Name) ResolveBaseResume(string selectedTemplate)
    {
        if (!TryResolveBaseResume(selectedTemplate, out var baseResumeId, out var baseResumeName))
            throw new ScoringTemplateResumeMappingException(selectedTemplate);

        return (baseResumeId, baseResumeName);
    }
}

/// <summary>
/// CreateOrGetAsync's return shape. WasCreated is false when an existing draft for the
/// (posting, template, base) tuple was found and reused with zero model calls; true when a
/// tailoring call actually ran and a new row was persisted. Lets callers (e.g. PipelineRunner's
/// automatic-tailoring loop) report created-vs-reused counts without duplicating this service's
/// internal template-resolution/lookup logic.
/// </summary>
public record TailoredDraftResult(TailoredDraft Draft, bool WasCreated);
