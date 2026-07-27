using System.Windows;
using System.Windows.Media.Animation;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Short, uniform transitions for surfaces that appear in place, such as the
/// Match Setup and App Settings overlays.
///
/// Every entry point is a no-op when Windows reports that client-area
/// animation is off, so reduced-motion users keep the instant behaviour the
/// shell had before the motion layer existed.
/// </summary>
internal static class ArenaMotion
{
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(140));

    private static bool Enabled => SystemMotionPreferences.AnimationsEnabled;

    /// <summary>
    /// Shows an overlay, fading it in from slightly transparent. The element is
    /// left at full opacity so a cancelled animation cannot strand it faded.
    /// </summary>
    public static void RevealOverlay(UIElement element)
    {
        element.Visibility = Visibility.Visible;
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var fade = new DoubleAnimation(0.6, 1, RevealDuration)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// Hides an overlay immediately. Collapsing is not animated: a delayed
    /// collapse would let dismissed content keep taking clicks.
    /// </summary>
    public static void HideOverlay(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Drops any running reveal so a reduced-motion switch takes effect on
    /// surfaces that are already on screen.
    /// </summary>
    public static void CancelReveal(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
    }
}
