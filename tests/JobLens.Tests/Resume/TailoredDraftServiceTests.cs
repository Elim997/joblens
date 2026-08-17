using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Core.Storage;
using JobLens.Tests.Pipeline;
using JobLens.Tests.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Resume;

public class TailoredDraftServiceTests
{
    private const string MessageId = "message-1";
    private const string BaseId = "qa-base-id";
    private const string TemplateName = "QA Automation Developer";

    private static JobPosting MakePosting() =>
        new(
            "QA Automation Engineer",
            "Acme",
            "Tel Aviv",
            "QA",
            "https://example.com/job",
            "- Playwright\n- API testing");

    private static ValidatedTailoredResume MakeTailored(
        string baseId = BaseId,
        string baseName = TemplateName) =>
        new(
            new BaseSelection(baseId, baseName, "Selected from persisted scoring metadata."),
            "QA automation engineer focused on reliable web and API testing.",
            [new TailoredExperienceItem("exp-1", "Built Playwright regression suites.")],
            [new TailoredSkillItem("skill-1", "Playwright, API testing")],
            "Emphasized evidence relevant to the scored posting.",
            ["exp-1"],
            ["skill-1"]);

    private static TailoredDraftService CreateService(
        FakeDatastore datastore,
        FakeResumeTailor tailor,
        FakeTailoredDraftStore draftStore,
        params BaseResumeConfig[] bases) =>
        new(
            datastore,
            tailor,
            draftStore,
            Options.Create(new ReziOptions { BaseResumes = bases.ToList() }));

    private static BaseResumeConfig MakeBase(
        string id = BaseId,
        string name = TemplateName) =>
        new() { Id = id, Name = name };

    [Fact]
    public async Task CreateOrGetAsync_UsesPersistedTemplateAndPersistsValidatedSnapshots()
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            93,
            "Strong QA fit.",
            $"  {TemplateName.ToUpperInvariant()}  ");
        var tailored = MakeTailored();
        var tailor = new FakeResumeTailor(tailored);
        var store = new FakeTailoredDraftStore();
        var service = CreateService(
            datastore,
            tailor,
            store,
            MakeBase(name: $" {TemplateName} "));

        var result = await service.CreateOrGetAsync(MessageId);

        Assert.NotNull(result);
        Assert.Equal(MessageId, result.MessageId);
        Assert.Equal(93, result.Score);
        Assert.Equal($"  {TemplateName.ToUpperInvariant()}  ", result.SelectedTemplate);
        Assert.Equal(BaseId, result.BaseResumeId);
        Assert.Equal(TemplateName, result.BaseResumeName);
        Assert.Equal(tailored.Summary, result.Summary);
        Assert.Equal(tailored.Experience, result.Experience);
        Assert.Equal(tailored.Skills, result.Skills);
        Assert.Equal(tailored.RewriteRationale, result.RewriteRationale);
        Assert.Equal(TailoredDraftStatus.Draft, result.Status);
        Assert.Null(result.ExportedAt);
        Assert.Equal(BaseId, tailor.LastBaseResumeId);
        Assert.Equal(TemplateName, tailor.LastBaseResumeName);
        Assert.Equal(1, tailor.CallCount);
        Assert.Equal(1, store.CreateOrGetCallCount);
    }

    [Fact]
    public async Task CreateOrGetAsync_ExistingMatchingDraftSkipsTailoringModel()
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            93,
            "Strong QA fit.",
            TemplateName);
        var existing = new TailoredDraft(
            "existing-draft",
            MessageId,
            91,
            TemplateName,
            BaseId,
            TemplateName,
            "Existing summary.",
            [new TailoredExperienceItem("exp-1", "Existing experience.")],
            [new TailoredSkillItem("skill-1", "Existing skill.")],
            "Existing rationale.",
            TailoredDraftStatus.ExportedToRezi,
            DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T11:00:00Z"));
        var store = new FakeTailoredDraftStore();
        store.Seed(existing);
        var tailor = new FakeResumeTailor(new InvalidOperationException("must not be called"));
        var service = CreateService(datastore, tailor, store, MakeBase());

        var result = await service.CreateOrGetAsync(MessageId);

        Assert.Same(existing, result);
        Assert.Equal(0, tailor.CallCount);
        Assert.Equal(0, store.CreateOrGetCallCount);
        Assert.Single(store.Drafts);
    }

    [Fact]
    public async Task CreateOrGetAsync_UnknownPostingReturnsNullWithoutSideEffects()
    {
        var datastore = new FakeDatastore();
        var tailor = new FakeResumeTailor(MakeTailored());
        var store = new FakeTailoredDraftStore();
        var service = CreateService(datastore, tailor, store, MakeBase());

        var result = await service.CreateOrGetAsync("unknown");

        Assert.Null(result);
        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(store.Drafts);
    }

    [Theory]
    [InlineData(null, 90)]
    [InlineData("", 90)]
    [InlineData("   ", 90)]
    [InlineData(TemplateName, null)]
    public async Task CreateOrGetAsync_MissingTrustedScoringMetadataRequiresRescore(
        string? selectedTemplate,
        int? score)
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            score,
            "Reasoning.",
            selectedTemplate);
        var tailor = new FakeResumeTailor(MakeTailored());
        var store = new FakeTailoredDraftStore();
        var service = CreateService(datastore, tailor, store, MakeBase());

        var exception = await Assert.ThrowsAsync<PostingNotScoredException>(() =>
            service.CreateOrGetAsync(MessageId));

        Assert.Contains("Rescore", exception.Message);
        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(store.Drafts);
    }

    [Theory]
    [InlineData("Historical Template", "Different Template", BaseId)]
    [InlineData(TemplateName, TemplateName, "   ")]
    public async Task CreateOrGetAsync_ConfigurationDriftFailsClosed(
        string selectedTemplate,
        string configuredName,
        string configuredId)
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            90,
            "Reasoning.",
            selectedTemplate);
        var tailor = new FakeResumeTailor(MakeTailored());
        var store = new FakeTailoredDraftStore();
        var service = CreateService(
            datastore,
            tailor,
            store,
            MakeBase(configuredId, configuredName));

        await Assert.ThrowsAsync<ScoringTemplateResumeMappingException>(() =>
            service.CreateOrGetAsync(MessageId));

        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(store.Drafts);
    }

    [Fact]
    public async Task CreateOrGetAsync_DefensiveValidationFailurePersistsNothing()
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            90,
            "Reasoning.",
            TemplateName);
        var malformed = new ValidatedTailoredResume(
            new BaseSelection(BaseId, TemplateName, "Trusted mapping."),
            "Summary.",
            [new TailoredExperienceItem("exp-1", "Valid text.")],
            [new TailoredSkillItem("invented", "Invented item.")],
            "Rationale.",
            ["exp-1"],
            ["skill-1"]);
        var tailor = new FakeResumeTailor(malformed);
        var store = new FakeTailoredDraftStore();
        var service = CreateService(datastore, tailor, store, MakeBase());

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() =>
            service.CreateOrGetAsync(MessageId));

        Assert.Equal(1, tailor.CallCount);
        Assert.Equal(0, store.CreateOrGetCallCount);
        Assert.Empty(store.Drafts);
    }

    [Fact]
    public async Task CreateOrGetAsync_CancellationPropagatesAndPersistsNothing()
    {
        var datastore = new FakeDatastore();
        datastore.SeedScored(
            MessageId,
            MakePosting(),
            [1f, 0f],
            90,
            "Reasoning.",
            TemplateName);
        var tailor = new FakeResumeTailor(MakeTailored());
        var store = new FakeTailoredDraftStore();
        var service = CreateService(datastore, tailor, store, MakeBase());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateOrGetAsync(MessageId, cancellation.Token));

        Assert.Equal(0, tailor.CallCount);
        Assert.Empty(store.Drafts);
    }
}
