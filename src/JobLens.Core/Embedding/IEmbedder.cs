namespace JobLens.Core.Embedding;

public interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
