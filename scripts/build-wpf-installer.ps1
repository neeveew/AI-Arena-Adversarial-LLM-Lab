param(
    [string]$Version = "0.4.124-beta",
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = "Release",
    [ValidateSet('win-x64')]
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [string[]]$Changes = @(),
    [string]$InnoCompiler = "",
    [ValidateSet('Optional', 'Required', 'Disabled')]
    [string]$SigningPolicy = 'Optional',
    [string]$SigningCertificateThumbprint = $env:AIARENA_SIGNING_CERT_THUMBPRINT,
    [string]$SignTool = "",
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$ResumeFinalization
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $repoRoot "scripts\release-security.ps1")

Assert-AIArenaReleaseVersion -Version $Version
Assert-AIArenaRuntimeIdentifier -Runtime $Runtime
if (-not $SelfContained.IsPresent) {
    throw "Installer distributions must be self-contained so AI Arena runs without a separately installed .NET Desktop Runtime."
}
$signing = Resolve-AIArenaSigningConfiguration `
    -Policy $SigningPolicy `
    -CertificateThumbprint $SigningCertificateThumbprint `
    -SignToolPath $SignTool `
    -TimestampUrl $TimestampUrl
if (-not $signing.Enabled -and $SigningPolicy -eq 'Optional') {
    Write-Warning "Authenticode signing is optional and will be skipped: $($signing.Reason)"
}

$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$installerRoot = [IO.Path]::GetFullPath((Join-Path $distRoot "installer"))
$releaseDir = [IO.Path]::GetFullPath((Join-Path $distRoot "AI Arena - $Version"))
$installerDir = [IO.Path]::GetFullPath((Join-Path $installerRoot "AI Arena - $Version"))
Assert-AIArenaPathWithinDirectory -Path $releaseDir -Directory $distRoot -Label 'Versioned release directory'
Assert-AIArenaPathWithinDirectory -Path $installerDir -Directory $installerRoot -Label 'Versioned installer directory'
if ((Split-Path -Leaf $releaseDir) -ne "AI Arena - $Version" -or (Split-Path -Leaf $installerDir) -ne "AI Arena - $Version") {
    throw "Release output leaves do not match the validated version."
}

$innoScript = Join-Path $repoRoot "packaging\inno\ai-arena-wpf.iss"
$releaseScript = Join-Path $repoRoot "scripts\build-wpf-release.ps1"
$sanityScript = Join-Path $repoRoot "scripts\wpf-release-sanity.ps1"
$artifactNames = @(
    "changelog.md",
    "changes.txt",
    "github-release-notes.md",
    "release-checksums.sha256",
    "release-manifest.txt",
    "release-signing.json"
)
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$installerSigningPath = Join-Path $installerDir 'installer-signing.json'
$installerChecksums = Join-Path $installerDir 'SHA256SUMS.txt'

$innoText = Get-Content -LiteralPath $innoScript -Raw
if ($innoText -notmatch ('#define MyAppVersion "' + [regex]::Escape($Version) + '"') `
    -or $innoText -notmatch ('#define MyReleaseDir "\.\.\\\.\.\\dist\\AI Arena - ' + [regex]::Escape($Version) + '"')) {
    throw "Inno Setup version metadata does not match release $Version."
}

if ($ResumeFinalization.IsPresent) {
    if (-not (Test-Path -LiteralPath $releaseDir -PathType Container) `
        -or -not (Test-Path -LiteralPath $installerDir -PathType Container) `
        -or -not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "Resume finalization requires an existing release directory and compiled installer for $Version."
    }

    $alreadyFinalized = @($artifactNames | Where-Object { Test-Path -LiteralPath (Join-Path $installerDir $_) })
    if ($alreadyFinalized.Count -gt 0 `
        -or (Test-Path -LiteralPath $installerSigningPath) `
        -or (Test-Path -LiteralPath $installerChecksums)) {
        throw "Resume finalization requires an untouched post-compile installer directory. Bump the version if finalization already started."
    }
}
else {
    if (Test-Path -LiteralPath $installerDir) {
        throw "Installer distribution already exists: $installerDir. Bump the version before building a new installer."
    }
    if (Test-Path -LiteralPath $releaseDir) {
        throw "Release directory already exists: $releaseDir. Bump the version or remove the incomplete release manually."
    }
}

$releaseArgs = @{
    Version = $Version
    Configuration = $Configuration
    Runtime = $Runtime
    SelfContained = $true
    SigningPolicy = $SigningPolicy
    SigningCertificateThumbprint = $SigningCertificateThumbprint
    SignTool = $SignTool
    TimestampUrl = $TimestampUrl
}
if ($Changes.Count -gt 0) {
    $releaseArgs.Changes = $Changes
}

if (-not $ResumeFinalization.IsPresent) {
    & $releaseScript @releaseArgs
}

if (-not $ResumeFinalization.IsPresent) {
    if (Test-Path -LiteralPath $installerDir) {
        throw "Installer distribution appeared before installer compile: $installerDir. Refusing to overwrite."
    }

    if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
        $candidates = @(
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $InnoCompiler = $candidates |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
            Select-Object -First 1
    }

    if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
        throw "Inno Setup compiler was not found. Install Inno Setup 6 or pass -InnoCompiler."
    }
    $innoCompilerFull = [IO.Path]::GetFullPath($InnoCompiler)
    Assert-AIArenaTrustedExecutable -Path $innoCompilerFull -Label 'Inno Setup compiler'
    Invoke-AIArenaNativeCommand -FilePath $innoCompilerFull -ArgumentList @($innoScript) -Label 'Inno Setup installer compilation'
}

if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Inno Setup did not create the expected installer: $installer"
}

foreach ($name in $artifactNames) {
    $source = Join-Path $releaseDir $name
    $target = Join-Path $installerDir $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing release artifact: $source"
    }
    if (Test-Path -LiteralPath $target) {
        throw "Refusing to overwrite existing installer artifact: $target"
    }

    Copy-Item -LiteralPath $source -Destination $target
}

$installerSignatureRecords = @(Invoke-AIArenaAuthenticodeSigning -Configuration $signing -Path @($installer))
$releaseExe = Join-Path $releaseDir 'AI Arena.exe'
$releaseExeSignature = Get-AuthenticodeSignature -LiteralPath $releaseExe
$installerSigning = [ordered]@{
    format = 'AI Arena installer signing report'
    formatVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    policy = $SigningPolicy
    signingEnabled = [bool]$signing.Enabled
    reason = [string]$signing.Reason
    certificateThumbprint = if ($signing.Enabled) { [string]$signing.CertificateThumbprint } else { $null }
    certificateStoreLocation = if ($signing.Enabled) { [string]$signing.CertificateStoreLocation } else { $null }
    timestampUrl = if ($signing.Enabled) { [string]$signing.TimestampUrl } else { $null }
    artifacts = @(
        [ordered]@{
            path = 'AI Arena.exe'
            location = 'release'
            status = $releaseExeSignature.Status.ToString()
            signerThumbprint = if ($null -ne $releaseExeSignature.SignerCertificate) { $releaseExeSignature.SignerCertificate.Thumbprint } else { $null }
        },
        [ordered]@{
            path = [IO.Path]::GetFileName($installerSignatureRecords[0].path)
            location = 'installer'
            status = $installerSignatureRecords[0].status
            signerThumbprint = $installerSignatureRecords[0].signerThumbprint
            timeStamperThumbprint = $installerSignatureRecords[0].timeStamperThumbprint
        }
    )
}
$installerSigning | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $installerSigningPath -Encoding UTF8

New-AIArenaSha256Manifest -BaseDirectory $installerDir -OutputPath $installerChecksums

& $sanityScript -Version $Version -SigningPolicy $SigningPolicy

Write-Host "WPF installer distribution created:"
Write-Host $installerDir
Write-Host $installer
Write-Host $installerChecksums
Write-Host $installerSigningPath
