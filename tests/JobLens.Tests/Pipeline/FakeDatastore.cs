using JobLens.Core.Parsing;
using JobLens.Core.Storage;

namespace JobLens.Tests.Pipeline;

// In-memory IDatastore for PipelineRunner tests. Only GetUnscoredPostingsAsync and
// MarkScoredAsync carry real behavior (stateful across calls, so the run-twice dedupe
// test can prove a second RunAsync sees nothing left); the rest aren't used by
// PipelineRunner and throw if they ever are.
public class FakeDatastore : IDatastore
{
    private record Row(string MessageId, JobPosting Posting, float[] Embedding, DateTimeOffset? ScoredAt);

    private readonly List<Row> _rows = [];

    public void Seed(string messageId, JobPosting posting, float[] embedding) =>
        _rows.Add(new Row(messageId, posting, embedding, null));

    public IReadOnlyList<string> ScoredMessageIds => _rows.Where(r => r.ScoredAt is not null).Select(r => r.MessageId).ToList();

    public Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by PipelineRunner.");

    public Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UnscoredPosting>>(
            _rows.Where(r => r.ScoredAt is null)
                 .Select(r => new UnscoredPosting(r.MessageId, r.Posting, r.Embedding))
                 .ToList());

    public Task MarkScoredAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (messageIds.Contains(_rows[i].MessageId))
                _rows[i] = _rows[i] with { ScoredAt = DateTimeOffset.UtcNow };
        }

        return Task.CompletedTask;
    }
}
