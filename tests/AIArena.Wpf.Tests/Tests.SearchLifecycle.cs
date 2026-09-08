using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AIArena.Wpf;

internal static partial class Program
{
    static void CrossSessionSearchRejectsLateResultsAndErrors()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var text = new TextBox { Text = "first" };
            var header = new TextBlock();
            var rows = new StackPanel();
            var picker = new ComboBox();
            picker.Items.Add(new ComboBoxItem { Content = "All Turns", Tag = "all", IsSelected = true });
            using var coordinator = new TranscriptSearchCoordinator(new Window(), Dispatcher.CurrentDispatcher,
                new Popup(), new Button(), text, new Button(), new Border(), rows, new TextBlock(), picker,
                new CheckBox(), new CheckBox(), new CheckBox(), new CheckBox(), () => false,
                AccentResourceBrush, _ => true, () => null, () => { }, resultListHeaderText: header);
            var first = new TaskCompletionSource<IReadOnlyList<CrossSessionSearchService.Hit>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken firstToken = default;
            var firstRun = coordinator.SearchAllSessionsAsync((query, token) => { firstToken = token; return first.Task; }, _ => { });
            text.Text = "second";
            coordinator.OnFilterChanged(debounceTextInput: true);
            Require(firstToken.IsCancellationRequested, "Typing must cancel the filesystem search immediately");
            var secondRun = coordinator.SearchAllSessionsAsync((query, _) => Task.FromResult<IReadOnlyList<CrossSessionSearchService.Hit>>(
                [new("second-session", DateTimeOffset.UtcNow, 1, "Agent", "second result")]), _ => { });
            PumpDocumentImportTask(secondRun);
            var currentRow = rows.Children[0];
            first.SetException(new IOException("stale scan failed"));
            PumpDocumentImportTask(firstRun);
            Require(ReferenceEquals(rows.Children[0], currentRow) && header.Text.Contains("1 match"),
                "An older failure must not replace the latest successful search");

            foreach (var boundary in new[] { "close", "surface", "dispose" })
            {
                var pending = new TaskCompletionSource<IReadOnlyList<CrossSessionSearchService.Hit>>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken pendingToken = default;
                var run = coordinator.SearchAllSessionsAsync((_, token) => { pendingToken = token; return pending.Task; }, _ => { });
                if (boundary == "close") coordinator.CloseSearch();
                else if (boundary == "surface") coordinator.SetSurface(ShellSearchSurface.Collaborate, "Search", "Search");
                else coordinator.Dispose();
                var boundaryRow = rows.Children.Count > 0 ? rows.Children[0] : null;
                Require(pendingToken.IsCancellationRequested, "Closing, switching surface or disposing must cancel search ownership");
                pending.SetResult([new("stale-session", DateTimeOffset.UtcNow, 2, "Agent", "stale result")]);
                PumpDocumentImportTask(run);
                Require((rows.Children.Count > 0 ? rows.Children[0] : null) == boundaryRow,
                    "Late results must not repopulate a closed or replaced search surface");
                text.Text = "second";
            }
        }));
    }
}
