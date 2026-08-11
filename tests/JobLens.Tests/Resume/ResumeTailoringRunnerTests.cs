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

    private static TailoredResume MakeTailored() => new(
        new BaseSelection(BaseId, "QA Automation Developer", "Matches the QA posting."),
        "Rewritten summary.",
        [new TailoredExperienceItem("exp1", "Rewritten bullet.")],
        [new TailoredSkillItem("sk1", "Rewritten skill.")],
        "Emphasized QA automation experience.");

    private static IOptions<ReziOptions> CreateOptions(string forEditId) => Options.Create(new ReziOptions
    {
        BaseResumes = [new BaseResumeConfig { Name = "QA Automation Developer", Id = BaseId }],
        ForEditResumeId = forEditId,
    });

    private static (ResumeTailoringRunner Runner, FakeResumeClient ResumeClient) CreateRunner(
        string forEditId = ForEditId, bool seedPosting = true)
    {
        var datastore = new FakeDatastore();
        if (seedPosting)
            datastore.Seed(MessageId, MakePosting(), [1f, 0f, 0f]);

        var resumeClient = new FakeResumeClient();
        var tailor = new FakeResumeTailor(MakeTailored());
        var runner = new ResumeTailoringRunner(datastore, tailor, resumeClient, CreateOptions(forEditId));
        return (runner, resumeClient);
    }

    [Fact]
    public async Task RunAsync_UnknownMessageId_ReturnsNull()
    {
        var (runner, _) = CreateRunner(seedPosting: false);

        var result = await runner.RunAsync("unknown-id", commit: false);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_CommitFalse_ReturnsPreviewAndWritesNothing()
    {
        var (runner, resumeClient) = CreateRunner();

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
        var (runner, resumeClient) = CreateRunner();

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
    public async Task RunAsync_CommitTrue_ForEditIdMatchesABaseId_ThrowsAndWritesNothing()
    {
        var (runner, resumeClient) = CreateRunner(forEditId: BaseId); // misconfigured: same as the base id

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }

    [Fact]
    public async Task RunAsync_CommitTrue_ForEditIdMissing_ThrowsAndWritesNothing()
    {
        var (runner, resumeClient) = CreateRunner(forEditId: "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(MessageId, commit: true));

        Assert.Empty(resumeClient.Writes);
    }
}
