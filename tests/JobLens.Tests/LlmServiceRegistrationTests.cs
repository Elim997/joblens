using JobLens.Core.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JobLens.Tests;

public class LlmServiceRegistrationTests
{
    [Fact]
    public void Services_UseSeparateConfiguredChatModelRoles()
    {
        const string scoringModel = "test-scoring-model";
        const string tailoringModel = "test-tailoring-model";
        using var factory = CreateFactory(scoringModel, tailoringModel);
        using var scope = factory.Services.CreateScope();

        var scoring = scope.ServiceProvider.GetRequiredService<IScoringChatClient>();
        var tailoring = scope.ServiceProvider.GetRequiredService<ITailoringChatClient>();
        var scoringMetadata = scoring.ChatClient.GetRequiredService<ChatClientMetadata>();
        var tailoringMetadata = tailoring.ChatClient.GetRequiredService<ChatClientMetadata>();

        Assert.Equal(scoringModel, scoringMetadata.DefaultModelId);
        Assert.Equal(tailoringModel, tailoringMetadata.DefaultModelId);
        Assert.NotSame(scoring, tailoring);
        Assert.NotSame(scoring.ChatClient, tailoring.ChatClient);
        Assert.Null(scope.ServiceProvider.GetService<IChatClient>());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string scoringModel,
        string tailoringModel) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    ["Llm:ScoringModel"] = scoringModel,
                    ["Llm:TailoringModel"] = tailoringModel,
                });
            });
        });
}
