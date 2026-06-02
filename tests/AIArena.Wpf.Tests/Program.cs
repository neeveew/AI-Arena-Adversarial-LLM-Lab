using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Resources;
using System.Windows.Media;

var tests = new (string Name, Action Test)[]
{
    ("saves and reloads visual generation settings", SaveReloadVisualGenerationSettings),
    ("normalizes legacy theme and blank settings", NormalizeLegacyThemeAndBlankSettings),
    ("overwrites read-only settings file", OverwriteReadOnlySettingsFile),
    ("backs up corrupt settings file", BackupCorruptSettingsFile),
    ("backs up corrupt scenario template file", BackupCorruptScenarioTemplateFile),
    ("scenario template restores dynamic roster", ScenarioTemplateRestoresDynamicRoster),
    ("provider reachability clamps probe timeout", ProviderReachabilityClampsProbeTimeout),
    ("provider reachability copies status metadata", ProviderReachabilityCopiesStatusMetadata),
    ("auto configure keeps low VRAM to one model", AutoConfigureLowVramSingleModel),
    ("auto configure spreads high VRAM model variety", AutoConfigureHighVramVariety),
    ("auto configure prefers useful multi GPU fit", AutoConfigurePrefersUsefulMultiGpuFit),
    ("role style catalog normalizes labels and chips", RoleStyleCatalogNormalizesLabelsAndChips),
    ("role style catalog formats voice adherence cues", RoleStyleCatalogFormatsVoiceAdherenceCues),
    ("agent memory coordinator normalizes notes", AgentMemoryCoordinatorNormalizesNotes),
    ("agent board coordinator formats statuses", AgentBoardCoordinatorFormatsStatuses),
    ("arena operation coordinator selects operation mode", ArenaOperationCoordinatorSelectsOperationMode),
    ("transcript adjunct helpers format labels", TranscriptAdjunctHelpersFormatLabels),
    ("transcript mutation coordinator formats statuses", TranscriptMutationCoordinatorFormatsStatuses),
    ("arena run coordinator formats statuses", ArenaRunCoordinatorFormatsStatuses),
    ("arena session mutation coordinator normalizes settings", ArenaSessionMutationCoordinatorNormalizesSettings),
    ("session overview coordinator formats summaries", SessionOverviewCoordinatorFormatsSummaries),
    ("shell ui helpers blend brushes", ShellUiHelpersBlendBrushes),
    ("window chrome service packs color refs", WindowChromeServicePacksColorRefs),
    ("user guide app icon resource is packaged", UserGuideAppIconResourceIsPackaged),
    ("provider reachability coordinator formats popup state", ProviderReachabilityCoordinatorFormatsPopupState),
    ("shell navigation coordinator selects themes", ShellNavigationCoordinatorSelectsThemes),
    ("app settings coordinator selects provider focus", AppSettingsCoordinatorSelectsProviderFocus),
    ("coordinator render contracts cover smoke states", CoordinatorRenderContractsCoverSmokeStates),
    ("transcript list coordinator selects retryable turns", TranscriptListCoordinatorSelectsRetryableTurns),
    ("transcript view coordinator normalizes view state", TranscriptViewCoordinatorNormalizesViewState),
    ("custom match summary coordinator normalizes card text", CustomMatchSummaryCoordinatorNormalizesCardText),
    ("scenario seed inspector coordinator formats metadata", ScenarioSeedInspectorCoordinatorFormatsMetadata),
    ("provider quick setup coordinator formats defaults", ProviderQuickSetupCoordinatorFormatsDefaults),
    ("news panel coordinator summarizes internet items", NewsPanelCoordinatorSummarizesInternetItems)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void SaveReloadVisualGenerationSettings()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings
        {
            ThemeId = "dark-green",
            AvatarStyle = "procedural",
            TopStripMode = "telemetry",
            CompactTranscriptMode = true,
            TurnCompareMode = true,
            ShowMatchQualityTimeline = true,
            ShowAgentMemoryNotes = true,
            AllowDebugControls = true,
            ShowStyleFit = true,
            EnforceVoiceDrift = true,
            RandomSeedPreset = "chaos_room",
            RandomSeedRolePack = "absurd_lab",
            RandomSeedStyle = "technical",
            RandomSeedIntensity = "chaos",
            RandomSeedAbsurdity = "maximum",
            OperatorTemplates = ["one", "two"]
        });

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-green", "theme did not persist");
        Require(loaded.AvatarStyle == "procedural", "avatar style did not persist");
        Require(loaded.TopStripMode == "telemetry", "top strip did not persist");
        Require(loaded.CompactTranscriptMode, "compact transcript did not persist");
        Require(loaded.TurnCompareMode, "turn compare did not persist");
        Require(loaded.ShowMatchQualityTimeline, "quality timeline did not persist");
        Require(loaded.ShowAgentMemoryNotes, "memory notes did not persist");
        Require(loaded.AllowDebugControls, "debug controls did not persist");
        Require(loaded.ShowStyleFit, "style fit did not persist");
        Require(loaded.EnforceVoiceDrift, "voice enforcement did not persist");
        Require(loaded.RandomSeedPreset == "chaos_room", "seed preset did not persist");
        Require(loaded.RandomSeedRolePack == "absurd_lab", "role pack did not persist");
        Require(loaded.RandomSeedStyle == "technical", "seed style did not persist");
        Require(loaded.RandomSeedIntensity == "chaos", "seed intensity did not persist");
        Require(loaded.RandomSeedAbsurdity == "maximum", "absurdity did not persist");
        Require(loaded.OperatorTemplates.SequenceEqual(["one", "two"]), "operator templates did not persist");
    });
}

static void NormalizeLegacyThemeAndBlankSettings()
{
    WithTempSettingsStore(store =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
        {
          "themeId": "system",
          "avatarStyle": "",
          "topStripMode": "",
          "randomSeedStyle": " ",
          "randomSeedIntensity": "",
          "randomSeedRolePack": "",
          "randomSeedAbsurdity": "",
          "randomSeedPreset": "",
          "operatorTemplates": null
        }
        """);

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-blue", "legacy system theme should normalize to dark-blue");
        Require(loaded.AvatarStyle == "pack", "blank avatar style should normalize");
        Require(loaded.TopStripMode == "diagnostics", "blank top strip should normalize");
        Require(loaded.RandomSeedStyle == "auto", "blank random seed style should normalize");
        Require(loaded.RandomSeedIntensity == "normal", "blank random seed intensity should normalize");
        Require(loaded.RandomSeedRolePack == "auto", "blank role pack should normalize");
        Require(loaded.RandomSeedAbsurdity == "grounded", "blank absurdity should normalize");
        Require(loaded.RandomSeedPreset == "manual", "blank preset should normalize");
        Require(loaded.OperatorTemplates.Count > 0, "null operator templates should keep default templates");
    });
}

static void OverwriteReadOnlySettingsFile()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings { ThemeId = "dark-green" });
        File.SetAttributes(store.SettingsPath, File.GetAttributes(store.SettingsPath) | FileAttributes.ReadOnly);
        store.Save(new WpfSettings
        {
            ThemeId = "dark-blue",
            RandomSeedPreset = "evidence_trial",
            RandomSeedIntensity = "sharp"
        });

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-blue", "read-only overwrite did not persist theme");
        Require(loaded.RandomSeedPreset == "evidence_trial", "read-only overwrite did not persist preset");
        Require(loaded.RandomSeedIntensity == "sharp", "read-only overwrite did not persist intensity");
    });
}

static void BackupCorruptSettingsFile()
{
    WithTempSettingsStore(store =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, "{ not valid json");

        var loaded = store.Load();
        var backupPattern = $"{Path.GetFileNameWithoutExtension(store.SettingsPath)}.corrupt.*{Path.GetExtension(store.SettingsPath)}";
        var backups = Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, backupPattern);

        Require(loaded.ThemeId == "dark-blue", "corrupt settings should fall back to defaults");
        Require(!File.Exists(store.SettingsPath), "corrupt settings file should be moved aside");
        Require(backups.Length == 1, "corrupt settings backup was not created");
        Require(store.LastLoadWarning.Contains("corrupt", StringComparison.OrdinalIgnoreCase), "settings warning was not recorded");
    });
}

static void BackupCorruptScenarioTemplateFile()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var templatePath = Path.Combine(root, "templates", "scenario-templates.json");
    var store = new ScenarioTemplateStore(templatePath);
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "{ not valid json");

        var loaded = store.Load();
        var backups = Directory.GetFiles(Path.GetDirectoryName(templatePath)!, "scenario-templates.corrupt.*.json");

        Require(loaded.Count == 0, "corrupt templates should fall back to empty list");
        Require(!File.Exists(templatePath), "corrupt template file should be moved aside");
        Require(backups.Length == 1, "corrupt template backup was not created");
        Require(store.LastLoadWarning.Contains("corrupt", StringComparison.OrdinalIgnoreCase), "template warning was not recorded");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ScenarioTemplateRestoresDynamicRoster()
{
    var snapshot = new ArenaSnapshot
    {
        MatchType = "technical"
    };
    snapshot.Engine.Agents.AddRange(
    [
        new DialogueAgent { Id = "alpha", Name = "Alpha", Persona = "old alpha", Active = true },
        new DialogueAgent { Id = "beta", Name = "Beta", Persona = "old beta", Active = true },
        new DialogueAgent { Id = "gamma", Name = "Gamma", Persona = "old gamma", Active = true },
        new DialogueAgent { Id = "epsilon", Name = "Epsilon", Persona = "old epsilon", Active = true },
        new DialogueAgent { Id = "zeta", Name = "Zeta", Persona = "old zeta", Active = true }
    ]);
    snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model" };
    snapshot.Configs["alpha"] = new ModelProviderConfig { Model = "old-alpha-model" };
    snapshot.Configs["beta"] = new ModelProviderConfig { Model = "old-beta-model" };
    snapshot.Configs["zeta"] = new ModelProviderConfig { Model = "old-zeta-model" };
    snapshot.Engine.TurnIndex = 4;

    var template = new ScenarioTemplate(
        "template-id",
        "Dynamic template",
        DateTimeOffset.Now,
        "research",
        "saved topic",
        "saved global",
        TopicLocked: true,
        GlobalLocked: false,
        Agents:
        [
            new ScenarioTemplateAgent("alpha", "Alpha saved", "saved alpha persona", true, false, "scientific", "evidence"),
            new ScenarioTemplateAgent("epsilon", "Epsilon saved", "saved epsilon persona", true, true, "idioms", "chaos"),
            new ScenarioTemplateAgent("narrator", "Narrator", "saved narrator persona", true, false, "skeptical")
        ],
        ModelConfigs: new Dictionary<string, ScenarioTemplateModelConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = new("http://127.0.0.1:1234/v1", "shared-model", 300, 0.8, 1024),
            ["alpha"] = new("http://127.0.0.1:1234/v1", "saved-alpha-model", 300, 0.8, 1024),
            ["epsilon"] = new("http://127.0.0.1:1234/v1", "saved-epsilon-model", 300, 0.8, 1024),
            ["narrator"] = new("http://127.0.0.1:1234/v1", "saved-narrator-model", 300, 0.8, 1024)
        });

    ScenarioTemplateStore.Apply(template, snapshot);

    Require(snapshot.MatchType == "research", "template match type was not applied");
    Require(snapshot.Engine.Agents.Select(agent => agent.Id).SequenceEqual(["alpha", "epsilon"]), "template should restore saved participant roster");
    Require(snapshot.Engine.Agents[1].VoiceStyle == "idioms", "dynamic agent voice was not restored");
    Require(snapshot.Engine.Agents[1].PressureProfile == "chaos", "dynamic agent pressure was not restored");
    Require(snapshot.MatchLocks.TryGetValue("epsilon", out var epsilonLocked) && epsilonLocked, "dynamic agent lock was not restored");
    Require(snapshot.Engine.TurnIndex == 0, "turn index should wrap to restored participant count");
    Require(snapshot.Configs["alpha"].Model == "saved-alpha-model", "saved alpha model was not restored");
    Require(snapshot.Configs["epsilon"].Model == "saved-epsilon-model", "saved epsilon model was not restored");
    Require(!snapshot.Configs.ContainsKey("beta"), "stale beta config was not removed");
    Require(!snapshot.Configs.ContainsKey("zeta"), "stale dynamic config was not removed");
}

static void ProviderReachabilityClampsProbeTimeout()
{
    var source = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "test-model",
        Timeout = 120,
        Temperature = 0.7,
        MaxOutputTokens = 2048,
        LastError = "previous error",
        LastLatencyMs = 99,
        LastTestOk = true
    };

    var probe = ProviderReachabilityService.HealthProbeConfig(source);

    Require(probe.Timeout == 3, "health probe timeout should clamp to 3 seconds");
    Require(probe.Model == source.Model, "health probe should preserve model");
    Require(probe.Temperature == source.Temperature, "health probe should preserve temperature");
    Require(probe.LastTestOk == source.LastTestOk, "health probe should preserve prior status");
}

static void ProviderReachabilityCopiesStatusMetadata()
{
    var source = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "test-model",
        Timeout = 60,
        Temperature = 0.4,
        MaxOutputTokens = 1024,
        LastError = "old",
        LastLatencyMs = 12,
        LastTestOk = false
    };

    var updated = ProviderReachabilityService.CopyConfigWithStatus(source, online: true, error: "", latencyMs: 42);

    Require(updated.BaseUrl == source.BaseUrl, "status copy should preserve base URL");
    Require(updated.Model == source.Model, "status copy should preserve model");
    Require(updated.LastTestOk, "status copy should update online flag");
    Require(updated.LastLatencyMs == 42, "status copy should update latency");
    Require(updated.LastError == "", "status copy should update error");
}

static void AutoConfigureLowVramSingleModel()
{
    var hardware = new HardwareProbe(
        [new GpuDeviceInfo("Tiny GPU", "unknown", 6, 1, 0)],
        16,
        8);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["large-13b-q4", "small-3b-q4", "medium-7b-q4"],
        hardware,
        "auto");

    Require(plan.ProviderOnline, "provider should stay online");
    Require(plan.Strategy == "low_vram", "auto strategy should pick low_vram");
    Require(plan.Assignments.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, "low VRAM should use one unique model");
    Require(plan.DefaultModel.Contains("3b", StringComparison.OrdinalIgnoreCase), "low VRAM should prefer smallest model");
}

static void AutoConfigureHighVramVariety()
{
    var hardware = new HardwareProbe(
        [new GpuDeviceInfo("Big GPU", "NVIDIA", 48, 4, 2)],
        96,
        24);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["model-a-3b-q4", "model-b-7b-q4", "model-c-13b-q4", "model-d-30b-q4", "embedding-model"],
        hardware,
        "max_variety");

    var uniqueModels = plan.Assignments.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    Require(plan.Strategy == "max_variety", "explicit strategy should be honored");
    Require(uniqueModels >= 3, "high VRAM variety should spread across models");
    Require(plan.Models.All(model => !model.Name.Contains("embedding", StringComparison.OrdinalIgnoreCase)), "embedding model should not be used as chat candidate");
    Require(plan.Assignments.Single(item => item.Role == "Narrator").Model.Contains("3b", StringComparison.OrdinalIgnoreCase), "narrator should use smallest model");
}

static void AutoConfigurePrefersUsefulMultiGpuFit()
{
    var hardware = new HardwareProbe(
        [
            new GpuDeviceInfo("Primary GPU", "NVIDIA", 16, 1, 4),
            new GpuDeviceInfo("Secondary GPU", "AMD", 8, 1, 3)
        ],
        64,
        18);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["tiny-helper-1b-q4", "useful-alpha-3b-q4", "useful-beta-4b-q4", "useful-gamma-4b-q4", "too-large-13b-q4"],
        hardware,
        "balanced");

    var assigned = plan.Assignments.Select(item => item.Model).ToArray();
    var uniqueUseful = assigned
        .Where(model => model.Contains("useful", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    Require(uniqueUseful >= 2, "multi-GPU balanced routing should use multiple useful small models");
    Require(!assigned.Take(4).Any(model => model.Contains("tiny-helper", StringComparison.OrdinalIgnoreCase)), "participant routes should not prefer the tiny helper");
    Require(!assigned.Any(model => model.Contains("too-large", StringComparison.OrdinalIgnoreCase)), "comfortable-fit routing should avoid oversized models");
    Require(plan.Warnings.Any(warning => warning.Contains("Multi-GPU", StringComparison.OrdinalIgnoreCase)), "multi-GPU guidance note should be present");
}

static void RoleStyleCatalogNormalizesLabelsAndChips()
{
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("Plain-Language") == "plain_language", "voice style should normalize dashes");
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("legal policy") == "legal_policy", "voice style should normalize spaces");
    Require(RoleStyleCatalog.NormalizeVoiceStyleTag("mystery") == "default", "unknown voice style should fall back to default");
    Require(RoleStyleCatalog.VoiceStyleChipText("default") == "", "default voice style should not render a chip");
    Require(RoleStyleCatalog.VoiceStyleChipText("evidence-ledger") == "Voice: Evidence", "voice style chip should use display label");

    Require(RoleStyleCatalog.NormalizeAgentPressureTag("Risk") == "risk", "pressure should normalize case");
    Require(RoleStyleCatalog.NormalizeAgentPressureTag("too much") == "default", "unknown pressure should fall back to default");
    Require(RoleStyleCatalog.AgentPressureChipText(null) == "", "default pressure should not render a chip");
    Require(RoleStyleCatalog.AgentPressureChipText("chaos") == "Pressure: Chaos", "pressure chip should use display label");

    Require(RoleStyleCatalog.IsStrictVoiceStyle("evidence ledger"), "evidence ledger should be strict");
    Require(!RoleStyleCatalog.IsStrictVoiceStyle("idioms"), "idioms should not be strict");
}

static void RoleStyleCatalogFormatsVoiceAdherenceCues()
{
    Require(RoleStyleCatalog.VoiceAdherenceState(74, 1) == "strong", "74 should be strong");
    Require(RoleStyleCatalog.VoiceAdherenceState(46, 1) == "drifting", "46 should be drifting");
    Require(RoleStyleCatalog.VoiceAdherenceState(45, 1) == "broken", "45 should be broken");
    Require(RoleStyleCatalog.VoiceAdherenceState(99, 0) == "none", "empty samples should be none");
    Require(RoleStyleCatalog.VoiceAdherenceDisplayState("drifting") == "partial cues", "drifting display label should be stable");

    var diagnostic = new VoiceAdherenceDiagnostic(
        "bullet_only",
        "Bullet-only",
        "broken",
        12,
        "Needs bullet structure.",
        ["sentence form"],
        ["bullet markers"]);

    Require(RoleStyleCatalog.VoiceAdherenceChipText(diagnostic) == "Cues: low 12", "adherence chip text should be stable");
    var tooltip = RoleStyleCatalog.VoiceAdherenceTooltip(diagnostic);
    Require(tooltip.Contains("Needs bullet structure.", StringComparison.OrdinalIgnoreCase), "tooltip should include summary");
    Require(tooltip.Contains("Evidence: sentence form", StringComparison.OrdinalIgnoreCase), "tooltip should include evidence");
    Require(tooltip.Contains("Missing: bullet markers", StringComparison.OrdinalIgnoreCase), "tooltip should include missing cues");
}

static void AgentMemoryCoordinatorNormalizesNotes()
{
    var longNote = new string('x', 450);
    var raw = string.Join(
        Environment.NewLine,
        "  alpha note  ",
        "ALPHA NOTE",
        "",
        longNote,
        string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(index => $"note {index:00}")));

    var notes = AgentMemoryCoordinator.NormalizeMemoryNotes(raw);

    Require(notes.Count == 60, "memory notes should cap at 60 entries");
    Require(notes[0] == "alpha note", "memory notes should trim whitespace");
    Require(notes.Count(note => note.Equals("alpha note", StringComparison.OrdinalIgnoreCase)) == 1, "memory notes should dedupe case-insensitively");
    Require(notes[1].Length == 400, "memory notes should truncate long lines");
    Require(!notes.Any(string.IsNullOrWhiteSpace), "memory notes should omit blank lines");
}

static void AgentBoardCoordinatorFormatsStatuses()
{
    Require(AgentBoardCoordinator.DisplayInlineStatus("") == "-", "blank inline status should be placeholder");
    Require(AgentBoardCoordinator.DisplayInlineStatus(" Thinking ") == "thinking", "inline status should trim and lower");
    Require(AgentBoardCoordinator.IsAgentWorkingStatus("busy"), "busy should count as working");
    Require(AgentBoardCoordinator.IsAgentWorkingStatus(" generating "), "generating should count as working");
    Require(!AgentBoardCoordinator.IsAgentWorkingStatus("waiting"), "waiting should not count as working");
}

static void ArenaOperationCoordinatorSelectsOperationMode()
{
    Require(ArenaOperationCoordinator.OperationMode(busy: false, allowDuringAutoChat: false, autoChatRunning: false) == ArenaOperationMode.OwnsBusyState, "idle operation should own busy state");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: false, autoChatRunning: true) == ArenaOperationMode.Blocked, "busy operation should block without explicit auto-chat allowance");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: true, autoChatRunning: false) == ArenaOperationMode.Blocked, "auto-chat allowance should not run when auto-chat is inactive");
    Require(ArenaOperationCoordinator.OperationMode(busy: true, allowDuringAutoChat: true, autoChatRunning: true) == ArenaOperationMode.RunsDuringAutoChat, "allowed operation should run during active auto-chat");
}

static void TranscriptAdjunctHelpersFormatLabels()
{
    var left = TranscriptForTest(4, "Alpha", "alpha", "message", "ok");
    var right = TranscriptForTest(2, "Beta", "beta", "message", "ok");

    Require(TranscriptAdjunctCoordinator.CompareSummary(left, right) == "Comparing turn 4 (Alpha) with turn 2 (Beta).", "compare summary should remain stable");
    Require(TranscriptAdjunctCoordinator.CompareDelta(8, 3) == "+5", "positive compare delta should include plus sign");
    Require(TranscriptAdjunctCoordinator.CompareDelta(3, 8) == "-5", "negative compare delta should use invariant formatting");
    Require(TranscriptAdjunctCoordinator.CompareDelta(3, 3) == "0", "zero compare delta should be plain zero");
    Require(TranscriptAdjunctCoordinator.InternetInspectorTitle(TranscriptForTest(7, "Tool", "internet", "internet_approval", "pending")) == "Approval - pending", "approval inspector title should include status");
    Require(TranscriptAdjunctCoordinator.InternetInspectorTitle(TranscriptForTest(8, "News", "news", "news", "ok")) == "Curated news - ok", "news inspector title should include status");
    Require(TranscriptAdjunctCoordinator.InternetInspectorTitle(TranscriptForTest(9, "Tool", "internet", "internet", "error")) == "Internet tool - error", "internet inspector title should include status");
}

static void TranscriptMutationCoordinatorFormatsStatuses()
{
    var pinned = TranscriptForTest(12, "Alpha", "alpha", "message", "ok") with { Pinned = true };
    var unpinned = pinned with { Pinned = false };
    var session = new SessionSummary("session", "", true, 0, 0, 0, DateTimeOffset.UtcNow);

    Require(TranscriptMutationCoordinator.DeleteStatus(pinned) == "Deleted turn 12.", "delete status should remain stable");
    Require(TranscriptMutationCoordinator.PinStatus(pinned) == "Unpinned turn 12.", "pinned status should describe unpinning");
    Require(TranscriptMutationCoordinator.PinStatus(unpinned) == "Pinned turn 12.", "unpinned status should describe pinning");
    Require(TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: false, session), "valid message should be mutable");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: true, session), "busy arena should block transcript mutation");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned, arenaBusy: false, activeSession: null), "missing session should block transcript mutation");
    Require(!TranscriptMutationCoordinator.CanMutateMessage(pinned with { Turn = 0 }, arenaBusy: false, session), "non-positive turn should block transcript mutation");
}

static void ArenaRunCoordinatorFormatsStatuses()
{
    var message = CoreMessageForTest(5, "Alpha", "alpha", "message", "ok", "model-a", 321);
    var approval = CoreMessageForTest(6, "Tool", "internet", "internet_approval", "pending", "tool-model", 12);
    var completed = OneTurnResult.Completed(
        new OneTurnPlan(true, "alpha", "Alpha", null, null, ""),
        message,
        new ModelCompletionResult(true, "", "model-a", "text", "", 321, 0, 0, 0, "", DateTimeOffset.UtcNow));
    var agent = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "model-a", true, false, []);
    var original = TranscriptForTest(5, "Alpha", "alpha", "message", "ok");

    Require(ArenaRunCoordinator.AutoChatStatus(completed) == "Auto Chat: Alpha spoke (model-a, 321 ms)", "auto-chat success status should remain stable");
    Require(ArenaRunCoordinator.OneTurnStatus(completed) == "1 TURN complete: Alpha (model-a, 321 ms)", "one-turn success status should remain stable");
    Require(ArenaRunCoordinator.AgentTurnStatus(agent, completed) == "Alpha one-shot complete: model-a, 321 ms", "agent turn success status should remain stable");
    Require(ArenaRunCoordinator.RetryStatus(original, completed) == "Retry replaced turn 5: Alpha (model-a, 321 ms)", "retry success status should remain stable");
    Require(ArenaRunCoordinator.NarratorStatus(NarratorResult.Completed(message)) == "Narrator added turn 5 (model-a, 321 ms)", "narrator success status should remain stable");
    Require(ArenaRunCoordinator.AutoChatStatus(OneTurnResult.Failed("offline")) == "Auto Chat stopped: offline", "auto-chat failure status should include error");
    Require(ArenaRunCoordinator.IsPendingInternetApproval(approval), "pending internet approval should be detected");
    Require(!ArenaRunCoordinator.IsPendingInternetApproval(CoreMessageForTest(6, "Tool", "internet", "internet_approval", "ok", "tool-model", 12)), "resolved internet approval should not be pending");
}

static void ArenaSessionMutationCoordinatorNormalizesSettings()
{
    Require(ArenaSessionMutationCoordinator.EffectiveInternetMode(useInternet: false, "auto") == "off", "disabled internet should force off mode");
    Require(ArenaSessionMutationCoordinator.EffectiveInternetMode(useInternet: true, "off") == "auto", "enabled internet should not keep off mode");
    Require(ArenaSessionMutationCoordinator.EffectiveInternetMode(useInternet: true, "model_requested") == "model_requested", "selected internet mode should be preserved");
    Require(ArenaSessionMutationCoordinator.ClampTimeout(-5) == 1, "timeout should clamp low");
    Require(ArenaSessionMutationCoordinator.ClampTimeout(5000) == 3600, "timeout should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampTemperature(-0.5) == 0, "temperature should clamp low");
    Require(ArenaSessionMutationCoordinator.ClampTemperature(3) == 2, "temperature should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampMaxOutput(50000) == 32768, "max output should clamp high");
    Require(ArenaSessionMutationCoordinator.ClampContextWindow(0) == 1, "transcript window should keep at least one turn");
    Require(ArenaSessionMutationCoordinator.ClampOptionalContextWindow(-2) == 0, "optional context windows should allow zero");
    Require(ArenaSessionMutationCoordinator.ClampInternetMaxResults(99) == 10, "internet max results should clamp high");
}

static void SessionOverviewCoordinatorFormatsSummaries()
{
    var alpha = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", true, false, []);
    var beta = new AgentState("beta", "Beta", "waiting", "", "default", "default", "beta-model", true, false, []);
    var messages = new[]
    {
        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { PromptTokens = 120, CompletionTokens = 40 },
        TranscriptForTest(2, "Beta", "beta", "message", "ok") with { PromptTokens = 240, CompletionTokens = -5 }
    };
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "shared-model",
        providerLastError: "provider boom",
        turnIndex: 1,
        messages,
        [alpha, beta]);
    var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);

    Require(current?.Id == "beta", "current turn should wrap over active agents");
    Require(SessionOverviewCoordinator.CurrentTurnModel(snapshot, current) == "beta-model", "current agent model should win over shared model");
    Require(SessionOverviewCoordinator.CurrentTurnModel(snapshot, alpha) == "shared-model", "shared model should be used when current agent has no model");
    Require(SessionOverviewCoordinator.DisplayStatusValue(" alpha ") == "ALPHA", "status labels should trim and uppercase");
    Require(SessionOverviewCoordinator.TopRunStateSummary(snapshot, current, model => model) == "Ready: next BETA using beta-model; provider offline.", "top run summary should remain stable");
    Require(SessionOverviewCoordinator.ProviderSetupStatus(snapshot) == "provider boom", "provider setup should surface provider error");
    Require(SessionOverviewCoordinator.ProviderSetupStatus(snapshot with { ProviderOnline = true }) == "Provider is online. Choose a model, then run 1 TURN.", "online provider setup status should remain stable");
    Require(SessionOverviewCoordinator.ParticipantSummary(snapshot) == "2 agents + operator", "participant summary should count active agents");
    Require(SessionOverviewCoordinator.TotalCompletionTokens(snapshot) == 40, "completion token total should ignore negative values");
    Require(SessionOverviewCoordinator.MaxPromptContext(snapshot) == 240, "prompt context should use maximum prompt tokens");
}

static void ShellUiHelpersBlendBrushes()
{
    var blended = ShellUiHelpers.BlendBrush(
        new SolidColorBrush(Color.FromRgb(0, 0, 0)),
        new SolidColorBrush(Color.FromRgb(100, 200, 255)),
        0.5);
    var color = RequireSolidColor(blended, "blend helper should return a solid brush");

    Require(color.R == 50, "red channel should blend halfway");
    Require(color.G == 100, "green channel should blend halfway");
    Require(color.B == 128, "blue channel should round midpoint");

    var low = RequireSolidColor(ShellUiHelpers.BlendBrush(new SolidColorBrush(Color.FromRgb(10, 20, 30)), new SolidColorBrush(Color.FromRgb(200, 210, 220)), -1), "low clamp should return solid");
    var high = RequireSolidColor(ShellUiHelpers.BlendBrush(new SolidColorBrush(Color.FromRgb(10, 20, 30)), new SolidColorBrush(Color.FromRgb(200, 210, 220)), 2), "high clamp should return solid");
    Require(low == Color.FromRgb(10, 20, 30), "blend amount should clamp below zero");
    Require(high == Color.FromRgb(200, 210, 220), "blend amount should clamp above one");
}

static void WindowChromeServicePacksColorRefs()
{
    Require(WindowChromeService.ColorRef(0x11, 0x22, 0x33) == 0x00332211, "COLORREF should pack bytes as 0x00bbggrr");
}

static void UserGuideAppIconResourceIsPackaged()
{
    using var stream = typeof(WindowChromeService).Assembly.GetManifestResourceStream("AI Arena.g.resources");
    Require(stream is not null, "WPF compiled resources should be present");

    using var reader = new ResourceReader(stream!);
    var resourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry entry in reader)
    {
        if (entry.Key is string key)
        {
            resourceKeys.Add(key);
        }
    }

    Require(resourceKeys.Contains("assets/ai-arena-icon.png"), "user guide header icon png should be packaged");
}

static void ProviderReachabilityCoordinatorFormatsPopupState()
{
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "missing-model",
        providerLastError: "provider offline",
        turnIndex: 0,
        [],
        []);
    var state = ProviderHealthPopupState.From(
        snapshot,
        "fallback-url",
        "fallback-model",
        ["available-model"],
        lastProviderModelCount: -1,
        DateTimeOffset.UtcNow,
        null);

    Require(!state.Online, "offline snapshot should format as offline");
    Require(state.StatusText == "OFFLINE", "offline status label should remain stable");
    Require(state.ModelCountText == "1", "advertised model count should win");
    Require(state.DefaultModelText == "missing-model", "snapshot model should win over fallback text");
    Require(state.ErrorText == "Last error: provider offline", "provider error should be surfaced");
    Require(state.HasError, "provider error flag should be set");
    Require(state.HasMissingModelWarning, "missing advertised model warning should be set");
    Require(state.ModelWarningText == ProviderReachabilityCoordinator.ProviderModelWarning("missing-model"), "missing model warning text should remain stable");

    var fallback = ProviderHealthPopupState.From(null, "", "", [], -1, null, null);
    Require(fallback.BaseUrl == "-", "blank fallback base URL should become placeholder");
    Require(fallback.DefaultModelText == "not selected", "blank fallback model should become not selected");
    Require(fallback.ModelCountText == "unknown", "unknown model count should stay unknown");
    Require(fallback.LastCheckText == "waiting", "missing timestamps should show waiting");
    Require(!fallback.HasMissingModelWarning, "missing model warning should require advertised models");
}

static void ShellNavigationCoordinatorSelectsThemes()
{
    var themes = ThemePalette.BuiltIn
        .Where(theme => !theme.Id.Equals("system", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "dark-green") == "dark-green", "known theme should be selected");
    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "system") == "dark-blue", "system theme should fall back for picker");
    Require(ShellNavigationCoordinator.SelectedThemeId(themes, "missing") == "dark-blue", "unknown theme should fall back");
}

static void AppSettingsCoordinatorSelectsProviderFocus()
{
    Require(AppSettingsCoordinator.ShouldFocusModelPicker(""), "blank model should focus the model picker");
    Require(AppSettingsCoordinator.ShouldFocusModelPicker("   "), "whitespace model should focus the model picker");
    Require(!AppSettingsCoordinator.ShouldFocusModelPicker("model-a"), "configured model should focus the test button");
}

static void CoordinatorRenderContractsCoverSmokeStates()
{
    var alpha = new AgentState("alpha", "Alpha", "thinking", "", "default", "default", "alpha-model", true, false, []);
    var beta = new AgentState("beta", "Beta", "waiting", "skeptic", "default", "default", "", true, false, []);
    var normal = TranscriptForTest(1, "Alpha", "alpha", "message", "ok");
    var approval = TranscriptForTest(2, "Tool", "internet", "internet_approval", "pending") with { InternetSources = ["https://example.test/a"] };
    var news = TranscriptForTest(3, "News", "news", "news", "ok") with { InternetSources = ["https://example.test/b"] };
    var snapshot = SnapshotForOverviewTest(
        providerOnline: false,
        providerModel: "",
        providerLastError: "offline",
        turnIndex: 1,
        [normal, approval, news],
        [alpha, beta])
        with
        {
            ProviderBaseUrl = "-",
            ScenarioTopic = "",
            ScenarioGlobal = "",
            ScenarioGeneratorSeed = "ai-choice",
            PersonaGeneratorStyle = "yolo"
        };

    var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
    var newsMessages = snapshot.Messages.Where(NewsPanelCoordinator.IsNewsMessage).ToArray();

    Require(current?.Id == "beta", "current turn should select beta for turn index 1");
    Require(SessionOverviewCoordinator.TopRunStateSummary(snapshot, current, model => model) == "Ready: next BETA using -; provider offline.", "top run summary should reflect offline provider and missing model");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, current), "offline smoke snapshot should show provider quick setup");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot) == "http://127.0.0.1:1234/v1", "offline smoke snapshot should use quick setup base URL fallback");
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText(snapshot.ScenarioTopic) == "No topic is set for this match yet.", "blank smoke scenario topic should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.ScenarioGlobalText(snapshot.ScenarioGlobal) == "No global instruction is set for this match yet.", "blank smoke global instruction should use empty-state copy");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource(snapshot.ScenarioGeneratorSeed, snapshot.PersonaGeneratorStyle) == "YOLO", "persona YOLO source should win in smoke seed metadata");
    Require(NewsPanelCoordinator.SummaryText(newsMessages) == "2 internet item(s), 2 source(s), 1 waiting for approval", "smoke news summary should count approval and curated news");
    Require(TranscriptListCoordinator.RetryableTurns(snapshot.Messages, speakerId => speakerId is "alpha" or "beta").SetEquals([1]), "smoke retryable turns should include only agent transcript messages");
}

static void TranscriptListCoordinatorSelectsRetryableTurns()
{
    var messages = new[]
    {
        TranscriptForTest(1, "System", "system", "status", "ok"),
        TranscriptForTest(2, "Alpha", "alpha", "message", "ok"),
        TranscriptForTest(3, "Operator", "operator", "message", "ok"),
        TranscriptForTest(4, "Beta", "beta", "message", "ok"),
        TranscriptForTest(5, "Narrator", "narrator", "narration", "ok"),
        TranscriptForTest(6, "Gamma", "gamma", "message", "ok"),
        TranscriptForTest(7, "Delta", "delta", "message", "ok")
    };

    var retryable = TranscriptListCoordinator.RetryableTurns(messages, speakerId =>
        speakerId is "alpha" or "beta" or "gamma" or "delta" or "epsilon" or "zeta" or "eta" or "theta");
    Require(retryable.SetEquals([4, 6, 7]), "retryable turns should be the latest three agent-speaker turns only");
}

static void TranscriptViewCoordinatorNormalizesViewState()
{
    Require(TranscriptViewCoordinator.CurrentAvatarStyle(new WpfSettings { AvatarStyle = "champion", ChampionAvatars = true }) == "procedural", "legacy champion avatar style should map to procedural");
    Require(TranscriptViewCoordinator.CurrentAvatarStyle(new WpfSettings { AvatarStyle = "", ChampionAvatars = false }) == "simple", "blank avatar style should fall back to simple when champion avatars are disabled");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "telemetry" }) == "telemetry", "known top strip mode should be preserved");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "hidden", ShowTranscriptDiagnostics = true }) == "hidden", "known hidden top strip mode should override legacy diagnostics flag");
    Require(TranscriptViewCoordinator.CurrentTopStripMode(new WpfSettings { TopStripMode = "weird", ShowTranscriptDiagnostics = true }) == "diagnostics", "unknown top strip mode should fall back from diagnostics flag");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, false, false, true, "diagnostics") == "Focused", "focused preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, true, true, true, "diagnostics") == "Diagnostics", "diagnostics preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, false, false, false, true, "diagnostics") == "Compact", "compact preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, true, true, true, false, "diagnostics") == "Review", "review preset should be detected");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(false, false, false, false, false, "diagnostics") == "Custom", "modified diagnostics preset should be custom");
    Require(TranscriptViewCoordinator.CurrentViewPresetName(true, true, true, true, false, "telemetry") == "Custom", "non-diagnostics top strip should be custom");
}

static void CustomMatchSummaryCoordinatorNormalizesCardText()
{
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText("") == "No topic is set for this match yet.", "blank topic should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText(" Debate topic ") == " Debate topic ", "topic text should be preserved");
    Require(CustomMatchSummaryCoordinator.ScenarioGlobalText(" ") == "No global instruction is set for this match yet.", "blank global instruction should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.AgentPersonaText("") == "(no persona)", "blank agent persona should use placeholder");
    Require(CustomMatchSummaryCoordinator.AgentPersonaText("skeptical analyst") == "skeptical analyst", "agent persona should be preserved");
    Require(CustomMatchSummaryCoordinator.NarratorPersonaText("") == "(no narrator persona)", "blank narrator persona should use placeholder");
    Require(CustomMatchSummaryCoordinator.NarratorPersonaText("referee") == "referee", "narrator persona should be preserved");
}

static void ScenarioSeedInspectorCoordinatorFormatsMetadata()
{
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("", "") == "Manual", "blank seed should be manual");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("manual-seed", "") == "Random", "nonblank seed should be random");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("ai-choice", "") == "AI Choice", "AI choice seed should be detected");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("YOLO-123", "") == "YOLO", "YOLO seed should be detected");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("manual", "yolo") == "YOLO", "YOLO persona style should win");
    Require(ScenarioSeedInspectorCoordinator.ShortSeedValue("") == "-", "blank seed should become placeholder");
    Require(ScenarioSeedInspectorCoordinator.ShortSeedValue("123456789012345678") == "123456789012345678", "18 character seed should not be shortened");
    Require(ScenarioSeedInspectorCoordinator.ShortSeedValue("1234567890123456789") == "123456789012345...", "long seed should be shortened");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("auto"), "auto role pack should be hidden");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("AUTO"), "auto role pack should hide case-insensitively");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("-"), "placeholder role pack should be hidden");
    Require(ScenarioSeedInspectorCoordinator.ShouldShowRolePack("absurd_lab"), "custom role pack should be visible");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("grounded"), "grounded absurdity should be hidden");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("GROUNDED"), "grounded absurdity should hide case-insensitively");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("-"), "placeholder absurdity should be hidden");
    Require(ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("maximum"), "non-grounded absurdity should be visible");
}

static void ProviderQuickSetupCoordinatorFormatsDefaults()
{
    var agent = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "agent-model", true, false, []);
    var snapshot = SnapshotForOverviewTest(true, "shared-model", "", 0, [], [agent]);

    Require(!ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, agent), "online provider with usable model should hide quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderOnline = false }, agent), "offline provider should show quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderModel = "" }, agent with { Model = "" }), "missing shared and current model should show quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderModel = "" }, agent), "missing shared provider model should show quick setup even when current agent has a model");
    Require(!ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, agent with { Model = "" }), "shared model should satisfy missing current agent model");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot with { ProviderBaseUrl = "-" }) == "http://127.0.0.1:1234/v1", "blank quick setup base URL should use LM Studio default");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot with { ProviderBaseUrl = "http://host/v1" }) == "http://host/v1", "custom base URL should be preserved");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot, agent) == "agent-model", "agent model should populate quick setup model first");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot, agent with { Model = "" }) == "shared-model", "shared provider model should backfill quick setup model");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot with { ProviderModel = "" }, agent with { Model = "" }) == "", "missing model should leave quick setup model blank");
}

static void NewsPanelCoordinatorSummarizesInternetItems()
{
    var normal = TranscriptForTest(1, "Alpha", "alpha", "message", "ok");
    var news = TranscriptForTest(2, "News", "news", "news", "ok") with { InternetSources = ["https://example.test/a"] };
    var approval = TranscriptForTest(3, "Tool", "internet", "internet_approval", "pending") with { InternetSources = ["https://example.test/b"] };
    var tool = TranscriptForTest(4, "Tool", "internet", "message", "ok") with { InternetTool = "web_fetch" };
    var sourced = TranscriptForTest(5, "Alpha", "alpha", "message", "ok") with { InternetSources = ["https://example.test/c"] };
    var resolvedApproval = approval with { Status = "ok" };

    Require(!NewsPanelCoordinator.IsNewsMessage(normal), "plain transcript messages should not appear in news panel");
    Require(NewsPanelCoordinator.IsNewsMessage(news), "news messages should appear in news panel");
    Require(NewsPanelCoordinator.IsNewsMessage(approval), "internet approval messages should appear in news panel");
    Require(NewsPanelCoordinator.IsNewsMessage(tool), "messages with internet tools should appear in news panel");
    Require(NewsPanelCoordinator.IsNewsMessage(sourced), "messages with internet sources should appear in news panel");
    Require(NewsPanelCoordinator.SummaryText([]) == "No internet activity in this session.", "empty news summary should remain stable");
    Require(NewsPanelCoordinator.SummaryText([news, approval, tool]) == "3 internet item(s), 2 source(s), 1 waiting for approval", "news summary should count items, sources, and pending approvals");
    Require(NewsPanelCoordinator.SummaryText([resolvedApproval, sourced]) == "2 internet item(s), 2 source(s)", "resolved approvals should not count as pending");
}

static Color RequireSolidColor(Brush brush, string message)
{
    if (brush is SolidColorBrush solid)
    {
        return solid.Color;
    }

    throw new InvalidOperationException(message);
}

static ArenaViewSnapshot SnapshotForOverviewTest(
    bool providerOnline,
    string providerModel,
    string providerLastError,
    int turnIndex,
    IReadOnlyList<TranscriptMessage> messages,
    IReadOnlyList<AgentState> agents)
{
    return new ArenaViewSnapshot(
        "session",
        "snapshot.json",
        DateTime.UtcNow,
        "research",
        "",
        "",
        false,
        false,
        "auto",
        "normal",
        "auto",
        "grounded",
        "",
        "auto",
        "",
        [],
        false,
        [],
        messages.Count,
        turnIndex,
        providerModel,
        "",
        "",
        "",
        "",
        "",
        "idle",
        "",
        "default",
        false,
        "http://127.0.0.1:1234/v1",
        60,
        0.7,
        2048,
        12,
        4,
        4,
        "",
        "",
        0,
        providerLastError,
        false,
        "off",
        "trusted",
        4,
        false,
        false,
        true,
        "manual",
        providerOnline,
        messages,
        agents);
}

static DialogueMessage CoreMessageForTest(int turn, string speaker, string speakerId, string kind, string status, string model, int latencyMs)
{
    return new DialogueMessage
    {
        Turn = turn,
        Speaker = speaker,
        SpeakerId = speakerId,
        Kind = kind,
        Status = status,
        Text = "text",
        Model = new ModelMetadata
        {
            Model = model,
            LatencyMs = latencyMs
        }
    };
}

static TranscriptMessage TranscriptForTest(int turn, string speaker, string speakerId, string kind, string status)
{
    return new TranscriptMessage(
        turn,
        speaker,
        speakerId,
        0,
        "model",
        0,
        0,
        0,
        0,
        status,
        "",
        false,
        kind,
        "text",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        false,
        []);
}

static void WithTempSettingsStore(Action<WpfSettingsStore> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var store = new WpfSettingsStore(Path.Combine(root, "configs", "native-wpf-settings.json"));
    try
    {
        action(store);
    }
    finally
    {
        if (File.Exists(store.SettingsPath))
        {
            File.SetAttributes(store.SettingsPath, File.GetAttributes(store.SettingsPath) & ~FileAttributes.ReadOnly);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
