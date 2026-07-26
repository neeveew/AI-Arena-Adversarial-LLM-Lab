using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
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
            ShowAutoModerator = false,
            AllowDebugControls = true,
            ShowWorldDebug = true,
            ShowAgentWorkspace = true,
            ShowStyleFit = true,
            EnforceVoiceDrift = true,
            FollowTranscript = false,
            RandomSeedPreset = "chaos_room",
            RandomSeedRolePack = "absurd_lab",
            RandomSeedStyle = "technical",
            RandomSeedIntensity = "chaos",
            RandomSeedAbsurdity = "maximum",
            AiChoiceTopicPrompt = "current AI regulation flashpoint",
            VoiceTtsEnabled = true,
            VoiceTtsAutoNarrator = false,
            VoiceTtsVoiceName = "Test Voice",
            VoiceTtsRate = 4,
            VoiceTtsVolume = 65,
            LabViewMode = "world",
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
        Require(!loaded.ShowAutoModerator, "auto moderator toggle did not persist");
        Require(loaded.AllowDebugControls, "debug controls did not persist");
        Require(loaded.ShowWorldDebug, "AI World debug toggle did not persist");
        Require(loaded.ShowAgentWorkspace, "Agent workspace navigation toggle did not persist");
        Require(loaded.ShowStyleFit, "style fit did not persist");
        Require(loaded.EnforceVoiceDrift, "voice enforcement did not persist");
        Require(!loaded.FollowTranscript, "follow transcript did not persist");
        Require(loaded.RandomSeedPreset == "chaos_room", "seed preset did not persist");
        Require(loaded.RandomSeedRolePack == "absurd_lab", "role pack did not persist");
        Require(loaded.RandomSeedStyle == "technical", "seed style did not persist");
        Require(loaded.RandomSeedIntensity == "chaos", "seed intensity did not persist");
        Require(loaded.RandomSeedAbsurdity == "maximum", "absurdity did not persist");
        Require(loaded.AiChoiceTopicPrompt == "current AI regulation flashpoint", "AI Choice topic prompt did not persist");
        Require(loaded.VoiceTtsEnabled, "voice TTS enabled flag did not persist");
        Require(!loaded.VoiceTtsAutoNarrator, "voice TTS auto narrator toggle did not persist");
        Require(loaded.VoiceTtsVoiceName == "Test Voice", "voice TTS voice did not persist");
        Require(loaded.VoiceTtsRate == 4, "voice TTS rate did not persist");
        Require(loaded.VoiceTtsVolume == 65, "voice TTS volume did not persist");
        Require(loaded.LabViewMode == "world", "AI World view mode did not persist behind its debug gate");
        Require(loaded.OperatorTemplates.SequenceEqual(["one", "two"]), "operator templates did not persist");
    });
}

static void NormalizeSystemThemeAndBlankSettings()
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
          "aiChoiceTopicPrompt": "   ",
          "allowDebugControls": false,
          "showWorldDebug": true,
          "labViewMode": "world",
          "operatorTemplates": null
        }
        """);

        var loaded = store.Load();
        Require(loaded.ThemeId == "system", "system theme should remain available so it can follow Windows contrast preferences");
        Require(loaded.AvatarStyle == "pack", "blank avatar style should normalize");
        Require(loaded.TopStripMode == "hidden", "blank top strip should normalize to the focus-first presentation");
        Require(!loaded.ShowTranscriptDiagnostics, "focus-first settings should keep diagnostics progressively disclosed");
        Require(loaded.RandomSeedStyle == "auto", "blank random seed style should normalize");
        Require(loaded.RandomSeedIntensity == "normal", "blank random seed intensity should normalize");
        Require(loaded.RandomSeedRolePack == "auto", "blank role pack should normalize");
        Require(loaded.RandomSeedAbsurdity == "grounded", "blank absurdity should normalize");
        Require(loaded.RandomSeedPreset == "manual", "blank preset should normalize");
        Require(loaded.AiChoiceTopicPrompt == "", "blank AI Choice topic prompt should normalize");
        Require(loaded.ShowAutoModerator, "missing auto moderator setting should default on");
        Require(!loaded.ShowWorldDebug, "AI World should default off and be cleared when master debug controls are disabled");
        Require(loaded.LabViewMode == "transcript", "disabled AI World debug should normalize stale world sessions back to transcript");
        Require(loaded.FollowTranscript, "missing follow transcript setting should default on");
        Require(loaded.OperatorTemplates.Count > 0, "null operator templates should keep default templates");
        Require(loaded.OperatorTemplates.SequenceEqual(new WpfSettings().OperatorTemplates), "null operator templates should restore the built-in prompts");
    });
}

static void VoiceNarrationSettingsAndTextCleanupStayStable()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings
        {
            VoiceTtsEnabled = true,
            VoiceTtsAutoNarrator = true,
            VoiceTtsVoiceName = " Narrator Voice ",
            VoiceTtsRate = 99,
            VoiceTtsVolume = -20
        });

        var loaded = store.Load();
        Require(loaded.VoiceTtsEnabled, "voice TTS enabled flag should round-trip");
        Require(loaded.VoiceTtsAutoNarrator, "voice TTS auto narrator should round-trip");
        Require(loaded.VoiceTtsVoiceName == "Narrator Voice", "voice TTS voice should trim");
        Require(loaded.VoiceTtsRate == 4, "voice TTS rate should clamp high");
        Require(loaded.VoiceTtsVolume == 0, "voice TTS volume should clamp low");
    });

    Require(VoiceNarrationService.NormalizeRate(-99) == -4, "voice rate should clamp low");
    Require(VoiceNarrationService.NormalizeRate(99) == 4, "voice rate should clamp high");
    Require(VoiceNarrationService.NormalizeVolume(-1) == 0, "voice volume should clamp low");
    Require(VoiceNarrationService.NormalizeVolume(101) == 100, "voice volume should clamp high");
    Require(VoiceNarrationService.VoiceLabel("") == "default Windows voice", "blank voice label should be readable");

    var prepared = VoiceNarrationService.PrepareText("""
    ## Narrator
    **Decision:** use `local TTS`.
    [Source](https://example.com)
    ```json
    {"skip":"code"}
    ```
    - Final note
    """);
    Require(!prepared.Contains("```", StringComparison.Ordinal), "voice cleanup should remove fenced code markers");
    Require(!prepared.Contains("https://", StringComparison.Ordinal), "voice cleanup should strip markdown link URLs");
    Require(prepared.Contains("Decision: use local TTS.", StringComparison.Ordinal), "voice cleanup should keep useful markdown text");
    Require(prepared.Contains("Source", StringComparison.Ordinal), "voice cleanup should preserve link labels");
    Require(prepared.Contains("Final note", StringComparison.Ordinal), "voice cleanup should keep list content");
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

static void JsonStoresIgnoreStaleTempFiles()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));

    try
    {
        var settingsPath = Path.Combine(root, "configs", "native-wpf-settings.json");
        CreateReadOnlyStaleTemp(settingsPath);
        var settingsStore = new WpfSettingsStore(settingsPath);
        settingsStore.Save(new WpfSettings { ThemeId = "dark-green", RandomSeedPreset = "operator_lab" });
        var settings = settingsStore.Load();
        Require(settings.ThemeId == "dark-green", "settings save should ignore stale temp file");
        Require(settings.RandomSeedPreset == "operator_lab", "settings save should persist through stale temp file");

        var historyPath = Path.Combine(root, "configs", "collaborate-history.json");
        CreateReadOnlyStaleTemp(historyPath);
        var historyStore = new CollaborateHistoryStore(historyPath);
        historyStore.Save(
            [
                new CollaborateHistoryConversation
                {
                    Title = "Temp collision check",
                    Exchanges =
                    [
                        new CollaborateHistoryExchange
                        {
                            Prompt = "Save despite stale temp.",
                            Answer = "Saved."
                        }
                    ]
                }
            ]);
        Require(historyStore.Load().Single().Title == "Temp collision check", "history save should ignore stale temp file");

        var templatePath = Path.Combine(root, "templates", "scenario-templates.json");
        CreateReadOnlyStaleTemp(templatePath);
        var templateStore = new ScenarioTemplateStore(templatePath);
        var snapshot = new ArenaSnapshot { MatchType = "stale-temp-check" };
        snapshot.Engine.Steering.Topic = "Template temp collision";
        templateStore.Save("Stale temp template", snapshot);
        var template = templateStore.Load().Single();
        Require(template.MatchType == "stale-temp-check", "template save should ignore stale temp file");
        Require(template.Topic == "Template temp collision", "template save should persist through stale temp file");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
    }
}

static void CreateReadOnlyStaleTemp(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var tempPath = $"{path}.tmp";
    File.WriteAllText(tempPath, "stale");
    File.SetAttributes(tempPath, File.GetAttributes(tempPath) | FileAttributes.ReadOnly);
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

static void AgentWorkspaceDefaultsOnAndPersistsSettingsToggle()
{
    WithTempSettingsStore(store =>
    {
        Require(new WpfSettings().ShowAgentWorkspace, "new settings should show the Agent workspace by default");

        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(
            store.SettingsPath,
            """{"AllowDebugControls":false,"ShowAgentWorkspaceDebug":false}""");
        var migrated = store.Load();
        Require(migrated.ShowAgentWorkspace, "legacy Debug-gated settings should migrate to the default-on Agent workspace");
        Require(!migrated.AllowDebugControls, "Agent workspace migration should not enable unrelated Debug controls");
        Require(migrated.AgentWorkspacePreferenceVersion == 1, "Agent workspace migration should be recorded once");

        migrated.ShowAgentWorkspace = false;
        store.Save(migrated);
        var hidden = store.Load();
        Require(!hidden.ShowAgentWorkspace, "an explicit Agent workspace opt-out should persist after migration");

        hidden.AllowDebugControls = false;
        hidden.ShowAgentWorkspace = true;
        store.Save(hidden);
        var shown = store.Load();
        Require(shown.ShowAgentWorkspace && !shown.AllowDebugControls, "Agent workspace should remain available independently of Debug controls");
    });
}

static void TransientSettingsReadFailuresPreserveFile()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings { ThemeId = "dark-green" });
        var original = File.ReadAllText(store.SettingsPath);
        using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = store.Load();
            Require(loaded.ThemeId == "dark-blue", "temporarily unavailable settings should use safe in-memory defaults");
            Require(store.LastLoadWarning.Contains("left unchanged", StringComparison.OrdinalIgnoreCase), "transient settings warning should state that the file was preserved");
            Require(File.Exists(store.SettingsPath), "transient settings failure must not move the valid file aside");
        }

        Require(File.ReadAllText(store.SettingsPath) == original, "transient settings failure should preserve valid file content");
        var backupPattern = $"{Path.GetFileNameWithoutExtension(store.SettingsPath)}.corrupt.*{Path.GetExtension(store.SettingsPath)}";
        Require(Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, backupPattern).Length == 0, "transient settings failure should not create a corrupt backup");
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

static void OverwriteReadOnlyScenarioTemplateFile()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var templatePath = Path.Combine(root, "templates", "scenario-templates.json");
    var store = new ScenarioTemplateStore(templatePath);
    try
    {
        var snapshot = new ArenaSnapshot { MatchType = "first-pass" };
        snapshot.Engine.Steering.Topic = "Initial topic";
        store.Save("Reusable template", snapshot);
        File.SetAttributes(templatePath, File.GetAttributes(templatePath) | FileAttributes.ReadOnly);

        snapshot.MatchType = "second-pass";
        snapshot.Engine.Steering.Topic = "Updated topic";
        store.Save("Reusable template", snapshot);
        var loaded = store.Load().Single();

        Require(loaded.MatchType == "second-pass", "read-only template overwrite did not persist match type");
        Require(loaded.Topic == "Updated topic", "read-only template overwrite did not persist topic");
        Require((File.GetAttributes(templatePath) & FileAttributes.ReadOnly) == 0, "template overwrite should leave the file writable");
    }
    finally
    {
        if (File.Exists(templatePath))
        {
            File.SetAttributes(templatePath, FileAttributes.Normal);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ScenarioTemplateLoadSkipsNullRecords()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var templatePath = Path.Combine(root, "templates", "scenario-templates.json");
    var store = new ScenarioTemplateStore(templatePath);
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, """
        [
          null,
          {
            "Id": "template-id",
            "Name": "Null-safe template",
            "SavedAt": "2026-06-11T09:00:00+00:00",
            "MatchType": "research",
            "Topic": "saved topic",
            "Global": "saved global",
            "TopicLocked": false,
            "GlobalLocked": true,
            "Agents": [],
            "ModelConfigs": {}
          }
        ]
        """);

        var loaded = store.Load();
        Require(store.LastLoadWarning == "", "null template rows should not mark the whole file corrupt");
        Require(loaded.Count == 1, "valid scenario template should survive null siblings");
        Require(loaded[0].Name == "Null-safe template", "loaded template name mismatch");
        Require(loaded[0].GlobalLocked, "loaded template lock flag mismatch");
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
    snapshot.Configs["alpha"] = new ModelProviderConfig { Model = "old-alpha-model", ApiMode = ModelProviderApiModes.OpenAiCompatible };
    snapshot.Configs["beta"] = new ModelProviderConfig { Model = "old-beta-model" };
    snapshot.Configs["zeta"] = new ModelProviderConfig { Model = "old-zeta-model" };
    snapshot.Engine.TurnIndex = 4;
    snapshot.Engine.LastError = "stale provider failure";
    snapshot.Engine.Narrator.Status = "error";
    snapshot.Engine.Narrator.LastError = "stale narrator failure";
    snapshot.Engine.Agents[0].Status = "error";
    snapshot.Engine.Agents[0].PrivateNotes.Add("old alpha note");
    snapshot.Engine.Agents[3].Status = "thinking";
    snapshot.Engine.Agents[3].PrivateNotes.Add("old epsilon note");

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
            new ScenarioTemplateAgent("alpha", "Alpha saved", "saved alpha persona", true, false, "scientific", "evidence", "35d6ff"),
            new ScenarioTemplateAgent("epsilon", "Epsilon saved", "saved epsilon persona", true, true, "idioms", "chaos", "#ff8a6a"),
            new ScenarioTemplateAgent("narrator", "Narrator", "saved narrator persona", true, false, "skeptical", AccentColor: "#d185ce")
        ],
        ModelConfigs: new Dictionary<string, ScenarioTemplateModelConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = new("http://127.0.0.1:1234/v1", "shared-model", 300, 0.8, 1024),
            ["alpha"] = new("http://127.0.0.1:1234/v1", "saved-alpha-model", 300, 0.8, 1024, ModelProviderApiModes.LmStudioNative, ContextLength: 8192, Reasoning: "low", NativeStatefulChat: false, NativeIdleTtlSeconds: 600),
            ["epsilon"] = new("http://127.0.0.1:1234/v1", "saved-epsilon-model", 300, 0.8, 1024),
            ["narrator"] = new("http://127.0.0.1:1234/v1", "saved-narrator-model", 300, 0.8, 1024)
        });

    ScenarioTemplateStore.Apply(template, snapshot);

    Require(snapshot.MatchType == "research", "template match type was not applied");
    Require(snapshot.Engine.Agents.Select(agent => agent.Id).SequenceEqual(["alpha", "epsilon"]), "template should restore saved participant roster");
    Require(snapshot.Engine.Agents[1].VoiceStyle == "idioms", "dynamic agent voice was not restored");
    Require(snapshot.Engine.Agents[1].PressureProfile == "chaos", "dynamic agent pressure was not restored");
    Require(snapshot.Engine.Agents[0].AccentColor == "#35D6FF", "agent accent color was not normalized");
    Require(snapshot.Engine.Agents[1].AccentColor == "#FF8A6A", "dynamic agent accent color was not restored");
    Require(snapshot.Engine.Narrator.AccentColor == "#D185CE", "narrator accent color was not restored");
    Require(snapshot.Engine.LastError == "", "template should clear stale engine errors");
    Require(snapshot.Engine.Narrator.Status == "idle", "template should reset stale narrator status");
    Require(snapshot.Engine.Narrator.LastError == "", "template should clear stale narrator errors");
    Require(snapshot.Engine.Agents.All(agent => agent.Status == "waiting"), "template should reset restored active agent statuses");
    Require(snapshot.Engine.Agents.All(agent => agent.PrivateNotes.Count == 0), "template should clear restored agent private notes");
    Require(snapshot.MatchLocks.TryGetValue("epsilon", out var epsilonLocked) && epsilonLocked, "dynamic agent lock was not restored");
    Require(snapshot.Engine.TurnIndex == 0, "turn index should wrap to restored participant count");
    Require(snapshot.Configs["alpha"].Model == "saved-alpha-model", "saved alpha model was not restored");
    Require(snapshot.Configs["alpha"].ApiMode == ModelProviderApiModes.LmStudioNative, "saved alpha API mode was not restored");
    Require(snapshot.Configs["alpha"].ContextLength == 8192, "saved alpha native context was not restored");
    Require(snapshot.Configs["alpha"].Reasoning == "low", "saved alpha reasoning setting was not restored");
    Require(!snapshot.Configs["alpha"].NativeStatefulChat, "saved alpha native stateful chat setting was not restored");
    Require(snapshot.Configs["alpha"].NativeIdleTtlSeconds == 600, "saved alpha native idle TTL was not restored");
    Require(snapshot.Configs["epsilon"].Model == "saved-epsilon-model", "saved epsilon model was not restored");
    Require(!snapshot.Configs.ContainsKey("beta"), "stale beta config was not removed");
    Require(!snapshot.Configs.ContainsKey("zeta"), "stale dynamic config was not removed");
}

static void ScenarioWorkflowSeparatesReplayAndCopyDuringAutoChat()
{
    Require(ScenarioWorkflowCoordinator.GenerationHistoryActionEnabled(true, arenaBusy: false, autoChatRunning: false), "history actions should enable while idle with a selection");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryActionEnabled(false, arenaBusy: false, autoChatRunning: false), "history actions should require a selected generation");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryActionEnabled(true, arenaBusy: true, autoChatRunning: false), "history actions should disable during non-auto-chat busy work");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryActionEnabled(true, arenaBusy: true, autoChatRunning: true), "replay actions should wait until auto chat is idle");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryActionEnabled(false, arenaBusy: true, autoChatRunning: true), "history actions should still require a selection during auto chat");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCopyActionEnabled(true), "copy actions should only require a selected generation");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryCopyActionEnabled(false), "copy actions should require a selected generation");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryPickerEnabled(arenaBusy: false, autoChatRunning: false), "history picker should enable while idle");
    Require(!ScenarioWorkflowCoordinator.GenerationHistoryPickerEnabled(arenaBusy: true, autoChatRunning: false), "history picker should disable during non-auto-chat busy work");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryPickerEnabled(arenaBusy: true, autoChatRunning: true), "history picker should remain selectable during auto chat");
}

static void ScenarioWorkflowClipboardHelperHandlesBusyClipboard()
{
    string? copied = null;
    var copiedOk = ScenarioWorkflowCoordinator.TrySetClipboardText("setup receipt", text => copied = text);
    Require(copiedOk, "scenario clipboard helper should report success when the setter succeeds");
    Require(copied == "setup receipt", "scenario clipboard helper should pass text to the setter");

    var busyOk = ScenarioWorkflowCoordinator.TrySetClipboardText(
        "setup receipt",
        _ => throw new InvalidOperationException("clipboard busy"));
    Require(!busyOk, "scenario clipboard helper should report busy clipboard failures without throwing");

    Require(ShellClipboard.TryGetText(out var pasted, () => "portable setup") && pasted == "portable setup", "portable setup import should read non-empty clipboard text");
    Require(!ShellClipboard.TryGetText(out _, () => "   "), "portable setup import should reject an empty clipboard");
    Require(!ShellClipboard.TryGetText(out _, () => throw new InvalidOperationException("clipboard busy")), "portable setup import should report busy clipboard reads without throwing");
}

static void ScenarioWorkflowPreservesGenerationHistorySelection()
{
    var first = new GenerationHistoryItem("gen_1", "random", "label one", "balanced", "normal", "balanced", "grounded", "Seed 1", "Persona 1", 100, "Topic one");
    var second = new GenerationHistoryItem(
        "gen_2",
        "ai_choice",
        "label two",
        "technical",
        "sharp",
        "red_team",
        "odd",
        "ai-choice",
        "ai-choice",
        200,
        "A much longer topic for selection status",
        "Global run rule",
        "Narrator watches evidence.",
        4,
        "alpha: Planner, beta: Skeptic");
    var third = new GenerationHistoryItem("gen_3", "current_topics", "label three", "legal", "sharp", "legal_policy", "grounded", "current-topics", "current-topics", 300, "Live policy flashpoint");
    Require(ScenarioWorkflowCoordinator.PreferredGenerationHistoryIndex([first, second], "gen_2") == 1, "history refresh should preserve previous selected item");
    Require(ScenarioWorkflowCoordinator.PreferredGenerationHistoryIndex([first, second], "missing") == 0, "history refresh should fall back to newest item when previous selection is gone");
    Require(ScenarioWorkflowCoordinator.PreferredGenerationHistoryIndex([], "gen_2") == -1, "empty history should not select an item");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(0, 0) == "No generated matches yet.", "empty generation history status should be clear");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(3, 3) == "3 generated match(es) available.", "complete generation history status should show total count");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(20, 24) == "Showing 20 of 24 generated match(es).", "truncated generation history status should show shown and total counts");
    Require(ScenarioWorkflowCoordinator.NormalizeGenerationHistoryFilter("Wild Seed") == "yolo", "history filter should normalize Wild Seed labels");
    Require(ScenarioWorkflowCoordinator.NormalizeGenerationHistoryFilter("Current Topics") == "current_topics", "history filter should normalize Current Topics labels");
    Require(ScenarioWorkflowCoordinator.FilterGenerationHistory([first, second], "ai_choice").Single().Id == "gen_2", "history filter should keep selected generation type only");
    Require(ScenarioWorkflowCoordinator.FilterGenerationHistory([first, second, third], "current_topics").Single().Id == "gen_3", "history filter should keep Current Topics history only");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(0, 2, 0, "yolo") == "No Wild Seed generated matches in 2 total.", "filtered empty status should explain hidden history");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(1, 2, 1, "ai_choice").Contains("AI Choice", StringComparison.Ordinal), "filtered count status should name the active filter");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryCountStatus(1, 3, 1, "current_topics").Contains("Current Topics", StringComparison.Ordinal), "filtered count status should name the Current Topics filter");
    Require(ScenarioWorkflowCoordinator.CurrentTopicsSearchQuery("policy_crisis_room") == "latest AI policy regulation court ruling today", "policy current-topic preset query changed");
    Require(ScenarioWorkflowCoordinator.CurrentTopicsSearchQuery("unknown") == "latest AI technology policy market news today", "current-topic fallback query changed");
    var brief = ScenarioWorkflowCoordinator.GenerationHistoryBrief(second);
    Require(brief.Contains("Generated match: label two", StringComparison.Ordinal), "history brief should include label");
    Require(brief.Contains("Kind: ai_choice", StringComparison.Ordinal), "history brief should include kind");
    Require(brief.Contains("Scenario seed: ai-choice", StringComparison.Ordinal), "history brief should include scenario seed");
    Require(brief.Contains("Persona seed: ai-choice", StringComparison.Ordinal), "history brief should include persona seed");
    var status = ScenarioWorkflowCoordinator.GenerationHistoryStatus(second);
    Require(status.Contains("ai_choice", StringComparison.Ordinal), "history status should include kind");
    Require(status.Contains("technical", StringComparison.Ordinal), "history status should include style");
    Require(status.Contains("sharp", StringComparison.Ordinal), "history status should include pressure");
    Require(status.Contains("4 role", StringComparison.Ordinal), "history status should include cast size");
    Require(status.Contains("A much longer topic", StringComparison.Ordinal), "history status should include topic preview");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryBrief(second).Contains("Global: Global run rule", StringComparison.Ordinal), "history brief should include global instructions");
    Require(ScenarioWorkflowCoordinator.GenerationHistoryBrief(second).Contains("Narrator brief: Narrator watches evidence.", StringComparison.Ordinal), "history brief should include narrator brief");
    var lockedSnapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "local-model",
        providerLastError: "",
        turnIndex: 0,
        messages: [],
        agents:
        [
            new AgentState("alpha", "Alpha: Planner", "waiting", "persona", "", "", "", "local-model", true, true, []),
            new AgentState("beta", "Beta", "waiting", "persona", "", "", "", "local-model", true, false, [])
        ]) with
    {
        ScenarioTopic = "Topic",
        ScenarioGlobal = "Global",
        TopicLocked = true,
        NarratorLocked = true
    };
    Require(ScenarioWorkflowCoordinator.ReplayLockLabels(lockedSnapshot).Length == 3, "replay lock labels should include topic, narrator, and active locked agent");
    Require(ScenarioWorkflowCoordinator.ReplayLockWarning(lockedSnapshot).Contains("3 lock", StringComparison.OrdinalIgnoreCase), "replay lock warning should summarize lock count");
    Require(ScenarioWorkflowCoordinator.GenerationSelectionStatus(second, lockedSnapshot).Contains("may preserve current", StringComparison.OrdinalIgnoreCase), "selection status should warn about replay locks");
    var spec = ScenarioWorkflowCoordinator.GenerationHistorySpec(second, lockedSnapshot);
    Require(spec.Contains("\"schema\": \"ai_arena.generated_match.v1\"", StringComparison.Ordinal), "setup spec should include schema id");
    Require(spec.Contains("\"deterministic\": false", StringComparison.Ordinal), "AI Choice spec should mark non-deterministic seed");
    Require(spec.Contains("\"replayMode\": \"captured_output_replayable\"", StringComparison.Ordinal), "AI Choice spec should identify captured-output replay");
    var currentTopicsSpec = ScenarioWorkflowCoordinator.GenerationHistorySpec(third, lockedSnapshot);
    Require(currentTopicsSpec.Contains("\"deterministic\": false", StringComparison.Ordinal), "Current Topics spec must not claim that live web and model output are seed-deterministic");
    Require(currentTopicsSpec.Contains("\"replayMode\": \"captured_output_replayable\"", StringComparison.Ordinal), "Current Topics spec should identify captured-output replay");
    Require(!ScenarioWorkflowCoordinator.GenerationSeedIsDeterministic(second), "AI Choice should copy its replay id instead of a synthetic seed");
    Require(!ScenarioWorkflowCoordinator.GenerationSeedIsDeterministic(third), "Current Topics should copy its replay id instead of a synthetic seed");
    Require(ScenarioWorkflowCoordinator.GenerationSeedIsDeterministic(first), "Random generation should retain deterministic seed behavior");
    Require(spec.Contains("\"presetMatches\"", StringComparison.Ordinal), "setup spec should include preset match metadata");
    Require(spec.Contains("\"review\"", StringComparison.Ordinal), "setup spec should include review helpers");
    Require(spec.Contains("\"currentLocks\"", StringComparison.Ordinal), "setup spec should include current lock impact");
    var diff = ScenarioWorkflowCoordinator.GenerationHistoryDiff(second, lockedSnapshot);
    Require(diff.Contains("Generated setup diff", StringComparison.Ordinal), "history diff should include a clear title");
    Require(diff.Contains("Topic changes", StringComparison.Ordinal), "history diff should call out topic changes");
    Require(diff.Contains("Cast size differs", StringComparison.Ordinal), "history diff should call out cast size changes");
    var rubric = ScenarioWorkflowCoordinator.GenerationHistoryRubric(second, lockedSnapshot);
    Require(rubric.Contains("Score each dimension 1-5", StringComparison.Ordinal), "history rubric should include scoring guidance");
    Require(rubric.Contains("Creative containment", StringComparison.Ordinal), "history rubric should adapt to odd or chaotic setups");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("socratic_audit") == ("safety_audit", "philosophical", "sharp", "grounded"), "Socratic audit preset should map to a sharp safety audit");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("incident_speedrun") == ("incident_response", "incident", "one_line", "grounded"), "incident speedrun preset should map to concise incident response");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("bureaucracy_inferno") == ("absurd_lab", "legal", "chaos", "maximum"), "bureaucracy inferno preset should map to maximal legal chaos");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("alien_courtroom") == ("absurd_lab", "philosophical", "spicy", "maximum"), "alien courtroom preset should map to spicy absurd philosophy");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("meme_tribunal") == ("absurd_lab", "creative", "one_line", "maximum"), "meme tribunal preset should map to one-line creative mayhem");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("paranoid_compliance") == ("safety_audit", "legal", "sharp", "odd"), "paranoid compliance preset should map to odd legal safety audit");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("model_duel") == ("benchmark_duel", "technical", "sharp", "grounded"), "model duel preset should map to benchmark duel");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("tool_reliability_trial") == ("tool_ops", "technical", "sharp", "grounded"), "tool trial preset should map to tool ops");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("governance_board") == ("governance_board", "legal", "spicy", "odd"), "governance board preset should map to legal oversight");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("policy_crisis_room") == ("legal_policy", "legal", "chaos", "grounded"), "policy crisis room preset should map to legal policy pressure");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("market_shock") == ("product_risk", "product", "sharp", "grounded"), "market shock preset should map to product and market risk");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("tech_ethics_hearing") == ("safety_audit", "safety", "spicy", "grounded"), "tech ethics hearing preset should map to safety audit pressure");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("geopolitical_risk_desk") == ("red_team", "red-team", "sharp", "grounded"), "geopolitical risk desk preset should map to red-team risk pressure");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("black_box_audit") == ("safety_audit", "technical", "sharp", "grounded"), "black-box audit preset should map to technical safety audit");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("approval_maze") == ("tool_ops", "legal", "chaos", "odd"), "approval maze preset should map to chaotic legal tool ops");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("launch_war_room") == ("product_risk", "product", "chaos", "grounded"), "launch war room preset should map to chaotic product risk");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("template_forge") == ("technical_architecture", "technical", "spicy", "odd"), "template forge preset should map to architecture tuning");
    Require(ScenarioWorkflowCoordinator.RandomSeedPresetValues("memory_handoff") == ("balanced", "research", "spicy", "absurd"), "memory handoff preset should map to research handoff tuning");
    Require(ScenarioWorkflowCoordinator.GenerationPresetCatalog.Count >= 27, "preset catalog should expose the expanded gallery");
    Require(ScenarioWorkflowCoordinator.GenerationPresetCatalog.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ScenarioWorkflowCoordinator.GenerationPresetCatalog.Count, "preset catalog keys should be unique");
    Require(ScenarioWorkflowCoordinator.GenerationPresetDetails("approval_maze").Risk.Contains("source boundaries", StringComparison.OrdinalIgnoreCase), "approval maze metadata should explain approval risk");
    Require(ScenarioWorkflowCoordinator.GenerationPresetReceiptText("geopolitical_risk_desk").Contains("Geopolitical Risk Desk", StringComparison.Ordinal), "current-topic preset receipt should include the stable label");
    Require(ScenarioWorkflowCoordinator.GenerationPresetDetails("missing").Key == "manual", "unknown preset details should fall back to manual");
    Require(ScenarioWorkflowCoordinator.GenerationPresetTooltip(ScenarioWorkflowCoordinator.GenerationPresetDetails("template_forge")).Contains("Best for:", StringComparison.Ordinal), "preset tooltip should include best-for guidance");
    Require(ScenarioWorkflowCoordinator.GenerationPresetReceiptText("black_box_audit").Contains("AI Arena preset: Black-Box Audit", StringComparison.Ordinal), "preset receipt should include a stable title");
    Require(ScenarioWorkflowCoordinator.GenerationPresetCatalogSummary().Contains("preset(s) across", StringComparison.Ordinal), "preset catalog summary should include category counts");
    Require(ScenarioWorkflowCoordinator.GenerationPresetMatchLabels("benchmark_duel", "technical", "sharp", "grounded").Contains("Model Duel"), "preset matcher should identify exact benchmark recipes");
    Require(ScenarioWorkflowCoordinator.GenerationPresetMatchSummary("balanced", "technical", "normal", "grounded") == "Preset match: custom recipe.", "preset matcher should identify custom recipes");
    var summary = ScenarioWorkflowCoordinator.GenerationControlSummary("model_duel", "benchmark_duel", "technical", "sharp", "grounded");
    Require(summary.Contains("model duel", StringComparison.OrdinalIgnoreCase), "generation recipe should include preset label");
    Require(summary.Contains("benchmark duel pack", StringComparison.OrdinalIgnoreCase), "generation recipe should include role pack");
    var readySnapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "local-model",
        providerLastError: "",
        turnIndex: 0,
        messages: [],
        agents:
        [
            new AgentState("alpha", "Alpha", "waiting", "persona", "", "", "", "local-model", true, false, []),
            new AgentState("beta", "Beta", "waiting", "persona", "", "", "", "local-model", true, false, [])
        ]) with
    {
        ScenarioTopic = "Compare two local models.",
        ScenarioGlobal = "Keep the comparison blind. Quality contract: define what a good outcome means, name at least one unacceptable failure, test one edge case, and finish with an actionable output plus unresolved uncertainty.",
        ScenarioGeneratorRolePack = "benchmark_duel",
        ScenarioGeneratorStyle = "technical",
        ScenarioGeneratorIntensity = "sharp",
        ScenarioGeneratorAbsurdity = "grounded",
        NarratorPersona = "Judge the match fairly."
    };
    var readyReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot);
    Require(readyReport.Status.StartsWith("Ready:", StringComparison.Ordinal), "ready setup should produce ready status");
    Require(readyReport.Blockers.Count == 0, "ready setup should have no blockers");
    Require(readyReport.Warnings.Count == 0, "ready setup should have no warnings");
    Require(readyReport.Badges.Any(badge => badge.Label == "State" && badge.Value == "Ready" && badge.Kind == "ready"), "ready setup should expose a ready state badge");
    Require(readyReport.Badges.Any(badge => badge.Label == "Provider" && badge.Value == "Online" && badge.Kind == "ready"), "ready setup should expose an online provider badge");
    Require(readyReport.Badges.Any(badge => badge.Label == "Criteria" && badge.Value == "Auditable" && badge.Kind == "ready"), "ready generated setup should expose auditable closure criteria");
    Require(readyReport.Badges.Any(badge => badge.Label == "Locks" && badge.Value == "None"), "ready setup should expose a lock-count badge");
    Require(readyReport.Checklist.Single().Kind == "ready", "ready setup should expose an all-clear checklist item");
    Require(ScenarioWorkflowCoordinator.SetupReadinessStatus(readySnapshot with { ScenarioTopic = "" }).Contains("set or generate a topic", StringComparison.OrdinalIgnoreCase), "blank topic should be called out in setup readiness");
    var basicCriteriaReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with { ScenarioGlobal = "Keep the comparison blind." });
    Require(basicCriteriaReport.Warnings.Any(warning => warning.Contains("quality contract", StringComparison.OrdinalIgnoreCase)), "legacy or manual scenarios without criteria should show a nonblocking readiness warning");
    Require(basicCriteriaReport.Badges.Any(badge => badge.Label == "Criteria" && badge.Value == "Basic" && badge.Kind == "warning"), "missing criteria should be visible as a warning badge");
    var partialCriteriaReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with
    {
        ScenarioGlobal = "Quality contract: define what a good outcome means."
    });
    Require(partialCriteriaReport.Badges.Any(badge => badge.Label == "Criteria" && badge.Value == "Basic"), "a partial quality marker must not be presented as an auditable contract");
    var roleModelReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with
    {
        ProviderModel = "",
        Agents =
        [
            new AgentState("alpha", "Alpha", "waiting", "persona", "", "", "", "alpha-model", true, false, []),
            new AgentState("beta", "Beta", "waiting", "persona", "", "", "", "beta-model", true, false, [])
        ]
    });
    Require(roleModelReport.Blockers.Count == 0, "role-specific active agent models should satisfy provider model readiness");
    Require(roleModelReport.Badges.Any(badge => badge.Label == "Provider" && badge.Value == "Role models" && badge.Tooltip.Contains("role-specific", StringComparison.OrdinalIgnoreCase)), "role-specific model readiness should be visible in provider badge");
    var warningReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with
    {
        ProviderOnline = false,
        ProviderLastError = "connection refused",
        NarratorPersona = "",
        Agents =
        [
            new AgentState("alpha", "Alpha", "waiting", "", "", "", "", "local-model", true, false, []),
            new AgentState("beta", "Beta", "waiting", "persona", "", "", "", "local-model", true, false, [])
        ]
    });
    Require(warningReport.Status.StartsWith("Ready with warnings:", StringComparison.Ordinal), "nonblocking setup issues should produce ready-with-warnings status");
    Require(warningReport.Blockers.Count == 0, "warning-only setup should not block runs");
    Require(warningReport.Warnings.Any(warning => warning.Contains("provider is offline", StringComparison.OrdinalIgnoreCase)), "offline provider should be a readiness warning");
    Require(warningReport.Warnings.Any(warning => warning.Contains("active agent persona", StringComparison.OrdinalIgnoreCase)), "blank active personas should be a readiness warning");
    Require(warningReport.Warnings.Any(warning => warning.Contains("narrator persona", StringComparison.OrdinalIgnoreCase)), "blank narrator persona should be a readiness warning");
    Require(warningReport.Badges.Any(badge => badge.Label == "State" && badge.Value == "Warnings" && badge.Kind == "warning"), "warning setup should expose a warning state badge");
    Require(warningReport.Badges.Any(badge => badge.Label == "Personas" && badge.Value == "1 blank" && badge.Kind == "warning"), "warning setup should expose blank persona badge");
    Require(warningReport.Badges.Any(badge => badge.Label == "Provider" && badge.Value == "Error" && badge.Tooltip.Contains("connection refused", StringComparison.OrdinalIgnoreCase)), "provider badge should retain offline error detail");
    Require(warningReport.Checklist.Count(item => item.Kind == "warning") == 3, "warning setup should expose visible advisory checklist items");
    var invalidMatrixReadySnapshot = readySnapshot with
    {
        RivalryMatrixEnabled = true,
        RivalryMatrix =
        [
            new RivalryMatrixItem("alpha", "alpha", "challenge"),
            new RivalryMatrixItem("gamma", "alpha", "support"),
            new RivalryMatrixItem("alpha", "beta", "neutral")
        ]
    };
    var blockedReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(invalidMatrixReadySnapshot);
    Require(blockedReport.Status.StartsWith("Setup blocked:", StringComparison.Ordinal), "blocking setup issues should produce blocked status");
    Require(blockedReport.Blockers.Any(blocker => blocker.Contains("relationship matrix is enabled but has no active rules", StringComparison.OrdinalIgnoreCase)), "setup readiness should ignore invalid relationship matrix noise");
    Require(blockedReport.Badges.Any(badge => badge.Label == "Matrix" && badge.Value == "No rules" && badge.Kind == "danger"), "blocked setup should expose matrix danger badge");
    Require(blockedReport.Tooltip.Contains("Blockers", StringComparison.Ordinal), "blocked setup tooltip should include blocker section");
    Require(warningReport.Tooltip.Contains("Warnings", StringComparison.Ordinal), "warning setup tooltip should include warning section");
    Require(blockedReport.Checklist.Any(item => item.Label == "Required" && item.Kind == "danger"), "blocked setup should expose visible required checklist items");
    var unknownStanceReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with
    {
        RivalryMatrixEnabled = true,
        RivalryMatrix = [new RivalryMatrixItem("alpha", "beta", "invented_stance")]
    });
    Require(unknownStanceReport.Blockers.Any(blocker => blocker.Contains("relationship matrix is enabled but has no active rules", StringComparison.OrdinalIgnoreCase)), "unknown relationship stances should normalize away before readiness counts active rules");
    var validNoisyMatrixReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(readySnapshot with
    {
        RivalryMatrixEnabled = true,
        RivalryMatrix =
        [
            new RivalryMatrixItem("alpha", "beta", "challenge"),
            new RivalryMatrixItem("beta", "beta", "support"),
            new RivalryMatrixItem("gamma", "alpha", "fact_check")
        ]
    });
    Require(validNoisyMatrixReport.Blockers.Count == 0, "one valid relationship rule should satisfy matrix readiness despite invalid noise");
    Require(validNoisyMatrixReport.Badges.Any(badge => badge.Label == "Matrix" && badge.Value == "1 rule(s)"), "matrix badge should count normalized active rules only");
}

static void MatchSetupCoordinatorPlansRivalryMatrixSafely()
{
    var plan = MatchSetupCoordinator.BuildRivalryMatrixPlan(
        [
            new RivalryMatrixItem("Alpha", "Beta", "Challenge"),
            new RivalryMatrixItem("beta", "beta", "rival"),
            new RivalryMatrixItem("gamma", "delta", "support"),
            new RivalryMatrixItem("alpha", "gamma", "steelman"),
            new RivalryMatrixItem("eta", "alpha", "cross-examine"),
            new RivalryMatrixItem("gamma", "", "support"),
            new RivalryMatrixItem("beta", "gamma", "neutral")
        ],
        ["alpha", "beta", "gamma"]);

    Require(plan.Links.Count == 1, "matrix plan should keep only valid active non-neutral source rules");
    Require(plan.Links[0] == new RivalryMatrixItem("alpha", "beta", "challenge"), "matrix plan should normalize agent ids and stance tags");
    Require(plan.SkippedInvalidRules == 4, "matrix plan should count self-target, inactive-source, inactive-target, and duplicate-source rules as skipped");
    Require(MatchSetupCoordinator.NormalizeRivalryStance("Cross-examine") == "cross_examine", "matrix stance normalization should accept user-facing labels");
    Require(MatchSetupCoordinator.NormalizeRivalryStance("Fact check") == "fact_check", "matrix stance normalization should accept fact-check labels");
    Require(MatchSetupCoordinator.NormalizeRivalryStance("Devil's advocate") == "devils_advocate", "matrix stance normalization should accept devil's advocate labels");

    var summary = MatchSetupCoordinator.Summary(
        enabled: true,
        [
            new RivalryMatrixItem("alpha", "beta", "challenge"),
            new RivalryMatrixItem("beta", "alpha", "support"),
            new RivalryMatrixItem("beta", "beta", "rival")
        ],
        ["alpha", "beta"]);
    Require(summary.Contains("challenge 1", StringComparison.OrdinalIgnoreCase), "enabled summary should include challenge count");
    Require(summary.Contains("support 1", StringComparison.OrdinalIgnoreCase), "enabled summary should include support count");
    Require(summary.Contains("mutual pairs 1", StringComparison.OrdinalIgnoreCase), "enabled summary should include mutual-pair count");
    Require(summary.Contains("coverage 2/2", StringComparison.OrdinalIgnoreCase), "enabled summary should include graph coverage");
    Require(summary.Contains("1 invalid rule(s) ignored", StringComparison.OrdinalIgnoreCase), "enabled summary should include invalid-rule warnings");
    var topology = MatchSetupCoordinator.Topology(
        enabled: true,
        [
            new RivalryMatrixItem("alpha", "beta", "challenge"),
            new RivalryMatrixItem("gamma", "beta", "fact_check")
        ],
        ["alpha", "beta", "gamma", "delta"]);
    Require(topology.ActiveRules == 2, "relationship topology should count active rules");
    Require(topology.ActiveSources == 2, "relationship topology should count covered sources");
    Require(topology.UnassignedSources == 2, "relationship topology should count neutral sources");
    Require(topology.HotspotTarget == "beta" && topology.HotspotIncoming == 2, "relationship topology should surface incoming hotspots");
    var insight = MatchSetupCoordinator.RelationshipInsight(
        true,
        [
            new RivalryMatrixItem("alpha", "beta", "challenge"),
            new RivalryMatrixItem("gamma", "beta", "fact_check")
        ],
        ["alpha", "beta", "gamma", "delta"]);
    Require(insight.Contains("covers 2/4", StringComparison.OrdinalIgnoreCase), "relationship insight should summarize coverage");
    Require(insight.Contains("beta receives 2", StringComparison.OrdinalIgnoreCase), "relationship insight should summarize target hotspots");
    var preview = MatchSetupCoordinator.RelationshipPreviewLines(
        true,
        [new RivalryMatrixItem("alpha", "beta", "fact_check")],
        ["alpha", "beta"]);
    Require(preview.Single().Contains("alpha -> beta", StringComparison.OrdinalIgnoreCase), "relationship preview should show source and target");
    Require(preview.Single().Contains("fact-check", StringComparison.OrdinalIgnoreCase), "relationship preview should show stance label");
    Require(MatchSetupCoordinator.MutualPressurePairs([
        new RivalryMatrixItem("alpha", "beta", "challenge"),
        new RivalryMatrixItem("beta", "alpha", "support"),
        new RivalryMatrixItem("gamma", "delta", "rival")
    ]) == 1, "mutual pressure pair counting should count bidirectional pairs once");

    var disabled = MatchSetupCoordinator.Summary(
        enabled: false,
        [new RivalryMatrixItem("alpha", "beta", "support")],
        ["alpha", "beta"]);
    Require(disabled.Contains("currently disabled", StringComparison.OrdinalIgnoreCase), "disabled summary should preserve saved-rule context");
    Require(disabled.Contains("coverage 1/2", StringComparison.OrdinalIgnoreCase), "disabled summary should preserve graph coverage");

    var pattern = MatchSetupCoordinator.BuildRivalryPatternDraft("evidence_ladder", ["alpha", "beta", "gamma", "delta"]);
    Require(pattern.Count == 4, "evidence ladder should create one draft rule per active source");
    Require(pattern[0] == new RivalryMatrixItem("alpha", "beta", "fact_check"), "evidence ladder should start with fact-check pressure");
    Require(pattern[1] == new RivalryMatrixItem("beta", "gamma", "cross_examine"), "evidence ladder should chain to cross-examination");
    var rivals = MatchSetupCoordinator.BuildRivalryPatternDraft("mutual_rivals", ["alpha", "beta", "gamma"]);
    Require(MatchSetupCoordinator.MutualPressurePairs(rivals) == 1, "mutual rivals should create one bidirectional pair for three agents");
    var crossfire = MatchSetupCoordinator.BuildRivalryPatternDraft("paired_crossfire", ["alpha", "beta", "gamma"]);
    Require(crossfire.Count == 3, "paired crossfire should create pair pressure plus an odd-agent fallback");
    Require(MatchSetupCoordinator.MutualPressurePairs(crossfire) == 1, "paired crossfire should create a mutual challenge pair");
    var spotlight = MatchSetupCoordinator.BuildRivalryPatternDraft("spotlight_defense", ["alpha", "beta", "gamma", "delta"]);
    Require(spotlight.Count(link => link.Target == "delta") == 3, "spotlight defense should focus most agents on the spotlight target");
    Require(spotlight.Any(link => link.Source == "delta" && link.Stance == "steelman"), "spotlight defense should give the spotlight a steelman response");
    var sweep = MatchSetupCoordinator.BuildRivalryPatternDraft("skeptic_sweep", ["alpha", "beta", "gamma"]);
    Require(sweep.Any(link => link.Stance == "fact_check") && sweep.Any(link => link.Stance == "cross_examine"), "skeptic sweep should mix fact-check and cross-examine pressure");
}

static void SavedStateWorkflowIgnoresStaleCheckpointRefresh()
{
    Require(SavedStateWorkflowCoordinator.ShouldApplyCheckpointRefresh("session-a", "SESSION-A"), "matching session should accept checkpoint refresh");
    Require(!SavedStateWorkflowCoordinator.ShouldApplyCheckpointRefresh("session-a", "session-b"), "stale session should reject checkpoint refresh");
    Require(!SavedStateWorkflowCoordinator.ShouldApplyCheckpointRefresh("session-a", null), "missing active session should reject checkpoint refresh");
    Require(!SavedStateWorkflowCoordinator.ShouldApplyCheckpointRefresh("", "session-a"), "blank captured session should reject checkpoint refresh");
    Require(SavedStateWorkflowCoordinator.CheckpointRefreshFailureStatus(new IOException("disk busy")) == "Checkpoint refresh failed: disk busy", "checkpoint refresh failure should produce a visible status");
}

static void AppShutdownSurvivesCleanupFailures()
{
    var shutdown = new CancellationTokenSource();
    var callbackCalls = 0;
    _ = shutdown.Token.Register(() =>
    {
        Interlocked.Increment(ref callbackCalls);
        throw new InvalidOperationException("simulated shutdown callback failure");
    });
    var service = new ThrowingTestDisposable();

    App.ShutdownServices(shutdown, service);

    Require(callbackCalls == 1, "app shutdown should still invoke registered cancellation callbacks");
    Require(service.DisposeCount == 1, "app shutdown should dispose its owned service after a callback failure");
    var cancellationDisposed = false;
    try
    {
        _ = shutdown.Token;
    }
    catch (ObjectDisposedException)
    {
        cancellationDisposed = true;
    }

    Require(cancellationDisposed, "app shutdown should dispose its cancellation source after cleanup failures");
}

static void VoiceNarrationDisposeWinsDelayedStart()
{
    using var factoryEntered = new ManualResetEventSlim();
    using var releaseFactory = new ManualResetEventSlim();
    var synthesizer = new TestVoiceNarrationSynthesizer();
    var factoryCalls = 0;
    var service = new VoiceNarrationService(() =>
    {
        Interlocked.Increment(ref factoryCalls);
        factoryEntered.Set();
        releaseFactory.Wait(TimeSpan.FromSeconds(5));
        return synthesizer;
    });

    var speaking = Task.Run(() => service.Speak(
        "A delayed narration request.",
        new VoiceNarrationOptions("Test Voice", 1, 75)));
    Require(factoryEntered.Wait(TimeSpan.FromSeconds(2)), "voice synthesizer construction did not enter the delayed factory");

    service.Dispose();
    releaseFactory.Set();
    Require(speaking.Wait(TimeSpan.FromSeconds(2)), "voice narration did not unwind after disposal");
    var result = speaking.GetAwaiter().GetResult();

    Require(!result.Ok && result.Status.Contains("disposed", StringComparison.OrdinalIgnoreCase), "a narration constructed after disposal should be rejected");
    Require(synthesizer.SpeakCount == 0, "a synthesizer returned after disposal must never start speaking");
    Require(synthesizer.CancelCount == 1 && synthesizer.DisposeCount == 1, "a rejected delayed synthesizer should be canceled and disposed exactly once");
    Require(!service.IsSpeaking, "disposed voice narration service should not retain a current synthesizer");

    var postDispose = service.Speak("post-dispose narration", new VoiceNarrationOptions("", 0, 100));
    Require(!postDispose.Ok, "post-dispose narration should be rejected");
    Require(service.InstalledVoiceNames().Count == 0, "post-dispose voice enumeration should be rejected");
    Require(factoryCalls == 1, "post-dispose voice calls should not create more synthesizers");
}

static void VoiceNarrationDisposalDrainsInFlightStart()
{
    using var startEntered = new ManualResetEventSlim();
    using var releaseStart = new ManualResetEventSlim();
    var synthesizer = new TestVoiceNarrationSynthesizer(startEntered, releaseStart);
    var service = new VoiceNarrationService(() => synthesizer);
    var speaking = Task.Run(() => service.Speak(
        "Narration whose synthesizer start is still in flight.",
        new VoiceNarrationOptions("Test Voice", 0, 80)));
    Require(startEntered.Wait(TimeSpan.FromSeconds(2)), "voice synthesizer did not enter its in-flight start");

    var disposing = Task.Run(service.Dispose);
    Require(!disposing.Wait(TimeSpan.FromMilliseconds(100)), "voice disposal should drain an in-flight synthesizer start before returning");
    releaseStart.Set();
    Require(Task.WaitAll([speaking, disposing], TimeSpan.FromSeconds(2)), "voice start and disposal did not finish after the start was released");
    var result = speaking.GetAwaiter().GetResult();

    Require(!result.Ok && result.Status.Contains("disposed", StringComparison.OrdinalIgnoreCase), "a start overtaken by disposal should not report success");
    Require(synthesizer.SpeakCount == 1, "the in-flight synthesizer start should execute only once");
    Require(synthesizer.CancelCount == 1 && synthesizer.DisposeCount == 1, "disposal should cancel and dispose the in-flight synthesizer exactly once");
    Require(!service.IsSpeaking, "disposal should leave no current narration session");
}

static void VoiceNarrationIsolatesSpeakingChangedFailures()
{
    var first = new TestVoiceNarrationSynthesizer();
    var second = new TestVoiceNarrationSynthesizer();
    var synthesizers = new Queue<TestVoiceNarrationSynthesizer>([first, second]);
    using var service = new VoiceNarrationService(() => synthesizers.Dequeue());
    var successfulCallbacks = 0;
    service.SpeakingChanged += () => throw new InvalidOperationException("simulated observer failure");
    service.SpeakingChanged += () => Interlocked.Increment(ref successfulCallbacks);

    var firstResult = service.Speak("First narration.", new VoiceNarrationOptions("Test Voice", 0, 80));
    Require(firstResult.Ok, "a throwing speaking-state observer should not fail narration startup");
    Require(successfulCallbacks == 1, "later speaking-state observers should still run after one throws");
    Require(service.IsSpeaking, "successful narration should own the first synthesizer");

    first.Complete();
    Require(successfulCallbacks == 2, "speech completion should safely notify every observer");
    Require(!service.IsSpeaking, "speech completion should clear the current synthesizer");
    Require(first.DisposeCount == 1, "completed synthesizer should be disposed exactly once");

    var secondResult = service.Speak("Second narration.", new VoiceNarrationOptions("", 0, 80));
    Require(secondResult.Ok, "voice service should remain usable after a throwing observer");
    service.Stop();
    Require(successfulCallbacks == 4, "start and stop should each notify non-throwing observers");
    Require(second.CancelCount == 1 && second.DisposeCount == 1, "stopped synthesizer should be canceled and disposed exactly once");
}

private sealed class ThrowingTestDisposable : IDisposable
{
    internal int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        throw new InvalidOperationException("simulated service disposal failure");
    }
}

private sealed class TestVoiceNarrationSynthesizer : IVoiceNarrationSynthesizer
{
    private readonly ManualResetEventSlim? speakEntered;
    private readonly ManualResetEventSlim? releaseSpeak;
    private int speakCount;
    private int cancelCount;
    private int disposeCount;

    internal TestVoiceNarrationSynthesizer(
        ManualResetEventSlim? speakEntered = null,
        ManualResetEventSlim? releaseSpeak = null)
    {
        this.speakEntered = speakEntered;
        this.releaseSpeak = releaseSpeak;
    }

    public event Action? SpeakCompleted;

    public int Rate { private get; set; }

    public int Volume { private get; set; }

    public string VoiceName { get; private set; } = "Test Voice";

    internal int SpeakCount => Volatile.Read(ref speakCount);

    internal int CancelCount => Volatile.Read(ref cancelCount);

    internal int DisposeCount => Volatile.Read(ref disposeCount);

    public IReadOnlyList<string> InstalledVoiceNames() => ["Test Voice"];

    public void SelectVoice(string voiceName)
    {
        VoiceName = voiceName;
    }

    public void SpeakAsync(string text)
    {
        Interlocked.Increment(ref speakCount);
        speakEntered?.Set();
        if (releaseSpeak is not null && !releaseSpeak.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting to release the test synthesizer start.");
        }
    }

    public void CancelAll()
    {
        Interlocked.Increment(ref cancelCount);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref disposeCount);
    }

    internal void Complete()
    {
        SpeakCompleted?.Invoke();
    }
}

}
