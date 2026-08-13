namespace AIArena.Wpf.Help;

internal sealed class HelpNavigationHistory
{
    private readonly int capacity;
    private readonly List<HelpLocation> entries = [];
    private int index = -1;

    public HelpNavigationHistory(int capacity = 64)
    {
        this.capacity = Math.Clamp(capacity, 2, 256);
    }

    public HelpLocation? Current => index >= 0 && index < entries.Count ? entries[index] : null;

    public bool CanGoBack => index > 0;

    public bool CanGoForward => index >= 0 && index < entries.Count - 1;

    public IReadOnlyList<HelpLocation> Entries => entries;

    public bool Navigate(HelpLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.ArticleId))
        {
            throw new ArgumentException("A help article ID is required.", nameof(location));
        }

        var normalized = new HelpLocation(
            location.ArticleId.Trim(),
            string.IsNullOrWhiteSpace(location.Anchor) ? null : HelpDeepLink.NormalizeAnchor(location.Anchor));
        if (Current is { } current
            && current.ArticleId.Equals(normalized.ArticleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Anchor, normalized.Anchor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (CanGoForward)
        {
            entries.RemoveRange(index + 1, entries.Count - index - 1);
        }

        entries.Add(normalized);
        if (entries.Count > capacity)
        {
            entries.RemoveAt(0);
        }

        index = entries.Count - 1;
        return true;
    }

    public bool TryGoBack(out HelpLocation? location)
    {
        if (!CanGoBack)
        {
            location = Current;
            return false;
        }

        index--;
        location = Current;
        return true;
    }

    public bool TryGoForward(out HelpLocation? location)
    {
        if (!CanGoForward)
        {
            location = Current;
            return false;
        }

        index++;
        location = Current;
        return true;
    }

    public void Clear()
    {
        entries.Clear();
        index = -1;
    }
}
