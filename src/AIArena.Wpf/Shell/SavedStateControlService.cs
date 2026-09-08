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

internal sealed record AIArenaSavedStatePostCommitProjection(
    string Outcome,
    AIArenaSavedStateControlState State);

/// <summary>
/// UI-independent saved-state command facade. MainWindow supplies only the active-session
/// boundary and refresh delegates; persistence and validation remain testable here.
/// </summary>
internal sealed class SavedStateControlService : IDisposable
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<SessionSummary, bool, CancellationToken, Task> loadSessionAsync;
    private readonly Func<string?, CancellationToken, Task> loadSessionsAsync;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;
    private readonly Func<string, CancellationToken, Task<SessionSelectionOutcome>>? selectSessionByIdAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionSummary>>> listSelectionSessionsAsync;
    private readonly SessionLoadCoordinator fallbackSelections = new();

    public SavedStateControlService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<SessionSummary?> activeSession,
        Func<SessionSummary, bool, CancellationToken, Task> loadSessionAsync,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        Func<string, CancellationToken, Task<SessionSelectionOutcome>>? selectSessionByIdAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SessionSummary>>>? listSelectionSessionsAsync = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.loadSessionAsync = loadSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.selectSessionByIdAsync = selectSessionByIdAsync;
        this.listSelectionSessionsAsync = listSelectionSessionsAsync ?? (token => sessionStore.ListSessionsAsync(token));
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
            return Failure("missing_argument", "session.select requires args.id.", await CaptureAsync(cancellationToken));

        var normalizedId = SessionStore.SafeSessionId(sessionId);
        // The shell callback acquires its shared lease before resolving this ID. Standalone
        // callers retain the same ordering through a local lease and cancellable load callback.
        var outcome = selectSessionByIdAsync is not null
            ? await selectSessionByIdAsync(normalizedId, cancellationToken)
            : await SelectSessionFallbackAsync(normalizedId, cancellationToken);
        var state = await CaptureAsync(cancellationToken);
        if (!outcome.Loaded) return Failure(outcome.ErrorCode, outcome.Message, state);
        return state.ActiveSessionId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(activeSession()?.Id, normalizedId, StringComparison.OrdinalIgnoreCase)
            ? Success(outcome.Message, state)
            : Failure("selection_superseded", "A newer session selection replaced this request.", state);
    }

    private async Task<SessionSelectionOutcome> SelectSessionFallbackAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = fallbackSelections.BeginSelection(sessionId, cancellationToken);
        try
        {
            var sessions = await listSelectionSessionsAsync(request.Token).WaitAsync(request.Token);
            if (!request.IsCurrent) return SupersededSelection();
            var selected = sessions.FirstOrDefault(session => session.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
            if (selected is null) return new(false, "not_found", $"Session '{sessionId}' was not found.");
            await loadSessionAsync(selected, true, request.Token);
            return request.IsCurrent
                ? new(true, "", $"Selected session: {selected.Id}.") : SupersededSelection();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SupersededSelection();
        }
    }

    private static SessionSelectionOutcome SupersededSelection() =>
        new(false, "selection_superseded", "A newer session selection replaced this request.");

    public void Dispose() => fallbackSelections.Dispose();

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

        // Keep a truthful, schema-valid fallback projection. If projection work
        // fails after the restore commits, the control request still succeeds
        // with an explicit warning instead of throwing or inventing state.
        var fallbackState = await CaptureAsync(cancellationToken);

        var result = await sessionStore.RestoreCheckpointWithSafetyCheckpointAsync(
            session.Id,
            selected.Id,
            cancellationToken);
        if (result is null)
        {
            return Failure("restore_failed", $"Checkpoint '{selected.Name}' could not be restored.", await CaptureAsync(cancellationToken));
        }

        var restored = result.RestoredCheckpoint;
        var safety = result.SafetyCheckpoint;
        // The live restore has committed. Treat event-log evidence as secondary
        // and complete refresh/capture with non-cancellable semantics so a late
        // logging or caller-cancellation failure cannot misreport the mutation.
        var evidence = await AppPostCommitEvidence.TryAppendAsync(
            eventLogStore,
            session.Id,
            "control_checkpoint_restored",
            new
            {
                restored.Id,
                restored.Name,
                safety_checkpoint_id = safety?.Checkpoint.Id ?? "",
                safety_checkpoint_name = safety?.Checkpoint.Name ?? "",
                protected_revision = safety?.ProtectedRevision,
                replacement_revision = safety?.ReplacementRevision
            },
            AppErrorContext.SavedState);
        var safetyStatus = safety is null
            ? "No prior live snapshot required a safety checkpoint."
            : $"Safety checkpoint: {safety.Checkpoint.Name}.";
        var outcome = evidence.AppendTo($"Restored checkpoint: {restored.Name}. {safetyStatus}");
        var projection = await CompleteCommittedRestoreProjectionAsync(
            outcome,
            fallbackState,
            refreshActiveSessionAsync,
            CaptureAsync);
        return Success(projection.Outcome, projection.State);
    }

    internal static async Task<AIArenaSavedStatePostCommitProjection> CompleteCommittedRestoreProjectionAsync(
        string outcome,
        AIArenaSavedStateControlState fallbackState,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        Func<CancellationToken, Task<AIArenaSavedStateControlState>> captureAsync)
    {
        ArgumentNullException.ThrowIfNull(fallbackState);
        ArgumentNullException.ThrowIfNull(refreshActiveSessionAsync);
        ArgumentNullException.ThrowIfNull(captureAsync);

        var refreshWarning = await AppPostCommitEvidence.TryCompleteAsync(
            () => refreshActiveSessionAsync(outcome, CancellationToken.None),
            "the restored session could not be refreshed",
            AppErrorContext.SavedState);
        outcome = AppPostCommitEvidence.AppendWarning(outcome, refreshWarning);

        var state = fallbackState;
        var captureWarning = await AppPostCommitEvidence.TryCompleteAsync(
            async () => state = await captureAsync(CancellationToken.None),
            "the restored Saved State projection could not be captured",
            AppErrorContext.SavedState);
        outcome = AppPostCommitEvidence.AppendWarning(outcome, captureWarning);
        return new AIArenaSavedStatePostCommitProjection(outcome, state);
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
