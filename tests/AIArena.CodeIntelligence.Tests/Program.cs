using System.Diagnostics;
using AIArena.CodeIntelligence;

if (args.Contains("--real-workspace", StringComparer.Ordinal))
{
    return RunRealWorkspaceSmoke();
}

var tests = new (string Name, Action Test)[]
{
    ("semantic explorer indexes code, projects, packages, XAML, and tests", SemanticExplorerIndexesFixture),
    ("impact prediction maps changed symbols to files, features, and focused tests", ImpactPredictionMapsAffectedTests),
    ("impact prediction labels absent and partial test evidence honestly", ImpactPredictionLabelsUnknownCoverage),
    ("impact prediction preserves relationship confidence", ImpactPredictionPreservesRelationshipConfidence),
    ("evaluated package evidence includes conditional and imported references", EvaluatedPackagesIncludeConditionalAndImportedReferences),
    ("duplicate XAML resource keys retain scoped or ambiguous evidence", DuplicateXamlResourceKeysRetainEvidence),
    ("test history distinguishes repeated failures, flakes, and unavailable evidence", TestHistoryIsDeterministic),
    ("semantic identifiers and ordering are deterministic and relative-only", SnapshotIsDeterministicAndPrivate),
    ("duplicate assembly symbols remain scoped to their defining projects", DuplicateAssemblySymbolsRemainProjectScoped),
    ("reparse-point files and directories cannot escape the analysis root", ReparsePointInputsAreRejected),
    ("diagnostics never expose workspace or linked absolute paths", DiagnosticsNeverExposeAbsolutePaths)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Test();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(exception);
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} code-intelligence tests passed.");
return failures == 0 ? 0 : 1;

static int RunRealWorkspaceSmoke()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null
           && (!Directory.Exists(Path.Combine(directory.FullName, ".git"))
               || !File.Exists(Path.Combine(directory.FullName, "AI Arena.slnx"))))
    {
        directory = directory.Parent;
    }
    if (directory is null)
    {
        throw new InvalidOperationException("The AI Arena workspace root could not be located.");
    }

    var root = directory.FullName;
    var snapshot = new RoslynImpactExplorer()
        .AnalyzeSolutionAsync(root, "AI Arena.slnx")
        .GetAwaiter()
        .GetResult();
    Require(snapshot.Availability != ImpactAvailability.Unavailable, Diagnostics(snapshot));
    Require(snapshot.Projects.Count == 7, "the real solution should expose all product and intelligence projects");
    Require(snapshot.Nodes.Any(node => node.IsProduction), "the real solution should expose production symbols");
    Require(snapshot.Diagnostics.All(item =>
        !item.Message.Contains(root, StringComparison.OrdinalIgnoreCase)),
        "real-workspace diagnostics must not contain the workspace root");
    Console.WriteLine(
        $"PASS real workspace: {snapshot.Availability}; "
        + $"{snapshot.Projects.Count} projects, {snapshot.Nodes.Count} nodes, "
        + $"{snapshot.Relationships.Count} relationships.");
    return 0;
}

static void SemanticExplorerIndexesFixture()
{
    WithFixture((_, snapshot) =>
    {
        Require(snapshot.Schema == ImpactSnapshot.CurrentSchema, "schema should be versioned");
        Require(
            snapshot.Availability == ImpactAvailability.Available,
            $"Availability={snapshot.Availability}; projects={snapshot.Projects.Count}; nodes={snapshot.Nodes.Count}. {Diagnostics(snapshot)}");
        Require(snapshot.Projects.Count == 2, "both projects should be indexed");
        Require(snapshot.Nodes.Any(node =>
            node.Kind == ImpactNodeKind.Type && node.QualifiedName == "Product.Greeter"), "type definition missing");
        Require(snapshot.Nodes.Any(node =>
            node.Kind == ImpactNodeKind.Test && node.Name == "Greets"), "attributed test definition missing");
        var caseDistinctSymbols = snapshot.Nodes
            .Where(node => node.QualifiedName is
                "Product.CaseSensitive.Foo()" or "Product.CaseSensitive.foo()")
            .ToArray();
        Require(
            caseDistinctSymbols.Length == 2
            && caseDistinctSymbols.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() == 2,
            "case-distinct C# symbols must retain distinct stable identities");
        var caseDistinctResources = snapshot.Nodes
            .Where(node =>
                node.Kind == ImpactNodeKind.XamlResource
                && node.Name is "AccentBrush" or "accentBrush")
            .ToArray();
        Require(
            caseDistinctResources.Length == 2
            && caseDistinctResources.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() == 2,
            "case-distinct XAML resource keys must retain distinct stable identities");
        Require(Relationship(snapshot, ImpactRelationshipKind.Implements, "Product.Clock", "Product.IClock"),
            "interface implementation missing");
        Require(Relationship(snapshot, ImpactRelationshipKind.Calls, "Product.Greeter.Greet()", "Product.IClock.Now()"),
            "caller/callee relationship missing");
        Require(snapshot.Relationships.Any(edge =>
                edge.Kind == ImpactRelationshipKind.Calls
                && snapshot.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName
                    == "Product.Tests.GreeterTests.Greets()"
                && snapshot.Nodes.Single(node => node.Id == edge.TargetId).Kind
                    == ImpactNodeKind.Constructor
                && snapshot.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName
                    .StartsWith("Product.Greeter.Greeter(", StringComparison.Ordinal)),
            "object creation should create a caller/callee edge to the primary constructor");
        Require(snapshot.Relationships.Any(edge => edge.Kind == ImpactRelationshipKind.ProjectReferences),
            "project relationship missing");
        Require(snapshot.Relationships.Any(edge => edge.Kind == ImpactRelationshipKind.PackageReferences
            && snapshot.Nodes.Single(node => node.Id == edge.TargetId).Name == "Microsoft.Build.Locator"),
            "package relationship missing");
        Require(snapshot.Relationships.Any(edge => edge.Kind == ImpactRelationshipKind.XamlCodeBehind),
            "XAML code-behind relationship missing");
        Require(snapshot.Relationships.Any(edge => edge.Kind == ImpactRelationshipKind.XamlUsesResource),
            "XAML resource relationship missing");
        Require(snapshot.Relationships.Any(edge => edge.Kind == ImpactRelationshipKind.Tests),
            "production-to-test relationship missing");
        Require(snapshot.Nodes.SelectMany(node => node.Definitions).All(item =>
            !item.RelativePath.Split('/').Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))),
            "generated build-artifact documents should not enter the public index");
        var greeter = new ImpactQueryService().SearchSymbols(snapshot, "Greeter.Greet")
            .Single(node => node.QualifiedName == "Product.Greeter.Greet()");
        var details = new ImpactQueryService().GetDetails(snapshot, greeter.Id);
        Require(details is not null && details.Callees.Count > 0, "query facade should expose callees");
    });
}

static void ImpactPredictionMapsAffectedTests()
{
    WithFixture((_, snapshot) =>
    {
        var prediction = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet(["src/Product/Greeter.cs"]));
        Require(prediction.Availability == ImpactAvailability.Available, "complete input should produce complete prediction");
        Require(prediction.AffectedFiles.Any(file =>
            file.RelativePath == "tests/Product.Tests/GreeterTests.cs"), "affected test file missing");
        Require(prediction.AffectedFeatures.Any(feature =>
            feature.Name == "Product.Tests"), "affected test feature missing");
        var focused = prediction.FocusedTests.Single(test => test.TestName.Contains("Greets", StringComparison.Ordinal));
        Require(focused.FileNameAndArguments.Take(3).SequenceEqual(
            ["dotnet", "test", "tests/Product.Tests/Product.Tests.csproj"]),
            "conventional test command should be project-correct");
        Require(focused.FileNameAndArguments.Contains("--filter"), "focused test should use an exact filter argument");
        Require(focused.FileNameAndArguments.Last()
            == "FullyQualifiedName=Product.Tests.GreeterTests.Greets",
            "focused test filter should use the runner's fully-qualified method name");
        Require(prediction.FocusedTests.All(test =>
                test.TestName != "Product.Tests.GreeterTests"),
            "test container types must not become exact method-filter recommendations");
        Require(prediction.ProductionTestEvidence.Any(item =>
            item.SymbolName.Contains("Product.Greeter", StringComparison.Ordinal)
            && item.State is StaticTestEvidenceState.Direct or StaticTestEvidenceState.Transitive),
            "changed production symbol should point to statically evidenced tests");
    });
}

static void ImpactPredictionLabelsUnknownCoverage()
{
    WithFixture((root, snapshot) =>
    {
        var unused = snapshot.Nodes.Single(node =>
            node.IsProduction && node.QualifiedName == "Product.Unused.Compute()");
        var complete = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet([], [unused.Id]));
        var noEvidence = complete.ProductionTestEvidence.Single(item => item.NodeId == unused.Id);
        Require(noEvidence.State == StaticTestEvidenceState.NoStaticEvidence,
            "complete static analysis should say no static evidence");
        Require(noEvidence.Explanation.Contains("not proof", StringComparison.OrdinalIgnoreCase),
            "absence must not be presented as proven lack of runtime coverage");

        var partialSnapshot = snapshot with { Availability = ImpactAvailability.Partial };
        var partial = new ImpactPredictor().Predict(
            partialSnapshot,
            new ImpactChangeSet([], [unused.Id]));
        Require(partial.ProductionTestEvidence.Single().State == StaticTestEvidenceState.Partial,
            "partial analysis must preserve unknown coverage");

        var invalid = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, Path.Combine(Path.GetDirectoryName(root)!, "outside.sln"))
            .GetAwaiter()
            .GetResult();
        Require(invalid.Availability == ImpactAvailability.Unavailable,
            "an out-of-root solution should return explicit unavailable state");

        var rejected = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet([Path.Combine(root, "src", "Product", "Greeter.cs")]));
        Require(rejected.SeedNodeIds.Count == 0, "absolute changed paths must be rejected");
        Require(rejected.Availability == ImpactAvailability.Partial,
            "rejected change inputs must not retain a complete prediction label");
        Require(rejected.Diagnostics.Any(item => item.Code == "change-path-rejected"),
            "rejected path should be diagnosed");

        var deleted = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet(["src/Product/DeletedFeature.cs"]));
        Require(
            deleted.Availability == ImpactAvailability.Partial
            && deleted.SeedNodeIds.Count == 0
            && deleted.Diagnostics.Any(item =>
                item.Code == "changed-path-not-indexed"
                && item.RelativePath == "src/Product/DeletedFeature.cs"),
            "safe deleted or unindexed paths must remain explicit partial evidence");

        var mixed = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet(
                ["src/Product/Greeter.cs", "src/Product/DeletedFeature.cs"]));
        Require(
            mixed.Availability == ImpactAvailability.Partial
            && mixed.AffectedFiles.Any(file => file.RelativePath == "src/Product/Greeter.cs")
            && mixed.Diagnostics.Any(item => item.Code == "changed-path-not-indexed"),
            "mixed indexed and deleted paths should keep proven impact while disclosing unknown impact");

        var missingNode = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet([], ["node:stale"]));
        Require(
            missingNode.Availability == ImpactAvailability.Partial
            && missingNode.SeedNodeIds.Count == 0
            && missingNode.Diagnostics.Any(item => item.Code == "changed-node-missing"),
            "a stale node-only request must not retain an available prediction label");

        var mixedNode = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet([], [unused.Id, "node:stale"]));
        Require(
            mixedNode.Availability == ImpactAvailability.Partial
            && mixedNode.SeedNodeIds.Contains(unused.Id, StringComparer.Ordinal)
            && mixedNode.Diagnostics.Any(item => item.Code == "changed-node-missing"),
            "mixed present and stale node IDs should retain proven evidence but remain partial");
    });
}

static void ImpactPredictionPreservesRelationshipConfidence()
{
    WithFixture((_, snapshot) =>
    {
        var resource = snapshot.Nodes.Single(node =>
            node.Kind == ImpactNodeKind.XamlResource
            && node.Name == "AccentBrush");
        var prediction = new ImpactPredictor().Predict(
            snapshot,
            new ImpactChangeSet([], [resource.Id]));
        var consumer = prediction.AffectedFiles.Single(file =>
            file.RelativePath == "src/Product/Views/Consumer.xaml");
        Require(
            consumer.Distance == 1
            && consumer.Confidence == ImpactConfidence.Medium,
            "a medium-confidence XAML resource edge must cap downstream file confidence");
    });
}

static void EvaluatedPackagesIncludeConditionalAndImportedReferences()
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-packages-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Write(root, "Packages.slnx",
            """
            <Solution>
              <Project Path="src/Product/Product.csproj" />
            </Solution>
            """);
        Write(root, "Directory.Build.props",
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Microsoft.Build.Locator"
                                  Version="1.11.2"
                                  PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        Write(root, "src/Product/Product.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Microsoft.Build.Framework"
                                  Version="17.11.48"
                                  ExcludeAssets="runtime"
                                  PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        Write(root, "src/Product/Product.cs",
            """
            namespace Product;
            public sealed class ProductFeature { }
            """);

        RunDotNet(root, "restore", "Packages.slnx", "--nologo");
        var explorer = new RoslynImpactExplorer();
        var evaluated = explorer
            .AnalyzeSolutionAsync(root, "Packages.slnx")
            .GetAwaiter()
            .GetResult();
        var evaluatedNodes = evaluated.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var packageNames = evaluated.Relationships
            .Where(edge => edge.Kind == ImpactRelationshipKind.PackageReferences)
            .Select(edge => evaluatedNodes[edge.TargetId].Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(
            evaluated.Availability == ImpactAvailability.Available,
            $"restored evaluated package evidence should be complete: {Diagnostics(evaluated)}");
        Require(
            packageNames.SetEquals(["Microsoft.Build.Framework", "Microsoft.Build.Locator"]),
            "evaluated assets should include both conditional and imported direct packages");

        File.Delete(Path.Combine(root, "src", "Product", "obj", "project.assets.json"));
        var unavailableEvaluation = explorer
            .AnalyzeSolutionAsync(root, "Packages.slnx")
            .GetAwaiter()
            .GetResult();
        Require(
            unavailableEvaluation.Availability == ImpactAvailability.Partial
            && unavailableEvaluation.Diagnostics.Any(item =>
                item.Code == "package-evaluated-evidence-unavailable"
                && item.RelativePath == "src/Product/Product.csproj"),
            "missing evaluated evidence must disclose conditional/imported package uncertainty");
    }
    finally
    {
        TryDelete(root);
    }
}

static void DuplicateXamlResourceKeysRetainEvidence()
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-xaml-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Write(root, "Themes.slnx",
            """
            <Solution>
              <Project Path="src/Product/Product.csproj" />
            </Solution>
            """);
        Write(root, "src/Product/Product.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write(root, "src/Product/Product.cs",
            """
            namespace Product;
            public sealed class ProductFeature { }
            """);
        Write(root, "src/Product/Themes/Dark.xaml",
            """
            <ResourceDictionary
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="AccentBrush" Color="#00AEEF" />
            </ResourceDictionary>
            """);
        Write(root, "src/Product/Themes/Light.xaml",
            """
            <ResourceDictionary
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="AccentBrush" Color="#006080" />
            </ResourceDictionary>
            """);
        Write(root, "src/Product/Views/MergedConsumer.xaml",
            """
            <Grid
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="../Themes/Dark.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </Grid.Resources>
              <Border Background="{DynamicResource AccentBrush}" />
            </Grid>
            """);
        Write(root, "src/Product/Views/AmbiguousConsumer.xaml",
            """
            <Border
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="{StaticResource AccentBrush}" />
            """);

        RunDotNet(root, "restore", "Themes.slnx", "--nologo");
        var snapshot = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, "Themes.slnx")
            .GetAwaiter()
            .GetResult();
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var mergedFile = snapshot.Nodes.Single(node =>
            node.Kind == ImpactNodeKind.XamlFile
            && node.QualifiedName == "src/Product/Views/MergedConsumer.xaml");
        var ambiguousFile = snapshot.Nodes.Single(node =>
            node.Kind == ImpactNodeKind.XamlFile
            && node.QualifiedName == "src/Product/Views/AmbiguousConsumer.xaml");
        var mergedEdges = snapshot.Relationships
            .Where(edge => edge.Kind == ImpactRelationshipKind.XamlUsesResource
                && edge.SourceId == mergedFile.Id)
            .ToArray();
        var ambiguousEdges = snapshot.Relationships
            .Where(edge => edge.Kind == ImpactRelationshipKind.XamlUsesResource
                && edge.SourceId == ambiguousFile.Id)
            .ToArray();
        Require(
            mergedEdges.Length == 1
            && mergedEdges[0].Confidence == ImpactConfidence.Medium
            && nodes[mergedEdges[0].TargetId].QualifiedName
                == "src/Product/Themes/Dark.xaml#AccentBrush",
            "a direct merged dictionary should deterministically select its resource definition");
        Require(
            ambiguousEdges.Length == 2
            && ambiguousEdges.All(edge => edge.Confidence == ImpactConfidence.Low)
            && ambiguousEdges.Select(edge => nodes[edge.TargetId].QualifiedName)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(
                [
                    "src/Product/Themes/Dark.xaml#AccentBrush",
                    "src/Product/Themes/Light.xaml#AccentBrush"
                ]),
            "unscoped duplicate keys should retain bounded low-confidence candidate edges");
        Require(
            snapshot.Availability == ImpactAvailability.Partial
            && snapshot.Diagnostics.Any(item =>
                item.Code == "xaml-resource-ambiguous"
                && item.RelativePath == "src/Product/Views/AmbiguousConsumer.xaml"),
            "ambiguous duplicate keys must make completeness uncertainty explicit");
    }
    finally
    {
        TryDelete(root);
    }
}

static void TestHistoryIsDeterministic()
{
    var now = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
    var observations = new[]
    {
        Observation("repeated", "run-1", now, TestObservationOutcome.Failed),
        Observation("repeated", "run-2", now.AddMinutes(1), TestObservationOutcome.Failed),
        Observation("flaky", "run-1", now, TestObservationOutcome.Passed),
        Observation("flaky", "run-2", now.AddMinutes(1), TestObservationOutcome.Failed),
        Observation("corrected", "run-1", now, TestObservationOutcome.Failed),
        Observation("corrected", "run-2", now.AddMinutes(1), TestObservationOutcome.Failed),
        Observation("corrected", "run-1", now.AddMinutes(2), TestObservationOutcome.Passed),
        Observation("failed-then-skipped", "run-1", now, TestObservationOutcome.Failed),
        Observation("failed-then-skipped", "run-2", now.AddMinutes(1), TestObservationOutcome.Skipped),
        Observation("skipped", "run-1", now, TestObservationOutcome.Skipped),
        Observation("unknown", "run-1", now, TestObservationOutcome.Unavailable)
    };
    var summaries = new TestReliabilityAnalyzer().Summarize(observations);
    Require(summaries.Single(item => item.TestNodeId == "repeated").State
        == TestReliabilityState.RepeatedlyFailing, "two terminal failures should be repeated");
    Require(summaries.Single(item => item.TestNodeId == "flaky").State
        == TestReliabilityState.Flaky, "mixed pass/fail history should be flaky");
    var corrected = summaries.Single(item => item.TestNodeId == "corrected");
    Require(
        corrected.State == TestReliabilityState.Flaky
        && corrected.LatestOutcome == TestObservationOutcome.Passed
        && corrected.ConsecutiveFailures == 0,
        "a later correction for a duplicate run ID must restore chronological terminal evidence");
    var failedThenSkipped = summaries.Single(item => item.TestNodeId == "failed-then-skipped");
    Require(
        failedThenSkipped.State == TestReliabilityState.Failing
        && failedThenSkipped.LatestOutcome == TestObservationOutcome.Failed,
        "a skipped observation must not replace the latest terminal result");
    var skippedOnly = summaries.Single(item => item.TestNodeId == "skipped");
    Require(
        skippedOnly.State == TestReliabilityState.Unavailable
        && skippedOnly.Availability == ImpactAvailability.Unavailable
        && skippedOnly.Skipped == 1,
        "skipped-only history must not be labelled passing");
    var unavailable = summaries.Single(item => item.TestNodeId == "unknown");
    Require(unavailable.State == TestReliabilityState.Unavailable
        && unavailable.Availability == ImpactAvailability.Unavailable,
        "unavailable observations must not invent a reliability result");
}

static void SnapshotIsDeterministicAndPrivate()
{
    WithFixture((root, first) =>
    {
        var second = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, "Fixture.slnx")
            .GetAwaiter()
            .GetResult();
        Require(first.Nodes.Select(node => node.Id).SequenceEqual(second.Nodes.Select(node => node.Id)),
            "node IDs/order should be deterministic");
        Require(first.Relationships.Select(edge => edge.Id).SequenceEqual(second.Relationships.Select(edge => edge.Id)),
            "relationship IDs/order should be deterministic");
        var publicPaths = first.Projects.Select(project => project.RelativePath)
            .Concat(first.Nodes.SelectMany(node => node.Definitions.Select(item => item.RelativePath)))
            .Concat(first.Relationships.SelectMany(edge => edge.Evidence.Select(item => item.RelativePath)))
            .Concat(first.Diagnostics.Select(item => item.RelativePath).OfType<string>())
            .ToArray();
        Require(publicPaths.All(path => !Path.IsPathRooted(path)
            && !path.Contains(root, StringComparison.OrdinalIgnoreCase)
            && !path.Split('/').Contains("..", StringComparer.Ordinal)),
            "public evidence must contain only safe relative paths");
    });
}

static void DuplicateAssemblySymbolsRemainProjectScoped()
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-duplicate-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Write(root, "Duplicate.slnx",
            """
            <Solution>
              <Project Path="src/One/One.csproj" />
              <Project Path="src/Two/Two.csproj" />
            </Solution>
            """);
        foreach (var project in new[] { "One", "Two" })
        {
            Write(root, $"src/{project}/{project}.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>Shared</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            Write(root, $"src/{project}/Thing.cs",
                """
                namespace Duplicate;
                public sealed class Thing
                {
                    public int Value() => 1;
                }
                """);
            Write(root, $"src/{project}/Thing.xaml",
                """
                <Grid
                  x:Class="Duplicate.Thing"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
                """);
        }

        RunDotNet(root, "restore", "Duplicate.slnx", "--nologo");
        var snapshot = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, "Duplicate.slnx")
            .GetAwaiter()
            .GetResult();
        var things = snapshot.Nodes
            .Where(node => node.Kind == ImpactNodeKind.Type
                && node.QualifiedName == "Duplicate.Thing")
            .ToArray();
        Require(
            things.Length == 2
            && things.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() == 2
            && things.Select(node => node.ProjectRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 2,
            "identical assembly/type identities from distinct projects must not collapse");
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var codeBehind = snapshot.Relationships
            .Where(edge => edge.Kind == ImpactRelationshipKind.XamlCodeBehind)
            .ToArray();
        Require(
            codeBehind.Length == 2
            && codeBehind.All(edge =>
                nodes[edge.SourceId].ProjectRelativePath?.Equals(
                    nodes[edge.TargetId].ProjectRelativePath,
                    StringComparison.OrdinalIgnoreCase) == true),
            "duplicate qualified x:Class names must resolve inside their own project");
    }
    finally
    {
        TryDelete(root);
    }
}

static void ReparsePointInputsAreRejected()
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-reparse-{Guid.NewGuid():N}");
    var outside = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-reparse-outside-{Guid.NewGuid():N}");
    var linkedFile = Path.Combine(root, "src", "Product", "LinkedFile.cs");
    var linkedDirectory = Path.Combine(root, "src", "Product", "LinkedDirectory");
    var linkedSolution = Path.Combine(root, "Linked.slnx");
    var linkedRoot = Path.Combine(
        Path.GetTempPath(),
        $"ai-arena-impact-reparse-root-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    try
    {
        Write(root, "Safe.slnx",
            """
            <Solution>
              <Project Path="src/Product/Product.csproj" />
            </Solution>
            """);
        Write(root, "src/Product/Product.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write(root, "src/Product/Safe.cs",
            """
            namespace Safe;
            public sealed class InsideRoot { }
            """);
        Write(outside, "LinkedFile.cs",
            """
            namespace Escaped;
            public sealed class ViaFileLink { }
            """);
        Write(outside, "LinkedDirectory/LinkedDirectory.cs",
            """
            namespace Escaped;
            public sealed class ViaDirectoryLink { }
            """);
        Write(outside, "Linked.slnx",
            """
            <Solution>
              <Project Path="External.csproj" />
            </Solution>
            """);
        Write(outside, "External.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        try
        {
            File.CreateSymbolicLink(linkedFile, Path.Combine(outside, "LinkedFile.cs"));
            Directory.CreateSymbolicLink(
                linkedDirectory,
                Path.Combine(outside, "LinkedDirectory"));
            File.CreateSymbolicLink(linkedSolution, Path.Combine(outside, "Linked.slnx"));
            Directory.CreateSymbolicLink(linkedRoot, root);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
        {
            Console.WriteLine(
                $"SKIP reparse-point fixture: symbolic links are unavailable ({exception.GetType().Name}).");
            return;
        }

        RunDotNet(root, "restore", "Safe.slnx", "--nologo");
        var explorer = new RoslynImpactExplorer();
        var snapshot = explorer
            .AnalyzeSolutionAsync(root, "Safe.slnx")
            .GetAwaiter()
            .GetResult();
        Require(snapshot.Availability == ImpactAvailability.Partial,
            "excluding linked source inputs must make the semantic snapshot partial");
        Require(snapshot.Nodes.Any(node => node.QualifiedName == "Safe.InsideRoot"),
            "safe in-root source should remain indexed");
        Require(snapshot.Nodes.All(node =>
                node.QualifiedName is not "Escaped.ViaFileLink"
                    and not "Escaped.ViaDirectoryLink"),
            "linked source content outside the root must not enter the semantic index");
        Require(snapshot.Diagnostics.Count(item => item.Code == "document-outside-root") >= 2,
            "file and directory reparse exclusions should be diagnosed");

        var linkedSolutionSnapshot = explorer
            .AnalyzeSolutionAsync(root, "Linked.slnx")
            .GetAwaiter()
            .GetResult();
        Require(linkedSolutionSnapshot.Availability == ImpactAvailability.Unavailable,
            "a solution-file reparse point must be rejected before semantic loading");

        var linkedRootSnapshot = explorer
            .AnalyzeSolutionAsync(linkedRoot, "Safe.slnx")
            .GetAwaiter()
            .GetResult();
        Require(linkedRootSnapshot.Availability == ImpactAvailability.Unavailable,
            "a workspace-root reparse point must be rejected before semantic loading");
    }
    finally
    {
        TryDeleteLink(linkedFile, isDirectory: false);
        TryDeleteLink(linkedSolution, isDirectory: false);
        TryDeleteLink(linkedDirectory, isDirectory: true);
        TryDeleteLink(linkedRoot, isDirectory: true);
        TryDelete(root);
        TryDelete(outside);
    }
}

static void DiagnosticsNeverExposeAbsolutePaths()
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-private-{Guid.NewGuid():N}");
    var outside = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-linked-{Guid.NewGuid():N}.cs");
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(outside, "public sealed class LinkedOutsideRoot { }");
        Write(root, "Private.slnx",
            """
            <Solution>
              <Project Path="Private.csproj" />
            </Solution>
            """);
        var escapedOutside = System.Security.SecurityElement.Escape(outside)
            ?? throw new InvalidOperationException("fixture path could not be escaped");
        Write(root, "Private.csproj",
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{escapedOutside}" />
              </ItemGroup>
            </Project>
            """);
        RunDotNet(root, "restore", "Private.slnx", "--nologo");
        var snapshot = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, Path.Combine(root, "Private.slnx"))
            .GetAwaiter()
            .GetResult();
        Require(snapshot.Availability == ImpactAvailability.Partial,
            "excluding a linked source outside the root should make analysis partial");
        Require(snapshot.Diagnostics.Any(item => item.Code == "document-outside-root"),
            "excluded linked source should have a diagnostic");
        Require(snapshot.Diagnostics.All(item =>
                !item.Message.Contains(root, StringComparison.OrdinalIgnoreCase)
                && !item.Message.Contains(outside, StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(
                    item.Message,
                    @"(?:[A-Za-z]:[\\/]|\\\\)",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
            "diagnostic messages must not expose any absolute path");
    }
    finally
    {
        TryDelete(root);
        try
        {
            File.Delete(outside);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

static bool Relationship(
    ImpactSnapshot snapshot,
    ImpactRelationshipKind kind,
    string source,
    string target)
{
    var nodes = snapshot.Nodes.ToDictionary(node => node.Id);
    return snapshot.Relationships.Any(edge =>
        edge.Kind == kind
        && nodes[edge.SourceId].QualifiedName == source
        && nodes[edge.TargetId].QualifiedName == target);
}

static TestRunObservation Observation(
    string id,
    string run,
    DateTimeOffset at,
    TestObservationOutcome outcome) =>
    new(id, $"Tests.{id}", "tests/Product.Tests/Product.Tests.csproj", run, at, outcome);

static void WithFixture(Action<string, ImpactSnapshot> action)
{
    var root = Path.Combine(Path.GetTempPath(), $"ai-arena-impact-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        WriteFixture(root);
        RunDotNet(root, "restore", "Fixture.slnx", "--nologo");
        var solution = Path.Combine(root, "Fixture.slnx");
        var snapshot = new RoslynImpactExplorer()
            .AnalyzeSolutionAsync(root, solution)
            .GetAwaiter()
            .GetResult();
        action(root, snapshot);
    }
    finally
    {
        TryDelete(root);
    }
}

static void WriteFixture(string root)
{
    Write(root, "Fixture.slnx",
        """
        <Solution>
          <Folder Name="/src/">
            <Project Path="src/Product/Product.csproj" />
          </Folder>
          <Folder Name="/tests/">
            <Project Path="tests/Product.Tests/Product.Tests.csproj" />
          </Folder>
        </Solution>
        """);
    Write(root, "src/Product/Product.csproj",
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" PrivateAssets="all" />
          </ItemGroup>
        </Project>
        """);
    Write(root, "src/Product/Greeter.cs",
        """
        namespace Product;

        public interface IClock
        {
            string Now();
        }

        public sealed class Clock : IClock
        {
            public string Now() => "now";
        }

        public sealed class Greeter(IClock clock)
        {
            public string Greet() => clock.Now();
        }

        public static class Unused
        {
            public static int Compute() => 42;
        }

        public static class CaseSensitive
        {
            public static int Foo() => 1;
            public static int foo() => 2;
        }

        public partial class MainView
        {
        }
        """);
    Write(root, "src/Product/Views/MainView.xaml",
        """
        <Grid
          x:Class="Product.MainView"
          xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid.Resources>
            <SolidColorBrush x:Key="AccentBrush" Color="#00AEEF" />
            <SolidColorBrush x:Key="accentBrush" Color="#FFAA00" />
          </Grid.Resources>
          <Border Background="{StaticResource AccentBrush}" />
        </Grid>
        """);
    Write(root, "src/Product/Views/Consumer.xaml",
        """
        <Border
          xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          Background="{StaticResource AccentBrush}" />
        """);
    Write(root, "tests/Product.Tests/Product.Tests.csproj",
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../../src/Product/Product.csproj" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
          </ItemGroup>
        </Project>
        """);
    Write(root, "tests/Product.Tests/GreeterTests.cs",
        """
        namespace Product.Tests;

        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class FactAttribute : System.Attribute
        {
        }

        public sealed class GreeterTests
        {
            [Fact]
            public void Greets()
            {
                var value = new Product.Greeter(new Product.Clock()).Greet();
                if (value.Length == 0)
                {
                    throw new System.InvalidOperationException();
                }
            }
        }
        """);
}

static void Write(string root, string relativePath, string contents)
{
    var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
}

static void RunDotNet(string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var actual = Process.Start(start) ?? throw new InvalidOperationException("dotnet could not be started");
    var output = actual.StandardOutput.ReadToEnd();
    var error = actual.StandardError.ReadToEnd();
    actual.WaitForExit();
    if (actual.ExitCode != 0)
    {
        throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed:\n{output}\n{error}");
    }
}

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void TryDeleteLink(string path, bool isDirectory)
{
    try
    {
        if (isDirectory && Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else if (!isDirectory && File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static string Diagnostics(ImpactSnapshot snapshot) =>
    string.Join(Environment.NewLine, snapshot.Diagnostics.Select(item =>
        $"{item.Severity} {item.Code}: {item.Message} ({item.RelativePath})"));

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
