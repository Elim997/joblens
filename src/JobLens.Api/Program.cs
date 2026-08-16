using JobLens.Core.Configuration;
using JobLens.Core.Embedding;
using JobLens.Core.Eval;
using JobLens.Core.Feed;
using JobLens.Core.Llm;
using JobLens.Core.Notification;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Resume;
using JobLens.Core.Scoring;
using JobLens.Core.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using Npgsql;
using OpenAI;
using Pgvector;            // for UseVector()
using System.ClientModel;  // for ApiKeyCredential

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// JobLens config: MessagesDbPath + GroupChatJids come from user-secrets (identifying),
// TargetCategories/Profile/ScoringTopK from appsettings. Injected later as IOptions<JobLensOptions>.
builder.Services.Configure<JobLensOptions>(config.GetSection("JobLens"));
builder.Services.Configure<LlmOptions>(config.GetSection("Llm"));

// Rezi base resume IDs and the "for edit" slot ID identify this account's resumes, so like
// GroupChatJids they live in user-secrets, never appsettings. They are not startup requirements:
// resume tailoring is on-demand rather than part of the always-on ingest/run path, so missing Rezi
// configuration fails clearly at first actual use instead of blocking /health.
builder.Services.Configure<ReziOptions>(config.GetSection("Rezi"));

// Postgres + pgvector, Gemini embeddings, and OmniRoute chat clients are resolved lazily
// after startup validation. Gemini remains embedding-only; all chat requests go through
// provider-neutral role registrations backed by the externally managed OmniRoute API.
builder.Services.AddSingleton(sp =>
{
    var pgConn = sp.GetRequiredService<IConfiguration>()["Postgres:ConnectionString"]
        ?? throw new InvalidOperationException("Missing Postgres:ConnectionString");
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(pgConn);
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var geminiKey = sp.GetRequiredService<IConfiguration>()["Gemini:ApiKey"]
        ?? throw new InvalidOperationException("Missing Gemini:ApiKey");
    var gemini = new OpenAIClient(
        new ApiKeyCredential(geminiKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"),
        });

    return gemini.GetEmbeddingClient("gemini-embedding-001").AsIEmbeddingGenerator();
});

builder.Services.AddSingleton<IScoringChatClient>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return new ScoringChatClient(CreateOmniRouteChatClient(llm, llm.ScoringModel));
});
builder.Services.AddSingleton<ITailoringChatClient>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return new TailoringChatClient(CreateOmniRouteChatClient(llm, llm.TailoringModel));
});

static IChatClient CreateOmniRouteChatClient(LlmOptions options, string model)
{
    var client = new OpenAIClient(
        new ApiKeyCredential(options.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) });
    return client.GetChatClient(model).AsIChatClient();
}

builder.Services.AddSingleton<IJobFeedSource, SqliteJobFeedSource>();
builder.Services.AddSingleton<IPostingParser, WhatsAppPostingParser>();
builder.Services.AddSingleton<IEmbedder, GeminiEmbedder>();
builder.Services.AddSingleton<IDatastore, PgvectorDatastore>();
builder.Services.AddSingleton<IProfileEmbeddingProvider, ProfileEmbeddingProvider>();
builder.Services.AddSingleton<IRelevanceScorer, LlmRelevanceScorer>();
builder.Services.AddSingleton<INotifier, ConsoleNotifier>();
builder.Services.AddSingleton<PipelineRunner>();
builder.Services.AddSingleton<EvalHarness>();

// EncryptedFileTokenCache is DPAPI-backed and Windows-only; this project already assumes
// Windows (see SETUP.md), so the platform-compat warning is suppressed at this one call site
// rather than tagging the whole assembly, which would incorrectly mark unrelated endpoints too.
#pragma warning disable CA1416
builder.Services.AddSingleton<ITokenCache>(sp => new EncryptedFileTokenCache(
    ReziMcpConnection.DefaultTokenCachePath, sp.GetRequiredService<ILogger<EncryptedFileTokenCache>>()));
#pragma warning restore CA1416
builder.Services.AddSingleton<IResumeClient, RealResumeClient>();
builder.Services.AddSingleton<IResumeTailor, LlmResumeTailor>();
builder.Services.AddSingleton<ResumeTailoringRunner>();

builder.Services.AddOpenApi();

var app = builder.Build();

// Fail fast on missing required config. Runs against app.Configuration - the fully
// built configuration - rather than the pre-Build builder.Configuration, so a test
// host's config overrides (only merged in during Build()) are visible here. See
// Program.ValidateRequiredConfig for the hermetic unit tests that prove this directly.
Program.ValidateRequiredConfig(app.Configuration);

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
        newlyStored = toEmbed.Count,
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

// Stored matches from past /run passes: postings with score >= MatchThreshold, ordered
// highest first. Reads what MarkScoredAsync already persisted - no model call, no re-run.
app.MapGet("/matches", async (
    IDatastore datastore,
    IOptions<JobLensOptions> options,
    CancellationToken cancellationToken) =>
{
    var matches = await datastore.GetMatchesAsync(options.Value.MatchThreshold, cancellationToken);
    return Results.Ok(matches);
});

// Eval harness (Milestone 7): scores the ~20 labeled postings in Eval/labeled-postings.json
// through the real scorer and reports precision/recall/F1. Caveat comes straight from that
// file and sits top-level in the response so an auto-seeded, unlabeled run is never mistaken
// for a real evaluation.
app.MapPost("/eval", async (EvalHarness harness, CancellationToken cancellationToken) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Eval", "labeled-postings.json");
    var labeledSet = await LabeledPostingLoader.LoadAsync(path, cancellationToken);
    var report = await harness.RunAsync(labeledSet, cancellationToken);
    return Results.Ok(report);
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

// Phase 3: tailor a resume for one stored posting. commit defaults to false (preview only -
// returns the chosen base, rationale, and rewritten content, writes nothing) so an accidental
// call never overwrites the "for edit" slot; commit=true is the only path in JobLens that
// writes to Rezi, and only ever to that one configured slot (see ResumeTailoringRunner).
app.MapPost("/tailor", async (
    string messageId,
    ResumeTailoringRunner runner,
    CancellationToken cancellationToken,
    bool commit = false) => // omitted commit query param -> preview, never a write
{
    try
    {
        var result = await runner.RunAsync(messageId, commit, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"No stored posting found for messageId '{messageId}'." })
            : Results.Ok(result);
    }
    catch (ReziAuthenticationRequiredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (ReziToolCallException ex)
    {
        // Rezi itself reported a failure (not auth, not a JobLens bug) - a legible 502
        // ("upstream failed") beats an opaque unhandled-exception 500.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TailoringModelUnavailableException ex)
    {
        // The tailoring chat client itself was unreachable/failed at the transport level -
        // "service unavailable" is more accurate than an opaque 500, and matches the intent of
        // ReziToolCallException's 502 for the equivalent Rezi-side failure.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TailoringOutputInvalidException ex)
    {
        // The model responded but never produced parseable output after retries - an upstream
        // (model-side) failure, not a JobLens bug, so 502 rather than 500.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ResumeTailoringValidationException ex)
    {
        // The model's output parsed but failed validation against the base resume it claims to
        // rewrite (invented id, blank field, etc.) - a 422 ("well-formed but unprocessable"),
        // never a write.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (ResumeWriteConfigurationException ex)
    {
        // The write destination itself is unsafe (missing or equal to a base id) - a JobLens
        // config bug, not an upstream failure, so 500.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

public partial class Program
{
    /// <summary>
    /// Guards against missing feed, Postgres, Gemini embedding, or OmniRoute chat
    /// configuration with a clear error instead of a cryptic failure at first use. This
    /// is a pure function of IConfiguration, so ProgramValidationTests can exercise it
    /// hermetically with in-memory values.
    /// </summary>
    public static void ValidateRequiredConfig(IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["JobLens:MessagesDbPath"]))
            throw new InvalidOperationException("Missing JobLens:MessagesDbPath");
        if (config.GetSection("JobLens:GroupChatJids").Get<string[]>() is not { Length: > 0 })
            throw new InvalidOperationException("Missing JobLens:GroupChatJids (must be a non-empty array)");
        if (string.IsNullOrWhiteSpace(config["Postgres:ConnectionString"]))
            throw new InvalidOperationException("Missing Postgres:ConnectionString");
        if (string.IsNullOrWhiteSpace(config["Gemini:ApiKey"]))
            throw new InvalidOperationException("Missing Gemini:ApiKey");

        var llmBaseUrl = config["Llm:BaseUrl"];
        if (string.IsNullOrWhiteSpace(llmBaseUrl))
            throw new InvalidOperationException("Missing Llm:BaseUrl");
        if (!Uri.TryCreate(llmBaseUrl, UriKind.Absolute, out var llmUri) ||
            (llmUri.Scheme != Uri.UriSchemeHttp && llmUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Llm:BaseUrl must be an absolute HTTP or HTTPS URI");
        }

        if (string.IsNullOrWhiteSpace(config["Llm:ApiKey"]))
            throw new InvalidOperationException("Missing Llm:ApiKey");

        var scoringModel = config["Llm:ScoringModel"];
        if (string.IsNullOrWhiteSpace(scoringModel))
            throw new InvalidOperationException("Missing Llm:ScoringModel");

        var tailoringModel = config["Llm:TailoringModel"];
        if (string.IsNullOrWhiteSpace(tailoringModel))
            throw new InvalidOperationException("Missing Llm:TailoringModel");

        if (string.Equals(scoringModel.Trim(), "coding-fallback", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tailoringModel.Trim(), scoringModel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Llm:TailoringModel must be a dedicated model, not the scoring fallback combo");
        }
    }
}