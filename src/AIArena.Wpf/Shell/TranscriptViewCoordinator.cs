using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class TranscriptViewCoordinator
{
    private readonly WpfSettingsStore settingsStore;
    private readonly Func<WpfSettings> settings;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Action<bool> setRenderingSnapshot;
    private readonly ComboBox avatarStylePicker;
    private readonly ComboBox systemGlyphStylePicker;
    private readonly ComboBox topStripModePicker;
    private readonly CheckBox compactTranscriptCheckBox;
    private readonly CheckBox turnCompareCheckBox;
    private readonly CheckBox matchQualityTimelineCheckBox;
    private readonly CheckBox memoryNotesCheckBox;
    private readonly CheckBox decisionCardCheckBox;
    private readonly CheckBox debugControlsCheckBox;
    private readonly CheckBox styleFitCheckBox;
    private readonly CheckBox voiceDriftEnforcementCheckBox;
    private readonly CheckBox followChatCheckBox;
    private readonly FrameworkElement debugMenuHost;
    private readonly Popup debugMenuPopup;
    private readonly Popup viewMenuPopup;
    private readonly TextBlock viewActivePresetText;
    private readonly Button viewMenuButton;
    private readonly Button viewPresetFocusedButton;
    private readonly Button viewPresetDiagnosticsButton;
    private readonly Button viewPresetCompactButton;
    private readonly Button viewPresetReviewButton;
    private readonly FrameworkElement transcriptDashboardGrid;
    private readonly Border transcriptDiagnosticsHost;
    private readonly Border transcriptTelemetryHost;
    private readonly Border transcriptFiltersHost;
    private readonly Func<IReadOnlyList<TranscriptMessage>> renderedMessages;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Action<IReadOnlyList<TranscriptMessage>> populateTranscript;
    private readonly Action<bool> setTurnCompareMode;
    private readonly Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics;
    private readonly Action closeDiagnostics;
    private readonly Action updateTelemetryTimerState;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    private string dashboardLayout = "";

    public TranscriptViewCoordinator(
        WpfSettingsStore settingsStore,
        Func<WpfSettings> settings,
        Func<bool> isRenderingSnapshot,
        Action<bool> setRenderingSnapshot,
        ComboBox avatarStylePicker,
        ComboBox systemGlyphStylePicker,
        ComboBox topStripModePicker,
        CheckBox compactTranscriptCheckBox,
        CheckBox turnCompareCheckBox,
        CheckBox matchQualityTimelineCheckBox,
        CheckBox memoryNotesCheckBox,
        CheckBox decisionCardCheckBox,
        CheckBox debugControlsCheckBox,
        CheckBox styleFitCheckBox,
        CheckBox voiceDriftEnforcementCheckBox,
        CheckBox followChatCheckBox,
        FrameworkElement debugMenuHost,
        Popup debugMenuPopup,
        Popup viewMenuPopup,
        TextBlock viewActivePresetText,
        Button viewMenuButton,
        Button viewPresetFocusedButton,
        Button viewPresetDiagnosticsButton,
        Button viewPresetCompactButton,
        Button viewPresetReviewButton,
        FrameworkElement transcriptDashboardGrid,
        Border transcriptDiagnosticsHost,
        Border transcriptTelemetryHost,
        Border transcriptFiltersHost,
        Func<IReadOnlyList<TranscriptMessage>> renderedMessages,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Action<IReadOnlyList<TranscriptMessage>> populateTranscript,
        Action<bool> setTurnCompareMode,
        Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics,
        Action closeDiagnostics,
        Action updateTelemetryTimerState,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.setRenderingSnapshot = setRenderingSnapshot;
        this.avatarStylePicker = avatarStylePicker;
        this.systemGlyphStylePicker = systemGlyphStylePicker;
        this.topStripModePicker = topStripModePicker;
        this.compactTranscriptCheckBox = compactTranscriptCheckBox;
        this.turnCompareCheckBox = turnCompareCheckBox;
        this.matchQualityTimelineCheckBox = matchQualityTimelineCheckBox;
        this.memoryNotesCheckBox = memoryNotesCheckBox;
        this.decisionCardCheckBox = decisionCardCheckBox;
        this.debugControlsCheckBox = debugControlsCheckBox;
        this.styleFitCheckBox = styleFitCheckBox;
        this.voiceDriftEnforcementCheckBox = voiceDriftEnforcementCheckBox;
        this.followChatCheckBox = followChatCheckBox;
        this.debugMenuHost = debugMenuHost;
        this.debugMenuPopup = debugMenuPopup;
        this.viewMenuPopup = viewMenuPopup;
        this.viewActivePresetText = viewActivePresetText;
        this.viewMenuButton = viewMenuButton;
        this.viewPresetFocusedButton = viewPresetFocusedButton;
        this.viewPresetDiagnosticsButton = viewPresetDiagnosticsButton;
        this.viewPresetCompactButton = viewPresetCompactButton;
        this.viewPresetReviewButton = viewPresetReviewButton;
        this.transcriptDashboardGrid = transcriptDashboardGrid;
        this.transcriptDiagnosticsHost = transcriptDiagnosticsHost;
        this.transcriptTelemetryHost = transcriptTelemetryHost;
        this.transcriptFiltersHost = transcriptFiltersHost;
        this.renderedMessages = renderedMessages;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.populateTranscript = populateTranscript;
        this.setTurnCompareMode = setTurnCompareMode;
        this.updateDiagnostics = updateDiagnostics;
        this.closeDiagnostics = closeDiagnostics;
        this.updateTelemetryTimerState = updateTelemetryTimerState;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public void InitializeControls()
    {
        setRenderingSnapshot(true);
        try
        {
            var currentSettings = settings();
            ShellUiHelpers.SelectComboTag(avatarStylePicker, CurrentAvatarStyle(currentSettings));
            ShellUiHelpers.SelectComboTag(systemGlyphStylePicker, currentSettings.SystemEventGlyphs ? "glyph" : "fallback");
            ShellUiHelpers.SelectComboTag(topStripModePicker, CurrentTopStripMode(currentSettings));
            compactTranscriptCheckBox.IsChecked = currentSettings.CompactTranscriptMode;
            turnCompareCheckBox.IsChecked = currentSettings.TurnCompareMode;
            matchQualityTimelineCheckBox.IsChecked = currentSettings.ShowMatchQualityTimeline;
            memoryNotesCheckBox.IsChecked = currentSettings.ShowAgentMemoryNotes;
            decisionCardCheckBox.IsChecked = currentSettings.ShowDecisionCard;
            debugControlsCheckBox.IsChecked = currentSettings.AllowDebugControls;
            styleFitCheckBox.IsChecked = currentSettings.ShowStyleFit;
            voiceDriftEnforcementCheckBox.IsChecked = currentSettings.EnforceVoiceDrift;
        }
        finally
        {
            setRenderingSnapshot(false);
        }

        UpdateDebugControlsVisibility();
        UpdateViewPresetState();
        UpdateDashboardLayout(transcriptDashboardGrid.ActualWidth, force: true);
        updateTelemetryTimerState();
    }

    public void OnVisualSettingsChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var currentSettings = settings();
        var avatarStyle = ShellUiHelpers.SelectedComboTag(avatarStylePicker, "pack");
        currentSettings.AvatarStyle = avatarStyle;
        currentSettings.ChampionAvatars = avatarStyle is "pack" or "procedural";
        currentSettings.SystemEventGlyphs = ShellUiHelpers.SelectedComboTag(systemGlyphStylePicker, "glyph") != "fallback";
        currentSettings.TopStripMode = ShellUiHelpers.SelectedComboTag(topStripModePicker, "diagnostics");
        currentSettings.ShowTranscriptDiagnostics = currentSettings.TopStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        currentSettings.AllowDebugControls = debugControlsCheckBox.IsChecked == true;
        settingsStore.Save(currentSettings);
        UpdateDebugControlsVisibility();
        UpdateDashboardLayout(transcriptDashboardGrid.ActualWidth, force: true);
        updateTelemetryTimerState();
        UpdateViewPresetState();
        if (renderedMessages().Count > 0)
        {
            populateTranscript(renderedMessages());
        }
    }

    public void OnCompactTranscriptChanged()
    {
        UpdateBooleanSetting(
            compactTranscriptCheckBox,
            value => settings().CompactTranscriptMode = value,
            shouldPopulate: () => renderedMessages().Count > 0);
    }

    public void OnTurnCompareChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var currentSettings = settings();
        currentSettings.TurnCompareMode = turnCompareCheckBox.IsChecked == true;
        setTurnCompareMode(currentSettings.TurnCompareMode);
        settingsStore.Save(currentSettings);
        if (renderedMessages().Count > 0)
        {
            populateTranscript(renderedMessages());
        }
        UpdateViewPresetState();
    }

    public void OnMatchQualityTimelineChanged()
    {
        UpdateBooleanSetting(
            matchQualityTimelineCheckBox,
            value => settings().ShowMatchQualityTimeline = value,
            shouldPopulate: () => renderedMessages().Count > 0);
    }

    public void OnMemoryNotesChanged()
    {
        UpdateBooleanSetting(
            memoryNotesCheckBox,
            value => settings().ShowAgentMemoryNotes = value,
            shouldPopulate: () => lastRenderedSnapshot() is not null);
    }

    public void OnDecisionCardChanged()
    {
        UpdateBooleanSetting(
            decisionCardCheckBox,
            value => settings().ShowDecisionCard = value,
            shouldPopulate: () => lastRenderedSnapshot() is not null);
    }

    public void OnStyleFitChanged()
    {
        UpdateBooleanSetting(
            styleFitCheckBox,
            value => settings().ShowStyleFit = value,
            shouldPopulate: () => renderedMessages().Count > 0);
    }

    public void OnVoiceDriftEnforcementChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var currentSettings = settings();
        currentSettings.EnforceVoiceDrift = voiceDriftEnforcementCheckBox.IsChecked == true;
        settingsStore.Save(currentSettings);
        var status = currentSettings.EnforceVoiceDrift
            ? "Debug: voice drift enforcement enabled."
            : "Debug: voice drift enforcement disabled.";
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    public void OnFollowChatChanged()
    {
        if (!isRenderingSnapshot())
        {
            UpdateViewPresetState();
        }
    }

    public void ToggleDebugMenu()
    {
        if (!settings().AllowDebugControls)
        {
            return;
        }

        debugMenuPopup.IsOpen = !debugMenuPopup.IsOpen;
    }

    public void ToggleViewMenu()
    {
        viewMenuPopup.IsOpen = !viewMenuPopup.IsOpen;
    }

    public void ApplyFocusedPreset()
    {
        ApplyViewPreset(false, false, false, false, "diagnostics", true);
    }

    public void ApplyDiagnosticsPreset()
    {
        ApplyViewPreset(false, false, true, true, "diagnostics", true);
    }

    public void ApplyCompactPreset()
    {
        ApplyViewPreset(true, false, false, false, "diagnostics", true);
    }

    public void ApplyReviewPreset()
    {
        ApplyViewPreset(true, true, true, true, "diagnostics", false);
    }

    public void UpdateDashboardLayout(double width, bool force = false)
    {
        var mode = CurrentTopStripMode(settings());
        var showTopStrip = !mode.Equals("hidden", StringComparison.OrdinalIgnoreCase) && width >= 1180;
        var showDiagnostics = showTopStrip && mode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        var showTelemetry = showTopStrip && mode.Equals("telemetry", StringComparison.OrdinalIgnoreCase);
        var layout = showDiagnostics ? "diagnostics" : showTelemetry ? "telemetry" : "hidden";
        if (!force && layout.Equals(dashboardLayout, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dashboardLayout = layout;
        Grid.SetRow(transcriptDiagnosticsHost, 0);
        Grid.SetColumn(transcriptDiagnosticsHost, 0);
        Grid.SetColumnSpan(transcriptDiagnosticsHost, 1);
        Grid.SetRow(transcriptTelemetryHost, 0);
        Grid.SetColumn(transcriptTelemetryHost, 0);
        Grid.SetColumnSpan(transcriptTelemetryHost, 1);
        Grid.SetRow(transcriptFiltersHost, 0);
        Grid.SetColumn(transcriptFiltersHost, 1);
        Grid.SetColumnSpan(transcriptFiltersHost, 1);

        transcriptDiagnosticsHost.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        transcriptTelemetryHost.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
        transcriptDiagnosticsHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        transcriptTelemetryHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        transcriptFiltersHost.HorizontalAlignment = showTopStrip
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        transcriptFiltersHost.CornerRadius = !showTopStrip
            ? new CornerRadius(8)
            : new CornerRadius(0, 8, 8, 0);
        transcriptFiltersHost.BorderThickness = !showTopStrip
            ? new Thickness(1)
            : new Thickness(0, 1, 1, 1);
        if (showDiagnostics)
        {
            updateDiagnostics(renderedMessages());
        }
        else
        {
            closeDiagnostics();
        }

        updateTelemetryTimerState();
    }

    public bool IsDiagnosticsDisplayed()
    {
        return dashboardLayout.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
            && transcriptDiagnosticsHost.Visibility == Visibility.Visible
            && transcriptDiagnosticsHost.IsVisible;
    }

    public bool IsTelemetryDisplayed()
    {
        return dashboardLayout.Equals("telemetry", StringComparison.OrdinalIgnoreCase)
            && transcriptTelemetryHost.Visibility == Visibility.Visible
            && transcriptTelemetryHost.IsVisible;
    }

    public string CurrentAvatarStyle()
    {
        return CurrentAvatarStyle(settings());
    }

    public string CurrentTopStripMode()
    {
        return CurrentTopStripMode(settings());
    }

    public void UpdateDebugControlsVisibility()
    {
        var allowDebug = settings().AllowDebugControls;
        debugMenuHost.Visibility = allowDebug ? Visibility.Visible : Visibility.Collapsed;
        if (!allowDebug)
        {
            debugMenuPopup.IsOpen = false;
        }
    }

    public void UpdateViewPresetState()
    {
        var activePreset = CurrentViewPresetName(
            compactTranscriptCheckBox.IsChecked == true,
            turnCompareCheckBox.IsChecked == true,
            matchQualityTimelineCheckBox.IsChecked == true,
            memoryNotesCheckBox.IsChecked == true,
            followChatCheckBox.IsChecked == true,
            CurrentTopStripMode());
        viewActivePresetText.Text = $"Active: {activePreset}";
        viewMenuButton.ToolTip = $"Transcript view controls - {activePreset}";
        StyleViewPresetButton(viewPresetFocusedButton, activePreset.Equals("Focused", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(viewPresetDiagnosticsButton, activePreset.Equals("Diagnostics", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(viewPresetCompactButton, activePreset.Equals("Compact", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(viewPresetReviewButton, activePreset.Equals("Review", StringComparison.OrdinalIgnoreCase));
    }

    internal static string CurrentAvatarStyle(WpfSettings settings)
    {
        var style = settings.AvatarStyle?.Trim().ToLowerInvariant();
        return style switch
        {
            "pack" or "procedural" or "simple" or "initials" => style,
            "champion" => "procedural",
            _ => settings.ChampionAvatars ? "pack" : "simple"
        };
    }

    internal static string CurrentTopStripMode(WpfSettings settings)
    {
        var mode = settings.TopStripMode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "diagnostics" or "telemetry" or "hidden" => mode,
            _ => settings.ShowTranscriptDiagnostics ? "diagnostics" : "hidden"
        };
    }

    internal static string CurrentViewPresetName(bool compact, bool compare, bool timeline, bool memory, bool autoScroll, string topStripMode)
    {
        if (!topStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return "Custom";
        }

        return (compact, compare, timeline, memory, autoScroll) switch
        {
            (false, false, false, false, true) => "Focused",
            (false, false, true, true, true) => "Diagnostics",
            (true, false, false, false, true) => "Compact",
            (true, true, true, true, false) => "Review",
            _ => "Custom"
        };
    }

    private void ApplyViewPreset(
        bool compact,
        bool compare,
        bool timeline,
        bool memory,
        string topStripMode,
        bool autoScroll)
    {
        setRenderingSnapshot(true);
        try
        {
            compactTranscriptCheckBox.IsChecked = compact;
            turnCompareCheckBox.IsChecked = compare;
            matchQualityTimelineCheckBox.IsChecked = timeline;
            memoryNotesCheckBox.IsChecked = memory;
            followChatCheckBox.IsChecked = autoScroll;
            ShellUiHelpers.SelectComboTag(topStripModePicker, topStripMode);
        }
        finally
        {
            setRenderingSnapshot(false);
        }

        var currentSettings = settings();
        currentSettings.CompactTranscriptMode = compact;
        currentSettings.TurnCompareMode = compare;
        currentSettings.ShowMatchQualityTimeline = timeline;
        currentSettings.ShowAgentMemoryNotes = memory;
        currentSettings.TopStripMode = topStripMode;
        currentSettings.ShowTranscriptDiagnostics = topStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        setTurnCompareMode(compare);
        settingsStore.Save(currentSettings);
        UpdateDashboardLayout(transcriptDashboardGrid.ActualWidth, force: true);
        updateTelemetryTimerState();
        populateTranscript(renderedMessages());
        UpdateViewPresetState();
        viewMenuPopup.IsOpen = false;
    }

    private void UpdateBooleanSetting(CheckBox checkBox, Action<bool> update, Func<bool> shouldPopulate)
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        update(checkBox.IsChecked == true);
        settingsStore.Save(settings());
        if (shouldPopulate())
        {
            populateTranscript(renderedMessages());
        }
        UpdateViewPresetState();
    }

    private static void StyleViewPresetButton(Button button, bool isActive)
    {
        button.SetResourceReference(Control.BackgroundProperty, isActive ? "PrimaryBrush" : "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, isActive ? "PrimaryBorderBrush" : "ControlBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, isActive ? "TextBrush" : "MutedTextBrush");
    }
}
