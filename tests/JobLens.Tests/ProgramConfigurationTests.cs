using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace JobLens.Tests;

// Hermetic tests for the Milestone F1 configuration-loading fix: Program.RepositionBeforeEnvironmentVariables
// (the generic core), Program.InsertUserSecretsBeforeEnvironmentVariables (the real user-secrets wrapper),
// and Program.DescribeConfigurationSources (the startup diagnostic). See Program.cs for the full rationale -
// a Task Scheduler-launched process runs Production, under which WebApplication.CreateBuilder never loads
// user-secrets unless this fix is in place, and naively appending AddUserSecrets<Program>() would let a
// stale secrets.json value outrank a live environment variable.
public class ProgramConfigurationTests
{
    // Exercises InsertUserSecretsBeforeEnvironmentVariables against a REAL WebApplicationBuilder, not just
    // the generic algorithm in isolation - proves AddUserSecrets<Program>() actually appends exactly one
    // source, and that the real EnvironmentVariablesConfigurationSource CreateBuilder itself registers is
    // found and used as the pivot. WebApplicationOptions.ApplicationName must be set explicitly to
    // JobLens.Api's own assembly name: CreateBuilder's implicit Development-only user-secrets registration
    // is keyed off the entry assembly, which in a test process is the test host, not JobLens.Api - without
    // this, the Development branch below would silently behave like Production (no implicit secrets source
    // to reposition), and the two branches would no longer be testing two different starting states.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void InsertUserSecretsBeforeEnvironmentVariables_RealBuilder_ResultsInExactlyOneSecretsSourceBeforeEnvVars(
        string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        var sourcesBefore = builder.Configuration.Sources.Count;
        var secretsSourcesBefore = CountSecretsSources(builder.Configuration.Sources);

        Program.InsertUserSecretsBeforeEnvironmentVariables(builder.Configuration);

        var sources = builder.Configuration.Sources;
        var secretsSourceIndices = IndicesWhere(sources, source => source is JsonConfigurationSource { Path: "secrets.json" });
        Assert.Single(secretsSourceIndices);

        // WebApplicationBuilder can legitimately contain more than one EnvironmentVariablesConfigurationSource
        // (an internal "DOTNET_"-prefixed host-bootstrap layer plus the unprefixed app-level one that
        // CreateBuilder itself adds) - confirmed empirically, not assumed. Program.RepositionBeforeEnvironmentVariables
        // deliberately pivots on the LAST occurrence for exactly this reason (see its XML doc comment), so this
        // test targets the same one, not the only one.
        var envVarIndices = IndicesWhere(sources, source => source is EnvironmentVariablesConfigurationSource);
        Assert.NotEmpty(envVarIndices);
        var envVarIndex = envVarIndices[^1];
        Assert.Equal(envVarIndex - 1, secretsSourceIndices[0]);

        if (environmentName == "Development")
        {
            // CreateBuilder already added a user-secrets source implicitly - the fix repositions it
            // rather than adding a second one, so the total source count is unchanged and there was
            // exactly one secrets source present even before the fix ran.
            Assert.Equal(1, secretsSourcesBefore);
            Assert.Equal(sourcesBefore, sources.Count);
        }
        else
        {
            // No implicit user-secrets source outside Development - the fix adds exactly one, growing
            // the source count by one.
            Assert.Equal(0, secretsSourcesBefore);
            Assert.Equal(sourcesBefore + 1, sources.Count);
        }
    }

    // The direct regression test for the precedence bug itself: proves RepositionBeforeEnvironmentVariables
    // actually changes which value IConfiguration resolves, not just source-list order. Uses in-memory
    // stand-ins rather than a real secrets.json file or real process environment variables, so it's
    // hermetic and immune to parallel test execution mutating shared environment state - a real
    // EnvironmentVariablesConfigurationSource is still used as the pivot, proving the algorithm correctly
    // locates the actual type it's designed to detect in production; a later in-memory source stands in
    // for "a source configured after user-secrets" (environment variables and command-line args, in the
    // real order) to prove repositioning doesn't accidentally move the target past everything else.
    [Fact]
    public void RepositionBeforeEnvironmentVariables_ValuePrecedence_LaterSourceStillWinsOverRepositionedSource()
    {
        var configBuilder = new ConfigurationBuilder();
        var earlierSource = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["Llm:ApiKey"] = "from-earlier-source" },
        };
        var secretsStandIn = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["Llm:ApiKey"] = "from-secrets-standin" },
        };
        var laterSource = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["Llm:ApiKey"] = "from-later-source" },
        };

        configBuilder.Add(earlierSource);
        configBuilder.AddEnvironmentVariables();
        configBuilder.Add(laterSource);

        Program.RepositionBeforeEnvironmentVariables(
            configBuilder.Sources,
            source => ReferenceEquals(source, secretsStandIn),
            sources => sources.Add(secretsStandIn));

        var envVarsSource = configBuilder.Sources.Single(source => source is EnvironmentVariablesConfigurationSource);
        Assert.Equal(
            new IConfigurationSource[] { earlierSource, secretsStandIn, envVarsSource, laterSource },
            configBuilder.Sources);

        var config = configBuilder.Build();
        Assert.Equal("from-later-source", config["Llm:ApiKey"]);
    }

    // Names and load status only - never a key or value - for a deliberately mixed set of provider
    // kinds: a JSON file that exists, an optional JSON file that doesn't, an in-memory source, and
    // environment variables. Proves the file-backed/not-file-backed split and the true/false/null
    // Loaded values independent of whatever's actually on the running machine.
    [Fact]
    public void DescribeConfigurationSources_MixedProviders_ReportsNamesAndLoadStatusOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"joblens-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string existingFileName = "existing.json";
            const string missingFileName = "missing.json";
            File.WriteAllText(Path.Combine(tempDir, existingFileName), "{}");

            var configRoot = (IConfigurationRoot)new ConfigurationBuilder()
                .SetBasePath(tempDir)
                .AddJsonFile(existingFileName, optional: true)
                .AddJsonFile(missingFileName, optional: true)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Foo"] = "bar" })
                .AddEnvironmentVariables()
                .Build();

            var descriptions = Program.DescribeConfigurationSources(configRoot);

            Assert.Equal(4, descriptions.Count);
            Assert.Equal("JsonConfigurationProvider", descriptions[0].ProviderName);
            Assert.True(descriptions[0].Loaded);
            Assert.Equal("JsonConfigurationProvider", descriptions[1].ProviderName);
            Assert.False(descriptions[1].Loaded);
            Assert.Equal("MemoryConfigurationProvider", descriptions[2].ProviderName);
            Assert.Null(descriptions[2].Loaded);
            Assert.Equal("EnvironmentVariablesConfigurationProvider", descriptions[3].ProviderName);
            Assert.Null(descriptions[3].Loaded);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int CountSecretsSources(IEnumerable<IConfigurationSource> sources) =>
        sources.Count(source => source is JsonConfigurationSource { Path: "secrets.json" });

    private static List<int> IndicesWhere(
        IList<IConfigurationSource> sources, Func<IConfigurationSource, bool> predicate)
    {
        var indices = new List<int>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (predicate(sources[i]))
                indices.Add(i);
        }
        return indices;
    }
}
