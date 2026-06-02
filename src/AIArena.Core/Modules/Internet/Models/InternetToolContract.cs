using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIArena.Core.Models;

public static class InternetToolNames
{
    public const string WebSearch = "web_search";
    public const string RssSearch = "rss_search";
    public const string FetchUrl = "fetch_url";
    public const string SummarizeSources = "summarize_sources";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WebSearch,
        RssSearch,
        FetchUrl,
        SummarizeSources
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
    public int MaxResults { get; init; } = 1;

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
}

public static partial class InternetToolContract
{
    private const int DefaultMaxResults = 1;
    private const int HardMaxResults = 10;

    public static bool TryParseRequest(string text, out InternetToolRequest request, out string error)
    {
        request = new InternetToolRequest();
        error = "";

        var json = ExtractJsonObject(text);
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

        var tool = candidate.Tool.Trim();
        if (!InternetToolNames.All.Contains(tool))
        {
            error = $"Unsupported internet tool '{candidate.Tool}'.";
            return false;
        }

        var query = string.IsNullOrWhiteSpace(candidate.Query)
            ? candidate.Input.Trim()
            : candidate.Query.Trim();
        var url = candidate.Url.Trim();
        if ((tool.Equals(InternetToolNames.WebSearch, StringComparison.OrdinalIgnoreCase)
                || tool.Equals(InternetToolNames.RssSearch, StringComparison.OrdinalIgnoreCase)
                || tool.Equals(InternetToolNames.SummarizeSources, StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrWhiteSpace(query))
        {
            error = $"{tool} requires a query.";
            return false;
        }

        if (tool.Equals(InternetToolNames.FetchUrl, StringComparison.OrdinalIgnoreCase)
            && !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
        {
            error = "fetch_url requires an absolute URL.";
            return false;
        }

        request = new InternetToolRequest
        {
            Tool = tool.ToLowerInvariant(),
            RequesterId = candidate.RequesterId.Trim(),
            Query = query,
            Url = url,
            MaxResults = Math.Clamp(candidate.MaxResults <= 0 ? DefaultMaxResults : candidate.MaxResults, 1, HardMaxResults),
            Reason = candidate.Reason.Trim(),
            Options = candidate.Options
        };
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
}
