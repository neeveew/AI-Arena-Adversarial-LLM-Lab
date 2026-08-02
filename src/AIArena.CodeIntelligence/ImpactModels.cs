namespace AIArena.CodeIntelligence;

public enum ImpactAvailability
{
    Unavailable,
    Partial,
    Available
}

public enum ImpactDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum ImpactNodeKind
{
    Project,
    Package,
    File,
    XamlFile,
    XamlResource,
    Type,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Test
}

public enum ImpactRelationshipKind
{
    Declares,
    References,
    Calls,
    Inherits,
    Implements,
    ProjectReferences,
    PackageReferences,
    XamlCodeBehind,
    XamlUsesResource,
    Tests
}

public enum ImpactConfidence
{
    Low,
    Medium,
    High
}

public sealed record ImpactDiagnostic(
    string Code,
    ImpactDiagnosticSeverity Severity,
    string Message,
    string? RelativePath = null);

public sealed record ImpactEvidence(
    string RelativePath,
    int? Line = null,
    int? Column = null);

public sealed record ImpactNode(
    string Id,
    ImpactNodeKind Kind,
    string Name,
    string QualifiedName,
    string? ProjectRelativePath,
    bool IsProduction,
    bool IsTest,
    IReadOnlyList<ImpactEvidence> Definitions);

public sealed record ImpactRelationship(
    string Id,
    string SourceId,
    string TargetId,
    ImpactRelationshipKind Kind,
    int Count,
    ImpactConfidence Confidence,
    IReadOnlyList<ImpactEvidence> Evidence);

public sealed record ImpactProject(
    string Id,
    string Name,
    string RelativePath,
    bool IsTestProject,
    bool IsPartial,
    IReadOnlyList<string> ProjectReferenceRelativePaths,
    IReadOnlyList<string> PackageNames);

public sealed record ImpactSnapshot(
    string Schema,
    ImpactAvailability Availability,
    IReadOnlyList<ImpactProject> Projects,
    IReadOnlyList<ImpactNode> Nodes,
    IReadOnlyList<ImpactRelationship> Relationships,
    IReadOnlyList<ImpactDiagnostic> Diagnostics)
{
    public const string CurrentSchema = "ai-arena.code-impact.v1";

    public static ImpactSnapshot Unavailable(params ImpactDiagnostic[] diagnostics) =>
        new(CurrentSchema, ImpactAvailability.Unavailable, [], [], [], diagnostics);
}

public sealed record ImpactChangeSet(
    IReadOnlyList<string> ChangedRelativePaths,
    IReadOnlyList<string>? ChangedNodeIds = null);

public sealed record AffectedFile(
    string RelativePath,
    int Distance,
    ImpactConfidence Confidence,
    string Reason);

public sealed record AffectedFeature(
    string Name,
    IReadOnlyList<string> RelativePaths,
    int NearestDistance);

public enum StaticTestEvidenceState
{
    Unavailable,
    Partial,
    Direct,
    Transitive,
    NoStaticEvidence
}

public sealed record FocusedTestRecommendation(
    string TestNodeId,
    string TestName,
    string ProjectRelativePath,
    IReadOnlyList<string> FileNameAndArguments,
    int Distance,
    ImpactConfidence Confidence,
    string Reason);

public sealed record ChangedProductionSymbolTestEvidence(
    string NodeId,
    string SymbolName,
    StaticTestEvidenceState State,
    IReadOnlyList<string> TestNodeIds,
    string Explanation);

public sealed record ImpactPrediction(
    ImpactAvailability Availability,
    IReadOnlyList<string> SeedNodeIds,
    IReadOnlyList<string> AffectedNodeIds,
    IReadOnlyList<AffectedFile> AffectedFiles,
    IReadOnlyList<AffectedFeature> AffectedFeatures,
    IReadOnlyList<FocusedTestRecommendation> FocusedTests,
    IReadOnlyList<ChangedProductionSymbolTestEvidence> ProductionTestEvidence,
    IReadOnlyList<ImpactDiagnostic> Diagnostics);

public sealed record ImpactNodeDetails(
    ImpactNode Node,
    IReadOnlyList<ImpactRelationship> IncomingReferences,
    IReadOnlyList<ImpactRelationship> OutgoingReferences,
    IReadOnlyList<ImpactRelationship> Callers,
    IReadOnlyList<ImpactRelationship> Callees,
    IReadOnlyList<ImpactRelationship> BaseTypesAndInterfaces,
    IReadOnlyList<ImpactRelationship> DerivedTypesAndImplementations);

public enum TestObservationOutcome
{
    Passed,
    Failed,
    Skipped,
    Unavailable
}

public sealed record TestRunObservation(
    string TestNodeId,
    string TestName,
    string ProjectRelativePath,
    string RunId,
    DateTimeOffset ObservedAt,
    TestObservationOutcome Outcome);

public enum TestReliabilityState
{
    Unavailable,
    Passing,
    Failing,
    RepeatedlyFailing,
    Flaky
}

public sealed record TestReliabilitySummary(
    string TestNodeId,
    string TestName,
    string ProjectRelativePath,
    ImpactAvailability Availability,
    TestReliabilityState State,
    int Passed,
    int Failed,
    int Skipped,
    int ConsecutiveFailures,
    TestObservationOutcome? LatestOutcome,
    IReadOnlyList<string> EvidenceRunIds);
