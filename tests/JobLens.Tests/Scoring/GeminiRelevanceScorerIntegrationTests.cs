using System.ClientModel;
using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Scoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;

namespace JobLens.Tests.Scoring;

// Needs real Gemini quota (SETUP.md step 5): one real chat completion call, no
// Postgres and no embedding calls - candidate/profile "embeddings" here are just
// small hand-picked vectors for the local cosine pre-rank, not real Gemini output.
[Trait("Category", "Integration")]
public class GeminiRelevanceScorerIntegrationTests
{
    [Fact]
    public async Task ScoreAsync_RealGeminiCall_ReturnsValidScoresAndReasoning()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var geminiKey = config["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Missing Gemini:ApiKey - run SETUP.md step 5.");

        var gemini = new OpenAIClient(
            new ApiKeyCredential(geminiKey),
            new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") });
        IChatClient chatClient = gemini.GetChatClient("gemini-flash-latest").AsIChatClient();

        var options = Options.Create(new JobLensOptions
        {
            Profile = "Junior backend/full-stack engineer. C#/.NET, Selenium test automation, " +
                      "building LLM agent pipelines and RAG systems, Postgres.",
            ScoringTopK = 2,
        });
        var scorer = new GeminiRelevanceScorer(chatClient, options, NullLogger<GeminiRelevanceScorer>.Instance);

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

        var results = await scorer.ScoreAsync(candidates, [1f, 0f, 0f]);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.InRange(r.Score, 0, 100));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Reasoning)));
    }
}
