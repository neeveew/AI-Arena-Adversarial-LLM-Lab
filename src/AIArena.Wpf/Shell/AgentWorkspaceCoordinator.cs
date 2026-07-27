using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using Microsoft.Win32;
using static AIArena.Wpf.Services.WorkspaceCommandHelpers;

namespace AIArena.Wpf;

internal sealed class AgentWorkspaceCoordinator : IDisposable
{
    private const int MaxActivityItems = 12;
    private const int MaxChatContextItems = 8;
    private const int MaxCommandOutputChars = 16000;
    private const int MaxWorkspaceFilesInReceipt = WorkspaceScannerService.MaxWorkspaceFilesInReceipt;
    internal const int MaxWorkspaceDirectoriesInReceipt = WorkspaceScannerService.MaxWorkspaceDirectoriesInReceipt;
    private const int MaxReceiptPathItems = 8;
    private const int MaxMaterializedFiles = 8;
    private const int MaxMaterializedFileChars = 12000;
    private const int MaxMaterializedTotalChars = 30000;
    private const int MaxAutoContinueSteps = 6;
    private int MaxAutoRescueAttempts => Math.Clamp(settings().AgentAutoRescueAttempts, 0, 5);
    private const int MaxCommandHistoryItems = 8;
    private const int MaxWorkspaceProfileFiles = WorkspaceScannerService.MaxWorkspaceProfileFiles;
    private bool builderOnlyForSession;
    internal const int MaxWorkspaceProfileDirectories = WorkspaceScannerService.MaxWorkspaceProfileDirectories;
    internal const int MaxWorkspaceProfileDirectoryCandidates = WorkspaceScannerService.MaxWorkspaceProfileDirectoryCandidates;
    internal const long MaxWorkspaceProfileTextFileBytes = WorkspaceScannerService.MaxWorkspaceProfileTextFileBytes;

    private static readonly Regex ActionIntentRegex = new(
        @"\b(write|create|make|modify|edit|change|scaffold|build|run|test|verify|repair|implement|generate|add|wire|set\s+up|setup|bootstrap|prototype|website|page|component|site|game|tool|ui|app|application)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly AgentWorkspaceRole[] Roles =
    [
        new("planner", "Planner", "Turns software requests into concrete implementation plans, file targets, risks, and next steps."),
        new("reviewer", "Reviewer", "Finds bugs, missing tests, unsafe commands, and sharper alternatives before work is run."),
        new("builder", "Builder", "Synthesizes the collaboration into an actionable coding plan, code suggestions, and command proposals.")
    ];

    private static readonly AgentWelcomeAction[] WelcomeActions =
    [
        new("Plan a change", "plan"),
        new("Break down a task", "breakdown"),
        new("Build a small app", "build_app")
    ];

    private readonly Window owner;
    private readonly Dispatcher dispatcher;
    private readonly WpfSettingsStore settingsStore;
    private readonly Func<WpfSettings> settings;
    private readonly IModelProviderClient modelClient;
    private readonly TextBox workspacePathText;
    private readonly Button workspaceBrowseButton;
    private readonly Button workspaceApplyButton;
    private readonly TextBlock workspaceStatusText;
    private readonly TextBlock workspaceBoundaryText;
    private readonly TextBlock leftWorkspacePathText;
    private readonly TextBlock leftBoundaryText;
    private readonly StackPanel leftRoleItems;
    private readonly TextBlock topWorkspaceText;
    private readonly TextBlock topProviderText;
    private readonly TextBlock topModeText;
    private readonly ScrollViewer chatScrollViewer;
    private readonly StackPanel messageItems;
    private readonly TextBox promptText;
    private readonly Button planPromptButton;
    private readonly Button breakdownPromptButton;
    private readonly Button progressPromptButton;
    private readonly Button commandPromptButton;
    private readonly Button buildAppPromptButton;
    private readonly Button nextStepPromptButton;
    private readonly Button verifyPromptButton;
    private readonly Button rescueCommandButton;
    private readonly Button sendButton;
    private readonly Button stopButton;
    private readonly Button clearButton;
    private readonly TextBlock promptBudgetText;
    private readonly TextBlock statusText;
    private readonly TextBlock phaseSummaryText;
    private readonly TextBlock runbookMetaText;
    private readonly StackPanel phaseItems;
    private readonly TextBlock buildEvidenceSummaryText;
    private readonly StackPanel buildEvidenceItems;
    private readonly TextBlock outputSummaryText;
    private readonly StackPanel outputItems;
    private readonly StackPanel activityItems;
    private readonly ComboBox shellPicker;
    private readonly TextBox commandText;
    private readonly Button previewButton;
    private readonly Button runButton;
    private readonly Button rejectButton;
    private readonly Button stopCommandButton;
    private readonly Button copyCommandButton;
    private readonly Button clearCommandButton;
    private readonly Button useHeldCommandButton;
    private readonly Button approveAllButton;
    private readonly TextBlock approveAllStatusText;
    private readonly Button autoContinueButton;
    private readonly TextBlock autoContinueStatusText;
    private readonly TextBlock approvalText;
    private readonly Panel riskItems;
    private readonly TextBox outputText;
    private readonly TextBlock commandStatusText;
    private readonly TextBlock commandSourceText;
    private readonly Button copyOutputButton;
    private readonly Button copyReceiptButton;
    private readonly TextBlock workSummaryText;
    private readonly Button copyBriefButton;
    private readonly Button stageVerifyButton;
    private readonly Button stageArtifactButton;
    private readonly Button stageNextButton;
    private readonly TextBlock commandHistorySummaryText;
    private readonly StackPanel commandHistoryItems;
    private readonly Button replayLastCommandButton;
    private readonly Button copyCommandHistoryButton;
    private readonly Func<ArenaViewSnapshot?> snapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<string> setShellStatus;
    private readonly Action<string, string, object?> publishControlEvent;
    private readonly Func<string, CancellationToken, Task<string>> buildWorkspaceProfileAsync;
    private readonly object workspaceProfileSync = new();
    private readonly List<AgentWorkspaceMessage> messages = [];
    private readonly List<AgentStep> latestSteps = [];
    private readonly List<AgentCommandHistoryItem> commandHistory = [];
    private readonly AgentRunbookService runbook = new();

    private CancellationTokenSource? chatCancellation;
    private CancellationTokenSource? commandCancellation;
    private CancellationTokenSource? workspaceProfileCancellation;
    private Task workspaceProfileRefreshTask = Task.CompletedTask;
    private long workspaceProfileRefreshVersion;
    private AgentCommandPreview? pendingPreview;
    private AgentCommandResult? lastCommandResult;
    private AgentWorkspaceFileReceipt? lastFileReceipt;
    private AgentArtifactSuggestion? latestArtifactSuggestion;
    private AgentArtifactSuggestion? stagedArtifactSuggestion;
    private AgentArtifactVerification? latestArtifactVerification;
    private AgentCommandSuggestion? heldCommandSuggestion;
    private readonly Dictionary<string, string> phaseStates = [];
    private string workspacePath = "";
    private bool isRunningChat;
    private bool isRunningCommand;
    private bool suppressCommandPreviewInvalidation;
    private bool currentPromptRequiresCommand;
    private bool autoApproveCommandsForSession;
    private bool autoContinueForSession;
    private bool allowRescueCommandReplacement;
    private bool lastCommandWasArtifactVerification;
    private bool runbookVerificationPending;
    private int autoContinueRemainingSteps;
    private int consecutiveAutoContinueNoChangeResults;
    private int autoRescueAttemptsRemaining;
    private int nextCommandHistoryId = 1;
    private int? activeCommandHistoryId;
    private string commandProposalSource = "Manual command";
    private string lastOperatorPrompt = "";
    private string lastWorkBrief = "";
    private string workspaceProfile = "No workspace profile yet.";
    private string buildEvidenceSummary = "No app-building task yet.";
    private string outputSummary = "No artifacts yet.";
    private bool disposed;

    internal string DebugWorkspacePath => workspacePath;

    internal string DebugCommandText => commandText.Text;

    internal string DebugSelectedShell => SelectedShell();

    internal bool DebugCommandRunEnabled => runButton.IsEnabled;

    internal bool DebugCommandPreviewEnabled => previewButton.IsEnabled;

    internal bool DebugCommandRejectEnabled => rejectButton.IsEnabled;

    internal bool DebugCommandStopEnabled => stopCommandButton.IsEnabled;

    internal bool DebugAutoApproveEnabled => autoApproveCommandsForSession;

    internal string DebugAutoApproveStatus => approveAllStatusText.Text;

    internal bool DebugAutoContinueEnabled => autoContinueForSession;

    internal int DebugAutoContinueRemaining => autoContinueRemainingSteps;

    internal string DebugAutoContinueStatus => autoContinueStatusText.Text;

    internal int DebugAutoRescueRemaining => autoRescueAttemptsRemaining;

    internal int DebugCommandHistoryCount => commandHistory.Count;

    internal string DebugCommandHistorySummary => commandHistorySummaryText.Text;

    internal bool DebugReplayLastCommandEnabled => replayLastCommandButton.IsEnabled;

    internal string DebugCommandHistoryCopyText => BuildCommandHistoryCopyText(commandHistory);

    internal string DebugWorkSummary => workSummaryText.Text;

    internal string DebugLatestWorkBrief => lastWorkBrief;

    internal string DebugWorkspaceProfile => Volatile.Read(ref workspaceProfile);

    internal Task DebugWorkspaceProfileRefreshTask => Volatile.Read(ref workspaceProfileRefreshTask);

    internal string DebugArtifactSuggestion => latestArtifactSuggestion?.Summary ?? "";

    internal string DebugArtifactVerification => latestArtifactVerification?.Summary ?? "";

    internal bool DebugCopyBriefEnabled => copyBriefButton.IsEnabled;

    internal bool DebugStageVerifyEnabled => stageVerifyButton.IsEnabled;

    internal bool DebugStageArtifactEnabled => stageArtifactButton.IsEnabled;

    internal bool DebugStageNextEnabled => stageNextButton.IsEnabled;

    internal string DebugStageNextLabel => stageNextButton.Content?.ToString() ?? "";

    internal string DebugStageNextToolTip => stageNextButton.ToolTip?.ToString() ?? "";

    internal string DebugApprovalText => approvalText.Text;

    internal string DebugCommandSource => commandSourceText.Text;

    internal string DebugTopModeText => topModeText.Text;

    internal string DebugLastMessageKind => messages.Count == 0 ? "" : messages[^1].Kind;

    internal string DebugLastMessageBody => messages.Count == 0 ? "" : messages[^1].Body;

    internal string DebugPhaseSummary => phaseSummaryText.Text;

    internal string DebugRunbookId => runbook.State.RunId;

    internal string DebugRunbookStatus => runbook.State.Status;

    internal int DebugRunbookCheckpointCount => runbook.State.Checkpoints.Count;

    internal string DebugPhaseState(string roleId) => phaseStates.TryGetValue(roleId, out var state) ? state : "";

    internal string DebugBuildEvidenceSummary => buildEvidenceSummaryText.Text;

    internal int DebugBuildEvidenceCount => buildEvidenceItems.Children.Count;

    internal string DebugOutputSummary => outputSummaryText.Text;

    internal int DebugOutputCount => outputItems.Children.Count;

    internal string DebugPromptText => promptText.Text;

    internal AIArenaAgentControlState ControlState => new(
        workspacePath,
        statusText.Text,
        promptText.Text,
        commandText.Text,
        commandSourceText.Text,
        commandStatusText.Text,
        runButton.IsEnabled,
        rejectButton.IsEnabled,
        stopCommandButton.IsEnabled,
        autoApproveCommandsForSession,
        autoContinueForSession,
        autoContinueRemainingSteps,
        buildEvidenceSummaryText.Text,
        lastWorkBrief,
        outputSummaryText.Text,
        latestArtifactSuggestion?.Summary ?? "",
        latestArtifactVerification?.Summary ?? "");

    internal object ControlRunbookState => runbook.ControlState;

    internal string ControlRunbookId => runbook.State.RunId;

    internal string ControlRunbookStatus => runbook.State.Status;

    internal bool ControlAddRunbookCheckpoint(string kind, string summary)
    {
        if (!runbook.HasActiveRun)
        {
            return false;
        }

        runbook.AddCheckpoint(kind, summary, DateTimeOffset.Now);
        PersistRunbook();
        RenderPhases();
        return true;
    }

    internal bool ControlResumeRunbook()
    {
        if (!runbook.HasActiveRun)
        {
            UpdateStatus("No Agent runbook is available to resume.");
            return false;
        }

        var verify = runbook.State.Steps.FirstOrDefault(step => step.Id == "verify");
        var execute = runbook.State.Steps.FirstOrDefault(step => step.Id == "execute");
        if (lastCommandResult is not null && verify is not null && !verify.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            StageVerifyPromptFromBrief();
            return true;
        }

        if (execute is not null && execute.Status is "Blocked" or "Failed")
        {
            if (lastCommandResult is not null)
            {
                StageNextPromptFromResult();
                return true;
            }

            StageRunbookResumePrompt(execute);
            return true;
        }

        var next = runbook.State.Steps.FirstOrDefault(step => step.Status is not "Completed" and not "Skipped");
        if (next is null)
        {
            UpdateStatus("Runbook is already complete.");
            return true;
        }

        StageRunbookResumePrompt(next);
        return true;
    }

    internal void ControlSetWorkspace(string path)
    {
        workspacePathText.Text = path ?? "";
        ApplyWorkspaceFromText(persist: true);
    }

    internal async Task ControlSendAsync(string prompt)
    {
        promptText.Text = prompt ?? "";
        promptText.CaretIndex = promptText.Text.Length;
        await SendAsync();
    }

    internal Task ControlApproveAsync()
    {
        return runButton.IsEnabled ? RunApprovedCommandAsync() : Task.CompletedTask;
    }

    internal void ControlReject()
    {
        if (rejectButton.IsEnabled)
        {
            RejectCommand();
        }
    }

    internal void ControlStop()
    {
        Stop();
    }

    internal void ControlStageNext()
    {
        StageNextPromptFromResult();
    }

    internal void ControlStageVerify()
    {
        StageVerifyPromptFromBrief();
    }

    internal void ControlStageArtifact()
    {
        StageArtifactSuggestionCommand();
    }

    internal void ControlStageCommand(string command, string? shell = null)
    {
        if (!string.IsNullOrWhiteSpace(shell))
        {
            SelectShell(shell);
        }

        pendingPreview = null;
        stagedArtifactSuggestion = null;
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Text = command ?? "";
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        SetCommandSource("Control plane command");
        PreviewCommand();
    }

    internal Task DebugSendAsync()
    {
        return SendAsync();
    }

    internal void DebugStageVerifyPromptFromBrief()
    {
        StageVerifyPromptFromBrief();
    }

    internal void DebugStageNextPromptFromResult()
    {
        StageNextPromptFromResult();
    }

    internal void DebugStageArtifactSuggestionCommand()
    {
        StageArtifactSuggestionCommand();
    }

    internal Task DebugRunApprovedCommandAsync()
    {
        return RunApprovedCommandAsync();
    }

    internal void DebugPreviewCommand()
    {
        PreviewCommand();
    }

    internal void DebugSetLatestArtifactSuggestion(AgentArtifactSuggestion suggestion)
    {
        latestArtifactSuggestion = suggestion;
        stagedArtifactSuggestion = null;
        latestArtifactVerification = null;
        lastCommandWasArtifactVerification = false;
        runbookVerificationPending = false;
        RefreshWorkSummary();
        RefreshBuildEvidence();
    }

    internal void DebugSetCommandRequiredForTest(bool required)
    {
        currentPromptRequiresCommand = required;
        RefreshBuildEvidence();
    }

    internal void DebugSetAutoApproveForSession(bool enabled)
    {
        autoApproveCommandsForSession = enabled;
        autoRescueAttemptsRemaining = enabled ? MaxAutoRescueAttempts : 0;
        RefreshAutoApproveAction();
        RefreshProviderState();
    }

    internal void DebugSetAutoContinueForSession(bool enabled, int remainingSteps = MaxAutoContinueSteps)
    {
        autoContinueForSession = enabled;
        autoContinueRemainingSteps = enabled ? Math.Clamp(remainingSteps, 1, MaxAutoContinueSteps) : 0;
        consecutiveAutoContinueNoChangeResults = 0;
        if (enabled)
        {
            autoApproveCommandsForSession = true;
            autoRescueAttemptsRemaining = MaxAutoRescueAttempts;
        }

        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshProviderState();
    }

    public AgentWorkspaceCoordinator(
        Window owner,
        Dispatcher dispatcher,
        WpfSettingsStore settingsStore,
        Func<WpfSettings> settings,
        IModelProviderClient? modelClient,
        TextBox workspacePathText,
        Button workspaceBrowseButton,
        Button workspaceApplyButton,
        TextBlock workspaceStatusText,
        TextBlock workspaceBoundaryText,
        TextBlock leftWorkspacePathText,
        TextBlock leftBoundaryText,
        StackPanel leftRoleItems,
        TextBlock topWorkspaceText,
        TextBlock topProviderText,
        TextBlock topModeText,
        ScrollViewer chatScrollViewer,
        StackPanel messageItems,
        TextBox promptText,
        Button planPromptButton,
        Button breakdownPromptButton,
        Button progressPromptButton,
        Button commandPromptButton,
        Button buildAppPromptButton,
        Button nextStepPromptButton,
        Button verifyPromptButton,
        Button rescueCommandButton,
        Button sendButton,
        Button stopButton,
        Button clearButton,
        TextBlock promptBudgetText,
        TextBlock statusText,
        TextBlock phaseSummaryText,
        StackPanel phaseItems,
        TextBlock buildEvidenceSummaryText,
        StackPanel buildEvidenceItems,
        StackPanel activityItems,
        ComboBox shellPicker,
        TextBox commandText,
        Button previewButton,
        Button runButton,
        Button rejectButton,
        Button stopCommandButton,
        Button copyCommandButton,
        Button clearCommandButton,
        Button useHeldCommandButton,
        Button approveAllButton,
        TextBlock approveAllStatusText,
        Button autoContinueButton,
        TextBlock autoContinueStatusText,
        TextBlock approvalText,
        Panel riskItems,
        TextBox outputText,
        TextBlock commandStatusText,
        TextBlock commandSourceText,
        Button copyOutputButton,
        Button copyReceiptButton,
        TextBlock workSummaryText,
        Button copyBriefButton,
        Button stageVerifyButton,
        TextBlock commandHistorySummaryText,
        StackPanel commandHistoryItems,
        Button replayLastCommandButton,
        Button copyCommandHistoryButton,
        Func<ArenaViewSnapshot?> snapshot,
        Func<string, Brush> resourceBrush,
        Action<string> setShellStatus,
        Button? stageArtifactButton = null,
        Button? stageNextButton = null,
        TextBlock? outputSummaryText = null,
        StackPanel? outputItems = null,
        Action<string, string, object?>? publishControlEvent = null,
        Func<string, CancellationToken, Task<string>>? buildWorkspaceProfileAsync = null,
        TextBlock? runbookMetaText = null)
    {
        this.owner = owner;
        this.dispatcher = dispatcher;
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.modelClient = modelClient ?? new ModelProviderClient();
        this.workspacePathText = workspacePathText;
        this.workspaceBrowseButton = workspaceBrowseButton;
        this.workspaceApplyButton = workspaceApplyButton;
        this.workspaceStatusText = workspaceStatusText;
        this.workspaceBoundaryText = workspaceBoundaryText;
        this.leftWorkspacePathText = leftWorkspacePathText;
        this.leftBoundaryText = leftBoundaryText;
        this.leftRoleItems = leftRoleItems;
        this.topWorkspaceText = topWorkspaceText;
        this.topProviderText = topProviderText;
        this.topModeText = topModeText;
        this.chatScrollViewer = chatScrollViewer;
        this.messageItems = messageItems;
        this.promptText = promptText;
        this.planPromptButton = planPromptButton;
        this.breakdownPromptButton = breakdownPromptButton;
        this.progressPromptButton = progressPromptButton;
        this.commandPromptButton = commandPromptButton;
        this.buildAppPromptButton = buildAppPromptButton;
        this.nextStepPromptButton = nextStepPromptButton;
        this.verifyPromptButton = verifyPromptButton;
        this.rescueCommandButton = rescueCommandButton;
        this.sendButton = sendButton;
        this.stopButton = stopButton;
        this.clearButton = clearButton;
        this.promptBudgetText = promptBudgetText;
        this.statusText = statusText;
        this.phaseSummaryText = phaseSummaryText;
        this.runbookMetaText = runbookMetaText ?? new TextBlock();
        this.phaseItems = phaseItems;
        this.buildEvidenceSummaryText = buildEvidenceSummaryText;
        this.buildEvidenceItems = buildEvidenceItems;
        this.outputSummaryText = outputSummaryText ?? new TextBlock();
        this.outputItems = outputItems ?? new StackPanel();
        this.activityItems = activityItems;
        this.shellPicker = shellPicker;
        this.commandText = commandText;
        this.previewButton = previewButton;
        this.runButton = runButton;
        this.rejectButton = rejectButton;
        this.stopCommandButton = stopCommandButton;
        this.copyCommandButton = copyCommandButton;
        this.clearCommandButton = clearCommandButton;
        this.useHeldCommandButton = useHeldCommandButton;
        this.approveAllButton = approveAllButton;
        this.approveAllStatusText = approveAllStatusText;
        this.autoContinueButton = autoContinueButton;
        this.autoContinueStatusText = autoContinueStatusText;
        this.approvalText = approvalText;
        this.riskItems = riskItems;
        this.outputText = outputText;
        this.commandStatusText = commandStatusText;
        this.commandSourceText = commandSourceText;
        this.copyOutputButton = copyOutputButton;
        this.copyReceiptButton = copyReceiptButton;
        this.workSummaryText = workSummaryText;
        this.copyBriefButton = copyBriefButton;
        this.stageVerifyButton = stageVerifyButton;
        this.stageArtifactButton = stageArtifactButton ?? new Button();
        this.stageNextButton = stageNextButton ?? new Button();
        this.commandHistorySummaryText = commandHistorySummaryText;
        this.commandHistoryItems = commandHistoryItems;
        this.replayLastCommandButton = replayLastCommandButton;
        this.copyCommandHistoryButton = copyCommandHistoryButton;
        this.snapshot = snapshot;
        this.resourceBrush = resourceBrush;
        this.setShellStatus = setShellStatus;
        this.publishControlEvent = publishControlEvent ?? ((_, _, _) => { });
        this.buildWorkspaceProfileAsync = buildWorkspaceProfileAsync ?? WorkspaceScannerService.BuildWorkspaceProfileAsync;

        this.workspaceBrowseButton.Click += (_, _) => BrowseWorkspace();
        this.workspaceApplyButton.Click += (_, _) => ApplyWorkspaceFromText(persist: true);
        this.promptText.TextChanged += (_, _) => RefreshPromptBudget();
        this.planPromptButton.Click += (_, _) => ApplyPromptTemplate("plan");
        this.breakdownPromptButton.Click += (_, _) => ApplyPromptTemplate("breakdown");
        this.progressPromptButton.Click += (_, _) => ApplyPromptTemplate("progress");
        this.commandPromptButton.Click += (_, _) => ApplyPromptTemplate("command");
        this.buildAppPromptButton.Click += (_, _) => ApplyPromptTemplate("build_app");
        this.nextStepPromptButton.Click += (_, _) => ApplyPromptTemplate("next_step");
        this.verifyPromptButton.Click += (_, _) => ApplyPromptTemplate("verify");
        this.rescueCommandButton.Click += (_, _) => ApplyPromptTemplate("rescue_command");
        this.promptText.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                args.Handled = true;
                await SendAsync();
            }
        };
        this.sendButton.Click += async (_, _) => await SendAsync();
        this.stopButton.Click += (_, _) => Stop();
        this.clearButton.Click += (_, _) => Clear();
        this.commandText.TextChanged += (_, _) =>
        {
            if (!suppressCommandPreviewInvalidation)
            {
                SetCommandSource(string.IsNullOrWhiteSpace(commandText.Text) ? "Manual command" : "Manual edit");
                stagedArtifactSuggestion = null;
                InvalidatePreview("Command changed. Preview again before running.");
            }
        };
        this.shellPicker.SelectionChanged += (_, _) =>
        {
            if (!suppressCommandPreviewInvalidation)
            {
                stagedArtifactSuggestion = null;
                InvalidatePreview("Shell changed. Preview again before running.");
            }
        };
        this.previewButton.Click += (_, _) => PreviewCommand();
        this.runButton.Click += async (_, _) => await RunApprovedCommandAsync();
        this.rejectButton.Click += (_, _) => RejectCommand();
        this.stopCommandButton.Click += (_, _) => StopCommand();
        this.copyCommandButton.Click += (_, _) => CopyCommandProposal();
        this.clearCommandButton.Click += (_, _) => ClearCommandProposal();
        this.useHeldCommandButton.Click += (_, _) => StageHeldCommandProposal();
        this.approveAllButton.Click += (_, _) => ToggleAutoApproveForSession();
        this.autoContinueButton.Click += (_, _) => ToggleAutoContinueForSession();
        this.copyOutputButton.Click += (_, _) => CopyCommandOutput();
        this.copyReceiptButton.Click += (_, _) => CopyFileReceipt();
        this.copyBriefButton.Click += (_, _) => CopyWorkBrief();
        this.stageNextButton.Click += (_, _) => StageNextPromptFromResult();
        this.stageVerifyButton.Click += (_, _) => StageVerifyPromptFromBrief();
        this.stageArtifactButton.Click += (_, _) => StageArtifactSuggestionCommand();
        this.replayLastCommandButton.Click += (_, _) => ReplayLastCommand();
        this.copyCommandHistoryButton.Click += (_, _) => CopyCommandHistory();
        this.commandText.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                PreviewCommand();
                args.Handled = true;
                await Task.CompletedTask;
            }
        };

        AutomationProperties.SetName(this.workspacePathText, "Agent workspace path");
        AutomationProperties.SetHelpText(this.workspacePathText, "Folder where Agent command proposals and approved terminal commands are scoped.");
        AutomationProperties.SetName(this.commandText, "Agent command proposal");
        AutomationProperties.SetHelpText(this.commandText, "Terminal or PowerShell command staged for preview and approval.");
        AutomationProperties.SetName(this.runButton, "Approve and run Agent command");
        AutomationProperties.SetHelpText(this.runButton, "Runs only the command that was previewed for the active workspace.");
        AutomationProperties.SetName(this.stopCommandButton, "Stop Agent command");
        AutomationProperties.SetHelpText(this.stopCommandButton, "Cancels the active Agent terminal command and captures a final receipt.");
        AutomationProperties.SetName(this.copyCommandButton, "Copy Agent command proposal");
        AutomationProperties.SetHelpText(this.copyCommandButton, "Copies the staged Agent command proposal.");
        AutomationProperties.SetName(this.clearCommandButton, "Clear Agent command proposal");
        AutomationProperties.SetHelpText(this.clearCommandButton, "Clears the staged Agent command proposal and resets preview state.");
        AutomationProperties.SetName(this.useHeldCommandButton, "Use held Agent command proposal");
        AutomationProperties.SetHelpText(this.useHeldCommandButton, "Stages the latest Builder command proposal that was held while the command rail was occupied.");
        AutomationProperties.SetName(this.approveAllButton, "Agent command approval mode");
        AutomationProperties.SetHelpText(this.approveAllButton, "Auto-runs only literal commands whose physical workspace boundary can be proven; every other preview still requires explicit approval.");
        AutomationProperties.SetName(this.autoContinueButton, "Auto continue Agent next steps");
        AutomationProperties.SetHelpText(this.autoContinueButton, "Automatically asks for the next Agent command after command output, up to a session budget, while keeping preview blocking active.");
        AutomationProperties.SetName(this.buildAppPromptButton, "Build app prompt");
        AutomationProperties.SetHelpText(this.buildAppPromptButton, "Stages a software creation prompt that asks Builder for an approvable first command.");
        AutomationProperties.SetName(this.nextStepPromptButton, "Next Agent step prompt");
        AutomationProperties.SetHelpText(this.nextStepPromptButton, "Stages a follow-up prompt that reviews terminal output and asks for the next approvable command.");
        AutomationProperties.SetName(this.verifyPromptButton, "Verify Agent work prompt");
        AutomationProperties.SetHelpText(this.verifyPromptButton, "Stages a verification prompt that asks for one build, run, or test command.");
        AutomationProperties.SetName(this.rescueCommandButton, "Rescue Agent command prompt");
        AutomationProperties.SetHelpText(this.rescueCommandButton, "Stages a recovery prompt that requires Builder to return exactly one runnable command proposal.");
        AutomationProperties.SetName(this.copyOutputButton, "Copy Agent command output");
        AutomationProperties.SetHelpText(this.copyOutputButton, "Copies the latest Agent terminal output, including stdout and stderr.");
        AutomationProperties.SetName(this.copyReceiptButton, "Copy Agent file receipt");
        AutomationProperties.SetHelpText(this.copyReceiptButton, "Copies the latest Agent file-change receipt.");
        AutomationProperties.SetName(this.copyBriefButton, "Copy Agent work brief");
        AutomationProperties.SetHelpText(this.copyBriefButton, "Copies a compact handoff brief with task, autonomy, latest command result, changed files, and next action.");
        AutomationProperties.SetName(this.stageNextButton, "Stage Agent next-step prompt");
        AutomationProperties.SetHelpText(this.stageNextButton, "Stages a result-aware follow-up or repair prompt from the latest command output.");
        AutomationProperties.SetName(this.stageVerifyButton, "Stage Agent verification prompt");
        AutomationProperties.SetHelpText(this.stageVerifyButton, "Stages a verification prompt based on the latest Agent command output and file receipt.");
        AutomationProperties.SetName(this.stageArtifactButton, "Stage Agent artifact command");
        AutomationProperties.SetHelpText(this.stageArtifactButton, "Stages the suggested generated-artifact preview command in the approval rail.");
        AutomationProperties.SetName(this.replayLastCommandButton, "Replay last Agent command");
        AutomationProperties.SetHelpText(this.replayLastCommandButton, "Stages the most recent Agent command from history for preview.");
        AutomationProperties.SetName(this.copyCommandHistoryButton, "Copy Agent command history");
        AutomationProperties.SetHelpText(this.copyCommandHistoryButton, "Copies the recent Agent command history with statuses and file-change summaries.");
    }

    public void Initialize()
    {
        RenderRoles();
        workspacePathText.Text = settings().AgentWorkspacePath;
        if (!string.IsNullOrWhiteSpace(workspacePathText.Text))
        {
            ApplyWorkspaceFromText(persist: false);
        }

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            UpdateWorkspaceDisplays("No workspace selected.", "Choose a project folder.");
        }

        builderOnlyForSession = settings().AgentBuilderOnlyDefault;
        runbook.Restore(settings().AgentRunbook, workspacePath, DateTimeOffset.Now);
        runbookVerificationPending = runbook.State.Steps.Any(step => step.Id == "verify" && step.Status == "Waiting")
            && runbook.State.Steps.Any(step => step.Id == "execute" && step.Status == "Completed");
        if (runbook.HasActiveRun)
        {
            RestorePhaseStatesFromRunbook();
            phaseSummaryText.Text = $"Restored {runbook.State.Status.ToLowerInvariant()} runbook.";
            PersistRunbook();
            RenderPhases();
        }
        else
        {
            ResetPhases("Ready for a software task.");
        }

        if (!RestorePersistedConversation())
        {
            RenderEmptyState();
        }

        RefreshPromptBudget();
        SetCommandSource("Manual command");
        SetBuildEvidenceSummary("No app-building task yet.");
        InvalidatePreview("Preview required before any command can run.");
        stopCommandButton.IsEnabled = false;
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshCommandHistory();
        RefreshOutputs();
        RefreshWorkSummary();
        RefreshOutputActions();
        RefreshHeldCommandAction();
        RefreshProviderState();
    }

    public void RefreshTheme()
    {
        RenderRoles();
        RefreshBuildEvidence();
        RefreshCommandHistory();
        RefreshOutputs();
        RefreshWorkSummary();
        RefreshProviderState();
        if (messages.Count == 0)
        {
            RenderEmptyState();
            return;
        }

        messageItems.Children.Clear();
        foreach (var message in messages)
        {
            messageItems.Children.Add(CreateMessageCard(message));
        }
    }

    public void RefreshProviderState()
    {
        var current = snapshot();
        topWorkspaceText.Text = ShortWorkspaceName(workspacePath);
        topProviderText.Text = current is null ? "-" : DisplayModel(current.ProviderModel);
        topModeText.Text = AgentModeLabel();
        RenderRoles();
    }

    public bool BuilderOnlyMode => builderOnlyForSession;

    public bool ToggleBuilderOnlyMode()
    {
        if (isRunningChat)
        {
            UpdateStatus("Wait for the active Agent response before changing team mode.");
            return builderOnlyForSession;
        }

        builderOnlyForSession = !builderOnlyForSession;
        AddCenterMessage(
            builderOnlyForSession ? "Builder-only mode" : "Full team mode",
            builderOnlyForSession
                ? "Planner and Reviewer are skipped for this session; Builder handles each turn directly for faster responses."
                : "Planner and Reviewer run before Builder on each task for deeper planning and review.",
            "Action");
        AddActivity(
            builderOnlyForSession ? "Builder only" : "Full team",
            builderOnlyForSession
                ? "Planner and Reviewer disabled for this session."
                : "Planner and Reviewer enabled for this session.");
        UpdateStatus(builderOnlyForSession ? "Builder-only mode enabled." : "Full team mode enabled.");
        return builderOnlyForSession;
    }

    public void Clear()
    {
        if (isRunningChat || isRunningCommand)
        {
            UpdateStatus("Stop active work before clearing Agent.");
            return;
        }

        builderOnlyForSession = settings().AgentBuilderOnlyDefault;
        messages.Clear();
        latestSteps.Clear();
        commandHistory.Clear();
        activeCommandHistoryId = null;
        nextCommandHistoryId = 1;
        lastCommandResult = null;
        lastFileReceipt = null;
        latestArtifactSuggestion = null;
        stagedArtifactSuggestion = null;
        latestArtifactVerification = null;
        heldCommandSuggestion = null;
        commandCancellation?.Dispose();
        commandCancellation = null;
        currentPromptRequiresCommand = false;
        lastCommandWasArtifactVerification = false;
        autoApproveCommandsForSession = false;
        autoContinueForSession = false;
        autoContinueRemainingSteps = 0;
        consecutiveAutoContinueNoChangeResults = 0;
        autoRescueAttemptsRemaining = 0;
        lastOperatorPrompt = "";
        lastWorkBrief = "";
        runbook.Reset(workspacePath);
        RefreshWorkspaceProfile();
        promptText.Clear();
        outputText.Text = "";
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        SetCommandSource("Manual command");
        InvalidatePreview("Preview required before any command can run.");
        RefreshOutputActions();
        RefreshOutputs();
        RefreshWorkSummary();
        RefreshHeldCommandAction();
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshCommandHistory();
        ResetPhases("Ready for a software task.");
        SetBuildEvidenceSummary("No app-building task yet.");
        PersistConversation();
        RenderEmptyState();
        AddActivity("Reset", "Agent workspace cleared.");
        UpdateStatus("Ready.");
    }

    private void BrowseWorkspace()
    {
        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before changing workspace.");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose Agent workspace",
            InitialDirectory = InitialWorkspaceDirectory()
        };

        if (dialog.ShowDialog(owner) == true)
        {
            workspacePathText.Text = dialog.FolderName;
            ApplyWorkspaceFromText(persist: true);
        }
    }

    private void ApplyWorkspaceFromText(bool persist)
    {
        if (disposed)
        {
            return;
        }

        var normalized = AgentWorkspaceCommand.NormalizeWorkspacePath(workspacePathText.Text, out var error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            workspacePath = "";
            runbook.Reset("");
            runbookVerificationPending = false;
            PersistRunbook();
            CancelWorkspaceProfileRefresh("No workspace profile yet.");
            latestArtifactSuggestion = null;
            stagedArtifactSuggestion = null;
            latestArtifactVerification = null;
            lastCommandWasArtifactVerification = false;
            RefreshOutputs();
            UpdateWorkspaceDisplays(error, "Commands disabled.");
            InvalidatePreview(error);
            autoApproveCommandsForSession = false;
            consecutiveAutoContinueNoChangeResults = 0;
            autoRescueAttemptsRemaining = 0;
            PauseAutoContinue("Workspace missing; Auto Continue was reset.");
            RefreshAutoApproveAction();
            SetBuildEvidenceSummary("Workspace missing; command proposals are disabled.");
            RefreshProviderState();
            return;
        }

        var changedWorkspace = !string.IsNullOrWhiteSpace(workspacePath)
            && !workspacePath.Equals(normalized, StringComparison.OrdinalIgnoreCase);
        workspacePath = normalized;
        RefreshWorkspaceProfile();
        workspacePathText.Text = normalized;
        if (changedWorkspace && autoApproveCommandsForSession)
        {
            autoApproveCommandsForSession = false;
            consecutiveAutoContinueNoChangeResults = 0;
            autoRescueAttemptsRemaining = 0;
            AddActivity("Full Access off", "Workspace changed; session autonomy was reset.");
        }

        if (changedWorkspace)
        {
            runbook.Reset(workspacePath);
            runbookVerificationPending = false;
            PersistRunbook();
            ResetPhases("Workspace changed. Ready for a new software task.");
            latestArtifactSuggestion = null;
            stagedArtifactSuggestion = null;
            latestArtifactVerification = null;
            lastCommandWasArtifactVerification = false;
            RefreshOutputs();
        }

        if (changedWorkspace && autoContinueForSession)
        {
            PauseAutoContinue("Workspace changed; Auto Continue was reset.");
        }

        if (persist)
        {
            var currentSettings = settings();
            currentSettings.AgentWorkspacePath = normalized;
            settingsStore.Save(currentSettings);
        }

        UpdateWorkspaceDisplays(normalized, $"Working dir: {normalized}");
        AddActivity("Workspace", normalized);
        InvalidatePreview("Workspace changed. Preview again before running.");
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        SetBuildEvidenceSummary("Workspace boundary is ready.");
        RefreshProviderState();
    }

    private void RefreshWorkspaceProfile()
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            CancelWorkspaceProfileRefresh("No workspace profile yet.");
            return;
        }

        CancellationTokenSource? previousCancellation;
        CancellationTokenSource cancellation;
        long version;
        var profilePath = workspacePath;
        lock (workspaceProfileSync)
        {
            if (disposed)
            {
                return;
            }

            version = ++workspaceProfileRefreshVersion;
            previousCancellation = workspaceProfileCancellation;
            cancellation = new CancellationTokenSource();
            workspaceProfileCancellation = cancellation;
            Volatile.Write(ref workspaceProfile, "Workspace profile is loading.");
        }

        TryCancel(previousCancellation);
        var refreshTask = RefreshWorkspaceProfileAsync(profilePath, version, cancellation);
        Volatile.Write(ref workspaceProfileRefreshTask, refreshTask);
    }

    private void CancelWorkspaceProfileRefresh(string replacementProfile)
    {
        CancellationTokenSource? cancellation;
        lock (workspaceProfileSync)
        {
            workspaceProfileRefreshVersion++;
            cancellation = workspaceProfileCancellation;
            workspaceProfileCancellation = null;
            Volatile.Write(ref workspaceProfile, replacementProfile);
        }

        TryCancel(cancellation);
    }

    private async Task RefreshWorkspaceProfileAsync(
        string profilePath,
        long version,
        CancellationTokenSource cancellation)
    {
        try
        {
            var profile = await buildWorkspaceProfileAsync(profilePath, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            ApplyWorkspaceProfileResult(
                profilePath,
                version,
                cancellation,
                string.IsNullOrWhiteSpace(profile) ? "Workspace profile unavailable." : profile);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ApplyWorkspaceProfileResult(
                profilePath,
                version,
                cancellation,
                "Workspace profile unavailable; ask Builder for a read-only inspection first.");
        }
        finally
        {
            lock (workspaceProfileSync)
            {
                if (ReferenceEquals(workspaceProfileCancellation, cancellation))
                {
                    workspaceProfileCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void ApplyWorkspaceProfileResult(
        string profilePath,
        long version,
        CancellationTokenSource cancellation,
        string profile)
    {
        lock (workspaceProfileSync)
        {
            if (disposed
                || version != workspaceProfileRefreshVersion
                || !ReferenceEquals(workspaceProfileCancellation, cancellation)
                || !workspacePath.Equals(profilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Volatile.Write(ref workspaceProfile, profile);
        }
    }

    private async Task SendAsync(string? internalPrompt = null)
    {
        if (isRunningChat)
        {
            return;
        }

        var isInternalPrompt = !string.IsNullOrWhiteSpace(internalPrompt);
        var prompt = isInternalPrompt ? internalPrompt!.Trim() : promptText.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (!isInternalPrompt)
            {
                promptText.Focus();
            }

            return;
        }

        if (!isInternalPrompt && TryHandleComposerSlashCommand(prompt))
        {
            return;
        }

        if (!WorkspaceReady())
        {
            workspacePathText.Focus();
            return;
        }

        var current = snapshot();
        if (current is null)
        {
            UpdateStatus("Configure a provider before Agent collaboration.");
            return;
        }

        var missing = MissingConfiguredModelRoles(current);
        if (missing.Count > 0)
        {
            UpdateStatus(missing.Count == 1
                ? $"No model configured for {missing[0]}."
                : $"No model configured for {string.Join(", ", missing)}.");
            return;
        }

        if (!isInternalPrompt && (!runbook.HasActiveRun || !AgentRunbookService.IsGeneratedContinuationPrompt(prompt)))
        {
            runbook.Begin(workspacePath, prompt, builderOnlyForSession, DateTimeOffset.Now);
            runbookVerificationPending = false;
            PersistRunbook();
            publishControlEvent("agent.runbook.started", "Agent runbook started.", runbook.ControlState);
        }
        else if (!runbook.HasActiveRun)
        {
            runbook.Begin(workspacePath, string.IsNullOrWhiteSpace(lastOperatorPrompt) ? prompt : lastOperatorPrompt, builderOnlyForSession, DateTimeOffset.Now);
            PersistRunbook();
        }

        isRunningChat = true;
        currentPromptRequiresCommand = PromptLikelyRequiresCommand(prompt);
        var isRescuePrompt = PromptIsAutoRescue(prompt);
        if (!isRescuePrompt || string.IsNullOrWhiteSpace(lastOperatorPrompt))
        {
            lastOperatorPrompt = prompt;
        }

        if (currentPromptRequiresCommand && !isRescuePrompt)
        {
            autoRescueAttemptsRemaining = MaxAutoRescueAttempts;
        }

        var autoRunAfterChat = false;
        var autoRescueAfterChat = false;
        var internalRescuePromptAfterChat = "";
        var previousRescueCommandReplacement = allowRescueCommandReplacement;
        chatCancellation?.Dispose();
        chatCancellation = new CancellationTokenSource();
        var cancellationToken = chatCancellation.Token;
        SetChatControlsEnabled(false);
        RefreshCommandActionState();
        if (!isInternalPrompt)
        {
            promptText.Clear();
        }

        RefreshProviderState();

        if (messages.Count == 0)
        {
            messageItems.Children.Clear();
        }

        var userMessage = isInternalPrompt
            ? new AgentWorkspaceMessage("system", "Agent Rescue", prompt, "Action", "", DateTimeOffset.Now)
            : new AgentWorkspaceMessage("operator", "Operator", prompt, "User", "", DateTimeOffset.Now);
        messages.Add(userMessage);
        messageItems.Children.Add(CreateMessageCard(userMessage));
        PersistConversation();
        AddActivity(isInternalPrompt ? "Auto Rescue" : "Prompt", isInternalPrompt ? "Builder is being retried for a runnable command." : "Software task sent to Agent team.");
        SetBuildEvidenceSummary(currentPromptRequiresCommand
            ? "App-work evidence started."
            : "Consultation task; command may be optional.");
        ScrollToEnd();

        try
        {
            allowRescueCommandReplacement = isRescuePrompt;
            latestSteps.Clear();
            heldCommandSuggestion = null;
            RefreshHeldCommandAction();
            if (builderOnlyForSession)
            {
                ResetPhases("Builder-only mode: Planner and Reviewer skipped.");
                SetPhase("planner", "Skipped", "Planner skipped; Builder is handling this turn directly.");
                SetPhase("reviewer", "Skipped", "Reviewer skipped; Builder is handling this turn directly.");
                AddActivity("Builder only", "Planner and Reviewer skipped for this Agent turn.");
            }
            else
            {
                ResetPhases("Planner is starting...");
                UpdateStatus("Planner is reading the workspace request...");
                SetPhase("planner", "Running", "Planner is reading the request.");
                var planner = await CompleteRoleAsync(
                    current,
                    Roles[0],
                    BuildRolePrompt(current, Roles[0], prompt, latestSteps),
                    cancellationToken);
                AddStep(planner);

                UpdateStatus("Reviewer is checking risks and tests...");
                SetPhase("reviewer", "Running", "Reviewer is checking risks and tests.");
                var reviewer = await CompleteRoleAsync(
                    current,
                    Roles[1],
                    BuildRolePrompt(current, Roles[1], prompt, latestSteps),
                    cancellationToken);
                AddStep(reviewer);
            }

            UpdateStatus(currentPromptRequiresCommand
                ? "Builder is creating a previewable command..."
                : "Builder is answering the workspace request...");
            SetPhase("builder", "Running", currentPromptRequiresCommand
                ? "Builder is creating a previewable command."
                : "Builder is answering the request.");
            var builder = await CompleteRoleAsync(
                current,
                Roles[2],
                BuildRolePrompt(current, Roles[2], prompt, latestSteps),
                cancellationToken);
            AddStep(builder);

            if (builder.Ok
                && currentPromptRequiresCommand
                && pendingPreview is null
                && heldCommandSuggestion is null
                && string.IsNullOrWhiteSpace(commandText.Text))
            {
                await TryStageCommandViaJsonExtractionAsync(current, prompt, builder, cancellationToken);
            }

            if (latestSteps.All(step => step.Ok) && pendingPreview is not null)
            {
                phaseSummaryText.Text = "Builder staged a command for approval.";
                if (autoApproveCommandsForSession)
                {
                    autoRunAfterChat = true;
                    UpdateStatus("Command proposal staged; Full Access will run it.");
                }
                else
                {
                    UpdateStatus("Command proposal staged for approval.");
                }
            }
            else if (latestSteps.All(step => step.Ok) && currentPromptRequiresCommand)
            {
                var shouldAutoRescue = autoRescueAttemptsRemaining > 0;
                if (!shouldAutoRescue)
                {
                    PauseAutoContinue("No command staged; Auto Continue paused for Rescue.");
                }

                AddCenterMessage(
                    "No command staged",
                    shouldAutoRescue
                        ? "This request sounds like it needs workspace changes, but Builder did not stage a runnable command. The app has not been written yet. Agent is retrying Builder internally for one previewable command."
                        : "This request sounds like it needs workspace changes, but Builder did not stage a runnable command. The app has not been written yet. A Rescue prompt is staged so Builder must return one previewable command.",
                    "Warning");
                AddActivity("Needs command", "Builder completed without a runnable command proposal.");
                if (shouldAutoRescue)
                {
                    runbook.UpdateStep("approval", "Waiting", "Builder output needs Auto Rescue before a command can be approved.", DateTimeOffset.Now);
                    autoRescueAttemptsRemaining--;
                    internalRescuePromptAfterChat = BuildRescueCommandPrompt();
                    autoRescueAfterChat = true;
                    RefreshAutoApproveAction();
                    AddActivity("Auto Rescue", $"{autoRescueAttemptsRemaining.ToString(CultureInfo.InvariantCulture)} retry attempt{(autoRescueAttemptsRemaining == 1 ? "" : "s")} remain.");
                    phaseSummaryText.Text = "Auto Rescue: asking for a runnable command.";
                    SetBuildEvidenceSummary("Auto Rescue is retrying prose-only app output.");
                    UpdateStatus("No command staged. Asking Builder for a command...");
                }
                else
                {
                    runbook.UpdateStep("approval", "Blocked", "Builder returned no runnable command; Rescue is staged.", DateTimeOffset.Now);
                    runbook.AddCheckpoint("needs-command", "Runbook paused because Builder returned no runnable command.", DateTimeOffset.Now);
                    StageRescuePrompt("Builder returned prose without a runnable command.");
                    phaseSummaryText.Text = "Needs command: no runnable Builder proposal.";
                    SetBuildEvidenceSummary("Needs command: Rescue prompt staged.");
                    UpdateStatus("No command staged. Rescue prompt staged.");
                }

                PersistRunbook();
                RenderPhases();
            }
            else
            {
                phaseSummaryText.Text = latestSteps.All(step => step.Ok) ? "Agent loop complete." : "Agent loop completed with warnings.";
                SetBuildEvidenceSummary(latestSteps.All(step => step.Ok) ? "Agent loop complete." : "Agent loop completed with warnings.");
                UpdateStatus(latestSteps.All(step => step.Ok) ? "Ready." : "Agent completed with model warnings.");
                if (runbook.HasActiveRun)
                {
                    if (latestSteps.All(step => step.Ok) && !currentPromptRequiresCommand)
                    {
                        runbook.MarkConsultationCompleted("Planner, Reviewer, and Builder completed without requiring a workspace command.", DateTimeOffset.Now);
                    }
                    else if (!latestSteps.All(step => step.Ok))
                    {
                        runbook.MarkInterrupted("One or more model steps failed.", DateTimeOffset.Now);
                    }

                    PersistRunbook();
                    RenderPhases();
                }
            }
        }
        catch (OperationCanceledException)
        {
            var stopped = new AgentWorkspaceMessage("system", "Agent", "Collaboration stopped.", "Status", "", DateTimeOffset.Now);
            messages.Add(stopped);
            messageItems.Children.Add(CreateMessageCard(stopped));
            PersistConversation();
            AddActivity("Stopped", "Agent collaboration cancelled.");
            phaseSummaryText.Text = "Agent collaboration stopped.";
            SetBuildEvidenceSummary("Agent collaboration stopped.");
            if (runbook.HasActiveRun)
            {
                runbook.MarkInterrupted("Agent collaboration was stopped by the operator.", DateTimeOffset.Now);
                PersistRunbook();
                RenderPhases();
            }

            UpdateStatus("Collaboration stopped.");
        }
        finally
        {
            allowRescueCommandReplacement = previousRescueCommandReplacement;
            RunOnUiThread(() =>
            {
                isRunningChat = false;
                SetChatControlsEnabled(true);
                RefreshCommandActionState();
            });
            chatCancellation?.Dispose();
            chatCancellation = null;
            if (autoRescueAfterChat && !string.IsNullOrWhiteSpace(internalRescuePromptAfterChat))
            {
                await SendAsync(internalRescuePromptAfterChat);
            }
            else if (autoRunAfterChat || (autoApproveCommandsForSession && pendingPreview is not null))
            {
                await TryAutoRunPendingPreviewAsync("Full Access is active for this session.");
            }

            RunOnUiThread(() =>
            {
                RefreshProviderState();
                ScrollToEnd();
            });
        }
    }

    private bool TryHandleComposerSlashCommand(string prompt)
    {
        if (!prompt.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var command = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var normalized = command.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "/help":
            case "/commands":
                promptText.Clear();
                AddCenterMessage("Agent commands", SlashCommandHelp(), "Status");
                AddActivity("Slash /help", "Composer command list shown.");
                UpdateStatus("Agent command help shown.");
                ScrollToEnd();
                return true;
            case "/status":
                promptText.Clear();
                AddCenterMessage("Agent status", BuildSlashStatusReport(), "Status");
                AddActivity("Slash /status", "Session status summarized.");
                UpdateStatus("Agent status summarized.");
                ScrollToEnd();
                return true;
            case "/brief":
                promptText.Clear();
                AddCenterMessage(
                    string.IsNullOrWhiteSpace(lastWorkBrief) ? "No work brief yet" : "Latest work brief",
                    string.IsNullOrWhiteSpace(lastWorkBrief)
                        ? "Run an approved command first. Agent will create a work brief from the command output, file receipt, recent history, and suggested next action."
                        : ShellUiHelpers.Truncate(lastWorkBrief, 2200, ShellUiHelpers.TruncatedNoticeSuffix),
                    "Status");
                AddActivity("Slash /brief", string.IsNullOrWhiteSpace(lastWorkBrief) ? "No work brief available." : "Latest work brief shown.");
                UpdateStatus(string.IsNullOrWhiteSpace(lastWorkBrief) ? "No work brief yet." : "Latest work brief shown.");
                ScrollToEnd();
                return true;
            case "/next":
                promptText.Clear();
                StageNextPromptFromResult();
                ScrollToEnd();
                return true;
            case "/verify":
                promptText.Clear();
                StageVerifyPromptFromBrief();
                ScrollToEnd();
                return true;
            case "/artifact":
            case "/preview":
                promptText.Clear();
                StageArtifactSuggestionCommand();
                ScrollToEnd();
                return true;
            default:
                promptText.Clear();
                AddCenterMessage(
                    "Unknown Agent command",
                    $"`{command}` is not an Agent composer command.\n\n{SlashCommandHelp()}",
                    "Warning");
                AddActivity("Slash command", $"Unknown command: {command}");
                UpdateStatus("Unknown Agent composer command.");
                ScrollToEnd();
                return true;
        }
    }

    private static string SlashCommandHelp()
    {
        return """
        /status - Show workspace, autonomy, preview, output, and next-action state.
        /brief - Show the latest copyable work brief after a command has run.
        /next - Stage a result-aware next, repair, or retry prompt.
        /verify - Stage a verification prompt from the latest work brief.
        /artifact - Stage the latest generated artifact preview or verification command.
        /help - Show this command list.
        """;
    }

    private string BuildSlashStatusReport()
    {
        var lines = new List<string>
        {
            $"Workspace: {CleanBriefValue(ShortWorkspaceName(workspacePath), "not set")}",
            $"Mode: {AgentModeLabel()}",
            $"Autonomy: {FormatAutonomyContext()}",
            $"Command source: {commandProposalSource}",
            $"Preview: {(pendingPreview is null ? "none staged" : "ready for approval")}",
            $"Command status: {commandStatusText.Text}",
            $"Outputs: {outputSummaryText.Text}",
            $"Build evidence: {buildEvidenceSummaryText.Text}"
        };

        if (lastCommandResult is not null && lastFileReceipt is not null)
        {
            lines.Add($"Latest command: {CommandResultLabel(lastCommandResult)} | {lastFileReceipt.Summary}");
            lines.Add($"Next action: {CommandNextAction(lastCommandResult, lastFileReceipt)}");
        }

        if (latestArtifactSuggestion is not null)
        {
            lines.Add($"Artifact: {latestArtifactSuggestion.Summary}");
        }

        if (latestArtifactVerification is not null)
        {
            lines.Add($"{latestArtifactVerification.ActionTitle}: {latestArtifactVerification.Summary}");
        }

        lines.Add("");
        lines.Add("Type /help for composer commands.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string CleanBriefValue(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
    }

    private void Stop()
    {
        if (isRunningChat)
        {
            PauseAutoContinue("User stopped Agent collaboration.");
            TryCancel(chatCancellation);
            stopButton.IsEnabled = false;
            UpdateStatus("Stopping Agent collaboration...");
            return;
        }

        StopCommand();
    }

    private void StopCommand()
    {
        if (!isRunningCommand)
        {
            UpdateStatus("No active Agent command to stop.");
            return;
        }

        PauseAutoContinue("User stopped the active command.");
        TryCancel(commandCancellation);
        stopCommandButton.IsEnabled = false;
        commandStatusText.Text = "Stopping command...";
        AddActivity("Stop command", "Cancellation requested for active command.");
        SetBuildEvidenceSummary("Stopping approved command.");
        UpdateStatus("Stopping Agent command...");
    }

    private void PreviewCommand(bool allowWhileChat = false)
    {
        if (isRunningChat && !allowWhileChat)
        {
            approvalText.Text = "Wait for Agent collaboration to finish before previewing commands.";
            commandStatusText.Text = "Agent collaboration is running.";
            runButton.IsEnabled = false;
            rejectButton.IsEnabled = false;
            RefreshProviderState();
            return;
        }

        var preview = AgentWorkspaceCommand.BuildPreview(workspacePath, SelectedShell(), commandText.Text);
        pendingPreview = preview.Ok ? preview : null;
        riskItems.Children.Clear();
        foreach (var risk in AgentCommandRailViewModel.RiskChipsForPreview(preview))
        {
            riskItems.Children.Add(CreateChip(risk.Label, risk.BorderResourceKey));
        }

        if (!preview.Ok)
        {
            RecordBlockedCommandPreview(preview);
            PauseAutoContinue("Preview blocked; Auto Continue paused.");
            approvalText.Text = preview.Error;
            commandStatusText.Text = AgentCommandRailViewModel.PreviewStatus(preview);
            runButton.IsEnabled = false;
            rejectButton.IsEnabled = false;
            UpdateCommandSourceDisplay();
            SetBuildEvidenceSummary("Command preview blocked.");
            if (runbook.HasActiveRun)
            {
                runbook.UpdateStep("approval", "Blocked", preview.Error, DateTimeOffset.Now);
                runbook.AddCheckpoint("preview-blocked", "Command preview was blocked before approval.", DateTimeOffset.Now, preview.Error);
                PersistRunbook();
                RenderPhases();
            }

            RefreshOutputs();
            RefreshProviderState();
            return;
        }

        var plannedWrites = DescribePlannedFileWrites(preview.Command);
        var plannedWritesSummary = plannedWrites.Count == 0
            ? ""
            : $"\n\nWrites {plannedWrites.Count.ToString(CultureInfo.InvariantCulture)} file{(plannedWrites.Count == 1 ? "" : "s")}:\n{string.Join("\n", plannedWrites.Select(write => $"  {write}"))}";
        approvalText.Text = $"{preview.DisplayInvocation}\n\nWorking directory: {preview.WorkspacePath}\nSource: {commandProposalSource}{plannedWritesSummary}";
        commandStatusText.Text = AgentCommandRailViewModel.PreviewStatus(preview);
        UpdateCommandSourceDisplay();
        runButton.IsEnabled = !isRunningChat && !isRunningCommand;
        rejectButton.IsEnabled = !isRunningChat && !isRunningCommand;
        SetBuildEvidenceSummary("Command preview is ready for approval.");
        if (runbook.HasActiveRun)
        {
            runbook.MarkApprovalReady($"{preview.Shell}: {preview.Command}", DateTimeOffset.Now);
            PersistRunbook();
            RenderPhases();
        }

        publishControlEvent(
            "command.staged",
            "Agent command staged.",
            new { preview.Shell, preview.Command, preview.WorkspacePath });
        RefreshOutputs();
        RefreshProviderState();
        _ = TryAutoRunPendingPreviewAsync("Full Access is active for this session.");
    }

    private void RejectCommand()
    {
        if (isRunningChat)
        {
            UpdateStatus("Wait for Agent collaboration to finish before rejecting a command preview.");
            RefreshCommandActionState();
            return;
        }

        pendingPreview = null;
        stagedArtifactSuggestion = null;
        runButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
        riskItems.Children.Clear();
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        approvalText.Text = "Command rejected.";
        commandStatusText.Text = "Command rejected.";
        SetCommandSource("Rejected. Command rail cleared.");
        AddActivity("Rejected", "Command preview rejected.");
        SetBuildEvidenceSummary("Command rejected; no approved workspace work has run.");
        if (runbook.HasActiveRun)
        {
            runbook.MarkApprovalRejected("Operator rejected the staged command.", DateTimeOffset.Now);
            PersistRunbook();
            RenderPhases();
        }

        RefreshOutputs();
        RefreshProviderState();
    }

    private async Task RunApprovedCommandAsync()
    {
        if (isRunningCommand)
        {
            return;
        }

        if (isRunningChat)
        {
            commandStatusText.Text = "Wait for Agent collaboration to finish before approving a command.";
            approvalText.Text = commandStatusText.Text;
            UpdateStatus(commandStatusText.Text);
            RefreshCommandActionState();
            return;
        }

        if (pendingPreview is null || pendingPreview.ApprovalKey != AgentWorkspaceCommand.BuildPreview(workspacePath, SelectedShell(), commandText.Text).ApprovalKey)
        {
            InvalidatePreview("Preview the current command before running.");
            return;
        }

        var preview = pendingPreview;
        var runningArtifactSuggestion = ArtifactSuggestionForPreview(preview);
        isRunningCommand = true;
        commandCancellation?.Dispose();
        var activeCancellation = new CancellationTokenSource();
        commandCancellation = activeCancellation;
        var commandToken = activeCancellation.Token;
        SetCommandControlsEnabled(false);
        RefreshProviderState();
        commandStatusText.Text = AgentCommandRailViewModel.RunningStatus(preview.Shell);
        outputText.Text = FormatCommandHeader(preview);
        AddActivity("Command", $"{preview.Shell}: {preview.Command}");
        SetBuildEvidenceSummary("Approved command is running.");
        if (runbook.HasActiveRun)
        {
            runbook.MarkExecutionStarted($"{preview.Shell}: {preview.Command}", DateTimeOffset.Now);
            PersistRunbook();
            RenderPhases();
        }

        StartCommandHistory(preview);

        AgentCommandResult? completedResult = null;
        AgentWorkspaceFileReceipt? completedReceipt = null;
        var beforeFiles = new AgentWorkspaceFileSnapshot(
            new Dictionary<string, AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase),
            ScannedLimit: true);
        var beforeCaptured = false;
        try
        {
            beforeFiles = await CaptureWorkspaceFilesAsync(preview.WorkspacePath, commandToken);
            beforeCaptured = true;
            var result = await AgentWorkspaceCommand.RunAsync(
                preview,
                AgentWorkspaceCommand.TimeoutFor(preview, TimeSpan.FromSeconds(Math.Clamp(settings().AgentCommandTimeoutSeconds, 10, 3600))),
                commandToken);
            // A cancelled command still needs a final receipt. Reusing its cancelled
            // token here used to throw out of the async WPF click handler and could
            // terminate the application.
            var afterFiles = await CaptureWorkspaceFilesAsync(preview.WorkspacePath, CancellationToken.None);
            beforeFiles = ExcludeInternalStateFiles(preview.WorkspacePath, beforeFiles);
            afterFiles = ExcludeInternalStateFiles(preview.WorkspacePath, afterFiles);
            var receipt = BuildFileReceipt(beforeFiles, afterFiles);
            completedResult = result;
            completedReceipt = receipt;
            ApplyCompletedCommand(preview, result, receipt, runningArtifactSuggestion);
        }
        catch (OperationCanceledException) when (commandToken.IsCancellationRequested)
        {
            var afterFiles = await CaptureWorkspaceFilesAsync(preview.WorkspacePath, CancellationToken.None);
            if (!beforeCaptured)
            {
                // Cancellation during the initial scan means the precise before-state
                // is unknown; avoid presenting existing files as newly created.
                beforeFiles = new AgentWorkspaceFileSnapshot(afterFiles.Files, ScannedLimit: true);
            }

            beforeFiles = ExcludeInternalStateFiles(preview.WorkspacePath, beforeFiles);
            afterFiles = ExcludeInternalStateFiles(preview.WorkspacePath, afterFiles);
            var receipt = BuildFileReceipt(beforeFiles, afterFiles);
            var result = new AgentCommandResult(
                false,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                -1,
                "",
                "",
                TimeSpan.Zero,
                false,
                true,
                "Command cancelled.");
            completedResult = result;
            completedReceipt = receipt;
            ApplyCompletedCommand(preview, result, receipt, runningArtifactSuggestion);
        }
        finally
        {
            RunOnUiThread(() =>
            {
                isRunningCommand = false;
                if (ReferenceEquals(commandCancellation, activeCancellation))
                {
                    commandCancellation = null;
                }

                activeCancellation.Dispose();
                SetCommandControlsEnabled(true);
                runButton.IsEnabled = false;
                rejectButton.IsEnabled = false;
                stopCommandButton.IsEnabled = false;
                RefreshCommandHistory();
                RefreshWorkSummary();
                RefreshProviderState();
            });
        }

        if (completedResult is not null && completedReceipt is not null)
        {
            await TryAutoContinueAfterCommandAsync(completedResult, completedReceipt);
        }
    }

    private void ApplyCompletedCommand(
        AgentCommandPreview preview,
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        AgentArtifactSuggestion? runningArtifactSuggestion)
    {
        var inferredArtifactSuggestion = InferArtifactSuggestion(preview.WorkspacePath, receipt);
        var artifactVerification = runningArtifactSuggestion is null
            ? null
            : AgentArtifactVerification.From(runningArtifactSuggestion, result);
        RunOnUiThread(() =>
        {
            lastFileReceipt = receipt;
            lastCommandResult = result;
            latestArtifactSuggestion = inferredArtifactSuggestion ?? runningArtifactSuggestion;
            latestArtifactVerification = artifactVerification;
            lastCommandWasArtifactVerification = runningArtifactSuggestion is not null;
            outputText.Text = $"{FormatCommandResult(result)}{Environment.NewLine}{Environment.NewLine}{FormatFileReceipt(lastFileReceipt)}";
            outputText.ScrollToEnd();
            RefreshOutputActions();
            RefreshOutputs();
            pendingPreview = null;
            commandStatusText.Text = CommandStatusSummary(result, lastFileReceipt);
            UpdateStatus(commandStatusText.Text);
            SetCommandSource(CommandSourceAfterResult(result, lastFileReceipt));
            SetBuildEvidenceSummary(BuildCommandResultEvidenceSummary(result, lastFileReceipt));
            if (runbook.HasActiveRun)
            {
                runbook.MarkExecutionFinished(result.Ok, result.Canceled, lastFileReceipt.Summary, DateTimeOffset.Now);
                if (result.Ok && (runbookVerificationPending || runningArtifactSuggestion is not null))
                {
                    runbook.MarkCompleted(lastFileReceipt.Summary, DateTimeOffset.Now);
                    runbookVerificationPending = false;
                }

                PersistRunbook();
                RenderPhases();
            }

            AddActivity(result.Ok ? "Exit 0" : result.Canceled ? "Canceled" : "Exit", commandStatusText.Text);
            AddActivity("Files", lastFileReceipt.Summary);
            publishControlEvent(
                "file.receipt.captured",
                "Agent file receipt captured.",
                new
                {
                    lastFileReceipt.Summary,
                    Created = lastFileReceipt.Created.Count,
                    Modified = lastFileReceipt.Modified.Count,
                    Deleted = lastFileReceipt.Deleted.Count
                });
            if (latestArtifactSuggestion is not null)
            {
                publishControlEvent(
                    "artifact.detected",
                    "Agent artifact detected.",
                    new
                    {
                        latestArtifactSuggestion.Kind,
                        latestArtifactSuggestion.EntryPath,
                        latestArtifactSuggestion.Summary
                    });
            }

            FinishCommandHistory(result, lastFileReceipt);
            ClearCompletedCommandEditor();
            RefreshWorkSummary();
            AddCommandResultMessage(result, lastFileReceipt);
            ScrollToEnd();
        });
    }

    private AgentArtifactSuggestion? ArtifactSuggestionForPreview(AgentCommandPreview preview)
    {
        if (stagedArtifactSuggestion is null
            || !stagedArtifactSuggestion.Shell.Equals(preview.Shell, StringComparison.OrdinalIgnoreCase)
            || !CommandsEquivalent(stagedArtifactSuggestion.Command, preview.Command))
        {
            return null;
        }

        return stagedArtifactSuggestion;
    }

    private void ClearCompletedCommandEditor()
    {
        pendingPreview = null;
        stagedArtifactSuggestion = null;
        riskItems.Children.Clear();
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        runButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
    }

    private async Task TryStageCommandViaJsonExtractionAsync(
        ArenaViewSnapshot current,
        string prompt,
        AgentStep builder,
        CancellationToken cancellationToken)
    {
        var plan = ProviderPlanForRole(current, "builder");
        if (plan.Primary is null)
        {
            return;
        }

        UpdateStatus("No command found in prose; asking Builder for one JSON command...");
        AddActivity("Command extraction", "Builder reply had no stageable command; requesting one JSON command object.");
        var config = WithReasoningDisabled(plan.Primary);
        var messages = new List<ModelChatMessage>
        {
            new("system", """
                You convert app-build notes into exactly one runnable Windows command for the selected workspace.
                Reply with only one JSON object shaped like {"shell":"powershell","command":"..."} and nothing else.
                No prose, no markdown fences, no explanation before or after the JSON object.
                The command must work from the workspace root, use relative paths when possible, and must not change directories above the workspace.
                Prefer a command that creates or modifies the files described in the notes.
                """),
            new("user", $"""
                Workspace: {workspacePath}

                Operator request:
                {ShellUiHelpers.Truncate(prompt, 1200, ShellUiHelpers.TruncatedNoticeSuffix)}

                Builder notes to convert into the first runnable command:
                {TruncateKeepingEnds(builder.Text, 2400, 800)}
                """)
        };

        var result = await modelClient.CompleteChatAsync(config, messages, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CompletionHasUsableText(result))
        {
            AddActivity("Command extraction", "JSON extraction call returned no usable text.");
            return;
        }

        StageFirstCommandSuggestion(result.Text, "Builder");
        var staged = pendingPreview is not null || heldCommandSuggestion is not null;
        if (staged && autoRescueAttemptsRemaining > 0)
        {
            autoRescueAttemptsRemaining--;
            RefreshAutoApproveAction();
        }

        AddActivity("Command extraction", staged
            ? "JSON extraction staged a previewable command."
            : "JSON extraction did not produce a previewable command.");
    }

    private async Task<ModelCompletionResult> CompleteChatPreferStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> prompt,
        string roleId,
        string roleName,
        CancellationToken cancellationToken)
    {
        if (!settings().StreamModelResponses || modelClient is not IStreamingModelProviderClient streamingClient)
        {
            return await modelClient.CompleteChatAsync(config, prompt, cancellationToken);
        }

        var liveCard = BeginLiveStreamCard(roleName);
        try
        {
            var progress = new Progress<string>(delta => AppendLiveStreamText(liveCard, delta, roleId, roleName));
            return await streamingClient.CompleteChatStreamingAsync(config, prompt, progress, cancellationToken);
        }
        finally
        {
            RemoveLiveStreamCard(liveCard);
        }
    }

    private sealed class LiveStreamCard
    {
        public required Border Container { get; init; }
        public required TextBlock Text { get; init; }
        public StringBuilder Buffer { get; } = new();
        public DateTime LastRender { get; set; } = DateTime.MinValue;
    }

    private LiveStreamCard BeginLiveStreamCard(string roleName)
    {
        var text = new TextBlock
        {
            Text = "",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{roleName} is writing...",
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 7)
        });
        panel.Children.Add(text);
        var container = new Border
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 34, 12)
        };
        container.Child = panel;
        var card = new LiveStreamCard { Container = container, Text = text };
        messageItems.Children.Add(container);
        ScrollToEnd();
        return card;
    }

    private void AppendLiveStreamText(LiveStreamCard card, string delta, string roleId, string roleName)
    {
        card.Buffer.Append(delta);
        var now = DateTime.UtcNow;
        if ((now - card.LastRender).TotalMilliseconds < 80)
        {
            return;
        }

        card.LastRender = now;
        var rendered = card.Buffer.ToString();
        const int maxLiveChars = 4000;
        card.Text.Text = rendered.Length <= maxLiveChars
            ? rendered
            : "..." + rendered[^maxLiveChars..];
        var approximateTokens = Math.Max(1, card.Buffer.Length / 4);
        SetPhase(roleId, "Running", $"{roleName} is writing... ~{approximateTokens.ToString(CultureInfo.InvariantCulture)} tokens", persist: false);
        ScrollToEnd();
    }

    private void RemoveLiveStreamCard(LiveStreamCard card)
    {
        messageItems.Children.Remove(card.Container);
    }

    private async Task<AgentStep> CompleteRoleAsync(
        ArenaViewSnapshot current,
        AgentWorkspaceRole role,
        IReadOnlyList<ModelChatMessage> prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ProviderPlanForRole(current, role.RoleId);
        if (plan.Primary is null)
        {
            return AgentStep.Failed(role.RoleId, role.Name, "-", "No model configured.");
        }

        var result = await CompleteChatPreferStreamingAsync(plan.Primary, prompt, role.RoleId, role.Name, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var model = CompletionModel(result, plan.Primary);
        if (CompletionIsEmptySuccess(result))
        {
            result = await CompleteChatPreferStreamingAsync(WithReasoningDisabled(plan.Primary), prompt, role.RoleId, role.Name, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            model = CompletionModel(result, plan.Primary);
        }

        if (!CompletionHasUsableText(result) && plan.Fallback is not null)
        {
            result = await CompleteChatPreferStreamingAsync(plan.Fallback, prompt, role.RoleId, role.Name, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            model = CompletionModel(result, plan.Fallback);
        }

        return CompletionHasUsableText(result)
            ? AgentStep.Completed(role.RoleId, role.Name, model, result.Text, result.LatencyMs, result.TotalTokens)
            : AgentStep.Failed(role.RoleId, role.Name, model, CompletionError(result));
    }

    private void AddStep(AgentStep step)
    {
        latestSteps.Add(step);
        var body = step.Ok
            ? step.Text
            : $"Model step failed: {step.Error}";
        var message = new AgentWorkspaceMessage(step.RoleId, step.RoleName, body, step.Ok ? "Agent" : "Error", step.Model, DateTimeOffset.Now);
        messages.Add(message);
        messageItems.Children.Add(CreateMessageCard(message));
        PersistConversation();
        SetPhase(step.RoleId, step.Ok ? "Done" : "Error", step.Ok ? $"{step.RoleName} completed." : step.Error);
        AddActivity(step.RoleName, step.Ok
            ? $"{step.Model} | {step.LatencyMs.ToString(CultureInfo.InvariantCulture)} ms | {step.TotalTokens.ToString(CultureInfo.InvariantCulture)} tok"
            : step.Error);
        if (step.Ok && step.RoleId.Equals("builder", StringComparison.OrdinalIgnoreCase))
        {
            StageFirstCommandSuggestion(step.Text, step.RoleName);
        }

        ScrollToEnd();
    }

    private IReadOnlyList<ModelChatMessage> BuildRolePrompt(
        ArenaViewSnapshot current,
        AgentWorkspaceRole role,
        string prompt,
        IReadOnlyList<AgentStep> priorSteps)
    {
        var prior = priorSteps.Count == 0
            ? "No prior Agent notes."
            : string.Join(
                "\n\n",
                priorSteps.Select(step => $"{step.RoleName}:\n{TruncateKeepingEnds(step.Ok ? step.Text : step.Error, 2000, 1000)}"));
        var commandContext = lastCommandResult is null
            ? "No command output captured yet."
            : FormatLatestCommandContext();
        var commandProposalInstruction = role.RoleId.Equals("builder", StringComparison.OrdinalIgnoreCase)
            ? """
                If the operator asks you to write, create, modify, scaffold, build, run, or test an app, never stop after prose.
                Local and cloud-hosted models must follow the same output contract.
                If an app request is underspecified, choose a small safe default that fits the workspace and state the assumption briefly instead of asking clarifying questions.
                Keep app-building replies short: one brief assumption sentence, then the command proposal. Do not include a long plan, review, markdown tutorial, or repeated code explanation before the command.
                End with a final section named "Command proposal".
                In that section, provide exactly one first runnable command or command block in a fenced ```powershell block, one <command shell="PowerShell">...</command> block, or one JSON object shaped like {"shell":"powershell","command":"..."}.
                For create, build, scaffold, site, game, UI, or app requests, the command should create or modify at least one file under the workspace unless the safest first step is explicitly read-only inspection.
                If a full app command would be too large, create the smallest runnable first slice instead of overflowing the response.
                Code snippets alone are not deliverables in this workspace; the deliverable starts when a previewable command is staged and approved.
                The command must work from the selected workspace, use relative paths when possible, and avoid changing directories above the workspace.
                If the safest first step is inspection, propose one read-only inspection command instead of narrative-only guidance.
                Do not say the app has been written until approved command output shows the file changes or build result.
                """
            : """
                Prefer planning, review, and concrete next steps. Do not include runnable command blocks unless they are necessary for the Builder to stage.
                """;

        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Arena Agent, a software-creation workspace.
                Role: {role.Instructions}
                Work inside this selected workspace only: {workspacePath}
                You may propose terminal or PowerShell commands, but the app requires user preview and approval before anything runs.
                Keep output concise, practical, and reviewable. Do not claim that commands have run unless command output is included.
                Prefer file paths relative to the workspace when possible.
                {commandProposalInstruction}
                """),
            new ModelChatMessage("user", $"""
                Workspace: {workspacePath}
                Provider: {DisplayModel(current.ProviderModel)}
                Workspace profile:
                {Volatile.Read(ref workspaceProfile)}

                Session autonomy:
                {FormatAutonomyContext()}

                Latest command output:
                {commandContext}

                Recent command history:
                {FormatCommandHistoryContext()}

                Prior visible Agent notes:
                {prior}

                Operator request:
                {prompt}
                """)
        ];
    }

    private void StageFirstCommandSuggestion(string text, string roleName)
    {
        var suggestion = ExtractCommandSuggestion(text);
        var suggestionSource = $"{roleName} proposal";
        var materializedFiles = "";
        if (currentPromptRequiresCommand)
        {
            var fileSuggestion = ExtractFileWriteSuggestion(text);
            if (fileSuggestion is not null
                && (suggestion is null || !CommandLooksLikeWorkspaceMutation(suggestion.Command)))
            {
                suggestion = new AgentCommandSuggestion("PowerShell", BuildFileWriteCommand(fileSuggestion));
                suggestionSource = $"{roleName} file snippets";
                materializedFiles = string.Join(", ", fileSuggestion.Files.Select(file => file.Path));
                AddActivity("Files detected", $"Converted file snippets into an approvable write command: {materializedFiles}");
            }
        }

        if (suggestion is null)
        {
            AddActivity("No command", $"{roleName} did not include a runnable command proposal.");
            SetCommandSource($"{roleName} did not stage a command.");
            RefreshBuildEvidence();
            return;
        }

        suggestion = NormalizeCommandSuggestion(suggestion);

        if (isRunningCommand)
        {
            heldCommandSuggestion = suggestion;
            RefreshHeldCommandAction();
            AddActivity("Command held", $"{suggestionSource} arrived while another command was running.");
            SetCommandSource($"{suggestionSource} held while command runs.");
            SetBuildEvidenceSummary("Command is running; new Builder proposal is held.");
            AddCenterMessage(
                "Command proposal held",
                $"{suggestionSource} produced a {suggestion.Shell} command while another command was running. Use Held will be available after the active command finishes.\n\nHeld proposal:\n{ShellUiHelpers.Truncate(suggestion.Command, 600, ShellUiHelpers.TruncatedNoticeSuffix)}",
                "Action");
            return;
        }

        if (!string.IsNullOrWhiteSpace(commandText.Text))
        {
            if (allowRescueCommandReplacement && pendingPreview is null)
            {
                ClearCommandRailForRescueReplacement();
                AddActivity("Command replaced", "Auto Rescue replaced a stale unpreviewed command proposal.");
            }
            else
            {
                AddActivity("Command held", $"{suggestionSource} produced an action, but the approval rail already has a command.");
                SetCommandSource($"{suggestionSource} held. Review the existing command first.");
                heldCommandSuggestion = suggestion;
                RefreshHeldCommandAction();
                AddCenterMessage(
                    "Command proposal held",
                    $"{suggestionSource} produced a {suggestion.Shell} command, but the approval rail already contains a command. Review or clear the current command first.\n\nHeld proposal:\n{ShellUiHelpers.Truncate(suggestion.Command, 600, ShellUiHelpers.TruncatedNoticeSuffix)}",
                    "Action");
                SetBuildEvidenceSummary("Builder proposal is held behind the current command.");
                return;
            }
        }

        SetCommandSource(suggestionSource);
        stagedArtifactSuggestion = null;
        suppressCommandPreviewInvalidation = true;
        try
        {
            SelectShell(suggestion.Shell);
            commandText.Text = suggestion.Command;
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        PreviewCommand(allowWhileChat: true);
        AddActivity(
            pendingPreview is null ? "Command blocked" : "Command staged",
            pendingPreview is null
                ? $"{suggestionSource} produced a command, but preview blocked it."
                : $"{suggestionSource} produced a {suggestion.Shell} command for approval.");
        var readyBody = string.IsNullOrWhiteSpace(materializedFiles)
            ? $"{suggestionSource} staged a {suggestion.Shell} command in the approval rail. Review the preview, then Approve to run it or Reject to clear it."
            : $"{suggestionSource} were converted into a {suggestion.Shell} write-files command for {materializedFiles}. Review the approval rail, then Approve to create the app files or Reject to clear it.";
        AddCenterMessage(
            pendingPreview is null ? "Command proposal blocked" : "Command proposal ready",
            pendingPreview is null
                ? $"{suggestionSource} produced a command, but the preview blocked it. Review the approval rail for the exact boundary or risk issue."
                : readyBody,
            pendingPreview is null ? "Warning" : "Action");
        SetBuildEvidenceSummary(pendingPreview is null
            ? "Builder proposed a command, but preview blocked it."
            : string.IsNullOrWhiteSpace(materializedFiles)
                ? "Builder staged a command for approval."
                : "Builder file snippets were converted into an approvable write command.");
        RefreshHeldCommandAction();
    }

    private void AddCenterMessage(string title, string body, string kind)
    {
        var message = new AgentWorkspaceMessage("system", title, body, kind, "", DateTimeOffset.Now);
        messages.Add(message);
        messageItems.Children.Add(CreateMessageCard(message));
        PersistConversation();
    }

    private void AddCommandResultMessage(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        var state = result.Ok ? "completed" : result.Canceled ? "was cancelled" : result.TimedOut ? "timed out" : "failed";
        var noChangeAction = SuccessfulNoChangeRequiresRepair(result, receipt);
        var next = CommandNextAction(result, receipt);
        AddCenterMessage(
            "Command result",
            $"{result.Shell} {state} with exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)} in {FormatElapsed(result.Elapsed)}.\n{receipt.Summary}.{ReceiptPreviewText(receipt)}\n{next}",
            result.Ok && !noChangeAction ? "Result" : "Warning");
    }

    private string CommandNextAction(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return AgentCommandResultService.CommandNextAction(
            result,
            receipt,
            currentPromptRequiresCommand,
            IsLatestArtifactVerificationResult(result),
            latestArtifactVerification?.ActionTitle ?? "Artifact preview/verification");
    }

    private AgentResultFollowUpDescriptor ResultFollowUpDescriptor(AgentCommandResult result, AgentWorkspaceFileReceipt? receipt)
    {
        return AgentCommandResultService.ResultFollowUpDescriptor(
            result,
            receipt,
            currentPromptRequiresCommand,
            IsLatestArtifactVerificationResult(result),
            latestArtifactVerification?.ActionTitle ?? "Artifact check");
    }

    private string CommandStatusSummary(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return CommandRailViewModel(result, receipt).CommandStatus;
    }

    private string CommandSourceAfterResult(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return CommandRailViewModel(result, receipt).CommandSource;
    }

    private string BuildCommandResultEvidenceSummary(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return CommandRailViewModel(result, receipt).BuildEvidenceSummary;
    }

    private AgentCommandRailViewModel CommandRailViewModel(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return AgentCommandResultService.CommandRailViewModel(
            result,
            receipt,
            currentPromptRequiresCommand,
            IsLatestArtifactVerificationResult(result),
            latestArtifactVerification?.ActionTitle ?? "Artifact check");
    }

    private bool SuccessfulNoChangeRequiresRepair(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return AgentCommandResultService.SuccessfulNoChangeRequiresRepair(
            result,
            receipt,
            currentPromptRequiresCommand,
            IsLatestArtifactVerificationResult(result));
    }

    private bool SuccessfulNoChangeIsExpected(AgentCommandResult result)
    {
        return AgentCommandResultService.SuccessfulNoChangeIsExpected(result, IsLatestArtifactVerificationResult(result));
    }

    private bool IsLatestArtifactVerificationResult(AgentCommandResult result)
    {
        return AgentCommandResultService.IsArtifactVerificationResult(lastCommandWasArtifactVerification, latestArtifactVerification, result);
    }

    private void RenderEmptyState()
    {
        messageItems.Children.Clear();
        messageItems.Children.Add(BuildEmptyStateCard(resourceBrush, ApplyPromptTemplate));
    }

    internal static Border BuildEmptyStateCard(
        Func<string, Brush> resourceBrush,
        Action<string> stageTemplate)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 488
        };
        content.Children.Add(new TextBlock
        {
            Text = "Start a software task",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Describe the work below, or choose a focused starting point.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var starterActions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        foreach (var action in WelcomeActions)
        {
            var button = new Button
            {
                Content = action.Label,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Background = ShellUiHelpers.BlendBrush(resourceBrush("InputBrush"), resourceBrush("PrimaryBorderBrush"), 0.06),
                BorderBrush = ShellUiHelpers.BlendBrush(resourceBrush("ControlBorderBrush"), resourceBrush("PrimaryBorderBrush"), 0.32),
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                MinHeight = 30
            };
            AutomationProperties.SetName(button, action.Label);
            button.Click += (_, _) => stageTemplate(action.TemplateId);
            starterActions.Children.Add(button);
        }

        content.Children.Add(starterActions);
        var card = new Border
        {
            Background = ShellUiHelpers.BlendBrush(resourceBrush("CardBrush"), resourceBrush("PrimaryBorderBrush"), 0.06),
            BorderBrush = ShellUiHelpers.BlendBrush(resourceBrush("ControlBorderBrush"), resourceBrush("PrimaryBorderBrush"), 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 16, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 520,
            Child = content
        };
        AutomationProperties.SetName(card, "Software task welcome");
        return card;
    }

    private bool RestorePersistedConversation()
    {
        var currentSettings = settings();
        var restoredMessages = AgentWorkspaceConversationStore.RestoreMessages(
            currentSettings.AgentWorkspaceMessages,
            currentSettings.AgentWorkspaceSessionWorkspacePath,
            workspacePath,
            DateTimeOffset.Now);

        if (restoredMessages.Count == 0)
        {
            return false;
        }

        messages.Clear();
        messageItems.Children.Clear();
        foreach (var message in restoredMessages)
        {
            messages.Add(message);
            messageItems.Children.Add(CreateMessageCard(message));
        }

        AddActivity("Restored", AgentWorkspaceConversationStore.RestoreActivityDetail(messages.Count));
        UpdateStatus("Agent chat restored.");
        ScrollToEnd();
        return true;
    }

    private void PersistConversation()
    {
        var currentSettings = settings();
        currentSettings.AgentWorkspaceSessionWorkspacePath = workspacePath;
        currentSettings.AgentWorkspaceMessages = AgentWorkspaceConversationStore.PersistedMessages(messages);
        currentSettings.AgentRunbook = runbook.State;
        settingsStore.Save(currentSettings);
    }

    private void PersistRunbook()
    {
        var currentSettings = settings();
        currentSettings.AgentRunbook = runbook.State;
        settingsStore.Save(currentSettings);
    }

    private Border CreateMessageCard(AgentWorkspaceMessage message)
    {
        var isUser = message.Kind.Equals("User", StringComparison.OrdinalIgnoreCase);
        var isStatus = message.Kind.Equals("Status", StringComparison.OrdinalIgnoreCase);
        var isError = message.Kind.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("Warning", StringComparison.OrdinalIgnoreCase);
        var isAction = message.Kind.Equals("Action", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("Result", StringComparison.OrdinalIgnoreCase);
        var borderBrush = isError
            ? resourceBrush("DangerBorderBrush")
            : isUser
                ? resourceBrush("PrimaryBorderBrush")
                : isAction
                    ? resourceBrush("AssistBorderBrush")
                    : resourceBrush("DisabledBorderBrush");
        var card = new Border
        {
            Background = isStatus ? Brushes.Transparent : resourceBrush(isUser ? "InputBrush" : "CardBrush"),
            BorderBrush = borderBrush,
            BorderThickness = isStatus ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = isStatus ? new Thickness(2, 4, 2, 4) : new Thickness(12),
            Margin = isUser ? new Thickness(110, 0, 0, 12) : new Thickness(0, 0, isStatus ? 0 : 34, 12),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = isUser ? 720 : double.PositiveInfinity
        };

        var panel = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = message.Model.Length == 0 ? message.Title : $"{message.Title} | {DisplayModel(message.Model)}",
            Foreground = resourceBrush(isStatus ? "MutedTextBrush" : "TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var kind = new TextBlock
        {
            Text = message.Kind,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(kind, 1);
        header.Children.Add(kind);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = message.Body,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        });
        card.Child = panel;
        AutomationProperties.SetName(card, $"Agent message {message.Title}");
        AutomationProperties.SetHelpText(card, message.Body);
        return card;
    }

    private void RenderRoles()
    {
        leftRoleItems.Children.Clear();
        var current = snapshot();
        foreach (var role in Roles)
        {
            leftRoleItems.Children.Add(CreateRoleRow(role, current));
        }
    }

    private void ResetPhases(string summary)
    {
        phaseStates.Clear();
        foreach (var role in Roles)
        {
            var status = builderOnlyForSession && role.RoleId is "planner" or "reviewer" ? "Skipped" : "Pending";
            phaseStates[role.RoleId] = status;
            if (runbook.HasActiveRun)
            {
                runbook.UpdateStep(AgentRunbookService.PhaseStepId(role.RoleId), status, summary, DateTimeOffset.Now);
            }
        }

        phaseSummaryText.Text = summary;
        PersistRunbook();
        RenderPhases();
    }

    private void RestorePhaseStatesFromRunbook()
    {
        phaseStates.Clear();
        foreach (var role in Roles)
        {
            var stepId = AgentRunbookService.PhaseStepId(role.RoleId);
            var status = runbook.State.Steps.FirstOrDefault(step => step.Id == stepId)?.Status ?? "Pending";
            phaseStates[role.RoleId] = status switch
            {
                "Completed" => "Done",
                "Failed" => "Error",
                _ => status
            };
        }
    }

    private void SetPhase(string roleId, string state, string summary, bool persist = true)
    {
        phaseStates[roleId] = state;
        phaseSummaryText.Text = summary;
        if (runbook.HasActiveRun)
        {
            runbook.UpdateStep(AgentRunbookService.PhaseStepId(roleId), state, summary, DateTimeOffset.Now);
            if (persist)
            {
                PersistRunbook();
            }
        }

        RenderPhases();
    }

    private void RenderPhases()
    {
        phaseItems.Children.Clear();
        runbookMetaText.Text = runbook.HasActiveRun
            ? $"{runbook.State.RunId} | {runbook.State.Status} | {runbook.State.Checkpoints.Count.ToString(CultureInfo.InvariantCulture)} checkpoints"
            : "No active runbook. A task creates one automatically.";
        runbookMetaText.ToolTip = runbook.HasActiveRun ? runbook.State.Objective : runbookMetaText.Text;
        if (runbook.HasActiveRun && runbook.State.Steps.Count > 0)
        {
            foreach (var step in runbook.State.Steps.OrderBy(step => step.Sequence))
            {
                phaseItems.Children.Add(CreateRunbookStepRow(step));
            }

            return;
        }

        foreach (var role in Roles)
        {
            phaseItems.Children.Add(CreatePhaseRow(role, phaseStates.TryGetValue(role.RoleId, out var state) ? state : "Pending"));
        }
    }

    private void SetBuildEvidenceSummary(string summary)
    {
        buildEvidenceSummary = string.IsNullOrWhiteSpace(summary) ? "No app-building task yet." : summary.Trim();
        RefreshBuildEvidence();
    }

    private void RefreshBuildEvidence()
    {
        buildEvidenceSummaryText.Text = buildEvidenceSummary;
        buildEvidenceSummaryText.ToolTip = buildEvidenceSummary;
        buildEvidenceItems.Children.Clear();
        foreach (var item in CurrentBuildEvidence())
        {
            buildEvidenceItems.Children.Add(CreateEvidenceRow(item));
        }
    }

    private void RefreshOutputs()
    {
        var items = CurrentOutputs();
        outputSummary = OutputSummary(items);
        outputSummaryText.Text = outputSummary;
        outputSummaryText.ToolTip = outputSummary;
        outputItems.Children.Clear();
        foreach (var item in items)
        {
            outputItems.Children.Add(CreateOutputRow(item));
        }
    }

    private IReadOnlyList<AgentOutputItem> CurrentOutputs()
    {
        var items = new List<AgentOutputItem>();
        if (latestArtifactVerification is not null)
        {
            items.Add(new AgentOutputItem(
                latestArtifactVerification.ActionTitle,
                latestArtifactVerification.Ok ? "Ready" : latestArtifactVerification.Canceled ? "Cancelled" : "Needs repair",
                latestArtifactVerification.Summary,
                latestArtifactVerification.Ok ? "AssistBorderBrush" : "DangerBorderBrush"));
        }

        if (latestArtifactSuggestion is not null)
        {
            items.Add(new AgentOutputItem(
                "Artifact",
                latestArtifactSuggestion.Kind,
                $"{latestArtifactSuggestion.Summary}\n{latestArtifactSuggestion.Shell}: {latestArtifactSuggestion.Command}",
                "PrimaryBorderBrush"));
        }

        if (lastFileReceipt is not null)
        {
            var changed = ReceiptHasChanges(lastFileReceipt);
            var limitedUnknown = ReceiptScanIsLimitedWithoutTrackedChanges(lastFileReceipt);
            var changedPaths = ChangedPathSummary(lastFileReceipt, 4);
            items.Add(new AgentOutputItem(
                "Files",
                changed ? lastFileReceipt.Summary : limitedUnknown ? "Scan limited" : "No changes",
                string.IsNullOrWhiteSpace(changedPaths)
                    ? limitedUnknown
                        ? "No changes detected inside the scanned file window; changes outside the scan limit are unknown."
                        : "No tracked file changes detected."
                    : changedPaths,
                changed ? "AssistBorderBrush" : limitedUnknown ? "PrimaryBorderBrush" : "DisabledBorderBrush"));
        }

        if (lastCommandResult is not null)
        {
            items.Add(new AgentOutputItem(
                "Command",
                CommandResultLabel(lastCommandResult),
                $"{lastCommandResult.Shell}: {lastCommandResult.Command}",
                lastCommandResult.Ok ? "AssistBorderBrush" : lastCommandResult.Canceled ? "DisabledBorderBrush" : "DangerBorderBrush"));
        }
        else if (pendingPreview is not null)
        {
            items.Add(new AgentOutputItem(
                "Preview",
                "Ready",
                pendingPreview.DisplayInvocation,
                "PrimaryBorderBrush"));
        }

        return items;
    }

    private static string OutputSummary(IReadOnlyList<AgentOutputItem> items)
    {
        return AgentCommandRailViewModel.OutputSummary(items);
    }

    private Border CreateOutputRow(AgentOutputItem item)
    {
        var card = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush(item.BorderResourceKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = item.Detail
        };
        var panel = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = item.Label,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var state = new TextBlock
        {
            Text = item.State,
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.State
        };
        Grid.SetColumn(state, 1);
        header.Children.Add(state);
        panel.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(item.Detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ShellUiHelpers.Truncate(item.Detail.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim(), 130, ShellUiHelpers.TruncatedNoticeSuffix),
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        card.Child = panel;
        AutomationProperties.SetName(card, $"Agent output {item.Label} {item.State}");
        AutomationProperties.SetHelpText(card, item.Detail);
        return card;
    }

    private static string CommandResultLabel(AgentCommandResult result)
    {
        if (result.Canceled)
        {
            return "Cancelled";
        }

        if (result.TimedOut)
        {
            return "Timed out";
        }

        return result.Ok ? $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}" : "Failed";
    }

    private IReadOnlyList<AgentEvidenceItem> CurrentBuildEvidence()
    {
        var workspaceReady = !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath);
        var hasCommand = !string.IsNullOrWhiteSpace(commandText.Text);
        var hasResult = lastCommandResult is not null;
        var hasReceipt = lastFileReceipt is not null;
        var hasChanges = lastFileReceipt is not null && ReceiptHasChanges(lastFileReceipt);
        var hasLimitedUnknownChanges = lastFileReceipt is not null && ReceiptScanIsLimitedWithoutTrackedChanges(lastFileReceipt);
        var artifactCheck = lastCommandResult is not null && IsLatestArtifactVerificationResult(lastCommandResult);
        var artifactCheckOk = artifactCheck && lastCommandResult!.Ok;
        var noChangeExpected = artifactCheckOk || (lastCommandResult is not null && SuccessfulNoChangeIsExpected(lastCommandResult));
        var artifactState = latestArtifactVerification is not null
            ? latestArtifactVerification.EvidenceState
            : latestArtifactSuggestion is null ? "None" : latestArtifactSuggestion.Kind;
        var artifactBrush = latestArtifactVerification is not null
            ? latestArtifactVerification.Ok ? "AssistBorderBrush" : "DangerBorderBrush"
            : latestArtifactSuggestion is null ? "DisabledBorderBrush" : "PrimaryBorderBrush";
        var filesBrush = "DisabledBorderBrush";
        if (hasReceipt)
        {
            if (hasChanges || noChangeExpected)
            {
                filesBrush = "AssistBorderBrush";
            }
            else if (hasLimitedUnknownChanges)
            {
                filesBrush = "PrimaryBorderBrush";
            }
            else if (currentPromptRequiresCommand)
            {
                filesBrush = "DangerBorderBrush";
            }
        }
        var verifyState = hasResult
            ? noChangeExpected
                ? "Checked"
                : lastCommandResult!.Ok && hasChanges ? "Verify next" : hasLimitedUnknownChanges ? "Review scan" : lastCommandResult.Canceled ? "Retry smaller" : "Repair next"
            : "Pending";
        var verifyBrush = hasResult
            ? noChangeExpected || (lastCommandResult!.Ok && (hasChanges || hasLimitedUnknownChanges))
                ? "PrimaryBorderBrush"
                : "DangerBorderBrush"
            : "DisabledBorderBrush";
        var autonomyState = autoContinueForSession
            ? $"Loop {autoContinueRemainingSteps.ToString(CultureInfo.InvariantCulture)}"
            : autoApproveCommandsForSession
                ? $"Full Access + Rescue {autoRescueAttemptsRemaining.ToString(CultureInfo.InvariantCulture)}"
                : "Manual";

        // Before any task runs, most rows would just read "None/Not run/Pending" - show
        // only the rows with live state and let the pipeline rows appear once a task starts.
        var idle = !currentPromptRequiresCommand
            && !hasCommand
            && !hasResult
            && !isRunningCommand
            && heldCommandSuggestion is null
            && latestArtifactVerification is null
            && latestArtifactSuggestion is null;
        if (idle)
        {
            var idleRows = new List<AgentEvidenceItem>
            {
                new("Workspace", workspaceReady ? ShortWorkspaceName(workspacePath) : "Missing", workspaceReady ? "AssistBorderBrush" : "DangerBorderBrush")
            };
            if (autoContinueForSession || autoApproveCommandsForSession)
            {
                idleRows.Add(new AgentEvidenceItem("Autonomy", autonomyState, "PrimaryBorderBrush"));
            }

            return idleRows;
        }

        return
        [
            new("Workspace", workspaceReady ? ShortWorkspaceName(workspacePath) : "Missing", workspaceReady ? "AssistBorderBrush" : "DangerBorderBrush"),
            new("Autonomy", autonomyState, autoContinueForSession || autoApproveCommandsForSession ? "PrimaryBorderBrush" : "DisabledBorderBrush"),
            new("Command Need", currentPromptRequiresCommand ? "Required" : "Optional", currentPromptRequiresCommand ? "PrimaryBorderBrush" : "DisabledBorderBrush"),
            new("Proposal", heldCommandSuggestion is not null ? "Held" : hasCommand ? "Staged" : currentPromptRequiresCommand ? "Missing" : "None", heldCommandSuggestion is not null || hasCommand ? "PrimaryBorderBrush" : currentPromptRequiresCommand ? "DangerBorderBrush" : "DisabledBorderBrush"),
            new("Preview", pendingPreview is not null ? "Ready" : hasCommand ? "Needs preview" : "None", pendingPreview is not null ? "AssistBorderBrush" : hasCommand ? "PrimaryBorderBrush" : "DisabledBorderBrush"),
            new("Command Run", isRunningCommand ? "Running" : hasResult ? lastCommandResult!.Ok ? $"Exit {lastCommandResult.ExitCode.ToString(CultureInfo.InvariantCulture)}" : lastCommandResult.Canceled ? "Cancelled" : "Failed" : "Not run", isRunningCommand ? "PrimaryBorderBrush" : hasResult ? lastCommandResult!.Ok ? "AssistBorderBrush" : "DangerBorderBrush" : "DisabledBorderBrush"),
            new("Files", hasReceipt ? hasChanges ? lastFileReceipt!.Summary : hasLimitedUnknownChanges ? "Scan limited" : noChangeExpected ? "No changes expected" : "No changes" : "Waiting", filesBrush),
            new("Artifact", artifactState, artifactBrush),
            new("Verify", verifyState, verifyBrush)
        ];
    }

    private Border CreateEvidenceRow(AgentEvidenceItem item)
    {
        var card = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush(item.BorderResourceKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = item.Label,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var state = new TextBlock
        {
            Text = item.State,
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.State
        };
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);
        card.Child = grid;
        AutomationProperties.SetName(card, $"Agent build evidence {item.Label} {item.State}");
        AutomationProperties.SetHelpText(card, $"{item.Label}: {item.State}.");
        return card;
    }

    private Border CreatePhaseRow(AgentWorkspaceRole role, string state)
    {
        var brush = state.Equals("Done", StringComparison.OrdinalIgnoreCase)
            ? "AssistBorderBrush"
            : state.Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? "PrimaryBorderBrush"
                : state.Equals("Error", StringComparison.OrdinalIgnoreCase)
                    ? "DangerBorderBrush"
                    : "DisabledBorderBrush";
        var card = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush(brush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = role.Name,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var stateText = new TextBlock
        {
            Text = state,
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(stateText, 1);
        grid.Children.Add(stateText);
        card.Child = grid;
        AutomationProperties.SetName(card, $"Agent phase {role.Name} {state}");
        AutomationProperties.SetHelpText(card, $"{role.Name} phase status: {state}.");
        return card;
    }

    private Border CreateRunbookStepRow(WpfAgentRunbookStep step)
    {
        var brush = step.Status switch
        {
            "Completed" => "AssistBorderBrush",
            "Running" => "PrimaryBorderBrush",
            "Waiting" => "ControlBorderBrush",
            "Blocked" or "Failed" => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var card = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush(brush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = string.IsNullOrWhiteSpace(step.Evidence)
                ? $"Step {step.Id}; owner {step.Owner}; status {step.Status}."
                : step.Evidence
        };
        var panel = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"{step.Sequence.ToString("00", CultureInfo.InvariantCulture)}  {step.Title}",
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var status = new TextBlock
        {
            Text = step.Status,
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = $"{step.Id} · {step.Owner}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        card.Child = panel;
        AutomationProperties.SetName(card, $"Agent runbook step {step.Sequence} {step.Title} {step.Status}");
        AutomationProperties.SetHelpText(card, $"Stable step id {step.Id}. Owner {step.Owner}. Status {step.Status}. {step.Evidence}");
        AutomationProperties.SetItemStatus(card, step.Status);
        return card;
    }

    private Border CreateRoleRow(AgentWorkspaceRole role, ArenaViewSnapshot? current)
    {
        var model = current is null ? "-" : DisplayModel(ModelForRole(current, role.RoleId));
        var card = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = role.Name,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        panel.Children.Add(new TextBlock
        {
            Text = model,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = model
        });
        card.Child = panel;
        return card;
    }

    private Border CreateChip(string text, string borderResourceKey)
    {
        var chip = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush(borderResourceKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 6)
        };
        chip.Child = new TextBlock
        {
            Text = text,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        return chip;
    }

    private void AddActivity(string title, string detail)
    {
        var item = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        item.Child = panel;
        activityItems.Children.Insert(0, item);
        while (activityItems.Children.Count > MaxActivityItems)
        {
            activityItems.Children.RemoveAt(activityItems.Children.Count - 1);
        }
    }

    private void ApplyPromptTemplate(string templateId)
    {
        if (isRunningChat)
        {
            return;
        }

        var current = promptText.Text.Trim();
        var prefix = BuildPromptTemplate(templateId);

        promptText.Text = string.IsNullOrWhiteSpace(current)
            ? prefix
            : $"{prefix}\n\nTask:\n{current}";
        promptText.Focus();
        promptText.CaretIndex = promptText.Text.Length;
        promptText.ScrollToEnd();
        RefreshPromptBudget();
    }

    private string BuildPromptTemplate(string templateId)
    {
        return templateId switch
        {
            "plan" => "Plan this software change. Include files to inspect, implementation steps, test strategy, risks, and the smallest shippable slice.",
            "breakdown" => "Break this software task into ordered work items with dependencies, validation checks, and places where agents should challenge assumptions.",
            "progress" => "Summarize current progress, likely blockers, next commands to consider, and what should be verified before release.",
            "command" => "Propose exactly one first terminal or PowerShell command to preview for this workspace. End with a fenced powershell Command proposal block or a JSON object with shell and command fields, then explain what the command checks or changes.",
            "build_app" => "Build a small, runnable app in this workspace. Start with the minimum useful file set, then propose exactly one first PowerShell command that creates or modifies files under this workspace. The Builder must not stop at prose or standalone code snippets; it must end with a fenced powershell Command proposal block or JSON shell/command object.",
            "next_step" => BuildNextStepPrompt(),
            "verify" => BuildVerifyPrompt(),
            "rescue_command" => BuildRescueCommandPrompt(),
            _ => ""
        };
    }

    private string BuildNextStepPrompt()
    {
        if (lastCommandResult is null)
        {
            return "Review the current Agent conversation and propose exactly one safest next command to preview. If no command is safe yet, propose a read-only workspace inspection command in a fenced powershell Command proposal block.";
        }

        var nextAction = lastFileReceipt is null
            ? "Review the latest command output and choose the safest next action."
            : CommandNextAction(lastCommandResult, lastFileReceipt);
        var brief = string.IsNullOrWhiteSpace(lastWorkBrief)
            ? "No work brief has been generated yet."
            : ShellUiHelpers.Truncate(lastWorkBrief, 2200, ShellUiHelpers.TruncatedNoticeSuffix);
        var artifact = latestArtifactSuggestion is null
            ? "No generated artifact suggestion detected yet."
            : $"{latestArtifactSuggestion.Summary}\nSuggested command ({latestArtifactSuggestion.Shell}):\n{latestArtifactSuggestion.Command}";
        var artifactCheck = latestArtifactVerification is null
            ? "No artifact preview or verification result has been recorded yet."
            : latestArtifactVerification.Summary;

        if (lastFileReceipt is not null && SuccessfulNoChangeRequiresRepair(lastCommandResult, lastFileReceipt))
        {
            return $"""
                Repair the previous app-building step. The last approved command exited without tracked workspace file changes, so the app has not been written yet.
                Propose exactly one next command that creates or modifies files under the selected workspace, unless a read-only inspection command is explicitly necessary to identify the project type.
                End with a fenced powershell Command proposal block.

                Recommended next action:
                {nextAction}

                Original task:
                {lastOperatorPrompt}

                Last command output:
                {FormatLatestCommandContext()}

                Latest work brief:
                {brief}

                Artifact suggestion:
                {artifact}
                """;
        }

        return $"""
            Review the last command output, decide whether it succeeded, and propose exactly one next command to continue or repair the app work.
            End with a fenced powershell Command proposal block.

            Recommended next action:
            {nextAction}

            Last command output:
            {FormatLatestCommandContext()}

            Latest work brief:
            {brief}

            Artifact suggestion:
            {artifact}

            Artifact verification:
            {artifactCheck}
            """;
    }

    private string BuildVerifyPrompt()
    {
        var context = lastCommandResult is null
            ? "No command has run yet. Prefer one read-only inspection command if the verification target is unclear."
            : FormatLatestCommandContext();
        var brief = string.IsNullOrWhiteSpace(lastWorkBrief)
            ? "No work brief has been generated yet."
            : ShellUiHelpers.Truncate(lastWorkBrief, 2200, ShellUiHelpers.TruncatedNoticeSuffix);
        var artifact = latestArtifactSuggestion is null
            ? "No generated artifact suggestion detected yet."
            : $"{latestArtifactSuggestion.Summary}\nSuggested preview command ({latestArtifactSuggestion.Shell}):\n{latestArtifactSuggestion.Command}";
        return $"""
            Verify the app or code work in this workspace. Propose exactly one build, run, smoke-test, or read-only inspection command to preview next.
            End with a fenced powershell Command proposal block.

            Current evidence:
            {context}

            Latest work brief:
            {brief}

            Artifact suggestion:
            {artifact}
            """;
    }

    private string BuildRescueCommandPrompt()
    {
        var original = string.IsNullOrWhiteSpace(lastOperatorPrompt)
            ? "the current software task"
            : lastOperatorPrompt;
        var commandContext = lastCommandResult is null
            ? "No approved command has run yet."
            : FormatLatestCommandContext();
        return $"""
            Rescue this Agent run from prose-only output.
            Original task:
            {original}

            Return exactly one runnable command for the selected workspace. Prefer PowerShell on Windows.
            For app, site, game, UI, scaffold, or build requests, the command should create or modify at least one workspace file unless a read-only inspection command is explicitly necessary.
            Do not include code snippets as standalone deliverables. Do not claim the app is written until command output or file-change receipts prove it.
            End with one fenced powershell section under the heading "Command proposal", one <command shell="PowerShell">...</command> block, or one JSON object with shell and command fields.

            Current evidence:
            {commandContext}
            """;
    }

    private void StageRescuePrompt(string reason)
    {
        if (!promptText.IsEnabled)
        {
            promptText.IsEnabled = true;
        }

        promptText.Text = BuildRescueCommandPrompt();
        promptText.CaretIndex = promptText.Text.Length;
        RefreshPromptBudget();
        AddActivity("Rescue prompt", reason);
    }

    private void RefreshPromptBudget()
    {
        var chars = promptText.Text.Length;
        promptBudgetText.Text = $"{chars.ToString(CultureInfo.InvariantCulture)} chars / ~{Math.Max(0, chars / 4).ToString(CultureInfo.InvariantCulture)} tok";
    }

    private void UpdateStatus(string status)
    {
        statusText.Text = status;
        setShellStatus(status);
    }

    private void RunOnUiThread(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private string AgentModeLabel()
    {
        if (isRunningCommand)
        {
            return "Command running";
        }

        if (isRunningChat)
        {
            return "Collaborating";
        }

        if (pendingPreview is not null)
        {
            return "Preview ready";
        }

        if (!string.IsNullOrWhiteSpace(commandText.Text))
        {
            return "Needs preview";
        }

        if (lastFileReceipt is not null && ReceiptHasChanges(lastFileReceipt))
        {
            return "Files changed";
        }

        if (lastFileReceipt is not null && ReceiptScanIsLimitedWithoutTrackedChanges(lastFileReceipt))
        {
            return "Scan limited";
        }

        if (lastCommandResult is not null && IsLatestArtifactVerificationResult(lastCommandResult))
        {
            return lastCommandResult.Ok ? "Artifact checked" : "Artifact repair";
        }

        if (autoContinueForSession)
        {
            return "Full access loop";
        }

        if (lastCommandResult is not null)
        {
            return lastCommandResult.Ok ? "Verify next" : "Repair next";
        }

        return autoApproveCommandsForSession ? "Full access" : "Manual approval";
    }

    private void SetCommandSource(string source)
    {
        commandProposalSource = string.IsNullOrWhiteSpace(source) ? "Manual command" : source.Trim();
        UpdateCommandSourceDisplay();
        RefreshBuildEvidence();
    }

    private void UpdateCommandSourceDisplay()
    {
        commandSourceText.Text = $"Source: {commandProposalSource}";
        commandSourceText.ToolTip = commandSourceText.Text;
    }

    private void CopyCommandOutput()
    {
        if (string.IsNullOrWhiteSpace(outputText.Text) || outputText.Text.Equals("No command output.", StringComparison.Ordinal))
        {
            UpdateStatus("No command output to copy.");
            return;
        }

        if (!TrySetClipboardText(outputText.Text))
        {
            UpdateStatus("Clipboard is unavailable. Try copying command output again.");
            return;
        }

        AddActivity("Copied", "Terminal output copied.");
        UpdateStatus("Command output copied.");
    }

    private void CopyFileReceipt()
    {
        if (lastFileReceipt is null)
        {
            UpdateStatus("No file receipt to copy.");
            return;
        }

        if (!TrySetClipboardText(FormatFileReceipt(lastFileReceipt)))
        {
            UpdateStatus("Clipboard is unavailable. Try copying the file receipt again.");
            return;
        }

        AddActivity("Copied", "File-change receipt copied.");
        UpdateStatus("File receipt copied.");
    }

    private void CopyWorkBrief()
    {
        if (string.IsNullOrWhiteSpace(lastWorkBrief))
        {
            UpdateStatus("No Agent work brief to copy.");
            return;
        }

        if (!TrySetClipboardText(lastWorkBrief))
        {
            UpdateStatus("Clipboard is unavailable. Try copying the Agent work brief again.");
            return;
        }

        AddActivity("Copied", "Agent work brief copied.");
        UpdateStatus("Agent work brief copied.");
    }

    private void StageVerifyPromptFromBrief()
    {
        if (lastCommandResult is null)
        {
            UpdateStatus("Run an approved command before staging verification.");
            return;
        }

        ApplyPromptTemplate("verify");
        runbookVerificationPending = true;
        if (runbook.HasActiveRun)
        {
            runbook.MarkVerificationStaged("Verification prompt prepared from the latest work brief and receipt.", DateTimeOffset.Now);
            PersistRunbook();
            RenderPhases();
        }

        AddActivity("Verify staged", "Verification prompt staged from the latest work brief.");
        UpdateStatus("Verification prompt staged.");
    }

    private void StageRunbookResumePrompt(WpfAgentRunbookStep step)
    {
        var recentCheckpoints = runbook.State.Checkpoints
            .OrderByDescending(checkpoint => checkpoint.Sequence)
            .Take(4)
            .OrderBy(checkpoint => checkpoint.Sequence)
            .Select(checkpoint => $"- {checkpoint.Id} [{checkpoint.Kind}] {checkpoint.Summary}")
            .ToArray();
        promptText.Text = $"""
            Resume the persisted Agent runbook without repeating completed work.

            Run: {runbook.State.RunId}
            Objective: {runbook.State.Objective}
            Next stable step: {step.Id} — {step.Title}
            Owner: {step.Owner}
            Current status: {step.Status}
            Evidence: {(string.IsNullOrWhiteSpace(step.Evidence) ? "No evidence recorded." : step.Evidence)}

            Recent checkpoints:
            {(recentCheckpoints.Length == 0 ? "- none" : string.Join(Environment.NewLine, recentCheckpoints))}

            Inspect the existing workspace and evidence first. Continue only this incomplete step. Do not repeat completed steps. If workspace changes are required, return exactly one previewable command for approval.
            """;
        promptText.CaretIndex = promptText.Text.Length;
        RefreshPromptBudget();
        runbook.UpdateStep(step.Id, "Waiting", $"Resume staged for {step.Id}.", DateTimeOffset.Now);
        runbook.AddCheckpoint("resume", $"Resume prompt staged for {step.Id}.", DateTimeOffset.Now, step.Evidence);
        runbookVerificationPending = step.Id == "verify";
        PersistRunbook();
        RenderPhases();
        AddActivity("Runbook resume", $"{step.Id}: {step.Title}");
        AddCenterMessage("Runbook resume staged", $"{runbook.State.RunId} will continue from {step.Id} ({step.Title}). Review the prompt, then send it.", "Action");
        UpdateStatus($"Runbook resume staged for {step.Title}.");
    }

    private void StageNextPromptFromResult()
    {
        if (lastCommandResult is null)
        {
            UpdateStatus("Run an approved command before staging a result-aware next step.");
            return;
        }

        var descriptor = ResultFollowUpDescriptor(lastCommandResult, lastFileReceipt);
        ApplyPromptTemplate("next_step");
        AddActivity(descriptor.ActivityTitle, descriptor.ActivityDetail);
        AddCenterMessage(
            descriptor.CardTitle,
            $"{descriptor.CardBody}\n\nThe prompt composer now contains the latest command output, file receipt, work brief, and recommended next action. Send it to ask Planner, Reviewer, and Builder for one previewable command.",
            "Action");
        SetBuildEvidenceSummary(descriptor.BuildEvidence);
        UpdateStatus(descriptor.Status);
    }

    private void CopyCommandProposal()
    {
        if (string.IsNullOrWhiteSpace(commandText.Text))
        {
            UpdateStatus("No command proposal to copy.");
            return;
        }

        if (!TrySetClipboardText(commandText.Text))
        {
            UpdateStatus("Clipboard is unavailable. Try copying the command proposal again.");
            return;
        }

        AddActivity("Copied", "Command proposal copied.");
        UpdateStatus("Command proposal copied.");
    }

    private void ToggleAutoApproveForSession()
    {
        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before changing Full Access.");
            return;
        }

        autoApproveCommandsForSession = !autoApproveCommandsForSession;
        autoRescueAttemptsRemaining = autoApproveCommandsForSession ? MaxAutoRescueAttempts : 0;
        if (!autoApproveCommandsForSession && autoContinueForSession)
        {
            PauseAutoContinue("Full Access was disabled.");
        }

        RefreshAutoApproveAction();
        AddActivity(
            autoApproveCommandsForSession ? "Full Access on" : "Full Access off",
            autoApproveCommandsForSession
                ? "Only literal Agent commands with a proven physical workspace boundary will run automatically for this session."
                : "Agent commands will wait for explicit Approval.");
        AddCenterMessage(
            autoApproveCommandsForSession ? "Full Access enabled" : "Full Access disabled",
            autoApproveCommandsForSession
                ? "Full Access is on for this workspace session. Only narrowly parsed commands with literal paths inside the physical workspace can run without another click; unparsed commands still require explicit Approval. Working-directory preview validation, blocked previews, and loop guards remain active."
                : "Approval mode is active. Agent commands will wait in the Approval rail until you explicitly approve them.",
            autoApproveCommandsForSession ? "Action" : "Status");
        SetBuildEvidenceSummary(autoApproveCommandsForSession
            ? "Full Access enabled for this workspace session; working-directory preview validation remains active."
            : "Full Access disabled; explicit Approval is required.");
        UpdateStatus(autoApproveCommandsForSession
            ? "Full Access enabled for this workspace session."
            : "Full Access disabled.");
        _ = TryAutoRunPendingPreviewAsync("Full Access was enabled with a preview-ready command.");
    }

    private void ToggleAutoContinueForSession()
    {
        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before changing Auto Continue.");
            return;
        }

        if (!autoContinueForSession)
        {
            autoContinueForSession = true;
            autoContinueRemainingSteps = MaxAutoContinueSteps;
            consecutiveAutoContinueNoChangeResults = 0;
            autoRescueAttemptsRemaining = MaxAutoRescueAttempts;
            if (!autoApproveCommandsForSession)
            {
                autoApproveCommandsForSession = true;
                AddActivity("Full Access on", "Auto Continue enabled command autonomy for this workspace session.");
            }

            AddActivity("Auto Continue on", $"{autoContinueRemainingSteps.ToString(CultureInfo.InvariantCulture)} follow-up steps available.");
            AddCenterMessage(
                "Auto Continue enabled",
                "Agent will ask for follow-up commands after each command result and Full Access will run preview-ready proposals. The loop budget, duplicate-command guard, no-change guard, blocked previews, cancellations, and workspace changes still pause autonomy.",
                "Action");
            SetBuildEvidenceSummary("Auto Continue enabled; follow-up commands run only after preview validation.");
            UpdateStatus("Auto Continue enabled for this workspace session.");
        }
        else
        {
            PauseAutoContinue("Auto Continue disabled by user.");
            SetBuildEvidenceSummary("Auto Continue disabled; use Next Step manually when ready.");
            UpdateStatus("Auto Continue disabled.");
        }

        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshProviderState();
        if (autoContinueForSession)
        {
            _ = TryAutoRunPendingPreviewAsync("Auto Continue was enabled with a preview-ready command.");
        }
    }

    private async Task TryAutoRunPendingPreviewAsync(string reason)
    {
        if (!autoApproveCommandsForSession
            || pendingPreview is null
            || isRunningChat
            || isRunningCommand)
        {
            return;
        }

        var preview = pendingPreview;
        if (RequiresManualApprovalUnderAutonomy(preview, out var riskReason))
        {
            AddActivity("Manual approval needed", riskReason);
            commandStatusText.Text = "Full Access paused for a risky preview. Review and approve manually.";
            approvalText.Text = $"{riskReason}{Environment.NewLine}{Environment.NewLine}{preview.DisplayInvocation}";
            SetBuildEvidenceSummary("Full Access paused for manual review of a risky preview.");
            UpdateStatus("Full Access paused for manual command review.");
            RefreshOutputs();
            RefreshProviderState();
            return;
        }

        if (!AgentWorkspaceCommand.TryCreateAutomaticExecutionPreview(preview, out var automaticPreview, out var automaticReason))
        {
            AddActivity("Manual approval needed", automaticReason);
            commandStatusText.Text = "Full Access paused because the execution boundary could not be materialized safely.";
            approvalText.Text = $"{automaticReason}{Environment.NewLine}{Environment.NewLine}{preview.DisplayInvocation}";
            SetBuildEvidenceSummary("Full Access paused for manual review of an unmaterialized command.");
            UpdateStatus("Full Access paused for manual command review.");
            RefreshOutputs();
            RefreshProviderState();
            return;
        }

        if (automaticPreview.ApprovalKey != preview.ApprovalKey)
        {
            preview = automaticPreview;
            pendingPreview = automaticPreview;
            suppressCommandPreviewInvalidation = true;
            try
            {
                commandText.Text = automaticPreview.Command;
            }
            finally
            {
                suppressCommandPreviewInvalidation = false;
            }
        }

        if (autoContinueForSession && ShouldPauseAutoRunForLoopHealth(preview, out var loopGuardReason))
        {
            PauseAutonomyForLoopGuard(loopGuardReason);
            return;
        }

        AddActivity("Full Access", reason);
        commandStatusText.Text = "Full Access active: running preview-ready command...";
        SetBuildEvidenceSummary("Full Access is running a preview-ready command.");
        UpdateStatus("Full Access running Agent command...");
        try
        {
            await RunApprovedCommandAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            RunOnUiThread(() =>
            {
                commandStatusText.Text = $"Full Access failed: {ex.Message}";
                SetBuildEvidenceSummary("Full Access failed before command completion.");
                AddActivity("Full Access failed", ex.Message);
                UpdateStatus("Full Access command failed before completion.");
            });
        }
    }

    private static bool RequiresManualApprovalUnderAutonomy(AgentCommandPreview preview, out string reason)
    {
        var risky = preview.Risks
            .Where(IsManualApprovalRisk)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (risky.Length > 0)
        {
            reason = $"Full Access will not auto-run this preview because it is flagged: {string.Join(", ", risky)}.";
            return true;
        }

        if (!AgentWorkspaceCommand.CanRunAutomatically(preview, out var boundaryReason))
        {
            reason = $"Full Access will not auto-run this preview because its workspace boundary cannot be proven. {boundaryReason}";
            return true;
        }

        reason = "";
        return false;
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CancellationTokenSource? profileCancellation;
        lock (workspaceProfileSync)
        {
            disposed = true;
            workspaceProfileRefreshVersion++;
            profileCancellation = workspaceProfileCancellation;
            workspaceProfileCancellation = null;
        }

        TryCancel(profileCancellation);
        TryCancel(chatCancellation);
        TryCancel(commandCancellation);
        AgentWorkspaceCommand.BeginApplicationShutdown();
    }

    private static bool IsManualApprovalRisk(string risk)
    {
        return risk.Equals("Destructive", StringComparison.OrdinalIgnoreCase)
            || risk.Equals("Network/install", StringComparison.OrdinalIgnoreCase)
            || risk.Equals("Long-running", StringComparison.OrdinalIgnoreCase)
            || risk.Equals("Elevated", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryAutoContinueAfterCommandAsync(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        if (!autoContinueForSession)
        {
            return;
        }

        if (result.Canceled)
        {
            PauseAutoContinue("Command was cancelled.");
            return;
        }

        if (!autoApproveCommandsForSession)
        {
            PauseAutoContinue("Approval mode is active.");
            return;
        }

        if (autoContinueRemainingSteps <= 0)
        {
            PauseAutoContinue("Follow-up budget reached.");
            return;
        }

        if (ShouldPauseAutoContinueAfterResult(result, receipt, out var loopGuardReason))
        {
            PauseAutonomyForLoopGuard(loopGuardReason);
            return;
        }

        if (isRunningChat || isRunningCommand || pendingPreview is not null)
        {
            return;
        }

        autoContinueRemainingSteps--;
        RunOnUiThread(() =>
        {
            ClearCommandRailForAutoContinue();
            promptText.Text = BuildAutoContinuePrompt(result, receipt);
            promptText.CaretIndex = promptText.Text.Length;
            RefreshPromptBudget();
            AddActivity("Auto Continue", AgentAutonomyPolicyService.FollowUpActivityDetail(autoContinueRemainingSteps));
            SetBuildEvidenceSummary("Auto Continue is asking for the next command based on terminal output.");
            UpdateStatus("Auto Continue asking Agent for the next step...");
            RefreshAutoContinueAction();
        });

        await SendOnUiThreadAsync();
        if (autoContinueForSession && autoContinueRemainingSteps <= 0 && !isRunningChat && !isRunningCommand)
        {
            PauseAutoContinue("Follow-up budget reached.");
        }
    }

    private string BuildAutoContinuePrompt(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        return AgentAutonomyPolicyService.BuildAutoContinuePrompt(
            result,
            receipt,
            FormatLatestCommandContext(),
            IsLatestArtifactVerificationResult(result));
    }

    private bool ShouldPauseAutoRunForLoopHealth(AgentCommandPreview preview, out string reason)
    {
        var decision = AgentAutonomyPolicyService.EvaluateRepeatedCommand(preview, commandHistory);
        reason = decision.Reason;
        return decision.ShouldPause;
    }

    private bool ShouldPauseAutoContinueAfterResult(AgentCommandResult result, AgentWorkspaceFileReceipt receipt, out string reason)
    {
        var policy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
            result,
            receipt,
            consecutiveAutoContinueNoChangeResults,
            currentPromptRequiresCommand,
            IsLatestArtifactVerificationResult(result),
            SuccessfulNoChangeIsExpected(result));
        consecutiveAutoContinueNoChangeResults = policy.NextConsecutiveNoChangeResults;
        reason = policy.Reason;
        return policy.ShouldPause;
    }

    private void PauseAutonomyForLoopGuard(string reason)
    {
        autoContinueForSession = false;
        autoContinueRemainingSteps = 0;
        consecutiveAutoContinueNoChangeResults = 0;
        autoApproveCommandsForSession = false;
        autoRescueAttemptsRemaining = 0;
        AddActivity("Loop guard", reason);
        publishControlEvent("loop.guard.paused", "Agent loop guard paused autonomy.", new { reason });
        commandStatusText.Text = "Loop guard paused autonomy. Review the staged command before approving.";
        approvalText.Text = string.IsNullOrWhiteSpace(pendingPreview?.DisplayInvocation)
            ? reason
            : $"{reason}{Environment.NewLine}{Environment.NewLine}{pendingPreview.DisplayInvocation}";
        SetBuildEvidenceSummary(reason);
        UpdateStatus("Loop guard paused Agent autonomy.");
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshProviderState();
    }

    private static bool CommandsEquivalent(string left, string right)
    {
        return AgentCommandResultService.CommandsEquivalent(left, right);
    }

    private static string NormalizeCommandForLoopComparison(string command)
    {
        return AgentCommandResultService.NormalizeCommandForLoopComparison(command);
    }

    private Task SendOnUiThreadAsync()
    {
        if (dispatcher.CheckAccess())
        {
            return SendAsync();
        }

        return dispatcher.InvokeAsync(() => SendAsync()).Task.Unwrap();
    }

    private void ClearCommandRailForAutoContinue()
    {
        pendingPreview = null;
        stagedArtifactSuggestion = null;
        riskItems.Children.Clear();
        runButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        SetCommandSource("Auto Continue waiting for next proposal");
        approvalText.Text = "Auto Continue is asking Builder for the next previewable command.";
        commandStatusText.Text = "Auto Continue preparing next step...";
    }

    private void ClearCommandRailForRescueReplacement()
    {
        pendingPreview = null;
        stagedArtifactSuggestion = null;
        riskItems.Children.Clear();
        runButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        SetCommandSource("Auto Rescue replacing stale command");
        approvalText.Text = "Auto Rescue is replacing a stale command proposal.";
        commandStatusText.Text = "Auto Rescue staging replacement command...";
    }

    private void ClearCommandProposal()
    {
        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before clearing.");
            return;
        }

        pendingPreview = null;
        stagedArtifactSuggestion = null;
        suppressCommandPreviewInvalidation = true;
        try
        {
            commandText.Clear();
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        SetCommandSource("Manual command");
        InvalidatePreview("Command cleared. Preview required before any command can run.");
        AddActivity("Cleared", "Command proposal cleared.");
        SetBuildEvidenceSummary("Command cleared. Preview required before any command can run.");
        RefreshOutputs();
    }

    private void StageHeldCommandProposal()
    {
        if (heldCommandSuggestion is null)
        {
            UpdateStatus("No held command proposal to stage.");
            return;
        }

        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before staging held proposal.");
            return;
        }

        SetCommandSource("Held Builder proposal");
        stagedArtifactSuggestion = null;
        suppressCommandPreviewInvalidation = true;
        try
        {
            SelectShell(heldCommandSuggestion.Shell);
            commandText.Text = heldCommandSuggestion.Command;
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        heldCommandSuggestion = null;
        RefreshHeldCommandAction();
        PreviewCommand();
        AddActivity("Held staged", "Held Builder command proposal staged.");
        AddCenterMessage("Held command staged", "The held Builder proposal is now in the approval rail. Review the preview, then Approve or Reject it.", "Action");
        SetBuildEvidenceSummary("Held Builder proposal staged for approval.");
    }

    private void RefreshOutputActions()
    {
        copyOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(outputText.Text)
            && !outputText.Text.Equals("No command output.", StringComparison.Ordinal);
        copyReceiptButton.IsEnabled = lastFileReceipt is not null;
    }

    private void RefreshWorkSummary()
    {
        if (lastCommandResult is null || lastFileReceipt is null)
        {
            workSummaryText.Text = "No command result yet. Run an approved command to produce a brief.";
            workSummaryText.ToolTip = workSummaryText.Text;
            copyBriefButton.IsEnabled = false;
            stageNextButton.Content = "Stage Next";
            stageNextButton.IsEnabled = false;
            stageNextButton.ToolTip = "No command result yet.";
            AutomationProperties.SetName(stageNextButton, "Stage Agent next-step prompt");
            AutomationProperties.SetHelpText(stageNextButton, "Stages a result-aware follow-up or repair prompt from the latest command output.");
            stageVerifyButton.IsEnabled = false;
            stageArtifactButton.IsEnabled = false;
            stageArtifactButton.ToolTip = "No generated artifact suggestion yet.";
            RefreshOutputs();
            return;
        }

        var nextAction = CommandNextAction(lastCommandResult, lastFileReceipt);
        var followUp = ResultFollowUpDescriptor(lastCommandResult, lastFileReceipt);
        workSummaryText.Text = BuildWorkSummaryLine(lastCommandResult, lastFileReceipt, nextAction, latestArtifactSuggestion, latestArtifactVerification);
        workSummaryText.ToolTip = workSummaryText.Text;
        lastWorkBrief = BuildWorkBrief(
            lastOperatorPrompt,
            FormatAutonomyContext(),
            lastCommandResult,
            lastFileReceipt,
            commandHistory,
            nextAction,
            latestArtifactSuggestion,
            latestArtifactVerification);
        copyBriefButton.IsEnabled = true;
        stageNextButton.Content = followUp.ButtonLabel;
        stageNextButton.IsEnabled = !isRunningChat && !isRunningCommand;
        stageNextButton.ToolTip = followUp.ToolTip;
        AutomationProperties.SetName(stageNextButton, followUp.ButtonLabel switch
        {
            "Stage Repair" => "Stage Agent repair prompt",
            "Stage Retry" => "Stage Agent retry prompt",
            _ => "Stage Agent next-step prompt"
        });
        AutomationProperties.SetHelpText(stageNextButton, followUp.ToolTip);
        stageVerifyButton.IsEnabled = !isRunningChat && !isRunningCommand;
        stageArtifactButton.IsEnabled = latestArtifactSuggestion is not null && !isRunningChat && !isRunningCommand;
        stageArtifactButton.ToolTip = latestArtifactSuggestion is null
            ? "No generated artifact suggestion yet."
            : $"{latestArtifactSuggestion.Summary}\n{latestArtifactSuggestion.Command}";
        RefreshOutputs();
    }

    private void StageArtifactSuggestionCommand()
    {
        if (latestArtifactSuggestion is null)
        {
            UpdateStatus("No generated artifact suggestion to stage.");
            return;
        }

        if (isRunningCommand)
        {
            UpdateStatus("Wait for the active command before staging the artifact command.");
            return;
        }

        if (isRunningChat)
        {
            UpdateStatus("Wait for active Agent work before staging the artifact command.");
            return;
        }

        if (pendingPreview is not null)
        {
            UpdateStatus("Review or reject the staged preview before using the artifact command.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(commandText.Text)
            && (lastCommandResult is null || !CommandsEquivalent(commandText.Text, lastCommandResult.Command)))
        {
            UpdateStatus("Clear or review the current command before using the artifact command.");
            return;
        }

        var artifact = latestArtifactSuggestion;
        if (!ArtifactEntryExists(workspacePath, artifact))
        {
            AddActivity("Artifact missing", artifact.EntryPath);
            AddCenterMessage(
                "Artifact command not staged",
                $"{artifact.Summary}\n\nThe artifact entry was not found in the selected workspace anymore. Run or stage verification again after regenerating it.",
                "Warning");
            SetBuildEvidenceSummary("Artifact suggestion was not staged because the file is missing.");
            UpdateStatus("Generated artifact is missing. Re-run or verify the latest work.");
            RefreshWorkSummary();
            return;
        }

        SetCommandSource($"{artifact.Kind} artifact suggestion");
        stagedArtifactSuggestion = artifact;
        suppressCommandPreviewInvalidation = true;
        try
        {
            SelectShell(artifact.Shell);
            commandText.Text = artifact.Command;
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        PreviewCommand();
        if (pendingPreview is null && !isRunningCommand)
        {
            stagedArtifactSuggestion = null;
        }

        AddActivity(
            pendingPreview is null ? "Artifact blocked" : "Artifact staged",
            pendingPreview is null
                ? $"{artifact.Kind} artifact command was blocked by preview validation."
                : $"{artifact.Kind} artifact command staged for approval.");
        AddCenterMessage(
            pendingPreview is null ? "Artifact command blocked" : "Artifact command staged",
            pendingPreview is null
                ? $"{artifact.Summary}\n\nThe suggested command did not pass preview validation. Review the approval rail for the exact blocker."
                : $"{artifact.Summary}\n\nThe suggested {artifact.Shell} command is staged in the approval rail. Review the preview, then Approve to run it or Reject to clear it.",
            pendingPreview is null ? "Warning" : "Action");
        SetBuildEvidenceSummary(pendingPreview is null
            ? "Artifact suggestion was blocked by preview validation."
            : "Artifact suggestion staged for approval.");
        RefreshWorkSummary();
    }

    private static bool ArtifactEntryExists(string root, AgentArtifactSuggestion artifact)
    {
        return AgentArtifactService.ArtifactEntryExists(root, artifact);
    }

    private void RefreshHeldCommandAction()
    {
        useHeldCommandButton.IsEnabled = heldCommandSuggestion is not null && !isRunningCommand;
        RefreshBuildEvidence();
    }

    private void RefreshAutoApproveAction()
    {
        approveAllButton.Content = autoApproveCommandsForSession ? "Full Access" : "Approval";
        approveAllButton.ToolTip = autoApproveCommandsForSession
            ? "Click to stop auto-running preview-ready Agent commands for this workspace session."
            : "Approval mode is active. Click to enable Full Access for preview-ready Agent commands in this workspace session.";
        AutomationProperties.SetName(approveAllButton, autoApproveCommandsForSession
            ? "Full Access mode for Agent commands"
            : "Approval mode for Agent commands");
        AutomationProperties.SetHelpText(approveAllButton, autoApproveCommandsForSession
            ? "Turn off Full Access. Commands will wait for explicit Approval again."
            : "Auto-run preview-ready Agent commands for this workspace session while preserving working-directory preview validation and blocked-preview stops.");
        var rescueRetryText = autoRescueAttemptsRemaining == 1 ? "retry" : "retries";
        approveAllStatusText.Text = autoApproveCommandsForSession
            ? $"Full Access is on for {ShortWorkspaceName(workspacePath)}. Only literal commands with a proven physical workspace boundary run automatically; prose-only app replies get {autoRescueAttemptsRemaining.ToString(CultureInfo.InvariantCulture)} Auto Rescue {rescueRetryText}; blocked previews, unparsed commands, loop guards, and workspace changes still stop autonomy."
            : "Approval mode. Preview-ready commands wait for explicit approval.";
        approveAllStatusText.ToolTip = approveAllStatusText.Text;
    }

    private void PauseAutoContinue(string reason)
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => PauseAutoContinue(reason));
            return;
        }

        if (!autoContinueForSession && autoContinueRemainingSteps == 0)
        {
            return;
        }

        autoContinueForSession = false;
        autoContinueRemainingSteps = 0;
        consecutiveAutoContinueNoChangeResults = 0;
        AddActivity("Auto Continue off", reason);
        RefreshAutoContinueAction();
        RefreshProviderState();
    }

    private void RefreshAutoContinueAction()
    {
        autoContinueButton.Content = autoContinueForSession ? "Loop: On" : "Auto Continue";
        autoContinueButton.ToolTip = autoContinueForSession
            ? "Agent will ask for follow-up commands after each command result until the budget is spent or a preview blocks."
            : "Enable a bounded follow-up loop after command output.";
        AutomationProperties.SetHelpText(autoContinueButton, autoContinueForSession
            ? "Turn off the bounded follow-up loop. Full Access remains available separately."
            : "Automatically ask for follow-up Agent commands after command output while preserving working-directory preview validation and loop guards.");
        autoContinueStatusText.Text = autoContinueForSession
            ? $"Auto Continue is on: {autoContinueRemainingSteps.ToString(CultureInfo.InvariantCulture)} follow-up step{(autoContinueRemainingSteps == 1 ? "" : "s")} left. Full Access is on; blocked previews, repeats, no-change loops, and workspace changes still pause."
            : "Auto Continue is off. Agent waits for Next Step after command output.";
        autoContinueStatusText.ToolTip = autoContinueStatusText.Text;
    }

    private void StartCommandHistory(AgentCommandPreview preview)
    {
        var item = new AgentCommandHistoryItem(
            nextCommandHistoryId++,
            DateTimeOffset.Now,
            preview.Shell,
            preview.Command,
            "Running",
            commandProposalSource,
            "Command started.",
            preview.WorkspacePath,
            "",
            null);
        activeCommandHistoryId = item.Id;
        AddCommandHistoryItem(item);
    }

    private void FinishCommandHistory(AgentCommandResult result, AgentWorkspaceFileReceipt receipt)
    {
        var status = result.Ok
            ? $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : result.Canceled
                ? "Cancelled"
                : result.TimedOut
                    ? "Timed out"
                    : $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}";
        var detail = $"{FormatElapsed(result.Elapsed)} | {receipt.Summary}";
        var index = activeCommandHistoryId is null
            ? -1
            : commandHistory.FindIndex(item => item.Id == activeCommandHistoryId.Value);
        var original = index >= 0 ? commandHistory[index] : null;
        var completed = new AgentCommandHistoryItem(
            activeCommandHistoryId ?? nextCommandHistoryId++,
            original?.CreatedAt ?? DateTimeOffset.Now,
            result.Shell,
            result.Command,
            status,
            original?.Source ?? commandProposalSource,
            detail,
            result.WorkingDirectory,
            receipt.Summary,
            result.ExitCode);
        if (index >= 0)
        {
            commandHistory[index] = completed;
        }
        else
        {
            AddCommandHistoryItem(completed);
        }

        activeCommandHistoryId = null;
        TrimCommandHistory();
        RefreshCommandHistory();
    }

    private void RecordBlockedCommandPreview(AgentCommandPreview preview)
    {
        if (string.IsNullOrWhiteSpace(preview.Command))
        {
            return;
        }

        AddCommandHistoryItem(new AgentCommandHistoryItem(
            nextCommandHistoryId++,
            DateTimeOffset.Now,
            preview.Shell,
            preview.Command,
            "Blocked",
            commandProposalSource,
            string.IsNullOrWhiteSpace(preview.Error) ? "Preview blocked." : preview.Error,
            preview.WorkspacePath,
            "",
            null));
    }

    private void AddCommandHistoryItem(AgentCommandHistoryItem item)
    {
        commandHistory.Insert(0, item);
        TrimCommandHistory();
        RefreshCommandHistory();
    }

    private void TrimCommandHistory()
    {
        if (commandHistory.Count > MaxCommandHistoryItems)
        {
            commandHistory.RemoveRange(MaxCommandHistoryItems, commandHistory.Count - MaxCommandHistoryItems);
        }
    }

    private void RefreshCommandHistory()
    {
        commandHistoryItems.Children.Clear();
        if (commandHistory.Count == 0)
        {
            commandHistorySummaryText.Text = "No commands recorded yet.";
            commandHistoryItems.Children.Add(CreateHintText("Approved, blocked, and auto-run commands will appear here."));
        }
        else
        {
            var latest = commandHistory[0];
            commandHistorySummaryText.Text = $"{commandHistory.Count.ToString(CultureInfo.InvariantCulture)} recent command{(commandHistory.Count == 1 ? "" : "s")} | latest: {latest.Status}";
            foreach (var item in commandHistory)
            {
                commandHistoryItems.Children.Add(CreateCommandHistoryRow(item));
            }
        }

        replayLastCommandButton.IsEnabled = LatestReplayableCommand() is not null && !isRunningCommand && !isRunningChat;
        copyCommandHistoryButton.IsEnabled = commandHistory.Count > 0;
        commandHistorySummaryText.ToolTip = commandHistorySummaryText.Text;
        RefreshBuildEvidence();
    }

    private Border CreateCommandHistoryRow(AgentCommandHistoryItem item)
    {
        var title = new TextBlock
        {
            Text = $"{item.Status} | {item.Shell}",
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var command = new TextBlock
        {
            Text = ShellUiHelpers.Truncate(FirstCommandLine(item.Command), 120, ShellUiHelpers.TruncatedNoticeSuffix),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = item.Command
        };
        var detail = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Detail) ? item.Source : $"{item.Detail} | {item.Source}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(command);
        stack.Children.Add(detail);
        return new Border
        {
            BorderBrush = resourceBrush(item.Status.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
    }

    private TextBlock CreateHintText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private void ReplayLastCommand()
    {
        var item = LatestReplayableCommand();
        if (item is null)
        {
            UpdateStatus("No command history to replay.");
            return;
        }

        if (isRunningChat || isRunningCommand)
        {
            UpdateStatus("Wait for active Agent work before replaying history.");
            return;
        }

        SetCommandSource("Command history replay");
        stagedArtifactSuggestion = null;
        suppressCommandPreviewInvalidation = true;
        try
        {
            SelectShell(item.Shell);
            commandText.Text = item.Command;
        }
        finally
        {
            suppressCommandPreviewInvalidation = false;
        }

        PreviewCommand();
        AddActivity("Replay", "Latest command history item staged.");
        SetBuildEvidenceSummary("Command history replay staged for preview.");
        UpdateStatus(pendingPreview is null ? "History command replay is blocked." : "History command staged for preview.");
    }

    private void CopyCommandHistory()
    {
        if (commandHistory.Count == 0)
        {
            UpdateStatus("No command history to copy.");
            return;
        }

        if (!TrySetClipboardText(BuildCommandHistoryCopyText(commandHistory)))
        {
            UpdateStatus("Clipboard is unavailable. Try copying command history again.");
            return;
        }

        AddActivity("Copied", "Command history copied.");
        UpdateStatus("Command history copied.");
    }

    internal static bool TrySetClipboardText(string text, Action<string>? setText = null)
    {
        return ShellClipboard.TrySetText(text, setText);
    }

    private AgentCommandHistoryItem? LatestReplayableCommand()
    {
        return commandHistory.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Command)
            && !item.Status.Equals("Running", StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstCommandLine(string command)
    {
        return (command ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
    }

    internal static string BuildCommandHistoryCopyText(IReadOnlyList<AgentCommandHistoryItem> history)
    {
        if (history.Count == 0)
        {
            return "No Agent command history.";
        }

        var lines = new List<string>
        {
            "Agent command history"
        };
        foreach (var item in history)
        {
            lines.Add("");
            lines.Add($"[{item.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}] {item.Status} | {item.Shell} | {item.Source}");
            if (!string.IsNullOrWhiteSpace(item.Workspace))
            {
                lines.Add($"Workspace: {item.Workspace}");
            }

            if (item.ExitCode is not null)
            {
                lines.Add($"Exit: {item.ExitCode.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                lines.Add($"Detail: {item.Detail}");
            }

            if (!string.IsNullOrWhiteSpace(item.ReceiptSummary))
            {
                lines.Add($"Files: {item.ReceiptSummary}");
            }

            lines.Add("Command:");
            lines.Add(item.Command);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string FormatCommandHistoryContext()
    {
        if (commandHistory.Count == 0)
        {
            return "No command history yet.";
        }

        return ShellUiHelpers.Truncate(
            string.Join(
                Environment.NewLine,
                commandHistory.Take(5).Select(item => $"- {item.Status} | {item.Shell} | {ShellUiHelpers.Truncate(FirstCommandLine(item.Command), 140, ShellUiHelpers.TruncatedNoticeSuffix)} | {item.Detail}")),
            2000,
            ShellUiHelpers.TruncatedNoticeSuffix);
    }

    private string FormatAutonomyContext()
    {
        if (!autoApproveCommandsForSession)
        {
            return "Manual approval mode: command proposals wait for explicit approval. Working-directory preview validation still applies.";
        }

        var autoContinue = autoContinueForSession
            ? $"Auto Continue is on with {autoContinueRemainingSteps.ToString(CultureInfo.InvariantCulture)} follow-up step{(autoContinueRemainingSteps == 1 ? "" : "s")} left."
            : "Auto Continue is off; commands staged by Agent turns can auto-run, but Agent will not ask for follow-up loops by itself.";
        return $"Full Access is on for this workspace session. Only narrowly parsed literal commands run after physical workspace validation; unparsed commands require explicit approval. Blocked previews, outside-workspace paths, duplicate commands, repeated no-change app commands, cancellations, and workspace changes pause or reset autonomy. Prose-only app replies can be Auto Rescued {autoRescueAttemptsRemaining.ToString(CultureInfo.InvariantCulture)} more time{(autoRescueAttemptsRemaining == 1 ? "" : "s")}. {autoContinue}";
    }

    private void UpdateWorkspaceDisplays(string pathText, string boundaryText)
    {
        workspaceStatusText.Text = string.IsNullOrWhiteSpace(workspacePath)
            ? pathText
            : $"Active: {ShortWorkspaceName(workspacePath)}";
        workspaceStatusText.ToolTip = pathText;
        workspaceBoundaryText.Text = boundaryText;
        workspaceBoundaryText.ToolTip = boundaryText;
        leftWorkspacePathText.Text = string.IsNullOrWhiteSpace(workspacePath) ? "-" : workspacePath;
        leftWorkspacePathText.ToolTip = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath;
        leftBoundaryText.Text = string.IsNullOrWhiteSpace(workspacePath)
            ? "No working directory."
            : "Commands start from this folder after approval.";
    }

    private void InvalidatePreview(string status)
    {
        pendingPreview = null;
        runButton.IsEnabled = false;
        rejectButton.IsEnabled = false;
        approvalText.Text = status;
        commandStatusText.Text = status;
        riskItems.Children.Clear();
        RefreshBuildEvidence();
        RefreshOutputs();
        RefreshProviderState();
    }

    private void SetChatControlsEnabled(bool enabled)
    {
        promptText.IsEnabled = enabled;
        planPromptButton.IsEnabled = enabled;
        breakdownPromptButton.IsEnabled = enabled;
        progressPromptButton.IsEnabled = enabled;
        commandPromptButton.IsEnabled = enabled;
        buildAppPromptButton.IsEnabled = enabled;
        nextStepPromptButton.IsEnabled = enabled;
        verifyPromptButton.IsEnabled = enabled;
        rescueCommandButton.IsEnabled = enabled;
        sendButton.IsEnabled = enabled;
        clearButton.IsEnabled = enabled;
        stopButton.IsEnabled = !enabled;
        approveAllButton.IsEnabled = !isRunningCommand;
        autoContinueButton.IsEnabled = !isRunningCommand;
        workspaceBrowseButton.IsEnabled = enabled && !isRunningCommand;
        workspaceApplyButton.IsEnabled = enabled && !isRunningCommand;
        RefreshCommandActionState();
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        RefreshWorkSummary();
    }

    private void SetCommandControlsEnabled(bool enabled)
    {
        shellPicker.IsEnabled = enabled;
        commandText.IsEnabled = enabled;
        previewButton.IsEnabled = enabled;
        copyCommandButton.IsEnabled = enabled;
        clearCommandButton.IsEnabled = enabled;
        stopCommandButton.IsEnabled = isRunningCommand;
        approveAllButton.IsEnabled = !isRunningCommand;
        autoContinueButton.IsEnabled = !isRunningCommand;
        RefreshHeldCommandAction();
        RefreshCommandActionState();
        RefreshAutoApproveAction();
        RefreshAutoContinueAction();
        workspaceBrowseButton.IsEnabled = enabled && !isRunningChat;
        workspaceApplyButton.IsEnabled = enabled && !isRunningChat;
        RefreshWorkSummary();
    }

    private void RefreshCommandActionState()
    {
        var canUseCommandRail = !isRunningChat && !isRunningCommand && commandText.IsEnabled;
        previewButton.IsEnabled = canUseCommandRail;
        runButton.IsEnabled = canUseCommandRail && pendingPreview is not null;
        rejectButton.IsEnabled = canUseCommandRail && pendingPreview is not null;
    }

    private bool WorkspaceReady()
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            return true;
        }

        UpdateStatus("Choose an Agent workspace first.");
        return false;
    }

    private string SelectedShell()
    {
        return shellPicker.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? item.Content?.ToString() ?? "Terminal"
            : "Terminal";
    }

    private void SelectShell(string shell)
    {
        foreach (var item in shellPicker.Items.OfType<ComboBoxItem>())
        {
            var value = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
            if (AgentWorkspaceCommand.NormalizeShell(value).Equals(AgentWorkspaceCommand.NormalizeShell(shell), StringComparison.OrdinalIgnoreCase))
            {
                shellPicker.SelectedItem = item;
                return;
            }
        }
    }

    private string InitialWorkspaceDirectory()
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            return workspacePath;
        }

        var candidate = workspacePathText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
        {
            return candidate;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static string BuildWorkspaceProfile(string root)
    {
        return WorkspaceScannerService.BuildWorkspaceProfile(root);
    }

    internal static IReadOnlyList<string> DiscoverWorkspaceProfileDirectories(string root)
    {
        return WorkspaceScannerService.DiscoverWorkspaceProfileDirectories(root);
    }

    private static bool IsWorkspaceProfileFile(string relativePath)
    {
        return WorkspaceScannerService.IsWorkspaceProfileFile(relativePath);
    }

    private static string FormatCommandHeader(AgentCommandPreview preview)
    {
        return $"""
            > {preview.Shell} in {preview.WorkspacePath}
            $ {preview.Command}

            Running...
            """;
    }

    private static string FormatCommandResult(AgentCommandResult result)
    {
        var stdout = ShellUiHelpers.Truncate(result.StandardOutput.TrimEnd(), MaxCommandOutputChars / 2, ShellUiHelpers.TruncatedNoticeSuffix);
        var stderr = ShellUiHelpers.Truncate(result.StandardError.TrimEnd(), MaxCommandOutputChars / 2, ShellUiHelpers.TruncatedNoticeSuffix);
        var error = string.IsNullOrWhiteSpace(result.Error) ? "" : $"\nError: {result.Error}";
        return $"""
            > {result.Shell} in {result.WorkingDirectory}
            $ {result.Command}

            Exit: {result.ExitCode}
            Elapsed: {FormatElapsed(result.Elapsed)}
            Timed out: {(result.TimedOut ? "yes" : "no")}
            Cancelled: {(result.Canceled ? "yes" : "no")}{error}

            STDOUT
            {DisplayOutput(stdout)}

            STDERR
            {DisplayOutput(stderr)}
            """;
    }

    private static string FormatCommandContext(AgentCommandResult result)
    {
        return ShellUiHelpers.Truncate($"""
            Shell: {result.Shell}
            Working directory: {result.WorkingDirectory}
            Command: {result.Command}
            Exit: {result.ExitCode}
            Timed out: {(result.TimedOut ? "yes" : "no")}
            Cancelled: {(result.Canceled ? "yes" : "no")}
            STDOUT:
            {result.StandardOutput}
            STDERR:
            {result.StandardError}
            """, 5000, ShellUiHelpers.TruncatedNoticeSuffix);
    }

    private string FormatLatestCommandContext()
    {
        if (lastCommandResult is null)
        {
            return "No command output captured yet.";
        }

        var receipt = lastFileReceipt is null ? "" : $"{Environment.NewLine}{Environment.NewLine}{FormatFileReceipt(lastFileReceipt)}";
        return ShellUiHelpers.Truncate($"{FormatCommandContext(lastCommandResult)}{receipt}", 6500, ShellUiHelpers.TruncatedNoticeSuffix);
    }

    private static Task<AgentWorkspaceFileSnapshot> CaptureWorkspaceFilesAsync(string root, CancellationToken cancellationToken)
    {
        return WorkspaceScannerService.CaptureWorkspaceFilesAsync(root, cancellationToken);
    }

    internal static AgentWorkspaceFileSnapshot CaptureWorkspaceFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        return WorkspaceScannerService.CaptureWorkspaceFiles(root, cancellationToken);
    }

    private AgentWorkspaceFileSnapshot ExcludeInternalStateFiles(string root, AgentWorkspaceFileSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(settingsStore.SettingsPath)
            || !AgentWorkspaceCommand.IsInsideWorkspace(root, settingsStore.SettingsPath))
        {
            return snapshot;
        }

        var relative = Path.GetRelativePath(root, settingsStore.SettingsPath).Replace('\\', '/');
        if (!snapshot.Files.ContainsKey(relative))
        {
            return snapshot;
        }

        var files = new SortedDictionary<string, AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Files)
        {
            files[item.Key] = item.Value;
        }

        files.Remove(relative);
        return new AgentWorkspaceFileSnapshot(files, snapshot.ScannedLimit);
    }

    internal static bool ShouldSkipWorkspaceReceiptDirectory(string directory, FileAttributes attributes)
    {
        return WorkspaceScannerService.ShouldSkipWorkspaceReceiptDirectory(directory, attributes);
    }

    internal static AgentWorkspaceFileReceipt BuildFileReceipt(
        IReadOnlyDictionary<string, AgentWorkspaceFileStamp> before,
        IReadOnlyDictionary<string, AgentWorkspaceFileStamp> after)
    {
        return WorkspaceScannerService.BuildFileReceipt(before, after);
    }

    internal static AgentWorkspaceFileReceipt BuildFileReceipt(
        AgentWorkspaceFileSnapshot before,
        AgentWorkspaceFileSnapshot after)
    {
        return WorkspaceScannerService.BuildFileReceipt(before, after);
    }

    internal static string FormatFileReceipt(AgentWorkspaceFileReceipt receipt)
    {
        return WorkspaceScannerService.FormatFileReceipt(receipt);
    }

    internal static AgentArtifactSuggestion? InferArtifactSuggestion(string workspaceRoot, AgentWorkspaceFileReceipt receipt)
    {
        return AgentArtifactService.InferArtifactSuggestion(workspaceRoot, receipt);
    }

    internal static string BuildWorkSummaryLine(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        string nextAction,
        AgentArtifactSuggestion? artifactSuggestion = null,
        AgentArtifactVerification? artifactVerification = null)
    {
        return AgentArtifactService.BuildWorkSummaryLine(
            result,
            receipt,
            nextAction,
            artifactSuggestion,
            artifactVerification);
    }

    internal static string BuildWorkBrief(
        string task,
        string autonomy,
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        IReadOnlyList<AgentCommandHistoryItem> history,
        string nextAction,
        AgentArtifactSuggestion? artifactSuggestion = null,
        AgentArtifactVerification? artifactVerification = null)
    {
        return AgentArtifactService.BuildWorkBrief(
            task,
            autonomy,
            result,
            receipt,
            history,
            nextAction,
            artifactSuggestion,
            artifactVerification);
    }

    private static bool ReceiptHasChanges(AgentWorkspaceFileReceipt receipt)
    {
        return WorkspaceScannerService.ReceiptHasChanges(receipt);
    }

    private static bool ReceiptHasKnownNoChanges(AgentWorkspaceFileReceipt receipt)
    {
        return WorkspaceScannerService.ReceiptHasKnownNoChanges(receipt);
    }

    internal static bool ReceiptScanIsLimitedWithoutTrackedChanges(AgentWorkspaceFileReceipt receipt)
    {
        return WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt);
    }

    private static string ReceiptPreviewText(AgentWorkspaceFileReceipt receipt)
    {
        return AgentArtifactService.ReceiptPreviewText(receipt);
    }

    private static string ChangedPathSummary(AgentWorkspaceFileReceipt receipt, int maxPaths)
    {
        return AgentArtifactService.ChangedPathSummary(receipt, maxPaths);
    }

    private static string DisplayOutput(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 10
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{elapsed.TotalMilliseconds:0}ms";
    }

    private void ScrollToEnd()
    {
        dispatcher.BeginInvoke(() => chatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private ProviderPlan ProviderPlanForRole(ArenaViewSnapshot current, string roleId)
    {
        var sharedModel = CleanModel(current.ProviderModel);
        var roleModel = CleanModel(ModelForRole(current, roleId));
        var model = string.IsNullOrWhiteSpace(roleModel) ? sharedModel : roleModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ProviderPlan(null, null);
        }

        // Reasoning models spend output tokens on thinking before public text, so these
        // budgets must absorb a full thinking pass or the step returns empty content.
        var maxTokens = roleId.Equals("builder", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(settings().AgentBuilderMaxTokens, 256, 32768)
            : Math.Clamp(settings().AgentPlannerReviewerMaxTokens, 256, 32768);
        var primary = Config(current, model, maxTokens);
        var fallback = !string.IsNullOrWhiteSpace(sharedModel)
            && !sharedModel.Equals(model, StringComparison.OrdinalIgnoreCase)
            ? Config(current, sharedModel, maxTokens)
            : null;
        if (fallback is null)
        {
            var rescueModel = CleanModel(settings().AgentRescueModel);
            if (!string.IsNullOrWhiteSpace(rescueModel) && !rescueModel.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                fallback = Config(current, rescueModel, maxTokens);
            }
        }

        return new ProviderPlan(primary, fallback);
    }

    private static IReadOnlyList<string> MissingConfiguredModelRoles(ArenaViewSnapshot current)
    {
        var sharedModel = CleanModel(current.ProviderModel);
        var missing = new List<string>();
        foreach (var role in Roles)
        {
            var roleModel = CleanModel(ModelForRole(current, role.RoleId));
            if (string.IsNullOrWhiteSpace(roleModel) && string.IsNullOrWhiteSpace(sharedModel))
            {
                missing.Add(role.Name);
            }
        }

        return missing;
    }

    private static ModelProviderConfig Config(ArenaViewSnapshot current, string model, int maxTokens)
    {
        return new ModelProviderConfig
        {
            BaseUrl = string.IsNullOrWhiteSpace(current.ProviderBaseUrl) || current.ProviderBaseUrl == "-"
                ? ModelProviderDefaults.BaseUrl
                : current.ProviderBaseUrl,
            ApiMode = ModelProviderApiModes.Normalize(current.ProviderApiMode),
            Model = model,
            ApiToken = current.ProviderApiToken,
            Timeout = Math.Clamp(current.ProviderTimeout, 1, 3600),
            Temperature = current.ProviderTemperature <= 0 ? ModelProviderDefaults.Temperature : current.ProviderTemperature,
            MaxOutputTokens = maxTokens,
            ContextLength = current.ProviderContextLength,
            Reasoning = current.ProviderReasoning,
            NativeStatefulChat = current.ProviderNativeStatefulChat,
            NativeIdleTtlSeconds = current.ProviderNativeIdleTtlSeconds
        };
    }

    private static bool CompletionHasUsableText(ModelCompletionResult result)
    {
        return result.Ok && !string.IsNullOrWhiteSpace(result.Text);
    }

    private static bool CompletionIsEmptySuccess(ModelCompletionResult result)
    {
        return result.Ok && string.IsNullOrWhiteSpace(result.Text);
    }

    private static ModelProviderConfig WithReasoningDisabled(ModelProviderConfig config)
    {
        return new ModelProviderConfig
        {
            BaseUrl = config.BaseUrl,
            ApiMode = config.ApiMode,
            ApiToken = config.ApiToken,
            Model = config.Model,
            Timeout = config.Timeout,
            Temperature = config.Temperature,
            MaxOutputTokens = config.MaxOutputTokens,
            ContextLength = config.ContextLength,
            Reasoning = "off",
            NativeStatefulChat = config.NativeStatefulChat,
            NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
            PreviousResponseId = config.PreviousResponseId,
            LastError = config.LastError,
            LastLatencyMs = config.LastLatencyMs,
            LastTestOk = config.LastTestOk,
            Extra = config.Extra
        };
    }

    internal static bool PromptLikelyRequiresCommand(string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) && ActionIntentRegex.IsMatch(prompt);
    }

    internal static bool CommandLooksLikeVerificationOrInspection(string command)
    {
        var normalized = NormalizeCommandForLoopComparison(command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains(" test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("test", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" verify", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("verify", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" build", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("npm test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("npm run test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("npm run build", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pnpm test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pnpm run test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pnpm run build", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("yarn test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("yarn build", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("cargo test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("go test", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("python -m pytest", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pytest", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("test-path", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("get-childitem", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("dir", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ls", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("type ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("get-content", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CommandLooksLikeWorkspaceMutation(string command)
    {
        var normalized = NormalizeCommandForLoopComparison(command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var first = AgentCommandProposalService.FirstCommandToken(command);
        if (first.Equals("Write-Host", StringComparison.OrdinalIgnoreCase)
            || first.Equals("echo", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains(">", StringComparison.Ordinal);
        }

        return first.Equals("New-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("ni", StringComparison.OrdinalIgnoreCase)
            || first.Equals("md", StringComparison.OrdinalIgnoreCase)
            || first.Equals("mkdir", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Set-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("sc", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Add-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Out-File", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Move-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("copy", StringComparison.OrdinalIgnoreCase)
            || first.Equals("xcopy", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("::WriteAllText", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Set-Content", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Add-Content", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Out-File", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("New-Item", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" dotnet new ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("dotnet new ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" npm create ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("npm create ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" npx create-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("npx create-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" pnpm create ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("pnpm create ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" yarn create ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("yarn create ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" cargo new ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("cargo new ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" go mod init", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("go mod init", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(">", StringComparison.Ordinal);
    }

    private static bool PromptIsAutoRescue(string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt)
            && prompt.Contains("Rescue this Agent run", StringComparison.OrdinalIgnoreCase)
            && prompt.Contains("prose-only", StringComparison.OrdinalIgnoreCase);
    }

    private static string CompletionModel(ModelCompletionResult result, ModelProviderConfig config)
    {
        var model = CleanModel(result.Model);
        return string.IsNullOrWhiteSpace(model) ? config.Model : model;
    }

    private static string CompletionError(ModelCompletionResult result)
    {
        if (!result.Ok)
        {
            return string.IsNullOrWhiteSpace(result.Error) ? "Model call failed." : result.Error;
        }

        return "Model returned an empty response.";
    }

    private static string ModelForRole(ArenaViewSnapshot current, string roleId)
    {
        return roleId.ToLowerInvariant() switch
        {
            "planner" => current.ProviderModel,
            "reviewer" => current.ProviderModel,
            "builder" => current.ProviderModel,
            _ => current.ProviderModel
        };
    }

    private static string CleanModel(string value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed == "-" ? "" : trimmed;
    }

    private static string DisplayModel(string value)
    {
        var model = CleanModel(value);
        if (string.IsNullOrWhiteSpace(model))
        {
            return "-";
        }

        var slash = model.LastIndexOf('/');
        return slash >= 0 && slash < model.Length - 1 ? model[(slash + 1)..] : model;
    }

    private static string ShortWorkspaceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var name = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? value : name;
    }

    internal static string TruncateKeepingEnds(string value, int headChars, int tailChars)
    {
        if (value.Length <= headChars + tailChars)
        {
            return value;
        }

        var omitted = value.Length - headChars - tailChars;
        return $"{value[..headChars]}\n[... omitted {omitted.ToString(CultureInfo.InvariantCulture)} chars ...]\n{value[^tailChars..]}";
    }

    internal static AgentFileSuggestion? ExtractFileWriteSuggestion(string text)
    {
        return AgentCommandProposalService.ExtractFileWriteSuggestion(text);
    }

    internal static IReadOnlyList<string> DescribePlannedFileWrites(string command)
    {
        return AgentCommandProposalService.DescribePlannedFileWrites(command);
    }

    internal static string BuildFileWriteCommand(AgentFileSuggestion suggestion)
    {
        return AgentCommandProposalService.BuildFileWriteCommand(suggestion);
    }

    internal static AgentCommandSuggestion? ExtractCommandSuggestion(string text)
    {
        return AgentCommandProposalService.ExtractCommandSuggestion(text);
    }

    internal static AgentCommandSuggestion NormalizeCommandSuggestion(AgentCommandSuggestion suggestion)
    {
        return AgentCommandProposalService.NormalizeCommandSuggestion(suggestion);
    }

    private sealed record AgentWorkspaceRole(string RoleId, string Name, string Instructions);

    private sealed record AgentWelcomeAction(string Label, string TemplateId);

    internal sealed record AgentWorkspaceMessage(string RoleId, string Title, string Body, string Kind, string Model, DateTimeOffset CreatedAt);

    private sealed record AgentEvidenceItem(string Label, string State, string BorderResourceKey);

    internal sealed record AgentOutputItem(string Label, string State, string Detail, string BorderResourceKey);

    private sealed record ProviderPlan(ModelProviderConfig? Primary, ModelProviderConfig? Fallback);

    internal sealed record AgentCommandSuggestion(string Shell, string Command);

    internal sealed record AgentCommandHistoryItem(
        int Id,
        DateTimeOffset CreatedAt,
        string Shell,
        string Command,
        string Status,
        string Source,
        string Detail,
        string Workspace,
        string ReceiptSummary,
        int? ExitCode);

    internal readonly record struct AgentResultFollowUpDescriptor(
        string ButtonLabel,
        string ToolTip,
        string ActivityTitle,
        string ActivityDetail,
        string CardTitle,
        string CardBody,
        string BuildEvidence,
        string Status);

    internal sealed record AgentArtifactSuggestion(
        string Kind,
        string EntryPath,
        string Shell,
        string Command,
        string Summary);

    internal sealed record AgentArtifactVerification(
        string Kind,
        string EntryPath,
        string Shell,
        string Command,
        bool Ok,
        bool Canceled,
        bool TimedOut,
        int ExitCode,
        DateTimeOffset CompletedAt)
    {
        public bool IsPreviewLaunch => AgentArtifactService.IsPreviewLaunch(Kind, Command);

        public string ActionTitle => AgentArtifactService.ActionTitle(this);

        public string EvidenceState => AgentArtifactService.EvidenceState(this);

        public string Summary => AgentArtifactService.Summary(this);

        public static AgentArtifactVerification From(AgentArtifactSuggestion suggestion, AgentCommandResult result)
        {
            return AgentArtifactService.BuildVerification(suggestion, result);
        }
    }

    internal sealed record AgentFileSuggestion(IReadOnlyList<AgentSuggestedFile> Files);

    internal sealed record AgentSuggestedFile(string Path, string Content, string Language);

    internal readonly record struct AgentWorkspaceFileStamp(long Length, DateTime LastWriteTimeUtc);

    internal sealed record AgentWorkspaceFileSnapshot(
        IReadOnlyDictionary<string, AgentWorkspaceFileStamp> Files,
        bool ScannedLimit);

    internal sealed record AgentWorkspaceFileReceipt(
        string Summary,
        IReadOnlyList<string> Created,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted,
        bool ScannedLimit);

    private sealed record AgentStep(
        bool Ok,
        string RoleId,
        string RoleName,
        string Model,
        string Text,
        int LatencyMs,
        int TotalTokens,
        string Error)
    {
        public static AgentStep Completed(string roleId, string roleName, string model, string text, int latencyMs, int totalTokens)
        {
            return new AgentStep(true, roleId, roleName, model, text, latencyMs, totalTokens, "");
        }

        public static AgentStep Failed(string roleId, string roleName, string model, string error)
        {
            return new AgentStep(false, roleId, roleName, model, "", 0, 0, error);
        }
    }
}