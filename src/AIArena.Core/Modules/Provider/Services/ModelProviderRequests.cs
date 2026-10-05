using AIArena.Core.Models;

namespace AIArena.Core.Providers;

/// <summary>Creates request copies without losing the saved route or request options.</summary>
public static class ModelProviderRequests
{
    public static ModelProviderConfig Copy(ModelProviderConfig config, int? maxOutputTokens = null,
        string? reasoning = null, string? model = null, ModelRuntimeSettings? settings = null,
        ModelRuntimeEvidence? runtimeEvidence = null, bool replaceRuntimeEvidence = false) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = model ?? config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = maxOutputTokens ?? config.MaxOutputTokens,
        // Both fields describe the same resolved model context. A saved zero
        // must not revive the legacy context override.
        ContextLength = settings?.ConfiguredContextWindow ?? config.ContextLength,
        ConfiguredContextWindow = settings?.ConfiguredContextWindow ?? config.ConfiguredContextWindow,
        HistoryPolicy = settings?.HistoryPolicy ?? config.HistoryPolicy,
        ResponseTone = settings?.ResponseTone ?? config.ResponseTone,
        CustomTone = settings?.CustomTone ?? config.CustomTone,
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
        Extra = config.Extra is null ? null : new(config.Extra, config.Extra.Comparer)
    };
}
