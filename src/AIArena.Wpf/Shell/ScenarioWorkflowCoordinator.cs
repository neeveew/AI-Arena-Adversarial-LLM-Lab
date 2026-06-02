using System.Windows;
using System.Windows.Controls;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

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
    private readonly Button randomSeedButton;
    private readonly Button aiChoiceButton;
    private readonly Button yoloScenarioButton;
    private readonly ComboBox generationHistoryPicker;
    private readonly Button replayGenerationButton;
    private readonly Button replayNewRunButton;
    private readonly Button copyGenerationSeedButton;
    private readonly ComboBox operatorTemplatePicker;
    private readonly TextBox operatorTurnText;
    private readonly Func<WpfSettings> settings;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    private bool isUpdating;

    public ScenarioWorkflowCoordinator(
        Window owner,
        MatchGenerationService matchGeneration,
        WpfSettingsStore settingsStore,
        ComboBox randomSeedPresetPicker,
        ComboBox randomSeedRolePackPicker,
        ComboBox randomSeedStylePicker,
        ComboBox randomSeedIntensityPicker,
        ComboBox randomSeedAbsurdityPicker,
        Button randomSeedButton,
        Button aiChoiceButton,
        Button yoloScenarioButton,
        ComboBox generationHistoryPicker,
        Button replayGenerationButton,
        Button replayNewRunButton,
        Button copyGenerationSeedButton,
        ComboBox operatorTemplatePicker,
        TextBox operatorTurnText,
        Func<WpfSettings> settings,
        Func<CoreSessionSummary?> activeSession,
        Func<ThemePalette> theme,
        Func<bool> isRenderingSnapshot,
        Func<bool> isArenaBusy,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Func<string?, Task> loadSessionsAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.matchGeneration = matchGeneration;
        this.settingsStore = settingsStore;
        this.randomSeedPresetPicker = randomSeedPresetPicker;
        this.randomSeedRolePackPicker = randomSeedRolePackPicker;
        this.randomSeedStylePicker = randomSeedStylePicker;
        this.randomSeedIntensityPicker = randomSeedIntensityPicker;
        this.randomSeedAbsurdityPicker = randomSeedAbsurdityPicker;
        this.randomSeedButton = randomSeedButton;
        this.aiChoiceButton = aiChoiceButton;
        this.yoloScenarioButton = yoloScenarioButton;
        this.generationHistoryPicker = generationHistoryPicker;
        this.replayGenerationButton = replayGenerationButton;
        this.replayNewRunButton = replayNewRunButton;
        this.copyGenerationSeedButton = copyGenerationSeedButton;
        this.operatorTemplatePicker = operatorTemplatePicker;
        this.operatorTurnText = operatorTurnText;
        this.settings = settings;
        this.activeSession = activeSession;
        this.theme = theme;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.isArenaBusy = isArenaBusy;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public GenerationHistoryItem? SelectedGenerationHistory =>
        generationHistoryPicker.SelectedItem is ComboBoxItem { Tag: GenerationHistoryItem item }
            ? item
            : null;

    public void InitializeControls()
    {
        var current = settings();
        isUpdating = true;
        try
        {
            SelectComboTag(randomSeedPresetPicker, current.RandomSeedPreset);
            SelectComboTag(randomSeedRolePackPicker, current.RandomSeedRolePack);
            SelectComboTag(randomSeedStylePicker, current.RandomSeedStyle);
            SelectComboTag(randomSeedIntensityPicker, current.RandomSeedIntensity);
            SelectComboTag(randomSeedAbsurdityPicker, current.RandomSeedAbsurdity);
            InitializeOperatorTemplates();
        }
        finally
        {
            isUpdating = false;
        }

        UpdateGenerationHistoryActions();
    }

    public void OnRandomSeedPresetChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        var current = settings();
        var preset = SelectedComboTag(randomSeedPresetPicker, "manual");
        current.RandomSeedPreset = preset;
        if (!preset.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            var config = RandomSeedPresetValues(preset);
            isUpdating = true;
            try
            {
                SelectComboTag(randomSeedRolePackPicker, config.RolePack);
                SelectComboTag(randomSeedStylePicker, config.Style);
                SelectComboTag(randomSeedIntensityPicker, config.Intensity);
                SelectComboTag(randomSeedAbsurdityPicker, config.Absurdity);
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
    }

    public void OnRandomSeedOptionsChanged()
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        var current = settings();
        current.RandomSeedRolePack = SelectedComboTag(randomSeedRolePackPicker, "auto");
        current.RandomSeedStyle = SelectedComboTag(randomSeedStylePicker, "auto");
        current.RandomSeedIntensity = SelectedComboTag(randomSeedIntensityPicker, "normal");
        current.RandomSeedAbsurdity = SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
        current.RandomSeedPreset = "manual";

        isUpdating = true;
        try
        {
            SelectComboTag(randomSeedPresetPicker, "manual");
        }
        finally
        {
            isUpdating = false;
        }

        settingsStore.Save(current);
    }

    public async Task GenerateRandomSeedAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var rolePack = SelectedComboTag(randomSeedRolePackPicker, "auto");
        var style = SelectedComboTag(randomSeedStylePicker, "auto");
        var intensity = SelectedComboTag(randomSeedIntensityPicker, "normal");
        var absurdity = SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
        await runArenaBusyAsync($"Generating {RandomSeedOptionLabel(style, intensity, rolePack, absurdity)} random seed...", randomSeedButton, async () =>
        {
            var result = await matchGeneration.GenerateRandomSeedAsync(session.Id, style, intensity, rolePack, absurdity);
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

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "AI Choice",
            "Ask the narrator model to generate an AI Choice match?\n\nThe current transcript will be preserved.",
            "Generate",
            tone: ConfirmDialogTone.Normal);
        if (!confirm)
        {
            return;
        }

        await runArenaBusyAsync("Asking narrator for AI Choice match...", aiChoiceButton, async () =>
        {
            var rolePack = SelectedComboTag(randomSeedRolePackPicker, "auto");
            var intensity = SelectedComboTag(randomSeedIntensityPicker, "normal");
            var absurdity = SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
            var result = await matchGeneration.GenerateAiChoiceAsync(session.Id, rolePack, intensity, absurdity);
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
            "YOLO Seed",
            "Generate a local seeded meta setup for the arena simulation?\n\nLocked fields and the current transcript will be preserved.",
            "Generate",
            tone: ConfirmDialogTone.Normal);
        if (!confirm)
        {
            return;
        }

        await runArenaBusyAsync("Generating YOLO seed...", yoloScenarioButton, async () =>
        {
            var rolePack = SelectedComboTag(randomSeedRolePackPicker, "auto");
            var intensity = SelectedComboTag(randomSeedIntensityPicker, "normal");
            var absurdity = SelectedComboTag(randomSeedAbsurdityPicker, "grounded");
            var result = await matchGeneration.GenerateYoloSeedAsync(session.Id, rolePack, intensity, absurdity);
            var status = result.Ok
                ? $"YOLO seed generated: {result.Label} - {result.Seed}"
                : $"YOLO seed failed: {result.Error}";
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

        await runArenaBusyAsync($"Replaying generated match: {item.Label}...", replayGenerationButton, async () =>
        {
            var result = await matchGeneration.ReplayGenerationAsync(session.Id, item.Id);
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

        await runArenaBusyAsync($"Creating replay run: {item.Label}...", replayNewRunButton, async () =>
        {
            var result = await matchGeneration.ReplayGenerationToNewSessionAsync(session.Id, item.Id);
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

        try
        {
            Clipboard.SetText(seed);
            SetBothStatuses($"Copied generation seed: {seed}");
        }
        catch (Exception ex)
        {
            SetBothStatuses($"Copy seed failed: {ex.Message}");
        }
    }

    public void PopulateGenerationHistory(ArenaViewSnapshot snapshot)
    {
        generationHistoryPicker.Items.Clear();
        foreach (var item in snapshot.GenerationHistory.Take(20))
        {
            generationHistoryPicker.Items.Add(new ComboBoxItem
            {
                Content = GenerationHistoryLabel(item),
                Tag = item,
                ToolTip = GenerationHistoryTooltip(item)
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

        generationHistoryPicker.SelectedIndex = 0;
        UpdateGenerationHistoryActions();
    }

    public void UpdateGenerationHistoryActions()
    {
        var hasItem = SelectedGenerationHistory is not null;
        var enabled = !isArenaBusy() && hasItem;
        replayGenerationButton.IsEnabled = enabled;
        replayNewRunButton.IsEnabled = enabled;
        copyGenerationSeedButton.IsEnabled = enabled;
    }

    public void UpdateBusyState(bool busy, bool autoChatRunning)
    {
        randomSeedButton.IsEnabled = !busy || autoChatRunning;
        randomSeedPresetPicker.IsEnabled = !busy;
        randomSeedRolePackPicker.IsEnabled = !busy;
        randomSeedStylePicker.IsEnabled = !busy;
        randomSeedIntensityPicker.IsEnabled = !busy;
        randomSeedAbsurdityPicker.IsEnabled = !busy;
        generationHistoryPicker.IsEnabled = !busy;
        aiChoiceButton.IsEnabled = !busy || autoChatRunning;
        yoloScenarioButton.IsEnabled = !busy || autoChatRunning;
        UpdateGenerationHistoryActions();
    }

    public void UseOperatorTemplate()
    {
        var template = CurrentOperatorTemplateText();
        if (string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        operatorTurnText.Text = template;
        operatorTurnText.Focus();
        operatorTurnText.CaretIndex = operatorTurnText.Text.Length;
    }

    public void SaveOperatorTemplate()
    {
        var template = operatorTurnText.Text.Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            setArenaRunStatus("Operator template is empty.");
            return;
        }

        var current = settings();
        current.OperatorTemplates ??= [];
        if (!current.OperatorTemplates.Contains(template, StringComparer.OrdinalIgnoreCase))
        {
            current.OperatorTemplates.Insert(0, template);
            current.OperatorTemplates = current.OperatorTemplates
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            settingsStore.Save(current);
            InitializeOperatorTemplates(template);
        }

        operatorTemplatePicker.SelectedItem = template;
        setArenaRunStatus("Operator template saved.");
    }

    public void DeleteOperatorTemplate()
    {
        var template = CurrentOperatorTemplateText();
        if (string.IsNullOrWhiteSpace(template))
        {
            setArenaRunStatus("No operator template selected.");
            return;
        }

        var current = settings();
        current.OperatorTemplates ??= [];
        var nextTemplates = current.OperatorTemplates
            .Where(item => !item.Equals(template, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nextTemplates.Count == current.OperatorTemplates.Count)
        {
            setArenaRunStatus("Operator template was already removed.");
            return;
        }

        var nextSelection = nextTemplates.FirstOrDefault();
        current.OperatorTemplates = nextTemplates;
        settingsStore.Save(current);
        InitializeOperatorTemplates(nextSelection);
        setArenaRunStatus("Operator template deleted.");
    }

    private void InitializeOperatorTemplates(string? preferredTemplate = null)
    {
        var current = settings();
        current.OperatorTemplates ??= [];
        operatorTemplatePicker.ItemsSource = null;
        operatorTemplatePicker.ItemsSource = current.OperatorTemplates;
        var preferredIndex = string.IsNullOrWhiteSpace(preferredTemplate)
            ? -1
            : current.OperatorTemplates.FindIndex(item => item.Equals(preferredTemplate, StringComparison.OrdinalIgnoreCase));
        operatorTemplatePicker.SelectedIndex = preferredIndex >= 0
            ? preferredIndex
            : current.OperatorTemplates.Count > 0 ? 0 : -1;
    }

    private string CurrentOperatorTemplateText()
    {
        return (operatorTemplatePicker.SelectedItem?.ToString() ?? operatorTemplatePicker.Text).Trim();
    }

    private void SetBothStatuses(string status)
    {
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    private static (string RolePack, string Style, string Intensity, string Absurdity) RandomSeedPresetValues(string preset)
    {
        return preset switch
        {
            "hostile_review" => ("red_team", "adversarial", "spicy", "grounded"),
            "evidence_trial" => ("scientific_review", "research", "sharp", "grounded"),
            "consensus_trap" => ("balanced", "philosophical", "sharp", "odd"),
            "chaos_room" => ("absurd_lab", "technical", "chaos", "maximum"),
            "one_line_mayhem" => ("absurd_lab", "creative", "one_line", "maximum"),
            _ => ("auto", "auto", "normal", "grounded")
        };
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
            "yolo" => "YOLO",
            "random" => "Random",
            _ => DisplayStatusValue(item.Kind)
        };
        var time = DisplayTime(item.CreatedAt);
        var style = DisplayStatusValue(item.Style);
        var pressure = item.Intensity.Equals("normal", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" / {item.Intensity.Replace('_', ' ')}";
        return $"{kind} - {style}{pressure} - {ShortHistoryText(item.Topic, 32)}{(string.IsNullOrWhiteSpace(time) ? "" : $" ({time})")}";
    }

    private static string GenerationHistoryTooltip(GenerationHistoryItem item)
    {
        return string.Join(
            Environment.NewLine,
            $"Label: {DisplayStatusValue(item.Label)}",
            $"Kind: {DisplayStatusValue(item.Kind)}",
            $"Style: {DisplayStatusValue(item.Style)}",
            $"Pressure: {DisplayStatusValue(item.Intensity)}",
            $"Pack: {DisplayStatusValue(item.RolePack)}",
            $"Absurdity: {DisplayStatusValue(item.Absurdity)}",
            $"Scenario seed: {DisplayStatusValue(item.ScenarioSeed)}",
            $"Persona seed: {DisplayStatusValue(item.PersonaSeed)}",
            $"Topic: {DisplayStatusValue(item.Topic)}");
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

    private static string SelectedComboTag(ComboBox combo, string fallback)
    {
        return combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag?.ToString() ?? "").Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }
}
