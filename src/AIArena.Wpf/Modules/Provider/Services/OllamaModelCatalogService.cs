using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public class OllamaModelCatalogService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private readonly HttpClient httpClient;

    public OllamaModelCatalogService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public virtual async Task<OllamaModelCatalog> TryLoadAsync(
        string providerBaseUrl,
        string apiToken = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var apiBase = ModelProviderClient.NormalizeOllamaApiBase(providerBaseUrl);
            var tags = await GetAsync(new Uri(new Uri(apiBase + "/", UriKind.Absolute), "tags"), apiToken, cancellationToken);
            if (!tags.Ok)
            {
                return OllamaModelCatalog.Failed(tags.Error);
            }

            var models = ParseTags(tags.Body);
            var ps = await GetAsync(new Uri(new Uri(apiBase + "/", UriKind.Absolute), "ps"), apiToken, cancellationToken);
            return ps.Ok
                ? OllamaModelCatalog.Success(MergeRunningModels(models, ParseRunningModels(ps.Body)), runningModelsOk: true, "")
                : OllamaModelCatalog.Success(models, runningModelsOk: false, ps.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UriFormatException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return OllamaModelCatalog.Failed(ProviderConfigurationControlService.SanitizeError(
                FriendlyException(ex),
                apiToken));
        }
    }

    private async Task<(bool Ok, string Body, string Error)> GetAsync(Uri endpoint, string apiToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthorization(request, apiToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? (true, body, "")
            : (false, "", ProviderConfigurationControlService.SanitizeError(
                FriendlyBody(body, response.ReasonPhrase),
                apiToken));
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string apiToken)
    {
        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiToken.Trim()}");
        }
    }

    public static IReadOnlyList<OllamaModelInfo> ParseTags(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, "models", out var models))
        {
            return [];
        }

        var entries = new List<OllamaModelInfo>();
        foreach (var item in models.EnumerateArray())
        {
            var model = FirstString(item, "model", "name").Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            entries.Add(ParseModelInfo(item, model));
        }

        return entries;
    }

    public static IReadOnlyList<OllamaModelInfo> ParseRunningModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, "models", out var models))
        {
            return [];
        }

        var entries = new List<OllamaModelInfo>();
        foreach (var item in models.EnumerateArray())
        {
            var model = FirstString(item, "model", "name").Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            entries.Add(ParseModelInfo(item, model));
        }

        return entries;
    }

    public static IReadOnlyList<OllamaModelInfo> MergeRunningModels(
        IReadOnlyList<OllamaModelInfo> localModels,
        IReadOnlyList<OllamaModelInfo> runningModels)
    {
        var merged = localModels.ToDictionary(model => model.PreferredIdentifier, StringComparer.OrdinalIgnoreCase);
        foreach (var running in runningModels)
        {
            if (merged.TryGetValue(running.PreferredIdentifier, out var local))
            {
                merged[running.PreferredIdentifier] = local with
                {
                    ContextLength = running.ContextLength ?? local.ContextLength,
                    ExpiresAt = running.ExpiresAt ?? local.ExpiresAt,
                    SizeVramBytes = running.SizeVramBytes ?? local.SizeVramBytes,
                    SizeBytes = running.SizeBytes ?? local.SizeBytes,
                    Digest = string.IsNullOrWhiteSpace(running.Digest) ? local.Digest : running.Digest,
                    Format = string.IsNullOrWhiteSpace(running.Format) ? local.Format : running.Format,
                    Family = string.IsNullOrWhiteSpace(running.Family) ? local.Family : running.Family,
                    ParameterSize = string.IsNullOrWhiteSpace(running.ParameterSize) ? local.ParameterSize : running.ParameterSize,
                    QuantizationLevel = string.IsNullOrWhiteSpace(running.QuantizationLevel) ? local.QuantizationLevel : running.QuantizationLevel,
                    Aliases = local.Aliases.Concat(running.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
                continue;
            }

            merged[running.PreferredIdentifier] = running;
        }

        return merged.Values
            .OrderByDescending(model => model.Loaded)
            .ThenBy(model => model.PreferredIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static OllamaModelInfo ParseModelInfo(JsonElement item, string model)
    {
        var details = item.TryGetProperty("details", out var detailsElement)
            && detailsElement.ValueKind == JsonValueKind.Object
            ? detailsElement
            : default;
        var aliases = new[]
            {
                model,
                FirstString(item, "name"),
                FirstString(item, "model")
            }
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OllamaModelInfo(
            Name: FirstString(item, "name"),
            Model: model,
            SizeBytes: NullableInt64(item, "size"),
            Digest: FirstString(item, "digest"),
            Format: details.ValueKind == JsonValueKind.Object ? FirstString(details, "format") : "",
            Family: details.ValueKind == JsonValueKind.Object ? FirstString(details, "family") : "",
            ParameterSize: details.ValueKind == JsonValueKind.Object ? FirstString(details, "parameter_size") : "",
            QuantizationLevel: details.ValueKind == JsonValueKind.Object ? FirstString(details, "quantization_level") : "",
            ContextLength: NullableInt(item, "context_length"),
            ExpiresAt: NullableDateTimeOffset(item, "expires_at"),
            SizeVramBytes: NullableInt64(item, "size_vram"),
            Aliases: aliases);
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

    private static DateTimeOffset? NullableDateTimeOffset(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : null;
    }

    private static string FriendlyBody(string body, string? reasonPhrase)
    {
        var message = LmStudioJsonMessageExtractor.ExtractMessage(body, "message", "error", "detail");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return !string.IsNullOrWhiteSpace(reasonPhrase)
            ? reasonPhrase
            : "Ollama native model catalog request failed.";
    }

    private static string FriendlyException(Exception ex)
    {
        if (ex is UriFormatException)
        {
            return $"Invalid Ollama native API URL: {ex.Message}";
        }

        if (ex is TaskCanceledException)
        {
            return "Timed out while asking Ollama for its native model catalog.";
        }

        return ex.Message;
    }
}

public sealed record OllamaModelCatalog(
    bool Ok,
    IReadOnlyList<OllamaModelInfo> Models,
    string Error,
    bool RunningModelsOk,
    string RunningModelsError)
{
    public static OllamaModelCatalog Empty { get; } = new(false, [], "", false, "");

    public int LoadedCount => Models.Count(model => model.Loaded);

    public static OllamaModelCatalog Success(
        IReadOnlyList<OllamaModelInfo> models,
        bool runningModelsOk,
        string runningModelsError)
    {
        return new OllamaModelCatalog(true, models, "", runningModelsOk, runningModelsError);
    }

    public static OllamaModelCatalog Failed(string error)
    {
        return new OllamaModelCatalog(false, [], error, false, error);
    }

    public OllamaModelInfo? Find(string selectedModel)
    {
        return Models.FirstOrDefault(model => model.Matches(selectedModel));
    }
}

public sealed record OllamaModelInfo(
    string Name,
    string Model,
    long? SizeBytes,
    string Digest,
    string Format,
    string Family,
    string ParameterSize,
    string QuantizationLevel,
    int? ContextLength,
    DateTimeOffset? ExpiresAt,
    long? SizeVramBytes,
    IReadOnlyList<string> Aliases)
{
    public string PreferredIdentifier => string.IsNullOrWhiteSpace(Model)
        ? Aliases.FirstOrDefault() ?? ""
        : Model;

    public bool Loaded => ContextLength.HasValue || SizeVramBytes.HasValue || ExpiresAt.HasValue;

    public double? SizeGb => SizeBytes is long bytes && bytes > 0
        ? bytes / Math.Pow(1024, 3)
        : null;

    public double? SizeVramGb => SizeVramBytes is long bytes && bytes > 0
        ? bytes / Math.Pow(1024, 3)
        : null;

    public string CapabilitySummary
    {
        get
        {
            var parts = new List<string>();
            if (Loaded)
            {
                parts.Add("loaded");
            }

            if (ContextLength is int contextLength && contextLength > 0)
            {
                parts.Add($"{FormatTokenCount(contextLength)} ctx");
            }

            if (SizeVramGb is double vram)
            {
                parts.Add($"{vram:0.#} GB VRAM");
            }

            if (ExpiresAt is DateTimeOffset expiresAt)
            {
                parts.Add($"expires {FormatExpiresIn(expiresAt)}");
            }

            if (!string.IsNullOrWhiteSpace(ParameterSize))
            {
                parts.Add(ParameterSize);
            }

            if (!string.IsNullOrWhiteSpace(QuantizationLevel))
            {
                parts.Add(QuantizationLevel);
            }

            if (!string.IsNullOrWhiteSpace(Family))
            {
                parts.Add(Family);
            }

            if (!string.IsNullOrWhiteSpace(Format))
            {
                parts.Add(Format);
            }

            return parts.Count == 0 ? "Ollama metadata" : string.Join(" / ", parts);
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
            PreferredIdentifier,
            "Provider: Ollama native"
        };
        if (SizeGb is double size)
        {
            lines.Add($"Size: {size:0.#} GB");
        }

        if (SizeVramGb is double vram)
        {
            lines.Add($"VRAM: {vram:0.#} GB");
        }

        if (ContextLength is int contextLength)
        {
            lines.Add($"Loaded context: {FormatTokenCount(contextLength)} tokens");
        }

        if (ExpiresAt is DateTimeOffset expiresAt)
        {
            lines.Add($"Expires: {FormatExpiresIn(expiresAt)}");
        }

        if (!string.IsNullOrWhiteSpace(ParameterSize))
        {
            lines.Add($"Parameters: {ParameterSize}");
        }

        if (!string.IsNullOrWhiteSpace(QuantizationLevel))
        {
            lines.Add($"Quantization: {QuantizationLevel}");
        }

        if (!string.IsNullOrWhiteSpace(Family))
        {
            lines.Add($"Family: {Family}");
        }

        if (!string.IsNullOrWhiteSpace(Format))
        {
            lines.Add($"Format: {Format}");
        }

        if (!string.IsNullOrWhiteSpace(Digest))
        {
            lines.Add($"Digest: {Digest}");
        }

        lines.Add($"Capabilities: {CapabilitySummary}");
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string FormatTokenCount(int value)
    {
        return value >= 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{value / 1000d:0.#}k")
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatExpiresIn(DateTimeOffset expiresAt)
    {
        var remaining = expiresAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"in {remaining.TotalHours:0.#}h";
        }

        return remaining.TotalMinutes >= 1
            ? $"in {remaining.TotalMinutes:0.#}m"
            : $"in {Math.Max(1, remaining.TotalSeconds):0}s";
    }
}
