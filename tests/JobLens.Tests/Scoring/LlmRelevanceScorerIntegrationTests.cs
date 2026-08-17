using System.ClientModel;
using JobLens.Core.Configuration;
using JobLens.Core.Llm;
using JobLens.Core.Parsing;
using JobLens.Core.Scoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;

namespace JobLens.Tests.Scoring;

// Needs a running OmniRoute instance and configured Llm settings: one real chat
// completion call, no Postgres and no embedding calls. Candidate/template "embeddings"
// are hand-picked vectors used only by the scorer's local cosine routing and pre-rank.
[Trait("Category", "Integration")]
public class LlmRelevanceScorerIntegrationTests
{
    [Fact]
    public async Task ScoreAsync_RealOmniRouteCall_ReturnsValidScoresAndReasoning()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var baseUrl = config["Llm:BaseUrl"]
            ?? throw new InvalidOperationException("Missing Llm:BaseUrl - see SETUP.md.");
        var apiKey = config["Llm:ApiKey"]
            ?? throw new InvalidOperationException("Missing Llm:ApiKey - see SETUP.md.");
        var scoringModel = config["Llm:ScoringModel"]
            ?? throw new InvalidOperationException("Missing Llm:ScoringModel - see SETUP.md.");

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        IChatClient chatClient = client.GetChatClient(scoringModel).AsIChatClient();

        var profile = "Junior backend/full-stack engineer. C#/.NET, Selenium test automation, " +
                      "building LLM agent pipelines and RAG systems, Postgres.";
        var templateCatalog = new FakeTemplateCatalog(
            new ScoringTemplate("General", profile, [1f, 0f, 0f]));
        var options = Options.Create(new JobLensOptions { ScoringTopK = 2 });
        var scorer = new LlmRelevanceScorer(
            new ScoringChatClient(chatClient),
            templateCatalog,
            options,
            NullLogger<LlmRelevanceScorer>.Instance);

        var backendPosting = new JobPosting(
            "Junior .NET Backend Engineer", "Acme", "Tel Aviv", "Software",
            "https://example.com/backend", "- C#, ASP.NET Core, Postgres, 0-2 years experience");
        var unrelatedPosting = new JobPosting(
            "Senior Mechanical Design Engineer", "Beta", "Haifa", "Mechanical Engineering",
            "https://example.com/mech", "- SolidWorks, 10+ years experience with HVAC systems");

        var candidates = new List<(string, JobPosting, float[])>
        {
            ("backend-id", backendPosting, [1f, 0f, 0f]),
            ("unrelated-id", unrelatedPosting, [0f, 1f, 0f]),
        };

        var results = await scorer.ScoreAsync(candidates);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.InRange(r.Score, 0, 100));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Reasoning)));
        Assert.All(results, r => Assert.Equal("General", r.TemplateName));
    }
}
