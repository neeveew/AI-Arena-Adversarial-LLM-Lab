using System.Net;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ContextWindowTests
{
    public static void DistinguishesCleanAndLegacyModelDefaults()
    {
        var clean = SessionStore.CreateDefaultSnapshot();
        var config = Config("model-a");
        clean.Configs["shared"] = config;
        var cleanResolved = ModelProviderRouting.Resolve(clean, "alpha", out _)!;
        Require(clean.ModelSettingsVersion == ModelRuntimeSettingsRegistry.CurrentSchemaVersion,
            "clean snapshots must declare the canonical settings schema");
        Require(cleanResolved.HistoryPolicy == ModelHistoryPolicies.Rolling80,
            "a newly routed clean-session model must default to rolling_80");

        var legacy = new ArenaSnapshot();
        legacy.Configs["shared"] = config;
        var legacyResolved = ModelProviderRouting.Resolve(legacy, "alpha", out _)!;
        Require(legacy.ModelSettingsVersion == 0 && legacyResolved.HistoryPolicy == ModelHistoryPolicies.Strict,
            "missing legacy schema markers must preserve strict history");
    }

    public static void ResolvesCanonicalAndRawAliases()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var canonical = Config("publisher/model.gguf");
        var rawAlias = Config("model.gguf");
        var entries = ModelRuntimeSettingsRegistry.RegisterAliases(
            snapshot,
            [canonical, rawAlias],
            32_768,
            ModelHistoryPolicies.Chaptered,
            ModelResponseTones.Direct,
            "ignored");
        Require(entries.Count == 2 && snapshot.ModelSettings.Count == 2,
            "equivalent catalog and role aliases must each receive opaque registry identities");
        var resolved = ModelRuntimeSettingsRegistry.Resolve(snapshot, rawAlias);
        Require(resolved.ConfiguredContextWindow == 32_768
            && resolved.HistoryPolicy == ModelHistoryPolicies.Chaptered
            && resolved.ResponseTone == ModelResponseTones.Direct,
            "raw routed aliases must resolve settings registered from a canonical catalog row");
        Require(snapshot.ModelSettings.Keys.All(key => key.StartsWith("model-settings:", StringComparison.Ordinal)
            && !key.Contains("model.gguf", StringComparison.OrdinalIgnoreCase)),
            "registry keys must not disclose provider model names");
    }

    public static void RollingBudgetDropsOnlyWholeOldestEntries()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.TranscriptWindow = 1;
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 12; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn == 12 ? "operator" : "beta",
                turn == 12 ? "Operator" : "Beta",
                $"ENTRY_{turn:D2}_BEGIN {new string((char)('a' + turn % 20), 900)} ENTRY_{turn:D2}_END"));
        }
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "failed-message-13",
            Turn = 13,
            SpeakerId = "beta",
            Speaker = "Beta",
            Text = "FAILED_CONTEXT_ROW_MUST_NOT_LEAK",
            Status = "error",
            Kind = "message",
            CreatedAt = 13
        });
        snapshot.Engine.TurnCount = 13;
        var config = Config("rolling-model", 4_096, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot,
            config,
            beforeTurn: null,
            transcriptAfterTurn: null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));

        Require(built.Ok && built.Receipt is not null, "rolling_80 must emit a durable budget receipt");
        var receipt = built.Receipt!;
        Require(receipt.OmittedEntryCount > 0 && receipt.IncludedEntryCount > 0,
            "the fixture must exercise both retained and omitted entries");
        Require(receipt.EligibleEntryCount == 12,
            "rolling history must ignore failed rows and consider successful Arena history independently of TranscriptWindow");
        var prompt = string.Join("\n", built.Messages.Select(message => message.Content));
        Require(!prompt.Contains("FAILED_CONTEXT_ROW_MUST_NOT_LEAK", StringComparison.Ordinal),
            "context-limit and other failed transcript rows must not leak into later Arena prompts");
        foreach (var message in snapshot.Engine.Messages)
        {
            var retained = receipt.IncludedMessageIds.Contains(message.MessageId!, StringComparer.Ordinal);
            Require(retained == prompt.Contains($"ENTRY_{message.Turn:D2}_BEGIN", StringComparison.Ordinal),
                $"entry {message.Turn} must be retained or omitted as a whole");
            Require(retained == prompt.Contains($"ENTRY_{message.Turn:D2}_END", StringComparison.Ordinal),
                $"entry {message.Turn} text must never be trimmed at the budget boundary");
        }
        Require(receipt.IncludedMessageIds.Contains("message-12", StringComparer.Ordinal),
            "latest Operator direction must remain mandatory");
    }

    public static void FrozenReceiptExcludesFutureTurns()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 5; turn++)
        {
            snapshot.Engine.Messages.Add(Message(turn, turn == 1 ? "operator" : "beta", turn == 1 ? "Operator" : "Beta", $"causal-{turn}"));
        }
        snapshot.Engine.TurnCount = 5;
        var config = Config("rolling-model", 16_384, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var original = ArenaHistoryBudgetService.Build(
            snapshot, config, 6, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, 6, includedTranscriptMessageIds: ids));
        Require(original.Receipt is not null, "original request must emit a receipt");
        var originalReceipt = original.Receipt!;

        snapshot.Engine.Messages.Add(Message(6, "gamma", "Gamma", "future-six"));
        snapshot.Engine.Messages.Add(Message(7, "operator", "Operator", "future-seven"));
        snapshot.Engine.TurnCount = 7;
        var retry = ArenaHistoryBudgetService.Build(
            snapshot, config, 6, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, 6, includedTranscriptMessageIds: ids),
            originalReceipt);
        Require(retry.Ok && retry.Receipt is not null, "unchanged causal history must permit a frozen retry");
        var retryReceipt = retry.Receipt!;
        Require(retryReceipt.ContextFingerprint == originalReceipt.ContextFingerprint
            && retryReceipt.IncludedMessageIds.SequenceEqual(originalReceipt.IncludedMessageIds),
            "retry must retain the original entry selection and fingerprint");
        var prompt = string.Join("\n", retry.Messages.Select(message => message.Content));
        Require(!prompt.Contains("future-six", StringComparison.Ordinal)
            && !prompt.Contains("future-seven", StringComparison.Ordinal),
            "future turns must never leak into a causal retry");
    }

    public static void FactorySuppressesToneAndRollingBudget()
    {
        var config = Config("factory-model", 4_096, ModelHistoryPolicies.Rolling80, 128, ModelResponseTones.Custom, "sound like a pirate");
        var source = new[] { new ModelChatMessage("user", "  exact factory root\r\n") };
        var toned = ModelResponseToneInstructions.Apply(config, source, factoryMode: true);
        Require(ReferenceEquals(source, toned) && toned.Count == 1 && toned[0].Content == "  exact factory root\r\n",
            "Factory public_group_v1 payloads must receive zero tone bytes");

        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.FactoryMode = true;
        var built = ArenaHistoryBudgetService.Build(snapshot, config, null, null, _ => source);
        Require(built.Receipt is null && built.Messages[0].Content == source[0].Content,
            "Factory must bypass Arena rolling receipts and keep exact content");
    }

    public static void ClassifiesProviderOutcomes()
    {
        var context = ModelCompletionOutcomeClassifier.Normalize(new ModelCompletionResult(
            false, "http://local", "model", "", "", 1, 0, 0, 0,
            "This request exceeds the maximum context length", DateTimeOffset.Now,
            ProviderStatusCode: 400, ProviderErrorCode: "context_length_exceeded"));
        Require(context.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && context.StopReason == ModelCompletionStopReason.ProviderError,
            "provider-specific context failures must normalize to the input-context outcome");
        Require(ModelCompletionOutcomeClassifier.ClassifyStopReason("length") == ModelCompletionStopReason.OutputLimitReached
            && ModelCompletionOutcomeClassifier.ClassifyStopReason("max_tokens") == ModelCompletionStopReason.OutputLimitReached,
            "provider output ceilings must normalize independently from request failures");
        using var response = JsonDocument.Parse("{\"choices\":[{\"finish_reason\":\"length\"}]}");
        Require(ModelCompletionOutcomeClassifier.ExtractStopReason(response.RootElement) == ModelCompletionStopReason.OutputLimitReached,
            "nested OpenAI-compatible finish reasons must be recognized");
        Require(ModelCompletionOutcomeClassifier.ClassifyFailure("previous_response_id expired because conversation state was not found")
                == ModelCompletionFailureKind.NativeStateExhausted
            && ModelCompletionOutcomeClassifier.ClassifyFailure("model is loading")
                == ModelCompletionFailureKind.ProviderLoading
            && ModelCompletionOutcomeClassifier.ClassifyFailure("Provider returned no public content after retry")
                == ModelCompletionFailureKind.EmptyPublicContent,
            "native-state, loading, and empty-public-content outcomes must remain distinct");

        const string providerToken = "sk-provider-secret-0123456789";
        Require(ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode("context_length_exceeded")
                == "context_length_exceeded"
            && ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode(providerToken).Length == 0
            && ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode("C:/private/model.gguf").Length == 0,
            "durable provider error codes must admit code-shaped evidence while rejecting credentials and paths");
        var hostileCode = ModelProviderClient.ExtractProviderErrorCode(
            "{\"error\":{\"code\":\"" + providerToken + "\"}}",
            providerToken);
        Require(hostileCode.Length == 0,
            "provider bodies must never copy the active credential into durable outcome metadata");
    }

    public static void ProviderAdaptersExposeTypedContextAndOutputLimits()
    {
        var adapters = new[]
        {
            new AdapterCase(
                "OpenAI-compatible",
                ModelProviderApiModes.OpenAiCompatible,
                "http://127.0.0.1:1234/v1",
                """{"model":"typed-model","choices":[{"finish_reason":"length","message":{"role":"assistant","content":"partial openai"}}]}"""),
            new AdapterCase(
                "llama.cpp",
                ModelProviderApiModes.LlamaCppNative,
                "http://127.0.0.1:8080/v1",
                """{"model":"typed-model","choices":[{"finish_reason":"max_tokens","message":{"role":"assistant","content":"partial llama"}}]}"""),
            new AdapterCase(
                "LM Studio native",
                ModelProviderApiModes.LmStudioNative,
                "http://127.0.0.1:1234/v1",
                """{"model_instance_id":"typed-model","finish_reason":"max_output_tokens","output":[{"type":"message","content":"partial lm studio"}]}"""),
            new AdapterCase(
                "Ollama native",
                ModelProviderApiModes.OllamaNative,
                "http://127.0.0.1:11434/v1",
                """{"model":"typed-model","done":true,"done_reason":"length","message":{"role":"assistant","content":"partial ollama"}}""")
        };

        foreach (var adapter in adapters)
        {
            const string errorBody = """{"error":{"code":"context_length_exceeded","message":"maximum context length exceeded"}}""";
            var rejectedHandler = new CaptureHandler(errorBody, HttpStatusCode.BadRequest);
            var rejected = new ModelProviderClient(new HttpClient(rejectedHandler)).CompleteChatAsync(
                AdapterConfig(adapter),
                [new ModelChatMessage("user", "oversized input")]).GetAwaiter().GetResult();
            Require(!rejected.Ok
                && rejected.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
                && rejected.StopReason == ModelCompletionStopReason.ProviderError
                && rejected.ProviderStatusCode == 400
                && rejected.ProviderErrorCode == "context_length_exceeded"
                && rejectedHandler.Calls == 1,
                $"{adapter.Name} must expose one typed input-context rejection with provider status/code evidence");

            var limitedHandler = new CaptureHandler(adapter.LimitedResponse);
            var limited = new ModelProviderClient(new HttpClient(limitedHandler)).CompleteChatAsync(
                AdapterConfig(adapter),
                [new ModelChatMessage("user", "bounded input")]).GetAwaiter().GetResult();
            Require(limited.Ok
                && limited.Text.StartsWith("partial", StringComparison.Ordinal)
                && limited.FailureKind == ModelCompletionFailureKind.None
                && limited.StopReason == ModelCompletionStopReason.OutputLimitReached
                && limitedHandler.Calls == 1,
                $"{adapter.Name} must preserve partial content while typing its provider stop reason as output_limit_reached");
        }
    }

    public static void OversizedMandatoryPromptFailsPreflight()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", new string('x', 20_000)));
        snapshot.Engine.TurnCount = 1;
        var config = Config("tiny", 512, ModelHistoryPolicies.Rolling80, maxOutputTokens: 64);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot, config, null, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));
        Require(!built.Ok
            && built.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && built.Receipt is { IncludedEntryCount: 1, OmittedEntryCount: 0 },
            "mandatory latest-Operator content must fail preflight rather than be trimmed or sent over budget");
    }

    public static void PreservesLatestOperatorAndNewestDialogue()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 8; turn++)
        {
            snapshot.Engine.Messages.Add(Message(turn, "gamma", "Gamma", $"OLD_{turn} {new string('z', 3_000)}"));
        }

        snapshot.Engine.Messages.Add(Message(9, "operator", "Operator", $"LATEST_OPERATOR {new string('o', 3_000)}"));
        snapshot.Engine.Messages.Add(Message(10, "beta", "Beta", $"NEWEST_DIALOGUE {new string('d', 3_000)}"));
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "internet-11",
            Turn = 11,
            SpeakerId = "internet",
            Speaker = "Internet",
            Text = $"NEWER_TOOL_ROW {new string('t', 3_000)}",
            Status = "ok",
            Kind = "internet",
            CreatedAt = 11
        });
        snapshot.Engine.TurnCount = 11;
        var config = Config("rolling", 8_192, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot, config, null, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));
        Require(built.Ok && built.Receipt is not null && built.Receipt.OmittedEntryCount > 0,
            "fixture must force whole-entry rolling omission without exhausting mandatory content");
        Require(built.Receipt!.IncludedMessageIds.Contains("message-9", StringComparer.Ordinal)
            && built.Receipt.IncludedMessageIds.Contains("message-10", StringComparer.Ordinal),
            "Rolling 80 must retain both the latest Operator direction and newest public dialogue");
        var prompt = string.Join("\n", built.Messages.Select(message => message.Content));
        Require(prompt.Contains("LATEST_OPERATOR", StringComparison.Ordinal)
            && prompt.Contains("NEWEST_DIALOGUE", StringComparison.Ordinal),
            "mandatory Operator and newest-dialogue text must reach the prompt intact");

        var oversized = SessionStore.CreateDefaultSnapshot();
        oversized.Engine.Messages.Clear();
        oversized.Engine.Messages.Add(Message(1, "gamma", "Gamma", $"REMOVABLE {new string('r', 4_000)}"));
        oversized.Engine.Messages.Add(Message(2, "operator", "Operator", $"MANDATORY_OPERATOR {new string('o', 8_000)}"));
        oversized.Engine.Messages.Add(Message(3, "beta", "Beta", $"MANDATORY_DIALOGUE {new string('d', 8_000)}"));
        oversized.Engine.TurnCount = 3;
        var oversizedPlan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var failed = ArenaHistoryBudgetService.Build(
            oversized, config, null, null,
            ids => TurnRunnerService.BuildPrompt(oversized, oversizedPlan, includedTranscriptMessageIds: ids));
        Require(!failed.Ok
            && failed.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && failed.Receipt is { IncludedEntryCount: 2, OmittedEntryCount: 1 }
            && failed.Receipt.IncludedMessageIds.SequenceEqual(["message-2", "message-3"]),
            "combined oversized mandatory Operator/dialogue material must fail preflight without trimming either row");
    }

    public static void ContextFailureDoesNotCallFallback()
    {
        WithTempStore((store, snapshot) =>
        {
            snapshot.Configs["shared"] = Config("fallback", historyPolicy: ModelHistoryPolicies.Strict);
            snapshot.Configs["alpha"] = Config("primary", historyPolicy: ModelHistoryPolicies.Strict, explicitlyAssigned: true);
            ModelRuntimeSettingsRegistry.Register(snapshot, snapshot.Configs["shared"], 0, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            ModelRuntimeSettingsRegistry.Register(snapshot, snapshot.Configs["alpha"], 0, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "answer this"));
            snapshot.Engine.TurnCount = 1;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                false, "http://local", "primary", "", "", 1, 0, 0, 0,
                "maximum context length exceeded", DateTimeOffset.Now,
                FailureKind: ModelCompletionFailureKind.ContextLimitExceeded,
                StopReason: ModelCompletionStopReason.ProviderError));
            var result = new TurnRunnerService(provider, store).RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(result.Executed && result.Completion is { Ok: false } && provider.Calls == 1,
                "confirmed input-context exhaustion must not resend the same oversized prompt to fallback");
            Require(result.Message?.Metadata.TryGetValue("completion_failure_kind", out var kind) == true
                && kind.GetString() == "context_limit_exceeded",
                "context failure must persist a structured agent-owned recovery outcome");
        });
    }

    public static void RollingLmStudioReconstructsVisibleHistory()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                Model = "rolling-native",
                NativeStatefulChat = true,
                ConfiguredContextWindow = 16_384,
                ContextLength = 16_384,
                HistoryPolicy = ModelHistoryPolicies.Rolling80,
                MaxOutputTokens = 256
            };
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "visible operator root"));
            var prior = Message(2, "alpha", "Alpha", "prior alpha visible row");
            prior.Metadata["provider_response_id"] = JsonSerializer.SerializeToElement("opaque-native-response-id");
            snapshot.Engine.Messages.Add(prior);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "new response", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var result = new TurnRunnerService(provider, store).RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(result.Executed && provider.Calls == 1, "rolling LM Studio turn should execute exactly one reconstructed request");
            Require(result.Message is not null
                && CompletionRouteReceipt.TryRead(result.Message, out var route)
                && route.RoutePhase == CompletionRouteReceipt.PrimaryPhase,
                "ordinary Arena responses must persist a privacy-safe producing-route receipt for causal recovery");
            Require(provider.Configs[0].NativeStatefulChat == false
                && string.IsNullOrWhiteSpace(provider.Configs[0].PreviousResponseId),
                "rolling_80 must disable opaque LM Studio continuation state for the request");
            var prompt = string.Join("\n", provider.Requests[0].Select(message => message.Content));
            Require(prompt.Contains("visible operator root", StringComparison.Ordinal)
                && prompt.Contains("prior alpha visible row", StringComparison.Ordinal),
                "rolling LM Studio calls must reconstruct selected visible transcript rows instead of trusting opaque provider history");
        });
    }

    public static void ContextFailureGatesUntilRetryAndAdvancesOnce()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("gated", 8_192, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 8_192, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "answer"));
            snapshot.Engine.TurnCount = 1;
            snapshot.Engine.TurnIndex = 0;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var invocation = 0;
            var provider = new ScriptedProvider(_ => ++invocation == 1
                ? new ModelCompletionResult(false, config.BaseUrl, config.Model, "", "", 1, 0, 0, 0,
                    "maximum context length exceeded", DateTimeOffset.Now,
                    FailureKind: ModelCompletionFailureKind.ContextLimitExceeded,
                    StopReason: ModelCompletionStopReason.ProviderError)
                : new ModelCompletionResult(true, config.BaseUrl, config.Model, "recovered", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var runner = new TurnRunnerService(provider, store);
            var failed = runner.RunOneTurnAsync().GetAwaiter().GetResult();
            var blockedSnapshot = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(failed.Executed
                && blockedSnapshot.Engine.TurnIndex == 0
                && TurnRunnerService.UnresolvedContextFailure(blockedSnapshot) is not null,
                "an input-context failure must keep the failed speaker current and remain explicitly unresolved");
            Require(!runner.PlanOneTurn(blockedSnapshot).Ok
                && !runner.PlanAgentTurn(blockedSnapshot, "beta").Ok
                && !runner.RunAgentTurnAsync("default", "beta").GetAwaiter().GetResult().Executed
                && provider.Calls == 1,
                "automatic and manual planning must remain gated without another provider call");

            var original = blockedSnapshot.Engine.Messages.Single(message => message.Turn == 2);
            var retried = runner.RetryTurnAsync("default", original.Turn, original.SpeakerId, original.CreatedAt).GetAwaiter().GetResult();
            var recovered = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(retried.Completion?.Ok == true
                && provider.Calls == 2
                && recovered.Engine.TurnIndex == 1
                && TurnRunnerService.UnresolvedContextFailure(recovered) is null,
                "a successful causal retry must resolve the gate and advance past the failed speaker exactly once");
        });
    }

    public static void RecoverySkipsContinuesAndEndsCausally()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write the result"));
            var partial = Message(2, "alpha", "Alpha", "The first half");
            partial.Metadata["completion_failure_kind"] = JsonSerializer.SerializeToElement("none");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            var alpha = snapshot.Engine.Agents.First(agent => agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase));
            StructuredMemoryService.AddTurnMemory(
                snapshot,
                alpha,
                partial,
                "Turn 2: The first half",
                DateTimeOffset.FromUnixTimeSeconds(2));
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, "http://local", "recovery", " and the second half.", "", 1, 10, 5, 15, "", DateTimeOffset.Now,
                StopReason: ModelCompletionStopReason.Completed));
            var recovery = new ContextRecoveryService(store, provider);
            var continued = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(continued.Ok && provider.Calls == 1
                && continued.Message?.Text == "The first half and the second half.",
                "Continue must append one causal continuation instead of restarting or retrying the response");
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Engine.Messages.Count == 2
                && loaded.Engine.Messages[1].Metadata["output_continuation_count"].GetInt32() == 1,
                "continuation must replace the partial response atomically and exactly once");
            var continuedMemory = StructuredMemoryService.SelectForPrompt(
                loaded,
                loaded.Engine.Agents.First(agent => agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase)),
                DateTimeOffset.UtcNow);
            Require(continuedMemory.Any(entry => entry.Text.Contains("The first half and the second half.", StringComparison.Ordinal)),
                "continuation must correct the agent's structured private memory to match the replacement response");

            var blocked = Message(3, "beta", "Beta", "Model call failed: maximum context length exceeded");
            blocked = new DialogueMessage
            {
                MessageId = blocked.MessageId,
                Turn = blocked.Turn,
                SpeakerId = blocked.SpeakerId,
                Speaker = blocked.Speaker,
                Text = blocked.Text,
                Status = "error",
                Kind = blocked.Kind,
                CreatedAt = blocked.CreatedAt,
                Metadata = new Dictionary<string, JsonElement>
                {
                    ["completion_failure_kind"] = JsonSerializer.SerializeToElement("context_limit_exceeded"),
                    ["completion_stop_reason"] = JsonSerializer.SerializeToElement("provider_error")
                }
            };
            loaded.Engine.Messages.Add(blocked);
            loaded.Engine.TurnCount = 3;
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var skipped = recovery.SkipBlockedTurnAsync("default", 3, "beta", 3).GetAwaiter().GetResult();
            Require(skipped.Ok && skipped.Message?.Metadata[ContextRecoveryService.RecoveryDispositionMetadataKey].GetString() == "skipped",
                "Skip must mark only a confirmed causal context failure and advance once");
            Require(!recovery.SkipBlockedTurnAsync("default", 3, "beta", 3).GetAwaiter().GetResult().Ok,
                "a blocked turn must not be skipped twice");

            var ended = recovery.EndMatchAsync("default", "operator chose to end").GetAwaiter().GetResult();
            var endedSnapshot = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(ended.Ok && endedSnapshot.Engine.MatchEnded
                && endedSnapshot.Engine.MatchEndReason == "operator chose to end"
                && !new TurnRunnerService(provider, store).PlanOneTurn(endedSnapshot).Ok,
                "End match must durably block later turns until a reset clears the marker");
            Require(!recovery.EndMatchAsync("default", "duplicate end").GetAwaiter().GetResult().Ok
                && store.LoadSnapshotAsync().GetAwaiter().GetResult()!.Engine.MatchEndReason == "operator chose to end",
                "the durable end-match marker must be first-writer-wins and must not be overwritten by duplicate actions");
        });
    }

    public static void ContinuationBindsRouteRejectsDescendantsAndPreservesBytes()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("route-a", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "Prefix \n");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "\t suffix \r\n", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var recovery = new ContextRecoveryService(store, provider);
            var continued = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(continued.Ok && continued.Message?.Text == "Prefix \n\t suffix \r\n",
                "continuation must concatenate provider text byte-for-byte without trim, inferred spaces, or newline normalization");

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            loaded.Engine.Messages[1].Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            loaded.Engine.Messages.Add(Message(3, "operator", "Operator", "later public direction"));
            loaded.Engine.TurnCount = 3;
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var calls = provider.Calls;
            var descendantRejected = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!descendantRejected.Ok && provider.Calls == calls,
                "continuation must reject a partial response once later successful public dialogue depends on it");

            loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            loaded.Engine.Messages.RemoveAll(message => message.Turn == 3);
            loaded.Engine.TurnCount = 2;
            loaded.Configs["shared"] = Config("route-b", 16_384, ModelHistoryPolicies.Strict, 256);
            ModelRuntimeSettingsRegistry.Register(loaded, loaded.Configs["shared"], 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var routeRejected = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!routeRejected.Ok && routeRejected.Error.Contains("route", StringComparison.OrdinalIgnoreCase)
                && provider.Calls == calls,
                "continuation must bind to the producing provider/model route and fail closed after routing changes");
        });
    }

    public static void DuplicateContinuationUsesOneProviderCall()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("single-flight", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new BlockingProvider(config);
            var recovery = new ContextRecoveryService(store, provider);
            var first = recovery.ContinueOutputAsync("default", 2, "alpha", 2);
            Require(provider.Entered.Wait(TimeSpan.FromSeconds(5)), "first continuation did not reach the provider");
            var duplicate = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!duplicate.Ok && provider.Calls == 1,
                "a duplicate/in-progress Continue action must fail before issuing a second provider request");
            provider.Release.TrySetResult(true);
            Require(first.GetAwaiter().GetResult().Ok && provider.Calls == 1,
                "the winning continuation should commit exactly once after the duplicate is rejected");
        });
    }

    public static void NarratorAndDecisionCardUseRollingBudget()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("narrator-rolling", 8_192, ModelHistoryPolicies.Rolling80, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 8_192, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.TranscriptWindow = 1;
            snapshot.Engine.Messages.Clear();
            for (var turn = 1; turn <= 8; turn++)
            {
                snapshot.Engine.Messages.Add(Message(turn, "beta", "Beta", $"NARRATOR_OLD_{turn} {new string('n', 4_000)}"));
            }
            snapshot.Engine.Messages.Add(Message(9, "operator", "Operator", "NARRATOR_LATEST_OPERATOR"));
            snapshot.Engine.Messages.Add(Message(10, "alpha", "Alpha", "NARRATOR_NEWEST_DIALOGUE"));
            snapshot.Engine.TurnCount = 10;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(config => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "bounded narrator output", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            using var narrator = new NarratorService(provider, store);
            var narrated = narrator.NarrateNowAsync("default").GetAwaiter().GetResult();
            var narratorReceipt = new ArenaHistoryBudgetReceipt();
            var hasReceipt = narrated.Message is not null
                && ArenaHistoryBudgetService.TryReadReceipt(narrated.Message, out narratorReceipt);
            Require(narrated.Ok && provider.Calls == 1
                && hasReceipt
                && narratorReceipt.OmittedEntryCount > 0,
                $"ordinary narrator calls must use the routed model's rolling whole-entry budget and persist its receipt (ok={narrated.Ok}, calls={provider.Calls}, receipt={hasReceipt}, omitted={(hasReceipt ? narratorReceipt.OmittedEntryCount : -1)}, error={narrated.Error})");
            var prompt = string.Join("\n", provider.Requests[0].Select(message => message.Content));
            Require(prompt.Contains("NARRATOR_LATEST_OPERATOR", StringComparison.Ordinal)
                && prompt.Contains("NARRATOR_NEWEST_DIALOGUE", StringComparison.Ordinal)
                && !prompt.Contains("NARRATOR_OLD_1", StringComparison.Ordinal),
                "narrator rolling history must ignore TranscriptWindow, retain mandatory newest rows, and omit old rows whole");

            var decision = narrator.GenerateDecisionCardAsync("default").GetAwaiter().GetResult();
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(decision.Ok && provider.Calls == 2
                && loaded.Engine.DecisionCard.Metadata.ContainsKey(ArenaHistoryBudgetReceipt.MetadataKey),
                "Decision Card calls must use and persist the same per-model rolling budget evidence");
        });
    }

    public static void NarratorOversizedMandatoryFailsPreflight()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("narrator-tiny", 512, ModelHistoryPolicies.Rolling80, 64);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 512, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", $"MANDATORY_NARRATOR_OPERATOR {new string('o', 6_000)}"));
            snapshot.Engine.Messages.Add(Message(2, "alpha", "Alpha", $"MANDATORY_NARRATOR_DIALOGUE {new string('a', 6_000)}"));
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => throw new InvalidOperationException("provider must not be called"));
            using var narrator = new NarratorService(provider, store);
            var result = narrator.NarrateNowAsync("default").GetAwaiter().GetResult();
            Require(!result.Ok && provider.Calls == 0
                && result.Message?.Metadata.TryGetValue("completion_failure_kind", out var failure) == true
                && failure.GetString() == "context_limit_exceeded"
                && ArenaHistoryBudgetService.TryReadReceipt(result.Message, out _),
                "oversized mandatory narrator input must fail with typed preflight evidence before any provider call");
        });
    }

    public static void CancelledContinuationClearsBusyMarker()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var recovery = new ContextRecoveryService(store, new ScriptedProvider(_ => throw new OperationCanceledException("cancelled")));
            try
            {
                recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
                throw new InvalidOperationException("expected continuation cancellation");
            }
            catch (OperationCanceledException)
            {
            }

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var restored = loaded.Engine.Messages.Single(message => message.Turn == 2);
            Require(!restored.Metadata.ContainsKey("output_continuation_attempt")
                && restored.Metadata["output_continuation_state"].GetString() == "failed",
                "cancelled continuation must not strand the durable response in a busy state");
        });
    }

    public static void CancellationBeforeProviderClearsBusyMarker()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var eventStore = new EventLogStore(store.DataRoot);
            using var blockedEventLease = CrossProcessWriteLease.AcquireAsync(
                eventStore.EventPath("default"),
                TimeSpan.FromSeconds(5),
                CancellationToken.None).GetAwaiter().GetResult();
            var provider = new ScriptedProvider(_ => throw new InvalidOperationException("provider must not be reached"));
            var recovery = new ContextRecoveryService(store, provider, eventLogStore: eventStore);
            using var cancellation = new CancellationTokenSource();
            var continuation = recovery.ContinueOutputAsync("default", 2, "alpha", 2, cancellation.Token);
            Require(SpinWait.SpinUntil(
                () =>
                {
                    var current = store.LoadSnapshotAsync().GetAwaiter().GetResult();
                    var source = current?.Engine.Messages.SingleOrDefault(message => message.Turn == 2);
                    return source is not null
                        && source.Metadata.ContainsKey("output_continuation_attempt")
                        && source.Metadata.TryGetValue("output_continuation_state", out var state)
                        && state.GetString() == "in_progress";
                },
                TimeSpan.FromSeconds(5)),
                "continuation did not persist its causal in-progress marker before event logging");
            cancellation.Cancel();
            try
            {
                continuation.GetAwaiter().GetResult();
                throw new InvalidOperationException("expected pre-provider continuation cancellation");
            }
            catch (OperationCanceledException)
            {
            }

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var restored = loaded.Engine.Messages.Single(message => message.Turn == 2);
            Require(provider.Calls == 0
                && !restored.Metadata.ContainsKey("output_continuation_attempt")
                && restored.Metadata["output_continuation_state"].GetString() == "failed",
                "cancellation during started-event logging must clear the durable busy marker before any provider call");
        });
    }

    private static ModelProviderConfig Config(
        string model,
        int context = 0,
        string historyPolicy = ModelHistoryPolicies.Strict,
        int maxOutputTokens = 256,
        string responseTone = ModelResponseTones.Default,
        string customTone = "",
        bool explicitlyAssigned = false) => new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        Model = model,
        ExplicitModelAssignment = explicitlyAssigned,
        ContextLength = context,
        ConfiguredContextWindow = context,
        HistoryPolicy = historyPolicy,
        MaxOutputTokens = maxOutputTokens,
        ResponseTone = responseTone,
        CustomTone = customTone
    };

    private static ModelProviderConfig AdapterConfig(AdapterCase adapter) => new()
    {
        BaseUrl = adapter.BaseUrl,
        ApiMode = adapter.ApiMode,
        Model = "typed-model",
        Timeout = 5,
        Temperature = 0.2,
        MaxOutputTokens = 64,
        ConfiguredContextWindow = 8_192,
        HistoryPolicy = ModelHistoryPolicies.Strict
    };

    private static DialogueMessage Message(int turn, string speakerId, string speaker, string text) => new()
    {
        MessageId = $"message-{turn}",
        Turn = turn,
        SpeakerId = speakerId,
        Speaker = speaker,
        Text = text,
        Status = "ok",
        Kind = "message",
        CreatedAt = turn
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WithTempStore(Action<SessionStore, ArenaSnapshot> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(new SessionStore(root), SessionStore.CreateDefaultSnapshot());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedProvider(Func<ModelProviderConfig, ModelCompletionResult> complete) : IModelProviderClient
    {
        public int Calls { get; private set; }
        public List<ModelProviderConfig> Configs { get; } = new();
        public List<IReadOnlyList<ModelChatMessage>> Requests { get; } = new();

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Configs.Add(config);
            Requests.Add(messages.ToArray());
            return Task.FromResult(complete(config));
        }
    }

    private sealed class BlockingProvider(ModelProviderConfig sourceConfig) : IModelProviderClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ManualResetEventSlim Entered { get; } = new(false);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            await Release.Task.WaitAsync(cancellationToken);
            return new ModelCompletionResult(
                true,
                sourceConfig.BaseUrl,
                sourceConfig.Model,
                " continued",
                "",
                1,
                1,
                1,
                2,
                "",
                DateTimeOffset.Now);
        }
    }

    private sealed record AdapterCase(string Name, string ApiMode, string BaseUrl, string LimitedResponse);
}
