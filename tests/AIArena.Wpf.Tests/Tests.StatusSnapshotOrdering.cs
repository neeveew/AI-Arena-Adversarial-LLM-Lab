using AIArena.Wpf;
using AIArena.Wpf.Services;
using AIArena.Wpf.ViewModels;

internal static partial class Program
{
    static void StatusSnapshotReentrantPublicationKeepsLatestProjection()
    {
        RunStaTest(() =>
        {
            var center = new ApplicationStatusCenter();
            var nested = false;
            center.Changed += (_, _) =>
            {
                if (nested) return;
                nested = true;
                center.PublishNotice("nested", "Fixture", ApplicationStatusState.Warning, "Newer nested status.");
            };
            ApplicationStatusSnapshot? lastSnapshot = null;
            center.Changed += (_, args) => lastSnapshot = args.Snapshot;
            var topBar = new ShellTopBarPresentationViewModel(center);
            var control = HostUniversalStatusCenter(out var host);
            var published = new List<string>();
            using var publisher = new AIArenaApplicationStatusControlPublisher(center, (_, summary, _) => published.Add(summary));
            try
            {
                control.Bind(center);
                center.PublishNotice("outer", "Fixture", ApplicationStatusState.Info, "Older outer status.");
                DrainUniversalStatusInput();
                Require(lastSnapshot?.AppStatus == center.AppStatus && topBar.DisplayStatus == center.AppStatus
                        && control.PrimarySummary == center.AppStatus && published.Last() == center.AppStatus,
                    "Nested status publication left a later subscriber, top bar, status card, or control publisher on an older snapshot.");
            }
            finally { host.Close(); }
        });
    }

    static void StatusSnapshotConcurrentPublicationKeepsLatestProjection()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var center = new ApplicationStatusCenter();
        center.Changed += (_, args) =>
        {
            if (args.Snapshot.AppStatus != "Older status.") return;
            entered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(5)), "Concurrent publication fixture did not release its first callback.");
        };
        ApplicationStatusSnapshot? projected = null;
        center.Changed += (_, args) => projected = args.Snapshot;
        var older = Task.Run(() => center.PublishNotice("older", "Fixture", ApplicationStatusState.Info, "Older status."));
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "The first status callback did not enter its controlled wait.");
            center.PublishNotice("newer", "Fixture", ApplicationStatusState.Warning, "Newer status.");
        }
        finally { release.Set(); older.GetAwaiter().GetResult(); }
        Require(projected?.AppStatus == center.AppStatus && projected!.History.Any(entry => entry.Key == "newer"),
            "A delayed older status notification overwrote a newer concurrent projection.");
    }

    static void StatusSnapshotQueuedUiUpdateCannotOverwriteNewerStatus()
    {
        RunStaTest(() =>
        {
            var center = new ApplicationStatusCenter();
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                control.Bind(center);
                Task.Run(() => center.PublishNotice("old", "Fixture", ApplicationStatusState.Info, "Queued older status.")).GetAwaiter().GetResult();
                center.PublishNotice("new", "Fixture", ApplicationStatusState.Warning, "Immediate newer status.");
                DrainUniversalStatusInput();
                Require(control.PrimarySummary == center.AppStatus && control.PrimarySummary == "Immediate newer status.",
                    "A queued background notification replaced the status card's newer UI-thread snapshot.");
            }
            finally { host.Close(); }
        });
    }

    static void StatusSnapshotQueuedOldBindingCannotOverwriteNewCenter()
    {
        RunStaTest(() =>
        {
            var previous = new ApplicationStatusCenter();
            var current = new ApplicationStatusCenter();
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                control.Bind(previous);
                Task.Run(() => previous.PublishNotice("old", "Fixture", ApplicationStatusState.Warning, "Previous center status.")).GetAwaiter().GetResult();
                control.Bind(current);
                current.PublishNotice("new", "Fixture", ApplicationStatusState.Info, "Current center status.");
                DrainUniversalStatusInput();
                Require(control.PrimarySummary == "Current center status.",
                    "A queued event from an old binding overwrote the status card's current center.");
            }
            finally { host.Close(); }
        });
    }

    static void StatusSnapshotBurstCoalescesWithoutLosingCurrentState()
    {
        var center = new ApplicationStatusCenter(historyCapacity: 20);
        var nested = false;
        var delivered = new List<ApplicationStatusSnapshot>();
        center.Changed += (_, _) =>
        {
            if (nested) return;
            nested = true;
            for (var index = 0; index < 200; index++)
                center.PublishNotice("burst", "Fixture", ApplicationStatusState.Info, $"Burst update {index}.");
        };
        center.Changed += (_, args) => delivered.Add(args.Snapshot);
        center.PublishNotice("initial", "Fixture", ApplicationStatusState.Info, "Initial update.");
        Require(delivered.Count <= 21 && delivered.Last().AppStatus == center.AppStatus
                && delivered.Last().History.First(entry => entry.Key == "burst").Summary == "Burst update 199."
                && delivered.Zip(delivered.Skip(1)).All(pair => pair.First.Revision < pair.Second.Revision),
            "A reentrant burst retained an unbounded backlog, lost current state, or delivered decreasing revisions.");
    }

    static void StatusSnapshotDiagnosticDeliveryStaysOrdered()
    {
        var center = new ApplicationStatusCenter();
        var nested = false;
        var delivered = new List<ApplicationStatusSnapshot>();
        center.Changed += (_, _) =>
        {
            if (nested) return;
            nested = true;
            center.PublishNotice("nested", "Fixture", ApplicationStatusState.Info, "Nested update.");
            throw new InvalidOperationException(PrivateProjectionFailure);
        };
        center.Changed += (_, args) => delivered.Add(args.Snapshot);
        center.PublishNotice("initial", "Fixture", ApplicationStatusState.Info, "Initial update.");
        Require(delivered.Zip(delivered.Skip(1)).All(pair => pair.First.Revision < pair.Second.Revision)
                && delivered.Last().Revision == center.Snapshot.Revision
                && delivered.Last().History.Any(entry => entry.Key == "nested")
                && delivered.Last().VisibleEntries.Any(entry => entry.Key == ApplicationStatusCenter.ObserverWarningKey),
            "An observer diagnostic overtook or lost a nested status snapshot.");
    }
}
