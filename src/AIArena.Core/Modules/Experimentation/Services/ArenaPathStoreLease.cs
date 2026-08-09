namespace AIArena.Core.Services;

/// <summary>
/// Provides a bounded, path-scoped writer lease for stores that can be opened by
/// more than one service instance or process. Callers must hold the lease for
/// the complete read/check/write transaction, not only for the final replace.
/// </summary>
internal static class ArenaPathStoreLease
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<FileStream> AcquireAsync(
        string root,
        string fileName,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("Lease name must be a file name, not a path.", nameof(fileName));
        }

        var deadline = timeout ?? DefaultTimeout;
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Artifact store writer lease was unavailable before its deadline.");
    }
}
