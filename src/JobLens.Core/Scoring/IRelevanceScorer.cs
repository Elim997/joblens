using JobLens.Core.Parsing;

namespace JobLens.Core.Scoring;

public record ScoredPosting(JobPosting Posting, int Score, string Reasoning);

public interface IRelevanceScorer
{
    /// <summary>
    /// Ranks candidates by cosine similarity to the profile embedding (cheap, local,
    /// no archive lookup), takes the configured top-K, and asks the model to score
    /// only that shortlist with reasoning. A malformed model response never throws -
    /// see GeminiRelevanceScorer for the exact fallback contract.
    /// </summary>
    Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(JobPosting Posting, float[] Embedding)> candidates,
        float[] profileEmbedding,
        CancellationToken cancellationToken = default);
}
