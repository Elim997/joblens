using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace JobLens.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "C:/fake/messages.db",
                    ["JobLens:GroupChatJids:0"] = "fake@g.us",
                    ["Postgres:ConnectionString"] = "Host=fake;Database=fake;Username=fake;Password=fake",
                    ["Gemini:ApiKey"] = "fake-gemini-key",
                    ["Llm:BaseUrl"] = "http://localhost:20128/v1",
                    ["Llm:ApiKey"] = "fake-llm-key",
                    ["Llm:ScoringModel"] = "coding-fallback",
                    ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
                });
            });
        });
    }

    [Fact]
    public async Task Health_ReturnsOkStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }

    // Milestone F1: /health now also reports BuildInfo.Marker so a stale published binary is
    // detectable from this endpoint alone. Parses the body rather than substring-matching, since
    // the marker's actual value (informational version, optionally +<commit-sha>) is
    // machine/build-dependent and shouldn't be hardcoded in a test.
    [Fact]
    public async Task Health_IncludesNonEmptyBuildMarker()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("build", out var build));
        Assert.False(string.IsNullOrWhiteSpace(build.GetString()));
    }

    // Proves this whole test class is genuinely hermetic, not passing by accident
    // because this machine's real user-secrets happen to satisfy validation: overriding
    // one required setting to blank must make the host fail to start, using only this
    // test's in-memory config - no real secrets involved either way.
    [Fact]
    public async Task Health_BlankRequiredSetting_HostFailsToStart()
    {
        var brokenFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "",
                });
            });
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var client = brokenFactory.CreateClient();
            await client.GetAsync("/health");
        });
    }
}
