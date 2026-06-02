using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class MatchQualityTimelineCoordinator
{
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<int?> selectedTurnFilter;
    private readonly Action<int> toggleTimelineTurnFilter;
    private readonly Action clearTimelineTurnFilter;
    private readonly Func<IReadOnlyList<TranscriptMessage>, int, DiagnosticHistoryPoint> diagnosticPointForWindow;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Func<string, Brush> accentForState;
    private readonly Func<string, Brush> accentForEvidence;
    private readonly Func<string, Brush> accentForRisk;

    private sealed record MatchQualityPoint(
        int Turn,
        string Speaker,
        int Quality,
        DiagnosticHistoryPoint Diagnostics,
        int Tokens);

    public MatchQualityTimelineCoordinator(
        Func<bool> compactTranscriptMode,
        Func<int?> selectedTurnFilter,
        Action<int> toggleTimelineTurnFilter,
        Action clearTimelineTurnFilter,
        Func<IReadOnlyList<TranscriptMessage>, int, DiagnosticHistoryPoint> diagnosticPointForWindow,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<int, string> formatCompactNumber,
        Func<string, Brush> accentForState,
        Func<string, Brush> accentForEvidence,
        Func<string, Brush> accentForRisk)
    {
        this.compactTranscriptMode = compactTranscriptMode;
        this.selectedTurnFilter = selectedTurnFilter;
        this.toggleTimelineTurnFilter = toggleTimelineTurnFilter;
        this.clearTimelineTurnFilter = clearTimelineTurnFilter;
        this.diagnosticPointForWindow = diagnosticPointForWindow;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.formatCompactNumber = formatCompactNumber;
        this.accentForState = accentForState;
        this.accentForEvidence = accentForEvidence;
        this.accentForRisk = accentForRisk;
    }

    public Border CreatePanel(IReadOnlyList<TranscriptMessage> messages)
    {
        var points = BuildTimeline(messages);
        var accent = QualityAccent(points.LastOrDefault()?.Quality ?? 0);
        var panel = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Match Quality Timeline",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = points.Count == 0
                ? "Quality appears after the first transcript turn."
                : $"Tracking {points.Count} quality checkpoint(s) across the current match transcript.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var current = selectedTurnFilter() is int turnFilter
            ? points.LastOrDefault(point => point.Turn == turnFilter) ?? points.LastOrDefault()
            : points.LastOrDefault();
        var currentIndex = current is null ? -1 : points.ToList().FindIndex(point => ReferenceEquals(point, current) || point.Turn == current.Turn);
        var previous = currentIndex > 0 ? points[currentIndex - 1] : points.Count >= 2 ? points[^2] : null;
        var scoreStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        scoreStack.Children.Add(new TextBlock
        {
            Text = current is null ? "-" : current.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = accent,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        scoreStack.Children.Add(new TextBlock
        {
            Text = current is null ? "No score yet" : $"Current {QualityLabel(current.Quality)} {FormatQualityTrend(current, previous)}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        if (selectedTurnFilter() is not null)
        {
            scoreStack.Children.Add(CreateClearButton());
        }

        Grid.SetColumn(scoreStack, 1);
        header.Children.Add(scoreStack);
        panel.Children.Add(header);

        var sparkline = new MetricSparklineControl
        {
            Height = compactTranscriptMode() ? 54 : 70,
            MinHeight = compactTranscriptMode() ? 54 : 70,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Values = points.Select(point => (double)point.Quality).ToArray(),
            AccentBrush = accent,
            MaxValue = 100,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(sparkline);

        if (points.Count > 0)
        {
            var diagnostics = current!.Diagnostics;
            var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            metrics.Children.Add(CreateMetric("Friction", diagnostics.Friction.ToString(System.Globalization.CultureInfo.InvariantCulture), accentForState(QualityFrictionLabel(diagnostics.Friction))));
            metrics.Children.Add(CreateMetric("Evidence", diagnostics.EvidencePressure.ToString(System.Globalization.CultureInfo.InvariantCulture), accentForEvidence(QualityEvidenceLabel(diagnostics.EvidencePressure))));
            metrics.Children.Add(CreateMetric("Consensus", $"{diagnostics.Consensus}%", accentForRisk(diagnostics.Consensus > 75 ? "High" : "Low")));
            metrics.Children.Add(CreateMetric("Drift", $"{diagnostics.RoleDrift}%", accentForRisk(diagnostics.RoleDrift > 35 ? "High" : "Low")));
            metrics.Children.Add(CreateMetric("Claims", diagnostics.UnsupportedClaims.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostics.UnsupportedClaims > 0 ? resourceBrush("BetaAccentBrush") : resourceBrush("PrimaryBorderBrush")));
            panel.Children.Add(metrics);
            panel.Children.Add(CreateBars(points));
        }

        return new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private WrapPanel CreateBars(IReadOnlyList<MatchQualityPoint> points)
    {
        var bars = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 0)
        };
        foreach (var point in points.TakeLast(compactTranscriptMode() ? 24 : 36))
        {
            var accent = QualityAccent(point.Quality);
            var selected = selectedTurnFilter() == point.Turn;
            var height = Math.Max(7, Math.Round(point.Quality / 100d * 28));
            var bar = new Border
            {
                Width = selected ? 12 : 8,
                Height = 32,
                Background = blendBrush(resourceBrush("InputBrush"), accent, selected ? 0.2 : 0.08),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, selected ? 0.85 : 0.25),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                ToolTip = $"Turn {point.Turn} {point.Speaker}{Environment.NewLine}Quality: {point.Quality} ({QualityLabel(point.Quality)}){Environment.NewLine}Tokens: {formatCompactNumber(point.Tokens)}{Environment.NewLine}{(selected ? "Click to clear this turn filter." : "Click to filter transcript to this turn.")}"
            };
            bar.MouseLeftButtonUp += (_, e) =>
            {
                toggleTimelineTurnFilter(point.Turn);
                e.Handled = true;
            };
            bar.Child = new Border
            {
                Height = height,
                Background = accent,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1)
            };
            bars.Children.Add(bar);
        }

        return bars;
    }

    private Button CreateClearButton()
    {
        var button = new Button
        {
            Content = "Clear turn",
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Foreground = resourceBrush("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MinHeight = 26,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Return timeline and transcript to the selected turn preset"
        };
        button.Click += (_, _) => clearTimelineTurnFilter();
        return button;
    }

    private Border CreateMetric(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private IReadOnlyList<MatchQualityPoint> BuildTimeline(IReadOnlyList<TranscriptMessage> messages)
    {
        var ordered = messages
            .Where(message => message.Kind is "message" or "internet" or "")
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var limit = compactTranscriptMode() ? 28 : 42;
        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)limit));
        var points = new List<MatchQualityPoint>();
        for (var end = 1; end <= ordered.Length; end += stride)
        {
            points.Add(QualityPointForWindow(ordered, end));
        }

        var final = QualityPointForWindow(ordered, ordered.Length);
        if (points.Count == 0 || points[^1].Turn != final.Turn || !points[^1].Speaker.Equals(final.Speaker, StringComparison.OrdinalIgnoreCase))
        {
            points.Add(final);
        }

        return points.Count > limit ? points.Skip(points.Count - limit).ToArray() : points;
    }

    private MatchQualityPoint QualityPointForWindow(IReadOnlyList<TranscriptMessage> orderedMessages, int endExclusive)
    {
        var end = Math.Clamp(endExclusive, 1, orderedMessages.Count);
        var message = orderedMessages[end - 1];
        var diagnostics = diagnosticPointForWindow(orderedMessages, end);
        var quality = QualityScore(diagnostics);
        return new MatchQualityPoint(
            message.Turn,
            message.Speaker,
            quality,
            diagnostics,
            Math.Max(message.TotalTokens, message.CompletionTokens));
    }

    private static int QualityScore(DiagnosticHistoryPoint point)
    {
        var consensusPenalty = Math.Abs(point.Consensus - 38) * 0.24;
        var rolePenalty = point.RoleDrift * 0.32;
        var claimPenalty = Math.Min(24, point.UnsupportedClaims * 6);
        var evidenceBoost = point.EvidencePressure * 0.26;
        var narrativePenalty = point.NarrativeHeat > 78 ? (point.NarrativeHeat - 78) * 0.28 : 0;
        var frictionPenalty = point.Friction switch
        {
            < 18 => (18 - point.Friction) * 0.45,
            > 70 => (point.Friction - 70) * 0.46,
            _ => 0
        };
        var score = 58 + evidenceBoost - consensusPenalty - rolePenalty - claimPenalty - narrativePenalty - frictionPenalty;
        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    private Brush QualityAccent(int quality)
    {
        return quality switch
        {
            >= 75 => resourceBrush("PrimaryBorderBrush"),
            >= 52 => resourceBrush("BetaAccentBrush"),
            _ => resourceBrush("DangerBorderBrush")
        };
    }

    private static string QualityLabel(int quality)
    {
        return quality switch
        {
            >= 75 => "Strong",
            >= 52 => "Watch",
            _ => "Fragile"
        };
    }

    private static string FormatQualityTrend(MatchQualityPoint current, MatchQualityPoint? previous)
    {
        if (previous is null)
        {
            return "new";
        }

        var delta = current.Quality - previous.Quality;
        return delta == 0 ? "0" : delta > 0 ? $"+{delta}" : $"-{Math.Abs(delta)}";
    }

    private static string QualityFrictionLabel(int friction)
    {
        return friction switch
        {
            < 20 => "Too Cold",
            <= 62 => "Productive Conflict",
            <= 72 => "Healthy",
            _ => "Theatre Risk"
        };
    }

    private static string QualityEvidenceLabel(int evidence)
    {
        return evidence switch
        {
            >= 70 => "Strong",
            >= 36 => "Medium",
            _ => "Weak"
        };
    }
}
