using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal enum TranscriptDashboardTier
{
    Hidden,
    Compact,
    Medium,
    Wide
}

internal readonly record struct TranscriptDashboardLayout(
    TranscriptDashboardTier Tier,
    bool ShowDiagnostics,
    bool ShowTelemetry,
    bool IsStacked,
    int DiagnosticsColumns,
    int TelemetryColumns,
    double DiagnosticsMinWidth,
    double TelemetryMinWidth)
{
    public bool ShowTopStrip => ShowDiagnostics || ShowTelemetry;
}

internal sealed class TranscriptViewCoordinator
{
    internal const double MediumDashboardMinWidth = 780;
    internal const double WideDashboardMinWidth = 1480;

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
    private readonly CheckBox battleReviewCheckBox;
    private readonly CheckBox memoryNotesCheckBox;
    private readonly CheckBox decisionCardCheckBox;
    private readonly CheckBox autoModeratorCheckBox;
    private readonly CheckBox debugControlsCheckBox;
    private readonly CheckBox styleFitCheckBox;
    private readonly CheckBox voiceDriftEnforcementCheckBox;
    private readonly CheckBox transcriptInternetDetailsCheckBox;
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
    private readonly UniformGrid transcriptDiagnosticsGrid;
    private readonly Border transcriptTelemetryHost;
    private readonly UniformGrid transcriptTelemetryGrid;
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

    private TranscriptDashboardLayout? dashboardLayout;

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
        CheckBox battleReviewCheckBox,
        CheckBox memoryNotesCheckBox,
        CheckBox decisionCardCheckBox,
        CheckBox autoModeratorCheckBox,
        CheckBox debugControlsCheckBox,
        CheckBox styleFitCheckBox,
        CheckBox voiceDriftEnforcementCheckBox,
        CheckBox transcriptInternetDetailsCheckBox,
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
        UniformGrid transcriptDiagnosticsGrid,
        Border transcriptTelemetryHost,
        UniformGrid transcriptTelemetryGrid,
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
        this.battleReviewCheckBox = battleReviewCheckBox;
        this.memoryNotesCheckBox = memoryNotesCheckBox;
        this.decisionCardCheckBox = decisionCardCheckBox;
        this.autoModeratorCheckBox = autoModeratorCheckBox;
        this.debugControlsCheckBox = debugControlsCheckBox;
        this.styleFitCheckBox = styleFitCheckBox;
        this.voiceDriftEnforcementCheckBox = voiceDriftEnforcementCheckBox;
        this.transcriptInternetDetailsCheckBox = transcriptInternetDetailsCheckBox;
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
        this.transcriptDiagnosticsGrid = transcriptDiagnosticsGrid;
        this.transcriptTelemetryHost = transcriptTelemetryHost;
        this.transcriptTelemetryGrid = transcriptTelemetryGrid;
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
            battleReviewCheckBox.IsChecked = currentSettings.ShowBattleReview;
            memoryNotesCheckBox.IsChecked = currentSettings.ShowAgentMemoryNotes;
            decisionCardCheckBox.IsChecked = currentSettings.ShowDecisionCard;
            autoModeratorCheckBox.IsChecked = currentSettings.ShowAutoModerator;
            debugControlsCheckBox.IsChecked = currentSettings.AllowDebugControls;
            styleFitCheckBox.IsChecked = currentSettings.ShowStyleFit;
            voiceDriftEnforcementCheckBox.IsChecked = currentSettings.EnforceVoiceDrift;
            transcriptInternetDetailsCheckBox.IsChecked = currentSettings.ShowTranscriptInternetDetails;
            followChatCheckBox.IsChecked = currentSettings.FollowTranscript;
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

    public void OnBattleReviewChanged()
    {
        UpdateBooleanSetting(
            battleReviewCheckBox,
            value => settings().ShowBattleReview = value,
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

    public void OnAutoModeratorChanged()
    {
        UpdateBooleanSetting(
            autoModeratorCheckBox,
            value => settings().ShowAutoModerator = value,
            shouldPopulate: () => renderedMessages().Count > 0);
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

    public void OnTranscriptInternetDetailsChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var currentSettings = settings();
        currentSettings.ShowTranscriptInternetDetails = currentSettings.AllowDebugControls
            && transcriptInternetDetailsCheckBox.IsChecked == true;
        settingsStore.Save(currentSettings);
        if (renderedMessages().Count > 0)
        {
            populateTranscript(renderedMessages());
        }

        var status = currentSettings.ShowTranscriptInternetDetails
            ? "Debug: transcript internet details shown."
            : "Debug: transcript internet details hidden.";
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    public void OnFollowChatChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var currentSettings = settings();
        currentSettings.FollowTranscript = followChatCheckBox.IsChecked == true;
        settingsStore.Save(currentSettings);
        UpdateViewPresetState();
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

    /// <summary>
    /// Raised with the preset name after one is applied, whichever route asked.
    /// The name lives here rather than in ApplyViewPreset because that method
    /// only ever sees the expanded flags, not which preset produced them.
    /// </summary>
    public Action<string>? PresetChanged { get; set; }

    public void ApplyFocusedPreset()
    {
        ApplyViewPreset(false, false, false, false, false, showDecisionCard: false, showAutoModerator: false, showStyleFit: false, "hidden", true);
        PresetChanged?.Invoke("focused");
    }

    public void ApplyDiagnosticsPreset()
    {
        ApplyViewPreset(false, false, true, false, true, showDecisionCard: false, showAutoModerator: true, showStyleFit: false, "diagnostics", true);
        PresetChanged?.Invoke("diagnostics");
    }

    public void ApplyCompactPreset()
    {
        ApplyViewPreset(true, false, false, false, false, showDecisionCard: false, showAutoModerator: false, showStyleFit: false, "hidden", true);
        PresetChanged?.Invoke("compact");
    }

    public void ApplyReviewPreset()
    {
        ApplyViewPreset(true, true, true, true, true, showDecisionCard: true, showAutoModerator: true, showStyleFit: true, "diagnostics", false);
        PresetChanged?.Invoke("review");
    }

    public void UpdateDashboardLayout(double width, bool force = false)
    {
        var mode = CurrentTopStripMode(settings());
        var layout = ResolveDashboardLayout(width, mode);
        if (!force && dashboardLayout == layout)
        {
            return;
        }

        dashboardLayout = layout;
        var showTopStrip = layout.ShowTopStrip;
        var stacked = showTopStrip && layout.IsStacked;
        Grid.SetRow(transcriptDiagnosticsHost, 0);
        Grid.SetColumn(transcriptDiagnosticsHost, 0);
        Grid.SetColumnSpan(transcriptDiagnosticsHost, stacked ? 2 : 1);
        Grid.SetRow(transcriptTelemetryHost, 0);
        Grid.SetColumn(transcriptTelemetryHost, 0);
        Grid.SetColumnSpan(transcriptTelemetryHost, stacked ? 2 : 1);
        Grid.SetRow(transcriptFiltersHost, stacked ? 1 : 0);
        Grid.SetColumn(transcriptFiltersHost, showTopStrip && !stacked ? 1 : 0);
        Grid.SetColumnSpan(transcriptFiltersHost, showTopStrip && !stacked ? 1 : 2);

        transcriptDiagnosticsHost.Visibility = layout.ShowDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        transcriptTelemetryHost.Visibility = layout.ShowTelemetry ? Visibility.Visible : Visibility.Collapsed;
        transcriptDiagnosticsGrid.Columns = layout.DiagnosticsColumns;
        transcriptDiagnosticsGrid.MinWidth = layout.DiagnosticsMinWidth;
        transcriptTelemetryGrid.Columns = layout.TelemetryColumns;
        transcriptTelemetryGrid.MinWidth = layout.TelemetryMinWidth;
        transcriptTelemetryGrid.Margin = stacked ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        var topStripCorners = stacked
            ? new CornerRadius(8, 8, 0, 0)
            : new CornerRadius(8, 0, 0, 8);
        transcriptDiagnosticsHost.CornerRadius = topStripCorners;
        transcriptTelemetryHost.CornerRadius = topStripCorners;
        transcriptFiltersHost.HorizontalAlignment = showTopStrip
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        transcriptFiltersHost.CornerRadius = stacked
            ? new CornerRadius(0, 0, 8, 8)
            : !showTopStrip
                ? new CornerRadius(8)
                : new CornerRadius(0, 8, 8, 0);
        transcriptFiltersHost.BorderThickness = stacked
            ? new Thickness(1, 0, 1, 1)
            : !showTopStrip
                ? new Thickness(1)
                : new Thickness(0, 1, 1, 1);
        if (layout.ShowDiagnostics)
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
        return dashboardLayout?.ShowDiagnostics == true
            && transcriptDiagnosticsHost.Visibility == Visibility.Visible
            && transcriptDiagnosticsHost.IsVisible;
    }

    public bool IsTelemetryDisplayed()
    {
        return dashboardLayout?.ShowTelemetry == true
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
        transcriptInternetDetailsCheckBox.IsEnabled = allowDebug;
        if (!allowDebug)
        {
            debugMenuPopup.IsOpen = false;
            if (transcriptInternetDetailsCheckBox.IsChecked == true)
            {
                settings().ShowTranscriptInternetDetails = false;
                transcriptInternetDetailsCheckBox.IsChecked = false;
            }
        }
    }

    public void UpdateViewPresetState()
    {
        var activePreset = CurrentViewPresetName(
            compactTranscriptCheckBox.IsChecked == true,
            turnCompareCheckBox.IsChecked == true,
            matchQualityTimelineCheckBox.IsChecked == true,
            battleReviewCheckBox.IsChecked == true,
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

    internal static TranscriptDashboardLayout ResolveDashboardLayout(double width, string? mode)
    {
        var showDiagnostics = mode?.Equals("diagnostics", StringComparison.OrdinalIgnoreCase) == true;
        var showTelemetry = mode?.Equals("telemetry", StringComparison.OrdinalIgnoreCase) == true;
        if (!showDiagnostics && !showTelemetry)
        {
            return new TranscriptDashboardLayout(
                TranscriptDashboardTier.Hidden,
                ShowDiagnostics: false,
                ShowTelemetry: false,
                IsStacked: false,
                DiagnosticsColumns: 6,
                TelemetryColumns: 4,
                DiagnosticsMinWidth: 900,
                TelemetryMinWidth: 560);
        }

        var availableWidth = double.IsFinite(width) ? Math.Max(0, width) : 0;
        if (availableWidth >= WideDashboardMinWidth)
        {
            return new TranscriptDashboardLayout(
                TranscriptDashboardTier.Wide,
                showDiagnostics,
                showTelemetry,
                IsStacked: false,
                DiagnosticsColumns: 6,
                TelemetryColumns: 4,
                DiagnosticsMinWidth: 900,
                TelemetryMinWidth: 560);
        }

        if (availableWidth >= MediumDashboardMinWidth)
        {
            return new TranscriptDashboardLayout(
                TranscriptDashboardTier.Medium,
                showDiagnostics,
                showTelemetry,
                IsStacked: true,
                DiagnosticsColumns: 3,
                TelemetryColumns: 4,
                DiagnosticsMinWidth: 0,
                TelemetryMinWidth: 0);
        }

        return new TranscriptDashboardLayout(
            TranscriptDashboardTier.Compact,
            showDiagnostics,
            showTelemetry,
            IsStacked: true,
            DiagnosticsColumns: 2,
            TelemetryColumns: 2,
            DiagnosticsMinWidth: 0,
            TelemetryMinWidth: 0);
    }

    internal static string CurrentViewPresetName(bool compact, bool compare, bool timeline, bool battleReview, bool memory, bool autoScroll, string topStripMode)
    {
        var normalizedTopStripMode = topStripMode.Trim().ToLowerInvariant();
        return (compact, compare, timeline, battleReview, memory, autoScroll, normalizedTopStripMode) switch
        {
            (false, false, false, false, false, true, "hidden") => "Focused",
            (false, false, true, false, true, true, "diagnostics") => "Diagnostics",
            (true, false, false, false, false, true, "hidden") => "Compact",
            (true, true, true, true, true, false, "diagnostics") => "Review",
            _ => "Custom"
        };
    }

    private void ApplyViewPreset(
        bool compact,
        bool compare,
        bool timeline,
        bool battleReview,
        bool memory,
        bool showDecisionCard,
        bool showAutoModerator,
        bool showStyleFit,
        string topStripMode,
        bool autoScroll)
    {
        setRenderingSnapshot(true);
        try
        {
            compactTranscriptCheckBox.IsChecked = compact;
            turnCompareCheckBox.IsChecked = compare;
            matchQualityTimelineCheckBox.IsChecked = timeline;
            battleReviewCheckBox.IsChecked = battleReview;
            memoryNotesCheckBox.IsChecked = memory;
            decisionCardCheckBox.IsChecked = showDecisionCard;
            autoModeratorCheckBox.IsChecked = showAutoModerator;
            styleFitCheckBox.IsChecked = showStyleFit;
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
        currentSettings.ShowBattleReview = battleReview;
        currentSettings.ShowAgentMemoryNotes = memory;
        currentSettings.ShowDecisionCard = showDecisionCard;
        currentSettings.ShowAutoModerator = showAutoModerator;
        currentSettings.ShowStyleFit = showStyleFit;
        currentSettings.TopStripMode = topStripMode;
        currentSettings.ShowTranscriptDiagnostics = topStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        currentSettings.FollowTranscript = autoScroll;
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
