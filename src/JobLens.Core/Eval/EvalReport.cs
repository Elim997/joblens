namespace JobLens.Core.Eval;

// SelectedTemplate is the trusted, locally-routed template name from ScoredPosting
// (see LlmRelevanceScorer) - null when the posting was never scored (outside the
// shortlist cutoff, or dropped after a malformed model response), since no template's
// group ever produced a result for it.
public record EvalItem(string MessageId, string Title, bool ActualRelevant, bool PredictedRelevant, int Score, string Reasoning, string? SelectedTemplate);

public record EvalReport(double Precision, double Recall, double F1, IReadOnlyList<EvalItem> Items, string Caveat);
