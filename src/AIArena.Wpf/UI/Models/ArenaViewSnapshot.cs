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
    /// Opaque identity for the current persisted session incarnation. Draft
    /// scopes must fail closed when this value is unavailable and must never use
    /// the reusable display name as an identity substitute.
    /// </summary>
    public string SessionInstanceId { get; init; } = "";

    /// <summary>
    /// When enabled, participant turns bypass Match Setup behavior and use the
    /// session's attributed public group conversation. Narration is unavailable.
    /// </summary>
    public bool FactoryMode { get; init; }

    /// <summary>
    /// When true, participant and Narrator roles without an explicit model use
    /// the shared provider model. The shared model remains available to Agent
    /// Workspace and provider diagnostics even when this policy is disabled.
    /// </summary>
    public bool DefaultForUnassignedAgentsEnabled { get; init; } = true;

    /// <summary>
    /// Explicit role-model routes keyed by durable role id. An inherited role
    /// is absent even when its effective model equals the shared model.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExplicitRoleModels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the durable Factory conversation root still resolves to its
    /// original public Operator message.
    /// </summary>
    public bool HasFactoryConversationRoot { get; init; }

    /// <summary>
    /// True after a Factory conversation has been anchored. This remains true
    /// when its root message is missing so readiness cannot silently promote a
    /// later Operator message to a replacement root.
    /// </summary>
    public bool FactoryConversationRootAssigned { get; init; }

    /// <summary>
    /// Eligible public entries currently associated with the Factory group.
    /// This count is content-free and safe to surface in readiness summaries.
    /// </summary>
    public int FactoryConversationEntryCount { get; init; }

    /// <summary>
    /// Older whole entries omitted by the fixed 50-entry Factory context window.
    /// </summary>
    public int FactoryConversationOmittedCount { get; init; }

    /// <summary>
    /// Durable terminal state set by an explicit End Match recovery action.
    /// A reset or fork is required before any further Arena turn can run.
    /// </summary>
    public bool MatchEnded { get; init; }

    public string MatchEndReason { get; init; } = "";

    /// <summary>
    /// Authoritative Core evidence that a context-limit failure still requires
    /// an explicit recovery action before Arena turns may continue.
    /// </summary>
    public bool HasUnresolvedContextFailure { get; init; }

    /// <summary>
    /// Per-role generation overrides keyed by role id (alpha..delta, narrator).
    /// A role appears here only when its persisted config differs from the shared
    /// temperature or max output tokens; absent roles inherit shared values.
    /// </summary>
    public IReadOnlyDictionary<string, RoleGenerationOverride> RoleOverrides { get; init; } =
        new Dictionary<string, RoleGenerationOverride>();

    public int ProviderLastLatencyMs { get; init; }

    /// <summary>
    /// Canonical, privacy-safe per-model runtime settings. Keys are opaque
    /// connection-and-model identities; provider residency evidence is never
    /// persisted in this projection.
    /// </summary>
    public IReadOnlyList<ProviderModelRuntimeSettingsView> ModelSettings { get; init; } = [];

    public int ProviderConfiguredContextWindow { get; init; }
    public string ProviderHistoryPolicy { get; init; } = "strict";
    public string ProviderResponseTone { get; init; } = "default";
    public string ProviderCustomTone { get; init; } = "";
}

public sealed record RoleGenerationOverride(double? Temperature, int? MaxOutputTokens);

public sealed record ProviderModelRuntimeSettingsView(
    string ModelIdentity,
    int ConfiguredContextWindow,
    string HistoryPolicy,
    string ResponseTone,
    string CustomTone,
    string Model = "",
    bool PendingApply = false);

public sealed record RivalryMatrixItem(
    string Source,
    string Target,
    string Stance);
