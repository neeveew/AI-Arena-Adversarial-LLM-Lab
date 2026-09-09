using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

internal static class ProviderRetryTests
{
    private static readonly ModelChatMessage[] Messages = [new("user", "retry safely")];
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    internal static void ParsesAndBoundsRetryAfterDelays()
    {
        Require(ResolveDelay("2") == TimeSpan.FromSeconds(2), "Retry-After delta seconds changed");
        Require(
            ResolveDelay(FixedUtcNow.AddSeconds(4).ToString("R", CultureInfo.InvariantCulture)) == TimeSpan.FromSeconds(4),
            "Retry-After HTTP date was not measured against the injected clock");
        Require(
            ResolveDelay(FixedUtcNow.AddSeconds(-1).ToString("R", CultureInfo.InvariantCulture)) == TimeSpan.Zero,
            "a past Retry-After date did not become an immediate bounded retry");
        Require(
            ResolveDelay("not-a-delay", jitter: 0.25) == TimeSpan.FromMilliseconds(175),
            "malformed Retry-After did not use deterministic bounded backoff and jitter");
        Require(ResolveDelay("5") == ModelProviderClient.MaximumRetryDelay,
            "a Retry-After delay exactly at policy was not admitted");
        Require(
            ResolveDelay("999999999999999999999999999999999999") is null,
            "an overflowing Retry-After delta was shortened into an unsafe early retry");
        Require(
            ResolveDelay(FixedUtcNow.AddDays(30).ToString("R", CultureInfo.InvariantCulture)) is null,
            "a huge Retry-After date was shortened into an unsafe early retry");
        Require(
            ResolveDelay("malformed", failedAttempt: 1, jitter: 1) == TimeSpan.FromMilliseconds(400),
            "fallback exponential backoff or deterministic jitter changed");

        var observedDelays = new List<TimeSpan>();
        var handler = new SequenceHandler((call, _, _) =>
        {
            var response = call == 1
                ? Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"unavailable\"}")
                : Response(HttpStatusCode.OK, SuccessBody(ModelProviderApiModes.LmStudioNative));
            if (call == 1)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", "2");
            }

            return Task.FromResult(response);
        });
        var result = CreateClient(
            handler,
            retryDelay: (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                observedDelays.Add(delay);
                return Task.CompletedTask;
            }).CompleteChatAsync(
                Config(ModelProviderApiModes.LmStudioNative, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
        Require(result.Ok && observedDelays.SequenceEqual([TimeSpan.FromSeconds(2)]),
            "physical retry did not route the response Retry-After value through the delay seam");

        var declinedDelays = new List<TimeSpan>();
        var declinedHandler = new SequenceHandler((_, _, _) =>
        {
            var response = Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"unavailable\"}");
            response.Headers.TryAddWithoutValidation("Retry-After", "6");
            return Task.FromResult(response);
        });
        var declined = CreateClient(
            declinedHandler,
            retryDelay: (delay, _) =>
            {
                declinedDelays.Add(delay);
                return Task.CompletedTask;
            }).CompleteChatAsync(
                Config(ModelProviderApiModes.LmStudioNative, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
        Require(!declined.Ok && declinedHandler.Calls == 1 && declinedDelays.Count == 0,
            "a Retry-After value above policy was replayed earlier than the provider permitted");
    }

    internal static void RetriesOnlyCapabilityAuthorizedAvailabilitySignals()
    {
        var modes = new[]
        {
            ModelProviderApiModes.OpenAiCompatible,
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OllamaNative,
            ModelProviderApiModes.LlamaCppNative
        };

        foreach (var mode in modes)
        {
            foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
            {
                var observer = new RecordingObserver();
                var handler = new SequenceHandler((call, _, _) => Task.FromResult(
                    call == 1
                        ? Response(status, "{\"error\":\"temporarily unavailable\"}")
                        : Response(HttpStatusCode.OK, SuccessBody(mode))));
                var client = CreateClient(handler, observer);
                var result = client.CompleteChatAsync(
                    Config(
                        mode,
                        supportsIdempotencyKey: true),
                    Messages).GetAwaiter().GetResult();

                Require(result.Ok, $"{mode} did not recover from standard {(int)status}: {result.Error}");
                AssertTwoAttemptReceipt(mode, handler, observer);
            }
        }

        // A remote 503 may be synthesized by a proxy after the upstream model
        // already accepted billable work. Without an explicit idempotency
        // contract, the client must surface the ambiguous failure once.
        {
            var acceptedWork = 0;
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((_, _, _) =>
            {
                Interlocked.Increment(ref acceptedWork);
                return Task.FromResult(Response(
                    HttpStatusCode.ServiceUnavailable,
                    "{\"error\":\"proxy synthesized 503 after acceptance\"}"));
            });
            var result = CreateClient(handler, observer).CompleteChatAsync(
                Config(
                    ModelProviderApiModes.OpenAiCompatible,
                    baseUrl: "https://remote-provider.example/v1"),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok
                    && handler.Calls == 1
                    && acceptedWork == 1
                    && handler.IdempotencyKeys.Single().Length == 0
                    && observer.Completions.Single().Outcome == "provider_error",
                "a remote compatible 503 without an idempotency contract was replayed");
        }

        foreach (var remoteNativeMode in new[]
        {
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OllamaNative,
            ModelProviderApiModes.LlamaCppNative
        })
        {
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.ServiceUnavailable,
                "{\"error\":\"model busy and queue full\"}")));
            var result = CreateClient(handler).CompleteChatAsync(
                Config(remoteNativeMode, baseUrl: "https://remote-native.example/v1"),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1,
                $"{remoteNativeMode} treated a remote native route as a safe local retry boundary");
        }

        foreach (var statefulOrCloudCapableLoopbackMode in new[]
        {
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OllamaNative,
            ModelProviderApiModes.LlamaCppNative
        })
        {
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.ServiceUnavailable,
                "{\"error\":\"temporarily unavailable\"}")));
            var result = CreateClient(handler).CompleteChatAsync(
                Config(statefulOrCloudCapableLoopbackMode),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1,
                $"{statefulOrCloudCapableLoopbackMode} treated loopback as proof of stateless or non-billable work");
        }

        {
            var handler = new SequenceHandler((_, _, _) =>
            {
                var response = Response(
                    HttpStatusCode.ServiceUnavailable,
                    "{\"error\":\"all slots busy; queue full\"}");
                response.RequestMessage = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://redirected-provider.example/v1/chat/completions");
                return Task.FromResult(response);
            });
            var result = CreateClient(handler).CompleteChatAsync(
                Config(ModelProviderApiModes.LlamaCppNative, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1,
                "a loopback llama.cpp request replayed after its effective response authority redirected remotely");
        }

        // With an explicitly configured end-to-end idempotency contract, every
        // physical attempt must reuse one key and exact payload. The fake
        // provider counts accepted logical work by unique key to prove that two
        // HTTP attempts still execute one billable completion.
        {
            var acceptedKeys = new HashSet<string>(StringComparer.Ordinal);
            var acceptedWork = 0;
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((call, request, _) =>
            {
                var key = request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : "";
                if (key.Length > 0 && acceptedKeys.Add(key))
                {
                    acceptedWork++;
                }

                return Task.FromResult(call == 1
                    ? Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"proxy synthesized 503\"}")
                    : Response(HttpStatusCode.OK, SuccessBody(ModelProviderApiModes.OpenAiCompatible)));
            });
            var result = CreateClient(handler, observer).CompleteChatAsync(
                Config(
                    ModelProviderApiModes.OpenAiCompatible,
                    baseUrl: "https://idempotent-provider.example/v1",
                    supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
            Require(result.Ok
                    && handler.Calls == 2
                    && acceptedWork == 1
                    && handler.IdempotencyKeys.Count == 2
                    && handler.IdempotencyKeys[0].Length > 0
                    && handler.IdempotencyKeys.Distinct(StringComparer.Ordinal).Count() == 1,
                "an explicitly idempotent remote retry did not reuse one key for one logical completion");
            Require(observer.Completions[0].PromptTokens.Explanation.Contains(
                    "idempotency_key",
                    StringComparison.Ordinal),
                "remote retry trace did not name its idempotency evidence");
            AssertTwoAttemptReceipt(ModelProviderApiModes.OpenAiCompatible, handler, observer);
        }

        {
            var handler = new SequenceHandler((call, _, _) => Task.FromResult(
                call % 2 == 1
                    ? Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"retry\"}")
                    : Response(HttpStatusCode.OK, SuccessBody(ModelProviderApiModes.OpenAiCompatible))));
            var client = CreateClient(handler);
            var config = Config(
                ModelProviderApiModes.OpenAiCompatible,
                baseUrl: "https://idempotent-provider.example/v1",
                supportsIdempotencyKey: true);
            Require(client.CompleteChatAsync(config, Messages).GetAwaiter().GetResult().Ok
                    && client.CompleteChatAsync(config, Messages).GetAwaiter().GetResult().Ok,
                "two independent idempotent completions did not finish");
            Require(handler.IdempotencyKeys.Count == 4
                    && handler.IdempotencyKeys[0] == handler.IdempotencyKeys[1]
                    && handler.IdempotencyKeys[2] == handler.IdempotencyKeys[3]
                    && !handler.IdempotencyKeys[0].Equals(handler.IdempotencyKeys[2], StringComparison.Ordinal),
                "idempotency keys were not stable within and unique across logical completions");
        }

        foreach (var mode in modes.Where(mode => mode != ModelProviderApiModes.LlamaCppNative))
        {
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, "{\"error\":\"model busy and loading\"}")));
            var client = CreateClient(handler);
            var result = client.CompleteChatAsync(Config(mode), Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1, $"{mode} incorrectly used llama.cpp-only busy-body retry predicates");
        }

        {
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.InternalServerError,
                "{\"error\":\"model busy and loading\",\"usage\":{\"completion_tokens\":2},\"choices\":[{}]}")));
            var result = CreateClient(handler).CompleteChatAsync(
                Config(ModelProviderApiModes.LlamaCppNative),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1,
                "llama.cpp replayed from a heuristic busy body without an idempotency contract");
        }

        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((call, _, _) => Task.FromResult(
                call == 1
                    ? Response(HttpStatusCode.InternalServerError, "{\"error\":\"model busy and loading\"}")
                    : Response(HttpStatusCode.OK, SuccessBody(ModelProviderApiModes.LlamaCppNative))));
            var client = CreateClient(handler, observer);
            var result = client.CompleteChatAsync(
                Config(ModelProviderApiModes.LlamaCppNative, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
            Require(result.Ok, $"idempotent llama.cpp busy/loading retry did not recover: {result.Error}");
            AssertTwoAttemptReceipt(ModelProviderApiModes.LlamaCppNative, handler, observer);
        }

        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(
                Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"still unavailable\"}")));
            var client = CreateClient(handler, observer);
            var result = client.CompleteChatAsync(
                Config(ModelProviderApiModes.OpenAiCompatible, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == ModelProviderClient.MaximumCompletionAttempts,
                "standard rejection did not stop at the physical-attempt bound");
            Require(observer.Requests.Select(item => item.Attempt).SequenceEqual([1, 2, 3]),
                "bounded retry trace attempt numbers were not truthful");
            Require(observer.Completions.Select(item => item.Outcome).SequenceEqual(
                    ["retryable_provider_failure", "retryable_provider_failure", "provider_error"]),
                "bounded retry traces did not terminate every physical attempt truthfully");
            Require(observer.Requests.Select(item => item.PayloadSha256).Distinct(StringComparer.Ordinal).Count() == 1,
                "bounded retry changed the exact payload hash");
        }
    }

    internal static void RetriesBufferedAndStreamingRejectionsWithoutDuplicateProgress()
    {
        var cases = new[]
        {
            (Mode: ModelProviderApiModes.OpenAiCompatible, Status: HttpStatusCode.TooManyRequests, Body: "{\"error\":\"rate limited\"}"),
            (Mode: ModelProviderApiModes.LmStudioNative, Status: HttpStatusCode.ServiceUnavailable, Body: "{\"error\":\"unavailable\"}"),
            (Mode: ModelProviderApiModes.OllamaNative, Status: HttpStatusCode.TooManyRequests, Body: "{\"error\":\"rate limited\"}"),
            (Mode: ModelProviderApiModes.LlamaCppNative, Status: HttpStatusCode.InternalServerError, Body: "{\"error\":\"queue full\"}")
        };

        foreach (var item in cases)
        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((call, _, _) => Task.FromResult(
                call == 1
                    ? Response(item.Status, item.Body)
                    : StreamingSuccessResponse(item.Mode)));
            var progress = new InlineProgress();
            var client = CreateClient(handler, observer);
            var result = client.CompleteChatStreamingAsync(
                Config(
                    item.Mode,
                    supportsIdempotencyKey: true),
                Messages,
                progress).GetAwaiter().GetResult();

            var adapterPath = item.Mode == ModelProviderApiModes.OllamaNative
                ? "NDJSON adapter"
                : "SSE adapter";
            Require(result.Ok, $"{item.Mode} {adapterPath} did not recover from a capability-authorized availability response: {result.Error}");
            AssertTwoAttemptReceipt(item.Mode, handler, observer);
            Require(progress.Values.Count == 1,
                $"{item.Mode} replay duplicated or lost accepted progress");
            Require(observer.Completions.Count == observer.Requests.Count,
                $"{item.Mode} did not emit exactly one completion per physical attempt");
        }
    }

    internal static void NeverReplaysAcceptedAmbiguousOrCancelledAttempts()
    {
        foreach (var mode in new[]
        {
            ModelProviderApiModes.OpenAiCompatible,
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OllamaNative,
            ModelProviderApiModes.LlamaCppNative
        })
        {
            var malformedObserver = new RecordingObserver();
            var malformedHandler = new SequenceHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{")));
            var malformed = CreateClient(malformedHandler, malformedObserver)
                .CompleteChatAsync(Config(mode), Messages).GetAwaiter().GetResult();
            Require(!malformed.Ok && malformedHandler.Calls == 1,
                $"{mode} replayed an accepted malformed JSON response");
            Require(malformedObserver.Completions.Single().Outcome == "provider_response_error",
                $"{mode} malformed accepted response received a misleading trace outcome");

            var emptyObserver = new RecordingObserver();
            var emptyHandler = new SequenceHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
            var empty = CreateClient(emptyHandler, emptyObserver)
                .CompleteChatAsync(Config(mode), Messages).GetAwaiter().GetResult();
            Require(!empty.Ok && emptyHandler.Calls == 1,
                $"{mode} replayed an accepted empty/schema-incomplete completion");
            Require(emptyObserver.Completions.Single().Outcome == "empty_response",
                $"{mode} empty accepted completion did not terminate its trace once");

            var ambiguousObserver = new RecordingObserver();
            var ambiguousHandler = new SequenceHandler((_, _, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("ambiguous send")));
            var ambiguous = CreateClient(ambiguousHandler, ambiguousObserver)
                .CompleteChatAsync(Config(mode), Messages).GetAwaiter().GetResult();
            Require(!ambiguous.Ok && ambiguousHandler.Calls == 1,
                $"{mode} replayed a transport-ambiguous request");
            Require(ambiguousObserver.Completions.Single().Outcome == "transport_error",
                $"{mode} transport ambiguity did not close its one trace truthfully");
        }

        {
            var observer = new RecordingObserver();
            var progress = new InlineProgress();
            var partialBody = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"once\"}}]}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultAfterBytesStream(partialBody))
            }));
            var result = CreateClient(handler, observer)
                .CompleteChatStreamingAsync(Config(ModelProviderApiModes.OpenAiCompatible), Messages, progress)
                .GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1, "a dropped accepted partial stream was replayed");
            Require(progress.Values.SequenceEqual(["once"]), "partial stream progress was duplicated or lost");
            Require(observer.Requests.Count == 1
                && observer.Completions.Count == 1
                && observer.Completions[0].Outcome == "transport_error",
                "partial-stream failure did not close exactly one physical trace");
        }

        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                "data: {not-json}\n\ndata: [DONE]\n\n",
                "text/event-stream")));
            var result = CreateClient(handler, observer)
                .CompleteChatStreamingAsync(Config(ModelProviderApiModes.OpenAiCompatible), Messages, new InlineProgress())
                .GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1, "an accepted malformed stream event was replayed");
            Require(observer.Completions.Single().Outcome == "provider_stream_error",
                "accepted malformed stream did not terminate once as a protocol error");
        }

        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                "data: {\"type\":\"message.delta\",\"content\":\"lm partial\"}\n\n",
                "text/event-stream")));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(!result.Ok && result.Text == "lm partial" && handler.Calls == 1,
                "LM Studio EOF without chat.end was not preserved as an incomplete non-replayed result");
            Require(observer.Completions.Single().Outcome == "provider_stream_incomplete",
                "LM Studio incomplete stream trace was reported as success");
        }

        {
            var observer = new RecordingObserver();
            var body = "data: {\"type\":\"message.delta\",\"content\":\"lm partial\"}\n\n"
                + "data: {\"type\":\"error\",\"error\":{\"message\":\"generation failed\"}}\n\n"
                + "data: {\"type\":\"chat.end\",\"result\":{\"model_instance_id\":\"retry-model\",\"output\":[{\"type\":\"message\",\"content\":\"lm partial\"}]}}\n\n";
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                body,
                "text/event-stream")));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(!result.Ok && result.Text == "lm partial" && handler.Calls == 1,
                "LM Studio error followed by chat.end suppressed the failure or replayed it");
            Require(observer.Completions.Single().Outcome == "provider_stream_error",
                "LM Studio error/chat.end trace was reported as success");
        }

        {
            var observer = new RecordingObserver();
            var body = "data: {\"type\":\"message.delta\",\"content\":\"lm partial\"}\n\n"
                + "data: {\"type\":\"error\"}\n\n"
                + "data: {\"type\":\"chat.end\",\"result\":{\"model_instance_id\":\"retry-model\",\"output\":[{\"type\":\"message\",\"content\":\"lm partial\"}]}}\n\n";
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                body,
                "text/event-stream")));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(!result.Ok
                    && result.Text == "lm partial"
                    && result.Error.Contains("error event", StringComparison.OrdinalIgnoreCase)
                    && handler.Calls == 1,
                "an empty LM Studio error event was erased by chat.end");
            Require(observer.Completions.Single().Outcome == "provider_stream_error",
                "an empty LM Studio error event received a success trace");
        }

        {
            var observer = new RecordingObserver();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"type\":\"message.delta\",\"content\":\"lm partial\"}\n\n"
                + "data: {\"type\":\"error\",\"error\":{\"message\":\"generation failed\"}}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultAfterBytesStream(bytes))
            }));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(!result.Ok
                    && result.Text == "lm partial"
                    && result.Error.Contains("generation failed", StringComparison.OrdinalIgnoreCase)
                    && handler.Calls == 1,
                "LM Studio error evidence was downgraded by a later stream read fault");
            Require(observer.Completions.Single().Outcome == "provider_stream_error",
                "LM Studio error followed by read fault received a transport trace");
        }

        {
            var observer = new RecordingObserver();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"type\":\"chat.end\",\"result\":{\"model_instance_id\":\"retry-model\",\"output\":[{\"type\":\"message\",\"content\":\"terminal\"}],\"stats\":{\"input_tokens\":3,\"total_output_tokens\":2}}}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultAfterBytesStream(bytes))
            }));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(result.Ok
                    && result.Text == "terminal"
                    && result.PromptTokens == 3
                    && result.CompletionTokens == 2
                    && handler.Calls == 1,
                "LM Studio did not stop reading after authoritative chat.end evidence");
            Require(observer.Completions.Single().Outcome == "succeeded",
                "authoritative LM Studio chat.end evidence was lost to trailing transport state");
        }

        {
            using var cancellation = new CancellationTokenSource();
            var observer = new RecordingObserver();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"type\":\"message.delta\",\"content\":\"lm partial\"}\n\n"
                + "data: {\"type\":\"error\",\"error\":{\"message\":\"generation failed\"}}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancelAfterBytesStream(bytes, cancellation))
            }));
            try
            {
                _ = CreateClient(handler, observer).CompleteChatStreamingAsync(
                    Config(ModelProviderApiModes.LmStudioNative),
                    Messages,
                    new InlineProgress(),
                    cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("LM Studio accepted-stream cancellation was swallowed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            Require(handler.Calls == 1
                    && observer.Completions.Single().Outcome == "provider_stream_error",
                "LM Studio error evidence was downgraded by later caller cancellation");
        }

        {
            var observer = new RecordingObserver();
            var body = "data: {\"choices\":[{\"delta\":{\"content\":\"openai partial\"}}]}\n\n"
                + "data: {\"error\":{\"message\":\"stream failed\",\"type\":\"server_error\"}}\n\n";
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(Response(
                HttpStatusCode.OK,
                body,
                "text/event-stream")));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.OpenAiCompatible),
                Messages,
                new InlineProgress()).GetAwaiter().GetResult();
            Require(!result.Ok && result.Text == "openai partial" && handler.Calls == 1,
                "a 200-SSE error envelope was ignored or replayed");
            Require(observer.Completions.Single().Outcome == "provider_stream_error",
                "a 200-SSE error envelope received a success trace");
        }

        foreach (var mode in new[]
        {
            ModelProviderApiModes.OpenAiCompatible,
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OllamaNative,
            ModelProviderApiModes.LlamaCppNative
        })
        {
            var observer = new RecordingObserver();
            var errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"unavailable");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StreamContent(new FaultAfterBytesStream(errorBytes))
            }));
            var result = CreateClient(handler, observer).CompleteChatAsync(
                Config(mode, supportsIdempotencyKey: true),
                Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1,
                $"{mode} replayed an ambiguous non-success response-body read fault");
            Require(observer.Completions.Single().Outcome == "transport_error",
                $"{mode} response-body read fault did not close one transport trace");
        }

        {
            var observer = new RecordingObserver();
            var progress = new InlineProgress();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"once\"}}]}\n\n"
                + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1,\"total_tokens\":4}}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultAfterBytesStream(bytes))
            }));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.OpenAiCompatible),
                Messages,
                progress).GetAwaiter().GetResult();
            var completion = observer.Completions.Single();
            Require(!result.Ok
                    && result.Text == "once"
                    && result.PromptTokens == 3
                    && result.CompletionTokens == 1
                    && result.TotalTokens == 4
                    && handler.Calls == 1,
                "accepted stream read fault lost partial text/counts or replayed work");
            Require(completion.Outcome == "transport_error"
                    && completion.PromptTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 3 }
                    && completion.CompletionTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 1 },
                "accepted stream read-fault trace discarded provider-reported usage");
        }

        {
            var observer = new RecordingObserver();
            var progress = new InlineProgress();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"type\":\"message.delta\",\"content\":\"lm once\"}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultAfterBytesStream(bytes))
            }));
            var result = CreateClient(handler, observer).CompleteChatStreamingAsync(
                Config(ModelProviderApiModes.LmStudioNative),
                Messages,
                progress).GetAwaiter().GetResult();
            Require(!result.Ok
                    && result.Text == "lm once"
                    && handler.Calls == 1
                    && progress.Values.SequenceEqual(["lm once"]),
                "accepted LM Studio stream read fault lost partial text or replayed work");
            Require(observer.Completions.Single().Outcome == "transport_error",
                "accepted LM Studio stream read fault did not close one transport trace");
        }

        {
            using var cancellation = new CancellationTokenSource();
            var observer = new RecordingObserver();
            var progress = new InlineProgress();
            var bytes = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"once\"}}]}\n\n"
                + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n");
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancelAfterBytesStream(bytes, cancellation))
            }));
            try
            {
                _ = CreateClient(handler, observer).CompleteChatStreamingAsync(
                    Config(ModelProviderApiModes.LlamaCppNative),
                    Messages,
                    progress,
                    cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("accepted stream cancellation was swallowed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            var completion = observer.Completions.Single();
            Require(handler.Calls == 1 && progress.Values.SequenceEqual(["once"]),
                "accepted stream cancellation duplicated or lost partial progress");
            Require(completion.Outcome == "caller_cancelled"
                    && completion.PromptTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 5 }
                    && completion.CompletionTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 2 },
                "accepted stream cancellation trace discarded observed provider usage");
        }

        {
            var observer = new RecordingObserver();
            var handler = new SequenceHandler(async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable timeout continuation");
            });
            var result = CreateClient(handler, observer)
                .CompleteChatAsync(Config(ModelProviderApiModes.OpenAiCompatible, timeout: 1), Messages).GetAwaiter().GetResult();
            Require(!result.Ok && handler.Calls == 1, "provider timeout was replayed");
            Require(observer.Completions.Single().Outcome == "provider_timeout",
                "provider timeout trace outcome changed");
        }

        {
            var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new RecordingObserver();
            var handler = new SequenceHandler((_, _, _) => Task.FromResult(
                Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"unavailable\"}")));
            using var cancellation = new CancellationTokenSource();
            var client = CreateClient(
                handler,
                observer,
                (_, token) =>
                {
                    delayStarted.TrySetResult();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            var completion = client.CompleteChatAsync(
                Config(ModelProviderApiModes.OpenAiCompatible, supportsIdempotencyKey: true),
                Messages,
                cancellation.Token);
            delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            cancellation.Cancel();
            try
            {
                completion.GetAwaiter().GetResult();
                throw new InvalidOperationException("caller cancellation during retry delay was swallowed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            Require(handler.Calls == 1, "cancellation during retry delay issued another physical request");
            Require(observer.Requests.Count == 1
                && observer.Completions.Count == 1
                && observer.Completions[0].Outcome == "retryable_provider_failure",
                "cancellation during delay duplicated or left the rejected attempt pending");
        }
    }

    private static TimeSpan? ResolveDelay(string retryAfter, int failedAttempt = 0, double jitter = 0)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Require(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter), "test Retry-After header was rejected");
        return ModelProviderClient.TryResolveRetryDelay(
            response.Headers,
            failedAttempt,
            new FixedTimeProvider(FixedUtcNow),
            _ => jitter,
            out var delay)
                ? delay
                : null;
    }

    private static ModelProviderClient CreateClient(
        SequenceHandler handler,
        RecordingObserver? observer = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null) =>
        new(
            new HttpClient(handler),
            observer,
            new FixedTimeProvider(FixedUtcNow),
            retryJitterUnit: _ => 0,
            retryDelayAsync: retryDelay ?? ((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }));

    private static ModelProviderConfig Config(
        string mode,
        int timeout = 30,
        string baseUrl = "http://127.0.0.1:45678/v1",
        bool supportsIdempotencyKey = false) => new()
    {
        BaseUrl = baseUrl,
        ApiMode = mode,
        Model = "retry-model",
        MaxOutputTokens = 64,
        Timeout = timeout,
        Extra = supportsIdempotencyKey
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ModelProviderClient.CompletionIdempotencyCapabilityKey] = JsonSerializer.SerializeToElement(true)
            }
            : null
    };

    private static string SuccessBody(string mode) => mode switch
    {
        ModelProviderApiModes.LmStudioNative =>
            "{\"model_instance_id\":\"retry-model\",\"output\":[{\"type\":\"message\",\"content\":\"ok\"}]}",
        ModelProviderApiModes.OllamaNative =>
            "{\"model\":\"retry-model\",\"message\":{\"content\":\"ok\"}}",
        _ =>
            "{\"model\":\"retry-model\",\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"
    };

    private static HttpResponseMessage StreamingSuccessResponse(string mode) => mode switch
    {
        ModelProviderApiModes.LmStudioNative => Response(
            HttpStatusCode.OK,
            "data: {\"type\":\"message.delta\",\"content\":\"streamed\"}\n\n"
                + "data: {\"type\":\"chat.end\",\"result\":{\"model_instance_id\":\"retry-model\",\"output\":[{\"type\":\"message\",\"content\":\"streamed\"}]}}\n\n",
            "text/event-stream"),
        ModelProviderApiModes.OllamaNative => Response(
            HttpStatusCode.OK,
            "{\"model\":\"retry-model\",\"message\":{\"content\":\"streamed\"},\"done\":false}\n"
                + "{\"model\":\"retry-model\",\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n",
            "application/x-ndjson"),
        _ => Response(
            HttpStatusCode.OK,
            "data: {\"model\":\"retry-model\",\"choices\":[{\"delta\":{\"content\":\"streamed\"}}]}\n\ndata: [DONE]\n\n",
            "text/event-stream")
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private static void AssertTwoAttemptReceipt(string mode, SequenceHandler handler, RecordingObserver observer)
    {
        Require(handler.Calls == 2, $"{mode} did not make exactly two physical attempts");
        Require(handler.Bodies.Count == 2 && handler.Bodies[0].AsSpan().SequenceEqual(handler.Bodies[1]),
            $"{mode} retry changed exact outbound payload bytes");
        Require(handler.IdempotencyKeys.Count == 2,
            $"{mode} did not record one idempotency decision per physical attempt");
        Require(handler.IdempotencyKeys[0].Length > 0
                && handler.IdempotencyKeys[0].Equals(handler.IdempotencyKeys[1], StringComparison.Ordinal),
            $"{mode} retry did not reuse one exact idempotency key");
        Require(observer.Requests.Select(item => item.Attempt).SequenceEqual([1, 2]),
            $"{mode} trace attempt numbers changed");
        Require(observer.Requests.Select(item => item.PayloadSha256).Distinct(StringComparer.Ordinal).Count() == 1,
            $"{mode} retry payload hashes changed");
        Require(observer.Completions.Select(item => item.Outcome).SequenceEqual(["retryable_provider_failure", "succeeded"]),
            $"{mode} physical attempts did not each receive a truthful terminal trace");
        Require(observer.Completions[0].PromptTokens.Explanation.Contains(
                "idempotency_key",
                StringComparison.Ordinal),
            $"{mode} retry trace did not name its admission evidence");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class InlineProgress : IProgress<string>
    {
        internal List<string> Values { get; } = [];

        public void Report(string value) => Values.Add(value);
    }

    private sealed class RecordingObserver : IPreparedProviderRequestObserver
    {
        internal List<ProviderRequestTrace> Requests { get; } = [];

        internal List<ProviderRequestCompletionObservation> Completions { get; } = [];

        public ProviderRequestObservationDetail ObservationDetail => ProviderRequestObservationDetail.MetadataOnly;

        public void ObservePreparedRequest(ProviderRequestTrace trace) => Requests.Add(trace);

        public void ObserveRequest(ProviderRequestTrace trace) => Requests.Add(trace);

        public void ObserveCompletion(ProviderRequestCompletionObservation completion) => Completions.Add(completion);
    }

    private sealed class SequenceHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal List<byte[]> Bodies { get; } = [];

        internal List<string> IdempotencyKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : "");
            var call = Interlocked.Increment(ref _calls);
            return await responseFactory(call, request, cancellationToken);
        }
    }

    private sealed class FaultAfterBytesStream(byte[] bytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= bytes.Length)
            {
                throw new IOException("accepted stream dropped");
            }

            var copied = Math.Min(count, bytes.Length - _offset);
            bytes.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset, copied));
            _offset += copied;
            return copied;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= bytes.Length)
            {
                throw new IOException("accepted stream dropped");
            }

            var copied = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelAfterBytesStream(
        byte[] bytes,
        CancellationTokenSource cancellation) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= bytes.Length)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            var copied = Math.Min(count, bytes.Length - _offset);
            bytes.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset, copied));
            _offset += copied;
            return copied;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= bytes.Length)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            var copied = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, copied).CopyTo(buffer);
            _offset += copied;
            return copied;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
