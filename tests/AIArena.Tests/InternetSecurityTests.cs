using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Services;
using PuppeteerSharp;

internal static class InternetSecurityTests
{
    internal static void LiveInstalledBrowserUsesHardenedRenderer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var renderer = new PuppeteerSharpPageRenderer();
        var html = renderer.RenderHtmlAsync("https://example.com/", timeout.Token).GetAwaiter().GetResult();

        Require(html.Contains("Example Domain", StringComparison.OrdinalIgnoreCase), "the installed browser did not render the public test page through the hardened request path");
        Console.WriteLine($"LIVE BROWSER rendered {html.Length} characters via {BrowserExecutableResolver.Resolve().ExecutablePath}");
    }

    internal static void SearchOptionsReachSearxng()
    {
        Uri? requested = null;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return JsonResponse("""{"results":[]}""");
        }));
        var client = new SearxngSearchClient(httpClient, new Uri("http://localhost:8081"));

        client.SearchJsonAsync(
                "AI regulation",
                5,
                new SearxngSearchParameters("en-GB", "month", "general,science"))
            .GetAwaiter()
            .GetResult();

        Require(requested is not null, "SearXNG request was not issued");
        var query = requested!.Query;
        Require(query.Contains("language=en-GB", StringComparison.Ordinal), "language was not forwarded");
        Require(query.Contains("time_range=month", StringComparison.Ordinal), "time range was not forwarded");
        Require(query.Contains("categories=general%2Cscience", StringComparison.OrdinalIgnoreCase), "categories were not forwarded");
        Require(query.Contains("format=json", StringComparison.Ordinal), "JSON format was not requested");
    }

    internal static void SearchOptionsAreValidated()
    {
        var valid = InternetToolContract.TryValidate(
            new InternetToolRequest
            {
                Tool = InternetToolNames.WebSearch,
                Query = "AI regulation",
                Language = "EN-gb",
                TimeRange = "MONTH",
                Categories = "General, Science"
            },
            out var request,
            out var error);
        Require(valid, $"valid search options were rejected: {error}");
        Require(request.Language == "en-GB", "language should normalize to a stable code");
        Require(request.TimeRange == "month", "time range should normalize");
        Require(request.Categories == "general,science", "categories should normalize");

        Require(
            !InternetToolContract.TryValidate(
                new InternetToolRequest { Tool = InternetToolNames.WebSearch, Query = "AI", TimeRange = "forever" },
                out _,
                out _),
            "unknown time ranges should be rejected");
        Require(
            !InternetToolContract.TryValidate(
                new InternetToolRequest { Tool = InternetToolNames.WebSearch, Query = "AI", Categories = "general,science,files,maps" },
                out _,
                out _),
            "too many categories should be rejected");
    }

    internal static void SearchRankingDiversifiesDomains()
    {
        var json = """
        {
          "results": [
            {"url":"https://one.example/a?utm_source=test","title":"First","content":"Short result one with enough useful context to cite.","engines":["brave","bing"],"publishedDate":"2026-07-10T10:00:00Z"},
            {"url":"https://one.example/b","title":"Second","content":"Another result from the same domain with useful details.","engines":["brave"]},
            {"url":"https://two.example/c#section","title":"Third","content":"Independent corroboration from a different domain with useful details.","engines":["brave","bing"]}
          ]
        }
        """;

        var sources = LocalInternetToolProvider.ParseSearxngResults(json, 2);

        Require(sources.Count == 2, "result count should remain bounded");
        Require(sources.Select(source => new Uri(source.Url).Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "top results should be domain-diverse");
        Require(!sources[0].Url.Contains("utm_", StringComparison.OrdinalIgnoreCase), "tracking parameters should be removed");
        Require(sources.All(source => !source.Url.Contains('#')), "fragments should be removed");
        Require(sources.Any(source => source.PublishedAt?.Year == 2026), "published date should be retained");
    }

    internal static void SearchEnrichmentRunsInParallel()
    {
        var searchJson = """
        {
          "results": [
            {"url":"https://one.example/a","title":"One","content":"thin"},
            {"url":"https://two.example/b","title":"Two","content":"thin"},
            {"url":"https://three.example/c","title":"Three","content":"thin"}
          ]
        }
        """;
        var pageHandler = new ConcurrentPageHandler();
        using var fetcher = new PublicWebFetcher(pageHandler, (_, _) => Task.CompletedTask);
        using var provider = new LocalInternetToolProvider(
            fetcher,
            searchClient: new FixedSearchClient(searchJson),
            pageExtractor: new SmartReaderPageExtractor(),
            browserRenderer: new NoopBrowserRenderer(),
            searchResultDestinationValidator: (_, _) => Task.FromResult(true),
            enrichSearchResults: true);

        var result = provider.ExecuteAsync(
                new InternetToolRequest
                {
                    Tool = InternetToolNames.WebSearch,
                    Query = "parallel enrichment",
                    MaxResults = 3
                },
                new InternetSettings { UseInternet = true, MaxResults = 3 })
            .GetAwaiter()
            .GetResult();

        Require(result.Ok, $"enriched search failed: {result.Error}");
        Require(pageHandler.MaximumConcurrency >= 2, "top result pages should be fetched concurrently");
        Require(result.Sources.All(source => source.Snippet.Contains("Readable enriched source", StringComparison.Ordinal)), "enriched readable text should replace thin snippets");
    }

    internal static void MixedDnsAnswersAreRejected()
    {
        var rejected = false;
        try
        {
            PublicWebDestinationValidator.ValidateResolvedAddresses(
                "mixed.example",
                [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]);
        }
        catch (HttpRequestException)
        {
            rejected = true;
        }

        Require(rejected, "mixed public/private DNS answers must be rejected");
        var publicOnly = PublicWebDestinationValidator.ValidateResolvedAddresses(
            "public.example",
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]);
        Require(publicOnly.Length == 2, "public DNS answers should remain usable");
    }

    internal static void RedirectsToPrivateNetworksAreRejected()
    {
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data/") }
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.CompletedTask);

        var rejected = false;
        try
        {
            fetcher.FetchAsync("https://public.example/").GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            rejected = true;
        }

        Require(rejected, "redirects to metadata/private destinations must be rejected");
        Require(handler.RequestCount == 1, "private redirect must be rejected before a second request");
    }

    internal static void RedirectCountIsBounded()
    {
        var handler = new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(request.RequestUri!, "/next") }
        });
        using var fetcher = new PublicWebFetcher(
            handler,
            (_, _) => Task.CompletedTask,
            maximumRedirects: 1);

        var rejected = false;
        try
        {
            fetcher.FetchAsync("https://public.example/start").GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            rejected = true;
        }

        Require(rejected, "redirect chains above the configured limit must fail");
        Require(handler.RequestCount == 2, "fetcher should stop at the redirect limit");
    }

    internal static void ChunkedBodiesAreBoundedAndCancelable()
    {
        using var oversized = new UnknownLengthContent(new byte[65]);
        oversized.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var rejected = false;
        try
        {
            BoundedTextContentReader.ReadAsync(oversized, 64).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            rejected = true;
        }

        Require(rejected, "unknown-length bodies must still obey the byte ceiling");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var blocking = new StreamContent(new BlockingReadStream());
        var canceled = false;
        try
        {
            BoundedTextContentReader.ReadAsync(blocking, 64, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Require(canceled, "body reads should propagate cancellation");
    }

    internal static void CompressedBodiesUseDecompressedByteCeiling()
    {
        var expanded = Encoding.UTF8.GetBytes(new string('x', 257));
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ExpandingGzipContent(expanded)
        });
        using var fetcher = new PublicWebFetcher(
            handler,
            (_, _) => Task.CompletedTask,
            maximumBodyBytes: 256);

        var rejected = false;
        try
        {
            fetcher.FetchAsync("https://public.example/compressed").GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
            rejected = true;
        }

        Require(rejected, "the limit must apply after content decompression");
    }

    internal static void UnsupportedMediaIsRejectedBeforeBodyRead()
    {
        var content = new ReadTrackingContent("application/pdf");
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.CompletedTask);

        var rejected = false;
        try
        {
            fetcher.FetchAsync("https://public.example/report.pdf").GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            rejected = ex.Message.Contains("media type", StringComparison.OrdinalIgnoreCase);
        }

        Require(rejected, "application/pdf should be rejected explicitly as unsupported media");
        Require(!content.WasRead, "unsupported response bodies must be rejected before streaming their bytes");
    }

    internal static void BrowserExecutableDiscoveryIsDeterministic()
    {
        const string configured = @"C:\Browsers\Configured\chrome.exe";
        const string edge = @"C:\Browsers\Edge\msedge.exe";
        const string chrome = @"C:\Browsers\Chrome\chrome.exe";
        var existing = new HashSet<string>([configured, edge, chrome], StringComparer.OrdinalIgnoreCase);

        var explicitResolution = BrowserExecutableResolver.Resolve(configured, [edge, chrome], existing.Contains);
        Require(explicitResolution.Ok && explicitResolution.ExecutablePath == configured, "AIARENA_CHROME_PATH should have deterministic priority");

        var installedResolution = BrowserExecutableResolver.Resolve(null, [edge, chrome], path => path == chrome);
        Require(installedResolution.Ok && installedResolution.ExecutablePath == chrome, "the first existing installed candidate should be selected");

        var invalidExplicit = BrowserExecutableResolver.Resolve(@"C:\missing.exe", [edge], path => path == edge);
        Require(!invalidExplicit.Ok && invalidExplicit.Error.Contains("AIARENA_CHROME_PATH", StringComparison.Ordinal), "an invalid explicit path should fail clearly instead of silently selecting another browser");

        var absent = BrowserExecutableResolver.Resolve(null, [edge, chrome], _ => false);
        Require(!absent.Ok && absent.Error.Contains("Install Microsoft Edge or Google Chrome", StringComparison.Ordinal), "a missing installed browser should return a repair action without downloading one");
    }

    internal static void BrowserResourcePolicyAndBudgetsAreBounded()
    {
        foreach (var allowed in new[]
        {
            ResourceType.Document,
            ResourceType.Script,
            ResourceType.StyleSheet,
            ResourceType.Xhr,
            ResourceType.Fetch
        })
        {
            Require(BrowserResourcePolicy.IsAllowedResourceType(allowed), $"textual resource type should be allowed: {allowed}");
        }

        foreach (var denied in new[]
        {
            ResourceType.WebSocket,
            ResourceType.EventSource,
            ResourceType.Beacon,
            ResourceType.Ping,
            ResourceType.Image,
            ResourceType.Media,
            ResourceType.Font,
            ResourceType.Other
        })
        {
            Require(!BrowserResourcePolicy.IsAllowedResourceType(denied), $"background or binary resource type should be denied: {denied}");
        }

        Require(BrowserResourcePolicy.IsAllowed("GET", "https://public.example/app.js", ResourceType.Script), "textual public GET should pass browser resource policy");
        Require(!BrowserResourcePolicy.IsAllowed("POST", "https://public.example/api", ResourceType.Fetch), "browser resource policy must reject non-GET requests");
        Require(!BrowserResourcePolicy.IsAllowed("GET", "not-a-url", ResourceType.Document), "browser resource policy must reject malformed URLs");

        var budget = new BrowserRequestBudget(maximumRequests: 2, maximumUtf8Bytes: 8);
        Require(budget.TryStartRequest(), "first browser request should fit the count budget");
        Require(budget.TryStartRequest(), "second browser request should fit the count budget");
        Require(!budget.TryStartRequest(), "requests above the count budget should fail closed");
        Require(budget.TryConsume("1234"), "first response should fit the byte budget");
        Require(budget.RemainingUtf8Bytes == 4, "remaining UTF-8 budget mismatch");
        Require(!budget.TryConsume("12345"), "responses above the aggregate byte budget should fail closed");
        Require(budget.TryConsume("5678") && budget.RemainingUtf8Bytes == 0, "the aggregate byte budget should be exactly consumable");
    }

    internal static void BrowserLifecycleDrainsActiveRenders()
    {
        using var lifecycle = new BrowserRenderLifecycle();
        var lease = lifecycle.Enter(CancellationToken.None, TimeSpan.FromMinutes(1));
        var shutdown = Task.Run(lifecycle.StopAndDrain);

        Require(SpinWait.SpinUntil(() => lifecycle.IsStopping, TimeSpan.FromSeconds(2)), "browser lifecycle did not enter stopping state");
        Require(lease.Token.IsCancellationRequested, "shutdown should cancel active render work");
        Require(!shutdown.IsCompleted, "shutdown must wait for the active render lease to drain");
        var rejected = false;
        try
        {
            lifecycle.Enter(CancellationToken.None, TimeSpan.FromSeconds(1));
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        Require(rejected, "new renders must be rejected after shutdown begins");
        lease.Dispose();
        Require(shutdown.Wait(TimeSpan.FromSeconds(2)), "shutdown should complete after the active render drains");
    }

    internal static void SearchFiltersHostnamesResolvingNonPublic()
    {
        const string json = """
        {
          "results": [
            {"url":"https://private-dns.example/internal","title":"Private","content":"Must never surface."},
            {"url":"https://public-dns.example/article","title":"Public","content":"A normal public source with useful evidence."}
          ]
        }
        """;
        using var fetcher = new PublicWebFetcher(new DelegateHandler(_ => throw new InvalidOperationException("page fetch was not expected")), (_, _) => Task.CompletedTask);
        using var provider = new LocalInternetToolProvider(
            fetcher,
            searchClient: new FixedSearchClient(json),
            browserRenderer: new NoopBrowserRenderer(),
            searchResultDestinationValidator: (uri, _) => Task.FromResult(uri.Host == "public-dns.example"),
            enrichSearchResults: false);

        var result = provider.ExecuteAsync(
                new InternetToolRequest { Tool = InternetToolNames.WebSearch, Query = "destination policy", MaxResults = 2 },
                new InternetSettings { UseInternet = true, MaxResults = 2 })
            .GetAwaiter()
            .GetResult();

        Require(result.Ok, $"public search source should remain usable: {result.Error}");
        Require(result.Sources.Count == 1, "non-public DNS destination should be removed before sources are surfaced");
        Require(new Uri(result.Sources[0].Url).Host == "public-dns.example", "wrong source survived DNS validation");
    }

    internal static void ConcurrentIdenticalRequestsUseSingleFlight()
    {
        var snapshot = new ArenaSnapshot();
        snapshot.Engine.Internet.UseInternet = true;
        snapshot.Engine.Internet.MaxResults = 2;
        var provider = new BlockingInternetProvider();
        using var service = new InternetToolService(provider);
        var request = new InternetToolRequest
        {
            Tool = InternetToolNames.WebSearch,
            RequesterId = "single-flight",
            Query = "concurrent internet single flight",
            MaxResults = 2
        };

        var first = service.ExecuteAsync(snapshot, request, "same-session");
        Require(provider.Started.Wait(TimeSpan.FromSeconds(2)), "first provider request did not start");
        var second = service.ExecuteAsync(snapshot, request, "same-session");
        Thread.Sleep(50);
        Require(provider.Calls == 1, "concurrent identical requests should share one provider execution");
        provider.Release.Set();
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        Require(provider.Calls == 1, "single-flight provider execution count changed after completion");
    }

    internal static void InternetServiceDrainsProviderBeforeDisposal()
    {
        var snapshot = new ArenaSnapshot();
        snapshot.Engine.Internet.UseInternet = true;
        var provider = new ShutdownProbeInternetProvider();
        var service = new InternetToolService(provider);
        var operation = service.ExecuteAsync(
            snapshot,
            new InternetToolRequest
            {
                Tool = InternetToolNames.WebSearch,
                RequesterId = "shutdown-lifetime",
                Query = "internet provider shutdown lifetime"
            });
        Require(provider.Started.Task.Wait(TimeSpan.FromSeconds(2)), "shutdown-lifetime provider did not start");

        service.Dispose();
        Require(provider.Canceled.Task.Wait(TimeSpan.FromSeconds(2)), "service disposal did not cancel the provider operation");
        Require(provider.DisposeCount == 0, "service disposed its provider before the active operation unwound");

        provider.Release.TrySetResult(true);
        try
        {
            _ = operation.GetAwaiter().GetResult();
            throw new InvalidOperationException("the shutdown-lifetime operation should observe cancellation");
        }
        catch (OperationCanceledException)
        {
        }

        Require(provider.Unwound.Task.Wait(TimeSpan.FromSeconds(2)), "provider operation did not unwind after cancellation");
        Require(provider.TokenLifetimeFailure is null, $"shutdown token source was disposed before provider unwind: {provider.TokenLifetimeFailure?.GetType().Name}");
        Require(SpinWait.SpinUntil(() => provider.DisposeCount == 1, TimeSpan.FromSeconds(2)), "provider resources were not disposed after the operation drained");
        service.Dispose();
        Require(provider.DisposeCount == 1, "repeated service disposal should not dispose provider resources twice");
    }

    internal static void FuturePublicationDatesReceiveNoRecencyBoost()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var future = DateTimeOffset.UtcNow.AddYears(5).ToString("O");
        var json = $$"""
        {
          "results": [
            {"url":"https://future.example/article","title":"Future","content":"Equal useful context for ranking comparison.","publishedDate":"{{future}}"},
            {"url":"https://recent.example/article","title":"Recent","content":"Equal useful context for ranking comparison.","publishedDate":"{{recent}}"}
          ]
        }
        """;

        var sources = LocalInternetToolProvider.ParseSearxngResults(json, 2);
        Require(sources.Count == 2, "ranking fixture should return both sources");
        Require(sources[0].Title == "Recent", "a future timestamp must not receive the freshness boost");
        Require(sources[0].Score > sources[1].Score, "recent legitimate publication should outrank future-dated metadata");
    }

    internal static void BrowserFallbackFailureKeepsReadableInitialFetch()
    {
        const string initialHtml = "<html><body><p>Requires JavaScript for enhanced navigation.</p></body></html>";
        var handler = new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(initialHtml, Encoding.UTF8, "text/html")
        });
        using var fetcher = new PublicWebFetcher(handler, (_, _) => Task.CompletedTask);
        var browser = new ThrowingBrowserRenderer();
        using var provider = new LocalInternetToolProvider(
            fetcher,
            pageExtractor: new FixedReadablePageExtractor(),
            browserRenderer: browser,
            searchResultDestinationValidator: (_, _) => Task.FromResult(true),
            enrichSearchResults: false);

        var result = provider.ExecuteAsync(
                new InternetToolRequest { Tool = InternetToolNames.FetchUrl, Url = "https://public.example/article" },
                new InternetSettings { UseInternet = true })
            .GetAwaiter()
            .GetResult();

        Require(browser.Calls == 1, "a thin JavaScript-marked response should attempt browser rendering once");
        Require(result.Ok, $"readable initial content should survive a browser failure: {result.Error}");
        Require(result.Sources.Single().Title == "Initial public article", "the initial fetched page should be returned");
        Require(result.Sources.Single().Snippet == FixedReadablePageExtractor.Snippet, "browser failure must not discard initial readable text");
    }

    internal static void SearchCandidatePoolSurvivesFilteredTopTen()
    {
        var json = JsonSerializer.Serialize(new
        {
            results = Enumerable.Range(1, 11).Select(index => new
            {
                url = $"https://source{index}.example/article",
                title = $"Source {index}",
                content = $"Evidence from source {index} with enough context to identify the surviving result."
            })
        });
        var searchClient = new RecordingSearchClient(json);
        using var fetcher = new PublicWebFetcher(
            new DelegateHandler(_ => throw new InvalidOperationException("page enrichment was not expected")),
            (_, _) => Task.CompletedTask);
        using var provider = new LocalInternetToolProvider(
            fetcher,
            searchClient: searchClient,
            browserRenderer: new NoopBrowserRenderer(),
            searchResultDestinationValidator: (uri, _) => Task.FromResult(uri.Host == "source11.example"),
            enrichSearchResults: false);

        var result = provider.ExecuteAsync(
                new InternetToolRequest { Tool = InternetToolNames.WebSearch, Query = "candidate pool", MaxResults = 10 },
                new InternetSettings { UseInternet = true, MaxResults = 10 })
            .GetAwaiter()
            .GetResult();

        Require(searchClient.RequestedMaxResults > 10, "the backend should be asked for more candidates than the visible result limit");
        Require(searchClient.RequestedMaxResults <= 40, "the candidate pool must remain bounded");
        Require(result.Ok, $"the valid eleventh candidate should survive filtering: {result.Error}");
        Require(result.Sources.Count == 1 && new Uri(result.Sources[0].Url).Host == "source11.example", "DNS filtering must happen before the visible Take(maxResults)");
    }

    internal static void ExplicitUrlsPreserveBalancedClosingParentheses()
    {
        Require(
            TurnRunnerService.TryExtractExplicitPublicUrl(
                "Review (https://en.wikipedia.org/wiki/Foo_(bar)).",
                out var balanced),
            "a public URL with balanced parentheses should be extracted");
        Require(balanced == "https://en.wikipedia.org/wiki/Foo_(bar)", "a balanced closing parenthesis is part of the URL and must be preserved");

        Require(
            TurnRunnerService.TryExtractExplicitPublicUrl("Review https://example.com/report).", out var wrapped),
            "a wrapped public URL should still be extracted");
        Require(wrapped == "https://example.com/report", "unmatched wrapper punctuation should be removed");
    }

    internal static void PublicHashUrlsAreNotCredentials()
    {
        const string revision = "502c820a25bfd9e7a7175671c9d7dc96cf8afbdf";
        const string checksum = "90af1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab";
        var commitUrl = $"https://github.com/searxng/searxng/commit/{revision}";
        var checksumUrl = $"https://downloads.example.org/package.zip?checksum={checksum}";

        Require(!InternetRequestSafety.ContainsSensitivePayload(commitUrl), "a public Git revision URL should not be classified as a credential");
        Require(!InternetRequestSafety.ContainsSensitivePayload($"Review {commitUrl} before replying."), "a public Git revision embedded in an operator prompt should remain usable");
        Require(!InternetRequestSafety.ContainsSensitivePayload(checksumUrl), "an explicitly labeled public checksum URL should remain usable");
        Require(
            InternetRequestSafety.IsSafeOutboundRequest(new InternetToolRequest { Tool = InternetToolNames.FetchUrl, Url = commitUrl }, out var error),
            $"a public Git revision should pass outbound request safety: {error}");

        var tokenUrl = $"https://downloads.example.org/package.zip?token={checksum}";
        Require(InternetRequestSafety.ContainsSensitivePayload(tokenUrl), "the same opaque value in a token-bearing URL must still be blocked");
        Require(
            !InternetRequestSafety.IsSafeOutboundRequest(new InternetToolRequest { Tool = InternetToolNames.FetchUrl, Url = tokenUrl }, out _),
            "credential-bearing URLs must remain blocked");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedSearchClient(string json) : ISearxngSearchClient
    {
        public Task<string> SearchJsonAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(json);
        }
    }

    private sealed class RecordingSearchClient(string json) : ISearxngSearchClient
    {
        internal int RequestedMaxResults { get; private set; }

        public Task<string> SearchJsonAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            RequestedMaxResults = maxResults;
            return Task.FromResult(json);
        }
    }

    private sealed class FixedReadablePageExtractor : IReadablePageExtractor
    {
        internal const string Snippet = "The initial public response already contains a concise readable article with enough evidence to answer the operator safely.";

        public FetchedPage Extract(string url, string html)
        {
            return new FetchedPage(url, "Initial public article", Snippet, null);
        }
    }

    private sealed class ThrowingBrowserRenderer : IBrowserPageRenderer
    {
        internal int Calls { get; private set; }

        public Task<string> RenderHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<string>(new InvalidOperationException("simulated browser startup failure"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConcurrentPageHandler : HttpMessageHandler
    {
        private int active;
        private int maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(current);
            try
            {
                await Task.Delay(75, cancellationToken);
                var host = request.RequestUri?.Host ?? "source";
                var html = $"<html><head><title>{host}</title></head><body><article><p>Readable enriched source text from {host} with enough detail for a grounded citation and a useful Arena answer.</p></article></body></html>";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
                };
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maximumConcurrency);
                if (observed >= value || Interlocked.CompareExchange(ref maximumConcurrency, value, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class NoopBrowserRenderer : IBrowserPageRenderer
    {
        public Task<string> RenderHtmlAsync(string url, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("");
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingInternetProvider : IInternetContextProvider, IDisposable
    {
        private int calls;

        internal ManualResetEventSlim Started { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal int Calls => Volatile.Read(ref calls);

        public async Task<InternetToolResult> ExecuteAsync(
            InternetToolRequest request,
            InternetSettings settings,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Started.Set();
            await Task.Run(() => Release.Wait(cancellationToken), cancellationToken);
            return new InternetToolResult
            {
                Ok = true,
                Tool = request.Tool,
                Query = request.Query,
                Summary = "single-flight result",
                Sources =
                [
                    new InternetToolSource
                    {
                        Title = "Single-flight source",
                        Url = "https://public.example/source",
                        Source = "public.example",
                        Snippet = "A source returned once for concurrent identical requests."
                    }
                ]
            };
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class ShutdownProbeInternetProvider : IInternetContextProvider, IDisposable
    {
        private int disposeCount;

        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Unwound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? TokenLifetimeFailure { get; private set; }
        internal int DisposeCount => Volatile.Read(ref disposeCount);

        public async Task<InternetToolResult> ExecuteAsync(
            InternetToolRequest request,
            InternetSettings settings,
            CancellationToken cancellationToken = default)
        {
            using var cancellationRegistration = cancellationToken.Register(() => Canceled.TrySetResult(true));
            using var throwingRegistration = cancellationToken.Register(static () => throw new InvalidOperationException("simulated cancellation callback failure"));
            Started.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            try
            {
                try
                {
                    using var lateRegistration = cancellationToken.Register(static () => { });
                }
                catch (Exception ex)
                {
                    TokenLifetimeFailure = ex;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new InternetToolResult { Ok = true, Tool = request.Tool, Query = request.Query };
            }
            finally
            {
                Unwound.TrySetResult(true);
            }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
        }
    }

    private sealed class ExpandingGzipContent : HttpContent
    {
        private readonly byte[] compressed;

        internal ExpandingGzipContent(byte[] expanded)
        {
            using var destination = new MemoryStream();
            using (var compressor = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                compressor.Write(expanded);
            }

            compressed = destination.ToArray();
            Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            Headers.ContentEncoding.Add("gzip");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(compressed).AsTask();
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(CreateExpandedStream());
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateExpandedStream());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private Stream CreateExpandedStream()
        {
            return new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        }
    }

    private sealed class ReadTrackingContent : HttpContent
    {
        internal ReadTrackingContent(string mediaType)
        {
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        internal bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 3;
            return true;
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
