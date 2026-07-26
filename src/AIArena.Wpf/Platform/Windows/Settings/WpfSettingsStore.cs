using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Persistence;

namespace AIArena.Wpf.Services;

public sealed class WpfSettingsStore
{
    private const int MaxAgentWorkspaceMessages = 80;
    private const int MaxAgentWorkspaceMessageChars = 8000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string SettingsPath { get; }
    public string LastLoadWarning { get; private set; } = "";

    public WpfSettingsStore()
    {
        var dataRoot = NativeDataPaths.DefaultDataRoot();
        SettingsPath = NativeDataPaths.ConfigPath(dataRoot, "native-wpf-settings.json");
    }

    public WpfSettingsStore(string settingsPath)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "native-wpf-settings.json")
            : settingsPath;
    }

    public WpfSettings Load()
    {
        LastLoadWarning = "";
        if (!File.Exists(SettingsPath))
        {
            return Normalize(new WpfSettings());
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<WpfSettings>(json, JsonOptions) ?? new WpfSettings());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LastLoadWarning = JsonFileRecovery.BackupCorruptFile(SettingsPath, "Settings", ex);
            return Normalize(new WpfSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadWarning = $"Settings could not be read and were left unchanged: {ex.Message}";
            return Normalize(new WpfSettings());
        }
    }

    public void Save(WpfSettings settings)
    {
        LastLoadWarning = "";
        Normalize(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        JsonFileRecovery.WriteTextReplacing(SettingsPath, json);
    }

    private static WpfSettings Normalize(WpfSettings settings)
    {
        settings.ThemeId = ThemePalette.NormalizeId(settings.ThemeId);
        settings.AvatarStyle = NormalizeChoice(settings.AvatarStyle, "pack");
        settings.TopStripMode = NormalizeChoice(settings.TopStripMode, "hidden");
        settings.ShowTranscriptDiagnostics = settings.TopStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        settings.RandomSeedStyle = NormalizeChoice(settings.RandomSeedStyle, "auto");
        settings.RandomSeedIntensity = NormalizeChoice(settings.RandomSeedIntensity, "normal");
        settings.RandomSeedRolePack = NormalizeChoice(settings.RandomSeedRolePack, "auto");
        settings.RandomSeedAbsurdity = NormalizeChoice(settings.RandomSeedAbsurdity, "grounded");
        settings.RandomSeedPreset = NormalizeChoice(settings.RandomSeedPreset, "manual");
        settings.AiChoiceTopicPrompt = NormalizeLongText(settings.AiChoiceTopicPrompt, 500);
        settings.VoiceTtsVoiceName = NormalizeChoice(settings.VoiceTtsVoiceName, "");
        settings.AgentWorkspacePath = NormalizeChoice(settings.AgentWorkspacePath, "");
        settings.AgentWorkspaceSessionWorkspacePath = NormalizeChoice(settings.AgentWorkspaceSessionWorkspacePath, "");
        settings.AgentRescueModel = NormalizeChoice(settings.AgentRescueModel, "");
        settings.AgentPlannerReviewerMaxTokens = Math.Clamp(settings.AgentPlannerReviewerMaxTokens, 256, 32768);
        settings.AgentBuilderMaxTokens = Math.Clamp(settings.AgentBuilderMaxTokens, 256, 32768);
        settings.AgentAutoRescueAttempts = Math.Clamp(settings.AgentAutoRescueAttempts, 0, 5);
        settings.AgentCommandTimeoutSeconds = Math.Clamp(settings.AgentCommandTimeoutSeconds, 10, 3600);
        settings.ProviderProfiles = (settings.ProviderProfiles ?? [])
            .Where(profile => !string.IsNullOrWhiteSpace(profile?.Name))
            .ToList();
        settings.AgentWorkspaceMessages = NormalizeAgentWorkspaceMessages(settings.AgentWorkspaceMessages).ToList();
        settings.AgentRunbook = NormalizeAgentRunbook(settings.AgentRunbook);
        if (settings.ControlPlanePreferenceVersion < 1)
        {
            // The control plane was previously an off-by-default Debug feature. Migrate
            // that legacy default once, while preserving later explicit user choices.
            settings.EnableControlPlane = true;
            settings.ControlPlanePreferenceVersion = 1;
        }

        if (settings.AgentWorkspacePreferenceVersion < 1)
        {
            // Agent is now a first-class workspace instead of a Debug experiment. Migrate
            // the legacy hidden default once, while preserving later explicit opt-outs.
            settings.ShowAgentWorkspace = true;
            settings.AgentWorkspacePreferenceVersion = 1;
        }

        if (!settings.AllowDebugControls)
        {
            settings.ShowTranscriptInternetDetails = false;
            settings.ShowWorldDebug = false;
        }

        settings.LabViewMode = settings.AllowDebugControls
            && settings.ShowWorldDebug
            && "world".Equals(settings.LabViewMode?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? "world"
                : "transcript";

        settings.VoiceTtsRate = VoiceNarrationService.NormalizeRate(settings.VoiceTtsRate);
        settings.VoiceTtsVolume = VoiceNarrationService.NormalizeVolume(settings.VoiceTtsVolume);
        settings.OperatorTemplates ??= new WpfSettings().OperatorTemplates.ToList();
        return settings;
    }

    private static IReadOnlyList<WpfAgentWorkspaceMessage> NormalizeAgentWorkspaceMessages(IReadOnlyList<WpfAgentWorkspaceMessage>? messages)
    {
        if (messages is null)
        {
            return [];
        }

        return messages
            .OfType<WpfAgentWorkspaceMessage>()
            .Select(NormalizeAgentWorkspaceMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message.Title) || !string.IsNullOrWhiteSpace(message.Body))
            .TakeLast(MaxAgentWorkspaceMessages)
            .ToList();
    }

    private static WpfAgentWorkspaceMessage NormalizeAgentWorkspaceMessage(WpfAgentWorkspaceMessage message)
    {
        return new WpfAgentWorkspaceMessage
        {
            RoleId = NormalizeChoice(message.RoleId, "system"),
            Title = NormalizeLongText(message.Title, 180),
            Body = NormalizeLongText(message.Body, MaxAgentWorkspaceMessageChars),
            Kind = NormalizeChoice(message.Kind, "Status"),
            Model = NormalizeLongText(message.Model, 180),
            CreatedAt = message.CreatedAt == default ? DateTimeOffset.Now : message.CreatedAt
        };
    }

    private static WpfAgentRunbookState NormalizeAgentRunbook(WpfAgentRunbookState? runbook)
    {
        if (runbook is null || string.IsNullOrWhiteSpace(runbook.RunId) || string.IsNullOrWhiteSpace(runbook.WorkspacePath))
        {
            return new WpfAgentRunbookState();
        }

        var createdAt = runbook.CreatedAt == default ? DateTimeOffset.Now : runbook.CreatedAt;
        var steps = (runbook.Steps ?? [])
            .OfType<WpfAgentRunbookStep>()
            .Where(step => !string.IsNullOrWhiteSpace(step.Id))
            .GroupBy(step => step.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .Select(step => new WpfAgentRunbookStep
            {
                Id = NormalizeLongText(step.Id, 80),
                Sequence = Math.Clamp(step.Sequence, 1, 99),
                Owner = NormalizeLongText(step.Owner, 120),
                Title = NormalizeLongText(step.Title, 180),
                Status = NormalizeChoice(step.Status, "Pending"),
                DependsOn = (step.DependsOn ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => NormalizeLongText(item, 80)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
                Evidence = NormalizeLongText(step.Evidence, 2000),
                UpdatedAt = step.UpdatedAt == default ? createdAt : step.UpdatedAt
            })
            .OrderBy(step => step.Sequence)
            .ToList();
        var checkpoints = (runbook.Checkpoints ?? [])
            .OfType<WpfAgentRunbookCheckpoint>()
            .TakeLast(AgentRunbookService.MaxCheckpoints)
            .Select(checkpoint => new WpfAgentRunbookCheckpoint
            {
                Id = NormalizeLongText(checkpoint.Id, 80),
                Sequence = Math.Clamp(checkpoint.Sequence, 1, 9999),
                Kind = NormalizeLongText(checkpoint.Kind, 80),
                Summary = NormalizeLongText(checkpoint.Summary, 600),
                Evidence = NormalizeLongText(checkpoint.Evidence, 2000),
                CreatedAt = checkpoint.CreatedAt == default ? createdAt : checkpoint.CreatedAt
            })
            .ToList();
        return new WpfAgentRunbookState
        {
            RunId = NormalizeLongText(runbook.RunId, 80),
            WorkspacePath = NormalizeLongText(runbook.WorkspacePath, 2048),
            Objective = NormalizeLongText(runbook.Objective, 1200),
            Status = NormalizeChoice(runbook.Status, "Running"),
            CreatedAt = createdAt,
            UpdatedAt = runbook.UpdatedAt == default ? createdAt : runbook.UpdatedAt,
            Steps = steps,
            Checkpoints = checkpoints
        };
    }

    private static string NormalizeLongText(string? value, int maxChars)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars];
    }

    private static string NormalizeChoice(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

public sealed class WpfSettings
{
    public string ThemeId { get; set; } = "dark-blue";
    public string AvatarStyle { get; set; } = "pack";
    public bool ChampionAvatars { get; set; } = true;
    public bool SystemEventGlyphs { get; set; } = true;
    public bool CompactTranscriptMode { get; set; }
    public bool TurnCompareMode { get; set; }
    public bool ShowMatchQualityTimeline { get; set; }
    public bool ShowBattleReview { get; set; }
    public bool ShowAgentMemoryNotes { get; set; }
    public bool ShowDecisionCard { get; set; }
    public bool ShowAutoModerator { get; set; } = true;
    public bool AllowDebugControls { get; set; }
    public bool ShowTranscriptInternetDetails { get; set; }
    public bool ShowWorldDebug { get; set; }
    public bool ShowAgentWorkspace { get; set; } = true;
    public int AgentWorkspacePreferenceVersion { get; set; }
    public bool EnableControlPlane { get; set; } = true;
    public int ControlPlanePreferenceVersion { get; set; }
    public bool ShowStyleFit { get; set; }
    public bool EnforceVoiceDrift { get; set; }
    public bool FollowTranscript { get; set; } = true;
    public string TopStripMode { get; set; } = "hidden";
    public bool ShowTranscriptDiagnostics { get; set; }
    public string RandomSeedStyle { get; set; } = "auto";
    public string RandomSeedIntensity { get; set; } = "normal";
    public string RandomSeedRolePack { get; set; } = "auto";
    public string RandomSeedAbsurdity { get; set; } = "grounded";
    public string RandomSeedPreset { get; set; } = "manual";
    public string AiChoiceTopicPrompt { get; set; } = "";
    public bool VoiceTtsEnabled { get; set; }
    public bool VoiceTtsAutoNarrator { get; set; } = true;
    public string VoiceTtsVoiceName { get; set; } = "";
    public string AgentWorkspacePath { get; set; } = "";
    public string AgentWorkspaceSessionWorkspacePath { get; set; } = "";
    public string AgentRescueModel { get; set; } = "";
    public bool StreamModelResponses { get; set; } = true;
    public bool AgentBuilderOnlyDefault { get; set; }
    public int AgentPlannerReviewerMaxTokens { get; set; } = 4096;
    public int AgentBuilderMaxTokens { get; set; } = 6144;
    public int AgentAutoRescueAttempts { get; set; } = 2;
    public int AgentCommandTimeoutSeconds { get; set; } = 120;
    public string LabViewMode { get; set; } = "transcript";
    public bool RightRailCollapsed { get; set; }
    public bool AgentPerformanceFullCards { get; set; }
    public List<WpfProviderProfile> ProviderProfiles { get; set; } = [];
    public List<WpfAgentWorkspaceMessage> AgentWorkspaceMessages { get; set; } = [];
    public WpfAgentRunbookState AgentRunbook { get; set; } = new();
    public int VoiceTtsRate { get; set; }
    public int VoiceTtsVolume { get; set; } = 80;
    public List<string> OperatorTemplates { get; set; } =
    [
        "Challenge the strongest assumption in the last turn.",
        "Summarize the disagreement and ask for the smallest concrete next step.",
        "Force the agents to separate facts, guesses, and decisions.",
        "Ask for risks, reversibility, and what would change the conclusion."
    ];
}

public sealed class WpfProviderProfile
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiMode { get; set; } = "";
    public string Model { get; set; } = "";
    public string AlphaModel { get; set; } = "";
    public string BetaModel { get; set; } = "";
    public string GammaModel { get; set; } = "";
    public string DeltaModel { get; set; } = "";
    public string NarratorModel { get; set; } = "";
}

public sealed class WpfAgentWorkspaceMessage
{
    public string RoleId { get; set; } = "system";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "Status";
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WpfAgentRunbookState
{
    public string RunId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Status { get; set; } = "Idle";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WpfAgentRunbookStep> Steps { get; set; } = [];
    public List<WpfAgentRunbookCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class WpfAgentRunbookStep
{
    public string Id { get; set; } = "";
    public int Sequence { get; set; }
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public List<string> DependsOn { get; set; } = [];
    public string Evidence { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WpfAgentRunbookCheckpoint
{
    public string Id { get; set; } = "";
    public int Sequence { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Evidence { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
