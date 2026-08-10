namespace JobLens.Core.Eval;

public record EvalItem(string MessageId, string Title, bool ActualRelevant, bool PredictedRelevant, int Score, string Reasoning);

public record EvalReport(double Precision, double Recall, double F1, IReadOnlyList<EvalItem> Items, string Caveat);
