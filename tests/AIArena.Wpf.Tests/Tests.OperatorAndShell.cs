using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static void OperatorTurnCoordinatorDisablesInputDuringBusyWork()
{
    Require(OperatorTurnCoordinator.OperatorInputEnabled(false, false), "operator input should be enabled while idle");
    Require(!OperatorTurnCoordinator.OperatorInputEnabled(true, false), "operator input should disable during normal busy work");
    Require(OperatorTurnCoordinator.OperatorInputEnabled(true, true), "operator input should remain available during auto chat");
    Require(!OperatorTurnCoordinator.OperatorInputEnabled(false, false, sendInProgress: true), "operator input should disable while a send is already in progress");
    Require(OperatorTurnCoordinator.TryNormalizeOperatorRoute(" PRIVATE ", out var privateRoute) && privateRoute == "private", "known operator routes should normalize without changing their visibility semantics");
    Require(OperatorTurnCoordinator.TryNormalizeOperatorRoute(null, out var defaultRoute) && defaultRoute == "public", "an omitted operator route should retain the documented public default");
    Require(!OperatorTurnCoordinator.TryNormalizeOperatorRoute("privte", out _), "an unknown operator route must be rejected instead of silently becoming public");

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-operator-turn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var settings = new WpfSettings { OperatorTemplates = ["Challenge the strongest assumption"] };
            var publicRouteButton = new Button();
            var privateRouteButton = new Button();
            var narratorRouteButton = new Button();
            var privateTargetPicker = new ComboBox();
            var privateTargetSummaryText = new TextBlock();
            var routeHintText = new TextBlock();
            var meterText = new TextBlock();
            var quickInterventionHintText = new TextBlock();
            var templatePicker = new ComboBox();
            var useTemplateButton = new Button();
            var saveTemplateButton = new Button();
            var deleteTemplateButton = new Button();
            var quickButtons = new[] { new Button(), new Button(), new Button(), new Button() };
            var turnText = new TextBox();
            var sendButton = new Button();
            SessionSummary? currentSession = null;
            var arenaStatus = "";
            var coordinator = new OperatorTurnCoordinator(
                sessionStore,
                eventLogStore,
                new TranscriptService(),
                new NarratorService(sessionStore: sessionStore, eventLogStore: eventLogStore),
                new DiscourseDiagnosticsService(),
                settingsStore,
                publicRouteButton,
                privateRouteButton,
                narratorRouteButton,
                new Grid(),
                privateTargetPicker,
                privateTargetSummaryText,
                routeHintText,
                meterText,
                quickInterventionHintText,
                quickButtons,
                templatePicker,
                useTemplateButton,
                saveTemplateButton,
                deleteTemplateButton,
                turnText,
                sendButton,
                () => settings,
                () => currentSession,
                () => null,
                () => false,
                AccentResourceBrush,
                (_, _, action, _) => action(),
                (_, _) => Task.CompletedTask,
                _ => Task.CompletedTask,
                _ => { },
                value => arenaStatus = value);

            coordinator.InitializeControls();
            Require(AutomationProperties.GetName(sendButton) == "Send Public", "public route should name the send action");
            Require(AutomationProperties.GetItemStatus(publicRouteButton) == "selected", "public route should expose selected automation state");
            Require(AutomationProperties.GetItemStatus(privateRouteButton) == "not selected", "private route should expose unselected automation state");
            Require(meterText.Text == "0 chars / ~0 tok | Public transcript", "operator meter should include the public route");
            Require(routeHintText.Text.Contains("Visible transcript turn", StringComparison.Ordinal), "public route hint should describe visibility");
            Require(meterText.ToolTip?.ToString()?.Contains("AI Arena Operator Draft", StringComparison.Ordinal) == true, "operator meter should expose a receipt tooltip");
            Require(AutomationProperties.GetHelpText(meterText).Contains("Public transcript", StringComparison.Ordinal), "operator meter should expose route automation help");
            Require(quickInterventionHintText.Text.Contains("Set Stakes -> Public", StringComparison.Ordinal), "quick intervention hint should include route labels");
            Require(quickButtons[0].ToolTip?.ToString()?.Contains("Intervention: Set Stakes", StringComparison.Ordinal) == true, "quick intervention button should expose receipt tooltip");
            Require(AutomationProperties.GetHelpText(quickButtons[0]).Contains("Route: Public transcript", StringComparison.Ordinal), "quick intervention automation help should include route");
            coordinator.SetRouteMode("private");
            Require(AutomationProperties.GetName(sendButton) == "Send Private", "private route should name the send action");
            Require(AutomationProperties.GetItemStatus(privateRouteButton) == "selected", "private route should expose selected automation state");
            Require(meterText.Text == "0 chars / ~0 tok | Private memory -> all active", "private route should update the meter without typing");
            Require(routeHintText.Text.Contains("Hidden from the public transcript", StringComparison.Ordinal), "private route hint should describe visibility");
            Require(privateTargetSummaryText.Text.Contains("No active private targets", StringComparison.Ordinal), "private target summary should explain empty snapshots");
            coordinator.SetRouteMode("narrator");
            Require(AutomationProperties.GetName(sendButton) == "Ask Narrator", "narrator route should name the send action");
            Require(AutomationProperties.GetItemStatus(narratorRouteButton) == "selected", "narrator route should expose selected automation state");
            Require(meterText.Text == "0 chars / ~0 tok | Narrator request", "narrator route should update the meter without typing");
            Require(routeHintText.Text.Contains("participant turn order", StringComparison.Ordinal), "narrator route hint should describe turn-order impact");
            coordinator.SetRouteMode("public");
            var operatorControlsExceptSave = new Control[]
            {
                sendButton,
                turnText,
                publicRouteButton,
                privateRouteButton,
                narratorRouteButton,
                privateTargetPicker,
                quickButtons[0],
                quickButtons[1],
                quickButtons[2],
                quickButtons[3],
                templatePicker,
                useTemplateButton,
                deleteTemplateButton
            };
            Require(operatorControlsExceptSave.All(control => control.IsEnabled), "operator controls should start enabled");
            Require(!saveTemplateButton.IsEnabled, "save template should disable until operator text is present");

            turnText.Text = "Persist this operator nudge";
            coordinator.UpdateTurnMeter();
            var operatorControls = operatorControlsExceptSave.Append(saveTemplateButton).ToArray();
            Require(saveTemplateButton.IsEnabled, "save template should enable when operator text is present");

            coordinator.UpdateBusyState(busy: true, autoChatRunning: false);
            Require(operatorControls.All(control => !control.IsEnabled), "operator controls should disable during normal busy work");

            coordinator.UpdateBusyState(busy: true, autoChatRunning: true);
            Require(operatorControls.All(control => control.IsEnabled), "operator controls should remain enabled during auto chat");

            coordinator.UpdateBusyState(busy: false, autoChatRunning: false);
            var sessionA = SnapshotForOverviewTest(false, "", "", 0, [], []) with { SessionId = "session-a" };
            var sessionB = sessionA with { SessionId = "session-b" };
            coordinator.ApplySnapshot(sessionA);
            turnText.Text = "Public draft for session A";
            coordinator.UpdateTurnMeter();
            coordinator.SetRouteMode("private");
            Require(turnText.Text == "", "each route should begin with an independent draft in the active session");
            turnText.Text = "Private draft for session A";
            coordinator.UpdateTurnMeter();
            coordinator.SetRouteMode("public");
            Require(turnText.Text == "Public draft for session A", "switching routes should restore that session's public draft");

            coordinator.ApplySnapshot(sessionB);
            Require(turnText.Text == "", "switching sessions must not leak the prior session's operator draft");
            Require(AutomationProperties.GetItemStatus(publicRouteButton) == "selected", "a new session should start on its safe public route");
            turnText.Text = "Public draft for session B";
            coordinator.UpdateTurnMeter();
            coordinator.ApplySnapshot(sessionA);
            Require(turnText.Text == "Public draft for session A", "returning to a session should restore its route-scoped draft");
            coordinator.SetRouteMode("private");
            Require(turnText.Text == "Private draft for session A", "returning to a saved route should restore its independent draft");
            coordinator.ApplySnapshot(sessionB);
            Require(turnText.Text == "Public draft for session B", "each session should restore its last selected route and draft");
            Require(routeHintText.Text.Contains("Session: session-b", StringComparison.Ordinal), "the visible route hint should identify the destination session");
            Require(AutomationProperties.GetHelpText(sendButton).Contains("Session: session-b", StringComparison.Ordinal), "send automation help should identify the destination session");

            coordinator.UseOperatorTemplate();
            Require(turnText.Text.StartsWith("Public draft for session B", StringComparison.Ordinal), "using a template must preserve the existing draft");
            Require(turnText.Text.Contains("Challenge the strongest assumption", StringComparison.Ordinal), "using a template should append the selected template");
            Require(arenaStatus.Contains("appended", StringComparison.OrdinalIgnoreCase), "template staging should explicitly report an append");
            var draftBeforeQuickAction = turnText.Text;
            coordinator.ApplyQuickIntervention("set_stakes");
            Require(turnText.Text.StartsWith(draftBeforeQuickAction, StringComparison.Ordinal), "quick interventions must not replace a nonempty draft");
            Require(arenaStatus.Contains("appended", StringComparison.OrdinalIgnoreCase), "quick intervention staging should explicitly report an append");

            currentSession = new SessionSummary("session-b", "", false, 0, 0, 0, DateTimeOffset.UtcNow);
            coordinator.SetRouteMode("private");
            turnText.Text = "Visible private draft";
            coordinator.UpdateTurnMeter();
            coordinator.ControlSendAsync("PowerShell public intervention", "public").GetAwaiter().GetResult();
            Require(turnText.Text == "Visible private draft", "control-plane sends must preserve the user's visible draft");
            Require(AutomationProperties.GetItemStatus(privateRouteButton) == "selected", "control-plane sends must preserve the user's visible route");

            coordinator.SendOperatorTurnAsync().GetAwaiter().GetResult();
            Require(turnText.Text == "Visible private draft", "a failed private send must retain its draft");
            coordinator.SetRouteMode("public");
            turnText.Text = "Visible public draft";
            coordinator.UpdateTurnMeter();
            coordinator.SendOperatorTurnAsync().GetAwaiter().GetResult();
            Require(turnText.Text == "Visible public draft", "a failed public send must retain its draft");
            coordinator.SetRouteMode("narrator");
            turnText.Text = "Visible narrator draft";
            coordinator.UpdateTurnMeter();
            coordinator.SendOperatorTurnAsync().GetAwaiter().GetResult();
            Require(turnText.Text == "Visible narrator draft", "a failed narrator request must retain its draft");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });
}

static void OperatorTurnCoordinatorSuggestsInterventions()
{
    var starter = OperatorTurnCoordinator.BuildInterventionSuggestions(null, null);
    Require(starter.Count == 4, "starter intervention deck should expose four quick choices");
    Require(starter[0].Id == "set_stakes", "starter deck should lead with stakes");
    Require(starter.Any(item => item.Route == "narrator"), "starter deck should include a narrator route");

    var diagnostics = new FrictionDiagnostics(
        "Theatre Risk",
        "danger",
        82,
        "High",
        41,
        "High",
        2,
        "danger",
        12,
        "Weak",
        88,
        "High",
        new Dictionary<string, MetricDiagnostic>());
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "model-a",
        providerLastError: "",
        turnIndex: 0,
        [
            TranscriptForTest(1, "Alpha", "alpha", "message", "ok"),
            TranscriptForTest(2, "Beta", "beta", "message", "error"),
            TranscriptForTest(3, "Gamma", "gamma", "message", "ok")
        ],
        [
            new AgentState("alpha", "Alpha", "waiting", "", "analyst", "default", "", "model-a", true, false, []),
            new AgentState("beta", "Beta", "waiting", "", "critic", "default", "", "model-b", true, false, []),
            new AgentState("gamma", "Gamma", "waiting", "", "evidence", "default", "", "model-c", true, false, [])
        ]);

    var suggestions = OperatorTurnCoordinator.BuildInterventionSuggestions(snapshot, diagnostics);
    Require(suggestions.Count == 4, "risk intervention deck should cap visible choices");
    Require(suggestions[0].Id == "repair", "errors should lead with a repair intervention");
    Require(suggestions.Any(item => item.Id == "evidence"), "weak evidence should add an evidence intervention");
    Require(suggestions.Any(item => item.Id == "break_consensus"), "high consensus should add a consensus breaker");
    Require(suggestions.Any(item => item.Route == "private"), "role drift should add a private role reset");
    Require(suggestions.All(item => !string.IsNullOrWhiteSpace(item.Prompt)), "interventions should stage concrete text");

}

static void TranscriptActionCoordinatorExposesAutomationNames()
{
    RunStaTest(() =>
    {
        var busy = false;
        var coordinator = new TranscriptActionCoordinator(
            () => false,
            () => busy,
            AccentResourceBrush);

        var iconButton = coordinator.CreateButton("Copy", null, enabled: true, iconGlyph: "\uE8C8");
        var labeledButton = coordinator.CreateLabeledButton("Pin", null, enabled: true, TranscriptActionKind.Primary, "\uE718");

        Require(AutomationProperties.GetName(iconButton) == "Copy", "icon transcript action should expose its command label");
        Require(AutomationProperties.GetHelpText(iconButton) == "Copy", "icon transcript action should expose command help text");
        Require(AutomationProperties.GetName(labeledButton) == "Pin", "labeled transcript action should expose its command label");
        Require(AutomationProperties.GetHelpText(labeledButton) == "Pin", "labeled transcript action should expose command help text");
        Require(iconButton.MinWidth >= 40 && iconButton.MinHeight >= 40, "standard icon actions should use a forgiving pointer target");
        Require(labeledButton.MinHeight >= 40, "standard labeled actions should use a forgiving pointer target");
        Require(coordinator.TrackedButtonCount == 2, "new transcript actions should be tracked for busy-state updates");

        iconButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Require(coordinator.TrackedButtonCount == 1, "an unrealized transcript card should release its action-button registration");
        busy = true;
        coordinator.UpdateBusyState(busy: true);
        iconButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Require(coordinator.TrackedButtonCount == 2, "a recycled transcript card should restore its action-button registration when realized");
        Require(!iconButton.IsEnabled, "a reloaded transcript action should immediately adopt a busy state that changed while it was unrealized");

        iconButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        busy = false;
        coordinator.UpdateBusyState(busy: false);
        Require(labeledButton.IsEnabled, "released busy state should restore enabled realized actions");
        iconButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Require(iconButton.IsEnabled, "a reloaded transcript action should immediately adopt an idle state that changed while it was unrealized");

        var listSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/TranscriptListCoordinator.cs"));
        Require(
            listSource.Contains("transcriptActions.Prune()", StringComparison.Ordinal)
            && !listSource.Contains("transcriptActions.Clear()", StringComparison.Ordinal),
            "incremental transcript refresh should prune dead action registrations without dropping live recycled cards");
    });
}

static void TranscriptExportCoordinatorPreviewsScope()
{
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok"),
        TranscriptForTest(2, "Beta", "beta", "message", "ok"),
        TranscriptForTest(3, "Narrator", "narrator", "narration", "ok")
    };

    var empty = TranscriptExportCoordinator.ExportScopeState([], source => source);
    var all = TranscriptExportCoordinator.ExportScopeState(messages, source => source);
    var filtered = TranscriptExportCoordinator.ExportScopeState(messages, source => source.Where(message => message.Turn == 2));
    var noneVisible = TranscriptExportCoordinator.ExportScopeState(messages, _ => []);

    Require(empty.Label == "", "empty export scope should not reserve top-bar text");
    Require(empty.ToolTip.Contains("No transcript", StringComparison.OrdinalIgnoreCase), "empty export scope should explain missing transcript");
    Require(all.Label == "Export: all 3", "full export scope should name all messages");
    Require(all.ToolTip.Contains("all 3", StringComparison.OrdinalIgnoreCase), "full export scope should explain complete export");
    Require(filtered.Label == "Export: 1/3", "filtered export scope should show visible/all count");
    Require(filtered.ToolTip.Contains("turn 2", StringComparison.OrdinalIgnoreCase), "filtered export scope should name the selected turn");
    Require(filtered.ToolTip.Contains("not the full transcript", StringComparison.OrdinalIgnoreCase), "filtered export scope should warn about partial export");
    Require(noneVisible.Label == "Export: all 3", "empty visible filter should fall back to all messages");
    Require(TranscriptExportCoordinator.ExportScopeDescription(3, 0) == "all", "scope description should fall back to all when no visible messages exist");
    Require(TranscriptExportCoordinator.ExportScopeDescription(3, 3) == "all", "scope description should say all when counts match");
    Require(TranscriptExportCoordinator.ExportScopeDescription(3, 1) == "visible", "scope description should say visible for partial exports");
}

static void ShellFileExportReplacesReadOnlyTargets()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var target = Path.Combine(root, "export.md");
    try
    {
        File.WriteAllText(target, "old");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);

        Require(ShellFileExport.TryWriteAllText(target, "new", out var error), $"export helper failed: {error}");
        Require(File.ReadAllText(target) == "new", "export helper should replace the target contents");
        Require((File.GetAttributes(target) & FileAttributes.ReadOnly) == 0, "export helper should clear read-only targets");
        Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "export helper should clean temporary files");

        File.WriteAllText(target, "locked original");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        using (var lockedTarget = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(!ShellFileExport.TryWriteAllText(target, "must not replace", out var lockedError), "a sharing violation should fail the export");
            Require(!string.IsNullOrWhiteSpace(lockedError), "failed export should return the filesystem error");
            Require(File.ReadAllText(target) == "locked original", "failed export should preserve the original contents");
            Require((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0, "failed export should restore the original read-only attribute");
        }

        Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "failed export should also clean temporary files");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }
}

static void ShellProcessLauncherReportsFailures()
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "ai-arena-test-target",
        UseShellExecute = true
    };

    var failed = ShellProcessLauncher.TryStart(
        startInfo,
        out var failure,
        _ => throw new InvalidOperationException("launcher blocked"));
    Require(!failed, "process launcher should report injected failures");
    Require(failure.Contains("launcher blocked", StringComparison.Ordinal), "process launcher should return the launch error");

    var trackedProcess = new DisposalTrackingProcess();
    var launched = ShellProcessLauncher.TryStart(startInfo, out var success, _ => trackedProcess);
    Require(launched, "process launcher should succeed when the injected launcher succeeds");
    Require(success == "", "successful launch should clear the error text");
    Require(trackedProcess.DisposeCalled, "process launcher should release the returned process handle after launch");
}

static void AgentPerformanceCoordinatorAggregatesNativeTelemetry()
{
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { TokensPerSecond = 0, TimeToFirstTokenMs = 0 },
        TranscriptForTest(2, "Alpha", "alpha", "message", "ok") with { TokensPerSecond = 20.25, TimeToFirstTokenMs = 180 },
        TranscriptForTest(3, "Alpha", "alpha", "message", "ok") with { TokensPerSecond = 41.75, TimeToFirstTokenMs = 320 }
    };

    Require(Math.Abs(AgentPerformanceCoordinator.AverageTokensPerSecond(messages) - 31.0) < 0.001, "agent performance should average native token speed samples");
    Require(AgentPerformanceCoordinator.AverageTimeToFirstTokenMs(messages) == 250, "agent performance should average native TTFT samples");
    Require(AgentPerformanceCoordinator.AverageTokensPerSecond([]) == 0, "empty native speed samples should be zero");
    Require(AgentPerformanceCoordinator.AverageTimeToFirstTokenMs([]) == 0, "empty native TTFT samples should be zero");
}

static void MetricSparklineControlMeasuresResponsively()
{
    Require(MetricSparklineControl.NormalizeValue(50, 100) == 0.5, "sparkline normalization should scale finite values");
    Require(MetricSparklineControl.NormalizeValue(200, 100) == 1, "sparkline normalization should clamp high values");
    Require(MetricSparklineControl.NormalizeValue(-5, 100) == 0, "sparkline normalization should clamp negative values");
    Require(MetricSparklineControl.NormalizeValue(5, double.NaN) == 1, "sparkline normalization should survive NaN max values");
    Require(MetricSparklineControl.NormalizeValue(double.NaN, 100) == 0, "sparkline normalization should ignore NaN samples");

    RunStaTest(() =>
    {
        var stretched = new MetricSparklineControl();
        stretched.Measure(new Size(240, 44));
        Require(Math.Abs(stretched.DesiredSize.Width - 240) < 0.001, "sparkline should accept finite offered width");
        Require(Math.Abs(stretched.DesiredSize.Height - 44) < 0.001, "sparkline should accept finite offered height");

        var fixedSize = new MetricSparklineControl
        {
            Width = 62,
            Height = 16
        };
        fixedSize.Measure(new Size(240, 44));
        Require(Math.Abs(fixedSize.DesiredSize.Width - 62) < 0.001, "sparkline explicit width should win over offered width");
        Require(Math.Abs(fixedSize.DesiredSize.Height - 16) < 0.001, "sparkline explicit height should win over offered height");

        var unbounded = new MetricSparklineControl();
        unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Require(Math.Abs(unbounded.DesiredSize.Width - 88) < 0.001, "sparkline should keep a compact default width when unconstrained");
        Require(Math.Abs(unbounded.DesiredSize.Height - 34) < 0.001, "sparkline should keep a compact default height when unconstrained");
    });
}

}
