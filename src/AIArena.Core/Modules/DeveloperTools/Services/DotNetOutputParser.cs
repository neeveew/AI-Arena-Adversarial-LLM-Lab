using System.Globalization;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed partial class DotNetOutputParser
{
    public const int DefaultMaximumStructuredDiagnostics = 256;
    public const int DefaultMaximumFailingTests = 64;
    public const int DefaultMaximumParsedCharactersPerStream = 2 * 1024 * 1024;
    public const int MaximumStructuredMessageCharacters = 4_096;
    public const int MaximumStructuredTestNameCharacters = 1_024;

    public DotNetCommandResult Parse(
        string workspaceRoot,
        DotNetCommandPlan command,
        int exitCode,
        string standardOutput,
        string standardError,
        bool wasCancelled = false,
        string? rawOutputReferenceId = null,
        int maximumStructuredDiagnostics = DefaultMaximumStructuredDiagnostics,
        int maximumFailingTests = DefaultMaximumFailingTests,
        int maximumParsedCharactersPerStream = DefaultMaximumParsedCharactersPerStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        var root = Path.GetFullPath(workspaceRoot);
        maximumStructuredDiagnostics = Math.Clamp(maximumStructuredDiagnostics, 1, 10_000);
        maximumFailingTests = Math.Clamp(maximumFailingTests, 1, 10_000);
        maximumParsedCharactersPerStream = Math.Clamp(maximumParsedCharactersPerStream, 4 * 1024, 16 * 1024 * 1024);

        var diagnostics = new List<DotNetBuildDiagnostic>();
        var failingTests = new List<DotNetFailingTest>();
        int? reportedWarnings = null;
        int? reportedErrors = null;
        DotNetTestTotals? testTotals = null;
        var harnessPassedCount = 0;
        var harnessFailedCount = 0;
        var limitReached = standardOutput.Length > maximumParsedCharactersPerStream
            || standardError.Length > maximumParsedCharactersPerStream;

        foreach (var line in EnumerateBoundedLines(standardOutput, maximumParsedCharactersPerStream)
                     .Concat(EnumerateBoundedLines(standardError, maximumParsedCharactersPerStream)))
        {
            if (diagnostics.Count < maximumStructuredDiagnostics
                && TryParseDiagnostic(root, line, out var diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
            else if (diagnostics.Count >= maximumStructuredDiagnostics
                     && DiagnosticCodeRegex().IsMatch(line))
            {
                limitReached = true;
            }

            if (TryParseBuildTotals(line, out var warnings, out var errors))
            {
                reportedWarnings = warnings ?? reportedWarnings;
                reportedErrors = errors ?? reportedErrors;
            }

            if (TryParseTestTotals(line, out var parsedTestTotals))
            {
                testTotals = AddTestTotals(testTotals, parsedTestTotals);
            }

            var harnessFailure = HarnessFailedTestRegex().IsMatch(line);
            if (harnessFailure)
            {
                harnessFailedCount++;
            }

            if (failingTests.Count < maximumFailingTests
                && TryParseFailingTest(root, line, command.TargetRelativePath, out var failingTest))
            {
                if (!failingTests.Any(existing => existing.Name.Equals(failingTest.Name, StringComparison.Ordinal)))
                {
                    failingTests.Add(failingTest);
                }

            }
            else if (failingTests.Count >= maximumFailingTests && IsFailingTestLine(line))
            {
                limitReached = true;
            }

            var harnessPass = HarnessPassedTestRegex().Match(line);
            if (harnessPass.Success)
            {
                harnessPassedCount++;
            }
        }

        if (testTotals is null && harnessPassedCount + harnessFailedCount > 0)
        {
            testTotals = new(
                harnessPassedCount,
                harnessFailedCount,
                0,
                harnessPassedCount + harnessFailedCount);
        }

        var diagnosticWarnings = diagnostics.Count(diagnostic => diagnostic.Severity == DotNetBuildDiagnosticSeverity.Warning);
        var diagnosticErrors = diagnostics.Count(diagnostic => diagnostic.Severity == DotNetBuildDiagnosticSeverity.Error);
        var warningCount = reportedWarnings ?? diagnosticWarnings;
        var errorCount = reportedErrors ?? diagnosticErrors;
        var rawOutput = new DotNetRawOutput(
            string.IsNullOrWhiteSpace(rawOutputReferenceId) ? Guid.NewGuid().ToString("N") : rawOutputReferenceId,
            standardOutput,
            standardError);
        var succeeded = !wasCancelled
            && exitCode == 0
            && errorCount == 0
            && (testTotals?.Failed ?? 0) == 0
            && failingTests.Count == 0;

        return new(
            command,
            exitCode,
            wasCancelled,
            succeeded,
            diagnostics,
            warningCount,
            errorCount,
            testTotals,
            failingTests,
            rawOutput,
            limitReached);
    }

    private static bool TryParseDiagnostic(string root, string line, out DotNetBuildDiagnostic diagnostic)
    {
        diagnostic = null!;
        var match = LocatedDiagnosticRegex().Match(line);
        if (!match.Success)
        {
            match = UnlocatedDiagnosticRegex().Match(line);
        }

        if (!match.Success)
        {
            return false;
        }

        var code = match.Groups["code"].Value.ToUpperInvariant();
        var severity = match.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
            ? DotNetBuildDiagnosticSeverity.Error
            : match.Groups["severity"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
                ? DotNetBuildDiagnosticSeverity.Warning
                : DotNetBuildDiagnosticSeverity.Information;
        var relativePath = SafeDiagnosticFilePath(
            root,
            match.Groups["file"].Success ? match.Groups["file"].Value : null);
        var projectRelativePath = SafeOutputPath(root, match.Groups["project"].Success ? match.Groups["project"].Value : null);
        var lineNumber = ParsePositiveInteger(match.Groups["line"].Value);
        var column = ParsePositiveInteger(match.Groups["column"].Value);
        var message = SanitizeStructuredText(
            root,
            match.Groups["message"].Value.Trim(),
            MaximumStructuredMessageCharacters);

        diagnostic = new(code, severity, message, relativePath, lineNumber, column, projectRelativePath);
        return true;
    }

    private static bool TryParseBuildTotals(string line, out int? warnings, out int? errors)
    {
        warnings = null;
        errors = null;
        var warningMatch = BuildWarningTotalRegex().Match(line);
        var errorMatch = BuildErrorTotalRegex().Match(line);
        if (!warningMatch.Success && !errorMatch.Success)
        {
            return false;
        }

        if (warningMatch.Success)
        {
            warnings = ParseNonNegativeInteger(warningMatch.Groups["count"].Value);
        }

        if (errorMatch.Success)
        {
            errors = ParseNonNegativeInteger(errorMatch.Groups["count"].Value);
        }

        return true;
    }

    private static bool TryParseTestTotals(string line, out DotNetTestTotals totals)
    {
        totals = null!;
        var match = VstestTotalsRegex().Match(line);
        if (!match.Success)
        {
            match = CompactTestTotalsRegex().Match(line);
        }

        if (!match.Success)
        {
            match = ModernTestTotalsRegex().Match(line);
        }

        if (!match.Success)
        {
            return false;
        }

        var passed = ParseNonNegativeInteger(match.Groups["passed"].Value);
        var failed = ParseNonNegativeInteger(match.Groups["failed"].Value);
        var skipped = ParseNonNegativeInteger(match.Groups["skipped"].Value);
        var total = match.Groups["total"].Success
            ? ParseNonNegativeInteger(match.Groups["total"].Value)
            : passed + failed + skipped;
        totals = new(passed, failed, skipped, total);
        return true;
    }

    private static bool TryParseFailingTest(
        string root,
        string line,
        string fallbackProjectRelativePath,
        out DotNetFailingTest failingTest)
    {
        failingTest = null!;
        var match = FailedTestRegex().Match(line);
        if (!match.Success)
        {
            match = XunitFailedTestRegex().Match(line);
        }

        if (!match.Success)
        {
            match = HarnessFailedTestRegex().Match(line);
        }

        if (!match.Success)
        {
            match = XunitSuffixFailedTestRegex().Match(line);
        }

        if (!match.Success)
        {
            return false;
        }

        var name = SanitizeStructuredText(
            root,
            match.Groups["name"].Value.Trim(),
            MaximumStructuredTestNameCharacters);
        if (name.Length == 0 || name.StartsWith("- Failed:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var projectPath = SafeOutputPath(root, match.Groups["project"].Success ? match.Groups["project"].Value : null)
            ?? (IsSafeRelativeModelPath(fallbackProjectRelativePath) ? fallbackProjectRelativePath : null);
        var detail = match.Groups["detail"].Success
            ? SanitizeStructuredText(
                root,
                match.Groups["detail"].Value.Trim(),
                MaximumStructuredMessageCharacters)
            : null;
        failingTest = new(name, projectPath, string.IsNullOrWhiteSpace(detail) ? null : detail);
        return true;
    }

    private static bool IsFailingTestLine(string line)
    {
        return FailedTestRegex().IsMatch(line)
            || XunitFailedTestRegex().IsMatch(line)
            || HarnessFailedTestRegex().IsMatch(line)
            || XunitSuffixFailedTestRegex().IsMatch(line);
    }

    private static IEnumerable<string> EnumerateBoundedLines(string value, int maximumCharacters)
    {
        using var reader = new StringReader(value[..Math.Min(value.Length, maximumCharacters)]);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string? SafeOutputPath(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim().Trim('"');
        try
        {
            var absolute = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(candidate, root);
            return DotNetWorkspaceIntelligenceService.TryGetSafeRelativePath(root, absolute, out var relative)
                ? relative
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? SafeDiagnosticFilePath(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim().Trim('"');
        var extension = Path.GetExtension(candidate);
        var looksLikeFile = Path.IsPathRooted(candidate)
            || candidate.Contains('\\')
            || candidate.Contains('/')
            || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
        return looksLikeFile ? SafeOutputPath(root, candidate) : null;
    }

    private static bool IsSafeRelativeModelPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && !value.Equals("..", StringComparison.Ordinal)
            && !value.StartsWith("../", StringComparison.Ordinal)
            && !value.StartsWith("..\\", StringComparison.Ordinal);
    }

    private static string SanitizeStructuredText(string root, string value, int maximumCharacters)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitized = ReplaceWorkspaceRoot(value, normalizedRoot);
        var alternateRoot = normalizedRoot.Replace('\\', '/');
        if (!alternateRoot.Equals(normalizedRoot, StringComparison.Ordinal))
        {
            sanitized = ReplaceWorkspaceRoot(sanitized, alternateRoot);
        }

        sanitized = QuotedAbsolutePathRegex().Replace(
            sanitized,
            match => $"{match.Groups["quote"].Value}<outside-workspace-path>{match.Groups["quote"].Value}");
        sanitized = WindowsAbsolutePathRegex().Replace(sanitized, "<outside-workspace-path>");
        sanitized = UnixAbsolutePathRegex().Replace(sanitized, "<outside-workspace-path>");
        sanitized = sanitized.Replace('\\', '/');

        if (sanitized.Length <= maximumCharacters)
        {
            return sanitized;
        }

        return sanitized[..maximumCharacters] + "…";
    }

    private static string ReplaceWorkspaceRoot(string value, string root)
    {
        var startIndex = 0;
        var matchIndex = FindWorkspaceRoot(value, root, startIndex);
        if (matchIndex < 0)
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        while (matchIndex >= 0)
        {
            result.Append(value, startIndex, matchIndex - startIndex);
            result.Append('.');
            startIndex = matchIndex + root.Length;
            matchIndex = FindWorkspaceRoot(value, root, startIndex);
        }

        result.Append(value, startIndex, value.Length - startIndex);
        return result.ToString();
    }

    private static int FindWorkspaceRoot(string value, string root, int startIndex)
    {
        var matchIndex = value.IndexOf(root, startIndex, StringComparison.OrdinalIgnoreCase);
        while (matchIndex >= 0)
        {
            var after = matchIndex + root.Length;
            if (after == value.Length || value[after] is '\\' or '/')
            {
                return matchIndex;
            }

            matchIndex = value.IndexOf(root, after, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    private static int? ParsePositiveInteger(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : null;
    }

    private static int ParseNonNegativeInteger(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : 0;
    }

    private static DotNetTestTotals AddTestTotals(DotNetTestTotals? current, DotNetTestTotals next)
    {
        if (current is null)
        {
            return next;
        }

        return new(
            SaturatingAdd(current.Passed, next.Passed),
            SaturatingAdd(current.Failed, next.Failed),
            SaturatingAdd(current.Skipped, next.Skipped),
            SaturatingAdd(current.Total, next.Total));
    }

    private static int SaturatingAdd(int left, int right)
    {
        return (int)Math.Min((long)left + right, int.MaxValue);
    }

    [GeneratedRegex(
        """^\s*(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?\)\s*:\s*(?<severity>error|warning|info)\s+(?<code>(?:CS|MSB)\d{4})\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+\.csproj)\])?\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocatedDiagnosticRegex();

    [GeneratedRegex(
        """^\s*(?:(?<file>.+?)\s*:\s*)?(?<severity>error|warning|info)\s+(?<code>(?:CS|MSB)\d{4})\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+\.csproj)\])?\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnlocatedDiagnosticRegex();

    [GeneratedRegex("""\b(?:CS|MSB)\d{4}\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticCodeRegex();

    [GeneratedRegex("""^\s*(?<count>\d+)\s+Warning\(s\)\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildWarningTotalRegex();

    [GeneratedRegex("""^\s*(?<count>\d+)\s+Error\(s\)\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildErrorTotalRegex();

    [GeneratedRegex(
        """(?:Passed!|Failed!)\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VstestTotalsRegex();

    [GeneratedRegex(
        """Total tests:\s*(?<total>\d+).*?Passed:\s*(?<passed>\d+).*?Failed:\s*(?<failed>\d+).*?Skipped:\s*(?<skipped>\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompactTestTotalsRegex();

    [GeneratedRegex(
        """Test summary:\s*total:\s*(?<total>\d+),\s*failed:\s*(?<failed>\d+),\s*(?:succeeded|passed):\s*(?<passed>\d+),\s*skipped:\s*(?<skipped>\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernTestTotalsRegex();

    [GeneratedRegex(
        """^\s*Failed\s+(?<name>.+?)(?:\s+\[(?<detail>[^\]]+)\])?\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex FailedTestRegex();

    [GeneratedRegex(
        """^\s*\[FAIL\]\s+(?<name>.+?)(?:\s+\[(?<project>[^\]]+\.csproj)\])?\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XunitFailedTestRegex();

    [GeneratedRegex(
        """^\s*PASS\s+(?<name>.+?)\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex HarnessPassedTestRegex();

    [GeneratedRegex(
        """^\s*FAIL\s+(?<name>[^:]+?)(?:\s*:\s*(?<detail>.*))?\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex HarnessFailedTestRegex();

    [GeneratedRegex(
        """^\s*(?:\[[^\]]+\]\s+)?(?<name>.+?)\s+\[FAIL\]\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XunitSuffixFailedTestRegex();

    [GeneratedRegex(
        """(?<quote>["'])(?:[A-Za-z]:[\\/]|\\\\|/)[^"']+\k<quote>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuotedAbsolutePathRegex();

    [GeneratedRegex(
        """(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\t"'<>|\]\),;]+""",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(
        """(?<![:/.A-Za-z0-9])/(?!/)[^\r\n\t"'<>|\]\),;]+""",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnixAbsolutePathRegex();
}
