using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace AIArena.Wpf.Controls;

public partial class AgentInspectionLabControl : UserControl
{
    public const string PromptInspectorFeatureKey = "context-prompt-inspector";
    public const string MemoryDebuggerFeatureKey = "agent-memory-debugger";
    internal const double WideContentThreshold = 720;
    internal const double WideViewportThreshold = 1200;
    private IReadOnlyList<ExperimentLabFeatureRegistration>? detachedFeatureRegistrations;
    private Window? responsiveHostWindow;
    private int memoryBusyDepth;
    private bool memoryAddAvailable;
    private bool memoryCorrectAvailable;
    private bool memoryExpireAvailable;
    internal event EventHandler? PromptRefreshRequested;
    internal event EventHandler? PromptClearRequested;
    internal event SelectionChangedEventHandler? PromptSelectionChanged;
    internal event EventHandler? MemoryRefreshRequested;
    internal event SelectionChangedEventHandler? MemoryAgentChanged;
    internal event SelectionChangedEventHandler? MemoryStateChanged;
    internal event SelectionChangedEventHandler? MemorySelectionChanged;
    internal event EventHandler? MemoryAddRequested;
    internal event EventHandler? MemoryCorrectRequested;
    internal event EventHandler? MemoryExpireRequested;

    public AgentInspectionLabControl()
    {
        InitializeComponent();
        PromptRefreshButton.Click += (_, _) => PromptRefreshRequested?.Invoke(this, EventArgs.Empty);
        PromptClearButton.Click += (_, _) => PromptClearRequested?.Invoke(this, EventArgs.Empty);
        PromptTraceList.SelectionChanged += (_, args) => PromptSelectionChanged?.Invoke(this, args);
        MemoryRefreshButton.Click += (_, _) => MemoryRefreshRequested?.Invoke(this, EventArgs.Empty);
        MemoryAgentPicker.SelectionChanged += (_, args) => MemoryAgentChanged?.Invoke(this, args);
        MemoryStatePicker.SelectionChanged += (_, args) => MemoryStateChanged?.Invoke(this, args);
        MemoryEntryList.SelectionChanged += (_, args) => MemorySelectionChanged?.Invoke(this, args);
        MemoryAddButton.Click += (_, _) => MemoryAddRequested?.Invoke(this, EventArgs.Empty);
        MemoryCorrectButton.Click += (_, _) => MemoryCorrectRequested?.Invoke(this, EventArgs.Empty);
        MemoryExpireButton.Click += (_, _) => MemoryExpireRequested?.Invoke(this, EventArgs.Empty);
        InspectionTabs.SelectionChanged += (_, _) => UpdateWorkspaceHeaderActionAvailability();
        PromptTraceList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyItemContainerAutomation(PromptTraceList);
        MemoryEntryList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyItemContainerAutomation(MemoryEntryList);
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            ApplyItemContainerAutomation(PromptTraceList);
            ApplyItemContainerAutomation(MemoryEntryList);
        };
        // These roots can be detached into two independent Experiment Lab
        // registrations. Their own size changes must therefore own responsive
        // layout; the optional tab host may no longer be in the visual tree.
        PromptFeatureRoot.SizeChanged += (_, args) => ApplyPromptResponsiveLayout(args.NewSize.Width);
        MemoryFeatureRoot.SizeChanged += (_, args) => ApplyMemoryResponsiveLayout(args.NewSize.Width);
        PromptFeatureRoot.Loaded += FeatureRoot_Loaded;
        PromptFeatureRoot.Unloaded += FeatureRoot_Unloaded;
        MemoryFeatureRoot.Loaded += FeatureRoot_Loaded;
        MemoryFeatureRoot.Unloaded += FeatureRoot_Unloaded;
    }

    internal InspectionLabLayoutTier CurrentLayoutTier { get; private set; } = InspectionLabLayoutTier.Wide;
    internal InspectionLabLayoutTier PromptLayoutTier { get; private set; } = InspectionLabLayoutTier.Wide;
    internal InspectionLabLayoutTier MemoryLayoutTier { get; private set; } = InspectionLabLayoutTier.Wide;

    internal ListBox PromptList => PromptTraceList;
    internal TextBlock PromptStatus => PromptStatusText;
    internal TextBlock PromptMetadata => PromptMetadataText;
    internal TextBlock PromptHash => PromptHashText;
    internal TextBlock PromptTokens => PromptTokenText;
    internal TextBox PromptPayload => PromptPayloadText;
    internal ItemsControl PromptRoles => PromptRolesItems;
    internal ItemsControl PromptContext => PromptContextItems;
    internal Button PromptClear => PromptClearButton;
    internal ComboBox MemoryAgent => MemoryAgentPicker;
    internal ComboBox MemoryState => MemoryStatePicker;
    internal ListBox MemoryList => MemoryEntryList;
    internal TextBlock MemoryStatus => MemoryStatusText;
    internal TextBlock MemoryPrivacy => MemoryPrivacyText;
    internal TextBlock MemoryMetadata => MemoryMetadataText;
    internal TextBox MemoryEditor => MemoryEditorText;
    internal ComboBox MemoryVisibility => MemoryVisibilityPicker;
    internal ComboBox MemoryExpiry => MemoryExpiryPicker;
    internal Button MemoryAdd => MemoryAddButton;
    internal Button MemoryCorrect => MemoryCorrectButton;
    internal Button MemoryExpire => MemoryExpireButton;
    internal TabControl Tabs => InspectionTabs;
    internal FrameworkElement MemoryBusyState => MemoryBusyOverlay;
    internal TextBlock MemoryBusyStatus => MemoryBusyText;
    internal bool IsMemoryBusy => memoryBusyDepth > 0;

    internal void SetMemoryActionAvailability(bool add, bool correct, bool expire)
    {
        memoryAddAvailable = add;
        memoryCorrectAvailable = correct;
        memoryExpireAvailable = expire;
        ApplyMemoryActionAvailability();
    }

    internal void SetMemoryBusy(bool busy, string? message = null)
    {
        if (busy)
        {
            checked
            {
                memoryBusyDepth++;
            }
        }
        else if (memoryBusyDepth > 0)
        {
            memoryBusyDepth--;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            MemoryBusyText.Text = message.Trim();
            AutomationProperties.SetHelpText(MemoryBusyText, MemoryBusyText.Text);
        }

        var isBusy = memoryBusyDepth > 0;
        MemoryBusyOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        MemoryRefreshButton.IsEnabled = !isBusy;
        MemoryAgentPicker.IsEnabled = !isBusy;
        MemoryStatePicker.IsEnabled = !isBusy;
        MemoryEntryList.IsEnabled = !isBusy;
        MemoryEditorText.IsEnabled = !isBusy;
        MemoryVisibilityPicker.IsEnabled = !isBusy;
        MemoryExpiryPicker.IsEnabled = !isBusy;
        UpdateWorkspaceHeaderActionAvailability();
        ApplyMemoryActionAvailability();
        AutomationProperties.SetItemStatus(MemoryFeatureRoot, isBusy ? "revalidating" : "ready");
    }

    private void ApplyMemoryActionAvailability()
    {
        var idle = memoryBusyDepth == 0;
        MemoryAddButton.IsEnabled = idle && memoryAddAvailable;
        MemoryCorrectButton.IsEnabled = idle && memoryCorrectAvailable;
        MemoryExpireButton.IsEnabled = idle && memoryExpireAvailable;
    }

    private void RefreshCurrentWorkspaceHeader_Click(object sender, RoutedEventArgs e)
    {
        if (InspectionTabs.SelectedItem == MemoryDebuggerTab)
        {
            if (memoryBusyDepth == 0)
            {
                MemoryRefreshRequested?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        PromptRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateWorkspaceHeaderActionAvailability() =>
        InspectionWorkspaceHeader.IsPrimaryActionEnabled =
            InspectionTabs.SelectedItem != MemoryDebuggerTab || memoryBusyDepth == 0;

    /// <summary>
    /// Detaches the two independently backed feature surfaces from this optional
    /// tab host and returns registrations ready for ExperimentLabControl. This is
    /// one-shot and idempotent; the same registrations are returned on later calls.
    /// </summary>
    public IReadOnlyList<ExperimentLabFeatureRegistration> DetachFeatureRegistrations()
    {
        if (detachedFeatureRegistrations is not null)
        {
            return detachedFeatureRegistrations;
        }

        if (PromptInspectorTab.Content is not FrameworkElement promptContent
            || MemoryDebuggerTab.Content is not FrameworkElement memoryContent)
        {
            throw new InvalidOperationException("Inspection feature content has already been detached unexpectedly.");
        }

        PromptInspectorTab.Content = null;
        MemoryDebuggerTab.Content = null;
        detachedFeatureRegistrations =
        [
            new ExperimentLabFeatureRegistration(
                PromptInspectorFeatureKey,
                "Context & Prompt Inspector",
                "Inspect exact outbound-body measurements, redacted payloads, token evidence, role transformations, and context omissions.",
                "Process-memory-only provider evidence. Credentials, endpoints, paths, and scoped-memory content remain redacted or unavailable.",
                promptContent),
            new ExperimentLabFeatureRegistration(
                MemoryDebuggerFeatureKey,
                "Agent Memory Debugger",
                "Inspect and safely revise one selected agent's provenance-aware memory, including correction, expiry, and branch state.",
                "Private content stays hidden until one agent is explicitly selected. Mutations persist through StructuredMemoryService and retain lifecycle evidence.",
                memoryContent)
        ];
        return detachedFeatureRegistrations;
    }

    internal void ApplyResponsiveLayout(double width)
        => ApplyResponsiveLayout(width, HostedViewportWidth(this));

    internal void ApplyResponsiveLayout(double width, double viewportWidth)
    {
        CurrentLayoutTier = ResolveLayout(width, viewportWidth);
        ApplyPromptResponsiveLayout(width, viewportWidth);
        ApplyMemoryResponsiveLayout(width, viewportWidth);
    }

    private void ApplyPromptResponsiveLayout(double width) =>
        ApplyPromptResponsiveLayout(width, HostedViewportWidth(PromptFeatureRoot));

    private void ApplyPromptResponsiveLayout(double width, double viewportWidth)
    {
        PromptLayoutTier = ResolveLayout(width, viewportWidth);
        var stacked = PromptLayoutTier == InspectionLabLayoutTier.Stacked;
        ApplyHeaderLayout(PromptHeaderActions, PromptHeaderActionColumn, PromptHeaderActionRow, stacked);
        ApplyPaneLayout(
            PromptListPane,
            PromptDetailPane,
            PromptListColumn,
            PromptDetailColumn,
            PromptPrimaryRow,
            PromptSecondaryRow,
            stacked,
            320);
    }

    private void ApplyMemoryResponsiveLayout(double width) =>
        ApplyMemoryResponsiveLayout(width, HostedViewportWidth(MemoryFeatureRoot));

    private void ApplyMemoryResponsiveLayout(double width, double viewportWidth)
    {
        MemoryLayoutTier = ResolveLayout(width, viewportWidth);
        var stacked = MemoryLayoutTier == InspectionLabLayoutTier.Stacked;
        ApplyHeaderLayout(MemoryHeaderActions, MemoryHeaderActionColumn, MemoryHeaderActionRow, stacked);
        ApplyPaneLayout(
            MemoryListPane,
            MemoryDetailPane,
            MemoryListColumn,
            MemoryDetailColumn,
            MemoryPrimaryRow,
            MemorySecondaryRow,
            stacked,
            380);
    }

    internal static InspectionLabLayoutTier ResolveLayout(double width, double viewportWidth = double.NaN)
    {
        var viewportIsCompact = IsUsableWidth(viewportWidth) && viewportWidth < WideViewportThreshold;
        var contentIsCompact = IsUsableWidth(width) && width < WideContentThreshold;
        return viewportIsCompact || contentIsCompact
            ? InspectionLabLayoutTier.Stacked
            : InspectionLabLayoutTier.Wide;
    }

    private void FeatureRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement featureRoot)
        {
            AttachResponsiveHostWindow(featureRoot);
            if (ReferenceEquals(featureRoot, PromptFeatureRoot))
            {
                ApplyPromptResponsiveLayout(featureRoot.ActualWidth);
            }
            else if (ReferenceEquals(featureRoot, MemoryFeatureRoot))
            {
                ApplyMemoryResponsiveLayout(featureRoot.ActualWidth);
            }
        }
    }

    private void FeatureRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!PromptFeatureRoot.IsLoaded && !MemoryFeatureRoot.IsLoaded)
        {
            DetachResponsiveHostWindow();
        }
    }

    private void AttachResponsiveHostWindow(FrameworkElement featureRoot)
    {
        var hostWindow = Window.GetWindow(featureRoot);
        if (ReferenceEquals(responsiveHostWindow, hostWindow))
        {
            return;
        }

        DetachResponsiveHostWindow();
        responsiveHostWindow = hostWindow;
        if (responsiveHostWindow is not null)
        {
            responsiveHostWindow.SizeChanged += ResponsiveHostWindow_SizeChanged;
        }
    }

    private void DetachResponsiveHostWindow()
    {
        if (responsiveHostWindow is not null)
        {
            responsiveHostWindow.SizeChanged -= ResponsiveHostWindow_SizeChanged;
            responsiveHostWindow = null;
        }
    }

    private void ResponsiveHostWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewportWidth = e.NewSize.Width;
        var promptWidth = IsUsableWidth(PromptFeatureRoot.ActualWidth) ? PromptFeatureRoot.ActualWidth : ActualWidth;
        var memoryWidth = IsUsableWidth(MemoryFeatureRoot.ActualWidth) ? MemoryFeatureRoot.ActualWidth : ActualWidth;
        CurrentLayoutTier = ResolveLayout(ActualWidth, viewportWidth);
        ApplyPromptResponsiveLayout(promptWidth, viewportWidth);
        ApplyMemoryResponsiveLayout(memoryWidth, viewportWidth);
    }

    private static double HostedViewportWidth(DependencyObject element)
    {
        var width = Window.GetWindow(element)?.ActualWidth ?? double.NaN;
        return IsUsableWidth(width) ? width : double.NaN;
    }

    private static bool IsUsableWidth(double width) =>
        !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;

    private static void ApplyHeaderLayout(
        FrameworkElement actionHost,
        ColumnDefinition actionColumn,
        RowDefinition actionRow,
        bool stacked)
    {
        actionColumn.Width = stacked ? new GridLength(0) : GridLength.Auto;
        actionRow.Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(actionHost, stacked ? 1 : 0);
        Grid.SetColumn(actionHost, stacked ? 0 : 1);
        Grid.SetColumnSpan(actionHost, stacked ? 2 : 1);
        actionHost.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        actionHost.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private static void ApplyItemContainerAutomation(ListBox list)
    {
        if (list.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            {
                continue;
            }

            BindingOperations.SetBinding(
                container,
                AutomationProperties.NameProperty,
                new Binding("AutomationName"));
            BindingOperations.SetBinding(
                container,
                AutomationProperties.HelpTextProperty,
                new Binding("AutomationHelp"));
        }
    }

    private static void ApplyPaneLayout(
        FrameworkElement listPane,
        FrameworkElement detailPane,
        ColumnDefinition listColumn,
        ColumnDefinition detailColumn,
        RowDefinition primaryRow,
        RowDefinition secondaryRow,
        bool stacked,
        double listWidth)
    {
        listColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(listWidth);
        detailColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        primaryRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        secondaryRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumn(listPane, 0);
        Grid.SetRow(listPane, 0);
        Grid.SetColumn(detailPane, stacked ? 0 : 1);
        Grid.SetRow(detailPane, stacked ? 1 : 0);
        listPane.Margin = stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 8, 0);
        listPane.MaxHeight = stacked ? 280 : double.PositiveInfinity;
    }
}

internal enum InspectionLabLayoutTier
{
    Wide,
    Stacked
}
