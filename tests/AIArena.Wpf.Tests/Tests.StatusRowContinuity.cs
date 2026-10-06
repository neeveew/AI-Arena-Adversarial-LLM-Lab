using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void StatusRowsStableRefreshPreservesViewsAndFocus()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                var timestamp = DateTimeOffset.Now;
                var rows = Enumerable.Range(0, 4).Select(index =>
                    StatusRow($"stable-{index}", "App", "Succeeded", $"Completed operation {index}.", timestamp)).ToArray();
                control.ApplyPresentation(rows);
                DrainUniversalStatusInput();
                var original = control.CompactRows.ToArray();
                var source = FindProviderModelsDescendants<TextBlock>(control)
                    .First(text => text.Text == "App" && ReferenceEquals(text.DataContext, original[0]));
                var button = FindProviderModelsDescendants<Button>(control)
                    .First(candidate => ReferenceEquals(candidate.DataContext, original[0]));
                Require(button.Focus(), "The stable status-row fixture could not focus its row.");
                var changes = new List<NotifyCollectionChangedAction>();
                control.CompactRows.CollectionChanged += (_, args) => changes.Add(args.Action);
                var historyChanges = 0;
                control.HistoryRows.CollectionChanged += (_, _) => historyChanges++;
                for (var iteration = 0; iteration < 50; iteration++)
                    control.ApplyPresentation(rows.Select(row => StatusRow(row.Id, row.Source, row.StateKey,
                        row.Summary, row.Timestamp)).ToArray());
                DrainUniversalStatusInput();
                Require(changes.Count == 0 && historyChanges == 0
                        && control.CompactRows.Select((row, index) => ReferenceEquals(row, original[index])).All(value => value)
                        && button.IsKeyboardFocusWithin && source.IsVisible
                        && FindProviderModelsDescendants<TextBlock>(control).Contains(source),
                    $"Identical status updates rebuilt collections or row views (compact changes {changes.Count}; history changes {historyChanges}).");
            }
            finally { host.Close(); }
        }));
    }

    static void StatusRowsReorderAndSparseSlotsStayIncremental()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var control = HostUniversalStatusCenter(out var host);
            try
            {
                var timestamp = DateTimeOffset.Now;
                var rows = Enumerable.Range(0, 3).Select(index =>
                    StatusRow($"order-{index}", "App", "Info", $"Update {index}.", timestamp)).ToArray();
                control.ApplyPresentation(rows);
                DrainUniversalStatusInput();
                var original = control.CompactRows.ToArray();
                var changes = new List<NotifyCollectionChangedAction>();
                control.CompactRows.CollectionChanged += (_, args) => changes.Add(args.Action);
                control.ApplyPresentation([rows[2], rows[0], rows[1]]);
                DrainUniversalStatusInput();
                Require(!changes.Contains(NotifyCollectionChangedAction.Reset) && changes.Count <= 2
                        && ReferenceEquals(control.CompactRows[0], original[2])
                        && ReferenceEquals(control.CompactRows[1], original[0])
                        && ReferenceEquals(control.CompactRows[2], original[1])
                        && ReferenceEquals(control.CompactRows[3], original[3]),
                    "Reordering status entries reset their row views or rebuilt the unused slot.");
                control.ApplyPresentation([rows[0]]);
                DrainUniversalStatusInput();
                var sparse = control.CompactRows.ToArray();
                changes.Clear();
                control.ApplyPresentation([StatusRow(rows[0].Id, "App", "Info", rows[0].Summary, timestamp)]);
                DrainUniversalStatusInput();
                Require(changes.Count == 0 && control.CompactRows.Count == 4
                        && control.CompactRows.Select((row, index) => ReferenceEquals(row, sparse[index])).All(value => value),
                    "A stable sparse card removed and recreated its placeholder slots.");
                var changed = StatusRow(rows[0].Id, "Provider", "Warning", "Updated connection warning.", timestamp.AddSeconds(1));
                control.ApplyPresentation([changed]);
                DrainUniversalStatusInput();
                Require(control.CompactRows[0].Summary == changed.Summary
                        && control.PrimarySummary == changed.Summary
                        && !ReferenceEquals(control.CompactRows[0], sparse[0])
                        && control.CompactRows.Skip(1).Select((row, index) => ReferenceEquals(row, sparse[index + 1])).All(value => value),
                    "The incremental card kept a changed entry stale or replaced unaffected placeholders.");
            }
            finally { host.Close(); }
        }));
    }
}
