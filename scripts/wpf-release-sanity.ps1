param(
    [string]$Version = "0.4.121-beta",
    [string]$SigningPolicy = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $Root "scripts/release-security.ps1")
Assert-AIArenaReleaseVersion -Version $Version
if (-not [string]::IsNullOrWhiteSpace($SigningPolicy) -and $SigningPolicy -notin @('Optional', 'Required', 'Disabled')) {
    throw "SigningPolicy must be Optional, Required, Disabled, or omitted."
}

$innoScript = Join-Path $Root "packaging/inno/ai-arena-wpf.iss"
$releaseDir = Join-Path $Root "dist/AI Arena - $Version"
$installerDir = Join-Path $Root "dist/installer/AI Arena - $Version"
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$changelog = Join-Path $installerDir "changelog.md"
$changes = Join-Path $installerDir "changes.txt"
$githubReleaseNotes = Join-Path $installerDir "github-release-notes.md"
$releaseManifest = Join-Path $releaseDir "release-manifest.txt"
$installerManifest = Join-Path $installerDir "release-manifest.txt"
$releaseChangelog = Join-Path $releaseDir "changelog.md"
$releaseGithubReleaseNotes = Join-Path $releaseDir "github-release-notes.md"
$releaseExe = Join-Path $releaseDir "AI Arena.exe"
$controlPlaneHelper = Join-Path $releaseDir "ai-arena-control.ps1"
$releaseRuntimeConfig = Join-Path $releaseDir "AI Arena.runtimeconfig.json"
$releasePrivateRuntimeFiles = @(
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'PresentationFramework.dll'
) | ForEach-Object { Join-Path $releaseDir $_ }
$searxngDir = Join-Path $releaseDir "searxng"
$searxngPythonw = Join-Path $searxngDir "python/pythonw.exe"
$searxngSettings = Join-Path $searxngDir "settings.yml"
$searxngArenaGateway = Join-Path $searxngDir "runtime/arena_searxng_wsgi.py"
$searxngLicense = Join-Path $searxngDir "LICENSE"
$searxngSourceOffer = Join-Path $searxngDir "SEARXNG-SOURCE-OFFER.txt"
$searxngPayloadManifest = Join-Path $searxngDir "payload-manifest.txt"
$searxngPayloadInventory = Join-Path $searxngDir "payload-inventory.json"
$searxngUpstreamLock = Join-Path $searxngDir "UPSTREAM-LOCK.json"
$searxngDependencyLock = Join-Path $searxngDir "PYTHON-REQUIREMENTS-LOCK.txt"
$upstreamLockFile = Join-Path $Root "packaging/upstream-lock.json"
$dependencyLockFile = Join-Path $Root "packaging/searxng-requirements-lock.txt"
$releaseChecksums = Join-Path $releaseDir "release-checksums.sha256"
$installerChecksums = Join-Path $installerDir "SHA256SUMS.txt"
$releaseSigningReport = Join-Path $releaseDir "release-signing.json"
$installerReleaseSigningReport = Join-Path $installerDir "release-signing.json"
$installerSigningReport = Join-Path $installerDir "installer-signing.json"
$dependencyIndexScript = Join-Path $Root "scripts/dependency-index.ps1"
$solutionFile = Join-Path $Root "AI Arena - WPF.sln"
$coreTests = Join-Path $Root "tests/AIArena.Tests/AIArena.Tests.csproj"
$wpfTests = Join-Path $Root "tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj"
$licenseFile = Join-Path $Root "LICENSE"
$noticeFile = Join-Path $Root "NOTICE.md"
$userGuideFile = Join-Path $Root "docs/USER_GUIDE.md"
$shortcutIconFile = Join-Path $Root "src/AIArena.Wpf/Assets/ai-arena-icon.ico"
$wpfProject = Join-Path $Root "src/AIArena.Wpf/AIArena.Wpf.csproj"
$coreProject = Join-Path $Root "src/AIArena.Core/AIArena.Core.csproj"

function Assert-PathExists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing ${Label}: $Path"
    }
}

Assert-PathExists $innoScript "Inno script"
Assert-PathExists $releaseExe "release executable"
Assert-PathExists $controlPlaneHelper "installed PowerShell control helper"
Assert-PathExists $releaseRuntimeConfig "self-contained runtime configuration"
foreach ($runtimeFile in $releasePrivateRuntimeFiles) {
    Assert-PathExists $runtimeFile "self-contained .NET runtime file"
}
Assert-PathExists $searxngDir "bundled SearXNG payload"
Assert-PathExists $searxngPythonw "bundled SearXNG pythonw"
Assert-PathExists $searxngSettings "bundled SearXNG settings"
Assert-PathExists $searxngArenaGateway "AI Arena SearXNG JSON API boundary"
Assert-PathExists $searxngLicense "bundled SearXNG AGPL licence"
Assert-PathExists $searxngSourceOffer "bundled SearXNG source offer"
Assert-PathExists $searxngPayloadManifest "bundled SearXNG payload manifest"
Assert-PathExists $searxngPayloadInventory "bundled SearXNG payload inventory"
Assert-PathExists $searxngUpstreamLock "bundled upstream lock"
Assert-PathExists $searxngDependencyLock "bundled Python dependency lock"
Assert-PathExists $upstreamLockFile "reviewed upstream lock"
Assert-PathExists $dependencyLockFile "reviewed Python dependency lock"
Assert-PathExists $installer "installer"
Assert-PathExists $changelog "installer changelog"
Assert-PathExists $changes "installer changes file"
Assert-PathExists $githubReleaseNotes "installer GitHub release notes"
Assert-PathExists $releaseManifest "release manifest"
Assert-PathExists $installerManifest "installer release manifest"
Assert-PathExists $releaseChecksums "release checksum manifest"
Assert-PathExists $installerChecksums "installer checksum manifest"
Assert-PathExists $releaseSigningReport "release signing report"
Assert-PathExists $installerReleaseSigningReport "installer copy of release signing report"
Assert-PathExists $installerSigningReport "installer signing report"
Assert-PathExists $releaseChangelog "release changelog"
Assert-PathExists $releaseGithubReleaseNotes "release GitHub release notes"
Assert-PathExists $dependencyIndexScript "dependency index script"
Assert-PathExists $solutionFile "WPF solution"
Assert-PathExists $coreTests "core console test harness"
Assert-PathExists $wpfTests "WPF console test harness"
Assert-PathExists $licenseFile "licence file"
Assert-PathExists $noticeFile "notice file"
Assert-PathExists $userGuideFile "user guide"
Assert-PathExists $shortcutIconFile "shortcut icon"
Assert-PathExists $wpfProject "WPF project"
Assert-PathExists $coreProject "core project"

& $dependencyIndexScript -Check

$solutionText = Get-Content -LiteralPath $solutionFile -Raw
if ($solutionText -notmatch [regex]::Escape("tests\AIArena.Tests\AIArena.Tests.csproj")) {
    throw "WPF solution does not include the core console test harness."
}
if ($solutionText -notmatch [regex]::Escape("tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj")) {
    throw "WPF solution does not include the WPF console test harness."
}

dotnet run --project $coreTests --no-restore
dotnet run --project $wpfTests --no-restore

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
if ($scriptText -notmatch '\[Components\]' -or $scriptText -notmatch 'Name: "searxng"; Description: "Local web search engine \(SearXNG, AGPL-3\.0\)"') {
    throw "Installer no longer exposes SearXNG as an install component."
}
if ($scriptText -notmatch 'Excludes: "searxng\\\*"') {
    throw "Installer base file rule should exclude SearXNG so the component can be unticked."
}
if ($scriptText -notmatch 'Components: searxng') {
    throw "Installer SearXNG payload files are not tied to the SearXNG component."
}
if ($scriptText -notmatch 'SearxngLicensePage' -or $scriptText -notmatch 'WizardIsComponentSelected\(''searxng''\)') {
    throw "Installer SearXNG AGPL licence gate is missing."
}
if ($scriptText -notmatch '\{param:SEARXNGLICENSE\|\}' -or $scriptText -notmatch "= 'accept'") {
    throw "Silent full installs must require explicit /SEARXNGLICENSE=accept acknowledgement."
}
if ($scriptText -match 'SW_HIDE') {
    throw "Installer should not spawn hidden cleanup helpers."
}
if ($scriptText -match 'schtasks|AI Arena SearXNG') {
    throw "Installer should not depend on the legacy scheduled-task SearXNG lifecycle."
}
if ($scriptText -notmatch '\[UninstallDelete\]' -or $scriptText -notmatch 'Type: filesandordirs; Name: "\{app\}\\searxng"') {
    throw "Installer should remove app-owned SearXNG runtime residue during uninstall."
}
if ($scriptText -notmatch 'StopBundledSearxng' -or $scriptText -notmatch 'ExecutablePath' -or $scriptText -notmatch '\{app\}\\searxng\\python') {
    throw "Installer should stop only the bundled app-managed SearXNG process tree on uninstall."
}
if ($scriptText -notmatch 'EscapePowerShellSingleQuoted') {
    throw "Installer must escape user-selected install paths before embedding them in the PowerShell cleanup command."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\LICENSE"; DestDir: "\{app\}"') {
    throw "Installer no longer installs LICENSE beside the app."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\NOTICE\.md"; DestDir: "\{app\}"') {
    throw "Installer no longer installs NOTICE.md beside the app."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\docs\\USER_GUIDE\.md"; DestDir: "\{app\}"') {
    throw "Installer no longer installs USER_GUIDE.md beside the app."
}
if ($scriptText -notmatch 'Filename: "\{app\}\\USER_GUIDE\.md"; Description: "Open user guide"; Flags: shellexec postinstall skipifsilent') {
    throw "Installer no longer offers the user guide at the end of setup."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\src\\AIArena\.Wpf\\Assets\\ai-arena-icon\.ico"; DestDir: "\{app\}"') {
    throw "Installer no longer installs the app icon beside the app."
}
if ($scriptText -notmatch 'DefaultDirName=\{localappdata\}\\Programs\\\{#MyAppName\}') {
    throw "Installer no longer separates program files from the per-user AI Arena data folder."
}
if ($scriptText -notmatch 'DisableDirPage=no') {
    throw "Installer no longer allows manual install directory selection."
}
if ($scriptText -notmatch 'UsePreviousAppDir=no') {
    throw "Installer may reuse an older path instead of the separated per-user program directory."
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
$expectedFileVersion = (($Version -replace '-.*$', '') + '.0')
if ($projectText -notmatch ('<FileVersion>' + [regex]::Escape($expectedFileVersion) + '</FileVersion>')) {
    throw "WPF project FileVersion drifted: expected $expectedFileVersion."
}

$coreProjectText = Get-Content -LiteralPath $coreProject -Raw
if ($coreProjectText -notmatch ('<Version>' + [regex]::Escape($Version) + '</Version>')) {
    throw "Core project Version drifted: expected $Version."
}
if ($coreProjectText -notmatch ('<InformationalVersion>' + [regex]::Escape($Version) + '</InformationalVersion>')) {
    throw "Core project InformationalVersion drifted: expected $Version."
}
if ($coreProjectText -notmatch ('<FileVersion>' + [regex]::Escape($expectedFileVersion) + '</FileVersion>')) {
    throw "Core project FileVersion drifted: expected $expectedFileVersion."
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
    '## AI Lab',
    '## Match Setup',
    '## Agent Performance',
    '## Licensing'
)) {
    if ($guideText -notmatch [regex]::Escape($requiredGuideSection)) {
        throw "User guide missing required section: $requiredGuideSection"
    }
}

$manifestText = Get-Content -LiteralPath $releaseManifest -Raw
$installerManifestText = Get-Content -LiteralPath $installerManifest -Raw
$releaseChangelogText = Get-Content -LiteralPath $releaseChangelog -Raw
$installerChangelogText = Get-Content -LiteralPath $changelog -Raw
$releaseGithubReleaseNotesText = Get-Content -LiteralPath $releaseGithubReleaseNotes -Raw
$installerGithubReleaseNotesText = Get-Content -LiteralPath $githubReleaseNotes -Raw
$releaseExeHash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
if ($manifestText -notmatch 'AI Arena Release Manifest') {
    throw "Release manifest missing title."
}
if ($manifestText -notmatch ('Version: ' + [regex]::Escape($Version))) {
    throw "Release manifest version drifted."
}
if ($manifestText -notmatch '(?m)^Self-contained: True\r?$') {
    throw "Installer releases must be self-contained."
}

$runtimeConfig = Get-Content -LiteralPath $releaseRuntimeConfig -Raw | ConvertFrom-Json
$runtimeOptionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
if ($runtimeOptionNames -notcontains 'includedFrameworks' -or $runtimeOptionNames -contains 'frameworks') {
    throw "Installer runtimeconfig must use includedFrameworks and must not request framework-dependent frameworks."
}
$includedFrameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { [string]$_.name })
foreach ($requiredFramework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    if ($includedFrameworkNames -notcontains $requiredFramework) {
        throw "Installer runtimeconfig is missing included framework: $requiredFramework"
    }
}
if ($manifestText -notmatch [regex]::Escape($releaseExeHash)) {
    throw "Release manifest does not include the release executable hash."
}
if ($manifestText -notmatch [regex]::Escape("searxng\python\pythonw.exe")) {
    throw "Release manifest does not include the bundled SearXNG Python runtime."
}
if ($manifestText -notmatch [regex]::Escape("searxng\settings.yml")) {
    throw "Release manifest does not include the bundled SearXNG settings."
}
if ($manifestText -notmatch [regex]::Escape("searxng\runtime\arena_searxng_wsgi.py")) {
    throw "Release manifest does not include the AI Arena SearXNG JSON API boundary."
}
if ($manifestText -notmatch [regex]::Escape("searxng\payload-inventory.json")) {
    throw "Release manifest does not include the bundled SearXNG payload inventory."
}
if ($manifestText -notmatch [regex]::Escape("release-checksums.sha256")) {
    throw "Release manifest does not include the machine-readable release checksums."
}
if ($installerManifestText -ne $manifestText) {
    throw "Installer release manifest copy does not match release manifest."
}
if ($installerChangelogText -ne $releaseChangelogText) {
    throw "Installer changelog copy does not match release changelog."
}
if ($installerGithubReleaseNotesText -ne $releaseGithubReleaseNotesText) {
    throw "Installer GitHub release notes copy does not match release GitHub release notes."
}

$installerReleaseChecksums = Join-Path $installerDir 'release-checksums.sha256'
if ((Get-Content -LiteralPath $installerReleaseChecksums -Raw) -ne (Get-Content -LiteralPath $releaseChecksums -Raw)) {
    throw "Installer release-checksum copy does not match the release checksum manifest."
}
if ((Get-Content -LiteralPath $installerReleaseSigningReport -Raw) -ne (Get-Content -LiteralPath $releaseSigningReport -Raw)) {
    throw "Installer release-signing copy does not match the release signing report."
}

[void](Test-AIArenaSha256Manifest `
    -BaseDirectory $releaseDir `
    -ManifestPath $releaseChecksums `
    -ExcludeRelativePath @('release-manifest.txt'))
[void](Test-AIArenaSha256Manifest -BaseDirectory $releaseDir -ManifestPath $releaseManifest -AllowPreamble)
[void](Test-AIArenaSha256Manifest -BaseDirectory $installerDir -ManifestPath $installerChecksums)

$upstreamLock = Get-Content -LiteralPath $upstreamLockFile -Raw | ConvertFrom-Json
$payloadInventory = Get-Content -LiteralPath $searxngPayloadInventory -Raw | ConvertFrom-Json
if ($upstreamLock.schemaVersion -ne 1 -or $payloadInventory.formatVersion -ne 1) {
    throw "Unsupported upstream-lock or payload-inventory schema."
}
$upstreamLockHash = (Get-FileHash -LiteralPath $upstreamLockFile -Algorithm SHA256).Hash.ToUpperInvariant()
$bundledUpstreamLockHash = (Get-FileHash -LiteralPath $searxngUpstreamLock -Algorithm SHA256).Hash.ToUpperInvariant()
if ($bundledUpstreamLockHash -ne $upstreamLockHash `
    -or $payloadInventory.upstreamLock.path -ne 'UPSTREAM-LOCK.json' `
    -or $payloadInventory.upstreamLock.sha256 -ne $upstreamLockHash) {
    throw "Payload inventory was not built from the reviewed upstream lock."
}
$dependencyLockHash = (Get-FileHash -LiteralPath $dependencyLockFile -Algorithm SHA256).Hash.ToUpperInvariant()
$bundledDependencyLockHash = (Get-FileHash -LiteralPath $searxngDependencyLock -Algorithm SHA256).Hash.ToUpperInvariant()
if ($dependencyLockHash -ne $upstreamLock.pythonDependencies.sha256 `
    -or $bundledDependencyLockHash -ne $dependencyLockHash `
    -or $payloadInventory.dependencyLock.path -ne 'PYTHON-REQUIREMENTS-LOCK.txt' `
    -or $payloadInventory.dependencyLock.sha256 -ne $dependencyLockHash `
    -or $payloadInventory.dependencyLock.platform -ne 'win_amd64' `
    -or $payloadInventory.dependencyLock.pythonAbi -ne 'cp311') {
    throw "Payload Python dependency lock does not match the reviewed Windows CPython lock."
}
if ($payloadInventory.payload.pythonVersion -ne $upstreamLock.python.version `
    -or $payloadInventory.payload.searxngRevision -ne $upstreamLock.searxng.revision `
    -or $payloadInventory.payload.granianVersion -ne $upstreamLock.granian.version) {
    throw "Payload component versions do not match the reviewed upstream lock."
}

$pythonArchive = @($payloadInventory.upstreamArchives | Where-Object { $_.name -eq 'CPython embeddable Windows runtime' })
$searxngArchive = @($payloadInventory.upstreamArchives | Where-Object { $_.name -eq 'SearXNG source' })
if ($pythonArchive.Count -ne 1 `
    -or $pythonArchive[0].url -ne $upstreamLock.python.url `
    -or $pythonArchive[0].sha256 -ne $upstreamLock.python.sha256) {
    throw "Payload Python archive identity does not match the reviewed upstream lock."
}
if ($searxngArchive.Count -ne 1 `
    -or $searxngArchive[0].url -ne $upstreamLock.searxng.url `
    -or $searxngArchive[0].sha256 -ne $upstreamLock.searxng.sha256) {
    throw "Payload SearXNG archive identity does not match the reviewed upstream lock."
}
if (-not @($payloadInventory.packages | Where-Object { $_.name -eq 'granian' -and $_.version -eq $upstreamLock.granian.version }).Count) {
    throw "Payload package inventory does not include the pinned Granian version."
}
foreach ($package in $payloadInventory.packages) {
    Assert-AIArenaHttpsUri -Value ([string]$package.archiveUrl) -Label "Payload package URL for $($package.name)"
    Assert-AIArenaSha256 -Value ([string]$package.archiveSha256) -Label "Payload package hash for $($package.name)"
}

$inventoryRecords = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $payloadInventory.files) {
    $relative = ([string]$record.path -replace '/', '\').TrimStart('\')
    if ([string]::IsNullOrWhiteSpace($relative) -or $inventoryRecords.ContainsKey($relative)) {
        throw "Payload inventory contains an empty or duplicate file path: $relative"
    }
    Assert-AIArenaSha256 -Value ([string]$record.sha256) -Label "Payload file hash for $relative"
    $inventoryRecords.Add($relative, $record)
}

$actualPayloadFiles = @(Get-ChildItem -LiteralPath $searxngDir -File -Recurse |
    Where-Object { $_.FullName -ne $searxngPayloadInventory })
if ($inventoryRecords.Count -ne $actualPayloadFiles.Count) {
    throw "Payload inventory file count mismatch. Expected $($actualPayloadFiles.Count), found $($inventoryRecords.Count)."
}
foreach ($file in $actualPayloadFiles) {
    $relative = $file.FullName.Substring($searxngDir.Length).TrimStart('\', '/')
    if (-not $inventoryRecords.ContainsKey($relative)) {
        throw "Payload inventory is missing: $relative"
    }
    $record = $inventoryRecords[$relative]
    if ([long]$record.bytes -ne $file.Length) {
        throw "Payload inventory byte length mismatch for $relative."
    }
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    if ([string]$record.sha256 -ne $actualHash) {
        throw "Payload inventory SHA-256 mismatch for $relative."
    }
}

$releaseSigning = Get-Content -LiteralPath $releaseSigningReport -Raw | ConvertFrom-Json
$installerSigning = Get-Content -LiteralPath $installerSigningReport -Raw | ConvertFrom-Json
if ($releaseSigning.formatVersion -ne 1 -or $installerSigning.formatVersion -ne 1) {
    throw "Unsupported release or installer signing-report schema."
}
$recordedPolicy = [string]$releaseSigning.policy
if ($recordedPolicy -notin @('Optional', 'Required', 'Disabled') -or [string]$installerSigning.policy -ne $recordedPolicy) {
    throw "Release and installer signing policies are invalid or inconsistent."
}
if (-not [string]::IsNullOrWhiteSpace($SigningPolicy) -and $SigningPolicy -ne $recordedPolicy) {
    throw "Requested signing policy '$SigningPolicy' does not match recorded policy '$recordedPolicy'."
}
if ([bool]$releaseSigning.signingEnabled -ne [bool]$installerSigning.signingEnabled) {
    throw "Release and installer signing-enabled records are inconsistent."
}

$releaseSignature = Get-AuthenticodeSignature -LiteralPath $releaseExe
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
$releaseArtifactRecord = @($releaseSigning.artifacts | Where-Object { $_.path -eq 'AI Arena.exe' })
$installerArtifactRecord = @($installerSigning.artifacts | Where-Object { $_.location -eq 'installer' })
if ($releaseArtifactRecord.Count -ne 1 -or $releaseArtifactRecord[0].status -ne $releaseSignature.Status.ToString()) {
    throw "Release executable signature status does not match the signing report."
}
if ($installerArtifactRecord.Count -ne 1 -or $installerArtifactRecord[0].status -ne $installerSignature.Status.ToString()) {
    throw "Installer signature status does not match the signing report."
}
foreach ($signature in @($releaseSignature, $installerSignature)) {
    if ($signature.Status -notin @(
        [System.Management.Automation.SignatureStatus]::Valid,
        [System.Management.Automation.SignatureStatus]::NotSigned)) {
        throw "Release artifact has an unacceptable Authenticode status: $($signature.Status)."
    }
}
if ([bool]$releaseSigning.signingEnabled -or $recordedPolicy -eq 'Required') {
    if ($releaseSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid `
        -or $installerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Signing policy '$recordedPolicy' requires valid Authenticode signatures on the app and installer."
    }
    if ($null -eq $releaseSignature.TimeStamperCertificate -or $null -eq $installerSignature.TimeStamperCertificate) {
        throw "Signed app and installer must have RFC 3161 timestamp countersignatures."
    }
    if ([string]::IsNullOrWhiteSpace([string]$releaseSigning.certificateThumbprint) `
        -or $releaseSignature.SignerCertificate.Thumbprint -ne $releaseSigning.certificateThumbprint `
        -or $installerSignature.SignerCertificate.Thumbprint -ne $releaseSigning.certificateThumbprint) {
        throw "App and installer signer certificates do not match the signing report."
    }
}

$looseInstallers = @(Get-ChildItem -LiteralPath (Join-Path $Root "dist/installer") -Filter "*.exe" -File -ErrorAction SilentlyContinue)
if ($looseInstallers.Count -gt 0) {
    throw "Loose installer exe files remain in dist/installer."
}

$installerInfo = Get-Item -LiteralPath $installer
if ($installerInfo.Length -le 0) {
    throw "Installer exists but is empty: $installer"
}

Write-Host "WPF release sanity passed for AI Arena $Version"
Write-Host $installer
