using JobLens.Core.Diagnostics;
using JobLens.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobLens.Tests;

public class RunOnceModeTests
{
    [Fact]
    public async Task RunOnceModeAsync_ResolvesOnlyTheCoordinatorAndReturnsItsResult()
    {
        // The restrictive service provider is what proves the CLI branch stays thin: it throws for
        // every service type except the coordinator, so any extra resolution fails the test.
        using var tempDirectory = new TempDirectory();
        var pipelineCalls = 0;
        var coordinator = new ScheduledRunService(
            new RunLock(tempDirectory.LockFilePath),
            new StubPreflight(),
            _ => Task.FromResult(new IngestSummary(3, 2, 1, 1, 1)),
            (_, _) =>
            {
                pipelineCalls++;
                return Task.FromResult(new RunSummary(1, 2, 1, 1, 1, 0, 0, false, null));
            },
            NullLogger<ScheduledRunService>.Instance);
        var services = new CoordinatorOnlyServiceProvider(coordinator);

        var result = await Program.RunOnceModeAsync(services, CancellationToken.None);

        Assert.Equal(ScheduledRunStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, pipelineCalls);
        Assert.Equal([typeof(ScheduledRunService)], services.RequestedServiceTypes);
    }

    [Fact]
    public async Task RunOnceModeAsync_LockHeldByAnotherRun_ReturnsExitCodeTwo()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        using var heldLock = runLock.TryAcquire();
        Assert.NotNull(heldLock);

        var preflight = new StubPreflight();
        var coordinator = new ScheduledRunService(
            runLock,
            preflight,
            _ => Task.FromResult(new IngestSummary(0, 0, 0, 0, 0)),
            (_, _) => Task.FromResult(new RunSummary(0, 0, 0, 0, 0, 0, 0, false, null)),
            NullLogger<ScheduledRunService>.Instance);

        var result = await Program.RunOnceModeAsync(
            new CoordinatorOnlyServiceProvider(coordinator),
            CancellationToken.None);

        Assert.Equal(ScheduledRunStatus.LockUnavailable, result.Status);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, preflight.Calls);
    }

    private sealed class StubPreflight : IPreflight
    {
        public int Calls { get; private set; }

        public Task<PreflightResult> RunAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PreflightResult(
                PreflightStatus.Success,
                "test-build",
                ReziTokenCache: null,
                LatestConfiguredGroupMessage: null,
                BridgeHealthy: true,
                Warnings: [],
                Failures: []));
        }
    }

    private sealed class CoordinatorOnlyServiceProvider(ScheduledRunService coordinator)
        : IServiceProvider
    {
        public List<Type> RequestedServiceTypes { get; } = [];

        public object? GetService(Type serviceType)
        {
            RequestedServiceTypes.Add(serviceType);
            return serviceType == typeof(ScheduledRunService)
                ? coordinator
                : throw new InvalidOperationException(
                    $"Run-once mode unexpectedly resolved '{serviceType}'.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"joblens-run-once-mode-test-{Guid.NewGuid()}");
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
