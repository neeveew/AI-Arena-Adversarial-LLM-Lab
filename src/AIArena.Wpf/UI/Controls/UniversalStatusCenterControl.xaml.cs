using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Presents the application-wide status projection as four stable rail rows and
/// a current-run history dashboard. Domain truth remains owned by the feature
/// that published each status; this control only presents and navigates it.
/// </summary>
public partial class UniversalStatusCenterControl : UserControl, INotifyPropertyChanged
{
    internal const int CompactRowCount = 4;
    internal const double DashboardWidth = 380;
    internal static readonly TimeSpan MinimumClockInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaximumClockInterval = TimeSpan.FromDays(1);

    private readonly DispatcherTimer relativeTimeTimer;
    private UniversalStatusRowPresentation? selectedHistoryRow;
    private string activeFilter = "All";
    private IInputElement? focusReturnTarget;
    private string? focusReturnStatusId;
    private Action<string?>? navigationRequested;
    private Action? clearCompletedRequested;
    private ApplicationStatusCenter? boundStatusCenter;
    private string primaryStateText = "Ready";
    private string primarySummary = "Ready";
    private string activeCountLabel = "Ready";
    private string historyCountLabel = "No recent activity";
    private bool canClearCompleted;
    private int clockWakeCount;
    private int compactClockRefreshCount;
    private int historyClockRefreshCount;

    public UniversalStatusCenterControl()
    {
        InitializeComponent();
        EnsureFourCompactRows();
        SelectedHistoryRow = CompactRows[0];
        relativeTimeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        relativeTimeTimer.Tick += (_, _) => RefreshStatusCenterClock();
        Loaded += (_, _) => ScheduleNextClockWake();
        Unloaded += (_, _) => relativeTimeTimer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the compact or expanded dashboard state changes.</summary>
    public event EventHandler? PresentationChanged;

    internal ObservableCollection<UniversalStatusRowPresentation> CompactRows { get; } = [];
    internal ObservableCollection<UniversalStatusRowPresentation> HistoryRows { get; } = [];
    internal ObservableCollection<UniversalStatusRowPresentation> FilteredHistoryRows { get; } = [];

    // WPF resolves ElementName bindings against public CLR properties. Keep the
    // strongly typed collections internal for the hosted seams, and expose only
    // read-only, non-generic projections to the presentation layer.
    public IEnumerable CompactRowsBinding => CompactRows;
    public IEnumerable FilteredHistoryRowsBinding => FilteredHistoryRows;
    public object? SelectedHistoryRowBinding => selectedHistoryRow;

    internal UniversalStatusRowPresentation? SelectedHistoryRow
    {
        get => selectedHistoryRow;
        private set
        {
            if (ReferenceEquals(selectedHistoryRow, value))
            {
                return;
            }

            selectedHistoryRow = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedHistoryRow)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedHistoryRowBinding)));
        }
    }

    public string PrimaryStateText
    {
        get => primaryStateText;
        private set => SetField(ref primaryStateText, value);
    }

    public string PrimarySummary
    {
        get => primarySummary;
        private set => SetField(ref primarySummary, value);
    }

    public string ActiveCountLabel
    {
        get => activeCountLabel;
        private set => SetField(ref activeCountLabel, value);
    }

    public string HistoryCountLabel
    {
        get => historyCountLabel;
        private set => SetField(ref historyCountLabel, value);
    }

    public bool CanClearCompleted
    {
        get => canClearCompleted;
        private set => SetField(ref canClearCompleted, value);
    }

    public bool IsDashboardOpen => HistoryPopup.IsOpen;

    public double DashboardMaximumHeight => Math.Max(
        320,
        Math.Min(720, SystemParameters.WorkArea.Height - 32));

    internal ListBox HistoryListTarget => HistoryList;
    internal ItemsControl CompactStatusItemsTarget => CompactStatusItems;
    internal Border StatusCenterCardTarget => StatusCenterCard;
    internal Popup HistoryPopupTarget => HistoryPopup;
    internal Button OpenHistoryButtonTarget => OpenHistoryButton;
    internal Button CloseHistoryButtonTarget => CloseHistoryButton;
    internal int ClockWakeCount => clockWakeCount;
    internal int CompactClockRefreshCount => compactClockRefreshCount;
    internal int HistoryClockRefreshCount => historyClockRefreshCount;
    internal bool IsClockScheduled => relativeTimeTimer.IsEnabled;
    internal TimeSpan ScheduledClockInterval => relativeTimeTimer.Interval;
    internal Button ClearCompletedButtonTarget => ClearCompletedButton;
    internal Button NavigateStatusButtonTarget => NavigateStatusButton;
    internal TextBlock LiveAnnouncementTarget => LiveAnnouncementText;
    internal IReadOnlyList<RadioButton> HistoryFilterButtons =>
        [AllFilterButton, RunningFilterButton, WarningsFilterButton, FailedFilterButton];

    /// <summary>
    /// Binds this presentation surface to the canonical process-only status center.
    /// Rebinding detaches the previous source and never creates a second history.
    /// </summary>
    internal void Bind(ApplicationStatusCenter statusCenter)
    {
        ArgumentNullException.ThrowIfNull(statusCenter);
        if (ReferenceEquals(boundStatusCenter, statusCenter))
        {
            return;
        }

        if (boundStatusCenter is not null)
        {
            boundStatusCenter.Changed -= StatusCenter_Changed;
        }

        boundStatusCenter = statusCenter;
        boundStatusCenter.Changed += StatusCenter_Changed;
        ApplySnapshot(boundStatusCenter.Snapshot);
    }

    /// <summary>
    /// Applies a sanitized projection from the shared status service. The service
    /// remains authoritative and supplies clear/navigation callbacks.
    /// </summary>
    internal void ApplyPresentation(
        IReadOnlyList<UniversalStatusRowPresentation> history,
        Action? clearCompleted = null,
        Action<string?>? navigate = null)
    {
        ApplyPresentation(history, history.Take(CompactRowCount).ToArray(), clearCompleted, navigate);
    }

    internal void ApplyPresentation(
        IReadOnlyList<UniversalStatusRowPresentation> history,
        IReadOnlyList<UniversalStatusRowPresentation> visibleEntries,
        Action? clearCompleted = null,
        Action<string?>? navigate = null,
        UniversalStatusRowPresentation? primaryEntry = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(visibleEntries);
        clearCompletedRequested = clearCompleted ?? clearCompletedRequested;
        navigationRequested = navigate ?? navigationRequested;

        var selectedId = SelectedHistoryRow?.Id;
        ReplaceRows(HistoryRows, history);
        ApplyFilter(activeFilter, preserveSelectionId: selectedId);
        ReplaceRows(CompactRows, visibleEntries.Take(CompactRowCount));
        EnsureFourCompactRows();

        var primary = primaryEntry
            ?? CompactRows.FirstOrDefault(row => !row.IsPlaceholder)
            ?? CompactRows[0];
        PrimaryStateText = primary.StateText;
        PrimarySummary = primary.Summary;
        var activeCount = history.Count(row => row.IsActive || row.IsUnresolved);
        ActiveCountLabel = activeCount > 0 ? $"{activeCount} active" : "Ready";
        HistoryCountLabel = history.Count switch
        {
            0 => "No recent activity",
            1 => "1 current-run update",
            _ => $"{history.Count} current-run updates"
        };
        CanClearCompleted = history.Any(row => row.CanClear);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        ScheduleNextClockWake();
    }

    private void StatusCenter_Changed(object? sender, ApplicationStatusChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => StatusCenter_Changed(sender, e),
                DispatcherPriority.DataBind);
            return;
        }

        ApplySnapshot(e.Snapshot);
        if (!e.ExpiredOnly
            && e.AnnouncementKind != ApplicationStatusAnnouncement.None
            && !string.IsNullOrWhiteSpace(e.Announcement))
        {
            Announce(
                e.Announcement,
                e.AnnouncementKind == ApplicationStatusAnnouncement.Assertive);
        }
    }

    private void ApplySnapshot(ApplicationStatusSnapshot snapshot)
    {
        var history = snapshot.History.Select(ToPresentation).ToArray();
        var visible = snapshot.VisibleEntries.Select(ToPresentation).ToArray();
        var primary = ToPresentation(snapshot.Primary);
        ApplyPresentation(
            history,
            visible,
            () => boundStatusCenter?.ClearCompleted(),
            target => boundStatusCenter?.RequestNavigation(target),
            primary);
    }

    private static UniversalStatusRowPresentation ToPresentation(ApplicationStatusEntry entry) => new(
        entry.Id,
        entry.Source,
        entry.State.ToString(),
        StateText(entry.State),
        StateIcon(entry.State),
        entry.Summary,
        entry.Detail,
        entry.UpdatedAt,
        entry.Progress,
        entry.NavigationTarget,
        entry.IsActive,
        entry.IsUnresolved,
        entry.CanClear);

    internal static string StateText(ApplicationStatusState state) => state switch
    {
        ApplicationStatusState.Running => "Running",
        ApplicationStatusState.Succeeded => "Succeeded",
        ApplicationStatusState.Info => "Information",
        ApplicationStatusState.Warning => "Warning",
        ApplicationStatusState.Failed => "Failed",
        ApplicationStatusState.Cancelled => "Cancelled",
        ApplicationStatusState.Unconfirmed => "Unconfirmed",
        ApplicationStatusState.Blocked => "Blocked",
        _ => "Information"
    };

    internal static string StateIcon(ApplicationStatusState state) => state switch
    {
        ApplicationStatusState.Running => "",
        ApplicationStatusState.Succeeded => "",
        ApplicationStatusState.Warning => "",
        ApplicationStatusState.Failed => "",
        ApplicationStatusState.Cancelled => "",
        ApplicationStatusState.Unconfirmed => "",
        ApplicationStatusState.Blocked => "",
        _ => ""
    };

    internal void Announce(string? text, bool assertive)
    {
        var normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0
            || normalized.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ready.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AutomationProperties.SetLiveSetting(
            LiveAnnouncementText,
            assertive ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var repeated = LiveAnnouncementText.Text.Equals(normalized, StringComparison.Ordinal);
        if (repeated)
        {
            var peer = UIElementAutomationPeer.FromElement(LiveAnnouncementText)
                ?? new TextBlockAutomationPeer(LiveAnnouncementText);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            return;
        }

        LiveAnnouncementText.Text = normalized;
    }

    public void OpenDashboard(IInputElement? invoker = null, string? selectedId = null)
    {
        focusReturnTarget = invoker ?? Keyboard.FocusedElement ?? OpenHistoryButton;
        focusReturnStatusId = invoker is FrameworkElement { Tag: string id } ? id : null;
        HistoryPopup.PlacementTarget = invoker as UIElement ?? StatusCenterCard;
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            AllFilterButton.IsChecked = true;
            ApplyFilter("All", preserveSelectionId: null);
        }

        SelectHistoryEntry(selectedId);
        HistoryPopup.IsOpen = true;
    }

    public void CloseDashboard(bool restoreFocus = true)
    {
        if (!HistoryPopup.IsOpen)
        {
            return;
        }

        if (!restoreFocus)
        {
            focusReturnTarget = null;
            focusReturnStatusId = null;
        }

        HistoryPopup.IsOpen = false;
    }

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
        => OpenCompleteHistory((IInputElement)sender);

    private void StatusCenterCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var compactContainer = e.OriginalSource is DependencyObject originalSource
            ? ItemsControl.ContainerFromElement(CompactStatusItems, originalSource) as FrameworkElement
            : null;
        if (compactContainer?.DataContext is UniversalStatusRowPresentation { IsInteractive: true })
        {
            return;
        }

        e.Handled = true;
        OpenCompleteHistory(OpenHistoryButton);
    }

    private void OpenCompleteHistory(IInputElement invoker)
    {
        AllFilterButton.IsChecked = true;
        ApplyFilter("All", preserveSelectionId: null);
        OpenDashboard(invoker);
    }

    private void CompactStatusRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } button)
        {
            OpenDashboard(button, id);
        }
    }

    private void CloseHistoryButton_Click(object sender, RoutedEventArgs e) => CloseDashboard();

    private void HistoryPopup_Opened(object? sender, EventArgs e)
    {
        RefreshRelativeTimes(includeHistory: true);
        ScheduleNextClockWake();
        HistoryDashboardTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        Dispatcher.BeginInvoke(() =>
        {
            if (HistoryPopup.IsOpen)
            {
                if (HistoryList.SelectedItem is not null)
                {
                    HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                    HistoryList.Focus();
                }
                else
                {
                    CloseHistoryButton.Focus();
                }
            }
        }, DispatcherPriority.Input);
    }

    private void HistoryPopup_Closed(object? sender, EventArgs e)
    {
        HistoryDashboardTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            null);
        HistoryDashboardTransform.X = 24;
        var target = ResolveFocusReturnTarget();
        focusReturnTarget = null;
        focusReturnStatusId = null;
        HistoryPopup.PlacementTarget = StatusCenterCard;
        ScheduleNextClockWake();
        if (target is not UIElement element)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!HistoryPopup.IsOpen
                && element is { IsVisible: true, IsEnabled: true, Focusable: true })
            {
                element.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void HistoryDashboard_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        CloseDashboard();
        e.Handled = true;
    }

    private void HistoryFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton { IsChecked: true, Tag: string filter })
        {
            return;
        }

        ApplyFilter(filter, SelectedHistoryRow?.Id);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is UniversalStatusRowPresentation row)
        {
            SelectedHistoryRow = row;
        }
    }

    private void NavigateStatusButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedHistoryRow?.NavigationTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        CloseDashboard(restoreFocus: false);
        navigationRequested?.Invoke(target);
    }

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e) =>
        clearCompletedRequested?.Invoke();

    private void ApplyFilter(string filter, string? preserveSelectionId)
    {
        activeFilter = NormalizeFilter(filter);
        var filtered = HistoryRows.Where(MatchesActiveFilter).ToArray();
        ReplaceRows(FilteredHistoryRows, filtered);
        SelectedHistoryRow = FilteredHistoryRows.FirstOrDefault(row =>
                row.Id.Equals(preserveSelectionId, StringComparison.Ordinal))
            ?? FilteredHistoryRows.FirstOrDefault()
            ?? CompactRows.FirstOrDefault(row => !row.IsPlaceholder);
        HistoryList.SelectedItem = SelectedHistoryRow is not null
            && FilteredHistoryRows.Contains(SelectedHistoryRow)
                ? SelectedHistoryRow
                : null;
    }

    private bool MatchesActiveFilter(UniversalStatusRowPresentation row) => activeFilter switch
    {
        "Running" => row.IsActive,
        "Warnings" => row.StateKey is "Warning" or "Unconfirmed",
        "Failed" => row.StateKey is "Failed" or "Blocked",
        _ => true
    };

    private static string NormalizeFilter(string? filter) => filter switch
    {
        "Running" => "Running",
        "Warnings" => "Warnings",
        "Failed" => "Failed",
        _ => "All"
    };

    private IInputElement? ResolveFocusReturnTarget()
    {
        if (!string.IsNullOrWhiteSpace(focusReturnStatusId))
        {
            CompactStatusItems.UpdateLayout();
            var row = CompactRows.FirstOrDefault(item =>
                item.Id.Equals(focusReturnStatusId, StringComparison.Ordinal));
            if (row is not null
                && CompactStatusItems.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
                && FindStatusButton(container, focusReturnStatusId) is { } currentButton
                && IsUsableFocusTarget(currentButton))
            {
                return currentButton;
            }
        }

        if (focusReturnTarget is UIElement directTarget && IsUsableFocusTarget(directTarget))
        {
            return directTarget;
        }

        return IsUsableFocusTarget(OpenHistoryButton) ? OpenHistoryButton : null;
    }

    private static bool IsUsableFocusTarget(UIElement target) =>
        target is { IsVisible: true, IsEnabled: true, Focusable: true };

    private static Button? FindStatusButton(DependencyObject parent, string statusId)
    {
        if (parent is Button { Tag: string id } button
            && id.Equals(statusId, StringComparison.Ordinal))
        {
            return button;
        }

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (FindStatusButton(System.Windows.Media.VisualTreeHelper.GetChild(parent, index), statusId) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private void SelectHistoryEntry(string? id)
    {
        var target = string.IsNullOrWhiteSpace(id)
            ? FilteredHistoryRows.FirstOrDefault()
            : FilteredHistoryRows.FirstOrDefault(row => row.Id.Equals(id, StringComparison.Ordinal));
        if (target is null && !string.IsNullOrWhiteSpace(id))
        {
            AllFilterButton.IsChecked = true;
            ApplyFilter("All", id);
            target = FilteredHistoryRows.FirstOrDefault(row => row.Id.Equals(id, StringComparison.Ordinal));
        }

        SelectedHistoryRow = target ?? FilteredHistoryRows.FirstOrDefault();
        HistoryList.SelectedItem = SelectedHistoryRow;
    }

    private void EnsureFourCompactRows()
    {
        if (CompactRows.Count == 0)
        {
            CompactRows.Add(UniversalStatusRowPresentation.Ready());
        }

        while (CompactRows.Count < CompactRowCount)
        {
            CompactRows.Add(UniversalStatusRowPresentation.Placeholder(CompactRows.Count));
        }

        while (CompactRows.Count > CompactRowCount)
        {
            CompactRows.RemoveAt(CompactRows.Count - 1);
        }
    }

    private void RefreshRelativeTimes(bool includeHistory)
    {
        var now = DateTimeOffset.Now;
        foreach (var row in CompactRows)
        {
            row.RefreshRelativeTime(now);
            compactClockRefreshCount++;
        }

        if (!includeHistory)
        {
            return;
        }

        foreach (var row in HistoryRows)
        {
            row.RefreshRelativeTime(now);
            historyClockRefreshCount++;
        }
    }

    internal void RefreshStatusCenterClock()
    {
        relativeTimeTimer.Stop();
        clockWakeCount++;
        boundStatusCenter?.RefreshExpirations();
        RefreshRelativeTimes(IsDashboardOpen);
        ScheduleNextClockWake();
    }

    private void ScheduleNextClockWake()
    {
        relativeTimeTimer.Stop();
        if (!IsLoaded)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var expirationDelay = boundStatusCenter?.TimeUntilNextExpiration;
        DateTimeOffset? next = expirationDelay is null ? null : now + expirationDelay.Value;
        var displayedRows = IsDashboardOpen
            ? CompactRows.Concat(HistoryRows)
            : CompactRows;
        foreach (var row in displayedRows)
        {
            var rowDeadline = UniversalStatusRowPresentation.NextRelativeTimeRefreshAt(row.Timestamp, now);
            if (rowDeadline is not null && (next is null || rowDeadline < next))
            {
                next = rowDeadline;
            }
        }

        if (next is null)
        {
            return;
        }

        var delay = next.Value - now;
        relativeTimeTimer.Interval = delay < MinimumClockInterval
            ? MinimumClockInterval
            : delay > MaximumClockInterval
                ? MaximumClockInterval
                : delay;
        relativeTimeTimer.Start();
    }

    private static void ReplaceRows(
        ObservableCollection<UniversalStatusRowPresentation> target,
        IEnumerable<UniversalStatusRowPresentation> source)
    {
        target.Clear();
        foreach (var row in source)
        {
            target.Add(row);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class UniversalStatusRowPresentation : INotifyPropertyChanged
{
    private string relativeTime;

    internal UniversalStatusRowPresentation(
        string id,
        string source,
        string stateKey,
        string stateText,
        string icon,
        string summary,
        string detail,
        DateTimeOffset timestamp,
        double? progressPercent = null,
        string? navigationTarget = null,
        bool isActive = false,
        bool isUnresolved = false,
        bool canClear = false,
        bool isPlaceholder = false)
    {
        Id = id;
        Source = source;
        StateKey = stateKey;
        StateText = stateText;
        Icon = icon;
        Summary = summary;
        Detail = detail;
        Timestamp = timestamp;
        ProgressPercent = progressPercent ?? 0;
        HasProgress = progressPercent.HasValue;
        NavigationTarget = navigationTarget;
        CanNavigate = !string.IsNullOrWhiteSpace(navigationTarget);
        IsActive = isActive;
        IsUnresolved = isUnresolved;
        CanClear = canClear;
        IsPlaceholder = isPlaceholder;
        IsInteractive = !isPlaceholder;
        relativeTime = FormatRelativeTime(timestamp, DateTimeOffset.Now);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Source { get; }
    public string StateKey { get; }
    public string StateText { get; }
    public string Icon { get; }
    public string Summary { get; }
    public string Detail { get; }
    public DateTimeOffset Timestamp { get; }
    public double ProgressPercent { get; }
    public bool HasProgress { get; }
    public string? NavigationTarget { get; }
    public bool CanNavigate { get; }
    public bool IsActive { get; }
    public bool IsUnresolved { get; }
    public bool CanClear { get; }
    public bool IsPlaceholder { get; }
    public bool IsInteractive { get; }
    public string RelativeTime => relativeTime;
    public string SourceAndState => $"{Source} · {StateText}";
    public string TimestampText => Timestamp == default ? "" : Timestamp.ToLocalTime().ToString("g");
    public string ToolTip => string.IsNullOrWhiteSpace(Detail) ? Summary : $"{Summary}\n{Detail}";
    public string AutomationName => IsPlaceholder
        ? "Unused status slot"
        : $"{Source}, {StateText}: {Summary}, {RelativeTime}";
    public string AutomationHelpText => IsPlaceholder
        ? "No additional current-run status."
        : $"Open status history. {ToolTip}";
    public string ProgressAutomationName => HasProgress
        ? $"{Summary}, {ProgressPercent:0} percent"
        : "No progress value";

    internal void RefreshRelativeTime(DateTimeOffset now)
    {
        var value = FormatRelativeTime(Timestamp, now);
        if (value.Equals(relativeTime, StringComparison.Ordinal))
        {
            return;
        }

        relativeTime = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelativeTime)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
    }

    internal static UniversalStatusRowPresentation Ready() => new(
        "ready",
        "App",
        "Info",
        "Ready",
        "",
        "Ready",
        "No meaningful application activity requires attention.",
        default);

    internal static UniversalStatusRowPresentation Placeholder(int index) => new(
        $"placeholder-{index}",
        "",
        "Info",
        "",
        "",
        "",
        "",
        default,
        isPlaceholder: true);

    internal static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset now)
    {
        if (timestamp == default)
        {
            return string.Empty;
        }

        var elapsed = now - timestamp;
        if (elapsed <= TimeSpan.FromSeconds(5))
        {
            return "now";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalSeconds)}s";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h";
        }

        return timestamp.ToLocalTime().ToString("MMM d");
    }

    internal static DateTimeOffset? NextRelativeTimeRefreshAt(DateTimeOffset timestamp, DateTimeOffset now)
    {
        if (timestamp == default)
        {
            return null;
        }

        var elapsed = now - timestamp;
        if (elapsed <= TimeSpan.FromSeconds(5))
        {
            return timestamp.AddSeconds(5).AddTicks(1);
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return timestamp.AddSeconds(Math.Floor(elapsed.TotalSeconds) + 1);
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return timestamp.AddMinutes(Math.Floor(elapsed.TotalMinutes) + 1);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return timestamp.AddHours(Math.Floor(elapsed.TotalHours) + 1);
        }

        return null;
    }
}
