using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

internal sealed record FaultLabInput(
    ArenaFaultKind Kind,
    string Seed,
    string AtSequence,
    string DurationMilliseconds,
    string Intensity,
    string MaxOccurrences,
    string MaximumConcurrency);

internal sealed record FaultKindChoice(ArenaFaultKind Kind, string Label)
{
    public override string ToString() => Label;
}

internal sealed record FaultObservationItem(
    string Heading,
    string EffectText,
    string CauseText,
    string RecoveryText)
{
    public string AutomationName => Heading;
    public string AutomationHelp => $"{EffectText} {CauseText} {RecoveryText}";
}

public partial class FaultInjectionLabControl : UserControl
{
    internal const double CompactLayoutThreshold = 720;
    private FaultInjectionLabCoordinator? coordinator;

    public FaultInjectionLabControl()
    {
        InitializeComponent();
        FaultKindPicker.ItemsSource = Enum.GetValues<ArenaFaultKind>()
            .Select(kind => new FaultKindChoice(kind, Label(kind)))
            .ToArray();
        FaultKindPicker.SelectedIndex = 0;
    }

    public ExperimentLabFeatureRegistration FeatureRegistration => new(
        "fault-injection",
        "Fault-Injection Lab",
        "Deterministic failures",
        "Arm bounded process-only provider faults and inspect separate cause and recovery evidence.",
        this);

    internal static bool UsesCompactLayout(double width) =>
        !double.IsNaN(width) && width > 0 && width < CompactLayoutThreshold;

    internal void Initialize(FaultInjectionLabCoordinator value)
    {
        coordinator = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal FaultLabInput ReadInput() => new(
        (FaultKindPicker.SelectedItem as FaultKindChoice)?.Kind ?? ArenaFaultKind.Timeout,
        FaultSeedText.Text,
        FaultSequenceText.Text,
        FaultDurationText.Text,
        FaultIntensityText.Text,
        FaultOccurrencesText.Text,
        FaultConcurrencyText.Text);

    internal string Status => FaultStatusText.Text;
    internal int ObservationCount => FaultObservationList.Items.Count;
    internal IReadOnlyList<FaultObservationItem> ObservationItems =>
        FaultObservationList.Items.Cast<FaultObservationItem>().ToArray();
    internal bool CanRunProbe => RunFaultProbeButton.IsEnabled;
    internal WorkspacePageHeaderControl WorkspaceHeader => FaultWorkspaceHeader;

    internal void SetInput(FaultLabInput input)
    {
        FaultKindPicker.SelectedItem = FaultKindPicker.Items.Cast<FaultKindChoice>().First(item => item.Kind == input.Kind);
        FaultSeedText.Text = input.Seed;
        FaultSequenceText.Text = input.AtSequence;
        FaultDurationText.Text = input.DurationMilliseconds;
        FaultIntensityText.Text = input.Intensity;
        FaultOccurrencesText.Text = input.MaxOccurrences;
        FaultConcurrencyText.Text = input.MaximumConcurrency;
    }

    internal void SetStatus(string value, string? helpText = null)
    {
        FaultStatusText.Text = value;
        ToolTipService.SetToolTip(FaultStatusText, helpText ?? value);
        AutomationProperties.SetHelpText(FaultStatusText, helpText ?? value);
    }

    internal void SetObservations(IEnumerable<FaultObservationItem> values) =>
        FaultObservationList.ItemsSource = values.ToArray();

    internal void SetLifecycle(bool connected, bool armed, bool busy)
    {
        FaultWorkspaceHeader.IsPrimaryActionEnabled = connected && !armed && !busy;
        RunFaultProbeButton.IsEnabled = connected && armed && !busy;
        DisarmFaultButton.IsEnabled = armed;
        FaultKindPicker.IsEnabled = !armed && !busy;
        FaultSeedText.IsEnabled = !armed && !busy;
        FaultSequenceText.IsEnabled = !armed && !busy;
        FaultDurationText.IsEnabled = !armed && !busy;
        FaultIntensityText.IsEnabled = !armed && !busy;
        FaultOccurrencesText.IsEnabled = !armed && !busy;
        FaultConcurrencyText.IsEnabled = !armed && !busy;
        AutomationProperties.SetItemStatus(FaultWorkspaceHeader.PrimaryAction, FaultWorkspaceHeader.IsPrimaryActionEnabled ? "available" : "unavailable");
        AutomationProperties.SetItemStatus(RunFaultProbeButton, RunFaultProbeButton.IsEnabled ? "available" : "unavailable");
        AutomationProperties.SetItemStatus(DisarmFaultButton, DisarmFaultButton.IsEnabled ? "available" : "unavailable");
    }

    internal void ApplyResponsiveLayout(bool compact)
    {
        if (compact)
        {
            FaultSetupColumn.Width = new GridLength(1, GridUnitType.Star);
            FaultGapColumn.Width = new GridLength(0);
            FaultEvidenceColumn.Width = new GridLength(0);
            FaultSetupRow.Height = GridLength.Auto;
            FaultCompactGapRow.Height = new GridLength(10);
            FaultEvidenceRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(FaultSetupPane, 0);
            Grid.SetColumn(FaultSetupPane, 0);
            Grid.SetColumnSpan(FaultSetupPane, 3);
            Grid.SetRow(FaultEvidencePane, 2);
            Grid.SetColumn(FaultEvidencePane, 0);
            Grid.SetColumnSpan(FaultEvidencePane, 3);
            FaultSetupPane.MaxHeight = 520;
        }
        else
        {
            FaultSetupColumn.Width = new GridLength(360);
            FaultGapColumn.Width = new GridLength(10);
            FaultEvidenceColumn.Width = new GridLength(1, GridUnitType.Star);
            FaultSetupRow.Height = new GridLength(1, GridUnitType.Star);
            FaultCompactGapRow.Height = new GridLength(0);
            FaultEvidenceRow.Height = new GridLength(0);
            Grid.SetRow(FaultSetupPane, 0);
            Grid.SetColumn(FaultSetupPane, 0);
            Grid.SetColumnSpan(FaultSetupPane, 1);
            Grid.SetRow(FaultEvidencePane, 0);
            Grid.SetColumn(FaultEvidencePane, 2);
            Grid.SetColumnSpan(FaultEvidencePane, 1);
            FaultSetupPane.ClearValue(MaxHeightProperty);
        }
    }

    private static string Label(ArenaFaultKind kind) => kind switch
    {
        ArenaFaultKind.Timeout => "Timeout",
        ArenaFaultKind.Disconnect => "Disconnect",
        ArenaFaultKind.MalformedStream => "Malformed stream",
        ArenaFaultKind.Saturation => "Saturation",
        ArenaFaultKind.EmptyResponse => "Empty response",
        ArenaFaultKind.Interruption => "Interruption",
        ArenaFaultKind.ContextPressure => "Context pressure",
        _ => kind.ToString()
    };

    private void FaultInjectionLabControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayout(e.NewSize.Width));

    private async void ArmFaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.ArmAsync();
    }

    private async void RunFaultProbeButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.RunProbeAsync();
    }

    private async void DisarmFaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.DisarmAsync();
    }
}
