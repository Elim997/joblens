using JobLens.Core.Configuration;
using JobLens.Core.Eval;
using JobLens.Core.Parsing;
using JobLens.Core.Scoring;
using JobLens.Tests.Pipeline;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Eval;

// Hermetic: a small SYNTHETIC labeled set with known answers, not the real 20 in
// Eval/labeled-postings.json (those are placeholder-labeled and would prove nothing
// about the precision/recall/F1 math itself).
public class EvalHarnessTests
{
    private const string TestCaveat = "synthetic set for math verification, not the real 20";

    private static JobPosting MakePosting(string title) =>
        new(title, "Acme", "Tel Aviv", "Software", $"https://example.com/{title}", "- test requirement");

    private static LabeledPostingSet MakeLabeledSet(params (string Id, bool IsRelevant)[] entries) =>
        new(TestCaveat, entries.Select(e => new LabeledPosting(e.Id, MakePosting(e.Id), e.IsRelevant)).ToList());

    private static EvalHarness CreateHarness(
        Func<IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)>, IReadOnlyList<ScoredPosting>> respond,
        int matchThreshold = 70) =>
        new(new FakeEmbedder(), new FakeRelevanceScorer(respond), new FakeProfileEmbeddingProvider([1f, 0f, 0f]),
            Options.Create(new JobLensOptions { Profile = "test profile", MatchThreshold = matchThreshold }));

    [Fact]
    public async Task RunAsync_MixOfTruePositiveFalsePositiveFalseNegativeTrueNegative_ComputesKnownPrecisionRecallF1()
    {
        var labeled = MakeLabeledSet(
            ("true-positive", true),
            ("false-negative", true),
            ("false-positive", false),
            ("true-negative", false));

        var scores = new Dictionary<string, int>
        {
            ["true-positive"] = 90,
            ["false-negative"] = 40,
            ["false-positive"] = 80,
            ["true-negative"] = 20,
        };

        var harness = CreateHarness(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, scores[c.Id], "reason")).ToList());

        var report = await harness.RunAsync(labeled);

        // TP=1, FP=1, FN=1 -> precision = 1/2, recall = 1/2, F1 = 2*.5*.5/(.5+.5) = .5
        Assert.Equal(0.5, report.Precision);
        Assert.Equal(0.5, report.Recall);
        Assert.Equal(0.5, report.F1);
        Assert.Equal(4, report.Items.Count);
        Assert.Equal(TestCaveat, report.Caveat);
    }

    [Fact]
    public async Task RunAsync_AllPredictionsCorrect_PerfectScore()
    {
        var labeled = MakeLabeledSet(("relevant", true), ("irrelevant", false));

        var harness = CreateHarness(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, c.Id == "relevant" ? 100 : 0, "reason")).ToList());

        var report = await harness.RunAsync(labeled);

        Assert.Equal(1.0, report.Precision);
        Assert.Equal(1.0, report.Recall);
        Assert.Equal(1.0, report.F1);
    }

    [Fact]
    public async Task RunAsync_ScorerDropsAPosting_TreatedAsPredictedNotRelevant()
    {
        // Simulates a posting outside the scorer's shortlist cutoff, or one the model's
        // response failed to validate - GeminiRelevanceScorer never throws for either,
        // it just omits the id from its result.
        var labeled = MakeLabeledSet(("scored", true), ("dropped", true));

        var harness = CreateHarness(candidates =>
            candidates.Where(c => c.Id == "scored").Select(c => new ScoredPosting(c.Id, c.Posting, 90, "reason")).ToList());

        var report = await harness.RunAsync(labeled);

        var droppedItem = report.Items.Single(i => i.MessageId == "dropped");
        Assert.False(droppedItem.PredictedRelevant);
        Assert.Equal(0, droppedItem.Score);
        Assert.Contains("Not scored", droppedItem.Reasoning);

        // 1 true positive ("scored"), 1 false negative ("dropped"): no false positives,
        // so precision is untouched but recall is dragged down to 1/2.
        Assert.Equal(1.0, report.Precision);
        Assert.Equal(0.5, report.Recall);
    }

    [Fact]
    public async Task RunAsync_NoPositivesPredictedOrActual_PrecisionAndRecallAreZeroNotNaN()
    {
        var labeled = MakeLabeledSet(("a", false), ("b", false));

        var harness = CreateHarness(candidates =>
            candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 10, "reason")).ToList());

        var report = await harness.RunAsync(labeled);

        Assert.Equal(0, report.Precision);
        Assert.Equal(0, report.Recall);
        Assert.Equal(0, report.F1);
    }

    [Fact]
    public async Task RunAsync_EmptyLabeledSet_ReturnsZeroedReportWithoutCallingScorer()
    {
        var labeled = new LabeledPostingSet(TestCaveat, []);
        var scorerCalled = false;
        var harness = CreateHarness(_ => { scorerCalled = true; return []; });

        var report = await harness.RunAsync(labeled);

        Assert.Equal(0, report.Precision);
        Assert.Equal(0, report.Recall);
        Assert.Equal(0, report.F1);
        Assert.Empty(report.Items);
        Assert.False(scorerCalled);
    }

    [Fact]
    public async Task RunAsync_ScoreExactlyAtThreshold_CountsAsPredictedRelevant()
    {
        var labeled = MakeLabeledSet(("at-threshold", true));

        var harness = CreateHarness(
            candidates => candidates.Select(c => new ScoredPosting(c.Id, c.Posting, 70, "reason")).ToList(),
            matchThreshold: 70);

        var report = await harness.RunAsync(labeled);

        Assert.True(report.Items.Single().PredictedRelevant);
    }
}
