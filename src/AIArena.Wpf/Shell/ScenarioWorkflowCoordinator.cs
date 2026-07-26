using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed record GenerationPresetInfo(
    string Key,
    string Label,
    string Category,
    string Summary,
    string BestFor,
    string Risk,
    string RolePack,
    string Style,
    string Intensity,
    string Absurdity);

internal sealed class ScenarioWorkflowCoordinator
{
    private readonly Window owner;
    private readonly MatchGenerationService matchGeneration;
    private readonly WpfSettingsStore settingsStore;
    private readonly ComboBox randomSeedPresetPicker;
    private readonly ComboBox randomSeedRolePackPicker;
    private readonly ComboBox randomSeedStylePicker;
    private readonly ComboBox randomSeedIntensityPicker;
    private readonly ComboBox randomSeedAbsurdityPicker;
    private readonly TextBlock setupReadinessStatusText;
    private readonly Panel setupReadinessBadgeItems;
    private readonly Panel setupReadinessChecklistItems;
    private readonly Button copyCurrentSetupBriefButton;
    private readonly Button copyCurrentSetupSpecButton;
    private readonly TextBlock generationPresetStatusText;
    private readonly Button randomSeedButton;
    private readonly Button aiChoiceButton;
    private readonly Button currentTopicsButton;
    private readonly Button yoloScenarioButton;
    private readonly ComboBox generationHistoryFilterPicker;
    private readonly ComboBox generationHistoryPicker;
    private readonly TextBlock generationHistoryStatusText;
    private readonly Button replayGenerationButton;
    private readonly Button replayNewRunButton;
    private readonly Button copyGenerationSeedButton;
    private readonly Button copyGenerationBriefButton;
    private readonly Button copyGenerationSpecButton;
    private readonly Button copyGenerationDiffButton;
    private readonly Button copyGenerationRubricButton;
    private readonly Func<WpfSettings> settings;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<string, Button?, Func<CancellationToken, Task>, bool, Task> runCancelableArenaBusyAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    private bool isUpdating;
    private bool isAutoChatRunning;
    private bool isPopulatingGenerationHistory;
    private ArenaViewSnapshot? lastSetupSnapshot;

    public ScenarioWorkflowCoordinator(
        Window owner,
        MatchGenerationService matchGeneration,
        WpfSettingsStore settingsStore,
        ComboBox randomSeedPresetPicker,
        ComboBox randomSeedRolePackPicker,
        ComboBox randomSeedStylePicker,
        ComboBox randomSeedIntensityPicker,
        ComboBox randomSeedAbsurdityPicker,
        TextBlock setupReadinessStatusText,
        Panel setupReadinessBadgeItems,
        Panel setupReadinessChecklistItems,
        Button copyCurrentSetupBriefButton,
        Button copyCurrentSetupSpecButton,
        TextBlock generationPresetStatusText,
        Button randomSeedButton,
        Button aiChoiceButton,
        Button currentTopicsButton,
        Button yoloScenarioButton,
        ComboBox generationHistoryFilterPicker,
        ComboBox generationHistoryPicker,
        TextBlock generationHistoryStatusText,
        Button replayGenerationButton,
        Button replayNewRunButton,
        Button copyGenerationSeedButton,
        Button copyGenerationBriefButton,
        Button copyGenerationSpecButton,
        Button copyGenerationDiffButton,
        Button copyGenerationRubricButton,
        Func<WpfSettings> settings,
        Func<CoreSessionSummary?> activeSession,
        Func<ThemePalette> theme,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<bool> isRenderingSnapshot,
        Func<bool> isArenaBusy,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Func<string?, Task> loadSessionsAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus,
        Func<string, Button?, Func<CancellationToken, Task>, bool, Task>? runCancelableArenaBusyAsync = null)
    {
        this.owner = owner;
        this.matchGeneration = matchGeneration;
        this.settingsStore = settingsStore;
        this.randomSeedPresetPicker = randomSeedPresetPicker;
        this.randomSeedRolePackPicker = randomSeedRolePackPicker;
        this.randomSeedStylePicker = randomSeedStylePicker;
        this.randomSeedIntensityPicker = randomSeedIntensityPicker;
        this.randomSeedAbsurdityPicker = randomSeedAbsurdityPicker;
        this.setupReadinessStatusText = setupReadinessStatusText;
        this.setupReadinessBadgeItems = setupReadinessBadgeItems;
        this.setupReadinessChecklistItems = setupReadinessChecklistItems;
        this.copyCurrentSetupBriefButton = copyCurrentSetupBriefButton;
        this.copyCurrentSetupSpecButton = copyCurrentSetupSpecButton;
        this.generationPresetStatusText = generationPresetStatusText;
        this.randomSeedButton = randomSeedButton;
        this.aiChoiceButton = aiChoiceButton;
        this.currentTopicsButton = currentTopicsButton;
        this.yoloScenarioButton = yoloScenarioButton;
        this.generationHistoryFilterPicker = generationHistoryFilterPicker;
        this.generationHistoryPicker = generationHistoryPicker;
        this.generationHistoryStatusText = generationHistoryStatusText;
        this.replayGenerationButton = replayGenerationButton;
        this.replayNewRunButton = replayNewRunButton;
        this.copyGenerationSeedButton = copyGenerationSeedButton;
        this.copyGenerationBriefButton = copyGenerationBriefButton;
        this.copyGenerationSpecButton = copyGenerationSpecButton;
        this.copyGenerationDiffButton = copyGenerationDiffButton;
        this.copyGenerationRubricButton = copyGenerationRubricButton;
        this.settings = settings;
        this.activeSession = activeSession;
        this.theme = theme;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.isArenaBusy = isArenaBusy;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.runCancelableArenaBusyAsync = runCancelableArenaBusyAsync
            ?? ((status, button, action, allowDuringAutoChat) =>
                runArenaBusyAsync(status, button, () => action(CancellationToken.None), allowDuringAutoChat));
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public GenerationHistoryItem? SelectedGenerationHistory =>
        generationHistoryPicker.SelectedItem is ComboBoxItem { Tag: GenerationHistoryItem item }
            ? item
            : null;

    internal static bool GenerationHistoryActionEnabled(bool hasItem, bool arenaBusy, bool autoChatRunning)
    {
        return hasItem && !arenaBusy;
    }

    internal static bool GenerationHistoryCopyActionEnabled(bool hasItem)
    {
        return hasItem;
    }

    internal static bool GenerationHistoryPickerEnabled(bool arenaBusy, bool autoChatRunning)
    {
        return !arenaBusy || autoChatRunning;
    }

    public void InitializeControls()
    {
        var current = settings();
        isUpdating = true;
        try
        {
            ShellUiHelpers.SelectComboTag(randomSeedPresetPicker, current.RandomSeedPreset);
            ShellUiHelpers.SelectComboTag(randomSeedRolePackPicker, current.RandomSeedRolePack);
            ShellUiHelpers.SelectComboTag(randomSeedStylePicker, current.RandomSeedStyle);
            ShellUiHelpers.SelectComboTag(randomSeedIntensityPicker, current.RandomSeedIntensity);
            ShellUiHelpers.SelectComboTag(randomSeedAbsurdityPicker, current.RandomSeedAbsurdity);
            ShellUiHelpers.SelectComboTag(generationHistoryFilterPicker, "all");
        }
        finally
        {
            isUpdating = false;
        }

        DecoratePresetPickerItems();
        UpdateGenerationHistoryActions();
        UpdateSetupFeedback();
    }

    public void OnRandomSeedPresetChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        var current = settings();
        var preset = ShellUiHelpers.SelectedComboTag(randomSeedPresetPicker, "manual");
        current.RandomSeedPreset = preset;
        if (!preset.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            var config = RandomSeedPresetValues(preset);
            isUpdating = true;
            try
            {
                ShellUiHelpers.SelectComboTag(randomSeedRolePackPicker, config.RolePack);
                ShellUiHelpers.SelectComboTag(randomSeedStylePicker, config.Style);
                ShellUiHelpers.SelectComboTag(randomSeedIntensityPicker, config.Intensity);
                ShellUiHelpers.SelectComboTag(randomSeedAbsurdityPicker, config.Absurdity);
            }
            finally
            {
                isUpdating = false;
            }

            current.RandomSeedRolePack = config.RolePack;
            current.RandomSeedStyle = config.Style;
            current.RandomSeedIntensity = config.Intensity;
            current.RandomSeedAbsurdity = config.Absurdity;
        }

        settingsStore.Save(current);
        UpdateSetupFeedback();
    }

    public void OnRandomSeedOptionsChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        var current = settings();
        current.RandomSeedRolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
        current.RandomSeedStyle = ShellUiHelpers.SelectedComboTag(randomSeedStylePicker, "auto");
        current.RandomSeedIntensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
        current.RandomSeedAbsurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
        current.RandomSeedPreset = "manual";

        isUpdating = true;
        try
        {
            ShellUiHelpers.SelectComboTag(randomSeedPresetPicker, "manual");
        }
        finally
        {
            isUpdating = false;
        }

        settingsStore.Save(current);
        UpdateSetupFeedback();
    }

    public async Task GenerateRandomSeedAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var rolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
        var style = ShellUiHelpers.SelectedComboTag(randomSeedStylePicker, "auto");
        var intensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
        var absurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
        await runCancelableArenaBusyAsync($"Generating {RandomSeedOptionLabel(style, intensity, rolePack, absurdity)} random seed...", randomSeedButton, async cancellationToken =>
        {
            var result = await matchGeneration.GenerateRandomSeedAsync(
                session.Id,
                style,
                intensity,
                rolePack,
                absurdity,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = result.Ok
                ? $"Random seed match generated: {result.Label}"
                : $"Random seed failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task GenerateAiChoiceAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var prompt = AiChoicePromptDialog.Show(
            owner,
            theme(),
            settings().AiChoiceTopicPrompt);
        if (prompt is null)
        {
            return;
        }

        var current = settings();
        current.AiChoiceTopicPrompt = prompt.Trim();
        settingsStore.Save(current);
        UpdateSetupFeedback();

        await runCancelableArenaBusyAsync("Asking narrator for AI Choice match...", aiChoiceButton, async cancellationToken =>
        {
            var rolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
            var intensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
            var absurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
            var result = await matchGeneration.GenerateAiChoiceAsync(
                session.Id,
                rolePack,
                intensity,
                absurdity,
                current.AiChoiceTopicPrompt,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = result.Ok
                ? $"AI Choice match generated: {result.Label}"
                : $"AI Choice failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task GenerateYoloSeedAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Wild Seed",
            "Generate a bolder local seeded setup for the arena simulation?\n\nLocked fields and the current transcript will be preserved.",
            "Generate",
            tone: ConfirmDialogTone.Normal);
        if (!confirm)
        {
            return;
        }

        await runCancelableArenaBusyAsync("Generating Wild Seed...", yoloScenarioButton, async cancellationToken =>
        {
            var rolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
            var intensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
            var absurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
            var result = await matchGeneration.GenerateYoloSeedAsync(
                session.Id,
                rolePack,
                intensity,
                absurdity,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = result.Ok
                ? $"Wild Seed generated: {result.Label} - {result.Seed}"
                : $"Wild Seed failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task GenerateCurrentTopicsSeedAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runCancelableArenaBusyAsync("Generating Current Topics match...", currentTopicsButton, async cancellationToken =>
        {
            var preset = ShellUiHelpers.SelectedComboTag(randomSeedPresetPicker, "manual");
            var rolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
            var intensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
            var absurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
            var result = await matchGeneration.GenerateCurrentTopicsSeedAsync(
                session.Id,
                rolePack,
                intensity,
                absurdity,
                CurrentTopicsSearchQuery(preset),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = result.Ok
                ? $"Current Topics match generated: {result.Label}"
                : $"Current Topics failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task ReplayGenerationAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        await runCancelableArenaBusyAsync($"Replaying generated match: {item.Label}...", replayGenerationButton, async cancellationToken =>
        {
            var result = await matchGeneration.ReplayGenerationAsync(session.Id, item.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = result.Ok
                ? $"Replayed generated match: {item.Label}"
                : $"Replay failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task ReplayGenerationToNewRunAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        await runCancelableArenaBusyAsync($"Creating replay run: {item.Label}...", replayNewRunButton, async cancellationToken =>
        {
            var result = await matchGeneration.ReplayGenerationToNewSessionAsync(session.Id, item.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Ok)
            {
                await refreshActiveSessionAsync($"New replay run failed: {result.Error}");
                return;
            }

            await loadSessionsAsync(result.Label);
            setLoadStatus($"Created replay run: {result.Label}");
            setArenaRunStatus($"Replay run ready: {item.Label}");
        }, true);
    }

    public void CopyGenerationSeed()
    {
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        var seed = string.IsNullOrWhiteSpace(item.ScenarioSeed) || item.ScenarioSeed == "-"
            ? item.PersonaSeed
            : item.ScenarioSeed;
        if (string.IsNullOrWhiteSpace(seed) || seed == "-")
        {
            SetBothStatuses("Selected generation has no seed to copy.");
            return;
        }

        if (!GenerationSeedIsDeterministic(item))
        {
            var sourceLabel = CapturedGenerationLabel(item.Kind);
            CopyTextToClipboard(
                item.Id,
                $"{sourceLabel} has no deterministic seed. Copied replay id: {item.Id}",
                "Copy seed failed");
        }
        else
        {
            CopyTextToClipboard(seed, $"Copied generation seed: {seed}", "Copy seed failed");
        }
    }

    public void CopyGenerationBrief()
    {
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            return;
        }

        CopyTextToClipboard(
            GenerationHistoryBrief(item),
            $"Copied generated match brief: {ShortHistoryText(item.Label, 48)}",
            "Copy brief failed");
    }

    public void CopyGenerationSpec()
    {
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        CopyTextToClipboard(
            GenerationHistorySpec(item, lastSetupSnapshot),
            $"Copied generated setup spec: {ShortHistoryText(item.Label, 48)}",
            "Copy spec failed");
    }

    public void CopyGenerationDiff()
    {
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        CopyTextToClipboard(
            GenerationHistoryDiff(item, lastSetupSnapshot),
            $"Copied generated setup diff: {ShortHistoryText(item.Label, 48)}",
            "Copy diff failed");
    }

    public void CopyGenerationRubric()
    {
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            SetBothStatuses("No generated match selected.");
            return;
        }

        CopyTextToClipboard(
            GenerationHistoryRubric(item, lastSetupSnapshot),
            $"Copied generated match rubric: {ShortHistoryText(item.Label, 48)}",
            "Copy rubric failed");
    }

    public void CopyCurrentSetupBrief()
    {
        if (lastSetupSnapshot is null)
        {
            SetBothStatuses("No current setup snapshot to copy.");
            return;
        }

        CopyTextToClipboard(
            CustomMatchSummaryCoordinator.CurrentSetupBrief(lastSetupSnapshot),
            "Copied current match setup brief.",
            "Copy current setup brief failed");
    }

    public void CopyCurrentSetupSpec()
    {
        if (lastSetupSnapshot is null)
        {
            SetBothStatuses("No current setup snapshot to copy.");
            return;
        }

        CopyTextToClipboard(
            CustomMatchSummaryCoordinator.CurrentSetupSpec(lastSetupSnapshot),
            "Copied current match setup JSON spec.",
            "Copy current setup spec failed");
    }

    public void OnGenerationHistoryFilterChanged()
    {
        if (isUpdating || isRenderingSnapshot())
        {
            return;
        }

        if (lastSetupSnapshot is not null)
        {
            PopulateGenerationHistory(lastSetupSnapshot);
        }
        else
        {
            UpdateGenerationHistoryActions();
        }
    }

    public void PopulateGenerationHistory(ArenaViewSnapshot snapshot)
    {
        lastSetupSnapshot = snapshot;
        var previousId = SelectedGenerationHistory?.Id ?? "";
        var filter = GenerationHistoryFilter();
        var filteredHistory = FilterGenerationHistory(snapshot.GenerationHistory, filter).Take(20).ToArray();
        generationHistoryPicker.Items.Clear();
        foreach (var item in filteredHistory)
        {
            generationHistoryPicker.Items.Add(new ComboBoxItem
            {
                Content = GenerationHistoryLabel(item),
                Tag = item,
                ToolTip = GenerationHistoryTooltip(item, snapshot)
            });
        }

        if (generationHistoryPicker.Items.Count == 0)
        {
            generationHistoryPicker.Items.Add(new ComboBoxItem
            {
                Content = "No history yet",
                IsEnabled = false
            });
        }

        isPopulatingGenerationHistory = true;
        try
        {
            generationHistoryPicker.SelectedIndex = PreferredGenerationHistoryIndex(
                generationHistoryPicker.Items
                    .OfType<ComboBoxItem>()
                    .Select(item => item.Tag as GenerationHistoryItem)
                    .ToArray(),
                previousId);
        }
        finally
        {
            isPopulatingGenerationHistory = false;
        }

        SetGenerationHistoryStatus(
            SelectedGenerationHistory is { } selected
                ? GenerationSelectionStatus(selected, snapshot)
                : GenerationHistoryCountStatus(
                    generationHistoryPicker.Items.OfType<ComboBoxItem>().Count(item => item.Tag is GenerationHistoryItem),
                    snapshot.GenerationHistory.Count,
                    FilterGenerationHistory(snapshot.GenerationHistory, filter).Count,
                    filter),
            SelectedGenerationHistory is { } tooltipItem
                ? GenerationHistoryTooltip(tooltipItem, snapshot)
                : GenerationHistoryCountStatus(
                    generationHistoryPicker.Items.OfType<ComboBoxItem>().Count(item => item.Tag is GenerationHistoryItem),
                    snapshot.GenerationHistory.Count,
                    FilterGenerationHistory(snapshot.GenerationHistory, filter).Count,
                    filter));
        UpdateGenerationHistoryActions();
        UpdateSetupFeedback();
    }

    public void OnGenerationHistorySelectionChanged()
    {
        if (isPopulatingGenerationHistory)
        {
            return;
        }

        UpdateGenerationHistoryActions();
        var item = SelectedGenerationHistory;
        if (item is null)
        {
            return;
        }

        var status = GenerationSelectionStatus(item, lastSetupSnapshot);
        SetGenerationHistoryStatus(status, GenerationHistoryTooltip(item, lastSetupSnapshot));
        setLoadStatus(status);
    }

    public void UpdateGenerationHistoryActions()
    {
        var hasItem = SelectedGenerationHistory is not null;
        var replayEnabled = GenerationHistoryActionEnabled(hasItem, isArenaBusy(), isAutoChatRunning);
        var copyEnabled = GenerationHistoryCopyActionEnabled(hasItem);
        replayGenerationButton.IsEnabled = replayEnabled;
        replayNewRunButton.IsEnabled = replayEnabled;
        copyGenerationSeedButton.IsEnabled = copyEnabled;
        copyGenerationBriefButton.IsEnabled = copyEnabled;
        copyGenerationSpecButton.IsEnabled = copyEnabled;
        copyGenerationDiffButton.IsEnabled = copyEnabled;
        copyGenerationRubricButton.IsEnabled = copyEnabled;
        UpdateGenerationHistoryActionTooltips(SelectedGenerationHistory);
    }

    public void UpdateBusyState(bool busy, bool autoChatRunning)
    {
        isAutoChatRunning = autoChatRunning;
        randomSeedButton.IsEnabled = !busy;
        randomSeedPresetPicker.IsEnabled = !busy;
        randomSeedRolePackPicker.IsEnabled = !busy;
        randomSeedStylePicker.IsEnabled = !busy;
        randomSeedIntensityPicker.IsEnabled = !busy;
        randomSeedAbsurdityPicker.IsEnabled = !busy;
        generationHistoryFilterPicker.IsEnabled = GenerationHistoryPickerEnabled(busy, autoChatRunning);
        generationHistoryPicker.IsEnabled = GenerationHistoryPickerEnabled(busy, autoChatRunning);
        aiChoiceButton.IsEnabled = !busy;
        currentTopicsButton.IsEnabled = !busy;
        yoloScenarioButton.IsEnabled = !busy;
        UpdateCurrentSetupCopyActions();
        UpdateGenerationHistoryActions();
        UpdateSetupFeedback();
    }

    private void UpdateSetupFeedback()
    {
        var preset = ShellUiHelpers.SelectedComboTag(randomSeedPresetPicker, "manual");
        var rolePack = ShellUiHelpers.SelectedComboTag(randomSeedRolePackPicker, "auto");
        var style = ShellUiHelpers.SelectedComboTag(randomSeedStylePicker, "auto");
        var intensity = ShellUiHelpers.SelectedComboTag(randomSeedIntensityPicker, "normal");
        var absurdity = ShellUiHelpers.SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
        var topicPrompt = settings().AiChoiceTopicPrompt.Trim();
        var topicSummary = string.IsNullOrWhiteSpace(topicPrompt) ? "" : $" AI Choice topic: {ShortHistoryText(topicPrompt, 52)}.";
        var summary = GenerationControlSummary(preset, rolePack, style, intensity, absurdity) + topicSummary;

        generationPresetStatusText.Text = summary;
        generationPresetStatusText.ToolTip = GenerationControlTooltip(preset, rolePack, style, intensity, absurdity, topicPrompt);
        randomSeedButton.ToolTip = $"Generate a local, replayable setup from this recipe. {summary}";
        aiChoiceButton.ToolTip = $"Ask the narrator model to design a setup using this recipe. {summary}";
        currentTopicsButton.ToolTip = $"Search locally for current topics, then ask AI Choice to design a setup from the sources. {summary}";
        yoloScenarioButton.ToolTip = $"Generate a bolder local setup using this recipe. {summary}";

        if (lastSetupSnapshot is null)
        {
            setupReadinessStatusText.Text = "Setup readiness will appear after a session loads.";
            setupReadinessStatusText.ToolTip = setupReadinessStatusText.Text;
            AutomationProperties.SetHelpText(setupReadinessStatusText, setupReadinessStatusText.Text);
            PopulateReadinessBadges(
            [
                new SetupReadinessBadge("State", "Awaiting session", "neutral", "Load or create a session before running preflight.")
            ]);
            PopulateReadinessChecklist(
            [
                new SetupReadinessChecklistItem("Preflight", "Load a session to inspect blockers and warnings.", "neutral")
            ]);
            UpdateCurrentSetupCopyActions();
            return;
        }

        var report = BuildSetupReadinessReport(lastSetupSnapshot, rolePack, style, intensity, absurdity);
        setupReadinessStatusText.Text = report.Status;
        setupReadinessStatusText.ToolTip = report.Tooltip;
        AutomationProperties.SetHelpText(setupReadinessStatusText, report.Tooltip);
        PopulateReadinessBadges(report.Badges);
        PopulateReadinessChecklist(report.Checklist);
        UpdateCurrentSetupCopyActions();
    }

    private void PopulateReadinessBadges(IReadOnlyList<SetupReadinessBadge> badges)
    {
        setupReadinessBadgeItems.Children.Clear();
        // When every badge reads "ready" the one-line status already tells the story,
        // so the badge row collapses to give the previews more room.
        var allReady = badges.Count > 0 && badges.All(badge => badge.Kind == "ready");
        setupReadinessBadgeItems.Visibility = allReady ? Visibility.Collapsed : Visibility.Visible;
        if (allReady)
        {
            return;
        }

        foreach (var badge in badges)
        {
            setupReadinessBadgeItems.Children.Add(CreateReadinessBadge(badge));
        }
    }

    private Border CreateReadinessBadge(SetupReadinessBadge badge)
    {
        var accent = badge.Kind switch
        {
            "ready" => resourceBrush("GammaAccentBrush"),
            "warning" => resourceBrush("BetaAccentBrush"),
            "danger" => resourceBrush("DangerTextBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
        var text = $"{badge.Label}: {badge.Value}";
        var tooltip = string.IsNullOrWhiteSpace(badge.Tooltip) ? text : $"{text} - {badge.Tooltip}";
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private void PopulateReadinessChecklist(IReadOnlyList<SetupReadinessChecklistItem> items)
    {
        setupReadinessChecklistItems.Children.Clear();
        var allReady = items.Count > 0 && items.All(item => item.Kind == "ready");
        setupReadinessChecklistItems.Visibility = allReady ? Visibility.Collapsed : Visibility.Visible;
        if (allReady)
        {
            return;
        }

        foreach (var item in items)
        {
            setupReadinessChecklistItems.Children.Add(CreateReadinessChecklistRow(item));
        }
    }

    private Border CreateReadinessChecklistRow(SetupReadinessChecklistItem item)
    {
        var accent = item.Kind switch
        {
            "ready" => resourceBrush("GammaAccentBrush"),
            "warning" => resourceBrush("BetaAccentBrush"),
            "danger" => resourceBrush("DangerTextBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = item.Label,
            Foreground = accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var body = new TextBlock
        {
            Text = item.Value,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.06),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.30),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5, 8, 6),
            Margin = new Thickness(0, 0, 0, 5),
            ToolTip = $"{item.Label}: {item.Value}",
            Child = row
        };
    }

    private void UpdateCurrentSetupCopyActions()
    {
        var hasSnapshot = lastSetupSnapshot is not null;
        copyCurrentSetupBriefButton.IsEnabled = hasSnapshot;
        copyCurrentSetupSpecButton.IsEnabled = hasSnapshot;
        copyCurrentSetupBriefButton.ToolTip = hasSnapshot
            ? "Copy the current match setup as a readable brief"
            : "Load a session before copying the current match setup";
        copyCurrentSetupSpecButton.ToolTip = hasSnapshot
            ? "Copy the current match setup as a JSON setup receipt"
            : "Load a session before copying the current match setup spec";
    }

    private void SetBothStatuses(string status)
    {
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    private void CopyTextToClipboard(string text, string successStatus, string failurePrefix)
    {
        if (TrySetClipboardText(text))
        {
            SetBothStatuses(successStatus);
            return;
        }

        SetBothStatuses($"{failurePrefix}: clipboard is unavailable. Try again.");
    }

    internal static bool TrySetClipboardText(string text, Action<string>? setText = null)
    {
        return ShellClipboard.TrySetText(text, setText);
    }

    private void SetGenerationHistoryStatus(string text, string? tooltip = null)
    {
        generationHistoryStatusText.Text = text;
        generationHistoryStatusText.ToolTip = tooltip ?? text;
    }

    private void UpdateGenerationHistoryActionTooltips(GenerationHistoryItem? item)
    {
        if (item is null)
        {
            copyGenerationSeedButton.Content = "Copy Seed";
            AutomationProperties.SetName(copyGenerationSeedButton, "Copy generation seed");
            AutomationProperties.SetHelpText(copyGenerationSeedButton, "Select a generated match to copy its deterministic seed or replay id.");
            replayGenerationButton.ToolTip = "Select a generated match to replay it without calling a model";
            replayNewRunButton.ToolTip = "Select a generated match to create a clean comparison run";
            copyGenerationSeedButton.ToolTip = "Select a generated match to copy its seed";
            copyGenerationBriefButton.ToolTip = "Select a generated match to copy its brief";
            copyGenerationSpecButton.ToolTip = "Select a generated match to copy its JSON setup receipt";
            copyGenerationDiffButton.ToolTip = "Select a generated match to copy a setup diff";
            copyGenerationRubricButton.ToolTip = "Select a generated match to copy an evaluation rubric";
            return;
        }

        var label = ShortHistoryText(item.Label, 36);
        var lockWarning = ReplayLockWarning(lastSetupSnapshot);
        var suffix = string.IsNullOrWhiteSpace(lockWarning) ? "" : $" {lockWarning}";
        var seedIsDeterministic = GenerationSeedIsDeterministic(item);
        copyGenerationSeedButton.Content = seedIsDeterministic ? "Copy Seed" : "Copy Replay ID";
        AutomationProperties.SetName(copyGenerationSeedButton, seedIsDeterministic ? "Copy generation seed" : "Copy generation replay ID");
        AutomationProperties.SetHelpText(
            copyGenerationSeedButton,
            seedIsDeterministic
                ? "Copy the deterministic seed for the selected generated match."
                : "Copy the saved replay ID for this captured-output generation.");
        replayGenerationButton.ToolTip = $"Replay {label} without calling a model.{suffix}";
        replayNewRunButton.ToolTip = $"Create a clean run from {label}.{suffix}";
        copyGenerationSeedButton.ToolTip = !seedIsDeterministic
            ? $"{CapturedGenerationLabel(item.Kind)} has no deterministic seed; copy replay id from {label}"
            : $"Copy seed from {label}";
        copyGenerationBriefButton.ToolTip = $"Copy brief from {label}";
        copyGenerationSpecButton.ToolTip = $"Copy JSON setup receipt from {label}";
        copyGenerationDiffButton.ToolTip = $"Copy setup diff for {label}";
        copyGenerationRubricButton.ToolTip = $"Copy evaluation rubric for {label}";
    }

    private string GenerationHistoryFilter()
    {
        return NormalizeGenerationHistoryFilter(ShellUiHelpers.SelectedComboTag(generationHistoryFilterPicker, "all"));
    }

    private void DecoratePresetPickerItems()
    {
        foreach (var item in randomSeedPresetPicker.Items.OfType<ComboBoxItem>())
        {
            var key = item.Tag?.ToString() ?? "manual";
            var info = GenerationPresetDetails(key);
            item.Content = info.Label;
            item.ToolTip = GenerationPresetTooltip(info);
            AutomationProperties.SetName(item, $"{info.Label} preset");
            AutomationProperties.SetHelpText(item, $"{info.Category}. {info.Summary} Best for: {info.BestFor} Risk: {info.Risk}");
        }
    }

    internal static IReadOnlyList<GenerationPresetInfo> GenerationPresetCatalog { get; } =
    [
        new("manual", "Manual", "Custom", "Directly use the visible tuning controls.", "Careful hand-built setup tuning.", "No preset guardrails; review the generated recipe yourself.", "auto", "auto", "normal", "grounded"),
        new("hostile_review", "Hostile Review", "Adversarial", "A red-team room that attacks weak assumptions without going fully chaotic.", "Hardening a recommendation before trust or launch decisions.", "Can over-focus on critique unless the operator asks for a decision.", "red_team", "adversarial", "spicy", "grounded"),
        new("evidence_trial", "Evidence Trial", "Evidence", "A research review that separates evidence, inference, and assumption.", "Fact-sensitive topics, source triage, and claim hygiene.", "May move slowly if the prompt needs creative ideation first.", "scientific_review", "research", "sharp", "grounded"),
        new("socratic_audit", "Socratic Audit", "Safety", "A philosophical safety audit with sharp follow-up questions.", "Surfacing hidden assumptions and failure modes.", "Can become abstract unless the operator asks for concrete next checks.", "safety_audit", "philosophical", "sharp", "grounded"),
        new("incident_speedrun", "Incident Speedrun", "Operations", "A concise incident room tuned for fast triage.", "Outage, security, support, and escalation drills.", "The one-line pressure can compress nuance; use follow-up turns for detail.", "incident_response", "incident", "one_line", "grounded"),
        new("bureaucracy_inferno", "Bureaucracy Inferno", "Absurd governance", "A maximum-absurdity legal maze for policy theatre and process stress.", "Testing whether agents can stay useful inside procedural chaos.", "High theatre risk; keep evidence and decision criteria visible.", "absurd_lab", "legal", "chaos", "maximum"),
        new("alien_courtroom", "Alien Courtroom", "Absurd debate", "A strange philosophical courtroom with strong adversarial pressure.", "Creative argument testing and premise stress under unusual frames.", "Can drift into spectacle; use narrator judging or operator receipts.", "absurd_lab", "philosophical", "spicy", "maximum"),
        new("meme_tribunal", "Meme Tribunal", "Absurd compression", "A one-line creative tribunal where roles must be vivid and brief.", "Testing concise reasoning, voice adherence, and punchy disagreement.", "May sacrifice depth; replay with a grounded preset for validation.", "absurd_lab", "creative", "one_line", "maximum"),
        new("paranoid_compliance", "Paranoid Compliance", "Governance", "A legal safety audit with odd persona pressure.", "Policy review, compliance edge cases, and adversarial risk framing.", "Can produce defensive overblocking unless success criteria are clear.", "safety_audit", "legal", "sharp", "odd"),
        new("consensus_trap", "Consensus Trap", "Debate quality", "A balanced room designed to expose premature agreement.", "Consensus-risk drills and stronger objection generation.", "May need a private role reset if agents converge too quickly.", "balanced", "philosophical", "sharp", "odd"),
        new("chaos_room", "Chaos Room", "Absurd systems", "A technical absurd lab with maximum pressure.", "Stress-testing technical claims under noisy, high-friction roles.", "High role-drift risk; watch the friction and evidence diagnostics.", "absurd_lab", "technical", "chaos", "maximum"),
        new("one_line_mayhem", "One-line Mayhem", "Absurd compression", "A creative absurd lab where each contribution must stay tight.", "Fast demos, comedic stress tests, and forced prioritization.", "Short answers can hide assumptions; follow with an evidence pass.", "absurd_lab", "creative", "one_line", "maximum"),
        new("model_duel", "Model Duel", "Benchmark", "A benchmark-style duel for comparing model behavior fairly.", "Blind or semi-blind local model comparisons.", "Style bias can leak into judging; keep rubric and reveal timing explicit.", "benchmark_duel", "technical", "sharp", "grounded"),
        new("red_team_gauntlet", "Red-Team Gauntlet", "Adversarial", "A chaotic red-team run that pressures the strongest claims.", "Security, abuse, safety, and robustness drills.", "Can become all-attack; ask for reversible next steps before ending.", "red_team", "red-team", "chaos", "odd"),
        new("tool_reliability_trial", "Tool Reliability Trial", "Tool governance", "A technical tool-ops setup that tests source, cache, and failure handling.", "Internet/tool workflow audits and reliability checks.", "Tool chatter can crowd out decisions; use scope gates and trace packets.", "tool_ops", "technical", "sharp", "grounded"),
        new("governance_board", "Governance Board", "Governance", "A policy board with odd pressure and public accountability.", "Reviewing standards, escalation paths, and organizational risk.", "May sound official without evidence; require named owners and tests.", "governance_board", "legal", "spicy", "odd"),
        new("weird_science_panel", "Weird Science Panel", "Research absurdity", "A chaotic scientific panel with absurd persona pressure.", "Creative science-policy prompts and unusual hypothesis stress tests.", "Can overfit to novelty; ask for falsifiable conditions.", "scientific_review", "scientific", "chaos", "absurd"),
        new("product_trust_room", "Product Trust Room", "Product risk", "A grounded product-risk room for launch trust decisions.", "Product, safety, UX, and trust tradeoff reviews.", "Can soften disagreement; add a dissent intervention when needed.", "product_risk", "product", "spicy", "grounded"),
        new("policy_crisis_room", "Policy Crisis Room", "Current topics", "A policy/legal room for fresh regulatory flashpoints and public-interest tradeoffs.", "AI regulation, court rulings, emergency policy shifts, and institutional response.", "Can sound official without grounding; compare statutes, agencies, and affected groups.", "legal_policy", "legal", "chaos", "grounded"),
        new("market_shock", "Market Shock", "Current topics", "A business-risk room for sudden market moves and fragile economic claims.", "Earnings surprises, sector shocks, supply-chain stress, and financial narratives.", "Can overstate causality from weak signals; separate price action from explanation.", "product_risk", "product", "sharp", "grounded"),
        new("tech_ethics_hearing", "Tech Ethics Hearing", "Current topics", "A public-interest hearing for emerging technology harms, safeguards, and accountability.", "AI releases, platform failures, privacy incidents, safety research, and public response.", "Can moralize too quickly; ask what evidence would change the remedy.", "safety_audit", "safety", "spicy", "grounded"),
        new("geopolitical_risk_desk", "Geopolitical Risk Desk", "Current topics", "A red-team risk desk for volatile international claims and second-order effects.", "Conflict escalation, sanctions, elections, cyber incidents, and alliance risk.", "Can drift into speculation; keep confidence levels and source dates visible.", "red_team", "red-team", "sharp", "grounded"),
        new("black_box_audit", "Black-Box Audit", "Safety", "A technical safety audit for unknown systems and hidden failure modes.", "Model behavior investigations, opaque tools, and unknown constraints.", "Can stall in uncertainty; demand testable probes and stop criteria.", "safety_audit", "technical", "sharp", "grounded"),
        new("approval_maze", "Approval Maze", "Tool governance", "A legal/tool-ops maze that stress-tests approval scope and source use.", "Human-in-the-loop approval drills and internet/tool policy checks.", "High process friction; keep the operator's allowed source boundaries explicit.", "tool_ops", "legal", "chaos", "odd"),
        new("launch_war_room", "Launch War Room", "Product risk", "A high-pressure product room for launch readiness and rollback thinking.", "Release decisions, go/no-go reviews, and incident-prevention drills.", "Can rush to action; require owner, risk, rollback, and evidence.", "product_risk", "product", "chaos", "grounded"),
        new("template_forge", "Template Forge", "Builder", "A technical architecture room for shaping reusable arena templates.", "Turning one good setup into reusable presets, rubrics, or workflows.", "May optimize the template instead of the current answer; keep scope clear.", "technical_architecture", "technical", "spicy", "odd"),
        new("memory_handoff", "Memory Handoff", "State continuity", "A research-flavored setup for durable assumptions, notes, and next checks.", "Long-running investigations, replay baselines, and session handoffs.", "Can become archival; ask each agent what changes the next decision.", "balanced", "research", "spicy", "absurd")
    ];

    internal static (string RolePack, string Style, string Intensity, string Absurdity) RandomSeedPresetValues(string preset)
    {
        var info = GenerationPresetDetails(preset);
        return (info.RolePack, info.Style, info.Intensity, info.Absurdity);
    }

    internal static string CurrentTopicsSearchQuery(string preset)
    {
        return NormalizeChoice(preset, "manual") switch
        {
            "policy_crisis_room" => "latest AI policy regulation court ruling today",
            "market_shock" => "latest market shock technology stocks economy today",
            "tech_ethics_hearing" => "latest AI ethics privacy safety incident today",
            "geopolitical_risk_desk" => "latest geopolitical cyber security risk today",
            "governance_board" or "approval_maze" or "paranoid_compliance" => "latest policy regulation accountability news today",
            "product_trust_room" or "launch_war_room" => "latest technology product trust safety news today",
            "tool_reliability_trial" or "black_box_audit" => "latest AI system reliability incident today",
            _ => "latest AI technology policy market news today"
        };
    }

    internal static GenerationPresetInfo GenerationPresetDetails(string preset)
    {
        var key = NormalizeChoice(preset, "manual");
        return GenerationPresetCatalog.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? GenerationPresetCatalog[0];
    }

    internal static string GenerationPresetTooltip(GenerationPresetInfo info)
    {
        return string.Join(Environment.NewLine,
            $"{info.Label} - {info.Category}",
            info.Summary,
            $"Best for: {info.BestFor}",
            $"Risk: {info.Risk}",
            $"Recipe: {DisplayOption(info.RolePack)} pack / {DisplayOption(info.Style)} / {DisplayOption(info.Intensity)} pressure / {DisplayOption(info.Absurdity)} personas");
    }

    internal static IReadOnlyList<string> GenerationPresetReceiptLines(string preset)
    {
        var info = GenerationPresetDetails(preset);
        return
        [
            $"AI Arena preset: {info.Label}",
            $"Category: {info.Category}",
            $"Summary: {info.Summary}",
            $"Best for: {info.BestFor}",
            $"Risk: {info.Risk}",
            $"Role pack: {DisplayOption(info.RolePack)}",
            $"Style: {DisplayOption(info.Style)}",
            $"Pressure: {DisplayOption(info.Intensity)}",
            $"Persona mixer: {DisplayOption(info.Absurdity)}"
        ];
    }

    internal static string GenerationPresetReceiptText(string preset)
    {
        return string.Join(Environment.NewLine, GenerationPresetReceiptLines(preset));
    }

    internal static string GenerationPresetCatalogSummary()
    {
        var categorySummary = GenerationPresetCatalog
            .GroupBy(item => item.Category)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToArray();
        return $"{GenerationPresetCatalog.Count} preset(s) across {categorySummary.Length} categories - {string.Join(", ", categorySummary)}.";
    }

    internal static IReadOnlyList<GenerationPresetInfo> GenerationPresetMatches(string rolePack, string style, string intensity, string absurdity)
    {
        var cleanRolePack = NormalizeChoice(rolePack, "auto");
        var cleanStyle = NormalizeChoice(style, "auto");
        var cleanIntensity = NormalizeChoice(intensity, "normal");
        var cleanAbsurdity = NormalizeChoice(absurdity, "grounded");
        return GenerationPresetCatalog
            .Where(item => !item.Key.Equals("manual", StringComparison.OrdinalIgnoreCase))
            .Where(item => NormalizeChoice(item.RolePack, "auto").Equals(cleanRolePack, StringComparison.OrdinalIgnoreCase))
            .Where(item => NormalizeChoice(item.Style, "auto").Equals(cleanStyle, StringComparison.OrdinalIgnoreCase))
            .Where(item => NormalizeChoice(item.Intensity, "normal").Equals(cleanIntensity, StringComparison.OrdinalIgnoreCase))
            .Where(item => NormalizeChoice(item.Absurdity, "grounded").Equals(cleanAbsurdity, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    internal static string[] GenerationPresetMatchLabels(string rolePack, string style, string intensity, string absurdity)
    {
        return GenerationPresetMatches(rolePack, style, intensity, absurdity)
            .Select(item => item.Label)
            .ToArray();
    }

    internal static string GenerationPresetMatchSummary(string rolePack, string style, string intensity, string absurdity)
    {
        var matches = GenerationPresetMatchLabels(rolePack, style, intensity, absurdity);
        return matches.Length == 0
            ? "Preset match: custom recipe."
            : $"Preset match: {string.Join(", ", matches)}.";
    }

    internal static string GenerationControlSummary(string preset, string rolePack, string style, string intensity, string absurdity)
    {
        var recipe = new List<string>();
        var cleanPreset = NormalizeChoice(preset, "manual");
        if (!cleanPreset.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            recipe.Add(PresetLabel(cleanPreset));
        }

        var cleanRolePack = NormalizeChoice(rolePack, "auto");
        var cleanStyle = NormalizeChoice(style, "auto");
        var cleanIntensity = NormalizeChoice(intensity, "normal");
        var cleanAbsurdity = NormalizeChoice(absurdity, "grounded");
        recipe.Add(cleanRolePack.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto pack" : $"{DisplayOption(cleanRolePack)} pack");
        recipe.Add(cleanStyle.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto style" : DisplayOption(cleanStyle));
        recipe.Add(cleanIntensity.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "normal pressure" : $"{DisplayOption(cleanIntensity)} pressure");
        recipe.Add(cleanAbsurdity.Equals("grounded", StringComparison.OrdinalIgnoreCase) ? "grounded personas" : $"{DisplayOption(cleanAbsurdity)} persona mixer");
        var prefix = cleanPreset.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "Manual setup" : "Preset recipe";
        return $"{prefix}: {string.Join(" / ", recipe)}.";
    }

    internal static string SetupReadinessStatus(ArenaViewSnapshot snapshot)
    {
        return SetupReadinessStatus(
            snapshot,
            snapshot.ScenarioGeneratorRolePack,
            snapshot.ScenarioGeneratorStyle,
            snapshot.ScenarioGeneratorIntensity,
            snapshot.ScenarioGeneratorAbsurdity);
    }

    internal static string SetupReadinessStatus(ArenaViewSnapshot snapshot, string rolePack, string style, string intensity, string absurdity)
    {
        return BuildSetupReadinessReport(snapshot, rolePack, style, intensity, absurdity).Status;
    }

    private static string SetupReadinessTooltip(ArenaViewSnapshot snapshot, string rolePack, string style, string intensity, string absurdity)
    {
        return BuildSetupReadinessReport(snapshot, rolePack, style, intensity, absurdity).Tooltip;
    }

    internal static SetupReadinessReport BuildSetupReadinessReport(ArenaViewSnapshot snapshot)
    {
        return BuildSetupReadinessReport(
            snapshot,
            snapshot.ScenarioGeneratorRolePack,
            snapshot.ScenarioGeneratorStyle,
            snapshot.ScenarioGeneratorIntensity,
            snapshot.ScenarioGeneratorAbsurdity);
    }

    internal static SetupReadinessReport BuildSetupReadinessReport(ArenaViewSnapshot snapshot, string rolePack, string style, string intensity, string absurdity)
    {
        var blockers = SetupReadinessBlockers(snapshot).ToArray();
        var warnings = SetupReadinessWarnings(snapshot).ToArray();
        var summary = SetupReadinessRunSummary(snapshot, rolePack, style, intensity, absurdity);
        var status = blockers.Length > 0
            ? $"Setup blocked: {blockers.Length} blocker(s) - {string.Join("; ", blockers.Take(2))}{(blockers.Length > 2 ? "; ..." : "")}."
            : warnings.Length > 0
                ? $"Ready with warnings: {summary}; {warnings.Length} warning(s) - {string.Join("; ", warnings.Take(2))}{(warnings.Length > 2 ? "; ..." : "")}."
                : $"Ready: {summary}.";
        var tooltip = SetupReadinessTooltipText(blockers, warnings, summary);
        return new SetupReadinessReport(
            status,
            tooltip,
            blockers,
            warnings,
            SetupReadinessBadges(snapshot, blockers, warnings),
            SetupReadinessChecklist(blockers, warnings));
    }

    private static string SetupReadinessRunSummary(ArenaViewSnapshot snapshot, string rolePack, string style, string intensity, string absurdity)
    {
        var activeAgents = snapshot.Agents.Count(agent => agent.Active);
        var history = snapshot.GenerationHistory.Count == 0
            ? "no replay history yet"
            : $"{snapshot.GenerationHistory.Count} replayable setup(s)";
        var relationshipCount = ActiveRelationshipPlan(snapshot).Links.Count;
        var relationship = snapshot.RivalryMatrixEnabled && relationshipCount > 0
            ? $"{relationshipCount} relationship rule(s)"
            : "neutral relationships";
        var recipe = GenerationControlSummary("manual", rolePack, style, intensity, absurdity)
            .Replace("Manual setup: ", "", StringComparison.Ordinal)
            .TrimEnd('.');
        return $"{activeAgents} active agents, {recipe}, {relationship}, {history}";
    }

    private static string SetupReadinessTooltipText(IReadOnlyList<string> blockers, IReadOnlyList<string> warnings, string summary)
    {
        var lines = new List<string> { $"Run summary: {summary}." };
        if (blockers.Count > 0)
        {
            lines.Add("");
            lines.Add("Blockers");
            lines.AddRange(blockers.Select(issue => $"- {issue}"));
        }

        if (warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings");
            lines.AddRange(warnings.Select(issue => $"- {issue}"));
        }

        if (blockers.Count == 0 && warnings.Count == 0)
        {
            lines.Add("");
            lines.Add("Locks and relationship pressure will be preserved when applicable.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<SetupReadinessBadge> SetupReadinessBadges(ArenaViewSnapshot snapshot, IReadOnlyList<string> blockers, IReadOnlyList<string> warnings)
    {
        var activeAgents = snapshot.Agents.Count(agent => agent.Active);
        var relationshipCount = ActiveRelationshipPlan(snapshot).Links.Count;
        var blankPersonaCount = BlankActivePersonaCount(snapshot);
        var providerError = !string.IsNullOrWhiteSpace(snapshot.ProviderLastError) && snapshot.ProviderLastError != "-";
        var lockCount = SetupLockCount(snapshot);
        var providerModelState = ProviderModelState(snapshot);
        return
        [
            new SetupReadinessBadge(
                "State",
                blockers.Count > 0 ? "Blocked" : warnings.Count > 0 ? "Warnings" : "Ready",
                blockers.Count > 0 ? "danger" : warnings.Count > 0 ? "warning" : "ready",
                blockers.Count > 0 ? $"{blockers.Count} blocker(s) need attention." : warnings.Count > 0 ? $"{warnings.Count} warning(s) to review." : "All required setup checks passed."),
            new SetupReadinessBadge(
                "Agents",
                activeAgents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                activeAgents < 2 ? "danger" : "ready",
                activeAgents < 2 ? "Activate at least two participants for an arena exchange." : $"{activeAgents} active participant(s) are available."),
            new SetupReadinessBadge(
                "Provider",
                snapshot.ProviderOnline ? providerModelState : providerError ? "Error" : "Offline",
                snapshot.ProviderOnline ? "ready" : "warning",
                ProviderBadgeTooltip(snapshot, providerModelState)),
            new SetupReadinessBadge(
                "Personas",
                blankPersonaCount == 0 ? "Filled" : $"{blankPersonaCount} blank",
                blankPersonaCount == 0 ? "ready" : "warning",
                blankPersonaCount == 0 ? "Active agents have persona text." : "Blank personas can make agent behavior generic."),
            new SetupReadinessBadge(
                "Narrator",
                string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "Blank" : "Briefed",
                string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "warning" : "ready",
                string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "Add narrator guidance for clearer judging and summaries." : "Narrator guidance is present."),
            new SetupReadinessBadge(
                "Criteria",
                HasScenarioQualityContract(snapshot) ? "Auditable" : "Basic",
                HasScenarioQualityContract(snapshot) ? "ready" : "warning",
                HasScenarioQualityContract(snapshot)
                    ? "Scenario defines success, unacceptable failure, an edge-case test, actionable output, and unresolved uncertainty."
                    : "Generate a new setup or add a quality contract to make closure criteria auditable."),
            new SetupReadinessBadge(
                "Matrix",
                snapshot.RivalryMatrixEnabled ? relationshipCount == 0 ? "No rules" : $"{relationshipCount} rule(s)" : "Neutral",
                snapshot.RivalryMatrixEnabled && relationshipCount == 0 ? "danger" : "ready",
                snapshot.RivalryMatrixEnabled
                    ? relationshipCount == 0 ? "Relationship matrix is enabled but has no active normalized rules." : $"{relationshipCount} active relationship rule(s) will shape prompts."
                    : "Relationship pressure is neutral."),
            new SetupReadinessBadge(
                "Locks",
                lockCount == 0 ? "None" : lockCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "neutral",
                lockCount == 0 ? "Generated setups can replace scenario and cast fields." : $"{lockCount} setup lock(s) will preserve current fields."),
            new SetupReadinessBadge(
                "History",
                snapshot.GenerationHistory.Count == 0 ? "None" : snapshot.GenerationHistory.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "neutral",
                snapshot.GenerationHistory.Count == 0 ? "No replayable generated setup history yet." : $"{snapshot.GenerationHistory.Count} replayable setup(s) are available.")
        ];
    }

    private static IReadOnlyList<SetupReadinessChecklistItem> SetupReadinessChecklist(IReadOnlyList<string> blockers, IReadOnlyList<string> warnings)
    {
        var items = new List<SetupReadinessChecklistItem>();
        if (blockers.Count > 0)
        {
            items.AddRange(blockers.Select(blocker => new SetupReadinessChecklistItem("Required", blocker, "danger")));
        }

        if (warnings.Count > 0)
        {
            items.AddRange(warnings.Select(warning => new SetupReadinessChecklistItem("Advisory", warning, "warning")));
        }

        if (items.Count == 0)
        {
            items.Add(new SetupReadinessChecklistItem("Preflight", "No blockers or warnings. Setup is ready to run.", "ready"));
        }

        return items;
    }

    private static IEnumerable<string> SetupReadinessBlockers(ArenaViewSnapshot snapshot)
    {
        var activeAgents = snapshot.Agents.Count(agent => agent.Active);
        if (activeAgents < 2)
        {
            yield return "activate at least two agents for a real arena exchange";
        }

        if (string.IsNullOrWhiteSpace(snapshot.ScenarioTopic))
        {
            yield return "set or generate a topic";
        }

        if (string.IsNullOrWhiteSpace(snapshot.ScenarioGlobal))
        {
            yield return "add global run rules";
        }

        if (!HasRunnableModelAssignment(snapshot))
        {
            yield return "choose a shared provider model or assign models to every active agent";
        }

        if (snapshot.RivalryMatrixEnabled && ActiveRelationshipPlan(snapshot).Links.Count == 0)
        {
            yield return "relationship matrix is enabled but has no active rules";
        }
    }

    private static IEnumerable<string> SetupReadinessWarnings(ArenaViewSnapshot snapshot)
    {
        if (!snapshot.ProviderOnline)
        {
            yield return string.IsNullOrWhiteSpace(snapshot.ProviderLastError) || snapshot.ProviderLastError == "-"
                ? "provider is offline; run Test connection before starting"
                : $"provider is offline: {ShortHistoryText(snapshot.ProviderLastError, 96)}";
        }

        var blankPersonaCount = BlankActivePersonaCount(snapshot);
        if (blankPersonaCount > 0)
        {
            yield return $"{blankPersonaCount} active agent persona(s) are blank";
        }

        if (string.IsNullOrWhiteSpace(snapshot.NarratorPersona))
        {
            yield return "narrator persona is blank";
        }

        if (!HasScenarioQualityContract(snapshot))
        {
            yield return "global run rules do not include an auditable scenario quality contract";
        }
    }

    internal static bool HasScenarioQualityContract(ArenaViewSnapshot snapshot)
    {
        return ScenarioAuditPolicy.HasCompleteQualityContract(snapshot.ScenarioGlobal);
    }

    private static int BlankActivePersonaCount(ArenaViewSnapshot snapshot)
    {
        return snapshot.Agents.Count(agent => agent.Active && string.IsNullOrWhiteSpace(agent.Persona));
    }

    private static bool HasRunnableModelAssignment(ArenaViewSnapshot snapshot)
    {
        if (HasModel(snapshot.ProviderModel))
        {
            return true;
        }

        var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
        return activeAgents.Length > 0 && activeAgents.All(agent => HasModel(agent.Model));
    }

    private static string ProviderModelState(ArenaViewSnapshot snapshot)
    {
        if (HasModel(snapshot.ProviderModel))
        {
            return "Online";
        }

        return HasRunnableModelAssignment(snapshot) ? "Role models" : "No model";
    }

    private static string ProviderBadgeTooltip(ArenaViewSnapshot snapshot, string providerModelState)
    {
        if (!snapshot.ProviderOnline)
        {
            return string.IsNullOrWhiteSpace(snapshot.ProviderLastError) || snapshot.ProviderLastError == "-"
                ? "Provider is offline; run Test connection before starting."
                : $"Provider is offline: {ShortHistoryText(snapshot.ProviderLastError, 96)}";
        }

        return providerModelState.Equals("Role models", StringComparison.OrdinalIgnoreCase)
            ? "Shared provider model is blank, but every active agent has a role-specific model."
            : HasModel(snapshot.ProviderModel)
                ? $"Shared provider model: {ShortHistoryText(snapshot.ProviderModel, 64)}"
                : "Choose a shared provider model or assign models to every active agent.";
    }

    private static int SetupLockCount(ArenaViewSnapshot snapshot)
    {
        return (snapshot.TopicLocked ? 1 : 0)
            + (snapshot.GlobalLocked ? 1 : 0)
            + (snapshot.NarratorLocked ? 1 : 0)
            + snapshot.Agents.Count(agent => agent.Active && agent.Locked);
    }

    private static bool HasModel(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value != "-";
    }

    private static int ActiveRelationshipRuleCount(ArenaViewSnapshot snapshot)
    {
        return ActiveRelationshipPlan(snapshot).Links.Count;
    }

    private static MatchSetupCoordinator.RivalryMatrixPlan ActiveRelationshipPlan(ArenaViewSnapshot snapshot)
    {
        var activeIds = snapshot.Agents
            .Where(agent => agent.Active)
            .Select(agent => agent.Id)
            .Where(AgentRosterService.IsParticipantId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.RivalryMatrixEnabled
            ? MatchSetupCoordinator.BuildRivalryMatrixPlan(snapshot.RivalryMatrix, activeIds)
            : new MatchSetupCoordinator.RivalryMatrixPlan([], 0);
    }

    private static string GenerationControlTooltip(string preset, string rolePack, string style, string intensity, string absurdity, string topicPrompt = "")
    {
        var info = GenerationPresetDetails(preset);
        return string.Join(
            Environment.NewLine,
            GenerationControlSummary(preset, rolePack, style, intensity, absurdity),
            string.IsNullOrWhiteSpace(topicPrompt)
                ? "AI Choice topic prompt: blank; the model may choose the scenario topic."
                : $"AI Choice topic prompt: {ShortHistoryText(topicPrompt, 160)}",
            info.Key.Equals("manual", StringComparison.OrdinalIgnoreCase)
                ? GenerationPresetCatalogSummary()
                : $"{info.Category}: {info.Summary}",
            $"Best for: {info.BestFor}",
            $"Risk: {info.Risk}",
            "Random Seed: local and deterministic.",
            "AI Choice: model-generated setup using the same tuning.",
            "Wild Seed: local setup with stronger arena pressure.");
    }

    private static string PresetLabel(string preset)
    {
        var info = GenerationPresetDetails(preset);
        return info.Label.ToLowerInvariant();
    }

    private static string DisplayOption(string value)
    {
        return NormalizeChoice(value, "-").Replace('_', ' ').Replace('-', ' ');
    }

    private static string NormalizeChoice(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private static string RandomSeedOptionLabel(string style, string intensity, string rolePack, string absurdity)
    {
        static string Clean(string value) => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().Replace('-', ' ');

        var cleanStyle = Clean(style);
        var cleanIntensity = Clean(intensity);
        var styleLabel = cleanStyle.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto-style"
            : cleanStyle;
        var baseLabel = cleanIntensity.Equals("normal", StringComparison.OrdinalIgnoreCase)
            ? styleLabel
            : $"{styleLabel} {cleanIntensity}";
        var pack = Clean(rolePack).Replace('_', ' ');
        var weird = Clean(absurdity);
        if (!string.IsNullOrWhiteSpace(pack) && !pack.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            baseLabel = $"{baseLabel}, {pack}";
        }

        if (!string.IsNullOrWhiteSpace(weird) && !weird.Equals("grounded", StringComparison.OrdinalIgnoreCase))
        {
            baseLabel = $"{baseLabel}, {weird}";
        }

        return baseLabel;
    }

    private static string GenerationHistoryLabel(GenerationHistoryItem item)
    {
        var kind = item.Kind switch
        {
            "ai_choice" => "AI Choice",
            "current_topics" => "Current Topics",
            "yolo" => "Wild Seed",
            "random" => "Random",
            _ => DisplayStatusValue(item.Kind)
        };
        var time = DisplayTime(item.CreatedAt);
        var style = DisplayStatusValue(item.Style);
        var pressure = item.Intensity.Equals("normal", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" / {item.Intensity.Replace('_', ' ')}";
        var cast = item.PersonaCount > 0 ? $" / {item.PersonaCount} roles" : "";
        return $"{kind} - {style}{pressure}{cast} - {ShortHistoryText(item.Topic, 32)}{(string.IsNullOrWhiteSpace(time) ? "" : $" ({time})")}";
    }

    private static string GenerationHistoryTooltip(GenerationHistoryItem item, ArenaViewSnapshot? snapshot = null)
    {
        var lines = new List<string>
        {
            $"Label: {DisplayStatusValue(item.Label)}",
            $"Kind: {DisplayStatusValue(item.Kind)}",
            $"Style: {DisplayStatusValue(item.Style)}",
            $"Pressure: {DisplayStatusValue(item.Intensity)}",
            $"Pack: {DisplayStatusValue(item.RolePack)}",
            $"Absurdity: {DisplayStatusValue(item.Absurdity)}",
            $"Scenario seed: {DisplayStatusValue(item.ScenarioSeed)}",
            $"Persona seed: {DisplayStatusValue(item.PersonaSeed)}",
            $"Cast: {CastSummary(item)}",
            $"Topic: {DisplayStatusValue(item.Topic)}",
            $"Global: {ShortHistoryText(item.Global, 120)}",
            $"Narrator: {ShortHistoryText(item.NarratorBrief, 120)}"
        };
        var warning = ReplayLockWarning(snapshot);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            lines.Add(warning);
        }

        return string.Join(
            Environment.NewLine,
            lines);
    }

    internal static IReadOnlyList<GenerationHistoryItem> FilterGenerationHistory(IReadOnlyList<GenerationHistoryItem> items, string filter)
    {
        var normalized = NormalizeGenerationHistoryFilter(filter);
        return normalized.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? items.ToArray()
            : items.Where(item => NormalizeGenerationHistoryFilter(item.Kind).Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    internal static string NormalizeGenerationHistoryFilter(string value)
    {
        var normalized = NormalizeChoice(value, "all");
        return normalized switch
        {
            "random" => "random",
            "ai_choice" or "ai choice" => "ai_choice",
            "current_topics" or "current topics" => "current_topics",
            "yolo" or "wild_seed" or "wild seed" => "yolo",
            _ => "all"
        };
    }

    internal static string GenerationHistoryFilterLabel(string filter)
    {
        return NormalizeGenerationHistoryFilter(filter) switch
        {
            "random" => "Random",
            "ai_choice" => "AI Choice",
            "current_topics" => "Current Topics",
            "yolo" => "Wild Seed",
            _ => "All"
        };
    }

    internal static int PreferredGenerationHistoryIndex(IReadOnlyList<GenerationHistoryItem?> items, string previousId)
    {
        if (items.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(previousId))
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index]?.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return index;
                }
            }
        }

        return 0;
    }

    internal static string GenerationHistoryStatus(GenerationHistoryItem item)
    {
        var kind = DisplayStatusValue(item.Kind);
        var style = DisplayStatusValue(item.Style);
        var pressure = DisplayStatusValue(item.Intensity);
        var topic = ShortHistoryText(item.Topic, 42);
        return $"{kind} / {style} / {pressure} / {CastSummary(item)} - {topic}";
    }

    internal static string GenerationSelectionStatus(GenerationHistoryItem item, ArenaViewSnapshot? snapshot)
    {
        var status = $"Selected: {GenerationHistoryStatus(item)}";
        var warning = ReplayLockWarning(snapshot);
        return string.IsNullOrWhiteSpace(warning) ? status : $"{status}. {warning}";
    }

    internal static string GenerationHistoryBrief(GenerationHistoryItem item)
    {
        return string.Join(Environment.NewLine,
            $"Generated match: {DisplayStatusValue(item.Label)}",
            $"Kind: {DisplayStatusValue(item.Kind)}",
            $"Style: {DisplayStatusValue(item.Style)}",
            $"Intensity: {DisplayStatusValue(item.Intensity)}",
            $"Role pack: {DisplayStatusValue(item.RolePack)}",
            $"Absurdity: {DisplayStatusValue(item.Absurdity)}",
            GenerationPresetMatchSummary(item.RolePack, item.Style, item.Intensity, item.Absurdity),
            $"Topic: {DisplayStatusValue(item.Topic)}",
            $"Global: {DisplayStatusValue(item.Global)}",
            $"Narrator brief: {DisplayStatusValue(item.NarratorBrief)}",
            $"Cast: {CastSummary(item)}",
            $"Cast preview: {DisplayStatusValue(item.PersonaPreview)}",
            $"Scenario seed: {DisplayStatusValue(item.ScenarioSeed)}",
            $"Persona seed: {DisplayStatusValue(item.PersonaSeed)}");
    }

    internal static string GenerationHistorySpec(GenerationHistoryItem item, ArenaViewSnapshot? snapshot = null)
    {
        var seedDeterministic = GenerationSeedIsDeterministic(item);
        var spec = new
        {
            schema = "ai_arena.generated_match.v1",
            id = item.Id,
            label = DisplayStatusValue(item.Label),
            kind = DisplayStatusValue(item.Kind),
            tuning = new
            {
                style = DisplayStatusValue(item.Style),
                intensity = DisplayStatusValue(item.Intensity),
                rolePack = DisplayStatusValue(item.RolePack),
                absurdity = DisplayStatusValue(item.Absurdity),
                presetMatches = GenerationPresetMatchLabels(item.RolePack, item.Style, item.Intensity, item.Absurdity)
            },
            seeds = new
            {
                scenario = DisplayStatusValue(item.ScenarioSeed),
                persona = DisplayStatusValue(item.PersonaSeed),
                deterministic = seedDeterministic,
                replayMode = ScenarioAuditPolicy.ReplayMode(item.Kind, item.ScenarioSeed)
            },
            scenario = new
            {
                topic = DisplayStatusValue(item.Topic),
                global = DisplayStatusValue(item.Global),
                narratorBrief = DisplayStatusValue(item.NarratorBrief)
            },
            cast = new
            {
                count = item.PersonaCount,
                preview = DisplayStatusValue(item.PersonaPreview)
            },
            review = new
            {
                diffSummary = GenerationHistoryDiffSummary(item, snapshot),
                rubric = GenerationRubricChecks(item)
            },
            replay = new
            {
                currentLocks = ReplayLockLabels(snapshot),
                warning = ReplayLockWarning(snapshot)
            }
        };

        return JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string GenerationHistoryDiff(GenerationHistoryItem item, ArenaViewSnapshot? snapshot = null)
    {
        var lines = new List<string>
        {
            $"Generated setup diff: {DisplayStatusValue(item.Label)}",
            $"Kind: {DisplayStatusValue(item.Kind)}",
            "",
            "Generated setup",
            $"- Topic: {DisplayStatusValue(item.Topic)}",
            $"- Global: {DisplayStatusValue(item.Global)}",
            $"- Narrator brief: {DisplayStatusValue(item.NarratorBrief)}",
            $"- Recipe: {DisplayStatusValue(item.RolePack)} / {DisplayStatusValue(item.Style)} / {DisplayStatusValue(item.Intensity)} / {DisplayStatusValue(item.Absurdity)}",
            $"- {GenerationPresetMatchSummary(item.RolePack, item.Style, item.Intensity, item.Absurdity)}",
            $"- Cast: {CastSummary(item)}"
        };

        if (snapshot is null)
        {
            lines.Add("");
            lines.Add("Current setup unavailable.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("");
        lines.Add("Current setup");
        lines.Add($"- Session: {DisplayStatusValue(snapshot.SessionId)}");
        lines.Add($"- Topic: {DisplayStatusValue(snapshot.ScenarioTopic)}");
        lines.Add($"- Global: {DisplayStatusValue(snapshot.ScenarioGlobal)}");
        lines.Add($"- Narrator: {ShortHistoryText(snapshot.NarratorPersona, 120)}");
        lines.Add($"- Recipe: {DisplayStatusValue(snapshot.ScenarioGeneratorRolePack)} / {DisplayStatusValue(snapshot.ScenarioGeneratorStyle)} / {DisplayStatusValue(snapshot.ScenarioGeneratorIntensity)} / {DisplayStatusValue(snapshot.ScenarioGeneratorAbsurdity)}");
        lines.Add($"- {GenerationPresetMatchSummary(snapshot.ScenarioGeneratorRolePack, snapshot.ScenarioGeneratorStyle, snapshot.ScenarioGeneratorIntensity, snapshot.ScenarioGeneratorAbsurdity)}");
        lines.Add($"- Active cast: {ActiveCastSummary(snapshot)}");

        var summary = GenerationHistoryDiffSummary(item, snapshot);
        if (summary.Length > 0)
        {
            lines.Add("");
            lines.Add("Review flags");
            foreach (var flag in summary)
            {
                lines.Add($"- {flag}");
            }
        }

        var warning = ReplayLockWarning(snapshot);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            lines.Add("");
            lines.Add($"Replay lock warning: {warning}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string[] GenerationHistoryDiffSummary(GenerationHistoryItem item, ArenaViewSnapshot? snapshot = null)
    {
        if (snapshot is null)
        {
            return ["No current setup snapshot available for comparison."];
        }

        var flags = new List<string>
        {
            DifferenceFlag("Topic", snapshot.ScenarioTopic, item.Topic),
            DifferenceFlag("Global instructions", snapshot.ScenarioGlobal, item.Global),
            DifferenceFlag("Role pack", snapshot.ScenarioGeneratorRolePack, item.RolePack),
            DifferenceFlag("Style", snapshot.ScenarioGeneratorStyle, item.Style),
            DifferenceFlag("Pressure", snapshot.ScenarioGeneratorIntensity, item.Intensity),
            DifferenceFlag("Persona mixer", snapshot.ScenarioGeneratorAbsurdity, item.Absurdity)
        };

        var activeAgents = snapshot.Agents.Count(agent => agent.Active);
        if (item.PersonaCount > 0 && activeAgents != item.PersonaCount)
        {
            flags.Add($"Cast size differs: current {activeAgents}, generated {item.PersonaCount}.");
        }

        var locks = ReplayLockLabels(snapshot);
        if (locks.Length > 0)
        {
            flags.Add($"{locks.Length} current lock(s) can override generated fields on replay.");
        }

        return flags.Where(flag => !string.IsNullOrWhiteSpace(flag)).ToArray();
    }

    internal static string GenerationHistoryRubric(GenerationHistoryItem item, ArenaViewSnapshot? snapshot = null)
    {
        var lines = new List<string>
        {
            $"AI Arena eval rubric: {DisplayStatusValue(item.Label)}",
            $"Topic: {DisplayStatusValue(item.Topic)}",
            $"Recipe: {DisplayStatusValue(item.RolePack)} / {DisplayStatusValue(item.Style)} / {DisplayStatusValue(item.Intensity)} / {DisplayStatusValue(item.Absurdity)}",
            GenerationPresetMatchSummary(item.RolePack, item.Style, item.Intensity, item.Absurdity),
            $"Cast: {CastSummary(item)}",
            "",
            "Score each dimension 1-5:",
        };

        foreach (var check in GenerationRubricChecks(item))
        {
            lines.Add($"- {check}");
        }

        lines.Add("");
        lines.Add("Tie-breakers:");
        lines.Add("- Prefer the answer that changes the operator's decision with less unsupported theatre.");
        lines.Add("- Prefer explicit uncertainty over confident invention.");
        lines.Add("- Penalize consensus that arrives before the strongest objection is handled.");

        var warning = ReplayLockWarning(snapshot);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            lines.Add("");
            lines.Add($"Replay note: {warning}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string[] GenerationRubricChecks(GenerationHistoryItem item)
    {
        var checks = new List<string>
        {
            "Role fidelity: each active agent stays inside its assigned expertise, voice, and pressure profile.",
            "Evidence discipline: claims are grounded, caveated, and challenged when evidence is thin.",
            "Useful disagreement: friction reveals real tradeoffs instead of repeating the same position.",
            "Operator value: the final state gives a clearer decision, next step, or experiment than the starting prompt."
        };

        var rolePack = NormalizeChoice(item.RolePack, "");
        var style = NormalizeChoice(item.Style, "");
        var intensity = NormalizeChoice(item.Intensity, "");
        var absurdity = NormalizeChoice(item.Absurdity, "grounded");

        if (rolePack.Contains("tool", StringComparison.OrdinalIgnoreCase) || style.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Tool reliability: agents distinguish tool results, assumptions, stale data, and failed calls.");
        }

        if (rolePack.Contains("governance", StringComparison.OrdinalIgnoreCase) || style.Contains("legal", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Governance clarity: policy, accountability, and escalation paths are explicit without becoming performative.");
        }

        if (rolePack.Contains("benchmark", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Comparison fairness: criteria are blind where possible, comparable across agents, and not biased by order or style.");
        }

        if (style.Contains("research", StringComparison.OrdinalIgnoreCase) || style.Contains("scientific", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Research hygiene: hypotheses, controls, missing evidence, and falsification routes are visible.");
        }

        if (intensity.Contains("chaos", StringComparison.OrdinalIgnoreCase) || !absurdity.Equals("grounded", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Creative containment: absurd constraints add signal and memorability without hiding weak reasoning.");
        }

        if (intensity.Contains("one_line", StringComparison.OrdinalIgnoreCase) || intensity.Contains("one line", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add("Compression quality: short answers preserve the decisive insight instead of flattening nuance.");
        }

        return checks.ToArray();
    }

    internal static string ReplayLockWarning(ArenaViewSnapshot? snapshot)
    {
        var locks = ReplayLockLabels(snapshot);
        if (locks.Length == 0)
        {
            return "";
        }

        var preview = locks.Length <= 3
            ? string.Join(", ", locks)
            : $"{string.Join(", ", locks.Take(3))}, +{locks.Length - 3} more";
        return $"{locks.Length} lock(s) may preserve current {preview} during replay.";
    }

    internal static string[] ReplayLockLabels(ArenaViewSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return [];
        }

        return new[]
            {
                snapshot.TopicLocked ? "topic" : "",
                snapshot.GlobalLocked ? "global" : "",
                snapshot.NarratorLocked ? "narrator" : ""
            }
            .Concat(snapshot.Agents
                .Where(agent => agent.Active && agent.Locked)
                .Select(agent => string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => ShortHistoryText(item, 34))
            .ToArray();
    }

    internal static string GenerationHistoryCountStatus(int shownCount, int totalCount)
    {
        return GenerationHistoryCountStatus(shownCount, totalCount, totalCount, "all");
    }

    internal static string GenerationHistoryCountStatus(int shownCount, int totalCount, int matchingCount, string filter)
    {
        if (totalCount <= 0 || shownCount <= 0)
        {
            if (totalCount > 0 && !NormalizeGenerationHistoryFilter(filter).Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return $"No {GenerationHistoryFilterLabel(filter)} generated matches in {totalCount} total.";
            }

            return "No generated matches yet.";
        }

        if (!NormalizeGenerationHistoryFilter(filter).Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var label = GenerationHistoryFilterLabel(filter);
            return shownCount >= matchingCount
                ? $"{matchingCount} {label} generated match(es) available ({totalCount} total)."
                : $"Showing {shownCount} of {matchingCount} {label} generated match(es) ({totalCount} total).";
        }

        return shownCount >= totalCount
            ? $"{totalCount} generated match(es) available."
            : $"Showing {shownCount} of {totalCount} generated match(es).";
    }

    private static string DifferenceFlag(string label, string currentValue, string generatedValue)
    {
        var current = DisplayStatusValue(currentValue);
        var generated = DisplayStatusValue(generatedValue);
        return current.Equals(generated, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"{label} changes: current \"{ShortHistoryText(current, 72)}\" -> generated \"{ShortHistoryText(generated, 72)}\".";
    }

    private static string ActiveCastSummary(ArenaViewSnapshot snapshot)
    {
        var activeAgents = snapshot.Agents
            .Where(agent => agent.Active)
            .Select(agent => string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name)
            .Take(6)
            .Select(agent => ShortHistoryText(agent, 28))
            .ToArray();
        if (activeAgents.Length == 0)
        {
            return "no active agents";
        }

        var suffix = snapshot.Agents.Count(agent => agent.Active) > activeAgents.Length
            ? $", +{snapshot.Agents.Count(agent => agent.Active) - activeAgents.Length} more"
            : "";
        return $"{activeAgents.Length}{suffix} active - {string.Join(", ", activeAgents)}";
    }

    private static string ShortHistoryText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return "-";
        }

        var singleLine = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..Math.Max(0, maxLength - 3)]}...";
    }

    private static string DisplayStatusValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static string CastSummary(GenerationHistoryItem item)
    {
        return item.PersonaCount > 0
            ? $"{item.PersonaCount} role(s){(string.IsNullOrWhiteSpace(item.PersonaPreview) ? "" : $" - {ShortHistoryText(item.PersonaPreview, 64)}")}"
            : "cast not stored";
    }

    internal static bool GenerationSeedIsDeterministic(GenerationHistoryItem item) =>
        ScenarioAuditPolicy.IsSeedDeterministic(item.Kind, item.ScenarioSeed);

    private static string CapturedGenerationLabel(string kind)
    {
        return NormalizeGenerationHistoryFilter(kind) switch
        {
            "ai_choice" => "AI Choice",
            "current_topics" => "Current Topics",
            _ => "Captured-output generation"
        };
    }

    private static string DisplayTime(double createdAt)
    {
        if (createdAt <= 0)
        {
            return "";
        }

        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds((long)createdAt)
                .ToLocalTime()
                .ToString("h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    internal sealed record SetupReadinessReport(
        string Status,
        string Tooltip,
        IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<SetupReadinessBadge> Badges,
        IReadOnlyList<SetupReadinessChecklistItem> Checklist);

    internal sealed record SetupReadinessBadge(
        string Label,
        string Value,
        string Kind,
        string Tooltip = "");

    internal sealed record SetupReadinessChecklistItem(
        string Label,
        string Value,
        string Kind);
}
