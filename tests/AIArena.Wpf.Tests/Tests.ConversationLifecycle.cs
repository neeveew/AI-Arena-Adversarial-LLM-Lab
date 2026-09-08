using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static void AgentConversationSaveFailuresRestoreComposer()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = DraftTestRoot("conversation-save-failure");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            try
            {
                foreach (var failingWrite in new[] { 1, 2 })
                {
                    var armed = false;
                    var writes = 0;
                    var prompt = new TextBox();
                    var status = new TextBlock();
                    var send = new Button();
                    var stop = new Button();
                    var center = new ApplicationStatusCenter();
                    var client = new LifecycleModelClient();
                    var store = new ComposerDraftStore(Path.Combine(root, $"draft-{failingWrite}.dat"), debounceDelay: TimeSpan.FromHours(1));
                    using var coordinator = CreateLifecycleAgent(workspace, client, prompt, status, send, stop,
                        new VirtualizingConversationPanel(), center, _ =>
                        {
                            if (armed && ++writes >= failingWrite)
                            {
                                throw new IOException("private storage sentinel");
                            }
                        }, store);
                    coordinator.Initialize();
                    prompt.Text = "Explain this workspace.";
                    armed = true;
                    PumpDocumentImportTask(coordinator.DebugSendAsync());
                    Require(client.Calls == 0, "A failed initial save must stop before provider work.");
                    Require(send.IsEnabled && !stop.IsEnabled && prompt.IsEnabled,
                        "Every initial save failure must restore Agent controls.");
                    Require(prompt.Text == "Explain this workspace."
                            && store.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == prompt.Text,
                        "Failed initial persistence must retain the exact visible and stored draft.");
                    Require(status.Text.Contains("AA-AGENT-IO", StringComparison.Ordinal)
                            && !status.Text.Contains("private storage sentinel", StringComparison.Ordinal),
                        "Repeated error-report save failure must remain bounded and privacy-safe.");
                    Require(center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Failed,
                        "A failed initial save must terminate its owned run receipt.");
                    armed = false;
                    PumpDocumentImportTask(coordinator.DebugSendAsync());
                    Require(client.Calls == 1 && prompt.Text.Length == 0,
                        "Agent must accept a successful retry after the storage failure is repaired.");
                    store.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var failStorage = false;
                var failureClient = new LifecycleModelClient { ThrowOnComplete = true };
                failureClient.OnStarted = () => failStorage = true;
                var failurePrompt = new TextBox();
                var failureStatus = new TextBlock();
                var failureSend = new Button();
                var failureStop = new Button();
                using var failure = CreateLifecycleAgent(workspace, failureClient, failurePrompt, failureStatus,
                    failureSend, failureStop, new VirtualizingConversationPanel(), new ApplicationStatusCenter(),
                    _ => { if (failStorage) throw new IOException("private catch save sentinel"); });
                failure.Initialize();
                failurePrompt.Text = "Explain the workspace.";
                PumpDocumentImportTask(failure.DebugSendAsync());
                Require(failureSend.IsEnabled && !failureStop.IsEnabled && failurePrompt.Text == "Explain the workspace.",
                    "Failure while saving a provider error must not escape cleanup or consume the draft.");
                Require(failureStatus.Text.Contains("AA-AGENT-UNEXPECTED", StringComparison.Ordinal)
                        && failureStatus.Text.Contains("AA-AGENT-IO", StringComparison.Ordinal)
                        && !failureStatus.Text.Contains("sentinel", StringComparison.Ordinal),
                    "The provider failure and secondary save failure must both use safe support codes.");
            }
            finally
            {
                DeleteDraftTestRoot(root);
            }
        }));
    }

    private static void AgentThemeRefreshPreservesLiveResponseAndStatus()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = DraftTestRoot("agent-live-theme");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var client = new LifecycleModelClient { HoldFromCall = 1 };
            var panel = new VirtualizingConversationPanel();
            var prompt = new TextBox();
            var center = new ApplicationStatusCenter();
            using var coordinator = CreateLifecycleAgent(workspace, client, prompt, new TextBlock(), new Button(),
                new Button(), panel, center, _ => { }, stream: true);
            try
            {
                coordinator.Initialize();
                prompt.Text = "Explain this workspace.";
                var send = coordinator.DebugSendAsync();
                PumpDocumentImportTask(client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                PumpLifecycleUi();
                panel.ApplyViewportForTest(0, 900, 650);
                var liveKey = panel.RealizedKeys.Single(key => key.ToString()!.StartsWith("agent-live-", StringComparison.Ordinal));
                var liveElement = panel.GetRealizedElement(liveKey);
                var keys = panel.RealizedKeys.ToArray();
                var pending = panel.PendingNewMessageCount;
                var receipt = center.VisibleEntries.Single(entry => entry.Key == "agent.run");
                coordinator.RefreshTheme();
                Require(panel.RealizedKeys.SequenceEqual(keys) && ReferenceEquals(panel.GetRealizedElement(liveKey), liveElement),
                    "Theme refresh must preserve Agent's live row and its existing stream target.");
                Require(panel.PendingNewMessageCount == pending, "Theme refresh must retain unread conversation state.");
                client.PushPartial(" after theme");
                PumpLifecycleUi();
                Require(LifecycleText(liveElement!).Contains("partial response", StringComparison.Ordinal),
                    "The preserved live response must remain visible after theme refresh.");
                coordinator.Clear();
                var stillRunning = center.VisibleEntries.Single(entry => entry.Key == "agent.run");
                Require(stillRunning.IsActive && stillRunning.Generation == receipt.Generation,
                    "An unrelated Agent notice must not complete or replace the live run receipt.");
                Require(center.VisibleEntries.Any(entry => entry.Key == "agent.notice"), "Incidental status must use a separate notice key.");
                client.Release.TrySetResult();
                PumpDocumentImportTask(send);
                Require(coordinator.DebugLastMessageBody.Contains("completed response", StringComparison.Ordinal)
                        && prompt.Text.Length == 0,
                    "The refreshed run must deliver its final response and consume only the successful draft.");
                Require(center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Succeeded,
                    "Only actual Agent completion should finish the owned receipt.");
            }
            finally
            {
                client.Release.TrySetResult();
                coordinator.Dispose();
                DeleteDraftTestRoot(root);
            }
        }));
    }

    private static void CollaborateThemeRefreshPreservesPendingRowsAndStatus()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var hasHistory in new[] { false, true })
            {
                var client = new LifecycleModelClient();
                var panel = new VirtualizingConversationPanel();
                var prompt = new TextBox();
                var center = new ApplicationStatusCenter();
                var coordinator = CreateLifecycleCollaborate(client, prompt, new TextBlock(), panel, center,
                    hasHistory ? "fast" : "team");
                coordinator.Initialize();
                if (hasHistory)
                {
                    prompt.Text = "An earlier conversation.";
                    PumpDocumentImportTask(coordinator.SendAsync());
                }
                client.HoldFromCall = 2;
                prompt.Text = "Explain the next step.";
                var send = coordinator.SendAsync();
                PumpDocumentImportTask(client.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                panel.ApplyViewportForTest(0, 1400, 650);
                var keys = panel.RealizedKeys.ToArray();
                var liveKey = keys.Single(key => key.ToString()!.StartsWith("collaborate-live-", StringComparison.Ordinal)
                    && key.ToString()!.EndsWith("-assistant", StringComparison.Ordinal));
                var liveElement = panel.GetRealizedElement(liveKey);
                var receipt = center.VisibleEntries.Single(entry => entry.Key == "collaborate.run");
                var contentBeforeTheme = LifecycleText(liveElement!);
                if (!hasHistory)
                {
                    Require(contentBeforeTheme.Contains("completed response", StringComparison.Ordinal),
                        $"The fixture must have a completed trace before theme refresh (calls: {client.Calls}; text: {contentBeforeTheme}).");
                }
                coordinator.RefreshTheme();
                Require(panel.RealizedKeys.SequenceEqual(keys) && ReferenceEquals(panel.GetRealizedElement(liveKey), liveElement),
                    "Theme refresh must preserve Collaborate's pending answer and trace controls with or without saved history.");
                if (!hasHistory)
                {
                    Require(LifecycleText(liveElement!) == contentBeforeTheme,
                        "Theme refresh must preserve the exact already-rendered trace and pending response text.");
                }
                coordinator.ExportCurrentConversation(new Window());
                var stillRunning = center.VisibleEntries.Single(entry => entry.Key == "collaborate.run");
                Require(stillRunning.IsActive && stillRunning.Generation == receipt.Generation,
                    "An in-run export notice must not finish or replace Collaborate's receipt.");
                client.Release.TrySetResult();
                PumpDocumentImportTask(send);
                Require(center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Succeeded
                        && prompt.Text.Length == 0,
                    "Only a successfully saved Collaborate result should complete its receipt and consume the draft.");
            }
        }));
    }

    private static void ConversationCancellationRetainsDraftAndTypedStatus()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = DraftTestRoot("conversation-cancellation");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            try
            {
                var agentClient = new LifecycleModelClient { HoldFromCall = 1 };
                var agentPrompt = new TextBox();
                var agentCenter = new ApplicationStatusCenter();
                using var agent = CreateLifecycleAgent(workspace, agentClient, agentPrompt, new TextBlock(), new Button(),
                    new Button(), new VirtualizingConversationPanel(), agentCenter, _ => { });
                agent.Initialize();
                agentPrompt.Text = "Keep this Agent draft.";
                var agentSend = agent.DebugSendAsync();
                PumpDocumentImportTask(agentClient.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                agent.ControlStop();
                PumpDocumentImportTask(agentSend);
                Require(agentPrompt.Text == "Keep this Agent draft."
                        && agentCenter.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Cancelled,
                    "Stopping Agent must preserve the draft and cancel only its owned receipt.");

                var collaborateClient = new LifecycleModelClient { HoldFromCall = 1 };
                var collaboratePrompt = new TextBox();
                var collaborateCenter = new ApplicationStatusCenter();
                var collaborate = CreateLifecycleCollaborate(collaborateClient, collaboratePrompt, new TextBlock(),
                    new VirtualizingConversationPanel(), collaborateCenter, "fast");
                collaborate.Initialize();
                collaboratePrompt.Text = "Keep this Collaborate draft.";
                var collaborateSend = collaborate.SendAsync();
                PumpDocumentImportTask(collaborateClient.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                collaborate.Stop();
                PumpDocumentImportTask(collaborateSend);
                Require(collaboratePrompt.Text == "Keep this Collaborate draft."
                        && collaborateCenter.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Cancelled,
                    "Stopping Collaborate must preserve the draft and cancel only its owned receipt.");
            }
            finally
            {
                DeleteDraftTestRoot(root);
            }
        }));
    }

    private static void PumpLifecycleUi() =>
        PumpDocumentImportTask(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);

    private static string LifecycleText(DependencyObject element)
    {
        var text = element switch
        {
            TextBlock block => new TextRange(block.ContentStart, block.ContentEnd).Text,
            Run run => run.Text,
            _ => ""
        };
        var children = LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>();
        if (element is ContentControl { Content: DependencyObject content })
        {
            // Collapsed expander content remains relevant to the retained trace,
            // even before a detached fixture has materialized its template.
            children = children.Append(content).Distinct();
        }
        foreach (var child in children)
        {
            text += " " + LifecycleText(child);
        }
        return text;
    }

    private sealed class LifecycleModelClient : IModelProviderClient, IStreamingModelProviderClient
    {
        public int Calls { get; private set; }
        public int HoldFromCall { get; set; } = int.MaxValue;
        public bool ThrowOnComplete { get; init; }
        public Action? OnStarted { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<string>? stream;

        public void PushPartial(string text) => stream?.Report(text);

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public async Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            OnStarted?.Invoke();
            Started.TrySetResult();
            if (ThrowOnComplete) throw new InvalidOperationException("private provider sentinel");
            if (Calls >= HoldFromCall)
            {
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new ModelCompletionResult(true, config.BaseUrl, config.Model, "completed response", "", 1, 0, 0, 4, "", DateTimeOffset.Now);
        }

        public Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            stream = progress;
            progress?.Report("partial response");
            return CompleteChatAsync(config, messages, cancellationToken);
        }
    }

    private static AgentWorkspaceCoordinator CreateLifecycleAgent(string workspace, IModelProviderClient client,
        TextBox prompt, TextBlock status, Button send, Button stop, Panel panel, ApplicationStatusCenter center,
        Action<WpfSettings> persist, ComposerDraftStore? store = null, bool stream = false)
    {
        var settings = new WpfSettings
        {
            AgentWorkspacePath = workspace,
            AgentBuilderOnlyDefault = true,
            StreamModelResponses = stream
        };
        var shellPicker = new ComboBox();
        shellPicker.Items.Add(new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" });
        shellPicker.SelectedIndex = 0;
        return new AgentWorkspaceCoordinator(
            owner: new Window(),
            dispatcher: Dispatcher.CurrentDispatcher,
            settingsStore: new WpfSettingsStore(Path.Combine(workspace, "unused-settings.json")),
            settings: () => settings,
            modelClient: client,
            workspacePathText: new TextBox(),
            workspaceBrowseButton: new Button(),
            workspaceApplyButton: new Button(),
            workspaceStatusText: new TextBlock(),
            workspaceBoundaryText: new TextBlock(),
            leftWorkspacePathText: new TextBlock(),
            leftBoundaryText: new TextBlock(),
            leftRoleItems: new StackPanel(),
            topWorkspaceText: new TextBlock(),
            topProviderText: new TextBlock(),
            topModeText: new TextBlock(),
            chatScrollViewer: new ScrollViewer(),
            messageItems: panel,
            promptText: prompt,
            planPromptButton: new Button(),
            breakdownPromptButton: new Button(),
            progressPromptButton: new Button(),
            commandPromptButton: new Button(),
            buildAppPromptButton: new Button(),
            nextStepPromptButton: new Button(),
            verifyPromptButton: new Button(),
            rescueCommandButton: new Button(),
            sendButton: send,
            stopButton: stop,
            clearButton: new Button(),
            promptBudgetText: new TextBlock(),
            statusText: status,
            phaseSummaryText: new TextBlock(),
            phaseItems: new StackPanel(),
            buildEvidenceSummaryText: new TextBlock(),
            buildEvidenceItems: new StackPanel(),
            activityItems: new StackPanel(),
            shellPicker: shellPicker,
            commandText: new TextBox(),
            previewButton: new Button(),
            runButton: new Button(),
            rejectButton: new Button(),
            stopCommandButton: new Button(),
            copyCommandButton: new Button(),
            clearCommandButton: new Button(),
            useHeldCommandButton: new Button(),
            approveAllButton: new Button(),
            approveAllStatusText: new TextBlock(),
            autoContinueButton: new Button(),
            autoContinueStatusText: new TextBlock(),
            approvalText: new TextBlock(),
            riskItems: new StackPanel(),
            outputText: new TextBox(),
            commandStatusText: new TextBlock(),
            commandSourceText: new TextBlock(),
            copyOutputButton: new Button(),
            copyReceiptButton: new Button(),
            workSummaryText: new TextBlock(),
            copyBriefButton: new Button(),
            stageVerifyButton: new Button(),
            commandHistorySummaryText: new TextBlock(),
            commandHistoryItems: new StackPanel(),
            replayLastCommandButton: new Button(),
            copyCommandHistoryButton: new Button(),
            snapshot: () => SnapshotForOverviewTest(true, "fixture-model", "", 0, [], []),
            resourceBrush: AccentResourceBrush,
            setShellStatus: _ => { },
            buildWorkspaceProfileAsync: (_, _) => Task.FromResult("profile"),
            composerDraftStore: store,
            persistSettings: persist,
            operationStatus: new WorkspaceOperationStatus(center, "agent", "Agent", "agent", () => ApplicationStatusIdentity.Empty));
    }

    private static CollaborateCoordinator CreateLifecycleCollaborate(IModelProviderClient client, TextBox prompt,
        TextBlock status, Panel panel, ApplicationStatusCenter center, string mode)
    {
        var modePicker = new ComboBox();
        modePicker.Items.Add(new ComboBoxItem { Content = mode, Tag = mode });
        modePicker.SelectedIndex = 0;
        var roundsPicker = new ComboBox();
        roundsPicker.Items.Add(new ComboBoxItem { Content = "1", Tag = "1" });
        roundsPicker.SelectedIndex = 0;
        return new CollaborateCoordinator(
            modelClient: client,
            dispatcher: Dispatcher.CurrentDispatcher,
            chatScrollViewer: new ScrollViewer(),
            messageItems: panel,
            promptText: prompt,
            planPromptButton: new Button(),
            critiquePromptButton: new Button(),
            shipPromptButton: new Button(),
            explainPromptButton: new Button(),
            promptBudgetText: new TextBlock(),
            contextReceiptButton: new Button(),
            sendButton: new Button(),
            stopButton: new Button(),
            clearButton: new Button(),
            modePicker: modePicker,
            roundsPicker: roundsPicker,
            statusText: status,
            providerText: new TextBlock(),
            topProviderText: new TextBlock(),
            topModeText: new TextBlock(),
            topTeamText: new TextBlock(),
            participantItems: new StackPanel(),
            recentItems: new StackPanel(),
            newChatButton: new Button(),
            providerSettingsButton: new Button(),
            toolDocumentItems: new StackPanel(),
            addDocumentButton: new Button(),
            clearDocumentsButton: new Button(),
            calculatorText: new TextBox(),
            runCalculatorButton: new Button(),
            clearCalculationsButton: new Button(),
            calculationItems: new StackPanel(),
            memoryText: new TextBox(),
            saveMemoryButton: new Button(),
            clearMemoryButton: new Button(),
            memoryItems: new StackPanel(),
            snapshot: () => SnapshotForOverviewTest(true, "fixture-model", "", 0, [], []),
            resourceBrush: AccentResourceBrush,
            setShellStatus: _ => { },
            historyStore: new RecordingCollaborateHistoryStore(),
            operationStatus: new WorkspaceOperationStatus(center, "collaborate", "Collaborate", "collaborate", () => ApplicationStatusIdentity.Empty));
    }
}