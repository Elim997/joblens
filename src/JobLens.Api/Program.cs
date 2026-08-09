using JobLens.Core.Configuration;
using JobLens.Core.Feed;
using JobLens.Core.Parsing;
using Microsoft.Extensions.AI;
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

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;