using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

internal sealed record RouteProposalItem(
    string Heading,
    string EvidenceText,
    string ConstraintText)
{
    public string AutomationName => Heading;
    public string AutomationHelp => $"{EvidenceText} {ConstraintText}";
}

public partial class ModelRoutingOptimizerControl : UserControl
{
    internal const double CompactLayoutThreshold = 720;
    private ModelRoutingOptimizerCoordinator? coordinator;
    private bool proposalCanApply;
    private bool applyBoundaryConnected;
    private bool evidenceCanPropose;
    private bool busy;

    public ModelRoutingOptimizerControl()
    {
        InitializeComponent();
    }

    public ExperimentLabFeatureRegistration FeatureRegistration => new(
        "routing-optimizer",
        "Routing Optimizer",
        "Proposal then approval",
        "Compare compatible observed model evidence, then separately approve an exact route application.",
        this);

    internal static bool UsesCompactLayout(double width) =>
        !double.IsNaN(width) && width > 0 && width < CompactLayoutThreshold;

    internal void Initialize(ModelRoutingOptimizerCoordinator value)
    {
        coordinator = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal string ApproverId => RouteApproverText.Text;

    internal bool IsExplicitlyApproved => ApproveRouteCheckBox.IsChecked == true;
    internal string Status => RouteStatusText.Text;
    internal int ProposalItemCount => RouteProposalList.Items.Count;
    internal bool CanApply => ApplyRouteButton.IsEnabled;
    internal WorkspacePageHeaderControl WorkspaceHeader => RoutingWorkspaceHeader;

    internal void SetExplicitApproval(bool approved, string approverId = "operator:local")
    {
        RouteApproverText.Text = approverId;
        ApproveRouteCheckBox.IsChecked = approved;
        UpdateApplyAvailability();
    }

    internal void SetStatus(string value, string? helpText = null)
    {
        RouteStatusText.Text = value;
        ToolTipService.SetToolTip(RouteStatusText, helpText ?? value);
        AutomationProperties.SetHelpText(RouteStatusText, helpText ?? value);
    }

    internal void SetEvidence(string summary, IEnumerable<string> diagnostics, bool canPropose)
    {
        RouteEvidenceSummaryText.Text = summary;
        RouteEvidenceList.ItemsSource = diagnostics.ToArray();
        evidenceCanPropose = canPropose;
        RoutingWorkspaceHeader.IsPrimaryActionEnabled = canPropose && !busy;
        AutomationProperties.SetItemStatus(RoutingWorkspaceHeader.PrimaryAction, RoutingWorkspaceHeader.IsPrimaryActionEnabled ? "available" : "unavailable");
    }

    internal void SetProposal(string summary, IEnumerable<RouteProposalItem> changes, bool canApply, bool applicationConnected)
    {
        RouteProposalSummaryText.Text = summary;
        RouteProposalList.ItemsSource = changes.ToArray();
        proposalCanApply = canApply;
        applyBoundaryConnected = applicationConnected;
        ApproveRouteCheckBox.IsChecked = false;
        ApproveRouteCheckBox.IsEnabled = canApply && applicationConnected && !busy;
        CopyRouteReceiptButton.IsEnabled = false;
        UpdateApplyAvailability();
    }

    internal void SetReceiptAvailable(bool available)
    {
        CopyRouteReceiptButton.IsEnabled = available && !busy;
        AutomationProperties.SetItemStatus(CopyRouteReceiptButton, CopyRouteReceiptButton.IsEnabled ? "available" : "unavailable");
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        RefreshRouteEvidenceButton.IsEnabled = !busy;
        RoutingWorkspaceHeader.IsPrimaryActionEnabled = !busy && evidenceCanPropose;
        AutomationProperties.SetItemStatus(RoutingWorkspaceHeader.PrimaryAction, RoutingWorkspaceHeader.IsPrimaryActionEnabled ? "available" : "unavailable");
        ApproveRouteCheckBox.IsEnabled = !busy && proposalCanApply && applyBoundaryConnected;
        RouteApproverText.IsEnabled = !busy;
        CopyRouteReceiptButton.IsEnabled = !busy && coordinator?.LastReceipt is not null;
        UpdateApplyAvailability();
    }

    internal void ApplyResponsiveLayout(bool compact)
    {
        if (compact)
        {
            RouteEvidenceColumn.Width = new GridLength(1, GridUnitType.Star);
            RouteGapColumn.Width = new GridLength(0);
            RouteProposalColumn.Width = new GridLength(0);
            RouteEvidenceRow.Height = GridLength.Auto;
            RouteCompactGapRow.Height = new GridLength(10);
            RouteProposalRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(RouteEvidencePane, 0);
            Grid.SetColumn(RouteEvidencePane, 0);
            Grid.SetColumnSpan(RouteEvidencePane, 3);
            Grid.SetRow(RouteProposalPane, 2);
            Grid.SetColumn(RouteProposalPane, 0);
            Grid.SetColumnSpan(RouteProposalPane, 3);
            RouteEvidencePane.MaxHeight = 300;
        }
        else
        {
            RouteEvidenceColumn.Width = new GridLength(330);
            RouteGapColumn.Width = new GridLength(10);
            RouteProposalColumn.Width = new GridLength(1, GridUnitType.Star);
            RouteEvidenceRow.Height = new GridLength(1, GridUnitType.Star);
            RouteCompactGapRow.Height = new GridLength(0);
            RouteProposalRow.Height = new GridLength(0);
            Grid.SetRow(RouteEvidencePane, 0);
            Grid.SetColumn(RouteEvidencePane, 0);
            Grid.SetColumnSpan(RouteEvidencePane, 1);
            Grid.SetRow(RouteProposalPane, 0);
            Grid.SetColumn(RouteProposalPane, 2);
            Grid.SetColumnSpan(RouteProposalPane, 1);
            RouteEvidencePane.ClearValue(MaxHeightProperty);
        }
    }

    private void UpdateApplyAvailability()
    {
        ApplyRouteButton.IsEnabled = !busy
            && proposalCanApply
            && applyBoundaryConnected
            && ApproveRouteCheckBox.IsChecked == true
            && !string.IsNullOrWhiteSpace(RouteApproverText.Text);
        AutomationProperties.SetItemStatus(ApplyRouteButton, ApplyRouteButton.IsEnabled ? "available" : "unavailable");
    }

    private void ModelRoutingOptimizerControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayout(e.NewSize.Width));

    private async void RefreshRouteEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.RefreshEvidenceAsync();
    }

    private async void BuildRouteProposalButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.BuildProposalAsync();
    }

    private async void ApplyRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.ApplyApprovedAsync();
    }

    private void CopyRouteReceiptButton_Click(object sender, RoutedEventArgs e) => coordinator?.CopyReceipt();

    private void ApproveRouteCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateApplyAvailability();
}
