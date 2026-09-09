namespace AIArena.Core.Models;

/// <summary>Fresh request-only server evidence; never replaces the user's saved model configuration.</summary>
public sealed record ModelRuntimeEvidence(
    int ContextWindow,
    bool CanDisableReasoning,
    string ModelInstanceId,
    DateTimeOffset CheckedAt,
    string Source,
    string ReducedReasoning = "");

public interface IModelRuntimeEvidenceResolver
{
    Task<ModelRuntimeEvidence?> ResolveAsync(ModelProviderConfig config, CancellationToken cancellationToken = default);
}
