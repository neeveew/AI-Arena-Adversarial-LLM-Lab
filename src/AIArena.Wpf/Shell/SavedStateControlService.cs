using AIArena.Core.Models;
using AIArena.Core.Persistence;

namespace AIArena.Wpf;

internal sealed record AIArenaSessionControlItem(
    string Id,
    bool Active,
    bool HasSnapshot,
    int MessageCount,
    int CheckpointCount,
    int EventCount,
    DateTimeOffset LastModified);

internal sealed record AIArenaCheckpointControlItem(
    string Id,
    string Name,
    string SessionId,
    DateTimeOffset CreatedAt);

internal sealed record AIArenaSavedStateControlState(
    string ActiveSessionId,
    SessionForkLineage? ActiveForkLineage,
    bool ParentAvailable,
    IReadOnlyList<AIArenaSessionControlItem> Sessions,
    IReadOnlyList<AIArenaCheckpointControlItem> Checkpoints);

internal sealed record AIArenaSavedStateControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaSavedStateControlState State);

/// <summary>
/// UI-independent saved-state command facade. MainWindow supplies only the active-session
/// boundary and refresh delegates; persistence and validation remain testable here.
/// </summary>
internal sealed class SavedStateControlService
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<SessionSummary, bool, CancellationToken, Task> loadSessionAsync;
    private readonly Func<string?, CancellationToken, Task> loadSessionsAsync;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;

    public SavedStateControlService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<SessionSummary?> activeSession,
        Func<SessionSummary, bool, CancellationToken, Task> loadSessionAsync,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.loadSessionAsync = loadSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
    }

    public async Task<AIArenaSavedStateControlState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var currentId = activeSession()?.Id ?? "";
        var sessions = await sessionStore.ListSessionsAsync(cancellationToken);
        var activeSnapshot = string.IsNullOrWhiteSpace(currentId)
            ? null
            : await sessionStore.LoadSnapshotAsync(currentId, cancellationToken);
        var activeForkLineage = activeSnapshot?.ForkLineage;
        var parentAvailable = activeForkLineage is not null
            && sessions.Any(session => session.Id.Equals(
                activeForkLineage.ParentSessionId,
                StringComparison.OrdinalIgnoreCase));
        var checkpoints = string.IsNullOrWhiteSpace(currentId)
            ? []
            : await sessionStore.ListCheckpointsAsync(currentId, cancellationToken);
        return new AIArenaSavedStateControlState(
            currentId,
            activeForkLineage,
            parentAvailable,
            sessions.Select(session => new AIArenaSessionControlItem(
                session.Id,
                session.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase),
                session.HasSnapshot,
                session.MessageCount,
                session.CheckpointCount,
                session.EventCount,
                session.LastModified)).ToArray(),
            checkpoints.Select(ToControlItem).ToArray());
    }

    public async Task<AIArenaSavedStateControlResult> SelectSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Failure("missing_argument", "session.select requires args.id.", await CaptureAsync(cancellationToken));
        }

        var normalizedId = SessionStore.SafeSessionId(sessionId);
        var sessions = await sessionStore.ListSessionsAsync(cancellationToken);
        var selected = sessions.FirstOrDefault(session =>
            session.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return Failure("not_found", $"Session '{sessionId}' was not found.", await CaptureAsync(cancellationToken));
        }

        await loadSessionAsync(selected, true, cancellationToken);
        return Success($"Selected session: {selected.Id}.", await CaptureAsync(cancellationToken));
    }

    public async Task<AIArenaSavedStateControlResult> CreateSessionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var source = activeSession();
        if (source is null)
        {
            return Failure("not_available", "No active session is available to copy.", await CaptureAsync(cancellationToken));
        }

        var newSessionId = SessionStore.SafeSessionId(name);
        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            return Failure("invalid_argument", "A valid session name is required.", await CaptureAsync(cancellationToken));
        }

        var sessions = await sessionStore.ListSessionsAsync(cancellationToken);
        if (sessions.Any(session => session.Id.Equals(newSessionId, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure("already_exists", $"Session '{newSessionId}' already exists.", await CaptureAsync(cancellationToken));
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(source.Id, cancellationToken);
        if (snapshot is null)
        {
            return Failure("not_available", $"Session '{source.Id}' has no snapshot to copy.", await CaptureAsync(cancellationToken));
        }

        await sessionStore.CreateSessionAsync(newSessionId, snapshot, cancellationToken);
        await eventLogStore.AppendAsync(newSessionId, "control_session_created", new { source = source.Id });
        await loadSessionsAsync(newSessionId, cancellationToken);
        return Success($"Created and selected session: {newSessionId}.", await CaptureAsync(cancellationToken));
    }

    public async Task<AIArenaSavedStateControlResult> SaveCheckpointAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var session = activeSession();
        if (session is null)
        {
            return Failure("not_available", "No active session is available to checkpoint.", await CaptureAsync(cancellationToken));
        }

        var checkpoint = await sessionStore.SaveCheckpointAsync(session.Id, name, cancellationToken);
        await eventLogStore.AppendAsync(session.Id, "control_checkpoint_saved", new { checkpoint.Id, checkpoint.Name });
        return Success($"Saved checkpoint: {checkpoint.Name}.", await CaptureAsync(cancellationToken));
    }

    public async Task<AIArenaSavedStateControlResult> RestoreCheckpointAsync(
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            return Failure("missing_argument", "session.checkpoint.restore requires args.id.", await CaptureAsync(cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return Failure("not_available", "No active session is available for restore.", await CaptureAsync(cancellationToken));
        }

        var checkpoints = await sessionStore.ListCheckpointsAsync(session.Id, cancellationToken);
        var selected = checkpoints.FirstOrDefault(checkpoint =>
            checkpoint.Id.Equals(checkpointId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return Failure("not_found", $"Checkpoint '{checkpointId}' was not found in session '{session.Id}'.", await CaptureAsync(cancellationToken));
        }

        var restored = await sessionStore.RestoreCheckpointAsync(session.Id, selected.Id, cancellationToken);
        if (restored is null)
        {
            return Failure("restore_failed", $"Checkpoint '{selected.Name}' could not be restored.", await CaptureAsync(cancellationToken));
        }

        await eventLogStore.AppendAsync(session.Id, "control_checkpoint_restored", new { restored.Id, restored.Name });
        await refreshActiveSessionAsync($"Restored checkpoint: {restored.Name}.", cancellationToken);
        return Success($"Restored checkpoint: {restored.Name}.", await CaptureAsync(cancellationToken));
    }

    private static AIArenaCheckpointControlItem ToControlItem(CheckpointSummary checkpoint)
    {
        return new AIArenaCheckpointControlItem(
            checkpoint.Id,
            checkpoint.Name,
            checkpoint.SessionId,
            DateTimeOffset.FromUnixTimeSeconds(checkpoint.CreatedAt));
    }

    private static AIArenaSavedStateControlResult Success(string message, AIArenaSavedStateControlState state)
    {
        return new AIArenaSavedStateControlResult(true, "", message, state);
    }

    private static AIArenaSavedStateControlResult Failure(
        string errorCode,
        string message,
        AIArenaSavedStateControlState state)
    {
        return new AIArenaSavedStateControlResult(false, errorCode, message, state);
    }
}
