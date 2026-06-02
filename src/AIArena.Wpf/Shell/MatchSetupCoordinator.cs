using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class MatchSetupCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly CheckBox rivalryMatrixEnabledCheckBox;
    private readonly Panel rivalryMatrixRows;
    private readonly TextBlock rivalryMatrixStatusText;
    private readonly Button applyRivalryMatrixButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;

    private readonly List<RivalryMatrixControlRow> rivalryMatrixControls = [];

    public MatchSetupCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        CheckBox rivalryMatrixEnabledCheckBox,
        Panel rivalryMatrixRows,
        TextBlock rivalryMatrixStatusText,
        Button applyRivalryMatrixButton,
        Func<CoreSessionSummary?> activeSession,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> displayStatusValue,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.rivalryMatrixEnabledCheckBox = rivalryMatrixEnabledCheckBox;
        this.rivalryMatrixRows = rivalryMatrixRows;
        this.rivalryMatrixStatusText = rivalryMatrixStatusText;
        this.applyRivalryMatrixButton = applyRivalryMatrixButton;
        this.activeSession = activeSession;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.displayStatusValue = displayStatusValue;
        this.blendBrush = blendBrush;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
    }

    public void PopulateRivalryMatrix(ArenaViewSnapshot snapshot)
    {
        rivalryMatrixEnabledCheckBox.IsChecked = snapshot.RivalryMatrixEnabled;
        rivalryMatrixRows.Children.Clear();
        rivalryMatrixControls.Clear();
        var links = snapshot.RivalryMatrix
            .GroupBy(link => link.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var agentIds = snapshot.Agents
            .Where(agent => agent.Active)
            .Select(agent => agent.Id)
            .Where(AgentRosterService.IsParticipantId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("alpha")
            .ToArray();

        foreach (var source in agentIds)
        {
            rivalryMatrixRows.Children.Add(CreateRivalryMatrixRow(source));
        }

        foreach (var (source, targetPicker, stancePicker) in RivalryMatrixControls())
        {
            PopulateRivalryTargetPicker(targetPicker, source, agentIds);
            PopulateRivalryStancePicker(stancePicker);
            var link = links.TryGetValue(source, out var item) ? item : null;
            ShellUiHelpers.SelectComboTag(targetPicker, link?.Target ?? "");
            ShellUiHelpers.SelectComboTag(stancePicker, NormalizeRivalryStance(link?.Stance ?? "neutral"));
        }

        rivalryMatrixStatusText.Text = Summary(snapshot.RivalryMatrixEnabled, snapshot.RivalryMatrix);
        rivalryMatrixStatusText.Foreground = resourceBrush("MutedTextBrush");
    }

    public async Task ApplyRivalryMatrixAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            rivalryMatrixStatusText.Text = "No active session.";
            rivalryMatrixStatusText.Foreground = resourceBrush("DangerTextBrush");
            return;
        }

        await runArenaBusyAsync("Applying relationship matrix...", applyRivalryMatrixButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                rivalryMatrixStatusText.Text = $"No snapshot found for session {session.Id}.";
                rivalryMatrixStatusText.Foreground = resourceBrush("DangerTextBrush");
                return;
            }

            snapshot.Engine.RivalryMatrix.Enabled = rivalryMatrixEnabledCheckBox.IsChecked == true;
            snapshot.Engine.RivalryMatrix.Links.Clear();
            foreach (var (source, targetPicker, stancePicker) in RivalryMatrixControls())
            {
                var target = ShellUiHelpers.SelectedComboTag(targetPicker, "");
                var stance = NormalizeRivalryStance(ShellUiHelpers.SelectedComboTag(stancePicker, "neutral"));
                if (string.IsNullOrWhiteSpace(target) || stance.Equals("neutral", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshot.Engine.RivalryMatrix.Links.Add(new AIArena.Core.Models.RivalryLink
                {
                    Source = source,
                    Target = target,
                    Stance = stance
                });
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_rivalry_matrix_applied", new
            {
                snapshot.Engine.RivalryMatrix.Enabled,
                links = snapshot.Engine.RivalryMatrix.Links.Select(link => new { link.Source, link.Target, link.Stance }).ToArray()
            });
            await refreshActiveSessionAsync(Summary(snapshot.Engine.RivalryMatrix.Enabled, snapshot.Engine.RivalryMatrix.Links
                .Select(link => new RivalryMatrixItem(link.Source, link.Target, link.Stance))
                .ToArray()));
        }, true);
    }

    private Border CreateRivalryMatrixRow(string source)
    {
        var stack = new StackPanel { Width = 178, Margin = new Thickness(0, 0, 8, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = displayStatusValue(source),
            Foreground = accentForSpeaker(source),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = displayStatusValue(source)
        });

        var target = new ComboBox
        {
            Tag = source,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(7, 5, 7, 5),
            FontSize = 11,
            ToolTip = $"Relationship target for {displayStatusValue(source)}"
        };
        var stance = new ComboBox
        {
            Tag = source,
            Padding = new Thickness(7, 5, 7, 5),
            FontSize = 11,
            ToolTip = $"Relationship stance for {displayStatusValue(source)}"
        };
        stack.Children.Add(target);
        stack.Children.Add(stance);

        rivalryMatrixControls.Add(new RivalryMatrixControlRow(source, target, stance));
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accentForSpeaker(source), 0.06),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accentForSpeaker(source), 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 8, 8),
            Child = stack
        };
    }

    private IEnumerable<(string Source, ComboBox Target, ComboBox Stance)> RivalryMatrixControls()
    {
        return rivalryMatrixControls.Select(row => (row.Source, row.Target, row.Stance));
    }

    private void PopulateRivalryTargetPicker(ComboBox picker, string source, IReadOnlyList<string> agentIds)
    {
        picker.Items.Clear();
        picker.Items.Add(new ComboBoxItem { Content = "No target", Tag = "" });
        foreach (var id in agentIds.Where(id => !id.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            picker.Items.Add(new ComboBoxItem { Content = displayStatusValue(id), Tag = id });
        }
    }

    private static void PopulateRivalryStancePicker(ComboBox picker)
    {
        picker.Items.Clear();
        picker.Items.Add(new ComboBoxItem { Content = "Neutral", Tag = "neutral" });
        picker.Items.Add(new ComboBoxItem { Content = "Challenge", Tag = "challenge" });
        picker.Items.Add(new ComboBoxItem { Content = "Support", Tag = "support" });
        picker.Items.Add(new ComboBoxItem { Content = "Steelman", Tag = "steelman" });
        picker.Items.Add(new ComboBoxItem { Content = "Cross-examine", Tag = "cross_examine" });
        picker.Items.Add(new ComboBoxItem { Content = "Rival", Tag = "rival" });
    }

    private static string NormalizeRivalryStance(string stance)
    {
        var value = string.IsNullOrWhiteSpace(stance) ? "neutral" : stance.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return value switch
        {
            "challenge" or "support" or "steelman" or "cross_examine" or "rival" => value,
            _ => "neutral"
        };
    }

    private static string Summary(bool enabled, IReadOnlyList<RivalryMatrixItem> links)
    {
        var active = links.Count(link => !NormalizeRivalryStance(link.Stance).Equals("neutral", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(link.Target));
        if (!enabled)
        {
            return active == 0 ? "Relationship pressure is off." : $"{active} relationship rule(s) saved, currently disabled.";
        }

        return active == 0 ? "Relationship pressure enabled with neutral rules." : $"{active} relationship rule(s) active.";
    }



    private sealed record RivalryMatrixControlRow(
        string Source,
        ComboBox Target,
        ComboBox Stance);
}
