using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ProviderReasoningOutcomeTests
{
    private const string Reasoning = "Private reasoning stays separate";
    private const string Model = "reasoning-fixture";
    private const string Token = "fixtureToken483291";
    private static readonly string[] Modes =
    [
        ModelProviderApiModes.LmStudioNative,
        ModelProviderApiModes.OpenAiCompatible,
        ModelProviderApiModes.OllamaNative
    ];
    private static readonly ModelChatMessage[] Messages = [new("user", "Answer briefly.")];

    internal static void PreservesPublicWhitespaceAcrossTransports()
    {
        const string output = " \tindented answer\r\n ";
        foreach (var mode in Modes.Append(ModelProviderApiModes.LlamaCppNative))
        foreach (var streaming in new[] { false, true })
        {
            var body = Body(mode, "stop", output);
            using var handler = new ReplyHandler(streaming ? PublicStream(mode, body, output) : body);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), streaming);
            Require(result.Ok && result.Text == output && handler.Calls == 1,
                $"{mode} ({(streaming ? "streaming" : "buffered")}) altered public whitespace at the provider boundary.");
            var emptyBody = Body(mode, "stop", " \t\r\n");
            using var emptyHandler = new ReplyHandler(streaming ? PublicStream(mode, emptyBody, " \t\r\n") : emptyBody);
            using var emptyHttp = new HttpClient(emptyHandler);
            var empty = Complete(new ModelProviderClient(emptyHttp), Config(mode), streaming);
            Require(!empty.Ok && empty.FailureKind == ModelCompletionFailureKind.EmptyPublicContent,
                $"{mode} whitespace-only output became a successful answer or an inconsistent public stream.");
        }
    }

    internal static void PreservesPublicWhitespaceAcrossStreamFailures()
    {
        const string partial = " \tpartial answer\r\n ";
        foreach (var mode in Modes.Append(ModelProviderApiModes.LlamaCppNative))
        foreach (var transportFailure in new[] { false, true })
        {
            var prefix = mode switch
            {
                ModelProviderApiModes.LmStudioNative => "data: " + JsonSerializer.Serialize(new { type = "message.delta", content = partial }) + "\n\n",
                ModelProviderApiModes.OllamaNative => JsonSerializer.Serialize(new { message = new { content = partial }, done = false }) + "\n",
                _ => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = partial } } } }) + "\n\n"
            };
            using var handler = new ReplyHandler(prefix, failAtEof: transportFailure);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), streaming: true);
            Require(!result.Ok && result.Text == partial && handler.Calls == 1,
                $"{mode} altered accepted partial output on {(transportFailure ? "transport failure" : "incomplete stream")}.");
        }
    }

    internal static void ContinuationPreservesWhitespaceThroughRealProviderClient()
    {
        foreach (var mode in Modes.Append(ModelProviderApiModes.LlamaCppNative))
        foreach (var streaming in new[] { false, true })
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.Combine(tempRoot, $"ai-arena-whitespace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var store = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                var config = Config(mode);
                snapshot.Configs["shared"] = config;
                ModelRuntimeSettingsRegistry.Register(snapshot, config, 16384,
                    ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
                snapshot.Engine.Messages.Clear();
                const string first = "The first half";
                const string suffix = " and the second half.\r\n";
                var partial = new DialogueMessage
                {
                    MessageId = "partial-answer", Turn = 2, SpeakerId = "alpha", Speaker = "Alpha",
                    Text = first, Status = "ok", Kind = "message", CreatedAt = 2
                };
                partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
                CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
                snapshot.Engine.Messages.Add(partial);
                snapshot.Engine.TurnCount = 2;
                store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
                var body = Body(mode, "stop", suffix);
                using var handler = new ReplyHandler(streaming ? PublicStream(mode, body, suffix) : body);
                using var http = new HttpClient(handler);
                var result = new ContextRecoveryService(store, new ModelProviderClient(http))
                    .ContinueOutputAsync("default", 2, "alpha", 2,
                        progress: streaming ? new Progress<ArenaTurnProgress>() : null).GetAwaiter().GetResult();
                Require(result.Ok && result.Message?.Text == first + suffix && handler.Calls == 1,
                    $"{mode} continuation joined words or lost formatting through the real provider transport: {result.Error}");
                var restored = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
                Require(restored.Engine.Messages.Single().Text == first + suffix,
                    $"{mode} continuation did not persist exact public output.");
            }
            finally
            {
                Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), tempRoot, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("ai-arena-whitespace-", StringComparison.Ordinal),
                    "refusing to clean a fixture outside its temporary root");
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string PublicStream(string mode, string response, string output) =>
        mode == ModelProviderApiModes.LmStudioNative
            ? "data: " + JsonSerializer.Serialize(new { type = "message.delta", content = output })
                + "\n\ndata: {\"type\":\"chat.end\",\"result\":" + response + "}\n\n"
            : Stream(mode, response);

    internal static void IncludesExplicitNativeReasoningOffInBothPayloads()
    {
        foreach (var streaming in new[] { false, true })
        {
            var response = Body(ModelProviderApiModes.LmStudioNative, "stop", publicText: "Visible answer");
            using var handler = new ReplyHandler(streaming ? NativeStream(response) : response);
            using var http = new HttpClient(handler);
            var client = new ModelProviderClient(http);
            var config = Config(ModelProviderApiModes.LmStudioNative, reasoning: "off");
            var result = Complete(client, config, streaming);
            using var payload = JsonDocument.Parse(handler.RequestBody);
            Require(result.Ok && result.Text == "Visible answer", "Native reasoning-off fixture did not complete.");
            Require(payload.RootElement.GetProperty("reasoning").GetString() == "off"
                && (payload.RootElement.TryGetProperty("stream", out var streamValue) && streamValue.GetBoolean()) == streaming,
                "Explicit native reasoning off was omitted or streaming mode changed.");
            Require(handler.Calls == 1 && handler.Path == "/api/v1/chat"
                && handler.Authorization == "Bearer " + Token,
                "Reasoning off changed provider identity, credential, endpoint, or request count.");
        }
    }

    internal static void NormalizesReasoningOnlyResponsesWithoutInventingOutputLimits()
    {
        foreach (var mode in Modes)
        foreach (var streaming in new[] { false, true })
        foreach (var stop in new[] { "", "stop", "length", "provider_specific_end" })
        {
            var response = Body(mode, stop);
            using var handler = new ReplyHandler(streaming ? Stream(mode, response) : response);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), streaming);
            var expectedStop = ModelCompletionOutcomeClassifier.ClassifyStopReason(stop);
            Require(!result.Ok && result.Text.Length == 0 && result.Reasoning == Reasoning
                && result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent,
                $"{mode} reasoning-only response was misclassified or lost reasoning.");
            Require(result.CompletionTokens == 1024 && result.PromptTokens == 7
                && result.StopReason == expectedStop && result.ProviderStopReason == stop,
                $"{mode} lost terminal evidence or inferred an output stop merely from the configured output count.");
            Require(handler.Calls == 1, $"{mode} provider adapter retried a completed empty response.");
        }

        var normalized = ModelCompletionOutcomeClassifier.Normalize(new ModelCompletionResult(
            true, "http://localhost", Model, " \r\n", Reasoning, 1, 7, 1024, 1031, "",
            DateTimeOffset.UtcNow));
        Require(!normalized.Ok && normalized.FailureKind == ModelCompletionFailureKind.EmptyPublicContent
            && normalized.StopReason == ModelCompletionStopReason.Unknown && normalized.Reasoning == Reasoning,
            "A successful alternate provider with no public answer did not normalize consistently.");
    }

    internal static void ExcludesStructuredToolOutputAndProtectsTerminalMetadata()
    {
        foreach (var mode in Modes)
        foreach (var streaming in new[] { false, true })
        {
            // The generated arguments contain stop-looking fields; these must
            // never become provider terminal metadata or a recovery trigger.
            var response = Body(mode, "", toolOutput: true);
            using var handler = new ReplyHandler(streaming ? Stream(mode, response) : response);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), streaming);
            Require(!result.Ok && result.Reasoning == Reasoning
                && result.StopReason == ModelCompletionStopReason.ToolCall
                && result.ProviderStopReason.Length == 0,
                $"{mode} confused structured tool output or its arguments with reasoning-only terminal evidence.");
        }

        var truncatedTool = "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    delta = new
                    {
                        content = "Visible partial", reasoning_content = Reasoning,
                        tool_calls = new[] { new { function = new { name = "lookup", arguments = "{" } } }
                    }
                }
            }
        }) + "\n\n";
        using (var handler = new ReplyHandler(truncatedTool))
        using (var http = new HttpClient(handler))
        {
            var result = Complete(new ModelProviderClient(http), Config(ModelProviderApiModes.OpenAiCompatible), true);
            Require(!result.Ok && result.Text == "Visible partial" && result.Reasoning == Reasoning
                && result.FailureKind == ModelCompletionFailureKind.InvalidResponse
                && result.StopReason == ModelCompletionStopReason.ProviderError && handler.Calls == 1,
                "Compatible tool-call fragments were mistaken for proof of terminal completion.");
        }

        foreach (var mode in Modes)
        {
            using var handler = new ReplyHandler(Body(mode, Token));
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), streaming: false);
            Require(result.ProviderStopReason.Length == 0,
                $"{mode} copied the active credential into terminal metadata.");
        }

        using var generated = JsonDocument.Parse(
            """{"output":[{"type":"message","content":{"status":"length","finish_reason":"max_tokens"}}]}""");
        Require(ModelCompletionOutcomeClassifier.ExtractStopReason(generated.RootElement) == ModelCompletionStopReason.Unknown
            && ModelCompletionOutcomeClassifier.ExtractProviderStopReason(generated.RootElement).Length == 0,
            "Generated message content was searched for provider terminal metadata.");
    }

    internal static void PreservesNativeReasoningEvidenceAndFailureBoundaries()
    {
        var terminal = """{"model_instance_id":"reasoning-fixture","output":[],"stats":{"input_tokens":7,"total_output_tokens":1024}}""";
        using (var handler = new ReplyHandler(NativeStream(terminal)))
        using (var http = new HttpClient(handler))
        {
            var result = Complete(new ModelProviderClient(http), Config(ModelProviderApiModes.LmStudioNative), true);
            Require(result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent
                && result.Reasoning == Reasoning && result.StopReason == ModelCompletionStopReason.Unknown,
                "A native terminal without repeated reasoning discarded its accepted private deltas.");
        }

        var cases = new (string Body, ModelCompletionFailureKind Kind)[]
        {
            ("data: {\"type\":\"reasoning.delta\",\"content\":\"" + Reasoning + "\"}\n\n",
                ModelCompletionFailureKind.InvalidResponse),
            ("data: {\"type\":\"reasoning.delta\",\"content\":\"" + Reasoning + "\"}\n\n"
                + "data: {\"type\":\"error\",\"error\":{\"message\":\"maximum context length exceeded\"}}\n\n"
                + "data: {\"type\":\"chat.end\",\"result\":" + terminal + "}\n\n",
                ModelCompletionFailureKind.ContextLimitExceeded)
        };
        foreach (var item in cases)
        {
            using var handler = new ReplyHandler(item.Body);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(ModelProviderApiModes.LmStudioNative), true);
            Require(!result.Ok && result.FailureKind == item.Kind
                && result.StopReason == ModelCompletionStopReason.ProviderError && result.Reasoning == Reasoning
                && handler.Calls == 1,
                "Accepted native error/truncation became a recoverable empty response or lost its reasoning.");
        }

        var failures = new (string Error, int? Status, ModelCompletionFailureKind Kind)[]
        {
            ("connection dropped", null, ModelCompletionFailureKind.Transport),
            ("caller cancelled", null, ModelCompletionFailureKind.Cancelled),
            ("maximum context length exceeded", 400, ModelCompletionFailureKind.ContextLimitExceeded),
            (ModelCompletionOutcomeClassifier.EmptyPublicContentError, 401, ModelCompletionFailureKind.ProviderRejected),
            ("request timed out", null, ModelCompletionFailureKind.Timeout)
        };
        foreach (var failure in failures)
        {
            var result = ModelCompletionOutcomeClassifier.Normalize(new ModelCompletionResult(
                false, "http://localhost", Model, "", Reasoning, 1, 0, 0, 0,
                failure.Error, DateTimeOffset.UtcNow, ProviderStatusCode: failure.Status));
            Require(result.FailureKind == failure.Kind && result.StopReason == ModelCompletionStopReason.ProviderError,
                "A transport, cancellation, context, authorization, or timeout failure became an empty-response recovery.");
        }
    }

    internal static void NeverTreatsPreviouslyStreamedPublicTextAsAnEmptyAnswer()
    {
        const string publicPartial = "Visible accepted answer";
        foreach (var terminalPublic in new[] { "", "Contradictory terminal answer" })
        {
            var stream = "data: " + JsonSerializer.Serialize(new { type = "reasoning.delta", content = Reasoning })
                + "\n\ndata: " + JsonSerializer.Serialize(new { type = "message.delta", content = publicPartial })
                + "\n\ndata: {\"type\":\"chat.end\",\"result\":"
                + Body(ModelProviderApiModes.LmStudioNative, "stop", terminalPublic) + "}\n\n";
            using var handler = new ReplyHandler(stream);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(ModelProviderApiModes.LmStudioNative), true);
            Require(!result.Ok && result.Text == publicPartial && result.Reasoning == Reasoning
                && result.FailureKind == ModelCompletionFailureKind.InvalidResponse
                && result.StopReason == ModelCompletionStopReason.ProviderError && result.ProviderStopReason == "stop"
                && handler.Calls == 1,
                "Missing or contradictory native terminal output erased accepted public text or permitted empty-answer recovery.");
        }

        foreach (var mode in new[] { ModelProviderApiModes.OpenAiCompatible, ModelProviderApiModes.OllamaNative })
        {
            var response = mode == ModelProviderApiModes.OllamaNative
                ? JsonSerializer.Serialize(new { message = new { content = publicPartial, thinking = Reasoning }, done = false }) + "\n"
                    + """{"message":{"content":""},"done":true,"done_reason":"stop"}""" + "\n"
                : "data: " + JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { delta = new { content = publicPartial, reasoning_content = Reasoning } } }
                    }) + "\n\ndata: " + """{"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\ndata: [DONE]\n\n";
            using var handler = new ReplyHandler(response);
            using var http = new HttpClient(handler);
            var result = Complete(new ModelProviderClient(http), Config(mode), true);
            Require(result.Ok && result.Text == publicPartial && result.Reasoning == Reasoning
                && result.FailureKind == ModelCompletionFailureKind.None && handler.Calls == 1,
                $"{mode} discarded its accumulated public output because the terminal frame had no additional text.");
        }
    }

    internal static void KeepsReportedStopOnAcceptedTransportFailure()
    {
        var prefix = "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = "Visible partial", reasoning_content = Reasoning } } }
        }) + "\n\ndata: " + """{"choices":[{"delta":{},"finish_reason":"length"}]}""" + "\n\n";
        using var handler = new ReplyHandler(prefix, failAtEof: true);
        using var http = new HttpClient(handler);
        var result = Complete(new ModelProviderClient(http), Config(ModelProviderApiModes.OpenAiCompatible), true);
        Require(!result.Ok && result.Text == "Visible partial" && result.Reasoning == Reasoning
            && result.StopReason == ModelCompletionStopReason.ProviderError && result.ProviderStopReason == "length"
            && result.FailureKind != ModelCompletionFailureKind.EmptyPublicContent && handler.Calls == 1,
            "Accepted transport failure discarded reported stop metadata, public partial text, or replayed generation.");
    }

    private static ModelCompletionResult Complete(ModelProviderClient client, ModelProviderConfig config, bool streaming) =>
        (streaming
            ? client.CompleteChatStreamingAsync(config, Messages, null)
            : client.CompleteChatAsync(config, Messages)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    private static ModelProviderConfig Config(string mode, string reasoning = "") => new()
    {
        BaseUrl = "http://127.0.0.1:45678/v1",
        ApiMode = mode,
        Reasoning = reasoning,
        Model = Model,
        ApiToken = Token,
        MaxOutputTokens = 1024,
        Timeout = 10
    };

    private static string Body(string mode, string stop, string publicText = "", bool toolOutput = false)
    {
        var tools = new[]
        {
            new { type = "function", function = new { name = "lookup", arguments = new { status = "length", finish_reason = "max_tokens" } } }
        };
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = publicText,
            ["reasoning_content"] = Reasoning,
            ["thinking"] = Reasoning
        };
        if (toolOutput) message["tool_calls"] = tools;

        if (mode == ModelProviderApiModes.LmStudioNative)
        {
            var output = new List<object> { new { type = "reasoning", content = Reasoning } };
            if (publicText.Length > 0) output.Add(new { type = "message", content = publicText });
            if (toolOutput) output.Add(new
            {
                type = "tool_call", tool = "lookup",
                arguments = new { status = "length", finish_reason = "max_tokens" }
            });
            return JsonSerializer.Serialize(new
            {
                model_instance_id = Model, output, finish_reason = stop.Length > 0 ? stop : null,
                stats = new { input_tokens = 7, total_output_tokens = 1024 }
            });
        }
        if (mode == ModelProviderApiModes.OllamaNative)
        {
            return JsonSerializer.Serialize(new
            {
                model = Model, message, done = true, done_reason = stop.Length > 0 ? stop : null,
                prompt_eval_count = 7, eval_count = 1024
            });
        }
        return JsonSerializer.Serialize(new
        {
            model = Model, choices = new[] { new { message, finish_reason = stop.Length > 0 ? stop : null } },
            usage = new { prompt_tokens = 7, completion_tokens = 1024, total_tokens = 1031 }
        });
    }

    private static string Stream(string mode, string response)
    {
        if (mode == ModelProviderApiModes.LmStudioNative) return NativeStream(response);
        if (mode == ModelProviderApiModes.OllamaNative) return response + "\n";
        using var parsed = JsonDocument.Parse(response);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");
        return "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = message } }
        }) + "\n\ndata: " + response + "\n\ndata: [DONE]\n\n";
    }

    private static string NativeStream(string response) =>
        "data: " + JsonSerializer.Serialize(new { type = "reasoning.delta", content = Reasoning })
        + "\n\ndata: {\"type\":\"chat.end\",\"result\":" + response + "}\n\n";

    private sealed class ReplyHandler(string response, bool failAtEof = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string RequestBody { get; private set; } = "";
        internal string Path { get; private set; } = "";
        internal string Authorization { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = failAtEof
                    ? new StreamContent(new FaultAtEofStream(response))
                    : new StringContent(response, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }

    private sealed class FaultAtEofStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position >= Length) throw new IOException("Accepted connection dropped after terminal metadata.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
