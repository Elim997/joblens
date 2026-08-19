namespace JobLens.Core.Pipeline;

/// <summary>
/// Exclusive, cross-process file lock guarding JobLens's pipeline-mutating entry points
/// (POST /ingest, POST /run today; --run-once in a later milestone) so at most one of them is
/// ever mutating pgvector/drafts at a time. Backed by a zero-byte lock file opened with
/// FileShare.None, which Windows enforces exclusively both across processes and across handles
/// within the same process - the latter is what makes the concurrent-acquire tests possible
/// in-process, without spawning anything.
///
/// The lock file path is a constructor argument, never resolved internally, so tests never
/// touch the real %LOCALAPPDATA% path - see Program.cs's registration for the production
/// default. Register the RunLock instance itself (which holds only the path, no open handle) as
/// a DI singleton; never register an acquired lock as a singleton, since that would hold the
/// lock open for the application's entire lifetime and block every request after the first.
/// </summary>
public sealed class RunLock(string lockFilePath)
{
    // Win32 ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, as surfaced through IOException.HResult
    // when another handle already holds the file exclusively. These are the ONLY IOExceptions that
    // mean "another holder has this lock" - every other IOException (unwritable path, full disk, a
    // directory sitting where the lock file should be, etc.) is a genuine failure and must propagate,
    // not be swallowed into a false "contended" result. Collapsing all IOExceptions to null would make
    // a broken lock path indistinguishable from real contention: F6's scheduled runner would map it to
    // "another run is in progress" and skip forever, with a log message that actively points away from
    // the real cause. UnauthorizedAccessException is not caught here at all - it doesn't derive from
    // IOException, so it already propagates unchanged.
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Attempts to acquire the lock immediately - no blocking, no retry. Returns a disposable
    /// that releases the lock (closes the file handle) when disposed; disposing never deletes
    /// the lock file itself, so a stable, empty run.lock left on disk between runs is the normal
    /// steady state, not an error condition to clean up. Returns null if another holder already
    /// has the file open, whether in this process or another. Any other I/O failure (unwritable
    /// path, full disk, a directory where the lock file should be, etc.) propagates as-is rather
    /// than being reported as contention.
    /// </summary>
    public IDisposable? TryAcquire()
    {
        var directory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation)
        {
            return null;
        }
    }
}
