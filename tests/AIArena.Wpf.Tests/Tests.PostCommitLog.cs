using System.Diagnostics;
using AIArena.Core.Persistence;
using AIArena.Wpf;

internal static partial class Program
{
    static void PostCommitLogHeldLeaseDoesNotHideCheckpointRestore()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-postcommit-restore-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture cleanup escaped its dedicated parent.");
        try
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.MatchType = "checkpoint-target";
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var checkpoint = store.SaveCheckpointAsync("default", "Restore target").GetAwaiter().GetResult();
            snapshot.MatchType = "newer-live-state";
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var log = EventLogStore.ForSessionStore(store);
            Directory.CreateDirectory(Path.GetDirectoryName(log.EventPath())!);
            // Hold the same OS sidecar used by CrossProcessWriteLease, not the snapshot/checkpoint write path.
            var held = new FileStream(log.EventPath() + ".write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
            Task<SavedStateWorkflowCoordinator.CheckpointRestoreCompletion?>? operation = null;
            var returnedWhileHeld = false;
            var activeRefreshes = 0;
            var checkpointRefreshes = 0;
            var refreshOutcome = "";
            var started = Stopwatch.GetTimestamp();
            try
            {
                operation = SavedStateWorkflowCoordinator.RestoreCheckpointWithSafetyCheckpointAndReportAsync(
                    store, log, "default", checkpoint.Id,
                    async outcome =>
                    {
                        activeRefreshes++;
                        refreshOutcome = outcome;
                        Require((await store.LoadSnapshotAsync())?.MatchType == "checkpoint-target", "Refresh did not see the committed restore.");
                    },
                    _ => { checkpointRefreshes++; return Task.CompletedTask; });
                returnedWhileHeld = ReferenceEquals(Task.WhenAny(operation, Task.Delay(2_000)).GetAwaiter().GetResult(), operation);
            }
            finally { held.Dispose(); }
            var completion = operation!.GetAwaiter().GetResult();
            Console.WriteLine($"postcommit restore receipt: returned_while_held={returnedWhileHeld}; active_refreshes={activeRefreshes}; checkpoint_refreshes={checkpointRefreshes}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
            Require(returnedWhileHeld && completion is not null && !completion.EventRecorded
                    && completion.Restore.RestoredCheckpoint.Id == checkpoint.Id && completion.Restore.SafetyCheckpoint is not null
                    && activeRefreshes == 1 && checkpointRefreshes == 1 && refreshOutcome == completion.Outcome
                    && completion.Outcome.Contains("change was saved", StringComparison.OrdinalIgnoreCase)
                    && !completion.Outcome.Contains(root, StringComparison.OrdinalIgnoreCase)
                    && store.LoadSnapshotAsync().GetAwaiter().GetResult()?.MatchType == "checkpoint-target",
                "A diagnostic lease hid the durable checkpoint restore or delayed its safe saved-with-warning refresh.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
