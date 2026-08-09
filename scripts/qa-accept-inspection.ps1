<#
.SYNOPSIS
    Explicitly accepts post-render inspection for one complete QA evidence bundle.

.DESCRIPTION
    This command is deliberately separate from qa-seal.ps1. Invoking it is the
    user action that attests to review of the rendered screenshots and their
    linked automation trees. Direct CLI use requires -AttestReviewedVisuals and
    records an external human attestation; the script verifies hashes and
    currentness but does not claim that it displayed or observed pixels. In-app
    use instead requires a hash-pinned manifest proving that every exact
    screenshot was deliberately previewed successfully.

    The command never accepts a pre-run boolean. It writes one privacy-safe,
    bundle-relative receipt, then atomically replaces qa-evidence.json. Candidate
    and final evidence are authoritatively revalidated. Any failure restores the
    original bytes and removes the receipt.

.EXAMPLE
    .\scripts\qa-accept-inspection.ps1 -EvidencePath .\artifacts\qa\20260809t120000z-abcd1234\qa-evidence.json -AttestReviewedVisuals
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidencePath,

    [string]$RepositoryRoot = '',

    [ValidateSet('DirectHumanAttestation', 'InAppReviewedManifest')]
    [string]$AcceptanceSource = 'DirectHumanAttestation',

    [switch]$AttestReviewedVisuals,

    [string]$ReviewedManifestPath = '',

    [string]$ReviewedManifestSha256 = '',

    # Synthetic fixture hook. It is rejected for the real repository and is not
    # a production validator bypass.
    [Parameter(DontShow = $true)]
    [string]$TestValidatorScript = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'
$env:NUGET_XMLDOC_MODE = 'skip'

$script:MaximumEvidenceBytes = 4MB
$script:MaximumArtifactBytes = 64MB
$script:QaSchema = 'ai_arena.qa_evidence.v1'
$script:SealManifest = 'ai_arena.qa_seal_manifest.v2'
$script:ReceiptArtifactId = 'artifact.inspection.user-acceptance.receipt'
$script:ReceiptRelativePath = 'metadata/inspection-receipt.json'
$script:ReviewManifestSchema = 'ai_arena.qa_inspection_review.v1'
$script:ReviewManifestRelativePath = 'metadata/in-app-inspection-review.json'
$script:RealRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:LockStream = $null
$script:LockPath = $null
$script:ValidatorOutputPath = $null
$script:ValidatorDllPath = $null
$script:ValidatorOutputManifest = $null
$script:ValidatorRestoreInputManifest = $null

$script:RequiredGlobalGateIds = @(
    'postflight.evidence-privacy',
    'postflight.map-source-stability',
    'postflight.source-stability',
    'preflight.artifact-root-ignored',
    'preflight.map-source-clean',
    'preflight.seal-configuration',
    'preflight.source-clean',
    'preflight.toolchain',
    'schema.explicit-v0-pack-migration',
    'ui.feature-surface-matrix',
    'ui.keyboard-automation-matrix',
    'ui.reduced-motion-matrix',
    'ui.theme-contrast-matrix',
    'ui.viewport-dpi-matrix',
    'verification.restart-soak-resource'
)
$script:RequiredPerPassGateSuffixes = @(
    'clean-build',
    'dependency-index',
    'local-runtime-qa',
    'map-full-suite',
    'release-security-tests',
    'rendered-ui',
    'tests-code-intelligence',
    'tests-core',
    'tests-verification-lab',
    'tests-wpf',
    'xaml-inventory-check',
    'xaml-inventory-tests'
)
$script:RequiredSchemas = @(
    'ai_arena.benchmark_pack.v1',
    'ai_arena.branch.v1',
    'ai_arena.claim_ledger.v1',
    'ai_arena.experiment.v1',
    'ai_arena.experiment_run.v1',
    'ai_arena.fault_profile.v1',
    'ai_arena.memory_trace.v1',
    'ai_arena.qa_evidence.v1',
    'ai_arena.route_application_receipt.v1',
    'ai_arena.route_proposal.v1',
    'ai_arena.rubric.v1',
    'ai_arena.scenario_pack.v1'
)
$script:RequiredPerformanceMetrics = @(
    'cancellation-latency',
    'clean-release-build-duration',
    'fault-recovery-latency',
    'handle-growth',
    'peak-working-set',
    'release-app-startup-duration',
    'soak-duration'
)
$script:RequiredLimitations = @(
    [pscustomobject][ordered]@{
        Id = 'limitation.source-boundary-sampling'
        Summary = 'Source fingerprints were checked at bounded QA boundaries, but the run was not executed from an immutable worktree; a transient edit-and-restore between checks is not cryptographically excluded.'
        EvidenceId = 'evidence.limitation.source-boundary-sampling'
        EvidenceSummary = 'Immutable-worktree execution evidence is unavailable in this local seal.'
        ReferenceId = 'artifact.postflight.source-stability.log'
        EvidenceLimitation = 'Fingerprints sample bounded QA boundaries; they cannot prove no transient edit-and-restore occurred between samples.'
    }
    [pscustomobject][ordered]@{
        Id = 'limitation.ui-animation-playback'
        Summary = 'Rendered animation playback over time was not observed; evidence is limited to process-only normal/reduced motion-preference plumbing and captured state.'
        EvidenceId = 'evidence.limitation.ui-animation-playback'
        EvidenceSummary = 'Animation playback evidence over time is unavailable in this local seal.'
        ReferenceId = 'artifact.ui.reduced-motion-matrix.log'
        EvidenceLimitation = 'The matrix proves motion-preference plumbing and state, not temporal animation behaviour.'
    }
    [pscustomobject][ordered]@{
        Id = 'limitation.ui-os-interaction'
        Summary = 'Interactive OS keyboard input and external UI Automation were not exercised; evidence is limited to programmatic in-process WPF focus traversal and a privacy-safe visual-tree snapshot.'
        EvidenceId = 'evidence.limitation.ui-os-interaction'
        EvidenceSummary = 'OS SendInput and external UI Automation evidence are unavailable in this local seal.'
        ReferenceId = 'artifact.ui.keyboard-automation-matrix.log'
        EvidenceLimitation = 'Only programmatic in-process WPF focus traversal and privacy-safe visual-tree metadata were captured.'
    }
    [pscustomobject][ordered]@{
        Id = 'limitation.ui-physical-dpi'
        Summary = 'Physical and per-monitor display DPI were not exercised; the 1.0, 1.5, and 2.0 values are off-screen screenshot raster-density scales at fixed DIP viewports.'
        EvidenceId = 'evidence.limitation.ui-physical-dpi'
        EvidenceSummary = 'Physical and per-monitor display DPI evidence is unavailable in this local seal.'
        ReferenceId = 'artifact.ui.viewport-dpi-matrix.log'
        EvidenceLimitation = 'The matrix proves fixed-DIP rendering at three off-screen raster densities only.'
    }
)
$script:RequiredLimitationIds = @($script:RequiredLimitations | ForEach-Object { $_.Id })

function Stop-QaAcceptance {
    param([Parameter(Mandatory)] [string]$Code)

    throw [InvalidOperationException]::new($Code)
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory)] [object]$InputObject,
        [Parameter(Mandatory)] [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        Stop-QaAcceptance 'qa_accept.contract_shape'
    }
    return $property.Value
}

function Get-OptionalNullProperty {
    param(
        [Parameter(Mandatory)] [object]$InputObject,
        [Parameter(Mandatory)] [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    return $(if ($null -eq $property) { $null } else { $property.Value })
}

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory)] [object]$InputObject,
        [Parameter(Mandatory)] [string[]]$Names,
        [Parameter(Mandatory)] [string]$FailureCode
    )

    $actual = @($InputObject.PSObject.Properties.Name | Sort-Object)
    $expected = @($Names | Sort-Object)
    if ($actual.Count -ne $expected.Count -or
        @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual).Count -ne 0) {
        Stop-QaAcceptance $FailureCode
    }
}

function Assert-SafeId {
    param([AllowNull()] [object]$Value)

    if ($Value -isnot [string] -or $Value -notmatch '^[a-z0-9][a-z0-9._-]{0,127}$') {
        Stop-QaAcceptance 'qa_accept.contract_shape'
    }
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

function Read-BoundedBytes {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [long]$MaximumBytes,
        [Parameter(Mandatory)] [string]$FailureCode
    )

    try {
        $stream = [IO.FileStream]::new(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            16384,
            [IO.FileOptions]::SequentialScan)
        try {
            if ($stream.Length -gt $MaximumBytes) {
                Stop-QaAcceptance $FailureCode
            }
            $buffer = [IO.MemoryStream]::new([int][Math]::Min($MaximumBytes, [Math]::Max(0, $stream.Length)))
            try {
                $chunk = [byte[]]::new(16384)
                while (($read = $stream.Read($chunk, 0, $chunk.Length)) -gt 0) {
                    if ($buffer.Length + $read -gt $MaximumBytes) {
                        Stop-QaAcceptance $FailureCode
                    }
                    $buffer.Write($chunk, 0, $read)
                }
                return $buffer.ToArray()
            }
            finally {
                $buffer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance $FailureCode
    }
}

function ConvertFrom-BoundedJson {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$FailureCode
    )

    $bytes = Read-BoundedBytes -Path $Path -MaximumBytes $script:MaximumEvidenceBytes -FailureCode $FailureCode
    try {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $text = $encoding.GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
            $text = $text.Substring(1)
        }
        if (-not (Test-QaTextPrivacy $text)) {
            Stop-QaAcceptance 'qa_accept.privacy'
        }
        $convertFromJson = Get-Command Microsoft.PowerShell.Utility\ConvertFrom-Json -CommandType Cmdlet -ErrorAction Stop
        $value = if ($convertFromJson.Parameters.ContainsKey('DateKind')) {
            # PowerShell 7.5+ otherwise parses ISO timestamps as DateTime and
            # ConvertTo-Json can rewrite UTC evidence using the local offset.
            # Preserve the exact contract lexemes; explicit validation casts
            # timestamps only where their values must be interpreted.
            $text | Microsoft.PowerShell.Utility\ConvertFrom-Json -DateKind String
        }
        else {
            # Windows PowerShell 5.1 and earlier pwsh releases retain JSON date
            # strings by default and do not expose the DateKind parameter.
            $text | Microsoft.PowerShell.Utility\ConvertFrom-Json
        }
        return [pscustomobject]@{
            Bytes = $bytes
            Text = $text
            Value = $value
        }
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance $FailureCode
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Text
    )

    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-Sha256Bytes {
    param([Parameter(Mandatory)] [byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-Sha256Text {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Text)

    return Get-Sha256Bytes ([Text.Encoding]::UTF8.GetBytes($Text))
}

function Get-LimitationSemanticSha256 {
    param([Parameter(Mandatory)] [object]$Limitation)

    $evidence = Get-RequiredProperty $Limitation 'evidence'
    $basis = Get-OptionalNullProperty $evidence 'basis'
    $fields = @(
        [string](Get-RequiredProperty $Limitation 'id')
        [string](Get-RequiredProperty $Limitation 'summary')
        [string](Get-RequiredProperty $evidence 'id')
        [string](Get-RequiredProperty $evidence 'state')
        [string](Get-RequiredProperty $evidence 'summary')
        [string](Get-RequiredProperty $evidence 'referenceId')
        $(if ($null -eq $basis) { '<null>' } else { [string]$basis })
        [string](Get-RequiredProperty $evidence 'limitation')
    )
    return Get-Sha256Text (($fields -join [char]0) + "`n")
}

function Get-Sha256File {
    param([Parameter(Mandatory)] [string]$Path)

    try {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    catch {
        Stop-QaAcceptance 'qa_accept.artifact_read'
    }
}

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory)] [string]$TrustedRoot,
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$TargetPath
    )

    $trusted = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\', '/')
    $base = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($TargetPath)
    $trustedPrefix = $trusted + [IO.Path]::DirectorySeparatorChar
    $prefix = $base + [IO.Path]::DirectorySeparatorChar
    if ((-not [string]::Equals($base, $trusted, [StringComparison]::OrdinalIgnoreCase) -and
         -not $base.StartsWith($trustedPrefix, [StringComparison]::OrdinalIgnoreCase)) -or
        -not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-QaAcceptance 'qa_accept.path_escape'
    }

    $cursor = $target
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-QaAcceptance 'qa_accept.path_escape'
            }
        }
        if ([string]::Equals($cursor, $trusted, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-QaAcceptance 'qa_accept.path_escape'
        }
        $cursor = $parent
    }
}

function Resolve-BundleArtifactPath {
    param(
        [Parameter(Mandatory)] [string]$TrustedRoot,
        [Parameter(Mandatory)] [string]$BundleRoot,
        [Parameter(Mandatory)] [object]$RelativePath
    )

    if ($RelativePath -isnot [string] -or
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath.Contains('\') -or
        $RelativePath.StartsWith('/', [StringComparison]::Ordinal) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        Stop-QaAcceptance 'qa_accept.path_escape'
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $RelativePath.Replace('/', '\')))
    Assert-NoReparsePoint -TrustedRoot $TrustedRoot -BasePath $BundleRoot -TargetPath $fullPath
    return $fullPath
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -c 'core.quotepath=false' -C $Root @Arguments 2>$null)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        Stop-QaAcceptance 'qa_accept.git'
    }
    return @($output)
}

function Get-SourceFingerprint {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [string[]]$ExcludedPrefixes = @()
    )

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $unique = [Collections.Generic.List[string]]::new()
    foreach ($value in @(
        @(Invoke-GitText -Root $Root -Arguments @('ls-files', '--cached', '--others', '--exclude-standard')) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )) {
        $path = [string]$value
        if ($seen.Add($path)) {
            $unique.Add($path)
        }
    }
    [string[]]$paths = @($unique)
    [Array]::Sort($paths, [StringComparer]::OrdinalIgnoreCase)
    $manifest = [Text.StringBuilder]::new()
    foreach ($path in $paths) {
        $normalized = ([string]$path).Replace('\', '/')
        if ($normalized -match '[\x00-\x1f]' -or [IO.Path]::IsPathRooted($normalized) -or $normalized.Split('/') -contains '..') {
            Stop-QaAcceptance 'qa_accept.git_path'
        }
        $excluded = @($ExcludedPrefixes | Where-Object {
            $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0
        if ($excluded) {
            continue
        }
        $fullPath = Join-Path $Root $path
        $hash = if (Test-Path -LiteralPath $fullPath -PathType Leaf) { Get-Sha256File $fullPath } else { 'deleted' }
        [void]$manifest.Append($normalized).Append([char]0).Append($hash).Append("`n")
    }
    return Get-Sha256Text $manifest.ToString()
}

function Get-CurrentRepositoryState {
    param([Parameter(Mandatory)] [string]$Root)

    $mapRoot = Join-Path $Root 'map'
    if (-not (Test-Path -LiteralPath (Join-Path $Root '.git')) -or
        -not (Test-Path -LiteralPath (Join-Path $mapRoot '.git'))) {
        Stop-QaAcceptance 'qa_accept.repository_layout'
    }
    if (@(Invoke-GitText -Root $Root -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Count -ne 0 -or
        @(Invoke-GitText -Root $mapRoot -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Count -ne 0) {
        Stop-QaAcceptance 'qa_accept.dirty_tree'
    }

    $outerRevision = ([string](@(Invoke-GitText -Root $Root -Arguments @('rev-parse', 'HEAD'))[0])).Trim().ToLowerInvariant()
    $mapRevision = ([string](@(Invoke-GitText -Root $mapRoot -Arguments @('rev-parse', 'HEAD'))[0])).Trim().ToLowerInvariant()
    $outerFingerprint = Get-SourceFingerprint -Root $Root -ExcludedPrefixes @('artifacts/')
    $mapFingerprint = Get-SourceFingerprint -Root $mapRoot
    $composite = Get-Sha256Text ("outer={0}`nmap={1}`n" -f $outerFingerprint, $mapFingerprint)
    return [pscustomobject]@{
        OuterRevision = $outerRevision
        MapRevision = $mapRevision
        MapFingerprint = $mapFingerprint
        CompositeFingerprint = $composite
    }
}

function Assert-ObservedEvidence {
    param([Parameter(Mandatory)] [object]$Evidence)

    if ([string](Get-RequiredProperty $Evidence 'state') -ne 'observed' -or
        [string]::IsNullOrWhiteSpace([string](Get-RequiredProperty $Evidence 'referenceId'))) {
        Stop-QaAcceptance 'qa_accept.incomplete_evidence'
    }
}

function Assert-ContractCompleteness {
    param(
        [Parameter(Mandatory)] [object]$Contract,
        [Parameter(Mandatory)] [object]$RepositoryState
    )

    if ([string](Get-RequiredProperty $Contract 'schema') -ne $script:QaSchema -or
        [string](Get-RequiredProperty $Contract 'sealManifestId') -ne $script:SealManifest -or
        [string](Get-RequiredProperty $Contract 'verdict') -ne 'partial') {
        Stop-QaAcceptance 'qa_accept.not_complete_partial'
    }
    if ((Get-RequiredProperty $Contract 'isWorkingTreeClean') -isnot [bool] -or
        -not [bool](Get-RequiredProperty $Contract 'isWorkingTreeClean')) {
        Stop-QaAcceptance 'qa_accept.stale_tree'
    }
    if ([string](Get-RequiredProperty $Contract 'sourceRevision') -ne $RepositoryState.OuterRevision -or
        [string](Get-RequiredProperty $Contract 'treeFingerprint') -ne $RepositoryState.CompositeFingerprint) {
        Stop-QaAcceptance 'qa_accept.stale_tree'
    }

    $nested = @(Get-RequiredProperty $Contract 'nestedRepositories')
    $mapEntries = @($nested | Where-Object { [string](Get-RequiredProperty $_ 'id') -eq 'map' })
    if ($mapEntries.Count -ne 1 -or
        (Get-RequiredProperty $mapEntries[0] 'isWorkingTreeClean') -isnot [bool] -or
        -not [bool](Get-RequiredProperty $mapEntries[0] 'isWorkingTreeClean') -or
        [string](Get-RequiredProperty $mapEntries[0] 'sourceRevision') -ne $RepositoryState.MapRevision -or
        [string](Get-RequiredProperty $mapEntries[0] 'treeFingerprint') -ne $RepositoryState.MapFingerprint) {
        Stop-QaAcceptance 'qa_accept.stale_tree'
    }

    $environment = Get-RequiredProperty $Contract 'environment'
    if ([string](Get-RequiredProperty $environment 'configuration') -ne 'Release' -or
        (Get-RequiredProperty $environment 'isReleaseBuild') -isnot [bool] -or
        -not [bool](Get-RequiredProperty $environment 'isReleaseBuild')) {
        Stop-QaAcceptance 'qa_accept.not_release'
    }

    $cleanPasses = Get-RequiredProperty $Contract 'cleanFullPasses'
    if ($cleanPasses -isnot [int] -and $cleanPasses -isnot [long]) {
        Stop-QaAcceptance 'qa_accept.clean_passes'
    }
    $cleanPasses = [int]$cleanPasses
    if ($cleanPasses -lt 2 -or $cleanPasses -gt 5) {
        Stop-QaAcceptance 'qa_accept.clean_passes'
    }

    $gates = @(Get-RequiredProperty $Contract 'gates')
    $gateById = @{}
    foreach ($gate in $gates) {
        $id = [string](Get-RequiredProperty $gate 'id')
        Assert-SafeId $id
        if ($gateById.ContainsKey($id)) {
            Stop-QaAcceptance 'qa_accept.duplicate_id'
        }
        $gateById[$id] = $gate
        $required = Get-RequiredProperty $gate 'required'
        if ($required -isnot [bool]) {
            Stop-QaAcceptance 'qa_accept.contract_shape'
        }
        if ($id -ne 'inspection.user-acceptance' -and [bool]$required -and
            [string](Get-RequiredProperty $gate 'outcome') -ne 'pass') {
            Stop-QaAcceptance 'qa_accept.incomplete_gates'
        }
    }
    foreach ($id in $script:RequiredGlobalGateIds) {
        if (-not $gateById.ContainsKey($id) -or
            -not [bool](Get-RequiredProperty $gateById[$id] 'required') -or
            [string](Get-RequiredProperty $gateById[$id] 'outcome') -ne 'pass') {
            Stop-QaAcceptance 'qa_accept.incomplete_gates'
        }
        Assert-ObservedEvidence (Get-RequiredProperty $gateById[$id] 'evidence')
    }
    $migrationGateEvidence = Get-RequiredProperty $gateById['schema.explicit-v0-pack-migration'] 'evidence'
    if ([string](Get-RequiredProperty $migrationGateEvidence 'referenceId') -cne 'artifact.schema.explicit-v0-pack-migration.log') {
        Stop-QaAcceptance 'qa_accept.migration_gate'
    }
    for ($pass = 1; $pass -le $cleanPasses; $pass++) {
        foreach ($suffix in $script:RequiredPerPassGateSuffixes) {
            $id = 'pass-{0:D2}.{1}' -f $pass, $suffix
            if (-not $gateById.ContainsKey($id) -or
                -not [bool](Get-RequiredProperty $gateById[$id] 'required') -or
                [string](Get-RequiredProperty $gateById[$id] 'outcome') -ne 'pass') {
                Stop-QaAcceptance 'qa_accept.incomplete_gates'
            }
            Assert-ObservedEvidence (Get-RequiredProperty $gateById[$id] 'evidence')
        }
    }
    if (-not $gateById.ContainsKey('inspection.user-acceptance')) {
        Stop-QaAcceptance 'qa_accept.inspection_gate'
    }
    $inspectionGate = $gateById['inspection.user-acceptance']
    if (-not [bool](Get-RequiredProperty $inspectionGate 'required') -or
        [string](Get-RequiredProperty $inspectionGate 'outcome') -ne 'partial' -or
        [string](Get-RequiredProperty (Get-RequiredProperty $inspectionGate 'evidence') 'state') -ne 'unavailable') {
        Stop-QaAcceptance 'qa_accept.inspection_gate'
    }
    $inspection = Get-RequiredProperty $Contract 'inspection'
    if ((Get-RequiredProperty $inspection 'userAccepted') -isnot [bool] -or
        [bool](Get-RequiredProperty $inspection 'userAccepted') -or
        $null -ne (Get-RequiredProperty $inspection 'acceptedAtUtc') -or
        $null -ne (Get-RequiredProperty $inspection 'treeFingerprint')) {
        Stop-QaAcceptance 'qa_accept.preaccepted'
    }

    $limitations = @(Get-RequiredProperty $Contract 'acceptedLimitations')
    $limitationIds = @{}
    $requiredLimitationsById = @{}
    foreach ($requirement in $script:RequiredLimitations) {
        $requiredLimitationsById[[string]$requirement.Id] = $requirement
    }
    foreach ($limitation in $limitations) {
        $id = [string](Get-RequiredProperty $limitation 'id')
        Assert-SafeId $id
        if ($limitationIds.ContainsKey($id)) {
            Stop-QaAcceptance 'qa_accept.duplicate_id'
        }
        $limitationIds[$id] = $true
        if ($id -notin $script:RequiredLimitationIds) {
            Stop-QaAcceptance 'qa_accept.limitations'
        }
        $accepted = Get-RequiredProperty $limitation 'userAccepted'
        if ($accepted -isnot [bool] -or [bool]$accepted) {
            Stop-QaAcceptance 'qa_accept.preaccepted'
        }
        $requirement = $requiredLimitationsById[$id]
        if ([string](Get-RequiredProperty $limitation 'summary') -cne [string]$requirement.Summary) {
            Stop-QaAcceptance 'qa_accept.limitations'
        }
        $limitationEvidence = Get-RequiredProperty $limitation 'evidence'
        $limitationBasis = Get-OptionalNullProperty $limitationEvidence 'basis'
        if ([string](Get-RequiredProperty $limitationEvidence 'id') -cne [string]$requirement.EvidenceId -or
            [string](Get-RequiredProperty $limitationEvidence 'state') -cne 'unavailable' -or
            [string](Get-RequiredProperty $limitationEvidence 'summary') -cne [string]$requirement.EvidenceSummary -or
            [string](Get-RequiredProperty $limitationEvidence 'referenceId') -cne [string]$requirement.ReferenceId -or
            $null -ne $limitationBasis -or
            [string](Get-RequiredProperty $limitationEvidence 'limitation') -cne [string]$requirement.EvidenceLimitation) {
            Stop-QaAcceptance 'qa_accept.limitations'
        }
    }
    if ($limitations.Count -ne $script:RequiredLimitationIds.Count -or
        @($script:RequiredLimitationIds | Where-Object { -not $limitationIds.ContainsKey($_) }).Count -ne 0) {
        Stop-QaAcceptance 'qa_accept.limitations'
    }

    $schemaByName = @{}
    foreach ($check in @(Get-RequiredProperty $Contract 'schemaChecks')) {
        $schemaName = [string](Get-RequiredProperty $check 'schema')
        if ($schemaByName.ContainsKey($schemaName)) {
            Stop-QaAcceptance 'qa_accept.duplicate_id'
        }
        $schemaByName[$schemaName] = $check
    }
    foreach ($schemaName in $script:RequiredSchemas) {
        if (-not $schemaByName.ContainsKey($schemaName) -or
            [string](Get-RequiredProperty $schemaByName[$schemaName] 'outcome') -ne 'pass') {
            Stop-QaAcceptance 'qa_accept.incomplete_schemas'
        }
        Assert-ObservedEvidence (Get-RequiredProperty $schemaByName[$schemaName] 'evidence')
    }
    foreach ($migrationRequirement in @(
        [pscustomobject]@{ Schema = 'ai_arena.scenario_pack.v1'; Source = 'ai_arena.scenario_pack.v0'; EvidenceId = 'evidence.schema.migration.ai-arena.scenario-pack.v1' },
        [pscustomobject]@{ Schema = 'ai_arena.benchmark_pack.v1'; Source = 'ai_arena.benchmark_pack.v0'; EvidenceId = 'evidence.schema.migration.ai-arena.benchmark-pack.v1' })) {
        $check = $schemaByName[$migrationRequirement.Schema]
        $evidence = Get-RequiredProperty $check 'evidence'
        if ([string](Get-RequiredProperty $check 'migratedFromSchema') -cne $migrationRequirement.Source -or
            [string](Get-RequiredProperty $evidence 'id') -cne $migrationRequirement.EvidenceId -or
            [string](Get-RequiredProperty $evidence 'referenceId') -cne 'artifact.schema.explicit-v0-pack-migration.log') {
            Stop-QaAcceptance 'qa_accept.migration_schema'
        }
    }

    $metricByName = @{}
    foreach ($measurement in @(Get-RequiredProperty $Contract 'performance')) {
        $metric = [string](Get-RequiredProperty $measurement 'metric')
        if ($metricByName.ContainsKey($metric)) {
            Stop-QaAcceptance 'qa_accept.duplicate_id'
        }
        $metricByName[$metric] = $measurement
    }
    foreach ($metric in $script:RequiredPerformanceMetrics) {
        if (-not $metricByName.ContainsKey($metric)) {
            Stop-QaAcceptance 'qa_accept.incomplete_performance'
        }
        $measurement = $metricByName[$metric]
        $value = Get-RequiredProperty $measurement 'value'
        $threshold = Get-RequiredProperty $measurement 'threshold'
        if (($value -isnot [int] -and $value -isnot [long] -and $value -isnot [double] -and $value -isnot [decimal]) -or
            ($threshold -isnot [int] -and $threshold -isnot [long] -and $threshold -isnot [double] -and $threshold -isnot [decimal])) {
            Stop-QaAcceptance 'qa_accept.incomplete_performance'
        }
        $kind = [string](Get-RequiredProperty $measurement 'thresholdKind')
        $meets = if ($kind -eq 'maximum') { [decimal]$value -le [decimal]$threshold } elseif ($kind -eq 'minimum') { [decimal]$value -ge [decimal]$threshold } else { $false }
        if (-not $meets) {
            Stop-QaAcceptance 'qa_accept.incomplete_performance'
        }
        Assert-ObservedEvidence (Get-RequiredProperty $measurement 'evidence')
    }

    return [pscustomobject]@{
        GateById = $gateById
        CleanPasses = $cleanPasses
        Limitations = $limitations
    }
}

function Assert-ArtifactManifest {
    param(
        [Parameter(Mandatory)] [object]$Contract,
        [Parameter(Mandatory)] [string]$TrustedRoot,
        [Parameter(Mandatory)] [string]$BundleRoot
    )

    $contractTree = [string](Get-RequiredProperty $Contract 'treeFingerprint')
    $ids = @{}
    $paths = @{}
    $screenshots = [Collections.Generic.List[object]]::new()
    $automation = [Collections.Generic.List[object]]::new()
    foreach ($artifact in @(Get-RequiredProperty $Contract 'artifacts')) {
        $id = [string](Get-RequiredProperty $artifact 'id')
        Assert-SafeId $id
        if ($ids.ContainsKey($id)) {
            Stop-QaAcceptance 'qa_accept.duplicate_id'
        }
        $ids[$id] = $artifact
        $relativePath = Get-RequiredProperty $artifact 'relativePath'
        if ($paths.ContainsKey([string]$relativePath)) {
            Stop-QaAcceptance 'qa_accept.duplicate_path'
        }
        $paths[[string]$relativePath] = $true
        $fullPath = Resolve-BundleArtifactPath -TrustedRoot $TrustedRoot -BundleRoot $BundleRoot -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Stop-QaAcceptance 'qa_accept.missing_artifact'
        }
        $length = (Get-Item -LiteralPath $fullPath).Length
        if ($length -lt 1 -or $length -gt $script:MaximumArtifactBytes) {
            Stop-QaAcceptance 'qa_accept.artifact_size'
        }
        $recordedHash = [string](Get-RequiredProperty $artifact 'sha256')
        if ($recordedHash -notmatch '^[0-9a-f]{64}$' -or (Get-Sha256File $fullPath) -ne $recordedHash) {
            Stop-QaAcceptance 'qa_accept.replaced_artifact'
        }
        $kind = [string](Get-RequiredProperty $artifact 'kind')
        if ($kind -notin @('rendered-ui-screenshot', 'render-baseline')) {
            $textArtifact = ConvertFrom-BoundedText -Path $fullPath
            if (-not (Test-QaTextPrivacy $textArtifact)) {
                Stop-QaAcceptance 'qa_accept.privacy'
            }
        }
        if ($kind -in @('rendered-ui-screenshot', 'automation-tree')) {
            $provenance = Get-RequiredProperty $artifact 'provenance'
            if ($null -eq $provenance -or [string](Get-RequiredProperty $provenance 'treeFingerprint') -ne $contractTree) {
                Stop-QaAcceptance 'qa_accept.artifact_provenance'
            }
        }
        if ($kind -eq 'rendered-ui-screenshot') {
            $png = Read-BoundedBytes -Path $fullPath -MaximumBytes $script:MaximumArtifactBytes -FailureCode 'qa_accept.artifact_read'
            $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
            if ($png.Length -lt 8 -or @(0..7 | Where-Object { $png[$_] -ne $signature[$_] }).Count -ne 0) {
                Stop-QaAcceptance 'qa_accept.screenshot_format'
            }
            $screenshots.Add($artifact)
        }
        elseif ($kind -eq 'automation-tree') {
            $automation.Add($artifact)
        }
    }
    if ($screenshots.Count -eq 0 -or $automation.Count -eq 0) {
        Stop-QaAcceptance 'qa_accept.visual_manifest'
    }
    if (-not $ids.ContainsKey('artifact.schema.explicit-v0-pack-migration.log')) {
        Stop-QaAcceptance 'qa_accept.migration_artifact'
    }
    $migrationArtifact = $ids['artifact.schema.explicit-v0-pack-migration.log']
    if ([string](Get-RequiredProperty $migrationArtifact 'kind') -cne 'sanitized-gate-log' -or
        [string](Get-RequiredProperty $migrationArtifact 'relativePath') -cne 'logs/schema.explicit-v0-pack-migration.log') {
        Stop-QaAcceptance 'qa_accept.migration_artifact'
    }
    foreach ($screenshot in $screenshots) {
        $linkedId = [string](Get-RequiredProperty (Get-RequiredProperty $screenshot 'provenance') 'linkedAutomationArtifactId')
        if ([string]::IsNullOrWhiteSpace($linkedId) -or -not $ids.ContainsKey($linkedId) -or
            [string](Get-RequiredProperty $ids[$linkedId] 'kind') -ne 'automation-tree') {
            Stop-QaAcceptance 'qa_accept.visual_link'
        }
    }
    return [pscustomobject]@{
        Screenshots = @($screenshots | Sort-Object { [string](Get-RequiredProperty $_ 'id') })
        Automation = @($automation | Sort-Object { [string](Get-RequiredProperty $_ 'id') })
        Ids = $ids
    }
}

function Assert-AcceptanceSource {
    if ($AcceptanceSource -eq 'DirectHumanAttestation') {
        if (-not $AttestReviewedVisuals.IsPresent -or
            -not [string]::IsNullOrWhiteSpace($ReviewedManifestPath) -or
            -not [string]::IsNullOrWhiteSpace($ReviewedManifestSha256)) {
            Stop-QaAcceptance 'qa_accept.attestation_required'
        }
        return
    }

    if ($AcceptanceSource -ne 'InAppReviewedManifest' -or
        $AttestReviewedVisuals.IsPresent -or
        [string]::IsNullOrWhiteSpace($ReviewedManifestPath) -or
        $ReviewedManifestSha256 -notmatch '^[0-9a-f]{64}$') {
        Stop-QaAcceptance 'qa_accept.review_manifest_required'
    }
}

function Assert-ReviewedManifestCurrent {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Sha256File $Path) -ne $ExpectedSha256) {
        Stop-QaAcceptance 'qa_accept.review_manifest_changed'
    }
}

function Assert-ReviewedManifest {
    param(
        [Parameter(Mandatory)] [object]$Contract,
        [Parameter(Mandatory)] [byte[]]$EvidenceBytes,
        [Parameter(Mandatory)] [object]$Visuals,
        [Parameter(Mandatory)] [string]$TrustedRoot,
        [Parameter(Mandatory)] [string]$BundleRoot
    )

    $expectedPath = Resolve-BundleArtifactPath -TrustedRoot $TrustedRoot -BundleRoot $BundleRoot -RelativePath $script:ReviewManifestRelativePath
    $providedPath = [IO.Path]::GetFullPath($ReviewedManifestPath)
    if (-not [string]::Equals($providedPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-QaAcceptance 'qa_accept.review_manifest_path'
    }
    Assert-NoReparsePoint -TrustedRoot $TrustedRoot -BasePath $BundleRoot -TargetPath $providedPath
    Assert-ReviewedManifestCurrent -Path $providedPath -ExpectedSha256 $ReviewedManifestSha256
    $document = ConvertFrom-BoundedJson -Path $providedPath -FailureCode 'qa_accept.review_manifest_read'
    if ($document.Bytes.Length -ge 3 -and
        $document.Bytes[0] -eq 0xEF -and $document.Bytes[1] -eq 0xBB -and $document.Bytes[2] -eq 0xBF) {
        Stop-QaAcceptance 'qa_accept.review_manifest_utf8'
    }
    $manifest = $document.Value
    Assert-ExactPropertyNames -InputObject $manifest -Names @('schema', 'evidenceSha256', 'treeFingerprint', 'updatedAtUtc', 'screenshots') -FailureCode 'qa_accept.review_manifest_shape'
    if ([string](Get-RequiredProperty $manifest 'schema') -ne $script:ReviewManifestSchema -or
        [string](Get-RequiredProperty $manifest 'evidenceSha256') -ne (Get-Sha256Bytes $EvidenceBytes) -or
        [string](Get-RequiredProperty $manifest 'treeFingerprint') -ne [string](Get-RequiredProperty $Contract 'treeFingerprint')) {
        Stop-QaAcceptance 'qa_accept.review_manifest_binding'
    }
    try {
        $updatedAt = [DateTimeOffset](Get-RequiredProperty $manifest 'updatedAtUtc')
    }
    catch {
        Stop-QaAcceptance 'qa_accept.review_manifest_time'
    }
    if ($updatedAt.Offset -ne [TimeSpan]::Zero -or $updatedAt -gt (Get-Date).ToUniversalTime().AddMinutes(5)) {
        Stop-QaAcceptance 'qa_accept.review_manifest_time'
    }

    $expected = @($Visuals.Screenshots | Sort-Object { [string](Get-RequiredProperty $_ 'id') })
    $actual = @(Get-RequiredProperty $manifest 'screenshots')
    if ($expected.Count -eq 0 -or $actual.Count -ne $expected.Count) {
        Stop-QaAcceptance 'qa_accept.review_manifest_incomplete'
    }
    $actualById = @{}
    foreach ($review in $actual) {
        Assert-ExactPropertyNames -InputObject $review -Names @('artifactId', 'sha256', 'reviewed') -FailureCode 'qa_accept.review_manifest_shape'
        $id = [string](Get-RequiredProperty $review 'artifactId')
        Assert-SafeId $id
        if ($actualById.ContainsKey($id)) {
            Stop-QaAcceptance 'qa_accept.review_manifest_incomplete'
        }
        $reviewed = Get-RequiredProperty $review 'reviewed'
        if ($reviewed -isnot [bool] -or -not [bool]$reviewed) {
            Stop-QaAcceptance 'qa_accept.review_manifest_incomplete'
        }
        $actualById[$id] = $review
    }
    foreach ($screenshot in $expected) {
        $id = [string](Get-RequiredProperty $screenshot 'id')
        if (-not $actualById.ContainsKey($id) -or
            [string](Get-RequiredProperty $actualById[$id] 'sha256') -ne [string](Get-RequiredProperty $screenshot 'sha256')) {
            Stop-QaAcceptance 'qa_accept.review_manifest_binding'
        }
    }
    return [pscustomobject]@{
        Path = $providedPath
        Sha256 = $ReviewedManifestSha256
    }
}

function Assert-EvidenceReferences {
    param(
        [Parameter(Mandatory)] [object]$Contract,
        [Parameter(Mandatory)] [hashtable]$ArtifactIds
    )

    $assertions = [Collections.Generic.List[object]]::new()
    foreach ($gate in @(Get-RequiredProperty $Contract 'gates')) {
        $assertions.Add((Get-RequiredProperty $gate 'evidence'))
    }
    foreach ($measurement in @(Get-RequiredProperty $Contract 'performance')) {
        $assertions.Add((Get-RequiredProperty $measurement 'evidence'))
    }
    foreach ($check in @(Get-RequiredProperty $Contract 'schemaChecks')) {
        $assertions.Add((Get-RequiredProperty $check 'evidence'))
    }
    foreach ($limitation in @(Get-RequiredProperty $Contract 'acceptedLimitations')) {
        $assertions.Add((Get-RequiredProperty $limitation 'evidence'))
    }
    foreach ($assertion in @(Get-RequiredProperty $Contract 'evidence')) {
        $assertions.Add($assertion)
    }
    $assertions.Add((Get-RequiredProperty (Get-RequiredProperty $Contract 'inspection') 'evidence'))

    foreach ($assertion in $assertions) {
        if ([string](Get-RequiredProperty $assertion 'state') -ne 'observed') {
            continue
        }
        $referenceId = [string](Get-RequiredProperty $assertion 'referenceId')
        if ([string]::IsNullOrWhiteSpace($referenceId) -or -not $ArtifactIds.ContainsKey($referenceId)) {
            Stop-QaAcceptance 'qa_accept.evidence_reference'
        }
    }
}

function ConvertFrom-BoundedText {
    param([Parameter(Mandatory)] [string]$Path)

    $bytes = Read-BoundedBytes -Path $Path -MaximumBytes $script:MaximumEvidenceBytes -FailureCode 'qa_accept.text_artifact_size'
    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        Stop-QaAcceptance 'qa_accept.text_artifact_utf8'
    }
}

function Assert-TestValidatorAllowed {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$ValidatorPath
    )

    if ([string]::Equals($Root.TrimEnd('\', '/'), $script:RealRepositoryRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        Stop-QaAcceptance 'qa_accept.test_validator_forbidden'
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $validator = [IO.Path]::GetFullPath($ValidatorPath)
    $rootPrefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $Root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $validator.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $Root '.ai-arena-qa-accept-fixture') -PathType Leaf) -or
        -not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        Stop-QaAcceptance 'qa_accept.test_validator_forbidden'
    }
    return $validator
}

function Get-LocalPackageSource {
    $candidate = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
    }
    else {
        $env:NUGET_PACKAGES
    }
    try {
        $full = [IO.Path]::GetFullPath($candidate).TrimEnd('\', '/')
        if (-not (Test-Path -LiteralPath $full -PathType Container)) {
            Stop-QaAcceptance 'qa_accept.validator_package_source'
        }
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-QaAcceptance 'qa_accept.validator_package_source'
        }
        return $full
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance 'qa_accept.validator_package_source'
    }
}

function Write-OfflineNuGetConfig {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$PackageSource
    )

    $escaped = [Security.SecurityElement]::Escape($PackageSource)
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-global-packages" value="$escaped" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText($Path, $xml + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-ValidatorRestoreInputManifest {
    param([Parameter(Mandatory)] [string]$OutputPath)

    $root = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\', '/')
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $manifest = @{}
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Force -Recurse)) {
            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-QaAcceptance 'qa_accept.validator_restore_tampered'
            }
            if ($item.PSIsContainer) {
                continue
            }
            $relativePath = $fullPath.Substring($prefix.Length).Replace('\', '/')
            $fileName = [IO.Path]::GetFileName($fullPath)
            $isPackage = $relativePath.StartsWith('packages/', [StringComparison]::Ordinal)
            $isRestoreInput = $relativePath.StartsWith('obj/', [StringComparison]::Ordinal) -and
                ($fileName -eq 'project.assets.json' -or
                 $fileName.EndsWith('.nuget.g.props', [StringComparison]::Ordinal) -or
                 $fileName.EndsWith('.nuget.g.targets', [StringComparison]::Ordinal))
            if (-not $isPackage -and -not $isRestoreInput -and $relativePath -ne 'NuGet.Config') {
                continue
            }
            if ([string]::IsNullOrWhiteSpace($relativePath) -or $manifest.ContainsKey($relativePath)) {
                Stop-QaAcceptance 'qa_accept.validator_restore_tampered'
            }
            $manifest[$relativePath] = Get-Sha256File $fullPath
        }
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance 'qa_accept.validator_restore_tampered'
    }
    $assetCount = @($manifest.Keys | Where-Object { $_.EndsWith('project.assets.json', [StringComparison]::Ordinal) }).Count
    if ($assetCount -eq 0 -or -not $manifest.ContainsKey('NuGet.Config')) {
        Stop-QaAcceptance 'qa_accept.validator_restore'
    }
    return $manifest
}

function Assert-ValidatorRestoreInputs {
    if ([string]::IsNullOrWhiteSpace($script:ValidatorOutputPath) -or $null -eq $script:ValidatorRestoreInputManifest) {
        Stop-QaAcceptance 'qa_accept.validator_restore'
    }
    $current = Get-ValidatorRestoreInputManifest -OutputPath $script:ValidatorOutputPath
    if ($current.Count -ne $script:ValidatorRestoreInputManifest.Count) {
        Stop-QaAcceptance 'qa_accept.validator_restore_tampered'
    }
    foreach ($relativePath in $script:ValidatorRestoreInputManifest.Keys) {
        if (-not $current.ContainsKey($relativePath) -or
            -not [string]::Equals([string]$current[$relativePath], [string]$script:ValidatorRestoreInputManifest[$relativePath], [StringComparison]::Ordinal)) {
            Stop-QaAcceptance 'qa_accept.validator_restore_tampered'
        }
    }
}

function Get-ValidatorOutputManifest {
    param([Parameter(Mandatory)] [string]$OutputPath)

    $root = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\', '/')
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $manifest = @{}
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Force -Recurse)) {
            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-QaAcceptance 'qa_accept.validator_tampered'
            }
            if ($item.PSIsContainer) {
                continue
            }
            $relativePath = $fullPath.Substring($prefix.Length).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($relativePath) -or $manifest.ContainsKey($relativePath)) {
                Stop-QaAcceptance 'qa_accept.validator_tampered'
            }
            $manifest[$relativePath] = Get-Sha256File $fullPath
        }
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance 'qa_accept.validator_tampered'
    }
    return $manifest
}

function Assert-FreshValidatorOutput {
    if ([string]::IsNullOrWhiteSpace($script:ValidatorOutputPath) -or
        [string]::IsNullOrWhiteSpace($script:ValidatorDllPath) -or
        $null -eq $script:ValidatorOutputManifest -or
        -not (Test-Path -LiteralPath $script:ValidatorDllPath -PathType Leaf)) {
        Stop-QaAcceptance 'qa_accept.validator_unavailable'
    }

    Assert-ValidatorRestoreInputs

    $currentManifest = Get-ValidatorOutputManifest -OutputPath $script:ValidatorOutputPath
    if ($currentManifest.Count -ne $script:ValidatorOutputManifest.Count) {
        Stop-QaAcceptance 'qa_accept.validator_tampered'
    }
    foreach ($relativePath in $script:ValidatorOutputManifest.Keys) {
        if (-not $currentManifest.ContainsKey($relativePath) -or
            -not [string]::Equals(
                [string]$currentManifest[$relativePath],
                [string]$script:ValidatorOutputManifest[$relativePath],
                [StringComparison]::Ordinal)) {
            Stop-QaAcceptance 'qa_accept.validator_tampered'
        }
    }
}

function Initialize-AuthoritativeValidator {
    param([Parameter(Mandatory)] [string]$Root)

    if (-not [string]::IsNullOrWhiteSpace($TestValidatorScript)) {
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($script:ValidatorDllPath)) {
        Assert-FreshValidatorOutput
        return
    }

    $projectPath = Join-Path $Root 'tests\AIArena.VerificationLab\AIArena.VerificationLab.csproj'
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        Stop-QaAcceptance 'qa_accept.validator_unavailable'
    }

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $outputPath = Join-Path $temporaryRoot ('ai-arena-qa-validator-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void](New-Item -ItemType Directory -Path $outputPath)
        $script:ValidatorOutputPath = [IO.Path]::GetFullPath($outputPath)
        $packageSource = Get-LocalPackageSource
        $packageRoot = Join-Path $outputPath 'packages'
        $configPath = Join-Path $outputPath 'NuGet.Config'
        Write-OfflineNuGetConfig -Path $configPath -PackageSource $packageSource
        & dotnet restore $projectPath --artifacts-path $outputPath --packages $packageRoot --configfile $configPath --no-cache --force --disable-parallel --nologo --verbosity quiet '-p:NuGetAudit=false' 1>$null 2>$null
        if ($LASTEXITCODE -ne 0) {
            Stop-QaAcceptance 'qa_accept.validator_restore'
        }
        $script:ValidatorRestoreInputManifest = Get-ValidatorRestoreInputManifest -OutputPath $outputPath
        & dotnet build $projectPath --configuration Release --no-restore --artifacts-path $outputPath --disable-build-servers --no-incremental --nologo --verbosity quiet '-p:UseAppHost=false' '-p:NuGetAudit=false' 1>$null 2>$null
        if ($LASTEXITCODE -ne 0) {
            Stop-QaAcceptance 'qa_accept.validator_build'
        }
        Assert-ValidatorRestoreInputs
        $dllCandidates = @(Get-ChildItem -LiteralPath (Join-Path $outputPath 'bin') -Filter 'AIArena.VerificationLab.dll' -File -Recurse -ErrorAction SilentlyContinue)
        if ($dllCandidates.Count -ne 1) {
            Stop-QaAcceptance 'qa_accept.validator_unavailable'
        }
        $dllPath = $dllCandidates[0].FullName
        $manifest = Get-ValidatorOutputManifest -OutputPath $outputPath
        if ($manifest.Count -eq 0) {
            Stop-QaAcceptance 'qa_accept.validator_unavailable'
        }
        $script:ValidatorDllPath = [IO.Path]::GetFullPath($dllPath)
        $script:ValidatorOutputManifest = $manifest
    }
    catch [InvalidOperationException] {
        throw
    }
    catch {
        Stop-QaAcceptance 'qa_accept.validator_build'
    }
}

function Remove-AuthoritativeValidator {
    if ([string]::IsNullOrWhiteSpace($script:ValidatorOutputPath)) {
        return
    }

    $candidate = [IO.Path]::GetFullPath($script:ValidatorOutputPath).TrimEnd('\', '/')
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $candidate)).TrimEnd('\', '/')
    $leaf = Split-Path -Leaf $candidate
    if (-not [string]::Equals($parent, $temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^ai-arena-qa-validator-[a-f0-9]{32}$') {
        Stop-QaAcceptance 'qa_accept.validator_cleanup'
    }
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-QaAcceptance 'qa_accept.validator_cleanup'
        }
        # Inspect every descendant before a recursive delete so a tampered
        # output cannot redirect cleanup through a child junction or symlink.
        [void](Get-ValidatorOutputManifest -OutputPath $candidate)
        Remove-Item -LiteralPath $candidate -Recurse -Force
    }
    $script:ValidatorOutputPath = $null
    $script:ValidatorDllPath = $null
    $script:ValidatorOutputManifest = $null
    $script:ValidatorRestoreInputManifest = $null
}

function Invoke-AuthoritativeValidation {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Root
    )

    $engine = (Get-Process -Id $PID).Path
    if (-not [string]::IsNullOrWhiteSpace($TestValidatorScript)) {
        $validator = Assert-TestValidatorAllowed -Root $Root -ValidatorPath $TestValidatorScript
        $arguments = @('-NoProfile')
        if ([IO.Path]::GetFileNameWithoutExtension($engine) -ieq 'powershell') {
            $arguments += @('-ExecutionPolicy', 'Bypass')
        }
        $arguments += @('-File', $validator, '--validate-evidence-current', $Path, $Root)
        & $engine @arguments 1>$null 2>$null
        if ($LASTEXITCODE -ne 0) {
            Stop-QaAcceptance 'qa_accept.authoritative_validation'
        }
        return
    }

    Initialize-AuthoritativeValidator -Root $Root
    Assert-FreshValidatorOutput
    & dotnet $script:ValidatorDllPath --validate-evidence-current $Path $Root 1>$null 2>$null
    $validatorExitCode = $LASTEXITCODE
    Assert-FreshValidatorOutput
    if ($validatorExitCode -ne 0) {
        Stop-QaAcceptance 'qa_accept.authoritative_validation'
    }
}

function New-ObservedEvidence {
    param([Parameter(Mandatory)] [string]$Summary)

    return [pscustomobject][ordered]@{
        id = 'evidence.inspection'
        state = 'observed'
        summary = $Summary
        referenceId = $script:ReceiptArtifactId
        basis = $null
        limitation = $null
    }
}

function Write-AtomicReplacement {
    param(
        [Parameter(Mandatory)] [string]$TemporaryPath,
        [Parameter(Mandatory)] [string]$DestinationPath,
        [Parameter(Mandatory)] [string]$BackupPath
    )

    try {
        [IO.File]::Replace($TemporaryPath, $DestinationPath, $BackupPath, $true)
    }
    catch {
        Stop-QaAcceptance 'qa_accept.atomic_replace'
    }
}

function Restore-OriginalEvidence {
    param(
        [Parameter(Mandatory)] [string]$Evidence,
        [Parameter(Mandatory)] [byte[]]$OriginalBytes,
        [AllowNull()] [string]$Backup
    )

    $discardPath = Join-Path (Split-Path -Parent $Evidence) ('.qa-evidence.discard.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $restoreTemp = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($Backup) -and (Test-Path -LiteralPath $Backup -PathType Leaf)) {
            [IO.File]::Replace($Backup, $Evidence, $discardPath, $true)
        }
        elseif ((Get-Sha256File $Evidence) -ne (Get-Sha256Bytes $OriginalBytes)) {
            $restoreTemp = Join-Path (Split-Path -Parent $Evidence) ('.qa-evidence.restore.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
            [IO.File]::WriteAllBytes($restoreTemp, $OriginalBytes)
            [IO.File]::Replace($restoreTemp, $Evidence, $discardPath, $true)
        }
        if ((Get-Sha256File $Evidence) -ne (Get-Sha256Bytes $OriginalBytes)) {
            Stop-QaAcceptance 'qa_accept.rollback_failed'
        }
    }
    catch {
        Stop-QaAcceptance 'qa_accept.rollback_failed'
    }
    finally {
        foreach ($temporary in @($discardPath, $restoreTemp)) {
            if (-not [string]::IsNullOrWhiteSpace($temporary) -and (Test-Path -LiteralPath $temporary -PathType Leaf)) {
                Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-QaInspectionAcceptance {
    Assert-AcceptanceSource
    $root = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $script:RealRepositoryRoot } else { [IO.Path]::GetFullPath($RepositoryRoot) }
    $evidence = [IO.Path]::GetFullPath($EvidencePath)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\qa')).TrimEnd('\', '/')
    $artifactPrefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $evidence.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFileName($evidence), 'qa-evidence.json', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $evidence -PathType Leaf)) {
        Stop-QaAcceptance 'qa_accept.evidence_path'
    }
    $bundleRoot = Split-Path -Parent $evidence
    Assert-NoReparsePoint -TrustedRoot $root -BasePath $artifactRoot -TargetPath $evidence

    $script:LockPath = Join-Path $bundleRoot '.inspection-accept.lock'
    try {
        $script:LockStream = [IO.FileStream]::new(
            $script:LockPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
    }
    catch {
        Stop-QaAcceptance 'qa_accept.concurrent'
    }

    $receiptPath = Resolve-BundleArtifactPath -TrustedRoot $root -BundleRoot $bundleRoot -RelativePath $script:ReceiptRelativePath
    $receiptDirectory = Split-Path -Parent $receiptPath
    if (-not (Test-Path -LiteralPath $receiptDirectory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $receiptDirectory)
    }
    if (Test-Path -LiteralPath $receiptPath) {
        Stop-QaAcceptance 'qa_accept.receipt_exists'
    }

    $document = ConvertFrom-BoundedJson -Path $evidence -FailureCode 'qa_accept.evidence_read'
    $contract = $document.Value
    $beforeState = Get-CurrentRepositoryState -Root $root
    Initialize-AuthoritativeValidator -Root $root
    Invoke-AuthoritativeValidation -Path $evidence -Root $root
    $completeness = Assert-ContractCompleteness -Contract $contract -RepositoryState $beforeState
    $visuals = Assert-ArtifactManifest -Contract $contract -TrustedRoot $root -BundleRoot $bundleRoot
    Assert-EvidenceReferences -Contract $contract -ArtifactIds $visuals.Ids
    $reviewManifest = if ($AcceptanceSource -eq 'InAppReviewedManifest') {
        Assert-ReviewedManifest -Contract $contract -EvidenceBytes $document.Bytes -Visuals $visuals -TrustedRoot $root -BundleRoot $bundleRoot
    }
    else {
        $null
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $acceptedAt = (Get-Date).ToUniversalTime()
    $acceptedAtText = $acceptedAt.ToString('o')
    $visualReceiptEntries = @(
        @($visuals.Screenshots) + @($visuals.Automation) |
            Sort-Object { [string](Get-RequiredProperty $_ 'id') } |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    id = [string](Get-RequiredProperty $_ 'id')
                    kind = [string](Get-RequiredProperty $_ 'kind')
                    sha256 = [string](Get-RequiredProperty $_ 'sha256')
                }
            }
    )
    $limitationReceiptEntries = @(
        $completeness.Limitations |
            Sort-Object { [string](Get-RequiredProperty $_ 'id') } |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    id = [string](Get-RequiredProperty $_ 'id')
                    evidenceState = [string](Get-RequiredProperty (Get-RequiredProperty $_ 'evidence') 'state')
                    semanticSha256 = Get-LimitationSemanticSha256 $_
                }
            }
    )
    $acceptanceMode = if ($AcceptanceSource -eq 'InAppReviewedManifest') { 'in-app-reviewed-manifest' } else { 'direct-human-attestation' }
    $attestation = if ($AcceptanceSource -eq 'InAppReviewedManifest') {
        'The in-app review manifest records one successful explicit preview for every listed screenshot, and the acceptance action accepts every listed evidence limitation. This script verified exact evidence, tree, artifact, and manifest hashes; it did not independently interpret pixels.'
    }
    else {
        'The user explicitly attested via the direct CLI that every listed screenshot and linked automation artifact was reviewed and that every listed evidence limitation was accepted. This script verified exact hashes and currentness; it did not display or observe pixels.'
    }
    $receipt = [pscustomobject][ordered]@{
        schema = 'ai_arena.qa_inspection_receipt.v1'
        id = 'inspection.user-acceptance.receipt'
        acceptedAtUtc = $acceptedAtText
        acceptanceMode = $acceptanceMode
        evidenceSha256 = Get-Sha256Bytes $document.Bytes
        treeFingerprint = [string](Get-RequiredProperty $contract 'treeFingerprint')
        artifacts = $visualReceiptEntries
        limitations = $limitationReceiptEntries
        reviewManifestSha256 = if ($null -eq $reviewManifest) { $null } else { $reviewManifest.Sha256 }
        attestation = $attestation
    }
    $receiptJson = $receipt | ConvertTo-Json -Depth 10
    if (-not (Test-QaTextPrivacy $receiptJson)) {
        Stop-QaAcceptance 'qa_accept.privacy'
    }
    $receiptBytes = [Text.UTF8Encoding]::new($false).GetBytes($receiptJson + "`n")
    $receiptHash = Get-Sha256Bytes $receiptBytes
    $receiptArtifact = [pscustomobject][ordered]@{
        id = $script:ReceiptArtifactId
        kind = 'inspection-receipt'
        relativePath = $script:ReceiptRelativePath
        sha256 = $receiptHash
        provenance = $null
    }

    $inspectionSummary = if ($AcceptanceSource -eq 'InAppReviewedManifest') {
        'The hash-bound in-app review manifest covered every referenced screenshot for this exact tested tree.'
    }
    else {
        'The user explicitly attested to external review of every referenced screenshot for this exact tested tree; the script did not observe pixels.'
    }
    $observedEvidence = New-ObservedEvidence -Summary $inspectionSummary
    $inspectionGate = $completeness.GateById['inspection.user-acceptance']
    $inspectionGate.outcome = 'pass'
    $inspectionGate.durationMilliseconds = [long]$watch.ElapsedMilliseconds
    $inspectionGate.tests = [pscustomobject][ordered]@{ passed = 1; failed = 0; skipped = 0; total = 1 }
    $inspectionGate.evidence = $observedEvidence

    $contract.artifacts = @(@(Get-RequiredProperty $contract 'artifacts') + $receiptArtifact | Sort-Object { [string](Get-RequiredProperty $_ 'id') })
    $contract.inspection = [pscustomobject][ordered]@{
        userAccepted = $true
        acceptedAtUtc = $acceptedAtText
        treeFingerprint = [string](Get-RequiredProperty $contract 'treeFingerprint')
        screenshotArtifactIds = @($visuals.Screenshots | ForEach-Object { [string](Get-RequiredProperty $_ 'id') })
        automationArtifactIds = @($visuals.Automation | ForEach-Object { [string](Get-RequiredProperty $_ 'id') })
        evidence = $observedEvidence
    }
    foreach ($requiredLimitationId in $script:RequiredLimitationIds) {
        $requiredLimitation = @($completeness.Limitations | Where-Object { [string](Get-RequiredProperty $_ 'id') -eq $requiredLimitationId })
        if ($requiredLimitation.Count -ne 1) {
            Stop-QaAcceptance 'qa_accept.limitations'
        }
        $requiredLimitation[0].userAccepted = $true
    }
    $contract.completedAtUtc = $acceptedAtText
    $contract.verdict = 'sealed'

    $candidateJson = $contract | ConvertTo-Json -Depth 30
    if (-not (Test-QaTextPrivacy $candidateJson)) {
        Stop-QaAcceptance 'qa_accept.privacy'
    }
    $candidatePath = Join-Path $bundleRoot ('.qa-evidence.accept.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $receiptTemp = Join-Path $receiptDirectory ('.inspection-receipt.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $backupPath = Join-Path $bundleRoot ('.qa-evidence.accept.{0}.bak' -f [Guid]::NewGuid().ToString('N'))
    $receiptCommitted = $false
    $evidenceCommitted = $false
    try {
        [IO.File]::WriteAllBytes($receiptTemp, $receiptBytes)
        [IO.File]::Move($receiptTemp, $receiptPath)
        $receiptCommitted = $true
        Write-Utf8NoBom -Path $candidatePath -Text ($candidateJson + "`n")

        if ($null -ne $reviewManifest) {
            Assert-ReviewedManifestCurrent -Path $reviewManifest.Path -ExpectedSha256 $reviewManifest.Sha256
        }

        # Validate the sealed candidate while the canonical partial evidence is
        # still untouched. The candidate lives in the bundle root, so relative
        # artifact validation sees the exact final receipt and visual files.
        Invoke-AuthoritativeValidation -Path $candidatePath -Root $root
        $candidateDocument = ConvertFrom-BoundedJson -Path $candidatePath -FailureCode 'qa_accept.candidate_read'
        $candidateState = Get-CurrentRepositoryState -Root $root
        if ($candidateState.CompositeFingerprint -ne $beforeState.CompositeFingerprint -or
            $candidateState.MapFingerprint -ne $beforeState.MapFingerprint) {
            Stop-QaAcceptance 'qa_accept.stale_tree'
        }
        $candidateArtifacts = Assert-ArtifactManifest -Contract $candidateDocument.Value -TrustedRoot $root -BundleRoot $bundleRoot
        Assert-EvidenceReferences -Contract $candidateDocument.Value -ArtifactIds $candidateArtifacts.Ids

        Write-AtomicReplacement -TemporaryPath $candidatePath -DestinationPath $evidence -BackupPath $backupPath
        $evidenceCommitted = $true
        Invoke-AuthoritativeValidation -Path $evidence -Root $root
        $afterState = Get-CurrentRepositoryState -Root $root
        if ($afterState.CompositeFingerprint -ne $beforeState.CompositeFingerprint -or
            $afterState.MapFingerprint -ne $beforeState.MapFingerprint) {
            Stop-QaAcceptance 'qa_accept.stale_tree'
        }
        $finalDocument = ConvertFrom-BoundedJson -Path $evidence -FailureCode 'qa_accept.final_read'
        if ([string](Get-RequiredProperty $finalDocument.Value 'verdict') -ne 'sealed' -or
            -not [bool](Get-RequiredProperty (Get-RequiredProperty $finalDocument.Value 'inspection') 'userAccepted')) {
            Stop-QaAcceptance 'qa_accept.final_state'
        }
        $finalArtifacts = Assert-ArtifactManifest -Contract $finalDocument.Value -TrustedRoot $root -BundleRoot $bundleRoot
        Assert-EvidenceReferences -Contract $finalDocument.Value -ArtifactIds $finalArtifacts.Ids
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Remove-Item -LiteralPath $backupPath -Force
        }
        if ($null -ne $reviewManifest) {
            Assert-ReviewedManifestCurrent -Path $reviewManifest.Path -ExpectedSha256 $reviewManifest.Sha256
            Remove-Item -LiteralPath $reviewManifest.Path -Force
            if (Test-Path -LiteralPath $reviewManifest.Path) {
                Stop-QaAcceptance 'qa_accept.review_manifest_consume'
            }
        }
    }
    catch {
        $failure = $_
        $rollbackFailed = $false
        if ($evidenceCommitted) {
            try {
                Restore-OriginalEvidence -Evidence $evidence -OriginalBytes $document.Bytes -Backup $backupPath
            }
            catch {
                $rollbackFailed = $true
            }
        }
        if ($receiptCommitted -and (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
            try { Remove-Item -LiteralPath $receiptPath -Force } catch { $rollbackFailed = $true }
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            try { Remove-Item -LiteralPath $backupPath -Force } catch { $rollbackFailed = $true }
        }
        if ($rollbackFailed) {
            Stop-QaAcceptance 'qa_accept.rollback_failed'
        }
        throw $failure
    }
    finally {
        foreach ($temporary in @($candidatePath, $receiptTemp)) {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }
}

$failureCode = $null
try {
    Invoke-QaInspectionAcceptance
}
catch {
    $candidateCode = $_.Exception.Message
    $failureCode = if ($candidateCode -match '^qa_accept\.[a-z0-9_]+$') { $candidateCode } else { 'qa_accept.internal' }
}
finally {
    try {
        Remove-AuthoritativeValidator
    }
    catch {
        if ($null -eq $failureCode) {
            $failureCode = 'qa_accept.validator_cleanup'
        }
    }
    if ($null -ne $script:LockStream) {
        $script:LockStream.Dispose()
    }
    if (-not [string]::IsNullOrWhiteSpace($script:LockPath) -and (Test-Path -LiteralPath $script:LockPath -PathType Leaf)) {
        Remove-Item -LiteralPath $script:LockPath -Force -ErrorAction SilentlyContinue
    }
}

if ($null -ne $failureCode) {
    Write-Host "BLOCKED $failureCode"
    exit 1
}

Write-Host 'SEALED inspection.user-acceptance'
exit 0
