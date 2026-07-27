using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class AppSettingsCoordinator
{
    private readonly Dispatcher dispatcher;
    private readonly ShellNavigationCoordinator shellNavigation;
    private readonly DispatcherTimer modelRefreshTimer;
    private readonly Func<bool> isVisible;
    private readonly Func<bool, Task> refreshAdvertisedModelsAsync;
    private readonly Expander modelProviderSettingsExpander;
    private readonly TextBox providerBaseUrlText;
    private readonly ComboBox providerModelText;
    private readonly Button testProviderButton;
    private readonly RotateTransform settingsGearRotate;
    private readonly Func<bool> animationsEnabled;

    public AppSettingsCoordinator(
        Dispatcher dispatcher,
        ShellNavigationCoordinator shellNavigation,
        DispatcherTimer modelRefreshTimer,
        Func<bool> isVisible,
        Func<bool, Task> refreshAdvertisedModelsAsync,
        Expander modelProviderSettingsExpander,
        TextBox providerBaseUrlText,
        ComboBox providerModelText,
        Button testProviderButton,
        RotateTransform settingsGearRotate,
        Func<bool>? animationsEnabled = null)
    {
        this.dispatcher = dispatcher;
        this.shellNavigation = shellNavigation;
        this.modelRefreshTimer = modelRefreshTimer;
        this.isVisible = isVisible;
        this.refreshAdvertisedModelsAsync = refreshAdvertisedModelsAsync;
        this.modelProviderSettingsExpander = modelProviderSettingsExpander;
        this.providerBaseUrlText = providerBaseUrlText;
        this.providerModelText = providerModelText;
        this.testProviderButton = testProviderButton;
        this.settingsGearRotate = settingsGearRotate;
        this.animationsEnabled = animationsEnabled ?? (() => SystemMotionPreferences.AnimationsEnabled);
    }

    public void Toggle()
    {
        AnimateSettingsGear();
        SetVisible(!isVisible());
    }

    /// <summary>
    /// Raised after the overlay's visibility has been applied, whichever route
    /// asked for it. The host uses this to announce the change once, rather than
    /// each caller remembering to.
    /// </summary>
    public Action<bool>? VisibilityChanged { get; set; }

    public void SetVisible(bool visible)
    {
        shellNavigation.SetAppSettingsVisible(visible);
        if (visible)
        {
            modelRefreshTimer.Start();
            if (modelProviderSettingsExpander.IsExpanded)
            {
                _ = refreshAdvertisedModelsAsync(true);
            }
        }
        else
        {
            modelRefreshTimer.Stop();
        }

        VisibilityChanged?.Invoke(visible);
    }

    public void OpenModelProviderSettings(string? baseUrl = null, string? model = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            providerBaseUrlText.Text = baseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            providerModelText.Text = model.Trim();
        }

        SetVisible(true);
        modelProviderSettingsExpander.IsExpanded = true;
        dispatcher.BeginInvoke(() =>
        {
            modelProviderSettingsExpander.BringIntoView();
            FocusProviderTarget(providerModelText.Text).Focus();
        }, DispatcherPriority.Background);
    }

    internal static bool ShouldFocusModelPicker(string model)
    {
        return string.IsNullOrWhiteSpace(model);
    }

    internal static bool ShouldAnimateSettingsGear(bool systemAnimationsEnabled)
    {
        return systemAnimationsEnabled;
    }

    internal void RefreshMotionPreference()
    {
        if (!ShouldAnimateSettingsGear(animationsEnabled()))
        {
            settingsGearRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private Control FocusProviderTarget(string model)
    {
        return ShouldFocusModelPicker(model) ? providerModelText : testProviderButton;
    }

    private void AnimateSettingsGear()
    {
        if (!ShouldAnimateSettingsGear(animationsEnabled()))
        {
            settingsGearRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }

        var animation = new DoubleAnimation(
            settingsGearRotate.Angle,
            settingsGearRotate.Angle + 120,
            TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        settingsGearRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }
}
