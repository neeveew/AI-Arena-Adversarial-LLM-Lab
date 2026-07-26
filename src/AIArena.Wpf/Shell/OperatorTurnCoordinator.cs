using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed record OperatorInterventionSuggestion(
    string Id,
    string Label,
    string Route,
    string Prompt,
    string Reason);

internal sealed record OperatorDraftAnalysis(
    string Route,
    string RouteLabel,
    string Destination,
    string Visibility,
    int CharacterCount,
    int TokenEstimate,
    string MeterText);

internal sealed class OperatorTurnCoordinator
{
    private const int QuickInterventionCount = 4;

    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly TranscriptService transcriptService;
    private readonly NarratorService narratorService;
    private readonly DiscourseDiagnosticsService discourseDiagnostics;
    private readonly WpfSettingsStore settingsStore;
    private readonly Button publicRouteButton;
    private readonly Button privateRouteButton;
    private readonly Button narratorRouteButton;
    private readonly FrameworkElement privateTargetRow;
    private readonly ComboBox privateTargetPicker;
    private readonly TextBlock privateTargetSummaryText;
    private readonly TextBlock routeHintText;
    private readonly TextBlock meterText;
    private readonly TextBlock quickInterventionHintText;
    private readonly IReadOnlyList<Button> quickInterventionButtons;
    private readonly ComboBox templatePicker;
    private readonly Button useTemplateButton;
    private readonly Button saveTemplateButton;
    private readonly Button deleteTemplateButton;
    private readonly TextBox turnText;
    private readonly Button sendButton;
    private readonly Func<WpfSettings> settings;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<string, System.Windows.Media.Brush> resourceBrush;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;
    private readonly Action<DialogueMessage> speakNarratorMessage;
    private readonly Dictionary<string, Dictionary<string, string>> draftsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> routeBySession = new(StringComparer.OrdinalIgnoreCase);

    private string routeMode = "public";
    private string draftSessionId = "";
    private bool restoringDraft;
    private bool sendInProgress;
    private bool lastBusy;
    private bool lastAutoChatRunning;

    public OperatorTurnCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TranscriptService transcriptService,
        NarratorService narratorService,
        DiscourseDiagnosticsService discourseDiagnostics,
        WpfSettingsStore settingsStore,
        Button publicRouteButton,
        Button privateRouteButton,
        Button narratorRouteButton,
        FrameworkElement privateTargetRow,
        ComboBox privateTargetPicker,
        TextBlock privateTargetSummaryText,
        TextBlock routeHintText,
        TextBlock meterText,
        TextBlock quickInterventionHintText,
        IReadOnlyList<Button> quickInterventionButtons,
        ComboBox templatePicker,
        Button useTemplateButton,
        Button saveTemplateButton,
        Button deleteTemplateButton,
        TextBox turnText,
        Button sendButton,
        Func<WpfSettings> settings,
        Func<CoreSessionSummary?> activeSession,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Func<bool> isRenderingSnapshot,
        Func<string, System.Windows.Media.Brush> resourceBrush,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus,
        Action<DialogueMessage>? speakNarratorMessage = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.transcriptService = transcriptService;
        this.narratorService = narratorService;
        this.discourseDiagnostics = discourseDiagnostics;
        this.settingsStore = settingsStore;
        this.publicRouteButton = publicRouteButton;
        this.privateRouteButton = privateRouteButton;
        this.narratorRouteButton = narratorRouteButton;
        this.privateTargetRow = privateTargetRow;
        this.privateTargetPicker = privateTargetPicker;
        this.privateTargetSummaryText = privateTargetSummaryText;
        this.routeHintText = routeHintText;
        this.meterText = meterText;
        this.quickInterventionHintText = quickInterventionHintText;
        this.quickInterventionButtons = quickInterventionButtons;
        this.templatePicker = templatePicker;
        this.useTemplateButton = useTemplateButton;
        this.saveTemplateButton = saveTemplateButton;
        this.deleteTemplateButton = deleteTemplateButton;
        this.turnText = turnText;
        this.sendButton = sendButton;
        this.settings = settings;
        this.activeSession = activeSession;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.resourceBrush = resourceBrush;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
        this.speakNarratorMessage = speakNarratorMessage ?? (_ => { });
    }

    public void InitializeControls()
    {
        publicRouteButton.Content = CreateCommandContent("\uE716", "Public", 11);
        privateRouteButton.Content = CreateCommandContent("\uE72E", "Private", 11);
        narratorRouteButton.Content = CreateCommandContent("\uE8D4", "Narrator", 11);
        draftSessionId = activeSession()?.Id?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(draftSessionId) && routeBySession.TryGetValue(draftSessionId, out var savedRoute))
        {
            routeMode = savedRoute;
        }
        InitializeOperatorTemplates();
        WireQuickInterventionButtons();
        UpdateRouteUi();
        UpdateQuickInterventions();
        UpdateTurnMeter();
        UpdateBusyState(lastBusy, lastAutoChatRunning);
    }

    public void SetRouteMode(string mode)
    {
        var normalizedRoute = NormalizeOperatorRoute(mode);
        if (!routeMode.Equals(normalizedRoute, StringComparison.OrdinalIgnoreCase))
        {
            CaptureVisibleDraft();
            routeMode = normalizedRoute;
            RememberCurrentRoute();
            RestoreVisibleDraft();
        }
        UpdateRouteUi();
        UpdateTurnMeter();
    }

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        var nextSessionId = snapshot.SessionId?.Trim() ?? "";
        if (!draftSessionId.Equals(nextSessionId, StringComparison.OrdinalIgnoreCase))
        {
            CaptureVisibleDraft();
            draftSessionId = nextSessionId;
            routeMode = routeBySession.TryGetValue(draftSessionId, out var savedRoute)
                ? savedRoute
                : "public";
            RestoreVisibleDraft();
        }

        PopulatePrivateTargetPicker(snapshot);
        UpdateRouteUi();
        UpdateQuickInterventions();
        UpdateTurnMeter();
    }

    public void OnPrivateTargetChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        UpdateRouteUi();
        UpdateTurnMeter();
    }

    public async Task OnTurnTextKeyDownAsync(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        await SendOperatorTurnAsync();
    }

    public void UpdateTurnMeter()
    {
        CaptureVisibleDraft();
        var text = turnText.Text?.Trim() ?? "";
        var analysis = AnalyzeOperatorDraft(routeMode, text, ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all"), lastRenderedSnapshot());
        meterText.Text = analysis.MeterText;
        meterText.ToolTip = OperatorDraftReceiptText(analysis, text);
        AutomationProperties.SetName(meterText, "Operator draft meter");
        AutomationProperties.SetHelpText(meterText, $"{analysis.RouteLabel}: {analysis.Destination}. {analysis.Visibility}");
        meterText.Foreground = analysis.CharacterCount > 0
            ? resourceBrush("OperatorAccentBrush")
            : resourceBrush("MutedTextBrush");
        RefreshTemplateActionState(OperatorInputEnabled(lastBusy, lastAutoChatRunning, sendInProgress));
    }

    public void UpdateBusyState(bool busy, bool autoChatRunning)
    {
        lastBusy = busy;
        lastAutoChatRunning = autoChatRunning;
        var enabled = OperatorInputEnabled(busy, autoChatRunning, sendInProgress);
        sendButton.IsEnabled = enabled;
        turnText.IsEnabled = enabled;
        publicRouteButton.IsEnabled = enabled;
        privateRouteButton.IsEnabled = enabled;
        narratorRouteButton.IsEnabled = enabled;
        privateTargetPicker.IsEnabled = enabled;
        foreach (var button in quickInterventionButtons)
        {
            button.IsEnabled = enabled && button.Tag is OperatorInterventionSuggestion;
        }

        RefreshTemplateActionState(enabled);
    }

    internal static bool OperatorInputEnabled(bool busy, bool autoChatRunning, bool sendInProgress = false)
    {
        return (!busy || autoChatRunning) && !sendInProgress;
    }

    public async Task SendOperatorTurnAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var text = turnText.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            setArenaRunStatus("Operator turn is empty.");
            return;
        }

        if (sendInProgress)
        {
            setArenaRunStatus("Operator turn is already sending.");
            return;
        }

        var mode = routeMode;
        var sent = await RunOperatorSendAsync(() => SendOperatorPromptAsync(session, text, mode));
        if (sent)
        {
            ClearVisibleDraftAfterSuccessfulSend(session.Id, mode, text);
        }
    }

    internal async Task ControlSendAsync(string prompt, string route)
    {
        if (!TryNormalizeOperatorRoute(route, out var normalizedRoute))
        {
            setArenaRunStatus("Operator route must be public, private, or narrator.");
            return;
        }

        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var text = prompt?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            setArenaRunStatus("Operator turn is empty.");
            return;
        }

        if (sendInProgress)
        {
            setArenaRunStatus("Operator turn is already sending.");
            return;
        }

        // Control-plane commands are an independent input channel. They must never
        // commandeer or clear the user's visible composer, route, or session draft.
        await RunOperatorSendAsync(() => SendOperatorPromptAsync(session, text, normalizedRoute));
    }

    private async Task<bool> RunOperatorSendAsync(Func<Task<bool>> action)
    {
        sendInProgress = true;
        UpdateBusyState(lastBusy, lastAutoChatRunning);
        try
        {
            return await action();
        }
        finally
        {
            sendInProgress = false;
            UpdateBusyState(lastBusy, lastAutoChatRunning);
        }
    }

    private Task<bool> SendOperatorPromptAsync(CoreSessionSummary session, string text, string mode)
    {
        return NormalizeOperatorRoute(mode) switch
        {
            "private" => SendPrivateOperatorNoteAsync(session, text),
            "narrator" => AskNarratorFromOperatorAsync(session, text),
            _ => SendPublicOperatorTurnAsync(session, text)
        };
    }

    private async Task<bool> SendPublicOperatorTurnAsync(CoreSessionSummary session, string text)
    {
        var sent = false;
        await runArenaBusyAsync("Injecting operator turn...", sendButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                setArenaRunStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            var message = transcriptService.CreateOperatorMessage(text, snapshot.Engine.TurnCount + 1);
            snapshot.Engine.Messages.Add(message);
            snapshot.Engine.TurnCount = message.Turn;
            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_operator_turn_added", new { message.Turn, message.Text, Route = "public" });
            await refreshActiveSessionAsync("Public operator turn added.");
            sent = true;
        }, true);
        return sent;
    }

    public void UseOperatorTemplate()
    {
        var template = CurrentOperatorTemplateText();
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        var appended = StageDraftText(template);
        setArenaRunStatus(appended
            ? "Operator template appended after the existing draft."
            : "Operator template staged.");
    }

    public void ApplyQuickIntervention(string id)
    {
        var suggestion = CurrentInterventions()
            .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (suggestion is null)
        {
            setArenaRunStatus("No operator intervention suggestion is available.");
            return;
        }

        SetRouteMode(suggestion.Route);
        var appended = StageDraftText(suggestion.Prompt);
        setArenaRunStatus(appended
            ? $"{suggestion.Label} intervention appended after the existing {OperatorRouteLabel(suggestion.Route).ToLowerInvariant()} draft."
            : $"{suggestion.Label} intervention staged for {OperatorRouteLabel(suggestion.Route).ToLowerInvariant()}.");
    }

    public void SaveOperatorTemplate()
    {
        var template = turnText.Text.Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            setArenaRunStatus("Operator template is empty.");
            return;
        }

        var current = settings();
        current.OperatorTemplates ??= [];
        if (!current.OperatorTemplates.Contains(template, StringComparer.OrdinalIgnoreCase))
        {
            current.OperatorTemplates.Insert(0, template);
            current.OperatorTemplates = current.OperatorTemplates
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            settingsStore.Save(current);
            InitializeOperatorTemplates(template);
        }

        templatePicker.SelectedItem = template;
        setArenaRunStatus("Operator template saved.");
    }

    public void DeleteOperatorTemplate()
    {
        var template = CurrentOperatorTemplateText();
        if (string.IsNullOrWhiteSpace(template))
        {
            setArenaRunStatus("No operator template selected.");
            return;
        }

        var current = settings();
        current.OperatorTemplates ??= [];
        var nextTemplates = current.OperatorTemplates
            .Where(item => !item.Equals(template, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nextTemplates.Count == current.OperatorTemplates.Count)
        {
            setArenaRunStatus("Operator template was already removed.");
            return;
        }

        var nextSelection = nextTemplates.FirstOrDefault();
        current.OperatorTemplates = nextTemplates;
        settingsStore.Save(current);
        InitializeOperatorTemplates(nextSelection);
        setArenaRunStatus("Operator template deleted.");
    }

    private async Task<bool> SendPrivateOperatorNoteAsync(CoreSessionSummary session, string text)
    {
        var sent = false;
        await runArenaBusyAsync("Sending private operator guidance...", sendButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                setArenaRunStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            var targets = OperatorPrivateTargets(snapshot).ToArray();
            if (targets.Length == 0)
            {
                setArenaRunStatus("No target agents found for private guidance.");
                return;
            }

            var note = BuildOperatorPrivateNote(text);
            foreach (var agent in targets)
            {
                agent.PrivateNotes.RemoveAll(existing => existing.Equals(note, StringComparison.OrdinalIgnoreCase));
                agent.PrivateNotes.Add(note);
                if (agent.PrivateNotes.Count > 60)
                {
                    agent.PrivateNotes.RemoveRange(0, agent.PrivateNotes.Count - 60);
                }
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_operator_private_guidance_added", new
            {
                Targets = targets.Select(agent => agent.Id).ToArray(),
                Text = text
            });
            await refreshActiveSessionAsync($"Private guidance sent to {FormatOperatorTargetSummary(targets)}.");
            sent = true;
        }, true);
        return sent;
    }

    private async Task<bool> AskNarratorFromOperatorAsync(CoreSessionSummary session, string text)
    {
        var sent = false;
        await runArenaBusyAsync("Asking narrator...", sendButton, async () =>
        {
            var result = await narratorService.AskNarratorAsync(session.Id, text);
            var status = result.Ok && result.Message is not null
                ? $"Narrator answered operator request at turn {result.Message.Turn}."
                : $"Narrator request failed: {result.Error}";
            await refreshActiveSessionAsync(status);
            if (result.Ok && result.Message is not null)
            {
                speakNarratorMessage(result.Message);
                sent = true;
            }
        }, true);
        return sent;
    }

    private IEnumerable<DialogueAgent> OperatorPrivateTargets(ArenaSnapshot snapshot)
    {
        var selected = ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all");
        var agents = snapshot.Engine.Agents.Where(agent => AgentRosterService.IsParticipantId(agent.Id));

        return selected.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? agents.Where(agent => agent.Active)
            : agents.Where(agent => agent.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private void PopulatePrivateTargetPicker(ArenaViewSnapshot snapshot)
    {
        var selected = ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all");
        privateTargetPicker.Items.Clear();
        privateTargetPicker.Items.Add(new ComboBoxItem { Content = "All active agents", Tag = "all" });
        foreach (var agent in snapshot.Agents.Where(agent => AgentRosterService.IsParticipantId(agent.Id)))
        {
            privateTargetPicker.Items.Add(new ComboBoxItem
            {
                Content = DisplayStatusValue(agent.Id),
                Tag = agent.Id,
                ToolTip = agent.Name
            });
        }

        ShellUiHelpers.SelectComboTag(privateTargetPicker, selected);
        if (privateTargetPicker.SelectedIndex < 0)
        {
            ShellUiHelpers.SelectComboTag(privateTargetPicker, "all");
        }
    }

    private void WireQuickInterventionButtons()
    {
        foreach (var button in quickInterventionButtons)
        {
            button.Click += (_, _) =>
            {
                if (button.Tag is OperatorInterventionSuggestion suggestion)
                {
                    ApplyQuickIntervention(suggestion.Id);
                }
            };
        }
    }

    private void UpdateQuickInterventions()
    {
        var suggestions = CurrentInterventions().Take(QuickInterventionCount).ToArray();
        for (var index = 0; index < quickInterventionButtons.Count; index++)
        {
            var button = quickInterventionButtons[index];
            if (index >= suggestions.Length)
            {
                button.Visibility = Visibility.Collapsed;
                button.Tag = null;
                continue;
            }

            var suggestion = suggestions[index];
            button.Visibility = Visibility.Visible;
            button.Tag = suggestion;
            button.Content = CreateCommandContent(RouteGlyph(suggestion.Route), suggestion.Label, 10.5);
            button.ToolTip = InterventionTooltip(suggestion);
            AutomationProperties.SetName(button, $"Stage {suggestion.Label} operator intervention");
            AutomationProperties.SetHelpText(button, InterventionAutomationHelp(suggestion));
        }

        quickInterventionHintText.Text = suggestions.Length == 0
            ? "Quick interventions appear after a snapshot is loaded."
            : OperatorQuickInterventionHint(suggestions);
        quickInterventionHintText.ToolTip = suggestions.Length == 0
            ? "Load or create a match to get route-aware operator interventions."
            : string.Join(Environment.NewLine, suggestions.Select(InterventionAutomationHelp));
        AutomationProperties.SetName(quickInterventionHintText, "Operator quick interventions");
        AutomationProperties.SetHelpText(quickInterventionHintText, quickInterventionHintText.ToolTip?.ToString() ?? quickInterventionHintText.Text);
        quickInterventionHintText.SetResourceReference(TextBlock.ForegroundProperty, suggestions.Any(item => item.Id is "evidence" or "break_consensus" or "role_reset" or "cool_rhetoric")
            ? "OperatorAccentBrush"
            : "MutedTextBrush");
    }

    private IReadOnlyList<OperatorInterventionSuggestion> CurrentInterventions()
    {
        var snapshot = lastRenderedSnapshot();
        if (snapshot is null)
        {
            return BuildInterventionSuggestions(null, null);
        }

        var diagnostics = discourseDiagnostics.Analyze(
            snapshot.Messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn),
            snapshot.Agents.ToDictionary(agent => agent.Id, agent => agent.Persona, StringComparer.OrdinalIgnoreCase));
        return BuildInterventionSuggestions(snapshot, diagnostics);
    }

    private void UpdateRouteUi()
    {
        StyleOperatorRouteButton(publicRouteButton, routeMode.Equals("public", StringComparison.OrdinalIgnoreCase), "OperatorAccentBrush", "Public route");
        StyleOperatorRouteButton(privateRouteButton, routeMode.Equals("private", StringComparison.OrdinalIgnoreCase), "BetaAccentBrush", "Private route");
        StyleOperatorRouteButton(narratorRouteButton, routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase), "AssistBorderBrush", "Narrator route");

        privateTargetRow.Visibility = routeMode.Equals("private", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var sendLabel = routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            ? "Ask Narrator"
            : routeMode.Equals("private", StringComparison.OrdinalIgnoreCase)
                ? "Send Private"
                : "Send Public";
        sendButton.Content = routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            ? CreateCommandContent("\uE8D4", sendLabel, 12)
            : routeMode.Equals("private", StringComparison.OrdinalIgnoreCase)
                ? CreateCommandContent("\uE72E", sendLabel, 12)
                : CreateCommandContent("\uE724", sendLabel, 12);
        AutomationProperties.SetName(sendButton, sendLabel);
        var sendHelp = routeMode switch
        {
            "private" => $"{OperatorVisibilitySummary(routeMode)} {OperatorDestinationSummary(routeMode, ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all"), lastRenderedSnapshot())}.",
            "narrator" => OperatorVisibilitySummary(routeMode),
            _ => OperatorVisibilitySummary(routeMode)
        };
        AutomationProperties.SetHelpText(sendButton, $"{sendHelp} Session: {DraftSessionLabel()}.");
        turnText.Tag = routeMode switch
        {
            "private" => "Private guidance for agent memory...",
            "narrator" => "Ask narrator...",
            _ => "Inject public operator turn..."
        };
        var analysis = AnalyzeOperatorDraft(routeMode, turnText.Text?.Trim() ?? "", ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all"), lastRenderedSnapshot());
        routeHintText.Text = $"{analysis.Visibility} Destination: {analysis.Destination}. Session: {DraftSessionLabel()}.";
        routeHintText.ToolTip = OperatorDraftReceiptText(analysis, turnText.Text?.Trim() ?? "");
        AutomationProperties.SetName(routeHintText, "Operator route visibility");
        AutomationProperties.SetHelpText(routeHintText, routeHintText.Text);
        routeHintText.SetResourceReference(TextBlock.ForegroundProperty, routeMode switch
        {
            "private" => "BetaAccentBrush",
            "narrator" => "NarratorAccentBrush",
            _ => "MutedTextBrush"
        });
        UpdatePrivateTargetSummary();
    }

    public void UpdatePrivateTargetSummary()
    {
        var selected = ShellUiHelpers.SelectedComboTag(privateTargetPicker, "all");
        if (selected.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var active = (lastRenderedSnapshot()?.Agents ?? [])
                .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
                .Select(agent => DisplayStatusValue(agent.Id))
                .ToArray();
            privateTargetSummaryText.Text = active.Length == 0
                ? "No active private targets in the current snapshot."
                : $"Writes private memory notes to {string.Join(", ", active)}; no public transcript turn.";
            privateTargetSummaryText.ToolTip = OperatorDestinationSummary("private", selected, lastRenderedSnapshot());
            AutomationProperties.SetName(privateTargetSummaryText, "Private operator target summary");
            AutomationProperties.SetHelpText(privateTargetSummaryText, privateTargetSummaryText.Text);
            return;
        }

        privateTargetSummaryText.Text = $"Writes a private memory note to {DisplayStatusValue(selected)}; no public transcript turn.";
        privateTargetSummaryText.ToolTip = OperatorDestinationSummary("private", selected, lastRenderedSnapshot());
        AutomationProperties.SetName(privateTargetSummaryText, "Private operator target summary");
        AutomationProperties.SetHelpText(privateTargetSummaryText, privateTargetSummaryText.Text);
    }

    private void InitializeOperatorTemplates(string? preferredTemplate = null)
    {
        var current = settings();
        current.OperatorTemplates ??= [];
        templatePicker.ItemsSource = null;
        templatePicker.ItemsSource = current.OperatorTemplates;
        var preferredIndex = string.IsNullOrWhiteSpace(preferredTemplate)
            ? -1
            : current.OperatorTemplates.FindIndex(item => item.Equals(preferredTemplate, StringComparison.OrdinalIgnoreCase));
        templatePicker.SelectedIndex = preferredIndex >= 0
            ? preferredIndex
            : current.OperatorTemplates.Count > 0 ? 0 : -1;
        RefreshTemplateActionState(OperatorInputEnabled(lastBusy, lastAutoChatRunning, sendInProgress));
    }

    private string CurrentOperatorTemplateText()
    {
        return (templatePicker.SelectedItem?.ToString() ?? templatePicker.Text).Trim();
    }

    private void CaptureVisibleDraft()
    {
        if (restoringDraft || string.IsNullOrWhiteSpace(draftSessionId))
        {
            return;
        }

        var drafts = GetOrCreateSessionDrafts(draftSessionId);
        var text = turnText.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            drafts.Remove(routeMode);
            if (drafts.Count == 0)
            {
                draftsBySession.Remove(draftSessionId);
            }
        }
        else
        {
            drafts[routeMode] = text;
        }

        RememberCurrentRoute();
    }

    private void RestoreVisibleDraft()
    {
        var text = "";
        if (!string.IsNullOrWhiteSpace(draftSessionId)
            && draftsBySession.TryGetValue(draftSessionId, out var drafts)
            && drafts.TryGetValue(routeMode, out var savedDraft))
        {
            text = savedDraft;
        }

        SetVisibleDraftText(text);
    }

    private void RememberCurrentRoute()
    {
        if (!string.IsNullOrWhiteSpace(draftSessionId))
        {
            routeBySession[draftSessionId] = routeMode;
        }
    }

    private Dictionary<string, string> GetOrCreateSessionDrafts(string sessionId)
    {
        if (!draftsBySession.TryGetValue(sessionId, out var drafts))
        {
            drafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            draftsBySession[sessionId] = drafts;
        }

        return drafts;
    }

    private void SetVisibleDraftText(string text)
    {
        restoringDraft = true;
        try
        {
            turnText.Text = text;
            turnText.CaretIndex = turnText.Text.Length;
        }
        finally
        {
            restoringDraft = false;
        }
    }

    private bool StageDraftText(string text)
    {
        var staged = text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(staged))
        {
            return false;
        }

        var existing = turnText.Text ?? "";
        var appended = !string.IsNullOrWhiteSpace(existing)
            && !existing.Trim().Equals(staged, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(existing))
        {
            turnText.Text = staged;
        }
        else if (appended)
        {
            turnText.Text = $"{existing.TrimEnd()}\r\n\r\n{staged}";
        }

        turnText.Focus();
        turnText.CaretIndex = turnText.Text?.Length ?? 0;
        UpdateTurnMeter();
        return appended;
    }

    private void ClearVisibleDraftAfterSuccessfulSend(string sessionId, string route, string sentText)
    {
        var normalizedRoute = NormalizeOperatorRoute(route);
        if (draftsBySession.TryGetValue(sessionId, out var drafts))
        {
            drafts.Remove(normalizedRoute);
            if (drafts.Count == 0)
            {
                draftsBySession.Remove(sessionId);
            }
        }

        if (!draftSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)
            || !routeMode.Equals(normalizedRoute, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(turnText.Text?.Trim(), sentText.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        SetVisibleDraftText("");
        UpdateTurnMeter();
    }

    private string DraftSessionLabel()
    {
        var sessionId = string.IsNullOrWhiteSpace(draftSessionId)
            ? activeSession()?.Id?.Trim() ?? ""
            : draftSessionId;
        return string.IsNullOrWhiteSpace(sessionId) ? "no active session" : DisplayStatusValue(sessionId);
    }

    private void RefreshTemplateActionState(bool enabled)
    {
        var hasTemplate = enabled && !string.IsNullOrWhiteSpace(CurrentOperatorTemplateText());
        templatePicker.IsEnabled = enabled && templatePicker.Items.Count > 0;
        useTemplateButton.IsEnabled = hasTemplate;
        deleteTemplateButton.IsEnabled = hasTemplate;
        saveTemplateButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(turnText.Text);
    }

    private static void StyleOperatorRouteButton(Button button, bool active, string accentBrushKey, string automationName)
    {
        button.SetResourceReference(Control.BackgroundProperty, active ? "NavActiveBrush" : "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, active ? accentBrushKey : "DisabledBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, active ? "TextBrush" : "MutedTextBrush");
        button.Opacity = active ? 1.0 : 0.86;
        AutomationProperties.SetName(button, automationName);
        AutomationProperties.SetItemStatus(button, active ? "selected" : "not selected");
        AutomationProperties.SetHelpText(button, active ? $"{automationName} selected." : $"Switch to {automationName.ToLowerInvariant()}.");
    }

    private static StackPanel CreateCommandContent(string glyph, string label, double iconSize)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = iconSize,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    internal static OperatorDraftAnalysis AnalyzeOperatorDraft(string route, string text, string privateTarget, ArenaViewSnapshot? snapshot)
    {
        var normalizedRoute = NormalizeOperatorRoute(route);
        var draft = text?.Trim() ?? "";
        var charCount = draft.Length;
        var tokenEstimate = charCount == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(charCount / 4.0));
        var routeLabel = OperatorRouteLabel(normalizedRoute);
        var destination = OperatorDestinationSummary(normalizedRoute, privateTarget, snapshot);
        var visibility = OperatorVisibilitySummary(normalizedRoute);
        var meter = $"{charCount} chars / ~{tokenEstimate} tok | {routeLabel}";
        if (normalizedRoute.Equals("private", StringComparison.OrdinalIgnoreCase))
        {
            meter += $" -> {PrivateTargetMeterLabel(privateTarget, snapshot)}";
        }

        return new OperatorDraftAnalysis(
            normalizedRoute,
            routeLabel,
            destination,
            visibility,
            charCount,
            tokenEstimate,
            meter);
    }

    internal static IReadOnlyList<string> OperatorDraftReceiptLines(OperatorDraftAnalysis analysis, string text, OperatorInterventionSuggestion? suggestion = null)
    {
        var lines = new List<string>
        {
            "AI Arena Operator Draft",
            $"Route: {analysis.RouteLabel}",
            $"Destination: {analysis.Destination}",
            $"Visibility: {analysis.Visibility}",
            $"Draft size: {analysis.CharacterCount} chars / ~{analysis.TokenEstimate} tokens"
        };
        if (suggestion is not null)
        {
            lines.Add($"Intervention: {suggestion.Label} ({suggestion.Id})");
            lines.Add($"Why: {suggestion.Reason}");
        }

        lines.Add($"Prompt: {ReceiptPromptText(text)}");
        lines.Add($"Next check: {OperatorNextCheck(analysis)}");
        return lines;
    }

    internal static string OperatorDraftReceiptText(OperatorDraftAnalysis analysis, string text, OperatorInterventionSuggestion? suggestion = null)
    {
        return string.Join(Environment.NewLine, OperatorDraftReceiptLines(analysis, text, suggestion));
    }

    internal static string InterventionTooltip(OperatorInterventionSuggestion suggestion)
    {
        var analysis = AnalyzeOperatorDraft(suggestion.Route, suggestion.Prompt, "all", null);
        return OperatorDraftReceiptText(analysis, suggestion.Prompt, suggestion);
    }

    internal static string InterventionAutomationHelp(OperatorInterventionSuggestion suggestion)
    {
        return $"{suggestion.Reason} Route: {OperatorRouteLabel(suggestion.Route)}. {OperatorVisibilitySummary(suggestion.Route)}";
    }

    internal static string OperatorQuickInterventionHint(IReadOnlyList<OperatorInterventionSuggestion> suggestions)
    {
        return suggestions.Count == 0
            ? "Quick interventions appear after a snapshot is loaded."
            : $"Quick interventions: {string.Join(", ", suggestions.Select(item => $"{item.Label} -> {OperatorRouteShortLabel(item.Route)}"))}.";
    }

    internal static string OperatorDestinationSummary(string route, string privateTarget, ArenaViewSnapshot? snapshot)
    {
        return NormalizeOperatorRoute(route) switch
        {
            "private" => $"Private memory for {PrivateDestinationLabel(privateTarget, snapshot)}",
            "narrator" => "Narrator referee channel",
            _ => "Public transcript for all agents"
        };
    }

    internal static string OperatorVisibilitySummary(string route)
    {
        return NormalizeOperatorRoute(route) switch
        {
            "private" => "Hidden from the public transcript; only selected agent memory receives it.",
            "narrator" => "Visible narrator reply; participant turn order is not advanced.",
            _ => "Visible transcript turn; every agent can use it as shared context."
        };
    }

    internal static string OperatorRouteLabel(string route)
    {
        return NormalizeOperatorRoute(route) switch
        {
            "private" => "Private memory",
            "narrator" => "Narrator request",
            _ => "Public transcript"
        };
    }

    internal static bool TryNormalizeOperatorRoute(string? route, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(route) ? "public" : route.Trim().ToLowerInvariant();
        return normalized is "public" or "private" or "narrator";
    }

    private static string NormalizeOperatorRoute(string? route)
    {
        return TryNormalizeOperatorRoute(route, out var normalized) ? normalized : "public";
    }

    private static string OperatorRouteShortLabel(string route)
    {
        return NormalizeOperatorRoute(route) switch
        {
            "private" => "Private",
            "narrator" => "Narrator",
            _ => "Public"
        };
    }

    private static string PrivateDestinationLabel(string privateTarget, ArenaViewSnapshot? snapshot)
    {
        var selected = string.IsNullOrWhiteSpace(privateTarget) ? "all" : privateTarget.Trim();
        if (!selected.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return DisplayStatusValue(selected);
        }

        var active = (snapshot?.Agents ?? [])
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .Select(agent => DisplayStatusValue(agent.Id))
            .ToArray();
        if (active.Length == 0)
        {
            return "all active agents";
        }

        var preview = string.Join(", ", active.Take(4));
        if (active.Length > 4)
        {
            preview += ", ...";
        }

        return active.Length == 1
            ? active[0]
            : $"{active.Length} active agents ({preview})";
    }

    private static string PrivateTargetMeterLabel(string privateTarget, ArenaViewSnapshot? snapshot)
    {
        var selected = string.IsNullOrWhiteSpace(privateTarget) ? "all" : privateTarget.Trim();
        if (!selected.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return DisplayStatusValue(selected);
        }

        var activeCount = (snapshot?.Agents ?? [])
            .Count(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id));
        return activeCount > 0 ? $"{activeCount} agents" : "all active";
    }

    private static string ReceiptPromptText(string text)
    {
        var prompt = string.Join(" ", (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(prompt) ? "(empty draft)" : prompt;
    }

    private static string OperatorNextCheck(OperatorDraftAnalysis analysis)
    {
        return analysis.Route switch
        {
            "private" => "Watch the next targeted agent response for role correction without public noise.",
            "narrator" => "Use the narrator answer as a referee note before resuming participant turns.",
            _ => "Check the next transcript turn for evidence boundaries and changed agent behavior."
        };
    }

    internal static IReadOnlyList<OperatorInterventionSuggestion> BuildInterventionSuggestions(ArenaViewSnapshot? snapshot, FrictionDiagnostics? diagnostics)
    {
        var messages = snapshot?.Messages ?? [];
        var suggestions = new List<OperatorInterventionSuggestion>();
        if (messages.Count == 0 || diagnostics is null)
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "set_stakes",
                "Set Stakes",
                "public",
                "Before the first turn, state the success criteria, unacceptable failure modes, and what kind of answer should count as a win.",
                "Start the match with explicit judging criteria."));
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "evidence",
                "Evidence",
                "public",
                "For the next turn, separate evidence, inference, and assumption. Mark unsupported claims as unproven instead of smoothing them over.",
                "Ask agents to preserve evidence boundaries."));
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "dissent",
                "Dissent",
                "public",
                "Before converging, one agent must make the strongest case against the current direction and name what evidence would change the answer.",
                "Force one productive disagreement before consensus."));
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "narrator_brief",
                "Narrator",
                "narrator",
                "Give a brief referee note: what should the agents optimize for, what should they avoid, and what would make the run useful?",
                "Ask the narrator to frame the run before it gets noisy."));
            return suggestions;
        }

        var errorTurns = messages.Count(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase));
        if (errorTurns > 0)
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "repair",
                "Repair",
                "public",
                "Pause the debate. Identify the failed turn, restate what context is still valid, and continue only after the failure path is acknowledged.",
                "A failed turn is present; repair context before continuing."));
        }

        if (diagnostics.UnsupportedClaimCount > 0 || diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "evidence",
                "Evidence",
                "public",
                "For the next turn, separate evidence, inference, and assumption. Mark unsupported claims as unproven instead of smoothing them over.",
                "Unsupported claims or weak evidence pressure are visible."));
        }

        if (diagnostics.ConsensusPercent >= 76)
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "break_consensus",
                "Break Lock",
                "public",
                "Before accepting the emerging consensus, assign one agent to attack the strongest assumption and one agent to defend it with concrete evidence.",
                "Consensus is high enough to risk premature convergence."));
        }

        if (diagnostics.RoleDriftPercent >= 35)
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "role_reset",
                "Reset Roles",
                "private",
                "Return to your assigned role and pressure profile. Do not imitate the previous speaker. Make your next contribution visibly different from the others.",
                "Role drift is high; private guidance can reset agent behavior without adding public noise."));
        }

        if (diagnostics.NarrativeHeatScore >= 82)
        {
            AddSuggestion(suggestions, new OperatorInterventionSuggestion(
                "cool_rhetoric",
                "Cool Heat",
                "public",
                "Cool the rhetoric. Convert the strongest claim into a testable condition, a risk, and a reversible next step.",
                "Narrative heat is high; ask for testable language."));
        }

        AddSuggestion(suggestions, new OperatorInterventionSuggestion(
            "decision_frame",
            "Frame",
            "public",
            "Summarize the live disagreement, name the remaining decision criteria, and identify the smallest reversible next step.",
            "Turn the debate into decision criteria."));
        AddSuggestion(suggestions, new OperatorInterventionSuggestion(
            "strong_objection",
            "Objection",
            "public",
            "State the strongest objection to the last substantive answer, then answer that objection without changing the topic.",
            "Focus the next turn on the strongest unresolved objection."));
        AddSuggestion(suggestions, new OperatorInterventionSuggestion(
            "narrator_judge",
            "Judge",
            "narrator",
            "Judge the current run so far: strongest claim, weakest claim, missing evidence, and the next operator move.",
            "Ask the narrator for a compact referee judgment."));
        AddSuggestion(suggestions, new OperatorInterventionSuggestion(
            "handoff_note",
            "Handoff",
            "public",
            "Create a handoff note: current decision, assumptions, unresolved risks, and the exact next check before the arena changes direction.",
            "Leave a durable breadcrumb before changing direction or pausing the run."));
        AddSuggestion(suggestions, new OperatorInterventionSuggestion(
            "next_step",
            "Next Step",
            "public",
            "Stop expanding the debate. Name the next action, the owner, the risk, and what would invalidate the recommendation.",
            "Force the discussion into an actionable next step."));

        return suggestions.Take(QuickInterventionCount).ToArray();
    }

    private static void AddSuggestion(List<OperatorInterventionSuggestion> suggestions, OperatorInterventionSuggestion suggestion)
    {
        if (suggestions.Any(existing => existing.Id.Equals(suggestion.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        suggestions.Add(suggestion);
    }

    private static string RouteGlyph(string route)
    {
        return route.ToLowerInvariant() switch
        {
            "private" => "\uE72E",
            "narrator" => "\uE8D4",
            _ => "\uE724"
        };
    }

    private static string BuildOperatorPrivateNote(string text)
    {
        var note = $"Operator private: {text.Trim()}";
        return note.Length <= 400 ? note : note[..400];
    }

    private static string FormatOperatorTargetSummary(IReadOnlyCollection<DialogueAgent> targets)
    {
        return targets.Count == 1
            ? DisplayStatusValue(targets.First().Id)
            : $"{targets.Count} agents";
    }

    private static string DisplayStatusValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }


}
