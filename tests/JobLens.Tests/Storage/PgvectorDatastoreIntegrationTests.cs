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
