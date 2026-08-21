using System.Net;
using System.Text.Json;
using JobLens.Core.Embedding;
using JobLens.Core.Feed;
using JobLens.Core.Notification;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobLens.Tests;

public class RunLockEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ConflictBody =
        "{\"error\":\"Another JobLens pipeline run is already in progress.\"}";

    private readonly WebApplicationFactory<Program> _factory;

    public RunLockEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Ingest_WhenLockIsHeld_ReturnsExactConflictWithoutPipelineWork()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        using var holder = runLock.TryAcquire();
        Assert.NotNull(holder);

        var feedSource = new RecordingFeedSource([]);
        var parser = new RecordingParser(MakePosting());
        var embedder = new RecordingEmbedder();
        var datastore = new EndpointDatastore();
        var scorer = new RecordingScorer();
        var notifier = new RecordingNotifier();
        using var factory = CreateFactory(
            runLock,
            feedSource,
            parser,
            embedder,
            datastore,
            scorer,
            notifier);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/ingest", content: null);

        await AssertExactConflictAsync(response);
        Assert.Equal(0, feedSource.Calls);
        Assert.Equal(0, parser.Calls);
        Assert.Equal(0, embedder.BatchCalls);
        Assert.Equal(0, datastore.TotalCalls);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(0, notifier.Calls);
    }

    // Post-F3 shape: /ingest reports ingest counters only. The RecordingScorer is still
    // registered in DI, so scorer.Calls == 0 here is a real end-to-end guard that the
    // convenience-scoring branch is gone, not just that the field was dropped from the JSON.
    [Fact]
    public async Task Ingest_WhenUnlocked_ReturnsIngestCountersOnlyAndNeverScores()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        var posting = MakePosting();
        var feedSource = new RecordingFeedSource(
        [
            new RawMessage(
                "message-1",
                "fake@g.us",
                "sender",
                "Controlled by the test parser.",
                DateTimeOffset.UtcNow),
        ]);
        var parser = new RecordingParser(posting);
        var embedder = new RecordingEmbedder();
        var datastore = new EndpointDatastore();
        var scorer = new RecordingScorer();
        var notifier = new RecordingNotifier();
        using var factory = CreateFactory(
            runLock,
            feedSource,
            parser,
            embedder,
            datastore,
            scorer,
            notifier);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/ingest", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            ["fetched", "parsed", "filteredOut", "alreadyStored", "newlyStored"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("fetched").GetInt32());
        Assert.Equal(1, root.GetProperty("parsed").GetInt32());
        Assert.Equal(0, root.GetProperty("filteredOut").GetInt32());
        Assert.Equal(0, root.GetProperty("alreadyStored").GetInt32());
        Assert.Equal(1, root.GetProperty("newlyStored").GetInt32());

        Assert.Equal(1, feedSource.Calls);
        Assert.Equal(1, parser.Calls);
        Assert.Equal(1, embedder.BatchCalls);
        Assert.Equal(1, datastore.GetExistingMessageIdsCalls);
        Assert.Equal(1, datastore.EnsureSchemaCalls);
        Assert.Equal(1, datastore.UpsertCalls);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task Run_WhenLockIsHeld_ReturnsExactConflictWithoutPipelineWork()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        using var holder = runLock.TryAcquire();
        Assert.NotNull(holder);

        var feedSource = new RecordingFeedSource([]);
        var parser = new RecordingParser(MakePosting());
        var embedder = new RecordingEmbedder();
        var datastore = new EndpointDatastore();
        var scorer = new RecordingScorer();
        var notifier = new RecordingNotifier();
        using var factory = CreateFactory(
            runLock,
            feedSource,
            parser,
            embedder,
            datastore,
            scorer,
            notifier);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/run", content: null);

        await AssertExactConflictAsync(response);
        Assert.Equal(0, feedSource.Calls);
        Assert.Equal(0, parser.Calls);
        Assert.Equal(0, embedder.BatchCalls);
        Assert.Equal(0, datastore.TotalCalls);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task Run_WhenUnlocked_PreservesExistingEmptyBacklogResponse()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        var feedSource = new RecordingFeedSource([]);
        var parser = new RecordingParser(MakePosting());
        var embedder = new RecordingEmbedder();
        var datastore = new EndpointDatastore();
        var scorer = new RecordingScorer();
        var notifier = new RecordingNotifier();
        using var factory = CreateFactory(
            runLock,
            feedSource,
            parser,
            embedder,
            datastore,
            scorer,
            notifier);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/run", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            [
                "batches",
                "scored",
                "matched",
                "notified",
                "draftsCreated",
                "draftsReused",
                "tailoringFailures",
                "stoppedEarly",
                "stopReason",
            ],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(0, root.GetProperty("batches").GetInt32());
        Assert.Equal(0, root.GetProperty("scored").GetInt32());
        Assert.Equal(0, root.GetProperty("matched").GetInt32());
        Assert.Equal(0, root.GetProperty("notified").GetInt32());
        Assert.Equal(0, root.GetProperty("draftsCreated").GetInt32());
        Assert.Equal(0, root.GetProperty("draftsReused").GetInt32());
        Assert.Equal(0, root.GetProperty("tailoringFailures").GetInt32());
        Assert.False(root.GetProperty("stoppedEarly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("stopReason").ValueKind);

        Assert.Equal(1, datastore.GetUnscoredPostingsCalls);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task Ingest_WhenHandlerThrows_ReleasesLock()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        var feedSource = new RecordingFeedSource(
            [],
            new InvalidOperationException("Simulated feed failure."));
        var parser = new RecordingParser(MakePosting());
        var embedder = new RecordingEmbedder();
        var datastore = new EndpointDatastore();
        var scorer = new RecordingScorer();
        var notifier = new RecordingNotifier();
        using var factory = CreateFactory(
            runLock,
            feedSource,
            parser,
            embedder,
            datastore,
            scorer,
            notifier);
        using var client = factory.CreateClient();

        HttpResponseMessage? response = null;
        Exception? requestException = null;
        try
        {
            response = await client.PostAsync("/ingest", content: null);
        }
        catch (Exception ex)
        {
            requestException = ex;
        }

        if (response is not null)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            response.Dispose();
        }
        else
        {
            Assert.NotNull(requestException);
        }

        Assert.Equal(1, feedSource.Calls);
        using var reacquired = runLock.TryAcquire();
        Assert.NotNull(reacquired);
    }

    private WebApplicationFactory<Program> CreateFactory(
        RunLock runLock,
        RecordingFeedSource feedSource,
        RecordingParser parser,
        RecordingEmbedder embedder,
        EndpointDatastore datastore,
        RecordingScorer scorer,
        RecordingNotifier notifier) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "C:/fake/messages.db",
                    ["JobLens:GroupChatJids:0"] = "fake@g.us",
                    ["JobLens:TargetCategories:0"] = "Software",
                    ["JobLens:ScoringTemplates:0:Name"] = "Test template",
                    ["JobLens:ScoringTemplates:0:Profile"] = "Fake test profile",
                    ["Postgres:ConnectionString"] = "Host=fake;Database=fake;Username=fake;Password=fake",
                    ["Gemini:ApiKey"] = "fake-gemini-key",
                    ["Llm:BaseUrl"] = "http://localhost:20128/v1",
                    ["Llm:ApiKey"] = "fake-llm-key",
                    ["Llm:ScoringModel"] = "coding-fallback",
                    ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<RunLock>();
                services.AddSingleton(runLock);
                services.RemoveAll<IJobFeedSource>();
                services.AddSingleton<IJobFeedSource>(feedSource);
                services.RemoveAll<IPostingParser>();
                services.AddSingleton<IPostingParser>(parser);
                services.RemoveAll<IEmbedder>();
                services.AddSingleton<IEmbedder>(embedder);
                services.RemoveAll<IDatastore>();
                services.AddSingleton<IDatastore>(datastore);
                services.RemoveAll<IRelevanceScorer>();
                services.AddSingleton<IRelevanceScorer>(scorer);
                services.RemoveAll<INotifier>();
                services.AddSingleton<INotifier>(notifier);
            });
        });

    private static async Task AssertExactConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ConflictBody, await response.Content.ReadAsStringAsync());
    }

    private static JobPosting MakePosting() =>
        new(
            "QA Automation Engineer",
            "Acme",
            "Remote",
            "Software",
            "https://example.test/jobs/1",
            "Build and maintain automated tests.");

    private sealed class RecordingFeedSource(
        IReadOnlyList<RawMessage> messages,
        Exception? exception = null) : IJobFeedSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RawMessage>> GetMessagesAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (exception is not null)
                throw exception;

            return Task.FromResult(messages);
        }
    }

    private sealed class RecordingParser(JobPosting posting) : IPostingParser
    {
        public int Calls { get; private set; }

        public JobPosting? Parse(RawMessage message)
        {
            Calls++;
            return posting;
        }
    }

    private sealed class RecordingEmbedder : IEmbedder
    {
        public int SingleCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<float[]> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            SingleCalls++;
            return Task.FromResult(new[] { 1f, 0f, 0f });
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => new[] { 1f, 0f, 0f }).ToList());
        }
    }

    private sealed class RecordingScorer : IRelevanceScorer
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ScoredPosting>> ScoreAsync(
            IReadOnlyList<(string Id, JobPosting Posting, float[] Embedding)> candidates,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ScoredPosting>>(
                candidates.Select(candidate => new ScoredPosting(
                    candidate.Id,
                    candidate.Posting,
                    91,
                    "Strong match.",
                    "Test template")).ToList());
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public int Calls { get; private set; }

        public Task NotifyAsync(
            IReadOnlyList<ScoredPosting> matches,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class EndpointDatastore : IDatastore
    {
        public int EnsureSchemaCalls { get; private set; }
        public int GetExistingMessageIdsCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public int GetUnscoredPostingsCalls { get; private set; }
        public int MarkScoredCalls { get; private set; }

        public int TotalCalls =>
            EnsureSchemaCalls +
            GetExistingMessageIdsCalls +
            UpsertCalls +
            GetUnscoredPostingsCalls +
            MarkScoredCalls;

        public Task EnsureSchemaAsync(
            int dimension,
            CancellationToken cancellationToken = default)
        {
            EnsureSchemaCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(
            CancellationToken cancellationToken = default)
        {
            GetExistingMessageIdsCalls++;
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        }

        public Task UpsertAsync(
            string messageId,
            JobPosting posting,
            float[] embedding,
            CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            return Task.CompletedTask;
        }

        public Task<ScoredPostingSnapshot?> GetScoredPostingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these endpoint tests.");

        public Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(
            float[] queryEmbedding,
            int topK,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these endpoint tests.");

        public Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(
            CancellationToken cancellationToken = default)
        {
            GetUnscoredPostingsCalls++;
            return Task.FromResult<IReadOnlyList<UnscoredPosting>>([]);
        }

        public Task MarkScoredAsync(
            IReadOnlyList<ScoredMark> scored,
            CancellationToken cancellationToken = default)
        {
            MarkScoredCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(
            int matchThreshold,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these endpoint tests.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"joblens-run-lock-endpoint-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string LockFilePath => Path.Combine(RootPath, "run.lock");

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
