namespace AIArena.Wpf.Services;

internal interface INativeModelClient
{
    Task<NativeModelCatalog> GetModelsAsync(CancellationToken cancellationToken);
    Task<NativeModelPreflight> PreflightModelAsync(string deploymentId, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> LoadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> UnloadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken);
    Task<NativeOperationSnapshot> ConfirmModelLoadAsync(string operationId, string planDigest, string capacitySampleId, string idempotencyKey, CancellationToken cancellationToken);
}

internal sealed record NativeModelCatalog(IReadOnlyList<NativeModelOption> Items, string DefaultDeploymentId, bool Truncated = false);
internal sealed record NativeModelOption(
    string DeploymentId, string State, bool Resident, bool Serviceable, string Health, string Target,
    string OperationId, string Phase, double? Progress, bool NeedsAttention, bool Historical,
    NativeModelAdmission? Admission = null)
{
    public string DisplayName => $"{DeploymentId} ({State.Replace('_', ' ')}; {Target})";
}
internal sealed record NativeModelAdmission(string Status, string OperationId, string PlanDigest, string CapacitySampleId);
internal sealed record NativeModelPreflight(string DeploymentId, string Summary, bool ConfirmationRequired, string PlanDigest);
