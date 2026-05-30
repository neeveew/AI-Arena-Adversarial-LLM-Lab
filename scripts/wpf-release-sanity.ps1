param(
    [string]$Version = "0.3.26-beta"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$innoScript = Join-Path $Root "packaging/inno/ai-arena-wpf.iss"
$releaseDir = Join-Path $Root "dist/AI Arena - $Version"
$installerDir = Join-Path $Root "dist/installer/AI Arena - $Version"
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$changes = Join-Path $installerDir "changes.txt"
$releaseExe = Join-Path $releaseDir "AI Arena.exe"
$dependencyIndexScript = Join-Path $Root "scripts/dependency-index.ps1"

function Assert-PathExists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing ${Label}: $Path"
    }
}

Assert-PathExists $innoScript "Inno script"
Assert-PathExists $releaseExe "release executable"
Assert-PathExists $installer "installer"
Assert-PathExists $changes "installer changes file"
Assert-PathExists $dependencyIndexScript "dependency index script"

& $dependencyIndexScript -Check

$scriptText = Get-Content -LiteralPath $innoScript -Raw
if ($scriptText -notmatch '#define MyAppName "AI Arena"') {
    throw "Installer identity drifted: expected MyAppName to be AI Arena."
}
if ($scriptText -notmatch ('#define MyAppVersion "' + [regex]::Escape($Version) + '"')) {
    throw "Installer version drifted: expected $Version."
}
if ($scriptText -notmatch ('#define MyReleaseDir "\.\.\\\.\.\\dist\\AI Arena - ' + [regex]::Escape($Version) + '"')) {
    throw "Installer release directory no longer points at dist/AI Arena - $Version."
}
if ($scriptText -notmatch ('OutputDir=\.\.\\\.\.\\dist\\installer\\AI Arena - \{#MyAppVersion\}')) {
    throw "Installer output directory no longer points at the versioned installer folder."
}
if ($scriptText -notmatch 'AppId=\{\{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61\}') {
    throw "Installer AppId drifted; stable AI Arena upgrade identity may be broken."
}

$looseInstallers = Get-ChildItem -LiteralPath (Join-Path $Root "dist/installer") -Filter "*.exe" -File -ErrorAction SilentlyContinue
if ($looseInstallers.Count -gt 0) {
    throw "Loose installer exe files remain in dist/installer."
}

$installerInfo = Get-Item -LiteralPath $installer
if ($installerInfo.Length -le 0) {
    throw "Installer exists but is empty: $installer"
}

Write-Host "WPF release sanity passed for AI Arena $Version"
Write-Host $installer
