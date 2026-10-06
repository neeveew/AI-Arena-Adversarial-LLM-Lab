using System.Windows.Threading;
using AIArena.Wpf.Services;
using AIArena.Wpf.ViewModels;

internal static partial class Program
{
    static void StatusProjectionTopBarRecoversOneTimePropertyFailure()
    {
        var center = new ApplicationStatusCenter();
        var view = new ShellTopBarPresentationViewModel(center);
        var failOnce = true;
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(view.DisplayStatus) && failOnce)
            {
                failOnce = false;
                throw new InvalidOperationException(PrivateProjectionFailure);
            }
        };
        center.PublishNotice("fixture", "Fixture", ApplicationStatusState.Info, "Fresh status.", "Fresh detail.");
        Require(view.DisplayStatus == center.AppStatus && view.DisplayStatusToolTip == center.Primary.Detail
                && view.DisplayStatusHelpText.Contains(center.Primary.Summary, StringComparison.Ordinal)
                && !view.DisplayStatus.Contains("sk-test-secret", StringComparison.Ordinal),
            "A one-time property callback left the top bar partly updated and excluded it from recovery.");
    }

    static void StatusProjectionCardRecoversOneTimeCollectionFailure()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var center = new ApplicationStatusCenter();
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                control.Bind(center);
                var failOnce = true;
                control.HistoryRows.CollectionChanged += (_, _) =>
                {
                    if (!failOnce) return;
                    failOnce = false;
                    throw new InvalidOperationException(PrivateProjectionFailure);
                };
                center.PublishNotice("fixture", "Fixture", ApplicationStatusState.Info, "Fresh status.");
                DrainUniversalStatusInput();
                Require(control.PrimarySummary == center.AppStatus
                        && control.CompactRows.Any(row => row.StateText == "Warning")
                        && control.HistoryRows.Any(row => row.Summary == "Fresh status."),
                    "A one-time collection callback left the status card on a partial older projection.");
                host.Content = null;
                if (control.Parent is System.Windows.Controls.Panel previousParent) previousParent.Children.Remove(control);
                var panel = new System.Windows.Controls.StackPanel { Width = 320 };
                panel.Children.Add(control);
                CaptureWorkspaceConversationPreview(panel, control, "recovered-status-card.png");
                panel.Children.Clear();
            }
            finally { host.Close(); }
        }));
    }

    static void StatusProjectionQueuedCardFailureDoesNotEscapeDispatcher()
    {
        RunStaTest(() =>
        {
            var center = new ApplicationStatusCenter();
            var control = HostUniversalStatusCenter(out var host);
            var escaped = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler capture = (_, args) => { escaped.Add(args.Exception); args.Handled = true; };
            Dispatcher.CurrentDispatcher.UnhandledException += capture;
            try
            {
                control.Bind(center);
                var failOnce = true;
                control.HistoryRows.CollectionChanged += (_, _) =>
                {
                    if (!failOnce) return;
                    failOnce = false;
                    throw new InvalidOperationException(PrivateProjectionFailure);
                };
                Task.Run(() => center.PublishNotice("fixture", "Fixture", ApplicationStatusState.Info, "Queued fresh status.")).GetAwaiter().GetResult();
                DrainUniversalStatusInput();
                Require(escaped.Count == 0 && control.PrimarySummary == center.AppStatus
                        && control.CompactRows.Any(row => row.StateText == "Warning"),
                    "A queued status projection exception escaped the dispatcher or failed to recover the current view.");
            }
            finally { Dispatcher.CurrentDispatcher.UnhandledException -= capture; host.Close(); }
        });
    }

    static void StatusProjectionPersistentQueuedFailureIsBounded()
    {
        RunStaTest(() =>
        {
            var center = new ApplicationStatusCenter();
            var control = HostUniversalStatusCenter(out var host);
            var calls = 0;
            var escaped = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler capture = (_, args) => { escaped.Add(args.Exception); args.Handled = true; };
            System.Collections.Specialized.NotifyCollectionChangedEventHandler fail = (_, _) =>
            {
                calls++;
                throw new InvalidOperationException(PrivateProjectionFailure);
            };
            Dispatcher.CurrentDispatcher.UnhandledException += capture;
            try
            {
                control.Bind(center);
                control.HistoryRows.CollectionChanged += fail;
                Task.Run(() => center.PublishNotice("fixture", "Fixture", ApplicationStatusState.Info, "Queued status.")).GetAwaiter().GetResult();
                DrainUniversalStatusInput();
                Require(escaped.Count == 0 && calls == 2
                        && center.VisibleEntries.Count(entry => entry.Key == ApplicationStatusCenter.ObserverWarningKey) == 1,
                    "A persistent queued rendering failure escaped or recursively retried its diagnostic.");
                control.HistoryRows.CollectionChanged -= fail;
                center.PublishNotice("next", "Fixture", ApplicationStatusState.Info, "Later recovered status.");
                DrainUniversalStatusInput();
                Require(control.PrimarySummary == center.AppStatus
                        && control.HistoryRows.Any(row => row.Summary == "Later recovered status."),
                    "Removing the failing rendering listener did not allow a later snapshot to recover.");
            }
            finally
            {
                control.HistoryRows.CollectionChanged -= fail;
                Dispatcher.CurrentDispatcher.UnhandledException -= capture;
                host.Close();
            }
        });
    }
}
