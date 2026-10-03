using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ContextRecoveryCommitTests
{
    public static void CommittedActionsRetainResultsWhenEvidenceFails()
    {
        foreach (var operation in new[] { "skip", "end", "continue" })
        foreach (var fault in new[] { "io", "cancel", "none" })
        {
            WithFixture(operation, (store, original) =>
            {
                using var cancellation = new CancellationTokenSource();
                var writes = 0;
                var durableAtEvidence = false;
                var terminalWrite = operation == "continue" ? 2 : 1;
                var log = new EventLogStore(store.DataRoot, new EventLogWriteObserver
                {
                    BeforeWrite = _ =>
                    {
                        if (++writes != terminalWrite) return;
                        durableAtEvidence = IsCommitted(operation, Load(store));
                        if (fault == "cancel")
                        {
                            cancellation.Cancel();
                            throw new OperationCanceledException("private evidence token", cancellation.Token);
                        }
                        if (fault == "io") throw new IOException("private path C:\\private\\session and token sk-fixture-secret");
                    }
                });
                var provider = new RecoveryProvider(config => Success(config));
                var recovery = new ContextRecoveryService(store, provider, eventLogStore: log);
                ContextRecoveryResult? result = null;
                Exception? observed = null;
                try { result = Invoke(recovery, operation, original, cancellation.Token).GetAwaiter().GetResult(); }
                catch (Exception exception) { observed = exception; }

                var saved = Load(store);
                Require(durableAtEvidence && IsCommitted(operation, saved),
                    $"{operation}/{fault}: the fault must occur after the recovery action is durably saved");
                Require(observed is null,
                    $"{operation}/{fault}: a durably saved recovery action threw {observed?.GetType().Name} during secondary evidence");
                Require(result is { Ok: true, Executed: true, Message: not null } && result.Error.Length == 0,
                    $"{operation}/{fault}: secondary evidence must not turn the committed recovery into failure");
                // Check the public serialized contract as well as the durable outcome.
                var contract = JsonSerializer.SerializeToElement(result);
                Require(contract.TryGetProperty("Committed", out var committed) && committed.GetBoolean()
                        && contract.TryGetProperty("EvidenceWarning", out var warning)
                        && warning.GetString() == (fault == "none" ? "" : PostCommitEvidence.ActivityLogWarning),
                    $"{operation}/{fault}: the public result must expose a committed discriminator and a safe evidence warning");
                Require(!contract.ToString().Contains("private", StringComparison.OrdinalIgnoreCase)
                        && !contract.ToString().Contains("sk-fixture-secret", StringComparison.Ordinal),
                    $"{operation}/{fault}: serialized recovery outcomes must not expose evidence exception details");
                Require(writes == terminalWrite && provider.Calls == (operation == "continue" ? 1 : 0)
                        && saved.Engine.LastError.Length == 0 && saved.Engine.Messages.Count == 2,
                    $"{operation}/{fault}: completion must not replay provider work or append another transcript message");
                if (operation == "continue")
                {
                    Require(result!.Completion is { Ok: true, Text: " and the remainder.", FailureKind: ModelCompletionFailureKind.None }
                            && result.Message!.Text == saved.Engine.Messages[1].Text
                            && saved.Engine.Messages[1].MessageId == original.MessageId,
                        "Continuation must return its original provider outcome and its one saved replacement.");
                }

                var revision = saved.PersistenceRevision;
                var duplicate = Invoke(recovery, operation, original, CancellationToken.None).GetAwaiter().GetResult();
                Require(!duplicate.Ok && !duplicate.Executed && Load(store).PersistenceRevision == revision
                        && provider.Calls == (operation == "continue" ? 1 : 0),
                    $"{operation}/{fault}: a repeated recovery command must not redo the committed action");
            });
        }

        WithFixture("skip", (store, original) =>
        {
            using var cancellation = new CancellationTokenSource();
            using var evidenceStarted = new ManualResetEventSlim();
            FileStream? blockedRead = null;
            var log = new EventLogStore(store.DataRoot, new EventLogWriteObserver
            {
                Clock = () =>
                {
                    // Event serialization follows the durable save. Force one
                    // temporarily unavailable read so the polling contract is
                    // deterministic instead of depending on a filesystem race.
                    blockedRead = File.Open(store.SnapshotPath(), FileMode.Open, FileAccess.Read, FileShare.None);
                    evidenceStarted.Set();
                    return DateTimeOffset.Now;
                }
            });
            using var blockedEvidence = CrossProcessWriteLease.AcquireAsync(
                log.EventPath(), TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            var provider = new RecoveryProvider(config => Success(config));
            var recovery = new ContextRecoveryService(store, provider, eventLogStore: log);
            var pending = Invoke(recovery, "skip", original, cancellation.Token);
            try
            {
                Require(evidenceStarted.Wait(TimeSpan.FromSeconds(5)),
                    "Skip did not reach evidence serialization after its durable save.");
                Require(!ObserveCommittedSkip(), "An unavailable snapshot read must keep the commit poll pending.");
                blockedRead!.Dispose();
                blockedRead = null;
                Require(SpinWait.SpinUntil(ObserveCommittedSkip, TimeSpan.FromSeconds(5)),
                    "Skip must save its disposition before waiting for the evidence lease.");
                cancellation.Cancel();
                blockedEvidence.Dispose();
                var result = pending.GetAwaiter().GetResult();
                Require(result.Ok && result.Committed && result.EvidenceWarning.Length == 0 && provider.Calls == 0
                        && File.ReadAllText(log.EventPath()).Contains("context_recovery_turn_skipped", StringComparison.Ordinal),
                    "Cancellation after Skip commits must not cancel its independent final evidence or report a failed action.");
            }
            finally
            {
                blockedRead?.Dispose();
                blockedEvidence.Dispose();
                // Do not let an assertion leave background evidence work racing
                // fixture-directory cleanup.
                pending.GetAwaiter().GetResult();
            }

            bool ObserveCommittedSkip()
            {
                var current = store.LoadSnapshotAsync().GetAwaiter().GetResult();
                return current is not null && IsCommitted("skip", current);
            }
        });

        WithFixture("continue", (store, original) =>
        {
            using var cancellation = new CancellationTokenSource();
            var completedCallbacks = 0;
            var provider = new RecoveryProvider(config => Success(config));
            var log = new EventLogStore(store.DataRoot);
            var recovery = new ContextRecoveryService(store, provider, eventLogStore: log);
            var progress = new InlineProgress(value =>
            {
                if (value.Kind != ArenaTurnProgressKind.Completed) return;
                Require(IsCommitted("continue", Load(store)), "Completed must follow the durable replacement.");
                completedCallbacks++;
                cancellation.Cancel();
            });
            var result = recovery.ContinueOutputAsync("default", original.Turn, original.SpeakerId,
                original.CreatedAt, cancellation.Token, progress).GetAwaiter().GetResult();
            Require(result.Ok && completedCallbacks == 1 && provider.Calls == 1
                    && File.ReadAllText(log.EventPath()).Contains("context_recovery_output_continued", StringComparison.Ordinal),
                "Cancellation from the Completed observer must preserve the saved continuation and its final evidence.");
        });
    }

    public static void UncommittedActionsPreserveFailureAndCancellation()
    {
        foreach (var operation in new[] { "skip", "end", "continue" })
        foreach (var fault in new[] { "cancel", "snapshot" })
        {
            WithFixture(operation, (store, original) =>
            {
                var before = File.ReadAllBytes(store.SnapshotPath());
                using var cancellation = new CancellationTokenSource();
                if (fault == "cancel") cancellation.Cancel();
                var provider = new RecoveryProvider(config => Success(config));
                var log = new EventLogStore(store.DataRoot);
                var recovery = new ContextRecoveryService(store, provider, eventLogStore: log);
                Exception? observed = null;
                using (var locked = fault == "snapshot"
                    ? File.Open(store.SnapshotPath(), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                {
                    try { Invoke(recovery, operation, original, cancellation.Token).GetAwaiter().GetResult(); }
                    catch (Exception exception) { observed = exception; }
                }
                Require(fault == "cancel" ? observed is OperationCanceledException : observed is IOException or UnauthorizedAccessException,
                    $"{operation}/{fault}: precommit cancellation and failed saves must remain observable");
                Require(before.SequenceEqual(File.ReadAllBytes(store.SnapshotPath())) && provider.Calls == 0
                        && !File.Exists(log.EventPath()),
                    $"{operation}/{fault}: precommit failure must not change durable state or call a provider");
            });
        }

        WithFixture("continue", (store, original) =>
        {
            var provider = new RecoveryProvider(config => Success(config) with
            {
                Ok = false, Text = "", Error = "Fixture provider rejection",
                FailureKind = ModelCompletionFailureKind.ProviderRejected,
                StopReason = ModelCompletionStopReason.ProviderError
            });
            var recovery = new ContextRecoveryService(store, provider);
            var result = Invoke(recovery, "continue", original, CancellationToken.None).GetAwaiter().GetResult();
            var contract = JsonSerializer.SerializeToElement(result);
            var saved = Load(store).Engine.Messages[1];
            Require(!result.Ok && result.Executed && result.Error == "Fixture provider rejection"
                    && result.Completion?.FailureKind == ModelCompletionFailureKind.ProviderRejected
                    && contract.TryGetProperty("Committed", out var committed) && !committed.GetBoolean()
                    && contract.TryGetProperty("EvidenceWarning", out var warning) && warning.GetString() == ""
                    && saved.Text == original.Text && !saved.Metadata.ContainsKey("output_continuation_attempt")
                    && saved.Metadata["output_continuation_state"].GetString() == "failed" && provider.Calls == 1,
                "A failed provider continuation must retain its failure and original answer without claiming a committed replacement.");
        });
    }

    private static Task<ContextRecoveryResult> Invoke(ContextRecoveryService service, string operation,
        DialogueMessage original, CancellationToken cancellationToken) => operation switch
    {
        "skip" => service.SkipBlockedTurnAsync("default", original.Turn, original.SpeakerId, original.CreatedAt, cancellationToken),
        "end" => service.EndMatchAsync("default", "operator chose to end", cancellationToken),
        _ => service.ContinueOutputAsync("default", original.Turn, original.SpeakerId, original.CreatedAt, cancellationToken)
    };

    private static bool IsCommitted(string operation, ArenaSnapshot snapshot) => operation switch
    {
        "skip" => snapshot.Engine.Messages[1].Metadata.TryGetValue(ContextRecoveryService.RecoveryDispositionMetadataKey, out var disposition)
            && disposition.GetString() == "skipped" && TurnRunnerService.UnresolvedContextFailure(snapshot) is null,
        "end" => snapshot.Engine.MatchEnded && snapshot.Engine.MatchEndReason == "operator chose to end",
        _ => snapshot.Engine.Messages[1].Text == "The first half and the remainder."
            && !snapshot.Engine.Messages[1].Metadata.ContainsKey("output_continuation_attempt")
            && snapshot.Engine.Messages[1].Metadata[ContextRecoveryService.ContinuationCountMetadataKey].GetInt32() == 1
    };

    private static void WithFixture(string operation, Action<SessionStore, DialogueMessage> action)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "ai-arena-recovery-commit-";
        var root = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.Messages.Clear();
            snapshot.Engine.FactoryMode = false;
            snapshot.Engine.LastError = "original recovery blocker";
            var config = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.OpenAiCompatible,
                Model = "recovery-fixture", ContextLength = 16_384, ConfiguredContextWindow = 16_384,
                HistoryPolicy = ModelHistoryPolicies.Strict, MaxOutputTokens = 256
            };
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(new DialogueMessage
            {
                MessageId = "message:operator", Turn = 1, SpeakerId = "operator", Speaker = "Operator", Text = "Write the result", CreatedAt = 1
            });
            var original = new DialogueMessage
            {
                MessageId = "message:recovery", Turn = 2, SpeakerId = "alpha", Speaker = "Alpha", CreatedAt = 2,
                Text = operation == "skip" ? "Model call failed: maximum context length exceeded" : "The first half",
                Status = operation == "skip" ? "error" : "ok"
            };
            original.Metadata["completion_failure_kind"] = JsonSerializer.SerializeToElement(operation == "skip" ? "context_limit_exceeded" : "none");
            original.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement(operation == "skip" ? "provider_error" : "output_limit_reached");
            CompletionRouteReceipt.Stamp(original, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(original);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            action(store, original);
        }
        catch (Exception exception)
        {
            // The lightweight harness prints only Exception.Message. Retain the
            // actual failure site for concurrency-sensitive fixture diagnostics.
            Console.Error.WriteLine($"Recovery commit fixture ({operation}): {exception}");
            throw;
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            Require(string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(Path.GetFileName(resolved)[prefix.Length..], "N", out _),
                "Refusing cleanup outside the generated recovery fixture directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static ArenaSnapshot Load(SessionStore store) => store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    private static ModelCompletionResult Success(ModelProviderConfig config) => new(
        true, config.BaseUrl, config.Model, " and the remainder.", "", 1, 10, 5, 15, "", DateTimeOffset.Now,
        StopReason: ModelCompletionStopReason.Completed);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecoveryProvider(Func<ModelProviderConfig, ModelCompletionResult> complete) : IModelProviderClient
    {
        public int Calls { get; private set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(complete(config));
        }
    }
    private sealed class InlineProgress(Action<ArenaTurnProgress> report) : IProgress<ArenaTurnProgress>
    {
        public void Report(ArenaTurnProgress value) => report(value);
    }
}
