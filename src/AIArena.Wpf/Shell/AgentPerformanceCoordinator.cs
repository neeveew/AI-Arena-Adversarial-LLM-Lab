using System.Windows;
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

    private ArenaViewSnapshot? lastSnapshot;
    private string? activeDetailId;

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
        Func<Brush, Brush, double, Brush> blendBrush)
    {
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

    public void Populate(ArenaViewSnapshot snapshot)
    {
        lastSnapshot = snapshot;
        var openDetailId = detailPopup.IsOpen ? activeDetailId : null;
        FrameworkElement? refreshedDetailTarget = null;
        AgentPerformanceStats? refreshedDetailStats = null;

        agentPerformanceItems.Children.Clear();
        var participants = snapshot.Agents
            .Where(agent => agent.Active || snapshot.Messages.Any(message => message.SpeakerId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)))
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
            .Select(group => group.First())
            .ToArray();

        var stats = participants
            .Select(agent => CreateStats(snapshot, agent))
            .ToArray();

        var maxTokens = Math.Max(1, stats.Select(item => item.Tokens).DefaultIfEmpty(0).Max());

        foreach (var item in stats)
        {
            var row = CreateRow(item, maxTokens);
            agentPerformanceItems.Children.Add(row);
            if (!string.IsNullOrWhiteSpace(openDetailId)
                && item.AgentId.Equals(openDetailId, StringComparison.OrdinalIgnoreCase))
            {
                refreshedDetailTarget = row;
                refreshedDetailStats = item;
            }
        }

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

    public void CloseDetail()
    {
        activeDetailId = null;
        detailPopup.IsOpen = false;
        detailPopup.PlacementTarget = null;
        detailContent.Children.Clear();
    }

    private AgentPerformanceStats CreateStats(ArenaViewSnapshot snapshot, AgentState agent)
    {
        var messages = snapshot.Messages
            .Where(message => message.SpeakerId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)
                && !message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var internetRequests = snapshot.Messages.Count(message =>
            message.InternetRequester.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)
            || (message.SpeakerId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(message.InternetTool) || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase))));
        var failures = messages.Count(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase));
        var empty = messages.Count(message => string.IsNullOrWhiteSpace(message.Text) || message.Text.Contains("(empty model response)", StringComparison.OrdinalIgnoreCase));
        var latencies = messages.Where(message => message.LatencyMs > 0).Select(message => message.LatencyMs).ToArray();
        var tokens = messages.Sum(message => Math.Max(message.CompletionTokens, 0));
        var context = messages.Select(message => message.PromptTokens).DefaultIfEmpty(0).Max();
        var lastLatency = messages.LastOrDefault(message => message.LatencyMs > 0)?.LatencyMs ?? 0;
        var activity = messages
            .TakeLast(12)
            .Select(message => (double)Math.Max(1, Math.Max(message.CompletionTokens, message.TotalTokens)))
            .ToArray();
        var voiceDiagnostics = messages
            .Select(message => voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text))
            .Where(diagnostic => !diagnostic.State.Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var voiceScore = voiceDiagnostics.Length == 0
            ? 0
            : (int)Math.Round(voiceDiagnostics.Average(diagnostic => diagnostic.Score));

        return new AgentPerformanceStats(
            agent.Id,
            string.IsNullOrWhiteSpace(agent.Name) ? displayStatusValue(agent.Id) : agent.Name,
            displayInlineStatus(agent.Status),
            agent.Model,
            messages.Length,
            tokens,
            context,
            latencies.Length == 0 ? 0 : (int)latencies.Average(),
            lastLatency,
            failures,
            empty,
            internetRequests,
            voiceScore,
            RoleStyleCatalog.VoiceAdherenceState(voiceScore, voiceDiagnostics.Length),
            voiceDiagnostics.Length,
            activity);
    }

    private Border CreateRow(AgentPerformanceStats stats, int maxTokens)
    {
        var accent = accentForSpeaker(stats.AgentId);
        var grid = new Grid
        {
            Margin = new Thickness(0)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var displayTitle = formatParticipantTitle(stats.AgentId, stats.Name, true);
        var title = new TextBlock
        {
            Text = displayStatusValue(displayTitle),
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(title);

        var statusPill = CreateStatusPill(stats.Status, accent);
        Grid.SetColumn(statusPill, 1);
        grid.Children.Add(statusPill);

        var model = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(stats.Model) ? "model not assigned" : shortModelName(stats.Model),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 9,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 4)
        };
        Grid.SetRow(model, 1);
        Grid.SetColumnSpan(model, 2);
        grid.Children.Add(model);

        var metrics = new UniformGrid
        {
            Rows = 1,
            Columns = 4,
            Margin = new Thickness(0, 0, 0, 4)
        };
        metrics.Children.Add(CreateStackedMetric("Turns", stats.Calls.ToString(System.Globalization.CultureInfo.InvariantCulture), accent));
        metrics.Children.Add(CreateStackedMetric("Tokens", formatCompactNumber(stats.Tokens), resourceBrush("PrimaryBorderBrush")));
        metrics.Children.Add(CreateStackedMetric("Avg", stats.AverageLatencyMs > 0 ? formatDuration(stats.AverageLatencyMs) : "-", resourceBrush("AlphaAccentBrush")));
        metrics.Children.Add(CreateStackedMetric("Ctx", stats.Context > 0 ? formatCompactNumber(stats.Context) : "-", resourceBrush("GammaAccentBrush")));
        Grid.SetRow(metrics, 2);
        Grid.SetColumnSpan(metrics, 2);
        grid.Children.Add(metrics);

        var activityRow = new Grid();
        activityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        activityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hasActivity = stats.Activity.Any(value => value > 0.001);
        var tokenTrack = new Grid
        {
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        tokenTrack.Children.Add(new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.14),
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.9
        });
        tokenTrack.Children.Add(new Border
        {
            Background = accent,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = stats.Tokens <= 0 ? 0.28 : 0.82,
            Width = stats.Tokens <= 0
                ? 10
                : Math.Max(18, 106 * Math.Clamp(stats.Tokens / (double)maxTokens, 0.08, 1)),
            VerticalAlignment = VerticalAlignment.Center
        });
        activityRow.Children.Add(tokenTrack);

        FrameworkElement activityMarker = hasActivity
            ? CreateActivitySparkline(stats, accent)
            : CreateIdleActivityMarker(accent);
        Grid.SetColumn(activityMarker, 1);
        activityRow.Children.Add(activityMarker);

        Grid.SetRow(activityRow, 3);
        Grid.SetColumnSpan(activityRow, 2);
        grid.Children.Add(activityRow);

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

        var card = new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 5),
            ToolTip = alerts.Count == 0 ? $"{displayTitle}: no warnings" : $"{displayTitle}: {string.Join(", ", alerts)}",
            Cursor = Cursors.Hand,
            Child = grid
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            ShowDetail(stats, card);
            e.Handled = true;
        };

        return card;
    }

    private void ShowDetail(AgentPerformanceStats stats, FrameworkElement target)
    {
        if (lastSnapshot is null)
        {
            return;
        }

        RenderDetail(stats, lastSnapshot, target, resetPopup: true);
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
            Rows = 2,
            Margin = new Thickness(0, 0, 0, 10)
        };
        metrics.Children.Add(CreateDetailMetric("Turns", stats.Calls.ToString(System.Globalization.CultureInfo.InvariantCulture), accent));
        metrics.Children.Add(CreateDetailMetric("Tokens", formatCompactNumber(stats.Tokens), resourceBrush("PrimaryBorderBrush")));
        metrics.Children.Add(CreateDetailMetric("Context", stats.Context > 0 ? formatCompactNumber(stats.Context) : "-", resourceBrush("GammaAccentBrush")));
        metrics.Children.Add(CreateDetailMetric("Memory", notesCount.ToString(System.Globalization.CultureInfo.InvariantCulture), resourceBrush("NarratorAccentBrush")));
        metrics.Children.Add(CreateDetailMetric("Avg", stats.AverageLatencyMs > 0 ? formatDuration(stats.AverageLatencyMs) : "-", resourceBrush("AlphaAccentBrush")));
        metrics.Children.Add(CreateDetailMetric("Last", stats.LastLatencyMs > 0 ? formatDuration(stats.LastLatencyMs) : "-", resourceBrush("AlphaAccentBrush")));
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
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.3),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 5, 5),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 8
                    },
                    new TextBlock
                    {
                        Text = value,
                        Foreground = accent,
                        FontSize = 11,
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
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.09),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, -2, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 11,
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
            Text = $"Style Cues: {displayStatusValue(RoleStyleCatalog.VoiceAdherenceDisplayState(stats.VoiceAdherenceState))} {stats.VoiceAdherenceScore}",
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
            CornerRadius = new CornerRadius(5),
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
            CornerRadius = new CornerRadius(5),
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
            Text = $"Turn {message.Turn} - {formatCompactNumber(message.CompletionTokens)} Tok - {formatDuration(message.LatencyMs)}",
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
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
    }

    private Border CreateStatusPill(string text, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.12),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.45),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 1),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static MetricSparklineControl CreateActivitySparkline(AgentPerformanceStats stats, Brush accent)
    {
        return new MetricSparklineControl
        {
            Width = 74,
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
            Width = 74,
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
                Margin = new Thickness(i == 0 ? 34 : 4, 6, 0, 0)
            });
        }

        return marker;
    }

    private StackPanel CreateStackedMetric(string label, string value, Brush accent)
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 4, 0),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Foreground = resourceBrush("MutedTextBrush"),
                    FontSize = 8,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = value,
                    Foreground = accent,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
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

    private sealed record AgentPerformanceStats(
        string AgentId,
        string Name,
        string Status,
        string Model,
        int Calls,
        int Tokens,
        int Context,
        int AverageLatencyMs,
        int LastLatencyMs,
        int Failures,
        int EmptyResponses,
        int InternetRequests,
        int VoiceAdherenceScore,
        string VoiceAdherenceState,
        int VoiceAdherenceSamples,
        IReadOnlyList<double> Activity);
}
