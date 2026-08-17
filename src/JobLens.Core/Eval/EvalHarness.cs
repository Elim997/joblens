using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Scoring;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Eval;

/// <summary>
/// Runs every labeled posting through the same embed -> local template routing/cosine
/// prefilter -> OmniRoute score path PipelineRunner uses, thresholds at MatchThreshold, and
/// compares the
/// prediction to the human-labeled IsRelevant to compute precision/recall/F1. This is
/// the only thing that tells us whether the scorer is actually good, not just running.
/// </summary>
public class EvalHarness(
    IEmbedder embedder,
    IRelevanceScorer scorer,
    IOptions<JobLensOptions> options)
{
    public async Task<EvalReport> RunAsync(LabeledPostingSet labeledSet, CancellationToken cancellationToken = default)
    {
        if (labeledSet.Postings.Count == 0)
            return new EvalReport(0, 0, 0, [], labeledSet.Caveat);

        var texts = labeledSet.Postings.Select(l => JobPostingTextNormalizer.ToEmbeddingText(l.Posting)).ToList();
        var embeddings = await embedder.EmbedBatchAsync(texts, cancellationToken);

        var candidates = labeledSet.Postings
            .Select((l, i) => (l.MessageId, l.Posting, embeddings[i]))
            .ToList();
        var scored = await scorer.ScoreAsync(candidates, cancellationToken);
        var scoredById = scored.ToDictionary(s => s.Id);

        var threshold = options.Value.MatchThreshold;
        var items = labeledSet.Postings.Select(l =>
        {
            // A posting beyond the scorer's shortlist cutoff, or dropped after a
            // malformed model response, was never actually scored - count it as
            // predicted-not-relevant instead of crashing or silently omitting it.
            if (scoredById.TryGetValue(l.MessageId, out var s))
                return new EvalItem(l.MessageId, l.Posting.Title, l.IsRelevant, s.Score >= threshold, s.Score, s.Reasoning, s.TemplateName);

            return new EvalItem(l.MessageId, l.Posting.Title, l.IsRelevant, false, 0,
                "Not scored - outside the shortlist cutoff or dropped by the model.", null);
        }).ToList();

        var truePositives = items.Count(i => i.ActualRelevant && i.PredictedRelevant);
        var falsePositives = items.Count(i => !i.ActualRelevant && i.PredictedRelevant);
        var falseNegatives = items.Count(i => i.ActualRelevant && !i.PredictedRelevant);

        var precision = truePositives + falsePositives == 0 ? 0 : (double)truePositives / (truePositives + falsePositives);
        var recall = truePositives + falseNegatives == 0 ? 0 : (double)truePositives / (truePositives + falseNegatives);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new EvalReport(precision, recall, f1, items, labeledSet.Caveat);
    }
}
