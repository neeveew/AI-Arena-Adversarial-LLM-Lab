using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Persistence;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void AgentBoardOwnershipRejectsStaleControls() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo", "run", "narrate" })
        foreach (var fault in new[] { "switch", "switch-repopulate", "clear" })
        {
            var result = RunAgentBoardOwnershipCase(action, fault);
            if (!result.SourceUnchanged || !result.OtherUnchanged || result.Saves != 0 || result.Runs != 0
                || result.Narrations != 0 || result.Refreshes != 0 || result.BusyErrors != 0)
                failures.Add(action + "/" + fault);
        }
        Require(failures.Count == 0, "Retired agent controls acted on a replacement or cleared session: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipRejectsQueuedSessionChanges() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo" })
        {
            var result = RunAgentBoardOwnershipCase(action, "switch-queue");
            if (!result.SourceUnchanged || !result.OtherUnchanged || result.Saves != 0 || result.Refreshes != 0)
                failures.Add(action);
        }
        Require(failures.Count == 0, "Queued agent controls chose a replacement session: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipRejectsMissingTargets() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo" })
        {
            var result = RunAgentBoardOwnershipCase(action, "missing");
            if (!result.SourceUnchanged || !result.OtherUnchanged || result.Saves != 0 || result.Refreshes != 0 || result.BusyErrors != 0)
                failures.Add(action);
        }
        Require(failures.Count == 0, "An absent agent target changed the remaining cast: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipSavedChangesSurviveSecondaryFailures() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo" })
        foreach (var fault in new[] { "healthy", "log", "refresh", "both", "feedback", "cancel-refresh" })
        {
            var result = RunAgentBoardOwnershipCase(action, fault);
            var needsVisibleWarning = fault is "log" or "refresh" or "both" or "cancel-refresh";
            if (!result.Committed || !result.OtherUnchanged || result.Saves != 1 || result.Refreshes != 1 || result.BusyErrors != 0
                || result.Feedback.Any(text => text.Contains("PRIVATE_BOARD_FAULT", StringComparison.Ordinal))
                || (needsVisibleWarning && !result.Feedback.Any(text => text.Contains("Warning:", StringComparison.Ordinal))))
                failures.Add(action + "/" + fault);
        }
        Require(failures.Count == 0, "Saved agent controls were hidden or reported as failed after secondary faults: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipCompletionStaysWithOrigin() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo" })
        foreach (var fault in new[] { "switch-saved", "switch-refresh" })
        {
            var result = RunAgentBoardOwnershipCase(action, fault);
            if (!result.Committed || !result.OtherUnchanged || result.Saves != 1 || result.BusyErrors != 0
                || result.Refreshes != (fault == "switch-saved" ? 0 : 1) || result.Feedback.Length != 0)
                failures.Add(action + "/" + fault);
        }
        Require(failures.Count == 0, "Saved agent feedback affected a replacement session: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipAuthoritativeFailuresRemainFailures() => RunStaTest(() =>
    {
        foreach (var action in new[] { "pause", "resume", "solo" })
        {
            var result = RunAgentBoardOwnershipCase(action, "save-fail");
            Require(result.SourceUnchanged && result.OtherUnchanged && result.Saves == 1 && result.Refreshes == 0
                    && result.BusyErrors == 1 && result.Feedback.Length == 0,
                action + ": a failed snapshot save was treated as a completed change.");
        }
    });

    static void AgentBoardOwnershipHeldLogLeaseDoesNotHideSavedChanges() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "pause", "resume", "solo" })
        {
            var result = RunAgentBoardOwnershipCase(action, "lease");
            if (!result.ReturnedWhileHeld || !result.Committed || !result.OtherUnchanged || result.Refreshes != 1
                || result.BusyErrors != 0 || !result.Feedback.Any(text => text.Contains("Warning:", StringComparison.Ordinal)))
                failures.Add(action);
        }
        Require(failures.Count == 0, "A diagnostic lease delayed saved agent controls: " + string.Join(", ", failures));
    });

    static void AgentBoardOwnershipCurrentControlsRemainUsable() => RunStaTest(() =>
    {
        foreach (var action in new[] { "pause", "resume", "solo", "run", "narrate" })
        foreach (var fault in new[] { "healthy", "current-other" })
        {
            var result = RunAgentBoardOwnershipCase(action, fault);
            var mutation = action is "pause" or "resume" or "solo";
            Require(result.BusyErrors == 0 && result.Saves == (mutation ? 1 : 0)
                    && result.Refreshes == (mutation ? 1 : 0) && (!mutation || result.Committed)
                    && result.Runs == (action == "run" ? 1 : 0) && result.Narrations == (action == "narrate" ? 1 : 0)
                    && (fault == "current-other" ? result.SourceUnchanged : result.OtherUnchanged),
                action + "/" + fault + ": a current-session control no longer performs its action.");
        }
    });

    private sealed record AgentBoardOwnershipObservation(bool SourceUnchanged, bool OtherUnchanged, bool Committed,
        int Saves, int Refreshes, int BusyErrors, int Runs, int Narrations, bool ReturnedWhileHeld, string[] Feedback);

    private static AgentBoardOwnershipObservation RunAgentBoardOwnershipCase(string action, string fault)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-board-ownership-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Board fixture cleanup escaped its parent.");
        FileStream? held = null;
        try
        {
            var store = new SessionStore(root);
            var source = SessionStore.CreateDefaultSnapshot();
            if (action == "resume") { source.Engine.Agents[0].Active = false; source.Engine.Agents[0].Status = "muted"; }
            store.SaveSnapshotAsync(source, "default").GetAwaiter().GetResult();
            store.CreateSessionAsync("other", SessionStore.CreateDefaultSnapshot()).GetAwaiter().GetResult();
            var other = store.LoadSnapshotAsync("other").GetAwaiter().GetResult()!;
            if (action == "resume") { other.Engine.Agents[0].Active = false; other.Engine.Agents[0].Status = "muted"; }
            store.SaveSnapshotAsync(other, "other").GetAwaiter().GetResult();
            var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
            var originSession = sessions.Single(session => session.Id == "default");
            var otherSession = sessions.Single(session => session.Id == "other");
            AIArena.Core.Models.SessionSummary? active = originSession;
            var panel = new StackPanel();
            var log = EventLogStore.ForSessionStore(store);
            var saves = 0; var refreshes = 0; var busyErrors = 0; var runs = 0; var narrations = 0;
            var feedback = new List<string>();
            var operations = new List<Task>();
            async Task Run(Func<Task> work)
            {
                if (fault == "switch-queue") active = otherSession;
                try { await work(); } catch { busyErrors++; }
            }
            var coordinator = new AgentBoardCoordinator(store, log, panel, () => active, () => false, () => false,
                _ => Brushes.SteelBlue, (first, _, _) => first, _ => Brushes.DeepSkyBlue, id => id,
                _ => { runs++; return Task.CompletedTask; }, (_, _) => narrations++,
                (_, _, work, _) => { var operation = Run(work); operations.Add(operation); return operation; },
                async (snapshot, sessionId) =>
                {
                    saves++;
                    if (fault == "save-fail") throw new IOException(@"C:\Users\PRIVATE_BOARD_FAULT\snapshot.txt");
                    await store.SaveSnapshotAsync(snapshot, sessionId);
                    if (fault == "switch-saved") active = otherSession;
                },
                outcome =>
                {
                    refreshes++;
                    if (fault == "switch-refresh") active = otherSession;
                    if (fault == "cancel-refresh") return Task.FromCanceled(new CancellationToken(true));
                    if (fault is "refresh" or "both" or "feedback" or "switch-refresh")
                        throw new IOException(@"C:\Users\PRIVATE_BOARD_FAULT\refresh.txt");
                    feedback.Add(outcome);
                    return Task.CompletedTask;
                },
                outcome =>
                {
                    if (fault == "feedback") throw new IOException(@"C:\Users\PRIVATE_BOARD_FAULT\feedback.txt");
                    feedback.Add(outcome);
                }, () => false);
            var sourceView = SnapshotForOverviewTest(true, "fixture-model", "", 0, [], SnapshotViewMapper.FromCore(originSession, source).Agents)
                with { SessionId = originSession.Id };
            var otherView = sourceView with { SessionId = otherSession.Id, Agents = SnapshotViewMapper.FromCore(otherSession, other).Agents };
            coordinator.Populate(sourceView, "alpha");
            var card = coordinator.DiagnosticCardFor("alpha")!;
            var narrator = coordinator.DiagnosticCardFor("narrator")!;
            if (fault is "switch" or "switch-repopulate" or "current-other") active = otherSession;
            if (fault is "switch-repopulate" or "current-other") coordinator.Populate(otherView, "alpha");
            if (fault == "current-other") { card = coordinator.DiagnosticCardFor("alpha")!; narrator = coordinator.DiagnosticCardFor("narrator")!; }
            if (fault == "clear") coordinator.PopulateFallback();
            if (fault == "missing")
            {
                var missing = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
                missing.Engine.Agents.RemoveAll(agent => agent.Id == "alpha");
                store.SaveSnapshotAsync(missing, "default").GetAwaiter().GetResult();
            }
            var beforeSource = File.ReadAllText(originSession.SnapshotPath);
            var beforeOther = File.ReadAllText(otherSession.SnapshotPath);
            if (fault is "log" or "both" or "lease")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log.EventPath())!);
                held = new FileStream(log.EventPath() + (fault == "lease" ? ".write.lock" : ""), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, fault == "lease" ? FileShare.Delete : FileShare.None);
            }
            var started = Stopwatch.GetTimestamp();
            var buttons = DescendantButtons(card).ToArray();
            if (action == "narrate") DescendantButtons(narrator).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action is "run" or "resume") buttons.Single(button => button.ContextMenu is null).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else
            {
                var menu = buttons.Single(button => button.ContextMenu is not null).ContextMenu!;
                menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == (action == "pause" ? "Pause agent" : "Solo agent"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            var operation = Task.WhenAll(operations);
            var returnedWhileHeld = fault != "lease" || ReferenceEquals(Task.WhenAny(operation, Task.Delay(2_000)).GetAwaiter().GetResult(), operation);
            if (fault == "lease") { held!.Dispose(); held = null; }
            operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            held?.Dispose(); held = null;
            var after = store.LoadSnapshotAsync(fault == "current-other" ? "other" : "default").GetAwaiter().GetResult()!;
            var alpha = after.Engine.Agents.FirstOrDefault(agent => agent.Id == "alpha");
            var committed = alpha is not null && (action == "solo" ? alpha.Active && alpha.Status == "waiting" && after.Engine.Agents.Where(agent => agent.Id != "alpha").All(agent => !agent.Active && agent.Status == "muted")
                : action == "pause" ? !alpha.Active && alpha.Status == "muted"
                : action == "resume" && alpha.Active && alpha.Status == "waiting");
            var sourceUnchanged = beforeSource == File.ReadAllText(originSession.SnapshotPath);
            var otherUnchanged = beforeOther == File.ReadAllText(otherSession.SnapshotPath);
            Console.WriteLine($"board ownership receipt: action={action}; fault={fault}; source_unchanged={sourceUnchanged}; other_unchanged={otherUnchanged}; committed={committed}; saves={saves}; refreshes={refreshes}; busy_errors={busyErrors}; runs={runs}; narrations={narrations}; returned_while_held={returnedWhileHeld}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
            return new(sourceUnchanged, otherUnchanged, committed, saves, refreshes, busyErrors, runs, narrations, returnedWhileHeld, feedback.ToArray());
        }
        finally { held?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
