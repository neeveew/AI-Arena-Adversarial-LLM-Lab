using AIArena.Core.Models;
using AIArena.Core.Services;

internal static class DotNetSolutionDoctorTests
{
    internal static void DiscoversSolutionsProjectsAndProjectCorrectCommands()
    {
        WithFixture(root =>
        {
            WriteProject(
                root,
                "src/Core/Core.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net10.0;net9.0</TargetFrameworks>
                  </PropertyGroup>
                </Project>
                """);
            WriteProject(root, "src/Core/obj/project.assets.json", "{}");
            WriteProject(
                root,
                "src/Arena App/Arena App.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <TargetFramework>net10.0-windows</TargetFramework>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Core/Core.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(
                root,
                "tests/Unit.Tests/Unit.Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(
                root,
                "tests/Harness.Tests/Harness.Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/Core/Core.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(
                root,
                "Arena $(whoami) & Workspace's.sln",
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "src\Core\Core.csproj", "{00000000-0000-0000-0000-000000000001}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Arena App", "src\Arena App\Arena App.csproj", "{00000000-0000-0000-0000-000000000002}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Unit.Tests", "tests\Unit.Tests\Unit.Tests.csproj", "{00000000-0000-0000-0000-000000000003}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Harness.Tests", "tests\Harness.Tests\Harness.Tests.csproj", "{00000000-0000-0000-0000-000000000004}"
                EndProject
                Global
                EndGlobal
                """);
            WriteProject(
                root,
                "Arena $(whoami) & Workspace's.slnx",
                """
                <Solution>
                  <Folder Name="/src/">
                    <Project Path="src/Core/Core.csproj" />
                    <Project Path="src/Arena App/Arena App.csproj" />
                  </Folder>
                  <Folder Name="/tests/">
                    <Project Path="tests/Unit.Tests/Unit.Tests.csproj" />
                    <Project Path="tests/Harness.Tests/Harness.Tests.csproj" />
                  </Folder>
                </Solution>
                """);

            var service = new DotNetWorkspaceIntelligenceService();
            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();

            Require(snapshot.Projects.Count == 4, "fixture should discover four projects");
            Require(snapshot.Solutions.Count == 2, "fixture should discover .sln and .slnx solutions");
            Require(snapshot.Solutions.All(solution => solution.ProjectRelativePaths.Count == 4), "both solution formats should contain four projects");

            var app = Project(snapshot, "src/Arena App/Arena App.csproj");
            Require(app.OutputType == DotNetProjectOutputType.WinExe, "WPF app should be WinExe");
            Require(app.UseWpf, "UseWPF should be discovered");
            Require(app.ProjectReferenceRelativePaths.SequenceEqual(["src/Core/Core.csproj"]), "project reference should be normalized workspace-relative");
            Require(app.RestoreState == DotNetRestoreState.AssetsMissing, "clean fixture should report missing restore assets");
            Require(Project(snapshot, "src/Core/Core.csproj").RestoreState == DotNetRestoreState.AssetsAvailable, "existing project.assets.json should report available restore assets");
            Require(snapshot.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW114"), "missing restore assets should produce a bounded offline diagnostic");
            Require(!snapshot.IsPartial, "deterministic missing-assets readiness alone should not make discovery partial");

            var conventionalTests = Project(snapshot, "tests/Unit.Tests/Unit.Tests.csproj");
            Require(conventionalTests.IsConventionalTestProject, "Microsoft.NET.Test.Sdk should classify a conventional test project");
            var harness = Project(snapshot, "tests/Harness.Tests/Harness.Tests.csproj");
            Require(harness.IsExecutableTestHarness, "test-like executable should classify as an executable test harness");

            var builds = snapshot.CommandPlans.Where(plan => plan.Kind == DotNetCommandKind.Build).ToArray();
            Require(builds.Length == 6, "both solutions and every project should have build plans");
            Require(builds.All(plan => plan.Arguments.Contains("--no-restore")), "every build should default to --no-restore");
            var restores = snapshot.CommandPlans.Where(plan => plan.Kind == DotNetCommandKind.Restore).ToArray();
            Require(restores.All(plan =>
                plan.RequiresUserApproval
                && plan.RequiresSeparateApproval
                && plan.NetworkRisk == DotNetNetworkRisk.MayAccessConfiguredPackageSources), "restore should retain a separate network-risk approval boundary");

            var conventionalPlan = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Test && plan.TargetRelativePath == conventionalTests.RelativePath);
            Require(conventionalPlan.Arguments.Contains("--no-build"), "conventional tests should run the already-built project");
            var harnessPlan = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Run && plan.TargetRelativePath == harness.RelativePath);
            Require(harnessPlan.Arguments.SequenceEqual(["run", "--project", harness.RelativePath, "--no-build"]), "harness should use dotnet run --project with --no-build");

            var spacedSolutionPlans = snapshot.CommandPlans.Where(plan =>
                plan.Kind == DotNetCommandKind.Build && plan.TargetKind == DotNetCommandTargetKind.Solution).ToArray();
            Require(
                spacedSolutionPlans.All(plan =>
                    plan.DisplayInvocation.Contains(
                        "'Arena $(whoami) & Workspace''s.",
                        StringComparison.Ordinal)),
                "PowerShell previews should single-quote interpolation, metacharacters, and apostrophes");
            Require(
                spacedSolutionPlans.All(plan => plan.Shell == DotNetCommandShell.PowerShell),
                "the PowerShell-encoded display invocation must declare its shell");
            Require(AllPublicPathsAreRelative(snapshot, root), "public path fields should remain workspace-relative");
        });

        WithFixture(root =>
        {
            const string literalProjectPath = "src/Literal $(whoami)/Literal $(whoami).csproj";
            WriteProject(
                root,
                literalProjectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            WriteProject(
                root,
                "Literal.sln",
                """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Literal", "src\Literal $(whoami)\Literal $(whoami).csproj", "{00000000-0000-0000-0000-000000000001}"
                EndProject
                Global
                EndGlobal
                """);
            WriteProject(
                root,
                "Literal.slnx",
                """
                <Solution>
                  <Project Path="src/Literal $(whoami)/Literal $(whoami).csproj" />
                </Solution>
                """);
            WriteProject(
                root,
                "Unmatched Literal.slnx",
                """
                <Solution>
                  <Project Path="src/Missing $(whoami)/Missing $(whoami).csproj" />
                </Solution>
                """);

            var snapshot = new DotNetWorkspaceIntelligenceService()
                .DiscoverAsync(root)
                .GetAwaiter()
                .GetResult();

            Require(snapshot.Projects.Count == 1, "literal-expression fixture should discover its project");
            Require(snapshot.Solutions.Count == 3, "literal-expression fixture should discover valid and unmatched solutions");
            var validSolutions = snapshot.Solutions
                .Where(solution => solution.RelativePath is "Literal.sln" or "Literal.slnx")
                .ToArray();
            Require(
                validSolutions.Length == 2
                && validSolutions.All(solution =>
                    !solution.IsPartial
                    && solution.ProjectRelativePaths.SequenceEqual([literalProjectPath])),
                "solution project paths containing literal $() text should use normal safe membership validation");
            var unmatchedSolution = snapshot.Solutions.Single(solution => solution.RelativePath == "Unmatched Literal.slnx");
            Require(
                unmatchedSolution.IsPartial && unmatchedSolution.ProjectRelativePaths.Count == 0,
                "an unmatched literal $() project path should be partial rather than silently accepted or ignored");
            Require(
                snapshot.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "DNW202"
                    && diagnostic.RelativePath == unmatchedSolution.RelativePath),
                "unmatched literal $() membership should emit DNW202 against the affected solution");

            var literalProjectBuild = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Build
                && plan.TargetKind == DotNetCommandTargetKind.Project
                && plan.TargetRelativePath == literalProjectPath);
            Require(
                literalProjectBuild.Arguments.SequenceEqual(["build", literalProjectPath, "--no-restore"]),
                "literal $() text should be preserved exactly in typed project build arguments");
            Require(
                literalProjectBuild.DisplayInvocation
                    == "dotnet build 'src/Literal $(whoami)/Literal $(whoami).csproj' --no-restore",
                "the PowerShell project-build preview should safely single-quote literal $() text");
        });
    }

    internal static void ParsesCompilerMsBuildAndExecutableHarnessEvidence()
    {
        WithFixture(root =>
        {
            WriteProject(
                root,
                "src/App/App.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            File.WriteAllText(Path.Combine(root, "src", "App", "Broken.cs"), "class Broken { }");

            var service = new DotNetWorkspaceIntelligenceService();
            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();
            var command = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Build && plan.TargetRelativePath == "src/App/App.csproj");
            var sourcePath = Path.Combine(root, "src", "App", "Broken.cs");
            var projectPath = Path.Combine(root, "src", "App", "App.csproj");
            var outsidePath = Path.Combine(Path.GetTempPath(), "outside solution doctor", "secret source.cs");
            var stdout = $"""
                {sourcePath}(12,9): error CS1002: ; expected while reading {sourcePath} [{projectPath}]
                {outsidePath}(2,1): warning CS0168: variable is declared at {outsidePath} [{projectPath}]
                    1 Warning(s)
                    2 Error(s)
                PASS loads fixture
                PASS parses output
                """;
            var stderr = $"""
                MSBUILD : error MSB1009: Project file does not exist.
                FAIL preserves evidence: expected detail was absent at {outsidePath}
                """;

            var result = new DotNetOutputParser().Parse(
                root,
                command,
                exitCode: 1,
                stdout,
                stderr,
                rawOutputReferenceId: "receipt-42");

            Require(!result.Succeeded, "non-zero harness/build result should fail");
            Require(result.WarningCount == 1 && result.ErrorCount == 2, "separate MSBuild summary lines should retain both totals");
            Require(result.Diagnostics.Count == 3, "compiler and MSBuild diagnostics should be structured");
            Require(result.Diagnostics[0].Code == "CS1002", "compiler code should be preserved");
            Require(result.Diagnostics[0].RelativePath == "src/App/Broken.cs", "in-workspace source should become relative");
            Require(result.Diagnostics[0].Line == 12 && result.Diagnostics[0].Column == 9, "compiler location should be parsed");
            Require(result.Diagnostics[0].ProjectRelativePath == "src/App/App.csproj", "diagnostic project should become relative");
            Require(!result.Diagnostics[0].Message.Contains(root, StringComparison.OrdinalIgnoreCase), "structured compiler message should replace the workspace root");
            Require(
                result.Diagnostics[0].Message.Contains("./src", StringComparison.Ordinal)
                && result.Diagnostics[0].Message.Contains("Broken.cs", StringComparison.Ordinal),
                "in-workspace paths inside messages should remain useful relative evidence");
            Require(result.Diagnostics[1].RelativePath is null, "outside-workspace source path should not enter structured models");
            Require(!result.Diagnostics[1].Message.Contains(outsidePath, StringComparison.OrdinalIgnoreCase), "structured diagnostic message should redact outside rooted paths");
            Require(!result.Diagnostics[1].Message.Contains("outside solution doctor", StringComparison.OrdinalIgnoreCase), "unquoted outside paths containing spaces should be fully redacted");
            Require(result.Diagnostics[2].Code == "MSB1009" && result.Diagnostics[2].RelativePath is null, "MSBuild tool prefixes should not be misreported as source paths");
            Require(result.TestTotals == new DotNetTestTotals(2, 1, 0, 3), "PASS/FAIL harness output should derive totals");
            Require(result.FailingTests.Count == 1, "FAIL output should create bounded failing-test evidence");
            Require(result.FailingTests[0].Name == "preserves evidence", "harness failure name should be parsed");
            Require(result.FailingTests[0].Detail?.Contains("<outside-workspace-path>", StringComparison.Ordinal) == true, "harness failure detail should redact outside rooted paths");
            Require(result.RawOutput.ReferenceId == "receipt-42", "raw output should keep the caller reference");
            Require(result.RawOutput.StandardOutput == stdout && result.RawOutput.StandardError == stderr, "raw stdout and stderr should remain exact");

            var boundedHarness = new DotNetOutputParser().Parse(
                root,
                command,
                1,
                "FAIL first failure: one\nFAIL second failure: two",
                "",
                maximumFailingTests: 1);
            Require(boundedHarness.FailingTests.Count == 1, "failing-test evidence should obey its bound");
            Require(boundedHarness.TestTotals == new DotNetTestTotals(0, 2, 0, 2), "harness totals should remain complete when evidence is capped");
            Require(boundedHarness.StructuredEvidenceLimitReached, "capped harness evidence should be disclosed");

            var retry = service.CreateNarrowedRetryPlan(snapshot, result)
                ?? throw new InvalidOperationException("compiler evidence did not produce a retry");
            Require(retry.IsNarrowed, "compiler evidence should narrow a build retry");
            Require(retry.Command.TargetRelativePath == "src/App/App.csproj", "retry should target the project that emitted the error");
            Require(retry.Command.Arguments.SequenceEqual(["build", "src/App/App.csproj", "--no-restore"]), "narrowed build should remain offline by default");
        });

        WithFixture(root =>
        {
            const string projectFile = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """;
            WriteProject(root, "src/Parent/ParentProjectWithAnIntentionallyLongName.csproj", projectFile);
            WriteProject(root, "src/Parent/N/N.csproj", projectFile);
            WriteProject(root, "src/Parent/N/Broken.cs", "class Broken { }");
            WriteProject(root, "src/Shared/A.csproj", projectFile);
            WriteProject(root, "src/Shared/ProjectWithAnIntentionallyLongName.csproj", projectFile);
            WriteProject(root, "src/Shared/Broken.cs", "class Broken { }");
            WriteProject(root, "src/App/App.csproj", projectFile);
            WriteProject(root, "src/App2/Broken.cs", "class Broken { }");

            var service = new DotNetWorkspaceIntelligenceService();
            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();
            var parser = new DotNetOutputParser();

            var parentCommand = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Build
                && plan.TargetRelativePath == "src/Parent/ParentProjectWithAnIntentionallyLongName.csproj");
            var nestedSourcePath = Path.Combine(root, "src", "Parent", "N", "Broken.cs");
            var nestedResult = parser.Parse(
                root,
                parentCommand,
                1,
                $"{nestedSourcePath}(3,2): error CS1002: ; expected",
                "");

            Require(nestedResult.Diagnostics.Single().ProjectRelativePath is null, "nested fixture should exercise inferred ownership");
            var nestedRetry = service.CreateNarrowedRetryPlan(snapshot, nestedResult)
                ?? throw new InvalidOperationException("nested source evidence did not produce a retry");
            Require(
                nestedRetry.Command.TargetRelativePath == "src/Parent/N/N.csproj",
                "inferred ownership should prefer the deepest containing project directory, not the longest project path");

            var sharedCommand = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Build
                && plan.TargetRelativePath == "src/Shared/A.csproj");
            var sharedSourcePath = Path.Combine(root, "src", "Shared", "Broken.cs");
            var sharedResult = parser.Parse(
                root,
                sharedCommand,
                1,
                $"{sharedSourcePath}(4,1): error CS1002: ; expected",
                "");

            Require(sharedResult.Diagnostics.Single().ProjectRelativePath is null, "shared-directory fixture should exercise inferred ownership");
            Require(
                service.CreateNarrowedRetryPlan(snapshot, sharedResult) is null,
                "inferred ownership should refuse to choose between projects in the same deepest directory");

            var sharedProjectPath = Path.Combine(root, "src", "Shared", "A.csproj");
            var explicitSharedResult = parser.Parse(
                root,
                sharedCommand,
                1,
                $"{sharedSourcePath}(5,1): error CS1002: ; expected [{sharedProjectPath}]",
                "");
            Require(
                explicitSharedResult.Diagnostics.Single().ProjectRelativePath == "src/Shared/A.csproj",
                "shared-directory fixture should preserve explicit project evidence");
            var explicitSharedRetry = service.CreateNarrowedRetryPlan(snapshot, explicitSharedResult)
                ?? throw new InvalidOperationException("explicit shared-directory project evidence did not produce a retry");
            Require(
                explicitSharedRetry.Command.TargetRelativePath == "src/Shared/A.csproj",
                "explicit project evidence should remain authoritative when path-only ownership is ambiguous");

            var appCommand = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Build
                && plan.TargetRelativePath == "src/App/App.csproj");
            var app2SourcePath = Path.Combine(root, "src", "App2", "Broken.cs");
            var boundaryResult = parser.Parse(
                root,
                appCommand,
                1,
                $"{app2SourcePath}(6,1): error CS1002: ; expected",
                "");
            Require(
                service.CreateNarrowedRetryPlan(snapshot, boundaryResult) is null,
                "project-directory matching should not treat sibling App2 as a child of App");
        });
    }

    internal static void PlansFocusedConventionalTestRetry()
    {
        WithFixture(root =>
        {
            WriteProject(
                root,
                "tests/Focused.Tests/Focused.Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                </Project>
                """);

            var service = new DotNetWorkspaceIntelligenceService();
            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();
            var command = snapshot.CommandPlans.Single(plan => plan.Kind == DotNetCommandKind.Test);
            var output = """
                Failed Namespace.FocusedTests.Handles_failure [18 ms]
                Failed!  - Failed: 1, Passed: 3, Skipped: 0, Total: 4, Duration: 40 ms
                """;
            var result = new DotNetOutputParser().Parse(root, command, 1, output, "");
            var retry = service.CreateNarrowedRetryPlan(snapshot, result)
                ?? throw new InvalidOperationException("failing test did not produce a retry");

            Require(result.TestTotals == new DotNetTestTotals(3, 1, 0, 4), "vstest totals should be parsed");
            Require(retry.IsNarrowed, "a failing conventional test should get a focused retry");
            Require(retry.Command.Arguments.Contains("--no-build"), "focused test retry should preserve --no-build");
            Require(retry.Command.Arguments.Contains("--filter"), "focused test retry should include a typed filter argument");
            Require(retry.Command.Arguments.Contains("FullyQualifiedName=Namespace.FocusedTests.Handles_failure"), "focused retry should name the failing test");

            var modern = new DotNetOutputParser().Parse(
                root,
                command,
                1,
                "Test summary: total: 4, failed: 1, succeeded: 2, skipped: 1, duration: 1.2s",
                "");
            Require(modern.TestTotals == new DotNetTestTotals(2, 1, 1, 4), "modern dotnet test summary should be parsed");
            Require(modern.ErrorCount == 0, "test failures should not be conflated with compiler/MSBuild error totals");
            Require(!modern.Succeeded, "reported test failures should still fail the structured outcome");

            var contradictory = new DotNetOutputParser().Parse(
                root,
                command,
                0,
                """
                Failed Namespace.FocusedTests.Contradictory_evidence [2 ms]
                Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 12 ms
                """,
                "");
            Require(contradictory.ExitCode == 0, "contradiction fixture should preserve the successful exit code");
            Require(contradictory.TestTotals?.Failed == 0, "contradiction fixture should preserve the zero-failure summary");
            Require(contradictory.FailingTests.Count == 1, "recognized failing evidence should still be retained");
            Require(!contradictory.Succeeded, "recognized failing evidence must override a contradictory successful exit and summary");

            var multiTarget = new DotNetOutputParser().Parse(
                root,
                command,
                1,
                """
                Test summary: total: 4, failed: 1, succeeded: 2, skipped: 1, duration: 1.2s
                Test summary: total: 3, failed: 0, succeeded: 3, skipped: 0, duration: 0.8s
                """,
                "");
            Require(multiTarget.TestTotals == new DotNetTestTotals(5, 1, 1, 7), "multi-target test summaries should aggregate to command totals");

            var unsafeFailure = new DotNetOutputParser().Parse(
                root,
                command,
                1,
                "Failed Namespace.FocusedTests.Case(value & injected) [2 ms]",
                "");
            Require(service.CreateNarrowedRetryPlan(snapshot, unsafeFailure) is null, "unsafe or non-FQN test display names should not become shell/filter retries");
        });
    }

    internal static void ReportsPartialProjectsAndHonorsCancellation()
    {
        WithFixture(root =>
        {
            WriteProject(
                root,
                "src/Broken/Broken.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../../outside.csproj" />
                    <ProjectReference Include="../Missing/Missing.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "src/Malformed/Malformed.csproj", "<Project><PropertyGroup>");
            WriteProject(
                root,
                "src/Conditional/Conditional.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Choose>
                    <When Condition="'$(Configuration)' == 'Debug'">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <UseWPF>true</UseWPF>
                      </PropertyGroup>
                    </When>
                  </Choose>
                </Project>
                """);

            var service = new DotNetWorkspaceIntelligenceService();
            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();
            Require(snapshot.IsPartial, "malformed or outside references should mark the snapshot partial");
            Require(snapshot.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW112"), "outside project references should be diagnosed");
            Require(snapshot.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW117"), "missing in-workspace project references should be diagnosed");
            Require(snapshot.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW101"), "malformed project should be diagnosed");
            var conditional = Project(snapshot, "src/Conditional/Conditional.csproj");
            Require(conditional.IsPartial && conditional.OutputType == DotNetProjectOutputType.Unknown, "conditional MSBuild classifications should remain partial and conservative");
            Require(conditional.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW116"), "unevaluated MSBuild inputs should be diagnosed");
            Require(!snapshot.CommandPlans.Any(plan =>
                plan.Kind == DotNetCommandKind.Run
                && plan.TargetRelativePath == conditional.RelativePath), "uncertain executable classifications should not produce Run actions");
            Require(AllPublicPathsAreRelative(snapshot, root), "partial diagnostics must not expose absolute paths");

            var diagnosticsBound = service.DiscoverAsync(
                root,
                new DotNetDiscoveryOptions(MaxDiagnostics: 2)).GetAwaiter().GetResult();
            Require(diagnosticsBound.Projects.All(project => project.Diagnostics.Count <= 2), "per-project diagnostics should obey MaxDiagnostics");
            Require(
                Project(diagnosticsBound, "src/Broken/Broken.csproj").Diagnostics.Any(diagnostic => diagnostic.Code == "DNW099"),
                "truncated per-project diagnostics should be disclosed");

            var commandsBound = service.DiscoverAsync(
                root,
                new DotNetDiscoveryOptions(MaxCommandPlans: 1)).GetAwaiter().GetResult();
            Require(commandsBound.CommandPlans.Count == 1, "command plans should obey MaxCommandPlans");
            Require(commandsBound.IsPartial && commandsBound.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW009"), "command-plan truncation should mark and diagnose a partial snapshot");
            AssertReparseProjectFileIsSkipped(root, service);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                service.DiscoverAsync(root, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("cancelled discovery unexpectedly completed");
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    internal static void ValidatesAiArenaProductSolutions()
    {
        var root = FindRepositoryRoot();
        var snapshot = new DotNetWorkspaceIntelligenceService()
            .DiscoverAsync(root)
            .GetAwaiter()
            .GetResult();
        var expectedProjects = new HashSet<string>(
            [
                "src/AIArena.Core/AIArena.Core.csproj",
                "src/AIArena.Wpf/AIArena.Wpf.csproj",
                "tests/AIArena.Tests/AIArena.Tests.csproj",
                "tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj"
            ],
            StringComparer.OrdinalIgnoreCase);

        foreach (var solutionPath in new[] { "AI Arena - WPF.sln", "AI Arena.slnx" })
        {
            var solution = snapshot.Solutions.Single(candidate =>
                candidate.RelativePath.Equals(solutionPath, StringComparison.OrdinalIgnoreCase));
            Require(
                expectedProjects.SetEquals(solution.ProjectRelativePaths),
                $"{solutionPath} should contain the four AI Arena product projects");
            Require(!solution.IsPartial, $"{solutionPath} should resolve without partial membership");
        }

        var productProjects = snapshot.Projects
            .Where(project => expectedProjects.Contains(project.RelativePath))
            .ToArray();
        Require(productProjects.Length == 4, "the real product solutions should resolve all four projects");
        foreach (var project in productProjects)
        {
            var projectDirectory = Path.GetDirectoryName(
                Path.Combine(root, project.RelativePath.Replace('/', Path.DirectorySeparatorChar)))!;
            var assetsExist = File.Exists(Path.Combine(projectDirectory, "obj", "project.assets.json"));
            var expectedRestoreState = assetsExist
                ? DotNetRestoreState.AssetsAvailable
                : DotNetRestoreState.AssetsMissing;
            Require(
                project.RestoreState == expectedRestoreState,
                $"{project.Name} should report restore state from its actual local assets without attempting a restore");
        }

        var app = Project(snapshot, "src/AIArena.Wpf/AIArena.Wpf.csproj");
        Require(app.UseWpf && app.OutputType == DotNetProjectOutputType.WinExe, "the product app should classify as a WPF WinExe");

        var harnesses = productProjects
            .Where(project => project.IsExecutableTestHarness)
            .OrderBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Require(harnesses.Length == 2, "both product test projects should classify as executable harnesses");
        Require(
            harnesses.Select(project => project.RelativePath).SequenceEqual(
                [
                    "tests/AIArena.Tests/AIArena.Tests.csproj",
                    "tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj"
                ],
                StringComparer.OrdinalIgnoreCase),
            "the executable harness classification should identify the two real test projects");
        foreach (var harness in harnesses)
        {
            var run = snapshot.CommandPlans.Single(plan =>
                plan.Kind == DotNetCommandKind.Run
                && plan.TargetRelativePath.Equals(harness.RelativePath, StringComparison.OrdinalIgnoreCase));
            Require(
                run.Arguments.SequenceEqual(["run", "--project", harness.RelativePath, "--no-build"]),
                $"the {harness.Name} harness should use dotnet run --project with --no-build");
        }

        Require(AllPublicPathsAreRelative(snapshot, root), "the real workspace contract must not expose absolute paths");
    }

    private static void AssertReparseProjectFileIsSkipped(
        string root,
        DotNetWorkspaceIntelligenceService service)
    {
        var outsideDirectory = Path.Combine(
            Path.GetDirectoryName(root)!,
            $"{Path.GetFileName(root)}-outside");
        var outsideProject = Path.Combine(outsideDirectory, "Linked.csproj");
        var linkPath = Path.Combine(root, "src", "Linked.csproj");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(
            outsideProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, outsideProject);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                    or IOException
                                                    or PlatformNotSupportedException)
            {
                return;
            }

            var snapshot = service.DiscoverAsync(root).GetAwaiter().GetResult();
            Require(
                !snapshot.Projects.Any(project => project.RelativePath == "src/Linked.csproj"),
                "reparse-point project files must not be parsed");
            Require(
                snapshot.Diagnostics.Any(diagnostic => diagnostic.Code == "DNW008"),
                "skipped reparse points should produce a boundary diagnostic");
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    private static DotNetProjectInfo Project(DotNetWorkspaceSnapshot snapshot, string relativePath)
    {
        return snapshot.Projects.Single(project =>
            project.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllPublicPathsAreRelative(DotNetWorkspaceSnapshot snapshot, string root)
    {
        var paths = snapshot.Solutions.Select(solution => solution.RelativePath)
            .Concat(snapshot.Solutions.SelectMany(solution => solution.ProjectRelativePaths))
            .Concat(snapshot.Projects.Select(project => project.RelativePath))
            .Concat(snapshot.Projects.SelectMany(project => project.ProjectReferenceRelativePaths))
            .Concat(snapshot.CommandPlans.Select(plan => plan.TargetRelativePath))
            .Concat(snapshot.CommandPlans.Select(plan => plan.WorkingDirectoryRelativePath))
            .Concat(snapshot.Diagnostics.Select(diagnostic => diagnostic.RelativePath).OfType<string>())
            .ToArray();
        return paths.All(path => !Path.IsPathRooted(path) && !path.Contains(root, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AI Arena - WPF.sln"))
                    && File.Exists(Path.Combine(directory.FullName, "AI Arena.slnx"))
                    && File.Exists(Path.Combine(directory.FullName, "src", "AIArena.Core", "AIArena.Core.csproj")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not locate the AI Arena repository root.");
    }

    private static void WriteProject(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WithFixture(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-solution-doctor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
