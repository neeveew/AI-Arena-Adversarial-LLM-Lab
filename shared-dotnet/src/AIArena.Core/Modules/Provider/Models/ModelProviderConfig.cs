using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Providers;

namespace AIArena.Core.Models;

public sealed class ModelProviderConfig
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = ModelProviderDefaults.BaseUrl;

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("timeout")]
    public int Timeout { get; init; } = ModelProviderDefaults.TimeoutSeconds;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = ModelProviderDefaults.Temperature;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; } = ModelProviderDefaults.MaxOutputTokens;

    [JsonPropertyName("last_error")]
    public string LastError { get; init; } = "";

    [JsonPropertyName("last_latency_ms")]
    public int LastLatencyMs { get; init; }

    [JsonPropertyName("last_test_ok")]
    public bool LastTestOk { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
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
    DateTimeOffset CheckedAt);
