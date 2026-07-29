using AIArena.Core.Models;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static void AgentSolutionDoctorPresentsTypedWorkspaceActions()
    {
        var snapshot = CreateDotNetSnapshot("Arena.App", "src/Arena.App/Arena.App.csproj");

        var profile = AgentDotNetSolutionDoctorService.FormatWorkspaceProfile(
            "Project signals: .NET",
            snapshot);
        Require(profile.Contains("1 solution(s), 1 project(s), ready", StringComparison.Ordinal), "typed profile should report solution and project counts");
        Require(profile.Contains("dotnet build Arena.sln --no-restore", StringComparison.Ordinal), "typed profile should offer the solution build without restore");
        Require(profile.Contains("dotnet run --project src/Arena.App/Arena.App.csproj --no-build", StringComparison.Ordinal), "executable harness should use dotnet run --project --no-build");
        Require(profile.Contains("separate approval", StringComparison.OrdinalIgnoreCase), "restore should be labelled as a separate approval");
        Require(!profile.Contains("dotnet test", StringComparison.OrdinalIgnoreCase), "an executable harness must not be presented as dotnet test");

        var build = AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build .\\Arena.sln --no-restore");
        Require(build is { Kind: DotNetCommandKind.Build, TargetKind: DotNetCommandTargetKind.Solution }, "command matching should tolerate workspace-relative path spelling");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet.exe build .\\Arena.sln --no-restore") == build, "dotnet.exe should retain the same exact typed command identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln") is null, "a build that omits --no-restore must not inherit the typed offline plan identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln --no-restore -t:Clean") is null, "a semantically different build target must not inherit the canonical build identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln; dotnet restore Arena.sln") is null, "compound commands should not receive a misleading typed identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln | Out-Null") is null, "pipelined commands should not receive canonical structured identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln && echo masked") is null, "chained commands should not receive canonical structured identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Arena.sln > build.log") is null, "redirected commands should not receive canonical structured identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build Missing.csproj --no-restore") is null, "an unrelated explicit target must not fall back to the only solution");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build ../Arena.sln --no-restore") is null, "a parent-relative target must not be normalized onto a workspace target");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(snapshot, "dotnet build /Arena.sln --no-restore") is null, "an absolute-looking target must not be normalized onto a workspace target");

        var verification = AgentDotNetSolutionDoctorService.RecommendedVerificationPlans(snapshot);
        Require(verification.Count == 2, "solution build and executable harness should be the two verification actions");
        Require(verification.All(plan => plan.Kind != DotNetCommandKind.Restore), "restore should never enter automatic verification recommendations");

        const string hostilePath = "src/$([System.IO.File]::WriteAllText('pwned','x')) & O'Brien/O'Brien.csproj";
        var hostileSnapshot = CreateDotNetSnapshot("Hostile.Project", hostilePath);
        var hostileRun = hostileSnapshot.CommandPlans.Single(plan => plan.Kind == DotNetCommandKind.Run);
        var safeInvocation = AgentDotNetSolutionDoctorService.FormatPowerShellInvocation(hostileRun);
        Require(safeInvocation.Contains("'src/$([System.IO.File]::WriteAllText(''pwned'',''x'')) & O''Brien/O''Brien.csproj'", StringComparison.Ordinal), "PowerShell suggestions should single-quote hostile workspace paths and double embedded apostrophes");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(hostileSnapshot, safeInvocation) == hostileRun, "safely quoted PowerShell invocation should retain its typed identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(hostileSnapshot, $"dotnet run --project \"{hostilePath}\" --no-build") is null, "expandable double-quoted hostile paths should be rejected from structured identity");

        var conventionalSnapshot = CreateDotNetSnapshot(
            "Arena.UnitTests",
            "tests/Arena.UnitTests/Arena.UnitTests.csproj",
            testKind: DotNetProjectTestKind.Conventional);
        const string canonicalTest = "dotnet test tests/Arena.UnitTests/Arena.UnitTests.csproj --no-build";
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(conventionalSnapshot, canonicalTest) is { Kind: DotNetCommandKind.Test }, "the exact conventional test plan should retain typed identity");
        Require(AgentDotNetSolutionDoctorService.FindCommandPlan(conventionalSnapshot, $"{canonicalTest} --list-tests") is null, "a test listing command must not be reported as an executed test gate");
        var focused = AgentDotNetSolutionDoctorService.FindCommandPlan(
            conventionalSnapshot,
            $"{canonicalTest} --filter FullyQualifiedName=Arena.UnitTests.FocusedCase");
        Require(
            focused is { Kind: DotNetCommandKind.Test }
            && focused.Arguments.TakeLast(2).SequenceEqual(["--filter", "FullyQualifiedName=Arena.UnitTests.FocusedCase"]),
            "the one validated focused-test suffix generated by narrowed retry should retain typed identity");
        Require(
            AgentDotNetSolutionDoctorService.FindCommandPlan(
                conventionalSnapshot,
                $"{canonicalTest} --filter FullyQualifiedName=Arena.UnitTests.*") is null,
            "an unsafe or non-FQN filter must not become a typed focused retry");
    }

    private static void AgentSolutionDoctorDrivesStructuredRepairEvidence()
    {
        RunStaTest(() =>
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-dotnet-result", Guid.NewGuid().ToString("N"));
            var workspaceRoot = Path.Combine(testRoot, "workspace");
            var projectRelativePath = "src/App/App.csproj";
            var projectPath = Path.Combine(workspaceRoot, "src", "App", "App.csproj");
            var sourcePath = Path.Combine(workspaceRoot, "src", "App", "Broken.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(sourcePath, "class Broken { }");
            try
            {
                var snapshot = CreateDotNetSnapshot("App", projectRelativePath, solutionRelativePath: "App.sln");
                var settings = new WpfSettings();
                var coordinator = CreateWorkspaceProfileTestCoordinator(
                    settings,
                    new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                    (_, _) => Task.FromResult("Project signals: .NET"),
                    (_, _) => Task.FromResult(snapshot));
                coordinator.Initialize();
                coordinator.ControlSetWorkspace(workspaceRoot);
                coordinator.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();

                var command = snapshot.CommandPlans.Single(plan =>
                    plan.Kind == DotNetCommandKind.Build
                    && plan.TargetKind == DotNetCommandTargetKind.Project).DisplayInvocation;
                var stdout = $"""
                    {sourcePath}(4,2): error CS1002: ; expected [{projectPath}]
                        0 Warning(s)
                        1 Error(s)
                    """;
                var result = new AgentCommandResult(
                    false,
                    "Terminal",
                    command,
                    workspaceRoot,
                    1,
                    stdout,
                    "",
                    TimeSpan.FromMilliseconds(40),
                    false,
                    false,
                    "");
                var receipt = AgentWorkspaceCoordinator.BuildFileReceipt(
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase));

                Require(coordinator.DebugApplyCompletedCommandForTest(result, receipt), "current-workspace command result should be accepted");
                Require(coordinator.DebugBuildEvidenceSummary.Contains(".NET Build", StringComparison.Ordinal), "Build Evidence summary should prioritize the structured .NET result");
                Require(coordinator.DebugDotNetResultPacket.Contains("CS1002", StringComparison.Ordinal), "structured packet should contain the compiler diagnostic");
                Require(coordinator.DebugDotNetResultPacket.Contains("src/App/Broken.cs", StringComparison.Ordinal), "structured packet should contain only the relative source location");
                Require(!coordinator.DebugDotNetResultPacket.Contains(workspaceRoot, StringComparison.OrdinalIgnoreCase), "structured packet should not expose the absolute workspace");
                Require(coordinator.DebugStageNextLabel == "Stage Repair", "a compiler failure should expose Stage Repair");
                Require(coordinator.DebugBuildEvidenceCount >= 4, "dynamic Build Evidence should include .NET workspace and diagnostic rows");

                coordinator.DebugStageNextPromptFromResult();
                Require(coordinator.DebugPromptText.Contains("dotnet build src/App/App.csproj --no-restore", StringComparison.Ordinal), "repair prompt should carry the narrowed project-correct command");
                Require(coordinator.DebugPromptText.Contains("C# Diagnostics", StringComparison.OrdinalIgnoreCase)
                    || coordinator.DebugPromptText.Contains("CS1002", StringComparison.Ordinal), "repair prompt should carry bounded structured failure evidence");

                var cancelled = result with
                {
                    ExitCode = -1,
                    StandardOutput = "cancelled raw output",
                    Canceled = true,
                    Error = "Command cancelled."
                };
                Require(coordinator.DebugApplyCompletedCommandForTest(cancelled, receipt), "cancelled current-workspace result should still be captured");
                Require(coordinator.DebugStageNextLabel == "Stage Retry", "cancelled typed commands should consistently expose Stage Retry");
                Require(coordinator.DebugCommandSource.Contains("Stage Retry", StringComparison.Ordinal), "cancelled typed command provenance should not contradict the retry action");
                Require(coordinator.DebugBuildEvidenceStates.Contains("C# Diagnostics: Cancelled", StringComparison.Ordinal), "Build Evidence should label typed cancellation without inventing a compiler failure");
                Require(!coordinator.DebugBuildEvidenceStates.Contains("First Failure", StringComparison.Ordinal), "typed cancellation should not add a fake first-failure row");

                var structuralReceipt = AgentWorkspaceCoordinator.BuildFileReceipt(
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase)
                    {
                        [projectRelativePath] = new(10, new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc))
                    },
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase)
                    {
                        [projectRelativePath] = new(20, new DateTime(2026, 7, 29, 10, 1, 0, DateTimeKind.Utc))
                    });
                Require(coordinator.DebugApplyCompletedCommandForTest(result, structuralReceipt), "same-workspace structural command result should retain its raw receipt");
                Require(coordinator.DebugDotNetResultPacket.Contains("No structured", StringComparison.Ordinal), "project-structure changes should clear typed retry provenance before rescanning");
                coordinator.Dispose();
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    private static void AgentSolutionDoctorRefreshIgnoresStaleTypedResults()
    {
        RunStaTest(() =>
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-dotnet-refresh", Guid.NewGuid().ToString("N"));
            var firstRoot = Path.Combine(testRoot, "first");
            var secondRoot = Path.Combine(testRoot, "second");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            try
            {
                var firstCompletion = new TaskCompletionSource<DotNetWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondCompletion = new TaskCompletionSource<DotNetWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = new List<(string Path, CancellationToken Token)>();
                Task<DotNetWorkspaceSnapshot> DiscoverAsync(string path, CancellationToken cancellationToken)
                {
                    calls.Add((path, cancellationToken));
                    return path.Equals(firstRoot, StringComparison.OrdinalIgnoreCase)
                        ? firstCompletion.Task
                        : secondCompletion.Task;
                }

                var coordinator = CreateWorkspaceProfileTestCoordinator(
                    new WpfSettings(),
                    new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                    (_, _) => Task.FromResult("Project signals: .NET"),
                    DiscoverAsync);
                coordinator.Initialize();
                coordinator.ControlSetWorkspace(firstRoot);
                var firstWorkspaceGeneration = coordinator.DebugWorkspaceGeneration;
                var firstRefresh = coordinator.DebugWorkspaceProfileRefreshTask;
                Require(calls.Count == 1, "first workspace should start typed discovery");

                coordinator.ControlSetWorkspace(secondRoot);
                var secondRefresh = coordinator.DebugWorkspaceProfileRefreshTask;
                Require(calls.Count == 2, "workspace switch should start replacement typed discovery");
                Require(calls[0].Token.IsCancellationRequested, "workspace switch should cancel the stale typed discovery");

                secondCompletion.SetResult(CreateDotNetSnapshot("Current.Project", "src/Current.Project/Current.Project.csproj"));
                secondRefresh.GetAwaiter().GetResult();
                Require(coordinator.DebugWorkspaceProfile.Contains("Current.Project", StringComparison.Ordinal), "current typed snapshot should enrich the accepted profile");

                var staleResult = new AgentCommandResult(
                    false,
                    "Terminal",
                    "dotnet build Arena.sln --no-restore",
                    firstRoot,
                    1,
                    "stale old-workspace command evidence",
                    "",
                    TimeSpan.FromMilliseconds(10),
                    false,
                    false,
                    "");
                var emptyReceipt = AgentWorkspaceCoordinator.BuildFileReceipt(
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase));
                Require(
                    !coordinator.DebugApplyCompletedCommandForTest(staleResult, emptyReceipt, firstWorkspaceGeneration),
                    "a completed command from the prior workspace generation must be discarded");
                Require(!coordinator.DebugStageNextEnabled, "discarded command results must not enable current-workspace repair or verification actions");

                firstCompletion.SetResult(CreateDotNetSnapshot("Stale.Project", "src/Stale.Project/Stale.Project.csproj"));
                firstRefresh.GetAwaiter().GetResult();
                Require(!coordinator.DebugWorkspaceProfile.Contains("Stale.Project", StringComparison.Ordinal), "stale typed snapshot must not overwrite the current workspace");
                coordinator.Dispose();
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    private static void AgentSolutionDoctorRefreshesAfterRestore()
    {
        RunStaTest(() =>
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-dotnet-restore", Guid.NewGuid().ToString("N"));
            var workspaceRoot = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            try
            {
                var missingAssets = CreateDotNetSnapshot(
                    "Restore.Project",
                    "src/Restore.Project/Restore.Project.csproj",
                    restoreState: DotNetRestoreState.AssetsMissing);
                var restored = CreateDotNetSnapshot(
                    "Restore.Project",
                    "src/Restore.Project/Restore.Project.csproj",
                    restoreState: DotNetRestoreState.AssetsAvailable);
                var discoveryCalls = 0;
                Task<DotNetWorkspaceSnapshot> DiscoverAsync(string _, CancellationToken __)
                {
                    discoveryCalls++;
                    return Task.FromResult(discoveryCalls == 1 ? missingAssets : restored);
                }

                var runbook = new AgentRunbookService();
                var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
                runbook.Begin(workspaceRoot, "Restore dependencies, then verify the product.", builderOnly: false, now);
                runbook.MarkExecutionStarted("Verification discovered missing restore assets.", now.AddMinutes(1));
                runbook.MarkExecutionFinished(ok: true, canceled: false, "Verification must continue after restore.", now.AddMinutes(2));
                var settings = new WpfSettings
                {
                    AgentWorkspacePath = workspaceRoot,
                    AgentRunbook = runbook.State
                };
                var coordinator = CreateWorkspaceProfileTestCoordinator(
                    settings,
                    new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                    (_, _) => Task.FromResult("Project signals: .NET"),
                    DiscoverAsync);
                coordinator.Initialize();
                coordinator.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();
                Require(coordinator.DebugWorkspaceProfile.Contains("DNW114", StringComparison.Ordinal), "initial typed profile should expose the known missing-assets state");
                Require(coordinator.DebugRunbookStatus == "Needs verification", "fixture should restore an active runbook with verification pending");

                coordinator.DebugSetCommandRequiredForTest(true);
                var restorePlan = missingAssets.CommandPlans.Single(plan =>
                    plan.Kind == DotNetCommandKind.Restore
                    && plan.TargetKind == DotNetCommandTargetKind.Solution);
                var restoreResult = new AgentCommandResult(
                    true,
                    "Terminal",
                    restorePlan.DisplayInvocation,
                    workspaceRoot,
                    0,
                    "Restore completed.",
                    "",
                    TimeSpan.FromMilliseconds(25),
                    false,
                    false,
                    "");
                var emptyReceipt = AgentWorkspaceCoordinator.BuildFileReceipt(
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase));

                Require(coordinator.DebugApplyCompletedCommandForTest(restoreResult, emptyReceipt), "approved typed restore result should be accepted");
                var postRestoreRefresh = coordinator.DebugWorkspaceProfileRefreshTask;
                postRestoreRefresh.GetAwaiter().GetResult();

                Require(discoveryCalls == 2, "a typed restore should refresh discovery even though obj is excluded from file receipts");
                Require(!coordinator.DebugWorkspaceProfile.Contains("DNW114", StringComparison.Ordinal), "post-restore discovery should replace the stale missing-assets profile");
                Require(coordinator.DebugDotNetResultPacket.Contains("Action: Restore", StringComparison.Ordinal), "the raw/structured restore result should remain available after refresh");
                Require(coordinator.DebugStageNextLabel == "Stage Next", "a successful restore with no tracked source changes must not expose Stage Repair");
                Require(!coordinator.DebugWorkSummary.Contains("repair", StringComparison.OrdinalIgnoreCase), "successful restore should treat its empty source receipt as expected");
                Require(coordinator.DebugRunbookStatus == "Needs verification", "restore must not complete an active runbook before build/test verification");

                var buildPlan = restored.CommandPlans.Single(plan =>
                    plan.Kind == DotNetCommandKind.Build
                    && plan.TargetKind == DotNetCommandTargetKind.Solution);
                var buildResult = restoreResult with
                {
                    Command = buildPlan.DisplayInvocation,
                    StandardOutput = "Build succeeded."
                };
                Require(coordinator.DebugApplyCompletedCommandForTest(buildResult, emptyReceipt), "post-restore typed build result should be accepted");
                Require(coordinator.DebugRunbookStatus == "Completed", "the retained verification state should complete only after a non-restore typed gate passes");
                coordinator.Dispose();
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    private static void AgentSolutionDoctorSurvivesLegacyProfileFailure()
    {
        RunStaTest(() =>
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "ai-arena-agent-dotnet-profile-fallback", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            try
            {
                var snapshot = CreateDotNetSnapshot("Fallback.Project", "src/Fallback.Project/Fallback.Project.csproj");
                var coordinator = CreateWorkspaceProfileTestCoordinator(
                    new WpfSettings(),
                    new WpfSettingsStore(Path.Combine(testRoot, "settings.json")),
                    (_, _) => Task.FromException<string>(new InvalidDataException("legacy scanner failed")),
                    (_, _) => Task.FromResult(snapshot));
                coordinator.Initialize();
                coordinator.ControlSetWorkspace(testRoot);
                coordinator.DebugWorkspaceProfileRefreshTask.GetAwaiter().GetResult();

                Require(coordinator.DebugWorkspaceProfile.Contains("Filesystem workspace profile unavailable", StringComparison.Ordinal), "legacy profile failure should be reported without suppressing typed discovery");
                Require(coordinator.DebugWorkspaceProfile.Contains("Fallback.Project", StringComparison.Ordinal), "valid typed discovery should survive an unrelated legacy profile failure");
                coordinator.Dispose();
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    private static DotNetWorkspaceSnapshot CreateDotNetSnapshot(
        string projectName,
        string projectRelativePath,
        string solutionRelativePath = "Arena.sln",
        DotNetProjectTestKind testKind = DotNetProjectTestKind.ExecutableHarness,
        DotNetRestoreState restoreState = DotNetRestoreState.Unknown)
    {
        DotNetWorkspaceDiagnostic[] restoreDiagnostics =
            restoreState == DotNetRestoreState.AssetsMissing
                ?
                [
                    new(
                        "DNW114",
                        DotNetWorkspaceDiagnosticSeverity.Warning,
                        "Restore assets are missing; use the separately approved Restore action.",
                        projectRelativePath)
                ]
                : [];
        var project = new DotNetProjectInfo(
            $"project:{projectName}",
            projectName,
            projectRelativePath,
            ["net10.0"],
            [],
            DotNetProjectOutputType.Exe,
            UseWpf: false,
            TestKind: testKind,
            IsPartial: false,
            Diagnostics: restoreDiagnostics,
            RestoreState: restoreState);
        var solution = new DotNetSolutionInfo(
            Path.GetFileNameWithoutExtension(solutionRelativePath),
            solutionRelativePath,
            [projectRelativePath],
            IsPartial: false);
        var buildSolution = DotNetPlan(
            "build:solution",
            DotNetCommandKind.Build,
            DotNetCommandTargetKind.Solution,
            solutionRelativePath,
            ["build", solutionRelativePath, "--no-restore"],
            $"dotnet build {solutionRelativePath} --no-restore");
        var restoreSolution = DotNetPlan(
            "restore:solution",
            DotNetCommandKind.Restore,
            DotNetCommandTargetKind.Solution,
            solutionRelativePath,
            ["restore", solutionRelativePath],
            $"dotnet restore {solutionRelativePath}",
            separateApproval: true,
            DotNetNetworkRisk.MayAccessConfiguredPackageSources);
        var buildProject = DotNetPlan(
            "build:project",
            DotNetCommandKind.Build,
            DotNetCommandTargetKind.Project,
            projectRelativePath,
            ["build", projectRelativePath, "--no-restore"],
            $"dotnet build {projectRelativePath} --no-restore");
        var testAction = testKind == DotNetProjectTestKind.Conventional
            ? DotNetPlan(
                "test:project",
                DotNetCommandKind.Test,
                DotNetCommandTargetKind.Project,
                projectRelativePath,
                ["test", projectRelativePath, "--no-build"],
                $"dotnet test {projectRelativePath} --no-build")
            : DotNetPlan(
                "run:harness",
                DotNetCommandKind.Run,
                DotNetCommandTargetKind.Project,
                projectRelativePath,
                ["run", "--project", projectRelativePath, "--no-build"],
                $"dotnet run --project {projectRelativePath} --no-build");
        return new(
            "workspace",
            [solution],
            [project],
            [buildSolution, restoreSolution, buildProject, testAction],
            restoreDiagnostics,
            IsPartial: false,
            ScanLimitReached: false);
    }

    private static DotNetCommandPlan DotNetPlan(
        string id,
        DotNetCommandKind kind,
        DotNetCommandTargetKind targetKind,
        string target,
        IReadOnlyList<string> arguments,
        string display,
        bool separateApproval = false,
        DotNetNetworkRisk networkRisk = DotNetNetworkRisk.None)
    {
        return new(
            id,
            kind,
            targetKind,
            target,
            "dotnet",
            arguments,
            ".",
            RequiresUserApproval: true,
            RequiresSeparateApproval: separateApproval,
            networkRisk,
            display,
            $"{kind} {target}");
    }
}
