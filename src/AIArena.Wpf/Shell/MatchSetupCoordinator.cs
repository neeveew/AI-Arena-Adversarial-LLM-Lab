using System.Windows;
using System.Windows.Automation;
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
    private readonly Panel rivalryMatrixPreviewItems;
    private readonly TextBlock rivalryMatrixInsightText;
    private readonly TextBlock rivalryMatrixStatusText;
    private readonly Button applyRivalryMatrixButton;
    private readonly Button clearRivalryMatrixButton;
    private readonly ComboBox rivalryMatrixPatternPicker;
    private readonly Button applyRivalryMatrixPatternButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;

    private readonly List<RivalryMatrixControlRow> rivalryMatrixControls = [];
    private bool rivalryMatrixBusy;
    private bool isUpdatingRivalryMatrix;

    public MatchSetupCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        CheckBox rivalryMatrixEnabledCheckBox,
        Panel rivalryMatrixRows,
        Panel rivalryMatrixPreviewItems,
        TextBlock rivalryMatrixInsightText,
        TextBlock rivalryMatrixStatusText,
        Button applyRivalryMatrixButton,
        Button clearRivalryMatrixButton,
        ComboBox rivalryMatrixPatternPicker,
        Button applyRivalryMatrixPatternButton,
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
        this.rivalryMatrixPreviewItems = rivalryMatrixPreviewItems;
        this.rivalryMatrixInsightText = rivalryMatrixInsightText;
        this.rivalryMatrixStatusText = rivalryMatrixStatusText;
        this.applyRivalryMatrixButton = applyRivalryMatrixButton;
        this.clearRivalryMatrixButton = clearRivalryMatrixButton;
        this.rivalryMatrixPatternPicker = rivalryMatrixPatternPicker;
        this.applyRivalryMatrixPatternButton = applyRivalryMatrixPatternButton;
        this.activeSession = activeSession;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.displayStatusValue = displayStatusValue;
        this.blendBrush = blendBrush;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.rivalryMatrixEnabledCheckBox.Checked += (_, _) => RefreshDraftRivalryMatrixPreview();
        this.rivalryMatrixEnabledCheckBox.Unchecked += (_, _) => RefreshDraftRivalryMatrixPreview();
    }

    public void PopulateRivalryMatrix(ArenaViewSnapshot snapshot)
    {
        isUpdatingRivalryMatrix = true;
        try
        {
            rivalryMatrixEnabledCheckBox.IsChecked = snapshot.RivalryMatrixEnabled;
            rivalryMatrixRows.Children.Clear();
            rivalryMatrixControls.Clear();
            var agentIds = snapshot.Agents
                .Where(agent => agent.Active)
                .Select(agent => agent.Id)
                .Where(AgentRosterService.IsParticipantId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty("alpha")
                .ToArray();
            var activeAgentIds = agentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var links = snapshot.RivalryMatrix
                .Where(link => IsValidRivalryLink(link, activeAgentIds))
                .GroupBy(link => link.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

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

            SetRivalryMatrixStatus(Summary(snapshot.RivalryMatrixEnabled, snapshot.RivalryMatrix, activeAgentIds), resourceBrush("MutedTextBrush"));
            UpdateBusyState(rivalryMatrixBusy);
        }
        finally
        {
            isUpdatingRivalryMatrix = false;
        }

        RefreshDraftRivalryMatrixPreview();
    }

    public void UpdateBusyState(bool busy)
    {
        rivalryMatrixBusy = busy;
        rivalryMatrixEnabledCheckBox.IsEnabled = !busy;
        applyRivalryMatrixButton.IsEnabled = !busy;
        clearRivalryMatrixButton.IsEnabled = !busy;
        rivalryMatrixPatternPicker.IsEnabled = !busy;
        applyRivalryMatrixPatternButton.IsEnabled = !busy;
        foreach (var (_, targetPicker, stancePicker) in RivalryMatrixControls())
        {
            targetPicker.IsEnabled = !busy;
            stancePicker.IsEnabled = !busy;
        }
    }

    public void ClearDraftRivalryMatrix()
    {
        if (rivalryMatrixBusy)
        {
            return;
        }

        rivalryMatrixEnabledCheckBox.IsChecked = false;
        foreach (var (_, targetPicker, stancePicker) in RivalryMatrixControls())
        {
            ShellUiHelpers.SelectComboTag(targetPicker, "");
            ShellUiHelpers.SelectComboTag(stancePicker, "neutral");
        }

        SetRivalryMatrixStatus("Relationship matrix cleared locally. Apply Matrix to save.", resourceBrush("MutedTextBrush"));
        RefreshDraftRivalryMatrixPreview("Relationship matrix cleared locally. Apply Matrix to save.");
    }

    public void ApplyRivalryMatrixPatternDraft()
    {
        if (rivalryMatrixBusy)
        {
            return;
        }

        var pattern = ShellUiHelpers.SelectedComboTag(rivalryMatrixPatternPicker, "custom");
        var activeAgentIds = RivalryMatrixControls()
            .Select(row => row.Source)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var draft = BuildRivalryPatternDraft(pattern, activeAgentIds);
        var bySource = draft.ToDictionary(link => link.Source, StringComparer.OrdinalIgnoreCase);
        foreach (var (source, targetPicker, stancePicker) in RivalryMatrixControls())
        {
            if (bySource.TryGetValue(source, out var link))
            {
                ShellUiHelpers.SelectComboTag(targetPicker, link.Target);
                ShellUiHelpers.SelectComboTag(stancePicker, link.Stance);
            }
            else
            {
                ShellUiHelpers.SelectComboTag(targetPicker, "");
                ShellUiHelpers.SelectComboTag(stancePicker, "neutral");
            }
        }

        rivalryMatrixEnabledCheckBox.IsChecked = draft.Count > 0;
        SetRivalryMatrixStatus(
            draft.Count == 0
                ? "Custom relationship draft ready. Choose targets or Apply Matrix to save neutral rules."
                : $"{PatternLabel(pattern)} drafted with {draft.Count} rule(s). Apply Matrix to save.",
            resourceBrush("MutedTextBrush"));
        RefreshDraftRivalryMatrixPreview(
            draft.Count == 0
                ? "Custom relationship draft ready. Choose targets or Apply Matrix to save neutral rules."
                : $"{PatternLabel(pattern)} drafted with {draft.Count} rule(s). Apply Matrix to save.");
    }

    private void SetRivalryMatrixStatus(string text, Brush brush)
    {
        rivalryMatrixStatusText.Text = text;
        rivalryMatrixStatusText.Foreground = brush;
        rivalryMatrixStatusText.ToolTip = text;
        AutomationProperties.SetHelpText(rivalryMatrixStatusText, text);
    }

    private void RefreshDraftRivalryMatrixPreview(string? statusOverride = null)
    {
        if (isUpdatingRivalryMatrix)
        {
            return;
        }

        var activeAgentIds = RivalryMatrixControls()
            .Select(row => row.Source)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var enabled = rivalryMatrixEnabledCheckBox.IsChecked == true;
        var plan = CurrentDraftPlan(activeAgentIds);
        SetRivalryMatrixStatus(
            statusOverride ?? Summary(enabled, plan.Links, activeAgentIds, plan.SkippedInvalidRules),
            resourceBrush(plan.Links.Count == 0 ? "MutedTextBrush" : "TextBrush"));
        PopulateRivalryMatrixPreview(enabled, plan.Links, activeAgentIds, plan.SkippedInvalidRules);
    }

    private RivalryMatrixPlan CurrentDraftPlan(IReadOnlyCollection<string> activeAgentIds)
    {
        return BuildRivalryMatrixPlan(
            RivalryMatrixControls().Select(row => new RivalryMatrixItem(
                row.Source,
                ShellUiHelpers.SelectedComboTag(row.Target, ""),
                ShellUiHelpers.SelectedComboTag(row.Stance, "neutral"))),
            activeAgentIds);
    }

    private void PopulateRivalryMatrixPreview(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string> activeAgentIds,
        int skippedInvalidRules)
    {
        rivalryMatrixPreviewItems.Children.Clear();
        foreach (var item in RelationshipPreviewItems(enabled, links, activeAgentIds))
        {
            rivalryMatrixPreviewItems.Children.Add(CreatePressurePreviewChip(item));
        }

        var insight = RelationshipInsight(enabled, links, activeAgentIds, skippedInvalidRules);
        rivalryMatrixInsightText.Text = insight;
        rivalryMatrixInsightText.ToolTip = insight;
        AutomationProperties.SetHelpText(rivalryMatrixInsightText, insight);
    }

    private Border CreatePressurePreviewChip(RelationshipPreviewItem item)
    {
        var accent = string.IsNullOrWhiteSpace(item.Source)
            ? resourceBrush("MutedTextBrush")
            : accentForSpeaker(item.Source);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.Route,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = item.Stance,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4, 8, 5),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = $"{item.Route} - {item.Stance}",
            Child = stack
        };
    }

    public async Task ApplyRivalryMatrixAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            SetRivalryMatrixStatus("No active session.", resourceBrush("DangerTextBrush"));
            return;
        }

        await runArenaBusyAsync("Applying relationship matrix...", applyRivalryMatrixButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                SetRivalryMatrixStatus($"No snapshot found for session {session.Id}.", resourceBrush("DangerTextBrush"));
                return;
            }

            snapshot.Engine.RivalryMatrix.Enabled = rivalryMatrixEnabledCheckBox.IsChecked == true;
            var activeAgentIds = snapshot.Engine.Agents
                .Where(agent => agent.Active)
                .Select(agent => agent.Id)
                .Where(AgentRosterService.IsParticipantId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plan = BuildRivalryMatrixPlan(
                RivalryMatrixControls().Select(row => new RivalryMatrixItem(
                    row.Source,
                    ShellUiHelpers.SelectedComboTag(row.Target, ""),
                    ShellUiHelpers.SelectedComboTag(row.Stance, "neutral"))),
                activeAgentIds);

            foreach (var (_, _, stancePicker) in RivalryMatrixControls())
            {
                var stance = NormalizeRivalryStance(ShellUiHelpers.SelectedComboTag(stancePicker, "neutral"));
                ShellUiHelpers.SelectComboTag(stancePicker, stance);
            }

            snapshot.Engine.RivalryMatrix.Links.Clear();
            snapshot.Engine.RivalryMatrix.Links.AddRange(plan.Links.Select(link => new AIArena.Core.Models.RivalryLink
            {
                Source = link.Source,
                Target = link.Target,
                Stance = link.Stance
            }));

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_rivalry_matrix_applied", new
            {
                snapshot.Engine.RivalryMatrix.Enabled,
                skipped = plan.SkippedInvalidRules,
                links = plan.Links.Select(link => new { link.Source, link.Target, link.Stance }).ToArray()
            });
            await refreshActiveSessionAsync(Summary(snapshot.Engine.RivalryMatrix.Enabled, plan.Links, activeAgentIds, plan.SkippedInvalidRules));
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
        AutomationProperties.SetName(target, $"{displayStatusValue(source)} relationship target");
        AutomationProperties.SetHelpText(target, $"Choose which active agent {displayStatusValue(source)} should respond to.");
        target.SelectionChanged += (_, _) => RefreshDraftRivalryMatrixPreview();
        var stance = new ComboBox
        {
            Tag = source,
            Padding = new Thickness(7, 5, 7, 5),
            FontSize = 11,
            ToolTip = $"Relationship stance for {displayStatusValue(source)}"
        };
        AutomationProperties.SetName(stance, $"{displayStatusValue(source)} relationship stance");
        AutomationProperties.SetHelpText(stance, $"Choose how {displayStatusValue(source)} should pressure the selected target.");
        stance.SelectionChanged += (_, _) => RefreshDraftRivalryMatrixPreview();
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
            ToolTip = $"{displayStatusValue(source)} relationship rule",
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
        picker.Items.Add(new ComboBoxItem { Content = "Fact-check", Tag = "fact_check" });
        picker.Items.Add(new ComboBoxItem { Content = "Amplify", Tag = "amplify" });
        picker.Items.Add(new ComboBoxItem { Content = "De-escalate", Tag = "deescalate" });
        picker.Items.Add(new ComboBoxItem { Content = "Devil's advocate", Tag = "devils_advocate" });
    }

    internal static string NormalizeRivalryStance(string stance)
    {
        var value = string.IsNullOrWhiteSpace(stance) ? "neutral" : stance.Trim().ToLowerInvariant().Replace("'", "").Replace('-', '_').Replace(' ', '_');
        return value switch
        {
            "challenge"
                or "support"
                or "steelman"
                or "cross_examine"
                or "rival"
                or "fact_check"
                or "amplify"
                or "deescalate"
                or "devils_advocate" => value,
            _ => "neutral"
        };
    }

    internal static IReadOnlyList<RivalryMatrixItem> BuildRivalryPatternDraft(string pattern, IReadOnlyList<string> agentIds)
    {
        var activeIds = agentIds
            .Select(NormalizeAgentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeIds.Length < 2)
        {
            return [];
        }

        var links = new List<RivalryMatrixItem>();
        switch (NormalizePattern(pattern))
        {
            case "round_robin_challenge":
                AddRing(links, activeIds, "challenge");
                break;
            case "mutual_rivals":
                for (var index = 0; index + 1 < activeIds.Length; index += 2)
                {
                    links.Add(new RivalryMatrixItem(activeIds[index], activeIds[index + 1], "rival"));
                    links.Add(new RivalryMatrixItem(activeIds[index + 1], activeIds[index], "rival"));
                }
                if (activeIds.Length % 2 == 1)
                {
                    links.Add(new RivalryMatrixItem(activeIds[^1], activeIds[0], "challenge"));
                }
                break;
            case "evidence_ladder":
                AddRing(links, activeIds, ["fact_check", "cross_examine", "steelman"]);
                break;
            case "support_chain":
                AddRing(links, activeIds, ["support", "amplify"]);
                break;
            case "deescalation_ring":
                AddRing(links, activeIds, "deescalate");
                break;
            case "devils_triangle":
                AddRing(links, activeIds.Take(Math.Min(3, activeIds.Length)).ToArray(), ["devils_advocate", "fact_check", "steelman"]);
                break;
            case "skeptic_sweep":
                AddRing(links, activeIds, ["fact_check", "cross_examine"]);
                break;
            case "paired_crossfire":
                for (var index = 0; index + 1 < activeIds.Length; index += 2)
                {
                    links.Add(new RivalryMatrixItem(activeIds[index], activeIds[index + 1], "challenge"));
                    links.Add(new RivalryMatrixItem(activeIds[index + 1], activeIds[index], "challenge"));
                }
                if (activeIds.Length % 2 == 1)
                {
                    links.Add(new RivalryMatrixItem(activeIds[^1], activeIds[0], "fact_check"));
                }
                break;
            case "spotlight_defense":
                var spotlight = activeIds[^1];
                for (var index = 0; index < activeIds.Length; index++)
                {
                    var source = activeIds[index];
                    links.Add(source.Equals(spotlight, StringComparison.OrdinalIgnoreCase)
                        ? new RivalryMatrixItem(source, activeIds[0], "steelman")
                        : new RivalryMatrixItem(source, spotlight, index % 2 == 0 ? "challenge" : "fact_check"));
                }
                break;
        }

        return BuildRivalryMatrixPlan(links, activeIds).Links;
    }

    internal static RivalryMatrixPlan BuildRivalryMatrixPlan(IEnumerable<RivalryMatrixItem> draftLinks, IReadOnlyCollection<string> activeAgentIds)
    {
        var activeSet = activeAgentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<RivalryMatrixItem>();
        var skipped = 0;
        foreach (var draft in draftLinks)
        {
            var source = NormalizeAgentId(draft.Source);
            var target = NormalizeAgentId(draft.Target);
            var stance = NormalizeRivalryStance(draft.Stance);
            if (string.IsNullOrWhiteSpace(target) || stance.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(source)
                || source.Equals(target, StringComparison.OrdinalIgnoreCase)
                || !activeSet.Contains(source)
                || !activeSet.Contains(target)
                || !seenSources.Add(source))
            {
                skipped++;
                continue;
            }

            links.Add(new RivalryMatrixItem(source, target, stance));
        }

        return new RivalryMatrixPlan(links, skipped);
    }

    internal static string Summary(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string>? activeAgentIds = null,
        int skippedInvalidRules = 0)
    {
        var activeIds = activeAgentIds ?? links
            .SelectMany(link => new[] { link.Source, link.Target })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var plan = BuildRivalryMatrixPlan(links, activeIds);
        var active = plan.Links.Count;
        var skipped = skippedInvalidRules + plan.SkippedInvalidRules;
        var stanceSummary = RivalryStanceSummary(plan.Links);
        var mutualSummary = MutualPressureSummary(plan.Links);
        var topology = Topology(enabled, plan.Links, activeIds, skipped);
        var coverageSummary = active > 0 ? $"; coverage {topology.ActiveSources}/{topology.TotalSources}" : "";
        var hotspotSummary = topology.HotspotIncoming > 1 ? $", hotspot {DisplayMatrixLabel(topology.HotspotTarget)} x{topology.HotspotIncoming}" : "";
        var invalidSuffix = skipped > 0 ? $" {skipped} invalid rule(s) ignored." : "";
        if (!enabled)
        {
            return active == 0
                ? $"Relationship pressure is off.{invalidSuffix}"
                : $"{active} relationship rule(s) saved ({stanceSummary}{mutualSummary}{coverageSummary}{hotspotSummary}), currently disabled.{invalidSuffix}";
        }

        return active == 0
            ? $"Relationship pressure enabled with neutral rules.{invalidSuffix}"
            : $"{active} relationship rule(s) active: {stanceSummary}{mutualSummary}{coverageSummary}{hotspotSummary}.{invalidSuffix}";
    }

    internal static RelationshipTopology Topology(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string> activeAgentIds,
        int skippedInvalidRules = 0)
    {
        var activeIds = activeAgentIds
            .Select(NormalizeAgentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var plan = BuildRivalryMatrixPlan(links, activeIds);
        var sources = plan.Links
            .Select(link => NormalizeAgentId(link.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unassigned = activeIds
            .Where(id => !sources.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var hotspot = plan.Links
            .GroupBy(link => NormalizeAgentId(link.Target), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return new RelationshipTopology(
            enabled,
            plan.Links.Count,
            sources.Length,
            activeIds.Length,
            unassigned.Length,
            hotspot?.Key ?? "",
            hotspot?.Count() ?? 0,
            MutualPressurePairs(plan.Links),
            skippedInvalidRules + plan.SkippedInvalidRules);
    }

    internal static string RelationshipInsight(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string> activeAgentIds,
        int skippedInvalidRules = 0)
    {
        var topology = Topology(enabled, links, activeAgentIds, skippedInvalidRules);
        if (!enabled)
        {
            return topology.ActiveRules == 0
                ? "Pressure graph off; all active agents use neutral debate pressure."
                : $"Pressure graph saved but disabled; {topology.ActiveRules} rule(s) will stay dormant until enabled.";
        }

        if (topology.ActiveRules == 0)
        {
            return "Pressure graph enabled with neutral rules; draft targets or choose a pattern before applying.";
        }

        var parts = new List<string>
        {
            $"Pressure graph covers {topology.ActiveSources}/{topology.TotalSources} active agents"
        };
        if (topology.UnassignedSources > 0)
        {
            parts.Add($"{topology.UnassignedSources} source(s) still neutral");
        }

        if (topology.HotspotIncoming > 1)
        {
            parts.Add($"{DisplayMatrixLabel(topology.HotspotTarget)} receives {topology.HotspotIncoming} incoming rule(s)");
        }

        if (topology.MutualPairs > 0)
        {
            parts.Add($"{topology.MutualPairs} mutual pair(s)");
        }

        if (topology.SkippedInvalidRules > 0)
        {
            parts.Add($"{topology.SkippedInvalidRules} invalid rule(s) ignored");
        }

        return string.Join("; ", parts) + ".";
    }

    internal static string[] RelationshipPreviewLines(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string> activeAgentIds)
    {
        return RelationshipPreviewItems(enabled, links, activeAgentIds)
            .Select(item => $"{item.Route} / {item.Stance}")
            .ToArray();
    }

    private static IReadOnlyList<RelationshipPreviewItem> RelationshipPreviewItems(
        bool enabled,
        IReadOnlyList<RivalryMatrixItem> links,
        IReadOnlyCollection<string> activeAgentIds)
    {
        var plan = BuildRivalryMatrixPlan(links, activeAgentIds);
        if (!enabled)
        {
            return
            [
                new RelationshipPreviewItem("", "Pressure graph off", plan.Links.Count == 0 ? "neutral debate" : $"{plan.Links.Count} saved rule(s) dormant")
            ];
        }

        if (plan.Links.Count == 0)
        {
            return
            [
                new RelationshipPreviewItem("", "Neutral pressure", "no active rules")
            ];
        }

        return plan.Links
            .Select(link => new RelationshipPreviewItem(
                link.Source,
                $"{DisplayMatrixLabel(link.Source)} -> {DisplayMatrixLabel(link.Target)}",
                StanceLabel(link.Stance)))
            .ToArray();
    }

    private static bool IsValidRivalryLink(RivalryMatrixItem link, IReadOnlyCollection<string> activeAgentIds)
    {
        return BuildRivalryMatrixPlan([link], activeAgentIds).Links.Count == 1;
    }

    private static string RivalryStanceSummary(IReadOnlyList<RivalryMatrixItem> links)
    {
        var counts = links
            .GroupBy(link => NormalizeRivalryStance(link.Stance), StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{StanceLabel(group.Key)} {group.Count()}")
            .ToArray();
        return counts.Length == 0 ? "neutral" : string.Join(", ", counts);
    }

    internal static int MutualPressurePairs(IReadOnlyList<RivalryMatrixItem> links)
    {
        var normalized = links
            .Select(link => new RivalryMatrixItem(NormalizeAgentId(link.Source), NormalizeAgentId(link.Target), NormalizeRivalryStance(link.Stance)))
            .Where(link => !string.IsNullOrWhiteSpace(link.Source) && !string.IsNullOrWhiteSpace(link.Target))
            .ToArray();
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var link in normalized)
        {
            var reverseExists = normalized.Any(other =>
                other.Source.Equals(link.Target, StringComparison.OrdinalIgnoreCase)
                && other.Target.Equals(link.Source, StringComparison.OrdinalIgnoreCase));
            if (!reverseExists)
            {
                continue;
            }

            var first = string.Compare(link.Source, link.Target, StringComparison.OrdinalIgnoreCase) <= 0 ? link.Source : link.Target;
            var second = first.Equals(link.Source, StringComparison.OrdinalIgnoreCase) ? link.Target : link.Source;
            if (seenPairs.Add($"{first}:{second}"))
            {
                count++;
            }
        }

        return count;
    }

    private static string MutualPressureSummary(IReadOnlyList<RivalryMatrixItem> links)
    {
        var mutualPairs = MutualPressurePairs(links);
        return mutualPairs > 0 ? $", mutual pairs {mutualPairs}" : "";
    }

    private static string StanceLabel(string stance)
    {
        return NormalizeRivalryStance(stance) switch
        {
            "fact_check" => "fact-check",
            "devils_advocate" => "devil's advocate",
            var normalized => normalized.Replace('_', ' ')
        };
    }

    private static string DisplayMatrixLabel(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Trim().Replace('_', ' ').Replace('-', ' ');
    }

    private static string NormalizeAgentId(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? "" : id.Trim().ToLowerInvariant();
    }

    private static void AddRing(List<RivalryMatrixItem> links, IReadOnlyList<string> agentIds, string stance)
    {
        AddRing(links, agentIds, [stance]);
    }

    private static void AddRing(List<RivalryMatrixItem> links, IReadOnlyList<string> agentIds, IReadOnlyList<string> stances)
    {
        for (var index = 0; index < agentIds.Count; index++)
        {
            var source = agentIds[index];
            var target = agentIds[(index + 1) % agentIds.Count];
            links.Add(new RivalryMatrixItem(source, target, stances[index % stances.Count]));
        }
    }

    private static string NormalizePattern(string pattern)
    {
        var value = string.IsNullOrWhiteSpace(pattern) ? "custom" : pattern.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return value switch
        {
            "round_robin_challenge"
                or "mutual_rivals"
                or "evidence_ladder"
                or "support_chain"
                or "deescalation_ring"
                or "devils_triangle"
                or "skeptic_sweep"
                or "paired_crossfire"
                or "spotlight_defense" => value,
            _ => "custom"
        };
    }

    private static string PatternLabel(string pattern)
    {
        return NormalizePattern(pattern) switch
        {
            "round_robin_challenge" => "Round-robin challenge",
            "mutual_rivals" => "Mutual rivals",
            "evidence_ladder" => "Evidence ladder",
            "support_chain" => "Support chain",
            "deescalation_ring" => "De-escalation ring",
            "devils_triangle" => "Devil's triangle",
            "skeptic_sweep" => "Skeptic sweep",
            "paired_crossfire" => "Paired crossfire",
            "spotlight_defense" => "Spotlight defense",
            _ => "Custom relationship pattern"
        };
    }



    private sealed record RivalryMatrixControlRow(
        string Source,
        ComboBox Target,
        ComboBox Stance);

    internal sealed record RivalryMatrixPlan(
        IReadOnlyList<RivalryMatrixItem> Links,
        int SkippedInvalidRules);

    internal sealed record RelationshipTopology(
        bool Enabled,
        int ActiveRules,
        int ActiveSources,
        int TotalSources,
        int UnassignedSources,
        string HotspotTarget,
        int HotspotIncoming,
        int MutualPairs,
        int SkippedInvalidRules);

    private sealed record RelationshipPreviewItem(
        string Source,
        string Route,
        string Stance);
}
