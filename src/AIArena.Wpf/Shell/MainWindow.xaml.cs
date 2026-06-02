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
    private readonly TranscriptCardRenderer? _transcriptCardRenderer;
    private readonly TranscriptAdjunctCoordinator? _transcriptAdjunctCoordinator;
    private readonly AgentMemoryCoordinator? _agentMemoryCoordinator;
    private readonly ScenarioWorkflowCoordinator? _scenarioWorkflowCoordinator;
    private readonly OperatorTurnCoordinator? _operatorTurnCoordinator;
    private readonly InternetWorkflowCoordinator? _internetWorkflowCoordinator;
    private readonly ArenaRunCoordinator? _arenaRunCoordinator;
    private readonly ProviderSettingsCoordinator? _providerSettingsCoordinator;
    private readonly ProviderReachabilityCoordinator? _providerReachabilityCoordinator;
    private readonly TelemetryWorkflowCoordinator? _telemetryWorkflowCoordinator;
    private readonly AgentPerformanceCoordinator? _agentPerformanceCoordinator;
    private readonly SessionOverviewCoordinator? _sessionOverviewCoordinator;
    private readonly DiagnosticsWorkflowCoordinator? _diagnosticsWorkflowCoordinator;
    private readonly MatchSetupCoordinator? _matchSetupCoordinator;
    private readonly MatchLockCoordinator? _matchLockCoordinator;
    private readonly AgentRosterCoordinator? _agentRosterCoordinator;
    private readonly ArenaSessionMutationCoordinator? _arenaSessionMutationCoordinator;
    private readonly ShellNavigationCoordinator? _shellNavigationCoordinator;
    private readonly MatchQualityTimelineCoordinator? _matchQualityTimelineCoordinator;
    private readonly AgentBoardCoordinator? _agentBoardCoordinator;
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
    private string _transcriptDashboardLayout = "";
    private Button? _breathingOperationButton;
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

    private TranscriptCardRenderer TranscriptCards =>
        _transcriptCardRenderer ?? throw new InvalidOperationException("Transcript card renderer is not initialized.");

    private TranscriptAdjunctCoordinator TranscriptAdjunct =>
        _transcriptAdjunctCoordinator ?? throw new InvalidOperationException("Transcript adjunct coordinator is not initialized.");

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

    private ProviderReachabilityCoordinator ProviderReachability =>
        _providerReachabilityCoordinator ?? throw new InvalidOperationException("Provider reachability coordinator is not initialized.");

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
        SessionOverview.UpdateSessionOverview(snapshot);
    }


    private void PopulateFallbackState(string message)
    {
        TranscriptItems.Children.Clear();
        NewsItems.Children.Clear();
        AgentPerformance.CloseDetail();
        _lastRenderedSnapshot = null;
        TranscriptItems.Children.Add(CreateCard("Transcript", message, ResourceBrush("CardBrush"), ResourceBrush("AlphaAccentBrush")));
        AgentBoard.PopulateFallback();
        NewsItems.Children.Add(CreateCard("News", "No live snapshot data.", ResourceBrush("CardBrush"), ResourceBrush("NarratorAccentBrush")));
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
        _lastRenderedMessages = messages;
        TranscriptItems.Children.Clear();
        TranscriptActions.Clear();
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
                TranscriptItems.Children.Add(AgentMemory.CreatePanel(_lastRenderedSnapshot));
            }

            TranscriptItems.Children.Add(CreateArenaReadyCard(_lastRenderedSnapshot));
            return;
        }

        if (visibleMessages.Length == 0)
        {
            if (_wpfSettings.ShowMatchQualityTimeline)
            {
                TranscriptItems.Children.Add(MatchQualityTimeline.CreatePanel(messages));
            }
            if (_wpfSettings.ShowAgentMemoryNotes && _lastRenderedSnapshot is not null)
            {
                TranscriptItems.Children.Add(AgentMemory.CreatePanel(_lastRenderedSnapshot));
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
            TranscriptItems.Children.Add(MatchQualityTimeline.CreatePanel(messages));
        }
        if (ShouldShowDecisionCard() && _lastRenderedSnapshot is not null)
        {
            TranscriptItems.Children.Add(TranscriptAdjunct.CreateDecisionCardPanel(_lastRenderedSnapshot));
        }
        if (_wpfSettings.TurnCompareMode)
        {
            TranscriptInsight.EnsureTurnCompareSelection(visibleMessages);
            TranscriptItems.Children.Add(TranscriptAdjunct.CreateTurnComparePanel(visibleMessages));
        }
        if (_wpfSettings.ShowAgentMemoryNotes && _lastRenderedSnapshot is not null)
        {
            TranscriptItems.Children.Add(AgentMemory.CreatePanel(_lastRenderedSnapshot));
        }
        var moderatorPanel = TranscriptAdjunct.CreateAutoModeratorPanel(messages);
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
        AgentBoard.Populate(snapshot, CurrentTurnAgent(snapshot)?.Id);
    }

    private void PopulateCustomMatch(ArenaViewSnapshot snapshot)
    {
        ScenarioPreviewItems.Children.Clear();
        CastPreviewItems.Children.Clear();
        ScenarioSeedInspector.Children.Clear();
        MatchLock.ClearControls();

        PopulateScenarioSeedInspector(snapshot);
        ScenarioWorkflow.PopulateGenerationHistory(snapshot);

        ScenarioPreviewItems.Children.Add(MatchLock.CreateLockCard(
            "topic",
            "Topic",
            string.IsNullOrWhiteSpace(snapshot.ScenarioTopic) ? "No topic is set for this match yet." : snapshot.ScenarioTopic,
            ResourceBrush("CardBrush"),
            snapshot.TopicLocked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            snapshot.TopicLocked));
        ScenarioPreviewItems.Children.Add(MatchLock.CreateLockCard(
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
                CastPreviewItems.Children.Add(MatchLock.CreateLockCard(
                    agent.Id,
                    MatchLockCoordinator.FormatCastPreviewTitle(agent.Id, agent.Name),
                    string.IsNullOrWhiteSpace(agent.Persona) ? "(no persona)" : agent.Persona,
                    BlendBrush(ResourceBrush("CardBrush"), AccentForSpeaker(agent.Id), 0.16),
                    AccentForSpeaker(agent.Id),
                    agent.Locked,
                    agent.VoiceStyle,
                    agent.PressureProfile));
            }
        }

        CastPreviewItems.Children.Add(MatchLock.CreateLockCard(
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
            NewsItems.Children.Add(TranscriptAdjunct.CreateNewsInspectorCard(message));
        }
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return ShellCards.CreateCard(title, body, background, accent);
    }

    private Border CreateEmptyStateCard(string title, string body, Brush accent)
    {
        return ShellCards.CreateEmptyStateCard(title, body, accent);
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
        actions.Children.Add(TranscriptActions.CreateButton("Save + test", async (_, _) =>
        {
            await ProviderSettings.SaveAndTestProviderQuickSetupAsync(baseUrlBox.Text, modelBox.Text, statusText);
        }, true, TranscriptActionKind.Primary));
        actions.Children.Add(TranscriptActions.CreateButton("Open settings", (_, _) => OpenModelProviderSettings(baseUrlBox.Text, modelBox.Text), true));

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
        return SessionOverviewCoordinator.ProviderSetupStatus(snapshot);
    }

    private Border CreateSetupChip(string label, string value, Brush accent)
    {
        return ShellCards.CreateSetupChip(label, value, accent);
    }

    private static string CurrentTurnModel(ArenaViewSnapshot snapshot, AgentState? current)
    {
        return SessionOverviewCoordinator.CurrentTurnModel(snapshot, current);
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

    private Border CreateTranscriptCard(TranscriptMessage message, bool retryable, bool searchMatch, bool isLatest)
    {
        return TranscriptCards.CreateCard(message, retryable, searchMatch, isLatest);
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

    private Border CreateCard(string title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        return ShellCards.CreateCard(title, body, background, accent, extraContent);
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
        var ownsBusyState = !_arenaBusy;
        var runsDuringAutoChat = !ownsBusyState && allowDuringAutoChat && (_arenaRunCoordinator?.IsAutoChatRunning == true);
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

                SetBreathingOperationButton(_arenaRunCoordinator?.IsAutoChatRunning == true ? AutoChatButton : null);
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
        var autoChatRunning = _arenaRunCoordinator?.IsAutoChatRunning == true;
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
        AgentRoster.UpdateBusyState(busy);
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
        AgentBoard.UpdateBusyState(busy);
        TranscriptActions.UpdateBusyState(busy);
        MatchLock.UpdateBusyState(busy);
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
        AnimateSettingsGear();
        ShellNavigation.ToggleAppSettings();
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
        ShellNavigation.SetAppSettingsVisible(visible);
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
