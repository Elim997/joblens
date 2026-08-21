using System.Reflection;
using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Feed;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Pipeline;

// F3 extracted POST /ingest's handler body into IngestService unchanged except for dropping
// the convenience-scoring branch. These tests pin the behavior that had to survive the move -
// the dedupe skip, category filtering, one batched embed call, embedding-failure semantics,
// and the exact counters - plus the one intended change: ingest never scores.
public class IngestServiceTests
{
    // The counter formula the pre-F3 handler used, exercised end to end against a feed that
    // hits every branch at once: one unparseable message, one off-category posting, one
    // already-stored posting, and two genuinely new ones.
    //   fetched      = messages returned by the feed          = 5
    //   parsed       = messages the parser understood         = 4 (the promo is skipped)
    //   filteredOut  = parsed - on-target                     = 4 - 3 = 1 (Hardware)
    //   alreadyStored= on-target - not-yet-stored             = 3 - 2 = 1
    //   newlyStored  = not-yet-stored                         = 2
    [Fact]
    public async Task IngestAsync_MixedFeed_ReportsTheSameCountersTheHandlerDid()
    {
        var messages = new[]
        {
            Message("promo"),
            Message("software-stored"),
            Message("software-new"),
            Message("qa-new"),
            Message("hardware"),
        };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["promo"] = null,
            ["software-stored"] = Posting("Backend Engineer", "Software"),
            ["software-new"] = Posting("QA Automation Engineer", "Software"),
            ["qa-new"] = Posting("Test Engineer", "QA"),
            ["hardware"] = Posting("FPGA Engineer", "Hardware"),
        });
        var datastore = new RecordingDatastore(existingIds: ["software-stored"]);
        var embedder = new RecordingEmbedder();

        var summary = await CreateService(messages, parser, embedder, datastore).IngestAsync();

        Assert.Equal(new IngestSummary(Fetched: 5, Parsed: 4, FilteredOut: 1, AlreadyStored: 1, NewlyStored: 2), summary);
    }

    // The already-stored skip is what keeps a repeated ingest free: embedding is the
    // quota-limited step, so a message id already in pgvector must never reach the embedder.
    [Fact]
    public async Task IngestAsync_AlreadyStoredPosting_IsNeitherEmbeddedNorUpserted()
    {
        var messages = new[] { Message("stored"), Message("fresh") };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["stored"] = Posting("Backend Engineer", "Software"),
            ["fresh"] = Posting("QA Automation Engineer", "Software"),
        });
        var datastore = new RecordingDatastore(existingIds: ["stored"]);
        var embedder = new RecordingEmbedder();

        await CreateService(messages, parser, embedder, datastore).IngestAsync();

        Assert.Equal(["fresh"], datastore.Upserted.Select(u => u.MessageId));
        var batch = Assert.Single(embedder.Batches);
        Assert.Equal(
            [JobPostingTextNormalizer.ToEmbeddingText(Posting("QA Automation Engineer", "Software"))],
            batch);
    }

    // One EmbedBatchAsync call for the whole run, not one per posting, and each posting is
    // upserted against its own message id and its own embedding - the pairing that a
    // JobPosting-only category filter would have thrown away.
    [Fact]
    public async Task IngestAsync_EmbedsNewPostingsInOneBatch_AndUpsertsEachWithItsOwnEmbedding()
    {
        var messages = new[] { Message("a"), Message("b"), Message("c") };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["a"] = Posting("A Engineer", "Software"),
            ["b"] = Posting("B Engineer", "Software"),
            ["c"] = Posting("C Engineer", "Software"),
        });
        var datastore = new RecordingDatastore(existingIds: []);
        var embedder = new RecordingEmbedder();

        await CreateService(messages, parser, embedder, datastore).IngestAsync();

        Assert.Equal(1, embedder.BatchCalls);
        Assert.Equal(0, embedder.SingleCalls);
        Assert.Equal(3, Assert.Single(embedder.Batches).Count);

        Assert.Equal(["a", "b", "c"], datastore.Upserted.Select(u => u.MessageId));
        Assert.Equal(["A Engineer", "B Engineer", "C Engineer"], datastore.Upserted.Select(u => u.Posting.Title));
        // RecordingEmbedder stamps each vector with its index in the batch, so a shifted
        // posting/embedding pairing would show up here rather than silently storing the
        // wrong vector for the wrong posting.
        Assert.Equal([0f, 1f, 2f], datastore.Upserted.Select(u => u.Embedding[0]));
        // The live dimension of the first embedding sizes the schema.
        Assert.Equal([3], datastore.EnsureSchemaDimensions);
    }

    // The bot's Category casing is its own; matching stays case-insensitive, as the shared
    // CategoryFilter rule has always been.
    [Fact]
    public async Task IngestAsync_MatchesTargetCategoriesCaseInsensitively()
    {
        var messages = new[] { Message("m") };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["m"] = Posting("QA Automation Engineer", "SOFTWARE"),
        });
        var datastore = new RecordingDatastore(existingIds: []);

        var summary = await CreateService(messages, parser, new RecordingEmbedder(), datastore).IngestAsync();

        Assert.Equal(0, summary.FilteredOut);
        Assert.Equal(1, summary.NewlyStored);
    }

    // Nothing new to store means nothing to embed and no schema call - the handler's
    // toEmbed.Count > 0 guard, preserved.
    [Fact]
    public async Task IngestAsync_WhenNothingIsNew_SkipsTheEmbedderAndTheSchemaCall()
    {
        var messages = new[] { Message("stored") };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["stored"] = Posting("Backend Engineer", "Software"),
        });
        var datastore = new RecordingDatastore(existingIds: ["stored"]);
        var embedder = new RecordingEmbedder();

        var summary = await CreateService(messages, parser, embedder, datastore).IngestAsync();

        Assert.Equal(new IngestSummary(Fetched: 1, Parsed: 1, FilteredOut: 0, AlreadyStored: 1, NewlyStored: 0), summary);
        Assert.Equal(0, embedder.BatchCalls);
        Assert.Empty(datastore.EnsureSchemaDimensions);
        Assert.Empty(datastore.Upserted);
    }

    // Error semantics are unchanged from the handler: an embedding failure fails the whole
    // ingest step rather than being swallowed into a partial summary, and nothing is stored.
    [Fact]
    public async Task IngestAsync_WhenEmbeddingFails_PropagatesAndStoresNothing()
    {
        var messages = new[] { Message("m") };
        var parser = new RecordingParser(new Dictionary<string, JobPosting?>
        {
            ["m"] = Posting("QA Automation Engineer", "Software"),
        });
        var datastore = new RecordingDatastore(existingIds: []);
        var embedder = new RecordingEmbedder(new InvalidOperationException("Simulated embedding failure."));
        var service = CreateService(messages, parser, embedder, datastore);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync());

        Assert.Equal("Simulated embedding failure.", ex.Message);
        Assert.Empty(datastore.Upserted);
        Assert.Empty(datastore.EnsureSchemaDimensions);
    }

    // The durable structural guard for Option A. IngestService cannot call the scorer if it
    // was never handed one, so this asserts on the constructor itself: the moment someone
    // reintroduces scoring here, this fails before any behavioral test would have to notice.
    // The behavioral half lives in RunLockEndpointTests, where a recording scorer really is
    // registered in DI and POST /ingest must leave it at zero calls.
    [Fact]
    public void IngestService_DoesNotTakeARelevanceScorerDependency()
    {
        var parameterTypes = typeof(IngestService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(IRelevanceScorer), parameterTypes);
    }

    private static IngestService CreateService(
        IReadOnlyList<RawMessage> messages,
        RecordingParser parser,
        RecordingEmbedder embedder,
        RecordingDatastore datastore) =>
        new(
            new RecordingFeedSource(messages),
            parser,
            embedder,
            datastore,
            Options.Create(new JobLensOptions { TargetCategories = ["Software", "QA"] }));

    private static RawMessage Message(string id) =>
        new(id, "fake@g.us", "sender", $"content for {id}", DateTimeOffset.UnixEpoch);

    private static JobPosting Posting(string title, string category) =>
        new(title, "Acme", "Remote", category, "https://example.test/jobs/1", $"Description for {title}.");

    private sealed class RecordingFeedSource(IReadOnlyList<RawMessage> messages) : IJobFeedSource
    {
        public Task<IReadOnlyList<RawMessage>> GetMessagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(messages);
    }

    // Keyed by message id so each test states, per message, exactly what the parser makes of
    // it - including null for the messages that fail the job-bot structure check.
    private sealed class RecordingParser(IReadOnlyDictionary<string, JobPosting?> postingsByMessageId) : IPostingParser
    {
        public JobPosting? Parse(RawMessage message) => postingsByMessageId[message.Id];
    }

    private sealed class RecordingEmbedder(Exception? batchException = null) : IEmbedder
    {
        public int SingleCalls { get; private set; }
        public int BatchCalls { get; private set; }
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            SingleCalls++;
            return Task.FromResult(new[] { 0f, 0f, 0f });
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            Batches.Add(texts);
            if (batchException is not null)
                throw batchException;

            // Index-stamped so a mispaired posting/embedding upsert is visible in assertions.
            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select((_, i) => new[] { i, 0f, 0f }).ToList());
        }
    }

    private sealed class RecordingDatastore(IReadOnlyList<string> existingIds) : IDatastore
    {
        private readonly IReadOnlySet<string> existing = new HashSet<string>(existingIds);

        public List<int> EnsureSchemaDimensions { get; } = [];
        public List<(string MessageId, JobPosting Posting, float[] Embedding)> Upserted { get; } = [];

        public Task EnsureSchemaAsync(int dimension, CancellationToken cancellationToken = default)
        {
            EnsureSchemaDimensions.Add(dimension);
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> GetExistingMessageIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(existing);

        public Task UpsertAsync(
            string messageId, JobPosting posting, float[] embedding, CancellationToken cancellationToken = default)
        {
            Upserted.Add((messageId, posting, embedding));
            return Task.CompletedTask;
        }

        public Task<ScoredPostingSnapshot?> GetScoredPostingByMessageIdAsync(
            string messageId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by IngestService.");

        public Task<IReadOnlyList<SimilarPosting>> QuerySimilarAsync(
            float[] queryEmbedding, int topK, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by IngestService.");

        public Task<IReadOnlyList<UnscoredPosting>> GetUnscoredPostingsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by IngestService.");

        // Ingest must never mark anything scored - that would drop the posting out of the
        // backlog before /run ever sees it, so it could never be notified or drafted.
        public Task MarkScoredAsync(
            IReadOnlyList<ScoredMark> scored, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("IngestService must never mark postings scored.");

        public Task<IReadOnlyList<StoredMatch>> GetMatchesAsync(
            int matchThreshold, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by IngestService.");
    }
}
