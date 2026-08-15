using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIArena.Wpf.Controls;

/// <summary>
/// A pixel-scrolling conversation host that keeps message descriptions logically present while
/// materializing only the rows near the viewport. The surrounding <see cref="ScrollViewer"/>
/// remains the scroll owner so existing shell input, automation, and scroll-bar behavior stays
/// unchanged.
/// </summary>
public sealed class VirtualizingConversationPanel : StackPanel
{
    internal const int MaximumOverscanRows = 24;
    internal const double DefaultEstimatedRowHeight = 112d;
    private const double MinimumEstimatedRowHeight = 24d;
    private const double MaximumEstimatedRowHeight = 8_192d;
    private const double MinimumOverscan = 180d;
    internal const double FollowLatestThreshold = 48d;

    private readonly List<ConversationRow> rows = [];
    private ScrollViewer? scrollOwner;
    private AdornerLayer? newMessagesAdornerLayer;
    private ConversationNewMessagesAdorner? newMessagesAdorner;
    private bool scrollHooked;
    private bool pendingScrollToEnd;
    private bool pendingScrollOperationScheduled;
    private bool pendingAnchorCorrection;
    private bool awaitingUserNavigation;
    private bool userNavigationCompletionScheduled;
    private int pendingNewMessageCount;
    private int pendingAutoFollowMessageCount;
    private int presentedNewMessageCount = -1;
    private double lastMeasureWidth = double.NaN;
    private long nextKey;
    private int elementCreationCount;
    private int peakRealizedRowCount;

    public VirtualizingConversationPanel()
    {
        Orientation = Orientation.Vertical;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal int LogicalRowCount => rows.Count;

    internal int RealizedRowCount => rows.Count(row => row.Element is not null);

    internal int PeakRealizedRowCount => peakRealizedRowCount;

    internal int ElementCreationCount => elementCreationCount;

    internal int PendingNewMessageCount => pendingNewMessageCount;

    internal int PendingAutoFollowMessageCount => pendingAutoFollowMessageCount;

    internal bool HasPendingFollowScroll => pendingScrollToEnd;

    internal bool IsFollowingLatest => pendingScrollToEnd || ScrollOwnerIsNearBottom();

    internal Button? JumpToLatestButton => newMessagesAdorner?.JumpButton;

    internal IReadOnlyList<object> RealizedKeys => rows
        .Where(row => row.Element is not null)
        .Select(row => row.Key)
        .ToArray();

    internal IReadOnlyList<object> RealizedVisualKeys => InternalChildren
        .Cast<UIElement>()
        .Select(element => rows.Single(row => ReferenceEquals(row.Element, element)).Key)
        .ToArray();

    internal UIElement? GetRealizedElement(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return rows.FirstOrDefault(row => Equals(row.Key, key))?.Element;
    }

    internal ConversationVirtualizationReceipt CaptureReceipt()
    {
        return new ConversationVirtualizationReceipt(
            rows.Count,
            RealizedRowCount,
            peakRealizedRowCount,
            elementCreationCount,
            rows.Sum(row => row.Height));
    }

    internal int RealizedOverscanRowCountForTest(double verticalOffset, double viewportHeight)
    {
        VerifyAccess();
        var viewportStart = NormalizeOffset(verticalOffset);
        var viewportEnd = viewportStart + NormalizeViewportHeight(viewportHeight);
        var top = 0d;
        var count = 0;
        foreach (var row in rows)
        {
            var bottom = top + row.Height;
            if (row.Element is not null && !Intersects(top, bottom, viewportStart, viewportEnd))
            {
                count++;
            }

            top = bottom;
        }

        return count;
    }

    internal void PinRowForTest(object key, bool keepAlive)
    {
        ArgumentNullException.ThrowIfNull(key);
        VerifyAccess();
        var row = rows.FirstOrDefault(candidate => Equals(candidate.Key, key))
            ?? throw new ArgumentException("Unknown conversation row key.", nameof(key));
        row.KeepAlive = keepAlive;
        InvalidateMeasure();
    }

    internal void RecycleRowForTest(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        VerifyAccess();
        var row = rows.FirstOrDefault(candidate => Equals(candidate.Key, key))
            ?? throw new ArgumentException("Unknown conversation row key.", nameof(key));
        Unrealize(row, preserveViewState: true);
        InvalidateMeasure();
    }

    internal void ApplyViewportForTest(double verticalOffset, double viewportHeight, double viewportWidth)
    {
        VerifyAccess();
        var desired = DesiredRows(verticalOffset, viewportHeight);
        RealizeDesiredRows(desired);
        foreach (var row in rows.Where(row => row.Element is not null))
        {
            row.Element!.Measure(new Size(Math.Max(0d, viewportWidth), double.PositiveInfinity));
            row.Height = NormalizeMeasuredHeight(row.Element.DesiredSize.Height, row.EstimatedHeight);
            row.HasMeasuredHeight = true;
        }

        peakRealizedRowCount = Math.Max(peakRealizedRowCount, RealizedRowCount);
    }

    internal ConversationRowHandle AddRow(
        Func<UIElement> factory,
        object? key = null,
        double estimatedHeight = DefaultEstimatedRowHeight,
        bool keepAlive = false,
        string? automationName = null,
        string? automationHelpText = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        VerifyAccess();

        var effectiveKey = key ?? $"conversation-row-{++nextKey}";
        if (rows.Any(row => Equals(row.Key, effectiveKey)))
        {
            throw new ArgumentException("Conversation row keys must be unique within a view.", nameof(key));
        }

        var row = new ConversationRow(
            effectiveKey,
            factory,
            NormalizeEstimate(estimatedHeight),
            keepAlive,
            automationName,
            automationHelpText);
        rows.Add(row);
        var handle = new ConversationRowHandle(this, row);
        if (keepAlive)
        {
            Realize(row);
        }

        InvalidateMeasure();
        return handle;
    }

    internal void ReplaceRows(
        IReadOnlyList<ConversationRowDefinition> definitions,
        bool refreshRealizedRows)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        VerifyAccess();

        var duplicate = definitions
            .GroupBy(definition => definition.Key)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("Conversation row keys must be unique within a view.", nameof(definitions));
        }

        var anchor = CaptureAnchor();
        var existing = rows.ToDictionary(row => row.Key);
        var replacements = new List<ConversationRow>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (existing.Remove(definition.Key, out var row))
            {
                row.Factory = definition.Factory;
                row.KeepAlive = definition.KeepAlive;
                if (!row.HasMeasuredHeight)
                {
                    row.Height = NormalizeEstimate(definition.EstimatedHeight);
                }

                replacements.Add(row);
                continue;
            }

            replacements.Add(new ConversationRow(
                definition.Key,
                definition.Factory,
                NormalizeEstimate(definition.EstimatedHeight),
                definition.KeepAlive,
                definition.AutomationName,
                definition.AutomationHelpText));
        }

        foreach (var removed in existing.Values)
        {
            Unrealize(removed, preserveViewState: true);
        }

        rows.Clear();
        rows.AddRange(replacements);
        ReconcileRealizedVisualOrder();

        if (refreshRealizedRows)
        {
            RefreshRealizedRowsCore();
        }

        RestoreAnchor(anchor);
        InvalidateMeasure();
    }

    private void ReconcileRealizedVisualOrder()
    {
        var focusedElement = Keyboard.FocusedElement;
        var targetIndex = 0;
        foreach (var element in rows
                     .Where(row => row.Element is not null)
                     .Select(row => row.Element!))
        {
            var currentIndex = InternalChildren.IndexOf(element);
            if (currentIndex != targetIndex)
            {
                InternalChildren.RemoveAt(currentIndex);
                InternalChildren.Insert(targetIndex, element);
            }

            targetIndex++;
        }

        if (focusedElement is IInputElement inputElement && !inputElement.IsKeyboardFocused)
        {
            Keyboard.Focus(inputElement);
        }
    }

    internal void ClearRows()
    {
        VerifyAccess();
        foreach (var row in rows.ToArray())
        {
            Unrealize(row, preserveViewState: false);
        }

        rows.Clear();
        pendingScrollToEnd = false;
        pendingScrollOperationScheduled = false;
        awaitingUserNavigation = false;
        userNavigationCompletionScheduled = false;
        pendingNewMessageCount = 0;
        pendingAutoFollowMessageCount = 0;
        UpdateNewMessagesPresentation();
        scrollOwner?.ScrollToTop();
        InvalidateMeasure();
    }

    internal void RefreshRealizedRows()
    {
        VerifyAccess();
        var anchor = CaptureAnchor();
        RefreshRealizedRowsCore();
        RestoreAnchor(anchor);
        InvalidateMeasure();
    }

    internal void ScrollToEnd()
    {
        JumpToLatest();
    }

    internal void NotifyContentChanged(int newMessageCount = 0)
    {
        VerifyAccess();
        if (newMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newMessageCount));
        }

        HookScrollOwner();
        if (scrollOwner is null
            || (ScrollOwnerIsNearBottom() && !awaitingUserNavigation)
            || pendingScrollToEnd)
        {
            AddPendingAutoFollowMessages(newMessageCount);
            pendingNewMessageCount = 0;
            UpdateNewMessagesPresentation();
            ScheduleScrollToEnd();
            return;
        }

        if (newMessageCount > 0)
        {
            pendingNewMessageCount = (int)Math.Min(
                int.MaxValue,
                (long)pendingNewMessageCount + newMessageCount);
            UpdateNewMessagesPresentation();
        }

        InvalidateMeasure();
    }

    internal void JumpToLatest()
    {
        VerifyAccess();
        pendingNewMessageCount = 0;
        pendingAutoFollowMessageCount = 0;
        awaitingUserNavigation = false;
        UpdateNewMessagesPresentation();
        ScheduleScrollToEnd();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        HookScrollOwner();
        EnsureNewMessagesAdorner();
        UpdateNewMessagesPresentation();
        if (pendingScrollToEnd)
        {
            ScheduleScrollToEnd();
        }

        InvalidateMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        RemoveNewMessagesAdorner();
        UnhookScrollOwner();
    }

    private void HookScrollOwner()
    {
        var candidate = FindScrollOwner(this);
        if (ReferenceEquals(candidate, scrollOwner) && scrollHooked)
        {
            return;
        }

        RemoveNewMessagesAdorner();
        UnhookScrollOwner();
        scrollOwner = candidate;
        if (scrollOwner is null)
        {
            return;
        }

        scrollOwner.ScrollChanged += OnScrollChanged;
        scrollOwner.AddHandler(
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollOwnerPreviewMouseWheel),
            handledEventsToo: true);
        scrollOwner.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnScrollOwnerPreviewMouseLeftButtonDown),
            handledEventsToo: true);
        scrollOwner.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnScrollOwnerPreviewKeyDown),
            handledEventsToo: true);
        scrollHooked = true;
        EnsureNewMessagesAdorner();
    }

    private void UnhookScrollOwner()
    {
        if (scrollOwner is not null && scrollHooked)
        {
            scrollOwner.ScrollChanged -= OnScrollChanged;
            scrollOwner.RemoveHandler(
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnScrollOwnerPreviewMouseWheel));
            scrollOwner.RemoveHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnScrollOwnerPreviewMouseLeftButtonDown));
            scrollOwner.RemoveHandler(
                UIElement.PreviewKeyDownEvent,
                new KeyEventHandler(OnScrollOwnerPreviewKeyDown));
        }

        scrollHooked = false;
        scrollOwner = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (ScrollOwnerIsNearBottom())
        {
            if (!awaitingUserNavigation)
            {
                pendingNewMessageCount = 0;
                UpdateNewMessagesPresentation();
            }
        }
        else
        {
            awaitingUserNavigation = false;
            if (args.VerticalChange != 0d)
            {
                CancelPendingFollowLatest();
            }
        }

        if (args.VerticalChange != 0 || args.ViewportHeightChange != 0 || args.ExtentHeightChange != 0)
        {
            InvalidateMeasure();
        }
    }

    private void OnScrollOwnerPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        CancelPendingFollowLatest(expectViewportChange: true);
    }

    private void OnScrollOwnerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (FindAncestor<ScrollBar>(args.OriginalSource as DependencyObject) is not null)
        {
            CancelPendingFollowLatest(expectViewportChange: true);
        }
    }

    private void OnScrollOwnerPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.OriginalSource is TextBoxBase)
        {
            return;
        }

        if (args.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            CancelPendingFollowLatest(expectViewportChange: true);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        HookScrollOwner();

        var width = ResolveMeasureWidth(availableSize.Width);
        if (double.IsNaN(lastMeasureWidth) || Math.Abs(lastMeasureWidth - width) > 0.5d)
        {
            ResetMeasuredHeightsForWidth(width);
        }

        var anchor = CaptureAnchor();
        var desired = DesiredRows();
        RealizeDesiredRows(desired);

        foreach (var row in rows)
        {
            if (row.Element is null)
            {
                continue;
            }

            row.Element.Measure(new Size(width, double.PositiveInfinity));
            row.Height = NormalizeMeasuredHeight(row.Element.DesiredSize.Height, row.EstimatedHeight);
            row.HasMeasuredHeight = true;
        }

        peakRealizedRowCount = Math.Max(peakRealizedRowCount, RealizedRowCount);
        CorrectAnchorAfterMeasure(anchor);

        var extentHeight = rows.Sum(row => row.Height);
        if (pendingScrollToEnd)
        {
            ScheduleScrollToEnd();
        }

        return new Size(width, extentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0d;
        foreach (var row in rows)
        {
            if (row.Element is not null)
            {
                row.Element.Arrange(new Rect(0d, top, Math.Max(0d, finalSize.Width), row.Height));
            }

            top += row.Height;
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, top));
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new VirtualizingConversationPanelAutomationPeer(this);
    }

    private HashSet<ConversationRow> DesiredRows()
    {
        var desired = new HashSet<ConversationRow>();
        if (rows.Count == 0)
        {
            return desired;
        }

        var viewportHeight = scrollOwner?.ViewportHeight ?? 0d;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0d)
        {
            viewportHeight = Math.Max(ActualHeight, 640d);
        }

        var offset = Math.Max(0d, scrollOwner?.VerticalOffset ?? 0d);
        if (pendingScrollToEnd)
        {
            offset = Math.Max(0d, rows.Sum(row => row.Height) - viewportHeight);
        }

        return DesiredRows(offset, viewportHeight);
    }

    private HashSet<ConversationRow> DesiredRows(double offset, double viewportHeight)
    {
        var desired = new HashSet<ConversationRow>();
        if (rows.Count == 0)
        {
            return desired;
        }

        var normalizedOffset = NormalizeOffset(offset);
        var normalizedViewportHeight = NormalizeViewportHeight(viewportHeight);
        var viewportStart = normalizedOffset;
        var viewportEnd = viewportStart + normalizedViewportHeight;
        var overscan = Math.Max(MinimumOverscan, normalizedViewportHeight * 0.5d);
        var overscanStart = Math.Max(0d, viewportStart - overscan);
        var overscanEnd = viewportEnd + overscan;
        var top = 0d;
        var candidates = new List<(ConversationRow Row, double Distance)>();
        foreach (var row in rows)
        {
            var bottom = top + row.Height;
            if (Intersects(top, bottom, viewportStart, viewportEnd))
            {
                // The viewport itself must never contain unrealized holes. A
                // separate cap applies only to optional rows around it.
                desired.Add(row);
            }
            else if (Intersects(top, bottom, overscanStart, overscanEnd))
            {
                var distance = bottom <= viewportStart
                    ? viewportStart - bottom
                    : top - viewportEnd;
                candidates.Add((row, distance));
            }

            top = bottom;
        }

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Distance)
                     .Take(MaximumOverscanRows)
                     .Select(candidate => candidate.Row))
        {
            desired.Add(candidate);
        }

        foreach (var row in rows.Where(row => row.KeepAlive || row.Element?.IsKeyboardFocusWithin == true))
        {
            desired.Add(row);
        }

        return desired;
    }

    private void RealizeDesiredRows(HashSet<ConversationRow> desired)
    {
        foreach (var row in rows)
        {
            if (desired.Contains(row))
            {
                Realize(row);
            }
            else if (row.Element is not null && !row.KeepAlive && !row.Element.IsKeyboardFocusWithin)
            {
                Unrealize(row, preserveViewState: true);
            }
        }
    }

    private void Realize(ConversationRow row)
    {
        if (row.Element is not null)
        {
            return;
        }

        var element = row.Factory()
            ?? throw new InvalidOperationException("A conversation row factory returned null.");
        row.Element = element;
        var visualIndex = rows
            .TakeWhile(candidate => !ReferenceEquals(candidate, row))
            .Count(candidate => candidate.Element is not null);
        InternalChildren.Insert(visualIndex, element);
        elementCreationCount++;
        RestoreViewState(element, row.ViewState);
    }

    private void Unrealize(ConversationRow row, bool preserveViewState)
    {
        if (row.Element is not UIElement element)
        {
            return;
        }

        if (preserveViewState)
        {
            row.ViewState = CaptureViewState(element);
        }
        else
        {
            row.ViewState = ConversationRowViewState.Empty;
        }

        InternalChildren.Remove(element);
        row.Element = null;
    }

    private void RefreshRealizedRowsCore()
    {
        foreach (var row in rows.Where(row => row.Element is not null).ToArray())
        {
            var hadFocus = row.Element!.IsKeyboardFocusWithin;
            row.ViewState = CaptureViewState(row.Element, captureFocus: hadFocus);
            InternalChildren.Remove(row.Element);
            row.Element = null;
            Realize(row);
        }
    }

    private void ResetMeasuredHeightsForWidth(double width)
    {
        lastMeasureWidth = width;
        foreach (var row in rows)
        {
            row.HasMeasuredHeight = false;
            row.Height = row.EstimatedHeight;
        }
    }

    private double ResolveMeasureWidth(double availableWidth)
    {
        if (double.IsFinite(availableWidth))
        {
            return Math.Max(0d, availableWidth);
        }

        if (scrollOwner is { ViewportWidth: > 0d } owner)
        {
            return owner.ViewportWidth;
        }

        return Math.Max(0d, ActualWidth);
    }

    private ConversationAnchor CaptureAnchor()
    {
        if (rows.Count == 0 || scrollOwner is null)
        {
            return ConversationAnchor.Empty;
        }

        var offset = Math.Max(0d, scrollOwner.VerticalOffset);
        var top = 0d;
        foreach (var row in rows)
        {
            var bottom = top + row.Height;
            if (bottom > offset || ReferenceEquals(row, rows[^1]))
            {
                return new ConversationAnchor(row.Key, offset - top);
            }

            top = bottom;
        }

        return ConversationAnchor.Empty;
    }

    private void RestoreAnchor(ConversationAnchor anchor)
    {
        if (anchor.IsEmpty || scrollOwner is null || pendingScrollToEnd)
        {
            return;
        }

        var top = RowTop(anchor.Key!);
        if (top is null)
        {
            return;
        }

        ScheduleAnchorCorrection(Math.Max(0d, top.Value + anchor.OffsetWithinRow));
    }

    private void CorrectAnchorAfterMeasure(ConversationAnchor anchor)
    {
        RestoreAnchor(anchor);
    }

    private double? RowTop(object key)
    {
        var top = 0d;
        foreach (var row in rows)
        {
            if (Equals(row.Key, key))
            {
                return top;
            }

            top += row.Height;
        }

        return null;
    }

    private void ScheduleAnchorCorrection(double target)
    {
        if (scrollOwner is null || pendingAnchorCorrection || Math.Abs(scrollOwner.VerticalOffset - target) <= 0.5d)
        {
            return;
        }

        pendingAnchorCorrection = true;
        Dispatcher.BeginInvoke(() =>
        {
            pendingAnchorCorrection = false;
            if (!pendingScrollToEnd)
            {
                scrollOwner?.ScrollToVerticalOffset(target);
            }
        }, DispatcherPriority.Loaded);
    }

    private void ApplyPendingScrollToEnd()
    {
        pendingScrollOperationScheduled = false;
        if (!pendingScrollToEnd)
        {
            return;
        }

        if (scrollOwner is null)
        {
            return;
        }

        pendingScrollToEnd = false;
        pendingNewMessageCount = 0;
        pendingAutoFollowMessageCount = 0;
        awaitingUserNavigation = false;
        UpdateNewMessagesPresentation();
        scrollOwner.ScrollToEnd();
        InvalidateMeasure();
    }

    private void ScheduleScrollToEnd()
    {
        pendingScrollToEnd = true;
        InvalidateMeasure();
        if (pendingScrollOperationScheduled)
        {
            return;
        }

        pendingScrollOperationScheduled = true;
        Dispatcher.BeginInvoke(ApplyPendingScrollToEnd, DispatcherPriority.Loaded);
    }

    private void CancelPendingFollowLatest(bool expectViewportChange = false)
    {
        if (expectViewportChange && ScrollOwnerIsNearBottom())
        {
            awaitingUserNavigation = true;
            ScheduleUserNavigationCompletion();
        }

        if (pendingScrollToEnd && pendingAutoFollowMessageCount > 0)
        {
            pendingNewMessageCount = (int)Math.Min(
                int.MaxValue,
                (long)pendingNewMessageCount + pendingAutoFollowMessageCount);
            pendingAutoFollowMessageCount = 0;
            UpdateNewMessagesPresentation();
        }

        pendingScrollToEnd = false;
    }

    private void ScheduleUserNavigationCompletion()
    {
        if (userNavigationCompletionScheduled)
        {
            return;
        }

        userNavigationCompletionScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            userNavigationCompletionScheduled = false;
            if (!awaitingUserNavigation)
            {
                return;
            }

            awaitingUserNavigation = false;
            if (ScrollOwnerIsNearBottom() && pendingNewMessageCount > 0)
            {
                pendingNewMessageCount = 0;
                UpdateNewMessagesPresentation();
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void AddPendingAutoFollowMessages(int count)
    {
        if (count <= 0)
        {
            return;
        }

        pendingAutoFollowMessageCount = (int)Math.Min(
            int.MaxValue,
            (long)pendingAutoFollowMessageCount + count);
    }

    private bool ScrollOwnerIsNearBottom()
    {
        return scrollOwner is null
            || IsNearBottom(
                scrollOwner.VerticalOffset,
                scrollOwner.ViewportHeight,
                scrollOwner.ExtentHeight);
    }

    internal static bool IsNearBottom(double verticalOffset, double viewportHeight, double extentHeight)
    {
        if (!double.IsFinite(verticalOffset)
            || !double.IsFinite(viewportHeight)
            || !double.IsFinite(extentHeight)
            || viewportHeight <= 0d
            || extentHeight <= viewportHeight)
        {
            return true;
        }

        var distance = extentHeight - Math.Max(0d, verticalOffset) - viewportHeight;
        return distance <= FollowLatestThreshold;
    }

    private void EnsureNewMessagesAdorner()
    {
        if (newMessagesAdorner is not null || scrollOwner is null || !IsLoaded)
        {
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(scrollOwner);
        if (layer is null)
        {
            return;
        }

        newMessagesAdornerLayer = layer;
        newMessagesAdorner = new ConversationNewMessagesAdorner(scrollOwner, JumpToLatest);
        newMessagesAdornerLayer.Add(newMessagesAdorner);
    }

    private void RemoveNewMessagesAdorner()
    {
        if (newMessagesAdorner is null)
        {
            return;
        }

        newMessagesAdornerLayer?.Remove(newMessagesAdorner);
        newMessagesAdorner = null;
        newMessagesAdornerLayer = null;
    }

    private void UpdateNewMessagesPresentation()
    {
        EnsureNewMessagesAdorner();
        newMessagesAdorner?.UpdateCount(pendingNewMessageCount);
        if (presentedNewMessageCount == pendingNewMessageCount)
        {
            return;
        }

        presentedNewMessageCount = pendingNewMessageCount;
        AutomationProperties.SetItemStatus(
            this,
            pendingNewMessageCount == 0
                ? "Following latest messages."
                : NewMessagesLabel(pendingNewMessageCount));
        if (UIElementAutomationPeer.FromElement(this) is VirtualizingConversationPanelAutomationPeer peer)
        {
            peer.RaiseLiveRegionChanged();
        }
    }

    private static string NewMessagesLabel(int count)
    {
        return $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} new message{(count == 1 ? "" : "s")}. Jump to latest.";
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static ScrollViewer? FindScrollOwner(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static double NormalizeEstimate(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumEstimatedRowHeight, MaximumEstimatedRowHeight)
            : DefaultEstimatedRowHeight;
    }

    private static double NormalizeMeasuredHeight(double value, double estimatedHeight)
    {
        // Estimates stay bounded because they drive realization before an
        // element exists. Once WPF produces a finite desired height, preserve
        // it so arrange, extent, hit testing, and UIA share the same geometry.
        return double.IsFinite(value) && value >= 0d
            ? value
            : NormalizeEstimate(estimatedHeight);
    }

    private static double NormalizeOffset(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;

    private static double NormalizeViewportHeight(double value) =>
        double.IsFinite(value) && value > 0d ? value : 640d;

    private static bool Intersects(double top, double bottom, double viewportStart, double viewportEnd) =>
        bottom > viewportStart && top < viewportEnd;

    private static ConversationRowViewState CaptureViewState(UIElement root, bool captureFocus = false)
    {
        var expanders = new Dictionary<string, bool>(StringComparer.Ordinal);
        var textBoxes = new Dictionary<string, TextSelectionState>(StringComparer.Ordinal);
        string? focusPath = null;
        Traverse(root, "0", element =>
        {
            switch (element.Element)
            {
                case Expander expander:
                    expanders[element.Path] = expander.IsExpanded;
                    break;
                case TextBox textBox:
                    textBoxes[element.Path] = new TextSelectionState(
                        textBox.SelectionStart,
                        textBox.SelectionLength,
                        textBox.CaretIndex,
                        textBox.VerticalOffset,
                        textBox.HorizontalOffset);
                    break;
            }

            if ((captureFocus || root.IsKeyboardFocusWithin) && ReferenceEquals(Keyboard.FocusedElement, element.Element))
            {
                focusPath = element.Path;
            }
        });
        return new ConversationRowViewState(expanders, textBoxes, focusPath);
    }

    private static void RestoreViewState(UIElement root, ConversationRowViewState state)
    {
        if (state.IsEmpty)
        {
            return;
        }

        IInputElement? focusTarget = null;
        Traverse(root, "0", element =>
        {
            if (element.Element is Expander expander
                && state.Expanders.TryGetValue(element.Path, out var expanded))
            {
                expander.IsExpanded = expanded;
            }

            if (element.Element is TextBox textBox
                && state.TextBoxes.TryGetValue(element.Path, out var selection))
            {
                var start = Math.Clamp(selection.SelectionStart, 0, textBox.Text.Length);
                var length = Math.Clamp(selection.SelectionLength, 0, textBox.Text.Length - start);
                textBox.Select(start, length);
                if (length == 0)
                {
                    textBox.CaretIndex = Math.Clamp(selection.CaretIndex, 0, textBox.Text.Length);
                }
                textBox.ScrollToVerticalOffset(selection.VerticalOffset);
                textBox.ScrollToHorizontalOffset(selection.HorizontalOffset);
            }

            if (string.Equals(state.FocusPath, element.Path, StringComparison.Ordinal))
            {
                focusTarget = element.Element as IInputElement;
            }
        });

        focusTarget?.Focus();
    }

    private static void Traverse(
        DependencyObject root,
        string path,
        Action<(DependencyObject Element, string Path)> visitor)
    {
        visitor((root, path));
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            Traverse(VisualTreeHelper.GetChild(root, index), $"{path}.{index}", visitor);
        }
    }

    internal sealed class ConversationRowHandle
    {
        private readonly VirtualizingConversationPanel owner;
        private ConversationRow? row;

        internal ConversationRowHandle(VirtualizingConversationPanel owner, ConversationRow row)
        {
            this.owner = owner;
            this.row = row;
        }

        internal UIElement? Element => row?.Element;

        internal object? Key => row?.Key;

        internal void UpdateFactory(Func<UIElement> factory, bool keepAlive)
        {
            ArgumentNullException.ThrowIfNull(factory);
            owner.VerifyAccess();
            if (row is null)
            {
                return;
            }

            row.Factory = factory;
            row.KeepAlive = keepAlive;
            owner.InvalidateMeasure();
        }

        internal void Remove()
        {
            owner.VerifyAccess();
            if (row is null)
            {
                return;
            }

            var anchor = owner.CaptureAnchor();
            owner.Unrealize(row, preserveViewState: false);
            owner.rows.Remove(row);
            row = null;
            owner.RestoreAnchor(anchor);
            owner.InvalidateMeasure();
        }
    }

    internal sealed record ConversationRowDefinition(
        object Key,
        Func<UIElement> Factory,
        double EstimatedHeight = DefaultEstimatedRowHeight,
        bool KeepAlive = false,
        string? AutomationName = null,
        string? AutomationHelpText = null);

    internal readonly record struct ConversationVirtualizationReceipt(
        int LogicalRows,
        int RealizedRows,
        int PeakRealizedRows,
        int ElementCreations,
        double EstimatedExtentHeight);

    internal sealed class ConversationRow
    {
        internal ConversationRow(
            object key,
            Func<UIElement> factory,
            double estimatedHeight,
            bool keepAlive,
            string? automationName,
            string? automationHelpText)
        {
            Key = key;
            Factory = factory;
            EstimatedHeight = estimatedHeight;
            Height = estimatedHeight;
            KeepAlive = keepAlive;
            AutomationName = string.IsNullOrWhiteSpace(automationName) ? key.ToString() ?? "Conversation message" : automationName;
            AutomationHelpText = automationHelpText ?? "";
        }

        internal object Key { get; }
        internal Func<UIElement> Factory { get; set; }
        internal double EstimatedHeight { get; }
        internal double Height { get; set; }
        internal bool HasMeasuredHeight { get; set; }
        internal bool KeepAlive { get; set; }
        internal UIElement? Element { get; set; }
        internal ConversationRowViewState ViewState { get; set; } = ConversationRowViewState.Empty;
        internal string AutomationName { get; }
        internal string AutomationHelpText { get; }
        internal VirtualConversationRowAutomationPeer? AutomationPeer { get; set; }
    }

    private sealed class VirtualizingConversationPanelAutomationPeer(VirtualizingConversationPanel owner)
        : FrameworkElementAutomationPeer(owner)
    {
        internal void RaiseLiveRegionChanged()
        {
            RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        protected override string GetClassNameCore() => nameof(VirtualizingConversationPanel);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override List<AutomationPeer> GetChildrenCore()
        {
            return owner.rows
                .Select(row => (AutomationPeer)(row.AutomationPeer ??= new VirtualConversationRowAutomationPeer(owner, row)))
                .ToList();
        }
    }

    internal sealed class VirtualConversationRowAutomationPeer(
        VirtualizingConversationPanel owner,
        ConversationRow row)
        : AutomationPeer, IVirtualizedItemProvider, IScrollItemProvider
    {
        protected override string GetClassNameCore() => "ConversationMessage";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetNameCore() => row.AutomationName;

        protected override string GetHelpTextCore() => row.AutomationHelpText;

        protected override string GetAutomationIdCore() => row.Key.ToString() ?? "conversation-message";

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        protected override bool IsEnabledCore() => owner.IsEnabled;

        protected override bool HasKeyboardFocusCore() => row.Element?.IsKeyboardFocusWithin == true;

        protected override bool IsKeyboardFocusableCore() => row.Element is UIElement element
            && (element.Focusable || FindFirstFocusableDescendant(element) is not null);

        protected override string GetAcceleratorKeyCore() => "";

        protected override string GetAccessKeyCore() => "";

        protected override Point GetClickablePointCore() => new(double.NaN, double.NaN);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            if (row.Element is null)
            {
                return null;
            }

            var peers = new List<AutomationPeer>();
            CollectAutomationChildren(row.Element, peers);
            return peers.Count == 0 ? null : peers;
        }

        private static void CollectAutomationChildren(DependencyObject parent, List<AutomationPeer> peers)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is UIElement element
                    && UIElementAutomationPeer.CreatePeerForElement(element) is { } peer)
                {
                    peers.Add(peer);
                    continue;
                }

                CollectAutomationChildren(child, peers);
            }
        }

        protected override string GetItemStatusCore() => "";

        protected override string GetItemTypeCore() => "Conversation message";

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Vertical;

        protected override bool IsOffscreenCore()
        {
            if (row.Element is not UIElement element
                || element.Visibility != Visibility.Visible
                || !element.IsVisible
                || !element.IsArrangeValid
                || element.RenderSize.Width <= 0d
                || element.RenderSize.Height <= 0d
                || owner.scrollOwner is not ScrollViewer viewer
                || viewer.Visibility != Visibility.Visible
                || !viewer.IsVisible
                || PresentationSource.FromVisual(element) is null)
            {
                return true;
            }

            var viewport = FindViewportPresenter(viewer);
            if (viewport is null
                || viewport.Visibility != Visibility.Visible
                || !viewport.IsVisible
                || !viewport.IsArrangeValid
                || viewport.RenderSize.Width <= 0d
                || viewport.RenderSize.Height <= 0d
                || !ReferenceEquals(
                    PresentationSource.FromVisual(element),
                    PresentationSource.FromVisual(viewport)))
            {
                return true;
            }

            try
            {
                var bounds = element
                    .TransformToAncestor(viewport)
                    .TransformBounds(new Rect(new Point(), element.RenderSize));
                return bounds.IsEmpty
                    || bounds.Right <= 0d
                    || bounds.Bottom <= 0d
                    || bounds.Left >= viewport.RenderSize.Width
                    || bounds.Top >= viewport.RenderSize.Height;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static ScrollContentPresenter? FindViewportPresenter(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is ScrollContentPresenter presenter)
                {
                    return presenter;
                }

                if (FindViewportPresenter(child) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }

        protected override bool IsPasswordCore() => false;

        protected override bool IsRequiredForFormCore() => false;

        protected override void SetFocusCore()
        {
            Realize();
            if (row.Element is not UIElement element)
            {
                return;
            }

            var focusTarget = element.Focusable && element.IsEnabled && element.Visibility == Visibility.Visible
                ? element
                : FindFirstFocusableDescendant(element);
            focusTarget?.Focus();
        }

        private static UIElement? FindFirstFocusableDescendant(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is UIElement { Focusable: true, IsEnabled: true, Visibility: Visibility.Visible } element)
                {
                    return element;
                }

                if (FindFirstFocusableDescendant(child) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }

        protected override Rect GetBoundingRectangleCore()
        {
            if (row.Element is not UIElement element || !element.IsArrangeValid)
            {
                return Rect.Empty;
            }

            try
            {
                var topLeft = element.PointToScreen(new Point(0d, 0d));
                return new Rect(topLeft, element.RenderSize);
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;
            }
        }

        public override object? GetPattern(PatternInterface patternInterface)
        {
            return patternInterface is PatternInterface.VirtualizedItem or PatternInterface.ScrollItem
                ? this
                : null;
        }

        public void Realize() => ScrollIntoView();

        public void ScrollIntoView()
        {
            void BringIntoView()
            {
                owner.CancelPendingFollowLatest();
                var top = owner.RowTop(row.Key);
                if (top is null)
                {
                    return;
                }

                owner.Realize(row);
                owner.scrollOwner?.ScrollToVerticalOffset(top.Value);
                owner.InvalidateMeasure();
            }

            if (owner.Dispatcher.CheckAccess())
            {
                BringIntoView();
            }
            else
            {
                owner.Dispatcher.Invoke(BringIntoView);
            }
        }
    }

    internal sealed record ConversationRowViewState(
        IReadOnlyDictionary<string, bool> Expanders,
        IReadOnlyDictionary<string, TextSelectionState> TextBoxes,
        string? FocusPath)
    {
        internal static readonly ConversationRowViewState Empty = new(
            new Dictionary<string, bool>(),
            new Dictionary<string, TextSelectionState>(),
            null);

        internal bool IsEmpty => Expanders.Count == 0 && TextBoxes.Count == 0 && FocusPath is null;
    }

    internal readonly record struct TextSelectionState(
        int SelectionStart,
        int SelectionLength,
        int CaretIndex,
        double VerticalOffset,
        double HorizontalOffset);

    private readonly record struct ConversationAnchor(object? Key, double OffsetWithinRow)
    {
        internal static readonly ConversationAnchor Empty = new(null, 0d);
        internal bool IsEmpty => Key is null;
    }

    private sealed class ConversationNewMessagesAdorner : Adorner
    {
        private readonly VisualCollection visuals;

        internal ConversationNewMessagesAdorner(UIElement adornedElement, Action jumpToLatest)
            : base(adornedElement)
        {
            JumpButton = new Button
            {
                MinHeight = 36d,
                MinWidth = 168d,
                Padding = new Thickness(12d, 6d, 12d, 6d),
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed,
                ToolTip = "Jump to the newest conversation message and resume following live updates."
            };
            JumpButton.SetResourceReference(FrameworkElement.StyleProperty, "Arena.Button.Assist");
            AutomationProperties.SetLiveSetting(JumpButton, AutomationLiveSetting.Polite);
            AutomationProperties.SetHelpText(
                JumpButton,
                "Moves to the newest conversation message and resumes following live updates.");
            JumpButton.Click += (_, _) => jumpToLatest();
            visuals = new VisualCollection(this) { JumpButton };
        }

        internal Button JumpButton { get; }

        internal void UpdateCount(int count)
        {
            if (count <= 0)
            {
                JumpButton.Visibility = Visibility.Collapsed;
                return;
            }

            var label = NewMessagesLabel(count);
            JumpButton.Content = $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} new message{(count == 1 ? "" : "s")} · Jump to latest";
            AutomationProperties.SetName(JumpButton, label);
            AutomationProperties.SetItemStatus(JumpButton, label);
            JumpButton.Visibility = Visibility.Visible;
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override int VisualChildrenCount => visuals.Count;

        protected override Visual GetVisualChild(int index) => visuals[index];

        protected override Size MeasureOverride(Size constraint)
        {
            // AdornerLayer measures every adorner with the size of the entire
            // layer. Size this adorner to the adorned ScrollViewer instead so
            // its bottom-right placement remains relative to the conversation
            // viewport when the viewport is offset or layout-scaled.
            var viewportSize = AdornedElement.RenderSize;
            JumpButton.Measure(new Size(
                Math.Max(0d, viewportSize.Width - 32d),
                Math.Max(0d, viewportSize.Height - 24d)));
            return viewportSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var desired = JumpButton.DesiredSize;
            var width = Math.Min(desired.Width, Math.Max(0d, finalSize.Width - 32d));
            var height = Math.Min(desired.Height, Math.Max(0d, finalSize.Height - 24d));
            JumpButton.Arrange(new Rect(
                Math.Max(16d, finalSize.Width - width - 18d),
                Math.Max(12d, finalSize.Height - height - 14d),
                width,
                height));
            return finalSize;
        }

        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return null;
        }
    }
}
