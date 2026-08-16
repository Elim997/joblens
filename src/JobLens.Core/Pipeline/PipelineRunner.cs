using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Notification;
using JobLens.Core.Parsing;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Pipeline;

/// <param name="Batches">How many scoring calls were made - each one bounded internally by
/// IRelevanceScorer's own ScoringTopK cutoff, never re-sliced by PipelineRunner.</param>
/// <param name="StoppedEarly">True if the loop stopped before the backlog was fully drained
/// because a batch returned zero usable scores, rather than looping forever on an
/// unscoreable remainder.</param>
/// <param name="StopReason">Human-readable reason when StoppedEarly is true; null otherwise.</param>
public record RunSummary(int Batches, int Scored, int Matched, int Notified, bool StoppedEarly, string? StopReason);

/// <summary>
/// The "score my whole archive" loop: repeatedly ranks every still-unscored posting in
/// pgvector against the profile, sends it to the model (which internally bounds each call
/// to ScoringTopK candidates via its own cosine prefilter), notifies matches at or above
/// MatchThreshold, and marks exactly what the model actually scored as scored_at=now - then
/// loops again on whatever backlog remains, since MarkScoredAsync shrinks what the next
/// GetUnscoredPostingsAsync call returns. Keeps looping until the backlog is fully drained,
/// or a batch returns zero usable scores - which stops the loop instead of retrying the same
/// unscoreable remainder forever. Notifications are deduped across the *entire* run, not
/// just within one batch, so a repost that lands in a later batch than its duplicate is
/// never notified twice.
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
        var batches = 0;
        var totalScored = 0;
        var totalMatched = 0;
        var totalNotified = 0;
        var stoppedEarly = false;
        string? stopReason = null;

        // Owned by the whole run, not per batch, so a repost scored in a later batch than
        // its duplicate is still recognized and never double-notified.
        var notifiedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fetched lazily on the first batch that actually needs it, then reused for every
        // subsequent batch in this run - the profile doesn't change mid-run, so there's no
        // reason to re-embed it once per batch.
        float[]? profileEmbedding = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unscored = await datastore.GetUnscoredPostingsAsync(cancellationToken);
            if (unscored.Count == 0)
                break;

            profileEmbedding ??= await profileEmbeddingProvider.GetProfileEmbeddingAsync(cancellationToken);
            var candidates = unscored.Select(u => (u.MessageId, u.Posting, u.Embedding)).ToList();

            var scored = await scorer.ScoreAsync(candidates, profileEmbedding, cancellationToken);
            batches++;

            if (scored.Count == 0)
            {
                // Fail-soft, run-level: the scorer already degrades safely on invalid/empty
                // model output (see LlmRelevanceScorer), but if an entire batch comes back
                // empty, retrying it here would spin forever on the same unscoreable
                // remainder. Stop and report a partial summary instead; nothing from this
                // batch is marked, so it's still there for a future /run to retry.
                stoppedEarly = true;
                stopReason = "A scoring batch returned zero usable scores; stopped to avoid retrying the same unscoreable backlog.";
                break;
            }

            var matches = scored.Where(s => s.Score >= options.Value.MatchThreshold).ToList();
            totalMatched += matches.Count;

            // The same job sometimes gets posted more than once (repost, tiny company-name
            // spelling difference); collapse before notifying so a duplicate never sends a
            // second notification, even if it lands in a different batch. Every scored
            // posting is still marked below regardless - dedup only affects what gets sent,
            // not what counts as scored.
            var toNotify = NearDuplicateCollapser.Collapse(matches, m => m.Posting, notifiedKeys);
            if (toNotify.Count > 0)
            {
                await notifier.NotifyAsync(toNotify, cancellationToken);
                totalNotified += toNotify.Count;
            }

            // Mark everything the model actually scored (matched or not) - not the whole
            // unscored set, so anything beyond this batch's shortlist stays available for
            // the next loop iteration (or a future run) instead of silently never being
            // considered. Persist score and reasoning too, so a match survives past this
            // response for GET /matches.
            await datastore.MarkScoredAsync(
                scored.Select(s => new ScoredMark(s.Id, s.Score, s.Reasoning)).ToList(),
                cancellationToken);

            totalScored += scored.Count;
        }

        return new RunSummary(batches, totalScored, totalMatched, totalNotified, stoppedEarly, stopReason);
    }
}
