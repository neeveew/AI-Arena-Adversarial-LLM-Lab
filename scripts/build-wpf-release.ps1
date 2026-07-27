param(
    [string]$Version = "0.4.121-beta",
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = "Release",
    [ValidateSet('win-x64')]
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [switch]$Force,
    [string[]]$Changes = @(),
    [ValidateSet('Optional', 'Required', 'Disabled')]
    [string]$SigningPolicy = 'Optional',
    [string]$SigningCertificateThumbprint = $env:AIARENA_SIGNING_CERT_THUMBPRINT,
    [string]$SignTool = "",
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$securityScript = Join-Path $repoRoot "scripts\release-security.ps1"
. $securityScript

Assert-AIArenaReleaseVersion -Version $Version
Assert-AIArenaRuntimeIdentifier -Runtime $Runtime
$signing = Resolve-AIArenaSigningConfiguration `
    -Policy $SigningPolicy `
    -CertificateThumbprint $SigningCertificateThumbprint `
    -SignToolPath $SignTool `
    -TimestampUrl $TimestampUrl
if (-not $signing.Enabled -and $SigningPolicy -eq 'Optional') {
    Write-Warning "Authenticode signing is optional and will be skipped: $($signing.Reason)"
}

$project = Join-Path $repoRoot "src\AIArena.Wpf\AIArena.Wpf.csproj"
$coreProject = Join-Path $repoRoot "src\AIArena.Core\AIArena.Core.csproj"
$coreTests = Join-Path $repoRoot "tests\AIArena.Tests\AIArena.Tests.csproj"
$wpfTests = Join-Path $repoRoot "tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj"
$controlPlaneHelper = Join-Path $repoRoot "scripts\ai-arena-control.ps1"
$searxngPayloadScript = Join-Path $repoRoot "scripts\build-searxng-payload.ps1"
$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$output = [IO.Path]::GetFullPath((Join-Path $distRoot "AI Arena - $Version"))
Assert-AIArenaPathWithinDirectory -Path $output -Directory $distRoot -Label 'Versioned release output'
if ((Split-Path -Leaf $output) -ne "AI Arena - $Version") {
    throw "Release output leaf does not match the validated version."
}

$changelogPath = Join-Path $output "changelog.md"
$changesPath = Join-Path $output "changes.txt"
$githubNotesPath = Join-Path $output "github-release-notes.md"
$releaseSigningPath = Join-Path $output "release-signing.json"
$releaseChecksumsPath = Join-Path $output "release-checksums.sha256"
$manifestPath = Join-Path $output "release-manifest.txt"
$packagedChangesPath = Join-Path $repoRoot "packaging\changes\$Version.txt"

foreach ($versionedProject in @($project, $coreProject)) {
    $projectText = Get-Content -LiteralPath $versionedProject -Raw
    if ($projectText -notmatch ('<Version>' + [regex]::Escape($Version) + '</Version>') `
        -or $projectText -notmatch ('<InformationalVersion>' + [regex]::Escape($Version) + '</InformationalVersion>')) {
        throw "Project version metadata does not match release ${Version}: $versionedProject"
    }
}

if ((Test-Path -LiteralPath $output) -and -not $Force) {
    throw "Versioned output already exists: $output. Choose a new version or pass -Force."
}
if ((Test-Path -LiteralPath $output) -and $Force) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
Invoke-AIArenaNativeCommand -FilePath $dotnet.Source -ArgumentList @('run', '--project', $coreTests, '--no-restore') -Label 'Core test harness'
Invoke-AIArenaNativeCommand -FilePath $dotnet.Source -ArgumentList @('run', '--project', $wpfTests, '--no-restore') -Label 'WPF test harness'

# ReadyToRun precompiles the hot startup path. Measured on a self-contained
# build: warm launch to a responsive shell drops from about 1230 ms to 1110 ms,
# for roughly 13 MB more on disk before installer compression.
$publishArgs = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $output,
    "-p:PublishSingleFile=false",
    "-p:PublishReadyToRun=true",
    "-p:UseAppHost=true",
    "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant()
)
Invoke-AIArenaNativeCommand -FilePath $dotnet.Source -ArgumentList $publishArgs -Label 'WPF release publish'

$exe = Join-Path $output "AI Arena.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Expected WPF executable was not created: $exe"
}

$runtimeConfigPath = Join-Path $output "AI Arena.runtimeconfig.json"
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "Expected WPF runtime configuration was not created: $runtimeConfigPath"
}

$privateRuntimeFileNames = @(
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'PresentationFramework.dll'
)
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$runtimeOptionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
$hasFrameworkReferences = $runtimeOptionNames -contains 'frameworks'
$hasIncludedFrameworks = $runtimeOptionNames -contains 'includedFrameworks'

if ($SelfContained.IsPresent) {
    $missingRuntimeFiles = @($privateRuntimeFileNames | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $output $_) -PathType Leaf)
    })
    if ($missingRuntimeFiles.Count -gt 0) {
        throw "Self-contained publish is missing private .NET runtime files: $($missingRuntimeFiles -join ', ')."
    }
    if (-not $hasIncludedFrameworks -or $hasFrameworkReferences) {
        throw "Self-contained publish has an inconsistent runtimeconfig.json; expected includedFrameworks and no framework-dependent frameworks property."
    }
} else {
    $unexpectedRuntimeFiles = @($privateRuntimeFileNames | Where-Object {
        Test-Path -LiteralPath (Join-Path $output $_) -PathType Leaf
    })
    if ($unexpectedRuntimeFiles.Count -gt 0) {
        throw "Framework-dependent publish unexpectedly includes private .NET runtime files: $($unexpectedRuntimeFiles -join ', '). Refusing a mixed runtime payload."
    }
    if (-not $hasFrameworkReferences -or $hasIncludedFrameworks) {
        throw "Framework-dependent publish has an inconsistent runtimeconfig.json; expected frameworks and no includedFrameworks property."
    }
}

& $searxngPayloadScript -OutputDir $output
if (-not (Test-Path -LiteralPath $controlPlaneHelper -PathType Leaf)) {
    throw "PowerShell control helper is missing: $controlPlaneHelper"
}
Copy-Item -LiteralPath $controlPlaneHelper -Destination (Join-Path $output "ai-arena-control.ps1")

$signatureRecords = @(Invoke-AIArenaAuthenticodeSigning -Configuration $signing -Path @($exe))
$releaseSigning = [ordered]@{
    format = 'AI Arena release signing report'
    formatVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    policy = $SigningPolicy
    signingEnabled = [bool]$signing.Enabled
    reason = [string]$signing.Reason
    certificateThumbprint = if ($signing.Enabled) { [string]$signing.CertificateThumbprint } else { $null }
    certificateSubject = if ($signing.Enabled) { [string]$signing.Certificate.Subject } else { $null }
    certificateStoreLocation = if ($signing.Enabled) { [string]$signing.CertificateStoreLocation } else { $null }
    timestampUrl = if ($signing.Enabled) { [string]$signing.TimestampUrl } else { $null }
    artifacts = @($signatureRecords | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetFileName($_.path)
            status = $_.status
            signerThumbprint = $_.signerThumbprint
            timeStamperThumbprint = $_.timeStamperThumbprint
        }
    })
}
$releaseSigning | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $releaseSigningPath -Encoding UTF8

if ($Changes.Count -eq 0 -and (Test-Path -LiteralPath $packagedChangesPath -PathType Leaf)) {
    $Changes = Get-Content -LiteralPath $packagedChangesPath |
        Where-Object { $_ -match '^\s*-\s+' } |
        ForEach-Object { ($_ -replace '^\s*-\s+', '').Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}
if ($Changes.Count -eq 0) {
    $Changes = @(
        "WPF beta build $Version",
        "See git history for detailed changes."
    )
}

$builtAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
$changeLines = @(
    "AI Arena $Version",
    "Built: $builtAt",
    "",
    "Changes:"
) + ($Changes | ForEach-Object { "- $_" })
Set-Content -LiteralPath $changesPath -Value $changeLines -Encoding UTF8

$markdownChanges = $Changes | ForEach-Object { "- $_" }
$changelogLines = @(
    "# AI Arena $Version",
    "",
    "Built: $builtAt",
    "",
    "## Changes"
) + $markdownChanges
Set-Content -LiteralPath $changelogPath -Value $changelogLines -Encoding UTF8

$githubNotesLines = @(
    "# AI Arena $Version",
    "",
    "## Highlights"
) + $markdownChanges + @(
    "",
    "## Assets",
    "- AI Arena Setup $Version.exe",
    "- SHA256SUMS.txt",
    "- changelog.md",
    "- changes.txt",
    "- github-release-notes.md",
    "- release-checksums.sha256",
    "- release-manifest.txt",
    "- release-signing.json"
)
Set-Content -LiteralPath $githubNotesPath -Value $githubNotesLines -Encoding UTF8

New-AIArenaSha256Manifest `
    -BaseDirectory $output `
    -OutputPath $releaseChecksumsPath `
    -ExcludeRelativePath @('release-manifest.txt')

$manifestLines = @(
    "AI Arena Release Manifest",
    "Version: $Version",
    "Built: $builtAt",
    "Configuration: $Configuration",
    "Runtime: $Runtime",
    "Self-contained: $($SelfContained.IsPresent)",
    "Signing policy: $SigningPolicy",
    "Signing enabled: $($signing.Enabled)",
    "",
    "SHA256:"
)
$manifestLines += Get-AIArenaSha256Entries -BaseDirectory $output -ExcludeRelativePath @('release-manifest.txt') |
    ForEach-Object { "$($_.Hash)  $($_.RelativePath)" }
Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding UTF8

Write-Host "WPF release build created:"
Write-Host $output
Write-Host $exe
Write-Host $changelogPath
Write-Host $changesPath
Write-Host $githubNotesPath
Write-Host $releaseChecksumsPath
Write-Host $manifestPath
Write-Host $releaseSigningPath
