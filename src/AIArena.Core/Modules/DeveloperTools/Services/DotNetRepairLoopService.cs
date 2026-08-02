using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Builds conservative repair intent, focused verification, and before/after
/// evidence without executing commands or editing workspace files.
/// </summary>
public sealed class DotNetRepairLoopService
{
    private const int MaxAffectedPaths = 48;
    private const int MaxDiffHunks = 12;
    private const int MaxVerificationPlans = 16;

    public DotNetRepairProposal CreateProposal(
        DotNetWorkspaceSnapshot? snapshot,
        DotNetCommandResult? result,
        DotNetRepairImpactHint? impactHint = null)
    {
        var baseline = CaptureEvidence(snapshot, result);
        var source = SelectRepairSource(snapshot, result);
        if (snapshot is null || source is null)
        {
            return new(
                RepairId(source?.Id ?? "unavailable"),
                source?.Id ?? "unavailable",
                source?.Code ?? "DNR000",
                baseline.Fingerprint,
                "Solution Doctor does not have a bounded finding with a safe relative target.",
                [],
                [],
                EmptyVerificationPlan("No typed project target is available."),
                DotNetRepairAvailability.Unavailable,
                RequiresExplicitApproval: true,
                "Run a read-only workspace inspection or a typed build before proposing an edit.");
        }

        var affectedPaths = SourcePaths(source, impactHint);
        var verification = BuildVerificationPlan(snapshot, source, affectedPaths, result, impactHint);
        var diff = BuildIntentDiff(source);
        var isPartial = snapshot.IsPartial
            || snapshot.ScanLimitReached
            || snapshot.FindingsLimitReached
            || baseline.IsPartial
            || verification.IsPartial
            || impactHint?.IsPartial == true;
        var availability = affectedPaths.Count == 0 || diff.Count == 0
            ? DotNetRepairAvailability.Unavailable
            : isPartial
                ? DotNetRepairAvailability.Partial
                : DotNetRepairAvailability.Ready;
        var limitation = availability switch
        {
            DotNetRepairAvailability.Unavailable =>
                "The finding is useful evidence, but no safe relative edit target could be proven.",
            DotNetRepairAvailability.Partial =>
                "Some workspace, impact, or test evidence is partial. The preview must not be treated as complete coverage.",
            _ =>
                "The diff is repair intent, not source text. Builder must inspect the target and return an exact diff or command for explicit approval."
        };

        return new(
            RepairId(source.Id),
            source.Id,
            source.Code,
            baseline.Fingerprint,
            Explain(source),
            affectedPaths,
            diff,
            verification,
            availability,
            RequiresExplicitApproval: true,
            limitation);
    }

    public DotNetRepairEvidenceSnapshot CaptureEvidence(
        DotNetWorkspaceSnapshot? snapshot,
        DotNetCommandResult? result)
    {
        var findings = new Dictionary<string, DotNetRepairFindingEvidence>(StringComparer.Ordinal);
        if (snapshot is not null)
        {
            foreach (var finding in snapshot.Findings)
            {
                AddFinding(findings, FindingEvidence(finding));
            }
        }

        if (result is not null)
        {
            foreach (var finding in result.Findings)
            {
                AddFinding(findings, FindingEvidence(finding));
            }

            foreach (var diagnostic in result.Diagnostics
                         .Where(item => item.Severity is
                             DotNetBuildDiagnosticSeverity.Warning or DotNetBuildDiagnosticSeverity.Error))
            {
                AddFinding(findings, DiagnosticEvidence(diagnostic));
            }

            if (!result.Succeeded
                && !result.WasCancelled
                && result.Findings.Count == 0
                && result.Diagnostics.Count == 0)
            {
                var target = SafeRelativePath(result.Command.TargetRelativePath);
                AddFinding(
                    findings,
                    new(
                        StableId(
                            "command",
                            $"{result.Command.Kind}|{result.Command.TargetKind}|{target ?? result.Command.Id}"),
                        "DOTNET_EXIT",
                        DotNetWorkspaceDiagnosticSeverity.Error,
                        target is null ? [] : [target]));
            }
        }

        var ordered = findings.Values
            .OrderBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
        var partial = snapshot is null
            || snapshot.IsPartial
            || snapshot.ScanLimitReached
            || snapshot.FindingsLimitReached
            || result?.StructuredEvidenceLimitReached == true;
        return new(
            EvidenceFingerprint(snapshot, result, ordered),
            ordered,
            partial);
    }

    public bool IsProposalCurrent(
        DotNetRepairProposal proposal,
        DotNetWorkspaceSnapshot? snapshot,
        DotNetCommandResult? result)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.BaselineFingerprint.Equals(
            CaptureEvidence(snapshot, result).Fingerprint,
            StringComparison.Ordinal);
    }

    public DotNetRepairComparison Compare(
        DotNetRepairEvidenceSnapshot baseline,
        DotNetRepairEvidenceSnapshot current,
        bool verificationSucceeded,
        bool wasCancelled)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var before = baseline.Findings.ToDictionary(finding => finding.Id, StringComparer.Ordinal);
        var after = current.Findings.ToDictionary(finding => finding.Id, StringComparer.Ordinal);
        var transitions = new List<DotNetRepairFindingTransition>();
        foreach (var finding in before.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(finding.Id, out var remaining))
            {
                transitions.Add(new(
                    finding.Id,
                    finding.Code,
                    current.IsPartial
                        ? DotNetRepairFindingTransitionState.Unknown
                        : DotNetRepairFindingTransitionState.Fixed,
                    finding.RelativePaths));
                continue;
            }

            transitions.Add(new(
                finding.Id,
                finding.Code,
                SeverityRank(remaining.Severity) > SeverityRank(finding.Severity)
                    ? DotNetRepairFindingTransitionState.Regressed
                    : DotNetRepairFindingTransitionState.Unchanged,
                finding.RelativePaths
                    .Concat(remaining.RelativePaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxAffectedPaths)
                    .ToArray()));
        }

        foreach (var finding in after.Values
                     .Where(item => !before.ContainsKey(item.Id))
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            transitions.Add(new(
                finding.Id,
                finding.Code,
                baseline.IsPartial
                    ? DotNetRepairFindingTransitionState.Unknown
                    : DotNetRepairFindingTransitionState.New,
                finding.RelativePaths));
        }

        var ordered = transitions
            .OrderBy(item => item.State)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var partial = baseline.IsPartial
            || current.IsPartial
            || ordered.Any(item => item.State == DotNetRepairFindingTransitionState.Unknown);
        var outcome = wasCancelled
            ? DotNetRepairLoopOutcome.Cancelled
            : partial
                ? DotNetRepairLoopOutcome.Partial
                : ordered.Any(item => item.State is
                    DotNetRepairFindingTransitionState.New or DotNetRepairFindingTransitionState.Regressed)
                    ? DotNetRepairLoopOutcome.Regressed
                    : ordered.Any(item => item.State == DotNetRepairFindingTransitionState.Unchanged)
                        ? DotNetRepairLoopOutcome.Unchanged
                        : verificationSucceeded
                            ? DotNetRepairLoopOutcome.Fixed
                            : DotNetRepairLoopOutcome.Partial;
        var summary = outcome switch
        {
            DotNetRepairLoopOutcome.Fixed => "Verification passed and every baseline finding is absent.",
            DotNetRepairLoopOutcome.Unchanged => "Verification left one or more baseline findings unchanged.",
            DotNetRepairLoopOutcome.Regressed => "Verification introduced a new finding or increased finding severity.",
            DotNetRepairLoopOutcome.Cancelled => "The explicitly approved repair or verification command was cancelled.",
            _ => "Evidence is partial, so Solution Doctor cannot claim a fixed or regressed result."
        };
        return new(outcome, ordered, partial, summary);
    }

    private static RepairSource? SelectRepairSource(
        DotNetWorkspaceSnapshot? snapshot,
        DotNetCommandResult? result)
    {
        if (result is not null)
        {
            var errorFinding = result.Findings
                .Where(finding => finding.Severity == DotNetWorkspaceDiagnosticSeverity.Error)
                .OrderBy(finding => finding.Code, StringComparer.Ordinal)
                .ThenBy(finding => finding.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (errorFinding is not null)
            {
                return RepairSource.FromFinding(errorFinding);
            }

            var errorDiagnostic = result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error)
                .OrderBy(diagnostic => diagnostic.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(diagnostic => diagnostic.Line)
                .ThenBy(diagnostic => diagnostic.Column)
                .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .FirstOrDefault();
            if (errorDiagnostic is not null)
            {
                return RepairSource.FromDiagnostic(errorDiagnostic);
            }
        }

        var workspaceFinding = snapshot?.Findings
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (workspaceFinding is not null)
        {
            return RepairSource.FromFinding(workspaceFinding);
        }

        var remainingFinding = result?.Findings
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (remainingFinding is not null)
        {
            return RepairSource.FromFinding(remainingFinding);
        }

        return result?.Diagnostics
            .Where(diagnostic => diagnostic.Severity != DotNetBuildDiagnosticSeverity.Information)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Select(RepairSource.FromDiagnostic)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> SourcePaths(
        RepairSource source,
        DotNetRepairImpactHint? impactHint)
    {
        return source.Paths
            .Concat(impactHint?.AffectedProjectRelativePaths ?? [])
            .Concat(impactHint?.LikelyTestProjectRelativePaths ?? [])
            .Select(SafeRelativePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAffectedPaths)
            .ToArray();
    }

    private static IReadOnlyList<DotNetRepairDiffHunk> BuildIntentDiff(RepairSource source)
    {
        var hunks = new List<DotNetRepairDiffHunk>();
        if (source.Diagnostic is { } diagnostic)
        {
            var path = SafeRelativePath(diagnostic.RelativePath)
                ?? SafeRelativePath(diagnostic.ProjectRelativePath);
            if (path is null)
            {
                return [];
            }

            var location = diagnostic.Line is null
                ? diagnostic.Code
                : $"{diagnostic.Code} at line {diagnostic.Line.Value}, column {diagnostic.Column ?? 1}";
            hunks.Add(new(
                path,
                location,
                $"Compiler diagnostic {diagnostic.Code} remains.",
                $"Source satisfies {diagnostic.Code}; exact source text must come from bounded file inspection.",
                DotNetRepairDiffReadiness.RequiresInspection,
                "Compiler locations identify the repair boundary but do not prove the intended source edit."));
            return hunks;
        }

        var finding = source.Finding!;
        switch (finding.Category)
        {
            case DotNetFindingCategory.ProjectReferenceCycle:
            {
                var edge = finding.RootCauseChain
                    .LastOrDefault(step => step.Kind == DotNetFindingEvidenceKind.ProjectReference);
                var path = SafeRelativePath(edge?.RelativePath);
                var target = SafeRelativePath(edge?.RelatedRelativePath);
                if (path is not null && target is not null)
                {
                    hunks.Add(new(
                        path,
                        "ProjectReference cycle edge",
                        $"ProjectReference: {path} -> {target}",
                        "Remove this edge or redesign the dependency direction after owner review.",
                        DotNetRepairDiffReadiness.RequiresInspection,
                        "Removing an arbitrary cycle edge can change product ownership and is not safe to automate."));
                }

                break;
            }
            case DotNetFindingCategory.TargetFrameworkIncompatibility:
            {
                var consumer = finding.RootCauseChain.FirstOrDefault(step =>
                    step.Kind == DotNetFindingEvidenceKind.TargetFramework
                    && step.Label.Contains("Referencing", StringComparison.OrdinalIgnoreCase));
                var dependency = finding.RootCauseChain.LastOrDefault(step =>
                    step.Kind == DotNetFindingEvidenceKind.TargetFramework);
                var path = SafeRelativePath(consumer?.RelativePath ?? finding.PrimaryRelativePath);
                if (path is not null)
                {
                    hunks.Add(new(
                        path,
                        "TargetFramework",
                        $"TargetFramework: {BoundValue(consumer?.Value)}",
                        $"Select a reviewed target compatible with {BoundValue(dependency?.Value)}.",
                        DotNetRepairDiffReadiness.RequiresInspection,
                        "Framework alignment can change runtime support and must be an explicit product decision."));
                }

                break;
            }
            case DotNetFindingCategory.PackageVersionConflict:
            {
                foreach (var step in finding.RootCauseChain
                             .Where(step => step.Kind == DotNetFindingEvidenceKind.PackageReference)
                             .Take(MaxDiffHunks))
                {
                    var path = SafeRelativePath(step.RelativePath);
                    if (path is null)
                    {
                        continue;
                    }

                    var package = step.Label.Replace(
                        "Direct package reference to ",
                        "",
                        StringComparison.OrdinalIgnoreCase);
                    hunks.Add(new(
                        path,
                        $"PackageReference {package}",
                        $"Version: {BoundValue(step.Value)}",
                        "Use one reviewed compatible version across this dependency closure.",
                        DotNetRepairDiffReadiness.RequiresInspection,
                        "The highest textual version is not necessarily API-compatible or intended."));
                }

                break;
            }
            case DotNetFindingCategory.PackageDowngrade:
            {
                var step = finding.RootCauseChain.LastOrDefault(item =>
                    item.Kind == DotNetFindingEvidenceKind.PackageReference);
                var path = SafeRelativePath(step?.RelativePath ?? finding.PrimaryRelativePath);
                if (path is not null)
                {
                    hunks.Add(new(
                        path,
                        "PackageReference downgrade",
                        $"Resolved transition: {BoundValue(step?.Value)}",
                        "Add or align a reviewed direct PackageReference that prevents the proven downgrade.",
                        DotNetRepairDiffReadiness.RequiresInspection,
                        "NuGet resolution proves the downgrade but not which dependency version the product should adopt."));
                }

                break;
            }
            case DotNetFindingCategory.TestDiscoveryFailure:
            {
                var path = SafeRelativePath(finding.PrimaryRelativePath);
                if (path is not null)
                {
                    hunks.Add(new(
                        path,
                        "Test discovery configuration",
                        "Typed test target reports no discoverable tests.",
                        "Register the intended test SDK/adapter or restore the intended discoverable test contract.",
                        DotNetRepairDiffReadiness.RequiresInspection,
                        "No-test output does not prove whether the project, adapter, or intended test set is wrong."));
                }

                break;
            }
        }

        return hunks.Take(MaxDiffHunks).ToArray();
    }

    private static DotNetRepairVerificationPlan BuildVerificationPlan(
        DotNetWorkspaceSnapshot snapshot,
        RepairSource source,
        IReadOnlyList<string> affectedPaths,
        DotNetCommandResult? result,
        DotNetRepairImpactHint? impactHint)
    {
        var projects = snapshot.Projects.ToDictionary(
            project => NormalizePath(project.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var affectedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in affectedPaths
                     .Concat(source.ProjectPaths)
                     .Concat(result?.Command.TargetKind == DotNetCommandTargetKind.Project
                         ? [result.Command.TargetRelativePath]
                         : []))
        {
            var normalized = NormalizePath(path);
            if (projects.ContainsKey(normalized))
            {
                affectedProjects.Add(normalized);
            }
        }

        var hintedTests = (impactHint?.LikelyTestProjectRelativePaths ?? [])
            .Select(NormalizePath)
            .Where(projects.ContainsKey)
            .Where(path => projects[path].TestKind != DotNetProjectTestKind.None)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testProjects = snapshot.Projects
            .Where(project => project.TestKind != DotNetProjectTestKind.None)
            .Where(project =>
                affectedProjects.Contains(NormalizePath(project.RelativePath))
                || hintedTests.Contains(NormalizePath(project.RelativePath))
                || ProjectClosure(project, projects).Overlaps(affectedProjects))
            .Select(project => NormalizePath(project.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builds = new List<DotNetCommandPlan>();
        var tests = new List<DotNetCommandPlan>();
        foreach (var path in affectedProjects.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            AddPlan(
                snapshot.CommandPlans.FirstOrDefault(plan =>
                    plan.Kind == DotNetCommandKind.Build
                    && plan.TargetKind == DotNetCommandTargetKind.Project
                    && NormalizePath(plan.TargetRelativePath).Equals(path, StringComparison.OrdinalIgnoreCase)),
                builds,
                selected);
        }

        foreach (var testPath in testProjects)
        {
            AddPlan(
                snapshot.CommandPlans.FirstOrDefault(plan =>
                    plan.Kind == DotNetCommandKind.Build
                    && plan.TargetKind == DotNetCommandTargetKind.Project
                    && NormalizePath(plan.TargetRelativePath).Equals(testPath, StringComparison.OrdinalIgnoreCase)),
                builds,
                selected);
            AddPlan(
                snapshot.CommandPlans.FirstOrDefault(plan =>
                    plan.TargetKind == DotNetCommandTargetKind.Project
                    && NormalizePath(plan.TargetRelativePath).Equals(testPath, StringComparison.OrdinalIgnoreCase)
                    && ((plan.Kind == DotNetCommandKind.Test
                            && projects[testPath].TestKind == DotNetProjectTestKind.Conventional)
                        || (plan.Kind == DotNetCommandKind.Run
                            && projects[testPath].TestKind == DotNetProjectTestKind.ExecutableHarness))),
                tests,
                selected);
        }

        var wasCapped = builds.Count + tests.Count > MaxVerificationPlans;
        var availableSlots = MaxVerificationPlans;
        var boundedBuilds = builds.Take(availableSlots).ToArray();
        availableSlots -= boundedBuilds.Length;
        var boundedTests = tests.Take(availableSlots).ToArray();
        var partial = snapshot.IsPartial
            || snapshot.ScanLimitReached
            || snapshot.FindingsLimitReached
            || impactHint?.IsPartial == true
            || wasCapped
            || affectedProjects.Any(path => projects[path].IsPartial)
            || affectedProjects.Count == 0
            || affectedProjects.Any(path => !boundedBuilds.Any(plan =>
                NormalizePath(plan.TargetRelativePath).Equals(path, StringComparison.OrdinalIgnoreCase)));
        var testEvidenceState = testProjects.Length == 0
            ? DotNetRepairTestEvidenceState.Unavailable
            : partial || boundedTests.Length < testProjects.Length
                ? DotNetRepairTestEvidenceState.Partial
                : DotNetRepairTestEvidenceState.Available;
        var basis = testEvidenceState switch
        {
            DotNetRepairTestEvidenceState.Available =>
                "Selected typed test projects that are affected directly, reference an affected project, or were supplied by bounded impact evidence.",
            DotNetRepairTestEvidenceState.Partial =>
                "Some affected-test relationships or typed commands are unavailable; the listed test selection is incomplete.",
            _ =>
                "No typed test project can be proven from project references or supplied impact evidence; focused test evidence is unavailable."
        };
        return new(boundedBuilds, boundedTests, testEvidenceState, basis, partial);
    }

    private static HashSet<string> ProjectClosure(
        DotNetProjectInfo project,
        IReadOnlyDictionary<string, DotNetProjectInfo> projects)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var reference in project.ProjectReferenceRelativePaths.Select(NormalizePath))
        {
            if (projects.ContainsKey(reference) && closure.Add(reference))
            {
                queue.Enqueue(reference);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var reference in projects[current].ProjectReferenceRelativePaths.Select(NormalizePath))
            {
                if (projects.ContainsKey(reference) && closure.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return closure;
    }

    private static void AddPlan(
        DotNetCommandPlan? plan,
        ICollection<DotNetCommandPlan> destination,
        ISet<string> selected)
    {
        if (plan is null
            || plan.Kind == DotNetCommandKind.Restore
            || plan.NetworkRisk != DotNetNetworkRisk.None
            || !plan.RequiresUserApproval
            || !selected.Add(plan.Id))
        {
            return;
        }

        destination.Add(plan);
    }

    private static string Explain(RepairSource source)
    {
        if (source.Diagnostic is { } diagnostic)
        {
            var path = SafeRelativePath(diagnostic.RelativePath)
                ?? SafeRelativePath(diagnostic.ProjectRelativePath);
            var location = path is null
                ? "the typed build target"
                : diagnostic.Line is null
                    ? path
                    : $"{path}:{diagnostic.Line.Value}";
            return $"{diagnostic.Code} is the first bounded compiler failure at {location}. "
                + "Doctor can prove the failure boundary, but the intended source semantics require inspection.";
        }

        var finding = source.Finding!;
        return finding.Category switch
        {
            DotNetFindingCategory.ProjectReferenceCycle =>
                $"{finding.Code} proves a closed ProjectReference path. Breaking it requires an ownership decision about which dependency direction is wrong.",
            DotNetFindingCategory.TargetFrameworkIncompatibility =>
                $"{finding.Code} proves that the consumer cannot select a compatible target from the referenced project. Framework support must be reviewed before alignment.",
            DotNetFindingCategory.PackageVersionConflict =>
                $"{finding.Code} proves multiple direct literal package versions inside one directed dependency closure. Compatibility, not textual version order, must choose the repair.",
            DotNetFindingCategory.PackageDowngrade =>
                $"{finding.Code} is grounded in NU1605 resolution evidence. A direct pin or dependency alignment must be reviewed before changing package intent.",
            DotNetFindingCategory.TestDiscoveryFailure =>
                $"{finding.Code} proves that the typed test target discovered no tests. It does not by itself prove whether the project, adapter, or intended test contract is wrong.",
            _ => $"{finding.Code} is supported by bounded Solution Doctor evidence."
        };
    }

    private static DotNetRepairFindingEvidence FindingEvidence(DotNetDoctorFinding finding)
    {
        var paths = new[] { finding.PrimaryRelativePath }
            .OfType<string>()
            .Concat(finding.RelatedProjectRelativePaths)
            .Concat(finding.RootCauseChain.SelectMany(step =>
                new[] { step.RelativePath, step.RelatedRelativePath }.OfType<string>()))
            .Select(SafeRelativePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAffectedPaths)
            .ToArray();
        return new(finding.Id, finding.Code, finding.Severity, paths);
    }

    private static DotNetRepairFindingEvidence DiagnosticEvidence(DotNetBuildDiagnostic diagnostic)
    {
        var paths = new[] { diagnostic.RelativePath, diagnostic.ProjectRelativePath }
            .OfType<string>()
            .Select(SafeRelativePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var identity = $"{diagnostic.Code}|{string.Join("|", paths)}";
        return new(
            StableId("diagnostic", identity),
            diagnostic.Code,
            diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error
                ? DotNetWorkspaceDiagnosticSeverity.Error
                : DotNetWorkspaceDiagnosticSeverity.Warning,
            paths);
    }

    private static void AddFinding(
        IDictionary<string, DotNetRepairFindingEvidence> findings,
        DotNetRepairFindingEvidence candidate)
    {
        if (!findings.TryGetValue(candidate.Id, out var existing))
        {
            findings[candidate.Id] = candidate;
            return;
        }

        findings[candidate.Id] = existing with
        {
            Severity = SeverityRank(candidate.Severity) > SeverityRank(existing.Severity)
                ? candidate.Severity
                : existing.Severity,
            RelativePaths = existing.RelativePaths
                .Concat(candidate.RelativePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxAffectedPaths)
                .ToArray()
        };
    }

    private static string EvidenceFingerprint(
        DotNetWorkspaceSnapshot? snapshot,
        DotNetCommandResult? result,
        IReadOnlyList<DotNetRepairFindingEvidence> findings)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot?.WorkspaceName ?? "unavailable")
            .Append('|')
            .Append(snapshot?.IsPartial == true ? '1' : '0')
            .Append('|')
            .Append(snapshot?.ScanLimitReached == true ? '1' : '0');
        if (snapshot is not null)
        {
            foreach (var project in snapshot.Projects
                         .OrderBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("\nP|")
                    .Append(NormalizePath(project.RelativePath))
                    .Append('|')
                    .Append(project.IsPartial ? '1' : '0')
                    .Append('|')
                    .Append(string.Join(';', project.TargetFrameworks.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))
                    .Append('|')
                    .Append(string.Join(';', project.ProjectReferenceRelativePaths
                        .Select(NormalizePath)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
            }
        }

        if (result is not null)
        {
            builder.Append("\nR|")
                .Append(result.Command.Id)
                .Append('|')
                .Append(result.Succeeded ? '1' : '0')
                .Append('|')
                .Append(result.WasCancelled ? '1' : '0');
            foreach (var diagnostic in result.Diagnostics
                         .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Line)
                         .ThenBy(item => item.Column)
                         .ThenBy(item => item.Code, StringComparer.Ordinal))
            {
                builder.Append("\nD|")
                    .Append(diagnostic.Code)
                    .Append('|')
                    .Append(SafeRelativePath(diagnostic.RelativePath) ?? "")
                    .Append('|')
                    .Append(diagnostic.Line)
                    .Append('|')
                    .Append(diagnostic.Column);
            }
        }

        foreach (var finding in findings)
        {
            builder.Append("\nF|")
                .Append(finding.Id)
                .Append('|')
                .Append((int)finding.Severity)
                .Append('|')
                .Append(string.Join(';', finding.RelativePaths));
        }

        return HashToken(builder.ToString(), 16);
    }

    private static DotNetRepairVerificationPlan EmptyVerificationPlan(string reason)
    {
        return new(
            [],
            [],
            DotNetRepairTestEvidenceState.Unavailable,
            reason,
            IsPartial: true);
    }

    private static int SeverityRank(DotNetWorkspaceDiagnosticSeverity severity)
    {
        return severity switch
        {
            DotNetWorkspaceDiagnosticSeverity.Error => 2,
            DotNetWorkspaceDiagnosticSeverity.Warning => 1,
            _ => 0
        };
    }

    private static string RepairId(string sourceId)
    {
        return StableId("repair", sourceId);
    }

    private static string StableId(string kind, string identity)
    {
        return $"doctor:{kind}:{HashToken($"{kind}|{identity.ToLowerInvariant()}", 8)}";
    }

    private static string HashToken(string value, int bytes)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, bytes)).ToLowerInvariant();
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/').Trim();
    }

    private static string? SafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in NormalizePath(value)
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string BoundValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not available";
        }

        var trimmed = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 120 ? trimmed : $"{trimmed[..117]}...";
    }

    private sealed record RepairSource(
        string Id,
        string Code,
        DotNetDoctorFinding? Finding,
        DotNetBuildDiagnostic? Diagnostic,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> ProjectPaths)
    {
        internal static RepairSource FromFinding(DotNetDoctorFinding finding)
        {
            var paths = new[] { finding.PrimaryRelativePath }
                .OfType<string>()
                .Concat(finding.RelatedProjectRelativePaths)
                .Concat(finding.RootCauseChain.SelectMany(step =>
                    new[] { step.RelativePath, step.RelatedRelativePath }.OfType<string>()))
                .Select(SafeRelativePath)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(
                finding.Id,
                finding.Code,
                finding,
                null,
                paths,
                paths.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        internal static RepairSource FromDiagnostic(DotNetBuildDiagnostic diagnostic)
        {
            var paths = new[] { diagnostic.RelativePath, diagnostic.ProjectRelativePath }
                .OfType<string>()
                .Select(SafeRelativePath)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var projectPaths = new[] { SafeRelativePath(diagnostic.ProjectRelativePath) }
                .OfType<string>()
                .ToArray();
            return new(
                StableId("diagnostic", $"{diagnostic.Code}|{string.Join("|", paths)}"),
                diagnostic.Code,
                null,
                diagnostic,
                paths,
                projectPaths);
        }
    }
}
