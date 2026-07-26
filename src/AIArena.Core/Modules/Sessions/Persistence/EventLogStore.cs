using System.Text;
using System.Text.Json;

namespace AIArena.Core.Persistence;

public sealed class EventLogStore
{
    private const long MaxBytes = 128 * 1024;
    private const int Rotations = 3;
    private static readonly TimeSpan EventWriteLeaseTimeout = TimeSpan.FromSeconds(45);
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
    private static readonly KeyedAsyncLockRegistry EventWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public EventLogStore(string? dataRoot = null)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
    }

    public string DataRoot { get; }

    public async Task AppendAsync(string sessionId, string type, object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = EventPath(sessionId);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var now = DateTimeOffset.Now;
        var entry = new
        {
            type,
            created_at = now.ToUnixTimeSeconds(),
            created_at_iso = now.ToString("O"),
            payload
        };
        var serializedEntry = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        var line = new byte[serializedEntry.Length + NewLineBytes.Length];
        serializedEntry.CopyTo(line, 0);
        NewLineBytes.CopyTo(line, serializedEntry.Length);
        using var processLock = await EventWriteLocks.AcquireAsync(fullPath, cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(fullPath, EventWriteLeaseTimeout, cancellationToken);
        // Rotation and append form one mutation. Once it begins, finish the JSONL
        // record so a late caller cancellation cannot leave a partial line.
        cancellationToken.ThrowIfCancellationRequested();
        RotateIfNeeded(fullPath);
        ClearReadOnly(fullPath);
        await AppendBytesAsync(fullPath, line, CancellationToken.None);
    }

    public string EventPath(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return NativeDataPaths.EventPath(DataRoot, safeSession);
    }

    private static async Task AppendBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes)
        {
            return;
        }

        for (var index = Rotations; index >= 1; index--)
        {
            var current = $"{path[..^".jsonl".Length]}.{index}.jsonl";
            var next = $"{path[..^".jsonl".Length]}.{index + 1}.jsonl";
            if (index == Rotations && File.Exists(current))
            {
                ClearReadOnly(current);
                if (!TryFileOperation(() => File.Delete(current)))
                {
                    return;
                }

                continue;
            }

            if (File.Exists(current))
            {
                ClearReadOnly(current);
                ClearReadOnly(next);
                if (!TryFileOperation(() => File.Move(current, next, overwrite: true)))
                {
                    return;
                }
            }
        }

        ClearReadOnly(path);
        ClearReadOnly($"{path[..^".jsonl".Length]}.1.jsonl");
        TryFileOperation(() => File.Move(path, $"{path[..^".jsonl".Length]}.1.jsonl", overwrite: true));
    }

    private static bool TryFileOperation(Action operation)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    return false;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }

        return false;
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
