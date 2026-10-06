using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void AgentProfileQueuedViewFailureKeepsSavedSend()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ProfilePresentationFixture();
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var agent = fixture.Create((_, _) => release.Task);
            agent.Initialize();
            fixture.Prompt.Text = "Explain this workspace once.";
            PumpDocumentImportTask(agent.DebugSendAsync());
            fixture.FailBrush = true;
            release.SetResult("A genuine current workspace profile.");
            agent.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();
            var escaped = CaptureProfileDispatcherFailure(fixture, () =>
            {
                PumpLifecycleUi();
                PumpLifecycleUi();
            });
            Require(escaped.Count == 0 && fixture.Client.Calls == 1
                    && agent.DebugWorkspaceProfile.Contains("genuine", StringComparison.Ordinal)
                    && fixture.Store.Load().AgentWorkspaceMessages.Any(message => message.Kind == "Agent")
                    && fixture.Center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Succeeded
                    && fixture.Prompt.Text.Length == 0 && fixture.Send.IsEnabled && !fixture.Stop.IsEnabled,
                $"A queued profile-view exception escaped or changed a saved send (trace: {escaped.FirstOrDefault()?.StackTrace}).");
            var warning = fixture.Center.VisibleEntries.Single(entry => entry.Key == "agent.notice.profile-view");
            Require(warning.State == ApplicationStatusState.Warning
                    && !warning.Summary.Contains("sk-test-secret", StringComparison.Ordinal)
                    && !warning.Summary.Contains("private", StringComparison.OrdinalIgnoreCase),
                "The background profile warning was missing or exposed private exception details.");
            agent.ControlSetWorkspace(Path.Combine(fixture.Root, "workspace"));
            agent.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();
            PumpLifecycleUi();
            Require(!fixture.Center.VisibleEntries.Any(entry => entry.Key == "agent.notice.profile-view")
                    && agent.DebugWorkspaceProfile.Contains("genuine", StringComparison.Ordinal)
                    && fixture.Client.Calls == 1,
                "A healthy profile repaint kept its stale view warning or started another model call.");
        }));
    }

    static void AgentProfileDirectViewFailurePreservesDiscoveryResult()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ProfilePresentationFixture();
            fixture.FailOnce = true;
            using var agent = fixture.Create((_, _) =>
            {
                fixture.FailBrush = true;
                return Task.FromResult("A genuine current workspace profile.");
            });
            agent.Initialize();
            Require(agent.DebugWorkspaceProfileRefreshTask.IsCompletedSuccessfully
                    && agent.DebugWorkspaceProfile.Contains("genuine", StringComparison.Ordinal)
                    && !agent.DebugWorkspaceProfile.Contains("profile unavailable", StringComparison.OrdinalIgnoreCase)
                    && fixture.BrushFailures == 1,
                "A synchronous view failure replaced successful discovery with an unavailable profile.");
            Require(fixture.Center.VisibleEntries.Any(entry => entry.State == ApplicationStatusState.Warning),
                "A synchronous profile-view failure was silently treated as a discovery failure.");
        }));
    }

    static void AgentProfileNoticeCategoriesCannotAlterRunOwnership()
    {
        var center = new ApplicationStatusCenter();
        var status = new WorkspaceOperationStatus(center, "agent", "Agent", "agent", () => ApplicationStatusIdentity.Empty);
        var receipt = status.Begin("Requested work.");
        status.PublishNotice("View warning.", ApplicationStatusState.Warning, category: "run");
        Require(center.VisibleEntries.Single(entry => entry.Key == "agent.run").IsActive
                && status.Update(receipt, "Still working.") && status.ResolveNotice("run")
                && center.VisibleEntries.Single(entry => entry.Key == "agent.run").IsActive,
            "A categorized notice overwrote or resolved the owned run receipt.");
        Require(status.Complete(receipt, "Completed work."), "The receipt lost ownership after resolving a categorized notice.");
    }

    static void AgentProfileQueuedRefreshSkipsObsoleteOwners()
    {
        foreach (var dispose in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new ProfilePresentationFixture();
                var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                using var agent = fixture.Create((_, _) => ++calls == 1 ? first.Task : second.Task);
                agent.Initialize();
                first.SetResult("First workspace profile.");
                agent.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();
                if (dispose) agent.Dispose();
                else
                {
                    var replacement = Path.Combine(fixture.Root, "replacement");
                    Directory.CreateDirectory(replacement);
                    agent.ControlSetWorkspace(replacement);
                }
                try
                {
                    var brushCalls = fixture.BrushCalls;
                    PumpLifecycleUi();
                    PumpLifecycleUi();
                    Require(fixture.BrushCalls == brushCalls,
                        "An obsolete queued profile projection touched the view after workspace change or disposal.");
                }
                finally
                {
                    second.TrySetResult("Replacement workspace profile.");
                    if (!dispose) agent.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();
                }
            }));
    }

    static List<Exception> CaptureProfileDispatcherFailure(ProfilePresentationFixture fixture, Action run)
    {
        var escaped = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, args) =>
        {
            escaped.Add(args.Exception);
            fixture.FailBrush = false;
            args.Handled = true;
        };
        Dispatcher.CurrentDispatcher.UnhandledException += capture;
        try { run(); }
        finally { Dispatcher.CurrentDispatcher.UnhandledException -= capture; fixture.FailBrush = false; }
        return escaped;
    }

    sealed class ProfilePresentationFixture : IDisposable
    {
        internal readonly string Root = DraftTestRoot("agent-profile-presentation");
        internal readonly TextBox Prompt = new();
        internal readonly Button Send = new();
        internal readonly Button Stop = new();
        internal readonly ApplicationStatusCenter Center = new();
        internal readonly LifecycleModelClient Client = new();
        internal readonly WpfSettingsStore Store;
        internal bool FailBrush;
        internal bool FailOnce;
        internal int BrushCalls;
        internal int BrushFailures;

        internal ProfilePresentationFixture() => Store = new WpfSettingsStore(Path.Combine(Root, "settings.json"));

        internal AgentWorkspaceCoordinator Create(Func<string, CancellationToken, Task<string>> build)
        {
            var workspace = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(workspace);
            var discovery = Task.Run(() => new DotNetWorkspaceIntelligenceService().DiscoverAsync(workspace)).GetAwaiter().GetResult();
            return CreateLifecycleAgent(workspace, Client, Prompt, new TextBlock(), Send, Stop, new StackPanel(), Center,
                Store.Save, resourceBrush: key =>
                {
                    BrushCalls++;
                    if (FailBrush && (!FailOnce || BrushFailures == 0))
                    {
                        BrushFailures++;
                        throw new InvalidOperationException(PrivateProjectionFailure);
                    }
                    return AccentResourceBrush(key);
                }, buildWorkspaceProfileAsync: build, discoverDotNetWorkspaceAsync: (_, _) => Task.FromResult(discovery));
        }

        public void Dispose() { FailBrush = false; DeleteDraftTestRoot(Root); }
    }
}
