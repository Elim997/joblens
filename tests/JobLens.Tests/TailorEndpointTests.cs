using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Core.Storage;
using JobLens.Tests.Pipeline;
using JobLens.Tests.Resume;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobLens.Tests;

/// <summary>
/// End-to-end coverage of POST /tailor through the real ASP.NET Core pipeline: the runner,
/// ResumeTailoringValidator, and Program.cs's exception-to-HTTP-status mapping are all exercised
/// together, with only IDatastore/IResumeTailor/IResumeClient swapped for in-memory fakes (see
/// CreateFactory) so no real Postgres/Gemini/OmniRoute/Rezi call ever happens. commit=true here
/// only ever reaches the in-memory FakeResumeClient - never a real Rezi write.
/// </summary>
public class TailorEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ForEditId = "for-edit-id";
    private const string BaseId = "base-id";
    private const string MessageId = "msg-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public TailorEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static JobPosting MakePosting() =>
        new("QA Automation Engineer", "Acme", "Tel Aviv", "QA", "https://example.com/job", "- Selenium");

    private static ValidatedTailoredResume MakeTailored() => new(
        new BaseSelection(BaseId, "QA Automation Developer", "Matches the QA posting."),
        "Rewritten summary.",
        [new TailoredExperienceItem("exp1", "Rewritten bullet.")],
        [new TailoredSkillItem("sk1", "Rewritten skill.")],
        "Emphasized QA automation experience.",
        ["exp1"],
        ["sk1"]);

    private WebApplicationFactory<Program> CreateFactory(
        FakeResumeTailor tailor,
        FakeResumeClient resumeClient,
        string forEditId = ForEditId,
        bool seedPosting = true)
    {
        var datastore = new FakeDatastore();
        if (seedPosting)
            datastore.Seed(MessageId, MakePosting(), [1f, 0f, 0f]);

        return _factory.WithWebHostBuilder(builder =>
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
                    ["Rezi:ForEditResumeId"] = forEditId,
                    ["Rezi:BaseResumes:0:Id"] = BaseId,
                    ["Rezi:BaseResumes:0:Name"] = "QA Automation Developer",
                });
            });
            builder.ConfigureServices(services =>
            {
                // Real IDatastore/IResumeTailor/IResumeClient talk to Postgres, the tailoring
                // model, and Rezi respectively - swapped for in-memory fakes so this whole class
                // stays hermetic, same intent as HealthEndpointTests' fake config values.
                services.RemoveAll<IDatastore>();
                services.AddSingleton<IDatastore>(datastore);
                services.RemoveAll<IResumeTailor>();
                services.AddSingleton<IResumeTailor>(tailor);
                services.RemoveAll<IResumeClient>();
                services.AddSingleton<IResumeClient>(resumeClient);
            });
        });
    }

    [Fact]
    public async Task Tailor_CommitOmitted_DefaultsToPreview_NoWrites()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailorResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(body.Committed);
        Assert.Null(body.WrittenToResumeId);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_CommitFalseExplicit_Preview_NoWrites()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=false", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailorResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(body.Committed);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_CommitTrue_WritesExactlyOnceToTheConfiguredForEditId()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TailorResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.True(body.Committed);
        Assert.Equal(ForEditId, body.WrittenToResumeId);

        var write = Assert.Single(resumeClient.Writes);
        Assert.Equal(ForEditId, write.ResumeId);
    }

    [Fact]
    public async Task Tailor_UnknownMessageId_ReturnsNotFound()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient, seedPosting: false).CreateClient();

        var response = await client.PostAsync("/tailor?messageId=unknown-id", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_TailoringModelUnavailable_ReturnsServiceUnavailable_NoWrites()
    {
        var tailor = new FakeResumeTailor(new TailoringModelUnavailableException("boom"));
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_TailoringOutputInvalid_ReturnsBadGateway_NoWrites()
    {
        var tailor = new FakeResumeTailor(new TailoringOutputInvalidException("boom"));
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_ValidationException_ReturnsUnprocessableEntity_NoWrites()
    {
        var tailor = new FakeResumeTailor(new ResumeTailoringValidationException("boom"));
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_WriteConfigurationUnsafe_ReturnsInternalServerError_NoWritesAndNoModelCall()
    {
        var tailor = new FakeResumeTailor(MakeTailored());
        var resumeClient = new FakeResumeClient();
        // misconfigured: ForEditResumeId equals the configured base id
        var client = CreateFactory(tailor, resumeClient, forEditId: BaseId).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
        Assert.Equal(0, tailor.CallCount); // destination validated before the tailoring call
    }

    [Fact]
    public async Task Tailor_ReziAuthenticationRequired_ReturnsUnauthorized_NoWrites()
    {
        var tailor = new FakeResumeTailor(new ReziAuthenticationRequiredException());
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task Tailor_ReziToolCallFailure_ReturnsBadGateway_NoWrites()
    {
        var tailor = new FakeResumeTailor(new ReziToolCallException("write_resume", "invalid resume id"));
        var resumeClient = new FakeResumeClient();
        var client = CreateFactory(tailor, resumeClient).CreateClient();

        var response = await client.PostAsync($"/tailor?messageId={MessageId}&commit=true", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(resumeClient.Writes);
    }
}
