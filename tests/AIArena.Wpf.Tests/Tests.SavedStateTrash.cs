using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Text.Json.Nodes;
using System.Windows.Controls;

internal static partial class Program
{
    static void SavedStateTrashUndoPresentation()
    {
        Require(
            SavedStateWorkflowCoordinator.PreferredSessionAfterDelete("active", "ACTIVE") == "default",
            "deleting the active session should fall back to default");
        Require(
            SavedStateWorkflowCoordinator.PreferredSessionAfterDelete("other", "active") == "active",
            "deleting a non-active session should preserve the active session");
        Require(
            SavedStateWorkflowCoordinator.PreferredSessionAfterDelete("other", null) == "default",
            "missing active session should fall back to default");
        Require(
            SavedStateWorkflowCoordinator.PreferredSessionAfterUndo("deleted-active", deletedSessionWasActive: true, "default") == "deleted-active",
            "Undo should reopen a session that was active when deleted");
        Require(
            SavedStateWorkflowCoordinator.PreferredSessionAfterUndo("deleted-other", deletedSessionWasActive: false, "still-active") == "still-active",
            "Undo of a non-active session should preserve the active session");

        var receipt = new SavedStateDeletionReceipt(
            "0123456789abcdef0123456789abcdef",
            SavedStateDeletionKind.Checkpoint,
            "default",
            "checkpoint-id",
            "Before risky turn",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(7));
        var undo = SavedStateWorkflowCoordinator.DeleteActionPresentation(
            idle: true,
            hasSelection: false,
            selectedDefaultSession: true,
            receipt);
        Require(undo.Content == "Undo" && undo.Enabled, "pending deletion should expose an enabled Undo without a picker selection");
        Require(undo.HelpText.Contains("checkpoint Before risky turn", StringComparison.Ordinal), "Undo help should identify the recoverable item");
        Require(undo.AutomationName == "Undo saved item deletion", "Undo should expose a distinct automation name");
        Require(SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(receipt), "a second Delete invocation should route a pending receipt to restore");
        Require(!SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(receipt, pendingUndoArmed: false),
            "selecting another item should release Delete from an unrelated pending Undo");
        Require(!SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(null), "Delete without a pending receipt should retain normal delete routing");
        var busyUndo = SavedStateWorkflowCoordinator.DeleteActionPresentation(
            idle: false,
            hasSelection: true,
            selectedDefaultSession: false,
            receipt);
        Require(!busyUndo.Enabled, "Undo should remain disabled while the arena is busy");

        var protectedDefault = SavedStateWorkflowCoordinator.DeleteActionPresentation(
            idle: true,
            hasSelection: true,
            selectedDefaultSession: true,
            pendingReceipt: null);
        Require(protectedDefault.Content == "Delete" && !protectedDefault.Enabled, "default session delete protection changed");
        Require(protectedDefault.HelpText == "Default session cannot be deleted.", "default session protection lost its explanation");

        var restored = SavedStateWorkflowCoordinator.UndoStatus(
            new SavedStateRestoreResult(SavedStateRestoreStatus.Restored, receipt));
        var collision = SavedStateWorkflowCoordinator.UndoStatus(
            new SavedStateRestoreResult(SavedStateRestoreStatus.NameCollision, receipt));
        var expired = SavedStateWorkflowCoordinator.UndoStatus(
            new SavedStateRestoreResult(SavedStateRestoreStatus.Expired, receipt));
        Require(restored == "Restored checkpoint Before risky turn from Trash.", "restored Undo status changed");
        Require(collision.Contains("already exists", StringComparison.Ordinal), "collision status does not tell the operator how restore was blocked");
        Require(expired.Contains("retention period expired", StringComparison.Ordinal), "expiration status is not explicit");
        Require(
            SavedStateWorkflowCoordinator.TrashRetentionDisclosure
                == "Trash keeps up to 64 items for at most seven days.",
            "Trash confirmation must disclose both capacity and maximum retention");

        DeleteRoutingAllowsAnotherItemBeforeExplicitUndo();
        CommittedTrashAndUndoSurviveLockedEventEvidence();
        TrashedSessionIdentityKeepsItsComposerDraftIsolated();
    }

    private static void DeleteRoutingAllowsAnotherItemBeforeExplicitUndo()
    {
        var root = SavedStateTestRoot("delete-routing");
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "default").GetAwaiter().GetResult();
            var first = store.SaveCheckpointAsync("default", "First pending delete").GetAwaiter().GetResult();
            var second = store.SaveCheckpointAsync("default", "Second selected delete").GetAwaiter().GetResult();
            var firstReceipt = store.TrashCheckpointAsync("default", first.Id, first.Name).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("first checkpoint did not enter Trash");
            Require(SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(firstReceipt, pendingUndoArmed: true),
                "the immediate explicit Undo path was not armed after the first delete");

            var pendingUndoArmed = false; // User selected the unrelated second row.
            Require(!SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(firstReceipt, pendingUndoArmed),
                "selecting the second row still routed Delete to the first row's Undo");
            var secondReceipt = store.TrashCheckpointAsync("default", second.Id, second.Name).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("second checkpoint was not deletable while the first remained in Trash");
            var pending = store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult();
            Require(pending.Count == 2
                    && pending[0].Id.Equals(secondReceipt.Id, StringComparison.OrdinalIgnoreCase)
                    && pending[1].Id.Equals(firstReceipt.Id, StringComparison.OrdinalIgnoreCase),
                "deleting the second row replaced or restored the first durable Trash receipt");

            Require(store.RestoreDeletedStateAsync(secondReceipt).GetAwaiter().GetResult().Restored,
                "the explicit Undo path could not restore the newest deletion");
            var rehydrated = store.ListRestorableDeletedStatesAsync().GetAwaiter().GetResult();
            Require(rehydrated.Count == 1
                    && rehydrated[0].Id.Equals(firstReceipt.Id, StringComparison.OrdinalIgnoreCase)
                    && SavedStateWorkflowCoordinator.ShouldUndoDeletionOnDelete(rehydrated[0], pendingUndoArmed: true),
                "rehydration did not expose explicit Undo for the older deletion");
            Require(store.RestoreDeletedStateAsync(firstReceipt).GetAwaiter().GetResult().Restored,
                "the older explicit Undo was no longer accessible after undoing the newer item");
        }
        finally
        {
            DeleteSavedStateTestRoot(root);
        }
    }

    private static void CommittedTrashAndUndoSurviveLockedEventEvidence()
    {
        var root = SavedStateTestRoot("locked-evidence");
        try
        {
            var store = new SessionStore(root);
            var events = new EventLogStore(root);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "default").GetAwaiter().GetResult();
            var checkpoint = store.SaveCheckpointAsync("default", "Locked evidence checkpoint")
                .GetAwaiter()
                .GetResult();
            var eventPath = events.EventPath("default");
            Directory.CreateDirectory(Path.GetDirectoryName(eventPath)!);
            File.WriteAllText(eventPath, "");
            using (File.Open(eventPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var deleteRefreshCalls = 0;
                var deleted = SavedStateWorkflowCoordinator.TrashCheckpointAndReportAsync(
                        store,
                        events,
                        "default",
                        checkpoint.Id,
                        checkpoint.Name,
                        async (_, token) =>
                        {
                            Require(!token.CanBeCanceled,
                                "committed checkpoint delete refresh inherited caller cancellation");
                            deleteRefreshCalls++;
                            var listed = await store.ListCheckpointsAsync("default", token);
                            Require(listed.All(item => !item.Id.Equals(checkpoint.Id, StringComparison.OrdinalIgnoreCase)),
                                "checkpoint delete refresh still projected the committed checkpoint");
                        })
                    .GetAwaiter()
                    .GetResult();
                Require(deleted.Receipt is not null
                        && !deleted.EventRecorded
                        && deleteRefreshCalls == 1
                        && !File.Exists(checkpoint.Path)
                        && deleted.Outcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase)
                        && deleted.Outcome.Contains("AA-SAVED-", StringComparison.Ordinal)
                        && !deleted.Outcome.Contains(eventPath, StringComparison.OrdinalIgnoreCase),
                    "locked evidence made a committed checkpoint delete fail, stale, or disclose its path");
                var deletedCheckpointReceipt = deleted.Receipt
                    ?? throw new InvalidOperationException("committed checkpoint delete omitted its receipt");

                var appliedCheckpointUndo = false;
                var checkpointRefreshCalls = 0;
                var checkpointRehydrateCalls = 0;
                var checkpointUndo = SavedStateWorkflowCoordinator.UndoDeletedStateAndReportAsync(
                        store,
                        events,
                        deletedCheckpointReceipt,
                        result => appliedCheckpointUndo = result.Restored,
                        async (restoredReceipt, token) =>
                        {
                            Require(!token.CanBeCanceled,
                                "committed checkpoint Undo refresh inherited caller cancellation");
                            checkpointRefreshCalls++;
                            var listed = await store.ListCheckpointsAsync(restoredReceipt.SessionId, token);
                            Require(listed.Any(item => item.Id.Equals(restoredReceipt.CheckpointId, StringComparison.OrdinalIgnoreCase)),
                                "checkpoint Undo refresh did not project the restored checkpoint");
                        },
                        async token =>
                        {
                            Require(!token.CanBeCanceled,
                                "checkpoint Undo rehydration inherited caller cancellation");
                            checkpointRehydrateCalls++;
                            var remaining = await store.ListRestorableDeletedStatesAsync(token);
                            Require(remaining.All(item => !item.Id.Equals(deletedCheckpointReceipt.Id, StringComparison.OrdinalIgnoreCase)),
                                "checkpoint Undo rehydration retained the consumed receipt");
                        })
                    .GetAwaiter()
                    .GetResult();
                Require(checkpointUndo.Restore.Restored
                        && appliedCheckpointUndo
                        && !checkpointUndo.EventRecorded
                        && checkpointRefreshCalls == 1
                        && checkpointRehydrateCalls == 1
                        && File.Exists(checkpoint.Path)
                        && checkpointUndo.Outcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase),
                    "locked evidence made committed checkpoint Undo disappear from UI completion");
            }

            const string sessionId = "locked-session-undo";
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), sessionId).GetAwaiter().GetResult();
            var sessionReceipt = store.TrashSessionAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("session Undo evidence fixture did not enter Trash");
            var sessionEventPath = events.EventPath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(sessionEventPath)!);
            File.WriteAllText(sessionEventPath, "");
            using (File.Open(sessionEventPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var sessionRefreshCalls = 0;
                var sessionRehydrateCalls = 0;
                var sessionUndo = SavedStateWorkflowCoordinator.UndoDeletedStateAndReportAsync(
                        store,
                        events,
                        sessionReceipt,
                        result => Require(result.Restored,
                            "session Undo apply callback did not observe the committed restore"),
                        (restoredReceipt, token) =>
                        {
                            Require(!token.CanBeCanceled && File.Exists(store.SnapshotPath(restoredReceipt.SessionId)),
                                "session Undo refresh ran with cancellation or before the snapshot was restored");
                            sessionRefreshCalls++;
                            return Task.CompletedTask;
                        },
                        async token =>
                        {
                            Require(!token.CanBeCanceled,
                                "session Undo rehydration inherited caller cancellation");
                            sessionRehydrateCalls++;
                            _ = await store.ListRestorableDeletedStatesAsync(token);
                        })
                    .GetAwaiter()
                    .GetResult();
                Require(sessionUndo.Restore.Restored
                        && !sessionUndo.EventRecorded
                        && sessionRefreshCalls == 1
                        && sessionRehydrateCalls == 1
                        && sessionUndo.Outcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase)
                        && !sessionUndo.Outcome.Contains(sessionEventPath, StringComparison.OrdinalIgnoreCase),
                    "locked evidence surfaced a failure or stale UI after committed session Undo");
            }

            const string projectionFailureSession = "trash-projection-failure";
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), projectionFailureSession)
                .GetAwaiter()
                .GetResult();
            var sessionTrash = SavedStateWorkflowCoordinator.TrashSessionAndReportAsync(
                    store,
                    projectionFailureSession,
                    "default",
                    (_, token) =>
                    {
                        Require(!token.CanBeCanceled,
                            "committed session Trash refresh inherited caller cancellation");
                        throw new IOException(@"C:\Users\private\session-list-refresh.txt");
                    })
                .GetAwaiter()
                .GetResult();
            var sessionTrashReceipt = sessionTrash.Receipt
                ?? throw new InvalidOperationException("projection-failure session did not enter Trash");
            Require(!File.Exists(store.SnapshotPath(projectionFailureSession))
                    && sessionTrash.Outcome.Contains("Moved session", StringComparison.Ordinal)
                    && sessionTrash.Outcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase)
                    && sessionTrash.Outcome.Contains("AA-SAVED-IO", StringComparison.Ordinal)
                    && !sessionTrash.Outcome.Contains("private", StringComparison.OrdinalIgnoreCase),
                "a session-list projection fault misreported or disclosed a committed session Trash move");
            Require(store.RestoreDeletedStateAsync(sessionTrashReceipt).GetAwaiter().GetResult().Restored,
                "projection-failure session Trash receipt was not recoverable");

            var template = new ScenarioTemplate(
                "locked-template",
                "Locked Evidence Template",
                DateTimeOffset.UnixEpoch,
                "template-after-locked-evidence",
                "Template topic",
                "Template global prompt",
                TopicLocked: false,
                GlobalLocked: false,
                Agents:
                [
                    new ScenarioTemplateAgent("alpha", "Alpha", "Template alpha", true, false),
                    new ScenarioTemplateAgent("narrator", "Narrator", "Template narrator", true, false)
                ],
                ModelConfigs: new Dictionary<string, ScenarioTemplateModelConfig>(StringComparer.OrdinalIgnoreCase));
            using (File.Open(eventPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var templateRefreshCalls = 0;
                var templateCompletion = SavedStateWorkflowCoordinator
                    .ApplyTemplateWithSafetyCheckpointAndReportAsync(
                        store,
                        events,
                        "default",
                        template,
                        async (_, token) =>
                        {
                            Require(!token.CanBeCanceled,
                                "committed template refresh inherited caller cancellation");
                            templateRefreshCalls++;
                            var applied = await store.LoadSnapshotAsync("default", token);
                            Require(applied?.MatchType == "template-after-locked-evidence",
                                "template refresh did not observe the committed template state");
                        })
                    .GetAwaiter()
                    .GetResult()
                    ?? throw new InvalidOperationException("locked-evidence template apply returned no completion");
                Require(!templateCompletion.EventRecorded
                        && templateRefreshCalls == 1
                        && File.Exists(templateCompletion.SafetyCheckpoint.Checkpoint.Path)
                        && templateCompletion.Outcome.Contains("Loaded template", StringComparison.Ordinal)
                        && templateCompletion.Outcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase)
                        && !templateCompletion.Outcome.Contains(eventPath, StringComparison.OrdinalIgnoreCase),
                    "locked evidence misreported committed template apply or suppressed its safety/UI receipt");
            }
        }
        finally
        {
            DeleteSavedStateTestRoot(root);
        }
    }

    private static void TrashedSessionIdentityKeepsItsComposerDraftIsolated()
    {
        RunStaTest(() =>
        {
            var root = SavedStateTestRoot("draft-identity");
            var draftPath = Path.Combine(root, "composer-drafts.dat");
            const string sessionId = "draft-owner";
            const string operatorDraft = "private unsent draft owned by the original session";
            const string collaborateDraft = "collaboration draft owned by the original session";
            ComposerDraftStore? restartedDrafts = null;
            try
            {
                var store = new SessionStore(root);
                store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), sessionId).GetAwaiter().GetResult();
                var originalSnapshot = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                var originalInstanceId = originalSnapshot.SessionInstanceId;
                Require(SessionStore.IsValidSessionInstanceId(originalInstanceId),
                    "the original draft owner lacked a durable session incarnation identity");
                var operatorScope = ComposerDraftScopes.Operator(sessionId, originalInstanceId, "private");
                var collaborateScope = ComposerDraftScopes.Collaborate(
                    sessionId,
                    originalInstanceId,
                    null,
                    "fast");

                var drafts = new ComposerDraftStore(draftPath, debounceDelay: TimeSpan.FromHours(1));
                drafts.Set(operatorScope, operatorDraft);
                drafts.Set(collaborateScope, collaborateDraft);
                Require(drafts.FlushAsync().GetAwaiter().GetResult(),
                    "draft identity fixture did not flush its unsent text");
                drafts.DisposeAsync().AsTask().GetAwaiter().GetResult();
                restartedDrafts = new ComposerDraftStore(draftPath, debounceDelay: TimeSpan.FromHours(1));

                var firstReceipt = store.TrashSessionAsync(sessionId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("draft owner did not enter Trash");
                Require(!store.TryCreateSessionAsync(sessionId, SessionStore.CreateDefaultSnapshot())
                        .GetAwaiter().GetResult(),
                    "a new unrelated session reused an unexpired draft owner's display name");
                Require(store.RestoreDeletedStateAsync(firstReceipt).GetAwaiter().GetResult().Restored,
                    "draft identity reservation blocked Undo of the original session");

                ArenaViewSnapshot currentView = SnapshotViewMapper.FromCore(
                    new SessionSummary(
                        sessionId,
                        store.SnapshotPath(sessionId),
                        true,
                        0,
                        0,
                        0,
                        DateTimeOffset.UtcNow),
                    store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!);
                SessionSummary? active = new(
                    sessionId,
                    store.SnapshotPath(sessionId),
                    true,
                    0,
                    0,
                    0,
                    DateTimeOffset.UtcNow);
                var operatorCoordinator = CreateDraftOperatorCoordinator(
                    root,
                    restartedDrafts,
                    () => active,
                    () => currentView);
                operatorCoordinator.Coordinator.InitializeControls();
                operatorCoordinator.Coordinator.SetRouteMode("private");
                Require(operatorCoordinator.Text.Text == operatorDraft,
                    "Undo did not reconnect Operator to the original incarnation's draft");

                var collaborateText = new TextBox();
                var collaborate = CreateCollaborateCoordinatorForTest(
                    new FixedCollaborateModelClient("unused"),
                    collaborateText,
                    new TextBlock(),
                    () => currentView,
                    _ => { },
                    new RecordingCollaborateHistoryStore(),
                    composerDraftStore: restartedDrafts);
                collaborate.Initialize();
                Require(collaborateText.Text == collaborateDraft,
                    "Undo did not reconnect Collaborate to the original incarnation's draft");

                var expiringReceipt = store.TrashSessionAsync(sessionId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("restored draft owner did not re-enter Trash");
                var tombstonePath = Path.Combine(
                    root,
                    ".trash",
                    "saved-state",
                    expiringReceipt.Id,
                    "tombstone.json");
                var tombstone = JsonNode.Parse(File.ReadAllText(tombstonePath))?.AsObject()
                    ?? throw new InvalidDataException("draft-owner tombstone was unreadable");
                var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                tombstone["deleted_at_unix_ms"] = expiredAt.AddMinutes(-1).ToUnixTimeMilliseconds();
                tombstone["expires_at_unix_ms"] = expiredAt.ToUnixTimeMilliseconds();
                File.WriteAllText(tombstonePath, tombstone.ToJsonString());
                var afterExpiry = new SessionStore(root);
                Require(afterExpiry.PurgeExpiredSavedStateTrashAsync().GetAwaiter().GetResult() == 1,
                    "expired draft-owner recovery was not purged");
                Require(afterExpiry.TryCreateSessionAsync(sessionId, SessionStore.CreateDefaultSnapshot())
                        .GetAwaiter().GetResult(),
                    "the expired display name could not be reused for a new session");
                var replacementSnapshot = afterExpiry.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Require(replacementSnapshot.SessionInstanceId != originalInstanceId,
                    "the replacement session reused the deleted incarnation identity");

                currentView = SnapshotViewMapper.FromCore(
                    active! with { LastModified = DateTimeOffset.UtcNow.AddSeconds(1) },
                    replacementSnapshot);
                operatorCoordinator.Coordinator.ApplySnapshot(currentView);
                operatorCoordinator.Coordinator.SetRouteMode("private");
                collaborate.RefreshProviderState();
                Require(operatorCoordinator.Text.Text.Length == 0,
                    "Operator restored an expired prior incarnation's unsent draft into the replacement session");
                Require(collaborateText.Text.Length == 0,
                    "Collaborate restored an expired prior incarnation's unsent draft into the replacement session");
                Require(restartedDrafts.Get(operatorScope) == operatorDraft
                        && restartedDrafts.Get(collaborateScope) == collaborateDraft,
                    "privacy isolation was a destructive draft purge rather than durable instance scoping");
                Require(afterExpiry.RestoreDeletedStateAsync(expiringReceipt).GetAwaiter().GetResult().Status
                        == SavedStateRestoreStatus.NotFound,
                    "a purged expired receipt collided with the replacement session");
            }
            finally
            {
                restartedDrafts?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                DeleteSavedStateTestRoot(root);
            }
        });
    }

    private static void SavedStateTrashAndUndoSettleUniversalStatus()
    {
        RunStaTest(() =>
        {
            var root = SavedStateTestRoot("status-lifecycle");
            try
            {
                var store = new SessionStore(root);
                var events = new EventLogStore(root);
                store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "default").GetAwaiter().GetResult();
                const string sessionId = "status-lifecycle-session";
                store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), sessionId).GetAwaiter().GetResult();
                var checkpoint = store.SaveCheckpointAsync("default", "Status lifecycle checkpoint")
                    .GetAwaiter()
                    .GetResult();

                var statusCenter = new ApplicationStatusCenter();
                var presentation = new AIArena.Wpf.ViewModels.ShellTopBarPresentationViewModel(statusCenter);
                var arenaStatus = new TextBlock { Text = "Ready." };
                var arenaStatusDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                    TextBlock.TextProperty,
                    typeof(TextBlock));
                arenaStatusDescriptor.AddValueChanged(
                    arenaStatus,
                    (_, _) => presentation.ArenaStatus = arenaStatus.Text);
                var busy = false;
                var coordinator = new ArenaOperationCoordinator(
                    new SemaphoreSlim(1, 1),
                    new TextBlock(),
                    arenaStatus,
                    new Button(),
                    new Button(),
                    new Button(),
                    new Button(),
                    new Button(),
                    [],
                    () => busy,
                    value => busy = value,
                    () => false,
                    (_, _) => { },
                    (_, _) => { },
                    (_, _) => { },
                    _ => { },
                    () => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { });

                string RunStatusOperation(string progress, Func<Task<string>> operation)
                {
                    string? outcome = null;
                    var ran = coordinator.RunAsync(progress, () =>
                    {
                        Require(statusCenter.History.Any(entry =>
                                entry.Key == "legacy.arena"
                                && entry.Summary == progress
                                && entry.IsActive),
                            $"{progress} was not published as active operation progress");
                        // Keep this deterministic STA harness on its owning
                        // thread. The production Dispatcher provides an async
                        // synchronization context; the test fixture does not.
                        outcome = operation().GetAwaiter().GetResult();
                        presentation.PublishCompatibilityStatus(
                            "app.saved-state",
                            "Saved State",
                            outcome,
                            navigationTarget: "arena",
                            identity: new ApplicationStatusIdentity("default"));
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();

                    Require(ran && !busy, $"{progress} did not run or release arena busy state");
                    Require(outcome is not null, $"{progress} did not publish a saved-state outcome");
                    Require(arenaStatus.Text == "Ready.",
                        $"{progress} left legacy arena progress instead of resolving its compatibility target");
                    Require(!statusCenter.History.Any(entry => entry.Key == "legacy.arena" && entry.IsActive),
                        $"{progress} leaked an active legacy.arena row after completion");
                    var savedStateEntry = statusCenter.History.FirstOrDefault(entry =>
                        entry.Key == "app.saved-state" && !entry.IsResolved);
                    Require(savedStateEntry is not null
                            && !savedStateEntry.IsActive
                            && savedStateEntry.State
                                == AIArena.Wpf.ViewModels.ShellTopBarPresentationViewModel.LegacyState(outcome!),
                        $"{progress} did not retain its terminal saved-state truth after settling progress");
                    return outcome!;
                }

                SavedStateWorkflowCoordinator.CheckpointTrashCompletion? checkpointTrash = null;
                var checkpointTrashOutcome = RunStatusOperation(
                    $"Moving checkpoint {checkpoint.Name} to Trash...",
                    async () =>
                    {
                        checkpointTrash = await SavedStateWorkflowCoordinator.TrashCheckpointAndReportAsync(
                            store,
                            events,
                            "default",
                            checkpoint.Id,
                            checkpoint.Name,
                            async (_, token) =>
                            {
                                var checkpoints = await store.ListCheckpointsAsync("default", token);
                                Require(checkpoints.All(item => item.Id != checkpoint.Id),
                                    "checkpoint Trash projection still contained the committed deletion");
                            });
                        return checkpointTrash.Outcome;
                    });
                var checkpointReceipt = checkpointTrash?.Receipt
                    ?? throw new InvalidOperationException("checkpoint status lifecycle delete did not commit");
                Require(checkpointTrashOutcome.StartsWith("Moved checkpoint", StringComparison.Ordinal),
                    "checkpoint Trash did not retain its committed success outcome");

                SavedStateWorkflowCoordinator.DeletedStateUndoCompletion? checkpointUndo = null;
                var checkpointUndoOutcome = RunStatusOperation(
                    $"Restoring checkpoint {checkpoint.Name} from Trash...",
                    async () =>
                    {
                        checkpointUndo = await SavedStateWorkflowCoordinator.UndoDeletedStateAndReportAsync(
                            store,
                            events,
                            checkpointReceipt,
                            _ => { },
                            (_, token) =>
                            {
                                Require(!token.CanBeCanceled,
                                    "post-commit checkpoint Undo projection inherited operation cancellation");
                                throw new IOException(@"C:\Users\private\checkpoint-undo-refresh.txt");
                            },
                            _ => Task.CompletedTask);
                        return checkpointUndo.Outcome;
                    });
                Require(checkpointUndo?.Restore.Restored == true
                        && File.Exists(checkpoint.Path)
                        && checkpointUndoOutcome.Contains("change was committed", StringComparison.OrdinalIgnoreCase)
                        && checkpointUndoOutcome.Contains("AA-SAVED-IO", StringComparison.Ordinal),
                    "checkpoint Undo warning lost committed truth or its coded projection warning");

                SavedStateWorkflowCoordinator.SessionTrashCompletion? sessionTrash = null;
                var sessionTrashOutcome = RunStatusOperation(
                    $"Moving session {sessionId} to Trash...",
                    async () =>
                    {
                        sessionTrash = await SavedStateWorkflowCoordinator.TrashSessionAndReportAsync(
                            store,
                            sessionId,
                            "default",
                            (_, token) =>
                            {
                                Require(!File.Exists(store.SnapshotPath(sessionId)),
                                    "session Trash projection ran before the committed move");
                                return Task.CompletedTask;
                            });
                        return sessionTrash.Outcome;
                    });
                var sessionReceipt = sessionTrash?.Receipt
                    ?? throw new InvalidOperationException("session status lifecycle delete did not commit");
                Require(sessionTrashOutcome.StartsWith("Moved session", StringComparison.Ordinal),
                    "session Trash did not retain its committed success outcome");

                SavedStateWorkflowCoordinator.DeletedStateUndoCompletion? sessionUndo = null;
                var sessionUndoOutcome = RunStatusOperation(
                    $"Restoring session {sessionId} from Trash...",
                    async () =>
                    {
                        sessionUndo = await SavedStateWorkflowCoordinator.UndoDeletedStateAndReportAsync(
                            store,
                            events,
                            sessionReceipt,
                            _ => { },
                            (receipt, _) =>
                            {
                                Require(File.Exists(store.SnapshotPath(receipt.SessionId)),
                                    "session Undo projection ran before the committed restore");
                                return Task.CompletedTask;
                            },
                            _ => Task.CompletedTask);
                        return sessionUndo.Outcome;
                    });
                Require(sessionUndo?.Restore.Restored == true
                        && sessionUndoOutcome.StartsWith("Restored session", StringComparison.Ordinal),
                    "session Undo did not retain its committed success outcome");

                SavedStateWorkflowCoordinator.CheckpointTrashCompletion? missingTrash = null;
                var failedOutcome = RunStatusOperation(
                    "Moving checkpoint Missing checkpoint to Trash...",
                    async () =>
                    {
                        missingTrash = await SavedStateWorkflowCoordinator.TrashCheckpointAndReportAsync(
                            store,
                            events,
                            "default",
                            "missing-checkpoint",
                            "Missing checkpoint",
                            (_, _) => Task.CompletedTask);
                        return missingTrash.Outcome;
                    });
                Require(missingTrash?.Receipt is null
                        && failedOutcome == "Checkpoint could not be moved to Trash.",
                    "failed checkpoint Trash did not retain its truthful terminal outcome");
            }
            finally
            {
                DeleteSavedStateTestRoot(root);
            }
        });
    }

    private static string SavedStateTestRoot(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ai-arena-wpf-saved-state-tests",
            $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteSavedStateTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
