using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf;

internal static partial class Program
{
    static void SavedStateSelectionRejectsDeferredOlderEnumeration()
    {
        WithSavedCommitFixture((store, events, directory) =>
        {
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "newer").GetAwaiter().GetResult();
            var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
            SessionSummary? active = sessions.First(item => item.Id == "default");
            var pendingList = new TaskCompletionSource<IReadOnlyList<SessionSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enumerations = 0;
            var applied = new List<string>();
            using var service = new SavedStateControlService(store, events, () => active,
                (session, force, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    active = session;
                    applied.Add(session.Id);
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
                listSelectionSessionsAsync: _ => ++enumerations == 1
                    ? pendingList.Task : Task.FromResult(sessions));
            var older = service.SelectSessionAsync("default");
            Require(!older.IsCompleted, "The first control selection must remain deferred in session enumeration");
            var newer = service.SelectSessionAsync("newer").GetAwaiter().GetResult();
            pendingList.SetResult(sessions);
            var obsolete = older.GetAwaiter().GetResult();
            Require(newer.Ok && active?.Id == "newer" && applied.SequenceEqual(["newer"]),
                "Only the newer control selection may reach the shell load callback when the older list finishes late");
            Require(!obsolete.Ok && obsolete.ErrorCode == "selection_superseded",
                "An obsolete selection must report supersession instead of claiming to have selected its former target");
        });
    }

    static void SavedStateSelectionUsesOwnedShellOutcome()
    {
        WithSavedCommitFixture((store, events, directory) =>
        {
            var active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            using var caller = new CancellationTokenSource();
            var shellCalled = false;
            using var service = new SavedStateControlService(store, events, () => active,
                (_, _, _) => throw new InvalidOperationException("Owned selection must not use the legacy load callback."),
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
                selectSessionByIdAsync: (sessionId, token) =>
                {
                    shellCalled = true;
                    Require(sessionId == "default" && token == caller.Token,
                        "The shell must receive the exact normalized target and caller token before selection enumeration");
                    return Task.FromResult(new SessionSelectionOutcome(false, "load_failed", "The session snapshot could not be loaded."));
                },
                listSelectionSessionsAsync: _ => throw new InvalidOperationException("Selection enumeration belongs to the shell lease."));
            var result = service.SelectSessionAsync("default", caller.Token).GetAwaiter().GetResult();
            Require(shellCalled && !result.Ok && result.ErrorCode == "load_failed" && result.State.ActiveSessionId == "default",
                "A failed snapshot read must remain a failed selection even when fallback UI retains the requested session ID");
        });
    }
}
