using System.Diagnostics;
using System.Net.Http.Json;
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

public class ModelProviderClient : IModelProviderClient
{
    private readonly HttpClient _httpClient;

    public ModelProviderClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var endpoint = new Uri(new Uri(baseUrl + "/"), "models");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            response.EnsureSuccessStatusCode();
            return new ModelProviderModels(true, baseUrl, ParseModelNames(body), "", DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ModelProviderModels(false, baseUrl, Array.Empty<string>(), FriendlyProviderError(ex, baseUrl, config.Timeout), DateTimeOffset.Now);
        }
    }

    public async Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        var endpoint = new Uri(new Uri(baseUrl + "/"), "chat/completions");
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

        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            using var timeout = TimeoutToken(config, cancellationToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            response.EnsureSuccessStatusCode();
            watch.Stop();
            var usage = ExtractUsage(body);
            return new ModelCompletionResult(
                true,
                baseUrl,
                model,
                ExtractAssistantContent(body).Trim(),
                ExtractReasoning(body).Trim(),
                (int)watch.ElapsedMilliseconds,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                "",
                DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            watch.Stop();
            return new ModelCompletionResult(false, baseUrl, model, "", "", (int)watch.ElapsedMilliseconds, 0, 0, 0, FriendlyProviderError(ex, baseUrl, config.Timeout), DateTimeOffset.Now);
        }
    }

    public static string NormalizeBaseUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:1234/v1" : value.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/v1";
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
            return Array.Empty<string>();
        }

        var models = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                models.Add(id.GetString() ?? "");
            }
        }

        return models.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
    }

    public static string ExtractAssistantContent(string json)
    {
        var message = FirstAssistantMessage(json);
        return message.HasValue && message.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";
    }

    public static string ExtractReasoning(string json)
    {
        var message = FirstAssistantMessage(json);
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
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
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

    private static JsonElement? FirstAssistantMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("message", out var message))
        {
            return null;
        }

        return message.Clone();
    }

    private static int GetTokenCount(JsonElement usage, string name)
    {
        return usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count
            : 0;
    }

    private static CancellationTokenSource TimeoutToken(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Timeout, 1, 300)));
        return timeout;
    }

    private static string FriendlyProviderError(Exception ex, string baseUrl, int timeoutSeconds)
    {
        if (ex is TaskCanceledException)
        {
            return $"Provider timed out after {Math.Clamp(timeoutSeconds, 1, 300)}s at {baseUrl}. Check that the model is loaded and responding.";
        }

        if (ex is JsonException)
        {
            return $"Provider returned an unreadable response at {baseUrl}. Check that the server is OpenAI-compatible.";
        }

        var message = ex.Message;
        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
        {
            return $"Provider unreachable at {baseUrl}. Start LM Studio server or check the base URL.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? $"Provider request failed at {baseUrl}."
            : $"Provider request failed at {baseUrl}: {message}";
    }
}

public sealed record ModelTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
