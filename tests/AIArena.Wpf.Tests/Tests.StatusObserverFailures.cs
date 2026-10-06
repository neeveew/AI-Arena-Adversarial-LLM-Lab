using System.IO;
using System.Windows.Controls;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void StatusObserverFailuresPreserveReceiptsAndLaterSubscribers()
    {
        var center = new ApplicationStatusCenter();
        var observed = new List<ApplicationStatusState>();
        center.Changed += (_, _) => throw new InvalidOperationException(PrivateProjectionFailure);
        center.Changed += (_, args) =>
        {
            var run = args.Snapshot.VisibleEntries.FirstOrDefault(entry => entry.Key == "fixture.run");
            if (run is not null) observed.Add(run.State);
        };
        ApplicationStatusReceipt receipt = default;
        Exception? escaped = null;
        try { receipt = center.Begin("fixture.run", "Fixture", "Requested work."); }
        catch (Exception exception) { escaped = exception; }
        Require(escaped is null && !receipt.IsEmpty && observed.Contains(ApplicationStatusState.Running),
            "A Changed observer exception prevented receipt return or delivery to a later subscriber.");
        Require(center.Update(receipt, "Progress.") && center.Complete(receipt, "Saved result.")
                && center.VisibleEntries.Single(entry => entry.Key == "fixture.run").State == ApplicationStatusState.Succeeded
                && observed.Contains(ApplicationStatusState.Succeeded) && !center.VisibleEntries.Any(entry => entry.IsActive),
            "A failing observer changed an owned operation's terminal state.");
        var warnings = center.VisibleEntries.Where(entry => entry.State == ApplicationStatusState.Warning).ToArray();
        Require(warnings.Length == 1 && warnings[0].RepeatCount >= 3
                && warnings[0].Summary.Contains("Code: AA-", StringComparison.Ordinal)
                && !warnings[0].Summary.Contains("sk-test-secret", StringComparison.Ordinal)
                && !warnings[0].Detail.Contains("private", StringComparison.OrdinalIgnoreCase),
            "Status observer failures lacked a bounded coalesced privacy-safe warning.");
    }

    static void StatusObserverWarningDeliveryCannotRecurseOrEscape()
    {
        var center = new ApplicationStatusCenter();
        var calls = 0;
        center.Changed += (_, _) => { calls++; throw new IOException(PrivateProjectionFailure); };
        center.Changed += (_, args) =>
        {
            calls++;
            if (args.Snapshot.VisibleEntries.Any(entry => entry.State == ApplicationStatusState.Warning))
                throw new InvalidOperationException(PrivateProjectionFailure);
        };
        ApplicationStatusReceipt receipt = default;
        Exception? escaped = null;
        try { receipt = center.Begin("fixture.run", "Fixture", "Work requested."); }
        catch (Exception exception) { escaped = exception; }
        Require(escaped is null && !receipt.IsEmpty && calls is >= 2 and <= 4
                && center.VisibleEntries.Count(entry => entry.State == ApplicationStatusState.Warning) == 1,
            "Reporting a subscriber failure recursively invoked faulting listeners or escaped Begin.");
    }

    static void CollaborateBeginObserverFailureCannotStrandRun()
    {
        foreach (var virtualized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("status-observer-begin");
                var store = new PostSaveCollaborateHistoryStore(Path.Combine(root, "history.json"), () => { });
                var client = new FixedCollaborateModelClient(PostSaveAnswer);
                var prompt = new TextBox();
                var status = new TextBlock();
                var center = new ApplicationStatusCenter();
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                var coordinator = CreateLifecycleCollaborate(client, prompt, status, panel, center, "fast", store);
                coordinator.Initialize();
                center.Changed += (_, args) =>
                {
                    if (args.Snapshot.VisibleEntries.Any(entry => entry.Key == "collaborate.run" && entry.IsActive))
                        throw new InvalidOperationException(PrivateProjectionFailure);
                };
                try
                {
                    prompt.Text = "Finish one run despite a broken status view.";
                    PumpDocumentImportTask(coordinator.SendAsync());
                    var saved = store.Load().Single().Exchanges.Single();
                    Require(client.CompleteCalls == 1 && store.SaveCalls == 1 && saved.Answer == PostSaveAnswer && !saved.Interrupted
                            && prompt.Text.Length == 0 && prompt.IsEnabled && !coordinator.IsRunning,
                        "A Begin observer failure replaced requested work with a saved error or stranded its controls.");
                    Require(center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Succeeded
                            && !center.VisibleEntries.Any(entry => entry.IsActive)
                            && center.VisibleEntries.Any(entry => entry.State == ApplicationStatusState.Warning),
                        "The run finished without terminating its status receipt or recording the UI warning.");
                }
                finally { coordinator.Stop(); DeleteDraftTestRoot(root); }
            }));
    }

    static void StatusObserverFailuresPreserveEveryOwnedTerminalOutcome()
    {
        foreach (var outcome in new[] { ApplicationStatusState.Succeeded, ApplicationStatusState.Failed,
            ApplicationStatusState.Cancelled, ApplicationStatusState.Unconfirmed, ApplicationStatusState.Blocked })
        {
            var center = new ApplicationStatusCenter();
            center.Changed += (_, _) => throw new InvalidOperationException(PrivateProjectionFailure);
            var laterStates = new List<ApplicationStatusState>();
            center.Changed += (_, args) => laterStates.Add(args.Snapshot.History.First(entry => entry.Key == "fixture.run").State);
            var receipt = center.Begin("fixture.run", "Fixture", "Requested operation.");
            var changed = outcome switch
            {
                ApplicationStatusState.Succeeded => center.Complete(receipt, "Completed."),
                ApplicationStatusState.Failed => center.Fail(receipt, "Failed."),
                ApplicationStatusState.Cancelled => center.Cancel(receipt, "Stopped."),
                ApplicationStatusState.Unconfirmed => center.MarkUnconfirmed(receipt, "Unconfirmed."),
                _ => center.Fail(receipt, "Blocked.", blocked: true)
            };
            Require(changed && laterStates.Contains(outcome)
                    && center.History.First(entry => entry.Key == "fixture.run").State == outcome
                    && !center.VisibleEntries.Any(entry => entry.IsActive)
                    && !center.Update(receipt, "Late callback."),
                "A broken status view altered a terminal outcome or allowed late progress to revive it.");
        }
    }

    static void AgentBeginObserverFailureCannotStrandRun()
    {
        foreach (var virtualized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("agent-status-observer-begin");
                var workspace = Path.Combine(root, "workspace");
                Directory.CreateDirectory(workspace);
                var client = new LifecycleModelClient();
                var prompt = new TextBox();
                var send = new Button();
                var stop = new Button();
                var center = new ApplicationStatusCenter();
                var store = new WpfSettingsStore(Path.Combine(root, "settings.json"));
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                using var agent = CreateLifecycleAgent(workspace, client, prompt, new TextBlock(), send, stop, panel, center, store.Save);
                try
                {
                    agent.Initialize();
                    center.Changed += (_, args) =>
                    {
                        if (args.Snapshot.VisibleEntries.Any(entry => entry.Key == "agent.run" && entry.IsActive))
                            throw new InvalidOperationException(PrivateProjectionFailure);
                    };
                    prompt.Text = "Explain this workspace once.";
                    PumpDocumentImportTask(agent.DebugSendAsync());
                    var saved = store.Load();
                    Require(client.Calls == 1 && prompt.Text.Length == 0 && send.IsEnabled && !stop.IsEnabled
                            && saved.AgentWorkspaceMessages.Any(message => message.RoleId == "builder" && message.Body.Contains("completed response", StringComparison.Ordinal)),
                        "A Begin status observer stopped the Agent response, saving, draft transition, or control cleanup.");
                    Require(center.History.First(entry => entry.Key == "agent.run").State == ApplicationStatusState.Succeeded
                            && !center.VisibleEntries.Any(entry => entry.IsActive)
                            && center.VisibleEntries.Any(entry => entry.Key == ApplicationStatusCenter.ObserverWarningKey),
                        "An Agent run lost its status ownership when a subscriber failed.");
                }
                finally { agent.Dispose(); DeleteDraftTestRoot(root); }
            }));
    }

    static void StatusObserverWarningReachesBoundStatusControl()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var center = new ApplicationStatusCenter();
            center.Changed += (_, _) => throw new InvalidOperationException(PrivateProjectionFailure);
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                control.Bind(center);
                var receipt = center.Begin("fixture.run", "Collaborate", "Requested operation.");
                Require(center.Complete(receipt, "Answer saved."), "The hosted status control fixture did not complete.");
                DrainUniversalStatusInput();
                Require(control.CompactRows.Any(row => row.StateText == "Warning" && row.Summary.Contains("status view", StringComparison.Ordinal))
                        && control.CompactRows.Any(row => row.StateText == "Succeeded" && row.Summary == "Answer saved.")
                        && control.CompactRows.All(row => !row.Summary.Contains("sk-test-secret", StringComparison.Ordinal)),
                    "A failing earlier subscriber hid the warning or successful result from the bound status card.");
                host.Content = null;
                if (control.Parent is Panel previousParent) previousParent.Children.Remove(control);
                var panel = new StackPanel { Width = 320 };
                panel.Children.Add(control);
                CaptureWorkspaceConversationPreview(panel, control, "status-observer-warning.png");
                panel.Children.Clear();
            }
            finally { host.Close(); }
        }));
    }
}
