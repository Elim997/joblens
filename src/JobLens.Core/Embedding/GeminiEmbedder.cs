using Microsoft.Extensions.AI;

namespace JobLens.Core.Embedding;

public class GeminiEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbedder
{
    // gemini-embedding-001 defaults to 3072 dims, above pgvector's ~2000-dim ANN index
    // ceiling; 1536 (Matryoshka-truncated) keeps most of the model's quality and stays indexable.
    public const int Dimensions = 1536;

    // Gemini's batch embedding endpoint caps requests at 100 texts; larger inputs are
    // chunked into multiple calls rather than one call per text (quota is per-request).
    private const int MaxBatchSize = 100;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vectors = await EmbedBatchAsync([text], cancellationToken);
        return vectors[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var chunk in texts.Chunk(MaxBatchSize))
        {
            var result = await generator.GenerateAsync(
                chunk,
                new EmbeddingGenerationOptions { Dimensions = Dimensions },
                cancellationToken);

            foreach (var embedding in result)
            {
                var vector = embedding.Vector.ToArray();

                // Confirm live rather than trust the request: a provider that ignores
                // output_dimensionality would otherwise silently corrupt the vector(N) schema.
                if (vector.Length != Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Gemini returned a {vector.Length}-dim embedding; expected {Dimensions}. " +
                        "output_dimensionality may not be honored for this model/endpoint.");
                }

                vectors.Add(vector);
            }
        }

        return vectors;
    }
}
