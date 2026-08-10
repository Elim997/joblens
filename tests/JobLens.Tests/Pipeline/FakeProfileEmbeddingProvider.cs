using JobLens.Core.Embedding;

namespace JobLens.Tests.Pipeline;

public class FakeProfileEmbeddingProvider(float[] embedding) : IProfileEmbeddingProvider
{
    public Task<float[]> GetProfileEmbeddingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(embedding);
}
