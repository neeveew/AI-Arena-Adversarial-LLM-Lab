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
            var firstKeys = panel.RealizedKeys.ToArray();
            var optimizedInitialAllocated = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
            panel.ApplyViewportForTest(8_000d, 640d, 900d);
            var middleReceipt = panel.CaptureReceipt();
            var finalOffset = Math.Max(0d, middleReceipt.EstimatedExtentHeight - 640d);
            panel.ApplyViewportForTest(finalOffset, 640d, 900d);
            var finalReceipt = panel.CaptureReceipt();
            var optimizedAllocated = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
            optimizedWatch.Stop();

            Require(baselineCards.Length == messageCount, "baseline should fully realize all 80 Agent cards");
            Require(firstReceipt.LogicalRows == messageCount, "virtualized Agent chat should retain all logical rows");
            Require(firstReceipt.RealizedRows <= VirtualizingConversationPanel.MaximumViewportRows,
                "virtualized Agent chat exceeded its realized-container bound at the first viewport");
            Require(middleReceipt.RealizedRows <= VirtualizingConversationPanel.MaximumViewportRows,
                "virtualized Agent chat exceeded its realized-container bound in the middle viewport");
            Require(finalReceipt.RealizedRows <= VirtualizingConversationPanel.MaximumViewportRows,
                "virtualized Agent chat exceeded its realized-container bound at the final viewport");
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
            collaboratePanel.ApplyViewportForTest(
                Math.Max(0d, collaborateFirst.EstimatedExtentHeight - 640d),
                640d,
                980d);
            var collaborateLast = collaboratePanel.CaptureReceipt();
            Require(collaborateFirst.LogicalRows == messageCount, "virtualized Collaborate chat should retain all logical rows");
            Require(collaborateFirst.RealizedRows <= VirtualizingConversationPanel.MaximumViewportRows
                    && collaborateLast.RealizedRows <= VirtualizingConversationPanel.MaximumViewportRows,
                "virtualized Collaborate chat exceeded its realized-container bound");
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
        });
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

    private static UIElement FindRealizedElement(VirtualizingConversationPanel panel, object key)
    {
        var element = panel.GetRealizedElement(key);
        Require(element is not null, $"expected realized conversation key {key}");
        return element!;
    }
}
