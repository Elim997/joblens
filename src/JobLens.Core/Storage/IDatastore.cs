using JobLens.Core.Parsing;

namespace JobLens.Core.Storage;

public record SimilarPosting(JobPosting Posting, double Similarity);

public record UnscoredPosting(string MessageId, JobPosting Posting, float[] Embedding);

public interface IDatastore
{
    /// <summary>
    /// Creates the pgvector extension/table if missing, sized to the given embedding
    /// dimension, and adds the scored_at column if missing. Idempotent - safe to call
    /// before every write or query.
    /// </summary>
    Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Message ids already stored, so a re-ingest can skip re-embedding them - embedding
    /// is the quota-limited step, not this lookup. Returns empty if the table doesn't
    /// exist yet (nothing has ever been embedded, so nothing needs skipping).
    /// </summary>
    Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every posting with scored_at IS NULL, with its stored embedding - the candidate
    /// pool for a /run pass. This is the whole archive, not just a fresh ingest batch.
    /// </summary>
    Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets scored_at = now() for the given ids so they are never re-scored or
    /// re-notified by a later run, regardless of whether they matched.
    /// </summary>
    Task MarkScoredAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default);
}
