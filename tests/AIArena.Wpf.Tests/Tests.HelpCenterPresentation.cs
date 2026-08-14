using AIArena.Wpf.Help;
using AIArena.Wpf.Help.Presentation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

internal static partial class Program
{
    private static void HelpCenterPresentationReflowsAtSupportedWidths()
    {
        Require(
            HelpCenterWindow.DebugLayoutModeForWidth(720) == HelpCenterLayoutMode.Compact,
            "narrow Help Center windows should use the topic drawer");
        Require(
            HelpCenterWindow.DebugLayoutModeForWidth(960) == HelpCenterLayoutMode.Standard,
            "960 DIP Help Center windows should keep a compact topic rail");
        Require(
            HelpCenterWindow.DebugLayoutModeForWidth(1200) == HelpCenterLayoutMode.Wide,
            "1200 DIP and wider Help Center windows should use the full topic rail");
        Require(
            HelpCenterWindow.DebugLayoutModeForWidth(1500) == HelpCenterLayoutMode.Wide,
            "1500 DIP Help Center windows should retain the wide presentation");
    }

    private static void HelpCenterPresentationHostsAccessibleTaskAndSearchContracts()
    {
        var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Help/Presentation/HelpCenterWindow.xaml");
        Require(xaml.Contains("Title=\"AI Arena - Lite - Help Center\"", StringComparison.Ordinal)
                && xaml.Contains("AutomationProperties.Name=\"AI Arena - Lite Help Center\"", StringComparison.Ordinal)
                && xaml.Contains("AI Arena - Lite  •  Shift+F1 for contextual help", StringComparison.Ordinal),
            "Help Center shell and accessibility identity should use the Lite product name");
        Require(xaml.Contains("AutomationProperties.AutomationId=\"HelpCenterSearchBox\"", StringComparison.Ordinal), "search should expose a stable UIA ID");
        Require(xaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "article and result changes should be politely announced");
        Require(xaml.Contains("HelpCenterHomeTasks", StringComparison.Ordinal), "the home page should expose task cards as one named landmark");
        Require(xaml.Contains("HelpCenterOnThisPage", StringComparison.Ordinal), "articles should expose an accessible on-this-page navigator");
        Require(xaml.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal) && xaml.Contains("TextTrimming=\"None\"", StringComparison.Ordinal), "long on-this-page labels should wrap instead of clipping");
        Require(xaml.Contains("HelpCenterMoreButton", StringComparison.Ordinal), "secondary guide-file actions should live behind overflow");
        Require(xaml.Contains("Arena.Help.Target", StringComparison.Ordinal), "primary Help Center actions should consume the shared 44-DIP Help target token");
        Require(xaml.Contains("HighlightRanges=\"{Binding TitleMatches}\"", StringComparison.Ordinal), "search titles should present matched ranges");
        Require(xaml.Contains("HighlightRanges=\"{Binding SnippetMatches}\"", StringComparison.Ordinal), "search snippets should present matched ranges");

        RunStaTest(() =>
        {
            var owner = new Window
            {
                Width = 1280,
                Height = 840,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            AttachHelpCenterPresentationResources(owner);
            var launcher = new Button { Content = "Help", Width = 80, Height = 44 };
            owner.Content = launcher;
            owner.Show();
            try
            {
                var service = new OfflineHelpContentService(FindWorkspaceFile("src/AIArena.Wpf/Help/Content/guide-manifest.json"));
                var state = new HelpCenterRunState();
                var window = new HelpCenterWindow(owner, service, launcher, state)
                {
                    Left = -10000,
                    Top = -10000
                };
                Require(window.NavigateTo("quick-start"), "hosted Help Center should navigate to a manifest article");
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                try
                {
                    var articleHeading = (TextBlock)window.FindName("ArticleHeading");
                    Require(articleHeading.IsKeyboardFocused, "contextual Help should initially focus the article heading instead of moving users back to search");
                    var homeCards = (ItemsControl)window.FindName("HomeCardsItems");
                    Require(homeCards.Items.Count == 8, "hosted Help Center should render all eight manifest journeys");
                    Require(window.ViewModel.CurrentHeadings.Count > 0, "quick start should expose parsed article headings");

                    window.DebugApplyResponsiveLayout(1200);
                    var outline = (FrameworkElement)window.FindName("OnThisPagePanel");
                    Require(outline.Visibility == Visibility.Visible, "wide hosted articles should show the on-this-page rail");

                    Require(window.NavigateTo("home"), "hosted Help Center should navigate to its task home");
                    window.DebugApplyResponsiveLayout(1200);
                    Require(outline.Visibility == Visibility.Collapsed, "the task home should not overlay its cards with a document-only on-this-page rail");
                    var homePanel = (ScrollViewer)window.FindName("HomePanel");
                    Require(homePanel.Visibility == Visibility.Visible, "the task home should remain visible after wide reflow");
                    Require(window.DebugUsesHomeScrollViewer, "Home scroll retention should use the task scroller rather than the hidden article scroller");
                    Require(((FrameworkElement)window.FindName("ArticleContentPanel")).Visibility == Visibility.Collapsed, "the article and outline layer should leave the task home layout entirely");
                    Require(window.NavigateTo("quick-start"), "leaving Home should save its independent task scroll position");
                    Require(((FrameworkElement)window.FindName("ArticleContentPanel")).Visibility == Visibility.Visible, "article navigation should restore the document and outline layer");
                    var articleViewer = (FlowDocumentScrollViewer)window.FindName("ArticleViewer");
                    var articleLink = HelpDocumentHyperlinks(articleViewer.Document)
                        .First(link => link.Tag is HelpLinkTarget { Kind: HelpLinkKind.Article });
                    var linkedArticleId = ((HelpLinkTarget)articleLink.Tag).ArticleId;
                    articleLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, articleLink));
                    Require(window.CurrentArticleId == linkedArticleId, "a rendered article hyperlink should navigate inside the modeless Help Center");
                    Require(window.NavigateTo("quick-start"), "hosted Help Center should restore quick start after hyperlink navigation");

                    window.DebugApplyResponsiveLayout(720);
                    var compactOutline = (FrameworkElement)window.FindName("CompactOnThisPagePanel");
                    Require(compactOutline.Visibility == Visibility.Visible, "compact hosted articles should show a non-overlaying on-this-page disclosure");
                    var drawerButton = (Button)window.FindName("DrawerButton");
                    drawerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var drawer = (FrameworkElement)window.FindName("CompactDrawer");
                    Require(drawer.Visibility == Visibility.Visible, "compact browse action should open the topic drawer");
                    Require(window.DebugHandleShortcut(Key.Escape, ModifierKeys.None), "Escape should be handled by the hosted Help Center");
                    Require(drawer.Visibility == Visibility.Collapsed && window.IsVisible, "first Escape should close only the open topic drawer");
                    Require(window.DebugHandleShortcut(Key.F, ModifierKeys.Control), "Control+F should be handled by the hosted Help Center");
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                    var search = (TextBox)window.FindName("SearchBox");
                    Require(search.IsKeyboardFocusWithin, "Control+F should focus the hosted Help Center search box");
                    Require(search.MinHeight >= 44 && search.ActualHeight >= 44, "Help search should retain a 44-DIP target");
                    var searchResultsPanel = (FrameworkElement)window.FindName("SearchResultsPanel");
                    search.Text = "context limit";
                    Require(string.IsNullOrEmpty(window.ViewModel.SearchText), "typing should preserve the 140 ms view-model debounce");
                    Require(searchResultsPanel.Visibility == Visibility.Collapsed, "typed search should not expose stale results before the debounced query commits");
                    PumpHelpDispatcherUntil(
                        () => window.ViewModel.SearchResults.Count > 0
                            && searchResultsPanel.Visibility == Visibility.Visible);
                    Require(window.ViewModel.SearchResults.Count > 0, "keyboard search should produce ranked results");
                    Require(searchResultsPanel.Visibility == Visibility.Visible, "the debounced view-model update should display its results surface");
                    Require(window.NavigateTo(window.CurrentArticleId), "rerendering for a theme refresh should preserve the selected article");
                    Require(searchResultsPanel.Visibility == Visibility.Visible, "rerendering should preserve active search presentation");
                    search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), Environment.TickCount, Key.Down)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    });
                    var searchResults = (ListBox)window.FindName("SearchResultsList");
                    Require(searchResults.SelectedIndex == 0, "Down from search should select the first result for keyboard navigation");
                    window.ViewModel.ClearSearch();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Require(window.TryFindResource("Arena.Button.Quiet") is Style, "hosted Help Center should resolve shared Arena control resources");
                    Require(window.TryFindResource("TextBrush") is System.Windows.Media.Brush, "hosted Help Center should resolve theme brushes");

                    window.DebugScrollArticleTo(145);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Close();
                    Require(state.LastArticleId == "quick-start", "closing the hosted Help Center should retain the last article for this app run");
                    Require(state.ScrollOffsetFor("quick-start") > 0, "closing the hosted Help Center should retain its article scroll offset");
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                }

                owner.Width = 800;
                owner.Height = 640;
                owner.UpdateLayout();
                var narrowWindow = new HelpCenterWindow(owner, service, launcher, state);
                Require(narrowWindow.Width <= 760 && narrowWindow.Height <= 600, "Help Center should size before owner-centered startup so a narrow owner cannot leave it off-screen");
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static void HelpCenterPresentationUsesManifestJourneysAndRetainsRunState()
    {
        var manifestPath = FindWorkspaceFile("src/AIArena.Wpf/Help/Content/guide-manifest.json");
        var service = new OfflineHelpContentService(manifestPath);
        var model = new HelpCenterViewModel(service);
        Require(model.HomeCards.Count == service.LoadCatalog().Journeys.Count, "home should render every manifest-defined journey");
        Require(model.HomeCards.Count == 8, "the current guide manifest should expose all eight task journeys");

        var state = new HelpCenterRunState { LastArticleId = "models-routing" };
        state.SaveScrollOffset("models-routing", 186.5);
        Require(state.LastArticleId == "models-routing", "run state should retain the last topic for reopen");
        Require(Math.Abs(state.ScrollOffsetFor("models-routing") - 186.5) < 0.01, "run state should retain per-article scroll offsets");
        Require(state.ScrollOffsetFor("quick-start") == 0, "unvisited articles should begin at the top");
    }

    private static void HelpCenterPresentationHighlightRangesAreSafe()
    {
        RunStaTest(() =>
        {
            var block = new HelpHighlightedTextBlock
            {
                HighlightText = "Model context recovery",
                HighlightRanges =
                [
                    new HelpTextRange(0, 5),
                    new HelpTextRange(999, 10),
                    new HelpTextRange(6, 500)
                ]
            };
            var rendered = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));
            Require(rendered == "Model context recovery", "highlight projection should preserve visible search copy while clipping invalid ranges");
            Require(block.Inlines.OfType<Run>().Count(run => run.FontWeight == System.Windows.FontWeights.SemiBold) == 2, "valid match ranges should render as emphasized runs");
        });
    }

    private static void AttachHelpCenterPresentationResources(FrameworkElement element)
    {
        var assemblyName = typeof(HelpCenterWindow).Assembly.GetName().Name
            ?? throw new InvalidOperationException("WPF assembly name is unavailable.");
        foreach (var relativePath in new[]
                 {
                     "UI/Theming/ThemeBrushes.xaml",
                     "UI/Theming/DesignTokens.xaml",
                     "UI/Theming/ControlStyles.xaml",
                     "UI/Theming/SurfaceStyles.xaml"
                 })
        {
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/{assemblyName};component/{relativePath}", UriKind.Relative)
            });
        }
    }

    private static void PumpHelpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                timer.Stop();
                timer.Tick -= tick;
                frame.Continue = false;
            };
            timer.Tick += tick;
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
