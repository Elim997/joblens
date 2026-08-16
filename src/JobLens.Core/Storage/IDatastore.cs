using JobLens.Core.Parsing;

namespace JobLens.Core.Storage;

public record SimilarPosting(JobPosting Posting, double Similarity);

public record UnscoredPosting(string MessageId, JobPosting Posting, float[] Embedding);

public record ScoredMark(string MessageId, int Score, string Reasoning);

public record StoredMatch(string MessageId, JobPosting Posting, int Score, string Reasoning);

public interface IDatastore
{
    /// <summary>
    /// Creates the pgvector extension/table if missing, sized to the given embedding
    /// dimension, and adds the scored_at/score/reasoning columns if missing. Idempotent -
    /// safe to call before every write or query.
    /// </summary>
    Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Message ids already stored, so a re-ingest can skip re-embedding them - embedding
    /// is the quota-limited step, not this lookup. Returns empty if the table doesn't
    /// exist yet (nothing has ever been embedded, so nothing needs skipping).
    /// </summary>
    Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// A single stored posting by its message id, for the on-demand /tailor endpoint. Returns
    /// null if never ingested (the caller turns that into a 404) - independent of whether it
    /// has been scored yet.
    /// </summary>
    Task<JobPosting?> GetPostingByMessageIdAsync(string messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every posting with scored_at IS NULL, with its stored embedding - the candidate
    /// pool for a /run pass. This is the whole archive, not just a fresh ingest batch.
    /// </summary>
    Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets scored_at = now() and stores score/reasoning for each entry, so a posting is
    /// never re-scored or re-notified by a later run and GetMatchesAsync can serve it
    /// without a live model call. Called for every scored posting, matched or not.
    /// </summary>
    Task MarkScoredAsync(IReadOnlyList<ScoredMark> scored, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stored postings with score >= matchThreshold, ordered by score descending - what
    /// GET /matches serves, so matches from a past /run are visible without a re-run.
    /// </summary>
    Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(int matchThreshold, CancellationToken cancellationToken = default);
}
