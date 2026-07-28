using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections.ObjectModel;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static void ArenaOperationCoordinatorSelectsOperationMode()
{
    Require(ArenaOperationCoordinator.OperationMode(busy: false, allowDuringAutoChat: false, autoChatRunning: false) == ArenaOperationMode.OwnsBusyState, "idle operation should own busy state");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: false, autoChatRunning: true) == ArenaOperationMode.Blocked, "busy operation should block without explicit auto-chat allowance");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: true, autoChatRunning: false) == ArenaOperationMode.Blocked, "auto-chat allowance should not run when auto-chat is inactive");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: true, autoChatRunning: true) == ArenaOperationMode.RunsDuringAutoChat, "allowed operation should run during active auto-chat");
    Require(ArenaOperationCoordinator.ShouldAnimateOperationButton(systemAnimationsEnabled: true, breathing: true), "active operation controls may animate when Windows animations are enabled");
    Require(!ArenaOperationCoordinator.ShouldAnimateOperationButton(systemAnimationsEnabled: false, breathing: true), "Windows reduced-motion preference should suppress operation animation clocks");
    Require(!ArenaOperationCoordinator.ShouldAnimateOperationButton(systemAnimationsEnabled: true, breathing: false), "idle operation controls should never animate");

    var offlineNoCast = SnapshotForOverviewTest(false, "-", "offline", 0, [], []);
    var cast = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "model-a", true, false, []);
    var offlineWithCast = SnapshotForOverviewTest(false, "model-a", "offline", 0, [], [cast]);
    var readySnapshot = SnapshotForOverviewTest(true, "model-a", "", 0, [], [cast]);
    Require(!ArenaOperationCoordinator.EvaluateReadiness(offlineNoCast).CanRun, "arena actions should remain gated while provider and cast setup are both incomplete");
    Require(ArenaOperationCoordinator.EvaluateReadiness(offlineWithCast).Message.Contains("provider", StringComparison.OrdinalIgnoreCase), "readiness feedback should identify provider setup as the blocker");
    Require(ArenaOperationCoordinator.EvaluateReadiness(readySnapshot).CanRun, "an online provider, selected model, and active agent should enable arena actions");

    RunStaTest(() =>
    {
        var disabledDuringBusy = new Button { IsEnabled = true };
        var autoChatButton = new Button();
        var oneTurnButton = new Button();
        var narrateButton = new Button();
        var stopButton = new Button();
        var callbackCount = 0;
        var busyFlag = false;
        var loadStatus = new TextBlock();
        var arenaStatus = new TextBlock();
        var coordinator = new ArenaOperationCoordinator(
            new SemaphoreSlim(1, 1),
            loadStatus,
            arenaStatus,
            autoChatButton,
            oneTurnButton,
            new Button(),
            narrateButton,
            stopButton,
            [disabledDuringBusy],
            () => busyFlag,
            value => busyFlag = value,
            () => false,
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            _ => { },
            () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => callbackCount++);

        coordinator.UpdateReadiness(new ArenaActionReadiness(true, "Arena actions ready."));
        Require(autoChatButton.IsEnabled && oneTurnButton.IsEnabled && narrateButton.IsEnabled, "ready arena actions should become available while idle");

        coordinator.SetBusy(true, "busy", stopEnabled: false);
        Require(!disabledDuringBusy.IsEnabled, "busy controls should disable while an arena operation owns busy state");
        Require(callbackCount == 0, "after-busy callback should not run when busy starts and re-enable gated controls");

        coordinator.SetBusy(false, "idle", stopEnabled: false);
        Require(disabledDuringBusy.IsEnabled, "busy controls should re-enable when arena busy ends");
        Require(callbackCount == 1, "after-busy callback should run once after busy ends");

        coordinator.RunAsync(
            "saving stale snapshot",
            () => throw new SnapshotConcurrencyException("snapshot.json", 1, 2)).GetAwaiter().GetResult();
        Require(arenaStatus.Text.Contains("Reload", StringComparison.Ordinal), "snapshot conflicts should become a recoverable arena status instead of escaping an async UI handler");
        Require(loadStatus.Text.Contains("not overwritten", StringComparison.Ordinal), "snapshot conflict status should confirm newer data was preserved");
        Require(!busyFlag && disabledDuringBusy.IsEnabled, "snapshot conflict handling should restore idle controls");

        coordinator.RunAsync(
            "writing event log",
            () => throw new IOException("  disk   became unavailable\r\nwhile saving  ")).GetAwaiter().GetResult();
        Require(arenaStatus.Text == "Operation failed: disk became unavailable while saving", "ordinary operation failures should become compact UI status instead of escaping an async event handler");
        Require(loadStatus.Text == arenaStatus.Text, "operation failure should stay consistent across status surfaces");
        Require(!busyFlag && disabledDuringBusy.IsEnabled, "ordinary failure handling should restore idle controls");
        Require(
            ArenaOperationCoordinator.OperationFailureStatus(new IOException(new string('x', 2000))).Length <= 300,
            "operation failure status should remain bounded for pathological exception messages");
        Require(
            ArenaOperationCoordinator.OperationFailureStatus(new InvalidOperationException("Bearer sk-proj-abcdefghijklmnopqrs"))
                == "Operation failed; sensitive error details were redacted.",
            "operation failure status should not publish credential-like exception details");

        coordinator.RunAsync(
            "cancelled operation",
            () => throw new OperationCanceledException()).GetAwaiter().GetResult();
        Require(arenaStatus.Text == "Operation cancelled.", "operation cancellation should not be reported as an app failure");
        Require(!busyFlag && disabledDuringBusy.IsEnabled, "cancellation handling should restore idle controls");

        using var operationStarted = new ManualResetEventSlim();
        using var cancellationObserved = new ManualResetEventSlim();
        using var allowTrackedCleanup = new ManualResetEventSlim();
        var trackedOperation = coordinator.TrackAsync(async cancellationToken =>
        {
            operationStarted.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.Set();
            }

            Require(allowTrackedCleanup.Wait(TimeSpan.FromSeconds(5)), "tracked provider-style cleanup was never released");
        });

        Require(operationStarted.Wait(TimeSpan.FromSeconds(5)), "tracked provider-style operation did not start");
        var drain = coordinator.DrainAsync();
        Require(cancellationObserved.Wait(TimeSpan.FromSeconds(5)), "tracked operation did not receive shutdown cancellation");
        var drainWasPending = !drain.IsCompleted;
        allowTrackedCleanup.Set();
        trackedOperation.GetAwaiter().GetResult();
        Require(drain.Wait(TimeSpan.FromSeconds(5)), "arena operation shutdown did not drain tracked work");

        Require(drainWasPending, "shutdown drain should wait for the actual tracked task to finish cleanup after cancellation");
        var lateActionRan = false;
        coordinator.RunAsync("late operation", () =>
        {
            lateActionRan = true;
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Require(!lateActionRan, "new arena operations must be rejected after shutdown begins");
    });
}

static void TranscriptAdjunctHelpersFormatLabels()
{
    var left = TranscriptForTest(4, "Alpha", "alpha", "message", "ok");
    var right = TranscriptForTest(2, "Beta", "beta", "message", "ok");

    Require(TranscriptAdjunctCoordinator.CompareSummary(left, right) == "Comparing turn 4 (Alpha) with turn 2 (Beta).", "compare summary should remain stable");
    Require(TranscriptAdjunctCoordinator.CompareDelta(8, 3) == "+5", "positive compare delta should include plus sign");
    Require(TranscriptAdjunctCoordinator.CompareDelta(3, 8) == "-5", "negative compare delta should use invariant formatting");
    Require(TranscriptAdjunctCoordinator.CompareDelta(3, 3) == "0", "zero compare delta should be plain zero");
}

static void DiagnosticsWaitForFirstScoredTurn()
{
    var systemOnly = new[] { TranscriptForTest(0, "System", "system", "status", "ok") };
    Require(!DiagnosticsWorkflowCoordinator.HasScoredTurns(systemOnly, message => message.SpeakerId == "system"), "zero-turn/system-only transcripts should show a neutral pending-metrics state");

    var firstAgentTurn = systemOnly.Append(TranscriptForTest(1, "Alpha", "alpha", "message", "ok")).ToArray();
    Require(DiagnosticsWorkflowCoordinator.HasScoredTurns(firstAgentTurn, message => message.SpeakerId == "system"), "diagnostic metrics should activate after the first scored agent turn");

    Require(DiagnosticsWorkflowCoordinator.ToneForState("Healthy") == DiagnosticVisualTone.Neutral, "healthy friction should remain visually neutral");
    Require(DiagnosticsWorkflowCoordinator.ToneForState("Too Cold") == DiagnosticVisualTone.Neutral, "insufficient diagnostic history should remain visually neutral");
    Require(DiagnosticsWorkflowCoordinator.ToneForState("Productive Conflict") == DiagnosticVisualTone.Neutral, "productive conflict should not borrow an agent identity accent");
    Require(DiagnosticsWorkflowCoordinator.ToneForState("Source Conflict") == DiagnosticVisualTone.Warning, "active source conflict should receive warning emphasis");
    Require(DiagnosticsWorkflowCoordinator.ToneForState("Theatre Risk") == DiagnosticVisualTone.Critical, "active theatre risk should receive critical emphasis");
    Require(DiagnosticsWorkflowCoordinator.ToneForRisk("Low") == DiagnosticVisualTone.Neutral, "low risk should remain neutral");
    Require(DiagnosticsWorkflowCoordinator.ToneForRisk("Medium") == DiagnosticVisualTone.Warning, "medium risk should receive warning emphasis");
    Require(DiagnosticsWorkflowCoordinator.ToneForRisk("High") == DiagnosticVisualTone.Critical, "high risk should receive critical emphasis");
    Require(DiagnosticsWorkflowCoordinator.ToneForEvidence("Strong") == DiagnosticVisualTone.Neutral, "strong evidence should remain neutral");
    Require(DiagnosticsWorkflowCoordinator.ToneForEvidence("Weak") == DiagnosticVisualTone.Critical, "weak evidence should receive critical emphasis");
    Require(DiagnosticsWorkflowCoordinator.ToneForNarrative("Low") == DiagnosticVisualTone.Neutral, "low narrative heat should remain neutral");
    Require(DiagnosticsWorkflowCoordinator.ToneForNarrative("Rising") == DiagnosticVisualTone.Warning, "rising narrative heat should receive warning emphasis");

    RunStaTest(() =>
    {
        var textBrush = Brushes.White;
        var mutedBrush = Brushes.Gray;
        var inputBrush = Brushes.Black;
        var controlBorderBrush = Brushes.DimGray;
        var warningBrush = Brushes.Gold;
        var criticalBrush = Brushes.Crimson;
        Brush Resource(string key) => key switch
        {
            "TextBrush" => textBrush,
            "MutedTextBrush" => mutedBrush,
            "InputBrush" => inputBrush,
            "ControlBorderBrush" => controlBorderBrush,
            "BetaAccentBrush" => warningBrush,
            "DangerBorderBrush" => criticalBrush,
            _ => Brushes.Transparent
        };

        var metricsGrid = new Grid();
        var emptyState = new Border();
        var frictionChip = new Border();
        var frictionValue = new TextBlock();
        var frictionTrend = new TextBlock();
        var consensusChip = new Border();
        var consensusValue = new TextBlock();
        var consensusTrend = new TextBlock();
        var consensusSparkline = new MetricSparklineControl();
        var roleDriftChip = new Border();
        var roleDriftValue = new TextBlock();
        var roleDriftTrend = new TextBlock();
        var roleDriftSparkline = new MetricSparklineControl();
        var unsupportedChip = new Border();
        var unsupportedValue = new TextBlock();
        var unsupportedTrend = new TextBlock();
        var unsupportedSparkline = new MetricSparklineControl();
        var evidenceChip = new Border();
        var evidenceValue = new TextBlock();
        var evidenceTrend = new TextBlock();
        var evidenceSparkline = new MetricSparklineControl();
        var narrativeChip = new Border();
        var narrativeValue = new TextBlock();
        var narrativeTrend = new TextBlock();
        var narrativeSparkline = new MetricSparklineControl();
        var coordinator = new DiagnosticsWorkflowCoordinator(
            new DiscourseDiagnosticsService(),
            metricsGrid,
            emptyState,
            frictionChip,
            frictionValue,
            frictionTrend,
            consensusChip,
            consensusValue,
            consensusTrend,
            consensusSparkline,
            roleDriftChip,
            roleDriftValue,
            roleDriftTrend,
            roleDriftSparkline,
            unsupportedChip,
            unsupportedValue,
            unsupportedTrend,
            unsupportedSparkline,
            evidenceChip,
            evidenceValue,
            evidenceTrend,
            evidenceSparkline,
            narrativeChip,
            narrativeValue,
            narrativeTrend,
            narrativeSparkline,
            new Popup(),
            new TextBlock(),
            new TextBlock(),
            new StackPanel(),
            () => new Dictionary<string, string>(),
            () => [],
            Resource,
            value => value,
            (message, isInternet) => isInternet || message.SpeakerId.Equals("system", StringComparison.OrdinalIgnoreCase),
            (_, right, _) => right);

        void RequireNeutral(
            Border chip,
            TextBlock value,
            TextBlock trend,
            MetricSparklineControl? sparkline,
            string label)
        {
            Require(ReferenceEquals(chip.Background, inputBrush), $"{label} should use the neutral tile background");
            Require(ReferenceEquals(chip.BorderBrush, controlBorderBrush), $"{label} should use the neutral tile border");
            Require(ReferenceEquals(value.Foreground, textBrush), $"{label} should use neutral primary text");
            Require(ReferenceEquals(trend.Foreground, mutedBrush), $"{label} should use neutral trend text");
            if (sparkline is not null)
            {
                Require(ReferenceEquals(sparkline.AccentBrush, mutedBrush), $"{label} should use a neutral sparkline");
            }
        }

        coordinator.InitializeTiles();
        RequireNeutral(frictionChip, frictionValue, frictionTrend, null, "new friction tile");
        RequireNeutral(consensusChip, consensusValue, consensusTrend, consensusSparkline, "new consensus tile");
        RequireNeutral(evidenceChip, evidenceValue, evidenceTrend, evidenceSparkline, "new evidence tile");

        frictionChip.Background = warningBrush;
        frictionChip.BorderBrush = warningBrush;
        frictionValue.Foreground = warningBrush;
        coordinator.Update(systemOnly);
        Require(metricsGrid.Visibility == Visibility.Collapsed && emptyState.Visibility == Visibility.Visible, "zero-turn diagnostics should keep metrics hidden behind the pending state");
        RequireNeutral(frictionChip, frictionValue, frictionTrend, null, "zero-turn friction tile");

        coordinator.Update(
        [
            TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "I propose a bounded mechanism." },
            TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "A concrete constraint keeps the mechanism scoped." }
        ]);
        RequireNeutral(frictionChip, frictionValue, frictionTrend, null, "healthy friction tile");
        RequireNeutral(consensusChip, consensusValue, consensusTrend, consensusSparkline, "low-consensus tile");
        RequireNeutral(roleDriftChip, roleDriftValue, roleDriftTrend, roleDriftSparkline, "low-role-drift tile");
        RequireNeutral(unsupportedChip, unsupportedValue, unsupportedTrend, unsupportedSparkline, "low-unsupported-claims tile");
        RequireNeutral(narrativeChip, narrativeValue, narrativeTrend, narrativeSparkline, "low-narrative-heat tile");
        Require(ReferenceEquals(evidenceValue.Foreground, criticalBrush), "an active weak-evidence condition should receive critical value emphasis");
        Require(ReferenceEquals(evidenceChip.BorderBrush, criticalBrush), "an active weak-evidence condition should receive a critical border");
        Require(ReferenceEquals(evidenceChip.Background, criticalBrush), "an active weak-evidence condition should receive a critical background tint");
    });
}

static void TranscriptBattleReviewSummarizesMatch()
{
    var diagnostics = new FrictionDiagnostics(
        "Theatre Risk",
        "danger",
        82,
        "High",
        42,
        "High",
        2,
        "danger",
        18,
        "Weak",
        86,
        "High",
        new Dictionary<string, MetricDiagnostic>
        {
            ["sourceConflicts"] = new MetricDiagnostic("Present", 1, ["Alpha and Beta have sourced claims that should be compared."]),
            ["unsupportedClaims"] = new MetricDiagnostic("High", 2, ["Detected unsupported metric: 90% certain"]),
            ["consensus"] = new MetricDiagnostic("Medium", 40, ["Friction: Beta rejects Alpha's confidence."])
        },
        1,
        "Present");
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with
        {
            Text = "The regulator update changes the deployment threshold. This should slow the rollout until compliance is mapped.",
            InternetSources = ["AI law update - https://example.test/ai-law - Regulator update changes deployment threshold."],
            InternetCheckedAt = "2026-06-17 10:00 +01:00",
            Model = "model-a",
            PromptTokens = 100,
            CompletionTokens = 80,
            TotalTokens = 180,
            LatencyMs = 1200
        },
        TranscriptForTest(2, "Beta", "beta", "message", "ok") with
        {
            Text = "However, that source conflicts with the agency FAQ, so the rollout claim is not proven yet.",
            Model = "model-b",
            PromptTokens = 130,
            CompletionTokens = 110,
            TotalTokens = 240,
            LatencyMs = 33000
        },
        TranscriptForTest(3, "Narrator", "narrator", "narration", "ok") with
        {
            Model = "model-n",
            PromptTokens = 90,
            CompletionTokens = 40,
            TotalTokens = 130,
            LatencyMs = 800
        }
    };

    var review = TranscriptAdjunctCoordinator.BuildBattleReview(messages, diagnostics);
    var lines = TranscriptAdjunctCoordinator.BattleReviewLines(review);
    var text = TranscriptAdjunctCoordinator.BattleReviewText(review);
    var markdown = TranscriptAdjunctCoordinator.BattleReviewMarkdown(review);
    var json = TranscriptAdjunctCoordinator.BattleReviewJson(review);
    var nudge = TranscriptAdjunctCoordinator.BattleReviewNudgeText(review);

    Require(review.Verdict == "Needs intervention", "danger diagnostics should request intervention");
    Require(review.Score < 64, "battle review score should reflect multiple risk signals");
    Require(review.TotalTokens == 550, "battle review should sum token totals");
    Require(review.ModelCount == 3, "battle review should count distinct models");
    Require(review.LeadingVoice.Contains("Beta", StringComparison.Ordinal), "battle review should name the leading token voice");
    Require(review.WatchTarget == "evidence", "weak evidence should be the priority watch target");
    Require(review.Flags.Any(flag => flag.Contains("unsupported", StringComparison.OrdinalIgnoreCase)), "battle review should flag unsupported claims");
    Require(review.Flags.Any(flag => flag.Contains("slow turn 2", StringComparison.OrdinalIgnoreCase)), "battle review should flag slow turns");
    Require(review.SlowestTurn.Contains("turn 2", StringComparison.OrdinalIgnoreCase), "battle review should identify slowest turn");
    Require(review.AfterActionReport.MainClaims.Any(claim => claim.Contains("regulator update", StringComparison.OrdinalIgnoreCase)), "after action report should include main claims");
    Require(review.AfterActionReport.SourcedClaims.Any(claim => claim.Contains("source(s)", StringComparison.OrdinalIgnoreCase)), "after action report should include sourced claims");
    Require(review.AfterActionReport.KeySources.Single().Url == "https://example.test/ai-law", "after action report should include key source URL");
    Require(review.AfterActionReport.SourceConflicts.Any(conflict => conflict.Contains("compared", StringComparison.OrdinalIgnoreCase)), "after action report should include source conflicts");
    Require(review.AfterActionReport.UnresolvedDisagreements.Any(item => item.Contains("However", StringComparison.OrdinalIgnoreCase)), "after action report should include unresolved disagreements");
    Require(review.AfterActionReport.StrongestArgument.Contains("Alpha", StringComparison.Ordinal), "after action report should identify strongest argument");
    Require(review.AfterActionReport.BestAgentPerformance.Contains("Alpha", StringComparison.Ordinal), "after action report should identify best agent performance");
    Require(lines.Any(line => line.StartsWith("Sourced claims:", StringComparison.Ordinal)), "battle review lines should include sourced claims");
    Require(lines.Any(line => line.StartsWith("Key sources:", StringComparison.Ordinal)), "battle review lines should include key sources");
    Require(json.Contains("\"afterAction\"", StringComparison.Ordinal), "battle review JSON should include after action data");
    Require(json.Contains("https://example.test/ai-law", StringComparison.Ordinal), "battle review JSON should include source URLs");
    Require(lines.Any(line => line.StartsWith("Next:", StringComparison.Ordinal)), "battle review lines should include a next action");
    Require(text.StartsWith("# AI Arena Battle Review", StringComparison.Ordinal), "battle review copy text should have a markdown title");
    Require(markdown.Contains("Sourced claims:", StringComparison.Ordinal), "battle review markdown should include sourced claims");
    Require(text.Contains("Source conflicts:", StringComparison.Ordinal), "battle review markdown should include source conflicts");
    Require(nudge.StartsWith("Operator intervention:", StringComparison.Ordinal), "battle review nudge should be ready to paste into operator turn");
    Require(nudge.Contains("score", StringComparison.OrdinalIgnoreCase), "battle review nudge should include review context");
}

static void TranscriptRunTraceSummarizesSpans()
{
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with
        {
            Model = "model-a",
            PromptTokens = 100,
            CompletionTokens = 80,
            TotalTokens = 180,
            LatencyMs = 1200
        },
        TranscriptForTest(2, "Tool", "internet", "internet", "pending") with
        {
            InternetRequester = "alpha",
            InternetTool = "web_search",
            InternetQuery = "AI regulation",
            TotalTokens = 0
        },
        TranscriptForTest(3, "Internet", "internet", "internet", "ok") with
        {
            InternetTool = "web_search",
            InternetSources = ["https://example.test/source"],
            InternetCached = true,
            TotalTokens = 0
        },
        TranscriptForTest(4, "Beta", "beta", "message", "error") with
        {
            Model = "model-b",
            PromptTokens = 3500,
            CompletionTokens = 800,
            TotalTokens = 4300,
            LatencyMs = 34000,
            Text = ""
        }
    };

    var trace = TranscriptAdjunctCoordinator.BuildRunTrace(messages);
    var lines = TranscriptAdjunctCoordinator.RunTraceLines(trace);
    var text = TranscriptAdjunctCoordinator.RunTraceText(trace);
    var beta = trace.Spans.Single(span => span.Turn == 4);
    var pendingTool = trace.Spans.Single(span => span.Turn == 2);
    var tool = trace.Spans.Single(span => span.Turn == 3);

    Require(trace.SpanCount == 4, "run trace should count every transcript span");
    Require(trace.ModelCallCount == 2, "run trace should count agent model spans");
    Require(trace.ToolCallCount == 2, "run trace should count internet tool spans");
    Require(trace.IssueCount == 2, "run trace should count pending/error/slow/high-token issue spans");
    Require(trace.TotalTokens == 4480, "run trace should sum token totals");
    Require(trace.SlowestSpan.Contains("Turn 4 Beta", StringComparison.Ordinal), "run trace should name the slowest span");
    Require(trace.NextAction.Contains("Repair", StringComparison.OrdinalIgnoreCase), "run trace should prioritize repairable failures");
    Require(trace.Triage.Severity == "Repair", "errors should drive run trace triage");
    Require(trace.Triage.Focus.Contains("Turn 4 Beta", StringComparison.Ordinal), "trace triage should focus the failed model span first");
    Require(trace.Triage.PendingSpanCount == 1, "trace triage should count generic pending spans");
    Require(trace.Triage.ErrorSpanCount == 1, "trace triage should count repair spans");
    Require(trace.Triage.SlowSpanCount == 1, "trace triage should count slow spans");
    Require(trace.Triage.HighTokenSpanCount == 1, "trace triage should count high-token spans");
    Require(trace.Triage.ToolSourceSpanCount == 2, "trace triage should count tool and source spans");
    Require(trace.Triage.ReviewTurns.Any(line => line.Contains("Turn 2", StringComparison.Ordinal)), "trace triage should include pending tool spans in review queue");
    Require(trace.Triage.ReviewTurns.Any(line => line.Contains("Turn 4", StringComparison.Ordinal)), "trace triage should include failed model span in review queue");
    Require(pendingTool.Kind == "internet tool" && pendingTool.Flags.Contains("pending") && pendingTool.Flags.Contains("tool"), "pending tool span should expose pending tool flags");
    Require(tool.Flags.Contains("tool") && tool.Flags.Contains("cached") && tool.Flags.Contains("1 source(s)"), "tool span should expose source and cache flags");
    Require(beta.Flags.Contains("error") && beta.Flags.Contains("slow") && beta.Flags.Contains("high tokens") && beta.Flags.Contains("empty text"), "problem span should expose issue flags");
    Require(lines.Any(line => line.StartsWith("Recent spans:", StringComparison.Ordinal)), "trace lines should include a recent spans section");
    Require(lines.Any(line => line.StartsWith("Triage:", StringComparison.Ordinal)), "trace lines should include triage summary");
    Require(lines.Any(line => line.StartsWith("Review queue:", StringComparison.Ordinal)), "trace lines should include a review queue");
    Require(text.StartsWith("AI Arena Run Trace", StringComparison.Ordinal), "copied run trace should have a stable title");
    Require(text.Contains("Focus: Turn 4 Beta", StringComparison.Ordinal), "copied run trace should include triage focus");
    Require(text.Contains("Next:", StringComparison.Ordinal), "copied run trace should include the recommended next action");
    Require(text.Contains("Tool events: 2", StringComparison.Ordinal), "copied run trace should include tool count");
    Require(text.Contains("high tokens", StringComparison.Ordinal), "copied run trace should include issue flags");

    var cleanTriage = TranscriptAdjunctCoordinator.BuildRunTraceTriage([
        new TranscriptRunTraceSpan(1, "alpha", "Alpha", "agent model", "ok", "model-a", 100, 10, 5, 15, [])
    ]);
    Require(cleanTriage.Severity == "Clean", "clean trace triage should stay clean");
    Require(cleanTriage.Focus.Contains("Turn 1 Alpha", StringComparison.Ordinal), "clean trace triage should focus the latest clean span");
}

static void TranscriptCardRendererLabelsInternetCards()
{
    var tool = TranscriptForTest(8, "Tool", "internet", "internet", "ok");
    const bool isInternet = true;
    var toolSystemEvent = TranscriptCardRenderer.IsSystemEvent(tool, isInternet);

    Require(toolSystemEvent, "internet tool cards should retain system-event card styling");
    Require(TranscriptCardRenderer.TranscriptSpeakerTitle(tool, isInternet, toolSystemEvent) == "Internet Tool", "tool title should be internet tool");
    Require(TranscriptCardRenderer.TranscriptRailLabel(tool, isInternet) == "Internet", "internet rail label should be internet");
    Require(TranscriptCardRenderer.CompactRailLabel(tool, isInternet) == "WEB", "compact internet rail label should be WEB");
    Require(TranscriptCardRenderer.CanSpeakMessage(tool with { Text = "Fetched source summary." }), "ordinary transcript cards with text should expose speech playback");
    Require(!TranscriptCardRenderer.CanSpeakMessage(tool with { Text = "" }), "empty transcript cards should not expose speech playback");
}

static void TranscriptCardRendererHidesInternetMetadataByDefault()
{
    var message = TranscriptForTest(9, "Beta", "beta", "message", "ok") with
    {
        InternetRequester = "beta",
        InternetTool = "web_search",
        InternetQuery = "AI safety regulation 2026",
        InternetSummary = "Fetched current context.",
        InternetSources = ["Source: https://example.test"]
    };

    Require(!TranscriptCardRenderer.ShouldRenderInternetDetails(message, showDebugDetails: false), "normal transcript cards should hide internet metadata");
    Require(TranscriptCardRenderer.ShouldRenderInternetDetails(message, showDebugDetails: true), "debug transcript cards should reveal internet metadata");
    Require(!TranscriptCardRenderer.ShouldRenderInternetDetails(message with { InternetQuery = "", InternetTool = "", InternetSummary = "", InternetSources = [] }, showDebugDetails: true), "debug transcript cards should not render an empty details expander");
}

static void TranscriptCardRendererShowsSourceGlobeOnSourcedTurns()
{
    RunStaTest(() =>
    {
        var renderer = CreateTranscriptCardRendererForTest();
        var sourced = TranscriptForTest(10, "Alpha", "alpha", "message", "ok") with
        {
            InternetQuery = "latest AI policy",
            InternetCheckedAt = "2026-06-23 10:00:00 +01:00",
            InternetSources = ["Policy source - https://example.test/policy - compact snippet"]
        };
        var plain = TranscriptForTest(11, "Beta", "beta", "message", "ok");

        var sourcedCard = renderer.CreateCard(sourced, retryable: true, searchMatch: false, isLatest: false);
        var plainCard = renderer.CreateCard(plain, retryable: true, searchMatch: false, isLatest: false);
        var sourcedButtons = DescendantButtons(sourcedCard).ToArray();
        var plainButtons = DescendantButtons(plainCard).ToArray();
        var sourceButton = sourcedButtons.SingleOrDefault(button =>
            AutomationProperties.GetName(button) == "Show internet sources for turn 10");

        Require(sourceButton is not null, "source-backed transcript turns should expose a source globe button");
        Require(!plainButtons.Any(button => AutomationProperties.GetName(button).StartsWith("Show internet sources", StringComparison.Ordinal)), "turns without sources should not expose a source globe button");
        Require(sourceButton!.ToolTip?.ToString()?.Contains("Searched web: 1 source", StringComparison.OrdinalIgnoreCase) == true, "source globe should provide the searched-web cue");
        Require(sourceButton.Content is TextBlock glyph && glyph.Text == "\uE774", "source globe should use the universal web glyph");
        Require(sourceButton.MinWidth >= 36 && sourceButton.MinHeight >= 36, "the source globe should keep a compact but safe pointer target");

        sourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(sourceButton.Tag is Popup popup && popup.Child is not null, "clicking source globe should open the sources popup");
    });
}

static void TranscriptCardRendererProgressivelyDisclosesMessageActions()
{
    RunStaTest(() =>
    {
        var renderer = CreateTranscriptCardRendererForTest();
        var card = renderer.CreateCard(
            TranscriptForTest(12, "Alpha", "alpha", "message", "ok"),
            retryable: true,
            searchMatch: false,
            isLatest: false);
        var disclosures = LogicalDescendants<Expander>(card)
            .Where(expander => AutomationProperties.GetName(expander) == "Message actions")
            .ToArray();

        Require(disclosures.Length == 1, "each transcript card should expose one stable message-actions disclosure");
        Require(!disclosures[0].IsExpanded, "message actions should start collapsed to keep the reading path quiet");
        Require(disclosures[0].Header is Border { MinHeight: >= 32 }, "the action disclosure header should retain a compact desktop pointer target");
        Require(disclosures[0].Content is Border { Child: WrapPanel actions } && actions.Children.Count >= 5,
            "expanding message actions should retain copy, speech, pin, retry, and delete controls");
        Require(AutomationProperties.GetHelpText(disclosures[0]).Contains("reveal", StringComparison.OrdinalIgnoreCase),
            "assistive technology should explain what the message-actions disclosure reveals");
    });

    static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in LogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

static TranscriptCardRenderer CreateTranscriptCardRendererForTest()
{
    var actionCoordinator = new TranscriptActionCoordinator(() => false, () => false, AccentResourceBrush);
    return new TranscriptCardRenderer(
        () => false,
        actionCoordinator,
        AccentResourceBrush,
        ShellUiHelpers.BlendBrush,
        _ => AccentResourceBrush("AlphaAccentBrush"),
        _ => "persona",
        () => "default",
        () => false,
        () => true,
        () => false,
        (_, _) => new VoiceAdherenceDiagnostic("", "", "none", 0, "", [], []),
        _ => AccentResourceBrush("MutedTextBrush"),
        _ => "0 ms",
        value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => { },
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => { },
        speakerId => !string.IsNullOrWhiteSpace(speakerId) && !speakerId.Equals("operator", StringComparison.OrdinalIgnoreCase),
        () => false,
        _ => false,
        _ => false,
        _ => { },
        _ => false,
        _ => { },
        () => false);
}

static void TranscriptMutationCoordinatorFormatsStatuses()
{
    var pinned = TranscriptForTest(12, "Alpha", "alpha", "message", "ok") with { Pinned = true };
    var unpinned = pinned with { Pinned = false };
    var session = new SessionSummary("session", "", true, 0, 0, 0, DateTimeOffset.UtcNow);

    Require(TranscriptMutationCoordinator.DeleteStatus(pinned) == "Deleted turn 12.", "delete status should remain stable");
    Require(TranscriptMutationCoordinator.PinStatus(pinned) == "Unpinned turn 12.", "pinned status should describe unpinning");
    Require(TranscriptMutationCoordinator.PinStatus(unpinned) == "Pinned turn 12.", "unpinned status should describe pinning");
    Require(TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: false, session), "valid message should be mutable");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: true, session), "busy arena should block transcript mutation");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: false, activeSession: null), "missing session should block transcript mutation");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned with { Turn = 0 }, arenaBusy: false, session), "non-positive turn should block transcript mutation");
}

static void ArenaRunCoordinatorFormatsStatuses()
{
    var message = CoreMessageForTest(5, "Alpha", "alpha", "message", "ok", "model-a", 321);
    var completed = OneTurnResult.Completed(
        new OneTurnPlan(true, "alpha", "Alpha", null, null, ""),
        message,
        new ModelCompletionResult(true, "", "model-a", "text", "", 321, 0, 0, 0, "", DateTimeOffset.UtcNow));
    var agent = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "model-a", true, false, []);
    var original = TranscriptForTest(5, "Alpha", "alpha", "message", "ok");

    Require(ArenaRunCoordinator.AutoChatStatus(completed) == "Auto Chat: Alpha spoke (model-a, 321 ms)", "auto-chat success status should remain stable");
    Require(ArenaRunCoordinator.OneTurnStatus(completed) == "1 TURN complete: Alpha (model-a, 321 ms)", "one-turn success status should remain stable");
    Require(ArenaRunCoordinator.AgentTurnStatus(agent, completed) == "Alpha one-shot complete: model-a, 321 ms", "agent turn success status should remain stable");
    Require(ArenaRunCoordinator.RetryStatus(original, completed) == "Retry replaced turn 5: Alpha (model-a, 321 ms)", "retry success status should remain stable");
    Require(ArenaRunCoordinator.NarratorStatus(NarratorResult.Completed(message)) == "Narrator added turn 5 (model-a, 321 ms)", "narrator success status should remain stable");
    Require(ArenaRunCoordinator.AutoChatStatus(OneTurnResult.Failed("offline")) == "Auto Chat stopped: offline", "auto-chat failure status should include error");
}

static void ArenaRunCoordinatorDrainsAutoChatOnStop()
{
    RunStaTest(ArenaRunCoordinatorDrainsAutoChatOnStopCore);
}

static void ArenaRunCoordinatorDrainsAutoChatOnStopCore()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-auto-chat-tests", Guid.NewGuid().ToString("N"));
    using var finalCleanupEntered = new ManualResetEventSlim();
    using var allowFinalCleanup = new ManualResetEventSlim();
    try
    {
        Directory.CreateDirectory(root);
        var store = new SessionStore(root);
        var eventLog = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        var modelClient = new CancellationBlockingModelClient();
        using var internet = new InternetToolService(eventLogStore: eventLog);
        var transcript = new TranscriptService();
        var turnRunner = new TurnRunnerService(modelClient, store, eventLog, transcript, internet);
        using var narrator = new NarratorService(modelClient, store, eventLog, transcript, internet);
        var operationLock = new SemaphoreSlim(1, 1);
        var busy = false;
        var coordinator = new ArenaRunCoordinator(
            turnRunner,
            narrator,
            operationLock,
            new Button(),
            new Button(),
            new Button(),
            () => new SessionSummary("default", "", true, 0, 0, 0, DateTimeOffset.UtcNow),
            () => busy,
            () => false,
            () => TimeSpan.FromSeconds(30),
            (value, _, _, _) =>
            {
                if (!value)
                {
                    finalCleanupEntered.Set();
                    Require(allowFinalCleanup.Wait(TimeSpan.FromSeconds(5)), "auto-chat final cleanup was never released");
                }

                busy = value;
            },
            async (_, _, action, _) => await action(),
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            speakerId => !speakerId.Equals("operator", StringComparison.OrdinalIgnoreCase));

        var running = coordinator.StartAutoChatAsync();
        Require(modelClient.Started.Task.Wait(TimeSpan.FromSeconds(5)), "auto chat did not enter the model request");
        var stopping = coordinator.StopAutoChatAsync();
        Require(finalCleanupEntered.Wait(TimeSpan.FromSeconds(5)), "auto chat did not enter final busy-state cleanup");
        Require(!stopping.IsCompleted, "stop completion must remain pending until final busy-state cleanup finishes");
        var stopRequestedDuringCleanup = coordinator.StopAutoChatAsync();
        Require(!stopRequestedDuringCleanup.IsCompleted, "a stop requested during final cleanup must join the same completion barrier");
        allowFinalCleanup.Set();
        Require(stopping.Wait(TimeSpan.FromSeconds(5)), "stop did not wait for auto chat cleanup");
        Require(stopRequestedDuringCleanup.Wait(TimeSpan.FromSeconds(5)), "late stop did not complete after auto chat cleanup");
        running.GetAwaiter().GetResult();

        Require(!coordinator.IsAutoChatRunning, "auto chat still reports running after stop completed");
        Require(!busy, "arena remained busy after auto chat drained");
        var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
        Require(loaded.Engine.Agents.All(agent => !agent.Status.Equals("thinking", StringComparison.OrdinalIgnoreCase)), "stopped auto chat left an agent thinking");
    }
    finally
    {
        allowFinalCleanup.Set();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ArenaRunCoordinatorSurvivesProviderAndCancellationFailures()
{
    RunStaTest(ArenaRunCoordinatorSurvivesProviderAndCancellationFailuresCore);
}

static void ArenaRunCoordinatorSurvivesProviderAndCancellationFailuresCore()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-auto-chat-failure-tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var store = new SessionStore(root);
        var eventLog = new EventLogStore(root);
        store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot()).GetAwaiter().GetResult();
        using var internet = new InternetToolService(eventLogStore: eventLog);
        var transcript = new TranscriptService();

        var providerFailureStatuses = new List<string>();
        var throwingRunner = new TurnRunnerService(
            new ThrowingCollaborateModelClient("simulated provider crash"),
            store,
            eventLog,
            transcript,
            internet);
        using (var narrator = new NarratorService(
            new ThrowingCollaborateModelClient("unused narrator provider"),
            store,
            eventLog,
            transcript,
            internet))
        {
            var coordinator = CreateArenaRunCoordinatorForFailureTest(
                throwingRunner,
                narrator,
                providerFailureStatuses.Add);
            coordinator.StartAutoChatAsync().GetAwaiter().GetResult();

            Require(!coordinator.IsAutoChatRunning, "provider failure left auto chat marked as running");
            Require(
                providerFailureStatuses.Any(status => status.Contains("simulated provider crash", StringComparison.Ordinal)),
                "provider failure should become a visible auto-chat status instead of escaping the UI handler");
        }

        var cancellationClient = new ThrowingCancellationModelClient();
        var cancellationRunner = new TurnRunnerService(cancellationClient, store, eventLog, transcript, internet);
        using (var narrator = new NarratorService(cancellationClient, store, eventLog, transcript, internet))
        {
            var cancellationStatuses = new List<string>();
            var coordinator = CreateArenaRunCoordinatorForFailureTest(
                cancellationRunner,
                narrator,
                cancellationStatuses.Add);
            var running = coordinator.StartAutoChatAsync();
            Require(cancellationClient.Started.Task.Wait(TimeSpan.FromSeconds(5)), "auto chat did not enter the cancellation test provider");

            Require(
                coordinator.StopAutoChatAsync().Wait(TimeSpan.FromSeconds(5)),
                "throwing cancellation callback prevented auto-chat shutdown from draining");
            running.GetAwaiter().GetResult();

            Require(!coordinator.IsAutoChatRunning, "throwing cancellation callback left auto chat running");
            Require(
                cancellationStatuses.Any(status => status == "Auto Chat stopped."),
                "throwing cancellation callback should still reach the normal stopped status");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static ArenaRunCoordinator CreateArenaRunCoordinatorForFailureTest(
    TurnRunnerService turnRunner,
    NarratorService narrator,
    Action<string> captureStatus)
{
    var busy = false;
    return new ArenaRunCoordinator(
        turnRunner,
        narrator,
        new SemaphoreSlim(1, 1),
        new Button(),
        new Button(),
        new Button(),
        () => new SessionSummary("default", "", true, 0, 0, 0, DateTimeOffset.UtcNow),
        () => busy,
        () => false,
        () => TimeSpan.FromSeconds(30),
        (value, status, _, _) =>
        {
            busy = value;
            captureStatus(status);
        },
        async (_, _, action, _) => await action(),
        _ => Task.CompletedTask,
        captureStatus,
        captureStatus,
        speakerId => !speakerId.Equals("operator", StringComparison.OrdinalIgnoreCase));
}

static void OperatorDraftReceiptsSummarizeRoutes()
{
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "model-a",
        providerLastError: "",
        turnIndex: 0,
        [
            TranscriptForTest(1, "Alpha", "alpha", "message", "ok")
        ],
        [
            new AgentState("alpha", "Alpha", "waiting", "", "analyst", "default", "", "model-a", true, false, []),
            new AgentState("beta", "Beta", "waiting", "", "critic", "default", "", "model-b", true, false, []),
            new AgentState("gamma", "Gamma", "waiting", "", "observer", "default", "", "model-c", false, false, [])
        ]);

    var privateAnalysis = OperatorTurnCoordinator.AnalyzeOperatorDraft("PRIVATE", "Focus\n  the next answer", "all", snapshot);
    Require(privateAnalysis.Route == "private", "draft analysis should normalize route names");
    Require(privateAnalysis.MeterText == "23 chars / ~6 tok | Private memory -> 2 agents", "private draft meter should summarize target count");
    Require(privateAnalysis.Destination.Contains("2 active agents (alpha, beta)", StringComparison.Ordinal), "private destination should name active targets");

    var privateReceipt = OperatorTurnCoordinator.OperatorDraftReceiptLines(privateAnalysis, "Focus\n  the next answer");
    Require(privateReceipt[0] == "AI Arena Operator Draft", "operator receipt should expose a stable title");
    Require(privateReceipt.Any(line => line == "Route: Private memory"), "operator receipt should include route");
    Require(privateReceipt.Any(line => line == "Prompt: Focus the next answer"), "operator receipt should collapse prompt whitespace");
    Require(privateReceipt.Last().Contains("targeted agent response", StringComparison.Ordinal), "operator receipt should include route-aware next checks");

    var publicAnalysis = OperatorTurnCoordinator.AnalyzeOperatorDraft("unknown", "", "all", null);
    Require(publicAnalysis.Route == "public", "unknown routes should fall back to public");
    Require(publicAnalysis.MeterText == "0 chars / ~0 tok | Public transcript", "public draft meter should remain compact for empty drafts");
    Require(OperatorTurnCoordinator.OperatorVisibilitySummary("narrator").Contains("participant turn order", StringComparison.Ordinal), "narrator visibility should explain turn order impact");

    var suggestion = new OperatorInterventionSuggestion(
        "handoff_note",
        "Handoff",
        "public",
        "Create a handoff note before changing direction.",
        "Leave a durable breadcrumb.");
    var tooltip = OperatorTurnCoordinator.InterventionTooltip(suggestion);
    Require(tooltip.Contains("Intervention: Handoff (handoff_note)", StringComparison.Ordinal), "intervention tooltip should include intervention identity");
    Require(tooltip.Contains("Route: Public transcript", StringComparison.Ordinal), "intervention tooltip should include route");
    Require(tooltip.Contains("Next check:", StringComparison.Ordinal), "intervention tooltip should include next check");

    var hint = OperatorTurnCoordinator.OperatorQuickInterventionHint([
        suggestion,
        new OperatorInterventionSuggestion("role_reset", "Reset Roles", "private", "Reset", "Drift")
    ]);
    Require(hint == "Quick interventions: Handoff -> Public, Reset Roles -> Private.", "quick intervention hint should expose route destinations");
}

static void ArenaSessionMutationCoordinatorNormalizesSettings()
{
    Require(ArenaSessionMutationCoordinator.ClampTimeout(-5) == 1, "timeout should clamp low");
    Require(ArenaSessionMutationCoordinator.ClampTimeout(5000) == 3600, "timeout should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampTemperature(-0.5) == 0, "temperature should clamp low");
    Require(ArenaSessionMutationCoordinator.ClampTemperature(3) == 2, "temperature should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampMaxOutput(50000) == 32768, "max output should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampProviderContextLength(-1) == 0, "native context should allow provider default");
    Require(ArenaSessionMutationCoordinator.ClampProviderContextLength(2000000) == 1048576, "native context should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(-1) == 0, "native idle TTL should allow provider default");
    Require(ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(999999) == 86400, "native idle TTL should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampContextWindow(0) == 1, "transcript window should keep at least one turn");
    Require(ArenaSessionMutationCoordinator.ClampOptionalContextWindow(-2) == 0, "optional context windows should allow zero");

    var baseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["timeout"] = "300",
        ["temperature"] = "0.7",
        ["context"] = "20"
    };
    var unchanged = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
    var edited = new Dictionary<string, string>(baseline, StringComparer.Ordinal)
    {
        ["timeout"] = "600",
        ["context"] = "24"
    };
    Require(MainWindow.CountChangedSessionSettings(baseline, unchanged) == 0, "unchanged session fields should keep Apply disabled");
    Require(MainWindow.CountChangedSessionSettings(baseline, edited) == 2, "pending settings should report their exact changed-field count");
    var delta = MainWindow.SessionSettingsDelta(baseline, edited);
    Require(delta.Count == 2 && delta["timeout"] == "600" && delta["context"] == "24", "session drafts should store only changed fields so untouched values can refresh safely");
    Require(!delta.ContainsKey("temperature"), "session drafts should not overwrite untouched fields with stale values");

    var previousShared = new ModelProviderConfig { Model = "shared", Temperature = 0.7, MaxOutputTokens = 1024 };
    var updatedShared = new ModelProviderConfig { Model = "shared", Temperature = 0.5, MaxOutputTokens = 2048 };
    var configs = new Dictionary<string, ModelProviderConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["shared"] = updatedShared,
        ["alpha"] = new ModelProviderConfig { Model = "alpha-model", Temperature = 0.9, MaxOutputTokens = 1024 },
        ["beta"] = new ModelProviderConfig { Model = "beta-model", Temperature = 0.7, MaxOutputTokens = 1024 }
    };
    ArenaSessionMutationCoordinator.RefreshRoleInheritedGenerationDefaults(configs, "alpha", previousShared, updatedShared);
    ArenaSessionMutationCoordinator.RefreshRoleInheritedGenerationDefaults(configs, "beta", previousShared, updatedShared);
    Require(Math.Abs(configs["alpha"].Temperature - 0.9) < 0.0001 && configs["alpha"].MaxOutputTokens == 2048, "Apply should preserve an explicit role temperature while refreshing its inherited output limit");
    Require(Math.Abs(configs["beta"].Temperature - 0.5) < 0.0001 && configs["beta"].MaxOutputTokens == 2048, "Apply should propagate shared generation defaults without touching persisted role routing");
}

static void SessionOverviewCoordinatorFormatsSummaries()
{
    var alpha = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []);
    var beta = new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "beta-model", true, false, []);
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { PromptTokens = 120, CompletionTokens = 40 },
        TranscriptForTest(2, "Beta", "beta", "message", "ok") with { PromptTokens = 240, CompletionTokens = -5 }
    };
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "shared-model",
        providerLastError: "provider boom",
        turnIndex: 1,
        messages,
        [alpha, beta]);
    var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);

    Require(current?.Id == "beta", "current turn should wrap over active agents");
    Require(SessionOverviewCoordinator.CurrentTurnModel(snapshot, current) == "beta-model", "current agent model should win over shared model");
    Require(SessionOverviewCoordinator.CurrentTurnModel(snapshot, alpha) == "shared-model", "shared model should be used when current agent has no model");
    Require(SessionOverviewCoordinator.DisplayStatusValue(" alpha ") == "ALPHA", "status labels should trim and uppercase");
    Require(SessionOverviewCoordinator.TopRunStateSummary(snapshot, current, model => model) == "Ready: next BETA using beta-model; provider offline.", "top run summary should remain stable");
    Require(SessionOverviewCoordinator.ProviderSetupStatus(snapshot) == "provider boom", "provider setup should surface provider error");
    Require(SessionOverviewCoordinator.ProviderSetupStatus(snapshot with { ProviderOnline = true }) == "Provider is online. Choose a model, then run 1 TURN.", "online provider setup status should remain stable");
    Require(SessionOverviewCoordinator.ParticipantSummary(snapshot) == "2 agents + operator", "participant summary should count active agents");
    Require(SessionOverviewCoordinator.TotalCompletionTokens(snapshot) == 40, "completion token total should ignore negative values");
    Require(SessionOverviewCoordinator.MaxPromptContext(snapshot) == 240, "prompt context should use maximum prompt tokens");
}

static void ShellUiHelpersBlendBrushes()
{
    var blended = ShellUiHelpers.BlendBrush(
        new SolidColorBrush(Color.FromRgb(0, 0, 0)),
        new SolidColorBrush(Color.FromRgb(100, 200, 255)),
        0.5);
    var color = RequireSolidColor(blended, "blend helper should return a solid brush");

    Require(color.R == 50, "red channel should blend halfway");
    Require(color.G == 100, "green channel should blend halfway");
    Require(color.B == 128, "blue channel should round midpoint");

    var low = RequireSolidColor(ShellUiHelpers.BlendBrush(new SolidColorBrush(Color.FromRgb(10, 20, 30)), new SolidColorBrush(Color.FromRgb(200, 210, 220)), -1), "low clamp should return solid");
    var high = RequireSolidColor(ShellUiHelpers.BlendBrush(new SolidColorBrush(Color.FromRgb(10, 20, 30)), new SolidColorBrush(Color.FromRgb(200, 210, 220)), 2), "high clamp should return solid");
    Require(low == Color.FromRgb(10, 20, 30), "blend amount should clamp below zero");
    Require(high == Color.FromRgb(200, 210, 220), "blend amount should clamp above one");
}

static void DialogChromeCloseButtonExposesAutomationName()
{
    RunStaTest(() =>
    {
        var named = new Button { ToolTip = "Close settings" };
        var fallback = new Button();

        DialogChrome.ApplyCloseButtonStyle(named, Brushes.Black, Brushes.DimGray, Brushes.White);
        DialogChrome.ApplyCloseButtonStyle(fallback, Brushes.Black, Brushes.DimGray, Brushes.White);

        Require(named.Content?.ToString() == "\uE8BB", "close button should retain the close glyph");
        Require(AutomationProperties.GetName(named) == "Close settings", "close button should use tooltip text as its automation name");
        Require(AutomationProperties.GetName(fallback) == "Close", "close button should fall back to a close automation name");
    });
}

static void CustomDialogsPreserveModalAccessibilityAndResponsiveBounds()
{
    RunStaTest(() =>
    {
        var dialog = new Window
        {
            Width = 680,
            Height = 460,
            MinWidth = 300,
            MinHeight = 280
        };
        var focusScope = new Border();

        DialogChrome.ConfigureModalSurface(
            dialog,
            focusScope,
            "Edit agent memory",
            "Edit the stored memory text.");

        Require(FocusManager.GetIsFocusScope(focusScope), "custom dialogs should define an independent focus scope");
        Require(KeyboardNavigation.GetTabNavigation(focusScope) == KeyboardNavigationMode.Cycle, "Tab should cycle inside custom dialogs");
        Require(KeyboardNavigation.GetControlTabNavigation(focusScope) == KeyboardNavigationMode.Cycle, "Control+Tab should stay inside custom dialogs");
        Require(KeyboardNavigation.GetDirectionalNavigation(focusScope) == KeyboardNavigationMode.Contained, "directional navigation should stay inside custom dialogs");
        Require(AutomationProperties.GetName(dialog) == "Edit agent memory", "custom dialog windows should expose their purpose to automation");
        Require(AutomationProperties.GetHelpText(focusScope) == "Edit the stored memory text.", "custom dialog surfaces should expose task help text");

        DialogChrome.ApplyResponsiveBounds(dialog, 500, 420, 1920, 1080);

        Require(dialog.MaxWidth == 452 && dialog.Width == 452, "dialog width should fit inside a narrow owner with a balanced inset");
        Require(dialog.MaxHeight == 372 && dialog.Height == 372, "dialog height should fit inside a short owner with a balanced inset");
        Require(DialogChrome.ResponsiveMaximum(0, 900, 300) == 852, "dialog sizing should fall back to the work area before the owner is measured");

        dialog.Close();
    });

    foreach (var file in new[] { "AiChoicePromptDialog", "ConfirmDialog", "TextEditDialog" })
    {
        var source = File.ReadAllText(FindWorkspaceFile($"src/AIArena.Wpf/Shell/Dialogs/{file}.xaml.cs"));
        Require(source.Contains("DialogChrome.PrepareModalWindow(", StringComparison.Ordinal), $"{file} should use the shared modal focus and sizing contract");
    }

    var confirm = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/Dialogs/ConfirmDialog.xaml.cs"));
    Require(confirm.Contains("tone == ConfirmDialogTone.Danger ? CancelButton : ConfirmButton", StringComparison.Ordinal), "danger confirmations should initially focus the safe action");

    foreach (var file in new[] { "AiChoicePromptDialog", "TextEditDialog" })
    {
        var xaml = File.ReadAllText(FindWorkspaceFile($"src/AIArena.Wpf/Shell/Dialogs/{file}.xaml"));
        Require(xaml.Contains("PreviewKeyDown=", StringComparison.Ordinal), $"{file} should expose a keyboard submit path without breaking multiline Enter");
    }
}

static void AgentAccentServiceNormalizesColors()
{
    Require(AgentAccentService.NormalizeColor("35d6ff") == "#35D6FF", "accent color should accept hex without hash");
    Require(AgentAccentService.NormalizeColor("#ff8a6a") == "#FF8A6A", "accent color should normalize casing");
    Require(AgentAccentService.NormalizeColor("not-a-color") == "", "invalid accent color should be rejected");

    var custom = RequireSolidColor(
        AgentAccentService.ResolveBrush("alpha", "#123456", AccentResourceBrush),
        "custom accent should resolve to a solid brush");
    Require(custom == Color.FromRgb(0x12, 0x34, 0x56), "custom accent should override default color");

    var epsilon = RequireSolidColor(AgentAccentService.ResolveBrush("epsilon", "", AccentResourceBrush), "epsilon should resolve");
    var zeta = RequireSolidColor(AgentAccentService.ResolveBrush("zeta", "", AccentResourceBrush), "zeta should resolve");
    var eta = RequireSolidColor(AgentAccentService.ResolveBrush("eta", "", AccentResourceBrush), "eta should resolve");
    var theta = RequireSolidColor(AgentAccentService.ResolveBrush("theta", "", AccentResourceBrush), "theta should resolve");
    Require(new[] { epsilon, zeta, eta, theta }.Distinct().Count() == 4, "extended participants should have distinct default accents");
}

static void WindowChromeServicePacksColorRefs()
{
    Require(WindowChromeService.ColorRef(0x11, 0x22, 0x33) == 0x00332211, "COLORREF should pack bytes as 0x00bbggrr");
}

static void AppStartsInternetBackendLazily()
{
    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/App.xaml.cs"));
    var startupStart = source.IndexOf("protected override void OnStartup", StringComparison.Ordinal);
    var ensureMethodStart = source.IndexOf("internal Task<SearxngSupervisorStatus> EnsureInternetSearchAsync", StringComparison.Ordinal);

    Require(startupStart >= 0 && ensureMethodStart > startupStart, "app internet lifecycle methods should remain discoverable");
    var startupBody = source[startupStart..ensureMethodStart];
    Require(!startupBody.Contains("EnsureStartedAsync", StringComparison.Ordinal), "app startup must not eagerly launch the local search backend");
    Require(source.Contains("internal void StopInternetSearch()", StringComparison.Ordinal), "app should expose owned-backend stop to the internet toggle workflow");
    Require(source.Contains("searxngSupervisor?.Stop();", StringComparison.Ordinal), "app internet stop should target only its supervised backend");
}

static void SearxngSupervisorPlansBundledLaunch()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-supervisor", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        File.WriteAllText(
            Path.Combine(root, "searxng", "payload-manifest.txt"),
            "AI Arena bundled SearXNG payload\nSearXNG revision: abcdef123456\nPython: 3.11.9\n");

        Require(SearxngSupervisorService.BundledPayloadExists(root), "supervisor should detect a complete bundled payload");
        var gatewayPath = Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py");
        File.Delete(gatewayPath);
        Require(!SearxngSupervisorService.BundledPayloadExists(root), "supervisor should reject a payload that bypasses AI Arena's JSON-only boundary");
        File.WriteAllText(gatewayPath, "");
        Require(SearxngSupervisorService.ResolvePayloadVersion(root) == "abcdef123456", "supervisor should report the packaged SearXNG revision");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Require(SearxngSupervisorService.ResolvePayloadAppDirectory() == Path.GetFullPath(root), "development payload override should accept a direct payload root");
        Require(SearxngSupervisorService.ResolveBaseUri(null).AbsoluteUri == "http://localhost:8081/", "blank override should resolve to bundled default");
        Require(SearxngSupervisorService.ShouldUseBundledForBaseUrl(null, new Uri("http://localhost:8081/")), "blank override should allow bundled startup");
        Require(SearxngSupervisorService.ShouldUseBundledForBaseUrl("http://127.0.0.1:8081", new Uri("http://127.0.0.1:8081/")), "loopback bundled override should allow bundled startup");
        Require(!SearxngSupervisorService.ShouldUseBundledForBaseUrl("http://localhost:9999", new Uri("http://localhost:9999/")), "custom override should not start bundled SearXNG");
        Require(!SearxngSupervisorService.IsBundledDefaultUri(new Uri("https://localhost:8081/")), "HTTPS overrides must not launch the HTTP-only bundled child");
        Require(!SearxngSupervisorService.IsBundledDefaultUri(new Uri("http://localhost:8081/prefix/")), "path-prefixed overrides must not launch the root-bound bundled child");
        Require(!SearxngSupervisorService.IsBundledDefaultUri(new Uri("http://[::1]:8081/")), "IPv6 loopback must not launch the IPv4-only bundled child");
        Require(!SearxngSupervisorService.IsBundledDefaultUri(new Uri("http://127.0.0.2:8081/")), "other loopback addresses must not be treated as the fixed bundled bind address");
        foreach (var invalidRemoteBaseUrl in new[]
        {
            "https://search.example.test/api?tenant=arena",
            "https://search.example.test/api#operator-fragment"
        })
        {
            Require(
                !SearxngSupervisorService.TryResolveBaseUri(invalidRemoteBaseUrl, out var rejectedBaseUri, out var error),
                "supervisor should reject remote SearXNG query strings and fragments");
            Require(rejectedBaseUri == SearxngSupervisorService.BundledDefaultBaseUri, "a rejected supervisor override should retain the safe fallback value");
            Require(error.Contains("query string or fragment", StringComparison.OrdinalIgnoreCase), "supervisor and search client should report the same invalid URL component");
        }

        var startInfo = SearxngSupervisorService.CreateStartInfo(root);
        Require(startInfo.FileName.EndsWith(@"searxng\python\pythonw.exe", StringComparison.OrdinalIgnoreCase), "supervisor should launch bundled pythonw");
        Require(!startInfo.UseShellExecute && startInfo.CreateNoWindow, "supervisor should launch as an app-managed background process");
        Require(startInfo.WorkingDirectory.EndsWith(@"searxng\runtime", StringComparison.OrdinalIgnoreCase), "supervisor should run from bundled runtime");
        Require(startInfo.ArgumentList.Contains("granian"), "supervisor should launch granian");
        Require(startInfo.ArgumentList.Contains("arena_searxng_wsgi:application"), "supervisor should target AI Arena's JSON-only SearXNG boundary");
        Require(startInfo.Environment["SEARXNG_SETTINGS_PATH"]?.EndsWith(@"searxng\settings.yml", StringComparison.OrdinalIgnoreCase) == true, "supervisor should point SearXNG at bundled settings");
        Require(startInfo.Environment["PYTHONPATH"]?.Contains(@"searxng\runtime", StringComparison.OrdinalIgnoreCase) == true, "supervisor should include bundled runtime on PYTHONPATH");
        Require(startInfo.Environment["PYTHONDONTWRITEBYTECODE"] == "1", "supervisor should keep the verified payload free of runtime Python bytecode writes");
    }
    finally
    {
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void SearxngSupervisorCleansCanceledStartup()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-supervisor", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var launchedProcessId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));

        using var client = new HttpClient(new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("not ready")
            }));
        using var supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var process = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                launchedProcessId.TrySetResult(process.Id);
                return process;
            });
        using var cancellation = new CancellationTokenSource();
        var startup = supervisor.EnsureStartedAsync(cancellation.Token);
        Require(launchedProcessId.Task.Wait(TimeSpan.FromSeconds(5)), "supervisor did not launch the test search child");
        var processId = launchedProcessId.Task.GetAwaiter().GetResult();

        cancellation.Cancel();
        try
        {
            _ = startup.GetAwaiter().GetResult();
            throw new InvalidOperationException("canceled startup should propagate cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var exited = SpinWait.SpinUntil(() => !ProcessIsAlive(processId), TimeSpan.FromSeconds(5));
        Require(exited, "canceled startup left the bundled SearXNG child running");
    }
    finally
    {
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        if (launchedProcessId.Task.IsCompletedSuccessfully)
        {
            TryKillProcess(launchedProcessId.Task.Result);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static void TryKillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
    }
}

static void SearxngSupervisorStopIsStartupBarrier()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-stop-barrier", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    var firstLaunchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var allowFirstStartReturn = new ManualResetEventSlim();
    var launchedProcessIds = new List<int>();
    var processIdsGate = new object();
    SearxngSupervisorService? supervisor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", null);

        var launchCount = 0;
        using var client = new HttpClient(new TestHttpMessageHandler(_ =>
        {
            var status = Volatile.Read(ref launchCount) >= 2
                ? System.Net.HttpStatusCode.OK
                : System.Net.HttpStatusCode.ServiceUnavailable;
            return new HttpResponseMessage(status) { Content = new StringContent(status == System.Net.HttpStatusCode.OK ? "ok" : "down") };
        }));
        supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                var call = Interlocked.Increment(ref launchCount);
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var process = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                lock (processIdsGate)
                {
                    launchedProcessIds.Add(process.Id);
                }

                if (call == 1)
                {
                    firstLaunchEntered.TrySetResult(true);
                    Require(allowFirstStartReturn.Wait(TimeSpan.FromSeconds(5)), "first delayed start was never released");
                }

                return process;
            },
            _ => Task.FromResult(new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), "")));

        var activeSupervisor = supervisor;
        var first = Task.Run(() => activeSupervisor.EnsureStartedAsync());
        Require(firstLaunchEntered.Task.Wait(TimeSpan.FromSeconds(5)), "first start did not reach the delayed process factory");

        // Calling the async method directly runs through the generation capture and
        // queues at startupLock before returning this incomplete task.
        var queuedBeforeStop = activeSupervisor.EnsureStartedAsync();
        Require(!queuedBeforeStop.IsCompleted, "second pre-stop ensure should be queued behind the active startup");

        activeSupervisor.Stop();
        allowFirstStartReturn.Set();
        RequireCanceled(first, "active pre-stop ensure should be canceled");
        RequireCanceled(queuedBeforeStop, "queued pre-stop ensure should be rejected by the stop generation barrier");
        Require(Volatile.Read(ref launchCount) == 1, "a queued pre-stop ensure launched a new child after Stop returned");

        int firstProcessId;
        lock (processIdsGate)
        {
            firstProcessId = launchedProcessIds[0];
        }
        Require(SpinWait.SpinUntil(() => !ProcessIsAlive(firstProcessId), TimeSpan.FromSeconds(5)), "the active pre-stop child was not cleaned up");

        var postStop = activeSupervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(postStop.Started && Volatile.Read(ref launchCount) == 2, "an ensure genuinely started after Stop should be allowed to launch");
        activeSupervisor.Stop();

        int secondProcessId;
        lock (processIdsGate)
        {
            secondProcessId = launchedProcessIds[1];
        }
        Require(!ProcessIsAlive(secondProcessId), "Stop should wait until the post-stop owned child has exited");
    }
    finally
    {
        allowFirstStartReturn.Set();
        supervisor?.Dispose();
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
        lock (processIdsGate)
        {
            foreach (var processId in launchedProcessIds)
            {
                TryKillProcess(processId);
            }
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void RequireCanceled(Task task, string message)
    {
        try
        {
            task.GetAwaiter().GetResult();
            throw new InvalidOperationException(message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static void TryKillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
    }
}

static void SearxngSupervisorDisposalCleansDelayedStart()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-supervisor", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    var launchedProcessId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var allowStartReturn = new ManualResetEventSlim();
    SearxngSupervisorService? supervisor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", null);

        using var client = new HttpClient(new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("not ready")
            }));
        var activeSupervisor = supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var process = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                launchedProcessId.TrySetResult(process.Id);
                allowStartReturn.Wait(TimeSpan.FromSeconds(5));
                return process;
            },
            _ => Task.FromResult(new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), "")));

        var startup = Task.Run(() => activeSupervisor.EnsureStartedAsync());
        Require(launchedProcessId.Task.Wait(TimeSpan.FromSeconds(5)), "supervisor did not enter the delayed process start");
        var processId = launchedProcessId.Task.GetAwaiter().GetResult();

        activeSupervisor.Dispose();
        allowStartReturn.Set();
        Require(SpinWait.SpinUntil(() => startup.IsCompleted, TimeSpan.FromSeconds(5)), "disposed delayed startup did not unwind");
        try
        {
            _ = startup.GetAwaiter().GetResult();
            throw new InvalidOperationException("disposed delayed startup should not report success");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        Require(SpinWait.SpinUntil(() => !ProcessIsAlive(processId), TimeSpan.FromSeconds(5)), "a process returned after supervisor disposal was left running");
        try
        {
            _ = activeSupervisor.EnsureStartedAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("a disposed supervisor should reject a new startup");
        }
        catch (ObjectDisposedException)
        {
        }
    }
    finally
    {
        allowStartReturn.Set();
        supervisor?.Dispose();
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
        if (launchedProcessId.Task.IsCompletedSuccessfully)
        {
            TryKillProcess(launchedProcessId.Task.Result);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static void TryKillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
    }
}

static void SearxngSupervisorReplacesUnhealthyOwnedChild()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-supervisor", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    var launchedProcessIds = new List<int>();
    SearxngSupervisorService? supervisor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", null);

        var healthCalls = 0;
        using var client = new HttpClient(new TestHttpMessageHandler(request =>
        {
            Require(request.RequestUri?.AbsolutePath == "/healthz", "supervisor readiness should use only the local health endpoint");
            var call = Interlocked.Increment(ref healthCalls);
            var status = call is 2 or 5
                ? System.Net.HttpStatusCode.OK
                : System.Net.HttpStatusCode.ServiceUnavailable;
            return new HttpResponseMessage(status) { Content = new StringContent(status == System.Net.HttpStatusCode.OK ? "ok" : "down") };
        }));
        var stopAttempts = 0;
        supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                if (launchedProcessIds.Count > 0)
                {
                    Require(!ProcessIsAlive(launchedProcessIds[0]), "replacement launch began before the unhealthy owned child had exited");
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var process = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                launchedProcessIds.Add(process.Id);
                return process;
            },
            _ => Task.FromResult(new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), "")),
            stopProcess: process =>
            {
                if (Interlocked.Increment(ref stopAttempts) == 1)
                {
                    return false;
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                    {
                        return false;
                    }
                }

                return process.HasExited;
            });

        var first = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(first.Started && !first.AlreadyRunning, "first healthy child should be reported as newly started");
        Require(launchedProcessIds.Count == 1 && ProcessIsAlive(launchedProcessIds[0]), "first supervised child should still be alive");

        var blockedReplacement = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(!blockedReplacement.Started && !blockedReplacement.AlreadyRunning, "an unconfirmed live child must block replacement startup");
        Require(launchedProcessIds.Count == 1, "failed stop confirmation must not launch a competing child");
        Require(ProcessIsAlive(launchedProcessIds[0]), "failed stop confirmation should retain the live owned child for retry");
        Require(Volatile.Read(ref stopAttempts) == 1, "unconfirmed stop should make exactly one bounded attempt");

        var replacement = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(replacement.Started && !replacement.AlreadyRunning, "an unhealthy owned child should be replaced, not reported ready");
        Require(launchedProcessIds.Count == 2, "unhealthy owned child should cause exactly one replacement launch");
        Require(Volatile.Read(ref stopAttempts) >= 2, "the retained owned process should be retried before replacement");
        Require(SpinWait.SpinUntil(() => !ProcessIsAlive(launchedProcessIds[0]), TimeSpan.FromSeconds(5)), "unhealthy owned child was not stopped before replacement");
        Require(ProcessIsAlive(launchedProcessIds[1]), "replacement child should remain supervised");

        supervisor.Stop();
        Require(SpinWait.SpinUntil(() => !ProcessIsAlive(launchedProcessIds[1]), TimeSpan.FromSeconds(5)), "explicit stop should terminate the replacement child");
    }
    finally
    {
        supervisor?.Dispose();
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
        foreach (var processId in launchedProcessIds)
        {
            TryKillProcess(processId);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static void TryKillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
    }
}

static void InternetDiagnosticsReportSearchMetadata()
{
    var requests = new List<Uri>();
    using var client = new HttpClient(new TestHttpMessageHandler(request =>
    {
        if (request.RequestUri is not null)
        {
            requests.Add(request.RequestUri);
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"results":[{"title":"one","url":"https://one.example/","engine":"brave"},{"title":"two","url":"https://two.example/","engines":["bing","brave"]}],"unresponsive_engines":[["duckduckgo","timeout"]]}""")
        };
    }));
    using var supervisor = new SearxngSupervisorService(
        client,
        fetchDiagnosticAsync: _ => Task.FromResult(
            new InternetFetchDiagnostic(true, TimeSpan.FromMilliseconds(31), new Uri("https://example.com/"), "")));

    var report = supervisor.RunDiagnosticsAsync().GetAwaiter().GetResult();

    Require(report.Backend.AlreadyRunning, "diagnostics should use an already-serving local search backend");
    Require(report.Search.Ok, "a non-empty SearXNG result set should pass the search diagnostic");
    Require(report.Search.ResultCount == 2, "diagnostics should report the result count");
    Require(report.Search.ResponsiveEngineCount == 2, "diagnostics should count distinct engines that returned results");
    Require(report.Search.UnresponsiveEngineCount == 1, "diagnostics should report SearXNG's unresponsive engine count");
    Require(report.Search.Latency >= TimeSpan.Zero, "diagnostics should report search latency");
    Require(report.Fetch.Ok && report.Fetch.FinalUri?.Host == "example.com", "diagnostics should preserve the direct-fetch result");
    Require(requests.Count == 2, "diagnostics should perform one health check and one real search");
    Require(requests[0].AbsolutePath == "/healthz" && string.IsNullOrEmpty(requests[0].Query), "diagnostic readiness should use the side-effect-free health endpoint");
    Require(requests[1].AbsolutePath == "/search" && requests[1].Query.Contains("format=json", StringComparison.OrdinalIgnoreCase), "diagnostics should reserve a real JSON search for the explicit search test");
}

static void LiveBundledInternetDiagnostic()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var supervisor = new SearxngSupervisorService();
    var report = supervisor.RunDiagnosticsAsync(timeout.Token).GetAwaiter().GetResult();

    Require(report.Backend.PayloadFound, $"live diagnostic did not find the bundled payload: {report.Backend.Message}");
    Require(report.Backend.Started || report.Backend.AlreadyRunning, $"live diagnostic could not start local search: {report.Backend.Message}");
    Require(report.Search.Ok, $"live SearXNG search failed: {report.Search.Error}");
    Require(report.Search.ResultCount > 0, "live SearXNG search returned no results");
    Require(report.Fetch.Ok, $"live hardened public-page fetch failed: {report.Fetch.Error}");
    Require(report.Fetch.FinalUri?.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase) == true, "live fetch did not finish at example.com");

    Console.WriteLine(
        $"LIVE INTERNET search={report.Search.ResultCount} results/{report.Search.Latency.TotalMilliseconds:0}ms " +
        $"fetch={report.Fetch.Latency.TotalMilliseconds:0}ms engines={report.Search.ResponsiveEngineCount?.ToString() ?? "n/a"} " +
        $"unresponsive={report.Search.UnresponsiveEngineCount?.ToString() ?? "n/a"} payload={report.Backend.PayloadVersion}");
}

static void InternetDiagnosticsSupersedeOlderRuns()
{
    var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var successful = new InternetDiagnosticsReport(
        new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready"),
        new InternetSearchDiagnostic(true, TimeSpan.FromMilliseconds(15), 2, 1, 0, ""),
        new InternetFetchDiagnostic(true, TimeSpan.FromMilliseconds(10), new Uri("https://example.com/"), ""));
    using var runner = new LatestInternetDiagnosticsRunner(async cancellationToken =>
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            firstStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                firstCancelled.TrySetResult(true);
                throw;
            }
        }

        return successful;
    });

    var firstRun = runner.RunAsync();
    Require(firstStarted.Task.Wait(TimeSpan.FromSeconds(2)), "the first diagnostic should start");
    var latest = runner.RunAsync().GetAwaiter().GetResult();
    var superseded = firstRun.GetAwaiter().GetResult();

    Require(firstCancelled.Task.Wait(TimeSpan.FromSeconds(2)), "a newer diagnostic should cancel the older run");
    Require(superseded is null, "a superseded diagnostic must not publish stale results");
    Require(ReferenceEquals(latest, successful), "the newest diagnostic result should be published");
}

static void InternetDiagnosticsKeepCancellationAliveWhileSupersededRunsUnwind()
{
    var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowTokenProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tokenProbe = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var successful = new InternetDiagnosticsReport(
        new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready"),
        new InternetSearchDiagnostic(true, TimeSpan.Zero, 1, 1, 0, ""),
        new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), ""));
    using var runner = new LatestInternetDiagnosticsRunner(async cancellationToken =>
    {
        if (Interlocked.Increment(ref calls) != 1)
        {
            return successful;
        }

        firstStarted.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            firstCanceled.TrySetResult(true);
            await allowTokenProbe.Task;
            try
            {
                _ = cancellationToken.WaitHandle;
                using var registration = cancellationToken.Register(static () => { });
                tokenProbe.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tokenProbe.TrySetResult(ex);
            }

            throw;
        }

        throw new InvalidOperationException("The first diagnostic should be canceled.");
    });

    var firstRun = runner.RunAsync();
    Require(firstStarted.Task.Wait(TimeSpan.FromSeconds(2)), "the first lifetime diagnostic should start");
    var latest = runner.RunAsync().GetAwaiter().GetResult();
    Require(firstCanceled.Task.Wait(TimeSpan.FromSeconds(2)), "the superseded diagnostic should observe cancellation");
    allowTokenProbe.TrySetResult(true);
    var superseded = firstRun.GetAwaiter().GetResult();

    Require(superseded is null, "the lifetime probe run should remain superseded");
    Require(ReferenceEquals(latest, successful), "the latest lifetime probe result should win");
    Require(tokenProbe.Task.Wait(TimeSpan.FromSeconds(2)), "the canceled diagnostic did not probe its token lifetime");
    Require(tokenProbe.Task.Result is null, $"the token source was disposed before its run unwound: {tokenProbe.Task.Result?.GetType().Name}");
}

static void InternetDiagnosticsKeepCancellationAliveWhileDisposedRunsUnwind()
{
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowTokenProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tokenProbe = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new LatestInternetDiagnosticsRunner(async cancellationToken =>
    {
        started.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled.TrySetResult(true);
            await allowTokenProbe.Task;
            try
            {
                _ = cancellationToken.WaitHandle;
                using var registration = cancellationToken.Register(static () => { });
                tokenProbe.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tokenProbe.TrySetResult(ex);
            }

            throw;
        }

        throw new InvalidOperationException("The disposed diagnostic should be canceled.");
    });

    var run = runner.RunAsync();
    Require(started.Task.Wait(TimeSpan.FromSeconds(2)), "the disposal lifetime diagnostic should start");
    runner.Dispose();
    Require(canceled.Task.Wait(TimeSpan.FromSeconds(2)), "disposing the runner should cancel its active diagnostic");
    allowTokenProbe.TrySetResult(true);
    var result = run.GetAwaiter().GetResult();

    Require(result is null, "a diagnostic canceled by disposal must not publish a result");
    Require(tokenProbe.Task.Wait(TimeSpan.FromSeconds(2)), "the disposed diagnostic did not probe its token lifetime");
    Require(tokenProbe.Task.Result is null, $"runner disposal invalidated the token before its run unwound: {tokenProbe.Task.Result?.GetType().Name}");
}

static void InternetDiagnosticsSurviveCancellationCallbackFailures()
{
    var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var successful = new InternetDiagnosticsReport(
        new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready"),
        new InternetSearchDiagnostic(true, TimeSpan.Zero, 1, 1, 0, ""),
        new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), ""));
    using var runner = new LatestInternetDiagnosticsRunner(async cancellationToken =>
    {
        if (Interlocked.Increment(ref calls) != 1)
        {
            return successful;
        }

        using var registration = cancellationToken.Register(
            static () => throw new InvalidOperationException("simulated cancellation callback failure"));
        firstStarted.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The first callback-failure diagnostic should be canceled.");
    });

    var firstRun = runner.RunAsync();
    Require(firstStarted.Task.Wait(TimeSpan.FromSeconds(2)), "the callback-failure diagnostic should start");
    var latest = runner.RunAsync().GetAwaiter().GetResult();
    var superseded = firstRun.GetAwaiter().GetResult();

    Require(superseded is null, "the callback-failure diagnostic should remain superseded");
    Require(ReferenceEquals(latest, successful), "a failing cancellation callback must not block the latest diagnostic");
}

static void InternetBackendHealthKeepsCancellationAliveWhileSupersededRefreshesUnwind()
{
    RunStaTest(() =>
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTokenProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenProbe = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var ready = new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready");
        using var coordinator = new InternetWorkflowCoordinator(
            new CheckBox { IsChecked = true },
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            ensureBackendAsync: async cancellationToken =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                {
                    return ready;
                }

                firstStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    firstCanceled.TrySetResult(true);
                    await allowTokenProbe.Task;
                    try
                    {
                        _ = cancellationToken.WaitHandle;
                        using var registration = cancellationToken.Register(static () => { });
                        tokenProbe.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tokenProbe.TrySetResult(ex);
                    }

                    throw;
                }

                throw new InvalidOperationException("The superseded backend refresh should be canceled.");
            });

        var firstRefresh = coordinator.RefreshBackendHealthAsync();
        Require(firstStarted.Task.Wait(TimeSpan.FromSeconds(2)), "the first backend health refresh should start");
        coordinator.RefreshBackendHealthAsync().GetAwaiter().GetResult();
        Require(firstCanceled.Task.Wait(TimeSpan.FromSeconds(2)), "the newer backend refresh should cancel the older one");
        allowTokenProbe.TrySetResult(true);
        firstRefresh.GetAwaiter().GetResult();

        Require(tokenProbe.Task.Wait(TimeSpan.FromSeconds(2)), "the canceled backend refresh did not probe its token lifetime");
        Require(tokenProbe.Task.Result is null, $"backend refresh disposed its token before unwinding: {tokenProbe.Task.Result?.GetType().Name}");
    });
}

static void InternetWorkflowStartsAndStopsBackendWithToggle()
{
    RunStaTest(() =>
    {
        var internetToggle = new CheckBox { IsChecked = false };
        var backendStatus = new TextBlock();
        var ensureCalls = 0;
        var stopCalls = 0;
        var persistedSettings = new List<bool>();
        var ready = new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready");
        using var coordinator = new InternetWorkflowCoordinator(
            internetToggle,
            new TextBlock(),
            backendStatus,
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            ensureBackendAsync: _ =>
            {
                Interlocked.Increment(ref ensureCalls);
                return Task.FromResult(ready);
            },
            stopBackend: () => Interlocked.Increment(ref stopCalls),
            persistInternetSettingAsync: (_, enabled, _) =>
            {
                persistedSettings.Add(enabled);
                return Task.CompletedTask;
            });

        coordinator.InitializeControls();
        Require(ensureCalls == 0, "initializing with Internet off must not start the local backend");
        Require(backendStatus.Text == "Local search: inactive", "disabled initialization should show an inactive backend");

        coordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with { InternetEnabled = true });
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref ensureCalls) == 1, TimeSpan.FromSeconds(2)), "enabling Internet should lazily ensure the backend");
        Require(ensureCalls == 1, "snapshot application should not duplicate the Checked-event health refresh");
        Require(stopCalls == 0, "enabling Internet must not stop the backend");
        Require(persistedSettings.Count == 0, "rendering a stored snapshot must not write the Internet setting back as a user edit");

        coordinator.ControlSetEnabledAsync(false).GetAwaiter().GetResult();
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref stopCalls) == 1, TimeSpan.FromSeconds(2)), "disabling Internet should stop the app-owned backend");
        Require(ensureCalls == 1, "disabling Internet must not run another backend startup check");
        Require(backendStatus.Text == "Local search: inactive", "disabling Internet should return backend status to inactive");
        Require(persistedSettings.SequenceEqual([false]), "unchecking Internet should immediately persist the disabled state");

        internetToggle.IsChecked = true;
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref ensureCalls) == 2, TimeSpan.FromSeconds(2)), "re-enabling Internet should restart backend readiness");
        Require(persistedSettings.SequenceEqual([false, true]), "checking Internet should immediately persist the enabled state");

        var failingToggle = new CheckBox { IsChecked = false };
        var failingHint = new TextBlock();
        using var failingCoordinator = new InternetWorkflowCoordinator(
            failingToggle,
            failingHint,
            new TextBlock(),
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            ensureBackendAsync: _ => Task.FromResult(ready),
            persistInternetSettingAsync: (_, _, _) => Task.FromException(new IOException("simulated save failure")));
        failingCoordinator.InitializeControls();
        failingCoordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with
        {
            SessionId = "failing-session",
            InternetEnabled = false
        });
        failingToggle.IsChecked = true;
        Require(failingToggle.IsChecked == false, "a failed direct-toggle save should revert to the last persisted state");
        Require(failingHint.Text.Contains("could not be saved", StringComparison.OrdinalIgnoreCase), "failed direct-toggle persistence should stay visible to the operator");
    });
}

static void InternetTogglePersistsSessionImmediately()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-internet-toggle-persistence", Guid.NewGuid().ToString("N"));
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Internet.UseInternet = false;
        sessionStore.SaveSnapshotAsync(snapshot, "direct-toggle").GetAwaiter().GetResult();

        var enabledSaved = InternetWorkflowCoordinator.PersistSessionSettingAsync(
            sessionStore,
            eventLogStore,
            "direct-toggle",
            enabled: true).GetAwaiter().GetResult();
        Require(enabledSaved, "direct Internet enable did not save");
        Require(sessionStore.LoadSnapshotAsync("direct-toggle").GetAwaiter().GetResult()!.Engine.Internet.UseInternet, "direct Internet enable was not stored in the Core snapshot");

        var disabledSaved = InternetWorkflowCoordinator.PersistSessionSettingAsync(
            sessionStore,
            eventLogStore,
            "direct-toggle",
            enabled: false).GetAwaiter().GetResult();
        Require(disabledSaved, "direct Internet disable did not save");
        Require(!sessionStore.LoadSnapshotAsync("direct-toggle").GetAwaiter().GetResult()!.Engine.Internet.UseInternet, "direct Internet disable was not stored in the Core snapshot");

        var eventAttempts = 0;
        var savedDespiteEventFailure = InternetWorkflowCoordinator.PersistSessionSettingAsync(
            sessionStore,
            eventLogStore,
            "direct-toggle",
            enabled: true,
            appendEventAsync: (_, _, _) =>
            {
                Interlocked.Increment(ref eventAttempts);
                return Task.FromException(new IOException("simulated event-log failure"));
            }).GetAwaiter().GetResult();
        Require(savedDespiteEventFailure, "a committed Internet snapshot should remain successful when auxiliary event logging fails");
        Require(eventAttempts == 1, "the Internet setting persistence path should attempt its audit event");
        Require(sessionStore.LoadSnapshotAsync("direct-toggle").GetAwaiter().GetResult()!.Engine.Internet.UseInternet, "event-log failure reverted the already committed Internet snapshot");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void InternetTogglePersistenceStaysWithOriginatingSession()
{
    RunStaTest(() =>
    {
        var internetToggle = new CheckBox { IsChecked = false };
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedSessions = new List<string>();
        var ready = new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready");
        using var coordinator = new InternetWorkflowCoordinator(
            internetToggle,
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            ensureBackendAsync: _ => Task.FromResult(ready),
            persistInternetSettingAsync: async (sessionId, _, cancellationToken) =>
            {
                observedSessions.Add(sessionId);
                if (sessionId.Equals("session-a", StringComparison.OrdinalIgnoreCase))
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                    firstFinished.TrySetResult(true);
                    return;
                }

                throw new IOException("simulated session-b save failure");
            });

        coordinator.InitializeControls();
        coordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with
        {
            SessionId = "session-a",
            InternetEnabled = false
        });
        internetToggle.IsChecked = true;
        Require(firstStarted.Task.Wait(TimeSpan.FromSeconds(2)), "session-a Internet save should remain pending for the switch test");

        coordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with
        {
            SessionId = "session-b",
            InternetEnabled = false
        });
        Require(internetToggle.IsChecked == false, "loading session-b must apply its Internet state while session-a persistence is pending");

        coordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with
        {
            SessionId = "session-a",
            InternetEnabled = false
        });
        Require(internetToggle.IsChecked == true, "returning to session-a should render its pending enabled value instead of session-b's state");

        coordinator.ApplySnapshot(SnapshotForOverviewTest(false, "", "", 0, [], []) with
        {
            SessionId = "session-b",
            InternetEnabled = false
        });
        Require(internetToggle.IsChecked == false, "switching away again should restore session-b's persisted Internet state");

        releaseFirst.TrySetResult(true);
        Require(firstFinished.Task.Wait(TimeSpan.FromSeconds(2)), "session-a persistence did not finish");
        internetToggle.IsChecked = true;

        Require(observedSessions.SequenceEqual(["session-a", "session-b"]), "each user toggle should persist against the session whose snapshot supplied the control state");
        Require(internetToggle.IsChecked == false, "session-b save failure should revert to session-b's persisted state, not session-a's completed value");
    });
}

static void InternetDiagnosticsCleanUpTemporaryBackendWhileOff()
{
    RunStaTest(() =>
    {
        var report = new InternetDiagnosticsReport(
            new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready"),
            new InternetSearchDiagnostic(true, TimeSpan.Zero, 1, 1, 0, ""),
            new InternetFetchDiagnostic(true, TimeSpan.Zero, new Uri("https://example.com/"), ""));

        var offStopCalls = 0;
        using (var offCoordinator = new InternetWorkflowCoordinator(
            new CheckBox { IsChecked = false },
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            runDiagnosticsAsync: _ => Task.FromResult(report),
            stopBackend: () => Interlocked.Increment(ref offStopCalls)))
        {
            offCoordinator.TestInternetAsync().GetAwaiter().GetResult();
        }

        Require(offStopCalls == 1, "an off-state diagnostic should stop its temporary backend after completion");

        var internetToggle = new CheckBox { IsChecked = false };
        var enabledStopCalls = 0;
        using var enabledCoordinator = new InternetWorkflowCoordinator(
            internetToggle,
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new TextBlock(),
            _ => Brushes.Black,
            runDiagnosticsAsync: _ =>
            {
                internetToggle.IsChecked = true;
                return Task.FromResult(report);
            },
            stopBackend: () => Interlocked.Increment(ref enabledStopCalls));

        enabledCoordinator.TestInternetAsync().GetAwaiter().GetResult();

        Require(enabledStopCalls == 0, "diagnostic cleanup must preserve the backend when Internet was enabled during the run");
    });
}

static void SearxngSupervisorRejectsStaleHealthAfterChildExit()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-stale-health", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    var childProcessId = 0;
    SearxngSupervisorService? supervisor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", null);

        var healthCalls = 0;
        using var client = new HttpClient(new TestHttpMessageHandler(_ =>
        {
            var call = Interlocked.Increment(ref healthCalls);
            if (call == 2)
            {
                using var child = System.Diagnostics.Process.GetProcessById(Volatile.Read(ref childProcessId));
                child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(5000), "test child did not exit during the stale health response");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") };
        }));
        supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var child = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                Volatile.Write(ref childProcessId, child.Id);
                return child;
            });

        var status = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(!status.Started && !status.AlreadyRunning, "a stale successful health response must not report a vanished backend as ready");
        Require(healthCalls >= 3, "owned-child exit after health success should force a fresh readiness probe");
        Require(status.Message.Contains("no search backend", StringComparison.OrdinalIgnoreCase), "stale health failure should explain that no backend remains");
    }
    finally
    {
        supervisor?.Dispose();
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
        if (Volatile.Read(ref childProcessId) != 0)
        {
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(childProcessId);
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void SearxngSupervisorRejectsStaleInitialHealthAfterOwnedExit()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-searxng-stale-initial-health", Guid.NewGuid().ToString("N"));
    var previousPayloadDirectory = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    var childProcessId = 0;
    var launchCount = 0;
    SearxngSupervisorService? supervisor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "searxng", "python"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "searx"));
        Directory.CreateDirectory(Path.Combine(root, "searxng", "runtime", "site-packages", "granian"));
        File.WriteAllText(Path.Combine(root, "searxng", "python", "pythonw.exe"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "searx", "webapp.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "arena_searxng_wsgi.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "runtime", "site-packages", "granian", "__init__.py"), "");
        File.WriteAllText(Path.Combine(root, "searxng", "settings.yml"), "");
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", Path.Combine(root, "searxng"));
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", null);

        var healthCalls = 0;
        using var client = new HttpClient(new TestHttpMessageHandler(_ =>
        {
            var call = Interlocked.Increment(ref healthCalls);
            if (call == 3)
            {
                using var child = System.Diagnostics.Process.GetProcessById(Volatile.Read(ref childProcessId));
                child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(5000), "test child did not exit during the initial stale health response");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") };
            }

            var status = call == 2
                ? System.Net.HttpStatusCode.OK
                : System.Net.HttpStatusCode.ServiceUnavailable;
            return new HttpResponseMessage(status) { Content = new StringContent(status == System.Net.HttpStatusCode.OK ? "ok" : "down") };
        }));
        supervisor = new SearxngSupervisorService(
            client,
            _ =>
            {
                if (Interlocked.Increment(ref launchCount) > 1)
                {
                    return null;
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
                var child = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("test child process did not start");
                Volatile.Write(ref childProcessId, child.Id);
                return child;
            });

        var started = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(started.Started && Volatile.Read(ref launchCount) == 1, "initial child should become supervised before the stale fast-path probe");

        var stale = supervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(!stale.Started && !stale.AlreadyRunning, "initial stale health must not report an exited owned child as ready");
        Require(healthCalls >= 4, "initial owned-child exit should force a fresh health probe");
        Require(Volatile.Read(ref launchCount) == 2, "a failed fresh probe should proceed to one replacement attempt");
    }
    finally
    {
        supervisor?.Dispose();
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR", previousPayloadDirectory);
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
        if (Volatile.Read(ref childProcessId) != 0)
        {
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(childProcessId);
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void SearxngSupervisorInitialHealthHonorsStopGeneration()
{
    using var probeEntered = new ManualResetEventSlim();
    using var allowProbeResponse = new ManualResetEventSlim();
    using var client = new HttpClient(new TestHttpMessageHandler(_ =>
    {
        probeEntered.Set();
        Require(allowProbeResponse.Wait(TimeSpan.FromSeconds(5)), "initial health probe was never released");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") };
    }));
    using var supervisor = new SearxngSupervisorService(client);
    var ensure = Task.Run(() => supervisor.EnsureStartedAsync());
    Require(probeEntered.Wait(TimeSpan.FromSeconds(5)), "initial health probe did not start");

    supervisor.Stop();
    allowProbeResponse.Set();
    try
    {
        _ = ensure.GetAwaiter().GetResult();
        throw new InvalidOperationException("a pre-stop initial health response should be rejected");
    }
    catch (OperationCanceledException)
    {
    }
}

static void SearxngSupervisorReportsBackendHealth()
{
    Uri? requested = null;
    var readyClient = new HttpClient(new TestHttpMessageHandler(request =>
    {
        requested = request.RequestUri;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok"}""")
        };
    }));

    var ready = SearxngSupervisorService.ProbeAsync(
        readyClient,
        configured: "http://localhost:8081",
        appDirectory: Path.Combine(Path.GetTempPath(), "ai-arena-missing-searxng"))
        .GetAwaiter()
        .GetResult();

    Require(ready.AlreadyRunning, "a healthy gateway should report the backend as ready");
    Require(!ready.Started, "health probe should never report that it started the backend");
    Require(ready.BaseUri.AbsoluteUri == "http://localhost:8081/", "health probe should normalize the configured base URL");
    Require(requested?.AbsolutePath == "/healthz", "health probe should call the bundled gateway health endpoint");
    Require(string.IsNullOrEmpty(requested?.Query), "health probe should not execute a search query");

    var offlineClient = new HttpClient(new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("down")
    }));
    var offline = SearxngSupervisorService.ProbeAsync(
        offlineClient,
        configured: "http://localhost:8081",
        appDirectory: Path.Combine(Path.GetTempPath(), "ai-arena-missing-searxng"))
        .GetAwaiter()
        .GetResult();

    Require(!offline.AlreadyRunning && !offline.Started, "unavailable backend should stay offline without start side effects");
    Require(!offline.PayloadFound, "missing payload should be reported");
    Require(offline.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase), "offline probe should explain unavailability");

    var invalid = SearxngSupervisorService.ProbeAsync(
        readyClient,
        configured: "http://search.example.com",
        appDirectory: Path.Combine(Path.GetTempPath(), "ai-arena-missing-searxng"))
        .GetAwaiter()
        .GetResult();
    Require(!invalid.AlreadyRunning && !invalid.Started, "an insecure remote override should not be probed");
    Require(invalid.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase), "invalid remote override should explain the HTTPS requirement");

    var previousBaseUrl = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
    try
    {
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", "https://search.example.test/");
        var launches = 0;
        using var remoteSupervisor = new SearxngSupervisorService(
            offlineClient,
            _ =>
            {
                Interlocked.Increment(ref launches);
                return null;
            });
        var remote = remoteSupervisor.EnsureStartedAsync().GetAwaiter().GetResult();
        Require(!remote.Started && !remote.AlreadyRunning, "an unavailable remote override should remain unavailable");
        Require(launches == 0, "an explicit remote override must never launch the bundled local child");
        Require(remote.BaseUri.AbsoluteUri == "https://search.example.test/", "remote override status should preserve the configured endpoint");
        Require(remote.Message.Contains("override", StringComparison.OrdinalIgnoreCase), "remote override outage should explain why bundled startup was skipped");
    }
    finally
    {
        Environment.SetEnvironmentVariable("AIARENA_SEARXNG_URL", previousBaseUrl);
    }
}

static void InternetWorkflowFormatsBackendHealth()
{
    var ready = new SearxngSupervisorStatus(false, true, true, new Uri("http://localhost:8081/"), "ready");
    var started = new SearxngSupervisorStatus(true, false, true, new Uri("http://127.0.0.1:8081/"), "started");
    var unavailable = new SearxngSupervisorStatus(false, false, true, new Uri("http://localhost:8081/"), "down");
    var missing = new SearxngSupervisorStatus(false, false, false, new Uri("http://localhost:8081/"), "missing");
    var remoteMissing = new SearxngSupervisorStatus(false, false, false, new Uri("https://search.example.test/"), "Configured SearXNG URL is unavailable; override is set.");

    Require(InternetWorkflowCoordinator.BackendStatusText(null, internetEnabled: false) == "Local search: inactive", "disabled backend text should be inactive");
    Require(InternetWorkflowCoordinator.BackendStatusText(ready, internetEnabled: true) == "Local search: ready (localhost:8081)", "ready backend text mismatch");
    Require(InternetWorkflowCoordinator.BackendStatusText(started, internetEnabled: true) == "Local search: ready (127.0.0.1:8081)", "started backend text mismatch");
    Require(InternetWorkflowCoordinator.BackendStatusText(unavailable, internetEnabled: true) == "Local search: unavailable (localhost:8081)", "unavailable backend text mismatch");
    Require(InternetWorkflowCoordinator.BackendStatusText(missing, internetEnabled: true) == "Local search: unavailable (not installed)", "missing payload backend text mismatch");
    Require(InternetWorkflowCoordinator.BackendStatusText(remoteMissing, internetEnabled: true) == "Local search: unavailable (search.example.test:443)", "remote override outage must identify its endpoint instead of blaming the optional local payload");
    Require(InternetWorkflowCoordinator.BackendStatusBrushKey(ready, internetEnabled: true) == "PrimaryBorderBrush", "ready backend should use healthy brush");
    Require(InternetWorkflowCoordinator.BackendStatusBrushKey(unavailable, internetEnabled: true) == "DangerTextBrush", "offline backend should use danger brush");
    Require(InternetWorkflowCoordinator.BackendStatusBrushKey(ready, internetEnabled: false) == "MutedTextBrush", "disabled backend should use muted brush");
}

static void InternetWorkflowFormatsActionableDiagnostics()
{
    var report = new InternetDiagnosticsReport(
        new SearxngSupervisorStatus(
            false,
            true,
            true,
            new Uri("http://localhost:8081/"),
            "ready",
            @"C:\AI Arena\searxng",
            "abcdef123456"),
        new InternetSearchDiagnostic(true, TimeSpan.FromMilliseconds(420), 7, 3, 1, ""),
        new InternetFetchDiagnostic(true, TimeSpan.FromMilliseconds(1200), new Uri("https://example.com/"), ""));

    var text = InternetWorkflowCoordinator.DiagnosticResultText(report);
    Require(text.Contains("passed with engine warnings", StringComparison.OrdinalIgnoreCase), "unresponsive engines should be visible as a warning");
    Require(text.Contains("7 result(s) in 420 ms", StringComparison.Ordinal), "diagnostics should show result count and search latency");
    Require(text.Contains("3 responsive, 1 unresponsive", StringComparison.Ordinal), "diagnostics should show engine counts when SearXNG reports them");
    Require(text.Contains("passed in 1.2 s (example.com)", StringComparison.Ordinal), "diagnostics should show direct-fetch latency and destination");
    Require(text.Contains("revision abcdef123456", StringComparison.Ordinal), "diagnostics should show the payload version");
    Require(text.Contains(@"C:\AI Arena\searxng", StringComparison.Ordinal), "diagnostics should show the payload path");

    var failed = report with
    {
        Backend = report.Backend with { PayloadFound = false, Message = "Bundled SearXNG payload is not installed." },
        Search = new InternetSearchDiagnostic(false, TimeSpan.Zero, 0, null, null, "search unavailable"),
        Fetch = new InternetFetchDiagnostic(false, TimeSpan.FromMilliseconds(50), null, "TLS handshake failed")
    };
    var failedText = InternetWorkflowCoordinator.DiagnosticResultText(failed);
    Require(failedText.Contains("reinstall AI Arena", StringComparison.OrdinalIgnoreCase), "missing payload failures should give a repair action");
    Require(failedText.Contains("firewall", StringComparison.OrdinalIgnoreCase) && failedText.Contains("HTTPS/DNS", StringComparison.Ordinal), "fetch failures should give network repair actions");
    Require(InternetWorkflowCoordinator.DiagnosticStatusBrushKey(failed) == "DangerTextBrush", "failed diagnostics should use the danger brush");

    var remoteFailed = failed with
    {
        Backend = failed.Backend with
        {
            BaseUri = new Uri("https://search.example.test/"),
            Message = "Configured SearXNG URL is unavailable; bundled local search was not started because an override is set."
        }
    };
    var remoteFailedText = InternetWorkflowCoordinator.DiagnosticResultText(remoteFailed);
    Require(remoteFailedText.Contains("verify AIARENA_SEARXNG_URL", StringComparison.Ordinal), "remote override failures should recommend checking the configured endpoint");
    Require(!remoteFailedText.Contains("reinstall AI Arena", StringComparison.OrdinalIgnoreCase), "remote override failures must not recommend reinstalling an unrelated local payload");
    Require(remoteFailedText.Contains("Search payload: external endpoint", StringComparison.Ordinal), "remote diagnostics should describe the external endpoint instead of saying the local payload is missing");
}

static void ReleaseScriptsProtectInstallerDistributions()
{
    var installerScript = File.ReadAllText(FindWorkspaceFile("scripts/build-wpf-installer.ps1"));
    var releaseScript = File.ReadAllText(FindWorkspaceFile("scripts/build-wpf-release.ps1"));
    var previewScript = File.ReadAllText(FindWorkspaceFile("scripts/build-wpf-preview.ps1"));
    var sanityScript = File.ReadAllText(FindWorkspaceFile("scripts/wpf-release-sanity.ps1"));
    var payloadScript = File.ReadAllText(FindWorkspaceFile("scripts/build-searxng-payload.ps1"));
    var arenaSearchGateway = File.ReadAllText(FindWorkspaceFile("packaging/arena_searxng_wsgi.py"));
    var releaseSecurityScript = File.ReadAllText(FindWorkspaceFile("scripts/release-security.ps1"));
    var upstreamLock = File.ReadAllText(FindWorkspaceFile("packaging/upstream-lock.json"));
    var dependencyLock = File.ReadAllText(FindWorkspaceFile("packaging/searxng-requirements-lock.txt"));
    var innoScript = File.ReadAllText(FindWorkspaceFile("packaging/inno/ai-arena-wpf.iss"));

    Require(installerScript.Contains("Join-Path $distRoot \"installer\"", StringComparison.Ordinal) && installerScript.Contains("Join-Path $installerRoot \"AI Arena - $Version\"", StringComparison.Ordinal), "installer helper should target a versioned installer folder");
    Require(installerScript.Contains("Installer distribution already exists", StringComparison.Ordinal), "installer helper should reject existing installer folders");
    Require(installerScript.Contains("Refusing to overwrite existing installer artifact", StringComparison.Ordinal), "installer helper should refuse to overwrite copied release files");
    Require(installerScript.Contains("[switch]$ResumeFinalization", StringComparison.Ordinal), "installer helper should expose an explicit interrupted-build recovery mode");
    Require(installerScript.Contains("untouched post-compile installer directory", StringComparison.Ordinal), "installer recovery should refuse partially finalized or mutable distributions");
    Require(installerScript.Contains("Resume finalization requires an existing release directory and compiled installer", StringComparison.Ordinal), "installer recovery should require both immutable build products before finalizing");
    Require(installerScript.Contains("build-wpf-release.ps1", StringComparison.Ordinal), "installer helper should build the release payload first");
    Require(installerScript.Contains("Version = $Version", StringComparison.Ordinal), "installer helper should pass the release version by name");
    Require(installerScript.Contains("Configuration = $Configuration", StringComparison.Ordinal), "installer helper should pass the release configuration by name");
    Require(installerScript.Contains("Runtime = $Runtime", StringComparison.Ordinal), "installer helper should pass the release runtime by name");
    Require(releaseScript.Contains("[switch]$SelfContained = $true", StringComparison.Ordinal), "versioned releases should be self-contained by default");
    Require(installerScript.Contains("[switch]$SelfContained = $true", StringComparison.Ordinal) && installerScript.Contains("Installer distributions must be self-contained", StringComparison.Ordinal) && installerScript.Contains("SelfContained = $true", StringComparison.Ordinal), "installer distributions should require and forward a self-contained runtime");
    Require(releaseScript.Contains("Framework-dependent publish unexpectedly includes private .NET runtime files", StringComparison.Ordinal) && releaseScript.Contains("includedFrameworks", StringComparison.Ordinal), "release publishing should reject mixed private and framework-dependent runtime payloads");
    Require(previewScript.Contains("Assert-AIArenaPathWithinDirectory", StringComparison.Ordinal) && previewScript.Contains("Remove-Item -LiteralPath $output -Recurse -Force", StringComparison.Ordinal), "preview publishing should safely clear its fixed output before switching runtime modes");
    Require(releaseScript.Contains("[ValidateSet('win-x64')]", StringComparison.Ordinal) && installerScript.Contains("[ValidateSet('win-x64')]", StringComparison.Ordinal), "release and installer helpers should reject architectures that do not match the pinned Windows x64 search payload");
    Require(installerScript.Contains("wpf-release-sanity.ps1", StringComparison.Ordinal), "installer helper should run release sanity after packaging");
    Require(releaseScript.Contains("build-searxng-payload.ps1", StringComparison.Ordinal), "release builder should assemble the bundled SearXNG payload");
    Require(releaseScript.Contains("scripts\\ai-arena-control.ps1", StringComparison.Ordinal) && releaseScript.Contains("ai-arena-control.ps1\")", StringComparison.Ordinal), "release builder should ship the PowerShell control helper beside the app");
    Require(releaseScript.Contains("changelog.md", StringComparison.Ordinal) && releaseScript.Contains("github-release-notes.md", StringComparison.Ordinal), "release builder should emit markdown release artifacts");
    Require(releaseScript.Contains("packaging\\changes\\$Version.txt", StringComparison.Ordinal), "release builder should consume versioned packaged change notes by default");
    Require(releaseScript.Contains("release-checksums.sha256", StringComparison.Ordinal) && releaseScript.Contains("release-signing.json", StringComparison.Ordinal), "release builder should emit checksums and a signing report");
    Require(releaseScript.Contains("Resolve-AIArenaSigningConfiguration", StringComparison.Ordinal) && releaseScript.Contains("Invoke-AIArenaAuthenticodeSigning", StringComparison.Ordinal), "release builder should apply the shared Authenticode policy");
    Require(installerScript.Contains("SHA256SUMS.txt", StringComparison.Ordinal) && installerScript.Contains("installer-signing.json", StringComparison.Ordinal), "installer builder should emit distributable checksums and a signing report");
    Require(installerScript.Contains("SigningPolicy = $SigningPolicy", StringComparison.Ordinal), "installer helper should pass the signing policy into the release build");
    Require(releaseSecurityScript.Contains("Authenticode signing is required", StringComparison.Ordinal) && releaseSecurityScript.Contains("signtool.exe was not found", StringComparison.Ordinal), "required signing should fail closed when prerequisites are absent");
    Require(releaseSecurityScript.Contains("Assert-AIArenaTrustedExecutable", StringComparison.Ordinal) && installerScript.Contains("Inno Setup compiler", StringComparison.Ordinal), "release tooling should verify Authenticode on SignTool and Inno Setup before execution");
    Require(releaseSecurityScript.Contains("Test-AIArenaSha256Manifest", StringComparison.Ordinal), "release security helper should verify checksum manifests");
    Require(payloadScript.Contains("Save-VerifiedDownload", StringComparison.Ordinal) && payloadScript.Contains("Assert-FileHash", StringComparison.Ordinal), "payload downloads should be verified before extraction");
    Require(payloadScript.Contains("--require-hashes", StringComparison.Ordinal) && payloadScript.Contains("PYTHON-REQUIREMENTS-LOCK.txt", StringComparison.Ordinal), "payload Python dependencies should install only from the reviewed hash lock");
    Require(payloadScript.Contains("https://pypi.org/simple", StringComparison.Ordinal) && payloadScript.Contains("files.pythonhosted.org", StringComparison.Ordinal), "payload dependency installation should use and attest the official PyPI hosts");
    Require(payloadScript.Contains("Get-SafeArchiveTarget", StringComparison.Ordinal) && payloadScript.Contains("payload-inventory.json", StringComparison.Ordinal), "payload builder should reject archive traversal and emit a hashed inventory");
    Require(payloadScript.Contains("Refusing to mutate a finalized release directory", StringComparison.Ordinal) && payloadScript.Contains("release-checksums.sha256", StringComparison.Ordinal), "payload builder should not invalidate a finalized release checksum set");
    Require(payloadScript.Contains("max_request_timeout: 6.0", StringComparison.Ordinal) && payloadScript.Contains("pool_connections: 32", StringComparison.Ordinal) && payloadScript.Contains("pool_maxsize: 16", StringComparison.Ordinal), "Arena SearXNG profile should bound outgoing timeouts and connection pools");
    Require(payloadScript.Contains("formats:", StringComparison.Ordinal) && payloadScript.Contains("- json", StringComparison.Ordinal) && payloadScript.Contains("searx.settings['search']['formats'] == ['json']", StringComparison.Ordinal) && payloadScript.Contains("enable_metrics: false", StringComparison.Ordinal), "Arena SearXNG profile should expose only JSON without unnecessary local UI metrics");
    Require(payloadScript.Contains("arena_searxng_wsgi.py", StringComparison.Ordinal) && payloadScript.Contains("probe_gateway", StringComparison.Ordinal), "payload build should install and exercise AI Arena's SearXNG API boundary");
    Require(arenaSearchGateway.Contains("SPDX-License-Identifier: AGPL-3.0-or-later", StringComparison.Ordinal) && arenaSearchGateway.Contains("path == \"/healthz\"", StringComparison.Ordinal) && arenaSearchGateway.Contains("path != \"/search\"", StringComparison.Ordinal) && arenaSearchGateway.Contains("parameters.get(\"format\") != [\"json\"]", StringComparison.Ordinal), "AI Arena's SearXNG boundary should retain AGPL provenance, expose health, and reject non-search or non-JSON requests");
    Require(!payloadScript.Contains("keep_only", StringComparison.Ordinal), "Arena SearXNG profile should inherit upstream engines instead of maintaining a brittle keep_only list");
    Require(upstreamLock.Contains("009D6BF7E3B2DDCA3D784FA09F90FE54336D5B60F0E0F305C37F400BF83CFD3B", StringComparison.Ordinal) && upstreamLock.Contains("B2A9F9836C6A916E3B0D4235DFB8B766D96285987A87B1B90C9B2EC61D45D7E9", StringComparison.Ordinal), "upstream lock should pin the reviewed CPython and SearXNG archive hashes");
    Require(dependencyLock.Contains("granian==2.7.6 --hash=sha256:", StringComparison.Ordinal) && dependencyLock.Contains("httpx[http2]==0.28.1 --hash=sha256:", StringComparison.Ordinal), "Python dependency lock should pin package versions and wheel hashes");
    Require(sanityScript.Contains("installer changelog", StringComparison.Ordinal) && sanityScript.Contains("installer GitHub release notes", StringComparison.Ordinal), "release sanity should require installer-side release artifacts");
    Require(sanityScript.Contains("bundled SearXNG payload", StringComparison.Ordinal) && sanityScript.Contains("arena_searxng_wsgi.py", StringComparison.Ordinal), "release sanity should require the bundled SearXNG payload and JSON API boundary");
    Require(sanityScript.Contains("installed PowerShell control helper", StringComparison.Ordinal), "release sanity should require the installed PowerShell control helper");
    Require(sanityScript.Contains("Payload inventory SHA-256 mismatch", StringComparison.Ordinal) && sanityScript.Contains("Signing policy", StringComparison.Ordinal), "release sanity should verify payload hashes and signing policy");
    Require(sanityScript.Contains("Self-contained: True", StringComparison.Ordinal) && sanityScript.Contains("includedFrameworks", StringComparison.Ordinal) && sanityScript.Contains("hostfxr.dll", StringComparison.Ordinal) && sanityScript.Contains("System.Private.CoreLib.dll", StringComparison.Ordinal), "release sanity should reject installers without a coherent private .NET runtime");
    Require(innoScript.Contains("OutputDir=..\\..\\dist\\installer\\AI Arena - {#MyAppVersion}", StringComparison.Ordinal), "Inno output should remain versioned");
    Require(innoScript.Contains("DefaultDirName={localappdata}\\Programs\\{#MyAppName}", StringComparison.Ordinal), "installer binaries should remain separate from the LocalAppData AI Arena user-data root");
    Require(innoScript.Contains("Name: \"searxng\"; Description: \"Local web search engine (SearXNG, AGPL-3.0)\"", StringComparison.Ordinal), "Inno installer should expose the SearXNG component");
    Require(innoScript.Contains("SearxngLicensePage", StringComparison.Ordinal), "Inno installer should include the SearXNG AGPL gate");
    Require(innoScript.Contains("{param:SEARXNGLICENSE|}", StringComparison.Ordinal), "silent full installs should expose explicit SearXNG licence acknowledgement");
    Require(innoScript.Contains("CONTROLPLANE.md", StringComparison.Ordinal), "installer should place the authoritative PowerShell command reference beside the app");
    Require(innoScript.Contains("= 'accept'", StringComparison.Ordinal), "silent SearXNG licence acknowledgement should require the exact accept value");
    Require(innoScript.Contains("SW_SHOWNORMAL", StringComparison.Ordinal), "Inno cleanup should not spawn hidden PowerShell helpers");
    Require(!innoScript.Contains("SW_HIDE", StringComparison.Ordinal), "Inno cleanup should avoid hidden helper windows");
    Require(!innoScript.Contains("schtasks", StringComparison.OrdinalIgnoreCase), "Inno installer should not depend on the legacy scheduled-task lifecycle");
    Require(!innoScript.Contains("AI Arena SearXNG", StringComparison.OrdinalIgnoreCase), "Inno installer should not reference the legacy scheduled-task name");
    Require(innoScript.Contains("[UninstallDelete]", StringComparison.Ordinal), "Inno installer should define cleanup for runtime-only payload residue");
    Require(innoScript.Contains("Type: filesandordirs; Name: \"{app}\\searxng\"", StringComparison.Ordinal), "uninstall should remove the app-owned SearXNG payload directory");
    Require(innoScript.Contains("{app}\\searxng\\python\\pythonw.exe", StringComparison.Ordinal) && innoScript.Contains("ExecutablePath", StringComparison.Ordinal), "Inno uninstall cleanup should target only bundled SearXNG executables");
    Require(innoScript.Contains("EscapePowerShellSingleQuoted", StringComparison.Ordinal), "Inno uninstall cleanup should escape user-selected install paths");
    Require(!innoScript.Contains("ExecutionPolicy Bypass", StringComparison.OrdinalIgnoreCase), "Inno uninstall cleanup should not bypass PowerShell execution policy");
    Require(sanityScript.Contains("Installer should not depend on the legacy scheduled-task SearXNG lifecycle", StringComparison.Ordinal), "release sanity should guard against scheduled-task lifecycle returning");
}

static void AppIconResourceIsPackaged()
{
    var appIconBytes = PackagedResourceLength("assets/ai-arena-icon.ico");
    var guideIconBytes = PackagedResourceLength("assets/ai-arena-guide-icon.png");

    Require(appIconBytes > 0, "app icon ico should be packaged");
    Require(guideIconBytes > 0, "user guide compact header icon should be packaged");
    Require(guideIconBytes is > 0 and <= 10 * 1024, "user guide compact header icon should stay under 10 KB");
}

static long PackagedResourceLength(string resourceKey)
{
    var assembly = typeof(WindowChromeService).Assembly;
    using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.g.resources");
    if (stream is not null)
    {
        using var reader = new ResourceReader(stream);
        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is string key
                && key.Equals(resourceKey, StringComparison.OrdinalIgnoreCase)
                && entry.Value is Stream packagedStream)
            {
                return packagedStream.Length;
            }
        }
    }

    using var embedded = assembly.GetManifestResourceStream(resourceKey);
    return embedded?.Length ?? -1;
}

static void UserGuideAppIconImageSourceLoads()
{
    var icon = UserGuideWindowHost.CreateAppIconImageSource();

    Require(icon.Width > 0, "user guide app icon should have a width");
    Require(icon.Height > 0, "user guide app icon should have a height");
}

static void UserGuideHeaderIconImageSourceLoads()
{
    var icon = UserGuideWindowHost.CreateGuideHeaderIconImageSource();

    Require(icon.Width > 0, "user guide header icon should have a width");
    Require(icon.Height > 0, "user guide header icon should have a height");
    Require(icon.Width <= 64, "user guide header icon should stay compact");
    Require(icon.Height <= 64, "user guide header icon should stay compact");
}

static void UserGuideReadHandlesMissingFiles()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);

    try
    {
        var guidePath = Path.Combine(tempRoot, "USER_GUIDE.md");
        File.WriteAllText(guidePath, "# Guide\n\nA stable guide body.");

        Require(UserGuideWindowHost.TryReadGuideText(guidePath, out var guideText), "existing user guide should load");
        Require(guideText.Contains("stable guide", StringComparison.OrdinalIgnoreCase), "loaded guide text should be returned");

        File.Delete(guidePath);
        Require(!UserGuideWindowHost.TryReadGuideText(guidePath, out var missingText), "missing guide should be reported unavailable");
        Require(string.IsNullOrEmpty(missingText), "missing guide text should be empty");

        var blankGuidePath = Path.Combine(tempRoot, "BLANK_GUIDE.md");
        File.WriteAllText(blankGuidePath, "   \r\n\t");
        Require(!UserGuideWindowHost.TryReadGuideText(blankGuidePath, out _), "blank guide should be reported unavailable");
    }
    finally
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}

static void UserGuideSearchMatchesUnorderedTokens()
{
    var guideText = """
    # AI Arena User Guide

    Intro text.

    ## Provider Setup
    Choose a model endpoint before running an arena.

    ## Transcript Review
    Compare turns and copy run trace details.

    ## Session Templates
    Save reusable setups and restore checkpoints.
    """;

    var providerMatches = UserGuideWindowHost.DebugFilteredGuideSectionTitles(guideText, "model   provider");
    Require(providerMatches.SequenceEqual(["Provider Setup"]), "guide search should match repeated-space tokens across title and text");

    var transcriptMatches = UserGuideWindowHost.DebugFilteredGuideSectionTitles(guideText, "trace transcript");
    Require(transcriptMatches.SequenceEqual(["Transcript Review"]), "guide search should match unordered title/text tokens");

    var missingMatches = UserGuideWindowHost.DebugFilteredGuideSectionTitles(guideText, "provider checkpoint");
    Require(missingMatches.Count == 0, "guide search should require every query token to match one section");
}

static void UserGuideArticleHeaderTrimsLongTitles()
{
    RunStaTest(() =>
    {
        var (titleBounds, panelBounds) = UserGuideWindowHost.DebugMeasureContentHeaderTitle(
            380,
            "A Very Long User Guide Section Title That Should Stay Inside The Article Header Without Bleeding Across The Chrome");

        Require(titleBounds.Left >= 0, "user guide article title should stay inside the panel left edge");
        Require(titleBounds.Right <= panelBounds.Right - 20, "user guide article title should trim before the panel right edge");
        Require(titleBounds.Width < 280, "user guide article title should receive a finite title column width");
    });
}

static void ProviderReachabilityCoordinatorFormatsPopupState()
{
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "missing-model",
        providerLastError: "provider offline",
        turnIndex: 0,
        [],
        []);
    var state = ProviderHealthPopupState.From(
        snapshot,
        "fallback-url",
        "fallback-model",
        ["available-model"],
        lastProviderModelCount: -1,
        DateTimeOffset.UtcNow,
        null);

    Require(!state.Online, "offline snapshot should format as offline");
    Require(state.StatusText == "OFFLINE", "offline status label should remain stable");
    Require(state.ModelCountText == "1", "advertised model count should win");
    Require(state.DefaultModelText == "missing-model", "snapshot model should win over fallback text");
    Require(state.ErrorText == "Last error: provider offline", "provider error should be surfaced");
    Require(state.HasError, "provider error flag should be set");
    Require(state.HasMissingModelWarning, "missing advertised model warning should be set");
    Require(state.ModelWarningText == ProviderReachabilityCoordinator.ProviderModelWarning("missing-model"), "missing model warning text should remain stable");

    var recovered = ProviderHealthPopupState.From(
        snapshot with { ProviderOnline = true, ProviderLastError = "completion test required" },
        "fallback-url",
        "fallback-model",
        [],
        lastProviderModelCount: 2,
        DateTimeOffset.UtcNow,
        null);
    Require(recovered.Online, "recovered provider should format as online");
    Require(recovered.StatusText == "ONLINE", "recovered provider status label should be online");
    Require(recovered.ErrorText == "Provider note: completion test required", "online advisory should not be labeled as a last error");

    var fallback = ProviderHealthPopupState.From(null, "", "", [], -1, null, null);
    Require(fallback.BaseUrl == "-", "blank fallback base URL should become placeholder");
    Require(fallback.DefaultModelText == "not selected", "blank fallback model should become not selected");
    Require(fallback.ModelCountText == "unknown", "unknown model count should stay unknown");
    Require(fallback.LastCheckText == "waiting", "missing timestamps should show waiting");
    Require(!fallback.HasMissingModelWarning, "missing model warning should require advertised models");
}

static void ProviderReachabilityProjectsOneCoherentUiGeneration()
{
    var coordinatorSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ProviderReachabilityCoordinator.cs"));
    var updateMethod = CSharpMethodBlock(coordinatorSource, "private async Task UpdateActiveProviderStatusOnlyAsync(");
    Require(updateMethod.Contains("applyProviderStatusProjection(latest, snapshot)", StringComparison.Ordinal),
        "provider reachability should publish one projection instead of updating independent surfaces");
    Require(!updateMethod.Contains("updateTopBarStatus", StringComparison.Ordinal),
        "provider reachability should not retain a split top-bar-only update path");

    var windowSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var projection = CSharpMethodBlock(windowSource, "private void ApplyProviderStatusProjection(");
    var stateIndex = projection.IndexOf("_lastRenderedSnapshot = snapshot", StringComparison.Ordinal);
    var topIndex = projection.IndexOf("UpdateTopBarStatus(snapshot)", StringComparison.Ordinal);
    var overviewIndex = projection.IndexOf("SessionOverview.UpdateSessionOverview(snapshot)", StringComparison.Ordinal);
    var transcriptIndex = projection.IndexOf("PopulateTranscript(snapshot.Messages)", StringComparison.Ordinal);
    Require(stateIndex >= 0 && topIndex > stateIndex && overviewIndex > topIndex && transcriptIndex > overviewIndex,
        "the coherent provider projection should publish state before refreshing top, rail/settings, and transcript readiness in one dispatcher turn");
}

static void TruncateRespectsItsLimitAndSuffix()
{
    // Five separate copies of this helper disagreed on the suffix and on
    // whether the limit was a hard cap; one reserved a single character for a
    // three-character suffix and overshot.
    Require(ShellUiHelpers.Truncate("short", 20) == "short", "text within the limit should be untouched");
    Require(ShellUiHelpers.Truncate("", 5) == "", "an empty string should stay empty");
    Require(ShellUiHelpers.Truncate("exactlyten", 10) == "exactlyten", "text exactly at the limit should not be cut");

    var ellipsis = ShellUiHelpers.Truncate(new string('a', 50), 10);
    Require(ellipsis.Length == 10, $"the limit must be a hard cap, got {ellipsis.Length}");
    Require(ellipsis.EndsWith(ShellUiHelpers.EllipsisSuffix, StringComparison.Ordinal), "the default suffix should be the ellipsis character");

    var notice = ShellUiHelpers.Truncate(new string('b', 200), 40, ShellUiHelpers.TruncatedNoticeSuffix);
    Require(notice.Length == 40, $"the notice suffix must also respect the cap, got {notice.Length}");
    Require(notice.EndsWith(ShellUiHelpers.TruncatedNoticeSuffix, StringComparison.Ordinal), "captured output should say it was truncated");

    // A limit shorter than the suffix cannot carry one, and must still not overshoot.
    var tiny = ShellUiHelpers.Truncate("abcdefgh", 2, ShellUiHelpers.TruncatedNoticeSuffix);
    Require(tiny.Length <= 2, $"a limit below the suffix length must still cap, got {tiny.Length}");
}

static void SessionPickerHidesEmptySessionsWithoutLosingReach()
{
    static AIArena.Core.Models.SessionSummary Session(string id, int messages)
    {
        return new AIArena.Core.Models.SessionSummary(id, $"{id}\\snapshot.json", true, messages, 0, 0, DateTimeOffset.UnixEpoch);
    }

    // A shared data root fills with sessions that never received a turn.
    var sessions = new[]
    {
        Session("default", 0),
        Session("real-run", 12),
        Session("replay-a", 0),
        Session("replay-b", 0)
    };

    var visible = SavedStateWorkflowCoordinator.VisibleSessions(sessions, includeEmpty: false, selectedId: null);
    Require(visible.Count == 2, $"empty foreign sessions should be hidden, got {visible.Count}");
    Require(visible.Any(session => session.Id == "real-run"), "sessions with turns must stay listed");
    Require(visible.Any(session => session.Id == "default"), "the default session must stay reachable even when empty");
    Require(!visible.Any(session => session.Id.StartsWith("replay-", StringComparison.Ordinal)), "empty sessions should be filtered");

    // The selected session must never vanish from under the operator.
    var withSelection = SavedStateWorkflowCoordinator.VisibleSessions(sessions, includeEmpty: false, selectedId: "replay-b");
    Require(withSelection.Any(session => session.Id == "replay-b"), "the selected session must remain visible even when empty");

    var all = SavedStateWorkflowCoordinator.VisibleSessions(sessions, includeEmpty: true, selectedId: null);
    Require(all.Count == sessions.Length, "the toggle should restore every session");

    // If nothing has a transcript, showing an empty picker would be worse than showing all.
    var allEmpty = new[] { Session("replay-a", 0), Session("replay-b", 0) };
    Require(SavedStateWorkflowCoordinator.VisibleSessions(allEmpty, includeEmpty: false, selectedId: null).Count == 2,
        "a store with no transcripts should fall back to listing everything rather than nothing");

    var status = SavedStateWorkflowCoordinator.SessionListStatus(total: 830, visible: 2, includeEmpty: false);
    Require(status.Contains("828 empty hidden", StringComparison.Ordinal), $"status should report how many were hidden, got '{status}'");
    Require(!SavedStateWorkflowCoordinator.SessionListStatus(4, 4, includeEmpty: true).Contains("hidden", StringComparison.Ordinal),
        "status should not mention hiding when nothing is hidden");
}

static void SessionTokenAccountingReportsTotalsAndPressure()
{
    static TranscriptMessage Turn(int turn, int promptTokens, int completionTokens)
    {
        return new TranscriptMessage(turn, "Alpha", "alpha", 0, "-", 0, promptTokens, completionTokens, promptTokens + completionTokens, "ok", "", false, "message", "body", "", "", "", "", "", "", "", "", false, []);
    }

    var messages = new[] { Turn(1, 1000, 200), Turn(2, 3000, 400) };
    var snapshot = SnapshotForOverviewTest(true, "model", "", 2, messages, []) with { ProviderContextLength = 4000 };

    Require(SessionOverviewCoordinator.TotalCompletionTokens(snapshot) == 600, "generated tokens should sum completion counts only");
    Require(SessionOverviewCoordinator.TotalSessionTokens(snapshot) == 4600, "session total should include prompt and completion tokens");
    Require(SessionOverviewCoordinator.MaxPromptContext(snapshot) == 3000, "context should track the largest prompt");

    var pressure = SessionOverviewCoordinator.ContextPressure(snapshot);
    Require(pressure is not null && Math.Abs(pressure.Value - 0.75) < 0.001, "context pressure should be the largest prompt over the window");
    Require(SessionOverviewCoordinator.ContextPressureLabel(snapshot, value => value.ToString()).Contains("75%", StringComparison.Ordinal), "context label should surface the pressure percentage");

    // An unknown window must read as unknown rather than as no pressure.
    var unknownWindow = snapshot with { ProviderContextLength = 0 };
    Require(SessionOverviewCoordinator.ContextPressure(unknownWindow) is null, "an unset context window should report unknown pressure");
    Require(!SessionOverviewCoordinator.ContextPressureLabel(unknownWindow, value => value.ToString()).Contains("%", StringComparison.Ordinal), "an unknown window should not claim a percentage");

    // Pressure is capped so an over-limit prompt cannot exceed 100%.
    var overLimit = snapshot with { ProviderContextLength = 1000 };
    Require(SessionOverviewCoordinator.ContextPressure(overLimit) == 1.0, "pressure should cap at the full window");
    Require(SessionOverviewCoordinator.ContextPressure(overLimit) >= SessionOverviewCoordinator.ContextPressureWarningThreshold, "an over-limit prompt should cross the warning threshold");

    var empty = SnapshotForOverviewTest(true, "model", "", 0, [], []);
    Require(SessionOverviewCoordinator.TotalSessionTokens(empty) == 0, "an empty session should report no tokens");
    Require(SessionOverviewCoordinator.ContextPressureLabel(empty, value => value.ToString()) == "-", "an empty session should show a placeholder context");
}

static void CrossSessionSearchAttributesAndCapsHits()
{
    static TranscriptMessage Message(int turn, string speaker, string text)
    {
        return new TranscriptMessage(turn, speaker, speaker.ToLowerInvariant(), 0, "-", 0, 0, 0, 0, "ok", "", false, "message", text, "", "", "", "", "", "", "", "", false, []);
    }

    var session = new AIArena.Core.Models.SessionSummary("run-7", "path", true, 3, 0, 0, DateTimeOffset.UnixEpoch.AddDays(5));
    var messages = new[]
    {
        Message(1, "Alpha", "We should measure the latency threshold before shipping."),
        Message(2, "Beta", "Unrelated turn about persona drift."),
        Message(3, "Gamma", "The latency threshold is the wrong invariant.")
    };

    var hits = new List<CrossSessionSearchService.Hit>();
    CrossSessionSearchService.CollectSessionHits(session, messages, "latency threshold", 100, hits);
    Require(hits.Count == 2, $"both matching turns should be found, not {hits.Count}");
    Require(hits.All(hit => hit.SessionId == "run-7"), "hits should carry the session they came from");
    Require(hits[0].Turn == 1 && hits[1].Turn == 3, "hits should preserve turn attribution");
    Require(hits[0].Speaker == "Alpha", "hits should carry the speaker");

    // The cap bounds a broad query over a large history.
    var capped = new List<CrossSessionSearchService.Hit>();
    CrossSessionSearchService.CollectSessionHits(session, messages, "latency threshold", 1, capped);
    Require(capped.Count == 1, "hit collection should respect the cap");

    var nonMatching = new List<CrossSessionSearchService.Hit>();
    CrossSessionSearchService.CollectSessionHits(session, messages, "no such phrase", 100, nonMatching);
    Require(nonMatching.Count == 0, "a query with no matches should produce no hits");

    // Excerpts should centre on the match rather than always showing the opening words.
    var longText = new string('a', 200) + " latency threshold " + new string('b', 200);
    var excerpt = CrossSessionSearchService.Excerpt(longText, "latency threshold");
    Require(excerpt.Contains("latency threshold", StringComparison.OrdinalIgnoreCase), "excerpt should include the matched phrase");
    Require(excerpt.StartsWith("...", StringComparison.Ordinal) && excerpt.EndsWith("...", StringComparison.Ordinal), "a mid-text match should be shown as a windowed excerpt");
    Require(excerpt.Length < longText.Length, "excerpt should be shorter than the full turn");
    Require(CrossSessionSearchService.Excerpt("", "x") == "", "an empty turn should produce an empty excerpt");
}

static void IdlePollingCadenceOnlyWhenNothingIsRunning()
{
    Require(MainWindow.ShouldUseIdlePollingCadence(windowActive: false, arenaBusy: false, autoChatRunning: false), "a backgrounded idle shell should slow its polling");
    Require(!MainWindow.ShouldUseIdlePollingCadence(windowActive: true, arenaBusy: false, autoChatRunning: false), "a focused shell should keep the responsive cadence");
    Require(!MainWindow.ShouldUseIdlePollingCadence(windowActive: false, arenaBusy: true, autoChatRunning: false), "a background run must not lose its refresh cadence");
    Require(!MainWindow.ShouldUseIdlePollingCadence(windowActive: false, arenaBusy: false, autoChatRunning: true), "auto chat in the background must not lose its refresh cadence");
}

static void TranscriptRowSyncPreservesUnchangedTail()
{
    // Rows are newest-first, so a new turn is prepended and every later index
    // shifts. The sync must rewrite only the head, or the virtualizing panel
    // rebuilds every realized container on every turn.
    // Prepending one row should touch exactly one index.
    var collection = new ObservableCollection<object>(["adjunct-1", "turn-3", "turn-2", "turn-1"]);
    var changes = 0;
    collection.CollectionChanged += (_, _) => changes++;
    TranscriptListCoordinator.SyncRowsInto(collection, ["adjunct-1", "turn-4", "turn-3", "turn-2", "turn-1"]);
    Require(collection.SequenceEqual(["adjunct-1", "turn-4", "turn-3", "turn-2", "turn-1"]), "prepending a turn should produce the target rows");
    Require(changes == 1, $"prepending a turn should raise one collection change, not {changes}");

    // A rebuilt adjunct panel should replace only that row.
    collection = new ObservableCollection<object>(["adjunct-a", "turn-2", "turn-1"]);
    changes = 0;
    collection.CollectionChanged += (_, _) => changes++;
    TranscriptListCoordinator.SyncRowsInto(collection, ["adjunct-b", "turn-2", "turn-1"]);
    Require(collection.SequenceEqual(["adjunct-b", "turn-2", "turn-1"]), "replacing an adjunct should produce the target rows");
    Require(changes == 1, $"replacing an adjunct should raise one collection change, not {changes}");

    // Identical input should not touch the collection at all.
    collection = new ObservableCollection<object>(["adjunct", "turn-2", "turn-1"]);
    changes = 0;
    collection.CollectionChanged += (_, _) => changes++;
    TranscriptListCoordinator.SyncRowsInto(collection, ["adjunct", "turn-2", "turn-1"]);
    Require(changes == 0, "an unchanged transcript should not mutate the bound collection");

    // Shrinking (filters applied) and growing from empty must still be exact.
    collection = new ObservableCollection<object>(["a", "b", "c", "d"]);
    TranscriptListCoordinator.SyncRowsInto(collection, ["z", "d"]);
    Require(collection.SequenceEqual(["z", "d"]), "filtering down should produce the target rows");

    collection = [];
    TranscriptListCoordinator.SyncRowsInto(collection, ["a", "b", "c"]);
    Require(collection.SequenceEqual(["a", "b", "c"]), "growing from empty should produce the target rows");

    TranscriptListCoordinator.SyncRowsInto(collection, []);
    Require(collection.Count == 0, "clearing should produce an empty collection");
}

static void AdvertisedShortcutsAreHandled()
{
    // The search tooltip promised Ctrl+F for a long time while no handler
    // existed. Any shortcut named in the UI must be reachable from the shell
    // key handler, and every shortcut the handler implements must be listed.
    var handler = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var handlerStart = handler.IndexOf("private bool TryHandleShellShortcut", StringComparison.Ordinal);
    var handlerEnd = handler.IndexOf("private void ShowShortcutsOverlay", handlerStart, StringComparison.Ordinal);
    Require(handlerStart >= 0 && handlerEnd > handlerStart, "shell shortcut handler should remain discoverable");
    var handlerBody = handler[handlerStart..handlerEnd];

    var keyForShortcut = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Ctrl+F"] = "Key.F",
        ["Ctrl+K"] = "Key.K",
        ["Ctrl+M"] = "Key.M",
        ["Ctrl+Enter"] = "Key.Enter",
        ["Ctrl+E"] = "Key.E",
        ["Ctrl+,"] = "Key.OemComma",
        ["Ctrl+Shift+R"] = "Key.R",
        ["Ctrl+1"] = "Key.D1",
        ["Ctrl+2"] = "Key.D2",
        ["Ctrl+3"] = "Key.D3",
        ["F2"] = "Key.F2",
        ["F5"] = "Key.F5",
        ["F7"] = "Key.F7",
        ["F8"] = "Key.F8",
        ["F9"] = "Key.F9",
        ["F10"] = "Key.F10"
    };

    // WPF delivers F10 and every Alt combination as Key.System with the real
    // key in SystemKey, because those keys traditionally open a window menu.
    // Matching on Key alone made F10 do nothing at all.
    Require(MainWindow.EffectiveShortcutKey(Key.System, Key.F10) == Key.F10, "F10 arrives as a system key and must resolve to F10");
    Require(MainWindow.EffectiveShortcutKey(Key.F5, Key.None) == Key.F5, "ordinary keys must pass through unchanged");
    Require(handlerBody.Contains("EffectiveShortcutKey", StringComparison.Ordinal), "the shell key handler must resolve system keys before matching");
    Require(!handlerBody.Contains("switch (e.Key)", StringComparison.Ordinal), "matching on e.Key directly would lose F10 again");

    // F3 means "find next" on Windows. Transcript search filters rather than
    // stepping through matches, so binding it would fight muscle memory for no
    // gain; this keeps it reserved until there is something to step through.
    Require(!handlerBody.Contains("Key.F3", StringComparison.Ordinal), "F3 should stay unbound while search filters rather than stepping matches");

    // Destructive actions stay pointer-only: a stray function key must not be
    // able to wipe a run.
    Require(!handlerBody.Contains("ResetButton_Click", StringComparison.Ordinal), "Reset must not be reachable from a shell shortcut");

    foreach (var pair in keyForShortcut)
    {
        Require(
            handlerBody.Contains(pair.Value, StringComparison.Ordinal),
            $"{pair.Key} is advertised but the shell key handler has no {pair.Value} case");
        Require(
            MainWindow.ShellShortcuts.Any(shortcut => shortcut.Keys.Contains(pair.Key, StringComparison.Ordinal)),
            $"{pair.Key} is handled but missing from the shortcut list shown to users");
    }

    // Tooltips that name a chord must match one the shell actually handles.
    string[] markupFiles =
    [
        "src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml",
        "src/AIArena.Wpf/Shell/MainWindow.xaml"
    ];
    foreach (var relativePath in markupFiles)
    {
        var markup = File.ReadAllText(FindWorkspaceFile(relativePath));
        foreach (Match match in Regex.Matches(markup, @"\(Ctrl\+(?:Shift\+)?[A-Za-z,]+\)"))
        {
            var advertised = match.Value.Trim('(', ')');
            Require(
                MainWindow.ShellShortcuts.Any(shortcut => shortcut.Keys.Contains(advertised, StringComparison.Ordinal)),
                $"{relativePath} advertises {advertised}, which the shell does not implement");
        }
    }
}

static void StatusTonesAvoidAgentIdentityAccents()
{
    // Agent accent brushes are user-customizable identity colors. Using one to
    // mean "ready", "warning", or "online" makes recolouring an agent repaint
    // unrelated status, so status tones must come from the fixed palette.
    string[] identityBrushes =
    [
        "AlphaAccentBrush",
        "BetaAccentBrush",
        "GammaAccentBrush",
        "DeltaAccentBrush",
        "NarratorAccentBrush"
    ];

    string[] statusKeywords =
    [
        "ready",
        "warning",
        "danger",
        "success",
        "online",
        "offline",
        "saved",
        "healthy"
    ];

    string[] sources =
    [
        "src/AIArena.Wpf/Shell/ScenarioWorkflowCoordinator.cs",
        "src/AIArena.Wpf/Shell/TranscriptListCoordinator.cs",
        "src/AIArena.Wpf/Shell/ProviderQuickSetupCoordinator.cs",
        "src/AIArena.Wpf/Shell/DiagnosticsWorkflowCoordinator.cs",
        "src/AIArena.Wpf/Shell/AgentBoardCoordinator.cs",
        "src/AIArena.Wpf/Shell/MainWindow.xaml.cs"
    ];

    foreach (var relativePath in sources)
    {
        var lines = File.ReadAllLines(FindWorkspaceFile(relativePath));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!identityBrushes.Any(brush => line.Contains(brush, StringComparison.Ordinal)))
            {
                continue;
            }

            // A line that names an agent alongside its accent is identity, not status.
            if (line.Contains("accentForSpeaker", StringComparison.OrdinalIgnoreCase)
                || line.Contains("\"Alpha\"", StringComparison.Ordinal)
                || line.Contains("\"Beta\"", StringComparison.Ordinal)
                || line.Contains("\"Gamma\"", StringComparison.Ordinal)
                || line.Contains("\"Delta\"", StringComparison.Ordinal)
                || line.Contains("\"alpha\"", StringComparison.Ordinal)
                || line.Contains("\"beta\"", StringComparison.Ordinal)
                || line.Contains("\"gamma\"", StringComparison.Ordinal)
                || line.Contains("\"delta\"", StringComparison.Ordinal)
                || line.Contains("\"narrator\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var offending = statusKeywords.FirstOrDefault(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            Require(
                offending is null,
                $"{relativePath}:{index + 1} uses an agent identity accent for the '{offending}' status tone; use the Arena status palette instead");
        }
    }
}

static void ShellNavigationCoordinatorSelectsThemes()
{
    var themes = ThemePalette.BuiltIn.ToArray();

    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "dark-green") == "dark-green", "known theme should be selected");
    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "system") == "system", "system theme should remain selectable");
    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "missing") == "dark-blue", "unknown theme should fall back");
    Require(ThemePalette.NormalizeId("System") == "system", "system theme should survive settings normalization");

    var normalSystem = ThemePalette.ResolveSystem(highContrast: false);
    var highContrastSystem = ThemePalette.ResolveSystem(highContrast: true);
    var highContrast = themes.Single(theme => theme.Id == "high-contrast");
    Require(normalSystem.Id == "system" && normalSystem.Name == "System", "normal system theme should retain the persisted system identity");
    Require(highContrastSystem.Id == "system" && highContrastSystem.Text == highContrast.Text, "system theme should adopt the high-contrast palette when Windows requests it");
    Require(MainWindow.ShouldReapplySystemTheme("system", nameof(SystemParameters.HighContrast)), "system theme should react to Windows high-contrast changes");
    Require(!MainWindow.ShouldReapplySystemTheme("dark-blue", nameof(SystemParameters.HighContrast)), "explicit themes should ignore system contrast changes");
    Require(!MainWindow.ShouldReapplySystemTheme("system", nameof(SystemParameters.ClientAreaAnimation)), "unrelated Windows preferences should not rebuild the theme");

    foreach (var theme in themes)
    {
        Require(ThemePalette.ContrastRatio(theme.Text, theme.Input) >= 4.5, $"{theme.Name} primary text must meet WCAG AA contrast against input surfaces");
        Require(ThemePalette.ContrastRatio(theme.MutedText, theme.Input) >= 4.5, $"{theme.Name} secondary text must meet WCAG AA contrast against input surfaces");
        Require(ThemePalette.ContrastRatio(theme.Border, theme.Input) >= 3.0, $"{theme.Name} enabled control boundaries must reach 3:1 contrast against input surfaces");
        Require(theme.OperatorAccent != theme.DangerBorder, $"{theme.Name} should reserve danger red for destructive/error semantics instead of the selected public operator route");
    }

    var mainWindow = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var handlerStart = mainWindow.IndexOf("private void OnSystemThemePreferenceChanged", StringComparison.Ordinal);
    var handlerEnd = mainWindow.IndexOf("internal static bool ShouldReapplySystemTheme", handlerStart, StringComparison.Ordinal);
    Require(handlerStart >= 0 && handlerEnd > handlerStart, "system-theme preference handler should remain discoverable");
    var handler = mainWindow[handlerStart..handlerEnd];
    var dispatchIndex = handler.IndexOf("Dispatcher.BeginInvoke", StringComparison.Ordinal);
    var firstGuard = handler.IndexOf("ShouldReapplySystemTheme", StringComparison.Ordinal);
    var queuedGuard = handler.IndexOf("ShouldReapplySystemTheme", firstGuard + 1, StringComparison.Ordinal);
    Require(dispatchIndex >= 0 && queuedGuard > dispatchIndex, "queued system-theme refresh should recheck the current theme before applying");

    RunStaTest(() =>
    {
        var navigationButton = new Button();
        ShellNavigationCoordinator.ApplyNavigationButtonState(navigationButton, active: true, AccentResourceBrush);
        Require(AutomationProperties.GetItemStatus(navigationButton) == "current page", "active navigation buttons should expose the current-page state to automation");
        ShellNavigationCoordinator.ApplyNavigationButtonState(navigationButton, active: false, AccentResourceBrush);
        Require(AutomationProperties.GetItemStatus(navigationButton) == "not current page", "inactive navigation buttons should expose their state to automation");

        var settingsButton = new Button();
        ShellNavigationCoordinator.ApplyAppSettingsButtonState(settingsButton, visible: true);
        Require(AutomationProperties.GetName(settingsButton) == "Close app settings", "an open settings drawer should expose a close action");
        Require(AutomationProperties.GetItemStatus(settingsButton) == "expanded", "an open settings drawer should expose expanded state");
        ShellNavigationCoordinator.ApplyAppSettingsButtonState(settingsButton, visible: false);
        Require(AutomationProperties.GetName(settingsButton) == "Open app settings", "a closed settings drawer should expose an open action");
        Require(AutomationProperties.GetItemStatus(settingsButton) == "collapsed", "a closed settings drawer should expose collapsed state");
    });
}

static void AppSettingsCoordinatorSelectsProviderFocus()
{
    Require(AppSettingsCoordinator.ShouldFocusModelPicker(""), "blank model should focus the model picker");
    Require(AppSettingsCoordinator.ShouldFocusModelPicker("   "), "whitespace model should focus the model picker");
    Require(!AppSettingsCoordinator.ShouldFocusModelPicker("model-a"), "configured model should focus the test button");
    Require(AppSettingsCoordinator.ShouldAnimateSettingsGear(systemAnimationsEnabled: true), "settings affordance may animate when Windows animations are enabled");
    Require(!AppSettingsCoordinator.ShouldAnimateSettingsGear(systemAnimationsEnabled: false), "Windows reduced-motion preference should suppress the settings gear animation");
}

static void CoordinatorRenderContractsCoverSmokeStates()
{
    var alpha = new AgentState("alpha", "Alpha", "thinking", "", "default", "default", "", "alpha-model", true, false, []);
    var beta = new AgentState("beta", "Beta", "waiting", "skeptic", "default", "default", "", "", true, false, []);
    var normal = TranscriptForTest(1, "Alpha", "alpha", "message", "ok");
    var internet = TranscriptForTest(2, "Tool", "internet", "internet", "ok") with { InternetSources = ["https://example.test/a"] };
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "",
        providerLastError: "offline",
        turnIndex: 1,
        [normal, internet],
        [alpha, beta])
        with
        {
            ProviderBaseUrl = "-",
            ScenarioTopic = "",
            ScenarioGlobal = "",
            ScenarioGeneratorSeed = "ai-choice",
            PersonaGeneratorStyle = "yolo"
        };

    var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
    Require(current?.Id == "beta", "current turn should select beta for turn index 1");
    Require(SessionOverviewCoordinator.TopRunStateSummary(snapshot, current, model => model) == "Ready: next BETA using -; provider offline.", "top run summary should reflect offline provider and missing model");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, current), "offline smoke snapshot should show provider quick setup");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot) == "http://127.0.0.1:1234/v1", "offline smoke snapshot should use quick setup base URL fallback");
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText(snapshot.ScenarioTopic) == "No topic is set for this match yet.", "blank smoke scenario topic should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.ScenarioGlobalText(snapshot.ScenarioGlobal) == "No global instruction is set for this match yet.", "blank smoke global instruction should use empty-state copy");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource(snapshot.ScenarioGeneratorSeed, snapshot.PersonaGeneratorStyle) == "Wild Seed", "persona Wild Seed source should win in smoke seed metadata");
    Require(TranscriptListCoordinator.RetryableTurns(snapshot.Messages, speakerId => speakerId is "alpha" or "beta").SetEquals([1]), "smoke retryable turns should include only agent transcript messages");
}

static void TranscriptListCoordinatorSelectsRetryableTurns()
{
    var messages = new[]
    {
        TranscriptForTest(1, "System", "system", "status", "ok"),
        TranscriptForTest(2, "Alpha", "alpha", "message", "ok"),
        TranscriptForTest(3, "Operator", "operator", "message", "ok"),
        TranscriptForTest(4, "Beta", "beta", "message", "ok"),
        TranscriptForTest(5, "Narrator", "narrator", "narration", "ok"),
        TranscriptForTest(6, "Gamma", "gamma", "message", "ok"),
        TranscriptForTest(7, "Delta", "delta", "message", "ok")
    };

    var retryable = TranscriptListCoordinator.RetryableTurns(messages, speakerId =>
        speakerId is "alpha" or "beta" or "gamma" or "delta" or "epsilon" or "zeta" or "eta" or "theta");
    Require(retryable.SetEquals([4, 6, 7]), "retryable turns should be the latest three agent-speaker turns only");
}

static void TranscriptEmptyStateReflectsReadinessAndFilters()
{
    Require(TranscriptListCoordinator.EmptyStateModelLabel(" - ") == "Not selected", "empty-state model status should replace sentinel punctuation with useful copy");
    Require(TranscriptListCoordinator.EmptyStateModelLabel(" qwen/qwen3-4b ") == "qwen/qwen3-4b", "empty-state model status should preserve configured models");
    var noMemoryAgent = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "model-a", true, false, []);
    var memoryAgent = noMemoryAgent with { PrivateNotes = ["Remember the operator constraint."] };
    Require(!TranscriptListCoordinator.HasMemoryNotes(SnapshotForOverviewTest(true, "model-a", "", 0, [], [noMemoryAgent])), "empty agent memory should remain a collapsed summary instead of rendering full per-agent editors");
    Require(TranscriptListCoordinator.HasMemoryNotes(SnapshotForOverviewTest(true, "model-a", "", 0, [], [memoryAgent])), "existing private notes should reveal the full memory panel in review workflows");

    var ready = TranscriptListCoordinator.DescribeEmptyState(
        totalMessages: 0,
        providerReachable: true,
        modelSelected: true,
        activeAgentCount: 4,
        currentAgentName: "Alpha");
    Require(ready.Title == "Ready for the first turn", "configured empty transcript should announce readiness without setup noise");
    Require(ready.Status.Contains("Alpha speaks next", StringComparison.Ordinal), "ready state should expose the next speaker");
    Require(ready.Actions.SequenceEqual([
        TranscriptListCoordinator.EmptyStateAction.RunOneTurn,
        TranscriptListCoordinator.EmptyStateAction.OpenMatchSetup]), "ready state should prioritize one turn, then Match Setup");

    var providerNeeded = TranscriptListCoordinator.DescribeEmptyState(
        totalMessages: 0,
        providerReachable: false,
        modelSelected: false,
        activeAgentCount: 4,
        currentAgentName: "Alpha");
    Require(providerNeeded.Title == "Connect a provider to begin", "offline provider should produce an actionable provider state");
    Require(providerNeeded.Actions.SequenceEqual([
        TranscriptListCoordinator.EmptyStateAction.OpenProviderSettings,
        TranscriptListCoordinator.EmptyStateAction.OpenMatchSetup]), "provider setup should become the primary action when the cast is ready");

    var modelNeeded = TranscriptListCoordinator.DescribeEmptyState(
        totalMessages: 0,
        providerReachable: true,
        modelSelected: false,
        activeAgentCount: 4,
        currentAgentName: "Alpha");
    Require(modelNeeded.Title == "Select a model to begin", "a reachable provider without a model should ask for model selection, not reconnection");
    Require(modelNeeded.Status.Contains("Provider connected", StringComparison.Ordinal)
        && modelNeeded.Status.Contains("Model selection needed", StringComparison.Ordinal), "model-selection state should preserve the known online provider fact");
    Require(modelNeeded.Actions.SequenceEqual([
        TranscriptListCoordinator.EmptyStateAction.OpenModelSettings,
        TranscriptListCoordinator.EmptyStateAction.OpenMatchSetup]), "model selection should be the primary action when the provider and cast are ready");

    var castNeeded = TranscriptListCoordinator.DescribeEmptyState(
        totalMessages: 0,
        providerReachable: true,
        modelSelected: true,
        activeAgentCount: 0,
        currentAgentName: null);
    Require(castNeeded.Actions.SequenceEqual([
        TranscriptListCoordinator.EmptyStateAction.OpenMatchSetup]), "missing cast should route directly to Match Setup");

    var filtered = TranscriptListCoordinator.DescribeEmptyState(
        totalMessages: 12,
        providerReachable: true,
        modelSelected: true,
        activeAgentCount: 4,
        currentAgentName: "Alpha");
    Require(filtered.Eyebrow == "FILTERED VIEW", "filtered transcript should not look like a new session");
    Require(filtered.Body.Contains("still contains 12 messages", StringComparison.Ordinal), "filtered state should reassure users that transcript data remains");
    Require(filtered.Actions.SequenceEqual([
        TranscriptListCoordinator.EmptyStateAction.ClearFilters]), "filtered state should offer one direct recovery action");

    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/TranscriptListCoordinator.cs"));
    Require(source.Contains("Arena.Surface.Panel", StringComparison.Ordinal)
        && source.Contains("Arena.Text.Title", StringComparison.Ordinal)
        && source.Contains("Arena.Button.Primary", StringComparison.Ordinal), "empty state should consume the shared Arena design system");
    Require(source.Contains("button.MinHeight = 34", StringComparison.Ordinal), "empty-state actions should use the compact 34-DIP desktop target");
    Require(source.Contains("AutomationProperties.SetName(button", StringComparison.Ordinal)
        && source.Contains("KeyboardNavigation.SetTabIndex(button", StringComparison.Ordinal), "empty-state actions should expose names and deterministic keyboard order");
}

static void TranscriptViewCoordinatorNormalizesViewState()
{
    Require(TranscriptViewCoordinator.CurrentAvatarStyle(new WpfSettings { AvatarStyle = "champion", ChampionAvatars = true }) == "procedural", "legacy champion avatar style should map to procedural");
    Require(TranscriptViewCoordinator.CurrentAvatarStyle(new WpfSettings { AvatarStyle = "", ChampionAvatars = false }) == "simple", "blank avatar style should fall back to simple when champion avatars are disabled");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "telemetry" }) == "telemetry", "known top strip mode should be preserved");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "hidden", ShowTranscriptDiagnostics = true }) == "hidden", "known hidden top strip mode should override legacy diagnostics flag");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "weird", ShowTranscriptDiagnostics = true }) == "diagnostics", "unknown top strip mode should fall back from diagnostics flag");
    Require(!TranscriptViewCoordinator.ShouldShowPerformanceMetadata(new WpfSettings { TopStripMode = "hidden" }), "focused reading should progressively disclose model delivery metadata");
    Require(!TranscriptViewCoordinator.ShouldShowPerformanceMetadata(new WpfSettings { TopStripMode = "hidden", CompactTranscriptMode = true }), "compact reading should progressively disclose model delivery metadata");
    Require(TranscriptViewCoordinator.ShouldShowPerformanceMetadata(new WpfSettings { TopStripMode = "diagnostics" }), "diagnostics mode should reveal model delivery metadata");
    Require(TranscriptViewCoordinator.ShouldShowPerformanceMetadata(new WpfSettings { TopStripMode = "hidden", ShowBattleReview = true }), "review adjuncts should reveal model delivery metadata");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, false, false, false, true, "hidden") == "Focused", "focused preset should be detected only when the diagnostic strip is disclosed");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, true, false, true, true, "diagnostics") == "Diagnostics", "diagnostics preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, false, false, false, false, true, "hidden") == "Compact", "compact preset should be detected with a focus-first top strip");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, true, true, true, true, false, "diagnostics") == "Review", "review preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, true, true, false, true, false, "diagnostics") == "Custom", "review without battle review should be custom");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, false, false, false, false, "hidden") == "Custom", "modified focused preset should be custom");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, true, true, true, true, false, "telemetry") == "Custom", "non-diagnostics top strip should be custom");
}

static void TranscriptViewCoordinatorAdaptsDashboardWidths()
{
    var defaultDiagnostics = TranscriptViewCoordinator.ResolveDashboardLayout(858, "diagnostics");
    Require(defaultDiagnostics.Tier == TranscriptDashboardTier.Medium, "the default center width should use the medium dashboard tier");
    Require(defaultDiagnostics.ShowDiagnostics && !defaultDiagnostics.ShowTelemetry, "diagnostics should remain visible at the default center width");
    Require(defaultDiagnostics.IsStacked, "the medium dashboard should stack filters below diagnostics");
    Require(defaultDiagnostics.DiagnosticsColumns == 3, "the medium diagnostics tier should use three columns");
    Require(defaultDiagnostics.DiagnosticsMinWidth == 0, "the medium diagnostics tier should not retain the desktop minimum width");

    var mediumTelemetry = TranscriptViewCoordinator.ResolveDashboardLayout(858, "telemetry");
    Require(mediumTelemetry.ShowTelemetry && !mediumTelemetry.ShowDiagnostics, "telemetry should remain visible at the default center width");
    Require(mediumTelemetry.TelemetryColumns == 4, "the medium telemetry tier should retain four compact metrics");
    Require(mediumTelemetry.TelemetryMinWidth == 0, "the medium telemetry tier should not retain the desktop minimum width");

    var compactTelemetry = TranscriptViewCoordinator.ResolveDashboardLayout(600, "telemetry");
    Require(compactTelemetry.Tier == TranscriptDashboardTier.Compact, "a manually revealed rail should allow a compact dashboard tier");
    Require(compactTelemetry.ShowTelemetry && compactTelemetry.IsStacked, "compact telemetry should stay visible and stacked");
    Require(compactTelemetry.TelemetryColumns == 2, "compact telemetry should wrap into two columns");

    var wideDiagnostics = TranscriptViewCoordinator.ResolveDashboardLayout(TranscriptViewCoordinator.WideDashboardMinWidth, "diagnostics");
    Require(wideDiagnostics.Tier == TranscriptDashboardTier.Wide, "the wide breakpoint should select the desktop dashboard tier");
    Require(!wideDiagnostics.IsStacked && wideDiagnostics.DiagnosticsColumns == 6, "wide diagnostics should retain the six-column strip");

    var hidden = TranscriptViewCoordinator.ResolveDashboardLayout(858, "hidden");
    Require(hidden.Tier == TranscriptDashboardTier.Hidden && !hidden.ShowTopStrip, "only an explicit hidden mode should suppress both dashboard strips");
}

static void TranscriptSearchCoordinatorKeepsCloseAvailableWhenEmpty()
{
    RunStaTest(() =>
    {
        var searchText = new TextBox();
        var clearButton = new Button();
        var turnFilterPicker = new ComboBox();
        turnFilterPicker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all", IsSelected = true });
        var coordinator = new TranscriptSearchCoordinator(
            new Window(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            new System.Windows.Controls.Primitives.Popup(),
            new Button(),
            searchText,
            clearButton,
            new Border(),
            new StackPanel(),
            new TextBlock(),
            turnFilterPicker,
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            () => false,
            AccentResourceBrush,
            _ => true,
            () => null,
            () => { });

        coordinator.UpdateSearchState();
        Require(clearButton.IsEnabled, "search popup close button should remain available when no search is active");
        Require(clearButton.Opacity >= 0.8, "empty-search close button should remain visibly actionable");

        searchText.Text = "  alpha  ";
        coordinator.UpdateSearchState();
        Require(clearButton.IsEnabled, "clear search button should enable when search text is active");
        Require(Math.Abs(clearButton.Opacity - 1.0) < 0.001, "clear search button should be fully opaque when search is active");

        searchText.Text = "   ";
        coordinator.UpdateSearchState();
        Require(clearButton.IsEnabled, "whitespace-only search should still allow the popup to close");
    });
}

static void TranscriptSearchCoordinatorDebouncesTextRefresh()
{
    RunStaTest(() =>
    {
        var searchText = new TextBox();
        var turnFilterPicker = new ComboBox();
        turnFilterPicker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all", IsSelected = true });
        var refreshes = 0;
        using var coordinator = new TranscriptSearchCoordinator(
            new Window(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            new System.Windows.Controls.Primitives.Popup(),
            new Button(),
            searchText,
            new Button(),
            new Border(),
            new StackPanel(),
            new TextBlock(),
            turnFilterPicker,
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            () => false,
            AccentResourceBrush,
            _ => true,
            () => null,
            () => refreshes++);

        searchText.Text = "a";
        coordinator.OnFilterChanged(debounceTextInput: true);
        searchText.Text = "alpha";
        coordinator.OnFilterChanged(debounceTextInput: true);

        Require(coordinator.DebugIsSearchRefreshPending, "typing should leave one debounced refresh pending");
        Require(refreshes == 0, "typing should not rebuild the transcript on every key");
        coordinator.FlushPendingFilterChange();
        Require(refreshes == 1, "flushing should apply only the latest search once");
        Require(!coordinator.DebugIsSearchRefreshPending, "flush should stop the debounce timer");

        coordinator.OnFilterChanged();
        Require(refreshes == 2, "non-text filter changes should remain immediate");
    });
}

static void DispatcherDebouncerCoalescesAndFlushesWork()
{
    RunStaTest(() =>
    {
        var calls = 0;
        using var debouncer = new DispatcherDebouncer(
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            TimeSpan.FromHours(1),
            () => calls++);

        debouncer.Schedule();
        debouncer.Schedule();
        Require(debouncer.IsPending, "the latest scheduled action should remain pending");
        Require(calls == 0, "scheduling should not execute work immediately");

        debouncer.Flush();
        Require(calls == 1, "multiple schedules should flush as one action");
        Require(!debouncer.IsPending, "flush should stop the timer");

        debouncer.Flush();
        Require(calls == 1, "flushing with no pending action should be a no-op");
        debouncer.Schedule();
        debouncer.Cancel();
        Require(!debouncer.IsPending && calls == 1, "cancel should discard pending work");
    });
}

static void TranscriptSearchCoordinatorDebouncesCollaborateResultRendering()
{
    RunStaTest(() =>
    {
        var searchText = new TextBox();
        var turnFilterPicker = new ComboBox();
        turnFilterPicker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all", IsSelected = true });
        var resultEvaluations = 0;
        using var coordinator = new TranscriptSearchCoordinator(
            new Window(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            new System.Windows.Controls.Primitives.Popup(),
            new Button(),
            searchText,
            new Button(),
            new Border(),
            new StackPanel(),
            new TextBlock(),
            turnFilterPicker,
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            () => false,
            AccentResourceBrush,
            _ => true,
            () => null,
            () => { },
            new TextBlock(),
            _ => { },
            _ =>
            {
                resultEvaluations++;
                return [];
            },
            _ => true);

        coordinator.SetSurface(
            ShellSearchSurface.Collaborate,
            "Search chats",
            "Search AI Collaborate chats");
        var baselineEvaluations = resultEvaluations;

        searchText.Text = "a";
        coordinator.OnFilterChanged(debounceTextInput: true);
        searchText.Text = "alpha";
        coordinator.OnFilterChanged(debounceTextInput: true);

        Require(resultEvaluations == baselineEvaluations, "typing should not rebuild collaborate result controls before the debounce expires");
        coordinator.FlushPendingFilterChange();
        Require(resultEvaluations == baselineEvaluations + 1, "the latest collaborate query should render exactly once when flushed");
    });
}

static void TranscriptSearchCoordinatorKeepsSurfaceQueriesSeparate()
{
    RunStaTest(() =>
    {
        var searchText = new TextBox();
        var clearButton = new Button();
        var turnFilterPicker = new ComboBox();
        turnFilterPicker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all" });
        turnFilterPicker.SelectedIndex = 0;
        var transcriptRefreshes = 0;
        var collaborateQuery = "";
        var coordinator = new TranscriptSearchCoordinator(
            new Window(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            new System.Windows.Controls.Primitives.Popup(),
            new Button(),
            searchText,
            clearButton,
            new Border(),
            new StackPanel(),
            new TextBlock(),
            turnFilterPicker,
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            () => false,
            AccentResourceBrush,
            _ => true,
            () => null,
            () => transcriptRefreshes++,
            new TextBlock(),
            query => collaborateQuery = query,
            _ => [],
            _ => true);

        coordinator.SetSurface(
            ShellSearchSurface.Transcript,
            "Search transcripts",
            "Search transcript text");
        searchText.Text = "alpha transcript";
        coordinator.OnFilterChanged();

        coordinator.SetSurface(
            ShellSearchSurface.Collaborate,
            "Search chats",
            "Search AI Collaborate chats");
        Require(searchText.Text == "", "collaborate search should start with its own empty query");
        searchText.Text = "robot legs";
        coordinator.OnFilterChanged();
        Require(collaborateQuery == "robot legs", "collaborate search should receive its active query");

        coordinator.SetSurface(
            ShellSearchSurface.Transcript,
            "Search transcripts",
            "Search transcript text");
        Require(searchText.Text == "alpha transcript", "transcript search query should be restored after leaving collaborate");
        Require(transcriptRefreshes >= 2, "transcript surface changes should refresh transcript results");

        coordinator.SetSurface(
            ShellSearchSurface.Collaborate,
            "Search chats",
            "Search AI Collaborate chats");
        Require(searchText.Text == "robot legs", "collaborate search query should be restored after returning to collaborate");
    });
}

static void TranscriptSearchCoordinatorExposesRowAutomation()
{
    RunStaTest(() =>
    {
        var searchText = new TextBox();
        var clearButton = new Button();
        var recentItems = new StackPanel();
        var turnFilterPicker = new ComboBox();
        turnFilterPicker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all" });
        turnFilterPicker.SelectedIndex = 0;
        var chatId = Guid.NewGuid();
        var openedChatId = Guid.Empty;
        var coordinator = new TranscriptSearchCoordinator(
            new Window(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            new System.Windows.Controls.Primitives.Popup(),
            new Button(),
            searchText,
            clearButton,
            new Border(),
            recentItems,
            new TextBlock(),
            turnFilterPicker,
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            new CheckBox { IsChecked = true },
            () => false,
            AccentResourceBrush,
            _ => true,
            () => null,
            () => { },
            new TextBlock(),
            _ => { },
            _ =>
            [
                new CollaborateCoordinator.CollaborateSearchResult(
                    chatId,
                    "Robot legs review",
                    "Snippet about collision, gestures, and inspector focus.",
                    new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero),
                    2)
            ],
            id =>
            {
                openedChatId = id;
                return true;
            });

        searchText.Text = "alpha assumption";
        typeof(TranscriptSearchCoordinator)
            .GetMethod("StoreCurrentSearch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null);
        coordinator.UpdateSearchState();
        var recentButton = recentItems.Children.OfType<Button>().Single();
        Require(AutomationProperties.GetName(recentButton) == "Search for alpha assumption", "recent search row should expose a concise automation name");
        Require(AutomationProperties.GetHelpText(recentButton).Contains("alpha assumption", StringComparison.Ordinal), "recent search row should expose action help text");
        Require(AutomationProperties.GetItemStatus(recentButton) == "most recent search", "recent search row should expose recency state");

        coordinator.SetSurface(
            ShellSearchSurface.Collaborate,
            "Search AI Collaborate chats",
            "Search saved AI Collaborate chats");
        searchText.Text = "legs";
        coordinator.OnFilterChanged();
        var collaborateButton = recentItems.Children.OfType<Button>().Single();
        Require(AutomationProperties.GetName(collaborateButton).Contains("Robot legs review", StringComparison.Ordinal), "collaborate row should include the chat title in its automation name");
        Require(AutomationProperties.GetName(collaborateButton).Contains("2 hits", StringComparison.Ordinal), "collaborate row should include match count in its automation name");
        Require(AutomationProperties.GetHelpText(collaborateButton).Contains("collision", StringComparison.Ordinal), "collaborate row should expose the result snippet as help text");
        Require(AutomationProperties.GetItemStatus(collaborateButton) == "2 hits", "collaborate row should expose match count item status");

        collaborateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(openedChatId == chatId, "collaborate search row should still open the selected chat");
    });
}

static void NarrowingTheWindowDoesNotHideTheRailForGood()
{
    // Once the window is wide again the effective state is decided by the latch
    // alone, and the resize handler only ever set it. Narrowing the window once
    // therefore hid the right rail for good: the preference said expanded, the
    // latch said collapsed, nothing announced the change, and only a toggle
    // could break the tie.
    Require(
        !MainWindow.IsRightRailEffectivelyCollapsed(false, false, false, false),
        "wide window, expanded preference, no latch: the rail belongs on screen");
    Require(
        MainWindow.IsRightRailEffectivelyCollapsed(false, true, false),
        "a narrow window collapses the rail even while the preference says expanded");
    Require(
        !MainWindow.IsRightRailEffectivelyCollapsed(false, true, true),
        "asking for it by hand while narrow should reveal it");
    Require(
        MainWindow.IsRightRailEffectivelyCollapsed(true, false, false),
        "an explicit preference to collapse still wins at any width");

    // This is the state the bug left behind, and it is still reachable if the
    // latch is ever left set while wide - which is what the guard below prevents.
    Require(
        MainWindow.IsRightRailEffectivelyCollapsed(false, false, false, widthCollapseLatched: true),
        "a latch left set while wide hides the rail, which is why it must be cleared");

    var shell = ReadMainWindowSource();
    var sizeChanged = shell.IndexOf("private void MainWindow_SizeChanged", StringComparison.Ordinal);
    Require(sizeChanged >= 0, "the resize handler should remain discoverable");
    var body = shell[sizeChanged..Math.Min(shell.Length, sizeChanged + 1400)];
    Require(
        body.Contains("_rightRailWidthCollapseLatched = autoCollapse", StringComparison.Ordinal),
        "the latch has to track the narrow range in both directions, not just on the way in");
}

static void SettingsAndMatchSetupDoNotShareTheWindow()
{
    // Opening Match Setup has always hidden settings. The mirror image was
    // never implemented, so opening settings over Match Setup left both up:
    // the settings panel covered Match Setup's close button and footer, and the
    // only way out was to close settings first. The asymmetry was the tell -
    // one direction had a deliberate hide, the other had nothing.
    var settings = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/AppSettingsCoordinator.cs"));
    Require(
        settings.Contains("Opening?.Invoke()", StringComparison.Ordinal),
        "the settings overlay should announce that it is about to open");

    var setVisible = settings.IndexOf("public void SetVisible", StringComparison.Ordinal);
    Require(setVisible >= 0, "SetVisible should remain the single visibility path");
    var body = settings[setVisible..Math.Min(settings.Length, setVisible + 400)];
    Require(
        body.IndexOf("Opening?.Invoke()", StringComparison.Ordinal)
            < body.IndexOf("SetAppSettingsVisible", StringComparison.Ordinal),
        "the host needs to dismiss the other overlay before this one is shown, not after");

    var shell = ReadMainWindowSource();
    Require(
        shell.Contains("appSettings.Opening", StringComparison.Ordinal)
            && shell.Contains("CloseMatchSetupFlyout()", StringComparison.Ordinal),
        "opening settings should close Match Setup, mirroring what Match Setup already does");
}

static void ScreenshotsAndModalsDoNotMisreportTheApp()
{
    // Two failures that both produced a confident wrong answer rather than an
    // obvious error, which is the worst shape a verification tool can have.
    //
    // RenderTargetBitmap walks one visual tree, so capturing the window alone
    // returned an image with no dialog in it while a dialog was plainly on
    // screen. Anyone checking a dialog against that image concluded it had
    // never opened.
    var screenshot = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaScreenshotControlService.cs"));
    Require(
        screenshot.Contains("RenderWithOpenDialogs", StringComparison.Ordinal),
        "the screenshot should composite dialogs over the window");
    Require(
        screenshot.Contains("candidate.Owner, window", StringComparison.Ordinal),
        "only dialogs owned by the shell window should be composited in");

    // ShowDialog runs a nested message loop and does not return until the
    // palette closes, so opening it inline made a control-plane Ctrl+K wait for
    // a human and time out.
    var palette = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.CommandPalette.cs"));
    var showStart = palette.IndexOf("private void ShowCommandPalette", StringComparison.Ordinal);
    Require(showStart >= 0, "the palette entry point should remain discoverable");

    // Bounded by the next member, or the slice runs into OpenCommandPalette,
    // which is supposed to contain the call being ruled out here.
    var showEnd = palette.IndexOf("private void OpenCommandPalette", showStart, StringComparison.Ordinal);
    Require(showEnd > showStart, "the deferred opener should remain discoverable");
    var showBody = palette[showStart..showEnd];
    Require(
        showBody.Contains("BeginInvoke", StringComparison.Ordinal),
        "the palette must open on a later dispatcher pass so a control-plane chord can return");
    Require(
        !showBody.Contains("CommandPaletteDialog.Show", StringComparison.Ordinal),
        "ShowCommandPalette must not open the dialog inline again");

    // Deferring the open made duplicates possible. Three quick Ctrl+K presses
    // queued three opens before the first had run and stacked three modals, and
    // the control plane could not dismiss them: a chord sent over the pipe
    // reaches the shell handler, not the focused dialog.
    Require(
        showBody.Contains("_paletteOpen", StringComparison.Ordinal),
        "ShowCommandPalette should toggle a palette that is already open");
    Require(
        showBody.Contains("open.Close()", StringComparison.Ordinal),
        "a second Ctrl+K should close the palette rather than stack another");

    var openStart = palette.IndexOf("private void OpenCommandPalette", StringComparison.Ordinal);
    var openEnd = palette.IndexOf("private void OpenCommandPaletteCore", openStart, StringComparison.Ordinal);
    Require(openEnd > openStart, "the guarded opener should remain discoverable");
    Require(
        palette[openStart..openEnd].Contains("if (_paletteOpen)", StringComparison.Ordinal),
        "the opener needs its own guard, because queued opens are dispatched before any of them has opened anything");
}

static void ControlPlaneKeyParsingAcceptsWhatPeopleWrite()
{
    // The control plane sends chords through the shell shortcut layer rather
    // than simulating operating-system input, because simulated input goes to
    // whichever window is foreground - which for a background caller is somebody
    // else's application.
    Require(MainWindow.TryParseShortcutKey("F2", out var f2) && f2 == Key.F2, "function keys should parse");
    Require(MainWindow.TryParseShortcutKey("k", out var k) && k == Key.K, "letters should parse case-insensitively");
    Require(MainWindow.TryParseShortcutKey(" Escape ", out var esc) && esc == Key.Escape, "surrounding space should not matter");

    // A bare digit is what someone would write for Ctrl+1, and D1 is not a name
    // anyone would guess.
    Require(MainWindow.TryParseShortcutKey("1", out var one) && one == Key.D1, "a bare digit should map onto the D keys");

    Require(!MainWindow.TryParseShortcutKey("", out _), "an empty key should be rejected");
    Require(!MainWindow.TryParseShortcutKey("nonsense", out _), "an unknown key should be rejected rather than silently ignored");

    var ctrlShift = MainWindow.ParseModifiers("ctrl+shift");
    Require(ctrlShift.Control && ctrlShift.Shift && !ctrlShift.Alt, "ctrl+shift should parse");
    var spaced = MainWindow.ParseModifiers("Control Alt");
    Require(spaced.Control && spaced.Alt && !spaced.Shift, "spaces and the long name should work too");
    var none = MainWindow.ParseModifiers(null);
    Require(!none.Control && !none.Shift && !none.Alt, "no modifiers should mean no modifiers");

    Require(MainWindow.DescribeChord(Key.K, true, false, false) == "Ctrl+K", "the chord should read back the way it was asked for");
    Require(MainWindow.DescribeChord(Key.F2, false, false, false) == "F2", "an unmodified key needs no prefix");
}

static void EveryHumanDrivableEventHasASharedPublisher()
{
    // Events published only from the control-plane dispatch describe what an
    // operator did through PowerShell and nothing else, so a watcher sees
    // silence while a person uses the app. Anything a person can also trigger
    // has to publish from the path both routes share.
    //
    // The list below is the deliberate exception: these are control-plane
    // concepts, not shell state. Adding a new event to the dispatch fails this
    // test until someone either gives it a shared publisher or writes down here
    // why it cannot have one.
    var controlPlaneOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        // Staging, approving and rejecting a command, and sending an Agent or
        // operator prompt, are all requests that arrive with the command. The
        // equivalent UI gestures report themselves through the Agent workspace
        // and transcript rather than through these.
        "agent.command.staged",
        "agent.command.approved",
        "agent.command.rejected",
        "agent.prompt.sent",
        "agent.prompt.staged",
        "agent.stop.requested",
        "agent.runbook.resumed",
        "agent.runbook.checkpointed",
        "agent.workspace.changed",
        "arena.operator.sent",
        "arena.reset.completed",
        "match.generation.changed",
        "session.saved-state.changed",
        "navigation.provider.focused"
    };

    var dispatch = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs"));
    var published = new HashSet<string>(StringComparer.Ordinal);
    foreach (System.Text.RegularExpressions.Match match in
             System.Text.RegularExpressions.Regex.Matches(dispatch, @"Publish\(""([^""]+)"""))
    {
        published.Add(match.Groups[1].Value);
    }

    Require(published.Count > 0, "the control-plane dispatch should still publish something");

    var unexplained = published.Where(name => !controlPlaneOnly.Contains(name)).OrderBy(name => name).ToList();
    Require(
        unexplained.Count == 0,
        $"these events publish only from the control-plane dispatch and need a shared publisher: {string.Join(", ", unexplained)}");

    // The other half: the shell publishers must still be doing their job.
    var shellEvents = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ShellEvents.cs"));
    foreach (var name in new[]
    {
        "arena.run.started",
        "arena.run.stopped",
        "arena.turn.completed",
        "arena.narration.completed",
        "internet.changed",
        "internet.test.completed"
    })
    {
        Require(
            shellEvents.Contains($"\"{name}\"", StringComparison.Ordinal),
            $"{name} should publish from the shared path");
        Require(
            !published.Contains(name),
            $"{name} must not also publish from the dispatch, or control-plane callers get it twice");
    }
}

static void DependencyIndexCheckIgnoresLineEndings()
{
    // The release gate compared the checked-in index against a freshly generated
    // one. .gitattributes checks that file out as LF while the generator builds
    // it with Environment.NewLine, so on Windows a fresh clone or a fresh pull
    // compared CRLF against LF and failed the release on an index that was
    // identical in content. It hid for a long time because the checkout that
    // generated the file was usually also the one being gated.
    var script = File.ReadAllText(FindWorkspaceFile("scripts/dependency-index.ps1"));
    var checkStart = script.IndexOf("if ($Check)", StringComparison.Ordinal);
    Require(checkStart >= 0, "the dependency index should still have a check mode");
    var checkBlock = script[checkStart..Math.Min(script.Length, checkStart + 1400)];

    Require(
        checkBlock.Contains("normalizedExisting", StringComparison.Ordinal)
            && checkBlock.Contains("normalizedNew", StringComparison.Ordinal),
        "the check should compare normalized documents");
    Require(
        checkBlock.Contains("`r`n", StringComparison.Ordinal),
        "the staleness check must normalize line endings, or a fresh clone fails the release gate");
    Require(
        checkBlock.Contains("Generated by <timestamp>.", StringComparison.Ordinal),
        "the staleness check must keep ignoring the generation timestamp");
}

static void EmptyAndOffStatesAreNotStyledAsFailures()
{
    // The danger tone should mean "something went wrong", not "there is nothing
    // here yet" or "you turned this off". Two places conflated them, and both
    // greeted the reader with red before they had done anything wrong.
    var shell = ReadMainWindowSource();

    var noSessions = shell.IndexOf("No saved sessions yet", StringComparison.Ordinal);
    Require(noSessions >= 0, "the first-run session status should still be reported");
    var noSessionsLine = shell[noSessions..Math.Min(shell.Length, noSessions + 160)];
    Require(
        !noSessionsLine.Contains("isDanger: true", StringComparison.Ordinal),
        "an empty data root is where every first run starts, not a failure");

    var internet = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/InternetWorkflowCoordinator.cs"));
    var offHint = internet.IndexOf("Internet is off.", StringComparison.Ordinal);
    Require(offHint >= 0, "the internet-off hint should still be shown");
    var offBlock = internet[offHint..Math.Min(internet.Length, offHint + 260)];
    Require(
        !offBlock.Contains("DangerTextBrush", StringComparison.Ordinal),
        "internet off is a deliberate choice, and the more conservative one, so it must not read as a fault");
    Require(
        offBlock.Contains("MutedTextBrush", StringComparison.Ordinal),
        "the internet-off hint should use the muted tone");

    // The flag must survive for the cases it is actually for, or this guard
    // would just be pushing every failure into the same silent grey.
    var savedState = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/SavedStateWorkflowCoordinator.cs"));
    Require(
        savedState.Contains("Checkpoint load failed.\", isDanger: true", StringComparison.Ordinal),
        "a genuine failure should still be styled as one");
}

static void CommandPaletteRanksMatchesPredictably()
{
    // A palette that reorders itself unpredictably is worse than one that only
    // filters, so the ranking is deliberately plain and pinned here.
    static ShellCommand Command(string title, string group = "Shell", string keys = "", string keywords = "", Func<bool>? available = null)
    {
        return new ShellCommand(title, title, group, keys, keywords, () => { }, available);
    }

    var commands = new List<ShellCommand>
    {
        Command("Go to AI Lab", "Navigate", "Ctrl+1", "arena transcript"),
        Command("Open Match Setup", "Match", "F2", "scenario cast seed"),
        Command("Open App Settings", "Shell", "F10", "preferences provider"),
        Command("Search the transcript", "Transcript", "Ctrl+F", "find filter"),
        Command("Theme: Dark Blue", "Appearance", "", "colour color dark light"),
        Command("Go to Agent", "Navigate", "Ctrl+2", "workspace", () => false)
    };

    // An unavailable command is dropped rather than shown greyed out: offering
    // something that cannot run wastes the reader's attention.
    var all = ShellCommandPalette.Filter(commands, "");
    Require(all.Count == 5, "an empty query should list every available command");
    Require(all.All(command => command.Title != "Go to Agent"), "a gated command should not be offered");
    Require(all[0].Title == "Go to AI Lab", "an empty query should preserve the declared order");

    // Prefix beats word-prefix beats substring.
    var open = ShellCommandPalette.Filter(commands, "open");
    Require(open.Count == 2, "'open' should match both Open commands");
    Require(open[0].Title == "Open Match Setup", "declared order should break ties between equal scores");

    var setup = ShellCommandPalette.Filter(commands, "setup");
    Require(setup.Count == 1 && setup[0].Title == "Open Match Setup", "a word prefix inside the title should match");

    var exact = ShellCommandPalette.Filter(commands, "Go to AI Lab");
    Require(exact[0].Title == "Go to AI Lab", "an exact title should rank first");

    // Someone who remembers the key but not the wording should still land on it.
    var byKey = ShellCommandPalette.Filter(commands, "F10");
    Require(byKey.Count == 1 && byKey[0].Title == "Open App Settings", "a shortcut chord should find its command");

    // And someone who thinks in intent rather than in the app's vocabulary.
    var byKeyword = ShellCommandPalette.Filter(commands, "colour");
    Require(byKeyword.Count == 1 && byKeyword[0].Title == "Theme: Dark Blue", "keywords should cover wording the title does not use");

    var byGroup = ShellCommandPalette.Filter(commands, "navigate");
    Require(byGroup.Count == 1 && byGroup[0].Title == "Go to AI Lab", "a group name should match while browsing");

    Require(ShellCommandPalette.Filter(commands, "SEARCH").Count == 1, "matching must be case-insensitive");
    Require(ShellCommandPalette.Filter(commands, "zzzz").Count == 0, "a query matching nothing should return nothing");

    // Recency reorders the browse list, so the handful of things you are doing
    // right now sit at the top when the palette opens.
    var recent = new[] { "Open App Settings", "Theme: Dark Blue" };
    var browsed = ShellCommandPalette.Filter(commands, "", recent);
    Require(browsed[0].Title == "Open App Settings", "the most recent command should lead an empty query");
    Require(browsed[1].Title == "Theme: Dark Blue", "recency order should be preserved");
    Require(browsed.Count == 5, "recency must not drop or duplicate anything");
    Require(browsed[2].Title == "Go to AI Lab", "everything else should keep its declared order");

    // But it only ever breaks ties. Typing a command's exact name and watching
    // something else sit above it is precisely the unpredictability this
    // ranking exists to avoid.
    var exactWithRecency = ShellCommandPalette.Filter(commands, "Go to AI Lab", recent);
    Require(
        exactWithRecency[0].Title == "Go to AI Lab",
        "an exact match must outrank a more recently used command");

    // Titles beat incidental substrings: "set" starts a word in "Settings" and
    // in "Setup", but must not drag in unrelated rows.
    var set = ShellCommandPalette.Filter(commands, "set");
    Require(set.Count == 2, "'set' should reach Setup and Settings only");
    Require(
        ShellCommandPalette.Score(Command("Open Match Setup"), "Open") < ShellCommandPalette.Score(Command("Open Match Setup"), "Match"),
        "a title prefix should outrank a later word");
}

static void ShellStateChangesReachTheControlPlaneFromBothRoutes()
{
    // Shell events used to be published by the control-plane command handlers
    // themselves, so a watcher only ever saw changes an operator caused through
    // PowerShell. Open Match Setup with F2 and the stream stayed silent, which
    // made "live events" useless for watching a person drive the app. The
    // publishers now live on the shared paths, and the handlers must not take
    // them back.
    var gate = typeof(MainWindow);
    Require(gate is not null, "MainWindow should be visible to the test project");

    // The gate keeps repeat calls quiet. This matters most for the right rail:
    // ApplyRightRailCollapsed runs on every window resize, so an ungated
    // publisher would flood the stream while someone drags a window corner.
    Require(MainWindow.ShouldPublishChange(null, "expanded"), "first observation should publish");
    Require(!MainWindow.ShouldPublishChange("expanded", "expanded"), "an unchanged rail state must stay quiet during resizes");
    Require(MainWindow.ShouldPublishChange("expanded", "collapsed"), "a real change should publish");
    Require(!MainWindow.ShouldPublishChange("collapsed", "  "), "a blank state is not worth announcing");
    Require(
        !MainWindow.ShouldPublishChange("dark-blue", "DARK-BLUE", StringComparison.OrdinalIgnoreCase),
        "theme ids should compare case-insensitively");
    Require(
        MainWindow.ShouldPublishChange("dark-blue", "DARK-BLUE"),
        "the default comparison stays ordinal so overlay keys are not accidentally merged");

    var shellEvents = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ShellEvents.cs"));
    foreach (var eventName in new[]
    {
        "navigation.changed",
        "navigation.rail.changed",
        "navigation.theme.changed",
        "shell.overlay.changed",
        "view.preset.changed"
    })
    {
        Require(
            shellEvents.Contains($"\"{eventName}\"", StringComparison.Ordinal),
            $"{eventName} should be published from the shared shell path");
    }

    // If a handler publishes again, control-plane callers get the event twice
    // while UI callers still get it once, which is worse than the original bug.
    var dispatch = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs"));
    foreach (var eventName in new[]
    {
        "navigation.changed",
        "navigation.rail.changed",
        "navigation.theme.changed",
        "view.preset.changed"
    })
    {
        Require(
            !dispatch.Contains($"Publish(\"{eventName}\"", StringComparison.Ordinal),
            $"{eventName} must be published from the shared path, not the control-plane dispatch");
    }

    foreach (var handler in new[]
    {
        "src/AIArena.Wpf/Shell/ControlPlane/AIArenaSettingsControlHandler.cs",
        "src/AIArena.Wpf/Shell/ControlPlane/AIArenaMatchSetupControlHandler.cs"
    })
    {
        Require(
            !File.ReadAllText(FindWorkspaceFile(handler)).Contains("Publish(\"shell.overlay.changed\"", StringComparison.Ordinal),
            $"{handler} must not republish shell.overlay.changed");
    }

    // The shared paths themselves must keep calling the publishers.
    var shell = ReadMainWindowSource();
    Require(
        shell.Contains("PublishNavigationChanged();", StringComparison.Ordinal),
        "the surface-change path must announce navigation");
    Require(
        shell.Contains("PublishRailChanged();", StringComparison.Ordinal),
        "the rail layout path must announce rail changes");
    Require(
        shell.Contains("PublishMatchSetupOverlayChanged(", StringComparison.Ordinal),
        "the Match Setup show and close paths must announce the overlay");
    Require(
        shell.Contains("PublishSettingsOverlayChanged(", StringComparison.Ordinal),
        "the settings search path must announce the query");

    // settings.search shows the overlay first and sets the query afterwards, so
    // announcing only on visibility would report a stale query.
    var searchStart = shell.IndexOf("private void SettingsSearchText_TextChanged", StringComparison.Ordinal);
    Require(searchStart >= 0, "the settings search handler should remain discoverable");
    var searchBody = shell[searchStart..Math.Min(shell.Length, searchStart + 800)];
    Require(
        searchBody.Contains("PublishSettingsOverlayChanged", StringComparison.Ordinal),
        "the settings query must be announced from the text-changed handler");
}

}
