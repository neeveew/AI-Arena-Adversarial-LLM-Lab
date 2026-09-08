using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Providers;

/// <summary>One bounded disclosure policy for provider response errors in every host.</summary>
public static class ProviderErrorSanitizer
{
    public const int MaximumLength = 360;
    public const int MaximumInputLength = 64 * 1024;
    public const string SensitiveError = "Provider returned an error containing sensitive data; details were hidden.";
    private static readonly Regex CredentialField = new(
        """(?i)(?:\b(?:bearer|basic)\s+\S+|\b(?:api[\s_-]?key|access[\s_-]?token|refresh[\s_-]?token|token|password|secret|authorization)["']?\s*(?::|=|\s)\s*["']?[^"'\s,;}\]]+|\b(?:sk-(?:proj-)?|sk_|hf_|github_pat_|ghp_)[A-Za-z0-9_-]{8,}\b|https?://[^/@\s]+@)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Sanitize(string? message, string? apiToken = null)
    {
        var text = message ?? "";
        if (text.Length > MaximumInputLength)
        {
            return "Provider returned an oversized error; details were hidden.";
        }

        try
        {
            var decoded = Uri.UnescapeDataString(text);
            if ((!string.IsNullOrWhiteSpace(apiToken)
                    && (text.Contains(apiToken.Trim(), StringComparison.Ordinal)
                        || decoded.Contains(apiToken.Trim(), StringComparison.Ordinal)))
                || InternetRequestSafety.ContainsSensitivePayload(text)
                || CredentialField.IsMatch(decoded))
            {
                return SensitiveError;
            }
        }
        catch (Exception exception) when (exception is UriFormatException or RegexMatchTimeoutException)
        {
            return SensitiveError;
        }

        return Compact(text);
    }

    public static string Compact(string? message)
    {
        var text = message ?? "";
        if (text.Length > MaximumInputLength)
        {
            return "Provider returned an oversized error; details were hidden.";
        }

        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumLength ? normalized : normalized[..(MaximumLength - 3)] + "...";
    }

    public static string Endpoint(string? baseUrl, string? apiToken = null)
    {
        var text = (baseUrl ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length > MaximumInputLength
            || (!string.IsNullOrWhiteSpace(apiToken) && text.Contains(apiToken.Trim(), StringComparison.Ordinal)))
        {
            return "configured endpoint";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "configured endpoint";
        }

        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        if (Sanitize(builder.Path, apiToken) == SensitiveError) builder.Path = "/";
        return Compact(builder.Uri.AbsoluteUri.TrimEnd('/'));
    }
}
