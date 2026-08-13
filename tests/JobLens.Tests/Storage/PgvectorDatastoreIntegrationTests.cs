using System.ClientModel;
using JobLens.Core.Embedding;
using JobLens.Core.Parsing;
using JobLens.Core.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Npgsql;
using OpenAI;

namespace JobLens.Tests.Storage;

// Needs a real Postgres+pgvector instance (SETUP.md step 3) and Gemini quota (step 5):
// exercises the live embedding-dimension confirmation and cosine ranking end to end,
// against real infrastructure rather than mocks.
[Trait("Category", "Integration")]
public class PgvectorDatastoreIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _testMessageIds = [];
    private NpgsqlDataSource _dataSource = null!;
    private PgvectorDatastore _datastore = null!;
    private GeminiEmbedder _embedder = null!;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var pgConn = config["Postgres:ConnectionString"]
            ?? throw new InvalidOperationException("Missing Postgres:ConnectionString - run SETUP.md steps 3 and 5.");
        var geminiKey = config["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Missing Gemini:ApiKey - run SETUP.md step 5.");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(pgConn);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
        _datastore = new PgvectorDatastore(_dataSource);

        var gemini = new OpenAIClient(
            new ApiKeyCredential(geminiKey),
            new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") });
        _embedder = new GeminiEmbedder(gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator());

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_testMessageIds.Count > 0)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM job_postings WHERE message_id = ANY(@ids);";
            command.Parameters.AddWithValue("ids", _testMessageIds.ToArray());
            await command.ExecuteNonQueryAsync();
        }

        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task EmbedUpsertAndQuery_RanksSemanticNeighborAboveUnrelatedPosting()
    {
        var backendId = $"test-milestone4-backend-{Guid.NewGuid():N}";
        var qaId = $"test-milestone4-qa-{Guid.NewGuid():N}";
        _testMessageIds.Add(backendId);
        _testMessageIds.Add(qaId);

        var backendPosting = new JobPosting(
            "Senior Backend Engineer", "Acme", "Tel Aviv", "Software",
            "https://example.com/backend", "- 5+ years C# and distributed systems");
        var qaPosting = new JobPosting(
            "QA Automation Engineer", "Beta", "Haifa", "QA",
            "https://example.com/qa", "- Selenium and Playwright experience");

        var backendEmbedding = await _embedder.EmbedAsync(JobPostingTextNormalizer.ToEmbeddingText(backendPosting));
        Assert.Equal(GeminiEmbedder.Dimensions, backendEmbedding.Length); // confirmed live, not assumed

        await _datastore.EnsureSchemaAsync(backendEmbedding.Length);
        await _datastore.UpsertAsync(backendId, backendPosting, backendEmbedding);

        var qaEmbedding = await _embedder.EmbedAsync(JobPostingTextNormalizer.ToEmbeddingText(qaPosting));
        await _datastore.UpsertAsync(qaId, qaPosting, qaEmbedding);

        var queryEmbedding = await _embedder.EmbedAsync("C# backend developer with distributed systems experience");

        // The table also holds real ingested postings (SETUP.md), so top-K isn't
        // necessarily just these two rows - compare their scores directly instead
        // of assuming they're the global top-2.
        var results = await _datastore.QuerySimilarAsync(queryEmbedding, topK: 10_000);
        var backendResult = results.Single(r => r.Posting.ApplyUrl == backendPosting.ApplyUrl);
        var qaResult = results.Single(r => r.Posting.ApplyUrl == qaPosting.ApplyUrl);

        Assert.True(backendResult.Similarity > qaResult.Similarity);
    }

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        var probe = await _embedder.EmbedAsync("dimension probe");

        await _datastore.EnsureSchemaAsync(probe.Length);
        await _datastore.EnsureSchemaAsync(probe.Length); // must not throw on a second call
    }

    [Fact]
    public async Task EmbedBatchAsync_EmbedsMultipleTextsInOneRequest()
    {
        var texts = new[]
        {
            "Senior Backend Engineer at Acme (Tel Aviv, Software)\n- 5+ years C#",
            "QA Automation Engineer at Beta (Haifa, QA)\n- Selenium and Playwright",
        };

        var vectors = await _embedder.EmbedBatchAsync(texts);

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(GeminiEmbedder.Dimensions, v.Length));
        Assert.NotEqual(vectors[0], vectors[1]);
    }

    [Fact]
    public async Task MarkScoredAsync_PersistsScoreAndReasoning_AndGetMatchesAsync_OrdersByScoreDescending()
    {
        var highId = $"test-milestone6-high-{Guid.NewGuid():N}";
        var lowId = $"test-milestone6-low-{Guid.NewGuid():N}";
        var belowThresholdId = $"test-milestone6-below-{Guid.NewGuid():N}";
        _testMessageIds.Add(highId);
        _testMessageIds.Add(lowId);
        _testMessageIds.Add(belowThresholdId);

        var highPosting = new JobPosting(
            "High Match", "Acme", "Tel Aviv", "Software", "https://example.com/high", "- test");
        var lowPosting = new JobPosting(
            "Low Match", "Acme", "Tel Aviv", "Software", "https://example.com/low", "- test");
        var belowPosting = new JobPosting(
            "Below Threshold", "Acme", "Tel Aviv", "Software", "https://example.com/below", "- test");

        var embedding = await _embedder.EmbedAsync("milestone 6 follow-up match test");
        await _datastore.EnsureSchemaAsync(embedding.Length);
        await _datastore.UpsertAsync(highId, highPosting, embedding);
        await _datastore.UpsertAsync(lowId, lowPosting, embedding);
        await _datastore.UpsertAsync(belowThresholdId, belowPosting, embedding);

        await _datastore.MarkScoredAsync(
        [
            new ScoredMark(highId, 95, "Great fit"),
            new ScoredMark(lowId, 75, "Decent fit"),
            new ScoredMark(belowThresholdId, 40, "Not a fit"),
        ]);

        var matches = await _datastore.GetMatchesAsync(matchThreshold: 70);

        // The table also holds real ingested/other-test postings, so compare relative
        // order and presence/absence of these three ids rather than the full result set.
        var matchIds = matches.Select(m => m.Posting.ApplyUrl).ToList();
        Assert.Contains(highPosting.ApplyUrl, matchIds);
        Assert.Contains(lowPosting.ApplyUrl, matchIds);
        Assert.DoesNotContain(belowPosting.ApplyUrl, matchIds);
        Assert.True(matchIds.IndexOf(highPosting.ApplyUrl) < matchIds.IndexOf(lowPosting.ApplyUrl));

        var highMatch = matches.Single(m => m.Posting.ApplyUrl == highPosting.ApplyUrl);
        Assert.Equal(95, highMatch.Score);
        Assert.Equal("Great fit", highMatch.Reasoning);
    }

    [Fact]
    public async Task GetMatchesAsync_TwoPostingsShareApplyUrl_CollapsesToTheHigherScoredOne()
    {
        // Same job, reposted under a different message_id with a slightly different
        // company-name spelling - two rows in storage (message_id is still the primary
        // key, unchanged), but GetMatchesAsync should surface it as one match.
        var applyUrl = $"https://example.com/dup-{Guid.NewGuid():N}";
        var higherId = $"test-milestone-cleanup-dup-high-{Guid.NewGuid():N}";
        var lowerId = $"test-milestone-cleanup-dup-low-{Guid.NewGuid():N}";
        _testMessageIds.Add(higherId);
        _testMessageIds.Add(lowerId);

        var higherPosting = new JobPosting("Backend Engineer", "Acme", "Tel Aviv", "Software", applyUrl, "- test");
        var lowerPosting = new JobPosting("Backend Engineer", "Acme Inc.", "Tel Aviv", "Software", applyUrl, "- test");

        var embedding = await _embedder.EmbedAsync("milestone cleanup near-duplicate collapse test");
        await _datastore.EnsureSchemaAsync(embedding.Length);
        await _datastore.UpsertAsync(higherId, higherPosting, embedding);
        await _datastore.UpsertAsync(lowerId, lowerPosting, embedding);

        await _datastore.MarkScoredAsync(
        [
            new ScoredMark(higherId, 95, "Great fit"),
            new ScoredMark(lowerId, 80, "Also a fit, but a repost"),
        ]);

        var matches = await _datastore.GetMatchesAsync(matchThreshold: 70);

        var duplicateMatches = matches.Where(m => m.Posting.ApplyUrl == applyUrl).ToList();
        var onlyMatch = Assert.Single(duplicateMatches);
        Assert.Equal(95, onlyMatch.Score);
        Assert.Equal("Acme", onlyMatch.Posting.Company);
    }

    [Fact]
    public async Task UpsertAsync_SameMessageIdTwice_DoesNotThrowAndStoresExactlyOneRow()
    {
        var id = $"test-idempotency-dup-{Guid.NewGuid():N}";
        _testMessageIds.Add(id);

        var posting = new JobPosting(
            "Idempotency Test Posting", "Acme", "Tel Aviv", "Software",
            "https://example.com/idempotency", "- re-ingested message must be a no-op");
        var embedding = await _embedder.EmbedAsync(JobPostingTextNormalizer.ToEmbeddingText(posting));
        await _datastore.EnsureSchemaAsync(embedding.Length);

        await _datastore.UpsertAsync(id, posting, embedding);
        await _datastore.UpsertAsync(id, posting, embedding); // re-ingest of the same source message - must not throw

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM job_postings WHERE message_id = @messageId;";
        command.Parameters.AddWithValue("messageId", id);
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetExistingMessageIdsAsync_ReflectsStoredRows()
    {
        var id = $"test-milestone4-dedupe-{Guid.NewGuid():N}";
        _testMessageIds.Add(id);

        var posting = new JobPosting(
            "Dedupe Test Posting", "Acme", "Tel Aviv", "Software",
            "https://example.com/dedupe", "- test row for GetExistingMessageIdsAsync");
        var embedding = await _embedder.EmbedAsync(JobPostingTextNormalizer.ToEmbeddingText(posting));
        await _datastore.EnsureSchemaAsync(embedding.Length);
        await _datastore.UpsertAsync(id, posting, embedding);

        var existingIds = await _datastore.GetExistingMessageIdsAsync();

        Assert.Contains(id, existingIds);
        Assert.DoesNotContain($"not-a-real-id-{Guid.NewGuid():N}", existingIds);
    }
}
