using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Persistence;
using AIArena.Wpf.Controls;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

/// <summary>
/// Coordinates aggregate evaluation evidence and the separate secret-free
/// replay setup. Copy Evidence never includes the replay package, session ID,
/// transcript bodies, or raw provider responses.
/// </summary>
internal sealed class ArenaEvaluationCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly ArenaEvaluationService evaluationService;
    private readonly ArenaEvaluationHistoryStore historyStore;
    private readonly TextBlock statusText;
    private readonly TextBlock historyText;
    private readonly TextBlock baselineText;
    private readonly TextBlock candidateText;
    private readonly TextBlock comparisonSummaryText;
    private readonly Panel comparisonItems;
    private readonly Panel modelItems;
    private readonly TextBlock qaSummaryText;
    private readonly Panel qaItems;
    private readonly Button captureBaselineButton;
    private readonly Button compareCurrentButton;
    private readonly Button runQaButton;
    private readonly Button copyEvidenceButton;
    private readonly Button copyReplaySetupButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<string> setArenaRunStatus;

    private ArenaEvaluationRecord? lastCandidate;
    private ArenaEvaluationRecord? lastBaseline;
    private ArenaEvaluationComparison? lastComparison;
    private ArenaRuntimeQaReport? lastQa;
    private string renderedSessionId = "";
    private bool operationRunning;
    private long operationGeneration;

    public ArenaEvaluationCoordinator(
        SessionStore sessionStore,
        ArenaEvaluationService evaluationService,
        ArenaEvaluationHistoryStore historyStore,
        TextBlock statusText,
        TextBlock historyText,
        TextBlock baselineText,
        TextBlock candidateText,
        TextBlock comparisonSummaryText,
        Panel comparisonItems,
        Panel modelItems,
        TextBlock qaSummaryText,
        Panel qaItems,
        Button captureBaselineButton,
        Button compareCurrentButton,
        Button runQaButton,
        Button copyEvidenceButton,
        Button copyReplaySetupButton,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string, Brush> resourceBrush,
        Action<string> setArenaRunStatus)
    {
        this.sessionStore = sessionStore;
        this.evaluationService = evaluationService;
        this.historyStore = historyStore;
        this.statusText = statusText;
        this.historyText = historyText;
        this.baselineText = baselineText;
        this.candidateText = candidateText;
        this.comparisonSummaryText = comparisonSummaryText;
        this.comparisonItems = comparisonItems;
        this.modelItems = modelItems;
        this.qaSummaryText = qaSummaryText;
        this.qaItems = qaItems;
        this.captureBaselineButton = captureBaselineButton;
        this.compareCurrentButton = compareCurrentButton;
        this.runQaButton = runQaButton;
        this.copyEvidenceButton = copyEvidenceButton;
        this.copyReplaySetupButton = copyReplaySetupButton;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.resourceBrush = resourceBrush;
        this.setArenaRunStatus = setArenaRunStatus;

        RenderEmpty();
        RefreshAvailability();
    }

    public void RefreshAvailability()
    {
        var sessionId = activeSession()?.Id ?? "";
        var sessionChanged = !sessionId.Equals(renderedSessionId, StringComparison.OrdinalIgnoreCase);
        if (sessionChanged)
        {
            renderedSessionId = sessionId;
            lastCandidate = null;
            lastBaseline = null;
            lastComparison = null;
            lastQa = null;
            RenderEmpty();
        }

        if (!sessionChanged && historyText.Text.Length > 0)
        {
            UpdateButtons();
            return;
        }

        ArenaEvaluationHistorySnapshot history;
        try
        {
            history = historyStore.Load();
            historyText.Text = ArenaEvaluationPresentation.FormatHistory(history.Entries.Count);
            if (!string.IsNullOrWhiteSpace(historyStore.LastLoadWarning))
            {
                SetStatus(historyStore.LastLoadWarning, announceInShell: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            historyText.Text = "History unavailable";
            SetStatus(FriendlyError(ex), announceInShell: false);
        }

        UpdateButtons();
    }

    public Task CaptureBaselineAsync(CancellationToken cancellationToken = default)
    {
        return RunCaptureAsync(
            "Capturing a repeatable baseline…",
            (candidate, history) =>
            {
                var qa = evaluationService.EvaluateQa(candidate);
                var saved = historyStore.Save(candidate, qa, setBaseline: true);
                lastBaseline = candidate;
                lastCandidate = candidate;
                lastComparison = null;
                lastQa = qa;
                historyText.Text = ArenaEvaluationPresentation.FormatHistory(saved.Entries.Count);
                RenderCurrent();
                SetStatus("Baseline captured. Change the model or provider, replay the same setup, then compare current. Factory comparisons also require the same durable public-group context.");
            },
            cancellationToken);
    }

    public Task CompareCurrentAsync(CancellationToken cancellationToken = default)
    {
        return RunCaptureAsync(
            "Capturing current evidence and comparing it with the baseline…",
            (candidate, history) =>
            {
                var baseline = history.BaselineFor(candidate.ScenarioFingerprint)?.Evaluation;
                var comparison = evaluationService.Compare(baseline, candidate);
                var qa = evaluationService.EvaluateQa(candidate, baseline);
                var saved = historyStore.Save(candidate, qa);
                lastBaseline = baseline;
                lastCandidate = candidate;
                lastComparison = comparison;
                lastQa = qa;
                historyText.Text = ArenaEvaluationPresentation.FormatHistory(saved.Entries.Count);
                RenderCurrent();
                SetStatus(baseline is null
                    ? "No baseline matches this model-neutral setup. Capture a baseline before claiming a comparison."
                    : comparison.Summary);
            },
            cancellationToken);
    }

    public Task RunQaAsync(CancellationToken cancellationToken = default)
    {
        return RunCaptureAsync(
            "Inspecting the current run against local QA gates…",
            (candidate, history) =>
            {
                var baseline = history.BaselineFor(candidate.ScenarioFingerprint)?.Evaluation;
                var comparison = baseline is null ? null : evaluationService.Compare(baseline, candidate);
                var qa = evaluationService.EvaluateQa(candidate, baseline);
                var saved = historyStore.Save(candidate, qa);
                lastBaseline = baseline;
                lastCandidate = candidate;
                lastComparison = comparison;
                lastQa = qa;
                historyText.Text = ArenaEvaluationPresentation.FormatHistory(saved.Entries.Count);
                RenderCurrent();
                SetStatus(qa.Summary);
            },
            cancellationToken);
    }

    public void CopyEvidence()
    {
        if (lastCandidate is null)
        {
            SetStatus("Capture a baseline, comparison, or QA result before copying evidence.");
            return;
        }

        try
        {
            var json = evaluationService.ExportJson(lastCandidate, lastComparison, lastQa);
            SetStatus(ShellClipboard.TrySetText(json)
                ? "Copied aggregate-only evaluation evidence; replay setup and session identity were excluded."
                : "The clipboard is busy; evaluation evidence was not copied.");
        }
        catch (InvalidOperationException)
        {
            SetStatus("Evaluation evidence no longer matches its replay package and was not copied.");
        }
    }

    public void CopyReplaySetup()
    {
        if (lastCandidate is null)
        {
            SetStatus("Capture a baseline, comparison, or QA result before copying its replay setup.");
            return;
        }

        SetStatus(ShellClipboard.TrySetText(lastCandidate.PortableSetupJson)
            ? "Copied the exact secret-free Match Setup JSON for replay."
            : "The clipboard is busy; the replay setup was not copied.");
    }

    private async Task RunCaptureAsync(
        string pendingStatus,
        Action<ArenaEvaluationRecord, ArenaEvaluationHistorySnapshot> apply,
        CancellationToken cancellationToken)
    {
        if (operationRunning || isArenaBusy())
        {
            SetStatus("Evaluation capture is available when the arena is idle.");
            return;
        }

        var session = activeSession();
        if (session is null)
        {
            SetStatus("Open a session before capturing evaluation evidence.");
            return;
        }

        var generation = Interlocked.Increment(ref operationGeneration);
        operationRunning = true;
        UpdateButtons();
        SetStatus(pendingStatus, announceInShell: false);
        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is null)
            {
                SetStatus("The active session has no snapshot to evaluate.");
                return;
            }

            if (activeSession() is not { } current
                || !current.Id.Equals(session.Id, StringComparison.OrdinalIgnoreCase)
                || generation != operationGeneration)
            {
                SetStatus("The active session changed; stale evaluation evidence was discarded.");
                return;
            }

            var candidate = evaluationService.Capture(session.Id, snapshot);
            var history = historyStore.Load();
            if (generation != operationGeneration)
            {
                return;
            }

            apply(candidate, history);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Evaluation capture was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            SetStatus(FriendlyError(ex));
        }
        finally
        {
            if (generation == operationGeneration)
            {
                operationRunning = false;
                UpdateButtons();
            }
        }
    }

    private void RenderEmpty()
    {
        statusText.Text = "Capture a baseline, then replay the same setup with another model. Factory comparisons also retain the same durable public-group context.";
        historyText.Text = "No local evaluation history";
        baselineText.Text = "Baseline: not selected";
        candidateText.Text = "Current: not captured";
        comparisonSummaryText.Text = "No comparison evidence.";
        qaSummaryText.Text = "QA has not run.";
        comparisonItems.Children.Clear();
        modelItems.Children.Clear();
        qaItems.Children.Clear();
        comparisonItems.Children.Add(CreateStateNotice(
            "Empty",
            "No comparison evidence",
            "Capture a baseline, then replay the same model-neutral setup to compare current values; Factory runs also require matching public-group context."));
        qaItems.Children.Add(CreateStateNotice(
            "Unavailable",
            "QA not run",
            "Run the local evidence checks after a session has produced model turns."));
        AutomationProperties.SetHelpText(statusText, statusText.Text);
    }

    private void RenderCurrent()
    {
        baselineText.Text = lastBaseline is null
            ? "Baseline: no matching setup"
            : $"Baseline: {ArenaEvaluationPresentation.RunLabel(lastBaseline)}";
        candidateText.Text = lastCandidate is null
            ? "Current: not captured"
            : $"Current: {ArenaEvaluationPresentation.RunLabel(lastCandidate)}";

        comparisonItems.Children.Clear();
        if (lastComparison is null)
        {
            var baselineOnly = lastBaseline is not null && ReferenceEquals(lastBaseline, lastCandidate);
            comparisonSummaryText.Text = baselineOnly
                ? "Baseline is ready; run the same setup again to compare."
                : "No comparison evidence.";
            comparisonItems.Children.Add(CreateStateNotice(
                "Empty",
                baselineOnly ? "Baseline ready" : "No comparison evidence",
                comparisonSummaryText.Text));
        }
        else
        {
            comparisonSummaryText.Text = lastComparison.Summary;
            foreach (var metric in lastComparison.Metrics)
            {
                comparisonItems.Children.Add(CreateMetricCard(metric, resourceBrush));
            }
            if (lastComparison.Metrics.Count == 0)
            {
                comparisonItems.Children.Add(CreateStateNotice(
                    "Unavailable",
                    "Comparison unavailable",
                    lastComparison.Summary));
            }
        }

        modelItems.Children.Clear();
        if (lastBaseline is not null)
        {
            modelItems.Children.Add(CreateEvidenceCard(
                "Baseline",
                ArenaEvaluationPresentation.FormatModels(lastBaseline.Models),
                "MutedTextBrush"));
        }

        if (lastCandidate is not null)
        {
            modelItems.Children.Add(CreateEvidenceCard(
                "Current",
                ArenaEvaluationPresentation.FormatModels(lastCandidate.Models),
                "TextBrush"));
        }

        qaItems.Children.Clear();
        if (lastQa is null)
        {
            qaSummaryText.Text = "QA has not run.";
            qaItems.Children.Add(CreateStateNotice(
                "Unavailable",
                "QA not run",
                "Run the local evidence checks after a session has produced model turns."));
        }
        else
        {
            qaSummaryText.Text = ArenaEvaluationPresentation.FormatQa(lastQa);
            foreach (var gate in lastQa.Gates
                         .Where(gate => gate.Status != ArenaQaGateStatuses.Pass)
                         .OrderByDescending(gate => gate.Status == ArenaQaGateStatuses.Fail)
                         .ThenByDescending(gate => gate.Required)
                         .ThenBy(gate => gate.Id, StringComparer.Ordinal)
                         .Take(5))
            {
                qaItems.Children.Add(CreateQaGateCard(gate));
            }
        }
    }

    internal static Border CreateMetricCard(
        ArenaEvaluationMetricComparison metric,
        Func<string, Brush> resourceBrush)
    {
        var kind = ArenaEvaluationPresentation.CardKind(metric.Status);
        var stateLabel = ArenaEvaluationPresentation.StatusLabel(metric.Status);
        var displayLabel = ArenaEvaluationPresentation.DisplayMetricLabel(metric);
        var comparable = metric.Status != ArenaEvaluationStatuses.Unavailable
            && metric.BaselineValue is not null
            && metric.CandidateValue is not null;
        var baselineValue = comparable
            ? ArenaEvaluationPresentation.FormatEndpoint(metric.BaselineValue, metric.Unit)
            : "Unavailable";
        var candidateValue = comparable
            ? ArenaEvaluationPresentation.FormatEndpoint(metric.CandidateValue, metric.Unit)
            : "Unavailable";
        var delta = comparable
            ? ArenaEvaluationPresentation.FormatDirectionalDelta(metric.Delta, metric.Unit)
            : "No observed comparison";

        var card = new AccessibleCardBorder
        {
            Tag = kind,
            Margin = new Thickness(0, 6, 0, 0),
            MinHeight = 120
        };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Arena.StateCard");
        AutomationProperties.SetName(card, $"{stateLabel} comparison metric: {displayLabel}");
        AutomationProperties.SetItemStatus(card, kind);
        AutomationProperties.SetHelpText(
            card,
            $"{displayLabel}. Baseline {baselineValue}. Current {candidateValue}. {delta}. {metric.Explanation} Evidence samples: baseline {metric.BaselineEvidence}, current {metric.CandidateEvidence}.");

        var content = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        header.Children.Add(CreateStatusChip(kind, stateLabel, resourceBrush));
        var label = new TextBlock
        {
            Text = displayLabel,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(label);
        content.Children.Add(header);

        var values = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values.Children.Add(CreateMetricEndpoint("Baseline", baselineValue, resourceBrush));
        var current = CreateMetricEndpoint("Current", candidateValue, resourceBrush);
        Grid.SetColumn(current, 2);
        values.Children.Add(current);
        content.Children.Add(values);

        if (comparable)
        {
            var maximum = Math.Max(Math.Abs(metric.BaselineValue!.Value), Math.Abs(metric.CandidateValue!.Value));
            var sparkline = new MetricSparklineControl
            {
                Height = 32,
                MinWidth = 120,
                Margin = new Thickness(0, 9, 0, 0),
                Mode = "bars",
                Values = [Math.Max(0, metric.BaselineValue.Value), Math.Max(0, metric.CandidateValue.Value)],
                MaxValue = maximum <= 0 ? 1 : maximum * 1.08,
                AccentBrush = resourceBrush(ArenaEvaluationPresentation.BrushKey(metric.Status))
            };
            AutomationProperties.SetName(sparkline, $"{metric.Label} baseline and current comparison bars");
            AutomationProperties.SetHelpText(sparkline, $"Baseline {baselineValue}; current {candidateValue}.");
            content.Children.Add(sparkline);
        }

        var deltaText = new TextBlock
        {
            Text = delta,
            Foreground = resourceBrush(ArenaEvaluationPresentation.BrushKey(metric.Status)),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        content.Children.Add(deltaText);
        content.Children.Add(new TextBlock
        {
            Text = metric.Explanation,
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        card.Child = content;
        ArenaMotion.RevealCard(card);
        return card;
    }

    private static Border CreateMetricEndpoint(
        string label,
        string value,
        Func<string, Brush> resourceBrush)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return new Border { Child = panel };
    }

    private static Border CreateStatusChip(
        string kind,
        string label,
        Func<string, Brush> resourceBrush)
    {
        var chip = new Border
        {
            Tag = kind,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(chip, Dock.Right);
        chip.SetResourceReference(FrameworkElement.StyleProperty, "Arena.StatusChip");
        chip.Child = new TextBlock
        {
            Text = label,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
        return chip;
    }

    private Border CreateEvidenceCard(string label, string details, string brushKey)
    {
        var card = new AccessibleCardBorder { Margin = new Thickness(0, 5, 0, 0) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Arena.Surface.Card");
        card.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, Foreground = resourceBrush("TextBrush"), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = details, Foreground = resourceBrush(brushKey), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
            }
        };
        AutomationProperties.SetName(card, $"{label} model evidence");
        AutomationProperties.SetItemStatus(card, "evidence");
        AutomationProperties.SetHelpText(card, details);
        return card;
    }

    private Border CreateQaGateCard(ArenaRuntimeQaGate gate)
    {
        var kind = gate.Status switch
        {
            ArenaQaGateStatuses.Fail => "Failed",
            ArenaQaGateStatuses.Warn => "Partial",
            ArenaQaGateStatuses.Pass => "Ready",
            _ => "Unavailable"
        };
        var card = new AccessibleCardBorder { Tag = kind, Margin = new Thickness(0, 5, 0, 0) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Arena.StateCard");
        var panel = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        header.Children.Add(CreateStatusChip(kind, kind, resourceBrush));
        header.Children.Add(new TextBlock
        {
            Text = gate.Id,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = gate.Explanation,
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        card.Child = panel;
        AutomationProperties.SetName(card, $"{kind} runtime QA gate: {gate.Id}");
        AutomationProperties.SetItemStatus(card, kind);
        AutomationProperties.SetHelpText(card, gate.Explanation);
        ArenaMotion.RevealCard(card);
        return card;
    }

    private Border CreateStateNotice(string kind, string title, string guidance)
    {
        var card = new AccessibleCardBorder { Tag = kind, Margin = new Thickness(0, 5, 0, 0) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Arena.StateCard");
        card.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Foreground = resourceBrush("TextBrush"), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = guidance, Foreground = resourceBrush("MutedTextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }
            }
        };
        AutomationProperties.SetName(card, $"{kind} state: {title}");
        AutomationProperties.SetItemStatus(card, kind);
        AutomationProperties.SetHelpText(card, guidance);
        return card;
    }

    private void UpdateButtons()
    {
        var canCapture = !operationRunning && !isArenaBusy() && activeSession() is not null;
        captureBaselineButton.IsEnabled = canCapture;
        compareCurrentButton.IsEnabled = canCapture;
        runQaButton.IsEnabled = canCapture;
        copyEvidenceButton.IsEnabled = !operationRunning && lastCandidate is not null;
        copyReplaySetupButton.IsEnabled = !operationRunning && lastCandidate is not null;
    }

    private void SetStatus(string value, bool announceInShell = true)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Evaluation status unavailable." : value.Trim();
        statusText.Text = text;
        AutomationProperties.SetName(statusText, "Evaluation and QA status");
        AutomationProperties.SetHelpText(statusText, text);
        if (announceInShell)
        {
            setArenaRunStatus(text);
        }
    }

    private static string FriendlyError(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "Local evaluation history could not be accessed.",
            IOException => "Local evaluation data could not be read or written.",
            JsonException => "Local evaluation data is invalid and was not used.",
            InvalidOperationException => exception.Message,
            _ => "Evaluation capture failed."
        };
    }
}

internal static class ArenaEvaluationPresentation
{
    internal static string CardKind(string status) => status switch
    {
        ArenaEvaluationStatuses.Improved => "Ready",
        ArenaEvaluationStatuses.Unchanged => "Ready",
        ArenaEvaluationStatuses.Regressed => "Failed",
        ArenaEvaluationStatuses.Unavailable => "Unavailable",
        ArenaEvaluationStatuses.Insufficient => "Unavailable",
        ArenaEvaluationStatuses.NotComparable => "Unavailable",
        _ => "Partial"
    };

    internal static string StatusLabel(string status) => status switch
    {
        ArenaEvaluationStatuses.Improved => "Improved",
        ArenaEvaluationStatuses.Regressed => "Regressed",
        ArenaEvaluationStatuses.Unchanged => "Unchanged",
        ArenaEvaluationStatuses.NotComparable => "Not comparable",
        ArenaEvaluationStatuses.Insufficient => "Insufficient",
        _ => "Unavailable"
    };

    internal static string DisplayMetricLabel(ArenaEvaluationMetricComparison metric) => metric.Id switch
    {
        "quality.score" => "Quality score",
        "run.success-rate" => "Successful turns",
        "run.failed-turns" => "Failed turns",
        "telemetry.average-latency" => "Latency",
        "telemetry.generated-tokens" => "Generated tokens",
        "telemetry.throughput" => "Throughput",
        _ => metric.Label
    };

    internal static string FormatEndpoint(double? value, string unit)
    {
        if (value is null) return "Unavailable";
        var suffix = unit.Equals("percentage points", StringComparison.OrdinalIgnoreCase)
            ? "%"
            : string.IsNullOrWhiteSpace(unit)
                ? ""
                : $" {unit}";
        return FormatNumber(value.Value) + suffix;
    }

    internal static string FormatDirectionalDelta(double? delta, string unit)
    {
        if (delta is null) return "No observed delta";
        var glyph = delta.Value > 0 ? "↑" : delta.Value < 0 ? "↓" : "→";
        var suffix = unit.Equals("percentage points", StringComparison.OrdinalIgnoreCase)
            ? " pp"
            : string.IsNullOrWhiteSpace(unit)
                ? ""
                : $" {unit}";
        return $"{glyph} {FormatSigned(delta.Value)}{suffix}";
    }

    public static string FormatHistory(int count)
    {
        return count == 1 ? "1 local run" : $"{Math.Max(0, count)} local runs";
    }

    public static string RunLabel(ArenaEvaluationRecord evaluation)
    {
        var modelTurns = evaluation.Evidence.ModelTurns;
        var modelLabel = evaluation.Models.Count == 0
            ? "no model evidence"
            : string.Join(", ", evaluation.Models.Select(model => model.Model).Take(2));
        var factoryGroup = evaluation.FactoryGroupContext is null
            ? ""
            : string.IsNullOrWhiteSpace(evaluation.FactoryGroupContext.ContextFingerprint)
                ? " · Factory group unavailable"
                : $" · Factory group {evaluation.FactoryGroupContext.CausalSampleCount} causal call(s), latest {evaluation.FactoryGroupContext.IncludedEntryCount}/{evaluation.FactoryGroupContext.EligibleEntryCount} entries";
        return $"{modelLabel} · {modelTurns} model turn{(modelTurns == 1 ? "" : "s")}{factoryGroup} · {evaluation.RunId}";
    }

    public static string FormatModels(IReadOnlyList<ArenaEvaluationModelAggregate> models)
    {
        if (models.Count == 0)
        {
            return "No model telemetry.";
        }

        var visible = models.Take(4).Select(model =>
        {
            var parts = new List<string>
            {
                model.Model,
                $"{model.Turns} turn{(model.Turns == 1 ? "" : "s")}"
            };
            if (model.AverageLatencyMs is > 0)
            {
                parts.Add($"{model.AverageLatencyMs.Value.ToString("N0", CultureInfo.InvariantCulture)} ms");
            }

            if (model.AverageTokensPerSecond is > 0)
            {
                parts.Add($"{model.AverageTokensPerSecond.Value.ToString("0.#", CultureInfo.InvariantCulture)} tok/s");
            }

            return string.Join(" · ", parts);
        }).ToList();
        if (models.Count > visible.Count)
        {
            visible.Add($"+{models.Count - visible.Count} more");
        }

        return string.Join("; ", visible);
    }

    public static string FormatMetric(ArenaEvaluationMetricComparison metric)
    {
        var glyph = metric.Status switch
        {
            ArenaEvaluationStatuses.Improved => "✓",
            ArenaEvaluationStatuses.Regressed => "!",
            ArenaEvaluationStatuses.Unchanged => "=",
            _ => "—"
        };
        if (metric.Status == ArenaEvaluationStatuses.Unavailable
            || metric.BaselineValue is null
            || metric.CandidateValue is null)
        {
            return $"{glyph} {metric.Label}: unavailable ({metric.Explanation})";
        }

        var baseline = FormatNumber(metric.BaselineValue.Value);
        var candidate = FormatNumber(metric.CandidateValue.Value);
        var delta = metric.Delta is null
            ? ""
            : $", Δ {FormatSigned(metric.Delta.Value)}";
        return $"{glyph} {metric.Label}: {baseline} → {candidate} {metric.Unit}{delta}";
    }

    public static string FormatQa(ArenaRuntimeQaReport report)
    {
        return $"{report.OverallReadiness.ToUpperInvariant()} · {report.Passed} pass · {report.Warnings} warn · {report.Failed} fail · {report.Unavailable} unavailable";
    }

    public static string FormatGate(ArenaRuntimeQaGate gate)
    {
        var glyph = gate.Status switch
        {
            ArenaQaGateStatuses.Fail => "!",
            ArenaQaGateStatuses.Warn => "△",
            ArenaQaGateStatuses.Pass => "✓",
            _ => "—"
        };
        return $"{glyph} {gate.Id}: {gate.Explanation}";
    }

    public static string BrushKey(string status)
    {
        return status switch
        {
            ArenaEvaluationStatuses.Improved => "PrimaryBorderBrush",
            ArenaEvaluationStatuses.Regressed => "DangerTextBrush",
            ArenaEvaluationStatuses.Unchanged => "TextBrush",
            _ => "MutedTextBrush"
        };
    }

    public static string QaBrushKey(string status)
    {
        return status switch
        {
            ArenaQaGateStatuses.Pass => "PrimaryBorderBrush",
            ArenaQaGateStatuses.Fail => "DangerTextBrush",
            ArenaQaGateStatuses.Warn => "BetaAccentBrush",
            _ => "MutedTextBrush"
        };
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.001
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string FormatSigned(double value)
    {
        return value > 0
            ? "+" + FormatNumber(value)
            : value < 0
                ? "−" + FormatNumber(Math.Abs(value))
                : "0";
    }
}
