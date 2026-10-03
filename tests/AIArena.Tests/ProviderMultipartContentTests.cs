using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

internal static class ProviderMultipartContentTests
{
    // Mistral documents list content for compatible assistant messages and SSE deltas:
    // https://docs.mistral.ai/studio/conversations/chat-completion
    // https://github.com/mistralai/client-python/blob/main/src/mistralai/client/models/deltamessage.py
    internal static void PreservesCompatibleTextPartsAcrossTransports()
    {
        string[][] examples =
        [
            [" \t", "if ready:\n", "    ", "return answer", "\r\n "],
            [" ", "\t", "\r\n"]
        ];
        foreach (var parts in examples)
        foreach (var streaming in new[] { false, true })
        {
            var expected = string.Concat(parts);
            var chunks = parts.Select(text => new { type = "text", text }).Cast<object>().ToArray();
            var hidden = new { type = "thinking", thinking = new[] { new { type = "text", text = "private reasoning" } } };
            var body = JsonSerializer.Serialize(new
            {
                id = "multipart-fixture", model = "multipart-model",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = chunks.Prepend(hidden) }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 3, completion_tokens = 7, total_tokens = 10 }
            });
            if (streaming)
            {
                var events = new StringBuilder();
                // Deliberately group the first two parts in one delta. Parts inside
                // an event and boundaries between events must have identical semantics.
                foreach (var group in chunks.Chunk(2))
                {
                    events.Append("data: ").Append(JsonSerializer.Serialize(new
                    {
                        model = "multipart-model",
                        choices = new[] { new { index = 0, delta = new { content = group.Prepend(hidden) } } }
                    })).Append("\n\n");
                }
                events.Append("data: ").Append(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 3, completion_tokens = 7, total_tokens = 10 }
                })).Append("\n\ndata: [DONE]\n\n");
                body = events.ToString();
            }
            using var handler = new MultipartReplyHandler(body, streaming);
            using var http = new HttpClient(handler);
            var provider = new ModelProviderClient(http);
            var config = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:45678/v1", Model = "multipart-model",
                ApiMode = ModelProviderApiModes.OpenAiCompatible, Timeout = 5, MaxOutputTokens = 128
            };
            var progress = new MultipartProgress();
            var messages = new[] { new ModelChatMessage("user", "Return the code unchanged.") };
            var result = (streaming
                ? provider.CompleteChatStreamingAsync(config, messages, progress)
                : provider.CompleteChatAsync(config, messages)).GetAwaiter().GetResult();
            Require(result.Text == expected,
                $"Compatible {(streaming ? "streamed" : "buffered")} content parts lost indentation/whitespace or inserted separators: {JsonSerializer.Serialize(result.Text)}");
            Require(result.Ok == !string.IsNullOrWhiteSpace(expected)
                    && (result.Ok || result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent),
                "Multipart output must use the same success/blank-content classification as plain text");
            Require(result.PromptTokens == 3 && result.CompletionTokens == 7 && result.TotalTokens == 10
                    && handler.Calls == 1 && handler.RequestPath == "/v1/chat/completions",
                "Multipart extraction must retain provider usage and the single compatible request");
            if (streaming)
            {
                Require(progress.Text.ToString() == expected && progress.Calls == (parts.Length + 1) / 2,
                    "Every public multipart delta, including whitespace-only deltas, must reach progress exactly once");
            }
        }
    }

    private sealed class MultipartProgress : IProgress<string>
    {
        internal StringBuilder Text { get; } = new();
        internal int Calls { get; private set; }
        public void Report(string value) { Calls++; Text.Append(value); }
    }

    private sealed class MultipartReplyHandler(string body, bool streaming) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
