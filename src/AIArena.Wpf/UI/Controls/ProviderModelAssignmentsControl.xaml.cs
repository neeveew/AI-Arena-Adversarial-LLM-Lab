using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace AIArena.Wpf.Controls;

public enum ProviderModelAvailability
{
    Loaded,
    Available,
    Unavailable
}

public enum ProviderConnectionState
{
    Unknown,
    Online,
    Offline,
    Checking,
    Failed
}

public enum ProviderAssignmentSaveState
{
    Ready,
    Saving,
    Saved,
    Failed
}

public enum ProviderModelLifecycleActionState
{
    Ready,
    Running,
    Unconfirmed,
    Succeeded,
    Failed
}

public sealed record ProviderAssignmentTargetPresentation(
    string Id,
    string DisplayName,
    string HelpText = "",
    bool IsEnabled = true);

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
    bool IsResidencyStale = false);

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

public partial class ProviderModelAssignmentsControl : UserControl
{
    internal const double CompactContentThreshold = 1100;
    internal const double WideHostWindowThreshold = 1200;
    private const double CompactHeaderThreshold = 760;
    private const int MaximumStatusLength = 240;

    private readonly RangeObservableCollection<ModelRowState> modelRows = [];
    private readonly ListCollectionView modelRowsView;
    private IReadOnlyList<ProviderAssignmentTargetPresentation> targets = [];
    private IReadOnlyDictionary<string, ProviderAssignmentTargetPresentation> targetsById =
        new Dictionary<string, ProviderAssignmentTargetPresentation>(StringComparer.Ordinal);
    private IReadOnlyList<TargetAssignmentState> selectedTargetRows = [];
    private ProviderModelAssignmentsPresentation? currentPresentation;
    private PendingAssignment? pendingAssignment;
    private PendingLifecycle? pendingLifecycle;
    private bool pendingLifecycleMutationStarted;
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
    private bool usesCompactLayout;
    private Window? layoutHostWindow;

    public ProviderModelAssignmentsControl()
    {
        InitializeComponent();
        modelRowsView = new ListCollectionView(modelRows)
        {
            CustomSort = ModelRowComparer.Instance,
            Filter = MatchesCurrentSearch
        };
        modelRowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelRowState.GroupName)));
        ModelsList.ItemsSource = modelRowsView;
        ModelsList.ItemContainerGenerator.StatusChanged += ModelsListItemContainerGenerator_StatusChanged;
        ApplyResponsiveLayout(compact: false);
        UpdateEmptyState();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ConnectionSettingsRequested;
    public event EventHandler<ProviderModelSearchChangedEventArgs>? SearchChanged;
    public event EventHandler<ProviderModelAssignmentChangedEventArgs>? AssignmentChanged;
    public event EventHandler<ProviderModelLifecycleRequestedEventArgs>? LifecycleRequested;

    public string SearchQuery => ModelSearchText.Text;
    public string SelectedModelId => (ModelsList.SelectedItem as ModelRowState)?.Id ?? "";
    public bool HasPendingAssignment => pendingAssignment is not null;
    public bool HasPendingLifecycle => pendingLifecycle is not null;

    public bool HasUnconfirmedLifecycleReceipt =>
        lastLifecycleState == ProviderModelLifecycleActionState.Unconfirmed
        && unconfirmedLifecycleReceipt is not null
        && LifecycleReceiptMatchesConnection(currentPresentation);

    internal bool UsesCompactLayout => usesCompactLayout;
    internal ListBox CatalogList => ModelsList;
    internal TextBox SearchBox => ModelSearchText;
    internal Border MasterSurface => MasterPane;
    internal Border DetailSurface => DetailPane;
    internal ItemsControl AssignmentTargets => AssignmentTargetsItems;
    internal TextBlock AssignmentStatus => AssignmentSaveStatusText;
    internal Border AssignmentStatusSurface => AssignmentSaveStatusCard;
    internal TextBlock CatalogStatus => CatalogStatusText;
    internal Border ConnectionStatusSurface => ConnectionStatusChip;
    internal TextBlock ConnectionStatusLabel => ConnectionStatusText;
    internal Button RefreshAction => RefreshButton;
    internal Button ConnectionAction => ConnectionButton;
    internal Button CloseAction => CloseButton;
    internal Button LifecycleAction => LifecycleButton;
    internal TextBlock LifecycleStatus => LifecycleStatusText;
    internal Border LifecycleStatusSurface => LifecycleStatusCard;
    internal int PresentationApplyCount { get; private set; }

    public void ApplyPresentation(ProviderModelAssignmentsPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        Dispatcher.VerifyAccess();
        PresentationApplyCount++;

        applyingPresentation = true;
        try
        {
            CancelPendingAssignmentWhenIdentityChanges(presentation);
            CancelPendingLifecycleWhenIdentityChanges(presentation);
            ClearUnconfirmedLifecycleForConnectionChange(presentation);
            currentPresentation = presentation;
            var priorSelection = SelectedModelId;
            ProviderNameText.Text = DisplayOrFallback(presentation.ProviderName, "Provider");
            ConnectionStatusText.Text = DisplayOrFallback(presentation.ConnectionStatus, "Unavailable");
            CatalogStatusText.Text = DisplayOrFallback(presentation.CatalogStatus, "Model evidence is unavailable.");
            RefreshButton.IsEnabled = presentation.CanRefresh && !presentation.IsRefreshing;
            RefreshButton.Content = presentation.IsRefreshing ? "Refreshing…" : "Refresh";
            ConnectionButton.IsEnabled = presentation.CanOpenConnectionSettings;
            CloseButton.IsEnabled = presentation.CanClose;
            AutomationProperties.SetItemStatus(
                CatalogStatusText,
                presentation.IsRefreshing ? "Refreshing" : "Current");
            ApplyConnectionState(presentation.ConnectionState);

            targets = DistinctTargets(presentation.Targets);
            targetsById = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);

            var preserveCatalogDuringRefresh = presentation.IsRefreshing
                && presentation.Models.Count == 0
                && modelRows.Count > 0;
            if (!preserveCatalogDuringRefresh)
            {
                var replacementRows = new List<ModelRowState>();
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

                foreach (var model in presentedModels)
                {
                    var row = new ModelRowState(model);
                    if (pendingAssignment is not null
                        && string.Equals(row.Id, pendingAssignment.ModelId, StringComparison.Ordinal))
                    {
                        row.SetAssigned(pendingAssignment.TargetId, pendingAssignment.RequestedValue);
                    }

                    row.RefreshAssignmentSummary(targetsById);
                    replacementRows.Add(row);
                }

                modelRows.ReplaceAll(replacementRows);
            }
            else
            {
                foreach (var row in modelRows)
                {
                    row.RefreshAssignmentSummary(targetsById);
                }
            }

            modelRowsView.Refresh();
            ReconcileUnconfirmedLifecycle(presentation);
            var requestedSelection = pendingAssignment?.ModelId
                ?? pendingLifecycle?.ModelId
                ?? (!string.IsNullOrWhiteSpace(presentation.SelectedModelId)
                    ? presentation.SelectedModelId
                    : priorSelection);
            ModelsList.SelectedItem = VisibleRows()
                .FirstOrDefault(row => string.Equals(row.Id, requestedSelection, StringComparison.Ordinal))
                ?? VisibleRows().FirstOrDefault();
            RebuildSelectedDetail();
            UpdateEmptyState();
            ApplyInteractionState();
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
            var model = FindModel(pending.ModelId);
            model?.SetAssigned(pending.TargetId, pending.PreviousValue);
            model?.RefreshAssignmentSummary(targetsById);
            var selectedTarget = selectedTargetRows.FirstOrDefault(target =>
                string.Equals(target.ModelId, pending.ModelId, StringComparison.Ordinal)
                && string.Equals(target.TargetId, pending.TargetId, StringComparison.Ordinal));
            selectedTarget?.SetAssigned(pending.PreviousValue);
        }

        pendingAssignment = null;
        lastResolvedModelId = pending.ModelId;
        lastSaveState = state;
        lastSaveMessage = state switch
        {
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

    public bool FocusCatalog()
    {
        if (!ModelsList.IsEnabled)
        {
            return false;
        }

        ModelsList.Focus();
        ModelsList.ScrollIntoView(ModelsList.SelectedItem);
        if (ModelsList.SelectedItem is not null
            && ModelsList.ItemContainerGenerator.ContainerFromItem(ModelsList.SelectedItem) is ListBoxItem item)
        {
            return item.Focus();
        }

        return ModelsList.IsKeyboardFocusWithin;
    }

    public bool FocusSearch() => ModelSearchText.Focus();

    internal static bool UsesCompactLayoutAt(double contentWidth, double hostWindowWidth = double.NaN)
    {
        if (!double.IsNaN(hostWindowWidth) && hostWindowWidth > 0)
        {
            return hostWindowWidth < WideHostWindowThreshold;
        }

        return !double.IsNaN(contentWidth)
            && contentWidth > 0
            && contentWidth <= CompactContentThreshold;
    }

    internal void ApplyResponsiveLayout(bool compact)
    {
        usesCompactLayout = compact;
        if (compact)
        {
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
        }
        else
        {
            MasterColumn.Width = new GridLength(72, GridUnitType.Star);
            WorkspaceGapColumn.Width = new GridLength(12);
            DetailColumn.Width = new GridLength(28, GridUnitType.Star);
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
    }

    private void ProviderModelAssignmentsControl_Loaded(object sender, RoutedEventArgs e)
    {
        AttachLayoutHostWindow(Window.GetWindow(this));
        ApplyResponsiveLayout(UsesCompactLayoutAt(ActualWidth, layoutHostWindow?.ActualWidth ?? double.NaN));
    }

    private void ProviderModelAssignmentsControl_Unloaded(object sender, RoutedEventArgs e) =>
        AttachLayoutHostWindow(null);

    private void ProviderModelAssignmentsControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayoutAt(e.NewSize.Width, layoutHostWindow?.ActualWidth ?? double.NaN));

    private void LayoutHostWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayoutAt(ActualWidth, e.NewSize.Width));

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
        if (ModelsList.SelectedItem is not ModelRowState model
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
        ApplyLifecycleAction(model);
        ApplyInteractionState();

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

    private void ModelSearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (modelRowsView is null)
        {
            return;
        }

        modelRowsView.Refresh();
        if (ModelsList.SelectedItem is not ModelRowState selected || !MatchesCurrentSearch(selected))
        {
            ModelsList.SelectedItem = VisibleRows().FirstOrDefault();
        }

        RebuildSelectedDetail();
        UpdateEmptyState();
        SearchChanged?.Invoke(this, new ProviderModelSearchChangedEventArgs(ModelSearchText.Text));
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!applyingPresentation && pendingAssignment is null)
        {
            lastResolvedModelId = "";
            lastSaveState = ProviderAssignmentSaveState.Ready;
            lastSaveMessage = "Ready to assign.";
        }

        RebuildSelectedDetail();
    }

    private void ModelsListItemContainerGenerator_StatusChanged(object? sender, EventArgs e)
    {
        if (ModelsList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        foreach (var row in VisibleRows())
        {
            if (ModelsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item)
            {
                continue;
            }

            BindingOperations.SetBinding(
                item,
                AutomationProperties.NameProperty,
                new Binding(nameof(ModelRowState.AutomationName)));
            BindingOperations.SetBinding(
                item,
                AutomationProperties.HelpTextProperty,
                new Binding(nameof(ModelRowState.AutomationHelp)));
            BindingOperations.SetBinding(
                item,
                AutomationProperties.ItemStatusProperty,
                new Binding(nameof(ModelRowState.Status)));
        }
    }

    private void AssignmentTargetCheckBox_ToggleRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || checkBox.DataContext is not TargetAssignmentState target
            || ModelsList.SelectedItem is not ModelRowState model
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

        model.SetAssigned(target.TargetId, requestedValue);
        model.RefreshAssignmentSummary(targetsById);
        target.SetAssigned(requestedValue);
        RefreshSelectedAssignmentText();

        var changeId = Guid.NewGuid();
        pendingAssignment = new PendingAssignment(
            changeId,
            model.Id,
            model.DisplayName,
            target.TargetId,
            previousValue,
            requestedValue,
            EffectiveConnectionIdentity(currentPresentation));
        lastResolvedModelId = model.Id;
        lastSaveState = ProviderAssignmentSaveState.Saving;
        lastSaveMessage = $"Saving assignment for {model.DisplayName}…";
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

    private void RebuildSelectedDetail()
    {
        if (ModelsList.SelectedItem is not ModelRowState model)
        {
            selectedTargetRows = [];
            AssignmentTargetsItems.ItemsSource = null;
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
        selectedTargetRows = targets
            .Select(target => new TargetAssignmentState(
                model.Id,
                model.DisplayName,
                target,
                model.IsAssigned(target.Id)))
            .ToArray();
        AssignmentTargetsItems.ItemsSource = selectedTargetRows;
        ModelDetailContent.Visibility = Visibility.Visible;
        ModelDetailEmptyState.Visibility = Visibility.Collapsed;

        if (pendingAssignment is not null)
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Saving, lastSaveMessage);
        }
        else if (currentPresentation?.IsRefreshing == true)
        {
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Ready,
                "Assignments are unavailable while provider models refresh.");
        }
        else if (currentPresentation?.CanAssign != true)
        {
            SetAssignmentStatus(
                ProviderAssignmentSaveState.Ready,
                "Assignments are unavailable for the current provider state.");
        }
        else if (string.Equals(lastResolvedModelId, model.Id, StringComparison.Ordinal))
        {
            SetAssignmentStatus(lastSaveState, lastSaveMessage);
        }
        else if (selectedTargetRows.Count == 0)
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Ready, "No assignment targets are available.");
        }
        else
        {
            SetAssignmentStatus(ProviderAssignmentSaveState.Ready, "Ready to assign.");
        }

        ApplyInteractionState();
    }

    private void ApplyLifecycleAction(ModelRowState model)
    {
        if (pendingLifecycle is not null)
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
            SetLifecycleStatus(ProviderModelLifecycleActionState.Running, lastLifecycleMessage);
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
                && currentPresentation.IsRefreshing == false;
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
    }

    private void RefreshSelectedAssignmentText()
    {
        if (ModelsList.SelectedItem is ModelRowState model)
        {
            SelectedModelAssignmentsText.Text = model.AssignmentSummary;
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

        var model = FindModel(pending.ModelId);
        model?.SetAssigned(pending.TargetId, pending.PreviousValue);
        model?.RefreshAssignmentSummary(targetsById);
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
            IsResidencyStale: true);
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
        modelRowsView.Refresh();
        ModelsList.SelectedItem = row;
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
            && pendingLifecycle is null;
        foreach (var target in selectedTargetRows)
        {
            target.SetInteractionEnabled(canAssign);
            target.SetSaving(
                pendingAssignment is not null
                && string.Equals(target.ModelId, pendingAssignment.ModelId, StringComparison.Ordinal)
                && string.Equals(target.TargetId, pendingAssignment.TargetId, StringComparison.Ordinal));
        }

        var hasPendingChange = pendingAssignment is not null || pendingLifecycle is not null;
        // Keep catalog inspection and scrolling available while a save or LM Studio
        // lifecycle request is running. Only controls that can start a conflicting
        // mutation are disabled below.
        ModelsList.IsEnabled = true;
        ModelSearchText.IsEnabled = !hasPendingChange;
        RefreshButton.IsEnabled = currentPresentation?.CanRefresh == true
            && currentPresentation.IsRefreshing == false
            && !hasPendingChange;
        LifecycleButton.IsEnabled = !hasPendingChange
            && currentPresentation?.CanRunLifecycle == true
            && currentPresentation?.IsRefreshing == false
            && ModelsList.SelectedItem is ModelRowState model
            && !IsUnconfirmedLifecycle(model)
            && TryLifecycleAction(model, out _);
        if (pendingAssignment is not null)
        {
            AutomationProperties.SetItemStatus(LifecycleButton, "Unavailable");
            AutomationProperties.SetHelpText(
                LifecycleButton,
                $"Wait for the assignment for {pendingAssignment.ModelDisplayName} to finish before changing LM Studio residency.");
        }
    }

    private static string PendingAssignmentMessage(PendingAssignment pending, string? message)
    {
        var progress = DisplayOrFallback(message, $"Saving assignment for {pending.ModelDisplayName}…");
        return progress.Contains(pending.ModelDisplayName, StringComparison.OrdinalIgnoreCase)
            ? progress
            : $"{progress} Model: {pending.ModelDisplayName}.";
    }

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
            ProviderConnectionState.Checking => "Arena.Brush.Info",
            ProviderConnectionState.Offline or ProviderConnectionState.Failed => "DangerBorderBrush",
            _ => "DisabledBorderBrush"
        };
        var textKey = state switch
        {
            ProviderConnectionState.Online => "Arena.Brush.Success",
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
        if (item is not ModelRowState model)
        {
            return false;
        }

        var query = ModelSearchText?.Text?.Trim() ?? "";
        return query.Length == 0
            || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.Status.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.Metadata.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ModelRowState> VisibleRows() =>
        modelRowsView.Cast<object>().OfType<ModelRowState>();

    private ModelRowState? FindModel(string modelId) =>
        modelRows.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));

    private void UpdateEmptyState()
    {
        var hasVisibleModels = VisibleRows().Any();
        ModelsEmptyState.Visibility = hasVisibleModels ? Visibility.Collapsed : Visibility.Visible;
        if (hasVisibleModels)
        {
            return;
        }

        var query = ModelSearchText?.Text?.Trim() ?? "";
        ModelsEmptyStateText.Text = modelRows.Count == 0
            ? "Refresh the provider model list or review the connection."
            : $"No models match “{NormalizeStatus(query)}”.";
        AutomationProperties.SetHelpText(ModelsEmptyState, ModelsEmptyStateText.Text);
    }

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

    private sealed class ModelRowState : INotifyPropertyChanged
    {
        private readonly HashSet<string> assignedTargetIds;
        private readonly string suppliedAutomationHelp;
        private string assignmentSummary = "Not assigned";
        private string automationHelp = "";

        public ModelRowState(ProviderModelAssignmentPresentation source)
        {
            Id = source.Id;
            DisplayName = source.DisplayName;
            Availability = source.Availability;
            Status = DisplayOrFallback(
                source.Status,
                source.Availability switch
                {
                    ProviderModelAvailability.Loaded => "Loaded",
                    ProviderModelAvailability.Available => "Available",
                    _ => "Load state unavailable"
                });
            Metadata = DisplayOrFallback(source.Metadata, source.Id);
            suppliedAutomationHelp = source.AutomationHelp;
            CanLoad = source.CanLoad;
            CanUnload = source.CanUnload;
            IsResidencyStale = source.IsResidencyStale;
            LifecycleHelp = DisplayOrFallback(
                source.LifecycleHelp,
                "LM Studio residency changes do not change model assignments.");
            assignedTargetIds = new HashSet<string>(
                source.AssignedTargetIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()),
                StringComparer.Ordinal);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName { get; }
        public ProviderModelAvailability Availability { get; }
        public string Status { get; }
        public string Metadata { get; }
        public bool CanLoad { get; }
        public bool CanUnload { get; }
        public bool IsResidencyStale { get; }
        public string LifecycleHelp { get; }
        public IReadOnlyCollection<string> AssignedTargetIds => assignedTargetIds.ToArray();
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

        public string AutomationName => $"{DisplayName}, {Status}";
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

        public void SetAssigned(string targetId, bool assigned)
        {
            if (assigned)
            {
                assignedTargetIds.Add(targetId);
            }
            else
            {
                assignedTargetIds.Remove(targetId);
            }
        }

        public void RefreshAssignmentSummary(
            IReadOnlyDictionary<string, ProviderAssignmentTargetPresentation> targetLookup)
        {
            var labels = assignedTargetIds
                .Select(targetId => targetLookup.TryGetValue(targetId, out var target)
                    ? target.DisplayName
                    : targetId)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AssignmentSummary = labels.Length == 0
                ? "Not assigned"
                : $"Assigned to {string.Join(", ", labels)}";
            AutomationHelp = string.Join(
                " ",
                new[] { suppliedAutomationHelp, Metadata, AssignmentSummary }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    private sealed class TargetAssignmentState : INotifyPropertyChanged
    {
        private bool isAssigned;
        private bool interactionEnabled;
        private bool saving;

        public TargetAssignmentState(
            string modelId,
            string modelDisplayName,
            ProviderAssignmentTargetPresentation target,
            bool assigned)
        {
            ModelId = modelId;
            ModelDisplayName = modelDisplayName;
            TargetId = target.Id;
            DisplayName = target.DisplayName;
            HelpText = target.HelpText;
            TargetEnabled = target.IsEnabled;
            isAssigned = assigned;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ModelId { get; }
        public string ModelDisplayName { get; }
        public string TargetId { get; }
        public string DisplayName { get; }
        public string HelpText { get; }
        public bool TargetEnabled { get; }
        public bool IsAssigned
        {
            get => isAssigned;
            set
            {
                if (isAssigned == value) return;
                isAssigned = value;
                Raise(nameof(IsAssigned));
                Raise(nameof(ItemStatus));
            }
        }

        public bool CanAssign => TargetEnabled && interactionEnabled;
        public string AutomationName => $"Assign {ModelDisplayName} to {DisplayName}";
        public string AutomationHelp => string.Join(
            " ",
            new[]
            {
                HelpText,
                $"Toggle whether {ModelDisplayName} is routed to {DisplayName}. Changes save immediately."
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        public string ItemStatus => saving ? "Saving" : IsAssigned ? "Assigned" : "Not assigned";

        public void SetAssigned(bool value) => IsAssigned = value;

        public void SetInteractionEnabled(bool value)
        {
            if (interactionEnabled == value) return;
            interactionEnabled = value;
            Raise(nameof(CanAssign));
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
        bool PreviousValue,
        bool RequestedValue,
        string ConnectionIdentity);

    private sealed record PendingLifecycle(
        Guid OperationId,
        string ModelId,
        string ModelDisplayName,
        bool Load,
        string ConnectionIdentity);

    private sealed class RangeObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> values)
        {
            Items.Clear();
            foreach (var value in values)
            {
                Items.Add(value);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }

    private sealed class ModelRowComparer : IComparer
    {
        public static ModelRowComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not ModelRowState left) return -1;
            if (y is not ModelRowState right) return 1;
            var group = left.GroupOrder.CompareTo(right.GroupOrder);
            if (group != 0) return group;
            var display = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            return display != 0 ? display : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }
    }
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
