using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;

namespace AIArena.Core.Persistence;

public sealed class SessionStore
{
    private const int SnapshotSaveRetries = 24;
    private static readonly TimeSpan SnapshotSaveRetryDelay = TimeSpan.FromMilliseconds(125);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SnapshotWriteLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SessionStore(string? dataRoot = null)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
    }

    public string DataRoot { get; }

    public string SettingsPath => NativeDataPaths.ConfigPath(DataRoot, "settings.json");

    public async Task<ArenaSnapshot?> LoadSnapshotAsync(string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var path = NativeDataPaths.SessionSnapshotPath(DataRoot, sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<ArenaSnapshot>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveSnapshotAsync(ArenaSnapshot snapshot, string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var path = SnapshotPath(sessionId);
        var fullPath = Path.GetFullPath(path);
        var writeLock = SnapshotWriteLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            }

            await ReplaceSnapshotFileAsync(tempPath, fullPath, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task ReplaceSnapshotFileAsync(string tempPath, string path, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < SnapshotSaveRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ClearReadOnly(path);
                    ReplaceOrMove(tempPath, path);
                    return;
                }
                catch (IOException) when (attempt < SnapshotSaveRetries - 1)
                {
                    await Task.Delay(SnapshotSaveRetryDelay * (attempt + 1), cancellationToken);
                }
                catch (UnauthorizedAccessException) when (attempt < SnapshotSaveRetries - 1)
                {
                    await Task.Delay(SnapshotSaveRetryDelay * (attempt + 1), cancellationToken);
                }
            }

            ClearReadOnly(path);
            ReplaceOrMove(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ReplaceOrMove(string tempPath, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, path);
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

    public async Task CreateSessionAsync(string newSessionId, ArenaSnapshot template, CancellationToken cancellationToken = default)
    {
        var safeSession = SafeSessionId(newSessionId);
        if (string.IsNullOrWhiteSpace(safeSession))
        {
            throw new ArgumentException("Session name is required.", nameof(newSessionId));
        }

        var path = SnapshotPath(safeSession);
        if (File.Exists(path))
        {
            return;
        }

        var cloneJson = JsonSerializer.Serialize(template, JsonOptions);
        var clone = JsonSerializer.Deserialize<ArenaSnapshot>(cloneJson, JsonOptions) ?? new ArenaSnapshot();
        clone.Engine.Messages.Clear();
        clone.Engine.Narration.Clear();
        clone.Engine.TurnCount = 0;
        clone.Engine.TurnIndex = 0;
        clone.Engine.LastError = "";
        foreach (var agent in clone.Engine.Agents)
        {
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
        }

        await SaveSnapshotAsync(clone, safeSession, cancellationToken);
    }

    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeSession = SafeSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(safeSession) || safeSession.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        var sessionsRoot = Path.GetFullPath(NativeDataPaths.SessionsRoot(DataRoot));
        var sessionPath = Path.GetFullPath(Path.Combine(sessionsRoot, safeSession));
        if (!sessionPath.StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sessionPath))
        {
            return Task.FromResult(false);
        }

        Directory.Delete(sessionPath, recursive: true);
        return Task.FromResult(true);
    }

    public bool SettingsExists() => File.Exists(SettingsPath);

    public int CountCheckpoints(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        var checkpointsPath = NativeDataPaths.CheckpointDirectory(DataRoot, safeSession);
        return Directory.Exists(checkpointsPath)
            ? Directory.EnumerateFiles(checkpointsPath, "*.json").Count()
            : 0;
    }

    public async Task<IReadOnlyList<CheckpointSummary>> ListCheckpointsAsync(string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var checkpointDir = CheckpointDirectory(sessionId);
        if (!Directory.Exists(checkpointDir))
        {
            return Array.Empty<CheckpointSummary>();
        }

        var checkpoints = new List<CheckpointSummary>();
        foreach (var path in Directory.EnumerateFiles(checkpointDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var record = await JsonSerializer.DeserializeAsync<CheckpointRecord>(stream, JsonOptions, cancellationToken);
                if (record is not null && !string.IsNullOrWhiteSpace(record.Id))
                {
                    checkpoints.Add(new CheckpointSummary(record.Id, record.Name, record.SessionId, record.CreatedAt, path));
                }
            }
            catch
            {
                // Ignore malformed checkpoints in the list; restore will report a precise error if selected directly.
            }
        }

        return checkpoints
            .OrderByDescending(checkpoint => checkpoint.CreatedAt)
            .ToArray();
    }

    public async Task<CheckpointSummary> SaveCheckpointAsync(string sessionId, string name, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException($"No snapshot found for session {sessionId}.");
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        var checkpointName = string.IsNullOrWhiteSpace(name)
            ? $"Arena checkpoint {now:yyyy-MM-dd HH:mm:ss}"
            : name.Trim()[..Math.Min(name.Trim().Length, 80)];
        var record = new CheckpointRecord
        {
            Id = id,
            Name = checkpointName,
            SessionId = sessionId,
            AppVersion = "wpf-beta",
            CreatedAt = now.ToUnixTimeSeconds(),
            Snapshot = snapshot
        };

        var checkpointDir = CheckpointDirectory(sessionId);
        Directory.CreateDirectory(checkpointDir);
        var path = Path.Combine(checkpointDir, $"{id}.json");
        var tempPath = $"{path}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
        return new CheckpointSummary(id, checkpointName, sessionId, record.CreatedAt, path);
    }

    public async Task<CheckpointSummary?> RestoreCheckpointAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var path = SafeCheckpointPath(sessionId, checkpointId);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var record = await JsonSerializer.DeserializeAsync<CheckpointRecord>(stream, JsonOptions, cancellationToken);
        if (record?.Snapshot is null)
        {
            return null;
        }

        await SaveSnapshotAsync(record.Snapshot, sessionId, cancellationToken);
        return new CheckpointSummary(record.Id, record.Name, sessionId, record.CreatedAt, path);
    }

    public Task<bool> DeleteCheckpointAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = SafeCheckpointPath(sessionId, checkpointId);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public string SnapshotPath(string sessionId = "default") => NativeDataPaths.SessionSnapshotPath(DataRoot, sessionId);

    public string CheckpointDirectory(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : SafeSessionId(sessionId);
        return NativeDataPaths.CheckpointDirectory(DataRoot, safeSession);
    }

    public static string SafeSessionId(string sessionId)
    {
        var cleaned = string.Join("-", sessionId.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Replace(' ', '-');
    }

    private string? SafeCheckpointPath(string sessionId, string checkpointId)
    {
        var safeId = SafeSessionId(checkpointId);
        if (string.IsNullOrWhiteSpace(safeId))
        {
            return null;
        }

        var checkpointDir = Path.GetFullPath(CheckpointDirectory(sessionId));
        var path = Path.GetFullPath(Path.Combine(checkpointDir, $"{safeId}.json"));
        return path.StartsWith(checkpointDir, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessionsRoot = NativeDataPaths.SessionsRoot(DataRoot);
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<SessionSummary>();
        }

        var summaries = new List<SessionSummary>();
        foreach (var sessionDir in Directory.EnumerateDirectories(sessionsRoot).OrderBy(Path.GetFileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(sessionDir);
            var snapshotPath = Path.Combine(sessionDir, "snapshot.json");
            var snapshot = File.Exists(snapshotPath)
                ? await LoadSnapshotAsync(id, cancellationToken)
                : null;
            var checkpointPath = CheckpointDirectory(id);
            var eventPath = NativeDataPaths.EventPath(DataRoot, id);
            var lastModified = Directory.GetLastWriteTime(sessionDir);
            summaries.Add(new SessionSummary(
                id,
                snapshotPath,
                File.Exists(snapshotPath),
                snapshot?.Engine.Messages.Count ?? 0,
                Directory.Exists(checkpointPath) ? Directory.EnumerateFiles(checkpointPath, "*.json").Count() : 0,
                File.Exists(eventPath) ? File.ReadLines(eventPath).Count() : 0,
                new DateTimeOffset(lastModified)));
        }

        return summaries;
    }
}

public sealed record CheckpointSummary(string Id, string Name, string SessionId, long CreatedAt, string Path)
{
    public override string ToString()
    {
        var localTime = DateTimeOffset.FromUnixTimeSeconds(CreatedAt).LocalDateTime;
        return $"{Name} - {localTime:g}";
    }
}

public sealed class CheckpointRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "default";

    [JsonPropertyName("app_version")]
    public string AppVersion { get; init; } = "wpf-beta";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("snapshot")]
    public ArenaSnapshot Snapshot { get; init; } = new();
}
