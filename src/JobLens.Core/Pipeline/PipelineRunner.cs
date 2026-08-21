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
    public Task<RunSummary> RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(NullRunObserver.Instance, cancellationToken);

    public async Task<RunSummary> RunAsync(
        IRunObserver observer,
        CancellationToken cancellationToken = default)
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
        var reziAuthKnownBroken = false;

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
            var scored = await scorer.ScoreAsync(candidates, observer, cancellationToken);
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

                // Register only after the notification succeeds. The collector appends in this
                // exact order and later draft callbacks update those same rows in place.
                foreach (var match in toNotify)
                    observer.MatchNotified(match);
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

            // Matches below AutoTailorThreshold keep the collector's initial NotAttempted
            // outcome. Automatic drafting remains independent from notification dedup and runs
            // for every eligible posting in the batch.
            foreach (var match in matches.Where(m => m.Score >= options.Value.AutoTailorThreshold))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // MarkScoredAsync above persisted exactly this template name, and F2's run lock
                // excludes a concurrent writer, so the in-memory value is the persisted value -
                // no re-read. CreateOrGetAsync still loads the row itself when it actually runs.
                var selectedTemplate = match.TemplateName;

                // Reachable via config drift: the scoring template catalog and ReziOptions'
                // base resumes are independent config, so a routed template can have no
                // configured base. Reported as NoTemplate before the run-wide auth flag is
                // consulted, and no draft-service/model/Rezi call is attempted.
                if (!draftService.TryResolveBaseResume(
                        selectedTemplate,
                        out _,
                        out _))
                {
                    observer.DraftResolved(
                        match.Id,
                        selectedTemplate,
                        DraftOutcomes.NoTemplate);
                    continue;
                }

                if (reziAuthKnownBroken)
                {
                    observer.DraftResolved(
                        match.Id,
                        selectedTemplate,
                        DraftOutcomes.SkippedAuth);
                    continue;
                }

                try
                {
                    var result = await draftService.CreateOrGetAsync(match.Id, cancellationToken);
                    if (result is null)
                    {
                        // Unreachable in principle: MarkScoredAsync persisted this posting
                        // moments ago and the run lock excludes a concurrent deleter. Logged at
                        // Error with its own message so that if it ever does fire, it is
                        // distinguishable from an ordinary tailoring failure rather than
                        // vanishing into the TailoringFailures count.
                        totalTailoringFailures++;
                        observer.DraftResolved(
                            match.Id,
                            selectedTemplate,
                            DraftOutcomes.Failed);
                        logger.LogError(
                            "Automatic tailoring found no stored posting for messageId {MessageId} immediately after marking it scored; this should be impossible and indicates the posting row was removed mid-run.",
                            match.Id);
                        continue;
                    }

                    if (result.WasCreated)
                    {
                        totalDraftsCreated++;
                        observer.DraftResolved(
                            match.Id,
                            selectedTemplate,
                            DraftOutcomes.Created,
                            result.Draft.Id);
                    }
                    else
                    {
                        totalDraftsReused++;
                        observer.DraftResolved(
                            match.Id,
                            selectedTemplate,
                            DraftOutcomes.Reused,
                            result.Draft.Id);
                    }
                }
                catch (ReziAuthenticationRequiredException ex)
                {
                    totalTailoringFailures++;
                    reziAuthKnownBroken = true;
                    observer.ReziAuthenticationFailed(ex.Message);
                    observer.DraftResolved(
                        match.Id,
                        selectedTemplate,
                        DraftOutcomes.Failed);
                    logger.LogWarning(
                        ex,
                        "Automatic tailoring requires Rezi authentication for messageId {MessageId}; later eligible postings in this run will be skipped.",
                        match.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    totalTailoringFailures++;
                    observer.DraftResolved(
                        match.Id,
                        selectedTemplate,
                        DraftOutcomes.Failed);
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
