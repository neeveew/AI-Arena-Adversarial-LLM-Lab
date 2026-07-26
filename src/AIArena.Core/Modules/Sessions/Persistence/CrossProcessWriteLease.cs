using System.Diagnostics;

namespace AIArena.Core.Persistence;

/// <summary>
/// Serializes a file mutation across AI Arena processes without globally blocking
/// unrelated files. The empty sidecar is reusable after a crash and is removed by
/// the next owner after a normal release.
/// </summary>
internal sealed class CrossProcessWriteLease : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly string path;
    private FileStream? stream;

    private CrossProcessWriteLease(string path, FileStream stream)
    {
        this.path = path;
        this.stream = stream;
    }

    public static async Task<CrossProcessWriteLease> AcquireAsync(
        string targetPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var fullPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var leasePath = $"{fullPath}.write.lock";
        var startedAt = Stopwatch.GetTimestamp();
        Exception? lastFailure = null;

        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Delete sharing lets this owner mark the sidecar for deletion while
                // its handle is still open. Other writers request read/write access,
                // so Windows excludes them until this handle closes.
                var leaseStream = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.None);
                return new CrossProcessWriteLease(leasePath, leaseStream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < RetryDelay ? remaining : RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException(
            $"Timed out waiting for another AI Arena process to finish writing '{fullPath}'.",
            lastFailure);
    }

    public void Dispose()
    {
        var ownedStream = Interlocked.Exchange(ref stream, null);
        if (ownedStream is null)
        {
            return;
        }

        try
        {
            // Mark for deletion before closing to avoid a release/delete race on
            // Windows. A denied cleanup leaves only a harmless reusable sidecar.
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
        finally
        {
            ownedStream.Dispose();
        }
    }
}
