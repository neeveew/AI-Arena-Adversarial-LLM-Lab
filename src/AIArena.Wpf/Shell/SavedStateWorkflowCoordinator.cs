using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class SavedStateWorkflowCoordinator
{
    internal sealed record CheckpointRestoreCompletion(
        CheckpointRestoreWithSafetyResult Restore,
        string Outcome,
        bool EventRecorded);

    internal sealed record CheckpointTrashCompletion(
        SavedStateDeletionReceipt? Receipt,
        string Outcome,
        bool EventRecorded);

    internal sealed record SessionTrashCompletion(
        SavedStateDeletionReceipt? Receipt,
        string Outcome);

    internal sealed record DeletedStateUndoCompletion(
        SavedStateRestoreResult Restore,
        string Outcome,
        bool EventRecorded);

    internal sealed record TemplateApplyCompletion(
        SnapshotSafetyCheckpointReceipt SafetyCheckpoint,
        string Outcome,
        bool EventRecorded);

    internal const string TrashRetentionDisclosure =
        "Trash keeps up to 64 items for at most seven days.";

    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ScenarioTemplateStore scenarioTemplateStore;
    private readonly ComboBox modePicker;
    private readonly TextBox nameText;
    private readonly ComboBox itemPicker;
    private readonly CheckBox? showEmptySessionsCheckBox;
    private readonly TextBlock nameLabel;
    private readonly TextBlock itemLabel;
    private readonly TextBlock helpText;
    private readonly TextBlock selectionDetails;
    private readonly TextBlock statusText;
    private readonly Button saveButton;
    private readonly Button loadButton;
    private readonly Button deleteButton;
    private readonly Button forkButton;
    private readonly FrameworkElement forkLineageReceipt;
    private readonly TextBlock forkLineageText;
    private readonly Button openParentButton;
    private readonly SessionForkWorkflowService sessionForkWorkflow;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Func<Task>, Task> runArenaBusyAsync;
    private readonly Func<CoreSessionSummary, bool, Task> loadSessionAsync;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<string> setArenaRunStatus;
    private readonly Action<string> setLoadStatus;

    private IReadOnlyList<CoreSessionSummary> sessionSummaries = [];
    private IReadOnlyList<CheckpointSummary> checkpointSummaries = [];
    private IReadOnlyList<ScenarioTemplate> scenarioTemplates = [];
    private SessionForkLineage? currentForkLineage;
    private PendingSavedStateDeletion? pendingDeletion;
    private bool pendingDeletionUndoArmed;
    private bool isUpdating;

    /// <summary>
    /// When true the picker also lists sessions that never received a turn.
    /// Off by default because a shared data root accumulates them.
    /// </summary>
    private bool showEmptySessions;

    public SavedStateWorkflowCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ScenarioTemplateStore scenarioTemplateStore,
        ComboBox modePicker,
        TextBox nameText,
        ComboBox itemPicker,
        TextBlock nameLabel,
        TextBlock itemLabel,
        TextBlock helpText,
        TextBlock selectionDetails,
        TextBlock statusText,
        Button saveButton,
        Button loadButton,
        Button deleteButton,
        Button forkButton,
        FrameworkElement forkLineageReceipt,
        TextBlock forkLineageText,
        Button openParentButton,
        SessionForkWorkflowService sessionForkWorkflow,
        Func<CoreSessionSummary?> activeSession,
        Func<ThemePalette> theme,
        Func<bool> isRenderingSnapshot,
        Func<bool> isArenaBusy,
        Func<string, Func<Task>, Task> runArenaBusyAsync,
        Func<CoreSessionSummary, bool, Task> loadSessionAsync,
        Func<string?, Task> loadSessionsAsync,
        Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Func<string, Brush> resourceBrush,
        Action<string> setArenaRunStatus,
        Action<string> setLoadStatus,
        CheckBox? showEmptySessionsCheckBox = null)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.scenarioTemplateStore = scenarioTemplateStore;
        this.modePicker = modePicker;
        this.nameText = nameText;
        this.itemPicker = itemPicker;
        this.nameLabel = nameLabel;
        this.itemLabel = itemLabel;
        this.helpText = helpText;
        this.selectionDetails = selectionDetails;
        this.statusText = statusText;
        this.saveButton = saveButton;
        this.loadButton = loadButton;
        this.deleteButton = deleteButton;
        this.forkButton = forkButton;
        this.forkLineageReceipt = forkLineageReceipt;
        this.forkLineageText = forkLineageText;
        this.openParentButton = openParentButton;
        this.sessionForkWorkflow = sessionForkWorkflow;
        this.activeSession = activeSession;
        this.theme = theme;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.isArenaBusy = isArenaBusy;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.loadSessionAsync = loadSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        _ = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.resourceBrush = resourceBrush;
        this.setArenaRunStatus = setArenaRunStatus;
        this.setLoadStatus = setLoadStatus;
        this.showEmptySessionsCheckBox = showEmptySessionsCheckBox;
    }

    public void SetSessions(IReadOnlyList<CoreSessionSummary> sessions)
    {
        sessionSummaries = sessions;
        UpdateLineagePresentation();
    }

    /// <summary>
    /// Sessions worth offering in the picker. A data root can be shared with
    /// other AI Arena implementations, which can leave behind large numbers of
    /// sessions that never received a turn, so empty ones are hidden unless the
    /// operator asks for them. The active and default sessions always stay
    /// visible so the list can never hide what is loaded.
    /// </summary>
    internal static IReadOnlyList<CoreSessionSummary> VisibleSessions(
        IReadOnlyList<CoreSessionSummary> sessions,
        bool includeEmpty,
        string? selectedId)
    {
        if (includeEmpty)
        {
            return sessions;
        }

        var kept = sessions
            .Where(session => session.MessageCount > 0
                || session.Id.Equals("default", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(selectedId) && session.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // Never present an empty picker: if nothing has a transcript yet, the
        // unfiltered list is more useful than none.
        return kept.Length == 0 ? sessions : kept;
    }

    internal static string SessionListStatus(int total, int visible, bool includeEmpty)
    {
        if (includeEmpty || visible >= total)
        {
            return CountLabel(total, "session") + " available.";
        }

        var hidden = total - visible;
        return $"{CountLabel(visible, "session")} available; {hidden} empty hidden.";
    }

    public void ApplyForkLineage(SessionForkLineage? lineage)
    {
        currentForkLineage = lineage;
        UpdateLineagePresentation();
    }

    public async Task ForkCurrentAsync()
    {
        var result = await sessionForkWorkflow.ForkCurrentAsync();
        SetStatus(result.Message, isDanger: !result.Ok);
        setArenaRunStatus(result.Message);
        UpdateActionButtons();
    }

    public async Task OpenParentAsync()
    {
        var parentSessionId = CurrentParentSessionId();
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            SetStatus("This run has no parent lineage to open.", isDanger: true);
            return;
        }

        if (isArenaBusy())
        {
            SetStatus("The arena is busy; the parent run was not opened.", isDanger: true);
            return;
        }

        if (!ParentSessionIsAvailable(parentSessionId))
        {
            SetStatus($"Parent session is no longer available: {parentSessionId}.", isDanger: true);
            UpdateActionButtons();
            return;
        }

        await loadSessionsAsync(parentSessionId);
        var opened = activeSession()?.Id.Equals(parentSessionId, StringComparison.OrdinalIgnoreCase) == true;
        var message = opened
            ? $"Opened parent session: {parentSessionId}."
            : $"Parent session could not be opened: {parentSessionId}.";
        SetStatus(message, isDanger: !opened);
        setArenaRunStatus(message);
        UpdateActionButtons();
    }

    public void LoadScenarioTemplates(string? preferredTemplateId = null)
    {
        scenarioTemplates = scenarioTemplateStore.Load();
        if (!string.IsNullOrWhiteSpace(scenarioTemplateStore.LastLoadWarning))
        {
            SetStatus(scenarioTemplateStore.LastLoadWarning, isDanger: true);
        }

        if (CurrentMode().Equals("template", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePicker(preferredTemplateId);
        }
    }

    public void OnModeSelectionChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        pendingDeletionUndoArmed = false;
        UpdatePicker();
        UpdateActionButtons();
    }

    public void OnItemSelectionChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        pendingDeletionUndoArmed = false;
        UpdateSelectionDetails();
        UpdateActionButtons();
    }

    public async Task SaveAsync()
    {
        if (activeSession() is null)
        {
            SetStatus("No active session.", isDanger: true);
            return;
        }

        switch (CurrentMode())
        {
            case "session":
                await SaveSessionCopyAsync();
                break;
            case "template":
                await SaveScenarioTemplateAsync();
                break;
            default:
                await SaveCheckpointAsync();
                break;
        }
    }

    public async Task LoadAsync()
    {
        switch (CurrentMode())
        {
            case "session":
                await LoadSelectedSessionAsync();
                break;
            case "template":
                await ApplySelectedTemplateAsync();
                break;
            default:
                await RestoreSelectedCheckpointAsync();
                break;
        }
    }

    public async Task DeleteAsync()
    {
        if (ShouldUndoDeletionOnDelete(pendingDeletion?.Receipt, pendingDeletionUndoArmed))
        {
            await UndoPendingDeletionAsync();
            return;
        }

        switch (CurrentMode())
        {
            case "session":
                await DeleteSelectedSessionAsync();
                break;
            case "template":
                await DeleteSelectedTemplateAsync();
                break;
            default:
                await DeleteSelectedCheckpointAsync();
                break;
        }
    }

    public void RefreshCheckpoints()
    {
        _ = RefreshCheckpointsSafelyAsync(activeSession()?.Id);
    }

    private async Task RefreshCheckpointsSafelyAsync(string? capturedSessionId)
    {
        try
        {
            await RefreshCheckpointsAsync();
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(capturedSessionId)
                || ShouldApplyCheckpointRefresh(capturedSessionId, activeSession()?.Id))
            {
                SetStatus(CheckpointRefreshFailureStatus(ex), isDanger: true);
            }
        }
    }

    public async Task RefreshCheckpointsAsync(string? selectedCheckpointId = null)
    {
        var session = activeSession();
        if (session is null)
        {
            ClearCheckpoints("No active session.");
            return;
        }

        var sessionId = session.Id;
        var summaries = await sessionStore.ListCheckpointsAsync(sessionId);
        if (!ShouldApplyCheckpointRefresh(sessionId, activeSession()?.Id))
        {
            return;
        }

        checkpointSummaries = summaries;
        if (CurrentMode().Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePicker(selectedCheckpointId);
            if (checkpointSummaries.Count == 0)
            {
                SetStatus("No checkpoints saved for this session.");
            }
        }
    }

    internal static bool ShouldApplyCheckpointRefresh(string capturedSessionId, string? currentSessionId)
    {
        return !string.IsNullOrWhiteSpace(capturedSessionId)
            && !string.IsNullOrWhiteSpace(currentSessionId)
            && capturedSessionId.Trim().Equals(currentSessionId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static string CheckpointRefreshFailureStatus(Exception ex)
    {
        return AppErrorPresenter.Present(ex, AppErrorContext.SavedState).DisplayText;
    }

    public void ClearCheckpoints(string status)
    {
        checkpointSummaries = [];
        if (CurrentMode().Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePicker();
        }

        SetStatus(status);
    }

    public void SetShowEmptySessions(bool showEmpty)
    {
        if (showEmptySessions == showEmpty)
        {
            return;
        }

        showEmptySessions = showEmpty;
        UpdatePicker(SelectedSessionId());
    }

    private string? SelectedSessionId()
    {
        return itemPicker?.SelectedItem is CoreSessionSummary session ? session.Id : null;
    }

    public void UpdatePicker(string? selectedId = null)
    {
        if (itemPicker is null || modePicker is null)
        {
            return;
        }

        isUpdating = true;
        try
        {
            var mode = CurrentMode();
            itemPicker.ItemsSource = null;
            itemPicker.DisplayMemberPath = "";
            if (showEmptySessionsCheckBox is not null)
            {
                showEmptySessionsCheckBox.Visibility = Visibility.Collapsed;
            }


            switch (mode)
            {
                case "session":
                    nameLabel.Text = "New session name";
                    itemLabel.Text = "Existing session";
                    helpText.Text = "Sessions are working folders. Save copies the current setup into a fresh session, load switches sessions, and delete removes non-default sessions.";
                    saveButton.Content = "Save";
                    loadButton.Content = "Load";
                    nameText.ToolTip = "Enter a unique session name. Existing sessions are not overwritten.";
                    itemPicker.ToolTip = "Choose the session to load or delete.";
                    var visibleSessions = VisibleSessions(sessionSummaries, showEmptySessions, selectedId);
                    itemPicker.ItemsSource = visibleSessions;
                    SelectItem(selectedId, item => item is CoreSessionSummary session ? session.Id : "");
                    SetStatus(SessionListStatus(sessionSummaries.Count, visibleSessions.Count, showEmptySessions));
                    if (showEmptySessionsCheckBox is not null)
                    {
                        // The toggle only makes sense for sessions; templates and
                        // checkpoints have no equivalent empty state.
                        showEmptySessionsCheckBox.Visibility = Visibility.Visible;
                    }

                    break;
                case "template":
                    nameLabel.Text = "Template name";
                    itemLabel.Text = "Saved template";
                    helpText.Text = "Templates save reusable match setup: topic, global prompt, cast, locks, participants, and model assignments. Transcript is not restored.";
                    saveButton.Content = "Save";
                    loadButton.Content = "Load";
                    nameText.ToolTip = "Leave blank for an automatic template name. Matching names ask before overwrite.";
                    itemPicker.ToolTip = "Choose the template to load or delete.";
                    itemPicker.DisplayMemberPath = "Name";
                    itemPicker.ItemsSource = scenarioTemplates;
                    SelectItem(selectedId, item => item is ScenarioTemplate template ? template.Id : "");
                    SetStatus(scenarioTemplates.Count == 0 ? "No templates saved yet." : CountLabel(scenarioTemplates.Count, "template") + " available.");
                    break;
                default:
                    nameLabel.Text = "Checkpoint name";
                    itemLabel.Text = "Saved checkpoint";
                    helpText.Text = "Checkpoints capture the full current session state: transcript, cast, locks, provider settings, notes, diagnostics, and turn order.";
                    saveButton.Content = "Save";
                    loadButton.Content = "Load";
                    nameText.ToolTip = "Leave blank for a timestamped checkpoint name.";
                    itemPicker.ToolTip = "Choose the checkpoint to load or delete.";
                    itemPicker.DisplayMemberPath = "Name";
                    itemPicker.ItemsSource = checkpointSummaries;
                    SelectItem(selectedId, item => item is CheckpointSummary checkpoint ? checkpoint.Id : "");
                    SetStatus(checkpointSummaries.Count == 0 ? "No checkpoints saved for this session." : CountLabel(checkpointSummaries.Count, "checkpoint") + " available.");
                    break;
            }

            UpdateSelectionDetails();
        }
        finally
        {
            isUpdating = false;
        }
    }

    public void UpdateSelectionDetails()
    {
        if (selectionDetails is null || itemPicker is null)
        {
            return;
        }

        var mode = CurrentMode();
        var selected = itemPicker.SelectedItem;
        switch (selected)
        {
            case CoreSessionSummary session:
                selectionDetails.Text = $"Selected session: {session.Id} | {CountLabel(session.MessageCount, "message")}, {CountLabel(session.CheckpointCount, "checkpoint")} | modified {session.LastModified.ToLocalTime():g}.";
                selectionDetails.ToolTip = string.IsNullOrWhiteSpace(session.SnapshotPath)
                    ? "Session has no snapshot yet."
                    : session.SnapshotPath;
                break;
            case ScenarioTemplate template:
                var activeAgents = template.Agents.Count(agent => agent.Active && !agent.Id.Equals("narrator", StringComparison.OrdinalIgnoreCase));
                var lockedItems = template.Agents.Count(agent => agent.Locked) + (template.TopicLocked ? 1 : 0) + (template.GlobalLocked ? 1 : 0);
                selectionDetails.Text = $"Selected template: {template.Name} | {template.MatchType} | {CountLabel(activeAgents, "active agent")} | {CountLabel(lockedItems, "lock")} | saved {template.SavedAt.ToLocalTime():g}.";
                selectionDetails.ToolTip = "Loads match setup only. Transcript stays in the current session.";
                break;
            case CheckpointSummary checkpoint:
                var checkpointTime = DateTimeOffset.FromUnixTimeSeconds(checkpoint.CreatedAt).ToLocalTime();
                selectionDetails.Text = $"Selected checkpoint: {checkpoint.Name} | saved {checkpointTime:g} | restores full session state.";
                selectionDetails.ToolTip = checkpoint.Path;
                break;
            default:
                selectionDetails.Text = mode switch
                {
                    "session" => "No session selected. Save creates a fresh session from the current match setup.",
                    "template" => "No template selected. Save stores reusable match setup without transcript data.",
                    _ => "No checkpoint selected. Save captures the full current session state."
                };
                selectionDetails.ToolTip = null;
                break;
        }

        UpdateActionButtons();
    }

    public void UpdateActionButtons()
    {
        if (loadButton is null
            || deleteButton is null
            || forkButton is null
            || openParentButton is null
            || itemPicker is null)
        {
            return;
        }

        var idle = !isArenaBusy();
        var hasSelection = itemPicker.SelectedItem is not null;
        var selectedDefaultSession = itemPicker.SelectedItem is CoreSessionSummary session
            && session.Id.Equals("default", StringComparison.OrdinalIgnoreCase);
        var parentSessionId = CurrentParentSessionId();
        var parentAvailable = ParentSessionIsAvailable(parentSessionId);
        loadButton.IsEnabled = idle && hasSelection;
        var deletePresentation = DeleteActionPresentation(
            idle,
            hasSelection,
            selectedDefaultSession,
            pendingDeletionUndoArmed ? pendingDeletion?.Receipt : null);
        deleteButton.Content = deletePresentation.Content;
        deleteButton.IsEnabled = deletePresentation.Enabled;
        forkButton.IsEnabled = idle && activeSession() is not null;
        openParentButton.IsEnabled = idle && parentAvailable;
        deleteButton.ToolTip = deletePresentation.HelpText;
        AutomationProperties.SetName(deleteButton, deletePresentation.AutomationName);
        AutomationProperties.SetHelpText(deleteButton, deletePresentation.HelpText);
        var parentHelpText = parentAvailable
            ? $"Open parent session {parentSessionId}"
            : string.IsNullOrWhiteSpace(parentSessionId)
                ? "This run has no parent lineage."
                : $"Parent session {parentSessionId} is no longer available.";
        openParentButton.ToolTip = parentHelpText;
        AutomationProperties.SetHelpText(openParentButton, parentHelpText);
    }

    public async Task RehydratePendingDeletionAsync(CancellationToken cancellationToken = default)
    {
        var receipts = await sessionStore.ListRestorableDeletedStatesAsync(cancellationToken);
        var newest = receipts.FirstOrDefault();
        if (newest is null)
        {
            pendingDeletion = null;
            pendingDeletionUndoArmed = false;
        }
        else
        {
            var wasActiveSession = pendingDeletion is not null
                && pendingDeletion.Receipt.Id.Equals(newest.Id, StringComparison.OrdinalIgnoreCase)
                && pendingDeletion.WasActiveSession;
            pendingDeletion = new PendingSavedStateDeletion(newest, wasActiveSession);
            pendingDeletionUndoArmed = true;
        }

        UpdateActionButtons();
    }

    private void UpdateLineagePresentation()
    {
        var parentSessionId = CurrentParentSessionId();
        if (string.IsNullOrWhiteSpace(parentSessionId) || currentForkLineage is null)
        {
            forkLineageReceipt.Visibility = Visibility.Collapsed;
            forkLineageText.Text = "Fork lineage unavailable.";
            forkLineageText.ToolTip = null;
            UpdateActionButtons();
            return;
        }

        var receipt = $"Forked from {parentSessionId} at turn {currentForkLineage.ParentTurnCount}";
        forkLineageText.Text = receipt;
        forkLineageText.ToolTip = receipt;
        forkLineageReceipt.Visibility = Visibility.Visible;
        UpdateActionButtons();
    }

    private string CurrentParentSessionId()
    {
        return currentForkLineage?.ParentSessionId?.Trim() ?? "";
    }

    private bool ParentSessionIsAvailable(string? parentSessionId)
    {
        return !string.IsNullOrWhiteSpace(parentSessionId)
            && sessionSummaries.Any(session => session.Id.Equals(parentSessionId, StringComparison.OrdinalIgnoreCase));
    }

    public void SetStatus(string status, bool isDanger = false)
    {
        statusText.Text = $"{status} · {DateTime.Now:h:mm tt}";
        statusText.Foreground = isDanger ? resourceBrush("DangerTextBrush") : resourceBrush("MutedTextBrush");
        setLoadStatus("");
    }

    private string CurrentMode()
    {
        return (modePicker?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "checkpoint";
    }

    private void SelectItem(string? selectedId, Func<object, string> idSelector)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            foreach (var item in itemPicker.Items)
            {
                if (idSelector(item).Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    itemPicker.SelectedItem = item;
                    return;
                }
            }
        }

        itemPicker.SelectedIndex = itemPicker.Items.Count > 0 ? 0 : -1;
    }

    private async Task SaveSessionCopyAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            SetStatus("No active session to copy.", isDanger: true);
            return;
        }

        var newSessionId = SessionStore.SafeSessionId(nameText.Text);
        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            SetStatus("Enter a new session name.", isDanger: true);
            return;
        }

        if (sessionSummaries.Any(item => item.Id.Equals(newSessionId, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"Session already exists: {newSessionId}. Choose a different name.", isDanger: true);
            return;
        }

        await runArenaBusyAsync($"Creating session {newSessionId}...", async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                SetStatus($"No snapshot found for session {session.Id}.", isDanger: true);
                return;
            }

            await sessionStore.CreateSessionAsync(newSessionId, snapshot);
            await eventLogStore.AppendAsync(newSessionId, "native_session_created", new { source = session.Id });
            nameText.Clear();
            await loadSessionsAsync(newSessionId);
            SetStatus($"Saved session: {newSessionId}.");
            setArenaRunStatus($"Session: {newSessionId}.");
        });
    }

    private async Task LoadSelectedSessionAsync()
    {
        if (itemPicker.SelectedItem is not CoreSessionSummary session)
        {
            SetStatus("Choose a session to load.", isDanger: true);
            return;
        }

        await loadSessionAsync(session, true);
        UpdatePicker(session.Id);
        SetStatus($"Loaded session: {session.Id}.");
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (itemPicker.SelectedItem is not CoreSessionSummary session)
        {
            SetStatus("Choose a session to delete.", isDanger: true);
            return;
        }

        if (session.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Default session cannot be deleted.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Delete Session",
            $"Move session \"{session.Id}\" to Trash?\n\nYou can undo this from Saved State. {TrashRetentionDisclosure}",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Session delete cancelled.");
            return;
        }

        var wasActive = activeSession()?.Id.Equals(session.Id, StringComparison.OrdinalIgnoreCase) == true;
        var preferredSessionId = PreferredSessionAfterDelete(session.Id, activeSession()?.Id);
        await runArenaBusyAsync($"Moving session {session.Id} to Trash...", async () =>
        {
            var completion = await TrashSessionAndReportAsync(
                sessionStore,
                session.Id,
                preferredSessionId,
                (selectedId, _) => loadSessionsAsync(selectedId));
            if (completion.Receipt is not null)
            {
                pendingDeletion = new PendingSavedStateDeletion(completion.Receipt, wasActive);
                pendingDeletionUndoArmed = true;
            }

            SetStatus(completion.Outcome, isDanger: completion.Receipt is null);
            setArenaRunStatus(statusText.Text);
            UpdateActionButtons();
        });
    }

    internal static async Task<SessionTrashCompletion> TrashSessionAndReportAsync(
        SessionStore sessionStore,
        string sessionId,
        string? preferredSessionId,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(loadSessionsAsync);
        var receipt = await sessionStore.TrashSessionAsync(sessionId, cancellationToken);
        if (receipt is null)
        {
            return new SessionTrashCompletion(
                null,
                $"Could not move session {sessionId} to Trash.");
        }

        var outcome = $"Moved session {sessionId} to Trash. Choose Undo to restore it.";
        var refreshWarning = await TryCompletePostCommitAsync(
            () => loadSessionsAsync(preferredSessionId, CancellationToken.None),
            "the session list could not be refreshed");
        return new SessionTrashCompletion(receipt, AppendCompletionWarning(outcome, refreshWarning));
    }

    private async Task SaveScenarioTemplateAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            SetStatus("No active session.", isDanger: true);
            return;
        }

        var requestedName = nameText.Text.Trim();
        var existingTemplate = string.IsNullOrWhiteSpace(requestedName)
            ? null
            : scenarioTemplates.FirstOrDefault(template => template.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
        if (existingTemplate is not null)
        {
            var replace = ConfirmDialog.Show(
                owner,
                theme(),
                "Replace Template",
                $"Replace template \"{existingTemplate.Name}\"?\n\nThe saved match setup will be overwritten. Transcript data is never stored in templates.",
                "Replace",
                tone: ConfirmDialogTone.Normal);
            if (!replace)
            {
                SetStatus("Template save cancelled.");
                return;
            }
        }

        await runArenaBusyAsync("Saving match template...", async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                SetStatus($"No snapshot found for session {session.Id}.", isDanger: true);
                return;
            }

            var template = scenarioTemplateStore.Save(nameText.Text, snapshot);
            nameText.Clear();
            LoadScenarioTemplates(template.Id);
            SetStatus($"Saved template: {template.Name}.");
            setArenaRunStatus(statusText.Text);
            await eventLogStore.AppendAsync(session.Id, "native_scenario_template_saved", new { template.Id, template.Name });
        });
    }

    private async Task ApplySelectedTemplateAsync()
    {
        var session = activeSession();
        if (session is null || itemPicker.SelectedItem is not ScenarioTemplate template)
        {
            SetStatus("Choose a template to load.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Load Template",
            $"Load template \"{template.Name}\"?\n\nThis replaces the current match framing, cast, locks, participants, and model assignments. The current transcript stays in this session.",
            "Load",
            tone: ConfirmDialogTone.Normal);
        if (!confirm)
        {
            SetStatus("Template load cancelled.");
            return;
        }

        await runArenaBusyAsync($"Applying match template {template.Name}...", async () =>
        {
            var completion = await ApplyTemplateWithSafetyCheckpointAndReportAsync(
                sessionStore,
                eventLogStore,
                session.Id,
                template,
                (outcome, _) => refreshActiveSessionAsync(outcome));
            if (completion is null)
            {
                SetStatus($"No snapshot found for session {session.Id}.", isDanger: true);
                return;
            }

            SetStatus(completion.Outcome);
        });
    }

    internal static async Task<TemplateApplyCompletion?> ApplyTemplateWithSafetyCheckpointAndReportAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        string sessionId,
        ScenarioTemplate template,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventLogStore);
        ArgumentNullException.ThrowIfNull(refreshActiveSessionAsync);
        var safety = await ApplyTemplateWithSafetyCheckpointAsync(
            sessionStore,
            sessionId,
            template,
            cancellationToken);
        if (safety is null)
        {
            return null;
        }

        var evidence = await AppPostCommitEvidence.TryAppendAsync(
            eventLogStore,
            sessionId,
            "native_scenario_template_applied",
            new
            {
                template.Id,
                template.Name,
                safety_checkpoint_id = safety.Checkpoint.Id,
                safety_checkpoint_name = safety.Checkpoint.Name,
                protected_revision = safety.ProtectedRevision,
                replacement_revision = safety.ReplacementRevision
            },
            AppErrorContext.SavedState);
        var refreshOutcome = evidence.AppendTo(
            $"Applied template: {template.Name}. Safety checkpoint: {safety.Checkpoint.Name}.");
        var outcome = evidence.AppendTo(
            $"Loaded template: {template.Name}. Transcript was preserved. Safety checkpoint: {safety.Checkpoint.Name}.");
        var refreshWarning = await TryCompletePostCommitAsync(
            () => refreshActiveSessionAsync(refreshOutcome, CancellationToken.None),
            "the template-applied session could not be refreshed");
        outcome = AppendCompletionWarning(outcome, refreshWarning);
        return new TemplateApplyCompletion(safety, outcome, evidence.Recorded);
    }

    internal static Task<SnapshotSafetyCheckpointReceipt?> ApplyTemplateWithSafetyCheckpointAsync(
        SessionStore sessionStore,
        string sessionId,
        ScenarioTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(template);
        return sessionStore.MutateSnapshotWithSafetyCheckpointAsync(
            sessionId,
            SnapshotSafetyCheckpointOperation.TemplateApply,
            template.Name,
            snapshot =>
            {
                ScenarioTemplateStore.Apply(template, snapshot);
                return snapshot;
            },
            cancellationToken);
    }

    private async Task DeleteSelectedTemplateAsync()
    {
        if (itemPicker.SelectedItem is not ScenarioTemplate template)
        {
            SetStatus("Choose a template to delete.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Delete Template",
            $"Delete template \"{template.Name}\"?\n\nThis removes only the reusable match setup. The current arena state is not changed.",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Template delete cancelled.");
            return;
        }

        var deleted = scenarioTemplateStore.Delete(template.Id);
        var session = activeSession();
        if (deleted && session is not null)
        {
            await eventLogStore.AppendAsync(session.Id, "native_scenario_template_deleted", new { template.Id, template.Name });
        }

        LoadScenarioTemplates();
        SetStatus(deleted ? $"Deleted template: {template.Name}." : "Template delete failed.", isDanger: !deleted);
        setArenaRunStatus(statusText.Text);
    }

    private async Task SaveCheckpointAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            SetStatus("No active session.", isDanger: true);
            return;
        }

        await runArenaBusyAsync("Saving checkpoint...", async () =>
        {
            var checkpoint = await sessionStore.SaveCheckpointAsync(session.Id, nameText.Text);
            await eventLogStore.AppendAsync(session.Id, "native_checkpoint_saved", new { checkpoint.Id, checkpoint.Name });
            nameText.Clear();
            await RefreshCheckpointsAsync(checkpoint.Id);
            SetStatus($"Saved checkpoint: {checkpoint.Name}.");
            setArenaRunStatus(statusText.Text);
        });
    }

    private async Task RestoreSelectedCheckpointAsync()
    {
        var session = activeSession();
        if (session is null || itemPicker.SelectedItem is not CheckpointSummary checkpoint)
        {
            SetStatus("Choose a checkpoint to load.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Load Checkpoint",
            $"Load \"{checkpoint.Name}\"?\n\nThe current arena will return to that saved state, including transcript, cast, locks, and settings.",
            "Load",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Checkpoint load cancelled.");
            return;
        }

        await runArenaBusyAsync($"Loading checkpoint {checkpoint.Name}...", async () =>
        {
            var completion = await RestoreCheckpointWithSafetyCheckpointAndReportAsync(
                sessionStore,
                eventLogStore,
                session.Id,
                checkpoint.Id,
                refreshActiveSessionAsync,
                RefreshCheckpointsAsync);
            if (completion is null)
            {
                SetStatus("Checkpoint load failed.", isDanger: true);
                return;
            }

            SetStatus(completion.Outcome);
        });
    }

    internal static async Task<CheckpointRestoreCompletion?> RestoreCheckpointWithSafetyCheckpointAndReportAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        string sessionId,
        string checkpointId,
        Func<string, Task> refreshActiveSessionAsync,
        Func<string?, Task> refreshCheckpointsAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventLogStore);
        ArgumentNullException.ThrowIfNull(refreshActiveSessionAsync);
        ArgumentNullException.ThrowIfNull(refreshCheckpointsAsync);
        var result = await RestoreCheckpointWithSafetyCheckpointAsync(
            sessionStore,
            sessionId,
            checkpointId,
            cancellationToken);
        if (result is null)
        {
            return null;
        }

        var restored = result.RestoredCheckpoint;
        var safety = result.SafetyCheckpoint;
        // The restore is authoritative at this point. Failure to append its
        // audit event is reported as a secondary warning, never as a failed
        // restore, and cannot prevent either required refresh.
        var evidence = await AppPostCommitEvidence.TryAppendAsync(
            eventLogStore,
            sessionId,
            "native_checkpoint_restored",
            new
            {
                restored.Id,
                restored.Name,
                safety_checkpoint_id = safety?.Checkpoint.Id ?? "",
                safety_checkpoint_name = safety?.Checkpoint.Name ?? "",
                protected_revision = safety?.ProtectedRevision,
                replacement_revision = safety?.ReplacementRevision
            },
            AppErrorContext.SavedState);
        var safetyStatus = safety is null
            ? "No prior live snapshot required a safety checkpoint."
            : $"Safety checkpoint: {safety.Checkpoint.Name}.";
        var outcome = evidence.AppendTo($"Loaded checkpoint: {restored.Name}. {safetyStatus}");
        var activeRefreshWarning = await TryCompletePostCommitAsync(
            () => refreshActiveSessionAsync(outcome),
            "the restored session could not be refreshed");
        outcome = AppendCompletionWarning(outcome, activeRefreshWarning);
        var checkpointRefreshWarning = await TryCompletePostCommitAsync(
            () => refreshCheckpointsAsync(restored.Id),
            "the checkpoint list could not be refreshed");
        outcome = AppendCompletionWarning(outcome, checkpointRefreshWarning);
        return new CheckpointRestoreCompletion(result, outcome, evidence.Recorded);
    }

    internal static Task<CheckpointRestoreWithSafetyResult?> RestoreCheckpointWithSafetyCheckpointAsync(
        SessionStore sessionStore,
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        return sessionStore.RestoreCheckpointWithSafetyCheckpointAsync(
            sessionId,
            checkpointId,
            cancellationToken);
    }

    internal static async Task<CheckpointTrashCompletion> TrashCheckpointAndReportAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        string sessionId,
        string checkpointId,
        string checkpointName,
        Func<string?, CancellationToken, Task> refreshCheckpointsAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(eventLogStore);
        ArgumentNullException.ThrowIfNull(refreshCheckpointsAsync);
        var receipt = await sessionStore.TrashCheckpointAsync(
            sessionId,
            checkpointId,
            checkpointName,
            cancellationToken);
        if (receipt is null)
        {
            await refreshCheckpointsAsync(null, CancellationToken.None);
            return new CheckpointTrashCompletion(
                null,
                "Checkpoint could not be moved to Trash.",
                EventRecorded: false);
        }

        // The payload move has committed. Evidence and projection refresh are
        // secondary completion work and must ignore late caller cancellation.
        var evidence = await AppPostCommitEvidence.TryAppendAsync(
            eventLogStore,
            sessionId,
            "native_checkpoint_deleted",
            new { Id = checkpointId, Name = checkpointName },
            AppErrorContext.SavedState);
        var outcome = evidence.AppendTo(
            $"Moved checkpoint {checkpointName} to Trash. Choose Undo to restore it.");
        var refreshWarning = await TryCompletePostCommitAsync(
            () => refreshCheckpointsAsync(null, CancellationToken.None),
            "the checkpoint list could not be refreshed");
        outcome = AppendCompletionWarning(outcome, refreshWarning);
        return new CheckpointTrashCompletion(receipt, outcome, evidence.Recorded);
    }

    private async Task DeleteSelectedCheckpointAsync()
    {
        var session = activeSession();
        if (session is null || itemPicker.SelectedItem is not CheckpointSummary checkpoint)
        {
            SetStatus("Choose a checkpoint to delete.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Delete Checkpoint",
            $"Move \"{checkpoint.Name}\" to Trash?\n\nThe current arena is unchanged, and you can undo this from Saved State. {TrashRetentionDisclosure}",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Delete cancelled.");
            return;
        }

        await runArenaBusyAsync($"Moving checkpoint {checkpoint.Name} to Trash...", async () =>
        {
            var completion = await TrashCheckpointAndReportAsync(
                sessionStore,
                eventLogStore,
                session.Id,
                checkpoint.Id,
                checkpoint.Name,
                (selectedId, _) => RefreshCheckpointsAsync(selectedId));
            if (completion.Receipt is not null)
            {
                pendingDeletion = new PendingSavedStateDeletion(completion.Receipt, WasActiveSession: false);
                pendingDeletionUndoArmed = true;
            }

            SetStatus(completion.Outcome, isDanger: completion.Receipt is null);
            setArenaRunStatus(statusText.Text);
            UpdateActionButtons();
        });
    }

    private async Task UndoPendingDeletionAsync()
    {
        var pending = pendingDeletion;
        if (pending is null)
        {
            return;
        }

        var receipt = pending.Receipt;
        await runArenaBusyAsync($"Restoring {DeletedItemLabel(receipt)} from Trash...", async () =>
        {
            var preferredSessionId = PreferredSessionAfterUndo(
                receipt.SessionId,
                pending.WasActiveSession,
                activeSession()?.Id);
            var completion = await UndoDeletedStateAndReportAsync(
                sessionStore,
                eventLogStore,
                receipt,
                result =>
                {
                    if (result.Restored
                        || result.Status is SavedStateRestoreStatus.Expired
                            or SavedStateRestoreStatus.NotFound
                            or SavedStateRestoreStatus.Invalid)
                    {
                        pendingDeletion = null;
                        pendingDeletionUndoArmed = false;
                    }
                },
                async (restoredReceipt, _) =>
                {
                    if (restoredReceipt.Kind == SavedStateDeletionKind.Session)
                    {
                        await loadSessionsAsync(preferredSessionId);
                    }
                    else if (activeSession()?.Id.Equals(
                                 restoredReceipt.SessionId,
                                 StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await RefreshCheckpointsAsync(restoredReceipt.CheckpointId);
                    }
                },
                RehydratePendingDeletionAsync);

            SetStatus(completion.Outcome, isDanger: !completion.Restore.Restored);
            setArenaRunStatus(statusText.Text);
            UpdateActionButtons();
        });
    }

    internal static async Task<DeletedStateUndoCompletion> UndoDeletedStateAndReportAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        SavedStateDeletionReceipt receipt,
        Action<SavedStateRestoreResult> applyRestoreResult,
        Func<SavedStateDeletionReceipt, CancellationToken, Task> refreshRestoredStateAsync,
        Func<CancellationToken, Task> rehydratePendingDeletionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(eventLogStore);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(applyRestoreResult);
        ArgumentNullException.ThrowIfNull(refreshRestoredStateAsync);
        ArgumentNullException.ThrowIfNull(rehydratePendingDeletionAsync);

        var result = await sessionStore.RestoreDeletedStateAsync(receipt, cancellationToken);
        applyRestoreResult(result);
        var eventRecorded = true;
        var outcome = UndoStatus(result);
        if (result.Restored)
        {
            AppPostCommitEvidenceResult evidence;
            if (receipt.Kind == SavedStateDeletionKind.Session)
            {
                evidence = await AppPostCommitEvidence.TryAppendAsync(
                    eventLogStore,
                    receipt.SessionId,
                    "native_session_delete_undone",
                    new { deletion_id = receipt.Id },
                    AppErrorContext.SavedState);
            }
            else
            {
                evidence = await AppPostCommitEvidence.TryAppendAsync(
                    eventLogStore,
                    receipt.SessionId,
                    "native_checkpoint_delete_undone",
                    new { deletion_id = receipt.Id, checkpoint_id = receipt.CheckpointId },
                    AppErrorContext.SavedState);
            }

            eventRecorded = evidence.Recorded;
            outcome = evidence.AppendTo(outcome);
            var refreshWarning = await TryCompletePostCommitAsync(
                () => refreshRestoredStateAsync(receipt, CancellationToken.None),
                "the restored Saved State view could not be refreshed");
            outcome = AppendCompletionWarning(outcome, refreshWarning);
        }

        var rehydrateWarning = await TryCompletePostCommitAsync(
            () => rehydratePendingDeletionAsync(CancellationToken.None),
            "the remaining Trash Undo state could not be refreshed");
        outcome = AppendCompletionWarning(outcome, rehydrateWarning);
        return new DeletedStateUndoCompletion(result, outcome, eventRecorded);
    }

    private static async Task<string> TryCompletePostCommitAsync(
        Func<Task> completion,
        string failureSummary)
    {
        return await AppPostCommitEvidence.TryCompleteAsync(
            completion,
            failureSummary,
            AppErrorContext.SavedState);
    }

    private static string AppendCompletionWarning(string outcome, string warning) =>
        AppPostCommitEvidence.AppendWarning(outcome, warning);

    internal static string PreferredSessionAfterDelete(string deletedSessionId, string? activeSessionId)
    {
        return string.IsNullOrWhiteSpace(activeSessionId)
               || deletedSessionId.Equals(activeSessionId, StringComparison.OrdinalIgnoreCase)
            ? "default"
            : activeSessionId;
    }

    internal static string PreferredSessionAfterUndo(
        string restoredSessionId,
        bool deletedSessionWasActive,
        string? currentActiveSessionId)
    {
        return deletedSessionWasActive
            ? restoredSessionId
            : string.IsNullOrWhiteSpace(currentActiveSessionId)
                ? "default"
                : currentActiveSessionId;
    }

    internal static bool ShouldUndoDeletionOnDelete(
        SavedStateDeletionReceipt? pendingReceipt,
        bool pendingUndoArmed = true)
    {
        return pendingUndoArmed && pendingReceipt is not null;
    }

    internal static SavedStateDeleteActionPresentation DeleteActionPresentation(
        bool idle,
        bool hasSelection,
        bool selectedDefaultSession,
        SavedStateDeletionReceipt? pendingReceipt)
    {
        if (pendingReceipt is not null)
        {
            return new SavedStateDeleteActionPresentation(
                "Undo",
                idle,
                $"Restore the {DeletedItemLabel(pendingReceipt)} most recently moved to Trash.",
                "Undo saved item deletion");
        }

        return new SavedStateDeleteActionPresentation(
            "Delete",
            idle && hasSelection && !selectedDefaultSession,
            selectedDefaultSession
                ? "Default session cannot be deleted."
                : "Move the selected saved item to Trash.",
            "Delete saved item");
    }

    internal static string UndoStatus(SavedStateRestoreResult result)
    {
        var label = DeletedItemLabel(result.Receipt);
        return result.Status switch
        {
            SavedStateRestoreStatus.Restored => $"Restored {label} from Trash.",
            SavedStateRestoreStatus.NameCollision => $"Could not restore {label}: an item with that name already exists.",
            SavedStateRestoreStatus.Expired => $"Could not restore {label}: its Trash retention period expired.",
            SavedStateRestoreStatus.NotFound => $"Could not restore {label}: it is no longer in Trash.",
            SavedStateRestoreStatus.Invalid => $"Could not restore {label}: its Trash receipt is invalid.",
            _ => $"Could not restore {label} from Trash."
        };
    }

    private static string DeletedItemLabel(SavedStateDeletionReceipt receipt)
    {
        return receipt.Kind == SavedStateDeletionKind.Session
            ? $"session {receipt.DisplayName}"
            : $"checkpoint {receipt.DisplayName}";
    }

    private static string CountLabel(int count, string singular)
    {
        return count == 1 ? $"1 {singular}" : $"{count} {singular}s";
    }

    private sealed record PendingSavedStateDeletion(
        SavedStateDeletionReceipt Receipt,
        bool WasActiveSession);
}

internal sealed record SavedStateDeleteActionPresentation(
    string Content,
    bool Enabled,
    string HelpText,
    string AutomationName);
