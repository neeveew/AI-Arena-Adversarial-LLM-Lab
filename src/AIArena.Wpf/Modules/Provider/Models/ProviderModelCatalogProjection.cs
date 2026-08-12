namespace AIArena.Wpf.Models;

internal enum ProviderCatalogEvidenceState
{
    Ready,
    Partial,
    Unavailable
}

internal enum ProviderModelLoadState
{
    Loaded,
    NotLoaded,
    Unavailable
}

internal sealed record ProviderModelCatalogItem(
    string Id,
    string DisplayName,
    ProviderModelLoadState LoadState,
    bool CanLoad,
    bool CanUnload,
    string Publisher,
    string Quantization,
    int? ContextLength,
    long? SizeBytes,
    string CapabilitySummary,
    IReadOnlyList<string> Aliases,
    bool IsConfiguredOnly = false,
    bool IsResidencyStale = false);

internal sealed record ProviderModelCatalogSnapshot(
    long Generation,
    string SessionId,
    string ProviderFingerprint,
    ProviderCatalogEvidenceState CatalogEvidence,
    ProviderCatalogEvidenceState ResidencyEvidence,
    IReadOnlyList<ProviderModelCatalogItem> LoadedModels,
    IReadOnlyList<ProviderModelCatalogItem> AvailableModels,
    string ConfiguredModel,
    bool ConfiguredModelMissing,
    int OmittedModelCount,
    string Status,
    DateTimeOffset CheckedAt)
{
    public IReadOnlyList<ProviderModelCatalogItem> Models =>
        LoadedModels.Concat(AvailableModels).ToArray();
}

internal sealed record ProviderModelCatalogRefreshLease(
    long Generation,
    string SessionId,
    string ProviderFingerprint);

internal sealed record ProviderModelAssignmentTarget(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsNarrator,
    bool Assigned,
    bool InheritsDefault,
    string AssignmentFingerprint);

internal sealed record ProviderModelAssignmentProjection(
    string SessionId,
    string ProviderFingerprint,
    long PersistenceRevision,
    string Model,
    IReadOnlyList<ProviderModelAssignmentTarget> Targets)
{
    public static ProviderModelAssignmentProjection Empty(string model = "") =>
        new("", "", 0, model, []);
}

internal sealed record ProviderModelAssignmentRequest(
    string TargetId,
    string Model,
    bool Assigned,
    string ExpectedProviderFingerprint,
    string ExpectedAssignmentFingerprint,
    IReadOnlyList<string>? EquivalentModelIds = null);

internal sealed record ProviderModelAssignmentControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    ProviderModelAssignmentProjection Assignment,
    IReadOnlyList<string> ChangedFields);
