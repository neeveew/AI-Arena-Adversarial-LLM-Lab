using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static AIArena.Wpf.Services.WorkspaceCommandHelpers;

using AgentCommandSuggestion = AIArena.Wpf.AgentWorkspaceCoordinator.AgentCommandSuggestion;
using AgentFileSuggestion = AIArena.Wpf.AgentWorkspaceCoordinator.AgentFileSuggestion;
using AgentSuggestedFile = AIArena.Wpf.AgentWorkspaceCoordinator.AgentSuggestedFile;

namespace AIArena.Wpf;

/// <summary>
/// Pure parser/materializer for Agent command proposals and file suggestions.
/// The coordinator owns WPF state; this service owns text-to-command extraction.
/// </summary>
internal static class AgentCommandProposalService
{
    private const int MaxMaterializedFiles = 8;
    private const int MaxMaterializedFileChars = 12000;
    private const int MaxMaterializedTotalChars = 30000;

    private static readonly Regex FencedCommandBlockRegex = new(
        @"```(?<lang>[^\r\n`]*)\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XmlCommandBlockRegex = new(
        @"<(?<tag>command|cmd|terminal)(?:\s+shell\s*=\s*[""'](?<shell>[^""']+)[""'])?[^>]*>(?<body>[\s\S]*?)</\k<tag>>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LabeledCommandRegex = new(
        @"^\s*(?<label>command proposal|next command|first command|run this command|use this command|powershell command|terminal command|command|run|terminal|powershell|pwsh|cmd)\s*:\s*(?<command>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InlineCodeCommandRegex = new(
        @"^\s*(?:[-*]|\d+[\.)])\s*`(?<command>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeredocRedirectBeforeRegex = new(
        @"^\s*(?:cat|tee)\s+>\s*(?<path>""[^""]+""|'[^']+'|[^\s|;&]+)\s+<<\s*[""']?(?<marker>[A-Za-z0-9_.-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HeredocRedirectAfterRegex = new(
        @"^\s*(?:cat|tee)\s+<<\s*[""']?(?<marker>[A-Za-z0-9_.-]+)[""']?\s*>\s*(?<path>""[^""]+""|'[^']+'|[^\s|;&]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TeeHeredocRegex = new(
        @"^\s*tee\s+(?<path>""[^""]+""|'[^']+'|[^\s|;&]+)\s+<<\s*[""']?(?<marker>[A-Za-z0-9_.-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ShellRedirectWriteRegex = new(
        @"^\s*(?<command>echo|printf)\s+(?<content>.+?)\s+(?<![>\d])>\s*(?<path>""[^""]+""|'[^']+'|[^\s|;&]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RawFileHeaderRegex = new(
        @"^\s*(?:[-*]\s*)?(?:file|path|filename)\s*:\s*(?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RawContentHeaderRegex = new(
        @"^\s*(?:content|contents|code)\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FilePathCandidateRegex = new(
        @"(?<![A-Za-z0-9_.\\/:-])(?<path>(?:\.?[\\/])?(?:[A-Za-z0-9][A-Za-z0-9 _.-]*[\\/])*[A-Za-z0-9][A-Za-z0-9 _.-]*\.(?:html|htm|css|js|mjs|cjs|jsx|ts|tsx|json|md|txt|cs|py|xml|xaml|yml|yaml|toml|ini|scss|less|vue|svelte|php|java|go|rs|rb|sql|csproj|sln|config))(?![A-Za-z0-9_.\\/-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlScriptSrcRegex = new(
        @"<script\b[^>]*\bsrc\s*=\s*[""'](?<path>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlLinkHrefRegex = new(
        @"<link\b[^>]*\bhref\s*=\s*[""'](?<path>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ActionIntentRegex = new(
        @"\b(write|create|make|modify|edit|change|scaffold|build|run|test|verify|repair|implement|generate|add|wire|set\s+up|setup|bootstrap|prototype|website|page|component|site|game|tool|ui|app|application)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PowerShellEchoNoNewlineRedirectRegex = new(
        @"(?im)^\s*echo\s+-n\s+(?<quote>[""'])(?<value>.*?)\k<quote>\s*>\s*(?<path>""[^""]+""|'[^']+'|[^\s]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PowerShellCommaFilterListingRegex = new(
        @"(?im)^\s*Get-ChildItem\b[^\r\n]*\s-Filter\s+[^""'\r\n\s]*,[^""'\r\n\s]*(?:\s*\|\s*Select-Object\b[^\r\n]*)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex GeneratedFileWriteEntryRegex = new(
        @"(?m)^\s*@\{ Path = '(?<path>(?:[^']|'')*)'; Base64 = '(?<base64>[A-Za-z0-9+/]*={0,2})' \}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static AgentFileSuggestion? ExtractFileWriteSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var files = new List<AgentSuggestedFile>();
        var totalChars = 0;
        AddHeredocFileSuggestions(text, files, ref totalChars);
        AddShellRedirectFileSuggestions(text, files, ref totalChars);
        AddRawFileContentSuggestions(text, files, ref totalChars);
        var htmlAssetReferences = CollectHtmlAssetReferences(text);
        foreach (Match match in FencedCommandBlockRegex.Matches(text))
        {
            var languageTag = match.Groups["lang"].Value;
            var language = FirstLanguageToken(languageTag);
            var body = TrimCodeFenceBody(match.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(body)
                || !string.IsNullOrWhiteSpace(ShellForCommandLanguage(language))
                || LooksLikeRunnableCommand(NormalizeCommandBlock(body)))
            {
                continue;
            }

            var candidatePath = FindFilePathForFence(text, match.Index, languageTag, body);
            candidatePath = PreferReferencedAssetPath(languageTag, body, candidatePath, htmlAssetReferences, files);
            if (!TryNormalizeSuggestedFilePath(candidatePath, out var path))
            {
                continue;
            }

            var content = StripFilePathMarkerLine(body, path);
            if (content.Length > MaxMaterializedFileChars)
            {
                continue;
            }

            if (totalChars + content.Length > MaxMaterializedTotalChars)
            {
                break;
            }

            var uniquePath = UniqueSuggestedFilePath(path, files);
            files.Add(new AgentSuggestedFile(uniquePath, content, language));
            totalChars += content.Length;
            if (files.Count >= MaxMaterializedFiles)
            {
                break;
            }
        }

        return files.Count == 0 ? null : new AgentFileSuggestion(files);
    }

    private static void AddRawFileContentSuggestions(string text, List<AgentSuggestedFile> files, ref int totalChars)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length && files.Count < MaxMaterializedFiles; index++)
        {
            if (!TryParseRawFileHeader(lines[index], out var path))
            {
                continue;
            }

            var contentStart = index + 1;
            while (contentStart < lines.Length && string.IsNullOrWhiteSpace(lines[contentStart]))
            {
                contentStart++;
            }

            if (contentStart < lines.Length && RawContentHeaderRegex.IsMatch(lines[contentStart]))
            {
                contentStart++;
            }

            while (contentStart < lines.Length && string.IsNullOrWhiteSpace(lines[contentStart]))
            {
                contentStart++;
            }

            if (contentStart >= lines.Length)
            {
                continue;
            }

            var contentLines = new List<string>();
            var cursor = contentStart;
            for (; cursor < lines.Length; cursor++)
            {
                var line = lines[cursor];
                if (cursor > contentStart && TryParseRawFileHeader(line, out _))
                {
                    break;
                }

                if (LooksLikeFileContentBoundary(line))
                {
                    break;
                }

                contentLines.Add(line);
            }

            var content = TrimSuggestedFileContent(string.Join("\n", contentLines));
            if (string.IsNullOrWhiteSpace(content)
                || content.Length > MaxMaterializedFileChars
                || totalChars + content.Length > MaxMaterializedTotalChars)
            {
                index = Math.Max(index, cursor - 1);
                continue;
            }

            var uniquePath = UniqueSuggestedFilePath(path, files);
            files.Add(new AgentSuggestedFile(uniquePath, content.Replace("\n", Environment.NewLine, StringComparison.Ordinal), ""));
            totalChars += content.Length;
            index = Math.Max(index, cursor - 1);
        }
    }

    private static bool TryParseRawFileHeader(string line, out string path)
    {
        path = "";
        var match = RawFileHeaderRegex.Match(line ?? "");
        return match.Success && TryNormalizeSuggestedFilePath(match.Groups["path"].Value, out path);
    }

    private static bool LooksLikeFileContentBoundary(string line)
    {
        var trimmed = (line ?? "").Trim();
        return trimmed.StartsWith("Command proposal", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Current evidence", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Artifact suggestion", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Recommended next action", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    private static string TrimSuggestedFileContent(string content)
    {
        var normalized = (content ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n', '\r');
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = normalized.IndexOf('\n');
            if (firstNewline >= 0)
            {
                normalized = normalized[(firstNewline + 1)..];
            }
        }

        if (normalized.EndsWith("```", StringComparison.Ordinal))
        {
            normalized = normalized[..^3].TrimEnd();
        }

        return normalized;
    }

    private static void AddHeredocFileSuggestions(string text, List<AgentSuggestedFile> files, ref int totalChars)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length && files.Count < MaxMaterializedFiles; index++)
        {
            if (!TryParseHeredocHeader(lines[index], out var marker, out var rawPath)
                || !TryNormalizeSuggestedFilePath(rawPath, out var path))
            {
                continue;
            }

            var contentLines = new List<string>();
            var contentStart = index + 1;
            var markerIndex = -1;
            for (var contentIndex = contentStart; contentIndex < lines.Length; contentIndex++)
            {
                if (HeredocMarkerMatches(lines[contentIndex], marker))
                {
                    markerIndex = contentIndex;
                    break;
                }

                contentLines.Add(lines[contentIndex]);
            }

            if (markerIndex < 0)
            {
                continue;
            }

            var content = string.Join(Environment.NewLine, contentLines);
            if (content.Length > MaxMaterializedFileChars)
            {
                index = markerIndex;
                continue;
            }

            if (totalChars + content.Length > MaxMaterializedTotalChars)
            {
                break;
            }

            var uniquePath = UniqueSuggestedFilePath(path, files);
            files.Add(new AgentSuggestedFile(uniquePath, content, ""));
            totalChars += content.Length;
            index = markerIndex;
        }
    }

    private static void AddShellRedirectFileSuggestions(string text, List<AgentSuggestedFile> files, ref int totalChars)
    {
        foreach (var rawLine in (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (files.Count >= MaxMaterializedFiles)
            {
                break;
            }

            if (!TryParseShellRedirectWriteLine(rawLine, out var path, out var content))
            {
                continue;
            }

            if (content.Length > MaxMaterializedFileChars)
            {
                continue;
            }

            if (totalChars + content.Length > MaxMaterializedTotalChars)
            {
                break;
            }

            var uniquePath = UniqueSuggestedFilePath(path, files);
            files.Add(new AgentSuggestedFile(uniquePath, content, ""));
            totalChars += content.Length;
        }
    }

    private static bool TryParseShellRedirectWriteLine(string line, out string path, out string content)
    {
        path = "";
        content = "";
        foreach (var candidate in ShellWriteLineCandidates(line))
        {
            var match = ShellRedirectWriteRegex.Match(candidate);
            if (!match.Success || !TryNormalizeSuggestedFilePath(match.Groups["path"].Value, out path))
            {
                continue;
            }

            var command = match.Groups["command"].Value;
            var rawContent = match.Groups["content"].Value.Trim();
            return TryDecodeShellRedirectContent(command, rawContent, out content);
        }

        return false;
    }

    private static bool TryDecodeShellRedirectContent(string command, string rawContent, out string content)
    {
        content = "";
        var value = rawContent.Trim();
        var decodeEscapes = command.Equals("printf", StringComparison.OrdinalIgnoreCase);
        if (command.Equals("echo", StringComparison.OrdinalIgnoreCase)
            && value.StartsWith("-e ", StringComparison.OrdinalIgnoreCase))
        {
            decodeEscapes = true;
            value = value[3..].TrimStart();
        }

        if (command.Equals("printf", StringComparison.OrdinalIgnoreCase)
            && !TryUnwrapShellQuotedString(value, out value))
        {
            return false;
        }

        if (!command.Equals("printf", StringComparison.OrdinalIgnoreCase)
            && TryUnwrapShellQuotedString(value, out var unwrapped))
        {
            value = unwrapped;
        }

        content = decodeEscapes ? DecodeShellEscapes(value) : value;
        return true;
    }

    private static bool TryUnwrapShellQuotedString(string value, out string unwrapped)
    {
        unwrapped = "";
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var quote = trimmed[0];
        if ((quote != '\'' && quote != '"') || trimmed[^1] != quote)
        {
            return false;
        }

        unwrapped = trimmed[1..^1];
        return true;
    }

    private static string DecodeShellEscapes(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                builder.Append(character);
                continue;
            }

            var next = value[++index];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '\'' => '\'',
                '"' => '"',
                _ => next
            });
        }

        return builder.ToString();
    }

    private static bool TryParseHeredocHeader(string line, out string marker, out string path)
    {
        foreach (var candidate in ShellWriteLineCandidates(line))
        {
            foreach (var regex in new[] { HeredocRedirectBeforeRegex, HeredocRedirectAfterRegex, TeeHeredocRegex })
            {
                var match = regex.Match(candidate);
                if (!match.Success)
                {
                    continue;
                }

                marker = match.Groups["marker"].Value.Trim();
                path = match.Groups["path"].Value.Trim();
                return !string.IsNullOrWhiteSpace(marker) && !string.IsNullOrWhiteSpace(path);
            }
        }

        marker = "";
        path = "";
        return false;
    }

    private static IEnumerable<string> ShellWriteLineCandidates(string line)
    {
        var normalized = RemovePromptPrefix(line ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;
        foreach (var separator in new[] { "&&", ";" })
        {
            var index = normalized.LastIndexOf(separator, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var candidate = normalized[(index + separator.Length)..].TrimStart();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool HeredocMarkerMatches(string line, string marker)
    {
        var trimmed = (line ?? "").Trim();
        return trimmed.Equals(marker, StringComparison.Ordinal)
            || trimmed.Equals($"{marker};", StringComparison.Ordinal);
    }

    private static readonly Regex FileManifestEntryRegex = new(
        @"@\{\s*Path\s*=\s*'(?<path>(?:[^']|'')+)'\s*;\s*Base64\s*=\s*'(?<base64>[A-Za-z0-9+/=]*)'\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex WriteCmdletTargetRegex = new(
        @"(?im)\b(?:set-content|add-content|out-file)\b\s+(?:-(?:literalpath|path|filepath)\s+)?(?<q>['""])(?<path>.+?)\k<q>",
        RegexOptions.Compiled);

    internal static IReadOnlyList<string> DescribePlannedFileWrites(string command)
    {
        var writes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FileManifestEntryRegex.Matches(command ?? ""))
        {
            var path = match.Groups["path"].Value.Replace("''", "'", StringComparison.Ordinal);
            var detail = path;
            try
            {
                var content = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["base64"].Value));
                var lineCount = content.Length == 0 ? 0 : content.Split('\n').Length;
                detail = $"{path} ({lineCount.ToString(CultureInfo.InvariantCulture)} line{(lineCount == 1 ? "" : "s")}, {FormatByteSize(Encoding.UTF8.GetByteCount(content))})";
            }
            catch (FormatException)
            {
            }

            if (seen.Add(path))
            {
                writes.Add(detail);
            }
        }

        foreach (Match match in WriteCmdletTargetRegex.Matches(command ?? ""))
        {
            var path = match.Groups["path"].Value.Replace("''", "'", StringComparison.Ordinal);
            if (seen.Add(path))
            {
                writes.Add(path);
            }
        }

        return writes;
    }

    private static string FormatByteSize(int bytes)
    {
        return bytes < 1024
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : $"{(bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KB";
    }

    internal static string BuildFileWriteCommand(AgentFileSuggestion suggestion)
    {
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$cwd = (Get-Location).Path",
            "$workspaceRoot = [System.IO.Path]::GetFullPath($cwd)",
            "$workspaceRoot = $workspaceRoot.TrimEnd([char]92, [char]47) + [System.IO.Path]::DirectorySeparatorChar",
            "$utf8NoBom = New-Object System.Text.UTF8Encoding $false",
            "$files = @("
        };

        foreach (var file in suggestion.Files)
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content));
            lines.Add($"    @{{ Path = '{EscapePowerShellSingleQuoted(file.Path)}'; Base64 = '{base64}' }}");
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
            "    $parent = Split-Path -Parent $fullPath",
            "    if (-not [string]::IsNullOrWhiteSpace($parent)) {",
            "        New-Item -ItemType Directory -Path $parent -Force | Out-Null",
            "    }",
            "    $content = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($file.Base64))",
            "    [System.IO.File]::WriteAllText($fullPath, $content, $utf8NoBom)",
            "}",
            "Write-Host (\"Wrote {0} file(s): {1}\" -f $files.Count, (($files | ForEach-Object { $_.Path }) -join ', '))"
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    internal static bool IsCanonicalFileWriteCommand(string command, out IReadOnlyList<string> paths)
    {
        paths = [];
        var normalized = NormalizeLineEndings(command);
        var files = new List<AgentSuggestedFile>();
        foreach (Match match in GeneratedFileWriteEntryRegex.Matches(normalized))
        {
            var rawPath = match.Groups["path"].Value.Replace("''", "'", StringComparison.Ordinal);
            if (!TryNormalizeSuggestedFilePath(rawPath, out var path))
            {
                return false;
            }

            byte[] contentBytes;
            try
            {
                contentBytes = Convert.FromBase64String(match.Groups["base64"].Value);
            }
            catch (FormatException)
            {
                return false;
            }

            files.Add(new AgentSuggestedFile(path, Encoding.UTF8.GetString(contentBytes), ""));
        }

        if (files.Count == 0 || files.Count > MaxMaterializedFiles)
        {
            return false;
        }

        var rebuilt = NormalizeLineEndings(BuildFileWriteCommand(new AgentFileSuggestion(files)));
        if (!normalized.Equals(rebuilt, StringComparison.Ordinal))
        {
            return false;
        }

        paths = files.Select(file => file.Path).ToArray();
        return true;
    }

    private static string NormalizeLineEndings(string value)
    {
        return (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    }

    private static string FindFilePathForFence(string text, int fenceIndex, string language, string body)
    {
        var languageParts = (language ?? "").Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (languageParts.Length > 1
            && TryFindFilePathCandidate(string.Join(" ", languageParts.Skip(1)), out var languageTailPath))
        {
            return languageTailPath;
        }

        if (TryFindFilePathCandidate(language ?? "", out var taggedPath))
        {
            return taggedPath;
        }

        var lookbackStart = Math.Max(0, fenceIndex - 360);
        var previousFenceIndex = text.LastIndexOf("```", Math.Max(0, fenceIndex - 1), StringComparison.Ordinal);
        if (previousFenceIndex >= 0)
        {
            lookbackStart = Math.Max(lookbackStart, previousFenceIndex + 3);
        }

        var lookback = text[lookbackStart..fenceIndex];
        foreach (var rawLine in lookback.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Reverse().Take(8))
        {
            if (TryFindFilePathCandidate(rawLine, out var path))
            {
                return path;
            }
        }

        foreach (var rawLine in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(4))
        {
            if (TryFindFilePathMarkerLine(rawLine, out var path))
            {
                return path;
            }
        }

        return DefaultFileNameForLanguage(language ?? "", body);
    }

    private static bool TryFindFilePathMarkerLine(string line, out string path)
    {
        if (TryFindFilePathCandidate(line, out path)
            && IsFilePathMarkerLine(line, path))
        {
            return true;
        }

        path = "";
        return false;
    }

    private static bool TryFindFilePathCandidate(string line, out string path)
    {
        foreach (Match match in FilePathCandidateRegex.Matches(line ?? ""))
        {
            if (TryNormalizeSuggestedFilePath(match.Groups["path"].Value, out path))
            {
                return true;
            }
        }

        path = "";
        return false;
    }

    internal static bool TryNormalizeSuggestedFilePath(string value, out string path)
    {
        path = "";
        var trimmed = (value ?? "")
            .Trim()
            .Trim('`', '"', '\'', '<', '>', ':', ',', ';', '(', ')', '[', ']')
            .Replace('\\', '/');
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Contains("://", StringComparison.Ordinal)
            || Path.IsPathRooted(trimmed))
        {
            return false;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var part in parts)
        {
            if (part.Equals(".", StringComparison.Ordinal)
                || part.Equals("..", StringComparison.Ordinal)
                || part.IndexOfAny(invalid) >= 0)
            {
                return false;
            }
        }

        if (parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("bin", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("obj", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = string.Join("/", parts);
        return true;
    }

    private static string DefaultFileNameForLanguage(string language, string body)
    {
        var normalized = FirstLanguageToken(language).ToLowerInvariant();
        return normalized switch
        {
            "html" or "htm" => "index.html",
            "css" or "scss" or "less" => "styles.css",
            "javascript" or "js" or "mjs" or "cjs" => "script.js",
            "jsx" => "src/App.jsx",
            "typescript" or "ts" => "src/app.ts",
            "tsx" => "src/App.tsx",
            "json" => body.Contains("\"scripts\"", StringComparison.OrdinalIgnoreCase) ? "package.json" : "data.json",
            "markdown" or "md" => "README.md",
            "python" or "py" => "app.py",
            "csharp" or "cs" => "Program.cs",
            "xaml" => "MainWindow.xaml",
            "vue" => "src/App.vue",
            "svelte" => "src/App.svelte",
            _ => ""
        };
    }

    private static IReadOnlyList<string> CollectHtmlAssetReferences(string text)
    {
        var paths = new List<string>();
        foreach (Match match in FencedCommandBlockRegex.Matches(text ?? ""))
        {
            var language = FirstLanguageToken(match.Groups["lang"].Value);
            var body = TrimCodeFenceBody(match.Groups["body"].Value);
            if (!language.Equals("html", StringComparison.OrdinalIgnoreCase)
                && !language.Equals("htm", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("<script", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("<link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddHtmlAssetReferences(body, HtmlLinkHrefRegex, paths);
            AddHtmlAssetReferences(body, HtmlScriptSrcRegex, paths);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddHtmlAssetReferences(string html, Regex regex, List<string> paths)
    {
        foreach (Match match in regex.Matches(html ?? ""))
        {
            if (TryNormalizeHtmlAssetReference(match.Groups["path"].Value, out var path))
            {
                paths.Add(path);
            }
        }
    }

    private static bool TryNormalizeHtmlAssetReference(string value, out string path)
    {
        path = "";
        var trimmed = System.Net.WebUtility.HtmlDecode(value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffixIndex = trimmed.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
        {
            trimmed = trimmed[..suffixIndex];
        }

        trimmed = trimmed.TrimStart('/');
        return TryNormalizeSuggestedFilePath(trimmed, out path);
    }

    private static string PreferReferencedAssetPath(
        string language,
        string body,
        string candidatePath,
        IReadOnlyList<string> htmlAssetReferences,
        IReadOnlyList<AgentSuggestedFile> files)
    {
        var defaultPath = DefaultFileNameForLanguage(language, body);
        if (htmlAssetReferences.Count == 0
            || string.IsNullOrWhiteSpace(defaultPath)
            || !NormalizeRelativePath(candidatePath).Equals(NormalizeRelativePath(defaultPath), StringComparison.OrdinalIgnoreCase))
        {
            return candidatePath;
        }

        var extensions = ReferencedAssetExtensionsForLanguage(language);
        if (extensions.Length == 0)
        {
            return candidatePath;
        }

        var referencedPath = htmlAssetReferences.FirstOrDefault(path =>
            extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && !files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
        return string.IsNullOrWhiteSpace(referencedPath) ? candidatePath : referencedPath;
    }

    private static string[] ReferencedAssetExtensionsForLanguage(string language)
    {
        return FirstLanguageToken(language).ToLowerInvariant() switch
        {
            "css" or "scss" or "less" => [".css", ".scss", ".less"],
            "javascript" or "js" or "mjs" or "cjs" => [".js", ".mjs", ".cjs"],
            "jsx" => [".jsx", ".js"],
            "typescript" or "ts" => [".ts", ".js"],
            "tsx" => [".tsx", ".ts", ".js"],
            _ => []
        };
    }

    private static string FirstLanguageToken(string value)
    {
        return (value ?? "").Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static string TrimCodeFenceBody(string value)
    {
        var body = (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        while (body.StartsWith('\n'))
        {
            body = body[1..];
        }

        while (body.EndsWith('\n'))
        {
            body = body[..^1];
        }

        return body.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static string StripFilePathMarkerLine(string content, string normalizedPath)
    {
        var body = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var newlineIndex = body.IndexOf('\n');
        var firstLine = newlineIndex >= 0 ? body[..newlineIndex] : body;
        if (!IsFilePathMarkerLine(firstLine, normalizedPath))
        {
            return content;
        }

        var remaining = newlineIndex >= 0 ? body[(newlineIndex + 1)..] : "";
        var remainingLines = remaining.Split('\n').ToList();
        while (remainingLines.Count > 0 && string.IsNullOrWhiteSpace(remainingLines[0]))
        {
            remainingLines.RemoveAt(0);
        }

        if (remainingLines.Count > 0 && RawContentHeaderRegex.IsMatch(remainingLines[0]))
        {
            remainingLines.RemoveAt(0);
        }

        while (remainingLines.Count > 0 && string.IsNullOrWhiteSpace(remainingLines[0]))
        {
            remainingLines.RemoveAt(0);
        }

        remaining = string.Join("\n", remainingLines);
        return remaining.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static bool IsFilePathMarkerLine(string line, string normalizedPath)
    {
        var trimmed = (line ?? "").Trim();
        var unwrapped = trimmed.Trim('`', '"', '\'');
        if (unwrapped.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
            || unwrapped.Equals($"File: {normalizedPath}", StringComparison.OrdinalIgnoreCase)
            || unwrapped.Equals($"Path: {normalizedPath}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryFindFilePathCandidate(trimmed, out var candidate)
            || !candidate.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("<!--", StringComparison.Ordinal)
            || trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Path:", StringComparison.OrdinalIgnoreCase);
    }

    private static string UniqueSuggestedFilePath(string path, IReadOnlyList<AgentSuggestedFile> files)
    {
        if (!files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return path;
        }

        var slashIndex = path.LastIndexOf('/');
        var directory = slashIndex >= 0 ? path[..(slashIndex + 1)] : "";
        var fileName = slashIndex >= 0 ? path[(slashIndex + 1)..] : path;
        var dotIndex = fileName.LastIndexOf('.');
        var stem = dotIndex > 0 ? fileName[..dotIndex] : fileName;
        var extension = dotIndex > 0 ? fileName[dotIndex..] : "";
        for (var index = 2; index < 100; index++)
        {
            var candidate = $"{directory}{stem}-{index.ToString(CultureInfo.InvariantCulture)}{extension}";
            if (!files.Any(file => file.Path.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{directory}{stem}-{Guid.NewGuid():N}{extension}";
    }

    internal static string EscapePowerShellSingleQuoted(string value)
    {
        return (value ?? "").Replace("'", "''", StringComparison.Ordinal);
    }

    internal static AgentCommandSuggestion? ExtractCommandSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var labeledFenced = ExtractFencedCommandSuggestion(text, preferCommandProposalLabel: true);
        if (labeledFenced is not null)
        {
            return labeledFenced;
        }

        var firstFenced = ExtractFencedCommandSuggestion(text, preferCommandProposalLabel: false);
        if (firstFenced is not null)
        {
            return firstFenced;
        }

        var xmlCommand = ExtractXmlCommandSuggestion(text);
        if (xmlCommand is not null)
        {
            return xmlCommand;
        }

        var structuredCommand = ExtractStructuredCommandSuggestion(text);
        if (structuredCommand is not null)
        {
            return structuredCommand;
        }

        var promptLineCommand = ExtractPromptLineCommand(text);
        if (!string.IsNullOrWhiteSpace(promptLineCommand))
        {
            if (TryBuildShellFileWriteCommand(promptLineCommand, out var promptLineWriteCommand))
            {
                return new AgentCommandSuggestion("PowerShell", promptLineWriteCommand);
            }

            return new AgentCommandSuggestion("Terminal", promptLineCommand);
        }

        return ExtractLabeledCommand(text) ?? ExtractInlineCodeCommand(text);
    }

    internal static AgentCommandSuggestion NormalizeCommandSuggestion(AgentCommandSuggestion suggestion)
    {
        if (!suggestion.Shell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            return suggestion;
        }

        var command = NormalizePowerShellCommandSuggestion(suggestion.Command);
        return command.Equals(suggestion.Command, StringComparison.Ordinal)
            ? suggestion
            : suggestion with { Command = command };
    }

    private static string NormalizePowerShellCommandSuggestion(string command)
    {
        var normalized = PowerShellEchoNoNewlineRedirectRegex.Replace(
            command ?? "",
            match =>
            {
                var path = TrimPowerShellPathToken(match.Groups["path"].Value);
                var value = EscapePowerShellSingleQuoted(match.Groups["value"].Value);
                return $"Set-Content -LiteralPath '{EscapePowerShellSingleQuoted(path)}' -Value '{value}' -NoNewline";
            });

        normalized = PowerShellCommaFilterListingRegex.Replace(normalized, "");
        normalized = string.Join(
            Environment.NewLine,
            normalized
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
        return normalized;
    }

    private static string TrimPowerShellPathToken(string value)
    {
        return (value ?? "")
            .Trim()
            .Trim('"', '\'');
    }

    private static AgentCommandSuggestion? ExtractFencedCommandSuggestion(string text, bool preferCommandProposalLabel)
    {
        foreach (Match match in FencedCommandBlockRegex.Matches(text))
        {
            if (preferCommandProposalLabel && !HasCommandProposalLabelBefore(text, match.Index))
            {
                continue;
            }

            var rawBody = CommandFenceBody(text, match);
            var command = NormalizeCommandBlock(rawBody);
            if (!string.IsNullOrWhiteSpace(command))
            {
                var shell = ShellForCommandLanguage(match.Groups["lang"].Value);
                if (string.IsNullOrWhiteSpace(shell))
                {
                    if (preferCommandProposalLabel && TryBuildShellFileWriteCommand(command, out var plainFenceWriteCommand))
                    {
                        return new AgentCommandSuggestion("PowerShell", plainFenceWriteCommand);
                    }

                    if (!preferCommandProposalLabel || !LooksLikeRunnableCommand(command))
                    {
                        continue;
                    }

                    shell = InferShell(command);
                }

                if (shell.Equals("Terminal", StringComparison.OrdinalIgnoreCase)
                    && TryBuildShellFileWriteCommand(command, out var writeCommand))
                {
                    return new AgentCommandSuggestion("PowerShell", writeCommand);
                }

                return new AgentCommandSuggestion(shell, command);
            }
        }

        return null;
    }

    private static string CommandFenceBody(string text, Match match)
    {
        var body = match.Groups["body"].Value;
        if (!PowerShellHereStringMayNeedExtendedFence(body))
        {
            return body;
        }

        var extended = ReadFenceBodyPreservingPowerShellHereStrings(text, match.Groups["body"].Index);
        return string.IsNullOrWhiteSpace(extended) ? body : extended;
    }

    private static bool PowerShellHereStringMayNeedExtendedFence(string body)
    {
        var normalized = body ?? "";
        return normalized.Contains("@\"", StringComparison.Ordinal)
            || normalized.Contains("@'", StringComparison.Ordinal);
    }

    private static string ReadFenceBodyPreservingPowerShellHereStrings(string text, int bodyStartIndex)
    {
        var source = text ?? "";
        var remaining = source[Math.Clamp(bodyStartIndex, 0, source.Length)..]
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = remaining.Split('\n');
        var builder = new StringBuilder();
        var inDoubleQuotedHereString = false;
        var inSingleQuotedHereString = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!inDoubleQuotedHereString
                && !inSingleQuotedHereString
                && trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                break;
            }

            builder.AppendLine(line);

            if (inDoubleQuotedHereString)
            {
                if (trimmed.Equals("\"@", StringComparison.Ordinal))
                {
                    inDoubleQuotedHereString = false;
                }

                continue;
            }

            if (inSingleQuotedHereString)
            {
                if (trimmed.Equals("'@", StringComparison.Ordinal))
                {
                    inSingleQuotedHereString = false;
                }

                continue;
            }

            var rightTrimmed = line.TrimEnd();
            if (rightTrimmed.EndsWith("@\"", StringComparison.Ordinal))
            {
                inDoubleQuotedHereString = true;
            }
            else if (rightTrimmed.EndsWith("@'", StringComparison.Ordinal))
            {
                inSingleQuotedHereString = true;
            }
        }

        return builder.ToString();
    }

    private static bool TryBuildShellFileWriteCommand(string command, out string writeCommand)
    {
        var fileSuggestion = ExtractFileWriteSuggestion(command);
        if (fileSuggestion is not null)
        {
            writeCommand = BuildFileWriteCommand(fileSuggestion);
            return true;
        }

        writeCommand = "";
        return false;
    }

    private static AgentCommandSuggestion? ExtractXmlCommandSuggestion(string text)
    {
        foreach (Match match in XmlCommandBlockRegex.Matches(text))
        {
            var command = NormalizeCommandBlock(System.Net.WebUtility.HtmlDecode(match.Groups["body"].Value));
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var shell = ShellForCommandLanguage(match.Groups["shell"].Value);
            if ((string.IsNullOrWhiteSpace(shell) || shell.Equals("Terminal", StringComparison.OrdinalIgnoreCase))
                && TryBuildShellFileWriteCommand(command, out var writeCommand))
            {
                return new AgentCommandSuggestion("PowerShell", writeCommand);
            }

            if (!LooksLikeRunnableCommand(command))
            {
                continue;
            }

            return new AgentCommandSuggestion(string.IsNullOrWhiteSpace(shell) ? InferShell(command) : shell, command);
        }

        return null;
    }

    private static AgentCommandSuggestion? ExtractStructuredCommandSuggestion(string text)
    {
        foreach (var candidate in StructuredCommandJsonCandidates(text))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var suggestion = ExtractStructuredCommandSuggestion(document.RootElement);
                if (suggestion is not null)
                {
                    return suggestion;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> StructuredCommandJsonCandidates(string text)
    {
        foreach (Match match in FencedCommandBlockRegex.Matches(text))
        {
            var language = FirstLanguageToken(match.Groups["lang"].Value);
            if (language.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                yield return TrimCodeFenceBody(match.Groups["body"].Value);
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            yield return text[start..(end + 1)];
        }
    }

    private static AgentCommandSuggestion? ExtractStructuredCommandSuggestion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var nestedName in new[] { "command_proposal", "commandProposal", "proposal", "action" })
        {
            if (TryGetJsonProperty(element, nestedName, out var nested))
            {
                var nestedSuggestion = ExtractStructuredCommandSuggestion(nested);
                if (nestedSuggestion is not null)
                {
                    return nestedSuggestion;
                }
            }
        }

        var command = JsonString(element, "command")
            ?? JsonString(element, "cmd")
            ?? JsonString(element, "run");
        if (string.IsNullOrWhiteSpace(command) && TryGetJsonProperty(element, "commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            var lines = commands
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            command = lines.Length == 0 ? "" : string.Join(Environment.NewLine, lines);
        }

        command = NormalizeCommandBlock(command ?? "");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var shellValue = JsonString(element, "shell")
            ?? JsonString(element, "type")
            ?? JsonString(element, "language");
        var shell = ShellForCommandLanguage(shellValue ?? "");
        if ((string.IsNullOrWhiteSpace(shell) || shell.Equals("Terminal", StringComparison.OrdinalIgnoreCase))
            && TryBuildShellFileWriteCommand(command, out var writeCommand))
        {
            return new AgentCommandSuggestion("PowerShell", writeCommand);
        }

        if (!LooksLikeRunnableCommand(command))
        {
            return null;
        }

        return new AgentCommandSuggestion(string.IsNullOrWhiteSpace(shell) ? InferShell(command) : shell, command);
    }

    private static string? JsonString(JsonElement element, string propertyName)
    {
        return TryGetJsonProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetJsonProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasCommandProposalLabelBefore(string text, int fenceIndex)
    {
        var lookbackStart = Math.Max(0, fenceIndex - 160);
        var prefix = text[lookbackStart..fenceIndex];
        return prefix.Contains("Command proposal", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("Next command", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShellForCommandLanguage(string value)
    {
        var normalized = (value ?? "").Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return normalized.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" or "ps1" or "ps" => "PowerShell",
            "terminal" or "cmd" or "bat" or "batch" or "shell" or "sh" or "bash" or "zsh" => "Terminal",
            _ => ""
        };
    }

    private static string NormalizeCommandBlock(string value)
    {
        var lines = (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(RemovePromptPrefix)
            .ToArray();
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string ExtractPromptLineCommand(string text)
    {
        var commands = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            if (TryRemovePromptPrefix(rawLine, out var command))
            {
                commands.Add(command);
                if (TryParseHeredocHeader(command, out var marker, out _))
                {
                    while (index + 1 < lines.Length)
                    {
                        index++;
                        var heredocLine = lines[index].TrimEnd();
                        commands.Add(heredocLine);
                        if (HeredocMarkerMatches(heredocLine, marker))
                        {
                            break;
                        }
                    }
                }

                continue;
            }

            if (commands.Count > 0)
            {
                break;
            }
        }

        return string.Join(Environment.NewLine, commands).Trim();
    }

    private static AgentCommandSuggestion? ExtractLabeledCommand(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = LabeledCommandRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var label = match.Groups["label"].Value;
            var inline = match.Groups["command"].Value.Trim().Trim('`');
            if (!string.IsNullOrWhiteSpace(inline)
                && TryBuildShellFileWriteCommand(inline, out var inlineWriteCommand))
            {
                return new AgentCommandSuggestion("PowerShell", inlineWriteCommand);
            }

            if (!string.IsNullOrWhiteSpace(inline)
                && !inline.StartsWith("```", StringComparison.Ordinal)
                && LooksLikeRunnableCommand(inline))
            {
                var shell = label.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                    ? "PowerShell"
                    : label.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                        || label.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                        ? "Terminal"
                        : InferShell(inline);
                return new AgentCommandSuggestion(shell, RemovePromptPrefix(inline));
            }

            var following = CollectFollowingCommandLines(lines, index + 1);
            if (!string.IsNullOrWhiteSpace(following)
                && TryBuildShellFileWriteCommand(following, out var followingWriteCommand))
            {
                return new AgentCommandSuggestion("PowerShell", followingWriteCommand);
            }

            if (!string.IsNullOrWhiteSpace(following) && LooksLikeRunnableCommand(following))
            {
                var shell = label.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                    ? "PowerShell"
                    : label.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                        || label.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                        ? "Terminal"
                        : InferShell(following);
                return new AgentCommandSuggestion(shell, following);
            }
        }

        return null;
    }

    private static AgentCommandSuggestion? ExtractInlineCodeCommand(string text)
    {
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = InlineCodeCommandRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var command = RemovePromptPrefix(match.Groups["command"].Value.Trim());
            if (!string.IsNullOrWhiteSpace(command) && LooksLikeRunnableCommand(command))
            {
                return new AgentCommandSuggestion(InferShell(command), command);
            }
        }

        return null;
    }

    private static string CollectFollowingCommandLines(IReadOnlyList<string> lines, int startIndex)
    {
        var commands = new List<string>();
        for (var index = startIndex; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (commands.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                break;
            }

            if (line.EndsWith(":", StringComparison.Ordinal) && commands.Count > 0)
            {
                break;
            }

            commands.Add(RemovePromptPrefix(line.Trim('`')));
        }

        return string.Join(Environment.NewLine, commands).Trim();
    }

    private static bool LooksLikeRunnableCommand(string command)
    {
        var first = FirstCommandToken(command);
        return first.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || first.Equals("npm", StringComparison.OrdinalIgnoreCase)
            || first.Equals("npx", StringComparison.OrdinalIgnoreCase)
            || first.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
            || first.Equals("yarn", StringComparison.OrdinalIgnoreCase)
            || first.Equals("python", StringComparison.OrdinalIgnoreCase)
            || first.Equals("py", StringComparison.OrdinalIgnoreCase)
            || first.Equals("node", StringComparison.OrdinalIgnoreCase)
            || first.Equals("bun", StringComparison.OrdinalIgnoreCase)
            || first.Equals("cargo", StringComparison.OrdinalIgnoreCase)
            || first.Equals("go", StringComparison.OrdinalIgnoreCase)
            || first.Equals("rustc", StringComparison.OrdinalIgnoreCase)
            || first.Equals("git", StringComparison.OrdinalIgnoreCase)
            || first.Equals("rg", StringComparison.OrdinalIgnoreCase)
            || first.Equals("ls", StringComparison.OrdinalIgnoreCase)
            || first.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || first.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Set-Location", StringComparison.OrdinalIgnoreCase)
            || first.Equals("New-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("ni", StringComparison.OrdinalIgnoreCase)
            || first.Equals("md", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Set-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("sc", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Add-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Out-File", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Move-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Get-ChildItem", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Get-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Select-String", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Test-Path", StringComparison.OrdinalIgnoreCase)
            || first.Equals("dir", StringComparison.OrdinalIgnoreCase)
            || first.Equals("type", StringComparison.OrdinalIgnoreCase)
            || first.Equals("where", StringComparison.OrdinalIgnoreCase)
            || first.Equals("echo", StringComparison.OrdinalIgnoreCase)
            || first.Equals("mkdir", StringComparison.OrdinalIgnoreCase)
            || first.Equals("copy", StringComparison.OrdinalIgnoreCase)
            || first.Equals("xcopy", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferShell(string command)
    {
        var first = FirstCommandToken(command);
        return first.Equals("New-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("ni", StringComparison.OrdinalIgnoreCase)
            || first.Equals("md", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Set-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("sc", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Get-ChildItem", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Get-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Select-String", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Test-Path", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Add-Content", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Out-File", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Move-Item", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Set-Location", StringComparison.OrdinalIgnoreCase)
            || first.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || first.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? "PowerShell"
            : "Terminal";
    }

    internal static string FirstCommandToken(string command)
    {
        foreach (var rawLine in (command ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = RemovePromptPrefix(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("#", StringComparison.Ordinal)
                || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }

        return "";
    }

    private static string RemovePromptPrefix(string line)
    {
        return TryRemovePromptPrefix(line, out var command) ? command : line.TrimEnd();
    }

    private static bool TryRemovePromptPrefix(string line, out string command)
    {
        var trimmed = (line ?? "").TrimStart();
        foreach (var prefix in new[] { "PS> ", "PS C:\\> ", "$ ", "> " })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                command = trimmed[prefix.Length..].TrimEnd();
                return !string.IsNullOrWhiteSpace(command);
            }
        }

        command = "";
        return false;
    }

}
