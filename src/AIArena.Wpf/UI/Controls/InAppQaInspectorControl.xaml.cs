using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

public partial class InAppQaInspectorControl : UserControl
{
    internal const double CompactLayoutThreshold = 760;
    private InAppQaInspectorCoordinator? coordinator;
    private bool suppressArtifactSelection;

    public InAppQaInspectorControl()
    {
        InitializeComponent();
    }

    public ExperimentLabFeatureRegistration FeatureRegistration => new(
        "in-app-qa-inspector",
        "In-App QA Inspector",
        "Seal evidence and focused QA",
        "Inspect current local seal evidence, run allowlisted focused suites, and explicitly accept verified post-render artifacts.",
        this);

    internal static bool UsesCompactLayout(double width) =>
        !double.IsNaN(width) && width > 0 && width < CompactLayoutThreshold;

    internal void Initialize(InAppQaInspectorCoordinator value) =>
        coordinator = value ?? throw new ArgumentNullException(nameof(value));

    internal void SetEvidenceChoices(IReadOnlyList<QaEvidenceChoice> choices, string? selectedRelativePath)
    {
        var prior = selectedRelativePath ?? (EvidenceRunPicker.SelectedItem as QaEvidenceChoice)?.RelativePath;
        EvidenceRunPicker.ItemsSource = choices;
        EvidenceRunPicker.SelectedItem = choices.FirstOrDefault(item => item.RelativePath.Equals(prior, StringComparison.Ordinal))
            ?? choices.FirstOrDefault();
    }

    internal string? SelectedEvidencePath => (EvidenceRunPicker.SelectedItem as QaEvidenceChoice)?.RelativePath;

    internal QaLocalSuite? SelectedSuite => (SuitePicker.SelectedItem as QaSuiteDefinition)?.Id;

    internal void SetSuiteChoices(IReadOnlyList<QaSuiteDefinition> choices)
    {
        SuitePicker.ItemsSource = choices;
        SuitePicker.SelectedIndex = choices.Count > 0 ? 0 : -1;
    }

    internal void SetPostCloseCommand(string value)
    {
        PostCloseCommandText.Text = value;
        AutomationProperties.SetHelpText(PostCloseCommandText, value);
    }

    internal void ApplyPresentation(QaInspectorPresentation value)
    {
        SetStatus(value.Status);
        VerdictText.Text = value.Verdict;
        ProvenanceText.Text = value.Provenance;
        EnvironmentText.Text = value.Environment;
        TestTotalsText.Text = value.TestTotals;
        LiveProviderText.Text = value.LiveProvider;
        InspectionText.Text = value.Inspection;
        GateList.ItemsSource = value.Gates;
        SchemaList.ItemsSource = value.Schemas;
        PerformanceList.ItemsSource = value.Performance;
        LimitationsList.ItemsSource = value.Limitations;
        suppressArtifactSelection = true;
        ArtifactList.ItemsSource = value.Artifacts;
        ArtifactList.SelectedIndex = -1;
        suppressArtifactSelection = false;
        AutomationProperties.SetItemStatus(QaInspectorRoot, value.State.ToString());
    }

    internal void SetStatus(string value)
    {
        QaStatusText.Text = value;
        ToolTipService.SetToolTip(QaStatusText, value);
        AutomationProperties.SetHelpText(QaStatusText, value);
    }

    internal void SetEvidenceBusy(bool busy)
    {
        RefreshEvidenceButton.IsEnabled = !busy;
        EvidenceRunPicker.IsEnabled = !busy;
        ArtifactList.IsEnabled = !busy;
        AcceptInspectionButton.IsEnabled = !busy && AcceptInspectionButton.Tag as bool? == true;
    }

    internal void SetAcceptanceAvailable(bool available)
    {
        AcceptInspectionButton.Tag = available;
        AcceptInspectionButton.IsEnabled = available && RefreshEvidenceButton.IsEnabled;
        AutomationProperties.SetItemStatus(AcceptInspectionButton, available ? "available" : "unavailable");
    }

    internal void SetReviewProgress(int reviewed, int total, int unacceptedLimitations)
    {
        var reviewText = total <= 0
            ? "No rendered screenshots are ready for explicit review."
            : reviewed >= total
                ? $"Reviewed {total}/{total} screenshot(s). The exact review manifest is complete."
                : $"Reviewed {reviewed}/{total} screenshot(s). Select each screenshot in Artifacts to preview it explicitly.";
        var limitationText = unacceptedLimitations > 0
            ? $" Accepting also explicitly accepts {unacceptedLimitations} listed evidence limitation(s); review them in Overview first."
            : "";
        var text = reviewText + limitationText;
        ReviewProgressText.Text = text;
        ToolTipService.SetToolTip(ReviewProgressText, text);
        AutomationProperties.SetHelpText(ReviewProgressText, text);
        AutomationProperties.SetHelpText(AcceptInspectionButton, text);
        AutomationProperties.SetItemStatus(ReviewProgressText, reviewed >= total && total > 0 ? "complete" : "incomplete");
    }

    internal void SetSuiteBusy(bool busy)
    {
        RunSuiteButton.IsEnabled = !busy;
        SuitePicker.IsEnabled = !busy;
        CancelSuiteButton.IsEnabled = busy;
    }

    internal void SetSuiteProgress(string value)
    {
        SuiteStatusText.Text = value;
        AutomationProperties.SetHelpText(SuiteStatusText, value);
    }

    internal void SetSuiteResult(QaSuiteRunResult value)
    {
        var text = $"{value.State.ToString().ToUpperInvariant()} · {value.CompletedSteps}/{value.TotalSteps} step(s) passed · {value.FailedSteps} failed · {value.DurationMilliseconds} ms. {value.Summary}";
        SuiteStatusText.Text = text;
        AutomationProperties.SetHelpText(SuiteStatusText, text);
        AutomationProperties.SetItemStatus(SuiteStatusText, value.State.ToString());
    }

    internal void SetReport(string value)
    {
        ReportText.Text = value;
        EvidenceTabs.SelectedIndex = 4;
    }

    internal bool SetPreview(QaArtifactPreview? preview)
    {
        if (preview is null)
        {
            CurrentPreviewImage.Source = null;
            BaselinePreviewImage.Source = null;
            CurrentEmptyText.Visibility = Visibility.Visible;
            BaselineEmptyText.Visibility = Visibility.Visible;
            AutomationLinkText.Text = "Linked automation unavailable.";
            return false;
        }

        CurrentPreviewImage.Source = DecodePng(preview.CurrentPng);
        CurrentEmptyText.Visibility = CurrentPreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        BaselinePreviewImage.Source = preview.BaselinePng is null ? null : DecodePng(preview.BaselinePng);
        BaselineEmptyText.Visibility = BaselinePreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationLinkText.Text = preview.AutomationStatus;
        AutomationProperties.SetHelpText(CurrentPreviewImage, $"Verified screenshot {preview.ArtifactId}, relative path {preview.RelativePath}.");
        AutomationProperties.SetHelpText(BaselinePreviewImage, preview.BaselineArtifactId is null
            ? "No baseline artifact was supplied."
            : $"Verified baseline {preview.BaselineArtifactId}, relative path {preview.BaselineRelativePath}.");
        AutomationProperties.SetHelpText(AutomationLinkText, preview.AutomationStatus);
        return CurrentPreviewImage.Source is not null;
    }

    internal void ApplyResponsiveLayout(bool compact)
    {
        if (compact)
        {
            EvidenceCommandColumn.Width = new GridLength(1, GridUnitType.Star);
            CommandGapColumn.Width = new GridLength(0);
            SuiteCommandColumn.Width = new GridLength(0);
            CommandCompactGapRow.Height = new GridLength(10);
            SuiteCommandCompactRow.Height = GridLength.Auto;
            Grid.SetRow(SuiteCommandPane, 2);
            Grid.SetColumn(SuiteCommandPane, 0);
            Grid.SetColumnSpan(SuiteCommandPane, 3);

            SchemaColumn.Width = new GridLength(1, GridUnitType.Star);
            SchemaGapColumn.Width = new GridLength(0);
            PerformanceColumn.Width = new GridLength(0);
            SchemaCompactGapRow.Height = new GridLength(10);
            PerformanceCompactRow.Height = GridLength.Auto;
            Grid.SetRow(PerformancePane, 2);
            Grid.SetColumn(PerformancePane, 0);
            Grid.SetColumnSpan(PerformancePane, 3);

            ArtifactListColumn.Width = new GridLength(1, GridUnitType.Star);
            ArtifactGapColumn.Width = new GridLength(0);
            ArtifactPreviewColumn.Width = new GridLength(0);
            ArtifactCompactGapRow.Height = new GridLength(10);
            ArtifactPreviewCompactRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(ArtifactPreviewPane, 2);
            Grid.SetColumn(ArtifactPreviewPane, 0);
            Grid.SetColumnSpan(ArtifactPreviewPane, 3);
            ArtifactListPane.MaxHeight = 340;
        }
        else
        {
            EvidenceCommandColumn.Width = new GridLength(1, GridUnitType.Star);
            CommandGapColumn.Width = new GridLength(10);
            SuiteCommandColumn.Width = new GridLength(1, GridUnitType.Star);
            CommandCompactGapRow.Height = new GridLength(0);
            SuiteCommandCompactRow.Height = new GridLength(0);
            Grid.SetRow(SuiteCommandPane, 0);
            Grid.SetColumn(SuiteCommandPane, 2);
            Grid.SetColumnSpan(SuiteCommandPane, 1);

            SchemaColumn.Width = new GridLength(1, GridUnitType.Star);
            SchemaGapColumn.Width = new GridLength(10);
            PerformanceColumn.Width = new GridLength(1, GridUnitType.Star);
            SchemaCompactGapRow.Height = new GridLength(0);
            PerformanceCompactRow.Height = new GridLength(0);
            Grid.SetRow(PerformancePane, 0);
            Grid.SetColumn(PerformancePane, 2);
            Grid.SetColumnSpan(PerformancePane, 1);

            ArtifactListColumn.Width = new GridLength(390);
            ArtifactGapColumn.Width = new GridLength(10);
            ArtifactPreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            ArtifactCompactGapRow.Height = new GridLength(0);
            ArtifactPreviewCompactRow.Height = new GridLength(0);
            Grid.SetRow(ArtifactPreviewPane, 0);
            Grid.SetColumn(ArtifactPreviewPane, 2);
            Grid.SetColumnSpan(ArtifactPreviewPane, 1);
            ArtifactListPane.ClearValue(MaxHeightProperty);
        }
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            return null;
        }
    }

    private void InAppQaInspectorControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayout(e.NewSize.Width));

    private async void RefreshEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.RefreshAsync(SelectedEvidencePath);
    }

    private async void RunSuiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null && SelectedSuite is { } suite) await coordinator.RunSuiteAsync(suite);
    }

    private void CancelSuiteButton_Click(object sender, RoutedEventArgs e) => coordinator?.CancelSuite();

    private void CopyReportButton_Click(object sender, RoutedEventArgs e) => coordinator?.CopyReport();

    private void CopyPostCloseCommandButton_Click(object sender, RoutedEventArgs e) => coordinator?.CopyPostCloseCommand();

    private async void AcceptInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null) await coordinator.AcceptInspectionAsync();
    }

    private async void ArtifactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!suppressArtifactSelection
            && coordinator is not null
            && ArtifactList.SelectedItem is QaArtifactPresentation { Kind: "rendered-ui-screenshot", State: "PASS" } artifact)
        {
            await coordinator.SelectScreenshotAsync(artifact.Id);
        }
    }
}
