using System.Globalization;
using System.IO;
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
            && snapshot.Diagnostics.Count == 0)
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
        int maximum = 4)
    {
        if (snapshot is null || maximum <= 0)
        {
            return [];
        }

        var solutionBuilds = snapshot.CommandPlans
            .Where(plan => plan.Kind == DotNetCommandKind.Build && plan.TargetKind == DotNetCommandTargetKind.Solution);
        var testActions = snapshot.CommandPlans
            .Where(plan => plan.Kind == DotNetCommandKind.Test
                || (plan.Kind == DotNetCommandKind.Run
                    && snapshot.Projects.Any(project =>
                        project.IsExecutableTestHarness
                        && project.RelativePath.Equals(plan.TargetRelativePath, StringComparison.OrdinalIgnoreCase))));

        return solutionBuilds
            .Concat(testActions)
            .DistinctBy(plan => plan.Id, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
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
        return $"{snapshot.Projects.Count.ToString(CultureInfo.InvariantCulture)} projects{suffix}";
    }

    internal static string ResultEvidenceState(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.WasCancelled)
        {
            return "Cancelled";
        }

        return result.Succeeded
            ? result.WarningCount == 0 ? "Passed" : $"Passed · {result.WarningCount.ToString(CultureInfo.InvariantCulture)} warnings"
            : $"{result.ErrorCount.ToString(CultureInfo.InvariantCulture)} errors · {result.WarningCount.ToString(CultureInfo.InvariantCulture)} warnings";
    }

    internal static string TestEvidenceState(StructuredDotNetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
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
        var normalized = relativePath.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
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
