using System.Diagnostics;
using JobLens.Core.Pipeline;

namespace JobLens.Tests.Pipeline;

public class RunLockTests
{
    [Fact]
    public void TryAcquire_WhenAlreadyHeld_ReturnsNullWithoutWaiting()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);
        using var holder = runLock.TryAcquire();
        Assert.NotNull(holder);

        var stopwatch = Stopwatch.StartNew();
        using var second = runLock.TryAcquire();
        stopwatch.Stop();

        Assert.Null(second);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Contended acquisition took {stopwatch.Elapsed}; it should fail immediately without retrying.");
    }

    [Fact]
    public void TryAcquire_AfterDisposal_IsImmediatelyReacquirable()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);

        var holder = runLock.TryAcquire();
        Assert.NotNull(holder);
        holder.Dispose();

        using var reacquired = runLock.TryAcquire();
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void TryAcquire_AfterHolderThrows_IsReacquirable()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);

        Action holdAndThrow = () =>
        {
            using var holder = runLock.TryAcquire();
            Assert.NotNull(holder);
            throw new InvalidOperationException("Simulated pipeline failure.");
        };

        Assert.Throws<InvalidOperationException>(holdAndThrow);

        using var reacquired = runLock.TryAcquire();
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void TryAcquire_WhenContainingDirectoryIsMissing_CreatesIt()
    {
        using var tempDirectory = new TempDirectory();
        var missingDirectory = Path.Combine(tempDirectory.RootPath, "missing", "nested");
        var lockFilePath = Path.Combine(missingDirectory, "run.lock");
        var runLock = new RunLock(lockFilePath);
        Assert.False(Directory.Exists(missingDirectory));

        using var holder = runLock.TryAcquire();

        Assert.NotNull(holder);
        Assert.True(Directory.Exists(missingDirectory));
    }

    [Fact]
    public void TryAcquire_WhenLockPathIsADirectory_PropagatesRatherThanReturningNull()
    {
        // A path that is itself an existing directory can never be opened as a file - this is a
        // genuine I/O failure, not another holder having the lock open. TryAcquire must let it
        // propagate rather than mapping it to a false "contended" null, since a caller (F6's
        // scheduled runner) treats null as "skip, another run is in progress" - collapsing a
        // broken lock path into that same outcome would silently skip every scheduled fire
        // forever with a message that points away from the real cause. Confirmed empirically:
        // FileStream's open-a-directory-as-a-file failure surfaces as UnauthorizedAccessException
        // (HResult 0x80070005), not IOException, so this exercises the "doesn't derive from
        // IOException, already propagates unchanged" guarantee RunLock's catch clause relies on -
        // proving TryAcquire doesn't accidentally widen that catch to swallow it too.
        using var tempDirectory = new TempDirectory();
        var directoryAsLockPath = Path.Combine(tempDirectory.RootPath, "not-a-file");
        Directory.CreateDirectory(directoryAsLockPath);
        var runLock = new RunLock(directoryAsLockPath);

        Assert.Throws<UnauthorizedAccessException>(() => runLock.TryAcquire());
    }

    [Fact]
    public void Dispose_LeavesPersistentEmptyLockFile()
    {
        using var tempDirectory = new TempDirectory();
        var runLock = new RunLock(tempDirectory.LockFilePath);

        var holder = runLock.TryAcquire();
        Assert.NotNull(holder);
        holder.Dispose();

        Assert.True(File.Exists(tempDirectory.LockFilePath));
        Assert.Equal(0, new FileInfo(tempDirectory.LockFilePath).Length);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"joblens-run-lock-test-{Guid.NewGuid()}");
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
