using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void CheckpointRefreshLateFailurePreservesChangedMode()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var mode in new[] { "session", "template", "checkpoint-return" })
            {
                using var fixture = new CheckpointRefreshFixture();
                var pending = NewCheckpointListCompletion();
                fixture.List = _ => pending.Task;
                var refresh = fixture.BackgroundRefresh();
                Require(!refresh.IsCompleted, "The listing failure must remain deferred until after the mode change.");
                fixture.ChangeMode(mode == "checkpoint-return" ? "template" : mode);
                if (mode == "checkpoint-return") fixture.ChangeMode("checkpoint");
                var currentStatus = fixture.Status.Text;
                pending.SetException(new IOException("private checkpoint path"));
                PumpDocumentImportTask(refresh);
                Require(fixture.Status.Text == currentStatus,
                    $"A late checkpoint failure replaced the newer {mode} status: {fixture.Status.Text}");
            }
        }));
    }

    static void CheckpointRefreshPendingResultsPreserveClearedState()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var fails in new[] { false, true })
            {
                using var fixture = new CheckpointRefreshFixture();
                fixture.List = _ => Task.FromResult<IReadOnlyList<CheckpointSummary>>([fixture.Row("initial")]);
                PumpDocumentImportTask(fixture.Coordinator.RefreshCheckpointsAsync());
                var pending = NewCheckpointListCompletion();
                fixture.List = _ => pending.Task;
                var refresh = fixture.BackgroundRefresh();
                fixture.Coordinator.ClearCheckpoints("Snapshot unavailable; previous checkpoint list cleared.");
                var currentStatus = fixture.Status.Text;
                if (fails) pending.SetException(new IOException("obsolete checkpoint failure"));
                else pending.SetResult([fixture.Row("obsolete")]);
                PumpDocumentImportTask(refresh);
                Require(fixture.Picker.Items.Count == 0 && fixture.Status.Text == currentStatus,
                    $"A pending {(fails ? "failure" : "success")} replaced intentionally cleared checkpoint state.");
            }
        }));
    }

    static void CheckpointRefreshCurrentFailureStaysVisibleAndRecovers()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new CheckpointRefreshFixture();
            fixture.List = _ => Task.FromException<IReadOnlyList<CheckpointSummary>>(new IOException("private checkpoint path"));
            PumpDocumentImportTask(fixture.BackgroundRefresh());
            Require(fixture.Status.Text.Contains("AA-SAVED-IO", StringComparison.Ordinal)
                    && !fixture.Status.Text.Contains("private checkpoint path", StringComparison.Ordinal),
                "A current background refresh must retain a privacy-safe failure notice.");
            var primaryFailure = false;
            try { PumpDocumentImportTask(fixture.Coordinator.RefreshCheckpointsAsync()); }
            catch (IOException) { primaryFailure = true; }
            Require(primaryFailure, "An awaited primary refresh must still propagate its actual store failure.");
            fixture.List = _ => Task.FromResult<IReadOnlyList<CheckpointSummary>>([fixture.Row("recovered")]);
            PumpDocumentImportTask(fixture.Coordinator.RefreshCheckpointsAsync("recovered"));
            Require(fixture.Picker.SelectedItem is CheckpointSummary { Id: "recovered" }
                    && fixture.Status.Text.StartsWith("1 checkpoint available.", StringComparison.Ordinal),
                "A healthy refresh must recover the list and clear the old error.");
        }));
    }

    static void CheckpointRefreshRealStorePreservesSelectionAndName()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new CheckpointRefreshFixture(useRealStore: true);
            PumpDocumentImportTask(fixture.Store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "default"));
            var first = PumpDocumentImportTask(fixture.Store.SaveCheckpointAsync("default", "First checkpoint"));
            PumpDocumentImportTask(fixture.Store.SaveCheckpointAsync("default", "Second checkpoint"));
            fixture.Name.Text = "Unsent checkpoint name";
            PumpDocumentImportTask(fixture.Coordinator.RefreshCheckpointsAsync(first.Id));
            Require(fixture.Picker.Items.Count == 2
                    && fixture.Picker.SelectedItem is CheckpointSummary selected && selected.Id == first.Id
                    && fixture.Name.Text == "Unsent checkpoint name",
                "The actual store boundary must preserve checkpoint selection and the name draft.");
        }));
    }

    private static TaskCompletionSource<IReadOnlyList<CheckpointSummary>> NewCheckpointListCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CheckpointRefreshFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "aiarena-checkpoint-refresh-" + Guid.NewGuid().ToString("N"));
        private readonly Window owner = new();
        private readonly ComboBox mode = new();
        internal SessionStore Store { get; }
        internal SavedStateWorkflowCoordinator Coordinator { get; }
        internal TextBlock Status { get; } = new();
        internal ComboBox Picker { get; } = new();
        internal TextBox Name { get; } = new();
        internal Func<string, Task<IReadOnlyList<CheckpointSummary>>> List { get; set; } =
            _ => Task.FromResult<IReadOnlyList<CheckpointSummary>>([]);

        internal CheckpointRefreshFixture(bool useRealStore = false)
        {
            Directory.CreateDirectory(root);
            Store = new SessionStore(root);
            var events = new EventLogStore(root);
            var active = new SessionSummary("default", Path.Combine(root, "snapshot.json"), true, 0, 0, 0, DateTimeOffset.Now);
            foreach (var tag in new[] { "checkpoint", "session", "template" })
                mode.Items.Add(new ComboBoxItem { Tag = tag, Content = tag });
            mode.SelectedIndex = 0;
            var fork = new SessionForkWorkflowService(Store, events, () => active, () => false,
                (_, action) => action(CancellationToken.None), (_, _) => Task.CompletedTask, (_, _, _) => { });
            Coordinator = new SavedStateWorkflowCoordinator(owner, Store, events,
                new ScenarioTemplateStore(Path.Combine(root, "templates.json")), mode, Name, Picker,
                new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock(), Status,
                new Button(), new Button(), new Button(), new Button(), new Border(), new TextBlock(), new Button(), fork,
                () => active, () => ThemePalette.Resolve("dark-blue"), () => false, () => false,
                (_, action) => action(), (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Brushes.White, _ => { }, _ => { },
                listCheckpointsAsync: useRealStore ? null : session => List(session));
            Coordinator.SetSessions([active]);
            Coordinator.UpdatePicker();
        }

        internal CheckpointSummary Row(string id) => new(id, id, "default", DateTimeOffset.Now.ToUnixTimeSeconds(), Path.Combine(root, id + ".json"));
        internal void ChangeMode(string tag)
        {
            mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
            Coordinator.OnModeSelectionChanged();
        }
        internal Task BackgroundRefresh() => (Task)typeof(SavedStateWorkflowCoordinator)
            .GetMethod("RefreshCheckpointsSafelyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Coordinator, ["default"])!;

        public void Dispose()
        {
            owner.Close();
            var resolved = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    && Path.GetFileName(resolved).StartsWith("aiarena-checkpoint-refresh-", StringComparison.Ordinal),
                "Checkpoint fixture cleanup escaped its owned temporary root.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
