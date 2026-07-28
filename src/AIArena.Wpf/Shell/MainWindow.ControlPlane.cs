using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Control plane surface for the shell: command dispatch, snapshot projection,
/// and the response helpers the PowerShell plane depends on. Split from the
/// main partial so the window file stays about the window.
/// </summary>
public partial class MainWindow
{
    bool IAIArenaControlTarget.IsControlPlaneEnabled => IsControlPlaneEnabled;

    private bool IsControlPlaneEnabled => _wpfSettings.EnableControlPlane;

    async Task<AIArenaControlResponse> IAIArenaControlTarget.ExecuteControlCommandAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        return await AIArenaControlDispatcher.InvokeAsync(
            Dispatcher,
            () => ExecuteControlCommandWithStateOnUiThreadAsync(request, cancellationToken),
            cancellationToken);
    }

    private async Task<AIArenaControlResponse> ExecuteControlCommandWithStateOnUiThreadAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteControlCommandOnUiThreadAsync(request, cancellationToken);
        return response with { State = BuildControlPlaneStateSummary() };
    }

    private async Task<AIArenaControlResponse> ExecuteControlCommandOnUiThreadAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsControlPlaneEnabled)
        {
            return AIArenaControlResponse.Error(
                request,
                "control_plane_disabled",
                "AI Arena control plane is disabled. Enable it in Settings > PowerShell Control first.");
        }

        if (!AIArenaControlCommands.IsKnown(request.Command))
        {
            return AIArenaControlResponse.Error(request, "unknown_command", $"Unknown control command '{request.Command}'.");
        }

        if (_settingsControlHandler.CanHandle(request.Command))
        {
            return _settingsControlHandler.Execute(request);
        }

        if (_appControlHandler.CanHandle(request.Command))
        {
            return await RunTrackedControlOperationAsync(
                operationCancellationToken => _appControlHandler.ExecuteAsync(request, operationCancellationToken),
                cancellationToken);
        }

        if (_providerControlHandler.CanHandle(request.Command))
        {
            return await RunProviderControlOperationAsync(request, cancellationToken);
        }

        if (_matchSetupControlHandler.CanHandle(request.Command))
        {
            return await _matchSetupControlHandler.ExecuteAsync(request, cancellationToken);
        }

        if (_sessionForkControlHandler.CanHandle(request.Command))
        {
            return await _sessionForkControlHandler.ExecuteAsync(request, cancellationToken);
        }

        if (_collaborateControlHandler.CanHandle(request.Command))
        {
            return await _collaborateControlHandler.ExecuteAsync(request);
        }

        switch (request.Command)
        {
            case AIArenaControlCommands.Capabilities:
                return AIArenaControlResponse.Success(
                    request,
                    "Control-plane capabilities captured.",
                    new { SchemaVersion = 1, Commands = AIArenaControlCapabilityCatalog.All });
            case AIArenaControlCommands.Status:
            case AIArenaControlCommands.Snapshot:
                return AIArenaControlResponse.Success(request, "Snapshot captured.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.NavigationSelect:
                {
                    var view = RequiredStringArg(request, "view");
                    if (string.IsNullOrWhiteSpace(view))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "navigation.select requires args.view.");
                    }

                    var normalizedView = view.Trim().ToLowerInvariant();
                    if (normalizedView == "agent" && !IsAgentWorkspaceEnabled(_wpfSettings))
                    {
                        return AIArenaControlResponse.Error(
                            request,
                            "feature_disabled",
                            "Agent workspace is hidden. Enable it in Settings -> Agent workspace or with settings.update.");
                    }

                    if (normalizedView is "world" or "ai.world" && !IsWorldDebugEnabled(_wpfSettings))
                    {
                        return AIArenaControlResponse.Error(
                            request,
                            "feature_disabled",
                            "AI World is disabled. Enable Debug controls and AI World (3D) before selecting it.");
                    }

                    if (!SelectControlPlaneView(view))
                    {
                        return AIArenaControlResponse.Error(request, "invalid_argument", $"Unknown navigation view '{view}'.");
                    }

                    // ApplyShellCommandState announces this for both routes.
                    return AIArenaControlResponse.Success(request, "AI Arena view changed.", BuildControlPlaneSnapshot());
                }
            case AIArenaControlCommands.NavigationThemeSet:
                {
                    var theme = RequiredStringArg(request, "theme");
                    if (string.IsNullOrWhiteSpace(theme))
                    {
                        theme = RequiredStringArg(request, "themeId");
                    }

                    if (string.IsNullOrWhiteSpace(theme))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "navigation.theme.set requires args.theme or args.themeId.");
                    }

                    var themeId = ThemePalette.NormalizeId(theme);
                    // ApplyTheme announces this for both routes.
                    ShellNavigation.ApplyTheme(themeId, persist: true, rerender: true);
                    return AIArenaControlResponse.Success(request, "AI Arena theme changed.", BuildControlPlaneSnapshot());
                }
            case AIArenaControlCommands.NavigationProviderFocus:
                OpenModelProviderSettings(
                    OptionalStringArg(request, "baseUrl"),
                    OptionalStringArg(request, "model"));
                _controlPlaneEvents.Publish("navigation.provider.focused", "Provider settings focused.");
                return AIArenaControlResponse.Success(request, "Provider settings focused.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.NavigationRailSet:
                {
                    var state = RequiredStringArg(request, "state");
                    if (!ControlSetRightRail(state))
                    {
                        return AIArenaControlResponse.Error(request, "invalid_argument", "navigation.rail.set requires args.state: show, hide, or toggle.");
                    }

                    // ApplyRightRailCollapsed announces this for both routes.
                    return AIArenaControlResponse.Success(request, "Right rail visibility changed.", BuildRightRailControlState());
                }
            case AIArenaControlCommands.ViewPresetSet:
                {
                    var preset = AIArenaControlPlaneProtocol.NormalizeCommand(RequiredStringArg(request, "preset"));
                    switch (preset)
                    {
                        case "focused":
                            _transcriptViewCoordinator?.ApplyFocusedPreset();
                            break;
                        case "diagnostics":
                            _transcriptViewCoordinator?.ApplyDiagnosticsPreset();
                            break;
                        case "compact":
                            _transcriptViewCoordinator?.ApplyCompactPreset();
                            break;
                        case "review":
                            _transcriptViewCoordinator?.ApplyReviewPreset();
                            break;
                        default:
                            return AIArenaControlResponse.Error(request, "invalid_argument", "view.preset.set requires args.preset: focused, diagnostics, compact, or review.");
                    }

                    // The preset methods announce this for both routes.
                    return AIArenaControlResponse.Success(request, "Transcript view preset changed.", new { preset });
                }
            case AIArenaControlCommands.ShellPaletteList:
                return AIArenaControlResponse.Success(request, "Command palette captured.", ControlListPaletteCommands());
            case AIArenaControlCommands.ShellPaletteRun:
                {
                    var id = RequiredStringArg(request, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "shell.palette.run requires args.id.");
                    }

                    // No publish here either: a palette entry runs the same
                    // handler a button runs, and those announce themselves.
                    var run = ControlRunPaletteCommand(id);
                    return run.Ok
                        ? AIArenaControlResponse.Success(request, run.Message, ControlListPaletteCommands())
                        : AIArenaControlResponse.Error(request, "not_available", run.Message, ControlListPaletteCommands());
                }
            case AIArenaControlCommands.ShellInputKey:
                {
                    var sent = ControlSendKey(RequiredStringArg(request, "key"), OptionalStringArg(request, "modifiers"));
                    return sent.Ok
                        ? AIArenaControlResponse.Success(request, sent.Message, sent.State)
                        : AIArenaControlResponse.Error(request, sent.ErrorCode, sent.Message, sent.State);
                }
            case AIArenaControlCommands.ShellInputType:
                {
                    // Absent and empty have to be told apart here. Empty is a
                    // legitimate value - it clears the field - so defaulting a
                    // missing argument to empty silently wiped the target for
                    // any caller who mistyped the argument name.
                    if (!AIArenaControlArguments.TryGetString(request, "text", out var textArg))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "shell.input.type requires args.text.");
                    }

                    var typed = ControlTypeText(OptionalStringArg(request, "target"), textArg);
                    return typed.Ok
                        ? AIArenaControlResponse.Success(request, typed.Message, typed.State)
                        : AIArenaControlResponse.Error(request, typed.ErrorCode, typed.Message, typed.State);
                }
            case AIArenaControlCommands.MatchGenerationState:
                return AIArenaControlResponse.Success(
                    request,
                    "Match generation state captured.",
                    await _scenarioGenerationControlService.CaptureAsync(cancellationToken));
            case AIArenaControlCommands.MatchGenerateRandom:
                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.GenerateAsync(
                        "random",
                        GenerationOptionsFromRequest(request),
                        cancellationToken));
            case AIArenaControlCommands.MatchGenerateAi:
                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.GenerateAsync(
                        "ai",
                        GenerationOptionsFromRequest(request),
                        cancellationToken));
            case AIArenaControlCommands.MatchGenerateCurrent:
                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.GenerateAsync(
                        "current",
                        GenerationOptionsFromRequest(request),
                        cancellationToken));
            case AIArenaControlCommands.MatchGenerateWild:
                if (!OptionalBoolArg(request, "confirm"))
                {
                    return AIArenaControlResponse.Error(
                        request,
                        "confirmation_required",
                        "match.generate.wild requires args.confirm=true because it makes a broad setup change.");
                }

                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.GenerateAsync(
                        "wild",
                        GenerationOptionsFromRequest(request),
                        cancellationToken));
            case AIArenaControlCommands.MatchReplay:
                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.ReplayAsync(
                        RequiredStringArg(request, "id"),
                        newSession: false,
                        cancellationToken));
            case AIArenaControlCommands.MatchReplayNew:
                return MatchGenerationControlResponse(
                    request,
                    await _scenarioGenerationControlService.ReplayAsync(
                        RequiredStringArg(request, "id"),
                        newSession: true,
                        cancellationToken));
            case AIArenaControlCommands.SessionState:
                return AIArenaControlResponse.Success(
                    request,
                    "Saved-state inventory captured.",
                    await _savedStateControlService.CaptureAsync(cancellationToken));
            case AIArenaControlCommands.SessionSelect:
                return SavedStateControlResponse(
                    request,
                    await _savedStateControlService.SelectSessionAsync(RequiredStringArg(request, "id"), cancellationToken));
            case AIArenaControlCommands.SessionCreate:
                return SavedStateControlResponse(
                    request,
                    await _savedStateControlService.CreateSessionAsync(RequiredStringArg(request, "name"), cancellationToken));
            case AIArenaControlCommands.SessionCheckpointCreate:
                return SavedStateControlResponse(
                    request,
                    await _savedStateControlService.SaveCheckpointAsync(OptionalStringArg(request, "name") ?? "", cancellationToken));
            case AIArenaControlCommands.SessionCheckpointRestore:
                if (!OptionalBoolArg(request, "confirm"))
                {
                    return AIArenaControlResponse.Error(
                        request,
                        "confirmation_required",
                        "session.checkpoint.restore requires args.confirm=true because it replaces the active session state.");
                }

                return SavedStateControlResponse(
                    request,
                    await _savedStateControlService.RestoreCheckpointAsync(RequiredStringArg(request, "id"), cancellationToken));
            case AIArenaControlCommands.AgentState:
                return AIArenaControlResponse.Success(request, "Agent state captured.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentCommandState:
                return AIArenaControlResponse.Success(request, "Agent command state captured.", BuildAgentCommandControlState());
            case AIArenaControlCommands.AgentWorkBrief:
                return AIArenaControlResponse.Success(request, "Agent work brief captured.", BuildAgentWorkControlState());
            case AIArenaControlCommands.AgentBuildEvidence:
                return AIArenaControlResponse.Success(request, "Agent build evidence captured.", BuildAgentWorkControlState());
            case AIArenaControlCommands.AgentOutputs:
                return AIArenaControlResponse.Success(request, "Agent outputs captured.", BuildAgentOutputControlState());
            case AIArenaControlCommands.AgentRunbookState:
                return AIArenaControlResponse.Success(request, "Agent runbook captured.", AgentWorkspace.ControlRunbookState);
            case AIArenaControlCommands.AgentRunbookResume:
                if (!AgentWorkspace.ControlResumeRunbook())
                {
                    return AIArenaControlResponse.Error(request, "not_available", "No Agent runbook is available to resume.");
                }

                _controlPlaneEvents.Publish("agent.runbook.resumed", "Agent runbook resume requested.", AgentWorkspace.ControlRunbookState);
                return AIArenaControlResponse.Success(request, "Agent runbook resume requested.", AgentWorkspace.ControlRunbookState);
            case AIArenaControlCommands.AgentRunbookCheckpoint:
                {
                    var summary = RequiredStringArg(request, "summary");
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "agent.runbook.checkpoint requires args.summary.");
                    }

                    if (!AgentWorkspace.ControlAddRunbookCheckpoint(OptionalStringArg(request, "kind") ?? "operator", summary))
                    {
                        return AIArenaControlResponse.Error(request, "not_available", "No Agent runbook is available to checkpoint.");
                    }

                    _controlPlaneEvents.Publish("agent.runbook.checkpointed", "Agent runbook checkpoint added.", AgentWorkspace.ControlRunbookState);
                    return AIArenaControlResponse.Success(request, "Agent runbook checkpoint added.", AgentWorkspace.ControlRunbookState);
                }
            case AIArenaControlCommands.AgentWorkspaceSet:
                {
                    var path = RequiredStringArg(request, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "agent.workspace.set requires args.path.");
                    }

                    AgentWorkspace.ControlSetWorkspace(path);
                    _controlPlaneEvents.Publish("agent.workspace.changed", "Agent workspace changed.", new { path = AgentWorkspace.DebugWorkspacePath });
                    return AIArenaControlResponse.Success(request, "Agent workspace updated.", AgentWorkspace.ControlState);
                }
            case AIArenaControlCommands.AgentSend:
                {
                    var prompt = RequiredStringArg(request, "prompt");
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "agent.send requires args.prompt.");
                    }

                    await AgentWorkspace.ControlSendAsync(prompt);
                    _controlPlaneEvents.Publish("agent.prompt.sent", "Agent prompt sent.", new { promptLength = prompt.Length });
                    return AIArenaControlResponse.Success(request, "Agent prompt sent.", AgentWorkspace.ControlState);
                }
            case AIArenaControlCommands.AgentApprove:
                await AgentWorkspace.ControlApproveAsync();
                _controlPlaneEvents.Publish("agent.command.approved", "Agent command approved.");
                return AIArenaControlResponse.Success(request, "Agent command approval requested.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentReject:
                AgentWorkspace.ControlReject();
                _controlPlaneEvents.Publish("agent.command.rejected", "Agent command rejected.");
                return AIArenaControlResponse.Success(request, "Agent command reject requested.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentStop:
                AgentWorkspace.ControlStop();
                _controlPlaneEvents.Publish("agent.stop.requested", "Agent stop requested.");
                return AIArenaControlResponse.Success(request, "Agent stop requested.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentStageNext:
                AgentWorkspace.ControlStageNext();
                _controlPlaneEvents.Publish("agent.prompt.staged", "Agent next-step prompt staged.", new { stage = "next" });
                return AIArenaControlResponse.Success(request, "Agent next-step prompt staged.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentStageVerify:
                AgentWorkspace.ControlStageVerify();
                _controlPlaneEvents.Publish("agent.prompt.staged", "Agent verify prompt staged.", new { stage = "verify" });
                return AIArenaControlResponse.Success(request, "Agent verify prompt staged.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentStageArtifact:
                AgentWorkspace.ControlStageArtifact();
                _controlPlaneEvents.Publish("agent.prompt.staged", "Agent artifact prompt staged.", new { stage = "artifact" });
                return AIArenaControlResponse.Success(request, "Agent artifact prompt staged.", AgentWorkspace.ControlState);
            case AIArenaControlCommands.AgentCommandStage:
                {
                    var command = RequiredStringArg(request, "command");
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "agent.command.stage requires args.command.");
                    }

                    AgentWorkspace.ControlStageCommand(command, OptionalStringArg(request, "shell") ?? "PowerShell");
                    _controlPlaneEvents.Publish("agent.command.staged", "Agent command staged from control plane.", new { commandLength = command.Length });
                    return AIArenaControlResponse.Success(request, "Agent command staged.", AgentWorkspace.ControlState);
                }
            case AIArenaControlCommands.ArenaStart:
                _ = ArenaRun.StartAutoChatAsync();
                return AIArenaControlResponse.Success(request, "Arena auto-chat start requested.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.ArenaStop:
                ArenaRun.StopAutoChat();
                return AIArenaControlResponse.Success(request, "Arena auto-chat stop requested.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.ArenaTurn:
                await ArenaRun.RunOneTurnAsync();
                return AIArenaControlResponse.Success(request, "Arena one-turn request completed.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.ArenaNarrate:
                await ArenaRun.NarrateNowAsync();
                return AIArenaControlResponse.Success(request, "Arena narration request completed.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.ArenaReset:
                if (!OptionalBoolArg(request, "confirm"))
                {
                    return AIArenaControlResponse.Error(request, "confirmation_required", "arena.reset requires args.confirm=true because it clears the current transcript and live state.");
                }

                await ArenaSessionMutations.ControlResetArenaAsync();
                _controlPlaneEvents.Publish("arena.reset.completed", "Arena reset request completed.");
                return AIArenaControlResponse.Success(request, "Arena reset request completed.", BuildControlPlaneSnapshot());
            case AIArenaControlCommands.ArenaOperatorSend:
                {
                    var prompt = RequiredStringArg(request, "prompt");
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        return AIArenaControlResponse.Error(request, "missing_argument", "arena.operator.send requires args.prompt.");
                    }

                    var requestedRoute = OptionalStringArg(request, "route") ?? "public";
                    if (!OperatorTurnCoordinator.TryNormalizeOperatorRoute(requestedRoute, out var route))
                    {
                        return AIArenaControlResponse.Error(
                            request,
                            "invalid_argument",
                            "arena.operator.send args.route must be public, private, or narrator.");
                    }

                    await OperatorTurn.ControlSendAsync(prompt, route);
                    _controlPlaneEvents.Publish("arena.operator.sent", "Operator message sent.", new { promptLength = prompt.Length });
                    return AIArenaControlResponse.Success(request, "Operator message sent.", BuildControlPlaneSnapshot());
                }
            case AIArenaControlCommands.InternetState:
                return AIArenaControlResponse.Success(request, "Internet state captured.", InternetWorkflow.ControlState);
            case AIArenaControlCommands.InternetSet:
                {
                    var enabledText = RequiredStringArg(request, "enabled");
                    if (!bool.TryParse(enabledText, out var enabled))
                    {
                        return AIArenaControlResponse.Error(request, "invalid_argument", "internet.set requires args.enabled: true or false.");
                    }

                    await InternetWorkflow.ControlSetEnabledAsync(enabled);
                    return AIArenaControlResponse.Success(request, "Internet setting changed.", InternetWorkflow.ControlState);
                }
            case AIArenaControlCommands.InternetTest:
                await InternetWorkflow.TestInternetAsync();
                return AIArenaControlResponse.Success(request, "Internet diagnostic completed.", InternetWorkflow.ControlState);
            case AIArenaControlCommands.ExportTranscript:
                return AIArenaControlResponse.Success(request, "Transcript export captured.", BuildTranscriptControlExport());
            case AIArenaControlCommands.ExportSession:
                return AIArenaControlResponse.Success(request, "Session export captured.", BuildSessionControlExport());
            case AIArenaControlCommands.ExportReceipts:
                return AIArenaControlResponse.Success(request, "Receipts export captured.", BuildReceiptControlExport());
            default:
                return AIArenaControlResponse.Error(
                    request,
                    "not_implemented",
                    $"Command '{request.Command}' is reserved in the control-plane schema but is not wired yet.");
        }
    }

    private void ShowScreenshotReceipt(AIArenaScreenshotControlResult result)
    {
        var fileName = Path.GetFileName(result.Path);
        SaveStatusText.Text = $"Screenshot saved: {fileName}";
        SaveStatusText.Visibility = Visibility.Visible;
        SaveStatusText.ToolTip = result.Path;
        AutomationProperties.SetName(SaveStatusText, "Screenshot capture receipt");
        AutomationProperties.SetHelpText(
            SaveStatusText,
            $"AI Arena saved a screenshot to {result.Path} at {result.CapturedAt:HH:mm:ss}.");
        AutomationProperties.SetLiveSetting(SaveStatusText, AutomationLiveSetting.Polite);
        ShellTopBar.Presentation.SetTransientStatusVisible(true);
        _ = HideScreenshotReceiptAsync(SaveStatusText.Text);
    }

    private async Task HideScreenshotReceiptAsync(string receiptText)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        await Dispatcher.InvokeAsync(() =>
        {
            if (!SaveStatusText.Text.Equals(receiptText, StringComparison.Ordinal))
            {
                return;
            }

            SaveStatusText.Visibility = Visibility.Collapsed;
            ShellTopBar.Presentation.SetTransientStatusVisible(false);
        });
    }

    private AIArenaControlSnapshot BuildControlPlaneSnapshot()
    {
        return new AIArenaControlSnapshot(
            ArenaRunStatus.Text,
            SelectedControlPlaneView(),
            _wpfSettings.ThemeId,
            IsControlPlaneEnabled,
            AgentWorkspace.ControlState,
            BuildProviderControlState());
    }

    private AIArenaControlResponse SavedStateControlResponse(
        AIArenaControlRequest request,
        AIArenaSavedStateControlResult result)
    {
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.State);
        }

        _controlPlaneEvents.Publish("session.saved-state.changed", result.Message, result.State);
        return AIArenaControlResponse.Success(request, result.Message, result.State);
    }

    private AIArenaControlResponse MatchGenerationControlResponse(
        AIArenaControlRequest request,
        AIArenaMatchGenerationControlResult result)
    {
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.Data);
        }

        _controlPlaneEvents.Publish("match.generation.changed", result.Message, result.Data);
        return AIArenaControlResponse.Success(request, result.Message, result.Data);
    }

    private static AIArenaMatchGenerationOptions GenerationOptionsFromRequest(AIArenaControlRequest request)
    {
        return new AIArenaMatchGenerationOptions(
            OptionalStringArg(request, "style") ?? "",
            OptionalStringArg(request, "intensity") ?? "",
            OptionalStringArg(request, "rolePack") ?? "",
            OptionalStringArg(request, "absurdity") ?? "",
            OptionalStringArg(request, "seed") ?? "",
            OptionalStringArg(request, "prompt") ?? "",
            OptionalStringArg(request, "query") ?? "");
    }
}
