using JobLens.Core.Configuration;
using JobLens.Core.Diagnostics;
using JobLens.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Diagnostics;

public class SourceFreshnessProbeTests
{
    [Fact]
    public async Task GetLatestConfiguredGroupMessageAsync_IgnoresNewerUnconfiguredGroups()
    {
        using var database = new SqliteFixtureDb();
        database.InsertMessage("old", "configured@g.us", "sender", "job", "2026-08-20T08:00:00+00:00", null);
        database.InsertMessage("new", "configured@g.us", "sender", "job", "2026-08-21T09:00:00+00:00", null);
        database.InsertMessage("newest", "other@g.us", "sender", "job", "2026-08-21T11:00:00+00:00", null);
        var probe = CreateProbe(database.Path, "configured@g.us");

        var latest = await probe.GetLatestConfiguredGroupMessageAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-08-21T09:00:00+00:00"), latest);
    }

    [Fact]
    public async Task GetLatestConfiguredGroupMessageAsync_MultipleConfiguredGroups_ReturnsNewestAcrossThem()
    {
        using var database = new SqliteFixtureDb();
        database.InsertMessage("first", "first@g.us", "sender", "job", "2026-08-20T08:00:00+00:00", null);
        database.InsertMessage("second", "second@g.us", "sender", "job", "2026-08-21T09:00:00+00:00", null);
        database.InsertMessage("unconfigured", "other@g.us", "sender", "job", "2026-08-21T11:00:00+00:00", null);
        var probe = CreateProbe(database.Path, "first@g.us", "second@g.us");

        var latest = await probe.GetLatestConfiguredGroupMessageAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-08-21T09:00:00+00:00"), latest);
    }

    [Fact]
    public async Task GetLatestConfiguredGroupMessageAsync_NoConfiguredRows_ReturnsNull()
    {
        using var database = new SqliteFixtureDb();
        database.InsertMessage("other", "other@g.us", "sender", "job", "2026-08-21T11:00:00+00:00", null);
        var probe = CreateProbe(database.Path, "configured@g.us");

        var latest = await probe.GetLatestConfiguredGroupMessageAsync(CancellationToken.None);

        Assert.Null(latest);
    }

    [Fact]
    public async Task GetLatestConfiguredGroupMessageAsync_PerformsNoWrites()
    {
        using var database = new SqliteFixtureDb();
        database.InsertMessage("one", "configured@g.us", "sender", "job", "2026-08-21T09:00:00+00:00", null);
        var probe = CreateProbe(database.Path, "configured@g.us");

        await probe.GetLatestConfiguredGroupMessageAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database.Path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static SqliteSourceFreshnessProbe CreateProbe(string path, params string[] groupChatJids) =>
        new(Options.Create(new JobLensOptions
        {
            MessagesDbPath = path,
            GroupChatJids = groupChatJids,
        }));
}
