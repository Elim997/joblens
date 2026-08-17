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
        ["JobLens:ScoringTemplates:0:Name"] = "General",
        ["JobLens:ScoringTemplates:0:Profile"] = "dummy profile",
        ["Postgres:ConnectionString"] = "Host=dummy;Database=dummy;Username=dummy;Password=dummy",
        ["Gemini:ApiKey"] = "dummy-gemini-key",
        ["Llm:BaseUrl"] = "http://localhost:20128/v1",
        ["Llm:ApiKey"] = "dummy-llm-key",
        ["Llm:ScoringModel"] = "coding-fallback",
        ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
    };

    [Fact]
    public void ValidateRequiredConfig_AllRequiredSettingsPresentWithDummyValues_DoesNotThrow()
    {
        var config = BuildConfig(AllRequiredSettingsPresent);

        var exception = Record.Exception(() => Program.ValidateRequiredConfig(config));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("JobLens:MessagesDbPath", "MessagesDbPath")]
    [InlineData("Postgres:ConnectionString", "Postgres:ConnectionString")]
    [InlineData("Gemini:ApiKey", "Gemini:ApiKey")]
    [InlineData("Llm:BaseUrl", "Llm:BaseUrl")]
    [InlineData("Llm:ApiKey", "Llm:ApiKey")]
    [InlineData("Llm:ScoringModel", "Llm:ScoringModel")]
    [InlineData("Llm:TailoringModel", "Llm:TailoringModel")]
    public void ValidateRequiredConfig_BlankRequiredSetting_Throws(string key, string expectedMessage)
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent) { [key] = "   " };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));

        Assert.Contains(expectedMessage, exception.Message);
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
    public void ValidateRequiredConfig_MissingScoringTemplates_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent);
        values.Remove("JobLens:ScoringTemplates:0:Name");
        values.Remove("JobLens:ScoringTemplates:0:Profile");
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));
        Assert.Contains("ScoringTemplates", exception.Message);
    }


    [Theory]
    [InlineData("Name")]
    [InlineData("Profile")]
    public void ValidateRequiredConfig_BlankScoringTemplateField_Throws(string field)
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent)
        {
            [$"JobLens:ScoringTemplates:0:{field}"] = "   ",
        };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));

        Assert.Contains("non-empty Name and Profile", exception.Message);
    }

    [Fact]
    public void ValidateRequiredConfig_DuplicateScoringTemplateNames_Throws()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent)
        {
            ["JobLens:ScoringTemplates:1:Name"] = " general ",
            ["JobLens:ScoringTemplates:1:Profile"] = "another profile",
        };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));

        Assert.Contains("names must be unique", exception.Message);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://localhost/v1")]
    [InlineData("://not-a-uri")]
    public void ValidateRequiredConfig_InvalidLlmBaseUrl_Throws(string baseUrl)
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent) { ["Llm:BaseUrl"] = baseUrl };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));

        Assert.Contains("absolute HTTP or HTTPS URI", exception.Message);
    }

    [Theory]
    [InlineData("coding-fallback")]
    [InlineData(" CODING-FALLBACK ")]
    public void ValidateRequiredConfig_TailoringUsesScoringFallback_Throws(string tailoringModel)
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent)
        {
            ["Llm:TailoringModel"] = tailoringModel,
        };
        var config = BuildConfig(values);

        var exception = Assert.Throws<InvalidOperationException>(() => Program.ValidateRequiredConfig(config));

        Assert.Contains("dedicated model", exception.Message);
    }

    [Fact]
    public void ValidateRequiredConfig_DistinctNonFallbackModels_DoesNotThrow()
    {
        var values = new Dictionary<string, string?>(AllRequiredSettingsPresent)
        {
            ["Llm:ScoringModel"] = "scoring-model",
            ["Llm:TailoringModel"] = "tailoring-model",
        };
        var config = BuildConfig(values);

        var exception = Record.Exception(() => Program.ValidateRequiredConfig(config));

        Assert.Null(exception);
    }
}
