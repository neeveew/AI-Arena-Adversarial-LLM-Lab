using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIArena.Core.Models;

public static class InternetToolNames
{
    public const string WebSearch = "web_search";
    public const string FetchUrl = "fetch_url";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WebSearch,
        FetchUrl
    };
}

public sealed class InternetToolRequest
{
    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "";

    [JsonPropertyName("requester_id")]
    public string RequesterId { get; init; } = "";

    [JsonPropertyName("query")]
    public string Query { get; init; } = "";

    [JsonPropertyName("input")]
    public string Input { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("max_results")]
    public int MaxResults { get; init; } = 5;

    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("time_range")]
    public string TimeRange { get; init; } = "";

    [JsonPropertyName("categories")]
    public string Categories { get; init; } = "general";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("options")]
    public Dictionary<string, JsonElement> Options { get; init; } = new();
}

public sealed class InternetToolSource
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("snippet")]
    public string Snippet { get; init; } = "";

    [JsonPropertyName("score")]
    public double Score { get; init; }
}

public sealed record SearxngSearchParameters(
    string Language = "auto",
    string TimeRange = "",
    string Categories = "general");

public sealed class InternetToolResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "";

    [JsonPropertyName("query")]
    public string Query { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonPropertyName("sources")]
    public IReadOnlyList<InternetToolSource> Sources { get; init; } = [];

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("checked_at")]
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("cached")]
    public bool Cached { get; init; }

    [JsonPropertyName("quality")]
    public string Quality { get; init; } = "";
}

public static partial class InternetToolContract
{
    private const int DefaultMaxResults = 5;
    private const int HardMaxResults = 10;

    public static bool TryParseRequest(string text, out InternetToolRequest request, out string error)
    {
        request = new InternetToolRequest();
        error = "";

        var json = ExtractJsonObject(text ?? "");
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "No JSON tool request found.";
            return false;
        }

        InternetToolRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<InternetToolRequest>(json);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON tool request: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "Tool request was empty.";
            return false;
        }

        return TryValidate(parsed, out request, out error);
    }

    public static bool TryValidate(InternetToolRequest candidate, out InternetToolRequest request, out string error)
    {
        request = new InternetToolRequest();
        error = "";

        var tool = candidate.Tool?.Trim() ?? "";
        if (!InternetToolNames.All.Contains(tool))
        {
            error = $"Unsupported internet tool '{candidate.Tool}'.";
            return false;
        }

        var query = string.IsNullOrWhiteSpace(candidate.Query)
            ? candidate.Input?.Trim() ?? ""
            : candidate.Query.Trim();
        var url = candidate.Url?.Trim() ?? "";
        if (tool.Equals(InternetToolNames.WebSearch, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(query))
        {
            error = $"{tool} requires a query.";
            return false;
        }

        if (tool.Equals(InternetToolNames.FetchUrl, StringComparison.OrdinalIgnoreCase)
            && (url.Length > InternetRequestSafety.MaximumOutboundUrlLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
                || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(parsedUrl.UserInfo)))
        {
            error = $"fetch_url requires a credential-free absolute HTTP or HTTPS URL no longer than {InternetRequestSafety.MaximumOutboundUrlLength} characters.";
            return false;
        }

        if (!TryNormalizeLanguage(candidate.Language, out var language))
        {
            error = "web_search language must be 'auto', 'all', or a language code such as 'en' or 'en-GB'.";
            return false;
        }

        if (!TryNormalizeTimeRange(candidate.TimeRange, out var timeRange))
        {
            error = "web_search time_range must be blank, day, month, or year.";
            return false;
        }

        if (!TryNormalizeCategories(candidate.Categories, out var categories))
        {
            error = "web_search categories must contain one to three simple category names.";
            return false;
        }

        request = new InternetToolRequest
        {
            Tool = tool.ToLowerInvariant(),
            RequesterId = candidate.RequesterId?.Trim() ?? "",
            Query = query,
            Url = url,
            MaxResults = Math.Clamp(candidate.MaxResults <= 0 ? DefaultMaxResults : candidate.MaxResults, 1, HardMaxResults),
            Language = language,
            TimeRange = timeRange,
            Categories = categories,
            Reason = candidate.Reason?.Trim() ?? "",
            Options = candidate.Options ?? new Dictionary<string, JsonElement>()
        };
        return true;
    }

    private static bool TryNormalizeLanguage(string? value, out string language)
    {
        language = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();
        if (language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || language.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            language = language.ToLowerInvariant();
            return true;
        }

        if (!LanguageRegex().IsMatch(language))
        {
            return false;
        }

        var parts = language.Split('-', 2);
        language = parts.Length == 1
            ? parts[0].ToLowerInvariant()
            : $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}";
        return true;
    }

    private static bool TryNormalizeTimeRange(string? value, out string timeRange)
    {
        timeRange = string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        return timeRange is "" or "day" or "month" or "year";
    }

    private static bool TryNormalizeCategories(string? value, out string categories)
    {
        var parts = (string.IsNullOrWhiteSpace(value) ? "general" : value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parts.Length is < 1 or > 3 || parts.Any(part => !CategoryRegex().IsMatch(part)))
        {
            categories = "";
            return false;
        }

        categories = string.Join(',', parts);
        return true;
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var fenced = JsonFenceRegex().Match(text);
        if (fenced.Success)
        {
            return fenced.Groups["json"].Value.Trim();
        }

        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first
            ? text[first..(last + 1)].Trim()
            : "";
    }

    [GeneratedRegex("```(?:json)?\\s*(?<json>\\{.*?\\})\\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex("^[a-z]{2,3}(?:-[a-z]{2})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9 _-]{0,31}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryRegex();
}
