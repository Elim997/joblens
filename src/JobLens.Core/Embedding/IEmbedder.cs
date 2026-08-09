namespace JobLens.Core.Embedding;

public interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds many texts in as few provider calls as possible (chunked internally to
    /// the provider's per-request limit). Use this over repeated EmbedAsync calls for
    /// bulk work - each EmbedAsync call is a separate quota-metered request.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
