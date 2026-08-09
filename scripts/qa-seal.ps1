<#
.SYNOPSIS
    Runs AI Arena's local verification gates and writes a privacy-safe QA evidence bundle.

.DESCRIPTION
    This is the single release-seal orchestrator. The default invocation performs two
    clean Release passes, all available console harnesses and static gates, and a
    control-plane-driven rendered-UI smoke capture. A seal is valid only when the
    source tree stays clean and stable. A successful complete run remains partial
    until qa-accept-inspection.ps1 binds explicit user inspection to the exact
    screenshot, automation, and source-tree hashes.

    Evidence is written below artifacts/qa/<run-id>. Command output is measured and
    hashed in memory, but is never persisted: source, prompts, responses, transcripts,
    credentials, and absolute private paths therefore cannot enter the bundle.

.EXAMPLE
    .\scripts\qa-seal.ps1

.EXAMPLE
    .\scripts\qa-seal.ps1 -Passes 1 -SkipRenderedUi -AllowPartial

.EXAMPLE
    .\scripts\qa-seal.ps1 -PlanOnly -AllowPartial
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 5)]
    [int]$Passes = 2,

    [switch]$SkipRenderedUi,
    [switch]$AllowPartial,
    [switch]$PlanOnly,

    [ValidateRange(5, 180)]
    [int]$UiStartupTimeoutSeconds = 45,

    [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,63}$')]
    [string]$RunId = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'qa-ui-matrix.ps1')

$script:Schema = 'ai_arena.qa_evidence.v1'
$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:SolutionPath = Join-Path $script:RepositoryRoot 'AI Arena.slnx'
$script:MapRoot = Join-Path $script:RepositoryRoot 'map'
$script:ArtifactRoot = Join-Path $script:RepositoryRoot 'artifacts\qa'
$script:PowerShellExecutable = (Get-Process -Id $PID).Path
$script:GateResults = [System.Collections.Generic.List[object]]::new()
$script:Artifacts = [System.Collections.Generic.List[object]]::new()
$script:CurrentGateTrace = $null
$script:CurrentGatePassed = 0
$script:CurrentGateFailed = 0
$script:CurrentGateTimedOut = $false
$script:ExecutionFailure = $false
$script:UiStartupMeasurements = [System.Collections.Generic.List[object]]::new()
$script:UiMatrixCells = [System.Collections.Generic.List[object]]::new()
$script:VerificationMeasurements = @()
$script:LastAuthoritativeValidationIssues = @()

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = '{0}-{1}' -f (Get-Date).ToUniversalTime().ToString("yyyyMMdd't'HHmmss'z'"), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
}

$script:RunRelativePath = "artifacts/qa/$RunId"
$script:RunRoot = Join-Path $script:ArtifactRoot $RunId
$script:LogsRoot = Join-Path $script:RunRoot 'logs'
$script:ScreenshotsRoot = Join-Path $script:RunRoot 'screenshots'
$script:AutomationRoot = Join-Path $script:RunRoot 'automation'
$script:MetadataRoot = Join-Path $script:RunRoot 'metadata'
$startedAtUtc = (Get-Date).ToUniversalTime()

function ConvertTo-SafeRelativePath {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $script:RunRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'QA artifact escaped its run directory.'
    }

    $relative = $fullPath.Substring($rootWithSeparator.Length).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or
        $relative.StartsWith('/', [StringComparison]::Ordinal) -or
        $relative.Split('/') -contains '..') {
        throw 'QA artifact path is not a safe relative path.'
    }

    return $relative
}

function Get-Sha256Text {
    param([AllowEmptyString()] [string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-Sha256File {
    param([Parameter(Mandatory)] [string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Protect-QaText {
    param([AllowEmptyString()] [string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $safe = $Text
    $privateRoots = @(
        $script:RepositoryRoot,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [IO.Path]::GetTempPath().TrimEnd('\', '/')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object Length -Descending -Unique

    foreach ($privateRoot in $privateRoots) {
        $safe = [regex]::Replace($safe, [regex]::Escape($privateRoot), '<private-root>', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }

    $safe = [regex]::Replace($safe, '(?i)\b(?:sk|pk|rk)-[a-z0-9_-]{10,}\b|\bgh[pousr]_[a-z0-9]{8,}\b', '<redacted-secret>')
    $safe = [regex]::Replace($safe, '(?i)\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|client[_-]?secret|password|secret)\s*[:=]\s*[^\s,;]+', '<redacted-secret>')
    $safe = [regex]::Replace($safe, '(?i)\bbearer\s+[a-z0-9._~+/=-]{10,}', '<redacted-secret>')
    $safe = [regex]::Replace($safe, '(?i)(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/])[^\r\n]*', '<redacted-path>')
    $safe = [regex]::Replace($safe, '(?i)/(?:users|home|root|opt|mnt|private|var|tmp|etc)(?:/[^\s"'']*)?', '<redacted-path>')
    return $safe
}

function Resolve-NativeCommandPath {
    param([Parameter(Mandatory)] [string]$Command)

    if ([IO.Path]::IsPathRooted($Command)) {
        if (-not (Test-Path -LiteralPath $Command -PathType Leaf)) {
            throw 'Native command path does not exist.'
        }

        return [IO.Path]::GetFullPath($Command)
    }

    $resolved = Get-Command -Name $Command -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    if ($null -eq $resolved -or
        [string]::IsNullOrWhiteSpace([string]$resolved.Source) -or
        -not (Test-Path -LiteralPath $resolved.Source -PathType Leaf)) {
        throw 'Native command could not be resolved to an executable file.'
    }

    return [IO.Path]::GetFullPath([string]$resolved.Source)
}

function Get-SafeValidatorIssueCodes {
    param([AllowEmptyString()] [string]$Text)

    $codes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unrecognized = $false
    foreach ($line in @($Text -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^FAIL\s+(?<code>[a-z0-9][a-z0-9._-]{0,95})(?:\s|$)') {
            [void]$codes.Add($Matches['code'])
            continue
        }
        if ($line -match '^PASS\s+[a-z0-9][a-z0-9._-]{0,95}(?:\s|$)') {
            continue
        }

        $unrecognized = $true
    }

    if ($unrecognized) {
        [void]$codes.Add('validator.unrecognized_output')
    }
    if ($codes.Count -eq 0) {
        [void]$codes.Add('validator.nonzero_without_issue')
    }

    return @($codes | Sort-Object)
}

function Get-OrdinalStringArray {
    param([AllowEmptyCollection()] [string[]]$Values = @())

    [string[]]$ordered = @($Values)
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    return $ordered
}

function Test-QaTextPrivacy {
    param([AllowEmptyString()] [string]$Text)

    $patterns = @(
        '(?i)(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/])',
        '(?i)/(?:users|home|root|opt|mnt|private|var|tmp|etc)(?:/[^\s"'']*)?',
        '(?i)\b(?:sk|pk|rk)-[a-z0-9_-]{10,}\b|\bgh[pousr]_[a-z0-9]{8,}\b',
        '(?i)\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|client[_-]?secret|password|secret)\s*[:=]',
        '(?i)\bbearer\s+[a-z0-9._~+/=-]{10,}',
        '(?i)"(?:rawOutput|rawResponse|rawTranscript|sourceCode|sourceContent|sourceText|transcriptContent|promptContent|responseContent)"\s*:'
    )

    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch($Text, $pattern)) {
            return $false
        }
    }

    return $true
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Text
    )

    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Add-QaArtifact {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [Parameter(Mandatory)] [string]$Kind,
        [Parameter(Mandatory)] [string]$Path,
        [AllowNull()] [object]$Provenance = $null
    )

    $relativePath = ConvertTo-SafeRelativePath $Path
    if ($script:Artifacts.Where({ $_.id -eq $Id }).Count -ne 0) {
        throw "Duplicate QA artifact ID: $Id"
    }

    $script:Artifacts.Add([pscustomobject][ordered]@{
        id = $Id
        kind = $Kind
        relativePath = $relativePath
        sha256 = Get-Sha256File $Path
        provenance = $Provenance
    })
}

function New-EvidenceAssertion {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [Parameter(Mandatory)] [ValidateSet('observed', 'inferred', 'unavailable')] [string]$State,
        [Parameter(Mandatory)] [string]$Summary,
        [AllowNull()] [string]$ReferenceId = $null,
        [AllowNull()] [string]$Basis = $null,
        [AllowNull()] [string]$Limitation = $null
    )

    $result = [ordered]@{
        id = $Id
        state = $State
        summary = Protect-QaText $Summary
    }
    if (-not [string]::IsNullOrWhiteSpace($ReferenceId)) {
        $result.referenceId = $ReferenceId
    }
    if (-not [string]::IsNullOrWhiteSpace($Basis)) {
        $result.basis = Protect-QaText $Basis
    }
    if (-not [string]::IsNullOrWhiteSpace($Limitation)) {
        $result.limitation = Protect-QaText $Limitation
    }
    return [pscustomobject]$result
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()] [string]$Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Add-GateTrace {
    param([Parameter(Mandatory)] [string]$Text)

    if ($null -eq $script:CurrentGateTrace) {
        throw 'Gate trace is not active.'
    }

    $script:CurrentGateTrace.Add((Protect-QaText $Text))
}

function Stop-QaProcessTree {
    param([Parameter(Mandatory)] [Diagnostics.Process]$Process)

    if ($Process.HasExited) {
        return
    }

    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 1>$null 2>$null
    }

    if (-not $Process.HasExited) {
        try { $Process.Kill() } catch { }
    }
}

function Invoke-CapturedCommand {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$DisplayCommand,
        [switch]$CaptureResult,
        [ValidateRange(5, 7200)] [int]$TimeoutSeconds = 900
    )

    # ProcessStartInfo with UseShellExecute=false must receive the resolved
    # batch-file path on Windows. Passing only "npm.cmd" makes cmd.exe assign
    # an incorrect %~dp0 and causes npm to search for its modules under the
    # repository working directory.
    $resolvedFilePath = Resolve-NativeCommandPath -Command $FilePath
    $commandStarted = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $resolvedFilePath
    $process.StartInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $process.StartInfo.WorkingDirectory = $script:RepositoryRoot
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true

    $stdout = ''
    $stderr = ''
    $exitCode = -1
    $timedOut = $false
    try {
        if (-not $process.Start()) {
            throw 'Native process did not start.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $script:CurrentGateTimedOut = $true
            Stop-QaProcessTree -Process $process
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not $timedOut) {
            $exitCode = $process.ExitCode
        }
    }
    finally {
        $commandStarted.Stop()
        $process.Dispose()
    }

    $stdoutLines = if ([string]::IsNullOrEmpty($stdout)) { 0 } else { ([regex]::Matches($stdout, '\r?\n')).Count + 1 }
    $stderrLines = if ([string]::IsNullOrEmpty($stderr)) { 0 } else { ([regex]::Matches($stderr, '\r?\n')).Count + 1 }
    $passedMarkers = ([regex]::Matches($stdout, '(?im)^\s*(?:\[pass\]|pass\b)')).Count
    $failedMarkers = ([regex]::Matches(($stdout + "`n" + $stderr), '(?im)^\s*(?:\[fail\]|fail\b)')).Count

    if ($passedMarkers -eq 0 -and $stdout -match '(?im)\b(?<passed>\d+)\s*/\s*(?<total>\d+)\b') {
        $reportedPassed = [int]$Matches['passed']
        $reportedTotal = [int]$Matches['total']
        if ($reportedPassed -le $reportedTotal) {
            $passedMarkers = $reportedPassed
            $failedMarkers += ($reportedTotal - $reportedPassed)
        }
    }

    $script:CurrentGatePassed += $passedMarkers
    $script:CurrentGateFailed += $failedMarkers
    Add-GateTrace "command=$DisplayCommand"
    Add-GateTrace ("exitCode={0}; durationMilliseconds={1}; stdoutLines={2}; stderrLines={3}" -f $exitCode, $commandStarted.ElapsedMilliseconds, $stdoutLines, $stderrLines)
    Add-GateTrace ("deadlineSeconds={0}; timedOut={1}" -f $TimeoutSeconds, $timedOut.ToString().ToLowerInvariant())
    Add-GateTrace ("stdoutSha256={0}; stderrSha256={1}" -f (Get-Sha256Text $stdout), (Get-Sha256Text $stderr))
    Add-GateTrace 'outputCapturePolicy=hash-and-count-only; raw command output was discarded'

    if ($timedOut) {
        throw [TimeoutException]::new("Command exceeded its $TimeoutSeconds-second QA deadline.")
    }

    if ($exitCode -ne 0 -and -not $CaptureResult) {
        if ($failedMarkers -eq 0) {
            $script:CurrentGateFailed++
        }
        throw "Command failed with exit code $exitCode."
    }

    if ($CaptureResult) {
        return [pscustomobject]@{
            ExitCode = $exitCode
            Stdout = $stdout
            Stderr = $stderr
        }
    }
}

function Write-GateLog {
    param(
        [Parameter(Mandatory)] [string]$GateId,
        [Parameter(Mandatory)] [System.Collections.Generic.List[string]]$Trace
    )

    $fileName = $GateId + '.log'
    $path = Join-Path $script:LogsRoot $fileName
    $content = @(
        'AI Arena QA gate evidence'
        "gate=$GateId"
        'contentPolicy=no source, prompt, response, transcript, credential, private path, or raw command output'
        $Trace
    ) -join "`n"
    $content = Protect-QaText $content
    Write-Utf8NoBom -Path $path -Text ($content + "`n")
    $artifactId = "artifact.$GateId.log"
    Add-QaArtifact -Id $artifactId -Kind 'sanitized-gate-log' -Path $path
    return $artifactId
}

function Add-QaGate {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [Parameter(Mandatory)] [string]$DisplayName,
        [Parameter(Mandatory)] [bool]$Required,
        [Parameter(Mandatory)] [ValidateSet('run', 'pass', 'fail', 'partial', 'blocked', 'unavailable')] [string]$Mode,
        [string]$Reason = '',
        [string]$PassSummary = '',
        [scriptblock]$Action
    )

    Write-Host ("[{0}] {1}" -f $Id, $DisplayName)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $script:CurrentGateTrace = [System.Collections.Generic.List[string]]::new()
    $script:CurrentGatePassed = 0
    $script:CurrentGateFailed = 0
    $script:CurrentGateTimedOut = $false
    $outcome = $Mode
    $summary = $Reason

    try {
        Add-GateTrace "name=$DisplayName"
        Add-GateTrace "required=$($Required.ToString().ToLowerInvariant())"
        if ($Mode -eq 'run') {
            if ($null -eq $Action) {
                throw 'Runnable gate is missing its action.'
            }
            & $Action
            $outcome = 'pass'
            if ($script:CurrentGatePassed -eq 0) {
                $script:CurrentGatePassed = 1
            }
            $summary = if ([string]::IsNullOrWhiteSpace($PassSummary)) {
                'The required command set completed successfully.'
            }
            else {
                $PassSummary
            }
        }
        elseif ([string]::IsNullOrWhiteSpace($summary)) {
            $summary = "Gate recorded as $Mode."
        }
        if ($outcome -eq 'fail') {
            $script:ExecutionFailure = $true
            $script:CurrentGateFailed = [Math]::Max(1, $script:CurrentGateFailed)
        }
        Add-GateTrace "outcome=$outcome"
    }
    catch {
        $outcome = if ($script:CurrentGateTimedOut) { 'blocked' } else { 'fail' }
        $script:ExecutionFailure = $true
        if (-not $script:CurrentGateTimedOut -and $script:CurrentGateFailed -eq 0) {
            $script:CurrentGateFailed = 1
        }
        $summary = if ($script:CurrentGateTimedOut) {
            'The command deadline expired. This is timeout evidence, not a failed test assertion.'
        }
        else {
            'The command set failed. Re-run the named gate directly for diagnostic output.'
        }
        Add-GateTrace "outcome=$outcome; diagnosticPolicy=exception details omitted from evidence"
    }
    finally {
        $watch.Stop()
    }

    $artifactId = Write-GateLog -GateId $Id -Trace $script:CurrentGateTrace
    $evidenceState = if ($outcome -in @('blocked', 'unavailable', 'partial')) { 'unavailable' } else { 'observed' }
    $limitation = if ($evidenceState -eq 'unavailable') { $summary } else { $null }
    $evidence = New-EvidenceAssertion `
        -Id "evidence.$Id" `
        -State $evidenceState `
        -Summary $summary `
        -ReferenceId $artifactId `
        -Limitation $limitation

    $testsPassed = if ($outcome -eq 'pass') { [Math]::Max(1, $script:CurrentGatePassed) } else { $script:CurrentGatePassed }
    $testsFailed = if ($outcome -eq 'fail') { [Math]::Max(1, $script:CurrentGateFailed) } else { $script:CurrentGateFailed }
    $script:GateResults.Add([pscustomobject][ordered]@{
        id = $Id
        outcome = $outcome
        required = $Required
        durationMilliseconds = [long]$watch.ElapsedMilliseconds
        tests = [ordered]@{
            passed = [int]$testsPassed
            failed = [int]$testsFailed
            skipped = 0
            total = [int]($testsPassed + $testsFailed)
        }
        evidence = $evidence
    })
    $script:CurrentGateTrace = $null
    $script:CurrentGateTimedOut = $false
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [string]$RepositoryRoot = $script:RepositoryRoot
    )

    $output = @(& git -C $RepositoryRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git repository inspection failed.'
    }
    return $output
}

function Get-SourceFingerprint {
    param(
        [string]$RepositoryRoot = $script:RepositoryRoot,
        [string[]]$ExcludedPrefixes = @('artifacts/')
    )

    $paths = @(Get-GitOutput -Arguments @('-c', 'core.quotepath=false', 'ls-files', '--cached', '--others', '--exclude-standard') -RepositoryRoot $RepositoryRoot) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    $manifest = [Text.StringBuilder]::new()
    foreach ($relativePath in $paths) {
        $normalized = ([string]$relativePath).Replace('\', '/')
        if (@($ExcludedPrefixes | Where-Object { $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
            continue
        }
        if ([IO.Path]::IsPathRooted($normalized) -or $normalized.Split('/') -contains '..') {
            throw 'Git returned an unsafe source path.'
        }

        $fullPath = Join-Path $RepositoryRoot $relativePath
        $contentHash = if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            Get-Sha256File $fullPath
        }
        else {
            'deleted'
        }
        [void]$manifest.Append($normalized).Append([char]0).Append($contentHash).Append("`n")
    }

    return Get-Sha256Text $manifest.ToString()
}

function Get-WorkingTreeState {
    param([string]$RepositoryRoot = $script:RepositoryRoot)

    $status = @(Get-GitOutput -Arguments @('status', '--porcelain=v1', '--untracked-files=all') -RepositoryRoot $RepositoryRoot)
    return [ordered]@{
        clean = $status.Count -eq 0
        changedPathCount = $status.Count
    }
}

function Get-CompositeSourceFingerprint {
    $outer = Get-SourceFingerprint
    $map = Get-SourceFingerprint -RepositoryRoot $script:MapRoot -ExcludedPrefixes @()
    return Get-Sha256Text ("outer={0}`nmap={1}`n" -f $outer, $map)
}

function Assert-SourceFingerprintUnchanged {
    param(
        [Parameter(Mandatory)] [string]$Expected,
        [Parameter(Mandatory)] [string]$Boundary
    )

    $observed = Get-CompositeSourceFingerprint
    if (-not [string]::Equals($observed, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source fingerprint drifted at QA boundary '$Boundary'."
    }
}

function Invoke-PowerShellScriptGate {
    param(
        [Parameter(Mandatory)] [string]$RelativePath,
        [string[]]$Arguments = @()
    )

    $scriptPath = Join-Path $script:RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw 'Required QA script is unavailable.'
    }

    $nativeArguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) + $Arguments
    $displayArgs = if ($Arguments.Count -eq 0) { '' } else { ' ' + ($Arguments -join ' ') }
    Invoke-CapturedCommand -FilePath $script:PowerShellExecutable -Arguments $nativeArguments -DisplayCommand ("powershell {0}{1}" -f $RelativePath.Replace('\', '/'), $displayArgs)
}

function Invoke-FullHarness {
    param(
        [Parameter(Mandatory)] [string]$ProjectPath,
        [Parameter(Mandatory)] [string]$DisplayProject
    )

    $previousFilter = $env:AIARENA_TEST_FILTER
    try {
        Remove-Item Env:AIARENA_TEST_FILTER -ErrorAction SilentlyContinue
        Invoke-CapturedCommand -FilePath 'dotnet' -Arguments @('run', '--project', $ProjectPath, '--no-build', '--no-restore', '-c', 'Release') -DisplayCommand "dotnet run --project $DisplayProject --no-build --no-restore -c Release"
    }
    finally {
        if ($null -eq $previousFilter) {
            Remove-Item Env:AIARENA_TEST_FILTER -ErrorAction SilentlyContinue
        }
        else {
            $env:AIARENA_TEST_FILTER = $previousFilter
        }
    }
}

function Test-AIArenaProcessRunning {
    $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq 'AI Arena' -or $_.ProcessName -eq 'AIArena.Wpf'
    })
    return $processes.Count -gt 0
}

function Get-PngRenderEvidence {
    param([Parameter(Mandatory)] [string]$Path)

    Add-Type -AssemblyName System.Drawing
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 1024) {
        throw 'Rendered UI PNG is too small to contain credible window evidence.'
    }

    $stream = [IO.MemoryStream]::new($bytes, $false)
    $image = $null
    $bitmap = $null
    try {
        $image = [Drawing.Image]::FromStream($stream, $false, $true)
        if ($image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or
            $image.Width -lt 320 -or $image.Height -lt 240 -or
            $image.Width -gt 20000 -or $image.Height -gt 20000) {
            throw 'Rendered UI artifact has an unsupported format or dimensions.'
        }

        $bitmap = [Drawing.Bitmap]::new($image)
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($xStep = 0; $xStep -lt 9; $xStep++) {
            $x = [Math]::Min($bitmap.Width - 1, [int](($bitmap.Width - 1) * $xStep / 8))
            for ($yStep = 0; $yStep -lt 9; $yStep++) {
                $y = [Math]::Min($bitmap.Height - 1, [int](($bitmap.Height - 1) * $yStep / 8))
                [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb())
            }
        }
        if ($colors.Count -lt 5) {
            throw 'Rendered UI artifact appears blank or visually uniform.'
        }

        return [pscustomobject]@{
            widthPixels = $image.Width
            heightPixels = $image.Height
            bytes = $bytes.Length
            sampledColors = $colors.Count
        }
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        if ($null -ne $image) { $image.Dispose() }
        $stream.Dispose()
    }
}

function Remove-IsolatedQaData {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$OwnerToken
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $leaf = Split-Path -Leaf $fullPath
    if (-not $fullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith('ai-arena-qa-', [StringComparison]::Ordinal)) {
        throw 'Refusing to remove an unverified QA data directory.'
    }

    if (Test-Path -LiteralPath $fullPath) {
        $marker = Join-Path $fullPath '.ai-arena-qa-owner'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            -not [string]::Equals((Get-Content -LiteralPath $marker -Raw).Trim(), $OwnerToken, [StringComparison]::Ordinal)) {
            throw 'Refusing to remove a QA data directory without its matching ownership marker.'
        }
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Resolve-IsolatedQaArtifact {
    param(
        [Parameter(Mandatory)] [string]$DataRoot,
        [Parameter(Mandatory)] [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw 'The app returned an unsafe relative QA artifact path.'
    }

    $root = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\', '/')
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath.Replace('/', '\')))
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The app QA artifact escaped its isolated data root.'
    }

    $cursor = $path
    while (-not [string]::Equals($cursor, $root, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The app QA artifact traversed a reparse point.'
            }
        }
        $cursor = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($cursor)) {
            throw 'The app QA artifact has an invalid parent chain.'
        }
    }

    return $path
}

function Copy-QaArtifactAtomic {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        throw 'A QA artifact destination already exists.'
    }
    $directory = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporary = Join-Path $directory ('.{0}.{1}.tmp' -f (Split-Path -Leaf $Destination), [Guid]::NewGuid().ToString('N'))
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Test-AIArenaQaSafeAutomationValue {
    param([AllowNull()] [object]$Value)

    $text = [string]$Value
    return $text.Length -gt 0 -and
        $text.Length -le 128 -and
        $text -cmatch '^[A-Za-z0-9_.:#-]+$'
}

function Test-AIArenaQaSameFocus {
    param(
        [AllowNull()] [object]$FirstIdentity,
        [AllowNull()] [object]$FirstControlType,
        [AllowNull()] [object]$SecondIdentity,
        [AllowNull()] [object]$SecondControlType)

    return [string]$FirstIdentity -ceq [string]$SecondIdentity -and
        [string]$FirstControlType -ceq [string]$SecondControlType
}

function Test-AIArenaQaFocusStep {
    param(
        [AllowNull()] [object]$Step,
        [Parameter(Mandatory)] [ValidateSet('next', 'previous')] [string]$Direction)

    if ($null -eq $Step) {
        return $false
    }

    try {
        return [string]$Step.direction -ceq $Direction -and
            [bool]$Step.moved -and
            [bool]$Step.focusChanged -and
            (Test-AIArenaQaSafeAutomationValue $Step.beforeIdentity) -and
            (Test-AIArenaQaSafeAutomationValue $Step.beforeControlType) -and
            (Test-AIArenaQaSafeAutomationValue $Step.afterIdentity) -and
            (Test-AIArenaQaSafeAutomationValue $Step.afterControlType) -and
            [string]$Step.afterIdentity -cne 'none' -and
            -not (Test-AIArenaQaSameFocus `
                $Step.beforeIdentity `
                $Step.beforeControlType `
                $Step.afterIdentity `
                $Step.afterControlType)
    }
    catch {
        return $false
    }
}

function Test-AIArenaQaFocusCycle {
    param(
        [AllowNull()] [object]$Next,
        [AllowNull()] [object]$Previous,
        [AllowNull()] [object]$Capture)

    return (Test-AIArenaQaFocusStep -Step $Next -Direction next) -and
        (Test-AIArenaQaFocusStep -Step $Previous -Direction previous) -and
        (Test-AIArenaQaFocusStep -Step $Capture -Direction next) -and
        (Test-AIArenaQaSameFocus $Next.afterIdentity $Next.afterControlType $Previous.beforeIdentity $Previous.beforeControlType) -and
        (Test-AIArenaQaSameFocus $Previous.afterIdentity $Previous.afterControlType $Capture.beforeIdentity $Capture.beforeControlType) -and
        (Test-AIArenaQaSameFocus $Next.beforeIdentity $Next.beforeControlType $Previous.afterIdentity $Previous.afterControlType) -and
        (Test-AIArenaQaSameFocus $Next.afterIdentity $Next.afterControlType $Capture.afterIdentity $Capture.afterControlType)
}

function Invoke-RenderedUiSmoke {
    param([Parameter(Mandatory)] [int]$PassNumber)

    $exe = Join-Path $script:RepositoryRoot 'src\AIArena.Wpf\bin\Release\net10.0-windows\AI Arena.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'Release application executable is unavailable.'
    }
    if (Test-AIArenaProcessRunning) {
        throw 'Rendered UI smoke cannot safely run while another AI Arena process is active.'
    }

    $ownerToken = [Guid]::NewGuid().ToString('N')
    $isolatedData = Join-Path ([IO.Path]::GetTempPath()) ("ai-arena-qa-{0}-pass-{1:D2}-{2}" -f $RunId, $PassNumber, $ownerToken)
    $previousDataRoot = $env:AI_ARENA_DATA_DIR
    $process = $null
    try {
        if (Test-Path -LiteralPath $isolatedData) {
            throw 'Fresh QA data directory unexpectedly already exists.'
        }
        New-Item -ItemType Directory -Path $isolatedData | Out-Null
        Write-Utf8NoBom -Path (Join-Path $isolatedData '.ai-arena-qa-owner') -Text ($ownerToken + "`n")
        $env:AI_ARENA_DATA_DIR = $isolatedData
        . (Join-Path $script:RepositoryRoot 'scripts\ai-arena-control.ps1')
        $startupWatch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds($UiStartupTimeoutSeconds)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) {
                break
            }
            try {
                $null = Invoke-AIArena status -TimeoutMs 1500
                $ready = $true
                break
            }
            catch {
                continue
            }
        }
        if (-not $ready) {
            throw 'Release application did not answer its local control plane before timeout.'
        }
        $startupWatch.Stop()
        $script:UiStartupMeasurements.Add([pscustomobject]@{
            passNumber = $PassNumber
            durationMilliseconds = [long]$startupWatch.ElapsedMilliseconds
        })

        $navigation = Select-AIArenaView 'arena'
        if (-not $navigation.ok -or
            [string]$navigation.state.view -ne 'arena' -or
            [string]$navigation.data.selectedView -ne 'arena') {
            throw 'Control-plane navigation did not positively observe the Arena surface.'
        }
        $themes = @('dark-blue', 'light', 'high-contrast')
        $viewports = @(
            [pscustomobject]@{ Width = 960; Height = 640 },
            [pscustomobject]@{ Width = 1500; Height = 960 }
        )
        $renderScales = @(
            [pscustomobject]@{ Value = [double]1.0; Label = '1-0' },
            [pscustomobject]@{ Value = [double]1.5; Label = '1-5' },
            [pscustomobject]@{ Value = [double]2.0; Label = '2-0' }
        )
        $motionModes = @('normal', 'reduced')

        foreach ($theme in $themes) {
            $themeResult = Set-AIArenaTheme $theme
            if (-not $themeResult.ok) {
                throw 'Control-plane QA theme selection failed.'
            }
            foreach ($viewport in $viewports) {
                $sized = Set-AIArenaQAWindowSize -Width $viewport.Width -Height $viewport.Height -TimeoutMs 10000
                if (-not $sized.ok) {
                    throw 'Control-plane QA window sizing failed.'
                }
                foreach ($motionMode in $motionModes) {
                    $motion = Set-AIArenaQAMotion -Mode $motionMode -TimeoutMs 10000
                    $expectedMotionSource = if ($motionMode -eq 'normal') { 'qa-normal' } else { 'qa-reduced' }
                    $expectedAnimations = $motionMode -eq 'normal'
                    if (-not $motion.ok -or
                        [string]$motion.data.preferenceSource -ne $expectedMotionSource -or
                        [bool]$motion.data.animationsEnabled -ne $expectedAnimations) {
                        throw 'Control-plane QA motion selection did not produce the requested process-only state.'
                    }

                    foreach ($renderScale in $renderScales) {
                        Start-Sleep -Milliseconds 100
                        $seedFocus = Move-AIArenaQAFocus -Direction next -TimeoutMs 10000
                        $nextFocus = Move-AIArenaQAFocus -Direction next -TimeoutMs 10000
                        $previousFocus = Move-AIArenaQAFocus -Direction previous -TimeoutMs 10000
                        $captureFocus = Move-AIArenaQAFocus -Direction next -TimeoutMs 10000
                        if (-not $seedFocus.ok -or
                            -not $nextFocus.ok -or -not $previousFocus.ok -or
                            -not $captureFocus.ok -or
                            -not (Test-AIArenaQaFocusStep -Step $seedFocus.data -Direction next) -or
                            -not (Test-AIArenaQaSameFocus `
                                $seedFocus.data.afterIdentity `
                                $seedFocus.data.afterControlType `
                                $nextFocus.data.beforeIdentity `
                                $nextFocus.data.beforeControlType) -or
                            -not (Test-AIArenaQaFocusCycle `
                                -Next $nextFocus.data `
                                -Previous $previousFocus.data `
                                -Capture $captureFocus.data)) {
                            throw 'Programmatic WPF focus traversal did not move in both directions and restore a visible capture focus.'
                        }

                        $cellKey = "p$($PassNumber.ToString('D2')).$theme.w$($viewport.Width).d$($renderScale.Label).$motionMode"
                        $expectedState = "arena-empty.closed.$theme.w$($viewport.Width).d$($renderScale.Label).$motionMode"
                        $structure = Save-AIArenaUIStructure `
                            -TreeFingerprint $sourceFingerprintStart `
                            -ExpectedState $expectedState `
                            -Path ("pass-{0:D2}/{1}.json" -f $PassNumber, $cellKey) `
                            -RenderDpiScale $renderScale.Value `
                            -TimeoutMs 10000
                        if (-not $structure.ok -or
                            [string]$structure.data.artifactKind -ne 'automation-tree' -or
                            [string]$structure.data.pathBase -ne 'data-root' -or
                            [bool]$structure.data.truncated -or
                            [string]$structure.data.theme -ne $theme -or
                            [string]$structure.data.expectedStateSource -ne 'observed-visible-roots' -or
                            [string]$structure.data.selectedView -ne 'arena' -or
                            [string]$structure.data.observedSurfaceState -ne 'arena-empty' -or
                            [string]$structure.data.dialogState -ne 'closed' -or
                            @($structure.data.visibleRootIdentities).Count -ne 1 -or
                            [string]$structure.data.visibleRootIdentities[0] -ne 'TranscriptPanel' -or
                            (@($structure.data.requiredControlIdentities | Sort-Object) -join ',') -ne 'RootLayout,ShellNavigationRail,ShellTopBar,TranscriptItems,TranscriptPanel' -or
                            [int]$structure.data.viewportWidthDip -ne $viewport.Width -or
                            [int]$structure.data.viewportHeightDip -ne $viewport.Height -or
                            [math]::Abs([double]$structure.data.renderDpiScale - $renderScale.Value) -gt 0.001 -or
                            -not [bool]$structure.data.renderDpiOverride -or
                            [string]$structure.data.motionPreferenceSource -ne $expectedMotionSource -or
                            [bool]$structure.data.animationsEnabled -ne $expectedAnimations -or
                            [string]::IsNullOrWhiteSpace([string]$structure.data.focusIdentity) -or
                            [string]$structure.data.focusIdentity -eq 'none' -or
                            [string]$structure.data.focusIdentity -ne [string]$captureFocus.data.afterIdentity) {
                            throw 'Control-plane automation-tree capture did not match its requested matrix cell.'
                        }
                        $structureSource = Resolve-IsolatedQaArtifact `
                            -DataRoot $isolatedData `
                            -RelativePath ([string]$structure.data.relativePath)
                        if (-not (Test-Path -LiteralPath $structureSource -PathType Leaf) -or
                            (Get-Sha256File $structureSource) -ne ([string]$structure.data.sha256).ToLowerInvariant()) {
                            throw 'Control-plane automation-tree evidence failed integrity validation.'
                        }
                        $automationPath = Join-Path $script:AutomationRoot ("$cellKey.automation-tree.json")
                        Copy-QaArtifactAtomic -Source $structureSource -Destination $automationPath
                        $automationArtifactId = "artifact.$cellKey.automation"
                        $automationProvenance = [ordered]@{
                            treeFingerprint = $sourceFingerprintStart
                            capturedAtUtc = ([DateTimeOffset]$structure.data.capturedAtUtc).ToUniversalTime().ToString('o')
                            theme = [string]$structure.data.theme
                            viewportWidthDip = [int]$structure.data.viewportWidthDip
                            viewportHeightDip = [int]$structure.data.viewportHeightDip
                            dpiScale = [decimal]$structure.data.renderDpiScale
                            expectedState = $expectedState
                            linkedAutomationArtifactId = $null
                            baselineArtifactId = $null
                        }
                        Add-QaArtifact -Id $automationArtifactId -Kind 'automation-tree' -Path $automationPath -Provenance $automationProvenance

                        $screenshotPath = Join-Path $script:ScreenshotsRoot ("$cellKey.rendered-ui.png")
                        $capture = Save-AIArenaScreenshot $screenshotPath -RenderDpiScale $renderScale.Value -TimeoutMs 10000
                        if (-not $capture.ok -or
                            -not (Test-Path -LiteralPath $screenshotPath -PathType Leaf) -or
                            [math]::Abs([double]$capture.data.renderDpiScale - $renderScale.Value) -gt 0.001 -or
                            -not [bool]$capture.data.renderDpiOverride -or
                            [int]$capture.data.viewportWidthDip -ne $viewport.Width -or
                            [int]$capture.data.viewportHeightDip -ne $viewport.Height) {
                            throw 'Control-plane screenshot capture did not match its requested matrix cell.'
                        }

                        $renderEvidence = Get-PngRenderEvidence -Path $screenshotPath
                        $screenshotArtifactId = "artifact.$cellKey.screenshot"
                        $screenshotProvenance = [ordered]@{
                            treeFingerprint = $sourceFingerprintStart
                            capturedAtUtc = ([DateTimeOffset]$capture.data.capturedAt).ToUniversalTime().ToString('o')
                            theme = [string]$structure.data.theme
                            viewportWidthDip = [int]$structure.data.viewportWidthDip
                            viewportHeightDip = [int]$structure.data.viewportHeightDip
                            dpiScale = [decimal]$structure.data.renderDpiScale
                            expectedState = $expectedState
                            linkedAutomationArtifactId = $automationArtifactId
                            baselineArtifactId = $null
                        }
                        Add-QaArtifact -Id $screenshotArtifactId -Kind 'rendered-ui-screenshot' -Path $screenshotPath -Provenance $screenshotProvenance

                        $script:UiMatrixCells.Add([pscustomobject][ordered]@{
                            key = $cellKey
                            theme = $theme
                            viewportWidthDip = $viewport.Width
                            viewportHeightDip = $viewport.Height
                            renderDpiScale = $renderScale.Value
                            motionMode = $motionMode
                            motionPreferenceSource = [string]$structure.data.motionPreferenceSource
                            animationsEnabled = [bool]$structure.data.animationsEnabled
                            focusNext = [ordered]@{
                                direction = [string]$nextFocus.data.direction
                                beforeIdentity = [string]$nextFocus.data.beforeIdentity
                                beforeControlType = [string]$nextFocus.data.beforeControlType
                                afterIdentity = [string]$nextFocus.data.afterIdentity
                                afterControlType = [string]$nextFocus.data.afterControlType
                                moved = [bool]$nextFocus.data.moved
                                focusChanged = [bool]$nextFocus.data.focusChanged
                            }
                            focusPrevious = [ordered]@{
                                direction = [string]$previousFocus.data.direction
                                beforeIdentity = [string]$previousFocus.data.beforeIdentity
                                beforeControlType = [string]$previousFocus.data.beforeControlType
                                afterIdentity = [string]$previousFocus.data.afterIdentity
                                afterControlType = [string]$previousFocus.data.afterControlType
                                moved = [bool]$previousFocus.data.moved
                                focusChanged = [bool]$previousFocus.data.focusChanged
                            }
                            focusCapture = [ordered]@{
                                direction = [string]$captureFocus.data.direction
                                beforeIdentity = [string]$captureFocus.data.beforeIdentity
                                beforeControlType = [string]$captureFocus.data.beforeControlType
                                afterIdentity = [string]$captureFocus.data.afterIdentity
                                afterControlType = [string]$captureFocus.data.afterControlType
                                moved = [bool]$captureFocus.data.moved
                                focusChanged = [bool]$captureFocus.data.focusChanged
                            }
                            automationArtifactId = $automationArtifactId
                            screenshotArtifactId = $screenshotArtifactId
                        })
                        Add-GateTrace ("cell={0}; pixels={1}x{2}; nodes={3}; automation={4}; screenshot={5}" -f `
                            $cellKey, $renderEvidence.widthPixels, $renderEvidence.heightPixels, [int]$structure.data.nodeCount, $automationArtifactId, $screenshotArtifactId)
                    }
                }
            }
        }

        $passCells = @(Get-AIArenaQaUiMatrixPassCells -Cells @($script:UiMatrixCells) -PassNumber $PassNumber)
        if ($passCells.Count -ne 36) {
            throw 'Rendered QA matrix did not produce every required cell.'
        }
        $matrixPath = Join-Path $script:MetadataRoot ("pass-{0:D2}.ui-matrix.json" -f $PassNumber)
        $matrixDocument = [ordered]@{
            schema = 'ai_arena.qa_ui_matrix.v1'
            passNumber = $PassNumber
            treeFingerprint = $sourceFingerprintStart
            cellCount = $passCells.Count
            cells = @($passCells | Sort-Object key)
        }
        Write-Utf8NoBom -Path $matrixPath -Text (($matrixDocument | ConvertTo-Json -Depth 8) + "`n")
        Add-QaArtifact -Id "artifact.pass-$($PassNumber.ToString('D2')).ui-matrix" -Kind 'qa-ui-matrix' -Path $matrixPath
        Add-GateTrace 'dataIsolation=temporary AI_ARENA_DATA_DIR; screenshot contains only the empty QA session'
        Add-GateTrace 'interactionBoundary=programmatic in-process WPF focus traversal and visual-tree snapshot; no OS SendInput or external UI Automation'
        Add-GateTrace 'densityBoundary=render scale is off-screen raster density, not physical or per-monitor display DPI'
        Add-GateTrace 'motionBoundary=preference plumbing/state only; animation playback frames are not proven'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            try {
                $null = $process.CloseMainWindow()
                if (-not $process.WaitForExit(5000)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $process.WaitForExit(5000) | Out-Null
                }
            }
            catch {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                try { $process.WaitForExit(5000) | Out-Null } catch { }
            }
        }
        if ($null -eq $previousDataRoot) {
            Remove-Item Env:AI_ARENA_DATA_DIR -ErrorAction SilentlyContinue
        }
        else {
            $env:AI_ARENA_DATA_DIR = $previousDataRoot
        }
        Remove-IsolatedQaData -Path $isolatedData -OwnerToken $ownerToken
    }
}

function Invoke-VerificationMeasurementRun {
    param([Parameter(Mandatory)] [string]$ProjectPath)

    $measurementPath = Join-Path $script:MetadataRoot 'verification-measurements.json'
    Invoke-CapturedCommand `
        -FilePath 'dotnet' `
        -Arguments @('run', '--project', $ProjectPath, '--no-build', '--no-restore', '-c', 'Release', '--', '--measure', $measurementPath) `
        -DisplayCommand 'dotnet run --project tests/AIArena.VerificationLab/AIArena.VerificationLab.csproj --no-build --no-restore -c Release -- --measure <verification-measurements.json>' `
        -TimeoutSeconds 120

    if (-not (Test-Path -LiteralPath $measurementPath -PathType Leaf)) {
        throw 'Verification measurement artifact is unavailable.'
    }
    $info = Get-Item -LiteralPath $measurementPath
    if ($info.Length -lt 2 -or $info.Length -gt 65536) {
        throw 'Verification measurement artifact exceeded its bounded size.'
    }
    $text = Get-Content -LiteralPath $measurementPath -Raw
    if (-not (Test-QaTextPrivacy $text)) {
        throw 'Verification measurement artifact failed privacy validation.'
    }
    $value = $text | ConvertFrom-Json
    if ([string]$value.schema -ne 'ai_arena.verification_measurements.v1') {
        throw 'Verification measurement artifact has an unsupported schema.'
    }
    $measurements = @($value.measurements)
    $expected = @('cancellation-latency', 'fault-recovery-latency', 'handle-growth', 'peak-working-set', 'soak-duration')
    $names = @($measurements | ForEach-Object { [string]$_.metric })
    if ($measurements.Count -ne $expected.Count -or
        @($names | Sort-Object -Unique).Count -ne $expected.Count -or
        @($expected | Where-Object { $_ -notin $names }).Count -ne 0 -or
        @($measurements | Where-Object { -not [bool]$_.passed }).Count -ne 0) {
        throw 'Verification measurements were incomplete or exceeded a threshold.'
    }

    Add-QaArtifact -Id 'artifact.verification.measurements' -Kind 'verification-measurements' -Path $measurementPath
    $script:VerificationMeasurements = $measurements
    Add-GateTrace ("measurementArtifactId=artifact.verification.measurements; soakRequests={0}; providerRestarts={1}; metrics={2}" -f `
        [int]$value.healthySoakRequests, [int]$value.providerRestarts, $measurements.Count)
}

function Write-MetadataArtifact {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [object]$Value
    )

    $path = Join-Path $script:MetadataRoot ($Name + '.json')
    $json = $Value | ConvertTo-Json -Depth 12
    $json = Protect-QaText $json
    Write-Utf8NoBom -Path $path -Text ($json + "`n")
    Add-QaArtifact -Id "artifact.metadata.$Name" -Kind 'qa-metadata' -Path $path
}

function Assert-EvidenceBundlePrivacy {
    foreach ($artifact in @($script:Artifacts)) {
        if ($artifact.kind -eq 'rendered-ui-screenshot') {
            continue
        }

        $path = Join-Path $script:RunRoot $artifact.relativePath.Replace('/', '\')
        $text = Get-Content -LiteralPath $path -Raw
        if (-not (Test-QaTextPrivacy $text)) {
            throw 'A text evidence artifact failed privacy validation.'
        }
        if ((Get-Sha256File $path) -ne $artifact.sha256) {
            throw 'An evidence artifact changed after it was recorded.'
        }
    }
}

function Invoke-AuthoritativeEvidenceValidation {
    param(
        [Parameter(Mandatory)] [string]$ProjectPath,
        [Parameter(Mandatory)] [string]$EvidencePath,
        [switch]$BundleOnly
    )

    $script:CurrentGateTrace = [System.Collections.Generic.List[string]]::new()
    $script:CurrentGatePassed = 0
    $script:CurrentGateFailed = 0
    $validatorSourceFingerprintBefore = ''
    try {
        $validatorSourceFingerprintBefore = Get-CompositeSourceFingerprint
        if ($validatorSourceFingerprintBefore -ne $sourceFingerprintStart) {
            throw 'Source fingerprint drifted immediately before authoritative validation.'
        }
        $validationArguments = if ($BundleOnly) {
            @('run', '--project', $ProjectPath, '--no-build', '--no-restore', '-c', 'Release', '--', '--validate-evidence', $EvidencePath)
        }
        else {
            @('run', '--project', $ProjectPath, '--no-build', '--no-restore', '-c', 'Release', '--', '--validate-evidence-current', $EvidencePath, $script:RepositoryRoot)
        }
        $displayCommand = if ($BundleOnly) {
            'dotnet run --project tests/AIArena.VerificationLab/AIArena.VerificationLab.csproj --no-build --no-restore -c Release -- --validate-evidence <qa-evidence.json>'
        }
        else {
            'dotnet run --project tests/AIArena.VerificationLab/AIArena.VerificationLab.csproj --no-build --no-restore -c Release -- --validate-evidence-current <qa-evidence.json> <repository-root>'
        }
        $validationResult = Invoke-CapturedCommand `
            -FilePath 'dotnet' `
            -Arguments $validationArguments `
            -DisplayCommand $displayCommand `
            -CaptureResult
        if ($validationResult.ExitCode -ne 0) {
            $script:LastAuthoritativeValidationIssues = @(Get-SafeValidatorIssueCodes `
                -Text ($validationResult.Stdout + "`n" + $validationResult.Stderr))
            Write-Warning ("Authoritative evidence validation failed: {0}" -f `
                ($script:LastAuthoritativeValidationIssues -join ', '))
            return $false
        }
        $script:LastAuthoritativeValidationIssues = @()
        $validatorSourceFingerprintAfter = Get-CompositeSourceFingerprint
        if ($validatorSourceFingerprintAfter -ne $validatorSourceFingerprintBefore) {
            throw 'Source fingerprint drifted during authoritative validation.'
        }
        return $true
    }
    catch {
        return $false
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($validatorSourceFingerprintBefore)) {
            $validatorBoundaryFingerprint = Get-CompositeSourceFingerprint
            if ($validatorBoundaryFingerprint -ne $validatorSourceFingerprintBefore) {
                $script:ExecutionFailure = $true
                throw 'Source fingerprint drifted across the authoritative validator boundary.'
            }
        }
        $script:CurrentGateTrace = $null
        $script:CurrentGatePassed = 0
        $script:CurrentGateFailed = 0
    }
}

if (-not (Test-Path -LiteralPath $script:SolutionPath -PathType Leaf)) {
    throw 'AI Arena solution file is unavailable.'
}
if (-not (Test-Path -LiteralPath (Join-Path $script:MapRoot '.git') -PathType Container)) {
    throw 'The nested Map repository is unavailable.'
}
if (Test-Path -LiteralPath $script:RunRoot) {
    throw 'QA run ID already exists; choose a new run ID.'
}

New-Item -ItemType Directory -Path $script:LogsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $script:ScreenshotsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $script:AutomationRoot -Force | Out-Null
New-Item -ItemType Directory -Path $script:MetadataRoot -Force | Out-Null

$sourceRevision = (@(Get-GitOutput @('rev-parse', 'HEAD'))[0]).Trim().ToLowerInvariant()
$headTreeRevision = (@(Get-GitOutput @('rev-parse', 'HEAD^{tree}'))[0]).Trim().ToLowerInvariant()
$mapSourceRevision = (@(Get-GitOutput -Arguments @('rev-parse', 'HEAD') -RepositoryRoot $script:MapRoot)[0]).Trim().ToLowerInvariant()
$mapHeadTreeRevision = (@(Get-GitOutput -Arguments @('rev-parse', 'HEAD^{tree}') -RepositoryRoot $script:MapRoot)[0]).Trim().ToLowerInvariant()
$outerSourceFingerprintStart = Get-SourceFingerprint
$mapSourceFingerprintStart = Get-SourceFingerprint -RepositoryRoot $script:MapRoot -ExcludedPrefixes @()
$sourceFingerprintStart = Get-Sha256Text ("outer={0}`nmap={1}`n" -f $outerSourceFingerprintStart, $mapSourceFingerprintStart)
$workingTreeStart = Get-WorkingTreeState
$mapWorkingTreeStart = Get-WorkingTreeState -RepositoryRoot $script:MapRoot
$dotnetSdkVersion = (& dotnet --version 2>$null | Select-Object -First 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdkVersion)) {
    $dotnetSdkVersion = 'unavailable'
}
$runtimeVersion = [Environment]::Version.ToString()
$powerShellVersion = $PSVersionTable.PSVersion.ToString()
$osVersion = [Environment]::OSVersion.VersionString
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

Add-QaGate -Id 'preflight.artifact-root-ignored' -DisplayName 'Artifact root is ignored by Git' -Required $true -Mode 'run' -Action {
    Invoke-CapturedCommand -FilePath 'git' -Arguments @('-C', $script:RepositoryRoot, 'check-ignore', '-q', '--', 'artifacts/qa/qa-seal-probe.txt') -DisplayCommand 'git check-ignore artifacts/qa/qa-seal-probe.txt'
}

$cleanMode = if ($workingTreeStart.clean) { 'pass' } else { 'blocked' }
$cleanReason = if ($workingTreeStart.clean) {
    'The working tree was clean before verification began.'
}
else {
    "The working tree contained $($workingTreeStart.changedPathCount) changed path record(s); a dirty tree cannot be sealed."
}
Add-QaGate -Id 'preflight.source-clean' -DisplayName 'Source working tree cleanliness' -Required $true -Mode $cleanMode -Reason $cleanReason

$mapCleanMode = if ($mapWorkingTreeStart.clean) { 'pass' } else { 'blocked' }
$mapCleanReason = if ($mapWorkingTreeStart.clean) {
    'The nested Map working tree was clean before verification began.'
}
else {
    "The nested Map tree contained $($mapWorkingTreeStart.changedPathCount) changed path record(s); a dirty nested repository cannot be sealed."
}
Add-QaGate -Id 'preflight.map-source-clean' -DisplayName 'Nested Map source cleanliness' -Required $true -Mode $mapCleanMode -Reason $mapCleanReason

$toolchainMode = if ($dotnetSdkVersion -eq 'unavailable') { 'unavailable' } else { 'pass' }
$toolchainReason = if ($toolchainMode -eq 'pass') {
    'Required operating-system, .NET SDK/runtime, and PowerShell versions were recorded.'
}
else {
    'The .NET SDK version was unavailable.'
}
Add-QaGate -Id 'preflight.toolchain' -DisplayName 'Toolchain inventory' -Required $true -Mode $toolchainMode -Reason $toolchainReason

$fullConfiguration = $Passes -ge 2 -and -not $SkipRenderedUi -and -not $PlanOnly
$configurationMode = if ($fullConfiguration) { 'pass' } else { 'partial' }
$configurationReason = if ($fullConfiguration) {
    'The run requested at least two complete passes and rendered-UI evidence.'
}
else {
    'Development switches make this an intentionally partial run; it cannot produce a seal.'
}
Add-QaGate -Id 'preflight.seal-configuration' -DisplayName 'Full-seal configuration' -Required $true -Mode $configurationMode -Reason $configurationReason

$solution = $script:SolutionPath
$projects = [ordered]@{
    core = Join-Path $script:RepositoryRoot 'tests\AIArena.Tests\AIArena.Tests.csproj'
    wpf = Join-Path $script:RepositoryRoot 'tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj'
    codeIntelligence = Join-Path $script:RepositoryRoot 'tests\AIArena.CodeIntelligence.Tests\AIArena.CodeIntelligence.Tests.csproj'
    verificationLab = Join-Path $script:RepositoryRoot 'tests\AIArena.VerificationLab\AIArena.VerificationLab.csproj'
}

for ($pass = 1; $pass -le $Passes; $pass++) {
    $prefix = 'pass-{0:D2}' -f $pass
    Assert-SourceFingerprintUnchanged -Expected $sourceFingerprintStart -Boundary "$prefix.before-pass"
    $passSourceFingerprintBefore = Get-CompositeSourceFingerprint
    if ($PlanOnly) {
        foreach ($plannedGate in @(
            'clean-build', 'tests-core', 'tests-wpf', 'tests-code-intelligence', 'tests-verification-lab',
            'local-runtime-qa', 'dependency-index', 'xaml-inventory-tests', 'xaml-inventory-check',
            'release-security-tests', 'map-full-suite', 'rendered-ui'
        )) {
            Add-QaGate -Id "$prefix.$plannedGate" -DisplayName $plannedGate -Required $true -Mode 'partial' -Reason 'Plan-only mode did not execute this required gate.'
        }
        continue
    }

    Add-QaGate -Id "$prefix.clean-build" -DisplayName "Clean Release build (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-CapturedCommand -FilePath 'dotnet' -Arguments @('build-server', 'shutdown') -DisplayCommand 'dotnet build-server shutdown'
        Invoke-CapturedCommand -FilePath 'dotnet' -Arguments @('clean', $solution, '-c', 'Release') -DisplayCommand 'dotnet clean <solution> -c Release'
        $buildSourceFingerprintBefore = Get-CompositeSourceFingerprint
        if ($buildSourceFingerprintBefore -ne $passSourceFingerprintBefore -or $buildSourceFingerprintBefore -ne $sourceFingerprintStart) {
            throw 'Source fingerprint drifted immediately before the Release build.'
        }
        Invoke-CapturedCommand -FilePath 'dotnet' -Arguments @('build', $solution, '--no-restore', '-c', 'Release') -DisplayCommand 'dotnet build <solution> --no-restore -c Release'
        $buildSourceFingerprintAfter = Get-CompositeSourceFingerprint
        if ($buildSourceFingerprintAfter -ne $buildSourceFingerprintBefore -or $buildSourceFingerprintAfter -ne $sourceFingerprintStart) {
            throw 'Source fingerprint drifted during the Release build.'
        }
        Add-GateTrace ("sourceFingerprintBefore={0}; sourceFingerprintAfter={1}" -f $buildSourceFingerprintBefore, $buildSourceFingerprintAfter)
    }

    Add-QaGate -Id "$prefix.tests-core" -DisplayName "Core harness (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-FullHarness -ProjectPath $projects.core -DisplayProject 'tests/AIArena.Tests/AIArena.Tests.csproj'
    }
    Add-QaGate -Id "$prefix.tests-wpf" -DisplayName "WPF harness (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-FullHarness -ProjectPath $projects.wpf -DisplayProject 'tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj'
    }
    Add-QaGate -Id "$prefix.tests-code-intelligence" -DisplayName "Code Intelligence harness (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-FullHarness -ProjectPath $projects.codeIntelligence -DisplayProject 'tests/AIArena.CodeIntelligence.Tests/AIArena.CodeIntelligence.Tests.csproj'
    }

    if (Test-Path -LiteralPath $projects.verificationLab -PathType Leaf) {
        Add-QaGate -Id "$prefix.tests-verification-lab" -DisplayName "Verification Lab harness (pass $pass)" -Required $true -Mode 'run' -Action {
            Invoke-FullHarness -ProjectPath $projects.verificationLab -DisplayProject 'tests/AIArena.VerificationLab/AIArena.VerificationLab.csproj'
        }
    }
    else {
        Add-QaGate -Id "$prefix.tests-verification-lab" -DisplayName "Verification Lab harness (pass $pass)" -Required $true -Mode 'unavailable' -Reason 'The Verification Lab project is not present in this source revision.'
    }

    Add-QaGate -Id "$prefix.local-runtime-qa" -DisplayName "Local runtime QA (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-PowerShellScriptGate -RelativePath 'scripts\local-runtime-qa.ps1' -Arguments @('-SkipBuild')
    }
    Add-QaGate -Id "$prefix.dependency-index" -DisplayName "Dependency index freshness (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-PowerShellScriptGate -RelativePath 'scripts\dependency-index.ps1' -Arguments @('-Check')
    }
    Add-QaGate -Id "$prefix.xaml-inventory-tests" -DisplayName "XAML inventory fixture tests (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-PowerShellScriptGate -RelativePath 'scripts\tests\xaml-hardcoded-values.tests.ps1'
    }
    Add-QaGate -Id "$prefix.xaml-inventory-check" -DisplayName "XAML inventory freshness (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-PowerShellScriptGate -RelativePath 'scripts\xaml-hardcoded-values.ps1' -Arguments @('-Check')
    }
    Add-QaGate -Id "$prefix.release-security-tests" -DisplayName "Release security fixture tests (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-PowerShellScriptGate -RelativePath 'scripts\tests\release-security.tests.ps1'
        Invoke-PowerShellScriptGate -RelativePath 'scripts\tests\qa-seal.tests.ps1'
    }
    Add-QaGate -Id "$prefix.map-full-suite" -DisplayName "Map index, type, test, render, and production-build suite (pass $pass)" -Required $true -Mode 'run' -Action {
        Invoke-CapturedCommand `
            -FilePath 'npm.cmd' `
            -Arguments @('--prefix', $script:MapRoot, 'run', 'test') `
            -DisplayCommand 'npm.cmd --prefix map run test' `
            -TimeoutSeconds 1800
    }

    if ($SkipRenderedUi) {
        Add-QaGate -Id "$prefix.rendered-ui" -DisplayName "Rendered Release UI smoke (pass $pass)" -Required $true -Mode 'partial' -Reason 'Rendered UI capture was skipped by explicit development switch.'
    }
    elseif (Test-AIArenaProcessRunning) {
        Add-QaGate -Id "$prefix.rendered-ui" -DisplayName "Rendered Release UI smoke (pass $pass)" -Required $true -Mode 'blocked' -Reason 'Another AI Arena process is active, so the isolated control-plane smoke could not run safely.'
    }
    else {
        Add-QaGate -Id "$prefix.rendered-ui" -DisplayName "Rendered Release UI smoke (pass $pass)" -Required $true -Mode 'run' -Action {
            Invoke-RenderedUiSmoke -PassNumber $pass
        }
    }
    Assert-SourceFingerprintUnchanged -Expected $passSourceFingerprintBefore -Boundary "$prefix.after-pass"
}

if ($PlanOnly -or $SkipRenderedUi) {
    $matrixReason = 'Development switches prevented this required matrix from executing.'
    Add-QaGate -Id 'ui.theme-contrast-matrix' -DisplayName 'Dark Blue, Light, and High Contrast render matrix' -Required $true -Mode 'partial' -Reason $matrixReason
    Add-QaGate -Id 'ui.viewport-dpi-matrix' -DisplayName '960/1500 DIP and raster-density render matrix' -Required $true -Mode 'partial' -Reason $matrixReason
    Add-QaGate -Id 'ui.keyboard-automation-matrix' -DisplayName 'Programmatic WPF focus and visual-tree matrix' -Required $true -Mode 'partial' -Reason $matrixReason
    Add-QaGate -Id 'ui.reduced-motion-matrix' -DisplayName 'Motion-preference plumbing and render matrix' -Required $true -Mode 'partial' -Reason $matrixReason
}
else {
    Add-QaGate -Id 'ui.theme-contrast-matrix' -DisplayName 'Dark Blue, Light, and High Contrast render matrix' -Required $true -Mode 'run' -PassSummary 'The three configured theme palettes produced distinct decoded PNG artifacts at every matching matrix coordinate; this does not independently certify colour contrast.' -Action {
        foreach ($pass in 1..$Passes) {
            $passCells = @(Get-AIArenaQaUiMatrixPassCells -Cells @($script:UiMatrixCells) -PassNumber $pass)
            foreach ($theme in @('dark-blue', 'light', 'high-contrast')) {
                $count = @($passCells | Where-Object { $_.theme -eq $theme }).Count
                if ($count -ne 12) {
                    throw 'A required theme matrix cell is missing.'
                }
                Add-GateTrace ("pass={0}; theme={1}; cells={2}" -f $pass, $theme, $count)
            }
        }
    }
    Add-QaGate -Id 'ui.viewport-dpi-matrix' -DisplayName '960/1500 DIP and raster-density render matrix' -Required $true -Mode 'run' -PassSummary 'The WPF window rendered at 960/1500 DIP into 1.0/1.5/2.0 off-screen raster densities. Physical and per-monitor display DPI were not exercised.' -Action {
        foreach ($pass in 1..$Passes) {
            $passCells = @(Get-AIArenaQaUiMatrixPassCells -Cells @($script:UiMatrixCells) -PassNumber $pass)
            foreach ($width in @(960, 1500)) {
                foreach ($scale in @([double]1.0, [double]1.5, [double]2.0)) {
                    $count = @($passCells | Where-Object {
                        $_.viewportWidthDip -eq $width -and [math]::Abs([double]$_.renderDpiScale - $scale) -le 0.001
                    }).Count
                    if ($count -ne 6) {
                        throw 'A required viewport or render-DPI matrix cell is missing.'
                    }
                    Add-GateTrace ("pass={0}; widthDip={1}; renderDpiScale={2}; cells={3}" -f $pass, $width, $scale, $count)
                }
            }
        }
    }
    Add-QaGate -Id 'ui.keyboard-automation-matrix' -DisplayName 'Programmatic WPF focus and visual-tree matrix' -Required $true -Mode 'run' -PassSummary 'Programmatic in-process WPF focus traversal and privacy-safe visual-tree snapshots completed. No OS SendInput or external UI Automation interaction was exercised.' -Action {
        $expected = $Passes * 36
        $verified = @($script:UiMatrixCells | Where-Object {
            (Test-AIArenaQaFocusCycle `
                -Next $_.focusNext `
                -Previous $_.focusPrevious `
                -Capture $_.focusCapture) -and
            -not [string]::IsNullOrWhiteSpace([string]$_.automationArtifactId)
        }).Count
        if ($verified -ne $expected) {
            throw 'Keyboard traversal or linked automation evidence is incomplete.'
        }
        Add-GateTrace ("bidirectionalFocusCells={0}; expected={1}" -f $verified, $expected)
    }
    Add-QaGate -Id 'ui.reduced-motion-matrix' -DisplayName 'Motion-preference plumbing and render matrix' -Required $true -Mode 'run' -PassSummary 'Normal and reduced process-only motion-preference states reached the WPF capture path. Captured evidence does not prove animation playback over time.' -Action {
        foreach ($pass in 1..$Passes) {
            $passCells = @(Get-AIArenaQaUiMatrixPassCells -Cells @($script:UiMatrixCells) -PassNumber $pass)
            $normal = @($passCells | Where-Object {
                $_.motionMode -eq 'normal' -and $_.motionPreferenceSource -eq 'qa-normal' -and [bool]$_.animationsEnabled
            }).Count
            $reduced = @($passCells | Where-Object {
                $_.motionMode -eq 'reduced' -and $_.motionPreferenceSource -eq 'qa-reduced' -and -not [bool]$_.animationsEnabled
            }).Count
            if ($normal -ne 18 -or $reduced -ne 18) {
                throw 'Normal or reduced-motion evidence is incomplete.'
            }
            Add-GateTrace ("pass={0}; normalCells={1}; reducedCells={2}" -f $pass, $normal, $reduced)
        }
    }
}
Add-QaGate `
    -Id 'verification.restart-soak-resource' `
    -DisplayName 'Restart, soak, cancellation, and bounded-resource verification' `
    -Required $true `
    -Mode $(if ($PlanOnly) { 'partial' } else { 'run' }) `
    -Reason $(if ($PlanOnly) { 'Plan-only mode did not execute restart, sustained-run, cancellation, or bounded-resource measurements.' } else { '' }) `
    -Action { Invoke-VerificationMeasurementRun -ProjectPath $projects.verificationLab }

$outerSourceFingerprintEnd = Get-SourceFingerprint
$mapSourceFingerprintEnd = Get-SourceFingerprint -RepositoryRoot $script:MapRoot -ExcludedPrefixes @()
$sourceFingerprintEnd = Get-Sha256Text ("outer={0}`nmap={1}`n" -f $outerSourceFingerprintEnd, $mapSourceFingerprintEnd)
$workingTreeEnd = Get-WorkingTreeState
$mapWorkingTreeEnd = Get-WorkingTreeState -RepositoryRoot $script:MapRoot
$stable = $sourceFingerprintEnd -eq $sourceFingerprintStart
$stableMode = if ($stable) { 'pass' } else { 'fail' }
$stableReason = if ($stable) {
    'The source-tree fingerprint did not change during verification.'
}
else {
    'The source-tree fingerprint changed during verification; all results are stale.'
}
Add-QaGate -Id 'postflight.source-stability' -DisplayName 'Source fingerprint stability' -Required $true -Mode $stableMode -Reason $stableReason

$mapStable = $mapSourceFingerprintEnd -eq $mapSourceFingerprintStart
$mapStableMode = if ($mapStable) { 'pass' } else { 'fail' }
$mapStableReason = if ($mapStable) {
    'The nested Map source fingerprint did not change during verification.'
}
else {
    'The nested Map source fingerprint changed during verification; all Map results are stale.'
}
Add-QaGate -Id 'postflight.map-source-stability' -DisplayName 'Nested Map source stability' -Required $true -Mode $mapStableMode -Reason $mapStableReason

$inspectionReason = 'Post-render user inspection has not been explicitly accepted for this exact evidence bundle.'
Add-QaGate -Id 'inspection.user-acceptance' -DisplayName 'Post-render user inspection acceptance' -Required $true -Mode 'partial' -Reason $inspectionReason

Write-MetadataArtifact -Name 'source' -Value ([ordered]@{
    sourceRevision = $sourceRevision
    headTreeRevision = $headTreeRevision
    sourceFingerprintStart = $sourceFingerprintStart
    sourceFingerprintEnd = $sourceFingerprintEnd
    outerSourceFingerprintStart = $outerSourceFingerprintStart
    outerSourceFingerprintEnd = $outerSourceFingerprintEnd
    stable = $stable
    cleanAtStart = [bool]$workingTreeStart.clean
    cleanAtEnd = [bool]$workingTreeEnd.clean
    changedPathCountAtStart = [int]$workingTreeStart.changedPathCount
    changedPathCountAtEnd = [int]$workingTreeEnd.changedPathCount
    nestedRepositories = @(
        [ordered]@{
            id = 'map'
            sourceRevision = $mapSourceRevision
            headTreeRevision = $mapHeadTreeRevision
            sourceFingerprintStart = $mapSourceFingerprintStart
            sourceFingerprintEnd = $mapSourceFingerprintEnd
            stable = $mapStable
            cleanAtStart = [bool]$mapWorkingTreeStart.clean
            cleanAtEnd = [bool]$mapWorkingTreeEnd.clean
            changedPathCountAtStart = [int]$mapWorkingTreeStart.changedPathCount
            changedPathCountAtEnd = [int]$mapWorkingTreeEnd.changedPathCount
        }
    )
})
Write-MetadataArtifact -Name 'toolchain' -Value ([ordered]@{
    operatingSystem = $osVersion
    architecture = $architecture
    dotnetSdk = $dotnetSdkVersion
    dotnetRuntime = $runtimeVersion
    powershell = $powerShellVersion
    configuration = 'Release'
})
Write-MetadataArtifact -Name 'configuration' -Value ([ordered]@{
    requestedPasses = $Passes
    renderedUiRequired = -not [bool]$SkipRenderedUi
    postRenderInspectionRequired = $true
    planOnly = [bool]$PlanOnly
    rawCommandOutputPersisted = $false
})

$privacyMode = 'pass'
$privacyReason = 'All JSON/log paths are relative, hashes match, and persisted text passed private-path, secret, and raw-content scans.'
try {
    Assert-EvidenceBundlePrivacy
}
catch {
    $privacyMode = 'fail'
    $privacyReason = 'One or more evidence artifacts failed privacy or integrity validation; the run cannot be sealed.'
    $script:ExecutionFailure = $true
}
Add-QaGate -Id 'postflight.evidence-privacy' -DisplayName 'Evidence privacy and integrity' -Required $true -Mode $privacyMode -Reason $privacyReason
try {
    Assert-EvidenceBundlePrivacy
}
catch {
    $script:ExecutionFailure = $true
    throw 'Final gate artifacts failed privacy validation; QA evidence was not written.'
}

$perPassRequired = @(
    'clean-build', 'tests-core', 'tests-wpf', 'tests-code-intelligence', 'tests-verification-lab',
    'local-runtime-qa', 'dependency-index', 'xaml-inventory-tests', 'xaml-inventory-check',
    'release-security-tests', 'map-full-suite', 'rendered-ui'
)
$cleanFullPasses = 0
for ($pass = 1; $pass -le $Passes; $pass++) {
    $prefix = 'pass-{0:D2}.' -f $pass
    $passGates = @($script:GateResults | Where-Object { $_.id.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($passGates.Count -eq $perPassRequired.Count -and @($passGates | Where-Object { $_.outcome -ne 'pass' }).Count -eq 0) {
        $cleanFullPasses++
    }
}

$requiredNonPass = @($script:GateResults | Where-Object { $_.required -and $_.outcome -ne 'pass' })
$isWorkingTreeClean = [bool]($workingTreeStart.clean -and $workingTreeEnd.clean -and $mapWorkingTreeStart.clean -and $mapWorkingTreeEnd.clean)
$canSeal = $requiredNonPass.Count -eq 0 -and
    $isWorkingTreeClean -and
    $stable -and
    $mapStable -and
    $cleanFullPasses -ge 2 -and
    $fullConfiguration

$hasBlockingOutcome = @($requiredNonPass | Where-Object { $_.outcome -in @('fail', 'blocked', 'unavailable') }).Count -gt 0
$verdict = if ($canSeal) {
    'sealed'
}
elseif ($hasBlockingOutcome) {
    'blocked'
}
else {
    'partial'
}

$performance = @(
    $passedBuildGates = @($script:GateResults | Where-Object { $_.id -match '^pass-[0-9]{2}\.clean-build$' -and $_.outcome -eq 'pass' })
    if ($passedBuildGates.Count -gt 0) {
        $buildGate = @($passedBuildGates | Sort-Object durationMilliseconds -Descending)[0]
        $artifactId = "artifact.$($buildGate.id).log"
        [pscustomobject][ordered]@{
            id = 'performance.clean-release-build-duration'
            metric = 'clean-release-build-duration'
            value = [decimal]$buildGate.durationMilliseconds
            unit = 'milliseconds'
            thresholdKind = 'maximum'
            threshold = [decimal]600000
            evidence = New-EvidenceAssertion `
                -Id 'evidence.performance.clean-release-build-duration' `
                -State 'observed' `
                -Summary 'The slowest clean Release build duration across the requested passes was measured by the QA orchestrator.' `
                -ReferenceId $artifactId
        }
    }

    if ($script:UiStartupMeasurements.Count -gt 0) {
        $startup = @($script:UiStartupMeasurements | Sort-Object durationMilliseconds -Descending)[0]
        $startupGateId = 'pass-{0:D2}.rendered-ui' -f [int]$startup.passNumber
        [pscustomobject][ordered]@{
            id = 'performance.release-app-startup-duration'
            metric = 'release-app-startup-duration'
            value = [decimal]$startup.durationMilliseconds
            unit = 'milliseconds'
            thresholdKind = 'maximum'
            threshold = [decimal]$UiStartupTimeoutSeconds * 1000
            evidence = New-EvidenceAssertion `
                -Id 'evidence.performance.release-app-startup-duration' `
                -State 'observed' `
                -Summary 'The slowest Release application startup-to-responsive-control-plane duration was measured across the rendered passes.' `
                -ReferenceId "artifact.$startupGateId.log"
        }
    }

    foreach ($measurement in @($script:VerificationMeasurements)) {
        $metric = [string]$measurement.metric
        [pscustomobject][ordered]@{
            id = "performance.$metric"
            metric = $metric
            value = [decimal]$measurement.value
            unit = [string]$measurement.unit
            thresholdKind = [string]$measurement.thresholdKind
            threshold = [decimal]$measurement.threshold
            evidence = New-EvidenceAssertion `
                -Id "evidence.performance.$metric" `
                -State 'observed' `
                -Summary 'The bounded local Verification Lab measured this resource or recovery threshold.' `
                -ReferenceId 'artifact.verification.measurements'
        }
    }
)

$verificationLabPresent = Test-Path -LiteralPath $projects.verificationLab -PathType Leaf
$schemaOutcome = if ($privacyMode -eq 'fail') {
    'fail'
}
elseif (-not $verificationLabPresent) {
    'unavailable'
}
elseif ($PlanOnly) {
    'partial'
}
else {
    'pass'
}
$schemaSummary = switch ($schemaOutcome) {
    'pass' { 'The v1 QA evidence structure and privacy invariants are ready for authoritative Verification Lab validation.' }
    'fail' { 'The v1 QA evidence structure failed its local privacy precondition.' }
    'partial' { 'Plan-only mode did not run authoritative QA evidence validation.' }
    default { 'The Verification Lab evidence validator is unavailable in this source revision.' }
}
$schemaEvidence = if ($schemaOutcome -eq 'pass' -or $schemaOutcome -eq 'fail') {
    New-EvidenceAssertion -Id 'evidence.schema.qa-evidence.v1' -State 'observed' -Summary $schemaSummary -ReferenceId 'artifact.postflight.evidence-privacy.log'
}
else {
    New-EvidenceAssertion -Id 'evidence.schema.qa-evidence.v1' -State 'unavailable' -Summary $schemaSummary -ReferenceId 'artifact.postflight.evidence-privacy.log' -Limitation $schemaSummary
}

$requiredContractSchemas = @(
    'ai_arena.experiment.v1',
    'ai_arena.experiment_run.v1',
    'ai_arena.branch.v1',
    'ai_arena.rubric.v1',
    'ai_arena.claim_ledger.v1',
    'ai_arena.memory_trace.v1',
    'ai_arena.scenario_pack.v1',
    'ai_arena.benchmark_pack.v1',
    'ai_arena.route_proposal.v1',
    'ai_arena.route_application_receipt.v1',
    'ai_arena.fault_profile.v1'
)
$coreSchemaGates = @($script:GateResults | Where-Object { $_.id -match '^pass-[0-9]{2}\.tests-core$' })
$contractSchemaOutcome = if ($PlanOnly) {
    'partial'
}
elseif ($coreSchemaGates.Count -gt 0 -and @($coreSchemaGates | Where-Object { $_.outcome -ne 'pass' }).Count -eq 0) {
    'pass'
}
elseif (@($coreSchemaGates | Where-Object { $_.outcome -in @('fail', 'blocked') }).Count -gt 0) {
    'fail'
}
else {
    'partial'
}
$contractSchemaSummary = switch ($contractSchemaOutcome) {
    'pass' { 'The complete Core contract harness validated every registered v1 contract, canonical codec, privacy rule, and migration invariant.' }
    'fail' { 'The Core contract harness did not complete successfully, so registered schema evidence cannot be accepted.' }
    default { 'The Core contract harness was not executed in this development configuration.' }
}
$contractSchemaEvidence = if ($contractSchemaOutcome -in @('pass', 'fail')) {
    New-EvidenceAssertion -Id 'evidence.schema.contracts.v1' -State 'observed' -Summary $contractSchemaSummary -ReferenceId 'artifact.pass-01.tests-core.log'
}
else {
    New-EvidenceAssertion -Id 'evidence.schema.contracts.v1' -State 'unavailable' -Summary $contractSchemaSummary -ReferenceId 'artifact.pass-01.tests-core.log' -Limitation $contractSchemaSummary
}

$screenshotArtifactIds = @(Get-OrdinalStringArray -Values @(
    $script:Artifacts |
        Where-Object { $_.kind -eq 'rendered-ui-screenshot' } |
        ForEach-Object { [string]$_.id }
))
$automationArtifactIds = @(Get-OrdinalStringArray -Values @(
    $script:Artifacts |
        Where-Object { $_.kind -eq 'automation-tree' } |
        ForEach-Object { [string]$_.id }
))
$inspectionEvidence = New-EvidenceAssertion -Id 'evidence.inspection' -State 'unavailable' -Summary $inspectionReason -ReferenceId 'artifact.inspection.user-acceptance.log' -Limitation $inspectionReason

$completedAtUtc = (Get-Date).ToUniversalTime()
$contract = [ordered]@{
    schema = $script:Schema
    id = "qa.$RunId"
    createdAtUtc = $completedAtUtc.ToString('o')
    sourceRevision = $sourceRevision
    treeFingerprint = $sourceFingerprintStart
    sealManifestId = 'ai_arena.qa_seal_manifest.v1'
    isWorkingTreeClean = $isWorkingTreeClean
    nestedRepositories = @(
        [ordered]@{
            id = 'map'
            sourceRevision = $mapSourceRevision
            treeFingerprint = $mapSourceFingerprintStart
            isWorkingTreeClean = [bool]($mapWorkingTreeStart.clean -and $mapWorkingTreeEnd.clean)
        }
    )
    startedAtUtc = $startedAtUtc.ToString('o')
    completedAtUtc = $completedAtUtc.ToString('o')
    verdict = $verdict
    cleanFullPasses = $cleanFullPasses
    environment = [ordered]@{
        operatingSystem = $osVersion
        architecture = $architecture
        runtimeVersion = $runtimeVersion
        sdkVersion = $dotnetSdkVersion
        configuration = 'Release'
        isReleaseBuild = $true
    }
    toolchain = @(
        [ordered]@{ name = 'dotnet-runtime'; version = $runtimeVersion }
        [ordered]@{ name = 'dotnet-sdk'; version = $dotnetSdkVersion }
        [ordered]@{ name = 'git'; version = ((git --version) -replace '^git version\s+', '').Trim() }
        [ordered]@{ name = 'powershell'; version = $powerShellVersion }
    )
    gates = @($script:GateResults)
    artifacts = @($script:Artifacts)
    performance = $performance
    schemaChecks = @(
        foreach ($contractSchema in $requiredContractSchemas) {
            [ordered]@{
                id = ('schema.' + $contractSchema.Replace('_', '-').Replace('.', '-'))
                schema = $contractSchema
                migratedFromSchema = $null
                outcome = $contractSchemaOutcome
                evidence = $contractSchemaEvidence
            }
        }
        [ordered]@{
            id = 'schema.qa-evidence.v1'
            schema = $script:Schema
            migratedFromSchema = $null
            outcome = $schemaOutcome
            evidence = $schemaEvidence
        }
    )
    liveProviderCoverage = [ordered]@{
        required = $false
        state = 'unavailable'
        providerProfileIds = @()
        evidenceRunIds = @()
        limitation = 'No live provider dependency was required for this deterministic local seal run.'
    }
    acceptedLimitations = @(
        [ordered]@{
            id = 'limitation.source-boundary-sampling'
            summary = 'Source fingerprints were checked at bounded QA boundaries, but the run was not executed from an immutable worktree; a transient edit-and-restore between checks is not cryptographically excluded.'
            userAccepted = $false
            evidence = New-EvidenceAssertion `
                -Id 'evidence.limitation.source-boundary-sampling' `
                -State 'unavailable' `
                -Summary 'Immutable-worktree execution evidence is unavailable in this local seal.' `
                -ReferenceId 'artifact.postflight.source-stability.log' `
                -Limitation 'Fingerprints sample bounded QA boundaries; they cannot prove no transient edit-and-restore occurred between samples.'
        }
        [ordered]@{
            id = 'limitation.ui-os-interaction'
            summary = 'Interactive OS keyboard input and external UI Automation were not exercised; evidence is limited to programmatic in-process WPF focus traversal and a privacy-safe visual-tree snapshot.'
            userAccepted = $false
            evidence = New-EvidenceAssertion `
                -Id 'evidence.limitation.ui-os-interaction' `
                -State 'unavailable' `
                -Summary 'OS SendInput and external UI Automation evidence are unavailable in this local seal.' `
                -ReferenceId 'artifact.ui.keyboard-automation-matrix.log' `
                -Limitation 'Only programmatic in-process WPF focus traversal and privacy-safe visual-tree metadata were captured.'
        }
        [ordered]@{
            id = 'limitation.ui-physical-dpi'
            summary = 'Physical and per-monitor display DPI were not exercised; the 1.0, 1.5, and 2.0 values are off-screen screenshot raster-density scales at fixed DIP viewports.'
            userAccepted = $false
            evidence = New-EvidenceAssertion `
                -Id 'evidence.limitation.ui-physical-dpi' `
                -State 'unavailable' `
                -Summary 'Physical and per-monitor display DPI evidence is unavailable in this local seal.' `
                -ReferenceId 'artifact.ui.viewport-dpi-matrix.log' `
                -Limitation 'The matrix proves fixed-DIP rendering at three off-screen raster densities only.'
        }
        [ordered]@{
            id = 'limitation.ui-animation-playback'
            summary = 'Rendered animation playback over time was not observed; evidence is limited to process-only normal/reduced motion-preference plumbing and captured state.'
            userAccepted = $false
            evidence = New-EvidenceAssertion `
                -Id 'evidence.limitation.ui-animation-playback' `
                -State 'unavailable' `
                -Summary 'Animation playback evidence over time is unavailable in this local seal.' `
                -ReferenceId 'artifact.ui.reduced-motion-matrix.log' `
                -Limitation 'The matrix proves motion-preference plumbing and state, not temporal animation behaviour.'
        }
    )
    inspection = [ordered]@{
        userAccepted = $false
        acceptedAtUtc = $null
        treeFingerprint = $null
        screenshotArtifactIds = $screenshotArtifactIds
        automationArtifactIds = $automationArtifactIds
        evidence = $inspectionEvidence
    }
    evidence = @()
}

$json = $contract | ConvertTo-Json -Depth 30
$json = Protect-QaText $json
if (-not (Test-QaTextPrivacy $json)) {
    throw 'Final QA evidence failed privacy validation and was not written.'
}

$evidencePath = Join-Path $script:RunRoot 'qa-evidence.json'
$temporaryEvidencePath = Join-Path $script:RunRoot ('.qa-evidence.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
Write-Utf8NoBom -Path $temporaryEvidencePath -Text ($json + "`n")
Move-Item -LiteralPath $temporaryEvidencePath -Destination $evidencePath

$authoritativeValidationFailed = $false
if ($verificationLabPresent -and -not $PlanOnly) {
    if (-not (Invoke-AuthoritativeEvidenceValidation -ProjectPath $projects.verificationLab -EvidencePath $evidencePath)) {
        $authoritativeValidationFailed = $true
        $script:ExecutionFailure = $true
        $contract.verdict = 'blocked'
        $qaSchemaChecks = @($contract.schemaChecks | Where-Object { $_.schema -eq $script:Schema })
        if ($qaSchemaChecks.Count -ne 1) {
            throw 'QA evidence schema check is missing or ambiguous.'
        }
        $qaSchemaChecks[0].outcome = 'fail'
        $qaSchemaChecks[0].evidence = New-EvidenceAssertion `
            -Id 'evidence.schema.qa-evidence.v1' `
            -State 'observed' `
            -Summary 'The authoritative Verification Lab validator rejected the QA evidence bundle.' `
            -ReferenceId 'artifact.postflight.evidence-privacy.log'
        $json = Protect-QaText ($contract | ConvertTo-Json -Depth 30)
        if (-not (Test-QaTextPrivacy $json)) {
            throw 'Blocked QA evidence failed privacy validation and was not retained.'
        }
        $blockedTemporaryPath = Join-Path $script:RunRoot ('.qa-evidence.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
        Write-Utf8NoBom -Path $blockedTemporaryPath -Text ($json + "`n")
        Move-Item -LiteralPath $blockedTemporaryPath -Destination $evidencePath -Force
        if (-not (Invoke-AuthoritativeEvidenceValidation -ProjectPath $projects.verificationLab -EvidencePath $evidencePath -BundleOnly)) {
            Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue
            throw 'The blocked QA evidence form is not structurally valid and was not accepted.'
        }
        $verdict = 'blocked'
    }
}

$finalOuterFingerprint = Get-SourceFingerprint
$finalMapFingerprint = Get-SourceFingerprint -RepositoryRoot $script:MapRoot -ExcludedPrefixes @()
$finalFingerprint = Get-Sha256Text ("outer={0}`nmap={1}`n" -f $finalOuterFingerprint, $finalMapFingerprint)
if ($finalFingerprint -ne $sourceFingerprintStart) {
    throw 'Source fingerprint changed during final evidence write; the bundle is stale.'
}

Write-Host ''
$script:GateResults | Select-Object id, outcome, required, durationMilliseconds | Format-Table -AutoSize
Write-Host ("QA verdict: {0}" -f $verdict)
Write-Host ("Evidence: {0}/qa-evidence.json" -f $script:RunRelativePath)
Write-Host ("Clean full passes: {0}" -f $cleanFullPasses)
if ($verdict -eq 'partial' -and
    @($script:GateResults | Where-Object { $_.id -ne 'inspection.user-acceptance' -and $_.required -and $_.outcome -ne 'pass' }).Count -eq 0) {
    Write-Host 'Inspection pending. After reviewing every rendered screenshot, run:'
    Write-Host ('.\scripts\qa-accept-inspection.ps1 -EvidencePath {0}/qa-evidence.json -AttestReviewedVisuals' -f $script:RunRelativePath)
}

if ($verdict -eq 'sealed') {
    exit 0
}
if ($AllowPartial -and -not $script:ExecutionFailure -and -not $authoritativeValidationFailed) {
    Write-Host 'Partial/blocked evidence was allowed for development; no Quality Seal was issued.'
    exit 0
}

exit 1
