using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderConnectionDiscoveryIdentifiesNativeWithoutPortGuesses()
    {
        using var handler = new ConnectionDiscoveryHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/gateway/api/v1/models"
                ? DiscoveryJson("{\"models\":[{\"type\":\"llm\",\"key\":\"local-model\",\"loaded_instances\":[]}]}")
                : DiscoveryJson("{}", HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var result = new ProviderConnectionDiscoveryService(client)
            .DiscoverAsync("https://models.example.test:9876/gateway/api/v1", "discovery-token")
            .GetAwaiter().GetResult();
        Require(result.Available && result.ApiMode == ModelProviderApiModes.LmStudioNative && result.CatalogCount == 1,
            "positive loaded_instances evidence must identify LM Studio at any configured port or proxy path");
        Require(result.BaseUrl == "https://models.example.test:9876/gateway/v1" && handler.Requests.Count == 1,
            "successful LM Studio discovery must preserve its configured origin/path and stop unnecessary probes");
        Require(handler.Requests.All(request => request.Method == "GET" && request.Authorization == "Bearer discovery-token"),
            "discovery must use authenticated read-only metadata without loading, downloading, or generating");

        using var compatibleHandler = new ConnectionDiscoveryHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? DiscoveryJson("{\"data\":[{\"id\":\"compatible-model\",\"owned_by\":\"someone-else\"}]}")
                : DiscoveryJson("{\"status\":\"ok\"}")));
        using var compatibleClient = new HttpClient(compatibleHandler);
        var compatible = new ProviderConnectionDiscoveryService(compatibleClient)
            .DiscoverAsync("http://127.0.0.1:1234").GetAwaiter().GetResult();
        Require(compatible.Available && compatible.ApiMode == ModelProviderApiModes.OpenAiCompatible,
            "a compatible catalog at LM Studio's usual port must stay compatible without native identity evidence");
        Require(compatibleHandler.Requests.All(request => !request.Uri.AbsolutePath.EndsWith("/health", StringComparison.Ordinal)
                && !request.Uri.AbsolutePath.EndsWith("/props", StringComparison.Ordinal)
                && !request.Uri.AbsolutePath.EndsWith("/slots", StringComparison.Ordinal)),
            "discovery must not confuse generic health success with native identity or wake router models through runtime queries");
    }

    static void ProviderConnectionDiscoveryIdentifiesOllamaAndLlamaCatalogs()
    {
        foreach (var empty in new[] { false, true })
        {
            using var handler = new ConnectionDiscoveryHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => DiscoveryJson(empty ? "{\"models\":[]}" : "{\"models\":[{\"name\":\"local:latest\",\"model\":\"local:latest\",\"digest\":\"" + new string('a', 64) + "\",\"details\":{\"format\":\"gguf\",\"family\":\"llama\"}}]}"),
                "/api/ps" => DiscoveryJson("{\"models\":[]}"),
                "/api/version" => DiscoveryJson("{\"version\":\"0.12.1\"}"),
                _ => DiscoveryJson("{}", HttpStatusCode.NotFound)
            }));
            using var client = new HttpClient(handler);
            var result = new ProviderConnectionDiscoveryService(client).DiscoverAsync("http://localhost:4411/api").GetAwaiter().GetResult();
            Require(result.Available && result.ApiMode == ModelProviderApiModes.OllamaNative && result.CatalogCount == (empty ? 0 : 1),
                "Ollama discovery requires native model fingerprints or verified empty inventories plus a version response");
        }
        foreach (var router in new[] { false, true })
        {
            using var handler = new ConnectionDiscoveryHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == (router ? "/models" : "/v1/models")
                    ? DiscoveryJson(router
                        ? "{\"data\":[{\"id\":\"router-model\",\"status\":{\"value\":\"unloaded\"}}]}"
                        : "{\"data\":[{\"id\":\"cpp-model\",\"owned_by\":\"llamacpp\"}]}")
                    : DiscoveryJson("{}", HttpStatusCode.NotFound)));
            using var client = new HttpClient(handler);
            var result = new ProviderConnectionDiscoveryService(client).DiscoverAsync("http://localhost:9911").GetAwaiter().GetResult();
            Require(result.Available && result.ApiMode == ModelProviderApiModes.LlamaCppNative && result.CatalogCount == 1,
                "llama.cpp owner metadata and native router status should choose its native adapter without port inference");
        }
    }

    static void ProviderConnectionDiscoveryRejectsErrorsAndHonorsCancellation()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.OK })
        {
            using var handler = new ConnectionDiscoveryHandler((_, _) => Task.FromResult(
                DiscoveryJson("{\"error\":\"discovery-secret-token\"}", status)));
            using var client = new HttpClient(handler);
            var result = new ProviderConnectionDiscoveryService(client)
                .DiscoverAsync("http://localhost:1234", "discovery-secret-token").GetAwaiter().GetResult();
            Require(!result.Available && result.ApiMode.Length == 0,
                "HTTP success without a catalog and unauthorized responses must not manufacture provider identity");
            Require(!JsonSerializer.Serialize(result).Contains("discovery-secret-token", StringComparison.Ordinal),
                "connection discovery results must never contain the configured credential or echoed response secret");
        }
        using var started = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        using var blockedHandler = new ConnectionDiscoveryHandler(async (_, token) =>
        {
            started.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DiscoveryJson("{}");
        });
        using var blockedClient = new HttpClient(blockedHandler);
        var discovery = new ProviderConnectionDiscoveryService(blockedClient).DiscoverAsync("http://localhost:1234", cancellationToken: cancellation.Token);
        Require(started.Wait(TimeSpan.FromSeconds(3)), "the cancellation fixture did not begin a provider request");
        cancellation.Cancel();
        var cancelled = false;
        try { discovery.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && blockedHandler.Requests.Count == 1, "caller cancellation must stop discovery without probing another server adapter");
    }

    static void ProviderConnectionDiscoveryLocalScanSeparatesCredentials()
    {
        using var handler = new ConnectionDiscoveryHandler((request, _) => Task.FromResult(
            request.RequestUri!.Port is 1234 or 11434 && request.RequestUri.AbsolutePath == "/api/v1/models"
                ? DiscoveryJson("{\"models\":[{\"type\":\"llm\",\"key\":\"local-model\",\"loaded_instances\":[]}]}")
                : DiscoveryJson("{}", HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var scan = new ProviderConnectionDiscoveryService(client)
            .DiscoverLocalAsync("http://localhost:1234/v1", "saved-server-secret").GetAwaiter().GetResult();
        Require(scan.Servers.Count == 2 && scan.Servers[0].BaseUrl == "http://localhost:1234/v1",
            "local discovery must retain saved-server preference while presenting all positive servers");
        Require(handler.Requests.Where(request => request.Uri.Port == 1234).All(request => request.Uri.Host == "localhost"
                    && request.Authorization == "Bearer saved-server-secret")
                && handler.Requests.Where(request => request.Uri.Port != 1234).All(request => request.Authorization.Length == 0),
            "saved credentials must remain on the saved server and never cross into another loopback port");
        Require(handler.Requests.All(request => request.Uri.IsLoopback && request.Uri.Port is 1234 or 11434 or 8080 or 8000
                    && request.Method == "GET"),
            "local discovery must stay within the explicit local candidate list and use read-only requests");
        var candidates = ProviderConnectionDiscoveryService.LocalCandidates("http://localhost:1234/v1");
        Require(candidates.Count == 4 && candidates.Count(candidate => candidate.UseSavedToken) == 1,
            "localhost and numeric-loopback aliases must not duplicate candidate servers or credentials");
        candidates = ProviderConnectionDiscoveryService.LocalCandidates("https://remote.example.test:9443/proxy/v1");
        Require(candidates.Count == 5 && candidates[0].Address == "https://remote.example.test:9443/proxy/v1"
                && candidates.Skip(1).All(candidate => !candidate.UseSavedToken),
            "a saved remote or custom-path server must be checked intact alongside anonymous local defaults");
    }

    static void ProviderConnectionDiscoveryBoundsConcurrentLocalProbes()
    {
        var active = 0;
        var peak = 0;
        using var handler = new ConnectionDiscoveryHandler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); }
            while (observed < current && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return DiscoveryJson("{}");
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var client = new HttpClient(handler);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var scan = new ProviderConnectionDiscoveryService(client, TimeSpan.FromMilliseconds(30))
            .DiscoverLocalAsync("https://saved.example.test:9443/proxy/v1").GetAwaiter().GetResult();
        Require(scan.Servers.Count == 0 && active == 0 && peak is > 1 and <= 4,
            "local discovery must bound concurrent transport requests and release every timed-out request");
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(5),
            "unresponsive servers must not outlive the configured per-probe timeout");
    }

    private static HttpResponseMessage DiscoveryJson(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class ConnectionDiscoveryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<DiscoveryRequest> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new DiscoveryRequest(request.RequestUri!, request.Method.Method,
                request.Headers.TryGetValues("Authorization", out var values) ? string.Join(" ", values) : ""));
            return respond(request, cancellationToken);
        }
    }
    private sealed record DiscoveryRequest(Uri Uri, string Method, string Authorization);
}