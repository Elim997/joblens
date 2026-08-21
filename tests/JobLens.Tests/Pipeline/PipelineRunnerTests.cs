using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Resume;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using JobLens.Tests.Resume;
using JobLens.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Pipeline;

public class PipelineRunnerTests
{
    // One configured base resume, "General", matching every test's FakeRelevanceScorer output
    // TemplateName below - the same 1:1 template/base-name convention TailoredDraftService
    // enforces in production.
    private const string BaseResumeId = "base-general";
    private const string TemplateName = "General";

    private static JobPosting MakePosting(string title) =>
        new(title, "Acme", "Tel Aviv", "Software", $"https://example.com/{title}", "- test requirement");

    private static JobPosting MakePosting(string title, string company, string applyUrl) =>
        new(title, company, "Tel Aviv", "Software", applyUrl, "- test requirement");

    private static ValidatedTailoredResume MakeTailored() =>
        new(
            new BaseSelection(BaseResumeId, TemplateName, "Selected from persisted scoring metadata."),
            "Summary.",
            [new TailoredExperienceItem("exp-1", "Experience.")],
            [new TailoredSkillItem("skill-1", "Skill.")],
            "Rationale.",
            ["exp-1"],
            ["skill-1"]);

    // autoTailorThreshold defaults to 91 - just above every score used by the pre-Milestone-E
    // tests below (max 90) - so those tests exercise zero auto-tailoring unless a test opts in
    // with its own lower threshold. tailor/draftStore default to a real TailoredDraftService
    // wired the same way production DI wires it, using FakeResumeTailor/FakeTailoredDraftStore
    // test doubles; a tailor that throws if called is the default so any test that accidentally
    // triggers auto-tailoring fails loudly instead of silently passing.
    private static PipelineRunner CreateRunner(
        FakeDatastore datastore,
        FakeRelevanceScorer scorer,
        FakeNotifier notifier,
        int matchThreshold = 70,
        int autoTailorThreshold = 91,
        FakeResumeTailor? tailor = null,
        FakeTailoredDraftStore? draftStore = null,
        IReadOnlyList<BaseResumeConfig>? baseResumes = null)
    {
        tailor ??= new FakeResumeTailor(new InvalidOperationException("Auto-tailoring should not run in this test."));
        draftStore ??= new FakeTailoredDraftStore();
        var reziOptions = Options.Create(new ReziOptions
        {
            BaseResumes = baseResumes?.ToList() ?? [new BaseResumeConfig { Id = BaseResumeId, Name = TemplateName }],
        });
        var draftService = new TailoredDraftService(datastore, tailor, draftStore, reziOptions);

        return new PipelineRunner(
            datastore,
            scorer,
            notifier,
            draftService,
            Options.Create(new JobLensOptions { MatchThreshold = matchThreshold, AutoTailorThreshold = autoTailorThreshold }),
            NullLogger<PipelineRunner>.Instance);
    }

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

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 2, Matched: 1, Notified: 1, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
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

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
    }

    [Fact]
    public async Task RunAsync_NoUnscoredPostings_ReturnsZerosWithoutCallingScorerOrNotifier()
    {
        var datastore = new FakeDatastore();
        var scorer = new FakeRelevanceScorer(_ => throw new InvalidOperationException("Should not be called."));
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 0, Scored: 0, Matched: 0, Notified: 0, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
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
        Assert.Equal(0, summary.DraftsCreated);
        Assert.Equal(0, summary.DraftsReused);
        Assert.Equal(0, summary.TailoringFailures);
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
        Assert.Equal(0, summary.DraftsCreated);
        Assert.Equal(0, summary.DraftsReused);
        Assert.Equal(0, summary.TailoringFailures);
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

        Assert.Equal(
            new RunSummary(Batches: 3, Scored: 5, Matched: 5, Notified: 5, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
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

        Assert.Equal(
            new RunSummary(Batches: 2, Scored: 3, Matched: 3, Notified: 3, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
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

        Assert.Equal(
            new RunSummary(Batches: 2, Scored: 3, Matched: 3, Notified: 2, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);

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
        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 2, Matched: 2, Notified: 2, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            firstRun);
        Assert.Equal(2, notifier.Calls[0].Count);

        var secondRun = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 0, Scored: 0, Matched: 0, Notified: 0, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            secondRun);
        // Both postings are already marked scored, so the second run finds nothing
        // unscored and returns before ever calling the scorer or notifier again -
        // nothing from run one gets re-scored or re-notified.
        Assert.Single(scorer.Calls);
        Assert.Single(notifier.Calls);
    }

    // ---- Milestone E: AutoTailorThreshold / automatic TailoredDraft creation ----

    [Fact]
    public async Task RunAsync_ScoreBelowMatchThreshold_NoMatchNoDraft()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Below Match"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 50, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(new InvalidOperationException("must not be called"));
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 0, Notified: 0, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(0, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_ScoreBetweenMatchAndAutoTailorThreshold_MatchesButDoesNotDraft()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Mid Score"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 75, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(new InvalidOperationException("must not be called"));
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(0, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_ScoreExactlyAtAutoTailorThreshold_CreatesDraft()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Exactly At Auto Threshold"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 80, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 1, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(1, tailor.CallCount);
        Assert.Single(draftStore.Drafts);
        Assert.Equal("id-1", draftStore.Drafts[0].MessageId);
    }

    [Fact]
    public async Task RunAsync_ScoreAboveAutoTailorThreshold_CreatesDraft()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Above Auto Threshold"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 95, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 1, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Single(draftStore.Drafts);
    }

    [Fact]
    public async Task RunAsync_ExistingDraftForQualifyingPosting_ReusesWithoutModelCall()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Already Drafted"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(new InvalidOperationException("must not be called"));
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(new TailoredDraft(
            "existing-draft",
            "id-1",
            90,
            TemplateName,
            BaseResumeId,
            TemplateName,
            "Existing summary.",
            [new TailoredExperienceItem("exp-1", "Existing experience.")],
            [new TailoredSkillItem("skill-1", "Existing skill.")],
            "Existing rationale.",
            TailoredDraftStatus.Draft,
            DateTimeOffset.UtcNow,
            null));
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 0, DraftsReused: 1, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(0, tailor.CallCount);
        Assert.Single(draftStore.Drafts);
    }

    [Fact]
    public async Task RunAsync_MultipleQualifyingPostings_CreatesIndependentDrafts()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("B"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 2, Matched: 2, Notified: 2, DraftsCreated: 2, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(2, tailor.CallCount);
        Assert.Equal(["id-1", "id-2"], draftStore.Drafts.Select(d => d.MessageId).OrderBy(id => id));
    }

    [Fact]
    public async Task RunAsync_OneTailoringFailure_DoesNotLosePersistedScoreOrBlockOtherDrafts()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Fails"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Succeeds"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(posting =>
            posting.Title == "Fails"
                ? throw new TailoringModelUnavailableException("Model unavailable.")
                : MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 2, Matched: 2, Notified: 2, DraftsCreated: 1, DraftsReused: 0, TailoringFailures: 1, StoppedEarly: false, StopReason: null),
            summary);
        // Both postings' scores/matches are persisted regardless of the tailoring outcome.
        Assert.Equal(["id-1", "id-2"], datastore.ScoredMessageIds.OrderBy(id => id));
        Assert.Single(draftStore.Drafts);
        Assert.Equal("id-2", draftStore.Drafts[0].MessageId);
    }

    [Fact]
    public async Task RunAsync_TailoringValidationFailure_PersistsNoDraftAndCountsAsFailure()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Malformed"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        // originalSkillItemIds references "skill-1", which doesn't exist among the produced
        // skill items ("invented") - ResumeTailoringValidator.ValidateComplete rejects this.
        var malformed = new ValidatedTailoredResume(
            new BaseSelection(BaseResumeId, TemplateName, "Trusted mapping."),
            "Summary.",
            [new TailoredExperienceItem("exp-1", "Valid text.")],
            [new TailoredSkillItem("invented", "Invented item.")],
            "Rationale.",
            ["exp-1"],
            ["skill-1"]);
        var tailor = new FakeResumeTailor(malformed);
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 1, Matched: 1, Notified: 1, DraftsCreated: 0, DraftsReused: 0, TailoringFailures: 1, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Empty(draftStore.Drafts);
        Assert.Equal(["id-1"], datastore.ScoredMessageIds);
    }

    [Fact]
    public async Task RunAsync_ReziReadFailureForOneJob_IsFailSoftAndDoesNotBlockOtherDrafts()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("ReziFails"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Succeeds"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(posting =>
            posting.Title == "ReziFails"
                ? throw new ReziToolCallException("read_resume", "Rezi read failed.")
                : MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 1, Scored: 2, Matched: 2, Notified: 2, DraftsCreated: 1, DraftsReused: 0, TailoringFailures: 1, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Single(draftStore.Drafts);
        Assert.Equal("id-2", draftStore.Drafts[0].MessageId);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringAutoTailorLoop_PropagatesAndSkipsRemainingPostings()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("First"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Second"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        using var cts = new CancellationTokenSource();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        // Cancel only once the first posting's draft has actually been persisted, so
        // cancellation lands cleanly at the top of the *next* loop iteration - not mid-call,
        // which would make FakeTailoredDraftStore's own cancellation check abort id-1's own
        // (still in-flight) persistence too.
        draftStore.AfterCreateOrGet = draft =>
        {
            if (draft.MessageId == "id-1")
                cts.Cancel();
        };
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(cts.Token));

        // Only the first posting's draft attempt ran before cancellation was observed at the
        // top of the next loop iteration; the fail-soft catch block explicitly excludes
        // OperationCanceledException, so this must propagate rather than be swallowed/counted.
        Assert.Equal(1, tailor.CallCount);
        Assert.Single(draftStore.Drafts);
        Assert.Equal("id-1", draftStore.Drafts[0].MessageId);
    }

    [Fact]
    public async Task RunAsync_MultipleBatches_DraftCountersAccumulateAcrossBatches()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("B"), [1f, 0f, 0f]);
        datastore.Seed("id-3", MakePosting("C"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Take(2).Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor, draftStore: draftStore);

        var summary = await runner.RunAsync();

        Assert.Equal(
            new RunSummary(Batches: 2, Scored: 3, Matched: 3, Notified: 3, DraftsCreated: 3, DraftsReused: 0, TailoringFailures: 0, StoppedEarly: false, StopReason: null),
            summary);
        Assert.Equal(3, draftStore.Drafts.Count);
    }

    [Fact]
    public async Task RunAsync_AutoTailoring_DoesNotChangeNotificationBehavior()
    {
        // Draft creation runs entirely after notifier.NotifyAsync is called and never calls
        // the notifier itself - a created draft must not add or duplicate a notification.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("A"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", "General")).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70, autoTailorThreshold: 80, tailor: tailor);

        var summary = await runner.RunAsync();

        Assert.Equal(1, summary.Notified);
        Assert.Single(notifier.Calls);
        Assert.Single(notifier.Calls[0]);
    }

    [Fact]
    public async Task RunAsync_WithCollector_ReportsNotificationOrderAndDraftOutcomes()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-created", MakePosting("Created"), [1f, 0f, 0f]);
        datastore.Seed("id-reused", MakePosting("Reused"), [1f, 0f, 0f]);
        datastore.Seed("id-not-attempted", MakePosting("Not Attempted"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(
                c.Id,
                c.Posting,
                c.Id == "id-not-attempted" ? 75 : 90,
                "reason",
                TemplateName)).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(MakeTailored());
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(new TailoredDraft(
            "existing-draft",
            "id-reused",
            90,
            TemplateName,
            BaseResumeId,
            TemplateName,
            "Existing summary.",
            [new TailoredExperienceItem("exp-1", "Existing experience.")],
            [new TailoredSkillItem("skill-1", "Existing skill.")],
            "Existing rationale.",
            TailoredDraftStatus.Draft,
            DateTimeOffset.UtcNow,
            null));
        var runner = CreateRunner(
            datastore,
            scorer,
            notifier,
            matchThreshold: 70,
            autoTailorThreshold: 80,
            tailor: tailor,
            draftStore: draftStore);
        var collector = new RunDetailCollector();

        var summary = await runner.RunAsync(collector);

        Assert.Equal(3, summary.Notified);
        Assert.Collection(
            collector.Matches,
            detail =>
            {
                Assert.Equal("id-created", detail.MessageId);
                Assert.Equal("Created", detail.Title);
                Assert.Equal(DraftOutcomes.Created, detail.DraftOutcome);
                Assert.NotNull(detail.DraftId);
            },
            detail =>
            {
                Assert.Equal("id-reused", detail.MessageId);
                Assert.Equal(DraftOutcomes.Reused, detail.DraftOutcome);
                Assert.Equal("existing-draft", detail.DraftId);
            },
            detail =>
            {
                Assert.Equal("id-not-attempted", detail.MessageId);
                Assert.Equal(DraftOutcomes.NotAttempted, detail.DraftOutcome);
                Assert.Null(detail.DraftId);
            });
    }

    [Fact]
    public async Task RunAsync_WithCollector_DedupSuppressedMatchHasNoDetailButCanReportRunWideAuthFailure()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-notified", MakePosting("First", "Acme", "https://example.com/job"), [1f, 0f, 0f]);
        datastore.Seed("id-suppressed", MakePosting("Repost", "Acme", "https://example.com/job"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", TemplateName)).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(posting =>
            posting.Title == "Repost"
                ? throw new ReziAuthenticationRequiredException()
                : MakeTailored());
        var runner = CreateRunner(
            datastore,
            scorer,
            notifier,
            matchThreshold: 70,
            autoTailorThreshold: 80,
            tailor: tailor);
        var collector = new RunDetailCollector();

        var summary = await runner.RunAsync(collector);

        Assert.Equal(2, summary.Matched);
        Assert.Equal(1, summary.Notified);
        Assert.Equal(1, summary.DraftsCreated);
        Assert.Equal(1, summary.TailoringFailures);
        Assert.Equal("id-notified", Assert.Single(collector.Matches).MessageId);
        Assert.NotNull(collector.ReziAuthenticationError);
        Assert.Equal(2, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_NotificationFailure_RegistersNoDetails()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Notify Failure"), [1f, 0f, 0f]);
        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 75, "reason", TemplateName)).ToList());
        var notifier = new ThrowingNotifier(new InvalidOperationException("Notification unavailable."));
        var draftService = new TailoredDraftService(
            datastore,
            new FakeResumeTailor(new InvalidOperationException("must not run")),
            new FakeTailoredDraftStore(),
            Options.Create(new ReziOptions
            {
                BaseResumes = [new BaseResumeConfig { Id = BaseResumeId, Name = TemplateName }],
            }));
        var runner = new PipelineRunner(
            datastore,
            scorer,
            notifier,
            draftService,
            Options.Create(new JobLensOptions { MatchThreshold = 70, AutoTailorThreshold = 80 }),
            NullLogger<PipelineRunner>.Instance);
        var collector = new RunDetailCollector();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(collector));

        Assert.Empty(collector.Matches);
        Assert.Empty(datastore.ScoredMessageIds);
    }

    [Fact]
    public async Task RunAsync_ReziAuthenticationFailure_SkipsLaterValidTemplatesButChecksNoTemplateFirst()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-auth", MakePosting("Auth Failure"), [1f, 0f, 0f]);
        datastore.Seed("id-no-template", MakePosting("No Template"), [1f, 0f, 0f]);
        datastore.Seed("id-skipped", MakePosting("Skipped Auth"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(
                c.Id,
                c.Posting,
                90,
                "reason",
                c.Id == "id-no-template" ? "Removed Template" : TemplateName)).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(new ReziAuthenticationRequiredException());
        var runner = CreateRunner(
            datastore,
            scorer,
            notifier,
            matchThreshold: 70,
            autoTailorThreshold: 80,
            tailor: tailor);
        var collector = new RunDetailCollector();

        var summary = await runner.RunAsync(collector);

        Assert.Equal(1, summary.TailoringFailures);
        Assert.Equal(1, tailor.CallCount);
        Assert.NotNull(collector.ReziAuthenticationError);
        Assert.Collection(
            collector.Matches,
            detail =>
            {
                Assert.Equal("id-auth", detail.MessageId);
                Assert.Equal(DraftOutcomes.Failed, detail.DraftOutcome);
            },
            detail =>
            {
                Assert.Equal("id-no-template", detail.MessageId);
                Assert.Equal("Removed Template", detail.SelectedTemplate);
                Assert.Equal(DraftOutcomes.NoTemplate, detail.DraftOutcome);
            },
            detail =>
            {
                Assert.Equal("id-skipped", detail.MessageId);
                Assert.Equal(DraftOutcomes.SkippedAuth, detail.DraftOutcome);
            });
    }

    [Fact]
    public async Task RunAsync_NullTemplateName_ReportsNoTemplateWithoutTailoringFailure()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-null-template", MakePosting("Legacy"), [1f, 0f, 0f]);
        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", null!)).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(new InvalidOperationException("must not run"));
        var runner = CreateRunner(
            datastore,
            scorer,
            notifier,
            matchThreshold: 70,
            autoTailorThreshold: 80,
            tailor: tailor);
        var collector = new RunDetailCollector();

        var summary = await runner.RunAsync(collector);

        Assert.Equal(0, summary.TailoringFailures);
        Assert.Equal(0, tailor.CallCount);
        var detail = Assert.Single(collector.Matches);
        Assert.Null(detail.SelectedTemplate);
        Assert.Equal(DraftOutcomes.NoTemplate, detail.DraftOutcome);
    }

    [Fact]
    public async Task RunAsync_OrdinaryTailoringFailure_ReportsFailedAndContinues()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-failed", MakePosting("Fails"), [1f, 0f, 0f]);
        datastore.Seed("id-created", MakePosting("Succeeds"), [1f, 0f, 0f]);
        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason", TemplateName)).ToList());
        var notifier = new FakeNotifier();
        var tailor = new FakeResumeTailor(posting =>
            posting.Title == "Fails"
                ? throw new TailoringModelUnavailableException("Model unavailable.")
                : MakeTailored());
        var runner = CreateRunner(
            datastore,
            scorer,
            notifier,
            matchThreshold: 70,
            autoTailorThreshold: 80,
            tailor: tailor);
        var collector = new RunDetailCollector();

        var summary = await runner.RunAsync(collector);

        Assert.Equal(1, summary.TailoringFailures);
        Assert.Equal(1, summary.DraftsCreated);
        Assert.Equal(DraftOutcomes.Failed, collector.Matches[0].DraftOutcome);
        Assert.Equal(DraftOutcomes.Created, collector.Matches[1].DraftOutcome);
        Assert.Equal(["id-created", "id-failed"], datastore.ScoredMessageIds.OrderBy(id => id));
    }

    private sealed class ThrowingNotifier(Exception exception) : JobLens.Core.Notification.INotifier
    {
        public Task NotifyAsync(
            IReadOnlyList<ScoredPosting> matches,
            CancellationToken cancellationToken = default) =>
            Task.FromException(exception);
    }
}
