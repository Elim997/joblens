using JobLens.Core.Parsing;

namespace JobLens.Core.Scoring;

public record ScoredPosting(string Id, JobPosting Posting, int Score, string Reasoning);

public interface IRelevanceScorer
{
    /// <summary>
    /// Ranks candidates by cosine similarity to the profile embedding (cheap, local,
    /// no archive lookup), takes the configured top-K, and asks the model to score
    /// only that shortlist with reasoning. A malformed model response never throws -
    /// see GeminiRelevanceScorer for the exact fallback contract. The returned Id
    /// lets a caller (e.g. PipelineRunner) mark exactly what was actually scored -
    /// candidates beyond the top-K cutoff, or an item the model's response failed to
    /// validate, are absent from the result and so never get marked.
    /// </summary>
    Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        float[] profileEmbedding,
        CancellationToken cancellationToken = default);
}
