using System.Globalization;
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
    private readonly StackPanel recentSearchItems;
    private readonly TextBlock resultCountText;
    private readonly ComboBox turnFilterPicker;
    private readonly CheckBox systemFilter;
    private readonly CheckBox agentsFilter;
    private readonly CheckBox narratorFilter;
    private readonly CheckBox operatorFilter;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<int?> timelineTurnFilter;
    private readonly Action refreshTranscript;

    private bool isDraggingSearchPopup;
    private Point searchPopupDragStart;
    private double searchPopupDragStartHorizontalOffset;
    private double searchPopupDragStartVerticalOffset;
    private readonly List<RecentSearchEntry> recentSearches = [];

    public TranscriptSearchCoordinator(
        Window owner,
        Dispatcher dispatcher,
        Popup searchPopup,
        Button searchButton,
        TextBox searchText,
        Button clearSearchButton,
        FrameworkElement dragHandle,
        StackPanel recentSearchItems,
        TextBlock resultCountText,
        ComboBox turnFilterPicker,
        CheckBox systemFilter,
        CheckBox agentsFilter,
        CheckBox narratorFilter,
        CheckBox operatorFilter,
        Func<bool> isRenderingSnapshot,
        Func<string, Brush> resourceBrush,
        Func<string, bool> isAgentSpeaker,
        Func<int?> timelineTurnFilter,
        Action refreshTranscript)
    {
        this.owner = owner;
        this.dispatcher = dispatcher;
        this.searchPopup = searchPopup;
        this.searchButton = searchButton;
        this.searchText = searchText;
        this.clearSearchButton = clearSearchButton;
        this.dragHandle = dragHandle;
        this.recentSearchItems = recentSearchItems;
        this.resultCountText = resultCountText;
        this.turnFilterPicker = turnFilterPicker;
        this.systemFilter = systemFilter;
        this.agentsFilter = agentsFilter;
        this.narratorFilter = narratorFilter;
        this.operatorFilter = operatorFilter;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.resourceBrush = resourceBrush;
        this.isAgentSpeaker = isAgentSpeaker;
        this.timelineTurnFilter = timelineTurnFilter;
        this.refreshTranscript = refreshTranscript;
    }

    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(CurrentSearch);

    private string CurrentSearch => searchText.Text.Trim();

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
        if (timelineTurnFilter() is int turn)
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
        PopulateRecentSearches();
    }

    public void ClearFilters()
    {
        searchText.Clear();
        searchPopup.IsOpen = false;
        PopulateRecentSearches();
        ShellUiHelpers.SelectComboTag(turnFilterPicker, "all");
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
        StoreCurrentSearch();
        searchText.Clear();
        PopulateRecentSearches();
        searchPopup.IsOpen = false;
        searchButton.Focus();
    }

    public void CloseSearch()
    {
        StoreCurrentSearch();
        PopulateRecentSearches();
        searchPopup.IsOpen = false;
    }

    public void ShowSearch()
    {
        PopulateRecentSearches();
        searchPopup.IsOpen = true;
        dispatcher.BeginInvoke(() =>
        {
            searchText.Focus();
            searchText.SelectAll();
        }, DispatcherPriority.Background);
    }

    public void OnSearchKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StoreCurrentSearch();
            PopulateRecentSearches();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        StoreCurrentSearch();
        searchPopup.IsOpen = false;
        searchButton.Focus();
        e.Handled = true;
    }

    public void OnSearchPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(searchText.Text))
        {
            return;
        }

        searchText.Focus();
        searchText.CaretIndex = 0;
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
        return timelineTurnFilter() is int turn
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

    private void StoreCurrentSearch()
    {
        var search = CurrentSearch;
        if (string.IsNullOrWhiteSpace(search))
        {
            return;
        }

        recentSearches.RemoveAll(item => item.Query.Equals(search, StringComparison.OrdinalIgnoreCase));
        recentSearches.Insert(0, new RecentSearchEntry(search, DateTime.Now));
        if (recentSearches.Count > 5)
        {
            recentSearches.RemoveRange(5, recentSearches.Count - 5);
        }
    }

    private void PopulateRecentSearches()
    {
        recentSearchItems.Children.Clear();
        if (recentSearches.Count == 0)
        {
            recentSearchItems.Children.Add(new TextBlock
            {
                Text = "Press Enter after a search to keep it here.",
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 14,
                Margin = new Thickness(18, 18, 18, 18),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        for (var index = 0; index < recentSearches.Count; index++)
        {
            recentSearchItems.Children.Add(CreateRecentSearchRow(recentSearches[index], index));
        }
    }

    private Button CreateRecentSearchRow(RecentSearchEntry recentSearch, int index)
    {
        var search = recentSearch.Query;
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = index == 0 ? Brushes.Transparent : resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(18, 0, 18, 0),
            MinHeight = 46,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Normal,
            ToolTip = $"Search for {search}"
        };
        button.Click += (_, _) =>
        {
            searchText.Text = search;
            searchText.Focus();
            searchText.SelectAll();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "\uE81C",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = resourceBrush("MutedTextBrush"),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new TextBlock
        {
            Text = TrimRecentSearch(search),
            Foreground = resourceBrush("TextBrush"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var when = new TextBlock
        {
            Text = FormatRecentSearchDate(recentSearch.SavedAt),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 14,
            Margin = new Thickness(18, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var open = new TextBlock
        {
            Text = "\uE8A7",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = resourceBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(open, 3);
        grid.Children.Add(open);

        button.Content = grid;
        return button;
    }

    private static string TrimRecentSearch(string search)
    {
        return search.Length <= 72 ? search : $"{search[..72]}...";
    }

    private static string FormatRecentSearchDate(DateTime savedAt)
    {
        var date = savedAt.Date;
        var today = DateTime.Today;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return savedAt.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private sealed record RecentSearchEntry(string Query, DateTime SavedAt);
}
