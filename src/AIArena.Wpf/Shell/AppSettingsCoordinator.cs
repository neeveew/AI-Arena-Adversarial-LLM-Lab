using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
        RotateTransform settingsGearRotate)
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
    }

    public void Toggle()
    {
        AnimateSettingsGear();
        SetVisible(!isVisible());
    }

    public void SetVisible(bool visible)
    {
        shellNavigation.SetAppSettingsVisible(visible);
        if (visible)
        {
            modelRefreshTimer.Start();
            _ = refreshAdvertisedModelsAsync(true);
        }
        else
        {
            modelRefreshTimer.Stop();
        }
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

    private Control FocusProviderTarget(string model)
    {
        return ShouldFocusModelPicker(model) ? providerModelText : testProviderButton;
    }

    private void AnimateSettingsGear()
    {
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
