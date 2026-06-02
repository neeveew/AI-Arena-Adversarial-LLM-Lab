using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using CoreDiscourseTurn = AIArena.Core.Models.DiscourseTurn;
using CoreFrictionDiagnostics = AIArena.Core.Models.FrictionDiagnostics;
using CoreMetricDiagnostic = AIArena.Core.Models.MetricDiagnostic;

namespace AIArena.Wpf;

internal sealed record DiagnosticHistoryPoint(
    int Friction,
    int Consensus,
    int RoleDrift,
    int UnsupportedClaims,
    int EvidencePressure,
    int NarrativeHeat);

internal sealed class DiagnosticsWorkflowCoordinator
{
    private const int HistoryLimit = 36;
    private const int WindowSize = 8;

    private readonly DiscourseDiagnosticsService discourseDiagnostics;
    private readonly Border frictionChip;
    private readonly TextBlock frictionValueText;
    private readonly TextBlock frictionTrendText;
    private readonly Border consensusChip;
    private readonly TextBlock consensusValueText;
    private readonly TextBlock consensusTrendText;
    private readonly MetricSparklineControl consensusSparkline;
    private readonly Border roleDriftChip;
    private readonly TextBlock roleDriftValueText;
    private readonly TextBlock roleDriftTrendText;
    private readonly MetricSparklineControl roleDriftSparkline;
    private readonly Border unsupportedClaimsChip;
    private readonly TextBlock unsupportedClaimsValueText;
    private readonly TextBlock unsupportedClaimsTrendText;
    private readonly MetricSparklineControl unsupportedClaimsSparkline;
    private readonly Border evidencePressureChip;
    private readonly TextBlock evidencePressureValueText;
    private readonly TextBlock evidencePressureTrendText;
    private readonly MetricSparklineControl evidencePressureSparkline;
    private readonly Border narrativeHeatChip;
    private readonly TextBlock narrativeHeatValueText;
    private readonly TextBlock narrativeHeatTrendText;
    private readonly MetricSparklineControl narrativeHeatSparkline;
    private readonly Popup detailPopup;
    private readonly TextBlock detailTitleText;
    private readonly TextBlock detailSubtitleText;
    private readonly Panel detailContent;
    private readonly Func<IReadOnlyDictionary<string, string>> agentPersonas;
    private readonly Func<IReadOnlyList<TranscriptMessage>> lastRenderedMessages;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<TranscriptMessage, bool, bool> isSystemEvent;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;

    private CoreFrictionDiagnostics? lastDiagnostics;
    private DiagnosticSeriesSet lastDiagnosticSeries = new(Array.Empty<DiagnosticHistoryPoint>(), 0);

    public DiagnosticsWorkflowCoordinator(
        DiscourseDiagnosticsService discourseDiagnostics,
        Border frictionChip,
        TextBlock frictionValueText,
        TextBlock frictionTrendText,
        Border consensusChip,
        TextBlock consensusValueText,
        TextBlock consensusTrendText,
        MetricSparklineControl consensusSparkline,
        Border roleDriftChip,
        TextBlock roleDriftValueText,
        TextBlock roleDriftTrendText,
        MetricSparklineControl roleDriftSparkline,
        Border unsupportedClaimsChip,
        TextBlock unsupportedClaimsValueText,
        TextBlock unsupportedClaimsTrendText,
        MetricSparklineControl unsupportedClaimsSparkline,
        Border evidencePressureChip,
        TextBlock evidencePressureValueText,
        TextBlock evidencePressureTrendText,
        MetricSparklineControl evidencePressureSparkline,
        Border narrativeHeatChip,
        TextBlock narrativeHeatValueText,
        TextBlock narrativeHeatTrendText,
        MetricSparklineControl narrativeHeatSparkline,
        Popup detailPopup,
        TextBlock detailTitleText,
        TextBlock detailSubtitleText,
        Panel detailContent,
        Func<IReadOnlyDictionary<string, string>> agentPersonas,
        Func<IReadOnlyList<TranscriptMessage>> lastRenderedMessages,
        Func<string, Brush> resourceBrush,
        Func<string, string> displayStatusValue,
        Func<TranscriptMessage, bool, bool> isSystemEvent,
        Func<Brush, Brush, double, Brush> blendBrush)
    {
        this.discourseDiagnostics = discourseDiagnostics;
        this.frictionChip = frictionChip;
        this.frictionValueText = frictionValueText;
        this.frictionTrendText = frictionTrendText;
        this.consensusChip = consensusChip;
        this.consensusValueText = consensusValueText;
        this.consensusTrendText = consensusTrendText;
        this.consensusSparkline = consensusSparkline;
        this.roleDriftChip = roleDriftChip;
        this.roleDriftValueText = roleDriftValueText;
        this.roleDriftTrendText = roleDriftTrendText;
        this.roleDriftSparkline = roleDriftSparkline;
        this.unsupportedClaimsChip = unsupportedClaimsChip;
        this.unsupportedClaimsValueText = unsupportedClaimsValueText;
        this.unsupportedClaimsTrendText = unsupportedClaimsTrendText;
        this.unsupportedClaimsSparkline = unsupportedClaimsSparkline;
        this.evidencePressureChip = evidencePressureChip;
        this.evidencePressureValueText = evidencePressureValueText;
        this.evidencePressureTrendText = evidencePressureTrendText;
        this.evidencePressureSparkline = evidencePressureSparkline;
        this.narrativeHeatChip = narrativeHeatChip;
        this.narrativeHeatValueText = narrativeHeatValueText;
        this.narrativeHeatTrendText = narrativeHeatTrendText;
        this.narrativeHeatSparkline = narrativeHeatSparkline;
        this.detailPopup = detailPopup;
        this.detailTitleText = detailTitleText;
        this.detailSubtitleText = detailSubtitleText;
        this.detailContent = detailContent;
        this.agentPersonas = agentPersonas;
        this.lastRenderedMessages = lastRenderedMessages;
        this.resourceBrush = resourceBrush;
        this.displayStatusValue = displayStatusValue;
        this.isSystemEvent = isSystemEvent;
        this.blendBrush = blendBrush;
    }

    public void InitializeTiles()
    {
        var chips = new (Border Chip, string Key)[]
        {
            (frictionChip, "friction"),
            (consensusChip, "consensus"),
            (roleDriftChip, "roleDrift"),
            (unsupportedClaimsChip, "unsupportedClaims"),
            (evidencePressureChip, "evidencePressure"),
            (narrativeHeatChip, "narrativeHeat")
        };

        foreach (var (chip, key) in chips)
        {
            chip.Tag = key;
            chip.Cursor = Cursors.Hand;
            chip.MouseLeftButtonUp += DiagnosticChip_MouseLeftButtonUp;
        }
    }

    public void Update(IReadOnlyList<TranscriptMessage> messages)
    {
        var diagnostics = discourseDiagnostics.Analyze(messages.Select(ToDiscourseTurn), agentPersonas());
        var series = BuildSeries(messages);
        lastDiagnostics = diagnostics;
        lastDiagnosticSeries = series;
        SetTile(
            frictionChip,
            frictionValueText,
            frictionTrendText,
            null,
            diagnostics.StateLabel,
            FrictionScore(diagnostics.StateLabel),
            series,
            diagnostics.Details["friction"],
            AccentForState(diagnostics.StateLabel));
        SetTile(
            consensusChip,
            consensusValueText,
            consensusTrendText,
            consensusSparkline,
            $"{diagnostics.ConsensusPercent}%",
            diagnostics.ConsensusPercent,
            series,
            diagnostics.Details["consensus"],
            AccentForRisk(diagnostics.ConsensusLabel));
        SetTile(
            roleDriftChip,
            roleDriftValueText,
            roleDriftTrendText,
            roleDriftSparkline,
            $"{diagnostics.RoleDriftPercent}%",
            diagnostics.RoleDriftPercent,
            series,
            diagnostics.Details["roleDrift"],
            AccentForRisk(diagnostics.RoleDriftLabel));
        SetTile(
            unsupportedClaimsChip,
            unsupportedClaimsValueText,
            unsupportedClaimsTrendText,
            unsupportedClaimsSparkline,
            diagnostics.UnsupportedClaimCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            diagnostics.UnsupportedClaimCount,
            series,
            diagnostics.Details["unsupportedClaims"],
            AccentForRisk(diagnostics.UnsupportedClaimSeverity),
            maxOverride: Math.Max(4, series.UnsupportedClaimsMax));
        SetTile(
            evidencePressureChip,
            evidencePressureValueText,
            evidencePressureTrendText,
            evidencePressureSparkline,
            diagnostics.EvidencePressureLabel,
            diagnostics.EvidencePressureScore,
            series,
            diagnostics.Details["evidencePressure"],
            AccentForEvidence(diagnostics.EvidencePressureLabel));
        SetTile(
            narrativeHeatChip,
            narrativeHeatValueText,
            narrativeHeatTrendText,
            narrativeHeatSparkline,
            diagnostics.NarrativeHeatLabel,
            diagnostics.NarrativeHeatScore,
            series,
            diagnostics.Details["narrativeHeat"],
            AccentForNarrative(diagnostics.NarrativeHeatLabel));
    }

    public void CloseDetail()
    {
        detailPopup.IsOpen = false;
    }

    public DiagnosticHistoryPoint PointForWindow(IReadOnlyList<TranscriptMessage> orderedMessages, int endExclusive)
    {
        var end = Math.Clamp(endExclusive, 1, orderedMessages.Count);
        var start = Math.Max(0, end - WindowSize);
        var window = orderedMessages
            .Skip(start)
            .Take(end - start)
            .Select(ToDiscourseTurn);
        var diagnostics = discourseDiagnostics.Analyze(window, agentPersonas());
        return new DiagnosticHistoryPoint(
            FrictionScore(diagnostics.StateLabel),
            diagnostics.ConsensusPercent,
            diagnostics.RoleDriftPercent,
            diagnostics.UnsupportedClaimCount,
            diagnostics.EvidencePressureScore,
            diagnostics.NarrativeHeatScore);
    }

    public Brush AccentForState(string label)
    {
        return label switch
        {
            "Healthy" => resourceBrush("PrimaryBorderBrush"),
            "Productive Conflict" => resourceBrush("AlphaAccentBrush"),
            "Too Cold" => resourceBrush("BetaAccentBrush"),
            "Harmony Risk" or "Theatre Risk" or "Evidence-Starved" or "Role Drift" or "Unsupported Claims Spike" => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    public Brush AccentForRisk(string label)
    {
        return label switch
        {
            "Low" or "Healthy" => resourceBrush("PrimaryBorderBrush"),
            "Medium" or "Moderate" or "High" => resourceBrush("BetaAccentBrush"),
            "Collapse Risk" => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    public Brush AccentForEvidence(string label)
    {
        return label switch
        {
            "Strong" => resourceBrush("PrimaryBorderBrush"),
            "Medium" => resourceBrush("BetaAccentBrush"),
            "Weak" => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    public Brush AccentForNarrative(string label)
    {
        return label switch
        {
            "Low" => resourceBrush("PrimaryBorderBrush"),
            "Medium" or "Rising" => resourceBrush("AssistBorderBrush"),
            "High" => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    public static CoreDiscourseTurn ToDiscourseTurn(TranscriptMessage message)
    {
        return new CoreDiscourseTurn(
            message.Turn,
            message.SpeakerId,
            message.Speaker,
            message.Kind,
            message.Text,
            message.InternetSources,
            message.CreatedAt);
    }

    private void DiagnosticChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement chip || chip.Tag is not string key)
        {
            return;
        }

        ShowDetail(key, chip);
        e.Handled = true;
    }

    private void ShowDetail(string key, FrameworkElement target)
    {
        var diagnostics = lastDiagnostics;
        var messages = lastRenderedMessages();
        if (diagnostics is null)
        {
            diagnostics = discourseDiagnostics.Analyze(messages.Select(ToDiscourseTurn), agentPersonas());
            lastDiagnostics = diagnostics;
        }

        var metric = key switch
        {
            "consensus" => diagnostics.Details["consensus"],
            "roleDrift" => diagnostics.Details["roleDrift"],
            "unsupportedClaims" => diagnostics.Details["unsupportedClaims"],
            "evidencePressure" => diagnostics.Details["evidencePressure"],
            "narrativeHeat" => diagnostics.Details["narrativeHeat"],
            _ => diagnostics.Details["friction"]
        };
        var spec = ExplanationSpecFor(key);
        var accent = AccentForKey(key, diagnostics);
        detailTitleText.Text = spec.Title;
        detailTitleText.Foreground = accent;
        detailSubtitleText.Text = $"{CurrentValue(key, diagnostics)} - {WindowSummary(messages)}";
        detailContent.Children.Clear();
        detailContent.Children.Add(CreateDetailSection("What it measures", spec.Meaning, accent));
        detailContent.Children.Add(CreateDetailSection(
            "Current reasons",
            DiagnosticLines(metric.Details, "No diagnostic reasons are available for this metric yet."),
            accent));
        detailContent.Children.Add(CreateDetailSection(
            "Movement",
            MovementLines(key),
            accent));
        detailContent.Children.Add(CreateDetailSection(
            "Recent evidence",
            EvidenceSnippets(messages, key, spec.EvidenceTerms),
            accent));
        detailContent.Children.Add(CreateDetailSection("Operator nudge", spec.OperatorNudge, accent));
        detailPopup.PlacementTarget = target;
        detailPopup.IsOpen = true;
    }

    private Border CreateDetailSection(string title, string body, Brush accent)
    {
        return CreateDetailSection(title, [body], accent);
    }

    private Border CreateDetailSection(string title, IReadOnlyList<string> lines, Brush accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11,
                LineHeight = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }

    private static IReadOnlyList<string> DiagnosticLines(IReadOnlyList<string> lines, string fallback)
    {
        return lines.Count == 0
            ? [fallback]
            : lines.Take(6).Select(line => $"- {line}").ToArray();
    }

    private IReadOnlyList<string> MovementLines(string key)
    {
        var points = lastDiagnosticSeries.Points;
        if (points.Count < 2)
        {
            return ["- Not enough diagnostic history yet. Run another turn to build movement context."];
        }

        var current = PointValue(points[^1], key);
        var previous = PointValue(points[^2], key);
        var delta = current - previous;
        var movement = delta switch
        {
            > 0 => $"+{delta}",
            < 0 => $"-{Math.Abs(delta)}",
            _ => "0"
        };
        return
        [
            $"- Latest change: {movement} from the previous sparkline point.",
            "- Sparkline history uses rolling transcript windows, so it explains direction rather than factual truth."
        ];
    }

    private static int PointValue(DiagnosticHistoryPoint point, string key)
    {
        return key switch
        {
            "consensus" => point.Consensus,
            "roleDrift" => point.RoleDrift,
            "unsupportedClaims" => point.UnsupportedClaims,
            "evidencePressure" => point.EvidencePressure,
            "narrativeHeat" => point.NarrativeHeat,
            _ => point.Friction
        };
    }

    private IReadOnlyList<string> EvidenceSnippets(
        IReadOnlyList<TranscriptMessage> messages,
        string key,
        IReadOnlyList<string> terms)
    {
        var snippets = messages
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Where(message => !IsSystemMessage(message) && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => EvidenceSnippet(message, terms))
            .OfType<string>()
            .Where(snippet => !string.IsNullOrWhiteSpace(snippet))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        if (snippets.Length > 0)
        {
            return snippets;
        }

        if (key.Equals("evidencePressure", StringComparison.OrdinalIgnoreCase))
        {
            return ["- No visible source, URL, quote, or transcript-reference signal was found in the recent window."];
        }

        return ["- No obvious matching transcript snippet was found in the recent window."];
    }

    private string? EvidenceSnippet(TranscriptMessage message, IReadOnlyList<string> terms)
    {
        var text = message.Text;
        if (terms.Count > 0 && !terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var speaker = string.IsNullOrWhiteSpace(message.Speaker) ? displayStatusValue(message.SpeakerId) : message.Speaker;
        return $"- T{message.Turn} {speaker}: {ShellUiHelpers.CompactPreview(text, 155, "(empty message)")}";
    }

    private bool IsSystemMessage(TranscriptMessage message)
    {
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase);
        return isSystemEvent(message, isInternet)
            || message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase);
    }

    private string WindowSummary(IReadOnlyList<TranscriptMessage> messages)
    {
        var scoredTurns = messages.Count(message => !IsSystemMessage(message));
        if (scoredTurns == 0)
        {
            return "no scored turns yet";
        }

        return $"rolling window: last {Math.Min(WindowSize, scoredTurns)} scored turn(s)";
    }

    private static DiagnosticExplanationSpec ExplanationSpecFor(string key)
    {
        return key switch
        {
            "consensus" => new DiagnosticExplanationSpec(
                "Consensus",
                "Estimates whether recent agent language is converging, agreeing, or challenging. Very high consensus can mean useful agreement, but it can also signal premature collapse.",
                "Ask one agent to name the strongest unresolved objection before the group settles.",
                ["i agree", "correct", "exactly", "building on", "however", "but", "counterargument", "assumption", "failure mode"]),
            "roleDrift" => new DiagnosticExplanationSpec(
                "Role Drift",
                "Checks whether agents are still behaving like their assigned persona and role, rather than sliding into closure, administration, or another participant voice.",
                "Restate the role contract for the drifting agent, then ask for one response in that role only.",
                ["final synthesis", "framework stands", "i agree", "as alpha", "as beta", "as gamma", "as delta", "boundary", "constraint", "hypothesis"]),
            "unsupportedClaims" => new DiagnosticExplanationSpec(
                "Unsupported Claims",
                "Counts authoritative claims, invented metrics, and certainty language that appear without visible evidence signals in the rolling window.",
                "Ask for evidence, scope, and what would falsify the claim before letting the match advance.",
                ["proves", "validated", "empirical", "universal", "self-evident", "only explanation", "truth", "%", "ms", "users"]),
            "evidencePressure" => new DiagnosticExplanationSpec(
                "Evidence Pressure",
                "Measures visible grounding pressure: sources, URLs, transcript references, quotes with evidence markers, or explicit uncertainty language.",
                "Ask the agents to cite the transcript, label assumptions, or request external evidence before making a stronger claim.",
                ["according to", "source", "operator provided", "turn", "transcript", "speculative", "inference", "hypothesis", "http", "www", "\""]),
            "narrativeHeat" => new DiagnosticExplanationSpec(
                "Narrative Heat",
                "Tracks dramatic or mythic language that can make a conversation feel coherent while hiding weak grounding.",
                "Cool the frame: ask for a concrete mechanism, a boundary condition, and one plain-language restatement.",
                ["ontology", "ontological", "emergence", "substrate", "universal law", "final synthesis", "truth", "coherence", "reality engine"]),
            _ => new DiagnosticExplanationSpec(
                "Friction",
                "Summarizes the overall discourse state by combining consensus, role drift, unsupported claims, evidence pressure, and narrative heat.",
                "If the match is too cold, invite disagreement. If it is too hot, ask for evidence and boundaries.",
                ["however", "but", "unsupported", "evidence", "assumption", "agree", "truth", "final synthesis", "constraint"])
        };
    }

    private static string CurrentValue(string key, CoreFrictionDiagnostics diagnostics)
    {
        return key switch
        {
            "consensus" => $"{diagnostics.ConsensusPercent}% {diagnostics.ConsensusLabel}",
            "roleDrift" => $"{diagnostics.RoleDriftPercent}% {diagnostics.RoleDriftLabel}",
            "unsupportedClaims" => $"{diagnostics.UnsupportedClaimCount} {diagnostics.UnsupportedClaimSeverity}",
            "evidencePressure" => $"{diagnostics.EvidencePressureLabel} {diagnostics.EvidencePressureScore}",
            "narrativeHeat" => $"{diagnostics.NarrativeHeatLabel} {diagnostics.NarrativeHeatScore}",
            _ => $"{diagnostics.StateLabel} {FrictionScore(diagnostics.StateLabel)}"
        };
    }

    private Brush AccentForKey(string key, CoreFrictionDiagnostics diagnostics)
    {
        return key switch
        {
            "consensus" => AccentForRisk(diagnostics.ConsensusLabel),
            "roleDrift" => AccentForRisk(diagnostics.RoleDriftLabel),
            "unsupportedClaims" => AccentForRisk(diagnostics.UnsupportedClaimSeverity),
            "evidencePressure" => AccentForEvidence(diagnostics.EvidencePressureLabel),
            "narrativeHeat" => AccentForNarrative(diagnostics.NarrativeHeatLabel),
            _ => AccentForState(diagnostics.StateLabel)
        };
    }

    private void SetTile(
        Border chip,
        TextBlock valueText,
        TextBlock trendText,
        MetricSparklineControl? sparkline,
        string display,
        int score,
        DiagnosticSeriesSet series,
        CoreMetricDiagnostic metric,
        Brush accent,
        int? maxOverride = null)
    {
        valueText.Text = display;
        valueText.Foreground = accent;
        trendText.Text = FormatTrend(score, PreviousScore(sparkline, series));
        trendText.Foreground = blendBrush(resourceBrush("MutedTextBrush"), accent, 0.55);
        if (sparkline is not null && series.Points.Count > 0)
        {
            sparkline.AccentBrush = accent;
            sparkline.Values = Series(sparkline, series);
            sparkline.MaxValue = Math.Max(1, maxOverride ?? 100);
        }

        chip.BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.62);
        chip.Background = blendBrush(resourceBrush("InputBrush"), accent, 0.1);
        chip.ToolTip = $"{ToolTip(metric)}{Environment.NewLine}Click for a compact explanation.";
        ToolTipService.SetShowDuration(chip, 60000);
    }

    private DiagnosticSeriesSet BuildSeries(IReadOnlyList<TranscriptMessage> messages)
    {
        var ordered = messages
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new DiagnosticSeriesSet([], 0);
        }

        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)HistoryLimit));
        var points = new List<DiagnosticHistoryPoint>();
        for (var end = 1; end <= ordered.Length; end += stride)
        {
            points.Add(PointForWindow(ordered, end));
        }

        if (points.Count == 0 || points[^1] != PointForWindow(ordered, ordered.Length))
        {
            points.Add(PointForWindow(ordered, ordered.Length));
        }

        if (points.Count > HistoryLimit)
        {
            points = points.Skip(points.Count - HistoryLimit).ToList();
        }

        return new DiagnosticSeriesSet(
            points,
            points.Select(point => point.UnsupportedClaims).DefaultIfEmpty(0).Max());
    }

    private int? PreviousScore(MetricSparklineControl? sparkline, DiagnosticSeriesSet series)
    {
        if (series.Points.Count < 2)
        {
            return null;
        }

        var previous = series.Points[^2];
        if (sparkline == consensusSparkline)
        {
            return previous.Consensus;
        }

        if (sparkline == roleDriftSparkline)
        {
            return previous.RoleDrift;
        }

        if (sparkline == unsupportedClaimsSparkline)
        {
            return previous.UnsupportedClaims;
        }

        if (sparkline == evidencePressureSparkline)
        {
            return previous.EvidencePressure;
        }

        if (sparkline == narrativeHeatSparkline)
        {
            return previous.NarrativeHeat;
        }

        return previous.Friction;
    }

    private IReadOnlyList<double> Series(MetricSparklineControl sparkline, DiagnosticSeriesSet series)
    {
        if (sparkline == consensusSparkline)
        {
            return series.Points.Select(point => (double)point.Consensus).ToArray();
        }

        if (sparkline == roleDriftSparkline)
        {
            return series.Points.Select(point => (double)point.RoleDrift).ToArray();
        }

        if (sparkline == unsupportedClaimsSparkline)
        {
            return series.Points.Select(point => (double)point.UnsupportedClaims).ToArray();
        }

        if (sparkline == evidencePressureSparkline)
        {
            return series.Points.Select(point => (double)point.EvidencePressure).ToArray();
        }

        if (sparkline == narrativeHeatSparkline)
        {
            return series.Points.Select(point => (double)point.NarrativeHeat).ToArray();
        }

        return series.Points.Select(point => (double)point.Friction).ToArray();
    }

    private static string FormatTrend(int current, int? previous)
    {
        if (!previous.HasValue)
        {
            return "new";
        }

        var delta = current - previous.Value;
        if (delta == 0)
        {
            return "0";
        }

        return delta > 0
            ? $"+{delta}"
            : $"-{Math.Abs(delta)}";
    }

    private static int FrictionScore(string label)
    {
        return label.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
            ? 64
            : label.Equals("Static", StringComparison.OrdinalIgnoreCase)
                ? 28
                : label.Equals("Chaotic", StringComparison.OrdinalIgnoreCase)
                    ? 88
                    : 45;
    }

    private static string ToolTip(CoreMetricDiagnostic metric)
    {
        return metric.Details.Count == 0
            ? metric.Label
            : $"{metric.Label}: {string.Join("; ", metric.Details.Take(2))}";
    }


    private sealed record DiagnosticSeriesSet(
        IReadOnlyList<DiagnosticHistoryPoint> Points,
        int UnsupportedClaimsMax);

    private sealed record DiagnosticExplanationSpec(
        string Title,
        string Meaning,
        string OperatorNudge,
        IReadOnlyList<string> EvidenceTerms);
}
