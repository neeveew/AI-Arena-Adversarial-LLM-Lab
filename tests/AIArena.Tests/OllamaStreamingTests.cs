using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

internal static class OllamaStreamingTests
{
    private const string ApiToken = "ollama-fixture-secret-68142";
    private const string PartialFrames =
        "{\"model\":\"canonical:latest\",\"message\":{\"thinking\":\"Plan carefully\"},\"done\":false}\n"
        + "{\"model\":\"canonical:latest\",\"message\":{\"content\":\"partial\"},\"done\":false}\n";
    private static readonly ModelChatMessage[] Messages = [new("user", "Show incremental output")];
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(5);

    internal static void StreamsIncrementallyAndKeepsTerminalEvidence() =>
        VerifyIncrementalStreamAsync().GetAwaiter().GetResult();

    private static async Task VerifyIncrementalStreamAsync()
    {
        var prefix = "\r\n{\"model\":\"canonical:latest\",\"message\":{\"thinking\":\"Plan carefully\"},\"done\":false}\r\n"
            + "{\"model\":\"canonical:latest\",\"message\":{\"content\":\"Hello\"},\"done\":false}\n"
            + "{\"message\":{\"content\":\" \"},\"done\":false}\n"
            + "{\"message\":{\"content\":\"世界\"},\"done\":false}\n";
        var terminal = "{\"model\":\"canonical:latest\",\"message\":{\"content\":\"!\",\"thinking\":\" now\"},"
            + "\"done\":true,\"done_reason\":\"length\",\"prompt_eval_count\":5,\"eval_count\":3,"
            + "\"eval_duration\":2000000000,\"load_duration\":12000000,\"response_id\":\"ollama-response\"}\n";
        using var stream = new ScriptedStream(prefix, terminal, new IOException("must not read past done"), gated: true);
        using var handler = new StreamHandler(stream);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TestBound);
        var observer = new RecordingObserver();
        var progress = new RecordingProgress();
        var pending = new ModelProviderClient(http, observer).CompleteChatStreamingAsync(
            Config(), Messages, progress, cancellation.Token);
        try
        {
            await progress.FirstValue.Task.WaitAsync(TestBound);
            await stream.WaitingForSuffix.Task.WaitAsync(TestBound);
            Require(progress.Values.SequenceEqual(["Hello", " ", "世界"]),
                "Ollama progress was buffered, lost a whitespace delta, or exposed thinking");
            Require(!pending.IsCompleted, "Ollama completed before terminal bytes became available");
            stream.Release();
            var result = await pending.WaitAsync(TestBound);
            Require(result.Ok && result.Text == "Hello 世界!" && result.Reasoning == "Plan carefully now",
                $"Ollama did not combine visible and thinking deltas separately: {result.Error}");
            Require(progress.Values.SequenceEqual(["Hello", " ", "世界", "!"]),
                "Ollama final-frame content was omitted, duplicated, or mixed with reasoning");
            Require(result.Model == "canonical:latest"
                    && result.PromptTokens == 5 && result.CompletionTokens == 3 && result.TotalTokens == 8
                    && result.TokensPerSecond == 1.5 && result.ModelLoadTimeMs == 12
                    && result.ResponseId == "ollama-response" && result.TimeToFirstTokenMs > 0
                    && result.StopReason == ModelCompletionStopReason.OutputLimitReached,
                "Ollama done frame lost model, usage, telemetry, or output-limit evidence");
            Require(stream.ReadsAfterSuffix == 0, "Ollama read past its authoritative done=true frame");
            AssertOneAttempt(handler, observer, "succeeded");
            var completion = observer.Completions.Single();
            Require(completion.PromptTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 5 }
                    && completion.CompletionTokens is { Kind: ProviderTokenEvidenceKind.ProviderReported, Value: 3 }
                    && completion.TotalTokens is { Kind: ProviderTokenEvidenceKind.Estimated, Value: 8 },
                "Ollama completion observation did not distinguish reported counts from their derived total");
        }
        finally
        {
            stream.Release();
            cancellation.Cancel();
        }
    }

    internal static void PreservesPartialOutputAcrossAcceptedStreamFailures()
    {
        var cases = new (string Name, string Suffix, Exception? ReadFailure, string Outcome)[]
        {
            ("provider error", "{\"error\":\"generation failed " + ApiToken + "\"}\n", null, "provider_stream_error"),
            ("malformed JSON", "{not-json}\n{\"done\":true}\n", null, "provider_stream_error"),
            ("missing done", "", null, "provider_stream_incomplete"),
            ("transport fault", "", new IOException("accepted connection dropped"), "transport_error"),
            ("transport timeout", "", new TaskCanceledException("provider read timed out"), "provider_timeout")
        };
        foreach (var item in cases)
        {
            using var stream = new ScriptedStream(PartialFrames, item.Suffix, item.ReadFailure);
            using var handler = new StreamHandler(stream);
            using var http = new HttpClient(handler);
            var observer = new RecordingObserver();
            var progress = new RecordingProgress();
            var result = new ModelProviderClient(http, observer).CompleteChatStreamingAsync(
                Config(), Messages, progress).WaitAsync(TestBound).GetAwaiter().GetResult();
            Require(!result.Ok && result.Text == "partial" && result.Reasoning == "Plan carefully",
                $"Ollama {item.Name} lost accepted partial output or became a successful completion");
            Require(progress.Values.SequenceEqual(["partial"]), $"Ollama {item.Name} replayed or lost progress");
            Require(result.Error.Length > 0 && !result.Error.Contains(ApiToken, StringComparison.Ordinal),
                $"Ollama {item.Name} failed without an error or exposed a provider credential");
            AssertOneAttempt(handler, observer, item.Outcome);
        }
    }

    internal static void PropagatesCallerCancellationWithoutReplay() =>
        VerifyCallerCancellationAsync().GetAwaiter().GetResult();

    private static async Task VerifyCallerCancellationAsync()
    {
        using var stream = new ScriptedStream(PartialFrames, "", gated: true);
        using var handler = new StreamHandler(stream);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var observer = new RecordingObserver();
        var progress = new RecordingProgress();
        var pending = new ModelProviderClient(http, observer).CompleteChatStreamingAsync(
            Config(), Messages, progress, cancellation.Token);
        try
        {
            await progress.FirstValue.Task.WaitAsync(TestBound);
            await stream.WaitingForSuffix.Task.WaitAsync(TestBound);
            cancellation.Cancel();
            try
            {
                _ = await pending.WaitAsync(TestBound);
                throw new InvalidOperationException("Ollama swallowed caller cancellation after stream acceptance");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            Require(progress.Values.SequenceEqual(["partial"]), "Ollama cancellation lost or duplicated accepted progress");
            AssertOneAttempt(handler, observer, "caller_cancelled");
        }
        finally
        {
            cancellation.Cancel();
            stream.Release();
        }
    }

    internal static void RejectsEmptyTerminalCompletion()
    {
        using var stream = new ScriptedStream(
            "{\"message\":{\"thinking\":\"Reasoning alone\"},\"done\":false}\n",
            "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":2,\"eval_count\":1}\n");
        using var handler = new StreamHandler(stream);
        using var http = new HttpClient(handler);
        var observer = new RecordingObserver();
        var progress = new RecordingProgress();
        var result = new ModelProviderClient(http, observer).CompleteChatStreamingAsync(
            Config(), Messages, progress).WaitAsync(TestBound).GetAwaiter().GetResult();
        Require(!result.Ok && result.Text.Length == 0 && result.Reasoning == "Reasoning alone"
                && result.PromptTokens == 2 && result.CompletionTokens == 1 && progress.Values.IsEmpty,
            "Ollama accepted thinking-only output as public content or discarded terminal usage");
        AssertOneAttempt(handler, observer, "empty_response");
    }

    internal static void BoundsOversizedFramesWithoutLosingPartialOutput()
    {
        var oversizedFrame = "{\"message\":{\"content\":\"" + new string('x', 1024 * 1024 + 8192)
            + "\"},\"done\":false}\n{\"done\":true}\n";
        using var stream = new ScriptedStream(PartialFrames, oversizedFrame, readChunkSize: 4096);
        using var handler = new StreamHandler(stream);
        using var http = new HttpClient(handler);
        var observer = new RecordingObserver();
        var progress = new RecordingProgress();
        var result = new ModelProviderClient(http, observer).CompleteChatStreamingAsync(
            Config(), Messages, progress).WaitAsync(TestBound).GetAwaiter().GetResult();
        Require(!result.Ok && result.Text == "partial" && result.Reasoning == "Plan carefully"
                && progress.Values.SequenceEqual(["partial"]),
            "An oversized Ollama frame discarded prior output or reached the progress consumer");
        Require(result.Error.Contains("oversized", StringComparison.OrdinalIgnoreCase)
                && stream.SuffixBytesRead < Encoding.UTF8.GetByteCount(oversizedFrame),
            "Ollama did not enforce its line bound until the whole oversized frame was read");
        AssertOneAttempt(handler, observer, "provider_stream_error");
    }
    private static ModelProviderConfig Config() => new()
    {
        BaseUrl = "http://127.0.0.1:45678/v1",
        ApiMode = ModelProviderApiModes.OllamaNative,
        Model = "requested:latest",
        ApiToken = ApiToken,
        Timeout = 30,
        MaxOutputTokens = 64,
        // Even explicitly idempotent requests may not be replayed after a 200
        // acceptance followed by ambiguous stream progress or failure.
        Extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [ModelProviderClient.CompletionIdempotencyCapabilityKey] = JsonSerializer.SerializeToElement(true)
        }
    };

    private static void AssertOneAttempt(StreamHandler handler, RecordingObserver observer, string outcome)
    {
        Require(handler.Calls == 1 && handler.Method == HttpMethod.Post && handler.Path == "/api/chat",
            "Ollama streaming used the wrong native endpoint or replayed an accepted request");
        using var payload = JsonDocument.Parse(handler.Body);
        Require(payload.RootElement.GetProperty("stream").GetBoolean(), "Ollama requested buffered HTTP output");
        var trace = observer.Requests.Single();
        Require(trace.RequestedStreaming && trace.PayloadStreaming && trace.Attempt == 1
                && trace.PayloadSha256.Equals(Convert.ToHexString(SHA256.HashData(handler.Body)), StringComparison.OrdinalIgnoreCase),
            "Ollama request observation does not describe the exact streaming payload");
        var completion = observer.Completions.Single();
        Require(completion.RequestId == trace.RequestId && completion.Outcome == outcome,
            $"Ollama accepted request was not closed exactly once as {outcome}: {completion.Outcome}");
        Require(!JsonSerializer.Serialize(trace).Contains(ApiToken, StringComparison.Ordinal),
            "Ollama request inspection exposed an authorization credential");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        internal ConcurrentQueue<string> Values { get; } = new();
        internal TaskCompletionSource FirstValue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(string value)
        {
            Values.Enqueue(value);
            FirstValue.TrySetResult();
        }
    }

    private sealed class RecordingObserver : IProviderRequestObserver
    {
        internal List<ProviderRequestTrace> Requests { get; } = [];
        internal List<ProviderRequestCompletionObservation> Completions { get; } = [];
        public void ObserveRequest(ProviderRequestTrace trace) => Requests.Add(trace);
        public void ObserveCompletion(ProviderRequestCompletionObservation completion) => Completions.Add(completion);
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string Path { get; private set; } = "";
        internal HttpMethod? Method { get; private set; }
        internal byte[] Body { get; private set; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    // Short reads split both JSON frames and UTF-8 code points. The suffix gate
    // proves progress was delivered while the provider's final bytes were absent.
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly byte[] _suffix;
        private readonly Exception? _readFailure;
        private readonly int _readChunkSize;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prefixOffset;
        private int _suffixOffset;
        internal TaskCompletionSource WaitingForSuffix { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadsAfterSuffix { get; private set; }
        internal int SuffixBytesRead => _suffixOffset;
        internal ScriptedStream(string prefix, string suffix, Exception? readFailure = null, bool gated = false, int readChunkSize = 7)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            _suffix = Encoding.UTF8.GetBytes(suffix);
            _readFailure = readFailure;
            _readChunkSize = readChunkSize;
            if (!gated) Release();
        }
        internal void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (_prefixOffset < _prefix.Length) return Copy(_prefix, ref _prefixOffset, buffer);
            WaitingForSuffix.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (_suffixOffset < _suffix.Length) return Copy(_suffix, ref _suffixOffset, buffer);
            ReadsAfterSuffix++;
            if (_readFailure is not null) throw _readFailure;
            return 0;
        }
        private int Copy(byte[] source, ref int offset, Memory<byte> destination)
        {
            var count = Math.Min(_readChunkSize, Math.Min(destination.Length, source.Length - offset));
            source.AsMemory(offset, count).CopyTo(destination);
            offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
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
