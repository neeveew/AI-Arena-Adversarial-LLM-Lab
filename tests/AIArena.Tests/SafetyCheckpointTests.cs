using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

internal static class SafetyCheckpointTests
{
    public static void CreatesAtomicSafetyCheckpointsBeforeDestructiveSnapshotReplacements()
    {
        ExactPreMutationStateIsRecoverable();
        MutationWaitsForDurableCheckpointCommit();
        BackupFailureAndCancellationLeaveLiveStateUntouched();
        ConcurrentReplacementsCheckpointEverySupersededRevision();
        CheckpointSaveSerializesWithWholeSessionTrash();
        CheckpointRestoreSerializesWithWholeSessionTrash();
        CheckpointRestoreAndLegacyEntryPointAreProtected();
        CheckpointRestoreUnprotectsSecretsExactlyOnce();
        CheckpointRestoreRejectsMismatchedIdentityBeforeMutation();
        CheckpointRestoreRejectsUnreadableLiveStateWithoutReplacement();
    }

    private static void ExactPreMutationStateIsRecoverable()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            var before = Snapshot("before", "BEFORE_SENTINEL");
            store.SaveSnapshotAsync(before, "exact").GetAwaiter().GetResult();
            var protectedRevision = before.PersistenceRevision;

            var receipt = store.MutateSnapshotWithSafetyCheckpointAsync(
                    "exact",
                    SnapshotSafetyCheckpointOperation.TemplateApply,
                    "Template One",
                    replacement =>
                    {
                        replacement.MatchType = "after";
                        replacement.Engine.Messages.Clear();
                        return replacement;
                    })
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("safety-checkpoint replacement returned no receipt");

            Require(receipt.Operation == SnapshotSafetyCheckpointOperation.TemplateApply,
                "safety receipt operation changed");
            Require(receipt.Subject == "Template One"
                    && receipt.Checkpoint.Name == "Safety before template apply: Template One",
                "template safety checkpoint naming should be deterministic");
            Require(receipt.ProtectedRevision == protectedRevision
                    && receipt.ReplacementRevision == protectedRevision + 1,
                "safety receipt should bind the protected and replacement revisions");

            var checkpoint = ReadCheckpoint(receipt.Checkpoint.Path);
            Require(checkpoint.Snapshot.PersistenceRevision == protectedRevision
                    && checkpoint.Snapshot.MatchType == "before"
                    && checkpoint.Snapshot.Engine.Messages.Single().Text == "BEFORE_SENTINEL",
                "automatic checkpoint should contain the exact authoritative pre-mutation state");
            var live = store.LoadSnapshotAsync("exact").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("replacement snapshot was not readable");
            Require(live.MatchType == "after"
                    && live.Engine.Messages.Count == 0
                    && live.PersistenceRevision == receipt.ReplacementRevision,
                "live snapshot should change only after the safety checkpoint commits");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void MutationWaitsForDurableCheckpointCommit()
    {
        var root = TestRoot();
        using var durableCommitReached = new ManualResetEventSlim();
        using var releaseDurableCommit = new ManualResetEventSlim();
        string? durableCheckpointPath = null;
        try
        {
            var store = new SessionStore(
                root,
                TimeProvider.System,
                SessionStore.DefaultSavedStateTrashRetention,
                SessionStore.DefaultSavedStateTrashEntryLimit,
                (path, cancellationToken) =>
                {
                    durableCheckpointPath = path;
                    durableCommitReached.Set();
                    releaseDurableCommit.Wait(cancellationToken);
                    return Task.CompletedTask;
                });
            store.SaveSnapshotAsync(Snapshot("durable-before", "DURABLE_BEFORE_SENTINEL"), "durable")
                .GetAwaiter()
                .GetResult();
            var mutationCalled = false;
            var mutationTask = Task.Run(() => store.MutateSnapshotWithSafetyCheckpointAsync(
                "durable",
                SnapshotSafetyCheckpointOperation.ArenaReset,
                null,
                replacement =>
                {
                    mutationCalled = true;
                    replacement.MatchType = "durable-after";
                    return replacement;
                }));

            Require(durableCommitReached.Wait(TimeSpan.FromSeconds(10)),
                "automatic checkpoint did not reach the durable-commit boundary");
            Require(!mutationCalled && !mutationTask.IsCompleted,
                "destructive mutation ran before the durable checkpoint commit completed");
            Require(!string.IsNullOrWhiteSpace(durableCheckpointPath)
                    && File.Exists(durableCheckpointPath)
                    && ReadCheckpoint(durableCheckpointPath).Snapshot.MatchType == "durable-before",
                "durable-commit boundary should expose the complete authoritative checkpoint at its final path");
            var liveBeforeRelease = store.LoadSnapshotAsync("durable").GetAwaiter().GetResult();
            Require(liveBeforeRelease?.MatchType == "durable-before"
                    && liveBeforeRelease.Engine.Messages.Single().Text == "DURABLE_BEFORE_SENTINEL",
                "live state changed while durable checkpoint completion was still blocked");

            releaseDurableCommit.Set();
            var receipt = mutationTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("durable checkpoint mutation returned no receipt");
            Require(mutationCalled
                    && receipt.Checkpoint.Path == durableCheckpointPath
                    && store.LoadSnapshotAsync("durable").GetAwaiter().GetResult()?.MatchType == "durable-after",
                "mutation should proceed only after the durable checkpoint completion boundary releases");
        }
        finally
        {
            releaseDurableCommit.Set();
            DeleteTestRoot(root);
        }
    }

    private static void BackupFailureAndCancellationLeaveLiveStateUntouched()
    {
        var failureRoot = TestRoot();
        try
        {
            var store = new SessionStore(failureRoot);
            store.SaveSnapshotAsync(Snapshot("failure", "FAILURE_SENTINEL"), "failure")
                .GetAwaiter()
                .GetResult();
            var checkpointDirectory = store.CheckpointDirectory("failure");
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointDirectory)!);
            File.WriteAllText(checkpointDirectory, "block checkpoint directory creation");
            var mutationCalled = false;

            Exception? failure = null;
            try
            {
                _ = store.MutateSnapshotWithSafetyCheckpointAsync(
                        "failure",
                        SnapshotSafetyCheckpointOperation.ArenaReset,
                        null,
                        replacement =>
                        {
                            mutationCalled = true;
                            replacement.MatchType = "must-not-save";
                            return replacement;
                        })
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }

            Require(failure is not null && !mutationCalled,
                "checkpoint write failure should abort before invoking destructive mutation");
            var unchanged = store.LoadSnapshotAsync("failure").GetAwaiter().GetResult();
            Require(unchanged?.MatchType == "failure"
                    && unchanged.Engine.Messages.Single().Text == "FAILURE_SENTINEL",
                "checkpoint write failure should leave live state untouched");
        }
        finally
        {
            DeleteTestRoot(failureRoot);
        }

        var cancellationRoot = TestRoot();
        try
        {
            var store = new SessionStore(cancellationRoot);
            store.SaveSnapshotAsync(Snapshot("cancel", "CANCEL_SENTINEL"), "cancel")
                .GetAwaiter()
                .GetResult();
            using var heldSnapshotLease = CrossProcessWriteLease.AcquireAsync(
                    store.SnapshotPath("cancel"),
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var mutationCalled = false;
            try
            {
                _ = store.MutateSnapshotWithSafetyCheckpointAsync(
                        "cancel",
                        SnapshotSafetyCheckpointOperation.ArenaReset,
                        null,
                        replacement =>
                        {
                            mutationCalled = true;
                            return replacement;
                        },
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("cancelled safety-checkpoint mutation unexpectedly completed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            Require(!mutationCalled, "cancellation while waiting for the mutation boundary should not invoke mutation");
            Require(store.ListCheckpointsAsync("cancel").GetAwaiter().GetResult().Count == 0,
                "pre-commit cancellation should not create a checkpoint");
            var unchanged = store.LoadSnapshotAsync("cancel").GetAwaiter().GetResult();
            Require(unchanged?.MatchType == "cancel"
                    && unchanged.Engine.Messages.Single().Text == "CANCEL_SENTINEL",
                "pre-commit cancellation should leave live state untouched");
        }
        finally
        {
            DeleteTestRoot(cancellationRoot);
        }
    }

    private static void ConcurrentReplacementsCheckpointEverySupersededRevision()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(Snapshot("base", "BASE_SENTINEL"), "concurrent")
                .GetAwaiter()
                .GetResult();
            using var firstEntered = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            var firstTask = Task.Run(async () => await store.MutateSnapshotWithSafetyCheckpointAsync(
                "concurrent",
                SnapshotSafetyCheckpointOperation.TemplateApply,
                "First",
                replacement =>
                {
                    firstEntered.Set();
                    if (!releaseFirst.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("concurrent safety-checkpoint fixture was not released");
                    }

                    replacement.MatchType = "first";
                    replacement.Engine.Messages.Add(Message(2, "FIRST_COMMITTED_SENTINEL"));
                    return replacement;
                }));
            Require(firstEntered.Wait(TimeSpan.FromSeconds(10)),
                "first safety-checkpoint mutation did not reach its protected mutation delegate");

            var secondTask = Task.Run(async () => await store.MutateSnapshotWithSafetyCheckpointAsync(
                "concurrent",
                SnapshotSafetyCheckpointOperation.ArenaReset,
                null,
                replacement =>
                {
                    replacement.MatchType = "second";
                    replacement.Engine.Messages.Clear();
                    return replacement;
                }));
            Require(!secondTask.Wait(TimeSpan.FromMilliseconds(150)),
                "a concurrent replacement bypassed the first operation's coherence boundary");
            releaseFirst.Set();

            var first = firstTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("first concurrent replacement returned no receipt");
            var second = secondTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("second concurrent replacement returned no receipt");
            Require(second.ProtectedRevision == first.ReplacementRevision,
                "later replacement should protect the exact revision committed by the earlier mutation");
            var secondSafety = ReadCheckpoint(second.Checkpoint.Path);
            Require(secondSafety.Snapshot.MatchType == "first"
                    && secondSafety.Snapshot.Engine.Messages.Any(message => message.Text == "FIRST_COMMITTED_SENTINEL"),
                "a later destructive replacement must checkpoint newer state before superseding it");
            var final = store.LoadSnapshotAsync("concurrent").GetAwaiter().GetResult();
            Require(final?.MatchType == "second" && final.Engine.Messages.Count == 0,
                "concurrent replacements should serialize to the later requested result");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CheckpointSaveSerializesWithWholeSessionTrash()
    {
        var root = TestRoot();
        var checkpointDurable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckpoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trashPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CheckpointSummary>? checkpointTask = null;
        Task<SavedStateDeletionReceipt?>? trashTask = null;
        try
        {
            const string sessionId = "checkpoint-save-trash-race";
            var store = new SessionStore(
                root,
                TimeProvider.System,
                SessionStore.DefaultSavedStateTrashRetention,
                SessionStore.DefaultSavedStateTrashEntryLimit,
                checkpointDurableCommitObserver: async (path, cancellationToken) =>
                {
                    if (!string.Equals(
                            Path.GetFileName(Path.GetDirectoryName(path)),
                            sessionId,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    checkpointDurable.TrySetResult(true);
                    await releaseCheckpoint.Task.WaitAsync(cancellationToken);
                },
                savedStateTrashPreparedObserver: async (receipt, cancellationToken) =>
                {
                    if (!receipt.SessionId.Equals(sessionId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    trashPrepared.TrySetResult(true);
                    await releaseTrash.Task.WaitAsync(cancellationToken);
                });
            store.SaveSnapshotAsync(Snapshot("save-race", "SAVE_RACE_SENTINEL"), sessionId)
                .GetAwaiter()
                .GetResult();

            checkpointTask = store.SaveCheckpointAsync(sessionId, "Save wins before Trash");
            Require(checkpointDurable.Task.Wait(TimeSpan.FromSeconds(5)),
                "checkpoint save did not reach its deterministic durable boundary");
            trashTask = store.TrashSessionAsync(sessionId);
            Require(!trashPrepared.Task.Wait(TimeSpan.FromMilliseconds(300)),
                "whole-session Trash entered while checkpoint save still owned its commit boundary");

            releaseCheckpoint.TrySetResult(true);
            var checkpoint = checkpointTask.GetAwaiter().GetResult();
            Require(trashPrepared.Task.Wait(TimeSpan.FromSeconds(5)),
                "whole-session Trash did not enter after checkpoint save released exclusion");
            releaseTrash.TrySetResult(true);
            var receipt = trashTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("whole-session Trash did not commit after checkpoint save");
            Require(store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult().Restored,
                "checkpoint save created a collision that blocked whole-session Undo");
            Require(store.ListCheckpointsAsync(sessionId).GetAwaiter().GetResult()
                    .Any(candidate => candidate.Id.Equals(checkpoint.Id, StringComparison.OrdinalIgnoreCase)),
                "the serialized checkpoint save was not durable after whole-session Undo");
        }
        finally
        {
            releaseCheckpoint.TrySetResult(true);
            releaseTrash.TrySetResult(true);
            try
            {
                checkpointTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            try
            {
                trashTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            DeleteTestRoot(root);
        }
    }

    private static void CheckpointRestoreSerializesWithWholeSessionTrash()
    {
        var root = TestRoot();
        var previousProtector = SessionStore.ProtectSecret;
        var previousUnprotector = SessionStore.UnprotectSecret;
        using var restoreProjectionReached = new ManualResetEventSlim();
        using var releaseRestoreProjection = new ManualResetEventSlim();
        var trashPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CheckpointRestoreWithSafetyResult?>? restoreTask = null;
        Task<SavedStateDeletionReceipt?>? trashTask = null;
        const string targetSecret = "checkpoint-restore-trash-race-secret";
        try
        {
            const string sessionId = "checkpoint-restore-trash-race";
            SessionStore.ProtectSecret = static value => value;
            SessionStore.UnprotectSecret = static value => value;
            var store = new SessionStore(
                root,
                TimeProvider.System,
                SessionStore.DefaultSavedStateTrashRetention,
                SessionStore.DefaultSavedStateTrashEntryLimit,
                savedStateTrashPreparedObserver: async (receipt, cancellationToken) =>
                {
                    if (!receipt.SessionId.Equals(sessionId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    trashPrepared.TrySetResult(true);
                    await releaseTrash.Task.WaitAsync(cancellationToken);
                });
            var target = Snapshot("restore-race-target", "RESTORE_RACE_TARGET_SENTINEL");
            target.Configs["shared"] = WithApiToken(target.Configs["shared"], targetSecret);
            store.SaveSnapshotAsync(target, sessionId).GetAwaiter().GetResult();
            var checkpoint = store.SaveCheckpointAsync(sessionId, "Restore wins before Trash")
                .GetAwaiter()
                .GetResult();
            var newer = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("checkpoint restore race source snapshot was unavailable");
            newer.MatchType = "restore-race-newer";
            newer.Engine.Messages.Clear();
            newer.Engine.Messages.Add(Message(2, "RESTORE_RACE_NEWER_SENTINEL"));
            newer.Engine.TurnCount = 2;
            newer.Configs["shared"] = WithApiToken(newer.Configs["shared"], "newer-race-secret");
            store.SaveSnapshotAsync(newer, sessionId).GetAwaiter().GetResult();

            SessionStore.UnprotectSecret = value =>
            {
                if (value.Equals(targetSecret, StringComparison.Ordinal))
                {
                    restoreProjectionReached.Set();
                    if (!releaseRestoreProjection.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("checkpoint restore projection fixture was not released");
                    }
                }

                return value;
            };
            restoreTask = Task.Run(() => store.RestoreCheckpointWithSafetyCheckpointAsync(sessionId, checkpoint.Id)
                .GetAwaiter()
                .GetResult());
            Require(restoreProjectionReached.Wait(TimeSpan.FromSeconds(5)),
                "checkpoint restore did not reach its deterministic projection boundary");
            trashTask = store.TrashSessionAsync(sessionId);
            Require(!trashPrepared.Task.Wait(TimeSpan.FromMilliseconds(300)),
                "whole-session Trash entered while checkpoint restore still owned its projection boundary");

            releaseRestoreProjection.Set();
            var restored = restoreTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("checkpoint restore race returned no result");
            Require(restored.RestoredCheckpoint.Id.Equals(checkpoint.Id, StringComparison.OrdinalIgnoreCase),
                "checkpoint restore race applied the wrong checkpoint");
            Require(trashPrepared.Task.Wait(TimeSpan.FromSeconds(5)),
                "whole-session Trash did not enter after checkpoint restore released exclusion");
            releaseTrash.TrySetResult(true);
            var receipt = trashTask.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("whole-session Trash did not commit after checkpoint restore");
            Require(store.RestoreDeletedStateAsync(receipt).GetAwaiter().GetResult().Restored,
                "checkpoint restore created a collision that blocked whole-session Undo");
            var recovered = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult();
            Require(recovered?.MatchType == "restore-race-target"
                    && recovered.Engine.Messages.Single().Text == "RESTORE_RACE_TARGET_SENTINEL",
                "whole-session Undo did not recover the checkpoint-restored revision");
        }
        finally
        {
            releaseRestoreProjection.Set();
            releaseTrash.TrySetResult(true);
            try
            {
                restoreTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            try
            {
                trashTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            SessionStore.ProtectSecret = previousProtector;
            SessionStore.UnprotectSecret = previousUnprotector;
            DeleteTestRoot(root);
        }
    }

    private static void CheckpointRestoreAndLegacyEntryPointAreProtected()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            var targetSnapshot = Snapshot("target", "TARGET_SENTINEL");
            store.SaveSnapshotAsync(targetSnapshot, "restore").GetAwaiter().GetResult();
            var target = store.SaveCheckpointAsync("restore", "Restore Target").GetAwaiter().GetResult();

            var newer = store.LoadSnapshotAsync("restore").GetAwaiter().GetResult()!;
            newer.MatchType = "newer";
            newer.Engine.Messages.Add(Message(2, "NEWER_SENTINEL"));
            store.SaveSnapshotAsync(newer, "restore").GetAwaiter().GetResult();
            var protectedRestore = store.RestoreCheckpointWithSafetyCheckpointAsync("restore", target.Id)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("protected checkpoint restore failed");
            var restoreReceipt = protectedRestore.SafetyCheckpoint
                ?? throw new InvalidOperationException("live checkpoint restore did not create a safety checkpoint");
            Require(protectedRestore.RestoredCheckpoint.Id == target.Id
                    && restoreReceipt.Checkpoint.Name == "Safety before checkpoint restore: Restore Target",
                "checkpoint restore should return both selected and deterministic safety receipts");
            var restoreSafety = ReadCheckpoint(restoreReceipt.Checkpoint.Path);
            Require(restoreSafety.Snapshot.MatchType == "newer"
                    && restoreSafety.Snapshot.Engine.Messages.Any(message => message.Text == "NEWER_SENTINEL"),
                "checkpoint restore should preserve the exact newer live state it replaces");
            Require(store.LoadSnapshotAsync("restore").GetAwaiter().GetResult()?.MatchType == "target",
                "checkpoint restore target was not applied");

            var checkpointCount = store.ListCheckpointsAsync("restore").GetAwaiter().GetResult().Count;
            var legacy = store.RestoreCheckpointAsync("restore", target.Id).GetAwaiter().GetResult();
            Require(legacy?.Id == target.Id,
                "legacy checkpoint restore entry point should retain its established result");
            Require(store.ListCheckpointsAsync("restore").GetAwaiter().GetResult().Count == checkpointCount + 1,
                "legacy checkpoint restore entry point should delegate through automatic safety protection");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CheckpointRestoreUnprotectsSecretsExactlyOnce()
    {
        var root = TestRoot();
        var previousProtector = SessionStore.ProtectSecret;
        var previousUnprotector = SessionStore.UnprotectSecret;
        const string secret = "restore-secret";
        const string newerSecret = "newer-secret";
        try
        {
            SessionStore.ProtectSecret = value => $"protected[{value}]";
            SessionStore.UnprotectSecret = value =>
                value.StartsWith("protected[", StringComparison.Ordinal)
                && value.EndsWith(']')
                    ? value[10..^1]
                    : value;

            var store = new SessionStore(root);
            var target = Snapshot("secret-target", "SECRET_TARGET_SENTINEL");
            target.Configs["shared"] = WithApiToken(target.Configs["shared"], secret);
            store.SaveSnapshotAsync(target, "secret-restore").GetAwaiter().GetResult();
            var checkpoint = store.SaveCheckpointAsync("secret-restore", "Secret Target")
                .GetAwaiter()
                .GetResult();

            var newer = store.LoadSnapshotAsync("secret-restore").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("newer secret fixture did not load");
            newer.MatchType = "secret-newer";
            newer.Configs["shared"] = WithApiToken(newer.Configs["shared"], newerSecret);
            store.SaveSnapshotAsync(newer, "secret-restore").GetAwaiter().GetResult();

            var restored = store.RestoreCheckpointWithSafetyCheckpointAsync("secret-restore", checkpoint.Id)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("protected secret checkpoint did not restore");
            Require(restored.SafetyCheckpoint is not null,
                "restoring over live secret state should create a safety checkpoint");
            var loaded = store.LoadSnapshotAsync("secret-restore").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("restored secret snapshot did not load");
            Require(loaded.Configs["shared"].ApiToken == secret,
                "checkpoint restore should expose the original plaintext token after one unprotect pass");
            var rawLive = File.ReadAllText(store.SnapshotPath("secret-restore"));
            Require(rawLive.Contains("protected[restore-secret]", StringComparison.Ordinal)
                    && !rawLive.Contains("protected[protected[restore-secret]]", StringComparison.Ordinal),
                "checkpoint restore should protect the restored token exactly once on disk");

            var checkpointCountBeforeTransformFailure = store.ListCheckpointsAsync("secret-restore")
                .GetAwaiter()
                .GetResult()
                .Count;
            var revisionBeforeTransformFailure = loaded.PersistenceRevision;
            var workingUnprotector = SessionStore.UnprotectSecret;
            var transformFailed = false;
            SessionStore.UnprotectSecret = _ => throw new InvalidOperationException("simulated checkpoint unprotect failure");
            try
            {
                _ = store.RestoreCheckpointWithSafetyCheckpointAsync("secret-restore", checkpoint.Id)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("simulated checkpoint", StringComparison.Ordinal))
            {
                transformFailed = true;
            }
            finally
            {
                SessionStore.UnprotectSecret = workingUnprotector;
            }

            Require(transformFailed
                    && File.ReadAllText(store.SnapshotPath("secret-restore")) == rawLive
                    && store.ListCheckpointsAsync("secret-restore").GetAwaiter().GetResult().Count
                        == checkpointCountBeforeTransformFailure
                    && store.LoadSnapshotAsync("secret-restore").GetAwaiter().GetResult()?.PersistenceRevision
                        == revisionBeforeTransformFailure,
                "checkpoint secret-transform failure should occur before mutation and leave live state and checkpoint inventory untouched");

            File.Delete(store.SnapshotPath("secret-restore"));
            var restoredWithoutLive = store.RestoreCheckpointWithSafetyCheckpointAsync(
                    "secret-restore",
                    checkpoint.Id)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("snapshot-less secret checkpoint did not restore");
            Require(restoredWithoutLive.SafetyCheckpoint is null,
                "snapshot-less restore should not invent a superseded live state");
            var loadedWithoutLive = store.LoadSnapshotAsync("secret-restore").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("snapshot-less secret restore did not load");
            Require(loadedWithoutLive.Configs["shared"].ApiToken == secret
                    && !File.ReadAllText(store.SnapshotPath("secret-restore"))
                        .Contains("protected[protected[restore-secret]]", StringComparison.Ordinal),
                "snapshot-less checkpoint restore should also round-trip a non-idempotent protector exactly once");
        }
        finally
        {
            SessionStore.ProtectSecret = previousProtector;
            SessionStore.UnprotectSecret = previousUnprotector;
            DeleteTestRoot(root);
        }
    }

    private static void CheckpointRestoreRejectsMismatchedIdentityBeforeMutation()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(Snapshot("identity-live", "IDENTITY_LIVE_SENTINEL"), "identity")
                .GetAwaiter()
                .GetResult();
            var checkpoint = store.SaveCheckpointAsync("identity", "Identity Target")
                .GetAwaiter()
                .GetResult();
            var originalJson = File.ReadAllText(checkpoint.Path);
            var original = ReadCheckpoint(checkpoint.Path);
            var initialCount = store.ListCheckpointsAsync("identity").GetAwaiter().GetResult().Count;
            var initialRevision = store.LoadSnapshotAsync("identity").GetAwaiter().GetResult()!.PersistenceRevision;

            WriteCheckpoint(checkpoint.Path, new CheckpointRecord
            {
                Id = "forged-id",
                Name = original.Name,
                SessionId = original.SessionId,
                AppVersion = original.AppVersion,
                CreatedAt = original.CreatedAt,
                Snapshot = original.Snapshot
            });
            Require(store.RestoreCheckpointWithSafetyCheckpointAsync("identity", checkpoint.Id)
                    .GetAwaiter()
                    .GetResult() is null,
                "checkpoint restore should reject a record whose id does not own the requested file");

            WriteCheckpoint(checkpoint.Path, new CheckpointRecord
            {
                Id = checkpoint.Id,
                Name = original.Name,
                SessionId = "another-session",
                AppVersion = original.AppVersion,
                CreatedAt = original.CreatedAt,
                Snapshot = original.Snapshot
            });
            Require(store.RestoreCheckpointWithSafetyCheckpointAsync("identity", checkpoint.Id)
                    .GetAwaiter()
                    .GetResult() is null,
                "checkpoint restore should reject a record owned by another session");

            WriteCheckpoint(checkpoint.Path, new CheckpointRecord
            {
                Id = checkpoint.Id,
                Name = original.Name,
                SessionId = original.SessionId,
                AppVersion = original.AppVersion,
                CreatedAt = long.MinValue,
                Snapshot = original.Snapshot
            });
            Require(store.RestoreCheckpointWithSafetyCheckpointAsync("identity", checkpoint.Id)
                    .GetAwaiter()
                    .GetResult() is null,
                "checkpoint restore should reject impossible checkpoint timestamps");

            var live = store.LoadSnapshotAsync("identity").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("identity fixture live state became unreadable");
            Require(live.MatchType == "identity-live"
                    && live.Engine.Messages.Single().Text == "IDENTITY_LIVE_SENTINEL"
                    && live.PersistenceRevision == initialRevision,
                "mismatched checkpoint identity should leave live state untouched");
            Require(store.ListCheckpointsAsync("identity").GetAwaiter().GetResult().Count == initialCount,
                "mismatched checkpoint identity should not create an automatic checkpoint");
            File.WriteAllText(checkpoint.Path, originalJson);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CheckpointRestoreRejectsUnreadableLiveStateWithoutReplacement()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(Snapshot("corrupt-live-target", "CORRUPT_LIVE_TARGET"), "corrupt-live")
                .GetAwaiter()
                .GetResult();
            var checkpoint = store.SaveCheckpointAsync("corrupt-live", "Corrupt Live Target")
                .GetAwaiter()
                .GetResult();
            const string corruptLive = "{ this is present live state but not a readable snapshot";
            File.WriteAllText(store.SnapshotPath("corrupt-live"), corruptLive);
            var checkpointCount = store.ListCheckpointsAsync("corrupt-live").GetAwaiter().GetResult().Count;

            var result = store.RestoreCheckpointWithSafetyCheckpointAsync("corrupt-live", checkpoint.Id)
                .GetAwaiter()
                .GetResult();
            Require(result is null
                    && File.ReadAllText(store.SnapshotPath("corrupt-live")) == corruptLive
                    && store.ListCheckpointsAsync("corrupt-live").GetAwaiter().GetResult().Count == checkpointCount,
                "an unreadable but present live snapshot must not be replaced without an exact safety checkpoint");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static ArenaSnapshot Snapshot(string matchType, string sentinel)
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.MatchType = matchType;
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.Messages.Add(Message(1, sentinel));
        snapshot.Engine.TurnCount = 1;
        return snapshot;
    }

    private static DialogueMessage Message(int turn, string text)
    {
        return new DialogueMessage
        {
            Turn = turn,
            Speaker = "Operator",
            SpeakerId = "operator",
            Kind = "message",
            Status = "ok",
            Text = text,
            CreatedAt = turn
        };
    }

    private static ModelProviderConfig WithApiToken(ModelProviderConfig config, string apiToken)
    {
        return new ModelProviderConfig
        {
            BaseUrl = config.BaseUrl,
            ApiMode = config.ApiMode,
            ApiToken = apiToken,
            Model = config.Model,
            ExplicitModelAssignment = config.ExplicitModelAssignment,
            Timeout = config.Timeout,
            Temperature = config.Temperature,
            MaxOutputTokens = config.MaxOutputTokens,
            ContextLength = config.ContextLength,
            ConfiguredContextWindow = config.ConfiguredContextWindow,
            HistoryPolicy = config.HistoryPolicy,
            ResponseTone = config.ResponseTone,
            CustomTone = config.CustomTone,
            Reasoning = config.Reasoning,
            NativeStatefulChat = config.NativeStatefulChat,
            NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
            PreviousResponseId = config.PreviousResponseId,
            LastError = config.LastError,
            LastLatencyMs = config.LastLatencyMs,
            LastTestOk = config.LastTestOk,
            Extra = config.Extra
        };
    }

    private static CheckpointRecord ReadCheckpoint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<CheckpointRecord>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException($"checkpoint was unreadable: {path}");
    }

    private static void WriteCheckpoint(string path, CheckpointRecord record)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(record));
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-safety-checkpoint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
