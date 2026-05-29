param(
    [string]$Version = "0.3.0-modern-preview"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$innoScript = Join-Path $Root "packaging/inno/ai-arena-wpf.iss"
$releaseDir = Join-Path $Root "dist/AI Arena - $Version"
$installerDir = Join-Path $Root "dist/installer/AI Arena - $Version"
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$changes = Join-Path $installerDir "changes.txt"
$changelog = Join-Path $installerDir "MODERN_PREVIEW_CHANGELOG.md"
$releaseExe = Join-Path $releaseDir "AI Arena.exe"
$releaseChangelog = Join-Path $releaseDir "MODERN_PREVIEW_CHANGELOG.md"

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
Assert-PathExists $changelog "installer changelog"
Assert-PathExists $releaseChangelog "release changelog"

$scriptText = Get-Content -LiteralPath $innoScript -Raw
if ($scriptText -notmatch '#define MyAppName "AI Arena Modern Preview"') {
    throw "Installer identity drifted: expected MyAppName to be AI Arena Modern Preview."
}
if ($scriptText -notmatch ('#define MyAppVersion "' + [regex]::Escape($Version) + '"')) {
    throw "Installer version drifted: expected $Version."
}
if ($scriptText -notmatch '#define MyReleaseDir "\.\.\\\.\.\\dist\\AI Arena - 0\.3\.0-modern-preview"') {
    throw "Installer release directory no longer points at the modern preview release folder."
}
if ($scriptText -notmatch 'AppId=\{\{C54E4C61-26F7-4D5E-8898-B7B54065F4AC\}') {
    throw "Installer AppId drifted; side-by-side preview identity may be broken."
}

$looseInstallers = Get-ChildItem -LiteralPath (Join-Path $Root "dist/installer") -Filter "*.exe" -File -ErrorAction SilentlyContinue
if ($looseInstallers.Count -gt 0) {
    throw "Loose installer exe files remain in dist/installer."
}

$installerInfo = Get-Item -LiteralPath $installer
if ($installerInfo.Length -le 0) {
    throw "Installer exists but is empty: $installer"
}

Write-Host "Modern preview sanity passed for AI Arena $Version"
Write-Host $installer
