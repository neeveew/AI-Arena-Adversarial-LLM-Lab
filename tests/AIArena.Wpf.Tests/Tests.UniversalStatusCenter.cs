using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void UniversalStatusCenterKeepsFourStableNewestFirstRows()
    {
        RunStaTest(() =>
        {
            var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);
            var rows = new[]
            {
                StatusRow("newest", "Models", "Running", "Loading Gemma", now, progress: 42),
                StatusRow("second", "Arena", "Succeeded", "Match saved", now.AddSeconds(-8)),
                StatusRow("third", "Provider", "Warning", "Connection unavailable", now.AddSeconds(-24)),
                StatusRow("fourth", "Alpha", "Info", "Assignment moved", now.AddMinutes(-1)),
                StatusRow("oldest", "App", "Info", "Older receipt", now.AddMinutes(-2))
            };
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                control.ApplyPresentation(rows);
                DrainUniversalStatusInput();
                Require(control.Height == 184
                        && control.CompactRows.Count == UniversalStatusCenterControl.CompactRowCount
                        && control.CompactRows.Select(row => row.Id).SequenceEqual(
                            new[] { "newest", "second", "third", "fourth" },
                            StringComparer.Ordinal),
                    "the fixed status card did not reserve exactly four newest-first compact rows");
                Require(control.CompactRows.All(row => row.IsInteractive)
                        && control.CompactRows[0].HasProgress
                        && control.CompactRows[0].ProgressPercent == 42,
                    "the compact projection lost row interactivity or determinate progress");

                control.ApplyPresentation([]);
                DrainUniversalStatusInput();
                Require(control.CompactRows.Count == 4
                        && control.CompactRows[0].Summary == "Ready"
                        && control.CompactRows.Skip(1).All(row => row.IsPlaceholder),
                    "the idle state collapsed the stable four-row footprint or omitted Ready");

                var clock = now;
                var center = new ApplicationStatusCenter(() => clock);
                control.Bind(center);
                var receipt = center.Begin(
                    "models.load",
                    "Models",
                    "Loading Gemma",
                    "LM Studio accepted the load request.",
                    progress: 25,
                    navigationTarget: "models:gemma");
                DrainUniversalStatusInput();
                Require(control.CompactRows[0].Source == "Models"
                        && control.CompactRows[0].Summary == "Loading Gemma"
                        && control.CompactRows[0].ProgressPercent == 25,
                    "binding the canonical center did not project its visible running entry");
                Require(center.Complete(receipt, "Gemma loaded", "LM Studio confirmed residency."),
                    "the canonical status receipt could not complete causally");
                DrainUniversalStatusInput();
                Require(control.CompactRows[0].Summary == "Gemma loaded"
                        && control.LiveAnnouncementTarget.Text.Contains("Gemma loaded", StringComparison.Ordinal),
                    "a canonical completion did not update the compact row and polite live region");
                var announcementBeforeExpiry = control.LiveAnnouncementTarget.Text;
                clock = clock.AddSeconds(7);
                control.RefreshStatusCenterClock();
                DrainUniversalStatusInput();
                Require(control.CompactRows[0].Summary == "Ready"
                        && control.LiveAnnouncementTarget.Text == announcementBeforeExpiry,
                    "silent expiry either left stale compact state or announced routine Ready state");

                clock = clock.AddSeconds(1);
                var blocker = center.PublishNotice(
                    "provider.blocker",
                    "Provider",
                    ApplicationStatusState.Failed,
                    "Provider authentication failed",
                    "Open provider settings to review the credential.");
                clock = clock.AddSeconds(1);
                center.PublishNotice(
                    "export.saved",
                    "Export",
                    ApplicationStatusState.Succeeded,
                    "Transcript exported");
                DrainUniversalStatusInput();
                Require(control.CompactRows[0].Summary == "Transcript exported"
                        && control.PrimaryStateText == "Failed"
                        && control.PrimarySummary == "Provider authentication failed",
                    "the newest-first rows and priority-ranked compact affordance were incorrectly conflated");
                Require(center.Resolve(blocker), "the deterministic blocker could not be resolved causally");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void UniversalStatusCenterClockIsDeadlineDrivenAndBoundsClosedHistoryWork()
    {
        RunStaTest(() =>
        {
            var now = DateTimeOffset.Now;
            Require(
                UniversalStatusRowPresentation.NextRelativeTimeRefreshAt(now, now) is { } nowBoundary
                    && nowBoundary > now.AddSeconds(5)
                    && nowBoundary < now.AddSeconds(6),
                "the relative-time clock did not schedule the first boundary after the canonical now label");
            Require(
                UniversalStatusRowPresentation.NextRelativeTimeRefreshAt(now.AddSeconds(-23.4), now) is { } secondBoundary
                    && secondBoundary > now
                    && secondBoundary <= now.AddSeconds(1),
                "the seconds label did not schedule its next exact display boundary");
            Require(
                UniversalStatusRowPresentation.NextRelativeTimeRefreshAt(now.AddMinutes(-12.25), now) is { } minuteBoundary
                    && minuteBoundary > now
                    && minuteBoundary <= now.AddMinutes(1),
                "the minutes label did not schedule its next exact display boundary");
            Require(
                UniversalStatusRowPresentation.NextRelativeTimeRefreshAt(now.AddDays(-2), now) is null,
                "a date-only relative label scheduled needless periodic work");

            var rows = Enumerable.Range(0, 100)
                .Select(index => StatusRow(
                    $"clock-{index}",
                    "App",
                    "Info",
                    $"Clock row {index}",
                    now.AddMinutes(-(index + 10))))
                .ToArray();
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                DrainUniversalStatusInput();
                Require(!control.IsClockScheduled,
                    "the idle status center scheduled clock work for its synthetic Ready row");

                control.ApplyPresentation(rows);
                DrainUniversalStatusInput();
                var compactBefore = control.CompactClockRefreshCount;
                var historyBefore = control.HistoryClockRefreshCount;
                control.RefreshStatusCenterClock();
                Require(control.CompactClockRefreshCount - compactBefore == 4,
                    "a closed status dashboard did not limit its clock refresh to four compact rows");
                Require(control.HistoryClockRefreshCount == historyBefore,
                    "a closed status dashboard refreshed its complete history");
                Require(control.ScheduledClockInterval >= UniversalStatusCenterControl.MinimumClockInterval,
                    "the status clock scheduled a busy-loop interval");

                control.OpenDashboard(control.OpenHistoryButtonTarget);
                DrainUniversalStatusInput();
                compactBefore = control.CompactClockRefreshCount;
                historyBefore = control.HistoryClockRefreshCount;
                control.RefreshStatusCenterClock();
                Require(control.CompactClockRefreshCount - compactBefore == 4
                        && control.HistoryClockRefreshCount - historyBefore == 100,
                    "an open status dashboard did not refresh exactly its compact and visible history projections");
                control.CloseDashboard();

                Console.WriteLine(
                    $"status clock receipt: legacy_idle_wakes_10m=600; deadline_wakes_without_rows=0; closed_rows_per_wake=4; open_rows_per_wake={rows.Length + 4}");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void UniversalStatusCenterDashboardFiltersAndRestoresFocus()
    {
        RunStaTest(() =>
        {
            var cleared = false;
            string? navigated = null;
            var now = DateTimeOffset.Now;
            var rows = new[]
            {
                StatusRow("running", "Models", "Running", "Loading model", now, isActive: true),
                StatusRow("old-running", "Agent", "Running", "Superseded run", now.AddSeconds(-1)),
                StatusRow("warning", "Provider", "Warning", "Provider slow", now.AddSeconds(-2), isUnresolved: true),
                StatusRow("failed", "Arena", "Failed", "Run failed", now.AddSeconds(-3), navigation: "arena", isUnresolved: true),
                StatusRow("done", "App", "Succeeded", "Export saved", now.AddSeconds(-4), canClear: true)
            };
            var control = HostUniversalStatusCenter(out var host);
            var opener = new Button { Content = "Open", Focusable = true, MinHeight = 44 };
            var outside = new Button { Content = "Outside", Focusable = true, MinHeight = 44 };
            var frame = (Grid)host.Content;
            frame.Children.Add(opener);
            frame.Children.Add(outside);
            Grid.SetColumn(opener, 0);
            Grid.SetColumn(outside, 0);
            outside.HorizontalAlignment = HorizontalAlignment.Right;
            try
            {
                control.ApplyPresentation(rows, () => cleared = true, target => navigated = target);
                DrainUniversalStatusInput();
                Require(opener.Focus(), "the status dashboard opener could not receive focus");
                control.OpenDashboard(opener, "failed");
                DrainUniversalStatusInput();
                Require(control.IsDashboardOpen
                        && control.HistoryPopupTarget.Placement == System.Windows.Controls.Primitives.PlacementMode.Left
                        && UniversalStatusCenterControl.DashboardWidth >= 340
                        && UniversalStatusCenterControl.DashboardWidth <= 400
                        && control.SelectedHistoryRow?.Id == "failed",
                    "the dashboard did not open leftward at compact width with the clicked event selected");
                Require(control.CloseHistoryButtonTarget.MinHeight >= 44
                        && AutomationProperties.GetName(control.CloseHistoryButtonTarget).Contains("Close", StringComparison.Ordinal),
                    "the dashboard close action did not retain an accessible 44-DIP target");

                var warningFilter = control.HistoryFilterButtons
                    .Single(button => Equals(button.Tag, "Warnings"));
                warningFilter.IsChecked = true;
                DrainUniversalStatusInput();
                Require(control.FilteredHistoryRows.Select(row => row.Id).SequenceEqual(["warning"], StringComparer.Ordinal),
                    "the Warnings filter did not isolate Warning and Unconfirmed history");
                var failedFilter = control.HistoryFilterButtons
                    .Single(button => Equals(button.Tag, "Failed"));
                failedFilter.IsChecked = true;
                DrainUniversalStatusInput();
                Require(control.FilteredHistoryRows.Select(row => row.Id).SequenceEqual(["failed"], StringComparer.Ordinal),
                    "the Failed filter did not isolate failed and blocked history");
                var runningFilter = control.HistoryFilterButtons
                    .Single(button => Equals(button.Tag, "Running"));
                runningFilter.IsChecked = true;
                DrainUniversalStatusInput();
                Require(control.FilteredHistoryRows.Select(row => row.Id).SequenceEqual(["running"], StringComparer.Ordinal),
                    "the Running filter included a resolved or superseded Running generation");

                control.CloseDashboard();
                DrainUniversalStatusInput();
                Require(opener.IsKeyboardFocused,
                    "closing status history did not restore focus to the invoking control");
                control.OpenDashboard(opener, "failed");
                DrainUniversalStatusInput();
                Require(outside.Focus(), "the outside-click focus target could not receive focus");
                control.HistoryPopupTarget.IsOpen = false;
                DrainUniversalStatusInput();
                Require(opener.IsKeyboardFocused,
                    "an outside-click dashboard dismissal did not restore focus to its invoker");

                warningFilter.IsChecked = true;
                control.StatusCenterCardTarget.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                    Source = control.StatusCenterCardTarget
                });
                DrainUniversalStatusInput();
                Require(control.FilteredHistoryRows.Count == rows.Length,
                    "clicking the compact card background did not open the complete All history");
                control.CloseDashboard();
                DrainUniversalStatusInput();

                var failedButton = FindStatusButton(control.CompactStatusItemsTarget, "failed")
                    ?? throw new InvalidOperationException(
                        $"the compact failed-status row was not realized in the fixed four-row center " +
                        $"(items={control.CompactStatusItemsTarget.Items.Count}, " +
                        $"ids={string.Join(',', control.CompactRows.Select(row => row.Id))}, " +
                        $"visible={control.CompactStatusItemsTarget.IsVisible}, " +
                        $"visualChildren={VisualTreeHelper.GetChildrenCount(control.CompactStatusItemsTarget)})");
                Require(failedButton.Focus(),
                    $"the compact failed-status row could not receive focus (visible={failedButton.IsVisible}, enabled={failedButton.IsEnabled}, focusable={failedButton.Focusable})");
                control.OpenDashboard(failedButton, "failed");
                DrainUniversalStatusInput();
                control.ApplyPresentation(rows.Select(row => row.Id == "failed"
                    ? StatusRow(
                        "failed",
                        "Arena",
                        "Failed",
                        "Run still failed",
                        now.AddSeconds(1),
                        navigation: "arena",
                        isUnresolved: true)
                    : row).ToArray());
                DrainUniversalStatusInput();
                control.CloseDashboard();
                DrainUniversalStatusInput();
                Require(FindStatusButton(control.CompactStatusItemsTarget, "failed")?.IsKeyboardFocused == true,
                    "a live status update replaced the invoking row container and lost close-time focus restoration");

                failedFilter.IsChecked = true;
                control.Visibility = Visibility.Collapsed;
                control.OpenDashboard(opener);
                DrainUniversalStatusInput();
                Require(control.IsDashboardOpen
                        && ReferenceEquals(control.HistoryPopupTarget.PlacementTarget, opener)
                        && control.FilteredHistoryRows.Count == rows.Length,
                    "the collapsed-rail top-bar affordance did not anchor the dashboard with complete All history");
                var clearButton = control.ClearCompletedButtonTarget;
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(cleared, "the dashboard did not route Clear completed to the authoritative status center");

                control.Visibility = Visibility.Visible;
                opener.Visibility = Visibility.Collapsed;
                control.CloseDashboard();
                DrainUniversalStatusInput();
                Require(control.OpenHistoryButtonTarget.IsKeyboardFocused,
                    "closing after the rail expanded did not reject the hidden compact invoker and focus the keyboard-accessible card affordance");
                opener.Visibility = Visibility.Visible;

                control.ApplyPresentation(rows);
                control.Announce("Ready.", assertive: false);
                Require(string.IsNullOrEmpty(control.LiveAnnouncementTarget.Text),
                    "returning to Ready incorrectly generated a screen-reader announcement");
                control.Announce("Arena run failed.", assertive: true);
                Require(control.LiveAnnouncementTarget.Text == "Arena run failed."
                        && AutomationProperties.GetLiveSetting(control.LiveAnnouncementTarget) == AutomationLiveSetting.Assertive,
                    "a blocking user-triggered failure did not use the single assertive shell announcer");
                control.Announce("Arena run failed.", assertive: true);
                Require(control.LiveAnnouncementTarget.Text == "Arena run failed.",
                    "a distinct later operation with identical summary lost its shell live announcement text");
            }
            finally
            {
                control.CloseDashboard(restoreFocus: false);
                host.Close();
            }
        });
    }

    static void UniversalStatusCenterShellLayoutIsPinnedAndOverlaySafe()
    {
        var mainXaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
        var controlXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/UniversalStatusCenterControl.xaml");
        var topBarXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
        var navigationXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml");

        Require(mainXaml.Contains("x:Name=\"RightRailHost\"", StringComparison.Ordinal)
                && mainXaml.Contains("x:Name=\"UniversalStatusCenter\"", StringComparison.Ordinal)
                && mainXaml.IndexOf("x:Name=\"UniversalStatusCenter\"", StringComparison.Ordinal)
                   < mainXaml.IndexOf("x:Name=\"RightRailScrollViewer\"", StringComparison.Ordinal),
            "the Universal Status Center was not pinned above the independently scrolling lower rail");
        Require(mainXaml.Contains("Visibility=\"{Binding Visibility, ElementName=ArenaRightRailPanel}\"", StringComparison.Ordinal),
            "Arena Controls did not preserve its page-specific visibility above the universal card");
        Require(controlXaml.Contains("Height=\"184\"", StringComparison.Ordinal)
                && controlXaml.Contains("Placement=\"Left\"", StringComparison.Ordinal)
                && controlXaml.Contains("Width=\"380\"", StringComparison.Ordinal)
                && controlXaml.Contains("MaxHeight=\"{Binding DashboardMaximumHeight, ElementName=Root}\"", StringComparison.Ordinal)
                && !controlXaml.Contains("Grid.ColumnSpan=\"3\"", StringComparison.Ordinal),
            "the card or history dashboard lost its fixed-height, left-overlay, compact-width contract");
        Require(controlXaml.Contains("PreviewMouseLeftButtonUp=\"StatusCenterCard_PreviewMouseLeftButtonUp\"", StringComparison.Ordinal)
                && !controlXaml.Contains("StatusCenterHeaderButton", StringComparison.Ordinal)
                && !controlXaml.Contains("Text=\"Status\"", StringComparison.Ordinal),
            "the compact card did not replace the visible Status header with a whole-card history affordance");
        Require(controlXaml.Contains("Text=\"{Binding Summary}\"", StringComparison.Ordinal)
                && controlXaml.Contains("FontSize=\"{DynamicResource Arena.Type.MicroSize}\"", StringComparison.Ordinal),
            "the compact card did not use the reduced typography token");
        Require(controlXaml.Contains("Text=\"{Binding StateText}\"", StringComparison.Ordinal),
            "compact status rows relied on glyph or color without visible state text");
        var compactAffordance = XamlStartTag(topBarXaml, "CollapsedStatusCenterButton", "Button");
        Require(compactAffordance.Contains("x:Name=\"CollapsedStatusCenterButton\"", StringComparison.Ordinal)
                && !compactAffordance.Contains("AutomationProperties.LiveSetting", StringComparison.Ordinal)
                && topBarXaml.Contains("x:Name=\"CollapsedStatusCenterStateText\"", StringComparison.Ordinal),
            "the collapsed-rail status affordance was missing or duplicated the live announcer");
        Require(XamlStartTag(mainXaml, "LoadStatus", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal)
                && XamlStartTag(mainXaml, "SettingsProviderStatusText", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal)
                && XamlStartTag(mainXaml, "ArenaEvaluationStatusText", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal)
                && XamlStartTag(mainXaml, "AgentCommandStatusText", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal)
                && XamlStartTag(topBarXaml, "ExportStatusText", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal)
                && XamlStartTag(topBarXaml, "ProviderHealthStatusText", "TextBlock")
                    .Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
            "migrated load, export, or provider feedback still duplicated the universal live announcer");
        Require(!navigationXaml.Contains("ShellStatusDockElement", StringComparison.Ordinal)
                && !navigationXaml.Contains("ShellStatusTextElement", StringComparison.Ordinal),
            "the retired bottom-left status dock remained in the visual tree");
    }

    private static UniversalStatusCenterControl HostUniversalStatusCenter(out Window host)
    {
        var control = new UniversalStatusCenterControl();
        AttachArenaPresentationResources(control);
        ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
        var frame = new Grid();
        frame.ColumnDefinitions.Add(new ColumnDefinition());
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        frame.Children.Add(control);
        Grid.SetColumn(control, 1);
        host = new Window
        {
            Content = frame,
            Width = 1200,
            Height = 760,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Show();
        return control;
    }

    private static UniversalStatusRowPresentation StatusRow(
        string id,
        string source,
        string state,
        string summary,
        DateTimeOffset timestamp,
        double? progress = null,
        string? navigation = null,
        bool isActive = false,
        bool isUnresolved = false,
        bool canClear = false) => new(
            id,
            source,
            state,
            state,
            state switch
            {
                "Running" => "",
                "Succeeded" => "",
                "Warning" => "",
                "Failed" => "",
                _ => ""
            },
            summary,
            $"Sanitized detail for {summary}.",
            timestamp,
            progress,
            navigation,
            isActive,
            isUnresolved,
            canClear);

    private static Button? FindStatusButton(DependencyObject root, string id)
    {
        if (root is Button { Tag: string tag } button && tag.Equals(id, StringComparison.Ordinal))
        {
            return button;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindStatusButton(VisualTreeHelper.GetChild(root, index), id) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static void DrainUniversalStatusInput() => Dispatcher.CurrentDispatcher.Invoke(
        DispatcherPriority.ApplicationIdle,
        new Action(() => { }));
}
