using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using CoreVoiceAdherenceDiagnostic = AIArena.Core.Models.VoiceAdherenceDiagnostic;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow : Window, IAIArenaControlTarget
{
    private const string ReleasesUrl = "https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases";
    private const double RightRailExpandedWidth = 320;
    internal const double SupportedMinimumWindowWidth = 960;
    internal const double SupportedMinimumWindowHeight = 540;
    internal const double RightRailAutoCollapseWidth = 1200;
    internal const double TopBarInlineMinWidth = 1180;
    internal const double NavigationRailCompactWidth = 184;
    internal const double NavigationRailStandardWidth = 208;
    internal const double NavigationRailComfortableWidth = 224;
    internal const double NavigationRailStandardMinWindowWidth = 1500;
    internal const double NavigationRailComfortableMinWindowWidth = 1920;
    internal const double RightRailCompactWidth = 220;
    internal const double RightRailFullWidthMinWindowWidth = 1500;
    private static readonly TimeSpan VoiceTtsSaveDebounceDelay = TimeSpan.FromMilliseconds(250);
    private readonly SessionStore _coreSessionStore = new();
    private readonly EventLogStore _eventLogStore;
    private readonly ModelProviderHealthService _providerHealth = new();
    private readonly ProviderReachabilityService _providerReachabilityService;
    private readonly ProviderConfigurationControlService _providerConfigurationControlService;
    private readonly ProviderRuntimeService _providerRuntimeService;
    private readonly ProviderRequestTraceStore _providerRequestTraceStore;
    private readonly ModelProviderClient _modelClient;
    private readonly TranscriptService _transcriptService = new();
    private readonly ContextRecoveryService _contextRecoveryService;
    private readonly TurnRunnerService _turnRunner;
    private readonly MatchGenerationService _matchGeneration;
    private readonly NarratorService _narratorService;
    private readonly DiscourseDiagnosticsService _discourseDiagnostics = new();
    private readonly VoiceStyleAdherenceService _voiceStyleAdherenceService = new();
    private readonly InternetToolService _internetToolService;
    private readonly WpfSettingsStore _wpfSettingsStore = new();
    private readonly ComposerDraftStore _composerDraftStore = new();
    private readonly ScenarioTemplateStore _scenarioTemplateStore = new();
    private readonly VoiceNarrationService _voiceNarrationService = new();
    private readonly UserGuideWindowHost _userGuideWindowHost = new();
    private readonly ShellCardFactory? _shellCardFactory;
    private readonly SavedStateWorkflowCoordinator? _savedStateCoordinator;
    private readonly SavedStateControlService _savedStateControlService;
    private readonly CrossSessionSearchService _crossSessionSearchService;
    private readonly SessionForkWorkflowService _sessionForkWorkflowService;
    private readonly ShellOverlayControlService _shellOverlayControlService;
    private readonly ScenarioGenerationControlService _scenarioGenerationControlService;
    private readonly RivalryMatrixControlService _rivalryMatrixControlService;
    private readonly MatchSetupPortabilityService _matchSetupPortabilityService;
    private readonly AppPreferenceControlService _appPreferenceControlService;
    private readonly AIArenaSettingsControlHandler _settingsControlHandler;
    private readonly AIArenaMatchSetupControlHandler _matchSetupControlHandler;
    private readonly AIArenaSessionForkControlHandler _sessionForkControlHandler;
    private readonly AIArenaCollaborateControlHandler _collaborateControlHandler;
    private readonly AIArenaProviderControlHandler _providerControlHandler;
    private readonly AIArenaAppControlHandler _appControlHandler;
    private readonly TranscriptExportCoordinator? _transcriptExportCoordinator;
    private readonly TranscriptSearchCoordinator? _transcriptSearchCoordinator;
    private readonly TranscriptInsightCoordinator? _transcriptInsightCoordinator;
    private readonly TranscriptActionCoordinator? _transcriptActionCoordinator;
    private readonly TranscriptMutationCoordinator? _transcriptMutationCoordinator;
    private readonly TranscriptListCoordinator? _transcriptListCoordinator;
    private readonly TranscriptCardRenderer? _transcriptCardRenderer;
    private readonly TranscriptAdjunctCoordinator? _transcriptAdjunctCoordinator;
    private readonly AgentMemoryCoordinator? _agentMemoryCoordinator;
    private readonly ScenarioWorkflowCoordinator? _scenarioWorkflowCoordinator;
    private readonly OperatorTurnCoordinator? _operatorTurnCoordinator;
    private readonly InternetWorkflowCoordinator? _internetWorkflowCoordinator;
    private readonly ArenaRunCoordinator? _arenaRunCoordinator;
    private readonly ProviderSettingsCoordinator? _providerSettingsCoordinator;
    private readonly LlamaCppRuntimeCoordinator? _llamaCppRuntimeCoordinator;
    private readonly ArenaEvaluationCoordinator? _arenaEvaluationCoordinator;
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
    private readonly ExperimentLabCoordinator? _experimentLabCoordinator;
    private readonly AgentInspectionLabControl _agentInspectionLabControl;
    private readonly AgentInspectionLabCoordinator _agentInspectionLabCoordinator;
    private readonly FaultInjectionLabControl _faultInjectionLabControl;
    private readonly FaultInjectionLabCoordinator _faultInjectionLabCoordinator;
    private readonly ModelRoutingOptimizerControl _modelRoutingOptimizerControl;
    private readonly ModelRoutingOptimizerCoordinator _modelRoutingOptimizerCoordinator;
    private readonly InAppQaInspectorControl _inAppQaInspectorControl;
    private readonly InAppQaInspectorCoordinator _inAppQaInspectorCoordinator;
    private readonly CollaborateCoordinator? _collaborateCoordinator;
    private readonly AgentWorkspaceCoordinator? _agentWorkspaceCoordinator;
    private readonly AgentImpactExplorerCoordinator? _agentImpactExplorerCoordinator;
    private readonly MatchQualityTimelineCoordinator? _matchQualityTimelineCoordinator;
    private readonly AgentBoardCoordinator? _agentBoardCoordinator;
    private readonly ArenaOperationCoordinator? _arenaOperationCoordinator;
    private readonly AppSettingsCoordinator? _appSettingsCoordinator;
    private readonly AIArenaControlPlaneEventHub _controlPlaneEvents = new();
    private readonly AIArenaApplicationStatusControlPublisher _applicationStatusControlPublisher;
    private AIArenaControlPlaneHost? _controlPlaneHost;
    private string _controlPlaneInitializationError = "";
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _modelRefreshTimer;
    private readonly DispatcherTimer _providerHealthTimer;
    private readonly DispatcherDebouncer _voiceTtsSettingsSaveDebouncer;
    private readonly SemaphoreSlim _arenaOperationLock = new(1, 1);
    private IReadOnlyList<TranscriptMessage> _lastRenderedMessages = [];
    private IReadOnlyDictionary<string, string> _lastAgentPersonas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private CoreSessionSummary? _activeSession;
    private string _statusCenterSessionId = "";
    private DateTimeOffset _activeSnapshotWriteUtc;
    private bool _snapshotRefreshInProgress;
    private bool _isRenderingSnapshot;
    private bool _arenaBusy;
    private bool _isUpdatingVoiceTtsSettings = true;
    private bool _isUpdatingWorldDebug;
    private bool _isUpdatingAgentWorkspace;
    private bool _isUpdatingControlPlane;
    private bool _rightRailAutoCollapseActive;
    private bool _rightRailNarrowRevealRequested;
    private bool _rightRailWidthCollapseLatched;
    private bool _topBarStacked = true;
    private bool _shutdownInProgress;
    private bool _shutdownAttemptInProgress;
    private bool _shutdownReady;
    private IInputElement? _settingsFocusReturnTarget;
    private IInputElement? _viewMenuFocusReturnTarget;
    private IInputElement? _debugMenuFocusReturnTarget;
    private IInputElement? _providerHealthFocusReturnTarget;
    private IInputElement? _matchSetupFocusReturnTarget;
    private IInputElement? _currentSetupTransferFocusReturnTarget;
    private IInputElement? _generationCopyFocusReturnTarget;
    private IInputElement? _agentComposerControlsFocusReturnTarget;
    private IInputElement? _diagnosticDetailFocusReturnTarget;
    private IInputElement? _agentPerformanceDetailFocusReturnTarget;
    private IInputElement? _transcriptFiltersFocusReturnTarget;
    private ShellSurface _activeShellSurface = ShellSurface.Lab;
    private ShellSurface _matchSetupReturnSurface = ShellSurface.Lab;
    private string _matchSetupSection = "scenario";
    private readonly Dictionary<Expander, bool> _settingsExpansionBeforeSearch = [];
    private bool _settingsSearchActive;
    private readonly Dictionary<string, string> _sessionSettingsBaseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _sessionSettingsDrafts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _unboundSessionSettingsDraft;
    private bool _sessionSettingsTrackingAttached;
    private bool _restoringSessionSettingsDraft;
    private string _trackedSessionSettingsSessionId = "";
    private WpfSettings _wpfSettings = new();
    private ThemePalette _theme = ThemePalette.Resolve("system");
    private ArenaViewSnapshot? _lastRenderedSnapshot;
    private readonly MatchSetupSnapshotRenderState _matchSetupSnapshotRenderState = new();

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

    private ExperimentLabCoordinator ExperimentLab =>
        _experimentLabCoordinator ?? throw new InvalidOperationException("Experiment Lab coordinator is not initialized.");

    private CollaborateCoordinator Collaborate =>
        _collaborateCoordinator ?? throw new InvalidOperationException("Collaborate coordinator is not initialized.");

    private AgentWorkspaceCoordinator AgentWorkspace =>
        _agentWorkspaceCoordinator ?? throw new InvalidOperationException("Agent workspace coordinator is not initialized.");

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
        _eventLogStore = EventLogStore.ForSessionStore(_coreSessionStore);
        InitializeComponent();
        ShellNavigationRail.Presentation = ShellTopBar.Presentation;
        _userGuideWindowHost.AppRouteRequested += UserGuideWindowHost_AppRouteRequested;
        ShellTopBar.Presentation.StatusCenter.NavigationRequested += ApplicationStatusCenter_NavigationRequested;
        UniversalStatusCenter.PresentationChanged += UniversalStatusCenter_PresentationChanged;
        UniversalStatusCenter.Bind(ShellTopBar.Presentation.StatusCenter);
        _applicationStatusControlPublisher = new AIArenaApplicationStatusControlPublisher(
            ShellTopBar.Presentation.StatusCenter,
            _controlPlaneEvents.Publish);
        _voiceTtsSettingsSaveDebouncer = new DispatcherDebouncer(
            Dispatcher,
            VoiceTtsSaveDebounceDelay,
            () => SaveVoiceTtsSettings("Voice TTS level saved."));
        _shellCardFactory = new ShellCardFactory(ResourceBrush, BlendBrush);
        _providerReachabilityService = new ProviderReachabilityService(_coreSessionStore, _eventLogStore, _providerHealth);
        _providerConfigurationControlService = new ProviderConfigurationControlService(
            _coreSessionStore,
            _eventLogStore,
            _arenaOperationLock,
            () => _activeSession,
            () => _arenaBusy,
            async (status, refreshModels, cancellationToken) =>
            {
                await RefreshActiveSessionAsync(status, cancellationToken);
                if (refreshModels)
                {
                    await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
                }

                await ProviderReachability.RefreshAsync(force: true, cancellationToken);
            });
        _providerRuntimeService = new ProviderRuntimeService(
            _coreSessionStore,
            _providerHealth,
            _providerReachabilityService);
        _appControlHandler = new AIArenaAppControlHandler(
            new AIArenaScreenshotControlService(this, _coreSessionStore.DataRoot),
            _controlPlaneEvents,
            ShowScreenshotReceipt,
            new AIArenaUiVerificationControlService(
                this,
                _coreSessionStore.DataRoot,
                () => _wpfSettings.ThemeId,
                () => _lastRenderedSnapshot?.Messages.Count,
                () => ExperimentLabPanel.SelectedFeatureKey));
        _providerRequestTraceStore = new ProviderRequestTraceStore();
        _modelClient = CreateObservedModelProviderClient(_providerRequestTraceStore);
        _contextRecoveryService = new ContextRecoveryService(
            _coreSessionStore,
            _modelClient,
            _transcriptService,
            _eventLogStore);
        _internetToolService = new InternetToolService(
            new LocalInternetToolProvider(ensureSearchBackendAsync: EnsureInternetBackendForSearchAsync),
            _eventLogStore);
        _turnRunner = new TurnRunnerService(_modelClient, _coreSessionStore, _eventLogStore, _transcriptService, _internetToolService);
        _matchGeneration = new MatchGenerationService(_modelClient, _coreSessionStore, _eventLogStore, _internetToolService);
        _narratorService = new NarratorService(_modelClient, _coreSessionStore, _eventLogStore, _transcriptService, _internetToolService);
        _sessionForkWorkflowService = new SessionForkWorkflowService(
            _coreSessionStore,
            _eventLogStore,
            () => _activeSession,
            () => _arenaBusy,
            (status, action) => RunArenaBusyForCoordinatorAsync(
                status,
                operationButton: null,
                action,
                allowDuringAutoChat: false),
            (sessionId, cancellationToken) => LoadSessionsAsync(sessionId, cancellationToken),
            (eventName, message, data) => _controlPlaneEvents.Publish(eventName, message, data));
        _sessionForkControlHandler = new AIArenaSessionForkControlHandler(_sessionForkWorkflowService);
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
            ForkCurrentMatchButton,
            ForkLineageReceipt,
            ForkLineageText,
            OpenForkParentButton,
            _sessionForkWorkflowService,
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
            status => SetApplicationStatus("app.saved-state", "App", status, "arena"),
            SetLoadStatus,
            SavedStateShowEmptyCheckBox);
        _crossSessionSearchService = new CrossSessionSearchService(_coreSessionStore);
        _savedStateControlService = new SavedStateControlService(
            _coreSessionStore,
            _eventLogStore,
            () => _activeSession,
            LoadSessionAsync,
            LoadSessionsAsync,
            RefreshActiveSessionForProviderAsync);
        _scenarioGenerationControlService = new ScenarioGenerationControlService(
            _matchGeneration,
            _coreSessionStore,
            () => _wpfSettings,
            () => _activeSession,
            (status, action) => RunArenaBusyForCoordinatorAsync(status, null, action, true),
            RefreshActiveSessionForProviderAsync,
            LoadSessionsAsync);
        _rivalryMatrixControlService = new RivalryMatrixControlService(
            _coreSessionStore,
            _eventLogStore,
            () => _activeSession,
            () => _arenaBusy,
            (status, action) => RunArenaBusyForCoordinatorAsync(status, null, action, true),
            RefreshActiveSessionForProviderAsync);
        _matchSetupPortabilityService = new MatchSetupPortabilityService(
            _coreSessionStore,
            _eventLogStore,
            () => _activeSession,
            () => _arenaBusy,
            LoadSessionsAsync,
            _arenaOperationLock,
            () => _providerModelsSurfaceCoordinator?.CaptureProfileModelSettings() ?? []);
        _transcriptInsightCoordinator = new TranscriptInsightCoordinator(
            () => PopulateTranscript(_lastRenderedMessages),
            () => Dispatcher.BeginInvoke(() => TranscriptItems.ScrollToTop(), DispatcherPriority.Background));
        _transcriptSearchCoordinator = new TranscriptSearchCoordinator(
            this,
            Dispatcher,
            TranscriptSearchPopup,
            TranscriptSearchButton,
            TranscriptSearchText,
            ClearTranscriptSearchButton,
            TranscriptSearchDragHandle,
            TranscriptRecentSearchItems,
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
            () => PopulateTranscript(_lastRenderedMessages),
            TranscriptSearchResultsHeader,
            query => Collaborate.UpdateRecentSearch(query),
            query => Collaborate.SearchConversations(query),
            id => Collaborate.TryOpenConversation(id));
        _transcriptExportCoordinator = new TranscriptExportCoordinator(
            this,
            ExportStatusText,
            () => _activeSession,
            () => _arenaBusy,
            () => _lastRenderedMessages,
            messages => TranscriptSearch.FilterMessages(messages),
            SetLoadStatus,
            status => SetApplicationStatus("app.transcript-export", "App", status, "arena"));
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
            RefreshActiveSessionForTranscriptMutationAsync,
            SetLoadStatus,
            SetTranscriptMutationStatus);
        var sharedModelPreloadService = new ModelPreloadService();
        _providerSettingsCoordinator = new ProviderSettingsCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            _providerHealth,
            _providerRuntimeService,
            sharedModelPreloadService,
            new LmStudioModelDownloadService(),
            new ProviderAutoConfigureService(_providerHealth),
            _arenaOperationLock,
            ProviderPresetPicker,
            ProviderPresetStatusText,
            ProviderApiModePicker,
            ProviderBaseUrlText,
            ProviderApiTokenBox,
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
            UnloadSelectedModelsButton,
            LoadPlanPreviewText,
            PreloadModelsStatusText,
            PreloadModelsItems,
            DownloadModelText,
            DownloadQuantizationPicker,
            DownloadModelButton,
            CheckDownloadStatusButton,
            DownloadModelStatusText,
            ProviderTimeoutText,
            ProviderContextLengthText,
            ProviderReasoningPicker,
            ProviderNativeStatefulChatCheckBox,
            ProviderNativeIdleTtlText,
            ProviderTestStatus,
            ProviderModelsStatus,
            () => _activeSession,
            () => _lastRenderedSnapshot,
            () => _theme,
            () => _isRenderingSnapshot,
            () => AppSettingsPanel.Visibility == Visibility.Visible,
            () => _arenaBusy,
            ResourceBrush,
            AccentForSpeaker,
            ShortModelName,
            DisplayStatusValue,
            (preferredSessionId, cancellationToken) => LoadSessionsAsync(preferredSessionId, cancellationToken),
            SaveSnapshotForProviderAsync,
            RefreshActiveSessionForProviderAsync,
            (force, cancellationToken) => ProviderReachability.RefreshAsync(force, cancellationToken),
            () => ProviderReachability.UpdatePopup(),
            RoleGenerationOverrideFor,
            statusCenter: ShellTopBar.Presentation.StatusCenter);
        _providerModelsSurfaceCoordinator = new ProviderModelsSurfaceCoordinator(
            ProviderModelsPanel,
            _coreSessionStore,
            _providerConfigurationControlService,
            _providerHealth,
            sharedModelPreloadService,
            _arenaOperationLock,
            () => _activeSession,
            () => _arenaBusy,
            statusCenter: ShellTopBar.Presentation.StatusCenter);
        _llamaCppRuntimeCoordinator = new LlamaCppRuntimeCoordinator(
            new LlamaCppRuntimeService(),
            new LlamaCppRuntimeControls(
                LlamaCppRuntimeStatusCard,
                LlamaCppRuntimeStatusText,
                LlamaCppRuntimeCheckedAtText,
                LlamaCppRuntimeBuildValue,
                LlamaCppRuntimeModelValue,
                LlamaCppRuntimeQuantizationValue,
                LlamaCppRuntimeContextValue,
                LlamaCppRuntimeGpuLayersValue,
                LlamaCppRuntimeSlotsValue,
                LlamaCppRuntimeMemoryValue,
                LlamaCppRuntimeThroughputValue,
                LlamaCppRuntimeCapacityWarning,
                LlamaCppRuntimeCapacityWarningText,
                LlamaCppInspectButton,
                LlamaCppReconnectButton,
                LlamaCppPreloadButton,
                LlamaCppUnloadButton),
            () => ProviderSettings.CaptureRuntimeConfig(),
            () => _arenaBusy,
            ResourceBrush,
            ShellTopBar.Presentation.StatusCenter,
            config => ProviderStatusIdentity(_statusCenterSessionId, config));
        _arenaEvaluationCoordinator = new ArenaEvaluationCoordinator(
            _coreSessionStore,
            new ArenaEvaluationService(_discourseDiagnostics, _voiceStyleAdherenceService),
            new ArenaEvaluationHistoryStore(),
            ArenaEvaluationStatusText,
            ArenaEvaluationHistoryText,
            ArenaEvaluationBaselineText,
            ArenaEvaluationCandidateText,
            ArenaEvaluationComparisonSummaryText,
            ArenaEvaluationComparisonItems,
            ArenaEvaluationModelItems,
            ArenaEvaluationQaSummaryText,
            ArenaEvaluationQaItems,
            ArenaEvaluationCaptureBaselineButton,
            ArenaEvaluationCompareCurrentButton,
            ArenaEvaluationRunQaButton,
            ArenaEvaluationCopyEvidenceButton,
            ArenaEvaluationCopyReplaySetupButton,
            () => _activeSession,
            () => _arenaBusy,
            ResourceBrush,
            status => SetApplicationStatus("arena.evaluation", "Arena", status, "arena"));
        _providerQuickSetupCoordinator = new ProviderQuickSetupCoordinator(
            TranscriptActions,
            () => ProviderSettings.AdvertisedModels,
            ResourceBrush,
            BlendBrush,
            (baseUrl, model, statusText) => RunProviderCommitSafelyAsync(
                (coordinator, cancellationToken) => coordinator.SaveAndTestProviderQuickSetupAsync(
                    baseUrl,
                    model,
                    statusText,
                    cancellationToken)),
            OpenModelProviderSettings);
        _wpfSettings = _wpfSettingsStore.Load();
        InitializeAgentAndStreamingSettingsFields();
        ShowMatchSetupSection("scenario");
        _experimentLabCoordinator = new ExperimentLabCoordinator(
            ExperimentLabPanel,
            _coreSessionStore,
            _modelClient,
            () => _activeSession?.Id,
            (sessionId, cancellationToken) => LoadSessionsAsync(sessionId, cancellationToken),
            _coreSessionStore.DataRoot,
            providerRequestTraces: _providerRequestTraceStore);
        ExperimentLabPanel.Initialize(_experimentLabCoordinator);
        _agentInspectionLabControl = new AgentInspectionLabControl();
        _agentInspectionLabCoordinator = new AgentInspectionLabCoordinator(
            _agentInspectionLabControl,
            _providerRequestTraceStore,
            _coreSessionStore,
            () => _activeSession?.Id);
        _faultInjectionLabControl = new FaultInjectionLabControl();
        _faultInjectionLabCoordinator = new FaultInjectionLabCoordinator(
            _faultInjectionLabControl,
            _modelClient,
            () => ProviderSettings.CaptureRuntimeConfig());
        _modelRoutingOptimizerControl = new ModelRoutingOptimizerControl();
        _modelRoutingOptimizerCoordinator = new ModelRoutingOptimizerCoordinator(
            _modelRoutingOptimizerControl,
            _experimentLabCoordinator.CreateRoutingEvidenceSource(),
            ModelRoutingOptimizerCoordinator.CreateSessionApplicationBoundary(
                new ArenaRouteApplicationService(_coreSessionStore),
                () => _activeSession?.Id,
                _arenaOperationLock,
                () => _arenaBusy,
                () => _arenaRunCoordinator?.IsAutoChatRunning == true,
                action => ArenaOperations.TrackAsync(action),
                (sessionId, cancellationToken) => LoadSessionsAsync(sessionId, cancellationToken)));
        _inAppQaInspectorControl = new InAppQaInspectorControl();
        var isolatedQaInspectorCapture = InAppQaInspectorCoordinator.ShouldPreserveReviewsForIsolatedCapture(
            _coreSessionStore.DataRoot);
        _inAppQaInspectorCoordinator = new InAppQaInspectorCoordinator(
            _inAppQaInspectorControl,
            ResolveQaRepositoryRoot(AppContext.BaseDirectory),
            isApplicationRunning: static () => true,
            readOnlyPreserveReviews: isolatedQaInspectorCapture);
        RegisterProductionExperimentFeatures(
            ExperimentLabPanel,
            _agentInspectionLabControl,
            _faultInjectionLabControl,
            _modelRoutingOptimizerControl,
            _inAppQaInspectorControl);
        RegisterProductionExperimentRefreshes();
        _shellNavigationCoordinator = new ShellNavigationCoordinator(
            this,
            _wpfSettingsStore,
            () => _wpfSettings,
            ThemePicker,
            ArenaNavButton,
            ExperimentLabNavButton,
            CustomMatchNavButton,
            AgentNavButton,
            CollaborateNavButton,
            AppSettingsButton,
            TranscriptPanel,
            ExperimentLabPanel,
            CustomMatchPanel,
            ProviderModelsPanel,
            AgentWorldPanel,
            AgentWorkspacePanel,
            CollaboratePanel,
            ArenaTopBarMetrics,
            AgentTopBarMetrics,
            CollaborateTopBarMetrics,
            ArenaRightRailPanel,
            ExperimentRightRailPanel,
            AgentRightRailPanel,
            CollaborateRightRailPanel,
            ArenaSessionOverviewPanel,
            ArenaLiveAgentsPanel,
            AgentLeftRailContextPanel,
            CollaborateLeftRailContextPanel,
            ExperimentLeftRailContextPanel,
            AppSettingsPanel,
            theme => _theme = theme,
            ResourceBrush,
            RefreshGeneratedThemeSurfaces,
            () => _activeSession is not null,
            RefreshActiveSession);
        _agentWorkspaceCoordinator = new AgentWorkspaceCoordinator(
            this,
            Dispatcher,
            _wpfSettingsStore,
            () => _wpfSettings,
            null,
            AgentWorkspacePathText,
            AgentWorkspaceBrowseButton,
            AgentWorkspaceApplyButton,
            AgentWorkspaceStatusText,
            AgentWorkspaceBoundaryText,
            AgentLeftWorkspacePathText,
            AgentLeftBoundaryText,
            AgentLeftRoleItems,
            AgentTopWorkspaceValue,
            AgentTopProviderValue,
            AgentTopModeValue,
            AgentChatScrollViewer,
            AgentMessageItems,
            AgentPromptText,
            AgentPlanPromptButton,
            AgentBreakdownPromptButton,
            AgentProgressPromptButton,
            AgentCommandPromptButton,
            AgentBuildAppPromptButton,
            AgentNextStepPromptButton,
            AgentVerifyPromptButton,
            AgentRescueCommandButton,
            AgentSendButton,
            AgentStopButton,
            AgentClearButton,
            AgentPromptBudgetText,
            AgentStatusText,
            AgentPhaseSummaryText,
            AgentPhaseItems,
            AgentBuildEvidenceSummaryText,
            AgentBuildEvidenceItems,
            AgentActivityItems,
            AgentCommandShellPicker,
            AgentCommandText,
            AgentCommandPreviewButton,
            AgentCommandRunButton,
            AgentCommandRejectButton,
            AgentCommandStopButton,
            AgentCommandCopyButton,
            AgentCommandClearButton,
            AgentCommandUseHeldButton,
            AgentCommandApproveAllButton,
            AgentCommandApproveAllStatusText,
            AgentCommandAutoContinueButton,
            AgentCommandAutoContinueStatusText,
            AgentCommandApprovalText,
            AgentCommandRiskItems,
            AgentCommandOutputText,
            AgentCommandStatusText,
            AgentCommandSourceText,
            AgentCommandCopyOutputButton,
            AgentCommandCopyReceiptButton,
            AgentCommandWorkSummaryText,
            AgentCommandCopyBriefButton,
            AgentCommandStageVerifyButton,
            AgentCommandHistorySummaryText,
            AgentCommandHistoryItems,
            AgentCommandReplayLastButton,
            AgentCommandCopyHistoryButton,
            () => _lastRenderedSnapshot,
            ResourceBrush,
            status => SetApplicationStatus("agent.workspace", "Agent", status, "agent"),
            AgentCommandStageArtifactButton,
            AgentCommandStageNextButton,
            AgentOutputSummaryText,
            AgentOutputItems,
            (type, message, data) => _controlPlaneEvents.Publish(type, message, data),
            runbookMetaText: AgentRunbookMetaText,
            composerDraftStore: _composerDraftStore);
        _agentImpactExplorerCoordinator = new AgentImpactExplorerCoordinator(
            AgentImpactExplorerExpander,
            AgentImpactStatusText,
            AgentImpactSolutionPicker,
            AgentImpactTargetText,
            AgentImpactAnalyzeButton,
            AgentImpactAnalyzeChangesButton,
            AgentImpactSummaryText,
            AgentImpactItems,
            AgentImpactStageTestsButton,
            AgentImpactCopyButton,
            () => AgentWorkspace.CurrentWorkspacePath,
            () => AgentWorkspace.CurrentChangedRelativePaths,
            (command, shell) => AgentWorkspace.ControlStageCommand(command, shell),
            ResourceBrush);
        _scenarioWorkflowCoordinator = new ScenarioWorkflowCoordinator(
            this,
            _matchGeneration,
            _wpfSettingsStore,
            RandomSeedPresetPicker,
            RandomSeedRolePackPicker,
            RandomSeedStylePicker,
            RandomSeedIntensityPicker,
            RandomSeedAbsurdityPicker,
            SetupReadinessStatusText,
            SetupReadinessExpander,
            SetupReadinessBadgeItems,
            SetupReadinessChecklistItems,
            CopyCurrentSetupBriefButton,
            CopyCurrentSetupSpecButton,
            GenerationPresetStatusText,
            RandomSeedButton,
            AiChoiceButton,
            CurrentTopicsButton,
            YoloScenarioButton,
            GenerationHistoryFilterPicker,
            GenerationHistoryPicker,
            GenerationHistoryStatusText,
            ReplayGenerationButton,
            ReplayNewRunButton,
            CopyGenerationSeedButton,
            CopyGenerationBriefButton,
            CopyGenerationSpecButton,
            CopyGenerationDiffButton,
            CopyGenerationRubricButton,
            () => _wpfSettings,
            () => _activeSession,
            () => _theme,
            ResourceBrush,
            BlendBrush,
            () => _isRenderingSnapshot,
            () => _arenaBusy,
            RunArenaBusyForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            SetLoadStatus,
            SetArenaRunStatus,
            RunArenaBusyForCoordinatorAsync,
            status => SetApplicationStatus("app.match-setup-copy", "Match Setup", status, "arena"));
        _operatorTurnCoordinator = new OperatorTurnCoordinator(
            _coreSessionStore,
            _eventLogStore,
            _transcriptService,
            _narratorService,
            _discourseDiagnostics,
            _wpfSettingsStore,
            OperatorPublicRouteButton,
            OperatorPrivateRouteButton,
            OperatorNarratorRouteButton,
            OperatorPrivateTargetRow,
            OperatorPrivateTargetPicker,
            OperatorPrivateTargetSummaryText,
            OperatorRouteHintText,
            OperatorTurnMeterText,
            OperatorQuickInterventionHintText,
            [
                OperatorQuickInterventionAButton,
                OperatorQuickInterventionBButton,
                OperatorQuickInterventionCButton,
                OperatorQuickInterventionDButton
            ],
            OperatorTemplatePicker,
            UseOperatorTemplateButton,
            SaveOperatorTemplateButton,
            DeleteOperatorTemplateButton,
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
            SetArenaRunStatus,
            SpeakNarratorMessage,
            _composerDraftStore);
        _internetWorkflowCoordinator = new InternetWorkflowCoordinator(
            UseInternetCheckBox,
            InternetHintText,
            InternetBackendStatusText,
            TestInternetButton,
            InternetDiagnosticResultText,
            ResourceBrush,
            persistInternetSettingAsync: PersistInternetSettingForActiveSessionAsync,
            statusCenter: ShellTopBar.Presentation.StatusCenter,
            statusIdentity: () => new ApplicationStatusIdentity(_activeSession?.Id ?? ""));
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
            FormatDuration,
            FormatCompactNumber,
            TranscriptExportCoordinator.CopyMessage,
            TranscriptMutations.TogglePinMessageAsync,
            message => ArenaRun.RetryTranscriptMessageAsync(message),
            TranscriptMutations.DeleteMessageAsync,
            TranscriptExportCoordinator.CopyInternetUrl,
            IsAgentSpeaker,
            () => _wpfSettings.TurnCompareMode,
            message => TranscriptInsight.IsTurnSelectedForCompare(message),
            TranscriptInsightCoordinator.CanCompareMessage,
            message => TranscriptInsight.ToggleTurnCompareMessage(message),
            CanSpeakTranscriptMessage,
            SpeakTranscriptMessage,
            () => _wpfSettings.AllowDebugControls && _wpfSettings.ShowTranscriptInternetDetails,
            openModelsRecoveryAsync: OpenModelsRecoveryAsync,
            startSessionForkAsync: () => _savedStateCoordinator.ForkCurrentAsync(),
            skipBlockedTurnAsync: SkipContextBlockedTurnAsync,
            continueOutputAsync: ContinueTruncatedOutputAsync,
            endMatchAsync: EndMatchAfterContextLimitAsync,
            openOutputSettingsAsync: OpenOutputSettingsRecoveryAsync);
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
            status => SetApplicationStatus("agent.memory", "Agent", status, "agent"));
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
            IsAgentSpeaker,
            SpeakNarratorMessage,
            RunArenaBusyForCoordinatorAsync);
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
            status => SetApplicationStatus("agent.board", "Agent", status, "agent"));
        ShellNavigation.ApplyTheme(_wpfSettings.ThemeId, persist: false, rerender: false);
        ShellNavigation.InitializeThemePicker();
        _agentWorkspaceCoordinator.Initialize();
        _collaborateCoordinator = new CollaborateCoordinator(
            null,
            Dispatcher,
            CollaborateChatScrollViewer,
            CollaborateMessageItems,
            CollaboratePromptText,
            CollaboratePlanPromptButton,
            CollaborateCritiquePromptButton,
            CollaborateShipPromptButton,
            CollaborateExplainPromptButton,
            CollaboratePromptBudgetText,
            CollaborateContextReceiptButton,
            CollaborateSendButton,
            CollaborateStopButton,
            CollaborateClearButton,
            CollaborateModePicker,
            CollaborateRoundsPicker,
            CollaborateStatusText,
            CollaborateProviderText,
            CollaborateTopProviderValue,
            CollaborateTopModeValue,
            CollaborateTopTeamValue,
            CollaborateParticipantItems,
            CollaborateRecentItems,
            CollaborateNewChatButton,
            CollaborateProviderSettingsButton,
            CollaborateToolDocumentItems,
            CollaborateAddDocumentButton,
            CollaborateClearDocumentsButton,
            CollaborateCalculatorText,
            CollaborateRunCalculatorButton,
            CollaborateClearCalculationsButton,
            CollaborateCalculationItems,
            CollaborateMemoryText,
            CollaborateSaveMemoryButton,
            CollaborateClearMemoryButton,
            CollaborateMemoryItems,
            () => _lastRenderedSnapshot,
            ResourceBrush,
            status => SetApplicationStatus("collaborate.run", "Collaborate", status, "collaborate"),
            composerDraftStore: _composerDraftStore);
        _collaborateCoordinator.Initialize();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _refreshTimer.Tick += (_, _) => RefreshIfSnapshotChanged();
        _modelRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _modelRefreshTimer.Tick += async (_, _) =>
        {
            if (AppSettingsPanel.Visibility != Visibility.Visible || !ModelProviderSettingsExpander.IsExpanded)
            {
                return;
            }

            await RunTrackedBackgroundOperationSafelyAsync(
                "provider model refresh",
                cancellationToken => RefreshAdvertisedModelsAsync(cancellationToken: cancellationToken));
        };
        _appSettingsCoordinator = new AppSettingsCoordinator(
            Dispatcher,
            ShellNavigation,
            _modelRefreshTimer,
            () => AppSettingsPanel.Visibility == Visibility.Visible,
            force => RunTrackedBackgroundOperationSafelyAsync(
                "provider model refresh",
                cancellationToken => RefreshAdvertisedModelsAsync(force, cancellationToken)),
            ModelProviderSettingsExpander,
            ProviderBaseUrlText,
            ProviderModelText,
            TestProviderButton,
            SettingsGearRotate);
        _shellOverlayControlService = new ShellOverlayControlService(
            BuildMatchSetupControlState,
            OpenMatchSetupFromControlPlane,
            CloseMatchSetupFlyout,
            ShowMatchSetupSection,
            BuildSettingsControlState,
            () => AppSettingsWorkflow.SetVisible(true),
            CloseAppSettings,
            query => SettingsSearchText.Text = query);
        _appPreferenceControlService = new AppPreferenceControlService(
            _wpfSettingsStore,
            () => _wpfSettings,
            ApplyPreferenceControlChanges,
            BuildSettingsControlState);
        _settingsControlHandler = new AIArenaSettingsControlHandler(
            _shellOverlayControlService,
            _appPreferenceControlService,
            _controlPlaneEvents);
        _providerHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _providerHealthTimer.Tick += async (_, _) => await RunTrackedBackgroundOperationSafelyAsync(
            "provider health refresh",
            cancellationToken => ProviderReachability.RefreshAsync(cancellationToken: cancellationToken));
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
            ApplyProviderStatusProjection,
            (force, cancellationToken) => RefreshAdvertisedModelsAsync(force, cancellationToken),
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
            BattleReviewCheckBox,
            MemoryNotesCheckBox,
            DecisionCardCheckBox,
            AutoModeratorCheckBox,
            DebugControlsCheckBox,
            StyleFitCheckBox,
            VoiceDriftEnforcementCheckBox,
            TranscriptInternetDetailsCheckBox,
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
            TranscriptDiagnosticsGrid,
            TranscriptTelemetryHost,
            TranscriptTelemetryGrid,
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
            BlendBrush,
            () => _wpfSettings.AgentPerformanceFullCards);
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
            snapshot => AgentPerformance.ObserveSnapshot(snapshot),
            snapshot => ProviderReachability.UpdatePopup(snapshot));
        _diagnosticsWorkflowCoordinator = new DiagnosticsWorkflowCoordinator(
            _discourseDiagnostics,
            TranscriptDiagnosticsGrid,
            TranscriptDiagnosticsEmptyState,
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
            FollowChatCheckBox,
            ShellCards,
            TranscriptActions,
            TranscriptSearch,
            TranscriptInsight,
            TranscriptCards,
            TranscriptAdjunct,
            AgentMemory,
            MatchQualityTimeline,
            () => _wpfSettings,
            () => _lastRenderedSnapshot,
            messages => _lastRenderedMessages = messages,
            () => _transcriptViewCoordinator?.IsDiagnosticsDisplayed() == true,
            messages => DiagnosticsWorkflow.Update(messages),
            ShouldShowDecisionCard,
            IsAgentSpeaker,
            ResourceBrush,
            AccentForSpeaker,
            ShortModelName,
            DisplayStatusValue,
            () => ArenaRun.RunOneTurnAsync(),
            ShowCustomMatchPanel,
            () => OpenModelProviderSettings(),
            ClearTranscriptFilters);
        _matchSetupCoordinator = new MatchSetupCoordinator(
            _coreSessionStore,
            _eventLogStore,
            RivalryMatrixEnabledCheckBox,
            RivalryMatrixRows,
            RivalryMatrixPreviewItems,
            RivalryMatrixInsightText,
            RivalryMatrixStatusText,
            ApplyRivalryMatrixButton,
            ClearRivalryMatrixButton,
            RivalryMatrixPatternPicker,
            ApplyRivalryMatrixPatternButton,
            ApplyMatchSetupToModelsCheckBox,
            ApplyMatchSetupToModelsStatusText,
            () => _activeSession,
            ResourceBrush,
            AccentForSpeaker,
            DisplayStatusValue,
            BlendBrush,
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
            (mode, message) => _controlPlaneEvents.Publish(
                "match.model-behavior.changed",
                message,
                new { mode }));
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
        _matchSetupControlHandler = new AIArenaMatchSetupControlHandler(
            _shellOverlayControlService,
            count => AgentRoster.ResizeAgentCountAsync(count),
            _rivalryMatrixControlService,
            _matchSetupPortabilityService,
            (factoryMode, cancellationToken) => MatchSetup.SetModelBehaviorModeAsync(factoryMode, cancellationToken),
            _controlPlaneEvents);
        _collaborateControlHandler = new AIArenaCollaborateControlHandler(Collaborate, _controlPlaneEvents);
        _providerControlHandler = new AIArenaProviderControlHandler(
            _providerConfigurationControlService,
            _providerRuntimeService,
            () => _activeSession,
            RefreshActiveSessionForProviderAsync,
            _controlPlaneEvents);
        _arenaSessionMutationCoordinator = new ArenaSessionMutationCoordinator(
            this,
            _coreSessionStore,
            _eventLogStore,
            ProviderTimeoutText,
            ProviderTemperatureText,
            ProviderMaxOutputText,
            ContextTranscriptWindowText,
            ContextPrivateWindowText,
            ContextNotesWindowText,
            ProviderTestStatus,
            ResetButton,
            () => _activeSession,
            () => _isRenderingSnapshot,
            () => _theme,
            preferredSessionId => LoadSessionsAsync(preferredSessionId),
            RunArenaBusyForCoordinatorAsync,
            SaveSnapshotForCoordinatorAsync,
            RefreshActiveSessionForCoordinatorAsync,
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
                UnloadSelectedModelsButton,
                DownloadModelText,
                DownloadQuantizationPicker,
                DownloadModelButton,
                ApplySettingsButton,
                ProviderBaseUrlText,
                ProviderApiModePicker,
                ProviderApiTokenBox,
                ProviderModelText,
                AlphaRoleModelText,
                BetaRoleModelText,
                GammaRoleModelText,
                DeltaRoleModelText,
                NarratorRoleModelText,
                ProviderTimeoutText,
                ProviderTemperatureText,
                ProviderMaxOutputText,
                ProviderContextLengthText,
                ProviderReasoningPicker,
                ProviderNativeStatefulChatCheckBox,
                ProviderNativeIdleTtlText,
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
            value =>
            {
                _arenaBusy = value;
                // A run started or finished, so the polling cadence may change
                // even though window focus did not.
                ApplyPollingCadence();
                RefreshProviderModelsPresentationIfVisible();
            },
            () => _arenaRunCoordinator?.IsAutoChatRunning == true,
            (busy, autoChatRunning) => ScenarioWorkflow.UpdateBusyState(busy, autoChatRunning),
            (busy, autoChatRunning) =>
            {
                InternetWorkflow.UpdateBusyState(busy, autoChatRunning);
            },
            (busy, autoChatRunning) => OperatorTurn.UpdateBusyState(busy, autoChatRunning),
            busy => AgentRoster.UpdateBusyState(busy),
            () => SavedStateCoordinator.UpdateActionButtons(),
            busy => AgentBoard.UpdateBusyState(busy),
            busy => TranscriptActions.UpdateBusyState(busy),
            busy => MatchLock.UpdateBusyState(busy),
            busy => MatchSetup.UpdateBusyState(busy),
            () =>
            {
                _providerSettingsCoordinator?.UpdateNativeLifecycleControls();
                _llamaCppRuntimeCoordinator?.UpdateBusyState();
                _arenaEvaluationCoordinator?.RefreshAvailability();
                RefreshSessionSettingsPendingState();
            },
            readinessStatus: ArenaControlReadinessText);
        InitializeAboutPanel();
        InitializeVisualSettings();
        ApplyLabViewMode(_wpfSettings.LabViewMode, persist: false);
        ApplyRightRailCollapsed();
        InitializeVoiceTtsSettings();
        ScenarioWorkflow.InitializeControls();
        OperatorTurn.InitializeControls();
        InternetWorkflow.InitializeControls();
        DiagnosticsWorkflow.InitializeTiles();
        SavedStateCoordinator.LoadScenarioTemplates();
        ShowStoreLoadWarningIfAny();
        if (AIArenaControlPlaneProtocol.TryCurrentEndpoint(
                out var controlPipeName,
                out var controlTokenPath,
                out _controlPlaneInitializationError))
        {
            _controlPlaneHost = new AIArenaControlPlaneHost(
                this,
                _controlPlaneEvents,
                controlPipeName,
                controlTokenPath);
            _ = RefreshControlPlaneHostAsync();
        }
        else
        {
            ApplyControlPlaneToggleState();
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "app.control-plane",
                "App",
                ApplicationStatusState.Warning,
                "PowerShell control plane is unavailable.",
                _controlPlaneInitializationError,
                "settings",
                lifetime: ApplicationStatusLifetime.UntilResolved);
        }
        SystemThemePreferences.PreferenceChanged += OnSystemThemePreferenceChanged;
        SystemMotionPreferences.PreferenceChanged += OnSystemMotionPreferenceChanged;
        Loaded += (_, _) =>
        {
            ArmShellEvents();
            LoadSessions();
            _ = InitializeExperimentExtensionsSafelyAsync();
            _refreshTimer.Start();
            _providerHealthTimer.Start();
            TelemetryWorkflow.UpdateTimerState();
            _ = RunTrackedBackgroundOperationSafelyAsync(
                "initial provider health refresh",
                cancellationToken => ProviderReachability.RefreshAsync(force: true, cancellationToken));
        };
        SourceInitialized += (_, _) => WindowChromeService.ApplyThemeChromeColor(this, _theme);
        StateChanged += (_, _) => ApplyMaximizedChromePadding();
        Activated += (_, _) => ApplyPollingCadence();
        Deactivated += (_, _) => ApplyPollingCadence();
        _voiceNarrationService.SpeakingChanged += () => Dispatcher.BeginInvoke(UpdateVoiceToggleButton);
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _appControlHandler.ResetProcessOverrides();
            SystemThemePreferences.PreferenceChanged -= OnSystemThemePreferenceChanged;
            SystemMotionPreferences.PreferenceChanged -= OnSystemMotionPreferenceChanged;
            _refreshTimer.Stop();
            _modelRefreshTimer.Stop();
            _providerHealthTimer.Stop();
            _voiceTtsSettingsSaveDebouncer.Flush();
            _voiceTtsSettingsSaveDebouncer.Dispose();
            TelemetryWorkflow.Stop();
            _transcriptSearchCoordinator?.Dispose();
            DisposeProviderModelsHeartbeat();
            _providerModelsSurfaceCoordinator?.Dispose();
            _agentImpactExplorerCoordinator?.Dispose();
            _agentInspectionLabCoordinator.Dispose();
            _faultInjectionLabCoordinator.Dispose();
            _modelRoutingOptimizerCoordinator.Dispose();
            _inAppQaInspectorCoordinator.Dispose();
            _experimentLabCoordinator?.Dispose();
            _agentWorkspaceCoordinator?.Dispose();
            _voiceNarrationService.Dispose();
            _matchGeneration.Dispose();
            _narratorService.Dispose();
            _internetToolService.Dispose();
            InternetWorkflow.Dispose();
            _userGuideWindowHost.AppRouteRequested -= UserGuideWindowHost_AppRouteRequested;
            _userGuideWindowHost.Close();
            _applicationStatusControlPublisher.Dispose();
            _controlPlaneHost?.Dispose();
        };
    }

    internal static ModelProviderClient CreateObservedModelProviderClient(
        ProviderRequestTraceStore traceStore,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(traceStore);
        return new ModelProviderClient(httpClient, traceStore);
    }

    internal static void RegisterProductionExperimentFeatures(
        ExperimentLabControl host,
        AgentInspectionLabControl inspection,
        FaultInjectionLabControl faultInjection,
        ModelRoutingOptimizerControl routingOptimizer,
        InAppQaInspectorControl qaInspector)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(faultInjection);
        ArgumentNullException.ThrowIfNull(routingOptimizer);
        ArgumentNullException.ThrowIfNull(qaInspector);
        foreach (var registration in inspection.DetachFeatureRegistrations())
        {
            host.RegisterFeature(registration);
        }
        host.RegisterFeature(faultInjection.FeatureRegistration);
        host.RegisterFeature(routingOptimizer.FeatureRegistration);
        host.RegisterFeature(qaInspector.FeatureRegistration);
    }

    private void RegisterProductionExperimentRefreshes()
    {
        ExperimentLab.RegisterFeatureRefresh(
            AgentInspectionLabControl.PromptInspectorFeatureKey,
            _ =>
            {
                _agentInspectionLabCoordinator.RefreshPromptTraces();
                return Task.CompletedTask;
            });
        ExperimentLab.RegisterFeatureRefresh(
            AgentInspectionLabControl.MemoryDebuggerFeatureKey,
            async cancellationToken =>
            {
                await _agentInspectionLabCoordinator.InitializeAsync(cancellationToken);
            });
        ExperimentLab.RegisterFeatureRefresh(
            "routing-optimizer",
            async cancellationToken =>
            {
                await _modelRoutingOptimizerCoordinator.RefreshEvidenceAsync(cancellationToken);
            });
        ExperimentLab.RegisterFeatureRefresh(
            "in-app-qa-inspector",
            async cancellationToken =>
            {
                await _inAppQaInspectorCoordinator.RefreshAsync(cancellationToken: cancellationToken);
            });
    }

    private async Task InitializeExperimentExtensionsSafelyAsync()
    {
        try
        {
            await _agentInspectionLabCoordinator.InitializeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Experiment inspection initialization failed safely: {exception.GetType().Name}");
        }

        try
        {
            await _inAppQaInspectorCoordinator.InitializeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"QA Inspector initialization failed safely: {exception.GetType().Name}");
        }
    }

    internal static string ResolveQaRepositoryRoot(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var candidate = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for (var depth = 0; candidate is not null && depth < 16; depth++, candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "src", "AIArena.Wpf", "AIArena.Wpf.csproj"))
                && File.Exists(Path.Combine(candidate.FullName, "scripts", "qa-seal.ps1")))
            {
                return candidate.FullName;
            }
        }

        // Installed builds do not contain repository scripts. Pointing the
        // inspector at the existing application directory yields an explicit
        // unavailable result instead of guessing a repository elsewhere.
        return Path.GetFullPath(baseDirectory);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress || !TryBeginShutdownAttempt(ref _shutdownAttemptInProgress))
        {
            return;
        }

        PreserveCurrentSessionSettingsDraft();
        if (HasPendingSessionSettings()
            && !ConfirmDialog.Show(
                this,
                _theme,
                "Unapplied session changes",
                "Advanced model-call or context-window edits have not been applied. Exiting now will discard those drafts.",
                "Discard and exit",
                "Keep app open",
                ConfirmDialogTone.Danger))
        {
            EndShutdownAttempt(ref _shutdownAttemptInProgress);
            return;
        }

        var draftsSaved = await FlushComposerDraftsForShutdownAsync(dispose: false);
        if (!draftsSaved)
        {
            var presentation = AppErrorPresenter.Present(new IOException(), AppErrorContext.Settings);
            var status = "Unsent drafts could not be protected and saved. "
                + $"Free disk space or copy the visible text before trying again. Code: {presentation.Code}.";
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "app.composer-drafts",
                "App",
                ApplicationStatusState.Failed,
                status,
                presentation.CopyDetails,
                identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
                lifetime: ApplicationStatusLifetime.UntilResolved);
            var discardConfirmed = ConfirmDialog.Show(
                this,
                _theme,
                "Unsent drafts were not saved",
                status + " Exit only if you accept losing those unsent drafts.",
                "Exit without saving drafts",
                "Keep app open",
                ConfirmDialogTone.Danger);
            if (!ShouldContinueShutdownAfterDraftFlush(draftsSaved, () => discardConfirmed))
            {
                EndShutdownAttempt(ref _shutdownAttemptInProgress);
                return;
            }
        }
        else
        {
            ShellTopBar.Presentation.StatusCenter.Resolve("app.composer-drafts");
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        AgentWorkspaceCommand.BeginApplicationShutdown();
        _refreshTimer.Stop();
        _modelRefreshTimer.Stop();
        _providerHealthTimer.Stop();
        _arenaOperationCoordinator?.RequestShutdown();
        try
        {
            if (_arenaRunCoordinator is not null)
            {
                try
                {
                    await _arenaRunCoordinator.StopAutoChatAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Auto-chat shutdown failed with {ex.GetType().Name}.");
                }
            }

            if (_arenaOperationCoordinator is not null)
            {
                try
                {
                    await _arenaOperationCoordinator.DrainAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Arena-operation shutdown failed with {ex.GetType().Name}.");
                }
            }

            if (_controlPlaneHost is not null)
            {
                try
                {
                    _controlPlaneHost.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Control-plane disposal failed with {ex.GetType().Name}.");
                }

                try
                {
                    await _controlPlaneHost.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Control-plane shutdown failed with {ex.GetType().Name}.");
                }
            }
        }
        finally
        {
            await FlushComposerDraftsForShutdownAsync(dispose: true);
            _shutdownReady = true;
            _shutdownInProgress = false;
            EndShutdownAttempt(ref _shutdownAttemptInProgress);
            ScheduleCloseAfterCleanup(Dispatcher, Close);
        }
    }

    private async Task<bool> FlushComposerDraftsForShutdownAsync(bool dispose)
    {
        var flushed = false;
        try
        {
            _operatorTurnCoordinator?.CaptureDraftForShutdown();
            _agentWorkspaceCoordinator?.CaptureDraftForShutdown();
            _collaborateCoordinator?.CaptureDraftForShutdown();
            flushed = await _composerDraftStore.FlushAsync();
            if (!flushed)
            {
                Debug.WriteLine($"Composer draft shutdown flush failed safely: {_composerDraftStore.LastDiagnosticCode}.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Composer draft shutdown flush failed safely: {ex.GetType().Name}.");
        }

        if (dispose)
        {
            try
            {
                await _composerDraftStore.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Composer draft shutdown disposal failed safely: {ex.GetType().Name}.");
            }
        }

        return flushed;
    }

    internal static bool ShouldContinueShutdownAfterDraftFlush(
        bool flushSucceeded,
        Func<bool> confirmDiscard)
    {
        ArgumentNullException.ThrowIfNull(confirmDiscard);
        return flushSucceeded || confirmDiscard();
    }

    internal static bool TryBeginShutdownAttempt(ref bool shutdownAttemptInProgress)
    {
        if (shutdownAttemptInProgress)
        {
            return false;
        }

        shutdownAttemptInProgress = true;
        return true;
    }

    internal static void EndShutdownAttempt(ref bool shutdownAttemptInProgress) =>
        shutdownAttemptInProgress = false;

    internal static void ScheduleCloseAfterCleanup(Dispatcher dispatcher, Action close)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(close);

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(close, DispatcherPriority.Normal);
    }

    private void InitializeAboutPanel()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        AboutVersionText.Text = $"Version {CleanDisplayVersion(version)}";
        OpenReleasesButton.ToolTip = ReleasesUrl;
    }


    private AIArenaMatchSetupControlState BuildMatchSetupControlState()
    {
        var snapshot = _lastRenderedSnapshot;
        return new AIArenaMatchSetupControlState(
            CustomMatchPanel.Visibility == Visibility.Visible,
            _matchSetupSection,
            _matchSetupReturnSurface.ToString().ToLowerInvariant(),
            _activeSession?.Id ?? "",
            snapshot?.MatchType ?? "",
            snapshot?.ScenarioTopic ?? "",
            snapshot?.Agents.Count(agent => agent.Active) ?? 0,
            _arenaBusy)
        {
            ModelBehaviorMode = snapshot?.FactoryMode == true ? "factory" : "arena"
        };
    }

    private AIArenaSettingsControlState BuildSettingsControlState()
    {
        return new AIArenaSettingsControlState(
            AppSettingsPanel.Visibility == Visibility.Visible,
            SettingsSearchText.Text.Trim(),
            _wpfSettings.ThemeId,
            _wpfSettings.CompactTranscriptMode,
            _wpfSettings.FollowTranscript,
            _wpfSettings.TopStripMode,
            _wpfSettings.TurnCompareMode,
            _wpfSettings.ShowMatchQualityTimeline,
            _wpfSettings.ShowBattleReview,
            _wpfSettings.ShowAgentMemoryNotes,
            _wpfSettings.ShowDecisionCard,
            _wpfSettings.ShowAutoModerator,
            _wpfSettings.ShowStyleFit,
            _wpfSettings.ShowTranscriptInternetDetails,
            _wpfSettings.RightRailCollapsed,
            _wpfSettings.AllowDebugControls,
            IsWorldDebugEnabled(_wpfSettings),
            IsAgentWorkspaceEnabled(_wpfSettings),
            IsControlPlaneEnabled,
            _wpfSettings.VoiceTtsEnabled);
    }

    private object BuildControlPlaneStateSummary()
    {
        var railCollapsed = IsRightRailEffectivelyCollapsed(
            _wpfSettings.RightRailCollapsed,
            _rightRailAutoCollapseActive,
            _rightRailNarrowRevealRequested,
            _rightRailWidthCollapseLatched);
        var provider = BuildProviderControlState();
        var applicationStatus = AIArenaApplicationStatusControlProjection.Project(
            ShellTopBar.Presentation.StatusCenter.Snapshot);
        return new
        {
            View = SelectedControlPlaneView(),
            Theme = _wpfSettings.ThemeId,
            SessionId = _activeSession?.Id ?? "",
            ArenaStatus = ArenaRunStatus.Text,
            Status = applicationStatus,
            InternetEnabled = InternetWorkflow.IsEnabled,
            RightRail = railCollapsed ? "collapsed" : "expanded",
            MatchSetupOpen = CustomMatchPanel.Visibility == Visibility.Visible,
            MatchSetupSection = _matchSetupSection,
            GenerationHistoryCount = _lastRenderedSnapshot?.GenerationHistory.Count ?? 0,
            SettingsOpen = AppSettingsPanel.Visibility == Visibility.Visible,
            SettingsQuery = SettingsSearchText.Text.Trim(),
            ProviderOnline = provider.Online,
            ProviderModel = provider.Model,
            AgentStatus = AgentWorkspace.ControlState.Status,
            AgentRunbookId = AgentWorkspace.ControlRunbookId,
            AgentRunbookStatus = AgentWorkspace.ControlRunbookStatus,
            CollaborateStatus = Collaborate.ControlState.Status
        };
    }

    private AIArenaAgentCommandControlState BuildAgentCommandControlState()
    {
        var state = AgentWorkspace.ControlState;
        return new AIArenaAgentCommandControlState(
            state.Command,
            state.CommandSource,
            state.CommandStatus,
            state.CanApprove,
            state.CanReject,
            state.CanStopCommand);
    }

    private AIArenaAgentWorkControlState BuildAgentWorkControlState()
    {
        var state = AgentWorkspace.ControlState;
        return new AIArenaAgentWorkControlState(
            state.Workspace,
            state.Status,
            state.LatestWorkBrief,
            state.BuildEvidence,
            state.ArtifactSuggestion,
            state.ArtifactVerification);
    }

    private AIArenaAgentOutputControlState BuildAgentOutputControlState()
    {
        var state = AgentWorkspace.ControlState;
        return new AIArenaAgentOutputControlState(
            state.OutputSummary,
            state.ArtifactSuggestion,
            state.ArtifactVerification);
    }

    private AIArenaProviderControlState BuildProviderControlState()
    {
        var snapshot = _lastRenderedSnapshot;
        if (snapshot is null)
        {
            return ProviderConfigurationControlService.EmptyState(_activeSession?.Id ?? "");
        }

        var roleIds = snapshot.Agents
            .Where(agent => agent.Active)
            .Select(agent => agent.Id.Trim().ToLowerInvariant())
            .Append("narrator")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roles = roleIds.Select(role =>
        {
            var configuredModel = snapshot.ExplicitRoleModels.TryGetValue(role, out var explicitModel)
                ? explicitModel
                : "";
            var effectiveModel = !string.IsNullOrWhiteSpace(configuredModel)
                ? configuredModel
                : snapshot.DefaultForUnassignedAgentsEnabled
                    ? snapshot.ProviderModel
                    : "";
            var generationOverride = snapshot.RoleOverrides.TryGetValue(role, out var roleOverride)
                ? roleOverride
                : null;
            return new AIArenaProviderRoleControlState(
                role,
                configuredModel,
                effectiveModel,
                snapshot.DefaultForUnassignedAgentsEnabled
                    && string.IsNullOrWhiteSpace(configuredModel)
                    && !string.IsNullOrWhiteSpace(snapshot.ProviderModel),
                generationOverride?.Temperature,
                generationOverride?.MaxOutputTokens);
        }).ToArray();
        string Effective(string role) => roles.FirstOrDefault(item =>
            item.Id.Equals(role, StringComparison.OrdinalIgnoreCase))?.EffectiveModel ?? "";
        var advertisedModels = _providerSettingsCoordinator?.AdvertisedModels ?? [];
        return new AIArenaProviderControlState(
            snapshot.ProviderOnline,
            snapshot.ProviderModel,
            Effective("alpha"),
            Effective("beta"),
            Effective("gamma"),
            Effective("delta"),
            Effective("narrator"),
            ProviderConfigurationControlService.SanitizeError(snapshot.ProviderLastError, snapshot.ProviderApiToken))
        {
            SessionId = snapshot.SessionId,
            Configured = !string.IsNullOrWhiteSpace(snapshot.ProviderBaseUrl),
            BaseUrl = ProviderConfigurationControlService.SanitizeBaseUrl(snapshot.ProviderBaseUrl),
            ApiMode = ModelProviderApiModes.Normalize(snapshot.ProviderApiMode),
            ApiTokenConfigured = !string.IsNullOrEmpty(snapshot.ProviderApiToken),
            TimeoutSeconds = snapshot.ProviderTimeout,
            Temperature = snapshot.ProviderTemperature,
            MaxOutputTokens = snapshot.ProviderMaxOutputTokens,
            ContextLength = snapshot.ProviderContextLength,
            Reasoning = string.IsNullOrWhiteSpace(snapshot.ProviderReasoning) ? "default" : snapshot.ProviderReasoning,
            NativeStatefulChat = snapshot.ProviderNativeStatefulChat,
            NativeIdleTtlSeconds = snapshot.ProviderNativeIdleTtlSeconds,
            DefaultForUnassignedAgentsEnabled = snapshot.DefaultForUnassignedAgentsEnabled,
            LastTestOk = snapshot.ProviderOnline,
            LastLatencyMs = snapshot.ProviderLastLatencyMs,
            LastHealthCheckedAt = _providerSettingsCoordinator?.LastProviderHealthCheckedAt,
            LastModelListCheckedAt = _providerSettingsCoordinator?.LastModelListCheckedAt,
            AdvertisedModelCount = _providerSettingsCoordinator is { LastProviderModelCount: >= 0 } settings
                ? settings.LastProviderModelCount
                : null,
            AdvertisedModels = advertisedModels,
            Roles = roles
        };
    }

    private AIArenaTranscriptExportControlState BuildTranscriptControlExport()
    {
        var sessionId = _activeSession?.Id ?? "";
        var messages = _lastRenderedMessages
            .OrderBy(message => message.Turn)
            .ToArray();
        var markdown = string.IsNullOrWhiteSpace(sessionId) || messages.Length == 0
            ? ""
            : TranscriptExportCoordinator.BuildTranscriptExport(sessionId, messages);
        return new AIArenaTranscriptExportControlState(sessionId, messages.Length, markdown);
    }

    private AIArenaSessionExportControlState BuildSessionControlExport()
    {
        return new AIArenaSessionExportControlState(
            _activeSession?.Id ?? "",
            ShellTopBar.Presentation.StatusCenter.AppStatus,
            SelectedControlPlaneView(),
            _lastRenderedSnapshot?.ProviderModel ?? "",
            _lastRenderedMessages.Count,
            AgentWorkspace.ControlState,
            Collaborate.ControlState,
            AIArenaApplicationStatusControlProjection.Project(
                ShellTopBar.Presentation.StatusCenter.Snapshot));
    }

    private AIArenaReceiptExportControlState BuildReceiptControlExport()
    {
        var agent = AgentWorkspace.ControlState;
        var provider = BuildProviderControlState();
        var providerReadiness = provider.Online
            ? $"Online: {provider.Model}"
            : string.IsNullOrWhiteSpace(provider.LastError) ? "Offline" : $"Offline: {provider.LastError}";
        return new AIArenaReceiptExportControlState(
            _activeSession?.Id ?? "",
            agent.BuildEvidence,
            agent.OutputSummary,
            Collaborate.ControlState.Status,
            providerReadiness);
    }

    private bool SelectControlPlaneView(string view)
    {
        switch (AIArenaControlPlaneProtocol.NormalizeCommand(view))
        {
            case "arena":
            case "lab":
            case "transcript":
                AppSettingsWorkflow.SetVisible(false);
                ShowTranscriptPanel(clearFilters: false);
                return true;
            case "custom-match":
            case "custom.match":
            case "match":
                OpenMatchSetupFromControlPlane();
                return true;
            case "models":
            case "provider.models":
                AppSettingsWorkflow.SetVisible(false);
                CloseNamedTransientShellFlyouts();
                ShowProviderModelsPanel();
                return true;
            case "world":
            case "ai.world":
                if (!IsWorldDebugEnabled(_wpfSettings))
                {
                    return false;
                }

                AppSettingsWorkflow.SetVisible(false);
                ShowWorldPanel();
                return true;
            case "agent":
                if (!IsAgentWorkspaceEnabled(_wpfSettings))
                {
                    return false;
                }

                AppSettingsWorkflow.SetVisible(false);
                ShowAgentPanel();
                return true;
            case "collaborate":
            case "ai.collaborate":
                AppSettingsWorkflow.SetVisible(false);
                ShowCollaboratePanel();
                return true;
            case "experiment":
            case "experiments":
            case "experiment.lab":
                if (!IsExperimentLabEnabled(_wpfSettings))
                {
                    return false;
                }

                AppSettingsWorkflow.SetVisible(false);
                ShowExperimentLabPanel();
                return true;
            case "settings":
                AppSettingsWorkflow.SetVisible(true);
                return true;
            case "provider":
            case "provider.settings":
                OpenModelProviderSettings();
                return true;
            default:
                return false;
        }
    }

    private string SelectedControlPlaneView()
    {
        if (AppSettingsPanel.Visibility == Visibility.Visible)
        {
            return "settings";
        }

        if (AgentWorkspacePanel.Visibility == Visibility.Visible)
        {
            return "agent";
        }

        if (CollaboratePanel.Visibility == Visibility.Visible)
        {
            return "collaborate";
        }

        if (ExperimentLabPanel.Visibility == Visibility.Visible)
        {
            return "experiment-lab";
        }

        if (CustomMatchPanel.Visibility == Visibility.Visible)
        {
            return "custom-match";
        }

        if (ProviderModelsPanel.Visibility == Visibility.Visible)
        {
            return "models";
        }

        return AgentWorldPanel.Visibility == Visibility.Visible ? "world" : "arena";
    }

    private static string RequiredStringArg(AIArenaControlRequest request, string name)
    {
        return AIArenaControlArguments.String(request, name);
    }

    private static string? OptionalStringArg(AIArenaControlRequest request, string name)
    {
        return AIArenaControlArguments.OptionalString(request, name);
    }

    private static bool OptionalBoolArg(AIArenaControlRequest request, string name)
    {
        return AIArenaControlArguments.TryOptionalBool(request, name, out var value)
            && value == true;
    }

    private async Task RefreshControlPlaneHostAsync()
    {
        var host = _controlPlaneHost;
        if (host is null)
        {
            return;
        }

        if (IsControlPlaneEnabled)
        {
            await host.StartIfEnabledAsync();
            if (IsControlPlaneEnabled && host.IsRunning)
            {
                _controlPlaneEvents.Publish("control.enabled", "AI Arena - Lite control plane enabled.");
            }

            return;
        }

        await host.StopAsync();
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
        _isUpdatingWorldDebug = true;
        _isUpdatingAgentWorkspace = true;
        _isUpdatingControlPlane = true;
        try
        {
            WorldDebugCheckBox.IsChecked = IsWorldDebugEnabled(_wpfSettings);
            AgentWorkspaceCheckBox.IsChecked = IsAgentWorkspaceEnabled(_wpfSettings);
            ControlPlaneCheckBox.IsChecked = IsControlPlaneEnabled;
        }
        finally
        {
            _isUpdatingWorldDebug = false;
            _isUpdatingAgentWorkspace = false;
            _isUpdatingControlPlane = false;
        }

        ApplyWorldDebugVisibility(persistIfForcedOff: false);
        ApplyExperimentLabVisibility();
        ApplyAgentWorkspaceVisibility();
        ApplyControlPlaneToggleState();
    }

    private void ApplyPreferenceControlChanges()
    {
        InitializeVisualSettings();
        InitializeVoiceTtsSettings();
        if (!_wpfSettings.VoiceTtsEnabled)
        {
            _voiceNarrationService.Stop();
        }

        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
    }

    private void InitializeVoiceTtsSettings()
    {
        _isUpdatingVoiceTtsSettings = true;
        try
        {
            VoiceTtsEnabledCheckBox.IsChecked = _wpfSettings.VoiceTtsEnabled;
            VoiceTtsAutoNarratorCheckBox.IsChecked = _wpfSettings.VoiceTtsAutoNarrator;
            PopulateVoiceTtsVoices();
            VoiceTtsRateSlider.Value = _wpfSettings.VoiceTtsRate;
            VoiceTtsVolumeSlider.Value = _wpfSettings.VoiceTtsVolume;
        }
        finally
        {
            _isUpdatingVoiceTtsSettings = false;
        }

        UpdateVoiceTtsUi("Voice TTS ready.");
    }

    private void PopulateVoiceTtsVoices()
    {
        var selected = _wpfSettings.VoiceTtsVoiceName;
        VoiceTtsVoicePicker.Items.Clear();
        VoiceTtsVoicePicker.Items.Add(new ComboBoxItem
        {
            Content = "Windows default voice",
            Tag = "",
            ToolTip = "Use the current Windows default speech voice"
        });

        foreach (var voiceName in _voiceNarrationService.InstalledVoiceNames())
        {
            VoiceTtsVoicePicker.Items.Add(new ComboBoxItem
            {
                Content = voiceName,
                Tag = voiceName,
                ToolTip = voiceName
            });
        }

        ShellUiHelpers.SelectComboTag(VoiceTtsVoicePicker, selected);
        if (VoiceTtsVoicePicker.SelectedIndex < 0)
        {
            ShellUiHelpers.SelectComboTag(VoiceTtsVoicePicker, "");
            _wpfSettings.VoiceTtsVoiceName = "";
        }
    }

    private void PersistVoiceTtsSettings(string status)
    {
        if (_isUpdatingVoiceTtsSettings || VoiceTtsStatusText is null)
        {
            return;
        }

        CaptureVoiceTtsSettings();
        _voiceTtsSettingsSaveDebouncer.Cancel();
        SaveVoiceTtsSettings(status);
        if (!_wpfSettings.VoiceTtsEnabled)
        {
            _voiceNarrationService.Stop();
        }
    }

    private void CaptureVoiceTtsSettings()
    {
        _wpfSettings.VoiceTtsEnabled = VoiceTtsEnabledCheckBox.IsChecked == true;
        _wpfSettings.VoiceTtsAutoNarrator = VoiceTtsAutoNarratorCheckBox.IsChecked == true;
        _wpfSettings.VoiceTtsVoiceName = ShellUiHelpers.SelectedComboTag(VoiceTtsVoicePicker, "");
        _wpfSettings.VoiceTtsRate = VoiceNarrationService.NormalizeRate((int)Math.Round(VoiceTtsRateSlider.Value));
        _wpfSettings.VoiceTtsVolume = VoiceNarrationService.NormalizeVolume((int)Math.Round(VoiceTtsVolumeSlider.Value));
    }

    private void SaveVoiceTtsSettings(string status)
    {
        try
        {
            _wpfSettingsStore.Save(_wpfSettings);
            UpdateVoiceTtsUi(status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VoiceTtsStatusText.Text = AppErrorPresenter
                .Present(ex, AppErrorContext.VoiceNarration)
                .DisplayText;
        }
    }

    private void UpdateVoiceTtsUi(string status)
    {
        var enabled = _wpfSettings.VoiceTtsEnabled;
        VoiceTtsAutoNarratorCheckBox.IsEnabled = enabled;
        VoiceTtsVoicePicker.IsEnabled = enabled;
        VoiceTtsRateSlider.IsEnabled = enabled;
        VoiceTtsVolumeSlider.IsEnabled = enabled;
        TestVoiceTtsButton.IsEnabled = enabled;
        VoiceTtsRateValueText.Text = _wpfSettings.VoiceTtsRate.ToString("+#;-#;0");
        VoiceTtsVolumeValueText.Text = $"{_wpfSettings.VoiceTtsVolume}%";
        VoiceTtsStatusText.Text = enabled
            ? status
            : "Local Windows voice playback is off.";
    }

    private VoiceNarrationOptions CurrentVoiceNarrationOptions()
    {
        return new VoiceNarrationOptions(
            _wpfSettings.VoiceTtsVoiceName,
            _wpfSettings.VoiceTtsRate,
            _wpfSettings.VoiceTtsVolume);
    }

    private void SpeakNarratorMessage(DialogueMessage message)
    {
        if (!_wpfSettings.VoiceTtsEnabled || !_wpfSettings.VoiceTtsAutoNarrator)
        {
            return;
        }

        if (!message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SpeakVoiceTts(message.Text, $"Reading narrator turn {message.Turn}.");
    }

    private void SpeakVoiceTts(string text, string startedStatus)
    {
        if (!_wpfSettings.VoiceTtsEnabled)
        {
            var disabledStatus = "Voice TTS is off. Enable it in Settings > Voice TTS.";
            UpdateVoiceTtsUi(disabledStatus);
            SetArenaRunStatus(disabledStatus);
            return;
        }

        var result = _voiceNarrationService.Speak(text, CurrentVoiceNarrationOptions());
        var status = result.Ok ? $"{startedStatus} {result.Status}" : result.Status;
        UpdateVoiceTtsUi(status);
        SetArenaRunStatus(status);
    }

    private bool CanSpeakTranscriptMessage(TranscriptMessage message)
    {
        return TranscriptCardRenderer.CanSpeakMessage(message);
    }

    private void SpeakTranscriptMessage(TranscriptMessage message)
    {
        var speaker = string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker;
        var label = message.Turn > 0 ? $"Reading turn {message.Turn}." : "Reading transcript card.";
        SpeakVoiceTts($"{speaker}: {message.Text}", label);
    }

    private void VoiceTtsSettings_Changed(object sender, RoutedEventArgs e)
    {
        PersistVoiceTtsSettings("Voice TTS settings saved.");
    }

    private void VoiceTtsVoicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PersistVoiceTtsSettings("Voice TTS voice saved.");
    }

    private void VoiceTtsSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVoiceTtsSettings || VoiceTtsStatusText is null)
        {
            return;
        }

        CaptureVoiceTtsSettings();
        UpdateVoiceTtsUi("Voice TTS level adjusted.");
        _voiceTtsSettingsSaveDebouncer.Schedule();
    }

    private void TestVoiceTtsButton_Click(object sender, RoutedEventArgs e)
    {
        PersistVoiceTtsSettings("Voice TTS settings saved.");
        SpeakVoiceTts("AI Arena - Lite voice narration is ready.", "Playing voice test.");
    }

    private void StopVoiceTtsButton_Click(object sender, RoutedEventArgs e)
    {
        StopVoicePlayback();
    }

    private void StopVoicePlayback()
    {
        _voiceNarrationService.Stop();
        UpdateVoiceTtsUi("Voice playback stopped.");
        SetArenaRunStatus("Voice playback stopped.");
    }

    private void StopVoicePlaybackForSessionChange(string nextSessionId)
    {
        if (!IsGenuineSessionChange(_activeSession?.Id, nextSessionId))
        {
            return;
        }

        // Stop unconditionally so a synthesizer that is still starting cannot
        // begin speaking content from the session that is being left.
        var wasSpeaking = _voiceNarrationService.IsSpeaking;
        _voiceNarrationService.Stop();
        UpdateVoiceToggleButton();
        if (wasSpeaking)
        {
            UpdateVoiceTtsUi("Voice playback stopped because the active session changed.");
        }
    }

    internal static bool IsGenuineSessionChange(string? currentSessionId, string? nextSessionId)
    {
        return !string.Equals(
            currentSessionId?.Trim(),
            nextSessionId?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }


    private async void LoadSessions(string? preferredSessionId = null)
    {
        await LoadSessionsAsync(preferredSessionId);
    }

    private async Task LoadSessionsAsync(
        string? preferredSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _coreSessionStore.ListSessionsAsync(SessionListingDetail.Messages, cancellationToken);
        if (sessions.Count == 0)
        {
            await _coreSessionStore.EnsureDefaultSessionAsync(cancellationToken);
            sessions = await _coreSessionStore.ListSessionsAsync(SessionListingDetail.Messages, cancellationToken);
        }

        SavedStateCoordinator.SetSessions(sessions);
        await SavedStateCoordinator.RehydratePendingDeletionAsync(cancellationToken);

        var defaultSession = sessions.FirstOrDefault(session => session.Id.Equals(preferredSessionId, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals(_activeSession?.Id, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault();

        if (defaultSession is null)
        {
            // A data root with no sessions is where every first run starts, not
            // a failure. This ran during startup rather than in response to
            // anything the reader did, so the danger tone told them something
            // had gone wrong before they had touched the app.
            LoadStatus.Text = $"No sessions found in {Path.Combine(_coreSessionStore.DataRoot, "sessions")}";
            SavedStateCoordinator.SetStatus("No saved sessions yet. Run a turn to create one.");
            SavedStateCoordinator.ApplyForkLineage(null);
            SavedStateCoordinator.UpdatePicker();
            PopulateFallbackState("No AI Arena - Lite sessions found.");
            return;
        }

        await LoadSessionAsync(defaultSession, force: true, cancellationToken);
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
        if (_snapshotRefreshInProgress)
        {
            return;
        }

        _snapshotRefreshInProgress = true;
        var refreshedSafely = false;
        try
        {
            if (_activeSession is null)
            {
                await LoadSessionsAsync();
                refreshedSafely = true;
                return;
            }

            var observedWriteTime = TryGetSessionDirectoryLastModified(_activeSession.SnapshotPath);
            if (!SnapshotRefreshRequiresSessionScan(_activeSnapshotWriteUtc, observedWriteTime))
            {
                refreshedSafely = true;
                return;
            }

            if (observedWriteTime is not null)
            {
                await LoadSessionAsync(_activeSession with { LastModified = observedWriteTime.Value }, force: true);
                refreshedSafely = true;
                return;
            }

            var latestSession = (await _coreSessionStore.ListSessionsAsync(SessionListingDetail.Identity))
                .FirstOrDefault(session => session.Id == _activeSession.Id);
            if (latestSession is null)
            {
                await LoadSessionsAsync();
                refreshedSafely = true;
                return;
            }

            if (latestSession.LastModified != _activeSnapshotWriteUtc)
            {
                await LoadSessionAsync(latestSession, force: true);
            }

            refreshedSafely = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var presentation = AppErrorPresenter.Present(ex, AppErrorContext.SavedState);
            LoadStatus.Text = presentation.DisplayText;
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "app.snapshot-refresh",
                "App",
                ApplicationStatusState.Warning,
                presentation.Summary,
                presentation.CopyDetails,
                "arena",
                new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
                background: true,
                lifetime: ApplicationStatusLifetime.UntilResolved);
        }
        finally
        {
            if (refreshedSafely)
            {
                ShellTopBar.Presentation.StatusCenter.Resolve("app.snapshot-refresh");
            }

            _snapshotRefreshInProgress = false;
        }
    }

    internal static bool SnapshotRefreshRequiresSessionScan(DateTimeOffset knownWriteTime, DateTimeOffset? observedWriteTime)
    {
        return observedWriteTime is null || observedWriteTime.Value != knownWriteTime;
    }

    internal static DateTimeOffset? TryGetSessionDirectoryLastModified(string snapshotPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(snapshotPath);
            return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                ? null
                : new DateTimeOffset(Directory.GetLastWriteTime(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
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

    private void BattleReviewCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnBattleReviewChanged();
    }

    private void MemoryNotesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnMemoryNotesChanged();
    }

    private void DecisionCardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnDecisionCardChanged();
    }

    private void AutoModeratorCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnAutoModeratorChanged();
    }

    private void StyleFitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnStyleFitChanged();
    }

    private void VoiceDriftEnforcementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnVoiceDriftEnforcementChanged();
    }

    private void TranscriptInternetDetailsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnTranscriptInternetDetailsChanged();
    }

    private void WorldDebugCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingWorldDebug)
        {
            return;
        }

        _wpfSettings.ShowWorldDebug = _wpfSettings.AllowDebugControls
            && WorldDebugCheckBox.IsChecked == true;
        if (!IsWorldDebugEnabled(_wpfSettings))
        {
            _wpfSettings.LabViewMode = "transcript";
        }

        _wpfSettingsStore.Save(_wpfSettings);
        ApplyWorldDebugVisibility(persistIfForcedOff: false);
        var status = IsWorldDebugEnabled(_wpfSettings)
            ? "Debug: AI World 3D view enabled."
            : "Debug: AI World 3D view disabled; using Transcript.";
        SetLoadStatus(status);
        SetArenaRunStatus(status);
    }

    private void AgentWorkspaceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingAgentWorkspace)
        {
            return;
        }

        _wpfSettings.ShowAgentWorkspace = AgentWorkspaceCheckBox.IsChecked == true;
        _wpfSettings.AgentWorkspacePreferenceVersion = 1;
        _wpfSettingsStore.Save(_wpfSettings);
        ApplyAgentWorkspaceVisibility();
        var status = IsAgentWorkspaceEnabled(_wpfSettings)
            ? "Agent workspace shown in navigation."
            : "Agent workspace hidden from navigation.";
        SetLoadStatus(status);
        SetArenaRunStatus(status);
    }

    private async void ControlPlaneCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingControlPlane)
        {
            return;
        }

        _wpfSettings.EnableControlPlane = ControlPlaneCheckBox.IsChecked == true;
        _wpfSettings.ControlPlanePreferenceVersion = 1;
        _wpfSettingsStore.Save(_wpfSettings);
        ApplyControlPlaneToggleState();
        await RefreshControlPlaneHostAsync();
        var status = IsControlPlaneEnabled
            ? "PowerShell control plane enabled."
            : "PowerShell control plane disabled.";
        SetLoadStatus(status);
        SetArenaRunStatus(status);
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

    private void AgentCountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _agentRosterCoordinator?.OnExactCountChanged();
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
        GenerationHelpPopup.PlacementTarget = sender as UIElement;
        GenerationHelpPopup.Placement = PlacementMode.Bottom;
        GenerationHelpPopup.IsOpen = true;
    }

    private static (string Title, string Body) GenerationHelpText(string key)
    {
        return key switch
        {
            "generate" => (
                "Generate",
                "Manual keeps your current tune settings. Random Seed is deterministic and local. AI Choice asks the configured model to build a match. Wild Seed creates a bolder local scenario and cast while respecting locks."),
            "tune" => (
                "Tune",
                "Role pack chooses the cast family. Style chooses the scenario domain. Pressure changes how hard the debate pushes. Absurdity mixes expertise, expression constraints, and reasoning distortions."),
            "recent" => (
                "Recent",
                "Recent stores generated setups in the session snapshot. Filter narrows the list by generator type. Replay restores the selected setup, New Run creates a clean comparison run, and Copy Seed, Copy Brief, Copy Spec, Copy Diff, or Rubric share review-ready setup details. Lock warnings show what the current match may preserve during replay."),
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
        _debugMenuFocusReturnTarget = DebugMenuButton;
        _transcriptViewCoordinator?.ToggleDebugMenu();
    }

    private void ViewMenuButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMenuFocusReturnTarget = ViewMenuButton;
        _transcriptViewCoordinator?.ToggleViewMenu();
    }

    private void ViewMenuPopup_Opened(object? sender, EventArgs e)
    {
        _viewMenuFocusReturnTarget ??= Keyboard.FocusedElement ?? ViewMenuButton;
        DebugMenuPopup.IsOpen = false;
        TranscriptFiltersPopup.IsOpen = false;
        ProviderReachability.ClosePopup();
        _transcriptSearchCoordinator?.CloseSearch();
        FocusOverlayEntry(ViewMenuPopup, ViewPresetFocusedButton);
    }

    private void ViewMenuPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _viewMenuFocusReturnTarget;
        _viewMenuFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, ViewMenuButton, () => !ViewMenuPopup.IsOpen);
    }

    private void ViewMenuPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(ViewMenuPopup, e);
    }

    private void DebugMenuPopup_Opened(object? sender, EventArgs e)
    {
        _debugMenuFocusReturnTarget ??= Keyboard.FocusedElement ?? DebugMenuButton;
        ViewMenuPopup.IsOpen = false;
        TranscriptFiltersPopup.IsOpen = false;
        ProviderReachability.ClosePopup();
        _transcriptSearchCoordinator?.CloseSearch();
        FocusOverlayEntry(DebugMenuPopup, DecisionCardCheckBox);
    }

    private void DebugMenuPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _debugMenuFocusReturnTarget;
        _debugMenuFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, DebugMenuButton, () => !DebugMenuPopup.IsOpen);
    }

    private void DebugMenuPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(DebugMenuPopup, e);
    }

    private void TranscriptFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        var opener = ArenaWorkspaceHeader.PrimaryAction;
        _transcriptFiltersFocusReturnTarget = opener;
        TranscriptFiltersPopup.PlacementTarget = opener;
        TranscriptFiltersPopup.IsOpen = !TranscriptFiltersPopup.IsOpen;
    }

    private void TranscriptFiltersPopup_Opened(object? sender, EventArgs e)
    {
        _transcriptFiltersFocusReturnTarget ??=
            TranscriptFiltersPopup.PlacementTarget ?? Keyboard.FocusedElement ?? ArenaWorkspaceHeader.PrimaryAction;
        ViewMenuPopup.IsOpen = false;
        DebugMenuPopup.IsOpen = false;
        ProviderReachability.ClosePopup();
        _transcriptSearchCoordinator?.CloseSearch();
        FocusOverlayEntry(TranscriptFiltersPopup, SelectedTranscriptTurnFilterEntry());
    }

    private void TranscriptFiltersPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = TranscriptFiltersPopup.PlacementTarget ?? _transcriptFiltersFocusReturnTarget;
        _transcriptFiltersFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, ArenaWorkspaceHeader.PrimaryAction, () => !TranscriptFiltersPopup.IsOpen);
    }

    private void TranscriptFiltersPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(TranscriptFiltersPopup, e);
    }

    internal static void ClosePopupOnEscape(Popup popup, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !popup.IsOpen)
        {
            return;
        }

        popup.IsOpen = false;
        e.Handled = true;
    }

    private void CurrentSetupTransferPopup_Opened(object? sender, EventArgs e)
    {
        _currentSetupTransferFocusReturnTarget ??= Keyboard.FocusedElement ?? CurrentSetupTransferButton;
        FocusOverlayEntry(CurrentSetupTransferPopup, CopyCurrentSetupBriefButton);
    }

    private void CurrentSetupTransferPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _currentSetupTransferFocusReturnTarget;
        _currentSetupTransferFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, CurrentSetupTransferButton, () => !CurrentSetupTransferPopup.IsOpen);
    }

    private void CurrentSetupTransferPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(CurrentSetupTransferPopup, e);
    }

    private void GenerationCopyPopup_Opened(object? sender, EventArgs e)
    {
        _generationCopyFocusReturnTarget ??= Keyboard.FocusedElement ?? GenerationCopyButton;
        FocusOverlayEntry(GenerationCopyPopup, CopyGenerationSeedButton);
    }

    private void GenerationCopyPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _generationCopyFocusReturnTarget;
        _generationCopyFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, GenerationCopyButton, () => !GenerationCopyPopup.IsOpen);
    }

    private void GenerationCopyPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(GenerationCopyPopup, e);
    }

    private void AgentComposerControlsPopup_Opened(object? sender, EventArgs e)
    {
        _agentComposerControlsFocusReturnTarget ??= Keyboard.FocusedElement ?? AgentComposerMenuButton;
        FocusOverlayEntry(AgentComposerControlsPopup, AgentPlanPromptButton);
    }

    private void AgentComposerControlsPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _agentComposerControlsFocusReturnTarget;
        _agentComposerControlsFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, AgentComposerMenuButton, () => !AgentComposerControlsPopup.IsOpen);
    }

    private void AgentComposerControlsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(AgentComposerControlsPopup, e);
    }

    private void DiagnosticDetailPopup_Opened(object? sender, EventArgs e)
    {
        _diagnosticDetailFocusReturnTarget ??=
            DiagnosticDetailPopup.PlacementTarget ?? Keyboard.FocusedElement ?? TranscriptDiagnosticsScroller;
        FocusOverlayEntry(DiagnosticDetailPopup, DiagnosticDetailCloseButton);
    }

    private void DiagnosticDetailPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = DiagnosticDetailPopup.PlacementTarget ?? _diagnosticDetailFocusReturnTarget;
        _diagnosticDetailFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, TranscriptDiagnosticsScroller, () => !DiagnosticDetailPopup.IsOpen);
    }

    private void DiagnosticDetailPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(DiagnosticDetailPopup, e);
    }

    private void AgentPerformanceDetailPopup_Opened(object? sender, EventArgs e)
    {
        _agentPerformanceDetailFocusReturnTarget ??=
            AgentPerformanceDetailPopup.PlacementTarget ?? Keyboard.FocusedElement ?? AgentPerformanceExpander;
        FocusOverlayEntry(AgentPerformanceDetailPopup, AgentPerformanceDetailCloseButton);
    }

    private void AgentPerformanceDetailPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = AgentPerformanceDetailPopup.PlacementTarget ?? _agentPerformanceDetailFocusReturnTarget;
        _agentPerformanceDetailFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, AgentPerformanceExpander, () => !AgentPerformanceDetailPopup.IsOpen);
    }

    private void AgentPerformanceDetailPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(AgentPerformanceDetailPopup, e);
    }

    private void AgentPerformanceExpander_Expanded(object sender, RoutedEventArgs e)
    {
        _agentPerformanceCoordinator?.SetExpanded(true);
    }

    private void AgentPerformanceExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        _agentPerformanceCoordinator?.SetExpanded(false);
        AgentPerformance.CloseDetail();
    }

    private void AgentComposerMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AgentComposerControlsPopup.IsOpen = !AgentComposerControlsPopup.IsOpen;
    }

    private void AgentComposerMenuAction_Click(object sender, RoutedEventArgs e)
    {
        AgentComposerControlsPopup.IsOpen = false;
        if (ReferenceEquals(sender, AgentClearButton))
        {
            AgentTeamModeButton.Content = "Team: Full";
        }
    }

    private void MatchSetupSectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            ShowMatchSetupSection(tag);
        }
    }

    private bool ShowMatchSetupSection(string tag)
    {
        var normalized = (tag ?? "").Trim().ToLowerInvariant();
        var sections = new (string Tag, Button Button, UIElement Section)[]
        {
            ("scenario", MatchSetupScenarioTabButton, MatchSetupScenarioSection),
            ("cast", MatchSetupCastTabButton, MatchSetupCastSection),
            ("matrix", MatchSetupMatrixTabButton, MatchSetupMatrixSection),
            ("saved", MatchSetupSavedTabButton, MatchSetupSavedSection)
        };
        if (!sections.Any(section => section.Tag.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var (sectionTag, button, section) in sections)
        {
            var active = sectionTag.Equals(normalized, StringComparison.OrdinalIgnoreCase);
            section.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            button.BorderBrush = ResourceBrush(active ? "PrimaryBorderBrush" : "ControlBorderBrush");
            button.Foreground = ResourceBrush(active ? "TextBrush" : "MutedTextBrush");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        _matchSetupSection = normalized;
        return true;
    }

    private void AgentClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _agentWorkspaceCoordinator?.Clear();
        AgentTeamModeButton.Content = "Team: Full";
    }

    // Starts true so checkbox/text events raised while InitializeComponent applies XAML
    // defaults can never save the not-yet-loaded settings object over the real file.
    // InitializeAgentAndStreamingSettingsFields clears it once loaded values are applied.
    private bool _suppressAgentSettingsHandlers = true;

    private void InitializeAgentAndStreamingSettingsFields()
    {
        _suppressAgentSettingsHandlers = true;
        try
        {
            AgentRescueModelText.Text = _wpfSettings.AgentRescueModel;
            StreamModelResponsesCheckBox.IsChecked = _wpfSettings.StreamModelResponses;
            AgentBuilderOnlyDefaultCheckBox.IsChecked = _wpfSettings.AgentBuilderOnlyDefault;
            AgentPlannerReviewerTokensText.Text = _wpfSettings.AgentPlannerReviewerMaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AgentBuilderTokensText.Text = _wpfSettings.AgentBuilderMaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AgentRescueAttemptsText.Text = _wpfSettings.AgentAutoRescueAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AgentCommandTimeoutText.Text = _wpfSettings.AgentCommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AgentTeamModeButton.Content = _wpfSettings.AgentBuilderOnlyDefault ? "Team: Builder only" : "Team: Full";
            AgentPerformanceFullCardsCheckBox.IsChecked = _wpfSettings.AgentPerformanceFullCards;
            RefreshProviderProfilePicker();
        }
        finally
        {
            _suppressAgentSettingsHandlers = false;
        }
    }

    private void AgentRescueModelText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressAgentSettingsHandlers)
        {
            return;
        }

        var value = AgentRescueModelText.Text.Trim();
        if (value.Equals(_wpfSettings.AgentRescueModel, StringComparison.Ordinal))
        {
            return;
        }

        _wpfSettings.AgentRescueModel = value;
        _wpfSettingsStore.Save(_wpfSettings);
    }

    private void StreamModelResponsesCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAgentSettingsHandlers || _wpfSettings is null)
        {
            return;
        }

        _wpfSettings.StreamModelResponses = StreamModelResponsesCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
    }

    private void AgentSettingsCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAgentSettingsHandlers || _wpfSettings is null)
        {
            return;
        }

        _wpfSettings.AgentBuilderOnlyDefault = AgentBuilderOnlyDefaultCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        AgentSettingsStatusText.Text = _wpfSettings.AgentBuilderOnlyDefault
            ? "New Agent sessions start in Builder-only mode."
            : "New Agent sessions start with the full Planner, Reviewer, and Builder team.";
    }

    private void AgentSettingsField_Commit(object sender, RoutedEventArgs e)
    {
        if (_suppressAgentSettingsHandlers)
        {
            return;
        }

        var notes = new List<string>();
        _wpfSettings.AgentPlannerReviewerMaxTokens = CommitClampedAgentField(AgentPlannerReviewerTokensText, _wpfSettings.AgentPlannerReviewerMaxTokens, 256, 32768, "Planner/Reviewer tokens", notes);
        _wpfSettings.AgentBuilderMaxTokens = CommitClampedAgentField(AgentBuilderTokensText, _wpfSettings.AgentBuilderMaxTokens, 256, 32768, "Builder tokens", notes);
        _wpfSettings.AgentAutoRescueAttempts = CommitClampedAgentField(AgentRescueAttemptsText, _wpfSettings.AgentAutoRescueAttempts, 0, 5, "Rescue attempts", notes);
        _wpfSettings.AgentCommandTimeoutSeconds = CommitClampedAgentField(AgentCommandTimeoutText, _wpfSettings.AgentCommandTimeoutSeconds, 10, 3600, "Command timeout", notes);
        _wpfSettingsStore.Save(_wpfSettings);
        AgentSettingsStatusText.Text = notes.Count == 0
            ? "Agent settings saved."
            : $"Saved with corrections: {string.Join("; ", notes)}.";
    }

    private static int CommitClampedAgentField(TextBox field, int currentValue, int min, int max, string label, List<string> notes)
    {
        if (!int.TryParse(field.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            field.Text = currentValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            notes.Add($"{label} must be a whole number; kept {currentValue}");
            return currentValue;
        }

        var clamped = Math.Clamp(parsed, min, max);
        if (clamped != parsed)
        {
            notes.Add($"{label} adjusted to {clamped} (allowed {min}-{max})");
        }

        field.Text = clamped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return clamped;
    }

    private void ResetAgentSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new WpfSettings();
        _wpfSettings.AgentBuilderOnlyDefault = defaults.AgentBuilderOnlyDefault;
        _wpfSettings.AgentPlannerReviewerMaxTokens = defaults.AgentPlannerReviewerMaxTokens;
        _wpfSettings.AgentBuilderMaxTokens = defaults.AgentBuilderMaxTokens;
        _wpfSettings.AgentAutoRescueAttempts = defaults.AgentAutoRescueAttempts;
        _wpfSettings.AgentCommandTimeoutSeconds = defaults.AgentCommandTimeoutSeconds;
        _wpfSettings.AgentRescueModel = defaults.AgentRescueModel;
        _wpfSettingsStore.Save(_wpfSettings);
        InitializeAgentAndStreamingSettingsFields();
        AgentSettingsStatusText.Text = "Agent settings restored to defaults.";
    }

    private (TextBox? Temperature, TextBox? MaxOutputTokens) RoleOverrideBoxes(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "alpha" => (AlphaTempOverrideText, AlphaMaxOverrideText),
            "beta" => (BetaTempOverrideText, BetaMaxOverrideText),
            "gamma" => (GammaTempOverrideText, GammaMaxOverrideText),
            "delta" => (DeltaTempOverrideText, DeltaMaxOverrideText),
            "narrator" => (NarratorTempOverrideText, NarratorMaxOverrideText),
            _ => (null, null)
        };
    }

    private (double? Temperature, int? MaxOutputTokens) RoleGenerationOverrideFor(string key)
    {
        var (temperatureBox, maxOutputBox) = RoleOverrideBoxes(key);
        double? temperature = null;
        if (temperatureBox is not null
            && double.TryParse(temperatureBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTemperature))
        {
            temperature = Math.Clamp(parsedTemperature, 0, 2);
        }

        int? maxOutputTokens = null;
        if (maxOutputBox is not null
            && int.TryParse(maxOutputBox.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedMaxOutput))
        {
            maxOutputTokens = Math.Clamp(parsedMaxOutput, 1, 32768);
        }

        return (temperature, maxOutputTokens);
    }

    private void ApplyRoleOverrideFields(ArenaViewSnapshot snapshot)
    {
        foreach (var key in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
        {
            var (temperatureBox, maxOutputBox) = RoleOverrideBoxes(key);
            if (temperatureBox is null || maxOutputBox is null)
            {
                continue;
            }

            snapshot.RoleOverrides.TryGetValue(key, out var roleOverride);
            temperatureBox.Text = roleOverride?.Temperature?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
            maxOutputBox.Text = roleOverride?.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }

    private async void RoleOverrideText_Commit(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot)
        {
            return;
        }

        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.PersistModelRoutingAsync(
                "Role generation overrides saved.",
                cancellationToken: cancellationToken));
    }

    private async void UseDefaultModelForAllRolesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.UseDefaultModelForAllRolesAsync(cancellationToken));
    }

    private async void TestAllRolesButton_Click(object sender, RoutedEventArgs e)
    {
        TestAllRolesButton.IsEnabled = false;
        try
        {
            await RunProviderCommitSafelyAsync(
                (coordinator, cancellationToken) => coordinator.TestAllRolesAsync(cancellationToken));
        }
        finally
        {
            TestAllRolesButton.IsEnabled = true;
        }
    }

    private void RefreshProviderProfilePicker(string? selectName = null)
    {
        ProviderProfilePicker.Items.Clear();
        foreach (var profile in _wpfSettings.ProviderProfiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            ProviderProfilePicker.Items.Add(profile.Name);
        }

        if (!string.IsNullOrWhiteSpace(selectName))
        {
            ProviderProfilePicker.Text = selectName;
        }
    }

    private void SaveProviderProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is null)
        {
            return;
        }

        var name = ProviderProfilePicker.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetProviderProfileStatus("Type a setup name before saving.", ApplicationStatusState.Warning);
            return;
        }

        var (baseUrl, apiMode, model, roleModels, defaultForUnassignedAgentsEnabled, modelSettings) = _providerSettingsCoordinator.CaptureProviderProfile();
        var catalogModelSettings = ProviderModelsSurface.CaptureProfileModelSettings();
        var portableModelSettings = modelSettings
            .Concat(catalogModelSettings)
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Model) || !string.IsNullOrWhiteSpace(setting.ModelIdentity))
            .GroupBy(setting => string.IsNullOrWhiteSpace(setting.Model)
                ? setting.ModelIdentity
                : $"model:{setting.Model}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var profile = new WpfProviderProfile
        {
            Name = name,
            BaseUrl = baseUrl,
            ApiMode = apiMode,
            Model = model,
            AlphaModel = roleModels.GetValueOrDefault("alpha", ""),
            BetaModel = roleModels.GetValueOrDefault("beta", ""),
            GammaModel = roleModels.GetValueOrDefault("gamma", ""),
            DeltaModel = roleModels.GetValueOrDefault("delta", ""),
            NarratorModel = roleModels.GetValueOrDefault("narrator", ""),
            RoleModels = roleModels.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase),
            DefaultForUnassignedAgentsEnabled = defaultForUnassignedAgentsEnabled,
            ModelSettings = portableModelSettings
        };
        _wpfSettings.ProviderProfiles.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _wpfSettings.ProviderProfiles.Add(profile);
        _wpfSettingsStore.Save(_wpfSettings);
        RefreshProviderProfilePicker(name);
        SetProviderProfileStatus(
            $"Setup '{name}' saved with {model} and the current role routing.",
            ApplicationStatusState.Succeeded);
    }

    private async void ApplyProviderProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerSettingsCoordinator is null)
        {
            return;
        }

        var name = ProviderProfilePicker.Text.Trim();
        var profile = _wpfSettings.ProviderProfiles.FirstOrDefault(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            SetProviderProfileStatus(
                string.IsNullOrWhiteSpace(name)
                    ? "Pick a saved setup to use."
                    : $"No setup named '{name}'.",
                ApplicationStatusState.Warning);
            return;
        }

        ShellTopBar.Presentation.StatusCenter.Resolve("provider.profile");

        var roleModels = new Dictionary<string, string>(
            profile.RoleModels ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (role, model) in new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = profile.AlphaModel,
            ["beta"] = profile.BetaModel,
            ["gamma"] = profile.GammaModel,
            ["delta"] = profile.DeltaModel,
            ["narrator"] = profile.NarratorModel
        })
        {
            if (!roleModels.ContainsKey(role) && !string.IsNullOrWhiteSpace(model))
            {
                roleModels[role] = model;
            }
        }
        await RunProviderCommitSafelyAsync(async (coordinator, cancellationToken) =>
        {
            await coordinator.ApplyProviderProfileAsync(
                profile.BaseUrl,
                profile.ApiMode,
                profile.Model,
                roleModels,
                profile.DefaultForUnassignedAgentsEnabled,
                profile.ModelSettings ?? [],
                profile.Name,
                cancellationToken);
            ProviderProfileStatusText.Text = $"Setup '{profile.Name}' is now in use.";
        });
    }

    private void DeleteProviderProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProviderProfilePicker.Text.Trim();
        var removed = _wpfSettings.ProviderProfiles.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            SetProviderProfileStatus(
                string.IsNullOrWhiteSpace(name)
                    ? "Pick a saved setup to delete."
                    : $"No setup named '{name}'.",
                ApplicationStatusState.Warning);
            return;
        }

        _wpfSettingsStore.Save(_wpfSettings);
        RefreshProviderProfilePicker();
        ProviderProfilePicker.Text = "";
        SetProviderProfileStatus($"Setup '{name}' deleted.", ApplicationStatusState.Succeeded);
    }

    private void SetProviderProfileStatus(string status, ApplicationStatusState state)
    {
        ProviderProfileStatusText.Text = status;
        ShellTopBar.Presentation.StatusCenter.PublishNotice(
            "provider.profile",
            "Provider",
            state,
            status,
            navigationTarget: "settings",
            identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
            lifetime: state is ApplicationStatusState.Warning or ApplicationStatusState.Failed
                ? ApplicationStatusLifetime.UntilResolved
                : ApplicationStatusLifetime.Transient);
    }

    private void SettingsSearchText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Fires whether a person typed or the control plane assigned Text, which
        // is why the query is announced here rather than from the open handler:
        // settings.search shows the overlay first and sets the query afterwards.
        PublishSettingsOverlayChanged("Settings search updated.");

        var query = SettingsSearchText.Text.Trim();
        var allExpanders = new List<Expander>();
        CollectSettingsExpanders(SettingsSectionsPanel, allExpanders);

        if (string.IsNullOrEmpty(query))
        {
            SettingsSearchFeedbackPanel.Visibility = Visibility.Collapsed;
            SettingsSearchFeedbackText.Text = "";
            foreach (var expander in allExpanders)
            {
                expander.Visibility = Visibility.Visible;
            }

            if (_settingsSearchActive)
            {
                RestoreSettingsExpansion(allExpanders, _settingsExpansionBeforeSearch);

                _settingsExpansionBeforeSearch.Clear();
                _settingsSearchActive = false;
            }

            return;
        }

        if (!_settingsSearchActive)
        {
            _settingsExpansionBeforeSearch.Clear();
            foreach (var expander in allExpanders)
            {
                _settingsExpansionBeforeSearch[expander] = expander.IsExpanded;
            }

            _settingsSearchActive = true;
        }

        var matchCount = 0;
        foreach (var child in SettingsSectionsPanel.Children)
        {
            if (child is not Expander expander)
            {
                continue;
            }

            var matches = SettingsNodeMatches(expander, query);
            expander.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            expander.IsExpanded = matches;
            if (matches)
            {
                matchCount++;
                ApplyNestedSettingsSearch(expander.Content, query);
            }
        }

        SettingsSearchFeedbackText.Text = matchCount == 0
            ? $"No settings match \u201c{query}\u201d."
            : $"{matchCount} {(matchCount == 1 ? "section" : "sections")} match \u201c{query}\u201d.";
        SettingsSearchFeedbackPanel.Visibility = Visibility.Visible;
    }

    private void SettingsSearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsSearchText.Clear();
        SettingsSearchText.Focus();
    }

    internal static bool SettingsNodeMatches(object? node, string query)
    {
        var texts = new List<string>();
        CollectLogicalText(node, texts);
        return texts.Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    internal static void ApplyNestedSettingsSearch(object? node, string query)
    {
        switch (node)
        {
            case null:
                return;
            case Expander expander:
                var matches = SettingsNodeMatches(expander, query);
                expander.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                expander.IsExpanded = matches;
                if (matches)
                {
                    ApplyNestedSettingsSearch(expander.Content, query);
                }

                return;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    ApplyNestedSettingsSearch(child, query);
                }

                return;
            case ItemsControl items:
                foreach (var item in items.Items)
                {
                    ApplyNestedSettingsSearch(item, query);
                }

                return;
            case ContentControl content:
                ApplyNestedSettingsSearch(content.Content, query);
                return;
            case Decorator decorator:
                ApplyNestedSettingsSearch(decorator.Child, query);
                return;
        }
    }

    internal static void CollectSettingsExpanders(object? node, List<Expander> sink)
    {
        switch (node)
        {
            case null:
                return;
            case Expander expander:
                sink.Add(expander);
                CollectSettingsExpanders(expander.Content, sink);
                return;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    CollectSettingsExpanders(child, sink);
                }

                return;
            case ItemsControl items:
                foreach (var item in items.Items)
                {
                    CollectSettingsExpanders(item, sink);
                }

                return;
            case ContentControl content:
                CollectSettingsExpanders(content.Content, sink);
                return;
            case Decorator decorator:
                CollectSettingsExpanders(decorator.Child, sink);
                return;
        }
    }

    internal static void RestoreSettingsExpansion(
        IEnumerable<Expander> expanders,
        IReadOnlyDictionary<Expander, bool> priorExpansion)
    {
        foreach (var expander in expanders)
        {
            expander.Visibility = Visibility.Visible;
            if (priorExpansion.TryGetValue(expander, out var wasExpanded))
            {
                expander.IsExpanded = wasExpanded;
            }
        }
    }

    private static void CollectLogicalText(object? node, List<string> sink)
    {
        switch (node)
        {
            case null:
                return;
            case string text:
                sink.Add(text);
                return;
            case TextBlock textBlock:
                sink.Add(textBlock.Text);
                return;
            case HeaderedContentControl headered:
                CollectLogicalText(headered.Header, sink);
                CollectLogicalText(headered.Content, sink);
                return;
            case ItemsControl items:
                foreach (var item in items.Items)
                {
                    CollectLogicalText(item, sink);
                }

                return;
            case ContentControl content:
                CollectLogicalText(content.Content, sink);
                return;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    CollectLogicalText(child, sink);
                }

                return;
            case Decorator decorator:
                CollectLogicalText(decorator.Child, sink);
                return;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions SettingsTransferJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = $"ai-arena-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_wpfSettings, SettingsTransferJsonOptions);
            var clone = System.Text.Json.JsonSerializer.Deserialize<WpfSettings>(json, SettingsTransferJsonOptions) ?? new WpfSettings();
            clone.AgentWorkspaceMessages = [];
            File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(clone, SettingsTransferJsonOptions));
            SetSettingsTransferStatus(
                $"Settings exported to {dialog.FileName}.",
                $"Settings exported to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetSettingsTransferStatus(
                AppErrorPresenter.Present(ex, AppErrorContext.FileTransfer).DisplayText);
        }
    }

    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = System.Text.Json.JsonSerializer.Deserialize<WpfSettings>(File.ReadAllText(dialog.FileName), SettingsTransferJsonOptions);
            if (imported is null)
            {
                SetSettingsTransferStatus("Import failed: the file did not contain AI Arena - Lite settings.");
                return;
            }

            // Keep the local Agent conversation; imports carry behavior, not history.
            imported.AgentWorkspaceMessages = _wpfSettings.AgentWorkspaceMessages;
            _wpfSettingsStore.Save(imported);
            _wpfSettings = _wpfSettingsStore.Load();
            InitializeAgentAndStreamingSettingsFields();
            RefreshProviderProfilePicker();
            ShellNavigation.ApplyTheme(_wpfSettings.ThemeId, persist: false, rerender: true);
            SetSettingsTransferStatus("Settings imported. Some visual options apply after restart.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetSettingsTransferStatus(
                AppErrorPresenter.Present(ex, AppErrorContext.FileTransfer).DisplayText);
        }
    }

    private void SetSettingsTransferStatus(string localStatus, string? applicationStatus = null)
    {
        SettingsTransferStatusText.Text = localStatus;
        SetApplicationStatus(
            "app.settings-transfer",
            "App",
            applicationStatus ?? localStatus,
            "settings");
    }

    private void AgentTeamModeButton_Click(object sender, RoutedEventArgs e)
    {
        AgentComposerControlsPopup.IsOpen = false;
        if (_agentWorkspaceCoordinator is null)
        {
            return;
        }

        var builderOnly = _agentWorkspaceCoordinator.ToggleBuilderOnlyMode();
        AgentTeamModeButton.Content = builderOnly ? "Team: Builder only" : "Team: Full";
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

    private async Task LoadSessionAsync(
        CoreSessionSummary session,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!force
            && string.Equals(_activeSession?.Id, session.Id, StringComparison.Ordinal)
            && session.LastModified == _activeSnapshotWriteUtc)
        {
            return;
        }

        StopVoicePlaybackForSessionChange(session.Id);

        // A session switch owns status causality immediately, even if reading its
        // snapshot later fails. Preserve the provider scope for same-session
        // refreshes; RenderSnapshot will refine a successful switch with the new
        // provider fingerprint.
        if (!string.Equals(_statusCenterSessionId, session.Id, StringComparison.Ordinal))
        {
            _statusCenterSessionId = session.Id;
            ShellTopBar.Presentation.StatusCenter.SetContext(new ApplicationStatusIdentity(session.Id));
        }

        try
        {
            ArenaSnapshot? coreSnapshot = null;
            var currentSession = session;
            ArenaViewSnapshot snapshot;
            if (session.HasSnapshot)
            {
                var sessionInstanceId = await _coreSessionStore.EnsureSessionInstanceIdAsync(
                    session.Id,
                    cancellationToken);
                coreSnapshot = await _coreSessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (coreSnapshot is null
                    || !coreSnapshot.SessionInstanceId.Equals(sessionInstanceId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The live session identity could not be established safely.");
                }

                var persistedWriteTime = new DateTimeOffset(
                    File.GetLastWriteTimeUtc(session.SnapshotPath),
                    TimeSpan.Zero);
                currentSession = session with
                {
                    MessageCount = coreSnapshot.Engine.Messages.Count,
                    LastModified = persistedWriteTime
                };
                snapshot = SnapshotViewMapper.FromCore(currentSession, coreSnapshot);
            }
            else
            {
                snapshot = SnapshotViewMapper.Empty(currentSession, "No snapshot file.");
            }

            _activeSession = currentSession;
            _experimentLabCoordinator?.NotifyActiveSessionChanged(currentSession.Id);
            _activeSnapshotWriteUtc = currentSession.LastModified;
            SavedStateCoordinator.ApplyForkLineage(coreSnapshot?.ForkLineage);
            RenderSnapshot(snapshot);
            SavedStateCoordinator.RefreshCheckpoints();
            LoadStatus.Text = $"Loaded session: {snapshot.SnapshotPath}\nExternal-change refresh: 1.2s";
            ShellTopBar.Presentation.StatusCenter.Resolve("app.session-load");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var presentation = AppErrorPresenter.Present(ex, AppErrorContext.SavedState);
            _activeSession = session;
            _experimentLabCoordinator?.NotifyActiveSessionChanged(session.Id);
            _activeSnapshotWriteUtc = session.LastModified;
            SavedStateCoordinator.ApplyForkLineage(null);
            PopulateFallbackState(presentation.DisplayText);
            SavedStateCoordinator.ClearCheckpoints("No checkpoint data.");
            LoadStatus.Text = presentation.DisplayText;
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "app.session-load",
                "App",
                ApplicationStatusState.Failed,
                presentation.Summary,
                presentation.CopyDetails,
                "arena",
                new ApplicationStatusIdentity(session.Id),
                background: false,
                lifetime: ApplicationStatusLifetime.UntilResolved);
        }

        _collaborateCoordinator?.PublishPendingRecoveryWarning(PublishCollaborateRecoveryWarning);
        await RefreshAgentInspectionForSessionSafelyAsync(cancellationToken);
    }

    private async Task RefreshAgentInspectionForSessionSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _agentInspectionLabCoordinator.InitializeAsync(cancellationToken);
            ShellTopBar.Presentation.StatusCenter.Resolve("agent.inspection-refresh");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The coordinator clears the prior private-memory view before a
            // session switch. A failed reload therefore remains default-deny.
            Debug.WriteLine($"Agent inspection session refresh failed safely: {exception.GetType().Name}");
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "agent.inspection-refresh",
                "Agent",
                ApplicationStatusState.Warning,
                "Agent inspection refresh failed.",
                $"Private inspection remains unavailable ({exception.GetType().Name}).",
                "agent",
                background: true,
                lifetime: ApplicationStatusLifetime.UntilResolved);
        }
    }

    private void RenderSnapshot(ArenaViewSnapshot snapshot)
    {
        // Advance the causal status scope synchronously with the rendered session.
        // Waiting for the asynchronous reachability refresh would briefly leave the
        // new session unable to publish receipts while showing the prior session's
        // unresolved statuses.
        var statusSessionId = string.IsNullOrWhiteSpace(snapshot.SessionId)
            ? _activeSession?.Id ?? string.Empty
            : snapshot.SessionId;
        _statusCenterSessionId = statusSessionId;
        ShellTopBar.Presentation.StatusCenter.SetContext(ProviderStatusIdentity(statusSessionId, snapshot));

        PreserveCurrentSessionSettingsDraft();
        UpdateTopBarStatus(snapshot);
        _lastRenderedSnapshot = snapshot;
        var arenaReadiness = ArenaOperationCoordinator.EvaluateReadiness(snapshot);
        ArenaOperations.UpdateReadiness(arenaReadiness);
        PublishArenaReadiness(snapshot, arenaReadiness);
        _isRenderingSnapshot = true;
        try
        {
            var activeCount = snapshot.Agents.Count(agent => agent.Active);
            AgentRoster.ApplySnapshot(activeCount);
            ProviderSettings.ApplySnapshot(snapshot);
            _llamaCppRuntimeCoordinator?.ConfigurationChanged();
            ApplyRoleOverrideFields(snapshot);
            ProviderTimeoutText.Text = snapshot.ProviderTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ProviderTemperatureText.Text = snapshot.ProviderTemperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            ProviderMaxOutputText.Text = snapshot.ProviderMaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ProviderContextLengthText.Text = snapshot.ProviderContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ShellUiHelpers.SelectComboTag(ProviderReasoningPicker, ModelProviderReasoningModes.Normalize(snapshot.ProviderReasoning));
            ProviderNativeStatefulChatCheckBox.IsChecked = snapshot.ProviderNativeStatefulChat;
            ProviderNativeIdleTtlText.Text = snapshot.ProviderNativeIdleTtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextTranscriptWindowText.Text = snapshot.TranscriptWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextPrivateWindowText.Text = snapshot.PrivateWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextNotesWindowText.Text = snapshot.NotesWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextSummaryText.Text = string.IsNullOrWhiteSpace(snapshot.Summary) ? "No summary has been generated for this session." : snapshot.Summary;
            InternetWorkflow.ApplySnapshot(snapshot);
            OperatorTurn.ApplySnapshot(snapshot);
            ApplyWorldSnapshotIfVisible(snapshot);
        }
        finally
        {
            _isRenderingSnapshot = false;
        }

        ReconcileSessionSettingsAfterSnapshot(_activeSession?.Id ?? "");

        InternetWorkflow.UpdateSettingsHint();
        OperatorTurn.UpdatePrivateTargetSummary();
        SessionOverview.UpdateSessionOverview(snapshot);
        _lastAgentPersonas = snapshot.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToDictionary(agent => agent.Id, agent => agent.Persona, StringComparer.OrdinalIgnoreCase);
        PopulateTranscript(snapshot.Messages);
        AgentBoard.Populate(snapshot, CurrentTurnAgent(snapshot)?.Id);
        PopulateCustomMatch(snapshot);
        RefreshProviderModelsPresentationIfVisible();
        _collaborateCoordinator?.RefreshProviderState();
        _arenaEvaluationCoordinator?.RefreshAvailability();
        OperatorTurn.UpdatePrivateTargetSummary();
    }

    private void PopulateFallbackState(string message)
    {
        AgentPerformance.CloseDetail();
        _lastRenderedSnapshot = null;
        _matchSetupSnapshotRenderState.Reset();
        ArenaOperations.UpdateReadiness(new ArenaActionReadiness(false, "Load a valid session before running the arena."));
        TranscriptItems.ItemsSource = new object[]
        {
            CreateCard("Transcript", message, ResourceBrush("CardBrush"), ResourceBrush("AlphaAccentBrush"))
        };
        AgentBoard.PopulateFallback();
        _collaborateCoordinator?.RefreshProviderState();
        _arenaEvaluationCoordinator?.RefreshAvailability();
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
        PublishTranscriptMessageEvents(messages);
        if (_transcriptListCoordinator is null)
        {
            _lastRenderedMessages = messages;
            _transcriptExportCoordinator?.RefreshExportScopeStatus();
            return;
        }

        TranscriptList.Populate(messages);
        TranscriptExportCoordinator.RefreshExportScopeStatus();
    }

    private void TranscriptDashboardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _transcriptViewCoordinator?.UpdateDashboardLayout(e.NewSize.Width);
    }

    private void PopulateCustomMatch(ArenaViewSnapshot snapshot)
    {
        // Match Setup is normally hidden. Keep its action/copy boundary pointed
        // at the latest authoritative snapshot, but do not mutate its visual
        // tree until it is visible. This also keeps unsaved control drafts alive
        // across ordinary arena turns.
        ScenarioWorkflow.ObserveSnapshot(snapshot);
        _matchSetupSnapshotRenderState.Observe(snapshot);
        RenderPendingMatchSetupSnapshot();
    }

    private void RenderPendingMatchSetupSnapshot()
    {
        var plan = _matchSetupSnapshotRenderState.Plan(CustomMatchPanel.Visibility == Visibility.Visible);
        if (plan is null)
        {
            return;
        }

        var areas = plan.Areas;
        if (areas.HasFlag(MatchSetupProjectionArea.ModelBehavior))
        {
            MatchSetup.PopulateModelBehavior(plan.Snapshot);
        }

        if (areas.HasFlag(MatchSetupProjectionArea.SeedInspector))
        {
            SeedInspector.Populate(plan.Snapshot);
        }

        if (areas.HasFlag(MatchSetupProjectionArea.GenerationHistory))
        {
            ScenarioWorkflow.PopulateGenerationHistory(plan.Snapshot);
        }
        else if (areas.HasFlag(MatchSetupProjectionArea.SetupFeedback))
        {
            ScenarioWorkflow.RefreshSetupFeedback(plan.Snapshot);
        }

        if (areas.HasFlag(MatchSetupProjectionArea.SetupSummary))
        {
            CustomMatchSummary.Populate(plan.Snapshot);
        }

        if (areas.HasFlag(MatchSetupProjectionArea.RivalryMatrix))
        {
            MatchSetup.PopulateRivalryMatrix(plan.Snapshot);
        }

        _matchSetupSnapshotRenderState.Commit(plan);
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return ShellCards.CreateCard(title, body, background, accent);
    }

    private bool ShouldShowStyleFit()
    {
        return _wpfSettings.ShowStyleFit && (_wpfSettings.AllowDebugControls || _wpfSettings.ShowBattleReview);
    }

    private bool ShouldShowDecisionCard()
    {
        return _wpfSettings.ShowDecisionCard && (_wpfSettings.AllowDebugControls || _wpfSettings.ShowBattleReview);
    }

    private bool ShouldEnforceVoiceDrift()
    {
        return _wpfSettings.AllowDebugControls && _wpfSettings.EnforceVoiceDrift;
    }

    private Brush VoiceAdherenceAccent(string state)
    {
        return state.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? ResourceBrush("Arena.Brush.Success")
            : state.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? ResourceBrush("Arena.Brush.Warning")
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

        if (_lastRenderedSnapshot?.MatchEnded == true)
        {
            ArenaRunStatus.Text = "This match has ended. Reset or fork the session before generating a Decision Card.";
            return;
        }

        if (_lastRenderedSnapshot?.FactoryMode == true)
        {
            ArenaRunStatus.Text = "Decision Card generation is unavailable in Factory mode. Turn Apply Match Setup to models on to use narrator guidance.";
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
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.TestProviderAsync(TestProviderButton, cancellationToken));
    }

    private async void ApplyProviderPresetButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ApplyProviderPresetAsync(cancellationToken));
        if (ProviderPresetStatusText.Text.StartsWith("Manual provider selected.", StringComparison.Ordinal))
        {
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "provider.preset",
                "Provider",
                ApplicationStatusState.Warning,
                ProviderPresetStatusText.Text,
                navigationTarget: "settings",
                identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
                lifetime: ApplicationStatusLifetime.UntilResolved);
        }
        else
        {
            ShellTopBar.Presentation.StatusCenter.Resolve("provider.preset");
        }
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
    }

    private async void LlamaCppInspectButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _llamaCppRuntimeCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "llama.cpp runtime inspection",
                coordinator.InspectAsync);
        }
    }

    private async void LlamaCppReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _llamaCppRuntimeCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "llama.cpp runtime reconnect",
                coordinator.ReconnectAsync);
        }
    }

    private async void LlamaCppPreloadButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _llamaCppRuntimeCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "llama.cpp model preload",
                coordinator.PreloadAsync);
        }
    }

    private async void LlamaCppUnloadButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _llamaCppRuntimeCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "llama.cpp model unload",
                coordinator.UnloadAsync);
        }
    }

    private async void ArenaEvaluationCaptureBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _arenaEvaluationCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "arena evaluation baseline capture",
                coordinator.CaptureBaselineAsync);
        }
    }

    private async void ArenaEvaluationCompareCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _arenaEvaluationCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "arena model comparison",
                coordinator.CompareCurrentAsync);
        }
    }

    private async void ArenaEvaluationRunQaButton_Click(object sender, RoutedEventArgs e)
    {
        var coordinator = _arenaEvaluationCoordinator;
        if (coordinator is not null)
        {
            await RunTrackedBackgroundOperationSafelyAsync(
                "arena runtime QA inspection",
                coordinator.RunQaAsync);
        }
    }

    private void ArenaEvaluationCopyEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        _arenaEvaluationCoordinator?.CopyEvidence();
    }

    private void ArenaEvaluationCopyReplaySetupButton_Click(object sender, RoutedEventArgs e)
    {
        _arenaEvaluationCoordinator?.CopyReplaySetup();
    }

    private async void PreloadSelectedModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.PreloadSelectedModelsAsync(cancellationToken));
    }

    private async void UnloadSelectedModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.UnloadSelectedModelsAsync(cancellationToken));
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.DownloadModelAsync(cancellationToken));
    }

    private async void CheckDownloadStatusButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.CheckDownloadStatusAsync(cancellationToken));
    }

    private async void AutoConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.AutoConfigureAsync(cancellationToken));
    }

    private async void ApplyAutoConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ApplyAutoConfigureAsync(cancellationToken));
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ArenaSessionMutations.ApplySettingsAsync())
        {
            ResetSessionSettingsBaseline();
            SettingsPendingChangesText.Text = "Session changes saved.";
        }
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

    private async void CurrentTopicsButton_Click(object sender, RoutedEventArgs e)
    {
        await ScenarioWorkflow.GenerateCurrentTopicsSeedAsync();
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
        GenerationCopyPopup.IsOpen = false;
        ScenarioWorkflow.CopyGenerationSeed();
    }

    private void CopyGenerationBriefButton_Click(object sender, RoutedEventArgs e)
    {
        GenerationCopyPopup.IsOpen = false;
        ScenarioWorkflow.CopyGenerationBrief();
    }

    private void CopyGenerationSpecButton_Click(object sender, RoutedEventArgs e)
    {
        GenerationCopyPopup.IsOpen = false;
        ScenarioWorkflow.CopyGenerationSpec();
    }

    private void CopyGenerationDiffButton_Click(object sender, RoutedEventArgs e)
    {
        GenerationCopyPopup.IsOpen = false;
        ScenarioWorkflow.CopyGenerationDiff();
    }

    private void CopyGenerationRubricButton_Click(object sender, RoutedEventArgs e)
    {
        GenerationCopyPopup.IsOpen = false;
        ScenarioWorkflow.CopyGenerationRubric();
    }

    private void CurrentSetupTransferButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentSetupTransferPopup.IsOpen = !CurrentSetupTransferPopup.IsOpen;
    }

    private void GenerationCopyButton_Click(object sender, RoutedEventArgs e)
    {
        GenerationCopyPopup.IsOpen = !GenerationCopyPopup.IsOpen;
    }

    private void CopyCurrentSetupBriefButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentSetupTransferPopup.IsOpen = false;
        ScenarioWorkflow.CopyCurrentSetupBrief();
    }

    private async void CopyCurrentSetupSpecButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentSetupTransferPopup.IsOpen = false;
        try
        {
            var result = await _matchSetupPortabilityService.ExportAsync();
            if (!result.Ok || result.State is null)
            {
                SetMatchSetupTransferStatus($"Match Setup export failed: {result.Message}");
                return;
            }

            var status = ScenarioWorkflowCoordinator.TrySetClipboardText(result.State.Json)
                ? $"Copied portable Match Setup JSON ({result.State.Fingerprint[..12]})."
                : "Copy portable Match Setup JSON failed because the clipboard is busy.";
            SetMatchSetupTransferStatus(status);
        }
        catch (Exception ex)
        {
            var status = ArenaOperationCoordinator.OperationFailureStatus(ex);
            SetMatchSetupTransferStatus(status);
        }
    }

    private async void ImportCurrentSetupSpecButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentSetupTransferPopup.IsOpen = false;
        if (!ShellClipboard.TryGetText(out var json))
        {
            const string status = "Match Setup import is unavailable because the clipboard does not contain readable JSON.";
            SetMatchSetupTransferStatus(status);
            return;
        }

        try
        {
            var result = await _matchSetupPortabilityService.ImportAsync(json, "");
            var detail = result.Receipt?.Warnings.FirstOrDefault();
            var resultMessage = result.Ok ? result.Message : $"Match Setup import failed: {result.Message}";
            var statusText = string.IsNullOrWhiteSpace(detail) ? resultMessage : $"{resultMessage} {detail}";
            SetMatchSetupTransferStatus(statusText);
        }
        catch (Exception ex)
        {
            var status = ArenaOperationCoordinator.OperationFailureStatus(ex);
            SetMatchSetupTransferStatus(status);
        }
    }

    private void SetMatchSetupTransferStatus(string status)
    {
        SetLoadStatus(status);
        SetApplicationStatus("app.match-setup-transfer", "Match Setup", status, "arena");
    }

    private async void ApplyRivalryMatrixButton_Click(object sender, RoutedEventArgs e)
    {
        await MatchSetup.ApplyRivalryMatrixAsync();
    }

    private void ClearRivalryMatrixButton_Click(object sender, RoutedEventArgs e)
    {
        MatchSetup.ClearDraftRivalryMatrix();
    }

    private void ApplyRivalryMatrixPatternButton_Click(object sender, RoutedEventArgs e)
    {
        MatchSetup.ApplyRivalryMatrixPatternDraft();
    }

    private void GenerationHistoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _scenarioWorkflowCoordinator?.OnGenerationHistorySelectionChanged();
    }

    private void GenerationHistoryFilterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _scenarioWorkflowCoordinator?.OnGenerationHistoryFilterChanged();
    }

    private async void NarrateNowButton_Click(object sender, RoutedEventArgs e)
    {
        await ArenaRun.NarrateNowAsync();
    }

    private void SpeakLatestNarratorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceNarrationService.IsSpeaking)
        {
            StopVoicePlayback();
            UpdateVoiceToggleButton();
            return;
        }

        var narratorMessage = _lastRenderedMessages
            .LastOrDefault(message => message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Text));
        if (narratorMessage is null)
        {
            SetArenaRunStatus("No narrator turn is available to speak.");
            return;
        }

        SpeakTranscriptMessage(narratorMessage);
        UpdateVoiceToggleButton();
    }

    private void AgentPerformanceFullCardsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAgentSettingsHandlers)
        {
            return;
        }

        _wpfSettings.AgentPerformanceFullCards = AgentPerformanceFullCardsCheckBox.IsChecked == true;
        _wpfSettingsStore.Save(_wpfSettings);
        _agentPerformanceCoordinator?.RefreshDensity();
    }

    private void RightRailToggleButton_Click(object sender, RoutedEventArgs e)
    {
        CloseNamedTransientShellFlyouts();
        if (_rightRailAutoCollapseActive)
        {
            if (_wpfSettings.RightRailCollapsed)
            {
                _wpfSettings.RightRailCollapsed = false;
                _wpfSettingsStore.Save(_wpfSettings);
                _rightRailNarrowRevealRequested = true;
                _rightRailWidthCollapseLatched = false;
            }
            else
            {
                _rightRailNarrowRevealRequested = !_rightRailNarrowRevealRequested;
                if (_rightRailNarrowRevealRequested)
                {
                    _rightRailWidthCollapseLatched = false;
                }
            }
        }
        else if (_rightRailWidthCollapseLatched)
        {
            _rightRailWidthCollapseLatched = false;
            _rightRailNarrowRevealRequested = false;
            _wpfSettings.RightRailCollapsed = false;
            _wpfSettingsStore.Save(_wpfSettings);
        }
        else
        {
            _rightRailNarrowRevealRequested = false;
            _wpfSettings.RightRailCollapsed = !_wpfSettings.RightRailCollapsed;
            _wpfSettingsStore.Save(_wpfSettings);
        }

        ApplyRightRailCollapsed();
    }

    private bool ControlSetRightRail(string state)
    {
        CloseNamedTransientShellFlyouts();
        var requested = AIArenaControlPlaneProtocol.NormalizeCommand(state);
        var collapsed = IsRightRailEffectivelyCollapsed(
            _wpfSettings.RightRailCollapsed,
            _rightRailAutoCollapseActive,
            _rightRailNarrowRevealRequested,
            _rightRailWidthCollapseLatched);
        if (requested == "toggle")
        {
            requested = collapsed ? "show" : "hide";
        }

        switch (requested)
        {
            case "show":
            case "expanded":
                _rightRailWidthCollapseLatched = false;
                _wpfSettings.RightRailCollapsed = false;
                _rightRailNarrowRevealRequested = _rightRailAutoCollapseActive;
                break;
            case "hide":
            case "collapsed":
                _rightRailWidthCollapseLatched = false;
                _wpfSettings.RightRailCollapsed = true;
                _rightRailNarrowRevealRequested = false;
                break;
            default:
                return false;
        }

        _wpfSettingsStore.Save(_wpfSettings);
        ApplyRightRailCollapsed();
        return true;
    }

    private object BuildRightRailControlState()
    {
        var collapsed = IsRightRailEffectivelyCollapsed(
            _wpfSettings.RightRailCollapsed,
            _rightRailAutoCollapseActive,
            _rightRailNarrowRevealRequested,
            _rightRailWidthCollapseLatched);
        return new
        {
            State = collapsed ? "collapsed" : "expanded",
            Preference = _wpfSettings.RightRailCollapsed ? "collapsed" : "expanded",
            AutoCollapse = _rightRailAutoCollapseActive,
            Overlay = ShouldOverlayRightRail(_rightRailAutoCollapseActive, collapsed)
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CloseTopmostShellOverlay())
        {
            e.Handled = true;
            return;
        }

        if (TryHandleShellShortcut(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Resolves the key a shortcut should match on.
    ///
    /// WPF routes F10 and every Alt combination as <see cref="Key.System"/>,
    /// putting the real key in <see cref="KeyEventArgs.SystemKey"/>, because
    /// those keys traditionally open a window menu. Matching on Key alone
    /// silently loses F10.
    /// </summary>
    internal static Key EffectiveShortcutKey(Key key, Key systemKey)
    {
        return key == Key.System ? systemKey : key;
    }

    /// <summary>
    /// Window-level shortcuts. These run in the preview pass so they work from
    /// anywhere in the shell, but text-entry chords keep priority for the
    /// focused editor: composers already bind Ctrl+Enter to send.
    /// </summary>
    private bool TryHandleShellShortcut(KeyEventArgs e)
    {
        return TryHandleShellShortcut(
            EffectiveShortcutKey(e.Key, e.SystemKey),
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control,
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift,
            (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt);
    }

    /// <summary>
    /// The chord is passed explicitly rather than read from the live keyboard so
    /// the control plane can drive the same shortcut layer a person uses.
    /// Synthesising real key events would not work: operating-system input goes
    /// to whichever window is foreground, not to the one that was asked for.
    /// </summary>
    internal bool TryHandleShellShortcut(Key key, bool control, bool shift, bool alt)
    {

        // Function keys move between surfaces and carry no modifier. Nothing
        // destructive is bound here: Reset stays a deliberate pointer action.
        if (!control && !alt)
        {
            switch (key)
            {
                case Key.F1 when shift:
                    OpenUserGuide(CurrentContextualHelpArticleId(), Keyboard.FocusedElement);
                    return true;
                case Key.F2:
                    MatchSetupButton_Click(MatchSetupButton, new RoutedEventArgs());
                    return true;
                case Key.F5:
                    _ = RefreshActiveSessionAsync("Reloaded the session from disk.");
                    return true;
                case Key.F7:
                    RightRailToggleButton_Click(RightRailToggleButton, new RoutedEventArgs());
                    return true;
                case Key.F8:
                    ViewMenuButton_Click(ViewMenuButton, new RoutedEventArgs());
                    return true;
                case Key.F9:
                    ToggleAutoChatFromShortcut();
                    return true;
                case Key.F10:
                    AppSettingsButton_Click(AppSettingsButton, new RoutedEventArgs());
                    return true;
            }
        }

        // Ctrl+1/2/3 select a view. The navigation handlers already explain
        // themselves when a view is gated off, so gating is not repeated here.
        if (control && !shift && !alt)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1:
                    ArenaNavButton_Click(ShellNavigationRail, new RoutedEventArgs());
                    return true;
                case Key.D2 or Key.NumPad2:
                    AgentNavButton_Click(ShellNavigationRail, new RoutedEventArgs());
                    return true;
                case Key.D3 or Key.NumPad3:
                    CollaborateNavButton_Click(ShellNavigationRail, new RoutedEventArgs());
                    return true;
            }
        }

        if (!control && key != Key.F1)
        {
            return false;
        }

        switch (key)
        {
            case Key.F when !shift:
                TranscriptSearchButton_Click(TranscriptSearchButton, new RoutedEventArgs());
                return true;
            case Key.K when !shift:
                ShowCommandPalette();
                return true;
            case Key.M when !shift:
                MatchSetupButton_Click(MatchSetupButton, new RoutedEventArgs());
                return true;
            case Key.M when shift:
                ModelsButton_Click(ModelsButton, new RoutedEventArgs());
                return true;
            case Key.OemComma when !shift:
                AppSettingsButton_Click(AppSettingsButton, new RoutedEventArgs());
                return true;
            case Key.E when !shift:
                ExportTranscriptButton_Click(ExportTranscriptBottomButton, new RoutedEventArgs());
                return true;
            case Key.Enter when !shift && OneTurnButton.IsEnabled:
                OneTurnButton_Click(OneTurnButton, new RoutedEventArgs());
                return true;
            case Key.R when shift && RightRailToggleButton.IsEnabled:
                RightRailToggleButton_Click(RightRailToggleButton, new RoutedEventArgs());
                return true;
            case Key.F1:
                ShowShortcutsOverlay();
                return true;
            case Key.OemQuestion when !shift:
                ShowShortcutsOverlay();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// One key drives both directions of the run loop: start automatic chat, or
    /// pause it if it is already running.
    /// </summary>
    private void ToggleAutoChatFromShortcut()
    {
        if (ArenaRun.IsAutoChatRunning)
        {
            StopButton_Click(StopButton, new RoutedEventArgs());
            return;
        }

        if (!AutoChatButton.IsEnabled)
        {
            const string status = "Auto Chat is unavailable until the arena is ready.";
            SetLoadStatus(status);
            SetApplicationStatus("arena.auto-chat", "Arena", status, "arena");
            return;
        }

        AutoChatButton_Click(AutoChatButton, new RoutedEventArgs());
    }

    private bool _shortcutsOverlayOpen;

    /// <summary>
    /// Deferred and guarded for the same reasons the command palette is.
    /// ConfirmDialog.Show runs a nested message loop and does not return until
    /// the reader dismisses it, so opening it inline meant a control-plane F1
    /// waited for a human and timed out, and repeated presses stacked dialogs
    /// that the plane had no way to close.
    /// </summary>
    private void ShowShortcutsOverlay()
    {
        if (_shortcutsOverlayOpen)
        {
            foreach (var open in Application.Current.Windows.OfType<ConfirmDialog>().ToList())
            {
                open.Close();
            }

            return;
        }

        CloseNamedTransientShellFlyouts();
        Dispatcher.BeginInvoke(new Action(OpenShortcutsOverlay), DispatcherPriority.Background);
    }

    private void OpenShortcutsOverlay()
    {
        // Checked here as well: queued opens are dispatched before any of them
        // has opened anything, so the flag above cannot catch a burst.
        if (_shortcutsOverlayOpen)
        {
            return;
        }

        _shortcutsOverlayOpen = true;
        try
        {
            var body = string.Join(
                Environment.NewLine,
                ShellShortcuts.Select(shortcut => $"{shortcut.Keys}  -  {shortcut.Action}"));
            ConfirmDialog.Show(
                this,
                _theme,
                "Keyboard shortcuts",
                body,
                "Close",
                "Close",
                ConfirmDialogTone.Info);
        }
        finally
        {
            _shortcutsOverlayOpen = false;
        }
    }

    /// <summary>
    /// Function keys move between surfaces, Ctrl chords perform actions, and
    /// Ctrl+number selects a view. F3 is intentionally absent: it means "find
    /// next" on Windows, and transcript search filters rather than stepping
    /// through matches, so there is nothing honest to bind it to yet.
    /// </summary>
    internal static IReadOnlyList<(string Keys, string Action)> ShellShortcuts { get; } =
    [
        ("Ctrl+1", "Go to AI Lab"),
        ("Ctrl+2", "Go to Agent"),
        ("Ctrl+3", "Go to AI Collaborate"),
        ("F2", "Open or close Match Setup"),
        ("F5", "Reload the session from disk"),
        ("F7", "Show or hide the right rail"),
        ("F8", "Open the transcript view menu"),
        ("F9", "Start Auto Chat, or pause it"),
        ("F10", "Open App Settings"),
        ("Ctrl+K", "Open the command palette"),
        ("Ctrl+F", "Search the transcript"),
        ("Ctrl+M", "Open or close Match Setup"),
        ("Ctrl+Shift+M", "Open or close Models and assignments"),
        ("Ctrl+Enter", "Run one arena turn"),
        ("Ctrl+E", "Export the transcript"),
        ("Ctrl+,", "Open App Settings"),
        ("Ctrl+Shift+R", "Show or hide the right rail"),
        ("Shift+F1", "Open contextual Help Center guidance"),
        ("Ctrl+/ or F1", "Show this shortcut list"),
        ("Esc", "Close the topmost overlay")
    ];

    private bool CloseTopmostShellOverlay()
    {
        if (AppSettingsPanel.Visibility == Visibility.Visible)
        {
            CloseAppSettings();
            return true;
        }

        if (ProviderHealthPopup.IsOpen)
        {
            ProviderReachability.ClosePopup();
            return true;
        }

        if (TranscriptSearchPopup.IsOpen)
        {
            _transcriptSearchCoordinator?.CloseSearch();
            TranscriptSearchButton.Focus();
            return true;
        }

        if (TranscriptFiltersPopup.IsOpen)
        {
            TranscriptFiltersPopup.IsOpen = false;
            return true;
        }

        if (DebugMenuPopup.IsOpen)
        {
            DebugMenuPopup.IsOpen = false;
            return true;
        }

        if (ViewMenuPopup.IsOpen)
        {
            ViewMenuPopup.IsOpen = false;
            return true;
        }

        if (CurrentSetupTransferPopup.IsOpen)
        {
            CurrentSetupTransferPopup.IsOpen = false;
            return true;
        }

        if (GenerationCopyPopup.IsOpen)
        {
            GenerationCopyPopup.IsOpen = false;
            return true;
        }

        if (AgentComposerControlsPopup.IsOpen)
        {
            AgentComposerControlsPopup.IsOpen = false;
            return true;
        }

        if (DiagnosticDetailPopup.IsOpen)
        {
            DiagnosticsWorkflow.CloseDetail();
            return true;
        }

        if (AgentPerformanceDetailPopup.IsOpen)
        {
            AgentPerformance.CloseDetail();
            return true;
        }

        if (CustomMatchPanel.Visibility == Visibility.Visible)
        {
            CloseMatchSetupFlyout();
            return true;
        }

        if (ProviderModelsPanel.Visibility == Visibility.Visible)
        {
            CloseProviderModelsPanel();
            return true;
        }

        return false;
    }

    internal static void FocusOverlayEntry(Popup popup, UIElement entry)
    {
        popup.Dispatcher.BeginInvoke(() =>
        {
            if (popup.IsOpen && entry.IsVisible && entry.IsEnabled)
            {
                entry.Focus();
            }
        }, DispatcherPriority.Input);
    }

    internal static void RestoreOverlayFocus(
        IInputElement? preferredTarget,
        UIElement fallbackTarget,
        Func<bool> overlayRemainsClosed)
    {
        fallbackTarget.Dispatcher.BeginInvoke(() =>
        {
            if (!overlayRemainsClosed())
            {
                return;
            }

            if (Keyboard.FocusedElement is UIElement
                {
                    IsVisible: true,
                    IsEnabled: true,
                    Focusable: true
                } activeElement
                && !ReferenceEquals(activeElement, preferredTarget)
                && !ReferenceEquals(activeElement, fallbackTarget))
            {
                return;
            }

            if (preferredTarget is UIElement { IsVisible: true, IsEnabled: true } preferredElement
                && preferredElement.Focusable)
            {
                Keyboard.Focus(preferredElement);
                return;
            }

            if (fallbackTarget.IsVisible && fallbackTarget.IsEnabled)
            {
                fallbackTarget.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private static readonly TimeSpan ActiveSnapshotPollInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan IdleSnapshotPollInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ActiveProviderHealthInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleProviderHealthInterval = TimeSpan.FromSeconds(30);

    private bool _idlePollingCadenceActive;

    /// <summary>
    /// A backgrounded shell with nothing running does not need second-scale
    /// snapshot polling or a provider probe every three seconds. Cadence drops
    /// while idle and is restored as soon as the window is focused or a run
    /// starts.
    /// </summary>
    internal static bool ShouldUseIdlePollingCadence(bool windowActive, bool arenaBusy, bool autoChatRunning)
    {
        return !windowActive && !arenaBusy && !autoChatRunning;
    }

    private void ApplyPollingCadence()
    {
        var idle = ShouldUseIdlePollingCadence(IsActive, _arenaBusy, ArenaRun.IsAutoChatRunning);
        if (idle == _idlePollingCadenceActive)
        {
            return;
        }

        _idlePollingCadenceActive = idle;
        _refreshTimer.Interval = idle ? IdleSnapshotPollInterval : ActiveSnapshotPollInterval;
        _providerHealthTimer.Interval = idle ? IdleProviderHealthInterval : ActiveProviderHealthInterval;
    }

    private void ApplyMaximizedChromePadding()
    {
        // With WindowChrome the maximized client area overhangs the monitor by the
        // resize border plus the fixed frame; padding the root keeps edge content
        // visible and clickable.
        var overhang = SystemParameters.WindowResizeBorderThickness;
        RootLayout.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                overhang.Left + 3,
                overhang.Top + 3,
                overhang.Right + 3,
                overhang.Bottom + 3)
            : new Thickness(0);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        NavigationRailColumn.Width = new GridLength(ResolveNavigationRailWidth(e.NewSize.Width));
        ArenaControlGrid.Columns = ResolveArenaControlColumns(ResolveRightRailDockWidth(e.NewSize.Width));
        ApplyTopBarLayout(ShouldStackTopBar(e.NewSize.Width));
        var autoCollapse = ShouldAutoCollapseRightRail(e.NewSize.Width);
        if (autoCollapse != _rightRailAutoCollapseActive)
        {
            // The latch lets a narrow window override an expanded preference,
            // and IsRightRailEffectivelyCollapsed consults it alone once the
            // window is wide again. It was only ever set, never cleared here, so
            // narrowing the window once hid the rail for good: widening brought
            // it back to a state where the preference said expanded, the latch
            // still said collapsed, and only a toggle could break the tie.
            _rightRailWidthCollapseLatched = autoCollapse;
            _rightRailAutoCollapseActive = autoCollapse;
            _rightRailNarrowRevealRequested = false;
        }

        ApplyRightRailCollapsed(e.NewSize.Width);
    }

    private void ApplyTopBarLayout(bool stacked)
    {
        if (_topBarStacked == stacked)
        {
            return;
        }

        _topBarStacked = stacked;
        Grid.SetRow(TopBarStatus, 0);
        Grid.SetColumn(TopBarStatus, 0);
        Grid.SetColumnSpan(TopBarStatus, stacked ? 2 : 1);
        Grid.SetRow(TopBarCommandPanel, stacked ? 1 : 0);
        Grid.SetColumn(TopBarCommandPanel, stacked ? 0 : 1);
        Grid.SetColumnSpan(TopBarCommandPanel, stacked ? 2 : 1);
        TopBarCommandPanel.Margin = stacked
            ? new Thickness(0, 6, 0, 0)
            : new Thickness(14, 0, 0, 0);
    }

    internal static bool ShouldStackTopBar(double windowWidth)
    {
        return !double.IsFinite(windowWidth)
            || windowWidth <= 0
            || windowWidth < TopBarInlineMinWidth;
    }

    internal static double ResolveNavigationRailWidth(double windowWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return NavigationRailStandardWidth;
        }

        if (windowWidth >= NavigationRailComfortableMinWindowWidth)
        {
            return NavigationRailComfortableWidth;
        }

        if (windowWidth >= NavigationRailStandardMinWindowWidth)
        {
            var comfortableProgress = Math.Clamp(
                (windowWidth - NavigationRailStandardMinWindowWidth)
                / (NavigationRailComfortableMinWindowWidth - NavigationRailStandardMinWindowWidth),
                0,
                1);
            return NavigationRailStandardWidth
                + ((NavigationRailComfortableWidth - NavigationRailStandardWidth) * comfortableProgress);
        }

        var progress = Math.Clamp(
            (windowWidth - SupportedMinimumWindowWidth)
            / (NavigationRailStandardMinWindowWidth - SupportedMinimumWindowWidth),
            0,
            1);
        return NavigationRailCompactWidth
            + ((NavigationRailStandardWidth - NavigationRailCompactWidth) * progress);
    }

    private void UniversalStatusCenter_PresentationChanged(object? sender, EventArgs e)
    {
        ShellTopBar.UpdateCollapsedStatusCenterPresentation(
            UniversalStatusCenter.PrimaryStateText,
            UniversalStatusCenter.PrimarySummary,
            UniversalStatusCenter.ActiveCountLabel);
    }

    private void ApplicationStatusCenter_NavigationRequested(object? sender, string target)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => ApplicationStatusCenter_NavigationRequested(sender, target),
                DispatcherPriority.Input);
            return;
        }

        var separator = target.IndexOf(':');
        var surface = (separator < 0 ? target : target[..separator]).Trim().ToLowerInvariant();
        var argument = separator < 0 ? "" : target[(separator + 1)..].Trim();
        if (argument.Length > 0)
        {
            try
            {
                argument = Uri.UnescapeDataString(argument);
            }
            catch (UriFormatException)
            {
                return;
            }
        }

        switch (surface)
        {
            case "models":
                ShowProviderModelsPanel();
                if (argument.Length > 0)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ProviderModelsPanel.Visibility == Visibility.Visible)
                        {
                            ProviderModelsPanel.SelectModel(argument);
                        }
                    }, DispatcherPriority.Input);
                }
                break;
            case "provider":
                OpenModelProviderSettings();
                break;
            case "settings":
                AppSettingsWorkflow.SetVisible(true);
                break;
            case "agent":
                if (IsAgentWorkspaceEnabled(_wpfSettings))
                {
                    ShowAgentPanel();
                }
                break;
            case "collaborate":
                ShowCollaboratePanel();
                break;
            case "arena":
                ShowTranscriptPanel(clearFilters: false);
                break;
        }
    }

    private void ApplyRightRailCollapsed()
    {
        ApplyRightRailCollapsed(ActualWidth);
    }

    private void ApplyRightRailCollapsed(double windowWidth)
    {
        var collapsed = IsRightRailEffectivelyCollapsed(
            _wpfSettings.RightRailCollapsed,
            _rightRailAutoCollapseActive,
            _rightRailNarrowRevealRequested,
            _rightRailWidthCollapseLatched);
        var overlay = ShouldOverlayRightRail(_rightRailAutoCollapseActive, collapsed);
        var statusCenterHadFocus = UniversalStatusCenter.IsKeyboardFocusWithin
            || UniversalStatusCenter.IsDashboardOpen;
        var restoreFocusAfterCollapse = collapsed && RightRailScrollViewer.IsKeyboardFocusWithin;
        restoreFocusAfterCollapse |= collapsed
            && (ArenaControlsCard.IsKeyboardFocusWithin || statusCenterHadFocus);
        RightRailColumn.Width = collapsed || overlay
            ? new GridLength(0)
            : new GridLength(ResolveRightRailDockWidth(windowWidth));
        RightRailScrollViewer.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RightRailHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedStatusCenterButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (collapsed && UniversalStatusCenter.IsDashboardOpen)
        {
            UniversalStatusCenter.CloseDashboard(restoreFocus: false);
        }
        ApplyRightRailPresentation(overlay, windowWidth);
        RightRailToggleGlyph.Text = collapsed ? "" : "";
        var temporaryLayout = _rightRailAutoCollapseActive && !_wpfSettings.RightRailCollapsed;
        RightRailToggleButton.ToolTip = collapsed
            ? temporaryLayout ? "Show right rail temporarily" : "Show right rail"
            : temporaryLayout ? "Hide temporarily revealed right rail" : "Hide right rail";
        AutomationProperties.SetName(
            RightRailToggleButton,
            collapsed ? "Show right rail" : "Hide right rail");
        AutomationProperties.SetHelpText(
            RightRailToggleButton,
            collapsed
                ? "Show the right rail panels to inspect supporting details."
                : "Hide the right rail panels to give the center workspace more room.");
        AutomationProperties.SetItemStatus(
            RightRailToggleButton,
            collapsed ? "collapsed" : "expanded");

        // Resizing the window lands here too, so this only speaks when the
        // collapsed state actually flipped.
        PublishRailChanged();

        if (restoreFocusAfterCollapse)
        {
            var focusTarget = statusCenterHadFocus
                ? CollapsedStatusCenterButton
                : RightRailToggleButton;
            Dispatcher.BeginInvoke(() =>
            {
                if (RightRailScrollViewer.Visibility == Visibility.Collapsed
                    && focusTarget.IsVisible
                    && focusTarget.IsEnabled)
                {
                    Keyboard.Focus(focusTarget);
                }
            }, DispatcherPriority.Input);
        }
    }

    internal static double ResolveRightRailDockWidth(double windowWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return RightRailExpandedWidth;
        }

        var progress = Math.Clamp(
            (windowWidth - SupportedMinimumWindowWidth)
            / (RightRailFullWidthMinWindowWidth - SupportedMinimumWindowWidth),
            0,
            1);
        return RightRailCompactWidth
            + ((RightRailExpandedWidth - RightRailCompactWidth) * progress);
    }

    internal static double ResolveExpandedCenterWorkspaceWidth(double windowWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return 0;
        }

        return Math.Max(
            0,
            windowWidth - ResolveNavigationRailWidth(windowWidth) - ResolveRightRailDockWidth(windowWidth));
    }

    internal static int ResolveArenaControlColumns(double rightRailWidth)
    {
        return double.IsFinite(rightRailWidth) && rightRailWidth < 320 ? 2 : 3;
    }

    private void ApplyRightRailPresentation(bool overlay, double windowWidth)
    {
        Grid.SetColumn(RightRailHost, overlay ? 1 : 2);
        Grid.SetColumnSpan(RightRailHost, overlay ? 2 : 1);
        Panel.SetZIndex(RightRailHost, overlay ? 12 : 0);
        RightRailHost.Width = overlay ? ResolveRightRailDockWidth(windowWidth) : double.NaN;
        RightRailHost.HorizontalAlignment = overlay
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
        RightRailHost.Background = overlay
            ? ResourceBrush("PanelBrush")
            : Brushes.Transparent;
        RightRailHost.Margin = overlay
            ? new Thickness(10, 0, 0, 0)
            : new Thickness(0, 14, 14, 14);
    }

    internal static bool ShouldAutoCollapseRightRail(double windowWidth)
    {
        return double.IsFinite(windowWidth)
            && windowWidth > 0
            && windowWidth < RightRailAutoCollapseWidth;
    }

    internal static bool IsRightRailEffectivelyCollapsed(
        bool userCollapsed,
        bool autoCollapseActive,
        bool narrowRevealRequested,
        bool widthCollapseLatched = false)
    {
        if (userCollapsed)
        {
            return true;
        }

        return autoCollapseActive
            ? !narrowRevealRequested
            : widthCollapseLatched;
    }

    internal static bool ShouldUseCollapsedStatusCenterLauncher(
        bool userCollapsed,
        bool autoCollapseActive,
        bool widthCollapseLatched)
    {
        return IsRightRailEffectivelyCollapsed(
            userCollapsed,
            autoCollapseActive,
            narrowRevealRequested: true,
            widthCollapseLatched);
    }

    internal static bool ShouldOverlayRightRail(bool autoCollapseActive, bool collapsed)
    {
        return autoCollapseActive && !collapsed;
    }

    private void UpdateVoiceToggleButton()
    {
        var speaking = _voiceNarrationService.IsSpeaking;
        VoiceToggleIcon.Text = speaking ? "" : "";
        VoiceToggleLabel.Text = speaking ? "Stop Voice" : "Speak";
        SpeakLatestNarratorButton.Tag = speaking ? "speaking" : null;
        SpeakLatestNarratorButton.ToolTip = speaking ? "Stop voice playback" : "Speak latest narrator turn";
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
        if (CollaboratePanel.Visibility == Visibility.Visible)
        {
            Collaborate.ExportCurrentConversation(this);
            return;
        }

        TranscriptExportCoordinator.ExportTranscript();
    }

    private void ApplyProviderStatusProjection(CoreSessionSummary session, ArenaViewSnapshot snapshot)
    {
        if (_activeSession is null
            || !_activeSession.Id.Equals(session.Id, StringComparison.Ordinal)
            || ProviderProjectionIsStale(session.LastModified, _activeSnapshotWriteUtc))
        {
            return;
        }

        _statusCenterSessionId = session.Id;
        ShellTopBar.Presentation.StatusCenter.SetContext(ProviderStatusIdentity(session.Id, snapshot));

        PublishProviderTransition(snapshot);
        _activeSession = session;
        _experimentLabCoordinator?.NotifyActiveSessionChanged(session.Id);
        _activeSnapshotWriteUtc = session.LastModified;
        ApplyArenaActionProjection(snapshot);
        _collaborateCoordinator?.RefreshProviderState();
        _agentWorkspaceCoordinator?.RefreshProviderState();
    }

    internal static bool ProviderProjectionIsStale(
        DateTimeOffset candidateWriteTime,
        DateTimeOffset authoritativeWriteTime)
    {
        // Session timestamps are the generation token available to the provider
        // heartbeat. Keep equality eligible because filesystem timestamp precision
        // can collapse two reads of the same durable generation to one value.
        return candidateWriteTime.UtcDateTime < authoritativeWriteTime.UtcDateTime;
    }

    private void ApplyArenaActionProjection(ArenaViewSnapshot snapshot)
    {
        _lastRenderedSnapshot = snapshot;
        OperatorTurn.ApplySnapshot(snapshot);
        var arenaReadiness = ArenaOperationCoordinator.EvaluateReadiness(snapshot);
        ArenaOperations.UpdateReadiness(arenaReadiness);
        PublishArenaReadiness(snapshot, arenaReadiness);
        UpdateTopBarStatus(snapshot);
        SessionOverview.UpdateSessionOverview(snapshot);
        PopulateTranscript(snapshot.Messages);
    }

    private void PublishTranscriptMessageEvents(IReadOnlyList<TranscriptMessage> messages)
    {
        var previousMaxTurn = _lastRenderedMessages
            .Select(message => message.Turn)
            .DefaultIfEmpty(0)
            .Max();
        foreach (var message in messages.Where(item => item.Turn > previousMaxTurn).OrderBy(item => item.Turn))
        {
            _controlPlaneEvents.Publish(
                "message.added",
                "Transcript message added.",
                new
                {
                    message.Turn,
                    message.Speaker,
                    message.SpeakerId,
                    message.Status,
                    TextLength = message.Text?.Length ?? 0
                });
        }
    }

    private void PublishProviderTransition(ArenaViewSnapshot snapshot)
    {
        var previous = _lastRenderedSnapshot;
        var online = snapshot.ProviderOnline;
        var changed = previous is null || previous.ProviderOnline != online;
        var errorChanged = previous is not null
            && !string.Equals(previous.ProviderLastError, snapshot.ProviderLastError, StringComparison.Ordinal);
        var providerChanged = previous is null
            || ProviderStatusIdentity(snapshot.SessionId, previous) != ProviderStatusIdentity(snapshot.SessionId, snapshot);
        if (!changed && !errorChanged && !providerChanged)
        {
            return;
        }

        ShellTopBar.Presentation.StatusCenter.PublishHeartbeatTransition(
            "provider.connection",
            "Provider",
            online,
            online ? "Provider connection restored." : "Provider is offline.",
            online
                ? "Provider health evidence is available again."
                : ProviderConfigurationControlService.SanitizeError(
                    snapshot.ProviderLastError,
                    snapshot.ProviderApiToken),
            ProviderStatusIdentity(_activeSession?.Id ?? snapshot.SessionId, snapshot));

        _controlPlaneEvents.Publish(
            online ? "provider.online" : "provider.offline",
            online ? "Provider is online." : "Provider is offline.",
            new
            {
                snapshot.ProviderModel,
                ProviderBaseUrl = ProviderConfigurationControlService.SanitizeBaseUrl(snapshot.ProviderBaseUrl),
                ProviderLastError = ProviderConfigurationControlService.SanitizeError(
                    snapshot.ProviderLastError,
                    snapshot.ProviderApiToken)
            });
    }

    private static ApplicationStatusIdentity ProviderStatusIdentity(string sessionId, ArenaViewSnapshot snapshot)
    {
        var config = new ModelProviderConfig
        {
            BaseUrl = snapshot.ProviderBaseUrl,
            ApiMode = snapshot.ProviderApiMode,
            ApiToken = snapshot.ProviderApiToken
        };
        return ProviderStatusIdentity(sessionId, config);
    }

    private static ApplicationStatusIdentity ProviderStatusIdentity(string sessionId, ModelProviderConfig config)
    {
        return new ApplicationStatusIdentity(
            sessionId,
            ProviderModelCatalogProjectionService.ConnectionFingerprint(sessionId, config));
    }

    private void PublishArenaReadiness(ArenaViewSnapshot snapshot, ArenaActionReadiness readiness)
    {
        const string readinessKey = "arena.readiness";
        if (readiness.CanRun)
        {
            ShellTopBar.Presentation.StatusCenter.Resolve(readinessKey);
            if (!_arenaBusy)
            {
                ShellTopBar.SetArenaRunStatusCompatibilityText("Ready.");
            }
            return;
        }

        if (!_arenaBusy)
        {
            // Preserve the hidden compatibility target for existing integrations
            // without routing its prose through the shell announcer. The typed
            // readiness entry below owns severity, lifetime, and one announcement.
            ShellTopBar.SetArenaRunStatusCompatibilityText(readiness.Message);
        }

        var state = ArenaReadinessStatusState(snapshot, readiness);
        ShellTopBar.Presentation.StatusCenter.PublishNotice(
            readinessKey,
            "Arena",
            state,
            readiness.Message,
            navigationTarget: "arena",
            identity: new ApplicationStatusIdentity(snapshot.SessionId),
            background: false,
            lifetime: ApplicationStatusLifetime.UntilResolved);
    }

    internal static ApplicationStatusState ArenaReadinessStatusState(
        ArenaViewSnapshot snapshot,
        ArenaActionReadiness readiness) =>
        !readiness.CanRun
        && (snapshot.MatchEnded
            || (snapshot.FactoryMode
                && ArenaOperationCoordinator.FactoryInputState(snapshot)
                    == FactoryConversationInputState.MissingRoot))
            ? ApplicationStatusState.Blocked
            : ApplicationStatusState.Warning;

    private void SavedStateModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _savedStateCoordinator?.OnModeSelectionChanged();
    }

    private void SavedStateItemPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _savedStateCoordinator?.OnItemSelectionChanged();
    }

    private void SavedStateShowEmptyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _savedStateCoordinator?.SetShowEmptySessions(SavedStateShowEmptyCheckBox.IsChecked == true);
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

    private async void ForkCurrentMatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedStateCoordinator is not null)
        {
            await _savedStateCoordinator.ForkCurrentAsync();
        }
    }

    private async void OpenForkParentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savedStateCoordinator is not null)
        {
            await _savedStateCoordinator.OpenParentAsync();
        }
    }

    private async Task SaveSnapshotWithFeedbackAsync(
        AIArena.Core.Models.ArenaSnapshot snapshot,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        SetSaveStatus("Saving...", ResourceBrush("MutedTextBrush"));
        try
        {
            await _coreSessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            SetSaveStatus($"Saved {DateTime.Now:h:mm tt}", ResourceBrush("Arena.Brush.Success"));
        }
        catch (Exception ex)
        {
            SetSaveStatus(
                AppErrorPresenter.Present(ex, AppErrorContext.SavedState).DisplayText,
                ResourceBrush("DangerTextBrush"));
            throw;
        }
    }

    private void SetSaveStatus(string text, Brush brush)
    {
        SaveStatusText.Text = text;
        SaveStatusText.Foreground = brush;
        SaveStatusText.ToolTip = text;
    }

    private Task<bool> RunArenaBusyForCoordinatorAsync(string status, Func<Task> action)
    {
        return RunArenaBusyAsync(status, action);
    }

    private Task<bool> RunArenaBusyForCoordinatorAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat)
    {
        return RunArenaBusyAsync(status, operationButton, action, allowDuringAutoChat);
    }

    private static async Task EnsureInternetBackendForSearchAsync(CancellationToken cancellationToken)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        var status = await app.EnsureInternetSearchAsync(cancellationToken);
        if (!status.Started && !status.AlreadyRunning)
        {
            throw new InvalidOperationException(status.Message);
        }
    }

    private Task PersistInternetSettingForActiveSessionAsync(
        string sessionId,
        bool enabled,
        CancellationToken workflowCancellationToken)
    {
        var operationCoordinator = _arenaOperationCoordinator;
        if (operationCoordinator is null || string.IsNullOrWhiteSpace(sessionId) || _shutdownInProgress)
        {
            return Task.FromException(new OperationCanceledException("Internet setting cannot be saved while the app is shutting down or no session is active."));
        }

        return operationCoordinator.TrackAsync(async shutdownCancellationToken =>
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                workflowCancellationToken,
                shutdownCancellationToken);
            var cancellationToken = linkedCancellation.Token;
            var operationLockTaken = false;
            try
            {
                await _arenaOperationLock.WaitAsync(cancellationToken);
                operationLockTaken = true;
                SetSaveStatus("Saving Internet setting...", ResourceBrush("MutedTextBrush"));
                var saved = await InternetWorkflowCoordinator.PersistSessionSettingAsync(
                    _coreSessionStore,
                    _eventLogStore,
                    sessionId,
                    enabled,
                    cancellationToken);
                if (!saved)
                {
                    throw new InvalidOperationException($"Session '{sessionId}' could not be loaded.");
                }

                if (_activeSession?.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    SetSaveStatus($"Saved {DateTime.Now:h:mm tt}", ResourceBrush("Arena.Brush.Success"));
                }
            }
            finally
            {
                if (operationLockTaken)
                {
                    _arenaOperationLock.Release();
                }
            }

        });
    }

    private Task<bool> RunArenaBusyForCoordinatorAsync(
        string status,
        Button? operationButton,
        Func<CancellationToken, Task> action,
        bool allowDuringAutoChat)
    {
        return ArenaOperations.RunAsync(status, operationButton, action, allowDuringAutoChat);
    }

    private Task SaveSnapshotForCoordinatorAsync(AIArena.Core.Models.ArenaSnapshot snapshot, string sessionId)
    {
        return SaveSnapshotWithFeedbackAsync(snapshot, sessionId);
    }

    private Task SaveSnapshotForProviderAsync(
        AIArena.Core.Models.ArenaSnapshot snapshot,
        string sessionId,
        CancellationToken cancellationToken)
    {
        return SaveSnapshotWithFeedbackAsync(snapshot, sessionId, cancellationToken);
    }

    private Task RefreshActiveSessionForCoordinatorAsync(string status)
    {
        return RefreshActiveSessionAsync(status);
    }

    private Task RefreshActiveSessionForProviderAsync(string status, CancellationToken cancellationToken)
    {
        return RefreshActiveSessionAsync(status, cancellationToken);
    }

    private void SetLoadStatus(string status)
    {
        LoadStatus.Text = status;
    }

    private void SetArenaRunStatus(string status)
    {
        ArenaRunStatus.Text = status;
    }

    private void SetApplicationStatus(
        string key,
        string source,
        string status,
        string? navigationTarget = null)
    {
        ShellTopBar.Presentation.PublishCompatibilityStatus(
            key,
            source,
            status,
            navigationTarget: navigationTarget,
            identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""));
    }

    private void PublishCollaborateRecoveryWarning(string warning)
    {
        ShellTopBar.Presentation.StatusCenter.PublishNotice(
            "collaborate.history-recovery",
            "Collaborate",
            ApplicationStatusState.Warning,
            warning,
            navigationTarget: "collaborate",
            identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
            lifetime: ApplicationStatusLifetime.UntilResolved);
    }

    private void SetTranscriptMutationStatus(string status)
    {
        var succeeded = status.StartsWith("Deleted turn ", StringComparison.Ordinal)
            || status.StartsWith("Pinned turn ", StringComparison.Ordinal)
            || status.StartsWith("Unpinned turn ", StringComparison.Ordinal);
        var blocked = status.Contains("Factory group root", StringComparison.Ordinal);
        var state = succeeded
            ? ApplicationStatusState.Succeeded
            : blocked
                ? ApplicationStatusState.Blocked
                : ApplicationStatusState.Failed;
        ShellTopBar.Presentation.StatusCenter.PublishNotice(
            "app.transcript-mutation",
            "Transcript",
            state,
            status,
            navigationTarget: "arena",
            identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
            lifetime: succeeded
                ? ApplicationStatusLifetime.Transient
                : ApplicationStatusLifetime.UntilResolved);
    }

    /// <summary>False when the arena was busy and the work was skipped.</summary>
    private async Task<bool> RunArenaBusyAsync(string status, Func<Task> action)
    {
        return await RunArenaBusyAsync(status, null, action);
    }

    private async Task<bool> RunArenaBusyAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat = false)
    {
        return await ArenaOperations.RunAsync(status, operationButton, action, allowDuringAutoChat);
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

    private async Task RefreshActiveSessionAsync(
        string status,
        CancellationToken cancellationToken = default,
        bool setArenaStatus = true)
    {
        if (_activeSession is null)
        {
            return;
        }

        var observedWriteTime = TryGetSessionDirectoryLastModified(_activeSession.SnapshotPath);
        var latest = observedWriteTime is null
            ? (await _coreSessionStore.ListSessionsAsync(SessionListingDetail.Identity, cancellationToken)).FirstOrDefault(session => session.Id == _activeSession.Id)
            : _activeSession with { LastModified = observedWriteTime.Value };
        if (latest is not null)
        {
            await LoadSessionAsync(latest, force: true, cancellationToken);
        }

        LoadStatus.Text = status;
        if (setArenaStatus)
        {
            ArenaRunStatus.Text = status;
        }
    }

    private Task RefreshActiveSessionForTranscriptMutationAsync(string status) =>
        RefreshActiveSessionAsync(status, setArenaStatus: false);

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

    private void OnSystemThemePreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        var propertyName = e.PropertyName;
        if (!ShouldReapplySystemTheme(_wpfSettings.ThemeId, propertyName)
            || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!Dispatcher.HasShutdownStarted
                    && ShouldReapplySystemTheme(_wpfSettings.ThemeId, propertyName))
                {
                    ShellNavigation.ApplyTheme("system", persist: false, rerender: true);
                }
            },
            DispatcherPriority.Background);
    }

    private void OnSystemMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SystemMotionPreferences.IsAnimationPreferenceChange(e.PropertyName)
            || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                if (Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                _arenaOperationCoordinator?.RefreshMotionPreference();
                _appSettingsCoordinator?.RefreshMotionPreference();
                _agentBoardCoordinator?.RefreshMotionPreference();
            },
            DispatcherPriority.Background);
    }

    internal static bool ShouldReapplySystemTheme(string? themeId, string? propertyName)
    {
        return string.Equals(themeId?.Trim(), "system", StringComparison.OrdinalIgnoreCase)
            && SystemThemePreferences.IsThemePreferenceChange(propertyName);
    }

    private Brush ResourceBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.White;
    }

    private void RefreshGeneratedThemeSurfaces()
    {
        WindowChromeService.ApplyThemeChromeColor(this, _theme);
        _userGuideWindowHost.RefreshTheme(this);
        _collaborateCoordinator?.RefreshTheme();
        _agentWorkspaceCoordinator?.RefreshTheme();
        _telemetryWorkflowCoordinator?.RefreshTheme();
    }

    private static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        return ShellUiHelpers.BlendBrush(baseBrush, accentBrush, accentAmount);
    }

    private Brush AccentForSpeaker(string speaker)
    {
        return AgentAccentService.ResolveBrush(speaker, AccentColorForSpeaker(speaker), ResourceBrush);
    }

    private string AccentColorForSpeaker(string speaker)
    {
        var normalized = AgentAccentService.NormalizeSpeakerId(speaker);
        if (normalized.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return _lastRenderedSnapshot?.NarratorAccentColor ?? "";
        }

        return _lastRenderedSnapshot?.Agents
            .FirstOrDefault(agent => agent.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?.AccentColor ?? "";
    }

    private void ArenaNavButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyLabViewMode(_wpfSettings.LabViewMode, persist: false);
    }

    private void ExperimentLabNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowExperimentLabPanel();
    }

    private void MatchSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomMatchPanel.Visibility == Visibility.Visible)
        {
            CloseMatchSetupFlyout();
            return;
        }

        ShowCustomMatchPanel();
    }

    private void CloseMatchSetupButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMatchSetupFlyout();
    }

    private void CustomMatchNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCustomMatchPanel();
    }

    private void LabViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            if (CustomMatchPanel.Visibility == Visibility.Visible)
            {
                CloseMatchSetupFlyout();
            }

            ApplyLabViewMode(tag, persist: true);
        }
    }

    private void ApplyLabViewMode(string mode, bool persist)
    {
        var world = IsWorldDebugEnabled(_wpfSettings)
            && "world".Equals(mode, StringComparison.OrdinalIgnoreCase);
        if (world)
        {
            ShowWorldPanel();
        }
        else
        {
            ShowTranscriptPanel(clearFilters: false);
        }

        var normalized = world ? "world" : "transcript";
        if (!_wpfSettings.LabViewMode.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            _wpfSettings.LabViewMode = normalized;
            if (persist)
            {
                _wpfSettingsStore.Save(_wpfSettings);
            }
        }
    }

    private void UpdateLabViewToggle()
    {
        var world = AgentWorldPanel.Visibility == Visibility.Visible;
        StyleLabViewButton(LabTranscriptViewButton, !world);
        StyleLabViewButton(LabWorldViewButton, world);
    }

    private void StyleLabViewButton(Button button, bool active)
    {
        button.BorderBrush = ResourceBrush(active ? "PrimaryBorderBrush" : "ControlBorderBrush");
        button.Foreground = ResourceBrush(active ? "TextBrush" : "MutedTextBrush");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void AgentNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAgentWorkspaceEnabled(_wpfSettings))
        {
            ApplyAgentWorkspaceVisibility();
            const string status = "Enable Agent workspace in Settings to show it in navigation.";
            SetLoadStatus(status);
            SetApplicationStatus("agent.navigation", "Agent", status, "settings");
            return;
        }

        ShowAgentPanel();
    }

    private void CollaborateNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCollaboratePanel();
    }

    private void SessionOverviewMatch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowCustomMatchPanel();
        e.Handled = true;
    }

    private void SessionOverviewTurns_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowTranscriptPanel(clearFilters: false);
        e.Handled = true;
    }

    private void SessionOverviewPerformance_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RevealAgentPerformance();
        e.Handled = true;
    }

    private void RevealAgentPerformance()
    {
        AgentPerformanceExpander.IsExpanded = true;
        AgentPerformanceCard.BringIntoView();
        Dispatcher.BeginInvoke(() => AgentPerformanceCard.BringIntoView(), DispatcherPriority.Background);
    }

    private void SessionOverviewProvider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenModelProviderSettings();
        e.Handled = true;
    }

    private void SessionOverviewHotspot_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsKeyboardActivationKey(e.Key) || sender is not FrameworkElement { Tag: string action })
        {
            return;
        }

        switch (action)
        {
            case "match":
                ShowCustomMatchPanel();
                break;
            case "turns":
                ShowTranscriptPanel(clearFilters: false);
                break;
            case "performance":
                RevealAgentPerformance();
                break;
            case "provider":
                OpenModelProviderSettings();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ShowTranscriptPanel(bool clearFilters)
    {
        var previousSurface = _activeShellSurface;
        ShellNavigation.ShowTranscriptPanel();
        _activeShellSurface = ShellSurface.Lab;
        ApplyShellCommandState(_activeShellSurface);
        UpdateLabViewToggleVisibility();
        UpdateLabViewToggle();
        ResetRightRailAfterSurfaceChange(previousSurface);

        if (clearFilters)
        {
            ClearTranscriptFilters();
        }
    }

    private void ShowCustomMatchPanel()
    {
        var opening = CustomMatchPanel.Visibility != Visibility.Visible;
        if (opening)
        {
            _matchSetupReturnSurface = _activeShellSurface switch
            {
                ShellSurface.MatchSetup => ShellSurface.Lab,
                ShellSurface.Models => _providerModelsReturnSurface,
                _ => _activeShellSurface
            };
            _matchSetupFocusReturnTarget = Keyboard.FocusedElement ?? MatchSetupButton;
        }

        _providerModelsFocusReturnTarget = null;
        _providerModelsReturnSurface = ShellSurface.Lab;
        ShellNavigation.ShowCustomMatchPanel();
        // Flush synchronously after visibility changes and before WPF presents
        // the next frame. An open panel therefore never flashes a stale setup.
        RenderPendingMatchSetupSnapshot();
        _activeShellSurface = ShellSurface.MatchSetup;
        ApplyShellCommandState(_activeShellSurface);
        UpdateLabViewToggleVisibility();
        UpdateLabViewToggle();
        PublishMatchSetupOverlayChanged("Match Setup opened.");
        if (opening)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (CustomMatchPanel.Visibility == Visibility.Visible)
                {
                    CloseMatchSetupButton.Focus();
                }
            }, DispatcherPriority.Input);
        }
    }

    private void OpenMatchSetupFromControlPlane()
    {
        var wasOpen = CustomMatchPanel.Visibility == Visibility.Visible;
        var settingsReturnTarget = AppSettingsPanel.Visibility == Visibility.Visible
            ? _settingsFocusReturnTarget
            : null;

        AppSettingsWorkflow.SetVisible(false);
        CloseNamedTransientShellFlyouts();
        ShowCustomMatchPanel();

        if (!wasOpen && settingsReturnTarget is not null)
        {
            _matchSetupFocusReturnTarget = settingsReturnTarget;
        }
    }

    private void CloseNamedTransientShellFlyouts()
    {
        _providerReachabilityCoordinator?.ClosePopup();
        _transcriptSearchCoordinator?.CloseSearch();
        ViewMenuPopup.IsOpen = false;
        DebugMenuPopup.IsOpen = false;
        TranscriptFiltersPopup.IsOpen = false;
        CurrentSetupTransferPopup.IsOpen = false;
        GenerationCopyPopup.IsOpen = false;
        _diagnosticsWorkflowCoordinator?.CloseDetail();
        GenerationHelpPopup.IsOpen = false;
        AgentComposerControlsPopup.IsOpen = false;
        _agentPerformanceCoordinator?.CloseDetail();
    }

    private void ShowWorldPanel()
    {
        if (!IsWorldDebugEnabled(_wpfSettings))
        {
            ShowTranscriptPanel(clearFilters: false);
            return;
        }

        var previousSurface = _activeShellSurface;
        ShellNavigation.ShowWorldPanel();
        _activeShellSurface = ShellSurface.World;
        ApplyShellCommandState(_activeShellSurface);
        UpdateLabViewToggleVisibility();
        UpdateLabViewToggle();
        ResetRightRailAfterSurfaceChange(previousSurface);

        if (_lastRenderedSnapshot is { } snapshot)
        {
            ApplyWorldSnapshotIfVisible(snapshot);
        }
    }

    private void ShowAgentPanel()
    {
        if (!IsAgentWorkspaceEnabled(_wpfSettings))
        {
            ApplyAgentWorkspaceVisibility();
            return;
        }

        var previousSurface = _activeShellSurface;
        ShellNavigation.ShowAgentPanel();
        _activeShellSurface = ShellSurface.Agent;
        ApplyShellCommandState(_activeShellSurface);
        LabViewToggleGroup.Visibility = Visibility.Collapsed;
        ResetRightRailAfterSurfaceChange(previousSurface);
        AgentWorkspace.RefreshProviderState();
        AgentPromptText.Focus();
    }

    private void ShowExperimentLabPanel()
    {
        if (!IsExperimentLabEnabled(_wpfSettings))
        {
            ApplyExperimentLabVisibility();
            return;
        }

        var previousSurface = _activeShellSurface;
        ShellNavigation.ShowExperimentLabPanel();
        _activeShellSurface = ShellSurface.ExperimentLab;
        ApplyShellCommandState(_activeShellSurface);
        LabViewToggleGroup.Visibility = Visibility.Collapsed;
        ResetRightRailAfterSurfaceChange(previousSurface);
        ExperimentLabPanel.FocusFeatureSelector();
        _ = RefreshExperimentLabSafelyAsync();
    }

    private async Task RefreshExperimentLabSafelyAsync()
    {
        try
        {
            await ExperimentLab.RefreshFeatureAsync(ExperimentLabPanel.SelectedFeatureKey ?? "matrix");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ExperimentLabPanel.SetMatrixStatus($"Experiment Lab refresh failed safely ({exception.GetType().Name}).");
        }
    }

    private void ApplyWorldSnapshotIfVisible(ArenaViewSnapshot snapshot)
    {
        if (ShouldApplyWorldSnapshot(AgentWorldPanel.Visibility, IsWorldDebugEnabled(_wpfSettings)))
        {
            AgentWorld3D.ApplySnapshot(snapshot);
        }
    }

    internal static bool ShouldApplyWorldSnapshot(Visibility agentWorldPanelVisibility, bool worldDebugEnabled)
    {
        return worldDebugEnabled && agentWorldPanelVisibility == Visibility.Visible;
    }

    private void CloseMatchSetupFlyout()
    {
        if (CustomMatchPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var returnSurface = _matchSetupReturnSurface;
        var returnTarget = _matchSetupFocusReturnTarget;
        _matchSetupFocusReturnTarget = null;
        _matchSetupReturnSurface = ShellSurface.Lab;
        CurrentSetupTransferPopup.IsOpen = false;
        GenerationCopyPopup.IsOpen = false;

        switch (returnSurface)
        {
            case ShellSurface.World when IsWorldDebugEnabled(_wpfSettings):
                ShowWorldPanel();
                break;
            case ShellSurface.Agent when IsAgentWorkspaceEnabled(_wpfSettings):
                ShowAgentPanel();
                break;
            case ShellSurface.Collaborate:
                ShowCollaboratePanel();
                break;
            case ShellSurface.ExperimentLab when IsExperimentLabEnabled(_wpfSettings):
                ShowExperimentLabPanel();
                break;
            default:
                ShowTranscriptPanel(clearFilters: false);
                break;
        }

        PublishMatchSetupOverlayChanged("Match Setup closed.");
        RestoreOverlayFocus(
            returnTarget,
            MatchSetupButton,
            () => CustomMatchPanel.Visibility != Visibility.Visible);
    }

    private void ShowCollaboratePanel()
    {
        var previousSurface = _activeShellSurface;
        ShellNavigation.ShowCollaboratePanel();
        _activeShellSurface = ShellSurface.Collaborate;
        ApplyShellCommandState(_activeShellSurface);
        LabViewToggleGroup.Visibility = Visibility.Collapsed;
        ResetRightRailAfterSurfaceChange(previousSurface);
        Collaborate.RefreshProviderState();
        CollaboratePromptText.Focus();
    }

    private void CollaborateToolSection_Expanded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not Expander activeSection)
        {
            return;
        }

        foreach (var section in new[]
                 {
                     CollaborateDocumentsExpander,
                     CollaborateCalculatorExpander,
                     CollaborateMemoryExpander
                 })
        {
            if (!ReferenceEquals(section, activeSection))
            {
                section.IsExpanded = false;
            }
        }
    }

    private void ApplyShellCommandState(ShellSurface surface)
    {
        // Every surface change runs through here, whichever route caused it, so
        // this is where navigation is announced. Callers set _activeShellSurface
        // before calling, which is what SelectedControlPlaneView reads.
        PublishNavigationChanged();
        if (surface != ShellSurface.Lab)
        {
            TranscriptFiltersPopup.IsOpen = false;
        }

        // Match Setup replaces the center canvas, but remains part of the Lab
        // workspace. Its command state deliberately mirrors Lab so opening setup
        // does not make the toggle and transcript tools jump.
        var state = ShellCommandState.For(surface);
        SetMatchSetupButtonState(
            state.ShowMatchSetup,
            open: surface == ShellSurface.MatchSetup && state.ShowMatchSetup);
        SetModelsButtonState(
            state.ShowModels,
            open: surface == ShellSurface.Models && state.ShowModels);
        SearchCommandHost.Visibility = state.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
        ExportTranscriptBottomButton.Visibility = state.ShowExport ? Visibility.Visible : Visibility.Collapsed;
        ViewMenuHost.Visibility = state.ShowView ? Visibility.Visible : Visibility.Collapsed;

        if (!state.ShowSearch)
        {
            _transcriptSearchCoordinator?.CloseSearch();
        }

        if (!state.ShowView)
        {
            ViewMenuPopup.IsOpen = false;
        }

        if (state.ShowSearch)
        {
            var collaborate = surface == ShellSurface.Collaborate;
            SetSearchContext(
                collaborate ? ShellSearchSurface.Collaborate : ShellSearchSurface.Transcript,
                collaborate ? "Search AI Collaborate chats..." : "Search transcripts, agents, notes...",
                state.SearchHelpText);
            AutomationProperties.SetName(TranscriptSearchButton, state.SearchAutomationName);
            AutomationProperties.SetHelpText(TranscriptSearchButton, state.SearchHelpText);
        }

        if (state.ShowExport)
        {
            SetExportContext(surface == ShellSurface.Collaborate);
            AutomationProperties.SetName(ExportTranscriptBottomButton, state.ExportAutomationName);
            AutomationProperties.SetHelpText(ExportTranscriptBottomButton, state.ExportHelpText);
        }
        else
        {
            ExportStatusText.Text = "";
        }
    }

    private void ResetRightRailAfterSurfaceChange(ShellSurface previousSurface)
    {
        if (previousSurface == _activeShellSurface
            || previousSurface is ShellSurface.MatchSetup or ShellSurface.Models)
        {
            return;
        }

        RightRailScrollViewer.ScrollToTop();
        Dispatcher.BeginInvoke(() =>
        {
            if (RightRailScrollViewer.Visibility == Visibility.Visible)
            {
                RightRailScrollViewer.ScrollToTop();
            }
        }, DispatcherPriority.Background);
    }

    private void SetMatchSetupButtonState(bool visible, bool open)
    {
        MatchSetupButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MatchSetupButton.Content = open ? "Close Setup" : "Match Setup";
        MatchSetupButton.ToolTip = open ? "Close Match Setup" : "Open Match Setup";
        AutomationProperties.SetName(MatchSetupButton, open ? "Close Match Setup" : "Open Match Setup");
        AutomationProperties.SetHelpText(
            MatchSetupButton,
            open ? "Close the Match Setup flyout." : "Open the Match Setup flyout.");
    }

    private void SetModelsButtonState(bool visible, bool open)
    {
        ModelsButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // Keep the visible label stable so the top rail never reflows when the
        // surface opens. UIA and the selected chrome still expose the toggle.
        ModelsButton.Content = "Models";
        ModelsButton.ToolTip = open ? "Close Models" : "Open Models";
        AutomationProperties.SetName(ModelsButton, open ? "Close Models" : "Open Models");
        AutomationProperties.SetHelpText(
            ModelsButton,
            open
                ? "Close model catalog and agent assignments."
                : "Open model catalog and agent assignments.");
        AutomationProperties.SetItemStatus(ModelsButton, open ? "open" : "closed");
        ModelsButton.SetResourceReference(
            Control.BackgroundProperty,
            open ? "NavActiveBrush" : "InputBrush");
        ModelsButton.SetResourceReference(
            Control.BorderBrushProperty,
            open ? "PrimaryBorderBrush" : "DisabledBorderBrush");
    }

    private void SetSearchContext(ShellSearchSurface surface, string placeholder, string tooltip)
    {
        if (_transcriptSearchCoordinator is null)
        {
            TranscriptSearchText.Tag = placeholder;
            TranscriptSearchText.ToolTip = tooltip;
            TranscriptSearchButton.ToolTip = tooltip;
            return;
        }

        TranscriptSearch.SetSurface(surface, placeholder, tooltip);
    }

    private void SetExportContext(bool collaborate)
    {
        if (collaborate)
        {
            ExportStatusText.Text = "Export: chat";
            ExportStatusText.ToolTip = "Export the current AI Collaborate chat, run reviews, memory notes, and team traces.";
            ExportTranscriptBottomButton.ToolTip = "Export AI Collaborate chat";
            AutomationProperties.SetName(ExportTranscriptBottomButton, "Export AI Collaborate chat");
            AutomationProperties.SetHelpText(
                ExportTranscriptBottomButton,
                "Export the current AI Collaborate chat with run reviews and team trace steps.");
            return;
        }

        ExportTranscriptBottomButton.ToolTip = "Export transcript";
        AutomationProperties.SetName(ExportTranscriptBottomButton, "Export transcript");
        AutomationProperties.SetHelpText(
            ExportTranscriptBottomButton,
            "Export the current transcript scope to a file.");
        _transcriptExportCoordinator?.RefreshExportScopeStatus();
    }

    private async void CollaborateSendButton_Click(object sender, RoutedEventArgs e)
    {
        await Collaborate.SendAsync();
    }

    private void CollaborateClearButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.Clear();
    }

    private void CollaborateStopButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.Stop();
    }

    private void CollaborateNewChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (Collaborate.IsRunning)
        {
            return;
        }

        Collaborate.Clear();
        CollaboratePromptText.Focus();
    }

    private async void CollaboratePromptText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        await Collaborate.SendAsync();
    }

    private void CollaboratePlanPromptButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ApplyPromptTemplate("plan");
    }

    private void CollaborateCritiquePromptButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ApplyPromptTemplate("critique");
    }

    private void CollaborateShipPromptButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ApplyPromptTemplate("ship");
    }

    private void CollaborateExplainPromptButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ApplyPromptTemplate("explain");
    }

    private void CollaborateProviderSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Collaborate.IsRunning)
        {
            return;
        }

        OpenModelProviderSettings();
    }

    private void CollaborateModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _collaborateCoordinator?.RefreshProviderState();
    }

    private void CollaborateRoundsPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _collaborateCoordinator?.RefreshProviderState();
    }

    private void CollaborateRoundsPicker_TextChanged(object sender, TextChangedEventArgs e)
    {
        _collaborateCoordinator?.RefreshProviderState();
    }

    private async void CollaborateAddDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await Collaborate.AddDocumentsAsync(this);
    }

    private void CollaborateClearDocumentsButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ClearDocuments();
    }

    private void CollaborateRunCalculatorButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.RunCalculatorTool();
    }

    private void CollaborateClearCalculationsButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ClearCalculations();
    }

    private void CollaborateSaveMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.SaveMemoryNote();
    }

    private void CollaborateClearMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        Collaborate.ClearMemoryNotes();
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
        Dispatcher.BeginInvoke(() => TranscriptItems.ScrollToTop(), DispatcherPriority.Background);
    }

    private void ResetTranscriptFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ClearTranscriptFilters();
        SelectedTranscriptTurnFilterEntry().Focus();
    }

    private UIElement SelectedTranscriptTurnFilterEntry() =>
        TranscriptTurnFilterPicker.SelectedItem as UIElement ?? TranscriptTurnFilterPicker;

    private void TranscriptTurnFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        TranscriptFilter_Changed(sender, e);

    private void TranscriptFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_transcriptSearchCoordinator is null || TranscriptItems is null)
        {
            return;
        }

        _transcriptSearchCoordinator.OnFilterChanged(ReferenceEquals(sender, TranscriptSearchText));
        if (CollaboratePanel.Visibility == Visibility.Visible)
        {
            SetExportContext(collaborate: true);
        }
        else
        {
            TranscriptExportCoordinator.RefreshExportScopeStatus();
        }
    }

    private void ClearTranscriptSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptSearchCoordinator?.ClearSearch();
    }

    private async void SearchAllSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        var query = TranscriptSearchText.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            TranscriptSearch.ShowCrossSessionMessage("Type a query first, then search all sessions.");
            return;
        }

        TranscriptSearch.ShowCrossSessionMessage($"Searching every session for \"{query}\"...");
        try
        {
            var hits = await _crossSessionSearchService.SearchAsync(query);
            TranscriptSearch.ShowCrossSessionResults(query, hits, sessionId => _ = SelectSessionFromSearchAsync(sessionId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TranscriptSearch.ShowCrossSessionMessage(
                AppErrorPresenter.Present(exception, AppErrorContext.SavedState).DisplayText);
        }
    }

    private async Task SelectSessionFromSearchAsync(string sessionId)
    {
        TranscriptSearch.ClosePopup();
        await _savedStateControlService.SelectSessionAsync(sessionId, CancellationToken.None);
    }

    private void TranscriptSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderReachability.ClosePopup();
        ViewMenuPopup.IsOpen = false;
        DebugMenuPopup.IsOpen = false;
        TranscriptFiltersPopup.IsOpen = false;
        _transcriptSearchCoordinator?.ToggleSearch();
    }

    private void TopProviderValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowProviderHealthPopup(sender as UIElement);
        e.Handled = true;
    }

    private void TopProviderValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsKeyboardActivationKey(e.Key))
        {
            return;
        }

        ShowProviderHealthPopup(sender as UIElement);
        e.Handled = true;
    }

    private void ShowProviderHealthPopup(UIElement? opener = null)
    {
        var target = opener ?? ActiveProviderStatusButton();
        ProviderHealthPopup.PlacementTarget = target;
        _providerHealthFocusReturnTarget = target;
        _transcriptSearchCoordinator?.CloseSearch();
        ViewMenuPopup.IsOpen = false;
        DebugMenuPopup.IsOpen = false;
        TranscriptFiltersPopup.IsOpen = false;
        ProviderReachability.ShowPopup();
    }

    private UIElement ActiveProviderStatusButton()
    {
        if (AgentTopBarMetrics.Visibility == Visibility.Visible)
        {
            return AgentTopProviderStatusButton;
        }

        if (CollaborateTopBarMetrics.Visibility == Visibility.Visible)
        {
            return CollaborateTopProviderStatusButton;
        }

        return TopProviderStatusButton;
    }

    private void ProviderHealthPopup_Opened(object? sender, EventArgs e)
    {
        _providerHealthFocusReturnTarget ??= Keyboard.FocusedElement ?? ActiveProviderStatusButton();
        FocusOverlayEntry(ProviderHealthPopup, ProviderHealthCloseButton);
    }

    private void ProviderHealthPopup_Closed(object? sender, EventArgs e)
    {
        var returnTarget = _providerHealthFocusReturnTarget;
        _providerHealthFocusReturnTarget = null;
        RestoreOverlayFocus(returnTarget, ActiveProviderStatusButton(), () => !ProviderHealthPopup.IsOpen);
    }

    private void ProviderHealthPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ClosePopupOnEscape(ProviderHealthPopup, e);
    }

    private static bool IsKeyboardActivationKey(Key key)
    {
        return key is Key.Enter or Key.Return or Key.Space;
    }

    private void ProviderHealthCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderReachability.ClosePopup();
    }

    private async void ProviderHealthTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "provider health test",
            cancellationToken => ProviderReachability.TestProviderAsync(cancellationToken));
    }

    private async void ProviderHealthRefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTrackedBackgroundOperationSafelyAsync(
            "provider model refresh",
            cancellationToken => ProviderReachability.RefreshModelsAsync(cancellationToken));
    }

    private void ProviderHealthSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderReachability.OpenSettings();
    }

    private void TranscriptSearchText_KeyDown(object sender, KeyEventArgs e)
    {
        _transcriptSearchCoordinator?.OnSearchKeyDown(e);
    }

    private void TranscriptSearchPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _transcriptSearchCoordinator?.OnSearchKeyDown(e);
        }
    }

    private void TranscriptSearchText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _transcriptSearchCoordinator?.OnSearchPreviewMouseDown(e);
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

    private void TranscriptSearchDragHandle_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _transcriptSearchCoordinator?.OnDragLostMouseCapture();
    }

    private void AppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettingsPanel.Visibility != Visibility.Visible)
        {
            _settingsFocusReturnTarget = AppSettingsButton;
        }

        _appSettingsCoordinator?.Toggle();
    }

    private void AppSettingsPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = AppSettingsPanel.IsVisible;
        AppSettingsScrim.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            EnsureSessionSettingsDirtyTracking();
            _settingsFocusReturnTarget ??= Keyboard.FocusedElement ?? AppSettingsButton;
            ViewMenuPopup.IsOpen = false;
            DebugMenuPopup.IsOpen = false;
            TranscriptFiltersPopup.IsOpen = false;
            ProviderReachability.ClosePopup();
            _transcriptSearchCoordinator?.CloseSearch();
            Dispatcher.BeginInvoke(() =>
            {
                if (AppSettingsPanel.Visibility == Visibility.Visible)
                {
                    SettingsSearchText.Focus();
                }
            }, DispatcherPriority.Input);
            return;
        }

        var returnTarget = _settingsFocusReturnTarget;
        _settingsFocusReturnTarget = null;
        RestoreOverlayFocus(
            returnTarget,
            AppSettingsButton,
            () => AppSettingsPanel.Visibility != Visibility.Visible);
    }

    private void AppSettingsScrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseAppSettings();
        e.Handled = true;
    }

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellProcessLauncher.TryStart(new ProcessStartInfo
        {
            FileName = ReleasesUrl,
            UseShellExecute = true
        }, out var error))
        {
            var status = $"Could not open releases: {error}";
            LoadStatus.Text = status;
            SetApplicationStatus("app.open-releases", "App", status, "settings");
            return;
        }

        ShellTopBar.Presentation.StatusCenter.Resolve("app.open-releases");
    }

    private void OpenUserGuideButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptFiltersPopup.IsOpen = false;
        OpenUserGuide(articleId: null, sender as IInputElement);
    }

    private void OpenUserGuide(string? articleId, IInputElement? launcher = null)
    {
        _transcriptSearchCoordinator?.CloseSearch();
        ProviderReachability.ClosePopup();
        TranscriptFiltersPopup.IsOpen = false;
        if (!_userGuideWindowHost.Show(this, articleId, launcher ?? Keyboard.FocusedElement))
        {
            const string status = "User guide not found.";
            LoadStatus.Text = status;
            ShellTopBar.Presentation.StatusCenter.PublishNotice(
                "app.user-guide",
                "App",
                ApplicationStatusState.Failed,
                status,
                navigationTarget: "settings",
                identity: new ApplicationStatusIdentity(_activeSession?.Id ?? ""),
                lifetime: ApplicationStatusLifetime.UntilResolved);
            return;
        }

        ShellTopBar.Presentation.StatusCenter.Resolve("app.user-guide");
    }

    private string CurrentContextualHelpArticleId()
    {
        var settingsOpen = AppSettingsPanel.Visibility == Visibility.Visible;
        var settingsContext = SettingsHelpContext(
            InternetAccessSettingsExpander.IsKeyboardFocusWithin,
            DebugControlsSettingsExpander.IsKeyboardFocusWithin,
            AgentSettingsExpander.IsKeyboardFocusWithin,
            InternetAccessSettingsExpander.IsExpanded,
            DebugControlsSettingsExpander.IsExpanded,
            AgentSettingsExpander.IsExpanded);
        return HelpArticleForContext(
            _activeShellSurface,
            settingsOpen,
            internetSettings: settingsOpen && settingsContext.Internet,
            debugSettings: settingsOpen && settingsContext.Debug,
            agentSettings: settingsOpen && settingsContext.Agent,
            statusCenterOpen: UniversalStatusCenter.IsDashboardOpen,
            providerHealthOpen: ProviderHealthPopup.IsOpen);
    }

    internal static (bool Internet, bool Debug, bool Agent) SettingsHelpContext(
        bool internetFocused,
        bool debugFocused,
        bool agentFocused,
        bool internetExpanded,
        bool debugExpanded,
        bool agentExpanded)
    {
        if (internetFocused || debugFocused || agentFocused)
        {
            return (internetFocused, debugFocused, agentFocused);
        }

        var expandedCount = (internetExpanded ? 1 : 0) + (debugExpanded ? 1 : 0) + (agentExpanded ? 1 : 0);
        return expandedCount == 1
            ? (internetExpanded, debugExpanded, agentExpanded)
            : (false, false, false);
    }

    internal static string HelpArticleForContext(
        ShellSurface surface,
        bool settingsOpen = false,
        bool internetSettings = false,
        bool debugSettings = false,
        bool agentSettings = false,
        bool statusCenterOpen = false,
        bool providerHealthOpen = false)
    {
        if (statusCenterOpen)
        {
            return "status-center";
        }

        if (settingsOpen)
        {
            if (internetSettings)
            {
                return "internet-privacy";
            }

            if (debugSettings)
            {
                return "ai-world-debug";
            }

            if (agentSettings)
            {
                return "agent";
            }

            return "provider-troubleshooting";
        }

        if (providerHealthOpen)
        {
            return "provider-troubleshooting";
        }

        return surface switch
        {
            ShellSurface.Models => "models-routing",
            ShellSurface.MatchSetup => "match-setup",
            ShellSurface.Agent => "agent",
            ShellSurface.Collaborate => "collaborate",
            ShellSurface.ExperimentLab => "experiment-lab",
            ShellSurface.World => "ai-world-debug",
            _ => "arena-mode"
        };
    }

    private void UserGuideWindowHost_AppRouteRequested(object? sender, AIArena.Wpf.Help.Presentation.HelpAppRouteRequestedEventArgs e)
    {
        switch (e.Route)
        {
            case "app/view/arena":
                ShowTranscriptPanel(clearFilters: false);
                break;
            case "app/models":
                ShowProviderModelsPanel();
                break;
            case "app/settings/provider":
                OpenModelProviderSettings();
                break;
            case "app/match-setup":
                ShowCustomMatchPanel();
                break;
            case "app/status-center":
                if (IsRightRailEffectivelyCollapsed(
                        _wpfSettings.RightRailCollapsed,
                        _rightRailAutoCollapseActive,
                        _rightRailNarrowRevealRequested,
                        _rightRailWidthCollapseLatched))
                {
                    _rightRailNarrowRevealRequested = true;
                    ApplyRightRailCollapsed();
                }

                var useCollapsedLauncher = ShouldUseCollapsedStatusCenterLauncher(
                    _wpfSettings.RightRailCollapsed,
                    _rightRailAutoCollapseActive,
                    _rightRailWidthCollapseLatched);
                UniversalStatusCenter.OpenDashboard(
                    useCollapsedLauncher ? CollapsedStatusCenterButton : null);
                break;
            case "app/view/agent":
                ShowAgentPanel();
                break;
            case "app/view/collaborate":
                ShowCollaboratePanel();
                break;
            case "app/view/experiment":
                ShowExperimentLabPanel();
                break;
            case "app/settings/debug":
                AppSettingsWorkflow.SetVisible(true);
                DebugControlsSettingsExpander.IsExpanded = true;
                DebugControlsSettingsExpander.BringIntoView();
                break;
            case "app/settings/internet":
                AppSettingsWorkflow.SetVisible(true);
                InternetAccessSettingsExpander.IsExpanded = true;
                InternetAccessSettingsExpander.BringIntoView();
                break;
        }
    }

    private void OpenModelProviderSettings(string? baseUrl = null, string? model = null)
    {
        if (!string.IsNullOrWhiteSpace(SettingsSearchText.Text))
        {
            SettingsSearchText.Clear();
        }

        if (_appSettingsCoordinator is not null)
        {
            AppSettingsWorkflow.OpenModelProviderSettings(baseUrl, model);
            return;
        }

        ShellNavigation.SetAppSettingsVisible(true);
    }

    private void CloseAppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseAppSettings();
    }

    private void CloseAppSettings()
    {
        _appSettingsCoordinator?.SetVisible(false);
    }

    private void EnsureSessionSettingsDirtyTracking()
    {
        if (!_sessionSettingsTrackingAttached)
        {
            foreach (var textBox in ManualSessionSettingsTextBoxes())
            {
                textBox.TextChanged += SessionSettingsInput_Changed;
            }

            _sessionSettingsTrackingAttached = true;
            _trackedSessionSettingsSessionId = _activeSession?.Id ?? "";
            ResetSessionSettingsBaseline();
            return;
        }

        RefreshSessionSettingsPendingState();
    }

    private TextBox[] ManualSessionSettingsTextBoxes()
    {
        return
        [
            ProviderTimeoutText,
            ProviderTemperatureText,
            ProviderMaxOutputText,
            ContextTranscriptWindowText,
            ContextPrivateWindowText,
            ContextNotesWindowText
        ];
    }

    private Dictionary<string, string> CaptureManualSessionSettings()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ProviderTimeoutText)] = ProviderTimeoutText.Text,
            [nameof(ProviderTemperatureText)] = ProviderTemperatureText.Text,
            [nameof(ProviderMaxOutputText)] = ProviderMaxOutputText.Text,
            [nameof(ContextTranscriptWindowText)] = ContextTranscriptWindowText.Text,
            [nameof(ContextPrivateWindowText)] = ContextPrivateWindowText.Text,
            [nameof(ContextNotesWindowText)] = ContextNotesWindowText.Text
        };
    }

    private void SessionSettingsInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isRenderingSnapshot || _restoringSessionSettingsDraft)
        {
            return;
        }

        PreserveCurrentSessionSettingsDraft();
        RefreshSessionSettingsPendingState();
    }

    private void PreserveCurrentSessionSettingsDraft()
    {
        if (!_sessionSettingsTrackingAttached)
        {
            return;
        }

        var current = CaptureManualSessionSettings();
        var delta = SessionSettingsDelta(_sessionSettingsBaseline, current);
        if (delta.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_trackedSessionSettingsSessionId))
            {
                _sessionSettingsDrafts.Remove(_trackedSessionSettingsSessionId);
            }
            else
            {
                _unboundSessionSettingsDraft = null;
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(_trackedSessionSettingsSessionId))
        {
            _sessionSettingsDrafts[_trackedSessionSettingsSessionId] = delta;
            _unboundSessionSettingsDraft = null;
        }
        else
        {
            _unboundSessionSettingsDraft = delta;
        }
    }

    private void ReconcileSessionSettingsAfterSnapshot(string sessionId)
    {
        if (!_sessionSettingsTrackingAttached)
        {
            return;
        }

        _trackedSessionSettingsSessionId = sessionId;
        ReplaceSessionSettingsBaseline(CaptureManualSessionSettings());
        Dictionary<string, string>? draft = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!_sessionSettingsDrafts.TryGetValue(sessionId, out draft)
                && _unboundSessionSettingsDraft is not null)
            {
                draft = _unboundSessionSettingsDraft;
                _unboundSessionSettingsDraft = null;
            }
        }

        if (draft is not null)
        {
            _restoringSessionSettingsDraft = true;
            try
            {
                RestoreManualSessionSettings(draft);
            }
            finally
            {
                _restoringSessionSettingsDraft = false;
            }

            PreserveCurrentSessionSettingsDraft();
        }

        RefreshSessionSettingsPendingState();
    }

    private void RestoreManualSessionSettings(IReadOnlyDictionary<string, string> values)
    {
        Restore(nameof(ProviderTimeoutText), ProviderTimeoutText);
        Restore(nameof(ProviderTemperatureText), ProviderTemperatureText);
        Restore(nameof(ProviderMaxOutputText), ProviderMaxOutputText);
        Restore(nameof(ContextTranscriptWindowText), ContextTranscriptWindowText);
        Restore(nameof(ContextPrivateWindowText), ContextPrivateWindowText);
        Restore(nameof(ContextNotesWindowText), ContextNotesWindowText);
        return;

        void Restore(string key, TextBox textBox)
        {
            if (values.TryGetValue(key, out var value))
            {
                textBox.Text = value;
            }
        }
    }

    private void ResetSessionSettingsBaseline()
    {
        ReplaceSessionSettingsBaseline(CaptureManualSessionSettings());
        if (!string.IsNullOrWhiteSpace(_trackedSessionSettingsSessionId))
        {
            _sessionSettingsDrafts.Remove(_trackedSessionSettingsSessionId);
        }

        _unboundSessionSettingsDraft = null;

        RefreshSessionSettingsPendingState();
    }

    private void ReplaceSessionSettingsBaseline(IReadOnlyDictionary<string, string> values)
    {
        _sessionSettingsBaseline.Clear();
        foreach (var pair in values)
        {
            _sessionSettingsBaseline[pair.Key] = pair.Value;
        }
    }

    private bool HasPendingSessionSettings()
    {
        return _sessionSettingsDrafts.Count > 0
            || _unboundSessionSettingsDraft is not null
            || (_sessionSettingsTrackingAttached
                && CountChangedSessionSettings(_sessionSettingsBaseline, CaptureManualSessionSettings()) > 0);
    }

    private void RefreshSessionSettingsPendingState()
    {
        if (!_sessionSettingsTrackingAttached)
        {
            ApplySettingsButton.IsEnabled = false;
            ApplySettingsLabel.Text = "No pending session changes";
            SettingsPendingChangesText.Text = "No session-scoped changes pending.";
            return;
        }

        var changed = CountChangedSessionSettings(_sessionSettingsBaseline, CaptureManualSessionSettings());
        ApplySettingsButton.IsEnabled = changed > 0 && !_arenaBusy && _activeSession is not null;
        ApplySettingsLabel.Text = changed == 0
            ? "No pending session changes"
            : $"Apply {changed} session {(changed == 1 ? "change" : "changes")}";
        SettingsPendingChangesText.Text = changed switch
        {
            0 => "No session-scoped changes pending.",
            _ when _activeSession is null => $"{changed} session-scoped {(changed == 1 ? "change" : "changes")} pending; waiting for a session to load.",
            _ => $"{changed} session-scoped {(changed == 1 ? "change" : "changes")} pending."
        };
    }

    internal static int CountChangedSessionSettings(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current)
    {
        return SessionSettingsDelta(baseline, current).Count;
    }

    internal static Dictionary<string, string> SessionSettingsDelta(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current)
    {
        return current
            .Where(pair => !baseline.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private async void VisualSettings_Changed(object sender, RoutedEventArgs e)
    {
        _transcriptViewCoordinator?.OnVisualSettingsChanged();
        ApplyWorldDebugVisibility(persistIfForcedOff: true);
        ApplyExperimentLabVisibility();
        ApplyAgentWorkspaceVisibility();
        await RefreshControlPlaneHostAsync();
    }

    internal static bool IsWorldDebugEnabled(WpfSettings settings)
    {
        return settings.AllowDebugControls && settings.ShowWorldDebug;
    }

    private void ApplyWorldDebugVisibility(bool persistIfForcedOff)
    {
        var settingsChanged = false;
        if (!_wpfSettings.AllowDebugControls && _wpfSettings.ShowWorldDebug)
        {
            _wpfSettings.ShowWorldDebug = false;
            settingsChanged = true;
        }

        var enabled = IsWorldDebugEnabled(_wpfSettings);
        if (!enabled && !_wpfSettings.LabViewMode.Equals("transcript", StringComparison.OrdinalIgnoreCase))
        {
            _wpfSettings.LabViewMode = "transcript";
            settingsChanged = true;
        }

        if (settingsChanged && persistIfForcedOff)
        {
            _wpfSettingsStore.Save(_wpfSettings);
        }

        WorldDebugCheckBox.IsEnabled = _wpfSettings.AllowDebugControls;
        if (WorldDebugCheckBox.IsChecked != enabled)
        {
            _isUpdatingWorldDebug = true;
            try
            {
                WorldDebugCheckBox.IsChecked = enabled;
            }
            finally
            {
                _isUpdatingWorldDebug = false;
            }
        }

        if (!enabled && AgentWorldPanel.Visibility == Visibility.Visible)
        {
            ShowTranscriptPanel(clearFilters: false);
            return;
        }

        UpdateLabViewToggleVisibility();
    }

    private void UpdateLabViewToggleVisibility()
    {
        var labSurfaceVisible = TranscriptPanel.Visibility == Visibility.Visible
            || AgentWorldPanel.Visibility == Visibility.Visible;
        LabViewToggleGroup.Visibility = IsWorldDebugEnabled(_wpfSettings) && labSurfaceVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal static bool IsAgentWorkspaceEnabled(WpfSettings settings)
    {
        return settings.ShowAgentWorkspace;
    }

    internal static bool IsExperimentLabEnabled(WpfSettings settings)
    {
        return settings.AllowDebugControls;
    }

    private void ApplyExperimentLabVisibility()
    {
        var enabled = IsExperimentLabEnabled(_wpfSettings);
        ExperimentLabNavButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled && ExperimentLabPanel.Visibility == Visibility.Visible)
        {
            ShowTranscriptPanel(clearFilters: false);
            return;
        }

        ShellNavigation.UpdateNavigationTheme();
    }

    private void ApplyAgentWorkspaceVisibility()
    {
        var enabled = IsAgentWorkspaceEnabled(_wpfSettings);
        AgentNavButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AgentWorkspaceCheckBox.IsEnabled = true;
        if (AgentWorkspaceCheckBox.IsChecked != enabled)
        {
            _isUpdatingAgentWorkspace = true;
            try
            {
                AgentWorkspaceCheckBox.IsChecked = enabled;
            }
            finally
            {
                _isUpdatingAgentWorkspace = false;
            }
        }

        if (!enabled && AgentWorkspacePanel.Visibility == Visibility.Visible)
        {
            ShowTranscriptPanel(clearFilters: false);
        }

        ShellNavigation.UpdateNavigationTheme();
        ApplyControlPlaneToggleState();
    }

    private void ApplyControlPlaneToggleState()
    {
        var initializationAvailable = string.IsNullOrWhiteSpace(_controlPlaneInitializationError);
        ControlPlaneCheckBox.IsEnabled = initializationAvailable;
        var help = initializationAvailable
            ? "Allow authenticated local PowerShell automation for this AI Arena - Lite process."
            : "PowerShell control plane is unavailable because its QA ownership identifier is invalid. Restart without that environment override.";
        ControlPlaneCheckBox.ToolTip = help;
        AutomationProperties.SetHelpText(ControlPlaneCheckBox, help);
        var enabled = IsControlPlaneEnabled;
        if (ControlPlaneCheckBox.IsChecked != enabled)
        {
            _isUpdatingControlPlane = true;
            try
            {
                ControlPlaneCheckBox.IsChecked = enabled;
            }
            finally
            {
                _isUpdatingControlPlane = false;
            }
        }
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
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderBaseUrlCommittedAsync(cancellationToken));
    }

    private async void ProviderBaseUrlText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderBaseUrlCommittedAsync(cancellationToken));
    }

    private async void ProviderApiTokenBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.PersistModelRoutingAsync(
                "Provider API token saved.",
                refreshModels: true,
                cancellationToken));
    }

    private async void ProviderApiTokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.PersistModelRoutingAsync(
                "Provider API token saved.",
                refreshModels: true,
                cancellationToken));
    }

    private async void ProviderModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderModelSelectionChangedAsync(cancellationToken));
    }

    private async void ProviderApiModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(async (coordinator, cancellationToken) =>
        {
            coordinator.UpdateNativeLifecycleControls();
            await coordinator.PersistModelRoutingAsync(
                "Provider API mode saved.",
                refreshModels: true,
                cancellationToken);
        });
    }

    private async void ModelProviderSettingsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (AppSettingsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        await RunTrackedBackgroundOperationSafelyAsync(
            "provider model refresh",
            cancellationToken => RefreshAdvertisedModelsAsync(force: true, cancellationToken));
    }

    private async void ProviderModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderModelCommittedAsync(cancellationToken));
    }

    private async void ProviderModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _llamaCppRuntimeCoordinator?.ConfigurationChanged();
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderModelCommittedAsync(cancellationToken));
    }

    private async void ProviderNativeOptions_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderNativeOptionsCommittedAsync(cancellationToken));
    }

    private async void ProviderNativeOptionsText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderNativeOptionsCommittedAsync(cancellationToken));
    }

    private async void ProviderNativeOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderNativeOptionsCommittedAsync(cancellationToken));
    }

    private async void ProviderNativeOptions_CheckedChanged(object sender, RoutedEventArgs e)
    {
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ProviderNativeOptionsCommittedAsync(cancellationToken));
    }

    private async void ParticipantModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            await RunProviderCommitSafelyAsync(
                (coordinator, cancellationToken) => coordinator.ParticipantModelSelectionChangedAsync(comboBox, cancellationToken));
        }
    }

    private async void ParticipantModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            await RunProviderCommitSafelyAsync(
                (coordinator, cancellationToken) => coordinator.ParticipantModelCommittedAsync(comboBox, cancellationToken));
        }
    }

    private async void ParticipantModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ComboBox comboBox)
        {
            return;
        }

        e.Handled = true;
        await RunProviderCommitSafelyAsync(
            (coordinator, cancellationToken) => coordinator.ParticipantModelCommittedAsync(comboBox, cancellationToken));
    }

    private Task RunProviderCommitSafelyAsync(
        Func<ProviderSettingsCoordinator, CancellationToken, Task> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var coordinator = _providerSettingsCoordinator;
        var operationCoordinator = _arenaOperationCoordinator;
        if (coordinator is null || operationCoordinator is null || _shutdownInProgress)
        {
            return Task.CompletedTask;
        }

        return RunUiCommitSafelyAsync(
            () => operationCoordinator.TrackAsync(cancellationToken => commit(coordinator, cancellationToken)),
            exception =>
            {
                var status = exception is OperationCanceledException
                    ? "Provider settings save cancelled."
                    : ArenaOperationCoordinator.OperationFailureStatus(exception);
                SetLoadStatus(status);
                Debug.WriteLine($"Provider settings commit failed with {exception.GetType().Name}.");
            });
    }

    private async Task<AIArenaControlResponse> RunProviderControlOperationAsync(
        AIArenaControlRequest request,
        CancellationToken callerCancellationToken)
    {
        return await RunTrackedControlOperationAsync(
            operationCancellationToken => _providerControlHandler.ExecuteAsync(request, operationCancellationToken),
            callerCancellationToken);
    }

    private async Task<AIArenaControlResponse> RunTrackedControlOperationAsync(
        Func<CancellationToken, Task<AIArenaControlResponse>> operation,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var operationCoordinator = _arenaOperationCoordinator;
        if (operationCoordinator is null || _shutdownInProgress)
        {
            throw new OperationCanceledException("Application shutdown has already started.");
        }

        AIArenaControlResponse? response = null;
        await operationCoordinator.TrackAsync(async shutdownCancellationToken =>
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                shutdownCancellationToken);
            response = await operation(linkedCancellation.Token);
        });
        return response ?? throw new InvalidOperationException("Tracked control operation completed without a response.");
    }

    private Task RunTrackedBackgroundOperationSafelyAsync(
        string operationName,
        Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        var operationCoordinator = _arenaOperationCoordinator;
        if (operationCoordinator is null || _shutdownInProgress)
        {
            return Task.CompletedTask;
        }

        return RunUiCommitSafelyAsync(
            () => operationCoordinator.TrackAsync(operation),
            exception =>
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                var presentation = AppErrorPresenter.Present(exception, AppErrorContext.Arena);
                SetLoadStatus(presentation.DisplayText);
                SetApplicationStatus(
                    $"app.background.{operationName}",
                    "App",
                    presentation.DisplayText,
                    "arena");
                Debug.WriteLine($"Tracked {operationName} failed with {exception.GetType().Name}.");
            });
    }

    internal static async Task RunUiCommitSafelyAsync(Func<Task> commit, Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(reportFailure);
        try
        {
            await commit();
        }
        catch (Exception ex)
        {
            try
            {
                reportFailure(ex);
            }
            catch (Exception reportingFailure)
            {
                Debug.WriteLine($"UI commit failure reporting failed with {reportingFailure.GetType().Name}.");
            }
        }
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

    private Task RefreshAdvertisedModelsAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        return _providerSettingsCoordinator?.RefreshAdvertisedModelsAsync(force, cancellationToken) ?? Task.CompletedTask;
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

}

public sealed class ShellPopupSurface : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ShellPopupSurfaceAutomationPeer(this);

    private sealed class ShellPopupSurfaceAutomationPeer(ShellPopupSurface owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ShellPopupSurface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}

public sealed class ShellPopupOpenerCard : Border
{
    public event EventHandler? Invoked;

    protected override AutomationPeer OnCreateAutomationPeer() => new ShellPopupOpenerCardAutomationPeer(this);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (e.Handled || !IsEnabled)
        {
            return;
        }

        Focus();
        RaiseInvoked();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsEnabled || e.Key is not (Key.Enter or Key.Return or Key.Space))
        {
            return;
        }

        RaiseInvoked();
        e.Handled = true;
    }

    private void RaiseInvoked()
    {
        if (IsEnabled)
        {
            Invoked?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ShellPopupOpenerCardAutomationPeer(ShellPopupOpenerCard owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(ShellPopupOpenerCard);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke()
        {
            if (!owner.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }

            owner.Dispatcher.BeginInvoke(owner.RaiseInvoked, DispatcherPriority.Input);
        }
    }
}
