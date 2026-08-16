using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace JobLens.Tests;

// Needs real Gemini embedding quota plus a running, configured OmniRoute for the
// batch embedding and scoring calls POST /eval makes against labeled-postings.json.
// Postgres:ConnectionString is required by startup validation but is never queried -
// /eval does not touch IDatastore.
[Trait("Category", "Integration")]
public class EvalEndpointIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EvalEndpointIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var secrets = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var geminiKey = secrets["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Missing Gemini:ApiKey - run SETUP.md step 5.");
        var pgConn = secrets["Postgres:ConnectionString"]
            ?? throw new InvalidOperationException("Missing Postgres:ConnectionString - run SETUP.md step 3.");
        var llmBaseUrl = secrets["Llm:BaseUrl"]
            ?? throw new InvalidOperationException("Missing Llm:BaseUrl - see SETUP.md.");
        var llmApiKey = secrets["Llm:ApiKey"]
            ?? throw new InvalidOperationException("Missing Llm:ApiKey - see SETUP.md.");
        var scoringModel = secrets["Llm:ScoringModel"]
            ?? throw new InvalidOperationException("Missing Llm:ScoringModel - see SETUP.md.");
        var tailoringModel = secrets["Llm:TailoringModel"]
            ?? throw new InvalidOperationException("Missing Llm:TailoringModel - see SETUP.md.");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "C:/fake/messages.db",
                    ["JobLens:GroupChatJids:0"] = "fake@g.us",
                    ["Postgres:ConnectionString"] = pgConn,
                    ["Gemini:ApiKey"] = geminiKey,
                    ["Llm:BaseUrl"] = llmBaseUrl,
                    ["Llm:ApiKey"] = llmApiKey,
                    ["Llm:ScoringModel"] = scoringModel,
                    ["Llm:TailoringModel"] = tailoringModel,
                });
            });
        });
    }

    [Fact]
    public async Task Eval_RealEmbeddingAndScoringCalls_ReturnsReportWithCaveatSurfacedTopLevel()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/eval", content: null);

        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.InRange(report.GetProperty("precision").GetDouble(), 0, 1);
        Assert.InRange(report.GetProperty("recall").GetDouble(), 0, 1);
        Assert.InRange(report.GetProperty("f1").GetDouble(), 0, 1);

        // The caveat must survive from labeled-postings.json to the top-level response -
        // it is what stops this report from being mistaken for a real evaluation.
        var caveat = report.GetProperty("caveat").GetString();
        Assert.False(string.IsNullOrWhiteSpace(caveat));
        Assert.Contains("PLACEHOLDER", caveat);

        var items = report.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(20, items.Count);
    }
}
