using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;

namespace AIArena.Core.Persistence;

public sealed class SessionStore
{
    private static readonly HashSet<string> RemovedLegacyInternetKeys = new(
        ["model_rss", "news_automation"],
        StringComparer.OrdinalIgnoreCase);
    private const int CheckpointMetadataPrefixBytes = 64 * 1024;
    private const int CheckpointMetadataReadChunkBytes = 4 * 1024;
    private const int SnapshotSaveRetries = 24;
    private const int MaxForkNameAttempts = 10_000;
    private const int MaxSafeCheckpointIdLength = 128;
    private static readonly TimeSpan SnapshotSaveRetryDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan SnapshotWriteLeaseTimeout = TimeSpan.FromSeconds(45);
    private static readonly KeyedAsyncLockRegistry SnapshotWriteLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot path to its last observed write stamp and message count. Shared
    /// across stores because the key is a full path, and a data root can be
    /// shared with other AI Arena implementations.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime WriteUtc, long Length, int Count)> MessageCountCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Event log path to its last observed write stamp and line count.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime WriteUtc, long Length, int Count)> EventLineCountCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Transforms provider API tokens before they are written to disk. Host apps can
    /// install an at-rest protector (e.g. Windows DPAPI); must be idempotent for
    /// already-protected values. Defaults to identity (plaintext).
    /// </summary>
    public static Func<string, string> ProtectSecret { get; set; } = static value => value;

    /// <summary>
    /// Reverses <see cref="ProtectSecret"/> when snapshots are loaded. Must pass
    /// unprotected/legacy plaintext values through unchanged.
    /// </summary>
    public static Func<string, string> UnprotectSecret { get; set; } = static value => value;

    public SessionStore(string? dataRoot = null)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
    }

    public string DataRoot { get; }

    public string SettingsPath => NativeDataPaths.ConfigPath(DataRoot, "settings.json");

    public async Task EnsureDefaultSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionsAsync(cancellationToken);
        if (sessions.Count > 0)
        {
            return;
        }

        await SaveSnapshotAsync(CreateDefaultSnapshot(), "default", cancellationToken);
    }

    public async Task<ArenaSnapshot?> LoadSnapshotAsync(string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var path = NativeDataPaths.SessionSnapshotPath(DataRoot, sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var snapshot = await JsonSerializer.DeserializeAsync<ArenaSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is not null)
            {
                ScrubRemovedLegacyInternetData(snapshot);
                TransformConfigTokens(snapshot, UnprotectSecret);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static ArenaSnapshot SnapshotForPersistence(ArenaSnapshot snapshot)
    {
        if (!snapshot.Configs.Values.Any(config => !string.IsNullOrEmpty(config.ApiToken)))
        {
            return snapshot;
        }

        // Work on a deep clone so the caller's in-memory snapshot keeps usable tokens.
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var clone = JsonSerializer.Deserialize<ArenaSnapshot>(json, JsonOptions) ?? snapshot;
        TransformConfigTokens(clone, ProtectSecret);
        return clone;
    }

    private static void TransformConfigTokens(ArenaSnapshot snapshot, Func<string, string> transform)
    {
        foreach (var key in snapshot.Configs.Keys.ToArray())
        {
            var config = snapshot.Configs[key];
            if (string.IsNullOrEmpty(config.ApiToken))
            {
                continue;
            }

            var transformed = transform(config.ApiToken);
            if (!transformed.Equals(config.ApiToken, StringComparison.Ordinal))
            {
                snapshot.Configs[key] = CloneWithApiToken(config, transformed);
            }
        }
    }

    private static ModelProviderConfig CloneWithApiToken(ModelProviderConfig config, string apiToken)
    {
        return new ModelProviderConfig
        {
            BaseUrl = config.BaseUrl,
            ApiMode = config.ApiMode,
            ApiToken = apiToken,
            Model = config.Model,
            Timeout = config.Timeout,
            Temperature = config.Temperature,
            MaxOutputTokens = config.MaxOutputTokens,
            ContextLength = config.ContextLength,
            Reasoning = config.Reasoning,
            NativeStatefulChat = config.NativeStatefulChat,
            NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
            PreviousResponseId = config.PreviousResponseId,
            LastError = config.LastError,
            LastLatencyMs = config.LastLatencyMs,
            LastTestOk = config.LastTestOk,
            Extra = config.Extra
        };
    }

    public async Task SaveSnapshotAsync(ArenaSnapshot snapshot, string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var path = SnapshotPath(sessionId);
        var fullPath = Path.GetFullPath(path);
        using var processLock = await SnapshotWriteLocks.AcquireAsync(fullPath, cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(fullPath, SnapshotWriteLeaseTimeout, cancellationToken);
        await SaveSnapshotCoreAsync(snapshot, fullPath, rejectStaleRevision: true, cancellationToken);
    }

    private static async Task SaveSnapshotCoreAsync(
        ArenaSnapshot snapshot,
        string fullPath,
        bool rejectStaleRevision,
        CancellationToken cancellationToken)
    {
        ScrubRemovedLegacyInternetData(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var currentRevision = await ReadPersistenceRevisionAsync(fullPath, cancellationToken);
        var expectedRevision = Math.Max(0, snapshot.PersistenceRevision);
        if (rejectStaleRevision && File.Exists(fullPath) && currentRevision != expectedRevision)
        {
            throw new SnapshotConcurrencyException(fullPath, expectedRevision, currentRevision);
        }

        var nextRevision = checked(currentRevision + 1);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        snapshot.PersistenceRevision = nextRevision;
        try
        {
            var persisted = SnapshotForPersistence(snapshot);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
            }

            await ReplaceSnapshotFileAsync(tempPath, fullPath, cancellationToken);
        }
        catch
        {
            snapshot.PersistenceRevision = expectedRevision;
            throw;
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    internal static bool ScrubRemovedLegacyInternetData(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var removed = false;
        var extensionData = snapshot.Engine.Extra;
        if (extensionData is not null && extensionData.Count > 0)
        {
            foreach (var key in extensionData.Keys.ToArray())
            {
                if (RemovedLegacyInternetKeys.Contains(key))
                {
                    removed |= extensionData.Remove(key);
                }
            }
        }

        if (snapshot.Engine.Messages is { Count: > 0 } messages)
        {
            removed |= messages.RemoveAll(IsExactLegacyCuratedNewsMessage) > 0;
        }

        return removed;
    }

    private static bool IsExactLegacyCuratedNewsMessage(DialogueMessage message)
    {
        return string.Equals(message.Speaker, "Curated News", StringComparison.Ordinal)
            && string.Equals(message.SpeakerId, "news", StringComparison.Ordinal)
            && string.Equals(message.Kind, "news", StringComparison.Ordinal);
    }

    private static async Task<long> ReadPersistenceRevisionAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("persistence_revision", out var revision)
                && revision.ValueKind == JsonValueKind.Number
                && revision.TryGetInt64(out var value)
                ? Math.Max(0, value)
                : 0;
        }
        catch (JsonException)
        {
            // Preserve the existing recovery behavior: a valid snapshot can replace
            // a corrupt/legacy file whose revision cannot be read.
            return 0;
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
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not mask the persistence result or original failure.
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
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void DeleteDirectoryTree(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (DirectoryIsReparsePoint(directory))
        {
            ClearReadOnly(directory);
            Directory.Delete(directory);
            return;
        }

        foreach (var file in SafeEnumerateFiles(directory, "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearReadOnly(file);
            File.Delete(file);
        }

        foreach (var childDirectory in SafeEnumerateChildDirectories(directory))
        {
            DeleteDirectoryTree(childDirectory, cancellationToken);
        }

        ClearReadOnly(directory);
        Directory.Delete(directory);
    }

    public async Task CreateSessionAsync(string newSessionId, ArenaSnapshot template, CancellationToken cancellationToken = default)
    {
        _ = await TryCreateSessionAsync(newSessionId, template, cancellationToken);
    }

    public async Task<bool> TryCreateSessionAsync(string newSessionId, ArenaSnapshot template, CancellationToken cancellationToken = default)
    {
        var safeSession = SafeSessionId(newSessionId);
        if (string.IsNullOrWhiteSpace(safeSession))
        {
            throw new ArgumentException("Session name is required.", nameof(newSessionId));
        }

        var cloneJson = JsonSerializer.Serialize(template, JsonOptions);
        var clone = JsonSerializer.Deserialize<ArenaSnapshot>(cloneJson, JsonOptions) ?? new ArenaSnapshot();
        clone.Engine.Messages.Clear();
        clone.Engine.Narration.Clear();
        clone.Engine.TurnCount = 0;
        clone.Engine.TurnIndex = 0;
        clone.Engine.LastError = "";
        clone.Engine.Narrator.Status = "idle";
        clone.Engine.Narrator.LastError = "";
        foreach (var agent in clone.Engine.Agents)
        {
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
        }

        var fullPath = Path.GetFullPath(SnapshotPath(safeSession));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(fullPath, cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(fullPath, SnapshotWriteLeaseTimeout, cancellationToken);
        if (File.Exists(fullPath))
        {
            return false;
        }

        await SaveSnapshotCoreAsync(clone, fullPath, rejectStaleRevision: false, cancellationToken);
        return true;
    }

    /// <summary>
    /// Creates an independent, full-state branch of a persisted session. The source
    /// snapshot is read at one authoritative persistence revision and is never
    /// written by this operation. Target names are reserved with create-new file
    /// semantics, so an existing session is never replaced.
    /// </summary>
    public async Task<SessionForkResult> ForkSessionAsync(
        string sourceSessionId,
        string? targetSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var safeSourceSessionId = SafeSessionId(sourceSessionId);
        var sourcePath = Path.GetFullPath(SnapshotPath(safeSourceSessionId));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Session '{safeSourceSessionId}' has no persisted snapshot to fork.", sourcePath);
        }

        ArenaSnapshot sourceSnapshot;
        using (await SnapshotWriteLocks.AcquireAsync(sourcePath, cancellationToken))
        using (await CrossProcessWriteLease.AcquireAsync(sourcePath, SnapshotWriteLeaseTimeout, cancellationToken))
        {
            sourceSnapshot = await LoadSnapshotAsync(safeSourceSessionId, cancellationToken)
                ?? throw new InvalidDataException($"Session '{safeSourceSessionId}' has an unreadable snapshot and cannot be forked.");
        }

        var sourceRevision = Math.Max(0, sourceSnapshot.PersistenceRevision);
        var forkedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var forkSnapshot = CloneSnapshot(sourceSnapshot);
        NormalizeForkSnapshot(forkSnapshot, safeSourceSessionId, sourceRevision, forkedAt);

        var baseTargetSessionId = string.IsNullOrWhiteSpace(targetSessionId)
            ? SafeSessionId($"{safeSourceSessionId}-fork-t{Math.Max(0, sourceSnapshot.Engine.TurnCount)}")
            : ValidateExplicitForkTargetSessionId(targetSessionId);

        for (var attempt = 0; attempt < MaxForkNameAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateSessionId = attempt == 0
                ? baseTargetSessionId
                : SafeSessionId($"{baseTargetSessionId}-{attempt + 1}");
            var targetPath = Path.GetFullPath(SnapshotPath(candidateSessionId));
            var targetDirectory = Path.GetDirectoryName(targetPath)!;
            var targetDirectoryExisted = Directory.Exists(targetDirectory);
            if (SessionIdentityExists(candidateSessionId, targetPath))
            {
                continue;
            }

            try
            {
                using var processLock = await SnapshotWriteLocks.AcquireAsync(targetPath, cancellationToken);
                using var writeLease = await CrossProcessWriteLease.AcquireAsync(targetPath, SnapshotWriteLeaseTimeout, cancellationToken);
                if (File.Exists(targetPath)
                    || SessionSideArtifactsExist(candidateSessionId)
                    || TargetDirectoryContainsUnexpectedEntries(targetDirectory, targetPath))
                {
                    continue;
                }

                forkSnapshot.PersistenceRevision = 0;
                if (!await TryCreateSnapshotFileAsync(forkSnapshot, targetPath, cancellationToken))
                {
                    continue;
                }

                return new SessionForkResult(
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    forkSnapshot.PersistenceRevision,
                    forkSnapshot.Engine.TurnCount,
                    forkSnapshot.Engine.Messages.Count,
                    forkSnapshot.Engine.Narration.Count,
                    forkSnapshot.Engine.Agents.Count(agent => agent.Active),
                    forkSnapshot.GenerationHistory.Count,
                    forkedAt);
            }
            finally
            {
                if (!targetDirectoryExisted && !File.Exists(targetPath))
                {
                    TryDeleteEmptyDirectory(targetDirectory);
                }
            }
        }

        throw new IOException($"Could not reserve a unique fork name based on '{baseTargetSessionId}'.");
    }

    private static string ValidateExplicitForkTargetSessionId(string targetSessionId)
    {
        var safeTargetSessionId = SafeSessionId(targetSessionId);
        if (safeTargetSessionId.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !targetSessionId.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Fork target name must contain at least one valid session-name character.", nameof(targetSessionId));
        }

        return safeTargetSessionId;
    }

    private bool SessionIdentityExists(string sessionId, string snapshotPath)
    {
        return Directory.Exists(Path.GetDirectoryName(snapshotPath))
            || SessionSideArtifactsExist(sessionId);
    }

    private bool SessionSideArtifactsExist(string sessionId)
    {
        var eventDirectory = Path.GetDirectoryName(NativeDataPaths.EventPath(DataRoot, sessionId));
        return Directory.Exists(CheckpointDirectory(sessionId))
            || (!string.IsNullOrWhiteSpace(eventDirectory) && Directory.Exists(eventDirectory));
    }

    private static bool TargetDirectoryContainsUnexpectedEntries(string targetDirectory, string snapshotPath)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        var activeLeasePath = $"{snapshotPath}.write.lock";
        try
        {
            return Directory.EnumerateFileSystemEntries(targetDirectory)
                .Any(path => !path.Equals(activeLeasePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static ArenaSnapshot CloneSnapshot(ArenaSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return JsonSerializer.Deserialize<ArenaSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("The source session snapshot could not be cloned.");
    }

    private static void NormalizeForkSnapshot(
        ArenaSnapshot snapshot,
        string parentSessionId,
        long parentPersistenceRevision,
        long forkedAt)
    {
        snapshot.PersistenceRevision = 0;
        snapshot.ForkLineage = new SessionForkLineage
        {
            ParentSessionId = parentSessionId,
            ParentPersistenceRevision = parentPersistenceRevision,
            ParentTurnCount = snapshot.Engine.TurnCount,
            ParentMessageCount = snapshot.Engine.Messages.Count,
            ForkedAt = forkedAt
        };
        snapshot.Engine.LastError = "";
        snapshot.Engine.Narrator.Status = "idle";
        snapshot.Engine.Narrator.LastError = "";
        foreach (var agent in snapshot.Engine.Agents)
        {
            agent.Status = agent.Active ? "waiting" : "muted";
        }
    }

    private static async Task<bool> TryCreateSnapshotFileAsync(
        ArenaSnapshot snapshot,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(fullPath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var originalRevision = snapshot.PersistenceRevision;
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        snapshot.PersistenceRevision = 1;
        try
        {
            var persisted = SnapshotForPersistence(snapshot);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(tempPath, fullPath);
                return true;
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                snapshot.PersistenceRevision = originalRevision;
                return false;
            }
        }
        catch
        {
            snapshot.PersistenceRevision = originalRevision;
            throw;
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort cleanup after a cancelled or failed create-new operation.
        }
    }

    public static ArenaSnapshot CreateDefaultSnapshot()
    {
        var snapshot = new ArenaSnapshot
        {
            MatchType = "balanced"
        };

        snapshot.Configs["shared"] = new ModelProviderConfig();
        snapshot.MatchLocks["scenario"] = false;
        snapshot.Engine.Steering.Topic = "";
        snapshot.Engine.Steering.Global = "";
        snapshot.Engine.Narrator.Persona = "Neutral observer. Track the exchange without joining as Alpha, Beta, Gamma, or Delta.";
        snapshot.Engine.Narrator.Status = "idle";
        snapshot.Engine.Agents.AddRange(
        [
            new DialogueAgent
            {
                Id = "alpha",
                Name = "Alpha",
                Persona = "Practical strategist. Surfaces assumptions, proposes concrete options, and keeps the exchange moving.",
                Active = true,
                Status = "waiting"
            },
            new DialogueAgent
            {
                Id = "beta",
                Name = "Beta",
                Persona = "Critical reviewer. Tests weak premises, edge cases, and hidden tradeoffs before accepting conclusions.",
                Active = true,
                Status = "waiting"
            },
            new DialogueAgent
            {
                Id = "gamma",
                Name = "Gamma",
                Persona = "Evidence mapper. Separates facts from guesses and asks what would change the current conclusion.",
                Active = true,
                Status = "waiting"
            },
            new DialogueAgent
            {
                Id = "delta",
                Name = "Delta",
                Persona = "Boundary tester. Identifies limits, misuse cases, escalation paths, and operational failure boundaries.",
                Active = true,
                Status = "waiting"
            }
        ]);

        return snapshot;
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
        if (!PathIsInsideDirectory(sessionsRoot, sessionPath) || !Directory.Exists(sessionPath))
        {
            return Task.FromResult(false);
        }

        try
        {
            DeleteDirectoryTree(sessionPath, cancellationToken);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Task.FromResult(false);
        }
    }

    public bool SettingsExists() => File.Exists(SettingsPath);

    public int CountCheckpoints(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        var checkpointsPath = NativeDataPaths.CheckpointDirectory(DataRoot, safeSession);
        return CountFiles(checkpointsPath, "*.json");
    }

    public async Task<IReadOnlyList<CheckpointSummary>> ListCheckpointsAsync(string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var checkpointDir = CheckpointDirectory(sessionId);
        if (!Directory.Exists(checkpointDir))
        {
            return Array.Empty<CheckpointSummary>();
        }

        var checkpoints = new List<CheckpointSummary>();
        foreach (var path in SafeEnumerateFiles(checkpointDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var metadata = await ReadCheckpointMetadataAsync(stream, cancellationToken);
                if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Id))
                {
                    checkpoints.Add(new CheckpointSummary(metadata.Id, metadata.Name, metadata.SessionId, metadata.CreatedAt, path));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Ignore unreadable metadata. Full snapshot validation is intentionally
                // deferred until the operator selects a checkpoint to restore.
            }
        }

        return checkpoints
            .OrderByDescending(checkpoint => checkpoint.CreatedAt)
            .ToArray();
    }

    private static async Task<CheckpointMetadata?> ReadCheckpointMetadataAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CheckpointMetadataPrefixBytes);
        try
        {
            var bytesRead = 0;
            while (bytesRead < CheckpointMetadataPrefixBytes)
            {
                var readLength = Math.Min(
                    CheckpointMetadataReadChunkBytes,
                    CheckpointMetadataPrefixBytes - bytesRead);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(bytesRead, readLength),
                    cancellationToken);
                bytesRead += read;

                if (TryReadCheckpointMetadata(buffer.AsSpan(0, bytesRead), read == 0, out var metadata))
                {
                    return metadata;
                }

                if (read == 0)
                {
                    break;
                }
            }

            // Legacy or manually-authored checkpoints may place metadata after the
            // snapshot. Preserve compatibility by using the original full reader
            // only when the bounded header scan cannot find all metadata fields.
            stream.Position = 0;
            var record = await JsonSerializer.DeserializeAsync<CheckpointRecord>(stream, JsonOptions, cancellationToken);
            return record is null
                ? null
                : new CheckpointMetadata(record.Id, record.Name, record.SessionId, record.CreatedAt);
        }
        finally
        {
            // A prefix can include provider configuration from unusually small
            // snapshots, so do not return its bytes to the shared pool uncleared.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool TryReadCheckpointMetadata(
        ReadOnlySpan<byte> json,
        bool isFinalBlock,
        out CheckpointMetadata? metadata)
    {
        metadata = null;
        var reader = new Utf8JsonReader(
            json,
            isFinalBlock,
            new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }));

        string? propertyName = null;
        var id = "";
        var name = "";
        var checkpointSessionId = "default";
        long createdAt = 0;
        var hasId = false;
        var hasName = false;
        var hasSessionId = false;
        var hasCreatedAt = false;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    propertyName = reader.GetString();
                    continue;
                }

                if (propertyName is null || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (propertyName.Equals("id", StringComparison.OrdinalIgnoreCase)
                    && reader.TokenType == JsonTokenType.String)
                {
                    id = reader.GetString() ?? "";
                    hasId = true;
                }
                else if (propertyName.Equals("name", StringComparison.OrdinalIgnoreCase)
                         && reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
                {
                    name = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    hasName = true;
                }
                else if (propertyName.Equals("session_id", StringComparison.OrdinalIgnoreCase)
                         && reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
                {
                    checkpointSessionId = reader.TokenType == JsonTokenType.String
                        ? reader.GetString() ?? "default"
                        : "default";
                    hasSessionId = true;
                }
                else if (propertyName.Equals("created_at", StringComparison.OrdinalIgnoreCase)
                         && reader.TokenType == JsonTokenType.Number
                         && reader.TryGetInt64(out var value))
                {
                    createdAt = value;
                    hasCreatedAt = true;
                }

                propertyName = null;
                if (hasId && hasName && hasSessionId && hasCreatedAt)
                {
                    metadata = new CheckpointMetadata(id, name, checkpointSessionId, createdAt);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // An incomplete prefix is expected for large or legacy records. The
            // caller either reads more bytes or falls back to full deserialization.
        }

        return false;
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
            Snapshot = SnapshotForPersistence(snapshot)
        };

        var checkpointDir = CheckpointDirectory(sessionId);
        Directory.CreateDirectory(checkpointDir);
        var path = Path.Combine(checkpointDir, $"{id}.json");
        var fullPath = Path.GetFullPath(path);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
            }

            await ReplaceSnapshotFileAsync(tempPath, fullPath, cancellationToken);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }

        return new CheckpointSummary(id, checkpointName, sessionId, record.CreatedAt, fullPath);
    }

    public async Task<CheckpointSummary?> RestoreCheckpointAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var path = SafeCheckpointPath(sessionId, checkpointId);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        CheckpointRecord? record;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            record = await JsonSerializer.DeserializeAsync<CheckpointRecord>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }

        if (record?.Snapshot is null)
        {
            return null;
        }

        var snapshotPath = Path.GetFullPath(SnapshotPath(sessionId));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(snapshotPath, SnapshotWriteLeaseTimeout, cancellationToken);
        // Restoring a checkpoint is an explicit whole-snapshot replacement, so
        // it intentionally supersedes the live revision while still advancing it.
        await SaveSnapshotCoreAsync(record.Snapshot, snapshotPath, rejectStaleRevision: false, cancellationToken);

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

        ClearReadOnly(path);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public string SnapshotPath(string sessionId = "default") => NativeDataPaths.SessionSnapshotPath(DataRoot, sessionId);

    public string CheckpointDirectory(string sessionId = "default")
    {
        return NativeDataPaths.CheckpointDirectory(DataRoot, sessionId);
    }

    public static string SafeSessionId(string sessionId) => NativeDataPaths.SafeSessionId(sessionId);

    private string? SafeCheckpointPath(string sessionId, string checkpointId)
    {
        var safeId = SafeCheckpointId(checkpointId);
        if (string.IsNullOrWhiteSpace(safeId))
        {
            return null;
        }

        var checkpointDir = Path.GetFullPath(CheckpointDirectory(sessionId));
        var path = Path.GetFullPath(Path.Combine(checkpointDir, $"{safeId}.json"));
        return PathIsInsideDirectory(checkpointDir, path) ? path : null;
    }

    private static string SafeCheckpointId(string? checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            return "";
        }

        var invalid = Path.GetInvalidFileNameChars()
            .Append(Path.DirectorySeparatorChar)
            .Append(Path.AltDirectorySeparatorChar)
            .ToHashSet();
        var cleaned = new string(checkpointId
            .Trim()
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray())
            .Trim('-', '.', ' ');

        return string.IsNullOrWhiteSpace(cleaned) || cleaned.All(ch => ch == '.') || cleaned.Length > MaxSafeCheckpointIdLength
            ? ""
            : cleaned;
    }

    private static bool PathIsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessionsRoot = NativeDataPaths.SessionsRoot(DataRoot);
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<SessionSummary>();
        }

        var summaries = new List<SessionSummary>();
        foreach (var sessionDir in SafeEnumerateDirectories(sessionsRoot).OrderBy(Path.GetFileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(sessionDir);
            var snapshotPath = Path.Combine(sessionDir, "snapshot.json");
            var messageCount = File.Exists(snapshotPath)
                ? await CountSnapshotMessagesAsync(snapshotPath, cancellationToken)
                : 0;
            var checkpointPath = CheckpointDirectory(id);
            var eventPath = NativeDataPaths.EventPath(DataRoot, id);
            var lastModified = DirectoryLastWriteTimeOrNow(sessionDir);
            summaries.Add(new SessionSummary(
                id,
                snapshotPath,
                File.Exists(snapshotPath),
                messageCount,
                CountFiles(checkpointPath, "*.json"),
                CountLines(eventPath),
                new DateTimeOffset(lastModified)));
        }

        return summaries;
    }

    /// <summary>
    /// Message counts for the session list used to deserialize every snapshot,
    /// which cost seconds once a data root held hundreds of sessions. The count
    /// is now read by streaming past everything except engine.messages, and
    /// cached against the file's write time so unchanged sessions are free.
    /// </summary>
    private async Task<int> CountSnapshotMessagesAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        DateTime writeUtc;
        long length;
        try
        {
            var info = new FileInfo(snapshotPath);
            writeUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (MessageCountCache.TryGetValue(snapshotPath, out var cached)
            && cached.WriteUtc == writeUtc
            && cached.Length == length)
        {
            return cached.Count;
        }

        int count;
        try
        {
            await using var stream = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
            using (document)
            {
                count = document.RootElement.TryGetProperty("engine", out var engine)
                    && engine.ValueKind == JsonValueKind.Object
                    && engine.TryGetProperty("messages", out var messages)
                    && messages.ValueKind == JsonValueKind.Array
                        ? messages.GetArrayLength()
                        : 0;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or locked snapshot degrades to zero, as it did before.
            return 0;
        }

        MessageCountCache[snapshotPath] = (writeUtc, length, count);
        return count;
    }

    private async Task<ArenaSnapshot?> TryLoadSnapshotForSummaryAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadSnapshotAsync(sessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                    .Where(directory => !DirectoryIsReparsePoint(directory))
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) && !DirectoryIsReparsePoint(directory)
                ? Directory.EnumerateFiles(directory, pattern).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) && !DirectoryIsReparsePoint(directory)
                ? Directory.EnumerateFiles(directory, pattern).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> SafeEnumerateChildDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) && !DirectoryIsReparsePoint(directory)
                ? Directory.EnumerateDirectories(directory).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Event logs are append-only, so a file whose write stamp and length are
    /// unchanged still has the same number of lines. Listing a data root with
    /// hundreds of sessions used to re-read every log in full.
    /// </summary>
    private static int CountLines(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        DateTime writeUtc;
        long length;
        try
        {
            var info = new FileInfo(path);
            writeUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (EventLineCountCache.TryGetValue(path, out var cached)
            && cached.WriteUtc == writeUtc
            && cached.Length == length)
        {
            return cached.Count;
        }

        var counted = CountLinesUncached(path);
        EventLineCountCache[path] = (writeUtc, length, counted);
        return counted;
    }

    private static int CountLinesUncached(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var count = 0;
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static DateTime DirectoryLastWriteTimeOrNow(string directory)
    {
        try
        {
            return Directory.GetLastWriteTime(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return DateTime.Now;
        }
    }

    private static bool DirectoryIsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private sealed record CheckpointMetadata(string Id, string Name, string SessionId, long CreatedAt);

}

public sealed record SessionForkResult(
    string SourceSessionId,
    string TargetSessionId,
    long SourcePersistenceRevision,
    long TargetPersistenceRevision,
    int TurnCount,
    int MessageCount,
    int NarrationCount,
    int ActiveAgentCount,
    int GenerationHistoryCount,
    long ForkedAt);

public sealed class SnapshotConcurrencyException : IOException
{
    public SnapshotConcurrencyException(string path, long expectedRevision, long currentRevision)
        : base($"Snapshot changed after it was loaded. Reload before saving '{path}' (expected revision {expectedRevision}, current revision {currentRevision}).")
    {
        Path = path;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
    }

    public string Path { get; }

    public long ExpectedRevision { get; }

    public long CurrentRevision { get; }
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
    [JsonPropertyOrder(0)]
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyOrder(1)]
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyOrder(2)]
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "default";

    [JsonPropertyOrder(3)]
    [JsonPropertyName("app_version")]
    public string AppVersion { get; init; } = "wpf-beta";

    [JsonPropertyOrder(4)]
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyOrder(100)]
    [JsonPropertyName("snapshot")]
    public ArenaSnapshot Snapshot { get; init; } = new();
}
