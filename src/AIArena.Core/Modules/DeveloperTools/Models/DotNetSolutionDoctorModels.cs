namespace AIArena.Core.Models;

public enum DotNetWorkspaceDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum DotNetProjectOutputType
{
    Unknown,
    Library,
    Exe,
    WinExe
}

public enum DotNetProjectTestKind
{
    None,
    Conventional,
    ExecutableHarness
}

public enum DotNetRestoreState
{
    Unknown,
    AssetsAvailable,
    AssetsMissing
}

public enum DotNetCommandKind
{
    Restore,
    Build,
    Test,
    Run
}

public enum DotNetCommandTargetKind
{
    Solution,
    Project
}

public enum DotNetCommandShell
{
    PowerShell
}

public enum DotNetNetworkRisk
{
    None,
    MayAccessConfiguredPackageSources
}

public sealed record DotNetDiscoveryOptions(
    int MaxDirectories = 2_000,
    int MaxFiles = 25_000,
    int MaxProjects = 256,
    int MaxSolutions = 32,
    int MaxDepth = 12,
    long MaxProjectFileBytes = 2 * 1024 * 1024,
    int MaxDiagnostics = 256,
    int MaxCommandPlans = 512);

public sealed record DotNetWorkspaceDiagnostic(
    string Code,
    DotNetWorkspaceDiagnosticSeverity Severity,
    string Message,
    string? RelativePath = null);

public sealed record DotNetSolutionInfo(
    string Name,
    string RelativePath,
    IReadOnlyList<string> ProjectRelativePaths,
    bool IsPartial);

public sealed record DotNetProjectInfo(
    string Id,
    string Name,
    string RelativePath,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> ProjectReferenceRelativePaths,
    DotNetProjectOutputType OutputType,
    bool UseWpf,
    DotNetProjectTestKind TestKind,
    bool IsPartial,
    IReadOnlyList<DotNetWorkspaceDiagnostic> Diagnostics,
    DotNetRestoreState RestoreState = DotNetRestoreState.Unknown)
{
    public bool IsExecutable => OutputType is DotNetProjectOutputType.Exe or DotNetProjectOutputType.WinExe;
    public bool IsConventionalTestProject => TestKind == DotNetProjectTestKind.Conventional;
    public bool IsExecutableTestHarness => TestKind == DotNetProjectTestKind.ExecutableHarness;
}

public sealed record DotNetCommandPlan(
    string Id,
    DotNetCommandKind Kind,
    DotNetCommandTargetKind TargetKind,
    string TargetRelativePath,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectoryRelativePath,
    bool RequiresUserApproval,
    bool RequiresSeparateApproval,
    DotNetNetworkRisk NetworkRisk,
    string DisplayInvocation,
    string Description,
    DotNetCommandShell Shell = DotNetCommandShell.PowerShell);

public sealed record DotNetWorkspaceSnapshot(
    string WorkspaceName,
    IReadOnlyList<DotNetSolutionInfo> Solutions,
    IReadOnlyList<DotNetProjectInfo> Projects,
    IReadOnlyList<DotNetCommandPlan> CommandPlans,
    IReadOnlyList<DotNetWorkspaceDiagnostic> Diagnostics,
    bool IsPartial,
    bool ScanLimitReached);

public enum DotNetBuildDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record DotNetBuildDiagnostic(
    string Code,
    DotNetBuildDiagnosticSeverity Severity,
    string Message,
    string? RelativePath,
    int? Line,
    int? Column,
    string? ProjectRelativePath);

public sealed record DotNetTestTotals(
    int Passed,
    int Failed,
    int Skipped,
    int Total);

public sealed record DotNetFailingTest(
    string Name,
    string? ProjectRelativePath,
    string? Detail);

/// <summary>
/// Preserves the command runner's unmodified output. ReferenceId lets UI and
/// persistence layers correlate the structured result with their own bounded log.
/// </summary>
public sealed record DotNetRawOutput(
    string ReferenceId,
    string StandardOutput,
    string StandardError);

public sealed record DotNetCommandResult(
    DotNetCommandPlan Command,
    int ExitCode,
    bool WasCancelled,
    bool Succeeded,
    IReadOnlyList<DotNetBuildDiagnostic> Diagnostics,
    int WarningCount,
    int ErrorCount,
    DotNetTestTotals? TestTotals,
    IReadOnlyList<DotNetFailingTest> FailingTests,
    DotNetRawOutput RawOutput,
    bool StructuredEvidenceLimitReached);

public sealed record DotNetNarrowedRetryPlan(
    DotNetCommandPlan Command,
    string Reason,
    bool IsNarrowed);
