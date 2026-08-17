using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using Microsoft.Extensions.Options;

namespace JobLens.Core.Scoring;

// Caches every configured template's embedding in memory: JobLens:ScoringTemplates is
// static config, so re-embedding it on every /ingest or /run call would waste quota for no
// reason. Batch-embeds all templates in one call rather than one call per template, mirroring
// the old ProfileEmbeddingProvider's caching pattern (which this class replaces).
public class TemplateCatalog(IEmbedder embedder, IOptions<JobLensOptions> options) : ITemplateCatalog
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<ScoringTemplate>? _cached;

    public async Task<IReadOnlyList<ScoringTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
            return cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cachedAfterWait)
                return cachedAfterWait;

            var configured = options.Value.ScoringTemplates;
            if (configured.Count == 0)
            {
                _cached = [];
                return _cached;
            }

            var profiles = configured.Select(t => t.Profile).ToList();
            var embeddings = await embedder.EmbedBatchAsync(profiles, cancellationToken);

            _cached = configured
                .Select((t, i) => new ScoringTemplate(t.Name, t.Profile, embeddings[i]))
                .ToList();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
