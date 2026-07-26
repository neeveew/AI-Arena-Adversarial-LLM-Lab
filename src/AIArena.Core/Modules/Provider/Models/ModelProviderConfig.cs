using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Providers;

namespace AIArena.Core.Models;

public sealed class ModelProviderConfig
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = ModelProviderDefaults.BaseUrl;

    [JsonPropertyName("api_mode")]
    public string ApiMode { get; init; } = ModelProviderApiModes.OpenAiCompatible;

    [JsonPropertyName("api_token")]
    public string ApiToken { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("timeout")]
    public int Timeout { get; init; } = ModelProviderDefaults.TimeoutSeconds;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = ModelProviderDefaults.Temperature;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; } = ModelProviderDefaults.MaxOutputTokens;

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; init; } = "";

    [JsonPropertyName("native_stateful_chat")]
    public bool NativeStatefulChat { get; init; } = true;

    [JsonPropertyName("native_idle_ttl_seconds")]
    public int NativeIdleTtlSeconds { get; init; }

    [JsonIgnore]
    public string PreviousResponseId { get; init; } = "";

    [JsonPropertyName("last_error")]
    public string LastError { get; init; } = "";

    [JsonPropertyName("last_latency_ms")]
    public int LastLatencyMs { get; init; }

    [JsonPropertyName("last_test_ok")]
    public bool LastTestOk { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public static class ModelProviderApiModes
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string LmStudioNative = "lmstudio_native";
    public const string OllamaNative = "ollama_native";

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OpenAiCompatible;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "lm_studio_native" or "lmstudio" or "lmstudio_native" or "native" => LmStudioNative,
            "ollama" or "ollama_native" or "ollama-native" or "ollama_api" or "ollama-api" => OllamaNative,
            "openai" or "openai_compatible" or "openai-compatible" or "compat" => OpenAiCompatible,
            _ => OpenAiCompatible
        };
    }

    public static bool IsNative(string value)
    {
        var normalized = Normalize(value);
        return normalized.Equals(LmStudioNative, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(OllamaNative, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLmStudioNative(string value)
    {
        return Normalize(value).Equals(LmStudioNative, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOllamaNative(string value)
    {
        return Normalize(value).Equals(OllamaNative, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ModelProviderReasoningModes
{
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? "";
        return normalized switch
        {
            "off" or "low" or "medium" or "high" or "on" => normalized,
            _ => ""
        };
    }
}

public sealed class ModelMetadata
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("latency_ms")]
    public int LatencyMs { get; init; }

    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("tokens_per_second")]
    public double TokensPerSecond { get; init; }

    [JsonPropertyName("time_to_first_token_ms")]
    public int TimeToFirstTokenMs { get; init; }

    [JsonPropertyName("model_load_time_ms")]
    public int ModelLoadTimeMs { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record ModelProviderHealth(
    bool Ok,
    string Label,
    string BaseUrl,
    int ModelCount,
    string Error,
    DateTimeOffset CheckedAt);

public sealed record ModelProviderModels(
    bool Ok,
    string BaseUrl,
    IReadOnlyList<string> Models,
    string Error,
    DateTimeOffset CheckedAt);

public sealed record ModelProviderTestResult(
    bool Ok,
    string BaseUrl,
    string Model,
    string Text,
    int LatencyMs,
    string Error,
    DateTimeOffset CheckedAt);

public sealed record ModelChatMessage(string Role, string Content);

public sealed record ModelCompletionResult(
    bool Ok,
    string BaseUrl,
    string Model,
    string Text,
    string Reasoning,
    int LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string Error,
    DateTimeOffset CheckedAt,
    double TokensPerSecond = 0,
    int TimeToFirstTokenMs = 0,
    string ResponseId = "",
    int ModelLoadTimeMs = 0);
