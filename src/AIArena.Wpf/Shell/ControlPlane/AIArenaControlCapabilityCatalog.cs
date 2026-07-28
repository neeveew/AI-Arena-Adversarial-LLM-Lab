namespace AIArena.Wpf;

internal sealed record AIArenaControlCapability(
    string Command,
    string Category,
    string Description,
    string[] RequiredArguments,
    string[] OptionalArguments,
    bool Destructive = false);

internal static class AIArenaControlCapabilityCatalog
{
    private static readonly AIArenaControlCapability[] Capabilities =
    [
        Capability(AIArenaControlCommands.Capabilities, "core", "List supported commands and their argument contracts."),
        Capability(AIArenaControlCommands.Status, "core", "Capture current app state."),
        Capability(AIArenaControlCommands.Snapshot, "core", "Capture current app state."),
        Capability(AIArenaControlCommands.EventsWatch, "core", "Stream control-plane events."),
        Capability(AIArenaControlCommands.AppScreenshot, "app", "Save the current AI Arena window visual as a PNG screenshot.", optional: ["path"]),
        Capability(AIArenaControlCommands.NavigationSelect, "navigation", "Open an app surface.", ["view"]),
        Capability(AIArenaControlCommands.NavigationThemeSet, "navigation", "Apply and persist a theme.", ["theme"]),
        Capability(AIArenaControlCommands.NavigationProviderFocus, "navigation", "Open provider settings.", optional: ["baseUrl", "model"]),
        Capability(AIArenaControlCommands.NavigationRailSet, "navigation", "Show, hide, or toggle the right rail.", ["state"]),
        Capability(AIArenaControlCommands.ViewPresetSet, "navigation", "Apply a transcript view preset.", ["preset"]),
        Capability(AIArenaControlCommands.ShellPaletteList, "navigation", "List the command palette entries available on the current surface."),
        Capability(AIArenaControlCommands.ShellPaletteRun, "navigation", "Run a command palette entry by id.", ["id"]),
        Capability(AIArenaControlCommands.ShellInputKey, "navigation", "Send a keyboard chord through the shell shortcut layer without needing window focus.", ["key"], optional: ["modifiers"]),
        Capability(AIArenaControlCommands.ShellInputType, "navigation", "Type text into a named text field, or the focused one.", ["text"], optional: ["target"]),
        Capability(AIArenaControlCommands.MatchSetupState, "match", "Capture Match Setup overlay and active setup state."),
        Capability(AIArenaControlCommands.MatchSetupOpen, "match", "Open Match Setup to a section.", optional: ["section"]),
        Capability(AIArenaControlCommands.MatchSetupClose, "match", "Close Match Setup and return to its originating workspace."),
        Capability(AIArenaControlCommands.MatchSetupExport, "match", "Export the exact active Match Setup as secret-free portable JSON."),
        Capability(AIArenaControlCommands.MatchSetupImport, "match", "Validate portable Match Setup JSON from args.json or a local args.path and create a clean imported session without overwriting the active run.", optional: ["json", "path", "name"]),
        Capability(AIArenaControlCommands.MatchRosterSet, "match", "Resize the active match cast through the normal session persistence path.", ["count"]),
        Capability(AIArenaControlCommands.MatchMatrixState, "match", "Capture the active relationship matrix and links."),
        Capability(AIArenaControlCommands.MatchMatrixSet, "match", "Atomically apply a named relationship-pressure pattern.", ["pattern"], ["enabled"]),
        Capability(AIArenaControlCommands.MatchGenerationState, "match", "Capture current generation recipe and replay history."),
        Capability(AIArenaControlCommands.MatchGenerateRandom, "match", "Generate a local deterministic match setup.", optional: ["style", "intensity", "rolePack", "absurdity", "seed"]),
        Capability(AIArenaControlCommands.MatchGenerateAi, "match", "Ask the configured narrator model to generate a match setup.", optional: ["rolePack", "intensity", "absurdity", "prompt"]),
        Capability(AIArenaControlCommands.MatchGenerateCurrent, "match", "Generate a match from current Internet topics.", optional: ["rolePack", "intensity", "absurdity", "query"]),
        Capability(AIArenaControlCommands.MatchGenerateWild, "match", "Generate a bolder local match setup.", ["confirm"], ["rolePack", "intensity", "absurdity", "seed"], destructive: true),
        Capability(AIArenaControlCommands.MatchReplay, "match", "Replay a generated setup in the active session while preserving transcript.", ["id"]),
        Capability(AIArenaControlCommands.MatchReplayNew, "match", "Replay a generated setup into a clean comparison session.", ["id"]),
        Capability(AIArenaControlCommands.SettingsState, "settings", "Capture visible application preferences and overlay state."),
        Capability(AIArenaControlCommands.SettingsOpen, "settings", "Open application settings.", optional: ["query"]),
        Capability(AIArenaControlCommands.SettingsClose, "settings", "Close application settings."),
        Capability(AIArenaControlCommands.SettingsSearch, "settings", "Open and filter application settings.", optional: ["query"]),
        Capability(AIArenaControlCommands.SettingsUpdate, "settings", "Update safe visual, transcript, voice, and optional-surface preferences.", optional: ["compactTranscript", "followTranscript", "topStripMode", "turnCompare", "matchTimeline", "battleReview", "memoryNotes", "decisionCard", "autoModerator", "styleFit", "internetDetails", "voiceEnabled", "worldEnabled", "agentWorkspaceEnabled"]),
        Capability(AIArenaControlCommands.SessionState, "session", "List sessions and checkpoints for the active session."),
        Capability(AIArenaControlCommands.SessionSelect, "session", "Select and load a saved session.", ["id"]),
        Capability(AIArenaControlCommands.SessionCreate, "session", "Create and select a clean session copied from the active setup.", ["name"]),
        Capability(AIArenaControlCommands.SessionFork, "session", "Create and select an independent full-state branch of the current match.", optional: ["name"]),
        Capability(AIArenaControlCommands.SessionCheckpointCreate, "session", "Save a full checkpoint of the active session.", optional: ["name"]),
        Capability(AIArenaControlCommands.SessionCheckpointRestore, "session", "Restore a checkpoint into the active session.", ["id", "confirm"], destructive: true),
        Capability(AIArenaControlCommands.AgentState, "agent", "Capture Agent workspace state."),
        Capability(AIArenaControlCommands.AgentCommandState, "agent", "Capture staged-command state."),
        Capability(AIArenaControlCommands.AgentWorkBrief, "agent", "Capture the latest Agent work brief."),
        Capability(AIArenaControlCommands.AgentBuildEvidence, "agent", "Capture Agent build evidence."),
        Capability(AIArenaControlCommands.AgentOutputs, "agent", "Capture Agent outputs and artifacts."),
        Capability(AIArenaControlCommands.AgentRunbookState, "agent", "Capture the durable Agent runbook, steps, dependencies, evidence, and checkpoints."),
        Capability(AIArenaControlCommands.AgentRunbookResume, "agent", "Resume the first actionable incomplete runbook step."),
        Capability(AIArenaControlCommands.AgentRunbookCheckpoint, "agent", "Append a durable operator checkpoint.", ["summary"], ["kind"]),
        Capability(AIArenaControlCommands.AgentSend, "agent", "Send an Agent prompt.", ["prompt"]),
        Capability(AIArenaControlCommands.AgentApprove, "agent", "Approve the staged Agent command."),
        Capability(AIArenaControlCommands.AgentReject, "agent", "Reject the staged Agent command."),
        Capability(AIArenaControlCommands.AgentStop, "agent", "Stop active Agent work."),
        Capability(AIArenaControlCommands.AgentStageNext, "agent", "Stage a next-step prompt."),
        Capability(AIArenaControlCommands.AgentStageVerify, "agent", "Stage a verification prompt."),
        Capability(AIArenaControlCommands.AgentStageArtifact, "agent", "Stage an artifact prompt."),
        Capability(AIArenaControlCommands.AgentCommandStage, "agent", "Stage a command for approval.", ["command"], ["shell"]),
        Capability(AIArenaControlCommands.AgentWorkspaceSet, "agent", "Set the Agent workspace.", ["path"]),
        Capability(AIArenaControlCommands.ProviderState, "provider", "Capture provider configuration, readiness, role routing, and advertised-model state."),
        Capability(
            AIArenaControlCommands.ProviderConfigSet,
            "provider",
            "Atomically update the active session's provider configuration and role routing.",
            optional:
            [
                "baseUrl",
                "apiMode",
                "apiToken",
                "clearApiToken",
                "model",
                "alphaModel",
                "betaModel",
                "gammaModel",
                "deltaModel",
                "narratorModel",
                "timeoutSeconds",
                "temperature",
                "maxOutputTokens",
                "contextLength",
                "reasoning",
                "nativeStatefulChat",
                "nativeIdleTtlSeconds",
                "refreshModels"
            ]),
        Capability(AIArenaControlCommands.ProviderModelSet, "provider", "Set all arena role models.", ["model"], ["refreshModels"]),
        Capability(AIArenaControlCommands.ProviderTest, "provider", "Run a completion probe and persist provider readiness.", optional: ["allRoles"]),
        Capability(AIArenaControlCommands.ProviderModelsRefresh, "provider", "Force-refresh the advertised provider model catalog."),
        Capability(AIArenaControlCommands.ArenaStart, "arena", "Start auto-chat."),
        Capability(AIArenaControlCommands.ArenaStop, "arena", "Stop auto-chat."),
        Capability(AIArenaControlCommands.ArenaTurn, "arena", "Run one arena turn."),
        Capability(AIArenaControlCommands.ArenaNarrate, "arena", "Run the narrator now."),
        Capability(AIArenaControlCommands.ArenaReset, "arena", "Reset transcript and live arena state.", ["confirm"], destructive: true),
        Capability(AIArenaControlCommands.ArenaOperatorSend, "arena", "Send an operator intervention.", ["prompt"], ["route"]),
        Capability(AIArenaControlCommands.InternetState, "internet", "Capture Internet and local-search state."),
        Capability(AIArenaControlCommands.InternetSet, "internet", "Enable or disable Internet for the active session.", ["enabled"]),
        Capability(AIArenaControlCommands.InternetTest, "internet", "Test local search and direct HTTPS fetch."),
        Capability(AIArenaControlCommands.CollaborateState, "collaborate", "Capture Collaborate state."),
        Capability(AIArenaControlCommands.CollaborateReview, "collaborate", "Capture the latest saved run review and full latest-turn trace.", optional: ["id"]),
        Capability(AIArenaControlCommands.CollaborateSend, "collaborate", "Send a Collaborate prompt.", ["prompt"]),
        Capability(AIArenaControlCommands.CollaborateStop, "collaborate", "Stop active collaboration."),
        Capability(AIArenaControlCommands.CollaborateFork, "collaborate", "Fork a saved collaboration.", optional: ["id"]),
        Capability(AIArenaControlCommands.CollaborateRepeat, "collaborate", "Repeat a saved collaboration prompt.", optional: ["id"]),
        Capability(AIArenaControlCommands.ExportTranscript, "export", "Return transcript Markdown."),
        Capability(AIArenaControlCommands.ExportSession, "export", "Return structured session state."),
        Capability(AIArenaControlCommands.ExportReceipts, "export", "Return evidence and readiness receipts.")
    ];

    private static readonly HashSet<string> KnownCommands = Capabilities
        .Select(capability => capability.Command)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AIArenaControlCapability> All => Capabilities;

    public static bool IsKnown(string command)
    {
        return KnownCommands.Contains(AIArenaControlPlaneProtocol.NormalizeCommand(command));
    }

    private static AIArenaControlCapability Capability(
        string command,
        string category,
        string description,
        string[]? required = null,
        string[]? optional = null,
        bool destructive = false)
    {
        return new AIArenaControlCapability(
            command,
            category,
            description,
            required ?? [],
            optional ?? [],
            destructive);
    }
}
