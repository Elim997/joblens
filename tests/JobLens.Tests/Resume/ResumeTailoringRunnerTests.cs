using JobLens.Core.Configuration;
using JobLens.Core.Parsing;
using JobLens.Core.Resume;
using JobLens.Tests.Pipeline;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Resume;

public class ResumeTailoringRunnerTests
{
    private const string ForEditId = "for-edit-id";
    private const string BaseId = "base-id";
    private const string MessageId = "msg-1";

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

    // A structurally well-typed ValidatedTailoredResume (only constructible internally/by tests -
    // see InternalsVisibleTo) whose final Experience omits an id present in
    // OriginalExperienceIds. Proves the runner's own ValidateComplete gate - not just whatever
    // Validate() enforced when the result was first produced - stands between a tailor result and
    // both a preview response and a write.
    private static ValidatedTailoredResume MakeIncompleteTailored() => new(
        new BaseSelection(BaseId, "QA Automation Developer", "Matches the QA posting."),
        "Rewritten summary.",
        [], // missing exp1 entirely
        [new TailoredSkillItem("sk1", "Rewritten skill.")],
        "Emphasized QA automation experience.",
        ["exp1"],
        ["sk1"]);

    private static IOptions<ReziOptions> CreateOptions(string forEditId, string baseId = BaseId) => Options.Create(new ReziOptions
    {
        BaseResumes = [new BaseResumeConfig { Name = "QA Automation Developer", Id = baseId }],
        ForEditResumeId = forEditId,
    });

    private static (ResumeTailoringRunner Runner, FakeResumeClient ResumeClient, FakeResumeTailor Tailor) CreateRunner(
        string forEditId = ForEditId,
        bool seedPosting = true,
        ValidatedTailoredResume? tailored = null,
        string baseId = BaseId)
    {
        var datastore = new FakeDatastore();
        if (seedPosting)
            datastore.Seed(MessageId, MakePosting(), [1f, 0f, 0f]);

        var resumeClient = new FakeResumeClient();
        var tailor = new FakeResumeTailor(tailored ?? MakeTailored());
        var runner = new ResumeTailoringRunner(datastore, tailor, resumeClient, CreateOptions(forEditId, baseId));
        return (runner, resumeClient, tailor);
    }

    private static (ResumeTailoringRunner Runner, FakeResumeClient ResumeClient, FakeResumeTailor Tailor) CreateRunnerWithThrowingTailor(
        Exception exception, string forEditId = ForEditId)
    {
        var datastore = new FakeDatastore();
        datastore.Seed(MessageId, MakePosting(), [1f, 0f, 0f]);

        var resumeClient = new FakeResumeClient();
        var tailor = new FakeResumeTailor(exception);
        var runner = new ResumeTailoringRunner(datastore, tailor, resumeClient, CreateOptions(forEditId));
        return (runner, resumeClient, tailor);
    }

    [Fact]
    public async Task RunAsync_UnknownMessageId_ReturnsNull()
    {
        var (runner, _, _) = CreateRunner(seedPosting: false);

        var result = await runner.RunAsync("unknown-id", commit: false);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_CommitFalse_ReturnsPreviewAndWritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunner();

        var result = await runner.RunAsync(MessageId, commit: false);

        Assert.NotNull(result);
        Assert.False(result.Committed);
        Assert.Null(result.WrittenToResumeId);
        Assert.Equal(BaseId, result.BaseResumeId);
        Assert.Equal("Rewritten summary.", result.Summary);
        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_WritesOnlyTheForEditSlot()
    {
        var (runner, resumeClient, _) = CreateRunner();

        var result = await runner.RunAsync(MessageId, commit: true);

        Assert.NotNull(result);
        Assert.True(result.Committed);
        Assert.Equal(ForEditId, result.WrittenToResumeId);

        var write = Assert.Single(resumeClient.Writes);
        Assert.Equal(ForEditId, write.ResumeId);
        Assert.Equal("Rewritten summary.", write.Resume["data"]!["summary"]!["summary"]!.GetValue<string>());
        Assert.Equal("Rewritten bullet.", write.Resume["data"]!["experience"]!["exp1"]!["description"]!.GetValue<string>());
        Assert.Equal("Rewritten skill.", write.Resume["data"]!["skills"]!["sk1"]!["skill"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdMatchesABaseId_ThrowsBeforeAnyModelCallAndWritesNothing()
    {
        var (runner, resumeClient, tailor) = CreateRunner(forEditId: BaseId); // misconfigured: same as the base id

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(0, tailor.CallCount); // destination validated before the tailoring call, not after
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdMatchesABaseId_CaseInsensitive_ThrowsAndWritesNothing()
    {
        var (runner, resumeClient, tailor) = CreateRunner(forEditId: BaseId.ToUpperInvariant());

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(0, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdMissing_ThrowsBeforeAnyModelCallAndWritesNothing()
    {
        var (runner, resumeClient, tailor) = CreateRunner(forEditId: "");

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(0, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdBlank_ThrowsBeforeAnyModelCallAndWritesNothing()
    {
        var (runner, resumeClient, tailor) = CreateRunner(forEditId: "   ");

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(0, tailor.CallCount);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdIsTrimmedBeforeWrite()
    {
        var (runner, resumeClient, _) = CreateRunner(forEditId: $"  {ForEditId}  ");

        var result = await runner.RunAsync(MessageId, commit: true);

        Assert.Equal(ForEditId, result!.WrittenToResumeId);
        Assert.Equal(ForEditId, Assert.Single(resumeClient.Writes).ResumeId);
    }

    [Fact]
    public async Task RunAsync_CommitFalse_MalformedTailoredResult_ThrowsAndWritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunner(tailored: MakeIncompleteTailored());

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() => runner.RunAsync(MessageId, commit: false));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_MalformedTailoredResult_ThrowsAndWritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunner(tailored: MakeIncompleteTailored());

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_TailorThrowsModelUnavailable_WritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunnerWithThrowingTailor(new TailoringModelUnavailableException("boom"));

        await Assert.ThrowsAsync<TailoringModelUnavailableException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_TailorThrowsOutputInvalid_WritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunnerWithThrowingTailor(new TailoringOutputInvalidException("boom"));

        await Assert.ThrowsAsync<TailoringOutputInvalidException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_TailorThrowsValidationException_WritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunnerWithThrowingTailor(new ResumeTailoringValidationException("boom"));

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_PropagatesAndWritesNothing()
    {
        var (runner, resumeClient, _) = CreateRunner();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(MessageId, commit: true, cts.Token));

        Assert.Empty(resumeClient.Writes);
    }
}
