using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace JobLens.Tests;

// Needs real Gemini quota (SETUP.md step 5) for the batch embedding + scoring calls
// POST /eval makes against the real Eval/labeled-postings.json shipped with the app.
// Postgres:ConnectionString is still required to build the app's DI graph (Program.cs
// wires NpgsqlDataSource unconditionally) but is never queried - /eval never touches IDatastore.
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
                });
            });
        });
    }

    [Fact]
    public async Task Eval_RealGeminiCall_ReturnsReportWithCaveatSurfacedTopLevel()
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
