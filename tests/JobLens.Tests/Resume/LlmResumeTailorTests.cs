using System.Text.Json.Nodes;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Tests.Scoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobLens.Tests.Resume;

public class LlmResumeTailorTests
{
    private const string QaBaseId = "qa-base-id";
    private const string BackendBaseId = "backend-base-id";

    private static JobPosting MakePosting(string title, string description) =>
        new(title, "Acme", "Tel Aviv", "QA", "https://example.com/job", description);

    private static JsonNode MakeBaseResume(
        string name,
        string summary,
        string expDescription,
        string skillText) =>
        new JsonObject
        {
            ["name"] = name,
            ["data"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["summary"] = summary },
                ["experience"] = new JsonObject
                {
                    ["exp1"] = new JsonObject
                    {
                        ["company"] = "Acme",
                        ["role"] = "Engineer",
                        ["description"] = expDescription,
                    },
                },
                ["skills"] = new JsonObject
                {
                    ["sk1"] = new JsonObject { ["skill"] = skillText },
                },
            },
        };

    private static FakeResumeClient CreateSeededResumeClient()
    {
        var client = new FakeResumeClient();
        client.Seed(QaBaseId, MakeBaseResume(
            "QA Automation Developer",
            "QA automation engineer with Selenium/Playwright experience.",
            "Wrote automated regression suites.",
            "Selenium, Playwright, Postman"));
        client.Seed(BackendBaseId, MakeBaseResume(
            "Junior Backend Engineer",
            "Backend developer focused on C#/.NET and SQL.",
            "Built REST APIs in ASP.NET Core.",
            "C#, .NET, PostgreSQL"));
        return client;
    }

    private static LlmResumeTailor CreateTailor(
        FakeResumeClient resumeClient,
        FakeChatClient chatClient) =>
        new(
            resumeClient,
            new JobLens.Core.Llm.TailoringChatClient(chatClient),
            NullLogger<LlmResumeTailor>.Instance);

    [Fact]
    public async Task TailorAsync_UsesExplicitBaseAndDoesNotRunASelectionCall()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """
            {
              "summary": "Backend engineer experienced with C# and ASP.NET Core.",
              "experience": [{"itemId": "exp1", "description": "Built production REST APIs in ASP.NET Core."}],
              "skills": [{"itemId": "sk1", "skill": "C#, .NET, PostgreSQL"}],
              "rationale": "Emphasized the backend experience relevant to the posting."
            }
            """);
        var tailor = CreateTailor(resumeClient, chatClient);

        var result = await tailor.TailorAsync(
            MakePosting("Backend Engineer", "- C#\n- ASP.NET Core"),
            BackendBaseId,
            "Junior Backend Engineer");

        Assert.Equal(BackendBaseId, result.BaseSelection.BaseResumeId);
        Assert.Equal("Junior Backend Engineer", result.BaseSelection.BaseResumeName);
        Assert.Contains("persisted scoring template", result.BaseSelection.Rationale);
        Assert.Equal([BackendBaseId], resumeClient.Reads);
        Assert.Equal(1, chatClient.CallCount);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_RewriteKeepsStructure_SameItemIdsAsBase()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """
            {
              "summary": "Rewritten summary.",
              "experience": [{"itemId": "exp1", "description": "Rewritten experience bullet."}],
              "skills": [{"itemId": "sk1", "skill": "Rewritten skill line."}],
              "rationale": "Rewrote to emphasize QA fit."
            }
            """);
        var tailor = CreateTailor(resumeClient, chatClient);

        var result = await tailor.TailorAsync(
            MakePosting("QA Engineer", "QA role"),
            QaBaseId,
            "QA Automation Developer");

        var experience = Assert.Single(result.Experience);
        Assert.Equal("exp1", experience.ItemId);
        Assert.Equal("Rewritten experience bullet.", experience.Description);

        var skill = Assert.Single(result.Skills);
        Assert.Equal("sk1", skill.ItemId);
        Assert.Equal("Rewritten skill line.", skill.Skill);

        Assert.Equal(["exp1"], result.OriginalExperienceIds);
        Assert.Equal(["sk1"], result.OriginalSkillIds);
        Assert.Equal([QaBaseId], resumeClient.Reads);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_ItemOmittedFromRewrite_KeepsOriginalText()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"summary": "New summary only.", "experience": [], "skills": [], "rationale": "Only the summary needed changing."}""");
        var tailor = CreateTailor(resumeClient, chatClient);

        var result = await tailor.TailorAsync(
            MakePosting("QA Engineer", "QA role"),
            QaBaseId,
            "QA Automation Developer");

        Assert.Equal("Wrote automated regression suites.", result.Experience[0].Description);
        Assert.Equal("Selenium, Playwright, Postman", result.Skills[0].Skill);
    }

    [Fact]
    public async Task TailorAsync_RewriteInventsUnknownItemId_ThrowsValidationException()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """
            {
              "summary": "New summary.",
              "experience": [
                {"itemId": "exp1", "description": "Real edit."},
                {"itemId": "made-up-id", "description": "Should not be accepted."}
              ],
              "skills": [],
              "rationale": "Emphasized relevant work."
            }
            """);
        var tailor = CreateTailor(resumeClient, chatClient);

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer"));

        Assert.Equal([QaBaseId], resumeClient.Reads);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_MissingExplicitBase_ThrowsWithoutModelOrWrite()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"summary": "s", "experience": [], "skills": [], "rationale": "r"}""");
        var tailor = CreateTailor(resumeClient, chatClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                "missing-base-id",
                "Missing Base"));

        Assert.Equal(["missing-base-id"], resumeClient.Reads);
        Assert.Equal(0, chatClient.CallCount);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_ChatClientThrowsOnRewrite_ThrowsModelUnavailableWithoutLeakingMessage()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            new HttpRequestException("upstream 500, secret-token=abc123"));
        var tailor = CreateTailor(resumeClient, chatClient);

        var ex = await Assert.ThrowsAsync<TailoringModelUnavailableException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer"));

        Assert.DoesNotContain("secret-token", ex.Message);
        Assert.Contains("HttpRequestException", ex.Message);
        Assert.Equal([QaBaseId], resumeClient.Reads);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_RewriteResponseNeverParses_ThrowsOutputInvalidAfterRetries()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient("not json at all", "still not json");
        var tailor = CreateTailor(resumeClient, chatClient);

        await Assert.ThrowsAsync<TailoringOutputInvalidException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer"));

        Assert.Equal(2, chatClient.CallCount);
        Assert.Equal([QaBaseId], resumeClient.Reads);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_RewriteRationaleMissing_ThrowsValidationException()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"summary": "summary", "experience": [], "skills": []}""");
        var tailor = CreateTailor(resumeClient, chatClient);

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer"));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_WrongJsonFieldType_NeverCreatesValidatedResult()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"summary": 42, "experience": [], "skills": [], "rationale": "why"}""",
            """{"summary": 42, "experience": [], "skills": [], "rationale": "why"}""");
        var tailor = CreateTailor(resumeClient, chatClient);

        await Assert.ThrowsAsync<TailoringOutputInvalidException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer"));

        Assert.Equal(2, chatClient.CallCount);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task TailorAsync_CancellationRequested_PropagatesAsOperationCanceled()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"summary": "s", "experience": [], "skills": [], "rationale": "r"}""");
        var tailor = CreateTailor(resumeClient, chatClient);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tailor.TailorAsync(
                MakePosting("QA Engineer", "QA role"),
                QaBaseId,
                "QA Automation Developer",
                cts.Token));

        Assert.Empty(resumeClient.Reads);
        Assert.Equal(0, chatClient.CallCount);
        Assert.Empty(resumeClient.Writes);
    }
}
