using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class OperatorTurnCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly TranscriptService transcriptService;
    private readonly NarratorService narratorService;
    private readonly WpfSettingsStore settingsStore;
    private readonly Button publicRouteButton;
    private readonly Button privateRouteButton;
    private readonly Button narratorRouteButton;
    private readonly FrameworkElement privateTargetRow;
    private readonly ComboBox privateTargetPicker;
    private readonly TextBlock privateTargetSummaryText;
    private readonly TextBlock routeHintText;
    private readonly TextBlock meterText;
    private readonly ComboBox templatePicker;
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

    private string routeMode = "public";

    public OperatorTurnCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TranscriptService transcriptService,
        NarratorService narratorService,
        WpfSettingsStore settingsStore,
        Button publicRouteButton,
        Button privateRouteButton,
        Button narratorRouteButton,
        FrameworkElement privateTargetRow,
        ComboBox privateTargetPicker,
        TextBlock privateTargetSummaryText,
        TextBlock routeHintText,
        TextBlock meterText,
        ComboBox templatePicker,
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
        Action<string> setArenaRunStatus)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.transcriptService = transcriptService;
        this.narratorService = narratorService;
        this.settingsStore = settingsStore;
        this.publicRouteButton = publicRouteButton;
        this.privateRouteButton = privateRouteButton;
        this.narratorRouteButton = narratorRouteButton;
        this.privateTargetRow = privateTargetRow;
        this.privateTargetPicker = privateTargetPicker;
        this.privateTargetSummaryText = privateTargetSummaryText;
        this.routeHintText = routeHintText;
        this.meterText = meterText;
        this.templatePicker = templatePicker;
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
    }

    public void InitializeControls()
    {
        InitializeOperatorTemplates();
        UpdateRouteUi();
        UpdateTurnMeter();
    }

    public void SetRouteMode(string mode)
    {
        routeMode = string.IsNullOrWhiteSpace(mode) ? "public" : mode.Trim();
        UpdateRouteUi();
    }

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        PopulatePrivateTargetPicker(snapshot);
    }

    public void OnPrivateTargetChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        UpdatePrivateTargetSummary();
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
        var text = turnText.Text?.Trim() ?? "";
        var charCount = text.Length;
        var tokenEstimate = charCount == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(charCount / 4.0));
        meterText.Text = $"{charCount} chars / ~{tokenEstimate} tok";
        meterText.Foreground = charCount > 0
            ? resourceBrush("OperatorAccentBrush")
            : resourceBrush("MutedTextBrush");
    }

    public void UpdateBusyState()
    {
        sendButton.IsEnabled = true;
        turnText.IsEnabled = true;
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

        if (routeMode.Equals("private", StringComparison.OrdinalIgnoreCase))
        {
            await SendPrivateOperatorNoteAsync(session, text);
            return;
        }

        if (routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            await AskNarratorFromOperatorAsync(session, text);
            return;
        }

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
            turnText.Clear();
            await refreshActiveSessionAsync("Public operator turn added.");
        }, true);
    }

    public void UseOperatorTemplate()
    {
        var template = CurrentOperatorTemplateText();
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        turnText.Text = template;
        turnText.Focus();
        turnText.CaretIndex = turnText.Text.Length;
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

    private async Task SendPrivateOperatorNoteAsync(CoreSessionSummary session, string text)
    {
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
            turnText.Clear();
            await refreshActiveSessionAsync($"Private guidance sent to {FormatOperatorTargetSummary(targets)}.");
        }, true);
    }

    private async Task AskNarratorFromOperatorAsync(CoreSessionSummary session, string text)
    {
        await runArenaBusyAsync("Asking narrator...", sendButton, async () =>
        {
            var result = await narratorService.AskNarratorAsync(session.Id, text);
            var status = result.Ok && result.Message is not null
                ? $"Narrator answered operator request at turn {result.Message.Turn}."
                : $"Narrator request failed: {result.Error}";
            turnText.Clear();
            await refreshActiveSessionAsync(status);
        }, true);
    }

    private IEnumerable<DialogueAgent> OperatorPrivateTargets(ArenaSnapshot snapshot)
    {
        var selected = SelectedComboTag(privateTargetPicker, "all");
        var agents = snapshot.Engine.Agents.Where(agent => AgentRosterService.IsParticipantId(agent.Id));

        return selected.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? agents.Where(agent => agent.Active)
            : agents.Where(agent => agent.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private void PopulatePrivateTargetPicker(ArenaViewSnapshot snapshot)
    {
        var selected = SelectedComboTag(privateTargetPicker, "all");
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

        SelectComboTag(privateTargetPicker, selected);
        if (privateTargetPicker.SelectedIndex < 0)
        {
            SelectComboTag(privateTargetPicker, "all");
        }
    }

    private void UpdateRouteUi()
    {
        StyleOperatorRouteButton(publicRouteButton, routeMode.Equals("public", StringComparison.OrdinalIgnoreCase), "OperatorAccentBrush");
        StyleOperatorRouteButton(privateRouteButton, routeMode.Equals("private", StringComparison.OrdinalIgnoreCase), "BetaAccentBrush");
        StyleOperatorRouteButton(narratorRouteButton, routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase), "AssistBorderBrush");

        privateTargetRow.Visibility = routeMode.Equals("private", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        sendButton.Content = routeMode.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            ? "ASK NARRATOR"
            : routeMode.Equals("private", StringComparison.OrdinalIgnoreCase)
                ? "SEND PRIVATE"
                : "SEND PUBLIC";
        turnText.Tag = routeMode switch
        {
            "private" => "Private guidance for agent memory...",
            "narrator" => "Ask narrator...",
            _ => "Inject public operator turn..."
        };
        routeHintText.Text = routeMode switch
        {
            "private" => "Private guidance is written into selected agent memory notes and is not added as a public transcript turn.",
            "narrator" => "Narrator requests ask the observer to answer publicly without advancing the participant turn order.",
            _ => "Public turns are visible in the transcript and become context for all agents."
        };
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
        var selected = SelectedComboTag(privateTargetPicker, "all");
        if (selected.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var active = (lastRenderedSnapshot()?.Agents ?? [])
                .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
                .Select(agent => DisplayStatusValue(agent.Id))
                .ToArray();
            privateTargetSummaryText.Text = active.Length == 0
                ? "No active private targets in the current snapshot."
                : $"Writes private memory notes to {string.Join(", ", active)}; no public transcript turn.";
            return;
        }

        privateTargetSummaryText.Text = $"Writes a private memory note to {DisplayStatusValue(selected)}; no public transcript turn.";
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
    }

    private string CurrentOperatorTemplateText()
    {
        return (templatePicker.SelectedItem?.ToString() ?? templatePicker.Text).Trim();
    }

    private static void StyleOperatorRouteButton(Button button, bool active, string accentBrushKey)
    {
        button.SetResourceReference(Control.BackgroundProperty, active ? "PrimaryBrush" : "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, active ? accentBrushKey : "DisabledBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, active ? "TextBrush" : "MutedTextBrush");
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

    private static string SelectedComboTag(ComboBox combo, string fallback)
    {
        return combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag?.ToString() ?? "").Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }
}
