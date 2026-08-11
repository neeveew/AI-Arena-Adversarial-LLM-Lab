using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Providers;

public interface IModelProviderClient
{
    Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default);

    Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default);
}

public interface IStreamingModelProviderClient
{
    Task<ModelCompletionResult> CompleteChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default);
}

public class ModelProviderClient : IModelProviderClient, IStreamingModelProviderClient
{
    private const int MaxProviderErrorLength = 360;
    private const string EmptyCompletionError = "Provider returned a successful response without assistant content.";
    private const int LlamaCppMaximumRetries = 2;
    private static readonly JsonSerializerOptions ProviderPayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IProviderRequestObserver? _requestObserver;

    public ModelProviderClient(HttpClient? httpClient = null, IProviderRequestObserver? requestObserver = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _requestObserver = requestObserver;
        // Per-request provider timeouts are enforced by TimeoutToken. HttpClient's
        // 100-second default would otherwise win for configured timeouts above 100s.
        _httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public async Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var apiMode = ModelProviderApiModes.Normalize(config.ApiMode);
        if (apiMode.Equals(ModelProviderApiModes.LlamaCppNative, StringComparison.OrdinalIgnoreCase))
        {
            return await ListLlamaCppModelsAsync(config, cancellationToken);
        }

        var listBaseUrl = apiMode switch
        {
            ModelProviderApiModes.LmStudioNative => NormalizeNativeApiBase(config.BaseUrl),
            ModelProviderApiModes.OllamaNative => NormalizeOllamaApiBase(config.BaseUrl),
            _ => baseUrl
        };
        var endpointPath = apiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase)
            ? "tags"
            : "models";

        try
        {
            var endpoint = new Uri(new Uri(listBaseUrl + "/"), endpointPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ApplyAuthorization(request, config);
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelProviderModels(
                    false,
                    baseUrl,
                    Array.Empty<string>(),
                    FriendlyProviderHttpError(body, response.ReasonPhrase, baseUrl, config.ApiToken),
                    DateTimeOffset.Now);
            }

            return new ModelProviderModels(true, baseUrl, ParseModelNames(body), "", DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            return new ModelProviderModels(false, baseUrl, Array.Empty<string>(), FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
        }
    }

    private async Task<ModelProviderModels> ListLlamaCppModelsAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        try
        {
            var rootEndpoint = new Uri(new Uri(NormalizeLlamaCppApiBase(config.BaseUrl) + "/"), "models");
            var rootResult = await TryListLlamaCppModelsAsync(rootEndpoint, config, cancellationToken);
            if (rootResult.Ok && rootResult.Models.Count > 0)
            {
                // Router mode lists available models here, including unloaded
                // models. This keeps an idle but healthy router from appearing
                // offline merely because /v1/models only reports live instances.
                return new ModelProviderModels(true, baseUrl, rootResult.Models, "", DateTimeOffset.Now);
            }

            var compatibleEndpoint = new Uri(new Uri(baseUrl + "/"), "models");
            var compatibleResult = await TryListLlamaCppModelsAsync(compatibleEndpoint, config, cancellationToken);
            if (compatibleResult.Ok)
            {
                return new ModelProviderModels(true, baseUrl, compatibleResult.Models, "", DateTimeOffset.Now);
            }

            if (rootResult.Ok)
            {
                return new ModelProviderModels(true, baseUrl, rootResult.Models, "", DateTimeOffset.Now);
            }

            var error = string.IsNullOrWhiteSpace(compatibleResult.Error)
                ? rootResult.Error
                : compatibleResult.Error;
            return new ModelProviderModels(false, baseUrl, Array.Empty<string>(), error, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            return new ModelProviderModels(false, baseUrl, Array.Empty<string>(), FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
        }
    }

    private async Task<(bool Ok, IReadOnlyList<string> Models, string Error)> TryListModelsAsync(
        Uri endpoint,
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthorization(request, config);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (false, Array.Empty<string>(), FriendlyProviderHttpError(body, response.ReasonPhrase, NormalizeBaseUrl(config.BaseUrl), config.ApiToken));
        }

        try
        {
            return (true, ParseModelNames(body), "");
        }
        catch (JsonException)
        {
            return (false, Array.Empty<string>(), $"Provider returned an unreadable model inventory at {SafeProviderEndpoint(endpoint.AbsoluteUri, config.ApiToken)}.");
        }
    }

    private async Task<(bool Ok, IReadOnlyList<string> Models, string Error)> TryListLlamaCppModelsAsync(
        Uri endpoint,
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(LlamaCppModelProbeTimeoutSeconds(config.Timeout)));
        try
        {
            return await TryListModelsAsync(endpoint, config, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (
                false,
                Array.Empty<string>(),
                $"llama.cpp model inventory probe timed out after {LlamaCppModelProbeTimeoutSeconds(config.Timeout)}s at {SafeProviderEndpoint(endpoint.AbsoluteUri, config.ApiToken)}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (
                false,
                Array.Empty<string>(),
                FriendlyProviderError(ex, NormalizeBaseUrl(config.BaseUrl), config.Timeout, config.ApiMode, config.ApiToken));
        }
    }

    internal static int LlamaCppModelProbeTimeoutSeconds(int configuredTimeoutSeconds)
    {
        // Router and compatible inventories are independent fallbacks. Keep
        // each attempt short so a hung router cannot block settings refresh.
        return Math.Clamp(configuredTimeoutSeconds, 1, 5);
    }

    public async Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var apiMode = ModelProviderApiModes.Normalize(config.ApiMode);
        if (apiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteNativeChatAsync(config, messages, cancellationToken);
        }

        if (apiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteOllamaNativeChatAsync(config, messages, requestedStreaming: false, cancellationToken);
        }

        return await CompleteOpenAiCompatibleChatAsync(
            config,
            messages,
            retryLlamaCppTransientFailures: apiMode.Equals(ModelProviderApiModes.LlamaCppNative, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    private async Task<ModelCompletionResult> CompleteOpenAiCompatibleChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        bool retryLlamaCppTransientFailures,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = new
        {
            model,
            messages = messages.Select(item => new { role = item.Role, content = item.Content }).ToArray(),
            temperature = config.Temperature,
            max_tokens = config.MaxOutputTokens,
            stream = false
        };
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        try
        {
            var endpoint = new Uri(new Uri(baseUrl + "/"), "chat/completions");
            using var timeout = TimeoutToken(config, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                activeObservationId = ObserveRequest(
                    config,
                    payloadBytes,
                    "openai_compatible_chat",
                    requestedStreaming: false,
                    attempt + 1);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = CreateJsonContent(payloadBytes)
                };
                ApplyAuthorization(request, config);
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (retryLlamaCppTransientFailures
                        && attempt < LlamaCppMaximumRetries
                        && IsTransientLlamaCppFailure(response.StatusCode, body))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            "The provider rejected this physical attempt before accepting it; no token evidence was returned.");
                        activeObservationId = "";
                        await DelayLlamaCppRetryAsync(attempt, timeout.Token);
                        continue;
                    }

                    watch.Stop();
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        model,
                        "",
                        "",
                        (int)watch.ElapsedMilliseconds,
                        0,
                        0,
                        0,
                        FriendlyProviderHttpError(body, response.ReasonPhrase, baseUrl, config.ApiToken),
                        DateTimeOffset.Now);
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                watch.Stop();
                using var completionDocument = JsonDocument.Parse(body);
                var completionRoot = completionDocument.RootElement;
                var usage = ExtractUsage(completionRoot);
                var telemetry = retryLlamaCppTransientFailures
                    ? ExtractLlamaCppTelemetry(completionRoot)
                    : new ModelProviderTelemetry(0, 0, "");
                var text = ExtractAssistantContent(completionRoot).Trim();
                var reasoning = ExtractReasoning(completionRoot).Trim();
                var responseModel = FirstString(completionRoot, "model");
                var completed = new ModelCompletionResult(
                    !string.IsNullOrWhiteSpace(text),
                    baseUrl,
                    retryLlamaCppTransientFailures && !string.IsNullOrWhiteSpace(responseModel) ? responseModel : model,
                    text,
                    reasoning,
                    (int)watch.ElapsedMilliseconds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    string.IsNullOrWhiteSpace(text) ? EmptyCompletionError : "",
                    DateTimeOffset.Now,
                    telemetry.TokensPerSecond,
                    telemetry.TimeToFirstTokenMs,
                    telemetry.ResponseId,
                    telemetry.ModelLoadTimeMs);
                return CompleteObservation(
                    activeObservationId,
                    completed,
                    completed.Ok ? "succeeded" : "empty_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, ex is OperationCanceledException ? "provider_timeout" : "transport_error");
        }
    }

    private async Task<ModelCompletionResult> CompleteNativeChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = NativeChatPayload(config, messages);
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        try
        {
            var endpoint = new Uri(new Uri(NormalizeNativeApiBase(config.BaseUrl) + "/"), "chat");
            activeObservationId = ObserveRequest(
                config,
                payloadBytes,
                "lmstudio_native_chat",
                requestedStreaming: false,
                attempt: 1);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = CreateJsonContent(payloadBytes)
            };
            ApplyAuthorization(request, config);
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                watch.Stop();
                var failed = new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    "",
                    "",
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    FriendlyProviderHttpError(body, response.ReasonPhrase, baseUrl, config.ApiToken),
                    DateTimeOffset.Now);
                return CompleteObservation(activeObservationId, failed, "provider_error");
            }

            watch.Stop();
            var completed = NativeCompletionFromBody(body, baseUrl, model, (int)watch.ElapsedMilliseconds);
            return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, ex is OperationCanceledException ? "provider_timeout" : "transport_error");
        }
    }

    private static ModelCompletionResult NativeCompletionFromBody(string body, string baseUrl, string fallbackModel, int latencyMs)
    {
        using var completionDocument = JsonDocument.Parse(body);
        var completionRoot = completionDocument.RootElement;
        var usage = ExtractNativeUsage(completionRoot);
        var telemetry = ExtractNativeTelemetry(completionRoot);
        var text = ExtractNativeOutputText(completionRoot, "message").Trim();
        return new ModelCompletionResult(
            !string.IsNullOrWhiteSpace(text),
            baseUrl,
            ExtractNativeModel(completionRoot, fallbackModel),
            text,
            ExtractNativeOutputText(completionRoot, "reasoning").Trim(),
            latencyMs,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            string.IsNullOrWhiteSpace(text) ? EmptyCompletionError : "",
            DateTimeOffset.Now,
            telemetry.TokensPerSecond,
            telemetry.TimeToFirstTokenMs,
            telemetry.ResponseId,
            telemetry.ModelLoadTimeMs);
    }

    public async Task<ModelCompletionResult> CompleteChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var apiMode = ModelProviderApiModes.Normalize(config.ApiMode);
        if (apiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteNativeChatStreamingAsync(config, messages, progress, cancellationToken);
        }

        if (apiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteOllamaNativeChatAsync(config, messages, requestedStreaming: true, cancellationToken);
        }

        return await CompleteOpenAiChatStreamingAsync(
            config,
            messages,
            progress,
            retryLlamaCppTransientFailures: apiMode.Equals(ModelProviderApiModes.LlamaCppNative, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    private async Task<ModelCompletionResult> CompleteNativeChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = NativeChatPayload(config, messages);
        payload["stream"] = true;
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        try
        {
            var endpoint = new Uri(new Uri(NormalizeNativeApiBase(config.BaseUrl) + "/"), "chat");
            activeObservationId = ObserveRequest(
                config,
                payloadBytes,
                "lmstudio_native_chat",
                requestedStreaming: true,
                attempt: 1);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = CreateJsonContent(payloadBytes)
            };
            ApplyAuthorization(request, config);
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
                watch.Stop();
                var failed = new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    "",
                    "",
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    FriendlyProviderHttpError(errorBody, response.ReasonPhrase, baseUrl, config.ApiToken),
                    DateTimeOffset.Now);
                return CompleteObservation(activeObservationId, failed, "provider_error");
            }

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var resultJson = "";
            var streamError = "";
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (string.IsNullOrWhiteSpace(data))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var type = FirstString(doc.RootElement, "type");
                    if (type.Equals("message.delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var delta = FirstString(doc.RootElement, "content");
                        if (delta.Length > 0)
                        {
                            content.Append(delta);
                            progress?.Report(delta);
                        }
                    }
                    else if (type.Equals("reasoning.delta", StringComparison.OrdinalIgnoreCase))
                    {
                        reasoning.Append(FirstString(doc.RootElement, "content"));
                    }
                    else if (type.Equals("chat.end", StringComparison.OrdinalIgnoreCase)
                        && doc.RootElement.TryGetProperty("result", out var result)
                        && result.ValueKind == JsonValueKind.Object)
                    {
                        resultJson = result.GetRawText();
                    }
                    else if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        streamError = ExtractProviderErrorMessage(doc.RootElement);
                    }
                }
                catch (JsonException)
                {
                }
            }

            watch.Stop();
            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                var completed = NativeCompletionFromBody(resultJson, baseUrl, model, (int)watch.ElapsedMilliseconds);
                return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
            }

            if (!string.IsNullOrWhiteSpace(streamError))
            {
                var failed = new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    content.ToString().Trim(),
                    reasoning.ToString().Trim(),
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    SanitizeProviderError(streamError, config.ApiToken),
                    DateTimeOffset.Now);
                return CompleteObservation(activeObservationId, failed, "provider_stream_error");
            }

            var streamedContent = content.ToString().Trim();
            var streamed = new ModelCompletionResult(
                !string.IsNullOrWhiteSpace(streamedContent),
                baseUrl,
                model,
                streamedContent,
                reasoning.ToString().Trim(),
                (int)watch.ElapsedMilliseconds,
                0,
                0,
                0,
                string.IsNullOrWhiteSpace(streamedContent) ? EmptyCompletionError : "",
                DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, streamed, streamed.Ok ? "succeeded" : "empty_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, ex is OperationCanceledException ? "provider_timeout" : "transport_error");
        }
    }

    private async Task<ModelCompletionResult> CompleteOpenAiChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        bool retryLlamaCppTransientFailures,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = new
        {
            model,
            messages = messages.Select(item => new { role = item.Role, content = item.Content }).ToArray(),
            temperature = config.Temperature,
            max_tokens = config.MaxOutputTokens,
            stream = true,
            stream_options = new { include_usage = true }
        };
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        try
        {
            var endpoint = new Uri(new Uri(baseUrl + "/"), "chat/completions");
            using var timeout = TimeoutToken(config, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                activeObservationId = ObserveRequest(
                    config,
                    payloadBytes,
                    "openai_compatible_chat",
                    requestedStreaming: true,
                    attempt + 1);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = CreateJsonContent(payloadBytes)
                };
                ApplyAuthorization(request, config);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (retryLlamaCppTransientFailures
                        && attempt < LlamaCppMaximumRetries
                        && IsTransientLlamaCppFailure(response.StatusCode, errorBody))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            "The provider rejected this physical attempt before accepting it; no token evidence was returned.");
                        activeObservationId = "";
                        await DelayLlamaCppRetryAsync(attempt, timeout.Token);
                        continue;
                    }

                    watch.Stop();
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        model,
                        "",
                        "",
                        (int)watch.ElapsedMilliseconds,
                        0,
                        0,
                        0,
                        FriendlyProviderHttpError(errorBody, response.ReasonPhrase, baseUrl, config.ApiToken),
                        DateTimeOffset.Now);
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                // From this point on the provider has accepted the request. Never
                // replay it: a dropped or malformed stream may already have emitted
                // tokens or committed provider-side state.
                var content = new StringBuilder();
                var reasoning = new StringBuilder();
                var responseModel = "";
                var usage = new ModelTokenUsage(0, 0, 0);
                var telemetry = new ModelProviderTelemetry(0, 0, "");
                var firstTokenMs = 0;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var data = line[5..].Trim();
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        continue;
                    }

                    if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (string.IsNullOrWhiteSpace(responseModel))
                        {
                            responseModel = FirstString(doc.RootElement, "model");
                        }

                        if (retryLlamaCppTransientFailures)
                        {
                            var chunkTelemetry = ExtractLlamaCppTelemetry(doc.RootElement);
                            telemetry = new ModelProviderTelemetry(
                                chunkTelemetry.TokensPerSecond > 0 ? chunkTelemetry.TokensPerSecond : telemetry.TokensPerSecond,
                                telemetry.TimeToFirstTokenMs,
                                string.IsNullOrWhiteSpace(chunkTelemetry.ResponseId) ? telemetry.ResponseId : chunkTelemetry.ResponseId,
                                chunkTelemetry.ModelLoadTimeMs > 0 ? chunkTelemetry.ModelLoadTimeMs : telemetry.ModelLoadTimeMs);
                        }

                        if (doc.RootElement.TryGetProperty("usage", out var usageElement)
                            && usageElement.ValueKind == JsonValueKind.Object)
                        {
                            var promptTokens = GetTokenCount(usageElement, "prompt_tokens");
                            var completionTokens = GetTokenCount(usageElement, "completion_tokens");
                            var totalTokens = GetTokenCount(usageElement, "total_tokens");
                            usage = new ModelTokenUsage(promptTokens, completionTokens, totalTokens <= 0 ? promptTokens + completionTokens : totalTokens);
                        }

                        if (!doc.RootElement.TryGetProperty("choices", out var choices)
                            || choices.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var first = choices.EnumerateArray().FirstOrDefault();
                        if (first.ValueKind != JsonValueKind.Object
                            || !first.TryGetProperty("delta", out var delta)
                            || delta.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var contentDelta = FirstString(delta, "content");
                        var reasoningDelta = FirstString(delta, "reasoning_content", "reasoning");
                        if (firstTokenMs <= 0 && (contentDelta.Length > 0 || reasoningDelta.Length > 0))
                        {
                            firstTokenMs = Math.Max(1, (int)watch.ElapsedMilliseconds);
                        }

                        if (contentDelta.Length > 0)
                        {
                            content.Append(contentDelta);
                            progress?.Report(contentDelta);
                        }

                        reasoning.Append(reasoningDelta);
                    }
                    catch (JsonException)
                    {
                    }
                }

                watch.Stop();
                var streamedContent = content.ToString().Trim();
                var completed = new ModelCompletionResult(
                    !string.IsNullOrWhiteSpace(streamedContent),
                    baseUrl,
                    string.IsNullOrWhiteSpace(responseModel) ? model : responseModel,
                    streamedContent,
                    reasoning.ToString().Trim(),
                    (int)watch.ElapsedMilliseconds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    string.IsNullOrWhiteSpace(streamedContent) ? EmptyCompletionError : "",
                    DateTimeOffset.Now,
                    telemetry.TokensPerSecond,
                    firstTokenMs,
                    telemetry.ResponseId,
                    telemetry.ModelLoadTimeMs);
                return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, ex is OperationCanceledException ? "provider_timeout" : "transport_error");
        }
    }

    private async Task<ModelCompletionResult> CompleteOllamaNativeChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        bool requestedStreaming,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = OllamaChatPayload(config, messages);
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        try
        {
            var endpoint = new Uri(new Uri(NormalizeOllamaApiBase(config.BaseUrl) + "/"), "chat");
            activeObservationId = ObserveRequest(
                config,
                payloadBytes,
                "ollama_native_chat",
                requestedStreaming,
                attempt: 1);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = CreateJsonContent(payloadBytes)
            };
            ApplyAuthorization(request, config);
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                watch.Stop();
                var failed = new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    "",
                    "",
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    FriendlyProviderHttpError(body, response.ReasonPhrase, baseUrl, config.ApiToken),
                    DateTimeOffset.Now);
                return CompleteObservation(activeObservationId, failed, "provider_error");
            }

            watch.Stop();
            using var completionDocument = JsonDocument.Parse(body);
            var completionRoot = completionDocument.RootElement;
            var usage = ExtractOllamaUsage(completionRoot);
            var telemetry = ExtractOllamaTelemetry(completionRoot);
            var text = ExtractOllamaChatContent(completionRoot).Trim();
            var completed = new ModelCompletionResult(
                !string.IsNullOrWhiteSpace(text),
                baseUrl,
                ExtractOllamaModel(completionRoot, model),
                text,
                ExtractOllamaReasoning(completionRoot).Trim(),
                (int)watch.ElapsedMilliseconds,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                string.IsNullOrWhiteSpace(text) ? EmptyCompletionError : "",
                DateTimeOffset.Now,
                telemetry.TokensPerSecond,
                telemetry.TimeToFirstTokenMs,
                telemetry.ResponseId,
                telemetry.ModelLoadTimeMs);
            return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, ex is OperationCanceledException ? "provider_timeout" : "transport_error");
        }
    }

    private static byte[] SerializePayload<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, ProviderPayloadJsonOptions);

    private static HttpContent CreateJsonContent(byte[] exactPayload)
    {
        var content = new ByteArrayContent(exactPayload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    private string ObserveRequest(
        ModelProviderConfig config,
        byte[] exactPayload,
        string transport,
        bool requestedStreaming,
        int attempt)
    {
        if (_requestObserver is null)
        {
            return "";
        }

        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            _requestObserver.ObserveRequest(ProviderPromptInspection.CreateTrace(
                requestId,
                config,
                exactPayload,
                transport,
                requestedStreaming,
                attempt));
        }
        catch (Exception)
        {
            // Inspection is diagnostic-only. Observer, redaction, or storage
            // failures must never change provider-call behavior.
        }

        return requestId;
    }

    private ModelCompletionResult CompleteObservation(
        string requestId,
        ModelCompletionResult result,
        string outcome)
    {
        if (string.IsNullOrWhiteSpace(requestId) || _requestObserver is null)
        {
            return result;
        }

        var prompt = ProviderReportedCount(
            result.PromptTokens,
            "prompt",
            "The provider response did not expose a positive prompt-token count; zero and absent cannot be distinguished by this adapter.");
        var completion = ProviderReportedCount(
            result.CompletionTokens,
            "completion",
            "The provider response did not expose a positive completion-token count; zero and absent cannot be distinguished by this adapter.");
        ProviderTokenEvidence total;
        if (result.TotalTokens <= 0)
        {
            total = ProviderTokenEvidence.Unavailable(
                "The provider response did not expose a positive total-token count.");
        }
        else if (prompt.Kind == ProviderTokenEvidenceKind.ProviderReported
            && completion.Kind == ProviderTokenEvidenceKind.ProviderReported
            && result.TotalTokens == result.PromptTokens + result.CompletionTokens)
        {
            // Existing adapters normalize a missing total by summing provider
            // components. Conservatively label this derived value rather than
            // claiming the provider reported the total itself.
            total = new ProviderTokenEvidence(
                ProviderTokenEvidenceKind.Estimated,
                result.TotalTokens,
                "Derived from provider-reported prompt and completion counts; the adapter cannot prove that the provider reported total_tokens separately.");
        }
        else
        {
            total = new ProviderTokenEvidence(
                ProviderTokenEvidenceKind.ProviderReported,
                result.TotalTokens,
                "Reported by the provider response and preserved by the adapter.");
        }

        ObserveCompletion(new ProviderRequestCompletionObservation(requestId, outcome, prompt, completion, total));
        return result;
    }

    private static ProviderTokenEvidence ProviderReportedCount(
        int value,
        string label,
        string unavailableExplanation) =>
        value > 0
            ? new ProviderTokenEvidence(
                ProviderTokenEvidenceKind.ProviderReported,
                value,
                $"The {label}-token count was reported by the provider response.")
            : ProviderTokenEvidence.Unavailable(unavailableExplanation);

    private void ObserveUnavailableCompletion(string requestId, string outcome, string explanation)
    {
        if (string.IsNullOrWhiteSpace(requestId) || _requestObserver is null)
        {
            return;
        }

        var unavailable = ProviderTokenEvidence.Unavailable(explanation);
        ObserveCompletion(new ProviderRequestCompletionObservation(
            requestId,
            outcome,
            unavailable,
            unavailable,
            unavailable));
    }

    private void ObserveCompletion(ProviderRequestCompletionObservation completion)
    {
        try
        {
            _requestObserver?.ObserveCompletion(completion);
        }
        catch (Exception)
        {
            // Inspection is diagnostic-only and cannot fail a provider call.
        }
    }

    public static string NormalizeBaseUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? ModelProviderDefaults.BaseUrl : value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7].TrimEnd('/');
        }

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/v1";
    }

    public static string NormalizeNativeApiBase(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? ModelProviderDefaults.BaseUrl : value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return $"{trimmed}/api/v1";
    }

    public static string NormalizeOllamaApiBase(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:11434" : value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7].TrimEnd('/');
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return $"{trimmed}/api";
    }

    public static string NormalizeLlamaCppApiBase(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:8080" : value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7].TrimEnd('/');
        }
        else if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return trimmed;
    }

    public static int CountModels(string json)
    {
        return ParseModelNames(json).Count;
    }

    public static IReadOnlyList<string> ParseModelNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            if (!doc.RootElement.TryGetProperty("models", out data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }
        }

        var models = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            models.Add(FirstString(item, "id", "key", "selected_variant", "model", "name"));
        }

        return models.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
    }

    public static string ExtractAssistantContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractAssistantContent(doc.RootElement);
    }

    private static string ExtractAssistantContent(JsonElement root)
    {
        var message = FirstAssistantMessage(root);
        return message.HasValue && message.Value.TryGetProperty("content", out var content)
            ? ExtractNativeTextContent(content)
            : "";
    }

    public static string ExtractReasoning(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractReasoning(doc.RootElement);
    }

    private static string ExtractReasoning(JsonElement root)
    {
        var message = FirstAssistantMessage(root);
        if (!message.HasValue)
        {
            return "";
        }

        if (message.Value.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
        {
            return reasoningContent.GetString() ?? "";
        }

        return message.Value.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String
            ? reasoning.GetString() ?? ""
            : "";
    }

    public static ModelTokenUsage ExtractUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractUsage(doc.RootElement);
    }

    private static ModelTokenUsage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new ModelTokenUsage(0, 0, 0);
        }

        var promptTokens = GetTokenCount(usage, "prompt_tokens");
        var completionTokens = GetTokenCount(usage, "completion_tokens");
        var totalTokens = GetTokenCount(usage, "total_tokens");
        if (totalTokens <= 0)
        {
            totalTokens = promptTokens + completionTokens;
        }

        return new ModelTokenUsage(promptTokens, completionTokens, totalTokens);
    }

    public static ModelProviderTelemetry ExtractLlamaCppTelemetry(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractLlamaCppTelemetry(doc.RootElement);
    }

    private static ModelProviderTelemetry ExtractLlamaCppTelemetry(JsonElement root)
    {
        var responseId = FirstString(root, "id", "response_id");
        if (!root.TryGetProperty("timings", out var timings) || timings.ValueKind != JsonValueKind.Object)
        {
            return new ModelProviderTelemetry(0, 0, responseId);
        }

        // llama-server's OpenAI-compatible response adds its native timings
        // object. Only consume explicitly reported values; prompt duration is
        // not time-to-first-token and must not be presented as such.
        var tokensPerSecond = FirstDouble(
            timings,
            "predicted_per_second",
            "tokens_per_second");
        var timeToFirstTokenMs = FirstDurationMs(
            timings,
            ("time_to_first_token_ms", 1d),
            ("ttft_ms", 1d));
        return new ModelProviderTelemetry(tokensPerSecond, timeToFirstTokenMs, responseId);
    }

    public static string ExtractNativeChatContent(string json)
    {
        return ExtractNativeOutputText(json, "message");
    }

    public static string ExtractNativeReasoning(string json)
    {
        return ExtractNativeOutputText(json, "reasoning");
    }

    public static ModelTokenUsage ExtractNativeUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractNativeUsage(doc.RootElement);
    }

    private static ModelTokenUsage ExtractNativeUsage(JsonElement root)
    {
        if (!root.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            return new ModelTokenUsage(0, 0, 0);
        }

        var promptTokens = GetTokenCount(stats, "input_tokens");
        var completionTokens = GetTokenCount(stats, "total_output_tokens");
        if (completionTokens <= 0)
        {
            completionTokens = GetTokenCount(stats, "output_tokens");
        }

        return new ModelTokenUsage(promptTokens, completionTokens, promptTokens + completionTokens);
    }

    public static string ExtractNativeModel(string json, string fallback)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractNativeModel(doc.RootElement, fallback);
    }

    private static string ExtractNativeModel(JsonElement root, string fallback)
    {
        return root.TryGetProperty("model_instance_id", out var model)
            && model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? fallback
            : fallback;
    }

    public static ModelProviderTelemetry ExtractNativeTelemetry(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractNativeTelemetry(doc.RootElement);
    }

    private static ModelProviderTelemetry ExtractNativeTelemetry(JsonElement root)
    {
        var responseId = FirstString(root, "response_id");
        if (!root.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            return new ModelProviderTelemetry(0, 0, responseId);
        }

        var tokensPerSecond = FirstDouble(stats, "tokens_per_second");
        var timeToFirstTokenMs = FirstDurationMs(
            stats,
            ("time_to_first_token_seconds", 1000d),
            ("time_to_first_token", 1000d),
            ("ttft_seconds", 1000d),
            ("time_to_first_token_ms", 1d),
            ("ttft_ms", 1d));
        var modelLoadTimeMs = FirstDurationMs(
            stats,
            ("model_load_time_seconds", 1000d),
            ("model_load_time", 1000d),
            ("load_time_seconds", 1000d),
            ("model_load_time_ms", 1d),
            ("load_time_ms", 1d));

        return new ModelProviderTelemetry(tokensPerSecond, timeToFirstTokenMs, responseId, modelLoadTimeMs);
    }

    public static string ExtractOllamaChatContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractOllamaChatContent(doc.RootElement);
    }

    private static string ExtractOllamaChatContent(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var content = FirstString(message, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return FirstString(root, "response", "content");
    }

    public static string ExtractOllamaReasoning(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractOllamaReasoning(doc.RootElement);
    }

    private static string ExtractOllamaReasoning(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var thinking = FirstString(message, "thinking", "reasoning", "reasoning_content");
            if (!string.IsNullOrWhiteSpace(thinking))
            {
                return thinking;
            }
        }

        return FirstString(root, "thinking", "reasoning");
    }

    public static ModelTokenUsage ExtractOllamaUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractOllamaUsage(doc.RootElement);
    }

    private static ModelTokenUsage ExtractOllamaUsage(JsonElement root)
    {
        var promptTokens = GetTokenCount(root, "prompt_eval_count");
        var completionTokens = GetTokenCount(root, "eval_count");
        return new ModelTokenUsage(promptTokens, completionTokens, promptTokens + completionTokens);
    }

    public static string ExtractOllamaModel(string json, string fallback)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractOllamaModel(doc.RootElement, fallback);
    }

    private static string ExtractOllamaModel(JsonElement root, string fallback)
    {
        var model = FirstString(root, "model");
        return string.IsNullOrWhiteSpace(model) ? fallback : model;
    }

    public static ModelProviderTelemetry ExtractOllamaTelemetry(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractOllamaTelemetry(doc.RootElement);
    }

    private static ModelProviderTelemetry ExtractOllamaTelemetry(JsonElement root)
    {
        var responseId = FirstString(root, "response_id", "id");
        var completionTokens = GetTokenCount(root, "eval_count");
        var evalDurationMs = FirstDurationMs(root, ("eval_duration", 0.000001d));
        var tokensPerSecond = completionTokens > 0 && evalDurationMs > 0
            ? Math.Round(completionTokens / (evalDurationMs / 1000d), 2)
            : FirstDouble(root, "tokens_per_second");
        var modelLoadTimeMs = FirstDurationMs(root, ("load_duration", 0.000001d));
        var timeToFirstTokenMs = FirstDurationMs(
            root,
            ("time_to_first_token_ms", 1d),
            ("time_to_first_token", 0.000001d));

        return new ModelProviderTelemetry(tokensPerSecond, timeToFirstTokenMs, responseId, modelLoadTimeMs);
    }

    private static Dictionary<string, object> NativeChatPayload(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = config.Model,
            ["input"] = NativeChatInput(messages, config.PreserveNativeInputWhitespace),
            ["temperature"] = config.Temperature,
            ["max_output_tokens"] = config.MaxOutputTokens,
            ["store"] = config.NativeStatefulChat
        };

        var previousResponseId = NativeResponseId(config.PreviousResponseId);
        if (config.NativeStatefulChat && !string.IsNullOrWhiteSpace(previousResponseId))
        {
            payload["previous_response_id"] = previousResponseId;
        }

        var systemPrompt = NativeSystemPrompt(messages);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system_prompt"] = systemPrompt;
        }

        if (config.ContextLength > 0)
        {
            payload["context_length"] = config.ContextLength;
        }

        var reasoning = ModelProviderReasoningModes.Normalize(config.Reasoning);
        if (!string.IsNullOrWhiteSpace(reasoning)
            && !reasoning.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning"] = reasoning;
        }

        return payload;
    }

    private static Dictionary<string, object> OllamaChatPayload(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = config.Model,
            ["messages"] = messages.Select(message => new
            {
                role = NormalizeOllamaRole(message.Role),
                content = message.Content
            }).ToArray(),
            ["stream"] = false
        };

        var options = new Dictionary<string, object>
        {
            ["temperature"] = config.Temperature,
            ["num_predict"] = config.MaxOutputTokens
        };
        if (config.ContextLength > 0)
        {
            options["num_ctx"] = config.ContextLength;
        }

        payload["options"] = options;

        var think = OllamaThinkValue(config.Reasoning);
        if (think is not null)
        {
            payload["think"] = think;
        }

        if (config.NativeIdleTtlSeconds > 0)
        {
            payload["keep_alive"] = config.NativeIdleTtlSeconds;
        }

        return payload;
    }

    private static object? OllamaThinkValue(string reasoning)
    {
        return ModelProviderReasoningModes.Normalize(reasoning) switch
        {
            "off" => false,
            "on" => true,
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null
        };
    }

    private static string NormalizeOllamaRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "system" or "user" or "assistant" or "tool" => role.Trim().ToLowerInvariant(),
            _ => "user"
        };
    }

    public static string NativeResponseId(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("resp_", StringComparison.OrdinalIgnoreCase) ? trimmed : "";
    }

    private static string NativeSystemPrompt(IReadOnlyList<ModelChatMessage> messages)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            messages
                .Where(message => message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Content.Trim())
                .Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    private static string NativeChatInput(
        IReadOnlyList<ModelChatMessage> messages,
        bool preserveInputWhitespace)
    {
        var nonSystem = messages
            .Where(message => !message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(message => FormatNativeChatMessage(message, preserveInputWhitespace))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
        if (nonSystem.Length > 0)
        {
            return string.Join(Environment.NewLine + Environment.NewLine, nonSystem);
        }

        return messages.LastOrDefault()?.Content ?? "";
    }

    private static string FormatNativeChatMessage(
        ModelChatMessage message,
        bool preserveInputWhitespace)
    {
        var content = preserveInputWhitespace ? message.Content : message.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        return message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? content
            : $"{message.Role}: {content}";
    }

    private static string ExtractNativeOutputText(string json, string type)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractNativeOutputText(doc.RootElement, type);
    }

    private static string ExtractNativeOutputText(JsonElement root, string type)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var itemType)
                || itemType.ValueKind != JsonValueKind.String
                || !type.Equals(itemType.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ExtractNativeOutputItemText(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ExtractNativeOutputItemText(JsonElement item)
    {
        if (item.TryGetProperty("content", out var content))
        {
            var text = ExtractNativeTextContent(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return FirstString(item, "text", "output_text");
    }

    private static string ExtractNativeTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            return FirstString(content, "text", "content", "output_text");
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                parts.Add(part.GetString() ?? "");
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object)
            {
                var text = FirstString(part, "text", "content", "output_text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static JsonElement? FirstAssistantMessage(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("message", out var message))
        {
            return null;
        }

        return message;
    }

    private static int GetTokenCount(JsonElement usage, string name)
    {
        return usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count
            : 0;
    }

    private static string FirstString(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        return "";
    }

    private static double FirstDouble(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return Math.Round(number, 2);
            }
        }

        return 0;
    }

    private static int FirstDurationMs(JsonElement item, params (string PropertyName, double Multiplier)[] propertyNames)
    {
        foreach (var (propertyName, multiplier) in propertyNames)
        {
            if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return Math.Max(0, (int)Math.Round(number * multiplier));
            }
        }

        return 0;
    }

    private static CancellationTokenSource TimeoutToken(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Timeout, 1, 3600)));
        return timeout;
    }

    private static bool IsTransientLlamaCppFailure(HttpStatusCode statusCode, string body)
    {
        if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return true;
        }

        if ((int)statusCode is < 409 or >= 600 || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var safePrefix = body.Length <= 4096 ? body : body[..4096];
        return safePrefix.Contains("loading", StringComparison.OrdinalIgnoreCase)
            || safePrefix.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || safePrefix.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || safePrefix.Contains("unavailable_error", StringComparison.OrdinalIgnoreCase)
            || safePrefix.Contains("no slot", StringComparison.OrdinalIgnoreCase)
            || safePrefix.Contains("queue full", StringComparison.OrdinalIgnoreCase);
    }

    private static Task DelayLlamaCppRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = attempt <= 0 ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromMilliseconds(400);
        return Task.Delay(delay, cancellationToken);
    }

    private static void ApplyAuthorization(HttpRequestMessage request, ModelProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiToken.Trim()}");
        }
    }

    private static string FriendlyProviderError(
        Exception ex,
        string baseUrl,
        int timeoutSeconds,
        string apiMode,
        string apiToken)
    {
        var safeBaseUrl = SafeProviderEndpoint(baseUrl, apiToken);
        if (ex is UriFormatException)
        {
            return $"Invalid provider base URL '{safeBaseUrl}'. Enter a full URL such as http://127.0.0.1:1234/v1.";
        }

        if (ex is OperationCanceledException)
        {
            return $"Provider timed out after {Math.Clamp(timeoutSeconds, 1, 3600)}s at {safeBaseUrl}. Check that the model is loaded and responding.";
        }

        if (ex is JsonException)
        {
            var apiLabel = ApiModeLabel(apiMode);
            return $"Provider returned an unreadable response at {safeBaseUrl}. Check that the server is returning valid {apiLabel} JSON.";
        }

        var message = SanitizeProviderError(ex.Message, apiToken);
        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
        {
            return $"Provider unreachable at {safeBaseUrl}. Start LM Studio, Ollama, or your local provider server, then check the base URL.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? $"Provider request failed at {safeBaseUrl}."
            : $"Provider request failed at {safeBaseUrl}: {message}";
    }

    private static string ApiModeLabel(string apiMode)
    {
        return ModelProviderApiModes.Normalize(apiMode) switch
        {
            ModelProviderApiModes.LmStudioNative => "LM Studio native",
            ModelProviderApiModes.OllamaNative => "Ollama native",
            ModelProviderApiModes.LlamaCppNative => "llama.cpp native",
            _ => "OpenAI-compatible"
        };
    }

    private static string FriendlyProviderHttpError(string body, string? reasonPhrase, string baseUrl, string apiToken)
    {
        var message = ExtractProviderErrorMessage(body);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(reasonPhrase) ? "HTTP request failed." : reasonPhrase.Trim();
        }

        return $"Provider request failed at {SafeProviderEndpoint(baseUrl, apiToken)}: {SanitizeProviderError(message, apiToken)}";
    }

    public static string ExtractProviderErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ExtractProviderErrorMessage(doc.RootElement);
        }
        catch (JsonException)
        {
            return ShortenProviderError(body.Trim());
        }
    }

    private static string ExtractProviderErrorMessage(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()?.Trim() ?? "";
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var message = ExtractProviderErrorMessage(item);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }

                return "";
            case JsonValueKind.Object:
                foreach (var propertyName in new[] { "error", "message", "detail", "reason", "msg", "code" })
                {
                    if (value.TryGetProperty(propertyName, out var child))
                    {
                        var message = ExtractProviderErrorMessage(child);
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            return message;
                        }
                    }
                }

                foreach (var property in value.EnumerateObject())
                {
                    var message = ExtractProviderErrorMessage(property.Value);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }

                return "";
            default:
                return "";
        }
    }

    private static string ShortenProviderError(string message)
    {
        var normalized = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxProviderErrorLength
            ? normalized
            : normalized[..(MaxProviderErrorLength - 3)] + "...";
    }

    private static string SanitizeProviderError(string message, string apiToken)
    {
        var rawMessage = message ?? "";
        if ((!string.IsNullOrWhiteSpace(apiToken)
                && rawMessage.Contains(apiToken.Trim(), StringComparison.Ordinal))
            || InternetRequestSafety.ContainsSensitivePayload(rawMessage))
        {
            return "Provider returned an error containing sensitive data; details were hidden.";
        }

        return ShortenProviderError(rawMessage);
    }

    private static string SafeProviderEndpoint(string baseUrl, string apiToken)
    {
        if (!string.IsNullOrWhiteSpace(apiToken)
            && baseUrl.Contains(apiToken.Trim(), StringComparison.Ordinal))
        {
            return "configured endpoint";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return InternetRequestSafety.ContainsSensitivePayload(baseUrl)
                ? "configured endpoint"
                : ShortenProviderError(baseUrl);
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        if (InternetRequestSafety.ContainsSensitivePayload(builder.Path))
        {
            builder.Path = "/";
        }

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}

public sealed record ModelTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record ModelProviderTelemetry(double TokensPerSecond, int TimeToFirstTokenMs, string ResponseId, int ModelLoadTimeMs = 0);
