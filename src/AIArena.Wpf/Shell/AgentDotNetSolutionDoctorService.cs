using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Services;

using StructuredDotNetResult = AIArena.Core.Models.DotNetCommandResult;

namespace AIArena.Wpf;

/// <summary>
/// Presents the Core .NET workspace model to Agent without moving discovery,
/// command planning, or output parsing into the WPF composition layer.
/// </summary>
internal static class AgentDotNetSolutionDoctorService
{
    private const int MaxProfileCharacters = 3_200;
    private const int MaxPromptPacketCharacters = 4_000;
    private const int MaxDiagnosticMessageCharacters = 260;
    private const int MaxExactDiffCharacters = 24_000;
    private const long MaxExactDiffExistingFileBytes = 16 * 1024;
    internal const int MaxSuggestedCommandCharacters = 600;

    internal static DotNetWorkspaceSnapshot CreateUnavailableSnapshot(string workspaceRoot, Exception exception)
    {
        var workspaceName = string.IsNullOrWhiteSpace(workspaceRoot)
            ? "workspace"
            : Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new(
            workspaceName,
            [],
            [],
            [],
            [
                new DotNetWorkspaceDiagnostic(
                    "DNW900",
                    DotNetWorkspaceDiagnosticSeverity.Warning,
                    $"The .NET workspace scan was unavailable ({exception.GetType().Name}); use a read-only inspection before choosing a command.")
            ],
            IsPartial: true,
            ScanLimitReached: false);
    }

    internal static string FormatWorkspaceProfile(
        string filesystemProfile,
        DotNetWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Solutions.Count == 0
            && snapshot.Projects.Count == 0
            && snapshot.Diagnostics.Count == 0
            && snapshot.Findings.Count == 0)
        {
            return filesystemProfile;
        }

        var state = snapshot.IsPartial ? "partial" : "ready";
        var lines = new List<string>
        {
            filesystemProfile.Trim(),
            "",
            $".NET Solution Doctor: {snapshot.Solutions.Count.ToString(CultureInfo.InvariantCulture)} solution(s), {snapshot.Projects.Count.ToString(CultureInfo.InvariantCulture)} project(s), {state}.",
            $"Projects: {FormatProjects(snapshot.Projects)}"
        };

        var verification = RecommendedVerificationPlans(snapshot, maximum: 5);
        if (verification.Count > 0)
        {
            lines.Add("Project-correct verification actions (PowerShell; stage through preview):");
            lines.AddRange(verification.Select(plan => $"- {FormatPowerShellInvocation(plan)}"));
        }

        var restore = snapshot.CommandPlans.FirstOrDefault(plan => plan.Kind == DotNetCommandKind.Restore);
        if (restore is not null)
        {
            lines.Add($"Restore (PowerShell; separate approval; may use package sources): {FormatPowerShellInvocation(restore)}");
        }

        foreach (var finding in snapshot.Findings.Take(3))
        {
            var path = string.IsNullOrWhiteSpace(finding.PrimaryRelativePath) ? "" : $" [{finding.PrimaryRelativePath}]";
            lines.Add($".NET finding {finding.Severity}: {finding.Code}{path} — {finding.Title}. {finding.Summary}");
        }

        foreach (var diagnostic in snapshot.Diagnostics.Take(3))
        {
            var path = string.IsNullOrWhiteSpace(diagnostic.RelativePath) ? "" : $" [{diagnostic.RelativePath}]";
            lines.Add($".NET discovery {diagnostic.Severity}: {diagnostic.Code}{path} — {diagnostic.Message}");
        }

        return ShellUiHelpers.Truncate(
            string.Join(Environment.NewLine, lines.Where(line => line is not null)),
            MaxProfileCharacters,
            ShellUiHelpers.TruncatedNoticeSuffix);
    }

    internal static StructuredDotNetResult? TryParseCommandResult(
        DotNetWorkspaceSnapshot? snapshot,
        string workspaceRoot,
        AgentCommandResult result)
    {
        if (snapshot is null
            || string.IsNullOrWhiteSpace(workspaceRoot)
            || !Directory.Exists(workspaceRoot))
        {
            return null;
        }

        var plan = FindCommandPlan(snapshot, result.Command);
        if (plan is null)
        {
            return null;
        }

        var parser = new DotNetOutputParser();
        return parser.Parse(
            workspaceRoot,
            plan,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.Canceled,
            rawOutputReferenceId: $"agent-command-{result.ExitCode.ToString(CultureInfo.InvariantCulture)}");
    }

    internal static DotNetCommandPlan? FindCommandPlan(
        DotNetWorkspaceSnapshot snapshot,
        string command)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryTokenizeCommand(command, out var tokens)
            || tokens.Count < 2
            || !IsDotNetExecutable(tokens[0])
            || !TryGetDotNetCommandKind(tokens[1], out var kind))
        {
            return null;
        }

        var candidates = snapshot.CommandPlans
            .Where(plan => plan.Kind == kind)
            .ToArray();
        var actualArguments = tokens.Skip(1).ToArray();
        var exact = candidates
            .Where(plan => ArgumentsMatchPlan(actualArguments, plan))
            .ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        if (exact.Length > 1)
        {
            return null;
        }

        // Focused retries are the only supported semantic extension to a
        // canonical command plan. Core generates this exact suffix after a
        // conventional test failure, and the FQN is validated before it can
        // become structured identity.
        var focused = candidates
            .Where(plan => FocusedTestArgumentsMatch(actualArguments, plan))
            .ToArray();
        if (focused.Length != 1)
        {
            return null;
        }

        var focusedPlan = focused[0] with
        {
            Id = $"{focused[0].Id}:focused-test",
            Arguments = actualArguments,
            Description = $"{focused[0].Description} (focused retry)"
        };
        return focusedPlan with
        {
            DisplayInvocation = FormatPowerShellInvocation(focusedPlan)
        };
    }

    internal static IReadOnlyList<DotNetCommandPlan> RecommendedVerificationPlans(
        DotNetWorkspaceSnapshot? snapshot,
        int maximum = 5)
    {
        if (snapshot is null || maximum <= 0)
        {
            return [];
        }

        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairedPlans = new List<DotNetCommandPlan>(Math.Min(maximum, snapshot.CommandPlans.Count));
        var pairedProjectTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var testActions = snapshot.CommandPlans
            .Where(plan => plan.TargetKind == DotNetCommandTargetKind.Project
                && snapshot.Projects.Any(project =>
                    NormalizePathToken(project.RelativePath).Equals(
                        NormalizePathToken(plan.TargetRelativePath),
                        StringComparison.OrdinalIgnoreCase)
                    && ((plan.Kind == DotNetCommandKind.Test && project.IsConventionalTestProject)
                        || (plan.Kind == DotNetCommandKind.Run && project.IsExecutableTestHarness))))
            .ToArray();
        foreach (var action in testActions)
        {
            var actionTarget = NormalizePathToken(action.TargetRelativePath);
            var requiresProjectBuild = !pairedProjectTargets.Contains(actionTarget);
            var requiredSlots = requiresProjectBuild ? 2 : 1;
            if (pairedPlans.Count + requiredSlots > maximum || selectedIds.Contains(action.Id))
            {
                continue;
            }

            if (requiresProjectBuild)
            {
                var projectBuild = snapshot.CommandPlans.FirstOrDefault(plan =>
                    plan.Kind == DotNetCommandKind.Build
                    && plan.TargetKind == DotNetCommandTargetKind.Project
                    && NormalizePathToken(plan.TargetRelativePath).Equals(actionTarget, StringComparison.OrdinalIgnoreCase)
                    && !selectedIds.Contains(plan.Id)
                    && !plan.Id.Equals(action.Id, StringComparison.OrdinalIgnoreCase));
                if (projectBuild is null)
                {
                    continue;
                }

                selectedIds.Add(projectBuild.Id);
                pairedPlans.Add(projectBuild);
                pairedProjectTargets.Add(actionTarget);
            }

            selectedIds.Add(action.Id);
            pairedPlans.Add(action);
        }

        var solutionBuilds = new List<DotNetCommandPlan>();
        var solutionCoveredProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solutionBuild in snapshot.CommandPlans.Where(plan =>
                     plan.Kind == DotNetCommandKind.Build
                     && plan.TargetKind == DotNetCommandTargetKind.Solution))
        {
            if (pairedPlans.Count + solutionBuilds.Count >= maximum || selectedIds.Contains(solutionBuild.Id))
            {
                continue;
            }

            var matchingSolutions = snapshot.Solutions
                .Where(solution =>
                    !solution.IsPartial
                    && NormalizePathToken(solution.RelativePath).Equals(
                        NormalizePathToken(solutionBuild.TargetRelativePath),
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var solutionProjects = matchingSolutions
                .SelectMany(solution => solution.ProjectRelativePaths)
                .Select(NormalizePathToken)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (matchingSolutions.Length == 0 || solutionProjects.IsSubsetOf(solutionCoveredProjects))
            {
                continue;
            }

            selectedIds.Add(solutionBuild.Id);
            solutionBuilds.Add(solutionBuild);
            solutionCoveredProjects.UnionWith(solutionProjects);
        }

        var standaloneProjectBuilds = new List<DotNetCommandPlan>();
        var selectedProjectTargets = new HashSet<string>(
            pairedProjectTargets,
            StringComparer.OrdinalIgnoreCase);
        foreach (var projectBuild in snapshot.CommandPlans.Where(plan =>
                     plan.Kind == DotNetCommandKind.Build
                     && plan.TargetKind == DotNetCommandTargetKind.Project))
        {
            if (pairedPlans.Count + solutionBuilds.Count + standaloneProjectBuilds.Count >= maximum)
            {
                break;
            }

            var projectTarget = NormalizePathToken(projectBuild.TargetRelativePath);
            if (selectedIds.Contains(projectBuild.Id) || selectedProjectTargets.Contains(projectTarget))
            {
                continue;
            }

            selectedIds.Add(projectBuild.Id);
            selectedProjectTargets.Add(projectTarget);
            standaloneProjectBuilds.Add(projectBuild);
        }

        return solutionBuilds
            .Concat(pairedPlans)
            .Concat(standaloneProjectBuilds)
            .ToArray();
    }

    internal static DotNetNarrowedRetryPlan? CreateNarrowedRetry(
        DotNetWorkspaceSnapshot? snapshot,
        StructuredDotNetResult? result)
    {
        return snapshot is null || result is null
            ? null
            : new DotNetWorkspaceIntelligenceService().CreateNarrowedRetryPlan(snapshot, result);
    }

    internal static DotNetRepairProposal CreateRepairProposal(
        DotNetWorkspaceSnapshot? snapshot,
        StructuredDotNetResult? result,
        DotNetRepairImpactHint? impactHint = null)
    {
        return new DotNetRepairLoopService().CreateProposal(snapshot, result, impactHint);
    }

    internal static DotNetRepairEvidenceSnapshot CaptureRepairEvidence(
        DotNetWorkspaceSnapshot? snapshot,
        StructuredDotNetResult? result)
    {
        return new DotNetRepairLoopService().CaptureEvidence(snapshot, result);
    }

    internal static DotNetRepairComparison CompareRepairEvidence(
        DotNetRepairEvidenceSnapshot baseline,
        DotNetRepairEvidenceSnapshot current,
        bool verificationSucceeded,
        bool wasCancelled)
    {
        return new DotNetRepairLoopService().Compare(
            baseline,
            current,
            verificationSucceeded,
            wasCancelled);
    }

    internal static bool RepairProposalIsCurrent(
        DotNetRepairProposal proposal,
        DotNetWorkspaceSnapshot? snapshot,
        StructuredDotNetResult? result)
    {
        return new DotNetRepairLoopService().IsProposalCurrent(proposal, snapshot, result);
    }

    internal static string FormatRepairProposal(DotNetRepairProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var lines = new List<string>
        {
            $"Repair case: {proposal.Code} · {proposal.Id}",
            $"Availability: {proposal.Availability}",
            $"Explanation: {proposal.Explanation}",
            "Deterministic diff intent (not apply-ready):"
        };
        if (proposal.DiffPreview.Count == 0)
        {
            lines.Add("- No bounded diff intent is available.");
        }
        else
        {
            foreach (var hunk in proposal.DiffPreview)
            {
                lines.Add($"--- a/{hunk.RelativePath}");
                lines.Add($"+++ b/{hunk.RelativePath}");
                lines.Add($"@@ {hunk.Location} @@");
                lines.Add($"- {hunk.Before}");
                lines.Add($"+ {hunk.After}");
                lines.Add($"  Review boundary: {hunk.Rationale}");
            }
        }

        lines.Add("Focused verification (each command still requires preview and explicit approval):");
        var plans = proposal.Verification.BuildPlans
            .Concat(proposal.Verification.TestPlans)
            .ToArray();
        lines.AddRange(plans.Length == 0
            ? ["- No complete typed verification command is available."]
            : plans.Select(plan => $"- {FormatPowerShellInvocation(plan)}"));
        lines.Add($"Focused test evidence: {proposal.Verification.TestEvidenceState}. {proposal.Verification.TestSelectionBasis}");
        if (!string.IsNullOrWhiteSpace(proposal.Limitation))
        {
            lines.Add($"Limitation: {proposal.Limitation}");
        }

        return ShellUiHelpers.Truncate(
            string.Join(Environment.NewLine, lines),
            MaxPromptPacketCharacters,
            ShellUiHelpers.TruncatedNoticeSuffix);
    }

    internal static AgentRepairExactDiffPreview BuildExactFileDiff(
        string workspaceRoot,
        AgentWorkspaceCoordinator.AgentFileSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return AgentRepairExactDiffPreview.Unavailable(
                "Exact proposed diff unavailable because the workspace root is not available.");
        }

        var output = new StringBuilder();
        var paths = new List<string>();
        var baselines = new List<AgentRepairFileBaseline>();
        foreach (var file in suggestion.Files)
        {
            if (!TryResolveBoundedDiffTarget(workspaceRoot, file.Path, out var relativePath, out var fullPath, out var error))
            {
                return AgentRepairExactDiffPreview.Unavailable(error);
            }

            string before;
            byte[]? beforeBytes;
            if (!File.Exists(fullPath))
            {
                before = "";
                beforeBytes = null;
            }
            else
            {
                try
                {
                    var info = new FileInfo(fullPath);
                    if (info.Length > MaxExactDiffExistingFileBytes)
                    {
                        return AgentRepairExactDiffPreview.Unavailable(
                            $"Exact proposed diff unavailable because {relativePath} exceeds the bounded read limit.");
                    }

                    var bytes = File.ReadAllBytes(fullPath);
                    beforeBytes = bytes;
                    before = new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false,
                            throwOnInvalidBytes: true)
                        .GetString(bytes)
                        .TrimStart('\uFEFF');
                }
                catch (DecoderFallbackException)
                {
                    return AgentRepairExactDiffPreview.Unavailable(
                        $"Exact proposed diff unavailable because {relativePath} is not bounded UTF-8 text.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return AgentRepairExactDiffPreview.Unavailable(
                        $"Exact proposed diff unavailable because {relativePath} could not be read ({exception.GetType().Name}).");
                }
            }

            AppendWholeFileUnifiedDiff(output, relativePath, before, file.Content);
            if (output.Length > MaxExactDiffCharacters)
            {
                return AgentRepairExactDiffPreview.Unavailable(
                    "Exact proposed diff unavailable because the bounded preview limit was exceeded.");
            }

            paths.Add(relativePath);
            baselines.Add(new(
                relativePath,
                Exists: beforeBytes is not null,
                Sha256: beforeBytes is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(beforeBytes))));
        }

        return paths.Count == 0
            ? AgentRepairExactDiffPreview.Unavailable(
                "Exact proposed diff unavailable because Builder supplied no bounded file snippets.")
            : new(
                Available: true,
                output.ToString().TrimEnd(),
                $"Exact before/after text preview for {paths.Count.ToString(CultureInfo.InvariantCulture)} file(s).",
                paths,
                baselines);
    }

    internal static string BuildGuardedRepairFileWriteCommand(
        AgentWorkspaceCoordinator.AgentFileSuggestion suggestion,
        AgentRepairExactDiffPreview preview)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Available
            || suggestion.Files.Count != preview.Baselines.Count
            || suggestion.Files.Any(file =>
                !AgentCommandProposalService.TryNormalizeSuggestedFilePath(file.Path, out var normalizedPath)
                || !normalizedPath.Equals(
                    file.Path.Replace('\\', '/'),
                    StringComparison.Ordinal))
            || !suggestion.Files.Select(file => file.Path).SequenceEqual(
                preview.Baselines.Select(baseline => baseline.RelativePath),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A guarded repair command requires an exact diff baseline for every file.");
        }

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$cwd = (Get-Location).Path",
            "$workspaceRootPath = [System.IO.Path]::GetFullPath($cwd).TrimEnd([char]92, [char]47)",
            "$workspaceRootItem = Get-Item -LiteralPath $workspaceRootPath -Force",
            "if (($workspaceRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {",
            "    throw \"Refusing to write through a workspace-root link.\"",
            "}",
            "$workspaceRoot = $workspaceRootPath + [System.IO.Path]::DirectorySeparatorChar",
            "$utf8NoBom = New-Object System.Text.UTF8Encoding $false",
            "$files = @("
        };
        for (var index = 0; index < suggestion.Files.Count; index++)
        {
            var file = suggestion.Files[index];
            var baseline = preview.Baselines[index];
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content));
            lines.Add(
                $"    @{{ Path = '{EscapePowerShellSingleQuoted(file.Path)}'; ExpectedExists = ${baseline.Exists.ToString().ToLowerInvariant()}; ExpectedSha256 = '{baseline.Sha256 ?? ""}'; Base64 = '{base64}' }}");
        }

        lines.AddRange(
        [
            ")",
            "foreach ($file in $files) {",
            "    $targetPath = Join-Path -Path $cwd -ChildPath $file.Path",
            "    $fullPath = [System.IO.Path]::GetFullPath($targetPath)",
            "    if (-not $fullPath.StartsWith($workspaceRoot, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "        throw \"Refusing to write outside workspace: $($file.Path)\"",
            "    }",
            "    $probePath = $fullPath",
            "    while (-not [string]::IsNullOrWhiteSpace($probePath) -and $probePath.StartsWith($workspaceRoot, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "        if (Test-Path -LiteralPath $probePath) {",
            "            $probeItem = Get-Item -LiteralPath $probePath -Force",
            "            if (($probeItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {",
            "                throw \"Refusing to write through a workspace link: $($file.Path)\"",
            "            }",
            "        }",
            "        $probePath = Split-Path -Parent $probePath",
            "    }",
            "    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf",
            "    if ($exists -ne [bool]$file.ExpectedExists) {",
            "        throw \"Repair preview is stale for $($file.Path); inspect and preview again.\"",
            "    }",
            "    if ($exists) {",
            "        $actualSha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash",
            "        if (-not $actualSha256.Equals([string]$file.ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "            throw \"Repair preview is stale for $($file.Path); inspect and preview again.\"",
            "        }",
            "    }",
            "}",
            "foreach ($file in $files) {",
            "    $targetPath = Join-Path -Path $cwd -ChildPath $file.Path",
            "    $fullPath = [System.IO.Path]::GetFullPath($targetPath)",
            "    $parent = Split-Path -Parent $fullPath",
            "    if (-not [string]::IsNullOrWhiteSpace($parent)) {",
            "        New-Item -ItemType Directory -Path $parent -Force | Out-Null",
            "    }",
            "    $content = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($file.Base64))",
            "    [System.IO.File]::WriteAllText($fullPath, $content, $utf8NoBom)",
            "}",
            "Write-Host (\"Applied reviewed repair to {0} file(s): {1}\" -f $files.Count, (($files | ForEach-Object { $_.Path }) -join ', '))"
        ]);
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatPowerShellInvocation(DotNetCommandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var invocation = $"{plan.FileName} {string.Join(' ', plan.Arguments.Select(QuotePowerShellArgument))}".TrimEnd();
        return invocation.Length <= MaxSuggestedCommandCharacters
            ? invocation
            : $"Typed {plan.Kind} action omitted because its command exceeds the safe prompt limit.";
    }

    internal static string FormatResultPacket(
        DotNetWorkspaceSnapshot? snapshot,
        StructuredDotNetResult? result)
    {
        if (result is null)
        {
            return "No structured .NET command evidence is available.";
        }

        var lines = new List<string>
        {
            ".NET structured evidence (bounded; raw stdout/stderr remains available):",
            $"Action: {result.Command.Kind} {result.Command.TargetKind} {result.Command.TargetRelativePath}",
            $"Outcome: {(result.Succeeded ? "passed" : result.WasCancelled ? "cancelled" : "failed")}; exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}; {result.ErrorCount.ToString(CultureInfo.InvariantCulture)} error(s); {result.WarningCount.ToString(CultureInfo.InvariantCulture)} warning(s)."
        };

        if (result.TestTotals is { } totals)
        {
            lines.Add($"Tests: {totals.Passed.ToString(CultureInfo.InvariantCulture)} passed, {totals.Failed.ToString(CultureInfo.InvariantCulture)} failed, {totals.Skipped.ToString(CultureInfo.InvariantCulture)} skipped, {totals.Total.ToString(CultureInfo.InvariantCulture)} total.");
        }

        if (result.Findings.Count > 0)
        {
            lines.Add("Root-cause findings:");
            foreach (var finding in result.Findings.Take(8))
            {
                var path = string.IsNullOrWhiteSpace(finding.PrimaryRelativePath) ? "" : $" [{finding.PrimaryRelativePath}]";
                lines.Add($"- {finding.Severity} {finding.Code}{path}: {finding.Title}. {ShellUiHelpers.Truncate(finding.Summary, MaxDiagnosticMessageCharacters, ShellUiHelpers.TruncatedNoticeSuffix)}");
                foreach (var evidence in finding.RootCauseChain.Take(4))
                {
                    var evidencePath = string.IsNullOrWhiteSpace(evidence.RelativePath) ? "" : $" [{evidence.RelativePath}]";
                    var relatedPath = string.IsNullOrWhiteSpace(evidence.RelatedRelativePath) ? "" : $" -> {evidence.RelatedRelativePath}";
                    var value = string.IsNullOrWhiteSpace(evidence.Value) ? "" : $" ({evidence.Value})";
                    lines.Add($"  {evidence.Sequence.ToString(CultureInfo.InvariantCulture)}. {evidence.Label}{evidencePath}{relatedPath}{value}");
                }
            }
        }

        if (result.Diagnostics.Count > 0)
        {
            lines.Add("Diagnostics:");
            foreach (var diagnostic in result.Diagnostics.Take(12))
            {
                var location = FormatDiagnosticLocation(diagnostic);
                lines.Add($"- {diagnostic.Severity} {diagnostic.Code}{location}: {ShellUiHelpers.Truncate(diagnostic.Message, MaxDiagnosticMessageCharacters, ShellUiHelpers.TruncatedNoticeSuffix)}");
            }
        }

        if (result.FailingTests.Count > 0)
        {
            lines.Add("Failing tests:");
            foreach (var failure in result.FailingTests.Take(8))
            {
                var project = string.IsNullOrWhiteSpace(failure.ProjectRelativePath) ? "" : $" [{failure.ProjectRelativePath}]";
                var detail = string.IsNullOrWhiteSpace(failure.Detail)
                    ? ""
                    : $": {ShellUiHelpers.Truncate(failure.Detail, MaxDiagnosticMessageCharacters, ShellUiHelpers.TruncatedNoticeSuffix)}";
                lines.Add($"- {failure.Name}{project}{detail}");
            }
        }

        var retry = CreateNarrowedRetry(snapshot, result);
        if (retry is not null)
        {
            lines.Add($"Narrowed retry: {FormatPowerShellInvocation(retry.Command)}");
            lines.Add($"Retry rationale: {retry.Reason}");
        }

        if (result.StructuredEvidenceLimitReached)
        {
            lines.Add("Structured evidence limit reached; consult the preserved raw output for the remainder.");
        }

        return ShellUiHelpers.Truncate(
            string.Join(Environment.NewLine, lines),
            MaxPromptPacketCharacters,
            ShellUiHelpers.TruncatedNoticeSuffix);
    }

    internal static string WorkspaceEvidenceState(DotNetWorkspaceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "Scanning";
        }

        if (snapshot.Projects.Count == 0)
        {
            return "No .NET projects";
        }

        var suffix = snapshot.IsPartial ? " · partial" : "";
        var findings = snapshot.Findings.Count == 0
            ? ""
            : $" · {snapshot.Findings.Count.ToString(CultureInfo.InvariantCulture)} finding(s)";
        return $"{snapshot.Projects.Count.ToString(CultureInfo.InvariantCulture)} projects{findings}{suffix}";
    }

    internal static string WorkspaceEvidenceBrushKey(DotNetWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Findings.Any(finding =>
                finding.Severity == DotNetWorkspaceDiagnosticSeverity.Error)
            || snapshot.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DotNetWorkspaceDiagnosticSeverity.Error))
        {
            return "DangerBorderBrush";
        }

        if (snapshot.IsPartial
            || snapshot.Findings.Any(finding =>
                finding.Severity == DotNetWorkspaceDiagnosticSeverity.Warning)
            || snapshot.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DotNetWorkspaceDiagnosticSeverity.Warning))
        {
            return "PrimaryBorderBrush";
        }

        return "AssistBorderBrush";
    }

    internal static string ResultEvidenceState(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.WasCancelled)
        {
            return "Cancelled";
        }

        if (result.Findings.Any(finding =>
                finding.Category == DotNetFindingCategory.TestDiscoveryFailure))
        {
            return "Test discovery failed";
        }

        return result.Succeeded
            ? result.WarningCount == 0 ? "Passed" : $"Passed · {result.WarningCount.ToString(CultureInfo.InvariantCulture)} warnings"
            : $"{result.ErrorCount.ToString(CultureInfo.InvariantCulture)} errors · {result.WarningCount.ToString(CultureInfo.InvariantCulture)} warnings";
    }

    internal static string ResultEvidenceBrushKey(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.WasCancelled)
        {
            return "PrimaryBorderBrush";
        }

        if (!result.Succeeded
            || result.Findings.Any(finding =>
                finding.Severity == DotNetWorkspaceDiagnosticSeverity.Error)
            || result.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error))
        {
            return "DangerBorderBrush";
        }

        if (result.WarningCount > 0
            || result.Findings.Any(finding =>
                finding.Severity == DotNetWorkspaceDiagnosticSeverity.Warning)
            || result.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == DotNetBuildDiagnosticSeverity.Warning))
        {
            return "PrimaryBorderBrush";
        }

        return "AssistBorderBrush";
    }

    internal static string TestEvidenceState(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Findings.Any(finding =>
                finding.Category == DotNetFindingCategory.TestDiscoveryFailure))
        {
            return "Discovery failed";
        }

        if (result.TestTotals is not { } totals)
        {
            return result.FailingTests.Count == 0
                ? "No test totals"
                : $"{result.FailingTests.Count.ToString(CultureInfo.InvariantCulture)} failed";
        }

        return $"{totals.Passed.ToString(CultureInfo.InvariantCulture)} passed · {totals.Failed.ToString(CultureInfo.InvariantCulture)} failed";
    }

    internal static string PrimaryFailureState(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error) is { } diagnostic)
        {
            var path = string.IsNullOrWhiteSpace(diagnostic.RelativePath) ? "" : $"{diagnostic.RelativePath}:";
            return ShellUiHelpers.Truncate($"{diagnostic.Code} · {path}{diagnostic.Line?.ToString(CultureInfo.InvariantCulture) ?? "?"}", 72, ShellUiHelpers.TruncatedNoticeSuffix);
        }

        if (result.Findings.FirstOrDefault(finding => finding.Severity == DotNetWorkspaceDiagnosticSeverity.Error) is { } finding)
        {
            var path = string.IsNullOrWhiteSpace(finding.PrimaryRelativePath) ? "" : $" · {finding.PrimaryRelativePath}";
            return ShellUiHelpers.Truncate($"{finding.Code}{path}", 72, ShellUiHelpers.TruncatedNoticeSuffix);
        }

        return result.FailingTests.FirstOrDefault() is { } failure
            ? ShellUiHelpers.Truncate(failure.Name, 72, ShellUiHelpers.TruncatedNoticeSuffix)
            : "See raw output";
    }

    private static string FormatProjects(IReadOnlyList<DotNetProjectInfo> projects)
    {
        if (projects.Count == 0)
        {
            return "none discovered";
        }

        var values = projects.Take(8).Select(project =>
        {
            var framework = project.TargetFrameworks.Count == 0 ? "TFM unresolved" : string.Join("/", project.TargetFrameworks);
            var kind = project.IsExecutableTestHarness
                ? "executable tests"
                : project.IsConventionalTestProject
                    ? "test SDK"
                    : project.UseWpf
                        ? "WPF"
                        : project.OutputType.ToString();
            return $"{project.Name} [{framework}; {kind}]";
        });
        var suffix = projects.Count > 8 ? $", +{(projects.Count - 8).ToString(CultureInfo.InvariantCulture)} more" : "";
        return $"{string.Join(", ", values)}{suffix}";
    }

    private static string FormatDiagnosticLocation(DotNetBuildDiagnostic diagnostic)
    {
        var location = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(diagnostic.RelativePath))
        {
            location.Append(" [").Append(diagnostic.RelativePath);
            if (diagnostic.Line is not null)
            {
                location.Append('(').Append(diagnostic.Line.Value.ToString(CultureInfo.InvariantCulture));
                if (diagnostic.Column is not null)
                {
                    location.Append(',').Append(diagnostic.Column.Value.ToString(CultureInfo.InvariantCulture));
                }

                location.Append(')');
            }

            location.Append(']');
        }
        else if (!string.IsNullOrWhiteSpace(diagnostic.ProjectRelativePath))
        {
            location.Append(" [").Append(diagnostic.ProjectRelativePath).Append(']');
        }

        return location.ToString();
    }

    private static bool TryGetDotNetCommandKind(string verb, out DotNetCommandKind kind)
    {
        kind = default;
        kind = verb.ToLowerInvariant() switch
        {
            "restore" => DotNetCommandKind.Restore,
            "build" => DotNetCommandKind.Build,
            "test" => DotNetCommandKind.Test,
            "run" => DotNetCommandKind.Run,
            _ => default
        };
        return verb.Equals("restore", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("build", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("test", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("run", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotNetExecutable(string value)
    {
        return value.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || value.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ArgumentsMatchPlan(
        IReadOnlyList<string> actualArguments,
        DotNetCommandPlan plan)
    {
        if (actualArguments.Count != plan.Arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < actualArguments.Count; index++)
        {
            if (!ArgumentsEquivalent(actualArguments[index], plan.Arguments[index], plan.TargetRelativePath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FocusedTestArgumentsMatch(
        IReadOnlyList<string> actualArguments,
        DotNetCommandPlan plan)
    {
        if (plan.Kind != DotNetCommandKind.Test
            || actualArguments.Count != plan.Arguments.Count + 2)
        {
            return false;
        }

        for (var index = 0; index < plan.Arguments.Count; index++)
        {
            if (!ArgumentsEquivalent(actualArguments[index], plan.Arguments[index], plan.TargetRelativePath))
            {
                return false;
            }
        }

        return actualArguments[^2].Equals("--filter", StringComparison.OrdinalIgnoreCase)
            && IsSafeFullyQualifiedTestFilter(actualArguments[^1]);
    }

    private static bool ArgumentsEquivalent(
        string actual,
        string expected,
        string targetRelativePath)
    {
        if (!expected.Equals(targetRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (Path.IsPathRooted(actual))
        {
            return false;
        }

        var normalized = NormalizePathToken(actual);
        return !normalized.Equals("..", StringComparison.Ordinal)
            && !normalized.StartsWith("../", StringComparison.Ordinal)
            && normalized.Equals(NormalizePathToken(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeFullyQualifiedTestFilter(string value)
    {
        const string prefix = "FullyQualifiedName=";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var testName = value[prefix.Length..];
        return testName.Length is > 0 and <= 512
            && (char.IsLetter(testName[0]) || testName[0] == '_')
            && testName.All(character =>
                char.IsLetterOrDigit(character)
                || character is '_' or '.' or '+' or '`');
    }

    private static string NormalizePathToken(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool TryResolveBoundedDiffTarget(
        string workspaceRoot,
        string suggestedPath,
        out string relativePath,
        out string fullPath,
        out string error)
    {
        relativePath = "";
        fullPath = "";
        error = "";
        if (!AgentCommandProposalService.TryNormalizeSuggestedFilePath(suggestedPath, out relativePath))
        {
            error = "Exact proposed diff unavailable because Builder supplied an unsafe file path.";
            return false;
        }
        try
        {
            var root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                error = "Exact proposed diff unavailable because the workspace root is a link.";
                fullPath = "";
                return false;
            }

            fullPath = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = $"{root}{Path.DirectorySeparatorChar}";
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "Exact proposed diff unavailable because the target escaped the workspace.";
                return false;
            }

            for (var probe = fullPath;
                 !string.IsNullOrWhiteSpace(probe)
                 && probe.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
                 probe = Path.GetDirectoryName(probe) ?? "")
            {
                if ((File.Exists(probe) || Directory.Exists(probe))
                    && (File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"Exact proposed diff unavailable because {relativePath} crosses a workspace link.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"Exact proposed diff unavailable because the target path could not be inspected ({exception.GetType().Name}).";
            fullPath = "";
            return false;
        }
    }

    private static void AppendWholeFileUnifiedDiff(
        StringBuilder output,
        string relativePath,
        string before,
        string after)
    {
        var beforeLines = SplitDiffLines(before, out var beforeTrailingNewline);
        var afterLines = SplitDiffLines(after, out var afterTrailingNewline);
        output.Append("--- a/").AppendLine(relativePath);
        output.Append("+++ b/").AppendLine(relativePath);
        output.Append("@@ -")
            .Append(beforeLines.Count == 0 ? "0,0" : $"1,{beforeLines.Count.ToString(CultureInfo.InvariantCulture)}")
            .Append(" +")
            .Append(afterLines.Count == 0 ? "0,0" : $"1,{afterLines.Count.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine(" @@");
        foreach (var line in beforeLines)
        {
            output.Append('-').AppendLine(line);
        }

        if (beforeLines.Count > 0 && !beforeTrailingNewline)
        {
            output.AppendLine(@"\ No newline at end of original file");
        }

        foreach (var line in afterLines)
        {
            output.Append('+').AppendLine(line);
        }

        if (afterLines.Count > 0 && !afterTrailingNewline)
        {
            output.AppendLine(@"\ No newline at end of proposed file");
        }

        output.AppendLine();
    }

    private static IReadOnlyList<string> SplitDiffLines(
        string value,
        out bool hasTrailingNewline)
    {
        var normalized = (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        hasTrailingNewline = normalized.EndsWith('\n');
        if (normalized.Length == 0)
        {
            return [];
        }

        var lines = normalized.Split('\n').ToList();
        if (hasTrailingNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static bool TryTokenizeCommand(string command, out IReadOnlyList<string> tokens)
    {
        var values = new List<string>();
        tokens = values;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && char.IsWhiteSpace(command[index]))
            {
                index++;
            }

            if (index >= command.Length)
            {
                break;
            }

            if (IsShellOperator(command[index]))
            {
                return false;
            }

            var token = new StringBuilder();
            var quote = command[index];
            if (quote is '\'' or '"')
            {
                index++;
                var closed = false;
                while (index < command.Length)
                {
                    var character = command[index++];
                    if (character == quote)
                    {
                        if (quote == '\'' && index < command.Length && command[index] == '\'')
                        {
                            token.Append('\'');
                            index++;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    if (quote == '"' && IsDynamicShellCharacter(character))
                    {
                        return false;
                    }

                    token.Append(character);
                }

                if (!closed || (index < command.Length && !char.IsWhiteSpace(command[index])))
                {
                    return false;
                }
            }
            else
            {
                while (index < command.Length && !char.IsWhiteSpace(command[index]))
                {
                    var character = command[index];
                    if (IsShellOperator(character) || IsDynamicShellCharacter(character))
                    {
                        return false;
                    }

                    token.Append(character);
                    index++;
                }
            }

            if (token.Length == 0)
            {
                return false;
            }

            values.Add(token.ToString());
        }

        return values.Count > 0;
    }

    private static bool IsShellOperator(char character)
    {
        return character is ';' or '|' or '&' or '<' or '>' or '\r' or '\n';
    }

    private static bool IsDynamicShellCharacter(char character)
    {
        return character is '$' or '`' or '%' or '(' or ')' or '{' or '}' or '[' or ']';
    }

    private static string QuotePowerShellArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
                char.IsLetterOrDigit(character)
                || character is '_' or '.' or '/' or '\\' or ':' or '=' or '+' or '-'))
        {
            return argument;
        }

        return $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}

internal sealed record AgentRepairExactDiffPreview(
    bool Available,
    string Text,
    string Message,
    IReadOnlyList<string> RelativePaths,
    IReadOnlyList<AgentRepairFileBaseline> Baselines)
{
    internal static AgentRepairExactDiffPreview Unavailable(string message)
    {
        return new(false, "", message, [], []);
    }
}

internal sealed record AgentRepairFileBaseline(
    string RelativePath,
    bool Exists,
    string? Sha256);
