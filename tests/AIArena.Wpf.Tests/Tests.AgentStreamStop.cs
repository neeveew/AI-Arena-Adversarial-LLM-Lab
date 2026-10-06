using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private const string StopStreamPrefix = "Accepted partial answer.\n```powershell\nWrite-Output 'partial'\n```";

    static void AgentStopRetainsAcceptedStreamAndRejectsLateText()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var beforeUi in new[] { false, true })
        foreach (var ignoresCancellation in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("agent-stream-stop");
                var workspace = Path.Combine(root, "workspace");
                Directory.CreateDirectory(workspace);
                var client = new StoppableAgentStreamClient { IgnoreCancellation = ignoresCancellation };
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                var prompt = new TextBox();
                var send = new Button();
                var stop = new Button();
                var center = new ApplicationStatusCenter();
                var resources = new Border();
                AttachArenaPresentationResources(resources);
                ApplyExperimentSurfaceTheme(resources, ThemePalette.BuiltIn.Single(theme => theme.Id == "dark-blue"));
                var store = new WpfSettingsStore(Path.Combine(workspace, "retained-settings.json"));
                WpfSettings? persisted = null;
                using var agent = CreateLifecycleAgent(workspace, client, prompt, new TextBlock(), send, stop, panel,
                    center, settings => { store.Save(settings); persisted = store.Load(); }, stream: true,
                    resourceBrush: key => resources.TryFindResource(key) as Brush ?? AccentResourceBrush(key));
                Task? run = null;
                try
                {
                    agent.Initialize();
                    prompt.Text = "Keep this stopped-stream draft.";
                    run = agent.DebugSendAsync();
                    PumpDocumentImportTask(client.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (!beforeUi)
                    {
                        PumpLifecycleUi();
                        if (panel is VirtualizingConversationPanel virtualPanel) virtualPanel.ApplyViewportForTest(0, 1200, 800);
                        Require(LifecycleText(panel).Contains("Accepted partial answer.", StringComparison.Ordinal),
                            "The fixture did not expose its accepted stream before Stop.");
                    }
                    agent.ControlStop();
                    client.ReportLate();
                    client.Release.TrySetResult();
                    PumpDocumentImportTask(run);
                    PumpLifecycleUi();
                    if (panel is VirtualizingConversationPanel finalPanel) finalPanel.ApplyViewportForTest(0, 1200, 800);
                    Require(client.Calls == 1 && prompt.Text == "Keep this stopped-stream draft."
                            && center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Cancelled,
                        "Stopping a streamed Agent call consumed its draft, retried, or reported success.");
                    Require(persisted is not null && persisted.AgentWorkspaceMessages.Any(message =>
                            message.RoleId == "builder" && message.Body.Contains(StopStreamPrefix, StringComparison.Ordinal)
                            && message.Kind != "Agent")
                            && LifecycleText(panel).Contains("Accepted partial answer.", StringComparison.Ordinal)
                            && !persisted.AgentWorkspaceMessages.Any(message => message.Body.Contains("LATE_AFTER_STOP", StringComparison.Ordinal))
                            && !agent.DebugCommandRunEnabled && agent.DebugPhaseState("builder") != "Running",
                        "Stop erased accepted public text, retained late output, promoted partial commands, or revived the Writing phase.");
                    var restored = AgentWorkspaceConversationStore.RestoreMessages(persisted!.AgentWorkspaceMessages,
                        persisted.AgentWorkspaceSessionWorkspacePath, workspace, DateTimeOffset.UtcNow);
                    Require(restored.Any(message => message.RoleId == "builder" && message.Body.Contains(StopStreamPrefix, StringComparison.Ordinal)),
                        "Stopped public text did not survive conversation restoration.");
                    if (!beforeUi && !ignoresCancellation)
                        CaptureWorkspaceConversationPreview(panel, resources, virtualized ? "stopped-virtualized.png" : "stopped-panel.png");
                }
                finally
                {
                    client.Release.TrySetResult();
                    agent.ControlStop();
                    if (run is not null) PumpDocumentImportTask(run);
                    agent.Dispose();
                    DeleteDraftTestRoot(root);
                }
            }));
    }

    private static void CaptureWorkspaceConversationPreview(Panel panel, FrameworkElement resources, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("AIARENA_CONVERSATION_PREVIEW_DIR")
            ?? Environment.GetEnvironmentVariable("AIARENA_AGENT_STOP_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var window = new Window
        {
            Width = 820, Height = 650, ShowInTaskbar = false,
            Background = resources.TryFindResource("AppBackgroundBrush") as Brush,
            Content = new ScrollViewer { Content = panel, Margin = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        AttachArenaPresentationResources(window);
        ApplyExperimentSurfaceTheme(window, ThemePalette.BuiltIn.Single(theme => theme.Id == "dark-blue"));
        try
        {
            window.Show();
            PumpLifecycleUi();
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, filename));
            encoder.Save(stream);
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    static void AgentInterruptedStreamRetainsTextWhenProviderThrows()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = DraftTestRoot("agent-stream-throw");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var client = new StoppableAgentStreamClient { ThrowAfterRelease = true };
            WpfSettings? persisted = null;
            var prompt = new TextBox();
            using var agent = CreateLifecycleAgent(workspace, client, prompt, new TextBlock(), new Button(), new Button(),
                new StackPanel(), new ApplicationStatusCenter(), settings => persisted = settings, stream: true);
            try
            {
                agent.Initialize();
                prompt.Text = "Keep this interrupted draft.";
                var run = agent.DebugSendAsync();
                PumpDocumentImportTask(client.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                client.Release.TrySetResult();
                PumpDocumentImportTask(run);
                client.ReportLate();
                PumpLifecycleUi();
                Require(client.Calls == 1 && persisted is not null && persisted.AgentWorkspaceMessages.Any(message =>
                        message.RoleId == "builder" && message.Body.Contains(StopStreamPrefix, StringComparison.Ordinal)
                        && message.Kind != "Agent") && !agent.DebugCommandRunEnabled
                        && prompt.Text == "Keep this interrupted draft.",
                    "A thrown provider failure erased the accepted stream or promoted its unfinished command.");
            }
            finally
            {
                client.Release.TrySetResult();
                agent.Dispose();
                DeleteDraftTestRoot(root);
            }
        }));
    }

    private sealed class StoppableAgentStreamClient : IModelProviderClient, IStreamingModelProviderClient
    {
        internal bool IgnoreCancellation { get; init; }
        internal bool ThrowAfterRelease { get; init; }
        internal int Calls { get; private set; }
        internal TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<string>? progress;
        internal void ReportLate() => progress?.Report("LATE_AFTER_STOP");
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Streaming fixture must use its streaming path.");
        public async Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            this.progress = progress;
            progress?.Report(StopStreamPrefix);
            Reported.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(cancellationToken);
            if (ThrowAfterRelease) throw new InvalidOperationException("fixture provider failed after public output");
            return new ModelCompletionResult(true, config.BaseUrl, config.Model, StopStreamPrefix + " LATE_AFTER_STOP", "",
                12, 10, 20, 30, "", DateTimeOffset.UtcNow);
        }
    }
}
