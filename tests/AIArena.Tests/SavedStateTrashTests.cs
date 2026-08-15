using System.Text.Json;
using System.Text.Json.Nodes;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

internal static class SavedStateTrashTests
{
    public static void RecoversSessionsAndCheckpointsWithBoundedTrash()
    {
        SessionAndCheckpointUndoSurviveAStoreRestart();
        RestoreNeverOverwritesNameCollisionsOrEscapesTheDataRoot();
        InterruptedEntriesExpireAndCapacityRemainsBounded();
        SessionSummaryCountCachesStayBoundedAndInvalidateAcrossTrashLifecycle();
        CapacityReservationNeverEvictsRecoveryBeforeTheNewDeleteCommits();
        LockedCapacityReconcilesAfterTheNewDeleteCommits();
        CancellationAndConcurrentDeletionHaveOneAtomicWinner();
        ProviderCallCannotEnterDuringTheTrashCommit();
        CheckpointUndoCannotRaceWholeSessionTrash();
        TrashedSessionIdentityRemainsReservedUntilUndoOrExpiry();
        SessionInstanceIdentityMigratesAndSurvivesCheckpointRestore();
        GuardedQueuedEventsCannotOutliveTheirSession();
        GuardedAndStandaloneEventQueuesDoNotCoalesce();
    }

    private static void SessionAndCheckpointUndoSurviveAStoreRestart()
    {
        var root = TestRoot();
        try
        {
            var at = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var clock = new MutableTrashTimeProvider(at);
            var store = Store(root, clock);
            SaveSession(store, "default", "default-state");
            SaveSession(store, "recoverable", "recoverable-state");
            var originalSessionBytes = File.ReadAllBytes(store.SnapshotPath("recoverable"));

            var sessionReceipt = store.TrashSessionAsync("recoverable").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("session Trash move did not return a receipt");
            Require(sessionReceipt.Kind == SavedStateDeletionKind.Session, "session receipt kind changed");
            Require(!Directory.Exists(Path.GetDirectoryName(store.SnapshotPath("recoverable"))!), "session remained in its live location");
            Require(Directory.Exists(Path.Combine(store.SavedStateTrashRoot, sessionReceipt.Id, "payload")), "session payload was not retained in Trash");
            Require(File.Exists(Path.Combine(store.SavedStateTrashRoot, sessionReceipt.Id, "tombstone.json")), "session tombstone was not durable");

            var restartedStore = Store(root, clock);
            var rehydratedSessionReceipts = restartedStore.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult();
            Require(rehydratedSessionReceipts.Count == 1
                    && rehydratedSessionReceipts[0].Id.Equals(sessionReceipt.Id, StringComparison.OrdinalIgnoreCase),
                "a restarted store did not reconstruct the durable session Undo receipt");
            var sessionRestore = restartedStore.RestoreDeletedStateAsync(rehydratedSessionReceipts[0]).GetAwaiter().GetResult();
            Require(sessionRestore.Status == SavedStateRestoreStatus.Restored, $"session Undo returned {sessionRestore.Status}");
            Require(File.ReadAllBytes(restartedStore.SnapshotPath("recoverable")).SequenceEqual(originalSessionBytes), "session Undo changed snapshot bytes");
            Require(!Directory.Exists(Path.Combine(store.SavedStateTrashRoot, sessionReceipt.Id)), "restored session tombstone was not cleaned up");
            Require(restartedStore.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult().Count == 0,
                "a restored session remained in the authoritative Undo list");

            var checkpoint = restartedStore.SaveCheckpointAsync("default", "Before risky turn").GetAwaiter().GetResult();
            var originalCheckpointBytes = File.ReadAllBytes(checkpoint.Path);
            var checkpointReceipt = restartedStore
                .TrashCheckpointAsync("default", checkpoint.Id, checkpoint.Name)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("checkpoint Trash move did not return a receipt");
            Require(checkpointReceipt.Kind == SavedStateDeletionKind.Checkpoint, "checkpoint receipt kind changed");
            Require(!File.Exists(checkpoint.Path), "checkpoint remained in its live location");
            Require(File.Exists(Path.Combine(store.SavedStateTrashRoot, checkpointReceipt.Id, "payload.json")), "checkpoint payload was not retained in Trash");

            var rehydratedCheckpointReceipts = Store(root, clock)
                .ListRestorableDeletedStatesAsync().GetAwaiter().GetResult();
            Require(rehydratedCheckpointReceipts.Count == 1
                    && rehydratedCheckpointReceipts[0].Id.Equals(checkpointReceipt.Id, StringComparison.OrdinalIgnoreCase),
                "a restarted store did not reconstruct the durable checkpoint Undo receipt");

            var checkpointRestore = Store(root, clock)
                .RestoreDeletedStateAsync(rehydratedCheckpointReceipts[0])
                .GetAwaiter()
                .GetResult();
            Require(checkpointRestore.Status == SavedStateRestoreStatus.Restored, $"checkpoint Undo returned {checkpointRestore.Status}");
            Require(File.ReadAllBytes(checkpoint.Path).SequenceEqual(originalCheckpointBytes), "checkpoint Undo changed bytes");
            Require(!restartedStore.DeleteSessionAsync("default").GetAwaiter().GetResult(), "default session became deletable");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void RestoreNeverOverwritesNameCollisionsOrEscapesTheDataRoot()
    {
        var root = TestRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, $"outside-{Guid.NewGuid():N}.txt");
        try
        {
            var clock = new MutableTrashTimeProvider(new DateTimeOffset(2041, 2, 3, 4, 5, 6, TimeSpan.Zero));
            var store = Store(root, clock);
            SaveSession(store, "collision", "original");
            var receipt = store.TrashSessionAsync("collision").GetAwaiter().GetResult()!;
            var replacementDirectory = Path.GetDirectoryName(store.SnapshotPath("collision"))!;
            Directory.CreateDirectory(replacementDirectory);
            File.Copy(
                Path.Combine(store.SavedStateTrashRoot, receipt.Id, "payload", "snapshot.json"),
                store.SnapshotPath("collision"));
            var replacementBytes = File.ReadAllBytes(store.SnapshotPath("collision"));

            var collision = store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult();
            Require(collision.Status == SavedStateRestoreStatus.NameCollision, $"session collision returned {collision.Status}");
            Require(File.ReadAllBytes(store.SnapshotPath("collision")).SequenceEqual(replacementBytes), "Undo overwrote a replacement session");
            Require(Directory.Exists(Path.Combine(store.SavedStateTrashRoot, receipt.Id, "payload")), "collision discarded the recoverable session");

            File.WriteAllText(outside, "outside-safe");
            var tombstonePath = Path.Combine(store.SavedStateTrashRoot, receipt.Id, "tombstone.json");
            var tombstone = JsonNode.Parse(File.ReadAllText(tombstonePath))!.AsObject();
            tombstone["original_relative_path"] = Path.Combine("..", Path.GetFileName(outside));
            File.WriteAllText(tombstonePath, tombstone.ToJsonString());
            var invalid = store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult();
            Require(invalid.Status == SavedStateRestoreStatus.Invalid, $"path escape returned {invalid.Status}");
            Require(File.ReadAllText(outside) == "outside-safe", "path escape touched data outside the data root");
            Require(!store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult()
                    .Any(candidate => candidate.Id.Equals(receipt.Id, StringComparison.OrdinalIgnoreCase)),
                "a path-escape tombstone was advertised as a durable Undo candidate");

            var checkpoint = store.SaveCheckpointAsync("collision", "collision checkpoint").GetAwaiter().GetResult();
            var checkpointReceipt = store.TrashCheckpointAsync("collision", checkpoint.Id, checkpoint.Name).GetAwaiter().GetResult()!;
            File.WriteAllText(checkpoint.Path, "replacement-checkpoint");
            var checkpointCollision = store.RestoreDeletedStateAsync(checkpointReceipt).GetAwaiter().GetResult();
            Require(checkpointCollision.Status == SavedStateRestoreStatus.NameCollision, $"checkpoint collision returned {checkpointCollision.Status}");
            Require(File.ReadAllText(checkpoint.Path) == "replacement-checkpoint", "Undo overwrote a replacement checkpoint");
            Require(File.Exists(Path.Combine(store.SavedStateTrashRoot, checkpointReceipt.Id, "payload.json")), "checkpoint collision discarded its Trash payload");
        }
        finally
        {
            TryDeleteFile(outside);
            DeleteTestRoot(root);
        }
    }

    private static void InterruptedEntriesExpireAndCapacityRemainsBounded()
    {
        var root = TestRoot();
        try
        {
            var start = new DateTimeOffset(2042, 3, 4, 5, 6, 7, TimeSpan.Zero);
            var clock = new MutableTrashTimeProvider(start);
            var store = new SessionStore(root, clock, TimeSpan.FromHours(1), 2);
            SaveSession(store, "prepared", "live-source");

            var preparedId = Guid.NewGuid().ToString("N");
            var preparedEntry = Path.Combine(store.SavedStateTrashRoot, preparedId);
            Directory.CreateDirectory(preparedEntry);
            File.WriteAllText(
                Path.Combine(preparedEntry, "tombstone.json"),
                JsonSerializer.Serialize(new
                {
                    schema_version = 1,
                    id = preparedId,
                    kind = "session",
                    session_id = "prepared",
                    checkpoint_id = "",
                    display_name = "prepared",
                    original_relative_path = Path.Combine("sessions", "prepared"),
                    deleted_at_unix_ms = start.ToUnixTimeMilliseconds(),
                    expires_at_unix_ms = start.AddHours(1).ToUnixTimeMilliseconds()
                }));
            Require(store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult().Count == 0,
                "a prepared tombstone without its payload was advertised for Undo");
            var reconciled = store.PurgeExpiredSavedStateTrashAsync().GetAwaiter().GetResult();
            Require(reconciled == 1, $"prepared tombstone reconciliation purged {reconciled} entries");
            Require(!Directory.Exists(preparedEntry), "interrupted pre-move tombstone survived reconciliation");
            Require(File.Exists(store.SnapshotPath("prepared")), "reconciliation deleted the still-live source");

            SaveSession(store, "expired", "expired-state");
            var expiredReceipt = store.TrashSessionAsync("expired").GetAwaiter().GetResult()!;
            clock.UtcNow = start.AddHours(2);
            Require(store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult().Count == 0,
                "expired Trash metadata remained an authoritative Undo candidate");
            var expiredRestore = store.RestoreDeletedStateAsync(expiredReceipt).GetAwaiter().GetResult();
            Require(expiredRestore.Status == SavedStateRestoreStatus.Expired, $"expired Undo returned {expiredRestore.Status}");
            Require(!Directory.Exists(Path.Combine(store.SavedStateTrashRoot, expiredReceipt.Id)), "expired payload was not purged");

            var corruptId = Guid.NewGuid().ToString("N");
            var corruptEntry = Path.Combine(store.SavedStateTrashRoot, corruptId);
            Directory.CreateDirectory(corruptEntry);
            File.WriteAllText(Path.Combine(corruptEntry, "tombstone.json"), "{broken");
            File.WriteAllText(Path.Combine(corruptEntry, "payload.json"), "orphaned");
            Directory.SetLastWriteTimeUtc(corruptEntry, clock.UtcNow.Subtract(TimeSpan.FromHours(2)).UtcDateTime);
            Require(store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult().Count == 0,
                "corrupt Trash metadata was advertised for Undo");
            Require(store.PurgeExpiredSavedStateTrashAsync().GetAwaiter().GetResult() == 1, "aged corrupt metadata was not purged");

            var receipts = new List<SavedStateDeletionReceipt>();
            for (var index = 0; index < 3; index++)
            {
                var id = $"bounded-{index}";
                SaveSession(store, id, id);
                receipts.Add(store.TrashSessionAsync(id).GetAwaiter().GetResult()!);
            }

            var entries = Directory.EnumerateDirectories(store.SavedStateTrashRoot).ToArray();
            Require(entries.Length == 2, $"bounded Trash retained {entries.Length} entries instead of 2");
            Require(!Directory.Exists(Path.Combine(store.SavedStateTrashRoot, receipts[0].Id)), "capacity cleanup retained the oldest entry");
            Require(store.RestoreDeletedStateAsync(receipts[0]).GetAwaiter().GetResult().Status == SavedStateRestoreStatus.NotFound,
                "purged receipt remained restorable");
            var restartedCandidates = new SessionStore(root, clock, TimeSpan.FromHours(1), 2)
                .ListRestorableDeletedStatesAsync().GetAwaiter().GetResult();
            Require(restartedCandidates.Select(receipt => receipt.Id).SequenceEqual(
                    new[] { receipts[2].Id, receipts[1].Id },
                    StringComparer.OrdinalIgnoreCase),
                "durable Undo candidates were not validated and returned newest first");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CancellationAndConcurrentDeletionHaveOneAtomicWinner()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            SaveSession(store, "cancelled", "cancelled-state");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    store.TrashSessionAsync("cancelled", cancellation.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("pre-cancelled delete completed");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            Require(File.Exists(store.SnapshotPath("cancelled")), "pre-cancelled delete moved the session");

            using (var blockingLease = CrossProcessWriteLease
                       .AcquireAsync(store.SnapshotPath("cancelled"), TimeSpan.FromSeconds(5), CancellationToken.None)
                       .GetAwaiter()
                       .GetResult())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                try
                {
                    store.TrashSessionAsync("cancelled", cancellation.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("lease-blocked delete completed");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }

            Require(File.Exists(store.SnapshotPath("cancelled")), "lease-cancelled delete moved the session");

            SaveSession(store, "concurrent", "concurrent-state");
            var deletes = Enumerable.Range(0, 2)
                .Select(_ => store.TrashSessionAsync("concurrent"))
                .ToArray();
            Task.WhenAll(deletes).GetAwaiter().GetResult();
            var winners = deletes.Select(task => task.Result).Where(receipt => receipt is not null).ToArray();
            Require(winners.Length == 1, $"concurrent delete produced {winners.Length} winners");
            Require(!Directory.Exists(Path.GetDirectoryName(store.SnapshotPath("concurrent"))!), "concurrent delete left a live session directory");
            Require(Directory.EnumerateDirectories(store.SavedStateTrashRoot).Count() == 1, "concurrent delete published duplicate Trash entries");
            Require(store.RestoreDeletedStateAsync(winners[0]!).GetAwaiter().GetResult().Restored, "concurrent winner could not be undone");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CapacityReservationNeverEvictsRecoveryBeforeTheNewDeleteCommits()
    {
        PrecommitSessionFailureRetainsOlderRecovery();
        PrecommitCheckpointFailureRetainsOlderRecovery();
    }

    private static void SessionSummaryCountCachesStayBoundedAndInvalidateAcrossTrashLifecycle()
    {
        var root = TestRoot();
        try
        {
            var clock = new MutableTrashTimeProvider(
                new DateTimeOffset(2042, 3, 4, 5, 6, 7, TimeSpan.Zero));
            var store = new SessionStore(
                root,
                clock,
                TimeSpan.FromMinutes(1),
                SessionStore.DefaultSavedStateTrashEntryLimit);
            var total = SessionStore.SessionSummaryCountCacheCapacity + 1;
            for (var index = 0; index < total; index++)
            {
                var sessionId = $"cache-churn-{index:D4}";
                var snapshotPath = store.SnapshotPath(sessionId);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                File.WriteAllText(snapshotPath, "{\"engine\":{\"messages\":[{}]}}");
                var eventPath = NativeDataPaths.EventPath(root, sessionId);
                Directory.CreateDirectory(Path.GetDirectoryName(eventPath)!);
                File.WriteAllText(eventPath, "{\"event\":1}\n");
            }

            var firstListing = store.ListSessionsAsync(SessionListingDetail.Full).GetAwaiter().GetResult();
            Require(firstListing.Count == total
                    && firstListing.All(summary => summary.MessageCount == 1 && summary.EventCount == 1),
                "session-summary cache churn changed the authoritative message or event counts");
            Require(SessionStore.MessageCountCacheCount == SessionStore.SessionSummaryCountCacheCapacity
                    && SessionStore.EventLineCountCacheCount == SessionStore.SessionSummaryCountCacheCapacity,
                "session-summary count caches did not enforce their exact fixed capacity under unique-path churn");

            // Remove the one entry that was admitted before the bounded cache's
            // retained window so the correctness pass exercises cache hits rather
            // than deliberately thrashing a working set one larger than capacity.
            var firstSessionId = "cache-churn-0000";
            Require(!SessionStore.MessageCountCacheContains(store.SnapshotPath(firstSessionId))
                    && !SessionStore.EventLineCountCacheContains(
                        NativeDataPaths.EventPath(root, firstSessionId)),
                "the least-recent unique session was not evicted from both bounded caches");
            Directory.Delete(Path.GetDirectoryName(store.SnapshotPath(firstSessionId))!, recursive: true);
            Directory.Delete(
                Path.GetDirectoryName(NativeDataPaths.EventPath(root, firstSessionId))!,
                recursive: true);

            var targetSessionId = $"cache-churn-{total - 1:D4}";
            var targetSnapshotPath = store.SnapshotPath(targetSessionId);
            var targetEventPath = NativeDataPaths.EventPath(root, targetSessionId);
            Require(SessionStore.MessageCountCacheContains(targetSnapshotPath)
                    && SessionStore.EventLineCountCacheContains(targetEventPath),
                "the deterministic retained cache target was not admitted");
            File.WriteAllText(targetSnapshotPath, "{\"engine\":{\"messages\":[{},{},{}]}}");
            File.WriteAllText(targetEventPath, "{\"event\":1}\n{\"event\":2}\n");

            var refreshed = store.ListSessionsAsync(SessionListingDetail.Full).GetAwaiter().GetResult()
                .Single(summary => summary.Id == targetSessionId);
            Require(refreshed.MessageCount == 3 && refreshed.EventCount == 2,
                "a retained path returned stale counts after its files changed");

            File.WriteAllText(targetSnapshotPath, "{\"engine\":{\"messages\":[{},{},{},{}]}}");
            File.WriteAllText(targetEventPath, "{\"event\":1}\n{\"event\":2}\n{\"event\":3}\n");
            using (File.Open(targetSnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (File.Open(targetEventPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = store.ListSessionsAsync(SessionListingDetail.Full).GetAwaiter().GetResult()
                    .Single(summary => summary.Id == targetSessionId);
                Require(locked.MessageCount == 0 && locked.EventCount == 0,
                    "locked summary inputs did not preserve the legacy zero-count fallback");
                Require(!SessionStore.MessageCountCacheContains(targetSnapshotPath)
                        && !SessionStore.EventLineCountCacheContains(targetEventPath),
                    "a transient read failure was cached as an authoritative zero count");
            }

            var afterUnlock = store.ListSessionsAsync(SessionListingDetail.Full).GetAwaiter().GetResult()
                .Single(summary => summary.Id == targetSessionId);
            Require(afterUnlock.MessageCount == 4 && afterUnlock.EventCount == 3,
                "summary counts did not recover after transient file locks were released");

            var receipt = store.TrashSessionAsync(targetSessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("cache lifecycle target did not enter Trash");
            Require(!SessionStore.MessageCountCacheContains(targetSnapshotPath)
                    && !SessionStore.EventLineCountCacheContains(targetEventPath),
                "Trash retained historical live-session paths in the summary caches");
            clock.UtcNow = receipt.ExpiresAt.AddMilliseconds(1);
            Require(store.PurgeExpiredSavedStateTrashAsync().GetAwaiter().GetResult() == 1
                    && !Directory.Exists(Path.Combine(store.SavedStateTrashRoot, receipt.Id))
                    && !SessionStore.MessageCountCacheContains(targetSnapshotPath)
                    && !SessionStore.EventLineCountCacheContains(targetEventPath),
                "purging the trashed session reintroduced its historical cache keys");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void PrecommitSessionFailureRetainsOlderRecovery()
    {
        var root = TestRoot();
        try
        {
            var clock = new MutableTrashTimeProvider(new DateTimeOffset(2043, 4, 5, 6, 7, 8, TimeSpan.Zero));
            var baseline = new SessionStore(root, clock, TimeSpan.FromDays(1), 1);
            SaveSession(baseline, "old-session", "old-session");
            var oldReceipt = baseline.TrashSessionAsync("old-session").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("old session capacity fixture did not enter Trash");
            SaveSession(baseline, "new-session", "new-session");

            var failing = new SessionStore(
                root,
                clock,
                TimeSpan.FromDays(1),
                1,
                savedStateTrashPreparedObserver: (receipt, _) => receipt.SessionId == "new-session"
                    ? Task.FromException(new IOException("injected prepared-session failure"))
                    : Task.CompletedTask);
            var failedReceipt = failing.TrashSessionAsync("new-session").GetAwaiter().GetResult();
            Require(failedReceipt is null, "a precommit session failure returned a recovery receipt");
            Require(File.Exists(failing.SnapshotPath("new-session")),
                "a precommit session failure moved the new live source");
            Require(Directory.Exists(Path.Combine(failing.SavedStateTrashRoot, oldReceipt.Id, "payload")),
                "capacity reservation evicted the older session before the new delete committed");
            Require(failing.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult()
                    .Single().Id.Equals(oldReceipt.Id, StringComparison.OrdinalIgnoreCase),
                "the older session was no longer the authoritative recovery candidate after rollback");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void PrecommitCheckpointFailureRetainsOlderRecovery()
    {
        var root = TestRoot();
        try
        {
            var clock = new MutableTrashTimeProvider(new DateTimeOffset(2043, 4, 5, 6, 7, 9, TimeSpan.Zero));
            var baseline = new SessionStore(root, clock, TimeSpan.FromDays(1), 1);
            SaveSession(baseline, "default", "checkpoint-capacity");
            var older = baseline.SaveCheckpointAsync("default", "older").GetAwaiter().GetResult();
            var oldReceipt = baseline.TrashCheckpointAsync("default", older.Id, older.Name)
                .GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("old checkpoint capacity fixture did not enter Trash");
            var newer = baseline.SaveCheckpointAsync("default", "newer").GetAwaiter().GetResult();

            var failing = new SessionStore(
                root,
                clock,
                TimeSpan.FromDays(1),
                1,
                savedStateTrashPreparedObserver: (receipt, _) => receipt.CheckpointId == newer.Id
                    ? Task.FromException(new IOException("injected prepared-checkpoint failure"))
                    : Task.CompletedTask);
            var failedReceipt = failing.TrashCheckpointAsync("default", newer.Id, newer.Name)
                .GetAwaiter().GetResult();
            Require(failedReceipt is null, "a precommit checkpoint failure returned a recovery receipt");
            Require(File.Exists(newer.Path), "a precommit checkpoint failure moved the new live checkpoint");
            Require(File.Exists(Path.Combine(failing.SavedStateTrashRoot, oldReceipt.Id, "payload.json")),
                "capacity reservation evicted the older checkpoint before the new delete committed");
            Require(failing.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult()
                    .Single().Id.Equals(oldReceipt.Id, StringComparison.OrdinalIgnoreCase),
                "the older checkpoint was no longer the authoritative recovery candidate after rollback");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void LockedCapacityReconcilesAfterTheNewDeleteCommits()
    {
        var root = TestRoot();
        try
        {
            var clock = new MutableTrashTimeProvider(new DateTimeOffset(2043, 4, 5, 6, 7, 8, TimeSpan.Zero));
            var store = new SessionStore(root, clock, TimeSpan.FromDays(1), 1);
            SaveSession(store, "old-trash", "old-trash");
            var oldReceipt = store.TrashSessionAsync("old-trash").GetAwaiter().GetResult()!;
            SaveSession(store, "must-stay-live", "must-stay-live");
            var tombstonePath = Path.Combine(store.SavedStateTrashRoot, oldReceipt.Id, "tombstone.json");
            using (var lockStream = new FileStream(
                       tombstonePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                var committed = store.TrashSessionAsync("must-stay-live").GetAwaiter().GetResult();
                Require(committed is not null, "a committed delete was reported as failed when the old entry was locked");
                Require(!File.Exists(store.SnapshotPath("must-stay-live")),
                    "the new source remained live after its durable Trash move committed");
                Require(Directory.EnumerateDirectories(store.SavedStateTrashRoot).Count() == 2,
                    "the locked oldest recovery was destroyed instead of allowing one bounded transient overflow");
            }

            Require(store.PurgeExpiredSavedStateTrashAsync().GetAwaiter().GetResult() == 1,
                "capacity maintenance did not reconcile the transient overflow after the old entry unlocked");
            Require(Directory.EnumerateDirectories(store.SavedStateTrashRoot).Count() == 1,
                "capacity maintenance left Trash above its configured bound");
            Require(!Directory.Exists(Path.Combine(store.SavedStateTrashRoot, oldReceipt.Id)),
                "capacity reconciliation did not evict the oldest recovery entry");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void ProviderCallCannotEnterDuringTheTrashCommit()
    {
        var root = TestRoot();
        var trashPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SavedStateDeletionReceipt?>? trashTask = null;
        try
        {
            var store = new SessionStore(
                root,
                TimeProvider.System,
                SessionStore.DefaultSavedStateTrashRetention,
                SessionStore.DefaultSavedStateTrashEntryLimit,
                savedStateTrashPreparedObserver: async (receipt, cancellationToken) =>
                {
                    if (!receipt.SessionId.Equals("provider-race-child", StringComparison.Ordinal))
                    {
                        return;
                    }

                    trashPrepared.TrySetResult(true);
                    await releaseTrash.Task.WaitAsync(cancellationToken);
                });
            SaveSession(store, "provider-race-source", "provider-race-source");
            var source = store.LoadSnapshotAsync("provider-race-source").GetAwaiter().GetResult()!;
            var sourceFingerprint = SessionStore.SetupFingerprint(source);
            var fork = store.ForkExperimentSessionAsync(
                    "provider-race-source",
                    "provider-race-child",
                    source.PersistenceRevision,
                    sourceFingerprint,
                    "experiment:provider-trash-race",
                    new ModelProviderConfig
                    {
                        BaseUrl = "http://127.0.0.1:1234/v1",
                        Model = "provider-race-model",
                        ApiToken = ""
                    })
                .GetAwaiter().GetResult();
            var guard = new ArenaExperimentChildGuard(
                fork.TargetSessionId,
                "experiment:provider-trash-race",
                fork.SourceSessionId,
                fork.SourcePersistenceRevision,
                sourceFingerprint,
                fork.ChildSetupFingerprint);

            trashTask = store.TrashSessionAsync(fork.TargetSessionId);
            Require(trashPrepared.Task.Wait(TimeSpan.FromSeconds(5)),
                "the deterministic Trash race did not reach its pre-move boundary");

            using (var providerWaitCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try
                {
                    using var lease = store.AcquireExperimentProviderCallLeaseAsync(
                        guard,
                        fork.TargetPersistenceRevision,
                        providerWaitCancellation.Token).AsTask().GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        "a provider call entered after the Trash tombstone but before the session move");
                }
                catch (OperationCanceledException) when (providerWaitCancellation.IsCancellationRequested)
                {
                }
            }

            releaseTrash.TrySetResult(true);
            var receipt = trashTask.GetAwaiter().GetResult();
            Require(receipt is not null, "the provider race Trash operation did not commit");
            try
            {
                using var lease = store.AcquireExperimentProviderCallLeaseAsync(
                    guard,
                    fork.TargetPersistenceRevision).AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException("a provider call validated after the Trash commit");
            }
            catch (ArenaExperimentChildDriftException)
            {
            }
            Require(!Directory.Exists(Path.GetDirectoryName(store.SnapshotPath(fork.TargetSessionId))!),
                "the provider race left the trashed child live");
        }
        finally
        {
            releaseTrash.TrySetResult(true);
            if (trashTask is not null)
            {
                try
                {
                    trashTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            DeleteTestRoot(root);
        }
    }

    private static void CheckpointUndoCannotRaceWholeSessionTrash()
    {
        var root = TestRoot();
        var trashPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SavedStateDeletionReceipt?>? trashTask = null;
        try
        {
            const string sessionId = "checkpoint-parent-race";
            var store = new SessionStore(
                root,
                TimeProvider.System,
                SessionStore.DefaultSavedStateTrashRetention,
                SessionStore.DefaultSavedStateTrashEntryLimit,
                savedStateTrashPreparedObserver: async (receipt, cancellationToken) =>
                {
                    if (receipt.Kind != SavedStateDeletionKind.Session
                        || !receipt.SessionId.Equals(sessionId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    trashPrepared.TrySetResult(true);
                    await releaseTrash.Task.WaitAsync(cancellationToken);
                });
            SaveSession(store, sessionId, "checkpoint-parent-race");
            var checkpoint = store.SaveCheckpointAsync(sessionId, "Recover after parent Undo")
                .GetAwaiter()
                .GetResult();
            var checkpointReceipt = store.TrashCheckpointAsync(sessionId, checkpoint.Id, checkpoint.Name)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("checkpoint race fixture did not enter Trash");
            var checkpointDirectory = Path.GetDirectoryName(checkpoint.Path)!;
            Require(!File.Exists(checkpoint.Path), "checkpoint race fixture remained live");
            Directory.Delete(checkpointDirectory);

            trashTask = store.TrashSessionAsync(sessionId);
            Require(trashPrepared.Task.Wait(TimeSpan.FromSeconds(5)),
                "whole-session Trash did not reach its deterministic pre-move boundary");

            using (var restoreCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try
                {
                    var racedRestore = store.RestoreDeletedStateAsync(
                            checkpointReceipt,
                            restoreCancellation.Token)
                        .GetAwaiter()
                        .GetResult();
                    throw new InvalidOperationException(
                        $"checkpoint Undo bypassed whole-session exclusion with {racedRestore.Status}");
                }
                catch (OperationCanceledException) when (restoreCancellation.IsCancellationRequested)
                {
                }
            }

            Require(!Directory.Exists(checkpointDirectory),
                "checkpoint Undo recreated a checkpoint tree while whole-session Trash owned exclusion");
            Require(File.Exists(Path.Combine(store.SavedStateTrashRoot, checkpointReceipt.Id, "payload.json")),
                "the cancelled checkpoint Undo lost its durable payload");

            releaseTrash.TrySetResult(true);
            var sessionReceipt = trashTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("whole-session Trash did not commit after checkpoint Undo cancelled");
            var unavailableCheckpoint = store.RestoreDeletedStateAsync(checkpointReceipt)
                .GetAwaiter()
                .GetResult();
            Require(unavailableCheckpoint.Status == SavedStateRestoreStatus.NotFound,
                $"checkpoint Undo without its live parent returned {unavailableCheckpoint.Status}");
            Require(!Directory.Exists(checkpointDirectory),
                "checkpoint Undo recreated an orphan checkpoint tree after whole-session Trash");

            Require(store.RestoreDeletedStateAsync(sessionReceipt).GetAwaiter().GetResult().Restored,
                "whole-session Undo was blocked by the checkpoint race");
            Require(store.RestoreDeletedStateAsync(checkpointReceipt).GetAwaiter().GetResult().Restored,
                "checkpoint Undo was not retryable after restoring its parent session");
            Require(File.Exists(checkpoint.Path),
                "retrying checkpoint Undo did not recover the original checkpoint");
        }
        finally
        {
            releaseTrash.TrySetResult(true);
            if (trashTask is not null)
            {
                try
                {
                    trashTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            DeleteTestRoot(root);
        }
    }

    private static void TrashedSessionIdentityRemainsReservedUntilUndoOrExpiry()
    {
        var root = TestRoot();
        try
        {
            const string sessionId = "draft-identity";
            var clock = new MutableTrashTimeProvider(
                new DateTimeOffset(2044, 5, 6, 7, 8, 9, TimeSpan.Zero));
            var store = new SessionStore(
                root,
                clock,
                TimeSpan.FromDays(7),
                SessionStore.DefaultSavedStateTrashEntryLimit);
            SaveSession(store, "fork-source", "fork-source");
            SaveSession(store, sessionId, "original-private-draft-owner");
            var staleOriginal = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
            var originalInstanceId = staleOriginal.SessionInstanceId;
            Require(SessionStore.IsValidSessionInstanceId(originalInstanceId),
                "the original persisted session did not receive an instance identity");
            var originalBytes = File.ReadAllBytes(store.SnapshotPath(sessionId));
            var receipt = store.TrashSessionAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("session identity reservation fixture did not enter Trash");

            var replacement = SessionStore.CreateDefaultSnapshot();
            replacement.Engine.Steering.Topic = "unrelated-replacement";
            Require(!store.TryCreateSessionAsync(sessionId, replacement).GetAwaiter().GetResult(),
                "TryCreateSessionAsync reused a session identity still reserved by Trash");
            try
            {
                store.SaveSnapshotAsync(replacement, sessionId).GetAwaiter().GetResult();
                throw new InvalidOperationException("SaveSnapshotAsync reused a session identity still reserved by Trash");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("reserved in Trash", StringComparison.Ordinal))
            {
            }

            var fork = store.ForkSessionAsync("fork-source", sessionId).GetAwaiter().GetResult();
            Require(!fork.TargetSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase),
                "fork creation reused an exact session identity still reserved by Trash");
            Require(!File.Exists(store.SnapshotPath(sessionId)),
                "a rejected identity reuse recreated the trashed live snapshot");

            var restored = store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult();
            Require(restored.Restored
                    && File.ReadAllBytes(store.SnapshotPath(sessionId)).SequenceEqual(originalBytes)
                    && store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!.SessionInstanceId == originalInstanceId,
                "identity reservation attempts changed or blocked the original session Undo");

            var expiringReceipt = store.TrashSessionAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("expiry reservation fixture did not re-enter Trash");
            clock.UtcNow = expiringReceipt.ExpiresAt.AddMilliseconds(1);
            Require(store.TryCreateSessionAsync(sessionId, replacement).GetAwaiter().GetResult(),
                "an expired Trash reservation did not release its session identity");
            var replacementInstanceId = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!.SessionInstanceId;
            Require(SessionStore.IsValidSessionInstanceId(replacementInstanceId)
                    && !replacementInstanceId.Equals(originalInstanceId, StringComparison.Ordinal),
                "reusing an expired display name reused the deleted session incarnation identity");
            staleOriginal.Engine.Steering.Topic = "stale process must not replace the new incarnation";
            try
            {
                store.SaveSnapshotAsync(staleOriginal, sessionId).GetAwaiter().GetResult();
                throw new InvalidOperationException(
                    "a stale same-revision snapshot overwrote a recreated session incarnation");
            }
            catch (SessionIdentityConflictException)
            {
            }
            var replacementAfterStaleSave = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
            Require(replacementAfterStaleSave.SessionInstanceId == replacementInstanceId
                    && replacementAfterStaleSave.Engine.Steering.Topic == "unrelated-replacement",
                "rejecting the stale incarnation changed the replacement session");
            var expiredUndo = store.RestoreDeletedStateAsync(expiringReceipt).GetAwaiter().GetResult();
            Require(expiredUndo.Status == SavedStateRestoreStatus.Expired
                    && File.Exists(store.SnapshotPath(sessionId)),
                "expired receipt cleanup collided with the permitted replacement session");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void SessionInstanceIdentityMigratesAndSurvivesCheckpointRestore()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            SaveSession(store, "legacy-instance", "legacy-instance");
            var snapshotPath = store.SnapshotPath("legacy-instance");
            var legacyJson = JsonNode.Parse(File.ReadAllText(snapshotPath))!.AsObject();
            legacyJson.Remove("session_instance_id");
            File.WriteAllText(snapshotPath, legacyJson.ToJsonString());

            var legacy = store.LoadSnapshotAsync("legacy-instance").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("legacy instance fixture was unreadable");
            var legacyRevision = legacy.PersistenceRevision;
            Require(legacy.SessionInstanceId.Length == 0, "legacy fixture retained an instance identity");

            var migratedId = store.EnsureSessionInstanceIdAsync("legacy-instance").GetAwaiter().GetResult();
            var migrated = store.LoadSnapshotAsync("legacy-instance").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("migrated instance fixture was unreadable");
            Require(SessionStore.IsValidSessionInstanceId(migratedId)
                    && migrated.SessionInstanceId == migratedId
                    && migrated.PersistenceRevision == legacyRevision + 1,
                "legacy identity migration was not one atomic authoritative snapshot revision");

            migrated.Engine.Steering.Topic = "ordinary save after migration";
            store.SaveSnapshotAsync(migrated, "legacy-instance").GetAwaiter().GetResult();
            var afterOrdinarySave = store.LoadSnapshotAsync("legacy-instance").GetAwaiter().GetResult()!;
            Require(afterOrdinarySave.SessionInstanceId == migratedId
                    && afterOrdinarySave.Engine.Steering.Topic == "ordinary save after migration",
                "the authoritative post-migration snapshot could not be saved normally");

            var checkpoint = store.SaveCheckpointAsync("legacy-instance", "identity checkpoint")
                .GetAwaiter().GetResult();
            var checkpointJson = JsonNode.Parse(File.ReadAllText(checkpoint.Path))!.AsObject();
            checkpointJson["snapshot"]!["session_instance_id"] = "22222222222222222222222222222222";
            File.WriteAllText(checkpoint.Path, checkpointJson.ToJsonString());

            afterOrdinarySave.Engine.Steering.Topic = "live identity owner";
            store.SaveSnapshotAsync(afterOrdinarySave, "legacy-instance").GetAwaiter().GetResult();
            var restored = store.RestoreCheckpointAsync("legacy-instance", checkpoint.Id).GetAwaiter().GetResult();
            Require(restored is not null, "identity checkpoint restore did not complete");
            var afterRestore = store.LoadSnapshotAsync("legacy-instance").GetAwaiter().GetResult()!;
            Require(afterRestore.SessionInstanceId == migratedId,
                "checkpoint restore adopted a historical checkpoint instance identity");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void GuardedQueuedEventsCannotOutliveTheirSession()
    {
        var root = TestRoot();
        using var guardEntered = new ManualResetEventSlim();
        using var releaseGuard = new ManualResetEventSlim();
        Task? appendTask = null;
        try
        {
            var store = new SessionStore(root);
            SaveSession(store, "queued-event", "queued-event");
            var observer = new EventLogWriteObserver
            {
                BeforeSessionGuard = () =>
                {
                    guardEntered.Set();
                    releaseGuard.Wait(TimeSpan.FromSeconds(5));
                }
            };
            var eventLog = new EventLogStore(root, observer, requireExistingSession: true);
            var eventDirectory = Path.GetDirectoryName(eventLog.EventPath("queued-event"))!;
            Require(!Directory.Exists(eventDirectory), "the event test did not start without a side-artifact directory");

            appendTask = eventLog.AppendAsync("queued-event", "queued_before_trash", new { ok = true });
            Require(guardEntered.Wait(TimeSpan.FromSeconds(5)),
                "the queued event did not reach its deterministic session-guard boundary");

            var receipt = store.TrashSessionAsync("queued-event").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("the queued-event session was not moved to Trash");
            Require(!Directory.Exists(eventDirectory),
                "a queued guarded event created its session side-artifact directory before acquiring exclusion");

            releaseGuard.Set();
            try
            {
                appendTask.GetAwaiter().GetResult();
                throw new InvalidOperationException("the guarded queued event appended after its session was trashed");
            }
            catch (DirectoryNotFoundException)
            {
            }

            Require(!Directory.Exists(eventDirectory),
                "a guarded event recreated side artifacts after its authoritative session disappeared");
            Require(store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult().Restored,
                "the failed queued event caused a collision while restoring the trashed session");

            var standalone = new EventLogStore(root);
            standalone.AppendAsync("standalone-only", "standalone_compatibility", new { ok = true })
                .GetAwaiter().GetResult();
            Require(File.Exists(standalone.EventPath("standalone-only")),
                "opt-in production guarding changed standalone event-log compatibility");
        }
        finally
        {
            releaseGuard.Set();
            if (appendTask is not null)
            {
                try
                {
                    appendTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            DeleteTestRoot(root);
        }
    }

    private static void GuardedAndStandaloneEventQueuesDoNotCoalesce()
    {
        var root = TestRoot();
        using var guardedQueueEntered = new ManualResetEventSlim();
        using var releaseGuardedQueue = new ManualResetEventSlim();
        Task? guardedAppend = null;
        try
        {
            var store = new SessionStore(root);
            SaveSession(store, "queue-policy", "queue-policy");
            var guarded = new EventLogStore(
                root,
                new EventLogWriteObserver
                {
                    BeforeSessionGuard = () =>
                    {
                        guardedQueueEntered.Set();
                        releaseGuardedQueue.Wait(TimeSpan.FromSeconds(5));
                    }
                },
                requireExistingSession: true);
            guardedAppend = guarded.AppendAsync("queue-policy", "guarded", new { ok = true });
            Require(guardedQueueEntered.Wait(TimeSpan.FromSeconds(5)),
                "the guarded queue isolation test did not reach its boundary");

            var standaloneAppend = new EventLogStore(root)
                .AppendAsync("queue-policy", "standalone", new { ok = true });
            Require(standaloneAppend.Wait(TimeSpan.FromSeconds(2)),
                "guarded and standalone writes coalesced under the guarded queue's owner policy");

            releaseGuardedQueue.Set();
            guardedAppend.GetAwaiter().GetResult();
        }
        finally
        {
            releaseGuardedQueue.Set();
            if (guardedAppend is not null)
            {
                try
                {
                    guardedAppend.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            DeleteTestRoot(root);
        }
    }

    private static SessionStore Store(string root, TimeProvider clock)
    {
        return new SessionStore(
            root,
            clock,
            SessionStore.DefaultSavedStateTrashRetention,
            SessionStore.DefaultSavedStateTrashEntryLimit);
    }

    private static void SaveSession(SessionStore store, string sessionId, string topic)
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Steering.Topic = topic;
        store.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-saved-state-trash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MutableTrashTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
