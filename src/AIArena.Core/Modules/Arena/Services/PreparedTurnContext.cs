using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>
/// Immutable, completion-scoped projection of the transcript and scoped memory
/// needed to build one logical provider completion. The owning turn runner never
/// retains this object after the turn, so expiry and causal boundaries are
/// evaluated once and shared by primary, fallback, repair, and tool-continuation
/// requests without becoming a cross-turn cache.
/// </summary>
internal sealed class PreparedTurnContext
{
    private readonly ArenaSnapshot _snapshot;
    private readonly DialogueMessage[] _promptTranscript;
    private readonly DialogueMessage[] _budgetTranscript;
    private readonly DialogueMessage[] _inspectionTranscript;
    private readonly StructuredMemoryEntry[] _scopedMemory;
    private readonly string[] _scopedMemoryPromptLines;
    private readonly Dictionary<string, NativeContinuationAnchor?> _nativeAnchors;
    private readonly Dictionary<int, DialogueMessage[]> _promptTranscriptAfter = new();
    private readonly Dictionary<int, DialogueMessage[]> _budgetTranscriptAfter = new();
    private readonly int _turnCount;
    private readonly int _messageCount;
    private readonly bool _factoryMode;
    private readonly DialogueAgent[] _activeAgents;
    private readonly DialogueAgent[] _allAgents;
    private readonly DialogueAgent? _selectedAgent;
    private readonly RivalryLink[] _rivalryLinks;

    private PreparedTurnContext(
        ArenaSnapshot snapshot,
        int? beforeTurn,
        DateTimeOffset preparedAtUtc,
        DialogueMessage[] promptTranscript,
        DialogueMessage[] budgetTranscript,
        DialogueMessage[] inspectionTranscript,
        StructuredMemoryEntry[] scopedMemory,
        string[] scopedMemoryPromptLines,
        Dictionary<string, NativeContinuationAnchor?> nativeAnchors,
        DialogueAgent[] allAgents,
        DialogueAgent[] activeAgents,
        DialogueAgent? selectedAgent,
        RivalryLink[] rivalryLinks,
        int messagesVisited,
        int memorySelectionPasses,
        int nativeConfigurationsPrepared)
    {
        _snapshot = snapshot;
        BeforeTurn = beforeTurn;
        PreparedAtUtc = preparedAtUtc;
        PersistenceRevision = snapshot.PersistenceRevision;
        _turnCount = snapshot.Engine.TurnCount;
        _messageCount = snapshot.Engine.Messages.Count;
        _factoryMode = snapshot.Engine.FactoryMode;
        _promptTranscript = promptTranscript;
        _budgetTranscript = budgetTranscript;
        _inspectionTranscript = inspectionTranscript;
        _scopedMemory = scopedMemory;
        _scopedMemoryPromptLines = scopedMemoryPromptLines;
        _nativeAnchors = nativeAnchors;
        _allAgents = allAgents;
        _activeAgents = activeAgents;
        _selectedAgent = selectedAgent;
        _rivalryLinks = rivalryLinks;
        Topic = snapshot.Engine.Steering.Topic;
        GlobalInstruction = snapshot.Engine.Steering.Global;
        Internet = new InternetSettings
        {
            UseInternet = snapshot.Engine.Internet.UseInternet,
            MaxResults = snapshot.Engine.Internet.MaxResults,
            SourceFreshnessMinutes = snapshot.Engine.Internet.SourceFreshnessMinutes
        };
        TranscriptWindow = snapshot.Engine.TranscriptWindow;
        NotesWindow = snapshot.Engine.NotesWindow;
        RivalryEnabled = snapshot.Engine.RivalryMatrix.Enabled;
        Diagnostics = new PreparedTurnContextDiagnostics(
            PersistenceRevision,
            PreparedAtUtc,
            messagesVisited,
            TranscriptSourcePasses: 1,
            MemoryNormalizationPasses: 1,
            MemorySelectionPasses: memorySelectionPasses,
            NativeConfigurationsPrepared: nativeConfigurationsPrepared);
    }

    internal int? BeforeTurn { get; }

    internal DateTimeOffset PreparedAtUtc { get; }

    internal long PersistenceRevision { get; }

    internal PreparedTurnContextDiagnostics Diagnostics { get; }

    internal bool FactoryMode => _factoryMode;

    internal int TurnCount => _turnCount;

    internal string Topic { get; }

    internal string GlobalInstruction { get; }

    internal InternetSettings Internet { get; }

    internal int TranscriptWindow { get; }

    internal int NotesWindow { get; }

    internal bool RivalryEnabled { get; }

    internal IReadOnlyList<DialogueAgent> ActiveAgents => _activeAgents;

    internal IReadOnlyList<DialogueAgent> AllAgents => _allAgents;

    internal DialogueAgent? SelectedAgent => _selectedAgent;

    internal IReadOnlyList<RivalryLink> RivalryLinks => _rivalryLinks;

    internal ModelProviderConfig PrimaryConfig { get; private init; } = new();

    internal ModelProviderConfig? FallbackConfig { get; private init; }

    internal ArenaSnapshot CreateInternetExecutionSnapshot() => new()
    {
        Engine = new EngineSnapshot
        {
            Internet = new InternetSettings
            {
                UseInternet = Internet.UseInternet,
                MaxResults = Internet.MaxResults,
                SourceFreshnessMinutes = Internet.SourceFreshnessMinutes
            }
        }
    };

    internal IReadOnlyList<StructuredMemoryEntry> ScopedMemory => _scopedMemory;

    internal IReadOnlyList<string> ScopedMemoryPromptLines => _scopedMemoryPromptLines;

    internal static PreparedTurnContext Create(
        ArenaSnapshot snapshot,
        OneTurnPlan plan,
        int? beforeTurn,
        DateTimeOffset preparedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (!plan.Ok || plan.Config is null)
        {
            throw new ArgumentException("A successful routed turn plan is required.", nameof(plan));
        }

        var normalizedPreparedAtUtc = preparedAtUtc.ToUniversalTime();
        var causalBeforeTurn = beforeTurn ?? checked(snapshot.Engine.TurnCount + 1);
        var promptTranscript = new List<DialogueMessage>(snapshot.Engine.Messages.Count);
        var budgetTranscript = new List<DialogueMessage>(snapshot.Engine.Messages.Count);
        var inspectionTranscript = new List<DialogueMessage>(snapshot.Engine.Messages.Count);
        var nativeCandidates = new List<DialogueMessage>();
        var messagesVisited = 0;

        // One source pass feeds every downstream projection. Sorting operates on
        // these bounded local lists and never re-enumerates the mutable snapshot.
        foreach (var message in snapshot.Engine.Messages)
        {
            messagesVisited++;
            if ((messagesVisited & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var beforePromptBoundary = beforeTurn is null || message.Turn < beforeTurn.Value;
            var promptEligible = beforePromptBoundary
                && message.Kind is "message" or "internet" or ""
                && message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Text);
            if (promptEligible)
            {
                promptTranscript.Add(message);
                if (message.Turn < causalBeforeTurn)
                {
                    budgetTranscript.Add(message);
                }
            }

            if (beforePromptBoundary && message.Kind is "message" or "internet" or "")
            {
                inspectionTranscript.Add(message);
            }

            if (beforePromptBoundary
                && message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                && message.Kind is "message" or ""
                && message.SpeakerId.Equals(plan.AgentId, StringComparison.OrdinalIgnoreCase)
                && !IsFactoryModeMessage(message))
            {
                nativeCandidates.Add(message);
            }
        }

        StructuredMemoryService.NormalizeSnapshot(snapshot, normalizedPreparedAtUtc);
        cancellationToken.ThrowIfCancellationRequested();
        var frozenMessages = new Dictionary<DialogueMessage, DialogueMessage>(ReferenceEqualityComparer.Instance);
        DialogueMessage FreezeOnce(DialogueMessage message)
        {
            if (!frozenMessages.TryGetValue(message, out var frozen))
            {
                frozen = FreezeMessage(message);
                frozenMessages[message] = frozen;
            }

            return frozen;
        }

        var orderedPrompt = promptTranscript
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .Select(FreezeOnce)
            .ToArray();
        var orderedBudget = budgetTranscript
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .Select(FreezeOnce)
            .ToArray();
        // OrderBy is deliberately stable here; the existing inspection contract
        // orders by turn only and preserves source order inside equal-turn rows.
        var orderedInspection = inspectionTranscript
            .OrderBy(message => message.Turn)
            .Select(FreezeOnce)
            .ToArray();
        var orderedNativeCandidates = nativeCandidates
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Select(FreezeOnce)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var activeAgentSources = snapshot.Engine.Agents
            .Where(candidate => candidate.Active)
            .ToArray();
        // Preserve BuildPrompt's legacy exact-id lookup. Inspection historically
        // used an ignore-case lookup, so its aggregate counts stay independent.
        var promptAgent = activeAgentSources.FirstOrDefault(candidate =>
            candidate.Id == plan.AgentId);
        var inspectionAgent = snapshot.Engine.Agents.FirstOrDefault(candidate =>
            candidate.Id.Equals(plan.AgentId, StringComparison.OrdinalIgnoreCase));
        var memorySelectionPasses = 0;
        var scopedMemory = inspectionAgent is null
            ? []
            : StructuredMemoryService.SelectNormalizedForPrompt(
                snapshot,
                inspectionAgent,
                normalizedPreparedAtUtc,
                beforeTurn)
                .Select(FreezeMemory)
                .ToArray();
        if (inspectionAgent is not null)
        {
            memorySelectionPasses++;
        }

        var inspectionMemoryLines = scopedMemory
            .Select(StructuredMemoryService.FormatPromptLine)
            .ToArray();
        var memoryLines = ReferenceEquals(promptAgent, inspectionAgent)
            ? inspectionMemoryLines
            : promptAgent is null
                ? []
                : StructuredMemoryService.SelectNormalizedForPrompt(
                    snapshot,
                    promptAgent,
                    normalizedPreparedAtUtc,
                    beforeTurn)
                    .Select(StructuredMemoryService.FormatPromptLine)
                    .ToArray();
        if (promptAgent is not null && !ReferenceEquals(promptAgent, inspectionAgent))
        {
            memorySelectionPasses++;
        }

        var allAgents = snapshot.Engine.Agents
            .Select(FreezeAgent)
            .ToArray();
        var activeAgents = allAgents
            .Where(candidate => candidate.Active)
            .ToArray();
        var selectedAgent = activeAgents.FirstOrDefault(candidate =>
            candidate.Id == plan.AgentId);
        var rivalryLinks = snapshot.Engine.RivalryMatrix.Links
            .Select(link => new RivalryLink
            {
                Source = link.Source,
                Target = link.Target,
                Stance = link.Stance
            })
            .ToArray();

        var frozenPrimaryConfig = FreezeConfig(plan.Config);
        var frozenFallbackConfig = plan.FallbackConfig is null
            ? null
            : FreezeConfig(plan.FallbackConfig);
        var nativeAnchors = new Dictionary<string, NativeContinuationAnchor?>(StringComparer.Ordinal);
        var configs = new[] { frozenPrimaryConfig, frozenFallbackConfig }
            .Where(config => config is not null)
            .Cast<ModelProviderConfig>()
            .GroupBy(NativeConfigKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var config in configs)
        {
            nativeAnchors[NativeConfigKey(config)] = FindNativeAnchor(config, orderedNativeCandidates);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return new PreparedTurnContext(
            snapshot,
            beforeTurn,
            normalizedPreparedAtUtc,
            orderedPrompt,
            orderedBudget,
            orderedInspection,
            scopedMemory,
            memoryLines,
            nativeAnchors,
            allAgents,
            activeAgents,
            selectedAgent,
            rivalryLinks,
            messagesVisited,
            memorySelectionPasses,
            configs.Length)
        {
            PrimaryConfig = frozenPrimaryConfig,
            FallbackConfig = frozenFallbackConfig
        };
    }

    internal void EnsureScope(ArenaSnapshot snapshot, int? beforeTurn)
    {
        if (!ReferenceEquals(snapshot, _snapshot)
            || beforeTurn != BeforeTurn
            || snapshot.PersistenceRevision != PersistenceRevision
            || snapshot.Engine.TurnCount != _turnCount
            || snapshot.Engine.Messages.Count != _messageCount
            || snapshot.Engine.FactoryMode != _factoryMode)
        {
            throw new InvalidOperationException(
                "The prepared turn context is stale or belongs to a different causal completion.");
        }
    }

    internal IReadOnlyList<DialogueMessage> PromptTranscriptAfter(int? transcriptAfterTurn)
    {
        if (transcriptAfterTurn is null)
        {
            return _promptTranscript;
        }

        if (!_promptTranscriptAfter.TryGetValue(transcriptAfterTurn.Value, out var prepared))
        {
            prepared = SliceAfter(_promptTranscript, transcriptAfterTurn.Value);
            _promptTranscriptAfter[transcriptAfterTurn.Value] = prepared;
        }

        return prepared;
    }

    internal IReadOnlyList<DialogueMessage> BudgetTranscriptAfter(int? transcriptAfterTurn)
    {
        if (transcriptAfterTurn is null)
        {
            return _budgetTranscript;
        }

        if (!_budgetTranscriptAfter.TryGetValue(transcriptAfterTurn.Value, out var prepared))
        {
            prepared = SliceAfter(_budgetTranscript, transcriptAfterTurn.Value);
            _budgetTranscriptAfter[transcriptAfterTurn.Value] = prepared;
        }

        return prepared;
    }

    internal IReadOnlyList<DialogueMessage> InspectionTranscript => _inspectionTranscript;

    internal NativeContinuationAnchor? NativeAnchor(ModelProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _nativeAnchors.TryGetValue(NativeConfigKey(config), out var anchor)
            ? anchor
            : null;
    }

    private static NativeContinuationAnchor? FindNativeAnchor(
        ModelProviderConfig config,
        IReadOnlyList<DialogueMessage> candidates)
    {
        if (!config.NativeStatefulChat
            || !ModelProviderApiModes.Normalize(config.ApiMode)
                .Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var message in candidates)
        {
            if (!string.IsNullOrWhiteSpace(config.Model)
                && !string.IsNullOrWhiteSpace(message.Model.Model)
                && !message.Model.Model.Equals(config.Model.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.Metadata.TryGetValue("provider_response_id", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && ModelProviderClient.NativeResponseId(value.GetString() ?? "") is { Length: > 0 } responseId)
            {
                return new NativeContinuationAnchor(message.Turn, responseId);
            }
        }

        return null;
    }

    private static DialogueMessage[] SliceAfter(DialogueMessage[] ordered, int turn)
    {
        var low = 0;
        var high = ordered.Length;
        while (low < high)
        {
            var midpoint = low + (high - low) / 2;
            if (ordered[midpoint].Turn <= turn)
            {
                low = midpoint + 1;
            }
            else
            {
                high = midpoint;
            }
        }

        return low == 0 ? ordered : ordered[low..];
    }

    private static DialogueMessage FreezeMessage(DialogueMessage message) => new()
    {
        MessageId = message.MessageId,
        Turn = message.Turn,
        Speaker = message.Speaker,
        SpeakerId = message.SpeakerId,
        Text = message.Text,
        Status = message.Status,
        Pinned = message.Pinned,
        Kind = message.Kind,
        CreatedAt = message.CreatedAt,
        Model = new ModelMetadata
        {
            Model = message.Model.Model,
            LatencyMs = message.Model.LatencyMs,
            PromptTokens = message.Model.PromptTokens,
            CompletionTokens = message.Model.CompletionTokens,
            TotalTokens = message.Model.TotalTokens,
            TokensPerSecond = message.Model.TokensPerSecond,
            TimeToFirstTokenMs = message.Model.TimeToFirstTokenMs,
            ModelLoadTimeMs = message.Model.ModelLoadTimeMs,
            Extra = message.Model.Extra is null
                ? null
                : new Dictionary<string, System.Text.Json.JsonElement>(message.Model.Extra, StringComparer.Ordinal)
        },
        Metadata = new Dictionary<string, System.Text.Json.JsonElement>(message.Metadata, StringComparer.Ordinal),
        Extra = message.Extra is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>(message.Extra, StringComparer.Ordinal)
    };

    private static StructuredMemoryEntry FreezeMemory(StructuredMemoryEntry entry) => new()
    {
        MemoryId = entry.MemoryId,
        Text = entry.Text,
        Origin = entry.Origin,
        Visibility = entry.Visibility,
        CreatedAt = entry.CreatedAt,
        RevisedAt = entry.RevisedAt,
        ExpiresAt = entry.ExpiresAt,
        SourceMessageId = entry.SourceMessageId,
        SourceTurn = entry.SourceTurn,
        SourceProvenanceAmbiguous = entry.SourceProvenanceAmbiguous,
        BranchId = entry.BranchId,
        SupersedesMemoryId = entry.SupersedesMemoryId,
        CorrectionOfMemoryId = entry.CorrectionOfMemoryId,
        IsCorrection = entry.IsCorrection
    };

    private static DialogueAgent FreezeAgent(DialogueAgent agent) => new()
    {
        Id = agent.Id,
        Name = agent.Name,
        Persona = agent.Persona,
        VoiceStyle = agent.VoiceStyle,
        PressureProfile = agent.PressureProfile,
        AccentColor = agent.AccentColor,
        Active = agent.Active,
        Status = agent.Status
    };

    private static ModelProviderConfig FreezeConfig(ModelProviderConfig config) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        PreviousResponseId = config.PreviousResponseId,
        PreserveNativeInputWhitespace = config.PreserveNativeInputWhitespace,
        LastError = config.LastError,
        LastLatencyMs = config.LastLatencyMs,
        LastTestOk = config.LastTestOk,
        Extra = config.Extra is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>(config.Extra, StringComparer.Ordinal)
    };

    private static string NativeConfigKey(ModelProviderConfig config) => string.Join(
        "\n",
        ModelProviderApiModes.Normalize(config.ApiMode).ToLowerInvariant(),
        config.NativeStatefulChat ? "stateful" : "stateless",
        config.Model.Trim().ToLowerInvariant());

    private static bool IsFactoryModeMessage(DialogueMessage message) =>
        message.Metadata.TryGetValue("prompt_mode", out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.String
        && (value.GetString() ?? "").Equals("factory", StringComparison.OrdinalIgnoreCase);
}

internal sealed record NativeContinuationAnchor(int Turn, string ResponseId);

internal sealed record PreparedTurnContextDiagnostics(
    long PersistenceRevision,
    DateTimeOffset PreparedAtUtc,
    int MessagesVisited,
    int TranscriptSourcePasses,
    int MemoryNormalizationPasses,
    int MemorySelectionPasses,
    int NativeConfigurationsPrepared);
