using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
public static class ArenaMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, PointerFeedbackRegistration> PointerFeedbackRegistrations = new();
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(140));
    internal static readonly TimeSpan NavigationDuration = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(140);
    internal static readonly TimeSpan DisclosureDuration = TimeSpan.FromMilliseconds(160);

    internal static bool Enabled => SystemMotionPreferences.AnimationsEnabled;

    /// <summary>
    /// Enables the shared hover/press opacity cue. The behavior changes no
    /// layout or hit-testing state and switches to equivalent instant feedback
    /// whenever the effective Windows motion preference is reduced.
    /// </summary>
    public static readonly DependencyProperty IsPointerFeedbackEnabledProperty = DependencyProperty.RegisterAttached(
        "IsPointerFeedbackEnabled",
        typeof(bool),
        typeof(ArenaMotion),
        new PropertyMetadata(false, OnIsPointerFeedbackEnabledChanged));

    public static bool GetIsPointerFeedbackEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsPointerFeedbackEnabledProperty);

    public static void SetIsPointerFeedbackEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsPointerFeedbackEnabledProperty, value);

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

    /// <summary>
    /// Gives a newly materialized card a short opacity cue. The final layout is
    /// already measured before the cue starts, so normal and reduced motion use
    /// identical geometry and hit testing.
    /// </summary>
    public static void RevealCard(UIElement element) =>
        RevealInPlace(element, 0.82, FeedbackDuration);

    /// <summary>Marks a navigation handoff without delaying selection.</summary>
    public static void NavigationSelected(UIElement element) =>
        RevealInPlace(element, 0.72, NavigationDuration);

    /// <summary>Draws attention to truthful live-status replacement text.</summary>
    public static void StatusChanged(UIElement element) =>
        RevealInPlace(element, 0.68, FeedbackDuration);

    /// <summary>Clarifies disclosure content appearing after expansion.</summary>
    public static void DisclosureOpened(UIElement element) =>
        RevealInPlace(element, 0.72, DisclosureDuration);

    private static void RevealInPlace(UIElement element, double from, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!Enabled)
        {
            return;
        }

        var fade = new DoubleAnimation(Math.Clamp(from, 0, 1), 1, new Duration(duration))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void OnIsPointerFeedbackEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var registration = PointerFeedbackRegistrations.GetValue(element, static target => new PointerFeedbackRegistration(target));
        if (args.NewValue is true)
        {
            registration.Enable();
        }
        else
        {
            registration.Disable();
        }
    }

    private static void AnimatePointerState(UIElement element, double targetOpacity)
    {
        var currentOpacity = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = targetOpacity;
        if (!Enabled || Math.Abs(currentOpacity - targetOpacity) < 0.001)
        {
            return;
        }

        var animation = new DoubleAnimation(currentOpacity, targetOpacity, new Duration(FeedbackDuration))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private sealed class PointerFeedbackRegistration
    {
        private readonly WeakReference<FrameworkElement> elementReference;
        private bool enabled;
        private bool attached;
        private bool hovered;
        private bool pressed;

        internal PointerFeedbackRegistration(FrameworkElement element)
        {
            elementReference = new WeakReference<FrameworkElement>(element);
        }

        internal void Enable()
        {
            if (enabled)
            {
                return;
            }

            enabled = true;
            if (!elementReference.TryGetTarget(out var element))
            {
                enabled = false;
                return;
            }

            element.Loaded += Element_Loaded;
            element.Unloaded += Element_Unloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }

        internal void Disable()
        {
            if (!enabled)
            {
                return;
            }

            enabled = false;
            if (elementReference.TryGetTarget(out var element))
            {
                element.Loaded -= Element_Loaded;
                element.Unloaded -= Element_Unloaded;
                Detach(element);
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.ClearValue(UIElement.OpacityProperty);
            }
            else
            {
                Detach();
            }
        }

        private void Element_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Attach(element);
            }
        }

        private void Element_Unloaded(object sender, RoutedEventArgs e) =>
            Detach(sender as FrameworkElement);

        private void Attach(FrameworkElement element)
        {
            if (attached)
            {
                return;
            }

            attached = true;
            hovered = element.IsMouseOver;
            element.MouseEnter += Element_MouseEnter;
            element.MouseLeave += Element_MouseLeave;
            element.PreviewMouseLeftButtonDown += Element_PreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += Element_PreviewMouseLeftButtonUp;
            element.LostMouseCapture += Element_LostMouseCapture;
            element.IsEnabledChanged += Element_IsEnabledChanged;
            SystemMotionPreferences.PreferenceChanged += MotionPreferenceChanged;
        }

        private void Detach(FrameworkElement? knownElement = null)
        {
            if (!attached)
            {
                return;
            }

            attached = false;
            hovered = false;
            pressed = false;
            var element = knownElement;
            if (element is null)
            {
                _ = elementReference.TryGetTarget(out element);
            }

            if (element is not null)
            {
                element.MouseEnter -= Element_MouseEnter;
                element.MouseLeave -= Element_MouseLeave;
                element.PreviewMouseLeftButtonDown -= Element_PreviewMouseLeftButtonDown;
                element.PreviewMouseLeftButtonUp -= Element_PreviewMouseLeftButtonUp;
                element.LostMouseCapture -= Element_LostMouseCapture;
                element.IsEnabledChanged -= Element_IsEnabledChanged;
            }

            SystemMotionPreferences.PreferenceChanged -= MotionPreferenceChanged;
        }

        private void Element_MouseEnter(object sender, MouseEventArgs e)
        {
            hovered = true;
            ApplyCurrentState(sender as FrameworkElement);
        }

        private void Element_MouseLeave(object sender, MouseEventArgs e)
        {
            hovered = false;
            pressed = false;
            ApplyCurrentState(sender as FrameworkElement);
        }

        private void Element_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            pressed = true;
            ApplyCurrentState(sender as FrameworkElement);
        }

        private void Element_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            pressed = false;
            ApplyCurrentState(sender as FrameworkElement);
        }

        private void Element_LostMouseCapture(object sender, MouseEventArgs e)
        {
            pressed = false;
            ApplyCurrentState(sender as FrameworkElement);
        }

        private void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            if (!element.IsEnabled)
            {
                pressed = false;
            }

            ApplyCurrentState(element);
        }

        private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!SystemMotionPreferences.IsAnimationPreferenceChange(e.PropertyName))
            {
                return;
            }

            if (!elementReference.TryGetTarget(out var element))
            {
                UnsubscribeFromMotionPreferenceChanges();
                return;
            }

            var dispatcher = element.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                ApplyCurrentStateIfAttached();
                return;
            }

            if (!dispatcher.Thread.IsAlive
                || dispatcher.HasShutdownStarted
                || dispatcher.HasShutdownFinished)
            {
                UnsubscribeFromMotionPreferenceChanges();
                return;
            }

            try
            {
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.DataBind,
                    new Action(ApplyCurrentStateIfAttached));
            }
            catch (InvalidOperationException)
            {
                UnsubscribeFromMotionPreferenceChanges();
            }
        }

        private void ApplyCurrentStateIfAttached()
        {
            if (attached && elementReference.TryGetTarget(out var element))
            {
                ApplyCurrentState(element);
            }
        }

        private void UnsubscribeFromMotionPreferenceChanges() =>
            SystemMotionPreferences.PreferenceChanged -= MotionPreferenceChanged;

        private void ApplyCurrentState(FrameworkElement? element)
        {
            if (element is null)
            {
                return;
            }

            if (!element.IsEnabled)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.ClearValue(UIElement.OpacityProperty);
                return;
            }

            var opacity = pressed
                    ? 0.82
                    : hovered
                        ? 0.94
                        : 1;
            AnimatePointerState(element, opacity);
        }
    }
}
