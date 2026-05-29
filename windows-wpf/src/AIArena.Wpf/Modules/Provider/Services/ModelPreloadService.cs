using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AIArena.Wpf.Services;

public sealed class ModelPreloadService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public async Task<IReadOnlyList<ModelPreloadResult>> PreloadAsync(
        string providerBaseUrl,
        IEnumerable<string> selectedModels,
        CancellationToken cancellationToken = default)
    {
        var models = selectedModels
            .Select(model => model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length == 0)
        {
            return [new ModelPreloadResult("", "skipped", "No selected models to preload.", false)];
        }

        var apiBase = NormalizeLmStudioApiBase(providerBaseUrl);
        var catalog = await TryLoadCatalogAsync(apiBase, cancellationToken);
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
            var loadModel = entry?.Key ?? model;
            if (entry?.Loaded == true)
            {
                results.Add(new ModelPreloadResult(model, "ready", "Already loaded in LM Studio.", false));
                continue;
            }

            results.Add(await LoadModelAsync(apiBase, model, loadModel, cancellationToken));
        }

        return results;
    }

    private static async Task<ModelPreloadResult> LoadModelAsync(
        string apiBase,
        string selectedModel,
        string loadModel,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(apiBase + "/"), "models/load");
        var startedAt = DateTimeOffset.Now;
        try
        {
            using var response = await HttpClient.PostAsJsonAsync(
                endpoint,
                new { model = loadModel, echo_load_config = true },
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelPreloadResult(selectedModel, "failed", FriendlyBody(body, response.ReasonPhrase), true);
            }

            var seconds = ExtractLoadSeconds(body);
            var elapsed = seconds > 0
                ? $"{seconds:0.#}s"
                : $"{(DateTimeOffset.Now - startedAt).TotalSeconds:0.#}s";
            return new ModelPreloadResult(selectedModel, "loaded", $"Loaded in {elapsed}.", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ModelPreloadResult(selectedModel, "failed", FriendlyException(ex), true);
        }
    }

    private static async Task<ModelCatalog> TryLoadCatalogAsync(string apiBase, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(apiBase + "/"), "models");
        try
        {
            using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ModelCatalog.Failed(FriendlyBody(body, response.ReasonPhrase));
            }

            return ModelCatalog.Success(ParseCatalog(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ModelCatalog.Failed(FriendlyException(ex));
        }
    }

    private static IReadOnlyList<ModelCatalogEntry> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetArray(doc.RootElement, "models", out var models)
            && !TryGetArray(doc.RootElement, "data", out models))
        {
            return [];
        }

        var entries = new List<ModelCatalogEntry>();
        foreach (var item in models.EnumerateArray())
        {
            var key = FirstString(item, "key", "id", "selected_variant");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var aliases = new[]
                {
                    key,
                    FirstString(item, "id"),
                    FirstString(item, "selected_variant"),
                    FirstString(item, "display_name")
                }
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var loaded = IsLoaded(item);
            entries.Add(new ModelCatalogEntry(key, aliases, loaded));
        }

        return entries;
    }

    private static bool IsLoaded(JsonElement item)
    {
        if (item.TryGetProperty("loaded_instances", out var loadedInstances)
            && loadedInstances.ValueKind == JsonValueKind.Array
            && loadedInstances.GetArrayLength() > 0)
        {
            return true;
        }

        if (item.TryGetProperty("loaded", out var loaded) && loaded.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return loaded.GetBoolean();
        }

        var status = FirstString(item, "status", "state");
        return status.Contains("loaded", StringComparison.OrdinalIgnoreCase)
            || status.Contains("running", StringComparison.OrdinalIgnoreCase)
            || status.Contains("ready", StringComparison.OrdinalIgnoreCase);
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

    private static string FriendlyBody(string body, string? reasonPhrase)
    {
        var message = ExtractJsonMessage(body);
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return !string.IsNullOrWhiteSpace(reasonPhrase)
            ? reasonPhrase
            : "LM Studio model preload request failed.";
    }

    private static string ExtractJsonMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return FirstString(doc.RootElement, "message", "error", "detail");
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static string FriendlyException(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return "Timed out while asking LM Studio to preload the model.";
        }

        return ex.Message;
    }

    private static string NormalizeLmStudioApiBase(string providerBaseUrl)
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

    private sealed record ModelCatalog(bool Ok, IReadOnlyList<ModelCatalogEntry> Entries, string Error)
    {
        public static ModelCatalog Success(IReadOnlyList<ModelCatalogEntry> entries)
        {
            return new ModelCatalog(true, entries, "");
        }

        public static ModelCatalog Failed(string error)
        {
            return new ModelCatalog(false, [], error);
        }

        public ModelCatalogEntry? Find(string selectedModel)
        {
            return Entries.FirstOrDefault(entry => entry.Matches(selectedModel));
        }
    }

    private sealed record ModelCatalogEntry(string Key, IReadOnlyList<string> Aliases, bool Loaded)
    {
        public bool Matches(string selectedModel)
        {
            return Aliases.Any(alias => string.Equals(alias, selectedModel, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public sealed record ModelPreloadResult(string Model, string Status, string Detail, bool IsFailure);
