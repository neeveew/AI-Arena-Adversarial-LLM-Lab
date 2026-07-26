using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using PuppeteerSharp;
using SmartReader;

namespace AIArena.Core.Services;

public interface IInternetContextProvider
{
    Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, InternetSettings settings, CancellationToken cancellationToken = default);
}

public interface IInternetToolProvider : IInternetContextProvider;

public sealed partial class InternetToolService : IDisposable
{
    private const int DefaultSearchMaxResults = 5;
    private const int HardSearchMaxResults = 10;
    private const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<InternetToolResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IInternetContextProvider _provider;
    private readonly EventLogStore? _eventLogStore;
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource<bool>? _providerOperationsDrained;
    private int _activeProviderOperations;
    private int _resourcesDisposed;
    private int _disposed;

    public InternetToolService(IInternetContextProvider? provider = null, EventLogStore? eventLogStore = null)
    {
        _provider = provider ?? new LocalInternetToolProvider();
        _eventLogStore = eventLogStore;
    }

    public async Task<InternetToolResult> ExecuteAsync(
        ArenaSnapshot snapshot,
        InternetToolRequest request,
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        if (!InternetToolContract.TryValidate(request, out request, out var contractError))
        {
            return new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool?.Trim().ToLowerInvariant() ?? "",
                Error = contractError,
                CheckedAt = DateTimeOffset.Now
            };
        }

        request = NormalizeRequestTool(request);
        if (!InternetRequestSafety.IsSafeOutboundRequest(request, out var safetyError))
        {
            return new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Error = safetyError,
                CheckedAt = DateTimeOffset.Now
            };
        }

        if (!CanExecute(snapshot.Engine.Internet, request.RequesterId, out var error))
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

        var settings = snapshot.Engine.Internet;
        var bounded = new InternetToolRequest
        {
            Tool = request.Tool,
            RequesterId = request.RequesterId,
            Query = request.Query,
            Url = request.Url,
            MaxResults = ClampSearchMaxResults(request.MaxResults, settings.MaxResults),
            Language = request.Language,
            TimeRange = request.TimeRange,
            Categories = request.Categories,
            Reason = request.Reason,
            Options = request.Options
        };

        if (!ValidateRequest(bounded, out error))
        {
            var rejected = new InternetToolResult
            {
                Ok = false,
                Tool = bounded.Tool,
                Query = bounded.Query,
                Url = bounded.Url,
                Error = error,
                CheckedAt = DateTimeOffset.Now
            };
            await LogAsync(sessionId, bounded, rejected, cancellationToken);
            return rejected;
        }

        var (result, cached) = await ExecuteProviderWithCacheAsync(sessionId, bounded, settings, cancellationToken);
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
        if (!InternetToolContract.TryValidate(request, out request, out var contractError))
        {
            return new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool?.Trim().ToLowerInvariant() ?? "",
                Error = contractError,
                CheckedAt = DateTimeOffset.Now
            };
        }

        request = NormalizeRequestTool(request);
        if (!InternetRequestSafety.IsSafeOutboundRequest(request, out var safetyError))
        {
            return new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Error = safetyError,
                CheckedAt = DateTimeOffset.Now
            };
        }

        if (!snapshot.Engine.Internet.UseInternet)
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

        var settings = snapshot.Engine.Internet;
        var bounded = new InternetToolRequest
        {
            Tool = request.Tool,
            RequesterId = request.RequesterId,
            Query = request.Query,
            Url = request.Url,
            MaxResults = ClampSearchMaxResults(request.MaxResults, settings.MaxResults),
            Language = request.Language,
            TimeRange = request.TimeRange,
            Categories = request.Categories,
            Reason = request.Reason,
            Options = request.Options
        };
        if (!ValidateRequest(bounded, out var error))
        {
            var rejected = new InternetToolResult
            {
                Ok = false,
                Tool = bounded.Tool,
                Query = bounded.Query,
                Url = bounded.Url,
                Error = error,
                CheckedAt = DateTimeOffset.Now
            };
            await LogAsync(sessionId, bounded, rejected, cancellationToken);
            return rejected;
        }

        var (result, cached) = await ExecuteProviderWithCacheAsync(sessionId, bounded, settings, cancellationToken);
        result = WithCacheState(result, cached);
        await LogAsync(sessionId, bounded, result, cancellationToken, cached);
        return result;
    }

    public static bool CanExecute(InternetSettings settings, string requesterId, out string error)
    {
        error = "";
        if (!settings.UseInternet)
        {
            error = "Internet is off.";
            return false;
        }

        return true;
    }

    private static string CacheKey(string sessionId, InternetToolRequest request)
    {
        var searchBackend = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL")?.Trim() ?? "bundled";
        return JsonSerializer.Serialize(new
        {
            Session = sessionId.Trim(),
            Backend = searchBackend,
            request.Tool,
            Query = NormalizeQueryForSafety(request.Query),
            Url = request.Url.Trim(),
            request.MaxResults,
            request.Language,
            request.TimeRange,
            request.Categories
        });
    }

    private static int ClampSearchMaxResults(int requested, int configured)
    {
        var requestLimit = requested <= 0 ? DefaultSearchMaxResults : requested;
        var settingsLimit = configured <= 0 ? DefaultSearchMaxResults : configured;
        return Math.Clamp(Math.Min(requestLimit, settingsLimit), 1, HardSearchMaxResults);
    }

    internal static bool ValidateRequest(InternetToolRequest request, out string error)
    {
        error = "";
        if (request.Tool.Equals(InternetToolNames.FetchUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri))
            {
                error = "Invalid internet request: fetch_url requires a valid http or https URL.";
                return false;
            }

            try
            {
                PublicWebDestinationValidator.ValidateUri(uri);
            }
            catch (HttpRequestException ex)
            {
                error = $"Invalid internet request: {ex.Message}";
                return false;
            }

            return true;
        }

        if (!request.Tool.Equals(InternetToolNames.WebSearch, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Invalid internet request: unsupported tool '{request.Tool}'.";
            return false;
        }

        var query = NormalizeQueryForSafety(request.Query);
        if (string.IsNullOrWhiteSpace(query))
        {
            error = "Invalid internet request: search query is empty.";
            return false;
        }

        if (request.Query.Any(char.IsControl))
        {
            error = "Invalid internet request: search query contains control characters.";
            return false;
        }

        if (query.Length > 500)
        {
            error = "Invalid internet request: search query is too long.";
            return false;
        }

        return true;
    }

    private static string NormalizeQueryForSafety(string query)
    {
        return string.Join(
            " ",
            (query ?? "")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task<(InternetToolResult Result, bool Cached)> ExecuteProviderWithCacheAsync(
        string sessionId,
        InternetToolRequest request,
        InternetSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = CacheKey(sessionId, request);
        var now = DateTimeOffset.UtcNow;
        var cacheTtl = TimeSpan.FromMinutes(Math.Clamp(settings.SourceFreshnessMinutes, 1, 1440));
        if (_cache.TryGetValue(cacheKey, out var cached) && now - cached.StoredAt <= cacheTtl)
        {
            return (cached.Result, true);
        }

        var candidate = new Lazy<Task<InternetToolResult>>(
            () => ExecuteProviderAndCacheAsync(cacheKey, request, settings),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var operation = _inflight.GetOrAdd(cacheKey, candidate);
        var coalesced = !ReferenceEquals(candidate, operation);
        if (!coalesced)
        {
            _ = RemoveInflightWhenCompleteAsync(cacheKey, operation);
        }

        var result = await operation.Value.WaitAsync(cancellationToken);
        return (result, coalesced);
    }

    private async Task<InternetToolResult> ExecuteProviderAndCacheAsync(
        string cacheKey,
        InternetToolRequest request,
        InternetSettings settings)
    {
        var cancellationToken = EnterProviderOperation();
        try
        {
            var result = await _provider.ExecuteAsync(request, settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Ok)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    foreach (var staleKey in _cache
                        .OrderBy(entry => entry.Value.StoredAt)
                        .Take(Math.Max(1, _cache.Count - MaxCacheEntries + 1))
                        .Select(entry => entry.Key))
                    {
                        _cache.TryRemove(staleKey, out _);
                    }
                }

                _cache[cacheKey] = new CacheEntry(result, DateTimeOffset.UtcNow);
            }

            return result;
        }
        finally
        {
            ExitProviderOperation();
        }
    }

    private CancellationToken EnterProviderOperation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var cancellationToken = _shutdown.Token;
            _activeProviderOperations++;
            return cancellationToken;
        }
    }

    private void ExitProviderOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_lifecycleGate)
        {
            if (--_activeProviderOperations == 0)
            {
                drained = _providerOperationsDrained;
            }
        }

        drained?.TrySetResult(true);
    }

    private async Task RemoveInflightWhenCompleteAsync(
        string cacheKey,
        Lazy<Task<InternetToolResult>> operation)
    {
        try
        {
            await operation.Value;
        }
        catch
        {
            // The initiating caller receives the provider failure. This continuation
            // exists only to make failed single-flight entries removable.
        }
        finally
        {
            if (_inflight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, operation))
            {
                _inflight.TryRemove(cacheKey, out _);
            }
        }
    }

    private static InternetToolRequest NormalizeRequestTool(InternetToolRequest request)
    {
        if (!string.Equals(request.Tool, InternetToolNames.WebSearch, StringComparison.OrdinalIgnoreCase)
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
            Language = request.Language,
            TimeRange = request.TimeRange,
            Categories = request.Categories,
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
            Cached = cached,
            Quality = result.Quality
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
                result.Quality,
                source_count = result.Sources.Count,
                cached
            },
            cancellationToken);
    }

    private sealed record CacheEntry(InternetToolResult Result, DateTimeOffset StoredAt);

    public void Dispose()
    {
        Task providerOperationsDrained;
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            if (_activeProviderOperations == 0)
            {
                providerOperationsDrained = Task.CompletedTask;
            }
            else
            {
                _providerOperationsDrained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                providerOperationsDrained = _providerOperationsDrained.Task;
            }
        }

        CancelShutdownBestEffort();
        if (providerOperationsDrained.IsCompletedSuccessfully)
        {
            DisposeResources();
            return;
        }

        _ = DisposeResourcesAfterDrainAsync(providerOperationsDrained);
    }

    private void CancelShutdownBestEffort()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // Cancellation invokes every callback before reporting callback failures.
            // Cleanup must still continue so provider resources are not leaked.
        }
    }

    private async Task DisposeResourcesAfterDrainAsync(Task providerOperationsDrained)
    {
        try
        {
            await providerOperationsDrained.ConfigureAwait(false);
            DisposeResources();
        }
        catch
        {
            // IDisposable cannot surface failures after an asynchronous drain.
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();
}

public interface ISearxngSearchClient
{
    Task<string> SearchJsonAsync(string query, int maxResults, CancellationToken cancellationToken = default);

    Task<string> SearchJsonAsync(
        string query,
        int maxResults,
        SearxngSearchParameters parameters,
        CancellationToken cancellationToken = default)
    {
        return SearchJsonAsync(query, maxResults, cancellationToken);
    }
}

public interface IReadablePageExtractor
{
    FetchedPage Extract(string url, string html);
}

public interface IBrowserPageRenderer : IDisposable
{
    Task<string> RenderHtmlAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record FetchedPage(string Url, string Title, string Snippet, DateTimeOffset? PublishedAt);

internal sealed class SearxngSearchClient : ISearxngSearchClient
{
    private const int MaximumJsonBytes = 1024 * 1024;
    private static readonly Uri DefaultBaseUrl = new("http://localhost:8081");
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly string _configurationError;

    public SearxngSearchClient(HttpClient httpClient, Uri? baseUrl = null)
    {
        _httpClient = httpClient;
        if (TryResolveBaseUrl(baseUrl, out var resolved, out var error))
        {
            _baseUrl = resolved;
            _configurationError = "";
        }
        else
        {
            _baseUrl = DefaultBaseUrl;
            _configurationError = error;
        }
    }

    public async Task<string> SearchJsonAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        return await SearchJsonAsync(query, maxResults, new SearxngSearchParameters(), cancellationToken);
    }

    public async Task<string> SearchJsonAsync(
        string query,
        int maxResults,
        SearxngSearchParameters parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_configurationError))
        {
            throw new InvalidOperationException(_configurationError);
        }

        var queryParameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query)}",
            "format=json",
            "safesearch=0",
            $"categories={Uri.EscapeDataString(string.IsNullOrWhiteSpace(parameters.Categories) ? "general" : parameters.Categories)}"
        };
        if (!string.IsNullOrWhiteSpace(parameters.Language)
            && !parameters.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            queryParameters.Add($"language={Uri.EscapeDataString(parameters.Language)}");
        }

        if (!string.IsNullOrWhiteSpace(parameters.TimeRange))
        {
            queryParameters.Add($"time_range={Uri.EscapeDataString(parameters.TimeRange)}");
        }

        var builder = new UriBuilder(new Uri(_baseUrl, "search"))
        {
            Query = string.Join('&', queryParameters)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"SearXNG returned HTTP {(int)response.StatusCode}.");
        }

        var json = await BoundedTextContentReader.ReadAsync(response.Content, MaximumJsonBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("SearXNG returned an empty response.");
        }

        return json;
    }

    internal static Uri ResolveBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
        return TryResolveBaseUrl(
            Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) ? configuredUri : null,
            out var uri,
            out _)
            ? uri
            : DefaultBaseUrl;
    }

    private static bool TryResolveBaseUrl(Uri? configured, out Uri baseUrl, out string error)
    {
        baseUrl = NormalizeBaseUrl(DefaultBaseUrl);
        error = "";
        if (configured is null)
        {
            var configuredText = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
            if (string.IsNullOrWhiteSpace(configuredText))
            {
                return true;
            }

            if (!Uri.TryCreate(configuredText, UriKind.Absolute, out configured))
            {
                error = "Configured SearXNG URL must be a credential-free absolute HTTP or HTTPS URL.";
                return false;
            }
        }

        if ((configured.Scheme != Uri.UriSchemeHttp && configured.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(configured.Host)
            || !string.IsNullOrEmpty(configured.UserInfo))
        {
            error = "Configured SearXNG URL must be a credential-free absolute HTTP or HTTPS URL.";
            return false;
        }

        if (configured.Scheme == Uri.UriSchemeHttp
            && !configured.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(configured.Host, out var address) && IPAddress.IsLoopback(address)))
        {
            error = "Configured remote SearXNG URLs must use HTTPS; HTTP is allowed only on loopback.";
            return false;
        }

        if (!string.IsNullOrEmpty(configured.Query) || !string.IsNullOrEmpty(configured.Fragment))
        {
            error = "Configured SearXNG URL must not include a query string or fragment.";
            return false;
        }

        baseUrl = NormalizeBaseUrl(configured);
        return true;
    }

    private static Uri NormalizeBaseUrl(Uri baseUrl)
    {
        var text = baseUrl.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(text);
    }
}

public sealed partial class SmartReaderPageExtractor : IReadablePageExtractor
{
    private const string UserAgent = "AI Arena local internet access";

    public FetchedPage Extract(string url, string html)
    {
        try
        {
            var article = Reader.ParseArticle(url, html, UserAgent);
            var title = FirstNonEmpty(article.Title, ExtractTitle(html));
            var text = FirstNonEmpty(article.TextContent, article.Excerpt, ExtractReadableTextFallback(html));
            var published = article.PublicationDate is null
                ? ParseDate(ExtractMetaFallback(html, "article:published_time"))
                : new DateTimeOffset(DateTime.SpecifyKind(article.PublicationDate.Value, DateTimeKind.Utc));
            return new FetchedPage(
                CanonicalOrOriginalUrl(url, html),
                title,
                CleanTextFallback(text),
                published);
        }
        catch
        {
            return new FetchedPage(
                CanonicalOrOriginalUrl(url, html),
                ExtractTitle(html),
                ExtractReadableTextFallback(html),
                ParseDate(ExtractMetaFallback(html, "article:published_time")));
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }

    private static string CanonicalOrOriginalUrl(string url, string html)
    {
        var canonical = ExtractLinkRelFallback(html, "canonical");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var original)
            || string.IsNullOrWhiteSpace(canonical)
            || !Uri.TryCreate(original, canonical, out var candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !candidate.Scheme.Equals(original.Scheme, StringComparison.OrdinalIgnoreCase)
            || !candidate.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != original.Port)
        {
            return url;
        }

        return candidate.AbsoluteUri;
    }

    private static string ExtractReadableTextFallback(string html)
    {
        var cleaned = ScriptStyleRegex().Replace(html, " ");
        var article = ArticleRegex().Match(cleaned);
        if (article.Success)
        {
            cleaned = article.Groups["body"].Value;
        }

        return CleanTextFallback(cleaned);
    }

    private static string ExtractTitle(string html)
    {
        var og = ExtractMetaFallback(html, "og:title");
        if (!string.IsNullOrWhiteSpace(og))
        {
            return WebUtility.HtmlDecode(og.Trim());
        }

        var match = TitleRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(CleanTextFallback(match.Groups["title"].Value)) : "";
    }

    private static string ExtractMetaFallback(string html, string name)
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

    private static string ExtractLinkRelFallback(string html, string rel)
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

    private static DateTimeOffset? ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string CleanTextFallback(string text)
    {
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(HtmlTagRegex().Replace(text, " "), " ").Trim());
    }

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

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed record BrowserExecutableResolution(bool Ok, string ExecutablePath, string Error);

internal static class BrowserExecutableResolver
{
    internal static BrowserExecutableResolution Resolve()
    {
        return Resolve(
            Environment.GetEnvironmentVariable("AIARENA_CHROME_PATH"),
            InstalledBrowserCandidates(),
            File.Exists);
    }

    internal static BrowserExecutableResolution Resolve(
        string? configuredPath,
        IEnumerable<string> installedCandidates,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(installedCandidates);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var explicitPath = configuredPath.Trim().Trim('"');
            return fileExists(explicitPath)
                ? new BrowserExecutableResolution(true, explicitPath, "")
                : new BrowserExecutableResolution(
                    false,
                    "",
                    $"AIARENA_CHROME_PATH points to a browser executable that does not exist: {explicitPath}");
        }

        foreach (var candidate in installedCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (fileExists(candidate))
            {
                return new BrowserExecutableResolution(true, candidate, "");
            }
        }

        return new BrowserExecutableResolution(
            false,
            "",
            "Browser fallback is unavailable. Install Microsoft Edge or Google Chrome, or set AIARENA_CHROME_PATH to an existing browser executable.");
    }

    private static IReadOnlyList<string> InstalledBrowserCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
        ];
    }
}

internal static class BrowserResourcePolicy
{
    internal static bool IsAllowed(IRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsAllowed(request.Method.Method, request.Url, request.ResourceType);
    }

    internal static bool IsAllowed(string method, string url, ResourceType resourceType)
    {
        return method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url, UriKind.Absolute, out _)
            && IsAllowedResourceType(resourceType);
    }

    internal static bool IsAllowedResourceType(ResourceType resourceType)
    {
        return resourceType is ResourceType.Document
            or ResourceType.Script
            or ResourceType.StyleSheet
            or ResourceType.Xhr
            or ResourceType.Fetch;
    }
}

internal sealed class BrowserRequestBudget
{
    internal const int DefaultMaximumRequests = 48;
    internal const int DefaultMaximumUtf8Bytes = 12 * 1024 * 1024;
    internal const int DefaultMaximumConcurrency = 4;
    private readonly int _maximumRequests;
    private readonly int _maximumUtf8Bytes;
    private int _requestCount;
    private int _consumedUtf8Bytes;

    internal BrowserRequestBudget(
        int maximumRequests = DefaultMaximumRequests,
        int maximumUtf8Bytes = DefaultMaximumUtf8Bytes)
    {
        if (maximumRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        }

        if (maximumUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        }

        _maximumRequests = maximumRequests;
        _maximumUtf8Bytes = maximumUtf8Bytes;
    }

    internal int RemainingUtf8Bytes => Math.Max(0, _maximumUtf8Bytes - Volatile.Read(ref _consumedUtf8Bytes));

    internal bool TryStartRequest()
    {
        return Interlocked.Increment(ref _requestCount) <= _maximumRequests;
    }

    internal bool TryConsume(string content)
    {
        var bytes = Encoding.UTF8.GetByteCount(content ?? "");
        while (true)
        {
            var consumed = Volatile.Read(ref _consumedUtf8Bytes);
            if (bytes > _maximumUtf8Bytes - consumed)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _consumedUtf8Bytes, consumed + bytes, consumed) == consumed)
            {
                return true;
            }
        }
    }
}

internal sealed class BrowserRenderLifecycle : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource<bool>? _drained;
    private int _activeRenders;
    private bool _stopping;
    private bool _disposed;

    internal bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _stopping;
            }
        }
    }

    internal BrowserRenderLease Enter(CancellationToken cancellationToken, TimeSpan timeout)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            if (_activeRenders++ == 0)
            {
                _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            linked.CancelAfter(timeout);
            return new BrowserRenderLease(this, linked);
        }
        catch
        {
            Exit();
            throw;
        }
    }

    internal void StopAndDrain()
    {
        BeginStop().GetAwaiter().GetResult();
    }

    internal Task BeginStop()
    {
        Task drain;
        var cancel = false;
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                cancel = true;
            }

            drain = _activeRenders == 0
                ? Task.CompletedTask
                : _drained!.Task;
        }

        if (cancel)
        {
            _shutdown.Cancel();
        }

        return drain;
    }

    public void Dispose()
    {
        StopAndDrain();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Dispose();
    }

    private void Exit()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_sync)
        {
            if (--_activeRenders == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult(true);
    }

    internal sealed class BrowserRenderLease : IDisposable
    {
        private BrowserRenderLifecycle? _owner;
        private readonly CancellationTokenSource _cancellation;

        internal BrowserRenderLease(BrowserRenderLifecycle owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        internal CancellationToken Token => _cancellation.Token;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            _cancellation.Dispose();
            owner.Exit();
        }
    }
}

internal sealed class PuppeteerSharpPageRenderer : IBrowserPageRenderer
{
    private const int MaxRenderedCharacters = 2_000_000;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(20);
    private const string BrowserNetworkIsolationScript = """
        (() => {
          const blocked = () => { throw new DOMException('Blocked by AI Arena browser policy', 'SecurityError'); };
          const blockedConstructor = class { constructor() { blocked(); } };
          for (const name of ['WebSocket', 'EventSource', 'WebTransport', 'Worker', 'SharedWorker', 'RTCPeerConnection', 'webkitRTCPeerConnection']) {
            try { Object.defineProperty(globalThis, name, { value: blockedConstructor, configurable: false, writable: false }); } catch {}
          }
          try { Object.defineProperty(globalThis, 'open', { value: () => null, configurable: false, writable: false }); } catch {}
          try { Object.defineProperty(navigator, 'sendBeacon', { value: () => false, configurable: false, writable: false }); } catch {}
          try {
            if (navigator.serviceWorker) {
              Object.defineProperty(navigator.serviceWorker, 'register', {
                value: () => Promise.reject(new DOMException('Blocked by AI Arena browser policy', 'SecurityError')),
                configurable: false,
                writable: false
              });
            }
          } catch {}
          try {
            if (navigator.mediaDevices) {
              Object.defineProperty(navigator.mediaDevices, 'getUserMedia', {
                value: () => Promise.reject(new DOMException('Blocked by AI Arena browser policy', 'SecurityError')),
                configurable: false,
                writable: false
              });
            }
          } catch {}
        })()
        """;

    private readonly object _disposeSync = new();
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly SemaphoreSlim _renderLock = new(2, 2);
    private readonly PublicWebFetcher _safeFetcher = new();
    private readonly BrowserRenderLifecycle _lifecycle = new();
    private TaskCompletionSource<bool>? _disposeCompletion;
    private IBrowser? _browser;

    public async Task<string> RenderHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        using var lease = _lifecycle.Enter(cancellationToken, RenderTimeout);
        var enteredRenderLock = false;
        await _renderLock.WaitAsync(lease.Token).ConfigureAwait(false);
        enteredRenderLock = true;
        try
        {
            var browser = await GetBrowserAsync(lease.Token).ConfigureAwait(false);
            return await RenderInIsolatedContextAsync(browser, url, lease.Token).ConfigureAwait(false);
        }
        finally
        {
            if (enteredRenderLock)
            {
                _renderLock.Release();
            }
        }
    }

    public void Dispose()
    {
        Task completion;
        Task? renderDrain = null;
        var ownsShutdown = false;
        lock (_disposeSync)
        {
            if (_disposeCompletion is null)
            {
                _disposeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ownsShutdown = true;
                renderDrain = _lifecycle.BeginStop();
            }

            completion = _disposeCompletion.Task;
        }

        if (!ownsShutdown)
        {
            completion.GetAwaiter().GetResult();
            return;
        }

        try
        {
            renderDrain!.GetAwaiter().GetResult();
            try
            {
                _browser?.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                // Browser shutdown is best-effort during app close.
            }

            _browserLock.Dispose();
            _renderLock.Dispose();
            _safeFetcher.Dispose();
            _lifecycle.Dispose();
        }
        finally
        {
            _disposeCompletion.TrySetResult(true);
        }
    }

    private async Task<string> RenderInIsolatedContextAsync(
        IBrowser browser,
        string url,
        CancellationToken cancellationToken)
    {
        IBrowserContext? context = null;
        IPage? page = null;
        var tracker = new BrowserRequestTracker(BrowserRequestBudget.DefaultMaximumRequests + 1);
        var budget = new BrowserRequestBudget();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var requestConcurrency = new SemaphoreSlim(BrowserRequestBudget.DefaultMaximumConcurrency);
        EventHandler<RequestEventArgs>? requestHandler = null;
        try
        {
            var renderToken = requestCancellation.Token;
            context = await browser.CreateBrowserContextAsync().WaitAsync(renderToken).ConfigureAwait(false);
            page = await context.NewPageAsync().WaitAsync(renderToken).ConfigureAwait(false);
            await page.SetUserAgentAsync("AI Arena local internet access", null).WaitAsync(renderToken).ConfigureAwait(false);
            await page.SetBypassServiceWorkerAsync(true).WaitAsync(renderToken).ConfigureAwait(false);
            await page.EvaluateExpressionOnNewDocumentAsync(BrowserNetworkIsolationScript).WaitAsync(renderToken).ConfigureAwait(false);
            await page.SetRequestInterceptionAsync(true).WaitAsync(renderToken).ConfigureAwait(false);
            requestHandler = (_, eventArgs) =>
            {
                if (!tracker.TryRun(() => HandleInterceptedRequestAsync(
                    eventArgs.Request,
                    budget,
                    requestConcurrency,
                    requestCancellation)))
                {
                    CancelBestEffort(requestCancellation);
                }
            };
            page.Request += requestHandler;

            await page.GoToAsync(url, 12000, [WaitUntilNavigation.DOMContentLoaded]).WaitAsync(renderToken).ConfigureAwait(false);
            await Task.Delay(250, renderToken).ConfigureAwait(false);
            return await page.EvaluateExpressionAsync<string>(
                    $"document.documentElement?.outerHTML?.slice(0, {MaxRenderedCharacters}) ?? ''")
                .WaitAsync(renderToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (page is not null && requestHandler is not null)
            {
                page.Request -= requestHandler;
            }

            requestCancellation.Cancel();
            await tracker.StopAndDrainAsync().ConfigureAwait(false);
            if (context is not null)
            {
                try
                {
                    await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Closing an isolated context is best-effort after handlers drain.
                }
            }
        }
    }

    private async Task HandleInterceptedRequestAsync(
        IRequest request,
        BrowserRequestBudget budget,
        SemaphoreSlim requestConcurrency,
        CancellationTokenSource renderCancellation)
    {
        if (!budget.TryStartRequest())
        {
            CancelBestEffort(renderCancellation);
            return;
        }

        if (!BrowserResourcePolicy.IsAllowed(request))
        {
            await AbortBestEffortAsync(request).ConfigureAwait(false);
            return;
        }

        await FulfillSafeRequestAsync(
                request,
                budget,
                requestConcurrency,
                renderCancellation.Token)
            .ConfigureAwait(false);
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            var resolution = BrowserExecutableResolver.Resolve();
            if (!resolution.Ok)
            {
                throw new InvalidOperationException(resolution.Error);
            }

            var launched = await Puppeteer.LaunchAsync(CreateLaunchOptions(resolution.ExecutablePath)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await launched.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // A canceled launch is never published for reuse.
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            _browser = launched;
            return launched;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    internal static LaunchOptions CreateLaunchOptions(string executablePath)
    {
        return new LaunchOptions
        {
            Headless = true,
            ExecutablePath = executablePath,
            Pipe = true,
            Timeout = 12000,
            Args =
            [
                "--disable-background-networking",
                "--disable-breakpad",
                "--disable-client-side-phishing-detection",
                "--disable-component-update",
                "--disable-default-apps",
                "--disable-domain-reliability",
                "--disable-extensions",
                "--disable-gpu",
                "--disable-preconnect",
                "--disable-sync",
                "--disable-translate",
                "--disable-webrtc",
                "--disable-dev-shm-usage",
                "--dns-prefetch-disable",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--metrics-recording-only",
                "--mute-audio",
                "--no-default-browser-check",
                "--no-first-run",
                "--safebrowsing-disable-auto-update"
            ]
        };
    }

    private async Task FulfillSafeRequestAsync(
        IRequest request,
        BrowserRequestBudget budget,
        SemaphoreSlim requestConcurrency,
        CancellationToken cancellationToken)
    {
        var enteredConcurrency = false;
        try
        {
            await requestConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredConcurrency = true;
            var remainingBytes = Math.Min(PublicWebFetcher.DefaultMaximumBodyBytes, budget.RemainingUtf8Bytes);
            if (remainingBytes <= 0)
            {
                await AbortBestEffortAsync(request);
                return;
            }

            var fetched = await _safeFetcher.FetchAsync(request.Url, remainingBytes, cancellationToken).ConfigureAwait(false);
            if (!budget.TryConsume(fetched.Content))
            {
                await AbortBestEffortAsync(request);
                return;
            }

            await request.RespondAsync(new ResponseData
                {
                    Status = HttpStatusCode.OK,
                    ContentType = fetched.MediaType,
                    Body = fetched.Content,
                    Headers = new Dictionary<string, object>
                    {
                        ["Access-Control-Allow-Origin"] = "*",
                        ["Cache-Control"] = "no-store"
                    }
                })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await AbortBestEffortAsync(request);
        }
        finally
        {
            if (enteredConcurrency)
            {
                requestConcurrency.Release();
            }
        }
    }

    private static async Task AbortBestEffortAsync(IRequest request)
    {
        try
        {
            await request.AbortAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The page may have closed while a request policy check was in flight.
        }
    }

    private static void CancelBestEffort(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A detached request event can race isolated-context teardown.
        }
    }

    private sealed class BrowserRequestTracker
    {
        private readonly object _sync = new();
        private readonly int _maximumActive;
        private TaskCompletionSource<bool>? _drained;
        private int _active;
        private bool _accepting = true;

        internal BrowserRequestTracker(int maximumActive)
        {
            if (maximumActive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumActive));
            }

            _maximumActive = maximumActive;
        }

        internal bool TryRun(Func<Task> operation)
        {
            lock (_sync)
            {
                if (!_accepting || _active >= _maximumActive)
                {
                    return false;
                }

                if (_active++ == 0)
                {
                    _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            _ = RunAsync(operation);
            return true;
        }

        internal Task StopAndDrainAsync()
        {
            lock (_sync)
            {
                _accepting = false;
                return _active == 0 ? Task.CompletedTask : _drained!.Task;
            }
        }

        private async Task RunAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch
            {
                // Individual request failures are isolated from the page lifecycle.
            }
            finally
            {
                TaskCompletionSource<bool>? drained = null;
                lock (_sync)
                {
                    if (--_active == 0)
                    {
                        drained = _drained;
                    }
                }

                drained?.TrySetResult(true);
            }
        }
    }
}

public sealed partial class LocalInternetToolProvider : IInternetToolProvider, IDisposable
{
    private const int MaximumEnrichedSearchSources = 3;
    private const int MaximumSearchCandidatePool = 40;
    private const int MinimumSearchCandidatePool = 20;
    private static readonly TimeSpan SearchEnrichmentTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SearchResultValidationTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient? _ownedSearchHttpClient;
    private readonly PublicWebFetcher _publicWebFetcher;
    private readonly ISearxngSearchClient _searchClient;
    private readonly IReadablePageExtractor _pageExtractor;
    private readonly IBrowserPageRenderer _browserRenderer;
    private readonly Func<Uri, CancellationToken, Task<bool>> _searchResultDestinationValidator;
    private readonly Func<CancellationToken, Task> _ensureSearchBackendAsync;
    private readonly bool _enrichSearchResults;

    public LocalInternetToolProvider(
        ISearxngSearchClient? searchClient = null,
        IReadablePageExtractor? pageExtractor = null,
        IBrowserPageRenderer? browserRenderer = null,
        bool? enrichSearchResults = null,
        Func<CancellationToken, Task>? ensureSearchBackendAsync = null)
    {
        _publicWebFetcher = new PublicWebFetcher();
        if (searchClient is null)
        {
            _ownedSearchHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            _searchClient = new SearxngSearchClient(_ownedSearchHttpClient);
        }
        else
        {
            _searchClient = searchClient;
        }

        _pageExtractor = pageExtractor ?? new SmartReaderPageExtractor();
        _browserRenderer = browserRenderer ?? new PuppeteerSharpPageRenderer();
        _searchResultDestinationValidator = ValidateSearchResultDestinationAsync;
        _ensureSearchBackendAsync = ensureSearchBackendAsync ?? (_ => Task.CompletedTask);
        _enrichSearchResults = enrichSearchResults ?? searchClient is null;
    }

    internal LocalInternetToolProvider(
        PublicWebFetcher publicWebFetcher,
        ISearxngSearchClient? searchClient = null,
        IReadablePageExtractor? pageExtractor = null,
        IBrowserPageRenderer? browserRenderer = null,
        Func<Uri, CancellationToken, Task<bool>>? searchResultDestinationValidator = null,
        bool? enrichSearchResults = null)
    {
        _publicWebFetcher = publicWebFetcher ?? throw new ArgumentNullException(nameof(publicWebFetcher));
        if (searchClient is null)
        {
            _ownedSearchHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            _searchClient = new SearxngSearchClient(_ownedSearchHttpClient);
        }
        else
        {
            _searchClient = searchClient;
        }

        _pageExtractor = pageExtractor ?? new SmartReaderPageExtractor();
        _browserRenderer = browserRenderer ?? new PuppeteerSharpPageRenderer();
        _searchResultDestinationValidator = searchResultDestinationValidator ?? ValidateSearchResultDestinationAsync;
        _ensureSearchBackendAsync = _ => Task.CompletedTask;
        _enrichSearchResults = enrichSearchResults ?? searchClient is null;
    }

    public async Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, InternetSettings settings, CancellationToken cancellationToken = default)
    {
        return request.Tool switch
        {
            InternetToolNames.WebSearch => await SearchWebAsync(request, cancellationToken),
            InternetToolNames.FetchUrl => await FetchUrlAsync(request, cancellationToken),
            _ => new InternetToolResult { Ok = false, Tool = request.Tool, Query = request.Query, Url = request.Url, Error = $"Unsupported internet tool '{request.Tool}'." }
        };
    }

    private async Task<InternetToolResult> SearchWebAsync(InternetToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _ensureSearchBackendAsync(cancellationToken);
            var maxResults = Math.Clamp(request.MaxResults, 1, 10);
            var candidateLimit = Math.Min(
                MaximumSearchCandidatePool,
                Math.Max(MinimumSearchCandidatePool, maxResults * 4));
            var parameters = new SearxngSearchParameters(request.Language, request.TimeRange, request.Categories);
            var json = await _searchClient.SearchJsonAsync(request.Query, candidateLimit, parameters, cancellationToken);
            var sources = await FilterPublicSearchSourcesAsync(
                ParseSearxngResults(json, candidateLimit),
                cancellationToken);
            sources = sources.Take(maxResults).ToArray();
            if (_enrichSearchResults && sources.Count > 0)
            {
                sources = await EnrichSearchSourcesAsync(sources, cancellationToken);
            }

            var quality = SearchQuality(sources, maxResults);

            return new InternetToolResult
            {
                Ok = sources.Count > 0,
                Tool = request.Tool,
                Query = request.Query,
                Summary = sources.Count == 0
                    ? $"No local web search results found for: {request.Query}"
                    : $"Found {sources.Count} local web result(s) for: {request.Query} ({quality} quality)",
                Sources = sources,
                Error = sources.Count == 0 ? "No local web search results found." : "",
                Quality = quality,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InternetToolResult
            {
                Ok = false,
                Tool = request.Tool,
                Query = request.Query,
                Error = $"Local search unavailable: {ex.Message}",
                Quality = "none"
            };
        }
    }

    private async Task<IReadOnlyList<InternetToolSource>> FilterPublicSearchSourcesAsync(
        IReadOnlyList<InternetToolSource> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return sources;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchResultValidationTimeout);
        var checks = sources.Select(async source =>
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            {
                return (Source: source, IsPublic: false);
            }

            try
            {
                return (Source: source, IsPublic: await _searchResultDestinationValidator(uri, timeout.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (Source: source, IsPublic: false);
            }
        }).ToArray();

        var checkedSources = await Task.WhenAll(checks);
        return checkedSources
            .Where(item => item.IsPublic)
            .Select(item => item.Source)
            .ToArray();
    }

    private static async Task<bool> ValidateSearchResultDestinationAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        await PublicWebDestinationValidator.ValidateAndResolveAsync(uri, cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<InternetToolSource>> EnrichSearchSourcesAsync(
        IReadOnlyList<InternetToolSource> sources,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchEnrichmentTimeout);
        var tasks = sources
            .Select((source, index) => index < MaximumEnrichedSearchSources
                ? EnrichSearchSourceAsync(source, cancellationToken, timeout.Token)
                : Task.FromResult(source))
            .ToArray();
        try
        {
            return await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return sources;
        }
    }

    private async Task<InternetToolSource> EnrichSearchSourceAsync(
        InternetToolSource source,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        try
        {
            var (finalUrl, html) = await FetchRawPublicPageAsync(source.Url, timeoutCancellationToken);
            var page = _pageExtractor.Extract(finalUrl, html);
            if (!IsUsablePage(page))
            {
                return source;
            }

            var snippet = TrimSnippet(page.Snippet, 1200);
            return new InternetToolSource
            {
                Title = string.IsNullOrWhiteSpace(page.Title) ? source.Title : page.Title,
                Url = string.IsNullOrWhiteSpace(page.Url) ? finalUrl : page.Url,
                Source = source.Source,
                PublishedAt = page.PublishedAt ?? source.PublishedAt,
                Snippet = snippet.Length >= source.Snippet.Length ? snippet : source.Snippet,
                Score = source.Score + 12
            };
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return source;
        }
    }

    private async Task<(string FinalUrl, string Html)> FetchRawPublicPageAsync(string url, CancellationToken cancellationToken)
    {
        var fetched = await _publicWebFetcher.FetchAsync(url, cancellationToken);
        return (fetched.FinalUri.AbsoluteUri, fetched.Content);
    }

    private async Task<InternetToolResult> FetchUrlAsync(InternetToolRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (page, _) = await FetchReadablePageAsync(request.Url, cancellationToken);
            var snippet = TrimSnippet(page.Snippet, 2600);
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return new InternetToolResult
                {
                    Ok = false,
                    Tool = request.Tool,
                    Url = request.Url,
                    Error = "No readable text found at URL."
                };
            }

            var summary = string.IsNullOrWhiteSpace(page.Title) ? snippet : $"{page.Title}{Environment.NewLine}{snippet}";
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
                        Score = 1
                    }
                ]
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InternetToolResult { Ok = false, Tool = request.Tool, Url = request.Url, Error = ex.Message };
        }
    }

    private async Task<(FetchedPage Page, string Html)> FetchReadablePageAsync(string url, CancellationToken cancellationToken)
    {
        var (finalUrl, html) = await FetchRawPublicPageAsync(url, cancellationToken);

        var page = _pageExtractor.Extract(finalUrl, html);
        if (!NeedsBrowserFallback(html, page))
        {
            return (page, html);
        }

        try
        {
            var renderedHtml = await _browserRenderer.RenderHtmlAsync(finalUrl, cancellationToken);
            var renderedPage = _pageExtractor.Extract(finalUrl, renderedHtml);
            return IsUsablePage(renderedPage) ? (renderedPage, renderedHtml) : (page, html);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch when (IsUsablePage(page))
        {
            // Browser rendering is an enhancement. A readable response fetched through the
            // hardened public-web path remains useful when Chromium is absent or fails.
            return (page, html);
        }
    }

    private static bool NeedsBrowserFallback(string html, FetchedPage page)
    {
        if (page.Snippet.Length >= 180)
        {
            return false;
        }

        var lower = html.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(page.Snippet)
            || lower.Contains("enable javascript", StringComparison.Ordinal)
            || lower.Contains("requires javascript", StringComparison.Ordinal)
            || lower.Contains("checking your browser", StringComparison.Ordinal)
            || lower.Contains("cf-browser-verification", StringComparison.Ordinal)
            || (html.Count(ch => ch == '<') > 20 && lower.Contains("<script", StringComparison.Ordinal));
    }

    private static bool IsUsablePage(FetchedPage page)
    {
        return !string.IsNullOrWhiteSpace(page.Snippet) && page.Snippet.Length >= 80;
    }

    private static string TrimSnippet(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength].Trim();
    }

    internal static IReadOnlyList<InternetToolSource> ParseSearxngResults(string json, int maxResults)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var boundedMaxResults = Math.Clamp(maxResults, 1, MaximumSearchCandidatePool);
        var candidates = new List<(InternetToolSource Source, double Rank)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var index = 0;
        foreach (var item in results.EnumerateArray())
        {
            index++;
            if (index > 100)
            {
                break;
            }

            var url = JsonString(item, "url");
            if (string.IsNullOrWhiteSpace(url)
                || url.Length > 4096
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !IsSafeSearchResultUri(uri))
            {
                continue;
            }

            uri = NormalizeSearchResultUri(uri);
            if (InternetRequestSafety.ContainsSensitivePayload(uri.AbsoluteUri))
            {
                continue;
            }

            if (!seenUrls.Add(CanonicalUrlKey(uri)))
            {
                continue;
            }

            var title = JsonString(item, "title");
            var content = JsonString(item, "content");
            var normalizedTitle = NormalizeTitle(title);
            var titleKey = $"{uri.Host}|{normalizedTitle}";
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && !seenTitles.Add(titleKey))
            {
                continue;
            }

            var snippet = TrimSnippet(CleanText(content), 900);
            var domain = DomainLabel(uri.AbsoluteUri);
            var publishedAt = ParsePublishedAt(item);
            var engineCount = JsonArrayCount(item, "engines");
            var rank = Math.Max(0, 220 - (index * 2))
                - (IsSearchAggregator(uri) ? 80 : 0)
                + (uri.Scheme == Uri.UriSchemeHttps ? 6 : 0)
                + (snippet.Length >= 140 ? 10 : snippet.Length >= 70 ? 4 : 0)
                + (!string.IsNullOrWhiteSpace(normalizedTitle) ? 3 : 0)
                + Math.Min(18, Math.Max(0, engineCount - 1) * 6)
                + (publishedAt is not null
                    && publishedAt <= now
                    && now - publishedAt <= TimeSpan.FromDays(31)
                        ? 8
                        : 0);
            candidates.Add((new InternetToolSource
            {
                Title = string.IsNullOrWhiteSpace(title) ? domain : WebUtility.HtmlDecode(CleanText(title)),
                Url = uri.AbsoluteUri,
                Source = FirstNonEmpty(domain, JsonString(item, "engine")),
                PublishedAt = publishedAt,
                Snippet = snippet,
                Score = rank
            }, rank));
        }

        var ranked = candidates
            .OrderByDescending(item => item.Rank)
            .ThenBy(item => item.Source.Url.Length)
            .ToArray();
        var selected = new List<(InternetToolSource Source, double Rank)>(boundedMaxResults);
        var selectedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ranked
            .GroupBy(item => DomainLabel(item.Source.Url), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            if (selected.Count >= boundedMaxResults)
            {
                break;
            }

            selected.Add(candidate);
            selectedUrls.Add(candidate.Source.Url);
        }

        foreach (var candidate in ranked)
        {
            if (selected.Count >= boundedMaxResults)
            {
                break;
            }

            if (selectedUrls.Add(candidate.Source.Url))
            {
                selected.Add(candidate);
            }
        }

        return selected
            .Select(item => new InternetToolSource
            {
                Title = item.Source.Title,
                Url = item.Source.Url,
                Source = item.Source.Source,
                PublishedAt = item.Source.PublishedAt,
                Snippet = item.Source.Snippet,
                Score = item.Rank
            })
            .ToArray();
    }

    internal static string SearchQuality(IReadOnlyList<InternetToolSource> sources, int maxResults)
    {
        if (sources.Count == 0)
        {
            return "none";
        }

        var distinctDomains = sources
            .Select(source => DomainLabel(source.Url))
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var directSources = sources.Count(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && !IsSearchAggregator(uri));
        var usefulSnippets = sources.Count(source => source.Snippet.Length >= 80);
        return distinctDomains >= Math.Min(3, Math.Max(1, maxResults))
            && directSources >= Math.Min(2, Math.Max(1, sources.Count))
            && usefulSnippets >= Math.Min(2, sources.Count)
            ? "strong"
            : "weak";
    }

    private static string CanonicalUrlKey(Uri uri)
    {
        return $"{uri.IdnHost}{uri.AbsolutePath}{uri.Query}".TrimEnd('/').ToLowerInvariant();
    }

    private static Uri NormalizeSearchResultUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = "" };
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            var queryParts = builder.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part =>
                {
                    var name = part.Split('=', 2)[0];
                    return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("fbclid", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("gclid", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("mc_cid", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("mc_eid", StringComparison.OrdinalIgnoreCase);
                });
            builder.Query = string.Join('&', queryParts);
        }

        return builder.Uri;
    }

    private static DateTimeOffset? ParsePublishedAt(JsonElement item)
    {
        foreach (var name in new[] { "publishedDate", "published_date", "pubdate" })
        {
            var value = JsonString(item, name);
            if (DateTimeOffset.TryParse(value, out var publishedAt))
            {
                return publishedAt;
            }
        }

        return null;
    }

    private static int JsonArrayCount(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;
    }

    private static bool IsSafeSearchResultUri(Uri uri)
    {
        try
        {
            PublicWebDestinationValidator.ValidateUri(uri);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static string NormalizeTitle(string title)
    {
        return string.Join(
            " ",
            CleanText(WebUtility.HtmlDecode(title ?? ""))
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsSearchAggregator(Uri uri)
    {
        var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return host is "duckduckgo.com" or "google.com" or "bing.com" or "yahoo.com" or "search.brave.com" or "searx.space"
            || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)
            || host.Contains("searx", StringComparison.OrdinalIgnoreCase);
    }

    private static string JsonString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string DomainLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "web";
    }

    private static string CleanText(string text)
    {
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(HtmlTagRegex().Replace(text, " "), " ").Trim());
    }

    public void Dispose()
    {
        _publicWebFetcher?.Dispose();
        _ownedSearchHttpClient?.Dispose();
        _browserRenderer.Dispose();
    }

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

}
