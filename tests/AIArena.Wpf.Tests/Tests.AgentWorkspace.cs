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
static void AgentRunbookPersistsStableWorkflowAndRecoversInterruptions()
{
    var now = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
    var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    var runbook = new AgentRunbookService();
    runbook.Begin(workspace, "Build and verify a small app.", builderOnly: false, now);

    Require(runbook.State.RunId.StartsWith("run-", StringComparison.Ordinal), "runbook should assign a stable run id");
    Require(runbook.State.Steps.Select(step => step.Id).SequenceEqual(["plan", "review", "build", "approval", "execute", "verify"]), "runbook should expose the six stable workflow step ids");
    Require(runbook.State.Steps.Single(step => step.Id == "execute").DependsOn.SequenceEqual(["approval"]), "runbook execute step should retain approval dependency");
    runbook.UpdateStep("planner", "Running", "Planner started.", now.AddMinutes(1));
    Require(runbook.State.Steps.Single(step => step.Id == "plan").Status == "Pending", "runbook service should require canonical stable ids");
    runbook.UpdateStep("plan", "Completed", "Plan captured.", now.AddMinutes(2));
    runbook.MarkApprovalReady("PowerShell: dotnet build", now.AddMinutes(3));
    Require(runbook.State.Status == "Awaiting approval", "approval checkpoint should put the runbook into awaiting approval state");
    Require(runbook.State.Checkpoints.Any(checkpoint => checkpoint.Kind == "approval"), "approval readiness should create a durable checkpoint");
    runbook.MarkExecutionStarted("PowerShell: dotnet build", now.AddMinutes(4));
    runbook.MarkExecutionFinished(ok: true, canceled: false, "Files: +1 created", now.AddMinutes(5));
    Require(runbook.State.Status == "Needs verification", "successful execution should require a visible verification step");
    Require(runbook.State.Steps.Single(step => step.Id == "verify").Status == "Waiting", "verification should wait after a successful receipt");
    runbook.MarkCompleted("Verification passed.", now.AddMinutes(6));
    Require(runbook.State.Status == "Completed", "successful verification should complete the runbook");

    var builderOnly = new AgentRunbookService();
    builderOnly.Begin(workspace, "Fast edit", builderOnly: true, now);
    Require(builderOnly.State.Steps.Where(step => step.Id is "plan" or "review").All(step => step.Status == "Skipped"), "builder-only runbooks should explicitly skip planner and reviewer");
    builderOnly.UpdateStep("build", "Running", "Builder active.", now.AddMinutes(1));
    var restored = new AgentRunbookService();
    restored.Restore(builderOnly.State, workspace, now.AddMinutes(2));
    Require(restored.State.Status == "Interrupted", "restoring a running step should mark the runbook interrupted");
    Require(restored.State.Steps.Single(step => step.Id == "build").Status == "Blocked", "interrupted running steps should recover as blocked rather than silently rerun");
    Require(restored.State.Checkpoints.Any(checkpoint => checkpoint.Kind == "interrupted"), "interrupted recovery should create an audit checkpoint");

    for (var index = 0; index < AgentRunbookService.MaxCheckpoints + 5; index++)
    {
        restored.AddCheckpoint("test", $"checkpoint {index}", now.AddMinutes(index + 3));
    }

    Require(restored.State.Checkpoints.Count == AgentRunbookService.MaxCheckpoints, "runbook checkpoint history should stay bounded");
    Require(AgentRunbookService.IsGeneratedContinuationPrompt("Latest work brief:\nfiles changed"), "work-brief follow-ups should continue the current runbook");

    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings { AgentRunbook = restored.State });
        var loaded = store.Load();
        Require(loaded.AgentRunbook.RunId == restored.State.RunId, "runbook id should persist across settings reload");
        Require(loaded.AgentRunbook.Steps.Count == restored.State.Steps.Count, "runbook steps should persist across settings reload");
        Require(loaded.AgentRunbook.Checkpoints.Count == AgentRunbookService.MaxCheckpoints, "persisted runbook checkpoints should retain the bounded audit window");
    });

    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    Require(xaml.Contains("Text=\"Runbook\"", StringComparison.Ordinal), "Agent right rail should label the durable workflow as Runbook");
    Require(xaml.Contains("x:Name=\"AgentRunbookMetaText\"", StringComparison.Ordinal), "Agent runbook should expose visible identity and checkpoint metadata");
}

static void AgentWorkspaceEmptyStateStaysCompact()
{
    RunStaTest(() =>
    {
        var stagedTemplate = "";
        var card = AgentWorkspaceCoordinator.BuildEmptyStateCard(
            AccentResourceBrush,
            templateId => stagedTemplate = templateId);

        Require(card.MaxWidth <= 520, "Agent welcome card should not exceed the compact 520-DIP content width");
        Require(card.Padding.Left <= 16 && card.Padding.Top <= 16 && card.Padding.Right <= 16 && card.Padding.Bottom <= 16, "Agent welcome card padding should remain at or below 16 DIP");
        Require(card.Margin.Top <= 24, "Agent welcome card should not use a large fixed top offset");

        var content = card.Child as StackPanel;
        Require(content is not null && content.Children.Count == 3, "Agent welcome should contain only a title, sentence, and starter actions");
        Require(content!.Children[0] is TextBlock title && title.Text == "Start a software task", "Agent welcome should use a concise task title instead of a faux message header");
        Require(content.Children[1] is TextBlock sentence && !string.IsNullOrWhiteSpace(sentence.Text), "Agent welcome should include one concise guidance sentence");
        Require(content.Children[2] is WrapPanel, "Agent welcome should end with starter actions");
        var actions = (WrapPanel)content.Children[2];
        Require(actions.Children.Count is > 0 and <= 3, "Agent welcome should expose no more than three starter actions");
        Require(actions.Children.OfType<Button>().All(button => button.MinHeight <= 32 && button.Padding.Top <= 4), "Agent starter actions should use compact button metrics");

        ((Button)actions.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(stagedTemplate == "breakdown", "Agent welcome actions should stage the matching prompt template");

        card.Measure(new Size(520, double.PositiveInfinity));
        Require(540 - card.DesiredSize.Height >= 360, "Agent welcome should leave enough of a 540-DIP viewport for the composer to remain visible");
    });
}

static void RoleStyleCatalogNormalizesLabelsAndChips()
{
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("Plain-Language") == "plain_language", "voice style should normalize dashes");
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("legal policy") == "legal_policy", "voice style should normalize spaces");
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("mystery") == "default", "unknown voice style should fall back to default");
    Require(RoleStyleCatalog.VoiceStyleChipText("default") == "", "default voice style should not render a chip");
    Require(RoleStyleCatalog.VoiceStyleChipText("evidence-ledger") == "Voice: Evidence", "voice style chip should use display label");

    Require(RoleStyleCatalog.NormalizeAgentPressureTag("Risk") == "risk", "pressure should normalize case");
    Require(RoleStyleCatalog.NormalizeAgentPressureTag("too much") == "default", "unknown pressure should fall back to default");
    Require(RoleStyleCatalog.AgentPressureChipText(null) == "", "default pressure should not render a chip");
    Require(RoleStyleCatalog.AgentPressureChipText("chaos") == "Pressure: Chaos", "pressure chip should use display label");

    Require(RoleStyleCatalog.IsStrictVoiceStyle("evidence ledger"), "evidence ledger should be strict");
    Require(!RoleStyleCatalog.IsStrictVoiceStyle("idioms"), "idioms should not be strict");
}
static void RoleStyleCatalogFormatsVoiceAdherenceCues()
{
    Require(RoleStyleCatalog.VoiceAdherenceState(74, 1) == "strong", "74 should be strong");
    Require(RoleStyleCatalog.VoiceAdherenceState(46, 1) == "drifting", "46 should be drifting");
    Require(RoleStyleCatalog.VoiceAdherenceState(45, 1) == "broken", "45 should be broken");
    Require(RoleStyleCatalog.VoiceAdherenceState(99, 0) == "none", "empty samples should be none");
    Require(RoleStyleCatalog.VoiceAdherenceDisplayState("drifting") == "partial cues", "drifting display label should be stable");

    var diagnostic = new VoiceAdherenceDiagnostic(
        "bullet_only",
        "Bullet-only",
        "broken",
        12,
        "Needs bullet structure.",
        ["sentence form"],
        ["bullet markers"]);

    Require(RoleStyleCatalog.VoiceAdherenceChipText(diagnostic) == "Cues: low 12", "adherence chip text should be stable");
    var tooltip = RoleStyleCatalog.VoiceAdherenceTooltip(diagnostic);
    Require(tooltip.Contains("Needs bullet structure.", StringComparison.OrdinalIgnoreCase), "tooltip should include summary");
    Require(tooltip.Contains("Evidence: sentence form", StringComparison.OrdinalIgnoreCase), "tooltip should include evidence");
    Require(tooltip.Contains("Missing: bullet markers", StringComparison.OrdinalIgnoreCase), "tooltip should include missing cues");
}

static void AgentMemoryCoordinatorNormalizesNotes()
{
    var longNote = new string('x', 450);
    var raw = string.Join(
        Environment.NewLine,
        "  alpha note  ",
        "ALPHA NOTE",
        "",
        longNote,
        string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(index => $"note {index:00}")));

    var notes = AgentMemoryCoordinator.NormalizeMemoryNotes(raw);

    Require(notes.Count == 60, "memory notes should cap at 60 entries");
    Require(notes[0] == "alpha note", "memory notes should trim whitespace");
    Require(notes.Count(note => note.Equals("alpha note", StringComparison.OrdinalIgnoreCase)) == 1, "memory notes should dedupe case-insensitively");
    Require(notes[1].Length == 400, "memory notes should truncate long lines");
    Require(!notes.Any(string.IsNullOrWhiteSpace), "memory notes should omit blank lines");
}

static void AgentBoardCoordinatorFormatsStatuses()
{
    Require(AgentBoardCoordinator.DisplayInlineStatus("") == "-", "blank inline status should be placeholder");
    Require(AgentBoardCoordinator.DisplayInlineStatus(" Thinking ") == "thinking", "inline status should trim and lower");
    Require(AgentBoardCoordinator.IsAgentWorkingStatus("busy"), "busy should count as working");
    Require(AgentBoardCoordinator.IsAgentWorkingStatus(" generating "), "generating should count as working");
    Require(!AgentBoardCoordinator.IsAgentWorkingStatus("waiting"), "waiting should not count as working");
    Require(!AgentBoardCoordinator.ModeActionEnabled(busy: true, autoChatRunning: false), "mode actions should disable during normal busy work");
    Require(AgentBoardCoordinator.ModeActionEnabled(busy: true, autoChatRunning: true), "mode actions should stay available during auto chat");
    Require(AgentBoardCoordinator.ShouldAnimateActivity(systemAnimationsEnabled: true, isRunning: true), "working agents may animate when Windows animations are enabled");
    Require(!AgentBoardCoordinator.ShouldAnimateActivity(systemAnimationsEnabled: false, isRunning: true), "reduced-motion preferences should suppress peripheral activity sweeps");
    Require(!AgentBoardCoordinator.ShouldAnimateActivity(systemAnimationsEnabled: true, isRunning: false), "idle agents should never animate");
}

static void AgentBoardCoordinatorDisablesModeButtonsWhileBusy()
{
    RunStaTest(() =>
    {
        var autoChatRunning = false;
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var panel = new StackPanel();
            Brush testBrush(string key) => key.Contains("Danger", StringComparison.OrdinalIgnoreCase)
                ? Brushes.IndianRed
                : key.Contains("Muted", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.LightSteelBlue
                    : Brushes.DeepSkyBlue;
            var coordinator = new AgentBoardCoordinator(
                new SessionStore(root),
                new EventLogStore(root),
                panel,
                () => null,
                () => false,
                () => autoChatRunning,
                testBrush,
                (left, _, _) => left,
                testBrush,
                value => value,
                _ => Task.CompletedTask,
                (_, _) => { },
                (_, _, action, _) => action(),
                (_, _) => Task.CompletedTask,
                _ => Task.CompletedTask,
                _ => { });

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "Persona", "default", "", "#35D6FF", "alpha-model", true, false, [])
                ]);
            coordinator.Populate(snapshot, "alpha");

            var buttons = DescendantButtons(panel).ToArray();
            Require(buttons.Length == 3, $"expected one agent run action, one overflow action, and the narrator action, got {buttons.Length}");
            Require(buttons.All(button => button.IsEnabled), "buttons should start enabled when arena is idle");
            var runButton = ButtonByToolTip(buttons, "Run one turn");
            var overflowButton = ButtonByToolTip(buttons, "More actions");
            var overflowMenu = overflowButton.ContextMenu
                ?? throw new InvalidOperationException("the compact agent action should expose a standard keyboard-accessible context menu");
            var modeItems = overflowMenu.Items.OfType<MenuItem>().ToArray();
            var pauseItem = modeItems.Single(item => (item.Header?.ToString() ?? "").Equals("Pause agent", StringComparison.Ordinal));
            var soloItem = modeItems.Single(item => (item.Header?.ToString() ?? "").Equals("Solo agent", StringComparison.Ordinal));

            Require(runButton.MinWidth == 32 && runButton.MinHeight == 32,
                "the primary per-agent turn action should use the compact desktop target");
            Require(overflowButton.MinWidth == 32 && overflowButton.MinHeight == 32,
                "secondary live-agent actions should collapse into one compact overflow target");
            Require(pauseItem.MinHeight == 32 && soloItem.MinHeight == 32,
                "overflow commands should retain compact keyboard and pointer targets");
            Require(ButtonByToolTip(buttons, "Narrate now").MinWidth == 32 && ButtonByToolTip(buttons, "Narrate now").MinHeight == 32,
                "the narrator action should match the compact primary control-rail target");
            var identityLines = DescendantTextBlocks(panel).Select(text => text.Text).ToArray();
            Require(identityLines.Contains("alpha", StringComparer.OrdinalIgnoreCase), "the full agent identity should have its own unshared line");
            Require(identityLines.Any(text => text.StartsWith("current", StringComparison.OrdinalIgnoreCase)), "the visible secondary line should lead with the agent state");

            coordinator.UpdateBusyState(true);
            Require(buttons.All(button => !button.IsEnabled), "all live-agent buttons should disable during normal busy work");
            Require(modeItems.All(item => !item.IsEnabled), "agent mode commands should disable during normal busy work");

            autoChatRunning = true;
            coordinator.UpdateBusyState(true);
            Require(!runButton.IsEnabled, "agent turn button should remain disabled during auto chat");
            Require(overflowButton.IsEnabled, "agent overflow should stay available for mode changes during auto chat");
            Require(pauseItem.IsEnabled, "pause command should stay enabled during auto chat");
            Require(soloItem.IsEnabled, "solo command should stay enabled during auto chat");
            Require(ButtonByToolTip(buttons, "Narrate now").IsEnabled, "narrator button should stay enabled during auto chat");
            Require(AutomationProperties.GetName(runButton) == "Run one turn for Alpha", "agent run button should expose an automation name");
            Require(AutomationProperties.GetName(overflowButton) == "More actions for Alpha", "agent overflow button should expose an automation name");
            Require(AutomationProperties.GetName(pauseItem) == "Pause Alpha", "agent pause command should expose an automation name");
            Require(AutomationProperties.GetName(soloItem) == "Solo Alpha", "agent solo command should expose an automation name");
            Require(AutomationProperties.GetName(ButtonByToolTip(buttons, "Narrate now")) == "Narrate now", "narrator action button should expose an automation name");

            var pausedSnapshot = snapshot with
            {
                Agents =
                [
                    new AgentState("alpha", "Alpha", "muted", "Persona", "default", "", "#35D6FF", "alpha-model", false, false, [])
                ]
            };
            coordinator.Populate(pausedSnapshot, null);
            var pausedButtons = DescendantButtons(panel).ToArray();
            var resumeButton = ButtonByToolTip(pausedButtons, "Resume Alpha");
            Require(resumeButton.MinWidth == 32 && resumeButton.IsEnabled, "a paused agent should replace the inline run action with a compact Resume action");
            Require(AutomationProperties.GetName(resumeButton) == "Resume Alpha", "the paused primary action should expose its Resume outcome to UI Automation");
            var pausedMenu = ButtonByToolTip(pausedButtons, "More actions").ContextMenu
                ?? throw new InvalidOperationException("paused agents should retain their overflow menu");
            Require(!pausedMenu.Items.OfType<MenuItem>().Any(item => (item.Header?.ToString() ?? "").Equals("Pause agent", StringComparison.Ordinal)),
                "a paused row should not duplicate Resume inside its overflow menu");
            Require(pausedMenu.Items.OfType<MenuItem>().Any(item => (item.Header?.ToString() ?? "").Equals("Solo agent", StringComparison.Ordinal)),
                "a paused row should preserve the Solo callback in overflow");
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

static void AgentWorkspaceCommandPreviewGatesExecution()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-command-preview", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "src"));
    try
    {
        var terminal = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "dotnet --info");
        Require(terminal.Ok, $"terminal preview should be valid: {terminal.Error}");
        Require(terminal.Executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase), "terminal preview should run through cmd.exe");
        Require(terminal.Arguments.Contains("/c", StringComparison.OrdinalIgnoreCase), "terminal preview should use cmd execution arguments");
        Require(terminal.WorkspacePath.Equals(AgentWorkspaceCommand.NormalizeWorkspacePath(root, out _), StringComparison.OrdinalIgnoreCase), "preview should normalize workspace path");
        Require(terminal.DisplayInvocation.Contains("dotnet --info", StringComparison.Ordinal), "preview should show the proposed command");

        var powershell = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Get-ChildItem .");
        Require(powershell.Ok, $"PowerShell preview should be valid: {powershell.Error}");
        Require(powershell.Executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase), "PowerShell preview should use powershell.exe");
        Require(powershell.Arguments.Contains("-EncodedCommand", StringComparison.Ordinal), "PowerShell preview should use encoded command arguments");
        Require(powershell.DisplayInvocation.Contains("Get-ChildItem .", StringComparison.Ordinal), "PowerShell preview should display the readable command");
        var insideAbsoluteWithSpaces = Path.Combine(root, "folder with space", "inside file.txt");
        var allowedInsideAbsolute = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", $"Set-Content -Path \"{insideAbsoluteWithSpaces}\" -Value ok");
        Require(allowedInsideAbsolute.Ok, $"absolute paths inside the workspace should pass preview: {allowedInsideAbsolute.Error}");

        var literalAutoWrite = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Content -Path .\\safe.txt -Value 'safe value'");
        Require(AgentWorkspaceCommand.CanRunAutomatically(literalAutoWrite, out var literalAutoReason), $"literal workspace write should remain eligible for Full Access: {literalAutoReason}");

        var nestedShellEscape = AgentWorkspaceCommand.BuildPreview(
            root,
            "PowerShell",
            "powershell.exe -NoProfile -Command \"Set-Content -Path $env:TEMP\\arena-escape.txt -Value escaped\"");
        Require(nestedShellEscape.Ok, "nested shell escape should remain available for explicit manual approval");
        Require(!AgentWorkspaceCommand.CanRunAutomatically(nestedShellEscape, out var nestedReason), "Full Access must not auto-run nested shells that bypass string path checks");
        Require(nestedReason.Contains("explicit approval", StringComparison.OrdinalIgnoreCase), "nested shell block should explain the manual approval boundary");

        var interpreterEscape = AgentWorkspaceCommand.BuildPreview(
            root,
            "PowerShell",
            "python -c \"import os; open(os.path.join(os.environ['TEMP'], 'arena-escape.txt'), 'w').write('escaped')\"");
        Require(interpreterEscape.Ok, "interpreter command should remain available for explicit manual approval");
        Require(!AgentWorkspaceCommand.CanRunAutomatically(interpreterEscape, out _), "Full Access must not auto-run interpreter expressions");

        var substitutionRead = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Write-Host $(Get-Content $env:USERPROFILE\\secret.txt)");
        Require(substitutionRead.Ok, "substitution command should remain available for explicit manual approval");
        Require(!AgentWorkspaceCommand.CanRunAutomatically(substitutionRead, out _), "Full Access must not auto-run command substitutions");

        var parentRead = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Get-Content ..\\outside.txt");
        Require(parentRead.Ok, "parent read should remain available for explicit manual approval");
        Require(!AgentWorkspaceCommand.CanRunAutomatically(parentRead, out _), "Full Access must not auto-run parent-path reads");

        var canonicalWriteCommand = AgentWorkspaceCoordinator.BuildFileWriteCommand(
            new AgentWorkspaceCoordinator.AgentFileSuggestion(
                [new AgentWorkspaceCoordinator.AgentSuggestedFile("src/generated.txt", "generated safely", "txt")]));
        var canonicalWrite = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", canonicalWriteCommand);
        Require(canonicalWrite.Ok, $"Arena file materializer should preview: {canonicalWrite.Error}");
        Require(AgentWorkspaceCommand.CanRunAutomatically(canonicalWrite, out var canonicalReason), $"canonical Arena file materializer should remain eligible for Full Access: {canonicalReason}");

        var blockedParent = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Location ..");
        Require(!blockedParent.Ok, "commands that move above the workspace should be blocked");
        Require(blockedParent.Error.Contains("above", StringComparison.OrdinalIgnoreCase), "parent traversal block should explain the boundary");

        var blockedParentWrite = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Content ..\\outside.txt -Value nope");
        Require(!blockedParentWrite.Ok, "commands that write to parent paths should be blocked");
        Require(blockedParentWrite.Risks.Contains("Outside workspace"), "parent write previews should include an outside-workspace risk chip");

        var blockedParentRedirect = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "echo nope > ..\\outside.txt");
        Require(!blockedParentRedirect.Ok, "terminal redirection to parent paths should be blocked");

        var blockedParentCopy = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Copy-Item .\\src\\file.txt ..\\outside.txt");
        Require(!blockedParentCopy.Ok, "copying workspace files to parent paths should be blocked");

        var blockedDotnetOutput = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "dotnet new console -o ..\\outside-app");
        Require(!blockedDotnetOutput.Ok, "dotnet scaffold output above the workspace should be blocked");

        var blockedDotnetEqualsOutput = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "dotnet new console --output=..\\outside-app");
        Require(!blockedDotnetEqualsOutput.Ok, "dotnet scaffold output equals syntax above the workspace should be blocked");

        var allowedDotnetOutput = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "dotnet new console --output .\\TinyApp");
        Require(allowedDotnetOutput.Ok, $"workspace-relative dotnet output should remain valid: {allowedDotnetOutput.Error}");

        var blockedNpmCreate = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "npm create vite@latest ..\\outside-app -- --template react");
        Require(!blockedNpmCreate.Ok, "npm create positional output above the workspace should be blocked");

        var allowedNpmCreate = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "npm create vite@latest TinyApp -- --template react");
        Require(allowedNpmCreate.Ok, $"workspace-relative npm create targets should remain valid: {allowedNpmCreate.Error}");

        var blockedNpxCreate = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "npx create-react-app ..\\outside-app");
        Require(!blockedNpxCreate.Ok, "npx create app targets above the workspace should be blocked");

        var blockedPnpmCreate = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "pnpm create vite ..\\outside-app");
        Require(!blockedPnpmCreate.Ok, "pnpm create targets above the workspace should be blocked");

        var blockedYarnCreate = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "yarn create vite ..\\outside-app");
        Require(!blockedYarnCreate.Ok, "yarn create targets above the workspace should be blocked");

        var blockedCargoNew = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "cargo new ..\\outside-app");
        Require(!blockedCargoNew.Ok, "cargo new targets above the workspace should be blocked");

        var blockedNgNew = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "ng new ..\\outside-app");
        Require(!blockedNgNew.Ok, "ng new targets above the workspace should be blocked");

        var blockedOutDir = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "vite build --out-dir=..\\dist");
        Require(!blockedOutDir.Ok, "out-dir options above the workspace should be blocked");

        var blockedDestination = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "tool --destination ..\\outside");
        Require(!blockedDestination.Ok, "destination options above the workspace should be blocked");

        var blockedPrefix = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "npm install --prefix ..\\outside left-pad");
        Require(!blockedPrefix.Ok, "prefix options above the workspace should be blocked");

        var blockedGitDirectory = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "git -C .. status");
        Require(!blockedGitDirectory.Ok, "git -C above the workspace should be blocked");

        var blockedCloneTarget = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "git clone https://example.invalid/repo.git ..\\clone-target");
        Require(!blockedCloneTarget.Ok, "git clone targets above the workspace should be blocked");

        var outsidePath = Path.Combine(Path.GetTempPath(), "ai-arena-outside.txt");
        var blockedOutside = AgentWorkspaceCommand.BuildPreview(root, "Terminal", $"type \"{outsidePath}\"");
        Require(!blockedOutside.Ok, "commands that reference absolute paths outside the workspace should be blocked");
        Require(blockedOutside.Risks.Contains("Outside workspace"), "outside path previews should include a risk chip");

        var blockedForwardSlashOutside = AgentWorkspaceCommand.BuildPreview(root, "Terminal", $"type \"{outsidePath.Replace('\\', '/')}\"");
        Require(!blockedForwardSlashOutside.Ok, "forward-slash Windows absolute paths outside the workspace should be blocked");

        var blockedEnvWrite = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Content -Path $env:TEMP\\ai-arena-escape.txt -Value nope");
        Require(!blockedEnvWrite.Ok, "commands that build write paths from environment variables should be blocked");
        Require(blockedEnvWrite.Risks.Contains("Outside workspace"), "dynamic environment write previews should include an outside-workspace risk chip");

        var blockedGeneratedTempWrite = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Content -Path ([System.IO.Path]::GetTempPath() + 'ai-arena-escape.txt') -Value nope");
        Require(!blockedGeneratedTempWrite.Ok, "commands that build write paths from temp helpers should be blocked");

        var destructive = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Remove-Item .\\bin -Recurse -Force");
        Require(destructive.Ok, $"workspace-relative destructive commands should preview for explicit approval: {destructive.Error}");
        Require(destructive.Risks.Contains("Destructive"), "destructive commands should be visibly risk flagged");

        var npmStart = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "npm start");
        Require(npmStart.Ok, $"npm start should remain previewable for manual approval: {npmStart.Error}");
        Require(npmStart.Risks.Contains("Long-running"), "npm start should be flagged as a long-running app preview");

        var dotnetRun = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "dotnet run --project .\\TinyApp");
        Require(dotnetRun.Ok, $"dotnet run should remain previewable for manual approval: {dotnetRun.Error}");
        Require(dotnetRun.Risks.Contains("Long-running"), "dotnet run should be flagged as a long-running app preview");

        var staticServer = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "python -m http.server 5173");
        Require(staticServer.Ok, $"python static server should remain previewable for manual approval: {staticServer.Error}");
        Require(staticServer.Risks.Contains("Long-running"), "python static servers should be flagged as long-running previews");

        Require(AgentWorkspaceCommand.IsInsideWorkspace(root, Path.Combine(root, "src", "file.cs")), "child paths should stay inside workspace");
        Require(!AgentWorkspaceCommand.IsInsideWorkspace(root, Path.GetTempPath()), "sibling temp paths should not be inside workspace");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceCommandRunnerCapturesTerminalOutput()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-command-runner", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var preview = AgentWorkspaceCommand.BuildPreview(root, "Terminal", "echo AI_ARENA_AGENT_OK");
        Require(preview.Ok, $"command preview should be valid before running: {preview.Error}");
        var result = AgentWorkspaceCommand.RunAsync(preview, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

        Require(result.Ok, $"approved command should complete successfully: {result.Error} {result.StandardError}");
        Require(result.ExitCode == 0, "approved command should report exit code 0");
        Require(result.WorkingDirectory.Equals(preview.WorkspacePath, StringComparison.OrdinalIgnoreCase), "command result should report the preview working directory");
        Require(result.StandardOutput.Contains("AI_ARENA_AGENT_OK", StringComparison.Ordinal), "command runner should capture stdout");
        Require(!result.TimedOut, "quick command should not time out");

        var noisyPreview = AgentWorkspaceCommand.BuildPreview(
            root,
            "PowerShell",
            "$out = 'O' * 140000; $err = 'E' * 140000; [Console]::Out.Write($out); [Console]::Error.Write($err)");
        Require(noisyPreview.Ok, $"noisy command preview should be valid: {noisyPreview.Error}");
        var noisyResult = AgentWorkspaceCommand.RunAsync(noisyPreview, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        var boundedLength = AgentWorkspaceCommand.MaxCapturedStreamChars + 96;
        Require(noisyResult.Ok, $"noisy command should still exit successfully: {noisyResult.Error}");
        Require(noisyResult.StandardOutput.Length <= boundedLength, "stdout capture should be bounded before UI display truncation");
        Require(noisyResult.StandardError.Length <= boundedLength, "stderr capture should be bounded before UI display truncation");
        Require(noisyResult.StandardOutput.Contains(AgentWorkspaceCommand.StreamTruncatedMarker, StringComparison.Ordinal), "stdout should disclose command output truncation");
        Require(noisyResult.StandardError.Contains(AgentWorkspaceCommand.StreamTruncatedMarker, StringComparison.Ordinal), "stderr should disclose command output truncation");
        Require(noisyResult.StandardOutput.StartsWith("OOOO", StringComparison.Ordinal), "stdout should preserve the beginning of captured output");
        Require(noisyResult.StandardError.StartsWith("EEEE", StringComparison.Ordinal), "stderr should preserve the beginning of captured output");

        var cancelPreview = AgentWorkspaceCommand.BuildPreview(root, "PowerShell", "Set-Content -Path .\\before-cancel.txt -Value started; Start-Sleep -Seconds 10; Set-Content -Path .\\after-cancel.txt -Value done");
        Require(cancelPreview.Ok, $"cancellable command preview should be valid: {cancelPreview.Error}");
        using var cancelSource = new CancellationTokenSource();
        var cancelTask = AgentWorkspaceCommand.RunAsync(cancelPreview, TimeSpan.FromSeconds(30), cancelSource.Token);
        var beforePath = Path.Combine(root, "before-cancel.txt");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(beforePath) && !cancelTask.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            Thread.Sleep(50);
        }

        Require(File.Exists(beforePath), "cancellation test should wait until the command has written its first file");
        cancelSource.Cancel();
        var cancelled = cancelTask.GetAwaiter().GetResult();
        Require(!cancelled.Ok, "cancelled command should not report success");
        Require(cancelled.Canceled, "cancelled command should report cancellation separately from timeout");
        Require(!cancelled.TimedOut, "user cancellation should not be reported as timeout");
        Require(cancelled.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase), "cancelled command should explain the cancellation");
        Require(File.Exists(beforePath), "cancelled command should preserve file changes made before cancellation");
        Require(!File.Exists(Path.Combine(root, "after-cancel.txt")), "cancelled command should kill the process before later file writes");

        var shutdownPreview = AgentWorkspaceCommand.BuildPreview(
            root,
            "PowerShell",
            "Set-Content -Path .\\before-shutdown.txt -Value started; Start-Sleep -Seconds 10; Set-Content -Path .\\after-shutdown.txt -Value done");
        Require(shutdownPreview.Ok, $"shutdown command preview should be valid: {shutdownPreview.Error}");
        var shutdownTask = AgentWorkspaceCommand.RunAsync(shutdownPreview, TimeSpan.FromSeconds(30));
        var shutdownMarker = Path.Combine(root, "before-shutdown.txt");
        watch.Restart();
        while (!File.Exists(shutdownMarker) && !shutdownTask.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            Thread.Sleep(50);
        }

        Require(File.Exists(shutdownMarker), "shutdown test should wait until the child command is active");
        Require(AgentWorkspaceCommand.ActiveProcessCount > 0, "active command should be registered for app shutdown cleanup");
        Require(AgentWorkspaceCommand.TerminateActiveProcesses() > 0, "app shutdown cleanup should terminate the active command tree");
        var shutdownResult = shutdownTask.GetAwaiter().GetResult();
        Require(!shutdownResult.Ok, "shutdown-terminated command should not report success");
        Require(AgentWorkspaceCommand.ActiveProcessCount == 0, "completed shutdown cleanup should unregister the child process");
        Require(!File.Exists(Path.Combine(root, "after-shutdown.txt")), "app shutdown cleanup should prevent descendant work after close");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceCommandRejectsInvalidTimeouts()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-command-timeout", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var markerPath = Path.Combine(root, "should-not-run.txt");
        var preview = AgentWorkspaceCommand.BuildPreview(
            root,
            "PowerShell",
            "Set-Content -LiteralPath .\\should-not-run.txt -Value launched");
        Require(preview.Ok, $"timeout test command should preview: {preview.Error}");

        var zeroTimeout = AgentWorkspaceCommand.RunAsync(preview, TimeSpan.Zero).GetAwaiter().GetResult();
        var infiniteTimeout = AgentWorkspaceCommand.RunAsync(preview, Timeout.InfiniteTimeSpan).GetAwaiter().GetResult();
        var excessiveTimeout = AgentWorkspaceCommand.RunAsync(preview, TimeSpan.FromDays(30)).GetAwaiter().GetResult();

        foreach (var result in new[] { zeroTimeout, infiniteTimeout, excessiveTimeout })
        {
            Require(!result.Ok, "invalid timeout must not report command success");
            Require(result.ExitCode == -1, "invalid timeout should report that no process exited");
            Require(result.Error.Contains("timeout", StringComparison.OrdinalIgnoreCase), "invalid timeout should return an actionable error");
        }

        Require(!File.Exists(markerPath), "invalid timeouts must be rejected before launching the child process");
        Require(AgentWorkspaceCommand.ActiveProcessCount == 0, "invalid timeouts must not register a child process");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceClipboardHelperHandlesBusyClipboard()
{
    string? copied = null;
    var copiedOk = AgentWorkspaceCoordinator.TrySetClipboardText("copy me", text => copied = text);
    Require(copiedOk, "clipboard helper should report success when the setter succeeds");
    Require(copied == "copy me", "clipboard helper should pass text to the setter");

    var busyOk = AgentWorkspaceCoordinator.TrySetClipboardText(
        "copy me",
        _ => throw new InvalidOperationException("clipboard busy"));
    Require(!busyOk, "clipboard helper should report busy clipboard failures without throwing");
}

static void AgentWorkspaceFileReceiptScanIsBounded()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-receipt-scan", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "ok");
        for (var index = 0; index < AgentWorkspaceCoordinator.MaxWorkspaceDirectoriesInReceipt + 2; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"empty-{index:D4}"));
        }

        var snapshot = AgentWorkspaceCoordinator.CaptureWorkspaceFiles(root);
        Require(snapshot.ScannedLimit, "workspace receipt scanner should mark directory-limited scans");
        Require(snapshot.Files.ContainsKey("tracked.txt"), "workspace receipt scanner should capture root files before walking child directories");

        var receipt = AgentWorkspaceCoordinator.BuildFileReceipt(snapshot, snapshot);
        Require(receipt.ScannedLimit, "directory-limited snapshots should propagate to file receipts");
        Require(AgentWorkspaceCoordinator.ReceiptScanIsLimitedWithoutTrackedChanges(receipt), "limited no-change receipts should not be treated as known no-change");

        var formatted = AgentWorkspaceCoordinator.FormatFileReceipt(receipt);
        Require(formatted.Contains("scan limited", StringComparison.OrdinalIgnoreCase), "limited receipts should disclose scan limits");
        Require(formatted.Contains(AgentWorkspaceCoordinator.MaxWorkspaceDirectoriesInReceipt.ToString(), StringComparison.Ordinal), "limited receipts should include the directory cap");

        Require(
            AgentWorkspaceCoordinator.ShouldSkipWorkspaceReceiptDirectory("linked-workspace", FileAttributes.Directory | FileAttributes.ReparsePoint),
            "workspace receipt scanner should skip reparse-point directories");
        Require(
            AgentWorkspaceCoordinator.ShouldSkipWorkspaceReceiptDirectory("node_modules", FileAttributes.Directory),
            "workspace receipt scanner should continue skipping dependency cache folders");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceProfileDirectoryScanIsBounded()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-profile-scan", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"build\":\"vite\"}}");
        for (var index = 0; index < AgentWorkspaceCoordinator.MaxWorkspaceProfileDirectoryCandidates + 16; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"profile-{index:D4}"));
        }

        var directories = AgentWorkspaceCoordinator.DiscoverWorkspaceProfileDirectories(root);
        Require(directories.Count == AgentWorkspaceCoordinator.MaxWorkspaceProfileDirectories, "workspace profile should cap child directory scans");
        Require(directories.SequenceEqual(directories.Order(StringComparer.OrdinalIgnoreCase)), "workspace profile directories should stay stable after bounded enumeration");
        Require(!directories.Any(path => Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)), "workspace profile should skip ignored metadata directories");

        var profile = AgentWorkspaceCoordinator.BuildWorkspaceProfile(root);
        Require(profile.Contains("Node", StringComparison.OrdinalIgnoreCase), "workspace profile should still detect root package.json after directory scan capping");
        Require(profile.Contains("npm run build", StringComparison.OrdinalIgnoreCase), "workspace profile should keep package script hints");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceProfileSkipsOversizedPackageJson()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-profile-scan", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var largePadding = new string('x', (int)AgentWorkspaceCoordinator.MaxWorkspaceProfileTextFileBytes + 4096);
        File.WriteAllText(Path.Combine(root, "package.json"), $$"""
        {
          "scripts": {
            "dev": "vite --host 0.0.0.0"
          },
          "padding": "{{largePadding}}"
        }
        """);

        var profile = AgentWorkspaceCoordinator.BuildWorkspaceProfile(root);

        Require(profile.Contains("Node", StringComparison.OrdinalIgnoreCase), "oversized package.json should still count as a Node project signal");
        Require(profile.Contains("npm run build", StringComparison.Ordinal), "oversized package.json should fall back to default build hint");
        Require(profile.Contains("npm test", StringComparison.Ordinal), "oversized package.json should fall back to default test hint");
        Require(!profile.Contains("npm run dev", StringComparison.Ordinal), "oversized package.json should not be fully parsed for script hints");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AgentWorkspaceGeneratedCommandsRejectShellMetacharacters()
{
    foreach (var unsafePath in new[]
    {
        "p&whoami&rem/pyproject.toml",
        "p%TEMP%/Cargo.toml",
        "p$(Set-Content escaped.txt)/go.mod",
        "p;Write-Output escaped/package.json"
    })
    {
        Require(!WorkspaceCommandHelpers.IsSafeGeneratedCommandPath(unsafePath), $"shell-active path should be rejected: {unsafePath}");
    }

    Require(WorkspaceCommandHelpers.PythonArtifactCommand("p&whoami&rem/pyproject.toml") == "", "Python artifact commands should fail closed on cmd separators");
    Require(WorkspaceCommandHelpers.RustArtifactCommand("p%TEMP%/Cargo.toml") == "", "Rust artifact commands should fail closed on cmd variable expansion");
    Require(WorkspaceCommandHelpers.GoArtifactCommand("p$(Set-Content escaped.txt)/go.mod") == "", "Go artifact commands should fail closed on PowerShell subexpressions");
    Require(
        WorkspaceCommandHelpers.ArtifactPackageScriptCommands("missing.json", "p;Write-Output escaped/package.json").Count == 0,
        "Node artifact commands should fail closed on shell statement separators");

    var unsafeSuggestion = AgentArtifactService.InferArtifactSuggestion(
        Path.GetTempPath(),
        new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
            "Files: +1 created, ~0 modified, -0 deleted",
            ["p&whoami&rem/pyproject.toml"],
            [],
            [],
            false));
    Require(unsafeSuggestion is null, "artifact inference should not stage a command from a shell-active filesystem path");

    var threw = false;
    try
    {
        _ = WorkspaceCommandHelpers.QuoteCommandArgument(".\\p&whoami&rem");
    }
    catch (ArgumentException)
    {
        threw = true;
    }

    Require(threw, "the shared command argument helper should reject future unsafe path call sites");
    Require(
        WorkspaceCommandHelpers.PythonArtifactCommand("Safe App/pyproject.toml") == "python -m pytest \".\\Safe App\"",
        "safe paths containing spaces should remain supported and quoted");
}

static void AgentWorkspaceBoundedReadersAndCandidatesStopAtLimits()
{
    using var oversizedStream = new CountingReadStream(100);
    var text = WorkspaceCommandHelpers.ReadBoundedText(oversizedStream, 32);
    Require(text is null, "bounded workspace text reads should reject content beyond the byte ceiling");
    Require(oversizedStream.BytesRead == 33, "bounded workspace text reads should inspect at most max plus one byte");

    var inspected = 0;
    Require(WorkspaceScannerService.TryConsumeScanCandidate(ref inspected, 3), "first scan candidate should fit");
    Require(WorkspaceScannerService.TryConsumeScanCandidate(ref inspected, 3), "second scan candidate should fit");
    Require(WorkspaceScannerService.TryConsumeScanCandidate(ref inspected, 3), "third scan candidate should fit");
    Require(!WorkspaceScannerService.TryConsumeScanCandidate(ref inspected, 3), "candidate scanning should stop at the hard ceiling");
    Require(inspected == 3, "rejected candidates should not move the counter beyond its bound");
}

static void AgentWorkspaceStagesBuilderCommandProposals()
{
    var suggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Notes first.

        ```csharp
        Console.WriteLine("not a command");
        ```

        Command proposal:
        ```powershell
        New-Item -ItemType Directory -Path .\TinyApp -Force
        Set-Content -Path .\TinyApp\app.txt -Value "hello"
        ```
        """);

    Require(suggestion is not null, "Agent should extract a command proposal from fenced command blocks");
    var extracted = suggestion ?? throw new InvalidOperationException("Missing command suggestion.");
    Require(extracted.Shell == "PowerShell", "powershell code fences should select PowerShell");
    Require(extracted.Command.Contains("New-Item", StringComparison.Ordinal), "command proposal should preserve the runnable command body");
    Require(!extracted.Command.Contains("Console.WriteLine", StringComparison.Ordinal), "non-command code fences should not be staged");

    var promptSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Next:
        $ dotnet build
        $ dotnet test
        """);
    Require(promptSuggestion is not null, "Agent should extract terminal prompt lines as a fallback command proposal");
    var promptExtracted = promptSuggestion ?? throw new InvalidOperationException("Missing prompt-line command suggestion.");
    Require(promptExtracted.Shell == "Terminal", "prompt-line fallback should use Terminal");
    Require(promptExtracted.Command.Contains("dotnet test", StringComparison.Ordinal), "prompt-line fallback should collect adjacent prompt commands");

    var labeledSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command:
        New-Item -ItemType Directory -Path .\TinyApp -Force
        Set-Content -Path .\TinyApp\app.txt -Value "hello"
        """);
    Require(labeledSuggestion is not null, "Agent should extract labeled command proposals without fenced blocks");
    var labeledExtracted = labeledSuggestion ?? throw new InvalidOperationException("Missing labeled command suggestion.");
    Require(labeledExtracted.Shell == "PowerShell", "labeled PowerShell-style commands should infer PowerShell");
    Require(labeledExtracted.Command.Contains("Set-Content", StringComparison.Ordinal), "labeled command extraction should collect adjacent command lines");

    var inlineSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Next action:
        - `dotnet build`
        """);
    Require(inlineSuggestion is not null, "Agent should extract inline-code command bullets as a fallback proposal");
    var inlineExtracted = inlineSuggestion ?? throw new InvalidOperationException("Missing inline command suggestion.");
    Require(inlineExtracted.Shell == "Terminal", "dotnet inline commands should use Terminal");
    Require(inlineExtracted.Command == "dotnet build", "inline command extraction should remove bullet and backticks");

    var finalProposalSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Earlier example:
        ```powershell
        Write-Host "not the final command"
        ```

        Command proposal:
        ```powershell
        Get-Content .\README.md
        ```
        """);
    Require(finalProposalSuggestion is not null, "Agent should extract the final labeled command proposal before earlier examples");
    var finalProposalExtracted = finalProposalSuggestion ?? throw new InvalidOperationException("Missing final proposal command suggestion.");
    Require(finalProposalExtracted.Command.Contains("Get-Content", StringComparison.Ordinal), "labeled Command proposal should win over earlier command examples");

    var nestedMarkdownFenceSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        ```powershell
        Set-Content -Path "README.md" -Value @"
        # Tiny App

        ```bash
        python app.py
        ```

        "@
        Set-Content -Path "app.py" -Value "print('ok')"
        ```
        """);
    Require(nestedMarkdownFenceSuggestion is not null, "PowerShell command fences should survive nested markdown fences inside here-strings");
    var nestedMarkdownFenceExtracted = nestedMarkdownFenceSuggestion ?? throw new InvalidOperationException("Missing nested markdown fence command suggestion.");
    Require(nestedMarkdownFenceExtracted.Command.Contains("python app.py", StringComparison.Ordinal), "nested markdown fences inside here-strings should remain command content");
    Require(nestedMarkdownFenceExtracted.Command.Contains("Set-Content -Path \"app.py\"", StringComparison.Ordinal), "PowerShell fence extraction should continue after the here-string closes");

    var normalizedPowerShell = AgentCommandProposalService.NormalizeCommandSuggestion(new AgentWorkspaceCoordinator.AgentCommandSuggestion(
        "PowerShell",
        """
        echo -n "click>python-dotenv" > requirements.txt
        New-Item -Path "app.py" -Force | Out-Null
        Get-ChildItem -Path . -Filter *.py,*.txt | Select-Object Name
        """));
    Require(!normalizedPowerShell.Command.Contains("echo -n", StringComparison.OrdinalIgnoreCase), "PowerShell command normalization should replace echo -n redirects");
    Require(normalizedPowerShell.Command.Contains("Set-Content -LiteralPath 'requirements.txt'", StringComparison.Ordinal), "PowerShell command normalization should materialize echo redirects with Set-Content");
    Require(!normalizedPowerShell.Command.Contains("-Filter *.py,*.txt", StringComparison.OrdinalIgnoreCase), "PowerShell command normalization should remove unsupported comma-separated Get-ChildItem filters");

    var inspectionSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Next:
        - `rg --files`
        """);
    Require(inspectionSuggestion is not null, "Agent should recognize rg inspection commands in inline-code fallbacks");
    var inspectionExtracted = inspectionSuggestion ?? throw new InvalidOperationException("Missing inspection command suggestion.");
    Require(inspectionExtracted.Command == "rg --files", "inspection command extraction should preserve rg arguments");

    var xmlSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        <command shell="powershell">
        # create the app folder
        ni -ItemType Directory -Path .\TinyApp -Force
        sc -Path .\TinyApp\app.txt -Value "hello"
        </command>
        """);
    Require(xmlSuggestion is not null, "Agent should extract XML-style command proposals");
    var xmlExtracted = xmlSuggestion ?? throw new InvalidOperationException("Missing XML command suggestion.");
    Require(xmlExtracted.Shell == "PowerShell", "XML shell attributes should select PowerShell");
    Require(xmlExtracted.Command.Contains("ni -ItemType Directory", StringComparison.Ordinal), "XML extraction should preserve aliased PowerShell commands");

    var jsonSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        ```json
        {
          "shell": "powershell",
          "command": "New-Item -ItemType Directory -Path .\\JsonApp -Force\nSet-Content -Path .\\JsonApp\\app.txt -Value \"hello\""
        }
        ```
        """);
    Require(jsonSuggestion is not null, "Agent should extract JSON command proposals from local-model tool-like replies");
    var jsonExtracted = jsonSuggestion ?? throw new InvalidOperationException("Missing JSON command suggestion.");
    Require(jsonExtracted.Shell == "PowerShell", "JSON shell fields should select PowerShell");
    Require(jsonExtracted.Command.Contains("JsonApp", StringComparison.Ordinal), "JSON command extraction should preserve command strings");

    var nestedJsonSuggestion = AgentCommandProposalService.ExtractCommandSuggestion("""
        {
          "command_proposal": {
            "type": "terminal",
            "commands": [
              "dotnet build",
              "dotnet test"
            ]
          }
        }
        """);
    Require(nestedJsonSuggestion is not null, "Agent should extract nested JSON command proposals");
    var nestedJsonExtracted = nestedJsonSuggestion ?? throw new InvalidOperationException("Missing nested JSON command suggestion.");
    Require(nestedJsonExtracted.Shell == "Terminal", "nested JSON terminal type should select Terminal");
    Require(nestedJsonExtracted.Command.Contains("dotnet test", StringComparison.Ordinal), "nested JSON command arrays should join adjacent commands");

    var plainLabeledFence = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        ```
        # first write files
        New-Item -ItemType Directory -Path .\PlainFence -Force
        Set-Content -Path .\PlainFence\index.html -Value "<h1>hi</h1>"
        ```
        """);
    Require(plainLabeledFence is not null, "labeled plain fences with runnable commands should be staged");
    var plainFenceExtracted = plainLabeledFence ?? throw new InvalidOperationException("Missing plain fence command suggestion.");
    Require(plainFenceExtracted.Shell == "PowerShell", "labeled plain fences should infer PowerShell from the first runnable line");

    var pwshLabel = AgentCommandProposalService.ExtractCommandSuggestion("""
        pwsh: dotnet new console -n TinyApp
        """);
    Require(pwshLabel is not null, "pwsh labels should be recognized as command proposals");
    var pwshExtracted = pwshLabel ?? throw new InvalidOperationException("Missing pwsh command suggestion.");
    Require(pwshExtracted.Shell == "PowerShell", "pwsh labels should select PowerShell");

    var npmCreate = AgentCommandProposalService.ExtractCommandSuggestion("""
        Run this command:
        npm create vite@latest tiny-app -- --template react
        """);
    Require(npmCreate is not null, "Run this command labels should be recognized");
    var npmCreateExtracted = npmCreate ?? throw new InvalidOperationException("Missing npm create command suggestion.");
    Require(npmCreateExtracted.Command.Contains("npm create vite", StringComparison.Ordinal), "npm create commands should remain stageable");

    var cargoProposal = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        cargo new TinyCrate
        """);
    Require(cargoProposal is not null, "plain labeled cargo command proposals should be recognized");
    var cargoExtracted = cargoProposal ?? throw new InvalidOperationException("Missing cargo command suggestion.");
    Require(cargoExtracted.Shell == "Terminal", "cargo command proposals should use the terminal shell");

    var goProposal = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        go mod init example.com/tiny
        """);
    Require(goProposal is not null, "plain labeled Go command proposals should be recognized");
    var goExtracted = goProposal ?? throw new InvalidOperationException("Missing Go command suggestion.");
    Require(goExtracted.Shell == "Terminal", "Go command proposals should use the terminal shell");

    var fileSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        Here are the app files.

        index.html
        ```html
        <main id="app">Hello Arena</main>
        ```

        scripts/app.js
        ```js
        document.body.dataset.ready = "yes";
        ```
        """);
    Require(fileSuggestion is not null, "Agent should extract app file snippets when Builder forgets a command proposal");
    var fileSuggestionExtracted = fileSuggestion ?? throw new InvalidOperationException("Missing file suggestion.");
    Require(fileSuggestionExtracted.Files.Count == 2, "file snippet extraction should collect multiple app files");
    Require(fileSuggestionExtracted.Files[0].Path == "index.html", "file snippet extraction should use the filename before the fence");
    Require(fileSuggestionExtracted.Files[1].Path == "scripts/app.js", "file snippet extraction should preserve nested relative paths");
    var fileWriteCommand = AgentCommandProposalService.BuildFileWriteCommand(fileSuggestionExtracted);
    Require(fileWriteCommand.Contains("FromBase64String", StringComparison.Ordinal), "file snippet write commands should encode content safely");
    Require(fileWriteCommand.Contains("index.html", StringComparison.Ordinal), "file snippet write commands should include target file paths");
    Require(AgentWorkspaceCommand.BuildPreview(Path.GetTempPath(), "PowerShell", fileWriteCommand).Ok, "generated file snippet commands should pass workspace preview");

    var heredocSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        Command proposal:
        ```bash
        cat > index.html <<'EOF'
        <main id="app">Hello from heredoc</main>
        EOF
        cat <<'EOF' > scripts/app.js
        document.body.dataset.ready = "heredoc";
        EOF
        ```
        """);
    Require(heredocSuggestion is not null, "Agent should extract POSIX heredoc file writes from local-model app commands");
    var heredocFiles = heredocSuggestion ?? throw new InvalidOperationException("Missing heredoc file suggestion.");
    Require(heredocFiles.Files.Count == 2, "heredoc extraction should collect multiple generated files");
    Require(heredocFiles.Files[0].Path == "index.html", "heredoc extraction should use the redirect target before the marker");
    Require(heredocFiles.Files[0].Content.Contains("Hello from heredoc", StringComparison.Ordinal), "heredoc extraction should preserve file content");
    Require(!heredocFiles.Files[0].Content.Contains("EOF", StringComparison.Ordinal), "heredoc extraction should strip marker lines");
    Require(heredocFiles.Files[1].Path == "scripts/app.js", "heredoc extraction should use redirect targets after the marker");
    var heredocCommand = AgentCommandProposalService.BuildFileWriteCommand(heredocFiles);
    Require(AgentWorkspaceCommand.BuildPreview(Path.GetTempPath(), "PowerShell", heredocCommand).Ok, "heredoc materialization commands should pass workspace preview");

    var bashHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        ```bash
        cat > index.html <<'EOF'
        <main>Converted from bash</main>
        EOF
        ```
        """);
    Require(bashHeredocCommand is not null, "bash heredoc command proposals should be converted into a Windows-safe write command");
    var convertedHeredoc = bashHeredocCommand ?? throw new InvalidOperationException("Missing converted heredoc command.");
    Require(convertedHeredoc.Shell == "PowerShell", "converted heredoc commands should stage PowerShell, not cmd.exe bash syntax");
    Require(convertedHeredoc.Command.Contains("FromBase64String", StringComparison.Ordinal), "converted heredoc commands should use safe file materialization");
    Require(!convertedHeredoc.Command.Contains("cat > index.html", StringComparison.Ordinal), "converted heredoc commands should not leave POSIX redirection in the approval rail");

    var chainedHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        ```bash
        mkdir -p TinyChain && cat > TinyChain/index.html <<'EOF'
        <main>Chained heredoc app</main>
        EOF
        ```
        """);
    Require(chainedHeredocCommand is not null, "chained POSIX heredoc write proposals should be converted into safe file materialization");
    var convertedChainedHeredoc = chainedHeredocCommand ?? throw new InvalidOperationException("Missing converted chained heredoc command.");
    Require(convertedChainedHeredoc.Shell == "PowerShell", "chained heredoc conversion should select PowerShell");
    Require(convertedChainedHeredoc.Command.Contains("TinyChain/index.html", StringComparison.Ordinal), "chained heredoc conversion should preserve nested target paths");
    Require(!convertedChainedHeredoc.Command.Contains("mkdir -p", StringComparison.Ordinal), "chained heredoc conversion should not leave POSIX setup commands in the approval rail");

    var plainHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        cat > index.html <<'EOF'
        <main>Plain heredoc</main>
        EOF
        """);
    Require(plainHeredocCommand is not null, "plain labeled heredoc proposals should be converted into a Windows-safe write command");
    var convertedPlainHeredoc = plainHeredocCommand ?? throw new InvalidOperationException("Missing plain heredoc command.");
    Require(convertedPlainHeredoc.Shell == "PowerShell", "plain heredoc conversion should select PowerShell");
    Require(convertedPlainHeredoc.Command.Contains("FromBase64String", StringComparison.Ordinal), "plain heredoc conversion should use safe file materialization");

    var xmlHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        <command shell="bash">
        cat > index.html <<'EOF'
        <main>XML heredoc</main>
        EOF
        </command>
        """);
    Require(xmlHeredocCommand is not null, "XML heredoc command proposals should be converted into a Windows-safe write command");
    var convertedXmlHeredoc = xmlHeredocCommand ?? throw new InvalidOperationException("Missing XML heredoc command.");
    Require(convertedXmlHeredoc.Shell == "PowerShell", "XML heredoc conversion should select PowerShell");
    Require(convertedXmlHeredoc.Command.Contains("FromBase64String", StringComparison.Ordinal), "XML heredoc conversion should use safe file materialization");

    var jsonHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        ```json
        {
          "shell": "bash",
          "command": "cat > index.html <<'EOF'\n<main>JSON heredoc</main>\nEOF"
        }
        ```
        """);
    Require(jsonHeredocCommand is not null, "JSON heredoc command proposals should be converted into a Windows-safe write command");
    var convertedJsonHeredoc = jsonHeredocCommand ?? throw new InvalidOperationException("Missing JSON heredoc command.");
    Require(convertedJsonHeredoc.Shell == "PowerShell", "JSON heredoc conversion should select PowerShell");
    Require(convertedJsonHeredoc.Command.Contains("FromBase64String", StringComparison.Ordinal), "JSON heredoc conversion should use safe file materialization");

    var promptLineHeredocCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        Next I would run:
        $ cat > index.html <<'EOF'
        <main>Prompt heredoc app</main>
        EOF
        """);
    Require(promptLineHeredocCommand is not null, "shell transcript heredoc proposals should preserve heredoc bodies and convert safely");
    var convertedPromptLineHeredoc = promptLineHeredocCommand ?? throw new InvalidOperationException("Missing prompt-line heredoc command.");
    Require(convertedPromptLineHeredoc.Shell == "PowerShell", "shell transcript heredoc conversion should select PowerShell");
    Require(convertedPromptLineHeredoc.Command.Contains("FromBase64String", StringComparison.Ordinal), "shell transcript heredoc conversion should use safe file materialization");
    var promptLineHeredocRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-prompt-heredoc", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(promptLineHeredocRoot);
    try
    {
        var preview = AgentWorkspaceCommand.BuildPreview(promptLineHeredocRoot, convertedPromptLineHeredoc.Shell, convertedPromptLineHeredoc.Command);
        Require(preview.Ok, $"converted shell transcript heredoc should pass preview: {preview.Error}");
        var result = AgentWorkspaceCommand.RunAsync(preview, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Require(result.Ok, $"converted shell transcript heredoc should run successfully: {result.Error} {result.StandardError}");
        var writtenApp = Path.Combine(promptLineHeredocRoot, "index.html");
        Require(File.Exists(writtenApp), "converted shell transcript heredoc should create the app entry file");
        Require(File.ReadAllText(writtenApp).Contains("Prompt heredoc app", StringComparison.Ordinal), "converted shell transcript heredoc should preserve app markup");
    }
    finally
    {
        if (Directory.Exists(promptLineHeredocRoot))
        {
            Directory.Delete(promptLineHeredocRoot, recursive: true);
        }
    }

    var echoRedirectCommand = AgentCommandProposalService.ExtractCommandSuggestion("""
        Command proposal:
        echo '<main>Echo redirect app</main>' > index.html
        printf 'console.log("ready")\n' > scripts/app.js
        """);
    Require(echoRedirectCommand is not null, "shell echo/printf redirect proposals should convert into safe file materialization");
    var convertedEchoRedirect = echoRedirectCommand ?? throw new InvalidOperationException("Missing shell redirect command.");
    Require(convertedEchoRedirect.Shell == "PowerShell", "shell redirect conversion should select PowerShell");
    Require(convertedEchoRedirect.Command.Contains("FromBase64String", StringComparison.Ordinal), "shell redirect conversion should use safe file materialization");
    Require(!convertedEchoRedirect.Command.Contains("echo '<main>", StringComparison.Ordinal), "shell redirect conversion should not leave fragile echo redirection in the approval rail");
    var echoRedirectRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-echo-redirect", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(echoRedirectRoot);
    try
    {
        var preview = AgentWorkspaceCommand.BuildPreview(echoRedirectRoot, convertedEchoRedirect.Shell, convertedEchoRedirect.Command);
        Require(preview.Ok, $"converted shell redirect should pass preview: {preview.Error}");
        var result = AgentWorkspaceCommand.RunAsync(preview, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Require(result.Ok, $"converted shell redirect should run successfully: {result.Error} {result.StandardError}");
        var writtenHtml = Path.Combine(echoRedirectRoot, "index.html");
        var writtenScript = Path.Combine(echoRedirectRoot, "scripts", "app.js");
        Require(File.Exists(writtenHtml), "converted shell redirect should create the HTML entry file");
        Require(File.ReadAllText(writtenHtml) == "<main>Echo redirect app</main>", "converted echo redirect should strip shell quotes");
        Require(File.Exists(writtenScript), "converted printf redirect should create nested app script files");
        Require(File.ReadAllText(writtenScript).Contains("ready", StringComparison.Ordinal), "converted printf redirect should preserve script content");
        Require(File.ReadAllText(writtenScript).EndsWith('\n'), "converted printf redirect should decode newline escapes");
    }
    finally
    {
        if (Directory.Exists(echoRedirectRoot))
        {
            Directory.Delete(echoRedirectRoot, recursive: true);
        }
    }

    var languageTaggedFileSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        ```html public/index.html
        <main>Tagged filename</main>
        ```
        """);
    Require(languageTaggedFileSuggestion is not null, "Agent should detect filenames in code fence language tags");
    var languageTaggedFiles = languageTaggedFileSuggestion ?? throw new InvalidOperationException("Missing language-tagged file suggestion.");
    Require(languageTaggedFiles.Files.Single().Path == "public/index.html", "language-tagged file extraction should preserve the tagged relative path");

    var linkedAssetSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        ```html
        <!doctype html>
        <html>
        <head>
          <link rel="stylesheet" href="style.css">
        </head>
        <body>
          <main id="app"></main>
          <script type="module" src="./src/main.js"></script>
        </body>
        </html>
        ```

        ```css
        body { margin: 0; }
        ```

        ```js
        document.querySelector("#app").textContent = "Linked";
        ```
        """);
    Require(linkedAssetSuggestion is not null, "Agent should infer linked asset paths from unlabeled HTML/CSS/JS snippets");
    var linkedAssetFiles = linkedAssetSuggestion ?? throw new InvalidOperationException("Missing linked asset suggestion.");
    var linkedAssetPaths = linkedAssetFiles.Files.Select(file => file.Path).ToArray();
    Require(linkedAssetPaths.SequenceEqual(["index.html", "style.css", "src/main.js"]), $"linked asset extraction should follow stylesheet and script paths referenced by HTML; got {string.Join(", ", linkedAssetPaths)}");
    var linkedAssetWriteCommand = AgentCommandProposalService.BuildFileWriteCommand(linkedAssetFiles);
    Require(linkedAssetWriteCommand.Contains("style.css", StringComparison.Ordinal), "linked asset write commands should use the referenced stylesheet path");
    Require(linkedAssetWriteCommand.Contains("src/main.js", StringComparison.Ordinal), "linked asset write commands should use the referenced script path");

    var rejectedFileSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        ../escape.txt
        ```txt
        no
        ```
        """);
    Require(rejectedFileSuggestion is null, "file snippet extraction should reject parent-path targets");

    AgentWorkspaceCoordinator.AgentCommandHistoryItem[] historyItems =
    [
        new AgentWorkspaceCoordinator.AgentCommandHistoryItem(
            2,
            new DateTimeOffset(2026, 6, 11, 12, 5, 0, TimeSpan.Zero),
            "PowerShell",
            "Set-Content -Path .\\follow-up.txt -Value ok",
            "Exit 0",
            "Auto Continue",
            "120 ms | Files: +1 created, ~0 modified, -0 deleted",
            "C:\\work",
            "Files: +1 created, ~0 modified, -0 deleted",
            0),
        new AgentWorkspaceCoordinator.AgentCommandHistoryItem(
            1,
            new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero),
            "PowerShell",
            "Set-Content -Path .\\index.html -Value hi",
            "Blocked",
            "Builder proposal",
            "Command references a parent path outside the selected workspace.",
            "C:\\work",
            "",
            null)
    ];
    var historyText = AgentWorkspaceCoordinator.BuildCommandHistoryCopyText(historyItems);
    Require(historyText.Contains("Agent command history", StringComparison.Ordinal), "command history copy text should include a stable title");
    Require(historyText.Contains("Exit 0 | PowerShell | Auto Continue", StringComparison.Ordinal), "command history copy text should include status, shell, and source");
    Require(historyText.Contains("follow-up.txt", StringComparison.Ordinal), "command history copy text should include command bodies");
    Require(historyText.Contains("Blocked | PowerShell | Builder proposal", StringComparison.Ordinal), "command history copy text should include blocked previews");

    var beforeFiles = new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase)
    {
        ["README.md"] = new(12, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ["src/old.txt"] = new(7, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    };
    var afterFiles = new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase)
    {
        ["README.md"] = new(18, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)),
        ["src/app.txt"] = new(5, new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc))
    };
    var receipt = AgentWorkspaceCoordinator.BuildFileReceipt(beforeFiles, afterFiles);
    Require(receipt.Created.SequenceEqual(["src/app.txt"]), "file receipt should report created workspace files");
    Require(receipt.Modified.SequenceEqual(["README.md"]), "file receipt should report modified workspace files");
    Require(receipt.Deleted.SequenceEqual(["src/old.txt"]), "file receipt should report deleted workspace files");
    Require(AgentWorkspaceCoordinator.FormatFileReceipt(receipt).Contains("Files: +1 created, ~1 modified, -1 deleted", StringComparison.Ordinal), "file receipt should summarize change counts");
    var limitedBeforeFiles = Enumerable.Range(0, 1500).ToDictionary(
        index => $"src/file-{index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}.txt",
        _ => new AgentWorkspaceCoordinator.AgentWorkspaceFileStamp(5, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        StringComparer.OrdinalIgnoreCase);
    var limitedAfterFiles = new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(limitedBeforeFiles, StringComparer.OrdinalIgnoreCase);
    var limitedReceipt = AgentWorkspaceCoordinator.BuildFileReceipt(limitedBeforeFiles, limitedAfterFiles);
    var limitedReceiptText = AgentWorkspaceCoordinator.FormatFileReceipt(limitedReceipt);
    Require(limitedReceipt.ScannedLimit, "full receipt window should be marked as scan-limited");
    Require(AgentWorkspaceCoordinator.ReceiptScanIsLimitedWithoutTrackedChanges(limitedReceipt), "limited no-delta receipt should be classified as unknown outside scan");
    Require(limitedReceiptText.Contains("scan limited", StringComparison.OrdinalIgnoreCase), "limited receipt should disclose scan cap");
    Require(limitedReceiptText.Contains("unknown", StringComparison.OrdinalIgnoreCase), "limited receipt should avoid claiming full no-change certainty");
    Require(!limitedReceiptText.Contains("No tracked file changes detected.", StringComparison.Ordinal), "limited receipt should not be presented as a definite no-change result");
    Require(AgentArtifactService.ReceiptPreviewText(limitedReceipt).Contains("outside the scan limit", StringComparison.Ordinal), "artifact receipt previews should keep limited no-change scans cautious");
    var artifactRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-artifacts", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(artifactRoot);
    try
    {
        File.WriteAllText(Path.Combine(artifactRoot, "package.json"), """
            {
              "scripts": {
                "build": "vite build",
                "test": "vitest run"
              }
            }
            """);
        var nodeSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["package.json"],
                [],
                [],
                false));
        Require(nodeSuggestion is not null && nodeSuggestion.Kind == "Node", "artifact suggestion should detect package.json as a Node artifact");
        var directNodeSuggestion = AgentArtifactService.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["package.json"],
                [],
                [],
                false));
        Require(directNodeSuggestion is not null && directNodeSuggestion.Command == "npm run build", "artifact service should own Node build artifact inference");
        var rootNode = nodeSuggestion ?? throw new InvalidOperationException("Missing root Node artifact suggestion.");
        Require(rootNode.Command == "npm run build", "Node artifact suggestions should prefer package build scripts");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, rootNode.Shell, rootNode.Command).Ok, "root Node artifact command should pass preview validation");
        Require(AgentArtifactService.ArtifactEntryExists(artifactRoot, rootNode), "artifact service should confirm generated artifact entries inside the workspace");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "TinyApp"));
        File.WriteAllText(Path.Combine(artifactRoot, "TinyApp", "package.json"), """
            {
              "scripts": {
                "build": "vite build"
              }
            }
            """);
        var nestedNodeSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["TinyApp/package.json"],
                [],
                [],
                false));
        Require(nestedNodeSuggestion is not null && nestedNodeSuggestion.Command == "npm --prefix .\\TinyApp run build", "nested Node artifact suggestions should use npm --prefix");
        var nestedNode = nestedNodeSuggestion ?? throw new InvalidOperationException("Missing nested Node artifact suggestion.");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, nestedNode.Shell, nestedNode.Command).Ok, "nested Node artifact command should pass preview validation");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "ExistingNode", "src"));
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingNode", "package.json"), """
            {
              "scripts": {
                "build": "vite build"
              }
            }
            """);
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingNode", "src", "App.jsx"), "export default function App() { return null; }");
        var sourceOnlyNodeSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +0 created, ~1 modified, -0 deleted",
                [],
                ["ExistingNode/src/App.jsx"],
                [],
                false));
        Require(sourceOnlyNodeSuggestion is not null && sourceOnlyNodeSuggestion.Command == "npm --prefix .\\ExistingNode run build", "source-only Node edits should infer the nearest existing package artifact");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "StartOnly"));
        File.WriteAllText(Path.Combine(artifactRoot, "StartOnly", "package.json"), """
            {
              "scripts": {
                "start": "vite --host 127.0.0.1"
              }
            }
            """);
        var startOnlySuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +2 created, ~0 modified, -0 deleted",
                ["StartOnly/package.json", "StartOnly/index.html"],
                [],
                [],
                false));
        Require(startOnlySuggestion is not null && startOnlySuggestion.Shell == "PowerShell", "start-only Node artifact suggestions should use a PowerShell preview launcher");
        var startOnlyArtifact = startOnlySuggestion ?? throw new InvalidOperationException("Missing start-only Node artifact suggestion.");
        Require(startOnlyArtifact.Command.StartsWith("Start-Process", StringComparison.Ordinal), "start-only Node artifact suggestions should detach preview servers from the bounded command runner");
        Require(startOnlyArtifact.Command.Contains("npm --prefix .\\StartOnly start", StringComparison.Ordinal), "detached Node preview launchers should preserve the real start script");
        var startOnlyPreview = AgentWorkspaceCommand.BuildPreview(artifactRoot, startOnlyArtifact.Shell, startOnlyArtifact.Command);
        Require(startOnlyPreview.Ok, "start-only Node artifact commands should pass preview validation");
        Require(startOnlyPreview.Risks.Contains("Long-running"), "start-only Node artifact commands should be flagged as manual app previews");
        var startOnlyPreviewResult = new AgentCommandResult(
            true,
            startOnlyArtifact.Shell,
            startOnlyArtifact.Command,
            artifactRoot,
            0,
            "",
            "",
            TimeSpan.FromMilliseconds(25),
            false,
            false,
            "");
        var startOnlyVerification = AgentWorkspaceCoordinator.AgentArtifactVerification.From(startOnlyArtifact, startOnlyPreviewResult);
        Require(startOnlyVerification.IsPreviewLaunch, "long-running Node artifact commands should be classified as artifact previews");
        Require(startOnlyVerification.ActionTitle == "Artifact preview", "long-running Node artifact commands should use preview labels");
        Require(startOnlyVerification.Summary.Contains("preview launched", StringComparison.OrdinalIgnoreCase), "detached Node preview launch summaries should report a launched preview instead of a timeout");
        Require(!startOnlyVerification.Summary.Contains("check", StringComparison.OrdinalIgnoreCase), "long-running Node artifact summaries should not call previews checks");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "PlainStatic"));
        File.WriteAllText(Path.Combine(artifactRoot, "PlainStatic", "package.json"), """
            {
              "private": true
            }
            """);
        var packageStaticSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +2 created, ~0 modified, -0 deleted",
                ["PlainStatic/package.json", "PlainStatic/index.html"],
                [],
                [],
                false));
        Require(packageStaticSuggestion is not null && packageStaticSuggestion.Kind == "Static web", "package files without runnable scripts should not hide generated static HTML artifacts");
        var packageStaticArtifact = packageStaticSuggestion ?? throw new InvalidOperationException("Missing static fallback artifact suggestion.");
        Require(packageStaticArtifact.Command.Contains("PlainStatic\\index.html", StringComparison.Ordinal), "package-less-script static suggestions should point at the generated HTML entry");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "NoScripts"));
        File.WriteAllText(Path.Combine(artifactRoot, "NoScripts", "package.json"), """
            {
              "private": true
            }
            """);
        var noScriptPackageSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["NoScripts/package.json"],
                [],
                [],
                false));
        Require(noScriptPackageSuggestion is not null && noScriptPackageSuggestion.Command.StartsWith("Get-Item", StringComparison.Ordinal), "package files without runnable scripts should stage a read-only inspection instead of fake npm scripts");
        var noScriptPackageArtifact = noScriptPackageSuggestion ?? throw new InvalidOperationException("Missing no-script package artifact suggestion.");
        Require(!noScriptPackageArtifact.Command.Contains("npm", StringComparison.OrdinalIgnoreCase), "no-script package suggestions should not invent npm commands");

        var staticSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["index.html"],
                [],
                [],
                false));
        Require(staticSuggestion is not null && staticSuggestion.Shell == "PowerShell", "static HTML artifact suggestions should use PowerShell inspection");
        var staticArtifact = staticSuggestion ?? throw new InvalidOperationException("Missing static artifact suggestion.");
        Require(staticArtifact.Command.StartsWith("Start-Process", StringComparison.Ordinal), "static HTML artifact suggestions should stage a default-browser preview command");
        Require(staticArtifact.Command.Contains("index.html", StringComparison.Ordinal), "static HTML artifact suggestions should point at the generated entry file");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, staticArtifact.Shell, staticArtifact.Command).Ok, "static artifact command should pass preview validation");
        var staticPreviewResult = new AgentCommandResult(
            true,
            staticArtifact.Shell,
            staticArtifact.Command,
            artifactRoot,
            0,
            "",
            "",
            TimeSpan.FromMilliseconds(12),
            false,
            false,
            "");
        var staticPreview = AgentWorkspaceCoordinator.AgentArtifactVerification.From(staticArtifact, staticPreviewResult);
        Require(staticPreview.IsPreviewLaunch, "static web Start-Process artifact commands should be classified as preview launches");
        Require(AgentArtifactService.IsPreviewLaunch(staticPreview.Kind, staticPreview.Command), "artifact service should classify static preview launch commands directly");
        Require(AgentArtifactService.ActionTitle(staticPreview) == "Artifact preview", "artifact service should own artifact preview action labels");
        Require(staticPreview.Summary.Contains("preview launched", StringComparison.OrdinalIgnoreCase), "static web artifact launch summaries should say preview launched");
        Require(!staticPreview.Summary.Contains("check succeeded", StringComparison.OrdinalIgnoreCase), "static web artifact launch summaries should not overclaim verification");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "ExistingStatic", "scripts"));
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingStatic", "index.html"), "<script src=\"scripts/app.js\"></script>");
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingStatic", "scripts", "app.js"), "document.body.dataset.ready = 'yes';");
        var sourceOnlyStaticSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +0 created, ~1 modified, -0 deleted",
                [],
                ["ExistingStatic/scripts/app.js"],
                [],
                false));
        Require(sourceOnlyStaticSuggestion is not null && sourceOnlyStaticSuggestion.Kind == "Static web", "source-only static edits should infer the nearest existing HTML artifact");
        var sourceOnlyStaticArtifact = sourceOnlyStaticSuggestion ?? throw new InvalidOperationException("Missing source-only static artifact suggestion.");
        Require(sourceOnlyStaticArtifact.Command.Contains("ExistingStatic\\index.html", StringComparison.Ordinal), "source-only static artifact previews should point at the existing HTML entry");

        var dotnetSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["src/TinyApp.csproj"],
                [],
                [],
                false));
        Require(dotnetSuggestion is not null && dotnetSuggestion.Command.Contains("dotnet build", StringComparison.Ordinal), ".NET artifact suggestions should propose dotnet build");
        var dotnetArtifact = dotnetSuggestion ?? throw new InvalidOperationException("Missing .NET artifact suggestion.");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, dotnetArtifact.Shell, dotnetArtifact.Command).Ok, ".NET artifact command should pass preview validation");

        var dotnetSlnSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +2 created, ~0 modified, -0 deleted",
                ["src/TinyApp.csproj", "TinyApp.sln"],
                [],
                [],
                false));
        Require(dotnetSlnSuggestion is not null && dotnetSlnSuggestion.EntryPath == "TinyApp.sln", ".NET artifact suggestions should prefer solution files over project files");

        Directory.CreateDirectory(Path.Combine(artifactRoot, "ExistingDotNet"));
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingDotNet", "ExistingDotNet.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(artifactRoot, "ExistingDotNet", "Program.cs"), "Console.WriteLine(\"hi\");");
        var sourceOnlyDotNetSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +0 created, ~1 modified, -0 deleted",
                [],
                ["ExistingDotNet/Program.cs"],
                [],
                false));
        Require(sourceOnlyDotNetSuggestion is not null && sourceOnlyDotNetSuggestion.Command.Contains("ExistingDotNet\\ExistingDotNet.csproj", StringComparison.Ordinal), "source-only .NET edits should infer the nearest existing project artifact");

        var pythonSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["PyApp/pyproject.toml"],
                [],
                [],
                false));
        Require(pythonSuggestion is not null && pythonSuggestion.Command == "python -m pytest .\\PyApp", "nested Python artifact suggestions should scope pytest to the generated folder");
        var pythonArtifact = pythonSuggestion ?? throw new InvalidOperationException("Missing Python artifact suggestion.");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, pythonArtifact.Shell, pythonArtifact.Command).Ok, "nested Python artifact command should pass preview validation");

        var rustSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["Crate/Cargo.toml"],
                [],
                [],
                false));
        Require(rustSuggestion is not null && rustSuggestion.Command == "cargo test --manifest-path .\\Crate\\Cargo.toml", "nested Rust artifact suggestions should use --manifest-path");
        var rustArtifact = rustSuggestion ?? throw new InvalidOperationException("Missing Rust artifact suggestion.");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, rustArtifact.Shell, rustArtifact.Command).Ok, "nested Rust artifact command should pass preview validation");

        var goSuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +1 created, ~0 modified, -0 deleted",
                ["GoApp/go.mod"],
                [],
                [],
                false));
        Require(goSuggestion is not null && goSuggestion.Command == "go test ./GoApp/...", "nested Go artifact suggestions should scope go test to the generated module");
        var goArtifact = goSuggestion ?? throw new InvalidOperationException("Missing Go artifact suggestion.");
        Require(AgentWorkspaceCommand.BuildPreview(artifactRoot, goArtifact.Shell, goArtifact.Command).Ok, "nested Go artifact command should pass preview validation");

        var deletedOnlySuggestion = AgentWorkspaceCoordinator.InferArtifactSuggestion(
            artifactRoot,
            new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
                "Files: +0 created, ~0 modified, -1 deleted",
                [],
                [],
                ["package.json"],
                false));
        Require(deletedOnlySuggestion is null, "deleted-only file receipts should not create artifact suggestions");
    }
    finally
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    var briefResult = new AgentCommandResult(
        true,
        "PowerShell",
        "Set-Content -Path .\\src\\app.txt -Value hello",
        "C:\\work",
        0,
        "wrote app",
        "",
        TimeSpan.FromMilliseconds(95),
        false,
        false,
        "");
    var artifactSuggestion = new AgentWorkspaceCoordinator.AgentArtifactSuggestion(
        "Static web",
        "src/app.txt",
        "PowerShell",
        "Get-Item .\\src\\app.txt",
        "Static artifact at src/app.txt; preview with `Get-Item .\\src\\app.txt`.");
    var brief = AgentWorkspaceCoordinator.BuildWorkBrief(
        "Write a tiny app",
        "Full Access is on for this workspace session. Preview-ready commands run after workspace validation.",
        briefResult,
        receipt,
        historyItems,
        "Use Verify to validate the generated app.",
        artifactSuggestion);
    Require(brief.Contains("Agent work brief", StringComparison.Ordinal), "work brief should include a stable title");
    Require(brief.Contains("Task: Write a tiny app", StringComparison.Ordinal), "work brief should include the original task");
    Require(brief.Contains("Autonomy: Full Access is on", StringComparison.Ordinal), "work brief should include autonomy state");
    Require(brief.Contains("workspace session", StringComparison.Ordinal), "work brief should preserve the session autonomy contract");
    Require(brief.Contains("Latest command: PowerShell exit 0", StringComparison.Ordinal), "work brief should include latest command status");
    Require(brief.Contains("Created:", StringComparison.Ordinal) && brief.Contains("src/app.txt", StringComparison.Ordinal), "work brief should include created changed paths");
    Require(brief.Contains("STDOUT", StringComparison.Ordinal) && brief.Contains("wrote app", StringComparison.Ordinal), "work brief should include bounded command output");
    Require(brief.Contains("Recent commands:", StringComparison.Ordinal) && brief.Contains("follow-up.txt", StringComparison.Ordinal), "work brief should include recent command history");
    Require(brief.Contains("Artifact suggestion:", StringComparison.Ordinal) && brief.Contains("Get-Item", StringComparison.Ordinal), "work brief should include artifact verification suggestions");
    var directBrief = AgentArtifactService.BuildWorkBrief(
        "Write a tiny app",
        "Full Access is on for this workspace session. Preview-ready commands run after workspace validation.",
        briefResult,
        receipt,
        historyItems,
        "Use Verify to validate the generated app.",
        artifactSuggestion);
    Require(directBrief.Contains("Artifact suggestion:", StringComparison.Ordinal), "artifact service should own work brief artifact context");
    var summaryLine = AgentCommandRailViewModel.BuildWorkSummaryLine(
        briefResult,
        receipt,
        "Use Verify to validate the generated app.",
        artifactSuggestion.Summary);
    Require(summaryLine.Contains("Exit 0", StringComparison.Ordinal) && summaryLine.Contains("Files: +1 created", StringComparison.Ordinal), "work summary line should combine status and receipt counts");
    Require(summaryLine.Contains("src/app.txt", StringComparison.Ordinal), "work summary line should preview changed paths");
    Require(summaryLine.Contains("Artifact:", StringComparison.Ordinal), "work summary line should include artifact suggestions");
    var limitedSummaryLine = AgentCommandRailViewModel.BuildWorkSummaryLine(
        briefResult,
        limitedReceipt,
        "Inspect the expected output path before continuing.",
        artifactSuggestion.Summary);
    Require(limitedSummaryLine.Contains("File scan limited", StringComparison.Ordinal), "work summary line should flag limited receipts as unknown");
    Require(limitedSummaryLine.Contains("unknown", StringComparison.OrdinalIgnoreCase), "work summary line should not imply a complete no-change scan");
    Require(!limitedSummaryLine.Contains("No tracked file changes.", StringComparison.Ordinal), "limited work summary should not use the definite no-change label");
    var safePreview = AgentWorkspaceCommand.BuildPreview(Path.GetTempPath(), "PowerShell", "Get-ChildItem .");
    var safeRiskChip = AgentCommandRailViewModel.RiskChipsForPreview(safePreview).Single();
    Require(AgentCommandRailViewModel.PreviewStatus(safePreview) == "Preview ready. Approval required.", "command rail view model should summarize safe previews");
    Require(safeRiskChip.Label == "Workspace scoped", "command rail view model should provide the default safe preview risk chip");
    Require(safeRiskChip.BorderResourceKey == "AssistBorderBrush", "safe preview risk chip should use the assist border");
    var blockedPreview = AgentWorkspaceCommand.BuildPreview(Path.GetTempPath(), "PowerShell", "Set-Location ..");
    var blockedRiskChip = AgentCommandRailViewModel.RiskChipsForPreview(blockedPreview).First();
    Require(AgentCommandRailViewModel.PreviewStatus(blockedPreview) == "Preview blocked.", "command rail view model should summarize blocked previews");
    Require(blockedRiskChip.BorderResourceKey == "DangerBorderBrush", "blocked preview risk chip should use the danger border");
    Require(AgentCommandRailViewModel.RunningStatus("PowerShell") == "Running PowerShell...", "command rail view model should summarize running commands");
    Require(AgentCommandRailViewModel.OutputSummary([]) == "No artifacts yet.", "command rail view model should summarize empty outputs");
    Require(
        AgentCommandRailViewModel.OutputSummary([new AgentWorkspaceCoordinator.AgentOutputItem("Files", "No changes", "None", "DisabledBorderBrush")]) == "Files: No changes.",
        "command rail view model should summarize the first output row");
    var changedDescriptor = AgentCommandResultService.ResultFollowUpDescriptor(
        briefResult,
        receipt,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        artifactActionTitle: "");
    Require(changedDescriptor.ButtonLabel == "Stage Next", "changed-file results should continue with Stage Next");
    Require(changedDescriptor.ToolTip.Contains("continuation", StringComparison.OrdinalIgnoreCase), "changed-file follow-up should describe continuation");
    var failedResult = new AgentCommandResult(
        false,
        "PowerShell",
        "npm run build",
        "C:\\work",
        1,
        "",
        "failed",
        TimeSpan.FromMilliseconds(20),
        false,
        false,
        "");
    var failedDescriptor = AgentCommandResultService.ResultFollowUpDescriptor(failedResult, receipt, true, false, "");
    Require(failedDescriptor.ButtonLabel == "Stage Repair", "failed command results should stage repair");
    var cancelledResult = new AgentCommandResult(
        false,
        "PowerShell",
        "npm run build",
        "C:\\work",
        -1,
        "",
        "",
        TimeSpan.FromMilliseconds(20),
        false,
        true,
        "");
    var cancelledDescriptor = AgentCommandResultService.ResultFollowUpDescriptor(cancelledResult, receipt, true, false, "");
    Require(cancelledDescriptor.ButtonLabel == "Stage Retry", "cancelled command results should stage retry");
    var noChangeReceipt = new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(
        "Files: +0 created, ~0 modified, -0 deleted",
        [],
        [],
        [],
        false);
    var noChangeBuildResult = new AgentCommandResult(
        true,
        "PowerShell",
        "Write-Host \"done\"",
        "C:\\work",
        0,
        "done",
        "",
        TimeSpan.FromMilliseconds(20),
        false,
        false,
        "");
    Require(
        AgentCommandResultService.SuccessfulNoChangeRequiresRepair(noChangeBuildResult, noChangeReceipt, promptRequiresCommand: true, isArtifactVerificationResult: false),
        "successful no-change app commands should require repair");
    var noChangeDescriptor = AgentCommandResultService.ResultFollowUpDescriptor(noChangeBuildResult, noChangeReceipt, true, false, "");
    Require(noChangeDescriptor.ButtonLabel == "Stage Repair", "successful no-change app commands should stage repair");
    var verifyResult = new AgentCommandResult(
        true,
        "PowerShell",
        "Test-Path .\\index.html",
        "C:\\work",
        0,
        "True",
        "",
        TimeSpan.FromMilliseconds(20),
        false,
        false,
        "");
    Require(
        !AgentCommandResultService.SuccessfulNoChangeRequiresRepair(verifyResult, noChangeReceipt, promptRequiresCommand: true, isArtifactVerificationResult: false),
        "successful no-change verification commands should not require repair");
    Require(
        AgentCommandResultService.SuccessfulNoChangeIsExpected(verifyResult, isArtifactVerificationResult: false),
        "read-only verification commands should be expected no-change results");
    var artifactVerification = new AgentWorkspaceCoordinator.AgentArtifactVerification(
        "Static web",
        "index.html",
        "PowerShell",
        "Start-Process .\\index.html",
        true,
        false,
        false,
        0,
        new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
    var artifactResult = new AgentCommandResult(
        true,
        "PowerShell",
        "Start-Process .\\index.html",
        "C:\\work",
        0,
        "",
        "",
        TimeSpan.FromMilliseconds(20),
        false,
        false,
        "");
    Require(
        AgentCommandResultService.IsArtifactVerificationResult(true, artifactVerification, artifactResult),
        "artifact result policy should match equivalent artifact commands");
    var artifactDescriptor = AgentCommandResultService.ResultFollowUpDescriptor(
        artifactResult,
        noChangeReceipt,
        promptRequiresCommand: true,
        isArtifactVerificationResult: true,
        artifactActionTitle: artifactVerification.ActionTitle);
    Require(artifactDescriptor.ButtonLabel == "Stage Next", "successful artifact previews should stage next instead of repair");
    Require(
        AgentCommandResultService.CommandNextAction(artifactResult, noChangeReceipt, true, true, artifactVerification.ActionTitle)
            .Contains("no workspace file changes were expected", StringComparison.OrdinalIgnoreCase),
        "artifact next action should explain that no file changes were expected");
    var normalizedLoopCommand = AgentCommandResultService.NormalizeCommandForLoopComparison("npm\r\n  run\tbuild");
    Require(normalizedLoopCommand == "npm run build", "command result service should normalize loop-guard commands");
    var repeatedPreview = new AgentCommandPreview(
        true,
        "PowerShell",
        "npm    run build",
        "C:\\work",
        "powershell.exe",
        "",
        "npm run build",
        [],
        "");
    var repeatedHistory = new[]
    {
        new AgentWorkspaceCoordinator.AgentCommandHistoryItem(
            2,
            new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
            "PowerShell",
            "npm run build",
            "Exit 0",
            "Auto Continue",
            "Completed",
            "C:\\work",
            "Files: +0 created, ~0 modified, -0 deleted",
            0)
    };
    var repeatedDecision = AgentAutonomyPolicyService.EvaluateRepeatedCommand(repeatedPreview, repeatedHistory);
    Require(repeatedDecision.ShouldPause, "autonomy policy should pause repeated auto-continue commands");
    Require(repeatedDecision.Reason.Contains("same command", StringComparison.OrdinalIgnoreCase), "repeated-command pauses should explain the duplicate command");
    var blockedHistoryDecision = AgentAutonomyPolicyService.EvaluateRepeatedCommand(
        repeatedPreview,
        [repeatedHistory[0] with { Status = "Blocked" }]);
    Require(!blockedHistoryDecision.ShouldPause, "autonomy policy should ignore blocked previews when detecting repeated completed commands");
    var firstNoChangePolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        noChangeBuildResult,
        noChangeReceipt,
        consecutiveNoChangeResults: 0,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        successfulNoChangeIsExpected: false);
    Require(!firstNoChangePolicy.ShouldPause && firstNoChangePolicy.NextConsecutiveNoChangeResults == 1, "first no-change app command should be counted without pausing");
    var secondNoChangePolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        noChangeBuildResult,
        noChangeReceipt,
        consecutiveNoChangeResults: firstNoChangePolicy.NextConsecutiveNoChangeResults,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        successfulNoChangeIsExpected: false);
    Require(secondNoChangePolicy.ShouldPause, "second consecutive no-change app command should pause autonomy");
    Require(secondNoChangePolicy.Reason.Contains("no-change", StringComparison.OrdinalIgnoreCase), "no-change loop pauses should explain repeated no-change commands");
    var failedResetPolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        failedResult,
        noChangeReceipt,
        consecutiveNoChangeResults: 1,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        successfulNoChangeIsExpected: false);
    Require(!failedResetPolicy.ShouldPause && failedResetPolicy.NextConsecutiveNoChangeResults == 0, "failed commands should reset no-change loop counters");
    var changedResetPolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        briefResult,
        receipt,
        consecutiveNoChangeResults: 1,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        successfulNoChangeIsExpected: false);
    Require(!changedResetPolicy.ShouldPause && changedResetPolicy.NextConsecutiveNoChangeResults == 0, "file-changing commands should reset no-change loop counters");
    var limitedResetPolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        briefResult,
        limitedReceipt,
        consecutiveNoChangeResults: 1,
        promptRequiresCommand: true,
        isArtifactVerificationResult: false,
        successfulNoChangeIsExpected: false);
    Require(!limitedResetPolicy.ShouldPause && limitedResetPolicy.NextConsecutiveNoChangeResults == 0, "limited scans should avoid false no-change loop pauses");
    var artifactResetPolicy = AgentAutonomyPolicyService.EvaluateAutoContinueResult(
        artifactResult,
        noChangeReceipt,
        consecutiveNoChangeResults: 1,
        promptRequiresCommand: true,
        isArtifactVerificationResult: true,
        successfulNoChangeIsExpected: false);
    Require(!artifactResetPolicy.ShouldPause && artifactResetPolicy.NextConsecutiveNoChangeResults == 0, "artifact previews and checks should not trip no-change loop guards");
    Require(
        AgentAutonomyPolicyService.AutoContinueChangeHint(artifactResult, noChangeReceipt, isArtifactVerificationResult: true)
            .Contains("artifact preview or verification", StringComparison.OrdinalIgnoreCase),
        "auto-continue hint should mention artifact results");
    Require(
        AgentAutonomyPolicyService.AutoContinueChangeHint(briefResult, receipt, isArtifactVerificationResult: false)
            .Contains("next smallest useful", StringComparison.OrdinalIgnoreCase),
        "auto-continue hint should describe changed-file follow-up work");
    Require(
        AgentAutonomyPolicyService.AutoContinueChangeHint(briefResult, limitedReceipt, isArtifactVerificationResult: false)
            .Contains("scan hit its cap", StringComparison.OrdinalIgnoreCase),
        "auto-continue hint should describe limited scan uncertainty");
    Require(
        AgentAutonomyPolicyService.AutoContinueChangeHint(noChangeBuildResult, noChangeReceipt, isArtifactVerificationResult: false)
            .Contains("repair or file-writing", StringComparison.OrdinalIgnoreCase),
        "auto-continue hint should prioritize repair for no-change app commands");
    var autoPrompt = AgentAutonomyPolicyService.BuildAutoContinuePrompt(briefResult, receipt, "Shell: PowerShell", isArtifactVerificationResult: false);
    Require(autoPrompt.Contains("Continue this Agent run automatically", StringComparison.Ordinal), "auto-continue prompt should keep the stable instruction title");
    Require(autoPrompt.Contains("Shell: PowerShell", StringComparison.Ordinal), "auto-continue prompt should include latest command context");
    Require(AgentAutonomyPolicyService.FollowUpActivityDetail(1) == "1 follow-up step left.", "autonomy policy should format singular follow-up counts");
    Require(AgentAutonomyPolicyService.FollowUpActivityDetail(2) == "2 follow-up steps left.", "autonomy policy should format plural follow-up counts");
    Require(AgentWorkspaceCoordinator.PromptLikelyRequiresCommand("Write a tiny app in this workspace."), "app-writing prompts should require a command proposal");
    Require(AgentWorkspaceCoordinator.PromptLikelyRequiresCommand("Bootstrap a tiny UI prototype."), "prototype/setup prompts should require a command proposal");
    Require(AgentWorkspaceCoordinator.PromptLikelyRequiresCommand("Make a website landing page."), "website/page prompts should require a command proposal");
    Require(AgentWorkspaceCoordinator.PromptLikelyRequiresCommand("Repair the previous app build."), "repair prompts should require a command proposal");
    Require(!AgentWorkspaceCoordinator.PromptLikelyRequiresCommand("Explain this architecture."), "explanation prompts should not require a command proposal");
    Require(AgentWorkspaceCoordinator.CommandLooksLikeVerificationOrInspection("Test-Path .\\index.html"), "Test-Path should be treated as expected no-change verification");
    Require(AgentWorkspaceCoordinator.CommandLooksLikeVerificationOrInspection("dotnet test .\\TinyApp.sln"), "dotnet test should be treated as expected no-change verification");
    Require(AgentWorkspaceCoordinator.CommandLooksLikeVerificationOrInspection("Get-ChildItem . -Force"), "workspace inspection should be treated as expected no-change work");
    Require(!AgentWorkspaceCoordinator.CommandLooksLikeVerificationOrInspection("Set-Content .\\index.html -Value hi"), "file-writing commands should not be exempted from no-change repair");
    Require(!AgentWorkspaceCoordinator.CommandLooksLikeWorkspaceMutation("Write-Host \"File demo_app.py created successfully.\""), "status-only commands should not count as workspace mutations");
    Require(AgentWorkspaceCoordinator.CommandLooksLikeWorkspaceMutation("Set-Content -Path .\\index.html -Value hi"), "file-writing commands should count as workspace mutations");
    var rawFileSuggestion = AgentCommandProposalService.ExtractFileWriteSuggestion("""
        File: demo_app.py
        Content:
        print("--- AI Arena Demo Application ---")
        print("This is a randomly generated demo application.")

        Command proposal:
        ```powershell
        Write-Host "File demo_app.py created successfully."
        ```
        """);
    Require(rawFileSuggestion is not null, "raw File/Content local-model replies should be materialized into file suggestions");
    var rawFileSuggestionExtracted = rawFileSuggestion ?? throw new InvalidOperationException("Missing raw file suggestion.");
    Require(rawFileSuggestionExtracted.Files.Single().Path == "demo_app.py", "raw File/Content extraction should preserve the target path");
    Require(!rawFileSuggestionExtracted.Files.Single().Content.Contains("Content:", StringComparison.Ordinal), "raw File/Content extraction should not include the content header in the written file");
    var profileRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-profile", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(profileRoot);
    try
    {
        Directory.CreateDirectory(Path.Combine(profileRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(profileRoot, "src"));
        Directory.CreateDirectory(Path.Combine(profileRoot, "node_modules"));
        File.WriteAllText(Path.Combine(profileRoot, "package.json"), """
            {
              "scripts": {
                "build": "vite build",
                "test": "vitest run",
                "lint": "eslint ."
              }
            }
            """);
        File.WriteAllText(Path.Combine(profileRoot, "index.html"), "<main>Hello</main>");
        File.WriteAllText(Path.Combine(profileRoot, "src", "TinyApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(profileRoot, "node_modules", "ignored.csproj"), "<Project />");
        var profile = AgentWorkspaceCoordinator.BuildWorkspaceProfile(profileRoot);
        Require(profile.Contains("Node", StringComparison.Ordinal), "workspace profile should detect Node projects");
        Require(profile.Contains(".NET", StringComparison.Ordinal), "workspace profile should detect nested .NET project files");
        Require(profile.Contains("Static web", StringComparison.Ordinal), "workspace profile should detect static web entry files");
        Require(profile.Contains("npm run build", StringComparison.Ordinal), "workspace profile should extract package build script hints");
        Require(profile.Contains("npm test", StringComparison.Ordinal), "workspace profile should extract package test script hints");
        Require(profile.Contains("src/TinyApp.csproj", StringComparison.Ordinal), "workspace profile should include one-level nested key files");
        Require(!profile.Contains("ignored.csproj", StringComparison.Ordinal), "workspace profile should ignore cache/dependency folders");
        Require(profile.Contains("Git: repository detected", StringComparison.Ordinal), "workspace profile should surface git repository detection");
    }
    finally
    {
        if (Directory.Exists(profileRoot))
        {
            Directory.Delete(profileRoot, recursive: true);
        }
    }

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-command-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                """
                I will stage the first creation command.

                Command proposal:
                ```bash
                cat > index.html <<'EOF'
                <main id="app">Hello from Bash heredoc</main>
                EOF
                cat <<'EOF' > scripts/app.js
                document.body.dataset.ready = "yes";
                EOF
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(!coordinator.DebugCommandStopEnabled, "Agent command stop should initialize disabled");
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled");
            coordinator.DebugSendAsync().GetAwaiter().GetResult();

            Require(modelClient.CompleteCalls == 1, "Agent software chat should run Builder only while Builder-only mode is enabled");
            Require(coordinator.DebugPhaseState("planner") == "Skipped", "Builder-only mode should mark Planner skipped in the progress rail");
            Require(coordinator.DebugPhaseState("reviewer") == "Skipped", "Builder-only mode should mark Reviewer skipped in the progress rail");
            Require(coordinator.DebugPhaseState("builder") == "Done", "Builder-only mode should still mark Builder done");
            Require(modelClient.CompletedConfigs.Single().Model == "shared-model", "Agent Builder should use the shared provider model, not arena participant role models");
            Require(coordinator.DebugSelectedShell == "PowerShell", "Builder bash heredoc proposals should convert to PowerShell in the approval rail");
            Require(coordinator.DebugCommandText.Contains("FromBase64String", StringComparison.Ordinal), "Builder bash heredoc proposals should be converted to safe file materialization");
            Require(coordinator.DebugCommandText.Contains("index.html", StringComparison.Ordinal), "converted heredoc commands should keep generated file paths");
            Require(!coordinator.DebugCommandText.Contains("cat > index.html", StringComparison.Ordinal), "converted heredoc commands should not run POSIX redirection through cmd.exe");
            Require(coordinator.DebugCommandRunEnabled, "staged Builder command should be previewed and ready for explicit approval");
            Require(!coordinator.DebugCommandStopEnabled, "Agent command stop should stay disabled until an approved command is running");
            Require(coordinator.DebugApprovalText.Contains(root, StringComparison.OrdinalIgnoreCase), "approval preview should show the active working directory");
            Require(coordinator.DebugCommandSource.Contains("Builder proposal", StringComparison.Ordinal), "approval rail should label commands staged from Builder output");
            Require(coordinator.DebugTopModeText == "Preview ready", "Agent top mode should surface preview-ready command state");
            Require(coordinator.DebugPhaseSummary.Contains("staged", StringComparison.OrdinalIgnoreCase), "Agent phase summary should report the staged Builder command");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("staged", StringComparison.OrdinalIgnoreCase), "Build Evidence summary should report staged commands");
            Require(coordinator.DebugBuildEvidenceCount >= 7, "Build Evidence should render workspace, command, preview, file, and verification rows");
            Require(coordinator.DebugLastMessageKind == "Action", "staged commands should add a visible center action card");
            Require(coordinator.DebugLastMessageBody.Contains("approval rail", StringComparison.OrdinalIgnoreCase), "center action card should point the user to the approval rail");
            Require(statusText.Text == "Command proposal staged for approval.", "Agent status should tell the user the command is waiting for approval");

            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugRunApprovedCommandAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(File.Exists(Path.Combine(root, "index.html")), "approved converted heredoc command should create the app entry file");
            Require(File.Exists(Path.Combine(root, "scripts", "app.js")), "approved converted heredoc command should create nested app files");
            Require(File.ReadAllText(Path.Combine(root, "index.html")).Contains("Hello from Bash heredoc", StringComparison.Ordinal), "approved converted heredoc command should preserve app markup");
            Require(coordinator.DebugArtifactSuggestion.Contains("Static web", StringComparison.Ordinal), "converted heredoc app files should surface a generated artifact suggestion");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-rescue-command", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                "Builder: here is prose about what I would do, but no command.",
                """
                Command proposal:
                ```powershell
                Set-Content -Path .\TinyManualApp.html -Value "<main>Manual rescued app</main>"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(!coordinator.DebugCommandStopEnabled, "Agent command stop should initialize disabled for prose-only runs");
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for prose-only runs");
            coordinator.DebugSendAsync().GetAwaiter().GetResult();

            Require(modelClient.CompleteCalls == 2, "manual app requests should recover prose-only Builder output with one additional Builder call");
            Require(coordinator.DebugPromptText.Length == 0, "manual Auto Rescue should not paste recovery instructions into the operator composer");
            Require(coordinator.DebugCommandText.Contains("TinyManualApp.html", StringComparison.Ordinal), "manual Auto Rescue should stage the recovered command for approval");
            Require(coordinator.DebugCommandRunEnabled, "manual Auto Rescue should leave the recovered command waiting for explicit approval");
            Require(coordinator.DebugCommandSource.Contains("Builder proposal", StringComparison.OrdinalIgnoreCase), "manual Auto Rescue should preserve Builder command provenance");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("staged", StringComparison.OrdinalIgnoreCase), "Build Evidence should mark the recovered command as staged");
            Require(statusText.Text == "Command proposal staged for approval.", "manual Auto Rescue should end at manual command approval");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-rescue-replaces-stale-command", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write an app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                """
                Command proposal:
                ```powershell
                Set-Content -Path "C:\AI Arena Outside\blocked.txt" -Value "blocked"
                ```
                """,
                """
                Command proposal:
                ```powershell
                Set-Content -Path .\RescueReplacement.html -Value "<main>Replacement app</main>"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for rescue retry runs");
            coordinator.DebugSendAsync().GetAwaiter().GetResult();

            Require(modelClient.CompleteCalls == 2, "blocked app commands should get one internal Builder rescue retry");
            Require(coordinator.DebugPromptText.Length == 0, "rescue replacement should not paste recovery text into the operator composer");
            Require(coordinator.DebugCommandText.Contains("RescueReplacement.html", StringComparison.Ordinal), "rescue replacement should stage the valid workspace command");
            Require(!coordinator.DebugCommandText.Contains("AI Arena Outside", StringComparison.Ordinal), "rescue replacement should clear the stale outside-workspace command");
            Require(coordinator.DebugCommandRunEnabled, "rescue replacement should leave the valid command ready for manual approval");
            Require(!coordinator.DebugCommandSource.Contains("held", StringComparison.OrdinalIgnoreCase), "rescue replacement should not hide the valid command behind Held");
            Require(statusText.Text == "Command proposal staged for approval.", "rescue replacement should finish at explicit command approval");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-auto-rescue-command", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var approveAllStatusText = new TextBlock();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                "Builder: here is prose about what I would do, but no command.",
                """
                Command proposal:
                ```powershell
                Set-Content -Path .\TinyAutoApp.html -Value "<main>Auto rescued app</main>"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                approveAllStatusText,
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for Auto Rescue runs");
            coordinator.DebugSetAutoApproveForSession(true);
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugSendAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            var appPath = Path.Combine(root, "TinyAutoApp.html");
            Require(File.Exists(appPath), "Full Access should auto-rescue prose-only app output and run the rescued command");
            Require(File.ReadAllText(appPath).Contains("Auto rescued app", StringComparison.Ordinal), "auto-rescued commands should write the requested app file");
            Require(modelClient.CompleteCalls == 2, "Auto Rescue should run one additional Builder call");
            Require(coordinator.DebugAutoApproveEnabled, "Full Access should remain enabled after Auto Rescue runs");
            Require(coordinator.DebugAutoRescueRemaining == 1, "Auto Rescue should consume one bounded retry");
            Require(coordinator.DebugCommandHistoryCount == 1, "auto-rescued approved commands should be recorded in history");
            Require(coordinator.DebugCommandHistoryCopyText.Contains("TinyAutoApp.html", StringComparison.Ordinal), "command history should include the auto-rescued command");
            Require(coordinator.DebugWorkSummary.Contains("Exit 0", StringComparison.Ordinal), "auto-rescued command should produce a visible work summary");
            Require(coordinator.DebugWorkSummary.Contains("Artifact:", StringComparison.Ordinal), "auto-rescued command should surface artifact suggestions");
            Require(coordinator.DebugLatestWorkBrief.Contains("TinyAutoApp.html", StringComparison.Ordinal), "work brief should include the auto-rescued command target");
            Require(coordinator.DebugLatestWorkBrief.Contains("Artifact suggestion:", StringComparison.Ordinal), "work brief should include generated artifact suggestions");
            Require(coordinator.DebugArtifactSuggestion.Contains("Static web", StringComparison.Ordinal), "generated HTML files should create a static web artifact suggestion");
            Require(coordinator.DebugLatestWorkBrief.Contains("Auto Rescue", StringComparison.Ordinal), "work brief should include the session autonomy context");
            Require(coordinator.DebugLatestWorkBrief.Contains("workspace session", StringComparison.Ordinal), "work brief should describe the workspace-scoped autonomy contract");
            Require(coordinator.DebugCopyBriefEnabled, "work brief should be copyable after an auto-rescued command");
            Require(coordinator.DebugStageNextEnabled, "stage next should enable after a command result");
            Require(coordinator.DebugStageNextLabel == "Stage Next", "successful file-changing commands should expose Stage Next");
            Require(coordinator.DebugStageNextToolTip.Contains("continuation", StringComparison.OrdinalIgnoreCase), "Stage Next tooltip should describe continuation behavior");
            Require(coordinator.DebugStageVerifyEnabled, "stage verify should enable after a command result");
            Require(coordinator.DebugStageArtifactEnabled, "artifact suggestions should expose a direct stage action after generated files are detected");
            Require(coordinator.DebugCommandText.Length == 0, "completed auto-approved commands should clear stale command text from the approval rail");
            coordinator.DebugStageNextPromptFromResult();
            Require(coordinator.DebugPromptText.Contains("Recommended next action:", StringComparison.Ordinal), "Stage Next should include the current recommended action");
            Require(coordinator.DebugPromptText.Contains("Latest work brief:", StringComparison.Ordinal), "Stage Next should include the latest work brief");
            Require(coordinator.DebugPromptText.Contains("Artifact suggestion:", StringComparison.Ordinal), "Stage Next should include artifact context");
            Require(coordinator.DebugPromptText.Contains("TinyAutoApp.html", StringComparison.Ordinal), "Stage Next should preserve latest command context");
            Require(coordinator.DebugLastMessageKind == "Action", "Stage Next should add a visible action card");
            Require(coordinator.DebugLastMessageBody.Contains("previewable command", StringComparison.OrdinalIgnoreCase), "Stage Next card should explain the next command handoff");
            coordinator.DebugStageVerifyPromptFromBrief();
            Require(coordinator.DebugPromptText.Contains("Verify the app", StringComparison.Ordinal), "Stage Verify should prepare a verification prompt from the latest result");
            Require(coordinator.DebugPromptText.Contains("TinyAutoApp.html", StringComparison.Ordinal), "verification prompt should preserve latest command context");
            Require(coordinator.DebugPromptText.Contains("Agent work brief", StringComparison.Ordinal), "verification prompt should include the latest work brief");
            Require(coordinator.DebugPromptText.Contains("Suggested preview command", StringComparison.Ordinal), "verification prompt should include the artifact preview command");
            Require(!coordinator.DebugCommandRunEnabled, "auto-rescued commands should not remain waiting for manual approval");
            Require(approveAllStatusText.Text.Contains("Auto Rescue", StringComparison.Ordinal), "Full Access status should surface the rescue behavior");
            Require(approveAllStatusText.Text.Contains("blocked previews", StringComparison.Ordinal), "Full Access status should keep blocked previews visible");
            Require(approveAllStatusText.Text.Contains("workspace changes", StringComparison.Ordinal), "Full Access status should explain workspace-change reset behavior");
            coordinator.DebugSetAutoApproveForSession(false);
            coordinator.DebugStageArtifactSuggestionCommand();
            Require(coordinator.DebugCommandText.Contains("Start-Process", StringComparison.Ordinal) && coordinator.DebugCommandText.Contains("TinyAutoApp.html", StringComparison.Ordinal), "Use Artifact should stage the suggested artifact preview command");
            Require(coordinator.DebugCommandRunEnabled, "Use Artifact should preview the staged artifact command for manual approval");
            Require(coordinator.DebugCommandSource.Contains("artifact suggestion", StringComparison.OrdinalIgnoreCase), "Use Artifact should label artifact command provenance");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-artifact-verification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<main>artifact check</main>");
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox();
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                new SequentialAgentModelClient(),
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSetCommandRequiredForTest(true);
            coordinator.DebugSetLatestArtifactSuggestion(new AgentWorkspaceCoordinator.AgentArtifactSuggestion(
                "Static web",
                "index.html",
                "PowerShell",
                "Test-Path .\\index.html",
                "Static web artifact at index.html; verify file presence with `Test-Path .\\index.html`."));
            coordinator.DebugStageArtifactSuggestionCommand();
            Require(coordinator.DebugCommandRunEnabled, "artifact verification commands should preview before running");
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugRunApprovedCommandAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(coordinator.DebugArtifactVerification.Contains("succeeded", StringComparison.Ordinal), "artifact verification should record success");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("Artifact check succeeded", StringComparison.Ordinal), "artifact verification success should update Build Evidence");
            Require(coordinator.DebugWorkSummary.Contains("No tracked file changes expected", StringComparison.Ordinal), "artifact verification summaries should treat no changes as expected");
            Require(coordinator.DebugWorkSummary.Contains("Artifact check:", StringComparison.Ordinal), "artifact verification summaries should include the artifact check result");
            Require(coordinator.DebugLatestWorkBrief.Contains("Artifact verification:", StringComparison.Ordinal), "work briefs should include artifact verification results");
            Require(coordinator.DebugStageNextEnabled, "artifact verification results should enable Stage Next");
            Require(coordinator.DebugStageNextLabel == "Stage Next", "successful artifact checks should expose Stage Next");
            Require(coordinator.DebugStageNextToolTip.Contains("artifact", StringComparison.OrdinalIgnoreCase), "artifact Stage Next tooltip should mention artifact context");
            Require(!coordinator.DebugWorkSummary.Contains("repair", StringComparison.OrdinalIgnoreCase), "successful artifact verification should not suggest repair in the work summary");
            Require(!coordinator.DebugLatestWorkBrief.Contains("repair", StringComparison.OrdinalIgnoreCase), "successful artifact verification should not suggest repair in the work brief");
            Require(statusText.Text.Contains("Artifact preview", StringComparison.OrdinalIgnoreCase) || statusText.Text.Contains("Artifact verification", StringComparison.OrdinalIgnoreCase) || statusText.Text.Contains("Artifact check", StringComparison.OrdinalIgnoreCase), "status should explain artifact verification success");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-readonly-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<main>readonly verify</main>");
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox();
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var previewButton = new Button();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = powershell;

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                new SequentialAgentModelClient(),
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                previewButton,
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSetCommandRequiredForTest(true);
            commandText.Text = "Test-Path .\\index.html";
            previewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(coordinator.DebugCommandRunEnabled, "read-only verification command should preview before running");

            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugRunApprovedCommandAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(coordinator.DebugBuildEvidenceSummary.Contains("verify", StringComparison.OrdinalIgnoreCase), "successful read-only verification should not be summarized as repair");
            Require(coordinator.DebugWorkSummary.Contains("No tracked file changes", StringComparison.Ordinal), "read-only verification summaries should mention no changes without repair");
            Require(!coordinator.DebugWorkSummary.Contains("repair", StringComparison.OrdinalIgnoreCase), "successful read-only verification should not suggest repair");
            Require(coordinator.DebugStageNextLabel == "Stage Next", "successful read-only verification should expose Stage Next");
            Require(!coordinator.DebugStageNextLabel.Contains("Repair", StringComparison.OrdinalIgnoreCase), "successful read-only verification should not expose Stage Repair");
            Require(!statusText.Text.Contains("app may not be written", StringComparison.OrdinalIgnoreCase), "read-only verification status should not imply missing app writes");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-artifact-auto-approve", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<main>artifact auto check</main>");
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox();
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                new SequentialAgentModelClient(),
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSetCommandRequiredForTest(true);
            coordinator.DebugSetLatestArtifactSuggestion(new AgentWorkspaceCoordinator.AgentArtifactSuggestion(
                "Static web",
                "index.html",
                "PowerShell",
                "Test-Path .\\index.html",
                "Static web artifact at index.html; verify file presence with `Test-Path .\\index.html`."));
            coordinator.DebugSetAutoApproveForSession(true);
            coordinator.DebugStageArtifactSuggestionCommand();

            var started = DateTimeOffset.UtcNow;
            while ((string.IsNullOrWhiteSpace(coordinator.DebugArtifactVerification)
                    || coordinator.DebugCommandRunEnabled
                    || coordinator.DebugCommandHistoryCount == 0)
                && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10))
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            Require(coordinator.DebugArtifactVerification.Contains("succeeded", StringComparison.Ordinal), "Full Access should preserve artifact verification attribution for Use Artifact commands");
            Require(coordinator.DebugCommandHistoryCount == 1, "auto-approved artifact checks should be recorded once");
            Require(!coordinator.DebugCommandRunEnabled, "auto-approved artifact checks should not remain waiting for manual approval");
            Require(coordinator.DebugCommandText.Length == 0, "auto-approved artifact checks should clear the command editor after completion");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("Artifact check succeeded", StringComparison.Ordinal), "auto-approved artifact checks should update Build Evidence as an artifact check");
            Require(coordinator.DebugWorkSummary.Contains("No tracked file changes", StringComparison.Ordinal)
                && !coordinator.DebugWorkSummary.Contains("repair", StringComparison.OrdinalIgnoreCase), "auto-approved artifact checks should not trigger no-change repair copy");
            Require(coordinator.DebugLatestWorkBrief.Contains("Artifact verification:", StringComparison.Ordinal), "auto-approved artifact checks should preserve verification in the work brief");
            Require(statusText.Text.Contains("Artifact preview", StringComparison.OrdinalIgnoreCase) || statusText.Text.Contains("Artifact verification", StringComparison.OrdinalIgnoreCase) || statusText.Text.Contains("Artifact check", StringComparison.OrdinalIgnoreCase), "auto-approved artifact checks should explain preview/verification success");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-risky-auto-approve", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox();
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var previewButton = new Button();
            var runButton = new Button();
            var approveAllStatusText = new TextBlock();
            var approvalText = new TextBlock();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = powershell;

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                new SequentialAgentModelClient(),
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                previewButton,
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                approveAllStatusText,
                new Button(),
                new TextBlock(),
                approvalText,
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSetAutoApproveForSession(true);
            commandText.Text = "Remove-Item .\\missing-risk-probe -Recurse -Force -ErrorAction SilentlyContinue";
            previewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Require(coordinator.DebugCommandRunEnabled, "risky previews should remain staged for manual approval");
            Require(coordinator.DebugCommandHistoryCount == 0, "Full Access should not auto-run destructive previews");
            Require(approvalText.Text.Contains("Full Access will not auto-run", StringComparison.Ordinal), "manual-review copy should explain why auto-run paused");
            Require(approvalText.Text.Contains("Destructive", StringComparison.Ordinal), "manual-review copy should list the risky flag");
            Require(statusText.Text.Contains("manual", StringComparison.OrdinalIgnoreCase), "global status should explain manual review for risky previews");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("manual review", StringComparison.OrdinalIgnoreCase), "Build Evidence should show the manual-review pause");
            Require(approveAllStatusText.Text.Contains("Full Access is on", StringComparison.Ordinal), "Full Access should remain armed after pausing a risky preview");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-mid-run-autonomy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var approveAllButton = new Button();
            var approveAllStatusText = new TextBlock();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new DelayedSequentialAgentModelClient(
                TimeSpan.FromMilliseconds(20),
                """
                Command proposal:
                ```powershell
                Set-Content -Path .\MidRunApp.html -Value "<main>mid-run autonomy</main>"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                approveAllButton,
                approveAllStatusText,
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            var task = coordinator.DebugSendAsync();
            Require(approveAllButton.IsEnabled, "Full Access should remain available while Agent is thinking");
            Require((approveAllButton.Content?.ToString() ?? "").Equals("Approval", StringComparison.Ordinal), "Agent autonomy button should show Approval before Full Access is armed");
            approveAllButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(coordinator.DebugAutoApproveEnabled, "Full Access should arm during an active Agent run");
            Require((approveAllButton.Content?.ToString() ?? "").Equals("Full Access", StringComparison.Ordinal), "Agent autonomy button should show Full Access after it is armed");
            Require(coordinator.DebugLastMessageKind == "Action", "mid-run Full Access should add a visible session-autonomy card");
            Require(coordinator.DebugLastMessageBody.Contains("workspace session", StringComparison.Ordinal), "session-autonomy card should scope trust to the workspace session");
            Require(coordinator.DebugLastMessageBody.Contains("preview validation", StringComparison.Ordinal), "session-autonomy card should preserve preview validation");
            Require(coordinator.DebugLastMessageBody.Contains("loop guards", StringComparison.Ordinal), "session-autonomy card should mention loop guards");
            task.ContinueWith(completed =>
            {
                if (completed.Exception is not null)
                {
                    asyncFailure = completed.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            var appPath = Path.Combine(root, "MidRunApp.html");
            Require(File.Exists(appPath), "mid-run Full Access should run the command staged by the active Agent turn");
            Require(File.ReadAllText(appPath).Contains("mid-run autonomy", StringComparison.Ordinal), "mid-run auto-approved commands should write the requested app file");
            Require(coordinator.DebugCommandHistoryCount == 1, "mid-run auto-approved commands should be recorded in history");
            Require(!coordinator.DebugCommandRunEnabled, "mid-run auto-approved commands should not remain waiting for manual approval");
            Require(coordinator.DebugAutoApproveStatus.Contains("Full Access is on", StringComparison.Ordinal), "Full Access status should remain visible after mid-run arming");
            Require(coordinator.DebugAutoApproveStatus.Contains("blocked previews", StringComparison.Ordinal), "Full Access status should keep blocked previews visible after mid-run arming");
            Require(coordinator.DebugLatestWorkBrief.Contains("workspace session", StringComparison.Ordinal), "mid-run approved work briefs should preserve the autonomy contract");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-auto-approve", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var runButton = new Button();
            var approveAllStatusText = new TextBlock();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                """
                Here are the app files.

                File: index.html
                Content:
                <main id="app">Hello Arena</main>

                File: scripts/app.js
                Content:
                document.body.dataset.ready = "yes";

                Command proposal:
                ```powershell
                Write-Host "File index.html created successfully."
                ```
                """,
                """
                Command proposal:
                ```powershell
                Set-Content -Path .\follow-up.txt -Value "auto continued"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                runButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                approveAllStatusText,
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for Auto Continue runs");
            coordinator.DebugSetAutoContinueForSession(true, 1);
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugSendAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            var indexPath = Path.Combine(root, "index.html");
            var scriptPath = Path.Combine(root, "scripts", "app.js");
            var followUpPath = Path.Combine(root, "follow-up.txt");
            Require(File.Exists(indexPath), "Full Access should auto-run generated file write commands");
            Require(File.ReadAllText(indexPath).Contains("Hello Arena", StringComparison.Ordinal), "auto-approved file writes should preserve HTML content");
            Require(File.Exists(scriptPath), "auto-approved file writes should create nested app files");
            Require(File.Exists(followUpPath), "Auto Continue should ask for and run a follow-up command after command output");
            Require(File.ReadAllText(followUpPath).Contains("auto continued", StringComparison.Ordinal), "Auto Continue follow-up commands should run in the workspace");
            Require(modelClient.CompleteCalls == 2, "Auto Continue should run one additional Builder call when the budget is one");
            Require(coordinator.DebugAutoApproveEnabled, "Full Access should remain enabled for the session after an auto-run");
            Require(!coordinator.DebugAutoContinueEnabled, "Auto Continue should turn off after its follow-up budget is spent");
            Require(coordinator.DebugAutoContinueRemaining == 0, "Auto Continue should spend its follow-up budget");
            Require(coordinator.DebugAutoApproveStatus.Contains("Full Access is on", StringComparison.Ordinal), "Full Access status should make autonomy visible");
            Require(coordinator.DebugAutoContinueStatus.Contains("Auto Continue is off", StringComparison.Ordinal), "Auto Continue status should explain when the loop is no longer armed");
            Require(!coordinator.DebugCommandRunEnabled, "auto-approved commands should not remain waiting for manual approval");
            Require(coordinator.DebugCommandText.Length == 0, "Auto Continue should not leave stale command text after completed commands");
            Require(coordinator.DebugCommandHistoryCount == 2, "command history should record the initial and follow-up approved commands");
            Require(coordinator.DebugCommandHistorySummary.Contains("2 recent commands", StringComparison.Ordinal), "command history summary should count recent commands");
            Require(coordinator.DebugCommandHistorySummary.Contains("Exit 0", StringComparison.Ordinal), "command history summary should show the latest command status");
            Require(coordinator.DebugCommandHistoryCopyText.Contains("follow-up.txt", StringComparison.Ordinal), "command history copy text should include the follow-up command");
            Require(coordinator.DebugCommandHistoryCopyText.Contains("index.html", StringComparison.Ordinal), "command history copy text should include the generated app command");
            Require(coordinator.DebugReplayLastCommandEnabled, "command history should enable replay after completed commands");
            Require(statusText.Text.Contains("succeeded", StringComparison.OrdinalIgnoreCase) || statusText.Text.Contains("Exit 0", StringComparison.OrdinalIgnoreCase), "auto-approved commands should surface completion status");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-node-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Build a tiny Node web app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var approveAllStatusText = new TextBlock();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                """
                Here are the app files.

                package.json
                ```json
                {
                  "scripts": {
                    "start": "vite --host 127.0.0.1"
                  },
                  "dependencies": {
                    "@vitejs/plugin-react": "latest",
                    "vite": "latest"
                  },
                  "devDependencies": {}
                }
                ```

                index.html
                ```html
                <main id="app">Node preview app</main>
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                approveAllStatusText,
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSetAutoApproveForSession(true);
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugSendAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(File.Exists(Path.Combine(root, "package.json")), "Full Access should create package.json from generated Node snippets");
            Require(File.Exists(Path.Combine(root, "index.html")), "Full Access should create index.html from generated Node snippets");
            Require(coordinator.DebugCommandHistoryCount == 1, "Node app file materialization should run exactly one approved command");
            Require(coordinator.DebugArtifactSuggestion.Contains("Node", StringComparison.Ordinal), "generated Node files should surface a Node artifact suggestion");
            Require(coordinator.DebugArtifactSuggestion.Contains("detached preview terminal", StringComparison.OrdinalIgnoreCase), "Node start-script artifacts should explain detached preview launching");
            Require(coordinator.DebugStageArtifactEnabled, "generated Node artifacts should expose Use Artifact after file writes");

            coordinator.DebugStageArtifactSuggestionCommand();
            Require(coordinator.DebugCommandRunEnabled, "Use Artifact should stage the Node preview launcher for manual approval");
            Require(coordinator.DebugCommandText.StartsWith("Start-Process", StringComparison.Ordinal), "Use Artifact should stage a detached preview launcher");
            Require(coordinator.DebugCommandText.Contains("npm start", StringComparison.Ordinal) || coordinator.DebugCommandText.Contains("npm --prefix", StringComparison.Ordinal), "detached preview launcher should preserve the npm start command");
            Require(coordinator.DebugCommandSource.Contains("artifact suggestion", StringComparison.OrdinalIgnoreCase), "Use Artifact should label detached preview provenance");
            Require(coordinator.DebugCommandHistoryCount == 1, "Full Access should not auto-run long-running artifact previews");
            Require(approveAllStatusText.Text.Contains("blocked previews", StringComparison.Ordinal), "Full Access status should keep blocked preview behavior visible");
            Require(statusText.Text.Contains("manual", StringComparison.OrdinalIgnoreCase), "status should explain that the long-running preview needs manual review");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-loop-duplicate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var duplicateCommand = "Set-Content -Path .\\same.txt -Value \"once\"";
            var modelClient = new SequentialAgentModelClient(
                $"""
                Command proposal:
                ```powershell
                {duplicateCommand}
                ```
                """,
                $"""
                Command proposal:
                ```powershell
                {duplicateCommand}
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for loop guard runs");
            coordinator.DebugSetAutoContinueForSession(true, 2);
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugSendAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(File.Exists(Path.Combine(root, "same.txt")), "initial auto-continued duplicate test should create the first file");
            Require(modelClient.CompleteCalls == 2, "duplicate loop guard should wait until the follow-up Builder proposes a command");
            Require(coordinator.DebugCommandHistoryCount == 1, "duplicate loop guard should not run the repeated command");
            Require(!coordinator.DebugAutoContinueEnabled, "duplicate loop guard should pause Auto Continue");
            Require(!coordinator.DebugAutoApproveEnabled, "duplicate loop guard should disable auto-approval until manual review");
            Require(coordinator.DebugCommandRunEnabled, "duplicate loop guard should leave the repeated command staged for manual approval");
            Require(statusText.Text.Contains("Loop guard", StringComparison.OrdinalIgnoreCase), "duplicate loop guard should explain the pause in global status");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("same command", StringComparison.OrdinalIgnoreCase), "duplicate loop guard should explain the duplicate command");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-loop-nochange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Write a tiny app in this workspace." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var shellPicker = new ComboBox();
            var terminal = new ComboBoxItem { Content = "Terminal", Tag = "Terminal" };
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(terminal);
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = terminal;

            var modelClient = new SequentialAgentModelClient(
                """
                Command proposal:
                ```powershell
                Write-Host "no app files yet"
                ```
                """,
                """
                Command proposal:
                ```powershell
                Write-Host "still no app files"
                ```
                """);

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            Require(coordinator.ToggleBuilderOnlyMode(), "Builder-only toggle should report Builder-only mode enabled for loop guard runs");
            coordinator.DebugSetAutoContinueForSession(true, 2);
            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            coordinator.DebugSendAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    asyncFailure = task.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(modelClient.CompleteCalls == 2, "no-change loop guard should allow one follow-up no-op before pausing");
            Require(coordinator.DebugCommandHistoryCount == 2, "no-change loop guard should record both no-change commands");
            Require(!coordinator.DebugAutoContinueEnabled, "no-change loop guard should pause Auto Continue");
            Require(!coordinator.DebugAutoApproveEnabled, "no-change loop guard should disable auto-approval until manual review");
            Require(coordinator.DebugStageNextEnabled, "no-change loop guard should leave Stage Next available for repair");
            Require(coordinator.DebugStageNextLabel == "Stage Repair", "no-change app commands should expose Stage Repair");
            Require(coordinator.DebugStageNextToolTip.Contains("no files", StringComparison.OrdinalIgnoreCase) || coordinator.DebugStageNextToolTip.Contains("changed no files", StringComparison.OrdinalIgnoreCase), "Stage Repair tooltip should explain the no-change repair");
            Require(statusText.Text.Contains("Loop guard", StringComparison.OrdinalIgnoreCase), "no-change loop guard should explain the pause in global status");
            Require(coordinator.DebugBuildEvidenceSummary.Contains("no-change", StringComparison.OrdinalIgnoreCase), "no-change loop guard should explain repeated no-change commands");
            Require(!File.Exists(Path.Combine(root, "index.html")), "no-change loop guard test should not fabricate app files");
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

static void AgentWorkspaceBlocksCommandApprovalDuringChat()
{
    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-command-chat-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(Path.Combine(root, "configs", "settings.json"));
            var promptText = new TextBox { Text = "Explain this workspace before I run the staged command." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var previewButton = new Button();
            var runButton = new Button();
            var rejectButton = new Button();
            var shellPicker = new ComboBox();
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = powershell;

            var blockedPath = Path.Combine(root, "blocked-during-chat.txt");
            var modelClient = new DelayedSequentialAgentModelClient(
                TimeSpan.FromMilliseconds(200),
                "Builder: no command needed for this explanation.");

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                previewButton,
                runButton,
                rejectButton,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            commandText.Text = "Set-Content -Path .\\blocked-during-chat.txt -Value blocked";
            coordinator.DebugPreviewCommand();
            Require(coordinator.DebugCommandRunEnabled, "pre-staged command should start ready for explicit approval");
            Require(coordinator.DebugCommandRejectEnabled, "pre-staged command should be rejectable before chat starts");

            Exception? asyncFailure = null;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var syncContext = new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            SynchronizationContext.SetSynchronizationContext(syncContext);
            var task = coordinator.DebugSendAsync();
            Require(!coordinator.DebugCommandPreviewEnabled, "command preview should disable while Agent chat is running");
            Require(!coordinator.DebugCommandRunEnabled, "command approval should disable while Agent chat is running");
            Require(!coordinator.DebugCommandRejectEnabled, "command rejection should disable while Agent chat is running");

            coordinator.DebugRunApprovedCommandAsync().GetAwaiter().GetResult();
            Require(!File.Exists(blockedPath), "defensive run guard should not execute a staged command while Agent chat is running");
            Require(coordinator.DebugCommandHistoryCount == 0, "blocked mid-chat approval should not create command history");

            task.ContinueWith(completed =>
            {
                if (completed.Exception is not null)
                {
                    asyncFailure = completed.Exception.GetBaseException();
                }

                frame.Continue = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (asyncFailure is not null)
            {
                ExceptionDispatchInfo.Capture(asyncFailure).Throw();
            }

            Require(!File.Exists(blockedPath), "chat completion should not run a manually staged command without approval");
            Require(coordinator.DebugCommandRunEnabled, "staged command should become approvable again after Agent chat finishes");
            Require(coordinator.DebugCommandRejectEnabled, "staged command should become rejectable again after Agent chat finishes");
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

static void AgentWorkspaceRestoresChatAfterRestart()
{
    RunStaTest(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-agent-chat-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fallbackCreatedAt = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var savedMessages = Enumerable.Range(0, AgentWorkspaceConversationStore.MaxPersistedMessages + 2)
                .Select(index => new WpfAgentWorkspaceMessage
                {
                    RoleId = index == 0 ? "" : "builder",
                    Title = index == 0 ? "" : "Builder",
                    Body = $"persisted message {index}",
                    Kind = index == 0 ? "" : "Agent",
                    Model = "gemma",
                    CreatedAt = index == 0 ? default : fallbackCreatedAt.AddMinutes(index)
                })
                .ToList();
            var restoredMessages = AgentWorkspaceConversationStore.RestoreMessages(savedMessages, root, root, fallbackCreatedAt);
            Require(restoredMessages.Count == AgentWorkspaceConversationStore.MaxPersistedMessages, "conversation store should cap restored messages");
            Require(restoredMessages[0].Body == "persisted message 2", "conversation store should keep the most recent messages when capped");
            Require(AgentWorkspaceConversationStore.RestoreMessages(savedMessages, Path.Combine(root, "other"), root, fallbackCreatedAt).Count == 0, "conversation store should not restore chats for a different workspace");
            Require(AgentWorkspaceConversationStore.RestoreActivityDetail(2) == "2 Agent messages restored.", "conversation store should format restored activity labels");
            var persistedMessages = AgentWorkspaceConversationStore.PersistedMessages(restoredMessages);
            Require(persistedMessages.Count == AgentWorkspaceConversationStore.MaxPersistedMessages, "conversation store should cap persisted messages");
            Require(persistedMessages[^1].Body == savedMessages[^1].Body, "conversation store should preserve the newest persisted message");
            var legacyRestoredMessage = AgentWorkspaceConversationStore.RestoreMessages(
                [new WpfAgentWorkspaceMessage { Body = "legacy body", CreatedAt = default }],
                root,
                root,
                fallbackCreatedAt).Single();
            Require(legacyRestoredMessage.RoleId == "system", "conversation store should normalize blank legacy role ids");
            Require(legacyRestoredMessage.Title == "Agent", "conversation store should normalize blank legacy titles");
            Require(legacyRestoredMessage.Kind == "Status", "conversation store should normalize blank legacy kinds");
            Require(legacyRestoredMessage.CreatedAt == fallbackCreatedAt, "conversation store should fill missing legacy timestamps");

            var settingsPath = Path.Combine(root, "configs", "settings.json");
            var settings = new WpfSettings { AgentWorkspacePath = root };
            var settingsStore = new WpfSettingsStore(settingsPath);
            var promptText = new TextBox { Text = "Explain this workspace and remember the answer." };
            var statusText = new TextBlock();
            var commandText = new TextBox();
            var shellPicker = new ComboBox();
            var powershell = new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" };
            shellPicker.Items.Add(powershell);
            shellPicker.SelectedItem = powershell;

            var modelClient = new SequentialAgentModelClient(
                "Builder: restart persistence marker.");

            var snapshot = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 0,
                messages: [],
                agents:
                [
                    new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "", true, false, []),
                    new AgentState("beta", "Beta", "waiting", "", "default", "default", "", "", true, false, [])
                ]);

            var coordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => settings,
                modelClient,
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                promptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                statusText,
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                shellPicker,
                commandText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            coordinator.Initialize();
            coordinator.DebugSendAsync().GetAwaiter().GetResult();

            var saved = settingsStore.Load();
            Require(saved.AgentWorkspaceMessages.Count >= 2, "Agent chat messages should be persisted after a Builder-only Agent turn");
            Require(saved.AgentWorkspaceMessages.Any(message => message.Body.Contains("restart persistence marker", StringComparison.Ordinal)), "persisted Agent chat should include Builder output");
            Require(saved.AgentWorkspaceSessionWorkspacePath.Equals(root, StringComparison.OrdinalIgnoreCase), "persisted Agent chat should be tied to the selected workspace");

            var restoredSettings = settingsStore.Load();
            var restoredPromptText = new TextBox();
            var restoredShellPicker = new ComboBox();
            restoredShellPicker.Items.Add(new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" });
            restoredShellPicker.SelectedIndex = 0;
            var restoredCoordinator = new AgentWorkspaceCoordinator(
                new Window(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                settingsStore,
                () => restoredSettings,
                new SequentialAgentModelClient(),
                new TextBox(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new ScrollViewer(),
                new StackPanel(),
                restoredPromptText,
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new TextBlock(),
                new StackPanel(),
                new TextBlock(),
                new StackPanel(),
                new StackPanel(),
                restoredShellPicker,
                new TextBox(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new TextBlock(),
                new TextBlock(),
                new WrapPanel(),
                new TextBox(),
                new TextBlock(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new Button(),
                new Button(),
                new TextBlock(),
                new StackPanel(),
                new Button(),
                new Button(),
                () => snapshot,
                AccentResourceBrush,
                _ => { });

            restoredCoordinator.Initialize();
            Require(restoredCoordinator.DebugLastMessageBody.Contains("restart persistence marker", StringComparison.Ordinal), "restarted Agent coordinator should restore the latest Builder chat");
            Require(restoredCoordinator.DebugLastMessageKind == "Agent", "restored Agent chat should preserve message kind");
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

static void AgentWorkspaceProfileRefreshIgnoresStaleResults()
{
    RunStaTest(() =>
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-profile-refresh", Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(testRoot, "first");
        var secondRoot = Path.Combine(testRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var firstCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<(string Path, CancellationToken Token)>();
            Task<string> BuildProfileAsync(string path, CancellationToken cancellationToken)
            {
                calls.Add((path, cancellationToken));
                return path.Equals(firstRoot, StringComparison.OrdinalIgnoreCase)
                    ? firstCompletion.Task
                    : secondCompletion.Task;
            }

            var settings = new WpfSettings();
            var coordinator = CreateWorkspaceProfileTestCoordinator(
                settings,
                new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                BuildProfileAsync);
            coordinator.Initialize();

            coordinator.ControlSetWorkspace(firstRoot);
            var firstRefresh = coordinator.DebugWorkspaceProfileRefreshTask;
            Require(calls.Count == 1, "setting a workspace should start one asynchronous profile refresh");
            Require(!firstRefresh.IsCompleted, "a slow workspace profile refresh must not block the UI-facing workspace setter");
            Require(coordinator.DebugWorkspaceProfile.Contains("loading", StringComparison.OrdinalIgnoreCase), "a pending refresh should expose a non-stale loading profile");

            coordinator.ControlSetWorkspace(secondRoot);
            var secondRefresh = coordinator.DebugWorkspaceProfileRefreshTask;
            Require(calls.Count == 2, "changing workspaces should start a replacement profile refresh");
            Require(calls[0].Token.IsCancellationRequested, "changing workspaces should cancel the prior profile refresh");

            secondCompletion.SetResult("profile from current workspace");
            secondRefresh.GetAwaiter().GetResult();
            Require(coordinator.DebugWorkspaceProfile == "profile from current workspace", "the current workspace profile should be applied when its refresh completes");

            firstCompletion.SetResult("stale profile from prior workspace");
            firstRefresh.GetAwaiter().GetResult();
            Require(coordinator.DebugWorkspaceProfile == "profile from current workspace", "a stale prior-workspace result must not overwrite the current profile");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    });
}

static void AgentWorkspaceProfileRefreshDisposesSafely()
{
    RunStaTest(() =>
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-profile-dispose", Guid.NewGuid().ToString("N"));
        var workspaceRoot = Path.Combine(testRoot, "workspace");
        var ignoredRoot = Path.Combine(testRoot, "ignored");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(ignoredRoot);
        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var started = new ManualResetEventSlim();
            using var cancellationObserved = new ManualResetEventSlim();
            Exception? lifetimeFailure = null;
            var calls = 0;

            async Task<string> BuildProfileAsync(string _, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref calls);
                using var registration = cancellationToken.Register(cancellationObserved.Set);
                started.Set();
                await release.Task.ConfigureAwait(false);
                try
                {
                    using var lateRegistration = cancellationToken.Register(static () => { });
                }
                catch (Exception ex)
                {
                    lifetimeFailure = ex;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return "profile completed after disposal";
            }

            var settings = new WpfSettings();
            var coordinator = CreateWorkspaceProfileTestCoordinator(
                settings,
                new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                BuildProfileAsync);
            coordinator.Initialize();
            coordinator.ControlSetWorkspace(workspaceRoot);
            var refresh = coordinator.DebugWorkspaceProfileRefreshTask;
            Require(started.Wait(TimeSpan.FromSeconds(2)), "the injected workspace profile refresh should start");

            coordinator.Dispose();
            Require(cancellationObserved.Wait(TimeSpan.FromSeconds(2)), "disposing the coordinator should cancel its active profile refresh");
            coordinator.ControlSetWorkspace(ignoredRoot);
            Require(Volatile.Read(ref calls) == 1, "a disposed coordinator should not start another profile refresh");

            release.SetResult();
            refresh.GetAwaiter().GetResult();
            Require(lifetimeFailure is null, $"the profile cancellation source must stay alive until the delegate unwinds: {lifetimeFailure?.Message}");
            Require(coordinator.DebugWorkspaceProfile != "profile completed after disposal", "a profile result completed after disposal must be ignored");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    });
}

private static AgentWorkspaceCoordinator CreateWorkspaceProfileTestCoordinator(
    WpfSettings settings,
    WpfSettingsStore settingsStore,
    Func<string, CancellationToken, Task<string>> buildWorkspaceProfileAsync)
{
    var shellPicker = new ComboBox();
    shellPicker.Items.Add(new ComboBoxItem { Content = "PowerShell", Tag = "PowerShell" });
    shellPicker.SelectedIndex = 0;
    return new AgentWorkspaceCoordinator(
        new Window(),
        System.Windows.Threading.Dispatcher.CurrentDispatcher,
        settingsStore,
        () => settings,
        null,
        new TextBox(),
        new Button(),
        new Button(),
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new StackPanel(),
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new ScrollViewer(),
        new StackPanel(),
        new TextBox(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new TextBlock(),
        new TextBlock(),
        new TextBlock(),
        new StackPanel(),
        new TextBlock(),
        new StackPanel(),
        new StackPanel(),
        shellPicker,
        new TextBox(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new Button(),
        new TextBlock(),
        new Button(),
        new TextBlock(),
        new TextBlock(),
        new WrapPanel(),
        new TextBox(),
        new TextBlock(),
        new TextBlock(),
        new Button(),
        new Button(),
        new TextBlock(),
        new Button(),
        new Button(),
        new TextBlock(),
        new StackPanel(),
        new Button(),
        new Button(),
        () => null,
        AccentResourceBrush,
        _ => { },
        buildWorkspaceProfileAsync: buildWorkspaceProfileAsync);
}

}
