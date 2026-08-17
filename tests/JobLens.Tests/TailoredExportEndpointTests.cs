using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobLens.Core.Resume;
using JobLens.Core.Storage;
using JobLens.Tests.Resume;
using JobLens.Tests.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobLens.Tests;

/// <summary>
/// Hermetic coverage for explicit persisted-draft export. The real Rezi client and PostgreSQL
/// store are replaced, so these tests cannot perform a live Rezi write.
/// </summary>
public class TailoredExportEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BaseId = "base-read-only";
    private const string ForEditId = "for-edit-only";
    private const string DraftId = "draft-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public TailoredExportEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    private static TailoredDraft MakeDraft(
        string summary = "Stored summary.",
        string rationale = "Stored rationale.") =>
        new(
            DraftId,
            "message-1",
            94,
            "QA Automation Developer",
            BaseId,
            "QA Automation Developer",
            summary,
            [new TailoredExperienceItem("exp-1", "Stored experience.")],
            [new TailoredSkillItem("skill-1", "Stored skill.")],
            rationale,
            TailoredDraftStatus.Draft,
            DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            null);

    private WebApplicationFactory<Program> CreateFactory(
        FakeResumeClient resumeClient,
        FakeTailoredDraftStore draftStore,
        string forEditId = ForEditId,
        string baseId = BaseId)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "C:/fake/messages.db",
                    ["JobLens:GroupChatJids:0"] = "fake@g.us",
                    ["JobLens:ScoringTemplates:0:Name"] = "QA Automation Developer",
                    ["JobLens:ScoringTemplates:0:Profile"] = "Fake test profile",
                    ["Postgres:ConnectionString"] = "Host=fake;Database=fake;Username=fake;Password=fake",
                    ["Gemini:ApiKey"] = "fake-gemini-key",
                    ["Llm:BaseUrl"] = "http://localhost:20128/v1",
                    ["Llm:ApiKey"] = "fake-llm-key",
                    ["Llm:ScoringModel"] = "coding-fallback",
                    ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
                    ["Rezi:ForEditResumeId"] = forEditId,
                    ["Rezi:BaseResumes:0:Id"] = baseId,
                    ["Rezi:BaseResumes:0:Name"] = "QA Automation Developer",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IResumeClient>();
                services.AddSingleton<IResumeClient>(resumeClient);
                services.RemoveAll<ITailoredDraftStore>();
                services.AddSingleton<ITailoredDraftStore>(draftStore);
            });
        });
    }

    [Fact]
    public async Task ExportToRezi_WritesPersistedDraftToConfiguredDestinationAndReturnsUpdatedDraft()
    {
        var draft = MakeDraft();
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(draft);
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync(
            $"/tailored/{DraftId}/export-to-rezi",
            null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailoredDraft>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(DraftId, body.Id);
        Assert.Equal(TailoredDraftStatus.ExportedToRezi, body.Status);
        Assert.NotNull(body.ExportedAt);
        Assert.Equal(draft.Summary, body.Summary);
        Assert.Equal(draft.Experience, body.Experience);
        Assert.Equal(draft.Skills, body.Skills);
        Assert.Empty(resumeClient.Reads);
        var write = Assert.Single(resumeClient.Writes);
        Assert.Equal(ForEditId, write.ResumeId);
        Assert.Equal(
            draft.Summary,
            write.Resume["data"]?["summary"]?["summary"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExportToRezi_UnknownDraftReturnsNotFoundWithoutWrite()
    {
        var draftStore = new FakeTailoredDraftStore();
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync(
            "/tailored/unknown/export-to-rezi",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Theory]
    [MemberData(nameof(ExportFailures))]
    public async Task ExportToRezi_ReziFailureMapsToExpectedStatusAndDoesNotMarkExported(
        Exception exception,
        HttpStatusCode expectedStatus)
    {
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(MakeDraft());
        var resumeClient = new FakeResumeClient { WriteException = exception };
        var client = CreateFactory(resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync(
            $"/tailored/{DraftId}/export-to-rezi",
            null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
        var unchanged = Assert.Single(draftStore.Drafts);
        Assert.Equal(TailoredDraftStatus.Draft, unchanged.Status);
        Assert.Null(unchanged.ExportedAt);
    }

    public static TheoryData<Exception, HttpStatusCode> ExportFailures => new()
    {
        { new ReziAuthenticationRequiredException(), HttpStatusCode.Unauthorized },
        { new ReziToolCallException("write_resume", "failed"), HttpStatusCode.BadGateway },
    };

    [Fact]
    public async Task ExportToRezi_InvalidStoredDraftReturnsUnprocessableEntityWithoutWrite()
    {
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(MakeDraft(summary: "   "));
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync(
            $"/tailored/{DraftId}/export-to-rezi",
            null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(draftStore.Drafts).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(BaseId)]
    public async Task ExportToRezi_InvalidWriteConfigurationReturnsInternalServerErrorWithoutWrite(
        string forEditId)
    {
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(MakeDraft());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(resumeClient, draftStore, forEditId).CreateClient();

        var response = await client.PostAsync(
            $"/tailored/{DraftId}/export-to-rezi",
            null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(draftStore.Drafts).Status);
    }
}
