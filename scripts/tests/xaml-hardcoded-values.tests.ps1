$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$gateScript = Join-Path $repositoryRoot "scripts/xaml-hardcoded-values.ps1"
$enginePath = (Get-Process -Id $PID).Path
$temporaryBase = [System.IO.Path]::GetTempPath().TrimEnd('\', '/')
$fixtureRoot = Join-Path $temporaryBase ("ai-arena-xaml-ratchet-" + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $fixtureRoot "src"
$docsRoot = Join-Path $fixtureRoot "docs"
$fixturePath = Join-Path $sourceRoot "Fixture.xaml"
$otherPath = Join-Path $sourceRoot "Other.xaml"
$excludedRoot = Join-Path $sourceRoot "bin"
$excludedPath = Join-Path $excludedRoot "Ignored.xaml"
$baselinePath = Join-Path $docsRoot "xaml-hardcoded-baseline.json"
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Require {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Write-FixtureFile {
    param([string]$Path, [string]$Text)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Invoke-Gate {
    param(
        [string[]]$Arguments,
        [string]$Engine = $enginePath
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $engineArguments = @('-NoProfile')
        if ([System.IO.Path]::GetFileNameWithoutExtension($Engine) -ieq 'powershell') {
            $engineArguments += @('-ExecutionPolicy', 'Bypass')
        }
        $engineArguments += @('-File', $gateScript, '-RepositoryRoot', $fixtureRoot)
        $output = @(& $Engine @engineArguments @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output -join [Environment]::NewLine
    }
}

function Invoke-FixtureGit {
    param([string[]]$Arguments)

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& git -c 'core.excludesFile=' -C $fixtureRoot @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "Fixture git command failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

$initialMarkup = @'
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Grid.Resources>
    <Style TargetType="Button">
      <Setter Value="7, 4" Property="Control.Padding" />
      <Setter Property="FontSize" Value="{StaticResource CaptionSize}" />
    </Style>
  </Grid.Resources>
  <Border Margin="0, 0, 6, 0" />
  <TextBlock TextElement.FontSize="12" />
</Grid>
'@

$reducedMarkup = @'
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Grid.Resources>
    <Style TargetType="Button">
      <Setter Value="7, 4" Property="Control.Padding" />
      <Setter Property="FontSize" Value="{StaticResource CaptionSize}" />
    </Style>
  </Grid.Resources>
  <Border Margin="{StaticResource InlineGap}" />
  <TextBlock TextElement.FontSize="12" />
</Grid>
'@

try {
    [void](New-Item -ItemType Directory -Path $sourceRoot -Force)
    [void](New-Item -ItemType Directory -Path $docsRoot -Force)
    Write-FixtureFile -Path $fixturePath -Text $initialMarkup
    Write-FixtureFile -Path $excludedPath -Text '<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Margin="99" />'

    $create = Invoke-Gate @('-Update')
    Require ($create.ExitCode -eq 0) "Initial -Update failed: $($create.Output)"
    Require (Test-Path -LiteralPath $baselinePath -PathType Leaf) "Initial -Update did not create the baseline."

    $baselineText = [System.IO.File]::ReadAllText($baselinePath)
    $baseline = $baselineText | ConvertFrom-Json
    Require ([int]$baseline.schemaVersion -eq 2) "Baseline should use schema version 2."
    Require ([int]$baseline.total -eq 3) "Fixture should inventory exactly three literals."
    Require ([int]$baseline.properties.Margin -eq 1) "Direct Margin attributes should be inventoried."
    Require ([int]$baseline.properties.Padding -eq 1) "Attached, attribute-reordered Setter values should be inventoried."
    Require ([int]$baseline.properties.FontSize -eq 1) "Direct attached properties should be inventoried."
    Require ($baselineText -notmatch [regex]::Escape($fixtureRoot)) "Baseline must not persist absolute paths."
    Require ($null -eq $baseline.files.PSObject.Properties['src/bin/Ignored.xaml']) "Excluded directories should not enter the baseline."

    $report = Invoke-Gate @()
    Require ($report.ExitCode -eq 0) "Report-only mode should succeed: $($report.Output)"
    Require ($report.Output -match 'report only; no files changed') "Report-only mode should state that it is non-mutating."
    Require ([System.IO.File]::ReadAllText($baselinePath) -eq $baselineText) "Report-only mode must not modify the baseline."

    $conflictingModes = Invoke-Gate @('-Check', '-Update')
    Require ($conflictingModes.ExitCode -ne 0) "Conflicting -Check and -Update modes should fail."
    $invalidRef = Invoke-Gate @('-Check', '-BaselineRef', 'abc')
    Require ($invalidRef.ExitCode -ne 0) "Short BaselineRef values should fail validation."
    $missingRef = Invoke-Gate @('-Check', '-BaselineRef', ('f' * 40))
    Require ($missingRef.ExitCode -ne 0) "Unavailable BaselineRef commits should fail."

    $check = Invoke-Gate @('-Check')
    Require ($check.ExitCode -eq 0) "Fresh baseline should pass -Check: $($check.Output)"

    Write-FixtureFile -Path $baselinePath -Text ($baselineText -replace '"schemaVersion": 2', '"schemaVersion": "2"')
    $stringSchema = Invoke-Gate @('-Check')
    Require ($stringSchema.ExitCode -ne 0) "String schemaVersion values should be rejected."
    Require ($stringSchema.Output -match 'JSON number') "Invalid schema type should report the JSON-number contract."
    Write-FixtureFile -Path $baselinePath -Text $baselineText

    Write-FixtureFile -Path $baselinePath -Text ($baselineText -replace '"Margin": 1', '"Margin": "1"')
    $stringCount = Invoke-Gate @('-Check')
    Require ($stringCount.ExitCode -ne 0) "String inventory counts should be rejected."
    Require ($stringCount.Output -match 'JSON number') "Invalid count type should report the JSON-number contract."
    Write-FixtureFile -Path $baselinePath -Text $baselineText

    $repeat = Invoke-Gate @('-Update')
    Require ($repeat.ExitCode -eq 0) "Deterministic repeat -Update failed: $($repeat.Output)"
    Require ([System.IO.File]::ReadAllText($baselinePath) -eq $baselineText) "Unchanged inventory should serialize deterministically."
    $remnants = @(Get-ChildItem -LiteralPath $docsRoot -Force -File |
        Where-Object { $_.Name -like '.xaml-hardcoded-baseline.json.*' })
    Require ($remnants.Count -eq 0) "Atomic update should not leave temporary or backup files."

    $alternateName = if ([System.IO.Path]::GetFileNameWithoutExtension($enginePath) -ieq 'pwsh') {
        'powershell'
    } else {
        'pwsh'
    }
    $alternateCommand = Get-Command $alternateName -ErrorAction SilentlyContinue
    if ($null -ne $alternateCommand) {
        $alternateRepeat = Invoke-Gate -Arguments @('-Update') -Engine $alternateCommand.Source
        Require ($alternateRepeat.ExitCode -eq 0) "Cross-engine -Update failed: $($alternateRepeat.Output)"
        Require ([System.IO.File]::ReadAllText($baselinePath) -eq $baselineText) "Windows PowerShell and pwsh should serialize identical baseline bytes."
    }

    Write-FixtureFile -Path $fixturePath -Text $reducedMarkup
    $staleReduction = Invoke-Gate @('-Check')
    Require ($staleReduction.ExitCode -ne 0) "A reduction should make the committed baseline stale."
    Require ($staleReduction.Output -match 'Reduced or removed') "Reduction failure should explain why the baseline is stale."

    $lockReduction = Invoke-Gate @('-Update')
    Require ($lockReduction.ExitCode -eq 0) "Reduction-only -Update should succeed: $($lockReduction.Output)"
    $reducedBaselineText = [System.IO.File]::ReadAllText($baselinePath)

    Write-FixtureFile -Path $fixturePath -Text ($reducedMarkup -replace '<Border Margin="\{StaticResource InlineGap\}" />', '<Border Margin="{StaticResource InlineGap}" CornerRadius="9" />')
    $increase = Invoke-Gate @('-Update')
    Require ($increase.ExitCode -ne 0) "-Update should refuse a new literal."
    Require ($increase.Output -match 'Refusing to increase or introduce') "Increase failure should explain the reduction-only rule."
    Require ([System.IO.File]::ReadAllText($baselinePath) -eq $reducedBaselineText) "Rejected -Update must not modify the baseline."

    Write-FixtureFile -Path $fixturePath -Text $reducedMarkup
    Write-FixtureFile -Path $otherPath -Text @'
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Button Padding="7,4" />
</Grid>
'@
    $movedValue = $reducedMarkup -replace '<Setter Value="7, 4" Property="Control\.Padding" />', '<Setter Value="{StaticResource RailInset}" Property="Control.Padding" />'
    Write-FixtureFile -Path $fixturePath -Text $movedValue

    $crossFileOffset = Invoke-Gate @('-Update')
    Require ($crossFileOffset.ExitCode -ne 0) "Moving the same literal to another file should not pass by offsetting counts."
    Require ($crossFileOffset.Output -match 'src/Other.xaml') "Cross-file failure should identify the new location."

    Remove-Item -LiteralPath $otherPath -Force
    Write-FixtureFile -Path $fixturePath -Text $reducedMarkup
    $restored = Invoke-Gate @('-Check')
    Require ($restored.ExitCode -eq 0) "Fixture should return to its reduced baseline before the git-ref test."

    [void](Invoke-FixtureGit @('init', '--quiet'))
    [void](Invoke-FixtureGit @('config', 'user.name', 'AI Arena Ratchet Test'))
    [void](Invoke-FixtureGit @('config', 'user.email', 'ratchet@example.invalid'))
    [void](Invoke-FixtureGit @('add', 'docs/xaml-hardcoded-baseline.json', 'src/Fixture.xaml'))
    [void](Invoke-FixtureGit @('commit', '--quiet', '-m', 'baseline'))
    $baseSha = (@(Invoke-FixtureGit @('rev-parse', 'HEAD')) -join '').Trim()
    Require ($baseSha -match '^[0-9a-f]{40}$') "Fixture git baseline should have a full commit SHA."

    Write-FixtureFile -Path $fixturePath -Text ($reducedMarkup -replace '<Border Margin="\{StaticResource InlineGap\}" />', '<Border Margin="{StaticResource InlineGap}" CornerRadius="9" />')
    Remove-Item -LiteralPath $baselinePath -Force
    $inflatedBaseline = Invoke-Gate @('-Update')
    Require ($inflatedBaseline.ExitCode -eq 0) "Fixture should be able to simulate a manually inflated replacement baseline."
    $baseComparison = Invoke-Gate @('-Check', '-BaselineRef', $baseSha)
    Require ($baseComparison.ExitCode -ne 0) "BaselineRef comparison should reject inventory inflation relative to the base commit."
    Require ($baseComparison.Output -match 'grew relative') "BaselineRef inflation failure should identify the base comparison."

    Write-Host "PASS XAML hard-coded ratchet fixtures"
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolvedFixture = (Resolve-Path -LiteralPath $fixtureRoot).Path
        if (-not $resolvedFixture.StartsWith($temporaryBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing fixture cleanup outside the temporary directory: $resolvedFixture"
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
