$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sealScript = Join-Path $repositoryRoot 'scripts\qa-seal.ps1'
$controlClient = Join-Path $repositoryRoot 'scripts\ai-arena-control.ps1'
$matrixHelpers = Join-Path $repositoryRoot 'scripts\qa-ui-matrix.ps1'
$featureMatrixHelpers = Join-Path $repositoryRoot 'scripts\qa-feature-surface-matrix.ps1'
$verificationLab = Join-Path $repositoryRoot 'tests\AIArena.VerificationLab\bin\Release\net10.0\AIArena.VerificationLab.dll'
$runId = 'qa-seal-fixture-' + [Guid]::NewGuid().ToString('N')
$planRunRoot = Join-Path $repositoryRoot ("artifacts\qa\$runId")
$blockedRunId = 'qa-seal-blocked-fixture-' + [Guid]::NewGuid().ToString('N')
$blockedRunRoot = Join-Path $repositoryRoot ("artifacts\qa\$blockedRunId")
$ownedFallbacks = [Collections.Generic.List[object]]::new()
$failed = $false
$previousControlOwner = $env:AI_ARENA_CONTROL_OWNER

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    . $controlClient
    try {
        Remove-Item Env:AI_ARENA_CONTROL_OWNER -ErrorAction SilentlyContinue
        $defaultEndpoint = Get-AIArenaControlEndpoint
        $firstOwner = '11111111111111111111111111111111'
        $secondOwner = '22222222222222222222222222222222'
        $env:AI_ARENA_CONTROL_OWNER = $firstOwner.ToUpperInvariant()
        $firstEndpoint = Get-AIArenaControlEndpoint
        $env:AI_ARENA_CONTROL_OWNER = $secondOwner
        $secondEndpoint = Get-AIArenaControlEndpoint
        Require ([string]$defaultEndpoint.PipeName -ceq 'ai-arena-wpf-control') 'PowerShell control client changed its ordinary default pipe.'
        Require ([string]$firstEndpoint.PipeName -ceq "ai-arena-wpf-control-$firstOwner") 'PowerShell control client did not normalize the first owner pipe.'
        Require ([string]$secondEndpoint.PipeName -ceq "ai-arena-wpf-control-$secondOwner") 'PowerShell control client did not isolate the second owner pipe.'
        Require ([string]$firstEndpoint.TokenPath -cne [string]$secondEndpoint.TokenPath) 'PowerShell control client reused an owner token path.'
        Require ((Split-Path -Leaf ([string]$firstEndpoint.TokenPath)).EndsWith("-$firstOwner.token", [StringComparison]::Ordinal)) 'PowerShell control client did not bind its token path to the normalized owner.'
        $env:AI_ARENA_CONTROL_OWNER = 'invalid-owner'
        $invalidOwnerRejected = $false
        try { $null = Get-AIArenaControlEndpoint }
        catch { $invalidOwnerRejected = $true }
        Require $invalidOwnerRejected 'PowerShell control client accepted an unsafe owner namespace.'
    }
    finally {
        if ($null -eq $previousControlOwner) {
            Remove-Item Env:AI_ARENA_CONTROL_OWNER -ErrorAction SilentlyContinue
        }
        else {
            $env:AI_ARENA_CONTROL_OWNER = $previousControlOwner
        }
    }

    . $matrixHelpers
    $matrixFixture = @(
        foreach ($pass in 1..2) {
            foreach ($theme in @('dark-blue', 'light', 'high-contrast')) {
                foreach ($width in @(960, 1500)) {
                    foreach ($dpi in @('1-0', '1-5', '2-0')) {
                        foreach ($motion in @('normal', 'reduced')) {
                            [pscustomobject]@{ key = "p$($pass.ToString('D2')).$theme.w$width.d$dpi.$motion" }
                        }
                    }
                }
            }
        }
    )
    $firstPassCells = @(Get-AIArenaQaUiMatrixPassCells -Cells $matrixFixture -PassNumber 1)
    $secondPassCells = @(Get-AIArenaQaUiMatrixPassCells -Cells $matrixFixture -PassNumber 2)
    Require ($firstPassCells.Count -eq 36 -and $secondPassCells.Count -eq 36) 'UI matrix helper did not select the exact per-pass cross-product.'
    Require (@($firstPassCells | Where-Object { -not ([string]$_.key).StartsWith('p01.', [StringComparison]::Ordinal) }).Count -eq 0) 'UI matrix helper mixed pass identities.'

. $featureMatrixHelpers

Require ((Get-AIArenaQaMigrationEvidenceId -Schema 'ai_arena.scenario_pack.v1') -ceq 'evidence.schema.migration.ai-arena.scenario-pack.v1') 'scenario migration evidence authority ID drifted'
Require ((Get-AIArenaQaMigrationEvidenceId -Schema 'ai_arena.benchmark_pack.v1') -ceq 'evidence.schema.migration.ai-arena.benchmark-pack.v1') 'benchmark migration evidence authority ID drifted'
    $expectedFeatureKeys = @(
        'matrix',
        'fork',
        'packs',
        'rubrics',
        'claims',
        'context-prompt-inspector',
        'agent-memory-debugger',
        'fault-injection',
        'routing-optimizer',
        'in-app-qa-inspector'
    )
    Require ((@(Get-AIArenaQaFeatureSurfaceKeys) -join ',') -ceq ($expectedFeatureKeys -join ',')) 'Feature-surface helper changed the closed Experiment Lab registry.'
    Require ((Get-AIArenaQaFeatureSurfaceIdentity -FeatureKey 'matrix') -ceq 'MatrixPanel') 'Feature-surface helper did not bind matrix to its visible content identity.'
    Require ((Get-AIArenaQaFeatureSurfaceIdentity -FeatureKey 'in-app-qa-inspector') -ceq 'QaInspectorRoot') 'Feature-surface helper did not bind QA Inspector to its visible content identity.'
    foreach ($ordinaryFeatureKey in @($expectedFeatureKeys | Where-Object { $_ -cne 'in-app-qa-inspector' })) {
        Require ((Get-AIArenaQaFeatureSelectionTimeoutMilliseconds -FeatureKey $ordinaryFeatureKey) -eq 30000) "Ordinary feature selection timeout drifted for $ordinaryFeatureKey."
    }
    Require ((Get-AIArenaQaFeatureSelectionTimeoutMilliseconds -FeatureKey 'in-app-qa-inspector') -eq 90000) 'QA Inspector selection does not have bounded currentness-validation headroom.'
    Require ((Get-AIArenaQaFeatureSelectionTimeoutMilliseconds -FeatureKey 'IN-APP-QA-INSPECTOR') -eq 90000) 'QA Inspector selection timeout changed under PowerShell case-insensitive parameter binding.'
    $unknownFeatureRejected = $false
    try {
        $null = Get-AIArenaQaFeatureSelectionTimeoutMilliseconds -FeatureKey 'unknown-feature'
    }
    catch {
        $unknownFeatureRejected = $true
    }
    Require $unknownFeatureRejected 'Feature selection timeout helper accepted a key outside the closed registry.'
    $featureCells = @(
        foreach ($pass in 1..2) {
            foreach ($theme in @('dark-blue', 'light', 'high-contrast')) {
                foreach ($width in @(960, 1500)) {
                    foreach ($featureKey in $expectedFeatureKeys) {
                        [pscustomobject]@{ key = "p$($pass.ToString('D2')).feature.$featureKey.$theme.w$width" }
                    }
                }
            }
        }
    )
    Require (@(Get-AIArenaQaFeatureSurfacePassCells -Cells $featureCells -PassNumber 1).Count -eq 60) 'Feature-surface helper did not select the exact 60-cell per-pass cross-product.'

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $sealScript,
        [ref]$tokens,
        [ref]$errors)
    Require ($errors.Count -eq 0) 'qa-seal has PowerShell parser errors.'
    $bareElse = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'else'
    }, $true))
    Require ($bareElse.Count -eq 0) 'qa-seal contains an else token parsed as a runtime command.'

    function Import-SealFunction {
        param([Parameter(Mandatory)] [string]$Name)
        $matches = @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
        }, $true))
        Require ($matches.Count -eq 1) "qa-seal is missing blocked-fallback helper $Name."
        $bodyText = $matches[0].Body.Extent.Text
        Set-Item -Path "Function:script:$Name" -Value ([scriptblock]::Create($bodyText.Substring(1, $bodyText.Length - 2)))
    }

    foreach ($helper in @(
        'ConvertTo-SafeRelativePath',
        'Get-Sha256Text',
        'Get-Sha256File',
        'Protect-QaText',
        'Test-QaTextPrivacy',
        'Write-Utf8NoBom',
        'Remove-IsolatedQaControlToken',
        'New-EvidenceAssertion',
        'Resolve-QaPathWithinDirectory',
        'Assert-QaPathHasNoReparsePoint',
        'Get-QaSafeFilesUnderDirectory',
        'Set-QaFileBytesAtomically',
        'Get-QaRejectedClosureArtifacts',
        'Get-QaBlockedFallbackAffectedReferenceIds',
        'Test-QaBlockedFallbackIssueScope',
        'Start-QaRejectedArtifactQuarantine',
        'Undo-QaRejectedArtifactQuarantine',
        'New-QaGeneratedSanitizedArtifact',
        'New-QaBlockedBoundaryArtifacts',
        'New-QaRetainedReferenceArtifacts',
        'Remove-QaGeneratedBlockedFallbackArtifacts',
        'New-QaAuthoritativeRejectionArtifact',
        'ConvertTo-QaBlockedFallbackContract',
        'Assert-QaBlockedBundleInventory',
        'Invoke-QaBlockedFallback')) {
        Import-SealFunction -Name $helper
    }

    $cleanupOwner = [Guid]::NewGuid().ToString('N')
    $savedOwnerForCleanup = $env:AI_ARENA_CONTROL_OWNER
    $cleanupTokenPath = $null
    try {
        $env:AI_ARENA_CONTROL_OWNER = $cleanupOwner
        $cleanupTokenPath = [string](Get-AIArenaControlEndpoint).TokenPath
        if (Test-Path -LiteralPath $cleanupTokenPath) {
            throw 'Fresh QA control-token cleanup fixture unexpectedly already exists.'
        }
        [IO.File]::WriteAllText($cleanupTokenPath, "fixture`n", [Text.UTF8Encoding]::new($false))
        Remove-IsolatedQaControlToken -Path $cleanupTokenPath -OwnerToken $cleanupOwner
        Require (-not (Test-Path -LiteralPath $cleanupTokenPath)) 'QA seal did not remove its exact owned control token.'
        $unownedTokenRejected = $false
        try {
            Remove-IsolatedQaControlToken `
                -Path (Join-Path ([IO.Path]::GetTempPath()) 'ai-arena-wpf-control-unowned.token') `
                -OwnerToken $cleanupOwner
        }
        catch { $unownedTokenRejected = $true }
        Require $unownedTokenRejected 'QA seal control-token cleanup accepted an unowned path.'
    }
    finally {
        if ($null -ne $cleanupTokenPath -and (Test-Path -LiteralPath $cleanupTokenPath)) {
            Remove-Item -LiteralPath $cleanupTokenPath -Force
        }
        if ($null -eq $savedOwnerForCleanup) {
            Remove-Item Env:AI_ARENA_CONTROL_OWNER -ErrorAction SilentlyContinue
        }
        else {
            $env:AI_ARENA_CONTROL_OWNER = $savedOwnerForCleanup
        }
    }

    $nativeResolverAst = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Resolve-NativeCommandPath'
    }, $true))
    Require ($nativeResolverAst.Count -eq 1) 'qa-seal is missing its native-command path resolver.'
    . ([scriptblock]::Create($nativeResolverAst[0].Extent.Text))
    $resolvedNpm = Resolve-NativeCommandPath -Command 'npm.cmd'
    Require ([IO.Path]::IsPathRooted($resolvedNpm)) 'qa-seal left npm.cmd unresolved for ProcessStartInfo.'
    Require (Test-Path -LiteralPath $resolvedNpm -PathType Leaf) 'qa-seal resolved npm.cmd to a missing file.'

    $validatorIssueParserAst = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-SafeValidatorIssueCodes'
    }, $true))
    Require ($validatorIssueParserAst.Count -eq 1) 'qa-seal is missing its safe validator issue-code parser.'
    . ([scriptblock]::Create($validatorIssueParserAst[0].Extent.Text))
    $safeIssueCodes = @(Get-SafeValidatorIssueCodes "FAIL bundle.ui_matrix_theme_render artifact=7`nPRIVATE_SENTINEL")
    Require (($safeIssueCodes -join ',') -eq 'bundle.ui_matrix_theme_render,validator.unrecognized_output') 'qa-seal did not reduce validator output to bounded content-free issue codes.'
    Require (($safeIssueCodes -join ',') -notmatch 'PRIVATE_SENTINEL') 'qa-seal leaked untrusted validator output through diagnostics.'

    $ordinalArrayAst = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-OrdinalStringArray'
    }, $true))
    Require ($ordinalArrayAst.Count -eq 1) 'qa-seal is missing its ordinal evidence-ID ordering helper.'
    . ([scriptblock]::Create($ordinalArrayAst[0].Extent.Text))
    $orderedIds = @(Get-OrdinalStringArray -Values @(
        'artifact.p01.dark-blue.w960.d1-0.normal.screenshot',
        'artifact.p01.dark-blue.w1500.d1-0.normal.screenshot',
        'artifact.p01.dark-blue.w960.d1-0.reduced.screenshot'))
    Require (($orderedIds -join ',') -eq (
        'artifact.p01.dark-blue.w1500.d1-0.normal.screenshot,' +
        'artifact.p01.dark-blue.w960.d1-0.normal.screenshot,' +
        'artifact.p01.dark-blue.w960.d1-0.reduced.screenshot')) 'qa-seal did not order inspection artifact IDs with the schema-required ordinal comparer.'

    $ordinalUniqueAst = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-OrdinalIgnoreCaseUniqueStringArray'
    }, $true))
    Require ($ordinalUniqueAst.Count -eq 1) 'qa-seal is missing its invariant repository-path ordering helper.'
    . ([scriptblock]::Create($ordinalUniqueAst[0].Extent.Text))
    $orderedPaths = @(Get-OrdinalIgnoreCaseUniqueStringArray -Values @(
        'package.json',
        'app/map-model.ts',
        'APP/MAP-MODEL.TS',
        'app/map_model.ts',
        'indexer/Program.cs',
        'app/MapDashboard.tsx',
        'indexer-tests/Program.cs',
        'package-lock.json',
        'package.json'))
    Require (($orderedPaths -join ',') -ceq (
        'app/map-model.ts,' +
        'app/MapDashboard.tsx,' +
        'app/map_model.ts,' +
        'indexer-tests/Program.cs,' +
        'indexer/Program.cs,' +
        'package-lock.json,' +
        'package.json')) 'qa-seal repository fingerprints depend on PowerShell engine or culture sorting.'

    $script:RepositoryRoot = $repositoryRoot
    $script:RunRoot = $blockedRunRoot
    $script:LogsRoot = Join-Path $blockedRunRoot 'logs'
    $script:RejectedArtifactRoot = Join-Path $repositoryRoot 'artifacts\qa-rejected'
    $script:Schema = 'ai_arena.qa_evidence.v1'
    foreach ($directory in @(
        $script:LogsRoot,
        (Join-Path $blockedRunRoot 'screenshots'),
        (Join-Path $blockedRunRoot 'automation'),
        (Join-Path $blockedRunRoot 'metadata'))) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    function New-BlockedFixtureArtifact {
        param([string]$Id, [string]$Kind, [string]$RelativePath, [string]$Text)
        $path = Join-Path $script:RunRoot $RelativePath.Replace('/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Write-Utf8NoBom -Path $path -Text $Text
        return [pscustomobject][ordered]@{
            id = $Id
            kind = $Kind
            relativePath = $RelativePath
            sha256 = Get-Sha256File -Path $path
            provenance = $null
        }
    }
    function New-BlockedFixtureGate {
        param([string]$Id)
        return [pscustomobject][ordered]@{
            id = $Id
            outcome = 'pass'
            required = $true
            durationMilliseconds = 7
            tests = [ordered]@{ passed = 1; failed = 0; skipped = 0; total = 1 }
            evidence = New-EvidenceAssertion -Id "evidence.$Id" -State observed -Summary 'fixture pass' -ReferenceId "artifact.$Id.log"
        }
    }
    function New-BlockedFixtureContract {
        param([object[]]$Artifacts)
        $gateIds = @(
            'pass-01.rendered-ui',
            'ui.feature-surface-matrix',
            'ui.keyboard-automation-matrix',
            'ui.reduced-motion-matrix',
            'ui.theme-contrast-matrix',
            'ui.viewport-dpi-matrix',
            'inspection.user-acceptance',
            'schema.explicit-v0-pack-migration',
            'postflight.evidence-privacy',
            'pass-01.tests-core')
        return [pscustomobject][ordered]@{
            schema = 'ai_arena.qa_evidence.v1'
            id = 'qa.blocked-fixture'
            verdict = 'partial'
            cleanFullPasses = 1
            gates = @($gateIds | ForEach-Object { New-BlockedFixtureGate -Id $_ })
            artifacts = @($Artifacts)
            performance = @([pscustomobject]@{
                id = 'performance.release-app-startup-duration'
                metric = 'release-app-startup-duration'
                value = 10
                unit = 'milliseconds'
                thresholdKind = 'maximum'
                threshold = 1000
                evidence = New-EvidenceAssertion -Id 'evidence.performance.release-app-startup-duration' -State observed -Summary 'fixture measurement' -ReferenceId 'artifact.pass-01.rendered-ui.log'
            })
            schemaChecks = @(
                [pscustomobject]@{
                    id = 'schema.qa-evidence.v1'
                    schema = 'ai_arena.qa_evidence.v1'
                    migratedFromSchema = $null
                    outcome = 'pass'
                    evidence = New-EvidenceAssertion -Id 'evidence.schema.qa-evidence.v1' -State observed -Summary 'fixture schema' -ReferenceId 'artifact.postflight.evidence-privacy.log'
                },
                [pscustomobject]@{
                    id = 'schema.ai-arena.scenario-pack.v1'
                    schema = 'ai_arena.scenario_pack.v1'
                    migratedFromSchema = 'ai_arena.scenario_pack.v0'
                    outcome = 'pass'
                    evidence = New-EvidenceAssertion -Id 'evidence.schema.migration.ai-arena.scenario-pack.v1' -State observed -Summary 'fixture migration' -ReferenceId 'artifact.schema.explicit-v0-pack-migration.log'
                })
            acceptedLimitations = @(
                [pscustomobject]@{
                    id = 'limitation.source-boundary-sampling'; summary = 'source boundary'; userAccepted = $false
                    evidence = New-EvidenceAssertion -Id 'evidence.limitation.source-boundary-sampling' -State unavailable -Summary 'source unavailable' -ReferenceId 'artifact.postflight.source-stability.log' -Limitation 'bounded samples only'
                },
                [pscustomobject]@{
                    id = 'limitation.ui-os-interaction'; summary = 'OS interaction'; userAccepted = $false
                    evidence = New-EvidenceAssertion -Id 'evidence.limitation.ui-os-interaction' -State unavailable -Summary 'OS input unavailable' -ReferenceId 'artifact.ui.keyboard-automation-matrix.log' -Limitation 'in-process focus only'
                },
                [pscustomobject]@{
                    id = 'limitation.ui-physical-dpi'; summary = 'physical DPI'; userAccepted = $false
                    evidence = New-EvidenceAssertion -Id 'evidence.limitation.ui-physical-dpi' -State unavailable -Summary 'physical DPI unavailable' -ReferenceId 'artifact.ui.viewport-dpi-matrix.log' -Limitation 'off-screen density only'
                },
                [pscustomobject]@{
                    id = 'limitation.ui-animation-playback'; summary = 'animation'; userAccepted = $false
                    evidence = New-EvidenceAssertion -Id 'evidence.limitation.ui-animation-playback' -State unavailable -Summary 'animation unavailable' -ReferenceId 'artifact.ui.reduced-motion-matrix.log' -Limitation 'preference plumbing only'
                })
            inspection = [pscustomobject][ordered]@{
                userAccepted = $false
                acceptedAtUtc = $null
                treeFingerprint = $null
                screenshotArtifactIds = @('artifact.visual.screenshot')
                automationArtifactIds = @('artifact.visual.automation')
                evidence = New-EvidenceAssertion -Id 'evidence.inspection' -State unavailable -Summary 'pending' -ReferenceId 'artifact.inspection.user-acceptance.log' -Limitation 'pending'
            }
            evidence = @()
        }
    }
    function Get-BlockedFixtureFileInventory {
        param([Parameter(Mandatory)] [string]$Root)

        [string[]]$entries = @(
            foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
                $relativePath = ConvertTo-SafeRelativePath -Path $file.FullName
                "{0}`t{1}" -f $relativePath, (Get-Sha256File -Path $file.FullName)
            }
        )
        [Array]::Sort($entries, [StringComparer]::Ordinal)
        return $entries -join "`n"
    }
    function Get-BlockedFixtureQuarantineInventory {
        if (-not (Test-Path -LiteralPath $script:RejectedArtifactRoot -PathType Container)) {
            return ''
        }
        [string[]]$entries = @(
            Get-ChildItem -LiteralPath $script:RejectedArtifactRoot -Force |
                ForEach-Object { "{0}`t{1}" -f $_.Name, [int]$_.Attributes }
        )
        [Array]::Sort($entries, [StringComparer]::Ordinal)
        return $entries -join "`n"
    }

    $fixtureArtifacts = [Collections.Generic.List[object]]::new()
    foreach ($gateId in @(
        'pass-01.rendered-ui',
        'ui.feature-surface-matrix',
        'ui.keyboard-automation-matrix',
        'ui.reduced-motion-matrix',
        'ui.theme-contrast-matrix',
        'ui.viewport-dpi-matrix',
        'inspection.user-acceptance',
        'postflight.evidence-privacy',
        'pass-01.tests-core')) {
        $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id "artifact.$gateId.log" -Kind 'sanitized-gate-log' -RelativePath "logs/$gateId.log" -Text "gate=$gateId`n"))
    }
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.schema.explicit-v0-pack-migration.log' -Kind 'sanitized-gate-log' -RelativePath 'logs/schema.explicit-v0-pack-migration.log' -Text "migration=pass`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.non-ui.log' -Kind 'sanitized-gate-log' -RelativePath 'logs/non-ui.log' -Text "nonUi=pass`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.postflight.source-stability.log' -Kind 'sanitized-gate-log' -RelativePath 'logs/postflight.source-stability.log' -Text "sourceStable=pass`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.metadata.source' -Kind 'qa-metadata' -RelativePath 'metadata/source.json' -Text "{`"schema`":`"fixture`"}`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.visual.screenshot' -Kind 'rendered-ui-screenshot' -RelativePath 'screenshots/cell.rendered-ui.png' -Text "fixture-png`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.visual.automation' -Kind 'automation-tree' -RelativePath 'automation/cell.automation.json' -Text "{`"schema`":`"fixture`"}`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.pass-01.ui-matrix' -Kind 'qa-ui-matrix' -RelativePath 'metadata/pass-01.ui-matrix.json' -Text "{`"schema`":`"fixture`"}`n"))
    $fixtureArtifacts.Add((New-BlockedFixtureArtifact -Id 'artifact.pass-01.feature-surface-matrix' -Kind 'qa-feature-surface-matrix' -RelativePath 'metadata/pass-01.feature-surface-matrix.json' -Text "{`"schema`":`"fixture`"}`n"))
    $originalContract = New-BlockedFixtureContract -Artifacts @($fixtureArtifacts)
    $originalContractJson = $originalContract | ConvertTo-Json -Depth 20
    $originalPerformance = @($originalContract.performance)[0]
    $originalPerformanceFields = "{0}|{1}|{2}|{3}|{4}" -f $originalPerformance.metric, $originalPerformance.value, $originalPerformance.unit, $originalPerformance.thresholdKind, $originalPerformance.threshold
    $originalMigrationGateJson = @($originalContract.gates | Where-Object id -eq 'schema.explicit-v0-pack-migration')[0] | ConvertTo-Json -Depth 10 -Compress
    $originalNonUiGateJson = @($originalContract.gates | Where-Object id -eq 'pass-01.tests-core')[0] | ConvertTo-Json -Depth 10 -Compress
    $originalPrivacyGate = @($originalContract.gates | Where-Object id -eq 'postflight.evidence-privacy')[0]
    $originalPrivacyGateFields = "{0}|{1}|{2}|{3}" -f $originalPrivacyGate.outcome, $originalPrivacyGate.required, $originalPrivacyGate.durationMilliseconds, $originalPrivacyGate.tests.total
    $originalMigrationSchemaJson = @($originalContract.schemaChecks | Where-Object schema -eq 'ai_arena.scenario_pack.v1')[0] | ConvertTo-Json -Depth 10 -Compress
    $originalLimitationsJson = $originalContract.acceptedLimitations | ConvertTo-Json -Depth 10 -Compress
    $originalBoundaryHashes = @{}
    foreach ($id in @('artifact.ui.keyboard-automation-matrix.log', 'artifact.ui.reduced-motion-matrix.log', 'artifact.ui.viewport-dpi-matrix.log')) {
        $originalBoundaryHashes[$id] = [string](@($originalContract.artifacts | Where-Object id -eq $id)[0].sha256)
    }
    $originalSourceBoundaryArtifactJson = @($originalContract.artifacts | Where-Object id -eq 'artifact.postflight.source-stability.log')[0] | ConvertTo-Json -Depth 10 -Compress
    $originalAffectedArtifacts = @(
        foreach ($id in @(Get-QaBlockedFallbackAffectedReferenceIds -Contract $originalContract)) {
            $artifact = @($originalContract.artifacts | Where-Object { [string]$_.id -ceq $id })[0]
            [pscustomobject]@{ id = [string]$artifact.id; relativePath = [string]$artifact.relativePath; sha256 = [string]$artifact.sha256 }
        }
    )
    $evidencePathFixture = Join-Path $blockedRunRoot 'qa-evidence.json'
    $originalEvidenceText = "{`"original`":true}`n"
    Write-Utf8NoBom -Path $evidencePathFixture -Text $originalEvidenceText

    # The fallback manifest is a single trusted file identity. An allowed issue
    # code must not make an escaped or reparse-point path eligible for reads or
    # any transaction side effects.
    $trustedPathBaseline = Get-BlockedFixtureFileInventory -Root $blockedRunRoot
    $trustedQuarantineBaseline = Get-BlockedFixtureQuarantineInventory
    $trustedEvidenceWriteTicks = [IO.File]::GetLastWriteTimeUtc($evidencePathFixture).Ticks
    $escapedRoot = Join-Path (Split-Path -Parent $blockedRunRoot) ('qa-seal-escaped-evidence-' + [Guid]::NewGuid().ToString('N'))
    $escapedEvidencePath = Join-Path $escapedRoot 'qa-evidence.json'
    try {
        New-Item -ItemType Directory -Path $escapedRoot | Out-Null
        Write-Utf8NoBom -Path $escapedEvidencePath -Text $originalEvidenceText
        $escapedEvidenceSha = Get-Sha256File -Path $escapedEvidencePath
        $script:escapedEvidenceValidatorInvoked = $false
        $escapedEvidenceRejected = $false
        $escapedEvidenceError = ''
        try {
            [void](Invoke-QaBlockedFallback `
                -Contract $originalContract `
                -EvidencePath $escapedEvidencePath `
                -IssueCodes @('bundle.feature_matrix_feature_render') `
                -ValidateBundle { param([string]$candidatePath) $script:escapedEvidenceValidatorInvoked = $true; return $true })
        }
        catch {
            $escapedEvidenceRejected = $true
            $escapedEvidenceError = $_.Exception.Message
        }
        Require $escapedEvidenceRejected 'Blocked fallback accepted an escaped evidence path for an allowed issue code.'
        Require ($escapedEvidenceError -match 'escaped its trusted directory') 'Escaped evidence path was not rejected by the trusted-root precondition.'
        Require (-not $script:escapedEvidenceValidatorInvoked) 'Escaped evidence path reached blocked-bundle validation.'
        Require ((Get-Sha256File -Path $escapedEvidencePath) -ceq $escapedEvidenceSha) 'Escaped evidence path bytes were mutated.'
        Require ((Get-BlockedFixtureFileInventory -Root $blockedRunRoot) -ceq $trustedPathBaseline) 'Escaped evidence path mutated the trusted run closure.'
        Require ((Get-BlockedFixtureQuarantineInventory) -ceq $trustedQuarantineBaseline) 'Escaped evidence path created or changed quarantine state.'
        Require (($originalContract | ConvertTo-Json -Depth 20) -ceq $originalContractJson) 'Escaped evidence path mutated the rejected candidate.'
        Require ([IO.File]::GetLastWriteTimeUtc($evidencePathFixture).Ticks -eq $trustedEvidenceWriteTicks) 'Escaped evidence path rewrote the trusted manifest.'
    }
    finally {
        if (Test-Path -LiteralPath $escapedEvidencePath -PathType Leaf) { [IO.File]::Delete($escapedEvidencePath) }
        if (Test-Path -LiteralPath $escapedRoot -PathType Container) { [IO.Directory]::Delete($escapedRoot) }
    }

    $reparseEvidenceTarget = Join-Path (Split-Path -Parent $blockedRunRoot) ('qa-seal-reparse-target-' + [Guid]::NewGuid().ToString('N'))
    $reparseEvidenceLink = Join-Path $blockedRunRoot 'evidence-link'
    $reparseEvidencePath = Join-Path $reparseEvidenceLink 'qa-evidence.json'
    try {
        New-Item -ItemType Directory -Path $reparseEvidenceTarget | Out-Null
        $targetEvidencePath = Join-Path $reparseEvidenceTarget 'qa-evidence.json'
        Write-Utf8NoBom -Path $targetEvidencePath -Text $originalEvidenceText
        $targetEvidenceSha = Get-Sha256File -Path $targetEvidencePath
        [void](New-Item -ItemType Junction -Path $reparseEvidenceLink -Target $reparseEvidenceTarget)
        $script:reparseEvidenceValidatorInvoked = $false
        $reparseEvidenceRejected = $false
        $reparseEvidenceError = ''
        try {
            [void](Invoke-QaBlockedFallback `
                -Contract $originalContract `
                -EvidencePath $reparseEvidencePath `
                -IssueCodes @('bundle.feature_matrix_feature_render') `
                -ValidateBundle { param([string]$candidatePath) $script:reparseEvidenceValidatorInvoked = $true; return $true })
        }
        catch {
            $reparseEvidenceRejected = $true
            $reparseEvidenceError = $_.Exception.Message
        }
        Require $reparseEvidenceRejected 'Blocked fallback accepted a reparse-point evidence path for an allowed issue code.'
        Require ($reparseEvidenceError -match 'reparse-point path') 'Reparse evidence path was not rejected by the no-reparse precondition.'
        Require (-not $script:reparseEvidenceValidatorInvoked) 'Reparse evidence path reached blocked-bundle validation.'
        Require ((Get-Sha256File -Path $targetEvidencePath) -ceq $targetEvidenceSha) 'Reparse evidence target bytes were mutated.'
        Require ((Get-BlockedFixtureQuarantineInventory) -ceq $trustedQuarantineBaseline) 'Reparse evidence path created or changed quarantine state.'
        Require (($originalContract | ConvertTo-Json -Depth 20) -ceq $originalContractJson) 'Reparse evidence path mutated the rejected candidate.'
        Require ([IO.File]::GetLastWriteTimeUtc($evidencePathFixture).Ticks -eq $trustedEvidenceWriteTicks) 'Reparse evidence path rewrote the trusted manifest.'
    }
    finally {
        if (Test-Path -LiteralPath $reparseEvidenceLink -PathType Container) { [IO.Directory]::Delete($reparseEvidenceLink) }
        $targetEvidencePath = Join-Path $reparseEvidenceTarget 'qa-evidence.json'
        if (Test-Path -LiteralPath $targetEvidencePath -PathType Leaf) { [IO.File]::Delete($targetEvidencePath) }
        if (Test-Path -LiteralPath $reparseEvidenceTarget -PathType Container) { [IO.Directory]::Delete($reparseEvidenceTarget) }
    }
    Require ((Get-BlockedFixtureFileInventory -Root $blockedRunRoot) -ceq $trustedPathBaseline) 'Reparse evidence-path fixture left generated files in the trusted run.'

    # The rejected physical closure must be exactly the artifact set the
    # candidate declared. A broad directory sweep may not absorb undeclared,
    # potentially private evidence into a quarantine.
    $unmanifestedVisualPath = Join-Path $blockedRunRoot 'screenshots\unmanifested-private.png'
    Write-Utf8NoBom -Path $unmanifestedVisualPath -Text "PRIVATE_UNMANIFESTED_VISUAL`n"
    $unmanifestedBaseline = Get-BlockedFixtureFileInventory -Root $blockedRunRoot
    $unmanifestedQuarantineBaseline = Get-BlockedFixtureQuarantineInventory
    $unmanifestedEvidenceWriteTicks = [IO.File]::GetLastWriteTimeUtc($evidencePathFixture).Ticks
    $script:unmanifestedValidatorInvoked = $false
    $unmanifestedRejected = $false
    $unmanifestedError = ''
    try {
        [void](Invoke-QaBlockedFallback `
            -Contract $originalContract `
            -EvidencePath $evidencePathFixture `
            -IssueCodes @('bundle.feature_matrix_feature_render') `
            -ValidateBundle { param([string]$candidatePath) $script:unmanifestedValidatorInvoked = $true; return $true })
    }
    catch {
        $unmanifestedRejected = $true
        $unmanifestedError = $_.Exception.Message
    }
    Require $unmanifestedRejected 'Blocked fallback accepted an undeclared physical visual artifact.'
    Require ($unmanifestedError -match 'physical closure does not exactly match') 'Undeclared visual artifact did not fail the exact-closure precondition.'
    Require (-not $script:unmanifestedValidatorInvoked) 'Undeclared visual artifact reached blocked-bundle validation.'
    Require ((Get-BlockedFixtureFileInventory -Root $blockedRunRoot) -ceq $unmanifestedBaseline) 'Undeclared visual rejection moved, created, or changed a run artifact.'
    Require ((Get-BlockedFixtureQuarantineInventory) -ceq $unmanifestedQuarantineBaseline) 'Undeclared visual rejection created or changed quarantine state.'
    Require (($originalContract | ConvertTo-Json -Depth 20) -ceq $originalContractJson) 'Undeclared visual rejection mutated the candidate contract.'
    Require ([IO.File]::GetLastWriteTimeUtc($evidencePathFixture).Ticks -eq $unmanifestedEvidenceWriteTicks) 'Undeclared visual rejection rewrote the manifest.'
    [IO.File]::Delete($unmanifestedVisualPath)
    Require ((Get-BlockedFixtureFileInventory -Root $blockedRunRoot) -ceq $trustedPathBaseline) 'Undeclared visual fixture left generated files in the trusted run.'

    $escapeRejected = $false
    try { [void](Resolve-QaPathWithinDirectory -Path (Join-Path $blockedRunRoot '..\escaped') -Directory $blockedRunRoot) }
    catch { $escapeRejected = $true }
    Require $escapeRejected 'Blocked fallback accepted a path outside its trusted run root.'

    foreach ($deniedIssueSet in @(
        @('current.tree_fingerprint'),
        @('bundle.feature_matrix_feature_render', 'current.tree_fingerprint'),
        @('bundle.feature_matrix_feature_render', 'bundle.privacy'),
        @('bundle.feature_matrix_schema'),
        @('bundle.feature_matrix_png'))) {
        $beforeRejectedCount = if (Test-Path -LiteralPath $script:RejectedArtifactRoot) {
            @(Get-ChildItem -LiteralPath $script:RejectedArtifactRoot -Force).Count
        }
        else { 0 }
        $denied = $false
        try {
            [void](Invoke-QaBlockedFallback -Contract $originalContract -EvidencePath $evidencePathFixture -IssueCodes $deniedIssueSet -ValidateBundle { param([string]$candidatePath) return $true })
        }
        catch { $denied = $true }
        Require $denied "Blocked fallback accepted out-of-scope validator issues: $($deniedIssueSet -join ',')"
        Require (-not (Test-Path -LiteralPath $evidencePathFixture)) 'Out-of-scope blocked fallback retained an invalid QA manifest.'
        Require (($originalContract | ConvertTo-Json -Depth 20) -ceq $originalContractJson) 'Out-of-scope blocked fallback mutated the rejected candidate.'
        $afterRejectedCount = if (Test-Path -LiteralPath $script:RejectedArtifactRoot) {
            @(Get-ChildItem -LiteralPath $script:RejectedArtifactRoot -Force).Count
        }
        else { 0 }
        Require ($afterRejectedCount -eq $beforeRejectedCount) 'Out-of-scope blocked fallback created a quarantine.'
        foreach ($artifact in $originalAffectedArtifacts) {
            Require (Test-Path -LiteralPath (Join-Path $blockedRunRoot ([string]$artifact.relativePath)) -PathType Leaf) 'Out-of-scope blocked fallback moved original capture evidence.'
        }
        Write-Utf8NoBom -Path $evidencePathFixture -Text $originalEvidenceText
    }

    $reparseSupported = $false
    $reparseTarget = Join-Path $blockedRunRoot 'reparse-target'
    $reparseLink = Join-Path $blockedRunRoot 'automation\reparse-link'
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    Write-Utf8NoBom -Path (Join-Path $reparseTarget 'sentinel.txt') -Text "sentinel`n"
    try {
        if ($null -eq [IO.Directory].GetMethod('CreateSymbolicLink', [type[]]@([string], [string]))) {
            throw [PlatformNotSupportedException]::new('Directory symbolic links are unavailable on this runtime.')
        }
        [void][IO.Directory]::CreateSymbolicLink($reparseLink, $reparseTarget)
        $reparseSupported = $true
        $reparseRejected = $false
        try { [void](Start-QaRejectedArtifactQuarantine -Contract $originalContract) }
        catch { $reparseRejected = $true }
        Require $reparseRejected 'Blocked fallback followed a reparse point in the rejected UI closure.'
        Require (Test-Path -LiteralPath (Join-Path $reparseTarget 'sentinel.txt') -PathType Leaf) 'Reparse rejection damaged its target.'
    }
    catch [UnauthorizedAccessException] { }
    catch [PlatformNotSupportedException] { }
    finally {
        if ($reparseSupported -and (Test-Path -LiteralPath $reparseLink)) { [IO.Directory]::Delete($reparseLink) }
        if (Test-Path -LiteralPath (Join-Path $reparseTarget 'sentinel.txt')) { [IO.File]::Delete((Join-Path $reparseTarget 'sentinel.txt')) }
        if (Test-Path -LiteralPath $reparseTarget) { [IO.Directory]::Delete($reparseTarget) }
    }

    $blockedResult = Invoke-QaBlockedFallback `
        -Contract $originalContract `
        -EvidencePath $evidencePathFixture `
        -IssueCodes @('bundle.feature_matrix_theme_render', 'bundle.feature_matrix_feature_render', 'bundle.feature_matrix_feature_render') `
        -ValidateBundle {
            param([string]$candidatePath)
            $candidate = (Get-Content -LiteralPath $candidatePath -Raw) | ConvertFrom-Json
            return [string]$candidate.verdict -ceq 'blocked' -and
                [int]$candidate.cleanFullPasses -eq 0 -and
                @($candidate.artifacts | Where-Object { [string]$_.kind -cin @('automation-tree', 'rendered-ui-screenshot', 'qa-ui-matrix', 'qa-feature-surface-matrix') }).Count -eq 0
        }
    $ownedFallbacks.Add($blockedResult)
    Require (($originalContract | ConvertTo-Json -Depth 20) -ceq $originalContractJson) 'Blocked fallback mutated the rejected candidate object on its success path.'
    $blocked = $blockedResult.Contract
    Require ([string]$blocked.verdict -ceq 'blocked' -and [int]$blocked.cleanFullPasses -eq 0) 'Blocked fallback did not reset verdict and clean-pass authority.'
    $rejectionArtifact = @($blocked.artifacts | Where-Object id -eq 'artifact.postflight.authoritative-rejection.log')
    Require ($rejectionArtifact.Count -eq 1) 'Blocked fallback did not emit exactly one rejection log artifact.'
    $rejectionText = Get-Content -LiteralPath (Join-Path $blockedRunRoot $rejectionArtifact[0].relativePath) -Raw
    Require ($rejectionText -match 'issue\.00=bundle\.feature_matrix_feature_render' -and $rejectionText -match 'issue\.01=bundle\.feature_matrix_theme_render') 'Blocked fallback rejection codes are not bounded and ordinal-sorted.'
    Require (Test-QaTextPrivacy -Text $rejectionText) 'Blocked fallback persisted unsafe validator output.'
    $failedUiIds = @('pass-01.rendered-ui', 'ui.feature-surface-matrix', 'ui.keyboard-automation-matrix', 'ui.reduced-motion-matrix', 'ui.theme-contrast-matrix', 'ui.viewport-dpi-matrix')
    foreach ($gateId in $failedUiIds) {
        $gate = @($blocked.gates | Where-Object { [string]$_.id -ceq $gateId })
        Require ($gate.Count -eq 1 -and [string]$gate[0].outcome -ceq 'fail' -and [string]$gate[0].evidence.referenceId -ceq $rejectionArtifact[0].id) "Blocked fallback did not fail and bind $gateId to the rejection log."
    }
    $blockedQaSchema = @($blocked.schemaChecks | Where-Object schema -eq 'ai_arena.qa_evidence.v1')[0]
    Require ([string]$blockedQaSchema.outcome -ceq 'fail' -and [string]$blockedQaSchema.evidence.referenceId -ceq $rejectionArtifact[0].id) 'Blocked fallback QA schema did not bind to the rejection log.'
    Require (@($blocked.inspection.screenshotArtifactIds).Count -eq 0 -and @($blocked.inspection.automationArtifactIds).Count -eq 0) 'Blocked fallback retained inspection closure.'
    Require (@(Get-QaRejectedClosureArtifacts -Contract $blocked).Count -eq 0) 'Blocked fallback retained visual or matrix artifacts.'
    $blockedPerformance = @($blocked.performance)[0]
    $blockedPerformanceFields = "{0}|{1}|{2}|{3}|{4}" -f $blockedPerformance.metric, $blockedPerformance.value, $blockedPerformance.unit, $blockedPerformance.thresholdKind, $blockedPerformance.threshold
    Require ($blockedPerformanceFields -ceq $originalPerformanceFields) 'Blocked fallback changed the measured startup performance value or threshold.'
    Require ([string]$blockedPerformance.evidence.referenceId -cmatch '^artifact\.blocked-retained\.performance\.' -and
        @($blocked.artifacts | Where-Object id -eq $blockedPerformance.evidence.referenceId).Count -eq 1) 'Blocked fallback did not rebind startup performance to a narrow retained-evidence artifact.'
    Require ((@($blocked.gates | Where-Object id -eq 'schema.explicit-v0-pack-migration')[0] | ConvertTo-Json -Depth 10 -Compress) -ceq $originalMigrationGateJson) 'Blocked fallback changed migration gate authority.'
    Require ((@($blocked.gates | Where-Object id -eq 'pass-01.tests-core')[0] | ConvertTo-Json -Depth 10 -Compress) -ceq $originalNonUiGateJson) 'Blocked fallback changed a non-UI gate.'
    $blockedPrivacyGate = @($blocked.gates | Where-Object id -eq 'postflight.evidence-privacy')[0]
    $blockedPrivacyGateFields = "{0}|{1}|{2}|{3}" -f $blockedPrivacyGate.outcome, $blockedPrivacyGate.required, $blockedPrivacyGate.durationMilliseconds, $blockedPrivacyGate.tests.total
    Require ($blockedPrivacyGateFields -ceq $originalPrivacyGateFields -and
        [string]$blockedPrivacyGate.evidence.referenceId -cmatch '^artifact\.blocked-retained\.gate\.') 'Blocked fallback did not preserve and narrowly rebind the evidence-privacy gate.'
    Require ((@($blocked.schemaChecks | Where-Object schema -eq 'ai_arena.scenario_pack.v1')[0] | ConvertTo-Json -Depth 10 -Compress) -ceq $originalMigrationSchemaJson) 'Blocked fallback changed migration schema authority.'
    Require (($blocked.acceptedLimitations | ConvertTo-Json -Depth 10 -Compress) -ceq $originalLimitationsJson) 'Blocked fallback changed frozen limitations.'
    Require ((@($blocked.artifacts | Where-Object id -eq 'artifact.postflight.source-stability.log')[0] | ConvertTo-Json -Depth 10 -Compress) -ceq $originalSourceBoundaryArtifactJson) 'Blocked fallback changed the source-boundary log.'
    foreach ($id in @(
        'artifact.pass-01.rendered-ui.log',
        'artifact.ui.feature-surface-matrix.log',
        'artifact.ui.theme-contrast-matrix.log',
        'artifact.inspection.user-acceptance.log',
        'artifact.postflight.evidence-privacy.log')) {
        Require (@($blocked.artifacts | Where-Object { [string]$_.id -ceq $id }).Count -eq 0) "Blocked fallback retained affected original log ID $id."
    }
    foreach ($id in @('artifact.ui.keyboard-automation-matrix.log', 'artifact.ui.reduced-motion-matrix.log', 'artifact.ui.viewport-dpi-matrix.log')) {
        $boundaryArtifact = @($blocked.artifacts | Where-Object id -eq $id)
        Require ($boundaryArtifact.Count -eq 1 -and [string]$boundaryArtifact[0].sha256 -cne [string]$originalBoundaryHashes[$id]) "Blocked fallback did not replace original UI pass bytes for $id."
        $boundaryText = Get-Content -LiteralPath (Join-Path $blockedRunRoot $boundaryArtifact[0].relativePath) -Raw
        Require ($boundaryText -match 'schema=ai_arena\.qa_blocked_boundary\.v1' -and $boundaryText -match 'state=unavailable' -and $boundaryText -notmatch 'outcome=pass') "Blocked fallback boundary artifact is not content-free unavailable evidence: $id"
    }
    foreach ($relativePath in @('screenshots/cell.rendered-ui.png', 'automation/cell.automation.json', 'metadata/pass-01.ui-matrix.json', 'metadata/pass-01.feature-surface-matrix.json')) {
        Require (-not (Test-Path -LiteralPath (Join-Path $blockedRunRoot $relativePath))) "Blocked fallback left rejected closure in the bundle: $relativePath"
        Require (Test-Path -LiteralPath (Join-Path $blockedResult.Quarantine.FinalRoot ('closure/' + $relativePath)) -PathType Leaf) "Blocked fallback did not quarantine rejected closure: $relativePath"
    }
    Assert-QaBlockedBundleInventory -Contract $blocked -EvidencePath $evidencePathFixture
    foreach ($relativePath in @(
        'logs/pass-01.rendered-ui.log',
        'logs/ui.feature-surface-matrix.log',
        'logs/ui.theme-contrast-matrix.log',
        'logs/inspection.user-acceptance.log',
        'logs/postflight.evidence-privacy.log')) {
        Require (-not (Test-Path -LiteralPath (Join-Path $blockedRunRoot $relativePath))) "Blocked fallback retained an affected original gate/schema/inspection log: $relativePath"
        Require (Test-Path -LiteralPath (Join-Path $blockedResult.Quarantine.FinalRoot ('closure/' + $relativePath)) -PathType Leaf) "Blocked fallback did not quarantine affected original bytes: $relativePath"
    }
    Remove-QaGeneratedBlockedFallbackArtifacts -Artifacts $blockedResult.GeneratedArtifacts
    Undo-QaRejectedArtifactQuarantine -Quarantine $blockedResult.Quarantine
    [void]$ownedFallbacks.Remove($blockedResult)
    foreach ($relativePath in @('screenshots/cell.rendered-ui.png', 'automation/cell.automation.json', 'metadata/pass-01.ui-matrix.json', 'metadata/pass-01.feature-surface-matrix.json')) {
        Require (Test-Path -LiteralPath (Join-Path $blockedRunRoot $relativePath) -PathType Leaf) "Rejected-closure rollback did not restore $relativePath"
    }
    Write-Utf8NoBom -Path $evidencePathFixture -Text $originalEvidenceText

    $rollbackContract = $originalContractJson | ConvertFrom-Json
    $rollbackCandidateJson = $rollbackContract | ConvertTo-Json -Depth 20
    $rollbackFailedClosed = $false
    try {
        [void](Invoke-QaBlockedFallback -Contract $rollbackContract -EvidencePath $evidencePathFixture -IssueCodes @('bundle.feature_matrix_feature_render') -ValidateBundle { param([string]$candidatePath) return $false })
    }
    catch { $rollbackFailedClosed = $true }
    Require $rollbackFailedClosed 'Blocked fallback accepted a rejected rebuilt contract.'
    Require (($rollbackContract | ConvertTo-Json -Depth 20) -ceq $rollbackCandidateJson) 'Blocked fallback mutated the rejected candidate object on its rollback path.'
    Require ((Get-Content -LiteralPath $evidencePathFixture -Raw) -ceq $originalEvidenceText) 'Blocked fallback validation failure did not atomically restore the original contract.'
    Require (-not (Test-Path -LiteralPath (Join-Path $blockedRunRoot 'logs/postflight.authoritative-rejection.log'))) 'Blocked fallback validation failure left its rejection log behind.'
    foreach ($relativePath in @('screenshots/cell.rendered-ui.png', 'automation/cell.automation.json', 'metadata/pass-01.ui-matrix.json', 'metadata/pass-01.feature-surface-matrix.json')) {
        Require (Test-Path -LiteralPath (Join-Path $blockedRunRoot $relativePath) -PathType Leaf) "Blocked fallback validation failure did not roll back $relativePath"
    }
    foreach ($artifact in $originalAffectedArtifacts) {
        $restoredPath = Join-Path $blockedRunRoot ([string]$artifact.relativePath)
        Require (Test-Path -LiteralPath $restoredPath -PathType Leaf) "Blocked fallback rollback did not restore affected artifact $($artifact.id)."
        Require ((Get-Sha256File -Path $restoredPath) -ceq [string]$artifact.sha256) "Blocked fallback rollback changed original bytes for $($artifact.id)."
    }

    foreach ($focusHelperName in @(
        'Test-AIArenaQaSafeAutomationValue',
        'Test-AIArenaQaSameFocus',
        'Test-AIArenaQaFocusStep',
        'Test-AIArenaQaFocusCycle')) {
        $focusHelperAst = @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $focusHelperName
        }, $true))
        Require ($focusHelperAst.Count -eq 1) "qa-seal is missing focus helper $focusHelperName."
        . ([scriptblock]::Create($focusHelperAst[0].Extent.Text))
    }
    function New-FocusStep {
        param(
            [string]$Direction,
            [string]$Before,
            [string]$After,
            [string]$BeforeType = 'Button',
            [string]$AfterType = 'Button')
        return [pscustomobject]@{
            direction = $Direction
            beforeIdentity = $Before
            beforeControlType = $BeforeType
            afterIdentity = $After
            afterControlType = $AfterType
            moved = $true
            focusChanged = $true
        }
    }
    $validNext = New-FocusStep next 'HeaderSite#0037' 'HeaderSite#0329'
    $validPrevious = New-FocusStep previous 'HeaderSite#0329' 'HeaderSite#0037'
    $validCapture = New-FocusStep next 'HeaderSite#0037' 'HeaderSite#0329'
    Require (Test-AIArenaQaFocusCycle $validNext $validPrevious $validCapture) 'qa-seal rejected a valid identity-and-control-type focus round trip.'
    $aliasedNext = New-FocusStep next 'HeaderSite' 'HeaderSite'
    Require (-not (Test-AIArenaQaFocusCycle $aliasedNext $aliasedNext $aliasedNext)) 'qa-seal accepted moved steps that aliased distinct controls to one identity.'
    $wrongDirection = New-FocusStep previous 'HeaderSite#0037' 'HeaderSite#0329'
    Require (-not (Test-AIArenaQaFocusCycle $wrongDirection $validPrevious $validCapture)) 'qa-seal accepted the wrong measured focus direction.'
    $brokenType = New-FocusStep previous 'HeaderSite#0329' 'HeaderSite#0037' 'CheckBox' 'Button'
    Require (-not (Test-AIArenaQaFocusCycle $validNext $brokenType $validCapture)) 'qa-seal accepted a focus edge whose control type did not close.'
    $parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    Require ('UserInspectionAccepted' -notin $parameterNames) 'qa-seal still permits pre-render inspection acceptance.'
    $sealSource = Get-Content -LiteralPath $sealScript -Raw
    Require ($sealSource.IndexOf('$resolvedFilePath = Resolve-NativeCommandPath -Command $FilePath', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not use the resolved native-command path.'
    Require ($sealSource.IndexOf('-CaptureResult', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not capture authoritative validator results safely.'
    Require ($sealSource.IndexOf('$paths = @(Get-OrdinalIgnoreCaseUniqueStringArray', [StringComparison]::Ordinal) -ge 0) 'qa-seal source fingerprints do not consume the invariant repository-path ordering helper.'
    Require ($sealSource.IndexOf("'-c', 'core.quotepath=false', 'ls-files'", [StringComparison]::Ordinal) -ge 0) 'qa-seal source fingerprints do not disable Git path quoting consistently.'
    Require ($sealSource.IndexOf('$screenshotArtifactIds = @(Get-OrdinalStringArray', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not ordinal-sort screenshot inspection IDs.'
    Require ($sealSource.IndexOf('$automationArtifactIds = @(Get-OrdinalStringArray', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not ordinal-sort automation inspection IDs.'
    foreach ($requiredMatrixToken in @(
        "@('dark-blue', 'light', 'high-contrast')",
        'Width = 960',
        'Height = 640',
        'Width = 1500',
        'Height = 960',
        "Label = '1-0'",
        "Label = '1-5'",
        "Label = '2-0'",
        "@('normal', 'reduced')",
        '$seedFocus = Move-AIArenaQAFocus -Direction next',
        'Move-AIArenaQAFocus -Direction next',
        'Move-AIArenaQAFocus -Direction previous',
        '$captureFocus = Move-AIArenaQAFocus -Direction next',
        'Test-AIArenaQaFocusCycle',
        'Test-AIArenaQaFocusStep -Step $seedFocus.data -Direction next',
        '$seedFocus.data.afterIdentity',
        '$nextFocus.data.beforeIdentity',
        '$env:AI_ARENA_CONTROL_OWNER = $ownerToken',
        'Remove-IsolatedQaControlToken',
        'renderFailureStage=',
        'controlIsolation=per-run AI_ARENA_CONTROL_OWNER pipe and token namespace',
        'Save-AIArenaUIStructure',
        'Save-AIArenaScreenshot',
        "expectedStateSource -ne 'observed-visible-roots'",
        "observedSurfaceState -ne 'arena-empty'",
        'Assert-SourceFingerprintUnchanged',
        '-AttestReviewedVisuals',
        'Get-AIArenaExperiment',
        'Select-AIArenaExperimentFeature',
        'Get-AIArenaQaFeatureSelectionTimeoutMilliseconds',
        '-TimeoutMs $selectionTimeoutMilliseconds',
        'Set-AIArenaQAFeatureFocus',
        '$featureFocus.data.afterIdentity',
        'featureFailureStage=',
        'Get-AIArenaQaFeatureSurfaceIdentity',
        '$featureRootIsWithinExperiment = Test-AIArenaQaRenderedDescendant',
        "-AncestorIdentity 'ExperimentLabPanel'",
        '-RequireFocusable',
        "'ai_arena.qa_feature_surface_matrix.v1'",
        "'qa-feature-surface-matrix'",
        '$passCells.Count -ne 60',
        'experiment-lab.feature-$featureKey.closed',
        "'schema.explicit-v0-pack-migration'",
        "'experiment pack store is strict versioned and diagnostic'",
        "migratedFromSchema = `$migratedFromSchema"
    )) {
        Require ($sealSource.IndexOf($requiredMatrixToken, [StringComparison]::Ordinal) -ge 0) "qa-seal is missing matrix behavior: $requiredMatrixToken"
    }

    $engine = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-NonInteractive')
    if ([IO.Path]::GetFileNameWithoutExtension($engine) -ieq 'powershell') {
        $arguments += @('-ExecutionPolicy', 'Bypass')
    }
    $arguments += @('-File', $sealScript, '-PlanOnly', '-AllowPartial', '-RunId', $runId)
    $output = @(& $engine @arguments 2>&1)
    Require ($LASTEXITCODE -eq 0) 'qa-seal plan-only execution failed.'

    $evidencePath = Join-Path $planRunRoot 'qa-evidence.json'
    Require (Test-Path -LiteralPath $evidencePath -PathType Leaf) 'qa-seal did not write plan evidence.'
    Require ((Get-Item -LiteralPath $evidencePath).Length -le 4MB) 'qa-seal evidence exceeded its bound.'
    $json = Get-Content -LiteralPath $evidencePath -Raw
    Require ($json -notmatch '(?i)(?:[a-z]:[\\/]|/(?:users|home|root|tmp|private)/)') 'qa-seal evidence contains an absolute private path.'
    $contract = $json | ConvertFrom-Json
    Require ($contract.schema -eq 'ai_arena.qa_evidence.v1') 'qa-seal emitted the wrong schema.'
    Require ($contract.sealManifestId -eq 'ai_arena.qa_seal_manifest.v2') 'qa-seal did not emit the current v2 authority.'
    Require ($contract.verdict -ne 'sealed') 'qa-seal directly sealed pre-inspection evidence.'
    Require ($contract.inspection.userAccepted -eq $false) 'qa-seal preaccepted inspection.'
    Require ($null -eq $contract.inspection.acceptedAtUtc -and $null -eq $contract.inspection.treeFingerprint) 'qa-seal wrote premature inspection identity.'
    Require (@($contract.schemaChecks).Count -eq 12) 'qa-seal did not report every frozen v1 schema.'
    Require (@($contract.gates | Where-Object { $_.id -eq 'inspection.user-acceptance' -and $_.outcome -eq 'partial' }).Count -eq 1) 'qa-seal inspection gate is not pending.'
    $expectedLimitations = @(
        'limitation.source-boundary-sampling',
        'limitation.ui-animation-playback',
        'limitation.ui-os-interaction',
        'limitation.ui-physical-dpi'
    )
    Require ((@($contract.acceptedLimitations | Sort-Object id | ForEach-Object { [string]$_.id }) -join ',') -eq ($expectedLimitations -join ',')) 'qa-seal did not emit the exact frozen evidence limitations.'
    Require (@($contract.acceptedLimitations | Where-Object { [bool]$_.userAccepted -or [string]$_.evidence.state -ne 'unavailable' }).Count -eq 0) 'qa-seal prematurely accepted or overstated an unavailable evidence boundary.'
    foreach ($matrixGate in @('ui.feature-surface-matrix', 'ui.theme-contrast-matrix', 'ui.viewport-dpi-matrix', 'ui.keyboard-automation-matrix', 'ui.reduced-motion-matrix')) {
        Require (@($contract.gates | Where-Object { $_.id -eq $matrixGate -and $_.outcome -eq 'partial' }).Count -eq 1) "qa-seal plan evidence omitted required matrix gate $matrixGate."
    }
    Require (@($contract.gates | Where-Object { $_.id -eq 'schema.explicit-v0-pack-migration' -and $_.outcome -eq 'partial' }).Count -eq 1) 'qa-seal plan evidence omitted the explicit v0 migration fixture gate.'

    Require (Test-Path -LiteralPath $verificationLab -PathType Leaf) 'Release VerificationLab binary is unavailable.'
    & dotnet $verificationLab --validate-evidence $evidencePath | Out-Null
    Require ($LASTEXITCODE -eq 0) 'VerificationLab rejected plan-only QA evidence.'

    $script:RunRoot = $planRunRoot
    $script:LogsRoot = Join-Path $planRunRoot 'logs'
    $script:RejectedArtifactRoot = Join-Path $repositoryRoot 'artifacts\qa-rejected'
    $script:Schema = 'ai_arena.qa_evidence.v1'
    $ordinaryBlocked = Invoke-QaBlockedFallback `
        -Contract $contract `
        -EvidencePath $evidencePath `
        -IssueCodes @('bundle.feature_matrix_theme_render') `
        -ValidateBundle {
            param([string]$candidatePath)
            $validatorOutput = @(& dotnet $verificationLab --validate-evidence $candidatePath 2>&1)
            return $LASTEXITCODE -eq 0
        }
    $ownedFallbacks.Add($ordinaryBlocked)
    Require ([string]$ordinaryBlocked.Contract.verdict -ceq 'blocked') 'Ordinary BundleOnly validation did not accept the rebuilt blocked fixture.'
    Remove-QaGeneratedBlockedFallbackArtifacts -Artifacts $ordinaryBlocked.GeneratedArtifacts
    Undo-QaRejectedArtifactQuarantine -Quarantine $ordinaryBlocked.Quarantine
    [void]$ownedFallbacks.Remove($ordinaryBlocked)
    Write-Host 'PASS QA seal orchestration fixtures'
}
catch {
    $failed = $true
    Write-Host "FAIL QA seal orchestration fixtures: $($_.Exception.Message)"
}
finally {
    if ($null -eq $previousControlOwner) {
        Remove-Item Env:AI_ARENA_CONTROL_OWNER -ErrorAction SilentlyContinue
    }
    else {
        $env:AI_ARENA_CONTROL_OWNER = $previousControlOwner
    }
    foreach ($fallback in @($ownedFallbacks)) {
        if (Test-Path -LiteralPath $fallback.Quarantine.FinalRoot -PathType Container) {
            try {
                $script:RunRoot = [string]$fallback.Quarantine.RunRoot
                Remove-QaGeneratedBlockedFallbackArtifacts -Artifacts $fallback.GeneratedArtifacts
                Undo-QaRejectedArtifactQuarantine -Quarantine $fallback.Quarantine
            }
            catch { }
        }
    }
    foreach ($ownedRunRoot in @($planRunRoot, $blockedRunRoot)) {
        if (Test-Path -LiteralPath $ownedRunRoot) {
            $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\qa')).TrimEnd('\', '/')
            $resolved = [IO.Path]::GetFullPath($ownedRunRoot)
            $prefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
            $leaf = Split-Path -Leaf $resolved
            $insideArtifactRoot = $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
            $ownedFixture = $leaf.StartsWith('qa-seal-fixture-', [StringComparison]::Ordinal) -or
                $leaf.StartsWith('qa-seal-blocked-fixture-', [StringComparison]::Ordinal)
            if ($insideArtifactRoot -and $ownedFixture) {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
}

if ($failed) { exit 1 }
exit 0
