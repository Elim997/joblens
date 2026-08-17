using JobLens.Core.Resume;
using JobLens.Core.Storage;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace JobLens.Tests.Storage;

[Trait("Category", "Integration")]
public class PgvectorTailoredDraftStoreIntegrationTests : IAsyncLifetime
{
    private readonly List<string> _messageIds = [];
    private NpgsqlDataSource _dataSource = null!;
    private PgvectorTailoredDraftStore _store = null!;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();
        var connectionString = config["Postgres:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Missing Postgres:ConnectionString - run SETUP.md steps 3 and 5.");

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _store = new PgvectorTailoredDraftStore(_dataSource);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_messageIds.Count > 0)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM tailored_drafts WHERE message_id = ANY(@messageIds);";
            command.Parameters.AddWithValue("messageIds", _messageIds.ToArray());
            await command.ExecuteNonQueryAsync();
        }

        await _dataSource.DisposeAsync();
    }

    private NewTailoredDraft MakeDraft(
        string messageId,
        int score = 92,
        string selectedTemplate = "QA Automation Developer",
        string baseResumeId = "qa-base-id",
        string summary = "Stored summary.") =>
        new(
            messageId,
            score,
            selectedTemplate,
            baseResumeId,
            "QA Automation Developer",
            summary,
            [
                new TailoredExperienceItem("exp-1", "First stored experience."),
                new TailoredExperienceItem("exp-2", "Second stored experience."),
            ],
            [
                new TailoredSkillItem("skill-1", "Playwright"),
                new TailoredSkillItem("skill-2", "API testing"),
            ],
            "Stored rewrite rationale.");

    private string NewMessageId(string scenario)
    {
        var messageId = $"test-tailored-draft-{scenario}-{Guid.NewGuid():N}";
        _messageIds.Add(messageId);
        return messageId;
    }

    [Fact]
    public async Task CreateOrGetAsync_RoundTripsJsonAndReturnsExistingRowUnchanged()
    {
        var messageId = NewMessageId("roundtrip");
        var firstInput = MakeDraft(messageId);

        var first = await _store.CreateOrGetAsync(firstInput);
        var second = await _store.CreateOrGetAsync(
            MakeDraft(messageId, score: 1, summary: "Must not replace the first row."));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(92, second.Score);
        Assert.Equal("Stored summary.", second.Summary);
        Assert.Equal(firstInput.Experience, second.Experience);
        Assert.Equal(firstInput.Skills, second.Skills);
        Assert.Equal(firstInput.RewriteRationale, second.RewriteRationale);
        Assert.Equal(TailoredDraftStatus.Draft, second.Status);
        Assert.Null(second.ExportedAt);

        var found = await _store.FindAsync(
            messageId,
            firstInput.SelectedTemplate,
            firstInput.BaseResumeId);
        var byId = await _store.GetByIdAsync(first.Id);

        // TailoredDraft's synthesized record equality compares Experience/Skills (typed as
        // IReadOnlyList<T>) via reference equality, since List<T> doesn't override Equals - so
        // Assert.Equal(first, found) would always fail once the lists come from separate DB
        // deserializations even when structurally identical. Compare fields individually instead;
        // xUnit's collection-aware comparer does structural comparison for the list fields.
        foreach (var other in new[] { found, byId })
        {
            Assert.NotNull(other);
            Assert.Equal(first.Id, other.Id);
            Assert.Equal(first.MessageId, other.MessageId);
            Assert.Equal(first.Score, other.Score);
            Assert.Equal(first.SelectedTemplate, other.SelectedTemplate);
            Assert.Equal(first.BaseResumeId, other.BaseResumeId);
            Assert.Equal(first.BaseResumeName, other.BaseResumeName);
            Assert.Equal(first.Summary, other.Summary);
            Assert.Equal(first.Experience, other.Experience);
            Assert.Equal(first.Skills, other.Skills);
            Assert.Equal(first.RewriteRationale, other.RewriteRationale);
            Assert.Equal(first.Status, other.Status);
            Assert.Equal(first.CreatedAt, other.CreatedAt);
            Assert.Equal(first.ExportedAt, other.ExportedAt);
        }
    }

    [Fact]
    public async Task CreateOrGetAsync_ConcurrentDuplicateRequestsConvergeOnOneRow()
    {
        var messageId = NewMessageId("concurrent");
        var input = MakeDraft(messageId);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _store.CreateOrGetAsync(input)));

        Assert.Single(results.Select(draft => draft.Id).Distinct());

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM tailored_drafts WHERE message_id = @messageId;";
        command.Parameters.AddWithValue("messageId", messageId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ListAsync_IsNewestFirstAndMarkExportedOnlyChangesExportMetadata()
    {
        var firstInput = MakeDraft(NewMessageId("older"), summary: "Older summary.");
        var secondInput = MakeDraft(
            NewMessageId("newer"),
            baseResumeId: "qa-base-id-2",
            summary: "Newer summary.");
        var first = await _store.CreateOrGetAsync(firstInput);
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var second = await _store.CreateOrGetAsync(secondInput);

        var listed = await _store.ListAsync();
        var relevant = listed
            .Where(draft => draft.Id == first.Id || draft.Id == second.Id)
            .ToList();
        Assert.Equal([second.Id, first.Id], relevant.Select(draft => draft.Id));

        var exported = await _store.MarkExportedAsync(first.Id);

        Assert.NotNull(exported);
        Assert.Equal(TailoredDraftStatus.ExportedToRezi, exported.Status);
        Assert.NotNull(exported.ExportedAt);
        Assert.Equal(first.Id, exported.Id);
        Assert.Equal(first.MessageId, exported.MessageId);
        Assert.Equal(first.Score, exported.Score);
        Assert.Equal(first.SelectedTemplate, exported.SelectedTemplate);
        Assert.Equal(first.BaseResumeId, exported.BaseResumeId);
        Assert.Equal(first.BaseResumeName, exported.BaseResumeName);
        Assert.Equal(first.Summary, exported.Summary);
        Assert.Equal(first.Experience, exported.Experience);
        Assert.Equal(first.Skills, exported.Skills);
        Assert.Equal(first.RewriteRationale, exported.RewriteRationale);
        Assert.Equal(first.CreatedAt, exported.CreatedAt);
        Assert.Null(await _store.MarkExportedAsync("unknown-draft-id"));
    }
}
