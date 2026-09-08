namespace AIArena.Wpf.Services;

/// <summary>Process-local inference uses dedicated read/cancel endpoints, separate from durable operations.</summary>
internal interface INativeInferenceClient
{
    Task<NativeGenerationSnapshot> StartInferenceAsync(string deploymentId, string prompt, int maximumTokens, double temperature, string idempotencyKey, CancellationToken cancellationToken);
    Task<NativeGenerationSnapshot> ReadInferenceAsync(string operationId, CancellationToken cancellationToken);
    Task<NativeGenerationSnapshot> CancelInferenceAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken);
}

internal sealed record NativeGenerationSnapshot(string OperationId, string DeploymentId, string State, bool IsTerminal, long Sequence, string Output, string ErrorCode)
{
    public bool CanCancel => !IsTerminal && State != "cancel_requested";
}
