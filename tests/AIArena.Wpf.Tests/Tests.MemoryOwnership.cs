using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Persistence;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static SharedPopupFixtureApplication? memoryApplicationFixture;

    static void MemoryOwnershipDialogsRejectSessionReplacement() => RunMemoryOwnershipProcess("dialogs");
    static void MemoryOwnershipQueuedActionsRejectSessionReplacement() => RunMemoryOwnershipProcess("queue");
    static void MemoryOwnershipStaleButtonsCannotOpenOrMutate() => RunMemoryOwnershipProcess("stale");
    static void MemoryOwnershipHealthyDialogsAndCancelRemainUsable() => RunMemoryOwnershipProcess("healthy");
    static void MemoryOwnershipSavedChangesSurviveSecondaryFaults() => RunMemoryOwnershipProcess("secondary");
    static void MemoryOwnershipAuthoritativeFailureDoesNotClaimSaved() => RunMemoryOwnershipProcess("save-fail");

    private static void RunMemoryOwnershipProcess(string group)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Test host executable is unavailable.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--memory-ownership-fixture");
        start.ArgumentList.Add(group);
        start.Environment["AI_ARENA_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "ai-arena-memory-app-" + Guid.NewGuid().ToString("N"));
        start.Environment["AI_ARENA_CONTROL_OWNER"] = Guid.NewGuid().ToString("N");
        start.Environment.Remove("AI_ARENA_CONTROL_TOKEN");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Memory fixture did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Owned memory fixture exceeded its budget.");
        }
        Console.Write(output.GetAwaiter().GetResult());
        Require(process.ExitCode == 0, "Memory fixture failed: " + error.GetAwaiter().GetResult());
    }

    private static int RunMemoryOwnershipFixture(string group)
    {
        try
        {
            Action test = group switch
            {
                "dialogs" => MemoryOwnershipDialogsRejectSessionReplacementCore,
                "queue" => MemoryOwnershipQueuedActionsRejectSessionReplacementCore,
                "stale" => MemoryOwnershipStaleButtonsCannotOpenOrMutateCore,
                "healthy" => MemoryOwnershipHealthyDialogsAndCancelRemainUsableCore,
                "secondary" => MemoryOwnershipSavedChangesSurviveSecondaryFaultsCore,
                "save-fail" => MemoryOwnershipAuthoritativeFailureDoesNotClaimSavedCore,
                _ => throw new InvalidOperationException("Unknown memory fixture group.")
            };
            test();
            memoryApplicationFixture?.ThrowDispatcherFailure();
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    static void MemoryOwnershipDialogsRejectSessionReplacementCore() => RunStaTest(() =>
    {
        foreach (var action in new[] { "edit", "clear" })
        {
            var result = RunMemoryOwnershipCase(action, "switch-dialog");
            Require(result.DialogSeen && result.SourceUnchanged && result.OtherUnchanged && result.Saves == 0 && result.Refreshes == 0,
                action + ": accepting a dialog after a session switch wrote or cleared private notes in the wrong session.");
        }
    });

    static void MemoryOwnershipQueuedActionsRejectSessionReplacementCore() => RunStaTest(() =>
    {
        foreach (var action in new[] { "save", "clear" })
        {
            var result = RunMemoryOwnershipCase(action, "switch-queue");
            Require(result.SourceUnchanged && result.OtherUnchanged && result.Saves == 0 && result.Refreshes == 0,
                action + ": a queued memory action selected a replacement session when the busy callback began.");
        }
    });

    static void MemoryOwnershipStaleButtonsCannotOpenOrMutateCore() => RunStaTest(() =>
    {
        foreach (var action in new[] { "stale-edit", "stale-clear", "stale-all" })
        {
            var result = RunMemoryOwnershipCase(action, "healthy");
            Require(!result.DialogSeen && result.SourceUnchanged && result.OtherUnchanged && result.Saves == 0,
                action + ": a prior-session memory button opened a stale dialog or changed the replacement session.");
        }
    });

    static void MemoryOwnershipHealthyDialogsAndCancelRemainUsableCore() => RunStaTest(() =>
    {
        foreach (var action in new[] { "edit", "clear" })
        foreach (var fault in new[] { "healthy", "cancel" })
        {
            var result = RunMemoryOwnershipCase(action, fault);
            Require(result.DialogSeen && result.OtherUnchanged && result.BusyErrors == 0
                    && (fault == "cancel" ? result.SourceUnchanged && result.Saves == 0 : result.Committed && result.Saves == 1 && result.Refreshes == 1),
                action + "/" + fault + ": ordinary edit, clear, or cancellation behavior changed.");
        }
    });

    static void MemoryOwnershipSavedChangesSurviveSecondaryFaultsCore() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var action in new[] { "save", "clear" })
        foreach (var fault in new[] { "healthy", "log", "refresh", "both", "feedback" })
        {
            var result = RunMemoryOwnershipCase(action, fault);
            if (!result.Committed || !result.OtherUnchanged || result.Saves != 1 || result.Refreshes != 1 || result.BusyErrors != 0
                || result.Feedback.Any(text => text.Contains("PRIVATE_MEMORY_FAULT", StringComparison.Ordinal)))
                failures.Add(action + "/" + fault);
        }
        Require(failures.Count == 0, "Saved memory outcomes escaped shared completion handling: " + string.Join(", ", failures));
    });

    static void MemoryOwnershipAuthoritativeFailureDoesNotClaimSavedCore() => RunStaTest(() =>
    {
        foreach (var action in new[] { "save", "clear" })
        {
            var result = RunMemoryOwnershipCase(action, "save-fail");
            Require(result.SourceUnchanged && result.OtherUnchanged && result.Saves == 1 && result.Refreshes == 0 && result.BusyErrors == 1,
                action + ": failure of the authoritative memory write was hidden or misreported.");
        }
    });

    private sealed record MemoryOwnershipObservation(bool DialogSeen, bool SourceUnchanged, bool OtherUnchanged,
        bool Committed, int Saves, int Refreshes, int BusyErrors, string[] Feedback);

    private static MemoryOwnershipObservation RunMemoryOwnershipCase(string action, string fault)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-memory-owner-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Memory fixture cleanup escaped its parent.");
        if (memoryApplicationFixture is null)
        {
            Require(Application.Current is null, "Memory fixture must own its only WPF Application.");
            memoryApplicationFixture = new SharedPopupFixtureApplication();
            memoryApplicationFixture.Application.InitializeComponent();
        }
        var owner = new Window { Width = 900, Height = 700, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false, Content = new Border() };
        AttachArenaPresentationResources(owner);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        FileStream? held = null;
        try
        {
            var store = new SessionStore(root);
            var source = SessionStore.CreateDefaultSnapshot();
            var other = SessionStore.CreateDefaultSnapshot();
            foreach (var agent in source.Engine.Agents) { agent.PrivateNotes.Clear(); agent.PrivateNotes.Add("ORIGIN_" + agent.Id); }
            foreach (var agent in other.Engine.Agents) { agent.PrivateNotes.Clear(); agent.PrivateNotes.Add("OTHER_" + agent.Id); }
            store.SaveSnapshotAsync(source, "default").GetAwaiter().GetResult();
            store.CreateSessionAsync("other", other).GetAwaiter().GetResult();
            other = store.LoadSnapshotAsync("other").GetAwaiter().GetResult()!;
            foreach (var agent in other.Engine.Agents) { agent.PrivateNotes.Clear(); agent.PrivateNotes.Add("OTHER_" + agent.Id); }
            store.SaveSnapshotAsync(other, "other").GetAwaiter().GetResult();
            Require(store.LoadSnapshotAsync("other").GetAwaiter().GetResult()!.Engine.Agents.All(agent => agent.PrivateNotes.SequenceEqual(new[] { "OTHER_" + agent.Id })), "Replacement-session canaries were not initialized.");
            var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
            var originSession = sessions.Single(session => session.Id == "default");
            var otherSession = sessions.Single(session => session.Id == "other");
            AIArena.Core.Models.SessionSummary? active = originSession;
            var log = EventLogStore.ForSessionStore(store);
            var saves = 0;
            var refreshes = 0;
            var busyErrors = 0;
            var dialogSeen = false;
            var feedback = new List<string>();
            var operations = new List<Task>();
            Exception? dialogFailure = null;
            var buttons = new List<Button>();
            if (fault is "log" or "both")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log.EventPath())!);
                held = new FileStream(log.EventPath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            async Task Run(Func<Task> work)
            {
                if (fault == "switch-queue") active = otherSession;
                try { await work(); } catch { busyErrors++; }
            }
            var coordinator = new AgentMemoryCoordinator(owner, store, log, () => ThemePalette.Resolve("dark-blue"),
                () => active, () => false, _ => Brushes.Black, (first, _, _) => first, _ => Brushes.Blue,
                model => model, id => id,
                (text, handler, enabled, _, _) =>
                {
                    var button = new Button { Content = text, IsEnabled = enabled };
                    if (handler is not null) button.Click += handler;
                    buttons.Add(button);
                    return button;
                },
                () => { },
                (_, work) => { var operation = Run(work); operations.Add(operation); return operation; },
                async (snapshot, sessionId) =>
                {
                    saves++;
                    if (fault == "save-fail") throw new IOException(@"C:\Users\PRIVATE_MEMORY_FAULT\snapshot.txt");
                    await store.SaveSnapshotAsync(snapshot, sessionId);
                },
                _ =>
                {
                    refreshes++;
                    if (fault is "refresh" or "both" or "feedback") throw new IOException(@"C:\Users\PRIVATE_MEMORY_FAULT\refresh.txt");
                    return Task.CompletedTask;
                },
                status =>
                {
                    if (fault == "feedback") throw new IOException(@"C:\Users\PRIVATE_MEMORY_FAULT\feedback.txt");
                    feedback.Add(status);
                });
            var view = SnapshotViewMapper.FromCore(originSession, source);
            var agentView = view.Agents.First(agent => agent.Id == "alpha");
            var began = Stopwatch.GetTimestamp();
            timer.Tick += (_, _) =>
            {
                var dialog = owner.OwnedWindows.Cast<Window>().FirstOrDefault(window => window is TextEditDialog or ConfirmDialog);
                if (dialog is null) return;
                dialogSeen = true;
                timer.Stop();
                try
                {
                    dialog.Left = -30000; dialog.Top = -30000;
                    if (fault == "switch-dialog") active = otherSession;
                    if (dialog is TextEditDialog editor)
                    {
                        ((TextBox)editor.FindName("EditText")).Text = "EDIT_A\nEDIT_B\nEDIT_A";
                        ((Button)editor.FindName(fault == "cancel" ? "CancelButton" : "ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else ((Button)dialog.FindName(fault == "cancel" ? "CancelButton" : "ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { dialogFailure = exception; dialog.Close(); }
            };
            owner.Show();
            memoryApplicationFixture.AssertIsolatedStartup(owner);
            timer.Start();
            Task operation;
            if (action.StartsWith("stale-", StringComparison.Ordinal))
            {
                _ = coordinator.CreatePanel(view);
                active = otherSession;
                var label = action == "stale-edit" ? "Edit" : action == "stale-all" ? "Clear all" : "Clear";
                buttons.First(button => (string)button.Content == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                operation = Task.WhenAll(operations);
            }
            else operation = action == "edit" ? coordinator.EditAgentMemoryNotesAsync(agentView)
                : action == "clear" ? coordinator.ClearAllAgentMemoryNotesAsync()
                : coordinator.SaveAgentMemoryNotesAsync("alpha", ["EDIT_A", "EDIT_B"]);
            PumpMemoryOwnershipTask(operation);
            timer.Stop();
            if (dialogFailure is not null) throw new InvalidOperationException("The real dialog action failed.", dialogFailure);
            held?.Dispose(); held = null;
            var after = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            var otherAfter = store.LoadSnapshotAsync("other").GetAwaiter().GetResult()!;
            var sourceUnchanged = after.Engine.Agents.All(agent => agent.PrivateNotes.SequenceEqual(new[] { "ORIGIN_" + agent.Id }));
            var otherUnchanged = otherAfter.Engine.Agents.All(agent => agent.PrivateNotes.SequenceEqual(new[] { "OTHER_" + agent.Id }));
            var committed = action == "clear" ? after.Engine.Agents.All(agent => agent.PrivateNotes.Count == 0)
                : after.Engine.Agents.Single(agent => agent.Id == "alpha").PrivateNotes.SequenceEqual(new[] { "EDIT_A", "EDIT_B" });
            Console.WriteLine($"memory ownership receipt: action={action}; fault={fault}; dialog_seen={dialogSeen}; source_unchanged={sourceUnchanged}; other_unchanged={otherUnchanged}; committed={committed}; saves={saves}; refreshes={refreshes}; busy_errors={busyErrors}; elapsed_ms={Stopwatch.GetElapsedTime(began).TotalMilliseconds:0}");
            return new MemoryOwnershipObservation(dialogSeen, sourceUnchanged, otherUnchanged, committed, saves, refreshes, busyErrors, feedback.ToArray());
        }
        finally
        {
            timer.Stop(); held?.Dispose();
            foreach (Window dialog in owner.OwnedWindows.Cast<Window>().ToArray()) dialog.Close();
            owner.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void PumpMemoryOwnershipTask(Task operation)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (!operation.IsCompleted && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Require(operation.IsCompleted, "The memory operation did not finish within the controlled fixture budget.");
        operation.GetAwaiter().GetResult();
    }
}
