$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$acceptanceScript = Join-Path $repositoryRoot 'scripts/qa-accept-inspection.ps1'
$enginePath = (Get-Process -Id $PID).Path
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ai-arena-qa-accept-' + [Guid]::NewGuid().ToString('N'))
$mapRoot = Join-Path $fixtureRoot 'map'
$artifactRoot = Join-Path $fixtureRoot 'artifacts/qa'
$validatorScript = Join-Path $fixtureRoot 'validator-stub.ps1'
$verificationProjectRoot = Join-Path $fixtureRoot 'tests/AIArena.VerificationLab'
$verificationProjectPath = Join-Path $verificationProjectRoot 'AIArena.VerificationLab.csproj'
$verificationProgramPath = Join-Path $verificationProjectRoot 'Program.cs'
$utf8 = [Text.UTF8Encoding]::new($false)
$testsFailed = $false

$requiredGlobalGateIds = @(
    'postflight.evidence-privacy',
    'postflight.map-source-stability',
    'postflight.source-stability',
    'preflight.artifact-root-ignored',
    'preflight.map-source-clean',
    'preflight.seal-configuration',
    'preflight.source-clean',
    'preflight.toolchain',
    'ui.keyboard-automation-matrix',
    'ui.reduced-motion-matrix',
    'ui.theme-contrast-matrix',
    'ui.viewport-dpi-matrix',
    'verification.restart-soak-resource'
)
$requiredPerPassGateSuffixes = @(
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
$requiredSchemas = @(
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
$requiredPerformanceMetrics = @(
    'cancellation-latency',
    'clean-release-build-duration',
    'fault-recovery-latency',
    'handle-growth',
    'peak-working-set',
    'release-app-startup-duration',
    'soak-duration'
)
$requiredLimitations = @(
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

function Require {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Write-FixtureText {
    param([string]$Path, [string]$Text)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Write-RealValidatorProgram {
    param([int]$ExitCode)

    $source = @'
using System.Reflection;
using System.Text;

if (args.Length != 3 || !string.Equals(args[0], "--validate-evidence-current", StringComparison.Ordinal))
{
    return 91;
}

var repositoryRoot = Path.GetFullPath(args[2]);
var artifactRoot = Path.Combine(repositoryRoot, "artifacts");
Directory.CreateDirectory(artifactRoot);
var callLog = Path.Combine(artifactRoot, "real-validator-calls.log");
File.AppendAllText(
    callLog,
    Assembly.GetExecutingAssembly().Location + Environment.NewLine,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
if (File.Exists(Path.Combine(artifactRoot, "tamper-validator-output.flag")))
{
    File.WriteAllText(
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "tampered.marker"),
        "tampered",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
return __EXIT_CODE__;
'@.Replace('__EXIT_CODE__', $ExitCode.ToString([Globalization.CultureInfo]::InvariantCulture))
    Write-FixtureText -Path $verificationProgramPath -Text ($source + "`n")
}

function Invoke-FixtureGit {
    param([string]$Root, [string[]]$Arguments)

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -c 'core.excludesFile=' -C $Root @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "Fixture git command failed."
    }
    return @($output)
}

function Get-Sha256Bytes {
    param([byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-Sha256Text {
    param([string]$Text)

    return Get-Sha256Bytes ([Text.Encoding]::UTF8.GetBytes($Text))
}

function Get-LimitationSemanticSha256 {
    param([object]$Limitation)

    $basis = $Limitation.evidence.basis
    $fields = @(
        [string]$Limitation.id
        [string]$Limitation.summary
        [string]$Limitation.evidence.id
        [string]$Limitation.evidence.state
        [string]$Limitation.evidence.summary
        [string]$Limitation.evidence.referenceId
        $(if ($null -eq $basis) { '<null>' } else { [string]$basis })
        [string]$Limitation.evidence.limitation
    )
    return Get-Sha256Text (($fields -join [char]0) + "`n")
}

function Get-Sha256File {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-FixtureFingerprint {
    param([string]$Root, [string[]]$ExcludedPrefixes = @())

    $paths = @(Invoke-FixtureGit -Root $Root -Arguments @('ls-files', '--cached', '--others', '--exclude-standard')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    $manifest = [Text.StringBuilder]::new()
    foreach ($path in $paths) {
        $normalized = ([string]$path).Replace('\', '/')
        if (@($ExcludedPrefixes | Where-Object { $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 0) {
            continue
        }
        $fullPath = Join-Path $Root $path
        $hash = if (Test-Path -LiteralPath $fullPath -PathType Leaf) { Get-Sha256File $fullPath } else { 'deleted' }
        [void]$manifest.Append($normalized).Append([char]0).Append($hash).Append("`n")
    }
    return Get-Sha256Text $manifest.ToString()
}

function New-ObservedEvidence {
    param([string]$Id, [string]$ReferenceId = 'artifact.automation.main')

    return [pscustomobject][ordered]@{
        id = $Id
        state = 'observed'
        summary = 'The deterministic fixture recorded this result.'
        referenceId = $ReferenceId
        basis = $null
        limitation = $null
    }
}

function New-PassingGate {
    param([string]$Id)

    return [pscustomobject][ordered]@{
        id = $Id
        outcome = 'pass'
        required = $true
        durationMilliseconds = 1
        tests = [pscustomobject][ordered]@{ passed = 1; failed = 0; skipped = 0; total = 1 }
        evidence = New-ObservedEvidence -Id ('evidence.' + $Id)
    }
}

function New-CompletePartialBundle {
    param([string]$Name, [string]$Mutation = '')

    $bundleRoot = Join-Path $artifactRoot $Name
    $screenshotPath = Join-Path $bundleRoot 'screenshots/view.png'
    $automationPath = Join-Path $bundleRoot 'metadata/automation.json'
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $screenshotPath) -Force)
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $automationPath) -Force)
    $png = [byte[]](137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4)
    [IO.File]::WriteAllBytes($screenshotPath, $png)
    Write-FixtureText -Path $automationPath -Text "{`"schema`":`"ai_arena.automation_tree.v1`",`"nodes`":[]}`n"

    $outerRevision = ([string](@(Invoke-FixtureGit -Root $fixtureRoot -Arguments @('rev-parse', 'HEAD'))[0])).Trim().ToLowerInvariant()
    $mapRevision = ([string](@(Invoke-FixtureGit -Root $mapRoot -Arguments @('rev-parse', 'HEAD'))[0])).Trim().ToLowerInvariant()
    $outerFingerprint = Get-FixtureFingerprint -Root $fixtureRoot -ExcludedPrefixes @('artifacts/')
    $mapFingerprint = Get-FixtureFingerprint -Root $mapRoot
    $treeFingerprint = Get-Sha256Text ("outer={0}`nmap={1}`n" -f $outerFingerprint, $mapFingerprint)
    $fixtureNow = (Get-Date).ToUniversalTime()
    $startedAt = $fixtureNow.AddHours(-2).ToString('o')
    $capturedAt = $fixtureNow.AddMinutes(-90).ToString('o')
    $completedAt = $fixtureNow.AddHours(-1).ToString('o')

    $automationArtifact = [pscustomobject][ordered]@{
        id = 'artifact.automation.main'
        kind = 'automation-tree'
        relativePath = 'metadata/automation.json'
        sha256 = Get-Sha256File $automationPath
        provenance = [pscustomobject][ordered]@{
            treeFingerprint = $treeFingerprint
            capturedAtUtc = $capturedAt
            theme = 'dark-blue'
            viewportWidthDip = 1500
            viewportHeightDip = 960
            dpiScale = 1.0
            expectedState = 'empty-qa-session'
            linkedAutomationArtifactId = $null
            baselineArtifactId = $null
        }
    }
    $screenshotArtifact = [pscustomobject][ordered]@{
        id = 'artifact.screenshot.main'
        kind = 'rendered-ui-screenshot'
        relativePath = 'screenshots/view.png'
        sha256 = Get-Sha256File $screenshotPath
        provenance = [pscustomobject][ordered]@{
            treeFingerprint = $treeFingerprint
            capturedAtUtc = $capturedAt
            theme = 'dark-blue'
            viewportWidthDip = 1500
            viewportHeightDip = 960
            dpiScale = 1.0
            expectedState = 'empty-qa-session'
            linkedAutomationArtifactId = 'artifact.automation.main'
            baselineArtifactId = $null
        }
    }

    $gates = [Collections.Generic.List[object]]::new()
    foreach ($id in $requiredGlobalGateIds) {
        $gates.Add((New-PassingGate $id))
    }
    for ($pass = 1; $pass -le 2; $pass++) {
        foreach ($suffix in $requiredPerPassGateSuffixes) {
            $gates.Add((New-PassingGate ('pass-{0:D2}.{1}' -f $pass, $suffix)))
        }
    }
    $gates.Add([pscustomobject][ordered]@{
        id = 'inspection.user-acceptance'
        outcome = 'partial'
        required = $true
        durationMilliseconds = 0
        tests = [pscustomobject][ordered]@{ passed = 0; failed = 0; skipped = 1; total = 1 }
        evidence = [pscustomobject][ordered]@{
            id = 'evidence.inspection'
            state = 'unavailable'
            summary = 'Post-render inspection has not been explicitly accepted.'
            referenceId = $null
            basis = $null
            limitation = 'Explicit post-render inspection is still required.'
        }
    })

    $contract = [pscustomobject][ordered]@{
        schema = 'ai_arena.qa_evidence.v1'
        id = ('qa.' + $Name)
        createdAtUtc = $completedAt
        sourceRevision = $outerRevision
        treeFingerprint = $treeFingerprint
        sealManifestId = 'ai_arena.qa_seal_manifest.v1'
        isWorkingTreeClean = $true
        nestedRepositories = @(
            [pscustomobject][ordered]@{
                id = 'map'
                sourceRevision = $mapRevision
                treeFingerprint = $mapFingerprint
                isWorkingTreeClean = $true
            }
        )
        startedAtUtc = $startedAt
        completedAtUtc = $completedAt
        verdict = 'partial'
        cleanFullPasses = 2
        environment = [pscustomobject][ordered]@{
            operatingSystem = 'fixture-os'
            architecture = 'x64'
            runtimeVersion = '10.0.0'
            sdkVersion = '10.0.100'
            configuration = 'Release'
            isReleaseBuild = $true
        }
        toolchain = @([pscustomobject][ordered]@{ name = 'fixture'; version = '1.0' })
        gates = @($gates)
        artifacts = @($automationArtifact, $screenshotArtifact)
        performance = @(
            foreach ($metric in $requiredPerformanceMetrics) {
                [pscustomobject][ordered]@{
                    id = ('performance.' + $metric)
                    metric = $metric
                    value = 1
                    unit = 'fixture-unit'
                    thresholdKind = 'maximum'
                    threshold = 2
                    evidence = New-ObservedEvidence -Id ('evidence.performance.' + $metric)
                }
            }
        )
        schemaChecks = @(
            foreach ($schema in $requiredSchemas) {
                [pscustomobject][ordered]@{
                    id = ('schema.' + $schema)
                    schema = $schema
                    migratedFromSchema = $null
                    outcome = 'pass'
                    evidence = New-ObservedEvidence -Id ('evidence.schema.' + $schema)
                }
            }
        )
        liveProviderCoverage = [pscustomobject][ordered]@{
            required = $false
            state = 'unavailable'
            providerProfileIds = @()
            evidenceRunIds = @()
            limitation = 'Live provider coverage was not required for deterministic verification.'
        }
        acceptedLimitations = @(
            foreach ($limitation in $requiredLimitations) {
                [pscustomobject][ordered]@{
                    id = $limitation.Id
                    summary = $limitation.Summary
                    userAccepted = $false
                    evidence = [pscustomobject][ordered]@{
                        id = $limitation.EvidenceId
                        state = 'unavailable'
                        summary = $limitation.EvidenceSummary
                        referenceId = $limitation.ReferenceId
                        basis = $null
                        limitation = $limitation.EvidenceLimitation
                    }
                }
            }
        )
        inspection = [pscustomobject][ordered]@{
            userAccepted = $false
            acceptedAtUtc = $null
            treeFingerprint = $null
            screenshotArtifactIds = @('artifact.screenshot.main')
            automationArtifactIds = @('artifact.automation.main')
            evidence = $gates[$gates.Count - 1].evidence
        }
        evidence = @()
    }

    switch ($Mutation) {
        'stale-tree' {
            $contract.treeFingerprint = ('f' * 64)
        }
        'no-automation' {
            $contract.artifacts = @($screenshotArtifact)
        }
        'bad-privacy' {
            $contract.gates[0].evidence.summary = 'Private source path C:\Users\Fixture\Secret.cs'
        }
        'incomplete-gate' {
            $gate = @($contract.gates | Where-Object { $_.id -eq 'pass-02.tests-core' })[0]
            $gate.outcome = 'partial'
            $gate.evidence.state = 'unavailable'
            $gate.evidence.referenceId = $null
            $gate.evidence.limitation = 'Fixture intentionally incomplete.'
        }
        'missing-limitation' {
            $contract.acceptedLimitations = @($contract.acceptedLimitations | Where-Object { $_.id -ne 'limitation.ui-physical-dpi' })
        }
        'renamed-limitation' {
            $contract.acceptedLimitations[0].id = 'limitation.source-boundary-renamed'
            $contract.acceptedLimitations[0].evidence.id = 'evidence.limitation.source-boundary-renamed'
        }
        'limitation-state' {
            $contract.acceptedLimitations[0].evidence.state = 'inferred'
            $contract.acceptedLimitations[0].evidence.basis = 'Fixture tamper must not be accepted.'
            $contract.acceptedLimitations[0].evidence.limitation = $null
        }
        'limitation-summary' {
            $contract.acceptedLimitations[0].summary = 'OS input and external UIA were fully tested.'
        }
        'limitation-evidence-summary' {
            $contract.acceptedLimitations[0].evidence.summary = 'All external interaction evidence was observed.'
        }
        'limitation-reference' {
            $contract.acceptedLimitations[0].evidence.referenceId = 'artifact.unrelated.log'
        }
        'limitation-text' {
            $contract.acceptedLimitations[0].evidence.limitation = 'No limitation.'
        }
        'limitation-basis' {
            $contract.acceptedLimitations[0].evidence.basis = 'This inferred basis must not be accepted.'
        }
    }

    $evidencePath = Join-Path $bundleRoot 'qa-evidence.json'
    Write-FixtureText -Path $evidencePath -Text (($contract | ConvertTo-Json -Depth 30) + "`n")
    if ($Mutation -eq 'missing-artifact') {
        Remove-Item -LiteralPath $automationPath -Force
    }
    elseif ($Mutation -eq 'replaced-artifact') {
        [IO.File]::WriteAllBytes($screenshotPath, [byte[]](137, 80, 78, 71, 13, 10, 26, 10, 9, 9, 9))
    }

    return [pscustomobject]@{
        Root = $bundleRoot
        EvidencePath = $evidencePath
        OriginalEvidenceBytes = [IO.File]::ReadAllBytes($evidencePath)
        ReceiptPath = Join-Path $bundleRoot 'metadata/inspection-receipt.json'
        TreeFingerprint = $treeFingerprint
    }
}

function Write-InAppReviewManifest {
    param(
        [Parameter(Mandatory)] [object]$Bundle,
        [string]$Mutation = ''
    )

    $contract = [IO.File]::ReadAllText($Bundle.EvidencePath, $utf8) | ConvertFrom-Json
    $screenshots = @(
        $contract.artifacts |
            Where-Object { $_.kind -eq 'rendered-ui-screenshot' } |
            Sort-Object id |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    artifactId = [string]$_.id
                    sha256 = [string]$_.sha256
                    reviewed = $true
                }
            }
    )
    if ($Mutation -eq 'incomplete' -and $screenshots.Count -gt 0) {
        $screenshots[0].reviewed = $false
    }
    $manifest = [pscustomobject][ordered]@{
        schema = 'ai_arena.qa_inspection_review.v1'
        evidenceSha256 = if ($Mutation -eq 'wrong-evidence') { 'f' * 64 } else { Get-Sha256File $Bundle.EvidencePath }
        treeFingerprint = [string]$contract.treeFingerprint
        updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        screenshots = $screenshots
    }
    $path = Join-Path $Bundle.Root 'metadata/in-app-inspection-review.json'
    Write-FixtureText -Path $path -Text (($manifest | ConvertTo-Json -Depth 10) + "`n")
    return [pscustomobject]@{
        Path = $path
        Sha256 = Get-Sha256File $path
    }
}

function Invoke-Acceptance {
    param(
        [object]$Bundle,
        [bool]$UseFixtureValidator = $true,
        [bool]$AttestReviewedVisuals = $true,
        [AllowNull()] [object]$InAppReview = $null
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $arguments = @('-NoProfile')
        if ([IO.Path]::GetFileNameWithoutExtension($enginePath) -ieq 'powershell') {
            $arguments += @('-ExecutionPolicy', 'Bypass')
        }
        $arguments += @(
            '-File', $acceptanceScript,
            '-EvidencePath', $Bundle.EvidencePath,
            '-RepositoryRoot', $fixtureRoot)
        if ($null -ne $InAppReview) {
            $arguments += @(
                '-AcceptanceSource', 'InAppReviewedManifest',
                '-ReviewedManifestPath', $InAppReview.Path,
                '-ReviewedManifestSha256', $InAppReview.Sha256)
        }
        elseif ($AttestReviewedVisuals) {
            $arguments += '-AttestReviewedVisuals'
        }
        if ($UseFixtureValidator) {
            $arguments += @('-TestValidatorScript', $validatorScript)
        }
        $output = @(& $enginePath @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output -join [Environment]::NewLine
    }
}

function Assert-BlockedUnchanged {
    param([object]$Bundle, [object]$Result, [string]$ExpectedCode)

    Require ($Result.ExitCode -ne 0) "$ExpectedCode should be blocked."
    Require ($Result.Output -match [regex]::Escape($ExpectedCode)) "$ExpectedCode should report its content-free code. Actual: $($Result.Output)"
    Require ($Result.Output -notmatch [regex]::Escape($fixtureRoot)) "$ExpectedCode must not echo a private fixture path."
    Require (-not (Test-Path -LiteralPath $Bundle.ReceiptPath)) "$ExpectedCode must not leave an inspection receipt."
    $after = [IO.File]::ReadAllBytes($Bundle.EvidencePath)
    Require ((Get-Sha256Bytes $after) -eq (Get-Sha256Bytes $Bundle.OriginalEvidenceBytes)) "$ExpectedCode must preserve the original evidence bytes."
}

try {
    $acceptanceSource = [IO.File]::ReadAllText($acceptanceScript, $utf8)
    Require ($acceptanceSource -match 'dotnet restore \$projectPath --artifacts-path \$outputPath --packages \$packageRoot --configfile \$configPath') 'Acceptance must regenerate isolated restore inputs from its explicit local-only package source.'
    Require ($acceptanceSource -match '<clear />' -and $acceptanceSource -match 'dotnet build \$projectPath --configuration Release --no-restore --artifacts-path \$outputPath') 'Acceptance must clear configured feeds and build only from the isolated restored graph.'
    Require ($acceptanceSource -match "'-p:NuGetAudit=false'") 'Acceptance must disable network-backed NuGet audit during its local-only restore/build.'
    Require ($acceptanceSource -match '& dotnet \$script:ValidatorDllPath[^\r\n]+\r?\n\s+\$validatorExitCode = \$LASTEXITCODE\r?\n\s+Assert-FreshValidatorOutput') 'Acceptance must re-hash the exact validator output after every authoritative invocation.'
    [void](New-Item -ItemType Directory -Path $fixtureRoot -Force)
    [void](New-Item -ItemType Directory -Path $mapRoot -Force)
    Write-FixtureText -Path (Join-Path $fixtureRoot '.gitignore') -Text "/artifacts/`n/map/`n/tests/AIArena.VerificationLab/bin/`n/tests/AIArena.VerificationLab/obj/`n"
    Write-FixtureText -Path (Join-Path $fixtureRoot '.ai-arena-qa-accept-fixture') -Text "fixture-only`n"
    Write-FixtureText -Path (Join-Path $fixtureRoot 'app.txt') -Text "fixture source`n"
    Write-FixtureText -Path (Join-Path $mapRoot 'map.txt') -Text "fixture map source`n"
    Write-FixtureText -Path $verificationProjectPath -Text @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
    Write-RealValidatorProgram -ExitCode 0
    Write-FixtureText -Path $validatorScript -Text @'
$ErrorActionPreference = 'Stop'
$arguments = @($args)
if ($arguments.Count -ne 3 -or $arguments[0] -ne '--validate-evidence-current') {
    exit 7
}
$evidencePath = [string]$arguments[1]
$root = [string]$arguments[2]
$counter = Join-Path $root 'artifacts/validator-calls.log'
$counterDirectory = Split-Path -Parent $counter
if (-not (Test-Path -LiteralPath $counterDirectory -PathType Container)) {
    [void](New-Item -ItemType Directory -Path $counterDirectory -Force)
}
[IO.File]::AppendAllText($counter, "validated`n", [Text.UTF8Encoding]::new($false))
$failCanonical = Join-Path $root 'artifacts/stub-fail-canonical.flag'
if ((Test-Path -LiteralPath $failCanonical -PathType Leaf) -and
    [IO.Path]::GetFileName($evidencePath) -eq 'qa-evidence.json') {
    $value = ([IO.File]::ReadAllText($evidencePath, [Text.Encoding]::UTF8) | ConvertFrom-Json)
    if ([string]$value.verdict -eq 'sealed') {
        exit 9
    }
}
exit 0
'@

    [void](Invoke-FixtureGit -Root $mapRoot -Arguments @('init', '--quiet'))
    [void](Invoke-FixtureGit -Root $mapRoot -Arguments @('config', 'user.name', 'AI Arena QA Fixture'))
    [void](Invoke-FixtureGit -Root $mapRoot -Arguments @('config', 'user.email', 'qa-fixture@example.invalid'))
    [void](Invoke-FixtureGit -Root $mapRoot -Arguments @('add', '--all'))
    [void](Invoke-FixtureGit -Root $mapRoot -Arguments @('commit', '--quiet', '-m', 'fixture map'))

    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('init', '--quiet'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('config', 'user.name', 'AI Arena QA Fixture'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('config', 'user.email', 'qa-fixture@example.invalid'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('add', '--all'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('commit', '--quiet', '-m', 'fixture outer'))

    & dotnet build $verificationProjectPath --configuration Release --nologo --verbosity quiet 1>$null 2>$null
    Require ($LASTEXITCODE -eq 0) 'Fixture stale-validator seed build should succeed.'
    $staleValidatorDll = Join-Path $verificationProjectRoot 'bin/Release/net10.0/AIArena.VerificationLab.dll'
    Require (Test-Path -LiteralPath $staleValidatorDll -PathType Leaf) 'Fixture stale-validator DLL should exist in the ignored conventional output.'
    $ignoredGeneratedTarget = Join-Path $verificationProjectRoot 'obj/AIArena.VerificationLab.csproj.nuget.g.targets'
    Write-FixtureText -Path $ignoredGeneratedTarget -Text @'
<Project>
  <Target Name="PoisonIgnoredRepoObj" BeforeTargets="CoreCompile">
    <WriteLinesToFile File="$(MSBuildProjectDirectory)\..\..\artifacts\repo-obj-poison-consumed.flag" Lines="poisoned" Overwrite="true" />
  </Target>
</Project>
'@

    $unattestedBundle = New-CompletePartialBundle -Name 'direct-attestation-required'
    $unattestedResult = Invoke-Acceptance -Bundle $unattestedBundle -AttestReviewedVisuals:$false
    Assert-BlockedUnchanged -Bundle $unattestedBundle -Result $unattestedResult -ExpectedCode 'qa_accept.attestation_required'

    foreach ($case in @(
        [pscustomobject]@{ Name = 'stale'; Mutation = 'stale-tree'; Code = 'qa_accept.stale_tree' },
        [pscustomobject]@{ Name = 'missing'; Mutation = 'missing-artifact'; Code = 'qa_accept.missing_artifact' },
        [pscustomobject]@{ Name = 'replaced'; Mutation = 'replaced-artifact'; Code = 'qa_accept.replaced_artifact' },
        [pscustomobject]@{ Name = 'no-automation'; Mutation = 'no-automation'; Code = 'qa_accept.visual_manifest' },
        [pscustomobject]@{ Name = 'privacy'; Mutation = 'bad-privacy'; Code = 'qa_accept.privacy' },
        [pscustomobject]@{ Name = 'incomplete'; Mutation = 'incomplete-gate'; Code = 'qa_accept.incomplete_gates' },
        [pscustomobject]@{ Name = 'missing-limitation'; Mutation = 'missing-limitation'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'renamed-limitation'; Mutation = 'renamed-limitation'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-state'; Mutation = 'limitation-state'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-summary'; Mutation = 'limitation-summary'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-evidence-summary'; Mutation = 'limitation-evidence-summary'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-reference'; Mutation = 'limitation-reference'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-text'; Mutation = 'limitation-text'; Code = 'qa_accept.limitations' },
        [pscustomobject]@{ Name = 'limitation-basis'; Mutation = 'limitation-basis'; Code = 'qa_accept.limitations' }
    )) {
        $bundle = New-CompletePartialBundle -Name $case.Name -Mutation $case.Mutation
        $result = Invoke-Acceptance $bundle
        Assert-BlockedUnchanged -Bundle $bundle -Result $result -ExpectedCode $case.Code
    }

    $rollbackBundle = New-CompletePartialBundle -Name 'validator-rollback'
    Write-FixtureText -Path (Join-Path $fixtureRoot 'artifacts/stub-fail-canonical.flag') -Text "fail sealed canonical validation`n"
    $rollbackResult = Invoke-Acceptance $rollbackBundle
    Assert-BlockedUnchanged -Bundle $rollbackBundle -Result $rollbackResult -ExpectedCode 'qa_accept.authoritative_validation'
    Remove-Item -LiteralPath (Join-Path $fixtureRoot 'artifacts/stub-fail-canonical.flag') -Force

    $validBundle = New-CompletePartialBundle -Name 'valid'
    $validBeforeCompletedAt = ([IO.File]::ReadAllText($validBundle.EvidencePath, $utf8) | ConvertFrom-Json).completedAtUtc
    $validResult = Invoke-Acceptance $validBundle
    Require ($validResult.ExitCode -eq 0) "Exact valid acceptance failed: $($validResult.Output)"
    Require ($validResult.Output -eq 'SEALED inspection.user-acceptance') 'Success output should be content-free and deterministic.'
    Require (Test-Path -LiteralPath $validBundle.ReceiptPath -PathType Leaf) 'Valid acceptance should add the inspection receipt.'
    $sealedText = [IO.File]::ReadAllText($validBundle.EvidencePath, $utf8)
    $sealed = $sealedText | ConvertFrom-Json
    $receiptText = [IO.File]::ReadAllText($validBundle.ReceiptPath, $utf8)
    $receipt = $receiptText | ConvertFrom-Json
    Require ($sealed.verdict -eq 'sealed') 'Valid acceptance should seal the contract.'
    Require ($sealed.inspection.userAccepted -eq $true) 'Valid acceptance should record explicit inspection.'
    Require ($sealed.inspection.treeFingerprint -eq $validBundle.TreeFingerprint) 'Inspection should bind the exact composite tree fingerprint.'
    Require ([DateTimeOffset]$sealed.completedAtUtc -gt [DateTimeOffset]$validBeforeCompletedAt) 'Acceptance should recompute completedAtUtc.'
    $inspectionGate = @($sealed.gates | Where-Object { $_.id -eq 'inspection.user-acceptance' })[0]
    Require ($inspectionGate.outcome -eq 'pass' -and $inspectionGate.evidence.state -eq 'observed') 'Acceptance should change only the inspection gate to observed pass evidence.'
    $receiptArtifact = @($sealed.artifacts | Where-Object { $_.id -eq 'artifact.inspection.user-acceptance.receipt' })[0]
    Require ($receiptArtifact.relativePath -eq 'metadata/inspection-receipt.json') 'Receipt manifest path should be bundle-relative.'
    Require ($receiptArtifact.sha256 -eq (Get-Sha256File $validBundle.ReceiptPath)) 'Receipt manifest hash should match the exact receipt bytes.'
    Require ($receipt.treeFingerprint -eq $validBundle.TreeFingerprint) 'Receipt should bind the exact composite tree fingerprint.'
    $directReceiptIsBound = ($receipt.acceptanceMode -eq 'direct-human-attestation' -and $receipt.evidenceSha256 -eq (Get-Sha256Bytes $validBundle.OriginalEvidenceBytes) -and $null -eq $receipt.reviewManifestSha256)
    Require $directReceiptIsBound 'Direct acceptance should record an exact evidence-bound external human-attestation mode without a review manifest.'
    Require ($receipt.attestation -match 'did not display or observe pixels') 'Direct attestation wording must not claim that the script observed pixels.'
    $originalContract = ([Text.Encoding]::UTF8.GetString($validBundle.OriginalEvidenceBytes) | ConvertFrom-Json)
    $requiredLimitationIds = @('limitation.source-boundary-sampling', 'limitation.ui-animation-playback', 'limitation.ui-os-interaction', 'limitation.ui-physical-dpi')
    $limitationWasAcceptedHonestly = (@($sealed.acceptedLimitations).Count -eq $requiredLimitationIds.Count -and
        @($sealed.acceptedLimitations | Where-Object { $_.id -notin $requiredLimitationIds -or -not $_.userAccepted -or $_.evidence.state -ne 'unavailable' }).Count -eq 0 -and
        @($sealed.acceptedLimitations | Where-Object {
            $sealedItem = $_
            $originalItem = @($originalContract.acceptedLimitations | Where-Object { $_.id -eq $sealedItem.id })[0]
            $null -eq $originalItem -or $sealedItem.evidence.limitation -ne $originalItem.evidence.limitation
        }).Count -eq 0)
    Require $limitationWasAcceptedHonestly 'Acceptance should atomically accept listed limitations without upgrading or replacing unavailable evidence.'
    $receiptBoundLimitations = (@($receipt.limitations).Count -eq $requiredLimitationIds.Count -and
        @($receipt.limitations | Where-Object {
            $receiptItem = $_
            $originalItem = @($originalContract.acceptedLimitations | Where-Object { $_.id -eq $receiptItem.id })[0]
            $receiptItem.id -notin $requiredLimitationIds -or
                $receiptItem.evidenceState -ne 'unavailable' -or
                $null -eq $originalItem -or
                $receiptItem.semanticSha256 -ne (Get-LimitationSemanticSha256 $originalItem)
        }).Count -eq 0)
    Require $receiptBoundLimitations 'Receipt should bind every explicitly accepted limitation and preserve its evidence state.'
    $visualManifest = @($sealed.artifacts | Where-Object { $_.kind -in @('rendered-ui-screenshot', 'automation-tree') } | Sort-Object id)
    $receiptVisual = @($receipt.artifacts | Sort-Object id)
    Require ($visualManifest.Count -eq $receiptVisual.Count) 'Receipt should include every inspected screenshot and automation artifact.'
    for ($index = 0; $index -lt $visualManifest.Count; $index++) {
        Require ($receiptVisual[$index].id -eq $visualManifest[$index].id) 'Receipt visual IDs should match the manifest exactly.'
        Require ($receiptVisual[$index].sha256 -eq $visualManifest[$index].sha256) 'Receipt visual hashes should match the manifest exactly.'
    }
    Require ($sealedText -notmatch [regex]::Escape($fixtureRoot)) 'Sealed evidence must not persist the machine-specific fixture path.'
    Require ($receiptText -notmatch [regex]::Escape($fixtureRoot)) 'Inspection receipt must not persist the machine-specific fixture path.'
    $remnants = @(Get-ChildItem -LiteralPath $validBundle.Root -Force -Recurse -File | Where-Object {
        $_.Name -like '.qa-evidence.*' -or $_.Name -like '.inspection-receipt.*' -or $_.Name -eq '.inspection-accept.lock'
    })
    Require ($remnants.Count -eq 0) 'Successful acceptance should not leave temporary, backup, or lock files.'
    $validatorCallCount = @([IO.File]::ReadAllLines((Join-Path $fixtureRoot 'artifacts/validator-calls.log'))).Count
    Require ($validatorCallCount -ge 3) 'Valid acceptance should authoritatively validate original, candidate, and canonical evidence.'

    $incompleteReviewBundle = New-CompletePartialBundle -Name 'in-app-incomplete-review'
    $incompleteReview = Write-InAppReviewManifest -Bundle $incompleteReviewBundle -Mutation 'incomplete'
    $incompleteReviewResult = Invoke-Acceptance -Bundle $incompleteReviewBundle -InAppReview $incompleteReview
    Assert-BlockedUnchanged -Bundle $incompleteReviewBundle -Result $incompleteReviewResult -ExpectedCode 'qa_accept.review_manifest_incomplete'
    Require (Test-Path -LiteralPath $incompleteReview.Path -PathType Leaf) 'A rejected in-app review manifest should remain available for correction or explicit refresh clearing.'

    $wrongReviewBundle = New-CompletePartialBundle -Name 'in-app-wrong-evidence'
    $wrongReview = Write-InAppReviewManifest -Bundle $wrongReviewBundle -Mutation 'wrong-evidence'
    $wrongReviewResult = Invoke-Acceptance -Bundle $wrongReviewBundle -InAppReview $wrongReview
    Assert-BlockedUnchanged -Bundle $wrongReviewBundle -Result $wrongReviewResult -ExpectedCode 'qa_accept.review_manifest_binding'

    $inAppBundle = New-CompletePartialBundle -Name 'in-app-valid'
    $inAppReview = Write-InAppReviewManifest -Bundle $inAppBundle
    $inAppReviewHash = $inAppReview.Sha256
    $inAppResult = Invoke-Acceptance -Bundle $inAppBundle -InAppReview $inAppReview
    Require ($inAppResult.ExitCode -eq 0) "Valid in-app review acceptance failed: $($inAppResult.Output)"
    Require (-not (Test-Path -LiteralPath $inAppReview.Path)) 'Successful in-app acceptance should consume the exact reviewed manifest.'
    $inAppSealed = [IO.File]::ReadAllText($inAppBundle.EvidencePath, $utf8) | ConvertFrom-Json
    $inAppReceipt = [IO.File]::ReadAllText($inAppBundle.ReceiptPath, $utf8) | ConvertFrom-Json
    $inAppReceiptIsBound = ($inAppReceipt.acceptanceMode -eq 'in-app-reviewed-manifest' -and $inAppReceipt.reviewManifestSha256 -eq $inAppReviewHash -and $inAppReceipt.evidenceSha256 -eq (Get-Sha256Bytes $inAppBundle.OriginalEvidenceBytes))
    Require $inAppReceiptIsBound 'In-app receipt should bind the consumed review manifest and exact pre-acceptance evidence bytes.'
    $inAppBoundaryIsHonest = ($inAppReceipt.attestation -match 'did not independently interpret pixels' -and $inAppSealed.acceptedLimitations[0].userAccepted -eq $true -and $inAppSealed.acceptedLimitations[0].evidence.state -eq 'unavailable')
    Require $inAppBoundaryIsHonest 'In-app acceptance wording or limitation evidence overstated the visual proof boundary.'

    # Seeded bin/Release contains the previously compiled permissive validator.
    # Current clean source now rejects every evidence document. Real mode must
    # compile that source into an isolated output rather than trusting the stale
    # ignored DLL.
    Write-RealValidatorProgram -ExitCode 73
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('add', 'tests/AIArena.VerificationLab/Program.cs'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('commit', '--quiet', '-m', 'reject in current validator source'))
    $staleBundle = New-CompletePartialBundle -Name 'real-mode-stale-bin'
    & dotnet $staleValidatorDll --validate-evidence-current $staleBundle.EvidencePath $fixtureRoot 1>$null 2>$null
    Require ($LASTEXITCODE -eq 0) 'The seeded ignored stale validator should demonstrate that it would authorize the fixture.'
    $realValidatorLog = Join-Path $fixtureRoot 'artifacts/real-validator-calls.log'
    Remove-Item -LiteralPath $realValidatorLog -Force
    $staleResult = Invoke-Acceptance -Bundle $staleBundle -UseFixtureValidator:$false
    Assert-BlockedUnchanged -Bundle $staleBundle -Result $staleResult -ExpectedCode 'qa_accept.authoritative_validation'
    Require (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot 'artifacts/repo-obj-poison-consumed.flag'))) 'Fresh acceptance consumed the poisoned ignored repository obj target.'
    $rejectCalls = @([IO.File]::ReadAllLines($realValidatorLog) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Require ($rejectCalls.Count -eq 1) 'Current rejecting validator source should be invoked exactly once.'
    Require (-not [string]::Equals([IO.Path]::GetFullPath($rejectCalls[0]), [IO.Path]::GetFullPath($staleValidatorDll), [StringComparison]::OrdinalIgnoreCase)) 'Real mode must not execute the ignored stale bin DLL.'
    Require (-not (Test-Path -LiteralPath $rejectCalls[0])) 'The isolated validator output should be removed after a blocked acceptance.'

    # A clean permissive current source should be built once into a fresh
    # isolated output and reused for original, candidate, and canonical checks.
    Write-RealValidatorProgram -ExitCode 0
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('add', 'tests/AIArena.VerificationLab/Program.cs'))
    [void](Invoke-FixtureGit -Root $fixtureRoot -Arguments @('commit', '--quiet', '-m', 'allow in current validator source'))
    Remove-Item -LiteralPath $realValidatorLog -Force
    $freshBundle = New-CompletePartialBundle -Name 'real-mode-fresh-output'
    $freshResult = Invoke-Acceptance -Bundle $freshBundle -UseFixtureValidator:$false
    Require ($freshResult.ExitCode -eq 0) "Fresh real-mode acceptance failed: $($freshResult.Output)"
    $freshCalls = @([IO.File]::ReadAllLines($realValidatorLog) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Require ($freshCalls.Count -eq 3) 'Fresh real mode should validate original, candidate, and canonical evidence.'
    $freshLocations = @($freshCalls | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)
    Require ($freshLocations.Count -eq 1) 'One hash-pinned isolated validator build should serve all authoritative checks in one acceptance.'
    Require (-not [string]::Equals($freshLocations[0], [IO.Path]::GetFullPath($staleValidatorDll), [StringComparison]::OrdinalIgnoreCase)) 'Fresh real mode must not execute the ignored conventional output.'
    Require (-not (Test-Path -LiteralPath $freshLocations[0])) 'The isolated validator output should be removed after successful acceptance.'

    $tamperBundle = New-CompletePartialBundle -Name 'real-mode-output-tamper'
    Write-FixtureText -Path (Join-Path $fixtureRoot 'artifacts/tamper-validator-output.flag') -Text "tamper after first invocation`n"
    $tamperResult = Invoke-Acceptance -Bundle $tamperBundle -UseFixtureValidator:$false
    Assert-BlockedUnchanged -Bundle $tamperBundle -Result $tamperResult -ExpectedCode 'qa_accept.validator_tampered'
    Remove-Item -LiteralPath (Join-Path $fixtureRoot 'artifacts/tamper-validator-output.flag') -Force

    # The trusted traversal must include artifacts/qa itself. Otherwise a
    # junction at that base can redirect evidence reads and receipt writes out
    # of the repository while every child path still appears bundle-relative.
    $junctionTarget = Join-Path ([IO.Path]::GetTempPath()) ('ai-arena-qa-accept-junction-target-' + [Guid]::NewGuid().ToString('N'))
    $normalArtifactRoot = $artifactRoot
    $artifactRoot = $junctionTarget
    $outsideBundle = New-CompletePartialBundle -Name 'redirected-base'
    $artifactRoot = $normalArtifactRoot
    $artifactRootFull = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\', '/')
    $fixturePrefix = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Require ($artifactRootFull.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) 'Junction regression cleanup must remain inside the fixture root.'
    Remove-Item -LiteralPath $artifactRootFull -Recurse -Force
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $artifactRootFull) -Force)
    $junctionCreated = $false
    try {
        [void](New-Item -ItemType Junction -Path $artifactRootFull -Target $junctionTarget -ErrorAction Stop)
        $junctionCreated = $true
        $aliasedBundle = [pscustomobject]@{
            Root = Join-Path $artifactRootFull 'redirected-base'
            EvidencePath = Join-Path $artifactRootFull 'redirected-base/qa-evidence.json'
            OriginalEvidenceBytes = $outsideBundle.OriginalEvidenceBytes
            ReceiptPath = Join-Path $artifactRootFull 'redirected-base/metadata/inspection-receipt.json'
            TreeFingerprint = $outsideBundle.TreeFingerprint
        }
        $junctionResult = Invoke-Acceptance $aliasedBundle
        Assert-BlockedUnchanged -Bundle $aliasedBundle -Result $junctionResult -ExpectedCode 'qa_accept.path_escape'
    }
    finally {
        if ($junctionCreated -and (Test-Path -LiteralPath $artifactRootFull)) {
            $junctionItem = Get-Item -LiteralPath $artifactRootFull -Force
            Require (($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'Junction cleanup must target the fixture reparse point itself.'
            [IO.Directory]::Delete($artifactRootFull)
        }
        $targetFull = [IO.Path]::GetFullPath($junctionTarget).TrimEnd('\', '/')
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($targetFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $targetFull)) {
            Remove-Item -LiteralPath $targetFull -Recurse -Force
        }
    }

    Write-Host 'PASS QA post-render inspection acceptance fixtures'
}
catch {
    $testsFailed = $true
    Write-Host "FAIL QA post-render inspection acceptance fixtures: $($_.Exception.Message) [$($_.InvocationInfo.ScriptLineNumber)]"
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolved = [IO.Path]::GetFullPath($fixtureRoot)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
            try { Remove-Item -LiteralPath $resolved -Recurse -Force } catch { }
        }
    }
}

if ($testsFailed) {
    exit 1
}
exit 0
