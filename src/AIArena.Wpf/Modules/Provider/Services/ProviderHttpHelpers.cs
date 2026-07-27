using System.Net.Http;
using System.Text.Json;

namespace AIArena.Wpf.Services;

/// <summary>
/// Request and response plumbing shared by the LM Studio and Ollama services.
///
/// Each of those services previously carried private copies of these helpers,
/// which drifted apart: some guarded the JSON value kind and trimmed, some did
/// not. The shared versions take the safer behaviour so a malformed payload
/// cannot throw where it previously returned a blank string.
/// </summary>
internal static class ProviderHttpHelpers
{
    /// <summary>Adds a bearer token when one is configured.</summary>
    public static void ApplyAuthorization(HttpRequestMessage request, string? apiToken)
    {
        var token = apiToken?.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }

    /// <summary>
    /// First string property present on the element, probed in the given order.
    /// Returns a blank string when the element is not an object or no probed
    /// property holds a string.
    /// </summary>
    public static string FirstString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? "";
            }
        }

        return "";
    }

    /// <summary>
    /// Turns an error response into something worth showing an operator: the
    /// message the provider supplied, else the HTTP reason phrase, else a
    /// caller-supplied fallback describing what was being attempted.
    /// </summary>
    public static string FriendlyBody(string body, string? reason, string fallback, params string[] propertyNames)
    {
        var message = LmStudioJsonMessageExtractor.ExtractMessage(body, propertyNames);
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
    }
}
