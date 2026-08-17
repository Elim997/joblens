using JobLens.Core.Parsing;
using JobLens.Core.Storage;

namespace JobLens.Tests.Pipeline;

// In-memory IDatastore for PipelineRunner tests, also reused by TailoredDraftService/endpoint
// tests. GetUnscoredPostingsAsync/MarkScoredAsync (stateful across calls, so the run-twice
// dedupe test can prove a second RunAsync sees nothing left) and GetScoredPostingByMessageIdAsync
// carry real behavior; the rest aren't used by either caller and throw if they ever are.
public class FakeDatastore : IDatastore
{
    private record Row(
        string MessageId,
        JobPosting Posting,
        float[] Embedding,
        DateTimeOffset? ScoredAt,
        int? Score,
        string? Reasoning,
        string? TemplateName);

    private readonly List<Row> _rows = [];

    public void Seed(string messageId, JobPosting posting, float[] embedding) =>
        _rows.Add(new Row(messageId, posting, embedding, null, null, null, null));

    public void SeedScored(
        string messageId,
        JobPosting posting,
        float[] embedding,
        int? score,
        string? reasoning,
        string? templateName) =>
        _rows.Add(new Row(
            messageId,
            posting,
            embedding,
            DateTimeOffset.UtcNow,
            score,
            reasoning,
            templateName));

    public IReadOnlyList<string> ScoredMessageIds => _rows.Where(r => r.ScoredAt is not null).Select(r => r.MessageId).ToList();

    public (int Score, string Reasoning, string TemplateName) GetPersistedScore(string messageId)
    {
        var row = _rows.Single(r => r.MessageId == messageId);
        return (row.Score!.Value, row.Reasoning!, row.TemplateName!);
    }

    public Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<ScoredPostingSnapshot?> GetScoredPostingByMessageIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var row = _rows.FirstOrDefault(r => r.MessageId == messageId);
        return Task.FromResult(row is null ? null : new ScoredPostingSnapshot(row.Posting, row.Score, row.Reasoning, row.TemplateName));
    }

    public Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UnscoredPosting>>(
            _rows.Where(r => r.ScoredAt is null)
                 .Select(r => new UnscoredPosting(r.MessageId, r.Posting, r.Embedding))
                 .ToList());

    public Task MarkScoredAsync(IReadOnlyList<ScoredMark> scored, CancellationToken cancellationToken = default)
    {
        var byId = scored.ToDictionary(s => s.MessageId);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (byId.TryGetValue(_rows[i].MessageId, out var mark))
                _rows[i] = _rows[i] with
                {
                    ScoredAt = DateTimeOffset.UtcNow,
                    Score = mark.Score,
                    Reasoning = mark.Reasoning,
                    TemplateName = mark.TemplateName,
                };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(int matchThreshold, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");
}
