using JobLens.Core.Embedding;

namespace JobLens.Tests.Eval;

// EvalHarnessTests only cares about the scores FakeRelevanceScorer hands back, not real
// cosine similarity, so every text embeds to the same fixed vector.
public class FakeEmbedder : IEmbedder
{
    private static readonly float[] Vector = [1f, 0f, 0f];

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(Vector);

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => Vector).ToList());
}
