using JobLens.Core.Resume;

namespace JobLens.Core.Storage;

public enum TailoredDraftStatus
{
    Draft,
    ExportedToRezi,
}

/// <summary>
/// Input to ITailoredDraftStore.CreateOrGetAsync. Score/SelectedTemplate are a snapshot of the
/// posting's persisted scoring metadata at draft-creation time - see JobLens.Core/Resume/
/// TailoredDraftService.cs, the only caller. BaseResumeId/BaseResumeName come from the
/// config-resolved Rezi base, never from model output.
/// </summary>
public record NewTailoredDraft(
    string MessageId,
    int Score,
    string SelectedTemplate,
    string BaseResumeId,
    string BaseResumeName,
    string Summary,
    IReadOnlyList<TailoredExperienceItem> Experience,
    IReadOnlyList<TailoredSkillItem> Skills,
    string RewriteRationale);

/// <summary>
/// A persisted, validated tailoring result. Once created, its content is never mutated -
/// exporting to Rezi only ever updates Status/ExportedAt (see ITailoredDraftStore.
/// MarkExportedAsync); the tailored content itself is immutable history.
/// </summary>
public record TailoredDraft(
    string Id,
    string MessageId,
    int Score,
    string SelectedTemplate,
    string BaseResumeId,
    string BaseResumeName,
    string Summary,
    IReadOnlyList<TailoredExperienceItem> Experience,
    IReadOnlyList<TailoredSkillItem> Skills,
    string RewriteRationale,
    TailoredDraftStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExportedAt);

/// <summary>
/// Persistence for tailored drafts. JobLens is the source of truth for tailored content - Rezi's
/// "for edit" resume is only ever a mutable export target (see TailoredDraftExporter), never read
/// back as truth. CreateOrGetAsync is the idempotency boundary: a (messageId, selectedTemplate,
/// baseResumeId) tuple maps to at most one row, ever, so a double-click or repeated
/// POST /tailor request reuses the existing draft rather than calling the tailoring model again.
/// </summary>
public interface ITailoredDraftStore
{
    /// <summary>
    /// Inserts a new draft, or - if a row already exists for this exact (MessageId,
    /// SelectedTemplate, BaseResumeId) tuple - returns that existing row unchanged. Race-safe at
    /// the database level (single statement, ON CONFLICT DO NOTHING + fallback SELECT), so two
    /// concurrent callers for the same tuple can never create two rows.
    /// </summary>
    Task<TailoredDraft> CreateOrGetAsync(NewTailoredDraft draft, CancellationToken cancellationToken = default);

    Task<TailoredDraft?> FindAsync(string messageId, string selectedTemplate, string baseResumeId, CancellationToken cancellationToken = default);

    /// <summary>Newest first (CreatedAt descending).</summary>
    Task<IReadOnlyList<TailoredDraft>> ListAsync(CancellationToken cancellationToken = default);

    Task<TailoredDraft?> GetByIdAsync(string draftId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets Status to ExportedToRezi and ExportedAt to now(), server-side, so repeated exports of
    /// the same draft just refresh the timestamp rather than corrupting anything. Returns null if
    /// the draft id doesn't exist.
    /// </summary>
    Task<TailoredDraft?> MarkExportedAsync(string draftId, CancellationToken cancellationToken = default);
}
