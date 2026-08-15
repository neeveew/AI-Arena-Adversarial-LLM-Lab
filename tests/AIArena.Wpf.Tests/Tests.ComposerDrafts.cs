using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static partial class Program
{
    static void ComposerDraftStoreProtectsAndRestoresPayloads()
    {
        var root = DraftTestRoot("protect");
        try
        {
            var path = Path.Combine(root, "composer-drafts.dat");
            var workspace = Path.Combine(root, "Distinctive Workspace Name");
            const string distinctive = "M6 distinctive plaintext :: red fox 918273";
            var operatorScope = ComposerDraftScopes.Operator("session-a", ComposerTestSessionInstanceId, "public");
            var agentScope = ComposerDraftScopes.AgentWorkspace(workspace);
            var conversationId = Guid.NewGuid();
            var collaborateScope = ComposerDraftScopes.Collaborate(
                "session-a",
                ComposerTestSessionInstanceId,
                conversationId,
                "critique");

            var store = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            store.Set(operatorScope, distinctive);
            store.Set(agentScope, "agent recovery text");
            store.Set(collaborateScope, "collaborate recovery text");
            Require(store.FlushAsync().GetAwaiter().GetResult(), "an explicit composer-draft flush should succeed");

            var ciphertext = File.ReadAllBytes(path);
            Require(!ContainsBytes(ciphertext, Encoding.UTF8.GetBytes(distinctive)),
                "the DPAPI file must not contain distinctive draft plaintext");
            Require(!ContainsBytes(ciphertext, Encoding.UTF8.GetBytes(workspace)),
                "the encrypted file must not contain a raw Agent workspace path");
            Require(agentScope.StartsWith("agent:", StringComparison.Ordinal)
                    && agentScope.Length == "agent:".Length + 64
                    && !agentScope.Contains("Distinctive", StringComparison.OrdinalIgnoreCase),
                "Agent scopes should retain only a SHA-256 workspace identity");

            var restarted = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(restarted.Get(operatorScope) == distinctive,
                "Operator text should round-trip through a process restart");
            Require(restarted.Get(agentScope) == "agent recovery text",
                "Agent text should round-trip through a process restart");
            Require(restarted.Get(collaborateScope) == "collaborate recovery text",
                "Collaborate text should round-trip through a process restart");

            Require(ComposerDraftScopes.AgentWorkspace(workspace + Path.DirectorySeparatorChar)
                    == ComposerDraftScopes.AgentWorkspace(workspace.ToUpperInvariant()),
                "equivalent Windows workspace identities should hash to the same scope");
            Require(ComposerDraftScopes.Operator("session-a", ComposerTestSessionInstanceId, "public")
                    != ComposerDraftScopes.Operator("session-a", ComposerTestSessionInstanceId, "private")
                    && ComposerDraftScopes.Operator("session-a", ComposerTestSessionInstanceId, "public")
                    != ComposerDraftScopes.Operator("session-b", ComposerTestSessionInstanceId, "public")
                    && ComposerDraftScopes.Operator("session-a", ComposerTestSessionInstanceId, "public")
                    != ComposerDraftScopes.Operator("session-a", "22222222222222222222222222222222", "public"),
                "Operator routes, sessions, and session incarnations should be isolated");
            Require(ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, conversationId, "team")
                    != ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, conversationId, "critique")
                    && ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, conversationId, "team")
                    != ComposerDraftScopes.Collaborate("session-b", ComposerTestSessionInstanceId, conversationId, "team")
                    && ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, conversationId, "team")
                    != ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, Guid.NewGuid(), "team")
                    && ComposerDraftScopes.Collaborate("session-a", ComposerTestSessionInstanceId, conversationId, "team")
                    != ComposerDraftScopes.Collaborate("session-a", "22222222222222222222222222222222", conversationId, "team"),
                "Collaborate sessions, incarnations, conversations, and modes should be isolated");
            Require(ComposerDraftScopes.Operator("session-a", "", "public").Length == 0
                    && ComposerDraftScopes.Collaborate("session-a", "", conversationId, "team").Length == 0,
                "session draft scopes must fail closed without a durable instance identity");
        }
        finally
        {
            DeleteDraftTestRoot(root);
        }
    }

    static void ComposerDraftStoreEnforcesBoundsAndRecoversSafely()
    {
        var root = DraftTestRoot("bounds");
        try
        {
            var path = Path.Combine(root, "composer-drafts.dat");
            var store = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            var oldest = ComposerDraftScopes.Operator("oldest", ComposerTestSessionInstanceId, "public");
            store.Set(oldest, "old");
            for (var index = 0; index < ComposerDraftStore.MaxEntries + 8; index++)
            {
                store.Set(ComposerDraftScopes.Operator($"session-{index:D3}", ComposerTestSessionInstanceId, "public"), $"draft-{index:D3}");
            }

            Require(store.Count == ComposerDraftStore.MaxEntries,
                "entry count should remain bounded");
            Require(store.Get(oldest) == "",
                "deterministic oldest-first eviction should remove the earliest untouched entry");

            var capScope = ComposerDraftScopes.Operator("cap", ComposerTestSessionInstanceId, "public");
            store.Set(capScope, new string('x', ComposerDraftStore.MaxDraftCharacters + 8192));
            Require(store.Get(capScope).Length == ComposerDraftStore.MaxDraftCharacters,
                "each draft should be capped before it enters durable state");

            var hostile = string.Concat(Enumerable.Repeat("\u0001\u001f😀漢\\\"", 5000));
            for (var index = 0; index < 12; index++)
            {
                store.Set(ComposerDraftScopes.Operator($"hostile-{index}", ComposerTestSessionInstanceId, "public"), hostile);
            }

            Require(store.FlushAsync().GetAwaiter().GetResult(),
                "escape-heavy Unicode should be evicted to the exact serialized plaintext bound, not fail the flush");
            Require(new FileInfo(path).Length <= ComposerDraftStore.MaxCiphertextBytes,
                "protected output should remain within its ciphertext cap");

            store.Set(capScope, "");
            Require(store.Get(capScope) == "", "an empty edit should delete its scope");
            Require(store.FlushAsync().GetAwaiter().GetResult(), "deletion should flush successfully");

            var corruptGeneration = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            File.WriteAllBytes(path, corruptGeneration);
            var corrupt = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(corrupt.Count == 0 && corrupt.LastDiagnosticCode == "read-failed",
                "corrupt or truncated ciphertext should recover to an empty store without throwing");

            var recoveredScope = ComposerDraftScopes.Operator(
                "corrupt-recovery",
                ComposerTestSessionInstanceId,
                "public");
            const string recoveredSecret = "private draft recovered after corrupt generation 4815162342";
            corrupt.Set(recoveredScope, recoveredSecret);
            Require(corrupt.FlushAsync().GetAwaiter().GetResult(),
                "a new edit should replace the exact corrupt generation observed during construction");
            var recoveredCiphertext = File.ReadAllBytes(path);
            Require(!recoveredCiphertext.SequenceEqual(corruptGeneration),
                "successful recovery should atomically replace the observed corrupt generation");
            Require(!ContainsBytes(recoveredCiphertext, Encoding.UTF8.GetBytes(recoveredSecret)),
                "corrupt-generation recovery must never persist the new draft as raw plaintext");
            var recoveredRestart = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(recoveredRestart.Get(recoveredScope) == recoveredSecret
                    && recoveredRestart.LastDiagnosticCode == "ready",
                "the protected replacement should restore the new edit after restart");

            var conflictPath = Path.Combine(root, "corrupt-recovery-conflict.dat");
            File.WriteAllBytes(conflictPath, [0x11, 0x22, 0x33, 0x44]);
            var winningRecovery = new ComposerDraftStore(conflictPath, debounceDelay: TimeSpan.FromHours(1));
            var staleRecovery = new ComposerDraftStore(conflictPath, debounceDelay: TimeSpan.FromHours(1));
            var winningScope = ComposerDraftScopes.Operator(
                "winning-recovery",
                ComposerTestSessionInstanceId,
                "public");
            var staleScope = ComposerDraftScopes.Operator(
                "stale-recovery",
                ComposerTestSessionInstanceId,
                "public");
            const string winningSecret = "winner private draft 8675309";
            const string staleSecret = "stale private draft must never leak 314159";
            winningRecovery.Set(winningScope, winningSecret);
            Require(winningRecovery.FlushAsync().GetAwaiter().GetResult(),
                "the first app instance should recover the corrupt generation");
            var winningCiphertext = File.ReadAllBytes(conflictPath);
            staleRecovery.Set(staleScope, staleSecret);
            Require(!staleRecovery.FlushAsync().GetAwaiter().GetResult()
                    && staleRecovery.LastDiagnosticCode == "recovery-conflict",
                "a stale app instance must fail closed when the corrupt generation changed after load");
            Require(File.ReadAllBytes(conflictPath).SequenceEqual(winningCiphertext),
                "a recovery conflict must leave the concurrently replaced protected file byte-for-byte intact");
            Require(!ContainsBytes(winningCiphertext, Encoding.UTF8.GetBytes(winningSecret))
                    && !ContainsBytes(winningCiphertext, Encoding.UTF8.GetBytes(staleSecret)),
                "neither winning nor rejected drafts may leak into the recovery file as raw plaintext");
            var conflictRestart = new ComposerDraftStore(conflictPath, debounceDelay: TimeSpan.FromHours(1));
            Require(conflictRestart.Get(winningScope) == winningSecret
                    && conflictRestart.Get(staleScope) == "",
                "restart should preserve the winner and exclude the rejected stale mutation");

            var protector = new WindowsComposerDraftProtector();
            WriteProtectedDraftPayload(path, protector,
                "{\"Version\":1,\"NextSequence\":2,\"Entries\":[{\"Key\":null,\"Text\":\"secret\",\"Sequence\":1}]}");
            var nullField = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(nullField.Count == 0 && nullField.LastDiagnosticCode == "invalid-payload",
                "a decrypted payload containing null fields should fail safely to empty");

            WriteProtectedDraftPayload(path, protector,
                $"{{\"Version\":1,\"NextSequence\":{long.MaxValue},\"Entries\":[]}}");
            var overflow = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(overflow.Count == 0 && overflow.LastDiagnosticCode == "invalid-payload",
                "a malicious sequence near overflow should be rejected before a later edit can throw");

            File.WriteAllBytes(path, new byte[ComposerDraftStore.MaxCiphertextBytes + 1]);
            var oversized = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(oversized.Count == 0 && oversized.LastDiagnosticCode == "read-bound",
                "an oversized or concurrently grown file should be capped before allocation/decryption");

            var alternatePath = Path.Combine(root, "wrong-entropy.dat");
            var correctEntropy = new TestDpapiDraftProtector("draft-test-entropy-a");
            var wrongEntropy = new TestDpapiDraftProtector("draft-test-entropy-b");
            var protectedStore = new ComposerDraftStore(
                alternatePath,
                correctEntropy,
                TimeSpan.FromHours(1));
            var wrongScope = ComposerDraftScopes.Operator("wrong-user", ComposerTestSessionInstanceId, "public");
            protectedStore.Set(wrongScope, "must not escape");
            Require(protectedStore.FlushAsync().GetAwaiter().GetResult(), "test entropy payload should flush");
            var wrongUser = new ComposerDraftStore(
                alternatePath,
                wrongEntropy,
                TimeSpan.FromHours(1));
            Require(wrongUser.Count == 0 && wrongUser.Get(wrongScope) == "",
                "wrong-user or wrong-entropy DPAPI recovery must fail closed to empty");
        }
        finally
        {
            DeleteDraftTestRoot(root);
        }
    }

    static void ComposerDraftStoreCoordinatesDebounceFlushCancellationAndDispose()
    {
        var root = DraftTestRoot("concurrency");
        try
        {
            var path = Path.Combine(root, "composer-drafts.dat");
            var writes = 0;
            async Task CountingWriter(string target, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref writes);
                await WriteDraftCiphertextAtomicAsync(target, bytes, cancellationToken);
            }

            var store = new ComposerDraftStore(
                path,
                debounceDelay: TimeSpan.FromMilliseconds(40),
                atomicWriter: CountingWriter);
            Parallel.For(0, 48, index =>
            {
                store.Set(ComposerDraftScopes.Operator($"parallel-{index:D2}", ComposerTestSessionInstanceId, "public"), $"value-{index:D2}");
            });
            for (var index = 0; index < 20; index++)
            {
                store.Set(ComposerDraftScopes.AgentWorkspace(root), $"latest-{index:D2}");
            }

            store.PendingDebounceTask.GetAwaiter().GetResult();
            Require(Volatile.Read(ref writes) == 1,
                "a burst of concurrent edits should collapse into one debounced protected write");
            Require(store.Get(ComposerDraftScopes.AgentWorkspace(root)) == "latest-19",
                "the shared generation should retain the latest coherent edit");

            store.Set(ComposerDraftScopes.Operator("explicit", ComposerTestSessionInstanceId, "public"), "explicit-value");
            var flushes = Enumerable.Range(0, 8).Select(_ => store.FlushAsync()).ToArray();
            Task.WhenAll(flushes).GetAwaiter().GetResult();
            Require(flushes.All(task => task.Result), "concurrent explicit flush callers should share a coherent file generation");
            var restarted = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
            Require(restarted.Get(ComposerDraftScopes.Operator("explicit", ComposerTestSessionInstanceId, "public")) == "explicit-value",
                "the final concurrent generation should restart cleanly");

            var multiProcessPath = Path.Combine(root, "multi-process.dat");
            var firstProcess = new ComposerDraftStore(multiProcessPath, debounceDelay: TimeSpan.FromHours(1));
            var secondProcess = new ComposerDraftStore(multiProcessPath, debounceDelay: TimeSpan.FromHours(1));
            var firstScope = ComposerDraftScopes.Operator("first-process", ComposerTestSessionInstanceId, "public");
            var secondScope = ComposerDraftScopes.Operator("second-process", ComposerTestSessionInstanceId, "public");
            var thirdScope = ComposerDraftScopes.Operator("third-draft", ComposerTestSessionInstanceId, "public");
            firstProcess.Set(firstScope, "first unsent draft");
            secondProcess.Set(secondScope, "second unsent draft");
            var disjointFlushes = new[] { firstProcess.FlushAsync(), secondProcess.FlushAsync() };
            Task.WhenAll(disjointFlushes).GetAwaiter().GetResult();
            Require(disjointFlushes.All(flush => flush.Result),
                "two app instances should serialize their first disjoint draft flushes");
            var mergedRestart = new ComposerDraftStore(multiProcessPath, debounceDelay: TimeSpan.FromHours(1));
            Require(mergedRestart.Get(firstScope) == "first unsent draft"
                    && mergedRestart.Get(secondScope) == "second unsent draft",
                "a later full-file writer must merge rather than erase another app instance's unsent draft");

            firstProcess.Remove(firstScope);
            secondProcess.Set(thirdScope, "third unsent draft");
            var deleteAndAddFlushes = new[] { firstProcess.FlushAsync(), secondProcess.FlushAsync() };
            Task.WhenAll(deleteAndAddFlushes).GetAwaiter().GetResult();
            Require(deleteAndAddFlushes.All(flush => flush.Result),
                "cross-process deletion and insertion should serialize without losing either intent");
            var mergedAfterDelete = new ComposerDraftStore(multiProcessPath, debounceDelay: TimeSpan.FromHours(1));
            Require(mergedAfterDelete.Get(firstScope) == ""
                    && mergedAfterDelete.Get(secondScope) == "second unsent draft"
                    && mergedAfterDelete.Get(thirdScope) == "third unsent draft",
                "cross-process merge should persist tombstones while retaining unrelated unsent drafts");
            firstProcess.DisposeAsync().AsTask().GetAwaiter().GetResult();
            secondProcess.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mergedRestart.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mergedAfterDelete.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var stablePath = Path.Combine(root, "cancelled.dat");
            var stable = new ComposerDraftStore(stablePath, debounceDelay: TimeSpan.FromHours(1));
            var stableScope = ComposerDraftScopes.Operator("stable", ComposerTestSessionInstanceId, "public");
            stable.Set(stableScope, "stable-before-cancel");
            Require(stable.FlushAsync().GetAwaiter().GetResult(), "the cancellation baseline should flush");

            var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new ComposerDraftStore(
                stablePath,
                debounceDelay: TimeSpan.FromHours(1),
                atomicWriter: async (_, _, token) =>
                {
                    writerStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            cancelled.Set(stableScope, "must-not-commit");
            using var cancellation = new CancellationTokenSource();
            var cancelledFlush = cancelled.FlushAsync(cancellation.Token);
            Require(writerStarted.Task.Wait(TimeSpan.FromSeconds(2)), "the cancellable writer seam should start");
            cancellation.Cancel();
            RequireThrows<OperationCanceledException>(
                () => cancelledFlush.GetAwaiter().GetResult(),
                "a cancelled explicit flush should observe cancellation");
            var afterCancellation = new ComposerDraftStore(stablePath, debounceDelay: TimeSpan.FromHours(1));
            Require(afterCancellation.Get(stableScope) == "stable-before-cancel",
                "a cancelled flush must leave the previous atomic file intact");

            var disposePath = Path.Combine(root, "dispose.dat");
            var disposeStore = new ComposerDraftStore(
                disposePath,
                debounceDelay: TimeSpan.FromMilliseconds(25),
                atomicWriter: async (target, bytes, token) =>
                {
                    await Task.Delay(15, token);
                    await WriteDraftCiphertextAtomicAsync(target, bytes, token);
                });
            var disposeScope = ComposerDraftScopes.Operator("dispose", ComposerTestSessionInstanceId, "public");
            disposeStore.Set(disposeScope, "captured-before-dispose");
            disposeStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var disposedRestart = new ComposerDraftStore(disposePath, debounceDelay: TimeSpan.FromHours(1));
            Require(disposedRestart.Get(disposeScope) == "captured-before-dispose",
                "DisposeAsync should cancel and await debounce work, then persist the final generation without racing the gate");
            RequireThrows<ObjectDisposedException>(
                () => disposeStore.Set(disposeScope, "late edit"),
                "a disposed store should reject late mutations deterministically");
        }
        finally
        {
            DeleteDraftTestRoot(root);
        }
    }

    static void ComposerDraftShutdownCapturesFlushesAndDisposes()
    {
        var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
        var closing = source.IndexOf("private async void MainWindow_Closing", StringComparison.Ordinal);
        var helper = source.IndexOf("private async Task<bool> FlushComposerDraftsForShutdownAsync", closing, StringComparison.Ordinal);
        Require(closing >= 0 && helper > closing, "MainWindow closing should own an awaitable composer-draft shutdown helper");
        var closingBody = source[closing..helper];
        Require(closingBody.Contains("await FlushComposerDraftsForShutdownAsync(dispose: false)", StringComparison.Ordinal)
                && closingBody.Contains("await FlushComposerDraftsForShutdownAsync(dispose: true)", StringComparison.Ordinal)
                && closingBody.IndexOf("TryBeginShutdownAttempt", StringComparison.Ordinal)
                    < closingBody.IndexOf("await FlushComposerDraftsForShutdownAsync(dispose: false)", StringComparison.Ordinal)
                && closingBody.IndexOf("await FlushComposerDraftsForShutdownAsync(dispose: false)", StringComparison.Ordinal)
                < closingBody.IndexOf("_shutdownInProgress = true", StringComparison.Ordinal)
                && closingBody.Contains("Exit without saving drafts", StringComparison.Ordinal)
                && closingBody.Contains("Keep app open", StringComparison.Ordinal)
                && closingBody.Contains("IsEnabled = false", StringComparison.Ordinal),
            "shutdown should durably capture before lifecycle teardown, require explicit discard on failure, freeze input, and dispose after drains");
        var helperEnd = source.IndexOf("internal static bool ShouldContinueShutdownAfterDraftFlush", helper, StringComparison.Ordinal);
        var helperBody = source[helper..helperEnd];
        Require(helperBody.Contains("_operatorTurnCoordinator?.CaptureDraftForShutdown()", StringComparison.Ordinal)
                && helperBody.Contains("_agentWorkspaceCoordinator?.CaptureDraftForShutdown()", StringComparison.Ordinal)
                && helperBody.Contains("_collaborateCoordinator?.CaptureDraftForShutdown()", StringComparison.Ordinal)
                && helperBody.Contains("await _composerDraftStore.FlushAsync()", StringComparison.Ordinal)
                && helperBody.Contains("await _composerDraftStore.DisposeAsync()", StringComparison.Ordinal),
            "the final close boundary should capture all three visible composers, await the shared flush, and dispose it");
        Require(!helperBody.Contains("promptText", StringComparison.OrdinalIgnoreCase)
                && !helperBody.Contains("StorePath", StringComparison.Ordinal),
            "shutdown diagnostics must not include raw draft text or the storage path");

        var shutdownAttemptInProgress = false;
        Require(MainWindow.TryBeginShutdownAttempt(ref shutdownAttemptInProgress)
                && shutdownAttemptInProgress
                && !MainWindow.TryBeginShutdownAttempt(ref shutdownAttemptInProgress),
            "a second close request should be rejected while the first durable draft preflight is awaiting");
        MainWindow.EndShutdownAttempt(ref shutdownAttemptInProgress);
        Require(!shutdownAttemptInProgress
                && MainWindow.TryBeginShutdownAttempt(ref shutdownAttemptInProgress),
            "keeping the app open should release the close-attempt guard for a later retry");
        MainWindow.EndShutdownAttempt(ref shutdownAttemptInProgress);

        var discardPromptCalls = 0;
        Require(!MainWindow.ShouldContinueShutdownAfterDraftFlush(false, () =>
                {
                    discardPromptCalls++;
                    return false;
                })
                && discardPromptCalls == 1,
            "a failed final draft flush should keep the app open when destructive discard is declined");
        Require(MainWindow.ShouldContinueShutdownAfterDraftFlush(false, () => true),
            "a failed final draft flush may close only after explicit destructive discard confirmation");
        Require(MainWindow.ShouldContinueShutdownAfterDraftFlush(true, () =>
                {
                    discardPromptCalls++;
                    return false;
                })
                && discardPromptCalls == 1,
            "a successful final draft flush should close without asking the user to discard anything");
    }

    static void ComposerDraftAgentSendSemanticsAreLossless()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("agent-send");
                var workspace = Path.Combine(root, "workspace");
                Directory.CreateDirectory(workspace);
                try
                {
                    var snapshot = SnapshotForOverviewTest(true, "draft-model", "", 0, [], []);

                    var successPrompt = new TextBox();
                    var successStore = new ComposerDraftStore(Path.Combine(root, "success.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var success = CreateDraftAgentCoordinator(
                        root,
                        workspace,
                        successPrompt,
                        successStore,
                        new SequentialAgentModelClient("Successful answer."),
                        snapshot);
                    success.Initialize();
                    successPrompt.Text = "successful visible Agent draft";
                    success.DebugSendAsync().GetAwaiter().GetResult();
                    Require(successPrompt.Text == ""
                            && successStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == "",
                        "a logically successful Agent response should clear the exact unchanged visible draft");
                    success.Dispose();

                    var failurePrompt = new TextBox();
                    var failureStore = new ComposerDraftStore(Path.Combine(root, "failure.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var failure = CreateDraftAgentCoordinator(
                        root,
                        workspace,
                        failurePrompt,
                        failureStore,
                        new ThrowingCollaborateModelClient("private provider failure"),
                        snapshot);
                    failure.Initialize();
                    failurePrompt.Text = "retain after Agent model failure";
                    failure.DebugSendAsync().GetAwaiter().GetResult();
                    Require(failurePrompt.Text == "retain after Agent model failure"
                            && failureStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == failurePrompt.Text,
                        "an Agent model failure should retain the visible and durable draft");
                    failure.Dispose();

                    var cancellationPrompt = new TextBox();
                    var cancellationStore = new ComposerDraftStore(Path.Combine(root, "cancel.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var cancellationClient = new CancellationBlockingModelClient();
                    var cancelled = CreateDraftAgentCoordinator(
                        root,
                        workspace,
                        cancellationPrompt,
                        cancellationStore,
                        cancellationClient,
                        snapshot);
                    cancelled.Initialize();
                    cancellationPrompt.Text = "retain after Agent cancellation";
                    var cancelledTask = cancelled.DebugSendAsync();
                    Require(cancellationClient.Started.Task.Wait(TimeSpan.FromSeconds(2)), "the cancellable Agent call should start");
                    cancelled.ControlStop();
                    PumpDocumentImportTask(cancelledTask);
                    Require(cancellationPrompt.Text == "retain after Agent cancellation"
                            && cancellationStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == cancellationPrompt.Text,
                        "an Agent cancellation should retain the visible and durable draft");
                    cancelled.Dispose();

                    var editPrompt = new TextBox();
                    var editStore = new ComposerDraftStore(Path.Combine(root, "edit.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var gateClient = new GatedDraftModelClient("completed after edit");
                    var edited = CreateDraftAgentCoordinator(root, workspace, editPrompt, editStore, gateClient, snapshot);
                    edited.Initialize();
                    editPrompt.Text = "Agent text at send";
                    var editedTask = edited.DebugSendAsync();
                    Require(gateClient.Started.Task.Wait(TimeSpan.FromSeconds(2)), "the gated Agent call should start");
                    editPrompt.Text = "Agent text edited while provider was running";
                    gateClient.Release.TrySetResult();
                    PumpDocumentImportTask(editedTask);
                    Require(editPrompt.Text == "Agent text edited while provider was running"
                            && editStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == editPrompt.Text,
                        "Agent must not clear text edited during an in-flight successful send");
                    edited.Dispose();

                    var controlPrompt = new TextBox();
                    var controlStore = new ComposerDraftStore(Path.Combine(root, "control.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var control = CreateDraftAgentCoordinator(
                        root,
                        workspace,
                        controlPrompt,
                        controlStore,
                        new SequentialAgentModelClient("control answer"),
                        snapshot);
                    control.Initialize();
                    controlPrompt.Text = "private visible Agent draft";
                    control.ControlSendAsync("control-plane Agent request").GetAwaiter().GetResult();
                    Require(controlPrompt.Text == "private visible Agent draft"
                            && controlStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == controlPrompt.Text,
                        "Agent control-plane sends must neither commandeer nor clear the visible draft");
                    control.Dispose();

                    var slashPrompt = new TextBox();
                    var slashStore = new ComposerDraftStore(Path.Combine(root, "slash.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var slash = CreateDraftAgentCoordinator(
                        root,
                        workspace,
                        slashPrompt,
                        slashStore,
                        new SequentialAgentModelClient("unused"),
                        snapshot);
                    slash.Initialize();
                    slashPrompt.Text = "/unknown-draft-command";
                    slash.DebugSendAsync().GetAwaiter().GetResult();
                    Require(slashPrompt.Text == "/unknown-draft-command"
                            && slashStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == slashPrompt.Text,
                        "an unsuccessful local slash command should retain its draft");
                    slashPrompt.Text = "/status";
                    slash.DebugSendAsync().GetAwaiter().GetResult();
                    Require(slashPrompt.Text == ""
                            && slashStore.Get(ComposerDraftScopes.AgentWorkspace(workspace)) == "",
                        "a successful local slash command should clear persistence consistently");
                    slash.Dispose();
                }
                finally
                {
                    DeleteDraftTestRoot(root);
                }
            });
        });
    }

    static void ComposerDraftCollaborateSendSemanticsAreLossless()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("collaborate-send");
                try
                {
                    var currentSnapshot = SnapshotForOverviewTest(true, "draft-model", "", 0, [], []);

                    var successPrompt = new TextBox();
                    var successStore = new ComposerDraftStore(Path.Combine(root, "success.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var success = CreateCollaborateCoordinatorForTest(
                        new FixedCollaborateModelClient("Successful answer."),
                        successPrompt,
                        new TextBlock(),
                        () => currentSnapshot,
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        composerDraftStore: successStore);
                    success.Initialize();
                    successPrompt.Text = "successful visible Collaborate draft";
                    success.SendAsync().GetAwaiter().GetResult();
                    Require(successPrompt.Text == "",
                        "a successful Collaborate response and history save should clear the exact unchanged visible draft");

                    var failurePrompt = new TextBox();
                    var failureStore = new ComposerDraftStore(Path.Combine(root, "failure.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var failure = CreateCollaborateCoordinatorForTest(
                        new ThrowingCollaborateModelClient("private provider failure"),
                        failurePrompt,
                        new TextBlock(),
                        () => currentSnapshot,
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        composerDraftStore: failureStore);
                    failure.Initialize();
                    failurePrompt.Text = "retain after Collaborate failure";
                    failure.SendAsync().GetAwaiter().GetResult();
                    var failureScope = ComposerDraftScopes.Collaborate(
                        currentSnapshot.SessionId,
                        currentSnapshot.SessionInstanceId,
                        failure.DebugCurrentConversationId,
                        "fast");
                    Require(failurePrompt.Text == "retain after Collaborate failure"
                            && failureStore.Get(failureScope) == failurePrompt.Text,
                        "a Collaborate provider exception should retain and rekey the visible draft");

                    var cancellationPrompt = new TextBox();
                    var cancellationStore = new ComposerDraftStore(Path.Combine(root, "cancel.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var cancellationClient = new CancellationBlockingModelClient();
                    var cancelled = CreateCollaborateCoordinatorForTest(
                        cancellationClient,
                        cancellationPrompt,
                        new TextBlock(),
                        () => currentSnapshot,
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        composerDraftStore: cancellationStore);
                    cancelled.Initialize();
                    cancellationPrompt.Text = "retain after Collaborate cancellation";
                    var cancelledTask = cancelled.SendAsync();
                    Require(cancellationClient.Started.Task.Wait(TimeSpan.FromSeconds(2)), "the cancellable Collaborate call should start");
                    cancelled.Stop();
                    PumpDocumentImportTask(cancelledTask);
                    var cancellationScope = ComposerDraftScopes.Collaborate(
                        currentSnapshot.SessionId,
                        currentSnapshot.SessionInstanceId,
                        cancelled.DebugCurrentConversationId,
                        "fast");
                    Require(cancellationPrompt.Text == "retain after Collaborate cancellation"
                            && cancellationStore.Get(cancellationScope) == cancellationPrompt.Text,
                        "a Collaborate cancellation should retain and rekey the visible draft");

                    var editPrompt = new TextBox();
                    var editStore = new ComposerDraftStore(Path.Combine(root, "edit.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var gateClient = new GatedDraftModelClient("completed after edit");
                    var edited = CreateCollaborateCoordinatorForTest(
                        gateClient,
                        editPrompt,
                        new TextBlock(),
                        () => currentSnapshot,
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        composerDraftStore: editStore);
                    edited.Initialize();
                    editPrompt.Text = "Collaborate text at send";
                    var editedTask = edited.SendAsync();
                    Require(gateClient.Started.Task.Wait(TimeSpan.FromSeconds(2)), "the gated Collaborate call should start");
                    editPrompt.Text = "Collaborate text edited while provider was running";
                    gateClient.Release.TrySetResult();
                    PumpDocumentImportTask(editedTask);
                    var editScope = ComposerDraftScopes.Collaborate(
                        currentSnapshot.SessionId,
                        currentSnapshot.SessionInstanceId,
                        edited.DebugCurrentConversationId,
                        "fast");
                    Require(editPrompt.Text == "Collaborate text edited while provider was running"
                            && editStore.Get(editScope) == editPrompt.Text,
                        "Collaborate must not clear text edited during an in-flight successful send");

                    var controlPrompt = new TextBox();
                    var controlStore = new ComposerDraftStore(Path.Combine(root, "control.dat"), debounceDelay: TimeSpan.FromHours(1));
                    var control = CreateCollaborateCoordinatorForTest(
                        new FixedCollaborateModelClient("control answer"),
                        controlPrompt,
                        new TextBlock(),
                        () => currentSnapshot,
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        composerDraftStore: controlStore);
                    control.Initialize();
                    controlPrompt.Text = "private visible Collaborate draft";
                    control.ControlSendAsync("control-plane Collaborate request").GetAwaiter().GetResult();
                    var controlScope = ComposerDraftScopes.Collaborate(
                        currentSnapshot.SessionId,
                        currentSnapshot.SessionInstanceId,
                        control.DebugCurrentConversationId,
                        "fast");
                    Require(controlPrompt.Text == "private visible Collaborate draft"
                            && controlStore.Get(controlScope) == controlPrompt.Text,
                        "Collaborate control-plane sends must preserve and rekey the visible draft");
                }
                finally
                {
                    DeleteDraftTestRoot(root);
                }
            });
        });
    }

    static void ComposerDraftScopesRestoreAcrossOperatorAgentAndCollaborateSwitches()
    {
        RunStaTest(() =>
        {
            var root = DraftTestRoot("scope-switch");
            try
            {
                var path = Path.Combine(root, "composer-drafts.dat");
                var store = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
                SessionSummary? activeSession = new("session-a", "", false, 0, 0, 0, DateTimeOffset.UtcNow);
                var first = CreateDraftOperatorCoordinator(root, store, () => activeSession);
                first.Coordinator.InitializeControls();
                first.Text.Text = "operator public A";
                first.Coordinator.UpdateTurnMeter();
                first.Coordinator.SetRouteMode("private");
                first.Text.Text = "operator private A";
                first.Coordinator.UpdateTurnMeter();
                first.Coordinator.SetRouteMode("public");
                first.Coordinator.ApplySnapshot(
                    SnapshotForOverviewTest(false, "", "", 0, [], []) with { SessionId = "session-b" });
                first.Text.Text = "operator public B";
                first.Coordinator.UpdateTurnMeter();
                Require(store.FlushAsync().GetAwaiter().GetResult(), "scoped Operator drafts should flush");

                var restartedStore = new ComposerDraftStore(path, debounceDelay: TimeSpan.FromHours(1));
                activeSession = new SessionSummary("session-a", "", false, 0, 0, 0, DateTimeOffset.UtcNow);
                var restarted = CreateDraftOperatorCoordinator(root, restartedStore, () => activeSession);
                restarted.Coordinator.InitializeControls();
                Require(restarted.Text.Text == "operator public A",
                    "Operator startup should restore the active session's public route draft");
                restarted.Coordinator.SetRouteMode("private");
                Require(restarted.Text.Text == "operator private A",
                    "Operator route switches should restore independent durable drafts");
                restarted.Coordinator.ApplySnapshot(
                    SnapshotForOverviewTest(false, "", "", 0, [], []) with { SessionId = "session-b" });
                Require(restarted.Text.Text == "operator public B",
                    "Operator session switches should restore independent durable drafts");

                var workspaceA = Path.Combine(root, "workspace-a");
                var workspaceB = Path.Combine(root, "workspace-b");
                Directory.CreateDirectory(workspaceA);
                Directory.CreateDirectory(workspaceB);
                var agentPrompt = new TextBox();
                var agentSettings = new WpfSettings { AgentWorkspacePath = workspaceA, AgentBuilderOnlyDefault = true };
                var agent = CreateWorkspaceProfileTestCoordinator(
                    agentSettings,
                    new WpfSettingsStore(Path.Combine(root, "agent-settings.json")),
                    (_, _) => Task.FromResult("profile"),
                    promptText: agentPrompt,
                    composerDraftStore: restartedStore);
                agent.Initialize();
                agentPrompt.Text = "Agent workspace A";
                agent.ControlSetWorkspace(workspaceB);
                Require(agentPrompt.Text == "", "a new Agent workspace should not leak another workspace's draft");
                agentPrompt.Text = "Agent workspace B";
                agent.ControlSetWorkspace(workspaceA);
                Require(agentPrompt.Text == "Agent workspace A",
                    "returning to a normalized Agent workspace should restore its hashed scope");
                agent.ControlSetWorkspace(workspaceB);
                Require(agentPrompt.Text == "Agent workspace B",
                    "Agent workspace B should retain its independent draft");
                agent.Dispose();

                var postDisposePreview = AgentWorkspaceCommand.BuildPreview(
                    workspaceB,
                    "Terminal",
                    "echo AI_ARENA_DISPOSE_SCOPE_OK");
                Require(postDisposePreview.Ok,
                    $"post-dispose command preview should remain valid: {postDisposePreview.Error}");
                var postDisposeResult = AgentWorkspaceCommand
                    .RunAsync(postDisposePreview, TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult();
                Require(postDisposeResult.Ok
                        && postDisposeResult.StandardOutput.Contains(
                            "AI_ARENA_DISPOSE_SCOPE_OK",
                            StringComparison.Ordinal),
                    "disposing one Agent workspace must not declare process-wide shutdown or cancel later work");
            }
            finally
            {
                DeleteDraftTestRoot(root);
            }
        });
    }

    static void CollaborateDocumentContextNeverLeaksSourcePath()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("document-path");
                try
                {
                    var sentinelRoot = Path.Combine(root, "PRIVATE-USER-SENTINEL", "Nested");
                    var sentinelPath = Path.Combine(sentinelRoot, "safe-title.md");
                    var client = new SequentialAgentModelClient("Answer using the document.");
                    var prompt = new TextBox();
                    var coordinator = CreateCollaborateCoordinatorForTest(
                        client,
                        prompt,
                        new TextBlock(),
                        () => SnapshotForOverviewTest(true, "draft-model", "", 0, [], []),
                        _ => { },
                        new RecordingCollaborateHistoryStore(),
                        toolDocumentStreamFactory: (_, _) => Task.FromResult<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("bounded document evidence"))));
                    coordinator.Initialize();
                    PumpDocumentImportTask(coordinator.ImportDocumentsAsync([sentinelPath]));
                    prompt.Text = "Use the imported evidence.";
                    PumpDocumentImportTask(coordinator.SendAsync());

                    var providerPrompt = string.Join(
                        "\n",
                        client.CompletedMessages.SelectMany(messages => messages).Select(message => message.Content));
                    Require(providerPrompt.Contains("safe-title.md", StringComparison.Ordinal)
                            && providerPrompt.Contains("bounded document evidence", StringComparison.Ordinal),
                        "provider context should retain a safe document title and bounded content");
                    Require(!providerPrompt.Contains(sentinelRoot, StringComparison.OrdinalIgnoreCase)
                            && !providerPrompt.Contains(sentinelPath, StringComparison.OrdinalIgnoreCase)
                            && !providerPrompt.Contains("PRIVATE-USER-SENTINEL", StringComparison.Ordinal),
                        "the full document path and private hierarchy must never cross the provider boundary");
                }
                finally
                {
                    DeleteDraftTestRoot(root);
                }
            });
        });
    }

    static void ComposerDraftInternalPromptPathsPreserveVisibleText()
    {
        var agent = ReadWorkspaceFile("src/AIArena.Wpf/Shell/AgentWorkspaceCoordinator.cs");
        Require(agent.Contains("await SendAsync(prompt ?? \"\", controlPrompt: true)", StringComparison.Ordinal),
            "Agent control sends should inject text without assigning the visible TextBox");
        Require(agent.Contains("await SendAsync(internalRescuePromptAfterChat)", StringComparison.Ordinal)
                && agent.Contains("var usesVisibleComposer = injectedPrompt is null", StringComparison.Ordinal),
            "Agent internal rescue sends should use the non-composer origin and therefore never clear visible text");
        Require(!agent.Contains("promptText.Text = BuildAutoContinuePrompt", StringComparison.Ordinal)
                && agent.Contains("await SendOnUiThreadAsync(autoContinuePrompt)", StringComparison.Ordinal),
            "generated Auto Continue prompts should remain transient instead of becoming recovered user drafts");

        var collaborate = ReadWorkspaceFile("src/AIArena.Wpf/Shell/CollaborateCoordinator.cs");
        Require(collaborate.Contains("await SendCoreAsync(prompt ?? \"\", controlPrompt: true)", StringComparison.Ordinal)
                && !collaborate.Contains("promptText.Text = prompt ??", StringComparison.Ordinal),
            "Collaborate control sends should bypass the visible composer");
    }

    private static AgentWorkspaceCoordinator CreateDraftAgentCoordinator(
        string root,
        string workspace,
        TextBox prompt,
        ComposerDraftStore store,
        IModelProviderClient modelClient,
        ArenaViewSnapshot snapshot)
    {
        var settings = new WpfSettings
        {
            AgentWorkspacePath = workspace,
            AgentBuilderOnlyDefault = true
        };
        return CreateWorkspaceProfileTestCoordinator(
            settings,
            new WpfSettingsStore(Path.Combine(root, $"agent-{Guid.NewGuid():N}.json")),
            (_, _) => Task.FromResult("profile"),
            modelClient: modelClient,
            snapshot: () => snapshot,
            promptText: prompt,
            composerDraftStore: store);
    }

    private static (OperatorTurnCoordinator Coordinator, TextBox Text) CreateDraftOperatorCoordinator(
        string root,
        ComposerDraftStore store,
        Func<SessionSummary?> activeSession,
        Func<ArenaViewSnapshot?>? renderedSnapshot = null)
    {
        var sessionStore = new SessionStore(root);
        var text = new TextBox();
        var coordinator = new OperatorTurnCoordinator(
            sessionStore,
            new EventLogStore(root),
            new TranscriptService(),
            new NarratorService(sessionStore: sessionStore, eventLogStore: new EventLogStore(root)),
            new DiscourseDiagnosticsService(),
            new WpfSettingsStore(Path.Combine(root, $"operator-{Guid.NewGuid():N}.json")),
            new Button(),
            new Button(),
            new Button(),
            new Grid(),
            new ComboBox(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            [new Button(), new Button(), new Button(), new Button()],
            new ComboBox(),
            new Button(),
            new Button(),
            new Button(),
            text,
            new Button(),
            () => new WpfSettings(),
            activeSession,
            renderedSnapshot ?? (() => activeSession() is { } current
                ? SnapshotForOverviewTest(false, "", "", 0, [], []) with { SessionId = current.Id }
                : null),
            () => false,
            AccentResourceBrush,
            (_, _, action, _) => action(),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            composerDraftStore: store);
        return (coordinator, text);
    }

    private static string DraftTestRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-composer-drafts", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDraftTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static void WriteProtectedDraftPayload(
        string path,
        IComposerDraftProtector protector,
        string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plaintext = Encoding.UTF8.GetBytes(json);
        try
        {
            File.WriteAllBytes(path, protector.Protect(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static async Task WriteDraftCiphertextAtomicAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class TestDpapiDraftProtector(string entropy) : IComposerDraftProtector
    {
        private readonly byte[] entropyBytes = Encoding.UTF8.GetBytes(entropy);

        public byte[] Protect(byte[] plaintext)
        {
            return ProtectedData.Protect(plaintext, entropyBytes, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            return ProtectedData.Unprotect(ciphertext, entropyBytes, DataProtectionScope.CurrentUser);
        }
    }

    private sealed class GatedDraftModelClient(string text) : IModelProviderClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderModels> ListModelsAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderModels(
                true,
                config.BaseUrl,
                [config.Model],
                "",
                DateTimeOffset.Now));
        }

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                text,
                "",
                1,
                0,
                0,
                Math.Max(1, text.Length / 4),
                "",
                DateTimeOffset.Now);
        }
    }
}
