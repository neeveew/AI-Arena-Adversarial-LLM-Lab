using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public sealed class ModelPreloadService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    private readonly HttpClient httpClient;
    private readonly LmStudioModelCatalogService catalogService;

    public ModelPreloadService(
        HttpClient? httpClient = null,
        LmStudioModelCatalogService? catalogService = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
        this.catalogService = catalogService ?? new LmStudioModelCatalogService(this.httpClient);
    }

    public async Task<IReadOnlyList<ModelPreloadResult>> PreloadAsync(
        string providerBaseUrl,
        IEnumerable<string> selectedModels,
        string apiMode = ModelProviderApiModes.LmStudioNative,
        string apiToken = "",
        int contextLength = 0,
        int nativeIdleTtlSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var models = NormalizeSelectedModels(selectedModels);

        if (models.Length == 0)
        {
            return [new ModelPreloadResult("", "skipped", "No selected models to preload.", false)];
        }

        var normalizedApiMode = ModelProviderApiModes.Normalize(apiMode);
        if (normalizedApiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            return await PreloadOllamaAsync(providerBaseUrl, models, apiToken, contextLength, nativeIdleTtlSeconds, cancellationToken);
        }

        if (!normalizedApiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return models
                .Select(model => new ModelPreloadResult(model, "unsupported", "Model preload uses native provider lifecycle endpoints. Switch API mode to LM Studio native or Ollama native.", true))
                .ToArray();
        }

        var apiBase = LmStudioModelCatalogService.NormalizeLmStudioApiBase(providerBaseUrl);
        var catalog = await catalogService.TryLoadAsync(providerBaseUrl, apiToken, cancellationToken);
        if (!catalog.Ok)
        {
            return models
                .Select(model => new ModelPreloadResult(model, "unsupported", catalog.Error, true))
                .ToArray();
        }

        var results = new List<ModelPreloadResult>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = catalog.Find(model);
            var loadModel = entry?.PreferredIdentifier ?? model;
            var effectiveContextLength = EffectiveContextLength(entry, contextLength);
            var effectiveNativeIdleTtlSeconds = NormalizeNativeIdleTtlSeconds(nativeIdleTtlSeconds);
            if (entry?.Loaded == true && IsLoadedWithRequestedContext(entry, effectiveContextLength))
            {
                results.Add(new ModelPreloadResult(model, "ready", AlreadyLoadedDetail(entry, contextLength, effectiveContextLength), false));
                continue;
            }

            if (entry?.Loaded == true && ShouldReloadForRequestedContext(entry, effectiveContextLength))
            {
                var unloadResults = new List<ModelPreloadResult>();
                if (!TryLoadedInstanceIds(entry, model, out var instanceIds))
                {
                    results.Add(new ModelPreloadResult(model, "failed", MissingLoadedInstanceIdDetail(entry), true));
                    continue;
                }

                foreach (var instanceId in instanceIds)
                {
                    unloadResults.Add(await UnloadModelAsync(apiBase, model, instanceId, apiToken, cancellationToken));
                }

                var unloadFailures = unloadResults.Where(result => result.IsFailure).ToArray();
                if (unloadFailures.Length > 0)
                {
                    results.Add(new ModelPreloadResult(model, "failed", $"Could not unload low-context instance before reload. {string.Join(" ", unloadFailures.Select(result => result.Detail))}", true));
                    continue;
                }

                var loadResult = await LoadModelAsync(apiBase, model, loadModel, apiToken, effectiveContextLength, effectiveNativeIdleTtlSeconds, cancellationToken);
                results.Add(loadResult.IsFailure
                    ? ApplyContextCapDetail(loadResult, entry, contextLength, effectiveContextLength)
                    : loadResult with
                    {
                        Status = "reloaded",
                        Detail = $"{ReloadedContextDetail(entry, contextLength, effectiveContextLength)} {loadResult.Detail}"
                    });
                continue;
            }

            var result = await LoadModelAsync(apiBase, model, loadModel, apiToken, effectiveContextLength, effectiveNativeIdleTtlSeconds, cancellationToken);
            results.Add(ApplyContextCapDetail(result, entry, contextLength, effectiveContextLength));
        }

        return results;
    }

    public async Task<IReadOnlyList<ModelPreloadResult>> UnloadAsync(
        string providerBaseUrl,
        IEnumerable<string> selectedModels,
        string apiMode = ModelProviderApiModes.LmStudioNative,
        string apiToken = "",
        CancellationToken cancellationToken = default)
    {
        var models = NormalizeSelectedModels(selectedModels);
        if (models.Length == 0)
        {
            return [new ModelPreloadResult("", "skipped", "No selected models to unload.", false)];
        }

        var normalizedApiMode = ModelProviderApiModes.Normalize(apiMode);
        if (normalizedApiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
        {
            return await UnloadOllamaAsync(providerBaseUrl, models, apiToken, cancellationToken);
        }

        if (!normalizedApiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return models
                .Select(model => new ModelPreloadResult(model, "unsupported", "Model unload uses native provider lifecycle endpoints. Switch API mode to LM Studio native or Ollama native.", true))
                .ToArray();
        }

        var apiBase = LmStudioModelCatalogService.NormalizeLmStudioApiBase(providerBaseUrl);
        var catalog = await catalogService.TryLoadAsync(providerBaseUrl, apiToken, cancellationToken);
        if (!catalog.Ok)
        {
            return models
                .Select(model => new ModelPreloadResult(model, "unsupported", catalog.Error, true))
                .ToArray();
        }

        var results = new List<ModelPreloadResult>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = catalog.Find(model);
            if (entry is null)
            {
                results.Add(new ModelPreloadResult(model, "missing", "Model was not found in LM Studio's native catalog.", true));
                continue;
            }

            if (!entry.Loaded)
            {
                results.Add(new ModelPreloadResult(model, "not loaded", "Model is already unloaded in LM Studio.", false));
                continue;
            }

            var unloadResults = new List<ModelPreloadResult>();
            if (!TryLoadedInstanceIds(entry, model, out var instanceIds))
            {
                results.Add(new ModelPreloadResult(model, "failed", MissingLoadedInstanceIdDetail(entry), true));
                continue;
            }

            foreach (var instanceId in instanceIds)
            {
                unloadResults.Add(await UnloadModelAsync(apiBase, model, instanceId, apiToken, cancellationToken));
            }

            var failures = unloadResults.Where(result => result.IsFailure).ToArray();
            results.Add(failures.Length == 0
                ? new ModelPreloadResult(model, "unloaded", $"Unloaded {unloadResults.Count} instance(s) from LM Studio.", false)
                : new ModelPreloadResult(model, "failed", string.Join(" ", failures.Select(result => result.Detail)), true));
        }

        return results;
    }

    private async Task<IReadOnlyList<ModelPreloadResult>> PreloadOllamaAsync(
        string providerBaseUrl,
        IReadOnlyList<string> models,
        string apiToken,
        int contextLength,
        int nativeIdleTtlSeconds,
        CancellationToken cancellationToken)
    {
        var apiBase = ModelProviderClient.NormalizeOllamaApiBase(providerBaseUrl);
        var results = new List<ModelPreloadResult>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveKeepAlive = NormalizeNativeIdleTtlSeconds(nativeIdleTtlSeconds);
            results.Add(await RunOllamaLifecycleAsync(
                apiBase,
                model,
                apiToken,
                keepAlive: effectiveKeepAlive > 0 ? effectiveKeepAlive : null,
                contextLength,
                loadedStatus: "loaded",
                loadedDetailPrefix: "Kept alive in Ollama",
                cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<ModelPreloadResult>> UnloadOllamaAsync(
        string providerBaseUrl,
        IReadOnlyList<string> models,
        string apiToken,
        CancellationToken cancellationToken)
    {
        var apiBase = ModelProviderClient.NormalizeOllamaApiBase(providerBaseUrl);
        var results = new List<ModelPreloadResult>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOllamaLifecycleAsync(
                apiBase,
                model,
                apiToken,
                keepAlive: 0,
                contextLength: 0,
                loadedStatus: "unloaded",
                loadedDetailPrefix: "Released from Ollama",
                cancellationToken));
        }

        return results;
    }

    private async Task<ModelPreloadResult> RunOllamaLifecycleAsync(
        string apiBase,
        string model,
        string apiToken,
        int? keepAlive,
        int contextLength,
        string loadedStatus,
        string loadedDetailPrefix,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["stream"] = false
        };
        if (keepAlive.HasValue)
        {
            payload["keep_alive"] = keepAlive.Value;
        }

        if (contextLength > 0)
        {
            payload["options"] = new Dictionary<string, object>
            {
                ["num_ctx"] = contextLength
            };
        }

        try
        {
            var endpoint = new Uri(new Uri(apiBase + "/"), "generate");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelPreloadResult(model, "failed", ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, "Native model lifecycle request failed.", "message", "error", "detail"), true);
            }

            var loadMs = ExtractOllamaLoadMilliseconds(body);
            var elapsed = loadMs > 0
                ? $"{loadMs / 1000d:0.#}s"
                : $"{(DateTimeOffset.Now - startedAt).TotalSeconds:0.#}s";
            var contextDetail = contextLength > 0 ? $" Context target: {contextLength:n0} tokens." : "";
            var ttlDetail = keepAlive is > 0 ? $" Keep-alive: {keepAlive.Value:n0}s." : "";
            return new ModelPreloadResult(model, loadedStatus, $"{loadedDetailPrefix} in {elapsed}.{contextDetail}{ttlDetail}", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return new ModelPreloadResult(model, "failed", FriendlyException(ex), true);
        }
    }

    private async Task<ModelPreloadResult> LoadModelAsync(
        string apiBase,
        string selectedModel,
        string loadModel,
        string apiToken,
        int contextLength,
        int nativeIdleTtlSeconds,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(apiBase + "/"), "models/load");
        var startedAt = DateTimeOffset.Now;
        var payload = new Dictionary<string, object>
        {
            ["model"] = loadModel,
            ["echo_load_config"] = true
        };
        if (contextLength > 0)
        {
            payload["context_length"] = contextLength;
        }

        if (nativeIdleTtlSeconds > 0)
        {
            payload["ttl"] = nativeIdleTtlSeconds;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelPreloadResult(selectedModel, "failed", ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, "Native model lifecycle request failed.", "message", "error", "detail"), true);
            }

            var seconds = ExtractLoadSeconds(body);
            var elapsed = seconds > 0
                ? $"{seconds:0.#}s"
                : $"{(DateTimeOffset.Now - startedAt).TotalSeconds:0.#}s";
            return new ModelPreloadResult(selectedModel, "loaded", $"Loaded in {elapsed}.", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ModelPreloadResult(selectedModel, "failed", FriendlyException(ex), true);
        }
    }

    private async Task<ModelPreloadResult> UnloadModelAsync(
        string apiBase,
        string selectedModel,
        string instanceId,
        string apiToken,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(apiBase + "/"), "models/unload");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { instance_id = instanceId })
            };
            ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelPreloadResult(selectedModel, "failed", ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, "Native model lifecycle request failed.", "message", "error", "detail"), true);
            }

            return new ModelPreloadResult(selectedModel, "unloaded", $"Unloaded instance {instanceId}.", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ModelPreloadResult(selectedModel, "failed", FriendlyException(ex), true);
        }
    }

    private static string[] NormalizeSelectedModels(IEnumerable<string> selectedModels)
    {
        return selectedModels
            .Select(model => model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLoadedWithRequestedContext(LmStudioModelInfo model, int contextLength)
    {
        return contextLength <= 0
            || (model.LoadedContextLength is int loadedContext && loadedContext >= contextLength);
    }

    private static bool ShouldReloadForRequestedContext(LmStudioModelInfo model, int contextLength)
    {
        return contextLength > 0
            && (model.LoadedContextLength is not int loadedContext || loadedContext < contextLength);
    }

    private static int EffectiveContextLength(LmStudioModelInfo? model, int requestedContextLength)
    {
        if (requestedContextLength <= 0)
        {
            return 0;
        }

        return model?.MaxContextLength is int maxContext && maxContext > 0
            ? Math.Min(requestedContextLength, maxContext)
            : requestedContextLength;
    }

    private static int NormalizeNativeIdleTtlSeconds(int nativeIdleTtlSeconds)
    {
        return Math.Clamp(nativeIdleTtlSeconds, 0, 86400);
    }

    private static bool TryLoadedInstanceIds(LmStudioModelInfo model, string selectedModel, out string[] instanceIds)
    {
        instanceIds = model.LoadedInstances
            .Select(instance => instance.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (instanceIds.Length > 0)
        {
            return true;
        }

        if (model.LoadedInstances.Count > 0)
        {
            return false;
        }

        var fallbackId = string.IsNullOrWhiteSpace(model.PreferredIdentifier)
            ? selectedModel.Trim()
            : model.PreferredIdentifier;
        instanceIds = string.IsNullOrWhiteSpace(fallbackId) ? [] : [fallbackId];
        return instanceIds.Length > 0;
    }

    private static string MissingLoadedInstanceIdDetail(LmStudioModelInfo model)
    {
        return $"LM Studio reports {model.DisplayTitle} as loaded but did not provide a loaded instance id. Refresh the model catalog or reload the model in LM Studio before unloading.";
    }

    private static double ExtractLoadSeconds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("load_time_seconds", out var seconds)
            && seconds.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private static int ExtractOllamaLoadMilliseconds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("load_duration", out var loadDuration)
            && loadDuration.ValueKind == JsonValueKind.Number
            && loadDuration.TryGetDouble(out var value)
            ? Math.Max(0, (int)Math.Round(value / 1_000_000d))
            : 0;
    }

    private static string AlreadyLoadedDetail(LmStudioModelInfo model, int requestedContextLength, int effectiveContextLength)
    {
        var capDetail = ContextCapDetail(model, requestedContextLength, effectiveContextLength);
        if (model.LoadedContextLength is int context)
        {
            return JoinDetail(capDetail, $"Already loaded in LM Studio with {context:n0} token context.");
        }

        return JoinDetail(capDetail, "Already loaded in LM Studio.");
    }

    private static string ReloadedContextDetail(LmStudioModelInfo model, int requestedContextLength, int effectiveContextLength)
    {
        var target = effectiveContextLength > 0 ? effectiveContextLength : requestedContextLength;
        var capDetail = ContextCapDetail(model, requestedContextLength, effectiveContextLength);
        var reloadDetail = model.LoadedContextLength is int loadedContext
            ? $"Reloaded from {loadedContext:n0} to {target:n0} token context."
            : $"Reloaded from unknown context to {target:n0} token context.";
        return JoinDetail(capDetail, reloadDetail);
    }

    private static ModelPreloadResult ApplyContextCapDetail(
        ModelPreloadResult result,
        LmStudioModelInfo? model,
        int requestedContextLength,
        int effectiveContextLength)
    {
        var capDetail = ContextCapDetail(model, requestedContextLength, effectiveContextLength);
        return string.IsNullOrWhiteSpace(capDetail)
            ? result
            : result with { Detail = JoinDetail(capDetail, result.Detail) };
    }

    private static string ContextCapDetail(LmStudioModelInfo? model, int requestedContextLength, int effectiveContextLength)
    {
        if (requestedContextLength <= 0 || effectiveContextLength <= 0 || requestedContextLength == effectiveContextLength)
        {
            return "";
        }

        return model?.LoadedContextLength is int
            ? $"Requested {requestedContextLength:n0} token context exceeds model max; using {effectiveContextLength:n0}."
            : $"Requested {requestedContextLength:n0} token context exceeds advertised model max; using {effectiveContextLength:n0}.";
    }

    private static string JoinDetail(params string[] parts)
    {
        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FriendlyException(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return "Timed out while asking the native provider to load or unload the model.";
        }

        if (ex is UriFormatException)
        {
            return "Invalid native provider base URL.";
        }

        return ex.Message;
    }
}

public sealed record ModelPreloadResult(string Model, string Status, string Detail, bool IsFailure);
