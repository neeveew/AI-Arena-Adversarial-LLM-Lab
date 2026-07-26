namespace AIArena.Wpf.Models;

public sealed record ArenaViewSnapshot(
    string SessionId,
    string SnapshotPath,
    DateTime LastWriteTimeUtc,
    string MatchType,
    string ScenarioTopic,
    string ScenarioGlobal,
    bool TopicLocked,
    bool GlobalLocked,
    string ScenarioGeneratorStyle,
    string ScenarioGeneratorIntensity,
    string ScenarioGeneratorRolePack,
    string ScenarioGeneratorAbsurdity,
    string ScenarioGeneratorSeed,
    string PersonaGeneratorStyle,
    string PersonaGeneratorSeed,
    IReadOnlyList<GenerationHistoryItem> GenerationHistory,
    bool RivalryMatrixEnabled,
    IReadOnlyList<RivalryMatrixItem> RivalryMatrix,
    int TurnCount,
    int TurnIndex,
    string ProviderModel,
    string AlphaModel,
    string BetaModel,
    string GammaModel,
    string DeltaModel,
    string NarratorModel,
    string NarratorStatus,
    string NarratorPersona,
    string NarratorVoiceStyle,
    string NarratorAccentColor,
    bool NarratorLocked,
    string ProviderBaseUrl,
    string ProviderApiMode,
    string ProviderApiToken,
    int ProviderTimeout,
    double ProviderTemperature,
    int ProviderMaxOutputTokens,
    int ProviderContextLength,
    string ProviderReasoning,
    bool ProviderNativeStatefulChat,
    int ProviderNativeIdleTtlSeconds,
    int TranscriptWindow,
    int PrivateWindow,
    int NotesWindow,
    string Summary,
    string DecisionCard,
    double DecisionCardUpdatedAt,
    string ProviderLastError,
    bool InternetEnabled,
    bool ProviderOnline,
    IReadOnlyList<TranscriptMessage> Messages,
    IReadOnlyList<AgentState> Agents)
{
    /// <summary>
    /// Per-role generation overrides keyed by role id (alpha..delta, narrator).
    /// A role appears here only when its persisted config differs from the shared
    /// temperature or max output tokens; absent roles inherit shared values.
    /// </summary>
    public IReadOnlyDictionary<string, RoleGenerationOverride> RoleOverrides { get; init; } =
        new Dictionary<string, RoleGenerationOverride>();

    public int ProviderLastLatencyMs { get; init; }
}

public sealed record RoleGenerationOverride(double? Temperature, int? MaxOutputTokens);

public sealed record RivalryMatrixItem(
    string Source,
    string Target,
    string Stance);
