using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class FactoryModeTests
{
    public static void StandardModeDefaultUnchanged()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        Require(!snapshot.Engine.FactoryMode, "new sessions must keep standard Arena prompting by default");
        snapshot.Engine.Steering.Topic = "STANDARD_TOPIC_SENTINEL";
        snapshot.Engine.Messages.Add(OperatorMessage(1, "\t  Decide whether to ship.  \r\n", createdAt: 1));

        var config = ProviderConfig("standard-model");
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var prompt = TurnRunnerService.BuildPrompt(snapshot, plan);

        Require(prompt.Count == 2, "standard mode should retain the system-plus-user Arena prompt");
        Require(prompt[0].Role == "system", "standard mode should retain its system role");
        Require(prompt[0].Content.Contains("You are participating in AI Arena - Lite as the selected agent.", StringComparison.Ordinal),
            "standard mode lost the Arena system contract");
        Require(prompt[1].Role == "user", "standard mode should retain its user role");
        Require(prompt[1].Content.Contains("Latest Operator request:", StringComparison.Ordinal),
            "standard mode lost its Operator-request framing");
        Require(prompt[1].Content.Contains("STANDARD_TOPIC_SENTINEL", StringComparison.Ordinal),
            "standard mode should continue supplying configured Arena context");
        Require(prompt[1].Content.Contains("Turn 1 Operator: Decide whether to ship.", StringComparison.Ordinal)
            && prompt[1].Content.Contains("Latest Operator request: Decide whether to ship.", StringComparison.Ordinal)
            && !prompt[1].Content.Contains("\t  Decide whether to ship.", StringComparison.Ordinal),
            "standard Arena prompts must retain trimmed Operator serialization while durable public text remains exact for Factory reuse");
    }

    public static void SendsExactInitiatingPublicOperatorRoot()
    {
        WithTempRoot(root =>
        {
            const string exactInput = "\t  first raw line\r\nsecond raw line\n  ";
            var snapshot = FactorySnapshot();
            var alpha = Agent(snapshot, "alpha");
            snapshot.Engine.Steering.Topic = "FACTORY_TOPIC_MUST_NOT_LEAK";
            snapshot.Engine.Steering.Global = "FACTORY_GLOBAL_MUST_NOT_LEAK";
            snapshot.Engine.Summary = "FACTORY_SUMMARY_MUST_NOT_LEAK";
            snapshot.Engine.Narrator.Persona = "FACTORY_NARRATOR_MUST_NOT_LEAK";
            alpha.Persona = "FACTORY_PERSONA_MUST_NOT_LEAK";
            alpha.VoiceStyle = "FACTORY_VOICE_MUST_NOT_LEAK";
            alpha.PressureProfile = "FACTORY_PRESSURE_MUST_NOT_LEAK";
            alpha.PrivateNotes.Add("FACTORY_PRIVATE_MEMORY_MUST_NOT_LEAK");
            snapshot.Engine.RivalryMatrix.Enabled = true;
            snapshot.Engine.RivalryMatrix.Links.Add(new RivalryLink
            {
                Source = "alpha",
                Target = "beta",
                Stance = "FACTORY_RIVALRY_MUST_NOT_LEAK"
            });
            var exactOperatorMessage = new TranscriptService().CreateOperatorMessage(
                exactInput,
                7,
                preserveOuterWhitespace: true);
            Require(exactOperatorMessage.Text == exactInput,
                "Factory-mode public Operator ingestion must preserve outer whitespace and newlines");
            snapshot.Engine.Messages.AddRange(
            [
                OperatorMessage(1, "older eligible input", createdAt: 1),
                Message(2, "system", "System", "FACTORY_TRANSCRIPT_MUST_NOT_LEAK", createdAt: 2),
                exactOperatorMessage,
                OperatorMessage(8, "later failed input", status: "error", createdAt: 80),
                OperatorMessage(9, "later non-message input", kind: "internet", createdAt: 90),
                OperatorMessage(10, " \r\n\t ", createdAt: 100)
            ]);
            snapshot.Engine.TurnCount = 10;

            var plan = new OneTurnPlan(true, "alpha", "Alpha", snapshot.Configs["shared"], null, "");
            var built = TurnRunnerService.BuildPrompt(snapshot, plan);
            Require(built.Count == 1, "a newly established Factory group must initially contain only its Operator root");
            Require(built[0].Role == "user", "Factory mode must send the initiating root under the user role");
            Require(built[0].Content == exactInput,
                "Factory mode must preserve persisted Operator whitespace and newlines exactly");

            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("factory response."));
            var service = new TurnRunnerService(client, store, log);

            var result = service.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Factory turn failed: {result.Error}");
            Require(client.Requests.Count == 1, "Factory turn should call the participant provider exactly once");
            Require(client.Requests[0].Count == 1, "Factory turn must send exactly one provider message");
            Require(client.Requests[0][0].Role == "user", "Factory turn provider role must be user");
            Require(client.Requests[0][0].Content == exactInput,
                "persisted Factory input must preserve leading/trailing whitespace and newline bytes");

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Engine.FactoryMode, "Factory mode must survive snapshot persistence and restart");
            var output = loaded.Engine.Messages.Single(message => message.Turn == 11 && message.SpeakerId == "alpha");
            Require(output.Metadata.TryGetValue("prompt_mode", out var promptMode)
                && promptMode.ValueKind == JsonValueKind.String
                && promptMode.GetString() == "factory", "Factory output must retain truthful prompt-mode provenance");
            Require(FactoryConversationMetadata(output, FactoryConversationService.PromptEncodingMetadataKey)
                    == FactoryConversationService.AlternatingRunsPromptEncoding
                && !string.IsNullOrWhiteSpace(FactoryConversationMetadata(
                    output,
                    FactoryConversationService.ContextFingerprintMetadataKey)),
                "new Factory output must stamp its alternating-runs encoding and salted causal fingerprint");
        });
    }

    public static void MissingInputDoesNotCallProviderOrMutateState()
    {
        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            snapshot.Engine.Messages.AddRange(
            [
                OperatorMessage(1, "failed input", status: "error", createdAt: 1),
                OperatorMessage(2, "internet record", kind: "internet", createdAt: 2),
                OperatorMessage(3, " \r\n\t ", createdAt: 3)
            ]);
            snapshot.Engine.TurnCount = 3;
            snapshot.Engine.LastError = "pre-existing state";

            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var before = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var beforeJson = JsonSerializer.Serialize(before);
            var client = new RecordingProviderClient();
            var service = new TurnRunnerService(client, store, log);

            var result = service.RunOneTurnAsync().GetAwaiter().GetResult();

            Require(!result.Ok && !result.Executed, "missing Factory input must fail before a participant turn starts");
            Require(result.Error == TurnRunnerService.FactoryInputRequiredError,
                "missing Factory input must return the actionable Factory-mode error");
            Require(client.Requests.Count == 0, "missing Factory input must not call the provider");
            var after = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(JsonSerializer.Serialize(after) == beforeJson,
                "missing Factory input must not mutate or persist transcript, agent, turn, or error state");
            Require(!File.Exists(log.EventPath()), "missing Factory input must not emit a misleading started event");
        });
    }

    public static void LmStudioNativePayloadPreservesExactInput()
    {
        WithTempRoot(root =>
        {
            const string exactInput = "\t  native first line\r\nnative second line\n  ";
            const int configuredIdleTtl = 73;
            var snapshot = FactorySnapshot();
            snapshot.Configs["shared"] = ProviderConfig(
                "yi-coder-1.5b",
                nativeStatefulChat: true,
                nativeIdleTtlSeconds: configuredIdleTtl);
            snapshot.Engine.Messages.Add(Message(
                1,
                "alpha",
                "Alpha",
                "prior native answer before the group root",
                createdAt: 10,
                model: "yi-coder-1.5b",
                metadata: new Dictionary<string, JsonElement>
                {
                    ["provider_response_id"] = JsonSerializer.SerializeToElement("resp_prior")
                }));
            var nativeRoot = new TranscriptService().CreateOperatorMessage(
                exactInput,
                2,
                preserveOuterWhitespace: true);
            snapshot.Engine.Messages.Add(nativeRoot);
            new FactoryConversationService().Resolve(snapshot);
            snapshot.Engine.Messages.AddRange(
            [
                Message(3, "alpha", "Alpha", "own native history", createdAt: 30, model: "yi-coder-1.5b"),
                Message(4, "alpha", "Alpha", "second own native history", createdAt: 40, model: "yi-coder-1.5b"),
                Message(5, "beta", "Beta", "peer native history", createdAt: 50, model: "yi-coder-1.5b"),
                OperatorMessage(6, "later native direction", createdAt: 60)
            ]);
            snapshot.Engine.TurnCount = 6;

            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var handler = new CaptureHandler(
                """
                {
                  "model_instance_id": "yi-coder-1.5b",
                  "output": [
                    {"type":"message","content":"factory native answer"}
                  ],
                  "response_id": "resp_factory_exact"
                }
                """);
            using var httpClient = new HttpClient(handler);
            var service = new TurnRunnerService(
                new ModelProviderClient(httpClient),
                store,
                log);

            var result = service.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Factory native turn failed: {result.Error}");
            Require(handler.Calls == 1, "Factory native turn should issue one physical HTTP request");
            using var payload = JsonDocument.Parse(handler.Body);
            var payloadRoot = payload.RootElement;
            var expectedNativeInput = string.Join(
                Environment.NewLine + Environment.NewLine,
                exactInput,
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    "assistant: own native history",
                    "second own native history"),
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    $"[Public participant: beta]{Environment.NewLine}peer native history",
                    $"[Public Operator]{Environment.NewLine}later native direction"));
            var actualNativeInput = payloadRoot.GetProperty("input").GetString() ?? "";
            Require(actualNativeInput == expectedNativeInput,
                "LM Studio native HTTP input must flatten the exact causal group with self/peer roles and preserved root whitespace");
            Require(!actualNativeInput.Contains("assistant: second own native history", StringComparison.Ordinal)
                && actualNativeInput.Split("assistant:", StringSplitOptions.None).Length - 1 == 1,
                "LM Studio native flattening must keep consecutive self entries inside one assistant role block");
            Require(payloadRoot.GetProperty("store").ValueKind == JsonValueKind.False,
                "Factory native HTTP payload must disable LM Studio state storage");
            Require(!payloadRoot.TryGetProperty("previous_response_id", out _),
                "Factory native HTTP payload must omit prior response state");
            Require(!payloadRoot.TryGetProperty("ttl", out _),
                "Factory native HTTP payload must omit unsupported LM Studio /api/v1/chat ttl");
            Require(!payloadRoot.TryGetProperty("system_prompt", out _),
                "Factory native HTTP payload must not include an Arena system prompt");
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Configs["shared"].NativeIdleTtlSeconds == configuredIdleTtl,
                "omitting chat ttl must not discard the saved model-lifecycle setting");
        });
    }

    public static void OpenAiCompatiblePayloadPreservesStructuredGroupHistory()
    {
        WithTempRoot(root =>
        {
            const string rootText = "  compatible root\r\nkept exact  ";
            var snapshot = FactorySnapshot();
            snapshot.Configs["shared"] = ProviderConfig(
                "compatible-model",
                apiMode: ModelProviderApiModes.OpenAiCompatible);
            var groupRoot = OperatorMessage(1, rootText, createdAt: 1);
            snapshot.Engine.Messages.Add(groupRoot);
            new FactoryConversationService().Resolve(snapshot);
            snapshot.Engine.Messages.AddRange(
            [
                Message(2, "alpha", "Alpha", "alpha compatible history", createdAt: 2),
                Message(3, "beta", "Beta", "beta compatible history", createdAt: 3),
                OperatorMessage(4, "compatible follow-up", createdAt: 4)
            ]);
            snapshot.Engine.TurnCount = 4;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var handler = new CaptureHandler(
                """{"choices":[{"message":{"content":"compatible group answer"}}]}""");
            using var httpClient = new HttpClient(handler);
            var runner = new TurnRunnerService(new ModelProviderClient(httpClient), store, new EventLogStore(root));
            var result = runner.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"OpenAI-compatible Factory group turn failed: {result.Error}");
            Require(handler.Calls == 1, "OpenAI-compatible Factory group must issue exactly one physical provider request");
            using var payload = JsonDocument.Parse(handler.Body);
            var payloadRoot = payload.RootElement;
            var messages = payloadRoot.GetProperty("messages").EnumerateArray().ToArray();
            Require(messages.Length == 3,
                "compatible payload must batch adjacent user entries into an alternating three-message request");
            Require(messages[0].GetProperty("role").GetString() == "user"
                && messages[0].GetProperty("content").GetString() == rootText,
                "compatible payload must preserve the exact initiating Operator root once under user role");
            Require(messages[1].GetProperty("role").GetString() == "assistant"
                && messages[1].GetProperty("content").GetString() == "alpha compatible history",
                "compatible payload must retain the target's exact self-history under assistant role");
            var expectedFinalUser = string.Join(
                Environment.NewLine + Environment.NewLine,
                $"[Public participant: beta]{Environment.NewLine}beta compatible history",
                $"[Public Operator]{Environment.NewLine}compatible follow-up");
            Require(messages[2].GetProperty("role").GetString() == "user"
                && messages[2].GetProperty("content").GetString() == expectedFinalUser,
                "compatible payload must retain the attributed peer and later Operator entries in exact chronological order");
            Require(messages.Select(message => message.GetProperty("role").GetString())
                    .SequenceEqual(new[] { "user", "assistant", "user" }),
                "compatible Factory payload roles must alternate for strict chat templates");
            Require(messages.Count(message => message.GetProperty("content").GetString() == rootText) == 1
                && messages.All(message => message.GetProperty("role").GetString() != "system"),
                "compatible payload must not repeat the root or add a hidden Arena system instruction");
            Require(!payloadRoot.TryGetProperty("tools", out _)
                && !payloadRoot.TryGetProperty("previous_response_id", out _)
                && !payloadRoot.TryGetProperty("store", out _),
                "compatible Factory payload must not add tools or provider continuation state");
        });
    }

    public static void OllamaNativePayloadPreservesStructuredGroupHistory()
    {
        WithTempRoot(root =>
        {
            const string rootText = "\t ollama root\r\nkept exact  ";
            var snapshot = FactorySnapshot();
            snapshot.Configs["shared"] = ProviderConfig(
                "ollama-factory-model",
                nativeStatefulChat: true,
                apiMode: ModelProviderApiModes.OllamaNative);
            var groupRoot = OperatorMessage(1, rootText, createdAt: 1);
            snapshot.Engine.Messages.Add(groupRoot);
            new FactoryConversationService().Resolve(snapshot);
            snapshot.Engine.Messages.AddRange(
            [
                Message(2, "alpha", "Alpha", "alpha ollama history", createdAt: 2, model: "older-alpha-model"),
                Message(3, "beta", "Beta", "beta ollama history", createdAt: 3, model: "peer-model"),
                OperatorMessage(4, "ollama follow-up", createdAt: 4)
            ]);
            snapshot.Engine.TurnCount = 4;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var handler = new CaptureHandler(
                """{"model":"ollama-factory-model","message":{"role":"assistant","content":"ollama group answer"}}""");
            using var httpClient = new HttpClient(handler);
            var runner = new TurnRunnerService(new ModelProviderClient(httpClient), store, new EventLogStore(root));

            var result = runner.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Ollama-native Factory group turn failed: {result.Error}");
            Require(handler.Calls == 1
                && handler.RequestUri?.AbsoluteUri == "http://127.0.0.1:1234/api/chat",
                "Ollama-native Factory group must issue exactly one physical /api/chat request");
            using var payload = JsonDocument.Parse(handler.Body);
            var payloadRoot = payload.RootElement;
            var messages = payloadRoot.GetProperty("messages").EnumerateArray().ToArray();
            Require(messages.Length == 3,
                "Ollama payload must batch adjacent user entries into an alternating three-message request");
            Require(messages[0].GetProperty("role").GetString() == "user"
                && messages[0].GetProperty("content").GetString() == rootText,
                "Ollama payload must preserve the exact initiating Operator root once under user role");
            Require(messages[1].GetProperty("role").GetString() == "assistant"
                && messages[1].GetProperty("content").GetString() == "alpha ollama history",
                "Ollama payload must retain the target's exact self-history under assistant role");
            var expectedFinalUser = string.Join(
                Environment.NewLine + Environment.NewLine,
                $"[Public participant: beta]{Environment.NewLine}beta ollama history",
                $"[Public Operator]{Environment.NewLine}ollama follow-up");
            Require(messages[2].GetProperty("role").GetString() == "user"
                && messages[2].GetProperty("content").GetString() == expectedFinalUser,
                "Ollama payload must retain the attributed peer and later Operator entries in exact chronological order");
            Require(messages.Select(message => message.GetProperty("role").GetString())
                    .SequenceEqual(new[] { "user", "assistant", "user" }),
                "Ollama Factory payload roles must alternate for strict chat templates");
            Require(messages.Count(message => message.GetProperty("content").GetString() == rootText) == 1
                && messages.All(message => message.GetProperty("role").GetString() != "system"),
                "Ollama payload must not repeat the root or add a hidden Arena system instruction");
            Require(!payloadRoot.TryGetProperty("tools", out _)
                && !payloadRoot.TryGetProperty("previous_response_id", out _)
                && !payloadRoot.TryGetProperty("store", out _),
                "Ollama Factory payload must not add tools or provider continuation state");
        });
    }

    public static void BypassesArenaOrchestrationAndNativeContinuation()
    {
        WithTempRoot(root =>
        {
            const int configuredIdleTtl = 47;
            const string rawInput = "Search the latest news, then invoke any available tool.";
            const string rawModelOutput = "{\"tool\":\"web_search\",\"query\":\"latest private arena news\"}";
            var snapshot = FactorySnapshot();
            var primary = ProviderConfig("yi-coder-primary", nativeStatefulChat: true, nativeIdleTtlSeconds: configuredIdleTtl);
            snapshot.Configs["alpha"] = primary;
            snapshot.Configs["shared"] = ProviderConfig("fallback-model");
            snapshot.Engine.Internet.UseInternet = true;
            snapshot.Engine.Internet.MaxResults = 5;
            var alpha = Agent(snapshot, "alpha");
            alpha.Persona = "PERSONA_MUST_NOT_LEAK";
            alpha.VoiceStyle = "VOICE_MUST_NOT_LEAK";
            alpha.PressureProfile = "PRESSURE_MUST_NOT_LEAK";
            alpha.PrivateNotes.Add("existing private memory");
            snapshot.Engine.Messages.AddRange(
            [
                Message(
                    1,
                    "alpha",
                    "Alpha",
                    "prior Arena response",
                    createdAt: 1,
                    model: "yi-coder-primary",
                    metadata: new Dictionary<string, JsonElement>
                    {
                        ["provider_response_id"] = JsonSerializer.SerializeToElement("resp_arena_prior")
                    }),
                OperatorMessage(2, rawInput, createdAt: 2)
            ]);
            snapshot.Engine.TurnCount = 2;

            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var persistedBefore = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var privateNotesBefore = Agent(persistedBefore, "alpha").PrivateNotes.ToArray();
            var client = new RecordingProviderClient(Success(rawModelOutput, model: "yi-coder-primary", responseId: "resp_factory"));
            var internet = new CountingInternetProvider();
            using var internetService = new InternetToolService(internet, log);
            var service = new TurnRunnerService(client, store, log, internetToolService: internetService);

            var result = service.RunAgentTurnAsync("default", "alpha", enforceVoiceDrift: true).GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Factory turn failed: {result.Error}");
            Require(client.Requests.Count == 1, "Factory mode must not add tool-continuation or fallback provider calls");
            Require(client.Requests[0].Count == 1
                && client.Requests[0][0].Role == "user"
                && client.Requests[0][0].Content == rawInput,
                "a newly established Factory participant call must contain the exact initiating public Operator root");
            Require(internet.Calls == 0, "Factory mode must bypass proactive and model-selected internet tools");
            Require(client.Configs.Count == 1, "Factory mode should capture one participant request config");
            Require(!client.Configs[0].NativeStatefulChat,
                "Factory mode must disable LM Studio conversational state/store for participant turns");
            Require(string.IsNullOrEmpty(client.Configs[0].PreviousResponseId),
                "Factory mode must clear prior provider response IDs");
            Require(client.Configs[0].NativeIdleTtlSeconds == configuredIdleTtl,
                "Factory mode must preserve configured model-residency TTL");

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var output = loaded.Engine.Messages.Single(message => message.Turn == 3 && message.SpeakerId == "alpha");
            Require(output.Text == rawModelOutput, "Factory mode must preserve a model-emitted tool-shaped response as raw output");
            Require(!output.Metadata.ContainsKey("tool_request") && !output.Metadata.ContainsKey("tool_result"),
                "Factory output must not claim that a tool was executed");
            Require(!output.Metadata.ContainsKey("voice_style"), "Factory output must not claim Arena voice shaping");
            Require(Agent(loaded, "alpha").PrivateNotes.SequenceEqual(privateNotesBefore),
                "Factory mode must not derive or mutate Arena private memory");
        });
    }

    public static void BypassesRepairAndFallback()
    {
        WithTempRoot(root =>
        {
            const string fragment = "this incomplete response would normally trigger a repair";
            var snapshot = FactorySnapshot();
            snapshot.Engine.Messages.Add(OperatorMessage(1, "Return the raw first completion.", createdAt: 1));
            snapshot.Engine.TurnCount = 1;
            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(
                Success(fragment),
                Success("unexpected repaired response."));
            var service = new TurnRunnerService(client, store, log);

            var result = service.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Factory repair-bypass turn failed: {result.Error}");
            Require(client.Requests.Count == 1, "Factory mode must not issue an automatic public-content repair call");
            Require(result.Message?.Text == fragment, "Factory mode must expose the provider's first raw completion unchanged");
            Require(!File.ReadAllText(log.EventPath()).Contains("empty_content_retry", StringComparison.Ordinal),
                "Factory mode must not emit a repair event that did not occur");
        });

        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            snapshot.Configs["alpha"] = ProviderConfig("primary-model");
            snapshot.Configs["shared"] = ProviderConfig("fallback-model");
            snapshot.Engine.Messages.Add(OperatorMessage(1, "Do not substitute another model.", createdAt: 1));
            snapshot.Engine.TurnCount = 1;
            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(
                Failure("primary provider failed", model: "primary-model"),
                Success("unexpected fallback response.", model: "fallback-model"));
            var service = new TurnRunnerService(client, store, log);

            var result = service.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();

            Require(result.Executed && result.Completion is { Ok: false },
                "Factory provider failure must remain a truthful failed completion");
            Require(client.Requests.Count == 1, "Factory mode must not substitute the shared fallback model");
            Require(client.Configs[0].Model == "primary-model", "Factory mode must call only the selected participant model");
            Require(result.Message?.Status == "error"
                && result.Message.Text.Contains("primary provider failed", StringComparison.Ordinal),
                "Factory transcript must preserve the selected provider failure honestly");
            Require(!File.ReadAllText(log.EventPath()).Contains("fallback_to_default", StringComparison.Ordinal),
                "Factory mode must not emit fallback evidence when no fallback occurred");
        });
    }

    public static void RetryUsesCausalPublicGroup()
    {
        WithTempRoot(root =>
        {
            const string causalInput = "causal Operator input\r\nwith exact spacing  ";
            const string futureInput = "future Operator input must not leak";
            const int configuredIdleTtl = 91;
            var snapshot = FactorySnapshot();
            snapshot.Configs["shared"] = ProviderConfig(
                "yi-coder-retry",
                nativeStatefulChat: true,
                nativeIdleTtlSeconds: configuredIdleTtl);
            var causalRoot = OperatorMessage(2, causalInput, createdAt: 20);
            var original = Message(
                3,
                "alpha",
                "Alpha",
                "original answer",
                createdAt: 30,
                model: "yi-coder-retry",
                metadata: new Dictionary<string, JsonElement>
                {
                    ["prompt_mode"] = JsonSerializer.SerializeToElement("factory")
                });
            snapshot.Engine.Messages.AddRange(
            [
                Message(
                    1,
                    "alpha",
                    "Alpha",
                    "earlier response",
                    createdAt: 10,
                    model: "yi-coder-retry",
                    metadata: new Dictionary<string, JsonElement>
                    {
                        ["provider_response_id"] = JsonSerializer.SerializeToElement("resp_old")
                    }),
                causalRoot,
                original,
                OperatorMessage(4, futureInput, createdAt: 40)
            ]);
            var conversation = new FactoryConversationService();
            var originalContext = conversation.BuildPromptContext(snapshot, "alpha", original.Turn);
            conversation.StampPublicParticipant(original, originalContext);
            snapshot.Engine.TurnCount = 4;

            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("replacement answer.", model: "yi-coder-retry"));
            var service = new TurnRunnerService(client, store, log);

            var result = service.RetryTurnAsync("default", turn: 3, speakerId: "alpha", createdAt: 30).GetAwaiter().GetResult();

            Require(result.Ok && result.Executed, $"Factory retry failed: {result.Error}");
            Require(client.Requests.Count == 1 && client.Requests[0].Count == 1,
                "Factory retry must make one exact provider call");
            Require(client.Requests[0][0].Role == "user" && client.Requests[0][0].Content == causalInput,
                "Factory retry must establish and use the causal Operator root before the retried turn");
            Require(!client.Requests[0][0].Content.Contains(futureInput, StringComparison.Ordinal),
                "Factory retry must exclude post-turn Operator input");
            Require(!client.Configs[0].NativeStatefulChat && string.IsNullOrEmpty(client.Configs[0].PreviousResponseId),
                "Factory retry must not inherit conversational provider state");
            Require(client.Configs[0].NativeIdleTtlSeconds == configuredIdleTtl,
                "Factory retry must preserve configured model-residency TTL");

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Engine.TurnCount == 4, "retry must not advance the durable turn counter");
            Require(loaded.Engine.Messages.Single(message => message.Turn == 3 && message.SpeakerId == "alpha").Text == "replacement answer.",
                "Factory retry must replace only the selected response");
            Require(loaded.Engine.Messages.Single(message => message.Turn == 4 && message.SpeakerId == "operator").Text == futureInput,
                "Factory retry must preserve later transcript history");
        });

        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            snapshot.Engine.Messages.AddRange(
            [
                OperatorMessage(1, "Legacy Arena direction", createdAt: 10),
                Message(2, "alpha", "Alpha", "unmarked legacy Arena response", createdAt: 20, model: "shared-model")
            ]);
            snapshot.Engine.TurnCount = 2;
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("replacement Arena response"));
            var runner = new TurnRunnerService(client, store, new EventLogStore(root));

            var result = runner.RetryTurnAsync("default", 2, "alpha", 20).GetAwaiter().GetResult();

            Require(result.Ok && client.Requests.Single().Count == 2
                && client.Requests[0][0].Role == "system"
                && client.Requests[0][0].Content.Contains("AI Arena - Lite", StringComparison.Ordinal),
                "an unmarked legacy response must retain the legacy Arena contract instead of inheriting the current Factory toggle");
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var replacement = loaded.Engine.Messages.Single(message => message.Turn == 2 && message.SpeakerId == "alpha");
            Require(loaded.Engine.FactoryMode
                && replacement.Metadata["prompt_mode"].GetString() == "arena",
                "legacy Arena retry must restore the current toggle while recording the replacement's actual prompt contract");
        });
    }

    public static void BuildsTargetRelativePublicGroupHistory()
    {
        const string rootText = "  Keep this initiating prompt exactly.\r\n";
        var snapshot = FactorySnapshot();
        var service = new FactoryConversationService();
        var root = OperatorMessage(1, rootText, createdAt: 10);
        snapshot.Engine.Messages.Add(root);
        var anchored = service.Resolve(snapshot);
        Require(anchored.HasUsableRoot && anchored.IsAnchored, "first Factory use must establish a usable group root");
        Require(anchored.RootMessageId == root.MessageId, "Factory root must use the stable initiating Operator message id");
        Require(anchored.ConversationId.StartsWith("factory-group:", StringComparison.Ordinal),
            "Factory group must receive an opaque durable conversation id");

        snapshot.Engine.Messages.AddRange(
        [
            Message(2, "alpha", "Alpha", "alpha first", createdAt: 20),
            Message(3, "beta", "Beta", "beta first", createdAt: 30),
            OperatorMessage(4, "operator follow-up", createdAt: 40),
            Message(5, "alpha", "Alpha", "alpha second", createdAt: 50),
            Message(6, "system", "System", "SYSTEM_MUST_NOT_LEAK", createdAt: 60),
            Message(7, "narrator", "Narrator", "NARRATOR_MUST_NOT_LEAK", createdAt: 70),
            Message(8, "internet", "Internet", "INTERNET_MUST_NOT_LEAK", kind: "internet", createdAt: 80),
            Message(9, "beta", "Beta", "FAILED_MUST_NOT_LEAK", status: "error", createdAt: 90),
            Message(10, "beta", "Beta", " \r\n\t ", createdAt: 100)
        ]);

        var alpha = service.BuildPromptContext(snapshot, "alpha");
        var beta = service.BuildPromptContext(snapshot, "beta");
        Require(alpha.Ok && beta.Ok, "both participant perspectives must resolve the same public group");
        Require(alpha.ContextFingerprint != beta.ContextFingerprint,
            "provider-context fingerprint must reflect the target-relative self/peer role mapping");
        Require(alpha.LogicalMessages.Count == 5 && beta.LogicalMessages.Count == 5,
            "Factory group must include only root, successful public participants, and later public Operator turns");
        Require(alpha.LogicalMessages[0] == new ModelChatMessage("user", rootText),
            "initiating Operator prompt must appear once, exact, and without an attribution envelope");
        Require(alpha.LogicalMessages[1] == new ModelChatMessage("assistant", "alpha first")
            && alpha.LogicalMessages[2] == new ModelChatMessage("user", "[Public participant: beta]\r\nbeta first".Replace("\r\n", Environment.NewLine))
            && alpha.LogicalMessages[3] == new ModelChatMessage("user", "[Public Operator]\r\noperator follow-up".Replace("\r\n", Environment.NewLine))
            && alpha.LogicalMessages[4] == new ModelChatMessage("assistant", "alpha second"),
            "Alpha must see its own exact replies as assistant history and attributed peer/Operator turns as user history");
        Require(beta.LogicalMessages[1] == new ModelChatMessage("user", "[Public participant: alpha]\r\nalpha first".Replace("\r\n", Environment.NewLine))
            && beta.LogicalMessages[2] == new ModelChatMessage("assistant", "beta first")
            && beta.LogicalMessages[4] == new ModelChatMessage("user", "[Public participant: alpha]\r\nalpha second".Replace("\r\n", Environment.NewLine)),
            "Beta must receive the inverse self/peer role mapping over the same chronological group");
        Require(alpha.LogicalMessages.Count(message => message.Content.Contains(rootText, StringComparison.Ordinal)) == 1,
            "Factory group must never silently repeat the initiating Operator prompt");
        Require(alpha.LogicalMessages.All(message => !message.Content.Contains("MUST_NOT_LEAK", StringComparison.Ordinal)),
            "Factory group must exclude System, Narrator, Internet, failed, and blank rows");
    }

    public static void NormalizesAdjacentRolesForStrictChatTemplates()
    {
        var snapshot = FactorySnapshot();
        var service = new FactoryConversationService();
        snapshot.Engine.Messages.Add(OperatorMessage(1, "strict root", createdAt: 1));
        service.Resolve(snapshot);
        snapshot.Engine.Messages.AddRange(
        [
            Message(2, "alpha", "Alpha", "alpha self", createdAt: 2),
            Message(3, "beta", "Beta", "beta peer", createdAt: 3),
            OperatorMessage(4, "operator follow-up", createdAt: 4),
            Message(5, "gamma", "Gamma", "gamma peer", createdAt: 5)
        ]);

        var context = service.BuildPromptContext(snapshot, "alpha");
        Require(context.Ok && context.IncludedEntryCount == 5 && context.LogicalMessages.Count == 5,
            "strict-template normalization fixture must retain all five semantic public entries before wire batching");
        var normalized = context.ProviderMessages;
        Require(normalized.SequenceEqual(
                FactoryConversationService.NormalizeProviderRequestMessages(context.LogicalMessages)),
            "FactoryPromptContext.ProviderMessages must explicitly expose the normalized collection used for transport");
        var plan = new OneTurnPlan(true, "alpha", "Alpha", snapshot.Configs["shared"], null, "");
        Require(TurnRunnerService.BuildPrompt(snapshot, plan).SequenceEqual(context.ProviderMessages),
            "TurnRunner Factory prompt construction must consume ProviderMessages rather than bypassing its encoding seam");
        Require(normalized.Count == 3
            && normalized.Select(message => message.Role).SequenceEqual(new[] { "user", "assistant", "user" }),
            "adjacent peer and Operator user entries must become one alternating provider block");
        var expectedFinalUser = string.Join(
            Environment.NewLine + Environment.NewLine,
            $"[Public participant: beta]{Environment.NewLine}beta peer",
            $"[Public Operator]{Environment.NewLine}operator follow-up",
            $"[Public participant: gamma]{Environment.NewLine}gamma peer");
        Require(normalized[0].Content == "strict root"
            && normalized[1] == new ModelChatMessage("assistant", "alpha self")
            && normalized[2] == new ModelChatMessage("user", expectedFinalUser),
            "Factory wire batching must preserve exact content, attribution, chronology, and self-role semantics");

        var betaContext = service.BuildPromptContext(snapshot, "beta");
        var normalizedBeta = betaContext.ProviderMessages;
        var expectedInitialUser = string.Join(
            Environment.NewLine + Environment.NewLine,
            "strict root",
            $"[Public participant: alpha]{Environment.NewLine}alpha self");
        var expectedBetaFinalUser = string.Join(
            Environment.NewLine + Environment.NewLine,
            $"[Public Operator]{Environment.NewLine}operator follow-up",
            $"[Public participant: gamma]{Environment.NewLine}gamma peer");
        Require(normalizedBeta.Count == 3
            && normalizedBeta[0] == new ModelChatMessage("user", expectedInitialUser)
            && normalizedBeta[1] == new ModelChatMessage("assistant", "beta peer")
            && normalizedBeta[2] == new ModelChatMessage("user", expectedBetaFinalUser),
            "a peer reply immediately after the root and peer replies after an Operator turn must normalize without changing attribution");

        var consecutiveSelf = FactoryConversationService.NormalizeProviderRequestMessages(
        [
            new ModelChatMessage("user", "prompt"),
            new ModelChatMessage("assistant", "first exact self reply"),
            new ModelChatMessage("assistant", "second exact self reply")
        ]);
        Require(consecutiveSelf.Count == 2
            && consecutiveSelf[1] == new ModelChatMessage(
                "assistant",
                string.Join(Environment.NewLine + Environment.NewLine, "first exact self reply", "second exact self reply")),
            "consecutive self replies must retain assistant semantics and exact chronological text in one role block");
        Require(consecutiveSelf[^1].Role == "assistant",
            "Factory role normalization must not invent a synthetic terminal user turn after self history");

        var wireEquivalent = FactorySnapshot();
        wireEquivalent.Engine.Messages.AddRange(
        [
            OperatorMessage(1, "strict root", createdAt: 1),
            Message(2, "alpha", "Alpha", "alpha self", createdAt: 2),
            Message(
                3,
                "beta",
                "Beta",
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    "beta peer",
                    $"[Public Operator]{Environment.NewLine}operator follow-up",
                    $"[Public participant: gamma]{Environment.NewLine}gamma peer"),
                createdAt: 3)
        ]);
        var wireEquivalentContext = service.BuildPromptContext(wireEquivalent, "alpha");
        Require(context.ContextFingerprint == wireEquivalentContext.ContextFingerprint
            && context.IncludedEntryCount == 5
            && wireEquivalentContext.IncludedEntryCount == 3,
            "Factory fingerprint must hash the actual normalized transport while logical entry counts stay unmerged");

        var saltFixture = FactorySnapshot();
        saltFixture.Engine.Messages.Add(OperatorMessage(1, "salted root", createdAt: 1));
        var currentSalted = service.BuildPromptContext(saltFixture, "alpha");
        var legacyUnsalted = service.BuildPromptContext(
            saltFixture,
            "alpha",
            promptEncoding: FactoryConversationService.LegacyPerEntryPromptEncoding);
        Require(currentSalted.ProviderMessages.SequenceEqual(legacyUnsalted.ProviderMessages)
            && currentSalted.ContextFingerprint != legacyUnsalted.ContextFingerprint
            && currentSalted.PromptEncoding == FactoryConversationService.AlternatingRunsPromptEncoding,
            "alternating-runs fingerprints must be salted by the explicit prompt encoding even when provider bytes match legacy");
    }

    public static void PreservesDurableParticipantHistoryAcrossRosterResize()
    {
        var snapshot = FactorySnapshot();
        AgentRosterService.EnsureParticipantCount(snapshot, 4);
        var service = new FactoryConversationService();
        snapshot.Engine.Messages.AddRange(
        [
            OperatorMessage(1, "durable roster root", createdAt: 1),
            Message(2, "alpha", "Alpha", "alpha before resize", createdAt: 2),
            Message(3, "beta", "Beta", "beta before resize", createdAt: 3),
            Message(4, "gamma", "Gamma", "gamma before resize", createdAt: 4)
        ]);

        var beforeResize = service.BuildPromptContext(snapshot, "alpha");
        Require(beforeResize.Ok && beforeResize.LogicalMessages.Count == 4,
            "Factory roster regression fixture must begin with the complete public group");

        AgentRosterService.EnsureParticipantCount(snapshot, 1);
        Require(snapshot.Engine.Agents.All(agent => !agent.Id.Equals("beta", StringComparison.OrdinalIgnoreCase)
                && !agent.Id.Equals("gamma", StringComparison.OrdinalIgnoreCase)),
            "roster shrink fixture must physically remove the prior Beta and Gamma slots");
        var afterShrink = service.BuildPromptContext(snapshot, "alpha");
        Require(afterShrink.Ok
            && afterShrink.LogicalMessages.SequenceEqual(beforeResize.LogicalMessages)
            && afterShrink.ContextFingerprint == beforeResize.ContextFingerprint
            && afterShrink.EligibleEntryCount == beforeResize.EligibleEntryCount,
            "shrinking the active roster must not silently remove earlier durable-slot replies from Factory history");

        AgentRosterService.EnsureParticipantCount(snapshot, 3);
        var afterResume = service.BuildPromptContext(snapshot, "alpha");
        Require(afterResume.Ok
            && afterResume.LogicalMessages.SequenceEqual(beforeResize.LogicalMessages)
            && afterResume.ContextFingerprint == beforeResize.ContextFingerprint,
            "resuming removed durable slots must not make their public history disappear and reappear in Factory context");
    }

    public static void PreservesSelfHistoryWhenAgentModelChanges()
    {
        var snapshot = FactorySnapshot();
        snapshot.Configs["alpha"] = ProviderConfig("alpha-model-v1");
        var service = new FactoryConversationService();
        snapshot.Engine.Messages.AddRange(
        [
            OperatorMessage(1, "model-change root", createdAt: 1),
            Message(2, "alpha", "Alpha", "alpha response from v1", createdAt: 2, model: "alpha-model-v1"),
            Message(3, "beta", "Beta", "beta peer response", createdAt: 3, model: "beta-model")
        ]);

        var beforeChange = service.BuildPromptContext(snapshot, "alpha");
        snapshot.Configs["alpha"] = ProviderConfig("alpha-model-v2");
        var routedAfterChange = ModelProviderRouting.Resolve(snapshot, "alpha", out _);
        var afterChange = service.BuildPromptContext(snapshot, "alpha");

        Require(routedAfterChange?.Model == "alpha-model-v2",
            "model-change regression fixture must route Alpha to the replacement model");
        Require(afterChange.Ok
            && afterChange.LogicalMessages.SequenceEqual(beforeChange.LogicalMessages)
            && afterChange.ContextFingerprint == beforeChange.ContextFingerprint,
            "changing Alpha's model must not alter the durable Alpha-relative Factory conversation identity");
        Require(afterChange.LogicalMessages[1] == new ModelChatMessage("assistant", "alpha response from v1")
            && afterChange.LogicalMessages[2].Role == "user"
            && afterChange.LogicalMessages[2].Content.EndsWith("beta peer response", StringComparison.Ordinal),
            "the replacement Alpha model must receive Alpha's prior public reply as self-history and Beta as attributed peer history");
    }

    public static void RetainsRootAndNewestFortyNineEntries()
    {
        var snapshot = FactorySnapshot();
        snapshot.Engine.TranscriptWindow = 1;
        var service = new FactoryConversationService();
        var root = OperatorMessage(1, "root", createdAt: 1);
        snapshot.Engine.Messages.Add(root);
        service.Resolve(snapshot);
        for (var turn = 2; turn <= 56; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn % 2 == 0 ? "alpha" : "beta",
                turn % 2 == 0 ? "Alpha" : "Beta",
                $"entry-{turn}-" + new string('x', 80),
                createdAt: turn));
        }

        var context = service.BuildPromptContext(snapshot, "alpha");
        Require(context.Ok, $"bounded Factory group failed: {context.Error}");
        Require(context.EligibleEntryCount == 56 && context.IncludedEntryCount == 50 && context.OmittedEntryCount == 6,
            "Factory group must retain root plus newest 49 entries and report whole-entry omissions");
        Require(context.LogicalMessages[0].Content == "root", "Factory cap must never evict the initiating Operator root");
        Require(context.LogicalMessages[1].Content.Contains("entry-8-", StringComparison.Ordinal)
            && context.LogicalMessages[^1].Content == "entry-56-" + new string('x', 80),
            "Factory cap must drop the oldest non-root entries without trimming retained text");
        Require(context.LogicalMessages.Count == 50,
            "Factory group cap must ignore the configurable Arena transcript_window");

        var output = Message(57, "alpha", "Alpha", "next output", createdAt: 57);
        service.StampPublicParticipant(output, context);
        Require(output.Metadata[FactoryConversationService.ContextEntryCountMetadataKey].GetInt32() == 50
            && output.Metadata[FactoryConversationService.ContextOmittedCountMetadataKey].GetInt32() == 6,
            "Factory completion metadata must report included and omitted group counts");
        Require(output.Metadata[FactoryConversationService.ContextFingerprintMetadataKey].GetString() == context.ContextFingerprint,
            "Factory completion must retain the privacy-safe causal context fingerprint");
    }

    public static void FingerprintsActualRetainedProviderContext()
    {
        static ArenaSnapshot Group(string identityPrefix, bool reverseReplies = false, string betaText = "beta reply")
        {
            var snapshot = FactorySnapshot();
            var conversations = new FactoryConversationService();
            var root = OperatorMessage(1, "same root", createdAt: 1);
            root.MessageId = $"message:{identityPrefix}:root";
            snapshot.Engine.Messages.Add(root);
            conversations.Resolve(snapshot);
            var alpha = Message(reverseReplies ? 3 : 2, "alpha", "Alpha", "alpha reply", createdAt: reverseReplies ? 3 : 2);
            alpha.MessageId = $"message:{identityPrefix}:alpha";
            var beta = Message(reverseReplies ? 2 : 3, "beta", "Beta", betaText, createdAt: reverseReplies ? 2 : 3);
            beta.MessageId = $"message:{identityPrefix}:beta";
            snapshot.Engine.Messages.AddRange(reverseReplies ? [beta, alpha] : [alpha, beta]);
            return snapshot;
        }

        var conversations = new FactoryConversationService();
        var first = Group("first");
        var second = Group("second");
        var firstAlpha = conversations.BuildPromptContext(first, "alpha");
        var secondAlpha = conversations.BuildPromptContext(second, "alpha");
        Require(firstAlpha.ConversationId != secondAlpha.ConversationId
            && firstAlpha.RootMessageId != secondAlpha.RootMessageId,
            "fingerprint regression requires independently identified Factory conversations");
        Require(firstAlpha.ContextFingerprint == secondAlpha.ContextFingerprint,
            "byte-identical target-relative role/content context must have the same fingerprint across clean conversations");
        Require(conversations.Inspect(first).ContextFingerprint == conversations.Inspect(second).ContextFingerprint,
            "run-level public-group fingerprint must also ignore opaque lineage and message identities");

        var firstBeta = conversations.BuildPromptContext(first, "beta");
        Require(firstAlpha.ContextFingerprint != firstBeta.ContextFingerprint,
            "Alpha and Beta perspectives must fingerprint differently when self/peer roles differ");

        var changedContent = conversations.BuildPromptContext(Group("content", betaText: "changed beta reply"), "alpha");
        var changedOrder = conversations.BuildPromptContext(Group("order", reverseReplies: true), "alpha");
        Require(firstAlpha.ContextFingerprint != changedContent.ContextFingerprint,
            "provider-context fingerprint must change when retained public content changes");
        Require(firstAlpha.ContextFingerprint != changedOrder.ContextFingerprint,
            "provider-context fingerprint must change when retained public order changes");

        var output = Message(4, "alpha", "Alpha", "completion", createdAt: 4);
        conversations.StampPublicParticipant(output, firstAlpha);
        Require(FactoryConversationMetadata(output, FactoryConversationService.ContextFingerprintMetadataKey)
                == firstAlpha.ContextFingerprint,
            "Factory output metadata must store the target-relative provider-context fingerprint");
    }

    public static void PreservesArenaInterludeAndLaterOperatorTurns()
    {
        var snapshot = FactorySnapshot();
        var service = new FactoryConversationService();
        var root = OperatorMessage(1, "group root", createdAt: 1);
        snapshot.Engine.Messages.Add(root);
        var firstContext = service.BuildPromptContext(snapshot, "alpha");
        var factoryReply = Message(2, "alpha", "Alpha", "factory alpha", createdAt: 2);
        factoryReply.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
        service.StampPublicParticipant(factoryReply, firstContext);
        snapshot.Engine.Messages.Add(factoryReply);

        snapshot.Engine.FactoryMode = false;
        var arenaReply = Message(3, "beta", "Beta", "arena-shaped beta", createdAt: 3);
        arenaReply.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("arena");
        service.StampPublicParticipant(snapshot, arenaReply);
        snapshot.Engine.Messages.Add(arenaReply);
        var laterOperator = OperatorMessage(4, "new public direction", createdAt: 4);
        snapshot.Engine.Messages.Add(laterOperator);
        service.StampPublicOperator(snapshot, laterOperator);

        snapshot.Engine.FactoryMode = true;
        var resumed = service.BuildPromptContext(snapshot, "alpha");
        Require(resumed.Ok && resumed.LogicalMessages.Count == 4,
            "Factory resume must preserve the established group across an Arena interlude");
        Require(resumed.LogicalMessages[1] == new ModelChatMessage("assistant", "factory alpha"),
            "Factory resume must preserve the target's Factory self-history");
        Require(resumed.LogicalMessages[2].Content.EndsWith("arena-shaped beta", StringComparison.Ordinal)
            && resumed.LogicalMessages[2].Role == "user",
            "successful Arena interlude replies must enter resumed Factory peer history");
        Require(resumed.LogicalMessages[3].Content.EndsWith("new public direction", StringComparison.Ordinal),
            "later public Operator turns must append instead of replacing the root");
        Require(FactoryConversationMetadata(laterOperator, FactoryConversationService.ConversationIdMetadataKey)
                == firstContext.ConversationId,
            "public Operator path must retain the existing conversation identity while Factory is off");
    }

    public static void OrphanedRootNeverSilentlyReanchors()
    {
        var snapshot = FactorySnapshot();
        var service = new FactoryConversationService();
        var root = OperatorMessage(1, "root to delete", createdAt: 1);
        snapshot.Engine.Messages.Add(root);
        var context = service.BuildPromptContext(snapshot, "alpha");
        var reply = Message(2, "alpha", "Alpha", "marked reply", createdAt: 2);
        service.StampPublicParticipant(reply, context);
        snapshot.Engine.Messages.Add(reply);
        snapshot.Engine.Messages.Remove(root);

        var replacementCandidate = OperatorMessage(3, "must not become a hidden replacement root", createdAt: 3);
        snapshot.Engine.Messages.Add(replacementCandidate);
        service.StampPublicOperator(snapshot, replacementCandidate);
        var inspection = service.Inspect(snapshot);
        var blocked = service.BuildPromptContext(snapshot, "alpha");
        Require(!inspection.HasUsableRoot && inspection.IsOrphaned,
            "deleting an established root must leave an explicit orphaned conversation state");
        Require(!blocked.Ok && blocked.Error == FactoryConversationService.OrphanedRootError,
            "orphaned Factory history must block rather than promote a later Operator turn");
        Require(!replacementCandidate.Metadata.ContainsKey(FactoryConversationService.ContractMetadataKey),
            "later Operator turns must not be stamped as a replacement root after deletion");
    }

    public static void ProtectsSoleRootDeletionWithoutBlockingReset()
    {
        var snapshot = FactorySnapshot();
        var conversations = new FactoryConversationService();
        var transcript = new TranscriptService();
        var soleRoot = OperatorMessage(1, "sole durable root", createdAt: 1);
        snapshot.Engine.Messages.Add(soleRoot);
        var first = conversations.Resolve(snapshot);
        Require(first.HasUsableRoot && FactoryConversationService.IsConversationRoot(soleRoot),
            "Factory first use must mark the sole Operator entry as the durable root");

        var deletedRoot = transcript.DeleteMessage(snapshot, soleRoot.Turn, soleRoot.SpeakerId, soleRoot.CreatedAt);
        Require(!deletedRoot && snapshot.Engine.Messages.Count == 1 && ReferenceEquals(snapshot.Engine.Messages[0], soleRoot),
            "authoritative individual deletion must protect even a sole Factory root");

        var ordinary = Message(2, "alpha", "Alpha", "ordinary public reply", createdAt: 2);
        snapshot.Engine.Messages.Add(ordinary);
        Require(transcript.DeleteMessage(snapshot, ordinary.Turn, ordinary.SpeakerId, ordinary.CreatedAt),
            "Factory root protection must not block ordinary transcript deletion");

        snapshot.Engine.Messages.Clear();
        Require(snapshot.Engine.Messages.Count == 0 && !conversations.HasUsableRoot(snapshot),
            "intentional whole-arena reset must remain able to clear the Factory group");
        var cleanRoot = OperatorMessage(1, "clean-session root", createdAt: 3);
        snapshot.Engine.Messages.Add(cleanRoot);
        var clean = conversations.Resolve(snapshot);
        Require(clean.HasUsableRoot && clean.RootMessageId == cleanRoot.MessageId
            && clean.ConversationId != first.ConversationId,
            "a clean post-reset session must establish a fresh Factory conversation normally");
    }

    public static void PersistsAcrossRestartAndProjectsForksCausally()
    {
        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            var conversations = new FactoryConversationService();
            var beforeRoot = Message(1, "alpha", "Alpha", "pre-root cursor", createdAt: 1);
            var groupRoot = OperatorMessage(2, "durable fork root", createdAt: 2);
            snapshot.Engine.Messages.AddRange([beforeRoot, groupRoot]);
            var alphaContext = conversations.BuildPromptContext(snapshot, "alpha");
            var reply = Message(3, "alpha", "Alpha", "durable group reply", createdAt: 3);
            reply.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
            conversations.StampPublicParticipant(reply, alphaContext);
            snapshot.Engine.Messages.Add(reply);
            snapshot.Engine.TurnCount = 3;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot, "source").GetAwaiter().GetResult();
            var restarted = new SessionStore(root).LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            var restartedInspection = conversations.Inspect(restarted);
            Require(restartedInspection.HasUsableRoot
                && restartedInspection.ConversationId == alphaContext.ConversationId
                && restartedInspection.RootMessageId == alphaContext.RootMessageId
                && restartedInspection.ContextFingerprint == conversations.Inspect(snapshot).ContextFingerprint,
                "Factory group root, conversation identity, and public context must survive persistence restart");

            var fullForkResult = store.ForkSessionAsync("source", "factory-full-fork").GetAwaiter().GetResult();
            var fullFork = store.LoadSnapshotAsync(fullForkResult.TargetSessionId).GetAwaiter().GetResult()!;
            var fullForkInspection = conversations.Inspect(fullFork);
            Require(fullForkInspection.HasUsableRoot
                && fullForkInspection.ConversationId == restartedInspection.ConversationId
                && fullForkInspection.RootMessageId == restartedInspection.RootMessageId
                && fullForkInspection.ContextFingerprint == restartedInspection.ContextFingerprint,
                "full-state fork after the root must preserve the durable Factory group lineage and context");

            var historicalResult = store.ForkSessionAtCursorAsync(
                "source",
                DialogueMessageIdentity.Resolve(beforeRoot),
                "factory-before-root-fork").GetAwaiter().GetResult();
            var historical = store.LoadSnapshotAsync(historicalResult.TargetSessionId).GetAwaiter().GetResult()!;
            var historicalInspection = conversations.Inspect(historical);
            Require(!historicalInspection.HasUsableRoot && !historicalInspection.IsAnchored,
                "historical fork before the root must contain no usable or orphaned Factory group anchor");
            Require(historical.Engine.Messages.All(message =>
                    !message.Metadata.ContainsKey(FactoryConversationService.ContractMetadataKey)
                    && !message.Metadata.ContainsKey(FactoryConversationService.ConversationIdMetadataKey)
                    && !message.Metadata.ContainsKey(FactoryConversationService.RootMessageIdMetadataKey)),
                "historical fork before the root must not leak future Factory conversation markers");

            var independentRoot = OperatorMessage(2, "independent fork root", createdAt: 20);
            historical.Engine.Messages.Add(independentRoot);
            var independent = conversations.Resolve(historical);
            Require(independent.HasUsableRoot
                && independent.RootMessageId == independentRoot.MessageId
                && independent.ConversationId != restartedInspection.ConversationId,
                "historical fork before the original root must be able to establish an independent Factory group later");
        });
    }

    public static void MigratesLegacyFactoryHistoryFromCausalRoot()
    {
        var snapshot = FactorySnapshot();
        var legacyFactory = Message(2, "alpha", "Alpha", "legacy Factory output", createdAt: 2);
        legacyFactory.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
        var causalRoot = OperatorMessage(1, "legacy causal root", createdAt: 1);
        var futureOperator = OperatorMessage(3, "later Operator must remain later", createdAt: 3);
        snapshot.Engine.Messages.AddRange([causalRoot, legacyFactory, futureOperator]);

        var service = new FactoryConversationService();
        var resolved = service.Resolve(snapshot);
        Require(resolved.HasUsableRoot && resolved.RootMessageId == causalRoot.MessageId,
            "legacy migration must anchor the latest Operator strictly before the earliest successful Factory output");
        Require(resolved.IncludedMessages.SequenceEqual(new[] { causalRoot, legacyFactory, futureOperator }),
            "legacy migration must preserve later successful public group chronology");
        Require(FactoryConversationMetadata(legacyFactory, FactoryConversationService.ContractMetadataKey)
                == FactoryConversationService.ContractVersion,
            "legacy Factory output must be upgraded to the public group contract");

        var failedSnapshot = FactorySnapshot();
        var failedRoot = OperatorMessage(1, "failed-call causal root", createdAt: 10);
        var failedFactory = Message(
            2,
            "alpha",
            "Alpha",
            "Model call failed: legacy provider error",
            status: "error",
            createdAt: 20);
        failedFactory.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
        var laterCandidate = OperatorMessage(3, "must stay a later group turn", createdAt: 30);
        failedSnapshot.Engine.Messages.AddRange([failedRoot, failedFactory, laterCandidate]);

        var failedMigration = service.Resolve(failedSnapshot);
        Require(failedMigration.HasUsableRoot && failedMigration.RootMessageId == failedRoot.MessageId,
            "a failed legacy Factory attempt must still preserve the Operator turn that causally initiated it");
        Require(failedMigration.IncludedMessages.SequenceEqual(new[] { failedRoot, laterCandidate })
            && !failedMigration.IncludedMessages.Contains(failedFactory),
            "failed legacy output must define the migration boundary without entering successful public group content");
    }

    public static void RetryUsesOriginalFactoryContractAndCausalGroup()
    {
        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            var conversation = new FactoryConversationService();
            var groupRoot = OperatorMessage(1, "root question", createdAt: 1);
            snapshot.Engine.Messages.Add(groupRoot);
            var alphaContext = conversation.BuildPromptContext(snapshot, "alpha");
            var alphaPrior = Message(2, "alpha", "Alpha", "alpha prior", createdAt: 2);
            alphaPrior.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
            conversation.StampPublicParticipant(alphaPrior, alphaContext);
            snapshot.Engine.Messages.Add(alphaPrior);
            var betaPrior = Message(3, "beta", "Beta", "beta prior", createdAt: 3);
            conversation.StampPublicParticipant(snapshot, betaPrior);
            snapshot.Engine.Messages.Add(betaPrior);
            var laterOperator = OperatorMessage(4, "pre-retry operator", createdAt: 4);
            snapshot.Engine.Messages.Add(laterOperator);
            conversation.StampPublicOperator(snapshot, laterOperator);
            var originalContext = conversation.BuildPromptContext(snapshot, "alpha");
            var original = Message(5, "alpha", "Alpha", "original alpha", createdAt: 5, model: "shared-model");
            original.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
            conversation.StampPublicParticipant(original, originalContext);
            snapshot.Engine.Messages.Add(original);
            snapshot.Engine.Messages.Add(Message(6, "beta", "Beta", "future beta", createdAt: 6));
            snapshot.Engine.Messages.Add(OperatorMessage(7, "future operator", createdAt: 7));
            snapshot.Engine.TurnCount = 7;
            snapshot.Engine.FactoryMode = false;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("replacement alpha"));
            var runner = new TurnRunnerService(client, store, new EventLogStore(root));
            var result = runner.RetryTurnAsync("default", 5, "alpha", 5).GetAwaiter().GetResult();

            Require(result.Ok, $"causal Factory retry failed: {result.Error}");
            Require(client.Requests.Count == 1
                && client.Requests[0].SequenceEqual(originalContext.ProviderMessages)
                && client.Requests[0].Select(message => message.Role).SequenceEqual(new[] { "user", "assistant", "user" }),
                "Factory retry must send the exact alternating-runs provider collection that produced the original fingerprint");
            Require(client.Requests[0][2].Content == string.Join(
                    Environment.NewLine + Environment.NewLine,
                    $"[Public participant: beta]{Environment.NewLine}beta prior",
                    $"[Public Operator]{Environment.NewLine}pre-retry operator"),
                "current Factory retry must preserve peer/Operator chronology inside the normalized user run");
            Require(client.Requests[0].All(message => !message.Content.Contains("original alpha", StringComparison.Ordinal)
                && !message.Content.Contains("future beta", StringComparison.Ordinal)
                && !message.Content.Contains("future operator", StringComparison.Ordinal)),
                "Factory retry must exclude the replaced response and every future group entry");
            Require(client.Configs.Single().RequestInspectionContext?.Explanations.Any(explanation =>
                    explanation.Subject == "factory_mode"
                    && explanation.Explanation.Contains(
                        FactoryConversationService.AlternatingRunsPromptEncoding,
                        StringComparison.Ordinal)) == true,
                "provider-request inspection must truthfully report the exact Factory prompt encoding it observed");
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(!loaded.Engine.FactoryMode, "retrying an original Factory response must not change the current Arena/Factory toggle");
            var replacement = loaded.Engine.Messages.Single(message => message.Turn == 5 && message.SpeakerId == "alpha");
            Require(replacement.Text == "replacement alpha"
                && FactoryConversationMetadata(replacement, FactoryConversationService.PromptEncodingMetadataKey)
                    == FactoryConversationService.AlternatingRunsPromptEncoding
                && FactoryConversationMetadata(replacement, FactoryConversationService.ContextFingerprintMetadataKey)
                    == originalContext.ContextFingerprint
                && FactoryConversationMetadata(original, FactoryConversationService.ContextFingerprintMetadataKey)
                    == originalContext.ContextFingerprint,
                "Factory retry must replace the original exactly once while preserving later history");
        });

        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            var conversation = new FactoryConversationService();
            snapshot.Engine.Messages.AddRange(
            [
                OperatorMessage(1, "legacy root", createdAt: 1),
                Message(2, "beta", "Beta", "legacy peer", createdAt: 2)
            ]);
            var legacyContext = conversation.BuildPromptContext(
                snapshot,
                "alpha",
                promptEncoding: FactoryConversationService.LegacyPerEntryPromptEncoding);
            var original = Message(3, "alpha", "Alpha", "legacy original", createdAt: 3, model: "shared-model");
            original.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
            conversation.StampPublicParticipant(original, legacyContext);
            original.Metadata.Remove(FactoryConversationService.PromptEncodingMetadataKey);
            snapshot.Engine.Messages.Add(original);
            snapshot.Engine.Messages.Add(OperatorMessage(4, "legacy future", createdAt: 4));
            snapshot.Engine.TurnCount = 4;
            snapshot.Engine.FactoryMode = false;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("legacy replacement"));
            var runner = new TurnRunnerService(client, store, new EventLogStore(root));
            var result = runner.RetryTurnAsync("default", 3, "alpha", 3).GetAwaiter().GetResult();

            Require(result.Ok
                && legacyContext.ProviderMessages.Select(message => message.Role).SequenceEqual(new[] { "user", "user" })
                && client.Requests.Single().SequenceEqual(legacyContext.ProviderMessages),
                "unmarked legacy Factory retry must retain its validated per-entry wire mapping instead of adopting alternating runs");
            Require(client.Requests[0].All(message => !message.Content.Contains("legacy future", StringComparison.Ordinal)),
                "legacy Factory retry must still exclude future public entries");
            var replacement = store.LoadSnapshotAsync().GetAwaiter().GetResult()!
                .Engine.Messages.Single(message => message.Turn == 3 && message.SpeakerId == "alpha");
            Require(FactoryConversationMetadata(replacement, FactoryConversationService.PromptEncodingMetadataKey)
                    == FactoryConversationService.LegacyPerEntryPromptEncoding
                && FactoryConversationMetadata(replacement, FactoryConversationService.ContextFingerprintMetadataKey)
                    == legacyContext.ContextFingerprint,
                "validated legacy retry replacement must record the preserved encoding and original causal fingerprint");
        });

        WithTempRoot(root =>
        {
            var snapshot = FactorySnapshot();
            var conversation = new FactoryConversationService();
            snapshot.Engine.Messages.Add(OperatorMessage(1, "unverifiable root", createdAt: 1));
            var legacyContext = conversation.BuildPromptContext(
                snapshot,
                "alpha",
                promptEncoding: FactoryConversationService.LegacyPerEntryPromptEncoding);
            var original = Message(2, "alpha", "Alpha", "unverifiable original", createdAt: 2, model: "shared-model");
            original.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement("factory");
            conversation.StampPublicParticipant(original, legacyContext);
            original.Metadata.Remove(FactoryConversationService.PromptEncodingMetadataKey);
            original.Metadata[FactoryConversationService.ContextFingerprintMetadataKey] = JsonSerializer.SerializeToElement("mismatch");
            snapshot.Engine.Messages.Add(original);
            snapshot.Engine.TurnCount = 2;

            var store = new SessionStore(root);
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var client = new RecordingProviderClient(Success("must not execute"));
            var runner = new TurnRunnerService(client, store, new EventLogStore(root));
            var result = runner.RetryTurnAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();

            Require(!result.Ok && !result.Executed
                && result.Error == FactoryConversationService.RetryContextValidationError
                && client.Requests.Count == 0,
                "unmarked Factory retry with an unverifiable stored fingerprint must fail before provider execution");
        });
    }

    private static string FactoryConversationMetadata(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static ArenaSnapshot FactorySnapshot()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.FactoryMode = true;
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.TurnCount = 0;
        snapshot.Engine.TurnIndex = 0;
        snapshot.Engine.LastError = "";
        snapshot.Configs.Clear();
        snapshot.Configs["shared"] = ProviderConfig("shared-model");
        foreach (var agent in snapshot.Engine.Agents)
        {
            agent.Active = agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase);
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
            agent.MemoryEntries.Clear();
        }

        return snapshot;
    }

    private static ModelProviderConfig ProviderConfig(
        string model,
        bool nativeStatefulChat = true,
        int nativeIdleTtlSeconds = 47,
        string apiMode = ModelProviderApiModes.LmStudioNative)
    {
        return new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = apiMode,
            Model = model,
            Timeout = 30,
            Temperature = 0.2,
            MaxOutputTokens = 256,
            ContextLength = 4096,
            Reasoning = "off",
            NativeStatefulChat = nativeStatefulChat,
            NativeIdleTtlSeconds = nativeIdleTtlSeconds
        };
    }

    private static DialogueAgent Agent(ArenaSnapshot snapshot, string id)
    {
        return snapshot.Engine.Agents.Single(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private static DialogueMessage OperatorMessage(
        int turn,
        string text,
        string status = "ok",
        string kind = "message",
        double createdAt = 0)
    {
        return Message(turn, "operator", "Operator", text, status, kind, createdAt, "operator");
    }

    private static DialogueMessage Message(
        int turn,
        string speakerId,
        string speaker,
        string text,
        string status = "ok",
        string kind = "message",
        double createdAt = 0,
        string model = "",
        Dictionary<string, JsonElement>? metadata = null)
    {
        return new DialogueMessage
        {
            MessageId = $"message:factory-test:{turn}:{speakerId}:{createdAt}",
            Turn = turn,
            Speaker = speaker,
            SpeakerId = speakerId,
            Text = text,
            Status = status,
            Kind = kind,
            CreatedAt = createdAt,
            Model = new ModelMetadata { Model = model },
            Metadata = metadata ?? new Dictionary<string, JsonElement>()
        };
    }

    private static ModelCompletionResult Success(string text, string model = "shared-model", string responseId = "")
    {
        return new ModelCompletionResult(
            true,
            "http://127.0.0.1:1234/v1",
            model,
            text,
            "provider reasoning",
            12,
            7,
            5,
            12,
            "",
            DateTimeOffset.UnixEpoch,
            ResponseId: responseId);
    }

    private static ModelCompletionResult Failure(string error, string model)
    {
        return new ModelCompletionResult(
            false,
            "http://127.0.0.1:1234/v1",
            model,
            "",
            "",
            12,
            0,
            0,
            0,
            error,
            DateTimeOffset.UnixEpoch);
    }

    private static void WithTempRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-factory-mode-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            action(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingProviderClient(params ModelCompletionResult[] results) : IModelProviderClient
    {
        private readonly Queue<ModelCompletionResult> _results = new(results);

        public List<ModelProviderConfig> Configs { get; } = [];

        public List<IReadOnlyList<ModelChatMessage>> Requests { get; } = [];

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UnixEpoch));
        }

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Configs.Add(config);
            Requests.Add(messages.ToArray());
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("Unexpected participant provider call.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CountingInternetProvider : IInternetToolProvider
    {
        public int Calls { get; private set; }

        public Task<InternetToolResult> ExecuteAsync(
            InternetToolRequest request,
            InternetSettings settings,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new InternetToolResult
            {
                Ok = true,
                Tool = request.Tool,
                Query = request.Query,
                Url = request.Url,
                Summary = "unexpected internet result",
                CheckedAt = DateTimeOffset.UnixEpoch
            });
        }
    }
}
