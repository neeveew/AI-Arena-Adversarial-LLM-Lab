using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.CodeIntelligence;

namespace AIArena.Wpf;

internal sealed partial class AgentImpactExplorerCoordinator : IDisposable
{
    private const int MaxSolutions = 64;
    private const int MaxDirectories = 2_000;
    private const int MaxRenderedDiagnostics = 3;
    private const int MaxRenderedFocusedTests = 8;
    private const int MaxRenderedEvidenceTextLength = 240;
    private const int MaxCopiedItems = 128;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "dist",
        "coverage"
    };

    private readonly Expander expander;
    private readonly TextBlock statusText;
    private readonly ComboBox solutionPicker;
    private readonly TextBox targetText;
    private readonly Button analyzeButton;
    private readonly Button analyzeChangesButton;
    private readonly TextBlock summaryText;
    private readonly StackPanel items;
    private readonly Button stageTestsButton;
    private readonly Button copyButton;
    private readonly Func<string> workspacePath;
    private readonly Func<IReadOnlyList<string>> changedRelativePaths;
    private readonly Action<string, string?> stageCommand;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, string, CancellationToken, Task<ImpactSnapshot>> analyzeSolutionAsync;
    private readonly Func<ImpactSnapshot, ImpactChangeSet, CancellationToken, ImpactPrediction> predict;
    private readonly ImpactQueryService query = new();

    private CancellationTokenSource? analysisCancellation;
    private long analysisGeneration;
    private FocusedTestRecommendation? stagedTest;
    private string copyText = "";
    private string resultWorkspaceRoot = "";
    private bool disposed;

    public AgentImpactExplorerCoordinator(
        Expander expander,
        TextBlock statusText,
        ComboBox solutionPicker,
        TextBox targetText,
        Button analyzeButton,
        Button analyzeChangesButton,
        TextBlock summaryText,
        StackPanel items,
        Button stageTestsButton,
        Button copyButton,
        Func<string> workspacePath,
        Func<IReadOnlyList<string>> changedRelativePaths,
        Action<string, string?> stageCommand,
        Func<string, Brush> resourceBrush,
        Func<string, string, CancellationToken, Task<ImpactSnapshot>>? analyzeSolutionAsync = null,
        Func<ImpactSnapshot, ImpactChangeSet, CancellationToken, ImpactPrediction>? predict = null)
    {
        this.expander = expander;
        this.statusText = statusText;
        this.solutionPicker = solutionPicker;
        this.targetText = targetText;
        this.analyzeButton = analyzeButton;
        this.analyzeChangesButton = analyzeChangesButton;
        this.summaryText = summaryText;
        this.items = items;
        this.stageTestsButton = stageTestsButton;
        this.copyButton = copyButton;
        this.workspacePath = workspacePath;
        this.changedRelativePaths = changedRelativePaths;
        this.stageCommand = stageCommand;
        this.resourceBrush = resourceBrush;

        var explorer = new RoslynImpactExplorer();
        var predictor = new ImpactPredictor();
        this.analyzeSolutionAsync = analyzeSolutionAsync ?? explorer.AnalyzeSolutionAsync;
        this.predict = predict ?? predictor.Predict;

        expander.Expanded += Expander_Expanded;
        analyzeButton.Click += AnalyzeButton_Click;
        analyzeChangesButton.Click += AnalyzeChangesButton_Click;
        stageTestsButton.Click += StageTestsButton_Click;
        copyButton.Click += CopyButton_Click;

        SetStatus("Choose a trusted .NET workspace to inspect semantic change impact.");
        RefreshActions();
    }

    internal string DebugStatus => statusText.Text;

    internal string DebugSummary => summaryText.Text;

    internal string DebugCopyText => copyText;

    internal int DebugRenderedItemCount => items.Children.Count;

    internal Task DebugAnalyzeTargetAsync() => AnalyzeAsync(useLatestChanges: false);

    internal Task DebugAnalyzeChangesAsync() => AnalyzeAsync(useLatestChanges: true);

    internal void DebugRefreshSolutions() => RefreshSolutions();

    internal string DebugStagedCommand => stagedTest is null ? "" : FormatFocusedTestCommand(stagedTest);

    private void Expander_Expanded(object sender, RoutedEventArgs e)
    {
        RefreshSolutions();
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeAsync(useLatestChanges: false);
    }

    private async void AnalyzeChangesButton_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeAsync(useLatestChanges: true);
    }

    private void StageTestsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ResultMatchesCurrentWorkspace())
        {
            DiscardStaleResults();
            return;
        }

        if (stagedTest is null || !TryFormatFocusedTestCommand(stagedTest, out var command))
        {
            SetStatus("No evidence-backed focused test command is available to stage.");
            return;
        }

        stageCommand(command, "PowerShell");
        SetStatus("Focused tests were staged in Command Approval. Review the exact command, then approve it explicitly.");
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ResultMatchesCurrentWorkspace())
        {
            DiscardStaleResults();
            return;
        }

        if (string.IsNullOrWhiteSpace(copyText))
        {
            SetStatus("No Impact Explorer evidence is available to copy.");
            return;
        }

        SetStatus(AgentWorkspaceCoordinator.TrySetClipboardText(copyText)
            ? "Relative-path-only Impact Explorer evidence copied."
            : "Clipboard is unavailable. Try copying Impact Explorer evidence again.");
    }

    private async Task AnalyzeAsync(bool useLatestChanges)
    {
        if (!TryWorkspace(out var root))
        {
            SetStatus("Choose an existing workspace before running Impact Explorer.");
            return;
        }

        RefreshSolutions();
        if (solutionPicker.SelectedItem is not string solutionRelativePath
            || !IsSafeRelativePath(solutionRelativePath))
        {
            SetStatus("No trusted solution is available inside this workspace.");
            return;
        }

        ImpactChangeSet? changes;
        string target = "";
        string targetLabel;
        if (useLatestChanges)
        {
            var paths = changedRelativePaths()
                .Select(NormalizeRelativePath)
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                SetStatus("No bounded Agent file receipt is available. Run work first or analyze a symbol or relative file.");
                return;
            }

            changes = new ImpactChangeSet(paths);
            targetLabel = $"{paths.Length} latest changed file(s)";
        }
        else
        {
            target = targetText.Text.Trim();
            if (target.Length == 0)
            {
                SetStatus("Enter a symbol or workspace-relative file before analyzing impact.");
                return;
            }
            if (target.Length > 1_000
                || target.Any(char.IsControl)
                || Path.IsPathRooted(target)
                || AbsolutePathRegex().IsMatch(target))
            {
                SetStatus("Impact targets must be bounded symbol names or workspace-relative paths.");
                return;
            }

            changes = null;
            targetLabel = target;
        }

        analysisCancellation?.Cancel();
        analysisCancellation?.Dispose();
        analysisCancellation = new CancellationTokenSource();
        var cancellationToken = analysisCancellation.Token;
        var generation = Interlocked.Increment(ref analysisGeneration);
        var capturedRoot = root;
        ClearResults();
        SetBusy(true);
        SetStatus("Evaluating the explicitly selected trusted solution with Roslyn and MSBuild…");

        try
        {
            var snapshot = await analyzeSolutionAsync(
                root,
                Path.Combine(root, solutionRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await InvokeOnUiAsync(() => IsCurrent(generation, capturedRoot)).ConfigureAwait(false))
            {
                return;
            }

            var computation = await Task.Run(
                    () => ComputePrediction(
                        snapshot,
                        changes,
                        useLatestChanges,
                        target,
                        targetLabel,
                        cancellationToken),
                    cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (!IsCurrent(generation, capturedRoot))
                {
                    return;
                }

                if (computation.Prediction is null)
                {
                    Render(snapshot, null, computation.TargetLabel, capturedRoot);
                    SetStatus("The semantic index loaded, but the requested symbol or relative file was not found.");
                    return;
                }

                Render(snapshot, computation.Prediction, computation.TargetLabel, capturedRoot);
                SetStatus(StatusFor(snapshot, computation.Prediction));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InvokeOnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref analysisGeneration))
                {
                    SetStatus("Impact analysis was cancelled.");
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            await InvokeOnUiAsync(() =>
            {
                if (IsCurrent(generation, capturedRoot))
                {
                    ClearResults();
                    SetStatus("Impact analysis failed safely. No impact or test-coverage claim was produced.");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await InvokeOnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref analysisGeneration))
                {
                    if (!IsCurrent(generation, capturedRoot))
                    {
                        ClearResults();
                        SetStatus("The workspace changed, so stale Impact Explorer evidence was discarded.");
                    }
                    SetBusy(false);
                }
            }).ConfigureAwait(false);
        }
    }

    private AnalysisComputation ComputePrediction(
        ImpactSnapshot snapshot,
        ImpactChangeSet? changes,
        bool useLatestChanges,
        string target,
        string targetLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!useLatestChanges)
        {
            var resolution = ResolveTarget(snapshot, target);
            if (resolution is null)
            {
                return new AnalysisComputation(null, targetLabel);
            }

            changes = resolution.Changes;
            if (resolution.MatchCount > 1)
            {
                targetLabel = $"{targetLabel} ({resolution.MatchCount} indexed matches)";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var prediction = predict(snapshot, changes!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new AnalysisComputation(prediction, targetLabel);
    }

    private TargetResolution? ResolveTarget(ImpactSnapshot snapshot, string target)
    {
        var relative = NormalizeRelativePath(target);
        var looksLikePath = target.Contains('/')
            || target.Contains('\\')
            || target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        if (looksLikePath && relative is not null)
        {
            var matchedNodes = snapshot.Nodes.Count(node =>
                node.Definitions.Any(definition =>
                    definition.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase))
                || node.QualifiedName.Equals(relative, StringComparison.OrdinalIgnoreCase));
            return matchedNodes > 0
                ? new TargetResolution(new ImpactChangeSet([relative]), matchedNodes)
                : null;
        }

        var exactCandidates = snapshot.Nodes
            .Where(node =>
                node.QualifiedName.Equals(target, StringComparison.Ordinal)
                || node.QualifiedName.Equals(target, StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals(target, StringComparison.Ordinal)
                || node.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var candidates = exactCandidates.Length > 0
            ? exactCandidates
            : query.SearchSymbols(snapshot, target, limit: 20)
                .Select(node => node.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        return candidates.Length == 0
            ? null
            : new TargetResolution(new ImpactChangeSet([], candidates), candidates.Length);
    }

    private void Render(
        ImpactSnapshot snapshot,
        ImpactPrediction? prediction,
        string targetLabel,
        string capturedRoot)
    {
        items.Children.Clear();
        resultWorkspaceRoot = capturedRoot;
        stagedTest = prediction?.FocusedTests.FirstOrDefault(item => TryFormatFocusedTestCommand(item, out _));

        var definitions = snapshot.Nodes.Count(node =>
            node.Kind is ImpactNodeKind.Type
                or ImpactNodeKind.Method
                or ImpactNodeKind.Constructor
                or ImpactNodeKind.Property
                or ImpactNodeKind.Field
                or ImpactNodeKind.Event);
        var references = Count(snapshot, ImpactRelationshipKind.References);
        var calls = Count(snapshot, ImpactRelationshipKind.Calls);
        var inheritance = Count(snapshot, ImpactRelationshipKind.Inherits)
            + Count(snapshot, ImpactRelationshipKind.Implements);
        summaryText.Text = prediction is null
            ? $"{AvailabilityLabel(snapshot.Availability)} semantic index: {definitions} symbol definitions and {snapshot.Relationships.Count} proven relationships."
            : $"{AvailabilityLabel(prediction.Availability)} prediction for {targetLabel}: {prediction.AffectedFiles.Count} likely file(s), {prediction.AffectedFeatures.Count} feature area(s), and {prediction.FocusedTests.Count} focused test recommendation(s).";

        items.Children.Add(CreateEvidenceCard(
            "Symbols",
            $"{definitions} definitions • {references} references • {calls} caller/callee links • {inheritance} inheritance/interface links",
            "PrimaryBorderBrush"));

        var packageNodes = snapshot.Nodes.Count(node => node.Kind == ImpactNodeKind.Package);
        items.Children.Add(CreateEvidenceCard(
            "Projects & packages",
            $"{snapshot.Projects.Count} projects • {Count(snapshot, ImpactRelationshipKind.ProjectReferences)} project links • {packageNodes} packages • {Count(snapshot, ImpactRelationshipKind.PackageReferences)} package links",
            "ControlBorderBrush"));

        var xamlFiles = snapshot.Nodes.Count(node => node.Kind == ImpactNodeKind.XamlFile);
        var xamlResources = snapshot.Nodes.Count(node => node.Kind == ImpactNodeKind.XamlResource);
        items.Children.Add(CreateEvidenceCard(
            "XAML",
            $"{xamlFiles} files • {xamlResources} resources • {Count(snapshot, ImpactRelationshipKind.XamlCodeBehind)} code-behind links • {Count(snapshot, ImpactRelationshipKind.XamlUsesResource)} resource uses",
            "ControlBorderBrush"));

        var tests = snapshot.Nodes.Count(node => node.IsTest || node.Kind == ImpactNodeKind.Test);
        items.Children.Add(CreateEvidenceCard(
            "Tests",
            $"{tests} indexed test symbol(s) • {Count(snapshot, ImpactRelationshipKind.Tests)} static test relationship(s). Static relationships are not runtime coverage.",
            "AssistBorderBrush"));

        if (prediction is not null)
        {
            items.Children.Add(CreateEvidenceCard(
                "Selected definitions",
                FormatSelectedDefinitions(snapshot, prediction),
                "PrimaryBorderBrush"));
            items.Children.Add(CreateEvidenceCard(
                "Selected relationships",
                FormatSelectedRelationships(snapshot, prediction),
                "PrimaryBorderBrush"));
            items.Children.Add(CreateEvidenceCard(
                "Affected files",
                FormatAffectedFiles(prediction.AffectedFiles),
                prediction.Availability == ImpactAvailability.Available
                    ? "PrimaryBorderBrush"
                    : "Arena.Brush.Warning"));
            items.Children.Add(CreateEvidenceCard(
                "Affected features",
                prediction.AffectedFeatures.Count == 0
                    ? "No feature area was evidenced by the bounded semantic traversal."
                    : string.Join(" • ", prediction.AffectedFeatures.Take(8).Select(feature =>
                        $"{feature.Name} (distance {feature.NearestDistance})")),
                "ControlBorderBrush"));
            items.Children.Add(CreateEvidenceCard(
                "Focused-test evidence",
                FormatTestEvidence(prediction),
                prediction.ProductionTestEvidence.Any(item =>
                    item.State == StaticTestEvidenceState.NoStaticEvidence)
                    ? "Arena.Brush.Warning"
                    : "AssistBorderBrush"));
        }

        foreach (var diagnostic in snapshot.Diagnostics
                     .Concat(prediction?.Diagnostics ?? [])
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.Code, StringComparer.Ordinal)
                     .Take(MaxRenderedDiagnostics))
        {
            items.Children.Add(CreateEvidenceCard(
                $"Diagnostic: {diagnostic.Code}",
                SafeDiagnosticMessage(diagnostic.Message),
                diagnostic.Severity == ImpactDiagnosticSeverity.Error
                    ? "DangerBorderBrush"
                    : "Arena.Brush.Warning"));
        }

        copyText = BuildCopyText(snapshot, prediction, targetLabel);
        RefreshActions();
    }

    private Border CreateEvidenceCard(string title, string detail, string borderResourceKey)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        var card = new Border
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = resourceBrush(borderResourceKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = panel
        };
        AutomationProperties.SetName(card, $"Impact Explorer {title}");
        AutomationProperties.SetHelpText(card, detail);
        return card;
    }

    private void RefreshSolutions()
    {
        var previous = solutionPicker.SelectedItem as string;
        solutionPicker.Items.Clear();
        if (!TryWorkspace(out var root))
        {
            if (!string.IsNullOrEmpty(resultWorkspaceRoot))
            {
                ClearResults();
            }
            RefreshActions();
            return;
        }
        if (!string.IsNullOrEmpty(resultWorkspaceRoot)
            && !resultWorkspaceRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            ClearResults();
            SetStatus("The workspace changed, so stale Impact Explorer evidence was discarded.");
        }

        foreach (var solution in FindSolutions(root))
        {
            solutionPicker.Items.Add(solution);
        }

        if (previous is not null && solutionPicker.Items.Contains(previous))
        {
            solutionPicker.SelectedItem = previous;
        }
        else if (solutionPicker.Items.Count > 0)
        {
            solutionPicker.SelectedIndex = 0;
        }

        RefreshActions();
    }

    internal static IReadOnlyList<string> FindSolutions(string root)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch
        {
            return [];
        }

        if (!Directory.Exists(fullRoot))
        {
            return [];
        }

        var results = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(fullRoot);
        var visited = 0;
        while (pending.Count > 0 && results.Count < MaxSolutions && visited < MaxDirectories)
        {
            var directory = pending.Dequeue();
            visited++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory)
                             .Where(file =>
                                 file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                 || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = NormalizeRelativePath(Path.GetRelativePath(fullRoot, file));
                    if (relative is not null)
                    {
                        results.Add(relative);
                        if (results.Count >= MaxSolutions)
                        {
                            break;
                        }
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new DirectoryInfo(child);
                    if (SkippedDirectories.Contains(info.Name)
                        || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    pending.Enqueue(child);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
            {
                // A bounded discovery failure only omits the inaccessible branch.
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string FormatFocusedTestCommand(FocusedTestRecommendation recommendation)
    {
        return TryFormatFocusedTestCommand(recommendation, out var command) ? command : "";
    }

    internal static bool TryFormatFocusedTestCommand(
        FocusedTestRecommendation recommendation,
        out string command)
    {
        command = "";
        var arguments = recommendation.FileNameAndArguments;
        if (arguments.Count is < 2 or > 8
            || !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || !IsSafeRelativePath(recommendation.ProjectRelativePath)
            || arguments.Any(argument =>
                string.IsNullOrWhiteSpace(argument)
                || argument.Length > 2_000
                || argument.Any(char.IsControl)))
        {
            return false;
        }

        var isConventionalTest = arguments.Count == 6
            && arguments[1].Equals("test", StringComparison.Ordinal)
            && arguments[2].Equals(recommendation.ProjectRelativePath, StringComparison.OrdinalIgnoreCase)
            && arguments[3].Equals("--no-restore", StringComparison.Ordinal)
            && arguments[4].Equals("--filter", StringComparison.Ordinal)
            && arguments[5].StartsWith("FullyQualifiedName=", StringComparison.Ordinal)
            && arguments[5].Length > "FullyQualifiedName=".Length;
        var isExecutableHarness = arguments.Count == 5
            && arguments[1].Equals("run", StringComparison.Ordinal)
            && arguments[2].Equals("--project", StringComparison.Ordinal)
            && arguments[3].Equals(recommendation.ProjectRelativePath, StringComparison.OrdinalIgnoreCase)
            && arguments[4].Equals("--no-restore", StringComparison.Ordinal);
        if (!isConventionalTest && !isExecutableHarness)
        {
            return false;
        }

        command = string.Join(" ", arguments.Select((argument, index) =>
            index == 0 || SafeCommandTokenRegex().IsMatch(argument)
                ? argument
                : $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'"));
        return command.Length <= 8_000;
    }

    private static string BuildCopyText(
        ImpactSnapshot snapshot,
        ImpactPrediction? prediction,
        string targetLabel)
    {
        var lines = new List<string>
        {
            "AI Arena - Lite Impact Explorer",
            $"Schema: {snapshot.Schema}",
            $"Index availability: {snapshot.Availability}",
            $"Target: {targetLabel}",
            $"Projects: {snapshot.Projects.Count}",
            $"Nodes: {snapshot.Nodes.Count}",
            $"Relationships: {snapshot.Relationships.Count}",
            "Evidence note: static test relationships and focused-test recommendations are not runtime coverage."
        };
        if (prediction is not null)
        {
            lines.Add($"Prediction availability: {prediction.Availability}");
            lines.Add("");
            lines.Add("Selected definitions:");
            lines.Add(FormatSelectedDefinitions(snapshot, prediction));
            lines.Add("");
            lines.Add("Selected callers, callees, references, and inheritance:");
            lines.Add(FormatSelectedRelationships(snapshot, prediction));
            lines.Add("");
            lines.Add("Likely affected files:");
            lines.AddRange(prediction.AffectedFiles
                .Where(file => IsSafeRelativePath(file.RelativePath))
                .Take(MaxCopiedItems)
                .Select(file =>
                    $"- {file.RelativePath} | distance {file.Distance} | {file.Confidence} | {file.Reason}"));
            lines.Add("");
            lines.Add("Likely affected features:");
            lines.AddRange(prediction.AffectedFeatures
                .Take(MaxCopiedItems)
                .Select(feature =>
                    $"- {feature.Name} | distance {feature.NearestDistance} | {feature.RelativePaths.Count} file(s)"));
            lines.Add("");
            lines.Add("Focused tests (stage and approve explicitly):");
            lines.AddRange(prediction.FocusedTests
                .Where(test => IsSafeRelativePath(test.ProjectRelativePath))
                .Take(MaxCopiedItems)
                .Select(test =>
                    $"- {test.TestName} | {test.ProjectRelativePath} | {test.Confidence} | {FormatFocusedTestCommand(test)}"));
            lines.Add("");
            lines.Add("Changed production symbol test evidence:");
            lines.AddRange(prediction.ProductionTestEvidence
                .Take(MaxCopiedItems)
                .Select(evidence =>
                    $"- {evidence.SymbolName} | {evidence.State} | {evidence.Explanation}"));
        }

        var diagnostics = snapshot.Diagnostics.Concat(prediction?.Diagnostics ?? []).Take(MaxCopiedItems).ToArray();
        if (diagnostics.Length > 0)
        {
            lines.Add("");
            lines.Add("Diagnostics:");
            lines.AddRange(diagnostics.Select(diagnostic =>
                $"- {diagnostic.Severity} {diagnostic.Code}: {SafeDiagnosticMessage(diagnostic.Message)}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatAffectedFiles(IReadOnlyList<AffectedFile> files)
    {
        if (files.Count == 0)
        {
            return "No affected file was evidenced by the bounded semantic traversal.";
        }

        var value = string.Join(" • ", files.Take(8).Select(file =>
            $"{file.RelativePath} (d{file.Distance}, {file.Confidence.ToString().ToLowerInvariant()})"));
        return files.Count > 8 ? $"{value} • +{files.Count - 8} more" : value;
    }

    private static string FormatSelectedDefinitions(
        ImpactSnapshot snapshot,
        ImpactPrediction prediction)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var definitions = prediction.SeedNodeIds
            .Where(nodes.ContainsKey)
            .Select(id => nodes[id])
            .OrderBy(node => node.QualifiedName, StringComparer.Ordinal)
            .Take(12)
            .Select(node =>
            {
                var locations = node.Definitions
                    .Where(definition => IsSafeRelativePath(definition.RelativePath))
                    .Take(3)
                    .Select(definition => definition.Line is null
                        ? definition.RelativePath
                        : $"{definition.RelativePath}:{definition.Line}");
                var location = string.Join(", ", locations);
                return location.Length == 0
                    ? node.QualifiedName
                    : $"{node.QualifiedName} — {location}";
            })
            .ToArray();
        if (definitions.Length == 0)
        {
            return "The change matched file/project evidence but no individual symbol definition.";
        }

        var value = string.Join(" • ", definitions);
        return prediction.SeedNodeIds.Count > definitions.Length
            ? $"{value} • +{prediction.SeedNodeIds.Count - definitions.Length} more"
            : value;
    }

    private static string FormatSelectedRelationships(
        ImpactSnapshot snapshot,
        ImpactPrediction prediction)
    {
        var nodeNames = snapshot.Nodes.ToDictionary(
            node => node.Id,
            node => node.QualifiedName,
            StringComparer.Ordinal);
        var seeds = prediction.SeedNodeIds.ToHashSet(StringComparer.Ordinal);
        var callers = RelatedNames(
            snapshot,
            seeds,
            ImpactRelationshipKind.Calls,
            incoming: true,
            nodeNames);
        var callees = RelatedNames(
            snapshot,
            seeds,
            ImpactRelationshipKind.Calls,
            incoming: false,
            nodeNames);
        var incomingReferences = RelatedNames(
            snapshot,
            seeds,
            ImpactRelationshipKind.References,
            incoming: true,
            nodeNames);
        var outgoingReferences = RelatedNames(
            snapshot,
            seeds,
            ImpactRelationshipKind.References,
            incoming: false,
            nodeNames);
        var bases = RelatedNames(
            snapshot,
            seeds,
            new HashSet<ImpactRelationshipKind>
            {
                ImpactRelationshipKind.Inherits,
                ImpactRelationshipKind.Implements
            },
            incoming: false,
            nodeNames);
        var derived = RelatedNames(
            snapshot,
            seeds,
            new HashSet<ImpactRelationshipKind>
            {
                ImpactRelationshipKind.Inherits,
                ImpactRelationshipKind.Implements
            },
            incoming: true,
            nodeNames);
        return string.Join(
            Environment.NewLine,
            [
                $"Callers: {FormatNames(callers)}",
                $"Callees: {FormatNames(callees)}",
                $"Incoming references: {FormatNames(incomingReferences)}",
                $"Outgoing references: {FormatNames(outgoingReferences)}",
                $"Base types/interfaces: {FormatNames(bases)}",
                $"Derived types/implementations: {FormatNames(derived)}"
            ]);
    }

    private static IReadOnlyList<string> RelatedNames(
        ImpactSnapshot snapshot,
        IReadOnlySet<string> seeds,
        ImpactRelationshipKind kind,
        bool incoming,
        IReadOnlyDictionary<string, string> nodeNames) =>
        RelatedNames(
            snapshot,
            seeds,
            new HashSet<ImpactRelationshipKind> { kind },
            incoming,
            nodeNames);

    private static IReadOnlyList<string> RelatedNames(
        ImpactSnapshot snapshot,
        IReadOnlySet<string> seeds,
        IReadOnlySet<ImpactRelationshipKind> kinds,
        bool incoming,
        IReadOnlyDictionary<string, string> nodeNames)
    {
        return snapshot.Relationships
            .Where(relationship => kinds.Contains(relationship.Kind))
            .Where(relationship => incoming
                ? seeds.Contains(relationship.TargetId)
                : seeds.Contains(relationship.SourceId))
            .Select(relationship => incoming ? relationship.SourceId : relationship.TargetId)
            .Where(nodeNames.ContainsKey)
            .Select(id => nodeNames[id])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static string FormatNames(IReadOnlyList<string> names) =>
        names.Count == 0 ? "none evidenced" : string.Join(", ", names);

    internal static string FormatTestEvidence(ImpactPrediction prediction)
    {
        var evidenceSummary = TestEvidenceSummary(prediction);
        if (prediction.FocusedTests.Count == 0)
        {
            return evidenceSummary;
        }

        var rendered = prediction.FocusedTests
            .Take(MaxRenderedFocusedTests)
            .ToArray();
        var lines = new List<string>
        {
            evidenceSummary,
            "",
            "Recommended focused tests:"
        };
        foreach (var test in rendered)
        {
            var name = SafeEvidenceText(test.TestName, "Unnamed test");
            var project = SafeEvidenceText(
                NormalizeRelativePath(test.ProjectRelativePath) ?? "",
                "Project path was unavailable");
            var reason = SafeEvidenceText(test.Reason, "No static relationship reason was supplied");
            lines.Add($"• {name}");
            lines.Add(
                $"  {project} • distance {Math.Max(0, test.Distance)} • {test.Confidence.ToString().ToLowerInvariant()} confidence • {reason}");
        }

        if (prediction.FocusedTests.Count > rendered.Length)
        {
            lines.Add($"• +{prediction.FocusedTests.Count - rendered.Length} more recommendation(s) not shown");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string TestEvidenceSummary(ImpactPrediction prediction)
    {
        if (prediction.ProductionTestEvidence.Count == 0)
        {
            return prediction.FocusedTests.Count == 0
                ? "No changed production symbol was identified. Focused test evidence is unavailable."
                : $"{prediction.FocusedTests.Count} focused test recommendation(s). Runtime coverage remains unavailable.";
        }

        var direct = prediction.ProductionTestEvidence.Count(item => item.State == StaticTestEvidenceState.Direct);
        var transitive = prediction.ProductionTestEvidence.Count(item => item.State == StaticTestEvidenceState.Transitive);
        var partial = prediction.ProductionTestEvidence.Count(item => item.State == StaticTestEvidenceState.Partial);
        var absent = prediction.ProductionTestEvidence.Count(item => item.State == StaticTestEvidenceState.NoStaticEvidence);
        return $"{direct} direct • {transitive} transitive • {partial} partial • {absent} with no static test relationship. Absence of static evidence does not prove missing runtime coverage.";
    }

    private static string SafeEvidenceText(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (AbsolutePathRegex().IsMatch(value))
        {
            return "Evidence text included a local absolute path and was redacted";
        }

        var compact = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= MaxRenderedEvidenceTextLength
            ? compact
            : $"{compact[..MaxRenderedEvidenceTextLength]}…";
    }

    private static int Count(ImpactSnapshot snapshot, ImpactRelationshipKind kind) =>
        snapshot.Relationships.Count(relationship => relationship.Kind == kind);

    private static string StatusFor(ImpactSnapshot snapshot, ImpactPrediction prediction)
    {
        if (snapshot.Availability == ImpactAvailability.Unavailable
            || prediction.Availability == ImpactAvailability.Unavailable)
        {
            return "Semantic evidence is unavailable. Unsupported predictions and test claims are hidden.";
        }

        return prediction.Availability == ImpactAvailability.Partial
            ? "Partial semantic evidence is displayed with bounded predictions; missing evidence remains unknown."
            : "Semantic impact prediction complete. Predictions are evidence-backed but remain change-risk guidance, not certainty.";
    }

    private static string AvailabilityLabel(ImpactAvailability availability) => availability switch
    {
        ImpactAvailability.Available => "Available",
        ImpactAvailability.Partial => "Partial",
        _ => "Unavailable"
    };

    private static string SafeDiagnosticMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No diagnostic detail was supplied.";
        }

        return AbsolutePathRegex().IsMatch(message)
            ? "Diagnostic details included a local absolute path and were redacted."
            : message.Length <= 2_000
                ? message
                : $"{message[..2_000]}…";
    }

    private bool TryWorkspace(out string root)
    {
        root = "";
        try
        {
            var candidate = workspacePath();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            root = Path.GetFullPath(candidate);
            return Directory.Exists(root);
        }
        catch
        {
            return false;
        }
    }

    private bool IsCurrent(long generation, string capturedRoot)
    {
        return generation == Volatile.Read(ref analysisGeneration)
            && TryWorkspace(out var currentRoot)
            && currentRoot.Equals(capturedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InvokeOnUiAsync(Action action)
    {
        if (disposed
            || expander.Dispatcher.HasShutdownStarted
            || expander.Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (expander.Dispatcher.CheckAccess())
        {
            if (!disposed)
            {
                action();
            }
            return;
        }

        try
        {
            await expander.Dispatcher.InvokeAsync(() =>
            {
                if (!disposed)
                {
                    action();
                }
            }).Task.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            disposed
            && exception is TaskCanceledException or InvalidOperationException)
        {
        }
    }

    private async Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        if (disposed
            || expander.Dispatcher.HasShutdownStarted
            || expander.Dispatcher.HasShutdownFinished)
        {
            return default!;
        }
        if (expander.Dispatcher.CheckAccess())
        {
            return disposed ? default! : action();
        }

        try
        {
            return await expander.Dispatcher.InvokeAsync(
                    () => disposed ? default! : action())
                .Task
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            disposed
            && exception is TaskCanceledException or InvalidOperationException)
        {
            return default!;
        }
    }

    private void SetBusy(bool busy)
    {
        analyzeButton.IsEnabled = !busy && solutionPicker.Items.Count > 0;
        analyzeChangesButton.IsEnabled = !busy && solutionPicker.Items.Count > 0;
        solutionPicker.IsEnabled = !busy;
        targetText.IsEnabled = !busy;
        if (busy)
        {
            stageTestsButton.IsEnabled = false;
            copyButton.IsEnabled = false;
        }
        else
        {
            RefreshActions();
        }
    }

    private void RefreshActions()
    {
        analyzeButton.IsEnabled = solutionPicker.Items.Count > 0;
        analyzeChangesButton.IsEnabled = solutionPicker.Items.Count > 0;
        var current = ResultMatchesCurrentWorkspace();
        stageTestsButton.IsEnabled = current && stagedTest is not null;
        copyButton.IsEnabled = current && !string.IsNullOrWhiteSpace(copyText);
    }

    private void ClearResults()
    {
        stagedTest = null;
        copyText = "";
        resultWorkspaceRoot = "";
        summaryText.Text = "No semantic impact analysis yet.";
        items.Children.Clear();
        RefreshActions();
    }

    private void SetStatus(string value)
    {
        statusText.Text = value;
        AutomationProperties.SetHelpText(statusText, value);
    }

    private bool ResultMatchesCurrentWorkspace()
    {
        return !string.IsNullOrEmpty(resultWorkspaceRoot)
            && TryWorkspace(out var currentRoot)
            && currentRoot.Equals(resultWorkspaceRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void DiscardStaleResults()
    {
        ClearResults();
        SetStatus("The workspace changed, so stale Impact Explorer evidence was discarded.");
    }

    private static string? NormalizeRelativePath(string path)
    {
        if (!IsSafeRelativePath(path))
        {
            return null;
        }

        return path.Replace('\\', '/');
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\0'))
        {
            return false;
        }

        return path.Replace('\\', '/')
            .Split('/')
            .All(segment => segment is not ("" or "." or ".."));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Interlocked.Increment(ref analysisGeneration);
        analysisCancellation?.Cancel();
        analysisCancellation?.Dispose();
        expander.Expanded -= Expander_Expanded;
        analyzeButton.Click -= AnalyzeButton_Click;
        analyzeChangesButton.Click -= AnalyzeChangesButton_Click;
        stageTestsButton.Click -= StageTestsButton_Click;
        copyButton.Click -= CopyButton_Click;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:/=+\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCommandTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();

    private sealed record TargetResolution(ImpactChangeSet Changes, int MatchCount);

    private sealed record AnalysisComputation(ImpactPrediction? Prediction, string TargetLabel);
}
