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
