using System.Diagnostics;
using AIArena.Core.Persistence;

internal static partial class ArenaTurnStreamingTests
{
    public static void PostCommitLogHeldLeasesReleaseSavedState()
    {
        WithFixture((store, _, root) =>
        {
            var snapshot = Load(store);
            snapshot.MatchType = "saved-before-diagnostic";
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var log = EventLogStore.ForSessionStore(store);
            var held = CrossProcessWriteLease.AcquireAsync(log.EventPath(), TimeSpan.FromSeconds(5), CancellationToken.None)
                .GetAwaiter().GetResult();
            Task<Exception?>? operation = null;
            var returnedWhileHeld = false;
            var guardReleased = false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                operation = PostCommitEvidence.TryAppendAsync(log, "default", "saved_setting", new { changed = true });
                returnedWhileHeld = ReferenceEquals(Task.WhenAny(operation, Task.Delay(2_000)).GetAwaiter().GetResult(), operation);
                if (returnedWhileHeld)
                {
                    var read = store.LoadSnapshotAsync();
                    guardReleased = ReferenceEquals(Task.WhenAny(read, Task.Delay(2_000)).GetAwaiter().GetResult(), read);
                    if (guardReleased) Require(read.Result?.MatchType == "saved-before-diagnostic", "The saved result changed while diagnostic logging waited.");
                }
            }
            finally { held.Dispose(); }
            var evidenceError = operation!.GetAwaiter().GetResult();
            var resumed = PostCommitEvidence.TryAppendAsync(log, "default", "next_setting", new { changed = false }).GetAwaiter().GetResult();
            var records = File.ReadAllLines(log.EventPath());
            Console.WriteLine($"postcommit lease receipt: returned_while_held={returnedWhileHeld}; snapshot_guard_released={guardReleased}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}; records={records.Length}");
            Require(returnedWhileHeld && guardReleased && evidenceError is TimeoutException && resumed is null
                    && records.Length == 1 && records[0].Contains("next_setting", StringComparison.Ordinal)
                    && Load(store).MatchType == "saved-before-diagnostic",
                "A committed result remained waiting for the diagnostic lease, retained its snapshot guard, or replayed the abandoned evidence.");
        });
    }

    public static void PostCommitLogLateAtomicCompletionRemainsUnreplayed()
    {
        var failures = new List<string>();
        foreach (var fault in new[] { false, true })
        {
            WithFixture((store, _, root) =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writes = 0;
                var log = new EventLogStore(root, new EventLogWriteObserver
                {
                    BeforeWrite = _ =>
                    {
                        Interlocked.Increment(ref writes);
                        entered.Set();
                        Require(release.Wait(TimeSpan.FromSeconds(5)), "The controlled atomic diagnostic write was not released.");
                        if (fault) throw new IOException(@"C:\Users\private\late-event.txt");
                    },
                    Completion = (_, _) => completed.TrySetResult()
                }, requireExistingSession: true);
                Task<Exception?>? operation = null;
                var returnedWhileHeld = false;
                try
                {
                    operation = PostCommitEvidence.TryAppendAsync(log, "default", "saved_setting", new { changed = true });
                    Require(entered.Wait(TimeSpan.FromSeconds(2)), "The atomic diagnostic write did not start.");
                    returnedWhileHeld = ReferenceEquals(Task.WhenAny(operation, Task.Delay(2_000)).GetAwaiter().GetResult(), operation);
                }
                finally { release.Set(); }
                var evidenceError = operation!.GetAwaiter().GetResult();
                Require(completed.Task.Wait(TimeSpan.FromSeconds(2)), "The released diagnostic write did not finish.");
                Load(store);
                var records = File.Exists(log.EventPath()) ? File.ReadAllLines(log.EventPath()).Length : 0;
                Console.WriteLine($"postcommit atomic receipt: late_fault={fault}; returned_while_held={returnedWhileHeld}; write_attempts={writes}; records={records}");
                if (!returnedWhileHeld || evidenceError is not TimeoutException || writes != 1 || records != (fault ? 0 : 1))
                    failures.Add(fault ? "late fault" : "late success");
            });
        }
        Require(failures.Count == 0, "Post-save diagnostics waited for or replayed controlled atomic completion: " + string.Join(", ", failures));
    }
}
