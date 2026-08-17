using JobLens.Core.Configuration;
using JobLens.Core.Notification;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Pipeline;

/// <param name="Batches">How many scoring calls were made - each one bounded internally by
/// IRelevanceScorer's own ScoringTopK cutoff, never re-sliced by PipelineRunner.</param>
/// <param name="DraftsCreated">Postings scored >= AutoTailorThreshold this run for which a new
/// TailoredDraft was actually persisted (a tailoring-model call ran).</param>
/// <param name="DraftsReused">Postings scored >= AutoTailorThreshold this run for which an
/// existing TailoredDraft was found and returned - no model call, no new row.</param>
/// <param name="TailoringFailures">Postings scored >= AutoTailorThreshold this run whose
/// automatic draft creation failed (model, Rezi-read, or validation failure). The posting's
/// score/match is still persisted and notified; only its draft is missing.</param>
/// <param name="StoppedEarly">True if the loop stopped before the backlog was fully drained
/// because a batch returned zero usable scores, rather than looping forever on an
/// unscoreable remainder.</param>
/// <param name="StopReason">Human-readable reason when StoppedEarly is true; null otherwise.</param>
public record RunSummary(
    int Batches,
    int Scored,
    int Matched,
    int Notified,
    int DraftsCreated,
    int DraftsReused,
    int TailoringFailures,
    bool StoppedEarly,
    string? StopReason);

/// <summary>
/// The "score my whole archive" loop: repeatedly routes and ranks every still-unscored
/// posting against the configured scoring templates, sends it to the model (which internally
/// bounds each call to ScoringTopK candidates via its own cosine prefilter), notifies matches at or above
/// MatchThreshold, and marks exactly what the model actually scored as scored_at=now - then
/// loops again on whatever backlog remains, since MarkScoredAsync shrinks what the next
/// GetUnscoredPostingsAsync call returns. Keeps looping until the backlog is fully drained,
/// or a batch returns zero usable scores - which stops the loop instead of retrying the same
/// unscoreable remainder forever. Notifications are deduped across the *entire* run, not
/// just within one batch, so a repost that lands in a later batch than its duplicate is
/// never notified twice.
///
/// Postings scoring >= AutoTailorThreshold also get a TailoredDraft automatically created (or
/// reused) via TailoredDraftService - the same path POST /tailor uses, never duplicated here.
/// This never writes to Rezi (TailoredDraftService has no dependency on IResumeTailor's
/// upstream Rezi client's write path or on TailoredDraftExporter) and is fail-soft at the run
/// level: a single posting's tailoring failure is logged and counted, but never discards that
/// posting's already-persisted score/match or stops the rest of the run.
/// </summary>
public class PipelineRunner(
    IDatastore datastore,
    IRelevanceScorer scorer,
    INotifier notifier,
    TailoredDraftService draftService,
    IOptions<JobLensOptions> options,
    ILogger<PipelineRunner> logger)
{
    public async Task<RunSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var batches = 0;
        var totalScored = 0;
        var totalMatched = 0;
        var totalNotified = 0;
        var totalDraftsCreated = 0;
        var totalDraftsReused = 0;
        var totalTailoringFailures = 0;
        var stoppedEarly = false;
        string? stopReason = null;

        // Owned by the whole run, not per batch, so a repost scored in a later batch than
        // its duplicate is still recognized and never double-notified.
        var notifiedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unscored = await datastore.GetUnscoredPostingsAsync(cancellationToken);
            if (unscored.Count == 0)
                break;

            var candidates = unscored.Select(u => (u.MessageId, u.Posting, u.Embedding)).ToList();

            // Template routing (which scoring template each candidate is scored against) is
            // resolved locally inside the scorer, per-candidate, from its own catalog - no
            // profile embedding is fetched here anymore.
            var scored = await scorer.ScoreAsync(candidates, cancellationToken);
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
                scored.Select(s => new ScoredMark(s.Id, s.Score, s.Reasoning, s.TemplateName)).ToList(),
                cancellationToken);

            totalScored += scored.Count;

            // Automatic drafting: every posting >= AutoTailorThreshold in this batch, not just
            // the notification-deduped toNotify subset - draft eligibility is a per-posting
            // score threshold, independent of cross-run notification dedup. Runs after
            // MarkScoredAsync because TailoredDraftService requires the posting's score/template
            // to already be persisted. Fail-soft: one posting's tailoring failure is logged and
            // counted, never discards its already-persisted score/match, and never stops the
            // rest of this batch or run - except for cancellation, which always propagates.
            foreach (var match in matches.Where(m => m.Score >= options.Value.AutoTailorThreshold))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await draftService.CreateOrGetAsync(match.Id, cancellationToken);
                    if (result is null)
                        continue; // Not expected (just scored), but not a failure either.

                    if (result.WasCreated)
                        totalDraftsCreated++;
                    else
                        totalDraftsReused++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    totalTailoringFailures++;
                    logger.LogWarning(
                        ex,
                        "Automatic tailoring failed for messageId {MessageId}; its score/match is already persisted.",
                        match.Id);
                }
            }
        }

        return new RunSummary(
            batches,
            totalScored,
            totalMatched,
            totalNotified,
            totalDraftsCreated,
            totalDraftsReused,
            totalTailoringFailures,
            stoppedEarly,
            stopReason);
    }
}
