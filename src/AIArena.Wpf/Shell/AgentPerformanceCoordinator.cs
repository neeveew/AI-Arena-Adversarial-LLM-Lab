using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using CoreVoiceAdherenceDiagnostic = AIArena.Core.Models.VoiceAdherenceDiagnostic;

namespace AIArena.Wpf;

internal sealed class AgentPerformanceCoordinator
{
    private readonly VoiceStyleAdherenceService voiceStyleAdherenceService;
    private readonly Panel agentPerformanceItems;
    private readonly Popup detailPopup;
    private readonly Panel detailContent;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string, bool, string> formatParticipantTitle;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<string, string> displayInlineStatus;
    private readonly Func<string, string> shortModelName;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Func<int, string> formatDuration;
    private readonly Func<string, Brush> voiceAdherenceStateAccent;
    private readonly Func<CoreVoiceAdherenceDiagnostic, Brush> voiceAdherenceDiagnosticAccent;
    private readonly Func<string?, int, string, string> compactPreview;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<bool> fullCardsPreferred;
    private readonly Dictionary<string, FrameworkElement> rows = new(StringComparer.OrdinalIgnoreCase);

    private ArenaViewSnapshot? lastSnapshot;
    private string? activeDetailId;
    private bool expanded;
    private bool renderPending;

    internal int DiagnosticRenderCount { get; private set; }
    internal int DiagnosticMessageVisits { get; private set; }
    internal IReadOnlyList<AgentPerformanceStats> LastStats { get; private set; } = [];

    public AgentPerformanceCoordinator(
        VoiceStyleAdherenceService voiceStyleAdherenceService,
        Panel agentPerformanceItems,
        Popup detailPopup,
        Panel detailContent,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string, bool, string> formatParticipantTitle,
        Func<string, string> displayStatusValue,
        Func<string, string> displayInlineStatus,
        Func<string, string> shortModelName,
        Func<int, string> formatCompactNumber,
        Func<int, string> formatDuration,
        Func<string, Brush> voiceAdherenceStateAccent,
        Func<CoreVoiceAdherenceDiagnostic, Brush> voiceAdherenceDiagnosticAccent,
        Func<string?, int, string, string> compactPreview,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<bool>? fullCardsPreferred = null)
    {
        this.fullCardsPreferred = fullCardsPreferred ?? (() => false);
        this.voiceStyleAdherenceService = voiceStyleAdherenceService;
        this.agentPerformanceItems = agentPerformanceItems;
        this.detailPopup = detailPopup;
        this.detailContent = detailContent;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.formatParticipantTitle = formatParticipantTitle;
        this.displayStatusValue = displayStatusValue;
        this.displayInlineStatus = displayInlineStatus;
        this.shortModelName = shortModelName;
        this.formatCompactNumber = formatCompactNumber;
        this.formatDuration = formatDuration;
        this.voiceAdherenceStateAccent = voiceAdherenceStateAccent;
        this.voiceAdherenceDiagnosticAccent = voiceAdherenceDiagnosticAccent;
        this.compactPreview = compactPreview;
        this.blendBrush = blendBrush;
    }

    public void ObserveSnapshot(ArenaViewSnapshot snapshot)
    {
        lastSnapshot = snapshot;
        renderPending = true;
        if (expanded)
        {
            RenderPendingSnapshot();
        }
    }

    public void SetExpanded(bool value)
    {
        expanded = value;
        if (expanded)
        {
            RenderPendingSnapshot();
        }
    }

    internal void RenderPendingSnapshot()
    {
        if (!expanded || !renderPending || lastSnapshot is null)
        {
            return;
        }

        renderPending = false;
        Populate(lastSnapshot);
    }

    private void Populate(ArenaViewSnapshot snapshot)
    {
        DiagnosticRenderCount++;
        var openDetailId = detailPopup.IsOpen ? activeDetailId : null;
        FrameworkElement? refreshedDetailTarget = null;
        AgentPerformanceStats? refreshedDetailStats = null;

        var stats = ComputeStats(snapshot);
        LastStats = stats;

        var maxTokens = Math.Max(1, stats.Select(item => item.Tokens).DefaultIfEmpty(0).Max());
        var fullCards = fullCardsPreferred();

        // In adaptive mode there is nothing to report before the first turn - the roster
        // and status already live in Live Agents, so a single hint keeps the rail clean.
        if (!fullCards && stats.All(item => item.Calls == 0) && !stats.Any(item => IsBusyStatus(item.Status)))
        {
            if (!string.IsNullOrWhiteSpace(openDetailId))
            {
                CloseDetail();
            }

            ReconcileRows([new TextBlock
            {
                Text = "Metrics appear after the first turn.",
                Foreground = resourceBrush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            }], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        // Only the agent doing the work earns the full card; everyone else collapses to a
        // slim row so the rail stays glanceable instead of five tall cards of idle zeros.
        var lastSpeakerId = stats
            .Where(item => item.LastMessageOrdinal >= 0)
            .OrderByDescending(item => item.LastMessageOrdinal)
            .FirstOrDefault()?.AgentId ?? "";
        var highlightId = stats.FirstOrDefault(item => IsBusyStatus(item.Status))?.AgentId ?? lastSpeakerId;

        var orderedRows = new List<FrameworkElement>(stats.Count);
        var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in stats)
        {
            var expanded = fullCards
                || (!string.IsNullOrWhiteSpace(highlightId)
                    && item.AgentId.Equals(highlightId, StringComparison.OrdinalIgnoreCase));
            FrameworkElement row = expanded ? CreateRow(item, maxTokens) : CreateCompactRow(item);
            liveIds.Add(item.AgentId);
            if (rows.TryGetValue(item.AgentId, out var existing)
                && CanUpdateRowInPlace(existing, row))
            {
                UpdateRowInPlace(existing, row);
                row = existing;
            }
            else
            {
                rows[item.AgentId] = row;
            }
            orderedRows.Add(row);
            if (!string.IsNullOrWhiteSpace(openDetailId)
                && item.AgentId.Equals(openDetailId, StringComparison.OrdinalIgnoreCase))
            {
                refreshedDetailTarget = row;
                refreshedDetailStats = item;
            }
        }

        ReconcileRows(orderedRows, liveIds);

        if (agentPerformanceItems.Children.Count == 0)
        {
            agentPerformanceItems.Children.Add(new TextBlock
            {
                Text = "No agent metrics yet.",
                Foreground = resourceBrush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!string.IsNullOrWhiteSpace(openDetailId))
        {
            if (refreshedDetailTarget is null || refreshedDetailStats is null)
            {
                CloseDetail();
            }
            else
            {
                RenderDetail(refreshedDetailStats, snapshot, refreshedDetailTarget, resetPopup: false);
            }
        }
    }

    internal IReadOnlyList<AgentPerformanceStats> ComputeStats(ArenaViewSnapshot snapshot)
    {
        var aggregates = AggregateMessages(snapshot.Messages, out var spokenIds);
        return snapshot.Agents
            .Where(agent => agent.Active || spokenIds.Contains(agent.Id))
            .Append(new AgentState(
                "narrator",
                "Narrator",
                snapshot.NarratorStatus,
                snapshot.NarratorPersona,
                snapshot.NarratorVoiceStyle,
                "",
                snapshot.NarratorAccentColor,
                snapshot.NarratorModel,
                true,
                snapshot.NarratorLocked,
                []))
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateStats(snapshot, group.First(), aggregates.GetValueOrDefault(group.Key)))
            .ToArray();
    }

    public void RefreshDensity()
    {
        if (lastSnapshot is not null)
        {
            renderPending = true;
            RenderPendingSnapshot();
        }
    }

    public void CloseDetail()
    {
        activeDetailId = null;
        detailPopup.IsOpen = false;
        detailPopup.PlacementTarget = null;
        detailContent.Children.Clear();
    }

    private AgentPerformanceStats CreateStats(
        ArenaViewSnapshot snapshot,
        AgentState agent,
        AgentPerformanceAggregate? aggregate)
    {
        aggregate ??= new AgentPerformanceAggregate();
        var messages = aggregate.Messages;
        var voiceDiagnostics = aggregate.VoiceDiagnostics;
        var voiceScore = voiceDiagnostics.Length == 0
            ? 0
            : (int)Math.Round(voiceDiagnostics.Average(diagnostic => diagnostic.Score));

        return new AgentPerformanceStats(
            agent.Id,
            string.IsNullOrWhiteSpace(agent.Name) ? displayStatusValue(agent.Id) : agent.Name,
            displayInlineStatus(agent.Status),
            agent.Model,
            messages.Count,
            aggregate.Tokens,
            aggregate.Context,
            aggregate.LatencyCount == 0 ? 0 : (int)(aggregate.LatencyTotal / (double)aggregate.LatencyCount),
            aggregate.LastLatency,
            aggregate.TokensPerSecondCount == 0 ? 0 : Math.Round(aggregate.TokensPerSecondTotal / aggregate.TokensPerSecondCount, 1),
            aggregate.TimeToFirstTokenCount == 0 ? 0 : (int)Math.Round(aggregate.TimeToFirstTokenTotal / (double)aggregate.TimeToFirstTokenCount),
            aggregate.Failures,
            aggregate.EmptyResponses,
            aggregate.InternetRequests,
            voiceScore,
            RoleStyleCatalog.VoiceAdherenceState(voiceScore, voiceDiagnostics.Length),
            voiceDiagnostics.Length,
            aggregate.Activity.ToArray(),
            agent.InternetSources ?? aggregate.LatestInternetSources,
            aggregate.LastMessageOrdinal);
    }

    private Dictionary<string, AgentPerformanceAggregate> AggregateMessages(
        IReadOnlyList<TranscriptMessage> messages,
        out HashSet<string> spokenIds)
    {
        var aggregates = new Dictionary<string, AgentPerformanceAggregate>(StringComparer.OrdinalIgnoreCase);
        spokenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < messages.Count; ordinal++)
        {
            var message = messages[ordinal];
            DiagnosticMessageVisits++;
            var speakerId = message.SpeakerId?.Trim() ?? "";
            var requesterId = message.InternetRequester?.Trim() ?? "";
            AgentPerformanceAggregate? speaker = null;
            if (speakerId.Length > 0)
            {
                spokenIds.Add(speakerId);
                speaker = GetAggregate(aggregates, speakerId);
                speaker.LastMessageOrdinal = ordinal;
            }

            var internetMessage = message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase);
            if (requesterId.Length > 0)
            {
                GetAggregate(aggregates, requesterId).InternetRequests++;
            }
            if (speaker is not null
                && (!string.IsNullOrWhiteSpace(message.InternetTool) || internetMessage)
                && !speakerId.Equals(requesterId, StringComparison.OrdinalIgnoreCase))
            {
                speaker.InternetRequests++;
            }

            if (message.InternetSources.Count > 0)
            {
                var sourceSummary = new AgentInternetSourceSummary(
                    message.InternetQuery,
                    message.InternetCheckedAt,
                    message.InternetSources);
                if (requesterId.Length > 0)
                {
                    GetAggregate(aggregates, requesterId).LatestInternetSources = sourceSummary;
                }
                if (speaker is not null
                    && (requesterId.Length == 0
                        || !requesterId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)))
                {
                    speaker.LatestInternetSources = sourceSummary;
                }
            }

            if (speaker is null || internetMessage)
            {
                continue;
            }

            speaker.Messages.Add(message);
            speaker.Tokens += Math.Max(message.CompletionTokens, 0);
            speaker.Context = Math.Max(speaker.Context, message.PromptTokens);
            if (message.LatencyMs > 0)
            {
                speaker.LatencyTotal += message.LatencyMs;
                speaker.LatencyCount++;
                speaker.LastLatency = message.LatencyMs;
            }
            if (message.TokensPerSecond > 0)
            {
                speaker.TokensPerSecondTotal += message.TokensPerSecond;
                speaker.TokensPerSecondCount++;
            }
            if (message.TimeToFirstTokenMs > 0)
            {
                speaker.TimeToFirstTokenTotal += message.TimeToFirstTokenMs;
                speaker.TimeToFirstTokenCount++;
            }
            if (message.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                speaker.Failures++;
            }
            if (string.IsNullOrWhiteSpace(message.Text)
                || message.Text.Contains("(empty model response)", StringComparison.OrdinalIgnoreCase))
            {
                speaker.EmptyResponses++;
            }

            speaker.Activity.Enqueue(Math.Max(1, Math.Max(message.CompletionTokens, message.TotalTokens)));
            if (speaker.Activity.Count > 12)
            {
                speaker.Activity.Dequeue();
            }

            var diagnostic = voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text);
            if (!diagnostic.State.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                speaker.VoiceDiagnosticsList.Add(diagnostic);
            }
        }

        foreach (var aggregate in aggregates.Values)
        {
            aggregate.FreezeDiagnostics();
        }

        return aggregates;
    }

    private static AgentPerformanceAggregate GetAggregate(
        IDictionary<string, AgentPerformanceAggregate> aggregates,
        string agentId)
    {
        if (!aggregates.TryGetValue(agentId, out var aggregate))
        {
            aggregate = new AgentPerformanceAggregate();
            aggregates[agentId] = aggregate;
        }

        return aggregate;
    }

    private void ReconcileRows(
        IReadOnlyList<FrameworkElement> orderedRows,
        IReadOnlySet<string> liveIds)
    {
        foreach (var staleId in rows.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            rows.Remove(staleId);
        }

        for (var index = 0; index < orderedRows.Count; index++)
        {
            var row = orderedRows[index];
            if (index < agentPerformanceItems.Children.Count
                && ReferenceEquals(agentPerformanceItems.Children[index], row))
            {
                continue;
            }

            var existingIndex = agentPerformanceItems.Children.IndexOf(row);
            if (existingIndex >= 0)
            {
                agentPerformanceItems.Children.RemoveAt(existingIndex);
            }
            agentPerformanceItems.Children.Insert(index, row);
        }

        while (agentPerformanceItems.Children.Count > orderedRows.Count)
        {
            agentPerformanceItems.Children.RemoveAt(agentPerformanceItems.Children.Count - 1);
        }
    }

    private static bool CanUpdateRowInPlace(FrameworkElement current, FrameworkElement replacement) =>
        current is ShellPopupOpenerCard && replacement is ShellPopupOpenerCard;

    private static void UpdateRowInPlace(FrameworkElement current, FrameworkElement replacement)
    {
        if (current is not ShellPopupOpenerCard target || replacement is not ShellPopupOpenerCard source)
        {
            return;
        }

        var focusedElement = Keyboard.FocusedElement as FrameworkElement;
        var focusedAutomationName = focusedElement is not null && IsLogicalDescendant(target, focusedElement)
            ? AutomationProperties.GetName(focusedElement)
            : "";

        target.Background = source.Background;
        target.BorderBrush = source.BorderBrush;
        target.BorderThickness = source.BorderThickness;
        target.CornerRadius = source.CornerRadius;
        target.Padding = source.Padding;
        target.Margin = source.Margin;
        target.ToolTip = source.ToolTip;
        target.Cursor = source.Cursor;
        var child = source.Child;
        source.Child = null;
        target.Child = child;
        target.Tag = source.Tag;
        AutomationProperties.SetName(target, AutomationProperties.GetName(source));
        AutomationProperties.SetHelpText(target, AutomationProperties.GetHelpText(source));
        if (!string.IsNullOrWhiteSpace(focusedAutomationName))
        {
            var focusTarget = LogicalDescendants(target)
                .FirstOrDefault(element => element.Focusable
                    && AutomationProperties.GetName(element)
                        .Equals(focusedAutomationName, StringComparison.Ordinal));
            if (focusTarget is not null)
            {
                target.Dispatcher.BeginInvoke(
                    () => focusTarget.Focus(),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }

    private static bool IsLogicalDescendant(DependencyObject root, DependencyObject candidate) =>
        ReferenceEquals(root, candidate)
        || LogicalDescendants(root).Any(element => ReferenceEquals(element, candidate));

    private static IEnumerable<FrameworkElement> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement element)
            {
                yield return element;
            }

            foreach (var descendant in LogicalDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsBusyStatus(string status)
    {
        var normalized = (status ?? "").Trim();
        return normalized.Contains("speak", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("think", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("running", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("generating", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("responding", StringComparison.OrdinalIgnoreCase);
    }

    private Border CreateCompactRow(AgentPerformanceStats stats)
    {
        var accent = accentForSpeaker(stats.AgentId);
        var displayTitle = formatParticipantTitle(stats.AgentId, stats.Name, true);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = displayStatusValue(displayTitle),
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var tokensText = new TextBlock
        {
            Text = stats.Tokens > 0 ? $"{formatCompactNumber(stats.Tokens)} tok" : "no turns yet",
            Foreground = stats.Failures > 0
                ? resourceBrush("DangerTextBrush")
                : stats.Tokens > 0 ? accent : resourceBrush("MutedTextBrush"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (stats.InternetSources is { Sources.Count: > 0 } internetSources)
        {
            var sourceButton = AgentInternetSourcesPresenter.CreateButton(
                internetSources,
                resourceBrush,
                blendBrush,
                $"Show internet sources for {stats.Name}",
                22);
            sourceButton.Margin = new Thickness(0, 0, 7, 0);
            Grid.SetColumn(sourceButton, 1);
            grid.Children.Add(sourceButton);
        }

        Grid.SetColumn(tokensText, 2);
        grid.Children.Add(tokensText);

        // Status pills stay with the Live Agents roster; this surface is telemetry only.
        var latencyText = new TextBlock
        {
            Text = stats.LastLatencyMs > 0 ? formatDuration(stats.LastLatencyMs) : "",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(latencyText, 3);
        grid.Children.Add(latencyText);

        var alertSummary = stats.Failures > 0
            ? $"{displayTitle}: {stats.Failures} failed turn{(stats.Failures == 1 ? "" : "s")}. Click for details."
            : $"{displayTitle}: click for details.";
        var card = new ShellPopupOpenerCard
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.05),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.22),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = alertSummary,
            Cursor = Cursors.Hand,
            Child = grid
        };
        ConfigureDetailCard(card, stats, displayTitle);

        return card;
    }

    private Border CreateRow(AgentPerformanceStats stats, int maxTokens)
    {
        var accent = accentForSpeaker(stats.AgentId);
        var displayTitle = formatParticipantTitle(stats.AgentId, stats.Name, true);
        var stack = new StackPanel();

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 3)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = displayStatusValue(displayTitle),
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(title);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (stats.InternetSources is { Sources.Count: > 0 } internetSources)
        {
            var sourceButton = AgentInternetSourcesPresenter.CreateButton(
                internetSources,
                resourceBrush,
                blendBrush,
                $"Show internet sources for {stats.Name}",
                23);
            sourceButton.Margin = new Thickness(0, 0, 6, 0);
            headerActions.Children.Add(sourceButton);
        }

        headerActions.Children.Add(CreateStatusPill(stats.Status, accent));
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        stack.Children.Add(header);

        var model = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(stats.Model) ? "model not assigned" : shortModelName(stats.Model),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(model);

        var metrics = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.Children.Add(CreatePrimaryMetric("Tokens", formatCompactNumber(stats.Tokens), accent));

        var secondaryMetrics = new UniformGrid
        {
            Rows = 1,
            Columns = 3,
            MinWidth = 174,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        secondaryMetrics.Children.Add(CreateInlineMetricPill("Turns", stats.Calls.ToString(System.Globalization.CultureInfo.InvariantCulture), accent));
        secondaryMetrics.Children.Add(CreateInlineMetricPill("Speed", FormatTokensPerSecond(stats.AverageTokensPerSecond), resourceBrush("PrimaryBorderBrush")));
        secondaryMetrics.Children.Add(CreateInlineMetricPill("Ctx", stats.Context > 0 ? formatCompactNumber(stats.Context) : "-", resourceBrush("GammaAccentBrush")));
        Grid.SetColumn(secondaryMetrics, 1);
        metrics.Children.Add(secondaryMetrics);
        stack.Children.Add(metrics);

        var activityRow = new Grid();
        activityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        activityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hasActivity = stats.Activity.Any(value => value > 0.001);
        activityRow.Children.Add(CreateTokenTrack(stats.Tokens, maxTokens, accent));

        FrameworkElement activityMarker = hasActivity
            ? CreateActivitySparkline(stats, accent)
            : CreateIdleActivityMarker(accent);
        Grid.SetColumn(activityMarker, 1);
        activityRow.Children.Add(activityMarker);

        stack.Children.Add(activityRow);

        var alerts = new List<string>();
        if (stats.Failures > 0)
        {
            alerts.Add($"{stats.Failures} fail");
        }
        if (stats.EmptyResponses > 0)
        {
            alerts.Add($"{stats.EmptyResponses} empty");
        }
        if (stats.InternetRequests > 0)
        {
            alerts.Add($"{stats.InternetRequests} web");
        }
        if (stats.VoiceAdherenceSamples > 0 && !stats.VoiceAdherenceState.Equals("strong", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add($"style {RoleStyleCatalog.VoiceAdherenceDisplayState(stats.VoiceAdherenceState)} {stats.VoiceAdherenceScore}");
        }
        if (stats.LastLatencyMs > 0)
        {
            alerts.Add($"last {formatDuration(stats.LastLatencyMs)}");
        }
        if (stats.AverageTokensPerSecond > 0)
        {
            alerts.Add($"{FormatTokensPerSecond(stats.AverageTokensPerSecond)} tok/s");
        }

        var card = new ShellPopupOpenerCard
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 0, 8),
            ToolTip = alerts.Count == 0 ? $"{displayTitle}: no warnings" : $"{displayTitle}: {string.Join(", ", alerts)}",
            Cursor = Cursors.Hand,
            Child = stack
        };
        ConfigureDetailCard(card, stats, displayTitle);

        return card;
    }

    private StackPanel CreatePrimaryMetric(string label, string value, Brush accent)
    {
        return new StackPanel
        {
            MinWidth = 74,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Foreground = resourceBrush("MutedTextBrush"),
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = value,
                    Foreground = accent,
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
    }

    private Border CreateInlineMetricPill(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.07),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.28),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 6, 4),
            Margin = new Thickness(4, 0, 0, 0),
            MinWidth = 52,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 8.5,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = value,
                        Foreground = accent,
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Grid CreateTokenTrack(int tokens, int maxTokens, Brush accent)
    {
        var track = new Grid
        {
            Height = 15,
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        track.Children.Add(new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.16),
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.9
        });

        var fill = new Border
        {
            Background = accent,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = tokens <= 0 ? 0.3 : 0.86,
            Width = tokens <= 0 ? 12 : 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        track.Children.Add(fill);

        void UpdateFill()
        {
            var trackWidth = track.ActualWidth > 0 ? track.ActualWidth : 120;
            fill.Width = tokens <= 0
                ? Math.Min(14, trackWidth)
                : Math.Clamp(trackWidth * Math.Clamp(tokens / (double)Math.Max(1, maxTokens), 0.08, 1), 18, trackWidth);
        }

        track.Loaded += (_, _) => UpdateFill();
        track.SizeChanged += (_, _) => UpdateFill();

        return track;
    }

    private void ShowDetail(AgentPerformanceStats stats, FrameworkElement target)
    {
        if (lastSnapshot is null)
        {
            return;
        }

        RenderDetail(stats, lastSnapshot, target, resetPopup: true);
    }

    private void ConfigureDetailCard(Border card, AgentPerformanceStats stats, string displayTitle)
    {
        card.Tag = stats;
        card.Focusable = true;
        KeyboardNavigation.SetIsTabStop(card, true);
        card.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Arena.FocusVisual");
        AutomationProperties.SetName(card, $"Open agent performance detail for {displayTitle}");
        AutomationProperties.SetHelpText(card, "Open turn, speed, context-use, quality, and failure details for this agent.");
        if (card is ShellPopupOpenerCard openerCard)
        {
            openerCard.Invoked += (_, _) =>
            {
                if (card.Tag is AgentPerformanceStats current)
                {
                    ShowDetail(current, card);
                }
            };
            return;
        }

        card.MouseLeftButtonUp += (_, e) =>
        {
            card.Focus();
            if (card.Tag is AgentPerformanceStats current)
            {
                ShowDetail(current, card);
            }
            e.Handled = true;
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return or Key.Space))
            {
                return;
            }

            if (card.Tag is AgentPerformanceStats current)
            {
                ShowDetail(current, card);
            }
            e.Handled = true;
        };
    }

    private void RenderDetail(
        AgentPerformanceStats stats,
        ArenaViewSnapshot snapshot,
        FrameworkElement target,
        bool resetPopup)
    {
        if (resetPopup)
        {
            detailPopup.IsOpen = false;
        }

        activeDetailId = stats.AgentId;
        detailContent.Children.Clear();
        detailContent.Children.Add(CreateDetail(stats, snapshot));
        detailPopup.PlacementTarget = target;
        detailPopup.IsOpen = true;
    }

    private StackPanel CreateDetail(AgentPerformanceStats stats, ArenaViewSnapshot snapshot)
    {
        var accent = accentForSpeaker(stats.AgentId);
        var agent = FindAgent(snapshot, stats.AgentId);
        var notesCount = agent?.PrivateNotes.Count ?? 0;
        var recentTurns = snapshot.Messages
            .Where(message => message.SpeakerId.Equals(stats.AgentId, StringComparison.OrdinalIgnoreCase)
                && !message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4)
            .Reverse()
            .ToArray();
        var displayTitle = formatParticipantTitle(stats.AgentId, stats.Name, true);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = displayStatusValue(displayTitle),
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(stats.Model) ? "model not assigned" : stats.Model,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        });
        var voiceChipText = RoleStyleCatalog.VoiceStyleChipText(agent?.VoiceStyle);
        if (!string.IsNullOrWhiteSpace(voiceChipText))
        {
            panel.Children.Add(CreateVoiceChip(voiceChipText, accent));
        }
        if (stats.VoiceAdherenceSamples > 0)
        {
            panel.Children.Add(CreateVoiceAdherenceDetail(stats, recentTurns));
        }

        var metrics = new UniformGrid
        {
            Columns = 4,
            Rows = 3,
            Margin = new Thickness(0, 0, -7, 10)
        };
        metrics.Children.Add(CreateDetailMetric("Turns", stats.Calls.ToString(System.Globalization.CultureInfo.InvariantCulture), accent));
        metrics.Children.Add(CreateDetailMetric("Tokens", formatCompactNumber(stats.Tokens), resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Context", stats.Context > 0 ? formatCompactNumber(stats.Context) : "-", resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Memory", notesCount.ToString(System.Globalization.CultureInfo.InvariantCulture), resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Avg", stats.AverageLatencyMs > 0 ? formatDuration(stats.AverageLatencyMs) : "-", resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Last", stats.LastLatencyMs > 0 ? formatDuration(stats.LastLatencyMs) : "-", resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Speed", FormatTokensPerSecond(stats.AverageTokensPerSecond), resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("TTFT", stats.AverageTimeToFirstTokenMs > 0 ? formatDuration(stats.AverageTimeToFirstTokenMs) : "-", resourceBrush("TextBrush")));
        metrics.Children.Add(CreateDetailMetric("Fails", stats.Failures.ToString(System.Globalization.CultureInfo.InvariantCulture), stats.Failures > 0 ? resourceBrush("DangerTextBrush") : resourceBrush("MutedTextBrush")));
        metrics.Children.Add(CreateDetailMetric("Web", stats.InternetRequests.ToString(System.Globalization.CultureInfo.InvariantCulture), stats.InternetRequests > 0 ? resourceBrush("AssistBorderBrush") : resourceBrush("MutedTextBrush")));
        panel.Children.Add(metrics);

        panel.Children.Add(new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new MetricSparklineControl
            {
                Height = 44,
                Mode = "bars",
                Values = stats.Activity.Any() ? stats.Activity : [0d],
                MaxValue = Math.Max(1, stats.Activity.DefaultIfEmpty(1).Max()),
                AccentBrush = accent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }
        });

        panel.Children.Add(CreateDetailSection(
            "Persona",
            compactPreview(agent?.Persona, 260, "No persona is assigned.")));

        panel.Children.Add(new TextBlock
        {
            Text = "Recent Turns",
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });

        if (recentTurns.Length == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No transcript turns yet.",
                Foreground = resourceBrush("MutedTextBrush"),
                FontStyle = FontStyles.Italic,
                FontSize = 11
            });
        }
        else
        {
            foreach (var message in recentTurns)
            {
                panel.Children.Add(CreateRecentTurn(message, accent));
            }
        }

        return panel;
    }

    private Border CreateDetailMetric(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.09),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 7, 7),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = value,
                        Foreground = accent,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateVoiceChip(string text, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.11),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, -2, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private Border CreateVoiceAdherenceDetail(AgentPerformanceStats stats, IReadOnlyList<TranscriptMessage> recentTurns)
    {
        var accent = voiceAdherenceStateAccent(stats.VoiceAdherenceState);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"Voice cues: {displayStatusValue(RoleStyleCatalog.VoiceAdherenceDisplayState(stats.VoiceAdherenceState))} {stats.VoiceAdherenceScore}",
            Foreground = accent,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{stats.VoiceAdherenceSamples} scored turn(s). Strong cues are 74+, partial cues are 46-73, low cues are below 46.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7)
        });

        foreach (var diagnostic in recentTurns
            .Select(message => (Message: message, Diagnostic: voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text)))
            .Where(item => !item.Diagnostic.State.Equals("none", StringComparison.OrdinalIgnoreCase))
            .Take(3))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Turn {diagnostic.Message.Turn}: {diagnostic.Diagnostic.Label} - {RoleStyleCatalog.VoiceAdherenceDisplayState(diagnostic.Diagnostic.State)} {diagnostic.Diagnostic.Score}",
                Foreground = voiceAdherenceDiagnosticAccent(diagnostic.Diagnostic),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = RoleStyleCatalog.VoiceAdherenceTooltip(diagnostic.Diagnostic)
            });
        }

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private Border CreateDetailSection(string title, string body)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private Border CreateRecentTurn(TranscriptMessage message, Brush accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"Turn {message.Turn} | {formatCompactNumber(message.CompletionTokens)} tok | {formatDuration(message.LatencyMs)}{FormatRecentTurnTelemetry(message)}",
            Foreground = accent,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = compactPreview(message.Text, 170, "(empty response)"),
            Foreground = resourceBrush("TextBrush"),
            FontSize = 11,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.28),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
    }

    private Border CreateStatusPill(string text, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.14),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 42,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
    }

    private static MetricSparklineControl CreateActivitySparkline(AgentPerformanceStats stats, Brush accent)
    {
        return new MetricSparklineControl
        {
            Width = 62,
            Height = 16,
            Mode = "bars",
            Values = stats.Activity,
            MaxValue = Math.Max(1, stats.Activity.DefaultIfEmpty(1).Max()),
            AccentBrush = accent,
            HorizontalAlignment = HorizontalAlignment.Right
        };
    }

    private StackPanel CreateIdleActivityMarker(Brush accent)
    {
        var marker = new StackPanel
        {
            Width = 62,
            Height = 16,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.48
        };

        for (var i = 0; i < 5; i++)
        {
            marker.Children.Add(new Border
            {
                Width = 4,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = blendBrush(resourceBrush("CardBrush"), accent, 0.28),
                Margin = new Thickness(i == 0 ? 24 : 4, 6, 0, 0)
            });
        }

        return marker;
    }

    internal static double AverageTokensPerSecond(IEnumerable<TranscriptMessage> messages)
    {
        var values = messages
            .Select(message => message.TokensPerSecond)
            .Where(value => value > 0)
            .ToArray();
        return values.Length == 0
            ? 0
            : Math.Round(values.Average(), 1);
    }

    internal static int AverageTimeToFirstTokenMs(IEnumerable<TranscriptMessage> messages)
    {
        var values = messages
            .Select(message => message.TimeToFirstTokenMs)
            .Where(value => value > 0)
            .ToArray();
        return values.Length == 0
            ? 0
            : (int)Math.Round(values.Average());
    }

    private static string FormatTokensPerSecond(double tokensPerSecond)
    {
        return tokensPerSecond > 0
            ? tokensPerSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : "-";
    }

    private string FormatRecentTurnTelemetry(TranscriptMessage message)
    {
        var parts = new List<string>();
        if (message.TokensPerSecond > 0)
        {
            parts.Add($"{FormatTokensPerSecond(message.TokensPerSecond)} tok/s");
        }

        if (message.TimeToFirstTokenMs > 0)
        {
            parts.Add($"ttft {formatDuration(message.TimeToFirstTokenMs)}");
        }

        if (message.ModelLoadTimeMs > 0)
        {
            parts.Add($"load {formatDuration(message.ModelLoadTimeMs)}");
        }

        return parts.Count == 0
            ? ""
            : $" | {string.Join(" | ", parts)}";
    }

    private static AgentState? FindAgent(ArenaViewSnapshot snapshot, string agentId)
    {
        return snapshot.Agents.FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
            ?? (agentId.Equals("narrator", StringComparison.OrdinalIgnoreCase)
                ? new AgentState(
                    "narrator",
                    "Narrator",
                    snapshot.NarratorStatus,
                    snapshot.NarratorPersona,
                    snapshot.NarratorVoiceStyle,
                    "",
                    snapshot.NarratorAccentColor,
                    snapshot.NarratorModel,
                    true,
                    snapshot.NarratorLocked,
                    [])
                : null);
    }

    internal sealed record AgentPerformanceStats(
        string AgentId,
        string Name,
        string Status,
        string Model,
        int Calls,
        int Tokens,
        int Context,
        int AverageLatencyMs,
        int LastLatencyMs,
        double AverageTokensPerSecond,
        int AverageTimeToFirstTokenMs,
        int Failures,
        int EmptyResponses,
        int InternetRequests,
        int VoiceAdherenceScore,
        string VoiceAdherenceState,
        int VoiceAdherenceSamples,
        IReadOnlyList<double> Activity,
        AgentInternetSourceSummary? InternetSources,
        int LastMessageOrdinal);

    private sealed class AgentPerformanceAggregate
    {
        public List<TranscriptMessage> Messages { get; } = [];
        public Queue<double> Activity { get; } = new();
        public List<CoreVoiceAdherenceDiagnostic> VoiceDiagnosticsList { get; } = [];
        public CoreVoiceAdherenceDiagnostic[] VoiceDiagnostics { get; private set; } = [];
        public int Tokens { get; set; }
        public int Context { get; set; }
        public long LatencyTotal { get; set; }
        public int LatencyCount { get; set; }
        public int LastLatency { get; set; }
        public double TokensPerSecondTotal { get; set; }
        public int TokensPerSecondCount { get; set; }
        public long TimeToFirstTokenTotal { get; set; }
        public int TimeToFirstTokenCount { get; set; }
        public int Failures { get; set; }
        public int EmptyResponses { get; set; }
        public int InternetRequests { get; set; }
        public int LastMessageOrdinal { get; set; } = -1;
        public AgentInternetSourceSummary? LatestInternetSources { get; set; }

        public void FreezeDiagnostics()
        {
            VoiceDiagnostics = VoiceDiagnosticsList.ToArray();
        }
    }
}
