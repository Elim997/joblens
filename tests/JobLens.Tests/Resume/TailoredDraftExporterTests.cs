using JobLens.Core.Configuration;
using JobLens.Core.Resume;
using JobLens.Core.Storage;
using JobLens.Tests.Storage;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Resume;

public class TailoredDraftExporterTests
{
    private const string BaseId = "base-read-only";
    private const string ForEditId = "for-edit-only";

    private static TailoredDraft MakeDraft(
        string id = "draft-1",
        string summary = "Stored summary.",
        IReadOnlyList<TailoredExperienceItem>? experience = null,
        IReadOnlyList<TailoredSkillItem>? skills = null,
        string rationale = "Stored rationale.",
        TailoredDraftStatus status = TailoredDraftStatus.Draft,
        DateTimeOffset? exportedAt = null) =>
        new(
            id,
            $"message-{id}",
            92,
            "QA Automation Developer",
            BaseId,
            "QA Automation Developer",
            summary,
            experience ?? [new TailoredExperienceItem("exp-1", "Stored experience.")],
            skills ?? [new TailoredSkillItem("skill-1", "Stored skill.")],
            rationale,
            status,
            DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            exportedAt);

    private static TailoredDraftExporter CreateExporter(
        FakeTailoredDraftStore store,
        FakeResumeClient resumeClient,
        string forEditId = ForEditId,
        params string[] baseIds) =>
        new(
            store,
            resumeClient,
            Options.Create(new ReziOptions
            {
                ForEditResumeId = forEditId,
                BaseResumes = (baseIds.Length == 0 ? [BaseId] : baseIds)
                    .Select((id, index) => new BaseResumeConfig
                    {
                        Id = id,
                        Name = $"Base {index}",
                    })
                    .ToList(),
            }));

    [Fact]
    public async Task ExportAsync_WritesExactStoredContentOnlyToForEditAndMarksExportedAfterSuccess()
    {
        var original = MakeDraft();
        var store = new FakeTailoredDraftStore();
        store.Seed(original);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient);

        var result = await exporter.ExportAsync(original.Id);

        Assert.NotNull(result);
        Assert.Equal(TailoredDraftStatus.ExportedToRezi, result.Status);
        Assert.NotNull(result.ExportedAt);
        Assert.Equal(original with
        {
            Status = result.Status,
            ExportedAt = result.ExportedAt,
        }, result);
        Assert.Empty(resumeClient.Reads);
        var write = Assert.Single(resumeClient.Writes);
        Assert.Equal(ForEditId, write.ResumeId);
        Assert.Equal(
            original.Summary,
            write.Resume["data"]?["summary"]?["summary"]?.GetValue<string>());
        Assert.Equal(
            original.Experience[0].Description,
            write.Resume["data"]?["experience"]?["exp-1"]?["description"]?.GetValue<string>());
        Assert.Equal(
            original.Skills[0].Skill,
            write.Resume["data"]?["skills"]?["skill-1"]?["skill"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExportAsync_UnknownDraftReturnsNullWithoutWrite()
    {
        var store = new FakeTailoredDraftStore();
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient);

        var result = await exporter.ExportAsync("unknown");

        Assert.Null(result);
        Assert.Empty(resumeClient.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportAsync_MissingWriteDestinationFailsBeforeWrite(string destination)
    {
        var draft = MakeDraft();
        var store = new FakeTailoredDraftStore();
        store.Seed(draft);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient, destination);

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() =>
            exporter.ExportAsync(draft.Id));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(store.Drafts).Status);
    }

    [Fact]
    public async Task ExportAsync_DestinationMatchingAnyConfiguredBaseFailsBeforeWrite()
    {
        var draft = MakeDraft();
        var store = new FakeTailoredDraftStore();
        store.Seed(draft);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(
            store,
            resumeClient,
            "  OTHER-BASE  ",
            BaseId,
            "other-base");

        await Assert.ThrowsAsync<ResumeWriteConfigurationException>(() =>
            exporter.ExportAsync(draft.Id));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(store.Drafts).Status);
    }

    public static TheoryData<TailoredDraft> InvalidDrafts => new()
    {
        MakeDraft(summary: "   "),
        MakeDraft(rationale: ""),
        MakeDraft(experience: [new TailoredExperienceItem("", "Text.")]),
        MakeDraft(experience: [
            new TailoredExperienceItem("duplicate", "First."),
            new TailoredExperienceItem("duplicate", "Second.")]),
        MakeDraft(experience: [new TailoredExperienceItem("exp-1", "   ")]),
        MakeDraft(skills: [new TailoredSkillItem("", "Skill.")]),
        MakeDraft(skills: [
            new TailoredSkillItem("duplicate", "First."),
            new TailoredSkillItem("duplicate", "Second.")]),
        MakeDraft(skills: [new TailoredSkillItem("skill-1", "   ")]),
    };

    [Theory]
    [MemberData(nameof(InvalidDrafts))]
    public async Task ExportAsync_InvalidStoredContentFailsBeforeWrite(TailoredDraft draft)
    {
        var store = new FakeTailoredDraftStore();
        store.Seed(draft);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient);

        await Assert.ThrowsAsync<ResumeTailoringValidationException>(() =>
            exporter.ExportAsync(draft.Id));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(store.Drafts).Status);
        Assert.Null(Assert.Single(store.Drafts).ExportedAt);
    }

    [Fact]
    public async Task ExportAsync_ReziWriteFailureDoesNotMarkDraftExported()
    {
        var draft = MakeDraft();
        var store = new FakeTailoredDraftStore();
        store.Seed(draft);
        var resumeClient = new FakeResumeClient
        {
            WriteException = new ReziToolCallException("write_resume", "failed"),
        };
        var exporter = CreateExporter(store, resumeClient);

        await Assert.ThrowsAsync<ReziToolCallException>(() =>
            exporter.ExportAsync(draft.Id));

        Assert.Empty(resumeClient.Writes);
        var unchanged = Assert.Single(store.Drafts);
        Assert.Equal(TailoredDraftStatus.Draft, unchanged.Status);
        Assert.Null(unchanged.ExportedAt);
    }

    [Fact]
    public async Task ExportAsync_DraftAThenBThenA_UsesEachImmutableStoredSnapshot()
    {
        var draftA = MakeDraft(
            id: "a",
            summary: "Summary A.",
            experience: [new TailoredExperienceItem("exp-a", "Experience A.")],
            skills: [new TailoredSkillItem("skill-a", "Skill A.")]);
        var draftB = MakeDraft(
            id: "b",
            summary: "Summary B.",
            experience: [new TailoredExperienceItem("exp-b", "Experience B.")],
            skills: [new TailoredSkillItem("skill-b", "Skill B.")]);
        var store = new FakeTailoredDraftStore();
        store.Seed(draftA);
        store.Seed(draftB);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient);

        await exporter.ExportAsync(draftA.Id);
        await exporter.ExportAsync(draftB.Id);
        await exporter.ExportAsync(draftA.Id);

        Assert.Equal(3, resumeClient.Writes.Count);
        Assert.All(resumeClient.Writes, write => Assert.Equal(ForEditId, write.ResumeId));
        Assert.Equal(
            ["Summary A.", "Summary B.", "Summary A."],
            resumeClient.Writes.Select(write =>
                write.Resume["data"]?["summary"]?["summary"]?.GetValue<string>()));
        Assert.Equal("Experience A.", draftA.Experience[0].Description);
        Assert.Equal("Experience B.", draftB.Experience[0].Description);
        Assert.All(store.Drafts, draft =>
        {
            Assert.Equal(TailoredDraftStatus.ExportedToRezi, draft.Status);
            Assert.NotNull(draft.ExportedAt);
        });
    }

    [Fact]
    public async Task ExportAsync_CancellationPropagatesWithoutWriteOrStatusChange()
    {
        var draft = MakeDraft();
        var store = new FakeTailoredDraftStore();
        store.Seed(draft);
        var resumeClient = new FakeResumeClient();
        var exporter = CreateExporter(store, resumeClient);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(draft.Id, cancellation.Token));

        Assert.Empty(resumeClient.Writes);
        Assert.Equal(TailoredDraftStatus.Draft, Assert.Single(store.Drafts).Status);
    }
}
