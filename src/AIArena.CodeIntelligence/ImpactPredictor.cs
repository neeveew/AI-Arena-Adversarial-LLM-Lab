namespace AIArena.CodeIntelligence;

public sealed record ImpactPredictionOptions(
    int MaxTraversalDepth = 4,
    int MaxAffectedNodes = 10_000,
    int MaxFocusedTests = 512,
    int MaxChangedProductionSymbols = 256);

/// <summary>
/// Predicts likely impact from proven static relationships. Results are
/// explicitly predictions; absent static test evidence is not runtime coverage.
/// </summary>
public sealed class ImpactPredictor
{
    private readonly ImpactPredictionOptions _options;

    public ImpactPredictor(ImpactPredictionOptions? options = null)
    {
        _options = options ?? new ImpactPredictionOptions();
        if (_options.MaxTraversalDepth < 0
            || _options.MaxAffectedNodes <= 0
            || _options.MaxFocusedTests <= 0
            || _options.MaxChangedProductionSymbols <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Impact prediction limits must be positive, except traversal depth which may be zero.");
        }
    }

    public ImpactPrediction Predict(
        ImpactSnapshot snapshot,
        ImpactChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Availability == ImpactAvailability.Unavailable)
        {
            return Empty(
                ImpactAvailability.Unavailable,
                new ImpactDiagnostic(
                    "impact-prediction-unavailable",
                    ImpactDiagnosticSeverity.Warning,
                    "Impact cannot be predicted because the semantic index is unavailable."));
        }

        var diagnostics = new List<ImpactDiagnostic>();
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var changedPaths = changes.ChangedRelativePaths
            .Select(TryNormalizeRelative)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var rejectedPathCount = changes.ChangedRelativePaths.Count - changedPaths.Length;
        if (rejectedPathCount > 0)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "change-path-rejected",
                ImpactDiagnosticSeverity.Warning,
                $"{rejectedPathCount} changed path(s) were rejected because they were not safe workspace-relative paths."));
        }

        var seeds = new HashSet<string>(StringComparer.Ordinal);
        var matchedChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodesByDefinitionPath = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var definitionPath in node.Definitions
                         .Select(definition => definition.RelativePath)
                         .Append(node.Kind is ImpactNodeKind.File or ImpactNodeKind.XamlFile
                             ? node.QualifiedName
                             : null)
                         .OfType<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!nodesByDefinitionPath.TryGetValue(definitionPath, out var nodeIds))
                {
                    nodeIds = new HashSet<string>(StringComparer.Ordinal);
                    nodesByDefinitionPath.Add(definitionPath, nodeIds);
                }
                nodeIds.Add(node.Id);
            }
        }
        foreach (var changedPath in changedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodesByDefinitionPath.TryGetValue(changedPath, out var nodeIds))
            {
                continue;
            }
            matchedChangedPaths.Add(changedPath);
            foreach (var nodeId in nodeIds)
            {
                seeds.Add(nodeId);
            }
        }
        var unmatchedChangedPaths = changedPaths
            .Where(path => !matchedChangedPaths.Contains(path))
            .ToArray();
        foreach (var path in unmatchedChangedPaths.Take(32))
        {
            diagnostics.Add(new ImpactDiagnostic(
                "changed-path-not-indexed",
                ImpactDiagnosticSeverity.Warning,
                "The changed path is not present in the current semantic index. Deletion, rename, or unsupported-input impact remains unknown.",
                path));
        }
        if (unmatchedChangedPaths.Length > 32)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "changed-path-diagnostic-limit",
                ImpactDiagnosticSeverity.Warning,
                $"{unmatchedChangedPaths.Length - 32} additional unmatched changed path(s) were omitted from diagnostics."));
        }
        var missingNodeCount = 0;
        foreach (var nodeId in changes.ChangedNodeIds ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.ContainsKey(nodeId))
            {
                seeds.Add(nodeId);
            }
            else
            {
                missingNodeCount++;
                diagnostics.Add(new ImpactDiagnostic(
                    "changed-node-missing",
                    ImpactDiagnosticSeverity.Warning,
                    "A requested changed node is not present in the semantic snapshot."));
            }
        }

        if (seeds.Count == 0)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "change-evidence-not-indexed",
                ImpactDiagnosticSeverity.Information,
                "None of the supplied changes matched indexed definitions."));
            return Empty(
                rejectedPathCount > 0
                    || unmatchedChangedPaths.Length > 0
                    || missingNodeCount > 0
                    ? ImpactAvailability.Partial
                    : snapshot.Availability,
                diagnostics.ToArray());
        }

        var transitions = BuildTransitions(snapshot.Relationships, cancellationToken);
        var traversal = Traverse(
            seeds,
            transitions,
            _options.MaxTraversalDepth,
            _options.MaxAffectedNodes,
            cancellationToken);
        if (traversal.LimitReached)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "impact-node-limit",
                ImpactDiagnosticSeverity.Warning,
                "The affected-node limit was reached; predictions are partial."));
        }

        var affectedFiles = BuildAffectedFiles(nodes, traversal, cancellationToken);
        var affectedFeatures = BuildAffectedFeatures(affectedFiles);
        var focusedTests = BuildFocusedTests(snapshot, nodes, traversal, cancellationToken)
            .Take(_options.MaxFocusedTests)
            .ToArray();
        if (focusedTests.Length == _options.MaxFocusedTests
            && traversal.Distance.Keys.Count(id => nodes.TryGetValue(id, out var node) && node.IsTest)
            > _options.MaxFocusedTests)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "focused-test-limit",
                ImpactDiagnosticSeverity.Warning,
                "The focused-test recommendation limit was reached."));
        }

        var productionTestEvidence = BuildProductionTestEvidence(
            snapshot,
            nodes,
            seeds,
            transitions,
            cancellationToken);
        if (productionTestEvidence.LimitReached)
        {
            diagnostics.Add(new ImpactDiagnostic(
                "changed-symbol-test-evidence-limit",
                ImpactDiagnosticSeverity.Warning,
                "Static test evidence was bounded to the configured changed-production-symbol limit."));
        }
        var availability = traversal.LimitReached
                || productionTestEvidence.LimitReached
                || rejectedPathCount > 0
                || unmatchedChangedPaths.Length > 0
                || missingNodeCount > 0
            ? ImpactAvailability.Partial
            : snapshot.Availability;
        return new ImpactPrediction(
            availability,
            seeds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            traversal.Distance.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            affectedFiles,
            affectedFeatures,
            focusedTests,
            productionTestEvidence.Items,
            diagnostics
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ToArray());
    }

    private TestEvidenceResult BuildProductionTestEvidence(
        ImpactSnapshot snapshot,
        IReadOnlyDictionary<string, ImpactNode> nodes,
        IReadOnlySet<string> seeds,
        IReadOnlyDictionary<string, IReadOnlyList<Transition>> transitions,
        CancellationToken cancellationToken)
    {
        var evidence = new List<ChangedProductionSymbolTestEvidence>();
        var productionSeeds = seeds
            .Where(seedId =>
                nodes.TryGetValue(seedId, out var node)
                && node.IsProduction
                && node.Kind is not (
                    ImpactNodeKind.Project
                    or ImpactNodeKind.Package
                    or ImpactNodeKind.File
                    or ImpactNodeKind.XamlFile
                    or ImpactNodeKind.XamlResource))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        foreach (var seedId in productionSeeds.Take(_options.MaxChangedProductionSymbols))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[seedId];
            var local = Traverse(
                [seedId],
                transitions,
                _options.MaxTraversalDepth,
                _options.MaxAffectedNodes,
                cancellationToken);
            var tests = local.Distance
                .Where(pair => pair.Key != seedId
                    && nodes.TryGetValue(pair.Key, out var candidate)
                    && candidate.IsTest)
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            StaticTestEvidenceState state;
            string explanation;
            if (tests.Length > 0)
            {
                state = tests.Any(pair => pair.Value == 1)
                    ? StaticTestEvidenceState.Direct
                    : StaticTestEvidenceState.Transitive;
                explanation = state == StaticTestEvidenceState.Direct
                    ? "A test has direct semantic evidence against this changed symbol."
                    : "A test is reachable through the bounded semantic impact graph.";
            }
            else if (snapshot.Availability == ImpactAvailability.Partial || local.LimitReached)
            {
                state = StaticTestEvidenceState.Partial;
                explanation = "No test was found, but the semantic index or traversal is partial; coverage is unknown.";
            }
            else
            {
                state = StaticTestEvidenceState.NoStaticEvidence;
                explanation = "No affected test is statically evidenced. This is not proof that runtime coverage is absent.";
            }

            evidence.Add(new ChangedProductionSymbolTestEvidence(
                node.Id,
                node.QualifiedName,
                state,
                tests.Select(pair => pair.Key).ToArray(),
                explanation));
        }
        return new TestEvidenceResult(
            evidence,
            productionSeeds.Length > _options.MaxChangedProductionSymbols);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Transition>> BuildTransitions(
        IReadOnlyList<ImpactRelationship> relationships,
        CancellationToken cancellationToken)
    {
        var transitions = new Dictionary<string, List<Transition>>(StringComparer.Ordinal);
        foreach (var relationship in relationships.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (relationship.Kind)
            {
                case ImpactRelationshipKind.Declares:
                    Add(relationship.SourceId, relationship.TargetId, relationship);
                    break;
                case ImpactRelationshipKind.XamlCodeBehind:
                    Add(relationship.SourceId, relationship.TargetId, relationship);
                    Add(relationship.TargetId, relationship.SourceId, relationship);
                    break;
                case ImpactRelationshipKind.References:
                case ImpactRelationshipKind.Calls:
                case ImpactRelationshipKind.Inherits:
                case ImpactRelationshipKind.Implements:
                case ImpactRelationshipKind.ProjectReferences:
                case ImpactRelationshipKind.PackageReferences:
                case ImpactRelationshipKind.XamlUsesResource:
                    Add(relationship.TargetId, relationship.SourceId, relationship);
                    break;
                case ImpactRelationshipKind.Tests:
                    Add(relationship.SourceId, relationship.TargetId, relationship);
                    break;
            }
        }
        return transitions.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Transition>)pair.Value
                .OrderBy(item => item.TargetId, StringComparer.Ordinal)
                .ThenBy(item => item.Relationship.Kind)
                .ToArray(),
            StringComparer.Ordinal);

        void Add(string source, string target, ImpactRelationship relationship)
        {
            if (!transitions.TryGetValue(source, out var values))
            {
                values = [];
                transitions.Add(source, values);
            }
            values.Add(new Transition(target, relationship));
        }
    }

    private static TraversalResult Traverse(
        IEnumerable<string> seeds,
        IReadOnlyDictionary<string, IReadOnlyList<Transition>> transitions,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        var reason = new Dictionary<string, ImpactRelationshipKind>(StringComparer.Ordinal);
        var confidence = new Dictionary<string, ImpactConfidence>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var seed in seeds.OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (distance.TryAdd(seed, 0))
            {
                confidence.Add(seed, ImpactConfidence.High);
                queue.Enqueue(seed);
            }
        }
        var limitReached = false;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = queue.Dequeue();
            var nextDistance = distance[source] + 1;
            if (nextDistance > maxDepth || !transitions.TryGetValue(source, out var values))
            {
                continue;
            }
            foreach (var transition in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathConfidence = Weakest(
                    confidence[source],
                    transition.Relationship.Confidence);
                if (distance.TryGetValue(transition.TargetId, out var existingDistance))
                {
                    if (nextDistance < existingDistance
                        || nextDistance == existingDistance
                        && pathConfidence > confidence[transition.TargetId])
                    {
                        distance[transition.TargetId] = nextDistance;
                        confidence[transition.TargetId] = pathConfidence;
                        reason[transition.TargetId] = transition.Relationship.Kind;
                        queue.Enqueue(transition.TargetId);
                    }
                    continue;
                }
                if (distance.Count >= maxNodes)
                {
                    limitReached = true;
                    break;
                }
                distance.Add(transition.TargetId, nextDistance);
                confidence.Add(transition.TargetId, pathConfidence);
                reason.Add(transition.TargetId, transition.Relationship.Kind);
                queue.Enqueue(transition.TargetId);
            }
            if (limitReached)
            {
                break;
            }
        }
        return new TraversalResult(distance, reason, confidence, limitReached);
    }

    private static AffectedFile[] BuildAffectedFiles(
        IReadOnlyDictionary<string, ImpactNode> nodes,
        TraversalResult traversal,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, (int Distance, ImpactConfidence Confidence, string Reason)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in traversal.Distance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(pair.Key, out var node))
            {
                continue;
            }
            foreach (var path in node.Definitions.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var distanceConfidence = pair.Value switch
                {
                    0 or 1 => ImpactConfidence.High,
                    2 => ImpactConfidence.Medium,
                    _ => ImpactConfidence.Low
                };
                var confidence = Weakest(
                    distanceConfidence,
                    traversal.Confidence.GetValueOrDefault(pair.Key, ImpactConfidence.Low));
                var reason = pair.Value == 0
                    ? "Changed definition"
                    : $"{traversal.Reason.GetValueOrDefault(pair.Key)} relationship at distance {pair.Value}";
                if (!files.TryGetValue(path, out var existing)
                    || pair.Value < existing.Distance
                    || pair.Value == existing.Distance && confidence > existing.Confidence)
                {
                    files[path] = (pair.Value, confidence, reason);
                }
            }
        }
        return files
            .OrderBy(pair => pair.Value.Distance)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AffectedFile(
                pair.Key,
                pair.Value.Distance,
                pair.Value.Confidence,
                pair.Value.Reason))
            .ToArray();
    }

    private static AffectedFeature[] BuildAffectedFeatures(IReadOnlyList<AffectedFile> files) =>
        files.GroupBy(file => FeatureName(file.RelativePath), StringComparer.Ordinal)
            .OrderBy(group => group.Min(item => item.Distance))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AffectedFeature(
                group.Key,
                group.Select(item => item.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                group.Min(item => item.Distance)))
            .ToArray();

    private static IEnumerable<FocusedTestRecommendation> BuildFocusedTests(
        ImpactSnapshot snapshot,
        IReadOnlyDictionary<string, ImpactNode> nodes,
        TraversalResult traversal,
        CancellationToken cancellationToken)
    {
        var projects = snapshot.Projects.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in traversal.Distance
                     .Where(pair => nodes.TryGetValue(pair.Key, out var node) && node.IsTest)
                     .OrderBy(pair => pair.Value)
                     .ThenBy(pair => nodes[pair.Key].QualifiedName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[pair.Key];
            if (node.ProjectRelativePath is null
                || !projects.TryGetValue(node.ProjectRelativePath, out var project))
            {
                continue;
            }
            var conventional = project.PackageNames.Contains(
                "Microsoft.NET.Test.Sdk",
                StringComparer.OrdinalIgnoreCase);
            var filterName = TestFilterName(node.QualifiedName);
            IReadOnlyList<string> command = conventional
                ?
                [
                    "dotnet",
                    "test",
                    project.RelativePath,
                    "--no-restore",
                    "--filter",
                    $"FullyQualifiedName={filterName}"
                ]
                :
                [
                    "dotnet",
                    "run",
                    "--project",
                    project.RelativePath,
                    "--no-restore"
                ];
            yield return new FocusedTestRecommendation(
                node.Id,
                node.QualifiedName,
                project.RelativePath,
                command,
                pair.Value,
                Weakest(
                    pair.Value <= 1 ? ImpactConfidence.High : ImpactConfidence.Medium,
                    traversal.Confidence.GetValueOrDefault(node.Id, ImpactConfidence.Low)),
                conventional
                    ? "The test is reachable from a changed symbol; use an exact test filter after approval."
                    : "The executable test harness is affected and cannot be narrowed safely below its project.");
        }
    }

    private static string FeatureName(string path)
    {
        var segments = path.Split('/');
        var modules = Array.FindIndex(segments, segment =>
            segment.Equals("Modules", StringComparison.OrdinalIgnoreCase));
        if (modules >= 0 && modules + 1 < segments.Length)
        {
            return segments[modules + 1];
        }
        var source = Array.FindIndex(segments, segment =>
            segment.Equals("src", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tests", StringComparison.OrdinalIgnoreCase));
        if (source >= 0 && source + 1 < segments.Length)
        {
            return segments[source + 1];
        }
        return segments.Length > 1 ? segments[0] : "Workspace";
    }

    private static string TestFilterName(string qualifiedName)
    {
        var parameters = qualifiedName.IndexOf('(');
        return parameters > 0 ? qualifiedName[..parameters] : qualifiedName;
    }

    private static ImpactConfidence Weakest(
        ImpactConfidence left,
        ImpactConfidence right) =>
        (ImpactConfidence)Math.Min((int)left, (int)right);

    private static string? TryNormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\0'))
        {
            return null;
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }
        return normalized;
    }

    private static ImpactPrediction Empty(
        ImpactAvailability availability,
        params ImpactDiagnostic[] diagnostics) =>
        new(
            availability,
            [],
            [],
            [],
            [],
            [],
            [],
            diagnostics);

    private sealed record Transition(string TargetId, ImpactRelationship Relationship);

    private sealed record TraversalResult(
        IReadOnlyDictionary<string, int> Distance,
        IReadOnlyDictionary<string, ImpactRelationshipKind> Reason,
        IReadOnlyDictionary<string, ImpactConfidence> Confidence,
        bool LimitReached);

    private sealed record TestEvidenceResult(
        IReadOnlyList<ChangedProductionSymbolTestEvidence> Items,
        bool LimitReached);
}
