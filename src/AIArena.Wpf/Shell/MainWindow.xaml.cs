using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CoreDialogueMessage = AIArena.Core.Models.DialogueMessage;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using CoreVoiceAdherenceDiagnostic = AIArena.Core.Models.VoiceAdherenceDiagnostic;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using CoreModelProviderConfig = AIArena.Core.Models.ModelProviderConfig;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow : Window
{
    private const string ReleasesUrl = "https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases";
    private readonly SessionStore _coreSessionStore = new();
    private readonly EventLogStore _eventLogStore = new();
    private readonly ModelProviderHealthService _providerHealth = new();
    private readonly ProviderReachabilityService _providerReachabilityService;
    private readonly TranscriptService _transcriptService = new();
    private readonly TurnRunnerService _turnRunner = new();
    private readonly MatchGenerationService _matchGeneration = new();
    private readonly NarratorService _narratorService = new();
    private readonly DiscourseDiagnosticsService _discourseDiagnostics = new();
    private readonly VoiceStyleAdherenceService _voiceStyleAdherenceService = new();
    private readonly InternetToolService _internetToolService;
    private readonly CuratedNewsService _curatedNewsService = new();
    private readonly WpfSettingsStore _wpfSettingsStore = new();
    private readonly ScenarioTemplateStore _scenarioTemplateStore = new();
    private readonly UserGuideWindowHost _userGuideWindowHost = new();
    private readonly SavedStateWorkflowCoordinator? _savedStateCoordinator;
    private readonly TranscriptExportCoordinator? _transcriptExportCoordinator;
    private readonly TranscriptSearchCoordinator? _transcriptSearchCoordinator;
    private readonly TranscriptInsightCoordinator? _transcriptInsightCoordinator;
    private readonly ScenarioWorkflowCoordinator? _scenarioWorkflowCoordinator;
    private readonly OperatorTurnCoordinator? _operatorTurnCoordinator;
    private readonly InternetWorkflowCoordinator? _internetWorkflowCoordinator;
    private readonly ProviderSettingsCoordinator? _providerSettingsCoordinator;
    private readonly TelemetryWorkflowCoordinator? _telemetryWorkflowCoordinator;
    private readonly AgentPerformanceCoordinator? _agentPerformanceCoordinator;
    private readonly DiagnosticsWorkflowCoordinator? _diagnosticsWorkflowCoordinator;
    private readonly MatchSetupCoordinator? _matchSetupCoordinator;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _modelRefreshTimer;
    private readonly DispatcherTimer _providerHealthTimer;
    private readonly List<Button> _agentTurnButtons = [];
    private readonly List<Button> _narratorActionButtons = [];
    private readonly List<Button> _transcriptActionButtons = [];
    private readonly List<CheckBox> _lockControls = [];
    private readonly List<ComboBox> _voiceControls = [];
    private readonly List<ComboBox> _pressureControls = [];
    private readonly SemaphoreSlim _arenaOperationLock = new(1, 1);
    private IReadOnlyList<TranscriptMessage> _lastRenderedMessages = [];
    private IReadOnlyDictionary<string, string> _lastAgentPersonas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private CoreSessionSummary? _activeSession;
    private DateTimeOffset _activeSnapshotWriteUtc;
    private bool _isSelectingTheme;
    private bool _isRenderingSnapshot;
    private bool _arenaBusy;
    private string _transcriptDashboardLayout = "";
    private Button? _breathingOperationButton;
    private bool _decisionCardExpanded;
    private WpfSettings _wpfSettings = new();
    private ThemePalette _theme = ThemePalette.Resolve("system");
    private CancellationTokenSource? _autoChatCancellation;
    private Style? _lockToggleStyle;
    private ArenaViewSnapshot? _lastRenderedSnapshot;

    private SavedStateWorkflowCoordinator SavedStateCoordinator =>
        _savedStateCoordinator ?? throw new InvalidOperationException("Saved-state coordinator is not initialized.");

    private TranscriptExportCoordinator TranscriptExportCoordinator =>
        _transcriptExportCoordinator ?? throw new InvalidOperationException("Transcript export coordinator is not initialized.");

    private TranscriptSearchCoordinator TranscriptSearch =>
        _transcriptSearchCoordinator ?? throw new InvalidOperationException("Transcript search coordinator is not initialized.");

    private TranscriptInsightCoordinator TranscriptInsight =>
        _transcriptInsightCoordinator ?? throw new InvalidOperationException("Transcript insight coordinator is not initialized.");

    private ScenarioWorkflowCoordinator ScenarioWorkflow =>
        _scenarioWorkflowCoordinator ?? throw new InvalidOperationException("Scenario workflow coordinator is not initialized.");

    private OperatorTurnCoordinator OperatorTurn =>
        _operatorTurnCoordinator ?? throw new InvalidOperationException("Operator turn coordinator is not initialized.");

    private InternetWorkflowCoordinator InternetWorkflow =>
        _internetWorkflowCoordinator ?? throw new InvalidOperationException("Internet workflow coordinator is not initialized.");

    private ProviderSettingsCoordinator ProviderSettings =>
        _providerSettingsCoordinator ?? throw new InvalidOperationException("Provider settings coordinator is not initialized.");

    private TelemetryWorkflowCoordinator TelemetryWorkflow =>
        _telemetryWorkflowCoordinator ?? throw new InvalidOperationException("Telemetry workflow coordinator is not initialized.");

    private AgentPerformanceCoordinator AgentPerformance =>
        _agentPerformanceCoordinator ?? throw new InvalidOperationException("Agent performance coordinator is not initialized.");

    private DiagnosticsWorkflowCoordinator DiagnosticsWorkflow =>
        _diagnosticsWorkflowCoordinator ?? throw new InvalidOperationException("Diagnostics workflow coordinator is not initialized.");

    private MatchSetupCoordinator MatchSetup =>
        _matchSetupCoordinator ?? throw new InvalidOperationException("Match setup coordinator is not initialized.");

    private sealed record MatchQualityPoint(
        int Turn,
        string Speaker,
        int Quality,
        DiagnosticHistoryPoint Diagnostics,
        int Tokens);

    private sealed record AutoModeratorAlert(
        string Label,
        string Body,
        string Severity);

    private sealed record RoleDetailPayload(string Title, string Persona);

    private static readonly string[] RoleDetailLabels =
    [
        "Absurd function:",
        "Arena function:",
        "Expertise leak:",
        "Failure bias:",
        "Role pressure:",
        "Persona mixer:",
        "Expression constraint:",
        "Reasoning distortion:"
    ];

    public MainWindow()
    {
        InitializeComponent();
        _providerReachabilityService = new ProviderReachabilityService(_coreSessionStore, _eventLogStore, _providerHealth);
        _internetToolService = new InternetToolService(eventLogStore: _eventLogStore);
        _savedStateCoordinator = new SavedStateWorkflowCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            _scenarioTemplateStore,
            SavedStateModePicker,
            SavedStateNameText,
            SavedStateItemPicker,
            SavedStateNameLabel,
            SavedStateItemLabel,
            SavedStateHelpText,
            SavedStateSelectionDetails,
            SavedStateStatus,
            SavedStateSaveButton,
            SavedStateLoadButton,
            SavedStateDeleteButton,
            () => _activeSession,
            () => _theme,
            () => _isRenderingSnapshot,
            () => _arenaBusy,
            RunArenaBusyForCoordinatorAsync,
            (session, force) => LoadSessionAsync(session, force),
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            ResourceBrush,
            SetArenaRunStatus,
            SetLoadStatus);
        _transcriptInsightCoordinator = new TranscriptInsightCoordinator(
            () => PopulateTranscript(_lastRenderedMessages),
            () => Dispatcher.BeginInvoke(() => TranscriptScrollViewer.ScrollToTop(), DispatcherPriority.Background));
        _transcriptSearchCoordinator = new TranscriptSearchCoordinator(
            this,
            Dispatcher,
            TranscriptSearchPopup,
            TranscriptSearchButton,
            TranscriptSearchText,
            ClearTranscriptSearchButton,
            TranscriptSearchDragHandle,
            TranscriptResultCountText,
            TranscriptTurnFilterPicker,
            TranscriptFilterSystemCheckBox,
            TranscriptFilterAgentsCheckBox,
            TranscriptFilterNarratorCheckBox,
            TranscriptFilterOperatorCheckBox,
            () => _isRenderingSnapshot,
            ResourceBrush,
            IsAgentSpeaker,
            () => TranscriptInsight.TimelineSelectedTurnFilter,
            () => PopulateTranscript(_lastRenderedMessages));
        _transcriptExportCoordinator = new TranscriptExportCoordinator(
            this,
            ExportStatusText,
            () => _activeSession,
            () => _arenaBusy,
            () => _lastRenderedMessages,
            messages => TranscriptSearch.FilterMessages(messages),
            SetLoadStatus,
            SetArenaRunStatus);
        _providerSettingsCoordinator = new ProviderSettingsCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            _providerHealth,
            new ModelPreloadService(),
            new ProviderAutoConfigureService(_providerHealth),
            _arenaOperationLock,
            ProviderPresetPicker,
            ProviderPresetStatusText,
            ProviderBaseUrlText,
            ProviderModelText,
            DefaultModelStatusText,
            AlphaRoleModelText,
            AlphaModelStatusText,
            BetaRoleModelText,
            BetaModelStatusText,
            GammaRoleModelText,
            GammaModelStatusText,
            DeltaRoleModelText,
            DeltaModelStatusText,
            NarratorRoleModelText,
            NarratorModelStatusText,
            RoleModelSummaryText,
            AutoConfigureStrategyPicker,
            AutoConfigureButton,
            ApplyAutoConfigureButton,
            AutoConfigureStatusText,
            AutoConfigureHardwareText,
            AutoConfigureProviderText,
            AutoConfigureRecommendationItems,
            PreloadSelectedModelsButton,
            LoadPlanPreviewText,
            PreloadModelsStatusText,
            PreloadModelsItems,
            ProviderTimeoutText,
            ProviderTestStatus,
            ProviderModelsStatus,
            () => _activeSession,
            () => _lastRenderedSnapshot,
            () => _theme,
            () => _isRenderingSnapshot,
            () => AppSettingsPanel.Visibility == Visibility.Visible,
            ResourceBrush,
            AccentForSpeaker,
            ShortModelName,
            DisplayStatusValue,
            LoadSharedProviderConfigAsync,
            (online, error, latencyMs, status) => PersistProviderReachabilityAsync(online, error, latencyMs, status),
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            force => RefreshProviderReachabilityAsync(force),
            () => UpdateProviderHealthPopup());
        _wpfSettings = _wpfSettingsStore.Load();
        _scenarioWorkflowCoordinator = new ScenarioWorkflowCoordinator(
            this,
            _matchGeneration,
            _wpfSettingsStore,
            RandomSeedPresetPicker,
            RandomSeedRolePackPicker,
            RandomSeedStylePicker,
            RandomSeedIntensityPicker,
            RandomSeedAbsurdityPicker,
            RandomSeedButton,
            AiChoiceButton,
            YoloScenarioButton,
            GenerationHistoryPicker,
            ReplayGenerationButton,
            ReplayNewRunButton,
            CopyGenerationSeedButton,
            () => _wpfSettings,
            () => _activeSession,
            () => _theme,
            () => _isRenderingSnapshot,
            () => _arenaBusy,
            RunArenaBusyForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            SetLoadStatus,
            SetArenaRunStatus);
        _operatorTurnCoordinator = new OperatorTurnCoordinator(
            _coreSessionStore,
            _eventLogStore,
            _transcriptService,
            _narratorService,
            _wpfSettingsStore,
            OperatorPublicRouteButton,
            OperatorPrivateRouteButton,
            OperatorNarratorRouteButton,
            OperatorPrivateTargetRow,
            OperatorPrivateTargetPicker,
            OperatorPrivateTargetSummaryText,
            OperatorRouteHintText,
            OperatorTurnMeterText,
            OperatorTemplatePicker,
            OperatorTurnText,
            SendTurnButton,
            () => _wpfSettings,
            () => _activeSession,
            () => _lastRenderedSnapshot,
            () => _isRenderingSnapshot,
            ResourceBrush,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetLoadStatus,
            SetArenaRunStatus);
        _internetWorkflowCoordinator = new InternetWorkflowCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            _transcriptService,
            _internetToolService,
            _curatedNewsService,
            UseInternetCheckBox,
            InternetModePicker,
            InternetModeHintText,
            InternetSourceScopePicker,
            InternetMaxResultsText,
            AllowParticipantInternetCheckBox,
            AllowNarratorInternetCheckBox,
            RequireInternetApprovalCheckBox,
            CurateNewsButton,
            () => _activeSession,
            () => _theme,
            () => _arenaBusy,
            () => _isRenderingSnapshot,
            ResourceBrush,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetLoadStatus,
            SetArenaRunStatus);
        ApplyTheme(_wpfSettings.ThemeId, persist: false, rerender: false);
        InitializeThemePicker();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _refreshTimer.Tick += (_, _) => RefreshIfSnapshotChanged();
        _modelRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _modelRefreshTimer.Tick += async (_, _) => await RefreshAdvertisedModelsAsync();
        _providerHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _providerHealthTimer.Tick += async (_, _) => await RefreshProviderReachabilityAsync();
        _telemetryWorkflowCoordinator = new TelemetryWorkflowCoordinator(
            TelemetryCpuValueText,
            TelemetryCpuSparkline,
            TelemetryGpuValueText,
            TelemetryGpuDetailText,
            TelemetryGpuSparkline,
            TelemetryVramValueText,
            TelemetryVramDetailText,
            TelemetryVramUsageBar,
            TelemetryRamValueText,
            TelemetryRamDetailText,
            TelemetryRamUsageBar,
            () => _transcriptDashboardLayout.Equals("telemetry", StringComparison.OrdinalIgnoreCase)
                && TranscriptTelemetryHost.Visibility == Visibility.Visible
                && TranscriptTelemetryHost.IsVisible,
            ResourceBrush);
        _agentPerformanceCoordinator = new AgentPerformanceCoordinator(
            _voiceStyleAdherenceService,
            AgentPerformanceItems,
            AgentPerformanceDetailPopup,
            AgentPerformanceDetailContent,
            ResourceBrush,
            AccentForSpeaker,
            FormatParticipantTitle,
            DisplayStatusValue,
            DisplayInlineStatus,
            ShortModelName,
            FormatCompactNumber,
            FormatDuration,
            VoiceStyleChipText,
            VoiceAdherenceState,
            VoiceAdherenceDisplayState,
            state => VoiceAdherenceAccent(state),
            diagnostic => VoiceAdherenceAccent(diagnostic),
            VoiceAdherenceTooltip,
            ShellUiHelpers.CompactPreview,
            BlendBrush);
        _diagnosticsWorkflowCoordinator = new DiagnosticsWorkflowCoordinator(
            _discourseDiagnostics,
            FrictionChip,
            FrictionValueText,
            FrictionTrendText,
            ConsensusChip,
            ConsensusValueText,
            ConsensusTrendText,
            ConsensusSparkline,
            RoleDriftChip,
            RoleDriftValueText,
            RoleDriftTrendText,
            RoleDriftSparkline,
            UnsupportedClaimsChip,
            UnsupportedClaimsValueText,
            UnsupportedClaimsTrendText,
            UnsupportedClaimsSparkline,
            EvidencePressureChip,
            EvidencePressureValueText,
            EvidencePressureTrendText,
            EvidencePressureSparkline,
            NarrativeHeatChip,
            NarrativeHeatValueText,
            NarrativeHeatTrendText,
            NarrativeHeatSparkline,
            DiagnosticDetailPopup,
            DiagnosticDetailTitleText,
            DiagnosticDetailSubtitleText,
            DiagnosticDetailContent,
            () => _lastAgentPersonas,
            () => _lastRenderedMessages,
            ResourceBrush,
            DisplayStatusValue,
            IsSystemEvent,
            BlendBrush);
        _matchSetupCoordinator = new MatchSetupCoordinator(
            _coreSessionStore,
            _eventLogStore,
            RivalryMatrixEnabledCheckBox,
            RivalryMatrixRows,
            RivalryMatrixStatusText,
            ApplyRivalryMatrixButton,
            () => _activeSession,
            ResourceBrush,
            AccentForSpeaker,
            DisplayStatusValue,
            BlendBrush,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync);
        InitializeAboutPanel();
        InitializeVisualSettings();
        ScenarioWorkflow.InitializeControls();
        OperatorTurn.InitializeControls();
        InternetWorkflow.InitializeControls();
        DiagnosticsWorkflow.InitializeTiles();
        SavedStateCoordinator.LoadScenarioTemplates();
        ShowStoreLoadWarningIfAny();
        Loaded += (_, _) =>
        {
            LoadSessions();
            _refreshTimer.Start();
            _providerHealthTimer.Start();
            TelemetryWorkflow.UpdateTimerState();
            _ = RefreshProviderReachabilityAsync(force: true);
        };
        SourceInitialized += (_, _) => ApplyNativeChromeColor();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _modelRefreshTimer.Stop();
            _providerHealthTimer.Stop();
            TelemetryWorkflow.Stop();
        };
    }

    private void InitializeAboutPanel()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        AboutVersionText.Text = $"Version {CleanDisplayVersion(version)}";
        OpenReleasesButton.ToolTip = ReleasesUrl;
    }

    private static string CleanDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? version[..plusIndex] : version;
    }

    private void InitializeThemePicker()
    {
        var themeId = ThemePalette.NormalizeId(_wpfSettings.ThemeId);
        _wpfSettings.ThemeId = themeId;
        var themes = ThemePalette.BuiltIn
            .Where(item => !item.Id.Equals("system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _isSelectingTheme = true;
        ThemePicker.ItemsSource = themes;
        ThemePicker.SelectedValue = themes.Any(item => item.Id == themeId)
            ? themeId
            : "dark-blue";
        _isSelectingTheme = false;
    }

    private void InitializeVisualSettings()
    {
        _isRenderingSnapshot = true;
        ShellUiHelpers.SelectComboTag(AvatarStylePicker, CurrentAvatarStyle());
        ShellUiHelpers.SelectComboTag(SystemGlyphStylePicker, _wpfSettings.SystemEventGlyphs ? "glyph" : "fallback");
        ShellUiHelpers.SelectComboTag(TopStripModePicker, CurrentTopStripMode());
        CompactTranscriptCheckBox.IsChecked = _wpfSettings.CompactTranscriptMode;
        TurnCompareCheckBox.IsChecked = _wpfSettings.TurnCompareMode;
        MatchQualityTimelineCheckBox.IsChecked = _wpfSettings.ShowMatchQualityTimeline;
        MemoryNotesCheckBox.IsChecked = _wpfSettings.ShowAgentMemoryNotes;
        DecisionCardCheckBox.IsChecked = _wpfSettings.ShowDecisionCard;
        DebugControlsCheckBox.IsChecked = _wpfSettings.AllowDebugControls;
        StyleFitCheckBox.IsChecked = _wpfSettings.ShowStyleFit;
        VoiceDriftEnforcementCheckBox.IsChecked = _wpfSettings.EnforceVoiceDrift;
        _isRenderingSnapshot = false;
        UpdateDebugControlsVisibility();
        UpdateViewPresetState();
        UpdateTranscriptDashboardLayout(TranscriptDashboardGrid.ActualWidth, force: true);
        TelemetryWorkflow.UpdateTimerState();
    }


    private async void LoadSessions(string? preferredSessionId = null)
    {
        await LoadSessionsAsync(preferredSessionId);
    }

    private async Task LoadSessionsAsync(string? preferredSessionId = null)
    {
        var sessions = await _coreSessionStore.ListSessionsAsync();
        if (sessions.Count == 0)
        {
            await _coreSessionStore.EnsureDefaultSessionAsync();
            sessions = await _coreSessionStore.ListSessionsAsync();
        }

        SavedStateCoordinator.SetSessions(sessions);

        var defaultSession = sessions.FirstOrDefault(session => session.Id.Equals(preferredSessionId, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals(_activeSession?.Id, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault();

        if (defaultSession is null)
        {
            LoadStatus.Text = $"No sessions found in {Path.Combine(_coreSessionStore.DataRoot, "sessions")}";
            SavedStateCoordinator.SetStatus("No saved sessions found.", isDanger: true);
            SavedStateCoordinator.UpdatePicker();
            PopulateFallbackState("No AI Arena sessions found.");
            return;
        }

        await LoadSessionAsync(defaultSession, force: true);
        SavedStateCoordinator.UpdatePicker(defaultSession.Id);
    }

    private void ShowStoreLoadWarningIfAny()
    {
        var warning = new[] { _wpfSettingsStore.LastLoadWarning, _scenarioTemplateStore.LastLoadWarning }
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        LoadStatus.Text = warning;
        ArenaRunStatus.Text = warning;
    }

    private async void RefreshIfSnapshotChanged()
    {
        if (_activeSession is null)
        {
            await LoadSessionsAsync();
            return;
        }

        var sessions = await _coreSessionStore.ListSessionsAsync();
        var latestSession = sessions.FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latestSession is null)
        {
            await LoadSessionsAsync();
            return;
        }

        if (latestSession.LastModified != _activeSnapshotWriteUtc)
        {
            await LoadSessionAsync(latestSession, force: true);
        }
    }

    private void CompactTranscriptCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || CompactTranscriptCheckBox is null)
        {
            return;
        }

        _wpfSettings.CompactTranscriptMode = CompactTranscriptCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void TurnCompareCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || TurnCompareCheckBox is null)
        {
            return;
        }

        _wpfSettings.TurnCompareMode = TurnCompareCheckBox.IsChecked == true;
        TranscriptInsight.SetTurnCompareMode(_wpfSettings.TurnCompareMode);
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void MatchQualityTimelineCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || MatchQualityTimelineCheckBox is null)
        {
            return;
        }

        _wpfSettings.ShowMatchQualityTimeline = MatchQualityTimelineCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void MemoryNotesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || MemoryNotesCheckBox is null)
        {
            return;
        }

        _wpfSettings.ShowAgentMemoryNotes = MemoryNotesCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedSnapshot is not null)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void DecisionCardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || DecisionCardCheckBox is null)
        {
            return;
        }

        _wpfSettings.ShowDecisionCard = DecisionCardCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedSnapshot is not null)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void StyleFitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || StyleFitCheckBox is null)
        {
            return;
        }

        _wpfSettings.ShowStyleFit = StyleFitCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
        UpdateViewPresetState();
    }

    private void VoiceDriftEnforcementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || VoiceDriftEnforcementCheckBox is null)
        {
            return;
        }

        _wpfSettings.EnforceVoiceDrift = VoiceDriftEnforcementCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        LoadStatus.Text = _wpfSettings.EnforceVoiceDrift
            ? "Debug: voice drift enforcement enabled."
            : "Debug: voice drift enforcement disabled.";
        ArenaRunStatus.Text = LoadStatus.Text;
    }

    private void RandomSeedPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        _scenarioWorkflowCoordinator?.OnRandomSeedPresetChanged();
    }

    private void RandomSeedOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        _scenarioWorkflowCoordinator?.OnRandomSeedOptionsChanged();
    }

    private void AgentCountPresetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingSnapshot || AgentCountPicker is null || AgentCountPresetPicker is null)
        {
            return;
        }

        var preset = ShellUiHelpers.SelectedComboTag(AgentCountPresetPicker, "4");
        if (!preset.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            ShellUiHelpers.SelectComboTag(AgentCountPicker, preset);
        }
    }

    private async void ApplyAgentCountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_arenaBusy)
        {
            return;
        }

        if (_activeSession is null)
        {
            await _coreSessionStore.EnsureDefaultSessionAsync();
            await LoadSessionsAsync("default");
        }

        if (_activeSession is null)
        {
            ArenaRunStatus.Text = "No session is available for agent roster changes.";
            return;
        }

        var count = SelectedAgentCount();
        await RunArenaBusyAsync($"Resizing cast to {count} agents...", ApplyAgentCountButton, async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id) ?? SessionStore.CreateDefaultSnapshot();
            AgentRosterService.EnsureParticipantCount(snapshot, count);
            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_roster_resized", new
            {
                Count = count,
                Agents = snapshot.Engine.Agents.Where(agent => agent.Active).Select(agent => agent.Id).ToArray()
            });
            await RefreshActiveSessionAsync($"Agent roster resized: {count} active agents.");
        }, allowDuringAutoChat: true);
    }

    private void GenerationHelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        var (title, body) = GenerationHelpText(key);
        GenerationHelpTitleText.Text = title;
        GenerationHelpBodyText.Text = body;
        GenerationHelpPopup.IsOpen = false;
        GenerationHelpPopup.IsOpen = true;
    }

    private static (string Title, string Body) GenerationHelpText(string key)
    {
        return key switch
        {
            "generate" => (
                "Generate",
                "Manual keeps your current tune settings. Random Seed is deterministic and local. AI Choice asks the configured model to build a match. YOLO creates a seeded meta scenario and cast while respecting locks."),
            "tune" => (
                "Tune",
                "Role pack chooses the cast family. Style chooses the scenario domain. Pressure changes how hard the debate pushes. Absurdity mixes expertise, expression constraints, and reasoning distortions."),
            "recent" => (
                "Recent",
                "Recent stores generated setups in the session snapshot. Replay restores the selected setup without another model call. Copy Seed copies the scenario seed for sharing or reruns."),
            _ => (
                "Custom Match",
                "Use generation controls to create scenario/cast setups, then lock anything you want to preserve before generating again.")
        };
    }

    private void FollowChatCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || FollowChatCheckBox is null)
        {
            return;
        }

        UpdateViewPresetState();
    }

    private void DebugMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_wpfSettings.AllowDebugControls)
        {
            DebugMenuPopup.IsOpen = false;
            return;
        }

        DebugMenuPopup.IsOpen = !DebugMenuPopup.IsOpen;
    }

    private void UpdateDebugControlsVisibility()
    {
        if (DebugMenuHost is null)
        {
            return;
        }

        var allowDebug = _wpfSettings.AllowDebugControls;
        DebugMenuHost.Visibility = allowDebug ? Visibility.Visible : Visibility.Collapsed;
        if (DebugMenuPopup is not null && !allowDebug)
        {
            DebugMenuPopup.IsOpen = false;
        }
    }

    private void ViewMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ViewMenuPopup.IsOpen = !ViewMenuPopup.IsOpen;
    }

    private void ViewPresetFocused_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewPreset(
            compact: false,
            compare: false,
            timeline: false,
            memory: false,
            topStripMode: "diagnostics",
            autoScroll: true);
    }

    private void ViewPresetDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewPreset(
            compact: false,
            compare: false,
            timeline: true,
            memory: true,
            topStripMode: "diagnostics",
            autoScroll: true);
    }

    private void ViewPresetCompact_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewPreset(
            compact: true,
            compare: false,
            timeline: false,
            memory: false,
            topStripMode: "diagnostics",
            autoScroll: true);
    }

    private void ViewPresetReview_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewPreset(
            compact: true,
            compare: true,
            timeline: true,
            memory: true,
            topStripMode: "diagnostics",
            autoScroll: false);
    }

    private void ApplyViewPreset(
        bool compact,
        bool compare,
        bool timeline,
        bool memory,
        string topStripMode,
        bool autoScroll)
    {
        _isRenderingSnapshot = true;
        try
        {
            CompactTranscriptCheckBox.IsChecked = compact;
            TurnCompareCheckBox.IsChecked = compare;
            MatchQualityTimelineCheckBox.IsChecked = timeline;
            MemoryNotesCheckBox.IsChecked = memory;
            FollowChatCheckBox.IsChecked = autoScroll;
            ShellUiHelpers.SelectComboTag(TopStripModePicker, topStripMode);
        }
        finally
        {
            _isRenderingSnapshot = false;
        }

        _wpfSettings.CompactTranscriptMode = compact;
        _wpfSettings.TurnCompareMode = compare;
        _wpfSettings.ShowMatchQualityTimeline = timeline;
        _wpfSettings.ShowAgentMemoryNotes = memory;
        _wpfSettings.TopStripMode = topStripMode;
        _wpfSettings.ShowTranscriptDiagnostics = topStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        TranscriptInsight.SetTurnCompareMode(compare);
        _wpfSettingsStore.Save(_wpfSettings);
        UpdateTranscriptDashboardLayout(TranscriptDashboardGrid.ActualWidth, force: true);
        TelemetryWorkflow.UpdateTimerState();
        PopulateTranscript(_lastRenderedMessages);
        UpdateViewPresetState();
        ViewMenuPopup.IsOpen = false;
    }

    private void UpdateViewPresetState()
    {
        if (ViewActivePresetText is null
            || ViewMenuButton is null
            || ViewPresetFocusedButton is null
            || ViewPresetDiagnosticsButton is null
            || ViewPresetCompactButton is null
            || ViewPresetReviewButton is null)
        {
            return;
        }

        var activePreset = CurrentViewPresetName();
        ViewActivePresetText.Text = $"Active: {activePreset}";
        ViewMenuButton.ToolTip = $"Transcript view controls - {activePreset}";
        StyleViewPresetButton(ViewPresetFocusedButton, activePreset.Equals("Focused", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(ViewPresetDiagnosticsButton, activePreset.Equals("Diagnostics", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(ViewPresetCompactButton, activePreset.Equals("Compact", StringComparison.OrdinalIgnoreCase));
        StyleViewPresetButton(ViewPresetReviewButton, activePreset.Equals("Review", StringComparison.OrdinalIgnoreCase));
    }

    private string CurrentViewPresetName()
    {
        var compact = CompactTranscriptCheckBox?.IsChecked == true;
        var compare = TurnCompareCheckBox?.IsChecked == true;
        var timeline = MatchQualityTimelineCheckBox?.IsChecked == true;
        var memory = MemoryNotesCheckBox?.IsChecked == true;
        var autoScroll = FollowChatCheckBox?.IsChecked == true;
        var diagnostics = CurrentTopStripMode().Equals("diagnostics", StringComparison.OrdinalIgnoreCase);

        if (!diagnostics)
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

    private static void StyleViewPresetButton(Button button, bool isActive)
    {
        button.SetResourceReference(Control.BackgroundProperty, isActive ? "PrimaryBrush" : "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, isActive ? "PrimaryBorderBrush" : "ControlBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, isActive ? "TextBrush" : "MutedTextBrush");
    }

    private async Task LoadSessionAsync(CoreSessionSummary session, bool force)
    {
        if (!force && session.LastModified == _activeSnapshotWriteUtc)
        {
            return;
        }

        try
        {
            var coreSnapshot = await _coreSessionStore.LoadSnapshotAsync(session.Id);
            var snapshot = coreSnapshot is null
                ? SnapshotViewMapper.Empty(session, "No snapshot file.")
                : SnapshotViewMapper.FromCore(session, coreSnapshot);
            _activeSession = session;
            _activeSnapshotWriteUtc = session.LastModified;
            RenderSnapshot(snapshot);
            SavedStateCoordinator.RefreshCheckpoints();
            LoadStatus.Text = $"Loaded read-only snapshot: {snapshot.SnapshotPath}\nAuto-refresh: 1.2s";
        }
        catch (Exception ex)
        {
            _activeSession = session;
            _activeSnapshotWriteUtc = session.LastModified;
            PopulateFallbackState($"Could not load snapshot: {ex.Message}");
            SavedStateCoordinator.ClearCheckpoints("No checkpoint data.");
            LoadStatus.Text = $"Could not load session '{session.Id}': {ex.Message}";
        }
    }

    private void RenderSnapshot(ArenaViewSnapshot snapshot)
    {
        UpdateTopBarStatus(snapshot);
        _lastRenderedSnapshot = snapshot;
        if (!_arenaBusy)
        {
            ArenaRunStatus.Text = snapshot.ProviderOnline ? "Ready." : "Provider offline. Check LM Studio or the provider base URL.";
        }
        _isRenderingSnapshot = true;
        var activeCount = snapshot.Agents.Count(agent => agent.Active);
        ShellUiHelpers.SelectComboTag(ActiveParticipantsPicker, Math.Clamp(activeCount, 1, AgentRosterService.MaxParticipants).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectAgentCountControls(activeCount);
        ProviderSettings.ApplySnapshot(snapshot);
        ProviderTimeoutText.Text = snapshot.ProviderTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProviderTemperatureText.Text = snapshot.ProviderTemperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        ProviderMaxOutputText.Text = snapshot.ProviderMaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextTranscriptWindowText.Text = snapshot.TranscriptWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextPrivateWindowText.Text = snapshot.PrivateWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextNotesWindowText.Text = snapshot.NotesWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextSummaryText.Text = string.IsNullOrWhiteSpace(snapshot.Summary) ? "No summary has been generated for this session." : snapshot.Summary;
        InternetWorkflow.ApplySnapshot(snapshot);
        OperatorTurn.ApplySnapshot(snapshot);
        _isRenderingSnapshot = false;
        InternetWorkflow.UpdateSettingsHint();
        OperatorTurn.UpdatePrivateTargetSummary();
        UpdateSessionOverview(snapshot);
        _lastAgentPersonas = snapshot.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToDictionary(agent => agent.Id, agent => agent.Persona, StringComparer.OrdinalIgnoreCase);
        PopulateTranscript(snapshot.Messages);
        PopulateAgents(snapshot);
        PopulateCustomMatch(snapshot);
        PopulateNews(snapshot.Messages);
        OperatorTurn.UpdatePrivateTargetSummary();
    }

    private void UpdateSessionOverview(ArenaViewSnapshot snapshot)
    {
        SessionOverviewMatchText.Text = DisplayStatusValue(snapshot.MatchType);
        SessionOverviewTurnsText.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SessionOverviewParticipantsText.Text = $"{snapshot.Agents.Count(agent => agent.Active)} agents + operator";
        SessionOverviewTokensText.Text = FormatCompactNumber(snapshot.Messages.Sum(message => Math.Max(message.CompletionTokens, 0)));
        SessionOverviewProviderText.Text = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        SessionOverviewProviderText.Foreground = snapshot.ProviderOnline ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DangerTextBrush");
        var context = snapshot.Messages.Select(message => message.PromptTokens).DefaultIfEmpty(0).Max();
        SessionOverviewContextText.Text = context > 0 ? FormatCompactNumber(context) : "-";
        AgentPerformance.Populate(snapshot);
    }


    private void PopulateFallbackState(string message)
    {
        TranscriptItems.Children.Clear();
        AgentItems.Children.Clear();
        NewsItems.Children.Clear();
        AgentPerformance.CloseDetail();
        _lastRenderedSnapshot = null;
        _agentTurnButtons.Clear();
        _narratorActionButtons.Clear();
        TranscriptItems.Children.Add(CreateCard("Transcript", message, ResourceBrush("CardBrush"), ResourceBrush("AlphaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Alpha", "waiting", ResourceBrush("AlphaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Beta", "waiting", ResourceBrush("BetaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Gamma", "waiting", ResourceBrush("GammaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Delta", "waiting", ResourceBrush("DeltaAccentBrush")));
        NewsItems.Children.Add(CreateCard("News", "No live snapshot data.", ResourceBrush("CardBrush"), ResourceBrush("NarratorAccentBrush")));
    }

    private void UpdateTopBarStatus(ArenaViewSnapshot snapshot)
    {
        TopMatchValue.Text = DisplayStatusValue(snapshot.MatchType);
        TopProviderValue.Text = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        TopProviderValue.Foreground = snapshot.ProviderOnline ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DangerTextBrush");
        TopProviderValue.ToolTip = $"Provider details - {snapshot.ProviderBaseUrl}";
        var current = CurrentTurnAgent(snapshot);
        TopCurrentTurnValue.Text = current?.Id.ToUpperInvariant() ?? "-";
        TopCurrentTurnValue.Foreground = current is null ? ResourceBrush("TextBrush") : AccentForSpeaker(current.Id);
        TopCurrentTurnValue.ToolTip = current is null
            ? "No active turn participant."
            : $"{DisplayStatusValue(current.Id)}: {current.Name}\nModel: {CurrentTurnModel(snapshot, current)}";
        TopTurnsValue.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        TopBarStatus.ToolTip = $"Session: {snapshot.SessionId}\nModel: {CurrentTurnModel(snapshot, current)}";
        if (!_arenaBusy && _autoChatCancellation is null)
        {
            ArenaRunStatus.Text = TopRunStateSummary(snapshot, current);
        }

        UpdateSettingsProviderStatus(snapshot);
    }

    private static string TopRunStateSummary(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var provider = snapshot.ProviderOnline ? "provider online" : "provider offline";
        if (current is null)
        {
            return $"Ready: no active turn participant; {provider}.";
        }

        var model = ShortModelName(CurrentTurnModel(snapshot, current));
        return $"Ready: next {DisplayStatusValue(current.Id)} using {model}; {provider}.";
    }

    private void UpdateSettingsProviderStatus(ArenaViewSnapshot snapshot)
    {
        var status = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        var detail = snapshot.ProviderOnline
            ? snapshot.ProviderModel
            : string.IsNullOrWhiteSpace(snapshot.ProviderLastError)
                ? snapshot.ProviderBaseUrl
                : snapshot.ProviderLastError;
        SettingsProviderStatusText.Text = $"Provider {status} - {detail}";
        SettingsProviderStatusText.Foreground = snapshot.ProviderOnline
            ? ResourceBrush("PrimaryBorderBrush")
            : ResourceBrush("DangerTextBrush");
        UpdateProviderHealthPopup(snapshot);
    }

    private static AgentState? CurrentTurnAgent(ArenaViewSnapshot snapshot)
    {
        var active = snapshot.Agents.Where(agent => agent.Active).ToArray();
        return active.Length == 0
            ? null
            : active[Math.Clamp(snapshot.TurnIndex, 0, int.MaxValue) % active.Length];
    }

    private static string DisplayStatusValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-"
            ? "-"
            : value.Trim().ToUpperInvariant();
    }

    private void PopulateTranscript(IReadOnlyList<TranscriptMessage> messages)
    {
        _lastRenderedMessages = messages;
        TranscriptItems.Children.Clear();
        _transcriptActionButtons.Clear();
        TranscriptInsight.ClearTimelineFilterIfMissing(messages);

        var visibleMessages = TranscriptSearch.FilterMessages(messages).ToArray();
        TranscriptSearch.UpdateResultCount(visibleMessages.Length, messages.Count);
        TranscriptSearch.UpdateSearchState();
        if (IsDiagnosticsDisplayed())
        {
            DiagnosticsWorkflow.Update(messages);
        }

        if (messages.Count == 0)
        {
            if (_wpfSettings.ShowAgentMemoryNotes && _lastRenderedSnapshot is not null)
            {
                TranscriptItems.Children.Add(CreateAgentMemoryPanel(_lastRenderedSnapshot));
            }

            TranscriptItems.Children.Add(CreateArenaReadyCard(_lastRenderedSnapshot));
            return;
        }

        if (visibleMessages.Length == 0)
        {
            if (_wpfSettings.ShowMatchQualityTimeline)
            {
                TranscriptItems.Children.Add(CreateMatchQualityTimelinePanel(messages));
            }
            if (_wpfSettings.ShowAgentMemoryNotes && _lastRenderedSnapshot is not null)
            {
                TranscriptItems.Children.Add(CreateAgentMemoryPanel(_lastRenderedSnapshot));
            }

            TranscriptItems.Children.Add(CreateEmptyStateCard(
                "No transcript matches",
                "Adjust the search text, turn preset, or speaker filters to widen the view.",
                ResourceBrush("MutedTextBrush")));
            return;
        }

        var retryableTurns = visibleMessages
            .Where(message => IsAgentSpeaker(message.SpeakerId))
            .OrderByDescending(message => message.Turn)
            .Take(3)
            .Select(message => message.Turn)
            .ToHashSet();

        var latestTurn = visibleMessages.Max(message => message.Turn);
        if (_wpfSettings.ShowMatchQualityTimeline)
        {
            TranscriptItems.Children.Add(CreateMatchQualityTimelinePanel(messages));
        }
        if (ShouldShowDecisionCard() && _lastRenderedSnapshot is not null)
        {
            TranscriptItems.Children.Add(CreateDecisionCardPanel(_lastRenderedSnapshot));
        }
        if (_wpfSettings.TurnCompareMode)
        {
            TranscriptInsight.EnsureTurnCompareSelection(visibleMessages);
            TranscriptItems.Children.Add(CreateTurnComparePanel(visibleMessages));
        }
        if (_wpfSettings.ShowAgentMemoryNotes && _lastRenderedSnapshot is not null)
        {
            TranscriptItems.Children.Add(CreateAgentMemoryPanel(_lastRenderedSnapshot));
        }
        var moderatorPanel = CreateAutoModeratorPanel(messages);
        if (moderatorPanel is not null)
        {
            TranscriptItems.Children.Add(moderatorPanel);
        }

        foreach (var message in visibleMessages.OrderByDescending(message => message.Turn))
        {
            TranscriptItems.Children.Add(CreateTranscriptCard(
                message,
                retryableTurns.Contains(message.Turn),
                TranscriptSearch.HasActiveSearch,
                message.Turn == latestTurn));
        }

        if (FollowChatCheckBox.IsChecked == true)
        {
            Dispatcher.BeginInvoke(() => TranscriptScrollViewer.ScrollToTop(), DispatcherPriority.Background);
        }
    }

    private void TranscriptDashboardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTranscriptDashboardLayout(e.NewSize.Width);
    }

    private void UpdateTranscriptDashboardLayout(double width, bool force = false)
    {
        var mode = CurrentTopStripMode();
        var showTopStrip = !mode.Equals("hidden", StringComparison.OrdinalIgnoreCase) && width >= 1180;
        var showDiagnostics = showTopStrip && mode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        var showTelemetry = showTopStrip && mode.Equals("telemetry", StringComparison.OrdinalIgnoreCase);
        var layout = showDiagnostics ? "diagnostics" : showTelemetry ? "telemetry" : "hidden";
        if (!force && layout.Equals(_transcriptDashboardLayout, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _transcriptDashboardLayout = layout;
        Grid.SetRow(TranscriptDiagnosticsHost, 0);
        Grid.SetColumn(TranscriptDiagnosticsHost, 0);
        Grid.SetColumnSpan(TranscriptDiagnosticsHost, 1);
        Grid.SetRow(TranscriptTelemetryHost, 0);
        Grid.SetColumn(TranscriptTelemetryHost, 0);
        Grid.SetColumnSpan(TranscriptTelemetryHost, 1);
        Grid.SetRow(TranscriptFiltersHost, 0);
        Grid.SetColumn(TranscriptFiltersHost, 1);
        Grid.SetColumnSpan(TranscriptFiltersHost, 1);

        TranscriptDiagnosticsHost.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        TranscriptTelemetryHost.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
        TranscriptDiagnosticsHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        TranscriptTelemetryHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        TranscriptFiltersHost.HorizontalAlignment = showTopStrip
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        TranscriptFiltersHost.CornerRadius = !showTopStrip
            ? new CornerRadius(8)
            : new CornerRadius(0, 8, 8, 0);
        TranscriptFiltersHost.BorderThickness = !showTopStrip
            ? new Thickness(1)
            : new Thickness(0, 1, 1, 1);
        if (showDiagnostics)
        {
            DiagnosticsWorkflow.Update(_lastRenderedMessages);
        }
        else
        {
            DiagnosticsWorkflow.CloseDetail();
        }

        TelemetryWorkflow.UpdateTimerState();
    }

    private bool IsDiagnosticsDisplayed()
    {
        return _transcriptDashboardLayout.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
            && TranscriptDiagnosticsHost.Visibility == Visibility.Visible
            && TranscriptDiagnosticsHost.IsVisible;
    }

    private void PopulateAgents(ArenaViewSnapshot snapshot)
    {
        var agents = snapshot.Agents;
        var currentAgentId = CurrentTurnAgent(snapshot)?.Id;
        AgentItems.Children.Clear();
        _agentTurnButtons.Clear();
        _narratorActionButtons.Clear();

        if (agents.Count == 0)
        {
            AgentItems.Children.Add(CreateAgentStatusCard("No agents", "No active snapshot", ResourceBrush("ControlBorderBrush")));
            return;
        }

        foreach (var agent in agents)
        {
            AgentItems.Children.Add(CreateAgentCard(agent, currentAgentId));
        }

        AgentItems.Children.Add(CreateNarratorCard(snapshot));
    }

    private void PopulateCustomMatch(ArenaViewSnapshot snapshot)
    {
        ScenarioPreviewItems.Children.Clear();
        CastPreviewItems.Children.Clear();
        ScenarioSeedInspector.Children.Clear();
        _lockControls.Clear();
        _voiceControls.Clear();
        _pressureControls.Clear();

        PopulateScenarioSeedInspector(snapshot);
        ScenarioWorkflow.PopulateGenerationHistory(snapshot);

        ScenarioPreviewItems.Children.Add(CreateLockCard(
            "topic",
            "Topic",
            string.IsNullOrWhiteSpace(snapshot.ScenarioTopic) ? "No topic is set for this match yet." : snapshot.ScenarioTopic,
            ResourceBrush("CardBrush"),
            snapshot.TopicLocked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            snapshot.TopicLocked));
        ScenarioPreviewItems.Children.Add(CreateLockCard(
            "global",
            "Global",
            string.IsNullOrWhiteSpace(snapshot.ScenarioGlobal) ? "No global instruction is set for this match yet." : snapshot.ScenarioGlobal,
            ResourceBrush("CardBrush"),
            snapshot.GlobalLocked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            snapshot.GlobalLocked));

        if (snapshot.Agents.Count == 0)
        {
            CastPreviewItems.Children.Add(CreateEmptyStateCard("Cast", "No active cast is available for this session yet.", ResourceBrush("MutedTextBrush")));
        }
        else
        {
            foreach (var agent in snapshot.Agents)
            {
                CastPreviewItems.Children.Add(CreateLockCard(
                    agent.Id,
                    FormatCastPreviewTitle(agent.Id, agent.Name),
                    string.IsNullOrWhiteSpace(agent.Persona) ? "(no persona)" : agent.Persona,
                    BlendBrush(ResourceBrush("CardBrush"), AccentForSpeaker(agent.Id), 0.16),
                    AccentForSpeaker(agent.Id),
                    agent.Locked,
                    agent.VoiceStyle,
                    agent.PressureProfile));
            }
        }

        CastPreviewItems.Children.Add(CreateLockCard(
            "narrator",
            "Narrator",
            string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "(no narrator persona)" : snapshot.NarratorPersona,
            BlendBrush(ResourceBrush("CardBrush"), ResourceBrush("NarratorAccentBrush"), 0.16),
            ResourceBrush("NarratorAccentBrush"),
            snapshot.NarratorLocked,
            snapshot.NarratorVoiceStyle));

        MatchSetup.PopulateRivalryMatrix(snapshot);
    }

    private void PopulateScenarioSeedInspector(ArenaViewSnapshot snapshot)
    {
        var scenarioSeed = DisplayStatusValue(snapshot.ScenarioGeneratorSeed);
        var scenarioStyle = DisplayStatusValue(snapshot.ScenarioGeneratorStyle);
        var scenarioIntensity = DisplayStatusValue(snapshot.ScenarioGeneratorIntensity);
        var scenarioRolePack = DisplayStatusValue(snapshot.ScenarioGeneratorRolePack);
        var scenarioAbsurdity = DisplayStatusValue(snapshot.ScenarioGeneratorAbsurdity);
        var personaSeed = DisplayStatusValue(snapshot.PersonaGeneratorSeed);
        var personaStyle = DisplayStatusValue(snapshot.PersonaGeneratorStyle);
        var source = ScenarioSeedSource(snapshot.ScenarioGeneratorSeed, snapshot.PersonaGeneratorStyle);

        ScenarioSeedInspector.Children.Add(CreateSetupChip("Source", source, ResourceBrush("PrimaryBorderBrush")));
        ScenarioSeedInspector.Children.Add(CreateSetupChip("Scenario", ShortSeedValue(scenarioSeed), ResourceBrush("TextBrush")));
        ScenarioSeedInspector.Children.Add(CreateSetupChip("Style", scenarioStyle, ResourceBrush("MutedTextBrush")));
        if (scenarioIntensity != "-")
        {
            ScenarioSeedInspector.Children.Add(CreateSetupChip("Pressure", scenarioIntensity, ResourceBrush("BetaAccentBrush")));
        }
        if (scenarioRolePack != "-" && !scenarioRolePack.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            ScenarioSeedInspector.Children.Add(CreateSetupChip("Pack", scenarioRolePack.Replace('_', ' '), ResourceBrush("PrimaryBorderBrush")));
        }
        if (scenarioAbsurdity != "-" && !scenarioAbsurdity.Equals("grounded", StringComparison.OrdinalIgnoreCase))
        {
            ScenarioSeedInspector.Children.Add(CreateSetupChip("Absurdity", scenarioAbsurdity, ResourceBrush("NarratorAccentBrush")));
        }
        ScenarioSeedInspector.Children.Add(CreateSetupChip("Personas", ShortSeedValue(personaSeed), ResourceBrush("NarratorAccentBrush")));
        ScenarioSeedInspector.Children.Add(CreateSetupChip("Persona style", personaStyle, ResourceBrush("MutedTextBrush")));
    }

    private static string ScenarioSeedSource(string scenarioSeed, string personaStyle)
    {
        if (scenarioSeed.StartsWith("YOLO-", StringComparison.OrdinalIgnoreCase)
            || personaStyle.Equals("yolo", StringComparison.OrdinalIgnoreCase))
        {
            return "YOLO";
        }

        if (scenarioSeed.Equals("ai-choice", StringComparison.OrdinalIgnoreCase))
        {
            return "AI Choice";
        }

        return string.IsNullOrWhiteSpace(scenarioSeed) || scenarioSeed == "-"
            ? "Manual"
            : "Random";
    }

    private static string ShortSeedValue(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed) || seed == "-")
        {
            return "-";
        }

        return seed.Length <= 18 ? seed : $"{seed[..15]}...";
    }

    private void PopulateNews(IReadOnlyList<TranscriptMessage> messages)
    {
        NewsItems.Children.Clear();
        var newsMessages = messages
            .Where(message => message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase)
                || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(message.InternetTool)
                || message.InternetSources.Count > 0)
            .OrderByDescending(message => message.Turn)
            .ToArray();

        if (newsMessages.Length == 0)
        {
            NewsSummaryText.Text = "No internet activity in this session.";
            NewsItems.Children.Add(CreateEmptyStateCard(
                "News inspector",
                "No fetched or curated news items in this session yet.",
                ResourceBrush("NarratorAccentBrush")));
            return;
        }

        var sourceCount = newsMessages.Sum(message => message.InternetSources.Count);
        var pendingCount = newsMessages.Count(message => message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
        NewsSummaryText.Text = $"{newsMessages.Length} internet item(s), {sourceCount} source(s)"
            + (pendingCount > 0 ? $", {pendingCount} waiting for approval" : "");

        foreach (var message in newsMessages)
        {
            NewsItems.Children.Add(CreateNewsInspectorCard(message));
        }
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return CreateCard(title, body, background, accent, null);
    }

    private Border CreateEmptyStateCard(string title, string body, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Quiet state",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        return CreateCard(title, body, BlendBrush(ResourceBrush("CardBrush"), accent, 0.08), accent, panel);
    }

    private Border CreateArenaReadyCard(ArenaViewSnapshot? snapshot)
    {
        var accent = ResourceBrush("AlphaAccentBrush");
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        if (snapshot is not null)
        {
            var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
            var current = CurrentTurnAgent(snapshot);
            var setup = new WrapPanel();
            setup.Children.Add(CreateSetupChip("Match", DisplayStatusValue(snapshot.MatchType), ResourceBrush("TextBrush")));
            setup.Children.Add(CreateSetupChip("Agents", $"{activeAgents.Length} + narrator", ResourceBrush("PrimaryBorderBrush")));
            setup.Children.Add(CreateSetupChip("Turn", current is null ? "-" : DisplayStatusValue(current.Id), current is null ? ResourceBrush("MutedTextBrush") : AccentForSpeaker(current.Id)));
            setup.Children.Add(CreateSetupChip("Model", ShortModelName(CurrentTurnModel(snapshot, current)), ResourceBrush("MutedTextBrush")));
            panel.Children.Add(setup);

            if (ShouldShowProviderSetup(snapshot, current))
            {
                panel.Children.Add(CreateProviderQuickSetupCard(snapshot, current));
            }
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Quiet state",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        });

        return CreateCard(
            "Arena is ready",
            "Start with 1 TURN, AUTO CHAT, an agent turn, or write directly in Operator Turn.",
            BlendBrush(ResourceBrush("CardBrush"), accent, 0.08),
            accent,
            panel);
    }

    private bool ShouldShowProviderSetup(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var currentModel = CurrentTurnModel(snapshot, current);
        return !snapshot.ProviderOnline
            || string.IsNullOrWhiteSpace(currentModel)
            || currentModel == "-"
            || string.IsNullOrWhiteSpace(snapshot.ProviderModel)
            || snapshot.ProviderModel == "-";
    }

    private Border CreateProviderQuickSetupCard(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var accent = snapshot.ProviderOnline ? ResourceBrush("BetaAccentBrush") : ResourceBrush("DangerBorderBrush");
        var baseUrlBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(snapshot.ProviderBaseUrl) || snapshot.ProviderBaseUrl == "-"
                ? "http://127.0.0.1:1234/v1"
                : snapshot.ProviderBaseUrl,
            Background = ResourceBrush("InputBrush"),
            Foreground = ResourceBrush("TextBrush"),
            BorderBrush = ResourceBrush("ControlBorderBrush"),
            Padding = new Thickness(8),
            MinWidth = 230,
            ToolTip = "OpenAI-compatible provider base URL."
        };

        var modelBox = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            Text = CurrentTurnModel(snapshot, current) == "-" ? "" : CurrentTurnModel(snapshot, current),
            Background = ResourceBrush("InputBrush"),
            Foreground = ResourceBrush("TextBrush"),
            BorderBrush = ResourceBrush("ControlBorderBrush"),
            Padding = new Thickness(8),
            MinWidth = 230,
            ToolTip = "Pick an advertised model or type one manually."
        };
        foreach (var model in ProviderSettings.AdvertisedModels)
        {
            modelBox.Items.Add(model);
        }

        var statusText = new TextBlock
        {
            Text = ProviderSetupStatus(snapshot),
            Foreground = ResourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        };

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(ActionButton("Save + test", async (_, _) =>
        {
            await ProviderSettings.SaveAndTestProviderQuickSetupAsync(baseUrlBox.Text, modelBox.Text, statusText);
        }, true, TranscriptActionKind.Primary));
        actions.Children.Add(ActionButton("Open settings", (_, _) => OpenModelProviderSettings(baseUrlBox.Text, modelBox.Text), true));

        var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var baseUrlStack = new StackPanel();
        baseUrlStack.Children.Add(new TextBlock
        {
            Text = "Provider base URL",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        baseUrlStack.Children.Add(baseUrlBox);
        fields.Children.Add(baseUrlStack);

        var modelStack = new StackPanel();
        modelStack.Children.Add(new TextBlock
        {
            Text = "Default model",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        modelStack.Children.Add(modelBox);
        Grid.SetColumn(modelStack, 2);
        fields.Children.Add(modelStack);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Provider setup",
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Connect LM Studio or another OpenAI-compatible /v1 provider before running turns.",
            Foreground = ResourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        panel.Children.Add(fields);
        panel.Children.Add(actions);
        panel.Children.Add(statusText);

        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 2),
            Child = panel
        };
    }

    private string ProviderSetupStatus(ArenaViewSnapshot snapshot)
    {
        if (snapshot.ProviderOnline)
        {
            return "Provider is online. Choose a model, then run 1 TURN.";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ProviderLastError) && snapshot.ProviderLastError != "-")
        {
            return snapshot.ProviderLastError;
        }

        return "Provider is offline. Start LM Studio server, then save and test.";
    }

    private Border CreateSetupChip(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static string CurrentTurnModel(ArenaViewSnapshot snapshot, AgentState? current)
    {
        if (!string.IsNullOrWhiteSpace(current?.Model))
        {
            return current.Model;
        }

        return !string.IsNullOrWhiteSpace(snapshot.ProviderModel)
            ? snapshot.ProviderModel
            : "-";
    }

    private Border CreateLockCard(string lockKey, string title, string body, Brush background, Brush accent, bool locked, string? voiceStyle = null, string? pressureProfile = null)
    {
        var lockAccent = ResourceBrush("BetaAccentBrush");
        var isCastCard = IsAgentSpeaker(lockKey) || NormalizeMatchLockKey(lockKey) == "narrator";
        var isAgentCard = IsAgentSpeaker(lockKey);
        var cardAccent = locked ? lockAccent : accent;
        var lockBox = new CheckBox
        {
            IsChecked = locked,
            IsEnabled = !_arenaBusy,
            Tag = lockKey,
            Style = CreateLockToggleStyle(),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = locked ? "Locked. Click to unlock." : "Unlocked. Click to lock."
        };
        lockBox.Checked += MatchLockChanged;
        lockBox.Unchecked += MatchLockChanged;
        _lockControls.Add(lockBox);

        var editButton = new Button
        {
            Content = "EDIT",
            Tag = lockKey,
            MinHeight = 28,
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ResourceBrush("InputBrush"),
            BorderBrush = cardAccent,
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Edit this text and lock it"
        };
        editButton.Click += EditLockCardButton_Click;

        var header = new DockPanel { LastChildFill = true };
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 0, 0, 0)
        };
        if (isAgentCard)
        {
            actions.Children.Add(CreateAgentPressurePicker(lockKey, pressureProfile ?? "", cardAccent));
        }
        if (isCastCard)
        {
            actions.Children.Add(CreateVoiceStylePicker(lockKey, voiceStyle ?? "", cardAccent));
        }
        if (isAgentCard && HasRoleDetails(body))
        {
            var detailsButton = new Button
            {
                Content = "?",
                Tag = new RoleDetailPayload(title, body),
                MinHeight = 28,
                MinWidth = 30,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 8, 0),
                Background = ResourceBrush("InputBrush"),
                BorderBrush = cardAccent,
                Foreground = cardAccent,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Inspect generated role constraints"
            };
            detailsButton.Click += RoleDetailsButton_Click;
            actions.Children.Add(detailsButton);
        }
        actions.Children.Add(editButton);
        actions.Children.Add(lockBox);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titlePanel.Children.Add(CreateLockMetaChip(DisplayLockKey(lockKey), accent));
        var visibleVoiceStyle = VoiceStyleChipText(voiceStyle);
        if (isCastCard && !string.IsNullOrWhiteSpace(visibleVoiceStyle))
        {
            titlePanel.Children.Add(CreateLockMetaChip(visibleVoiceStyle, accent));
        }
        var visiblePressure = AgentPressureChipText(pressureProfile);
        if (isAgentCard && !string.IsNullOrWhiteSpace(visiblePressure))
        {
            titlePanel.Children.Add(CreateLockMetaChip(visiblePressure, accent));
        }
        if (locked)
        {
            titlePanel.Children.Add(CreateLockMetaChip("Locked", lockAccent));
        }
        header.Children.Add(titlePanel);

        var displayBody = isCastCard ? CompactCastPreviewBody(body) : body;
        var text = new TextBlock
        {
            Text = displayBody,
            Foreground = ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            MaxHeight = isCastCard ? 58 : double.PositiveInfinity,
            Margin = new Thickness(0, 7, 0, 0),
            ToolTip = body
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(text);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Background = cardAccent,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 1, 10, 1)
        });
        Grid.SetColumn(stack, 1);
        layout.Children.Add(stack);

        return new Border
        {
            Background = BlendBrush(background, accent, locked ? 0.16 : isCastCard ? 0.1 : 0.05),
            BorderBrush = locked ? lockAccent : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = locked ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, isCastCard ? 0 : 10, 10),
            Child = layout
        };
    }

    private static bool HasRoleDetails(string persona)
    {
        return RoleDetailLabels.Any(label => persona.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    private static string CompactCastPreviewBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var firstDetailIndex = RoleDetailLabels
            .Select(label => body.IndexOf(label, StringComparison.OrdinalIgnoreCase))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();
        var preview = firstDetailIndex > 0 ? body[..firstDetailIndex] : body;
        preview = preview.Trim().Replace("\r", " ").Replace("\n", " ");
        while (preview.Contains("  ", StringComparison.Ordinal))
        {
            preview = preview.Replace("  ", " ", StringComparison.Ordinal);
        }

        return preview.Length <= 300
            ? preview
            : $"{preview[..297].TrimEnd()}...";
    }

    private void RoleDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RoleDetailPayload payload })
        {
            return;
        }

        var window = new Window
        {
            Owner = this,
            Title = $"{payload.Title} details",
            Width = 620,
            Height = 420,
            MinWidth = 460,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = ResourceBrush("PanelBrush"),
            Foreground = ResourceBrush("TextBrush")
        };
        DialogChrome.ImportOwnerResources(this, window);
        DialogChrome.ApplyImplicitControlStyles(window);

        var root = new Grid
        {
            Background = ResourceBrush("PanelBrush"),
            Margin = new Thickness(1)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = payload.Title,
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(14, 12, 14, 8)
        };
        root.Children.Add(title);

        var details = new TextBox
        {
            Text = RoleDetailsText(payload.Persona),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = ResourceBrush("InputBrush"),
            Foreground = ResourceBrush("TextBrush"),
            BorderBrush = ResourceBrush("ControlBorderBrush"),
            Padding = new Thickness(12),
            Margin = new Thickness(14, 0, 14, 10)
        };
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var close = new Button
        {
            Content = "CLOSE",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96,
            Margin = new Thickness(14, 0, 14, 14)
        };
        close.Click += (_, _) => window.Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);

        window.Content = root;
        window.Show();
        window.Activate();
    }

    private static string RoleDetailsText(string persona)
    {
        var lines = RoleDetailLabels
            .Select(label => ExtractRoleDetailSegment(persona, label, RoleDetailLabels))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return lines.Length == 0
            ? persona
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", lines);
    }

    private static string ExtractRoleDetailSegment(string persona, string label, IReadOnlyList<string> labels)
    {
        var start = persona.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        var valueStart = start + label.Length;
        var next = labels
            .Select(candidate => persona.IndexOf(candidate, valueStart, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(persona.Length)
            .Min();
        var value = persona[valueStart..next].Trim(' ', '\r', '\n', '\t', '.');
        return string.IsNullOrWhiteSpace(value) ? "" : $"{label} {value}";
    }

    private ComboBox CreateVoiceStylePicker(string lockKey, string voiceStyle, Brush accent)
    {
        var picker = new ComboBox
        {
            Tag = lockKey,
            Width = 132,
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            ToolTip = "Communication style for this model"
        };

        foreach (var option in VoiceStyleOptions())
        {
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Tag });
        }

        ShellUiHelpers.SelectComboTag(picker, NormalizeVoiceStyleTag(voiceStyle));
        picker.SelectionChanged += VoiceStylePicker_SelectionChanged;
        _voiceControls.Add(picker);
        return picker;
    }

    private ComboBox CreateAgentPressurePicker(string lockKey, string pressureProfile, Brush accent)
    {
        var picker = new ComboBox
        {
            Tag = lockKey,
            Width = 124,
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            ToolTip = "Debate pressure for this agent"
        };

        foreach (var option in AgentPressureOptions())
        {
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Tag });
        }

        ShellUiHelpers.SelectComboTag(picker, NormalizeAgentPressureTag(pressureProfile));
        picker.SelectionChanged += AgentPressurePicker_SelectionChanged;
        _pressureControls.Add(picker);
        return picker;
    }

    private async void VoiceStylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingSnapshot || _activeSession is null || sender is not ComboBox picker || picker.Tag is not string key)
        {
            return;
        }

        var voiceStyle = ShellUiHelpers.SelectedComboTag(picker, "default");
        await RunArenaBusyAsync($"Updating {DisplayLockKey(key)} voice...", async () =>
        {
            var latest = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (latest is null)
            {
                LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            if (!ApplyVoiceStyle(latest, key, voiceStyle))
            {
                LoadStatus.Text = $"Could not update {DisplayLockKey(key)} voice.";
                ArenaRunStatus.Text = LoadStatus.Text;
                return;
            }

            await SaveSnapshotWithFeedbackAsync(latest, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_match_voice_style_changed", new
            {
                key = NormalizeMatchLockKey(key),
                voice_style = voiceStyle
            });
            RefreshActiveSession($"Updated {DisplayLockKey(key)} voice: {VoiceStyleLabel(voiceStyle)}.");
        });
    }

    private async void AgentPressurePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingSnapshot || _activeSession is null || sender is not ComboBox picker || picker.Tag is not string key)
        {
            return;
        }

        var pressure = ShellUiHelpers.SelectedComboTag(picker, "default");
        await RunArenaBusyAsync($"Updating {DisplayLockKey(key)} pressure...", async () =>
        {
            var latest = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (latest is null)
            {
                LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            if (!ApplyAgentPressure(latest, key, pressure))
            {
                LoadStatus.Text = $"Could not update {DisplayLockKey(key)} pressure.";
                ArenaRunStatus.Text = LoadStatus.Text;
                return;
            }

            await SaveSnapshotWithFeedbackAsync(latest, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_pressure_changed", new
            {
                key = NormalizeMatchLockKey(key),
                pressure_profile = pressure
            });
            RefreshActiveSession($"Updated {DisplayLockKey(key)} pressure: {AgentPressureLabel(pressure)}.");
        });
    }

    private static IReadOnlyList<(string Tag, string Label)> VoiceStyleOptions()
    {
        return
        [
            ("default", "Voice: Default"),
            ("scientific", "Voice: Scientific"),
            ("legal_policy", "Voice: Legal / Policy"),
            ("plain_language", "Voice: Plain"),
            ("idioms", "Voice: Idioms"),
            ("cute", "Voice: Cute"),
            ("poetic", "Voice: Poetic"),
            ("socratic", "Voice: Socratic"),
            ("bullet_only", "Voice: Bullet-only"),
            ("skeptical", "Voice: Skeptical"),
            ("executive_brief", "Voice: Executive"),
            ("evidence_ledger", "Voice: Evidence"),
            ("no_analogies", "Voice: No analogies"),
            ("hedge_uncertainty", "Voice: Hedge"),
            ("bark_only", "Voice: Bark-only"),
            ("science_gibberish", "Voice: Science gibberish")
        ];
    }

    private static IReadOnlyList<(string Tag, string Label)> AgentPressureOptions()
    {
        return
        [
            ("default", "Pressure: Default"),
            ("calm", "Pressure: Calm"),
            ("assertive", "Pressure: Assertive"),
            ("contrarian", "Pressure: Contrarian"),
            ("evidence", "Pressure: Evidence"),
            ("risk", "Pressure: Risk"),
            ("concise", "Pressure: Concise"),
            ("expansive", "Pressure: Expansive"),
            ("chaos", "Pressure: Chaos")
        ];
    }

    private static string NormalizeVoiceStyleTag(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return VoiceStyleOptions().Any(option => option.Tag.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            ? cleaned
            : "default";
    }

    private static string VoiceStyleLabel(string? value)
    {
        var tag = NormalizeVoiceStyleTag(value);
        var label = VoiceStyleOptions().First(option => option.Tag == tag).Label;
        return label.Replace("Voice: ", "", StringComparison.OrdinalIgnoreCase);
    }

    private static string VoiceStyleChipText(string? value)
    {
        return NormalizeVoiceStyleTag(value).Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Voice: {VoiceStyleLabel(value)}";
    }

    private static string NormalizeAgentPressureTag(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return AgentPressureOptions().Any(option => option.Tag.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            ? cleaned
            : "default";
    }

    private static string AgentPressureLabel(string? value)
    {
        var tag = NormalizeAgentPressureTag(value);
        var label = AgentPressureOptions().First(option => option.Tag == tag).Label;
        return label.Replace("Pressure: ", "", StringComparison.OrdinalIgnoreCase);
    }

    private static string AgentPressureChipText(string? value)
    {
        return NormalizeAgentPressureTag(value).Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Pressure: {AgentPressureLabel(value)}";
    }

    private bool ShouldShowStyleFit()
    {
        return _wpfSettings.AllowDebugControls && _wpfSettings.ShowStyleFit;
    }

    private bool ShouldShowDecisionCard()
    {
        return _wpfSettings.AllowDebugControls && _wpfSettings.ShowDecisionCard;
    }

    private bool ShouldEnforceVoiceDrift()
    {
        return _wpfSettings.AllowDebugControls && _wpfSettings.EnforceVoiceDrift;
    }

    private static string VoiceAdherenceState(int score, int samples)
    {
        if (samples <= 0)
        {
            return "none";
        }

        return score >= 74 ? "strong" : score >= 46 ? "drifting" : "broken";
    }

    private static string VoiceAdherenceDisplayState(string state)
    {
        return state.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? "strong cues"
            : state.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? "partial cues"
                : state.Equals("broken", StringComparison.OrdinalIgnoreCase)
                    ? "low cues"
                    : "no cues";
    }

    private static string VoiceAdherenceChipText(CoreVoiceAdherenceDiagnostic diagnostic)
    {
        var cue = diagnostic.State.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? "strong"
            : diagnostic.State.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? "partial"
                : diagnostic.State.Equals("broken", StringComparison.OrdinalIgnoreCase)
                    ? "low"
                    : "none";
        return $"Cues: {cue} {diagnostic.Score}";
    }

    private static bool IsStrictVoiceStyle(string? voiceStyle)
    {
        var normalized = NormalizeVoiceStyleTag(voiceStyle);
        return normalized.Equals("bullet_only", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("evidence_ledger", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no_analogies", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("bark_only", StringComparison.OrdinalIgnoreCase);
    }

    private Brush VoiceAdherenceAccent(string state)
    {
        return state.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? ResourceBrush("GammaAccentBrush")
            : state.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? ResourceBrush("BetaAccentBrush")
                : ResourceBrush("MutedTextBrush");
    }

    private Brush VoiceAdherenceAccent(CoreVoiceAdherenceDiagnostic diagnostic)
    {
        if (diagnostic.State.Equals("broken", StringComparison.OrdinalIgnoreCase) && IsStrictVoiceStyle(diagnostic.VoiceStyle))
        {
            return ResourceBrush("DangerBorderBrush");
        }

        return VoiceAdherenceAccent(diagnostic.State);
    }

    private static string VoiceAdherenceTooltip(CoreVoiceAdherenceDiagnostic diagnostic)
    {
        var evidence = diagnostic.Evidence.Count == 0
            ? "Evidence: -"
            : $"Evidence: {string.Join("; ", diagnostic.Evidence)}";
        var missing = diagnostic.Missing.Count == 0
            ? "Missing: -"
            : $"Missing: {string.Join("; ", diagnostic.Missing)}";
        return $"{diagnostic.Summary}{Environment.NewLine}{evidence}{Environment.NewLine}{missing}";
    }

    private Border CreateLockMetaChip(string text, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static string FormatCastPreviewTitle(string id, string name)
    {
        return FormatParticipantTitle(id, name, uppercaseRole: false);
    }

    private static string FormatParticipantTitle(string id, string name, bool uppercaseRole)
    {
        var role = DisplayLockKey(id);
        var cleaned = string.IsNullOrWhiteSpace(name) ? role : name.Trim();
        var roleLabel = uppercaseRole ? role.ToUpperInvariant() : role;
        var duplicatePrefix = $"{role}:";
        if (cleaned.StartsWith(duplicatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[duplicatePrefix.Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) || cleaned.Equals(role, StringComparison.OrdinalIgnoreCase)
            ? roleLabel
            : $"{roleLabel}: {cleaned}";
    }

    private Style CreateLockToggleStyle()
    {
        if (_lockToggleStyle is not null)
        {
            return _lockToggleStyle;
        }

        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush("MutedTextBrush")));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 44d));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(CheckBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "LockTrack";
        track.SetValue(FrameworkElement.WidthProperty, 42d);
        track.SetValue(FrameworkElement.HeightProperty, 22d);
        track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(Border.BackgroundProperty, ResourceBrush("InputBrush"));
        track.SetValue(Border.BorderBrushProperty, ResourceBrush("ControlBorderBrush"));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));

        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.Name = "LockThumb";
        thumb.SetValue(FrameworkElement.WidthProperty, 16d);
        thumb.SetValue(FrameworkElement.HeightProperty, 16d);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        thumb.SetValue(Border.BackgroundProperty, ResourceBrush("MutedTextBrush"));
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.Name = "LockGlyph";
        glyph.SetValue(TextBlock.TextProperty, "\uE785");
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 9d);
        glyph.SetValue(TextBlock.ForegroundProperty, ResourceBrush("DisabledBorderBrush"));
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.AppendChild(glyph);
        track.AppendChild(thumb);
        root.AppendChild(track);

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush("TextBrush")));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("BetaAccentBrush"), 0.16), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("BetaAccentBrush"), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("BetaAccentBrush"), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.TextProperty, "\uE72E", "LockGlyph"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, ResourceBrush("InputBrush"), "LockGlyph"));
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("HoverBorderBrush"), "LockTrack"));
        template.Triggers.Add(hoverTrigger);

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(disabledTrigger);

        template.VisualTree = root;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        _lockToggleStyle = style;
        return style;
    }

    private Border CreateTranscriptCard(TranscriptMessage message, bool retryable, bool searchMatch, bool isLatest)
    {
        var hasInternetDetails = !string.IsNullOrWhiteSpace(message.InternetTool)
            || !string.IsNullOrWhiteSpace(message.InternetQuery)
            || !string.IsNullOrWhiteSpace(message.InternetUrl)
            || message.InternetSources.Count > 0;
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase);
        var body = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text;
        var isSystemEvent = IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? ResourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet || hasInternetDetails) ? ResourceBrush("AssistBorderBrush") : AccentForSpeaker(message.SpeakerId);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var canMutate = message.Turn > 0;
        actions.Children.Add(ActionButton("Copy", (_, _) => CopyTranscriptMessage(message), canMutate, iconGlyph: "\uE8C8"));
        actions.Children.Add(ActionButton(message.Pinned ? "Unpin" : "Pin", async (_, _) => await TogglePinTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Primary, "\uE718"));
        actions.Children.Add(ActionButton("Retry", async (_, _) => await RetryTranscriptMessageAsync(message), canMutate && retryable && IsAgentSpeaker(message.SpeakerId) && !isInternet, iconGlyph: "\uE72C"));
        actions.Children.Add(ActionButton("Delete", async (_, _) => await DeleteTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Danger, "\uE74D"));
        if (_wpfSettings.TurnCompareMode)
        {
            var selectedForCompare = TranscriptInsight.IsTurnSelectedForCompare(message);
            actions.Children.Add(ActionButton(
                selectedForCompare ? "Drop compare" : "Compare",
                (_, _) => TranscriptInsight.ToggleTurnCompareMessage(message),
                canMutate && TranscriptInsightCoordinator.CanCompareMessage(message),
                selectedForCompare ? TranscriptActionKind.Primary : TranscriptActionKind.Neutral));
        }
        if (message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase) && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            actions.Children.Add(ActionButton("Approve Once", async (_, _) => await InternetWorkflow.ApproveInternetRequestAsync(message), canMutate, TranscriptActionKind.Primary));
            actions.Children.Add(ActionButton("Reject", async (_, _) => await InternetWorkflow.RejectInternetRequestAsync(message), canMutate, TranscriptActionKind.Danger));
            actions.Children.Add(ActionButton("Copy URL", (_, _) => CopyInternetUrl(message), canMutate && !string.IsNullOrWhiteSpace(message.InternetUrl)));
        }

        var extras = new StackPanel();
        if (hasInternetDetails)
        {
            extras.Children.Add(CreateTranscriptExpander(
                message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase) ? "Source / selection" : "Internet details",
                accent: ResourceBrush("AssistBorderBrush"),
                content: CreateInternetDetails(message)));
        }
        if (!string.IsNullOrWhiteSpace(message.Reasoning))
        {
            extras.Children.Add(CreateTranscriptExpander(
                "Model reasoning",
                AccentForSpeaker(message.SpeakerId),
                new TextBlock
                {
                    Text = message.Reasoning,
                    Foreground = ResourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                }));
        }
        extras.Children.Add(actions);
        return CreateTranscriptCardLayout(message, body, accent, isInternet, searchMatch, isLatest, isSystemEvent, extras);
    }

    private Border CreateTurnComparePanel(IReadOnlyList<TranscriptMessage> visibleMessages)
    {
        var selected = TranscriptInsight.SelectedTurnCompareMessages.ToArray();
        var accent = ResourceBrush("BetaAccentBrush");
        var panel = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Turn Compare",
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = selected.Length >= 2
                ? CompareSummary(selected[0], selected[1])
                : "Select two transcript cards to compare wording, model, tokens, context, and latency.",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton(
            "Auto latest",
            (_, _) => TranscriptInsight.ReselectLatest(visibleMessages),
            visibleMessages.Any(TranscriptInsightCoordinator.CanCompareMessage)));
        actions.Children.Add(ActionButton(
            "Clear",
            (_, _) => TranscriptInsight.ClearTurnCompareSelection(suppressAutoSeed: true, refresh: true),
            TranscriptInsight.HasTurnCompareSelection));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        panel.Children.Add(header);

        if (selected.Length >= 2)
        {
            var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            metrics.Children.Add(CreateCompareMetric("Token delta", CompareDelta(selected[0].CompletionTokens, selected[1].CompletionTokens), ResourceBrush("PrimaryBorderBrush")));
            metrics.Children.Add(CreateCompareMetric("Context delta", CompareDelta(selected[0].PromptTokens, selected[1].PromptTokens), ResourceBrush("GammaAccentBrush")));
            metrics.Children.Add(CreateCompareMetric("Latency delta", CompareDurationDelta(selected[0].LatencyMs, selected[1].LatencyMs), ResourceBrush("AlphaAccentBrush")));
            panel.Children.Add(metrics);
        }

        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(selected.Length > 0 ? CreateTurnCompareColumn(selected[0], "A") : CreateTurnComparePlaceholder("A"));
        grid.Children.Add(selected.Length > 1 ? CreateTurnCompareColumn(selected[1], "B") : CreateTurnComparePlaceholder("B"));
        panel.Children.Add(grid);

        return new Border
        {
            Background = BlendBrush(ResourceBrush("CardBrush"), accent, 0.09),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Border CreateDecisionCardPanel(ArenaViewSnapshot snapshot)
    {
        var accent = ResourceBrush("NarratorAccentBrush");
        var hasCard = !string.IsNullOrWhiteSpace(snapshot.DecisionCard);
        var card = new Border
        {
            Background = BlendBrush(ResourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, 0.58),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var root = new DockPanel { LastChildFill = true };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var expandButton = ActionButton(
            _decisionCardExpanded ? "Collapse" : "Expand",
            (_, _) =>
            {
                _decisionCardExpanded = !_decisionCardExpanded;
                PopulateTranscript(_lastRenderedMessages);
            },
            hasCard);
        var generateButton = ActionButton("Generate", async (_, _) => await GenerateDecisionCardAsync(), true, TranscriptActionKind.Primary);
        SetDecisionCardActionSize(expandButton);
        SetDecisionCardActionSize(generateButton);
        actions.Children.Add(expandButton);
        actions.Children.Add(generateButton);
        DockPanel.SetDock(actions, Dock.Right);
        root.Children.Add(actions);

        var content = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Decision Card",
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        if (snapshot.DecisionCardUpdatedAt > 0)
        {
            titleRow.Children.Add(new TextBlock
            {
                Text = $"  updated {DateTimeOffset.FromUnixTimeSeconds((long)snapshot.DecisionCardUpdatedAt).ToLocalTime():h:mm tt}",
                Foreground = ResourceBrush("MutedTextBrush"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        content.Children.Add(titleRow);
        var summary = hasCard
            ? snapshot.DecisionCard.Trim().Replace("\r", " ").Replace("\n", " ")
            : "No decision card yet. Generate one to capture agreed points, conflict, risk, and the next operator move.";
        content.Children.Add(new TextBlock
        {
            Text = _decisionCardExpanded && hasCard ? snapshot.DecisionCard.Trim() : summary,
            Foreground = hasCard ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            FontSize = 11.5,
            TextWrapping = _decisionCardExpanded && hasCard ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = _decisionCardExpanded && hasCard ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            LineHeight = _decisionCardExpanded && hasCard ? 17 : double.NaN,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = summary
        });
        root.Children.Add(content);
        card.Child = root;
        return card;
    }

    private void SetDecisionCardActionSize(Button button)
    {
        button.Width = double.NaN;
        button.MinWidth = 74;
        button.Height = _wpfSettings.CompactTranscriptMode ? 26 : 32;
        button.MinHeight = button.Height;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = _wpfSettings.CompactTranscriptMode
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(10, 5, 10, 5);
    }

    private async Task GenerateDecisionCardAsync()
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Generating decision card...", null, async () =>
        {
            var result = await _narratorService.GenerateDecisionCardAsync(_activeSession.Id);
            await RefreshActiveSessionAsync(result.Ok ? "Decision card updated." : $"Decision card failed: {result.Error}");
        }, allowDuringAutoChat: true);
    }

    private Border CreateTurnCompareColumn(TranscriptMessage message, string slot)
    {
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase);
        var isSystemEvent = IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? ResourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet ? ResourceBrush("AssistBorderBrush") : AccentForSpeaker(message.SpeakerId));

        var stack = new StackPanel();
        var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        title.Children.Add(CreateTranscriptStatPill(slot, isInternet));
        title.Children.Add(new TextBlock
        {
            Text = $"Turn {message.Turn}: {TranscriptSpeakerTitle(message, isInternet, isSystemEvent)}",
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(title);

        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (!string.IsNullOrWhiteSpace(message.Model))
        {
            meta.Children.Add(CreateTranscriptStatPill(message.Model, isInternet));
        }
        var compareVoiceChip = VoiceStyleChipText(message.VoiceStyle);
        if (!isInternet && !isSystemEvent && !string.IsNullOrWhiteSpace(compareVoiceChip))
        {
            meta.Children.Add(CreateTranscriptStatPill(compareVoiceChip, isInternet));
            if (ShouldShowStyleFit())
            {
                var compareAdherence = _voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text);
                meta.Children.Add(CreateTranscriptStatPill(
                    VoiceAdherenceChipText(compareAdherence),
                    isInternet,
                    accentOverride: VoiceAdherenceAccent(compareAdherence),
                    toolTip: VoiceAdherenceTooltip(compareAdherence)));
            }
        }
        meta.Children.Add(CreateTranscriptStatPill(FormatGeneratedTokens(message), isInternet));
        if (message.PromptTokens > 0)
        {
            meta.Children.Add(CreateTranscriptStatPill($"ctx: {FormatCompactNumber(message.PromptTokens)}", isInternet));
        }
        if (message.LatencyMs > 0)
        {
            meta.Children.Add(CreateTranscriptStatPill(FormatDuration(message.LatencyMs), isInternet));
        }
        meta.Children.Add(CreateTranscriptStatPill(DisplayTime(message.CreatedAt), isInternet));
        stack.Children.Add(meta);

        stack.Children.Add(new Border
        {
            Background = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, 0.12),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                MaxHeight = _wpfSettings.CompactTranscriptMode ? 170 : 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text,
                    Foreground = ResourceBrush("TextBrush"),
                    FontSize = _wpfSettings.CompactTranscriptMode ? 12 : 13,
                    LineHeight = _wpfSettings.CompactTranscriptMode ? 17 : 19,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.42),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = slot == "A" ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0),
            Child = stack
        };
    }

    private Border CreateTurnComparePlaceholder(string slot)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"Slot {slot}",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Use Compare on any transcript card to fill this side.",
            Foreground = ResourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = ResourceBrush("InputBrush"),
            BorderBrush = ResourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = slot == "A" ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0),
            Child = stack
        };
    }

    private Border CreateCompareMetric(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static string CompareSummary(TranscriptMessage left, TranscriptMessage right)
    {
        return $"Comparing turn {left.Turn} ({left.Speaker}) with turn {right.Turn} ({right.Speaker}).";
    }

    private static string CompareDelta(int left, int right)
    {
        var delta = left - right;
        return delta == 0 ? "0" : delta > 0 ? $"+{delta}" : delta.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CompareDurationDelta(int leftMs, int rightMs)
    {
        var delta = leftMs - rightMs;
        return delta == 0 ? "0s" : delta > 0 ? $"+{FormatDuration(delta)}" : $"-{FormatDuration(Math.Abs(delta))}";
    }

    private Border CreateMatchQualityTimelinePanel(IReadOnlyList<TranscriptMessage> messages)
    {
        var points = BuildMatchQualityTimeline(messages);
        var accent = QualityAccent(points.LastOrDefault()?.Quality ?? 0);
        var panel = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Match Quality Timeline",
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = points.Count == 0
                ? "Quality appears after the first transcript turn."
                : $"Tracking {points.Count} quality checkpoint(s) across the current match transcript.",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var current = TranscriptInsight.TimelineSelectedTurnFilter is int turnFilter
            ? points.LastOrDefault(point => point.Turn == turnFilter) ?? points.LastOrDefault()
            : points.LastOrDefault();
        var currentIndex = current is null ? -1 : points.ToList().FindIndex(point => ReferenceEquals(point, current) || point.Turn == current.Turn);
        var previous = currentIndex > 0 ? points[currentIndex - 1] : points.Count >= 2 ? points[^2] : null;
        var scoreStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        scoreStack.Children.Add(new TextBlock
        {
            Text = current is null ? "-" : current.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = accent,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        scoreStack.Children.Add(new TextBlock
        {
            Text = current is null ? "No score yet" : $"Current {QualityLabel(current.Quality)} {FormatQualityTrend(current, previous)}",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        if (TranscriptInsight.TimelineSelectedTurnFilter is not null)
        {
            scoreStack.Children.Add(CreateTimelineClearButton());
        }

        Grid.SetColumn(scoreStack, 1);
        header.Children.Add(scoreStack);
        panel.Children.Add(header);

        var sparkline = new MetricSparklineControl
        {
            Height = _wpfSettings.CompactTranscriptMode ? 54 : 70,
            MinHeight = _wpfSettings.CompactTranscriptMode ? 54 : 70,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Values = points.Select(point => (double)point.Quality).ToArray(),
            AccentBrush = accent,
            MaxValue = 100,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(sparkline);

        if (points.Count > 0)
        {
            var diagnostics = current!.Diagnostics;
            var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            metrics.Children.Add(CreateQualityMetric("Friction", diagnostics.Friction.ToString(System.Globalization.CultureInfo.InvariantCulture), DiagnosticsWorkflow.AccentForState(QualityFrictionLabel(diagnostics.Friction))));
            metrics.Children.Add(CreateQualityMetric("Evidence", diagnostics.EvidencePressure.ToString(System.Globalization.CultureInfo.InvariantCulture), DiagnosticsWorkflow.AccentForEvidence(QualityEvidenceLabel(diagnostics.EvidencePressure))));
            metrics.Children.Add(CreateQualityMetric("Consensus", $"{diagnostics.Consensus}%", DiagnosticsWorkflow.AccentForRisk(diagnostics.Consensus > 75 ? "High" : "Low")));
            metrics.Children.Add(CreateQualityMetric("Drift", $"{diagnostics.RoleDrift}%", DiagnosticsWorkflow.AccentForRisk(diagnostics.RoleDrift > 35 ? "High" : "Low")));
            metrics.Children.Add(CreateQualityMetric("Claims", diagnostics.UnsupportedClaims.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostics.UnsupportedClaims > 0 ? ResourceBrush("BetaAccentBrush") : ResourceBrush("PrimaryBorderBrush")));
            panel.Children.Add(metrics);
            panel.Children.Add(CreateQualityBars(points));
        }

        return new Border
        {
            Background = BlendBrush(ResourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private WrapPanel CreateQualityBars(IReadOnlyList<MatchQualityPoint> points)
    {
        var bars = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 0)
        };
        foreach (var point in points.TakeLast(_wpfSettings.CompactTranscriptMode ? 24 : 36))
        {
            var accent = QualityAccent(point.Quality);
            var selected = TranscriptInsight.TimelineSelectedTurnFilter == point.Turn;
            var height = Math.Max(7, Math.Round(point.Quality / 100d * 28));
            var bar = new Border
            {
                Width = selected ? 12 : 8,
                Height = 32,
                Background = BlendBrush(ResourceBrush("InputBrush"), accent, selected ? 0.2 : 0.08),
                BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, selected ? 0.85 : 0.25),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                ToolTip = $"Turn {point.Turn} {point.Speaker}{Environment.NewLine}Quality: {point.Quality} ({QualityLabel(point.Quality)}){Environment.NewLine}Tokens: {FormatCompactNumber(point.Tokens)}{Environment.NewLine}{(selected ? "Click to clear this turn filter." : "Click to filter transcript to this turn.")}"
            };
            bar.MouseLeftButtonUp += (_, e) =>
            {
                ApplyTimelineTurnFilter(point.Turn);
                e.Handled = true;
            };
            bar.Child = new Border
            {
                Height = height,
                Background = accent,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1)
            };
            bars.Children.Add(bar);
        }

        return bars;
    }

    private Button CreateTimelineClearButton()
    {
        var button = new Button
        {
            Content = "Clear turn",
            Background = ResourceBrush("InputBrush"),
            BorderBrush = ResourceBrush("ControlBorderBrush"),
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MinHeight = 26,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Return timeline and transcript to the selected turn preset"
        };
        button.Click += (_, _) => ClearTimelineTurnFilter();
        return button;
    }

    private void ApplyTimelineTurnFilter(int turn)
    {
        TranscriptInsight.ToggleTimelineTurnFilter(turn);
    }

    private void ClearTimelineTurnFilter()
    {
        TranscriptInsight.ClearTimelineTurnFilter();
    }

    private Border CreateQualityMetric(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private IReadOnlyList<MatchQualityPoint> BuildMatchQualityTimeline(IReadOnlyList<TranscriptMessage> messages)
    {
        var ordered = messages
            .Where(message => message.Kind is "message" or "internet" or "")
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var limit = _wpfSettings.CompactTranscriptMode ? 28 : 42;
        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)limit));
        var points = new List<MatchQualityPoint>();
        for (var end = 1; end <= ordered.Length; end += stride)
        {
            points.Add(QualityPointForWindow(ordered, end));
        }

        var final = QualityPointForWindow(ordered, ordered.Length);
        if (points.Count == 0 || points[^1].Turn != final.Turn || !points[^1].Speaker.Equals(final.Speaker, StringComparison.OrdinalIgnoreCase))
        {
            points.Add(final);
        }

        return points.Count > limit ? points.Skip(points.Count - limit).ToArray() : points;
    }

    private MatchQualityPoint QualityPointForWindow(IReadOnlyList<TranscriptMessage> orderedMessages, int endExclusive)
    {
        var end = Math.Clamp(endExclusive, 1, orderedMessages.Count);
        var message = orderedMessages[end - 1];
        var diagnostics = DiagnosticsWorkflow.PointForWindow(orderedMessages, end);
        var quality = QualityScore(diagnostics);
        return new MatchQualityPoint(
            message.Turn,
            message.Speaker,
            quality,
            diagnostics,
            Math.Max(message.TotalTokens, message.CompletionTokens));
    }

    private static int QualityScore(DiagnosticHistoryPoint point)
    {
        var consensusPenalty = Math.Abs(point.Consensus - 38) * 0.24;
        var rolePenalty = point.RoleDrift * 0.32;
        var claimPenalty = Math.Min(24, point.UnsupportedClaims * 6);
        var evidenceBoost = point.EvidencePressure * 0.26;
        var narrativePenalty = point.NarrativeHeat > 78 ? (point.NarrativeHeat - 78) * 0.28 : 0;
        var frictionPenalty = point.Friction switch
        {
            < 18 => (18 - point.Friction) * 0.45,
            > 70 => (point.Friction - 70) * 0.46,
            _ => 0
        };
        var score = 58 + evidenceBoost - consensusPenalty - rolePenalty - claimPenalty - narrativePenalty - frictionPenalty;
        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    private Brush QualityAccent(int quality)
    {
        return quality switch
        {
            >= 75 => ResourceBrush("PrimaryBorderBrush"),
            >= 52 => ResourceBrush("BetaAccentBrush"),
            _ => ResourceBrush("DangerBorderBrush")
        };
    }

    private static string QualityLabel(int quality)
    {
        return quality switch
        {
            >= 75 => "Strong",
            >= 52 => "Watch",
            _ => "Fragile"
        };
    }

    private static string FormatQualityTrend(MatchQualityPoint current, MatchQualityPoint? previous)
    {
        if (previous is null)
        {
            return "new";
        }

        var delta = current.Quality - previous.Quality;
        return delta == 0 ? "0" : delta > 0 ? $"+{delta}" : $"-{Math.Abs(delta)}";
    }

    private static string QualityFrictionLabel(int friction)
    {
        return friction switch
        {
            < 20 => "Too Cold",
            <= 62 => "Productive Conflict",
            <= 72 => "Healthy",
            _ => "Theatre Risk"
        };
    }

    private static string QualityEvidenceLabel(int evidence)
    {
        return evidence switch
        {
            >= 70 => "Strong",
            >= 36 => "Medium",
            _ => "Weak"
        };
    }

    private Border? CreateAutoModeratorPanel(IReadOnlyList<TranscriptMessage> messages)
    {
        var alerts = BuildAutoModeratorAlerts(messages);
        if (alerts.Count == 0)
        {
            return null;
        }

        var danger = alerts.Any(alert => alert.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase));
        var accent = danger ? ResourceBrush("DangerBorderBrush") : ResourceBrush("BetaAccentBrush");
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var alert in alerts.Take(5))
        {
            var alertAccent = alert.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase)
                ? ResourceBrush("DangerBorderBrush")
                : ResourceBrush("BetaAccentBrush");
            panel.Children.Add(new Border
            {
                Background = BlendBrush(ResourceBrush("InputBrush"), alertAccent, 0.1),
                BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), alertAccent, 0.35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = alert.Label,
                            Foreground = alertAccent,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = alert.Body,
                            Foreground = ResourceBrush("TextBrush"),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 0)
                        }
                    }
                }
            });
        }

        return CreateCard(
            "Auto Moderator",
            "Suggested watch items from the current transcript window.",
            BlendBrush(ResourceBrush("CardBrush"), accent, 0.08),
            accent,
            panel);
    }

    private IReadOnlyList<AutoModeratorAlert> BuildAutoModeratorAlerts(IReadOnlyList<TranscriptMessage> messages)
    {
        var conversation = messages
            .Where(message => message.Kind is "message" or "" or "internet")
            .Where(message => !message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (conversation.Length < 2)
        {
            return [];
        }

        var diagnostics = _discourseDiagnostics.Analyze(messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn), _lastAgentPersonas);
        var alerts = new List<AutoModeratorAlert>();
        if (diagnostics.StateSeverity.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new AutoModeratorAlert(diagnostics.StateLabel, "The discourse state is entering a risky pattern. Use an operator turn to demand evidence, a concrete next step, or a dissenting frame.", "danger"));
        }

        if (diagnostics.UnsupportedClaimCount > 0 && diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new AutoModeratorAlert("Evidence-starved claims", $"{diagnostics.UnsupportedClaimCount} unsupported claim marker(s) with weak evidence pressure. Ask the next agent to separate evidence, inference, and assumption.", "danger"));
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            alerts.Add(new AutoModeratorAlert("Consensus lock-in", $"Consensus is {diagnostics.ConsensusPercent}%. Inject a challenge or boundary-test turn before the agents converge too early.", "watch"));
        }

        if (diagnostics.RoleDriftPercent >= 38)
        {
            alerts.Add(new AutoModeratorAlert("Role drift", $"Role drift is {diagnostics.RoleDriftPercent}%. Remind agents to preserve their assigned persona and pressure profile.", "watch"));
        }

        if (diagnostics.NarrativeHeatScore >= 82)
        {
            alerts.Add(new AutoModeratorAlert("Narrative heat", $"Narrative heat is {diagnostics.NarrativeHeatLabel}. Ask for a testable claim or operational checkpoint to cool the rhetoric.", "watch"));
        }

        var voiceAlert = VoiceDriftAutoModeratorAlert(conversation);
        if (voiceAlert is not null)
        {
            alerts.Add(voiceAlert);
        }

        return alerts
            .GroupBy(alert => alert.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private AutoModeratorAlert? VoiceDriftAutoModeratorAlert(IReadOnlyList<TranscriptMessage> messages)
    {
        var drift = messages
            .Where(message => IsAgentSpeaker(message.SpeakerId) || message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
            .Where(message => !string.IsNullOrWhiteSpace(message.VoiceStyle))
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Take(5)
            .Select(message => (Message: message, Diagnostic: _voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text)))
            .Where(item => item.Diagnostic.State is "broken" or "drifting")
            .Take(2)
            .ToArray();
        if (drift.Length == 0)
        {
            return null;
        }

        var summary = string.Join("; ", drift.Select(item => $"{DisplayStatusValue(item.Message.SpeakerId)} {item.Diagnostic.Label}: {item.Diagnostic.State}"));
        return new AutoModeratorAlert("Voice drift", $"{summary}. Turn on debug voice enforcement or use an operator nudge if voice style matters for this run.", "watch");
    }

    private Border CreateAgentMemoryPanel(ArenaViewSnapshot snapshot)
    {
        var accent = ResourceBrush("GammaAccentBrush");
        var panel = new StackPanel();
        var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
        var noteCount = snapshot.Agents.Sum(agent => agent.PrivateNotes.Count);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Agent Memory Notes",
            Foreground = ResourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{noteCount} note(s) across {activeAgents.Length} active agent(s). Notes are written into the session snapshot and used by model context windows.",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Refresh", (_, _) => PopulateTranscript(_lastRenderedMessages), true));
        actions.Children.Add(ActionButton("Clear all", async (_, _) => await ClearAllAgentMemoryNotesAsync(), noteCount > 0, TranscriptActionKind.Danger));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        panel.Children.Add(header);

        var grid = new UniformGrid
        {
            Columns = _wpfSettings.CompactTranscriptMode ? 1 : 2
        };
        foreach (var agent in snapshot.Agents)
        {
            grid.Children.Add(CreateAgentMemoryCard(agent));
        }

        if (snapshot.Agents.Count == 0)
        {
            grid.Children.Add(CreateMemoryPlaceholder());
        }

        panel.Children.Add(grid);

        return new Border
        {
            Background = BlendBrush(ResourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.45),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Border CreateAgentMemoryCard(AgentState agent)
    {
        var accent = AccentForSpeaker(agent.Id);
        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = FormatParticipantTitle(agent.Id, agent.Name, uppercaseRole: true),
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{ShortModelName(agent.Model)} - {agent.PrivateNotes.Count} note(s)",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Edit", async (_, _) => await EditAgentMemoryNotesAsync(agent), true, TranscriptActionKind.Primary));
        actions.Children.Add(ActionButton("Clear", async (_, _) => await SaveAgentMemoryNotesAsync(agent.Id, []), agent.PrivateNotes.Count > 0, TranscriptActionKind.Danger));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        stack.Children.Add(header);

        if (agent.PrivateNotes.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No private notes yet.",
                Foreground = ResourceBrush("MutedTextBrush"),
                FontSize = 12,
                FontStyle = FontStyles.Italic
            });
        }
        else
        {
            var list = new StackPanel();
            foreach (var note in agent.PrivateNotes.Take(_wpfSettings.CompactTranscriptMode ? 4 : 6))
            {
                list.Children.Add(new TextBlock
                {
                    Text = "- " + note,
                    Foreground = ResourceBrush("TextBrush"),
                    FontSize = _wpfSettings.CompactTranscriptMode ? 12 : 13,
                    LineHeight = _wpfSettings.CompactTranscriptMode ? 17 : 19,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            if (agent.PrivateNotes.Count > (_wpfSettings.CompactTranscriptMode ? 4 : 6))
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"+ {agent.PrivateNotes.Count - (_wpfSettings.CompactTranscriptMode ? 4 : 6)} more",
                    Foreground = ResourceBrush("MutedTextBrush"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                });
            }

            stack.Children.Add(list);
        }

        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, _wpfSettings.CompactTranscriptMode ? 0 : 8, 8),
            MinHeight = _wpfSettings.CompactTranscriptMode ? 104 : 132,
            Child = stack
        };
    }

    private Border CreateMemoryPlaceholder()
    {
        return new Border
        {
            Background = ResourceBrush("InputBrush"),
            BorderBrush = ResourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = "No agents are available in this session.",
                Foreground = ResourceBrush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private async Task EditAgentMemoryNotesAsync(AgentState agent)
    {
        var edited = TextEditDialog.Show(
            this,
            _theme,
            $"Edit {agent.Name} memory",
            string.Join(Environment.NewLine, agent.PrivateNotes),
            "One memory note per line. Empty lines are ignored.");
        if (edited is null)
        {
            return;
        }

        await SaveAgentMemoryNotesAsync(agent.Id, NormalizeMemoryNotes(edited));
    }

    private async Task ClearAllAgentMemoryNotesAsync()
    {
        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Clear Memory Notes",
            "Clear private memory notes for every agent in this session?",
            "Clear",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            return;
        }

        await RunArenaBusyAsync("Clearing agent memory notes...", async () =>
        {
            if (_activeSession is null)
            {
                return;
            }

            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                return;
            }

            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.PrivateNotes.Clear();
            }

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_memory_notes_cleared", new { AllAgents = true });
            RefreshActiveSession("Cleared agent memory notes.");
        });
    }

    private async Task SaveAgentMemoryNotesAsync(string agentId, IReadOnlyList<string> notes)
    {
        await RunArenaBusyAsync($"Saving {agentId} memory notes...", async () =>
        {
            if (_activeSession is null)
            {
                return;
            }

            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            var agent = snapshot?.Engine.Agents.FirstOrDefault(item => item.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null || agent is null)
            {
                ArenaRunStatus.Text = $"Agent {agentId} not found.";
                return;
            }

            agent.PrivateNotes.Clear();
            agent.PrivateNotes.AddRange(notes);

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_memory_notes_saved", new { Agent = agentId, Count = notes.Count });
            RefreshActiveSession($"Saved {DisplayStatusValue(agentId)} memory notes.");
        });
    }

    private static IReadOnlyList<string> NormalizeMemoryNotes(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Length <= 400 ? note : note[..400])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToArray();
    }

    private IReadOnlyList<Border> CreateTranscriptStatPills(TranscriptMessage message, bool isInternet)
    {
        var pills = new List<Border>
        {
            CreateTranscriptStatPill(FormatDuration(message.LatencyMs), isInternet)
        };

        pills.Add(CreateTranscriptStatPill(FormatGeneratedTokens(message), isInternet));

        if (message.PromptTokens > 0)
        {
            pills.Add(CreateTranscriptStatPill($"ctx: {FormatCompactNumber(message.PromptTokens)}", isInternet));
        }
        else if (message.TotalTokens > 0 && message.CompletionTokens <= 0)
        {
            pills.Add(CreateTranscriptStatPill($"total: {FormatCompactNumber(message.TotalTokens)}", isInternet));
        }

        if (!message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            pills.Add(CreateTranscriptStatPill(message.Status, isInternet, isDanger: message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)));
        }

        if (message.Pinned)
        {
            pills.Add(CreateTranscriptStatPill("pinned", isInternet));
        }

        return pills;
    }

    private Border CreateTranscriptStatPill(string text, bool isInternet, bool isDanger = false, Brush? accentOverride = null, string? toolTip = null)
    {
        var compact = _wpfSettings.CompactTranscriptMode;
        var accent = accentOverride ?? ResourceBrush(isInternet ? "AssistBorderBrush" : "PrimaryBorderBrush");
        return new Border
        {
            Background = isDanger ? ResourceBrush("DangerBrush") : BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, 0.1),
            BorderBrush = isDanger ? ResourceBrush("DangerBorderBrush") : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.38),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = compact ? new Thickness(5, 0, 5, 1) : new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, compact ? 4 : 6, 0),
            ToolTip = toolTip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = isDanger ? ResourceBrush("DangerTextBrush") : accentOverride ?? ResourceBrush("MutedTextBrush"),
                FontSize = compact ? 10 : 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private Expander CreateTranscriptExpander(string header, Brush accent, UIElement content)
    {
        return new Expander
        {
            Header = header,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
            ToolTip = $"Show {header.ToLowerInvariant()}",
            Content = new Border
            {
                Background = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, 0.14),
                BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.42),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = content
            }
        };
    }

    private Border CreateTranscriptCardLayout(TranscriptMessage message, string body, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent, UIElement? extraContent)
    {
        var compact = _wpfSettings.CompactTranscriptMode;
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accentWeight = isLatest ? (compact ? 0.28 : 0.32) : (compact ? 0.14 : 0.18);
        var normalBackground = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, isError ? 0.16 : accentWeight);
        var hoverBackground = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, isError ? 0.22 : accentWeight + 0.06);
        var normalBorder = isError
            ? ResourceBrush("DangerBorderBrush")
            : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, searchMatch || isLatest ? 0.82 : 0.38);
        var hoverBorder = isError ? ResourceBrush("DangerTextBrush") : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.82);
        var border = new Border
        {
            Style = null,
            Background = normalBackground,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(searchMatch || isLatest || isError ? 2 : 1),
            CornerRadius = new CornerRadius(compact ? 6 : 8),
            Margin = new Thickness(0, 0, 0, compact ? 6 : 12),
            Opacity = isLatest || isError || isSystemEvent ? 1.0 : 0.88
        };
        border.MouseEnter += (_, _) =>
        {
            border.Background = hoverBackground;
            border.BorderBrush = hoverBorder;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = normalBackground;
            border.BorderBrush = normalBorder;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 46 : 84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rail = new Grid
        {
            Background = BlendBrush(ResourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.22 : 0.11)
        };
        rail.Children.Add(new Border
        {
            Width = isLatest ? (compact ? 4 : 5) : (compact ? 2 : 3),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(compact ? 6 : 8, 0, 0, compact ? 6 : 8)
        });
        var railStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = compact ? new Thickness(6, 8, 4, 6) : new Thickness(8, 13, 6, 12)
        };
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!compact)
        {
            railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var turnNumber = new TextBlock
        {
            Text = message.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontSize = compact ? 16 : 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(turnNumber, 0);
        railStack.Children.Add(turnNumber);

        if (!compact)
        {
            var avatar = CreateTranscriptAvatar(message, accent, isInternet, isSystemEvent);
            avatar.Margin = new Thickness(0, 7, 0, 5);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(avatar, 1);
            railStack.Children.Add(avatar);
        }

        var railLabel = new TextBlock
        {
            Text = compact ? CompactRailLabel(message, isInternet) : TranscriptRailLabel(message, isInternet),
            Foreground = accent,
            FontSize = compact ? 9 : 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(railLabel, compact ? 1 : 2);
        railStack.Children.Add(railLabel);

        rail.Children.Add(railStack);
        Grid.SetColumn(rail, 0);
        grid.Children.Add(rail);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var header = new Border
        {
            Background = BlendBrush(ResourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.28 : isError ? 0.18 : 0.16),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, isLatest ? 0.56 : 0.28),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = compact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(0, compact ? 6 : 8, 0, 0)
        };
        header.Child = CreateTranscriptHeader(message, accent, isInternet, searchMatch, isLatest, isSystemEvent);
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var bodyStack = new StackPanel
        {
            Margin = compact ? new Thickness(10, 8, 10, 8) : new Thickness(14, 13, 14, 13)
        };
        var bodyBlock = new TextBlock
        {
            Text = body,
            Foreground = isError ? ResourceBrush("DangerTextBrush") : ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = compact ? 13 : 15,
            LineHeight = compact ? 18 : 22
        };
        if (isError)
        {
            bodyStack.Children.Add(new Border
            {
                Background = BlendBrush(ResourceBrush("DangerBrush"), accent, 0.08),
                BorderBrush = ResourceBrush("DangerBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = bodyBlock
            });
        }
        else
        {
            bodyStack.Children.Add(bodyBlock);
        }

        if (extraContent is not null)
        {
            bodyStack.Children.Add(extraContent);
        }

        Grid.SetRow(bodyStack, 1);
        content.Children.Add(bodyStack);

        border.Child = grid;
        return border;
    }

    private AgentAvatarControl CreateTranscriptAvatar(TranscriptMessage message, Brush accent, bool isInternet, bool isSystemEvent)
    {
        var avatar = new AgentAvatarControl
        {
            Width = 44,
            Height = 44,
            AgentId = message.SpeakerId,
            DisplayName = message.Speaker,
            Model = message.Model,
            Persona = PersonaForSpeaker(message.SpeakerId),
            AccentBrush = accent,
            BaseBrush = ResourceBrush("TranscriptBodyBrush"),
            IsSystem = isSystemEvent || isInternet,
            AvatarStyle = CurrentAvatarStyle(),
            UseChampionPortrait = _wpfSettings.ChampionAvatars,
            UseSystemGlyph = _wpfSettings.SystemEventGlyphs,
            FallbackText = SpeakerGlyph(message, isInternet),
            ToolTip = AvatarToolTip(message, isSystemEvent)
        };
        ToolTipService.SetShowDuration(avatar, 60000);
        return avatar;
    }

    private Grid CreateTranscriptHeader(TranscriptMessage message, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = TranscriptSpeakerTitle(message, isInternet, isSystemEvent),
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        if (isLatest)
        {
            identity.Children.Add(CreateTranscriptStatPill("Latest", isInternet));
        }
        if (!isInternet && !string.IsNullOrWhiteSpace(message.Model))
        {
            identity.Children.Add(CreateTranscriptStatPill(message.Model, isInternet));
        }
        var voiceChip = VoiceStyleChipText(message.VoiceStyle);
        if (!isInternet && !isSystemEvent && !string.IsNullOrWhiteSpace(voiceChip))
        {
            identity.Children.Add(CreateTranscriptStatPill(voiceChip, isInternet));
            if (ShouldShowStyleFit())
            {
                var voiceAdherence = _voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text);
                identity.Children.Add(CreateTranscriptStatPill(
                    VoiceAdherenceChipText(voiceAdherence),
                    isInternet,
                    accentOverride: VoiceAdherenceAccent(voiceAdherence),
                    toolTip: VoiceAdherenceTooltip(voiceAdherence)));
            }
        }
        foreach (var pill in CreateTranscriptStatPills(message, isInternet))
        {
            identity.Children.Add(pill);
        }
        if (searchMatch)
        {
            identity.Children.Add(CreateTranscriptStatPill("search match", isInternet));
        }
        Grid.SetColumn(identity, 0);
        header.Children.Add(identity);

        var time = new TextBlock
        {
            Text = DisplayTime(message.CreatedAt),
            Foreground = ResourceBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(time, 1);
        header.Children.Add(time);

        return header;
    }

    private static string TranscriptSpeakerTitle(TranscriptMessage message, bool isInternet, bool isSystemEvent)
    {
        if (isSystemEvent)
        {
            return "SYSTEM EVENT";
        }

        if (isInternet)
        {
            return message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                ? "Internet Approval"
                : "Internet Tool";
        }

        return string.IsNullOrWhiteSpace(message.Speaker) ? "Unknown" : message.Speaker;
    }

    private static string TranscriptRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet) || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSTEM";
        }

        return message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase)
            ? "OPERATOR"
            : "AGENT";
    }

    private static string CompactRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet) || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return "SYS";
        }

        if (message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            return "OP";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerId)
            ? "AI"
            : message.SpeakerId[..Math.Min(3, message.SpeakerId.Length)].ToUpperInvariant();
    }

    private static string SpeakerGlyph(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet))
        {
            return "!";
        }

        if (isInternet)
        {
            return "i";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerId)
            ? "?"
            : message.SpeakerId[..1].ToUpperInvariant();
    }

    private string PersonaForSpeaker(string speakerId)
    {
        return _lastAgentPersonas.TryGetValue(speakerId, out var persona) ? persona : "";
    }

    private static string AvatarToolTip(TranscriptMessage message, bool isSystemEvent)
    {
        var speaker = string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker;
        var model = string.IsNullOrWhiteSpace(message.Model) ? "-" : message.Model;
        var kind = isSystemEvent ? "System event" : "Deterministic procedural avatar";
        return $"{speaker}{Environment.NewLine}Model: {model}{Environment.NewLine}{kind}";
    }

    private static bool IsSystemEvent(TranscriptMessage message, bool isInternet)
    {
        if (isInternet || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!message.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Text.Contains("Model call failed", StringComparison.OrdinalIgnoreCase)
            || message.Text.Contains("Provider unreachable", StringComparison.OrdinalIgnoreCase)
            || message.Text.Contains("provider", StringComparison.OrdinalIgnoreCase);
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

    private Border CreateNewsInspectorCard(TranscriptMessage message)
    {
        var isPending = message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accent = isError
            ? ResourceBrush("DangerBorderBrush")
            : isPending
                ? ResourceBrush("NarratorAccentBrush")
                : ResourceBrush("AssistBorderBrush");
        var title = $"Turn {message.Turn} - {InternetInspectorTitle(message)}";
        var body = string.Join(
            Environment.NewLine,
            string.IsNullOrWhiteSpace(message.InternetTool) ? "" : $"Tool: {message.InternetTool}",
            string.IsNullOrWhiteSpace(message.InternetQuery) ? "" : $"Query: {message.InternetQuery}",
            string.IsNullOrWhiteSpace(message.InternetUrl) ? "" : $"URL: {message.InternetUrl}",
            string.IsNullOrWhiteSpace(message.InternetReason) ? "" : $"Why selected: {message.InternetReason}",
            string.IsNullOrWhiteSpace(message.InternetCheckedAt) ? "" : $"Fetched: {message.InternetCheckedAt}",
            string.IsNullOrWhiteSpace(message.Text) ? "" : $"Drop: {message.Text}")
            .Trim();
        var extras = CreateTranscriptExpander("Internet details", accent, CreateInternetDetails(message));
        return CreateCard(title, string.IsNullOrWhiteSpace(body) ? "(no internet details)" : body, ResourceBrush("CardBrush"), accent, extras);
    }

    private static string InternetInspectorTitle(TranscriptMessage message)
    {
        if (message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase))
        {
            return $"Approval - {message.Status}";
        }

        if (message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return $"Curated news - {message.Status}";
        }

        return $"Internet tool - {message.Status}";
    }

    private UIElement CreateInternetDetails(TranscriptMessage message)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 2, 0, 0)
        };

        void AddRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var row = new Border
            {
                Background = ResourceBrush("InputBrush"),
                BorderBrush = ResourceBrush("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = $"{label}: {value}",
                    Foreground = ResourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            panel.Children.Add(row);
        }

        AddRow("Requester", message.InternetRequester);
        AddRow("Mode", message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase) ? "pending approval" : "executed");
        AddRow("Tool", message.InternetTool);
        AddRow("Query", message.InternetQuery);
        AddRow("URL", message.InternetUrl);
        AddRow("Reason", message.InternetReason);
        AddRow("Fetch", string.IsNullOrWhiteSpace(message.InternetCheckedAt)
            ? ""
            : $"{(message.InternetCached ? "cached" : "fetched")} at {message.InternetCheckedAt}");
        AddRow("Summary", message.InternetSummary);
        if (message.InternetSources.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Sources",
                Foreground = ResourceBrush("MutedTextBrush"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4)
            });
            foreach (var source in message.InternetSources)
            {
                panel.Children.Add(new Border
                {
                    Background = BlendBrush(ResourceBrush("TranscriptBodyBrush"), ResourceBrush("AssistBorderBrush"), 0.12),
                    BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), ResourceBrush("AssistBorderBrush"), 0.45),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = source,
                        Foreground = ResourceBrush("TextBrush"),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        return panel;
    }

    private Button ActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        var iconMode = !string.IsNullOrWhiteSpace(iconGlyph);
        var compact = _wpfSettings.CompactTranscriptMode;
        var iconSize = compact ? 24 : 30;
        var background = ResourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => ResourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("ControlBorderBrush")
        };
        var foreground = kind switch
        {
            TranscriptActionKind.Primary => ResourceBrush("TextBrush"),
            TranscriptActionKind.Danger => ResourceBrush("DangerTextBrush"),
            _ => ResourceBrush("TextBrush")
        };
        var button = new Button
        {
            Content = iconMode
                ? new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = compact ? 13 : 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : text,
            IsEnabled = enabled && !_arenaBusy,
            Tag = enabled,
            Background = iconMode ? Brushes.Transparent : background,
            BorderBrush = iconMode ? Brushes.Transparent : border,
            Foreground = foreground,
            FontSize = iconMode ? (compact ? 13 : 15) : (compact ? 12 : 13),
            FontWeight = FontWeights.SemiBold,
            MinWidth = iconMode ? iconSize : 0,
            MinHeight = iconMode ? iconSize : (compact ? 26 : 32),
            Width = iconMode ? iconSize : double.NaN,
            Height = iconMode ? iconSize : double.NaN,
            Padding = iconMode ? new Thickness(0) : compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5),
            Margin = iconMode ? new Thickness(0, 0, compact ? 3 : 5, compact ? 2 : 4) : new Thickness(0, 0, compact ? 5 : 8, compact ? 5 : 8),
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        if (handler is not null)
        {
            button.Click += handler;
        }
        _transcriptActionButtons.Add(button);
        return button;
    }

    private enum TranscriptActionKind
    {
        Neutral,
        Primary,
        Danger
    }

    private Border CreateAgentStatusCard(string title, string status, Brush accent)
    {
        var card = new Border
        {
            Background = ResourceBrush("CardBrush"),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = ResourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title
        });
        text.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = status
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        return card;
    }

    private Border CreateAgentCard(AgentState agent, string? currentAgentId)
    {
        var isActive = agent.Active;
        var isPaused = !isActive || agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase);
        var isCurrent = isActive && string.Equals(agent.Id, currentAgentId, StringComparison.OrdinalIgnoreCase);
        var isWorkingStatus = IsAgentWorkingStatus(agent.Status);
        var isRunning = isActive && isWorkingStatus;
        var showActivitySweep = isRunning;
        var speakerLabel = DisplayStatusValue(agent.Id);
        var activityLabel = isRunning ? "thinking" : isCurrent ? "current" : isPaused ? "paused" : "waiting";
        var playButton = new Button
        {
            Content = "▶",
            IsEnabled = isActive && !_arenaBusy && !string.IsNullOrWhiteSpace(agent.Id),
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = isActive ? ResourceBrush("PrimaryBrush") : ResourceBrush("DisabledBrush"),
            BorderBrush = isActive ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DisabledBorderBrush"),
            Foreground = isActive ? Brushes.White : ResourceBrush("DisabledTextBrush"),
            ToolTip = isActive
                ? $"Run one turn for {agent.Name}"
                : "Inactive: increase Active participants in App Settings to include this agent."
        };
        playButton.Click += async (_, _) => await RunAgentTurnAsync(agent);
        _agentTurnButtons.Add(playButton);

        var identityAccent = AccentForSpeaker(agent.Id);
        var stateAccent = ResourceBrush("PrimaryBorderBrush");
        var card = new Border
        {
            Background = isRunning
                ? BlendBrush(ResourceBrush("InputBrush"), stateAccent, 0.18)
                : isCurrent
                    ? BlendBrush(ResourceBrush("InputBrush"), stateAccent, 0.1)
                    : isPaused
                        ? BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("DisabledBorderBrush"), 0.12)
                        : ResourceBrush("InputBrush"),
            BorderBrush = isPaused
                ? BlendBrush(ResourceBrush("DisabledBorderBrush"), ResourceBrush("MutedTextBrush"), 0.18)
                : BlendBrush(ResourceBrush("DisabledBorderBrush"), isRunning || isCurrent ? stateAccent : identityAccent, 0.75),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true
        };

        var cardLayer = new Grid();
        if (showActivitySweep)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(stateAccent, isRunning));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{speakerLabel} - {activityLabel}",
            Foreground = isPaused ? ResourceBrush("MutedTextBrush") : Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = agent.Name
        });
        var modelText = string.IsNullOrWhiteSpace(agent.Model) ? "model not set" : agent.Model;
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isPaused
                ? ResourceBrush("DisabledTextBrush")
                : isRunning || isCurrent
                ? stateAccent
                : ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{modelText}\n{agent.Name}"
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = isPaused
                ? ResourceBrush("DisabledBorderBrush")
                : isRunning || isCurrent ? stateAccent : identityAccent,
            Opacity = isRunning ? 1.0 : isCurrent ? 0.85 : isPaused ? 0.35 : 0.45,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = activityLabel
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        var routeControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        var muteButton = CreateAgentModeButton("⏸", isActive ? "Pause agent" : "Activate paused agent", isPaused);
        muteButton.Click += async (_, _) => await SetAgentMuteAsync(agent.Id, mute: isActive);
        routeControls.Children.Add(muteButton);
        var soloButton = CreateAgentModeButton("S", "Solo this agent");
        soloButton.Click += async (_, _) => await SoloAgentAsync(agent.Id);
        routeControls.Children.Add(soloButton);
        Grid.SetColumn(routeControls, 2);
        grid.Children.Add(routeControls);

        Grid.SetColumn(playButton, 3);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        if (!isActive)
        {
            card.ToolTip = "Paused: click the pause button to activate this agent.";
        }

        return card;
    }

    private Button CreateAgentModeButton(string content, string tooltip, bool highlighted = false)
    {
        return new Button
        {
            Content = content,
            Width = 24,
            MinWidth = 24,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = content == "⏸" ? 12 : 9.5,
            FontFamily = content == "⏸" ? new FontFamily("Segoe UI Symbol") : SystemFonts.MessageFontFamily,
            Background = highlighted ? BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("BetaAccentBrush"), 0.28) : ResourceBrush("InputBrush"),
            BorderBrush = highlighted ? ResourceBrush("BetaAccentBrush") : ResourceBrush("DisabledBorderBrush"),
            Foreground = highlighted ? ResourceBrush("BetaAccentBrush") : ResourceBrush("MutedTextBrush"),
            ToolTip = tooltip
        };
    }

    private async Task SetAgentMuteAsync(string agentId, bool mute)
    {
        await RunArenaBusyAsync(mute ? $"Muting {agentId}..." : $"Activating {agentId}...", null, async () =>
        {
            if (_activeSession is null)
            {
                return;
            }

            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            var agent = snapshot?.Engine.Agents.FirstOrDefault(item => item.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null || agent is null)
            {
                ArenaRunStatus.Text = $"Agent {agentId} not found.";
                return;
            }

            agent.Active = !mute;
            if (!agent.Active)
            {
                agent.Status = "muted";
            }
            else if (agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(agent.Status))
            {
                agent.Status = "waiting";
            }

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_active_changed", new { Agent = agentId, Active = agent.Active });
            await RefreshActiveSessionAsync(agent.Active ? $"{DisplayStatusValue(agentId)} activated." : $"{DisplayStatusValue(agentId)} muted.");
        }, allowDuringAutoChat: true);
    }

    private async Task SoloAgentAsync(string agentId)
    {
        await RunArenaBusyAsync($"Soloing {agentId}...", null, async () =>
        {
            if (_activeSession is null)
            {
                return;
            }

            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                return;
            }

            foreach (var agent in snapshot.Engine.Agents)
            {
                var selected = agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase);
                agent.Active = selected;
                agent.Status = selected ? "waiting" : "muted";
            }

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_agent_solo_enabled", new { Agent = agentId });
            await RefreshActiveSessionAsync($"{DisplayStatusValue(agentId)} solo enabled.");
        }, allowDuringAutoChat: true);
    }

    private Border CreateNarratorCard(ArenaViewSnapshot snapshot)
    {
        var accent = ResourceBrush("NarratorAccentBrush");
        var isRunning = IsAgentWorkingStatus(snapshot.NarratorStatus);
        var modelText = string.IsNullOrWhiteSpace(snapshot.NarratorModel) ? "model not set" : snapshot.NarratorModel;
        var status = string.IsNullOrWhiteSpace(snapshot.NarratorStatus) ? "idle" : snapshot.NarratorStatus;
        var buttonEnabled = !_arenaBusy || _autoChatCancellation is not null;
        var playButton = new Button
        {
            Content = "▶",
            IsEnabled = buttonEnabled,
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.5),
            BorderBrush = accent,
            Foreground = Brushes.White,
            ToolTip = "Narrate now"
        };
        playButton.Click += NarrateNowButton_Click;
        _narratorActionButtons.Add(playButton);

        var card = new Border
        {
            Background = isRunning
                ? BlendBrush(ResourceBrush("InputBrush"), accent, 0.18)
                : ResourceBrush("InputBrush"),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, 0.58),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(snapshot.NarratorPersona)
                ? "Narrator"
                : snapshot.NarratorPersona
        };

        var cardLayer = new Grid();
        if (isRunning)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(accent, isRunning: true));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"Narrator - {DisplayInlineStatus(status)}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Narrator"
        });
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isRunning ? accent : ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = modelText
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = accent,
            Opacity = isRunning ? 1.0 : 0.45,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = status
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        Grid.SetColumn(playButton, 2);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        return card;
    }

    private static string DisplayInlineStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "-" : status.Trim().ToLowerInvariant();
    }

    private static bool IsAgentWorkingStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "thinking" or "generating" or "running" or "working" or "busy";
    }

    private Border CreateAgentActivitySweep(Brush accent, bool isRunning)
    {
        var accentColor = BrushColor(accent, Colors.DeepSkyBlue);
        var sweep = new Border
        {
            Width = 86,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Opacity = isRunning ? 0.92 : 0.56,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0),
                    new(Color.FromArgb(isRunning ? (byte)74 : (byte)38, accentColor.R, accentColor.G, accentColor.B), 0.48),
                    new(Colors.Transparent, 1)
                },
                new Point(0, 0.5),
                new Point(1, 0.5))
        };

        var translate = new TranslateTransform(-110, 0);
        sweep.RenderTransform = translate;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-110, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 1350 : 1900))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 2050 : 3000))));
        translate.BeginAnimation(TranslateTransform.XProperty, animation);
        return sweep;
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        return CreateCard(CreateCardTitle(title), body, background, accent, extraContent);
    }

    private Border CreateCard(UIElement title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        var border = new Border
        {
            Style = null,
            Background = background,
            BorderBrush = ResourceBrush("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Margin = new Thickness(-16, -16, 10, -16)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(stack, 1);
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        });
        if (extraContent is not null)
        {
            stack.Children.Add(extraContent);
        }
        grid.Children.Add(stack);

        border.Child = grid;
        return border;
    }

    private TextBlock CreateCardTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static string FormatDuration(int latencyMs)
    {
        if (latencyMs <= 0)
        {
            return "time unknown";
        }

        return latencyMs < 1000
            ? $"{latencyMs} ms"
            : $"{latencyMs / 1000.0:0.0}s";
    }

    private static string FormatGeneratedTokens(TranscriptMessage message)
    {
        return message.CompletionTokens > 0
            ? $"{FormatCompactNumber(message.CompletionTokens)} Tok"
            : "Tok unknown";
    }

    private static string FormatCompactNumber(int value)
    {
        return value >= 1000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.TestProviderAsync(TestProviderButton);
        }
    }

    private async void ApplyProviderPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ApplyProviderPresetAsync();
        }
    }

    private async void PreloadSelectedModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.PreloadSelectedModelsAsync();
        }
    }

    private async void AutoConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.AutoConfigureAsync();
        }
    }

    private async void ApplyAutoConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ApplyAutoConfigureAsync();
        }
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot)
        {
            return;
        }

        if (_activeSession is null)
        {
            await _coreSessionStore.EnsureDefaultSessionAsync();
            await LoadSessionsAsync("default");
            if (_activeSession is null)
            {
                ProviderTestStatus.Text = "No writable session is available for settings.";
                return;
            }
        }

        var baseUrl = ProviderBaseUrlText.Text.Trim();
        var model = ProviderModelText.Text.Trim();
        ProviderSettings.SaveRoleModelDrafts();
        var alphaModel = ProviderSettings.RoleModel("alpha");
        var betaModel = ProviderSettings.RoleModel("beta");
        var gammaModel = ProviderSettings.RoleModel("gamma");
        var deltaModel = ProviderSettings.RoleModel("delta");
        var narratorModel = ProviderSettings.RoleModel("narrator");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            ProviderTestStatus.Text = "Base URL and model are required.";
            return;
        }

        if (!int.TryParse(ProviderTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout))
        {
            ProviderTestStatus.Text = "Timeout must be a whole number.";
            return;
        }

        if (!double.TryParse(ProviderTemperatureText.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature))
        {
            ProviderTestStatus.Text = "Temperature must be a number.";
            return;
        }

        if (!int.TryParse(ProviderMaxOutputText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var maxOutput))
        {
            ProviderTestStatus.Text = "Max output must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextTranscriptWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var transcriptWindow))
        {
            ProviderTestStatus.Text = "Transcript window must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextPrivateWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var privateWindow))
        {
            ProviderTestStatus.Text = "Private notes window must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextNotesWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var notesWindow))
        {
            ProviderTestStatus.Text = "Pinned notes window must be a whole number.";
            return;
        }

        if (!int.TryParse(InternetMaxResultsText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var internetMaxResults))
        {
            ProviderTestStatus.Text = "Internet results must be a whole number.";
            return;
        }

        timeout = Math.Clamp(timeout, 1, 3600);
        temperature = Math.Clamp(temperature, 0, 2);
        maxOutput = Math.Clamp(maxOutput, 1, 32768);
        transcriptWindow = Math.Clamp(transcriptWindow, 1, 60);
        privateWindow = Math.Clamp(privateWindow, 0, 60);
        notesWindow = Math.Clamp(notesWindow, 0, 60);
        internetMaxResults = Math.Clamp(internetMaxResults, 1, 10);
        var activeParticipants = ParseActiveParticipants();
        var internetMode = ShellUiHelpers.SelectedComboTag(InternetModePicker, "manual");
        var useInternet = UseInternetCheckBox.IsChecked == true;
        if (!useInternet)
        {
            internetMode = "off";
        }
        else if (internetMode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            internetMode = "auto";
        }

        var internetSourceScope = ShellUiHelpers.SelectedComboTag(InternetSourceScopePicker, "trusted");

        await RunArenaBusyAsync("Applying settings...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                snapshot = SessionStore.CreateDefaultSnapshot();
            }

            snapshot.Configs["shared"] = new CoreModelProviderConfig
            {
                BaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl),
                Model = model,
                Timeout = timeout,
                Temperature = temperature,
                MaxOutputTokens = maxOutput,
                LastError = snapshot.Configs.TryGetValue("shared", out var existing) ? existing.LastError : "",
                LastLatencyMs = snapshot.Configs.TryGetValue("shared", out existing) ? existing.LastLatencyMs : 0,
                LastTestOk = snapshot.Configs.TryGetValue("shared", out existing) && existing.LastTestOk
            };
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "alpha", alphaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "beta", betaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "gamma", gammaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "delta", deltaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "narrator", narratorModel, snapshot.Configs["shared"]);
            snapshot.Engine.TranscriptWindow = transcriptWindow;
            snapshot.Engine.PrivateWindow = privateWindow;
            snapshot.Engine.NotesWindow = notesWindow;
            snapshot.Engine.ModelRss.UseInternet = useInternet && !internetMode.Equals("off", StringComparison.OrdinalIgnoreCase);
            snapshot.Engine.ModelRss.Mode = internetMode;
            snapshot.Engine.ModelRss.SourceScope = internetSourceScope;
            snapshot.Engine.ModelRss.MaxResults = internetMaxResults;
            snapshot.Engine.ModelRss.AllowParticipantRequests = AllowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowModelRss = AllowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowNarratorRequests = AllowNarratorInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.RequireApproval = RequireInternetApprovalCheckBox.IsChecked == true;
            AgentRosterService.EnsureParticipantCount(snapshot, Math.Max(activeParticipants, AgentRosterService.MinParticipants));
            for (var index = 0; index < snapshot.Engine.Agents.Count; index++)
            {
                snapshot.Engine.Agents[index].Active = AgentRosterService.IsParticipantId(snapshot.Engine.Agents[index].Id)
                    && index < activeParticipants;
            }

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_settings_applied", new
            {
                snapshot.Configs["shared"].BaseUrl,
                snapshot.Configs["shared"].Model,
                snapshot.Configs["shared"].Timeout,
                snapshot.Configs["shared"].Temperature,
                snapshot.Configs["shared"].MaxOutputTokens,
                AlphaModel = alphaModel,
                BetaModel = betaModel,
                GammaModel = gammaModel,
                DeltaModel = deltaModel,
                NarratorModel = narratorModel,
                snapshot.Engine.ModelRss.UseInternet,
                snapshot.Engine.ModelRss.Mode,
                snapshot.Engine.ModelRss.SourceScope,
                snapshot.Engine.ModelRss.MaxResults,
                snapshot.Engine.ModelRss.AllowParticipantRequests,
                snapshot.Engine.ModelRss.AllowNarratorRequests,
                snapshot.Engine.ModelRss.RequireApproval,
                ActiveParticipants = activeParticipants
            });
            ProviderTestStatus.Text = "Settings applied.";
            RefreshActiveSession("Settings applied.");
            _ = RefreshProviderReachabilityAsync(force: true);
        });
    }

    private async void AutoChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null || _autoChatCancellation is not null)
        {
            return;
        }

        _autoChatCancellation = new CancellationTokenSource();
        var token = _autoChatCancellation.Token;
        SetArenaBusy(true, "Auto Chat running...", stopEnabled: true, AutoChatButton);

        try
        {
            while (!token.IsCancellationRequested && _activeSession is not null)
            {
                await _arenaOperationLock.WaitAsync(token);
                OneTurnResult result;
                try
                {
                    result = await _turnRunner.RunOneTurnAsync(_activeSession.Id, ShouldEnforceVoiceDrift(), token);
                }
                finally
                {
                    _arenaOperationLock.Release();
                }

                var status = result.Ok && result.Message is not null
                    ? $"Auto Chat: {result.Message.Speaker} spoke ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                    : $"Auto Chat stopped: {result.Error}";
                RefreshActiveSession(status);
                if (!result.Ok)
                {
                    break;
                }

                if (result.Message is not null
                    && result.Message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                    && result.Message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = await InternetWorkflow.HandleInternetApprovalDialogAsync(result.Message, resumeAutoChat: true);
                    if (!resolved)
                    {
                        ArenaRunStatus.Text = "Auto Chat paused for internet approval.";
                        LoadStatus.Text = ArenaRunStatus.Text;
                        break;
                    }
                }

                await Task.Delay(AutoChatCadence(), token);
            }
        }
        catch (OperationCanceledException)
        {
            ArenaRunStatus.Text = "Auto Chat stopped.";
            LoadStatus.Text = "Auto Chat stopped.";
        }
        finally
        {
            _autoChatCancellation?.Dispose();
            _autoChatCancellation = null;
            SetArenaBusy(false, ArenaRunStatus.Text, stopEnabled: false);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _autoChatCancellation?.Cancel();
        ArenaRunStatus.Text = "Stopping Auto Chat...";
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Reset Arena",
            "Reset the current arena transcript and live state?\n\nScenario, cast, locks, provider settings, and checkpoints are preserved.",
            "Reset",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            ArenaRunStatus.Text = "Reset cancelled.";
            return;
        }

        await RunArenaBusyAsync("Resetting arena...", ResetButton, async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ArenaRunStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Narration.Clear();
            snapshot.Engine.TurnCount = 0;
            snapshot.Engine.TurnIndex = 0;
            snapshot.Engine.LastError = "";
            snapshot.Engine.Narrator.Status = "idle";
            snapshot.Engine.Narrator.LastError = "";
            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.Status = "waiting";
                agent.PrivateNotes.Clear();
            }

            await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_arena_reset", new { session = _activeSession.Id });
            RefreshActiveSession("Arena reset.");
        });
    }

    private async void RandomSeedButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.GenerateRandomSeedAsync();
    }

    private async void AiChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.GenerateAiChoiceAsync();
    }

    private async void YoloScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.GenerateYoloSeedAsync();
    }

    private async void ReplayGenerationButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.ReplayGenerationAsync();
    }

    private async void ReplayNewRunButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.ReplayGenerationToNewRunAsync();
    }

    private void CopyGenerationSeedButton_Click(object sender, RoutedEventArgs e)
    {
        ScenarioWorkflow.CopyGenerationSeed();
    }

    private async void ApplyRivalryMatrixButton_Click(object sender, RoutedEventArgs e)
    {
        await MatchSetup.ApplyRivalryMatrixAsync();
    }

    private void GenerationHistoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _scenarioWorkflowCoordinator?.UpdateGenerationHistoryActions();
    }

    private async void NarrateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Narrator thinking...", NarrateNowButton, async () =>
        {
            var result = await _narratorService.NarrateNowAsync(_activeSession.Id);
            var status = result.Ok && result.Message is not null
                ? $"Narrator added turn {result.Message.Turn} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"Narrator failed: {result.Error}";
            RefreshActiveSession(status);
        }, allowDuringAutoChat: true);
    }

    private async void CurateNewsButton_Click(object sender, RoutedEventArgs e)
    {
        await InternetWorkflow.CurateNewsAsync();
    }

    private async void OneTurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Running native 1 TURN...", OneTurnButton, async () =>
        {
            var result = await _turnRunner.RunOneTurnAsync(_activeSession.Id, ShouldEnforceVoiceDrift());
            var status = result.Ok && result.Message is not null
                ? $"1 TURN complete: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"1 TURN failed: {result.Error}";
            await RefreshActiveSessionAsync(status);
            ArenaRunStatus.Text = status;
            LoadStatus.Text = status;
            if (result.Message is not null
                && result.Message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                && result.Message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                await InternetWorkflow.HandleInternetApprovalDialogAsync(result.Message, resumeAutoChat: false);
            }
        });
    }

    private async Task RunAgentTurnAsync(AgentState agent)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync($"Running {agent.Name} once...", async () =>
        {
            var result = await _turnRunner.RunAgentTurnAsync(_activeSession.Id, agent.Id, ShouldEnforceVoiceDrift());
            var status = result.Ok && result.Message is not null
                ? $"{agent.Name} one-shot complete: {result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms"
                : $"{agent.Name} one-shot failed: {result.Error}";
            RefreshActiveSession(status);
        });
    }

    private async void SendTurnButton_Click(object sender, RoutedEventArgs e)
    {
        await OperatorTurn.SendOperatorTurnAsync();
    }

    private void OperatorPublicRouteButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.SetRouteMode("public");
    }

    private void OperatorPrivateRouteButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.SetRouteMode("private");
    }

    private void OperatorNarratorRouteButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.SetRouteMode("narrator");
    }

    private void OperatorPrivateTargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _operatorTurnCoordinator?.OnPrivateTargetChanged();
    }

    private async void OperatorTurnText_KeyDown(object sender, KeyEventArgs e)
    {
        await OperatorTurn.OnTurnTextKeyDownAsync(e);
    }

    private void UseOperatorTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.UseOperatorTemplate();
    }

    private void SaveOperatorTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.SaveOperatorTemplate();
    }

    private void DeleteOperatorTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        OperatorTurn.DeleteOperatorTemplate();
    }

    private void OperatorTurnText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _operatorTurnCoordinator?.UpdateTurnMeter();
    }

    private void CopyTranscriptMessage(TranscriptMessage message)
    {
        TranscriptExportCoordinator.CopyMessage(message);
    }

    private void CopyInternetUrl(TranscriptMessage message)
    {
        TranscriptExportCoordinator.CopyInternetUrl(message);
    }

    private void ExportTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptExportCoordinator.ExportTranscript();
    }

    private async Task DeleteTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync($"Deleted turn {message.Turn}.", async snapshot =>
        {
            var deleted = _transcriptService.DeleteMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (!deleted)
            {
                LoadStatus.Text = $"Could not find turn {message.Turn} to delete.";
                return false;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, "native_transcript_message_deleted", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
        });
    }

    private async Task RetryTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0 || !IsAgentSpeaker(message.SpeakerId))
        {
            return;
        }

        await RunArenaBusyAsync($"Retrying turn {message.Turn} with {message.Speaker}...", async () =>
        {
            var result = await _turnRunner.RetryTurnAsync(_activeSession.Id, message.Turn, message.SpeakerId, message.CreatedAt, ShouldEnforceVoiceDrift());
            var status = result.Ok && result.Message is not null
                ? $"Retry replaced turn {message.Turn}: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"Retry failed: {result.Error}";
            RefreshActiveSession(status);
        });
    }

    private async Task TogglePinTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync(message.Pinned ? $"Unpinned turn {message.Turn}." : $"Pinned turn {message.Turn}.", async snapshot =>
        {
            var changed = _transcriptService.TogglePinned(snapshot, message.Turn, message.SpeakerId, message.CreatedAt, out var pinned);
            if (!changed)
            {
                LoadStatus.Text = $"Could not find turn {message.Turn} to pin.";
                return false;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, pinned ? "native_transcript_message_pinned" : "native_transcript_message_unpinned", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
        });
    }

    private async Task MutateTranscriptAsync(string successStatus, Func<AIArena.Core.Models.ArenaSnapshot, Task<bool>> mutation)
    {
        if (_activeSession is null)
        {
            return;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
            return;
        }

        if (!await mutation(snapshot))
        {
            return;
        }

        await SaveSnapshotWithFeedbackAsync(snapshot, _activeSession.Id);
        RefreshActiveSession(successStatus);
    }

    private async Task<CoreModelProviderConfig?> LoadSharedProviderConfigAsync()
    {
        return _activeSession is null
            ? null
            : await _providerReachabilityService.LoadSharedConfigAsync(_activeSession.Id);
    }

    private async Task RefreshProviderReachabilityAsync(bool force = false)
    {
        if (_activeSession is null || (_arenaBusy && !force))
        {
            return;
        }

        var result = await _providerReachabilityService.RefreshAsync(_activeSession.Id);
        if (result is null)
        {
            return;
        }

        _providerSettingsCoordinator?.RecordProviderReachabilityCheck(result.CheckedAt, result.ModelCount);
        _providerHealthTimer.Interval = result.NextInterval;
        await UpdateActiveProviderStatusOnlyAsync(result.Status);
        UpdateProviderHealthPopup();
    }

    private async Task PersistProviderReachabilityAsync(
        bool online,
        string error,
        int latencyMs,
        string status,
        AIArena.Core.Models.ArenaSnapshot? snapshot = null,
        string? sessionId = null)
    {
        if (_activeSession is null)
        {
            return;
        }

        sessionId ??= _activeSession.Id;
        var result = await _providerReachabilityService.PersistAsync(sessionId, online, error, latencyMs, status, snapshot);
        if (result is null)
        {
            return;
        }

        _providerHealthTimer.Interval = result.NextInterval;
        await UpdateActiveProviderStatusOnlyAsync(result.Status);
        UpdateProviderHealthPopup();
    }

    private async Task UpdateActiveProviderStatusOnlyAsync(string status)
    {
        if (_activeSession is null)
        {
            return;
        }

        var latest = (await _coreSessionStore.ListSessionsAsync()).FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latest is null)
        {
            return;
        }

        var coreSnapshot = await _coreSessionStore.LoadSnapshotAsync(latest.Id);
        if (coreSnapshot is null)
        {
            return;
        }

        var snapshot = SnapshotViewMapper.FromCore(latest, coreSnapshot);
        _activeSession = latest;
        _activeSnapshotWriteUtc = latest.LastModified;
        _lastRenderedSnapshot = snapshot;
        UpdateTopBarStatus(snapshot);
        if (!_arenaBusy)
        {
            ArenaRunStatus.Text = status;
        }
    }

    private void SavedStateModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _savedStateCoordinator?.OnModeSelectionChanged();
    }

    private void SavedStateItemPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _savedStateCoordinator?.OnItemSelectionChanged();
    }

    private async void SavedStateSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedStateCoordinator is not null)
        {
            await _savedStateCoordinator.SaveAsync();
        }
    }

    private async void SavedStateLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedStateCoordinator is not null)
        {
            await _savedStateCoordinator.LoadAsync();
        }
    }

    private async void SavedStateDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedStateCoordinator is not null)
        {
            await _savedStateCoordinator.DeleteAsync();
        }
    }

    private static async Task RunBusyAsync(Control control, Func<Task> action)
    {
        control.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            control.IsEnabled = true;
        }
    }

    private async Task SaveSnapshotWithFeedbackAsync(AIArena.Core.Models.ArenaSnapshot snapshot, string sessionId)
    {
        SetSaveStatus("Saving...", ResourceBrush("MutedTextBrush"));
        try
        {
            await _coreSessionStore.SaveSnapshotAsync(snapshot, sessionId);
            SetSaveStatus($"Saved {DateTime.Now:h:mm tt}", ResourceBrush("AlphaAccentBrush"));
        }
        catch (Exception ex)
        {
            SetSaveStatus($"Save failed: {ex.Message}", ResourceBrush("DangerTextBrush"));
            throw;
        }
    }

    private void SetSaveStatus(string text, Brush brush)
    {
        SaveStatusText.Text = text;
        SaveStatusText.Foreground = brush;
        SaveStatusText.ToolTip = text;
    }

    private Task RunArenaBusyForCoordinatorAsync(string status, Func<Task> action)
    {
        return RunArenaBusyAsync(status, action);
    }

    private Task RunArenaBusyForCoordinatorAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat)
    {
        return RunArenaBusyAsync(status, operationButton, action, allowDuringAutoChat);
    }

    private Task SaveSnapshotForCoordinatorAsync(AIArena.Core.Models.ArenaSnapshot snapshot, string sessionId)
    {
        return SaveSnapshotWithFeedbackAsync(snapshot, sessionId);
    }

    private Task RefreshActiveSessionForCoordinatorAsync(string status)
    {
        return RefreshActiveSessionAsync(status);
    }

    private void SetLoadStatus(string status)
    {
        LoadStatus.Text = status;
    }

    private void SetArenaRunStatus(string status)
    {
        ArenaRunStatus.Text = status;
    }

    private async Task RunArenaBusyAsync(string status, Func<Task> action)
    {
        await RunArenaBusyAsync(status, null, action);
    }

    private async Task RunArenaBusyAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat = false)
    {
        var ownsBusyState = !_arenaBusy;
        var runsDuringAutoChat = !ownsBusyState && allowDuringAutoChat && _autoChatCancellation is not null;
        if (!ownsBusyState && !runsDuringAutoChat)
        {
            return;
        }

        if (ownsBusyState)
        {
            SetArenaBusy(true, status, stopEnabled: false, operationButton);
        }
        else
        {
            ArenaRunStatus.Text = status;
            LoadStatus.Text = status;
            SetBreathingOperationButton(operationButton);
            if (operationButton is not null)
            {
                operationButton.IsEnabled = false;
            }
        }

        try
        {
            await _arenaOperationLock.WaitAsync();
            await action();
        }
        finally
        {
            _arenaOperationLock.Release();
            if (ownsBusyState)
            {
                SetArenaBusy(false, ArenaRunStatus.Text, stopEnabled: false);
            }
            else if (runsDuringAutoChat)
            {
                if (operationButton is not null)
                {
                    operationButton.IsEnabled = true;
                }

                SetBreathingOperationButton(_autoChatCancellation is not null ? AutoChatButton : null);
            }
        }
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled)
    {
        SetArenaBusy(busy, status, stopEnabled, null);
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled, Button? operationButton)
    {
        _arenaBusy = busy;
        SetBreathingOperationButton(busy ? operationButton : null);
        SetButtonBreathing(StopButton, busy && stopEnabled);
        var autoChatRunning = _autoChatCancellation is not null;
        AutoChatButton.IsEnabled = !busy;
        OneTurnButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        ScenarioWorkflow.UpdateBusyState(busy, autoChatRunning);
        InternetWorkflow.UpdateBusyState(busy, autoChatRunning);
        NarrateNowButton.IsEnabled = !busy || autoChatRunning;
        StopButton.IsEnabled = stopEnabled;
        if (busy && operationButton is not null)
        {
            operationButton.IsEnabled = true;
        }
        OperatorTurn.UpdateBusyState();
        TestProviderButton.IsEnabled = !busy;
        PreloadSelectedModelsButton.IsEnabled = !busy;
        ApplySettingsButton.IsEnabled = !busy;
        ActiveParticipantsPicker.IsEnabled = !busy;
        ProviderBaseUrlText.IsEnabled = !busy;
        ProviderModelText.IsEnabled = !busy;
        AlphaRoleModelText.IsEnabled = !busy;
        BetaRoleModelText.IsEnabled = !busy;
        GammaRoleModelText.IsEnabled = !busy;
        DeltaRoleModelText.IsEnabled = !busy;
        NarratorRoleModelText.IsEnabled = !busy;
        ProviderTimeoutText.IsEnabled = !busy;
        ProviderTemperatureText.IsEnabled = !busy;
        ProviderMaxOutputText.IsEnabled = !busy;
        ContextTranscriptWindowText.IsEnabled = !busy;
        ContextPrivateWindowText.IsEnabled = !busy;
        ContextNotesWindowText.IsEnabled = !busy;
        AutoChatCadencePicker.IsEnabled = !busy;
        AvatarStylePicker.IsEnabled = !busy;
        SystemGlyphStylePicker.IsEnabled = !busy;
        TopStripModePicker.IsEnabled = !busy;
        DebugControlsCheckBox.IsEnabled = !busy;
        VoiceDriftEnforcementCheckBox.IsEnabled = !busy;
        SavedStateModePicker.IsEnabled = !busy;
        SavedStateNameText.IsEnabled = !busy;
        SavedStateItemPicker.IsEnabled = !busy;
        SavedStateSaveButton.IsEnabled = !busy;
        SavedStateCoordinator.UpdateActionButtons();
        foreach (var button in _agentTurnButtons)
        {
            button.IsEnabled = !busy;
        }
        foreach (var button in _narratorActionButtons)
        {
            button.IsEnabled = !busy || autoChatRunning;
        }
        foreach (var button in _transcriptActionButtons)
        {
            button.IsEnabled = !busy && button.Tag is true;
        }
        foreach (var checkBox in _lockControls)
        {
            checkBox.IsEnabled = !busy;
        }
        foreach (var comboBox in _voiceControls)
        {
            comboBox.IsEnabled = !busy;
        }
        foreach (var comboBox in _pressureControls)
        {
            comboBox.IsEnabled = !busy;
        }
        ArenaRunStatus.Text = status;
    }

    private void SetBreathingOperationButton(Button? button)
    {
        if (_breathingOperationButton == button)
        {
            return;
        }

        if (_breathingOperationButton is not null)
        {
            SetButtonBreathing(_breathingOperationButton, false);
        }

        _breathingOperationButton = button;
        if (_breathingOperationButton is not null)
        {
            SetButtonBreathing(_breathingOperationButton, true);
        }
    }

    private static void SetButtonBreathing(Button button, bool breathing)
    {
        if (!breathing)
        {
            if (button.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            if (button.Effect is DropShadowEffect glow && !glow.IsFrozen)
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            }

            button.Effect = null;
            return;
        }

        var scaleTransform = new ScaleTransform(1, 1);
        button.RenderTransform = scaleTransform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var borderColor = button.BorderBrush is SolidColorBrush borderBrush
            ? borderBrush.Color
            : Colors.White;
        var glowEffect = new DropShadowEffect
        {
            Color = borderColor,
            Direction = 0,
            ShadowDepth = 0,
            BlurRadius = 9,
            Opacity = 0.2
        };
        button.Effect = glowEffect;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var scaleAnimation = new DoubleAnimation(1, 1.025, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var glowAnimation = new DoubleAnimation(0.18, 0.62, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var blurAnimation = new DoubleAnimation(8, 15, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, glowAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnimation);
    }

    private async void RefreshActiveSession(string status)
    {
        await RefreshActiveSessionAsync(status);
    }

    private async Task RefreshActiveSessionAsync(string status)
    {
        if (_activeSession is null)
        {
            return;
        }

        var latest = (await _coreSessionStore.ListSessionsAsync()).FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latest is not null)
        {
            await LoadSessionAsync(latest, force: true);
        }

        LoadStatus.Text = status;
        ArenaRunStatus.Text = status;
    }

    private async void MatchLockChanged(object sender, RoutedEventArgs e)
    {
        if (_arenaBusy || _activeSession is null || sender is not CheckBox checkBox || checkBox.Tag is not string key)
        {
            return;
        }

        var locked = checkBox.IsChecked == true;
        await RunArenaBusyAsync($"Updating {key} lock...", async () =>
        {
            await _matchGeneration.ToggleLockAsync(_activeSession.Id, key, locked);
            RefreshActiveSession($"{key} lock {(locked ? "enabled" : "disabled")}.");
        });
    }

    private async void EditLockCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_arenaBusy || _activeSession is null || sender is not Button button || button.Tag is not string key)
        {
            return;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
            ArenaRunStatus.Text = LoadStatus.Text;
            return;
        }

        var current = CurrentMatchText(snapshot, key);
        var edited = TextEditDialog.Show(this, _theme, $"Edit {DisplayLockKey(key)}", current);
        if (edited is null)
        {
            LoadStatus.Text = "Edit cancelled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(edited))
        {
            LoadStatus.Text = "Edit text is empty.";
            ArenaRunStatus.Text = LoadStatus.Text;
            return;
        }

        await RunArenaBusyAsync($"Updating {key}...", async () =>
        {
            var latest = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (latest is null)
            {
                LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            if (!ApplyMatchTextEdit(latest, key, edited))
            {
                LoadStatus.Text = $"Could not edit {key}.";
                ArenaRunStatus.Text = LoadStatus.Text;
                return;
            }

            latest.MatchLocks[NormalizeMatchLockKey(key)] = true;
            await SaveSnapshotWithFeedbackAsync(latest, _activeSession.Id);
            var normalizedKey = NormalizeMatchLockKey(key);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_match_text_edited", new
            {
                key = normalizedKey,
                locked = true
            });
            RefreshActiveSession($"Updated and locked {DisplayLockKey(key)}.");
        });
    }

    private static string CurrentMatchText(AIArena.Core.Models.ArenaSnapshot snapshot, string key)
    {
        return NormalizeMatchLockKey(key) switch
        {
            "topic" => snapshot.Engine.Steering.Topic,
            "global" => snapshot.Engine.Steering.Global,
            var agentId when AgentRosterService.IsParticipantId(agentId) || agentId == "narrator" =>
                snapshot.Engine.Agents.FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))?.Persona
                ?? (agentId == "narrator" ? snapshot.Engine.Narrator.Persona : ""),
            _ => ""
        };
    }

    private static bool ApplyMatchTextEdit(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeMatchLockKey(key);
        switch (normalizedKey)
        {
            case "topic":
                snapshot.Engine.Steering.Topic = value;
                return true;
            case "global":
                snapshot.Engine.Steering.Global = value;
                return true;
            case "narrator":
                snapshot.Engine.Narrator.Persona = value;
                return true;
            case var agentId when AgentRosterService.IsParticipantId(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.Persona = value;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyVoiceStyle(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeMatchLockKey(key);
        var normalizedVoice = NormalizeVoiceStyleTag(value);
        switch (normalizedKey)
        {
            case "narrator":
                snapshot.Engine.Narrator.VoiceStyle = normalizedVoice == "default" ? "" : normalizedVoice;
                return true;
            case var agentId when AgentRosterService.IsParticipantId(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.VoiceStyle = normalizedVoice == "default" ? "" : normalizedVoice;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyAgentPressure(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeMatchLockKey(key);
        var normalizedPressure = NormalizeAgentPressureTag(value);
        switch (normalizedKey)
        {
            case var agentId when AgentRosterService.IsParticipantId(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.PressureProfile = normalizedPressure == "default" ? "" : normalizedPressure;
                return true;
            default:
                return false;
        }
    }

    private void DiagnosticDetailCloseButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsWorkflow.CloseDetail();
    }
    private static string NormalizeMatchLockKey(string key)
    {
        var cleaned = string.IsNullOrWhiteSpace(key) ? "" : key.Trim().ToLowerInvariant();
        return AgentRosterService.IsParticipantId(cleaned) || cleaned is "narrator" or "topic" or "global" or "scenario"
            ? cleaned
            : "scenario";
    }

    private static string DisplayLockKey(string key)
    {
        return NormalizeMatchLockKey(key) switch
        {
            "topic" => "Topic",
            "global" => "Global",
            var agentId when AgentRosterService.IsParticipantId(agentId) => AgentRosterService.DisplayName(agentId),
            "narrator" => "Narrator",
            _ => "Scenario"
        };
    }

    private TimeSpan AutoChatCadence()
    {
        var value = (AutoChatCadencePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromMilliseconds(Math.Clamp(seconds, 0.1, 30) * 1000)
            : TimeSpan.FromMilliseconds(1200);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingTheme)
        {
            return;
        }

        var themeId = ThemePicker.SelectedItem is ThemePalette selectedTheme
            ? selectedTheme.Id
            : ThemePalette.NormalizeId(ThemePicker.SelectedValue?.ToString());
        ApplyTheme(themeId, persist: true, rerender: true);
    }

    private void ApplyTheme(string themeId, bool persist, bool rerender)
    {
        _theme = ThemePalette.Resolve(themeId);
        SetBrush("AppBackgroundBrush", _theme.AppBackground);
        SetBrush("TopBarBrush", _theme.TopBar);
        SetBrush("PanelBrush", _theme.Panel);
        SetBrush("CardBrush", _theme.Card);
        SetBrush("InputBrush", _theme.Input);
        SetBrush("TranscriptHeaderBrush", _theme.Panel);
        SetBrush("TranscriptBodyBrush", _theme.Card);
        SetBrush("ControlBorderBrush", _theme.Border);
        SetBrush("TextBrush", _theme.Text);
        SetBrush("MutedTextBrush", _theme.MutedText);
        SetBrush("PrimaryBrush", _theme.Primary);
        SetBrush("PrimaryBorderBrush", _theme.PrimaryBorder);
        SetBrush("AssistBrush", _theme.Assist);
        SetBrush("AssistBorderBrush", _theme.AssistBorder);
        SetBrush("DangerBrush", _theme.Danger);
        SetBrush("DangerBorderBrush", _theme.DangerBorder);
        SetBrush("DangerTextBrush", _theme.DangerText);
        SetBrush("DisabledBrush", _theme.Disabled);
        SetBrush("DisabledBorderBrush", _theme.DisabledBorder);
        SetBrush("DisabledTextBrush", _theme.DisabledText);
        SetBrush("HoverBorderBrush", _theme.HoverBorder);
        SetBrush("NavHoverBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.18));
        SetBrush("NavActiveBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.24));
        SetBrush("NavPressedBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.12));
        SetBrush("PressedPrimaryBrush", _theme.PressedPrimary);
        SetBrush("OverlayBrush", _theme.Overlay);
        SetBrush("AlphaAccentBrush", _theme.AlphaAccent);
        SetBrush("BetaAccentBrush", _theme.BetaAccent);
        SetBrush("GammaAccentBrush", _theme.GammaAccent);
        SetBrush("DeltaAccentBrush", _theme.DeltaAccent);
        SetBrush("NarratorAccentBrush", _theme.NarratorAccent);
        SetBrush("OperatorAccentBrush", _theme.OperatorAccent);

        if (persist)
        {
            _wpfSettings.ThemeId = _theme.Id;
            _wpfSettingsStore.Save(_wpfSettings);
        }

        UpdateNavigationTheme();
        _userGuideWindowHost.RefreshTheme(this);
        if (rerender && _activeSession is not null)
        {
            RefreshActiveSession($"Theme applied: {_theme.Name}");
        }
    }

    private void SetBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private void SetBrush(string key, Brush brush)
    {
        Resources[key] = brush;
    }

    private Brush ResourceBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.White;
    }

    private static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        var baseColor = BrushColor(baseBrush, Colors.Transparent);
        var accentColor = BrushColor(accentBrush, baseColor);
        var amount = Math.Clamp(accentAmount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            BlendChannel(baseColor.R, accentColor.R, amount),
            BlendChannel(baseColor.G, accentColor.G, amount),
            BlendChannel(baseColor.B, accentColor.B, amount)));
    }

    private static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static byte BlendChannel(byte baseline, byte accent, double amount)
    {
        return (byte)Math.Round(baseline + ((accent - baseline) * amount));
    }

    private Brush AccentForSpeaker(string speaker)
    {
        return speaker.ToLowerInvariant() switch
        {
            "alpha" => ResourceBrush("AlphaAccentBrush"),
            "beta" => ResourceBrush("BetaAccentBrush"),
            "gamma" => ResourceBrush("GammaAccentBrush"),
            "delta" => ResourceBrush("DeltaAccentBrush"),
            "epsilon" => ResourceBrush("AssistBorderBrush"),
            "zeta" => ResourceBrush("PrimaryBorderBrush"),
            "eta" => BlendBrush(ResourceBrush("GammaAccentBrush"), ResourceBrush("AlphaAccentBrush"), 0.35),
            "theta" => BlendBrush(ResourceBrush("NarratorAccentBrush"), ResourceBrush("DeltaAccentBrush"), 0.35),
            "narrator" => ResourceBrush("NarratorAccentBrush"),
            "operator" => ResourceBrush("OperatorAccentBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private void ArenaNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTranscriptPanel(clearFilters: false);
    }

    private void CustomMatchNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCustomMatchPanel();
    }

    private void SessionOverviewMatch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowCustomMatchPanel();
        e.Handled = true;
    }

    private void SessionOverviewTurns_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowTranscriptPanel(clearFilters: true);
        e.Handled = true;
    }

    private void SessionOverviewPerformance_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AgentPerformanceCard.BringIntoView();
        Dispatcher.BeginInvoke(() => AgentPerformanceCard.BringIntoView(), DispatcherPriority.Background);
        e.Handled = true;
    }

    private void SessionOverviewProvider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenModelProviderSettings();
        e.Handled = true;
    }

    private void ShowTranscriptPanel(bool clearFilters)
    {
        TranscriptPanel.Visibility = Visibility.Visible;
        CustomMatchPanel.Visibility = Visibility.Collapsed;
        NewsPanel.Visibility = Visibility.Collapsed;
        UpdateNavigationTheme();

        if (clearFilters)
        {
            ClearTranscriptFilters();
        }
    }

    private void ShowCustomMatchPanel()
    {
        TranscriptPanel.Visibility = Visibility.Collapsed;
        CustomMatchPanel.Visibility = Visibility.Visible;
        NewsPanel.Visibility = Visibility.Collapsed;
        UpdateNavigationTheme();
    }

    private void ClearTranscriptFilters()
    {
        _isRenderingSnapshot = true;
        try
        {
            TranscriptInsight.ClearTimelineTurnFilter(refresh: false);
            TranscriptSearch.ClearFilters();
        }
        finally
        {
            _isRenderingSnapshot = false;
        }

        PopulateTranscript(_lastRenderedMessages);
        Dispatcher.BeginInvoke(() => TranscriptScrollViewer.ScrollToTop(), DispatcherPriority.Background);
    }

    private void TranscriptFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_transcriptSearchCoordinator is null || TranscriptItems is null)
        {
            return;
        }

        _transcriptSearchCoordinator.OnFilterChanged();
    }

    private void ClearTranscriptSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptSearchCoordinator?.ClearSearch();
    }

    private void TranscriptSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptSearchCoordinator?.ShowSearch();
    }

    private void TopProviderValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        UpdateProviderHealthPopup();
        ProviderHealthPopup.IsOpen = true;
        e.Handled = true;
    }

    private void ProviderHealthCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderHealthPopup.IsOpen = false;
    }

    private async void ProviderHealthTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.TestProviderAsync(ProviderHealthTestButton);
        }

        UpdateProviderHealthPopup();
    }

    private async void ProviderHealthRefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(ProviderHealthRefreshModelsButton, async () =>
        {
            ProviderHealthStatusText.Text = "Refreshing advertised models...";
            await RefreshAdvertisedModelsAsync(force: true);
        });
        UpdateProviderHealthPopup();
    }

    private void ProviderHealthSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderHealthPopup.IsOpen = false;
        OpenModelProviderSettings();
    }

    private void UpdateProviderHealthPopup(ArenaViewSnapshot? snapshot = null)
    {
        if (ProviderHealthStatusText is null)
        {
            return;
        }

        snapshot ??= _lastRenderedSnapshot;
        var online = snapshot?.ProviderOnline == true;
        var baseUrl = snapshot?.ProviderBaseUrl ?? ProviderBaseUrlText?.Text?.Trim() ?? "-";
        var model = snapshot?.ProviderModel ?? ProviderModelText?.Text?.Trim() ?? "-";
        var error = snapshot?.ProviderLastError ?? "";
        var advertisedModels = _providerSettingsCoordinator?.AdvertisedModels ?? [];
        var modelCount = advertisedModels.Count > 0
            ? advertisedModels.Count
            : _providerSettingsCoordinator?.LastProviderModelCount ?? -1;
        var checkedAt = _providerSettingsCoordinator?.LastProviderHealthCheckedAt
            ?? _providerSettingsCoordinator?.LastModelListCheckedAt;

        ProviderHealthStatusText.Text = online ? "ONLINE" : "OFFLINE";
        ProviderHealthStatusText.Foreground = online ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DangerTextBrush");
        ProviderHealthBaseUrlText.Text = string.IsNullOrWhiteSpace(baseUrl) ? "-" : baseUrl;
        ProviderHealthModelCountText.Text = modelCount >= 0 ? modelCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        ProviderHealthDefaultModelText.Text = string.IsNullOrWhiteSpace(model) || model == "-" ? "not selected" : model;
        ProviderHealthLastCheckText.Text = checkedAt is null
            ? "waiting"
            : checkedAt.Value.ToLocalTime().ToString("h:mm:ss tt", System.Globalization.CultureInfo.CurrentCulture);
        ProviderHealthLastErrorText.Text = string.IsNullOrWhiteSpace(error)
            ? "No provider error recorded."
            : $"Last error: {error}";
        ProviderHealthLastErrorText.Foreground = string.IsNullOrWhiteSpace(error)
            ? ResourceBrush("MutedTextBrush")
            : ResourceBrush("DangerTextBrush");

        var missingModel = advertisedModels.Count > 0
            && !string.IsNullOrWhiteSpace(model)
            && model != "-"
            && !advertisedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
        ProviderHealthModelWarning.Visibility = missingModel ? Visibility.Visible : Visibility.Collapsed;
        ProviderHealthModelWarningText.Text = missingModel
            ? $"Selected default model '{model}' is not in the advertised model list. Open settings to reselect or type it manually."
            : "";
    }

    private void TranscriptSearchText_KeyDown(object sender, KeyEventArgs e)
    {
        _transcriptSearchCoordinator?.OnSearchKeyDown(e);
    }

    private void AgentPerformanceDetailCloseButton_Click(object sender, RoutedEventArgs e)
    {
        AgentPerformance.CloseDetail();
    }

    private void TranscriptSearchDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _transcriptSearchCoordinator?.OnDragMouseLeftButtonDown(e);
    }

    private void TranscriptSearchDragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        _transcriptSearchCoordinator?.OnDragMouseMove(e);
    }

    private void TranscriptSearchDragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _transcriptSearchCoordinator?.OnDragMouseLeftButtonUp(e);
    }

    private void UpdateNavigationTheme()
    {
        var arenaActive = TranscriptPanel.Visibility == Visibility.Visible;
        var customActive = CustomMatchPanel.Visibility == Visibility.Visible;
        ApplyNavigationButtonState(ArenaNavButton, arenaActive);
        ApplyNavigationButtonState(CustomMatchNavButton, customActive);
    }

    private void ApplyNavigationButtonState(Button button, bool active)
    {
        button.Background = active
            ? ResourceBrush("NavActiveBrush")
            : Brushes.Transparent;
        button.BorderBrush = active ? ResourceBrush("PrimaryBorderBrush") : Brushes.Transparent;
        button.Foreground = active ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush");
    }

    private void AppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AnimateSettingsGear();
        SetAppSettingsVisible(AppSettingsPanel.Visibility != Visibility.Visible);
    }

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ReleasesUrl,
            UseShellExecute = true
        });
    }

    private void OpenUserGuideButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_userGuideWindowHost.Show(this))
        {
            LoadStatus.Text = "User guide not found.";
        }
    }

    private void OpenModelProviderSettings(string? baseUrl = null, string? model = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            ProviderBaseUrlText.Text = baseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            ProviderModelText.Text = model.Trim();
        }

        SetAppSettingsVisible(true);
        ModelProviderSettingsExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(() =>
        {
            ModelProviderSettingsExpander.BringIntoView();
            if (string.IsNullOrWhiteSpace(ProviderModelText.Text))
            {
                ProviderModelText.Focus();
            }
            else
            {
                TestProviderButton.Focus();
            }
        }, DispatcherPriority.Background);
    }

    private void CloseAppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetAppSettingsVisible(false);
    }

    private void VisualSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot
            || AvatarStylePicker is null
            || SystemGlyphStylePicker is null
            || TopStripModePicker is null
            || DebugControlsCheckBox is null)
        {
            return;
        }

        var avatarStyle = (AvatarStylePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pack";
        _wpfSettings.AvatarStyle = avatarStyle;
        _wpfSettings.ChampionAvatars = avatarStyle is "pack" or "procedural";
        _wpfSettings.SystemEventGlyphs = (SystemGlyphStylePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "fallback";
        _wpfSettings.TopStripMode = (TopStripModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "diagnostics";
        _wpfSettings.ShowTranscriptDiagnostics = _wpfSettings.TopStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        _wpfSettings.AllowDebugControls = DebugControlsCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        UpdateDebugControlsVisibility();
        UpdateTranscriptDashboardLayout(TranscriptDashboardGrid.ActualWidth, force: true);
        TelemetryWorkflow.UpdateTimerState();
        UpdateViewPresetState();
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
    }

    private string CurrentAvatarStyle()
    {
        var style = _wpfSettings.AvatarStyle?.Trim().ToLowerInvariant();
        return style switch
        {
            "pack" or "procedural" or "simple" or "initials" => style,
            "champion" => "procedural",
            _ => _wpfSettings.ChampionAvatars ? "pack" : "simple"
        };
    }

    private string CurrentTopStripMode()
    {
        var mode = _wpfSettings.TopStripMode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "diagnostics" or "telemetry" or "hidden" => mode,
            _ => _wpfSettings.ShowTranscriptDiagnostics ? "diagnostics" : "hidden"
        };
    }

    private async void ProviderBaseUrlText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ProviderBaseUrlCommittedAsync();
        }
    }

    private async void ProviderBaseUrlText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ProviderBaseUrlCommittedAsync();
        }
    }

    private async void ProviderModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ProviderModelSelectionChangedAsync();
        }
    }

    private async void ProviderModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ProviderModelCommittedAsync();
        }
    }

    private async void ProviderModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_providerSettingsCoordinator is not null)
        {
            await _providerSettingsCoordinator.ProviderModelCommittedAsync();
        }
    }

    private async void ParticipantModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null && sender is ComboBox comboBox)
        {
            await _providerSettingsCoordinator.ParticipantModelSelectionChangedAsync(comboBox);
        }
    }

    private async void ParticipantModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_providerSettingsCoordinator is not null && sender is ComboBox comboBox)
        {
            await _providerSettingsCoordinator.ParticipantModelCommittedAsync(comboBox);
        }
    }

    private async void ParticipantModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _providerSettingsCoordinator is null || sender is not ComboBox comboBox)
        {
            return;
        }

        e.Handled = true;
        await _providerSettingsCoordinator.ParticipantModelCommittedAsync(comboBox);
    }
    private void SetAppSettingsVisible(bool visible)
    {
        AppSettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppSettingsButton.ToolTip = visible ? "Hide Settings" : "App Settings";
        if (visible)
        {
            _modelRefreshTimer.Start();
            _ = RefreshAdvertisedModelsAsync(force: true);
        }
        else
        {
            _modelRefreshTimer.Stop();
        }
    }

    private void AnimateSettingsGear()
    {
        var animation = new DoubleAnimation(
            SettingsGearRotate.Angle,
            SettingsGearRotate.Angle + 120,
            TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        SettingsGearRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private static bool IsAgentSpeaker(string speakerId)
    {
        return AgentRosterService.IsParticipantId(speakerId);
    }


    private void SelectAgentCountControls(int activeCount)
    {
        if (AgentCountPicker is null || AgentCountPresetPicker is null)
        {
            return;
        }

        var count = Math.Clamp(activeCount, AgentRosterService.MinParticipants, AgentRosterService.MaxParticipants);
        ShellUiHelpers.SelectComboTag(AgentCountPicker, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var preset = count switch
        {
            2 => "2",
            4 => "4",
            6 => "6",
            8 => "8",
            _ => "custom"
        };
        ShellUiHelpers.SelectComboTag(AgentCountPresetPicker, preset);
        if (AgentRosterStatusText is not null)
        {
            AgentRosterStatusText.Text = count switch
            {
                1 => "Solo",
                2 => "Duel",
                4 => "Classic",
                6 => "Council",
                8 => "Swarm",
                _ => "Custom"
            };
            AgentRosterStatusText.ToolTip = $"{count} active AI agents in this session";
        }
    }

    private int SelectedAgentCount()
    {
        var selected = (AgentCountPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, AgentRosterService.MinParticipants, AgentRosterService.MaxParticipants)
            : 4;
    }

    private int ParseActiveParticipants()
    {
        var selected = (ActiveParticipantsPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, 1, AgentRosterService.MaxParticipants)
            : 3;
    }

    private static void EnsureDeltaAgent(AIArena.Core.Models.ArenaSnapshot snapshot)
    {
        if (snapshot.Engine.Agents.Any(agent => agent.Id.Equals("delta", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var delta = new AIArena.Core.Models.DialogueAgent
        {
            Id = "delta",
            Name = "Delta: Boundary tester",
            Persona = "Boundary tester. Thinking style: identifies limits, misuse cases, escalation paths, and operational failure boundaries. Temperament: calm and exacting. Priority/bias: make constraints explicit before conclusions are accepted. Blind spot: may over-index on edge cases and slow convergence.",
            Active = false,
            Status = "waiting"
        };
        var insertAt = snapshot.Engine.Agents.FindIndex(agent => agent.Id.Equals("gamma", StringComparison.OrdinalIgnoreCase));
        if (insertAt >= 0 && insertAt < snapshot.Engine.Agents.Count - 1)
        {
            snapshot.Engine.Agents.Insert(insertAt + 1, delta);
        }
        else
        {
            snapshot.Engine.Agents.Add(delta);
        }
    }

    private Task RefreshAdvertisedModelsAsync(bool force = false)
    {
        return _providerSettingsCoordinator?.RefreshAdvertisedModelsAsync(force) ?? Task.CompletedTask;
    }
    private static string ShortModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "-";
        }

        var trimmed = model.Trim();
        return trimmed.Length <= 28 ? trimmed : string.Concat(trimmed.AsSpan(0, 25), "...");
    }

    private static string DisplayInternetMode(string mode)
    {
        return string.IsNullOrWhiteSpace(mode) ? "manual" : mode.Replace('_', ' ');
    }

    private static string DisplayInternetScope(string scope)
    {
        return scope.Equals("open_web", StringComparison.OrdinalIgnoreCase) ? "open web" : "trusted sources";
    }

    private static string InternetModeDescription(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "off" => "all internet fetches blocked",
            "model_requested" => "approved model tool requests only",
            "auto" => "manual buttons plus automatic/model-requested fetches",
            _ => "manual user actions only"
        };
    }

    private void ApplyNativeChromeColor()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkGray = ColorRef(0x20, 0x20, 0x20);
        var borderGray = ColorRef(0x58, 0x58, 0x58);
        var white = ColorRef(0xFF, 0xFF, 0xFF);
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.BorderColor, ref borderGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref darkGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref white, Marshal.SizeOf<int>());
    }

    private static int ColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int attributeValue, int attributeSize);

    private enum DwmWindowAttribute
    {
        BorderColor = 34,
        CaptionColor = 35,
        TextColor = 36
    }

}
