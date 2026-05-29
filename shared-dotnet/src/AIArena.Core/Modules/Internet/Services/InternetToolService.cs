using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

namespace AIArena.Core.Services;

public interface IInternetToolProvider
{
    Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, ModelRssSettings settings, CancellationToken cancellationToken = default);
}

public sealed partial class InternetToolService
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly string[] DefaultTrustedSourceUrls =
    [
        "https://feeds.bbci.co.uk/news/world/rss.xml",
        "https://www.theguardian.com/world/rss",
        "https://feeds.npr.org/1001/rss.xml",
        "https://techcrunch.com/feed/",
        "https://www.theverge.com/rss/index.xml"
    ];
    private readonly IInternetToolProvider _provider;
    private readonly EventLogStore? _eventLogStore;

    public InternetToolService(IInternetToolProvider? provider = null, EventLogStore? eventLogStore = null)
    {
        _provider = provider ?? new TrustedRssInternetToolProvider();
        _eventLogStore = eventLogStore;
    }

    public async Task<InternetToolResult> ExecuteAsync(
        ArenaSnapshot snapshot,
        InternetToolRequest request,
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        request = NormalizeRequestTool(request);
        if (!CanExecute(snapshot.Engine.ModelRss, request.RequesterId, out var error))
        {
            var rejected = new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Query = request.Query,
                Url = request.Url,
                Error = error
            };
            await LogAsync(sessionId, request, rejected, cancellationToken);
            return rejected;
        }

        var settings = snapshot.Engine.ModelRss;
        var bounded = new InternetToolRequest
        {
            Tool = request.Tool,
            RequesterId = request.RequesterId,
            Query = request.Query,
            Url = request.Url,
            MaxResults = Math.Clamp(request.MaxResults <= 0 ? settings.MaxResults : request.MaxResults, 1, Math.Clamp(settings.MaxResults <= 0 ? 1 : settings.MaxResults, 1, 10)),
            Reason = request.Reason,
            Options = request.Options
        };

        var (result, cached) = await ExecuteProviderWithCacheAsync(bounded, settings, cancellationToken);
        result = WithCacheState(result, cached);
        await LogAsync(sessionId, bounded, result, cancellationToken, cached);
        return result;
    }

    public async Task<InternetToolResult> ExecuteManualAsync(
        ArenaSnapshot snapshot,
        InternetToolRequest request,
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        request = NormalizeRequestTool(request);
        if (!snapshot.Engine.ModelRss.UseInternet)
        {
            var rejected = new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Query = request.Query,
                Url = request.Url,
                Error = "Internet is off."
            };
            await LogAsync(sessionId, request, rejected, cancellationToken);
            return rejected;
        }

        var mode = string.IsNullOrWhiteSpace(snapshot.Engine.ModelRss.Mode)
            ? "manual"
            : snapshot.Engine.ModelRss.Mode.Trim().ToLowerInvariant();
        if (mode is "off" or "model_requested")
        {
            var rejected = new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Query = request.Query,
                Url = request.Url,
                Error = mode == "off"
                    ? "Internet mode is off."
                    : "Manual internet actions are disabled in Model Requested mode."
            };
            await LogAsync(sessionId, request, rejected, cancellationToken);
            return rejected;
        }

        var settings = snapshot.Engine.ModelRss;
        var bounded = new InternetToolRequest
        {
            Tool = request.Tool,
            RequesterId = request.RequesterId,
            Query = request.Query,
            Url = request.Url,
            MaxResults = Math.Clamp(request.MaxResults <= 0 ? settings.MaxResults : request.MaxResults, 1, Math.Clamp(settings.MaxResults <= 0 ? 1 : settings.MaxResults, 1, 10)),
            Reason = request.Reason,
            Options = request.Options
        };
        var (result, cached) = await ExecuteProviderWithCacheAsync(bounded, settings, cancellationToken);
        result = WithCacheState(result, cached);
        await LogAsync(sessionId, bounded, result, cancellationToken, cached);
        return result;
    }

    public static bool CanExecute(ModelRssSettings settings, string requesterId, out string error)
    {
        error = "";
        if (!settings.UseInternet)
        {
            error = "Internet is off.";
            return false;
        }

        var mode = string.IsNullOrWhiteSpace(settings.Mode) ? "manual" : settings.Mode.Trim().ToLowerInvariant();
        if (mode is "off")
        {
            error = "Internet mode is off.";
            return false;
        }

        if (mode is not ("model_requested" or "auto"))
        {
            error = "Model-requested internet is not enabled.";
            return false;
        }

        if (requesterId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            if (!settings.AllowNarratorRequests)
            {
                error = "Narrator internet requests are disabled.";
                return false;
            }
            return true;
        }

        if (!settings.AllowModelRss && !settings.AllowParticipantRequests)
        {
            error = "Participant internet requests are disabled.";
            return false;
        }

        return true;
    }

    private static string CacheKey(InternetToolRequest request, ModelRssSettings settings)
    {
        var sources = string.Join(",", settings.AllowedSources.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        return $"{request.Tool}|{request.Query}|{request.Url}|{request.MaxResults}|{NormalizeSourceScope(settings.SourceScope)}|{sources}";
    }

    internal static string NormalizeSourceScope(string value)
    {
        return value.Trim().Equals("open_web", StringComparison.OrdinalIgnoreCase) ? "open_web" : "trusted";
    }

    private async Task<(InternetToolResult Result, bool Cached)> ExecuteProviderWithCacheAsync(
        InternetToolRequest request,
        ModelRssSettings settings,
        CancellationToken cancellationToken)
    {
        if (request.Tool.Equals(InternetToolNames.FetchUrl, StringComparison.OrdinalIgnoreCase)
            && NormalizeSourceScope(settings.SourceScope) != "open_web"
            && !IsTrustedUrl(request.Url, settings))
        {
            return (new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Url = request.Url,
                Error = "Direct URL fetch is limited to trusted sources. Enable Open web allowed in App Settings to fetch arbitrary URLs."
            }, false);
        }

        var cacheKey = CacheKey(request, settings);
        if (Cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.Now - cached.StoredAt <= CacheTtl)
        {
            return (cached.Result, true);
        }

        var result = await _provider.ExecuteAsync(request, settings, cancellationToken);
        if (result.Ok)
        {
            Cache[cacheKey] = new CacheEntry(result, DateTimeOffset.Now);
        }

        return (result, false);
    }

    private static InternetToolRequest NormalizeRequestTool(InternetToolRequest request)
    {
        if (!request.Tool.Equals(InternetToolNames.WebSearch, StringComparison.OrdinalIgnoreCase)
            || !LooksLikeBareDomain(request.Query, out var url))
        {
            return request;
        }

        return new InternetToolRequest
        {
            Tool = InternetToolNames.FetchUrl,
            RequesterId = request.RequesterId,
            Query = request.Query,
            Url = url,
            MaxResults = request.MaxResults,
            Reason = string.IsNullOrWhiteSpace(request.Reason)
                ? "Converted domain search to direct URL fetch."
                : request.Reason,
            Options = request.Options
        };
    }

    private static bool LooksLikeBareDomain(string value, out string url)
    {
        url = "";
        var trimmed = value.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('?'))
        {
            return false;
        }

        if (!DomainRegex().IsMatch(trimmed))
        {
            return false;
        }

        url = $"https://{trimmed}";
        return true;
    }

    private static bool IsTrustedUrl(string url, ModelRssSettings settings)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requested))
        {
            return false;
        }

        IEnumerable<string> trustedSources = settings.AllowedSources.Count == 0
            ? DefaultTrustedSourceUrls
            : settings.AllowedSources;
        return trustedSources.Any(source =>
            Uri.TryCreate(source, UriKind.Absolute, out var trusted)
            && requested.Host.Equals(trusted.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static InternetToolResult WithCacheState(InternetToolResult result, bool cached)
    {
        return new InternetToolResult
        {
            Ok = result.Ok,
            Tool = result.Tool,
            Query = result.Query,
            Url = result.Url,
            Summary = result.Summary,
            Sources = result.Sources,
            Error = result.Error,
            CheckedAt = result.CheckedAt,
            Cached = cached
        };
    }

    private async Task LogAsync(string sessionId, InternetToolRequest request, InternetToolResult result, CancellationToken cancellationToken, bool cached = false)
    {
        if (_eventLogStore is null)
        {
            return;
        }

        await _eventLogStore.AppendAsync(
            sessionId,
            result.Ok ? "native_internet_tool_completed" : "native_internet_tool_failed",
            new
            {
                request.Tool,
                request.RequesterId,
                request.Query,
                request.Url,
                result.Ok,
                result.Error,
                source_count = result.Sources.Count,
                cached
            },
            cancellationToken);
    }

    private sealed record CacheEntry(InternetToolResult Result, DateTimeOffset StoredAt);

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();
}

public sealed partial class TrustedRssInternetToolProvider : IInternetToolProvider
{
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "against", "also", "arena", "because", "before", "being",
        "between", "could", "current", "data", "debate", "discussion", "does", "each", "from",
        "have", "here", "into", "model", "must", "news", "once", "open", "phase", "please",
        "proposed", "should", "since", "that", "their", "there", "these", "this", "those",
        "through", "turn", "turns", "what", "when", "where", "which", "while", "with", "would",
        "your"
    };

    private static readonly Dictionary<string, string> TrustedFeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bbc-world"] = "https://feeds.bbci.co.uk/news/world/rss.xml",
        ["guardian-world"] = "https://www.theguardian.com/world/rss",
        ["npr-news"] = "https://feeds.npr.org/1001/rss.xml",
        ["techcrunch"] = "https://techcrunch.com/feed/",
        ["the-verge"] = "https://www.theverge.com/rss/index.xml"
    };

    private readonly HttpClient _httpClient;

    public TrustedRssInternetToolProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, ModelRssSettings settings, CancellationToken cancellationToken = default)
    {
        return request.Tool switch
        {
            InternetToolNames.RssSearch or InternetToolNames.WebSearch => await SearchRssAsync(request, settings, cancellationToken),
            InternetToolNames.FetchUrl => await FetchUrlAsync(request, cancellationToken),
            InternetToolNames.SummarizeSources => await SearchRssAsync(request, settings, cancellationToken),
            _ => new InternetToolResult { Ok = false, Tool = request.Tool, Query = request.Query, Url = request.Url, Error = $"Unsupported internet tool '{request.Tool}'." }
        };
    }

    private async Task<InternetToolResult> SearchRssAsync(InternetToolRequest request, ModelRssSettings settings, CancellationToken cancellationToken)
    {
        var feeds = ResolveFeeds(settings);
        if (feeds.Count == 0)
        {
            return new InternetToolResult { Ok = false, Tool = request.Tool, Query = request.Query, Error = "No trusted RSS sources are configured." };
        }

        var sources = new List<InternetToolSource>();
        foreach (var (name, url) in feeds)
        {
            try
            {
                using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                sources.AddRange(ParseRss(document, name, request.Query));
            }
            catch
            {
                // One broken feed should not break the whole tool result.
            }
        }

        var ranked = sources
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.PublishedAt)
            .Take(Math.Clamp(request.MaxResults, 1, 10))
            .ToArray();

        return new InternetToolResult
        {
            Ok = ranked.Length > 0,
            Tool = request.Tool,
            Query = request.Query,
            Summary = ranked.Length == 0
                ? $"No trusted RSS matches found for: {request.Query}"
                : $"Found {ranked.Length} trusted RSS match(es) for: {request.Query}",
            Sources = ranked,
            Error = ranked.Length == 0 ? "No matching trusted RSS items found." : ""
        };
    }

    private async Task<InternetToolResult> FetchUrlAsync(InternetToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _httpClient.GetStringAsync(request.Url, cancellationToken);
            var primary = ExtractPage(request.Url, text);
            var followed = await TryFetchRelevantSameDomainLinkAsync(request, text, cancellationToken);
            var page = followed ?? primary;
            var snippet = TrimSnippet(page.Snippet, followed is null ? 1600 : 2600);
            var summary = string.IsNullOrWhiteSpace(page.Title) ? snippet : $"{page.Title}{Environment.NewLine}{snippet}";
            if (followed is not null)
            {
                summary = $"Followed relevant same-domain link from {request.Url}:{Environment.NewLine}{summary}";
            }

            return new InternetToolResult
            {
                Ok = true,
                Tool = request.Tool,
                Url = request.Url,
                Summary = summary,
                Sources =
                [
                    new InternetToolSource
                    {
                        Title = string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title,
                        Url = page.Url,
                        Source = "direct-url",
                        PublishedAt = page.PublishedAt,
                        Snippet = snippet,
                        Score = followed is null ? 1 : 2
                    }
                ]
            };
        }
        catch (Exception ex)
        {
            return new InternetToolResult { Ok = false, Tool = request.Tool, Url = request.Url, Error = ex.Message };
        }
    }

    private async Task<FetchedPage?> TryFetchRelevantSameDomainLinkAsync(InternetToolRequest request, string html, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var terms = MeaningfulTerms($"{request.Query} {request.Reason}");
        if (terms.Count == 0)
        {
            return null;
        }

        var best = ExtractLinks(html, baseUri)
            .Where(link => link.Uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
                && !SameUrl(link.Uri, baseUri))
            .Select(link => new
            {
                Link = link,
                Score = ScoreLink(link, terms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Link.Uri.AbsoluteUri.Length)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        try
        {
            var text = await _httpClient.GetStringAsync(best.Link.Uri, cancellationToken);
            return ExtractPage(best.Link.Uri.AbsoluteUri, text);
        }
        catch
        {
            return null;
        }
    }

    private static FetchedPage ExtractPage(string url, string html)
    {
        var title = ExtractTitle(html);
        var canonical = ExtractLinkRel(html, "canonical");
        var published = ExtractMeta(html, "article:published_time");
        return new FetchedPage(
            string.IsNullOrWhiteSpace(canonical) ? url : canonical,
            title,
            ExtractReadableText(html),
            ParseDate(published));
    }

    private static IReadOnlyList<PageLink> ExtractLinks(string html, Uri baseUri)
    {
        return AnchorRegex().Matches(html)
            .Select(match =>
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
                var label = CleanText(match.Groups["body"].Value);
                return Uri.TryCreate(baseUri, href, out var uri)
                    ? new PageLink(uri, label)
                    : null;
            })
            .Where(link => link is not null)
            .Select(link => link!)
            .DistinctBy(link => link.Uri.AbsoluteUri)
            .ToArray();
    }

    private static int ScoreLink(PageLink link, IReadOnlyList<string> terms)
    {
        var haystack = $"{link.Text} {link.Uri.AbsolutePath}".ToLowerInvariant();
        return terms.Sum(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }

    private static bool SameUrl(Uri left, Uri right)
    {
        return left.GetLeftPart(UriPartial.Path).TrimEnd('/').Equals(right.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSnippet(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength].Trim();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ResolveFeeds(ModelRssSettings settings)
    {
        if (settings.AllowedSources.Count == 0)
        {
            return TrustedFeeds.Take(3).ToArray();
        }

        return settings.AllowedSources
            .Select(source => TrustedFeeds.TryGetValue(source, out var url)
                ? new KeyValuePair<string, string>(source, url)
                : Uri.TryCreate(source, UriKind.Absolute, out _) ? new KeyValuePair<string, string>(source, source) : default)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToArray();
    }

    private static IEnumerable<InternetToolSource> ParseRss(XDocument document, string sourceName, string query)
    {
        var terms = MeaningfulTerms(query);
        return document.Descendants()
            .Where(element => element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var title = ChildValue(item, "title");
                var url = ChildValue(item, "link");
                if (string.IsNullOrWhiteSpace(url))
                {
                    url = item.Elements().FirstOrDefault(element => element.Name.LocalName == "link")?.Attribute("href")?.Value ?? "";
                }

                var snippet = CleanText(ChildValue(item, "description"));
                if (string.IsNullOrWhiteSpace(snippet))
                {
                    snippet = CleanText(ChildValue(item, "summary"));
                }

                var normalizedTitle = title.ToLowerInvariant();
                var normalizedSnippet = snippet.ToLowerInvariant();
                var score = terms.Count == 0
                    ? 0
                    : terms.Sum(term =>
                        (normalizedTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                        + (normalizedSnippet.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                return new InternetToolSource
                {
                    Title = WebUtility.HtmlDecode(title),
                    Url = url,
                    Source = sourceName,
                    PublishedAt = ParseDate(ChildValue(item, "pubDate")) ?? ParseDate(ChildValue(item, "updated")),
                    Snippet = WebUtility.HtmlDecode(snippet),
                    Score = score
                };
            })
            .Where(item => item.Score > 0 && !string.IsNullOrWhiteSpace(item.Title));
    }

    private static IReadOnlyList<string> MeaningfulTerms(string query)
    {
        return SearchTokenRegex().Matches(query)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(token => (token.Length >= 3 || token.Equals("ai", StringComparison.OrdinalIgnoreCase)) && !SearchStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    private static string ChildValue(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string CleanText(string text)
    {
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(HtmlTagRegex().Replace(text, " "), " ").Trim());
    }

    private static string ExtractReadableText(string html)
    {
        var cleaned = ScriptStyleRegex().Replace(html, " ");
        var article = ArticleRegex().Match(cleaned);
        if (article.Success)
        {
            cleaned = article.Groups["body"].Value;
        }

        return CleanText(cleaned);
    }

    private static string ExtractTitle(string html)
    {
        var og = ExtractMeta(html, "og:title");
        if (!string.IsNullOrWhiteSpace(og))
        {
            return WebUtility.HtmlDecode(og.Trim());
        }

        var match = TitleRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(CleanText(match.Groups["title"].Value)) : "";
    }

    private static string ExtractMeta(string html, string name)
    {
        foreach (Match match in MetaRegex().Matches(html))
        {
            var key = match.Groups["key"].Value;
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(match.Groups["content"].Value.Trim());
            }
        }

        return "";
    }

    private static string ExtractLinkRel(string html, string rel)
    {
        foreach (Match match in LinkRegex().Matches(html))
        {
            if (match.Groups["rel"].Value.Equals(rel, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
            }
        }

        return "";
    }

    private sealed record FetchedPage(string Url, string Title, string Snippet, DateTimeOffset? PublishedAt);

    private sealed record PageLink(Uri Uri, string Text);

    [GeneratedRegex("<(script|style|noscript)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<article[^>]*>(?<body>.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("<title[^>]*>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<meta[^>]+(?:property|name)=[\"'](?<key>[^\"']+)[\"'][^>]+content=[\"'](?<content>[^\"']*)[\"'][^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex("<link[^>]+rel=[\"'](?<rel>[^\"']+)[\"'][^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\-']*")]
    private static partial Regex SearchTokenRegex();
}
