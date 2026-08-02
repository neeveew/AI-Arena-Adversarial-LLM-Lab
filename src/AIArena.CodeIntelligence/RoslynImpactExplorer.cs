using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace AIArena.CodeIntelligence;

public sealed record ImpactAnalysisOptions(
    int MaxNodes = 50_000,
    int MaxRelationships = 250_000,
    int MaxEvidencePerItem = 16,
    int MaxDiagnostics = 256,
    int MaxXamlFiles = 5_000,
    int MaxXamlDirectories = 10_000);

/// <summary>
/// Builds a relative-path-only semantic index. Loading failures return a valid
/// unavailable or partial snapshot rather than a guessed empty/clean result.
/// </summary>
public sealed partial class RoslynImpactExplorer
{
    private static readonly object RegistrationLock = new();
    private static readonly SymbolDisplayFormat QualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly ImpactAnalysisOptions _options;

    public RoslynImpactExplorer(ImpactAnalysisOptions? options = null)
    {
        _options = options ?? new ImpactAnalysisOptions();
    }

    public async Task<ImpactSnapshot> AnalyzeSolutionAsync(
        string root,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        ImpactPath paths;
        try
        {
            paths = new ImpactPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ImpactSnapshot.Unavailable(new ImpactDiagnostic(
                "impact-root-invalid",
                ImpactDiagnosticSeverity.Error,
                "The analysis root is unavailable."));
        }

        var solutionCandidate = Path.IsPathRooted(solutionPath)
            ? solutionPath
            : Path.Combine(paths.Root, solutionPath);
        if (!paths.TryContainedFullPath(solutionCandidate, out var fullSolution)
            || !File.Exists(fullSolution))
        {
            return ImpactSnapshot.Unavailable(new ImpactDiagnostic(
                "impact-solution-unavailable",
                ImpactDiagnosticSeverity.Error,
                "The solution must be an existing file inside the analysis root."));
        }

        var state = new AnalysisState(paths, _options);
        try
        {
            EnsureMsBuildRegistered();
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                var severity = args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? ImpactDiagnosticSeverity.Warning
                    : ImpactDiagnosticSeverity.Information;
                state.AddDiagnostic("msbuild-workspace", severity, args.Diagnostic.Message);
                if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    state.MarkPartial();
                }
            });

            var solution = await workspace.OpenSolutionAsync(
                fullSolution,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await CollectProjectsAndDefinitionsAsync(solution, state, cancellationToken).ConfigureAwait(false);
            await CollectSemanticRelationshipsAsync(solution, state, cancellationToken).ConfigureAwait(false);
            CollectProjectRelationships(solution, state);
            CollectXamlRelationships(solution, state, cancellationToken);
            state.AddTestRelationships();
            return state.Build();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or XmlException)
        {
            state.AddDiagnostic(
                "impact-analysis-unavailable",
                ImpactDiagnosticSeverity.Error,
                $"Semantic analysis could not be completed: {exception.Message}");
            return state.HasUsefulEvidence
                ? state.Build(ImpactAvailability.Partial)
                : state.Build(ImpactAvailability.Unavailable);
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private async Task CollectProjectsAndDefinitionsAsync(
        Solution solution,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        foreach (var project in OrderedProjects(solution))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!state.Paths.TryRelative(project.FilePath, out var projectPath))
            {
                state.AddDiagnostic(
                    "project-outside-root",
                    ImpactDiagnosticSeverity.Warning,
                    "A project outside the analysis root was excluded.");
                state.MarkPartial();
                continue;
            }

            var projectBuilder = state.AddProject(project, projectPath);
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                projectBuilder.IsPartial = true;
                state.MarkPartial();
                state.AddDiagnostic(
                    "compilation-unavailable",
                    ImpactDiagnosticSeverity.Warning,
                    $"Compilation was unavailable for {project.Name}.",
                    projectPath);
            }

            foreach (var document in project.Documents
                         .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
                         .OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Paths.TryRelative(document.FilePath, out var documentPath))
                {
                    if (IsGeneratedDocumentName(document.Name))
                    {
                        continue;
                    }
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "document-outside-root",
                        ImpactDiagnosticSeverity.Warning,
                        $"A linked document outside the analysis root was excluded ({Path.GetFileName(document.FilePath)}).");
                    continue;
                }
                if (IsBuildArtifactRelativePath(documentPath))
                {
                    continue;
                }
                var fileNode = state.AddFileNode(
                    documentPath,
                    projectPath,
                    ImpactNodeKind.File,
                    projectBuilder.IsTestProject);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "document-semantic-model-unavailable",
                        ImpactDiagnosticSeverity.Warning,
                        "A document could not be semantically indexed.",
                        documentPath);
                    continue;
                }

                state.RegisterDocument(document.Id, documentPath, projectPath, fileNode.Id);
                foreach (var declaration in DeclarationSyntax(root))
                {
                    foreach (var symbol in DeclaredSymbols(model, declaration, cancellationToken))
                    {
                        if (!IsSupportedDefinition(symbol))
                        {
                            continue;
                        }
                        var evidence = Evidence(declaration.GetLocation(), documentPath);
                        var node = state.AddSymbol(symbol, projectPath, projectBuilder.IsTestProject, evidence);
                        state.AddRelationship(
                            fileNode.Id,
                            node.Id,
                            ImpactRelationshipKind.Declares,
                            ImpactConfidence.High,
                            evidence);
                    }
                }
            }
        }
    }

    private static async Task CollectSemanticRelationshipsAsync(
        Solution solution,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        foreach (var project in OrderedProjects(solution))
        {
            foreach (var document in project.Documents
                         .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
                         .OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.TryGetDocument(document.Id, out var documentInfo))
                {
                    continue;
                }
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var typeDeclaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol source
                        || !state.TrySymbolNode(source, out var sourceNode))
                    {
                        continue;
                    }
                    if (source.BaseType is { SpecialType: SpecialType.None } baseType
                        && state.TrySymbolNode(baseType, out var baseNode))
                    {
                        state.AddRelationship(
                            sourceNode.Id,
                            baseNode.Id,
                            ImpactRelationshipKind.Inherits,
                            ImpactConfidence.High,
                            Evidence(typeDeclaration.GetLocation(), documentInfo.RelativePath));
                    }
                    foreach (var @interface in source.Interfaces)
                    {
                        if (state.TrySymbolNode(@interface, out var interfaceNode))
                        {
                            state.AddRelationship(
                                sourceNode.Id,
                                interfaceNode.Id,
                                ImpactRelationshipKind.Implements,
                                ImpactConfidence.High,
                                Evidence(typeDeclaration.GetLocation(), documentInfo.RelativePath));
                        }
                    }
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var target = BestSymbol(model.GetSymbolInfo(invocation, cancellationToken));
                    AddSemanticRelationship(
                        state,
                        model,
                        invocation,
                        target,
                        ImpactRelationshipKind.Calls,
                        documentInfo,
                        cancellationToken);
                }

                foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var target = BestSymbol(model.GetSymbolInfo(creation, cancellationToken));
                    AddSemanticRelationship(
                        state,
                        model,
                        creation,
                        target,
                        ImpactRelationshipKind.Calls,
                        documentInfo,
                        cancellationToken);
                }

                foreach (var creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
                {
                    var target = BestSymbol(model.GetSymbolInfo(creation, cancellationToken));
                    AddSemanticRelationship(
                        state,
                        model,
                        creation,
                        target,
                        ImpactRelationshipKind.Calls,
                        documentInfo,
                        cancellationToken);
                }

                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var target = BestSymbol(model.GetSymbolInfo(name, cancellationToken));
                    if (target is null)
                    {
                        continue;
                    }
                    AddSemanticRelationship(
                        state,
                        model,
                        name,
                        target,
                        ImpactRelationshipKind.References,
                        documentInfo,
                        cancellationToken);
                    if (documentInfo.IsTestProject
                        && target is IMethodSymbol
                        && IsHarnessRegistration(name))
                    {
                        state.MarkTest(target);
                    }
                }
            }
        }
    }

    private static void AddSemanticRelationship(
        AnalysisState state,
        SemanticModel model,
        SyntaxNode syntax,
        ISymbol? targetSymbol,
        ImpactRelationshipKind kind,
        DocumentInfo document,
        CancellationToken cancellationToken)
    {
        if (targetSymbol is null
            || !state.TrySymbolNode(targetSymbol, out var target))
        {
            return;
        }

        var enclosing = model.GetEnclosingSymbol(syntax.SpanStart, cancellationToken);
        if (!state.TrySymbolNode(enclosing, out var source))
        {
            return;
        }
        if (source.Id == target.Id)
        {
            return;
        }

        state.AddRelationship(
            source.Id,
            target.Id,
            kind,
            ImpactConfidence.High,
            Evidence(syntax.GetLocation(), document.RelativePath));
    }

    private static void CollectProjectRelationships(Solution solution, AnalysisState state)
    {
        foreach (var project in OrderedProjects(solution))
        {
            if (!state.TryGetProject(project.Id, out var source))
            {
                continue;
            }
            foreach (var reference in project.ProjectReferences.OrderBy(item => item.ProjectId.Id))
            {
                if (!state.TryGetProject(reference.ProjectId, out var target))
                {
                    source.IsPartial = true;
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "project-reference-unresolved",
                        ImpactDiagnosticSeverity.Warning,
                        $"A project reference from {source.Name} could not be resolved.",
                        source.RelativePath);
                    continue;
                }
                source.ProjectReferences.Add(target.RelativePath);
                state.AddRelationship(
                    source.Id,
                    target.Id,
                    ImpactRelationshipKind.ProjectReferences,
                    ImpactConfidence.High,
                    new ImpactEvidence(source.RelativePath));
            }

            state.CollectPackageReferences(source);
        }
    }

    private void CollectXamlRelationships(
        Solution solution,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        var resources = new Dictionary<string, List<NodeBuilder>>(StringComparer.Ordinal);
        var usages = new List<(NodeBuilder File, string Key, ImpactEvidence Evidence)>();
        var mergedDictionaries = new Dictionary<string, string[]>(
            StringComparer.OrdinalIgnoreCase);
        var xamlCount = 0;
        foreach (var project in OrderedProjects(solution))
        {
            if (!state.TryGetProject(project.Id, out var projectInfo))
            {
                continue;
            }
            var directory = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            var traversalPartial = false;
            foreach (var xamlPath in EnumerateXamlFiles(
                         directory,
                         _options.MaxXamlDirectories,
                         cancellationToken,
                         () => traversalPartial = true)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++xamlCount > _options.MaxXamlFiles)
                {
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "xaml-file-limit",
                        ImpactDiagnosticSeverity.Warning,
                        "The XAML file limit was reached; XAML relationships are partial.");
                    return;
                }
                if (!state.Paths.TryRelative(xamlPath, out var relativePath))
                {
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "xaml-outside-root",
                        ImpactDiagnosticSeverity.Warning,
                        "A XAML file outside the analysis root was excluded.");
                    continue;
                }

                var file = state.AddFileNode(
                    relativePath,
                    projectInfo.RelativePath,
                    ImpactNodeKind.XamlFile,
                    projectInfo.IsTestProject);
                XDocument document;
                try
                {
                    document = XDocument.Load(xamlPath, LoadOptions.SetLineInfo);
                }
                catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
                {
                    state.MarkPartial();
                    state.AddDiagnostic(
                        "xaml-parse-failed",
                        ImpactDiagnosticSeverity.Warning,
                        $"XAML could not be parsed: {exception.Message}",
                        relativePath);
                    continue;
                }

                var root = document.Root;
                var className = root?.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName == "Class"
                    && attribute.Name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml")?.Value;
                if (!string.IsNullOrWhiteSpace(className)
                    && state.TryQualifiedType(
                        className.Trim(),
                        projectInfo.RelativePath,
                        out var codeBehind))
                {
                    state.AddRelationship(
                        file.Id,
                        codeBehind.Id,
                        ImpactRelationshipKind.XamlCodeBehind,
                        ImpactConfidence.High,
                        XmlEvidence(root!, relativePath));
                }

                mergedDictionaries[relativePath] = document.Descendants()
                    .Where(element => element.Name.LocalName == "ResourceDictionary")
                    .Select(element => element.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "Source")?.Value)
                    .Where(source => TryResolveMergedDictionarySource(
                        state.Paths,
                        xamlPath,
                        source,
                        out _))
                    .Select(source =>
                    {
                        TryResolveMergedDictionarySource(
                            state.Paths,
                            xamlPath,
                            source,
                            out var mergedPath);
                        return mergedPath;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var element in document.Descendants())
                {
                    var key = element.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "Key"
                        && attribute.Name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml")?.Value;
                    if (IsSafeXamlKey(key))
                    {
                        var resource = state.AddXamlResource(
                            key!.Trim(),
                            projectInfo.RelativePath,
                            XmlEvidence(element, relativePath));
                        if (!resources.TryGetValue(key.Trim(), out var candidates))
                        {
                            candidates = [];
                            resources.Add(key.Trim(), candidates);
                        }
                        candidates.Add(resource);
                    }
                    foreach (var attribute in element.Attributes())
                    {
                        foreach (Match match in XamlResourceUsageRegex().Matches(attribute.Value))
                        {
                            var usageKey = match.Groups["key"].Value.Trim();
                            if (IsSafeXamlKey(usageKey))
                            {
                                usages.Add((file, usageKey, XmlEvidence(element, relativePath)));
                            }
                        }
                    }
                }
            }
            if (traversalPartial)
            {
                state.MarkPartial();
                state.AddDiagnostic(
                    "xaml-traversal-partial",
                    ImpactDiagnosticSeverity.Warning,
                    "One or more inaccessible, reparse-point, or over-limit XAML directories were excluded.",
                    projectInfo.RelativePath);
            }
        }

        var ambiguitiesReported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var usage in usages
                     .OrderBy(item => item.File.QualifiedName, StringComparer.Ordinal)
                     .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!resources.TryGetValue(usage.Key, out var candidates))
            {
                continue;
            }
            var sameProject = candidates
                .Where(candidate => candidate.ProjectRelativePath == usage.File.ProjectRelativePath)
                .DistinctBy(candidate => candidate.Id)
                .OrderBy(candidate => candidate.Definitions
                    .Select(definition => definition.RelativePath)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault(), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray();
            if (sameProject.Length == 0)
            {
                continue;
            }

            var sameFile = sameProject
                .Where(candidate => candidate.Definitions.Any(definition =>
                    definition.RelativePath.Equals(
                        usage.File.QualifiedName,
                        StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (sameFile.Length == 1)
            {
                state.AddRelationship(
                    usage.File.Id,
                    sameFile[0].Id,
                    ImpactRelationshipKind.XamlUsesResource,
                    ImpactConfidence.High,
                    usage.Evidence);
                continue;
            }

            NodeBuilder[] scopedCandidates;
            var scope = "same-file";
            if (sameFile.Length > 1)
            {
                scopedCandidates = sameFile;
            }
            else
            {
                var mergedPaths = MergedDictionaryClosure(
                    usage.File.QualifiedName,
                    mergedDictionaries,
                    _options.MaxXamlFiles);
                var mergedCandidates = sameProject
                    .Where(candidate => candidate.Definitions.Any(definition =>
                        mergedPaths.Contains(definition.RelativePath)))
                    .ToArray();
                if (mergedCandidates.Length == 1)
                {
                    state.AddRelationship(
                        usage.File.Id,
                        mergedCandidates[0].Id,
                        ImpactRelationshipKind.XamlUsesResource,
                        ImpactConfidence.Medium,
                        usage.Evidence);
                    continue;
                }
                if (mergedCandidates.Length > 1)
                {
                    scopedCandidates = mergedCandidates;
                    scope = "merged-dictionary";
                }
                else if (sameProject.Length == 1)
                {
                    state.AddRelationship(
                        usage.File.Id,
                        sameProject[0].Id,
                        ImpactRelationshipKind.XamlUsesResource,
                        ImpactConfidence.Medium,
                        usage.Evidence);
                    continue;
                }
                else
                {
                    scopedCandidates = sameProject;
                    scope = "same-project";
                }
            }

            var candidateLimit = Math.Clamp(_options.MaxEvidencePerItem, 1, 16);
            var retained = scopedCandidates.Take(candidateLimit).ToArray();
            foreach (var candidate in retained)
            {
                state.AddRelationship(
                    usage.File.Id,
                    candidate.Id,
                    ImpactRelationshipKind.XamlUsesResource,
                    ImpactConfidence.Low,
                    usage.Evidence);
            }
            state.MarkProjectPartial(usage.File.ProjectRelativePath);
            var ambiguityIdentity =
                $"{usage.File.ProjectRelativePath}\n{usage.File.QualifiedName}\n{usage.Key}";
            if (ambiguitiesReported.Add(ambiguityIdentity))
            {
                state.AddDiagnostic(
                    "xaml-resource-ambiguous",
                    ImpactDiagnosticSeverity.Warning,
                    $"XAML resource '{usage.Key}' has {scopedCandidates.Length} "
                    + $"{scope} definitions; {retained.Length} bounded candidate "
                    + "relationship(s) were retained.",
                    usage.File.QualifiedName);
            }
        }
    }

    private static HashSet<string> MergedDictionaryClosure(
        string sourcePath,
        IReadOnlyDictionary<string, string[]> mergedDictionaries,
        int maxFiles)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        if (mergedDictionaries.TryGetValue(sourcePath, out var direct))
        {
            foreach (var path in direct)
            {
                pending.Enqueue(path);
            }
        }
        while (pending.Count > 0 && closure.Count < maxFiles)
        {
            var path = pending.Dequeue();
            if (!closure.Add(path)
                || !mergedDictionaries.TryGetValue(path, out var nested))
            {
                continue;
            }
            foreach (var nestedPath in nested)
            {
                pending.Enqueue(nestedPath);
            }
        }
        return closure;
    }

    private static bool TryResolveMergedDictionarySource(
        ImpactPath paths,
        string xamlPath,
        string? source,
        out string relativePath)
    {
        relativePath = "";
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }
        var value = source.Trim();
        if (value.Contains('{')
            || value.Contains('}')
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains(";component", StringComparison.OrdinalIgnoreCase)
            || value.Contains('#')
            || value.Contains('?')
            || Path.IsPathRooted(value))
        {
            return false;
        }
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(xamlPath)!,
                value.Replace('/', Path.DirectorySeparatorChar)));
            return paths.TryRelative(candidate, out relativePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static IEnumerable<Project> OrderedProjects(Solution solution) =>
        solution.Projects.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<SyntaxNode> DeclarationSyntax(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is BaseTypeDeclarationSyntax
                or DelegateDeclarationSyntax
                or MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or DestructorDeclarationSyntax
                or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax
                or EventDeclarationSyntax
                or EventFieldDeclarationSyntax
                or FieldDeclarationSyntax)
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<ISymbol> DeclaredSymbols(
        SemanticModel model,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        switch (declaration)
        {
            case TypeDeclarationSyntax typeDeclaration:
                if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
                {
                    yield break;
                }
                yield return typeSymbol;
                if (typeDeclaration.ParameterList is not null)
                {
                    foreach (var constructor in typeSymbol.InstanceConstructors
                                 .Where(constructor => constructor.DeclaringSyntaxReferences.Any(reference =>
                                     reference.SyntaxTree == typeDeclaration.SyntaxTree
                                     && reference.Span == typeDeclaration.Span)))
                    {
                        yield return constructor;
                    }
                }
                yield break;
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is { } fieldSymbol)
                    {
                        yield return fieldSymbol;
                    }
                }
                yield break;
            case EventFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is { } eventSymbol)
                    {
                        yield return eventSymbol;
                    }
                }
                yield break;
            default:
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is { } symbol)
                {
                    yield return symbol;
                }
                yield break;
        }
    }

    private static bool IsSupportedDefinition(ISymbol symbol) =>
        symbol is INamedTypeSymbol
            or IMethodSymbol
            or IPropertySymbol
            or IFieldSymbol
            or IEventSymbol;

    private static ISymbol? BestSymbol(SymbolInfo info) =>
        info.Symbol?.OriginalDefinition
        ?? info.CandidateSymbols
            .Select(symbol => symbol.OriginalDefinition)
            .OrderBy(SymbolIdentity, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsHarnessRegistration(SimpleNameSyntax name)
    {
        var variable = name.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        return variable is not null
            && variable.Identifier.ValueText.Contains("test", StringComparison.OrdinalIgnoreCase)
            && name.Ancestors().Any(node => node is InitializerExpressionSyntax or CollectionExpressionSyntax);
    }

    private static IEnumerable<string> EnumerateXamlFiles(
        string root,
        int maxDirectories,
        CancellationToken cancellationToken,
        Action onPartial)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > maxDirectories)
            {
                onPartial();
                yield break;
            }
            var directory = pending.Dequeue();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.xaml", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
            {
                onPartial();
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
            foreach (var child in children)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or FileNotFoundException)
                {
                    onPartial();
                    continue;
                }
                var name = Path.GetFileName(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    onPartial();
                    continue;
                }
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pending.Enqueue(child);
            }
        }
    }

    private static bool IsBuildArtifactRelativePath(string path) =>
        path.Split('/').Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsGeneratedDocumentName(string name) =>
        name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Microsoft.NET.Test.Sdk.Program.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeXamlKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 160
        && !value.Any(char.IsControl)
        && !value.Contains("://", StringComparison.Ordinal)
        && !value.Contains(":\\", StringComparison.Ordinal)
        && !value.StartsWith(@"\\", StringComparison.Ordinal);

    private static ImpactEvidence XmlEvidence(XObject value, string relativePath)
    {
        var line = value as IXmlLineInfo;
        return new ImpactEvidence(
            relativePath,
            line?.HasLineInfo() == true ? line.LineNumber : null,
            line?.HasLineInfo() == true ? line.LinePosition : null);
    }

    private static ImpactEvidence Evidence(Location location, string relativePath)
    {
        var span = location.GetLineSpan().StartLinePosition;
        return new ImpactEvidence(relativePath, span.Line + 1, span.Character + 1);
    }

    internal static string SymbolIdentity(ISymbol symbol)
    {
        var original = symbol.OriginalDefinition;
        var assembly = original.ContainingAssembly?.Identity.Name ?? "";
        var documentationId = original.GetDocumentationCommentId();
        var display = documentationId ?? original.ToDisplayString(QualifiedFormat);
        return $"{assembly}|{original.Kind}|{display}";
    }

    internal static string QualifiedName(ISymbol symbol) =>
        symbol.OriginalDefinition.ToDisplayString(QualifiedFormat);

    [GeneratedRegex(@"\{(?:StaticResource|DynamicResource)\s+(?<key>[^},\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex XamlResourceUsageRegex();
}
