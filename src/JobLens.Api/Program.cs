using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Feed;
using JobLens.Core.Notification;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI;
using Pgvector;            // for UseVector()
using System.ClientModel;  // for ApiKeyCredential

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// JobLens config: MessagesDbPath + GroupChatJids come from user-secrets (identifying),
// TargetCategories/Profile/ScoringTopK from appsettings. Injected later as IOptions<JobLensOptions>.
builder.Services.Configure<JobLensOptions>(config.GetSection("JobLens"));

if (string.IsNullOrWhiteSpace(config["JobLens:MessagesDbPath"]))
    throw new InvalidOperationException("Missing JobLens:MessagesDbPath");
if (config.GetSection("JobLens:GroupChatJids").Get<string[]>() is not { Length: > 0 })
    throw new InvalidOperationException("Missing JobLens:GroupChatJids (must be a non-empty array)");

// Postgres + pgvector data source, registered in DI.
var pgConn = config["Postgres:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Postgres:ConnectionString");
var dataSourceBuilder = new NpgsqlDataSourceBuilder(pgConn);
dataSourceBuilder.UseVector();
builder.Services.AddSingleton(dataSourceBuilder.Build());

// Gemini via its OpenAI-compatible endpoint, exposed as the M.E.AI abstractions.
var geminiKey = config["Gemini:ApiKey"]
    ?? throw new InvalidOperationException("Missing Gemini:ApiKey");
var gemini = new OpenAIClient(
    new ApiKeyCredential(geminiKey),
    new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") });
// gemini-2.5-flash and gemini-2.5-flash-lite are deprecated (404 "no longer
// available to new users" as of this key). gemini-flash-latest is Google's rolling
// alias to their current flash model - confirmed working live; see CLAUDE.md.
builder.Services.AddSingleton<IChatClient>(
    gemini.GetChatClient("gemini-flash-latest").AsIChatClient());
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator());

builder.Services.AddSingleton<IJobFeedSource, SqliteJobFeedSource>();
builder.Services.AddSingleton<IPostingParser, WhatsAppPostingParser>();
builder.Services.AddSingleton<IEmbedder, GeminiEmbedder>();
builder.Services.AddSingleton<IDatastore, PgvectorDatastore>();
builder.Services.AddSingleton<IProfileEmbeddingProvider, ProfileEmbeddingProvider>();
builder.Services.AddSingleton<IRelevanceScorer, GeminiRelevanceScorer>();
builder.Services.AddSingleton<INotifier, ConsoleNotifier>();
builder.Services.AddSingleton<PipelineRunner>();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Runs Feed -> Parse -> category filter -> Embed -> Store for every message currently
// in the bridge's messages.db. Already-stored message ids are skipped before embedding
// (embedding is the quota-limited step) and the remainder is embedded in batches, not
// one API call per posting. The first embedding's live dimension sizes the schema.
// Scoring here is a convenience over just this run's newly-embedded batch, not the
// whole archive - Milestone 6's /run covers postings ingested before the profile
// existed by pulling unscored postings from pgvector instead.
app.MapPost("/ingest", async (
    IJobFeedSource feedSource,
    IPostingParser parser,
    IEmbedder embedder,
    IDatastore datastore,
    IRelevanceScorer scorer,
    IProfileEmbeddingProvider profileEmbeddingProvider,
    IOptions<JobLensOptions> options,
    CancellationToken cancellationToken) =>
{
    var messages = await feedSource.GetMessagesAsync(cancellationToken);
    var parsed = messages.Select(m => (Message: m, Posting: parser.Parse(m))).ToList();
    var parsedCount = parsed.Count(x => x.Posting is not null);

    var targetCategories = new HashSet<string>(options.Value.TargetCategories, StringComparer.OrdinalIgnoreCase);
    var candidates = parsed
        .Where(x => x.Posting is not null && targetCategories.Contains(x.Posting.Category))
        .ToList();

    var existingIds = await datastore.GetExistingMessageIdsAsync(cancellationToken);
    var toEmbed = candidates.Where(x => !existingIds.Contains(x.Message.Id)).ToList();

    IReadOnlyList<ScoredPosting> matches = [];
    if (toEmbed.Count > 0)
    {
        var texts = toEmbed.Select(x => JobPostingTextNormalizer.ToEmbeddingText(x.Posting!)).ToList();
        var embeddings = await embedder.EmbedBatchAsync(texts, cancellationToken);

        await datastore.EnsureSchemaAsync(embeddings[0].Length, cancellationToken);
        for (var i = 0; i < toEmbed.Count; i++)
            await datastore.UpsertAsync(toEmbed[i].Message.Id, toEmbed[i].Posting!, embeddings[i], cancellationToken);

        var profileEmbedding = await profileEmbeddingProvider.GetProfileEmbeddingAsync(cancellationToken);
        var scoringCandidates = toEmbed.Select((x, i) => (x.Message.Id, x.Posting!, embeddings[i])).ToList();
        matches = await scorer.ScoreAsync(scoringCandidates, profileEmbedding, cancellationToken);
    }

    return Results.Ok(new
    {
        fetched = messages.Count,
        parsed = parsedCount,
        filteredOut = parsedCount - candidates.Count,
        alreadyStored = candidates.Count - toEmbed.Count,
        embedded = toEmbed.Count,
        matches,
    });
});

// The real "score my whole archive" loop: ranks every unscored posting in pgvector
// against the profile (not just a fresh /ingest batch), scores the top ScoringTopK,
// notifies matches at/above MatchThreshold, and marks exactly what got scored so a
// later run never re-scores or re-notifies it.
app.MapPost("/run", async (PipelineRunner runner, CancellationToken cancellationToken) =>
{
    var summary = await runner.RunAsync(cancellationToken);
    return Results.Ok(summary);
});

// Semantic archive search: embeds the query text and ranks stored postings by cosine similarity.
app.MapGet("/query", async (
    string text,
    int? topK,
    IEmbedder embedder,
    IDatastore datastore,
    CancellationToken cancellationToken) =>
{
    var queryEmbedding = await embedder.EmbedAsync(text, cancellationToken);
    await datastore.EnsureSchemaAsync(queryEmbedding.Length, cancellationToken);
    var results = await datastore.QuerySimilarAsync(queryEmbedding, topK ?? 5, cancellationToken);
    return Results.Ok(results);
});

app.Run();

public partial class Program;