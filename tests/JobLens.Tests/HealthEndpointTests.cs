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
                    ["JobLens:GroupChatJid"] = "fake@g.us",
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
}
