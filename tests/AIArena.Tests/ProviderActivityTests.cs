using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

internal static class ProviderActivityTests
{
    private const string PrivatePrompt = "private prompt marker 8374";
    private const string PrivateReasoning = "private reasoning marker 6291";
    private const string ReasoningTail = " plus another thought";
    private const string PublicText = "Visible answer";
    private const string ApiToken = "activity-fixture-token-7842";
    private static readonly ModelChatMessage[] Messages = [new("user", PrivatePrompt)];
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(5);

    internal static void ReportsNativeStagesBeforeCompletionWithoutContentExposure() =>
        VerifyNativeActivityAsync().GetAwaiter().GetResult();

    private static async Task VerifyNativeActivityAsync()
    {
        var prefix = NativeEvent("model_load.start")
            + NativeEvent("model_load.progress", progress: 0.25)
            + NativeEvent("model_load.progress")
            + NativeEvent("model_load.progress", progress: -4)
            + NativeEvent("model_load.progress", progress: 4)
            + NativeEvent("model_load.end")
            + NativeEvent("prompt_processing.start")
            + NativeEvent("prompt_processing.progress", progress: 0.5)
            + NativeEvent("prompt_processing.end")
            + NativeEvent("reasoning.start")
            + NativeEvent("reasoning.delta", PrivateReasoning)
            + NativeEvent("reasoning.delta", ReasoningTail)
            + NativeEvent("reasoning.end");
        var suffix = NativeEvent("message.delta", "Visible")
            + NativeEvent("message.delta", " answer")
            + NativeTerminal(PublicText, PrivateReasoning + ReasoningTail);
        using var stream = new GatedResponseStream(prefix, suffix);
        using var handler = new ResponseHandler(() => new StreamContent(stream));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TestBound);
        var activity = new RecordingProgress<ModelProviderActivity>();
        var text = new RecordingProgress<string>();
        var startedAt = DateTimeOffset.UtcNow;
        IActivityStreamingModelProviderClient client = new ModelProviderClient(http);
        var pending = client.CompleteChatStreamingWithActivityAsync(
            Config(ModelProviderApiModes.LmStudioNative), Messages, text, activity, cancellation.Token);
        try
        {
            await stream.WaitingForSuffix.Task.WaitAsync(TestBound);
            var early = activity.Values.ToArray();
            Require(!pending.IsCompleted && text.Values.IsEmpty,
                "LM Studio buffered activity until public output or completed before terminal bytes were available");
            Require(early.Length > 0, "LM Studio emitted no activity while loading, reading context, or reasoning");
            var loading = early.Where(value => value.Stage == ModelProviderActivityStage.Loading).ToArray();
            var reading = early.Where(value => value.Stage == ModelProviderActivityStage.ReadingContext).ToArray();
            Require(loading.Length > 0 && loading.First().Progress == 0 && loading.Last().Progress == 1
                    && loading.Any(value => value.Progress == 0.25) && loading.Any(value => value.Progress is null),
                "LM Studio loading activity lost start, progress, unknown-progress, or completion evidence");
            Require(reading.Select(value => value.Progress).SequenceEqual(new double?[] { 0, 0.5, 1 }),
                "LM Studio prompt processing did not report ReadingContext progress");
            Require(loading.Concat(reading).All(value => value.PublicCharacters == 0 && value.ReasoningCharacters == 0),
                "LM Studio loading or context processing counted prompt material as generated output");
            Require(early.Last() is { Stage: ModelProviderActivityStage.Thinking, PublicCharacters: 0 }
                    && early.Last().ReasoningCharacters == PrivateReasoning.Length + ReasoningTail.Length,
                "LM Studio did not expose cumulative reasoning activity before public output");
            AssertMetadataAndPrivacy(early, startedAt, DateTimeOffset.UtcNow);

            stream.Release();
            var result = await pending.WaitAsync(TestBound);
            Require(result.Ok && result.Text == PublicText && result.Reasoning == PrivateReasoning + ReasoningTail,
                $"LM Studio activity changed its completed content: {result.Error}");
            Require(text.Values.SequenceEqual(["Visible", " answer"]) && handler.Calls == 1,
                "LM Studio activity leaked reasoning to public callbacks or replayed accepted work");
            var all = activity.Values.ToArray();
            Require(all.Last().Stage == ModelProviderActivityStage.Writing
                    && all.Last().PublicCharacters == PublicText.Length
                    && all.Last().ReasoningCharacters == PrivateReasoning.Length + ReasoningTail.Length,
                "LM Studio final activity did not preserve cumulative counts or end in Writing");
            AssertMetadataAndPrivacy(all, startedAt, DateTimeOffset.UtcNow);
        }
        finally
        {
            cancellation.Cancel();
            stream.Release();
        }
    }

    internal static void MapsReasoningAliasesToThinkingAndMixedDeltasToWriting()
    {
        foreach (var mode in new[] { ModelProviderApiModes.OpenAiCompatible, ModelProviderApiModes.OllamaNative })
        {
            foreach (var alias in new[] { "reasoning_content", "reasoning", "thinking" })
            {
                var body = CompletionBody(mode, alias);
                using var handler = new ResponseHandler(() => new StringContent(body, Encoding.UTF8));
                using var http = new HttpClient(handler);
                var activity = new RecordingProgress<ModelProviderActivity>();
                var text = new RecordingProgress<string>();
                var startedAt = DateTimeOffset.UtcNow;
                IActivityStreamingModelProviderClient client = new ModelProviderClient(http);
                var result = client.CompleteChatStreamingWithActivityAsync(
                    Config(mode), Messages, text, activity).WaitAsync(TestBound).GetAwaiter().GetResult();
                Require(result.Ok && result.Text == PublicText && result.Reasoning == PrivateReasoning + ReasoningTail,
                    $"{mode} activity changed content for the {alias} alias: {result.Error}");
                Require(text.Values.SequenceEqual([PublicText]) && handler.Calls == 1,
                    $"{mode} {alias} leaked private reasoning to public callbacks or replayed generation");
                var events = activity.Values.ToArray();
                Require(events.Select(value => value.Stage).SequenceEqual(new[]
                    {
                        ModelProviderActivityStage.Thinking,
                        ModelProviderActivityStage.Thinking,
                        ModelProviderActivityStage.Writing
                    }),
                    $"{mode} {alias} did not transition from private thinking to writing for a mixed delta");
                Require(events[0].PublicCharacters == 0 && events[0].ReasoningCharacters == PrivateReasoning.Length
                        && events[1].PublicCharacters == 0
                        && events[1].ReasoningCharacters == PrivateReasoning.Length + ReasoningTail.Length
                        && events[2].PublicCharacters == PublicText.Length
                        && events[2].ReasoningCharacters == PrivateReasoning.Length + ReasoningTail.Length,
                    $"{mode} {alias} activity did not accumulate private and public counts separately");
                AssertMetadataAndPrivacy(events, startedAt, DateTimeOffset.UtcNow);
            }
        }
    }

    internal static void IsolatesThrowingActivityObserversFromGeneration()
    {
        foreach (var mode in new[]
        {
            ModelProviderApiModes.LmStudioNative,
            ModelProviderApiModes.OpenAiCompatible,
            ModelProviderApiModes.OllamaNative
        })
        {
            using var handler = new ResponseHandler(() => new StringContent(CompletionBody(mode, "thinking"), Encoding.UTF8));
            using var http = new HttpClient(handler);
            var observer = new ThrowingActivityObserver();
            var text = new RecordingProgress<string>();
            IActivityStreamingModelProviderClient client = new ModelProviderClient(http);
            var result = client.CompleteChatStreamingWithActivityAsync(
                Config(mode), Messages, text, observer).WaitAsync(TestBound).GetAwaiter().GetResult();
            Require(observer.Calls > 0 && result.Ok && result.Text == PublicText
                    && result.Reasoning == PrivateReasoning + ReasoningTail
                    && text.Values.SequenceEqual([PublicText]) && handler.Calls == 1,
                $"{mode} let an optional activity observer fail, change, or replay accepted generation: {result.Error}");
        }
    }

    private static void AssertMetadataAndPrivacy(ModelProviderActivity[] events, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        Require(events.All(value => value.ObservedAtUtc.Offset == TimeSpan.Zero
                    && value.ObservedAtUtc >= startedAt && value.ObservedAtUtc <= endedAt
                    && value.ElapsedMilliseconds >= 0 && value.PublicCharacters >= 0 && value.ReasoningCharacters >= 0
                    && (value.Progress is null || double.IsFinite(value.Progress.Value) && value.Progress is >= 0 and <= 1)),
            "Provider activity contained invalid UTC time, elapsed time, cumulative counts, or progress");
        for (var index = 1; index < events.Length; index++)
        {
            Require(events[index].ElapsedMilliseconds >= events[index - 1].ElapsedMilliseconds
                    && events[index].PublicCharacters >= events[index - 1].PublicCharacters
                    && events[index].ReasoningCharacters >= events[index - 1].ReasoningCharacters,
                "Provider activity elapsed time or cumulative counts moved backward");
        }
        Require(events.Where(value => value.Stage is ModelProviderActivityStage.Thinking or ModelProviderActivityStage.Writing)
                .All(value => value.Progress is null),
            "Provider activity invented percentage progress for generated reasoning or public text");
        var serialized = JsonSerializer.Serialize(events);
        Require(new[] { PrivatePrompt, PrivateReasoning, ReasoningTail, PublicText, ApiToken }
                .All(secret => !serialized.Contains(secret, StringComparison.Ordinal)),
            "Provider activity carried prompt, reasoning, public content, or credentials instead of metadata");
    }

    private static string CompletionBody(string mode, string reasoningAlias)
    {
        if (mode == ModelProviderApiModes.LmStudioNative)
        {
            return NativeEvent("model_load.start")
                + NativeEvent("reasoning.delta", PrivateReasoning)
                + NativeEvent("reasoning.delta", ReasoningTail)
                + NativeEvent("message.delta", PublicText)
                + NativeTerminal(PublicText, PrivateReasoning + ReasoningTail);
        }
        var reasoningOnly = new Dictionary<string, string> { [reasoningAlias] = PrivateReasoning };
        var mixed = new Dictionary<string, string> { [reasoningAlias] = ReasoningTail, ["content"] = PublicText };
        return mode == ModelProviderApiModes.OllamaNative
            ? JsonSerializer.Serialize(new { message = reasoningOnly, done = false }) + "\n"
                + JsonSerializer.Serialize(new { message = mixed, done = false }) + "\n"
                + "{\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":4,\"eval_count\":2}\n"
            : Sse(new { choices = new[] { new { delta = reasoningOnly } } })
                + Sse(new { choices = new[] { new { delta = mixed } } })
                + "data: [DONE]\n\n";
    }

    private static string NativeEvent(string type, string? content = null, double? progress = null)
    {
        var value = new Dictionary<string, object> { ["type"] = type };
        if (content is not null) value["content"] = content;
        if (progress is not null) value["progress"] = progress.Value;
        return Sse(value);
    }

    private static string NativeTerminal(string text, string reasoning) => Sse(new
    {
        type = "chat.end",
        result = new
        {
            model_instance_id = "activity-model",
            output = new[] { new { type = "reasoning", content = reasoning }, new { type = "message", content = text } },
            stats = new { input_tokens = 4, total_output_tokens = 2 }
        }
    });

    private static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static ModelProviderConfig Config(string mode) => new()
    {
        BaseUrl = "http://127.0.0.1:45678/v1",
        ApiMode = mode,
        Model = "activity-model",
        ApiToken = ApiToken,
        Timeout = 30,
        Extra = new Dictionary<string, JsonElement>
        {
            [ModelProviderClient.CompletionIdempotencyCapabilityKey] = JsonSerializer.SerializeToElement(true)
        }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        internal ConcurrentQueue<T> Values { get; } = new();
        public void Report(T value) => Values.Enqueue(value);
    }

    private sealed class ThrowingActivityObserver : IProgress<ModelProviderActivity>
    {
        internal int Calls { get; private set; }
        public void Report(ModelProviderActivity value)
        {
            Calls++;
            throw new InvalidOperationException("Optional activity observer failed");
        }
    }

    private sealed class ResponseHandler(Func<HttpContent> contentFactory) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = contentFactory() });
        }
    }

    private sealed class GatedResponseStream(string prefix, string suffix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        private readonly MemoryStream _suffix = new(Encoding.UTF8.GetBytes(suffix));
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WaitingForSuffix { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (_prefix.Position < _prefix.Length) return _prefix.Read(buffer.Span);
            WaitingForSuffix.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return _suffix.Read(buffer.Span);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
                _prefix.Dispose();
                _suffix.Dispose();
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
