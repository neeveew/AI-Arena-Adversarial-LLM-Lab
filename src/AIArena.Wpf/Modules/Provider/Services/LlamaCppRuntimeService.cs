using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

/// <summary>
/// Reads the optional operational endpoints exposed by a user-owned
/// llama-server. AI Arena never starts, replaces, or silently reconfigures the
/// process. Every endpoint is capability-detected because llama.cpp builds and
/// single-model/router modes expose different subsets.
/// </summary>
public sealed partial class LlamaCppRuntimeService
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumModelCount = 256;
    private const int MaximumSlotCount = 256;
    private const int MaximumServerModelMappings = 512;
    private const int MaximumSafeTextLength = 320;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, string> serverModelIdentifiers = new(StringComparer.OrdinalIgnoreCase);

    public LlamaCppRuntimeService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public Task<LlamaCppRuntimeSnapshot> ReconnectAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        // The app does not own llama-server. Reconnect therefore means a fresh,
        // uncached capability inspection rather than a process restart.
        return InspectAsync(config, cancellationToken);
    }

    public async Task<LlamaCppRuntimeSnapshot> InspectAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var checkedAt = DateTimeOffset.Now;
        if (!ModelProviderApiModes.IsLlamaCppNative(config.ApiMode))
        {
            return LlamaCppRuntimeSnapshot.Unavailable(
                "llama.cpp runtime inspection requires llamacpp_native mode.",
                checkedAt);
        }

        string apiRoot;
        string compatibleBase;
        try
        {
            apiRoot = ModelProviderClient.NormalizeLlamaCppApiBase(config.BaseUrl);
            compatibleBase = ModelProviderClient.NormalizeBaseUrl(config.BaseUrl);
            _ = new Uri(apiRoot + "/", UriKind.Absolute);
            _ = new Uri(compatibleBase + "/", UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            return LlamaCppRuntimeSnapshot.Unavailable(
                SafeError($"Invalid llama.cpp server address: {ex.Message}", config.ApiToken),
                checkedAt);
        }

        try
        {
            var warnings = new List<string>();
            var health = await ProbeGetAsync(new Uri(new Uri(apiRoot + "/"), "health"), config, cancellationToken);
            var routerModelsResponse = await ProbeGetAsync(new Uri(new Uri(apiRoot + "/"), "models"), config, cancellationToken);
            var compatibleModelsResponse = await ProbeGetAsync(new Uri(new Uri(compatibleBase + "/"), "models"), config, cancellationToken);

            var models = new Dictionary<string, MutableModel>(StringComparer.OrdinalIgnoreCase);
            var routerOmittedEntryCount = 0;
            var routerMode = routerModelsResponse.Success
                && TryParseModels(
                    routerModelsResponse.Body,
                    models,
                    assumeLoaded: false,
                    requireRouterStatus: true,
                    out routerOmittedEntryCount,
                    "Router model inventory",
                    warnings);
            var compatibleOmittedEntryCount = 0;
            var openAiModelsAvailable = compatibleModelsResponse.Success
                && TryParseModels(
                    compatibleModelsResponse.Body,
                    models,
                    assumeLoaded: true,
                    requireRouterStatus: false,
                    out compatibleOmittedEntryCount,
                    "OpenAI model inventory",
                    warnings);
            var selectedModel = SelectModel(config.Model, models.Values);
            if (serverModelIdentifiers.Count > MaximumServerModelMappings)
            {
                serverModelIdentifiers.Clear();
            }

            foreach (var model in models.Values)
            {
                serverModelIdentifiers[ServerModelKey(apiRoot, model.Id)] = model.ServerId;
            }

            var propsEndpoint = new Uri(new Uri(apiRoot + "/"), "props");
            var slotsEndpoint = new Uri(new Uri(apiRoot + "/"), "slots");
            if (routerMode && !string.IsNullOrWhiteSpace(selectedModel))
            {
                propsEndpoint = WithModelQuery(propsEndpoint, selectedModel);
                slotsEndpoint = WithModelQuery(slotsEndpoint, selectedModel);
            }

            var props = string.IsNullOrWhiteSpace(selectedModel) && routerMode
                ? ProbeResponse.Unsupported("Select a router model before requesting model properties.")
                : await ProbeGetAsync(propsEndpoint, config, cancellationToken);
            var slots = string.IsNullOrWhiteSpace(selectedModel) && routerMode
                ? ProbeResponse.Unsupported("Select a router model before requesting slots.")
                : await ProbeGetAsync(slotsEndpoint, config, cancellationToken);

            var propsInfo = LlamaCppProps.Empty;
            var slotItems = Array.Empty<LlamaCppRuntimeSlot>();
            var propsAvailable = props.Success && TryParseProps(props.Body, out propsInfo, warnings);
            var slotsAvailable = slots.Success && TryParseSlots(slots.Body, out slotItems, warnings);
            var selected = models.TryGetValue(selectedModel, out var selectedEntry) ? selectedEntry : null;
            if (selected is not null)
            {
                selected.ContextLength ??= propsInfo.ContextLength;
                selected.ParallelSlots ??= propsInfo.TotalSlots;
            }

            AddOptionalWarning(warnings, "Router model inventory", routerModelsResponse);
            AddOptionalWarning(warnings, "OpenAI model inventory", compatibleModelsResponse);
            AddOptionalWarning(warnings, "Runtime properties", props);
            AddOptionalWarning(warnings, "Slot telemetry", slots);
            var displayOmittedModelCount = Math.Max(0, models.Count - MaximumModelCount);
            var omittedModelCount = (int)Math.Min(
                int.MaxValue,
                (long)routerOmittedEntryCount + compatibleOmittedEntryCount + displayOmittedModelCount);
            if (omittedModelCount > 0)
            {
                warnings.Add($"{omittedModelCount} model inventory entries were omitted by safety limits.");
            }

            var buildInfo = SafeText(propsInfo.BuildInfo);
            var healthState = ParseHealth(health);
            if (health.Success && !healthState.Valid)
            {
                warnings.Add("Health endpoint returned unreadable JSON.");
            }

            var identified = routerMode
                || ModelsIdentifyLlamaCpp(routerModelsResponse.Body)
                || ModelsIdentifyLlamaCpp(compatibleModelsResponse.Body)
                || !string.IsNullOrWhiteSpace(buildInfo)
                || healthState.IdentifiesLlamaCpp;
            var available = identified && (health.StatusCode.HasValue || props.Success || routerModelsResponse.Success || compatibleModelsResponse.Success);
            var error = available
                ? ""
                : SafeError(FirstUsefulError(health, props, routerModelsResponse, compatibleModelsResponse), config.ApiToken);
            var safeModels = models.Values
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumModelCount)
                .Select(model => model.Freeze())
                .ToArray();
            var safeSlots = slotItems.Take(MaximumSlotCount).ToArray();
            var busySlots = safeSlots.Count(slot => slot.Processing);
            var totalSlots = propsInfo.TotalSlots ?? (safeSlots.Length > 0 ? safeSlots.Length : selected?.ParallelSlots);
            var tokensPerSecond = safeSlots
                .Where(slot => slot.TokensPerSecond is > 0)
                .Select(slot => slot.TokensPerSecond!.Value)
                .DefaultIfEmpty(0)
                .Average();
            var status = healthState.Status.Length > 0
                ? healthState.Status
                : available ? "available" : "unavailable";

            return new LlamaCppRuntimeSnapshot(
                Available: available,
                Ready: available && healthState.Ready,
                IsLlamaCpp: identified,
                Status: status,
                BuildInfo: buildInfo,
                Model: selectedModel,
                Quantization: selected?.Quantization ?? "",
                ContextLength: selected?.ContextLength ?? propsInfo.ContextLength,
                GpuLayers: selected?.GpuLayers,
                SlotCount: totalSlots,
                BusySlots: totalSlots.HasValue || safeSlots.Length > 0 ? busySlots : null,
                Sleeping: propsInfo.Sleeping,
                RouterMode: routerMode,
                Loaded: selected?.Loaded,
                ModelSizeBytes: selected?.ModelSizeBytes,
                MemoryBytes: null,
                ParameterCount: selected?.ParameterCount,
                TokensPerSecond: tokensPerSecond > 0 ? Math.Round(tokensPerSecond, 2) : null,
                Capabilities: new LlamaCppRuntimeCapabilities(
                    Health: health.Supported && health.StatusCode.HasValue,
                    OpenAiModels: openAiModelsAvailable,
                    Props: propsAvailable,
                    Slots: slotsAvailable,
                    RouterModels: routerMode,
                    ModelLifecycle: routerMode,
                    DetailedTelemetry: propsAvailable || slotsAvailable),
                Models: Array.AsReadOnly(safeModels),
                Slots: Array.AsReadOnly(safeSlots),
                Warnings: Array.AsReadOnly(warnings.Select(SafeText).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray()),
                Error: error,
                CheckedAt: checkedAt,
                OmittedModelCount: omittedModelCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or UriFormatException)
        {
            return LlamaCppRuntimeSnapshot.Unavailable(SafeError(FriendlyException(ex), config.ApiToken), checkedAt);
        }
    }

    public Task<LlamaCppRuntimeActionResult> LoadAsync(
        ModelProviderConfig config,
        string model,
        CancellationToken cancellationToken = default)
    {
        return RunLifecycleAsync(config, model, "load", cancellationToken);
    }

    public Task<LlamaCppRuntimeActionResult> UnloadAsync(
        ModelProviderConfig config,
        string model,
        CancellationToken cancellationToken = default)
    {
        return RunLifecycleAsync(config, model, "unload", cancellationToken);
    }

    private async Task<LlamaCppRuntimeActionResult> RunLifecycleAsync(
        ModelProviderConfig config,
        string model,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var checkedAt = DateTimeOffset.Now;
        var requestModel = model?.Trim() ?? "";
        var displayModel = SafeModelId(requestModel);
        if (!ModelProviderApiModes.IsLlamaCppNative(config.ApiMode))
        {
            return LlamaCppRuntimeActionResult.Unsupported(action, displayModel, "llamacpp_native mode is required.", checkedAt);
        }

        if (requestModel.Length is 0 or > 512 || requestModel.Any(char.IsControl))
        {
            return new LlamaCppRuntimeActionResult(true, false, action, "", "invalid model", "Select a valid llama.cpp router model.", checkedAt);
        }

        try
        {
            var apiRoot = ModelProviderClient.NormalizeLlamaCppApiBase(config.BaseUrl);
            var endpoint = new Uri(new Uri(apiRoot + "/"), $"models/{action}");
            var lifecycleModel = serverModelIdentifiers.TryGetValue(ServerModelKey(apiRoot, displayModel), out var serverModel)
                ? serverModel
                : requestModel;
            using var timeout = TimeoutToken(config, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { model = lifecycleModel })
            };
            ProviderHttpHelpers.ApplyAuthorization(request, config.ApiToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = await ReadBoundedBodyAsync(response, timeout.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return LlamaCppRuntimeActionResult.Unsupported(
                    action,
                    displayModel,
                    "This llama-server build does not expose router model lifecycle endpoints.",
                    checkedAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new LlamaCppRuntimeActionResult(
                    true,
                    false,
                    action,
                    displayModel,
                    $"{action} failed",
                    SafeError(ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, $"llama.cpp model {action} failed.", "error", "message", "detail"), config.ApiToken),
                    checkedAt);
            }

            var ok = ParseSuccess(body);
            return new LlamaCppRuntimeActionResult(
                true,
                ok,
                action,
                displayModel,
                ok ? $"model {action}ed" : $"{action} was not confirmed",
                ok ? "" : "llama-server returned success HTTP status without confirming the lifecycle action.",
                checkedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or UriFormatException)
        {
            return new LlamaCppRuntimeActionResult(true, false, action, displayModel, $"{action} failed", SafeError(FriendlyException(ex), config.ApiToken), checkedAt);
        }
    }

    private async Task<ProbeResponse> GetAsync(Uri endpoint, ModelProviderConfig config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ProviderHttpHelpers.ApplyAuthorization(request, config.ApiToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await ReadBoundedBodyAsync(response, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return ProbeResponse.Unsupported("Endpoint is not exposed by this llama-server build.");
        }

        if (response.IsSuccessStatusCode)
        {
            return new ProbeResponse(true, true, response.StatusCode, body, "");
        }

        var error = ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, "llama.cpp runtime request failed.", "error", "message", "detail");
        return new ProbeResponse(false, true, response.StatusCode, body, SafeError(error, config.ApiToken));
    }

    private async Task<ProbeResponse> ProbeGetAsync(
        Uri endpoint,
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        using var timeout = ProbeTimeoutToken(config, cancellationToken);
        try
        {
            return await GetAsync(endpoint, config, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            // Operational endpoints are independently optional. A transport
            // failure, per-probe timeout, or bounded-body rejection must not
            // erase evidence already obtained from the other endpoints.
            return ProbeResponse.Failed(SafeError(FriendlyException(ex), config.ApiToken));
        }
    }

    private static bool ParseModels(
        string json,
        IDictionary<string, MutableModel> target,
        bool assumeLoaded,
        bool requireRouterStatus,
        out int omittedEntryCount)
    {
        omittedEntryCount = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        omittedEntryCount = Math.Max(0, data.GetArrayLength() - MaximumModelCount);
        var foundRouterStatus = false;
        foreach (var item in data.EnumerateArray().Take(MaximumModelCount))
        {
            var serverId = ProviderHttpHelpers.FirstString(item, "id", "model", "name").Trim();
            var id = SafeModelId(serverId);
            if (string.IsNullOrWhiteSpace(id)
                || serverId.Length > 512
                || serverId.Any(char.IsControl))
            {
                continue;
            }

            if (!target.TryGetValue(id, out var model))
            {
                model = new MutableModel(id, serverId);
                target[id] = model;
            }

            if (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                foundRouterStatus = true;
                model.Status = ProviderHttpHelpers.FirstString(status, "value", "status");
                model.Loaded = model.Status.Equals("loaded", StringComparison.OrdinalIgnoreCase)
                    || model.Status.Equals("sleeping", StringComparison.OrdinalIgnoreCase);
                if (status.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var values = args.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? "")
                        .Take(256)
                        .ToArray();
                    model.ContextLength ??= ParseIntArgument(values, "-c", "-ctx", "--ctx-size");
                    model.GpuLayers ??= ParseGpuLayers(values);
                    model.ParallelSlots ??= ParseIntArgument(values, "-np", "--parallel");
                }
            }
            else if (assumeLoaded)
            {
                model.Status = string.IsNullOrWhiteSpace(model.Status) ? "loaded" : model.Status;
                model.Loaded = true;
            }

            if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                model.ContextLength ??= NullableInt(meta, "n_ctx", "n_ctx_train");
                model.ModelSizeBytes ??= NullableInt64(meta, "size", "size_bytes");
                model.ParameterCount ??= NullableInt64(meta, "n_params", "parameter_count");
            }

            model.Quantization = string.IsNullOrWhiteSpace(model.Quantization) ? InferQuantization(id) : model.Quantization;
        }

        return !requireRouterStatus || foundRouterStatus;
    }

    private static bool TryParseModels(
        string json,
        IDictionary<string, MutableModel> target,
        bool assumeLoaded,
        bool requireRouterStatus,
        out int omittedEntryCount,
        string label,
        ICollection<string> warnings)
    {
        omittedEntryCount = 0;
        try
        {
            return ParseModels(
                json,
                target,
                assumeLoaded,
                requireRouterStatus,
                out omittedEntryCount);
        }
        catch (JsonException)
        {
            omittedEntryCount = 0;
            warnings.Add($"{label} returned unreadable JSON.");
            return false;
        }
    }

    private static LlamaCppProps ParseProps(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int? contextLength = null;
        if (root.TryGetProperty("default_generation_settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            contextLength = NullableInt(settings, "n_ctx");
        }

        return new LlamaCppProps(
            ContextLength: contextLength,
            TotalSlots: NullableInt(root, "total_slots"),
            BuildInfo: ProviderHttpHelpers.FirstString(root, "build_info", "version"),
            Sleeping: NullableBool(root, "is_sleeping", "sleeping"));
    }

    private static bool TryParseProps(
        string json,
        out LlamaCppProps props,
        ICollection<string> warnings)
    {
        try
        {
            props = ParseProps(json);
            return true;
        }
        catch (JsonException)
        {
            props = LlamaCppProps.Empty;
            warnings.Add("Runtime properties returned unreadable JSON.");
            return false;
        }
    }

    private static LlamaCppRuntimeSlot[] ParseSlots(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Take(MaximumSlotCount)
            .Select(item =>
            {
                var timings = item.TryGetProperty("timings", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value
                    : default;
                var nextToken = item.TryGetProperty("next_token", out var next) && next.ValueKind == JsonValueKind.Object
                    ? next
                    : default;
                return new LlamaCppRuntimeSlot(
                    Id: NullableInt(item, "id", "id_slot") ?? -1,
                    Processing: NullableBool(item, "is_processing") == true,
                    ContextLength: NullableInt(item, "n_ctx"),
                    DecodedTokens: nextToken.ValueKind == JsonValueKind.Object ? NullableInt(nextToken, "n_decoded") : null,
                    TokensPerSecond: timings.ValueKind == JsonValueKind.Object
                        ? NullableDouble(timings, "predicted_per_second", "tokens_per_second")
                        : null);
            })
            .ToArray();
    }

    private static bool TryParseSlots(
        string json,
        out LlamaCppRuntimeSlot[] slots,
        ICollection<string> warnings)
    {
        try
        {
            slots = ParseSlots(json);
            return true;
        }
        catch (JsonException)
        {
            slots = [];
            warnings.Add("Slot telemetry returned unreadable JSON.");
            return false;
        }
    }

    private static HealthState ParseHealth(ProbeResponse health)
    {
        if (health.Success)
        {
            try
            {
                using var document = JsonDocument.Parse(health.Body);
                var status = ProviderHttpHelpers.FirstString(document.RootElement, "status");
                var normalized = SafeText(status);
                return new HealthState(
                    Ready: normalized.Length == 0 || normalized.Equals("ok", StringComparison.OrdinalIgnoreCase),
                    Status: normalized.Length == 0 ? "ready" : normalized,
                    IdentifiesLlamaCpp: normalized.Equals("ok", StringComparison.OrdinalIgnoreCase),
                    Valid: true);
            }
            catch (JsonException)
            {
                return new HealthState(false, "unreadable", false, false);
            }
        }

        if (health.StatusCode == HttpStatusCode.ServiceUnavailable
            && (health.Error.Contains("load", StringComparison.OrdinalIgnoreCase)
                || health.Body.Contains("unavailable_error", StringComparison.OrdinalIgnoreCase)))
        {
            return new HealthState(false, "loading", true, true);
        }

        return new HealthState(false, "unavailable", false, true);
    }

    private static bool ModelsIdentifyLlamaCpp(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return data.EnumerateArray().Any(item =>
            {
                var owner = ProviderHttpHelpers.FirstString(item, "owned_by");
                return owner.Equals("llamacpp", StringComparison.OrdinalIgnoreCase)
                    || owner.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SelectModel(string configuredModel, IEnumerable<MutableModel> models)
    {
        var configured = SafeModelId(configuredModel);
        var values = models.ToArray();
        if (configured.Length > 0 && values.Any(model => model.Id.Equals(configured, StringComparison.OrdinalIgnoreCase)))
        {
            return values.First(model => model.Id.Equals(configured, StringComparison.OrdinalIgnoreCase)).Id;
        }

        return values.FirstOrDefault(model => model.Loaded == true)?.Id
            ?? values.FirstOrDefault()?.Id
            ?? configured;
    }

    private static Uri WithModelQuery(Uri endpoint, string model)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = $"model={Uri.EscapeDataString(model)}&autoload=false"
        };
        return builder.Uri;
    }

    private static int? ParseGpuLayers(IReadOnlyList<string> args)
    {
        var value = ParseArgument(args, "-ngl", "--gpu-layers", "--n-gpu-layers");
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static int? ParseIntArgument(IReadOnlyList<string> args, params string[] names)
    {
        var value = ParseArgument(args, names);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string ParseArgument(IReadOnlyList<string> args, params string[] names)
    {
        for (var index = 0; index < args.Count; index++)
        {
            foreach (var name in names)
            {
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                {
                    return args[index + 1];
                }

                var prefix = name + "=";
                if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index][prefix.Length..];
                }
            }
        }

        return "";
    }

    private static string InferQuantization(string model)
    {
        var match = QuantizationRegex().Match(model);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static bool ParseSuccess(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind is JsonValueKind.True or JsonValueKind.False
            && success.GetBoolean();
    }

    private static int? NullableInt(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static long? NullableInt64(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static double? NullableDouble(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var result))
            {
                return Math.Round(result, 2);
            }
        }

        return null;
    }

    private static bool? NullableBool(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static void AddOptionalWarning(List<string> warnings, string label, ProbeResponse response)
    {
        if (response.Success)
        {
            return;
        }

        warnings.Add(response.Supported
            ? $"{label} unavailable: {response.Error}"
            : $"{label} is not exposed by this llama-server build.");
    }

    private static string FirstUsefulError(params ProbeResponse[] responses)
    {
        return responses.Select(response => response.Error).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error))
            ?? "The configured endpoint did not identify itself as llama-server.";
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > MaximumResponseBytes)
        {
            throw new InvalidDataException($"llama.cpp response exceeded the {MaximumResponseBytes / 1024} KiB safety limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = MaximumResponseBytes + 1 - checked((int)output.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > MaximumResponseBytes)
            {
                throw new InvalidDataException($"llama.cpp response exceeded the {MaximumResponseBytes / 1024} KiB safety limit.");
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static CancellationTokenSource TimeoutToken(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Timeout, 1, 30)));
        return timeout;
    }

    private static CancellationTokenSource ProbeTimeoutToken(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds(config.Timeout)));
        return timeout;
    }

    internal static int ProbeTimeoutSeconds(int configuredTimeoutSeconds)
    {
        // Five sequential capability probes must remain responsive even when a
        // provider's generation timeout is configured in minutes.
        return Math.Clamp(configuredTimeoutSeconds, 1, 5);
    }

    private static string FriendlyException(Exception ex)
    {
        return ex switch
        {
            OperationCanceledException => "Timed out while inspecting llama-server.",
            UriFormatException => "The configured llama.cpp server address is invalid.",
            InvalidDataException => ex.Message,
            _ => $"llama.cpp runtime request failed: {ex.Message}"
        };
    }

    private static string SafeError(string value, string apiToken)
    {
        return SafeText(ProviderConfigurationControlService.SanitizeError(value, apiToken));
    }

    private static string SafeText(string value)
    {
        var normalized = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumSafeTextLength
            ? normalized
            : normalized[..(MaximumSafeTextLength - 3)].TrimEnd() + "...";
    }

    private static string SafeModelId(string value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0)
        {
            return "";
        }

        if (Path.IsPathRooted(normalized)
            || Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var fileName = Path.GetFileName(normalized.Replace('/', Path.DirectorySeparatorChar));
            return SafeText(string.IsNullOrWhiteSpace(fileName) ? "local model" : fileName);
        }

        return SafeText(normalized);
    }

    private static string ServerModelKey(string apiRoot, string model)
    {
        return $"{apiRoot.TrimEnd('/')}\n{SafeModelId(model)}";
    }

    [GeneratedRegex(@"(?i)(?:^|[-_:.])((?:IQ|Q)\d(?:_[A-Z0-9]+)+)(?:$|[-_:.])", RegexOptions.CultureInvariant)]
    private static partial Regex QuantizationRegex();

    private sealed record ProbeResponse(bool Success, bool Supported, HttpStatusCode? StatusCode, string Body, string Error)
    {
        public static ProbeResponse Unsupported(string error) => new(false, false, null, "", error);

        public static ProbeResponse Failed(string error) => new(false, true, null, "", error);
    }

    private sealed record LlamaCppProps(int? ContextLength, int? TotalSlots, string BuildInfo, bool? Sleeping)
    {
        public static LlamaCppProps Empty { get; } = new(null, null, "", null);
    }

    private sealed record HealthState(bool Ready, string Status, bool IdentifiesLlamaCpp, bool Valid);

    private sealed class MutableModel(string id, string serverId)
    {
        public string Id { get; } = SafeText(id);
        public string ServerId { get; } = serverId;
        public string Status { get; set; } = "";
        public string Quantization { get; set; } = "";
        public int? ContextLength { get; set; }
        public int? GpuLayers { get; set; }
        public int? ParallelSlots { get; set; }
        public bool? Loaded { get; set; }
        public long? ModelSizeBytes { get; set; }
        public long? ParameterCount { get; set; }

        public LlamaCppRuntimeModel Freeze()
        {
            return new LlamaCppRuntimeModel(
                Id,
                SafeText(Status),
                Quantization,
                ContextLength,
                GpuLayers,
                ParallelSlots,
                Loaded,
                ModelSizeBytes,
                ParameterCount);
        }
    }
}

public sealed record LlamaCppRuntimeCapabilities(
    bool Health,
    bool OpenAiModels,
    bool Props,
    bool Slots,
    bool RouterModels,
    bool ModelLifecycle,
    bool DetailedTelemetry);

public sealed record LlamaCppRuntimeModel(
    string Id,
    string Status,
    string Quantization,
    int? ContextLength,
    int? GpuLayers,
    int? ParallelSlots,
    bool? Loaded,
    long? ModelSizeBytes,
    long? ParameterCount);

public sealed record LlamaCppRuntimeSlot(
    int Id,
    bool Processing,
    int? ContextLength,
    int? DecodedTokens,
    double? TokensPerSecond);

public sealed record LlamaCppRuntimeSnapshot(
    bool Available,
    bool Ready,
    bool IsLlamaCpp,
    string Status,
    string BuildInfo,
    string Model,
    string Quantization,
    int? ContextLength,
    int? GpuLayers,
    int? SlotCount,
    int? BusySlots,
    bool? Sleeping,
    bool RouterMode,
    bool? Loaded,
    long? ModelSizeBytes,
    long? MemoryBytes,
    long? ParameterCount,
    double? TokensPerSecond,
    LlamaCppRuntimeCapabilities Capabilities,
    IReadOnlyList<LlamaCppRuntimeModel> Models,
    IReadOnlyList<LlamaCppRuntimeSlot> Slots,
    IReadOnlyList<string> Warnings,
    string Error,
    DateTimeOffset CheckedAt,
    int OmittedModelCount = 0)
{
    public static LlamaCppRuntimeSnapshot Unavailable(string error, DateTimeOffset checkedAt)
    {
        return new LlamaCppRuntimeSnapshot(
            false,
            false,
            false,
            "unavailable",
            "",
            "",
            "",
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            new LlamaCppRuntimeCapabilities(false, false, false, false, false, false, false),
            Array.AsReadOnly(Array.Empty<LlamaCppRuntimeModel>()),
            Array.AsReadOnly(Array.Empty<LlamaCppRuntimeSlot>()),
            Array.AsReadOnly(Array.Empty<string>()),
            error,
            checkedAt);
    }
}

public sealed record LlamaCppRuntimeActionResult(
    bool Supported,
    bool Ok,
    string Action,
    string Model,
    string Status,
    string Error,
    DateTimeOffset CheckedAt)
{
    public static LlamaCppRuntimeActionResult Unsupported(string action, string model, string error, DateTimeOffset checkedAt)
    {
        return new LlamaCppRuntimeActionResult(false, false, action, model, "unsupported", error, checkedAt);
    }
}
