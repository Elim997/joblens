namespace JobLens.Core.Embedding;

public interface IProfileEmbeddingProvider
{
    Task<float[]> GetProfileEmbeddingAsync(CancellationToken cancellationToken = default);
}
