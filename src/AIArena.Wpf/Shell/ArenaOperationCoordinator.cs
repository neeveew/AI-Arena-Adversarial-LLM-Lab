using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class ArenaOperationCoordinator
{
    private static readonly Regex SensitiveErrorRegex = new(
        @"(?ix)(?:\b(?:api[_\s-]?key|access[_\s-]?token|authorization|bearer|client[_\s-]?secret|password|refresh[_\s-]?token)\b\s*(?::|=|\s)\s*[""']?[A-Za-z0-9_+./~=-]{8,}|\bsk-(?:proj-)?[A-Za-z0-9_-]{16,}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim operationLock;
    private readonly TextBlock loadStatus;
    private readonly TextBlock arenaRunStatus;
    private readonly Button autoChatButton;
    private readonly Button oneTurnButton;
    private readonly Button resetButton;
    private readonly Button narrateNowButton;
    private readonly Button stopButton;
    private readonly IReadOnlyList<Control> busyDisabledControls;
    private readonly Func<bool> isBusy;
    private readonly Action<bool> setBusyFlag;
    private readonly Func<bool> isAutoChatRunning;
    private readonly Action<bool, bool> updateScenarioBusyState;
    private readonly Action<bool, bool> updateInternetBusyState;
    private readonly Action<bool, bool> updateOperatorBusyState;
    private readonly Action<bool> updateAgentRosterBusyState;
    private readonly Action updateSavedStateActionButtons;
    private readonly Action<bool> updateAgentBoardBusyState;
    private readonly Action<bool> updateTranscriptActionsBusyState;
    private readonly Action<bool> updateMatchLockBusyState;
    private readonly Action<bool> updateMatchSetupBusyState;
    private readonly Action? afterBusyStateChanged;
    private readonly Func<bool> animationsEnabled;
    private readonly TextBlock? readinessStatus;
    private readonly string autoChatReadyHelp;
    private readonly string oneTurnReadyHelp;
    private readonly string narrateReadyHelp;
    private readonly object lifecycleGate = new();
    private readonly CancellationTokenSource shutdownCancellation = new();

    private Button? breathingOperationButton;
    private TaskCompletionSource<bool>? operationsDrained;
    private int activeOperationCount;
    private bool shutdownRequested;
    private bool arenaReady;
    private string readinessMessage = "Load a session to enable arena actions.";

    public ArenaOperationCoordinator(
        SemaphoreSlim operationLock,
        TextBlock loadStatus,
        TextBlock arenaRunStatus,
        Button autoChatButton,
        Button oneTurnButton,
        Button resetButton,
        Button narrateNowButton,
        Button stopButton,
        IReadOnlyList<Control> busyDisabledControls,
        Func<bool> isBusy,
        Action<bool> setBusyFlag,
        Func<bool> isAutoChatRunning,
        Action<bool, bool> updateScenarioBusyState,
        Action<bool, bool> updateInternetBusyState,
        Action<bool, bool> updateOperatorBusyState,
        Action<bool> updateAgentRosterBusyState,
        Action updateSavedStateActionButtons,
        Action<bool> updateAgentBoardBusyState,
        Action<bool> updateTranscriptActionsBusyState,
        Action<bool> updateMatchLockBusyState,
        Action<bool> updateMatchSetupBusyState,
        Action? afterBusyStateChanged = null,
        Func<bool>? animationsEnabled = null,
        TextBlock? readinessStatus = null)
    {
        this.operationLock = operationLock;
        this.loadStatus = loadStatus;
        this.arenaRunStatus = arenaRunStatus;
        this.autoChatButton = autoChatButton;
        this.oneTurnButton = oneTurnButton;
        this.resetButton = resetButton;
        this.narrateNowButton = narrateNowButton;
        this.stopButton = stopButton;
        this.busyDisabledControls = busyDisabledControls;
        this.isBusy = isBusy;
        this.setBusyFlag = setBusyFlag;
        this.isAutoChatRunning = isAutoChatRunning;
        this.updateScenarioBusyState = updateScenarioBusyState;
        this.updateInternetBusyState = updateInternetBusyState;
        this.updateOperatorBusyState = updateOperatorBusyState;
        this.updateAgentRosterBusyState = updateAgentRosterBusyState;
        this.updateSavedStateActionButtons = updateSavedStateActionButtons;
        this.updateAgentBoardBusyState = updateAgentBoardBusyState;
        this.updateTranscriptActionsBusyState = updateTranscriptActionsBusyState;
        this.updateMatchLockBusyState = updateMatchLockBusyState;
        this.updateMatchSetupBusyState = updateMatchSetupBusyState;
        this.afterBusyStateChanged = afterBusyStateChanged;
        this.animationsEnabled = animationsEnabled ?? (() => SystemMotionPreferences.AnimationsEnabled);
        this.readinessStatus = readinessStatus;
        autoChatReadyHelp = AutomationProperties.GetHelpText(autoChatButton);
        oneTurnReadyHelp = AutomationProperties.GetHelpText(oneTurnButton);
        narrateReadyHelp = AutomationProperties.GetHelpText(narrateNowButton);
        UpdateReadiness(new ArenaActionReadiness(false, readinessMessage));
    }

    public Task RunAsync(string status, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(status, null, _ => action());
    }

    public Task RunAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(status, operationButton, _ => action(), allowDuringAutoChat);
    }

    public async Task RunAsync(
        string status,
        Button? operationButton,
        Func<CancellationToken, Task> action,
        bool allowDuringAutoChat = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        var mode = OperationMode(isBusy(), allowDuringAutoChat, isAutoChatRunning());
        if (mode == ArenaOperationMode.Blocked || !TryBeginOperation(out var cancellationToken))
        {
            return;
        }

        var operationLockTaken = false;
        try
        {
            if (mode == ArenaOperationMode.OwnsBusyState)
            {
                SetBusy(true, status, stopEnabled: false, operationButton);
            }
            else
            {
                arenaRunStatus.Text = status;
                loadStatus.Text = status;
                SetBreathingOperationButton(operationButton);
                if (operationButton is not null)
                {
                    operationButton.IsEnabled = false;
                }
            }

            await operationLock.WaitAsync(cancellationToken);
            operationLockTaken = true;
            await action(cancellationToken);
        }
        catch (SnapshotConcurrencyException)
        {
            const string conflictStatus = "Session changed during this operation. Reload the session and retry; newer data was not overwritten.";
            arenaRunStatus.Text = conflictStatus;
            loadStatus.Text = conflictStatus;
        }
        catch (OperationCanceledException)
        {
            const string canceledStatus = "Operation cancelled.";
            arenaRunStatus.Text = canceledStatus;
            loadStatus.Text = canceledStatus;
        }
        catch (Exception ex)
        {
            var failureStatus = OperationFailureStatus(ex);
            arenaRunStatus.Text = failureStatus;
            loadStatus.Text = failureStatus;
        }
        finally
        {
            try
            {
                if (operationLockTaken)
                {
                    operationLock.Release();
                }

                if (mode == ArenaOperationMode.OwnsBusyState)
                {
                    SetBusy(false, arenaRunStatus.Text, stopEnabled: false);
                }
                else if (mode == ArenaOperationMode.RunsDuringAutoChat)
                {
                    if (operationButton is not null)
                    {
                        operationButton.IsEnabled = true;
                    }

                    SetBreathingOperationButton(isAutoChatRunning() ? autoChatButton : null);
                }
            }
            finally
            {
                EndOperation();
            }
        }
    }

    public async Task TrackAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!TryBeginOperation(out var cancellationToken))
        {
            throw new OperationCanceledException("Application shutdown has already started.");
        }

        try
        {
            // Provider workflows already coordinate with operationLock internally.
            // Track their real task without taking that semaphore a second time so
            // shutdown can cancel/drain them without introducing a self-deadlock.
            await action(cancellationToken);
        }
        finally
        {
            EndOperation();
        }
    }

    public void RequestShutdown()
    {
        var cancel = false;
        lock (lifecycleGate)
        {
            if (!shutdownRequested)
            {
                shutdownRequested = true;
                cancel = true;
            }
        }

        if (!cancel)
        {
            return;
        }

        try
        {
            shutdownCancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation is still requested even if an external callback fails.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already completed.
        }
    }

    public Task DrainAsync()
    {
        RequestShutdown();
        lock (lifecycleGate)
        {
            return activeOperationCount == 0
                ? Task.CompletedTask
                : operationsDrained!.Task;
        }
    }

    public void SetBusy(bool busy, string status, bool stopEnabled)
    {
        SetBusy(busy, status, stopEnabled, null);
    }

    public void SetBusy(bool busy, string status, bool stopEnabled, Button? operationButton)
    {
        setBusyFlag(busy);
        SetBreathingOperationButton(busy ? operationButton : null);
        SetButtonBreathing(stopButton, busy && stopEnabled);
        var autoChatRunning = isAutoChatRunning();
        var focusMovesToStop = stopEnabled && autoChatButton.IsKeyboardFocusWithin;
        var focusReturnsToStart = !stopEnabled && stopButton.IsKeyboardFocusWithin;
        autoChatButton.Visibility = stopEnabled ? Visibility.Collapsed : Visibility.Visible;
        stopButton.Visibility = stopEnabled ? Visibility.Visible : Visibility.Collapsed;
        autoChatButton.IsEnabled = !busy && arenaReady;
        oneTurnButton.IsEnabled = !busy && arenaReady;
        resetButton.IsEnabled = !busy;
        updateScenarioBusyState(busy, autoChatRunning);
        updateInternetBusyState(busy, autoChatRunning);
        narrateNowButton.IsEnabled = (!busy || autoChatRunning) && arenaReady;
        stopButton.IsEnabled = stopEnabled;
        if (focusMovesToStop || focusReturnsToStart)
        {
            var target = focusMovesToStop ? stopButton : autoChatButton;
            target.Dispatcher.BeginInvoke(
                () => target.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
        }
        foreach (var control in busyDisabledControls)
        {
            control.IsEnabled = !busy;
        }

        updateAgentRosterBusyState(busy);
        updateSavedStateActionButtons();
        updateAgentBoardBusyState(busy);
        updateTranscriptActionsBusyState(busy);
        updateMatchLockBusyState(busy);
        updateMatchSetupBusyState(busy);
        if (!busy)
        {
            afterBusyStateChanged?.Invoke();
        }

        if (busy && operationButton is not null)
        {
            operationButton.IsEnabled = true;
        }

        updateOperatorBusyState(busy, autoChatRunning);
        arenaRunStatus.Text = status;
    }

    public void UpdateReadiness(ArenaActionReadiness readiness)
    {
        arenaReady = readiness.CanRun;
        readinessMessage = readiness.Message;
        var busy = isBusy();
        var autoChatRunning = isAutoChatRunning();
        autoChatButton.IsEnabled = !busy && arenaReady;
        oneTurnButton.IsEnabled = !busy && arenaReady;
        narrateNowButton.IsEnabled = (!busy || autoChatRunning) && arenaReady;

        if (readinessStatus is not null)
        {
            readinessStatus.Text = readinessMessage;
            readinessStatus.Visibility = arenaReady ? Visibility.Collapsed : Visibility.Visible;
        }

        ApplyReadinessHelp(autoChatButton, autoChatReadyHelp);
        ApplyReadinessHelp(oneTurnButton, oneTurnReadyHelp);
        ApplyReadinessHelp(narrateNowButton, narrateReadyHelp);
    }

    internal static ArenaActionReadiness EvaluateReadiness(ArenaViewSnapshot snapshot)
    {
        var activeAgentCount = snapshot.Agents.Count(agent => agent.Active);
        var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
        var currentModel = SessionOverviewCoordinator.CurrentTurnModel(snapshot, current);
        var providerReachable = snapshot.ProviderOnline;
        var modelSelected = !string.IsNullOrWhiteSpace(currentModel) && currentModel != "-";
        if (activeAgentCount == 0 && (!providerReachable || !modelSelected))
        {
            return new ArenaActionReadiness(false, "Finish provider, model, and cast setup before running the arena.");
        }

        if (activeAgentCount == 0)
        {
            return new ArenaActionReadiness(false, "Add an active agent in Match Setup before running the arena.");
        }

        if (!providerReachable)
        {
            return new ArenaActionReadiness(false, "Connect the configured provider before running the arena.");
        }

        if (!modelSelected)
        {
            return new ArenaActionReadiness(false, "Select a model before running the arena.");
        }

        return new ArenaActionReadiness(true, "Arena actions ready.");
    }

    private void ApplyReadinessHelp(Button button, string readyHelp)
    {
        var help = arenaReady ? readyHelp : readinessMessage;
        AutomationProperties.SetHelpText(button, help);
        button.ToolTip = help;
    }

    internal static ArenaOperationMode OperationMode(bool busy, bool allowDuringAutoChat, bool autoChatRunning)
    {
        if (!busy)
        {
            return ArenaOperationMode.OwnsBusyState;
        }

        return allowDuringAutoChat && autoChatRunning
            ? ArenaOperationMode.RunsDuringAutoChat
            : ArenaOperationMode.Blocked;
    }

    internal static string OperationFailureStatus(Exception exception)
    {
        var rawDetail = exception.Message ?? "";
        if (rawDetail.Length > 1024)
        {
            rawDetail = rawDetail[..1024];
        }

        if (SensitiveErrorRegex.IsMatch(rawDetail))
        {
            return "Operation failed; sensitive error details were redacted.";
        }

        var detail = string.Join(
            " ",
            rawDetail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (detail.Length > 280)
        {
            detail = detail[..277].TrimEnd() + "...";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? "Operation failed. Check the current settings and try again."
            : $"Operation failed: {detail}";
    }

    private bool TryBeginOperation(out CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            if (shutdownRequested)
            {
                cancellationToken = new CancellationToken(canceled: true);
                return false;
            }

            if (activeOperationCount == 0)
            {
                operationsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activeOperationCount++;
            cancellationToken = shutdownCancellation.Token;
            return true;
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (lifecycleGate)
        {
            activeOperationCount--;
            if (activeOperationCount == 0)
            {
                drained = operationsDrained;
                operationsDrained = null;
            }
        }

        drained?.TrySetResult(true);
    }

    private void SetBreathingOperationButton(Button? button)
    {
        if (breathingOperationButton == button)
        {
            return;
        }

        if (breathingOperationButton is not null)
        {
            SetButtonBreathing(breathingOperationButton, false);
        }

        breathingOperationButton = button;
        if (breathingOperationButton is not null)
        {
            SetButtonBreathing(breathingOperationButton, true);
        }
    }

    internal void RefreshMotionPreference()
    {
        if (breathingOperationButton is not null)
        {
            ApplyButtonBusyVisual(breathingOperationButton, breathing: true);
        }
    }

    internal static bool ShouldAnimateOperationButton(bool systemAnimationsEnabled, bool breathing)
    {
        return systemAnimationsEnabled && breathing;
    }

    private void SetButtonBreathing(Button button, bool breathing)
    {
        ApplyButtonBusyVisual(button, breathing);
    }

    private void ApplyButtonBusyVisual(Button button, bool breathing)
    {
        if (!ShouldAnimateOperationButton(animationsEnabled(), breathing))
        {
            if (button.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            if (button.Effect is DropShadowEffect glow && !glow.IsFrozen)
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            }

            button.Effect = null;
            if (breathing)
            {
                // The adjacent status text and disabled action remain the primary busy
                // cues when Windows requests reduced motion. A slightly stronger static
                // boundary preserves state visibility without starting an animation clock.
                button.BorderThickness = new Thickness(2);
            }
            else
            {
                button.ClearValue(Control.BorderThicknessProperty);
            }
            return;
        }

        button.ClearValue(Control.BorderThicknessProperty);

        var scaleTransform = new ScaleTransform(1, 1);
        button.RenderTransform = scaleTransform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var borderColor = button.BorderBrush is SolidColorBrush borderBrush
            ? borderBrush.Color
            : Colors.White;
        var glowEffect = new DropShadowEffect
        {
            Color = borderColor,
            Direction = 0,
            ShadowDepth = 0,
            BlurRadius = 9,
            Opacity = 0.2
        };
        button.Effect = glowEffect;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var scaleAnimation = new DoubleAnimation(1, 1.025, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var glowAnimation = new DoubleAnimation(0.18, 0.62, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var blurAnimation = new DoubleAnimation(8, 15, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, glowAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnimation);
    }
}

internal sealed record ArenaActionReadiness(bool CanRun, string Message);

internal enum ArenaOperationMode
{
    OwnsBusyState,
    RunsDuringAutoChat,
    Blocked
}
