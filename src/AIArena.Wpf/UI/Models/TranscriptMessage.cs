namespace AIArena.Wpf.Models;

public sealed record TranscriptMessage(
    int Turn,
    string Speaker,
    string SpeakerId,
    double CreatedAt,
    string Model,
    int LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string Status,
    string VoiceStyle,
    bool Pinned,
    string Kind,
    string Text,
    string Reasoning,
    string InternetRequester,
    string InternetTool,
    string InternetQuery,
    string InternetUrl,
    string InternetReason,
    string InternetSummary,
    string InternetCheckedAt,
    bool InternetCached,
    IReadOnlyList<string> InternetSources,
    double TokensPerSecond = 0,
    int TimeToFirstTokenMs = 0,
    string ProviderResponseId = "",
    int ModelLoadTimeMs = 0)
{
    public string CompletionFailureKind { get; init; } = "none";
    public string CompletionStopReason { get; init; } = "unknown";
    public int? ProviderStatusCode { get; init; }
    public string ProviderErrorCode { get; init; } = "";
    public ArenaHistoryBudgetReceiptView? HistoryBudgetReceipt { get; init; }
}

public sealed record ArenaHistoryBudgetReceiptView(
    string Contract,
    string HistoryPolicy,
    int ConfiguredContextWindow,
    int TargetPercent,
    int InputTokenBudget,
    int OutputTokenReserve,
    int EstimatedPromptTokens,
    int EligibleEntryCount,
    int IncludedEntryCount,
    int OmittedEntryCount,
    string ContextFingerprint,
    int BeforeTurn,
    string TokenEvidence);
