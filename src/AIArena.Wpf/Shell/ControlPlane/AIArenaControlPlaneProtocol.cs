using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArena.Wpf;

internal static class AIArenaControlPlaneProtocol
{
    public const string PipeName = "ai-arena-wpf-control";
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxConcurrentClients = 8;
    public const int MaxEventQueueItems = 256;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    internal static bool TryParseRequest(string json, out AIArenaControlRequest request, out string error)
    {
        request = new AIArenaControlRequest("", "", null);
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Request body is empty.";
            return false;
        }

        if (json.Length > MaxRequestBytes)
        {
            error = "Request body is too large.";
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<AIArenaControlRequest>(json, JsonOptions)
                ?? new AIArenaControlRequest("", "", null);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            error = "Command is required.";
            return false;
        }

        request = request with
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim(),
            Command = NormalizeCommand(request.Command),
            Token = request.Token.Trim()
        };
        return true;
    }

    internal static string NormalizeCommand(string command)
    {
        return (command ?? "").Trim().Replace('_', '.').ToLowerInvariant();
    }

    internal static string DefaultTokenPath()
    {
        var user = string.Join(
            "_",
            Environment.UserName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(user))
        {
            user = "user";
        }

        return Path.Combine(Path.GetTempPath(), $"ai-arena-wpf-control-{user}.token");
    }
}

internal static class AIArenaControlCommands
{
    public const string Capabilities = "capabilities";
    public const string Status = "status";
    public const string Snapshot = "snapshot";
    public const string EventsWatch = "events.watch";
    public const string AppScreenshot = "app.screenshot";
    public const string NavigationSelect = "navigation.select";
    public const string NavigationThemeSet = "navigation.theme.set";
    public const string NavigationProviderFocus = "navigation.provider.focus";
    public const string NavigationRailSet = "navigation.rail.set";
    public const string ViewPresetSet = "view.preset.set";
    public const string ShellPaletteList = "shell.palette.list";
    public const string ShellPaletteRun = "shell.palette.run";
    public const string ShellInputKey = "shell.input.key";
    public const string ShellInputType = "shell.input.type";
    public const string MatchSetupState = "match.setup.state";
    public const string MatchSetupOpen = "match.setup.open";
    public const string MatchSetupClose = "match.setup.close";
    public const string MatchSetupExport = "match.setup.export";
    public const string MatchSetupImport = "match.setup.import";
    public const string MatchRosterSet = "match.roster.set";
    public const string MatchMatrixState = "match.matrix.state";
    public const string MatchMatrixSet = "match.matrix.set";
    public const string MatchGenerationState = "match.generation.state";
    public const string MatchGenerateRandom = "match.generate.random";
    public const string MatchGenerateAi = "match.generate.ai";
    public const string MatchGenerateCurrent = "match.generate.current";
    public const string MatchGenerateWild = "match.generate.wild";
    public const string MatchReplay = "match.replay";
    public const string MatchReplayNew = "match.replay.new";
    public const string SettingsState = "settings.state";
    public const string SettingsOpen = "settings.open";
    public const string SettingsClose = "settings.close";
    public const string SettingsSearch = "settings.search";
    public const string SettingsUpdate = "settings.update";
    public const string SessionState = "session.state";
    public const string SessionSelect = "session.select";
    public const string SessionCreate = "session.create";
    public const string SessionFork = "session.fork";
    public const string SessionCheckpointCreate = "session.checkpoint.create";
    public const string SessionCheckpointRestore = "session.checkpoint.restore";
    public const string AgentState = "agent.state";
    public const string AgentCommandState = "agent.command.state";
    public const string AgentWorkBrief = "agent.work.brief";
    public const string AgentBuildEvidence = "agent.build.evidence";
    public const string AgentOutputs = "agent.outputs";
    public const string AgentRunbookState = "agent.runbook.state";
    public const string AgentRunbookResume = "agent.runbook.resume";
    public const string AgentRunbookCheckpoint = "agent.runbook.checkpoint";
    public const string AgentSend = "agent.send";
    public const string AgentApprove = "agent.approve";
    public const string AgentReject = "agent.reject";
    public const string AgentStop = "agent.stop";
    public const string AgentStageNext = "agent.stage.next";
    public const string AgentStageVerify = "agent.stage.verify";
    public const string AgentStageArtifact = "agent.stage.artifact";
    public const string AgentCommandStage = "agent.command.stage";
    public const string AgentWorkspaceSet = "agent.workspace.set";
    public const string ProviderState = "provider.state";
    public const string ProviderConfigSet = "provider.config.set";
    public const string ProviderModelSet = "provider.model.set";
    public const string ProviderTest = "provider.test";
    public const string ProviderModelsRefresh = "provider.models.refresh";
    public const string ArenaStart = "arena.start";
    public const string ArenaStop = "arena.stop";
    public const string ArenaTurn = "arena.turn";
    public const string ArenaNarrate = "arena.narrate";
    public const string ArenaReset = "arena.reset";
    public const string ArenaOperatorSend = "arena.operator.send";
    public const string InternetState = "internet.state";
    public const string InternetSet = "internet.set";
    public const string InternetTest = "internet.test";
    public const string CollaborateState = "collaborate.state";
    public const string CollaborateReview = "collaborate.review";
    public const string CollaborateSend = "collaborate.send";
    public const string CollaborateStop = "collaborate.stop";
    public const string CollaborateFork = "collaborate.fork";
    public const string CollaborateRepeat = "collaborate.repeat";
    public const string ExportTranscript = "export.transcript";
    public const string ExportSession = "export.session";
    public const string ExportReceipts = "export.receipts";

    public static bool IsKnown(string command)
    {
        return AIArenaControlCapabilityCatalog.IsKnown(command);
    }
}

internal sealed record AIArenaControlRequest(
    string Id,
    string Command,
    Dictionary<string, JsonElement>? Args,
    string Token = "");

internal sealed record AIArenaControlResponse(
    string Id,
    string Command,
    bool Ok,
    string Status,
    string Message,
    object? Data = null,
    string? ErrorCode = null,
    object? State = null)
{
    public static AIArenaControlResponse Success(AIArenaControlRequest request, string message, object? data = null)
    {
        return new AIArenaControlResponse(request.Id, request.Command, true, "ok", message, data);
    }

    public static AIArenaControlResponse Error(AIArenaControlRequest request, string code, string message, object? data = null)
    {
        return new AIArenaControlResponse(request.Id, request.Command, false, "error", message, data, code);
    }
}

internal sealed record AIArenaControlEvent(
    string Type,
    DateTimeOffset CreatedAt,
    string Message,
    object? Data = null)
{
    public string ToJsonLine()
    {
        return AIArenaControlPlaneProtocol.Serialize(this);
    }
}

internal sealed record AIArenaControlSnapshot(
    string AppStatus,
    string SelectedView,
    string Theme,
    bool ControlPlaneEnabled,
    AIArenaAgentControlState Agent,
    AIArenaProviderControlState Provider);

internal sealed record AIArenaAgentControlState(
    string Workspace,
    string Status,
    string Prompt,
    string Command,
    string CommandSource,
    string CommandStatus,
    bool CanApprove,
    bool CanReject,
    bool CanStopCommand,
    bool AutoApprove,
    bool AutoContinue,
    int AutoContinueRemaining,
    string BuildEvidence,
    string LatestWorkBrief,
    string OutputSummary,
    string ArtifactSuggestion,
    string ArtifactVerification);

internal sealed record AIArenaAgentCommandControlState(
    string Command,
    string Source,
    string Status,
    bool CanApprove,
    bool CanReject,
    bool CanStop);

internal sealed record AIArenaAgentWorkControlState(
    string Workspace,
    string Status,
    string LatestWorkBrief,
    string BuildEvidence,
    string ArtifactSuggestion,
    string ArtifactVerification);

internal sealed record AIArenaAgentOutputControlState(
    string Summary,
    string ArtifactSuggestion,
    string ArtifactVerification);

internal sealed record AIArenaProviderRoleControlState(
    string Id,
    string ConfiguredModel,
    string EffectiveModel,
    bool InheritsShared,
    double? TemperatureOverride,
    int? MaxOutputTokensOverride);

internal sealed record AIArenaProviderControlState(
    bool Online,
    string Model,
    string AlphaModel,
    string BetaModel,
    string GammaModel,
    string DeltaModel,
    string NarratorModel,
    string LastError)
{
    // Used only inside the process to bind transient diagnostics to the exact
    // provider configuration that produced them. Internal properties are not
    // included in control-plane JSON, so this fingerprint is never disclosed.
    internal string ConfigurationIdentity { get; init; } = "";

    public string SessionId { get; init; } = "";

    public long PersistenceRevision { get; init; }

    public bool Configured { get; init; }

    public string BaseUrl { get; init; } = "";

    public string ApiMode { get; init; } = "";

    public bool ApiTokenConfigured { get; init; }

    public int TimeoutSeconds { get; init; }

    public double Temperature { get; init; }

    public int MaxOutputTokens { get; init; }

    public int ContextLength { get; init; }

    public string Reasoning { get; init; } = "default";

    public bool NativeStatefulChat { get; init; }

    public int NativeIdleTtlSeconds { get; init; }

    public bool LastTestOk { get; init; }

    public int LastLatencyMs { get; init; }

    public DateTimeOffset? LastHealthCheckedAt { get; init; }

    public DateTimeOffset? LastModelListCheckedAt { get; init; }

    public int? AdvertisedModelCount { get; init; }

    public IReadOnlyList<string> AdvertisedModels { get; init; } = [];

    public bool Busy { get; init; }

    public IReadOnlyList<AIArenaProviderRoleControlState> Roles { get; init; } = [];
}

internal sealed record AIArenaCollaborateControlState(
    bool IsRunning,
    string Status,
    string Prompt,
    string CurrentConversationId,
    int OpenExchangeCount,
    int SavedConversationCount,
    string Provider,
    string Mode,
    string Team);

internal sealed record AIArenaCollaborateTraceControlState(
    string RoleId,
    string RoleName,
    string Model,
    string Label,
    string Text,
    bool Ok,
    string Error,
    int LatencyMs,
    int TotalTokens);

internal sealed record AIArenaCollaborateReviewControlState(
    bool Available,
    string ConversationId,
    string Title,
    DateTimeOffset? UpdatedAt,
    string ReviewState,
    int TurnCount,
    int MemoryNoteCount,
    string LatestPrompt,
    string LatestAnswer,
    string Verdict,
    string Outcome,
    int StepCount,
    int IssueCount,
    int TotalTokens,
    int TotalLatencyMs,
    IReadOnlyList<string> Models,
    string NextAction,
    bool NeedsReview,
    IReadOnlyList<AIArenaCollaborateTraceControlState> Trace);

internal sealed record AIArenaTranscriptExportControlState(
    string SessionId,
    int MessageCount,
    string Markdown);

internal sealed record AIArenaSessionExportControlState(
    string SessionId,
    string AppStatus,
    string SelectedView,
    string ProviderModel,
    int TranscriptMessageCount,
    AIArenaAgentControlState Agent,
    AIArenaCollaborateControlState Collaborate);

internal sealed record AIArenaReceiptExportControlState(
    string SessionId,
    string AgentBuildEvidence,
    string AgentOutputs,
    string CollaborateStatus,
    string ProviderReadiness);

internal interface IAIArenaControlTarget
{
    bool IsControlPlaneEnabled { get; }

    Task<AIArenaControlResponse> ExecuteControlCommandAsync(AIArenaControlRequest request, CancellationToken cancellationToken);
}

internal interface IAIArenaControlEventSource
{
    IDisposable Subscribe(Action<AIArenaControlEvent> onEvent);

    void Publish(AIArenaControlEvent controlEvent);
}
