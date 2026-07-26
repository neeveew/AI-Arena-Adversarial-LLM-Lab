using System.Windows;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// A virtualizing transcript list. Rows are plain data items; their (imperatively built)
/// card content is produced lazily by <see cref="ContentFactory"/> when a container is
/// realized or recycled, so only the visible cards exist as live controls. This keeps the
/// proven imperative card renderer while gaining UI virtualization for long sessions.
/// </summary>
public sealed class TranscriptListBox : ListBox
{
    /// <summary>Maps a row data item to its content element. Set once by the coordinator.</summary>
    public Func<object, UIElement>? ContentFactory { get; set; }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ListBoxItem container && ContentFactory is not null)
        {
            container.Content = ContentFactory(item);
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        // Release the realized card so recycled containers do not pin a stale visual tree.
        if (element is ListBoxItem container)
        {
            container.Content = null;
        }

        base.ClearContainerForItemOverride(element, item);
    }

    public void ScrollToTop()
    {
        if (Items.Count > 0)
        {
            ScrollIntoView(Items[0]);
        }
    }
}
