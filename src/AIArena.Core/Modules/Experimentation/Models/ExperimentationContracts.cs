using System.Collections.Immutable;

namespace AIArena.Core.Models;

/// <summary>
/// Stable identifiers for local experimentation and verification artifacts.
/// Existing v1 meaning is immutable; incompatible changes require a new schema.
/// </summary>
public static class ArenaContractSchemas
{
    public const string Experiment = "ai_arena.experiment.v1";
    public const string ExperimentRun = "ai_arena.experiment_run.v1";
    public const string Branch = "ai_arena.branch.v1";
    public const string Rubric = "ai_arena.rubric.v1";
    public const string ClaimLedger = "ai_arena.claim_ledger.v1";
    public const string MemoryTrace = "ai_arena.memory_trace.v1";
    public const string ScenarioPack = "ai_arena.scenario_pack.v1";
    public const string BenchmarkPack = "ai_arena.benchmark_pack.v1";
    public const string RouteProposal = "ai_arena.route_proposal.v1";
    public const string RouteApplicationReceipt = "ai_arena.route_application_receipt.v1";
    public const string FaultProfile = "ai_arena.fault_profile.v1";
    public const string QaEvidence = "ai_arena.qa_evidence.v1";

    public static ImmutableArray<string> All { get; } =
    [
        Experiment,
        ExperimentRun,
        Branch,
        Rubric,
        ClaimLedger,
        MemoryTrace,
        ScenarioPack,
        BenchmarkPack,
        RouteProposal,
        RouteApplicationReceipt,
        FaultProfile,
        QaEvidence
    ];
}

/// <summary>
/// Authoritative v1 requirements for a release-ready QA seal. Gate IDs are
/// deliberately versioned and closed: changing the required evidence creates a
/// new manifest rather than silently weakening an existing seal.
/// </summary>
public static class ArenaQaSealManifestV1
{
    public sealed record LimitationRequirement(
        string Id,
        string Summary,
        string EvidenceId,
        string EvidenceSummary,
        string ReferenceId,
        string EvidenceLimitation);

    public const string Id = "ai_arena.qa_seal_manifest.v1";
    public const int RequiredCleanPasses = 2;

    public static ImmutableArray<string> RequiredGlobalGateIds { get; } =
    [
        "inspection.user-acceptance",
        "postflight.evidence-privacy",
        "postflight.map-source-stability",
        "postflight.source-stability",
        "preflight.artifact-root-ignored",
        "preflight.map-source-clean",
        "preflight.seal-configuration",
        "preflight.source-clean",
        "preflight.toolchain",
        "ui.keyboard-automation-matrix",
        "ui.reduced-motion-matrix",
        "ui.theme-contrast-matrix",
        "ui.viewport-dpi-matrix",
        "verification.restart-soak-resource"
    ];

    public static ImmutableArray<string> RequiredPerPassGateSuffixes { get; } =
    [
        "clean-build",
        "dependency-index",
        "local-runtime-qa",
        "map-full-suite",
        "release-security-tests",
        "rendered-ui",
        "tests-code-intelligence",
        "tests-core",
        "tests-verification-lab",
        "tests-wpf",
        "xaml-inventory-check",
        "xaml-inventory-tests"
    ];

    public static ImmutableArray<string> RequiredSchemaIds => ArenaContractSchemas.All;

    public static ImmutableArray<string> RequiredNestedRepositoryIds { get; } =
    [
        "map"
    ];

    public static ImmutableArray<string> RequiredPerformanceMetrics { get; } =
    [
        "cancellation-latency",
        "clean-release-build-duration",
        "fault-recovery-latency",
        "handle-growth",
        "peak-working-set",
        "release-app-startup-duration",
        "soak-duration"
    ];

    public static ImmutableArray<string> RequiredArtifactKinds { get; } =
    [
        "automation-tree",
        "rendered-ui-screenshot"
    ];

    public static ImmutableArray<LimitationRequirement> RequiredLimitations { get; } =
    [
        new(
            "limitation.source-boundary-sampling",
            "Source fingerprints were checked at bounded QA boundaries, but the run was not executed from an immutable worktree; a transient edit-and-restore between checks is not cryptographically excluded.",
            "evidence.limitation.source-boundary-sampling",
            "Immutable-worktree execution evidence is unavailable in this local seal.",
            "artifact.postflight.source-stability.log",
            "Fingerprints sample bounded QA boundaries; they cannot prove no transient edit-and-restore occurred between samples."),
        new(
            "limitation.ui-animation-playback",
            "Rendered animation playback over time was not observed; evidence is limited to process-only normal/reduced motion-preference plumbing and captured state.",
            "evidence.limitation.ui-animation-playback",
            "Animation playback evidence over time is unavailable in this local seal.",
            "artifact.ui.reduced-motion-matrix.log",
            "The matrix proves motion-preference plumbing and state, not temporal animation behaviour."),
        new(
            "limitation.ui-os-interaction",
            "Interactive OS keyboard input and external UI Automation were not exercised; evidence is limited to programmatic in-process WPF focus traversal and a privacy-safe visual-tree snapshot.",
            "evidence.limitation.ui-os-interaction",
            "OS SendInput and external UI Automation evidence are unavailable in this local seal.",
            "artifact.ui.keyboard-automation-matrix.log",
            "Only programmatic in-process WPF focus traversal and privacy-safe visual-tree metadata were captured."),
        new(
            "limitation.ui-physical-dpi",
            "Physical and per-monitor display DPI were not exercised; the 1.0, 1.5, and 2.0 values are off-screen screenshot raster-density scales at fixed DIP viewports.",
            "evidence.limitation.ui-physical-dpi",
            "Physical and per-monitor display DPI evidence is unavailable in this local seal.",
            "artifact.ui.viewport-dpi-matrix.log",
            "The matrix proves fixed-DIP rendering at three off-screen raster densities only.")
    ];

    public static ImmutableArray<string> RequiredGateIds(int passCount)
    {
        if (passCount < RequiredCleanPasses)
        {
            throw new ArgumentOutOfRangeException(nameof(passCount));
        }

        return
        [
            .. RequiredGlobalGateIds,
            .. Enumerable.Range(1, passCount).SelectMany(pass =>
                RequiredPerPassGateSuffixes.Select(suffix => $"pass-{pass:D2}.{suffix}"))
        ];
    }
}

public enum ArenaEvidenceState
{
    Observed,
    Inferred,
    Unavailable
}

public enum ArenaExperimentStatus
{
    Draft,
    Running,
    Completed,
    Cancelled
}

public enum ArenaExperimentRunState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum ArenaRubricEvaluatorKind
{
    Deterministic,
    Human,
    ModelJudge,
    BlindPairwise
}

public enum ArenaClaimStatus
{
    Asserted,
    Supported,
    Contradicted,
    Resolved,
    Withdrawn,
    Unavailable
}

public enum ArenaMemoryVisibility
{
    Private,
    Shared,
    System
}

public enum ArenaMemoryOrigin
{
    Manual,
    Turn,
    Import,
    LegacyUnknown,
    System,
    Derived
}

/// <summary>
/// The optimizer can only emit proposals. Approval and application are separate,
/// explicitly authorized product actions and are not represented as optimizer states.
/// </summary>
public enum ArenaRouteProposalStatus
{
    Proposed,
    InsufficientEvidence,
    Dismissed
}

public enum ArenaEvidenceSufficiency
{
    Insufficient,
    Partial,
    Sufficient
}

public enum ArenaRouteConstraintKind
{
    Hardware,
    Capability,
    Policy
}

public enum ArenaFaultTarget
{
    Provider,
    Network,
    Persistence,
    ControlPlane,
    UserInterface
}

public enum ArenaFaultKind
{
    Timeout,
    Disconnect,
    MalformedStream,
    Saturation,
    EmptyResponse,
    Interruption,
    ContextPressure
}

public enum ArenaQaGateOutcome
{
    Pass,
    Fail,
    Partial,
    Blocked,
    Unavailable
}

public enum ArenaQaVerdict
{
    Sealed,
    Partial,
    Blocked
}

public enum ArenaQaThresholdKind
{
    Maximum,
    Minimum
}

public interface IArenaVersionedContract
{
    string Schema { get; }

    string Id { get; }

    DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>
/// Content-free evidence reference. Summary describes a result; ReferenceId
/// points to another bounded artifact; Basis explains inference; Limitation
/// explains unavailable evidence.
/// </summary>
public sealed record ArenaEvidenceAssertion(
    string Id,
    ArenaEvidenceState State,
    string Summary,
    string? ReferenceId = null,
    string? Basis = null,
    string? Limitation = null);

public sealed record ArenaExperimentDimension(
    string Id,
    string Parameter,
    ImmutableArray<string> Values);

public sealed record ArenaExperimentContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Title,
    ArenaExperimentStatus Status,
    string ScenarioPackId,
    string? BenchmarkPackId,
    ImmutableArray<string> ProviderProfileIds,
    ImmutableArray<string> RubricIds,
    ImmutableArray<string> FaultProfileIds,
    ImmutableArray<ArenaExperimentDimension> Dimensions,
    int Repetitions,
    int TurnBudget,
    int MaxParallelism,
    ImmutableArray<string> BranchIds,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaExperimentRunContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ExperimentId,
    string ExperimentFingerprint,
    string VariantFingerprint,
    int Repetition,
    string CellKey,
    ArenaExperimentRunState State,
    int Attempts,
    DateTimeOffset UpdatedAtUtc,
    ImmutableArray<string> TrialIds,
    string? InterruptionReason,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaTranscriptForkPoint(
    string MessageId,
    int MessageIndex,
    int Turn,
    string MessageFingerprint);

/// <summary>
/// Conversation fork identity only. Transcript text and memory values are never
/// embedded; the referenced session stores remain the authority for that data.
/// </summary>
public sealed record ArenaBranchContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string? ExperimentId,
    string ParentSessionId,
    long ParentRevision,
    ArenaTranscriptForkPoint ForkPoint,
    string SetupFingerprint,
    long MemoryRevision,
    string ChildSessionId,
    DateTimeOffset ForkedAtUtc,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaRubricCriterion(
    string Id,
    string Label,
    string Description,
    decimal Weight,
    decimal MinimumScore,
    decimal MaximumScore);

public sealed record ArenaRubricEvaluator(
    string Id,
    ArenaRubricEvaluatorKind Kind,
    string? ProfileId);

public sealed record ArenaRubricContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Name,
    string Version,
    ImmutableArray<ArenaRubricEvaluator> Evaluators,
    ImmutableArray<ArenaRubricCriterion> Criteria,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaClaim(
    string Id,
    string MessageId,
    string ClaimantId,
    string ClaimSummary,
    ArenaClaimStatus Status,
    decimal AssertedConfidence,
    ImmutableArray<string> SourceEvidenceIds,
    ImmutableArray<string> ContradictionClaimIds,
    ImmutableArray<string> ReviewerIds,
    ArenaEvidenceAssertion Provenance);

public sealed record ArenaClaimLedgerContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ExperimentId,
    string BranchId,
    ImmutableArray<ArenaClaim> Claims,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaMemoryEntry(
    string Id,
    string AgentId,
    string MemoryKey,
    string ValueSummary,
    ArenaMemoryVisibility Visibility,
    ArenaMemoryOrigin Origin,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RevisedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string SourceMessageId,
    string BranchId,
    string? SupersedesMemoryId,
    string? CorrectionMemoryId,
    ArenaEvidenceAssertion Provenance);

public sealed record ArenaMemoryTraceContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ExperimentId,
    string BranchId,
    ImmutableArray<ArenaMemoryEntry> Entries) : IArenaVersionedContract;

public sealed record ArenaScenarioDefinition(
    string Id,
    string Version,
    string Title,
    string MatchSetupReference,
    string SetupFingerprint,
    string ScenarioSeed,
    int TurnBudget,
    ImmutableArray<string> Tags,
    ImmutableArray<string> InvariantIds,
    ImmutableArray<string> RequiredEvidenceIds);

public sealed record ArenaScenarioInvariant(
    string Id,
    string RuleId,
    string ExpectedOutcome,
    bool Required);

public sealed record ArenaPackMigrationProvenance(
    string SourceSchema,
    string SourceVersion,
    string SourceContentFingerprint,
    string MigratorVersion,
    DateTimeOffset MigratedAtUtc);

public sealed record ArenaScenarioPackContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Name,
    string Version,
    string ContentFingerprint,
    ArenaPackMigrationProvenance? Migration,
    ImmutableArray<ArenaScenarioInvariant> Invariants,
    ImmutableArray<ArenaScenarioDefinition> Scenarios,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaBenchmarkCase(
    string Id,
    string ScenarioId,
    ImmutableArray<string> RubricIds,
    int Repetitions,
    ImmutableArray<string> RequiredProviderCapabilities,
    ImmutableArray<string> RequiredEvidenceIds);

public sealed record ArenaBenchmarkPackContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Name,
    string Version,
    string ContentFingerprint,
    ArenaPackMigrationProvenance? Migration,
    string ScenarioPackId,
    ImmutableArray<ArenaBenchmarkCase> Cases,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaRouteScoreComponent(
    string Id,
    decimal Weight,
    decimal? CurrentScore,
    decimal? ProposedScore,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaRouteConstraint(
    string Id,
    ArenaRouteConstraintKind Kind,
    string Description,
    bool? Satisfied,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaModelRouteChange(
    string Id,
    string AgentId,
    string CurrentModelId,
    string ProposedModelId,
    string Reason,
    bool RequiresExplicitApproval,
    ImmutableArray<string> EvidenceRunIds,
    int SampleCount,
    ArenaEvidenceSufficiency EvidenceSufficiency,
    ImmutableArray<ArenaRouteScoreComponent> ScoreComponents,
    ImmutableArray<ArenaRouteConstraint> Constraints,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaRouteProposalContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ExperimentId,
    string SetupFingerprint,
    ArenaRouteProposalStatus Status,
    ImmutableArray<ArenaModelRouteChange> Changes) : IArenaVersionedContract;

public sealed record ArenaAppliedRouteChange(
    string ProposalChangeId,
    string AgentId,
    string PreviousModelId,
    string AppliedModelId);

/// <summary>
/// Immutable receipt emitted only after a separately authorized application
/// action. Optimizers emit ArenaRouteProposalContract and can never create this
/// receipt or mutate routing on their own.
/// </summary>
public sealed record ArenaRouteApplicationReceiptContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ProposalId,
    string ExperimentId,
    string SetupFingerprint,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset AppliedAtUtc,
    ImmutableArray<ArenaAppliedRouteChange> Changes,
    ArenaEvidenceAssertion ApprovalEvidence,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaFaultInjection(
    string Id,
    ArenaFaultTarget Target,
    ArenaFaultKind Kind,
    int AtSequence,
    int DurationMilliseconds,
    int Intensity,
    int MaxOccurrences,
    string ExpectedBehavior);

public sealed record ArenaFaultProfileContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Name,
    string Seed,
    ImmutableArray<ArenaFaultInjection> Injections,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaQaEnvironment(
    string OperatingSystem,
    string Architecture,
    string RuntimeVersion,
    string SdkVersion,
    string Configuration,
    bool IsReleaseBuild);

public sealed record ArenaQaRepositoryProvenance(
    string Id,
    string SourceRevision,
    string TreeFingerprint,
    bool IsWorkingTreeClean);

public sealed record ArenaQaToolchainEntry(
    string Name,
    string Version);

public sealed record ArenaQaTestCounts(
    int Passed,
    int Failed,
    int Skipped,
    int Total);

public sealed record ArenaQaGateEvidence(
    string Id,
    ArenaQaGateOutcome Outcome,
    bool Required,
    long DurationMilliseconds,
    ArenaQaTestCounts Tests,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaQaArtifact(
    string Id,
    string Kind,
    string RelativePath,
    string Sha256,
    ArenaQaArtifactProvenance? Provenance);

public sealed record ArenaQaArtifactProvenance(
    string TreeFingerprint,
    DateTimeOffset CapturedAtUtc,
    string Theme,
    int ViewportWidthDip,
    int ViewportHeightDip,
    decimal DpiScale,
    string ExpectedState,
    string? LinkedAutomationArtifactId,
    string? BaselineArtifactId);

public sealed record ArenaQaPerformanceMeasurement(
    string Id,
    string Metric,
    decimal Value,
    string Unit,
    ArenaQaThresholdKind ThresholdKind,
    decimal Threshold,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaQaSchemaCheck(
    string Id,
    string Schema,
    string? MigratedFromSchema,
    ArenaQaGateOutcome Outcome,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaQaLiveProviderCoverage(
    bool Required,
    ArenaEvidenceState State,
    ImmutableArray<string> ProviderProfileIds,
    ImmutableArray<string> EvidenceRunIds,
    string? Limitation);

public sealed record ArenaQaAcceptedLimitation(
    string Id,
    string Summary,
    bool UserAccepted,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaQaInspectionEvidence(
    bool UserAccepted,
    DateTimeOffset? AcceptedAtUtc,
    string? TreeFingerprint,
    ImmutableArray<string> ScreenshotArtifactIds,
    ImmutableArray<string> AutomationArtifactIds,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaQaEvidenceContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SourceRevision,
    string TreeFingerprint,
    string SealManifestId,
    bool IsWorkingTreeClean,
    ImmutableArray<ArenaQaRepositoryProvenance> NestedRepositories,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ArenaQaVerdict Verdict,
    int CleanFullPasses,
    ArenaQaEnvironment Environment,
    ImmutableArray<ArenaQaToolchainEntry> Toolchain,
    ImmutableArray<ArenaQaGateEvidence> Gates,
    ImmutableArray<ArenaQaArtifact> Artifacts,
    ImmutableArray<ArenaQaPerformanceMeasurement> Performance,
    ImmutableArray<ArenaQaSchemaCheck> SchemaChecks,
    ArenaQaLiveProviderCoverage LiveProviderCoverage,
    ImmutableArray<ArenaQaAcceptedLimitation> AcceptedLimitations,
    ArenaQaInspectionEvidence Inspection,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;
