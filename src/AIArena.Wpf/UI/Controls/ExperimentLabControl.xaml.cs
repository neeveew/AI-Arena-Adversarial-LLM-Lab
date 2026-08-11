using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AIArena.Core.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

public sealed class ExperimentLabFeatureRegistration : INotifyPropertyChanged
{
    private string refreshStatus = "registered";

    public ExperimentLabFeatureRegistration(
        string key,
        string title,
        string summary,
        string helpText,
        FrameworkElement content,
        string group = "Inspect & verify",
        string iconGlyph = "")
    {
        Key = key;
        Title = title;
        Summary = summary;
        HelpText = helpText;
        Content = content;
        Group = group;
        IconGlyph = iconGlyph;
    }

    public string Key { get; }
    public string Title { get; }
    public string Summary { get; }
    public string HelpText { get; }
    public FrameworkElement Content { get; }
    public string Group { get; }
    public string IconGlyph { get; }

    public string RefreshStatus
    {
        get => refreshStatus;
        internal set
        {
            if (string.Equals(refreshStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            refreshStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefreshStatusLabel));
            OnPropertyChanged(nameof(RefreshStatusKind));
        }
    }

    public string RefreshStatusLabel => refreshStatus switch
    {
        "refreshing" => "Refreshing",
        "ready" => "Ready",
        "refresh-failed" => "Refresh failed",
        "superseded" => "Superseded",
        _ => "Available"
    };

    public string RefreshStatusKind => refreshStatus switch
    {
        "refreshing" => "Revalidating",
        "ready" => "Ready",
        "refresh-failed" => "Failed",
        "superseded" => "Partial",
        _ => "Status"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record ExperimentLabFeatureControlState(
    string Key,
    string Title,
    bool Registered,
    bool Selectable,
    bool Busy,
    string Status);

internal sealed record ExperimentLabControlState(
    string SelectedKey,
    bool Busy,
    IReadOnlyList<ExperimentLabFeatureControlState> Features);

internal sealed record ExperimentMatrixInput(
    string Title,
    string ScenarioPackId,
    string ProviderProfileIds,
    string DimensionParameter,
    string DimensionValues,
    string Repetitions,
    string TurnBudget,
    string MaxParallelism,
    string BenchmarkPackId = "",
    string RubricIds = "");

internal sealed record ExperimentPackInput(string Name, string Version, string TurnBudget);

internal sealed record ExperimentRubricInput(string Name, string Version, string Criterion);

internal sealed record ExperimentRubricObservationInput(
    string Source,
    string Score,
    string ScoreB,
    string SubjectA,
    string SubjectB,
    bool Unavailable,
    bool Pairwise,
    string Preference);

internal sealed record ExperimentClaimLedgerInput(string ExperimentId, string BranchId);

/// <summary>
/// Hosts independently registered Experiment Lab features. Only backed features
/// are registered; later inspectors can add their own controls without changing
/// shell navigation or this control's layout contract.
/// </summary>
public partial class ExperimentLabControl : UserControl
{
    internal const double CompactLayoutThreshold = 760;
    private readonly ObservableCollection<ExperimentLabFeatureRegistration> features = [];
    private readonly Dictionary<string, ExperimentLabFeatureRegistration> featureByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> featureRefreshSummaryByKey = new(StringComparer.Ordinal);
    private ExperimentLabCoordinator? coordinator;
    private bool matrixRunning;
    private bool operationBusy;
    private bool featureSelectionRefreshing;
    private bool matrixUiInitialized;

    public ExperimentLabControl()
    {
        InitializeComponent();
        var featureView = CollectionViewSource.GetDefaultView(features);
        if (featureView.CanGroup)
        {
            featureView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ExperimentLabFeatureRegistration.Group)));
        }
        FeatureSelector.ItemsSource = featureView;
        RegisterBuiltInFeatures();
        if (features.Count > 0)
        {
            FeatureSelector.SelectedIndex = 0;
        }
        matrixUiInitialized = true;
    }

    internal IReadOnlyList<ExperimentLabFeatureRegistration> RegisteredFeatures => features;

    internal string? SelectedFeatureKey =>
        (FeatureSelector.SelectedItem as ExperimentLabFeatureRegistration)?.Key;

    internal ExperimentLabControlState ReadControlPlaneState()
    {
        var selectedKey = SelectedFeatureKey ?? "";
        var busy = operationBusy || matrixRunning || featureSelectionRefreshing;
        var featureStates = features
            .Select(feature =>
            {
                var selected = string.Equals(feature.Key, selectedKey, StringComparison.Ordinal);
                var featureBusy = busy && selected;
                var refreshSummary = featureRefreshSummaryByKey.GetValueOrDefault(feature.Key, "registered");
                return new ExperimentLabFeatureControlState(
                    feature.Key,
                    feature.Title,
                    Registered: true,
                    Selectable: !operationBusy && !matrixRunning,
                    Busy: featureBusy,
                    Status: featureBusy
                        ? "busy"
                        : refreshSummary == "registered" && selected
                            ? "selected"
                            : refreshSummary);
            })
            .ToArray();
        return new ExperimentLabControlState(selectedKey, busy, featureStates);
    }

    internal bool TrySelectRegisteredFeature(string key, out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(key)
            || !featureByKey.TryGetValue(key.Trim(), out var feature))
        {
            return false;
        }

        changed = !ReferenceEquals(FeatureSelector.SelectedItem, feature);
        FeatureSelector.SelectedItem = feature;
        return true;
    }

    internal bool FocusFeatureSelector() => FeatureSelector.Focus();

    internal void Initialize(ExperimentLabCoordinator value)
    {
        coordinator = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void RegisterFeature(ExperimentLabFeatureRegistration feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (string.IsNullOrWhiteSpace(feature.Key)
            || string.IsNullOrWhiteSpace(feature.Title)
            || string.IsNullOrWhiteSpace(feature.HelpText))
        {
            throw new ArgumentException("Experiment Lab features require stable keys, titles, and help text.", nameof(feature));
        }

        if (!featureByKey.TryAdd(feature.Key, feature))
        {
            throw new InvalidOperationException($"Experiment Lab feature '{feature.Key}' is already registered.");
        }

        featureRefreshSummaryByKey.Add(feature.Key, "registered");

        if (!FeatureContentGrid.Children.Contains(feature.Content))
        {
            if (feature.Content.Parent is not null)
            {
                throw new InvalidOperationException("Experiment Lab feature content already belongs to another visual parent.");
            }

            FeatureContentGrid.Children.Add(feature.Content);
        }

        feature.Content.Visibility = Visibility.Collapsed;
        features.Add(feature);
    }

    internal static bool UsesCompactLayout(double width) =>
        !double.IsNaN(width) && width > 0 && width < CompactLayoutThreshold;

    internal bool ClaimLedgerUsesStackedLayout => Grid.GetRow(ClaimDetailPane) == 2;

    internal ExperimentMatrixInput ReadMatrixInput()
    {
        var benchmark = MatrixBenchmarkPicker.SelectedItem as ExperimentBenchmarkSelection;
        return new(
            MatrixTitleText.Text,
            MatrixScenarioPackText.Text,
            MatrixProvidersText.Text,
            SelectedTag(MatrixDimensionParameterPicker, ""),
            MatrixDimensionValuesText.Text,
            MatrixRepetitionsText.Text,
            MatrixTurnBudgetText.Text,
            MatrixParallelismText.Text,
            benchmark?.BenchmarkPackId ?? "",
            MatrixRubricsText.Text);
    }

    internal ExperimentPackInput ReadPackInput() =>
        new(PackNameText.Text, PackVersionText.Text, PackTurnBudgetText.Text);

    internal ExperimentRubricInput ReadRubricInput() =>
        new(RubricNameText.Text, RubricVersionText.Text, RubricCriterionText.Text);

    internal ExperimentRubricObservationInput ReadRubricObservationInput() => new(
        SelectedTag(RubricSourcePicker, "human"),
        RubricScoreText.Text,
        RubricScoreBText.Text,
        RubricSubjectAText.Text,
        RubricSubjectBText.Text,
        RubricUnavailableCheckBox.IsChecked == true,
        RubricPairwiseCheckBox.IsChecked == true,
        SelectedTag(RubricPreferencePicker, "A"));

    internal ExperimentClaimLedgerInput ReadClaimLedgerInput() =>
        new(ClaimExperimentText.Text, ClaimBranchText.Text);

    internal string ClaimSummary => ClaimSummaryText.Text;

    internal object? SelectedForkCursor => ForkCursorPicker.SelectedItem;
    internal object? SelectedPack => PackItems.SelectedItem;
    internal object? SelectedRubric => RubricPicker.SelectedItem;
    internal object? SelectedLedger => ClaimLedgerPicker.SelectedItem;
    internal object? SelectedClaimMessage => ClaimMessagePicker.SelectedItem;
    internal IReadOnlyList<object> SelectedClaims => ClaimItems.SelectedItems.Cast<object>().ToArray();
    internal string ForkTargetId => ForkTargetText.Text;

    internal bool ConsumeMatrixRetryApproval()
    {
        var approved = MatrixRetryApprovedCheckBox.IsChecked == true;
        MatrixRetryApprovedCheckBox.IsChecked = false;
        return approved;
    }

    internal void SetMatrixRetryApproval(bool approved) => MatrixRetryApprovedCheckBox.IsChecked = approved;

    internal void SetMatrixStatus(string value) => MatrixStatusText.Text = value;
    internal void SetForkStatus(string value) => ForkStatusText.Text = value;
    internal void SetPackStatus(string value) => PackStatusText.Text = value;
    internal void SetRubricStatus(string value) => RubricStatusText.Text = value;
    internal void SetClaimStatus(string value) => ClaimStatusText.Text = value;
    internal void SetForkSession(string value) => ForkSessionText.Text = value;

    internal void SetFeatureRefreshSummary(string key, string summary)
    {
        if (!featureByKey.ContainsKey(key))
        {
            return;
        }

        if (summary is not ("refreshing" or "ready" or "refresh-failed" or "superseded"))
        {
            throw new ArgumentOutOfRangeException(nameof(summary), summary, "Unknown safe feature refresh summary.");
        }

        featureRefreshSummaryByKey[key] = summary;
        featureByKey[key].RefreshStatus = summary;
    }

    internal void ShowBlindJudgeView(ArenaBlindPairwiseJudgeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        BlindJudgeViewText.Text = $"A · {view.LabelAToken}\n{view.LabelAContent}\n\nB · {view.LabelBToken}\n{view.LabelBContent}";
        BlindJudgeView.Visibility = Visibility.Visible;
        SubjectAIdentityPanel.Visibility = Visibility.Collapsed;
        SubjectBIdentityPanel.Visibility = Visibility.Collapsed;
        RubricPicker.IsEnabled = false;
        RubricSourcePicker.IsEnabled = false;
        RubricPairwiseCheckBox.IsEnabled = false;
        BeginBlindPairwiseButton.IsEnabled = false;
        SubmitBlindPairwiseButton.IsEnabled = true;
        CancelBlindPairwiseButton.IsEnabled = true;
        RecordRubricObservationButton.IsEnabled = false;
        RunProviderJudgeButton.IsEnabled = false;
    }

    internal void ResetBlindJudgeView()
    {
        BlindJudgeViewText.Text = "";
        BlindJudgeView.Visibility = Visibility.Collapsed;
        SubjectAIdentityPanel.Visibility = Visibility.Visible;
        SubjectBIdentityPanel.Visibility = Visibility.Visible;
        RubricPicker.IsEnabled = true;
        RubricSourcePicker.IsEnabled = true;
        RubricPairwiseCheckBox.IsEnabled = true;
        BeginBlindPairwiseButton.IsEnabled = RubricPairwiseCheckBox.IsChecked == true;
        SubmitBlindPairwiseButton.IsEnabled = false;
        CancelBlindPairwiseButton.IsEnabled = false;
        RecordRubricObservationButton.IsEnabled = RubricPairwiseCheckBox.IsChecked != true;
        RunProviderJudgeButton.IsEnabled = RubricPairwiseCheckBox.IsChecked != true;
    }

    internal bool BlindIdentityInputsVisible =>
        SubjectAIdentityPanel.Visibility == Visibility.Visible
        && SubjectBIdentityPanel.Visibility == Visibility.Visible;

    internal string BlindJudgeSummary => BlindJudgeViewText.Text;
    internal string RubricStatus => RubricStatusText.Text;
    internal bool ProviderJudgeRunning => CancelProviderJudgeButton.Visibility == Visibility.Visible;
    internal bool ProviderJudgeCancelAvailable => CancelProviderJudgeButton.IsEnabled && ProviderJudgeRunning;
    internal bool ProviderJudgeRunAvailable => RunProviderJudgeButton.IsEnabled && !ProviderJudgeRunning;

    internal void SetProviderJudgeInput(string source, string subjectReferenceId)
    {
        RubricSourcePicker.SelectedItem = RubricSourcePicker.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), source, StringComparison.OrdinalIgnoreCase));
        RubricSubjectAText.Text = subjectReferenceId;
        RubricUnavailableCheckBox.IsChecked = false;
        RubricPairwiseCheckBox.IsChecked = false;
    }

    internal void SetProviderJudgeRunning(bool running)
    {
        var pendingBlind = BlindJudgeView.Visibility == Visibility.Visible;
        FeatureSelector.IsEnabled = !running;
        RubricPicker.IsEnabled = !running && !pendingBlind;
        RubricNameText.IsEnabled = !running;
        RubricVersionText.IsEnabled = !running;
        RubricCriterionText.IsEnabled = !running;
        SaveRubricButton.IsEnabled = !running;
        RubricSourcePicker.IsEnabled = !running && !pendingBlind;
        RubricScoreText.IsEnabled = !running;
        RubricScoreBText.IsEnabled = !running;
        SubjectAIdentityPanel.IsEnabled = !running;
        SubjectBIdentityPanel.IsEnabled = !running;
        RubricUnavailableCheckBox.IsEnabled = !running;
        RubricPairwiseCheckBox.IsEnabled = !running && !pendingBlind;
        RecordRubricObservationButton.IsEnabled = !running && !pendingBlind && RubricPairwiseCheckBox.IsChecked != true;
        RunProviderJudgeButton.IsEnabled = !running && !pendingBlind && RubricPairwiseCheckBox.IsChecked != true;
        RefreshRubricsButton.IsEnabled = !running;
        BeginBlindPairwiseButton.IsEnabled = !running && !pendingBlind && RubricPairwiseCheckBox.IsChecked == true;
        SubmitBlindPairwiseButton.IsEnabled = !running && pendingBlind;
        CancelBlindPairwiseButton.IsEnabled = !running && pendingBlind;
        CancelProviderJudgeButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelProviderJudgeButton.IsEnabled = running;
    }

    internal void SetProviderJudgeCommitting()
    {
        CancelProviderJudgeButton.IsEnabled = false;
        CancelProviderJudgeButton.Visibility = Visibility.Collapsed;
    }

    internal void SetMatrixPreview(IEnumerable<string> values)
    {
        var items = values.ToArray();
        MatrixPreviewItems.ItemsSource = items;
        var empty = items.Length == 0;
        MatrixPreviewItems.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        MatrixPreviewEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MatrixPreviewStateText.Text = empty ? "Empty" : $"{items.Length} expansion row(s)";
        MatrixPreviewEmptyText.Text = "Validate the current configuration to generate a bounded expansion preview.";
        AutomationProperties.SetItemStatus(MatrixPreviewItems, empty ? "empty" : "ready");
    }

    internal void SetRunHistory(IEnumerable<string> values)
    {
        var items = values.ToArray();
        RunHistoryItems.ItemsSource = items;
        var empty = items.Length == 0;
        RunHistoryItems.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RunHistoryEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RunHistoryStateText.Text = empty ? "Empty" : $"{items.Length} persisted run(s)";
        RunHistoryEmptyText.Text = "No persisted experiment runs are available yet.";
        AutomationProperties.SetItemStatus(RunHistoryItems, empty ? "empty" : "ready");
    }

    internal void SetRunHistoryRefreshing()
    {
        var hasRows = RunHistoryItems.Items.Count > 0;
        RunHistoryStateText.Text = hasRows ? "Revalidating" : "Loading";
        RunHistoryItems.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        RunHistoryEmptyState.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        RunHistoryEmptyText.Text = hasRows
            ? ""
            : "Loading persisted experiment run history…";
        AutomationProperties.SetItemStatus(RunHistoryItems, hasRows ? "revalidating" : "loading");
    }

    internal void SetRunHistoryRefreshFailed()
    {
        var hasRows = RunHistoryItems.Items.Count > 0;
        RunHistoryStateText.Text = "Failed";
        RunHistoryItems.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        RunHistoryEmptyState.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        RunHistoryEmptyText.Text = hasRows
            ? ""
            : "Run history could not be refreshed. Existing persisted evidence was not reinterpreted.";
        AutomationProperties.SetItemStatus(RunHistoryItems, "failed");
    }
    internal void SetMatrixBenchmarks(IEnumerable<object> values) => SetItems(MatrixBenchmarkPicker, values);
    internal void SetForkCursors(IEnumerable<object> values) => SetItems(ForkCursorPicker, values);
    internal void SetPacks(IEnumerable<object> values) => SetItems(PackItems, values);
    internal void SetRubrics(IEnumerable<object> values) => SetItems(RubricPicker, values);
    internal void SetRubricResults(IEnumerable<string> values) => RubricResultItems.ItemsSource = values.ToArray();
    internal void SetClaimLedgers(IEnumerable<object> values) => SetItems(ClaimLedgerPicker, values);
    internal void SetClaimMessages(IEnumerable<object> values) => SetItems(ClaimMessagePicker, values);
    internal void SetClaims(IEnumerable<object> values) => SetItems(ClaimItems, values);

    internal void SetBusy(bool busy)
    {
        operationBusy = busy;
        FeatureSelector.IsEnabled = !busy && !matrixRunning;
        FeatureContentGrid.IsEnabled = !busy && !featureSelectionRefreshing;
        ExperimentWorkspaceHeader.IsPrimaryActionEnabled = !busy && !matrixRunning && !featureSelectionRefreshing;
    }

    internal void SetFeatureSelectionRefreshing(bool refreshing)
    {
        featureSelectionRefreshing = refreshing;
        // Selection refreshes are cancellable. Keep navigation enabled so a
        // newer pointer or keyboard selection can supersede a slow refresh.
        FeatureSelector.IsEnabled = !operationBusy && !matrixRunning;
        FeatureContentGrid.IsEnabled = !operationBusy && !refreshing;
        ExperimentWorkspaceHeader.IsPrimaryActionEnabled = !operationBusy && !matrixRunning && !refreshing;
    }

    internal void ReconcileMatrixProviderProfiles(
        IReadOnlyCollection<string> availableProfileIds,
        bool preserveCurrentInput = false)
    {
        if (preserveCurrentInput)
        {
            return;
        }

        var available = availableProfileIds.ToHashSet(StringComparer.Ordinal);
        var current = MatrixProvidersText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(available.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        MatrixProvidersText.Text = string.Join(", ", current.Length > 0 ? current : available.Order(StringComparer.Ordinal).Take(1));
    }

    internal void SetMatrixScenarioPackId(string packId) => MatrixScenarioPackText.Text = packId;

    internal void SetMatrixRubricIds(string rubricIds) => MatrixRubricsText.Text = rubricIds;

    internal void SetMatrixDefinition(ArenaExperimentContract definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Dimensions.Length > 1
            || !definition.FaultProfileIds.IsEmpty
            || !definition.BranchIds.IsEmpty)
        {
            throw new InvalidDataException("The v1 Matrix Runner UI cannot restore this definition without dropping execution inputs.");
        }

        MatrixTitleText.Text = definition.Title;
        MatrixProvidersText.Text = string.Join(", ", definition.ProviderProfileIds);
        MatrixRepetitionsText.Text = definition.Repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MatrixTurnBudgetText.Text = definition.TurnBudget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MatrixParallelismText.Text = definition.MaxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dimension = definition.Dimensions.SingleOrDefault();
        var parameter = dimension?.Parameter ?? "";
        MatrixDimensionParameterPicker.SelectedItem = MatrixDimensionParameterPicker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), parameter, StringComparison.Ordinal));
        MatrixDimensionValuesText.Text = dimension is null ? "" : string.Join(", ", dimension.Values);
        SelectMatrixBenchmark(definition.BenchmarkPackId);
        // Benchmark selection intentionally runs first; these persisted values
        // then remain the authority even if a referenced pack has changed.
        MatrixScenarioPackText.Text = definition.ScenarioPackId;
        MatrixRubricsText.Text = string.Join(", ", definition.RubricIds);
    }

    internal void ReconcileMatrixRubricIds(
        IReadOnlyCollection<string> availableRubricIds,
        bool preserveCurrentInput = false)
    {
        if (preserveCurrentInput)
        {
            return;
        }

        if (MatrixBenchmarkPicker.SelectedItem is ExperimentBenchmarkSelection { RubricIds.IsEmpty: false } benchmark)
        {
            MatrixRubricsText.Text = string.Join(", ", benchmark.RubricIds);
            return;
        }

        var available = availableRubricIds.ToHashSet(StringComparer.Ordinal);
        var current = MatrixRubricsText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(available.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        MatrixRubricsText.Text = string.Join(", ", current.Length > 0 ? current : available.Order(StringComparer.Ordinal).Take(1));
    }

    internal void SelectMatrixBenchmark(string? benchmarkPackId)
    {
        var selected = MatrixBenchmarkPicker.Items
            .OfType<ExperimentBenchmarkSelection>()
            .FirstOrDefault(item => string.Equals(item.BenchmarkPackId, benchmarkPackId, StringComparison.Ordinal));
        if (selected is null && benchmarkPackId is not null)
        {
            var items = MatrixBenchmarkPicker.Items.Cast<object>()
                .Append(new ExperimentBenchmarkSelection(
                    $"Unavailable durable benchmark · {benchmarkPackId}",
                    benchmarkPackId,
                    null,
                    []))
                .ToArray();
            SetItems(MatrixBenchmarkPicker, items);
            selected = MatrixBenchmarkPicker.Items
                .OfType<ExperimentBenchmarkSelection>()
                .Single(item => string.Equals(item.BenchmarkPackId, benchmarkPackId, StringComparison.Ordinal));
        }

        MatrixBenchmarkPicker.SelectedItem = selected;
    }

    internal void SetMatrixExecutionAvailability(bool available, string helpText)
    {
        ExecuteMatrixButton.Tag = available;
        ExecuteMatrixButton.IsEnabled = available && !matrixRunning;
        ExecuteMatrixButton.ToolTip = helpText;
        AutomationProperties.SetHelpText(ExecuteMatrixButton, helpText);
        AutomationProperties.SetItemStatus(ExecuteMatrixButton, available ? "available" : "unavailable");
        MatrixValidationStageStatusText.Text = available ? "Ready" : "Unavailable";
        MatrixRunStageStatusText.Text = available ? "Ready" : "Blocked";
        AutomationProperties.SetItemStatus(MatrixValidateStage, available ? "ready" : "unavailable");
        AutomationProperties.SetItemStatus(MatrixRunStage, available ? "ready" : "blocked");
    }

    internal void SetMatrixRunning(bool running)
    {
        matrixRunning = running;
        FeatureSelector.IsEnabled = !running;
        MatrixTitleText.IsEnabled = !running;
        MatrixScenarioPackText.IsEnabled = !running;
        MatrixProvidersText.IsEnabled = !running;
        MatrixRubricsText.IsEnabled = !running;
        MatrixBenchmarkPicker.IsEnabled = !running;
        MatrixDimensionParameterPicker.IsEnabled = !running;
        MatrixDimensionValuesText.IsEnabled = !running;
        MatrixRepetitionsText.IsEnabled = !running;
        MatrixTurnBudgetText.IsEnabled = !running;
        MatrixParallelismText.IsEnabled = !running;
        MatrixRetryApprovedCheckBox.IsEnabled = !running;
        ValidateMatrixButton.IsEnabled = !running;
        RefreshRunHistoryButton.IsEnabled = !running;
        ExecuteMatrixButton.IsEnabled = !running && ExecuteMatrixButton.Tag is true;
        CancelMatrixButton.IsEnabled = running;
        AutomationProperties.SetItemStatus(CancelMatrixButton, running ? "available" : "unavailable");
        MatrixRunStageStatusText.Text = running
            ? "Running"
            : ExecuteMatrixButton.Tag is true
                ? "Ready"
                : "Blocked";
        AutomationProperties.SetItemStatus(MatrixRunStage, running ? "running" : ExecuteMatrixButton.Tag is true ? "ready" : "blocked");
        ExperimentWorkspaceHeader.IsPrimaryActionEnabled = !running && !operationBusy && !featureSelectionRefreshing;
    }

    private void RegisterBuiltInFeatures()
    {
        RegisterFeature(new(
            "matrix",
            "Matrix Runner",
            "Deterministic expansion",
            "Validate bounded experiment matrices and inspect durable run history.",
            MatrixPanel,
            "Run & replay"));
        RegisterFeature(new(
            "fork",
            "Conversation Fork",
            "Exact historical cursor",
            "Create and load an isolated child session at a stable transcript cursor.",
            ForkPanel,
            "Run & replay"));
        RegisterFeature(new(
            "packs",
            "Scenario Packs",
            "Canonical local artifacts",
            "Create, import, export, and diagnose scenario and benchmark packs.",
            PacksPanel,
            "Definitions & evidence"));
        RegisterFeature(new(
            "rubrics",
            "Rubric Studio",
            "Separated judgments",
            "Version rubrics and record human, deterministic, model, or unavailable evidence.",
            RubricPanel,
            "Definitions & evidence"));
        RegisterFeature(new(
            "claims",
            "Claim Ledger",
            "Evidence provenance",
            "Add, review, and contradict claims through monotonic stable references.",
            ClaimPanel,
            "Definitions & evidence"));
    }

    private void FeatureSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeatureSelector.SelectedItem is not ExperimentLabFeatureRegistration selected)
        {
            return;
        }

        foreach (var feature in features)
        {
            feature.Content.Visibility = ReferenceEquals(feature, selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        bool AnimateSelectedContainer()
        {
            if (ReferenceEquals(FeatureSelector.SelectedItem, selected)
                && FeatureSelector.ItemContainerGenerator.ContainerFromItem(selected) is UIElement container)
            {
                ArenaMotion.NavigationSelected(container);
                return true;
            }

            return false;
        }

        if (!AnimateSelectedContainer())
        {
            Dispatcher.BeginInvoke(
                (Action)(() => AnimateSelectedContainer()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (coordinator is not null)
        {
            coordinator.RequestFeatureSelectionRefresh(selected.Key);
        }
    }

    private void RefreshSelectedFeatureHeader_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator is not null
            && FeatureSelector.SelectedItem is ExperimentLabFeatureRegistration selected
            && ExperimentWorkspaceHeader.IsPrimaryActionEnabled)
        {
            coordinator.RequestFeatureSelectionRefresh(selected.Key);
        }
    }

    private void ExperimentLabControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(UsesCompactLayout(e.NewSize.Width));

    internal void ApplyResponsiveLayout(bool compact)
    {
        ApplyClaimLedgerLayout(compact);
        if (compact)
        {
            SelectorColumn.Width = new GridLength(1, GridUnitType.Star);
            WideGapColumn.Width = new GridLength(0);
            ContentColumn.Width = new GridLength(0);
            SelectorRow.Height = GridLength.Auto;
            CompactGapRow.Height = new GridLength(10);
            ContentRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(FeatureSelectorFrame, 0);
            Grid.SetColumn(FeatureSelectorFrame, 0);
            Grid.SetColumnSpan(FeatureSelectorFrame, 3);
            Grid.SetRow(FeatureContentGrid, 2);
            Grid.SetColumn(FeatureContentGrid, 0);
            Grid.SetColumnSpan(FeatureContentGrid, 3);
            FeatureSelector.MaxHeight = 126;
        }
        else
        {
            SelectorColumn.Width = new GridLength(190);
            WideGapColumn.Width = new GridLength(12);
            ContentColumn.Width = new GridLength(1, GridUnitType.Star);
            SelectorRow.Height = new GridLength(1, GridUnitType.Star);
            CompactGapRow.Height = new GridLength(0);
            ContentRow.Height = new GridLength(0);
            Grid.SetRow(FeatureSelectorFrame, 0);
            Grid.SetColumn(FeatureSelectorFrame, 0);
            Grid.SetColumnSpan(FeatureSelectorFrame, 1);
            Grid.SetRow(FeatureContentGrid, 0);
            Grid.SetColumn(FeatureContentGrid, 2);
            Grid.SetColumnSpan(FeatureContentGrid, 1);
            FeatureSelector.ClearValue(MaxHeightProperty);
        }
    }

    private void ApplyClaimLedgerLayout(bool stacked)
    {
        if (stacked)
        {
            ClaimMasterColumn.Width = new GridLength(1, GridUnitType.Star);
            ClaimWideGapColumn.Width = new GridLength(0);
            ClaimDetailColumn.Width = new GridLength(0);
            ClaimMasterRow.Height = GridLength.Auto;
            ClaimCompactGapRow.Height = new GridLength(12);
            ClaimDetailRow.Height = GridLength.Auto;
            Grid.SetRow(ClaimMasterPane, 0);
            Grid.SetColumn(ClaimMasterPane, 0);
            Grid.SetColumnSpan(ClaimMasterPane, 3);
            Grid.SetRow(ClaimDetailPane, 2);
            Grid.SetColumn(ClaimDetailPane, 0);
            Grid.SetColumnSpan(ClaimDetailPane, 3);
            return;
        }

        ClaimMasterColumn.Width = new GridLength(260);
        ClaimWideGapColumn.Width = new GridLength(12);
        ClaimDetailColumn.Width = new GridLength(1, GridUnitType.Star);
        ClaimMasterRow.Height = GridLength.Auto;
        ClaimCompactGapRow.Height = new GridLength(0);
        ClaimDetailRow.Height = new GridLength(0);
        Grid.SetRow(ClaimMasterPane, 0);
        Grid.SetColumn(ClaimMasterPane, 0);
        Grid.SetColumnSpan(ClaimMasterPane, 1);
        Grid.SetRow(ClaimDetailPane, 0);
        Grid.SetColumn(ClaimDetailPane, 2);
        Grid.SetColumnSpan(ClaimDetailPane, 1);
    }

    private async void ValidateMatrixButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.ValidateMatrixAsync());

    private void MatrixConfiguration_TextChanged(object sender, TextChangedEventArgs e) =>
        InvalidateMatrixValidation();

    private void MatrixConfiguration_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        InvalidateMatrixValidation();

    private void InvalidateMatrixValidation()
    {
        if (!matrixUiInitialized || matrixRunning)
        {
            return;
        }

        ExecuteMatrixButton.Tag = false;
        ExecuteMatrixButton.IsEnabled = false;
        const string helpText = "Configuration changed. Validate the matrix again before running.";
        ExecuteMatrixButton.ToolTip = helpText;
        AutomationProperties.SetHelpText(ExecuteMatrixButton, helpText);
        AutomationProperties.SetItemStatus(ExecuteMatrixButton, "unavailable");
        MatrixValidationStageStatusText.Text = "Needs validation";
        MatrixRunStageStatusText.Text = "Blocked";
        AutomationProperties.SetItemStatus(MatrixValidateStage, "needs-validation");
        AutomationProperties.SetItemStatus(MatrixRunStage, "blocked");
        MatrixStatusText.Text = helpText;
        SetMatrixPreview([]);
    }

    private async void RefreshRunHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RefreshRunHistoryAsync());

    private void MatrixBenchmarkPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatrixBenchmarkPicker.SelectedItem is ExperimentBenchmarkSelection selection
            && !string.IsNullOrWhiteSpace(selection.ScenarioPackId))
        {
            MatrixScenarioPackText.Text = selection.ScenarioPackId;
            MatrixRubricsText.Text = string.Join(", ", selection.RubricIds);
        }

        InvalidateMatrixValidation();
    }

    private async void ExecuteMatrixButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.ExecuteMatrixAsync());

    private void CancelMatrixButton_Click(object sender, RoutedEventArgs e) =>
        coordinator?.CancelMatrix();

    private async void RefreshForkCursorsButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RefreshForkCursorsAsync());

    private async void CreateForkButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.CreateForkAsync());

    private async void SaveScenarioPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.SaveScenarioPackAsync());

    private async void SaveBenchmarkPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.SaveBenchmarkPackAsync());

    private async void ImportPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.ImportPackAsync());

    private async void ExportPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.ExportSelectedPackAsync());

    private async void RefreshPacksButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RefreshPacksAsync());

    private async void SaveRubricButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.SaveRubricAsync());

    private async void RecordRubricObservationButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RecordRubricObservationAsync());

    private async void RunProviderJudgeButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RunProviderJudgeAsync());

    private void CancelProviderJudgeButton_Click(object sender, RoutedEventArgs e) =>
        coordinator?.CancelProviderJudge();

    private async void BeginBlindPairwiseButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.BeginBlindPairwiseAsync());

    private async void SubmitBlindPairwiseButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.SubmitBlindPairwiseAsync());

    private void CancelBlindPairwiseButton_Click(object sender, RoutedEventArgs e) =>
        coordinator?.CancelBlindPairwise();

    private async void RefreshRubricsButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RefreshRubricsAsync());

    private async void CreateClaimLedgerButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.CreateClaimLedgerAsync());

    private async void RefreshClaimsButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.RefreshClaimsAsync());

    private async void ClaimLedgerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (coordinator is not null)
        {
            await coordinator.RefreshSelectedClaimsAsync();
        }
    }

    private async void AddClaimButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.AddClaimAsync());

    private async void ReviewClaimButton_Click(object sender, RoutedEventArgs e)
    {
        var status = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        await RunAsync(value => value.ReviewSelectedClaimAsync(status));
    }

    private async void LinkContradictionButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(static value => value.LinkSelectedContradictionAsync());

    private void RubricPairwiseCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var pairwise = RubricPairwiseCheckBox.IsChecked == true;
        PairwiseFields.Visibility = pairwise ? Visibility.Visible : Visibility.Collapsed;
        BeginBlindPairwiseButton.IsEnabled = pairwise;
        RecordRubricObservationButton.IsEnabled = !pairwise;
        RunProviderJudgeButton.IsEnabled = !pairwise;
    }

    private async Task RunAsync(Func<ExperimentLabCoordinator, Task> action)
    {
        if (coordinator is null)
        {
            return;
        }

        await action(coordinator);
    }

    private static string SelectedTag(ComboBox picker, string fallback) =>
        (picker.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? fallback;

    private static void SetItems(ItemsControl control, IEnumerable<object> values)
    {
        var selected = (control as Selector)?.SelectedItem;
        var selectedKey = selected?.ToString();
        var items = values.ToArray();
        control.ItemsSource = items;
        if (control is Selector selector)
        {
            selector.SelectedItem = items.FirstOrDefault(item => item.ToString() == selectedKey)
                ?? items.FirstOrDefault();
        }
    }
}
