using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIArena.Wpf;

internal sealed record AgentCommandPreview(
    bool Ok,
    string Shell,
    string Command,
    string WorkspacePath,
    string Executable,
    string Arguments,
    string DisplayInvocation,
    IReadOnlyList<string> Risks,
    string Error)
{
    public string ApprovalKey => string.Join('\u001f', Shell, Command, WorkspacePath, Executable, Arguments);
}

internal sealed record AgentCommandResult(
    bool Ok,
    string Shell,
    string Command,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed,
    bool TimedOut,
    bool Canceled,
    string Error);

internal static partial class AgentWorkspaceCommand
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NetworkInstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(24);

    public static TimeSpan TimeoutFor(AgentCommandPreview preview, TimeSpan? baseTimeout = null)
    {
        var effectiveBase = baseTimeout ?? DefaultTimeout;
        return preview.Risks.Contains("Network/install", StringComparer.OrdinalIgnoreCase)
            ? TimeSpan.FromTicks(Math.Max(effectiveBase.Ticks, NetworkInstallTimeout.Ticks))
            : effectiveBase;
    }
    internal const int MaxCapturedStreamChars = 128 * 1024;
    internal const string StreamTruncatedMarker = "[stream truncated";
    private const int StreamReadBufferChars = 4096;
    private static readonly ConcurrentDictionary<int, Process> ActiveProcesses = new();
    private static int applicationShutdownRequested;

    public static AgentCommandPreview BuildPreview(string workspacePath, string shell, string command)
    {
        var normalizedShell = NormalizeShell(shell);
        var trimmedCommand = (command ?? "").Trim();
        var risks = new List<string>();

        if (string.IsNullOrWhiteSpace(trimmedCommand))
        {
            return Invalid(normalizedShell, trimmedCommand, workspacePath, risks, "Enter a command before previewing.");
        }

        var workspace = NormalizeWorkspacePath(workspacePath, out var workspaceError);
        if (!string.IsNullOrWhiteSpace(workspaceError))
        {
            return Invalid(normalizedShell, trimmedCommand, workspace, risks, workspaceError);
        }

        risks.AddRange(DetectRisks(trimmedCommand));
        if (TryFindBoundaryIssue(workspace, trimmedCommand, out var boundaryIssue))
        {
            risks.Add("Outside workspace");
            return Invalid(normalizedShell, trimmedCommand, workspace, risks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), boundaryIssue);
        }

        var executable = normalizedShell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
            ? "powershell.exe"
            : "cmd.exe";
        var arguments = normalizedShell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
            ? PowerShellArguments(trimmedCommand)
            : TerminalArguments(trimmedCommand);
        var invocation = normalizedShell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
            ? $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command {trimmedCommand}"
            : $"cmd.exe /d /s /c \"{trimmedCommand}\"";

        return new AgentCommandPreview(
            true,
            normalizedShell,
            trimmedCommand,
            workspace,
            executable,
            arguments,
            invocation,
            risks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            "");
    }

    public static async Task<AgentCommandResult> RunAsync(
        AgentCommandPreview preview,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!preview.Ok)
        {
            return new AgentCommandResult(
                false,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                -1,
                "",
                "",
                TimeSpan.Zero,
                false,
                false,
                string.IsNullOrWhiteSpace(preview.Error) ? "Command preview was not approved." : preview.Error);
        }

        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref applicationShutdownRequested) != 0)
        {
            return new AgentCommandResult(
                false,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                -1,
                "",
                "",
                TimeSpan.Zero,
                false,
                true,
                "Command cancelled.");
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout == System.Threading.Timeout.InfiniteTimeSpan
            || effectiveTimeout > MaximumTimeout)
        {
            return new AgentCommandResult(
                false,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                -1,
                "",
                "",
                TimeSpan.Zero,
                false,
                false,
                $"Command timeout must be greater than zero and no more than {MaximumTimeout.TotalHours:0} hours.");
        }

        var watch = Stopwatch.StartNew();
        var processId = 0;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = preview.Executable,
                Arguments = preview.Arguments,
                WorkingDirectory = preview.WorkspacePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            processId = process.Id;
            ActiveProcesses[processId] = process;
            if (Volatile.Read(ref applicationShutdownRequested) != 0)
            {
                TryKill(process);
            }

            var standardOutput = ReadBoundedStreamAsync(process.StandardOutput);
            var standardError = ReadBoundedStreamAsync(process.StandardError);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(effectiveTimeout);
            var timedOut = false;
            var canceled = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            watch.Stop();
            var stdout = await standardOutput;
            var stderr = await standardError;
            return new AgentCommandResult(
                !timedOut && !canceled && process.ExitCode == 0,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                timedOut || canceled ? -1 : process.ExitCode,
                stdout,
                stderr,
                watch.Elapsed,
                timedOut,
                canceled,
                canceled ? "Command cancelled." : timedOut ? "Command timed out." : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            watch.Stop();
            return new AgentCommandResult(
                false,
                preview.Shell,
                preview.Command,
                preview.WorkspacePath,
                -1,
                "",
                "",
                watch.Elapsed,
                false,
                false,
                ex.Message);
        }
        finally
        {
            if (processId != 0)
            {
                ActiveProcesses.TryRemove(processId, out _);
            }

            if (ProcessIsRunning(process))
            {
                TryKill(process);
            }
        }
    }

    internal static int ActiveProcessCount => ActiveProcesses.Count;

    internal static int TerminateActiveProcesses()
    {
        var terminated = 0;
        foreach (var process in ActiveProcesses.Values.ToArray())
        {
            if (TryKill(process))
            {
                terminated++;
            }
        }

        return terminated;
    }

    internal static int BeginApplicationShutdown()
    {
        Interlocked.Exchange(ref applicationShutdownRequested, 1);
        return TerminateActiveProcesses();
    }

    private static async Task<string> ReadBoundedStreamAsync(TextReader reader)
    {
        var builder = new StringBuilder(capacity: Math.Min(MaxCapturedStreamChars, StreamReadBufferChars));
        var buffer = new char[StreamReadBufferChars];
        long omitted = 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            var remaining = MaxCapturedStreamChars - builder.Length;
            if (remaining > 0)
            {
                var keep = Math.Min(remaining, read);
                builder.Append(buffer, 0, keep);
                omitted += read - keep;
            }
            else
            {
                omitted += read;
            }
        }

        if (omitted > 0)
        {
            builder.AppendLine();
            builder.Append("... ");
            builder.Append(StreamTruncatedMarker);
            builder.Append("; omitted ");
            builder.Append(omitted.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" chars]");
        }

        return builder.ToString();
    }

    public static string NormalizeShell(string value)
    {
        return (value ?? "").Trim().Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || (value ?? "").Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
            ? "PowerShell"
            : "Terminal";
    }

    public static string NormalizeWorkspacePath(string workspacePath, out string error)
    {
        error = "";
        var value = Environment.ExpandEnvironmentVariables((workspacePath ?? "").Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Choose a workspace folder before previewing commands.";
            return "";
        }

        try
        {
            var fullPath = TrimTrailingSeparator(ResolvePathThroughReparsePoints(Path.GetFullPath(value)));
            if (!Directory.Exists(fullPath))
            {
                error = "Workspace folder does not exist.";
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = $"Workspace path is invalid: {ex.Message}";
            return value;
        }
    }

    internal static bool IsInsideWorkspace(string workspacePath, string candidatePath)
    {
        var workspace = TrimTrailingSeparator(ResolvePathThroughReparsePoints(Path.GetFullPath(workspacePath)));
        var candidate = TrimTrailingSeparator(ResolvePathThroughReparsePoints(Path.GetFullPath(candidatePath)));
        if (candidate.Equals(workspace, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = workspace.EndsWith(Path.DirectorySeparatorChar)
            ? workspace
            : workspace + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CanRunAutomatically(AgentCommandPreview preview, out string reason)
    {
        if (!preview.Ok)
        {
            reason = string.IsNullOrWhiteSpace(preview.Error) ? "The command preview is invalid." : preview.Error;
            return false;
        }

        if (!preview.Shell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Full Access only auto-runs narrowly parsed PowerShell commands; terminal commands require explicit approval.";
            return false;
        }

        if (AgentCommandProposalService.IsCanonicalFileWriteCommand(preview.Command, out var generatedPaths))
        {
            foreach (var path in generatedPaths)
            {
                if (!LiteralPathIsInsideWorkspace(preview.WorkspacePath, path, out reason))
                {
                    return false;
                }
            }

            reason = "";
            return true;
        }

        if (AutoLiteralWriteRegex().Match(preview.Command) is { Success: true } writeMatch)
        {
            if (!LiteralPowerShellValueIsSafe(writeMatch.Groups["value"].Value))
            {
                reason = "Full Access will not evaluate substitutions or expressions in an automatically approved write value.";
                return false;
            }

            return LiteralPathIsInsideWorkspace(preview.WorkspacePath, writeMatch.Groups["path"].Value, out reason);
        }

        if (AutoLiteralPathCommandRegex().Match(preview.Command) is { Success: true } pathMatch)
        {
            return LiteralPathIsInsideWorkspace(preview.WorkspacePath, pathMatch.Groups["path"].Value, out reason);
        }

        if (AutoLiteralOutputRegex().Match(preview.Command) is { Success: true } outputMatch
            && LiteralPowerShellValueIsSafe(outputMatch.Groups["value"].Value))
        {
            reason = "";
            return true;
        }

        reason = "Full Access cannot prove this shell command stays inside the workspace. Nested shells, interpreters, expressions, pipelines, redirects, and unparsed commands require explicit approval.";
        return false;
    }

    internal static bool TryCreateAutomaticExecutionPreview(
        AgentCommandPreview preview,
        out AgentCommandPreview automaticPreview,
        out string reason)
    {
        automaticPreview = preview;
        if (!CanRunAutomatically(preview, out reason))
        {
            return false;
        }

        if (AgentCommandProposalService.IsCanonicalFileWriteCommand(preview.Command, out _))
        {
            return true;
        }

        var writeMatch = AutoLiteralWriteRegex().Match(preview.Command);
        if (!writeMatch.Success
            || !writeMatch.Groups[1].Value.Equals("set-content", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rawPath = UnquotePowerShellLiteral(writeMatch.Groups["path"].Value);
        if (!AgentCommandProposalService.TryNormalizeSuggestedFilePath(rawPath, out var path))
        {
            reason = "Full Access could not materialize the literal write path safely.";
            return false;
        }

        var content = UnquotePowerShellLiteral(writeMatch.Groups["value"].Value);
        var canonicalCommand = AgentCommandProposalService.BuildFileWriteCommand(
            new AgentWorkspaceCoordinator.AgentFileSuggestion(
                [new AgentWorkspaceCoordinator.AgentSuggestedFile(path, content, "")]));
        automaticPreview = BuildPreview(preview.WorkspacePath, "PowerShell", canonicalCommand);
        return CanRunAutomatically(automaticPreview, out reason);
    }

    private static string UnquotePowerShellLiteral(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length < 2)
        {
            return trimmed;
        }

        if (trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool LiteralPathIsInsideWorkspace(string workspacePath, string rawPath, out string reason)
    {
        var path = (rawPath ?? "").Trim();
        if (path.Length >= 2 && ((path[0] == '\'' && path[^1] == '\'') || (path[0] == '"' && path[^1] == '"')))
        {
            path = path[1..^1];
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(['$', '`', '*', '?', '[', ']']) >= 0
            || path.Contains("::", StringComparison.Ordinal)
            || (path.Contains(':') && !Path.IsPathFullyQualified(path)))
        {
            reason = "Full Access requires a literal filesystem path with no variables, wildcards, providers, or expressions.";
            return false;
        }

        try
        {
            var candidate = Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(workspacePath, path);
            if (!IsInsideWorkspace(workspacePath, candidate))
            {
                reason = $"Full Access will not auto-run a path outside the workspace: {path}";
                return false;
            }

            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            reason = $"Full Access could not validate the command path: {ex.Message}";
            return false;
        }
    }

    private static bool LiteralPowerShellValueIsSafe(string rawValue)
    {
        var value = (rawValue ?? "").Trim();
        if (value.Length == 0 || value.Contains('`') || value.Contains('\r') || value.Contains('\n'))
        {
            return false;
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return true;
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return !value.Contains('$');
        }

        return value.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '_' or '-' or '.' or ':' or '/' or '\\');
    }

    private static string ResolvePathThroughReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return fullPath;
        }

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals(".", StringComparison.Ordinal))
        {
            return fullPath;
        }

        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo? item = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (item is not null && (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                current = item.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }

    private static AgentCommandPreview Invalid(
        string shell,
        string command,
        string workspacePath,
        IReadOnlyList<string> risks,
        string error)
    {
        return new AgentCommandPreview(false, shell, command, workspacePath, "", "", "", risks, error);
    }

    private static string PowerShellArguments(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static string TerminalArguments(string command)
    {
        var escaped = command.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"/d /s /c \"{escaped}\"";
    }

    private static bool TryFindBoundaryIssue(string workspacePath, string command, out string issue)
    {
        issue = "";
        if (ChangesDirectoryAboveWorkspaceRegex().IsMatch(command))
        {
            issue = "Command tries to move above the selected workspace.";
            return true;
        }

        if (RelativeParentWritePathRegex().IsMatch(command)
            || RedirectionToParentPathRegex().IsMatch(command)
            || WorkspaceOptionParentPathRegex().IsMatch(command)
            || ScaffoldParentPathRegex().IsMatch(command))
        {
            issue = "Command references a parent path outside the selected workspace.";
            return true;
        }

        if (DynamicWritePathRegex().IsMatch(command))
        {
            issue = "Command builds a write path dynamically; use explicit workspace-relative paths so preview validation can verify the target.";
            return true;
        }

        foreach (var path in ExtractMentionedAbsolutePaths(command))
        {
            try
            {
                if (!IsInsideWorkspace(workspacePath, path))
                {
                    issue = $"Command references a path outside the workspace: {path}";
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                issue = $"Command references an invalid path: {path}";
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExtractMentionedAbsolutePaths(string command)
    {
        foreach (Match match in QuotedWindowsPathRegex().Matches(command))
        {
            yield return match.Groups["path"].Value;
        }

        foreach (Match match in UnquotedWindowsPathRegex().Matches(command))
        {
            yield return match.Groups["path"].Value.TrimEnd(',', '.', ')', ']');
        }
    }

    private static IReadOnlyList<string> DetectRisks(string command)
    {
        var risks = new List<string>();
        if (DestructiveCommandRegex().IsMatch(command))
        {
            risks.Add("Destructive");
        }

        if (NetworkOrInstallCommandRegex().IsMatch(command))
        {
            risks.Add("Network/install");
        }

        if (LooksLongRunningCommand(command))
        {
            risks.Add("Long-running");
        }

        if (ElevatedCommandRegex().IsMatch(command))
        {
            risks.Add("Elevated");
        }

        return risks;
    }

    internal static bool LooksLongRunningCommand(string command)
    {
        return LongRunningCommandRegex().IsMatch(command ?? "");
    }

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ProcessIsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?im)(^|[;&|])\s*(cd|chdir|pushd|set-location|sl)\s+(/d\s+)?[""']?\.\.([\\/]|[""']?(\s|$))")]
    private static partial Regex ChangesDirectoryAboveWorkspaceRegex();

    [GeneratedRegex(@"(?im)(^|[;&|])\s*(set-content|add-content|out-file|copy-item|move-item|new-item|remove-item|mkdir|md|ni|copy|xcopy)\b[^\r\n;|&]*(\.\.[\\/])")]
    private static partial Regex RelativeParentWritePathRegex();

    [GeneratedRegex(@"(?im)(^|[\s])([12]?>|>>)\s*[""']?\.\.[\\/]")]
    private static partial Regex RedirectionToParentPathRegex();

    [GeneratedRegex(@"(?im)(^|[\s])(-o|--output|--out-dir|--outDir|--output-dir|--output-path|--project|--cwd|--prefix|--directory|--dir|--dest|--destination|--target|--path|--working-directory|-C)\s*(=|\s+)\s*[""']?\.\.([\\/]|[""']?(\s|$))")]
    private static partial Regex WorkspaceOptionParentPathRegex();

    [GeneratedRegex(@"(?im)\b(dotnet\s+new|npm\s+(create|init)|pnpm\s+create|yarn\s+create|npx\s+create-[A-Za-z0-9_.-]+|cargo\s+new|ng\s+new|git\s+clone)\b[^\r\n;|&]*(^|\s)[""']?\.\.[\\/]")]
    private static partial Regex ScaffoldParentPathRegex();

    [GeneratedRegex(@"(?im)(^|[;&|])\s*(set-content|add-content|out-file|copy-item|move-item|new-item|remove-item|mkdir|md|ni|copy|xcopy)\b[^\r\n;|&]*(\$env:|%[A-Za-z_][A-Za-z0-9_]*%|\[System\.IO\.Path\]::GetTempPath|Join-Path\s+\$env:)")]
    private static partial Regex DynamicWritePathRegex();

    [GeneratedRegex(@"(?<quote>[""'])(?<path>([A-Za-z]:[\\/]|\\\\)[^""']+)\k<quote>")]
    private static partial Regex QuotedWindowsPathRegex();

    [GeneratedRegex(@"(?<![\w:""':])(?<path>([A-Za-z]:[\\/]|\\\\)[^\s""'`|&;<>]+)")]
    private static partial Regex UnquotedWindowsPathRegex();

    [GeneratedRegex(@"(?i)\b(rm\s+-rf|remove-item\b[^\r\n;|&]*-recurse\b|del\b.*(/s|/q)|rmdir\b|rd\s+|git\s+clean\b|git\s+reset\s+--hard|format\b|diskpart\b|reg\s+delete)\b")]
    private static partial Regex DestructiveCommandRegex();

    [GeneratedRegex(@"(?i)\b(curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm|npm\s+install|pnpm\s+install|yarn\s+install|pip\s+install|dotnet\s+add\s+package|winget|choco)\b")]
    private static partial Regex NetworkOrInstallCommandRegex();

    [GeneratedRegex(@"(?i)\b(npm\s+(--prefix\s+(""[^""]+""|'[^']+'|\S+)\s+)?(run\s+)?(dev|start|serve|preview)|pnpm\s+(run\s+)?(dev|start|serve|preview)|yarn\s+(run\s+)?(dev|start|serve|preview)|bun\s+(run\s+)?(dev|start)|npx\s+vite|vite(\.cmd)?|dotnet\s+(watch|run)|python\s+(-m\s+)?http\.server|py\s+(-m\s+)?http\.server|watch\s+|start-process)\b")]
    private static partial Regex LongRunningCommandRegex();

    [GeneratedRegex(@"(?i)\b(runas|start-process\b.*\b-verb\s+runas)\b")]
    private static partial Regex ElevatedCommandRegex();

    [GeneratedRegex(@"(?is)^\s*(set-content|add-content)\s+(?:-(?:literal)?path\s+)?(?<path>""[^""\r\n]+""|'[^'\r\n]+'|[^\s;|&<>]+)\s+-value\s+(?<value>""(?:[^""`]|`.)*""|'(?:[^']|'')*'|[A-Za-z0-9_./:\\ -]+)(?:\s+-(?:nonewline|force)|\s+-encoding\s+(?:utf8|utf8nobom|unicode|ascii))*\s*$")]
    private static partial Regex AutoLiteralWriteRegex();

    [GeneratedRegex(@"(?is)^\s*(test-path|get-item|get-content|get-childitem|get-child-item)\s+(?:-(?:literal)?path\s+)?(?<path>""[^""\r\n]+""|'[^'\r\n]+'|[^\s;|&<>]+)\s*$")]
    private static partial Regex AutoLiteralPathCommandRegex();

    [GeneratedRegex(@"(?is)^\s*write-(?:host|output)\s+(?<value>""(?:[^""`]|`.)*""|'(?:[^']|'')*'|[A-Za-z0-9_./:\\ -]+)\s*$")]
    private static partial Regex AutoLiteralOutputRegex();
}
