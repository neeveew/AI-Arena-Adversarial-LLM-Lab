using AIArena.CodeIntelligence;
using AIArena.Wpf;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static partial class Program
{
    static void AgentImpactExplorerRendersEvidenceAndStagesFocusedTests()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Fixture.sln"), "");
            try
            {
                var snapshot = ImpactFixtureSnapshot(root);
                var prediction = ImpactFixturePrediction();
                var stagedCommand = "";
                var stagedShell = "";
                var controls = CreateImpactControls();
                using var coordinator = new AgentImpactExplorerCoordinator(
                    controls.Expander,
                    controls.Status,
                    controls.Solution,
                    controls.Target,
                    controls.Analyze,
                    controls.AnalyzeChanges,
                    controls.Summary,
                    controls.Items,
                    controls.StageTests,
                    controls.Copy,
                    () => root,
                    () => ["src/App/Service.cs"],
                    (command, shell) =>
                    {
                        stagedCommand = command;
                        stagedShell = shell ?? "";
                    },
                    _ => Brushes.Transparent,
                    (_, _, _) => Task.FromResult(snapshot),
                    (_, changes, _) =>
                    {
                        Require(
                            changes.ChangedNodeIds?.SequenceEqual(["type:service"], StringComparer.Ordinal) == true,
                            "symbol analysis should send every exact matching stable node id to the predictor");
                        return prediction;
                    });

                coordinator.DebugRefreshSolutions();
                controls.Target.Text = "App.Service";
                AwaitImpactAnalysis(coordinator.DebugAnalyzeTargetAsync());

                Require(
                    coordinator.DebugSummary.Contains("2 likely file(s)", StringComparison.Ordinal),
                    "impact summary should disclose bounded affected-file predictions");
                Require(coordinator.DebugRenderedItemCount >= 7, "impact evidence should render symbol, project, XAML, test, and prediction cards");
                Require(controls.StageTests.IsEnabled, "a structurally validated focused test should be stageable");
                Require(controls.Copy.IsEnabled, "completed impact evidence should be copyable");
                Require(
                    coordinator.DebugCopyText.Contains("not runtime coverage", StringComparison.OrdinalIgnoreCase),
                    "copied evidence should distinguish static test relationships from coverage");
                Require(
                    coordinator.DebugCopyText.Contains("App.Service — src/App/Service.cs:4", StringComparison.Ordinal)
                    && coordinator.DebugCopyText.Contains("Callers: App.Tests.ServiceTests.Changes_value", StringComparison.Ordinal)
                    && coordinator.DebugCopyText.Contains("Incoming references: App.Tests.ServiceTests.Changes_value", StringComparison.Ordinal),
                    "selected symbol evidence should expose definitions, callers, and references instead of only aggregate counts");
                var focusedTestCard = ImpactCardDetail(controls.Items, "Impact Explorer Focused-test evidence");
                Require(
                    focusedTestCard.Contains("App.Tests.ServiceTests.Changes_value", StringComparison.Ordinal)
                    && focusedTestCard.Contains("tests/App.Tests/App.Tests.csproj", StringComparison.Ordinal)
                    && focusedTestCard.Contains("Direct test relationship", StringComparison.Ordinal),
                    "the rendered focused-test card should expose the recommended test name, project, and static reason");
                Require(
                    !coordinator.DebugCopyText.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "copied evidence must never expose the absolute workspace root");
                Require(
                    coordinator.DebugCopyText.Contains("absolute path and were redacted", StringComparison.OrdinalIgnoreCase),
                    "defensive UI rendering should redact absolute paths even if an upstream diagnostic regresses");

                controls.StageTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(stagedShell == "PowerShell", "focused tests should be staged through the existing PowerShell approval rail");
                Require(
                    stagedCommand == "dotnet test tests/App.Tests/App.Tests.csproj --no-restore --filter FullyQualifiedName=App.Tests.ServiceTests.Changes_value",
                    "focused-test staging should preserve the exact evidence-backed project and test filter");
                Require(
                    coordinator.DebugStatus.Contains("approve it explicitly", StringComparison.OrdinalIgnoreCase),
                    "staging must explain that execution still requires explicit approval");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void AgentImpactExplorerRejectsUntrustedFocusedCommands()
    {
        var valid = new FocusedTestRecommendation(
            "test:service",
            "App.Tests.ServiceTests.Changes_value",
            "tests/App.Tests/App.Tests.csproj",
            [
                "dotnet",
                "test",
                "tests/App.Tests/App.Tests.csproj",
                "--no-restore",
                "--filter",
                "FullyQualifiedName=App.Tests.ServiceTests.Changes_value"
            ],
            1,
            ImpactConfidence.High,
            "Direct test relationship");
        Require(
            AgentImpactExplorerCoordinator.TryFormatFocusedTestCommand(valid, out var validCommand)
            && validCommand.StartsWith("dotnet test ", StringComparison.Ordinal),
            "the exact focused-test shape should be stageable");

        var arbitrary = valid with
        {
            FileNameAndArguments =
            [
                "dotnet",
                "test",
                "tests/App.Tests/App.Tests.csproj",
                "--logger",
                "console;verbosity=diagnostic"
            ]
        };
        Require(
            !AgentImpactExplorerCoordinator.TryFormatFocusedTestCommand(arbitrary, out _),
            "Impact Explorer must not turn arbitrary argument arrays into command proposals");

        var escaped = valid with
        {
            ProjectRelativePath = "../Outside.Tests/Outside.Tests.csproj",
            FileNameAndArguments =
            [
                "dotnet",
                "test",
                "../Outside.Tests/Outside.Tests.csproj",
                "--no-restore",
                "--filter",
                "FullyQualifiedName=Outside.Tests"
            ]
        };
        Require(
            !AgentImpactExplorerCoordinator.TryFormatFocusedTestCommand(escaped, out _),
            "focused tests outside the workspace-relative boundary must be rejected");
    }

    static void AgentImpactExplorerKeepsUnavailableEvidenceHonest()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-unavailable-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Fixture.slnx"), "<Solution />");
            try
            {
                var snapshot = ImpactSnapshot.Unavailable(new ImpactDiagnostic(
                    "missing-assets",
                    ImpactDiagnosticSeverity.Warning,
                    "Restored semantic assets are unavailable."));
                var controls = CreateImpactControls();
                using var coordinator = new AgentImpactExplorerCoordinator(
                    controls.Expander,
                    controls.Status,
                    controls.Solution,
                    controls.Target,
                    controls.Analyze,
                    controls.AnalyzeChanges,
                    controls.Summary,
                    controls.Items,
                    controls.StageTests,
                    controls.Copy,
                    () => root,
                    () => ["src/App/Service.cs"],
                    (_, _) => throw new InvalidOperationException("no command should be staged"),
                    _ => Brushes.Transparent,
                    (_, _, _) => Task.FromResult(snapshot));

                coordinator.DebugRefreshSolutions();
                AwaitImpactAnalysis(coordinator.DebugAnalyzeChangesAsync());

                Require(
                    coordinator.DebugStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                    "unavailable semantic loading should remain explicitly unavailable");
                Require(!controls.StageTests.IsEnabled, "unavailable evidence must not enable a guessed test command");
                Require(
                    !coordinator.DebugCopyText.Contains("untested", StringComparison.OrdinalIgnoreCase)
                    && !coordinator.DebugCopyText.Contains("is covered", StringComparison.OrdinalIgnoreCase),
                    "unavailable evidence must not be converted into test or coverage verdicts");
                Require(
                    coordinator.DebugCopyText.Contains("not runtime coverage", StringComparison.OrdinalIgnoreCase),
                    "the evidence report should retain its no-coverage-claim disclaimer");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void AgentImpactExplorerDiscardsStaleWorkspaceEvidence()
    {
        RunStaTest(() =>
        {
            var rootA = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-root-a-{Guid.NewGuid():N}");
            var rootB = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-root-b-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            File.WriteAllText(Path.Combine(rootA, "Fixture.sln"), "");
            File.WriteAllText(Path.Combine(rootB, "Fixture.sln"), "");
            try
            {
                var currentRoot = rootA;
                var switchDuringAnalysis = false;
                var staged = "";
                var controls = CreateImpactControls();
                using var coordinator = new AgentImpactExplorerCoordinator(
                    controls.Expander,
                    controls.Status,
                    controls.Solution,
                    controls.Target,
                    controls.Analyze,
                    controls.AnalyzeChanges,
                    controls.Summary,
                    controls.Items,
                    controls.StageTests,
                    controls.Copy,
                    () => currentRoot,
                    () => ["src/App/Service.cs"],
                    (command, _) => staged = command,
                    _ => Brushes.Transparent,
                    (_, _, _) =>
                    {
                        if (switchDuringAnalysis)
                        {
                            currentRoot = rootB;
                        }
                        return Task.FromResult(ImpactFixtureSnapshot(rootA));
                    },
                    (_, _, _) => ImpactFixturePrediction());

                coordinator.DebugRefreshSolutions();
                controls.Target.Text = "App.Service";
                AwaitImpactAnalysis(coordinator.DebugAnalyzeTargetAsync());
                Require(controls.StageTests.IsEnabled, "fresh evidence should initially be stageable");

                currentRoot = rootB;
                controls.StageTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(staged.Length == 0, "a completed result from another workspace must never stage a command");
                Require(
                    !controls.StageTests.IsEnabled
                    && !controls.Copy.IsEnabled
                    && coordinator.DebugStatus.Contains("stale", StringComparison.OrdinalIgnoreCase),
                    "post-result workspace switches should discard every action and explain the stale result");

                currentRoot = rootA;
                coordinator.DebugRefreshSolutions();
                switchDuringAnalysis = true;
                AwaitImpactAnalysis(coordinator.DebugAnalyzeTargetAsync());
                Require(
                    coordinator.DebugSummary == "No semantic impact analysis yet."
                    && !controls.StageTests.IsEnabled
                    && !controls.Copy.IsEnabled,
                    "a workspace switch while analysis is completing must not render or re-enable stale evidence");
            }
            finally
            {
                Directory.Delete(rootA, recursive: true);
                Directory.Delete(rootB, recursive: true);
            }
        });
    }

    static void AgentImpactExplorerBoundsInspectableFocusedTestEvidence()
    {
        var template = ImpactFixturePrediction().FocusedTests.Single();
        var recommendations = Enumerable.Range(1, 11)
            .Select(index => template with
            {
                TestNodeId = $"test:{index}",
                TestName = $"App.Tests.ServiceTests.Case_{index:00}",
                ProjectRelativePath = $"tests/App.Tests{index:00}/App.Tests{index:00}.csproj",
                Reason = $"Static relationship reason {index:00}"
            })
            .ToArray();
        var prediction = ImpactFixturePrediction() with
        {
            FocusedTests = recommendations
        };

        var detail = AgentImpactExplorerCoordinator.FormatTestEvidence(prediction);
        Require(
            detail.Contains("App.Tests.ServiceTests.Case_01", StringComparison.Ordinal)
            && detail.Contains("tests/App.Tests01/App.Tests01.csproj", StringComparison.Ordinal)
            && detail.Contains("Static relationship reason 01", StringComparison.Ordinal),
            "focused-test details should show the test name, project, and static reason");
        Require(
            detail.Contains("App.Tests.ServiceTests.Case_08", StringComparison.Ordinal)
            && !detail.Contains("App.Tests.ServiceTests.Case_09", StringComparison.Ordinal)
            && detail.Contains("+3 more recommendation(s) not shown", StringComparison.Ordinal),
            "focused-test details should render a deterministic eight-item bound and disclose omitted recommendations");
        Require(
            detail.Contains("not prove missing runtime coverage", StringComparison.OrdinalIgnoreCase),
            "the inspectable list must preserve the no-coverage-claim qualification");
    }

    static void AgentImpactExplorerPredictsOffDispatcherWithCancellation()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-worker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Fixture.sln"), "");
            try
            {
                var controls = CreateImpactControls();
                var uiThreadId = Environment.CurrentManagedThreadId;
                var predictorThreadIds = new List<int>();
                using var firstPredictionEntered = new ManualResetEventSlim();
                var firstCancellationObserved = false;
                var predictionCalls = 0;
                using var coordinator = new AgentImpactExplorerCoordinator(
                    controls.Expander,
                    controls.Status,
                    controls.Solution,
                    controls.Target,
                    controls.Analyze,
                    controls.AnalyzeChanges,
                    controls.Summary,
                    controls.Items,
                    controls.StageTests,
                    controls.Copy,
                    () => root,
                    () => ["src/App/Service.cs"],
                    (_, _) => throw new InvalidOperationException("no command should be staged"),
                    _ => Brushes.Transparent,
                    (_, _, _) => Task.FromResult(ImpactFixtureSnapshot(root)),
                    (_, _, cancellationToken) =>
                    {
                        lock (predictorThreadIds)
                        {
                            predictorThreadIds.Add(Environment.CurrentManagedThreadId);
                        }

                        if (Interlocked.Increment(ref predictionCalls) == 1)
                        {
                            firstPredictionEntered.Set();
                            try
                            {
                                cancellationToken.WaitHandle.WaitOne();
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            catch (OperationCanceledException)
                            {
                                firstCancellationObserved = true;
                                throw;
                            }
                        }

                        return ImpactFixturePrediction();
                    });

                coordinator.DebugRefreshSolutions();
                controls.Target.Text = "App.Service";
                var cancelledAnalysis = coordinator.DebugAnalyzeTargetAsync();
                Require(
                    firstPredictionEntered.Wait(TimeSpan.FromSeconds(5)),
                    "the first background prediction should start without requiring dispatcher work");

                controls.Target.Text = "Service";
                var currentAnalysis = coordinator.DebugAnalyzeTargetAsync();
                AwaitImpactAnalysis(Task.WhenAll(cancelledAnalysis, currentAnalysis));

                Require(
                    predictorThreadIds.Count >= 2
                    && predictorThreadIds.All(threadId => threadId != uiThreadId),
                    "ImpactPredictor work must never execute on the WPF dispatcher thread");
                Require(
                    firstCancellationObserved,
                    "starting a newer analysis should cooperatively cancel the in-flight predictor");
                Require(
                    coordinator.DebugSummary.Contains("prediction for Service", StringComparison.Ordinal),
                    "only the current analysis should render after cancellation");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static ImpactSnapshot ImpactFixtureSnapshot(string absoluteRoot)
    {
        IReadOnlyList<ImpactNode> nodes =
        [
            new(
                "project:app",
                ImpactNodeKind.Project,
                "App",
                "src/App/App.csproj",
                "src/App/App.csproj",
                true,
                false,
                [new ImpactEvidence("src/App/App.csproj")]),
            new(
                "file:service",
                ImpactNodeKind.File,
                "Service.cs",
                "src/App/Service.cs",
                "src/App/App.csproj",
                true,
                false,
                [new ImpactEvidence("src/App/Service.cs")]),
            new(
                "type:service",
                ImpactNodeKind.Type,
                "Service",
                "App.Service",
                "src/App/App.csproj",
                true,
                false,
                [new ImpactEvidence("src/App/Service.cs", 4, 1)]),
            new(
                "test:service",
                ImpactNodeKind.Test,
                "Changes_value",
                "App.Tests.ServiceTests.Changes_value",
                "tests/App.Tests/App.Tests.csproj",
                false,
                true,
                [new ImpactEvidence("tests/App.Tests/ServiceTests.cs", 8, 1)]),
            new(
                "xaml:view",
                ImpactNodeKind.XamlFile,
                "View.xaml",
                "src/App/View.xaml",
                "src/App/App.csproj",
                true,
                false,
                [new ImpactEvidence("src/App/View.xaml")]),
            new(
                "xaml:resource",
                ImpactNodeKind.XamlResource,
                "PrimaryButton",
                "PrimaryButton",
                "src/App/App.csproj",
                true,
                false,
                [new ImpactEvidence("src/App/View.xaml", 5, 1)]),
            new(
                "package:test-sdk",
                ImpactNodeKind.Package,
                "Microsoft.NET.Test.Sdk",
                "Microsoft.NET.Test.Sdk",
                "tests/App.Tests/App.Tests.csproj",
                false,
                true,
                [])
        ];
        IReadOnlyList<ImpactRelationship> relationships =
        [
            new("declares", "file:service", "type:service", ImpactRelationshipKind.Declares, 1, ImpactConfidence.High, [new ImpactEvidence("src/App/Service.cs", 4, 1)]),
            new("references", "test:service", "type:service", ImpactRelationshipKind.References, 1, ImpactConfidence.High, [new ImpactEvidence("tests/App.Tests/ServiceTests.cs", 8, 1)]),
            new("calls", "test:service", "type:service", ImpactRelationshipKind.Calls, 1, ImpactConfidence.High, [new ImpactEvidence("tests/App.Tests/ServiceTests.cs", 8, 1)]),
            new("tests", "type:service", "test:service", ImpactRelationshipKind.Tests, 1, ImpactConfidence.High, [new ImpactEvidence("tests/App.Tests/ServiceTests.cs", 8, 1)]),
            new("xaml", "xaml:view", "xaml:resource", ImpactRelationshipKind.XamlUsesResource, 1, ImpactConfidence.High, [new ImpactEvidence("src/App/View.xaml", 5, 1)]),
            new("package", "project:app", "package:test-sdk", ImpactRelationshipKind.PackageReferences, 1, ImpactConfidence.High, [new ImpactEvidence("src/App/App.csproj")])
        ];
        return new ImpactSnapshot(
            ImpactSnapshot.CurrentSchema,
            ImpactAvailability.Available,
            [
                new(
                    "project:app",
                    "App",
                    "src/App/App.csproj",
                    false,
                    false,
                    [],
                    []),
                new(
                    "project:tests",
                    "App.Tests",
                    "tests/App.Tests/App.Tests.csproj",
                    true,
                    false,
                    ["src/App/App.csproj"],
                    ["Microsoft.NET.Test.Sdk"])
            ],
            nodes,
            relationships,
            [
                new(
                    "path-defence",
                    ImpactDiagnosticSeverity.Warning,
                    $"A linked tool at {Path.Combine(absoluteRoot, "tools", "helper.cs")} could not load.")
            ]);
    }

    private static ImpactPrediction ImpactFixturePrediction()
    {
        return new ImpactPrediction(
            ImpactAvailability.Available,
            ["type:service"],
            ["type:service", "test:service"],
            [
                new("src/App/Service.cs", 0, ImpactConfidence.High, "Changed definition"),
                new("tests/App.Tests/ServiceTests.cs", 1, ImpactConfidence.High, "Tests relationship")
            ],
            [
                new(
                    "App",
                    ["src/App/Service.cs", "tests/App.Tests/ServiceTests.cs"],
                    0)
            ],
            [
                new(
                    "test:service",
                    "App.Tests.ServiceTests.Changes_value",
                    "tests/App.Tests/App.Tests.csproj",
                    [
                        "dotnet",
                        "test",
                        "tests/App.Tests/App.Tests.csproj",
                        "--no-restore",
                        "--filter",
                        "FullyQualifiedName=App.Tests.ServiceTests.Changes_value"
                    ],
                    1,
                    ImpactConfidence.High,
                    "Direct test relationship")
            ],
            [
                new(
                    "type:service",
                    "App.Service",
                    StaticTestEvidenceState.Direct,
                    ["test:service"],
                    "A test has direct semantic evidence against this changed symbol.")
            ],
            []);
    }

    private static ImpactControls CreateImpactControls()
    {
        return new ImpactControls(
            new Expander(),
            new TextBlock(),
            new ComboBox(),
            new TextBox(),
            new Button(),
            new Button(),
            new TextBlock(),
            new StackPanel(),
            new Button(),
            new Button());
    }

    private static string ImpactCardDetail(StackPanel items, string automationName)
    {
        var card = items.Children
            .OfType<Border>()
            .Single(item => AutomationProperties.GetName(item).Equals(
                automationName,
                StringComparison.Ordinal));
        return string.Join(
            Environment.NewLine,
            ((StackPanel)card.Child).Children
                .OfType<TextBlock>()
                .Select(item => item.Text));
    }

    private static void AwaitImpactAnalysis(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var completed = false;
            var timedOut = false;
            var timeout = new DispatcherTimer(
                DispatcherPriority.Send,
                dispatcher)
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                if (completed)
                {
                    return;
                }

                timedOut = true;
                frame.Continue = false;
            };
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() =>
                    {
                        completed = true;
                        frame.Continue = false;
                    })),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (timedOut)
            {
                throw new TimeoutException("Impact Explorer analysis did not complete within 20 seconds.");
            }
        }

        task.GetAwaiter().GetResult();
    }

    private sealed record ImpactControls(
        Expander Expander,
        TextBlock Status,
        ComboBox Solution,
        TextBox Target,
        Button Analyze,
        Button AnalyzeChanges,
        TextBlock Summary,
        StackPanel Items,
        Button StageTests,
        Button Copy);
}
