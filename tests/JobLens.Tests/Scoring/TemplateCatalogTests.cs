using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Scoring;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Scoring;

public class TemplateCatalogTests
{
    [Fact]
    public async Task GetTemplatesAsync_BatchEmbedsAllProfilesOnceAndPreservesOrder()
    {
        var embedder = new RecordingEmbedder(
            [
                [1f, 0f],
                [0f, 1f],
                [0.5f, 0.5f],
            ]);
        var configured = new List<ScoringTemplateOptions>
        {
            new() { Name = "QA", Profile = "qa profile" },
            new() { Name = "Backend", Profile = "backend profile" },
            new() { Name = "Full Stack", Profile = "full-stack profile" },
        };
        var catalog = CreateCatalog(embedder, configured);

        var templates = await catalog.GetTemplatesAsync();

        var request = Assert.Single(embedder.BatchRequests);
        Assert.Equal(configured.Select(t => t.Profile), request);
        Assert.Equal(configured.Select(t => t.Name), templates.Select(t => t.Name));
        Assert.Equal(configured.Select(t => t.Profile), templates.Select(t => t.Profile));
        Assert.Equal([1f, 0f], templates[0].Embedding);
        Assert.Equal([0f, 1f], templates[1].Embedding);
        Assert.Equal([0.5f, 0.5f], templates[2].Embedding);
        Assert.Equal(0, embedder.SingleCallCount);
    }

    [Fact]
    public async Task GetTemplatesAsync_RepeatedCallsReuseCachedTemplates()
    {
        var embedder = new RecordingEmbedder([[1f, 0f]]);
        var catalog = CreateCatalog(
            embedder,
            [new ScoringTemplateOptions { Name = "Backend", Profile = "backend profile" }]);

        var first = await catalog.GetTemplatesAsync();
        var second = await catalog.GetTemplatesAsync();

        Assert.Same(first, second);
        Assert.Single(embedder.BatchRequests);
    }

    [Fact]
    public async Task GetTemplatesAsync_ConcurrentInitializationEmbedsOnlyOnce()
    {
        var embedder = new BlockingEmbedder([[1f, 0f]]);
        var catalog = CreateCatalog(
            embedder,
            [new ScoringTemplateOptions { Name = "Backend", Profile = "backend profile" }]);

        var first = catalog.GetTemplatesAsync();
        await embedder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = catalog.GetTemplatesAsync();
        embedder.Release.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal(1, embedder.BatchCallCount);
    }

    [Fact]
    public async Task GetTemplatesAsync_NoConfiguredTemplates_DoesNotCallEmbedder()
    {
        var embedder = new RecordingEmbedder([]);
        var catalog = CreateCatalog(embedder, []);

        var templates = await catalog.GetTemplatesAsync();

        Assert.Empty(templates);
        Assert.Empty(embedder.BatchRequests);
        Assert.Equal(0, embedder.SingleCallCount);
    }

    private static TemplateCatalog CreateCatalog(
        IEmbedder embedder,
        List<ScoringTemplateOptions> templates) =>
        new(embedder, Options.Create(new JobLensOptions { ScoringTemplates = templates }));

    private class RecordingEmbedder(IReadOnlyList<float[]> responses) : IEmbedder
    {
        public int SingleCallCount { get; private set; }
        public List<IReadOnlyList<string>> BatchRequests { get; } = [];

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            SingleCallCount++;
            throw new InvalidOperationException("TemplateCatalog must use batch embedding.");
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchRequests.Add(texts.ToList());
            return Task.FromResult(responses);
        }
    }

    private sealed class BlockingEmbedder(IReadOnlyList<float[]> responses) : IEmbedder
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BatchCallCount { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TemplateCatalog must use batch embedding.");

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return responses;
        }
    }
}
