using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf;

public partial class MainWindow
{
    private ProviderModelsSurfaceCoordinator? _providerModelsSurfaceCoordinator;
    private IInputElement? _providerModelsFocusReturnTarget;
    private ShellSurface _providerModelsReturnSurface = ShellSurface.Lab;
    private ProviderModelsHeartbeatController? _providerModelsHeartbeat;

    private ProviderModelsSurfaceCoordinator ProviderModelsSurface =>
        _providerModelsSurfaceCoordinator
        ?? throw new InvalidOperationException("Provider Models surface coordinator is not initialized.");

    private void ModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderModelsPanel.Visibility == Visibility.Visible)
        {
            CloseProviderModelsPanel();
            return;
        }

        ShowProviderModelsPanel();
    }

    private void OpenModelsSurfaceButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsReturnTarget = AppSettingsPanel.Visibility == Visibility.Visible
            ? _settingsFocusReturnTarget
            : null;
        AppSettingsWorkflow.SetVisible(false);
        ShowProviderModelsPanel();
        if (settingsReturnTarget is not null)
        {
            _providerModelsFocusReturnTarget = settingsReturnTarget;
        }
    }

    private void ShowProviderModelsPanel()
    {
        var opening = ProviderModelsPanel.Visibility != Visibility.Visible;
        if (opening)
        {
            _providerModelsReturnSurface = _activeShellSurface switch
            {
                ShellSurface.MatchSetup => _matchSetupReturnSurface,
                ShellSurface.Models => ShellSurface.Lab,
                _ => _activeShellSurface
            };
            _providerModelsFocusReturnTarget = Keyboard.FocusedElement ?? ModelsButton;
        }

        // Models and Match Setup are sibling shell overlays. Switching between
        // them preserves the underlying workspace instead of creating an overlay
        // return loop.
        _matchSetupFocusReturnTarget = null;
        _matchSetupReturnSurface = ShellSurface.Lab;
        CloseNamedTransientShellFlyouts();
        ShellNavigation.ShowProviderModelsPanel();
        _activeShellSurface = ShellSurface.Models;
        ApplyShellCommandState(_activeShellSurface);
        UpdateLabViewToggleVisibility();
        UpdateLabViewToggle();

        if (opening)
        {
            _ = RunTrackedBackgroundOperationSafelyAsync(
                "Models refresh",
                cancellationToken => ProviderModelsSurface.RefreshAsync(
                    refreshCatalog: true,
                    cancellationToken));
            Dispatcher.BeginInvoke(() =>
            {
                if (ProviderModelsPanel.Visibility == Visibility.Visible)
                {
                    ProviderModelsPanel.FocusCatalog();
                }
            }, DispatcherPriority.Input);
        }
    }

    private void ProviderModelsPanel_CloseRequested(object? sender, EventArgs e) =>
        CloseProviderModelsPanel();

    private async void ProviderModelsPanel_RefreshRequested(object? sender, EventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "Models refresh",
            cancellationToken => ProviderModelsSurface.RefreshAsync(
                refreshCatalog: true,
                cancellationToken));
    }

    private void ProviderModelsPanel_ConnectionSettingsRequested(object? sender, EventArgs e) =>
        OpenModelProviderSettings();

    private async void ProviderModelsPanel_AssignmentChanged(
        object? sender,
        ProviderModelAssignmentChangedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "Model assignment",
            cancellationToken => ProviderModelsSurface.SaveAssignmentAsync(e, cancellationToken));
    }

    private async void ProviderModelsPanel_LifecycleRequested(
        object? sender,
        ProviderModelLifecycleRequestedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            e.Load ? "LM Studio model load" : "LM Studio model unload",
            cancellationToken => ProviderModelsSurface.RunLifecycleAsync(e, cancellationToken));
    }

    private async void ProviderModelsPanel_ConfigurationChanged(
        object? sender,
        ProviderModelConfigurationChangedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "Model configuration",
            cancellationToken => ProviderModelsSurface.SaveConfigurationAsync(e, cancellationToken));
    }

    private async void ProviderModelsPanel_ConfigurationReloadRequested(
        object? sender,
        ProviderModelConfigurationReloadRequestedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "LM Studio context reload",
            cancellationToken => ProviderModelsSurface.RunConfigurationReloadAsync(e, cancellationToken));
    }

    private void ProviderModelsPanel_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && ProviderModelsPanel.IsVisible)
        {
            StartProviderModelsHeartbeat();
        }
        else
        {
            StopProviderModelsHeartbeat();
        }
    }

    private void StartProviderModelsHeartbeat()
    {
        if (!ProviderModelsPanel.IsVisible)
        {
            return;
        }

        _providerModelsHeartbeat ??= new ProviderModelsHeartbeatController(
            Dispatcher,
            ProviderModelsSurfaceCoordinator.HeartbeatInterval,
            RunProviderModelsHeartbeatAsync);
        _providerModelsHeartbeat.SetEffectivelyVisible(true);
    }

    private void StopProviderModelsHeartbeat() =>
        _providerModelsHeartbeat?.SetEffectivelyVisible(false);

    private Task RunProviderModelsHeartbeatAsync(CancellationToken visibilityToken)
    {
        return RunTrackedBackgroundOperationSafelyAsync(
            "LM Studio model heartbeat",
            async cancellationToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    visibilityToken);
                await ProviderModelsSurface.HeartbeatAsync(linked.Token);
            });
    }

    private void DisposeProviderModelsHeartbeat()
    {
        _providerModelsHeartbeat?.Dispose();
        _providerModelsHeartbeat = null;
    }

    private async Task OpenModelsRecoveryAsync(string modelId, bool focusConfiguration)
    {
        ShowProviderModelsPanel();
        await RunTrackedBackgroundOperationSafelyAsync(
            "Context recovery model refresh",
            cancellationToken => ProviderModelsSurface.RefreshAsync(
                refreshCatalog: true,
                cancellationToken));
        if (ProviderModelsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (focusConfiguration)
        {
            if (!ProviderModelsPanel.SelectModel(modelId, focusConfiguration: true))
            {
                ProviderModelsPanel.FocusSearch();
            }
            return;
        }

        ProviderModelsPanel.FocusSearch();
    }

    private async Task EndMatchAfterContextLimitAsync()
    {
        await ArenaRun.StopAutoChatAsync();
        var session = _activeSession;
        if (session is null)
        {
            SetArenaRunStatus("No active session is available to end.");
            return;
        }

        var ran = await RunArenaBusyForCoordinatorAsync(
            "Ending the match after its context-limit stop…",
            operationButton: null,
            async cancellationToken =>
            {
                var result = await _contextRecoveryService.EndMatchAsync(
                    session.Id,
                    "operator_context_limit_end",
                    cancellationToken);
                if (!result.Ok)
                {
                    throw new InvalidOperationException(result.Error);
                }

                await RefreshActiveSessionAsync(
                    "Match ended by the operator after a context-limit stop.",
                    cancellationToken);
            },
            allowDuringAutoChat: false);
        if (!ran)
        {
            SetArenaRunStatus("The match could not end while another arena operation was active.");
        }
    }

    private Task OpenOutputSettingsRecoveryAsync()
    {
        OpenModelProviderSettings();
        Dispatcher.BeginInvoke(() =>
        {
            ProviderMaxOutputText.BringIntoView();
            ProviderMaxOutputText.Focus();
            ProviderMaxOutputText.SelectAll();
        }, DispatcherPriority.Input);
        return Task.CompletedTask;
    }

    private async Task SkipContextBlockedTurnAsync(Models.TranscriptMessage message)
    {
        var session = _activeSession;
        if (session is null)
        {
            SetArenaRunStatus("No active session is available for context recovery.");
            return;
        }

        var ran = await RunArenaBusyForCoordinatorAsync(
            "Skipping the context-blocked turn…",
            operationButton: null,
            async cancellationToken =>
            {
                var result = await _contextRecoveryService.SkipBlockedTurnAsync(
                    session.Id,
                    message.Turn,
                    message.SpeakerId,
                    message.CreatedAt,
                    cancellationToken);
                if (!result.Ok)
                {
                    throw new InvalidOperationException(result.Error);
                }

                await RefreshActiveSessionAsync("Skipped the blocked turn. Auto Chat remains stopped.", cancellationToken);
            },
            allowDuringAutoChat: false);
        if (!ran)
        {
            SetArenaRunStatus("Context recovery is unavailable while another arena operation is active.");
        }
    }

    private async Task ContinueTruncatedOutputAsync(Models.TranscriptMessage message)
    {
        var session = _activeSession;
        if (session is null)
        {
            SetArenaRunStatus("No active session is available for output continuation.");
            return;
        }

        var ran = await RunArenaBusyForCoordinatorAsync(
            "Continuing the output-limited response…",
            operationButton: null,
            async cancellationToken =>
            {
                var result = await _contextRecoveryService.ContinueOutputAsync(
                    session.Id,
                    message.Turn,
                    message.SpeakerId,
                    message.CreatedAt,
                    cancellationToken);
                if (!result.Ok)
                {
                    throw new InvalidOperationException(result.Error);
                }

                await RefreshActiveSessionAsync("Continued the output-limited response.", cancellationToken);
            },
            allowDuringAutoChat: false);
        if (!ran)
        {
            SetArenaRunStatus("Output continuation is unavailable while another arena operation is active.");
        }
    }

    private void CloseProviderModelsPanel()
    {
        if (ProviderModelsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var returnSurface = _providerModelsReturnSurface;
        var returnTarget = _providerModelsFocusReturnTarget;
        _providerModelsFocusReturnTarget = null;
        _providerModelsReturnSurface = ShellSurface.Lab;

        switch (returnSurface)
        {
            case ShellSurface.World when IsWorldDebugEnabled(_wpfSettings):
                ShowWorldPanel();
                break;
            case ShellSurface.Agent when IsAgentWorkspaceEnabled(_wpfSettings):
                ShowAgentPanel();
                break;
            case ShellSurface.Collaborate:
                ShowCollaboratePanel();
                break;
            case ShellSurface.ExperimentLab when IsExperimentLabEnabled(_wpfSettings):
                ShowExperimentLabPanel();
                break;
            default:
                ShowTranscriptPanel(clearFilters: false);
                break;
        }

        RestoreOverlayFocus(
            returnTarget,
            ModelsButton,
            () => ProviderModelsPanel.Visibility != Visibility.Visible);
    }

    private void RefreshProviderModelsPresentationIfVisible()
    {
        if (_providerModelsSurfaceCoordinator is null
            || ProviderModelsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        _ = RunTrackedBackgroundOperationSafelyAsync(
            "Models presentation refresh",
            cancellationToken => ProviderModelsSurface.RefreshAsync(
                refreshCatalog: false,
                cancellationToken));
    }
}
