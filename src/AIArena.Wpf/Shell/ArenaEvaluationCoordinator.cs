using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Persistence;
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
                SetStatus("Baseline captured. Change the model or provider, replay the same setup, then compare current.");
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
        statusText.Text = "Capture a baseline, then replay the same setup with another model to compare it.";
        historyText.Text = "No local evaluation history";
        baselineText.Text = "Baseline: not selected";
        candidateText.Text = "Current: not captured";
        comparisonSummaryText.Text = "No comparison evidence.";
        qaSummaryText.Text = "QA has not run.";
        comparisonItems.Children.Clear();
        modelItems.Children.Clear();
        qaItems.Children.Clear();
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
            comparisonSummaryText.Text = lastBaseline is not null && ReferenceEquals(lastBaseline, lastCandidate)
                ? "Baseline is ready; run the same setup again to compare."
                : "No comparison evidence.";
        }
        else
        {
            comparisonSummaryText.Text = lastComparison.Summary;
            foreach (var metric in lastComparison.Metrics)
            {
                comparisonItems.Children.Add(CreateLine(
                    ArenaEvaluationPresentation.FormatMetric(metric),
                    ArenaEvaluationPresentation.BrushKey(metric.Status)));
            }
        }

        modelItems.Children.Clear();
        if (lastBaseline is not null)
        {
            modelItems.Children.Add(CreateLine(
                "Baseline · " + ArenaEvaluationPresentation.FormatModels(lastBaseline.Models),
                "MutedTextBrush"));
        }

        if (lastCandidate is not null)
        {
            modelItems.Children.Add(CreateLine(
                "Current · " + ArenaEvaluationPresentation.FormatModels(lastCandidate.Models),
                "TextBrush"));
        }

        qaItems.Children.Clear();
        if (lastQa is null)
        {
            qaSummaryText.Text = "QA has not run.";
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
                qaItems.Children.Add(CreateLine(
                    ArenaEvaluationPresentation.FormatGate(gate),
                    ArenaEvaluationPresentation.QaBrushKey(gate.Status)));
            }
        }
    }

    private TextBlock CreateLine(string text, string brushKey)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = resourceBrush(brushKey),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
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
        return $"{modelLabel} · {modelTurns} model turn{(modelTurns == 1 ? "" : "s")} · {evaluation.RunId}";
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
