using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;

namespace JobLens.Core.Scoring;

public record ScoredPosting(string Id, JobPosting Posting, int Score, string Reasoning, string TemplateName);

public interface IRelevanceScorer
{
    /// <summary>
    /// Routes each candidate locally (cosine similarity, no model call) to whichever
    /// configured scoring template's profile it's closest to, takes the configured
    /// top-K across all candidates by that best-template similarity, groups the
    /// shortlist by selected template, and asks the model to score each group against
    /// its own template's profile with reasoning. Invalid model output and provider
    /// failures degrade safely to no scores for the affected template group only; see
    /// LlmRelevanceScorer for the exact contract. TemplateName on the result is the
    /// locally-selected template - the model is never asked to choose or return one.
    /// The returned Id lets a caller (e.g. PipelineRunner) mark exactly what was
    /// actually scored - candidates beyond the top-K cutoff, or an item the model's
    /// response failed to validate, are absent from the result and so never get marked.
    /// </summary>
    Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
        IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
        IRunObserver observer,
        CancellationToken cancellationToken = default) =>
        ScoreAsync(candidates, cancellationToken);
}
