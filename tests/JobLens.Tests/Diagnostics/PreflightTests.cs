using JobLens.Core.Configuration;
using JobLens.Core.Diagnostics;
using JobLens.Core.Resume;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Diagnostics;

public class PreflightTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_AllChecksHealthy_ReturnsSuccess()
    {
        var postgres = new RecordingPostgresProbe();
        var source = new StubSourceProbe(Now.AddHours(-1));
        var bridge = new StubBridgeProbe(true);
        var preflight = CreatePreflight(postgres, source, bridge);

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, postgres.Attempts);
        Assert.Equal(ReziTokenCacheStatus.Absent, result.ReziTokenCache);
        Assert.True(result.BridgeHealthy);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task RunAsync_PostgresSucceedsAfterFailures_RetriesWithoutRealDelay()
    {
        var postgres = new RecordingPostgresProbe(failuresBeforeSuccess: 2);
        var runtime = new RecordingRuntime(Now);
        var preflight = CreatePreflight(postgres, runtime: runtime);

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Equal(3, postgres.Attempts);
        Assert.Equal(2, runtime.Delays.Count);
        Assert.All(runtime.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(2), delay));
    }

    [Fact]
    public async Task RunAsync_PostgresNeverReady_IsFatalAfterFiveAttempts()
    {
        var postgres = new RecordingPostgresProbe(failuresBeforeSuccess: int.MaxValue);
        var runtime = new RecordingRuntime(Now);
        var preflight = CreatePreflight(postgres, runtime: runtime);

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Fatal, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(5, postgres.Attempts);
        Assert.Equal(4, runtime.Delays.Count);
        Assert.Contains(result.Failures, failure => failure.Contains("PostgreSQL readiness failed"));
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_ReturnsCancelledAndDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var postgres = new CancellingPostgresProbe(cancellation);
        var preflight = CreatePreflight(postgres);

        var result = await preflight.RunAsync(cancellation.Token);

        Assert.Equal(PreflightStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(1, postgres.Attempts);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringRetryDelay_ReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var postgres = new RecordingPostgresProbe(failuresBeforeSuccess: 1);
        var runtime = new CancellingRuntime(Now, cancellation);
        var preflight = CreatePreflight(postgres, runtime: runtime);

        var result = await preflight.RunAsync(cancellation.Token);

        Assert.Equal(PreflightStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(1, postgres.Attempts);
        Assert.Single(runtime.Delays);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringTokenInspection_ReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var preflight = CreatePreflight(
            tokenCache: new CancellingTokenDiagnostics(cancellation));

        var result = await preflight.RunAsync(cancellation.Token);

        Assert.Equal(PreflightStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringSourceRead_ReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = new StubBridgeProbe(true);
        var preflight = CreatePreflight(
            source: new CancellingSourceProbe(cancellation),
            bridge: bridge);

        var result = await preflight.RunAsync(cancellation.Token);

        Assert.Equal(PreflightStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, bridge.Attempts);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringBridgeProbe_ReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var preflight = CreatePreflight(
            bridge: new CancellingBridgeProbe(cancellation));

        var result = await preflight.RunAsync(cancellation.Token);

        Assert.Equal(PreflightStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_TokenInspectionThrows_IsWarningOnly()
    {
        var preflight = CreatePreflight(tokenCache: new ThrowingTokenDiagnostics());

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Null(result.ReziTokenCache);
        Assert.Empty(result.Failures);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be inspected"));
    }

    [Fact]
    public async Task RunAsync_PresentTokenCache_IsInformationalOnly()
    {
        var preflight = CreatePreflight(
            tokenCache: new StubTokenDiagnostics(
                new ReziTokenCacheInspection(ReziTokenCacheStatus.Present)));

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Equal(ReziTokenCacheStatus.Present, result.ReziTokenCache);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task RunAsync_MessageExactlyAtStaleThreshold_DoesNotWarn()
    {
        var preflight = CreatePreflight(
            source: new StubSourceProbe(Now.AddHours(-12)),
            bridge: new StubBridgeProbe(true));

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task RunAsync_UnreadableSourceAndHealthyBridge_IsDegradedWithIndependentSignals()
    {
        var source = new ThrowingSourceProbe();
        var bridge = new StubBridgeProbe(true);
        var preflight = CreatePreflight(source: source, bridge: bridge);

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Degraded, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.LatestConfiguredGroupMessage);
        Assert.True(result.BridgeHealthy);
        Assert.Contains(result.Failures, failure => failure.Contains("source database is unreadable"));
        Assert.Equal(1, bridge.Attempts);
    }

    [Fact]
    public async Task RunAsync_BridgeDown_IsDegraded()
    {
        var preflight = CreatePreflight(bridge: new StubBridgeProbe(false));

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Degraded, result.Status);
        Assert.False(result.BridgeHealthy);
        Assert.Contains(result.Failures, failure => failure.Contains("bridge TCP probe failed"));
    }

    [Fact]
    public async Task RunAsync_HealthyBridgeWithOldMessage_IsWarningOnly()
    {
        var preflight = CreatePreflight(
            source: new StubSourceProbe(Now.AddHours(-13)),
            bridge: new StubBridgeProbe(true));

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Empty(result.Failures);
        Assert.Contains(result.Warnings, warning => warning.Contains("older than 12 hours"));
    }

    [Fact]
    public async Task RunAsync_UndecryptableTokenCache_IsWarningOnly()
    {
        var tokenCache = new StubTokenDiagnostics(
            new ReziTokenCacheInspection(
                ReziTokenCacheStatus.Undecryptable,
                new System.Security.Cryptography.CryptographicException("wrong user")));
        var preflight = CreatePreflight(tokenCache: tokenCache);

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Equal(ReziTokenCacheStatus.Undecryptable, result.ReziTokenCache);
        Assert.Contains(result.Warnings, warning => warning.Contains("DPAPI CurrentUser"));
    }

    [Fact]
    public async Task RunAsync_InvalidConfigurationStopsBeforeAnyProbe()
    {
        var postgres = new RecordingPostgresProbe();
        var source = new StubSourceProbe(Now);
        var bridge = new StubBridgeProbe(true);
        var preflight = CreatePreflight(
            postgres,
            source,
            bridge,
            configuration: BuildConfiguration(includeMessagesPath: false));

        var result = await preflight.RunAsync();

        Assert.Equal(PreflightStatus.Fatal, result.Status);
        Assert.Equal(0, postgres.Attempts);
        Assert.Equal(0, source.Attempts);
        Assert.Equal(0, bridge.Attempts);
    }

    private static Preflight CreatePreflight(
        IPostgresReadinessProbe? postgres = null,
        ISourceFreshnessProbe? source = null,
        IBridgeHealthProbe? bridge = null,
        IReziTokenCacheDiagnostics? tokenCache = null,
        IPreflightRuntime? runtime = null,
        IConfiguration? configuration = null) =>
        new(
            configuration ?? BuildConfiguration(),
            Options.Create(new JobLensOptions { BridgeHealthPort = 8080 }),
            postgres ?? new RecordingPostgresProbe(),
            tokenCache ?? new StubTokenDiagnostics(
                new ReziTokenCacheInspection(ReziTokenCacheStatus.Absent)),
            source ?? new StubSourceProbe(Now.AddHours(-1)),
            bridge ?? new StubBridgeProbe(true),
            runtime ?? new RecordingRuntime(Now),
            NullLogger<Preflight>.Instance);

    private static IConfiguration BuildConfiguration(bool includeMessagesPath = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["JobLens:GroupChatJids:0"] = "configured@g.us",
            ["JobLens:ScoringTemplates:0:Name"] = "General",
            ["JobLens:ScoringTemplates:0:Profile"] = "profile",
            ["Postgres:ConnectionString"] = "Host=dummy",
            ["Gemini:ApiKey"] = "dummy",
            ["Llm:BaseUrl"] = "http://localhost:20128/v1",
            ["Llm:ApiKey"] = "dummy",
            ["Llm:ScoringModel"] = "coding-fallback",
            ["Llm:TailoringModel"] = "cc/claude-sonnet-5",
        };
        if (includeMessagesPath)
            values["JobLens:MessagesDbPath"] = "C:/dummy/messages.db";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class RecordingPostgresProbe(int failuresBeforeSuccess = 0) : IPostgresReadinessProbe
    {
        public int Attempts { get; private set; }

        public Task ProbeAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= failuresBeforeSuccess)
                throw new InvalidOperationException("not ready");
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingPostgresProbe(CancellationTokenSource cancellation) : IPostgresReadinessProbe
    {
        public int Attempts { get; private set; }

        public Task ProbeAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class StubSourceProbe(DateTimeOffset? latest) : ISourceFreshnessProbe
    {
        public int Attempts { get; private set; }

        public Task<DateTimeOffset?> GetLatestConfiguredGroupMessageAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(latest);
        }
    }

    private sealed class ThrowingSourceProbe : ISourceFreshnessProbe
    {
        public Task<DateTimeOffset?> GetLatestConfiguredGroupMessageAsync(CancellationToken cancellationToken) =>
            throw new IOException("unreadable");
    }

    private sealed class CancellingSourceProbe(CancellationTokenSource cancellation) : ISourceFreshnessProbe
    {
        public Task<DateTimeOffset?> GetLatestConfiguredGroupMessageAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class StubBridgeProbe(bool healthy) : IBridgeHealthProbe
    {
        public int Attempts { get; private set; }

        public Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(healthy);
        }
    }

    private sealed class CancellingBridgeProbe(CancellationTokenSource cancellation) : IBridgeHealthProbe
    {
        public Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class StubTokenDiagnostics(ReziTokenCacheInspection inspection)
        : IReziTokenCacheDiagnostics
    {
        public ValueTask<ReziTokenCacheInspection> InspectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(inspection);
    }

    private sealed class CancellingTokenDiagnostics(CancellationTokenSource cancellation)
        : IReziTokenCacheDiagnostics
    {
        public ValueTask<ReziTokenCacheInspection> InspectAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ThrowingTokenDiagnostics : IReziTokenCacheDiagnostics
    {
        public ValueTask<ReziTokenCacheInspection> InspectAsync(CancellationToken cancellationToken) =>
            throw new IOException("token cache is locked by another process");
    }

    private sealed class RecordingRuntime(DateTimeOffset utcNow) : IPreflightRuntime
    {
        public DateTimeOffset UtcNow => utcNow;
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>Cancels while awaiting the retry backoff, which raises the cancellation from
    /// inside the retry handler rather than from the probe itself.</summary>
    private sealed class CancellingRuntime(DateTimeOffset utcNow, CancellationTokenSource cancellation)
        : IPreflightRuntime
    {
        public DateTimeOffset UtcNow => utcNow;
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
