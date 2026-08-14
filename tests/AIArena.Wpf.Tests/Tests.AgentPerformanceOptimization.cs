using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static partial class Program
{
    static void AgentPerformanceStaysLazyAggregatesOnceAndRetainsRows()
    {
        RunStaTest(() =>
        {
            var items = new StackPanel();
            var detail = new StackPanel();
            var popup = new Popup { Child = detail };
            var coordinator = new AgentPerformanceCoordinator(
                new VoiceStyleAdherenceService(),
                items,
                popup,
                detail,
                _ => new SolidColorBrush(Colors.Gray),
                _ => new SolidColorBrush(Colors.DeepSkyBlue),
                (_, name, _) => name,
                value => string.IsNullOrWhiteSpace(value) ? "-" : value,
                value => string.IsNullOrWhiteSpace(value) ? "idle" : value,
                value => value,
                value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => $"{value}ms",
                _ => new SolidColorBrush(Colors.Goldenrod),
                _ => new SolidColorBrush(Colors.Goldenrod),
                (value, _, fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value,
                (first, _, _) => first,
                () => true);

            var alpha = new AgentState(
                "alpha", "Alpha", "waiting", "persona", "default", "", "", "model-a", true, false, [],
                new AgentInternetSourceSummary("query", "now", ["source"]));
            var beta = new AgentState(
                "beta", "Beta", "waiting", "persona", "default", "", "", "model-b", false, false, []);
            var messages = Enumerable.Range(1, 1_000)
                .Select(turn => new TranscriptMessage(
                    turn,
                    turn % 2 == 0 ? "Alpha" : "Beta",
                    turn % 2 == 0 ? "alpha" : "beta",
                    turn,
                    "model",
                    turn,
                    turn * 2,
                    10,
                    12,
                    turn == 999 ? "error" : "ok",
                    "default",
                    false,
                    "message",
                    $"response {turn}",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    false,
                    [],
                    20,
                    100))
                .ToArray();
            var first = SnapshotForOverviewTest(true, "model", "", 0, messages, [alpha, beta]);

            for (var index = 0; index < 100; index++)
            {
                coordinator.ObserveSnapshot(first);
            }
            Require(coordinator.DiagnosticRenderCount == 0 && coordinator.DiagnosticMessageVisits == 0,
                "collapsed Agent Performance should retain only the latest snapshot and perform zero message or visual work");
            Require(items.Children.Count == 0,
                "collapsed Agent Performance should not materialize rows");

            coordinator.SetExpanded(true);
            Require(coordinator.DiagnosticRenderCount == 1,
                "first expansion should render the newest retained snapshot exactly once");
            Require(coordinator.DiagnosticMessageVisits == messages.Length,
                "expanded metrics should aggregate the entire transcript in one pass");
            Require(items.Children.Count == 3,
                "inactive historical participants and Narrator should remain represented");
            var alphaStats = coordinator.LastStats.Single(item => item.AgentId == "alpha");
            var betaStats = coordinator.LastStats.Single(item => item.AgentId == "beta");
            Require(alphaStats.Calls == 500 && alphaStats.Tokens == 5_000 && alphaStats.Context == 2_000,
                "single-pass Alpha aggregation should preserve call, token, and maximum-context metrics");
            Require(alphaStats.AverageLatencyMs == 501 && alphaStats.LastLatencyMs == 1_000
                    && alphaStats.AverageTokensPerSecond == 20 && alphaStats.AverageTimeToFirstTokenMs == 100,
                "single-pass Alpha aggregation should preserve average/latest native telemetry");
            Require(betaStats.Calls == 500 && betaStats.Tokens == 5_000 && betaStats.Context == 1_998
                    && betaStats.AverageLatencyMs == 500 && betaStats.LastLatencyMs == 999
                    && betaStats.Failures == 1 && betaStats.EmptyResponses == 0,
                "single-pass Beta aggregation should preserve inactive-history, truncating averages, and quality counts");
            Require(alphaStats.Activity.Count == 12 && betaStats.Activity.Count == 12,
                "single-pass aggregation should retain the exact twelve-turn activity window per participant");
            var alphaRow = items.Children[0];
            var betaRow = items.Children[1];
            var host = new Window { Content = items, Width = 500, Height = 500, ShowInTaskbar = false };
            host.Show();
            host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var focusedSources = DescendantButtons((DependencyObject)alphaRow)
                .Single(button => AutomationProperties.GetName(button) == "Show internet sources for Alpha");
            Require(focusedSources.Focus() && ReferenceEquals(Keyboard.FocusedElement, focusedSources),
                "the internet-sources action should accept keyboard focus before reconciliation");

            var extra = new TranscriptMessage(
                1_001, "Alpha", "alpha", 1_001, "model", 200, 2_500, 25, 25, "ok", "default", false,
                "message", "latest response", "", "", "", "", "", "", "", "", false, [], 25, 80);
            var next = first with { Messages = messages.Append(extra).ToArray() };
            coordinator.ObserveSnapshot(next);
            Require(coordinator.DiagnosticRenderCount == 2,
                "a visible new generation should reconcile immediately");
            Require(coordinator.DiagnosticMessageVisits == messages.Length + next.Messages.Count,
                "each visible generation should visit each message once, independent of participant count");
            Require(ReferenceEquals(alphaRow, items.Children[0]) && ReferenceEquals(betaRow, items.Children[1]),
                "durable participant row identity should survive ordinary turn updates");
            host.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
            var refreshedSources = DescendantButtons((DependencyObject)items.Children[0])
                .Single(button => AutomationProperties.GetName(button) == "Show internet sources for Alpha");
            Require(!ReferenceEquals(focusedSources, refreshedSources)
                    && ReferenceEquals(Keyboard.FocusedElement, refreshedSources),
                "reconciling a retained row should restore focus to its equivalent nested action");
            Require(((FrameworkElement)items.Children[0]).Tag is AgentPerformanceCoordinator.AgentPerformanceStats refreshed
                    && refreshed.Calls == 501 && refreshed.Tokens == 5_025 && refreshed.Context == 2_500,
                "retained rows should receive current stats rather than keeping stale event-handler state");

            coordinator.SetExpanded(false);
            var hidden = next with { Messages = next.Messages.Append(extra with { Turn = 1_002, Text = "hidden" }).ToArray() };
            coordinator.ObserveSnapshot(hidden);
            Require(coordinator.DiagnosticRenderCount == 2,
                "collapsed updates should not render after the surface has been used");
            coordinator.SetExpanded(true);
            Require(coordinator.DiagnosticRenderCount == 3
                    && coordinator.DiagnosticMessageVisits == messages.Length + next.Messages.Count + hidden.Messages.Count,
                "reopening should render only the newest hidden generation in one pass");

            Console.WriteLine(
                $"RECEIPT agent-performance baseline_hidden_refreshes=100 baseline_child_rebuilds=300 " +
                $"hidden_message_visits=0 hidden_renders=0 visible_messages={messages.Length} visible_message_visits={messages.Length}");
            host.Close();
        });
    }
}
