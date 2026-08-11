using System.Text.Json.Nodes;
using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Tests.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Resume;

public class GeminiResumeTailorTests
{
    private const string QaBaseId = "qa-base-id";
    private const string BackendBaseId = "backend-base-id";
    private const string FullStackBaseId = "fullstack-base-id";

    private static JobPosting MakePosting(string title, string description) =>
        new(title, "Acme", "Tel Aviv", "QA", "https://example.com/job", description);

    private static JsonNode MakeBaseResume(string name, string summary, string expDescription, string skillText) =>
        new JsonObject
        {
            ["name"] = name,
            ["data"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["summary"] = summary },
                ["experience"] = new JsonObject
                {
                    ["exp1"] = new JsonObject { ["company"] = "Acme", ["role"] = "Engineer", ["description"] = expDescription },
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
            "QA Automation Developer", "QA automation engineer with Selenium/Playwright experience.",
            "Wrote automated regression suites.", "Selenium, Playwright, Postman"));
        client.Seed(BackendBaseId, MakeBaseResume(
            "Junior Backend Engineer", "Backend developer focused on C#/.NET and SQL.",
            "Built REST APIs in ASP.NET Core.", "C#, .NET, PostgreSQL"));
        client.Seed(FullStackBaseId, MakeBaseResume(
            "Full Stack Developer", "Full-stack developer across React and Node.js.",
            "Shipped a booking platform end to end.", "React, Node.js, TypeScript"));
        return client;
    }

    private static IOptions<ReziOptions> CreateOptions() => Options.Create(new ReziOptions
    {
        BaseResumes =
        [
            new BaseResumeConfig { Name = "QA Automation Developer", Id = QaBaseId },
            new BaseResumeConfig { Name = "Junior Backend Engineer", Id = BackendBaseId },
            new BaseResumeConfig { Name = "Full Stack Developer", Id = FullStackBaseId },
        ],
        ForEditResumeId = "for-edit-id",
    });

    [Fact]
    public async Task TailorAsync_QaJobPosting_SelectsQaBase()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            /* base selection: index 0 = QA Automation Developer */
            """{"index": 0, "rationale": "QA automation background matches the posting."}""",
            /* rewrite */
            """
            {
              "summary": "QA automation engineer experienced with Selenium and Playwright, well-suited for this role.",
              "experience": [{"itemId": "exp1", "description": "Wrote automated regression suites covering the full checkout flow."}],
              "skills": [{"itemId": "sk1", "skill": "Selenium, Playwright, REST API testing"}],
              "rationale": "Emphasized automation testing depth relevant to the posting."
            }
            """);
        var tailor = new GeminiResumeTailor(resumeClient, chatClient, CreateOptions(), NullLogger<GeminiResumeTailor>.Instance);

        var posting = MakePosting("QA Automation Engineer", "- Selenium\n- Playwright\n- 1+ years automation experience");
        var result = await tailor.TailorAsync(posting);

        Assert.Equal(QaBaseId, result.BaseSelection.BaseResumeId);
        Assert.Equal("QA Automation Developer", result.BaseSelection.BaseResumeName);
        Assert.NotEmpty(result.BaseSelection.Rationale);
    }

    [Fact]
    public async Task TailorAsync_RewriteKeepsStructure_SameItemIdsAsBase()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"index": 0, "rationale": "QA fit"}""",
            """
            {
              "summary": "Rewritten summary.",
              "experience": [{"itemId": "exp1", "description": "Rewritten experience bullet."}],
              "skills": [{"itemId": "sk1", "skill": "Rewritten skill line."}],
              "rationale": "Rewrote to emphasize QA fit."
            }
            """);
        var tailor = new GeminiResumeTailor(resumeClient, chatClient, CreateOptions(), NullLogger<GeminiResumeTailor>.Instance);

        var result = await tailor.TailorAsync(MakePosting("QA Engineer", "QA role"));

        Assert.Single(result.Experience);
        Assert.Equal("exp1", result.Experience[0].ItemId);
        Assert.Equal("Rewritten experience bullet.", result.Experience[0].Description);

        Assert.Single(result.Skills);
        Assert.Equal("sk1", result.Skills[0].ItemId);
        Assert.Equal("Rewritten skill line.", result.Skills[0].Skill);
    }

    [Fact]
    public async Task TailorAsync_ItemOmittedFromRewrite_KeepsOriginalText()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"index": 0, "rationale": "QA fit"}""",
            // Rewrite response omits both experience and skills entirely.
            """{"summary": "New summary only.", "experience": [], "skills": [], "rationale": "Only the summary needed changing."}""");
        var tailor = new GeminiResumeTailor(resumeClient, chatClient, CreateOptions(), NullLogger<GeminiResumeTailor>.Instance);

        var result = await tailor.TailorAsync(MakePosting("QA Engineer", "QA role"));

        Assert.Equal("Wrote automated regression suites.", result.Experience[0].Description);
        Assert.Equal("Selenium, Playwright, Postman", result.Skills[0].Skill);
    }

    [Fact]
    public async Task TailorAsync_RewriteInventsUnknownItemId_DropsItRatherThanApplyingIt()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"index": 0, "rationale": "QA fit"}""",
            """
            {
              "summary": "New summary.",
              "experience": [{"itemId": "exp1", "description": "Real edit."}, {"itemId": "made-up-id", "description": "Should be dropped."}],
              "skills": [],
              "rationale": "..."
            }
            """);
        var tailor = new GeminiResumeTailor(resumeClient, chatClient, CreateOptions(), NullLogger<GeminiResumeTailor>.Instance);

        var result = await tailor.TailorAsync(MakePosting("QA Engineer", "QA role"));

        Assert.Single(result.Experience); // still just exp1 - the invented id never made it into the result
        Assert.Equal("exp1", result.Experience[0].ItemId);
        Assert.Equal("Real edit.", result.Experience[0].Description);
    }

    [Fact]
    public async Task TailorAsync_NeverWritesAnything()
    {
        var resumeClient = CreateSeededResumeClient();
        var chatClient = new FakeChatClient(
            """{"index": 1, "rationale": "Backend fit"}""",
            """{"summary": "s", "experience": [], "skills": [], "rationale": "r"}""");
        var tailor = new GeminiResumeTailor(resumeClient, chatClient, CreateOptions(), NullLogger<GeminiResumeTailor>.Instance);

        await tailor.TailorAsync(MakePosting("Backend Engineer", "C#/.NET role"));

        Assert.Empty(resumeClient.Writes);
    }
}
