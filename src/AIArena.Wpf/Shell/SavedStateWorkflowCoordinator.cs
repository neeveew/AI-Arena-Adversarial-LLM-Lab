using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class SavedStateWorkflowCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ScenarioTemplateStore scenarioTemplateStore;
    private readonly ComboBox modePicker;
    private readonly TextBox nameText;
    private readonly ComboBox itemPicker;
    private readonly TextBlock nameLabel;
    private readonly TextBlock itemLabel;
    private readonly TextBlock helpText;
    private readonly TextBlock selectionDetails;
    private readonly TextBlock statusText;
    private readonly Button saveButton;
    private readonly Button loadButton;
    private readonly Button deleteButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Func<Task>, Task> runArenaBusyAsync;
    private readonly Func<CoreSessionSummary, bool, Task> loadSessionAsync;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<string> setArenaRunStatus;
    private readonly Action<string> setLoadStatus;

    private IReadOnlyList<CoreSessionSummary> sessionSummaries = [];
    private IReadOnlyList<CheckpointSummary> checkpointSummaries = [];
    private IReadOnlyList<ScenarioTemplate> scenarioTemplates = [];
    private bool isUpdating;

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
        Action<string> setLoadStatus)
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
        this.activeSession = activeSession;
        this.theme = theme;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.isArenaBusy = isArenaBusy;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.loadSessionAsync = loadSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.resourceBrush = resourceBrush;
        this.setArenaRunStatus = setArenaRunStatus;
        this.setLoadStatus = setLoadStatus;
    }

    public void SetSessions(IReadOnlyList<CoreSessionSummary> sessions)
    {
        sessionSummaries = sessions;
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

        UpdatePicker();
    }

    public void OnItemSelectionChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        UpdateSelectionDetails();
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
        _ = RefreshCheckpointsAsync();
    }

    public async Task RefreshCheckpointsAsync(string? selectedCheckpointId = null)
    {
        var session = activeSession();
        if (session is null)
        {
            ClearCheckpoints("No active session.");
            return;
        }

        checkpointSummaries = await sessionStore.ListCheckpointsAsync(session.Id);
        if (CurrentMode().Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePicker(selectedCheckpointId);
            if (checkpointSummaries.Count == 0)
            {
                SetStatus("No checkpoints saved for this session.");
            }
        }
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

            switch (mode)
            {
                case "session":
                    nameLabel.Text = "New session name";
                    itemLabel.Text = "Existing session";
                    helpText.Text = "Sessions are working folders. Save copies the current setup into a fresh session, load switches sessions, and delete removes non-default sessions.";
                    saveButton.Content = "SAVE";
                    loadButton.Content = "LOAD";
                    nameText.ToolTip = "Enter a unique session name. Existing sessions are not overwritten.";
                    itemPicker.ToolTip = "Choose the session to load or delete.";
                    itemPicker.ItemsSource = sessionSummaries;
                    SelectItem(selectedId, item => item is CoreSessionSummary session ? session.Id : "");
                    SetStatus(CountLabel(sessionSummaries.Count, "session") + " available.");
                    break;
                case "template":
                    nameLabel.Text = "Template name";
                    itemLabel.Text = "Saved template";
                    helpText.Text = "Templates save reusable match setup: topic, global prompt, cast, locks, participants, and model assignments. Transcript is not restored.";
                    saveButton.Content = "SAVE";
                    loadButton.Content = "LOAD";
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
                    saveButton.Content = "SAVE";
                    loadButton.Content = "LOAD";
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
        if (loadButton is null || deleteButton is null || itemPicker is null)
        {
            return;
        }

        var hasSelection = itemPicker.SelectedItem is not null;
        var selectedDefaultSession = itemPicker.SelectedItem is CoreSessionSummary session
            && session.Id.Equals("default", StringComparison.OrdinalIgnoreCase);
        loadButton.IsEnabled = !isArenaBusy() && hasSelection;
        deleteButton.IsEnabled = !isArenaBusy() && hasSelection && !selectedDefaultSession;
        deleteButton.ToolTip = selectedDefaultSession
            ? "Default session cannot be deleted."
            : "Delete the selected saved item.";
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
            $"Delete session \"{session.Id}\"?\n\nThis removes the session folder and cannot be undone.",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Session delete cancelled.");
            return;
        }

        await runArenaBusyAsync($"Deleting session {session.Id}...", async () =>
        {
            var deleted = await sessionStore.DeleteSessionAsync(session.Id);
            await loadSessionsAsync("default");
            SetStatus(deleted ? $"Deleted session: {session.Id}." : $"Could not delete session: {session.Id}.", isDanger: !deleted);
            setArenaRunStatus(statusText.Text);
        });
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
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                SetStatus($"No snapshot found for session {session.Id}.", isDanger: true);
                return;
            }

            ScenarioTemplateStore.Apply(template, snapshot);
            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_scenario_template_applied", new { template.Id, template.Name });
            await refreshActiveSessionAsync($"Applied template: {template.Name}.");
            SetStatus($"Loaded template: {template.Name}. Transcript was preserved.");
        });
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
            var restored = await sessionStore.RestoreCheckpointAsync(session.Id, checkpoint.Id);
            if (restored is null)
            {
                SetStatus("Checkpoint load failed.", isDanger: true);
                return;
            }

            await eventLogStore.AppendAsync(session.Id, "native_checkpoint_restored", new { restored.Id, restored.Name });
            await refreshActiveSessionAsync($"Loaded checkpoint: {restored.Name}");
            await RefreshCheckpointsAsync(restored.Id);
            SetStatus($"Loaded checkpoint: {restored.Name}.");
        });
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
            $"Delete \"{checkpoint.Name}\"?\n\nThis removes only the saved checkpoint. The current arena state is not changed.",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetStatus("Delete cancelled.");
            return;
        }

        await runArenaBusyAsync($"Deleting checkpoint {checkpoint.Name}...", async () =>
        {
            var deleted = await sessionStore.DeleteCheckpointAsync(session.Id, checkpoint.Id);
            if (deleted)
            {
                await eventLogStore.AppendAsync(session.Id, "native_checkpoint_deleted", new { checkpoint.Id, checkpoint.Name });
            }

            await RefreshCheckpointsAsync();
            SetStatus(deleted ? $"Deleted checkpoint: {checkpoint.Name}." : "Checkpoint delete failed.", isDanger: !deleted);
            setArenaRunStatus(statusText.Text);
        });
    }

    private static string CountLabel(int count, string singular)
    {
        return count == 1 ? $"1 {singular}" : $"{count} {singular}s";
    }
}
