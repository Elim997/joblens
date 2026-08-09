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

    Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);
}
