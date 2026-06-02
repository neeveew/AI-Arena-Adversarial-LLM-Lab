using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using CoreVoiceAdherenceDiagnostic = AIArena.Core.Models.VoiceAdherenceDiagnostic;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
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
    private readonly ShellCardFactory? _shellCardFactory;
    private readonly SavedStateWorkflowCoordinator? _savedStateCoordinator;
    private readonly TranscriptExportCoordinator? _transcriptExportCoordinator;
    private readonly TranscriptSearchCoordinator? _transcriptSearchCoordinator;
    private readonly TranscriptInsightCoordinator? _transcriptInsightCoordinator;
    private readonly TranscriptActionCoordinator? _transcriptActionCoordinator;
    private readonly TranscriptMutationCoordinator? _transcriptMutationCoordinator;
    private readonly TranscriptListCoordinator? _transcriptListCoordinator;
    private readonly TranscriptCardRenderer? _transcriptCardRenderer;
    private readonly TranscriptAdjunctCoordinator? _transcriptAdjunctCoordinator;
    private readonly NewsPanelCoordinator? _newsPanelCoordinator;
    private readonly AgentMemoryCoordinator? _agentMemoryCoordinator;
    private readonly ScenarioWorkflowCoordinator? _scenarioWorkflowCoordinator;
    private readonly OperatorTurnCoordinator? _operatorTurnCoordinator;
    private readonly InternetWorkflowCoordinator? _internetWorkflowCoordinator;
    private readonly ArenaRunCoordinator? _arenaRunCoordinator;
    private readonly ProviderSettingsCoordinator? _providerSettingsCoordinator;
    private readonly ProviderQuickSetupCoordinator? _providerQuickSetupCoordinator;
    private readonly ProviderReachabilityCoordinator? _providerReachabilityCoordinator;
    private readonly TranscriptViewCoordinator? _transcriptViewCoordinator;
    private readonly TelemetryWorkflowCoordinator? _telemetryWorkflowCoordinator;
    private readonly AgentPerformanceCoordinator? _agentPerformanceCoordinator;
    private readonly SessionOverviewCoordinator? _sessionOverviewCoordinator;
    private readonly DiagnosticsWorkflowCoordinator? _diagnosticsWorkflowCoordinator;
    private readonly MatchSetupCoordinator? _matchSetupCoordinator;
    private readonly MatchLockCoordinator? _matchLockCoordinator;
    private readonly CustomMatchSummaryCoordinator? _customMatchSummaryCoordinator;
    private readonly ScenarioSeedInspectorCoordinator? _scenarioSeedInspectorCoordinator;
    private readonly AgentRosterCoordinator? _agentRosterCoordinator;
    private readonly ArenaSessionMutationCoordinator? _arenaSessionMutationCoordinator;
    private readonly ShellNavigationCoordinator? _shellNavigationCoordinator;
    private readonly MatchQualityTimelineCoordinator? _matchQualityTimelineCoordinator;
    private readonly AgentBoardCoordinator? _agentBoardCoordinator;
    private readonly ArenaOperationCoordinator? _arenaOperationCoordinator;
    private readonly AppSettingsCoordinator? _appSettingsCoordinator;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _modelRefreshTimer;
    private readonly DispatcherTimer _providerHealthTimer;
    private readonly SemaphoreSlim _arenaOperationLock = new(1, 1);
    private IReadOnlyList<TranscriptMessage> _lastRenderedMessages = [];
    private IReadOnlyDictionary<string, string> _lastAgentPersonas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private CoreSessionSummary? _activeSession;
    private DateTimeOffset _activeSnapshotWriteUtc;
    private bool _isRenderingSnapshot;
    private bool _arenaBusy;
    private WpfSettings _wpfSettings = new();
    private ThemePalette _theme = ThemePalette.Resolve("system");
    private ArenaViewSnapshot? _lastRenderedSnapshot;

    private SavedStateWorkflowCoordinator SavedStateCoordinator =>
        _savedStateCoordinator ?? throw new InvalidOperationException("Saved-state coordinator is not initialized.");

    private ShellCardFactory ShellCards =>
        _shellCardFactory ?? throw new InvalidOperationException("Shell card factory is not initialized.");

    private TranscriptExportCoordinator TranscriptExportCoordinator =>
        _transcriptExportCoordinator ?? throw new InvalidOperationException("Transcript export coordinator is not initialized.");

    private TranscriptSearchCoordinator TranscriptSearch =>
        _transcriptSearchCoordinator ?? throw new InvalidOperationException("Transcript search coordinator is not initialized.");

    private TranscriptInsightCoordinator TranscriptInsight =>
        _transcriptInsightCoordinator ?? throw new InvalidOperationException("Transcript insight coordinator is not initialized.");

    private TranscriptActionCoordinator TranscriptActions =>
        _transcriptActionCoordinator ?? throw new InvalidOperationException("Transcript action coordinator is not initialized.");

    private TranscriptMutationCoordinator TranscriptMutations =>
        _transcriptMutationCoordinator ?? throw new InvalidOperationException("Transcript mutation coordinator is not initialized.");

    private TranscriptListCoordinator TranscriptList =>
        _transcriptListCoordinator ?? throw new InvalidOperationException("Transcript list coordinator is not initialized.");

    private TranscriptCardRenderer TranscriptCards =>
        _transcriptCardRenderer ?? throw new InvalidOperationException("Transcript card renderer is not initialized.");

    private TranscriptAdjunctCoordinator TranscriptAdjunct =>
        _transcriptAdjunctCoordinator ?? throw new InvalidOperationException("Transcript adjunct coordinator is not initialized.");

    private NewsPanelCoordinator NewsPanelWorkflow =>
        _newsPanelCoordinator ?? throw new InvalidOperationException("News panel coordinator is not initialized.");

    private AgentMemoryCoordinator AgentMemory =>
        _agentMemoryCoordinator ?? throw new InvalidOperationException("Agent memory coordinator is not initialized.");

    private ScenarioWorkflowCoordinator ScenarioWorkflow =>
        _scenarioWorkflowCoordinator ?? throw new InvalidOperationException("Scenario workflow coordinator is not initialized.");

    private OperatorTurnCoordinator OperatorTurn =>
        _operatorTurnCoordinator ?? throw new InvalidOperationException("Operator turn coordinator is not initialized.");

    private InternetWorkflowCoordinator InternetWorkflow =>
        _internetWorkflowCoordinator ?? throw new InvalidOperationException("Internet workflow coordinator is not initialized.");

    private ArenaRunCoordinator ArenaRun =>
        _arenaRunCoordinator ?? throw new InvalidOperationException("Arena run coordinator is not initialized.");

    private ProviderSettingsCoordinator ProviderSettings =>
        _providerSettingsCoordinator ?? throw new InvalidOperationException("Provider settings coordinator is not initialized.");

    private ProviderQuickSetupCoordinator ProviderQuickSetup =>
        _providerQuickSetupCoordinator ?? throw new InvalidOperationException("Provider quick setup coordinator is not initialized.");

    private ProviderReachabilityCoordinator ProviderReachability =>
        _providerReachabilityCoordinator ?? throw new InvalidOperationException("Provider reachability coordinator is not initialized.");

    private TranscriptViewCoordinator TranscriptView =>
        _transcriptViewCoordinator ?? throw new InvalidOperationException("Transcript view coordinator is not initialized.");

    private TelemetryWorkflowCoordinator TelemetryWorkflow =>
        _telemetryWorkflowCoordinator ?? throw new InvalidOperationException("Telemetry workflow coordinator is not initialized.");

    private AgentPerformanceCoordinator AgentPerformance =>
        _agentPerformanceCoordinator ?? throw new InvalidOperationException("Agent performance coordinator is not initialized.");

    private SessionOverviewCoordinator SessionOverview =>
        _sessionOverviewCoordinator ?? throw new InvalidOperationException("Session overview coordinator is not initialized.");

    private DiagnosticsWorkflowCoordinator DiagnosticsWorkflow =>
        _diagnosticsWorkflowCoordinator ?? throw new InvalidOperationException("Diagnostics workflow coordinator is not initialized.");

    private MatchSetupCoordinator MatchSetup =>
        _matchSetupCoordinator ?? throw new InvalidOperationException("Match setup coordinator is not initialized.");

    private MatchLockCoordinator MatchLock =>
        _matchLockCoordinator ?? throw new InvalidOperationException("Match lock coordinator is not initialized.");

    private CustomMatchSummaryCoordinator CustomMatchSummary =>
        _customMatchSummaryCoordinator ?? throw new InvalidOperationException("Custom match summary coordinator is not initialized.");

    private ScenarioSeedInspectorCoordinator SeedInspector =>
        _scenarioSeedInspectorCoordinator ?? throw new InvalidOperationException("Scenario seed inspector coordinator is not initialized.");

    private AgentRosterCoordinator AgentRoster =>
        _agentRosterCoordinator ?? throw new InvalidOperationException("Agent roster coordinator is not initialized.");

    private ArenaSessionMutationCoordinator ArenaSessionMutations =>
        _arenaSessionMutationCoordinator ?? throw new InvalidOperationException("Arena session mutation coordinator is not initialized.");

    private ShellNavigationCoordinator ShellNavigation =>
        _shellNavigationCoordinator ?? throw new InvalidOperationException("Shell navigation coordinator is not initialized.");

    private MatchQualityTimelineCoordinator MatchQualityTimeline =>
        _matchQualityTimelineCoordinator ?? throw new InvalidOperationException("Match quality timeline coordinator is not initialized.");

    private AgentBoardCoordinator AgentBoard =>
        _agentBoardCoordinator ?? throw new InvalidOperationException("Agent board coordinator is not initialized.");

    private ArenaOperationCoordinator ArenaOperations =>
        _arenaOperationCoordinator ?? throw new InvalidOperationException("Arena operation coordinator is not initialized.");

    private AppSettingsCoordinator AppSettingsWorkflow =>
        _appSettingsCoordinator ?? throw new InvalidOperationException("App settings coordinator is not initialized.");

    public MainWindow()
    {
        InitializeComponent();
        _shellCardFactory = new ShellCardFactory(ResourceBrush, BlendBrush);
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
        _transcriptActionCoordinator = new TranscriptActionCoordinator(
            () => _wpfSettings.CompactTranscriptMode,
            () => _arenaBusy,
            ResourceBrush);
        _transcriptMutationCoordinator = new TranscriptMutationCoordinator(
            _coreSessionStore,
            _eventLogStore,
            _transcriptService,
            () => _activeSession,
            () => _arenaBusy,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetLoadStatus);
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
            () => ProviderReachability.LoadSharedConfigAsync(),
            (online, error, latencyMs, status) => ProviderReachability.PersistAsync(online, error, latencyMs, status),
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            force => ProviderReachability.RefreshAsync(force),
            () => ProviderReachability.UpdatePopup());
        _providerQuickSetupCoordinator = new ProviderQuickSetupCoordinator(
            TranscriptActions,
            () => ProviderSettings.AdvertisedModels,
            ResourceBrush,
            BlendBrush,
            (baseUrl, model, statusText) => ProviderSettings.SaveAndTestProviderQuickSetupAsync(baseUrl, model, statusText),
            OpenModelProviderSettings);
        _wpfSettings = _wpfSettingsStore.Load();
        _shellNavigationCoordinator = new ShellNavigationCoordinator(
            this,
            _wpfSettingsStore,
            () => _wpfSettings,
            ThemePicker,
            ArenaNavButton,
            CustomMatchNavButton,
            AppSettingsButton,
            TranscriptPanel,
            CustomMatchPanel,
            NewsPanel,
            AppSettingsPanel,
            theme => _theme = theme,
            ResourceBrush,
            () => _userGuideWindowHost.RefreshTheme(this),
            () => _activeSession is not null,
            RefreshActiveSession);
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
        _transcriptCardRenderer = new TranscriptCardRenderer(
            () => _wpfSettings.CompactTranscriptMode,
            TranscriptActions,
            ResourceBrush,
            BlendBrush,
            AccentForSpeaker,
            speakerId => _lastAgentPersonas.TryGetValue(speakerId, out var persona) ? persona : "",
            CurrentAvatarStyle,
            () => _wpfSettings.ChampionAvatars,
            () => _wpfSettings.SystemEventGlyphs,
            ShouldShowStyleFit,
            (style, text) => _voiceStyleAdherenceService.Analyze(style, text),
            diagnostic => VoiceAdherenceAccent(diagnostic),
            FormatDuration,
            FormatCompactNumber,
            TranscriptExportCoordinator.CopyMessage,
            TranscriptMutations.TogglePinMessageAsync,
            message => ArenaRun.RetryTranscriptMessageAsync(message),
            TranscriptMutations.DeleteMessageAsync,
            message => InternetWorkflow.ApproveInternetRequestAsync(message),
            message => InternetWorkflow.RejectInternetRequestAsync(message),
            TranscriptExportCoordinator.CopyInternetUrl,
            IsAgentSpeaker,
            () => _wpfSettings.TurnCompareMode,
            message => TranscriptInsight.IsTurnSelectedForCompare(message),
            TranscriptInsightCoordinator.CanCompareMessage,
            message => TranscriptInsight.ToggleTurnCompareMessage(message));
        _transcriptAdjunctCoordinator = new TranscriptAdjunctCoordinator(
            _discourseDiagnostics,
            _voiceStyleAdherenceService,
            TranscriptCards,
            () => _wpfSettings.CompactTranscriptMode,
            () => _lastAgentPersonas,
            () => TranscriptInsight.SelectedTurnCompareMessages,
            () => TranscriptInsight.HasTurnCompareSelection,
            ResourceBrush,
            BlendBrush,
            AccentForSpeaker,
            IsAgentSpeaker,
            DisplayStatusValue,
            ShouldShowStyleFit,
            diagnostic => VoiceAdherenceAccent(diagnostic),
            FormatCompactNumber,
            FormatDuration,
            TranscriptActions.CreateButton,
            () => PopulateTranscript(_lastRenderedMessages),
            visibleMessages => TranscriptInsight.ReselectLatest(visibleMessages),
            () => TranscriptInsight.ClearTurnCompareSelection(suppressAutoSeed: true, refresh: true),
            GenerateDecisionCardAsync);
        _newsPanelCoordinator = new NewsPanelCoordinator(
            NewsItems,
            NewsSummaryText,
            ShellCards,
            TranscriptAdjunct,
            ResourceBrush);
        _agentMemoryCoordinator = new AgentMemoryCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            () => _theme,
            () => _activeSession,
            () => _wpfSettings.CompactTranscriptMode,
            ResourceBrush,
            BlendBrush,
            AccentForSpeaker,
            ShortModelName,
            DisplayStatusValue,
            TranscriptActions.CreateButton,
            () => PopulateTranscript(_lastRenderedMessages),
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetArenaRunStatus);
        _arenaRunCoordinator = new ArenaRunCoordinator(
            _turnRunner,
            _narratorService,
            _arenaOperationLock,
            AutoChatButton,
            OneTurnButton,
            NarrateNowButton,
            () => _activeSession,
            () => _arenaBusy,
            ShouldEnforceVoiceDrift,
            AutoChatCadence,
            SetArenaBusy,
            RunArenaBusyForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetLoadStatus,
            SetArenaRunStatus,
            (message, resumeAutoChat) => InternetWorkflow.HandleInternetApprovalDialogAsync(message, resumeAutoChat),
            IsAgentSpeaker);
        _agentBoardCoordinator = new AgentBoardCoordinator(
            _coreSessionStore,
            _eventLogStore,
            AgentItems,
            () => _activeSession,
            () => _arenaBusy,
            () => ArenaRun.IsAutoChatRunning,
            ResourceBrush,
            BlendBrush,
            AccentForSpeaker,
            DisplayStatusValue,
            agent => ArenaRun.RunAgentTurnAsync(agent),
            NarrateNowButton_Click,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetArenaRunStatus);
        ShellNavigation.ApplyTheme(_wpfSettings.ThemeId, persist: false, rerender: false);
        ShellNavigation.InitializeThemePicker();
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
        _appSettingsCoordinator = new AppSettingsCoordinator(
            Dispatcher,
            ShellNavigation,
            _modelRefreshTimer,
            () => AppSettingsPanel.Visibility == Visibility.Visible,
            force => RefreshAdvertisedModelsAsync(force),
            ModelProviderSettingsExpander,
            ProviderBaseUrlText,
            ProviderModelText,
            TestProviderButton,
            SettingsGearRotate);
        _providerHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _providerHealthTimer.Tick += async (_, _) => await ProviderReachability.RefreshAsync();
        _providerReachabilityCoordinator = new ProviderReachabilityCoordinator(
            _coreSessionStore,
            _providerReachabilityService,
            _providerHealthTimer,
            ProviderHealthPopup,
            ProviderHealthStatusText,
            ProviderHealthBaseUrlText,
            ProviderHealthModelCountText,
            ProviderHealthDefaultModelText,
            ProviderHealthLastCheckText,
            ProviderHealthLastErrorText,
            ProviderHealthModelWarning,
            ProviderHealthModelWarningText,
            ProviderHealthTestButton,
            ProviderHealthRefreshModelsButton,
            ProviderBaseUrlText,
            ProviderModelText,
            () => _activeSession,
            () => _arenaBusy,
            () => _lastRenderedSnapshot,
            () => _providerSettingsCoordinator,
            ResourceBrush,
            ApplyProviderStatusSnapshot,
            UpdateTopBarStatus,
            SetArenaRunStatus,
            force => RefreshAdvertisedModelsAsync(force),
            () => OpenModelProviderSettings());
        _transcriptViewCoordinator = new TranscriptViewCoordinator(
            _wpfSettingsStore,
            () => _wpfSettings,
            () => _isRenderingSnapshot,
            value => _isRenderingSnapshot = value,
            AvatarStylePicker,
            SystemGlyphStylePicker,
            TopStripModePicker,
            CompactTranscriptCheckBox,
            TurnCompareCheckBox,
            MatchQualityTimelineCheckBox,
            MemoryNotesCheckBox,
            DecisionCardCheckBox,
            DebugControlsCheckBox,
            StyleFitCheckBox,
            VoiceDriftEnforcementCheckBox,
            FollowChatCheckBox,
            DebugMenuHost,
            DebugMenuPopup,
            ViewMenuPopup,
            ViewActivePresetText,
            ViewMenuButton,
            ViewPresetFocusedButton,
            ViewPresetDiagnosticsButton,
            ViewPresetCompactButton,
            ViewPresetReviewButton,
            TranscriptDashboardGrid,
            TranscriptDiagnosticsHost,
            TranscriptTelemetryHost,
            TranscriptFiltersHost,
            () => _lastRenderedMessages,
            () => _lastRenderedSnapshot,
            PopulateTranscript,
            compare => TranscriptInsight.SetTurnCompareMode(compare),
            messages => DiagnosticsWorkflow.Update(messages),
            () => DiagnosticsWorkflow.CloseDetail(),
            () => _telemetryWorkflowCoordinator?.UpdateTimerState(),
            SetLoadStatus,
            SetArenaRunStatus);
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
            () => TranscriptView.IsTelemetryDisplayed(),
            ResourceBrush);
        _agentPerformanceCoordinator = new AgentPerformanceCoordinator(
            _voiceStyleAdherenceService,
            AgentPerformanceItems,
            AgentPerformanceDetailPopup,
            AgentPerformanceDetailContent,
            ResourceBrush,
            AccentForSpeaker,
            MatchLockCoordinator.FormatParticipantTitle,
            DisplayStatusValue,
            AgentBoardCoordinator.DisplayInlineStatus,
            ShortModelName,
            FormatCompactNumber,
            FormatDuration,
            state => VoiceAdherenceAccent(state),
            diagnostic => VoiceAdherenceAccent(diagnostic),
            ShellUiHelpers.CompactPreview,
            BlendBrush);
        _sessionOverviewCoordinator = new SessionOverviewCoordinator(
            SessionOverviewMatchText,
            SessionOverviewTurnsText,
            SessionOverviewParticipantsText,
            SessionOverviewTokensText,
            SessionOverviewProviderText,
            SessionOverviewContextText,
            TopMatchValue,
            TopProviderValue,
            TopCurrentTurnValue,
            TopTurnsValue,
            TopBarStatus,
            ArenaRunStatus,
            SettingsProviderStatusText,
            () => _arenaBusy,
            () => _arenaRunCoordinator?.IsAutoChatRunning == true,
            ResourceBrush,
            AccentForSpeaker,
            FormatCompactNumber,
            ShortModelName,
            snapshot => AgentPerformance.Populate(snapshot),
            snapshot => ProviderReachability.UpdatePopup(snapshot));
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
        _matchQualityTimelineCoordinator = new MatchQualityTimelineCoordinator(
            () => _wpfSettings.CompactTranscriptMode,
            () => TranscriptInsight.TimelineSelectedTurnFilter,
            turn => TranscriptInsight.ToggleTimelineTurnFilter(turn),
            () => TranscriptInsight.ClearTimelineTurnFilter(),
            (messages, end) => DiagnosticsWorkflow.PointForWindow(messages, end),
            ResourceBrush,
            BlendBrush,
            FormatCompactNumber,
            label => DiagnosticsWorkflow.AccentForState(label),
            label => DiagnosticsWorkflow.AccentForEvidence(label),
            label => DiagnosticsWorkflow.AccentForRisk(label));
        _transcriptListCoordinator = new TranscriptListCoordinator(
            Dispatcher,
            TranscriptItems,
            TranscriptScrollViewer,
            FollowChatCheckBox,
            ShellCards,
            TranscriptActions,
            TranscriptSearch,
            TranscriptInsight,
            TranscriptCards,
            TranscriptAdjunct,
            AgentMemory,
            MatchQualityTimeline,
            ProviderQuickSetup,
            () => _wpfSettings,
            () => _lastRenderedSnapshot,
            messages => _lastRenderedMessages = messages,
            () => _transcriptViewCoordinator?.IsDiagnosticsDisplayed() == true,
            messages => DiagnosticsWorkflow.Update(messages),
            ShouldShowDecisionCard,
            IsAgentSpeaker,
            ResourceBrush,
            AccentForSpeaker,
            BlendBrush,
            ShortModelName,
            DisplayStatusValue);
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
        _matchLockCoordinator = new MatchLockCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            _matchGeneration,
            () => _activeSession,
            () => _theme,
            () => _arenaBusy,
            () => _isRenderingSnapshot,
            ResourceBrush,
            BlendBrush,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetLoadStatus,
            SetArenaRunStatus);
        _customMatchSummaryCoordinator = new CustomMatchSummaryCoordinator(
            ScenarioPreviewItems,
            CastPreviewItems,
            ShellCards,
            MatchLock,
            ResourceBrush,
            AccentForSpeaker,
            BlendBrush);
        _scenarioSeedInspectorCoordinator = new ScenarioSeedInspectorCoordinator(
            ScenarioSeedInspector,
            ShellCards,
            ResourceBrush,
            DisplayStatusValue);
        _agentRosterCoordinator = new AgentRosterCoordinator(
            _coreSessionStore,
            _eventLogStore,
            AgentCountPresetPicker,
            AgentCountPicker,
            ActiveParticipantsPicker,
            ApplyAgentCountButton,
            AgentRosterStatusText,
            () => _isRenderingSnapshot,
            () => _arenaBusy,
            () => _activeSession,
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            SetArenaRunStatus);
        _arenaSessionMutationCoordinator = new ArenaSessionMutationCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            ProviderSettings,
            AgentRoster,
            ProviderBaseUrlText,
            ProviderModelText,
            ProviderTimeoutText,
            ProviderTemperatureText,
            ProviderMaxOutputText,
            ContextTranscriptWindowText,
            ContextPrivateWindowText,
            ContextNotesWindowText,
            UseInternetCheckBox,
            InternetModePicker,
            InternetSourceScopePicker,
            InternetMaxResultsText,
            AllowParticipantInternetCheckBox,
            AllowNarratorInternetCheckBox,
            RequireInternetApprovalCheckBox,
            ProviderTestStatus,
            ResetButton,
            () => _activeSession,
            () => _isRenderingSnapshot,
            () => _theme,
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            force => ProviderReachability.RefreshAsync(force),
            SetLoadStatus,
            SetArenaRunStatus);
        _arenaOperationCoordinator = new ArenaOperationCoordinator(
            _arenaOperationLock,
            LoadStatus,
            ArenaRunStatus,
            AutoChatButton,
            OneTurnButton,
            ResetButton,
            NarrateNowButton,
            StopButton,
            [
                TestProviderButton,
                PreloadSelectedModelsButton,
                ApplySettingsButton,
                ProviderBaseUrlText,
                ProviderModelText,
                AlphaRoleModelText,
                BetaRoleModelText,
                GammaRoleModelText,
                DeltaRoleModelText,
                NarratorRoleModelText,
                ProviderTimeoutText,
                ProviderTemperatureText,
                ProviderMaxOutputText,
                ContextTranscriptWindowText,
                ContextPrivateWindowText,
                ContextNotesWindowText,
                AutoChatCadencePicker,
                AvatarStylePicker,
                SystemGlyphStylePicker,
                TopStripModePicker,
                DebugControlsCheckBox,
                VoiceDriftEnforcementCheckBox,
                SavedStateModePicker,
                SavedStateNameText,
                SavedStateItemPicker,
                SavedStateSaveButton
            ],
            () => _arenaBusy,
            value => _arenaBusy = value,
            () => _arenaRunCoordinator?.IsAutoChatRunning == true,
            (busy, autoChatRunning) => ScenarioWorkflow.UpdateBusyState(busy, autoChatRunning),
            (busy, autoChatRunning) => InternetWorkflow.UpdateBusyState(busy, autoChatRunning),
            () => OperatorTurn.UpdateBusyState(),
            busy => AgentRoster.UpdateBusyState(busy),
            () => SavedStateCoordinator.UpdateActionButtons(),
            busy => AgentBoard.UpdateBusyState(busy),
            busy => TranscriptActions.UpdateBusyState(busy),
            busy => MatchLock.UpdateBusyState(busy));
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
            _ = ProviderReachability.RefreshAsync(force: true);
        };
        SourceInitialized += (_, _) => WindowChromeService.ApplyNativeChromeColor(this);
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

    private void InitializeVisualSettings()
    {
        TranscriptView.InitializeControls();
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
        _transcriptViewCoordinator?.OnCompactTranscriptChanged();
    }

    private void TurnCompareCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnTurnCompareChanged();
    }

    private void MatchQualityTimelineCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnMatchQualityTimelineChanged();
    }

    private void MemoryNotesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnMemoryNotesChanged();
    }

    private void DecisionCardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnDecisionCardChanged();
    }

    private void StyleFitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnStyleFitChanged();
    }

    private void VoiceDriftEnforcementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnVoiceDriftEnforcementChanged();
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
        _agentRosterCoordinator?.OnPresetChanged();
    }

    private async void ApplyAgentCountButton_Click(object sender, RoutedEventArgs e)
    {
        await AgentRoster.ApplyAgentCountAsync();
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
        _transcriptViewCoordinator?.OnFollowChatChanged();
    }

    private void DebugMenuButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ToggleDebugMenu();
    }

    private void ViewMenuButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ToggleViewMenu();
    }

    private void ViewPresetFocused_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ApplyFocusedPreset();
    }

    private void ViewPresetDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ApplyDiagnosticsPreset();
    }

    private void ViewPresetCompact_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ApplyCompactPreset();
    }

    private void ViewPresetReview_Click(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.ApplyReviewPreset();
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
        AgentRoster.ApplySnapshot(activeCount);
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
        SessionOverview.UpdateSessionOverview(snapshot);
        _lastAgentPersonas = snapshot.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToDictionary(agent => agent.Id, agent => agent.Persona, StringComparer.OrdinalIgnoreCase);
        PopulateTranscript(snapshot.Messages);
        AgentBoard.Populate(snapshot, CurrentTurnAgent(snapshot)?.Id);
        PopulateCustomMatch(snapshot);
        NewsPanelWorkflow.Populate(snapshot.Messages);
        OperatorTurn.UpdatePrivateTargetSummary();
    }

    private void PopulateFallbackState(string message)
    {
        TranscriptItems.Children.Clear();
        AgentPerformance.CloseDetail();
        _lastRenderedSnapshot = null;
        TranscriptItems.Children.Add(CreateCard("Transcript", message, ResourceBrush("CardBrush"), ResourceBrush("AlphaAccentBrush")));
        AgentBoard.PopulateFallback();
        NewsPanelWorkflow.PopulateFallback();
    }

    private void UpdateTopBarStatus(ArenaViewSnapshot snapshot)
    {
        SessionOverview.UpdateTopBarStatus(snapshot);
    }

    private static AgentState? CurrentTurnAgent(ArenaViewSnapshot snapshot)
    {
        return SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
    }

    private static string DisplayStatusValue(string value)
    {
        return SessionOverviewCoordinator.DisplayStatusValue(value);
    }

    private void PopulateTranscript(IReadOnlyList<TranscriptMessage> messages)
    {
        if (_transcriptListCoordinator is null)
        {
            _lastRenderedMessages = messages;
            return;
        }

        TranscriptList.Populate(messages);
    }

    private void TranscriptDashboardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _transcriptViewCoordinator?.UpdateDashboardLayout(e.NewSize.Width);
    }

    private void PopulateCustomMatch(ArenaViewSnapshot snapshot)
    {
        SeedInspector.Populate(snapshot);
        ScenarioWorkflow.PopulateGenerationHistory(snapshot);
        CustomMatchSummary.Populate(snapshot);
        MatchSetup.PopulateRivalryMatrix(snapshot);
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return ShellCards.CreateCard(title, body, background, accent);
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
        if (diagnostic.State.Equals("broken", StringComparison.OrdinalIgnoreCase) && RoleStyleCatalog.IsStrictVoiceStyle(diagnostic.VoiceStyle))
        {
            return ResourceBrush("DangerBorderBrush");
        }

        return VoiceAdherenceAccent(diagnostic.State);
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

    private static bool IsSystemEvent(TranscriptMessage message, bool isInternet)
    {
        return TranscriptCardRenderer.IsSystemEvent(message, isInternet);
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
        await ArenaSessionMutations.ApplySettingsAsync();
    }

    private async void AutoChatButton_Click(object sender, RoutedEventArgs e)
    {
        await ArenaRun.StartAutoChatAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ArenaRun.StopAutoChat();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        await ArenaSessionMutations.ResetArenaAsync();
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
        await ArenaRun.NarrateNowAsync();
    }

    private async void CurateNewsButton_Click(object sender, RoutedEventArgs e)
    {
        await InternetWorkflow.CurateNewsAsync();
    }

    private async void OneTurnButton_Click(object sender, RoutedEventArgs e)
    {
        await ArenaRun.RunOneTurnAsync();
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

    private void ExportTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptExportCoordinator.ExportTranscript();
    }

    private void ApplyProviderStatusSnapshot(CoreSessionSummary session, ArenaViewSnapshot snapshot)
    {
        _activeSession = session;
        _activeSnapshotWriteUtc = session.LastModified;
        _lastRenderedSnapshot = snapshot;
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
        await ArenaOperations.RunAsync(status, operationButton, action, allowDuringAutoChat);
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled)
    {
        SetArenaBusy(busy, status, stopEnabled, null);
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled, Button? operationButton)
    {
        if (_arenaOperationCoordinator is null)
        {
            _arenaBusy = busy;
            ArenaRunStatus.Text = status;
            return;
        }

        ArenaOperations.SetBusy(busy, status, stopEnabled, operationButton);
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

    private void DiagnosticDetailCloseButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsWorkflow.CloseDetail();
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
        ShellNavigation.OnThemeSelectionChanged();
    }

    private Brush ResourceBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.White;
    }

    private static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        return ShellUiHelpers.BlendBrush(baseBrush, accentBrush, accentAmount);
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
        ShellNavigation.ShowTranscriptPanel();

        if (clearFilters)
        {
            ClearTranscriptFilters();
        }
    }

    private void ShowCustomMatchPanel()
    {
        ShellNavigation.ShowCustomMatchPanel();
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
        ProviderReachability.ShowPopup();
        e.Handled = true;
    }

    private void ProviderHealthCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderReachability.ClosePopup();
    }

    private async void ProviderHealthTestButton_Click(object sender, RoutedEventArgs e)
    {
        await ProviderReachability.TestProviderAsync();
    }

    private async void ProviderHealthRefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await ProviderReachability.RefreshModelsAsync();
    }

    private void ProviderHealthSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderReachability.OpenSettings();
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

    private void AppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _appSettingsCoordinator?.Toggle();
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
        if (_appSettingsCoordinator is not null)
        {
            AppSettingsWorkflow.OpenModelProviderSettings(baseUrl, model);
            return;
        }

        ShellNavigation.SetAppSettingsVisible(true);
    }

    private void CloseAppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _appSettingsCoordinator?.SetVisible(false);
    }

    private void VisualSettings_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnVisualSettingsChanged();
    }

    private string CurrentAvatarStyle()
    {
        return _transcriptViewCoordinator?.CurrentAvatarStyle()
            ?? TranscriptViewCoordinator.CurrentAvatarStyle(_wpfSettings);
    }

    private string CurrentTopStripMode()
    {
        return _transcriptViewCoordinator?.CurrentTopStripMode()
            ?? TranscriptViewCoordinator.CurrentTopStripMode(_wpfSettings);
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
        if (_appSettingsCoordinator is not null)
        {
            AppSettingsWorkflow.SetVisible(visible);
        }
    }

    private static bool IsAgentSpeaker(string speakerId)
    {
        return AgentRosterService.IsParticipantId(speakerId);
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

}
