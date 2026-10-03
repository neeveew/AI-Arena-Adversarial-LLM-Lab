using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using CoreArenaSnapshot = AIArena.Core.Models.ArenaSnapshot;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class TranscriptMutationCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly TranscriptService transcriptService;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Func<CancellationToken, Task>, Task<bool>> runMutationAsync;
    private readonly Func<CoreArenaSnapshot, string, CancellationToken, Task> saveSnapshotAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string>? setMutationStatus;

    public TranscriptMutationCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TranscriptService transcriptService,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string, Func<CancellationToken, Task>, Task<bool>> runMutationAsync,
        Func<CoreArenaSnapshot, string, CancellationToken, Task> saveSnapshotAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string>? setMutationStatus = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.transcriptService = transcriptService;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.runMutationAsync = runMutationAsync;
        this.saveSnapshotAsync = saveSnapshotAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setMutationStatus = setMutationStatus;
    }

    public async Task DeleteMessageAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (!CanMutateMessage(message, isArenaBusy(), session))
        {
            return;
        }

        await MutateTranscriptAsync(session!, "Deleting transcript turn…", snapshot =>
        {
            var storedMessage = TranscriptService.FindMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (storedMessage is not null && FactoryConversationService.IsConversationRoot(storedMessage))
            {
                SetStatus(FactoryRootDeleteBlockedStatus(message));
                return null;
            }

            var deleted = transcriptService.DeleteMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (!deleted)
            {
                SetStatus($"Could not find turn {message.Turn} to delete.");
                return null;
            }

            return new MutationOutcome(DeleteStatus(message), "native_transcript_message_deleted",
                new { message.Turn, message.Speaker, message.SpeakerId });
        });
    }

    public async Task TogglePinMessageAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (!CanMutateMessage(message, isArenaBusy(), session))
        {
            return;
        }

        await MutateTranscriptAsync(session!, "Updating transcript pin…", snapshot =>
        {
            var changed = transcriptService.TogglePinned(snapshot, message.Turn, message.SpeakerId, message.CreatedAt, out var pinned);
            if (!changed)
            {
                SetStatus($"Could not find turn {message.Turn} to pin.");
                return null;
            }

            return new MutationOutcome(PinStatus(message.Turn, pinned),
                pinned ? "native_transcript_message_pinned" : "native_transcript_message_unpinned",
                new { message.Turn, message.Speaker, message.SpeakerId });
        });
    }

    internal static bool CanMutateMessage(TranscriptMessage message, bool arenaBusy, CoreSessionSummary? activeSession)
    {
        return !arenaBusy && activeSession is not null && message.Turn > 0;
    }

    internal static string DeleteStatus(TranscriptMessage message)
    {
        return $"Deleted turn {message.Turn}.";
    }

    internal static string FactoryRootDeleteBlockedStatus(TranscriptMessage message)
    {
        return $"Turn {message.Turn} is the Factory group root and cannot be deleted individually. Use Reset Arena or start a clean session to begin a new group.";
    }

    internal static string PinStatus(TranscriptMessage message)
    {
        return PinStatus(message.Turn, !message.Pinned);
    }

    private static string PinStatus(int turn, bool pinned) =>
        pinned ? $"Pinned turn {turn}." : $"Unpinned turn {turn}.";

    private sealed record MutationOutcome(string Status, string EventType, object Payload);

    private async Task MutateTranscriptAsync(
        CoreSessionSummary session, string progressStatus, Func<CoreArenaSnapshot, MutationOutcome?> mutation)
    {
        await runMutationAsync(progressStatus, async cancellationToken =>
        {
            try
            {
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (!string.Equals(activeSession()?.Id, session.Id, StringComparison.OrdinalIgnoreCase)) return;
                if (snapshot is null)
                {
                    SetStatus($"No snapshot found for session {session.Id}.");
                    return;
                }

                var outcome = mutation(snapshot);
                if (outcome is null) return;

                await saveSnapshotAsync(snapshot, session.Id, cancellationToken);
                var evidence = await AppPostCommitEvidence.TryAppendAsync(
                    eventLogStore, session.Id, outcome.EventType, outcome.Payload, AppErrorContext.Arena);
                var presentation = await AppPostCommitEvidence.RefreshAsync(
                    evidence.AppendTo(outcome.Status), committed: true, refreshActiveSessionAsync, AppErrorContext.Arena);
                SetStatus(presentation.Status);
            }
            catch (SnapshotConcurrencyException)
            {
                SetStatus("The session changed before the transcript edit was saved. Refresh the transcript and try again.");
            }
            catch (Exception exception)
            {
                // These commands are invoked from async UI handlers. Pre-save
                // failures must remain reported outcomes, not fatal dispatcher errors.
                SetStatus(AppErrorPresenter.Present(exception, AppErrorContext.Arena).DisplayText);
            }
        });
    }

    private void SetStatus(string status)
    {
        setLoadStatus(status);
        setMutationStatus?.Invoke(status);
    }
}
