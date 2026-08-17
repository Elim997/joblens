using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Scoring;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Pipeline;

public class PipelineRunnerTests
{
    private static JobPosting MakePosting(string title) =>
        new(title, "Acme", "Tel Aviv", "Software", $"https://example.com/{title}", "- test requirement");

    private static JobPosting MakePosting(string title, string company, string applyUrl) =>
        new(title, company, "Tel Aviv", "Software", applyUrl, "- test requirement");

    private static PipelineRunner CreateRunner(
        FakeDatastore datastore, FakeRelevanceScorer scorer, FakeNotifier notifier, int matchThreshold = 70) =>
        new(datastore, scorer, notifier,
            Options.Create(new JobLensOptions { MatchThreshold = matchThreshold }));

    [Fact]
    public async Task RunAsync_ScoresAll_NotifiesOnlyAtOrAboveThreshold_MarksEveryScoredPosting()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("High Score"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Low Score"), [0f, 1f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, c.Id == "id-1" ? 90 : 30, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 1, Scored: 2, Matched: 1, Notified: 1, StoppedEarly: false, StopReason: null), summary);
        Assert.Single(notifier.Calls);
        Assert.Equal("High Score", notifier.Calls[0].Single().Posting.Title);
        // Both postings were scored (matched or not), so both are marked - the
        // low scorer isn't left to be re-scored just because it didn't match.
        Assert.Equal(["id-1", "id-2"], datastore.ScoredMessageIds.OrderBy(id => id));

        // Score and reasoning are persisted for both, not just the matched one -
        // this is the Milestone 6 follow-up gap: /run's response is a snapshot,
        // but the datastore must keep the data past that response.
        Assert.Equal((90, "reason", "General"), datastore.GetPersistedScore("id-1"));
        Assert.Equal((30, "reason", "General"), datastore.GetPersistedScore("id-2"));
    }

    [Fact]
    public async Task RunAsync_ScoreExactlyAtThreshold_CountsAsMatch()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Exactly At Threshold"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 70, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, StoppedEarly: false, StopReason: null), summary);
    }

    [Fact]
    public async Task RunAsync_NoUnscoredPostings_ReturnsZerosWithoutCallingScorerOrNotifier()
    {
        var datastore = new FakeDatastore();
        var scorer = new FakeRelevanceScorer(_ => throw new InvalidOperationException("Should not be called."));
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 0, Scored: 0, Matched: 0, Notified: 0, StoppedEarly: false, StopReason: null), summary);
        Assert.Empty(scorer.Calls);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task RunAsync_ScorerReturnsEmpty_StopsSafelyWithoutMarkingAndAllowsRetry()
    {
        // Simulates LlmRelevanceScorer giving up after a malformed model response - a
        // zero-usable-score batch must stop the loop safely instead of retrying the same
        // unscoreable posting forever.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Unscoreable"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(_ => []);
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier);

        var summary = await runner.RunAsync();

        Assert.Equal(1, summary.Batches);
        Assert.Equal(0, summary.Scored);
        Assert.Equal(0, summary.Matched);
        Assert.Equal(0, summary.Notified);
        Assert.True(summary.StoppedEarly);
        Assert.False(string.IsNullOrWhiteSpace(summary.StopReason));
        Assert.Empty(notifier.Calls);
        Assert.Empty(datastore.ScoredMessageIds);

        // Still unscored, so a later run would pick it up again.
        var stillUnscored = await datastore.GetUnscoredPostingsAsync();
        Assert.Single(stillUnscored);
    }

    [Fact]
    public async Task RunAsync_LaterBatchReturnsZeroScores_StopsEarlyButKeepsPriorBatchResults()
    {
        // First batch scores id-1 successfully; the second batch (id-2 only) comes back
        // empty, as if the model's response for the remaining backlog failed validation.
        // The loop must stop instead of looping forever, but must not lose or re-attempt
        // what the first batch already scored.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Scoreable"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Unscoreable"), [1f, 0f, 0f]);

        var callCount = 0;
        var scorer = new FakeRelevanceScorer(candidates =>
        {
            callCount++;
            // First call sees both postings (nothing scored yet) but only scores id-1 -
            // simulating the real scorer's own topK/validation cutoff leaving id-2 behind.
            if (callCount == 1)
                return candidates.Where(c => c.Id == "id-1")
                    .Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList();
            return [];
        });
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(2, summary.Batches);
        Assert.Equal(1, summary.Scored);
        Assert.Equal(1, summary.Matched);
        Assert.Equal(1, summary.Notified);
        Assert.True(summary.StoppedEarly);
        Assert.False(string.IsNullOrWhiteSpace(summary.StopReason));

        // id-1's result from the first batch survived; id-2 stays unscored for a future run.
        Assert.Equal(["id-1"], datastore.ScoredMessageIds);
        var stillUnscored = await datastore.GetUnscoredPostingsAsync();
        Assert.Equal(["id-2"], stillUnscored.Select(u => u.MessageId));
    }

    [Fact]
    public async Task RunAsync_MoreCandidatesThanOneBatch_LoopsUntilBacklogDrained()
    {
        // Simulates a scorer whose internal ScoringTopK bounds each call's *scored output*
        // to 2 candidates (mirroring LlmRelevanceScorer's own cosine-prefilter + topK cutoff).
        // PipelineRunner doesn't slice the candidate list itself - it passes the whole
        // remaining backlog each call and just keeps looping until GetUnscoredPostingsAsync
        // comes back empty, so each call's *input* naturally shrinks as prior batches get
        // marked scored.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("B"), [1f, 0f, 0f]);
        datastore.Seed("id-3", MakePosting("C"), [1f, 0f, 0f]);
        datastore.Seed("id-4", MakePosting("D"), [1f, 0f, 0f]);
        datastore.Seed("id-5", MakePosting("E"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Take(2).Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 3, Scored: 5, Matched: 5, Notified: 5, StoppedEarly: false, StopReason: null), summary);
        Assert.Equal(3, scorer.Calls.Count);
        // Each call's input is the whole remaining backlog - 5, then 3, then 1 - shrinking
        // as MarkScoredAsync removes what the prior batch scored.
        Assert.Equal(5, scorer.Calls[0].Count);
        Assert.Equal(3, scorer.Calls[1].Count);
        Assert.Single(scorer.Calls[2]);
        Assert.Equal(["id-1", "id-2", "id-3", "id-4", "id-5"], datastore.ScoredMessageIds.OrderBy(id => id));
    }

    [Fact]
    public async Task RunAsync_BatchScoresFewerThanPassed_RemainderIsPickedUpByNextBatch()
    {
        // Simulates the real scorer dropping one item whose model response failed
        // validation: that item stays unscored and is retried in the very next loop
        // iteration alongside nothing else new, not lost or re-sent with fresh candidates.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("B"), [1f, 0f, 0f]);
        datastore.Seed("id-3", MakePosting("C"), [1f, 0f, 0f]);

        var callCount = 0;
        var scorer = new FakeRelevanceScorer(candidates =>
        {
            callCount++;
            var toScore = callCount == 1 ? candidates.Where(c => c.Id != "id-2") : candidates;
            return toScore.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList();
        });
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 2, Scored: 3, Matched: 3, Notified: 3, StoppedEarly: false, StopReason: null), summary);
        Assert.Equal(2, scorer.Calls.Count);
        Assert.Equal(3, scorer.Calls[0].Count);
        Assert.Single(scorer.Calls[1]);
        Assert.Equal("id-2", scorer.Calls[1][0].Id);
    }

    [Fact]
    public async Task RunAsync_CancelledBetweenBatches_ThrowsAndStopsProcessingFurtherBatches()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("B"), [1f, 0f, 0f]);
        datastore.Seed("id-3", MakePosting("C"), [1f, 0f, 0f]);

        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var scorer = new FakeRelevanceScorer(candidates =>
        {
            callCount++;
            var batch = candidates.Take(1).ToList();
            if (callCount == 2)
                cts.Cancel(); // cancel after the 2nd batch is scored - the loop should not attempt a 3rd
            return batch.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList();
        });
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(cts.Token));

        // The 2 batches that completed before cancellation was observed are still persisted;
        // the 3rd was never attempted.
        Assert.Equal(2, scorer.Calls.Count);
        Assert.Equal(2, datastore.ScoredMessageIds.Count);
    }

    [Fact]
    public async Task RunAsync_SameApplyUrlScoredInDifferentBatches_NotifiesOnlyOnce()
    {
        // The same job reposted under a different message id lands in a *different* batch
        // than its duplicate - dedup must survive across the whole run, not just within
        // one batch's notification call.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Backend Engineer", "Acme", "https://example.com/job/123"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Other Job"), [1f, 0f, 0f]);
        datastore.Seed("id-3", MakePosting("Backend Engineer", "Acme Inc.", "https://example.com/job/123"), [1f, 0f, 0f]);

        var callCount = 0;
        var scorer = new FakeRelevanceScorer(candidates =>
        {
            callCount++;
            var batch = callCount == 1 ? candidates.Take(1).ToList() : candidates.ToList();
            return batch.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList();
        });
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 2, Scored: 3, Matched: 3, Notified: 2, StoppedEarly: false, StopReason: null), summary);

        // Across both notifier calls combined, the shared-URL job appears exactly once.
        var allNotified = notifier.Calls.SelectMany(c => c).ToList();
        Assert.Equal(2, allNotified.Count);
        Assert.Single(allNotified, m => m.Posting.ApplyUrl == "https://example.com/job/123");
    }

    [Fact]
    public async Task RunAsync_CalledTwice_SecondPassNotifiesZeroAndDoesNotRescoreFirstRunsPostings()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Match One"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Match Two"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var firstRun = await runner.RunAsync();
        Assert.Equal(new RunSummary(Batches: 1, Scored: 2, Matched: 2, Notified: 2, StoppedEarly: false, StopReason: null), firstRun);
        Assert.Equal(2, notifier.Calls[0].Count);

        var secondRun = await runner.RunAsync();

        Assert.Equal(new RunSummary(Batches: 0, Scored: 0, Matched: 0, Notified: 0, StoppedEarly: false, StopReason: null), secondRun);
        // Both postings are already marked scored, so the second run finds nothing
        // unscored and returns before ever calling the scorer or notifier again -
        // nothing from run one gets re-scored or re-notified.
        Assert.Single(scorer.Calls);
        Assert.Single(notifier.Calls);
    }
}
