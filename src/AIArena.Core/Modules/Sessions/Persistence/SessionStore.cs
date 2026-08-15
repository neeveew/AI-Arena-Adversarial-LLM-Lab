using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;

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
    private const int MaxCheckpointNameLength = 80;
    private const int SavedStateTrashSchemaVersion = 1;
    internal const int SnapshotMutationGenerationCapacity = 4_096;
    internal const int SessionSummaryCountCacheCapacity = 1_024;
    internal const int DefaultSavedStateTrashEntryLimit = 64;
    private static readonly TimeSpan SnapshotSaveRetryDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan SnapshotWriteLeaseTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan DefaultSavedStateTrashRetention = TimeSpan.FromDays(7);
    private static readonly KeyedAsyncLockRegistry SnapshotWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly KeyedAsyncLockRegistry SavedStateTrashLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot path to its last observed write stamp and message count. Shared
    /// across stores because the key is a full path, and a data root can be
    /// shared with other AI Arena implementations.
    /// </summary>
    private static readonly BoundedPathCountCache MessageCountCache =
        new(SessionSummaryCountCacheCapacity);

    /// <summary>Event log path to its last observed write stamp and line count.</summary>
    private static readonly BoundedPathCountCache EventLineCountCache =
        new(SessionSummaryCountCacheCapacity);
    private static readonly object SnapshotMutationGenerationGate = new();
    private static readonly Dictionary<string, (long Stamp, LinkedListNode<string> RecencyNode)> SnapshotMutationGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> SnapshotMutationGenerationRecency = new();
    private static long snapshotMutationSequence;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan savedStateTrashRetention;
    private readonly int savedStateTrashEntryLimit;
    private readonly Func<string, CancellationToken, Task> checkpointDurableCommitObserver;
    private readonly Func<SavedStateDeletionReceipt, CancellationToken, Task> savedStateTrashPreparedObserver;

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
        : this(dataRoot, TimeProvider.System, DefaultSavedStateTrashRetention, DefaultSavedStateTrashEntryLimit)
    {
    }

    internal SessionStore(
        string? dataRoot,
        TimeProvider timeProvider,
        TimeSpan savedStateTrashRetention,
        int savedStateTrashEntryLimit,
        Func<string, CancellationToken, Task>? checkpointDurableCommitObserver = null,
        Func<SavedStateDeletionReceipt, CancellationToken, Task>? savedStateTrashPreparedObserver = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (savedStateTrashRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(savedStateTrashRetention));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(savedStateTrashEntryLimit, 1);
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
        this.timeProvider = timeProvider;
        this.savedStateTrashRetention = savedStateTrashRetention;
        this.savedStateTrashEntryLimit = savedStateTrashEntryLimit;
        this.checkpointDurableCommitObserver = checkpointDurableCommitObserver
            ?? (static (_, _) => Task.CompletedTask);
        this.savedStateTrashPreparedObserver = savedStateTrashPreparedObserver
            ?? (static (_, _) => Task.CompletedTask);
    }

    public string DataRoot { get; }

    internal string SavedStateTrashRoot => Path.Combine(DataRoot, ".trash", "saved-state");

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
                StructuredMemoryService.NormalizeSnapshot(snapshot);
                ModelRuntimeSettingsRegistry.Normalize(snapshot);
                TransformConfigTokens(snapshot, UnprotectSecret);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
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
            ExplicitModelAssignment = config.ExplicitModelAssignment,
            Timeout = config.Timeout,
            Temperature = config.Temperature,
            MaxOutputTokens = config.MaxOutputTokens,
            ContextLength = config.ContextLength,
            ConfiguredContextWindow = config.ConfiguredContextWindow,
            HistoryPolicy = config.HistoryPolicy,
            ResponseTone = config.ResponseTone,
            CustomTone = config.CustomTone,
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
        ArgumentNullException.ThrowIfNull(snapshot);
        var safeSession = SafeSessionId(sessionId);
        var path = SnapshotPath(safeSession);
        var fullPath = Path.GetFullPath(path);
        var snapshotDirectory = Path.GetDirectoryName(fullPath)!;
        var snapshotExistedAtRequest = File.Exists(fullPath);
        SavedStateTrashMaintenanceScope? identityReservation = null;
        try
        {
            if (!snapshotExistedAtRequest)
            {
                identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
                if (await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                        identityReservation.TrashRoot,
                        safeSession,
                        cancellationToken))
                {
                    throw new SessionIdentityConflictException(
                        $"Session identity '{safeSession}' is reserved in Trash.");
                }
            }

            using var processLock = await SnapshotWriteLocks.AcquireAsync(fullPath, cancellationToken);
            using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                SessionTreeLeaseTarget(fullPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (File.Exists(fullPath) != snapshotExistedAtRequest)
            {
                throw new SessionIdentityConflictException(
                    $"Session identity '{safeSession}' changed while the snapshot save was waiting.");
            }

            if (!snapshotExistedAtRequest
                && (SessionSideArtifactsExist(safeSession)
                    || TargetDirectoryContainsUnexpectedEntries(snapshotDirectory, fullPath)))
            {
                throw new SessionIdentityConflictException(
                    $"Session identity '{safeSession}' is already reserved.");
            }

            using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
                ExperimentProviderLeaseTarget(fullPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            using var writeLease = await CrossProcessWriteLease.AcquireAsync(fullPath, SnapshotWriteLeaseTimeout, cancellationToken);
            if (snapshotExistedAtRequest)
            {
                var authoritative = await LoadSnapshotAsync(safeSession, cancellationToken);
                var authoritativeInstanceId = authoritative?.SessionInstanceId;
                if (IsValidSessionInstanceId(authoritativeInstanceId))
                {
                    if (IsValidSessionInstanceId(snapshot.SessionInstanceId)
                        && !snapshot.SessionInstanceId.Equals(authoritativeInstanceId, StringComparison.Ordinal))
                    {
                        throw new SessionIdentityConflictException(
                            $"Session identity '{safeSession}' now belongs to a different session instance.");
                    }

                    snapshot.SessionInstanceId = authoritativeInstanceId!;
                }
                else if (!IsValidSessionInstanceId(snapshot.SessionInstanceId))
                {
                    snapshot.SessionInstanceId = NewSessionInstanceId();
                }
            }
            else
            {
                snapshot.SessionInstanceId = NewSessionInstanceId();
            }

            await SaveSnapshotCoreAsync(snapshot, fullPath, rejectStaleRevision: true, cancellationToken);
        }
        finally
        {
            identityReservation?.Dispose();
            if (!snapshotExistedAtRequest && !File.Exists(fullPath))
            {
                TryDeleteEmptyDirectory(snapshotDirectory);
            }
        }
    }

    /// <summary>
    /// Returns the durable identity for one live session incarnation. Legacy
    /// snapshots are migrated atomically under the normal session write order so
    /// callers never need to fall back to the reusable display name.
    /// </summary>
    public async Task<string> EnsureSessionInstanceIdAsync(
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        var safeSession = SafeSessionId(sessionId);
        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || !File.Exists(snapshotPath)
            || PathIsReparsePoint(snapshotPath))
        {
            throw new FileNotFoundException("The live session snapshot is unavailable.", snapshotPath);
        }

        using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
            ExperimentProviderLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(
            snapshotPath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || !File.Exists(snapshotPath)
            || PathIsReparsePoint(snapshotPath))
        {
            throw new FileNotFoundException("The live session snapshot is unavailable.", snapshotPath);
        }

        var snapshot = await LoadSnapshotAsync(safeSession, cancellationToken)
            ?? throw new InvalidDataException("The live session snapshot could not be read safely.");
        if (IsValidSessionInstanceId(snapshot.SessionInstanceId))
        {
            return snapshot.SessionInstanceId;
        }

        snapshot.SessionInstanceId = NewSessionInstanceId();
        await SaveSnapshotCoreAsync(snapshot, snapshotPath, rejectStaleRevision: false, cancellationToken);
        return snapshot.SessionInstanceId;
    }

    public static bool IsValidSessionInstanceId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && Guid.TryParseExact(value, "N", out var parsed)
            && value.Equals(parsed.ToString("N"), StringComparison.Ordinal);
    }

    private static string NewSessionInstanceId() => Guid.NewGuid().ToString("N");

    private static string ExperimentProviderLeaseTarget(string fullSnapshotPath) =>
        $"{fullSnapshotPath}.experiment-provider-call";

    private string SessionTreeLeaseTarget(string fullSnapshotPath) =>
        SessionTreeLeaseTargetForSnapshot(DataRoot, fullSnapshotPath);

    internal static string SessionTreeLeaseTargetForSnapshot(string dataRootPath, string fullSnapshotPath)
    {
        var dataRoot = Path.GetFullPath(dataRootPath);
        var lockContainer = Path.GetFullPath(Path.Combine(dataRoot, ".locks"));
        var sessionTreeRoot = Path.GetFullPath(Path.Combine(lockContainer, "session-tree"));
        if (!PathIsInsideDirectory(dataRoot, sessionTreeRoot))
        {
            throw new IOException("The session-tree lock path escaped the AI Arena data root.");
        }

        EnsureDirectoryWithoutReparsePoint(dataRoot, "AI Arena data root");
        EnsureDirectoryWithoutReparsePoint(lockContainer, "lock");
        EnsureDirectoryWithoutReparsePoint(sessionTreeRoot, "session-tree lock");
        var normalizedPath = Path.GetFullPath(fullSnapshotPath).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();
        return Path.Combine(sessionTreeRoot, key);
    }

    private static async Task SaveSnapshotCoreAsync(
        ArenaSnapshot snapshot,
        string fullPath,
        bool rejectStaleRevision,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionInstanceId(snapshot.SessionInstanceId))
        {
            throw new InvalidDataException("The session instance identity is unavailable.");
        }

        ScrubRemovedLegacyInternetData(snapshot);
        StructuredMemoryService.NormalizeSnapshot(snapshot);
        ModelRuntimeSettingsRegistry.Normalize(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var currentRevision = await ReadPersistenceRevisionAsync(fullPath, cancellationToken);
        var expectedRevision = Math.Max(0, snapshot.PersistenceRevision);
        var snapshotExists = File.Exists(fullPath);
        if (rejectStaleRevision
            && (snapshotExists
                ? currentRevision != expectedRevision
                : expectedRevision != 0))
        {
            throw new SnapshotConcurrencyException(fullPath, expectedRevision, currentRevision);
        }

        var nextRevision = checked(currentRevision + 1);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        snapshot.PersistenceRevision = nextRevision;
        try
        {
            var persistenceJsonOptions = SnapshotPersistenceJson.CreateOptions(JsonOptions, ProtectSecret);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, persistenceJsonOptions, cancellationToken);
            }

            await ReplaceSnapshotFileAsync(tempPath, fullPath, cancellationToken);
            RecordSnapshotMutation(fullPath);
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
            return await PersistenceRevisionReader.ReadAsync(stream, cancellationToken);
        }
        catch (JsonException)
        {
            // A valid snapshot may replace a corrupt or legacy file whose
            // durable generation cannot be trusted.
            return 0;
        }
    }

    public long SnapshotMutationGeneration(string sessionId = "default")
    {
        var fullPath = Path.GetFullPath(SnapshotPath(sessionId));
        lock (SnapshotMutationGenerationGate)
        {
            return SnapshotMutationGenerations.TryGetValue(fullPath, out var entry)
                ? entry.Stamp
                : 0;
        }
    }

    internal static int SnapshotMutationGenerationCount
    {
        get
        {
            lock (SnapshotMutationGenerationGate)
            {
                return SnapshotMutationGenerations.Count;
            }
        }
    }

    internal static int MessageCountCacheCount => MessageCountCache.Count;

    internal static int EventLineCountCacheCount => EventLineCountCache.Count;

    internal static bool MessageCountCacheContains(string path) => MessageCountCache.Contains(path);

    internal static bool EventLineCountCacheContains(string path) => EventLineCountCache.Contains(path);

    internal static void RecordSnapshotMutation(string path)
    {
        var fullPath = Path.GetFullPath(path);
        MessageCountCache.Remove(fullPath);
        lock (SnapshotMutationGenerationGate)
        {
            var stamp = checked(++snapshotMutationSequence);
            if (SnapshotMutationGenerations.Remove(fullPath, out var previous))
            {
                SnapshotMutationGenerationRecency.Remove(previous.RecencyNode);
            }

            var node = SnapshotMutationGenerationRecency.AddFirst(fullPath);
            SnapshotMutationGenerations[fullPath] = (stamp, node);
            while (SnapshotMutationGenerations.Count > SnapshotMutationGenerationCapacity)
            {
                var oldest = SnapshotMutationGenerationRecency.Last;
                if (oldest is null)
                {
                    break;
                }

                SnapshotMutationGenerationRecency.RemoveLast();
                SnapshotMutationGenerations.Remove(oldest.Value);
            }
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
        if (!await TryCreateSessionAsync(newSessionId, template, cancellationToken))
        {
            throw new IOException($"Session identity '{SafeSessionId(newSessionId)}' is already reserved.");
        }
    }

    public async Task<bool> TryCreateSessionAsync(string newSessionId, ArenaSnapshot template, CancellationToken cancellationToken = default)
    {
        var safeSession = SafeSessionId(newSessionId);
        if (string.IsNullOrWhiteSpace(safeSession))
        {
            throw new ArgumentException("Session name is required.", nameof(newSessionId));
        }

        using var identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
        if (await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                identityReservation.TrashRoot,
                safeSession,
                cancellationToken))
        {
            return false;
        }

        var cloneJson = JsonSerializer.Serialize(template, JsonOptions);
        var clone = JsonSerializer.Deserialize<ArenaSnapshot>(cloneJson, JsonOptions) ?? new ArenaSnapshot();
        clone.SessionInstanceId = NewSessionInstanceId();
        clone.Engine.Messages.Clear();
        clone.Engine.Narration.Clear();
        clone.Engine.TurnCount = 0;
        clone.Engine.TurnIndex = 0;
        clone.Engine.MatchEnded = false;
        clone.Engine.MatchEndedAt = null;
        clone.Engine.MatchEndReason = "";
        clone.Engine.LastError = "";
        clone.Engine.Narrator.Status = "idle";
        clone.Engine.Narrator.LastError = "";
        foreach (var agent in clone.Engine.Agents)
        {
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
            agent.MemoryEntries.Clear();
        }

        var fullPath = Path.GetFullPath(SnapshotPath(safeSession));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(fullPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(fullPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        var targetDirectory = Path.GetDirectoryName(fullPath)!;
        if (File.Exists(fullPath)
            || SessionSideArtifactsExist(safeSession)
            || TargetDirectoryContainsUnexpectedEntries(targetDirectory, fullPath))
        {
            return false;
        }

        using var writeLease = await CrossProcessWriteLease.AcquireAsync(fullPath, SnapshotWriteLeaseTimeout, cancellationToken);
        if (File.Exists(fullPath)
            || SessionSideArtifactsExist(safeSession)
            || TargetDirectoryContainsUnexpectedEntries(targetDirectory, fullPath))
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
        using (await CrossProcessWriteLease.AcquireAsync(
                   SessionTreeLeaseTarget(sourcePath),
                   SnapshotWriteLeaseTimeout,
                   cancellationToken))
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
            using var identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
            if (SessionIdentityExists(candidateSessionId, targetPath)
                || await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                    identityReservation.TrashRoot,
                    candidateSessionId,
                    cancellationToken))
            {
                continue;
            }

            try
            {
                using var processLock = await SnapshotWriteLocks.AcquireAsync(targetPath, cancellationToken);
                using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                    SessionTreeLeaseTarget(targetPath),
                    SnapshotWriteLeaseTimeout,
                    cancellationToken);
                using var writeLease = await CrossProcessWriteLease.AcquireAsync(targetPath, SnapshotWriteLeaseTimeout, cancellationToken);
                if (File.Exists(targetPath)
                    || SessionSideArtifactsExist(candidateSessionId)
                    || TargetDirectoryContainsUnexpectedEntries(targetDirectory, targetPath))
                {
                    continue;
                }

                var candidateSnapshot = CloneSnapshot(forkSnapshot);
                var receipt = AttachBranchReceipt(
                    candidateSnapshot,
                    sourceSnapshot,
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    forkedAt,
                    sourceSnapshot.Engine.Messages.Count - 1,
                    ImmutableArray<ArenaEvidenceAssertion>.Empty);
                RebaseRetainedMemory(candidateSnapshot, sourceSnapshot.BranchReceipt?.Id ?? "", receipt.Id);
                candidateSnapshot.PersistenceRevision = 0;
                if (!await TryCreateSnapshotFileAsync(candidateSnapshot, targetPath, cancellationToken))
                {
                    continue;
                }

                return new SessionForkResult(
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    candidateSnapshot.PersistenceRevision,
                    candidateSnapshot.Engine.TurnCount,
                    candidateSnapshot.Engine.Messages.Count,
                    candidateSnapshot.Engine.Narration.Count,
                    candidateSnapshot.Engine.Agents.Count(agent => agent.Active),
                    candidateSnapshot.GenerationHistory.Count,
                    forkedAt)
                {
                    BranchReceiptId = receipt.Id,
                    CursorMessageId = receipt.ForkPoint.MessageId
                };
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

    /// <summary>
    /// Atomically creates one experiment-owned child. The source revision and
    /// replayable setup are validated while the source write leases are held,
    /// and the only durable child representation already contains its experiment
    /// identity and token-empty replacement provider setup.
    /// </summary>
    public async Task<SessionForkResult> ForkExperimentSessionAsync(
        string sourceSessionId,
        string targetSessionId,
        long expectedSourcePersistenceRevision,
        string expectedSourceSetupFingerprint,
        string experimentId,
        ModelProviderConfig replacementConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedSourcePersistenceRevision, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceSetupFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentNullException.ThrowIfNull(replacementConfig);
        if (expectedSourceSetupFingerprint.Length != 64
            || !expectedSourceSetupFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected source setup fingerprint must be a SHA-256 value.", nameof(expectedSourceSetupFingerprint));
        }
        if (experimentId.Length > 160 || experimentId.Any(char.IsControl))
        {
            throw new ArgumentException("Experiment identity must be bounded and contain no control characters.", nameof(experimentId));
        }
        if (!string.IsNullOrEmpty(replacementConfig.ApiToken))
        {
            throw new ArgumentException("Experiment child replacement configuration must not contain a provider credential.", nameof(replacementConfig));
        }

        var safeSourceSessionId = SafeSessionId(sourceSessionId);
        var safeTargetSessionId = ValidateExplicitForkTargetSessionId(targetSessionId);
        using var identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
        if (await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                identityReservation.TrashRoot,
                safeTargetSessionId,
                cancellationToken))
        {
            throw new IOException("The exact experiment child session identity is already reserved.");
        }

        var sourcePath = Path.GetFullPath(SnapshotPath(safeSourceSessionId));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Session '{safeSourceSessionId}' has no persisted snapshot to fork.", sourcePath);
        }

        using var sourceProcessLock = await SnapshotWriteLocks.AcquireAsync(sourcePath, cancellationToken);
        using var sourceSessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(sourcePath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var sourceExperimentCallLease = await CrossProcessWriteLease.AcquireAsync(
            ExperimentProviderLeaseTarget(sourcePath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var sourceWriteLease = await CrossProcessWriteLease.AcquireAsync(
            sourcePath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        var sourceSnapshot = await LoadSnapshotAsync(safeSourceSessionId, cancellationToken)
            ?? throw new InvalidDataException($"Session '{safeSourceSessionId}' has an unreadable snapshot and cannot be forked.");
        var sourceRevision = Math.Max(0, sourceSnapshot.PersistenceRevision);
        var sourceSetupFingerprint = SetupFingerprint(sourceSnapshot);
        if (sourceRevision != expectedSourcePersistenceRevision
            || !sourceSetupFingerprint.Equals(expectedSourceSetupFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArenaExperimentSourceChangedException();
        }

        var targetPath = Path.GetFullPath(SnapshotPath(safeTargetSessionId));
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var targetDirectoryExisted = Directory.Exists(targetDirectory);
        if (SessionIdentityExists(safeTargetSessionId, targetPath))
        {
            throw new IOException("The exact experiment child session identity is already reserved.");
        }

        try
        {
            using var targetProcessLock = await SnapshotWriteLocks.AcquireAsync(targetPath, cancellationToken);
            using var targetSessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                SessionTreeLeaseTarget(targetPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            using var targetExperimentCallLease = await CrossProcessWriteLease.AcquireAsync(
                ExperimentProviderLeaseTarget(targetPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            using var targetWriteLease = await CrossProcessWriteLease.AcquireAsync(
                targetPath,
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (File.Exists(targetPath)
                || SessionSideArtifactsExist(safeTargetSessionId)
                || TargetDirectoryContainsUnexpectedEntries(targetDirectory, targetPath))
            {
                throw new IOException("The exact experiment child session identity is already reserved.");
            }

            var forkedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var child = CloneSnapshot(sourceSnapshot);
            NormalizeForkSnapshot(child, safeSourceSessionId, sourceRevision, forkedAt);
            var receipt = AttachBranchReceipt(
                child,
                sourceSnapshot,
                safeSourceSessionId,
                safeTargetSessionId,
                sourceRevision,
                forkedAt,
                sourceSnapshot.Engine.Messages.Count - 1,
                ImmutableArray<ArenaEvidenceAssertion>.Empty) with
            {
                ExperimentId = experimentId
            };
            child.BranchReceipt = receipt;
            RebaseRetainedMemory(child, sourceSnapshot.BranchReceipt?.Id ?? "", receipt.Id);
            child.Configs.Clear();
            child.Configs[ModelProviderRouting.SharedConfigKey] = ExperimentProviderSetup(replacementConfig);
            ModelRuntimeSettingsRegistry.Normalize(child);
            child.PersistenceRevision = 0;
            var childSetupFingerprint = SetupFingerprint(child);
            if (!await TryCreateSnapshotFileAsync(child, targetPath, cancellationToken))
            {
                throw new IOException("The exact experiment child session identity is already reserved.");
            }

            return new SessionForkResult(
                safeSourceSessionId,
                safeTargetSessionId,
                sourceRevision,
                child.PersistenceRevision,
                child.Engine.TurnCount,
                child.Engine.Messages.Count,
                child.Engine.Narration.Count,
                child.Engine.Agents.Count(agent => agent.Active),
                child.GenerationHistory.Count,
                forkedAt)
            {
                BranchReceiptId = receipt.Id,
                CursorMessageId = receipt.ForkPoint.MessageId,
                ChildSetupFingerprint = childSetupFingerprint
            };
        }
        finally
        {
            if (!targetDirectoryExisted && !File.Exists(targetPath))
            {
                TryDeleteEmptyDirectory(targetDirectory);
            }
        }
    }

    internal async ValueTask<ArenaExperimentProviderCallLease> AcquireExperimentProviderCallLeaseAsync(
        ArenaExperimentChildGuard guard,
        long expectedPersistenceRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedPersistenceRevision, 1);
        var fullPath = Path.GetFullPath(SnapshotPath(guard.SessionId));
        var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(fullPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new ArenaExperimentChildDriftException();
            }

            var callLease = await CrossProcessWriteLease.AcquireAsync(
                ExperimentProviderLeaseTarget(fullPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await LoadSnapshotAsync(guard.SessionId, cancellationToken).ConfigureAwait(false);
                ValidateExperimentChild(snapshot, guard, expectedPersistenceRevision);
                return new ArenaExperimentProviderCallLease(
                    sessionTreeLease,
                    callLease,
                    expectedPersistenceRevision);
            }
            catch
            {
                callLease.Dispose();
                throw;
            }
        }
        catch
        {
            sessionTreeLease.Dispose();
            throw;
        }
    }

    internal async Task<long> ValidateExperimentChildAsync(
        ArenaExperimentChildGuard guard,
        long expectedPersistenceRevision,
        CancellationToken cancellationToken = default)
    {
        using var callLease = await AcquireExperimentProviderCallLeaseAsync(
            guard,
            expectedPersistenceRevision,
            cancellationToken).ConfigureAwait(false);
        return callLease.PersistenceRevision;
    }

    private static void ValidateExperimentChild(
        ArenaSnapshot? snapshot,
        ArenaExperimentChildGuard guard,
        long expectedPersistenceRevision)
    {
        if (snapshot is null
            || snapshot.PersistenceRevision != expectedPersistenceRevision
            || snapshot.BranchReceipt is not { } receipt
            || !string.Equals(receipt.ExperimentId, guard.ExperimentId, StringComparison.Ordinal)
            || !receipt.ParentSessionId.Equals(guard.ParentSessionId, StringComparison.Ordinal)
            || receipt.ParentRevision != guard.ParentPersistenceRevision
            || !receipt.SetupFingerprint.Equals(guard.ParentSetupFingerprint, StringComparison.OrdinalIgnoreCase)
            || !receipt.ChildSessionId.Equals(guard.SessionId, StringComparison.Ordinal)
            || snapshot.ForkLineage is not { } lineage
            || !lineage.ParentSessionId.Equals(guard.ParentSessionId, StringComparison.Ordinal)
            || lineage.ParentPersistenceRevision != guard.ParentPersistenceRevision
            || !SetupFingerprint(snapshot).Equals(guard.ChildSetupFingerprint, StringComparison.OrdinalIgnoreCase)
            || snapshot.Configs.Count != 1
            || !snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var config)
            || !string.IsNullOrEmpty(config.ApiToken)
            || config.Extra is { Count: > 0 })
        {
            throw new ArenaExperimentChildDriftException();
        }
    }

    /// <summary>
    /// Creates an isolated branch at one exact stable transcript message. State
    /// that cannot be proven to exist at the cursor is omitted rather than copied
    /// from the future. The source snapshot remains unchanged.
    /// </summary>
    public async Task<SessionForkResult> ForkSessionAtCursorAsync(
        string sourceSessionId,
        string cursorMessageId,
        string? targetSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cursorMessageId))
        {
            throw new ArgumentException("A stable transcript message cursor is required.", nameof(cursorMessageId));
        }

        var safeSourceSessionId = SafeSessionId(sourceSessionId);
        var sourcePath = Path.GetFullPath(SnapshotPath(safeSourceSessionId));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Session '{safeSourceSessionId}' has no persisted snapshot to fork.", sourcePath);
        }

        ArenaSnapshot sourceSnapshot;
        using (await SnapshotWriteLocks.AcquireAsync(sourcePath, cancellationToken))
        using (await CrossProcessWriteLease.AcquireAsync(
                   SessionTreeLeaseTarget(sourcePath),
                   SnapshotWriteLeaseTimeout,
                   cancellationToken))
        using (await CrossProcessWriteLease.AcquireAsync(sourcePath, SnapshotWriteLeaseTimeout, cancellationToken))
        {
            sourceSnapshot = await LoadSnapshotAsync(safeSourceSessionId, cancellationToken)
                ?? throw new InvalidDataException($"Session '{safeSourceSessionId}' has an unreadable snapshot and cannot be forked.");
        }

        StructuredMemoryService.NormalizeSnapshot(sourceSnapshot);
        var normalizedCursorId = cursorMessageId.Trim();
        var cursorIndex = sourceSnapshot.Engine.Messages.FindIndex(message =>
            DialogueMessageIdentity.Resolve(message).Equals(normalizedCursorId, StringComparison.OrdinalIgnoreCase));
        if (cursorIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorMessageId), $"Message cursor '{normalizedCursorId}' does not exist in session '{safeSourceSessionId}'.");
        }

        var sourceRevision = Math.Max(0, sourceSnapshot.PersistenceRevision);
        var forkedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cursor = sourceSnapshot.Engine.Messages[cursorIndex];
        var baseTargetSessionId = string.IsNullOrWhiteSpace(targetSessionId)
            ? SafeSessionId($"{safeSourceSessionId}-fork-{normalizedCursorId.Replace(':', '-')}")
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
            using var identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
            if (SessionIdentityExists(candidateSessionId, targetPath)
                || await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                    identityReservation.TrashRoot,
                    candidateSessionId,
                    cancellationToken))
            {
                continue;
            }

            try
            {
                using var processLock = await SnapshotWriteLocks.AcquireAsync(targetPath, cancellationToken);
                using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                    SessionTreeLeaseTarget(targetPath),
                    SnapshotWriteLeaseTimeout,
                    cancellationToken);
                using var writeLease = await CrossProcessWriteLease.AcquireAsync(targetPath, SnapshotWriteLeaseTimeout, cancellationToken);
                if (File.Exists(targetPath)
                    || SessionSideArtifactsExist(candidateSessionId)
                    || TargetDirectoryContainsUnexpectedEntries(targetDirectory, targetPath))
                {
                    continue;
                }

                var candidateSnapshot = CloneSnapshot(sourceSnapshot);
                var provisionalBranchId = BranchIdentity(
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    normalizedCursorId,
                    forkedAt);
                var projection = StructuredMemoryService.ProjectAtCursor(
                    candidateSnapshot,
                    cursorIndex,
                    DateTimeOffset.FromUnixTimeSeconds(forkedAt),
                    provisionalBranchId);
                ProjectDerivedStateAtCursor(candidateSnapshot, cursorIndex);
                NormalizeForkSnapshot(
                    candidateSnapshot,
                    safeSourceSessionId,
                    sourceRevision,
                    forkedAt,
                    sourceSnapshot.Engine.TurnCount,
                    sourceSnapshot.Engine.Messages.Count);

                var evidence = ImmutableArray.CreateBuilder<ArenaEvidenceAssertion>();
                var usesCurrentSetup = cursorIndex < sourceSnapshot.Engine.Messages.Count - 1;
                if (usesCurrentSetup)
                {
                    evidence.Add(new ArenaEvidenceAssertion(
                        "evidence:historical-setup-projection-unavailable",
                        ArenaEvidenceState.Unavailable,
                        "The transcript and provenance-bearing memory were projected to the selected cursor, but the current replayable setup was retained.",
                        Limitation: "Legacy session snapshots do not retain cursor-scoped provider, persona, steering, relationship, or active-cast history."));
                }
                if (projection.UnprojectableEntryCount > 0)
                {
                    evidence.Add(new ArenaEvidenceAssertion(
                        "evidence:legacy-memory-projection-unavailable",
                        ArenaEvidenceState.Unavailable,
                        "Unproven legacy memory was omitted from the historical fork.",
                        Limitation: "Legacy memory without a source cursor or timestamp cannot be projected safely."));
                }
                if (cursorIndex < sourceSnapshot.Engine.Messages.Count - 1
                    && sourceSnapshot.Engine.ResearchItems.Count > 0)
                {
                    evidence.Add(new ArenaEvidenceAssertion(
                        "evidence:research-projection-unavailable",
                        ArenaEvidenceState.Unavailable,
                        "Research state was omitted from the historical fork.",
                        Limitation: "Legacy research items do not contain a transcript cursor."));
                }
                if (cursorIndex < sourceSnapshot.Engine.Messages.Count - 1
                    && sourceSnapshot.Engine.Attachments.Count > 0)
                {
                    evidence.Add(new ArenaEvidenceAssertion(
                        "evidence:attachment-projection-unavailable",
                        ArenaEvidenceState.Unavailable,
                        "Attachments were omitted from the historical fork.",
                        Limitation: "Legacy attachments do not contain a transcript cursor."));
                }

                var receipt = AttachBranchReceipt(
                    candidateSnapshot,
                    sourceSnapshot,
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    forkedAt,
                    cursorIndex,
                    evidence.ToImmutable(),
                    provisionalBranchId);
                candidateSnapshot.PersistenceRevision = 0;
                if (!await TryCreateSnapshotFileAsync(candidateSnapshot, targetPath, cancellationToken))
                {
                    continue;
                }

                return new SessionForkResult(
                    safeSourceSessionId,
                    candidateSessionId,
                    sourceRevision,
                    candidateSnapshot.PersistenceRevision,
                    candidateSnapshot.Engine.TurnCount,
                    candidateSnapshot.Engine.Messages.Count,
                    candidateSnapshot.Engine.Narration.Count,
                    candidateSnapshot.Engine.Agents.Count(agent => agent.Active),
                    candidateSnapshot.GenerationHistory.Count,
                    forkedAt)
                {
                    BranchReceiptId = receipt.Id,
                    CursorMessageId = normalizedCursorId,
                    ExcludedMemoryEntryCount = projection.ExcludedEntryCount,
                    UnprojectableMemoryEntryCount = projection.UnprojectableEntryCount,
                    HistoricalSetupProjectionUnavailable = usesCurrentSetup
                };
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

    private static void ProjectDerivedStateAtCursor(ArenaSnapshot snapshot, int cursorIndex)
    {
        var isHistoricalCursor = cursorIndex < snapshot.Engine.Messages.Count - 1;
        var cursor = snapshot.Engine.Messages[cursorIndex];
        snapshot.Engine.Messages = snapshot.Engine.Messages.Take(cursorIndex + 1).ToList();
        snapshot.Engine.Narration.RemoveAll(entry => entry.ToTurn > cursor.Turn || entry.FromTurn > cursor.Turn);
        if (isHistoricalCursor)
        {
            snapshot.Engine.Attachments.Clear();
            snapshot.Engine.ResearchItems.Clear();
            snapshot.Engine.DecisionCard.Text = "";
            snapshot.Engine.DecisionCard.UpdatedAt = 0;
            snapshot.Engine.DecisionCard.InternetRequest = null;
            snapshot.Engine.DecisionCard.InternetResult = null;
            snapshot.Engine.DecisionCard.Metadata.Clear();
            snapshot.Engine.Summary = "";
            snapshot.GenerationHistory.RemoveAll(entry => cursor.CreatedAt <= 0 || entry.CreatedAt > cursor.CreatedAt);
            foreach (var key in snapshot.Configs.Keys.ToArray())
            {
                snapshot.Configs[key] = ProviderSetupWithoutRuntime(snapshot.Configs[key]);
            }
        }
        snapshot.Engine.TurnCount = Math.Max(0, snapshot.Engine.Messages.Select(message => message.Turn).DefaultIfEmpty(0).Max());
        var activeIds = snapshot.Engine.Agents.Where(agent => agent.Active).Select(agent => agent.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedAgentTurns = snapshot.Engine.Messages.Count(message => activeIds.Contains(message.SpeakerId));
        snapshot.Engine.TurnIndex = activeIds.Count == 0 ? 0 : completedAgentTurns % activeIds.Count;
    }

    private static ArenaBranchContract AttachBranchReceipt(
        ArenaSnapshot target,
        ArenaSnapshot source,
        string parentSessionId,
        string childSessionId,
        long parentRevision,
        long forkedAt,
        int cursorIndex,
        ImmutableArray<ArenaEvidenceAssertion> evidence,
        string? receiptId = null)
    {
        DialogueMessage cursor;
        ArenaTranscriptForkPoint forkPoint;
        if (cursorIndex >= 0 && cursorIndex < source.Engine.Messages.Count)
        {
            cursor = source.Engine.Messages[cursorIndex];
            forkPoint = new ArenaTranscriptForkPoint(
                DialogueMessageIdentity.Resolve(cursor),
                cursorIndex,
                Math.Max(0, cursor.Turn),
                DialogueMessageIdentity.Fingerprint(cursor));
        }
        else
        {
            var originHash = Sha256($"empty\n{parentSessionId}\n{parentRevision}");
            forkPoint = new ArenaTranscriptForkPoint("message:empty-origin", 0, 0, originHash);
        }

        var id = receiptId ?? BranchIdentity(parentSessionId, childSessionId, parentRevision, forkPoint.MessageId, forkedAt);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(forkedAt);
        var receipt = new ArenaBranchContract(
            ArenaContractSchemas.Branch,
            id,
            timestamp,
            null,
            parentSessionId,
            parentRevision,
            forkPoint,
            SetupFingerprint(source),
            parentRevision,
            childSessionId,
            timestamp,
            evidence);
        target.BranchReceipt = receipt;
        return receipt;
    }

    private static string BranchIdentity(
        string parentSessionId,
        string childSessionId,
        long parentRevision,
        string cursorMessageId,
        long forkedAt)
    {
        return StructuredMemoryService.StableId(
            "branch",
            $"{parentSessionId}\n{childSessionId}\n{parentRevision}\n{cursorMessageId}\n{forkedAt}");
    }

    internal static string SetupFingerprint(ArenaSnapshot snapshot)
    {
        var canonical = new List<string>
        {
            $"match|{snapshot.MatchType}",
            $"model-behavior|{(snapshot.Engine.FactoryMode ? "factory" : "arena")}",
            $"model-settings-schema|{snapshot.ModelSettingsVersion}",
            $"pending-model-configuration-applies|{string.Join(",", snapshot.PendingModelConfigurationApplies.OrderBy(value => value, StringComparer.Ordinal))}",
            $"default-for-unassigned-agents|{snapshot.Engine.DefaultForUnassignedAgentsEnabled}",
            $"steering.mode|{snapshot.Engine.Steering.Mode}",
            $"steering.topic|{snapshot.Engine.Steering.Topic}",
            $"steering.global|{snapshot.Engine.Steering.Global}",
            $"windows|{snapshot.Engine.TranscriptWindow}|{snapshot.Engine.PrivateWindow}|{snapshot.Engine.NotesWindow}",
            $"internet|{snapshot.Engine.Internet.UseInternet}|{snapshot.Engine.Internet.MaxResults}|{snapshot.Engine.Internet.SourceFreshnessMinutes}",
            $"narrator|{snapshot.Engine.Narrator.Mode}|{snapshot.Engine.Narrator.Persona}|{snapshot.Engine.Narrator.VoiceStyle}|{snapshot.Engine.Narrator.AccentColor}|{snapshot.Engine.Narrator.Cadence}|{snapshot.Engine.Narrator.InspectPrivateNotes}",
            $"rivalry|{snapshot.Engine.RivalryMatrix.Enabled}",
            $"scenario-generator|{GeneratorFingerprint(snapshot.ScenarioGenerator)}",
            $"persona-randomizer|{GeneratorFingerprint(snapshot.PersonaRandomizer)}"
        };
        if (snapshot.Engine.FactoryMode)
        {
            canonical.Add($"factory-conversation-contract|{FactoryConversationService.ContractVersion}");
        }
        canonical.AddRange(snapshot.MatchLocks
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"lock|{pair.Key}|{pair.Value}"));
        canonical.AddRange(snapshot.Engine.Agents
            .OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .Select(agent => $"agent|{agent.Id}|{agent.Name}|{agent.Persona}|{agent.Active}|{agent.VoiceStyle}|{agent.PressureProfile}|{agent.AccentColor}"));
        canonical.AddRange(snapshot.Engine.RivalryMatrix.Links
            .OrderBy(link => link.Source, StringComparer.Ordinal)
            .ThenBy(link => link.Target, StringComparer.Ordinal)
            .ThenBy(link => link.Stance, StringComparer.Ordinal)
            .Select(link => $"rivalry-link|{link.Source}|{link.Target}|{link.Stance}"));
        canonical.AddRange(snapshot.Configs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Join(
                "|",
                "provider",
                pair.Key,
                SafeProviderEndpoint(pair.Value.BaseUrl),
                pair.Value.ApiMode,
                pair.Value.Model,
                pair.Value.ExplicitModelAssignment,
                pair.Value.Timeout,
                pair.Value.Temperature.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                pair.Value.MaxOutputTokens,
                pair.Value.ContextLength,
                pair.Value.ConfiguredContextWindow,
                ModelHistoryPolicies.NormalizeHistoryPolicy(pair.Value.HistoryPolicy),
                ModelResponseTones.NormalizeResponseTone(pair.Value.ResponseTone),
                ModelResponseTones.NormalizeCustomTone(pair.Value.CustomTone),
                pair.Value.Reasoning,
                pair.Value.NativeStatefulChat,
                pair.Value.NativeIdleTtlSeconds)));
        canonical.AddRange(snapshot.ModelSettings
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Join(
                "|",
                "model-settings",
                pair.Key,
                pair.Value.ConfiguredContextWindow,
                ModelHistoryPolicies.NormalizeHistoryPolicy(pair.Value.HistoryPolicy),
                ModelResponseTones.NormalizeResponseTone(pair.Value.ResponseTone),
                ModelResponseTones.NormalizeCustomTone(pair.Value.CustomTone))));
        return Sha256(string.Join("\n", canonical));
    }

    private static string GeneratorFingerprint(GeneratorState state) =>
        $"{state.Style}|{state.Seed}|{state.Intensity}|{state.RolePack}|{state.Absurdity}|{state.ApplyOnReset}";

    private static ModelProviderConfig ProviderSetupWithoutRuntime(ModelProviderConfig config) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        Extra = config.Extra
    };

    private static ModelProviderConfig ExperimentProviderSetup(ModelProviderConfig config) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = "",
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        PreviousResponseId = "",
        RequestInspectionContext = null,
        LastError = "",
        LastLatencyMs = 0,
        LastTestOk = false,
        Extra = null
    };

    private static string SafeProviderEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "invalid-endpoint";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{path}";
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RebaseRetainedMemory(ArenaSnapshot snapshot, string sourceBranchId, string childBranchId)
    {
        foreach (var agent in snapshot.Engine.Agents)
        {
            var foreignTexts = agent.MemoryEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.BranchId)
                    && !entry.BranchId.Equals(sourceBranchId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Text)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            agent.MemoryEntries.RemoveAll(entry => !string.IsNullOrWhiteSpace(entry.BranchId)
                && !entry.BranchId.Equals(sourceBranchId, StringComparison.OrdinalIgnoreCase));
            var retainedTexts = agent.MemoryEntries.Select(entry => entry.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            agent.PrivateNotes.RemoveAll(note => foreignTexts.Contains(note) && !retainedTexts.Contains(note));
            foreach (var entry in agent.MemoryEntries)
            {
                entry.BranchId = childBranchId;
            }
        }
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
        var activeExperimentCallLeasePath = $"{ExperimentProviderLeaseTarget(snapshotPath)}.write.lock";
        try
        {
            return Directory.EnumerateFileSystemEntries(targetDirectory)
                .Any(path => !path.Equals(activeLeasePath, StringComparison.OrdinalIgnoreCase)
                    && !path.Equals(activeExperimentCallLeasePath, StringComparison.OrdinalIgnoreCase));
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
        long forkedAt,
        int? parentTurnCount = null,
        int? parentMessageCount = null)
    {
        snapshot.SessionInstanceId = NewSessionInstanceId();
        snapshot.PersistenceRevision = 0;
        snapshot.ForkLineage = new SessionForkLineage
        {
            ParentSessionId = parentSessionId,
            ParentPersistenceRevision = parentPersistenceRevision,
            ParentTurnCount = parentTurnCount ?? snapshot.Engine.TurnCount,
            ParentMessageCount = parentMessageCount ?? snapshot.Engine.Messages.Count,
            ForkedAt = forkedAt
        };
        snapshot.Engine.LastError = "";
        snapshot.Engine.MatchEnded = false;
        snapshot.Engine.MatchEndedAt = null;
        snapshot.Engine.MatchEndReason = "";
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
        if (!IsValidSessionInstanceId(snapshot.SessionInstanceId))
        {
            throw new InvalidDataException("The session instance identity is unavailable.");
        }

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
            var persistenceJsonOptions = SnapshotPersistenceJson.CreateOptions(JsonOptions, ProtectSecret);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, persistenceJsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(tempPath, fullPath);
                RecordSnapshotMutation(fullPath);
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
            MatchType = "balanced",
            ModelSettingsVersion = ModelRuntimeSettingsRegistry.CurrentSchemaVersion
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

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await TrashSessionAsync(sessionId, cancellationToken) is not null;
    }

    public async Task<SavedStateDeletionReceipt?> TrashSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeSession = SafeSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(safeSession) || safeSession.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sessionsRoot = Path.GetFullPath(NativeDataPaths.SessionsRoot(DataRoot));
        var sessionPath = Path.GetFullPath(Path.Combine(sessionsRoot, safeSession));
        if (!PathIsInsideDirectory(sessionsRoot, sessionPath)
            || !Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath))
        {
            return null;
        }

        string? entryPath = null;
        var moved = false;
        try
        {
            var trashRoot = EnsureSavedStateTrashRoot();
            var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
            using var maintenanceLock = await SavedStateTrashLocks.AcquireAsync(
                $"{trashRoot}|maintenance",
                cancellationToken);
            using var maintenanceLease = await CrossProcessWriteLease.AcquireAsync(
                Path.Combine(trashLeaseRoot, "maintenance"),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            // Preflight only reconciles invalid or expired entries. A valid
            // recovery candidate is never evicted to reserve space for work that
            // has not committed yet.
            await PurgeSavedStateTrashUnderMaintenanceAsync(
                int.MaxValue,
                cancellationToken);
            if (SavedStateTrashEntryCount(trashRoot) > savedStateTrashEntryLimit)
            {
                // A prior committed delete can temporarily exceed the cap when
                // its oldest entry is externally locked. Do not grow that
                // bounded overflow until maintenance can reconcile it.
                return null;
            }

            var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
            using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
            using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                SessionTreeLeaseTarget(snapshotPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (!Directory.Exists(sessionPath) || PathIsReparsePoint(sessionPath))
            {
                return null;
            }

            using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
                ExperimentProviderLeaseTarget(snapshotPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            using var writeLease = await CrossProcessWriteLease.AcquireAsync(
                snapshotPath,
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            // Provider-call, snapshot-write, and session-tree exclusion must all
            // remain live through the durable tombstone and the atomic move. If
            // either file lease is released here, a validated provider call or
            // snapshot writer can enter while the soon-to-be-trashed tree still
            // resolves at its original path.
            if (!Directory.Exists(sessionPath) || PathIsReparsePoint(sessionPath))
            {
                return null;
            }

            var deletedAt = await NextSavedStateDeletionTimeAsync(trashRoot, cancellationToken);
            var receipt = new SavedStateDeletionReceipt(
                Guid.NewGuid().ToString("N"),
                SavedStateDeletionKind.Session,
                safeSession,
                "",
                safeSession,
                deletedAt,
                deletedAt.Add(savedStateTrashRetention));
            entryPath = SavedStateTrashEntryPath(trashRoot, receipt.Id);
            var payloadPath = Path.Combine(entryPath, "payload");
            EnsureDirectoryWithoutReparsePoint(entryPath, "saved-state Trash entry");
            using var entryLock = await SavedStateTrashLocks.AcquireAsync(entryPath, cancellationToken);
            using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                SavedStateTrashEntryLeaseTarget(trashLeaseRoot, receipt.Id),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            var tombstone = CreateSavedStateTombstone(
                receipt,
                Path.GetRelativePath(Path.GetFullPath(DataRoot), sessionPath));
            await WriteSavedStateTombstoneAsync(entryPath, tombstone, cancellationToken);
            await savedStateTrashPreparedObserver(receipt, cancellationToken);

            // Cancellation is observed before the single same-volume rename.
            // Once the rename succeeds the operation is committed and returns a
            // receipt rather than reporting cancellation after data moved.
            cancellationToken.ThrowIfCancellationRequested();
            ClearReadOnly(sessionPath);
            experimentCallLease.PrepareForContainingDirectoryMove();
            writeLease.PrepareForContainingDirectoryMove();
            Directory.Move(sessionPath, payloadPath);
            moved = true;
            InvalidateSessionSummaryCaches(safeSession);
            RecordSnapshotMutation(snapshotPath);
            await ReconcileSavedStateTrashCapacityAfterCommitAsync();
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!moved && !string.IsNullOrWhiteSpace(entryPath))
            {
                TryDeleteTrashEntry(entryPath);
            }
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
        var safeSession = SafeSessionId(sessionId);
        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        var checkpointDir = Path.GetFullPath(CheckpointDirectory(safeSession));
        if (!Directory.Exists(checkpointDir) || PathIsReparsePoint(checkpointDir))
        {
            return Array.Empty<CheckpointSummary>();
        }

        var checkpoints = new List<CheckpointSummary>();
        foreach (var path in SafeEnumerateFiles(checkpointDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!PathIsInsideDirectory(checkpointDir, fullPath) || PathIsReparsePoint(fullPath))
                {
                    continue;
                }

                using var checkpointLock = await SavedStateTrashLocks.AcquireAsync(fullPath, cancellationToken);
                using var checkpointLease = await CrossProcessWriteLease.AcquireAsync(
                    fullPath,
                    SnapshotWriteLeaseTimeout,
                    cancellationToken);
                if (!File.Exists(fullPath)
                    || PathIsReparsePoint(fullPath)
                    || PathIsReparsePoint(checkpointDir))
                {
                    continue;
                }

                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var metadata = await ReadCheckpointMetadataAsync(stream, cancellationToken);
                var expectedId = Path.GetFileNameWithoutExtension(fullPath);
                if (metadata is not null
                    && metadata.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)
                    && metadata.SessionId.Equals(safeSession, StringComparison.OrdinalIgnoreCase))
                {
                    checkpoints.Add(new CheckpointSummary(metadata.Id, metadata.Name, safeSession, metadata.CreatedAt, fullPath));
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
        var safeSession = SafeSessionId(sessionId);
        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || !File.Exists(snapshotPath)
            || PathIsReparsePoint(snapshotPath))
        {
            throw new InvalidOperationException($"No snapshot found for session {safeSession}.");
        }

        using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
            ExperimentProviderLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(
            snapshotPath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || !File.Exists(snapshotPath)
            || PathIsReparsePoint(snapshotPath))
        {
            throw new InvalidOperationException($"No snapshot found for session {safeSession}.");
        }

        var snapshot = await LoadSnapshotAsync(safeSession, cancellationToken)
            ?? throw new InvalidOperationException($"No snapshot found for session {sessionId}.");
        await EnsureSnapshotInstanceIdUnderWriteLeaseAsync(snapshot, snapshotPath, cancellationToken);
        return await SaveCheckpointCoreAsync(safeSession, name, snapshot, cancellationToken);
    }

    /// <summary>
    /// Replaces one persisted snapshot only after a full checkpoint of the
    /// authoritative pre-mutation revision has committed. The mutation delegate
    /// receives a deep clone, never the object serialized into the checkpoint.
    /// All session mutation leases remain held across both commits.
    /// </summary>
    public async Task<SnapshotSafetyCheckpointReceipt?> MutateSnapshotWithSafetyCheckpointAsync(
        string sessionId,
        SnapshotSafetyCheckpointOperation operation,
        string? subject,
        Func<ArenaSnapshot, ArenaSnapshot> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var safeSession = SafeSessionId(sessionId);
        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || !File.Exists(snapshotPath)
            || PathIsReparsePoint(snapshotPath))
        {
            return null;
        }

        using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
            ExperimentProviderLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(
            snapshotPath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!File.Exists(snapshotPath) || PathIsReparsePoint(snapshotPath))
        {
            return null;
        }

        var authoritative = await LoadSnapshotAsync(safeSession, cancellationToken);
        if (authoritative is null)
        {
            return null;
        }

        await EnsureSnapshotInstanceIdUnderWriteLeaseAsync(authoritative, snapshotPath, cancellationToken);

        return await MutateSnapshotWithSafetyCheckpointCoreAsync(
            safeSession,
            snapshotPath,
            authoritative,
            operation,
            subject,
            mutation,
            cancellationToken);
    }

    private async Task<SnapshotSafetyCheckpointReceipt> MutateSnapshotWithSafetyCheckpointCoreAsync(
        string safeSession,
        string snapshotPath,
        ArenaSnapshot authoritative,
        SnapshotSafetyCheckpointOperation operation,
        string? subject,
        Func<ArenaSnapshot, ArenaSnapshot> mutation,
        CancellationToken cancellationToken)
    {
        var protectedRevision = Math.Max(0, authoritative.PersistenceRevision);
        var mutationInput = CloneSnapshot(authoritative);
        var normalizedSubject = NormalizeSavedStateDisplayName(subject, "");
        var safetyCheckpoint = await SaveCheckpointCoreAsync(
            safeSession,
            AutomaticSafetyCheckpointName(operation, normalizedSubject),
            authoritative,
            cancellationToken);

        // Cancellation before replacement is fail-closed. A checkpoint that
        // already committed is intentionally retained as harmless extra safety.
        cancellationToken.ThrowIfCancellationRequested();
        var replacement = mutation(mutationInput)
            ?? throw new InvalidOperationException("The safety-checkpoint mutation did not produce a replacement snapshot.");
        replacement.SessionInstanceId = authoritative.SessionInstanceId;
        cancellationToken.ThrowIfCancellationRequested();
        await SaveSnapshotCoreAsync(
            replacement,
            snapshotPath,
            rejectStaleRevision: false,
            cancellationToken);

        // SaveSnapshotCoreAsync has committed at this point. Do not observe the
        // caller token again or report a successful replacement as cancelled.
        return new SnapshotSafetyCheckpointReceipt(
            safetyCheckpoint,
            operation,
            normalizedSubject,
            protectedRevision,
            replacement.PersistenceRevision);
    }

    private static async Task EnsureSnapshotInstanceIdUnderWriteLeaseAsync(
        ArenaSnapshot snapshot,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        if (IsValidSessionInstanceId(snapshot.SessionInstanceId))
        {
            return;
        }

        snapshot.SessionInstanceId = NewSessionInstanceId();
        await SaveSnapshotCoreAsync(
            snapshot,
            snapshotPath,
            rejectStaleRevision: false,
            cancellationToken);
    }

    internal static string AutomaticSafetyCheckpointName(
        SnapshotSafetyCheckpointOperation operation,
        string? subject)
    {
        var prefix = operation switch
        {
            SnapshotSafetyCheckpointOperation.ArenaReset => "Safety before arena reset",
            SnapshotSafetyCheckpointOperation.CheckpointRestore => "Safety before checkpoint restore",
            SnapshotSafetyCheckpointOperation.TemplateApply => "Safety before template apply",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown safety-checkpoint operation.")
        };
        var normalizedSubject = NormalizeSavedStateDisplayName(subject, "");
        var name = string.IsNullOrWhiteSpace(normalizedSubject)
            ? prefix
            : $"{prefix}: {normalizedSubject}";
        return name[..Math.Min(name.Length, MaxCheckpointNameLength)];
    }

    private async Task<CheckpointSummary> SaveCheckpointCoreAsync(
        string sessionId,
        string name,
        ArenaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        var checkpointName = string.IsNullOrWhiteSpace(name)
            ? $"Arena checkpoint {now:yyyy-MM-dd HH:mm:ss}"
            : name.Trim()[..Math.Min(name.Trim().Length, MaxCheckpointNameLength)];
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
        var fullPath = Path.GetFullPath(path);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 4 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var persistenceJsonOptions = SnapshotPersistenceJson.CreateOptions(JsonOptions, ProtectSecret);
                await JsonSerializer.SerializeAsync(stream, record, persistenceJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceSnapshotFileAsync(tempPath, fullPath, cancellationToken);
            await checkpointDurableCommitObserver(fullPath, cancellationToken);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }

        return new CheckpointSummary(id, checkpointName, sessionId, record.CreatedAt, fullPath);
    }

    public async Task<CheckpointSummary?> RestoreCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var result = await RestoreCheckpointWithSafetyCheckpointAsync(
            sessionId,
            checkpointId,
            cancellationToken);
        return result?.RestoredCheckpoint;
    }

    public async Task<CheckpointRestoreWithSafetyResult?> RestoreCheckpointWithSafetyCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        var safeSession = SafeSessionId(sessionId);
        var safeCheckpoint = SafeCheckpointId(checkpointId);
        if (string.IsNullOrWhiteSpace(safeCheckpoint))
        {
            return null;
        }

        var path = SafeCheckpointPath(safeSession, safeCheckpoint);
        if (path is null)
        {
            return null;
        }

        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        using var identityReservation = await AcquireSavedStateTrashMaintenanceScopeAsync(cancellationToken);
        var allowMissingSessionDirectory = !Directory.Exists(sessionPath);
        if ((allowMissingSessionDirectory && File.Exists(sessionPath))
            || (!allowMissingSessionDirectory && PathIsReparsePoint(sessionPath))
            || (allowMissingSessionDirectory
                && await SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
                    identityReservation.TrashRoot,
                    safeSession,
                    cancellationToken)))
        {
            return null;
        }

        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        // Legacy checkpoints may predate a live session directory. The Trash
        // maintenance reservation distinguishes that compatibility case from a
        // session tree that was atomically moved away and remains recoverable.
        if (!SessionDirectoryIsSafeForCheckpointRestore(sessionPath, allowMissingSessionDirectory))
        {
            return null;
        }

        using var experimentCallLease = await CrossProcessWriteLease.AcquireAsync(
            ExperimentProviderLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        using var writeLease = await CrossProcessWriteLease.AcquireAsync(
            snapshotPath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!SessionDirectoryIsSafeForCheckpointRestore(sessionPath, allowMissingSessionDirectory))
        {
            return null;
        }

        using var checkpointLock = await SavedStateTrashLocks.AcquireAsync(path, cancellationToken);
        if (!File.Exists(path)
            || PathIsReparsePoint(path)
            || PathIsReparsePoint(Path.GetDirectoryName(path)!))
        {
            return null;
        }

        using var checkpointLease = await CrossProcessWriteLease.AcquireAsync(
            path,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        if (!File.Exists(path)
            || PathIsReparsePoint(path)
            || PathIsReparsePoint(Path.GetDirectoryName(path)!))
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

        if (record?.Snapshot is null
            || !string.Equals(record.Id, safeCheckpoint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.SessionId, safeSession, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(record.CreatedAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        // Checkpoint payloads use the same at-rest protection as snapshots. The
        // complete projection stays inside the session-tree coherence boundary;
        // a corrupt payload or failed secret transform still exits before any
        // checkpoint or live-snapshot replacement. The later persistence write
        // protects plaintext once; carrying ciphertext forward would
        // double-protect it.
        ScrubRemovedLegacyInternetData(record.Snapshot);
        StructuredMemoryService.NormalizeSnapshot(record.Snapshot);
        ModelRuntimeSettingsRegistry.Normalize(record.Snapshot);
        TransformConfigTokens(record.Snapshot, UnprotectSecret);

        var restoredName = NormalizeSavedStateDisplayName(record.Name, safeCheckpoint);
        var restoredCheckpoint = new CheckpointSummary(
            safeCheckpoint,
            restoredName,
            safeSession,
            record.CreatedAt,
            path);
        var authoritative = await LoadSnapshotAsync(safeSession, cancellationToken);
        if (authoritative is null)
        {
            if (File.Exists(snapshotPath))
            {
                // A present but unreadable live snapshot is still state. Never
                // overwrite it under the snapshot-less compatibility path,
                // because no exact safety checkpoint can be created from it.
                return null;
            }

            var replacement = CloneSnapshot(record.Snapshot);
            replacement.SessionInstanceId = NewSessionInstanceId();
            await SaveSnapshotCoreAsync(
                replacement,
                snapshotPath,
                rejectStaleRevision: false,
                cancellationToken);
            // Nothing existed to supersede, so there is no destructive state to
            // checkpoint. Preserve legacy recovery of snapshot-less sessions.
            return new CheckpointRestoreWithSafetyResult(restoredCheckpoint, SafetyCheckpoint: null);
        }

        await EnsureSnapshotInstanceIdUnderWriteLeaseAsync(authoritative, snapshotPath, cancellationToken);

        var safetyCheckpoint = await MutateSnapshotWithSafetyCheckpointCoreAsync(
            safeSession,
            snapshotPath,
            authoritative,
            SnapshotSafetyCheckpointOperation.CheckpointRestore,
            restoredName,
            _ =>
            {
                var replacement = CloneSnapshot(record.Snapshot);
                replacement.SessionInstanceId = authoritative.SessionInstanceId;
                return replacement;
            },
            cancellationToken);
        return new CheckpointRestoreWithSafetyResult(restoredCheckpoint, safetyCheckpoint);
    }

    public async Task<bool> DeleteCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        return await TrashCheckpointAsync(sessionId, checkpointId, null, cancellationToken) is not null;
    }

    public async Task<SavedStateDeletionReceipt?> TrashCheckpointAsync(
        string sessionId,
        string checkpointId,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeSession = SafeSessionId(sessionId);
        var safeCheckpoint = SafeCheckpointId(checkpointId);
        var path = SafeCheckpointPath(safeSession, safeCheckpoint);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        string? entryPath = null;
        var moved = false;
        try
        {
            var trashRoot = EnsureSavedStateTrashRoot();
            var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
            using var maintenanceLock = await SavedStateTrashLocks.AcquireAsync(
                $"{trashRoot}|maintenance",
                cancellationToken);
            using var maintenanceLease = await CrossProcessWriteLease.AcquireAsync(
                Path.Combine(trashLeaseRoot, "maintenance"),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            // Capacity eviction is post-commit; preflight may only remove entries
            // that are already invalid or expired independently of this delete.
            await PurgeSavedStateTrashUnderMaintenanceAsync(
                int.MaxValue,
                cancellationToken);
            if (SavedStateTrashEntryCount(trashRoot) > savedStateTrashEntryLimit)
            {
                return null;
            }

            using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
            using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
                SessionTreeLeaseTarget(snapshotPath),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (!Directory.Exists(sessionPath) || PathIsReparsePoint(sessionPath))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            using var mutationLock = await SavedStateTrashLocks.AcquireAsync(fullPath, cancellationToken);
            if (!File.Exists(fullPath)
                || PathIsReparsePoint(fullPath)
                || PathIsReparsePoint(Path.GetDirectoryName(fullPath)!))
            {
                return null;
            }

            using var writeLease = await CrossProcessWriteLease.AcquireAsync(
                fullPath,
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (!File.Exists(fullPath)
                || PathIsReparsePoint(fullPath)
                || PathIsReparsePoint(Path.GetDirectoryName(fullPath)!))
            {
                return null;
            }

            CheckpointMetadata? metadata;
            try
            {
                await using var metadataStream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                metadata = await ReadCheckpointMetadataAsync(metadataStream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (metadata is null
                || !metadata.Id.Equals(safeCheckpoint, StringComparison.OrdinalIgnoreCase)
                || !metadata.SessionId.Equals(safeSession, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? metadata.Name
                : displayName;
            resolvedDisplayName = NormalizeSavedStateDisplayName(resolvedDisplayName, safeCheckpoint);
            var deletedAt = await NextSavedStateDeletionTimeAsync(trashRoot, cancellationToken);
            var receipt = new SavedStateDeletionReceipt(
                Guid.NewGuid().ToString("N"),
                SavedStateDeletionKind.Checkpoint,
                safeSession,
                safeCheckpoint,
                resolvedDisplayName,
                deletedAt,
                deletedAt.Add(savedStateTrashRetention));
            entryPath = SavedStateTrashEntryPath(trashRoot, receipt.Id);
            var payloadPath = Path.Combine(entryPath, "payload.json");
            EnsureDirectoryWithoutReparsePoint(entryPath, "saved-state Trash entry");
            using var entryLock = await SavedStateTrashLocks.AcquireAsync(entryPath, cancellationToken);
            using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                SavedStateTrashEntryLeaseTarget(trashLeaseRoot, receipt.Id),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            var tombstone = CreateSavedStateTombstone(
                receipt,
                Path.GetRelativePath(Path.GetFullPath(DataRoot), fullPath));
            await WriteSavedStateTombstoneAsync(entryPath, tombstone, cancellationToken);
            await savedStateTrashPreparedObserver(receipt, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            ClearReadOnly(fullPath);
            File.Move(fullPath, payloadPath);
            moved = true;
            await ReconcileSavedStateTrashCapacityAfterCommitAsync();
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!moved && !string.IsNullOrWhiteSpace(entryPath))
            {
                TryDeleteTrashEntry(entryPath);
            }
        }
    }

    public async Task<SavedStateRestoreResult> RestoreDeletedStateAsync(
        SavedStateDeletionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(receipt.Id, "N", out _))
        {
            return new SavedStateRestoreResult(SavedStateRestoreStatus.Invalid, receipt);
        }

        try
        {
            var trashRoot = EnsureSavedStateTrashRoot();
            var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
            var entryPath = SavedStateTrashEntryPath(trashRoot, receipt.Id);
            if (!Directory.Exists(entryPath) || DirectoryIsReparsePoint(entryPath))
            {
                return new SavedStateRestoreResult(SavedStateRestoreStatus.NotFound, receipt);
            }

            using var entryLock = await SavedStateTrashLocks.AcquireAsync(entryPath, cancellationToken);
            using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                SavedStateTrashEntryLeaseTarget(trashLeaseRoot, receipt.Id),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            if (!Directory.Exists(entryPath))
            {
                return new SavedStateRestoreResult(SavedStateRestoreStatus.NotFound, receipt);
            }

            var resolved = await ReadSavedStateTrashEntryAsync(entryPath, cancellationToken);
            if (resolved is null || !ReceiptsIdentifySameDeletion(receipt, resolved.Receipt))
            {
                return new SavedStateRestoreResult(SavedStateRestoreStatus.Invalid, receipt);
            }

            if (resolved.Receipt.ExpiresAt <= timeProvider.GetUtcNow())
            {
                TryDeleteTrashEntry(entryPath);
                return new SavedStateRestoreResult(SavedStateRestoreStatus.Expired, resolved.Receipt);
            }

            if (!SavedStatePayloadExists(resolved))
            {
                TryDeleteTrashEntry(entryPath);
                return new SavedStateRestoreResult(SavedStateRestoreStatus.NotFound, resolved.Receipt);
            }

            var status = resolved.Receipt.Kind == SavedStateDeletionKind.Session
                ? await RestoreSessionTrashEntryAsync(resolved, cancellationToken)
                : await RestoreCheckpointTrashEntryAsync(resolved, cancellationToken);
            if (status == SavedStateRestoreStatus.Restored)
            {
                // The data move is already committed. Tombstone cleanup is
                // deliberately best effort so a cleanup error can never turn a
                // successful restore into an apparent failure.
                TryDeleteTrashEntry(entryPath);
            }

            return new SavedStateRestoreResult(status, resolved.Receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or JsonException)
        {
            return new SavedStateRestoreResult(SavedStateRestoreStatus.Failed, receipt);
        }
    }

    /// <summary>
    /// Reconstructs the durable Undo candidates from validated Trash entries.
    /// The returned receipts are authoritative for this read, newest first; no
    /// caller-held receipt or process-local UI state is trusted as the source of
    /// truth.
    /// </summary>
    public async Task<IReadOnlyList<SavedStateDeletionReceipt>> ListRestorableDeletedStatesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var trashRoot = EnsureSavedStateTrashRoot();
            var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
            using var maintenanceLock = await SavedStateTrashLocks.AcquireAsync(
                $"{trashRoot}|maintenance",
                cancellationToken);
            using var maintenanceLease = await CrossProcessWriteLease.AcquireAsync(
                Path.Combine(trashLeaseRoot, "maintenance"),
                SnapshotWriteLeaseTimeout,
                cancellationToken);

            var now = timeProvider.GetUtcNow();
            var receipts = new List<SavedStateDeletionReceipt>();
            foreach (var entryPath in EnumerateSavedStateTrashEntries(trashRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileName(
                    entryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!Guid.TryParseExact(id, "N", out _) || DirectoryIsReparsePoint(entryPath))
                {
                    continue;
                }

                try
                {
                    using var entryLock = await SavedStateTrashLocks.AcquireAsync(entryPath, cancellationToken);
                    using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                        SavedStateTrashEntryLeaseTarget(trashLeaseRoot, id),
                        SnapshotWriteLeaseTimeout,
                        cancellationToken);
                    if (!Directory.Exists(entryPath) || DirectoryIsReparsePoint(entryPath))
                    {
                        continue;
                    }

                    var resolved = await ReadSavedStateTrashEntryAsync(entryPath, cancellationToken);
                    if (resolved is not null
                        && resolved.Receipt.ExpiresAt > now
                        && SavedStatePayloadExists(resolved))
                    {
                        receipts.Add(resolved.Receipt);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or DirectoryNotFoundException
                                               or JsonException)
                {
                    // A single locked or invalid entry is not an Undo candidate.
                    // Continue under the bounded maintenance snapshot rather than
                    // allowing it to hide other independently valid entries.
                }
            }

            return receipts
                .OrderByDescending(receipt => receipt.DeletedAt)
                .ThenByDescending(receipt => receipt.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DirectoryNotFoundException
                                       or JsonException)
        {
            return [];
        }
    }

    public async Task<int> PurgeExpiredSavedStateTrashAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var trashRoot = EnsureSavedStateTrashRoot();
            var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
            using var maintenanceLock = await SavedStateTrashLocks.AcquireAsync(
                $"{trashRoot}|maintenance",
                cancellationToken);
            using var maintenanceLease = await CrossProcessWriteLease.AcquireAsync(
                Path.Combine(trashLeaseRoot, "maintenance"),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            return await PurgeSavedStateTrashUnderMaintenanceAsync(
                savedStateTrashEntryLimit,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
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

    private static bool SessionDirectoryIsSafeForCheckpointRestore(
        string sessionPath,
        bool allowMissingSessionDirectory)
    {
        if (Directory.Exists(sessionPath))
        {
            return !PathIsReparsePoint(sessionPath);
        }

        return allowMissingSessionDirectory && !File.Exists(sessionPath);
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

    private string EnsureSavedStateTrashRoot()
    {
        var dataRoot = Path.GetFullPath(DataRoot);
        var trashContainer = Path.GetFullPath(Path.Combine(dataRoot, ".trash"));
        var trashRoot = Path.GetFullPath(Path.Combine(trashContainer, "saved-state"));
        if (!PathIsInsideDirectory(dataRoot, trashRoot))
        {
            throw new IOException("The saved-state Trash path escaped the AI Arena data root.");
        }

        EnsureDirectoryWithoutReparsePoint(dataRoot, "AI Arena data root");
        EnsureDirectoryWithoutReparsePoint(trashContainer, "Trash");
        EnsureDirectoryWithoutReparsePoint(trashRoot, "saved-state Trash");
        return trashRoot;
    }

    private string EnsureSavedStateTrashLeaseRoot()
    {
        var dataRoot = Path.GetFullPath(DataRoot);
        var lockContainer = Path.GetFullPath(Path.Combine(dataRoot, ".locks"));
        var leaseRoot = Path.GetFullPath(Path.Combine(lockContainer, "saved-state-trash"));
        if (!PathIsInsideDirectory(dataRoot, leaseRoot))
        {
            throw new IOException("The saved-state Trash lock path escaped the AI Arena data root.");
        }

        EnsureDirectoryWithoutReparsePoint(dataRoot, "AI Arena data root");
        EnsureDirectoryWithoutReparsePoint(lockContainer, "lock");
        EnsureDirectoryWithoutReparsePoint(leaseRoot, "saved-state Trash lock");
        return leaseRoot;
    }

    private async Task<SavedStateTrashMaintenanceScope> AcquireSavedStateTrashMaintenanceScopeAsync(
        CancellationToken cancellationToken)
    {
        var trashRoot = EnsureSavedStateTrashRoot();
        var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
        var processLease = await SavedStateTrashLocks.AcquireAsync(
            $"{trashRoot}|maintenance",
            cancellationToken);
        try
        {
            var crossProcessLease = await CrossProcessWriteLease.AcquireAsync(
                Path.Combine(trashLeaseRoot, "maintenance"),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            return new SavedStateTrashMaintenanceScope(
                trashRoot,
                processLease,
                crossProcessLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

    private async Task<bool> SessionIdentityIsReservedInTrashUnderMaintenanceAsync(
        string trashRoot,
        string safeSessionId,
        CancellationToken cancellationToken)
    {
        var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
        var now = timeProvider.GetUtcNow();
        foreach (var entryPath in EnumerateSavedStateTrashEntries(trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryId = Path.GetFileName(
                entryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!Guid.TryParseExact(entryId, "N", out _))
            {
                continue;
            }

            using var entryLock = await SavedStateTrashLocks.AcquireAsync(entryPath, cancellationToken);
            using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                SavedStateTrashEntryLeaseTarget(trashLeaseRoot, entryId),
                SnapshotWriteLeaseTimeout,
                cancellationToken);
            var resolved = await ReadSavedStateTrashEntryAsync(entryPath, cancellationToken);
            if (resolved is not null
                && resolved.Receipt.Kind == SavedStateDeletionKind.Session
                && resolved.Receipt.SessionId.Equals(safeSessionId, StringComparison.OrdinalIgnoreCase)
                && resolved.Receipt.ExpiresAt > now
                && SavedStatePayloadExists(resolved))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureDirectoryWithoutReparsePoint(string path, string description)
    {
        if (Directory.Exists(path) && DirectoryIsReparsePoint(path))
        {
            throw new IOException($"The {description} directory cannot be a reparse point.");
        }

        Directory.CreateDirectory(path);
        if (DirectoryIsReparsePoint(path))
        {
            throw new IOException($"The {description} directory cannot be a reparse point.");
        }
    }

    private static string SavedStateTrashEntryPath(string trashRoot, string deletionId)
    {
        if (!Guid.TryParseExact(deletionId, "N", out _))
        {
            throw new IOException("The saved-state deletion receipt is invalid.");
        }

        var entryPath = Path.GetFullPath(Path.Combine(trashRoot, deletionId));
        if (!PathIsInsideDirectory(trashRoot, entryPath))
        {
            throw new IOException("The saved-state Trash entry escaped its root.");
        }

        return entryPath;
    }

    private static string SavedStateTrashEntryLeaseTarget(string trashLeaseRoot, string entryId)
    {
        var normalizedEntryId = entryId.ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEntryId))).ToLowerInvariant();
        return Path.Combine(trashLeaseRoot, $"entry-{key}");
    }

    private async Task<DateTimeOffset> NextSavedStateDeletionTimeAsync(
        string trashRoot,
        CancellationToken cancellationToken)
    {
        // Tombstones persist timestamps to millisecond precision. The global
        // maintenance lease serializes deletions, so advancing past the newest
        // retained tombstone gives restart-stable ordering even when the clock is
        // frozen or moves backward.
        var now = timeProvider.GetUtcNow();
        var next = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        foreach (var entryPath in EnumerateSavedStateTrashEntries(trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await ReadSavedStateTrashEntryAsync(entryPath, cancellationToken);
            if (resolved is null || resolved.Receipt.DeletedAt < next)
            {
                continue;
            }

            try
            {
                next = resolved.Receipt.DeletedAt.AddMilliseconds(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                // An implausible externally-authored timestamp must not make a
                // live item deletable without a trustworthy ordering receipt.
                throw new IOException("The saved-state Trash ordering metadata is invalid.");
            }
        }

        return next;
    }

    private static SavedStateTrashTombstone CreateSavedStateTombstone(
        SavedStateDeletionReceipt receipt,
        string originalRelativePath)
    {
        return new SavedStateTrashTombstone
        {
            SchemaVersion = SavedStateTrashSchemaVersion,
            Id = receipt.Id,
            Kind = receipt.Kind == SavedStateDeletionKind.Session ? "session" : "checkpoint",
            SessionId = receipt.SessionId,
            CheckpointId = receipt.CheckpointId,
            DisplayName = receipt.DisplayName,
            OriginalRelativePath = originalRelativePath,
            DeletedAtUnixMilliseconds = receipt.DeletedAt.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMilliseconds = receipt.ExpiresAt.ToUnixTimeMilliseconds()
        };
    }

    private static async Task WriteSavedStateTombstoneAsync(
        string entryPath,
        SavedStateTrashTombstone tombstone,
        CancellationToken cancellationToken)
    {
        var tombstonePath = Path.Combine(entryPath, "tombstone.json");
        var tempPath = Path.Combine(entryPath, $"tombstone.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 4 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, tombstone, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, tombstonePath);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTrashEntry(string entryPath)
    {
        try
        {
            DeleteDirectoryTree(entryPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An interrupted prepared tombstone is reconciled by the next Trash
            // maintenance pass. Never mask the source operation's outcome.
        }
    }

    private async Task<int> PurgeSavedStateTrashUnderMaintenanceAsync(
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);
        cancellationToken.ThrowIfCancellationRequested();
        var trashRoot = EnsureSavedStateTrashRoot();
        var trashLeaseRoot = EnsureSavedStateTrashLeaseRoot();
        var now = timeProvider.GetUtcNow();
        var inspections = new List<SavedStateTrashInspection>();
        foreach (var entryPath in EnumerateSavedStateTrashEntries(trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspections.Add(await InspectSavedStateTrashEntryAsync(entryPath, now, cancellationToken));
        }

        var purgeIds = inspections
            .Where(inspection => inspection.ShouldPurge)
            .Select(inspection => inspection.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = inspections
            .Where(inspection => !purgeIds.Contains(inspection.Id))
            .OrderBy(inspection => inspection.SortTime)
            .ThenBy(inspection => inspection.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excess = Math.Max(0, retained.Length - maximumEntries);
        foreach (var inspection in retained.Take(excess))
        {
            purgeIds.Add(inspection.Id);
        }

        var purged = 0;
        foreach (var inspection in inspections
                     .Where(candidate => purgeIds.Contains(candidate.Id))
                     .OrderBy(candidate => candidate.SortTime))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var entryLock = await SavedStateTrashLocks.AcquireAsync(
                    inspection.EntryPath,
                    cancellationToken);
                using var entryLease = await CrossProcessWriteLease.AcquireAsync(
                    SavedStateTrashEntryLeaseTarget(trashLeaseRoot, inspection.Id),
                    SnapshotWriteLeaseTimeout,
                    cancellationToken);
                if (!Directory.Exists(inspection.EntryPath))
                {
                    continue;
                }

                // Re-inspect after the per-entry lease. A restore may have won
                // the race after the maintenance scan; in that case its entry is
                // already gone and must not count as a purge.
                var current = await InspectSavedStateTrashEntryAsync(
                    inspection.EntryPath,
                    now,
                    cancellationToken);
                var forcedByCapacity = retained.Take(excess)
                    .Any(candidate => candidate.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                if (!current.ShouldPurge && !forcedByCapacity)
                {
                    continue;
                }

                DeleteDirectoryTree(current.EntryPath, cancellationToken);
                if (!Directory.Exists(current.EntryPath))
                {
                    purged++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or JsonException)
            {
                // One locked/corrupt Trash entry must not make deletion of a
                // different item irreversible or unavailable. It remains for a
                // future bounded maintenance pass.
            }
        }

        return purged;
    }

    private async Task ReconcileSavedStateTrashCapacityAfterCommitAsync()
    {
        try
        {
            // The caller still owns the global maintenance scope. The source
            // move is already committed, so reconciliation is non-cancelable and
            // best effort: an externally locked oldest entry may temporarily
            // leave one bounded overflow, but can never turn a successful delete
            // into an apparent failure or destroy the new recovery receipt.
            await PurgeSavedStateTrashUnderMaintenanceAsync(
                savedStateTrashEntryLimit,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or DirectoryNotFoundException
                                   or JsonException)
        {
        }
    }

    private async Task<SavedStateTrashInspection> InspectSavedStateTrashEntryAsync(
        string entryPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var id = Path.GetFileName(entryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (DirectoryIsReparsePoint(entryPath) || !Guid.TryParseExact(id, "N", out _))
        {
            return new SavedStateTrashInspection(id, entryPath, DateTimeOffset.MinValue, true);
        }

        var resolved = await ReadSavedStateTrashEntryAsync(entryPath, cancellationToken);
        if (resolved is not null)
        {
            return new SavedStateTrashInspection(
                id,
                entryPath,
                resolved.Receipt.DeletedAt,
                resolved.Receipt.ExpiresAt <= now || !SavedStatePayloadExists(resolved));
        }

        var lastWrite = SavedStateTrashEntryLastWrite(entryPath);
        return new SavedStateTrashInspection(
            id,
            entryPath,
            lastWrite,
            lastWrite <= now.Subtract(savedStateTrashRetention));
    }

    private async Task<ResolvedSavedStateTrashEntry?> ReadSavedStateTrashEntryAsync(
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(entryPath) || PathIsReparsePoint(entryPath))
        {
            return null;
        }

        var tombstonePath = Path.Combine(entryPath, "tombstone.json");
        if (!File.Exists(tombstonePath) || PathIsReparsePoint(tombstonePath))
        {
            return null;
        }

        SavedStateTrashTombstone? tombstone;
        try
        {
            await using var stream = new FileStream(
                tombstonePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            tombstone = await JsonSerializer.DeserializeAsync<SavedStateTrashTombstone>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (tombstone is null
            || tombstone.SchemaVersion != SavedStateTrashSchemaVersion
            || !Guid.TryParseExact(tombstone.Id, "N", out _)
            || !tombstone.Id.Equals(
                Path.GetFileName(entryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        SavedStateDeletionKind kind;
        if (tombstone.Kind.Equals("session", StringComparison.Ordinal))
        {
            kind = SavedStateDeletionKind.Session;
        }
        else if (tombstone.Kind.Equals("checkpoint", StringComparison.Ordinal))
        {
            kind = SavedStateDeletionKind.Checkpoint;
        }
        else
        {
            return null;
        }

        var safeSession = SafeSessionId(tombstone.SessionId);
        if (string.IsNullOrWhiteSpace(tombstone.SessionId)
            || !safeSession.Equals(tombstone.SessionId, StringComparison.Ordinal))
        {
            return null;
        }

        string expectedOriginalPath;
        string payloadPath;
        var safeCheckpoint = "";
        if (kind == SavedStateDeletionKind.Session)
        {
            if (safeSession.Equals("default", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(tombstone.CheckpointId))
            {
                return null;
            }

            expectedOriginalPath = Path.GetFullPath(Path.Combine(NativeDataPaths.SessionsRoot(DataRoot), safeSession));
            payloadPath = Path.GetFullPath(Path.Combine(entryPath, "payload"));
        }
        else
        {
            safeCheckpoint = SafeCheckpointId(tombstone.CheckpointId);
            if (string.IsNullOrWhiteSpace(safeCheckpoint)
                || !safeCheckpoint.Equals(tombstone.CheckpointId, StringComparison.Ordinal))
            {
                return null;
            }

            expectedOriginalPath = Path.GetFullPath(
                Path.Combine(NativeDataPaths.CheckpointDirectory(DataRoot, safeSession), $"{safeCheckpoint}.json"));
            payloadPath = Path.GetFullPath(Path.Combine(entryPath, "payload.json"));
        }

        var dataRoot = Path.GetFullPath(DataRoot);
        string recordedOriginalPath;
        try
        {
            recordedOriginalPath = Path.GetFullPath(Path.Combine(dataRoot, tombstone.OriginalRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!PathIsInsideDirectory(dataRoot, expectedOriginalPath)
            || !PathIsInsideDirectory(dataRoot, recordedOriginalPath)
            || !PathsEqual(expectedOriginalPath, recordedOriginalPath)
            || !PathIsInsideDirectory(entryPath, payloadPath))
        {
            return null;
        }

        DateTimeOffset deletedAt;
        DateTimeOffset expiresAt;
        try
        {
            deletedAt = DateTimeOffset.FromUnixTimeMilliseconds(tombstone.DeletedAtUnixMilliseconds);
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(tombstone.ExpiresAtUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (expiresAt <= deletedAt)
        {
            return null;
        }

        var fallbackName = kind == SavedStateDeletionKind.Session ? safeSession : safeCheckpoint;
        var receipt = new SavedStateDeletionReceipt(
            tombstone.Id,
            kind,
            safeSession,
            safeCheckpoint,
            NormalizeSavedStateDisplayName(tombstone.DisplayName, fallbackName),
            deletedAt,
            expiresAt);
        return new ResolvedSavedStateTrashEntry(receipt, entryPath, payloadPath, expectedOriginalPath);
    }

    private async Task<SavedStateRestoreStatus> RestoreSessionTrashEntryAsync(
        ResolvedSavedStateTrashEntry entry,
        CancellationToken cancellationToken)
    {
        var snapshotPath = Path.GetFullPath(
            Path.Combine(entry.OriginalPath, "snapshot.json"));
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(entry.OriginalPath) || File.Exists(entry.OriginalPath))
        {
            return SavedStateRestoreStatus.NameCollision;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // The session-tree lease lives outside the directory being restored,
            // so it coordinates writers without recreating the destination. The
            // atomic move remains the final collision arbiter.
            Directory.Move(entry.PayloadPath, entry.OriginalPath);
            InvalidateSessionSummaryCaches(entry.Receipt.SessionId);
            RecordSnapshotMutation(snapshotPath);
            return SavedStateRestoreStatus.Restored;
        }
        catch (IOException) when (Directory.Exists(entry.OriginalPath) || File.Exists(entry.OriginalPath))
        {
            return SavedStateRestoreStatus.NameCollision;
        }
    }

    private async Task<SavedStateRestoreStatus> RestoreCheckpointTrashEntryAsync(
        ResolvedSavedStateTrashEntry entry,
        CancellationToken cancellationToken)
    {
        var safeSession = SafeSessionId(entry.Receipt.SessionId);
        var snapshotPath = Path.GetFullPath(SnapshotPath(safeSession));
        var sessionPath = Path.GetDirectoryName(snapshotPath)!;
        var checkpointDirectory = Path.GetDirectoryName(entry.OriginalPath)!;
        using var processLock = await SnapshotWriteLocks.AcquireAsync(snapshotPath, cancellationToken);
        using var sessionTreeLease = await CrossProcessWriteLease.AcquireAsync(
            SessionTreeLeaseTarget(snapshotPath),
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        // A deleted checkpoint remains recoverable while its parent session is in
        // Trash, but restoring it must not recreate an orphan checkpoint tree.
        // The operator can first Undo the session and then retry this receipt.
        if (!Directory.Exists(sessionPath) || PathIsReparsePoint(sessionPath))
        {
            return SavedStateRestoreStatus.NotFound;
        }

        if (Directory.Exists(checkpointDirectory) && PathIsReparsePoint(checkpointDirectory))
        {
            return SavedStateRestoreStatus.Invalid;
        }

        using var mutationLock = await SavedStateTrashLocks.AcquireAsync(
            entry.OriginalPath,
            cancellationToken);
        if (!File.Exists(entry.PayloadPath)
            || PathIsReparsePoint(entry.PayloadPath)
            || (Directory.Exists(checkpointDirectory) && PathIsReparsePoint(checkpointDirectory)))
        {
            return SavedStateRestoreStatus.NotFound;
        }

        using var writeLease = await CrossProcessWriteLease.AcquireAsync(
            entry.OriginalPath,
            SnapshotWriteLeaseTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(sessionPath)
            || PathIsReparsePoint(sessionPath)
            || PathIsReparsePoint(checkpointDirectory)
            || !File.Exists(entry.PayloadPath)
            || PathIsReparsePoint(entry.PayloadPath))
        {
            return SavedStateRestoreStatus.NotFound;
        }

        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
        {
            return SavedStateRestoreStatus.NameCollision;
        }

        Directory.CreateDirectory(checkpointDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(entry.PayloadPath, entry.OriginalPath);
            return SavedStateRestoreStatus.Restored;
        }
        catch (IOException) when (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
        {
            return SavedStateRestoreStatus.NameCollision;
        }
    }

    private static bool SavedStatePayloadExists(ResolvedSavedStateTrashEntry entry)
    {
        return entry.Receipt.Kind == SavedStateDeletionKind.Session
            ? Directory.Exists(entry.PayloadPath) && !PathIsReparsePoint(entry.PayloadPath)
            : File.Exists(entry.PayloadPath) && !PathIsReparsePoint(entry.PayloadPath);
    }

    private static bool ReceiptsIdentifySameDeletion(
        SavedStateDeletionReceipt requested,
        SavedStateDeletionReceipt persisted)
    {
        return requested.Id.Equals(persisted.Id, StringComparison.OrdinalIgnoreCase)
            && requested.Kind == persisted.Kind
            && requested.SessionId.Equals(persisted.SessionId, StringComparison.OrdinalIgnoreCase)
            && requested.CheckpointId.Equals(persisted.CheckpointId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSavedStateDisplayName(string? value, string fallback)
    {
        var cleaned = new string((value ?? "")
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = fallback;
        }

        return cleaned[..Math.Min(cleaned.Length, 160)];
    }

    private static IReadOnlyList<string> EnumerateSavedStateTrashEntries(string trashRoot)
    {
        try
        {
            return Directory.Exists(trashRoot)
                ? Directory.EnumerateDirectories(trashRoot).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static int SavedStateTrashEntryCount(string trashRoot)
    {
        try
        {
            return Directory.Exists(trashRoot)
                ? Directory.EnumerateDirectories(trashRoot).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An unreadable root cannot prove capacity, so callers fail closed.
            return int.MaxValue;
        }
    }

    private static DateTimeOffset SavedStateTrashEntryLastWrite(string entryPath)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(entryPath), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathIsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        return ListSessionsAsync(SessionListingDetail.Full, cancellationToken);
    }

    /// <summary>
    /// Lists sessions at the requested level of detail.
    ///
    /// A data root can hold thousands of sessions, including ones written by
    /// other AI Arena implementations that share it, so callers that only need
    /// identities should not pay for per-session counts. Checkpoint and event
    /// counts are the expensive part: each means enumerating a directory or
    /// reading a log.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        SessionListingDetail detail,
        CancellationToken cancellationToken = default)
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
            var hasSnapshot = File.Exists(snapshotPath);
            var messageCount = detail >= SessionListingDetail.Messages && hasSnapshot
                ? await CountSnapshotMessagesAsync(snapshotPath, cancellationToken)
                : 0;
            var checkpointCount = detail >= SessionListingDetail.Full
                ? CountFiles(CheckpointDirectory(id), "*.json")
                : 0;
            var eventCount = detail >= SessionListingDetail.Full
                ? CountLines(NativeDataPaths.EventPath(DataRoot, id))
                : 0;
            var lastModified = DirectoryLastWriteTimeOrNow(sessionDir);
            summaries.Add(new SessionSummary(
                id,
                snapshotPath,
                hasSnapshot,
                messageCount,
                checkpointCount,
                eventCount,
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
        snapshotPath = Path.GetFullPath(snapshotPath);
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
            MessageCountCache.Remove(snapshotPath);
            return 0;
        }

        if (MessageCountCache.TryGet(snapshotPath, writeUtc, length, out var cachedCount))
        {
            return cachedCount;
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

        MessageCountCache.Set(snapshotPath, writeUtc, length, count);
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
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            EventLineCountCache.Remove(path);
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
            EventLineCountCache.Remove(path);
            return 0;
        }

        if (EventLineCountCache.TryGet(path, writeUtc, length, out var cachedCount))
        {
            return cachedCount;
        }

        if (!TryCountLinesUncached(path, out var counted))
        {
            return 0;
        }

        EventLineCountCache.Set(path, writeUtc, length, counted);
        return counted;
    }

    private static bool TryCountLinesUncached(string path, out int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            count = 0;
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            count = 0;
            return false;
        }
    }

    private void InvalidateSessionSummaryCaches(string sessionId)
    {
        TryRemoveSessionSummaryCacheEntry(MessageCountCache, SnapshotPath(sessionId));
        TryRemoveSessionSummaryCacheEntry(
            EventLineCountCache,
            NativeDataPaths.EventPath(DataRoot, sessionId));
    }

    private static void TryRemoveSessionSummaryCacheEntry(BoundedPathCountCache cache, string path)
    {
        try
        {
            cache.Remove(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Cache invalidation is only a memory-retention optimization. The
            // underlying move has already committed, and the fixed cache bound
            // still prevents an invalid path from growing process memory.
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

    private static bool DirectoryIsReparsePoint(string directory) => PathIsReparsePoint(directory);

    private static bool PathIsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private sealed class SavedStateTrashTombstone
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "";

        [JsonPropertyName("session_id")]
        public string SessionId { get; init; } = "";

        [JsonPropertyName("checkpoint_id")]
        public string CheckpointId { get; init; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("original_relative_path")]
        public string OriginalRelativePath { get; init; } = "";

        [JsonPropertyName("deleted_at_unix_ms")]
        public long DeletedAtUnixMilliseconds { get; init; }

        [JsonPropertyName("expires_at_unix_ms")]
        public long ExpiresAtUnixMilliseconds { get; init; }
    }

    private sealed record ResolvedSavedStateTrashEntry(
        SavedStateDeletionReceipt Receipt,
        string EntryPath,
        string PayloadPath,
        string OriginalPath);

    private sealed record SavedStateTrashInspection(
        string Id,
        string EntryPath,
        DateTimeOffset SortTime,
        bool ShouldPurge);

    private sealed class BoundedPathCountCache
    {
        private readonly int capacity;
        private readonly object gate = new();
        private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> recency = new();

        internal BoundedPathCountCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            this.capacity = capacity;
        }

        internal int Count
        {
            get
            {
                lock (gate)
                {
                    return entries.Count;
                }
            }
        }

        internal bool Contains(string path)
        {
            var fullPath = Path.GetFullPath(path);
            lock (gate)
            {
                return entries.ContainsKey(fullPath);
            }
        }

        internal bool TryGet(string path, DateTime writeUtc, long length, out int count)
        {
            var fullPath = Path.GetFullPath(path);
            lock (gate)
            {
                if (!entries.TryGetValue(fullPath, out var entry))
                {
                    count = 0;
                    return false;
                }

                if (entry.WriteUtc != writeUtc || entry.Length != length)
                {
                    RemoveUnderLock(fullPath, entry);
                    count = 0;
                    return false;
                }

                recency.Remove(entry.RecencyNode);
                recency.AddFirst(entry.RecencyNode);
                count = entry.Count;
                return true;
            }
        }

        internal void Set(string path, DateTime writeUtc, long length, int count)
        {
            var fullPath = Path.GetFullPath(path);
            lock (gate)
            {
                if (entries.Remove(fullPath, out var previous))
                {
                    recency.Remove(previous.RecencyNode);
                }

                var node = recency.AddFirst(fullPath);
                entries[fullPath] = new CacheEntry(writeUtc, length, count, node);
                while (entries.Count > capacity && recency.Last is { } oldest)
                {
                    recency.RemoveLast();
                    entries.Remove(oldest.Value);
                }
            }
        }

        internal void Remove(string path)
        {
            var fullPath = Path.GetFullPath(path);
            lock (gate)
            {
                if (entries.Remove(fullPath, out var entry))
                {
                    recency.Remove(entry.RecencyNode);
                }
            }
        }

        private void RemoveUnderLock(string fullPath, CacheEntry entry)
        {
            entries.Remove(fullPath);
            recency.Remove(entry.RecencyNode);
        }

        private sealed record CacheEntry(
            DateTime WriteUtc,
            long Length,
            int Count,
            LinkedListNode<string> RecencyNode);
    }

    private sealed class SavedStateTrashMaintenanceScope(
        string trashRoot,
        KeyedAsyncLockRegistry.Lease processLease,
        CrossProcessWriteLease crossProcessLease) : IDisposable
    {
        private KeyedAsyncLockRegistry.Lease? process = processLease;
        private CrossProcessWriteLease? crossProcess = crossProcessLease;

        internal string TrashRoot { get; } = trashRoot;

        public void Dispose()
        {
            Interlocked.Exchange(ref crossProcess, null)?.Dispose();
            Interlocked.Exchange(ref process, null)?.Dispose();
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
    long ForkedAt)
{
    public string BranchReceiptId { get; init; } = "";

    public string CursorMessageId { get; init; } = "";

    public int ExcludedMemoryEntryCount { get; init; }

    public int UnprojectableMemoryEntryCount { get; init; }

    public bool HistoricalSetupProjectionUnavailable { get; init; }

    public string ChildSetupFingerprint { get; init; } = "";
}

internal sealed record ArenaExperimentChildGuard(
    string SessionId,
    string ExperimentId,
    string ParentSessionId,
    long ParentPersistenceRevision,
    string ParentSetupFingerprint,
    string ChildSetupFingerprint);

internal sealed class ArenaExperimentProviderCallLease(
    CrossProcessWriteLease sessionTreeLease,
    CrossProcessWriteLease providerCallLease,
    long persistenceRevision) : IDisposable
{
    private CrossProcessWriteLease? _sessionTreeLease = sessionTreeLease;
    private CrossProcessWriteLease? _providerCallLease = providerCallLease;

    internal long PersistenceRevision { get; } = persistenceRevision;

    public void Dispose()
    {
        Interlocked.Exchange(ref _providerCallLease, null)?.Dispose();
        Interlocked.Exchange(ref _sessionTreeLease, null)?.Dispose();
    }
}

public sealed class ArenaExperimentSourceChangedException : InvalidOperationException
{
    public ArenaExperimentSourceChangedException()
        : base("The experiment source changed after its execution plan was resolved.")
    {
    }
}

internal sealed class ArenaExperimentChildDriftException : IOException
{
    internal ArenaExperimentChildDriftException()
        : base("The experiment child changed outside its guarded execution boundary.")
    {
    }
}

internal sealed class SessionIdentityConflictException : InvalidOperationException
{
    internal SessionIdentityConflictException(string message)
        : base(message)
    {
    }
}

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

public enum SavedStateDeletionKind
{
    Session,
    Checkpoint
}

public sealed record SavedStateDeletionReceipt(
    string Id,
    SavedStateDeletionKind Kind,
    string SessionId,
    string CheckpointId,
    string DisplayName,
    DateTimeOffset DeletedAt,
    DateTimeOffset ExpiresAt);

public enum SavedStateRestoreStatus
{
    Restored,
    NotFound,
    NameCollision,
    Expired,
    Invalid,
    Failed
}

public sealed record SavedStateRestoreResult(
    SavedStateRestoreStatus Status,
    SavedStateDeletionReceipt Receipt)
{
    public bool Restored => Status == SavedStateRestoreStatus.Restored;
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
