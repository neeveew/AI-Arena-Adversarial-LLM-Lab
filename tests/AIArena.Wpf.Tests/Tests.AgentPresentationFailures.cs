using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void AgentCompletedRepliesSurvivePresentationFailures()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var persistent in new[] { false, true })
        foreach (var team in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new AgentPresentationFixture(virtualized);
                fixture.FailBrushAfterModelStarts = true;
                fixture.PersistentBrushFailure = persistent;
                if (team) fixture.Agent.ToggleBuilderOnlyMode();
                fixture.Prompt.Text = "Explain this workspace once.";
                PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
                var roles = team ? 3 : 1;
                var saved = fixture.Store.Load();
                Require(fixture.Client.Calls == roles && fixture.BrushFailures > 0
                        && saved.AgentWorkspaceMessages.Count(message => message.Kind == "Agent") == roles
                        && !saved.AgentWorkspaceMessages.Any(message => message.Kind == "Error")
                        && fixture.Prompt.Text.Length == 0,
                    "A reply presentation failure lost a completed role, invented a model failure, repeated a call, or retained a consumed draft.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Succeeded);
                AssertAgentPresentationWarning(fixture, saved: true);
                fixture.BrushFailureArmed = false;
                if (!persistent)
                {
                    if (fixture.Panel is VirtualizingConversationPanel panel) panel.ApplyViewportForTest(0, 2000, 800);
                    Require(LifecycleText(fixture.Panel).Contains("completed response", StringComparison.Ordinal),
                        "The one-time rendering failure did not recover the completed reply in the final view.");
                    if (!virtualized && !team)
                        CaptureWorkspaceConversationPreview(fixture.Panel, fixture.Resources, "agent-recovered-reply.png");
                }
                fixture.Prompt.Text = "Explain the next tradeoff.";
                PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
                Require(fixture.Client.Calls == roles * 2 && fixture.Prompt.Text.Length == 0,
                    "The recovered Agent rejected the next send or repeated a completed role.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Succeeded);
            }));
    }

    static void AgentCleanupFailureCannotStrandSavedReplies()
    {
        foreach (var virtualized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new AgentPresentationFixture(virtualized);
                fixture.Prompt.IsEnabledChanged += (_, args) =>
                {
                    if (fixture.Client.Calls > 0 && args.NewValue is true)
                        throw new InvalidOperationException(PrivateProjectionFailure);
                };
                fixture.Prompt.Text = "Explain this workspace once.";
                PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
                Require(fixture.Store.Load().AgentWorkspaceMessages.Any(message => message.RoleId == "builder"
                        && message.Body == "completed response") && fixture.Prompt.Text.Length == 0,
                    "A cleanup callback replaced a committed reply or retained its consumed draft.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Succeeded);
                AssertAgentPresentationWarning(fixture, saved: true);
            }));
    }

    static void AgentPrimaryFailuresRemainTruthfulThroughBrokenViews()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var saveFailure in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var client = new LifecycleModelClient { ThrowOnComplete = !saveFailure };
                using var fixture = new AgentPresentationFixture(virtualized, client);
                fixture.FailBrushAfterModelStarts = true;
                fixture.FailReplySave = saveFailure;
                fixture.Prompt.Text = "Retain this failed draft.";
                PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
                var saved = fixture.Store.Load();
                Require(client.Calls == 1 && fixture.Prompt.Text == "Retain this failed draft."
                        && !saved.AgentWorkspaceMessages.Any(message => message.Kind == "Agent")
                        && (!saveFailure || fixture.Status.Text.Contains("AA-AGENT-IO", StringComparison.Ordinal)),
                    "A failed provider or primary reply save was hidden, replayed, or consumed its draft.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Failed);
                AssertAgentPresentationWarning(fixture, saved: !saveFailure);
            }));
    }

    static void AgentStoppedStreamsSurviveBrokenViews()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var failBeforeStream in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var client = new LifecycleModelClient { HoldFromCall = 1 };
                using var fixture = new AgentPresentationFixture(virtualized, client, stream: true);
                fixture.FailBrushAfterModelStarts = true;
                fixture.BrushFailureArmed = failBeforeStream;
                fixture.Prompt.Text = "Retain this stopped draft.";
                var run = fixture.Agent.DebugSendAsync();
                PumpDocumentImportTask(client.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                fixture.Agent.ControlStop();
                PumpDocumentImportTask(run);
                Require(client.Calls == 1 && fixture.Prompt.Text == "Retain this stopped draft."
                        && fixture.Store.Load().AgentWorkspaceMessages.Any(message => message.Kind == "Partial"
                            && message.Body.Contains("partial response", StringComparison.Ordinal)),
                    "A stopped stream lost its accepted output, replayed the model, or consumed its draft through a view failure.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Cancelled);
                AssertAgentPresentationWarning(fixture, saved: true);
            }));
    }

    static void AgentStreamingContinuesWhenLiveCardCreationFails()
    {
        foreach (var virtualized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new AgentPresentationFixture(virtualized, stream: true);
                fixture.FailBrushAfterModelStarts = true;
                fixture.BrushFailureArmed = true;
                fixture.Prompt.Text = "Explain this workspace once.";
                PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
                Require(fixture.Client.Calls == 1 && fixture.Prompt.Text.Length == 0
                        && fixture.Store.Load().AgentWorkspaceMessages.Any(message => message.Kind == "Agent"
                            && message.Body == "completed response"),
                    "A failed live-card render prevented the model call or replaced its completed response.");
                AssertAgentPresentationOutcome(fixture, ApplicationStatusState.Succeeded);
                AssertAgentPresentationWarning(fixture, saved: true);
            }));
    }

    static void AssertAgentPresentationOutcome(AgentPresentationFixture fixture, ApplicationStatusState expected)
    {
        Require(fixture.Prompt.IsEnabled && fixture.Send.IsEnabled && !fixture.Stop.IsEnabled
                && fixture.Center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == expected
                && !fixture.Center.VisibleEntries.Any(entry => entry.IsActive),
            "A presentation failure stranded Agent controls or changed the authoritative operation outcome.");
    }

    static void AssertAgentPresentationWarning(AgentPresentationFixture fixture, bool saved)
    {
        var warning = fixture.Center.VisibleEntries.Single(entry => entry.Key == "agent.notice");
        Require(warning.State == ApplicationStatusState.Warning
                && warning.Summary.Contains("saved", StringComparison.OrdinalIgnoreCase) == saved
                && !warning.Summary.Contains("sk-test-secret", StringComparison.Ordinal)
                && !fixture.Status.Text.Contains("private", StringComparison.OrdinalIgnoreCase),
            "The presentation warning hides a save failure or leaks raw callback details.");
    }

    sealed class AgentPresentationFixture : IDisposable
    {
        private readonly string root;
        internal readonly AgentWorkspaceCoordinator Agent;
        internal readonly LifecycleModelClient Client;
        internal readonly TextBox Prompt = new();
        internal readonly TextBlock Status = new();
        internal readonly Button Send = new();
        internal readonly Button Stop = new();
        internal readonly ApplicationStatusCenter Center = new();
        internal readonly Panel Panel;
        internal readonly WpfSettingsStore Store;
        internal readonly Border Resources = new();
        internal bool FailBrushAfterModelStarts;
        internal bool BrushFailureArmed;
        internal bool PersistentBrushFailure = true;
        internal bool FailReplySave;
        internal int BrushFailures;

        internal AgentPresentationFixture(bool virtualized, LifecycleModelClient? client = null, bool stream = false)
        {
            root = DraftTestRoot("agent-presentation");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Store = new WpfSettingsStore(Path.Combine(root, "settings.json"));
            AttachArenaPresentationResources(Resources);
            ApplyExperimentSurfaceTheme(Resources, ThemePalette.Resolve("dark-blue"));
            Panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
            Client = client ?? new LifecycleModelClient();
            Client.OnStarted = () => BrushFailureArmed = FailBrushAfterModelStarts;
            Agent = CreateLifecycleAgent(workspace, Client, Prompt, Status, Send, Stop, Panel, Center, settings =>
            {
                if (FailReplySave && settings.AgentWorkspaceMessages.Any(message => message.Kind == "Agent"))
                    throw new IOException("fixture reply save failed");
                Store.Save(settings);
            }, stream: stream, resourceBrush: key =>
            {
                if (BrushFailureArmed && (PersistentBrushFailure || BrushFailures == 0))
                {
                    BrushFailures++;
                    throw new InvalidOperationException(PrivateProjectionFailure);
                }
                return Resources.TryFindResource(key) as Brush ?? AccentResourceBrush(key);
            });
            Agent.Initialize();
        }

        public void Dispose()
        {
            BrushFailureArmed = false;
            Agent.Dispose();
            DeleteDraftTestRoot(root);
        }
    }
}
