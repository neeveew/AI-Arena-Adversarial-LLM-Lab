using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static partial class ArenaTurnStreamingTests
{
    public static void ReasoningOnlyFactoryRetryPreservesExactConversation()
    {
        WithFixture((store, log, _) =>
        {
            const string raw = "\t  Compare these two approaches.\r\nKeep the exact user input.  ";
            var snapshot = Load(store);
            snapshot.Engine.FactoryMode = true;
            snapshot.Engine.Steering.Topic = "DO_NOT_INJECT_SCENARIO";
            snapshot.Engine.Agents.Single(agent => agent.Id == "alpha").Persona = "DO_NOT_INJECT_PERSONA";
            snapshot.Engine.Messages.Add(Public(1, "operator", raw));
            snapshot.Engine.TurnCount = 1;
            SetReasoning(snapshot, "high");
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecoveryClient((config, _, progress, _, call) =>
            {
                Require(Load(store).Engine.Messages.Count == 1 && Load(store).Engine.TurnCount == 1,
                    "neither the first reasoning-only response nor the recovery request may advance public history");
                if (call == 1) return Task.FromResult(ReasoningOnly(config, ModelCompletionStopReason.Unknown));
                progress?.Report(Answer);
                return Task.FromResult(Success(config, Answer));
            });
            var observer = new Recorder(store);
            var runner = new TurnRunnerService(client, store, log,
                runtimeEvidenceResolver: new FixedRuntimeEvidence(2048, canDisable: true));
            var result = runner.RunAgentTurnAsync("default", "alpha", progress: observer).GetAwaiter().GetResult();
            Require(result.Ok && result.Message?.Status == "ok", $"Factory recovery failed: {result.Error}");
            Require(client.Requests.Count == 2 && client.Requests[0].Config.Model == client.Requests[1].Config.Model
                && client.Requests[0].Config.Reasoning == "high" && client.Requests[1].Config.Reasoning == "off",
                "exactly one supported same-model reduction must follow reasoning-only completion");
            Require(client.Requests[0].Messages.SequenceEqual(client.Requests[1].Messages)
                && client.Requests[1].Messages.Count == 1 && client.Requests[1].Messages[0].Role == "user"
                && client.Requests[1].Messages[0].Content == raw,
                "Factory recovery must preserve original bytes and add no repair persona or instruction");
            VerifyCommitted(observer, result.Message!, expectedDeltas: 1);
            var evidence = result.Message!.Metadata[ArenaCompletionExecutor.RecoveryMetadataKey];
            Require(evidence.GetProperty("attempted").GetBoolean()
                && evidence.GetProperty("initial_stop_reason").GetString() == "unknown"
                && evidence.GetProperty("output_cap_reached_inferred").GetBoolean(),
                "usage reaching the cap must remain an inference separate from absent provider stop evidence");
            Require(Load(store).Engine.Messages.Count == 2 && Load(store).Engine.TurnCount == 2,
                "only the final recovered response may be committed");
            Require(Load(store).Configs["shared"].Reasoning == "high",
                "request-only recovery must not overwrite saved reasoning preferences");
        });
        var anchoredConfig = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
            Model = "anchored-model", Reasoning = "high", NativeStatefulChat = true,
            PreviousResponseId = "resp_original_committed_anchor",
            RuntimeEvidence = new ModelRuntimeEvidence(4096, true, "fixture-instance", DateTimeOffset.UtcNow, "fixture")
        };
        var anchoredClient = new RecoveryClient((config, _, _, _, call) => Task.FromResult(call == 1
            ? ReasoningOnly(config, ModelCompletionStopReason.Unknown) with { ResponseId = "resp_failed_reasoning_only" }
            : Success(config, Answer) with { ResponseId = "resp_recovered_public_answer" }));
        var recovered = new ArenaCompletionExecutor(anchoredClient, null).CompleteAsync(anchoredConfig,
            [new ModelChatMessage("user", "Continue from the committed conversation.")], CancellationToken.None).GetAwaiter().GetResult();
        Require(anchoredClient.Requests.Count == 2 && anchoredClient.Requests.All(request =>
                request.Config.NativeStatefulChat && request.Config.PreviousResponseId == "resp_original_committed_anchor")
            && recovered.ResponseId == "resp_recovered_public_answer",
            "native recovery must reuse the original committed server anchor and never accept the failed reasoning-only response ID");
    }

    public static void ReasoningOnlyRecoveryHonorsCapabilitiesAndTerminalFailures()
    {
        foreach (var scenario in new[] { "unsupported", "already_off", "already_low", "low", "twice", "transport", "context", "tool", "cancel" })
        {
            WithFixture((store, log, _) =>
            {
                var snapshot = Load(store);
                SetReasoning(snapshot, scenario == "already_off" ? "off" : scenario == "already_low" ? "low" : "high");
                store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
                using var cts = new CancellationTokenSource();
                var client = new RecoveryClient((config, _, progress, token, call) =>
                {
                    if (scenario == "cancel")
                    {
                        cts.Cancel();
                        return Task.FromCanceled<ModelCompletionResult>(token);
                    }
                    if (call > 1 && scenario != "twice")
                    {
                        progress?.Report(Answer);
                        return Task.FromResult(Success(config, Answer));
                    }
                    var result = ReasoningOnly(config, ModelCompletionStopReason.OutputLimitReached);
                    return Task.FromResult(scenario switch
                    {
                        "transport" => result with { FailureKind = ModelCompletionFailureKind.Transport, StopReason = ModelCompletionStopReason.ProviderError },
                        "context" => result with { FailureKind = ModelCompletionFailureKind.ContextLimitExceeded, StopReason = ModelCompletionStopReason.ProviderError },
                        "tool" => result with { StopReason = ModelCompletionStopReason.ToolCall },
                        _ => result
                    });
                });
                var runner = new TurnRunnerService(client, store, log, runtimeEvidenceResolver: new FixedRuntimeEvidence(
                    8192, canDisable: scenario is not ("unsupported" or "low" or "already_low"),
                    reduced: scenario is "low" or "already_low" ? "low" : ""));
                if (scenario == "cancel")
                {
                    var canceled = false;
                    try { runner.RunOneTurnAsync("default", cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { canceled = true; }
                    Require(canceled && Load(store).Engine.Messages.Count == 0 && client.Requests.Count == 1,
                        "cancellation must neither recover nor persist an answer");
                    return;
                }
                var turn = runner.RunOneTurnAsync("default").GetAwaiter().GetResult();
                var expectedCalls = scenario is "low" or "twice" ? 2 : 1;
                Require(client.Requests.Count == expectedCalls,
                    $"{scenario} expected {expectedCalls} calls, got {client.Requests.Count}");
                Require(Load(store).Engine.Messages.Count == 1,
                    "every terminal operation must persist at most its single final response/error");
                if (scenario == "low")
                    Require(turn.Message?.Status == "ok" && client.Requests[1].Config.Reasoning == "low",
                        "a provider that supports reduction but not disabling must receive its documented low mode");
                else if (scenario is "unsupported" or "already_off" or "already_low" or "twice")
                    Require(turn.Message?.Status == "error" && turn.Message.Text.Contains("reasoning but no public answer", StringComparison.Ordinal),
                        "unrecoverable reasoning-only output needs a clear terminal explanation without invented public content");
            });
        }
    }

    public static void NarratorAndContinuationShareBoundedReasoningRecovery()
    {
        WithFixture((store, log, _) =>
        {
            var snapshot = Load(store);
            SetReasoning(snapshot, "high");
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecoveryClient((config, _, progress, _, call) =>
            {
                if (call == 1) return Task.FromResult(ReasoningOnly(config, ModelCompletionStopReason.Completed));
                progress?.Report(Answer);
                return Task.FromResult(Success(config, Answer));
            });
            using var narrator = new NarratorService(client, store, log,
                runtimeEvidenceResolver: new FixedRuntimeEvidence(4096, canDisable: true));
            var notes = narrator.AskNarratorAsync("default", "Explain the key uncertainty.").GetAwaiter().GetResult();
            Require(notes.Ok && notes.Message?.Status == "ok" && client.Requests.Count == 2
                && client.Requests[0].Messages.SequenceEqual(client.Requests[1].Messages),
                "narration must use the same bounded recovery and preserve the original narrator prompt");
        });
        WithFixture((store, log, _) =>
        {
            var snapshot = Load(store);
            SetReasoning(snapshot, "high");
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecoveryClient((config, _, progress, _, call) =>
            {
                if (call == 1) return Task.FromResult(Success(config, Answer) with { StopReason = ModelCompletionStopReason.OutputLimitReached });
                if (call == 2) return Task.FromResult(ReasoningOnly(config, ModelCompletionStopReason.Unknown));
                progress?.Report(" The final continuation supplies the remaining explanation.");
                return Task.FromResult(Success(config, " The final continuation supplies the remaining explanation."));
            });
            var runtime = new FixedRuntimeEvidence(4096, canDisable: true);
            var initial = new TurnRunnerService(client, store, log, runtimeEvidenceResolver: runtime)
                .RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(initial.Message is not null, "initial partial is missing");
            var recovery = new ContextRecoveryService(store, client, eventLogStore: log, runtimeEvidenceResolver: runtime);
            var result = recovery.ContinueOutputAsync("default", initial.Message!.Turn, "alpha", initial.Message.CreatedAt).GetAwaiter().GetResult();
            Require(result.Ok && client.Requests.Count == 3 && client.Requests[1].Messages.SequenceEqual(client.Requests[2].Messages)
                && client.Requests[2].Config.Reasoning == "off" && Load(store).Engine.Messages.Count == 1,
                "continuation must recover once on its existing exact prompt and atomically replace the original card");
            Require(result.Message!.Metadata[ArenaCompletionExecutor.RecoveryMetadataKey].GetProperty("attempted").GetBoolean(),
                "continued message must retain recovery evidence");
        });
    }

    public static void ActiveContextFitsRequestsWithoutChangingSavedPreferences()
    {
        WithFixture((store, log, _) =>
        {
            var snapshot = Load(store);
            snapshot.Engine.Messages.Add(Public(1, "operator", "Explain this short problem clearly."));
            snapshot.Engine.TurnCount = 1;
            snapshot.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1", Model = "small-context", MaxOutputTokens = 1024,
                ConfiguredContextWindow = 0, ContextLength = 0, HistoryPolicy = ModelHistoryPolicies.Rolling80
            };
            snapshot.ModelSettings.Clear();
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var before = Load(store).Configs["shared"];
            var client = new RecoveryClient((config, messages, _, _, _) =>
            {
                Require(ArenaHistoryBudgetService.EstimateTokens(messages) + config.MaxOutputTokens <= 1638,
                    "actual prompt and generation allowance must fit 80% of the confirmed 2,048-token context");
                Require(config.RuntimeEvidence?.ContextWindow == 2048,
                    "request clones and prepared context must retain fresh loaded-instance evidence");
                return Task.FromResult(Success(config, Answer));
            });
            var turn = new TurnRunnerService(client, store, log,
                runtimeEvidenceResolver: new FixedRuntimeEvidence(2048, canDisable: false))
                .RunOneTurnAsync("default").GetAwaiter().GetResult();
            Require(turn.Message?.Status == "ok" && client.Requests.Count == 1, $"active context fitting failed: {turn.Message?.Text}");
            var after = Load(store).Configs["shared"];
            Require(before.ConfiguredContextWindow == after.ConfiguredContextWindow && after.ConfiguredContextWindow == 0
                && after.MaxOutputTokens == 1024 && after.RuntimeEvidence is null,
                "fitted context/output and live evidence must remain request-only under Server default");
        });
        var config = ArenaRequestBudget.Copy(Config("strict"), maxOutputTokens: 1024,
            runtimeEvidence: new ModelRuntimeEvidence(2048, false, "instance", DateTimeOffset.UtcNow, "fixture"), replaceRuntimeEvidence: true);
        var exact = new[] { new ModelChatMessage("user", new string('x', 3200)) };
        var strictSnapshot = SessionStore.CreateDefaultSnapshot();
        var fit = ArenaHistoryBudgetService.Build(strictSnapshot, config, null, null, _ => exact);
        Require(fit.Ok && fit.Messages.SequenceEqual(exact) && fit.OutputTokenLimit is > 0 and < 1024,
            "Strict may reduce generation allowance when necessary but must preserve every prompt byte");
        var tooLarge = new[] { new ModelChatMessage("user", new string('x', 10000)) };
        var rejected = ArenaHistoryBudgetService.Build(strictSnapshot, config, null, null, _ => tooLarge);
        Require(!rejected.Ok && rejected.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && rejected.Messages.SequenceEqual(tooLarge), "oversized Strict text must fail clearly without silent truncation");
    }

    public static void FactoryActiveBudgetRetainsCriticalMessagesAndFrozenRetry()
    {
        WithFixture((store, log, _) =>
        {
            var snapshot = Load(store);
            snapshot.Engine.FactoryMode = true;
            snapshot.Engine.Messages.Add(Public(1, "operator", "EXACT ROOT\r\n  preserved."));
            snapshot.Engine.TurnCount = 1;
            var factory = new FactoryConversationService();
            factory.Resolve(snapshot);
            snapshot.Engine.Messages.Add(Public(2, "operator", "LATEST OPERATOR: keep this requirement."));
            for (var turn = 3; turn <= 62; turn++)
                snapshot.Engine.Messages.Add(Public(turn, turn % 2 == 0 ? "alpha" : "beta", $"Entry {turn}: " + new string('x', 240)));
            snapshot.Engine.TurnCount = 62;
            var config = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1", Model = "small-factory", MaxOutputTokens = 512,
                HistoryPolicy = ModelHistoryPolicies.Rolling80, Reasoning = "off"
            };
            snapshot.Configs["shared"] = config;
            snapshot.ModelSettings.Clear();
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecoveryClient((request, messages, _, _, _) =>
            {
                var text = string.Join("\n", messages.Select(item => item.Content));
                Require(text.Contains("EXACT ROOT\r\n  preserved.", StringComparison.Ordinal)
                    && text.Contains("LATEST OPERATOR: keep this requirement.", StringComparison.Ordinal)
                    && text.Contains("Entry 62:", StringComparison.Ordinal),
                    "token fitting must preserve root, latest Operator even outside the old tail, and latest public answer");
                Require(ArenaHistoryBudgetService.EstimateTokens(messages) + request.MaxOutputTokens <= 1638,
                    "Factory provider request must fit the confirmed active context");
                return Task.FromResult(Success(request, Answer));
            });
            var runner = new TurnRunnerService(client, store, log,
                runtimeEvidenceResolver: new FixedRuntimeEvidence(2048, canDisable: false));
            var first = runner.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(first.Message?.Status == "ok", $"Factory budgeting failed: {first.Error}");
            var receipt = first.Message!.Metadata[FactoryConversationService.BudgetReceiptMetadataKey].Deserialize<ArenaHistoryBudgetReceipt>()!;
            Require(receipt.IncludedEntryCount < 50 && receipt.OmittedEntryCount > 0,
                "small Factory context must omit older whole entries and record exact retained counts");
            var retry = runner.RetryTurnAsync("default", first.Message.Turn, first.Message.SpeakerId,
                first.Message.CreatedAt).GetAwaiter().GetResult();
            Require(retry.Message?.Status == "ok" && client.Requests.Count == 2
                && client.Requests[0].Messages.SequenceEqual(client.Requests[1].Messages)
                && retry.Message.Metadata[FactoryConversationService.ContextFingerprintMetadataKey].GetString()
                    == first.Message.Metadata[FactoryConversationService.ContextFingerprintMetadataKey].GetString(),
                "retry must reconstruct the original selected IDs and exact provider fingerprint rather than select a fresh history window");
            var changed = Load(store);
            var originalRoot = changed.Engine.Messages[0];
            changed.Engine.Messages[0] = Public(originalRoot.Turn, originalRoot.SpeakerId, "ALTERED ROOT");
            // Preserve the root identity/contract while changing its bytes.
            changed.Engine.Messages[0].MessageId = originalRoot.MessageId;
            foreach (var pair in originalRoot.Metadata) changed.Engine.Messages[0].Metadata[pair.Key] = pair.Value;
            store.SaveSnapshotAsync(changed).GetAwaiter().GetResult();
            var invalid = runner.RetryTurnAsync("default", retry.Message!.Turn, retry.Message.SpeakerId,
                retry.Message.CreatedAt).GetAwaiter().GetResult();
            Require(!invalid.Ok && client.Requests.Count == 2,
                "changed retained public bytes must invalidate the frozen Factory retry before any model call");
        });
    }

    private static DialogueMessage Public(int turn, string speaker, string text) => new()
    {
        Turn = turn, SpeakerId = speaker, Speaker = speaker, Text = text, Status = "ok", Kind = "message", CreatedAt = turn
    };

    private static void SetReasoning(ArenaSnapshot snapshot, string reasoning)
    {
        snapshot.Configs["shared"] = ArenaRequestBudget.Copy(snapshot.Configs["shared"], reasoning: reasoning);
    }

    private static ModelCompletionResult ReasoningOnly(ModelProviderConfig config, ModelCompletionStopReason stop) =>
        Success(config, "") with
        {
            Ok = false, Reasoning = "Internal reasoning that never reached a public answer.",
            CompletionTokens = config.MaxOutputTokens, FailureKind = ModelCompletionFailureKind.EmptyPublicContent,
            StopReason = stop, Error = "Provider returned a successful response without assistant content."
        };

    internal sealed class FixedRuntimeEvidence(int context, bool canDisable, string reduced = "") : IModelRuntimeEvidenceResolver
    {
        public Task<ModelRuntimeEvidence?> ResolveAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelRuntimeEvidence?>(new(context, canDisable, "fixture-loaded-instance", DateTimeOffset.UtcNow,
                "isolated_fixture", reduced));
    }

    private sealed class RecoveryClient(Func<ModelProviderConfig, IReadOnlyList<ModelChatMessage>, IProgress<string>?,
        CancellationToken, int, Task<ModelCompletionResult>> complete) : IModelProviderClient, IStreamingModelProviderClient
    {
        internal List<(ModelProviderConfig Config, IReadOnlyList<ModelChatMessage> Messages)> Requests { get; } = [];
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default) =>
            CompleteChatStreamingAsync(config, messages, null, cancellationToken);
        public Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            Requests.Add((config, messages.ToArray()));
            return complete(config, messages, progress, cancellationToken, Requests.Count);
        }
    }
}
