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

public enum DotNetFindingCategory
{
    ProjectReferenceCycle,
    TargetFrameworkIncompatibility,
    PackageVersionConflict,
    PackageDowngrade,
    TestDiscoveryFailure
}

public enum DotNetFindingConfidence
{
    Medium,
    High
}

public enum DotNetFindingEvidenceKind
{
    Project,
    ProjectReference,
    TargetFramework,
    PackageReference,
    Command,
    Diagnostic,
    TestRunner
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
    int MaxCommandPlans = 512)
{
    /// <summary>
    /// When non-null, discovery is bounded to these workspace-relative .csproj
    /// paths and does not traverse the workspace for other projects or solutions.
    /// </summary>
    public IReadOnlyList<string>? AllowedProjectRelativePaths { get; init; }
}

public sealed record DotNetWorkspaceDiagnostic(
    string Code,
    DotNetWorkspaceDiagnosticSeverity Severity,
    string Message,
    string? RelativePath = null);

public sealed record DotNetPackageReferenceInfo(
    string Name,
    string Version);

public sealed record DotNetFindingEvidenceStep(
    int Sequence,
    DotNetFindingEvidenceKind Kind,
    string Label,
    string? RelativePath = null,
    string? RelatedRelativePath = null,
    string? Value = null);

public sealed record DotNetDoctorFinding(
    string Id,
    string Code,
    DotNetWorkspaceDiagnosticSeverity Severity,
    DotNetFindingCategory Category,
    DotNetFindingConfidence Confidence,
    string Title,
    string Summary,
    string? PrimaryRelativePath,
    IReadOnlyList<string> RelatedProjectRelativePaths,
    IReadOnlyList<DotNetFindingEvidenceStep> RootCauseChain);

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
    public IReadOnlyList<DotNetPackageReferenceInfo> PackageReferences { get; init; } = [];
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
    bool ScanLimitReached)
{
    public IReadOnlyList<DotNetDoctorFinding> Findings { get; init; } = [];
    public bool FindingsLimitReached { get; init; }
}

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
    bool StructuredEvidenceLimitReached)
{
    public IReadOnlyList<DotNetDoctorFinding> Findings { get; init; } = [];
}

public sealed record DotNetNarrowedRetryPlan(
    DotNetCommandPlan Command,
    string Reason,
    bool IsNarrowed);

public enum DotNetRepairAvailability
{
    Ready,
    Partial,
    Unavailable
}

public enum DotNetRepairDiffReadiness
{
    RequiresInspection,
    Unavailable
}

public enum DotNetRepairTestEvidenceState
{
    Available,
    Partial,
    Unavailable
}

public enum DotNetRepairFindingTransitionState
{
    Fixed,
    Unchanged,
    Regressed,
    New,
    Unknown
}

public enum DotNetRepairLoopOutcome
{
    Fixed,
    Unchanged,
    Regressed,
    Partial,
    Cancelled
}

/// <summary>
/// Optional product-owned impact evidence. Roslyn or another local index may
/// supply these paths without coupling Solution Doctor to that implementation.
/// </summary>
public sealed record DotNetRepairImpactHint(
    IReadOnlyList<string> AffectedProjectRelativePaths,
    IReadOnlyList<string> LikelyTestProjectRelativePaths,
    bool IsPartial,
    string Source);

/// <summary>
/// A bounded, source-free description of an intended change. It is never an
/// executable patch: the exact edit still has to be inspected and staged in the
/// normal command preview rail.
/// </summary>
public sealed record DotNetRepairDiffHunk(
    string RelativePath,
    string Location,
    string Before,
    string After,
    DotNetRepairDiffReadiness Readiness,
    string Rationale);

public sealed record DotNetRepairVerificationPlan(
    IReadOnlyList<DotNetCommandPlan> BuildPlans,
    IReadOnlyList<DotNetCommandPlan> TestPlans,
    DotNetRepairTestEvidenceState TestEvidenceState,
    string TestSelectionBasis,
    bool IsPartial);

public sealed record DotNetRepairProposal(
    string Id,
    string SourceId,
    string Code,
    string BaselineFingerprint,
    string Explanation,
    IReadOnlyList<string> AffectedRelativePaths,
    IReadOnlyList<DotNetRepairDiffHunk> DiffPreview,
    DotNetRepairVerificationPlan Verification,
    DotNetRepairAvailability Availability,
    bool RequiresExplicitApproval,
    string? Limitation);

public sealed record DotNetRepairFindingEvidence(
    string Id,
    string Code,
    DotNetWorkspaceDiagnosticSeverity Severity,
    IReadOnlyList<string> RelativePaths);

public sealed record DotNetRepairEvidenceSnapshot(
    string Fingerprint,
    IReadOnlyList<DotNetRepairFindingEvidence> Findings,
    bool IsPartial);

public sealed record DotNetRepairFindingTransition(
    string Id,
    string Code,
    DotNetRepairFindingTransitionState State,
    IReadOnlyList<string> RelativePaths);

public sealed record DotNetRepairComparison(
    DotNetRepairLoopOutcome Outcome,
    IReadOnlyList<DotNetRepairFindingTransition> FindingTransitions,
    bool IsPartial,
    string Summary);

public enum DotNetSolutionDoctorTestOutcome
{
    Passed,
    Failed,
    Skipped,
    Unavailable
}

public sealed record DotNetSolutionDoctorTestObservation(
    string? TestId,
    string TestName,
    string ProjectRelativePath,
    DotNetSolutionDoctorTestOutcome Outcome);

public sealed record DotNetSolutionDoctorTestEvidence(
    bool Available,
    bool Complete,
    int Passed,
    int Failed,
    IReadOnlyList<string> Flaky,
    IReadOnlyList<string> Failing,
    IReadOnlyList<DotNetSolutionDoctorTestObservation> Observations);

public sealed record DotNetSolutionDoctorFindingTransition(
    string FindingId,
    string Code,
    DotNetRepairFindingTransitionState State,
    string? PrimaryRelativePath,
    IReadOnlyList<string> RelatedRelativePaths);

public sealed record DotNetSolutionDoctorHistoryRun(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? BaselineRevision,
    string? TargetRevision,
    DotNetRepairLoopOutcome Outcome,
    IReadOnlyList<string> AffectedRelativePaths,
    IReadOnlyList<DotNetSolutionDoctorFindingTransition> Transitions,
    DotNetSolutionDoctorTestEvidence TestEvidence);

public sealed record DotNetSolutionDoctorHistory(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DotNetSolutionDoctorHistoryRun> Runs);

public sealed record DotNetSolutionDoctorHistoryWriteResult(
    bool Succeeded,
    string RelativePath,
    string Message);
