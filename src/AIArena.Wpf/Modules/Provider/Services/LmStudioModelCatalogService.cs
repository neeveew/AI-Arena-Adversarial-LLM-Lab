using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AIArena.Wpf.Services;

public class LmStudioModelCatalogService
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumJsonDepth = 32;
    internal const int MaximumModelEntries = 1024;

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
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
            {
                requestCancellation.CancelAfter(httpClient.Timeout);
            }

            var requestToken = requestCancellation.Token;
            var apiBase = NormalizeLmStudioApiBase(providerBaseUrl);
            var endpoint = new Uri(new Uri(apiBase + "/", UriKind.Absolute), "models");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);
            var body = await ReadBoundedContentAsync(response.Content, requestToken);
            if (!response.IsSuccessStatusCode)
            {
                return LmStudioModelCatalog.Failed(ProviderConfigurationControlService.SanitizeError(
                    ProviderHttpHelpers.FriendlyBody(
                        Encoding.UTF8.GetString(body.Memory.Span),
                        response.ReasonPhrase,
                        "LM Studio native model catalog request failed.",
                        "message",
                        "error",
                        "detail"),
                    apiToken));
            }

            return ParseCatalog(body.Memory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var presentation = AppErrorPresenter.Present(
                new TimeoutException(),
                AppErrorContext.Provider);
            return LmStudioModelCatalog.Failed(
                $"Timed out while asking LM Studio for its native model catalog. {presentation.DisplayText}");
        }
        catch (Exception ex) when (ex is UriFormatException
                                   or HttpRequestException
                                   or JsonException
                                   or InvalidDataException)
        {
            return LmStudioModelCatalog.Failed(ProviderConfigurationControlService.SanitizeError(
                FriendlyException(ex),
                apiToken));
        }
    }

    public static IReadOnlyList<LmStudioModelInfo> ParseModels(string json)
    {
        var catalog = ParseCatalog(Encoding.UTF8.GetBytes(json ?? ""));
        if (catalog.OmittedModelCount > 0)
        {
            throw new JsonException(
                $"LM Studio model catalog exceeded the {MaximumModelEntries} entry parser limit.");
        }

        return catalog.Models;
    }

    internal static LmStudioModelCatalog ParseCatalog(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("LM Studio model catalog root must be an object.");
        }

        if (!TryGetArray(doc.RootElement, "models", out var models)
            && !TryGetArray(doc.RootElement, "data", out models))
        {
            throw new JsonException("LM Studio model catalog did not contain a models array.");
        }

        var entries = new List<LmStudioModelInfo>();
        var sourceEntryCount = models.GetArrayLength();
        var retainedEntryCount = Math.Min(sourceEntryCount, MaximumModelEntries);
        var observedEntryCount = 0;
        foreach (var item in models.EnumerateArray())
        {
            if (observedEntryCount++ >= retainedEntryCount)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("LM Studio model catalog contained a malformed model entry.");
            }

            var key = ProviderHttpHelpers.FirstString(item, "key", "id", "selected_variant", "model").Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new JsonException("LM Studio model catalog contained a model without an identifier.");
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

            var hasResidencyEvidence = item.TryGetProperty("loaded_instances", out var loadedInstancesElement)
                && loadedInstancesElement.ValueKind == JsonValueKind.Array
                && loadedInstancesElement.EnumerateArray().All(instance => instance.ValueKind == JsonValueKind.Object);
            var loadedInstances = hasResidencyEvidence
                ? ParseLoadedInstances(loadedInstancesElement)
                : [];
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
                Description: ProviderHttpHelpers.FirstString(item, "description"),
                HasResidencyEvidence: hasResidencyEvidence));
        }

        return LmStudioModelCatalog.Success(
            entries,
            Math.Max(0, sourceEntryCount - retainedEntryCount));
    }

    private static async Task<BoundedContent> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declaredLength
            && declaredLength > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"LM Studio native model catalog exceeded the {MaximumResponseBytes / 1024 / 1024} MiB response limit.");
        }

        var initialCapacity = content.Headers.ContentLength is long contentLength
            ? (int)Math.Clamp(contentLength, 0, MaximumResponseBytes)
            : 16 * 1024;
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(initialCapacity);
        var buffer = new byte[64 * 1024];
        var totalRead = 0;
        while (true)
        {
            var remaining = MaximumResponseBytes + 1 - totalRead;
            if (remaining <= 0)
            {
                throw new InvalidDataException(
                    $"LM Studio native model catalog exceeded the {MaximumResponseBytes / 1024 / 1024} MiB response limit.");
            }

            var read = await source.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
        }

        if (totalRead > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"LM Studio native model catalog exceeded the {MaximumResponseBytes / 1024 / 1024} MiB response limit.");
        }

        return new BoundedContent(destination.GetBuffer(), totalRead);
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

    private static IReadOnlyList<LmStudioLoadedInstance> ParseLoadedInstances(JsonElement loadedInstances)
    {
        var instances = new List<LmStudioLoadedInstance>();
        foreach (var instance in loadedInstances.EnumerateArray())
        {
            if (instance.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

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
        var presentation = AppErrorPresenter.Present(ex, AppErrorContext.Provider);
        return ex switch
        {
            UriFormatException => $"Invalid LM Studio native API URL. {presentation.DisplayText}",
            InvalidDataException => $"LM Studio native model catalog exceeded the response limit. {presentation.DisplayText}",
            _ => presentation.DisplayText
        };
    }

    private readonly record struct BoundedContent(byte[] Buffer, int Length)
    {
        public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Length);
    }
}

public sealed record LmStudioModelCatalog(
    bool Ok,
    IReadOnlyList<LmStudioModelInfo> Models,
    string Error,
    int OmittedModelCount = 0)
{
    public static LmStudioModelCatalog Empty { get; } = new(false, [], "");

    public IReadOnlyList<LmStudioModelInfo> ChatModels =>
        Models.Where(model => model.IsChatModel).ToArray();

    public IReadOnlyList<LmStudioModelInfo> EmbeddingModels =>
        Models.Where(model => model.IsEmbeddingModel).ToArray();

    public int LoadedCount => Models.Count(model => model.Loaded);

    public static LmStudioModelCatalog Success(
        IReadOnlyList<LmStudioModelInfo> models,
        int omittedModelCount = 0)
    {
        return new LmStudioModelCatalog(
            true,
            models,
            "",
            Math.Max(0, omittedModelCount));
    }

    public static LmStudioModelCatalog Failed(string error)
    {
        return new LmStudioModelCatalog(false, [], error);
    }

    public LmStudioModelInfo? Find(string selectedModel)
    {
        return EquivalentModels(selectedModel).FirstOrDefault();
    }

    public LmStudioModelInfo? FindForLoad(string selectedModel)
    {
        var equivalents = EquivalentModels(selectedModel);
        return equivalents.FirstOrDefault(model => model.Loaded)
            ?? equivalents.FirstOrDefault(model => model.HasResidencyEvidence && !model.Loaded)
            ?? equivalents.FirstOrDefault();
    }

    public LmStudioModelInfo? FindForUnload(string selectedModel)
    {
        var equivalents = EquivalentModels(selectedModel);
        return equivalents.FirstOrDefault(model => model.Loaded
                && model.LoadedInstances.Any(instance => !string.IsNullOrWhiteSpace(instance.Id)))
            ?? equivalents.FirstOrDefault(model => model.Loaded)
            ?? equivalents.FirstOrDefault();
    }

    private IReadOnlyList<LmStudioModelInfo> EquivalentModels(string selectedModel)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedAlias = ProviderModelCatalogProjectionService.SafeModelIdentifier(selectedModel);
        if (selectedAlias.Length > 0)
        {
            aliases.Add(selectedAlias);
        }

        var equivalent = new List<LmStudioModelInfo>();
        var observed = new HashSet<LmStudioModelInfo>();
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var model in Models)
            {
                if (observed.Contains(model)
                    || !model.Aliases
                        .Select(ProviderModelCatalogProjectionService.SafeModelIdentifier)
                        .Any(aliases.Contains))
                {
                    continue;
                }

                observed.Add(model);
                equivalent.Add(model);
                foreach (var alias in model.Aliases)
                {
                    var safeAlias = ProviderModelCatalogProjectionService.SafeModelIdentifier(alias);
                    if (safeAlias.Length > 0)
                    {
                        aliases.Add(safeAlias);
                    }
                }

                expanded = true;
            }
        }

        return equivalent;
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
    string Description,
    bool HasResidencyEvidence)
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
            lines.Add($"Loaded instances: {LoadedInstances.Count}");
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
