using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Services;

namespace AIArena.Wpf.Controls;

public enum ProviderModelAvailability
{
    Loaded,
    Available,
    Unavailable
}

public enum ProviderTargetAssignmentState
{
    Unassigned,
    Default,
    Explicit,
    InheritsDefault
}

public enum ProviderConnectionState
{
    Unknown,
    Online,
    Partial,
    Offline,
    Checking,
    Failed
}

public enum ProviderAssignmentSaveState
{
    Ready,
    Saving,
    Saved,
    Failed,
    Unavailable
}

public enum ProviderModelLifecycleActionState
{
    Ready,
    Running,
    Unconfirmed,
    Succeeded,
    Failed
}

public enum ProviderModelHistoryPolicy
{
    Strict,
    Rolling80Percent,
    Chaptered
}

public enum ProviderModelResponseTone
{
    Default,
    Neutral,
    Concise,
    Analytical,
    Creative,
    Direct,
    Custom
}

public enum ProviderModelConfigurationSaveState
{
    Ready,
    Saving,
    Saved,
    Failed,
    Unavailable
}

public enum ProviderModelConfigurationReloadState
{
    NotRequired,
    Required,
    Running,
    Unconfirmed,
    Succeeded,
    Failed,
    Unavailable
}

public sealed record ProviderAssignmentTargetPresentation(
    string Id,
    string DisplayName,
    string HelpText = "",
    bool IsEnabled = true,
    ProviderTargetAssignmentState AssignmentState = ProviderTargetAssignmentState.Unassigned,
    ProviderTargetAssignmentState AssignedState = ProviderTargetAssignmentState.Explicit,
    ProviderTargetAssignmentState ClearedState = ProviderTargetAssignmentState.Unassigned,
    bool IsDefault = false);

public sealed record ProviderModelAssignmentPresentation(
    string Id,
    string DisplayName,
    ProviderModelAvailability Availability,
    string Status,
    string Metadata,
    IReadOnlyCollection<string> AssignedTargetIds,
    string AutomationHelp = "",
    bool CanLoad = false,
    bool CanUnload = false,
    string LifecycleHelp = "",
    bool IsResidencyStale = false,
    ProviderModelConfigurationPresentation? Configuration = null);

public sealed record ProviderModelConfigurationPresentation(
    int ContextWindow = 0,
    int EffectiveContextWindow = 0,
    string ContextEvidence = "Effective context is not reported.",
    ProviderModelHistoryPolicy HistoryPolicy = ProviderModelHistoryPolicy.Strict,
    ProviderModelResponseTone ResponseTone = ProviderModelResponseTone.Default,
    string CustomTone = "",
    bool CanEdit = true,
    bool ChapteredAvailable = false,
    bool RequiresReload = false,
    string ConfigurationIdentity = "",
    string Status = "Ready to configure.",
    int MinimumContextWindow = 512,
    int MaximumContextWindow = 1_048_576);

public sealed record ProviderModelAssignmentsPresentation(
    string ProviderName,
    string ConnectionStatus,
    ProviderConnectionState ConnectionState,
    string CatalogStatus,
    IReadOnlyList<ProviderModelAssignmentPresentation> Models,
    IReadOnlyList<ProviderAssignmentTargetPresentation> Targets,
    string SelectedModelId = "",
    bool IsRefreshing = false,
    bool CanRefresh = true,
    bool CanOpenConnectionSettings = true,
    bool CanClose = true,
    bool CanAssign = true,
    string PresentationIdentity = "",
    bool CanRunLifecycle = false,
    string ConnectionIdentity = "");

public sealed class ProviderModelSearchChangedEventArgs(string query) : EventArgs
{
    public string Query { get; } = query;
}

public sealed class ProviderModelAssignmentChangedEventArgs(
    Guid changeId,
    string modelId,
    string targetId,
    bool isAssigned) : EventArgs
{
    public Guid ChangeId { get; } = changeId;
    public string ModelId { get; } = modelId;
    public string TargetId { get; } = targetId;
    public bool IsAssigned { get; } = isAssigned;
}

public sealed class ProviderModelLifecycleRequestedEventArgs(
    Guid operationId,
    string modelId,
    bool load) : EventArgs
{
    public Guid OperationId { get; } = operationId;
    public string ModelId { get; } = modelId;
    public bool Load { get; } = load;
}

public sealed class ProviderModelConfigurationChangedEventArgs(
    Guid changeId,
    string modelId,
    int contextWindow,
    ProviderModelHistoryPolicy historyPolicy,
    ProviderModelResponseTone responseTone,
    string customTone,
    string configurationIdentity) : EventArgs
{
    public Guid ChangeId { get; } = changeId;
    public string ModelId { get; } = modelId;
    public int ContextWindow { get; } = contextWindow;
    public ProviderModelHistoryPolicy HistoryPolicy { get; } = historyPolicy;
    public ProviderModelResponseTone ResponseTone { get; } = responseTone;
    public string CustomTone { get; } = customTone;
    public string ConfigurationIdentity { get; } = configurationIdentity;
}

public sealed class ProviderModelConfigurationReloadRequestedEventArgs(
    Guid operationId,
    string modelId,
    string configurationIdentity) : EventArgs
{
    public Guid OperationId { get; } = operationId;
    public string ModelId { get; } = modelId;
    public string ConfigurationIdentity { get; } = configurationIdentity;
}

public partial class ProviderModelAssignmentsControl : UserControl
{
    internal const double CompactContentThreshold = 1100;
    internal const double WideHostWindowThreshold = 1200;
    private const double CompactHeaderThreshold = 760;
    private const int MaximumStatusLength = 240;
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(180);

    private readonly ObservableCollection<ModelRowState> modelRows = [];
    private readonly ListCollectionView loadedRowsView;
    private readonly ListCollectionView modelRowsView;
    private readonly DispatcherTimer searchDebounceTimer;
    private readonly bool usesIncrementalLiveShaping;
    // These detached probes retain causal state for rollback logic and focused
    // tests without adding another status surface or live region to the pane.
    private readonly TextBlock AssignmentSaveStatusText = new();
    private readonly Border AssignmentSaveStatusCard = new();
    private readonly TextBlock LifecycleStatusText = new();
    private readonly Border LifecycleStatusCard = new();
    private readonly TextBlock ConfigurationStatusText = new();
    private readonly Border ConfigurationStatusCard = new();
    private IReadOnlyList<ProviderAssignmentTargetPresentation> targets = [];
    private IReadOnlyDictionary<string, ProviderAssignmentTargetPresentation> targetsById =
        new Dictionary<string, ProviderAssignmentTargetPresentation>(StringComparer.Ordinal);
    private readonly ObservableCollection<TargetAssignmentState> selectedTargetRows = [];
    private ProviderModelAssignmentsPresentation? currentPresentation;
    private PendingAssignment? pendingAssignment;
    private PendingLifecycle? pendingLifecycle;
    private PendingConfiguration? pendingConfiguration;
    private PendingConfigurationReload? pendingConfigurationReload;
    private bool pendingLifecycleMutationStarted;
    private bool pendingConfigurationReloadMutationStarted;
    private string lastResolvedModelId = "";
    private ProviderAssignmentSaveState lastSaveState = ProviderAssignmentSaveState.Ready;
    private string lastSaveMessage = "Ready to assign.";
    private string lastLifecycleModelId = "";
    private ProviderModelLifecycleActionState lastLifecycleState = ProviderModelLifecycleActionState.Ready;
    private string lastLifecycleMessage = "Ready to manage LM Studio residency.";
    private bool? lastLifecycleDesiredLoaded;
    private string lastLifecycleConnectionIdentity = "";
    private ProviderModelAssignmentPresentation? unconfirmedLifecycleReceipt;
    private bool lifecycleReceiptOrphanedByConnectionChange;
    private bool applyingPresentation;
    private bool synchronizingSelection;
    private ModelRowState? selectedModel;
    private bool usesCompactLayout;
    private Window? layoutHostWindow;
    private string committedSearchQuery = "";
    private string lastPublishedSearchQuery = "";
    private ProviderModelFacet selectedFacet = ProviderModelFacet.All;
    private int catalogContinuityGeneration;
    private bool applyingConfigurationControls;
    private string lastConfigurationModelId = "";
    private ProviderModelConfigurationSaveState lastConfigurationState = ProviderModelConfigurationSaveState.Ready;
    private string lastConfigurationMessage = "Ready to configure.";
    private string lastReloadModelId = "";
    private ProviderModelConfigurationReloadState lastReloadState = ProviderModelConfigurationReloadState.NotRequired;
    private string lastReloadMessage = "Configuration is active.";
    private string lastReloadConnectionIdentity = "";

    public ProviderModelAssignmentsControl()
    {
        InitializeComponent();
        InitializeDetachedStatusProbes();
        AssignmentTargetsItems.ItemsSource = selectedTargetRows;
        HistoryPolicyCombo.ItemsSource = HistoryPolicyOption.Options;
        HistoryPolicyCombo.ItemContainerGenerator.StatusChanged += HistoryPolicyItemContainers_StatusChanged;
        ResponseToneCombo.ItemsSource = ResponseToneOption.Options;
        searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = SearchDebounceInterval
        };
        searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        loadedRowsView = new ListCollectionView(modelRows) { Filter = IsLoadedModel };
        using (loadedRowsView.DeferRefresh())
        {
            loadedRowsView.SortDescriptions.Add(
                new SortDescription(nameof(ModelRowState.DisplayName), ListSortDirection.Ascending));
            loadedRowsView.SortDescriptions.Add(
                new SortDescription(nameof(ModelRowState.Id), ListSortDirection.Ascending));
        }
        modelRowsView = new ListCollectionView(modelRows) { Filter = MatchesCurrentSearch };
        using (modelRowsView.DeferRefresh())
        {
            modelRowsView.SortDescriptions.Add(
                new SortDescription(nameof(ModelRowState.GroupOrder), ListSortDirection.Ascending));
            modelRowsView.SortDescriptions.Add(
                new SortDescription(nameof(ModelRowState.DisplayName), ListSortDirection.Ascending));
            modelRowsView.SortDescriptions.Add(
                new SortDescription(nameof(ModelRowState.Id), ListSortDirection.Ascending));
            modelRowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelRowState.GroupName)));
        }
        usesIncrementalLiveShaping = ConfigureIncrementalLiveShaping(loadedRowsView)
            && ConfigureIncrementalLiveShaping(modelRowsView);
        LoadedModelsList.ItemsSource = loadedRowsView;
        ModelsList.ItemsSource = modelRowsView;
        ModelFacetCombo.ItemsSource = ModelFacetOption.Options;
        ModelFacetCombo.SelectedIndex = 0;
        IsVisibleChanged += ProviderModelAssignmentsControl_IsVisibleChanged;
        ApplyResponsiveLayout(compact: false);
        UpdateSearchChrome();
        UpdateCatalogSummary();
        UpdateSearchResults();
        UpdateEmptyState();
    }

    private void InitializeDetachedStatusProbes()
    {
        AssignmentSaveStatusText.Text = lastSaveMessage;
        LifecycleStatusText.Text = lastLifecycleMessage;
        ConfigurationStatusText.Text = lastConfigurationMessage;
        AutomationProperties.SetName(AssignmentSaveStatusText, "Assignment save state");
        AutomationProperties.SetName(LifecycleStatusText, "LM Studio lifecycle state");
        AutomationProperties.SetName(ConfigurationStatusText, "Model configuration save state");
        AutomationProperties.SetLiveSetting(AssignmentSaveStatusText, AutomationLiveSetting.Off);
        AutomationProperties.SetLiveSetting(LifecycleStatusText, AutomationLiveSetting.Off);
        AutomationProperties.SetLiveSetting(ConfigurationStatusText, AutomationLiveSetting.Off);
        SetAssignmentStatus(lastSaveState, lastSaveMessage);
        SetLifecycleStatus(lastLifecycleState, lastLifecycleMessage);
        SetConfigurationStatus(lastConfigurationState, lastConfigurationMessage);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ConnectionSettingsRequested;
    public event EventHandler<ProviderModelSearchChangedEventArgs>? SearchChanged;
    public event EventHandler<ProviderModelAssignmentChangedEventArgs>? AssignmentChanged;
    public event EventHandler<ProviderModelLifecycleRequestedEventArgs>? LifecycleRequested;
    public event EventHandler<ProviderModelConfigurationChangedEventArgs>? ConfigurationChanged;
    public event EventHandler<ProviderModelConfigurationReloadRequestedEventArgs>? ConfigurationReloadRequested;

    public string SearchQuery => ModelSearchText.Text;
    public string SelectedModelId => selectedModel?.Id ?? "";
    public bool HasPendingAssignment => pendingAssignment is not null;
    public bool HasPendingLifecycle => pendingLifecycle is not null;
    public bool HasPendingConfiguration => pendingConfiguration is not null;
    public bool HasPendingConfigurationReload => pendingConfigurationReload is not null;

    public bool HasUnconfirmedLifecycleReceipt =>
        lastLifecycleState == ProviderModelLifecycleActionState.Unconfirmed
        && unconfirmedLifecycleReceipt is not null
        && LifecycleReceiptMatchesConnection(currentPresentation);

    internal bool UsesCompactLayout => usesCompactLayout;
    internal ListBox CatalogList => ModelsList;
    internal ListBox LoadedList => LoadedModelsList;
    internal Expander CatalogExpander => AvailableCatalogExpander;
    internal TextBox SearchBox => ModelSearchText;
    internal Border MasterSurface => MasterPane;
    internal Border DetailSurface => DetailPane;
    internal ScrollViewer WorkspaceScroller => WorkspaceScrollViewer;
    internal ItemsControl AssignmentTargets => AssignmentTargetsItems;
    internal TextBlock AssignmentStatus => AssignmentSaveStatusText;
    internal Border AssignmentStatusSurface => AssignmentSaveStatusCard;
    internal TextBlock CatalogStatus => CatalogStatusText;
    internal TextBlock CatalogSummary => CatalogSummaryText;
    internal Border CatalogAlert => CatalogAlertBorder;
    internal Border ConnectionStatusSurface => ConnectionStatusChip;
    internal TextBlock ConnectionStatusLabel => ConnectionStatusText;
    internal Button RefreshAction => RefreshButton;
    internal Button ConnectionAction => ConnectionButton;
    internal Button CloseAction => CloseButton;
    internal Button LifecycleAction => LifecycleButton;
    internal TextBlock LifecycleStatus => LifecycleStatusText;
    internal Border LifecycleStatusSurface => LifecycleStatusCard;
    internal ProgressBar LifecycleProgress => LifecycleProgressBar;
    internal TextBlock FilteredCount => SearchResultsText;
    internal ComboBox FacetFilter => ModelFacetCombo;
    internal TextBlock AssignmentAvailability => AssignmentAvailabilityText;
    internal TextBlock SelectedAssignmentSummary => SelectedModelAssignmentsText;
    internal TextBox ContextWindowInput => ModelContextWindowText;
    internal CheckBox ProviderDefaultContextToggle => UseProviderDefaultContextCheckBox;
    internal ComboBox HistoryPolicySelector => HistoryPolicyCombo;
    internal ComboBox ResponseToneSelector => ResponseToneCombo;
    internal TextBox CustomToneInput => CustomToneText;
    internal TextBlock ConfigurationValidation => ConfigurationValidationText;
    internal TextBlock ConfigurationStatus => ConfigurationStatusText;
    internal Border ConfigurationStatusSurface => ConfigurationStatusCard;
    internal Button ConfigurationReloadAction => ConfigurationReloadButton;
    internal TextBlock ConfiguredContextEvidence => ConfiguredContextText;
    internal TextBlock EffectiveContextEvidence => EffectiveContextText;
    internal ScrollViewer DetailScroller => ModelDetailScrollViewer;
    internal int AssignmentGridColumns => AssignmentColumnCount;
    internal bool UsesIncrementalLiveShaping => usesIncrementalLiveShaping;
    internal int CatalogRowCount => modelRows.Count;
    internal int LoadedRowCount => LoadedRows().Count();
    internal int AvailableCatalogRowCount => modelRows.Count - LoadedRowCount;
    internal int VisibleCatalogRowCount => VisibleCatalogRows().Count();
    internal int PresentationApplyCount { get; private set; }

    public int AssignmentColumnCount
    {
        get => (int)GetValue(AssignmentColumnCountProperty);
        private set => SetValue(AssignmentColumnCountPropertyKey, value);
    }

    private static readonly DependencyPropertyKey AssignmentColumnCountPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(AssignmentColumnCount),
            typeof(int),
            typeof(ProviderModelAssignmentsControl),
            new PropertyMetadata(3));

    public static readonly DependencyProperty AssignmentColumnCountProperty =
        AssignmentColumnCountPropertyKey.DependencyProperty;

    public void ApplyPresentation(ProviderModelAssignmentsPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        Dispatcher.VerifyAccess();
        PresentationApplyCount++;
        var continuityGeneration = ++catalogContinuityGeneration;

        applyingPresentation = true;
        try
        {
            CancelPendingAssignmentWhenIdentityChanges(presentation);
            CancelPendingLifecycleWhenIdentityChanges(presentation);
            CancelPendingConfigurationWhenIdentityChanges(presentation);
            CancelPendingConfigurationReloadWhenIdentityChanges(presentation);
            ClearUnconfirmedLifecycleForConnectionChange(presentation);
            currentPresentation = presentation;
            var priorSelection = SelectedModelId;
            var catalogScrollViewer = FindVisualDescendant<ScrollViewer>(ModelsList);
            var priorCatalogOffset = catalogScrollViewer?.VerticalOffset ?? 0;
            var priorViewportAnchor = CaptureCatalogViewportAnchor(catalogScrollViewer);
            var catalogHadKeyboardFocus = ModelsList.IsKeyboardFocusWithin
                || LoadedModelsList.IsKeyboardFocusWithin;
            ProviderNameText.Text = DisplayOrFallback(presentation.ProviderName, "Provider");
            ConnectionStatusText.Text = DisplayOrFallback(presentation.ConnectionStatus, "Unavailable");
            CatalogStatusText.Text = DisplayOrFallback(presentation.CatalogStatus, "Model evidence is unavailable.");
            UpdateCatalogHealth(presentation);
            RefreshButton.IsEnabled = presentation.CanRefresh && !presentation.IsRefreshing;
            RefreshButton.Content = presentation.IsRefreshing ? "Refreshing…" : "Refresh";
            ConnectionButton.IsEnabled = presentation.CanOpenConnectionSettings;
            CloseButton.IsEnabled = presentation.CanClose;
            ApplyConnectionState(presentation.ConnectionState);

            targets = DistinctTargets(presentation.Targets);
            targetsById = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);

            var preserveCatalogDuringRefresh = presentation.IsRefreshing
                && presentation.Models.Count == 0
                && modelRows.Count > 0;
            if (!preserveCatalogDuringRefresh)
            {
                var presentedModels = DistinctModels(presentation.Models).ToList();
                if (lastLifecycleState == ProviderModelLifecycleActionState.Unconfirmed
                    && lastLifecycleDesiredLoaded.HasValue
                    && unconfirmedLifecycleReceipt is not null
                    && LifecycleReceiptMatchesConnection(presentation)
                    && presentedModels.All(model => !string.Equals(
                        model.Id,
                        unconfirmedLifecycleReceipt.Id,
                        StringComparison.Ordinal)))
                {
                    presentedModels.Add(unconfirmedLifecycleReceipt);
                }

                CaptureLatestPendingAssignmentAuthority(presentedModels);
                CaptureLatestPendingConfigurationAuthority(presentedModels);
                ReconcileModelRows(presentedModels);
                ApplyPendingConfigurationDraft();
                ReconcileUnconfirmedConfigurationReload(presentation);
            }
            else
            {
                foreach (var row in modelRows)
                {
                    row.RefreshAssignmentSummary(targetsById);
                }
            }

            ReconcileUnconfirmedLifecycle(presentation);
            ApplyLifecycleActivityMarkers();
            var requestedSelection = pendingAssignment?.ModelId
                ?? (!string.IsNullOrWhiteSpace(presentation.SelectedModelId)
                    ? presentation.SelectedModelId
                    : priorSelection);
            SelectModel(
                SelectableRows().FirstOrDefault(row => string.Equals(
                    row.Id,
                    requestedSelection,
                    StringComparison.Ordinal))
                ?? SelectableRows().FirstOrDefault());
            if (catalogScrollViewer is not null && priorCatalogOffset > 0)
            {
                ModelsList.UpdateLayout();
                catalogScrollViewer.ScrollToVerticalOffset(
                    Math.Min(priorCatalogOffset, catalogScrollViewer.ScrollableHeight));
            }
            RebuildSelectedDetail();
            UpdateCatalogSummary();
            UpdateSearchResults();
            UpdateEmptyState();
            ApplyInteractionState();
            ScheduleCatalogContinuity(
                requestedSelection,
                priorCatalogOffset,
                priorViewportAnchor,
                catalogHadKeyboardFocus,
                continuityGeneration);
        }
        finally
        {
            applyingPresentation = false;
        }
    }

    public bool SetAssignmentState(
        Guid changeId,
        ProviderAssignmentSaveState state,
        string? message = null)
    {
        Dispatcher.VerifyAccess();
        var pending = pendingAssignment;
        if (pending is null || pending.ChangeId != changeId)
        {
            return false;
        }

        if (state == ProviderAssignmentSaveState.Saving)
        {
            lastResolvedModelId = pending.ModelId;
            lastSaveState = ProviderAssignmentSaveState.Saving;
            lastSaveMessage = PendingAssignmentMessage(pending, message);
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Saving,
                lastSaveMessage);
            return true;
        }

        if (state is ProviderAssignmentSaveState.Failed or ProviderAssignmentSaveState.Ready)
        {
            RestorePendingAssignment(pending);
        }

        pendingAssignment = null;
        lastResolvedModelId = pending.ModelId;
        lastSaveState = state;
        lastSaveMessage = state switch
        {
            ProviderAssignmentSaveState.Saved when pending.IsTransfer => pending.IsDefaultTarget
                ? $"Changed {pending.TargetDisplayName} to {pending.ModelDisplayName}."
                : $"Moved {pending.TargetDisplayName} to {pending.ModelDisplayName}.",
            ProviderAssignmentSaveState.Saved => DisplayOrFallback(message, "Saved."),
            ProviderAssignmentSaveState.Failed => $"Failed: {DisplayOrFallback(message, "Could not save the assignment.")}",
            _ => DisplayOrFallback(message, "Ready to assign.")
        };
        RebuildSelectedDetail();
        return true;
    }

    public bool SetLifecycleState(
        Guid operationId,
        ProviderModelLifecycleActionState state,
        string? message = null)
    {
        Dispatcher.VerifyAccess();
        var pending = pendingLifecycle;
        if (pending is null || pending.OperationId != operationId)
        {
            return false;
        }

        if (state == ProviderModelLifecycleActionState.Running)
        {
            lastLifecycleModelId = pending.ModelId;
            lastLifecycleState = ProviderModelLifecycleActionState.Running;
            lastLifecycleMessage = PendingLifecycleMessage(pending, message);
            SetLifecycleStatus(
                state,
                lastLifecycleMessage);
            return true;
        }

        pendingLifecycle = null;
        pendingLifecycleMutationStarted = false;
        lastLifecycleModelId = pending.ModelId;
        lastLifecycleState = state;
        lastLifecycleDesiredLoaded = state == ProviderModelLifecycleActionState.Unconfirmed
            ? pending.Load
            : null;
        lastLifecycleConnectionIdentity = state == ProviderModelLifecycleActionState.Unconfirmed
            ? pending.ConnectionIdentity
            : "";
        lifecycleReceiptOrphanedByConnectionChange = false;
        lastLifecycleMessage = state switch
        {
            ProviderModelLifecycleActionState.Unconfirmed => DisplayOrFallback(
                message,
                pending.Load
                    ? "LM Studio accepted the load request, but loaded residency is not yet confirmed."
                    : "LM Studio accepted the unload request, but unloaded residency is not yet confirmed."),
            ProviderModelLifecycleActionState.Succeeded => DisplayOrFallback(
                message,
                pending.Load ? "Load request succeeded." : "Unload request succeeded."),
            ProviderModelLifecycleActionState.Failed => $"Failed: {DisplayOrFallback(message, "LM Studio could not change model residency.")}",
            _ => DisplayOrFallback(message, "Ready to manage LM Studio residency.")
        };
        unconfirmedLifecycleReceipt = state == ProviderModelLifecycleActionState.Unconfirmed
            ? CreateUnconfirmedLifecycleReceipt(pending)
            : null;
        EnsureUnconfirmedLifecycleReceiptRow();
        ApplyLifecycleActivityMarkers();
        RebuildSelectedDetail();
        return true;
    }

    public bool SetConfigurationState(
        Guid changeId,
        ProviderModelConfigurationSaveState state,
        string? message = null)
    {
        Dispatcher.VerifyAccess();
        var pending = pendingConfiguration;
        if (pending is null || pending.ChangeId != changeId)
        {
            return false;
        }

        lastConfigurationModelId = pending.ModelId;
        if (state == ProviderModelConfigurationSaveState.Saving)
        {
            lastConfigurationState = state;
            lastConfigurationMessage = DisplayOrFallback(message, $"Saving configuration for {pending.ModelDisplayName}…");
            SetConfigurationStatus(state, lastConfigurationMessage);
            return true;
        }

        var row = FindModel(pending.ModelId);
        if (state is ProviderModelConfigurationSaveState.Failed or ProviderModelConfigurationSaveState.Ready)
        {
            row?.SetConfiguration(pending.Previous);
        }
        else if (state == ProviderModelConfigurationSaveState.Saved)
        {
            row?.SetConfiguration(pending.Requested);
        }

        pendingConfiguration = null;
        lastConfigurationState = state;
        lastConfigurationMessage = state switch
        {
            ProviderModelConfigurationSaveState.Saved => DisplayOrFallback(message, "Model configuration saved."),
            ProviderModelConfigurationSaveState.Failed => $"Failed: {DisplayOrFallback(message, "Could not save model configuration.")}",
            ProviderModelConfigurationSaveState.Unavailable => DisplayOrFallback(message, "Model configuration is unavailable."),
            _ => DisplayOrFallback(message, "Ready to configure.")
        };
        RebuildSelectedDetail();
        return true;
    }

    public bool SetConfigurationReloadState(
        Guid operationId,
        ProviderModelConfigurationReloadState state,
        string? message = null)
    {
        Dispatcher.VerifyAccess();
        var pending = pendingConfigurationReload;
        if (pending is null || pending.OperationId != operationId)
        {
            return false;
        }

        lastReloadModelId = pending.ModelId;
        if (state == ProviderModelConfigurationReloadState.Running)
        {
            lastReloadState = state;
            lastReloadMessage = DisplayOrFallback(message, $"Reloading {pending.ModelDisplayName}…");
            SetConfigurationReloadStatus(FindModel(pending.ModelId), state, lastReloadMessage);
            return true;
        }

        pendingConfigurationReload = null;
        pendingConfigurationReloadMutationStarted = false;
        lastReloadState = state;
        lastReloadConnectionIdentity = state == ProviderModelConfigurationReloadState.Unconfirmed
            ? pending.ConnectionIdentity
            : "";
        lastReloadMessage = state switch
        {
            ProviderModelConfigurationReloadState.Succeeded => DisplayOrFallback(message, "Reload confirmed; the configured context is active."),
            ProviderModelConfigurationReloadState.Unconfirmed => DisplayOrFallback(message, "Reload request accepted; waiting for effective context evidence."),
            ProviderModelConfigurationReloadState.Failed => $"Failed: {DisplayOrFallback(message, "Could not reload the model configuration.")}",
            ProviderModelConfigurationReloadState.Required => DisplayOrFallback(message, "Reload required to apply the configured context."),
            ProviderModelConfigurationReloadState.Unavailable => DisplayOrFallback(message, "Reload is unavailable for this model."),
            _ => DisplayOrFallback(message, "Configuration is active.")
        };
        if (state == ProviderModelConfigurationReloadState.Succeeded
            && FindModel(pending.ModelId) is { } row)
        {
            row.SetConfiguration(row.Configuration with { RequiresReload = false });
        }

        RebuildSelectedDetail();
        return true;
    }

    public bool MarkLifecycleMutationStarted(Guid operationId)
    {
        Dispatcher.VerifyAccess();
        if (pendingLifecycle is null || pendingLifecycle.OperationId != operationId)
        {
            return false;
        }

        pendingLifecycleMutationStarted = true;
        return true;
    }

    public bool MarkConfigurationReloadMutationStarted(Guid operationId)
    {
        Dispatcher.VerifyAccess();
        if (pendingConfigurationReload is null || pendingConfigurationReload.OperationId != operationId)
        {
            return false;
        }

        pendingConfigurationReloadMutationStarted = true;
        return true;
    }

    public bool FocusCatalog()
    {
        var list = selectedModel?.Availability == ProviderModelAvailability.Loaded
            ? LoadedModelsList
            : ModelsList;
        if (!list.IsEnabled)
        {
            return false;
        }

        list.Focus();
        list.ScrollIntoView(list.SelectedItem);
        if (list.SelectedItem is not null
            && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
        {
            return item.Focus();
        }

        return list.IsKeyboardFocusWithin;
    }

    public bool FocusSearch() => ModelSearchText.Focus();

    public bool SelectModel(string modelId, bool focusConfiguration = false)
    {
        Dispatcher.VerifyAccess();
        var requested = modelId?.Trim() ?? "";
        if (requested.Length == 0)
        {
            return false;
        }

        var model = ResolveModel(requested);
        if (model is null)
        {
            return false;
        }

        if (model.Availability != ProviderModelAvailability.Loaded)
        {
            AvailableCatalogExpander.IsExpanded = true;
            var typedQuery = ModelSearchText.Text.Trim();
            var pendingQueryWouldHideModel = typedQuery.Length > 0
                && !model.SearchText.Contains(typedQuery, StringComparison.OrdinalIgnoreCase);
            if (!MatchesCurrentSearch(model) || pendingQueryWouldHideModel)
            {
                searchDebounceTimer.Stop();
                ModelSearchText.Clear();
                ModelFacetCombo.SelectedIndex = 0;
                selectedFacet = ProviderModelFacet.All;
                committedSearchQuery = "";
                modelRowsView.Refresh();
                UpdateSearchChrome();
                UpdateSearchResults();
                UpdateEmptyState();
            }
        }

        SelectModel(model);
        RebuildSelectedDetail();
        var list = model.Availability == ProviderModelAvailability.Loaded
            ? LoadedModelsList
            : ModelsList;
        list.ScrollIntoView(model);
        list.UpdateLayout();
        if (focusConfiguration)
        {
            ModelContextWindowText.BringIntoView();
            Dispatcher.BeginInvoke(() =>
            {
                ModelContextWindowText.BringIntoView();
                ModelContextWindowText.Focus();
                ModelContextWindowText.SelectAll();
            }, DispatcherPriority.Input);
        }

        return true;
    }

    internal static bool UsesCompactLayoutAt(
        double contentWidth,
        double hostWindowWidth = double.NaN,
        bool contentIsScaled = false)
    {
        if (!double.IsNaN(hostWindowWidth) && hostWindowWidth > 0)
        {
            return hostWindowWidth < WideHostWindowThreshold
                || (contentIsScaled
                    && contentWidth > 0
                    && contentWidth <= CompactContentThreshold);
        }

        return contentWidth > 0 && contentWidth <= CompactContentThreshold;
    }

    internal static int AssignmentColumnsAt(bool compact, double contentWidth, bool contentIsScaled = false)
    {
        if (contentIsScaled)
        {
            return 1;
        }

        if (!compact)
        {
            return 3;
        }

        return contentWidth >= 720 ? 3 : contentWidth >= 480 ? 2 : 1;
    }

    internal void ApplyResponsiveLayout(bool compact)
    {
        usesCompactLayout = compact;
        if (compact)
        {
            WorkspaceScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            MasterColumn.Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGapColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
            MasterRow.Height = GridLength.Auto;
            CompactWorkspaceGapRow.Height = new GridLength(12);
            DetailRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(MasterPane, 0);
            Grid.SetColumn(MasterPane, 0);
            Grid.SetColumnSpan(MasterPane, 3);
            Grid.SetRow(DetailPane, 2);
            Grid.SetColumn(DetailPane, 0);
            Grid.SetColumnSpan(DetailPane, 3);
            MasterPane.MaxHeight = 360;
            DetailPane.ClearValue(MaxWidthProperty);
        }
        else
        {
            WorkspaceScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            MasterColumn.Width = new GridLength(1, GridUnitType.Star);
            WorkspaceGapColumn.Width = new GridLength(12);
            DetailColumn.Width = new GridLength(380);
            MasterRow.Height = new GridLength(1, GridUnitType.Star);
            CompactWorkspaceGapRow.Height = new GridLength(0);
            DetailRow.Height = new GridLength(0);
            Grid.SetRow(MasterPane, 0);
            Grid.SetColumn(MasterPane, 0);
            Grid.SetColumnSpan(MasterPane, 1);
            Grid.SetRow(DetailPane, 0);
            Grid.SetColumn(DetailPane, 2);
            Grid.SetColumnSpan(DetailPane, 1);
            MasterPane.ClearValue(MaxHeightProperty);
            DetailPane.MaxWidth = 380;
        }

        var compactHeader = ActualWidth > 0 && ActualWidth <= CompactHeaderThreshold;
        CompactHeaderActionsRow.Height = compactHeader ? GridLength.Auto : new GridLength(0);
        HeaderActionsGapColumn.Width = compactHeader ? new GridLength(0) : new GridLength(16);
        HeaderActionsColumn.Width = compactHeader ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(HeaderActionsPanel, compactHeader ? 1 : 0);
        Grid.SetColumn(HeaderActionsPanel, compactHeader ? 0 : 2);
        Grid.SetColumnSpan(HeaderActionsPanel, compactHeader ? 3 : 1);
        HeaderActionsPanel.HorizontalAlignment = compactHeader
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        HeaderActionsPanel.Margin = compactHeader
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);
        AssignmentColumnCount = AssignmentColumnsAt(compact, ActualWidth, HasScaledLayoutTransform());
    }

    private void ProviderModelAssignmentsControl_Loaded(object sender, RoutedEventArgs e)
    {
        AttachLayoutHostWindow(Window.GetWindow(this));
        ApplyResponsiveLayout(UsesCompactLayoutAt(
            ActualWidth,
            layoutHostWindow?.ActualWidth ?? double.NaN,
            HasScaledLayoutTransform()));
    }

    private void ProviderModelAssignmentsControl_Unloaded(object sender, RoutedEventArgs e)
    {
        catalogContinuityGeneration++;
        if (searchDebounceTimer.IsEnabled)
        {
            CommitSearchAndFacet();
        }
        else
        {
            searchDebounceTimer.Stop();
        }
        AttachLayoutHostWindow(null);
    }

    private void ProviderModelAssignmentsControl_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false)
        {
            return;
        }

        catalogContinuityGeneration++;
        if (searchDebounceTimer.IsEnabled)
        {
            CommitSearchAndFacet();
        }
    }

    private void ProviderModelAssignmentsControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayoutAt(
            e.NewSize.Width,
            layoutHostWindow?.ActualWidth ?? double.NaN,
            HasScaledLayoutTransform()));

    private void LayoutHostWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayoutAt(
            ActualWidth,
            e.NewSize.Width,
            HasScaledLayoutTransform()));

    private bool HasScaledLayoutTransform() =>
        LayoutTransform is { } transform && !transform.Value.IsIdentity;

    private void AttachLayoutHostWindow(Window? host)
    {
        if (ReferenceEquals(layoutHostWindow, host))
        {
            return;
        }

        if (layoutHostWindow is not null)
        {
            layoutHostWindow.SizeChanged -= LayoutHostWindow_SizeChanged;
        }

        layoutHostWindow = host;
        if (layoutHostWindow is not null)
        {
            layoutHostWindow.SizeChanged += LayoutHostWindow_SizeChanged;
        }
    }

    private void ProviderModelAssignmentsControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = FocusSearch();
            return;
        }

        if (e.Key == Key.F5 && RefreshButton.IsEnabled)
        {
            e.Handled = true;
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == Key.Down
            && LoadedModelsList.IsKeyboardFocusWithin
            && ReferenceEquals(selectedModel, LoadedRows().LastOrDefault())
            && AvailableCatalogExpander.IsExpanded
            && VisibleCatalogRows().FirstOrDefault() is { } firstCatalogModel)
        {
            e.Handled = MoveKeyboardSelection(firstCatalogModel, ModelsList);
            return;
        }

        if (e.Key == Key.Up
            && ModelsList.IsKeyboardFocusWithin
            && ReferenceEquals(selectedModel, VisibleCatalogRows().FirstOrDefault())
            && LoadedRows().LastOrDefault() is { } lastLoadedModel)
        {
            e.Handled = MoveKeyboardSelection(lastLoadedModel, LoadedModelsList);
            return;
        }

        if (e.Key == Key.Escape && CloseButton.IsEnabled)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ConnectionButton_Click(object sender, RoutedEventArgs e) =>
        ConnectionSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void LifecycleButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedModel is not { } model
            || pendingLifecycle is not null
            || pendingAssignment is not null
            || currentPresentation?.IsRefreshing == true
            || currentPresentation?.CanRunLifecycle != true
            || !TryLifecycleAction(model, out var load))
        {
            return;
        }

        var operationId = Guid.NewGuid();
        pendingLifecycle = new PendingLifecycle(
            operationId,
            model.Id,
            model.DisplayName,
            load,
            EffectiveConnectionIdentity(currentPresentation));
        pendingLifecycleMutationStarted = false;
        lastLifecycleModelId = model.Id;
        lastLifecycleState = ProviderModelLifecycleActionState.Running;
        lastLifecycleMessage = load
            ? $"Loading {model.DisplayName}…"
            : $"Unloading {model.DisplayName}…";
        SetLifecycleStatus(lastLifecycleState, lastLifecycleMessage);
        ApplyLifecycleActivityMarkers();
        RebuildSelectedDetail();

        var handler = LifecycleRequested;
        if (handler is null)
        {
            SetLifecycleState(
                operationId,
                ProviderModelLifecycleActionState.Failed,
                "No LM Studio lifecycle handler is connected.");
            return;
        }

        try
        {
            handler.Invoke(
                this,
                new ProviderModelLifecycleRequestedEventArgs(operationId, model.Id, load));
        }
        catch
        {
            SetLifecycleState(
                operationId,
                ProviderModelLifecycleActionState.Failed,
                "The LM Studio lifecycle handler could not accept the request.");
        }
    }

    private void ModelContextWindowText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = TryRequestConfigurationChange();
    }

    private void ModelContextWindowText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _ = TryRequestConfigurationChange();

    private void CustomToneText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = TryRequestConfigurationChange();
        }
    }

    private void CustomToneText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _ = TryRequestConfigurationChange();

    private void UseProviderDefaultContextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (applyingConfigurationControls)
        {
            return;
        }

        ModelContextWindowText.IsEnabled = UseProviderDefaultContextCheckBox.IsChecked != true
            && CanEditConfiguration();
        ModelContextWindowText.Text = UseProviderDefaultContextCheckBox.IsChecked == true
            ? "Provider default"
            : SelectedConfiguration().ContextWindow > 0
                ? SelectedConfiguration().ContextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Math.Max(SelectedConfiguration().MinimumContextWindow, 512).ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateConfigurationValidation();
        _ = TryRequestConfigurationChange();
    }

    private void HistoryPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingConfigurationControls)
        {
            return;
        }

        if (HistoryPolicyCombo.SelectedItem is HistoryPolicyOption { IsEnabled: true })
        {
            _ = TryRequestConfigurationChange();
        }
    }

    private void ResponseToneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingConfigurationControls)
        {
            return;
        }

        CustomTonePanel.Visibility = SelectedResponseTone() == ProviderModelResponseTone.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        CustomToneText.IsEnabled = SelectedResponseTone() == ProviderModelResponseTone.Custom
            && CanEditConfiguration();
        UpdateConfigurationValidation();
        _ = TryRequestConfigurationChange();
    }

    private void ModelConfigurationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!applyingConfigurationControls)
        {
            UpdateConfigurationValidation();
        }
    }

    private void ConfigurationReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedModel is not { } model
            || pendingConfigurationReload is not null
            || pendingConfiguration is not null
            || pendingAssignment is not null
            || pendingLifecycle is not null
            || !model.Configuration.RequiresReload
            || model.Availability != ProviderModelAvailability.Loaded)
        {
            return;
        }

        var operationId = Guid.NewGuid();
        pendingConfigurationReload = new PendingConfigurationReload(
            operationId,
            model.Id,
            model.DisplayName,
            model.Configuration.ConfigurationIdentity,
            EffectiveConnectionIdentity(currentPresentation));
        pendingConfigurationReloadMutationStarted = false;
        lastReloadModelId = model.Id;
        lastReloadState = ProviderModelConfigurationReloadState.Running;
        lastReloadMessage = $"Reloading {model.DisplayName} to apply its context window…";
        RebuildSelectedDetail();
        var handler = ConfigurationReloadRequested;
        if (handler is null)
        {
            SetConfigurationReloadState(
                operationId,
                ProviderModelConfigurationReloadState.Failed,
                "No configuration reload handler is connected.");
            return;
        }

        try
        {
            handler.Invoke(
                this,
                new ProviderModelConfigurationReloadRequestedEventArgs(
                    operationId,
                    model.Id,
                    model.Configuration.ConfigurationIdentity));
        }
        catch
        {
            SetConfigurationReloadState(
                operationId,
                ProviderModelConfigurationReloadState.Failed,
                "The configuration reload handler could not accept the request.");
        }
    }

    private void ModelSearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (modelRowsView is null)
        {
            return;
        }

        UpdateSearchChrome();
        searchDebounceTimer.Stop();
        searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        searchDebounceTimer.Stop();
        CommitSearchAndFacet();
    }

    private void ModelFacetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelFacetCombo.SelectedItem is ModelFacetOption option)
        {
            selectedFacet = option.Facet;
        }

        if (modelRowsView is not null)
        {
            CommitSearchAndFacet();
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        searchDebounceTimer.Stop();
        ModelSearchText.Clear();
        searchDebounceTimer.Stop();
        CommitSearchAndFacet();
        ModelSearchText.Focus();
    }

    private void EmptyClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        searchDebounceTimer.Stop();
        ModelSearchText.Clear();
        searchDebounceTimer.Stop();
        ModelFacetCombo.SelectedIndex = 0;
        selectedFacet = ProviderModelFacet.All;
        CommitSearchAndFacet();
        ModelSearchText.Focus();
    }

    private void CommitSearchAndFacet()
    {
        searchDebounceTimer.Stop();
        committedSearchQuery = ModelSearchText.Text.Trim();
        synchronizingSelection = true;
        try
        {
            modelRowsView.Refresh();
        }
        finally
        {
            synchronizingSelection = false;
        }

        var firstCatalogResult = VisibleCatalogRows().FirstOrDefault();
        var activeCatalogFilter = committedSearchQuery.Length > 0
            || selectedFacet != ProviderModelFacet.All;
        if (selectedModel is { Availability: ProviderModelAvailability.Loaded }
            && activeCatalogFilter
            && firstCatalogResult is not null)
        {
            SelectModel(firstCatalogResult);
        }
        else if (selectedModel is not { } selected
            || (selected.Availability != ProviderModelAvailability.Loaded
                && !MatchesCurrentSearch(selected)))
        {
            SelectModel(firstCatalogResult ?? LoadedRows().FirstOrDefault());
        }
        else
        {
            SynchronizeSelection(selected);
        }

        RebuildSelectedDetail();
        UpdateSearchChrome();
        UpdateSearchResults();
        UpdateEmptyState();
        if (!string.Equals(lastPublishedSearchQuery, ModelSearchText.Text, StringComparison.Ordinal))
        {
            lastPublishedSearchQuery = ModelSearchText.Text;
            SearchChanged?.Invoke(this, new ProviderModelSearchChangedEventArgs(ModelSearchText.Text));
        }
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleListSelectionChanged(ModelsList);
    }

    private void LoadedModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleListSelectionChanged(LoadedModelsList);
    }

    private void HandleListSelectionChanged(ListBox source)
    {
        if (synchronizingSelection || applyingPresentation)
        {
            return;
        }

        if (source.SelectedItem is not ModelRowState model)
        {
            return;
        }

        SelectModel(model);
        if (!applyingPresentation && pendingAssignment is null)
        {
            lastResolvedModelId = "";
            lastSaveState = ProviderAssignmentSaveState.Ready;
            lastSaveMessage = "Ready to assign.";
        }

        RebuildSelectedDetail();
    }

    private void AssignmentTargetCheckBox_ToggleRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || checkBox.DataContext is not TargetAssignmentState target
            || selectedModel is not { } model
            || pendingAssignment is not null
            || currentPresentation?.CanAssign != true)
        {
            return;
        }

        var previousValue = model.IsAssigned(target.TargetId);
        var requestedValue = checkBox.IsChecked == true;
        if (requestedValue == previousValue)
        {
            return;
        }

        var targetPresentation = targetsById.TryGetValue(target.TargetId, out var presentedTarget)
            ? presentedTarget
            : new ProviderAssignmentTargetPresentation(target.TargetId, target.DisplayName);
        var mutations = BuildAssignmentMutations(model, target.TargetId, requestedValue);
        ApplyPendingAssignmentMutations(target.TargetId, mutations);
        ReconcileSelectedTargetRows(model);
        RefreshSelectedAssignmentText();

        var changeId = Guid.NewGuid();
        pendingAssignment = new PendingAssignment(
            changeId,
            model.Id,
            model.DisplayName,
            target.TargetId,
            target.DisplayName,
            requestedValue,
            targetPresentation.IsDefault
                || targetPresentation.AssignmentState == ProviderTargetAssignmentState.Default,
            mutations
                .Where(mutation => mutation.PreviousValue
                    && !mutation.RequestedValue
                    && !string.Equals(mutation.ModelId, model.Id, StringComparison.Ordinal))
                .Select(mutation => mutation.ModelDisplayName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            mutations,
            EffectiveConnectionIdentity(currentPresentation));
        lastResolvedModelId = model.Id;
        lastSaveState = ProviderAssignmentSaveState.Saving;
        lastSaveMessage = AssignmentProgressMessage(pendingAssignment);
        SetAssignmentStatus(lastSaveState, lastSaveMessage);
        ApplyInteractionState();

        var handler = AssignmentChanged;
        if (handler is null)
        {
            SetAssignmentState(
                changeId,
                ProviderAssignmentSaveState.Failed,
                "No assignment handler is connected.");
            return;
        }

        try
        {
            handler.Invoke(
                this,
                new ProviderModelAssignmentChangedEventArgs(
                    changeId,
                    model.Id,
                    target.TargetId,
                    requestedValue));
        }
        catch
        {
            SetAssignmentState(
                changeId,
                ProviderAssignmentSaveState.Failed,
                "The assignment handler could not accept the change.");
        }
    }

    private IReadOnlyList<PendingAssignmentMutation> BuildAssignmentMutations(
        ModelRowState selected,
        string targetId,
        bool requestedValue)
    {
        return modelRows
            .Select(row =>
            {
                var previousValue = row.IsAssigned(targetId);
                var nextValue = ReferenceEquals(row, selected)
                    ? requestedValue
                    : requestedValue
                        ? false
                        : previousValue;
                return new PendingAssignmentMutation(
                row.Id,
                row.DisplayName,
                previousValue,
                nextValue);
            })
            .ToArray();
    }

    private void ApplyPendingAssignmentMutations(
        string targetId,
        IEnumerable<PendingAssignmentMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            var row = FindModel(mutation.ModelId);
            row?.SetAssigned(targetId, mutation.RequestedValue);
            row?.RefreshAssignmentSummary(targetsById);
        }
    }

    private void RestorePendingAssignment(PendingAssignment pending)
    {
        foreach (var mutation in pending.Mutations)
        {
            var row = FindModel(mutation.ModelId);
            row?.SetAssigned(pending.TargetId, mutation.PreviousValue);
            row?.RefreshAssignmentSummary(targetsById);
        }
    }

    private void CaptureLatestPendingAssignmentAuthority(
        IReadOnlyList<ProviderModelAssignmentPresentation> presentedModels)
    {
        var pending = pendingAssignment;
        if (pending is null)
        {
            return;
        }

        var latest = presentedModels
            .Select(model =>
            {
                var previousValue = model.AssignedTargetIds.Contains(
                    pending.TargetId,
                    StringComparer.Ordinal);
                var requestedValue = pending.RequestedValue
                    ? string.Equals(model.Id, pending.ModelId, StringComparison.Ordinal)
                    : string.Equals(model.Id, pending.ModelId, StringComparison.Ordinal)
                        ? false
                        : previousValue;
                return new PendingAssignmentMutation(
                    model.Id,
                    model.DisplayName,
                    previousValue,
                    requestedValue);
            })
            .ToArray();
        pendingAssignment = pending with { Mutations = latest };
    }

    private void RebuildSelectedDetail()
    {
        if (selectedModel is not { } model)
        {
            selectedTargetRows.Clear();
            ModelDetailContent.Visibility = Visibility.Collapsed;
            ModelDetailEmptyState.Visibility = Visibility.Visible;
            return;
        }

        SelectedModelNameText.Text = model.DisplayName;
        SelectedModelIdentifierText.Text = model.Id;
        SelectedModelIdentifierText.ToolTip = model.Id;
        SelectedModelMetadataText.Text = model.Metadata;
        SelectedModelMetadataText.ToolTip = model.AutomationHelp;
        SelectedModelAssignmentsText.Text = model.AssignmentSummary;
        SelectedModelLifecycleHelpText.Text = model.LifecycleHelp;
        ApplyLifecycleAction(model);
        ReconcileSelectedTargetRows(model);
        RefreshSelectedAssignmentText();
        ApplyConfigurationDetail(model);
        ModelDetailContent.Visibility = Visibility.Visible;
        ModelDetailEmptyState.Visibility = Visibility.Collapsed;
        AssignmentAvailabilityText.Text = AssignmentAvailabilityMessage();

        if (pendingAssignment is not null)
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Saving, lastSaveMessage);
        }
        else if (pendingLifecycle is not null)
        {
            var activity = pendingLifecycle.Load ? "loading" : "unloading";
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Unavailable,
                $"Assignments are paused while {pendingLifecycle.ModelDisplayName} is {activity}. Catalog browsing remains available.");
        }
        else if (currentPresentation?.IsRefreshing == true)
        {
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Unavailable,
                "Assignments are unavailable while provider models refresh.");
        }
        else if (currentPresentation?.CanAssign != true)
        {
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Unavailable,
                "Assignments are unavailable for the current provider state.");
        }
        else if (string.Equals(lastResolvedModelId, model.Id, StringComparison.Ordinal))
        {
            SetAssignmentStatus(lastSaveState, lastSaveMessage);
        }
        else if (selectedTargetRows.Count == 0)
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Unavailable, "No assignment targets are available.");
        }
        else
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Ready, "Ready to assign.");
        }

        ApplyInteractionState();
    }

    private void ApplyConfigurationDetail(ModelRowState model)
    {
        var configuration = model.Configuration;
        applyingConfigurationControls = true;
        try
        {
            var providerDefault = configuration.ContextWindow == 0;
            UseProviderDefaultContextCheckBox.IsChecked = providerDefault;
            ModelContextWindowText.Text = providerDefault
                ? "Provider default"
                : configuration.ContextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SelectHistoryPolicy(configuration.HistoryPolicy, configuration.ChapteredAvailable);
            SelectResponseTone(configuration.ResponseTone);
            CustomToneText.Text = configuration.CustomTone;
            CustomTonePanel.Visibility = configuration.ResponseTone == ProviderModelResponseTone.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            applyingConfigurationControls = false;
        }

        var editable = CanEditConfiguration();
        UseProviderDefaultContextCheckBox.IsEnabled = editable;
        ModelContextWindowText.IsEnabled = editable && configuration.ContextWindow != 0;
        HistoryPolicyCombo.IsEnabled = editable;
        ResponseToneCombo.IsEnabled = editable;
        CustomToneText.IsEnabled = editable && configuration.ResponseTone == ProviderModelResponseTone.Custom;
        ConfiguredContextText.Text = configuration.ContextWindow == 0
            ? "Provider default"
            : $"{configuration.ContextWindow:n0} tokens";
        EffectiveContextText.Text = configuration.EffectiveContextWindow > 0
            ? $"{configuration.EffectiveContextWindow:n0} tokens. {DisplayOrFallback(configuration.ContextEvidence, "Provider-reported evidence.")}"
            : DisplayOrFallback(configuration.ContextEvidence, "Effective context is not reported.");
        AutomationProperties.SetItemStatus(
            EffectiveContextText,
            configuration.EffectiveContextWindow > 0 ? "Observed" : "Unavailable");
        UpdateConfigurationValidation();

        if (pendingConfiguration is { } pending
            && string.Equals(pending.ModelId, model.Id, StringComparison.Ordinal))
        {
            SetConfigurationStatus(ProviderModelConfigurationSaveState.Saving, lastConfigurationMessage);
        }
        else if (string.Equals(lastConfigurationModelId, model.Id, StringComparison.Ordinal))
        {
            SetConfigurationStatus(lastConfigurationState, lastConfigurationMessage);
        }
        else
        {
            SetConfigurationStatus(
                configuration.CanEdit ? ProviderModelConfigurationSaveState.Ready : ProviderModelConfigurationSaveState.Unavailable,
                DisplayOrFallback(configuration.Status, configuration.CanEdit ? "Ready to configure." : "Model configuration is unavailable."));
        }

        var reloadState = pendingConfigurationReload is { } reload
            && string.Equals(reload.ModelId, model.Id, StringComparison.Ordinal)
                ? ProviderModelConfigurationReloadState.Running
                : string.Equals(lastReloadModelId, model.Id, StringComparison.Ordinal)
                  && (lastReloadState == ProviderModelConfigurationReloadState.Failed
                      || lastReloadState == ProviderModelConfigurationReloadState.Unconfirmed)
                    ? lastReloadState
                : configuration.RequiresReload
                    ? ProviderModelConfigurationReloadState.Required
                : string.Equals(lastReloadModelId, model.Id, StringComparison.Ordinal)
                    ? lastReloadState
                    : ProviderModelConfigurationReloadState.NotRequired;
        var reloadMessage = reloadState == ProviderModelConfigurationReloadState.Running
            ? lastReloadMessage
            : reloadState == ProviderModelConfigurationReloadState.Required
                ? "Reload required to apply the configured context window. Routing assignments remain unchanged."
                : string.Equals(lastReloadModelId, model.Id, StringComparison.Ordinal)
                    ? lastReloadMessage
                    : "Configuration is active; no reload is required.";
        SetConfigurationReloadStatus(model, reloadState, reloadMessage);
    }

    private ProviderModelConfigurationPresentation SelectedConfiguration() =>
        selectedModel?.Configuration ?? UnavailableConfiguration();

    private bool CanEditConfiguration() =>
        selectedModel?.Configuration.CanEdit == true
        && currentPresentation?.IsRefreshing == false
        && pendingAssignment is null
        && pendingLifecycle is null
        && pendingConfiguration is null
        && pendingConfigurationReload is null;

    private void SelectHistoryPolicy(ProviderModelHistoryPolicy policy, bool chapteredAvailable)
    {
        HistoryPolicyCombo.ItemsSource = HistoryPolicyOption.OptionsFor(chapteredAvailable);
        HistoryPolicyCombo.SelectedItem = HistoryPolicyCombo.Items
            .Cast<HistoryPolicyOption>()
            .FirstOrDefault(option => option.Policy == policy)
            ?? HistoryPolicyCombo.Items.Cast<HistoryPolicyOption>().First();
        ApplyHistoryPolicyItemContainers();
        Dispatcher.BeginInvoke(ApplyHistoryPolicyItemContainers, DispatcherPriority.Loaded);
    }

    private void HistoryPolicyItemContainers_StatusChanged(object? sender, EventArgs e) =>
        ApplyHistoryPolicyItemContainers();

    private void ApplyHistoryPolicyItemContainers()
    {
        foreach (var option in HistoryPolicyCombo.Items.Cast<HistoryPolicyOption>())
        {
            if (HistoryPolicyCombo.ItemContainerGenerator.ContainerFromItem(option) is not ComboBoxItem container)
            {
                continue;
            }

            container.IsEnabled = option.IsEnabled;
            AutomationProperties.SetName(container, option.AutomationName);
            AutomationProperties.SetHelpText(container, option.HelpText);
        }
    }

    private void SelectResponseTone(ProviderModelResponseTone tone)
    {
        ResponseToneCombo.SelectedItem = ResponseToneOption.Options
            .FirstOrDefault(option => option.Tone == tone)
            ?? ResponseToneOption.Options[0];
    }

    private ProviderModelHistoryPolicy SelectedHistoryPolicy() =>
        HistoryPolicyCombo.SelectedItem is HistoryPolicyOption option
            ? option.Policy
            : ProviderModelHistoryPolicy.Strict;

    private ProviderModelResponseTone SelectedResponseTone() =>
        ResponseToneCombo.SelectedItem is ResponseToneOption option
            ? option.Tone
            : ProviderModelResponseTone.Default;

    private bool TryConfigurationDraft(out ProviderModelConfigurationPresentation draft, out string error)
    {
        var current = SelectedConfiguration();
        var providerDefault = UseProviderDefaultContextCheckBox.IsChecked == true;
        var contextWindow = 0;
        if (!providerDefault
            && (!int.TryParse(
                    ModelContextWindowText.Text.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out contextWindow)
                || contextWindow < current.MinimumContextWindow
                || contextWindow > current.MaximumContextWindow))
        {
            draft = current;
            error = $"Enter a whole number from {current.MinimumContextWindow:n0} through {current.MaximumContextWindow:n0}, or turn on Provider default.";
            return false;
        }

        var tone = SelectedResponseTone();
        var customTone = CustomToneText.Text.Trim();
        if (tone == ProviderModelResponseTone.Custom && customTone.Length == 0)
        {
            draft = current;
            error = "Enter a custom tone instruction or choose another response tone.";
            return false;
        }

        draft = NormalizeConfiguration(current with
        {
            ContextWindow = providerDefault ? 0 : contextWindow,
            HistoryPolicy = SelectedHistoryPolicy(),
            ResponseTone = tone,
            CustomTone = tone == ProviderModelResponseTone.Custom ? customTone : "",
            RequiresReload = current.RequiresReload
                || (selectedModel?.Availability == ProviderModelAvailability.Loaded
                    && current.ContextWindow != (providerDefault ? 0 : contextWindow))
        });
        error = "";
        return true;
    }

    private bool TryRequestConfigurationChange()
    {
        if (applyingConfigurationControls
            || selectedModel is not { } model
            || !CanEditConfiguration()
            || !TryConfigurationDraft(out var draft, out _))
        {
            UpdateConfigurationValidation();
            return false;
        }

        if (ConfigurationEquivalent(model.Configuration, draft))
        {
            UpdateConfigurationValidation();
            return false;
        }

        var changeId = Guid.NewGuid();
        pendingConfiguration = new PendingConfiguration(
            changeId,
            model.Id,
            model.DisplayName,
            model.Configuration,
            draft,
            model.Configuration.ConfigurationIdentity,
            EffectiveConnectionIdentity(currentPresentation));
        model.SetConfiguration(draft);
        lastConfigurationModelId = model.Id;
        lastConfigurationState = ProviderModelConfigurationSaveState.Saving;
        lastConfigurationMessage = $"Saving configuration for {model.DisplayName}…";
        ApplyConfigurationDetail(model);
        ApplyInteractionState();

        var handler = ConfigurationChanged;
        if (handler is null)
        {
            SetConfigurationState(
                changeId,
                ProviderModelConfigurationSaveState.Failed,
                "No model configuration handler is connected.");
            return false;
        }

        try
        {
            handler.Invoke(
                this,
                new ProviderModelConfigurationChangedEventArgs(
                    changeId,
                    model.Id,
                    draft.ContextWindow,
                    draft.HistoryPolicy,
                    draft.ResponseTone,
                    draft.CustomTone,
                    model.Configuration.ConfigurationIdentity));
            return true;
        }
        catch
        {
            SetConfigurationState(
                changeId,
                ProviderModelConfigurationSaveState.Failed,
                "The model configuration handler could not accept the change.");
            return false;
        }
    }

    private void UpdateConfigurationValidation()
    {
        if (applyingConfigurationControls || selectedModel is null)
        {
            return;
        }

        var valid = TryConfigurationDraft(out _, out var error);
        ConfigurationValidationText.Text = error;
        ConfigurationValidationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetItemStatus(ConfigurationValidationText, valid ? "Valid" : "Invalid");
    }

    private static bool ConfigurationEquivalent(
        ProviderModelConfigurationPresentation left,
        ProviderModelConfigurationPresentation right) =>
        left.ContextWindow == right.ContextWindow
        && left.HistoryPolicy == right.HistoryPolicy
        && left.ResponseTone == right.ResponseTone
        && string.Equals(left.CustomTone, right.CustomTone, StringComparison.Ordinal);

    private void SetConfigurationStatus(ProviderModelConfigurationSaveState state, string message)
    {
        var safe = NormalizeStatus(message);
        ConfigurationStatusText.Text = safe;
        AutomationProperties.SetItemStatus(ConfigurationStatusText, state.ToString());
        AutomationProperties.SetHelpText(ConfigurationStatusText, safe);
        var borderKey = state switch
        {
            ProviderModelConfigurationSaveState.Saving => "Arena.Brush.Info",
            ProviderModelConfigurationSaveState.Saved => "Arena.Brush.Success",
            ProviderModelConfigurationSaveState.Failed => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var textKey = state switch
        {
            ProviderModelConfigurationSaveState.Saving => "Arena.Brush.Info",
            ProviderModelConfigurationSaveState.Saved => "Arena.Brush.Success",
            ProviderModelConfigurationSaveState.Failed => "DangerTextBrush",
            _ => "MutedTextBrush"
        };
        ConfigurationStatusCard.SetResourceReference(Border.BorderBrushProperty, borderKey);
        ConfigurationStatusText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
    }

    private void SetConfigurationReloadStatus(
        ModelRowState? model,
        ProviderModelConfigurationReloadState state,
        string message)
    {
        var label = state switch
        {
            ProviderModelConfigurationReloadState.Required => "Reload to apply",
            ProviderModelConfigurationReloadState.Running => "Reloading…",
            ProviderModelConfigurationReloadState.Unconfirmed => "Awaiting confirmation",
            ProviderModelConfigurationReloadState.Failed => "Retry reload",
            ProviderModelConfigurationReloadState.Unavailable => "Reload unavailable",
            _ => "Configuration is active"
        };
        var enabled = model is not null
            && model.Availability == ProviderModelAvailability.Loaded
            && pendingAssignment is null
            && pendingLifecycle is null
            && pendingConfiguration is null
            && pendingConfigurationReload is null
            && state is ProviderModelConfigurationReloadState.Required or ProviderModelConfigurationReloadState.Failed;
        ConfigurationReloadButton.Content = label;
        ConfigurationReloadButton.IsEnabled = enabled;
        AutomationProperties.SetName(ConfigurationReloadButton, model is null ? label : $"{label} for {model.DisplayName}");
        AutomationProperties.SetHelpText(ConfigurationReloadButton, NormalizeStatus(message));
        AutomationProperties.SetItemStatus(ConfigurationReloadButton, state.ToString());
    }

    private void ReconcileSelectedTargetRows(ModelRowState model)
    {
        var defaultTargetIds = targets
            .Where(target => target.IsDefault
                || target.AssignmentState == ProviderTargetAssignmentState.Default)
            .Select(target => target.Id)
            .ToArray();
        var selectedIsDefault = defaultTargetIds.Any(model.IsAssigned);
        var modelTargets = targets
            .OrderBy(TargetDisplayOrder)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(target => TargetPresentationForModel(model, target, selectedIsDefault))
            .ToArray();
        var targetIdsMatch = selectedTargetRows.Count == modelTargets.Length
            && selectedTargetRows.Select(row => row.TargetId)
                .SequenceEqual(modelTargets.Select(target => target.Id), StringComparer.Ordinal);
        if (!targetIdsMatch)
        {
            selectedTargetRows.Clear();
            foreach (var target in modelTargets)
            {
                selectedTargetRows.Add(new TargetAssignmentState(
                    model.Id,
                    model.DisplayName,
                    target,
                    model.IsAssigned(target.Id)));
            }

            return;
        }

        for (var index = 0; index < modelTargets.Length; index++)
        {
            var target = modelTargets[index];
            selectedTargetRows[index].Retarget(
                model.Id,
                model.DisplayName,
                target,
                model.IsAssigned(target.Id));
        }
    }

    private static int TargetDisplayOrder(ProviderAssignmentTargetPresentation target)
    {
        if (target.IsDefault || target.AssignmentState == ProviderTargetAssignmentState.Default)
        {
            return 0;
        }

        if (target.Id.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            || target.DisplayName.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2 + AgentRosterService.ParticipantOrder(target.Id);
    }

    private ProviderAssignmentTargetPresentation TargetPresentationForModel(
        ModelRowState model,
        ProviderAssignmentTargetPresentation target,
        bool selectedIsDefault)
    {
        var isDefaultTarget = target.IsDefault
            || target.AssignmentState == ProviderTargetAssignmentState.Default;
        var explicitlyAssigned = model.IsAssigned(target.Id);
        var explicitlyOwnedByAnotherModel = modelRows.Any(row =>
            !ReferenceEquals(row, model) && row.IsAssigned(target.Id));
        var assignmentState = isDefaultTarget && explicitlyAssigned
            ? ProviderTargetAssignmentState.Default
            : explicitlyAssigned
                ? ProviderTargetAssignmentState.Explicit
                : selectedIsDefault
                    && !isDefaultTarget
                    && !explicitlyOwnedByAnotherModel
                    ? ProviderTargetAssignmentState.InheritsDefault
                    : ProviderTargetAssignmentState.Unassigned;
        var assignedState = isDefaultTarget
            ? ProviderTargetAssignmentState.Default
            : ProviderTargetAssignmentState.Explicit;
        var clearedState = selectedIsDefault
            && !isDefaultTarget
            && !explicitlyOwnedByAnotherModel
            ? ProviderTargetAssignmentState.InheritsDefault
            : ProviderTargetAssignmentState.Unassigned;
        var enabled = target.IsEnabled;
        var help = assignmentState switch
        {
            ProviderTargetAssignmentState.Default =>
                "Turn off this default to leave targets without an explicit assignment unassigned.",
            ProviderTargetAssignmentState.Explicit =>
                "Turn off this explicit model override to use the default again when one is enabled.",
            ProviderTargetAssignmentState.InheritsDefault =>
                "Turn on to preserve this model as an explicit route even if the default changes.",
            _ when isDefaultTarget =>
                "Turn on this model as the default for targets without an explicit assignment.",
            _ => "Turn on an explicit model override for this target."
        };
        return target with
        {
            DisplayName = isDefaultTarget ? "Default" : target.DisplayName,
            HelpText = help,
            IsEnabled = enabled,
            AssignmentState = assignmentState,
            AssignedState = assignedState,
            ClearedState = clearedState,
            IsDefault = isDefaultTarget
        };
    }

    private void ApplyLifecycleAction(ModelRowState model)
    {
        LifecycleProgressBar.Visibility = Visibility.Collapsed;
        var backgroundLifecycle = pendingLifecycle is not null
            && !string.Equals(pendingLifecycle.ModelId, model.Id, StringComparison.Ordinal);
        if (pendingLifecycle is not null && !backgroundLifecycle)
        {
            LifecycleButton.Content = pendingLifecycle.Load ? "Loading…" : "Unloading…";
            AutomationProperties.SetName(
                LifecycleButton,
                pendingLifecycle.Load
                    ? $"Loading {pendingLifecycle.ModelDisplayName} in LM Studio"
                    : $"Unloading {pendingLifecycle.ModelDisplayName} from LM Studio");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"{lastLifecycleMessage} You can continue browsing models while provider-changing controls remain unavailable.");
            AutomationProperties.SetItemStatus(LifecycleButton, "Running");
            LifecycleProgressBar.Visibility = Visibility.Visible;
            SetLifecycleStatus(
                ProviderModelLifecycleActionState.Running,
                $"{lastLifecycleMessage} Request sent; waiting for authoritative LM Studio load-state evidence. Browsing remains available.");
            return;
        }

        if (IsUnconfirmedLifecycle(model))
        {
            LifecycleButton.Content = "Awaiting confirmation";
            LifecycleButton.SetResourceReference(FrameworkElement.StyleProperty, "Arena.Button.Secondary");
            AutomationProperties.SetName(
                LifecycleButton,
                $"Awaiting LM Studio confirmation for {model.DisplayName}");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"{lastLifecycleMessage} Refresh or wait for the next LM Studio residency check. Assignments remain unchanged.");
            AutomationProperties.SetItemStatus(LifecycleButton, "Unconfirmed");
            SetLifecycleStatus(ProviderModelLifecycleActionState.Unconfirmed, lastLifecycleMessage);
            return;
        }

        if (TryLifecycleAction(model, out var load))
        {
            var canRun = currentPresentation?.CanRunLifecycle == true
                && currentPresentation.IsRefreshing == false
                && pendingAssignment is null
                && pendingConfiguration is null
                && pendingConfigurationReload is null;
            LifecycleButton.Content = load ? "Load model" : "Unload model";
            LifecycleButton.SetResourceReference(
                FrameworkElement.StyleProperty,
                load ? "Arena.Button.Primary" : "Arena.Button.Danger");
            AutomationProperties.SetName(
                LifecycleButton,
                load
                    ? $"Load {model.DisplayName} in LM Studio"
                    : $"Unload {model.DisplayName} from LM Studio");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                string.Join(
                    " ",
                    new[]
                    {
                        model.LifecycleHelp,
                        load
                            ? "Request that LM Studio load this model. Assignments remain unchanged."
                            : "Request that LM Studio unload this model. Assignments remain unchanged.",
                        canRun
                            ? ""
                            : currentPresentation?.IsRefreshing == true
                                ? "Wait for provider model evidence to finish refreshing."
                                : "This action is unavailable while the arena or another provider operation is active."
                    }.Where(value => !string.IsNullOrWhiteSpace(value))));
            AutomationProperties.SetItemStatus(LifecycleButton, canRun ? "Ready" : "Unavailable");
        }
        else
        {
            LifecycleButton.Content = "Load state unavailable";
            LifecycleButton.SetResourceReference(FrameworkElement.StyleProperty, "Arena.Button.Secondary");
            AutomationProperties.SetName(LifecycleButton, $"Load state unavailable for {model.DisplayName}");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                string.Join(
                    " ",
                    new[]
                    {
                        model.LifecycleHelp,
                        "LM Studio did not report an actionable loaded or available state for this model."
                    }.Where(value => !string.IsNullOrWhiteSpace(value))));
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
        }

        if (string.Equals(lastLifecycleModelId, model.Id, StringComparison.Ordinal))
        {
            SetLifecycleStatus(lastLifecycleState, lastLifecycleMessage);
        }
        else
        {
            SetLifecycleStatus(
                ProviderModelLifecycleActionState.Ready,
                TryLifecycleAction(model, out _) && currentPresentation?.CanRunLifecycle == true
                    ? "Ready to manage LM Studio residency."
                    : TryLifecycleAction(model, out _)
                        ? "LM Studio residency actions are temporarily unavailable."
                        : "LM Studio load state is unavailable for this model.");
        }

        if (backgroundLifecycle && pendingLifecycle is not null)
        {
            var activity = pendingLifecycle.Load ? "Loading" : "Unloading";
            SetLifecycleStatus(
                ProviderModelLifecycleActionState.Running,
                $"{activity} {pendingLifecycle.ModelDisplayName} in the background. You can inspect this model; provider-changing controls remain unavailable until LM Studio confirms the request.");
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"Wait for the {activity.ToLowerInvariant()} request for {pendingLifecycle.ModelDisplayName} to finish before changing another model.");
        }
    }

    private void RefreshSelectedAssignmentText()
    {
        if (selectedModel is { } model)
        {
            var inheritedTargets = selectedTargetRows
                .Where(target => target.AssignmentState == ProviderTargetAssignmentState.InheritsDefault)
                .Select(target => target.DisplayName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SelectedModelAssignmentsText.Text = inheritedTargets.Length == 0
                ? model.AssignmentSummary
                : $"{model.AssignmentSummary}. Default also covers {string.Join(", ", inheritedTargets)}.";
        }
    }

    private void CancelPendingAssignmentWhenIdentityChanges(
        ProviderModelAssignmentsPresentation presentation)
    {
        var pending = pendingAssignment;
        if (pending is null
            || string.Equals(
                pending.ConnectionIdentity,
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal))
        {
            return;
        }

        // The incoming presentation belongs to a different provider/session
        // identity and is authoritative for that identity. Drop the old overlay;
        // do not project old-session ownership onto the replacement rows.
        pendingAssignment = null;
        lastResolvedModelId = pending.ModelId;
        lastSaveState = ProviderAssignmentSaveState.Failed;
        lastSaveMessage = "Failed: The provider or session changed before the assignment finished.";
    }

    private void CancelPendingLifecycleWhenIdentityChanges(
        ProviderModelAssignmentsPresentation presentation)
    {
        var pending = pendingLifecycle;
        if (pending is null
            || string.Equals(
                pending.ConnectionIdentity,
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal))
        {
            return;
        }

        var mutationStarted = pendingLifecycleMutationStarted;
        pendingLifecycle = null;
        pendingLifecycleMutationStarted = false;
        lastLifecycleModelId = pending.ModelId;
        lastLifecycleState = mutationStarted
            ? ProviderModelLifecycleActionState.Unconfirmed
            : ProviderModelLifecycleActionState.Failed;
        lastLifecycleMessage = mutationStarted
            ? "The provider or session changed after the lifecycle request started. The LM Studio load state is unknown; refresh the original connection to verify it."
            : "Failed: The provider or session changed before the lifecycle request finished.";
        lastLifecycleDesiredLoaded = mutationStarted
            ? pending.Load
            : null;
        lastLifecycleConnectionIdentity = mutationStarted
            ? pending.ConnectionIdentity
            : "";
        unconfirmedLifecycleReceipt = mutationStarted
            ? CreateUnconfirmedLifecycleReceipt(pending)
            : null;
        lifecycleReceiptOrphanedByConnectionChange = mutationStarted;
        // A replacement session can legitimately project an empty catalog. In that
        // case RebuildSelectedDetail has no row from which to refresh the status card,
        // so publish the causal terminal state here instead of leaving stale Running
        // evidence visible until another model is selected.
        SetLifecycleStatus(lastLifecycleState, lastLifecycleMessage);
        ApplyLifecycleActivityMarkers();
    }

    private void CancelPendingConfigurationWhenIdentityChanges(
        ProviderModelAssignmentsPresentation presentation)
    {
        var pending = pendingConfiguration;
        if (pending is null
            || string.Equals(
                pending.ConnectionIdentity,
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal))
        {
            return;
        }

        pendingConfiguration = null;
        lastConfigurationModelId = pending.ModelId;
        lastConfigurationState = ProviderModelConfigurationSaveState.Failed;
        lastConfigurationMessage = "Failed: The provider or session changed before the model configuration finished saving.";
        SetConfigurationStatus(lastConfigurationState, lastConfigurationMessage);
    }

    private void CancelPendingConfigurationReloadWhenIdentityChanges(
        ProviderModelAssignmentsPresentation presentation)
    {
        var pending = pendingConfigurationReload;
        if (pending is null
            || string.Equals(
                pending.ConnectionIdentity,
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal))
        {
            return;
        }

        var mutationStarted = pendingConfigurationReloadMutationStarted;
        pendingConfigurationReload = null;
        pendingConfigurationReloadMutationStarted = false;
        lastReloadModelId = pending.ModelId;
        lastReloadState = mutationStarted
            ? ProviderModelConfigurationReloadState.Unconfirmed
            : ProviderModelConfigurationReloadState.Failed;
        lastReloadConnectionIdentity = mutationStarted ? pending.ConnectionIdentity : "";
        lastReloadMessage = mutationStarted
            ? "The provider or session changed after the reload request started. Effective context is unconfirmed; refresh the original connection."
            : "Failed: The provider or session changed before a configuration reload request was sent.";
        SetConfigurationReloadStatus(FindModel(pending.ModelId), lastReloadState, lastReloadMessage);
    }

    private void ReconcileUnconfirmedConfigurationReload(
        ProviderModelAssignmentsPresentation presentation)
    {
        var hasUnresolvedReloadReceipt = lastReloadState == ProviderModelConfigurationReloadState.Unconfirmed
            || (lastReloadState == ProviderModelConfigurationReloadState.Failed
                && lastReloadConnectionIdentity.Length > 0);
        if (!hasUnresolvedReloadReceipt
            || lastReloadConnectionIdentity.Length == 0
            || !lastReloadConnectionIdentity.Equals(
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal)
            || FindModel(lastReloadModelId) is not { } model)
        {
            return;
        }

        if (model.Availability == ProviderModelAvailability.Loaded)
        {
            lastReloadState = model.Configuration.RequiresReload
                ? ProviderModelConfigurationReloadState.Required
                : ProviderModelConfigurationReloadState.Succeeded;
            lastReloadConnectionIdentity = "";
            lastReloadMessage = model.Configuration.RequiresReload
                ? "Provider evidence confirms the model is loaded, but the desired context is not active. Retry reload."
                : "Provider evidence now confirms the configured model residency.";
        }
        else if (model.Availability == ProviderModelAvailability.Available)
        {
            lastReloadState = ProviderModelConfigurationReloadState.Failed;
            lastReloadMessage = "Failed: Provider evidence confirms the prior instance was unloaded and no replacement is loaded. Use Load model to retry.";
        }
        else
        {
            return;
        }

    }

    private void CaptureLatestPendingConfigurationAuthority(
        IReadOnlyList<ProviderModelAssignmentPresentation> presentedModels)
    {
        var pending = pendingConfiguration;
        if (pending is null)
        {
            return;
        }

        var authority = presentedModels.FirstOrDefault(model =>
            string.Equals(model.Id, pending.ModelId, StringComparison.Ordinal));
        if (authority?.Configuration is not null)
        {
            pendingConfiguration = pending with
            {
                Previous = NormalizeConfiguration(authority.Configuration)
            };
        }
    }

    private void ApplyPendingConfigurationDraft()
    {
        if (pendingConfiguration is { } pending)
        {
            FindModel(pending.ModelId)?.SetConfiguration(pending.Requested);
        }
    }

    private void ClearUnconfirmedLifecycleForConnectionChange(
        ProviderModelAssignmentsPresentation presentation)
    {
        if (lastLifecycleState != ProviderModelLifecycleActionState.Unconfirmed
            || !lastLifecycleDesiredLoaded.HasValue
            || string.Equals(
                lastLifecycleConnectionIdentity,
                EffectiveConnectionIdentity(presentation),
                StringComparison.Ordinal))
        {
            return;
        }

        if (lifecycleReceiptOrphanedByConnectionChange)
        {
            return;
        }

        lastLifecycleModelId = "";
        lastLifecycleState = ProviderModelLifecycleActionState.Ready;
        lastLifecycleMessage = "LM Studio residency evidence changed with the provider connection.";
        lastLifecycleDesiredLoaded = null;
        lastLifecycleConnectionIdentity = "";
        unconfirmedLifecycleReceipt = null;
        lifecycleReceiptOrphanedByConnectionChange = false;
    }

    private ProviderModelAssignmentPresentation CreateUnconfirmedLifecycleReceipt(
        PendingLifecycle pending)
    {
        var current = FindModel(pending.ModelId);
        var desiredState = pending.Load ? "loaded" : "unloaded";
        return new ProviderModelAssignmentPresentation(
            pending.ModelId,
            pending.ModelDisplayName,
            ProviderModelAvailability.Unavailable,
            "Awaiting confirmation",
            current?.Metadata ?? "Provider model metadata unavailable.",
            current?.AssignedTargetIds ?? [],
            $"Lifecycle request receipt awaiting authoritative LM Studio evidence. {current?.AutomationHelp}",
            CanLoad: false,
            CanUnload: false,
            LifecycleHelp: $"LM Studio has not yet confirmed that this model is {desiredState}. Refresh or wait for the next residency check.",
            IsResidencyStale: true,
            Configuration: current?.Configuration);
    }

    private void EnsureUnconfirmedLifecycleReceiptRow()
    {
        if (lastLifecycleState != ProviderModelLifecycleActionState.Unconfirmed
            || !lastLifecycleDesiredLoaded.HasValue
            || unconfirmedLifecycleReceipt is null
            || !LifecycleReceiptMatchesConnection(currentPresentation)
            || FindModel(unconfirmedLifecycleReceipt.Id) is not null)
        {
            return;
        }

        var row = new ModelRowState(unconfirmedLifecycleReceipt);
        row.RefreshAssignmentSummary(targetsById);
        modelRows.Add(row);
        loadedRowsView.Refresh();
        modelRowsView.Refresh();
        SelectModel(row);
    }

    private void ReconcileUnconfirmedLifecycle(
        ProviderModelAssignmentsPresentation presentation)
    {
        if (lastLifecycleState != ProviderModelLifecycleActionState.Unconfirmed
            || lastLifecycleDesiredLoaded is not bool desiredLoaded
            || !LifecycleReceiptMatchesConnection(presentation))
        {
            return;
        }

        if (presentation.IsRefreshing || FindModel(lastLifecycleModelId) is not { } model)
        {
            return;
        }

        var confirmed = !model.IsResidencyStale
            && (desiredLoaded
                ? model.Availability == ProviderModelAvailability.Loaded
                : model.Availability == ProviderModelAvailability.Available);
        if (confirmed)
        {
            lastLifecycleState = ProviderModelLifecycleActionState.Succeeded;
            lastLifecycleMessage = desiredLoaded
                ? $"LM Studio now confirms {model.DisplayName} is loaded."
                : $"LM Studio now confirms {model.DisplayName} is unloaded.";
            lastLifecycleDesiredLoaded = null;
            lastLifecycleConnectionIdentity = "";
            unconfirmedLifecycleReceipt = null;
            lifecycleReceiptOrphanedByConnectionChange = false;
            return;
        }

        var oppositeObserved = !model.IsResidencyStale
            && (desiredLoaded
                ? model.Availability == ProviderModelAvailability.Available
                : model.Availability == ProviderModelAvailability.Loaded);
        if (!oppositeObserved)
        {
            return;
        }

        lastLifecycleState = ProviderModelLifecycleActionState.Unconfirmed;
        lastLifecycleMessage = desiredLoaded
            ? $"Request not yet confirmed. LM Studio still reports {model.DisplayName} is unloaded. Retry Load or refresh."
            : $"Request not yet confirmed. LM Studio still reports {model.DisplayName} is loaded. Retry Unload or refresh.";
        unconfirmedLifecycleReceipt = null;
        lifecycleReceiptOrphanedByConnectionChange = false;
    }

    private bool IsUnconfirmedLifecycle(ModelRowState model) =>
        lastLifecycleState == ProviderModelLifecycleActionState.Unconfirmed
        && lastLifecycleDesiredLoaded.HasValue
        && unconfirmedLifecycleReceipt is not null
        && LifecycleReceiptMatchesConnection(currentPresentation)
        && string.Equals(lastLifecycleModelId, model.Id, StringComparison.Ordinal);

    private bool LifecycleReceiptMatchesConnection(
        ProviderModelAssignmentsPresentation? presentation) =>
        !string.IsNullOrWhiteSpace(lastLifecycleConnectionIdentity)
        && string.Equals(
            lastLifecycleConnectionIdentity,
            EffectiveConnectionIdentity(presentation),
            StringComparison.Ordinal);

    private void ApplyInteractionState()
    {
        var canAssign = currentPresentation?.CanAssign == true
            && currentPresentation.IsRefreshing == false
            && pendingAssignment is null
            && pendingLifecycle is null
            && pendingConfiguration is null
            && pendingConfigurationReload is null;
        foreach (var target in selectedTargetRows)
        {
            target.SetInteractionEnabled(canAssign, canAssign ? "" : AssignmentAvailabilityMessage());
            target.SetSaving(
                pendingAssignment is not null
                && string.Equals(target.ModelId, pendingAssignment.ModelId, StringComparison.Ordinal)
                && string.Equals(target.TargetId, pendingAssignment.TargetId, StringComparison.Ordinal));
        }

        var hasPendingChange = pendingAssignment is not null
            || pendingLifecycle is not null
            || pendingConfiguration is not null
            || pendingConfigurationReload is not null;
        // Keep catalog inspection and scrolling available while a save or LM Studio
        // lifecycle request is running. Only controls that can start a conflicting
        // mutation are disabled below.
        ModelsList.IsEnabled = true;
        LoadedModelsList.IsEnabled = true;
        AvailableCatalogExpander.IsEnabled = true;
        ModelSearchText.IsEnabled = true;
        ModelFacetCombo.IsEnabled = true;
        ClearSearchButton.IsEnabled = true;
        RefreshButton.IsEnabled = currentPresentation?.CanRefresh == true
            && currentPresentation.IsRefreshing == false
            && !hasPendingChange;
        LifecycleButton.IsEnabled = !hasPendingChange
            && currentPresentation?.CanRunLifecycle == true
            && currentPresentation?.IsRefreshing == false
            && selectedModel is { } model
            && !IsUnconfirmedLifecycle(model)
            && TryLifecycleAction(model, out _);
        EmptyRefreshButton.IsEnabled = RefreshButton.IsEnabled;
        EmptyConnectionButton.IsEnabled = ConnectionButton.IsEnabled;
        EmptyClearFiltersButton.IsEnabled = true;
        if (pendingAssignment is not null)
        {
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"Wait for the assignment for {pendingAssignment.ModelDisplayName} to finish before changing LM Studio residency.");
        }
        else if (pendingConfiguration is not null)
        {
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"Wait for the configuration for {pendingConfiguration.ModelDisplayName} to finish saving before changing LM Studio residency.");
        }
        else if (pendingConfigurationReload is not null)
        {
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"Wait for the configuration reload for {pendingConfigurationReload.ModelDisplayName} to finish before changing LM Studio residency.");
        }
    }

    private static string PendingAssignmentMessage(PendingAssignment pending, string? message)
    {
        if (pending.IsTransfer)
        {
            return AssignmentProgressMessage(pending);
        }

        var progress = DisplayOrFallback(message, $"Saving assignment for {pending.ModelDisplayName}…");
        return progress.Contains(pending.ModelDisplayName, StringComparison.OrdinalIgnoreCase)
            ? progress
            : $"{progress} Model: {pending.ModelDisplayName}.";
    }

    private static string AssignmentProgressMessage(PendingAssignment pending)
    {
        if (!pending.IsTransfer)
        {
            return $"Saving assignment for {pending.ModelDisplayName}…";
        }

        var priorModels = JoinDisplayNames(pending.ReplacedModelDisplayNames);
        var verb = pending.IsDefaultTarget ? "Changing" : "Moving";
        return $"{verb} {pending.TargetDisplayName} from {priorModels} to {pending.ModelDisplayName}…";
    }

    private static string JoinDisplayNames(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "another model",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };

    private static string PendingLifecycleMessage(PendingLifecycle pending, string? message)
    {
        var progress = DisplayOrFallback(
            message,
            pending.Load
                ? $"Loading {pending.ModelDisplayName}…"
                : $"Unloading {pending.ModelDisplayName}…");
        return progress.Contains(pending.ModelDisplayName, StringComparison.OrdinalIgnoreCase)
            ? progress
            : $"{progress} Model: {pending.ModelDisplayName}.";
    }

    private void ApplyConnectionState(ProviderConnectionState state)
    {
        var borderKey = state switch
        {
            ProviderConnectionState.Online => "Arena.Brush.Success",
            ProviderConnectionState.Partial => "Arena.Brush.Warning",
            ProviderConnectionState.Checking => "Arena.Brush.Info",
            ProviderConnectionState.Offline or ProviderConnectionState.Failed => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var textKey = state switch
        {
            ProviderConnectionState.Online => "Arena.Brush.Success",
            ProviderConnectionState.Partial => "Arena.Brush.Warning",
            ProviderConnectionState.Checking => "Arena.Brush.Info",
            ProviderConnectionState.Offline or ProviderConnectionState.Failed => "DangerTextBrush",
            _ => "MutedTextBrush"
        };
        ConnectionStatusChip.SetResourceReference(Border.BorderBrushProperty, borderKey);
        ConnectionStatusText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
        AutomationProperties.SetItemStatus(ConnectionStatusChip, state.ToString());
        AutomationProperties.SetHelpText(
            ConnectionStatusChip,
            $"{ProviderNameText.Text}: {ConnectionStatusText.Text}");
    }

    private void SetAssignmentStatus(ProviderAssignmentSaveState state, string message)
    {
        var safeMessage = NormalizeStatus(message);
        AssignmentSaveStatusText.Text = safeMessage;
        AutomationProperties.SetItemStatus(AssignmentSaveStatusText, state.ToString());
        AutomationProperties.SetHelpText(AssignmentSaveStatusText, safeMessage);
        var borderKey = state switch
        {
            ProviderAssignmentSaveState.Saving => "Arena.Brush.Info",
            ProviderAssignmentSaveState.Saved => "Arena.Brush.Success",
            ProviderAssignmentSaveState.Failed => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var textKey = state switch
        {
            ProviderAssignmentSaveState.Saving => "Arena.Brush.Info",
            ProviderAssignmentSaveState.Saved => "Arena.Brush.Success",
            ProviderAssignmentSaveState.Failed => "DangerTextBrush",
            _ => "MutedTextBrush"
        };
        AssignmentSaveStatusCard.SetResourceReference(Border.BorderBrushProperty, borderKey);
        AssignmentSaveStatusText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
    }

    private void SetLifecycleStatus(ProviderModelLifecycleActionState state, string message)
    {
        var safeMessage = NormalizeStatus(message);
        LifecycleStatusText.Text = safeMessage;
        AutomationProperties.SetItemStatus(LifecycleStatusText, state.ToString());
        AutomationProperties.SetHelpText(LifecycleStatusText, safeMessage);
        var borderKey = state switch
        {
            ProviderModelLifecycleActionState.Running => "Arena.Brush.Info",
            ProviderModelLifecycleActionState.Unconfirmed => "Arena.Brush.Warning",
            ProviderModelLifecycleActionState.Succeeded => "Arena.Brush.Success",
            ProviderModelLifecycleActionState.Failed => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var textKey = state switch
        {
            ProviderModelLifecycleActionState.Running => "Arena.Brush.Info",
            ProviderModelLifecycleActionState.Unconfirmed => "Arena.Brush.Warning",
            ProviderModelLifecycleActionState.Succeeded => "Arena.Brush.Success",
            ProviderModelLifecycleActionState.Failed => "DangerTextBrush",
            _ => "MutedTextBrush"
        };
        LifecycleStatusCard.SetResourceReference(Border.BorderBrushProperty, borderKey);
        LifecycleStatusText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
    }

    private static bool TryLifecycleAction(ModelRowState model, out bool load)
    {
        if (model.Availability == ProviderModelAvailability.Loaded && model.CanUnload)
        {
            load = false;
            return true;
        }

        if (model.Availability == ProviderModelAvailability.Available && model.CanLoad)
        {
            load = true;
            return true;
        }

        load = false;
        return false;
    }

    private bool MatchesCurrentSearch(object item)
    {
        if (item is not ModelRowState model
            || model.Availability == ProviderModelAvailability.Loaded)
        {
            return false;
        }

        var facetMatch = selectedFacet switch
        {
            ProviderModelFacet.Available => model.Availability == ProviderModelAvailability.Available,
            ProviderModelFacet.Assigned => model.AssignedTargetIds.Count > 0,
            ProviderModelFacet.Unassigned => model.AssignedTargetIds.Count == 0,
            ProviderModelFacet.Unavailable => model.Availability == ProviderModelAvailability.Unavailable,
            _ => true
        };
        return facetMatch
            && (committedSearchQuery.Length == 0
                || model.SearchText.Contains(committedSearchQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoadedModel(object item) =>
        item is ModelRowState { Availability: ProviderModelAvailability.Loaded };

    private IEnumerable<ModelRowState> LoadedRows() =>
        loadedRowsView.Cast<object>().OfType<ModelRowState>();

    private IEnumerable<ModelRowState> VisibleCatalogRows() =>
        modelRowsView.Cast<object>().OfType<ModelRowState>();

    private IEnumerable<ModelRowState> SelectableRows() =>
        LoadedRows().Concat(VisibleCatalogRows());

    private void SelectModel(ModelRowState? model)
    {
        selectedModel = model;
        SynchronizeSelection(model);
    }

    private void SynchronizeSelection(ModelRowState? model)
    {
        synchronizingSelection = true;
        try
        {
            LoadedModelsList.SelectedItem = model?.Availability == ProviderModelAvailability.Loaded
                ? model
                : null;
            ModelsList.SelectedItem = model is not null
                && model.Availability != ProviderModelAvailability.Loaded
                && VisibleCatalogRows().Contains(model)
                    ? model
                    : null;
        }
        finally
        {
            synchronizingSelection = false;
        }
    }

    private bool MoveKeyboardSelection(ModelRowState model, ListBox destination)
    {
        SelectModel(model);
        destination.ScrollIntoView(model);
        destination.UpdateLayout();
        destination.Focus();
        return destination.ItemContainerGenerator.ContainerFromItem(model) is ListBoxItem item
            ? item.Focus()
            : destination.IsKeyboardFocusWithin;
    }

    private ModelRowState? FindModel(string modelId) =>
        modelRows.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    private ModelRowState? ResolveModel(string modelId)
    {
        var exact = modelRows.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var display = modelRows
            .Where(model => string.Equals(model.DisplayName, modelId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (display.Length == 1)
        {
            return display[0];
        }

        var leaf = ModelIdentityLeaf(modelId);
        var aliases = modelRows
            .Where(model => string.Equals(ModelIdentityLeaf(model.Id), leaf, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ModelIdentityLeaf(model.DisplayName), leaf, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private static string ModelIdentityLeaf(string value)
    {
        var normalized = (value ?? "").Trim().Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private void UpdateEmptyState()
    {
        var hasVisibleModels = VisibleCatalogRows().Any();
        ModelsEmptyState.Visibility = hasVisibleModels ? Visibility.Collapsed : Visibility.Visible;
        if (hasVisibleModels)
        {
            return;
        }

        var query = committedSearchQuery;
        var catalogModelCount = modelRows.Count(model =>
            model.Availability != ProviderModelAvailability.Loaded);
        ModelsEmptyStateText.Text = catalogModelCount == 0
            ? "Refresh the provider model list or review the connection."
            : query.Length > 0
                ? $"No models match “{NormalizeStatus(query)}” in {SelectedFacetName().ToLowerInvariant()}."
                : $"No models match {SelectedFacetName().ToLowerInvariant()}.";
        AutomationProperties.SetHelpText(ModelsEmptyState, ModelsEmptyStateText.Text);
        EmptyClearFiltersButton.Visibility = catalogModelCount > 0
            && (committedSearchQuery.Length > 0 || selectedFacet != ProviderModelFacet.All)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ReconcileModelRows(IReadOnlyList<ProviderModelAssignmentPresentation> presentedModels)
    {
        var desiredIds = presentedModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        if (modelRows.Count == 0 && presentedModels.Count > 1)
        {
            // WPF forbids mutating a collection while its ListCollectionView is inside
            // DeferRefresh. Strip the expensive view work first, populate the empty keyed
            // source, then restore filtering/grouping/sorting in one deferred refresh.
            // Subsequent refreshes use the keyed live-shaping path so existing containers and
            // focus remain intact.
            LoadedModelsList.ItemsSource = null;
            ModelsList.ItemsSource = null;
            try
            {
                using (loadedRowsView.DeferRefresh())
                {
                    loadedRowsView.Filter = null;
                    loadedRowsView.SortDescriptions.Clear();
                }
                using (modelRowsView.DeferRefresh())
                {
                    modelRowsView.Filter = null;
                    modelRowsView.SortDescriptions.Clear();
                    modelRowsView.GroupDescriptions.Clear();
                }

                ReconcileModelRowsCore(presentedModels, desiredIds);

                using (loadedRowsView.DeferRefresh())
                {
                    loadedRowsView.Filter = IsLoadedModel;
                    loadedRowsView.SortDescriptions.Add(
                        new SortDescription(nameof(ModelRowState.DisplayName), ListSortDirection.Ascending));
                    loadedRowsView.SortDescriptions.Add(
                        new SortDescription(nameof(ModelRowState.Id), ListSortDirection.Ascending));
                }
                using (modelRowsView.DeferRefresh())
                {
                    modelRowsView.Filter = MatchesCurrentSearch;
                    modelRowsView.SortDescriptions.Add(
                        new SortDescription(nameof(ModelRowState.GroupOrder), ListSortDirection.Ascending));
                    modelRowsView.SortDescriptions.Add(
                        new SortDescription(nameof(ModelRowState.DisplayName), ListSortDirection.Ascending));
                    modelRowsView.SortDescriptions.Add(
                        new SortDescription(nameof(ModelRowState.Id), ListSortDirection.Ascending));
                    modelRowsView.GroupDescriptions.Add(
                        new PropertyGroupDescription(nameof(ModelRowState.GroupName)));
                }
            }
            finally
            {
                LoadedModelsList.ItemsSource = loadedRowsView;
                ModelsList.ItemsSource = modelRowsView;
            }

            return;
        }

        ReconcileModelRowsCore(presentedModels, desiredIds);
    }

    private void ReconcileModelRowsCore(
        IReadOnlyList<ProviderModelAssignmentPresentation> presentedModels,
        IReadOnlySet<string> desiredIds)
    {
        var existingById = modelRows.ToDictionary(model => model.Id, StringComparer.Ordinal);
        foreach (var stale in modelRows.Where(model => !desiredIds.Contains(model.Id)).ToArray())
        {
            modelRows.Remove(stale);
        }

        foreach (var presented in presentedModels)
        {
            var existing = existingById.TryGetValue(presented.Id, out var row);
            var rowChangedViewKeys = false;
            var availabilityChanged = false;
            if (!existing)
            {
                row = new ModelRowState(presented);
                modelRows.Add(row);
            }
            else
            {
                var previousAvailability = row!.Availability;
                rowChangedViewKeys = row!.UpdateFrom(presented);
                availabilityChanged = previousAvailability != row.Availability;
            }

            if (pendingAssignment is { } pending)
            {
                var optimisticValue = pending.RequestedValue
                    ? string.Equals(row!.Id, pending.ModelId, StringComparison.Ordinal)
                    : row!.IsAssigned(pending.TargetId);
                if (!pending.RequestedValue
                    && string.Equals(row.Id, pending.ModelId, StringComparison.Ordinal))
                {
                    optimisticValue = false;
                }

                if (row.IsAssigned(pending.TargetId) != optimisticValue)
                {
                    row.SetAssigned(pending.TargetId, optimisticValue);
                    rowChangedViewKeys = true;
                }
            }

            rowChangedViewKeys |= row!.RefreshAssignmentSummary(targetsById);
            if (existing
                && (availabilityChanged
                    || (rowChangedViewKeys && !usesIncrementalLiveShaping)))
            {
                // Live filtering schedules cross-view moves at DataBind priority. A
                // source remove/add keeps the same row object but makes a confirmed
                // Loaded <-> catalog transfer visible synchronously to selection,
                // status, and callers before ApplyPresentation returns.
                modelRows.Remove(row);
                modelRows.Add(row);
            }

        }
    }

    private void ScheduleCatalogContinuity(
        string requestedSelection,
        double priorCatalogOffset,
        CatalogViewportAnchor? priorViewportAnchor,
        bool restoreKeyboardFocus,
        int continuityGeneration)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (continuityGeneration != catalogContinuityGeneration || !IsLoaded || !IsVisible)
                {
                    return;
                }

                var requestedRow = SelectableRows().FirstOrDefault(row => string.Equals(
                    row.Id,
                    requestedSelection,
                    StringComparison.Ordinal));
                if (requestedRow is not null && !ReferenceEquals(selectedModel, requestedRow))
                {
                    SelectModel(requestedRow);
                    RebuildSelectedDetail();
                }

                if (restoreKeyboardFocus)
                {
                    FocusCatalog();
                }

                if (selectedModel?.Availability == ProviderModelAvailability.Loaded)
                {
                    LoadedModelsList.ScrollIntoView(selectedModel);
                    LoadedModelsList.UpdateLayout();
                }
                else
                {
                    ModelsList.UpdateLayout();
                    if (FindVisualDescendant<ScrollViewer>(ModelsList) is { } scrollViewer)
                    {
                        if (!RestoreCatalogViewportAnchor(scrollViewer, priorViewportAnchor)
                            && priorCatalogOffset > 0)
                        {
                            scrollViewer.ScrollToVerticalOffset(
                                Math.Min(priorCatalogOffset, scrollViewer.ScrollableHeight));
                        }
                    }
                }
            }));
    }

    private CatalogViewportAnchor? CaptureCatalogViewportAnchor(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
        {
            return null;
        }

        CatalogViewportAnchor? anchor = null;
        foreach (var row in modelRows)
        {
            if (ModelsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container
                || !container.IsVisible)
            {
                continue;
            }

            try
            {
                var y = container.TransformToAncestor(scrollViewer).Transform(new Point()).Y;
                if (y + container.ActualHeight <= 0 || y >= scrollViewer.ViewportHeight)
                {
                    continue;
                }

                if (anchor is null || y < anchor.RelativeY)
                {
                    anchor = new CatalogViewportAnchor(row.Id, y);
                }
            }
            catch (InvalidOperationException)
            {
                // A recycled container can detach between lookup and transform.
            }
        }

        return anchor;
    }

    private bool RestoreCatalogViewportAnchor(
        ScrollViewer scrollViewer,
        CatalogViewportAnchor? anchor)
    {
        if (anchor is null || FindModel(anchor.ModelId) is not { } row)
        {
            return false;
        }

        ModelsList.ScrollIntoView(row);
        ModelsList.UpdateLayout();
        if (ModelsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container)
        {
            return false;
        }

        try
        {
            var currentY = container.TransformToAncestor(scrollViewer).Transform(new Point()).Y;
            var targetOffset = Math.Clamp(
                scrollViewer.VerticalOffset + currentY - anchor.RelativeY,
                0,
                scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(targetOffset);
            ModelsList.UpdateLayout();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ApplyLifecycleActivityMarkers()
    {
        foreach (var row in modelRows)
        {
            row.SetLifecycleActivity("");
        }

        if (pendingLifecycle is not null)
        {
            FindModel(pendingLifecycle.ModelId)?.SetLifecycleActivity(
                pendingLifecycle.Load ? "Loading…" : "Unloading…");
        }
        else if (lastLifecycleState == ProviderModelLifecycleActionState.Unconfirmed
                 && unconfirmedLifecycleReceipt is not null
                 && LifecycleReceiptMatchesConnection(currentPresentation))
        {
            FindModel(lastLifecycleModelId)?.SetLifecycleActivity("Awaiting confirmation");
        }

    }

    private static bool ConfigureIncrementalLiveShaping(ListCollectionView view)
    {
        if (view is not ICollectionViewLiveShaping live
            || !live.CanChangeLiveSorting
            || !live.CanChangeLiveGrouping
            || !live.CanChangeLiveFiltering)
        {
            return false;
        }

        live.LiveSortingProperties.Add(nameof(ModelRowState.GroupOrder));
        live.LiveSortingProperties.Add(nameof(ModelRowState.DisplayName));
        live.LiveSortingProperties.Add(nameof(ModelRowState.Id));
        live.LiveGroupingProperties.Add(nameof(ModelRowState.GroupName));
        live.LiveFilteringProperties.Add(nameof(ModelRowState.SearchText));
        live.LiveFilteringProperties.Add(nameof(ModelRowState.Availability));
        live.LiveFilteringProperties.Add(nameof(ModelRowState.AssignedTargetIds));
        live.IsLiveSorting = true;
        live.IsLiveGrouping = true;
        live.IsLiveFiltering = true;
        return live.IsLiveSorting == true
            && live.IsLiveGrouping == true
            && live.IsLiveFiltering == true;
    }

    private void UpdateCatalogHealth(ProviderModelAssignmentsPresentation presentation)
    {
        var stale = presentation.Models.Any(model => model.IsResidencyStale);
        var exceptional = presentation.IsRefreshing
            || presentation.ConnectionState != ProviderConnectionState.Online
            || stale;
        CatalogAlertBorder.Visibility = exceptional ? Visibility.Visible : Visibility.Collapsed;
        var borderKey = presentation.ConnectionState switch
        {
            ProviderConnectionState.Checking => "Arena.Brush.Info",
            ProviderConnectionState.Online when stale => "Arena.Brush.Warning",
            ProviderConnectionState.Partial => "Arena.Brush.Warning",
            ProviderConnectionState.Online => "DisabledBorderBrush",
            _ => "DangerBorderBrush"
        };
        var textKey = presentation.ConnectionState switch
        {
            ProviderConnectionState.Checking => "Arena.Brush.Info",
            ProviderConnectionState.Online when stale => "Arena.Brush.Warning",
            ProviderConnectionState.Partial => "Arena.Brush.Warning",
            ProviderConnectionState.Online => "MutedTextBrush",
            _ => "DangerTextBrush"
        };
        CatalogAlertBorder.SetResourceReference(Border.BorderBrushProperty, borderKey);
        CatalogStatusText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
        AutomationProperties.SetItemStatus(
            CatalogStatusText,
            presentation.IsRefreshing ? "Refreshing" : stale ? "Stale" : presentation.ConnectionState.ToString());
        AutomationProperties.SetHelpText(CatalogSummaryText, CatalogStatusText.Text);
    }

    private void UpdateCatalogSummary()
    {
        var loaded = modelRows.Count(model => model.Availability == ProviderModelAvailability.Loaded);
        var available = modelRows.Count(model => model.Availability == ProviderModelAvailability.Available);
        var unavailable = modelRows.Count - loaded - available;
        CatalogSummaryText.Text = unavailable > 0
            ? $"{loaded} loaded · {available} available · {unavailable} state unknown"
            : $"{loaded} loaded · {available} available";
        AutomationProperties.SetItemStatus(CatalogSummaryText, $"{modelRows.Count} total models");
        LoadedModelsCountText.Text = loaded.ToString();
        AvailableCatalogCountText.Text = (available + unavailable).ToString();
        LoadedModelsEmptyState.Visibility = loaded == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadedModelsList.Visibility = loaded == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomationProperties.SetItemStatus(LoadedModelsCountText, $"{loaded} loaded models");
        AutomationProperties.SetItemStatus(
            AvailableCatalogCountText,
            $"{available + unavailable} catalog models");
    }

    private void UpdateSearchChrome()
    {
        var hasQuery = !string.IsNullOrEmpty(ModelSearchText.Text);
        SearchWatermarkText.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSearchResults()
    {
        var shown = VisibleCatalogRows().Count();
        var catalogCount = modelRows.Count(model =>
            model.Availability != ProviderModelAvailability.Loaded);
        SearchResultsText.Text = shown == catalogCount
            ? $"{shown} catalog models"
            : $"{shown} of {catalogCount}";
        AutomationProperties.SetItemStatus(
            SearchResultsText,
            shown == catalogCount ? "All catalog models shown" : "Filtered catalog results");
    }

    private string AssignmentAvailabilityMessage()
    {
        if (pendingAssignment is not null)
        {
            return $"{AssignmentProgressMessage(pendingAssignment)} Other routing changes wait until this save finishes.";
        }

        if (pendingLifecycle is not null)
        {
            var activity = pendingLifecycle.Load ? "loads" : "unloads";
            return $"Assignments are paused while LM Studio {activity} {pendingLifecycle.ModelDisplayName}. You can keep browsing models.";
        }

        if (pendingConfiguration is not null)
        {
            return $"Assignments are paused while the configuration for {pendingConfiguration.ModelDisplayName} saves. You can keep browsing models.";
        }

        if (pendingConfigurationReload is not null)
        {
            return $"Assignments are paused while {pendingConfigurationReload.ModelDisplayName} reloads. You can keep browsing models.";
        }

        if (currentPresentation?.IsRefreshing == true)
        {
            return "Assignments are paused while provider evidence refreshes.";
        }

        if (currentPresentation?.CanAssign != true)
        {
            return "Assignments are unavailable for the current provider or arena state.";
        }

        return "Changes save immediately. Turn routing targets on or off without changing model residency.";
    }

    private string SelectedFacetName() =>
        (ModelFacetCombo.SelectedItem as ModelFacetOption)?.DisplayName ?? "All catalog";

    private static IReadOnlyList<ProviderAssignmentTargetPresentation> DistinctTargets(
        IEnumerable<ProviderAssignmentTargetPresentation>? values)
    {
        return (values ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.Id))
            .GroupBy(target => target.Id.Trim(), StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                Id = group.Key,
                DisplayName = DisplayOrFallback(group.First().DisplayName, group.Key)
            })
            .OrderBy(TargetDisplayOrder)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ProviderModelAssignmentPresentation> DistinctModels(
        IEnumerable<ProviderModelAssignmentPresentation>? values)
    {
        return (values ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id.Trim(), StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                Id = group.Key,
                DisplayName = DisplayOrFallback(group.First().DisplayName, group.Key),
                AssignedTargetIds = group.First().AssignedTargetIds ?? []
            })
            .ToArray();
    }

    private static string DisplayOrFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ProviderModelConfigurationPresentation UnavailableConfiguration() => new(
        CanEdit: false,
        Status: "Model configuration evidence is unavailable.");

    private static ProviderModelConfigurationPresentation NormalizeConfiguration(
        ProviderModelConfigurationPresentation source)
    {
        var minimum = Math.Clamp(source.MinimumContextWindow, 512, 1_048_576);
        var maximum = Math.Clamp(source.MaximumContextWindow, minimum, 1_048_576);
        var contextWindow = source.ContextWindow == 0
            ? 0
            : Math.Clamp(source.ContextWindow, minimum, maximum);
        return source with
        {
            ContextWindow = contextWindow,
            EffectiveContextWindow = Math.Max(0, source.EffectiveContextWindow),
            ContextEvidence = NormalizeStatus(source.ContextEvidence),
            CustomTone = (source.CustomTone ?? "").Trim() is { Length: > 240 } custom
                ? custom[..240]
                : (source.CustomTone ?? "").Trim(),
            ConfigurationIdentity = (source.ConfigurationIdentity ?? "").Trim(),
            Status = NormalizeStatus(source.Status),
            MinimumContextWindow = minimum,
            MaximumContextWindow = maximum,
            ChapteredAvailable = source.ChapteredAvailable,
            HistoryPolicy = source.HistoryPolicy == ProviderModelHistoryPolicy.Chaptered && !source.ChapteredAvailable
                ? ProviderModelHistoryPolicy.Strict
                : source.HistoryPolicy
        };
    }

    private static string EffectivePresentationIdentity(
        ProviderModelAssignmentsPresentation? presentation)
    {
        if (presentation is null)
        {
            return "provider:unavailable";
        }

        return string.IsNullOrWhiteSpace(presentation.PresentationIdentity)
            ? $"provider:{DisplayOrFallback(presentation.ProviderName, "unavailable")}"
            : presentation.PresentationIdentity.Trim();
    }

    private static string EffectiveConnectionIdentity(
        ProviderModelAssignmentsPresentation? presentation)
    {
        if (presentation is null)
        {
            return "connection:unavailable";
        }

        return string.IsNullOrWhiteSpace(presentation.ConnectionIdentity)
            ? EffectivePresentationIdentity(presentation)
            : presentation.ConnectionIdentity.Trim();
    }

    private static string NormalizeStatus(string? value)
    {
        var normalized = string.Join(
            " ",
            (value ?? "")
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0)
        {
            return "Status unavailable.";
        }

        return normalized.Length <= MaximumStatusLength
            ? normalized
            : normalized[..(MaximumStatusLength - 1)] + "…";
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private enum ProviderModelFacet
    {
        All,
        Available,
        Assigned,
        Unassigned,
        Unavailable
    }

    private sealed record ModelFacetOption(ProviderModelFacet Facet, string DisplayName)
    {
        public static IReadOnlyList<ModelFacetOption> Options { get; } =
        [
            new(ProviderModelFacet.All, "All catalog"),
            new(ProviderModelFacet.Available, "Available"),
            new(ProviderModelFacet.Assigned, "Assigned"),
            new(ProviderModelFacet.Unassigned, "Unassigned"),
            new(ProviderModelFacet.Unavailable, "State unavailable")
        ];

        public override string ToString() => DisplayName;
    }

    private sealed record HistoryPolicyOption(
        ProviderModelHistoryPolicy Policy,
        string DisplayName,
        bool IsEnabled,
        string HelpText)
    {
        public string AutomationName => IsEnabled ? DisplayName : $"{DisplayName}, unavailable";

        public static IReadOnlyList<HistoryPolicyOption> Options { get; } = OptionsFor(false);

        public static IReadOnlyList<HistoryPolicyOption> OptionsFor(bool chapteredAvailable) =>
        [
            new(
                ProviderModelHistoryPolicy.Strict,
                "Strict",
                true,
                "Preserve the exact selected history and surface a truthful context-limit failure when it does not fit."),
            new(
                ProviderModelHistoryPolicy.Rolling80Percent,
                "Rolling 80%",
                true,
                "Keep recent whole history entries within an 80 percent input budget and report every omission."),
            new(
                ProviderModelHistoryPolicy.Chaptered,
                chapteredAvailable ? "Chaptered" : "Chaptered — Coming later",
                chapteredAvailable,
                chapteredAvailable
                    ? "Use durable chapter boundaries and receipts."
                    : "Chaptered history is visible for planning but is not available yet.")
        ];

        public override string ToString() => DisplayName;
    }

    private sealed record ResponseToneOption(ProviderModelResponseTone Tone, string DisplayName)
    {
        public static IReadOnlyList<ResponseToneOption> Options { get; } =
        [
            new(ProviderModelResponseTone.Default, "Default"),
            new(ProviderModelResponseTone.Neutral, "Neutral"),
            new(ProviderModelResponseTone.Concise, "Concise"),
            new(ProviderModelResponseTone.Analytical, "Analytical"),
            new(ProviderModelResponseTone.Creative, "Creative"),
            new(ProviderModelResponseTone.Direct, "Direct"),
            new(ProviderModelResponseTone.Custom, "Custom")
        ];

        public override string ToString() => DisplayName;
    }

    private sealed class ModelRowState : INotifyPropertyChanged
    {
        private readonly HashSet<string> assignedTargetIds = new(StringComparer.Ordinal);
        private string displayName = "";
        private ProviderModelAvailability availability;
        private string status = "";
        private string metadata = "";
        private string suppliedAutomationHelp = "";
        private bool canLoad;
        private bool canUnload;
        private bool isResidencyStale;
        private string lifecycleHelp = "";
        private string lifecycleActivity = "";
        private string assignmentSummary = "Not assigned";
        private string automationHelp = "";
        private string searchText = "";
        private ProviderModelConfigurationPresentation configuration = UnavailableConfiguration();

        public ModelRowState(ProviderModelAssignmentPresentation source)
        {
            Id = source.Id;
            UpdateFrom(source);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName => displayName;
        public ProviderModelAvailability Availability => availability;
        public string Status => status;
        public string DisplayStatus => lifecycleActivity.Length > 0 ? lifecycleActivity : status;
        public string Metadata => metadata;
        public bool CanLoad => canLoad;
        public bool CanUnload => canUnload;
        public bool IsResidencyStale => isResidencyStale;
        public string LifecycleHelp => lifecycleHelp;
        public bool HasLifecycleActivity => lifecycleActivity.Length > 0;
        public string SearchText => searchText;
        public IReadOnlyCollection<string> AssignedTargetIds => assignedTargetIds.ToArray();
        public ProviderModelConfigurationPresentation Configuration => configuration;
        public int GroupOrder => Availability switch
        {
            ProviderModelAvailability.Loaded => 0,
            ProviderModelAvailability.Available => 1,
            _ => 2
        };
        public string GroupName => Availability switch
        {
            ProviderModelAvailability.Loaded => "Loaded Models",
            ProviderModelAvailability.Available => "Available Models",
            _ => "Load State Unavailable"
        };
        public string AssignmentSummary
        {
            get => assignmentSummary;
            private set
            {
                if (assignmentSummary == value) return;
                assignmentSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssignmentSummary)));
            }
        }

        public string AutomationName => $"{DisplayName}, {DisplayStatus}";
        public string AutomationHelp
        {
            get => automationHelp;
            private set
            {
                if (automationHelp == value) return;
                automationHelp = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationHelp)));
            }
        }

        public bool IsAssigned(string targetId) => assignedTargetIds.Contains(targetId);

        public bool UpdateFrom(ProviderModelAssignmentPresentation source)
        {
            var viewRefreshNeeded = false;
            var nextDisplayName = DisplayOrFallback(source.DisplayName, source.Id);
            if (SetField(ref displayName, nextDisplayName, nameof(DisplayName)))
            {
                viewRefreshNeeded = true;
                Raise(nameof(AutomationName));
            }

            if (availability != source.Availability)
            {
                availability = source.Availability;
                viewRefreshNeeded = true;
                Raise(nameof(Availability));
                Raise(nameof(GroupOrder));
                Raise(nameof(GroupName));
            }

            var nextStatus = DisplayOrFallback(
                source.Status,
                source.Availability switch
                {
                    ProviderModelAvailability.Loaded => "Loaded",
                    ProviderModelAvailability.Available => "Available",
                    _ => "Load state unavailable"
                });
            if (SetField(ref status, nextStatus, nameof(Status)))
            {
                viewRefreshNeeded = true;
                Raise(nameof(DisplayStatus));
                Raise(nameof(AutomationName));
            }

            viewRefreshNeeded |= SetField(
                ref metadata,
                DisplayOrFallback(source.Metadata, source.Id),
                nameof(Metadata));
            suppliedAutomationHelp = source.AutomationHelp;
            SetField(ref canLoad, source.CanLoad, nameof(CanLoad));
            SetField(ref canUnload, source.CanUnload, nameof(CanUnload));
            SetField(ref isResidencyStale, source.IsResidencyStale, nameof(IsResidencyStale));
            SetField(
                ref lifecycleHelp,
                DisplayOrFallback(
                    source.LifecycleHelp,
                    "LM Studio residency changes do not change model assignments."),
                nameof(LifecycleHelp));
            SetConfiguration(source.Configuration ?? UnavailableConfiguration());

            var nextAssignments = source.AssignedTargetIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (!assignedTargetIds.SetEquals(nextAssignments))
            {
                assignedTargetIds.Clear();
                assignedTargetIds.UnionWith(nextAssignments);
                viewRefreshNeeded = true;
                Raise(nameof(AssignedTargetIds));
            }

            RefreshSearchText();
            return viewRefreshNeeded;
        }

        public void SetLifecycleActivity(string value)
        {
            var normalized = value?.Trim() ?? "";
            if (!SetField(ref lifecycleActivity, normalized, nameof(HasLifecycleActivity)))
            {
                return;
            }

            Raise(nameof(DisplayStatus));
            Raise(nameof(AutomationName));
            RefreshSearchText();
        }

        public void SetAssigned(string targetId, bool assigned)
        {
            var changed = assigned
                ? assignedTargetIds.Add(targetId)
                : assignedTargetIds.Remove(targetId);
            if (changed)
            {
                Raise(nameof(AssignedTargetIds));
            }
        }

        public void SetConfiguration(ProviderModelConfigurationPresentation value)
        {
            var normalized = NormalizeConfiguration(value);
            if (Equals(configuration, normalized))
            {
                return;
            }

            configuration = normalized;
            Raise(nameof(Configuration));
        }

        public bool RefreshAssignmentSummary(
            IReadOnlyDictionary<string, ProviderAssignmentTargetPresentation> targetLookup)
        {
            var labels = assignedTargetIds
                .Select(targetId => targetLookup.TryGetValue(targetId, out var target)
                    ? target.DisplayName
                    : targetId)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var nextSummary = labels.Length == 0
                ? "Not assigned"
                : $"Assigned to {string.Join(", ", labels)}";
            var changed = !string.Equals(AssignmentSummary, nextSummary, StringComparison.Ordinal);
            AssignmentSummary = nextSummary;
            AutomationHelp = string.Join(
                " ",
                new[] { suppliedAutomationHelp, Metadata, AssignmentSummary, lifecycleActivity }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            RefreshSearchText();
            return changed;
        }

        private void RefreshSearchText()
        {
            var value = string.Join(
                " ",
                new[] { DisplayName, Id, Status, Metadata, AssignmentSummary, lifecycleActivity }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            SetField(ref searchText, value, nameof(SearchText));
        }

        private bool SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(propertyName);
            return true;
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class TargetAssignmentState : INotifyPropertyChanged
    {
        private string modelId;
        private string modelDisplayName;
        private string displayName;
        private string helpText;
        private bool targetEnabled;
        private bool isDefaultTarget;
        private bool isAssigned;
        private bool interactionEnabled;
        private bool saving;
        private string interactionMessage = "";
        private ProviderTargetAssignmentState assignmentState;
        private ProviderTargetAssignmentState assignedState;
        private ProviderTargetAssignmentState clearedState;

        public TargetAssignmentState(
            string modelId,
            string modelDisplayName,
            ProviderAssignmentTargetPresentation target,
            bool assigned)
        {
            this.modelId = modelId;
            this.modelDisplayName = modelDisplayName;
            TargetId = target.Id;
            displayName = target.DisplayName;
            helpText = target.HelpText;
            targetEnabled = target.IsEnabled;
            isDefaultTarget = target.IsDefault;
            isAssigned = assigned;
            assignedState = target.AssignmentState == ProviderTargetAssignmentState.Unassigned
                ? target.AssignedState
                : target.AssignmentState;
            clearedState = target.ClearedState;
            assignmentState = target.AssignmentState != ProviderTargetAssignmentState.Unassigned
                ? target.AssignmentState
                : assigned
                    ? ProviderTargetAssignmentState.Explicit
                    : ProviderTargetAssignmentState.Unassigned;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ModelId => modelId;
        public string ModelDisplayName => modelDisplayName;
        public string TargetId { get; }
        public string DisplayName => displayName;
        public string HelpText => helpText;
        public bool TargetEnabled => targetEnabled;
        public ProviderTargetAssignmentState AssignmentState => assignmentState;
        public string AssignmentStateLabel => assignmentState switch
        {
            ProviderTargetAssignmentState.Default => "Default",
            ProviderTargetAssignmentState.Explicit => "Explicit",
            ProviderTargetAssignmentState.InheritsDefault => "Uses default",
            _ => "Unassigned"
        };
        public bool IsAssigned
        {
            get => isAssigned;
            set
            {
                if (isAssigned == value) return;
                isAssigned = value;
                Raise(nameof(IsAssigned));
                Raise(nameof(AutomationHelp));
                Raise(nameof(ItemStatus));
            }
        }

        public bool CanAssign => TargetEnabled && interactionEnabled;
        public string AutomationName => $"Assign {ModelDisplayName} to {(isDefaultTarget ? "Default for unassigned agents" : DisplayName)}";
        public string AutomationHelp => string.Join(
            " ",
            new[]
            {
                HelpText,
                $"{(IsAssigned ? "Turn off" : "Turn on")} routing of {ModelDisplayName} to {DisplayName}. Changes save immediately.",
                CanAssign ? "" : interactionMessage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        public string ItemStatus => saving ? "Saving" : AssignmentStateLabel;

        public void Retarget(
            string nextModelId,
            string nextModelDisplayName,
            ProviderAssignmentTargetPresentation target,
            bool assigned)
        {
            modelId = nextModelId;
            modelDisplayName = nextModelDisplayName;
            displayName = target.DisplayName;
            helpText = target.HelpText;
            targetEnabled = target.IsEnabled;
            isDefaultTarget = target.IsDefault;
            assignedState = target.AssignmentState == ProviderTargetAssignmentState.Unassigned
                ? target.AssignedState
                : target.AssignmentState;
            clearedState = target.ClearedState;
            isAssigned = assigned;
            assignmentState = target.AssignmentState != ProviderTargetAssignmentState.Unassigned
                ? target.AssignmentState
                : assigned
                    ? assignedState
                    : ProviderTargetAssignmentState.Unassigned;
            Raise(nameof(ModelId));
            Raise(nameof(ModelDisplayName));
            Raise(nameof(DisplayName));
            Raise(nameof(HelpText));
            Raise(nameof(TargetEnabled));
            Raise(nameof(IsAssigned));
            Raise(nameof(AssignmentState));
            Raise(nameof(AssignmentStateLabel));
            Raise(nameof(CanAssign));
            Raise(nameof(AutomationName));
            Raise(nameof(AutomationHelp));
            Raise(nameof(ItemStatus));
        }

        public void SetAssigned(bool value)
        {
            IsAssigned = value;
            var nextState = value
                ? assignedState
                : clearedState;
            if (assignmentState == nextState)
            {
                return;
            }

            assignmentState = nextState;
            Raise(nameof(AssignmentState));
            Raise(nameof(AssignmentStateLabel));
            Raise(nameof(AutomationHelp));
            Raise(nameof(ItemStatus));
        }

        public void SetInteractionEnabled(bool value, string unavailableMessage)
        {
            var normalizedMessage = unavailableMessage?.Trim() ?? "";
            if (interactionEnabled == value
                && string.Equals(interactionMessage, normalizedMessage, StringComparison.Ordinal))
            {
                return;
            }

            interactionEnabled = value;
            interactionMessage = normalizedMessage;
            Raise(nameof(CanAssign));
            Raise(nameof(AutomationHelp));
        }

        public void SetSaving(bool value)
        {
            if (saving == value) return;
            saving = value;
            Raise(nameof(ItemStatus));
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record PendingAssignment(
        Guid ChangeId,
        string ModelId,
        string ModelDisplayName,
        string TargetId,
        string TargetDisplayName,
        bool RequestedValue,
        bool IsDefaultTarget,
        IReadOnlyList<string> ReplacedModelDisplayNames,
        IReadOnlyList<PendingAssignmentMutation> Mutations,
        string ConnectionIdentity)
    {
        public bool IsTransfer => RequestedValue && ReplacedModelDisplayNames.Count > 0;
    }

    private sealed record PendingAssignmentMutation(
        string ModelId,
        string ModelDisplayName,
        bool PreviousValue,
        bool RequestedValue);

    private sealed record PendingLifecycle(
        Guid OperationId,
        string ModelId,
        string ModelDisplayName,
        bool Load,
        string ConnectionIdentity);

    private sealed record PendingConfiguration(
        Guid ChangeId,
        string ModelId,
        string ModelDisplayName,
        ProviderModelConfigurationPresentation Previous,
        ProviderModelConfigurationPresentation Requested,
        string ConfigurationIdentity,
        string ConnectionIdentity);

    private sealed record PendingConfigurationReload(
        Guid OperationId,
        string ModelId,
        string ModelDisplayName,
        string ConfigurationIdentity,
        string ConnectionIdentity);

    private sealed record CatalogViewportAnchor(string ModelId, double RelativeY);

}

public sealed class ProviderAssignmentCheckBox : CheckBox
{
    public event RoutedEventHandler? ToggleRequested;

    protected override void OnToggle()
    {
        base.OnToggle();
        ToggleRequested?.Invoke(this, new RoutedEventArgs());
    }
}

/// <summary>
/// Keeps assignment targets available to UI Automation while tolerating the
/// short container-regeneration window caused by a live theme resource change.
/// WPF's stock ItemsControl peer can dereference a disconnected child peer in
/// that window; retrying on the next automation query is safer than allowing a
/// theme switch to fault the UI thread.
/// </summary>
public sealed class StableAutomationItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new StableItemsControlAutomationPeer(this);

    private sealed class StableItemsControlAutomationPeer : ItemsControlAutomationPeer
    {
        private readonly ItemsControl owner;

        public StableItemsControlAutomationPeer(ItemsControl owner)
            : base(owner)
        {
            this.owner = owner;
        }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
            new StableItemAutomationPeer(item, this);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            try
            {
                return base.GetChildrenCore();
            }
            catch (NullReferenceException)
            {
                // DynamicResource changes can transiently disconnect an item
                // container while the stock peer validates its child tree.
                // Returning the currently connected children keeps the parent
                // peer valid; WPF invalidates and rebuilds this list afterward.
                var connected = new List<AutomationPeer>();
                for (var index = 0; index < owner.Items.Count; index++)
                {
                    if (owner.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                    {
                        continue;
                    }

                    var peer = UIElementAutomationPeer.CreatePeerForElement(container)
                        ?? new FrameworkElementAutomationPeer(container);
                    connected.Add(peer);
                }

                return connected;
            }
        }
    }

    private sealed class StableItemAutomationPeer(
        object item,
        ItemsControlAutomationPeer parent)
        : ItemAutomationPeer(item, parent)
    {
        protected override string GetClassNameCore() => "ProviderAssignmentTarget";

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Group;
    }
}

/// <summary>
/// Applies the same transient automation-tree protection to the virtualized,
/// grouped provider catalog. Group expanders can raise Toggle events while a
/// recycled ListBox container is being reconnected during a live theme change.
/// </summary>
public sealed class StableAutomationListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new StableListBoxAutomationPeer(this);

    private sealed class StableListBoxAutomationPeer : ListBoxAutomationPeer
    {
        private readonly ListBox owner;

        public StableListBoxAutomationPeer(ListBox owner)
            : base(owner)
        {
            this.owner = owner;
        }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
            new ListBoxItemAutomationPeer(item, this);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            try
            {
                return base.GetChildrenCore();
            }
            catch (NullReferenceException)
            {
                var connected = new List<AutomationPeer>();
                for (var index = 0; index < owner.Items.Count; index++)
                {
                    if (owner.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                    {
                        continue;
                    }

                    var peer = UIElementAutomationPeer.CreatePeerForElement(container)
                        ?? new FrameworkElementAutomationPeer(container);
                    connected.Add(peer);
                }

                return connected;
            }
        }
    }
}
