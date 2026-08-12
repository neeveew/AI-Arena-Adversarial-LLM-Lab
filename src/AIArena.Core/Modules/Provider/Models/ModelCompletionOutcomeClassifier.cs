using System.Text.Json;

namespace AIArena.Core.Models;

/// <summary>Provider-neutral completion outcome normalization.</summary>
public static class ModelCompletionOutcomeClassifier
{
    private static readonly string[] ContextMarkers =
    [
        "context_length_exceeded", "context length exceeded", "maximum context length",
        "context window", "too many tokens", "token limit", "prompt is too long",
        "prompt too long", "input is too long", "input too long", "exceeds the context",
        "exceed context", "n_ctx", "context overflow"
    ];

    public static ModelCompletionResult Normalize(ModelCompletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var failureKind = result.Ok
            ? ModelCompletionFailureKind.None
            : result.FailureKind == ModelCompletionFailureKind.None
                ? ClassifyFailure(result.Error, result.ProviderStatusCode, result.ProviderErrorCode)
                : result.FailureKind;
        var stopReason = result.StopReason != ModelCompletionStopReason.Unknown
            ? result.StopReason
            : result.Ok
                ? ModelCompletionStopReason.Completed
                : ModelCompletionStopReason.ProviderError;
        return result with
        {
            FailureKind = failureKind,
            StopReason = stopReason,
            ProviderErrorCode = PrivacySafeProviderErrorCode(result.ProviderErrorCode)
        };
    }

    /// <summary>
    /// Provider codes are durable diagnostic metadata, not provider error bodies.
    /// Admit only a short code-shaped value so credentials, URLs, paths, and
    /// arbitrary echoed payloads never enter a snapshot through this field.
    /// </summary>
    public static string PrivacySafeProviderErrorCode(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length is > 0 and <= 96
            && !InternetRequestSafety.ContainsSensitivePayload(normalized)
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
                ? normalized
                : "";
    }

    public static ModelCompletionFailureKind ClassifyFailure(
        string? error,
        int? providerStatusCode = null,
        string? providerErrorCode = null)
    {
        var combined = $"{providerErrorCode} {error}".Trim().ToLowerInvariant();
        if (combined.Contains("previous_response", StringComparison.Ordinal)
            || combined.Contains("previous response", StringComparison.Ordinal)
            || combined.Contains("conversation state", StringComparison.Ordinal)
            || combined.Contains("state expired", StringComparison.Ordinal)
            || combined.Contains("state not found", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.NativeStateExhausted;
        }

        if (ContextMarkers.Any(marker => combined.Contains(marker, StringComparison.Ordinal)))
        {
            return ModelCompletionFailureKind.ContextLimitExceeded;
        }

        if (combined.Contains("cancel", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.Cancelled;
        }

        if (combined.Contains("loading model", StringComparison.Ordinal)
            || combined.Contains("model is loading", StringComparison.Ordinal)
            || combined.Contains("model loading", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.ProviderLoading;
        }

        if (combined.Contains("timeout", StringComparison.Ordinal)
            || combined.Contains("timed out", StringComparison.Ordinal)
            || combined.Contains("deadline exceeded", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.Timeout;
        }

        if (providerStatusCode is 429 or 502 or 503 or 504
            || combined.Contains("rate limit", StringComparison.Ordinal)
            || combined.Contains("overloaded", StringComparison.Ordinal)
            || combined.Contains("capacity", StringComparison.Ordinal)
            || combined.Contains("busy", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.Capacity;
        }

        if (combined.Contains("returned no public content", StringComparison.Ordinal)
            || combined.Contains("empty public response", StringComparison.Ordinal)
            || combined.Contains("empty model response", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.EmptyPublicContent;
        }

        if (combined.Contains("invalid response", StringComparison.Ordinal)
            || combined.Contains("invalid json", StringComparison.Ordinal)
            || combined.Contains("malformed", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.InvalidResponse;
        }

        if (combined.Contains("connection", StringComparison.Ordinal)
            || combined.Contains("network", StringComparison.Ordinal)
            || combined.Contains("transport", StringComparison.Ordinal)
            || combined.Contains("unreachable", StringComparison.Ordinal)
            || combined.Contains("refused", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.Transport;
        }

        if (providerStatusCode is >= 400 || combined.Contains("provider request failed", StringComparison.Ordinal))
        {
            return ModelCompletionFailureKind.ProviderRejected;
        }

        return string.IsNullOrWhiteSpace(combined)
            ? ModelCompletionFailureKind.Unknown
            : ModelCompletionFailureKind.ProviderRejected;
    }

    public static ModelCompletionStopReason ClassifyStopReason(string? providerValue) =>
        (providerValue ?? "").Trim().ToLowerInvariant() switch
        {
            "stop" or "stopped" or "end_turn" or "eos" or "eos_token" or "completed" or "complete" or "done"
                => ModelCompletionStopReason.Completed,
            "length" or "max_tokens" or "max_output_tokens" or "token_limit" or "limit" or "max_length"
                => ModelCompletionStopReason.OutputLimitReached,
            "content_filter" or "content_filtered" or "safety" or "blocked"
                => ModelCompletionStopReason.ContentFiltered,
            "tool_calls" or "tool_call" or "function_call"
                => ModelCompletionStopReason.ToolCall,
            "error" or "failed" or "cancelled" or "canceled"
                => ModelCompletionStopReason.ProviderError,
            _ => ModelCompletionStopReason.Unknown
        };

    public static ModelCompletionStopReason ExtractStopReason(JsonElement root)
    {
        foreach (var property in new[] { "finish_reason", "stop_reason", "done_reason", "status" })
        {
            var value = FindString(root, property, 0);
            var classified = ClassifyStopReason(value);
            if (classified != ModelCompletionStopReason.Unknown)
            {
                return classified;
            }
        }

        return ModelCompletionStopReason.Unknown;
    }

    public static string FailureKindWire(ModelCompletionFailureKind value) => value switch
    {
        ModelCompletionFailureKind.None => "none",
        ModelCompletionFailureKind.ContextLimitExceeded => "context_limit_exceeded",
        ModelCompletionFailureKind.Timeout => "timeout",
        ModelCompletionFailureKind.Capacity => "capacity",
        ModelCompletionFailureKind.Transport => "transport",
        ModelCompletionFailureKind.ProviderRejected => "provider_rejected",
        ModelCompletionFailureKind.InvalidResponse => "invalid_response",
        ModelCompletionFailureKind.EmptyPublicContent => "empty_public_content",
        ModelCompletionFailureKind.NativeStateExhausted => "native_state_exhausted",
        ModelCompletionFailureKind.ProviderLoading => "provider_loading",
        ModelCompletionFailureKind.Cancelled => "cancelled",
        _ => "unknown"
    };

    public static string StopReasonWire(ModelCompletionStopReason value) => value switch
    {
        ModelCompletionStopReason.Completed => "completed",
        ModelCompletionStopReason.OutputLimitReached => "output_limit_reached",
        ModelCompletionStopReason.ContentFiltered => "content_filtered",
        ModelCompletionStopReason.ToolCall => "tool_call",
        ModelCompletionStopReason.ProviderError => "provider_error",
        _ => "unknown"
    };

    private static string FindString(JsonElement element, string name, int depth)
    {
        if (depth > 5)
        {
            return "";
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString() ?? "";
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindString(property.Value, name, depth + 1);
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name, depth + 1);
                if (nested.Length > 0)
                {
                    return nested;
                }
            }
        }

        return "";
    }
}
