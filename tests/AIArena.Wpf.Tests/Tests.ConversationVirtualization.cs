using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AgentWorkspaceMessage = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceMessage;

internal static partial class Program
{
    static void ConversationVirtualizationBoundsLongAgentAndCollaborateChats()
    {
        RunStaTest(() =>
        {
            const int messageCount = 80;
            var messages = Enumerable.Range(0, messageCount)
                .Select(index => new AgentWorkspaceMessage(
                    index % 3 == 0 ? "operator" : "builder",
                    index % 3 == 0 ? "Operator" : "Builder",
                    $"Message {index}: " + new string((char)('a' + (index % 20)), 560),
                    index % 3 == 0 ? "User" : "Agent",
                    index % 3 == 0 ? "" : "fixture/model",
                    new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero).AddSeconds(index)))
                .ToArray();

            // Keep the allocation comparison independent of first-use WPF and
            // panel type initialization. Full-suite order otherwise charges the
            // optimized path for class initialization that a focused run charges
            // to the baseline card factory.
            _ = CreateBaselineMessageCard(messages[0]);
            var warmupPanel = new VirtualizingConversationPanel();
            warmupPanel.AddRow(
                () => CreateBaselineMessageCard(messages[0]),
                "agent-warmup",
                AgentWorkspaceCoordinator.EstimateMessageHeight(messages[0]));
            warmupPanel.ApplyViewportForTest(0d, 640d, 900d);

            var baselineWatch = Stopwatch.StartNew();
            var baselineAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var baselineCards = messages.Select(CreateBaselineMessageCard).ToArray();
            var baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineAllocatedBefore;
            baselineWatch.Stop();

            var panel = new VirtualizingConversationPanel();
            var optimizedWatch = Stopwatch.StartNew();
            var optimizedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            foreach (var (message, index) in messages.Select((message, index) => (message, index)))
            {
                var captured = message;
                panel.AddRow(
                    () => CreateBaselineMessageCard(captured),
                    $"agent-{index}",
                    AgentWorkspaceCoordinator.EstimateMessageHeight(captured));
            }

            panel.ApplyViewportForTest(0d, 640d, 900d);
            var firstReceipt = panel.CaptureReceipt();
            var firstOverscanRows = panel.RealizedOverscanRowCountForTest(0d, 640d);
            var firstKeys = panel.RealizedKeys.ToArray();
            var optimizedInitialAllocated = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
            panel.ApplyViewportForTest(8_000d, 640d, 900d);
            var middleReceipt = panel.CaptureReceipt();
            var middleOverscanRows = panel.RealizedOverscanRowCountForTest(8_000d, 640d);
            var finalOffset = Math.Max(0d, middleReceipt.EstimatedExtentHeight - 640d);
            panel.ApplyViewportForTest(finalOffset, 640d, 900d);
            var finalReceipt = panel.CaptureReceipt();
            var finalOverscanRows = panel.RealizedOverscanRowCountForTest(finalOffset, 640d);
            var optimizedAllocated = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
            optimizedWatch.Stop();

            Require(baselineCards.Length == messageCount, "baseline should fully realize all 80 Agent cards");
            Require(firstReceipt.LogicalRows == messageCount, "virtualized Agent chat should retain all logical rows");
            Require(firstOverscanRows <= VirtualizingConversationPanel.MaximumOverscanRows
                    && middleOverscanRows <= VirtualizingConversationPanel.MaximumOverscanRows
                    && finalOverscanRows <= VirtualizingConversationPanel.MaximumOverscanRows,
                "virtualized Agent chat exceeded its optional overscan bound");
            Require(panel.RealizedKeys.Any(key => key.Equals("agent-79")),
                "virtualized Agent chat should realize the newest keyed row at the final viewport");
            Require(firstKeys.SequenceEqual(firstKeys.Distinct()), "realized Agent keys should remain unique");
            Require(firstReceipt.RealizedRows < baselineCards.Length,
                "virtualized Agent chat should materially reduce initially realized cards");
            Require(optimizedInitialAllocated <= baselineAllocated * 1.25,
                $"virtualized Agent first-viewport allocation should stay bounded while replacing full realization ({optimizedInitialAllocated} vs {baselineAllocated})");
            Require(AutomationProperties.GetName(panel.RealizedKeys.Count > 0
                        ? FindRealizedElement(panel, panel.RealizedKeys[0])
                        : null) is not null,
                "realized conversation cards should remain available to UI Automation");

            Console.WriteLine(
                $"conversation-virtualization-receipt agent messages={messageCount} baseline_cards={baselineCards.Length} " +
                $"baseline_ms={baselineWatch.Elapsed.TotalMilliseconds:0.###} baseline_alloc={baselineAllocated} " +
                $"optimized_initial_cards={firstReceipt.RealizedRows} peak_cards={finalReceipt.PeakRealizedRows} " +
                $"element_creations={finalReceipt.ElementCreations} optimized_ms={optimizedWatch.Elapsed.TotalMilliseconds:0.###} " +
                $"optimized_harness_alloc={optimizedAllocated} optimized_initial_viewport_alloc={optimizedInitialAllocated}");

            var collaboratePanel = new VirtualizingConversationPanel();
            for (var index = 0; index < messageCount; index++)
            {
                var captured = index;
                var body = $"Collaborate message {captured}\n\n```csharp\nvar value = {captured};\n```";
                collaboratePanel.AddRow(
                    () => CreateCollaborateFixtureCard(captured, body),
                    $"collaborate-{captured}",
                    CollaborateCoordinator.EstimateConversationRowHeight(body, captured % 4));
            }

            collaboratePanel.ApplyViewportForTest(0d, 640d, 980d);
            var collaborateFirst = collaboratePanel.CaptureReceipt();
            var collaborateFirstOverscan = collaboratePanel.RealizedOverscanRowCountForTest(0d, 640d);
            var collaborateFinalOffset = Math.Max(0d, collaborateFirst.EstimatedExtentHeight - 640d);
            collaboratePanel.ApplyViewportForTest(
                collaborateFinalOffset,
                640d,
                980d);
            var collaborateLast = collaboratePanel.CaptureReceipt();
            Require(collaborateFirst.LogicalRows == messageCount, "virtualized Collaborate chat should retain all logical rows");
            Require(collaborateFirstOverscan <= VirtualizingConversationPanel.MaximumOverscanRows
                    && collaboratePanel.RealizedOverscanRowCountForTest(collaborateFinalOffset, 640d)
                        <= VirtualizingConversationPanel.MaximumOverscanRows,
                "virtualized Collaborate chat exceeded its optional overscan bound");
            Require(collaboratePanel.RealizedKeys.Any(key => key.Equals("collaborate-79")),
                "virtualized Collaborate chat should realize the newest keyed row at the final viewport");
            Console.WriteLine(
                $"conversation-virtualization-receipt collaborate messages={messageCount} " +
                $"initial_cards={collaborateFirst.RealizedRows} final_cards={collaborateLast.RealizedRows} " +
                $"peak_cards={collaborateLast.PeakRealizedRows} element_creations={collaborateLast.ElementCreations}");
        });
    }

    static void ConversationVirtualizationPreservesKeysSelectionFocusAndThemeRefresh()
    {
        RunStaTest(() =>
        {
            var panel = new VirtualizingConversationPanel();
            var themeGeneration = 1;
            for (var index = 0; index < 40; index++)
            {
                var captured = index;
                panel.AddRow(
                    () => CreateStatefulFixtureCard(captured, themeGeneration),
                    $"row-{captured}",
                    112d);
            }

            panel.ApplyViewportForTest(0d, 420d, 800d);
            var rowZero = (Border)FindRealizedElement(panel, "row-0");
            var state = (StackPanel)rowZero.Child;
            var editor = state.Children.OfType<TextBox>().Single();
            var expander = state.Children.OfType<Expander>().Single();
            var panelPeer = UIElementAutomationPeer.CreatePeerForElement(panel);
            var rowPeer = panelPeer?.GetChildren()?.FirstOrDefault();
            var interactivePeers = rowPeer?.GetChildren();
            Require(interactivePeers is { Count: >= 2 }
                    && interactivePeers.Any(peer => peer.GetAutomationControlType() == AutomationControlType.Edit)
                    && interactivePeers.Any(peer => peer.GetAutomationControlType() == AutomationControlType.Group),
                "a realized virtual conversation row hid its interactive descendants from UI Automation");
            editor.Select(4, 6);
            var expectedCaret = editor.CaretIndex;
            expander.IsExpanded = true;
            panel.PinRowForTest("row-0", keepAlive: true);
            panel.ApplyViewportForTest(panel.CaptureReceipt().EstimatedExtentHeight - 420d, 420d, 800d);
            Require(panel.RealizedKeys.Contains("row-0"), "focused/live row pin must stay realized when it leaves the viewport");
            panel.PinRowForTest("row-0", keepAlive: false);
            panel.RecycleRowForTest("row-0");
            panel.ApplyViewportForTest(panel.CaptureReceipt().EstimatedExtentHeight - 420d, 420d, 800d);
            Require(!panel.RealizedKeys.Contains("row-0"), "unfocused offscreen row should be recycled");

            panel.ApplyViewportForTest(0d, 420d, 800d);
            Require(panel.RealizedVisualKeys.SequenceEqual(panel.RealizedKeys),
                "re-realized conversation rows changed visual and keyboard order");
            rowZero = (Border)FindRealizedElement(panel, "row-0");
            state = (StackPanel)rowZero.Child;
            editor = state.Children.OfType<TextBox>().Single();
            expander = state.Children.OfType<Expander>().Single();
            Require(editor.SelectionStart == 4 && editor.SelectionLength == 6 && editor.CaretIndex == expectedCaret,
                "recycled row should restore text selection and caret state");
            Require(expander.IsExpanded, "recycled row should restore expander state");

            themeGeneration = 2;
            panel.RefreshRealizedRows();
            rowZero = (Border)FindRealizedElement(panel, "row-0");
            Require((int)rowZero.Tag == 2, "theme refresh should reconstruct realized rows through keyed factories");
            Require(panel.LogicalRowCount == 40, "theme refresh should preserve logical transcript identity");

            panel.ReplaceRows(
                Enumerable.Range(0, 40)
                    .Reverse()
                    .Select(index => new VirtualizingConversationPanel.ConversationRowDefinition(
                        $"row-{index}",
                        () => CreateStatefulFixtureCard(index, themeGeneration),
                        112d))
                    .ToArray(),
                refreshRealizedRows: false);
            Require(panel.RealizedVisualKeys.SequenceEqual(panel.RealizedKeys),
                "keyed conversation reordering changed visual and keyboard order");

            var duplicateRejected = false;
            try
            {
                panel.AddRow(() => new TextBlock(), "row-0");
            }
            catch (ArgumentException)
            {
                duplicateRejected = true;
            }

            Require(duplicateRejected, "duplicate conversation row keys should fail closed");

            var focusPanel = new VirtualizingConversationPanel();
            focusPanel.AddRow(() => CreateStatefulFixtureCard(0, 1), "focus-row", 112d);
            var focusHost = new Window
            {
                Width = 500,
                Height = 300,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new ScrollViewer { Content = focusPanel }
            };
            try
            {
                focusHost.Show();
                focusHost.Activate();
                focusHost.UpdateLayout();
                focusPanel.ApplyViewportForTest(0, 240, 480);
                var focusPanelPeer = UIElementAutomationPeer.CreatePeerForElement(focusPanel);
                var focusRowPeer = focusPanelPeer?.GetChildren()?.Single();
                Require(focusRowPeer is not null, "the focused virtual conversation row had no UIA list item");
                focusRowPeer!.SetFocus();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                Require(FindRealizedElement(focusPanel, "focus-row").IsKeyboardFocusWithin,
                    "UI Automation SetFocus did not move focus into the realized conversation row");
            }
            finally
            {
                focusHost.Close();
            }

            var viewportPanel = new VirtualizingConversationPanel();
            var viewportKeys = new List<string>();
            for (var index = 0; index < 8; index++)
            {
                var captured = index;
                var key = captured == 4
                    ? $"collaborate-live-{Guid.NewGuid():N}"
                    : $"agent-live-{Guid.NewGuid():N}";
                viewportKeys.Add(key);
                viewportPanel.AddRow(
                    () => new Border
                    {
                        Height = 80,
                        Child = new TextBlock { Text = $"Viewport message {captured}" }
                    },
                    key,
                    80d,
                    automationName: captured switch
                    {
                        3 => "Agent message Builder",
                        4 => "Collaborate message AI Collaborate",
                        _ => $"Conversation message {captured}"
                    },
                    automationHelpText: captured switch
                    {
                        3 => "Builder response is streaming.",
                        4 => "AI Collaborate response is streaming.",
                        _ => $"Conversation message {captured} help."
                    });
            }

            viewportPanel.ApplyViewportForTest(0d, 200d, 400d);
            var viewportPanelPeer = UIElementAutomationPeer.CreatePeerForElement(viewportPanel);
            var viewportRowPeers = viewportPanelPeer?.GetChildren();
            Require(viewportRowPeers is { Count: 8 },
                "the hosted viewport fixture did not expose all logical conversation rows to UI Automation");
            Require(viewportRowPeers![3].IsOffscreen(),
                "a realized but disconnected overscan row should report offscreen safely");

            var viewport = new ScrollViewer
            {
                Width = 420,
                Height = 200,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = viewportPanel
            };
            var viewportHost = new Window
            {
                Width = 460,
                Height = 240,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = viewport
            };
            try
            {
                viewportHost.Show();
                viewportHost.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                viewportHost.UpdateLayout();

                Require(viewportPanel.RealizedRowCount >= 5,
                    "the hosted viewport fixture did not retain its overscan rows");
                Require(viewportRowPeers[3].GetName() == "Agent message Builder"
                        && viewportRowPeers[3].GetHelpText() == "Builder response is streaming.",
                    "a live Agent row exposed its GUID key instead of stable human automation metadata");
                Require(viewportRowPeers[4].GetName() == "Collaborate message AI Collaborate"
                        && viewportRowPeers[4].GetHelpText() == "AI Collaborate response is streaming.",
                    "a live Collaborate row exposed its GUID key instead of stable human automation metadata");
                Require(viewportRowPeers[3].IsOffscreen(),
                    "a realized overscan row outside the actual ScrollViewer viewport reported onscreen");

                var overscanElement = viewportPanel.GetRealizedElement(viewportKeys[3]);
                Require(overscanElement is not null, "the selected overscan row was unexpectedly unrealized");
                overscanElement!.Visibility = Visibility.Hidden;
                Require(viewportRowPeers[3].IsOffscreen(),
                    "an invisible realized conversation row should report offscreen safely");
                overscanElement.Visibility = Visibility.Visible;
                viewportHost.UpdateLayout();

                var scrollItem = viewportRowPeers[3].GetPattern(PatternInterface.ScrollItem) as IScrollItemProvider;
                Require(scrollItem is not null, "the overscan row did not expose its ScrollItem provider");
                scrollItem!.ScrollIntoView();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                viewportHost.UpdateLayout();
                Require(!viewportRowPeers[3].IsOffscreen(),
                    "ScrollIntoView did not move the realized overscan row into the actual viewport");
            }
            finally
            {
                viewportHost.Close();
            }

            VerifyOversizedConversationRowGeometry();
            VerifyTallConversationViewportHasNoVirtualizationHoles();
        });
    }

    static void ConversationFollowLiveRespectsReaderViewportAndExposesJumpAction()
    {
        RunStaTest(() =>
        {
            var themeGeneration = 1;
            var panel = new VirtualizingConversationPanel();
            Require(
                AutomationProperties.GetLiveSetting(panel) == AutomationLiveSetting.Polite,
                "the conversation new-message status should be exposed as a polite accessibility live region");
            for (var index = 0; index < 36; index++)
            {
                var captured = index;
                panel.AddRow(
                    () => CreateFollowLiveFixtureCard(captured, themeGeneration),
                    $"follow-row-{captured}",
                    72d,
                    automationName: $"Conversation message {captured}");
            }

            var viewport = new ScrollViewer
            {
                Width = 520,
                Height = 240,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            };
            var host = new Window
            {
                Width = 560,
                Height = 280,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new System.Windows.Documents.AdornerDecorator { Child = viewport }
            };

            try
            {
                host.Show();
                host.Activate();
                PumpFollowLiveLayout(host);
                panel.JumpToLatest();
                PumpFollowLiveLayout(host);
                Require(
                    VirtualizingConversationPanel.IsNearBottom(viewport.VerticalOffset, viewport.ViewportHeight, viewport.ExtentHeight),
                    "explicit initial follow did not reach the latest conversation row");

                viewport.ScrollToVerticalOffset(144d);
                PumpFollowLiveLayout(host);
                var readerOffset = viewport.VerticalOffset;
                Require(
                    !VirtualizingConversationPanel.IsNearBottom(readerOffset, viewport.ViewportHeight, viewport.ExtentHeight),
                    "the follow-live fixture did not enter a historical reading position");

                var historyCard = (Border)FindRealizedElement(panel, "follow-row-2");
                var historyEditor = (TextBox)historyCard.Child;
                historyEditor.Select(5, 8);
                historyEditor.Focus();
                var expectedSelectionStart = historyEditor.SelectionStart;
                var expectedSelectionLength = historyEditor.SelectionLength;

                var liveHandle = panel.AddRow(
                    () => CreateFollowLiveFixtureCard(36, themeGeneration),
                    "agent-live-follow",
                    112d,
                    keepAlive: true,
                    automationName: "Agent message Builder",
                    automationHelpText: "Builder response is streaming.");
                panel.NotifyContentChanged(newMessageCount: 1);
                panel.NotifyContentChanged();
                panel.NotifyContentChanged();
                PumpFollowLiveLayout(host);

                Require(panel.PendingNewMessageCount == 1,
                    "a streamed response should announce once when it begins, not once per delta or again at completion");
                Require(Math.Abs(viewport.VerticalOffset - readerOffset) <= 1d,
                    "new streamed content moved a user away from the historical viewport");
                Require(panel.RealizedOverscanRowCountForTest(readerOffset, viewport.ViewportHeight)
                        <= VirtualizingConversationPanel.MaximumOverscanRows + 2,
                    "follow-live updates exceeded the optional overscan bound plus focused/live pins");
                Require(historyEditor.IsKeyboardFocusWithin
                        && historyEditor.SelectionStart == expectedSelectionStart
                        && historyEditor.SelectionLength == expectedSelectionLength,
                    "follow-live updates changed focus or text selection in the historical row");

                themeGeneration = 2;
                liveHandle.UpdateFactory(
                    () => CreateFollowLiveFixtureCard(36, themeGeneration),
                    keepAlive: true);
                panel.RefreshRealizedRows();
                PumpFollowLiveLayout(host);
                historyCard = (Border)FindRealizedElement(panel, "follow-row-2");
                historyEditor = (TextBox)historyCard.Child;
                Require((int)historyCard.Tag == 2
                        && historyEditor.IsKeyboardFocusWithin
                        && historyEditor.SelectionStart == expectedSelectionStart
                        && historyEditor.SelectionLength == expectedSelectionLength,
                    "theme refresh while new messages were pending lost focus, selection, or refreshed styling");
                Require(panel.PendingNewMessageCount == 1 && Math.Abs(viewport.VerticalOffset - readerOffset) <= 1d,
                    "theme refresh changed unread state or the historical anchor");

                panel.JumpToLatest();
                PumpFollowLiveLayout(host);
                panel.AddRow(
                    () => CreateFollowLiveFixtureCard(37, themeGeneration),
                    "queued-follow-row",
                    72d,
                    automationName: "Collaborate message AI Collaborate");
                panel.NotifyContentChanged(newMessageCount: 1);
                Require(panel.HasPendingFollowScroll && panel.PendingAutoFollowMessageCount == 1,
                    $"the near-bottom response did not queue exactly one follow operation (pending={panel.HasPendingFollowScroll}, auto_count={panel.PendingAutoFollowMessageCount})");

                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = viewport
                };
                viewport.RaiseEvent(wheel);
                Require(!panel.HasPendingFollowScroll && panel.PendingNewMessageCount == 1,
                    $"the hosted scroll-up input did not cancel queued follow-live synchronously (pending={panel.HasPendingFollowScroll}, count={panel.PendingNewMessageCount}, auto_count={panel.PendingAutoFollowMessageCount})");
                viewport.ScrollToVerticalOffset(Math.Max(0d, viewport.VerticalOffset - 360d));
                PumpFollowLiveLayout(host);

                Require(panel.PendingNewMessageCount == 1,
                    $"scrolling up while an automatic follow was queued lost the unseen-message count (count={panel.PendingNewMessageCount}, offset={viewport.VerticalOffset:0.##}, viewport={viewport.ViewportHeight:0.##}, extent={viewport.ExtentHeight:0.##})");
                Require(
                    !VirtualizingConversationPanel.IsNearBottom(viewport.VerticalOffset, viewport.ViewportHeight, viewport.ExtentHeight),
                    "scrolling up while an automatic follow was queued still snapped to the latest row");

                var jumpButton = panel.JumpToLatestButton;
                Require(jumpButton is { Visibility: Visibility.Visible, IsEnabled: true }
                        && AutomationProperties.GetName(jumpButton).Contains("1 new message", StringComparison.Ordinal)
                        && AutomationProperties.GetName(jumpButton).Contains("Jump to latest", StringComparison.Ordinal),
                    "the pending response did not expose an accessible new-message count and jump action");
                host.UpdateLayout();
                var viewportBounds = viewport.TransformToAncestor(host).TransformBounds(
                    new Rect(new Point(), viewport.RenderSize));
                var jumpBounds = jumpButton!.TransformToAncestor(host).TransformBounds(
                    new Rect(new Point(), jumpButton.RenderSize));
                Require(RectContains(viewportBounds, jumpBounds),
                    $"the 100% hosted Jump to latest control escaped the conversation viewport (viewport={viewportBounds}, jump={jumpBounds})");
                var buttonCenter = jumpButton!.TransformToAncestor(host).Transform(
                    new Point(jumpButton.ActualWidth / 2d, jumpButton.ActualHeight / 2d));
                var hit = host.InputHitTest(buttonCenter) as DependencyObject;
                Require(ReferenceEquals(FindVisualAncestor<Button>(hit), jumpButton),
                    "the hosted adorner intercepted hit testing before the Jump to latest button");

                var buttonPeer = UIElementAutomationPeer.CreatePeerForElement(jumpButton)
                    ?? new ButtonAutomationPeer(jumpButton);
                var invoke = buttonPeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                Require(invoke is not null, "Jump to latest did not expose the UI Automation Invoke pattern");
                invoke!.Invoke();
                PumpFollowLiveLayout(host);
                Require(panel.PendingNewMessageCount == 0
                        && jumpButton.Visibility == Visibility.Collapsed
                        && VirtualizingConversationPanel.IsNearBottom(viewport.VerticalOffset, viewport.ViewportHeight, viewport.ExtentHeight),
                    "UI Automation Invoke did not jump to the latest row and resume follow-live");

                viewport.ScrollToVerticalOffset(Math.Max(0d, viewport.ScrollableHeight - 24d));
                PumpFollowLiveLayout(host);
                panel.AddRow(
                    () => CreateFollowLiveFixtureCard(38, themeGeneration),
                    "near-bottom-follow-row",
                    72d,
                    automationName: "Agent message Builder");
                panel.NotifyContentChanged(newMessageCount: 1);
                PumpFollowLiveLayout(host);
                Require(panel.PendingNewMessageCount == 0
                        && VirtualizingConversationPanel.IsNearBottom(viewport.VerticalOffset, viewport.ViewportHeight, viewport.ExtentHeight),
                    "returning near the bottom did not resume automatic follow-live");
            }
            finally
            {
                host.Close();
            }

            VerifyScaledHostedJumpAdornerGeometry();
        });
    }

    private static void VerifyScaledHostedJumpAdornerGeometry()
    {
        const double layoutScale = 1.5d;
        var panel = new VirtualizingConversationPanel();
        for (var index = 0; index < 32; index++)
        {
            var captured = index;
            panel.AddRow(
                () => CreateFollowLiveFixtureCard(captured, themeGeneration: 1),
                $"scaled-follow-row-{captured}",
                72d,
                automationName: $"Collaborate message {captured}");
        }

        var viewport = new ScrollViewer
        {
            Width = 480d,
            Height = 220d,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        var composer = new Border
        {
            Height = 76d,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = new TextBlock { Text = "Collaborate composer" }
        };
        var scaledSurface = new Grid
        {
            Width = 480d,
            Height = 296d,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(42d, 30d, 0d, 0d),
            LayoutTransform = new System.Windows.Media.ScaleTransform(layoutScale, layoutScale)
        };
        scaledSurface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220d) });
        scaledSurface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76d) });
        Grid.SetRow(viewport, 0);
        Grid.SetRow(composer, 1);
        scaledSurface.Children.Add(viewport);
        scaledSurface.Children.Add(composer);

        var host = new Window
        {
            Width = 820d,
            Height = 540d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = new System.Windows.Documents.AdornerDecorator { Child = scaledSurface }
        };

        try
        {
            host.Show();
            PumpFollowLiveLayout(host);
            panel.JumpToLatest();
            PumpFollowLiveLayout(host);
            viewport.ScrollToVerticalOffset(144d);
            PumpFollowLiveLayout(host);
            var readerOffset = viewport.VerticalOffset;
            Require(!VirtualizingConversationPanel.IsNearBottom(
                    readerOffset,
                    viewport.ViewportHeight,
                    viewport.ExtentHeight),
                "the 150% hosted fixture did not enter a historical reading position");

            panel.AddRow(
                () => CreateFollowLiveFixtureCard(32, themeGeneration: 1),
                "scaled-collaborate-live-row",
                72d,
                automationName: "Collaborate message AI Collaborate");
            panel.NotifyContentChanged(newMessageCount: 1);
            PumpFollowLiveLayout(host);

            var jumpButton = panel.JumpToLatestButton;
            Require(jumpButton is { Visibility: Visibility.Visible, IsEnabled: true },
                "the 150% hosted conversation did not expose Jump to latest");
            Require(Math.Abs(viewport.VerticalOffset - readerOffset) <= 1d,
                "the 150% hosted new message changed the historical scroll position");
            Require(panel.RealizedRowCount < panel.LogicalRowCount
                    && panel.RealizedOverscanRowCountForTest(readerOffset, viewport.ViewportHeight)
                        <= VirtualizingConversationPanel.MaximumOverscanRows,
                "the 150% hosted jump presentation disabled bounded conversation virtualization");

            var viewportBounds = viewport.TransformToAncestor(host).TransformBounds(
                new Rect(new Point(), viewport.RenderSize));
            var jumpBounds = jumpButton!.TransformToAncestor(host).TransformBounds(
                new Rect(new Point(), jumpButton.RenderSize));
            var composerBounds = composer.TransformToAncestor(host).TransformBounds(
                new Rect(new Point(), composer.RenderSize));
            Require(RectContains(viewportBounds, jumpBounds),
                $"the 150% hosted Jump to latest control escaped the conversation viewport (viewport={viewportBounds}, jump={jumpBounds})");
            Require(!jumpBounds.IntersectsWith(composerBounds),
                $"the 150% hosted Jump to latest control overlapped the composer (jump={jumpBounds}, composer={composerBounds})");

            var center = new Point(
                jumpBounds.Left + (jumpBounds.Width / 2d),
                jumpBounds.Top + (jumpBounds.Height / 2d));
            var hit = host.InputHitTest(center) as DependencyObject;
            Require(ReferenceEquals(FindVisualAncestor<Button>(hit), jumpButton),
                "the 150% hosted Jump to latest control was not clickable at its rendered bounds");

            var peer = UIElementAutomationPeer.CreatePeerForElement(jumpButton)
                ?? new ButtonAutomationPeer(jumpButton);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Require(invoke is not null,
                "the 150% hosted Jump to latest control lost its UI Automation Invoke pattern");
            invoke!.Invoke();
            PumpFollowLiveLayout(host);
            Require(panel.PendingNewMessageCount == 0
                    && jumpButton.Visibility == Visibility.Collapsed
                    && VirtualizingConversationPanel.IsNearBottom(
                        viewport.VerticalOffset,
                        viewport.ViewportHeight,
                        viewport.ExtentHeight),
                "the 150% hosted UI Automation action did not resume follow-live");
        }
        finally
        {
            host.Close();
        }
    }

    private static bool RectContains(Rect outer, Rect inner, double tolerance = 0.75d)
    {
        return inner.Left >= outer.Left - tolerance
            && inner.Top >= outer.Top - tolerance
            && inner.Right <= outer.Right + tolerance
            && inner.Bottom <= outer.Bottom + tolerance;
    }

    private static Border CreateBaselineMessageCard(AgentWorkspaceMessage message)
    {
        var card = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = message.Title, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = message.Body, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        AutomationProperties.SetName(card, $"Agent message {message.Title}");
        AutomationProperties.SetHelpText(card, message.Body);
        return card;
    }

    private static Border CreateCollaborateFixtureCard(int index, string body)
    {
        var card = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"AI Collaborate {index}" },
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                    new Expander { Header = "Team debate", Content = new TextBlock { Text = $"Trace {index}" } }
                }
            }
        };
        AutomationProperties.SetName(card, $"Collaborate message {index}");
        return card;
    }

    private static Border CreateStatefulFixtureCard(int index, int themeGeneration)
    {
        var textBox = new TextBox
        {
            Text = $"stateful message {index:D2} selection marker",
            IsReadOnly = true
        };
        var card = new Border
        {
            Tag = themeGeneration,
            Child = new StackPanel
            {
                Children =
                {
                    textBox,
                    new Expander { Header = "Details", Content = new TextBlock { Text = "Preserved detail" } }
                }
            }
        };
        AutomationProperties.SetName(card, $"Stateful message {index}");
        return card;
    }

    private static Border CreateFollowLiveFixtureCard(int index, int themeGeneration)
    {
        var editor = new TextBox
        {
            Text = $"follow-live message {index:D2} selection marker",
            IsReadOnly = true,
            Height = 72d
        };
        var card = new Border
        {
            Tag = themeGeneration,
            Height = 72d,
            Child = editor
        };
        AutomationProperties.SetName(card, $"Follow-live message {index}");
        return card;
    }

    private static void VerifyOversizedConversationRowGeometry()
    {
        const double oversizedHeight = 9_000d;
        const double followingHeight = 80d;
        Button? tailMarker = null;
        var panel = new VirtualizingConversationPanel();
        panel.AddRow(
            () =>
            {
                tailMarker = new Button
                {
                    Width = 180d,
                    Height = 44d,
                    Margin = new Thickness(8d),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Content = "Oversized row tail"
                };
                var content = new Grid { MinHeight = oversizedHeight };
                content.Children.Add(new TextBlock { Text = "Oversized conversation row head" });
                content.Children.Add(tailMarker);
                return new Border
                {
                    MinHeight = oversizedHeight,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Child = content
                };
            },
            "oversized-row",
            112d,
            automationName: "Oversized conversation row");
        panel.AddRow(
            () => new Border
            {
                Height = followingHeight,
                Background = System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock { Text = "Following conversation row" }
            },
            "following-row",
            followingHeight,
            automationName: "Following conversation row");

        var viewport = new ScrollViewer
        {
            Width = 500d,
            Height = 240d,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = panel
        };
        var host = new Window
        {
            Width = 540d,
            Height = 280d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = viewport
        };

        try
        {
            host.Show();
            PumpFollowLiveLayout(host);
            var panelPeer = UIElementAutomationPeer.CreatePeerForElement(panel);
            var rowPeers = panelPeer?.GetChildren();
            Require(rowPeers is { Count: 2 }
                    && rowPeers[0].GetName() == "Oversized conversation row"
                    && rowPeers[1].GetName() == "Following conversation row",
                "oversized hosted rows lost their logical UI Automation order");
            Require(rowPeers![1].IsOffscreen(),
                "the row after an oversized card should initially be offscreen");

            var receipt = panel.CaptureReceipt();
            Require(receipt.EstimatedExtentHeight >= oversizedHeight + followingHeight - 0.5d,
                $"oversized realized height was clipped out of the scroll extent ({receipt.EstimatedExtentHeight:0.##})");

            var scrollItem = rowPeers[1].GetPattern(PatternInterface.ScrollItem) as IScrollItemProvider;
            Require(scrollItem is not null, "the row after an oversized card lost ScrollItem support");
            scrollItem!.ScrollIntoView();
            PumpFollowLiveLayout(host);

            Require(viewport.VerticalOffset > 8_192d
                    && viewport.VerticalOffset >= viewport.ScrollableHeight - 1d
                    && viewport.VerticalOffset <= viewport.ScrollableHeight + 0.5d,
                $"UIA ScrollIntoView escaped the oversized-row scroll bounds (offset={viewport.VerticalOffset:0.##}, max={viewport.ScrollableHeight:0.##})");
            Require(!rowPeers[1].IsOffscreen(),
                "UIA ScrollIntoView did not expose the row after an oversized card");

            var oversizedElement = FindRealizedElement(panel, "oversized-row");
            var followingElement = FindRealizedElement(panel, "following-row");
            var oversizedBounds = oversizedElement
                .TransformToAncestor(panel)
                .TransformBounds(new Rect(new Point(), oversizedElement.RenderSize));
            var followingBounds = followingElement
                .TransformToAncestor(panel)
                .TransformBounds(new Rect(new Point(), followingElement.RenderSize));
            Require(oversizedBounds.Height >= oversizedHeight - 0.5d
                    && followingBounds.Top >= oversizedBounds.Bottom - 0.5d,
                $"the row after an oversized card overlapped it ({oversizedBounds.Bottom:0.##} vs {followingBounds.Top:0.##})");

            Require(tailMarker is { IsVisible: true }, "the oversized-row tail marker was not realized");
            var markerBounds = tailMarker!
                .TransformToAncestor(viewport)
                .TransformBounds(new Rect(new Point(), tailMarker.RenderSize));
            Require(markerBounds.Bottom > 0d && markerBounds.Top < viewport.ViewportHeight,
                "the oversized-row tail remained unreachable at the maximum scroll offset");
            var markerPoint = new Point(
                markerBounds.Left + (markerBounds.Width / 2d),
                markerBounds.Top + (markerBounds.Height / 2d));
            var markerHit = viewport.InputHitTest(markerPoint) as DependencyObject;
            Require(ReferenceEquals(FindVisualAncestor<Button>(markerHit), tailMarker),
                "the reachable oversized-row tail was covered by the following row or a virtualization gap");
        }
        finally
        {
            host.Close();
        }
    }

    private static void VerifyTallConversationViewportHasNoVirtualizationHoles()
    {
        const int rowCount = 160;
        const double rowHeight = 32d;
        var panel = new VirtualizingConversationPanel();
        for (var index = 0; index < rowCount; index++)
        {
            var captured = index;
            panel.AddRow(
                () => new Border
                {
                    Height = rowHeight,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Child = new TextBlock { Text = $"Tall viewport row {captured}" }
                },
                $"tall-row-{captured}",
                rowHeight,
                automationName: $"Tall viewport row {captured}");
        }

        var viewport = new ScrollViewer
        {
            Width = 500d,
            Height = 1_280d,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = panel
        };
        var logicalFrame = new Grid
        {
            Width = 500d,
            Height = 1_280d,
            ClipToBounds = true,
            Children = { viewport }
        };

        // Keep the proof independent of the native desktop work area: CI can
        // expose a 768px HWND while this logical frame remains exactly 1280 DIP.
        viewport.ApplyTemplate();
        for (var pass = 0; pass < 2; pass++)
        {
            logicalFrame.Measure(new Size(500d, 1_280d));
            logicalFrame.Arrange(new Rect(0d, 0d, 500d, 1_280d));
            logicalFrame.UpdateLayout();
            panel.ApplyViewportForTest(0d, 1_280d, 500d);
        }

        logicalFrame.Measure(new Size(500d, 1_280d));
        logicalFrame.Arrange(new Rect(0d, 0d, 500d, 1_280d));
        logicalFrame.UpdateLayout();

        var visibleRows = Math.Min(rowCount, (int)Math.Ceiling(1_280d / rowHeight));
        Require(visibleRows > VirtualizingConversationPanel.MaximumOverscanRows,
            "the tall logical viewport did not intersect enough short rows to exercise the former global cap");
        Require(Enumerable.Range(0, visibleRows)
                .All(index => panel.GetRealizedElement($"tall-row-{index}") is not null),
            "a tall viewport retained blank holes by dropping rows that intersect the actual viewport");
        Require(panel.RealizedOverscanRowCountForTest(0d, 1_280d)
                <= VirtualizingConversationPanel.MaximumOverscanRows,
            "the tall viewport exceeded the optional overscan-row budget");

        var hitIndex = visibleRows - 3;
        var hitRow = FindRealizedElement(panel, $"tall-row-{hitIndex}");
        var hitBounds = hitRow
            .TransformToAncestor(panel)
            .TransformBounds(new Rect(new Point(), hitRow.RenderSize));
        Require(hitBounds.Top >= 0d && hitBounds.Bottom <= 1_280.5d,
            "the late tall-viewport row was not laid out inside the visible logical frame");
        var hitPoint = new Point(
            hitBounds.Left + Math.Max(1d, hitBounds.Width / 2d),
            hitBounds.Top + (hitBounds.Height / 2d));
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(panel, hitPoint)?.VisualHit;
        Require(ReferenceEquals(FindVisualAncestor<Border>(hit), hitRow),
            "the tall viewport contained a non-hit-testable blank where a visible row belonged");
    }

    private static void PumpFollowLiveLayout(Window host)
    {
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static UIElement FindRealizedElement(VirtualizingConversationPanel panel, object key)
    {
        var element = panel.GetRealizedElement(key);
        Require(element is not null, $"expected realized conversation key {key}");
        return element!;
    }
}
