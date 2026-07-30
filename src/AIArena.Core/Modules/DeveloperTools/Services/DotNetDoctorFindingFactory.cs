using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

internal static partial class DotNetDoctorFindingFactory
{
    internal static (IReadOnlyList<DotNetDoctorFinding> Findings, bool LimitReached) CreateWorkspaceFindings(
        IReadOnlyList<DotNetProjectInfo> projects,
        int maximumFindings)
    {
        ArgumentNullException.ThrowIfNull(projects);
        maximumFindings = Math.Clamp(maximumFindings, 1, 10_000);

        var evaluatedProjects = projects
            .Where(project => !project.IsPartial)
            .ToArray();
        var findings = FindProjectReferenceCycles(evaluatedProjects)
            .Concat(FindTargetFrameworkIncompatibilities(evaluatedProjects))
            .Concat(FindPackageVersionConflicts(evaluatedProjects))
            .OrderBy(finding => finding.Category)
            .ThenBy(finding => finding.PrimaryRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
        return (findings.Take(maximumFindings).ToArray(), findings.Length > maximumFindings);
    }

    internal static IReadOnlyList<DotNetDoctorFinding> CreateCommandFindings(
        DotNetCommandPlan command,
        IReadOnlyList<DotNetBuildDiagnostic> diagnostics,
        bool testDiscoveryFailed)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var findings = diagnostics
            .Where(diagnostic => diagnostic.Code.Equals("NU1605", StringComparison.OrdinalIgnoreCase))
            .OrderBy(diagnostic => diagnostic.ProjectRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .Select(diagnostic => CreatePackageDowngradeFinding(command, diagnostic))
            .ToList();
        if (testDiscoveryFailed)
        {
            findings.Add(CreateTestDiscoveryFinding(command));
        }

        return findings
            .DistinctBy(finding => finding.Id, StringComparer.Ordinal)
            .OrderBy(finding => finding.Category)
            .ThenBy(finding => finding.PrimaryRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<DotNetDoctorFinding> FindProjectReferenceCycles(
        IReadOnlyList<DotNetProjectInfo> projects)
    {
        var projectByPath = projects.ToDictionary(
            project => project.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var adjacency = projectByPath.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ProjectReferenceRelativePaths
                .Where(projectByPath.ContainsKey)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var component in StronglyConnectedComponents(adjacency)
                     .Where(component =>
                         component.Count > 1
                         || adjacency[component[0]].Contains(component[0], StringComparer.OrdinalIgnoreCase))
                     .OrderBy(component => component[0], StringComparer.OrdinalIgnoreCase))
        {
            var cycle = FindDeterministicCycle(component, adjacency);
            var relatedProjects = component
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var evidence = new List<DotNetFindingEvidenceStep>
            {
                new(
                    1,
                    DotNetFindingEvidenceKind.Project,
                    "Cycle starts at project",
                    cycle[0])
            };
            for (var index = 0; index + 1 < cycle.Count; index++)
            {
                evidence.Add(new(
                    evidence.Count + 1,
                    DotNetFindingEvidenceKind.ProjectReference,
                    "Project reference continues the cycle",
                    cycle[index],
                    cycle[index + 1]));
            }

            var summary = $"Project references form a cycle: {string.Join(" -> ", cycle)}.";
            yield return new(
                FindingId(DotNetFindingCategory.ProjectReferenceCycle, string.Join("|", relatedProjects)),
                "DND301",
                DotNetWorkspaceDiagnosticSeverity.Error,
                DotNetFindingCategory.ProjectReferenceCycle,
                DotNetFindingConfidence.High,
                "Circular project references",
                summary,
                cycle[0],
                relatedProjects,
                evidence);
        }
    }

    private static IEnumerable<DotNetDoctorFinding> FindTargetFrameworkIncompatibilities(
        IReadOnlyList<DotNetProjectInfo> projects)
    {
        var projectByPath = projects.ToDictionary(
            project => project.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.OrderBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var referencePath in project.ProjectReferenceRelativePaths
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!projectByPath.TryGetValue(referencePath, out var referencedProject))
                {
                    continue;
                }

                var incompatibleFrameworks = FrameworksWithoutCompatibleReference(
                    project.TargetFrameworks,
                    referencedProject.TargetFrameworks,
                    out var referencedFrameworks);
                if (incompatibleFrameworks.Count == 0)
                {
                    continue;
                }

                var consumerValue = string.Join(";", incompatibleFrameworks);
                var dependencyValue = string.Join(";", referencedFrameworks);
                var relatedProjects = new[] { project.RelativePath, referencedProject.RelativePath }
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var evidence = new DotNetFindingEvidenceStep[]
                {
                    new(
                        1,
                        DotNetFindingEvidenceKind.Project,
                        "Referencing project",
                        project.RelativePath),
                    new(
                        2,
                        DotNetFindingEvidenceKind.ProjectReference,
                        "Project reference",
                        project.RelativePath,
                        referencedProject.RelativePath),
                    new(
                        3,
                        DotNetFindingEvidenceKind.TargetFramework,
                        "Referencing target framework has no compatible target",
                        project.RelativePath,
                        Value: consumerValue),
                    new(
                        4,
                        DotNetFindingEvidenceKind.TargetFramework,
                        "Referenced project target frameworks",
                        referencedProject.RelativePath,
                        Value: dependencyValue)
                };
                yield return new(
                    FindingId(
                        DotNetFindingCategory.TargetFrameworkIncompatibility,
                        $"{NormalizeIdentity(project.RelativePath)}|{NormalizeIdentity(referencedProject.RelativePath)}"),
                    "DND302",
                    DotNetWorkspaceDiagnosticSeverity.Error,
                    DotNetFindingCategory.TargetFrameworkIncompatibility,
                    DotNetFindingConfidence.High,
                    "Incompatible project target frameworks",
                    $"{project.RelativePath} targets {consumerValue}, which cannot select a compatible target from {referencedProject.RelativePath} ({dependencyValue}).",
                    project.RelativePath,
                    relatedProjects,
                    evidence);
            }
        }
    }

    private static IEnumerable<DotNetDoctorFinding> FindPackageVersionConflicts(
        IReadOnlyList<DotNetProjectInfo> projects)
    {
        var projectByPath = projects.ToDictionary(
            project => project.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var findings = new List<DotNetDoctorFinding>();
        foreach (var rootProject in projects.OrderBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var closure = DirectedDependencyClosure(rootProject.RelativePath, projectByPath);
            var packageGroups = closure
                .Select(path => projectByPath[path])
                .SelectMany(project => project.PackageReferences.Select(package => new
                {
                    ProjectPath = project.RelativePath,
                    Package = package
                }))
                .GroupBy(item => item.Package.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var packageGroup in packageGroups)
            {
                var entries = packageGroup
                    .DistinctBy(
                        item => $"{NormalizeIdentity(item.ProjectPath)}\u001f{item.Package.Version.ToLowerInvariant()}",
                        StringComparer.Ordinal)
                    .OrderBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Package.Version, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var versions = entries
                    .Select(item => item.Package.Version)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (versions.Length < 2)
                {
                    continue;
                }

                var relatedProjects = entries
                    .Select(item => item.ProjectPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var evidence = entries
                    .Select((item, index) => new DotNetFindingEvidenceStep(
                        index + 1,
                        DotNetFindingEvidenceKind.PackageReference,
                        $"Direct package reference to {packageGroup.Key}",
                        item.ProjectPath,
                        Value: item.Package.Version))
                    .ToArray();
                findings.Add(new(
                    FindingId(DotNetFindingCategory.PackageVersionConflict, $"{packageGroup.Key}|{string.Join("|", relatedProjects)}"),
                    "DND303",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    DotNetFindingCategory.PackageVersionConflict,
                    DotNetFindingConfidence.Medium,
                    "Conflicting direct package versions",
                    $"{packageGroup.Key} has multiple unconditional literal versions in one directed project dependency closure: {string.Join(", ", versions)}.",
                    relatedProjects[0],
                    relatedProjects,
                    [
                        new(
                            1,
                            DotNetFindingEvidenceKind.Project,
                            "Dependency closure root that reaches each conflicting reference",
                            rootProject.RelativePath),
                        .. evidence.Select(step => step with { Sequence = step.Sequence + 1 })
                    ]));
            }
        }

        return findings
            .DistinctBy(finding => finding.Id, StringComparer.Ordinal)
            .OrderBy(finding => finding.PrimaryRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static DotNetDoctorFinding CreatePackageDowngradeFinding(
        DotNetCommandPlan command,
        DotNetBuildDiagnostic diagnostic)
    {
        var match = PackageDowngradeRegex().Match(diagnostic.Message);
        var packageName = match.Success ? match.Groups["package"].Value : "package";
        var versionTransition = match.Success
            ? $"{match.Groups["from"].Value} -> {match.Groups["to"].Value}"
            : null;
        var commandTargetPath = SafeRelativeModelPath(command.TargetRelativePath);
        var primaryPath = SafeRelativeModelPath(diagnostic.ProjectRelativePath)
            ?? (command.TargetKind == DotNetCommandTargetKind.Project ? commandTargetPath : null);
        var relatedProjects = new[] { primaryPath }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidence = new List<DotNetFindingEvidenceStep>
        {
            new(
                1,
                DotNetFindingEvidenceKind.Command,
                $"{command.Kind} command",
                commandTargetPath),
            new(
                2,
                DotNetFindingEvidenceKind.Diagnostic,
                "NuGet downgrade diagnostic",
                SafeRelativeModelPath(diagnostic.RelativePath)
                    ?? SafeRelativeModelPath(diagnostic.ProjectRelativePath),
                Value: diagnostic.Code)
        };
        if (match.Success)
        {
            evidence.Add(new(
                3,
                DotNetFindingEvidenceKind.PackageReference,
                $"Resolved package downgrade for {packageName}",
                primaryPath,
                Value: versionTransition));
        }

        return new(
            FindingId(
                DotNetFindingCategory.PackageDowngrade,
                $"{NormalizeIdentity(commandTargetPath ?? command.Id)}|{NormalizeIdentity(primaryPath ?? "unknown-project")}|{packageName.ToLowerInvariant()}"),
            "DND401",
            diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error
                ? DotNetWorkspaceDiagnosticSeverity.Error
                : DotNetWorkspaceDiagnosticSeverity.Warning,
            DotNetFindingCategory.PackageDowngrade,
            DotNetFindingConfidence.High,
            "NuGet package downgrade",
            match.Success
                ? $"NuGet resolved {packageName} from {match.Groups["from"].Value} down to {match.Groups["to"].Value}."
                : "NuGet reported a package downgrade.",
            primaryPath,
            relatedProjects,
            evidence);
    }

    private static DotNetDoctorFinding CreateTestDiscoveryFinding(DotNetCommandPlan command)
    {
        var targetPath = SafeRelativeModelPath(command.TargetRelativePath);
        var relatedProjects = command.TargetKind == DotNetCommandTargetKind.Project
            && targetPath is not null
            ? new[] { targetPath }
            : [];
        return new(
            FindingId(
                DotNetFindingCategory.TestDiscoveryFailure,
                NormalizeIdentity(targetPath ?? command.Id)),
            "DND402",
            DotNetWorkspaceDiagnosticSeverity.Error,
            DotNetFindingCategory.TestDiscoveryFailure,
            DotNetFindingConfidence.High,
            "Test discovery failed",
            targetPath is null
                ? "The test runner reported that no tests were available for the typed target."
                : $"The test runner reported that no tests were available for {targetPath}.",
            targetPath,
            relatedProjects,
            [
                new(
                    1,
                    DotNetFindingEvidenceKind.Command,
                    "Test command",
                    targetPath),
                new(
                    2,
                    DotNetFindingEvidenceKind.TestRunner,
                    "Test runner reported no discoverable tests",
                    targetPath)
            ]);
    }

    private static IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents(
        IReadOnlyDictionary<string, IReadOnlyList<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finishingOrder = new List<string>(adjacency.Count);
        foreach (var start in adjacency.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var traversal = new Stack<(string Node, int NextNeighbor)>();
            traversal.Push((start, 0));
            while (traversal.Count > 0)
            {
                var (node, nextNeighbor) = traversal.Pop();
                if (nextNeighbor >= adjacency[node].Count)
                {
                    finishingOrder.Add(node);
                    continue;
                }

                traversal.Push((node, nextNeighbor + 1));
                var neighbor = adjacency[node][nextNeighbor];
                if (visited.Add(neighbor))
                {
                    traversal.Push((neighbor, 0));
                }
            }
        }

        var reverseAdjacency = adjacency.Keys.ToDictionary(
            path => path,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (source, targets) in adjacency)
        {
            foreach (var target in targets)
            {
                reverseAdjacency[target].Add(source);
            }
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<IReadOnlyList<string>>();
        for (var index = finishingOrder.Count - 1; index >= 0; index--)
        {
            var start = finishingOrder[index];
            if (!assigned.Add(start))
            {
                continue;
            }

            var component = new List<string>();
            var traversal = new Stack<string>();
            traversal.Push(start);
            while (traversal.Count > 0)
            {
                var node = traversal.Pop();
                component.Add(node);
                foreach (var neighbor in reverseAdjacency[node]
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (assigned.Add(neighbor))
                    {
                        traversal.Push(neighbor);
                    }
                }
            }

            components.Add(component
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        return components;
    }

    private static IReadOnlyList<string> DirectedDependencyClosure(
        string rootProjectPath,
        IReadOnlyDictionary<string, DotNetProjectInfo> projectByPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        if (projectByPath.ContainsKey(rootProjectPath))
        {
            visited.Add(rootProjectPath);
            queue.Enqueue(rootProjectPath);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependency in projectByPath[current].ProjectReferenceRelativePaths
                         .Where(projectByPath.ContainsKey)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (visited.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        return visited
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> FindDeterministicCycle(
        IReadOnlyList<string> component,
        IReadOnlyDictionary<string, IReadOnlyList<string>> adjacency)
    {
        var members = component.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var start = component.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
        if (adjacency[start].Contains(start, StringComparer.OrdinalIgnoreCase))
        {
            return [start, start];
        }

        foreach (var firstNeighbor in adjacency[start].Where(members.Contains))
        {
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { firstNeighbor };
            var predecessor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(firstNeighbor);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbor in adjacency[current].Where(members.Contains))
                {
                    if (neighbor.Equals(start, StringComparison.OrdinalIgnoreCase))
                    {
                        var tail = new List<string> { current };
                        while (predecessor.TryGetValue(current, out var previous))
                        {
                            current = previous;
                            tail.Add(current);
                        }

                        tail.Reverse();
                        return [start, .. tail, start];
                    }

                    if (visited.Add(neighbor))
                    {
                        predecessor[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        throw new InvalidDataException("A strongly connected project-reference component did not contain a cycle.");
    }

    private static IReadOnlyList<string> FrameworksWithoutCompatibleReference(
        IReadOnlyList<string> consumerFrameworks,
        IReadOnlyList<string> dependencyFrameworks,
        out IReadOnlyList<string> normalizedDependencyFrameworks)
    {
        normalizedDependencyFrameworks = [];
        var dependencies = dependencyFrameworks
            .Select(value => TryParseFramework(value, out var framework) ? framework : null)
            .ToArray();
        if (dependencies.Length == 0 || dependencies.Any(framework => framework is null))
        {
            return [];
        }

        var consumers = consumerFrameworks
            .Select(value => TryParseFramework(value, out var framework) ? framework : null)
            .ToArray();
        if (consumers.Length == 0 || consumers.Any(framework => framework is null))
        {
            return [];
        }

        normalizedDependencyFrameworks = dependencies
            .Select(framework => framework!.Normalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incompatible = new List<string>();
        foreach (var consumer in consumers
                     .Select(framework => framework!)
                     .OrderBy(framework => framework.Normalized, StringComparer.OrdinalIgnoreCase))
        {
            var compatibility = dependencies
                .Select(dependency => CompareFrameworks(consumer, dependency!))
                .ToArray();
            if (compatibility.All(result => result == FrameworkCompatibility.Incompatible))
            {
                incompatible.Add(consumer.Normalized);
            }
        }

        return incompatible
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static FrameworkCompatibility CompareFrameworks(
        ParsedFramework consumer,
        ParsedFramework dependency)
    {
        var baseCompatibility = (consumer.Kind, dependency.Kind) switch
        {
            var pair when pair.Item1 == pair.Item2 =>
                consumer.Version >= dependency.Version
                    ? FrameworkCompatibility.Compatible
                    : FrameworkCompatibility.Incompatible,
            (FrameworkKind.ModernNet, FrameworkKind.NetStandard) =>
                dependency.Version <= new Version(2, 1)
                    ? FrameworkCompatibility.Compatible
                    : FrameworkCompatibility.Incompatible,
            (FrameworkKind.NetCoreApp, FrameworkKind.NetStandard) =>
                dependency.Version <= MaximumNetStandardForNetCoreApp(consumer.Version)
                    ? FrameworkCompatibility.Compatible
                    : FrameworkCompatibility.Incompatible,
            _ => FrameworkCompatibility.Unknown
        };
        if (baseCompatibility != FrameworkCompatibility.Compatible
            || string.IsNullOrWhiteSpace(dependency.Platform))
        {
            return baseCompatibility;
        }

        if (string.IsNullOrWhiteSpace(consumer.Platform))
        {
            return FrameworkCompatibility.Incompatible;
        }

        if (!consumer.Platform.Equals(dependency.Platform, StringComparison.OrdinalIgnoreCase))
        {
            return FrameworkCompatibility.Incompatible;
        }

        if (dependency.PlatformMinimumVersion is null)
        {
            return FrameworkCompatibility.Compatible;
        }

        if (consumer.PlatformMinimumVersion is null)
        {
            return FrameworkCompatibility.Unknown;
        }

        return consumer.PlatformMinimumVersion >= dependency.PlatformMinimumVersion
            ? FrameworkCompatibility.Compatible
            : FrameworkCompatibility.Incompatible;
    }

    private static bool TryParseFramework(string value, out ParsedFramework? framework)
    {
        framework = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
        {
            return false;
        }

        var match = SupportedTargetFrameworkRegex().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var family = match.Groups["family"].Value.ToLowerInvariant();
        var kind = family switch
        {
            "netstandard" => FrameworkKind.NetStandard,
            "netcoreapp" => FrameworkKind.NetCoreApp,
            "net" => FrameworkKind.ModernNet,
            _ => default
        };
        if (!Version.TryParse(match.Groups["version"].Value, out var version)
            || (kind == FrameworkKind.ModernNet && version.Major < 5))
        {
            return false;
        }

        var platform = match.Groups["platform"].Success
            ? match.Groups["platform"].Value.ToLowerInvariant()
            : null;
        if (platform is not null
            && (kind != FrameworkKind.ModernNet || !IsSupportedPlatform(platform)))
        {
            return false;
        }

        Version? platformMinimumVersion = null;
        if (match.Groups["platformVersion"].Success
            && !Version.TryParse(match.Groups["platformVersion"].Value, out platformMinimumVersion))
        {
            return false;
        }

        var normalized = $"{family}{version.Major}.{version.Minor}";
        if (platform is not null)
        {
            normalized += $"-{platform}";
            if (platformMinimumVersion is not null)
            {
                normalized += platformMinimumVersion.ToString(
                    platformMinimumVersion.Revision >= 0
                        ? 4
                        : platformMinimumVersion.Build >= 0 ? 3 : 2);
            }
        }

        framework = new(
            kind,
            version,
            platform,
            platformMinimumVersion,
            normalized);
        return true;
    }

    private static bool IsSupportedPlatform(string platform)
    {
        return platform is
            "android"
            or "browser"
            or "ios"
            or "maccatalyst"
            or "macos"
            or "tizen"
            or "tvos"
            or "windows";
    }

    private static Version MaximumNetStandardForNetCoreApp(Version consumerVersion)
    {
        if (consumerVersion.Major <= 1)
        {
            return new Version(1, 6);
        }

        return consumerVersion.Major == 2
            ? new Version(2, 0)
            : new Version(2, 1);
    }

    private static string FindingId(DotNetFindingCategory category, string identity)
    {
        var normalized = $"{category}:{NormalizeIdentity(identity)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"doctor:{CategoryToken(category)}:{Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string NormalizeIdentity(string value)
    {
        return value.Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static string? SafeRelativeModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in value.Replace('\\', '/')
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

        return segments.Count == 0 ? "." : string.Join('/', segments);
    }

    private static string CategoryToken(DotNetFindingCategory category)
    {
        return category switch
        {
            DotNetFindingCategory.ProjectReferenceCycle => "project-cycle",
            DotNetFindingCategory.TargetFrameworkIncompatibility => "target-framework",
            DotNetFindingCategory.PackageVersionConflict => "package-conflict",
            DotNetFindingCategory.PackageDowngrade => "package-downgrade",
            DotNetFindingCategory.TestDiscoveryFailure => "test-discovery",
            _ => "finding"
        };
    }

    private enum FrameworkKind
    {
        ModernNet,
        NetCoreApp,
        NetStandard
    }

    private enum FrameworkCompatibility
    {
        Unknown,
        Compatible,
        Incompatible
    }

    private sealed record ParsedFramework(
        FrameworkKind Kind,
        Version Version,
        string? Platform,
        Version? PlatformMinimumVersion,
        string Normalized);

    [GeneratedRegex(
        """^(?<family>netstandard|netcoreapp|net)(?<version>[0-9]{1,2}\.[0-9]{1,2})(?:-(?<platform>[a-z]+)(?<platformVersion>[0-9]{1,5}\.[0-9]{1,5}(?:\.[0-9]{1,5}){0,2})?)?$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedTargetFrameworkRegex();

    [GeneratedRegex(
        """Detected package downgrade:\s*(?<package>[A-Za-z0-9_.-]+)\s+from\s+(?<from>[A-Za-z0-9_.+-]+)\s+to\s+(?<to>[A-Za-z0-9_.+-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageDowngradeRegex();
}
