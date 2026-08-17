using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Core.Storage;
using JobLens.Tests.Pipeline;
using JobLens.Tests.Resume;
using JobLens.Tests.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobLens.Tests;

/// <summary>
/// Hermetic endpoint coverage for persistent draft creation and retrieval. The real datastore,
/// tailoring model/Rezi reader, and PostgreSQL draft store are replaced with in-memory fakes, so
/// POST /tailor cannot reach a live model, Rezi account, or database in these tests.
/// </summary>
public class TailorEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BaseId = "base-id";
    private const string BaseName = "QA Automation Developer";
    private const string MessageId = "msg-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public TailorEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static JobPosting MakePosting() =>
        new(
            "QA Automation Engineer",
            "Acme",
            "Tel Aviv",
            "QA",
            "https://example.com/job",
            "- Selenium");

    private static ValidatedTailoredResume MakeTailored() => new(
        new BaseSelection(BaseId, BaseName, "Matches the QA posting."),
        "Rewritten summary.",
        [new TailoredExperienceItem("exp1", "Rewritten bullet.")],
        [new TailoredSkillItem("sk1", "Rewritten skill.")],
        "Emphasized QA automation experience.",
        ["exp1"],
        ["sk1"]);

    private static TailoredDraft MakeDraft(
        string id,
        DateTimeOffset createdAt,
        TailoredDraftStatus status = TailoredDraftStatus.Draft) =>
        new(
            id,
            MessageId,
            91,
            BaseName,
            BaseId,
            BaseName,
            "Rewritten summary.",
            [new TailoredExperienceItem("exp1", "Rewritten bullet.")],
            [new TailoredSkillItem("sk1", "Rewritten skill.")],
            "Emphasized QA automation experience.",
            status,
            createdAt,
            status == TailoredDraftStatus.ExportedToRezi ? createdAt.AddMinutes(1) : null);

    private WebApplicationFactory<Program> CreateFactory(
        FakeResumeTailor tailor,
        FakeResumeClient resumeClient,
        FakeTailoredDraftStore draftStore,
        string? selectedTemplate = BaseName,
        int? score = 91,
        bool seedPosting = true,
        string configuredBaseName = BaseName,
        string configuredBaseId = BaseId)
    {
        var datastore = new FakeDatastore();
        if (seedPosting)
        {
            datastore.SeedScored(
                MessageId,
                MakePosting(),
                [1f, 0f, 0f],
                score,
                "Strong QA match.",
                selectedTemplate);
        }

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JobLens:MessagesDbPath"] = "C:/fake/messages.db",
                    ["JobLens:GroupChatJids:0"] = "fake@g.us",
                    ["JobLens:ScoringTemplates:0:Name"] = BaseName,
                    ["JobLens:ScoringTemplates:0:Profile"] = "Fake test profile",
                    ["Postgres:ConnectionString"] = "Host=fake;Database=fake;Username=fake;Password=fake",
                    ["Gemini:ApiKey"] = "fake-gemini-key",
                    ["Llm:BaseUrl"] = "http://localhost:20128/v1",
                    ["Llm:ApiKey"] = "fake-llm-key",
                    ["Llm:ScoringModel"] = "coding-fallback",
                    ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
                    ["Rezi:ForEditResumeId"] = "for-edit-id",
                    ["Rezi:BaseResumes:0:Id"] = configuredBaseId,
                    ["Rezi:BaseResumes:0:Name"] = configuredBaseName,
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDatastore>();
                services.AddSingleton<IDatastore>(datastore);
                services.RemoveAll<IResumeTailor>();
                services.AddSingleton<IResumeTailor>(tailor);
                services.RemoveAll<IResumeClient>();
                services.AddSingleton<IResumeClient>(resumeClient);
                services.RemoveAll<ITailoredDraftStore>();
                services.AddSingleton<ITailoredDraftStore>(draftStore);
            });
        });
    }

    [Fact]
    public async Task Tailor_CreatesPersistedDraftFromStoredTemplate_WithoutReziWrite()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailoredDraft>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(MessageId, body.MessageId);
        Assert.Equal(91, body.Score);
        Assert.Equal(BaseName, body.SelectedTemplate);
        Assert.Equal(BaseId, body.BaseResumeId);
        Assert.Equal(BaseName, body.BaseResumeName);
        Assert.Equal(TailoredDraftStatus.Draft, body.Status);
        Assert.Null(body.ExportedAt);
        Assert.Equal(BaseId, tailor.LastBaseResumeId);
        Assert.Equal(BaseName, tailor.LastBaseResumeName);
        Assert.Equal(1, tailor.CallCount);
        Assert.Equal(1, draftStore.CreateOrGetCallCount);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_RepeatedRequest_ReturnsSameDraftWithoutSecondModelCall()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var firstResponse = await client.PostAsync($"/tailor?messageId={MessageId}", null);
        var secondResponse = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<TailoredDraft>(JsonOptions);
        var second = await secondResponse.Content.ReadFromJsonAsync<TailoredDraft>(JsonOptions);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, tailor.CallCount);
        Assert.Equal(1, draftStore.CreateOrGetCallCount);
        Assert.Single(draftStore.Drafts);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_UnknownMessageId_ReturnsNotFound()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(
            tailor,
            resumeClient,
            draftStore,
            seedPosting: false).CreateClient();

        var response = await client.PostAsync("/tailor?messageId=unknown-id", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(draftStore.Drafts);
        Assert.Empty(resumeClient.Writes);
    }

    [Theory]
    [InlineData(null, 91)]
    [InlineData("", 91)]
    [InlineData("   ", 91)]
    [InlineData(BaseName, null)]
    public async Task Tailor_NotEligibleForDrafting_ReturnsConflict(
        string? selectedTemplate,
        int? score)
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(
            tailor,
            resumeClient,
            draftStore,
            selectedTemplate,
            score).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Rescore", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(draftStore.Drafts);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_StoredTemplateNoLongerMapsToBase_ReturnsInternalServerError()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(
            tailor,
            resumeClient,
            draftStore,
            selectedTemplate: "Historical QA Template",
            configuredBaseName: BaseName).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(draftStore.Drafts);
        Assert.Empty(resumeClient.Writes);
    }

    [Theory]
    [MemberData(nameof(TailorFailures))]
    public async Task Tailor_FailureMapsToExpectedStatus_AndPersistsNothing(
        Exception exception,
        HttpStatusCode expectedStatus)
    {
        var tailor = new FakeResumeTailor(exception);
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Empty(draftStore.Drafts);
        Assert.Equal(0, draftStore.CreateOrGetCallCount);
        Assert.Empty(resumeClient.Writes);
    }

    public static TheoryData<Exception, HttpStatusCode> TailorFailures => new()
    {
        { new ReziAuthenticationRequiredException(), HttpStatusCode.Unauthorized },
        { new ReziToolCallException("read_resume", "failed"), HttpStatusCode.BadGateway },
        { new TailoringModelUnavailableException("boom"), HttpStatusCode.ServiceUnavailable },
        { new TailoringOutputInvalidException("boom"), HttpStatusCode.BadGateway },
        { new ResumeTailoringValidationException("boom"), HttpStatusCode.UnprocessableEntity },
    };

    [Fact]
    public async Task GetTailored_ReturnsNewestFirst()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var older = MakeDraft("older", DateTimeOffset.Parse("2026-08-17T10:00:00Z"));
        var newer = MakeDraft(
            "newer",
            DateTimeOffset.Parse("2026-08-17T11:00:00Z"),
            TailoredDraftStatus.ExportedToRezi);
        draftStore.Seed(older);
        draftStore.Seed(newer);
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var response = await client.GetAsync("/tailored");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TailoredDraft>>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(["newer", "older"], body.Select(d => d.Id));
    }

    [Fact]
    public async Task GetTailoredById_ReturnsStoredDraft()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        draftStore.Seed(MakeDraft("draft-1", DateTimeOffset.Parse("2026-08-17T10:00:00Z")));
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var response = await client.GetAsync("/tailored/draft-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailoredDraft>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("draft-1", body.Id);
        Assert.Equal("Rewritten summary.", body.Summary);
    }

    [Fact]
    public async Task GetTailoredById_UnknownDraft_ReturnsNotFound()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var draftStore = new FakeTailoredDraftStore();
        var client = CreateFactory(tailor, resumeClient, draftStore).CreateClient();

        var response = await client.GetAsync("/tailored/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
