using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Services;

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

/// <summary>Optional richer progress; existing streaming clients remain supported.</summary>
public interface IActivityStreamingModelProviderClient : IStreamingModelProviderClient
{
    Task<ModelCompletionResult> CompleteChatStreamingWithActivityAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? publicProgress,
        IProgress<ModelProviderActivity>? activity,
        CancellationToken cancellationToken = default);
}
public class ModelProviderClient : IModelProviderClient, IActivityStreamingModelProviderClient
{
    internal const int MaximumModelCatalogBytes = 4 * 1024 * 1024;
    // Provider inventories are untrusted input. Inspect no more than this many
    // source-array entries even when a highly compressed inventory remains
    // below the independent four-MiB response bound.
    internal const int MaximumModelCatalogEntries = 1024;
    private const string EmptyCompletionError = ModelCompletionOutcomeClassifier.EmptyPublicContentError;
    internal const int MaximumCompletionAttempts = 3;
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private const int InitialRetryDelayMilliseconds = 150;
    private const int MaximumRetryJitterMilliseconds = 100;
    internal const string CompletionIdempotencyCapabilityKey = "completion_idempotency_key_supported";
    private static readonly JsonSerializerOptions ProviderPayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IProviderRequestObserver? _requestObserver;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, double> _retryJitterUnit;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelayAsync;

    public ModelProviderClient(HttpClient? httpClient = null, IProviderRequestObserver? requestObserver = null)
        : this(httpClient, requestObserver, TimeProvider.System)
    {
    }

    internal ModelProviderClient(
        HttpClient? httpClient,
        IProviderRequestObserver? requestObserver,
        TimeProvider timeProvider,
        Func<int, double>? retryJitterUnit = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _requestObserver = requestObserver;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _retryJitterUnit = retryJitterUnit ?? (_ => Random.Shared.NextDouble());
        _retryDelayAsync = retryDelayAsync ?? ((delay, token) => Task.Delay(delay, _timeProvider, token));
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
            return await ListLlamaCppModelsAsync(config, cancellationToken).ConfigureAwait(false);
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
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var body = await BoundedTextContentReader.ReadAsync(
                response.Content,
                MaximumModelCatalogBytes,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelProviderModels(
                    false,
                    baseUrl,
                    Array.Empty<string>(),
                    FriendlyProviderHttpError(body, response.ReasonPhrase, baseUrl, config.ApiToken),
                    DateTimeOffset.Now);
            }

            var catalog = ParseModelCatalog(body);
            return new ModelProviderModels(
                true,
                baseUrl,
                catalog.Models,
                "",
                DateTimeOffset.Now,
                catalog.OmittedEntryCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
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
            var rootResult = await TryListLlamaCppModelsAsync(rootEndpoint, config, cancellationToken).ConfigureAwait(false);
            if (rootResult.Ok && rootResult.Models.Count > 0)
            {
                // Router mode lists available models here, including unloaded
                // models. This keeps an idle but healthy router from appearing
                // offline merely because /v1/models only reports live instances.
                return new ModelProviderModels(
                    true,
                    baseUrl,
                    rootResult.Models,
                    "",
                    DateTimeOffset.Now,
                    rootResult.OmittedEntryCount);
            }

            var compatibleEndpoint = new Uri(new Uri(baseUrl + "/"), "models");
            var compatibleResult = await TryListLlamaCppModelsAsync(compatibleEndpoint, config, cancellationToken).ConfigureAwait(false);
            if (compatibleResult.Ok)
            {
                return new ModelProviderModels(
                    true,
                    baseUrl,
                    compatibleResult.Models,
                    "",
                    DateTimeOffset.Now,
                    compatibleResult.OmittedEntryCount);
            }

            if (rootResult.Ok)
            {
                return new ModelProviderModels(
                    true,
                    baseUrl,
                    rootResult.Models,
                    "",
                    DateTimeOffset.Now,
                    rootResult.OmittedEntryCount);
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
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            return new ModelProviderModels(false, baseUrl, Array.Empty<string>(), FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
        }
    }

    private async Task<(bool Ok, IReadOnlyList<string> Models, string Error, int OmittedEntryCount)> TryListModelsAsync(
        Uri endpoint,
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthorization(request, config);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await BoundedTextContentReader.ReadAsync(
            response.Content,
            MaximumModelCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, Array.Empty<string>(), FriendlyProviderHttpError(body, response.ReasonPhrase, NormalizeBaseUrl(config.BaseUrl), config.ApiToken), 0);
        }

        try
        {
            var catalog = ParseModelCatalog(body);
            return (true, catalog.Models, "", catalog.OmittedEntryCount);
        }
        catch (JsonException)
        {
            return (false, Array.Empty<string>(), $"Provider returned an unreadable model inventory at {ProviderErrorSanitizer.Endpoint(endpoint.AbsoluteUri, config.ApiToken)}.", 0);
        }
    }

    private async Task<(bool Ok, IReadOnlyList<string> Models, string Error, int OmittedEntryCount)> TryListLlamaCppModelsAsync(
        Uri endpoint,
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(LlamaCppModelProbeTimeoutSeconds(config.Timeout)));
        try
        {
            return await TryListModelsAsync(endpoint, config, timeout.Token).ConfigureAwait(false);
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
                $"llama.cpp model inventory probe timed out after {LlamaCppModelProbeTimeoutSeconds(config.Timeout)}s at {ProviderErrorSanitizer.Endpoint(endpoint.AbsoluteUri, config.ApiToken)}.",
                0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return (
                false,
                Array.Empty<string>(),
                FriendlyProviderError(ex, NormalizeBaseUrl(config.BaseUrl), config.Timeout, config.ApiMode, config.ApiToken),
                0);
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
        ModelCompletionResult result;
        if (apiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            result = await CompleteNativeChatAsync(config, messages, cancellationToken);
        }
        else if (apiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            result = await CompleteOllamaNativeChatAsync(config, messages, requestedStreaming: false, cancellationToken);
        }
        else
        {
            result = await CompleteOpenAiCompatibleChatAsync(
                config,
                messages,
                retryLlamaCppTransientFailures: apiMode.Equals(ModelProviderApiModes.LlamaCppNative, StringComparison.OrdinalIgnoreCase),
                cancellationToken);
        }

        return ModelCompletionOutcomeClassifier.Normalize(result);
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
            var idempotencyKey = CompletionIdempotencyKey(config);
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
                ApplyCompletionIdempotencyKey(request, idempotencyKey);
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < MaximumCompletionAttempts - 1
                        && TryAuthorizeCompletionRetry(
                            config,
                            endpoint,
                            response.RequestMessage?.RequestUri,
                            response.StatusCode,
                            body,
                            retryLlamaCppTransientFailures,
                            idempotencyKey,
                            out var retryEvidence)
                        && TryResolveRetryDelay(
                            response.Headers,
                            attempt,
                            _timeProvider,
                            _retryJitterUnit,
                            out var retryDelay))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            retryEvidence);
                        activeObservationId = "";
                        await _retryDelayAsync(retryDelay, timeout.Token);
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
                        DateTimeOffset.Now,
                        ProviderStatusCode: (int)response.StatusCode,
                        ProviderErrorCode: ExtractProviderErrorCode(body, config.ApiToken));
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
                    telemetry.ModelLoadTimeMs,
                    StopReason: ModelCompletionOutcomeClassifier.ExtractStopReason(completionRoot),
                    ProviderStopReason: ModelCompletionOutcomeClassifier.ExtractProviderStopReason(completionRoot, config.ApiToken));
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
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var failed = new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now);
            return CompleteObservation(activeObservationId, failed, FailureObservationOutcome(ex));
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
            var idempotencyKey = CompletionIdempotencyKey(config);
            using var timeout = TimeoutToken(config, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                activeObservationId = ObserveRequest(
                    config,
                    payloadBytes,
                    "lmstudio_native_chat",
                    requestedStreaming: false,
                    attempt + 1);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = CreateJsonContent(payloadBytes)
                };
                ApplyAuthorization(request, config);
                ApplyCompletionIdempotencyKey(request, idempotencyKey);
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < MaximumCompletionAttempts - 1
                        && TryAuthorizeCompletionRetry(
                            config,
                            endpoint,
                            response.RequestMessage?.RequestUri,
                            response.StatusCode,
                            body,
                            useLlamaCppBusyBodyPredicates: false,
                            idempotencyKey,
                            out var retryEvidence)
                        && TryResolveRetryDelay(
                            response.Headers,
                            attempt,
                            _timeProvider,
                            _retryJitterUnit,
                            out var retryDelay))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            retryEvidence);
                        activeObservationId = "";
                        await _retryDelayAsync(retryDelay, timeout.Token);
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
                        DateTimeOffset.Now,
                        ProviderStatusCode: (int)response.StatusCode,
                        ProviderErrorCode: ExtractProviderErrorCode(body, config.ApiToken));
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                watch.Stop();
                var completed = NativeCompletionFromBody(body, baseUrl, model, (int)watch.ElapsedMilliseconds, config.ApiToken);
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
            return CompleteObservation(activeObservationId, failed, FailureObservationOutcome(ex));
        }
    }

    private static ModelCompletionResult NativeCompletionFromBody(string body, string baseUrl, string fallbackModel, int latencyMs, string apiToken)
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
            telemetry.ModelLoadTimeMs,
            StopReason: ModelCompletionOutcomeClassifier.ExtractStopReason(completionRoot),
            ProviderStopReason: ModelCompletionOutcomeClassifier.ExtractProviderStopReason(completionRoot, apiToken));
    }

    public Task<ModelCompletionResult> CompleteChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default) =>
        CompleteChatStreamingWithActivityAsync(config, messages, progress, activity: null, cancellationToken);

    public async Task<ModelCompletionResult> CompleteChatStreamingWithActivityAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? publicProgress,
        IProgress<ModelProviderActivity>? activity,
        CancellationToken cancellationToken = default)
    {
        var progress = publicProgress;
        var activityReporter = activity is null ? null : new ProviderActivityReporter(activity);
        var apiMode = ModelProviderApiModes.Normalize(config.ApiMode);
        ModelCompletionResult result;
        if (apiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            result = await CompleteNativeChatStreamingAsync(config, messages, progress, cancellationToken, activityReporter);
        }
        else if (apiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            result = await CompleteOllamaNativeChatAsync(config, messages, requestedStreaming: true, cancellationToken, progress, activityReporter);
        }
        else
        {
            result = await CompleteOpenAiChatStreamingAsync(
                config,
                messages,
                progress,
                retryLlamaCppTransientFailures: apiMode.Equals(ModelProviderApiModes.LlamaCppNative, StringComparison.OrdinalIgnoreCase),
                cancellationToken,
                activityReporter);
        }

        return ModelCompletionOutcomeClassifier.Normalize(result);
    }

    private async Task<ModelCompletionResult> CompleteNativeChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        ProviderActivityReporter? activity = null)
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
        StringBuilder? acceptedContent = null;
        StringBuilder? acceptedReasoning = null;
        var acceptedStreamError = "";
        var acceptedMalformedEvent = false;
        try
        {
            var endpoint = new Uri(new Uri(NormalizeNativeApiBase(config.BaseUrl) + "/"), "chat");
            var idempotencyKey = CompletionIdempotencyKey(config);
            using var timeout = TimeoutToken(config, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                activeObservationId = ObserveRequest(
                    config,
                    payloadBytes,
                    "lmstudio_native_chat",
                    requestedStreaming: true,
                    attempt + 1);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = CreateJsonContent(payloadBytes)
                };
                ApplyAuthorization(request, config);
                ApplyCompletionIdempotencyKey(request, idempotencyKey);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (attempt < MaximumCompletionAttempts - 1
                        && TryAuthorizeCompletionRetry(
                            config,
                            endpoint,
                            response.RequestMessage?.RequestUri,
                            response.StatusCode,
                            errorBody,
                            useLlamaCppBusyBodyPredicates: false,
                            idempotencyKey,
                            out var retryEvidence)
                        && TryResolveRetryDelay(
                            response.Headers,
                            attempt,
                            _timeProvider,
                            _retryJitterUnit,
                            out var retryDelay))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            retryEvidence);
                        activeObservationId = "";
                        await _retryDelayAsync(retryDelay, timeout.Token);
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
                        DateTimeOffset.Now,
                        ProviderStatusCode: (int)response.StatusCode,
                        ProviderErrorCode: ExtractProviderErrorCode(errorBody, config.ApiToken));
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                // A successful status is the acceptance boundary. Never replay
                // after this point: the stream may have produced billable work
                // even when no usable event reaches the caller.

                acceptedContent = new StringBuilder();
                acceptedReasoning = new StringBuilder();
                acceptedStreamError = "";
                acceptedMalformedEvent = false;
                var content = acceptedContent;
                var reasoning = acceptedReasoning;
                var resultJson = "";
                var sawTerminalEvent = false;
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
                        activity?.NativeStage(type, doc.RootElement);
                        if (type.Equals("message.delta", StringComparison.OrdinalIgnoreCase))
                        {
                            var delta = FirstString(doc.RootElement, "content");
                            if (delta.Length > 0)
                            {
                                content.Append(delta);
                                progress?.Report(delta);
                                activity?.PublicDelta(delta.Length);
                            }
                        }
                        else if (type.Equals("reasoning.delta", StringComparison.OrdinalIgnoreCase))
                        {
                            var delta = FirstString(doc.RootElement, "content");
                            reasoning.Append(delta);
                            activity?.ReasoningDelta(delta.Length);
                        }
                        else if (type.Equals("chat.end", StringComparison.OrdinalIgnoreCase))
                        {
                            sawTerminalEvent = true;
                            if (doc.RootElement.TryGetProperty("result", out var result)
                                && result.ValueKind == JsonValueKind.Object)
                            {
                                resultJson = result.GetRawText();
                            }

                            break;
                        }
                        else if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                        {
                            var observedError = doc.RootElement.TryGetProperty("error", out var errorElement)
                                ? ExtractProviderErrorMessage(errorElement)
                                : FirstString(doc.RootElement, "message", "detail", "reason");
                            if (!string.IsNullOrWhiteSpace(observedError))
                            {
                                acceptedStreamError = observedError;
                            }
                            else if (string.IsNullOrWhiteSpace(acceptedStreamError))
                            {
                                acceptedStreamError = "Provider stream returned an LM Studio error event.";
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        acceptedMalformedEvent = true;
                    }
                }

                watch.Stop();
                ModelCompletionResult? terminalResult = null;
                if (!string.IsNullOrWhiteSpace(resultJson))
                {
                    terminalResult = NativeCompletionFromBody(resultJson, baseUrl, model, (int)watch.ElapsedMilliseconds, config.ApiToken);
                    if (string.IsNullOrWhiteSpace(terminalResult.Reasoning) && reasoning.Length > 0)
                    {
                        terminalResult = terminalResult with { Reasoning = reasoning.ToString().Trim() };
                    }
                }

                // LM Studio documents error -> chat.end as a normal failure
                // sequence. Error evidence must win over the terminal result;
                // the latter is retained only for safe partial/token evidence.
                if (!string.IsNullOrWhiteSpace(acceptedStreamError))
                {
                    var partialText = !string.IsNullOrWhiteSpace(terminalResult?.Text)
                        ? terminalResult.Text
                        : content.ToString().Trim();
                    var partialReasoning = !string.IsNullOrWhiteSpace(terminalResult?.Reasoning)
                        ? terminalResult.Reasoning
                        : reasoning.ToString().Trim();
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        terminalResult?.Model ?? model,
                        partialText,
                        partialReasoning,
                        (int)watch.ElapsedMilliseconds,
                        terminalResult?.PromptTokens ?? 0,
                        terminalResult?.CompletionTokens ?? 0,
                        terminalResult?.TotalTokens ?? 0,
                        ProviderErrorSanitizer.Sanitize(acceptedStreamError, config.ApiToken),
                        DateTimeOffset.Now,
                        terminalResult?.TokensPerSecond ?? 0,
                        terminalResult?.TimeToFirstTokenMs ?? 0,
                        terminalResult?.ResponseId ?? "",
                        terminalResult?.ModelLoadTimeMs ?? 0,
                        StopReason: ModelCompletionStopReason.ProviderError,
                        ProviderStopReason: terminalResult?.ProviderStopReason ?? "");
                    return CompleteObservation(activeObservationId, failed, "provider_stream_error");
                }

                if (acceptedMalformedEvent)
                {
                    var partialText = !string.IsNullOrWhiteSpace(terminalResult?.Text)
                        ? terminalResult.Text
                        : content.ToString().Trim();
                    var partialReasoning = !string.IsNullOrWhiteSpace(terminalResult?.Reasoning)
                        ? terminalResult.Reasoning
                        : reasoning.ToString().Trim();
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        terminalResult?.Model ?? model,
                        partialText,
                        partialReasoning,
                        (int)watch.ElapsedMilliseconds,
                        terminalResult?.PromptTokens ?? 0,
                        terminalResult?.CompletionTokens ?? 0,
                        terminalResult?.TotalTokens ?? 0,
                        "Provider stream contained malformed LM Studio event data; any partial response was preserved.",
                        DateTimeOffset.Now,
                        terminalResult?.TokensPerSecond ?? 0,
                        terminalResult?.TimeToFirstTokenMs ?? 0,
                        terminalResult?.ResponseId ?? "",
                        terminalResult?.ModelLoadTimeMs ?? 0,
                        StopReason: ModelCompletionStopReason.ProviderError,
                        ProviderStopReason: terminalResult?.ProviderStopReason ?? "");
                    return CompleteObservation(activeObservationId, failed, "provider_stream_error");
                }

                if (terminalResult is not null)
                {
                    var streamedText = content.ToString().Trim();
                    if (streamedText.Length > 0
                        && !string.Equals(streamedText, terminalResult.Text, StringComparison.Ordinal))
                    {
                        // Accepted public output is never an empty-answer
                        // retry candidate, even if the terminal body loses it.
                        var inconsistent = terminalResult with
                        {
                            Ok = false,
                            Text = streamedText,
                            Error = "Provider terminal response did not match the public text already streamed; the partial response was preserved.",
                            FailureKind = ModelCompletionFailureKind.InvalidResponse,
                            StopReason = ModelCompletionStopReason.ProviderError
                        };
                        return CompleteObservation(activeObservationId, inconsistent, "provider_stream_error");
                    }

                    return CompleteObservation(
                        activeObservationId,
                        terminalResult,
                        terminalResult.Ok ? "succeeded" : "empty_response");
                }

                var partialContent = content.ToString().Trim();
                var incomplete = new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    partialContent,
                    reasoning.ToString().Trim(),
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    sawTerminalEvent
                        ? "Provider stream ended without a terminal LM Studio result; any partial response was preserved."
                        : "Provider stream ended before the required LM Studio chat.end event; any partial response was preserved.",
                    DateTimeOffset.Now,
                    FailureKind: ModelCompletionFailureKind.InvalidResponse);
                return CompleteObservation(activeObservationId, incomplete, "provider_stream_incomplete");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            if (acceptedContent is not null
                && (!string.IsNullOrWhiteSpace(acceptedStreamError) || acceptedMalformedEvent))
            {
                _ = CompleteObservation(
                    activeObservationId,
                    LmStudioAcceptedStreamFailureResult(
                        baseUrl,
                        model,
                        acceptedContent,
                        acceptedReasoning,
                        (int)watch.ElapsedMilliseconds,
                        acceptedStreamError,
                        acceptedMalformedEvent,
                        config.ApiToken),
                    "provider_stream_error");
            }
            else
            {
                ObserveUnavailableCompletion(
                    activeObservationId,
                    "caller_cancelled",
                    acceptedContent is { Length: > 0 }
                        ? "The caller cancelled after provider acceptance and partial progress; the accepted stream was not replayed."
                        : "The caller cancelled after the provider request began; the physical attempt was not replayed.");
            }
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var failed = acceptedContent is not null
                && (!string.IsNullOrWhiteSpace(acceptedStreamError) || acceptedMalformedEvent)
                ? LmStudioAcceptedStreamFailureResult(
                    baseUrl,
                    model,
                    acceptedContent,
                    acceptedReasoning,
                    (int)watch.ElapsedMilliseconds,
                    acceptedStreamError,
                    acceptedMalformedEvent,
                    config.ApiToken)
                : new ModelCompletionResult(
                    false,
                    baseUrl,
                    model,
                    acceptedContent?.ToString().Trim() ?? "",
                    acceptedReasoning?.ToString().Trim() ?? "",
                    (int)watch.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken),
                    DateTimeOffset.Now);
            var outcome = !string.IsNullOrWhiteSpace(acceptedStreamError) || acceptedMalformedEvent
                ? "provider_stream_error"
                : FailureObservationOutcome(ex);
            return CompleteObservation(activeObservationId, failed, outcome);
        }
    }

    private async Task<ModelCompletionResult> CompleteOpenAiChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        bool retryLlamaCppTransientFailures,
        CancellationToken cancellationToken,
        ProviderActivityReporter? activity = null)
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
        StringBuilder? acceptedContent = null;
        StringBuilder? acceptedReasoning = null;
        var acceptedResponseModel = "";
        var acceptedUsage = new ModelTokenUsage(0, 0, 0);
        var acceptedTelemetry = new ModelProviderTelemetry(0, 0, "");
        var acceptedStopReason = ModelCompletionStopReason.Unknown;
        var acceptedProviderStopReason = "";
        var acceptedSawTerminalStop = false;
        var acceptedFirstTokenMs = 0;
        try
        {
            var endpoint = new Uri(new Uri(baseUrl + "/"), "chat/completions");
            var idempotencyKey = CompletionIdempotencyKey(config);
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
                ApplyCompletionIdempotencyKey(request, idempotencyKey);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (attempt < MaximumCompletionAttempts - 1
                        && TryAuthorizeCompletionRetry(
                            config,
                            endpoint,
                            response.RequestMessage?.RequestUri,
                            response.StatusCode,
                            errorBody,
                            retryLlamaCppTransientFailures,
                            idempotencyKey,
                            out var retryEvidence)
                        && TryResolveRetryDelay(
                            response.Headers,
                            attempt,
                            _timeProvider,
                            _retryJitterUnit,
                            out var retryDelay))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            retryEvidence);
                        activeObservationId = "";
                        await _retryDelayAsync(retryDelay, timeout.Token);
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
                        DateTimeOffset.Now,
                        ProviderStatusCode: (int)response.StatusCode,
                        ProviderErrorCode: ExtractProviderErrorCode(errorBody, config.ApiToken));
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                // From this point on the provider has accepted the request. Never
                // replay it: a dropped or malformed stream may already have emitted
                // tokens or committed provider-side state.
                acceptedContent = new StringBuilder();
                acceptedReasoning = new StringBuilder();
                var content = acceptedContent;
                var reasoning = acceptedReasoning;
                acceptedResponseModel = "";
                acceptedUsage = new ModelTokenUsage(0, 0, 0);
                acceptedTelemetry = new ModelProviderTelemetry(0, 0, "");
                acceptedStopReason = ModelCompletionStopReason.Unknown;
                acceptedProviderStopReason = "";
                acceptedSawTerminalStop = false;
                acceptedFirstTokenMs = 0;
                var sawDone = false;
                var sawMalformedEvent = false;
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

                    if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        sawDone = true;
                        break;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("error", out _))
                        {
                            streamError = ExtractProviderErrorMessage(doc.RootElement);
                            if (string.IsNullOrWhiteSpace(streamError))
                            {
                                streamError = "Provider stream returned an error event.";
                            }

                            break;
                        }

                        if (string.IsNullOrWhiteSpace(acceptedResponseModel))
                        {
                            acceptedResponseModel = FirstString(doc.RootElement, "model");
                        }

                        var rawStopReason = ModelCompletionOutcomeClassifier.ExtractProviderStopReason(doc.RootElement, config.ApiToken);
                        if (rawStopReason.Length > 0) acceptedProviderStopReason = rawStopReason;
                        acceptedSawTerminalStop |= ModelCompletionOutcomeClassifier.ClassifyStopReason(rawStopReason)
                            != ModelCompletionStopReason.Unknown;
                        var chunkStopReason = ModelCompletionOutcomeClassifier.ExtractStopReason(doc.RootElement);
                        if (chunkStopReason != ModelCompletionStopReason.Unknown
                            && (acceptedStopReason != ModelCompletionStopReason.ToolCall
                                || chunkStopReason is ModelCompletionStopReason.ProviderError or ModelCompletionStopReason.ContentFiltered))
                        {
                            acceptedStopReason = chunkStopReason;
                        }

                        if (retryLlamaCppTransientFailures)
                        {
                            var chunkTelemetry = ExtractLlamaCppTelemetry(doc.RootElement);
                            acceptedTelemetry = new ModelProviderTelemetry(
                                chunkTelemetry.TokensPerSecond > 0 ? chunkTelemetry.TokensPerSecond : acceptedTelemetry.TokensPerSecond,
                                acceptedTelemetry.TimeToFirstTokenMs,
                                string.IsNullOrWhiteSpace(chunkTelemetry.ResponseId) ? acceptedTelemetry.ResponseId : chunkTelemetry.ResponseId,
                                chunkTelemetry.ModelLoadTimeMs > 0 ? chunkTelemetry.ModelLoadTimeMs : acceptedTelemetry.ModelLoadTimeMs);
                        }

                        if (doc.RootElement.TryGetProperty("usage", out var usageElement)
                            && usageElement.ValueKind == JsonValueKind.Object)
                        {
                            var promptTokens = GetTokenCount(usageElement, "prompt_tokens");
                            var completionTokens = GetTokenCount(usageElement, "completion_tokens");
                            var totalTokens = GetTokenCount(usageElement, "total_tokens");
                            acceptedUsage = new ModelTokenUsage(promptTokens, completionTokens, totalTokens <= 0 ? promptTokens + completionTokens : totalTokens);
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
                        var reasoningDelta = FirstString(delta, "reasoning_content", "reasoning", "thinking");
                        if (acceptedFirstTokenMs <= 0 && (contentDelta.Length > 0 || reasoningDelta.Length > 0))
                        {
                            acceptedFirstTokenMs = Math.Max(1, (int)watch.ElapsedMilliseconds);
                        }

                        reasoning.Append(reasoningDelta);
                        activity?.ReasoningDelta(reasoningDelta.Length);
                        if (contentDelta.Length > 0)
                        {
                            content.Append(contentDelta);
                            progress?.Report(contentDelta);
                            activity?.PublicDelta(contentDelta.Length);
                        }
                    }
                    catch (JsonException)
                    {
                        sawMalformedEvent = true;
                    }
                }

                watch.Stop();
                var streamedContent = content.ToString().Trim();
                var streamedReasoning = reasoning.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(streamError))
                {
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        string.IsNullOrWhiteSpace(acceptedResponseModel) ? model : acceptedResponseModel,
                        streamedContent,
                        streamedReasoning,
                        (int)watch.ElapsedMilliseconds,
                        acceptedUsage.PromptTokens,
                        acceptedUsage.CompletionTokens,
                        acceptedUsage.TotalTokens,
                        ProviderErrorSanitizer.Sanitize(streamError, config.ApiToken),
                        DateTimeOffset.Now,
                        acceptedTelemetry.TokensPerSecond,
                        acceptedFirstTokenMs,
                        acceptedTelemetry.ResponseId,
                        acceptedTelemetry.ModelLoadTimeMs,
                        StopReason: ModelCompletionStopReason.ProviderError,
                        ProviderStopReason: acceptedProviderStopReason);
                    return CompleteObservation(activeObservationId, failed, "provider_stream_error");
                }

                if (sawMalformedEvent)
                {
                    var failed = new ModelCompletionResult(
                        false,
                        baseUrl,
                        string.IsNullOrWhiteSpace(acceptedResponseModel) ? model : acceptedResponseModel,
                        streamedContent,
                        streamedReasoning,
                        (int)watch.ElapsedMilliseconds,
                        acceptedUsage.PromptTokens,
                        acceptedUsage.CompletionTokens,
                        acceptedUsage.TotalTokens,
                        "Provider stream contained malformed event data; any partial response was preserved.",
                        DateTimeOffset.Now,
                        acceptedTelemetry.TokensPerSecond,
                        acceptedFirstTokenMs,
                        acceptedTelemetry.ResponseId,
                        acceptedTelemetry.ModelLoadTimeMs,
                        StopReason: ModelCompletionStopReason.ProviderError,
                        ProviderStopReason: acceptedProviderStopReason);
                    return CompleteObservation(activeObservationId, failed, "provider_stream_error");
                }

                if (!sawDone && !acceptedSawTerminalStop)
                {
                    var incomplete = new ModelCompletionResult(
                        false,
                        baseUrl,
                        string.IsNullOrWhiteSpace(acceptedResponseModel) ? model : acceptedResponseModel,
                        streamedContent,
                        streamedReasoning,
                        (int)watch.ElapsedMilliseconds,
                        acceptedUsage.PromptTokens,
                        acceptedUsage.CompletionTokens,
                        acceptedUsage.TotalTokens,
                        "Provider stream ended before terminal completion evidence; any partial response was preserved.",
                        DateTimeOffset.Now,
                        acceptedTelemetry.TokensPerSecond,
                        acceptedFirstTokenMs,
                        acceptedTelemetry.ResponseId,
                        acceptedTelemetry.ModelLoadTimeMs,
                        FailureKind: ModelCompletionFailureKind.InvalidResponse,
                        StopReason: ModelCompletionStopReason.ProviderError,
                        ProviderStopReason: acceptedProviderStopReason);
                    return CompleteObservation(activeObservationId, incomplete, "provider_stream_incomplete");
                }

                var completed = new ModelCompletionResult(
                    !string.IsNullOrWhiteSpace(streamedContent),
                    baseUrl,
                    string.IsNullOrWhiteSpace(acceptedResponseModel) ? model : acceptedResponseModel,
                    streamedContent,
                    streamedReasoning,
                    (int)watch.ElapsedMilliseconds,
                    acceptedUsage.PromptTokens,
                    acceptedUsage.CompletionTokens,
                    acceptedUsage.TotalTokens,
                    string.IsNullOrWhiteSpace(streamedContent) ? EmptyCompletionError : "",
                    DateTimeOffset.Now,
                    acceptedTelemetry.TokensPerSecond,
                    acceptedFirstTokenMs,
                    acceptedTelemetry.ResponseId,
                    acceptedTelemetry.ModelLoadTimeMs,
                    StopReason: acceptedStopReason,
                    ProviderStopReason: acceptedProviderStopReason);
                return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            if (acceptedContent is not null)
            {
                _ = CompleteObservation(
                    activeObservationId,
                    AcceptedStreamInterruptionResult(
                        baseUrl,
                        model,
                        acceptedResponseModel,
                        acceptedContent,
                        acceptedReasoning,
                        acceptedUsage,
                        acceptedTelemetry,
                        acceptedFirstTokenMs,
                        (int)watch.ElapsedMilliseconds,
                        "Provider stream was cancelled after acceptance; any partial response was preserved and was not replayed."),
                    "caller_cancelled");
            }
            else
            {
                ObserveUnavailableCompletion(
                    activeObservationId,
                    "caller_cancelled",
                    "The caller cancelled after the provider request began; the physical attempt was not replayed.");
            }
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var failed = acceptedContent is null
                ? new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken), DateTimeOffset.Now)
                : AcceptedStreamInterruptionResult(
                    baseUrl,
                    model,
                    acceptedResponseModel,
                    acceptedContent,
                    acceptedReasoning,
                    acceptedUsage,
                    acceptedTelemetry,
                    acceptedFirstTokenMs,
                    (int)watch.ElapsedMilliseconds,
                    FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken));
            return CompleteObservation(activeObservationId,
                failed with { ProviderStopReason = acceptedProviderStopReason }, FailureObservationOutcome(ex));
        }
    }

    private async Task<ModelCompletionResult> CompleteOllamaNativeChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        bool requestedStreaming,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        ProviderActivityReporter? activity = null)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "" : config.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ModelCompletionResult(false, baseUrl, "", "", "", 0, 0, 0, 0, "No model configured.", DateTimeOffset.Now);
        }

        var payload = OllamaChatPayload(config, messages);
        payload["stream"] = requestedStreaming;
        var payloadBytes = SerializePayload(payload);

        var watch = Stopwatch.StartNew();
        var activeObservationId = "";
        StringBuilder? acceptedContent = null;
        StringBuilder? acceptedReasoning = null;
        try
        {
            var endpoint = new Uri(new Uri(NormalizeOllamaApiBase(config.BaseUrl) + "/"), "chat");
            var idempotencyKey = CompletionIdempotencyKey(config);
            using var timeout = TimeoutToken(config, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                activeObservationId = ObserveRequest(
                    config,
                    payloadBytes,
                    "ollama_native_chat",
                    requestedStreaming,
                    attempt + 1);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = CreateJsonContent(payloadBytes)
                };
                ApplyAuthorization(request, config);
                ApplyCompletionIdempotencyKey(request, idempotencyKey);
                using var response = await _httpClient.SendAsync(
                    request,
                    requestedStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (attempt < MaximumCompletionAttempts - 1
                        && TryAuthorizeCompletionRetry(
                            config,
                            endpoint,
                            response.RequestMessage?.RequestUri,
                            response.StatusCode,
                            body,
                            useLlamaCppBusyBodyPredicates: false,
                            idempotencyKey,
                            out var retryEvidence)
                        && TryResolveRetryDelay(
                            response.Headers,
                            attempt,
                            _timeProvider,
                            _retryJitterUnit,
                            out var retryDelay))
                    {
                        ObserveUnavailableCompletion(
                            activeObservationId,
                            "retryable_provider_failure",
                            retryEvidence);
                        activeObservationId = "";
                        await _retryDelayAsync(retryDelay, timeout.Token);
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
                        DateTimeOffset.Now,
                        ProviderStatusCode: (int)response.StatusCode,
                        ProviderErrorCode: ExtractProviderErrorCode(body, config.ApiToken));
                    return CompleteObservation(activeObservationId, failed, "provider_error");
                }

                if (requestedStreaming)
                {
                    // Successful headers are the acceptance boundary. Never
                    // replay this request, including empty or interrupted streams.
                    acceptedContent = new StringBuilder();
                    acceptedReasoning = new StringBuilder();
                    var streamed = await ReadOllamaNativeStreamAsync(
                        response.Content,
                        config,
                        baseUrl,
                        model,
                        watch,
                        acceptedContent,
                        acceptedReasoning,
                        progress,
                        timeout.Token,
                        activity);
                    watch.Stop();
                    return CompleteObservation(activeObservationId, streamed.Result, streamed.Outcome);
                }

                var completionBody = await response.Content.ReadAsStringAsync(timeout.Token);
                watch.Stop();
                using var completionDocument = JsonDocument.Parse(completionBody);
                var completed = OllamaCompletionFromJson(
                    completionDocument.RootElement, baseUrl, model, (int)watch.ElapsedMilliseconds, apiToken: config.ApiToken);
                return CompleteObservation(activeObservationId, completed, completed.Ok ? "succeeded" : "empty_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            ObserveUnavailableCompletion(
                activeObservationId,
                "caller_cancelled",
                acceptedContent is { Length: > 0 }
                    ? "The caller cancelled after provider acceptance and partial progress; the accepted stream was not replayed."
                    : "The caller cancelled before provider token evidence was available.");
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            watch.Stop();
            var invalidStream = acceptedContent is not null && ex is JsonException;
            var failed = new ModelCompletionResult(
                false,
                baseUrl,
                model,
                acceptedContent?.ToString().Trim() ?? "",
                acceptedReasoning?.ToString().Trim() ?? "",
                (int)watch.ElapsedMilliseconds,
                0,
                0,
                0,
                invalidStream
                    ? "Provider stream contained malformed or oversized Ollama event data; any partial response was preserved."
                    : FriendlyProviderError(ex, baseUrl, config.Timeout, config.ApiMode, config.ApiToken),
                DateTimeOffset.Now,
                FailureKind: invalidStream ? ModelCompletionFailureKind.InvalidResponse : ModelCompletionFailureKind.None);
            return CompleteObservation(activeObservationId, failed,
                invalidStream ? "provider_stream_error" : FailureObservationOutcome(ex));
        }
    }

    private static ModelCompletionResult OllamaCompletionFromJson(
        JsonElement root,
        string baseUrl,
        string model,
        int latencyMs,
        string? content = null,
        string? reasoning = null,
        string apiToken = "")
    {
        var usage = ExtractOllamaUsage(root);
        var telemetry = ExtractOllamaTelemetry(root);
        var text = (content ?? ExtractOllamaChatContent(root)).Trim();
        return new ModelCompletionResult(
            !string.IsNullOrWhiteSpace(text),
            baseUrl,
            ExtractOllamaModel(root, model),
            text,
            (reasoning ?? ExtractOllamaReasoning(root)).Trim(),
            latencyMs,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            string.IsNullOrWhiteSpace(text) ? EmptyCompletionError : "",
            DateTimeOffset.Now,
            telemetry.TokensPerSecond,
            telemetry.TimeToFirstTokenMs,
            telemetry.ResponseId,
            telemetry.ModelLoadTimeMs,
            StopReason: ModelCompletionOutcomeClassifier.ExtractStopReason(root),
            ProviderStopReason: ModelCompletionOutcomeClassifier.ExtractProviderStopReason(root, apiToken));
    }

    private static async Task<(ModelCompletionResult Result, string Outcome)> ReadOllamaNativeStreamAsync(
        HttpContent responseContent,
        ModelProviderConfig config,
        string baseUrl,
        string model,
        Stopwatch watch,
        StringBuilder content,
        StringBuilder reasoning,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        ProviderActivityReporter? activity)
    {
        await using var stream = await responseContent.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var firstTokenMs = 0;
        var sawToolOutput = false;
        await foreach (var line in ReadBoundedOllamaLinesAsync(reader, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException();
                }

                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    var message = ExtractProviderErrorMessage(error);
                    var failed = OllamaCompletionFromJson(
                        root, baseUrl, model, (int)watch.ElapsedMilliseconds, content.ToString(), reasoning.ToString(), config.ApiToken) with
                    {
                        Ok = false,
                        Error = ProviderErrorSanitizer.Sanitize(
                            string.IsNullOrWhiteSpace(message)
                                ? "Provider stream returned an Ollama error."
                                : message,
                            config.ApiToken),
                        StopReason = ModelCompletionStopReason.ProviderError,
                        ProviderErrorCode = ExtractProviderErrorCode(line, config.ApiToken)
                    };
                    return (failed, "provider_stream_error");
                }

                if (!root.TryGetProperty("done", out var done)
                    || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || (root.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind != JsonValueKind.Object))
                {
                    throw new JsonException();
                }

                sawToolOutput |= ModelCompletionOutcomeClassifier.ExtractStopReason(root) == ModelCompletionStopReason.ToolCall;
                var reasoningDelta = ExtractOllamaReasoning(root, preserveWhitespace: true);
                reasoning.Append(reasoningDelta);
                activity?.ReasoningDelta(reasoningDelta.Length);
                var delta = ExtractOllamaChatContent(root, preserveWhitespace: true);
                if (delta.Length > 0)
                {
                    if (firstTokenMs == 0)
                    {
                        firstTokenMs = Math.Max(1, (int)watch.ElapsedMilliseconds);
                    }

                    content.Append(delta);
                    progress?.Report(delta);
                    activity?.PublicDelta(delta.Length);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (done.GetBoolean())
                {
                    var completed = OllamaCompletionFromJson(
                        root, baseUrl, model, (int)watch.ElapsedMilliseconds, content.ToString(), reasoning.ToString(), config.ApiToken);
                    if (sawToolOutput && completed.StopReason is not (ModelCompletionStopReason.ProviderError or ModelCompletionStopReason.ContentFiltered))
                    {
                        completed = completed with { StopReason = ModelCompletionStopReason.ToolCall };
                    }
                    if (completed.TimeToFirstTokenMs == 0 && firstTokenMs > 0)
                    {
                        completed = completed with { TimeToFirstTokenMs = firstTokenMs };
                    }

                    return (completed, completed.Ok ? "succeeded" : "empty_response");
                }
            }
            catch (JsonException)
            {
                return (Failure(
                    "Provider stream contained malformed Ollama event data; any partial response was preserved."),
                    "provider_stream_error");
            }
        }

        return (Failure(
            "Provider stream ended before the required Ollama done event; any partial response was preserved."),
            "provider_stream_incomplete");

        ModelCompletionResult Failure(string error) => new(
            false,
            baseUrl,
            model,
            content.ToString().Trim(),
            reasoning.ToString().Trim(),
            (int)watch.ElapsedMilliseconds,
            0,
            0,
            0,
            error,
            DateTimeOffset.Now,
            FailureKind: ModelCompletionFailureKind.InvalidResponse,
            StopReason: ModelCompletionStopReason.ProviderError);
    }

    private static async IAsyncEnumerable<string> ReadBoundedOllamaLinesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A faulty server must not grow an unterminated NDJSON frame without
        // bound. Read small blocks so complete lines reach progress immediately.
        const int maximumLineCharacters = 1024 * 1024;
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else
                {
                    if (line.Length >= maximumLineCharacters)
                    {
                        throw new JsonException("Provider stream contained an oversized Ollama JSON line.");
                    }

                    line.Append(character);
                }
            }
        }

        if (line.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line.ToString().TrimEnd('\r');
        }
    }
    private sealed class ProviderActivityReporter(IProgress<ModelProviderActivity> observer)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private int publicCharacters;
        private int reasoningCharacters;

        internal void PublicDelta(int length)
        {
            if (length <= 0) return;
            publicCharacters = (int)Math.Min(int.MaxValue, (long)publicCharacters + length);
            Report(ModelProviderActivityStage.Writing);
        }

        internal void ReasoningDelta(int length)
        {
            if (length <= 0) return;
            reasoningCharacters = (int)Math.Min(int.MaxValue, (long)reasoningCharacters + length);
            Report(ModelProviderActivityStage.Thinking);
        }

        internal void NativeStage(string type, JsonElement data)
        {
            if (type is "model_load.start" or "model_load.progress" or "model_load.end")
            {
                Report(ModelProviderActivityStage.Loading, StageProgress(type, data));
            }
            else if (type is "prompt_processing.start" or "prompt_processing.progress" or "prompt_processing.end")
            {
                Report(ModelProviderActivityStage.ReadingContext, StageProgress(type, data));
            }
            else if (type is "reasoning.start" or "reasoning.end")
            {
                Report(ModelProviderActivityStage.Thinking);
            }
        }

        private static double? StageProgress(string type, JsonElement data)
        {
            if (type.EndsWith(".start", StringComparison.Ordinal)) return 0;
            if (type.EndsWith(".end", StringComparison.Ordinal)) return 1;
            return data.TryGetProperty("progress", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var progress)
                && double.IsFinite(progress)
                    ? Math.Clamp(progress, 0, 1)
                    : null;
        }

        private void Report(ModelProviderActivityStage stage, double? progress = null)
        {
            try
            {
                observer.Report(new ModelProviderActivity(
                    stage, DateTimeOffset.UtcNow, watch.ElapsedMilliseconds,
                    publicCharacters, reasoningCharacters, progress));
            }
            catch
            {
                // Optional activity observers cannot change generation or retry behavior.
            }
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
            var preparedObserver = _requestObserver as IPreparedProviderRequestObserver;
            var observationDetail = preparedObserver?.ObservationDetail
                ?? ProviderRequestObservationDetail.RedactedPreview;
            var trace = ProviderPromptInspection.CreateTrace(
                requestId,
                config,
                exactPayload,
                transport,
                requestedStreaming,
                attempt,
                observationDetail);
            if (preparedObserver is not null)
            {
                preparedObserver.ObservePreparedRequest(trace);
            }
            else
            {
                _requestObserver.ObserveRequest(trace);
            }
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
        result = ModelCompletionOutcomeClassifier.Normalize(result);
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
        return ParseModelCatalog(json).Models;
    }

    private static ParsedModelCatalog ParseModelCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            if (!doc.RootElement.TryGetProperty("models", out data) || data.ValueKind != JsonValueKind.Array)
            {
                return new ParsedModelCatalog(Array.Empty<string>(), 0);
            }
        }

        var sourceEntryCount = data.GetArrayLength();
        var inspectedEntryCount = Math.Min(sourceEntryCount, MaximumModelCatalogEntries);
        var models = new List<string>(inspectedEntryCount);
        var inspected = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (inspected++ >= inspectedEntryCount)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            models.Add(FirstString(item, "id", "key", "selected_variant", "model", "name"));
        }

        return new ParsedModelCatalog(
            models.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            Math.Max(0, sourceEntryCount - inspectedEntryCount));
    }

    private readonly record struct ParsedModelCatalog(
        IReadOnlyList<string> Models,
        int OmittedEntryCount);

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

        return FirstString(message.Value, "reasoning", "thinking");
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

    private static string ExtractOllamaChatContent(JsonElement root, bool preserveWhitespace = false)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var content = FirstString(message, "content");
            if (preserveWhitespace ? content.Length > 0 : !string.IsNullOrWhiteSpace(content))
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

    private static string ExtractOllamaReasoning(JsonElement root, bool preserveWhitespace = false)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var thinking = FirstString(message, "thinking", "reasoning", "reasoning_content");
            if (preserveWhitespace ? thinking.Length > 0 : !string.IsNullOrWhiteSpace(thinking))
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

        var effectiveContextWindow = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        if (effectiveContextWindow > 0)
        {
            payload["context_length"] = effectiveContextWindow;
        }

        var reasoning = ModelProviderReasoningModes.Normalize(config.Reasoning);
        if (!string.IsNullOrWhiteSpace(reasoning))
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
        var effectiveContextWindow = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        if (effectiveContextWindow > 0)
        {
            options["num_ctx"] = effectiveContextWindow;
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

    private static bool TryAuthorizeCompletionRetry(
        ModelProviderConfig config,
        Uri requestedEndpoint,
        Uri? responseEndpoint,
        HttpStatusCode statusCode,
        string body,
        bool useLlamaCppBusyBodyPredicates,
        string idempotencyKey,
        out string evidence)
    {
        evidence = "";
        if (!IsRetryableAvailabilitySignal(statusCode, body, useLlamaCppBusyBodyPredicates))
        {
            return false;
        }

        // A fully received 429/503 is delay guidance, not proof that an
        // upstream remote provider did no work. Automatic replay is therefore
        // limited to an explicitly configured end-to-end idempotency-key
        // contract. The effective response authority must still be the one the
        // user configured; an HTTP redirect cannot silently broaden that claim.
        var effectiveEndpoint = responseEndpoint ?? requestedEndpoint;
        if (idempotencyKey.Length > 0
            && SupportsCompletionIdempotencyKey(config)
            && HasSameAuthority(requestedEndpoint, effectiveEndpoint))
        {
            evidence = "Retry evidence: idempotency_key. "
                + "The explicitly configured endpoint contract reuses one idempotency key for this logical completion.";
            return true;
        }

        return false;
    }

    private static bool HasSameAuthority(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool IsRetryableAvailabilitySignal(
        HttpStatusCode statusCode,
        string body,
        bool useLlamaCppBusyBodyPredicates)
    {
        if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return true;
        }

        return useLlamaCppBusyBodyPredicates
            && IsLlamaCppAvailabilitySignal(statusCode, body);
    }

    private static bool IsLlamaCppAvailabilitySignal(HttpStatusCode statusCode, string body)
    {
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

    private static string CompletionIdempotencyKey(ModelProviderConfig config) =>
        SupportsCompletionIdempotencyKey(config)
            ? $"ai-arena-{Guid.NewGuid():N}"
            : "";

    internal static bool SupportsCompletionIdempotencyKey(ModelProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Extra is not null
            && config.Extra.TryGetValue(CompletionIdempotencyCapabilityKey, out var capability)
            && capability.ValueKind == JsonValueKind.True;
    }

    private static void ApplyCompletionIdempotencyKey(HttpRequestMessage request, string idempotencyKey)
    {
        if (idempotencyKey.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
    }

    internal static bool TryResolveRetryDelay(
        HttpResponseHeaders headers,
        int failedAttempt,
        TimeProvider timeProvider,
        Func<int, double> retryJitterUnit,
        out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(retryJitterUnit);

        if (headers.TryGetValues("Retry-After", out var values))
        {
            var rawValue = values.FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(rawValue)
                && TryParseRetryAfter(rawValue, timeProvider.GetUtcNow(), out var serverDelay))
            {
                if (serverDelay > MaximumRetryDelay)
                {
                    // Retrying sooner than the provider requested can amplify
                    // overload or repeat work. A delay outside local policy is
                    // therefore surfaced to the caller without an auto-replay.
                    delay = default;
                    return false;
                }

                delay = serverDelay <= TimeSpan.Zero ? TimeSpan.Zero : serverDelay;
                return true;
            }
        }

        var exponent = Math.Clamp(failedAttempt, 0, 4);
        var baseMilliseconds = Math.Min(
            InitialRetryDelayMilliseconds * (1 << exponent),
            (int)MaximumRetryDelay.TotalMilliseconds);
        var jitterUnit = retryJitterUnit(failedAttempt);
        if (double.IsNaN(jitterUnit) || double.IsInfinity(jitterUnit))
        {
            jitterUnit = 0;
        }

        var jitterMilliseconds = Math.Round(
            Math.Clamp(jitterUnit, 0, 1) * MaximumRetryJitterMilliseconds,
            MidpointRounding.AwayFromZero);
        delay = CapRetryDelay(TimeSpan.FromMilliseconds(baseMilliseconds + jitterMilliseconds));
        return true;
    }

    private static bool TryParseRetryAfter(
        string rawValue,
        DateTimeOffset utcNow,
        out TimeSpan delay)
    {
        if (rawValue.All(character => character is >= '0' and <= '9'))
        {
            // Treat an arbitrarily long digit sequence as a valid but hostile
            // delta rather than overflowing and falling back to a short delay.
            if (!decimal.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds > (decimal)MaximumRetryDelay.TotalSeconds)
            {
                delay = MaximumRetryDelay + TimeSpan.FromTicks(1);
                return true;
            }

            delay = TimeSpan.FromSeconds((double)seconds);
            return true;
        }

        if (DateTimeOffset.TryParse(
            rawValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var retryAt))
        {
            delay = retryAt <= utcNow ? TimeSpan.Zero : retryAt - utcNow;
            return true;
        }

        delay = default;
        return false;
    }

    private static TimeSpan CapRetryDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay >= MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private static string FailureObservationOutcome(Exception exception) => exception switch
    {
        OperationCanceledException => "provider_timeout",
        JsonException => "provider_response_error",
        _ => "transport_error"
    };

    private static ModelCompletionResult LmStudioAcceptedStreamFailureResult(
        string baseUrl,
        string model,
        StringBuilder content,
        StringBuilder? reasoning,
        int latencyMs,
        string streamError,
        bool sawMalformedEvent,
        string apiToken)
    {
        var error = !string.IsNullOrWhiteSpace(streamError)
            ? ProviderErrorSanitizer.Sanitize(streamError, apiToken)
            : sawMalformedEvent
                ? "Provider stream contained malformed LM Studio event data; any partial response was preserved."
                : "Provider stream ended after acceptance; any partial response was preserved.";
        return new ModelCompletionResult(
            false,
            baseUrl,
            model,
            content.ToString().Trim(),
            reasoning?.ToString().Trim() ?? "",
            latencyMs,
            0,
            0,
            0,
            error,
            DateTimeOffset.Now,
            StopReason: ModelCompletionStopReason.ProviderError);
    }

    private static ModelCompletionResult AcceptedStreamInterruptionResult(
        string baseUrl,
        string fallbackModel,
        string responseModel,
        StringBuilder content,
        StringBuilder? reasoning,
        ModelTokenUsage usage,
        ModelProviderTelemetry telemetry,
        int firstTokenMs,
        int latencyMs,
        string error) => new(
            false,
            baseUrl,
            string.IsNullOrWhiteSpace(responseModel) ? fallbackModel : responseModel,
            content.ToString().Trim(),
            reasoning?.ToString().Trim() ?? "",
            latencyMs,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            error,
            DateTimeOffset.Now,
            telemetry.TokensPerSecond,
            firstTokenMs,
            telemetry.ResponseId,
            telemetry.ModelLoadTimeMs,
            StopReason: ModelCompletionStopReason.ProviderError);

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
        var safeBaseUrl = ProviderErrorSanitizer.Endpoint(baseUrl, apiToken);
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

        var message = ProviderErrorSanitizer.Sanitize(ex.Message, apiToken);
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
        var safeBody = ProviderErrorSanitizer.Sanitize(body, apiToken);
        var message = body.Length > ProviderErrorSanitizer.MaximumInputLength || safeBody == ProviderErrorSanitizer.SensitiveError
            ? safeBody : ExtractProviderErrorMessage(body);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(reasonPhrase) ? "HTTP request failed." : reasonPhrase.Trim();
        }

        return $"Provider request failed at {ProviderErrorSanitizer.Endpoint(baseUrl, apiToken)}: {ProviderErrorSanitizer.Sanitize(message, apiToken)}";
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
            return body.Trim();
        }
    }

    public static string ExtractProviderErrorCode(string body, string apiToken = "")
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = FindProviderErrorCode(doc.RootElement, 0);
            if (!string.IsNullOrWhiteSpace(apiToken)
                && code.Contains(apiToken.Trim(), StringComparison.Ordinal))
            {
                return "";
            }

            return ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode(code);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string FindProviderErrorCode(JsonElement value, int depth)
    {
        if (depth > 5)
        {
            return "";
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("code", out var code))
            {
                return code.ValueKind switch
                {
                    JsonValueKind.String => ProviderErrorSanitizer.Compact(code.GetString()?.Trim() ?? ""),
                    JsonValueKind.Number => code.GetRawText(),
                    _ => ""
                };
            }

            foreach (var property in value.EnumerateObject())
            {
                var nested = FindProviderErrorCode(property.Value, depth + 1);
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindProviderErrorCode(item, depth + 1);
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }

        return "";
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

}

public sealed record ModelTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record ModelProviderTelemetry(double TokensPerSecond, int TimeToFirstTokenMs, string ResponseId, int ModelLoadTimeMs = 0);
