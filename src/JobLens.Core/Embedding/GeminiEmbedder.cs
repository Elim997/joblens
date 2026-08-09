using Microsoft.Extensions.AI;

namespace JobLens.Core.Embedding;

public class GeminiEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbedder
{
    // gemini-embedding-001 defaults to 3072 dims, above pgvector's ~2000-dim ANN index
    // ceiling; 1536 (Matryoshka-truncated) keeps most of the model's quality and stays indexable.
    public const int Dimensions = 1536;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await generator.GenerateAsync(
            [text],
            new EmbeddingGenerationOptions { Dimensions = Dimensions },
            cancellationToken);

        var vector = result[0].Vector.ToArray();

        // Confirm live rather than trust the request: a provider that ignores
        // output_dimensionality would otherwise silently corrupt the vector(N) schema.
        if (vector.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Gemini returned a {vector.Length}-dim embedding; expected {Dimensions}. " +
                "output_dimensionality may not be honored for this model/endpoint.");
        }

        return vector;
    }
}
