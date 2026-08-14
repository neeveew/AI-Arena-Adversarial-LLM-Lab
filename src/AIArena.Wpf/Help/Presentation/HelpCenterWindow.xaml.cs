using AIArena.Wpf.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIArena.Wpf.Help.Presentation;

internal partial class HelpCenterWindow : Window
{
    private readonly IHelpContentService contentService;
    private readonly HelpCenterViewModel viewModel;
    private readonly HelpCenterRunState runState;
    private IInputElement? launcher;
    private HelpCenterLayoutMode layoutMode;

    internal HelpCenterWindow(
        Window owner,
        IHelpContentService contentService,
        IInputElement? launcher = null,
        HelpCenterRunState? runState = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        this.launcher = launcher;
        this.runState = runState ?? new HelpCenterRunState();

        InitializeComponent();
        Icon = UserGuideWindowHost.CreateAppIconImageSource();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        Owner = owner;
        ApplyResponsiveBounds();
        viewModel = new HelpCenterViewModel(contentService);
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = viewModel;

        ConfigureWindowAccessibility();
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += Window_Closed;
        ContentRendered += Window_ContentRendered;
    }

    internal event EventHandler<HelpAppRouteRequestedEventArgs>? AppRouteRequested;

    internal HelpCenterViewModel ViewModel => viewModel;

    internal HelpCenterLayoutMode LayoutMode => layoutMode;

    internal string? CurrentArticleId => viewModel.CurrentArticle?.Id;

    internal HelpCenterRunState RunState => runState;

    internal void SetLauncher(IInputElement? value)
    {
        launcher = value ?? launcher;
    }

    internal bool NavigateTo(string? articleId, string? anchor = null)
    {
        SaveCurrentScrollOffset();
        if (!viewModel.Navigate(articleId, anchor))
        {
            return false;
        }

        RenderCurrentArticle(anchor);
        return true;
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    internal void FocusCurrentDestination()
    {
        if (viewModel.CurrentArticle?.Id.Equals("home", StringComparison.OrdinalIgnoreCase) == true)
        {
            FocusSearch();
            return;
        }

        ArticleHeading.Focus();
        Keyboard.Focus(ArticleHeading);
    }

    internal static HelpCenterLayoutMode DebugLayoutModeForWidth(double width) => HelpCenterLayout.ForWidth(width);

    internal void DebugApplyResponsiveLayout(double width) => ApplyResponsiveLayout(width);

    internal void DebugScrollArticleTo(double offset)
    {
        if (CurrentContentScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        scrollViewer.UpdateLayout();
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, offset));
        scrollViewer.UpdateLayout();
    }

    internal bool DebugUsesHomeScrollViewer => ReferenceEquals(CurrentContentScrollViewer(), HomePanel);

    internal bool DebugHandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
        {
            if (CompactDrawer.Visibility == Visibility.Visible)
            {
                CompactDrawer.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                viewModel.ClearSearch();
            }
            else
            {
                Close();
            }

            return true;
        }

        if (key == Key.F && modifiers.HasFlag(ModifierKeys.Control))
        {
            FocusSearch();
            return true;
        }

        if (key == Key.BrowserBack || key == Key.Left && modifiers.HasFlag(ModifierKeys.Alt))
        {
            BackButton_Click(BackButton, new RoutedEventArgs());
            return true;
        }

        if (key == Key.BrowserForward || key == Key.Right && modifiers.HasFlag(ModifierKeys.Alt))
        {
            ForwardButton_Click(ForwardButton, new RoutedEventArgs());
            return true;
        }

        return false;
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= Window_ContentRendered;
        ApplyResponsiveBounds();
        ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        if (viewModel.CurrentArticle is null)
        {
            NavigateTo("home");
        }

        Dispatcher.BeginInvoke(FocusCurrentDestination, DispatcherPriority.Input);
    }

    private void ConfigureWindowAccessibility()
    {
        FocusManager.SetIsFocusScope(HelpFocusScope, true);
        KeyboardNavigation.SetTabNavigation(HelpFocusScope, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(HelpFocusScope, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(HelpFocusScope, KeyboardNavigationMode.Contained);
        AutomationProperties.SetName(HelpFocusScope, "AI Arena - Lite Help Center");
        AutomationProperties.SetHelpText(HelpFocusScope, "Browse topics, search the guide, and open validated AI Arena - Lite destinations.");
    }

    private void ApplyResponsiveBounds()
    {
        var ownerWidth = Owner?.ActualWidth ?? 0;
        var ownerHeight = Owner?.ActualHeight ?? 0;
        var availableWidth = double.IsFinite(ownerWidth) && ownerWidth > 0 ? ownerWidth : SystemParameters.WorkArea.Width;
        var availableHeight = double.IsFinite(ownerHeight) && ownerHeight > 0 ? ownerHeight : SystemParameters.WorkArea.Height;
        MaxWidth = Math.Max(680, availableWidth - 40);
        MaxHeight = Math.Max(480, availableHeight - 40);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(1240, MaxWidth);
        Height = Math.Min(820, MaxHeight);
    }

    private void ApplyResponsiveLayout(double width)
    {
        layoutMode = HelpCenterLayout.ForWidth(width);
        var compact = layoutMode == HelpCenterLayoutMode.Compact;
        NavigationColumn.Width = compact ? new GridLength(0) : new GridLength(layoutMode == HelpCenterLayoutMode.Wide ? 288 : 244);
        NavigationGapColumn.Width = compact ? new GridLength(0) : new GridLength(1);
        NavigationPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DrawerButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        BrandBlock.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = Visibility.Visible;
        // The Home article uses its own task-card presentation rather than the
        // rendered document. Its Markdown headings therefore have no visible
        // scroll targets and must not create an outline over the task grid.
        var isHome = viewModel.CurrentArticle?.Id.Equals("home", StringComparison.OrdinalIgnoreCase) == true;
        var hasHeadings = viewModel.CurrentHeadings.Count > 0 && !isHome;
        var showOutline = layoutMode != HelpCenterLayoutMode.Compact && hasHeadings;
        OutlineColumn.Width = showOutline ? new GridLength(layoutMode == HelpCenterLayoutMode.Wide ? 220 : 186) : new GridLength(0);
        OutlineGapColumn.Width = showOutline ? new GridLength(12) : new GridLength(0);
        OnThisPagePanel.Visibility = showOutline ? Visibility.Visible : Visibility.Collapsed;
        CompactOnThisPagePanel.Visibility = layoutMode == HelpCenterLayoutMode.Compact && hasHeadings
            ? Visibility.Visible
            : Visibility.Collapsed;
        var cardPanel = new FrameworkElementFactory(typeof(UniformGrid));
        cardPanel.SetValue(UniformGrid.ColumnsProperty, layoutMode switch
        {
            HelpCenterLayoutMode.Compact => 1,
            HelpCenterLayoutMode.Standard => 2,
            _ => 3
        });
        HomeCardsItems.ItemsPanel = new ItemsPanelTemplate(cardPanel);
        if (!compact)
        {
            CompactDrawer.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderCurrentArticle(string? anchor = null)
    {
        var article = viewModel.CurrentArticle;
        if (article is null)
        {
            return;
        }

        var isHome = article.Id.Equals("home", StringComparison.OrdinalIgnoreCase);
        ArticleViewer.Document = contentService.BuildDocument(article.Id, this);
        AttachDocumentNavigation(ArticleViewer.Document);
        ArticlePanel.Visibility = Visibility.Visible;
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ArticleContentPanel.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        ArticleViewer.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        CompactDrawer.Visibility = Visibility.Collapsed;
        UpdateNavigationControls();
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            ScrollToAnchor(anchor);
        }
        else
        {
            Dispatcher.BeginInvoke(
                () => CurrentContentScrollViewer()?.ScrollToVerticalOffset(runState.ScrollOffsetFor(article.Id)),
                DispatcherPriority.Loaded);
        }

        AutomationProperties.SetName(ArticleHeading, article.Title);
        runState.LastArticleId = article.Id;
        ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        UpdateSearchMode();
    }

    private void AttachDocumentNavigation(FlowDocument document)
    {
        foreach (var hyperlink in EnumerateHyperlinks(document))
        {
            hyperlink.Click -= Hyperlink_Click;
            hyperlink.Click += Hyperlink_Click;
        }
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument document)
    {
        var navigator = document.ContentStart;
        while (navigator is not null && navigator.CompareTo(document.ContentEnd) < 0)
        {
            if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
                && navigator.GetAdjacentElement(LogicalDirection.Forward) is Hyperlink hyperlink)
            {
                yield return hyperlink;
            }

            navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
        }

        foreach (var nestedViewer in document.Blocks
                     .OfType<BlockUIContainer>()
                     .SelectMany(block => LogicalDescendants<FlowDocumentScrollViewer>(block.Child)))
        {
            foreach (var hyperlink in EnumerateHyperlinks(nestedViewer.Document))
            {
                yield return hyperlink;
            }
        }
    }

    private void Hyperlink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Hyperlink hyperlink)
        {
            return;
        }

        var target = hyperlink.Tag as HelpLinkTarget;
        if (target is null && !contentService.TryResolveLink(hyperlink.NavigateUri?.OriginalString, out target))
        {
            return;
        }

        HandleLinkTarget(target);
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        if (root is T match)
        {
            yield return match;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in LogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void HandleLinkTarget(HelpLinkTarget target)
    {
        switch (target.Kind)
        {
            case HelpLinkKind.Article:
                NavigateTo(target.ArticleId, target.Anchor);
                break;
            case HelpLinkKind.Anchor:
                ScrollToAnchor(target.Anchor);
                break;
            case HelpLinkKind.App:
                AppRouteRequested?.Invoke(this, new HelpAppRouteRequestedEventArgs(target.Route));
                break;
            case HelpLinkKind.External when target.ExternalUri is { } uri:
                ShellProcessLauncher.TryStart(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, out _);
                break;
        }
    }

    private void ScrollToAnchor(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor) || ArticleViewer.Document is null)
        {
            return;
        }

        var normalized = HelpDeepLink.NormalizeAnchor(anchor);
        foreach (var block in ArticleViewer.Document.Blocks)
        {
            if (block.Tag is string tag && HelpDeepLink.NormalizeAnchor(tag).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                block.BringIntoView();
                return;
            }
        }
    }

    private ScrollViewer? ArticleScrollViewer()
    {
        ArticleViewer.ApplyTemplate();
        return FindVisualDescendant<ScrollViewer>(ArticleViewer);
    }

    private ScrollViewer? CurrentContentScrollViewer() =>
        viewModel.CurrentArticle?.Id.Equals("home", StringComparison.OrdinalIgnoreCase) == true
            ? HomePanel
            : ArticleScrollViewer();

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            if (FindVisualDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void SaveCurrentScrollOffset()
    {
        if (viewModel.CurrentArticle is { } article && CurrentContentScrollViewer() is { } scrollViewer)
        {
            runState.SaveScrollOffset(article.Id, scrollViewer.VerticalOffset);
        }
    }

    private void UpdateNavigationControls()
    {
        BackButton.IsEnabled = viewModel.CanGoBack;
        ForwardButton.IsEnabled = viewModel.CanGoForward;
        PreviousArticleButton.IsEnabled = viewModel.PreviousArticle is not null;
        NextArticleButton.IsEnabled = viewModel.NextArticle is not null;
    }

    private void ArticleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HelpArticle article })
        {
            viewModel.ClearSearch();
            NavigateTo(article.Id);
        }
    }

    private void SearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HelpArticle article })
        {
            viewModel.ClearSearch();
            NavigateTo(article.Id);
        }
    }

    private void AdjacentArticleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HelpArticle article })
        {
            NavigateTo(article.Id);
        }
    }

    private void HomeTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string articleId })
        {
            NavigateTo(articleId);
        }
    }

    private void HeadingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string anchor })
        {
            ScrollToAnchor(anchor);
        }
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ClearSearch();
        NavigateTo("home");
    }

    private void AppActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route } && contentService.TryResolveLink(route, out var target))
        {
            HandleLinkTarget(target);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentScrollOffset();
        var location = viewModel.GoBack();
        if (location is not null)
        {
            RenderCurrentArticle(location.Anchor);
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentScrollOffset();
        var location = viewModel.GoForward();
        if (location is not null)
        {
            RenderCurrentArticle(location.Anchor);
        }
    }

    private void DrawerButton_Click(object sender, RoutedEventArgs e)
    {
        CompactDrawer.Visibility = CompactDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e) => CompactDrawer.Visibility = Visibility.Collapsed;

    private void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var guidePath = UserGuideWindowHost.ResolveUserGuidePathForHelpCenter();
        if (!string.IsNullOrWhiteSpace(guidePath))
        {
            ShellProcessLauncher.TryStart(new ProcessStartInfo(guidePath) { UseShellExecute = true }, out _);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.IsOpen = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = DebugHandleShortcut(e.Key, Keyboard.Modifiers);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HelpCenterViewModel.IsSearchMode)
            or nameof(HelpCenterViewModel.SearchResults))
        {
            Dispatcher.BeginInvoke(UpdateSearchMode, DispatcherPriority.DataBind);
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || SearchResultsList.Items.Count == 0)
        {
            return;
        }

        SearchResultsList.SelectedIndex = Math.Max(0, SearchResultsList.SelectedIndex);
        if (SearchResultsList.ItemContainerGenerator.ContainerFromIndex(SearchResultsList.SelectedIndex) is ListBoxItem item)
        {
            item.Focus();
        }

        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchResult();
        }

        e.Handled = true;
    }

    private void SearchResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchResult();
            e.Handled = true;
        }
    }

    private void OpenSelectedSearchResult()
    {
        if (SearchResultsList.SelectedItem is HelpSearchResult result)
        {
            viewModel.ClearSearch();
            NavigateTo(result.Article.Id);
        }
    }

    private void UpdateSearchMode()
    {
        var searchMode = viewModel.IsSearchMode;
        ArticlePanel.Visibility = searchMode ? Visibility.Collapsed : Visibility.Visible;
        SearchResultsPanel.Visibility = searchMode ? Visibility.Visible : Visibility.Collapsed;
        if (searchMode && SearchResultsList.Items.Count > 0)
        {
            SearchResultsList.SelectedIndex = 0;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        SaveCurrentScrollOffset();
        if (launcher is UIElement { IsVisible: true, IsEnabled: true } element)
        {
            Dispatcher.BeginInvoke(() => Keyboard.Focus(element), DispatcherPriority.Input);
        }

        launcher = null;
    }

    private void WindowShell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        DialogChrome.DragMoveIfPossible(this, e, ignoreTextInputs: true);
}

internal sealed class HelpAppRouteRequestedEventArgs(string route) : EventArgs
{
    public string Route { get; } = route;
}
