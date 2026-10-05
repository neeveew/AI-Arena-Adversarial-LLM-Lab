using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>Request-only fitting; saved preferences and prompt text are never rewritten.</summary>
internal static class ArenaRequestBudget
{
    internal const int MinimumUsefulOutputTokens = ModelProviderRequestBudget.MinimumUsefulOutputTokens;

    internal static int ContextWindow(ModelProviderConfig config)
        => ModelProviderRequestBudget.ContextWindow(config);

    internal static int TargetTokens(int contextWindow) =>
        ModelProviderRequestBudget.TargetTokens(contextWindow);

    internal static int FittedOutput(ModelProviderConfig config, int promptTokens)
        => ModelProviderRequestBudget.FittedOutput(config, promptTokens);

    internal static ArenaBudgetedPrompt FitUnchanged(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, ArenaHistoryBudgetReceipt? receipt = null)
        => ModelProviderRequestBudget.FitUnchanged(config, messages, receipt);

    internal static ModelProviderConfig Apply(ModelProviderConfig config, ArenaBudgetedPrompt prompt) =>
        ModelProviderRequestBudget.Apply(config, prompt);

    internal static async Task<ModelProviderConfig> ResolveAsync(ModelProviderConfig config,
        IModelRuntimeEvidenceResolver? resolver, CancellationToken cancellationToken)
    {
        if (resolver is null) return config;
        var evidence = await resolver.ResolveAsync(config, cancellationToken).ConfigureAwait(false);
        return Copy(config, runtimeEvidence: evidence, replaceRuntimeEvidence: true);
    }

    internal static ModelProviderConfig Copy(ModelProviderConfig config, int? maxOutputTokens = null,
        string? reasoning = null, ModelRuntimeEvidence? runtimeEvidence = null, bool replaceRuntimeEvidence = false) =>
        ModelProviderRequests.Copy(config, maxOutputTokens, reasoning,
            runtimeEvidence: runtimeEvidence, replaceRuntimeEvidence: replaceRuntimeEvidence);
}
