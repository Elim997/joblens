using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Feed;
using JobLens.Core.Parsing;
using JobLens.Core.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI;
using Pgvector;            // for UseVector()
using System.ClientModel;  // for ApiKeyCredential

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// JobLens config: MessagesDbPath + GroupChatJid come from user-secrets,
// TargetCategories from appsettings. Injected later as IOptions<JobLensOptions>.
builder.Services.Configure<JobLensOptions>(config.GetSection("JobLens"));

if (string.IsNullOrWhiteSpace(config["JobLens:MessagesDbPath"]))
    throw new InvalidOperationException("Missing JobLens:MessagesDbPath");
if (string.IsNullOrWhiteSpace(config["JobLens:GroupChatJid"]))
    throw new InvalidOperationException("Missing JobLens:GroupChatJid");

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
builder.Services.AddSingleton<IChatClient>(
    gemini.GetChatClient("gemini-2.5-flash").AsIChatClient());
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator());

builder.Services.AddSingleton<IJobFeedSource, SqliteJobFeedSource>();
builder.Services.AddSingleton<IPostingParser, WhatsAppPostingParser>();
builder.Services.AddSingleton<IEmbedder, GeminiEmbedder>();
builder.Services.AddSingleton<IDatastore, PgvectorDatastore>();

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
app.MapPost("/ingest", async (
    IJobFeedSource feedSource,
    IPostingParser parser,
    IEmbedder embedder,
    IDatastore datastore,
    IOptions<JobLensOptions> options,
    CancellationToken cancellationToken) =>
{
    var messages = await feedSource.GetMessagesAsync(cancellationToken);
    var targetCategories = new HashSet<string>(options.Value.TargetCategories, StringComparer.OrdinalIgnoreCase);
    var candidates = messages
        .Select(m => (Message: m, Posting: parser.Parse(m)))
        .Where(x => x.Posting is not null && targetCategories.Contains(x.Posting.Category))
        .ToList();

    var existingIds = await datastore.GetExistingMessageIdsAsync(cancellationToken);
    var toEmbed = candidates.Where(x => !existingIds.Contains(x.Message.Id)).ToList();

    if (toEmbed.Count > 0)
    {
        var texts = toEmbed.Select(x => JobPostingTextNormalizer.ToEmbeddingText(x.Posting!)).ToList();
        var embeddings = await embedder.EmbedBatchAsync(texts, cancellationToken);

        await datastore.EnsureSchemaAsync(embeddings[0].Length, cancellationToken);
        for (var i = 0; i < toEmbed.Count; i++)
            await datastore.UpsertAsync(toEmbed[i].Message.Id, toEmbed[i].Posting!, embeddings[i], cancellationToken);
    }

    return Results.Ok(new { read = messages.Count, alreadyStored = candidates.Count - toEmbed.Count, embedded = toEmbed.Count });
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