$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sealScript = Join-Path $repositoryRoot 'scripts\qa-seal.ps1'
$matrixHelpers = Join-Path $repositoryRoot 'scripts\qa-ui-matrix.ps1'
$verificationLab = Join-Path $repositoryRoot 'tests\AIArena.VerificationLab\bin\Release\net10.0\AIArena.VerificationLab.dll'
$runId = 'qa-seal-fixture-' + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repositoryRoot ("artifacts\qa\$runId")
$failed = $false

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
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
    $parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    Require ('UserInspectionAccepted' -notin $parameterNames) 'qa-seal still permits pre-render inspection acceptance.'
    $sealSource = Get-Content -LiteralPath $sealScript -Raw
    Require ($sealSource.IndexOf('$resolvedFilePath = Resolve-NativeCommandPath -Command $FilePath', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not use the resolved native-command path.'
    Require ($sealSource.IndexOf('-CaptureResult', [StringComparison]::Ordinal) -ge 0) 'qa-seal does not capture authoritative validator results safely.'
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
        'Move-AIArenaQAFocus -Direction next',
        'Move-AIArenaQAFocus -Direction previous',
        '$captureFocus = Move-AIArenaQAFocus -Direction next',
        '[bool]$_.focusCapture.moved',
        '[string]$_.focusCapture.afterIdentity -ne ''none''',
        'Save-AIArenaUIStructure',
        'Save-AIArenaScreenshot',
        "expectedStateSource -ne 'observed-visible-roots'",
        "observedSurfaceState -ne 'arena-empty'",
        'Assert-SourceFingerprintUnchanged',
        '-AttestReviewedVisuals'
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

    $evidencePath = Join-Path $runRoot 'qa-evidence.json'
    Require (Test-Path -LiteralPath $evidencePath -PathType Leaf) 'qa-seal did not write plan evidence.'
    Require ((Get-Item -LiteralPath $evidencePath).Length -le 4MB) 'qa-seal evidence exceeded its bound.'
    $json = Get-Content -LiteralPath $evidencePath -Raw
    Require ($json -notmatch '(?i)(?:[a-z]:[\\/]|/(?:users|home|root|tmp|private)/)') 'qa-seal evidence contains an absolute private path.'
    $contract = $json | ConvertFrom-Json
    Require ($contract.schema -eq 'ai_arena.qa_evidence.v1') 'qa-seal emitted the wrong schema.'
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
    foreach ($matrixGate in @('ui.theme-contrast-matrix', 'ui.viewport-dpi-matrix', 'ui.keyboard-automation-matrix', 'ui.reduced-motion-matrix')) {
        Require (@($contract.gates | Where-Object { $_.id -eq $matrixGate -and $_.outcome -eq 'partial' }).Count -eq 1) "qa-seal plan evidence omitted required matrix gate $matrixGate."
    }

    Require (Test-Path -LiteralPath $verificationLab -PathType Leaf) 'Release VerificationLab binary is unavailable.'
    & dotnet $verificationLab --validate-evidence $evidencePath | Out-Null
    Require ($LASTEXITCODE -eq 0) 'VerificationLab rejected plan-only QA evidence.'
    Write-Host 'PASS QA seal orchestration fixtures'
}
catch {
    $failed = $true
    Write-Host "FAIL QA seal orchestration fixtures: $($_.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\qa')).TrimEnd('\', '/')
        $resolved = [IO.Path]::GetFullPath($runRoot)
        $prefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
        $leaf = Split-Path -Leaf $resolved
        $insideArtifactRoot = $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        $ownedFixture = $leaf.StartsWith('qa-seal-fixture-', [StringComparison]::Ordinal)
        if ($insideArtifactRoot -and $ownedFixture) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

if ($failed) { exit 1 }
exit 0
