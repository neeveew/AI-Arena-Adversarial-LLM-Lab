using AIArena.Core.Models;

namespace AIArena.Core.Providers;

/// <summary>Fits request output against configured and observed context while preserving prompt text.</summary>
public static class ModelProviderRequestBudget
{
    public const int MinimumUsefulOutputTokens = 128;

    public static int ContextWindow(ModelProviderConfig config)
    {
        var configured = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        var active = Math.Max(0, config.RuntimeEvidence?.ContextWindow ?? 0);
        return active > 0 ? configured > 0 ? Math.Min(active, configured) : active : configured;
    }

    public static int TargetTokens(int contextWindow) =>
        Math.Max(1, (int)Math.Floor(contextWindow * (ArenaHistoryBudgetService.TargetPercent / 100d)));

    public static int FittedOutput(ModelProviderConfig config, int promptTokens)
    {
        var context = ContextWindow(config);
        var requested = Math.Max(1, config.MaxOutputTokens);
        if (context <= 0) return requested;
        var remaining = TargetTokens(context) - promptTokens;
        var minimum = Math.Min(requested, MinimumUsefulOutputTokens);
        return remaining < minimum ? 0 : Math.Min(requested, remaining);
    }

    public static ArenaBudgetedPrompt FitUnchanged(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, ArenaHistoryBudgetReceipt? receipt = null)
    {
        var context = ContextWindow(config);
        if (context <= 0) return new(messages, receipt, "");
        var estimated = ArenaHistoryBudgetService.EstimateTokens(messages);
        var output = FittedOutput(config, estimated);
        return output <= 0
            ? new(messages, receipt,
                $"The retained prompt needs about {estimated:N0} tokens and cannot fit the {context:N0}-token context with room for a public answer. Shorten the latest input or increase the loaded context; required conversation text was preserved.",
                ModelCompletionFailureKind.ContextLimitExceeded)
            : new ArenaBudgetedPrompt(messages, receipt, "") { OutputTokenLimit = output };
    }

    public static ModelProviderConfig Apply(ModelProviderConfig config, ArenaBudgetedPrompt prompt) =>
        prompt.OutputTokenLimit > 0 && prompt.OutputTokenLimit != config.MaxOutputTokens
            ? ModelProviderRequests.Copy(config, maxOutputTokens: prompt.OutputTokenLimit) : config;
}
