using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.VerificationLab;

internal sealed record VerificationLabRunResult(
    int PassedChecks,
    int FailedChecks,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string RequestCaptureFingerprint);

internal static class VerificationLabHarness
{
    private const string PrivatePrompt = "never-capture-this-operator-content";
    private const string PrivateToken = "qa-token-never-record";
    private const string PrivateModelPath = @"C:\Users\Private\models\secret-model.gguf";

    public static async Task<VerificationLabRunResult> RunAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await using var provider = await ScriptedProviderHost.StartAsync(cancellationToken);
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new ModelProviderClient(httpClient);

        var checks = new (string Name, Func<Task> Run)[]
        {
            ("loopback health, root, and llama telemetry endpoints", () => VerifySupportEndpointsAsync(provider, cancellationToken)),
            ("llama router and OpenAI model discovery", () => VerifyModelDiscoveryAsync(client, provider, cancellationToken)),
            ("nonstreaming chat and llama telemetry", () => VerifyNonStreamingChatAsync(client, provider, cancellationToken)),
            ("SSE streaming chat and progress", () => VerifyStreamingChatAsync(client, provider, cancellationToken)),
            ("caller cancellation propagates", () => VerifyCallerCancellationAsync(client, provider)),
            ("provider timeout is reported", () => VerifyProviderTimeoutAsync(client, provider)),
            ("disconnect is contained", () => VerifyDisconnectAsync(client, provider, cancellationToken)),
            ("malformed stream is rejected", () => VerifyMalformedStreamAsync(client, provider, cancellationToken)),
            ("saturation is surfaced", () => VerifySaturationAsync(client, provider, cancellationToken)),
            ("empty completion is rejected", () => VerifyEmptyCompletionAsync(client, provider, cancellationToken)),
            ("HTTP error is surfaced", () => VerifyHttpErrorAsync(client, provider, cancellationToken)),
            ("request capture is deterministic and content-free", () => VerifyCaptureSafetyAsync(client, provider, cancellationToken)),
            ("experiment matrix forks real loopback trials and resumes privately", () => ExperimentExecutionVerification.RunAsync(provider, client, cancellationToken)),
            ("QA evidence validator detects missing, replaced, traversal, and stale artifacts", () => VerificationEvidenceValidatorChecks.RunAsync(cancellationToken)),
            ("provider request and capture evidence stays bounded", () => VerifyProviderBoundsAsync(provider, cancellationToken)),
            ("performance, restart, and resource measurements are bounded", () => VerifyPerformanceMeasurementsAsync(cancellationToken))
        };

        var passed = 0;
        var failed = 0;
        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            try
            {
                await check.Run();
                watch.Stop();
                passed++;
                await output.WriteLineAsync($"PASS {check.Name} ({watch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                watch.Stop();
                failed++;
                await output.WriteLineAsync($"FAIL {check.Name} ({watch.ElapsedMilliseconds} ms): {SafeFailure(ex)}");
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        var captureFingerprint = VerificationLabEvidence.RequestCaptureFingerprint(provider.Captures);
        await output.WriteLineAsync($"Verification Lab: {passed}/{checks.Length} passed; request evidence {captureFingerprint[..12]}.");
        return new VerificationLabRunResult(passed, failed, startedAt, completedAt, captureFingerprint);
    }

    private static async Task VerifySupportEndpointsAsync(ScriptedProviderHost provider, CancellationToken cancellationToken)
    {
        Require(provider.BaseUri.IsLoopback && provider.BaseUri.Scheme == Uri.UriSchemeHttp, "Verification provider must bind to loopback HTTP only.");
        using var http = new HttpClient { BaseAddress = provider.BaseUri };
        foreach (var path in new[] { "", "health", "props", "slots" })
        {
            using var response = await http.GetAsync(path, cancellationToken);
            Require(response.IsSuccessStatusCode, $"Expected {path} to succeed.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var _ = JsonDocument.Parse(body);
        }
    }

    private static async Task VerifyModelDiscoveryAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        var llama = await client.ListModelsAsync(Config(provider, ModelProviderApiModes.LlamaCppNative), cancellationToken);
        Require(llama.Ok, llama.Error);
        Require(llama.Models.SequenceEqual([ScriptedProviderHost.PrimaryModel, ScriptedProviderHost.SecondaryModel]), "Router model order changed.");

        var compatible = await client.ListModelsAsync(Config(provider, ModelProviderApiModes.OpenAiCompatible), cancellationToken);
        Require(compatible.Ok, compatible.Error);
        Require(compatible.Models.SequenceEqual(llama.Models), "OpenAI model inventory differs from the router inventory.");
        Require(provider.Captures.Any(item => item.Path == "/models"), "Router discovery did not reach /models.");
        Require(provider.Captures.Any(item => item.Path == "/v1/models"), "Compatible discovery did not reach /v1/models.");
    }

    private static async Task VerifyNonStreamingChatAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        var result = await client.CompleteChatAsync(Config(provider), Messages(), cancellationToken);
        Require(result.Ok, result.Error);
        Require(result.Text == ScriptedProviderHost.CompletionText, "Completion text changed.");
        Require(result.Reasoning == ScriptedProviderHost.ReasoningText, "Reasoning text changed.");
        Require(result.Model == ScriptedProviderHost.PrimaryModel, "Response model changed.");
        Require(result.PromptTokens == 7 && result.CompletionTokens == 3 && result.TotalTokens == 10, "Usage telemetry changed.");
        Require(Math.Abs(result.TokensPerSecond - 42.5) < 0.001, "llama.cpp throughput telemetry changed.");
        Require(result.TimeToFirstTokenMs == 12, "llama.cpp TTFT telemetry changed.");
        Require(result.ResponseId == "chatcmpl-scripted", "Response ID changed.");
    }

    private static async Task VerifyStreamingChatAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        var progress = new ProgressCapture();
        var result = await client.CompleteChatStreamingAsync(Config(provider), Messages(), progress, cancellationToken);
        Require(result.Ok, result.Error);
        Require(result.Text == ScriptedProviderHost.CompletionText, "Streamed completion text changed.");
        Require(result.Reasoning == ScriptedProviderHost.ReasoningText, "Streamed reasoning changed.");
        Require(progress.Text == ScriptedProviderHost.CompletionText, "Progress deltas do not reconstruct the completion.");
        Require(result.PromptTokens == 7 && result.CompletionTokens == 3 && result.TotalTokens == 10, "Streaming usage telemetry changed.");
        Require(Math.Abs(result.TokensPerSecond - 42.5) < 0.001, "Streaming throughput telemetry changed.");
        Require(result.TimeToFirstTokenMs > 0, "Streaming TTFT must be observed from the first delta.");
    }

    private static async Task VerifyCallerCancellationAsync(ModelProviderClient client, ScriptedProviderHost provider)
    {
        provider.QueueFault(ScriptedProviderFault.Timeout);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var watch = Stopwatch.StartNew();
        try
        {
            _ = await client.CompleteChatAsync(Config(provider, timeoutSeconds: 10), Messages(), cancellation.Token);
            throw new InvalidOperationException("Caller cancellation did not propagate.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            watch.Stop();
            Require(watch.Elapsed < TimeSpan.FromSeconds(3), "Caller cancellation was not prompt.");
        }
    }

    private static async Task VerifyProviderTimeoutAsync(ModelProviderClient client, ScriptedProviderHost provider)
    {
        provider.QueueFault(ScriptedProviderFault.Timeout);
        var result = await client.CompleteChatAsync(Config(provider, timeoutSeconds: 1), Messages());
        Require(!result.Ok, "Timed-out completion was reported as successful.");
        Require(result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase), "Timeout error was not classified.");
    }

    private static async Task VerifyDisconnectAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        provider.QueueFault(ScriptedProviderFault.Disconnect);
        var result = await client.CompleteChatAsync(Config(provider), Messages(), cancellationToken);
        Require(!result.Ok, "Disconnected request was reported as successful.");
        Require(result.Text.Length == 0, "Disconnected request retained unverified content.");
    }

    private static async Task VerifyMalformedStreamAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        provider.QueueFault(ScriptedProviderFault.MalformedStream);
        var result = await client.CompleteChatStreamingAsync(Config(provider), Messages(), progress: null, cancellationToken);
        Require(!result.Ok, "Malformed stream was reported as successful.");
        Require(result.Text.Length == 0, "Malformed stream retained unverified content.");
    }

    private static async Task VerifySaturationAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        provider.QueueFault(ScriptedProviderFault.Saturation);
        var result = await client.CompleteChatAsync(
            Config(provider, ModelProviderApiModes.OpenAiCompatible),
            Messages(),
            cancellationToken);
        Require(!result.Ok, "Saturated provider was reported as successful.");
        Require(result.Error.Contains("queue full", StringComparison.OrdinalIgnoreCase), "Saturation evidence was lost.");
    }

    private static async Task VerifyEmptyCompletionAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        provider.QueueFault(ScriptedProviderFault.Empty);
        var result = await client.CompleteChatAsync(Config(provider), Messages(), cancellationToken);
        Require(!result.Ok, "Empty completion was reported as successful.");
        Require(result.Error.Contains("without assistant content", StringComparison.OrdinalIgnoreCase), "Empty completion was not classified.");
    }

    private static async Task VerifyHttpErrorAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        provider.QueueFault(ScriptedProviderFault.HttpError);
        var result = await client.CompleteChatAsync(Config(provider), Messages(), cancellationToken);
        Require(!result.Ok, "HTTP failure was reported as successful.");
        Require(result.Error.Contains("scripted HTTP failure", StringComparison.Ordinal), "Provider error evidence was lost.");
    }

    private static async Task VerifyCaptureSafetyAsync(
        ModelProviderClient client,
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        var before = provider.Captures.Count;
        var first = await client.CompleteChatAsync(Config(provider), Messages(PrivatePrompt), cancellationToken);
        var second = await client.CompleteChatAsync(Config(provider), Messages(PrivatePrompt), cancellationToken);
        var pathModel = await client.CompleteChatAsync(Config(provider, model: PrivateModelPath), Messages(PrivatePrompt), cancellationToken);
        Require(first.Ok && second.Ok && pathModel.Ok, "Determinism probes failed.");

        var captures = provider.Captures.Skip(before).ToArray();
        Require(captures.Length == 3, "Expected exactly three capture-safety probes.");
        Require(captures[0].BodySha256 == captures[1].BodySha256, "Identical requests produced different fingerprints.");
        Require(captures.All(item => item.AuthorizationSupplied), "Authorization presence was not captured.");
        Require(captures.All(item => item.BodySha256.Length == 64), "Request hashes must be SHA-256.");
        Require(captures[2].ModelEvidence.StartsWith("model-sha256-", StringComparison.Ordinal), "Private model paths must be opaque in capture evidence.");

        var serialized = JsonSerializer.Serialize(provider.Captures);
        Require(!serialized.Contains(PrivatePrompt, StringComparison.Ordinal), "Request content leaked into capture evidence.");
        Require(!serialized.Contains(PrivateToken, StringComparison.Ordinal), "Authorization secret leaked into capture evidence.");
        Require(!serialized.Contains(PrivateModelPath, StringComparison.OrdinalIgnoreCase), "Absolute model path leaked into capture evidence.");
        Require(!serialized.Contains("secret-model.gguf", StringComparison.OrdinalIgnoreCase), "Private model filename leaked into capture evidence.");

        var sequences = provider.Captures.Select(item => item.Sequence).ToArray();
        Require(sequences.SequenceEqual(Enumerable.Range(1, sequences.Length)), "Request capture order is not deterministic.");
    }

    private static async Task VerifyProviderBoundsAsync(
        ScriptedProviderHost provider,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = provider.BaseUri };
        var privateOversizeBody = new string('x', ScriptedProviderHost.MaximumRequestBodyBytes + 1);
        using (var content = new StringContent(privateOversizeBody, System.Text.Encoding.UTF8, "application/json"))
        using (var response = await http.PostAsync("v1/chat/completions", content, cancellationToken))
        {
            Require(response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge, "Oversize scripted-provider request was not rejected with 413.");
        }
        Require(provider.RejectedOversizeRequestCount == 1, "Oversize request rejection was not recorded as aggregate evidence.");
        Require(!JsonSerializer.Serialize(provider.Captures).Contains(privateOversizeBody, StringComparison.Ordinal), "Oversize request content entered retained evidence.");

        var requestsNeeded = ScriptedProviderHost.MaximumRetainedCaptures + 8;
        for (var index = 0; index < requestsNeeded; index++)
        {
            using var response = await http.GetAsync("health", cancellationToken);
            Require(response.IsSuccessStatusCode, "Capture saturation probe failed.");
        }
        Require(provider.Captures.Count == ScriptedProviderHost.MaximumRetainedCaptures, "Retained request capture evidence exceeded its hard cap.");
        Require(provider.DroppedCaptureCount > 0, "Capture overflow was not diagnosed by an aggregate count.");
    }

    private static async Task VerifyPerformanceMeasurementsAsync(CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ai-arena-verification-measurements-{Guid.NewGuid():N}.json");
        try
        {
            using var writer = new StringWriter();
            var exitCode = await VerificationPerformanceRunner.RunAndWriteAsync(outputPath, writer, cancellationToken);
            Require(exitCode == 0, "Measurement runner did not meet every declared threshold.");
            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            Require(bytes.Length is > 0 and <= 64 * 1024, "Measurement artifact exceeded its bound.");
            var bundle = JsonSerializer.Deserialize<VerificationMeasurementBundle>(bytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Require(bundle is not null, "Measurement artifact did not deserialize.");
            if (bundle is null)
            {
                throw new InvalidOperationException("Measurement artifact did not deserialize.");
            }
            Require(bundle.Schema == VerificationPerformanceRunner.Schema, "Measurement schema changed.");
            Require(bundle.Measurements.Count == 5 && bundle.Measurements.All(item => item.Passed), "Measurement manifest is incomplete.");
            Require(bundle.HealthySoakRequests >= 10 && bundle.ProviderRestarts == 3, "Restart or soak evidence is incomplete.");
            var text = Encoding.UTF8.GetString(bytes);
            Require(!text.Contains(PrivatePrompt, StringComparison.Ordinal), "Measurement artifact leaked prompt content.");
            Require(!text.Contains(PrivateToken, StringComparison.Ordinal), "Measurement artifact leaked provider credentials.");
            Require(!text.Contains(PrivateModelPath, StringComparison.OrdinalIgnoreCase), "Measurement artifact leaked a private model path.");

            var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            using var secondWriter = new StringWriter();
            var secondExit = await VerificationPerformanceRunner.RunAndWriteAsync(outputPath, secondWriter, cancellationToken);
            Require(secondExit != 0, "Measurement runner overwrote existing evidence.");
            var afterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(outputPath, cancellationToken)));
            Require(originalHash == afterHash, "Existing measurement evidence changed after a rejected write.");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static ModelProviderConfig Config(
        ScriptedProviderHost provider,
        string apiMode = ModelProviderApiModes.LlamaCppNative,
        int timeoutSeconds = 5,
        string? model = null)
    {
        return new ModelProviderConfig
        {
            BaseUrl = provider.BaseUri.AbsoluteUri,
            ApiMode = apiMode,
            ApiToken = PrivateToken,
            Model = model ?? ScriptedProviderHost.PrimaryModel,
            Timeout = timeoutSeconds,
            Temperature = 0.25,
            MaxOutputTokens = 64
        };
    }

    private static IReadOnlyList<ModelChatMessage> Messages(string content = "Run the deterministic verification turn.") =>
    [
        new ModelChatMessage("system", "Follow the verification contract."),
        new ModelChatMessage("user", content)
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string SafeFailure(Exception exception)
    {
        var message = exception.Message
            .Replace(PrivatePrompt, "[content hidden]", StringComparison.Ordinal)
            .Replace(PrivateToken, "[secret hidden]", StringComparison.Ordinal)
            .Replace(PrivateModelPath, "[private path hidden]", StringComparison.OrdinalIgnoreCase)
            .Replace("secret-model.gguf", "[private model hidden]", StringComparison.OrdinalIgnoreCase);
        message = Regex.Replace(
            message,
            @"(?ix)(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/]|/(?:users|home|root|var|tmp|private)(?:/|\\))[^\r\n]*",
            "[private path hidden]");
        message = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"{exception.GetType().Name}: {(message.Length <= 240 ? message : message[..237] + "...")}";
    }

    private sealed class ProgressCapture : IProgress<string>
    {
        private readonly object _lock = new();
        private readonly List<string> _pieces = [];

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return string.Concat(_pieces);
                }
            }
        }

        public void Report(string value)
        {
            lock (_lock)
            {
                _pieces.Add(value);
            }
        }
    }
}
