using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AIArena.Wpf;

internal readonly record struct SnapshotStamp(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    long ChangeTimeFileTicks,
    long CreationTimeFileTicks,
    string ContentHash,
    long ProcessMutationGeneration);

internal sealed record StampedSnapshot<T>(T Value, SnapshotStamp Stamp);

/// <summary>One file-based stamp for polling and loaded snapshots; directory activity is not snapshot evidence.</summary>
internal sealed class SnapshotStampReader
{
    internal static readonly TimeSpan RecentNativeChangeHashWindow = TimeSpan.FromSeconds(2);
    private readonly bool forceContentHashGeneration;
    private readonly TimeProvider timeProvider;
    private readonly Action<int>? hashChunkObserved;

    internal SnapshotStampReader(bool forceContentHashGeneration = false, TimeProvider? timeProvider = null,
        Action<int>? hashChunkObserved = null)
    {
        this.forceContentHashGeneration = forceContentHashGeneration;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.hashChunkObserved = hashChunkObserved;
    }

    public SnapshotStamp? Capture(string snapshotPath, Func<long>? mutationGeneration = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var processBefore = mutationGeneration?.Invoke() ?? 0;
                var stamp = ReadFileStamp(snapshotPath, cancellationToken);
                var processAfter = mutationGeneration?.Invoke() ?? 0;
                if (stamp is null) return null;
                if (processBefore == processAfter) return stamp.Value with { ProcessMutationGeneration = processAfter };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { }
        return null;
    }

    public async Task<StampedSnapshot<T>> ReadStableAsync<T>(string snapshotPath,
        Func<CancellationToken, Task<T>> load, Func<long>? mutationGeneration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = Capture(snapshotPath, mutationGeneration, cancellationToken)
                ?? throw new IOException("The snapshot file generation is unavailable.");
            var value = await load(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var after = Capture(snapshotPath, mutationGeneration, cancellationToken);
            if (after is { } observed && before == observed) return new(value, observed);
        }
        throw new IOException("The snapshot changed while it was being read. Refresh to read the current session.");
    }

    private SnapshotStamp? ReadFileStamp(string path, CancellationToken cancellationToken)
    {
        if (!forceContentHashGeneration)
        {
            try
            {
                var native = ReadNative(path, cancellationToken);
                if (native is not null) return native;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                or DllNotFoundException or EntryPointNotFoundException) { }
        }

        // Filesystems without trustworthy native change times retain content-hash evidence.
        var before = new FileInfo(path);
        if (!before.Exists) return null;
        var length = before.Length;
        var write = before.LastWriteTimeUtc;
        var created = before.CreationTimeUtc;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = HashFile(handle, cancellationToken);
        var after = new FileInfo(path);
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != write || after.CreationTimeUtc != created)
            return null;
        return new(length, new DateTimeOffset(write, TimeSpan.Zero), 0, created.ToFileTimeUtc(), hash, 0);
    }

    private SnapshotStamp? ReadNative(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandleEx(handle, 0, out var before, (uint)Marshal.SizeOf<FileBasicInfo>())
                || !ShouldTrustNativeChangeTime(before.ChangeTime)) return null;
            var length = RandomAccess.GetLength(handle);
            var hash = ShouldHashRecentNativeChangeTime(before.ChangeTime, timeProvider.GetUtcNow())
                ? HashFile(handle, cancellationToken) : "";
            if (!GetFileInformationByHandleEx(handle, 0, out var after, (uint)Marshal.SizeOf<FileBasicInfo>())
                || length != RandomAccess.GetLength(handle) || before.CreationTime != after.CreationTime
                || before.LastWriteTime != after.LastWriteTime || before.ChangeTime != after.ChangeTime
                || before.FileAttributes != after.FileAttributes) continue;
            return new(length, new DateTimeOffset(DateTime.FromFileTimeUtc(after.LastWriteTime), TimeSpan.Zero),
                after.ChangeTime, after.CreationTime, hash, 0);
        }
        return null;
    }

    internal static bool ShouldTrustNativeChangeTime(long changeTimeFileTicks) => changeTimeFileTicks > 0;

    internal static bool ShouldHashRecentNativeChangeTime(long changeTimeFileTicks, DateTimeOffset utcNow)
    {
        if (!ShouldTrustNativeChangeTime(changeTimeFileTicks)) return true;
        try
        {
            return utcNow.ToUniversalTime() - new DateTimeOffset(DateTime.FromFileTimeUtc(changeTimeFileTicks), TimeSpan.Zero)
                < RecentNativeChangeHashWindow;
        }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    private string HashFile(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var chunks = 0;
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, buffer.AsSpan(), offset);
                if (read == 0) return Convert.ToHexString(hash.GetHashAndReset());
                hash.AppendData(buffer, 0, read);
                offset += read;
                hashChunkObserved?.Invoke(++chunks);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass,
        out FileBasicInfo fileInformation, uint bufferSize);
}
