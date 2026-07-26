using System.IO;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

namespace AIArena.Wpf;

internal sealed record AIArenaSessionForkReceipt(
    string SourceSessionId,
    string ForkSessionId,
    long SourcePersistenceRevision,
    long ForkPersistenceRevision,
    int TurnCount,
    int MessageCount,
    int NarrationCount,
    int ActiveAgentCount,
    int GenerationHistoryCount,
    long ForkedAt);

internal sealed record AIArenaSessionForkWorkflowResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaSessionForkReceipt? Receipt = null);

/// <summary>
/// Protocol- and visual-independent workflow for branching the current match. The
/// host supplies its exclusive-operation and session-selection boundaries so UI
/// and control-plane callers share one persistence, audit, and receipt path.
/// </summary>
internal sealed class SessionForkWorkflowService
{
    private const string ForkStatus = "Forking current match...";

    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<bool> isBusy;
    private readonly Func<string, Func<CancellationToken, Task>, Task> runExclusiveOperationAsync;
    private readonly Func<string, CancellationToken, Task> selectSessionAsync;
    private readonly Action<string, string, object?> publishEvent;

    public SessionForkWorkflowService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<SessionSummary?> activeSession,
        Func<bool> isBusy,
        Func<string, Func<CancellationToken, Task>, Task> runExclusiveOperationAsync,
        Func<string, CancellationToken, Task> selectSessionAsync,
        Action<string, string, object?> publishEvent)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.isBusy = isBusy;
        this.runExclusiveOperationAsync = runExclusiveOperationAsync;
        this.selectSessionAsync = selectSessionAsync;
        this.publishEvent = publishEvent;
    }

    public async Task<AIArenaSessionForkWorkflowResult> ForkCurrentAsync(
        string? requestedName = null,
        CancellationToken cancellationToken = default)
    {
        if (isBusy())
        {
            return Failure("busy", "The arena is busy; the current match was not forked.");
        }

        var source = activeSession();
        if (source is null)
        {
            return Failure("not_found", "No active session is available to fork.");
        }

        if (!TryNormalizeRequestedName(requestedName, out var targetName))
        {
            return Failure("invalid_argument", "The requested fork name is invalid.");
        }

        AIArenaSessionForkReceipt? receipt = null;
        var selected = false;
        Exception? forkFailure = null;
        Exception? auditFailure = null;
        try
        {
            await runExclusiveOperationAsync(ForkStatus, async operationCancellationToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    operationCancellationToken);
                SessionForkResult fork;
                try
                {
                    fork = await sessionStore.ForkSessionAsync(source.Id, targetName, linked.Token);
                }
                catch (Exception ex)
                {
                    // ArenaOperationCoordinator intentionally turns callback failures
                    // into UI status. Capture the original exception here so command
                    // callers still receive the stable workflow error contract.
                    forkFailure = ex;
                    return;
                }

                receipt = ToReceipt(fork);

                // The durable event deliberately contains only lineage identifiers,
                // revisions, counts, and time. Match text, prompts, provider details,
                // credentials, paths, and transcript content never enter the receipt.
                try
                {
                    await eventLogStore.AppendAsync(
                        fork.TargetSessionId,
                        "control_session_fork_created",
                        receipt,
                        linked.Token);
                }
                catch (Exception ex)
                {
                    auditFailure = ex;
                }

                try
                {
                    await eventLogStore.AppendAsync(
                        fork.SourceSessionId,
                        "control_session_fork_child_created",
                        receipt,
                        linked.Token);
                }
                catch (Exception ex)
                {
                    auditFailure ??= ex;
                }

                try
                {
                    await selectSessionAsync(fork.TargetSessionId, linked.Token);
                    selected = true;
                }
                catch (Exception)
                {
                }
            });
        }
        catch (Exception ex)
        {
            forkFailure ??= ex;
        }

        if (receipt is null)
        {
            if (forkFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw forkFailure;
            }

            if (forkFailure is FileNotFoundException)
            {
                return Failure("not_found", $"Session '{source.Id}' no longer has a persisted match to fork.");
            }

            if (forkFailure is ArgumentException)
            {
                return Failure("invalid_argument", "The requested fork name is invalid.");
            }

            return Failure(
                isBusy() ? "busy" : "operation_failed",
                isBusy()
                    ? "The arena became busy before the current match could be forked."
                    : "The current match could not be forked.");
        }

        if (!selected)
        {
            var selectionMessage = $"Fork '{receipt.ForkSessionId}' was created, but it could not be selected.";
            publishEvent("session.fork.selection_failed", selectionMessage, receipt);
            return Failure("selection_failed", selectionMessage, receipt);
        }

        if (auditFailure is not null)
        {
            var auditMessage = $"Fork '{receipt.ForkSessionId}' was created and selected, but its audit event could not be fully recorded.";
            publishEvent("session.fork.audit_failed", auditMessage, receipt);
            return Failure("audit_failed", auditMessage, receipt);
        }

        var message = $"Forked current match to '{receipt.ForkSessionId}' and selected it.";
        publishEvent("session.forked", message, receipt);
        return new AIArenaSessionForkWorkflowResult(true, "", message, receipt);
    }

    internal static bool TryNormalizeRequestedName(string? requestedName, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return true;
        }

        var trimmed = requestedName.Trim();
        var safe = SessionStore.SafeSessionId(trimmed);
        if (safe.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = safe;
        return true;
    }

    private static AIArenaSessionForkReceipt ToReceipt(SessionForkResult result)
    {
        return new AIArenaSessionForkReceipt(
            result.SourceSessionId,
            result.TargetSessionId,
            result.SourcePersistenceRevision,
            result.TargetPersistenceRevision,
            result.TurnCount,
            result.MessageCount,
            result.NarrationCount,
            result.ActiveAgentCount,
            result.GenerationHistoryCount,
            result.ForkedAt);
    }

    private static AIArenaSessionForkWorkflowResult Failure(
        string errorCode,
        string message,
        AIArenaSessionForkReceipt? receipt = null)
    {
        return new AIArenaSessionForkWorkflowResult(false, errorCode, message, receipt);
    }
}
