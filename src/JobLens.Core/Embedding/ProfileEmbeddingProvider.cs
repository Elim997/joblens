using JobLens.Core.Configuration;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Embedding;

// Caches the profile embedding in memory: JobLens:Profile is static config, so
// re-embedding it on every /ingest call would waste quota for no reason.
public class ProfileEmbeddingProvider(IEmbedder embedder, IOptions<JobLensOptions> options) : IProfileEmbeddingProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private float[]? _cached;

    public async Task<float[]> GetProfileEmbeddingAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
            return cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _cached ??= await embedder.EmbedAsync(options.Value.Profile, cancellationToken);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
