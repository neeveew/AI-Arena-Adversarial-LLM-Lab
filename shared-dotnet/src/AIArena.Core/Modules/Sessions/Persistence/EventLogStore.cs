using System.Text.Json;

namespace AIArena.Core.Persistence;

public sealed class EventLogStore
{
    private const long MaxBytes = 128 * 1024;
    private const int Rotations = 3;
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
        var path = EventPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateIfNeeded(path);
        var now = DateTimeOffset.Now;
        var entry = new
        {
            type,
            created_at = now.ToUnixTimeSeconds(),
            created_at_iso = now.ToString("O"),
            payload
        };
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, cancellationToken);
    }

    public string EventPath(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return Path.Combine(DataRoot, "sessions", safeSession, "events.jsonl");
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
                File.Delete(current);
                continue;
            }

            if (File.Exists(current))
            {
                File.Move(current, next, overwrite: true);
            }
        }

        File.Move(path, $"{path[..^".jsonl".Length]}.1.jsonl", overwrite: true);
    }
}
