param(
    [string]$Version = "0.3.86-beta"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$innoScript = Join-Path $Root "packaging/inno/ai-arena-wpf.iss"
$releaseDir = Join-Path $Root "dist/AI Arena - $Version"
$installerDir = Join-Path $Root "dist/installer/AI Arena - $Version"
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$changes = Join-Path $installerDir "changes.txt"
$releaseManifest = Join-Path $releaseDir "release-manifest.txt"
$installerManifest = Join-Path $installerDir "release-manifest.txt"
$releaseExe = Join-Path $releaseDir "AI Arena.exe"
$dependencyIndexScript = Join-Path $Root "scripts/dependency-index.ps1"
$licenseFile = Join-Path $Root "LICENSE"
$noticeFile = Join-Path $Root "NOTICE.md"
$userGuideFile = Join-Path $Root "windows-wpf/docs/USER_GUIDE.md"
$shortcutIconFile = Join-Path $Root "windows-wpf/src/AIArena.Wpf/Assets/ai-arena-icon.ico"
$wpfProject = Join-Path $Root "windows-wpf/src/AIArena.Wpf/AIArena.Wpf.csproj"

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
Assert-PathExists $releaseManifest "release manifest"
Assert-PathExists $installerManifest "installer release manifest"
Assert-PathExists $dependencyIndexScript "dependency index script"
Assert-PathExists $licenseFile "licence file"
Assert-PathExists $noticeFile "notice file"
Assert-PathExists $userGuideFile "user guide"
Assert-PathExists $shortcutIconFile "shortcut icon"
Assert-PathExists $wpfProject "WPF project"

& $dependencyIndexScript -Check

$scriptText = Get-Content -LiteralPath $innoScript -Raw
if ($scriptText -notmatch '#define MyAppName "AI Arena"') {
    throw "Installer identity drifted: expected MyAppName to be AI Arena."
}
if ($scriptText -notmatch '#define MyAppDisplayName "AI Arena: Adversarial LLM Lab"') {
    throw "Installer display identity drifted: expected AI Arena: Adversarial LLM Lab."
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
if ($scriptText -notmatch '#define MyReleaseUrl "https://github\.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases"') {
    throw "Installer release URL drifted."
}
if ($scriptText -notmatch 'AppPublisherURL=\{#MyReleaseUrl\}') {
    throw "Installer publisher URL is missing."
}
if ($scriptText -notmatch 'AppSupportURL=\{#MyReleaseUrl\}') {
    throw "Installer support URL is missing."
}
if ($scriptText -notmatch 'AppUpdatesURL=\{#MyReleaseUrl\}') {
    throw "Installer updates URL is missing."
}
if ($scriptText -notmatch 'AppId=\{\{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61\}') {
    throw "Installer AppId drifted; stable AI Arena upgrade identity may be broken."
}
if ($scriptText -notmatch 'AppName=\{#MyAppDisplayName\}') {
    throw "Installer AppName no longer uses the public display name."
}
if ($scriptText -notmatch 'AppVerName=\{#MyAppDisplayName\} - \{#MyAppVersion\}') {
    throw "Installer AppVerName no longer shows the public display name and version."
}
if ($scriptText -notmatch 'LicenseFile=\.\.\\\.\.\\LICENSE') {
    throw "Installer licence page drifted: expected LICENSE to be shown during setup."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\LICENSE"; DestDir: "\{app\}"') {
    throw "Installer no longer installs LICENSE beside the app."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\NOTICE\.md"; DestDir: "\{app\}"') {
    throw "Installer no longer installs NOTICE.md beside the app."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\windows-wpf\\docs\\USER_GUIDE\.md"; DestDir: "\{app\}"') {
    throw "Installer no longer installs USER_GUIDE.md beside the app."
}
if ($scriptText -notmatch 'Filename: "\{app\}\\USER_GUIDE\.md"; Description: "Open user guide"; Flags: shellexec postinstall skipifsilent') {
    throw "Installer no longer offers the user guide at the end of setup."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\windows-wpf\\src\\AIArena\.Wpf\\Assets\\ai-arena-icon\.ico"; DestDir: "\{app\}"') {
    throw "Installer no longer installs the app icon beside the app."
}
if ($scriptText -notmatch 'DefaultDirName=\{localappdata\}\\\{#MyAppName\}') {
    throw "Installer no longer defaults to the per-user LocalAppData AI Arena folder."
}
if ($scriptText -notmatch 'DisableDirPage=no') {
    throw "Installer no longer allows manual install directory selection."
}
if ($scriptText -notmatch 'UsePreviousAppDir=no') {
    throw "Installer may remember older Program Files install paths instead of the current per-user default."
}
if ($scriptText -notmatch 'PrivilegesRequired=lowest') {
    throw "Installer no longer uses per-user privileges."
}
if ($scriptText -notmatch 'Name: "\{userdesktop\}\\\{#MyAppName\}".*IconFilename: "\{app\}\\\{#MyAppIconName\}"') {
    throw "Per-user desktop shortcut no longer has an explicit icon."
}
if ($scriptText -notmatch 'Name: "\{group\}\\\{#MyAppName\}".*IconFilename: "\{app\}\\\{#MyAppIconName\}"') {
    throw "Start Menu shortcut no longer has an explicit icon."
}
if ($scriptText -notmatch 'Name: "\{group\}\\AI Arena User Guide"; Filename: "\{app\}\\USER_GUIDE\.md"') {
    throw "Start Menu user guide shortcut is missing."
}
if ($scriptText -notmatch 'Name: "\{group\}\\Release Notes"; Filename: "\{app\}\\changes\.txt"') {
    throw "Start Menu release notes shortcut is missing."
}
if ($scriptText -notmatch 'Name: "\{group\}\\GitHub Releases"; Filename: "\{#MyReleaseUrl\}"') {
    throw "Start Menu GitHub releases shortcut is missing."
}

$projectText = Get-Content -LiteralPath $wpfProject -Raw
if ($projectText -notmatch ('<Version>' + [regex]::Escape($Version) + '</Version>')) {
    throw "WPF project Version drifted: expected $Version."
}
if ($projectText -notmatch ('<InformationalVersion>' + [regex]::Escape($Version) + '</InformationalVersion>')) {
    throw "WPF project InformationalVersion drifted: expected $Version."
}

$licenseText = Get-Content -LiteralPath $licenseFile -Raw
if ($licenseText -notmatch 'Shareable No-Derivatives Software Licence') {
    throw "Root licence does not identify the expected no-derivatives licence."
}
if ($licenseText -notmatch 'Copyright © 2026 Dominik Fiala') {
    throw "Root licence copyright notice drifted."
}

$guideText = Get-Content -LiteralPath $userGuideFile -Raw
foreach ($requiredGuideSection in @(
    '## Quick Start',
    '## Transcript',
    '## Custom Match',
    '## Agent Performance',
    '## Licensing'
)) {
    if ($guideText -notmatch [regex]::Escape($requiredGuideSection)) {
        throw "User guide missing required section: $requiredGuideSection"
    }
}

$manifestText = Get-Content -LiteralPath $releaseManifest -Raw
$installerManifestText = Get-Content -LiteralPath $installerManifest -Raw
$releaseExeHash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
if ($manifestText -notmatch 'AI Arena Release Manifest') {
    throw "Release manifest missing title."
}
if ($manifestText -notmatch ('Version: ' + [regex]::Escape($Version))) {
    throw "Release manifest version drifted."
}
if ($manifestText -notmatch [regex]::Escape($releaseExeHash)) {
    throw "Release manifest does not include the release executable hash."
}
if ($installerManifestText -ne $manifestText) {
    throw "Installer release manifest copy does not match release manifest."
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
