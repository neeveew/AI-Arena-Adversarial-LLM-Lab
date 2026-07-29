using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed partial class DotNetWorkspaceIntelligenceService
{
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "artifacts",
        "TestResults", "coverage", "dist"
    };

    public async Task<DotNetWorkspaceSnapshot> DiscoverAsync(
        string workspaceRoot,
        DotNetDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The workspace directory does not exist: {workspaceRoot}");
        }

        options = NormalizeOptions(options ?? new DotNetDiscoveryOptions());
        await Task.Yield();

        var diagnostics = new List<DotNetWorkspaceDiagnostic>();
        var candidateProjects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateSolutions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanLimitReached = ScanCandidates(
            root,
            options,
            candidateProjects,
            candidateSolutions,
            diagnostics,
            cancellationToken);

        var projects = new List<DotNetProjectInfo>();
        foreach (var projectRelativePath in candidateProjects.Take(options.MaxProjects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(ParseProject(root, projectRelativePath, options, diagnostics));
        }

        if (candidateProjects.Count > options.MaxProjects)
        {
            scanLimitReached = true;
            AddDiagnostic(
                diagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW002",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    $"Project discovery stopped at the configured limit of {options.MaxProjects.ToString(CultureInfo.InvariantCulture)}."));
        }

        ValidateIndexedProjectReferences(projects, options.MaxDiagnostics, diagnostics);
        var knownProjects = projects
            .Select(project => project.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var solutions = new List<DotNetSolutionInfo>();
        foreach (var solutionRelativePath in candidateSolutions.Take(options.MaxSolutions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            solutions.Add(ParseSolution(root, solutionRelativePath, knownProjects, options, diagnostics));
        }

        if (candidateSolutions.Count > options.MaxSolutions)
        {
            scanLimitReached = true;
            AddDiagnostic(
                diagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW003",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    $"Solution discovery stopped at the configured limit of {options.MaxSolutions.ToString(CultureInfo.InvariantCulture)}."));
        }

        projects.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        solutions.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        var (plans, commandPlansTruncated) = CreateCommandPlans(solutions, projects, options.MaxCommandPlans);
        if (commandPlansTruncated)
        {
            AddDiagnostic(
                diagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW009",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    $"Command planning stopped at the configured limit of {options.MaxCommandPlans.ToString(CultureInfo.InvariantCulture)}."));
        }

        var isPartial = scanLimitReached
            || commandPlansTruncated
            || diagnostics.Any(diagnostic =>
                diagnostic.Code != "DNW114"
                && diagnostic.Severity is DotNetWorkspaceDiagnosticSeverity.Warning or DotNetWorkspaceDiagnosticSeverity.Error)
            || projects.Any(project => project.IsPartial)
            || solutions.Any(solution => solution.IsPartial);

        return new(
            Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            solutions,
            projects,
            plans,
            diagnostics,
            isPartial,
            scanLimitReached);
    }

    public IReadOnlyList<DotNetCommandPlan> CreateCommandPlans(DotNetWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return CreateCommandPlans(
            snapshot.Solutions,
            snapshot.Projects,
            new DotNetDiscoveryOptions().MaxCommandPlans).Plans;
    }

    public DotNetNarrowedRetryPlan? CreateNarrowedRetryPlan(
        DotNetWorkspaceSnapshot snapshot,
        DotNetCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);

        if (result.WasCancelled)
        {
            return new(result.Command, "The prior command was cancelled; retry the same bounded action after operator review.", false);
        }

        if (result.Command.Kind == DotNetCommandKind.Test && result.FailingTests.Count > 0)
        {
            var testName = result.FailingTests[0].Name;
            if (testName.Length <= 512 && SafeFullyQualifiedTestNameRegex().IsMatch(testName))
            {
                var arguments = RemoveTestFilter(result.Command.Arguments);
                arguments.Add("--filter");
                arguments.Add($"FullyQualifiedName={testName}");
                var command = RebuildPlan(
                    result.Command,
                    arguments,
                    $"{result.Command.Description} (focused retry: {testName})",
                    "focused-test");
                return new(command, $"Retry only the first failing test, {testName}.", true);
            }
        }

        var projectRelativePath = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ProjectRelativePath ?? FindOwningProject(snapshot, diagnostic.RelativePath))
            .FirstOrDefault(path => path is not null);
        if (projectRelativePath is not null)
        {
            var project = snapshot.Projects.FirstOrDefault(candidate =>
                candidate.RelativePath.Equals(projectRelativePath, StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                var retry = CreatePlan(
                    DotNetCommandKind.Build,
                    DotNetCommandTargetKind.Project,
                    project.RelativePath,
                    ["build", project.RelativePath, "--no-restore"],
                    $"Build {project.Name} without restoring packages.",
                    requiresSeparateApproval: false,
                    DotNetNetworkRisk.None);
                return new(retry, $"Narrow the retry to the project that emitted the first structured error: {project.RelativePath}.", true);
            }
        }

        return null;
    }

    private static DotNetDiscoveryOptions NormalizeOptions(DotNetDiscoveryOptions options)
    {
        return options with
        {
            MaxDirectories = Math.Clamp(options.MaxDirectories, 1, 100_000),
            MaxFiles = Math.Clamp(options.MaxFiles, 1, 1_000_000),
            MaxProjects = Math.Clamp(options.MaxProjects, 1, 10_000),
            MaxSolutions = Math.Clamp(options.MaxSolutions, 1, 1_000),
            MaxDepth = Math.Clamp(options.MaxDepth, 0, 64),
            MaxProjectFileBytes = Math.Clamp(options.MaxProjectFileBytes, 4 * 1024, 32L * 1024 * 1024),
            MaxDiagnostics = Math.Clamp(options.MaxDiagnostics, 1, 10_000),
            MaxCommandPlans = Math.Clamp(options.MaxCommandPlans, 1, 50_000)
        };
    }

    private static bool ScanCandidates(
        string root,
        DotNetDiscoveryOptions options,
        ISet<string> projects,
        ISet<string> solutions,
        ICollection<DotNetWorkspaceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var directoryCount = 0;
        var fileCount = 0;
        var limitReached = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            if (++directoryCount > options.MaxDirectories)
            {
                limitReached = true;
                AddDiagnostic(
                    diagnostics,
                    options.MaxDiagnostics,
                    new("DNW004", DotNetWorkspaceDiagnosticSeverity.Warning, "Directory scan limit reached."));
                break;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++fileCount > options.MaxFiles)
                    {
                        limitReached = true;
                        AddDiagnostic(
                            diagnostics,
                            options.MaxDiagnostics,
                            new("DNW005", DotNetWorkspaceDiagnosticSeverity.Warning, "File scan limit reached."));
                        return true;
                    }

                    var extension = Path.GetExtension(file);
                    if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryGetSafeRelativePath(root, file, out var relativePath))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!diagnostics.Any(diagnostic => diagnostic.Code == "DNW008"))
                        {
                            AddDiagnostic(
                                diagnostics,
                                options.MaxDiagnostics,
                                new(
                                    "DNW008",
                                    DotNetWorkspaceDiagnosticSeverity.Warning,
                                    "Reparse-point files or directories were skipped to preserve the workspace boundary.",
                                    relativePath));
                        }

                        continue;
                    }

                    if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        projects.Add(relativePath);
                    }
                    else
                    {
                        solutions.Add(relativePath);
                    }
                }

                if (depth >= options.MaxDepth)
                {
                    if (Directory.EnumerateDirectories(directory)
                        .Any(child => !SkippedDirectoryNames.Contains(Path.GetFileName(child))))
                    {
                        limitReached = true;
                        if (!diagnostics.Any(diagnostic => diagnostic.Code == "DNW007"))
                        {
                            AddDiagnostic(
                                diagnostics,
                                options.MaxDiagnostics,
                                new("DNW007", DotNetWorkspaceDiagnosticSeverity.Warning, "Directory depth limit reached."));
                        }
                    }

                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SkippedDirectoryNames.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!diagnostics.Any(diagnostic => diagnostic.Code == "DNW008"))
                        {
                            AddDiagnostic(
                                diagnostics,
                                options.MaxDiagnostics,
                                new(
                                    "DNW008",
                                    DotNetWorkspaceDiagnosticSeverity.Warning,
                                    "Reparse-point files or directories were skipped to preserve the workspace boundary.",
                                    TryGetSafeRelativePath(root, child, out var relativeChild) ? relativeChild : null));
                        }

                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddDiagnostic(
                    diagnostics,
                    options.MaxDiagnostics,
                    new(
                        "DNW006",
                        DotNetWorkspaceDiagnosticSeverity.Warning,
                        $"A directory could not be inspected: {exception.GetType().Name}.",
                        TryGetSafeRelativePath(root, directory, out var relativeDirectory) ? relativeDirectory : null));
            }
        }

        return limitReached;
    }

    private static DotNetProjectInfo ParseProject(
        string root,
        string relativePath,
        DotNetDiscoveryOptions options,
        ICollection<DotNetWorkspaceDiagnostic> workspaceDiagnostics)
    {
        var diagnostics = new List<DotNetWorkspaceDiagnostic>();
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var frameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputType = DotNetProjectOutputType.Unknown;
        var useWpf = false;
        var conventionalTest = false;
        var partial = false;
        var restoreState = DotNetRestoreState.Unknown;

        try
        {
            var absolutePath = ResolveRelativePath(root, relativePath);
            if (PathContainsReparsePoint(root, absolutePath))
            {
                throw new InvalidDataException("The project path contains a reparse point.");
            }

            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > options.MaxProjectFileBytes)
            {
                throw new InvalidDataException("The project file exceeds the configured size limit.");
            }

            using var stream = File.OpenRead(absolutePath);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = options.MaxProjectFileBytes
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var properties = document
                .Descendants()
                .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
                .ToList();
            var projectDirectory = Path.GetDirectoryName(absolutePath) ?? root;
            var evaluationIsPartial = HasUnevaluatedBuildInputs(
                root,
                projectDirectory,
                document,
                properties);
            if (evaluationIsPartial)
            {
                partial = true;
                AddProjectDiagnostic(
                    diagnostics,
                    workspaceDiagnostics,
                    options.MaxDiagnostics,
                    new(
                        "DNW116",
                        DotNetWorkspaceDiagnosticSeverity.Warning,
                        "Conditional, imported, or externally supplied MSBuild values could not be evaluated; classifications are conservative.",
                        relativePath));
            }

            foreach (var value in PropertyValues(properties, "TargetFramework"))
            {
                AddFrameworks(frameworks, value);
            }

            foreach (var value in PropertyValues(properties, "TargetFrameworks"))
            {
                AddFrameworks(frameworks, value);
            }

            var outputValue = PropertyValues(properties, "OutputType").LastOrDefault();
            outputType = ParseOutputType(outputValue);
            if (outputType == DotNetProjectOutputType.Unknown
                && outputValue is null
                && !evaluationIsPartial
                && HasPlainMicrosoftNetSdk(document))
            {
                outputType = DotNetProjectOutputType.Library;
            }

            useWpf = PropertyValues(properties, "UseWPF")
                .Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase));
            conventionalTest = PropertyValues(properties, "IsTestProject")
                    .Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase))
                || document.Descendants()
                    .Where(element => element.Name.LocalName == "PackageReference")
                    .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
                    .Any(package => package?.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) == true)
                || HasConventionalTestSdk(document);

            if (evaluationIsPartial)
            {
                outputType = DotNetProjectOutputType.Unknown;
                useWpf = false;
                conventionalTest = false;
            }

            restoreState = evaluationIsPartial
                ? DotNetRestoreState.Unknown
                : InspectRestoreState(
                    root,
                    projectDirectory,
                    relativePath,
                    properties,
                    diagnostics,
                    workspaceDiagnostics,
                    options.MaxDiagnostics);
            foreach (var referenceElement in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = ((string?)referenceElement.Attribute("Include"))?.Trim();
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                if (HasCondition(referenceElement)
                    || include.Contains("$(", StringComparison.Ordinal)
                    || include.Contains("@(", StringComparison.Ordinal)
                    || include.Contains("%(", StringComparison.Ordinal))
                {
                    partial = true;
                    AddProjectDiagnostic(
                        diagnostics,
                        workspaceDiagnostics,
                        options.MaxDiagnostics,
                        new(
                            "DNW111",
                            DotNetWorkspaceDiagnosticSeverity.Warning,
                            "A conditional or unevaluated project reference was omitted.",
                            relativePath));
                    continue;
                }

                try
                {
                    var candidate = Path.GetFullPath(include, projectDirectory);
                    if (TryGetSafeRelativePath(root, candidate, out var referenceRelativePath))
                    {
                        if (!File.Exists(candidate))
                        {
                            partial = true;
                            AddProjectDiagnostic(
                                diagnostics,
                                workspaceDiagnostics,
                                options.MaxDiagnostics,
                                new(
                                    "DNW117",
                                    DotNetWorkspaceDiagnosticSeverity.Warning,
                                    "A referenced project does not exist and was omitted.",
                                    relativePath));
                        }
                        else if (PathContainsReparsePoint(root, candidate))
                        {
                            partial = true;
                            AddProjectDiagnostic(
                                diagnostics,
                                workspaceDiagnostics,
                                options.MaxDiagnostics,
                                new(
                                    "DNW118",
                                    DotNetWorkspaceDiagnosticSeverity.Warning,
                                    "A referenced project crosses a reparse point and was omitted.",
                                    relativePath));
                        }
                        else
                        {
                            references.Add(referenceRelativePath);
                        }
                    }
                    else
                    {
                        partial = true;
                        AddProjectDiagnostic(
                            diagnostics,
                            workspaceDiagnostics,
                            options.MaxDiagnostics,
                            new(
                                "DNW112",
                                DotNetWorkspaceDiagnosticSeverity.Warning,
                                "A project reference points outside the workspace and was omitted.",
                                relativePath));
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    partial = true;
                    AddProjectDiagnostic(
                        diagnostics,
                        workspaceDiagnostics,
                        options.MaxDiagnostics,
                        new(
                            "DNW112",
                            DotNetWorkspaceDiagnosticSeverity.Warning,
                            "A project reference path is invalid and was omitted.",
                            relativePath));
                }
            }

            if (frameworks.Count == 0)
            {
                partial = true;
                AddProjectDiagnostic(
                    diagnostics,
                    workspaceDiagnostics,
                    options.MaxDiagnostics,
                    new(
                        "DNW113",
                        DotNetWorkspaceDiagnosticSeverity.Information,
                        "No literal TargetFramework or TargetFrameworks value was found; evaluation may require imported MSBuild properties.",
                        relativePath));
            }
        }
        catch (Exception exception) when (exception is IOException
                                                or UnauthorizedAccessException
                                                or XmlException
                                                or InvalidDataException
                                                or ArgumentException
                                                or NotSupportedException)
        {
            partial = true;
            AddProjectDiagnostic(
                diagnostics,
                workspaceDiagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW101",
                    DotNetWorkspaceDiagnosticSeverity.Error,
                    $"The project could not be parsed: {exception.GetType().Name}.",
                    relativePath));
        }

        var looksLikeTest = IsTestLikePath(relativePath, name);
        var testKind = conventionalTest
            ? DotNetProjectTestKind.Conventional
            : outputType is DotNetProjectOutputType.Exe or DotNetProjectOutputType.WinExe && looksLikeTest
                ? DotNetProjectTestKind.ExecutableHarness
                : DotNetProjectTestKind.None;

        return new(
            StableId(relativePath),
            name,
            relativePath,
            frameworks.ToArray(),
            references.ToArray(),
            outputType,
            useWpf,
            testKind,
            partial,
            diagnostics,
            restoreState);
    }

    private static void ValidateIndexedProjectReferences(
        IList<DotNetProjectInfo> projects,
        int maximumDiagnostics,
        ICollection<DotNetWorkspaceDiagnostic> workspaceDiagnostics)
    {
        var indexedPaths = projects
            .Select(project => project.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var missing = project.ProjectReferenceRelativePaths
                .Where(reference => !indexedPaths.Contains(reference))
                .ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            var projectDiagnostics = project.Diagnostics.ToList();
            AddProjectDiagnostic(
                projectDiagnostics,
                workspaceDiagnostics,
                maximumDiagnostics,
                new(
                    "DNW119",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    "One or more referenced projects were outside the indexed project bound and were omitted.",
                    project.RelativePath));
            projects[index] = project with
            {
                ProjectReferenceRelativePaths = project.ProjectReferenceRelativePaths
                    .Where(indexedPaths.Contains)
                    .ToArray(),
                IsPartial = true,
                Diagnostics = projectDiagnostics
            };
        }
    }

    private static DotNetSolutionInfo ParseSolution(
        string root,
        string relativePath,
        IReadOnlySet<string> knownProjects,
        DotNetDiscoveryOptions options,
        ICollection<DotNetWorkspaceDiagnostic> diagnostics)
    {
        var projectPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        try
        {
            var absolutePath = ResolveRelativePath(root, relativePath);
            if (PathContainsReparsePoint(root, absolutePath))
            {
                throw new InvalidDataException("The solution path contains a reparse point.");
            }

            var solutionDirectory = Path.GetDirectoryName(absolutePath) ?? root;
            if (Path.GetExtension(relativePath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(absolutePath);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = options.MaxProjectFileBytes
                });
                var document = XDocument.Load(reader, LoadOptions.None);
                foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Project"))
                {
                    AddSolutionProject(
                        root,
                        solutionDirectory,
                        (string?)element.Attribute("Path") ?? (string?)element.Attribute("Include"),
                        knownProjects,
                        projectPaths,
                        ref partial);
                }
            }
            else
            {
                var fileInfo = new FileInfo(absolutePath);
                if (fileInfo.Length > options.MaxProjectFileBytes)
                {
                    throw new InvalidDataException("The solution file exceeds the configured size limit.");
                }

                foreach (var line in File.ReadLines(absolutePath))
                {
                    var match = SolutionProjectRegex().Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    AddSolutionProject(
                        root,
                        solutionDirectory,
                        match.Groups["path"].Value,
                        knownProjects,
                        projectPaths,
                        ref partial);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            partial = true;
            AddDiagnostic(
                diagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW201",
                    DotNetWorkspaceDiagnosticSeverity.Error,
                    $"The solution could not be parsed: {exception.GetType().Name}.",
                    relativePath));
        }

        if (partial)
        {
            AddDiagnostic(
                diagnostics,
                options.MaxDiagnostics,
                new(
                    "DNW202",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    "One or more solution project entries could not be matched to a discovered in-workspace project.",
                    relativePath));
        }

        return new(Path.GetFileNameWithoutExtension(relativePath), relativePath, projectPaths.ToArray(), partial);
    }

    private static void AddSolutionProject(
        string root,
        string solutionDirectory,
        string? path,
        IReadOnlySet<string> knownProjects,
        ISet<string> projects,
        ref bool partial)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.Contains("$(", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var candidate = Path.GetFullPath(path, solutionDirectory);
            if (TryGetSafeRelativePath(root, candidate, out var relativePath) && knownProjects.Contains(relativePath))
            {
                projects.Add(relativePath);
                return;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        partial = true;
    }

    private static (IReadOnlyList<DotNetCommandPlan> Plans, bool Truncated) CreateCommandPlans(
        IReadOnlyList<DotNetSolutionInfo> solutions,
        IReadOnlyList<DotNetProjectInfo> projects,
        int maxPlans)
    {
        var plans = new List<DotNetCommandPlan>();
        var attemptedPlans = 0;
        foreach (var solution in solutions.Where(solution => !solution.IsPartial))
        {
            AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                DotNetCommandKind.Restore,
                DotNetCommandTargetKind.Solution,
                solution.RelativePath,
                ["restore", solution.RelativePath],
                $"Restore packages for {solution.Name}.",
                requiresSeparateApproval: true,
                DotNetNetworkRisk.MayAccessConfiguredPackageSources));
            AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                DotNetCommandKind.Build,
                DotNetCommandTargetKind.Solution,
                solution.RelativePath,
                ["build", solution.RelativePath, "--no-restore"],
                $"Build {solution.Name} without restoring packages.",
                requiresSeparateApproval: false,
                DotNetNetworkRisk.None));
        }

        foreach (var project in projects.Where(project =>
                     !project.Diagnostics.Any(diagnostic => diagnostic.Severity == DotNetWorkspaceDiagnosticSeverity.Error)))
        {
            AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                DotNetCommandKind.Restore,
                DotNetCommandTargetKind.Project,
                project.RelativePath,
                ["restore", project.RelativePath],
                $"Restore packages for {project.Name}.",
                requiresSeparateApproval: true,
                DotNetNetworkRisk.MayAccessConfiguredPackageSources));
            AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                DotNetCommandKind.Build,
                DotNetCommandTargetKind.Project,
                project.RelativePath,
                ["build", project.RelativePath, "--no-restore"],
                $"Build {project.Name} without restoring packages.",
                requiresSeparateApproval: false,
                DotNetNetworkRisk.None));

            if (project.IsConventionalTestProject)
            {
                AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                    DotNetCommandKind.Test,
                    DotNetCommandTargetKind.Project,
                    project.RelativePath,
                    ["test", project.RelativePath, "--no-build"],
                    $"Run the already-built tests in {project.Name}.",
                    requiresSeparateApproval: false,
                    DotNetNetworkRisk.None));
            }
            else if (project.IsExecutable)
            {
                AddPlan(plans, maxPlans, ref attemptedPlans, CreatePlan(
                    DotNetCommandKind.Run,
                    DotNetCommandTargetKind.Project,
                    project.RelativePath,
                    ["run", "--project", project.RelativePath, "--no-build"],
                    project.IsExecutableTestHarness
                        ? $"Run the already-built executable test harness {project.Name}."
                        : $"Run the already-built {project.Name}.",
                    requiresSeparateApproval: false,
                    DotNetNetworkRisk.None));
            }
        }

        var orderedPlans = plans
            .OrderBy(plan => plan.Kind)
            .ThenBy(plan => plan.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (orderedPlans, attemptedPlans > maxPlans);
    }

    private static DotNetCommandPlan CreatePlan(
        DotNetCommandKind kind,
        DotNetCommandTargetKind targetKind,
        string targetRelativePath,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSeparateApproval,
        DotNetNetworkRisk networkRisk)
    {
        var id = $"{kind.ToString().ToLowerInvariant()}:{StableId(targetRelativePath)}";
        return new(
            id,
            kind,
            targetKind,
            targetRelativePath,
            "dotnet",
            arguments,
            ".",
            RequiresUserApproval: true,
            RequiresSeparateApproval: requiresSeparateApproval,
            NetworkRisk: networkRisk,
            DisplayInvocation: $"dotnet {string.Join(' ', arguments.Select(QuoteArgument))}",
            Description: description);
    }

    private static DotNetCommandPlan RebuildPlan(
        DotNetCommandPlan original,
        IReadOnlyList<string> arguments,
        string description,
        string idSuffix)
    {
        return original with
        {
            Id = $"{original.Id}:{idSuffix}",
            Arguments = arguments,
            DisplayInvocation = $"dotnet {string.Join(' ', arguments.Select(QuoteArgument))}",
            Description = description
        };
    }

    private static List<string> RemoveTestFilter(IReadOnlyList<string> arguments)
    {
        var filtered = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count)
                {
                    index++;
                }

                continue;
            }

            if (argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(argument);
        }

        return filtered;
    }

    private static void AddPlan(
        ICollection<DotNetCommandPlan> plans,
        int maxPlans,
        ref int attemptedPlans,
        DotNetCommandPlan plan)
    {
        attemptedPlans++;
        if (plans.Count < maxPlans)
        {
            plans.Add(plan);
        }
    }

    private static string? FindOwningProject(DotNetWorkspaceSnapshot snapshot, string? fileRelativePath)
    {
        if (fileRelativePath is null)
        {
            return null;
        }

        return snapshot.Projects
            .Where(project => IsRelativePathWithin(Path.GetDirectoryName(project.RelativePath)?.Replace('\\', '/') ?? ".", fileRelativePath))
            .OrderByDescending(project => project.RelativePath.Length)
            .Select(project => project.RelativePath)
            .FirstOrDefault();
    }

    private static bool IsRelativePathWithin(string directory, string path)
    {
        if (directory == ".")
        {
            return true;
        }

        return path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> PropertyValues(IEnumerable<XElement> properties, string name)
    {
        return properties
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0);
    }

    private static bool HasConventionalTestSdk(XDocument document)
    {
        var sdkValues = new List<string>();
        if ((string?)document.Root?.Attribute("Sdk") is { Length: > 0 } rootSdk)
        {
            sdkValues.Add(rootSdk);
        }

        sdkValues.AddRange(document.Descendants()
            .Where(element => element.Name.LocalName == "Sdk")
            .Select(element => (string?)element.Attribute("Name") ?? element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
        return sdkValues
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(value => value.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasPlainMicrosoftNetSdk(XDocument document)
    {
        return ((string?)document.Root?.Attribute("Sdk"))?
            .Trim()
            .Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasUnevaluatedBuildInputs(
        string root,
        string projectDirectory,
        XDocument document,
        IReadOnlyList<XElement> properties)
    {
        if (document.Descendants().Any(element => element.Name.LocalName == "Import"))
        {
            return true;
        }

        var relevantProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TargetFramework",
            "TargetFrameworks",
            "OutputType",
            "UseWPF",
            "IsTestProject",
            "MSBuildProjectExtensionsPath",
            "BaseIntermediateOutputPath"
        };
        if (properties.Any(element =>
                relevantProperties.Contains(element.Name.LocalName)
                && (HasCondition(element)
                    || element.Value.Contains("$(", StringComparison.Ordinal)
                    || element.Value.Contains("@(", StringComparison.Ordinal)
                    || element.Value.Contains("%(", StringComparison.Ordinal))))
        {
            return true;
        }

        if (document.Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
            .Any(HasCondition))
        {
            return true;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(projectDirectory);
        while (TryGetSafeRelativePath(normalizedRoot, current, out _))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props"))
                || File.Exists(Path.Combine(current, "Directory.Build.targets")))
            {
                return true;
            }

            if (current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        var rootSdk = ((string?)document.Root?.Attribute("Sdk"))?.Trim();
        return string.IsNullOrWhiteSpace(rootSdk)
            || (!rootSdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
                && !rootSdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)
                && !PropertyValues(properties, "OutputType").Any());
    }

    private static bool HasCondition(XElement element)
    {
        for (XElement? current = element; current is not null; current = current.Parent)
        {
            if (current.Attribute("Condition") is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static DotNetRestoreState InspectRestoreState(
        string root,
        string projectDirectory,
        string projectRelativePath,
        IReadOnlyList<XElement> properties,
        ICollection<DotNetWorkspaceDiagnostic> projectDiagnostics,
        ICollection<DotNetWorkspaceDiagnostic> workspaceDiagnostics,
        int maximumDiagnostics)
    {
        var extensionsPath = PropertyValues(properties, "MSBuildProjectExtensionsPath").LastOrDefault();
        var intermediatePath = PropertyValues(properties, "BaseIntermediateOutputPath").LastOrDefault();
        var assetsDirectory = extensionsPath ?? intermediatePath ?? "obj";
        if (assetsDirectory.Contains("$(", StringComparison.Ordinal)
            || assetsDirectory.Contains("@(", StringComparison.Ordinal)
            || assetsDirectory.Contains("%(", StringComparison.Ordinal))
        {
            AddProjectDiagnostic(
                projectDiagnostics,
                workspaceDiagnostics,
                maximumDiagnostics,
                new(
                    "DNW115",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    "Restore assets could not be checked because their path contains an unevaluated MSBuild expression.",
                    projectRelativePath));
            return DotNetRestoreState.Unknown;
        }

        try
        {
            var candidateDirectory = Path.GetFullPath(assetsDirectory, projectDirectory);
            if (!TryGetSafeRelativePath(root, candidateDirectory, out _))
            {
                AddProjectDiagnostic(
                    projectDiagnostics,
                    workspaceDiagnostics,
                    maximumDiagnostics,
                    new(
                        "DNW115",
                        DotNetWorkspaceDiagnosticSeverity.Warning,
                        "Restore assets could not be checked because their configured path is outside the workspace.",
                        projectRelativePath));
                return DotNetRestoreState.Unknown;
            }

            var assetsPath = Path.Combine(candidateDirectory, "project.assets.json");
            if (File.Exists(assetsPath) && !PathContainsReparsePoint(root, assetsPath))
            {
                return DotNetRestoreState.AssetsAvailable;
            }

            AddProjectDiagnostic(
                projectDiagnostics,
                workspaceDiagnostics,
                maximumDiagnostics,
                new(
                    "DNW114",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    "Restore assets are missing; offline Build/Test/Run actions may require the separately approved Restore action first.",
                    projectRelativePath));
            return DotNetRestoreState.AssetsMissing;
        }
        catch (Exception exception) when (exception is IOException
                                                or UnauthorizedAccessException
                                                or ArgumentException
                                                or NotSupportedException
                                                or PathTooLongException)
        {
            AddProjectDiagnostic(
                projectDiagnostics,
                workspaceDiagnostics,
                maximumDiagnostics,
                new(
                    "DNW115",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    $"Restore assets could not be checked: {exception.GetType().Name}.",
                    projectRelativePath));
            return DotNetRestoreState.Unknown;
        }
    }

    private static void AddFrameworks(ISet<string> frameworks, string value)
    {
        foreach (var framework in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!framework.Contains("$(", StringComparison.Ordinal))
            {
                frameworks.Add(framework);
            }
        }
    }

    private static DotNetProjectOutputType ParseOutputType(string? outputType)
    {
        if (outputType is null)
        {
            return DotNetProjectOutputType.Unknown;
        }

        return outputType.Trim().ToLowerInvariant() switch
        {
            "library" => DotNetProjectOutputType.Library,
            "exe" => DotNetProjectOutputType.Exe,
            "winexe" => DotNetProjectOutputType.WinExe,
            _ => DotNetProjectOutputType.Unknown
        };
    }

    private static bool IsTestLikePath(string relativePath, string name)
    {
        var normalized = $"/{relativePath.Replace('\\', '/').Trim('/')}/";
        return normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddProjectDiagnostic(
        ICollection<DotNetWorkspaceDiagnostic> projectDiagnostics,
        ICollection<DotNetWorkspaceDiagnostic> workspaceDiagnostics,
        int maximum,
        DotNetWorkspaceDiagnostic diagnostic)
    {
        if (projectDiagnostics.Count < maximum)
        {
            projectDiagnostics.Add(diagnostic);
        }
        else if (!projectDiagnostics.Any(existing => existing.Code == "DNW099")
                 && projectDiagnostics is IList<DotNetWorkspaceDiagnostic> list
                 && list.Count > 0)
        {
            list[list.Count - 1] = new(
                "DNW099",
                DotNetWorkspaceDiagnosticSeverity.Warning,
                "Additional project diagnostics were omitted at the configured bound.",
                diagnostic.RelativePath);
        }

        AddDiagnostic(workspaceDiagnostics, maximum, diagnostic);
    }

    private static void AddDiagnostic(
        ICollection<DotNetWorkspaceDiagnostic> diagnostics,
        int maximum,
        DotNetWorkspaceDiagnostic diagnostic)
    {
        if (diagnostics.Count < maximum)
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static string StableId(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
                char.IsLetterOrDigit(character)
                || character is '_' or '.' or '/' or '\\' or ':' or '=' or '+' or '-'))
        {
            return argument;
        }

        return $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string ResolveRelativePath(string root, string relativePath)
    {
        var absolute = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
        if (!TryGetSafeRelativePath(root, absolute, out _))
        {
            throw new InvalidDataException("The path escaped the workspace.");
        }

        return absolute;
    }

    private static bool PathContainsReparsePoint(string root, string candidate)
    {
        if (!TryGetSafeRelativePath(root, candidate, out var relativePath))
        {
            return true;
        }

        var current = Path.GetFullPath(root);
        foreach (var segment in relativePath.Split(
                     ['/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetSafeRelativePath(string root, string candidate, out string relativePath)
    {
        relativePath = "";
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate).Replace('\\', '/');
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        relativePath = relative.Length == 0 ? "." : relative;
        return true;
    }

    [GeneratedRegex("""Project\([^)]*\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+\.csproj)"\s*,""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SolutionProjectRegex();

    [GeneratedRegex("""^[A-Za-z_][A-Za-z0-9_.+`]*$""", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFullyQualifiedTestNameRegex();
}
