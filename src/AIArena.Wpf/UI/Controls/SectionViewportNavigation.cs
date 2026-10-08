using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIArena.Wpf.Controls;

/// <summary>Reveals one explicitly selected editor after layout, retaining the latest request only.</summary>
internal sealed class SectionViewportNavigation(ScrollViewer viewport, Func<bool> isActive)
{
    private DispatcherOperation? pending;
    private long generation;

    internal void Reveal(FrameworkElement target, UIElement focusTarget)
    {
        Cancel();
        if (!viewport.IsVisible || viewport.Dispatcher.HasShutdownStarted) return;
        var request = generation;
        pending = viewport.Dispatcher.BeginInvoke(() =>
        {
            pending = null;
            bool Current() => request == generation && isActive() && viewport.IsVisible && target.IsVisible;
            if (!Current()) return;

            if (focusTarget.IsVisible && focusTarget.IsEnabled)
            {
                if (focusTarget.Focusable) focusTarget.Focus();
                else focusTarget.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            // Focus handlers can close the panel or select a different section.
            if (!Current()) return;
            viewport.UpdateLayout();
            if (!Current() || viewport.Content is not Visual content || !target.IsDescendantOf(content)) return;

            var top = target.TransformToAncestor(content).Transform(new Point()).Y;
            if (double.IsFinite(top)) viewport.ScrollToVerticalOffset(Math.Max(0, top));
        }, DispatcherPriority.Input);
    }

    internal void Cancel()
    {
        generation++;
        pending?.Abort();
        pending = null;
    }
}
