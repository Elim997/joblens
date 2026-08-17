using JobLens.Core.Storage;

namespace JobLens.Tests.Storage;

public class FakeTailoredDraftStore : ITailoredDraftStore
{
    private readonly List<TailoredDraft> _drafts = [];

    public int CreateOrGetCallCount { get; private set; }

    public IReadOnlyList<TailoredDraft> Drafts => _drafts;

    // Fires right after a draft is persisted/reused and returned, but before control returns to
    // the caller - lets a test simulate cancellation landing cleanly *between* two postings'
    // auto-tailor iterations (PipelineRunnerTests' cancellation test), rather than mid-call.
    public Action<TailoredDraft>? AfterCreateOrGet { get; set; }

    public void Seed(TailoredDraft draft) => _drafts.Add(draft);

    public Task<TailoredDraft> CreateOrGetAsync(
        NewTailoredDraft draft,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateOrGetCallCount++;

        var existing = _drafts.FirstOrDefault(d =>
            d.MessageId == draft.MessageId &&
            d.SelectedTemplate == draft.SelectedTemplate &&
            d.BaseResumeId == draft.BaseResumeId);
        if (existing is not null)
        {
            AfterCreateOrGet?.Invoke(existing);
            return Task.FromResult(existing);
        }

        var stored = new TailoredDraft(
            Guid.NewGuid().ToString(),
            draft.MessageId,
            draft.Score,
            draft.SelectedTemplate,
            draft.BaseResumeId,
            draft.BaseResumeName,
            draft.Summary,
            draft.Experience.ToArray(),
            draft.Skills.ToArray(),
            draft.RewriteRationale,
            TailoredDraftStatus.Draft,
            DateTimeOffset.UtcNow,
            null);
        _drafts.Add(stored);
        AfterCreateOrGet?.Invoke(stored);
        return Task.FromResult(stored);
    }

    public Task<TailoredDraft?> FindAsync(
        string messageId,
        string selectedTemplate,
        string baseResumeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_drafts.FirstOrDefault(d =>
            d.MessageId == messageId &&
            d.SelectedTemplate == selectedTemplate &&
            d.BaseResumeId == baseResumeId));
    }

    public Task<IReadOnlyList<TailoredDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TailoredDraft>>(
            _drafts.OrderByDescending(d => d.CreatedAt).ToArray());
    }

    public Task<TailoredDraft?> GetByIdAsync(string draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_drafts.FirstOrDefault(d => d.Id == draftId));
    }

    public Task<TailoredDraft?> MarkExportedAsync(string draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _drafts.FindIndex(d => d.Id == draftId);
        if (index < 0)
            return Task.FromResult<TailoredDraft?>(null);

        var updated = _drafts[index] with
        {
            Status = TailoredDraftStatus.ExportedToRezi,
            ExportedAt = DateTimeOffset.UtcNow,
        };
        _drafts[index] = updated;
        return Task.FromResult<TailoredDraft?>(updated);
    }
}
