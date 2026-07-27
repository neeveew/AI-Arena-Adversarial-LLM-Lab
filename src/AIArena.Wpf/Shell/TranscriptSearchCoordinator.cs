using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class TranscriptSearchCoordinator : IDisposable
{
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(150);
    private readonly Window owner;
    private readonly Dispatcher dispatcher;
    private readonly Popup searchPopup;
    private readonly Button searchButton;
    private readonly TextBox searchText;
    private readonly Button clearSearchButton;
    private readonly FrameworkElement dragHandle;
    private readonly StackPanel recentSearchItems;
    private readonly TextBlock resultCountText;
    private readonly TextBlock? resultListHeaderText;
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
    private readonly Action<string> collaborateSearchChanged;
    private readonly Func<string, IReadOnlyList<CollaborateCoordinator.CollaborateSearchResult>> collaborateSearch;
    private readonly Func<Guid, bool> openCollaborateConversation;
    private readonly DispatcherDebouncer searchDebouncer;

    private bool isDraggingSearchPopup;
    private bool isSwitchingSearchSurface;
    private Point searchPopupDragStart;
    private double searchPopupDragStartHorizontalOffset;
    private double searchPopupDragStartVerticalOffset;
    private readonly List<RecentSearchEntry> recentSearches = [];
    private ShellSearchSurface activeSurface = ShellSearchSurface.Transcript;
    private string transcriptSearchText = "";
    private string collaborateSearchText = "";

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
        Action refreshTranscript,
        TextBlock? resultListHeaderText = null,
        Action<string>? collaborateSearchChanged = null,
        Func<string, IReadOnlyList<CollaborateCoordinator.CollaborateSearchResult>>? collaborateSearch = null,
        Func<Guid, bool>? openCollaborateConversation = null)
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
        this.resultListHeaderText = resultListHeaderText;
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
        this.collaborateSearchChanged = collaborateSearchChanged ?? (_ => { });
        this.collaborateSearch = collaborateSearch ?? (_ => []);
        this.openCollaborateConversation = openCollaborateConversation ?? (_ => false);
        searchDebouncer = new DispatcherDebouncer(
            dispatcher,
            SearchDebounceDelay,
            ApplyFilterChange);
    }

    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(CurrentSearch);

    private string CurrentSearch => searchText.Text.Trim();

    private string CurrentTranscriptSearch => SearchTextForSurface(ShellSearchSurface.Transcript);

    public void SetSurface(ShellSearchSurface surface, string placeholder, string tooltip)
    {
        CaptureActiveSearchText();
        var restoredSearch = StoredSearchTextForSurface(surface);
        activeSurface = surface;
        searchText.Tag = placeholder;
        searchText.ToolTip = tooltip;
        searchButton.ToolTip = tooltip;

        if (!string.Equals(searchText.Text, restoredSearch, StringComparison.Ordinal))
        {
            isSwitchingSearchSurface = true;
            searchText.Text = restoredSearch;
            isSwitchingSearchSurface = false;
        }

        OnFilterChanged();
        UpdateSearchState();
    }

    public IEnumerable<TranscriptMessage> FilterMessages(IEnumerable<TranscriptMessage> messages)
    {
        var search = CurrentTranscriptSearch;
        var filtered = messages.Where(TranscriptSourceEnabled);
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(message => TranscriptMatchesSearch(message, search));
        }

        return ApplyTurnFilter(filtered);
    }

    public void UpdateResultCount(int visibleCount, int totalCount)
    {
        var search = CurrentTranscriptSearch;
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
        UpdateSearchChrome();
        PopulateRecentSearches();
    }

    private void UpdateSearchChrome()
    {
        CaptureActiveSearchText();
        var active = HasActiveSearch;
        clearSearchButton.Opacity = active ? 1.0 : 0.82;
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
        transcriptSearchText = "";
        if (activeSurface == ShellSearchSurface.Transcript)
        {
            searchText.Clear();
        }

        searchPopup.IsOpen = false;
        PopulateRecentSearches();
        ShellUiHelpers.SelectComboTag(turnFilterPicker, "all");
        systemFilter.IsChecked = true;
        agentsFilter.IsChecked = true;
        narratorFilter.IsChecked = true;
        operatorFilter.IsChecked = true;
    }

    public void OnFilterChanged(bool debounceTextInput = false)
    {
        if (isSwitchingSearchSurface)
        {
            return;
        }

        CaptureActiveSearchText();
        if (isRenderingSnapshot())
        {
            return;
        }

        if (debounceTextInput)
        {
            searchDebouncer.Schedule();
            UpdateSearchChrome();
            return;
        }

        FlushPendingFilterChange();
    }

    internal void FlushPendingFilterChange()
    {
        if (searchDebouncer.IsPending)
        {
            searchDebouncer.Flush();
            return;
        }

        ApplyFilterChange();
    }

    private void ApplyFilterChange()
    {
        CaptureActiveSearchText();
        if (isRenderingSnapshot())
        {
            return;
        }

        if (activeSurface == ShellSearchSurface.Collaborate)
        {
            collaborateSearchChanged(CurrentSearch);
            UpdateSearchState();
            return;
        }

        refreshTranscript();
    }

    public void ClearSearch()
    {
        StoreCurrentSearch();
        searchText.Clear();
        FlushPendingFilterChange();
        PopulateRecentSearches();
        searchPopup.IsOpen = false;
        CancelSearchPopupDrag();
        searchButton.Focus();
    }

    public void CloseSearch()
    {
        StoreCurrentSearch();
        PopulateRecentSearches();
        searchPopup.IsOpen = false;
        CancelSearchPopupDrag();
    }

    public void ToggleSearch()
    {
        if (searchPopup.IsOpen)
        {
            CloseSearch();
            searchButton.Focus();
            return;
        }

        ShowSearch();
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
            FlushPendingFilterChange();
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
        CancelSearchPopupDrag();
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

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelSearchPopupDrag();
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(owner);
        searchPopup.HorizontalOffset = searchPopupDragStartHorizontalOffset + current.X - searchPopupDragStart.X;
        searchPopup.VerticalOffset = searchPopupDragStartVerticalOffset + current.Y - searchPopupDragStart.Y;
        e.Handled = true;
    }

    public void OnDragMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        CancelSearchPopupDrag();
        e.Handled = true;
    }

    public void OnDragLostMouseCapture()
    {
        CancelSearchPopupDrag();
    }

    public bool DebugIsDraggingSearchPopup => isDraggingSearchPopup;

    internal bool DebugIsSearchRefreshPending => searchDebouncer.IsPending;

    public void Dispose()
    {
        searchDebouncer.Dispose();
    }

    public void DebugPrimeSearchPopupDragForTests()
    {
        isDraggingSearchPopup = true;
        searchPopupDragStart = new Point(0, 0);
        searchPopupDragStartHorizontalOffset = searchPopup.HorizontalOffset;
        searchPopupDragStartVerticalOffset = searchPopup.VerticalOffset;
    }

    private void CancelSearchPopupDrag()
    {
        if (!isDraggingSearchPopup)
        {
            return;
        }

        isDraggingSearchPopup = false;
        if (dragHandle.IsMouseCaptured)
        {
            dragHandle.ReleaseMouseCapture();
        }
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

    internal static bool TranscriptMatchesSearch(TranscriptMessage message, string search)
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
        if (activeSurface != ShellSearchSurface.Transcript)
        {
            return;
        }

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
        if (activeSurface == ShellSearchSurface.Collaborate)
        {
            PopulateCollaborateSearches();
            return;
        }

        if (resultListHeaderText is not null)
        {
            resultListHeaderText.Text = "Recent searches";
        }

        if (recentSearches.Count == 0)
        {
            recentSearchItems.Children.Add(CreateEmptyState(
                "\uE721",
                "No recent searches yet",
                "Press Enter after a search to keep it here."));
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
            Background = resourceBrush("CardBrush"),
            BorderBrush = index == 0 ? resourceBrush("PrimaryBorderBrush") : resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 48,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Normal,
            Foreground = resourceBrush("TextBrush"),
            ToolTip = $"Search for {search}"
        };
        AutomationProperties.SetName(button, $"Search for {TrimRecentSearch(search)}");
        AutomationProperties.SetHelpText(button, $"Run recent transcript search for {search}.");
        AutomationProperties.SetItemStatus(button, index == 0 ? "most recent search" : "recent search");
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
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = 16,
            Foreground = index == 0 ? resourceBrush("PrimaryBorderBrush") : resourceBrush("MutedTextBrush"),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new TextBlock
        {
            Text = TrimRecentSearch(search),
            Foreground = resourceBrush("TextBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var when = new TextBlock
        {
            Text = FormatRecentSearchDate(recentSearch.SavedAt),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var open = new TextBlock
        {
            Text = "\uE8A7",
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = 13,
            Foreground = resourceBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(open, 3);
        grid.Children.Add(open);

        button.Content = grid;
        return button;
    }

    private void PopulateCollaborateSearches()
    {
        var search = CurrentSearch;
        var results = collaborateSearch(search);
        if (resultListHeaderText is not null)
        {
            resultListHeaderText.Text = string.IsNullOrWhiteSpace(search)
                ? "Recent AI Collaborate chats"
                : "Matching AI Collaborate chats";
        }

        if (results.Count == 0)
        {
            recentSearchItems.Children.Add(CreateEmptyState(
                "\uE8D4",
                string.IsNullOrWhiteSpace(search) ? "No recent chats yet" : "No matching chats",
                string.IsNullOrWhiteSpace(search)
                    ? "Saved AI Collaborate chats will appear here."
                    : "Try another prompt, answer, model, role, or memory note."));
            return;
        }

        for (var index = 0; index < results.Count; index++)
        {
            recentSearchItems.Children.Add(CreateCollaborateSearchRow(results[index], index));
        }
    }

    private Button CreateCollaborateSearchRow(CollaborateCoordinator.CollaborateSearchResult result, int index)
    {
        var button = new Button
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = index == 0 ? resourceBrush("PrimaryBorderBrush") : resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 62,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Normal,
            Foreground = resourceBrush("TextBrush"),
            ToolTip = result.Snippet
        };
        var matchDetail = string.IsNullOrWhiteSpace(CurrentSearch)
            ? "recent chat"
            : $"{result.MatchCount.ToString(CultureInfo.InvariantCulture)} {(result.MatchCount == 1 ? "hit" : "hits")}";
        AutomationProperties.SetName(button, $"Open AI Collaborate chat {TrimRecentSearch(result.Title)}, {matchDetail}");
        AutomationProperties.SetHelpText(button, string.IsNullOrWhiteSpace(result.Snippet)
            ? "Open this saved AI Collaborate chat."
            : TrimAutomationText(result.Snippet, 160));
        AutomationProperties.SetItemStatus(button, matchDetail);
        button.Click += (_, _) =>
        {
            if (openCollaborateConversation(result.Id))
            {
                searchPopup.IsOpen = false;
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "\uE8D4",
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = 16,
            Foreground = index == 0 ? resourceBrush("PrimaryBorderBrush") : resourceBrush("MutedTextBrush"),
            Margin = new Thickness(0, 2, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        });

        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = result.Title,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(new TextBlock
        {
            Text = result.Snippet,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var meta = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(CurrentSearch)
                ? FormatRecentSearchDate(result.UpdatedAt.LocalDateTime)
                : $"{result.MatchCount.ToString(CultureInfo.InvariantCulture)} {(result.MatchCount == 1 ? "hit" : "hits")}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);

        button.Content = grid;
        return button;
    }

    public void ClosePopup()
    {
        searchPopup.IsOpen = false;
    }

    /// <summary>Replaces the result list with a single status line.</summary>
    public void ShowCrossSessionMessage(string message)
    {
        if (resultListHeaderText is not null)
        {
            resultListHeaderText.Text = "All sessions";
        }

        recentSearchItems.Children.Clear();
        recentSearchItems.Children.Add(CreateEmptyState("", "All sessions", message));
    }

    /// <summary>
    /// Lists matches from other sessions, each row switching to that session.
    /// </summary>
    public void ShowCrossSessionResults(
        string query,
        IReadOnlyList<CrossSessionSearchService.Hit> hits,
        Action<string> openSession)
    {
        if (resultListHeaderText is not null)
        {
            resultListHeaderText.Text = $"All sessions - {hits.Count} {(hits.Count == 1 ? "match" : "matches")}";
        }

        recentSearchItems.Children.Clear();
        if (hits.Count == 0)
        {
            recentSearchItems.Children.Add(CreateEmptyState(
                "",
                "No matches in other sessions",
                $"Nothing matched \"{TrimSearchForDisplay(query)}\" in the stored sessions."));
            return;
        }

        foreach (var group in hits.GroupBy(hit => hit.SessionId).OrderByDescending(group => group.First().SessionLastModified))
        {
            recentSearchItems.Children.Add(CreateCrossSessionRow(group.Key, group.ToArray(), openSession));
        }
    }

    private Button CreateCrossSessionRow(
        string sessionId,
        IReadOnlyList<CrossSessionSearchService.Hit> hits,
        Action<string> openSession)
    {
        var first = hits[0];
        var button = new Button
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Normal,
            Foreground = resourceBrush("TextBrush"),
            ToolTip = $"Open session {sessionId}"
        };
        AutomationProperties.SetName(button, $"Open session {sessionId}");
        AutomationProperties.SetHelpText(
            button,
            $"{hits.Count} matching turn(s) in session {sessionId}. Opens that session.");
        button.Click += (_, _) => openSession(sessionId);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{sessionId}  -  {hits.Count} {(hits.Count == 1 ? "match" : "matches")}",
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = ArenaTokens.LabelFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Turn {first.Turn} - {first.Speaker}: {first.Excerpt}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = ArenaTokens.CaptionFontSize,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 46,
            Margin = new Thickness(0, 3, 0, 0)
        });
        button.Content = stack;
        return button;
    }

    private Border CreateEmptyState(string icon, string title, string body)
    {
        return new Border
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = icon,
                        FontFamily = ArenaTokens.IconFontFamily,
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 18,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    new TextBlock
                    {
                        Text = title,
                        Foreground = resourceBrush("TextBrush"),
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = body,
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 12,
                        Margin = new Thickness(0, 4, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
    }

    private void CaptureActiveSearchText()
    {
        if (activeSurface == ShellSearchSurface.Collaborate)
        {
            collaborateSearchText = searchText.Text;
            return;
        }

        transcriptSearchText = searchText.Text;
    }

    private string SearchTextForSurface(ShellSearchSurface surface)
    {
        return (activeSurface == surface
            ? searchText.Text
            : surface == ShellSearchSurface.Collaborate
                ? collaborateSearchText
                : transcriptSearchText).Trim();
    }

    private string StoredSearchTextForSurface(ShellSearchSurface surface)
    {
        return (surface == ShellSearchSurface.Collaborate
            ? collaborateSearchText
            : transcriptSearchText).Trim();
    }

    private static string TrimRecentSearch(string search)
    {
        return search.Length <= 72 ? search : $"{search[..72]}...";
    }

    private static string TrimAutomationText(string text, int maxLength)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : $"{trimmed[..Math.Max(0, maxLength - 3)]}...";
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

internal enum ShellSearchSurface
{
    Transcript,
    Collaborate
}
