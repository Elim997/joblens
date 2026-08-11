using JobLens.Core.Parsing;
using JobLens.Core.Storage;

namespace JobLens.Tests.Pipeline;

// In-memory IDatastore for PipelineRunner tests. Only GetUnscoredPostingsAsync and
// MarkScoredAsync carry real behavior (stateful across calls, so the run-twice dedupe
// test can prove a second RunAsync sees nothing left); the rest aren't used by
// PipelineRunner and throw if they ever are.
public class FakeDatastore : IDatastore
{
    private record Row(string MessageId, JobPosting Posting, float[] Embedding, DateTimeOffset? ScoredAt, int? Score, string? Reasoning);

    private readonly List<Row> _rows = [];

    public void Seed(string messageId, JobPosting posting, float[] embedding) =>
        _rows.Add(new Row(messageId, posting, embedding, null, null, null));

    public IReadOnlyList<string> ScoredMessageIds => _rows.Where(r => r.ScoredAt is not null).Select(r => r.MessageId).ToList();

    public (int Score, string Reasoning) GetPersistedScoreAndReasoning(string messageId)
    {
        var row = _rows.Single(r => r.MessageId == messageId);
        return (row.Score!.Value, row.Reasoning!);
    }

    public Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<JobPosting?> GetPostingByMessageIdAsync(string messageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.FirstOrDefault(r => r.MessageId == messageId)?.Posting);

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
                _rows[i] = _rows[i] with { ScoredAt = DateTimeOffset.UtcNow, Score = mark.Score, Reasoning = mark.Reasoning };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(int matchThreshold, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");
}
