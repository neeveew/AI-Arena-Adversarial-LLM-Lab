using System.Text.Json;

namespace AIArena.Core.Models;

/// <summary>Provider-neutral completion outcome normalization.</summary>
public static class ModelCompletionOutcomeClassifier
{
    public const string EmptyPublicContentError = "Provider returned a successful response without assistant content.";
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
        var missingPublicContent = result.Ok && string.IsNullOrWhiteSpace(result.Text);
        var ok = result.Ok && !missingPublicContent;
        var failureKind = missingPublicContent
            ? ModelCompletionFailureKind.EmptyPublicContent
            : ok
                ? ModelCompletionFailureKind.None
                : result.FailureKind == ModelCompletionFailureKind.None
                    ? ClassifyFailure(result.Error, result.ProviderStatusCode, result.ProviderErrorCode)
                    : result.FailureKind;
        var stopReason = result.StopReason != ModelCompletionStopReason.Unknown
            ? result.StopReason
            : ok
                ? ModelCompletionStopReason.Completed
                : failureKind == ModelCompletionFailureKind.EmptyPublicContent
                    ? ModelCompletionStopReason.Unknown
                    : ModelCompletionStopReason.ProviderError;
        return result with
        {
            Ok = ok,
            Error = missingPublicContent ? EmptyPublicContentError : result.Error,
            FailureKind = failureKind,
            StopReason = stopReason,
            ProviderErrorCode = PrivacySafeProviderErrorCode(result.ProviderErrorCode),
            ProviderStopReason = PrivacySafeProviderErrorCode(result.ProviderStopReason)
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

        if ((providerStatusCode is null or >= 200 and < 300)
            && (combined.Contains("returned no public content", StringComparison.Ordinal)
                || combined.Contains("empty public response", StringComparison.Ordinal)
                || combined.Contains("empty model response", StringComparison.Ordinal)
                || combined.Contains("successful response without assistant content", StringComparison.Ordinal)))
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
        var reported = ClassifyStopReason(ExtractProviderStopReason(root));
        // A structured tool response is not a reasoning-only answer. Keep its
        // raw terminal code separately, and never interpret tool arguments.
        return reported is not (ModelCompletionStopReason.ProviderError or ModelCompletionStopReason.ContentFiltered)
            && HasToolOutput(root)
                ? ModelCompletionStopReason.ToolCall
                : reported;
    }

    /// <summary>Preserves a short terminal code verbatim; arbitrary provider payloads are not metadata.</summary>
    public static string ExtractProviderStopReason(JsonElement root, string? apiToken = null)
    {
        string firstUnknown = "";
        foreach (var property in new[] { "finish_reason", "stop_reason", "done_reason", "status" })
        {
            var value = PrivacySafeProviderErrorCode(FindTerminalString(root, property, 0));
            if (value.Length == 0
                || (!string.IsNullOrEmpty(apiToken) && value.Contains(apiToken, StringComparison.Ordinal))) continue;
            if (ClassifyStopReason(value) != ModelCompletionStopReason.Unknown) return value;
            if (firstUnknown.Length == 0) firstUnknown = value;
        }
        return firstUnknown;
    }

    private static bool HasToolOutput(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array
            && output.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is "tool_call" or "invalid_tool_call" or "function_call"))
        {
            return true;
        }
        if (HasMessageToolCall(root)) return true;
        if (root.TryGetProperty("message", out var message) && HasMessageToolCall(message)) return true;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in new[] { "message", "delta" })
                {
                    if (choice.TryGetProperty(field, out var part) && HasMessageToolCall(part)) return true;
                }
            }
        }
        return false;
    }

    private static bool HasMessageToolCall(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && ((message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0)
            || (message.TryGetProperty("function_call", out var function) && function.ValueKind == JsonValueKind.Object));

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

    private static string FindTerminalString(JsonElement element, string name, int depth)
    {
        if (depth > 3 || element.ValueKind != JsonValueKind.Object) return "";
        if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString() ?? "";
        }

        // Only documented response envelopes may carry terminal metadata.
        // Do not descend into generated output, tool arguments, or usage.
        if (element.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty(name, out var choiceValue) && choiceValue.ValueKind == JsonValueKind.String)
            {
                return choiceValue.GetString() ?? "";
            }
        }
        foreach (var envelope in new[] { "result", "response" })
        {
            if (!element.TryGetProperty(envelope, out var nested)) continue;
            var value = FindTerminalString(nested, name, depth + 1);
            if (value.Length > 0) return value;
        }
        return "";
    }
}
