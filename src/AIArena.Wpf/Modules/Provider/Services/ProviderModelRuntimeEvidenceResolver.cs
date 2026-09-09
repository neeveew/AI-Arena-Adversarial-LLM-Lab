using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

/// <summary>Reads native server evidence at the request boundary; never trusts cached catalog maxima as loaded context.</summary>
internal sealed class ProviderModelRuntimeEvidenceResolver(
    LmStudioModelCatalogService lmStudio,
    OllamaModelCatalogService ollama,
    LlamaCppRuntimeService llamaCpp,
    HttpClient? metadataClient = null) : IModelRuntimeEvidenceResolver
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient httpClient = metadataClient ?? SharedHttpClient;

    public async Task<ModelRuntimeEvidence?> ResolveAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(config.Model)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (ModelProviderApiModes.IsLmStudioNative(config.ApiMode))
            {
                var catalog = await lmStudio.TryLoadAsync(config.BaseUrl, config.ApiToken, deadline.Token).ConfigureAwait(false);
                return FromLmStudio(catalog, config.Model, DateTimeOffset.UtcNow);
            }
            if (ModelProviderApiModes.IsOllamaNative(config.ApiMode))
            {
                var catalog = await ollama.TryLoadAsync(config.BaseUrl, config.ApiToken, deadline.Token).ConfigureAwait(false);
                var model = catalog.Ok ? catalog.Find(config.Model) : null;
                if (model is null) return null;
                var now = DateTimeOffset.UtcNow;
                var context = catalog.RunningModelsOk && model.Loaded
                    && (model.ExpiresAt is null || model.ExpiresAt > now)
                    ? PositiveContext(model.ContextLength) : 0;
                var reasoning = await ReadOllamaReasoningAsync(config, deadline.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new ModelRuntimeEvidence(context, reasoning == "off", model.PreferredIdentifier,
                    now, "ollama_native", reasoning);
            }
            if (ModelProviderApiModes.IsLlamaCppNative(config.ApiMode))
            {
                var runtime = await llamaCpp.InspectAsync(config, deadline.Token).ConfigureAwait(false);
                if (!runtime.Available || !runtime.IsLlamaCpp || runtime.Loaded == false) return null;
                var slotContexts = runtime.Slots.Select(slot => slot.ContextLength).ToArray();
                var context = slotContexts.Length > 0 && slotContexts.All(value => value is > 0)
                    ? slotContexts.Min(value => PositiveContext(value))
                    : PositiveContext(runtime.OperationalContextLength);
                return new ModelRuntimeEvidence(context, false, runtime.Model, runtime.CheckedAt, "llamacpp_runtime");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException or UriFormatException)
        {
            // Optional evidence may be unavailable. Never borrow another server's context or credentials.
        }
        return null;
    }

    internal static ModelRuntimeEvidence? FromLmStudio(LmStudioModelCatalog catalog, string requestedModel, DateTimeOffset now)
    {
        if (!catalog.Ok) return null;
        var exact = catalog.ChatModels.Where(model => model.HasResidencyEvidence
            && model.LoadedInstances.Any(instance => instance.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))).ToArray();
        var matches = exact.Length > 0 ? exact
            : catalog.ChatModels.Where(model => model.Matches(requestedModel)).ToArray();
        if (matches.Length == 0) return null;
        var instances = matches.Where(model => model.HasResidencyEvidence)
            .SelectMany(model => model.LoadedInstances)
            .Where(instance => exact.Length == 0 || instance.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        // An alias may address several live instances. Budget against the smallest confirmed allocation.
        // If any candidate allocation is unknown, retain that uncertainty instead of using a training maximum.
        var context = matches.All(model => model.HasResidencyEvidence && model.LoadedInstances.Count > 0) && instances.Length > 0
            && instances.All(instance => instance.ContextLength is > 0)
            ? instances.Min(instance => PositiveContext(instance.ContextLength)) : 0;
        var off = matches.All(model => model.ReasoningOptions.Contains("off", StringComparer.OrdinalIgnoreCase));
        var low = matches.All(model => model.ReasoningOptions.Contains("low", StringComparer.OrdinalIgnoreCase));
        return new ModelRuntimeEvidence(context, off, instances.Length == 1 ? instances[0].Id : "",
            now, "lmstudio_native", off ? "off" : low ? "low" : "");
    }

    private async Task<string> ReadOllamaReasoningAsync(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var api = ModelProviderClient.NormalizeOllamaApiBase(config.BaseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(api + "/"), "show"))
            {
                Content = JsonContent.Create(new { model = config.Model, verbose = false })
            };
            ProviderHttpHelpers.ApplyAuthorization(request, config.ApiToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "";
            const int maximumBytes = 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) return "";
            using var buffer = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maximumBytes) return "";
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array
                || !capabilities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "thinking")) return "";
            var architecture = root.TryGetProperty("model_info", out var info)
                ? ProviderHttpHelpers.FirstString(info, "general.architecture") : "";
            var family = root.TryGetProperty("details", out var details)
                ? ProviderHttpHelpers.FirstString(details, "family") : "";
            // Ollama documents GPT-OSS as mandatory-thinking: boolean false is ignored.
            return architecture.Equals("gptoss", StringComparison.OrdinalIgnoreCase)
                || family.Equals("gptoss", StringComparison.OrdinalIgnoreCase) ? "low" : "off";
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException or UriFormatException)
        {
            return "";
        }
    }

    private static int PositiveContext(int? value) => value is > 0 and <= ModelRuntimeSettingsRegistry.MaximumConfiguredContextWindow ? value.Value : 0;
}
