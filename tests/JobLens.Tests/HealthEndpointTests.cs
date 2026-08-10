using System.Net;
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
                    ["Gemini:ApiKey"] = "fake-key",
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
