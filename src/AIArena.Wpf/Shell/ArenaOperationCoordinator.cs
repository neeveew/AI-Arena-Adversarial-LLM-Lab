using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AIArena.Wpf;

internal sealed class ArenaOperationCoordinator
{
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
    private readonly Action updateOperatorBusyState;
    private readonly Action<bool> updateAgentRosterBusyState;
    private readonly Action updateSavedStateActionButtons;
    private readonly Action<bool> updateAgentBoardBusyState;
    private readonly Action<bool> updateTranscriptActionsBusyState;
    private readonly Action<bool> updateMatchLockBusyState;

    private Button? breathingOperationButton;

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
        Action updateOperatorBusyState,
        Action<bool> updateAgentRosterBusyState,
        Action updateSavedStateActionButtons,
        Action<bool> updateAgentBoardBusyState,
        Action<bool> updateTranscriptActionsBusyState,
        Action<bool> updateMatchLockBusyState)
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
    }

    public async Task RunAsync(string status, Func<Task> action)
    {
        await RunAsync(status, null, action);
    }

    public async Task RunAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat = false)
    {
        var mode = OperationMode(isBusy(), allowDuringAutoChat, isAutoChatRunning());
        if (mode == ArenaOperationMode.Blocked)
        {
            return;
        }

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

        try
        {
            await operationLock.WaitAsync();
            await action();
        }
        finally
        {
            operationLock.Release();
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
        autoChatButton.IsEnabled = !busy;
        oneTurnButton.IsEnabled = !busy;
        resetButton.IsEnabled = !busy;
        updateScenarioBusyState(busy, autoChatRunning);
        updateInternetBusyState(busy, autoChatRunning);
        narrateNowButton.IsEnabled = !busy || autoChatRunning;
        stopButton.IsEnabled = stopEnabled;
        updateOperatorBusyState();
        foreach (var control in busyDisabledControls)
        {
            control.IsEnabled = !busy;
        }

        updateAgentRosterBusyState(busy);
        updateSavedStateActionButtons();
        updateAgentBoardBusyState(busy);
        updateTranscriptActionsBusyState(busy);
        updateMatchLockBusyState(busy);
        if (busy && operationButton is not null)
        {
            operationButton.IsEnabled = true;
        }

        arenaRunStatus.Text = status;
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

    private static void SetButtonBreathing(Button button, bool breathing)
    {
        if (!breathing)
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
            return;
        }

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

internal enum ArenaOperationMode
{
    OwnsBusyState,
    RunsDuringAutoChat,
    Blocked
}
