using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

internal static class NativeMessageBoundaryTests
{
    // Native output contains distinct message items, unlike compatible content
    // parts within one message. message.start/end delimit their streamed form:
    // https://lmstudio.ai/docs/developer/rest/chat
    // https://lmstudio.ai/docs/developer/rest/streaming-events
    internal static void PreservesNativeMessageBoundaries()
    {
        string[][][] examples =
        [
            [["A"], ["B"]],
            [[" \t", "first\n", "    "], [" second", "\r\n "]],
            [[""], [" \t"], [], ["A"], ["\r\n"], ["B"], []],
            [[], [""]],
            [[" \t"], ["\r\n"]]
        ];
        foreach (var messages in examples)
        foreach (var boundaries in new[] { "both", "no-first-start", "start-only", "end-only" })
        {
            var expected = Flatten(messages);
            var terminal = Terminal(messages.Select(parts => string.Concat(parts)).ToArray());
            using var handler = new NativeReplyHandler(Prefix(messages, boundaries) + Event(new { type = "chat.end", result = terminal }));
            using var http = new HttpClient(handler);
            var progress = new RecordingProgress<string>();
            var activity = new RecordingProgress<ModelProviderActivity>();
            var result = new ModelProviderClient(http).CompleteChatStreamingWithActivityAsync(
                Config(), Messages, progress, activity).GetAwaiter().GetResult();
            Require(result.Ok == !string.IsNullOrWhiteSpace(expected) && result.Text == expected,
                $"Native message boundaries ({boundaries}) changed public text or rejected a valid terminal: expected {JsonSerializer.Serialize(expected)}, got {JsonSerializer.Serialize(result.Text)}, error {result.Error}");
            Require(result.Ok || result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent,
                "Empty/whitespace-only native message blocks must remain empty-answer failures.");
            Require(string.Concat(progress.Values) == expected
                    && progress.Values.Count == messages.Sum(parts => parts.Count(part => part.Length > 0)),
                "Native message whitespace must reach progress without buffering, trimming, or phantom empty-block separators.");
            Require(activity.Values.Last().PublicCharacters == expected.Length,
                "Native public activity count must match the text delivered to progress.");
            Require(result.Reasoning == "hidden reasoning" && result.PromptTokens == 7 && result.CompletionTokens == 9
                    && result.TotalTokens == 16 && result.ResponseId == "resp_boundaries"
                    && handler.Calls == 1 && handler.RequestPath == "/api/v1/chat",
                "Message boundaries changed native usage, reasoning privacy, response identity, or physical request count.");
            using var bufferedHandler = new NativeReplyHandler(JsonSerializer.Serialize(terminal));
            using var bufferedHttp = new HttpClient(bufferedHandler);
            var buffered = new ModelProviderClient(bufferedHttp).CompleteChatAsync(Config(), Messages).GetAwaiter().GetResult();
            Require(buffered.Text == expected && buffered.Ok == result.Ok && buffered.FailureKind == result.FailureKind,
                "Buffered and streaming native message flattening must agree, including whitespace-only public blocks.");
        }

        // Existing servers/fixtures can omit all boundaries for a single message.
        string[][] implicitMessage = [[" \t", "implicit", " answer\r\n"]];
        var implicitTerminal = Terminal(implicitMessage.Select(parts => string.Concat(parts)).ToArray());
        using (var handler = new NativeReplyHandler(Prefix(implicitMessage, "none") + Event(new { type = "chat.end", result = implicitTerminal })))
        using (var http = new HttpClient(handler))
        {
            var progress = new RecordingProgress<string>();
            var result = new ModelProviderClient(http).CompleteChatStreamingAsync(Config(), Messages, progress).GetAwaiter().GetResult();
            Require(result.Ok && result.Text == string.Concat(implicitMessage[0]) && progress.Values.SequenceEqual(implicitMessage[0]),
                "Deltas without any message.start/end must retain single-message compatibility.");
        }

        string[][] partialMessages = [["first", " \t"], [" \r\n", "second", " "]];
        var expectedPartial = Flatten(partialMessages);
        foreach (var failure in new[] { "eof", "io", "error", "malformed", "mismatch", "collapsed-boundary" })
        {
            var terminal = Terminal(failure == "collapsed-boundary"
                ? [string.Concat(partialMessages.SelectMany(parts => parts))]
                : ["different terminal text"]);
            var prefix = Prefix(partialMessages, "both");
            var suffix = failure switch
            {
                "error" => Event(new { type = "error", error = new { message = "native failure" } }),
                "malformed" => "data: {broken-json}\n\n",
                _ => ""
            };
            if (failure is not ("eof" or "io")) suffix += Event(new { type = "chat.end", result = terminal });
            using var handler = new NativeReplyHandler(prefix + suffix, failAtEof: failure == "io");
            using var http = new HttpClient(handler);
            var progress = new RecordingProgress<string>();
            var result = new ModelProviderClient(http).CompleteChatStreamingAsync(Config(), Messages, progress).GetAwaiter().GetResult();
            Require(!result.Ok && result.Text == expectedPartial && string.Concat(progress.Values) == expectedPartial && handler.Calls == 1,
                $"Native multi-message {failure} erased accepted partial output or replayed the request.");
            Require(result.FailureKind != ModelCompletionFailureKind.EmptyPublicContent,
                "Accepted native multi-message failure must never become an empty-answer retry candidate.");
            if (failure is "mismatch" or "collapsed-boundary")
                Require(result.FailureKind == ModelCompletionFailureKind.InvalidResponse
                        && result.StopReason == ModelCompletionStopReason.ProviderError,
                    "True terminal message mismatches must remain protocol failures.");
            if (failure is not ("eof" or "io"))
                Require(result.PromptTokens == 7 && result.CompletionTokens == 9 && result.ResponseId == "resp_boundaries",
                    "A failed native stream must retain terminal token and response evidence.");
        }

        // With no public deltas, retain the terminal-only fallback, including
        // the same between-message separator, instead of inventing progress.
        using (var handler = new NativeReplyHandler(Event(new { type = "chat.end", result = Terminal(["A", "B"]) })))
        using (var http = new HttpClient(handler))
        {
            var progress = new RecordingProgress<string>();
            var result = new ModelProviderClient(http).CompleteChatStreamingAsync(Config(), Messages, progress).GetAwaiter().GetResult();
            Require(result.Ok && result.Text == "A" + Environment.NewLine + "B" && progress.Values.Count == 0 && handler.Calls == 1,
                "Native terminal-only fallback changed when no public messages were streamed.");
        }
    }

    private static readonly ModelChatMessage[] Messages = [new("user", "Return the result.")];
    private static ModelProviderConfig Config() => new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
        Model = "boundary-model", Timeout = 5,
        Extra = new Dictionary<string, JsonElement> { [ModelProviderClient.CompletionIdempotencyCapabilityKey] = JsonSerializer.SerializeToElement(true) }
    };

    private static string Flatten(string[][] messages) =>
        string.Join(Environment.NewLine, messages.Select(parts => string.Concat(parts)).Where(text => text.Length > 0));

    private static string Prefix(string[][] messages, string boundaries)
    {
        var providerInfo = new { type = "plugin", plugin_id = "fixture/plugin" };
        var body = new StringBuilder(Event(new { type = "chat.start", model_instance_id = "boundary-model" }));
        for (var i = 0; i < messages.Length; i++)
        {
            if (boundaries is "both" or "start-only" || boundaries == "no-first-start" && i > 0)
                body.Append(Event(new { type = "message.start" }));
            foreach (var delta in messages[i]) body.Append(Event(new { type = "message.delta", content = delta }));
            if (boundaries is "both" or "end-only" or "no-first-start") body.Append(Event(new { type = "message.end" }));
            if (i != 0) continue;
            body.Append(Event(new { type = "tool_call.start", tool = "test_tool", provider_info = providerInfo }));
            body.Append(Event(new { type = "tool_call.success", tool = "test_tool", arguments = new { }, output = "private tool output", provider_info = providerInfo }));
            body.Append(Event(new { type = "reasoning.start" }));
            body.Append(Event(new { type = "reasoning.delta", content = "hidden reasoning" }));
            body.Append(Event(new { type = "reasoning.end" }));
        }
        return body.ToString();
    }

    private static object Terminal(string[] messages)
    {
        var output = new List<object>();
        for (var i = 0; i < messages.Length; i++)
        {
            output.Add(new { type = "message", content = messages[i] });
            if (i != 0) continue;
            output.Add(new { type = "tool_call", tool = "test_tool", arguments = new { }, output = "private tool output",
                provider_info = new { type = "plugin", plugin_id = "fixture/plugin" } });
            output.Add(new { type = "reasoning", content = "hidden reasoning" });
            output.Add(new { type = "reasoning", content = " \t" });
        }
        return new { model_instance_id = "boundary-model", response_id = "resp_boundaries", output,
            stats = new { input_tokens = 7, total_output_tokens = 9 }, stop_reason = "stop" };
    }

    private static string Event(object data) => "data: " + JsonSerializer.Serialize(data) + "\n\n";

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        internal List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class NativeReplyHandler(string body, bool failAtEof = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = failAtEof ? new StreamContent(new FaultAtEofStream(body)) : new StringContent(body, Encoding.UTF8),
                RequestMessage = request
            });
        }
    }

    private sealed class FaultAtEofStream(string body) : MemoryStream(Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= Length) throw new IOException("Fixture connection dropped after public messages.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
