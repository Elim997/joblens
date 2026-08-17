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
// TargetCategories/ScoringTemplates/ScoringTopK from appsettings. Injected later as
// IOptions<JobLensOptions>.
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
builder.Services.AddSingleton<ITemplateCatalog, TemplateCatalog>();
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
builder.Services.AddSingleton<ITailoredDraftStore, PgvectorTailoredDraftStore>();
builder.Services.AddSingleton<TailoredDraftService>();
builder.Services.AddSingleton<TailoredDraftExporter>();

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
// whole archive - Milestone 6's /run covers postings ingested before scoring was run
// by pulling unscored postings from pgvector instead.
app.MapPost("/ingest", async (
    IJobFeedSource feedSource,
    IPostingParser parser,
    IEmbedder embedder,
    IDatastore datastore,
    IRelevanceScorer scorer,
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

        var scoringCandidates = toEmbed.Select((x, i) => (x.Message.Id, x.Posting!, embeddings[i])).ToList();
        matches = await scorer.ScoreAsync(scoringCandidates, cancellationToken);
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

// The real "score my whole archive" loop: routes/ranks every unscored posting in
// pgvector against the configured scoring templates (not just a fresh /ingest batch),
// scores it in bounded
// ScoringTopK batches - looping until the backlog is drained or a batch returns zero
// usable scores - notifies matches at/above MatchThreshold (deduped across the whole
// run, not just per batch), and marks exactly what got scored so a later run never
// re-scores or re-notifies it.
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

// Creates a persistent draft for one scored posting. The selected template comes only from the
// persisted score, and this path never writes to Rezi. Repeated requests for the same posting,
// template, and mapped base return the existing draft without another tailoring-model call.
app.MapPost("/tailor", async (
    string messageId,
    TailoredDraftService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CreateOrGetAsync(messageId, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"No stored posting found for messageId '{messageId}'." })
            : Results.Ok(result.Draft);
    }
    catch (PostingNotScoredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (ScoringTemplateResumeMappingException ex)
    {
        // A persisted trusted template no longer maps to a configured base - a JobLens config
        // integrity problem, never permission to silently choose another base.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (ReziAuthenticationRequiredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (ReziToolCallException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TailoringModelUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TailoringOutputInvalidException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ResumeTailoringValidationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});

// Stored TailoredDrafts are JobLens's source of truth; listing is newest first.
app.MapGet("/tailored", async (
    ITailoredDraftStore draftStore,
    CancellationToken cancellationToken) =>
{
    var drafts = await draftStore.ListAsync(cancellationToken);
    return Results.Ok(drafts);
});

app.MapGet("/tailored/{draftId}", async (
    string draftId,
    ITailoredDraftStore draftStore,
    CancellationToken cancellationToken) =>
{
    var draft = await draftStore.GetByIdAsync(draftId, cancellationToken);
    return draft is null
        ? Results.NotFound(new { error = $"No tailored draft found for draftId '{draftId}'." })
        : Results.Ok(draft);
});

// The only Rezi write path in JobLens: exports this exact stored draft to the configured mutable
// "for edit" resume. It never re-runs tailoring and never writes to a configured base resume.
app.MapPost("/tailored/{draftId}/export-to-rezi", async (
    string draftId,
    TailoredDraftExporter exporter,
    CancellationToken cancellationToken) =>
{
    try
    {
        var draft = await exporter.ExportAsync(draftId, cancellationToken);
        return draft is null
            ? Results.NotFound(new { error = $"No tailored draft found for draftId '{draftId}'." })
            : Results.Ok(draft);
    }
    catch (ReziAuthenticationRequiredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (ReziToolCallException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ResumeTailoringValidationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (ResumeWriteConfigurationException ex)
    {
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

        var scoringTemplates = config.GetSection("JobLens:ScoringTemplates").Get<ScoringTemplateOptions[]>();
        if (scoringTemplates is not { Length: > 0 })
            throw new InvalidOperationException("Missing JobLens:ScoringTemplates (must be a non-empty array)");
        if (scoringTemplates.Any(t => string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Profile)))
        {
            throw new InvalidOperationException(
                "Each JobLens:ScoringTemplates entry must have a non-empty Name and Profile");
        }
        if (scoringTemplates.Select(t => t.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            scoringTemplates.Length)
        {
            throw new InvalidOperationException("JobLens:ScoringTemplates names must be unique");
        }

        // AutoTailorThreshold gates automatic TailoredDraft creation during /run (Milestone E).
        // It must sit within the valid 0-100 score range and at or above MatchThreshold - below
        // that, /run would auto-tailor postings that never even reached match status, which
        // contradicts the three-tier score contract, so this fails startup instead of warning.
        var defaults = new JobLensOptions();
        var matchThreshold = config.GetValue("JobLens:MatchThreshold", defaults.MatchThreshold);
        var autoTailorThreshold = config.GetValue("JobLens:AutoTailorThreshold", defaults.AutoTailorThreshold);

        if (autoTailorThreshold < 0 || autoTailorThreshold > 100)
        {
            throw new InvalidOperationException(
                "JobLens:AutoTailorThreshold must be between 0 and 100 (the valid relevance score range)");
        }
        if (autoTailorThreshold < matchThreshold)
        {
            throw new InvalidOperationException(
                "JobLens:AutoTailorThreshold must be greater than or equal to JobLens:MatchThreshold");
        }

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