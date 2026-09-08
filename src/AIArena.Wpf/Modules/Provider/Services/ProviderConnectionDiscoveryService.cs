using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

/// <summary>Identifies one configured server through read-only catalog evidence, without changing models or routing.</summary>
public sealed class ProviderConnectionDiscoveryService
{
    private const int MaximumProbeResponseBytes = 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly HttpClient httpClient;
    private readonly TimeSpan probeTimeout;

    public ProviderConnectionDiscoveryService(HttpClient? httpClient = null, TimeSpan? probeTimeout = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
        this.probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
        if (this.probeTimeout <= TimeSpan.Zero || this.probeTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
    }

    public async Task<ProviderConnectionDiscoveryResult> DiscoverAsync(
        string serverAddress, string apiToken = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeAddress(serverAddress, out var baseUrl))
            return Failed("", "Enter a valid http:// or https:// server address without a query, fragment, or embedded credentials.");

        var lmStudio = await ProbeAsync(
            token => new LmStudioModelCatalogService(httpClient).TryLoadAsync(baseUrl, apiToken, token), cancellationToken);
        if (lmStudio is { Ok: true } && lmStudio.Models.Any(model => model.HasResidencyEvidence))
            return Found(baseUrl, ModelProviderApiModes.LmStudioNative, "LM Studio", lmStudio.ChatModels.Count);

        var ollama = await ProbeAsync(
            token => new OllamaModelCatalogService(httpClient).TryLoadAsync(baseUrl, apiToken, token), cancellationToken);
        if (ollama is { Ok: true })
        {
            var identified = ollama.Models.Any(model => HasOllamaModelEvidence(model));
            // Empty inventories cannot supply model fingerprints. Require two valid
            // Ollama inventories plus its version endpoint before identifying an empty server.
            if (!identified && ollama.Models.Count == 0 && ollama.RunningModelsOk)
            {
                var version = await ProbeAsync(token => GetAsync(
                    new Uri(ModelProviderClient.NormalizeOllamaApiBase(baseUrl) + "/version"), apiToken, token), cancellationToken);
                identified = version is { Ok: true } && HasVersion(version.Body);
            }
            if (identified)
                return Found(baseUrl, ModelProviderApiModes.OllamaNative, "Ollama", ollama.Models.Count);
        }

        // Catalog-only probes avoid props/slots requests that can wake a sleeping
        // router model. A generic /health success is not provider identity evidence.
        var llamaModels = await ProbeAsync(token => GetAsync(
            new Uri(ModelProviderClient.NormalizeLlamaCppApiBase(baseUrl) + "/models"), apiToken, token), cancellationToken);
        if (llamaModels is { Ok: true } && HasLlamaCppEvidence(llamaModels.Body))
            return Found(baseUrl, ModelProviderApiModes.LlamaCppNative, "llama.cpp", ModelCount(llamaModels.Body));

        var compatible = await ProbeAsync(token => GetAsync(new Uri(baseUrl + "/models"), apiToken, token), cancellationToken);
        if (compatible is { Ok: true } && TryModelCount(compatible.Body, out var compatibleCount))
        {
            return HasLlamaCppEvidence(compatible.Body)
                ? Found(baseUrl, ModelProviderApiModes.LlamaCppNative, "llama.cpp", compatibleCount)
                : Found(baseUrl, ModelProviderApiModes.OpenAiCompatible, "Compatible server", compatibleCount);
        }

        var error = compatible?.Error;
        if (string.IsNullOrWhiteSpace(error))
            error = "No supported model catalog responded. Check the server address and that its API server is running.";
        return Failed(baseUrl, ProviderErrorSanitizer.Sanitize(error, apiToken));
    }

    public async Task<ProviderConnectionScanResult> DiscoverLocalAsync(
        string currentServerAddress, string currentApiToken = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scan.CancelAfter(TimeSpan.FromSeconds(10));
        using var limit = new SemaphoreSlim(4, 4);
        var candidates = LocalCandidates(currentServerAddress);
        var tasks = candidates.Select(async candidate =>
        {
            var entered = false;
            try
            {
                await limit.WaitAsync(scan.Token);
                entered = true;
                // Only the exact saved candidate carries its credentials. Other local
                // ports and paths are independently discovered without authentication.
                var result = await DiscoverAsync(candidate.Address, candidate.UseSavedToken ? currentApiToken : "", scan.Token);
                return result.Available ? result : null;
            }
            catch (OperationCanceledException) when (scan.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                if (entered) limit.Release();
            }
        }).ToArray();
        var found = (await Task.WhenAll(tasks)).OfType<ProviderConnectionDiscoveryResult>().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var timedOut = scan.IsCancellationRequested;
        var summary = found.Length switch
        {
            0 => timedOut ? "The server scan timed out. Enter a server address to connect directly." : "No running model servers were found. Start a server or enter its address.",
            1 => $"Found {found[0].ProviderName}.",
            _ => $"Found {found.Length} running model servers. Choose a server to connect."
        };
        return new ProviderConnectionScanResult(Array.AsReadOnly(found), timedOut, summary);
    }

    internal static IReadOnlyList<ProviderDiscoveryCandidate> LocalCandidates(string currentServerAddress)
    {
        var result = new List<ProviderDiscoveryCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Add(currentServerAddress, true);
        foreach (var port in new[] { 1234, 11434, 8080, 8000 }) Add($"http://127.0.0.1:{port}/v1", false);
        return result;

        void Add(string address, bool useSavedToken)
        {
            if (!TryNormalizeAddress(address, out var normalized)) return;
            var uri = new Uri(normalized);
            var keyUri = new UriBuilder(uri);
            if (uri.IsLoopback) keyUri.Host = "localhost";
            if (keys.Add(keyUri.Uri.AbsoluteUri)) result.Add(new ProviderDiscoveryCandidate(normalized, useSavedToken));
        }
    }

    private async Task<T?> ProbeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) where T : class
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(probeTimeout);
        try { return await action(probe.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or UriFormatException)
        {
            return null;
        }
    }

    private async Task<CatalogProbe> GetAsync(Uri endpoint, string apiToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ProviderHttpHelpers.ApplyAuthorization(request, apiToken);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new CatalogProbe(false, "", "The server requires a valid access token. Check its authentication settings.");
        if (!response.IsSuccessStatusCode)
            return new CatalogProbe(false, "", $"The server did not provide a model catalog (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > MaximumProbeResponseBytes)
            return new CatalogProbe(false, "", "The server returned an oversized model catalog.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumProbeResponseBytes)
                return new CatalogProbe(false, "", "The server returned an oversized model catalog.");
            buffer.Write(chunk, 0, read);
        }
        return new CatalogProbe(true, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)), "");
    }

    private static bool HasOllamaModelEvidence(OllamaModelInfo model)
    {
        var digest = model.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? model.Digest[7..] : model.Digest;
        return digest.Length == 64 && digest.All(Uri.IsHexDigit)
            && (model.Format.Length > 0 || model.Family.Length > 0 || model.QuantizationLevel.Length > 0);
    }

    private static bool HasVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var version = ProviderHttpHelpers.FirstString(doc.RootElement, "version");
            return version.Length is > 0 and <= 64 && Version.TryParse(version.Split('-', '+')[0], out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool HasLlamaCppEvidence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array) return false;
            return models.EnumerateArray().Any(model =>
            {
                if (model.ValueKind != JsonValueKind.Object || ProviderHttpHelpers.FirstString(model, "id").Length == 0) return false;
                var owner = ProviderHttpHelpers.FirstString(model, "owned_by");
                if (owner.Equals("llamacpp", StringComparison.OrdinalIgnoreCase) || owner.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase)) return true;
                return model.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                    && ProviderHttpHelpers.FirstString(status, "value") is "loaded" or "unloaded" or "loading" or "failed";
            });
        }
        catch (JsonException) { return false; }
    }

    private static bool TryModelCount(string json, out int count)
    {
        count = 0;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array
                || models.EnumerateArray().Any(model => model.ValueKind != JsonValueKind.Object || ProviderHttpHelpers.FirstString(model, "id").Length == 0)) return false;
            count = ModelProviderHealthService.ParseModelNames(json).Count;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { return false; }
    }

    private static int ModelCount(string json) => TryModelCount(json, out var count) ? count : 0;

    private static bool TryNormalizeAddress(string address, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var value = uri.AbsoluteUri.TrimEnd('/');
        if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        normalized = ModelProviderClient.NormalizeBaseUrl(value);
        return true;
    }

    private static ProviderConnectionDiscoveryResult Found(string baseUrl, string mode, string provider, int count) =>
        new(true, baseUrl, mode, provider, count, $"Connected to {provider}. {count} catalog model(s) available.", "");
    private static ProviderConnectionDiscoveryResult Failed(string baseUrl, string error) => new(false, baseUrl, "", "", 0, error, error);
    private sealed record CatalogProbe(bool Ok, string Body, string Error);
}

internal sealed record ProviderDiscoveryCandidate(string Address, bool UseSavedToken);
public sealed record ProviderConnectionDiscoveryResult(bool Available, string BaseUrl, string ApiMode, string ProviderName, int CatalogCount, string Summary, string Error);
public sealed record ProviderConnectionScanResult(IReadOnlyList<ProviderConnectionDiscoveryResult> Servers, bool TimedOut, string Summary);