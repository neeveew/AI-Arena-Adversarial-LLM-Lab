using AIArena.Core.Models;

namespace AIArena.Wpf.Services;

/// <summary>Captures a request destination and credentials together before any await.</summary>
internal sealed record ProviderOperationContext
{
    public string SessionId { get; }
    public string BaseUrl { get; }
    public string ApiMode { get; }
    public string ApiToken { get; }
    public string ConnectionIdentity { get; }

    public ProviderOperationContext(string sessionId, ModelProviderConfig config)
    {
        SessionId = sessionId;
        BaseUrl = config.BaseUrl;
        ApiMode = config.ApiMode;
        ApiToken = config.ApiToken;
        ConnectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(sessionId, config);
    }

    public bool Matches(ProviderOperationContext? other) => other is not null
        && SessionId.Equals(other.SessionId, StringComparison.Ordinal)
        && ConnectionIdentity.Equals(other.ConnectionIdentity, StringComparison.Ordinal);

    public override string ToString() => $"Provider operation ({SessionId})";
}

/// <summary>Initial observation always uses the same destination as download creation.</summary>
internal sealed class ProviderDownloadWorkflow(LmStudioModelDownloadService service)
{
    public async Task<LmStudioModelDownloadResult> StartAsync(
        ProviderOperationContext context, string model, string quantization, CancellationToken cancellationToken,
        Func<LmStudioModelDownloadResult, Task>? accepted = null)
    {
        var result = await service.StartDownloadAsync(context.BaseUrl, model, quantization,
            context.ApiMode, context.ApiToken, cancellationToken);
        // Preserve acceptance before optional observation: cancellation or transport failure cannot erase it.
        if (result.Ok && accepted is not null) await accepted(result);
        return result.Ok && !result.IsComplete && !string.IsNullOrWhiteSpace(result.JobId)
            ? await ObserveAsync(context, result.JobId, result.Model, result.Quantization, cancellationToken)
            : result;
    }

    public Task<LmStudioModelDownloadResult> ObserveAsync(
        ProviderOperationContext context, string jobId, string model, string quantization, CancellationToken cancellationToken) =>
        service.GetStatusAsync(context.BaseUrl, jobId, model, quantization, context.ApiToken, cancellationToken);
}
