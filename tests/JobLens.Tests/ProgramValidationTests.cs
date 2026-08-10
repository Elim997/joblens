using Microsoft.Extensions.Configuration;

namespace JobLens.Tests;

// Hermetic, direct unit tests of Program.ValidateRequiredConfig: pure IConfiguration in,
// throw-or-not out. No WebApplicationFactory, no real user-secrets - proves the pass/fail
// behavior with in-memory config alone, independent of what's on this machine.
public class ProgramValidationTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static readonly IDictionary<string, string?> AllRequiredSettingsPresent = new Dictionary<string, string?>
    {
        ["JobLens:MessagesDbPath"] = "C:/dummy/messages.db",
        ["JobLens:GroupChatJids:0"] = "dummy@g.us",
        ["Postgres:ConnectionString"] = "Host=dummy;Database=dummy;Username=dummy;Password=dummy",
        ["Gemini:ApiKey"] = "dummy-key",
    };

    [Fact]
    public void ValidateRequiredConfig_AllRequiredSettingsPresentWithDummyValues_DoesNotThrow()
    {
        var config = BuildConfig(AllRequiredSettingsPresent);

        var exception = Record.Exception(() => Program.ValidateRequiredConfig(config));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateRequiredConfig_BlankMessagesDbPath_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent) { ["JobLens:MessagesDbPath"] = "" };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));
        Assert.Contains("MessagesDbPath", exception.Message);
    }

    [Fact]
    public void ValidateRequiredConfig_MissingGroupChatJids_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent);
        values.Remove("JobLens:GroupChatJids:0");
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));
        Assert.Contains("GroupChatJids", exception.Message);
    }

    [Fact]
    public void ValidateRequiredConfig_BlankPostgresConnectionString_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent) { ["Postgres:ConnectionString"] = "   " };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));
        Assert.Contains("Postgres:ConnectionString", exception.Message);
    }

    [Fact]
    public void ValidateRequiredConfig_BlankGeminiApiKey_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent) { ["Gemini:ApiKey"] = "" };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));
        Assert.Contains("Gemini:ApiKey", exception.Message);
    }
}
