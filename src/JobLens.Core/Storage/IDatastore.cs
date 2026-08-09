using JobLens.Core.Parsing;

namespace JobLens.Core.Storage;

public record SimilarPosting(JobPosting Posting, double Similarity);

public interface IDatastore
{
    /// <summary>
    /// Creates the pgvector extension/table if missing, sized to the given embedding
    /// dimension. Idempotent - safe to call before every write or query.
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
}
