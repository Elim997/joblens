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
        new(datastore, scorer, new FakeProfileEmbeddingProvider([1f, 0f, 0f]), notifier,
            Options.Create(new JobLensOptions { Profile = "test profile", MatchThreshold = matchThreshold }));

    [Fact]
    public async Task RunAsync_ScoresAll_NotifiesOnlyAtOrAboveThreshold_MarksEveryScoredPosting()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("High Score"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Low Score"), [0f, 1f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, c.Id == "id-1" ? 90 : 30, "reason")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Scored: 2, Matched: 1, Notified: 1), summary);
        Assert.Single(notifier.Calls);
        Assert.Equal("High Score", notifier.Calls[0].Single().Posting.Title);
        // Both postings were scored (matched or not), so both are marked - the
        // low scorer isn't left to be re-scored just because it didn't match.
        Assert.Equal(["id-1", "id-2"], datastore.ScoredMessageIds.OrderBy(id => id));

        // Score and reasoning are persisted for both, not just the matched one -
        // this is the Milestone 6 follow-up gap: /run's response is a snapshot,
        // but the datastore must keep the data past that response.
        Assert.Equal((90, "reason"), datastore.GetPersistedScoreAndReasoning("id-1"));
        Assert.Equal((30, "reason"), datastore.GetPersistedScoreAndReasoning("id-2"));
    }

    [Fact]
    public async Task RunAsync_ScoreExactlyAtThreshold_CountsAsMatch()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Exactly At Threshold"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 70, "reason")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(Scored: 1, Matched: 1, Notified: 1), summary);
    }

    [Fact]
    public async Task RunAsync_NoUnscoredPostings_ReturnsZerosWithoutCallingScorerOrNotifier()
    {
        var datastore = new FakeDatastore();
        var scorer = new FakeRelevanceScorer(_ => throw new InvalidOperationException("Should not be called."));
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(0, 0, 0), summary);
        Assert.Empty(scorer.Calls);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task RunAsync_ScorerReturnsEmpty_NothingIsMarkedSoItCanBeRetried()
    {
        // Simulates LlmRelevanceScorer giving up after a malformed model response.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Unscoreable"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(_ => []);
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier);

        var summary = await runner.RunAsync();

        Assert.Equal(new RunSummary(0, 0, 0), summary);
        Assert.Empty(notifier.Calls);
        Assert.Empty(datastore.ScoredMessageIds);

        // Still unscored, so a later run would pick it up again.
        var stillUnscored = await datastore.GetUnscoredPostingsAsync();
        Assert.Single(stillUnscored);
    }

    [Fact]
    public async Task RunAsync_TwoMatchesShareApplyUrl_NotifiesOnceButMarksBothScored()
    {
        // The same job reposted under a different message_id, with a slightly different
        // company-name spelling - both parse as separate postings, but they're the same job.
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Backend Engineer", "Acme", "https://example.com/job/123"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Backend Engineer", "Acme Inc.", "https://example.com/job/123"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var summary = await runner.RunAsync();

        // Both count as matched (raw threshold pass), but only one notification goes out.
        Assert.Equal(new RunSummary(Scored: 2, Matched: 2, Notified: 1), summary);
        Assert.Single(notifier.Calls);
        Assert.Single(notifier.Calls[0]);

        // Dedup only affects what gets notified - both postings are still marked scored,
        // so neither is left to be rescored or renotified by a later run.
        Assert.Equal(["id-1", "id-2"], datastore.ScoredMessageIds.OrderBy(id => id));
    }

    [Fact]
    public async Task RunAsync_CalledTwice_SecondPassNotifiesZeroAndDoesNotRescoreFirstRunsPostings()
    {
        var datastore = new FakeDatastore();
        datastore.Seed("id-1", MakePosting("Match One"), [1f, 0f, 0f]);
        datastore.Seed("id-2", MakePosting("Match Two"), [1f, 0f, 0f]);

        var scorer = new FakeRelevanceScorer(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason")).ToList());
        var notifier = new FakeNotifier();
        var runner = CreateRunner(datastore, scorer, notifier, matchThreshold: 70);

        var firstRun = await runner.RunAsync();
        Assert.Equal(new RunSummary(Scored: 2, Matched: 2, Notified: 2), firstRun);
        Assert.Equal(2, notifier.Calls[0].Count);

        var secondRun = await runner.RunAsync();

        Assert.Equal(new RunSummary(0, 0, 0), secondRun);
        // Both postings are already marked scored, so the second run finds nothing
        // unscored and returns before ever calling the scorer or notifier again -
        // nothing from run one gets re-scored or re-notified.
        Assert.Single(scorer.Calls);
        Assert.Single(notifier.Calls);
    }
}
