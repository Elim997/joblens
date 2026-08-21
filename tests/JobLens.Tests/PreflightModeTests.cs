using JobLens.Core.Diagnostics;
using JobLens.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobLens.Tests;

public class PreflightModeTests
{
    [Fact]
    public async Task RunPreflightModeAsync_SucceedsWhileRunLockIsHeldAndResolvesOnlyPreflight()
    {
        // A pipeline invocation already owns the lock. Preflight must still complete, and the
        // restrictive service provider is what actually proves it never reached for RunLock:
        // it throws for every service type except IPreflight.
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        using var heldLock = runLock.TryAcquire();
        Assert.NotNull(heldLock);

        var preflight = new RecordingPreflight();
        var services = new PreflightOnlyServiceProvider(preflight);

        var result = await Program.RunPreflightModeAsync(
            services,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(PreflightStatus.Success, result.Status);
        Assert.Equal(1, preflight.Calls);
        Assert.Equal([typeof(IPreflight)], services.RequestedServiceTypes);
    }

    private sealed class RecordingPreflight : IPreflight
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
                BridgeHealthy: null,
                Warnings: [],
                Failures: []));
        }
    }

    private sealed class PreflightOnlyServiceProvider(IPreflight preflight) : IServiceProvider
    {
        public List<Type> RequestedServiceTypes { get; } = [];

        public object? GetService(Type serviceType)
        {
            RequestedServiceTypes.Add(serviceType);
            return serviceType == typeof(IPreflight)
                ? preflight
                : throw new InvalidOperationException(
                    $"Preflight mode unexpectedly resolved '{serviceType}'.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"joblens-preflight-mode-test-{Guid.NewGuid()}");
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
