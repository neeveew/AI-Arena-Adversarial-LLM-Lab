namespace AIArena.Wpf.Services;

/// <summary>Outbound native-app operations; never edits provider routing or opens its database.</summary>
internal interface INativeServicesClient
{
    Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string idempotencyKey, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken);
}

internal sealed record NativeConnectionSnapshot(
    string HealthSummary,
    string ModelsSummary,
    string OperationsSummary,
    IReadOnlyList<string> Commands,
    bool CanCreateDiagnosticBundle);

internal sealed record NativeOperationSnapshot(
    string OperationId,
    string State,
    long Transition,
    string Summary,
    double? Progress = null,
    string ResultPath = "",
    bool ConfirmationRequired = false,
    string PlanDigest = "",
    string CapacitySampleId = "")
{
    public bool IsTerminal => State is "succeeded" or "partial" or "blocked" or "failed" or "canceled" or "interrupted";
    public bool CanCancel => !IsTerminal && State != "cancel_requested";
}
