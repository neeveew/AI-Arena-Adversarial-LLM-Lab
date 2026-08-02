using System.Xml;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace AIArena.CodeIntelligence;

internal sealed class AnalysisState
{
    private const long MaxProjectAssetsBytes = 32 * 1024 * 1024;
    private const int MaxDirectPackagesPerProject = 4_096;

    private readonly ImpactAnalysisOptions _options;
    private readonly Dictionary<string, NodeBuilder> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelationshipBuilder> _relationships = new(StringComparer.Ordinal);
    private readonly Dictionary<ProjectId, ProjectBuilder> _projects = [];
    private readonly Dictionary<string, ProjectBuilder> _projectsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NodeBuilder>> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NodeBuilder>> _typesByQualifiedName = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentId, DocumentInfo> _documents = [];
    private readonly List<ImpactDiagnostic> _diagnostics = [];
    private bool _partial;
    private bool _nodeLimitReported;
    private bool _relationshipLimitReported;

    public AnalysisState(ImpactPath paths, ImpactAnalysisOptions options)
    {
        Paths = paths;
        _options = options;
    }

    public ImpactPath Paths { get; }
    public bool HasUsefulEvidence => _nodes.Count > 0 || _projects.Count > 0;

    public ProjectBuilder AddProject(Project project, string relativePath)
    {
        if (_projects.TryGetValue(project.Id, out var existing))
        {
            return existing;
        }
        var id = ImpactPath.StableId(
            "project",
            ImpactPath.CaseInsensitiveIdentity(relativePath));
        var isTest = IsTestProject(project.Name, relativePath);
        var node = AddNode(new NodeBuilder(
            id,
            ImpactNodeKind.Project,
            project.Name,
            relativePath,
            relativePath,
            isProduction: !isTest,
            isTest: false));
        node.AddDefinition(new ImpactEvidence(relativePath), _options.MaxEvidencePerItem);
        var builder = new ProjectBuilder(
            project.Id,
            id,
            project.Name,
            relativePath,
            isTest,
            node);
        _projects.Add(project.Id, builder);
        _projectsByPath[relativePath] = builder;
        return builder;
    }

    public bool TryGetProject(ProjectId id, out ProjectBuilder project) =>
        _projects.TryGetValue(id, out project!);

    public NodeBuilder AddFileNode(
        string relativePath,
        string projectRelativePath,
        ImpactNodeKind kind,
        bool isTestProject)
    {
        var id = ImpactPath.StableId(
            kind == ImpactNodeKind.XamlFile ? "xaml" : "file",
            ImpactPath.CaseInsensitiveIdentity(projectRelativePath),
            ImpactPath.CaseInsensitiveIdentity(relativePath));
        var node = AddNode(new NodeBuilder(
            id,
            kind,
            Path.GetFileName(relativePath),
            relativePath,
            projectRelativePath,
            isProduction: !isTestProject,
            isTest: false));
        node.AddDefinition(new ImpactEvidence(relativePath), _options.MaxEvidencePerItem);
        if (_projectsByPath.TryGetValue(projectRelativePath, out var project))
        {
            AddRelationship(
                project.Id,
                node.Id,
                ImpactRelationshipKind.Declares,
                ImpactConfidence.High,
                new ImpactEvidence(relativePath));
        }
        return node;
    }

    public NodeBuilder AddSymbol(
        ISymbol symbol,
        string projectRelativePath,
        bool isTestProject,
        ImpactEvidence evidence)
    {
        var identity = RoslynImpactExplorer.SymbolIdentity(symbol);
        if (_symbols.TryGetValue(identity, out var symbolCandidates))
        {
            var existing = symbolCandidates.FirstOrDefault(candidate =>
                candidate.ProjectRelativePath?.Equals(
                    projectRelativePath,
                    StringComparison.OrdinalIgnoreCase) == true);
            if (existing is not null)
            {
                existing.AddDefinition(evidence, _options.MaxEvidencePerItem);
                existing.IsTest |= IsTestSymbol(symbol, isTestProject);
                return existing;
            }
        }

        var qualifiedName = RoslynImpactExplorer.QualifiedName(symbol);
        var node = AddNode(new NodeBuilder(
            ImpactPath.StableId(
                "symbol",
                ImpactPath.CaseInsensitiveIdentity(projectRelativePath),
                identity),
            NodeKind(symbol, IsTestSymbol(symbol, isTestProject)),
            symbol.Name,
            qualifiedName,
            projectRelativePath,
            isProduction: !isTestProject,
            isTest: IsTestSymbol(symbol, isTestProject)));
        node.AddDefinition(evidence, _options.MaxEvidencePerItem);
        if (symbolCandidates is null)
        {
            symbolCandidates = [];
            _symbols.Add(identity, symbolCandidates);
        }
        symbolCandidates.Add(node);
        if (symbol is INamedTypeSymbol)
        {
            if (!_typesByQualifiedName.TryGetValue(qualifiedName, out var typeCandidates))
            {
                typeCandidates = [];
                _typesByQualifiedName.Add(qualifiedName, typeCandidates);
            }
            typeCandidates.Add(node);
        }
        return node;
    }

    public NodeBuilder AddXamlResource(
        string key,
        string projectRelativePath,
        ImpactEvidence evidence)
    {
        var node = AddNode(new NodeBuilder(
            ImpactPath.StableId(
                "xaml-resource",
                ImpactPath.CaseInsensitiveIdentity(projectRelativePath),
                ImpactPath.CaseInsensitiveIdentity(evidence.RelativePath),
                key),
            ImpactNodeKind.XamlResource,
            key,
            $"{evidence.RelativePath}#{key}",
            projectRelativePath,
            isProduction: !_projectsByPath.TryGetValue(projectRelativePath, out var project) || !project.IsTestProject,
            isTest: false));
        node.AddDefinition(evidence, _options.MaxEvidencePerItem);
        return node;
    }

    public bool TrySymbolNode(ISymbol? symbol, out NodeBuilder node)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (!_symbols.TryGetValue(
                    RoslynImpactExplorer.SymbolIdentity(current),
                    out var candidates))
            {
                continue;
            }

            var distinct = candidates.DistinctBy(candidate => candidate.Id).Take(2).ToArray();
            if (distinct.Length == 1)
            {
                node = distinct[0];
                return true;
            }

            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in current.Locations.Where(location => location.IsInSource))
            {
                if (Paths.TryRelative(location.SourceTree?.FilePath, out var relativePath))
                {
                    sourcePaths.Add(relativePath);
                }
            }
            var sourceMatches = candidates
                .Where(candidate => candidate.Definitions.Any(definition =>
                    sourcePaths.Contains(definition.RelativePath)))
                .DistinctBy(candidate => candidate.Id)
                .Take(2)
                .ToArray();
            if (sourceMatches.Length == 1)
            {
                node = sourceMatches[0];
                return true;
            }
        }
        node = null!;
        return false;
    }

    public bool TryQualifiedType(
        string qualifiedName,
        string? projectRelativePath,
        out NodeBuilder node)
    {
        if (_typesByQualifiedName.TryGetValue(qualifiedName, out var candidates))
        {
            if (!string.IsNullOrWhiteSpace(projectRelativePath))
            {
                var sameProject = candidates
                    .Where(item => item.ProjectRelativePath?.Equals(
                        projectRelativePath,
                        StringComparison.OrdinalIgnoreCase) == true)
                    .DistinctBy(item => item.Id)
                    .Take(2)
                    .ToArray();
                if (sameProject.Length == 1)
                {
                    node = sameProject[0];
                    return true;
                }
            }
            var distinct = candidates.DistinctBy(item => item.Id).Take(2).ToArray();
            if (distinct.Length == 1)
            {
                node = distinct[0];
                return true;
            }
        }
        node = null!;
        return false;
    }

    public void MarkTest(ISymbol symbol)
    {
        if (TrySymbolNode(symbol, out var node))
        {
            node.IsTest = true;
            node.Kind = ImpactNodeKind.Test;
        }
    }

    public void RegisterDocument(
        DocumentId id,
        string relativePath,
        string projectRelativePath,
        string fileNodeId)
    {
        var isTestProject = _projectsByPath.TryGetValue(projectRelativePath, out var project)
            && project.IsTestProject;
        _documents[id] = new DocumentInfo(relativePath, projectRelativePath, fileNodeId, isTestProject);
    }

    public bool TryGetDocument(DocumentId id, out DocumentInfo info) =>
        _documents.TryGetValue(id, out info!);

    public void AddRelationship(
        string sourceId,
        string targetId,
        ImpactRelationshipKind kind,
        ImpactConfidence confidence,
        ImpactEvidence evidence)
    {
        if (sourceId == targetId || !_nodes.ContainsKey(sourceId) || !_nodes.ContainsKey(targetId))
        {
            return;
        }
        var identity = $"{sourceId}\n{targetId}\n{kind}";
        if (!_relationships.TryGetValue(identity, out var relationship))
        {
            if (_relationships.Count >= _options.MaxRelationships)
            {
                MarkPartial();
                if (!_relationshipLimitReported)
                {
                    _relationshipLimitReported = true;
                    AddDiagnostic(
                        "relationship-limit",
                        ImpactDiagnosticSeverity.Warning,
                        "The semantic relationship limit was reached; the index is partial.");
                }
                return;
            }
            relationship = new RelationshipBuilder(
                ImpactPath.StableId("edge", sourceId, targetId, kind.ToString()),
                sourceId,
                targetId,
                kind,
                confidence);
            _relationships.Add(identity, relationship);
        }
        relationship.Count++;
        relationship.AddEvidence(evidence, _options.MaxEvidencePerItem);
    }

    public void AddTestRelationships()
    {
        var semantic = _relationships.Values
            .Where(edge => edge.Kind is ImpactRelationshipKind.Calls or ImpactRelationshipKind.References)
            .ToArray();
        foreach (var edge in semantic)
        {
            if (!_nodes.TryGetValue(edge.SourceId, out var source)
                || !_nodes.TryGetValue(edge.TargetId, out var target)
                || !source.IsTest
                || !target.IsProduction)
            {
                continue;
            }
            foreach (var evidence in edge.Evidence)
            {
                AddRelationship(
                    target.Id,
                    source.Id,
                    ImpactRelationshipKind.Tests,
                    edge.Kind == ImpactRelationshipKind.Calls
                        ? ImpactConfidence.High
                        : ImpactConfidence.Medium,
                    evidence);
            }
        }
    }

    public void CollectPackageReferences(ProjectBuilder project)
    {
        if (!Paths.TryResolveRelative(project.RelativePath, out var projectPath))
        {
            project.IsPartial = true;
            MarkPartial();
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.SetLineInfo);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            project.IsPartial = true;
            MarkPartial();
            AddDiagnostic(
                "project-package-read-failed",
                ImpactDiagnosticSeverity.Warning,
                $"Package references could not be inspected: {exception.Message}",
                project.RelativePath);
            return;
        }

        var packageElements = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .OrderBy(element => element.Attribute("Include")?.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (TryCollectEvaluatedPackageReferences(project, projectPath, out var evaluatedReadFailed))
        {
            return;
        }

        foreach (var element in packageElements)
        {
            if (!string.IsNullOrWhiteSpace(element.Attribute("Condition")?.Value)
                || element.Ancestors().Any(ancestor =>
                    !string.IsNullOrWhiteSpace(ancestor.Attribute("Condition")?.Value)))
            {
                continue;
            }
            var include = element.Attribute("Include")?.Value?.Trim();
            if (!IsSafePackageName(include))
            {
                continue;
            }
            var version = element.Attribute("Version")?.Value?.Trim()
                ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value.Trim();
            if (!IsSafePackageVersion(version))
            {
                version = null;
            }
            AddPackageReference(
                project,
                include!,
                version,
                ImpactConfidence.High,
                XmlEvidence(element, project.RelativePath));
        }

        var hasConditionalReferences = packageElements.Any(element =>
            !string.IsNullOrWhiteSpace(element.Attribute("Condition")?.Value)
            || element.Ancestors().Any(ancestor =>
                !string.IsNullOrWhiteSpace(ancestor.Attribute("Condition")?.Value)));
        var hasPotentialImportedReferences = HasPotentialImportedPackageReferences(
            projectPath,
            document);
        if (evaluatedReadFailed || hasConditionalReferences || hasPotentialImportedReferences)
        {
            project.IsPartial = true;
            MarkPartial();
            AddDiagnostic(
                "package-evaluated-evidence-unavailable",
                ImpactDiagnosticSeverity.Warning,
                evaluatedReadFailed
                    ? "Evaluated NuGet evidence could not be read; only safe literal project references were retained."
                    : "Evaluated NuGet evidence is unavailable; conditional or imported package references may be missing.",
                project.RelativePath);
        }
    }

    private bool TryCollectEvaluatedPackageReferences(
        ProjectBuilder project,
        string projectPath,
        out bool readFailed)
    {
        readFailed = false;
        var assetsPath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "obj",
            "project.assets.json");
        if (!Paths.TryContainedFullPath(assetsPath, out assetsPath)
            || !File.Exists(assetsPath))
        {
            return false;
        }

        try
        {
            var assetsInfo = new FileInfo(assetsPath);
            if (assetsInfo.Length > MaxProjectAssetsBytes)
            {
                readFailed = true;
                AddDiagnostic(
                    "package-assets-limit",
                    ImpactDiagnosticSeverity.Warning,
                    "Evaluated NuGet evidence exceeded the bounded assets-file size limit.",
                    project.RelativePath);
                return false;
            }

            using var stream = File.OpenRead(assetsPath);
            using var assets = JsonDocument.Parse(stream);
            if (!assets.RootElement.TryGetProperty("project", out var projectElement)
                || !projectElement.TryGetProperty("frameworks", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
            {
                readFailed = true;
                AddDiagnostic(
                    "package-assets-invalid",
                    ImpactDiagnosticSeverity.Warning,
                    "Evaluated NuGet evidence did not contain project framework dependencies.",
                    project.RelativePath);
                return false;
            }

            var directNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requestedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (!framework.Value.TryGetProperty("dependencies", out var dependencies)
                    || dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (dependency.Value.ValueKind == JsonValueKind.Object
                        && dependency.Value.TryGetProperty("target", out var target)
                        && target.ValueKind == JsonValueKind.String
                        && target.GetString()?.Equals(
                            "Project",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        continue;
                    }
                    if (!IsSafePackageName(dependency.Name))
                    {
                        continue;
                    }
                    directNames.Add(dependency.Name);
                    if (dependency.Value.ValueKind == JsonValueKind.Object
                        && dependency.Value.TryGetProperty("version", out var version)
                        && version.ValueKind == JsonValueKind.String
                        && IsSafePackageVersion(version.GetString()))
                    {
                        requestedVersions[dependency.Name] = version.GetString()!;
                    }
                }
            }

            if (directNames.Count > MaxDirectPackagesPerProject)
            {
                project.IsPartial = true;
                MarkPartial();
                AddDiagnostic(
                    "package-reference-limit",
                    ImpactDiagnosticSeverity.Warning,
                    "The direct package-reference limit was reached; package relationships are partial.",
                    project.RelativePath);
            }

            var boundedNames = directNames
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDirectPackagesPerProject)
                .ToArray();
            var boundedNameSet = boundedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var resolvedVersions = new Dictionary<string, SortedSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (assets.RootElement.TryGetProperty("libraries", out var libraries)
                && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || !string.Equals(
                            type.GetString(),
                            "package",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var separator = library.Name.LastIndexOf('/');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    var name = library.Name[..separator];
                    var version = library.Name[(separator + 1)..];
                    if (!boundedNameSet.Contains(name) || !IsSafePackageVersion(version))
                    {
                        continue;
                    }
                    if (!resolvedVersions.TryGetValue(name, out var versions))
                    {
                        versions = new SortedSet<string>(StringComparer.Ordinal);
                        resolvedVersions.Add(name, versions);
                    }
                    versions.Add(version);
                }
            }

            var evidence = new ImpactEvidence(project.RelativePath);
            foreach (var name in boundedNames)
            {
                if (resolvedVersions.TryGetValue(name, out var versions)
                    && versions.Count > 0)
                {
                    foreach (var version in versions)
                    {
                        AddPackageReference(
                            project,
                            name,
                            version,
                            ImpactConfidence.High,
                            evidence);
                    }
                }
                else
                {
                    requestedVersions.TryGetValue(name, out var requestedVersion);
                    AddPackageReference(
                        project,
                        name,
                        requestedVersion,
                        ImpactConfidence.High,
                        evidence);
                }
            }
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            readFailed = true;
            AddDiagnostic(
                "package-assets-read-failed",
                ImpactDiagnosticSeverity.Warning,
                $"Evaluated NuGet evidence could not be read: {exception.Message}",
                project.RelativePath);
            return false;
        }
    }

    private void AddPackageReference(
        ProjectBuilder project,
        string include,
        string? version,
        ImpactConfidence confidence,
        ImpactEvidence evidence)
    {
        if (!IsSafePackageVersion(version))
        {
            version = null;
        }
        var packageId = ImpactPath.StableId(
            "package",
            ImpactPath.CaseInsensitiveIdentity(include),
            ImpactPath.CaseInsensitiveIdentity(version ?? "<managed>"));
        var package = AddNode(new NodeBuilder(
            packageId,
            ImpactNodeKind.Package,
            include,
            version is null ? include : $"{include}@{version}",
            null,
            isProduction: false,
            isTest: false));
        package.AddDefinition(evidence, _options.MaxEvidencePerItem);
        project.PackageNames.Add(include);
        AddRelationship(
            project.Id,
            package.Id,
            ImpactRelationshipKind.PackageReferences,
            confidence,
            evidence);
    }

    private bool HasPotentialImportedPackageReferences(
        string projectPath,
        XDocument projectDocument)
    {
        if (projectDocument.Descendants()
            .Any(element => element.Name.LocalName == "Import"))
        {
            return true;
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        while (current is not null
               && Paths.TryContainedFullPath(current.FullName, out _))
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var candidate = Path.Combine(current.FullName, name);
                if (!File.Exists(candidate)
                    || !Paths.TryContainedFullPath(candidate, out candidate))
                {
                    continue;
                }
                try
                {
                    var imported = XDocument.Load(candidate);
                    if (imported.Descendants()
                        .Any(element => element.Name.LocalName == "PackageReference"))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is XmlException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    return true;
                }
            }
            if (current.FullName.Equals(Paths.Root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }
        return false;
    }

    public void AddDiagnostic(
        string code,
        ImpactDiagnosticSeverity severity,
        string message,
        string? relativePath = null)
    {
        if (_diagnostics.Count >= _options.MaxDiagnostics)
        {
            return;
        }
        _diagnostics.Add(new ImpactDiagnostic(
            code,
            severity,
            Bound(SanitizeDiagnostic(message, Paths.Root), 1_000),
            relativePath));
    }

    public void MarkPartial() => _partial = true;

    public void MarkProjectPartial(string? projectRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(projectRelativePath)
            && _projectsByPath.TryGetValue(projectRelativePath, out var project))
        {
            project.IsPartial = true;
        }
        MarkPartial();
    }

    public ImpactSnapshot Build(ImpactAvailability? forcedAvailability = null)
    {
        var availability = forcedAvailability
            ?? (_partial ? ImpactAvailability.Partial : ImpactAvailability.Available);
        var projects = _projects.Values
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => new ImpactProject(
                item.Id,
                item.Name,
                item.RelativePath,
                item.IsTestProject,
                item.IsPartial,
                item.ProjectReferences
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                item.PackageNames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        var nodes = _nodes.Values
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Build())
            .ToArray();
        var relationships = _relationships.Values
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Build())
            .ToArray();
        var diagnostics = _diagnostics
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToArray();
        return new ImpactSnapshot(
            ImpactSnapshot.CurrentSchema,
            availability,
            projects,
            nodes,
            relationships,
            diagnostics);
    }

    private NodeBuilder AddNode(NodeBuilder candidate)
    {
        if (_nodes.TryGetValue(candidate.Id, out var existing))
        {
            return existing;
        }
        if (_nodes.Count >= _options.MaxNodes)
        {
            MarkPartial();
            if (!_nodeLimitReported)
            {
                _nodeLimitReported = true;
                AddDiagnostic(
                    "node-limit",
                    ImpactDiagnosticSeverity.Warning,
                    "The semantic node limit was reached; the index is partial.");
            }
            throw new InvalidOperationException("The configured semantic node limit was reached.");
        }
        _nodes.Add(candidate.Id, candidate);
        return candidate;
    }

    private static bool IsTestProject(string name, string relativePath) =>
        name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
        || relativePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestSymbol(ISymbol symbol, bool isTestProject)
    {
        if (!isTestProject)
        {
            return false;
        }
        return symbol is IMethodSymbol
            && symbol.GetAttributes().Any(attribute =>
                IsTestAttribute(attribute.AttributeClass?.Name));
    }

    private static bool IsTestAttribute(string? name) =>
        name is "FactAttribute"
            or "TheoryAttribute"
            or "TestAttribute"
            or "TestCaseAttribute"
            or "TestMethodAttribute"
            or "DataTestMethodAttribute";

    private static ImpactNodeKind NodeKind(ISymbol symbol, bool isTest)
    {
        if (isTest)
        {
            return ImpactNodeKind.Test;
        }
        return symbol switch
        {
            INamedTypeSymbol => ImpactNodeKind.Type,
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } =>
                ImpactNodeKind.Constructor,
            IMethodSymbol => ImpactNodeKind.Method,
            IPropertySymbol => ImpactNodeKind.Property,
            IFieldSymbol => ImpactNodeKind.Field,
            IEventSymbol => ImpactNodeKind.Event,
            _ => ImpactNodeKind.Method
        };
    }

    private static bool IsSafePackageName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 200
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static bool IsSafePackageVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && !value.Any(char.IsControl)
        && !value.Contains('$')
        && !value.Contains('\\')
        && !value.Contains('/');

    private static ImpactEvidence XmlEvidence(XObject value, string relativePath)
    {
        var line = value as IXmlLineInfo;
        return new ImpactEvidence(
            relativePath,
            line?.HasLineInfo() == true ? line.LineNumber : null,
            line?.HasLineInfo() == true ? line.LinePosition : null);
    }

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string SanitizeDiagnostic(string value, string root)
    {
        var sanitized = value
            .Replace(root, "<workspace>", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/'), "<workspace>", StringComparison.OrdinalIgnoreCase);
        sanitized = Regex.Replace(
            sanitized,
            @"(?<![\w:])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""'<>|]*",
            "<absolute-path>",
            RegexOptions.CultureInvariant);
        return sanitized;
    }
}

internal sealed class ProjectBuilder(
    ProjectId projectId,
    string id,
    string name,
    string relativePath,
    bool isTestProject,
    NodeBuilder node)
{
    public ProjectId ProjectId { get; } = projectId;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string RelativePath { get; } = relativePath;
    public bool IsTestProject { get; } = isTestProject;
    public bool IsPartial { get; set; }
    public NodeBuilder Node { get; } = node;
    public List<string> ProjectReferences { get; } = [];
    public List<string> PackageNames { get; } = [];
}

internal sealed record DocumentInfo(
    string RelativePath,
    string ProjectRelativePath,
    string FileNodeId,
    bool IsTestProject);

internal sealed class NodeBuilder(
    string id,
    ImpactNodeKind kind,
    string name,
    string qualifiedName,
    string? projectRelativePath,
    bool isProduction,
    bool isTest)
{
    private readonly List<ImpactEvidence> _definitions = [];

    public string Id { get; } = id;
    public ImpactNodeKind Kind { get; set; } = kind;
    public string Name { get; } = name;
    public string QualifiedName { get; } = qualifiedName;
    public string? ProjectRelativePath { get; } = projectRelativePath;
    public bool IsProduction { get; } = isProduction;
    public bool IsTest { get; set; } = isTest;
    public IReadOnlyList<ImpactEvidence> Definitions => _definitions;

    public void AddDefinition(ImpactEvidence evidence, int limit)
    {
        if (_definitions.Count < limit && !_definitions.Contains(evidence))
        {
            _definitions.Add(evidence);
        }
    }

    public ImpactNode Build() => new(
        Id,
        Kind,
        Name,
        QualifiedName,
        ProjectRelativePath,
        IsProduction,
        IsTest,
        _definitions
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Column)
            .ToArray());
}

internal sealed class RelationshipBuilder(
    string id,
    string sourceId,
    string targetId,
    ImpactRelationshipKind kind,
    ImpactConfidence confidence)
{
    private readonly List<ImpactEvidence> _evidence = [];

    public string Id { get; } = id;
    public string SourceId { get; } = sourceId;
    public string TargetId { get; } = targetId;
    public ImpactRelationshipKind Kind { get; } = kind;
    public ImpactConfidence Confidence { get; } = confidence;
    public int Count { get; set; }
    public IReadOnlyList<ImpactEvidence> Evidence => _evidence;

    public void AddEvidence(ImpactEvidence evidence, int limit)
    {
        if (_evidence.Count < limit && !_evidence.Contains(evidence))
        {
            _evidence.Add(evidence);
        }
    }

    public ImpactRelationship Build() => new(
        Id,
        SourceId,
        TargetId,
        Kind,
        Count,
        Confidence,
        _evidence
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Column)
            .ToArray());
}
