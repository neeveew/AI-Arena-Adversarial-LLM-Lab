using System.Net;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void OllamaCatalogStreamsAndBoundsNativeResponses()
    {
        var tagsContent = new OllamaResponseHeadersProbeContent(Encoding.UTF8.GetBytes(OllamaTagsJson()));
        var psContent = new OllamaResponseHeadersProbeContent(Encoding.UTF8.GetBytes(OllamaPsJson()));
        var streamingHandler = new TestHttpMessageHandler(request =>
        {
            var content = request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true
                ? tagsContent
                : psContent;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var streamed = new OllamaModelCatalogService(new HttpClient(streamingHandler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();

        Require(streamed.Ok
                && streamed.RunningModelsOk
                && tagsContent.ReadStreamCalls == 1
                && psContent.ReadStreamCalls == 1
                && tagsContent.SerializeCalls == 0
                && psContent.SerializeCalls == 0,
            "Ollama catalog requests should stream response bodies after headers instead of eagerly buffering them");

        var declaredOversizeHandler = new TestHttpMessageHandler(_ =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = OllamaModelCatalogService.MaximumResponseBytes + 1L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var declaredOversize = new OllamaModelCatalogService(new HttpClient(declaredOversizeHandler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();
        Require(!declaredOversize.Ok
                && declaredOversize.Models.Count == 0
                && declaredOversize.Error.Contains("response limit", StringComparison.OrdinalIgnoreCase),
            "an oversized /api/tags response must be unavailable rather than authoritative empty evidence");

        var oversizedPsStream = new CountingReadStream(OllamaModelCatalogService.MaximumResponseBytes + 8192);
        var oversizedPsHandler = new TestHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(OllamaTagsJson(), Encoding.UTF8, "application/json")
                };
            }

            var content = new StreamContent(oversizedPsStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var oversizedPs = new OllamaModelCatalogService(new HttpClient(oversizedPsHandler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();
        Require(oversizedPs.Ok
                && oversizedPs.Models.Count == 2
                && !oversizedPs.RunningModelsOk
                && oversizedPs.RunningModelsError.Contains("response limit", StringComparison.OrdinalIgnoreCase)
                && oversizedPsStream.BytesRead <= OllamaModelCatalogService.MaximumResponseBytes + 1,
            "an oversized /api/ps response should stop at the byte boundary and preserve authoritative tags without inventing residency");
    }

    static void OllamaCatalogRejectsMalformedEvidenceAndPreservesTags()
    {
        var failedPsHandler = new TestHttpMessageHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(OllamaTagsJson(), Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        "{\"error\":\"running inventory unavailable\"}",
                        Encoding.UTF8,
                        "application/json")
                });
        var failedPs = new OllamaModelCatalogService(new HttpClient(failedPsHandler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();
        Require(failedPs.Ok
                && failedPs.Models.Count == 2
                && !failedPs.RunningModelsOk
                && failedPs.RunningModelsError.Contains("running inventory unavailable", StringComparison.Ordinal),
            "an HTTP failure from /api/ps should preserve authoritative tags and expose unavailable residency");

        foreach (var malformedTags in new[]
                 {
                     "{}",
                     "{\"models\":[null]}",
                     OllamaExcessiveDepthJson()
                 })
        {
            var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(malformedTags, Encoding.UTF8, "application/json")
            });
            var catalog = new OllamaModelCatalogService(new HttpClient(handler))
                .TryLoadAsync("http://127.0.0.1:11434/v1")
                .GetAwaiter()
                .GetResult();
            Require(!catalog.Ok && catalog.Models.Count == 0,
                "malformed or excessive-depth /api/tags JSON must not become authoritative empty evidence");
        }

        foreach (var malformedPs in new[]
                 {
                     "{}",
                     "{\"models\":[null]}",
                     OllamaExcessiveDepthJson()
                 })
        {
            var handler = new TestHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true
                        ? OllamaTagsJson()
                        : malformedPs,
                    Encoding.UTF8,
                    "application/json")
            });
            var catalog = new OllamaModelCatalogService(new HttpClient(handler))
                .TryLoadAsync("http://127.0.0.1:11434/v1")
                .GetAwaiter()
                .GetResult();
            Require(catalog.Ok
                    && catalog.Models.Count == 2
                    && !catalog.RunningModelsOk
                    && !string.IsNullOrWhiteSpace(catalog.RunningModelsError),
                "malformed or excessive-depth /api/ps JSON should preserve tags and mark residency unavailable");
        }

        var duplicateHandler = new TestHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true
                    ? """{"models":[{"model":"qwen:8b","name":"qwen:8b","size":1000},{"model":"QWEN:8B","name":"qwen-alias","details":{"quantization_level":"Q4"}}]}"""
                    : """{"models":[{"model":"qwen:8b","context_length":8192,"size_vram":500}]}""",
                Encoding.UTF8,
                "application/json")
        });
        var duplicateCatalog = new OllamaModelCatalogService(new HttpClient(duplicateHandler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();
        Require(duplicateCatalog.Ok
                && duplicateCatalog.RunningModelsOk
                && duplicateCatalog.Models.Count == 1
                && duplicateCatalog.Models[0].Loaded
                && duplicateCatalog.Models[0].ContextLength == 8192
                && duplicateCatalog.Models[0].Aliases.Contains("qwen-alias", StringComparer.OrdinalIgnoreCase),
            "duplicate case-insensitive Ollama tags did not merge deterministically with running evidence and aliases");
    }

    static void OllamaCatalogCapsSourceEntriesAndProjectsPartialEvidence()
    {
        const int extraTags = 3;
        const int extraRunning = 2;
        var tagsJson = OllamaModelArrayJson(
            "tag",
            OllamaModelCatalogService.MaximumModelEntries + extraTags,
            running: false);
        var runningJson = OllamaModelArrayJson(
            "running",
            OllamaModelCatalogService.MaximumModelEntries + extraRunning,
            running: true);
        var handler = new TestHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri?.AbsolutePath.EndsWith("/tags", StringComparison.Ordinal) == true
                    ? tagsJson
                    : runningJson,
                Encoding.UTF8,
                "application/json")
        });
        var catalog = new OllamaModelCatalogService(new HttpClient(handler))
            .TryLoadAsync("http://127.0.0.1:11434/v1")
            .GetAwaiter()
            .GetResult();

        Require(catalog.Ok
                && catalog.RunningModelsOk
                && catalog.Models.Count == OllamaModelCatalogService.MaximumModelEntries * 2
                && catalog.OmittedTagEntryCount == extraTags
                && catalog.OmittedRunningEntryCount == extraRunning
                && catalog.OmittedModelCount == extraTags + extraRunning,
            "Ollama tags and running inventories should each retain 1024 entries and report exact source omissions");

        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
            ApiMode = ModelProviderApiModes.OllamaNative,
            Model = "tag-0000"
        };
        var projection = new ProviderModelCatalogProjectionService();
        var lease = projection.BeginRefresh("ollama-bounds-session", snapshot);
        var projected = ProviderModelCatalogProjectionService.FromOllama(
            lease,
            catalog,
            "tag-0000",
            DateTimeOffset.UnixEpoch);
        var projectedCount = projected.LoadedModels.Count + projected.AvailableModels.Count;
        var displayOmissions = catalog.Models.Count - ProviderModelCatalogProjectionService.MaximumModelCount;
        var expectedOmissions = extraTags + extraRunning + displayOmissions;
        Require(projected.CatalogEvidence == ProviderCatalogEvidenceState.Partial
                && projected.ResidencyEvidence == ProviderCatalogEvidenceState.Partial
                && projectedCount == ProviderModelCatalogProjectionService.MaximumModelCount
                && projected.OmittedModelCount == expectedOmissions
                && projected.Status.Contains(
                    $"{expectedOmissions} additional catalog entries omitted",
                    StringComparison.Ordinal),
            "Ollama source and display omissions should make the bounded UI projection explicitly Partial");
    }

    private static string OllamaExcessiveDepthJson() =>
        "{\"models\":[],\"nested\":"
        + new string('[', OllamaModelCatalogService.MaximumJsonDepth + 2)
        + "0"
        + new string(']', OllamaModelCatalogService.MaximumJsonDepth + 2)
        + "}";

    private static string OllamaModelArrayJson(string prefix, int count, bool running)
    {
        var models = Enumerable.Range(0, count).Select(index => new
        {
            name = $"{prefix}-{index:0000}",
            model = $"{prefix}-{index:0000}",
            size = 1_000_000L + index,
            size_vram = running ? 500_000L + index : (long?)null,
            context_length = running ? 8192 : (int?)null,
            details = new
            {
                format = "gguf",
                family = prefix,
                parameter_size = "1B",
                quantization_level = "Q4_K_M"
            }
        });
        return JsonSerializer.Serialize(new { models });
    }
}

sealed class OllamaResponseHeadersProbeContent(byte[] bytes) : HttpContent
{
    public int SerializeCalls { get; private set; }
    public int ReadStreamCalls { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        SerializeCalls++;
        throw new InvalidOperationException("ResponseContentRead attempted to buffer the Ollama response body.");
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        ReadStreamCalls++;
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateContentReadStreamAsync();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
