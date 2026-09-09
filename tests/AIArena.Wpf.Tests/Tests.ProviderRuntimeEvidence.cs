using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void LmStudioRuntimeEvidenceSelectsExactInstanceAndConservativeAlias()
    {
        var model = RuntimeEvidenceLmModel("catalog/model", [
            new("instance-small", 4096, null, null, null),
            new("instance-large", 16384, null, null, null)
        ], ["off", "on"]);
        var catalog = LmStudioModelCatalog.Success([model]);
        var now = DateTimeOffset.UtcNow;
        var exact = ProviderModelRuntimeEvidenceResolver.FromLmStudio(catalog, "INSTANCE-LARGE", now);
        Require(exact is { ContextWindow: 16384, ModelInstanceId: "instance-large", Source: "lmstudio_native" }
                && exact.CheckedAt == now,
            "an exact loaded-instance route should use that instance's allocation and identity, independent of smaller sibling instances");
        var alias = ProviderModelRuntimeEvidenceResolver.FromLmStudio(catalog, "catalog/model", now);
        Require(alias is { ContextWindow: 4096, ModelInstanceId: "" },
            "a catalog alias that may select several live instances must use the smallest confirmed allocation without claiming an exact instance");
        Require(ProviderModelRuntimeEvidenceResolver.FromLmStudio(catalog, "unrelated-model", now) is null,
            "an unrelated model route must not inherit another model's runtime allocation");
    }

    static void LmStudioRuntimeEvidenceKeepsUnknownAllocationsUnknown()
    {
        var model = RuntimeEvidenceLmModel("catalog/model", [
            new("known", 8192, null, null, null),
            new("unknown", null, null, null, null)
        ], []);
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in new[]
        {
            model,
            model with { LoadedInstances = [] },
            model with { HasResidencyEvidence = false },
            model with { LoadedInstances = [new("invalid", -1, null, null, null)] },
            model with { LoadedInstances = [new("oversized", int.MaxValue, null, null, null)] }
        })
        {
            var evidence = ProviderModelRuntimeEvidenceResolver.FromLmStudio(LmStudioModelCatalog.Success([candidate]), candidate.Key, now);
            Require(evidence is { ContextWindow: 0 },
                "missing, ambiguous, invalid, or unconfirmed allocations must stay unknown even when the catalog advertises a large training maximum");
        }
        var exactKnown = ProviderModelRuntimeEvidenceResolver.FromLmStudio(LmStudioModelCatalog.Success([model]), "known", now);
        Require(exactKnown is { ContextWindow: 8192 },
            "unknown sibling allocation must not erase confirmed evidence for an explicitly addressed live instance");
    }

    static void LmStudioRuntimeEvidenceRequiresSharedReasoningCapabilities()
    {
        var first = RuntimeEvidenceLmModel("variant-one", [new("one", 4096, null, null, null)], ["off", "low", "high"])
            with { Aliases = ["shared-alias", "variant-one", "one"] };
        var second = RuntimeEvidenceLmModel("variant-two", [new("two", 8192, null, null, null)], ["low", "high"])
            with { Aliases = ["shared-alias", "variant-two", "two"] };
        var now = DateTimeOffset.UtcNow;
        var shared = ProviderModelRuntimeEvidenceResolver.FromLmStudio(LmStudioModelCatalog.Success([first, second]), "shared-alias", now);
        Require(shared is { CanDisableReasoning: false, ReducedReasoning: "low", ContextWindow: 4096 },
            "a shared route may reduce reasoning only to a mode supported by every matching model; one off-capable variant is insufficient");
        var exact = ProviderModelRuntimeEvidenceResolver.FromLmStudio(LmStudioModelCatalog.Success([first, second]), "one", now);
        Require(exact is { CanDisableReasoning: true, ReducedReasoning: "off" },
            "an exact instance should retain its own advertised off capability");
        var unsupported = ProviderModelRuntimeEvidenceResolver.FromLmStudio(
            LmStudioModelCatalog.Success([first, second with { ReasoningOptions = [] }]), "shared-alias", now);
        Require(unsupported is { CanDisableReasoning: false, ReducedReasoning: "" },
            "an ambiguous route must not invent a reasoning override when a candidate lacks supporting evidence");
    }

    static void RuntimeEvidenceProbesFreshCatalogAndPreservesRouting()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/models",
                "LM Studio runtime evidence must use the native catalog endpoint");
            return RuntimeEvidenceJson(JsonSerializer.Serialize(new
            {
                models = new[] { new {
                    type = "llm", key = "catalog/model", max_context_length = 262144,
                    loaded_instances = new[] { new { id = "live-model", config = new { context_length = ++calls * 2048 } } }
                } }
            }));
        });
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var firstConfig = RuntimeEvidenceConfig(ModelProviderApiModes.LmStudioNative, "catalog/model", "http://lm-one.test:1234/v1", "first-token");
        var first = resolver.ResolveAsync(firstConfig).GetAwaiter().GetResult();
        var refreshed = resolver.ResolveAsync(firstConfig).GetAwaiter().GetResult();
        var routed = resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.LmStudioNative, "catalog/model", "http://lm-two.test:4321/v1", "second-token"))
            .GetAwaiter().GetResult();
        Require(first is { ContextWindow: 2048 } && refreshed is { ContextWindow: 4096 } && routed is { ContextWindow: 6144 } && calls == 3,
            "each model-call boundary must obtain fresh native allocation evidence, including repeated requests for the same route");
        Require(handler.Requests.Select(uri => uri.Authority).SequenceEqual(new[] { "lm-one.test:1234", "lm-one.test:1234", "lm-two.test:4321" })
                && handler.AuthorizationHeaders.SequenceEqual(new[] { "Bearer first-token", "Bearer first-token", "Bearer second-token" }),
            "native evidence probes must stay on the selected endpoint and use only that endpoint's configured token");
    }

    static void OllamaRuntimeEvidenceUsesRunningAllocationAndNativeReasoningCapabilities()
    {
        var family = "qwen3";
        var handler = new TestHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => RuntimeEvidenceJson("""{"models":[{"name":"reasoner:latest","model":"reasoner:latest"}]}"""),
            "/api/ps" => RuntimeEvidenceJson("""{"models":[{"name":"reasoner:latest","model":"reasoner:latest","context_length":6144,"expires_at":"2099-01-01T00:00:00Z"}]}"""),
            "/api/show" => RuntimeEvidenceJson(JsonSerializer.Serialize(new
            {
                capabilities = new[] { "completion", "thinking" },
                model_info = new Dictionary<string, object> { ["general.architecture"] = family, ["qwen3.context_length"] = 262144 },
                details = new { family }
            })),
            _ => throw new InvalidOperationException("Runtime evidence made an unexpected Ollama request.")
        });
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var config = RuntimeEvidenceConfig(ModelProviderApiModes.OllamaNative, "reasoner:latest", "http://ollama.test:11434/v1", "ollama-token");
        var regular = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        family = "gptoss";
        var mandatoryThinking = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        Require(regular is { ContextWindow: 6144, CanDisableReasoning: true, ReducedReasoning: "off", Source: "ollama_native" },
            "Ollama must combine the live /ps allocation with explicit /show thinking capability, without borrowing the training maximum");
        Require(mandatoryThinking is { ContextWindow: 6144, CanDisableReasoning: false, ReducedReasoning: "low" },
            "GPT-OSS must use its supported low reasoning mode because boolean off is not a supported reduction");
        Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual(new[] { "/api/tags", "/api/ps", "/api/show", "/api/tags", "/api/ps", "/api/show" })
                && handler.Requests.All(uri => uri.Authority == "ollama.test:11434")
                && handler.AuthorizationHeaders.All(value => value == "Bearer ollama-token"),
            "catalog, running allocation, and capabilities must all be freshly probed on the selected authenticated Ollama route");
        foreach (var body in handler.Bodies.Where(value => value.Length > 0))
        {
            using var document = JsonDocument.Parse(body);
            Require(document.RootElement.GetProperty("model").GetString() == config.Model
                    && !document.RootElement.GetProperty("verbose").GetBoolean(),
                "Ollama capability probes must identify the requested model without fetching verbose model metadata");
        }
    }

    static void OllamaRuntimeEvidenceRejectsExpiredMissingAndFailedRuntimeInventory()
    {
        var psBody = """{"models":[{"name":"model:latest","model":"model:latest","context_length":8192,"expires_at":"2000-01-01T00:00:00Z"}]}""";
        var psStatus = HttpStatusCode.OK;
        var showStatus = HttpStatusCode.OK;
        var handler = new TestHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => RuntimeEvidenceJson("""{"models":[{"name":"model:latest","model":"model:latest"}]}"""),
            "/api/ps" => RuntimeEvidenceJson(psBody, psStatus),
            "/api/show" => RuntimeEvidenceJson("""{"capabilities":["completion"],"model_info":{"general.architecture":"qwen3","qwen3.context_length":262144}}""", showStatus),
            _ => throw new InvalidOperationException("Unexpected runtime evidence request.")
        });
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var config = RuntimeEvidenceConfig(ModelProviderApiModes.OllamaNative, "model:latest");
        var expired = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        psBody = """{"models":[]}""";
        var unloaded = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        psStatus = HttpStatusCode.ServiceUnavailable;
        var failedInventory = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        Require(new[] { expired, unloaded, failedInventory }.All(value => value is { ContextWindow: 0, CanDisableReasoning: false, ReducedReasoning: "" }),
            "expired, absent, and unavailable running evidence must retain unknown context; model metadata alone must not authorize reasoning overrides");
        psStatus = HttpStatusCode.OK;
        psBody = """{"models":[{"name":"model:latest","model":"model:latest","context_length":4096,"expires_at":"2099-01-01T00:00:00Z"}]}""";
        showStatus = HttpStatusCode.ServiceUnavailable;
        var unavailableCapabilities = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        Require(unavailableCapabilities is { ContextWindow: 4096, CanDisableReasoning: false, ReducedReasoning: "" },
            "failed optional capability metadata should preserve independently confirmed context while keeping reasoning support unknown");
    }

    static void RuntimeEvidenceKeepsCompatibleAndFailedCatalogsUnknown()
    {
        var handler = new TestHttpMessageHandler(_ => RuntimeEvidenceJson("""{"error":"unavailable"}""", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var compatible = resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.OpenAiCompatible, "model")).GetAwaiter().GetResult();
        var empty = resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.LmStudioNative, "")).GetAwaiter().GetResult();
        Require(compatible is null && empty is null && handler.Requests.Count == 0,
            "generic compatible mode and missing model identity must return unknown evidence without guessing a native server or issuing probes");
        var lmFailure = resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.LmStudioNative, "model")).GetAwaiter().GetResult();
        var ollamaFailure = resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.OllamaNative, "model")).GetAwaiter().GetResult();
        Require(lmFailure is null && ollamaFailure is null && handler.Requests.Count == 2
                && handler.Requests.All(uri => !uri.AbsolutePath.EndsWith("/show", StringComparison.Ordinal)),
            "failed native catalogs must produce unknown evidence and must not fall through to unrelated servers or model capability requests");
    }

    static void OllamaRuntimeEvidencePropagatesCapabilityProbeCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new TestHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/show")
            {
                cancellation.Cancel();
                return RuntimeEvidenceJson("""{"capabilities":["thinking"]}""");
            }
            return RuntimeEvidenceJson("""{"models":[{"name":"model:latest","model":"model:latest","context_length":4096}]}""");
        });
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var canceled = false;
        try
        {
            resolver.ResolveAsync(RuntimeEvidenceConfig(ModelProviderApiModes.OllamaNative, "model:latest"), cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            canceled = true;
        }
        Require(canceled && handler.Requests.Last().AbsolutePath == "/api/show",
            "caller cancellation during the optional capability probe must propagate instead of returning apparently successful runtime evidence");
    }

    static void LlamaCppRuntimeEvidenceUsesOperationalSlotsNeverTrainingMaximum()
    {
        var propsBody = """{"default_generation_settings":{"n_ctx":12288},"build_info":"llama.cpp fixture"}""";
        var slotsBody = """[{"id":0,"n_ctx":4096},{"id":1,"n_ctx":8192}]""";
        var operationalStatus = HttpStatusCode.OK;
        var handler = new TestHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health" => RuntimeEvidenceJson("""{"status":"ok"}"""),
            "/models" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/v1/models" => RuntimeEvidenceJson("""{"data":[{"id":"fixture.gguf","owned_by":"llamacpp","meta":{"n_ctx_train":262144}}]}"""),
            "/props" => RuntimeEvidenceJson(propsBody, operationalStatus),
            "/slots" => RuntimeEvidenceJson(slotsBody, operationalStatus),
            _ => throw new InvalidOperationException("Unexpected llama.cpp runtime request.")
        });
        using var http = new HttpClient(handler);
        var resolver = RuntimeEvidenceResolverForTest(http);
        var config = RuntimeEvidenceConfig(ModelProviderApiModes.LlamaCppNative, "fixture.gguf");
        var slots = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        slotsBody = "[]";
        var props = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        operationalStatus = HttpStatusCode.NotFound;
        var trainingOnly = resolver.ResolveAsync(config).GetAwaiter().GetResult();
        Require(slots is { ContextWindow: 4096, CanDisableReasoning: false, Source: "llamacpp_runtime" },
            "a model routable across multiple llama.cpp slots must be budgeted against the smallest observed live slot");
        Require(props is { ContextWindow: 12288 },
            "runtime properties may provide the operational allocation when slot allocations are unavailable");
        Require(trainingOnly is { ContextWindow: 0, CanDisableReasoning: false },
            "training-context metadata from a compatible model listing must never become a loaded llama.cpp allocation");
    }

    private static ProviderModelRuntimeEvidenceResolver RuntimeEvidenceResolverForTest(HttpClient http) =>
        new(new LmStudioModelCatalogService(http), new OllamaModelCatalogService(http), new LlamaCppRuntimeService(http), http);

    private static ModelProviderConfig RuntimeEvidenceConfig(string apiMode, string model,
        string baseUrl = "http://runtime.test:1234/v1", string token = "fixture-token") => new()
    {
        ApiMode = apiMode, Model = model, BaseUrl = baseUrl, ApiToken = token,
        ConfiguredContextWindow = 262144, ContextLength = 262144, Reasoning = "high"
    };

    private static LmStudioModelInfo RuntimeEvidenceLmModel(string key, IReadOnlyList<LmStudioLoadedInstance> instances,
        IReadOnlyList<string> reasoningOptions) => new(
        Key: key, DisplayName: key, Type: "llm", Publisher: "fixture", Architecture: "fixture",
        QuantizationName: "Q4", BitsPerWeight: 4, SizeBytes: 1, ParamsString: "1B",
        LoadedInstances: instances, MaxContextLength: 262144, Format: "gguf", Vision: false,
        TrainedForToolUse: false, ReasoningOptions: reasoningOptions, ReasoningDefault: "high",
        SelectedVariant: "", Aliases: new[] { key }.Concat(instances.Select(instance => instance.Id)).ToArray(),
        Description: "", HasResidencyEvidence: true);

    private static HttpResponseMessage RuntimeEvidenceJson(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
