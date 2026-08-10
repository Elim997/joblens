using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Notification;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Pipeline;

public record RunSummary(int Scored, int Matched, int Notified);

/// <summary>
/// The "score my whole archive" loop: ranks every unscored posting in pgvector
/// against the profile, sends the top ScoringTopK to the model, notifies matches
/// at or above MatchThreshold, and marks exactly what the model actually scored
/// as scored_at=now so it is never re-scored or re-notified by a later run.
/// Postings beyond the top-K cutoff stay unscored for a future run to pick up.
/// </summary>
public class PipelineRunner(
    IDatastore datastore,
    IRelevanceScorer scorer,
    IProfileEmbeddingProvider profileEmbeddingProvider,
    INotifier notifier,
    IOptions<JobLensOptions> options)
{
    public async Task<RunSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var unscored = await datastore.GetUnscoredPostingsAsync(cancellationToken);
        if (unscored.Count == 0)
            return new RunSummary(0, 0, 0);

        var profileEmbedding = await profileEmbeddingProvider.GetProfileEmbeddingAsync(cancellationToken);
        var candidates = unscored.Select(u => (u.MessageId, u.Posting, u.Embedding)).ToList();

        var scored = await scorer.ScoreAsync(candidates, profileEmbedding, cancellationToken);
        if (scored.Count == 0)
            return new RunSummary(0, 0, 0);

        var matches = scored.Where(s => s.Score >= options.Value.MatchThreshold).ToList();
        if (matches.Count > 0)
            await notifier.NotifyAsync(matches, cancellationToken);

        // Mark everything the model actually scored (matched or not) - not the whole
        // unscored set, so anything beyond this run's shortlist stays available for
        // the next run instead of silently never being considered. Persist score and
        // reasoning too, so a match survives past this response for GET /matches.
        await datastore.MarkScoredAsync(
            scored.Select(s => new ScoredMark(s.Id, s.Score, s.Reasoning)).ToList(),
            cancellationToken);

        return new RunSummary(scored.Count, matches.Count, matches.Count);
    }
}
