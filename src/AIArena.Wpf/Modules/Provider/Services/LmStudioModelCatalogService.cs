using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AIArena.Wpf.Services;

public class LmStudioModelCatalogService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private readonly HttpClient httpClient;

    public LmStudioModelCatalogService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public virtual async Task<LmStudioModelCatalog> TryLoadAsync(
        string providerBaseUrl,
        CancellationToken cancellationToken = default)
    {
        return await TryLoadAsync(providerBaseUrl, "", cancellationToken);
    }

    public virtual async Task<LmStudioModelCatalog> TryLoadAsync(
        string providerBaseUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var apiBase = NormalizeLmStudioApiBase(providerBaseUrl);
            var endpoint = new Uri(new Uri(apiBase + "/", UriKind.Absolute), "models");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return LmStudioModelCatalog.Failed(ProviderConfigurationControlService.SanitizeError(
                    ProviderHttpHelpers.FriendlyBody(body, response.ReasonPhrase, "LM Studio native model catalog request failed.", "message", "error", "detail"),
                    apiToken));
            }

            return LmStudioModelCatalog.Success(ParseModels(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return LmStudioModelCatalog.Failed(ProviderConfigurationControlService.SanitizeError(
                FriendlyException(ex),
                apiToken));
        }
    }

    public static IReadOnlyList<LmStudioModelInfo> ParseModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, "models", out var models)
            && !TryGetArray(doc.RootElement, "data", out models))
        {
            return [];
        }

        var entries = new List<LmStudioModelInfo>();
        foreach (var item in models.EnumerateArray())
        {
            var key = ProviderHttpHelpers.FirstString(item, "key", "id", "selected_variant", "model").Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var displayName = ProviderHttpHelpers.FirstString(item, "display_name", "name").Trim();
            var selectedVariant = ProviderHttpHelpers.FirstString(item, "selected_variant").Trim();
            var type = ProviderHttpHelpers.FirstString(item, "type").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(type))
            {
                type = InferModelType(key, displayName);
            }

            var quantization = item.TryGetProperty("quantization", out var quantizationElement)
                && quantizationElement.ValueKind == JsonValueKind.Object
                ? quantizationElement
                : default;
            var capabilities = item.TryGetProperty("capabilities", out var capabilitiesElement)
                && capabilitiesElement.ValueKind == JsonValueKind.Object
                ? capabilitiesElement
                : default;
            var reasoning = capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("reasoning", out var reasoningElement)
                && reasoningElement.ValueKind == JsonValueKind.Object
                ? reasoningElement
                : default;

            var loadedInstances = ParseLoadedInstances(item);
            var aliases = new[]
                {
                    key,
                    ProviderHttpHelpers.FirstString(item, "id"),
                    selectedVariant,
                    displayName
                }
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            entries.Add(new LmStudioModelInfo(
                Key: key,
                DisplayName: displayName,
                Type: type,
                Publisher: ProviderHttpHelpers.FirstString(item, "publisher"),
                Architecture: ProviderHttpHelpers.FirstString(item, "architecture"),
                QuantizationName: quantization.ValueKind == JsonValueKind.Object ? ProviderHttpHelpers.FirstString(quantization, "name") : "",
                BitsPerWeight: quantization.ValueKind == JsonValueKind.Object ? NullableDouble(quantization, "bits_per_weight") : null,
                SizeBytes: NullableInt64(item, "size_bytes"),
                ParamsString: ProviderHttpHelpers.FirstString(item, "params_string"),
                LoadedInstances: loadedInstances,
                MaxContextLength: NullableInt(item, "max_context_length"),
                Format: ProviderHttpHelpers.FirstString(item, "format"),
                Vision: capabilities.ValueKind == JsonValueKind.Object && Bool(capabilities, "vision"),
                TrainedForToolUse: capabilities.ValueKind == JsonValueKind.Object && Bool(capabilities, "trained_for_tool_use"),
                ReasoningOptions: reasoning.ValueKind == JsonValueKind.Object ? StringArray(reasoning, "allowed_options") : [],
                ReasoningDefault: reasoning.ValueKind == JsonValueKind.Object ? ProviderHttpHelpers.FirstString(reasoning, "default") : "",
                SelectedVariant: selectedVariant,
                Aliases: aliases,
                Description: ProviderHttpHelpers.FirstString(item, "description")));
        }

        return entries;
    }

    public static string NormalizeLmStudioApiBase(string providerBaseUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(providerBaseUrl)
            ? "http://127.0.0.1:1234/v1"
            : providerBaseUrl.Trim().TrimEnd('/');

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

    private static IReadOnlyList<LmStudioLoadedInstance> ParseLoadedInstances(JsonElement item)
    {
        if (!TryGetArray(item, "loaded_instances", out var loadedInstances))
        {
            return [];
        }

        var instances = new List<LmStudioLoadedInstance>();
        foreach (var instance in loadedInstances.EnumerateArray())
        {
            var config = instance.TryGetProperty("config", out var configElement)
                && configElement.ValueKind == JsonValueKind.Object
                ? configElement
                : default;
            instances.Add(new LmStudioLoadedInstance(
                Id: ProviderHttpHelpers.FirstString(instance, "id", "instance_id"),
                ContextLength: config.ValueKind == JsonValueKind.Object ? NullableInt(config, "context_length") : null,
                Parallel: config.ValueKind == JsonValueKind.Object ? NullableInt(config, "parallel") : null,
                FlashAttention: config.ValueKind == JsonValueKind.Object ? NullableBool(config, "flash_attention") : null,
                OffloadKvCacheToGpu: config.ValueKind == JsonValueKind.Object ? NullableBool(config, "offload_kv_cache_to_gpu") : null));
        }

        return instances;
    }

    private static string InferModelType(string key, string displayName)
    {
        var value = $"{key} {displayName}".ToLowerInvariant();
        return value.Contains("embed", StringComparison.OrdinalIgnoreCase)
            || value.Contains("rerank", StringComparison.OrdinalIgnoreCase)
            || value.Contains("whisper", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tts", StringComparison.OrdinalIgnoreCase)
            ? "embedding"
            : "llm";
    }

    private static bool TryGetArray(JsonElement root, string propertyName, out JsonElement array)
    {
        if (root.TryGetProperty(propertyName, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static IReadOnlyList<string> StringArray(JsonElement item, string propertyName)
    {
        if (!TryGetArray(item, propertyName, out var values))
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool Bool(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static bool? NullableBool(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static int? NullableInt(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static long? NullableInt64(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static double? NullableDouble(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static string FriendlyException(Exception ex)
    {
        if (ex is UriFormatException)
        {
            return $"Invalid LM Studio native API URL: {ex.Message}";
        }

        if (ex is TaskCanceledException)
        {
            return "Timed out while asking LM Studio for its native model catalog.";
        }

        return ex.Message;
    }
}

public sealed record LmStudioModelCatalog(bool Ok, IReadOnlyList<LmStudioModelInfo> Models, string Error)
{
    public static LmStudioModelCatalog Empty { get; } = new(false, [], "");

    public IReadOnlyList<LmStudioModelInfo> ChatModels =>
        Models.Where(model => model.IsChatModel).ToArray();

    public IReadOnlyList<LmStudioModelInfo> EmbeddingModels =>
        Models.Where(model => model.IsEmbeddingModel).ToArray();

    public int LoadedCount => Models.Count(model => model.Loaded);

    public static LmStudioModelCatalog Success(IReadOnlyList<LmStudioModelInfo> models)
    {
        return new LmStudioModelCatalog(true, models, "");
    }

    public static LmStudioModelCatalog Failed(string error)
    {
        return new LmStudioModelCatalog(false, [], error);
    }

    public LmStudioModelInfo? Find(string selectedModel)
    {
        return Models.FirstOrDefault(model => model.Matches(selectedModel));
    }
}

public sealed record LmStudioModelInfo(
    string Key,
    string DisplayName,
    string Type,
    string Publisher,
    string Architecture,
    string QuantizationName,
    double? BitsPerWeight,
    long? SizeBytes,
    string ParamsString,
    IReadOnlyList<LmStudioLoadedInstance> LoadedInstances,
    int? MaxContextLength,
    string Format,
    bool Vision,
    bool TrainedForToolUse,
    IReadOnlyList<string> ReasoningOptions,
    string ReasoningDefault,
    string SelectedVariant,
    IReadOnlyList<string> Aliases,
    string Description)
{
    public string PreferredIdentifier => string.IsNullOrWhiteSpace(Key)
        ? Aliases.FirstOrDefault() ?? ""
        : Key;

    public bool Loaded => LoadedInstances.Count > 0;

    public bool IsEmbeddingModel => Type.Equals("embedding", StringComparison.OrdinalIgnoreCase);

    public bool IsChatModel => !IsEmbeddingModel;

    public int? LoadedContextLength
    {
        get
        {
            var values = LoadedInstances
                .Select(instance => instance.ContextLength)
                .Where(value => value.HasValue)
                .Select(value => value.GetValueOrDefault())
                .ToArray();
            return values.Length == 0 ? null : values.Max();
        }
    }

    public double? SizeGb => SizeBytes is long bytes && bytes > 0
        ? bytes / Math.Pow(1024, 3)
        : null;

    public string DisplayTitle => string.IsNullOrWhiteSpace(DisplayName) ? PreferredIdentifier : DisplayName;

    public string CapabilitySummary
    {
        get
        {
            var parts = new List<string>();
            if (Loaded)
            {
                parts.Add("loaded");
            }

            if (TrainedForToolUse)
            {
                parts.Add("tools");
            }

            if (Vision)
            {
                parts.Add("vision");
            }

            if (!string.IsNullOrWhiteSpace(ReasoningDefault) || ReasoningOptions.Count > 0)
            {
                parts.Add($"reasoning {ReasoningDefaultOrOptions()}");
            }

            if (MaxContextLength is int maxContext && maxContext > 0)
            {
                parts.Add($"{FormatTokenCount(maxContext)} ctx");
            }

            if (!string.IsNullOrWhiteSpace(QuantizationName))
            {
                parts.Add(QuantizationName);
            }

            if (!string.IsNullOrWhiteSpace(Format))
            {
                parts.Add(Format);
            }

            return parts.Count == 0 ? "native metadata" : string.Join(" / ", parts);
        }
    }

    public bool Matches(string selectedModel)
    {
        return Aliases.Any(alias => string.Equals(alias, selectedModel, StringComparison.OrdinalIgnoreCase));
    }

    public string Tooltip()
    {
        var lines = new List<string>
        {
            DisplayTitle,
            PreferredIdentifier
        };
        if (!string.IsNullOrWhiteSpace(Type))
        {
            lines.Add($"Type: {Type}");
        }

        if (!string.IsNullOrWhiteSpace(Architecture))
        {
            lines.Add($"Architecture: {Architecture}");
        }

        if (SizeGb is double size)
        {
            lines.Add($"Size: {size:0.#} GB");
        }

        if (!string.IsNullOrWhiteSpace(ParamsString))
        {
            lines.Add($"Parameters: {ParamsString}");
        }

        if (!string.IsNullOrWhiteSpace(QuantizationName))
        {
            var bits = BitsPerWeight is double value ? $" ({value:0.#} bpw)" : "";
            lines.Add($"Quantization: {QuantizationName}{bits}");
        }

        if (MaxContextLength is int maxContext)
        {
            lines.Add($"Max context: {FormatTokenCount(maxContext)} tokens");
        }

        if (LoadedContextLength is int loadedContext)
        {
            lines.Add($"Loaded context: {FormatTokenCount(loadedContext)} tokens");
        }

        lines.Add($"Capabilities: {CapabilitySummary}");
        if (LoadedInstances.Count > 0)
        {
            lines.Add($"Loaded instance: {string.Join(", ", LoadedInstances.Select(instance => instance.Id).Where(id => !string.IsNullOrWhiteSpace(id)))}");
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private string ReasoningDefaultOrOptions()
    {
        if (!string.IsNullOrWhiteSpace(ReasoningDefault))
        {
            return ReasoningDefault;
        }

        return ReasoningOptions.Count == 0
            ? "available"
            : string.Join("/", ReasoningOptions);
    }

    private static string FormatTokenCount(int value)
    {
        return value >= 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{value / 1000d:0.#}k")
            : value.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record LmStudioLoadedInstance(
    string Id,
    int? ContextLength,
    int? Parallel,
    bool? FlashAttention,
    bool? OffloadKvCacheToGpu);
