using System.Windows;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

public partial class AgentInspectionLabControl : UserControl
{
    public const string PromptInspectorFeatureKey = "context-prompt-inspector";
    public const string MemoryDebuggerFeatureKey = "agent-memory-debugger";
    private IReadOnlyList<ExperimentLabFeatureRegistration>? detachedFeatureRegistrations;
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
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        // These roots can be detached into two independent Experiment Lab
        // registrations. Their own size changes must therefore own responsive
        // layout; the optional tab host may no longer be in the visual tree.
        PromptFeatureRoot.SizeChanged += (_, args) => ApplyPromptResponsiveLayout(args.NewSize.Width);
        MemoryFeatureRoot.SizeChanged += (_, args) => ApplyMemoryResponsiveLayout(args.NewSize.Width);
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
    {
        CurrentLayoutTier = ResolveLayout(width);
        ApplyPromptResponsiveLayout(width);
        ApplyMemoryResponsiveLayout(width);
    }

    private void ApplyPromptResponsiveLayout(double width)
    {
        PromptLayoutTier = ResolveLayout(width);
        ApplyPaneLayout(
            PromptListPane,
            PromptDetailPane,
            PromptListColumn,
            PromptDetailColumn,
            PromptPrimaryRow,
            PromptSecondaryRow,
            PromptLayoutTier == InspectionLabLayoutTier.Stacked,
            320);
    }

    private void ApplyMemoryResponsiveLayout(double width)
    {
        MemoryLayoutTier = ResolveLayout(width);
        ApplyPaneLayout(
            MemoryListPane,
            MemoryDetailPane,
            MemoryListColumn,
            MemoryDetailColumn,
            MemoryPrimaryRow,
            MemorySecondaryRow,
            MemoryLayoutTier == InspectionLabLayoutTier.Stacked,
            380);
    }

    internal static InspectionLabLayoutTier ResolveLayout(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) || width <= 0 || width >= 840
            ? InspectionLabLayoutTier.Wide
            : InspectionLabLayoutTier.Stacked;

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
