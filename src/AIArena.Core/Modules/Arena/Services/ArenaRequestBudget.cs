using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>Request-only fitting; saved preferences and prompt text are never rewritten.</summary>
internal static class ArenaRequestBudget
{
    internal const int MinimumUsefulOutputTokens = 128;

    internal static int ContextWindow(ModelProviderConfig config)
    {
        var configured = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        var active = Math.Max(0, config.RuntimeEvidence?.ContextWindow ?? 0);
        return active > 0 ? configured > 0 ? Math.Min(active, configured) : active : configured;
    }

    internal static int TargetTokens(int contextWindow) =>
        Math.Max(1, (int)Math.Floor(contextWindow * (ArenaHistoryBudgetService.TargetPercent / 100d)));

    internal static int FittedOutput(ModelProviderConfig config, int promptTokens)
    {
        var context = ContextWindow(config);
        var requested = Math.Max(1, config.MaxOutputTokens);
        if (context <= 0) return requested;
        var remaining = TargetTokens(context) - promptTokens;
        var minimum = Math.Min(requested, MinimumUsefulOutputTokens);
        return remaining < minimum ? 0 : Math.Min(requested, remaining);
    }

    internal static ArenaBudgetedPrompt FitUnchanged(ModelProviderConfig config,
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

    internal static ModelProviderConfig Apply(ModelProviderConfig config, ArenaBudgetedPrompt prompt) =>
        prompt.OutputTokenLimit > 0 && prompt.OutputTokenLimit != config.MaxOutputTokens
            ? Copy(config, maxOutputTokens: prompt.OutputTokenLimit)
            : config;

    internal static async Task<ModelProviderConfig> ResolveAsync(ModelProviderConfig config,
        IModelRuntimeEvidenceResolver? resolver, CancellationToken cancellationToken)
    {
        if (resolver is null) return config;
        var evidence = await resolver.ResolveAsync(config, cancellationToken).ConfigureAwait(false);
        return Copy(config, runtimeEvidence: evidence, replaceRuntimeEvidence: true);
    }

    internal static ModelProviderConfig Copy(ModelProviderConfig config, int? maxOutputTokens = null,
        string? reasoning = null, ModelRuntimeEvidence? runtimeEvidence = null, bool replaceRuntimeEvidence = false) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = maxOutputTokens ?? config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = reasoning ?? config.Reasoning,
        RuntimeEvidence = replaceRuntimeEvidence ? runtimeEvidence : config.RuntimeEvidence,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        PreviousResponseId = config.PreviousResponseId,
        PreserveNativeInputWhitespace = config.PreserveNativeInputWhitespace,
        RequestInspectionContext = config.RequestInspectionContext,
        LastError = config.LastError,
        LastLatencyMs = config.LastLatencyMs,
        LastTestOk = config.LastTestOk,
        Extra = config.Extra
    };
}
