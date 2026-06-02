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
    private readonly Func<CoreArenaSnapshot, string, Task> saveSnapshotAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;

    public TranscriptMutationCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TranscriptService transcriptService,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<CoreArenaSnapshot, string, Task> saveSnapshotAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.transcriptService = transcriptService;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.saveSnapshotAsync = saveSnapshotAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
    }

    public async Task DeleteMessageAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (!CanMutateMessage(message, isArenaBusy(), session))
        {
            return;
        }

        await MutateTranscriptAsync(DeleteStatus(message), async snapshot =>
        {
            var deleted = transcriptService.DeleteMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (!deleted)
            {
                setLoadStatus($"Could not find turn {message.Turn} to delete.");
                return false;
            }

            await eventLogStore.AppendAsync(session!.Id, "native_transcript_message_deleted", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
        });
    }

    public async Task TogglePinMessageAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (!CanMutateMessage(message, isArenaBusy(), session))
        {
            return;
        }

        await MutateTranscriptAsync(PinStatus(message), async snapshot =>
        {
            var changed = transcriptService.TogglePinned(snapshot, message.Turn, message.SpeakerId, message.CreatedAt, out var pinned);
            if (!changed)
            {
                setLoadStatus($"Could not find turn {message.Turn} to pin.");
                return false;
            }

            await eventLogStore.AppendAsync(session!.Id, pinned ? "native_transcript_message_pinned" : "native_transcript_message_unpinned", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
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

    internal static string PinStatus(TranscriptMessage message)
    {
        return message.Pinned
            ? $"Unpinned turn {message.Turn}."
            : $"Pinned turn {message.Turn}.";
    }

    private async Task MutateTranscriptAsync(string successStatus, Func<CoreArenaSnapshot, Task<bool>> mutation)
    {
        var session = activeSession();
        if (session is null)
        {
            return;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
        if (snapshot is null)
        {
            setLoadStatus($"No snapshot found for session {session.Id}.");
            return;
        }

        if (!await mutation(snapshot))
        {
            return;
        }

        await saveSnapshotAsync(snapshot, session.Id);
        await refreshActiveSessionAsync(successStatus);
    }
}
