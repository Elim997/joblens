using JobLens.Core.Diagnostics;
using JobLens.Core.Parsing;
using JobLens.Core.Pipeline;
using JobLens.Core.Scoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobLens.Tests.Pipeline;

public class ScheduledRunServiceTests
{
    [Fact]
    public async Task RunAsync_SuccessfulRun_PreservesSummariesDetailsAndOrderUnderLock()
    {
        using var temp = new TempDirectory();
        var order = new List<string>();
        var runLock = new RunLock(temp.LockFilePath);
        var preflight = new StubPreflight(HealthyPreflight(), () =>
        {
            AssertContended(runLock);
            order.Add("preflight");
        });
        var ingest = new IngestSummary(5, 4, 1, 2, 1);
        var pipeline = new RunSummary(2, 3, 1, 1, 1, 0, 0, false, null);
        var service = CreateService(
            runLock,
            preflight,
            _ =>
            {
                AssertContended(runLock);
                order.Add("ingest");
                return Task.FromResult(ingest);
            },
            (observer, _) =>
            {
                AssertContended(runLock);
                order.Add("pipeline");
                observer.MatchNotified(Match());
                observer.DraftResolved("message-1", "General", DraftOutcomes.Created, "draft-1");
                return Task.FromResult(pipeline);
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["preflight", "ingest", "pipeline"], order);
        Assert.Equal(ingest, result.Ingest);
        Assert.Equal(pipeline, result.Pipeline);
        var match = Assert.Single(result.Matches);
        Assert.Equal("message-1", match.MessageId);
        Assert.Equal("draft-1", match.DraftId);
        using var reacquired = runLock.TryAcquire();
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task RunAsync_LockUnavailable_ReturnsTwoAndRunsNothing()
    {
        using var temp = new TempDirectory();
        var runLock = new RunLock(temp.LockFilePath);
        using var held = runLock.TryAcquire();
        Assert.NotNull(held);
        var preflight = new StubPreflight(HealthyPreflight());
        var ingestCalls = 0;
        var pipelineCalls = 0;
        var service = CreateService(
            runLock,
            preflight,
            _ =>
            {
                ingestCalls++;
                return Task.FromResult(EmptyIngest());
            },
            (_, _) =>
            {
                pipelineCalls++;
                return Task.FromResult(EmptyPipeline());
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.LockUnavailable, result.Status);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, preflight.Calls);
        Assert.Equal(0, ingestCalls);
        Assert.Equal(0, pipelineCalls);
    }

    [Fact]
    public async Task RunAsync_FatalPreflight_StopsBeforeIngestAndPipeline()
    {
        using var temp = new TempDirectory();
        var downstreamCalls = 0;
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(new PreflightResult(
                PreflightStatus.Fatal,
                "test-build",
                null,
                null,
                null,
                [],
                ["PostgreSQL readiness failed."])),
            _ =>
            {
                downstreamCalls++;
                return Task.FromResult(EmptyIngest());
            },
            (_, _) =>
            {
                downstreamCalls++;
                return Task.FromResult(EmptyPipeline());
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Fatal, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, downstreamCalls);
        Assert.Null(result.Ingest);
        Assert.Null(result.Pipeline);
    }

    [Fact]
    public async Task RunAsync_DegradedBridge_ContinuesAndReturnsThree()
    {
        using var temp = new TempDirectory();
        var ingestCalls = 0;
        var pipelineCalls = 0;
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(new PreflightResult(
                PreflightStatus.Degraded,
                "test-build",
                null,
                DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
                false,
                [],
                ["WhatsApp bridge TCP probe failed."])),
            _ =>
            {
                ingestCalls++;
                return Task.FromResult(EmptyIngest());
            },
            (_, _) =>
            {
                pipelineCalls++;
                return Task.FromResult(EmptyPipeline());
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Degraded, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(1, ingestCalls);
        Assert.Equal(1, pipelineCalls);
        Assert.False(result.Preflight!.BridgeHealthy);
    }

    [Fact]
    public async Task RunAsync_StaleSourceWarningAlone_RemainsSuccessful()
    {
        using var temp = new TempDirectory();
        var preflight = HealthyPreflight() with
        {
            Warnings = ["Latest configured-group source message is older than 12 hours."],
        };
        var service = CreateService(new RunLock(temp.LockFilePath), new StubPreflight(preflight));

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Preflight!.Warnings);
    }

    [Fact]
    public async Task RunAsync_ReziAuthenticationFailure_PreservesScoringAndReturnsThree()
    {
        using var temp = new TempDirectory();
        var pipeline = new RunSummary(1, 1, 1, 1, 0, 0, 1, false, null);
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(HealthyPreflight()),
            pipeline: (observer, _) =>
            {
                observer.MatchNotified(Match());
                observer.ReziAuthenticationFailed("Authentication required.");
                observer.DraftResolved("message-1", "General", DraftOutcomes.SkippedAuth);
                return Task.FromResult(pipeline);
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Degraded, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(pipeline, result.Pipeline);
        Assert.Equal("Authentication required.", result.ReziAuthenticationError);
        Assert.Equal(DraftOutcomes.SkippedAuth, Assert.Single(result.Matches).DraftOutcome);
    }

    [Fact]
    public async Task RunAsync_ScoringTransportFailure_PreservesFailSoftResultAndReturnsThree()
    {
        using var temp = new TempDirectory();
        var pipeline = new RunSummary(
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            true,
            "A scoring batch returned zero usable scores.");
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(HealthyPreflight()),
            pipeline: (observer, _) =>
            {
                observer.ScoringTransportFailed("General", new TimeoutException("provider timed out"));
                return Task.FromResult(pipeline);
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Degraded, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(pipeline, result.Pipeline);
        var failure = Assert.Single(result.ScoringTransportFailures);
        Assert.Equal("General", failure.TemplateName);
        Assert.Equal(nameof(TimeoutException), failure.ExceptionType);
    }

    [Fact]
    public async Task RunAsync_ZeroNewlyStored_StillRunsBacklogPipeline()
    {
        using var temp = new TempDirectory();
        var pipelineCalls = 0;
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(HealthyPreflight()),
            _ => Task.FromResult(new IngestSummary(8, 6, 1, 5, 0)),
            (_, _) =>
            {
                pipelineCalls++;
                return Task.FromResult(new RunSummary(1, 2, 0, 0, 0, 0, 0, false, null));
            });

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Success, result.Status);
        Assert.Equal(0, result.Ingest!.NewlyStored);
        Assert.Equal(1, pipelineCalls);
        Assert.Equal(2, result.Pipeline!.Scored);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_ReturnsFourAndStopsLaterStages()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var pipelineCalls = 0;
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(HealthyPreflight()),
            token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(EmptyIngest());
            },
            (_, _) =>
            {
                pipelineCalls++;
                return Task.FromResult(EmptyPipeline());
            });

        var result = await service.RunAsync(cancellation.Token);

        Assert.Equal(ScheduledRunStatus.Cancelled, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, pipelineCalls);
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_IsLoggedAndReturnsOne()
    {
        using var temp = new TempDirectory();
        var logger = new RecordingLogger<ScheduledRunService>();
        var service = CreateService(
            new RunLock(temp.LockFilePath),
            new StubPreflight(HealthyPreflight()),
            _ => throw new InvalidOperationException("unexpected ingest failure"),
            logger: logger);

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Fatal, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("unexpected ingest failure", result.FatalError);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task RunAsync_BrokenLockPath_IsFatalNotLockUnavailable()
    {
        using var temp = new TempDirectory();
        var directoryAsLockPath = Path.Combine(temp.RootPath, "not-a-file");
        Directory.CreateDirectory(directoryAsLockPath);
        var preflight = new StubPreflight(HealthyPreflight());
        var service = CreateService(new RunLock(directoryAsLockPath), preflight);

        var result = await service.RunAsync();

        Assert.Equal(ScheduledRunStatus.Fatal, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, preflight.Calls);
    }

    private static ScheduledRunService CreateService(
        RunLock runLock,
        IPreflight preflight,
        Func<CancellationToken, Task<IngestSummary>>? ingest = null,
        Func<IRunObserver, CancellationToken, Task<RunSummary>>? pipeline = null,
        ILogger<ScheduledRunService>? logger = null) =>
        new(
            runLock,
            preflight,
            ingest ?? (_ => Task.FromResult(EmptyIngest())),
            pipeline ?? ((_, _) => Task.FromResult(EmptyPipeline())),
            logger ?? NullLogger<ScheduledRunService>.Instance);

    private static PreflightResult HealthyPreflight() =>
        new(
            PreflightStatus.Success,
            "test-build",
            null,
            DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
            true,
            [],
            []);

    private static IngestSummary EmptyIngest() => new(0, 0, 0, 0, 0);

    private static RunSummary EmptyPipeline() => new(0, 0, 0, 0, 0, 0, 0, false, null);

    private static ScoredPosting Match() =>
        new(
            "message-1",
            new JobPosting(
                "Backend Developer",
                "Example Co",
                "Remote",
                "Software",
                "https://example.com/job",
                "Build services."),
            91,
            "Strong fit.",
            "General");

    private static void AssertContended(RunLock runLock)
    {
        using var contender = runLock.TryAcquire();
        Assert.Null(contender);
    }

    private sealed class StubPreflight(
        PreflightResult result,
        Action? beforeReturn = null) : IPreflight
    {
        public int Calls { get; private set; }

        public Task<PreflightResult> RunAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            beforeReturn?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    private record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"joblens-scheduled-run-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }
        public string LockFilePath => Path.Combine(RootPath, "run.lock");

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
