using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class TranscriptSearchCoordinator
{
    private readonly Window owner;
    private readonly Dispatcher dispatcher;
    private readonly Popup searchPopup;
    private readonly Button searchButton;
    private readonly TextBox searchText;
    private readonly Button clearSearchButton;
    private readonly FrameworkElement dragHandle;
    private readonly TextBlock resultCountText;
    private readonly ComboBox turnFilterPicker;
    private readonly CheckBox systemFilter;
    private readonly CheckBox agentsFilter;
    private readonly CheckBox narratorFilter;
    private readonly CheckBox operatorFilter;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Action refreshTranscript;
    private readonly Action scrollTranscriptToTop;

    private bool isDraggingSearchPopup;
    private Point searchPopupDragStart;
    private double searchPopupDragStartHorizontalOffset;
    private double searchPopupDragStartVerticalOffset;
    private int? timelineSelectedTurnFilter;

    public TranscriptSearchCoordinator(
        Window owner,
        Dispatcher dispatcher,
        Popup searchPopup,
        Button searchButton,
        TextBox searchText,
        Button clearSearchButton,
        FrameworkElement dragHandle,
        TextBlock resultCountText,
        ComboBox turnFilterPicker,
        CheckBox systemFilter,
        CheckBox agentsFilter,
        CheckBox narratorFilter,
        CheckBox operatorFilter,
        Func<bool> isRenderingSnapshot,
        Func<string, Brush> resourceBrush,
        Func<string, bool> isAgentSpeaker,
        Action refreshTranscript,
        Action scrollTranscriptToTop)
    {
        this.owner = owner;
        this.dispatcher = dispatcher;
        this.searchPopup = searchPopup;
        this.searchButton = searchButton;
        this.searchText = searchText;
        this.clearSearchButton = clearSearchButton;
        this.dragHandle = dragHandle;
        this.resultCountText = resultCountText;
        this.turnFilterPicker = turnFilterPicker;
        this.systemFilter = systemFilter;
        this.agentsFilter = agentsFilter;
        this.narratorFilter = narratorFilter;
        this.operatorFilter = operatorFilter;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.resourceBrush = resourceBrush;
        this.isAgentSpeaker = isAgentSpeaker;
        this.refreshTranscript = refreshTranscript;
        this.scrollTranscriptToTop = scrollTranscriptToTop;
    }

    public int? TimelineSelectedTurnFilter => timelineSelectedTurnFilter;

    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(CurrentSearch);

    private string CurrentSearch => searchText.Text.Trim();

    public void ClearTimelineFilterIfMissing(IReadOnlyList<TranscriptMessage> messages)
    {
        if (timelineSelectedTurnFilter is int selectedTurn
            && messages.All(message => message.Turn != selectedTurn))
        {
            timelineSelectedTurnFilter = null;
        }
    }

    public IEnumerable<TranscriptMessage> FilterMessages(IEnumerable<TranscriptMessage> messages)
    {
        var search = CurrentSearch;
        var filtered = messages.Where(TranscriptSourceEnabled);
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(message => TranscriptMatchesSearch(message, search));
        }

        return ApplyTurnFilter(filtered);
    }

    public void UpdateResultCount(int visibleCount, int totalCount)
    {
        var search = CurrentSearch;
        var filter = (turnFilterPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Turns";
        if (timelineSelectedTurnFilter is int turn)
        {
            filter = $"Turn {turn}";
        }

        resultCountText.Text = string.IsNullOrWhiteSpace(search)
            ? visibleCount == totalCount
                ? $"{visibleCount} shown"
                : $"{visibleCount} of {totalCount} - {filter}"
            : $"{visibleCount} {(visibleCount == 1 ? "match" : "matches")} \"{TrimSearchForDisplay(search)}\"";
    }

    public void UpdateSearchState()
    {
        var active = HasActiveSearch;
        clearSearchButton.Opacity = active ? 1.0 : 0.45;
        clearSearchButton.IsEnabled = true;
        searchText.BorderBrush = active
            ? resourceBrush("PrimaryBorderBrush")
            : resourceBrush("ControlBorderBrush");
        searchButton.BorderBrush = active
            ? resourceBrush("PrimaryBorderBrush")
            : resourceBrush("DisabledBorderBrush");
        searchButton.Foreground = active
            ? resourceBrush("PrimaryBorderBrush")
            : resourceBrush("MutedTextBrush");
    }

    public void ClearFilters()
    {
        timelineSelectedTurnFilter = null;
        searchText.Clear();
        searchPopup.IsOpen = false;
        SelectComboTag(turnFilterPicker, "all");
        systemFilter.IsChecked = true;
        agentsFilter.IsChecked = true;
        narratorFilter.IsChecked = true;
        operatorFilter.IsChecked = true;
    }

    public void OnFilterChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        refreshTranscript();
    }

    public void ClearSearch()
    {
        if (string.IsNullOrWhiteSpace(searchText.Text))
        {
            searchPopup.IsOpen = false;
            searchButton.Focus();
            return;
        }

        searchText.Clear();
        searchText.Focus();
    }

    public void ShowSearch()
    {
        searchPopup.IsOpen = true;
        dispatcher.BeginInvoke(() =>
        {
            searchText.Focus();
            searchText.SelectAll();
        }, DispatcherPriority.Background);
    }

    public void OnSearchKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        searchPopup.IsOpen = false;
        searchButton.Focus();
        e.Handled = true;
    }

    public void OnDragMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        isDraggingSearchPopup = true;
        searchPopupDragStart = e.GetPosition(owner);
        searchPopupDragStartHorizontalOffset = searchPopup.HorizontalOffset;
        searchPopupDragStartVerticalOffset = searchPopup.VerticalOffset;
        dragHandle.CaptureMouse();
        e.Handled = true;
    }

    public void OnDragMouseMove(MouseEventArgs e)
    {
        if (!isDraggingSearchPopup)
        {
            return;
        }

        var current = e.GetPosition(owner);
        searchPopup.HorizontalOffset = searchPopupDragStartHorizontalOffset + current.X - searchPopupDragStart.X;
        searchPopup.VerticalOffset = searchPopupDragStartVerticalOffset + current.Y - searchPopupDragStart.Y;
        e.Handled = true;
    }

    public void OnDragMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        isDraggingSearchPopup = false;
        dragHandle.ReleaseMouseCapture();
        e.Handled = true;
    }

    public void ToggleTimelineTurnFilter(int turn)
    {
        timelineSelectedTurnFilter = timelineSelectedTurnFilter == turn ? null : turn;
        refreshTranscript();
        scrollTranscriptToTop();
    }

    public void ClearTimelineTurnFilter()
    {
        timelineSelectedTurnFilter = null;
        refreshTranscript();
    }

    private IEnumerable<TranscriptMessage> ApplyTurnFilter(IEnumerable<TranscriptMessage> messages)
    {
        var filter = (turnFilterPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var filtered = filter switch
        {
            "latest10" => messages.OrderByDescending(message => message.Turn).Take(10),
            "latest25" => messages.OrderByDescending(message => message.Turn).Take(25),
            "errors" => messages.Where(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)),
            "pinned" => messages.Where(message => message.Pinned),
            _ => messages
        };
        return timelineSelectedTurnFilter is int turn
            ? filtered.Where(message => message.Turn == turn)
            : filtered;
    }

    private bool TranscriptSourceEnabled(TranscriptMessage message)
    {
        if (message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            return operatorFilter.IsChecked == true;
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return narratorFilter.IsChecked == true;
        }

        if (isAgentSpeaker(message.SpeakerId))
        {
            return agentsFilter.IsChecked == true;
        }

        return systemFilter.IsChecked == true;
    }

    private static bool TranscriptMatchesSearch(TranscriptMessage message, string search)
    {
        return ContainsSearch(message.Speaker, search)
            || ContainsSearch(message.SpeakerId, search)
            || ContainsSearch(message.Model, search)
            || ContainsSearch(message.Status, search)
            || ContainsSearch(message.Kind, search)
            || ContainsSearch(message.Text, search)
            || ContainsSearch(message.Reasoning, search)
            || ContainsSearch(message.InternetQuery, search)
            || ContainsSearch(message.InternetUrl, search)
            || message.InternetSources.Any(source => ContainsSearch(source, search));
    }

    private static bool ContainsSearch(string value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSearchForDisplay(string search)
    {
        return search.Length <= 24 ? search : $"{search[..24]}...";
    }

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }
}
