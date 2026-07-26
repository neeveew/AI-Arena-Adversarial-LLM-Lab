namespace AIArena.Wpf.Models;

public sealed record AgentInternetSourceItem(
    string Title,
    string Domain,
    string Url,
    string Snippet,
    string PublishedAt,
    string DisplayText)
{
    public static AgentInternetSourceItem FromDisplayText(string displayText)
    {
        var url = FirstUrl(displayText);
        var domain = DomainLabel(url);
        var segments = displayText
            .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var urlIndex = Array.FindIndex(segments, item => !string.IsNullOrWhiteSpace(url) && item.Contains(url, StringComparison.OrdinalIgnoreCase));
        var title = urlIndex > 0 ? segments[urlIndex - 1] : segments.FirstOrDefault() ?? "";
        var snippet = urlIndex >= 0 && urlIndex < segments.Length - 1
            ? string.Join(" - ", segments.Skip(urlIndex + 1))
            : "";
        return new AgentInternetSourceItem(title, domain, url, snippet, "", displayText);
    }

    private static string FirstUrl(string value)
    {
        var marker = value.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return "";
        }

        var end = value.IndexOfAny([' ', '\r', '\n', '\t'], marker);
        var url = end < 0 ? value[marker..] : value[marker..end];
        return url.TrimEnd('.', ',', ';', ')', ']');
    }

    private static string DomainLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "";
    }
}

public sealed record AgentInternetSourceSummary(
    string Query,
    string CheckedAt,
    IReadOnlyList<string> Sources,
    IReadOnlyList<AgentInternetSourceItem>? SourceItems = null)
{
    public IReadOnlyList<AgentInternetSourceItem> Items => SourceItems is { Count: > 0 }
        ? SourceItems
        : Sources.Select(AgentInternetSourceItem.FromDisplayText).ToArray();
}

public sealed record AgentState(
    string Id,
    string Name,
    string Status,
    string Persona,
    string VoiceStyle,
    string PressureProfile,
    string AccentColor,
    string Model,
    bool Active,
    bool Locked,
    IReadOnlyList<string> PrivateNotes,
    AgentInternetSourceSummary? InternetSources = null)
{
    public bool HasInternetSources => InternetSources?.Sources.Count > 0;
}
