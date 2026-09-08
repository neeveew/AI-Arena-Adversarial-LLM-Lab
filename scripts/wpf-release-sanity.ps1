param(
    [string]$Version = "0.4.140-beta",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$SigningPolicy = "",
    [string]$VerificationReceiptPath = "",
    [string]$VerificationReceiptKey = "",
    [string]$InstallerCompileReceiptPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$productDisplayName = 'AI Arena - Lite: Adversarial LLM Lab'
$productShortDisplayName = 'AI Arena - Lite'
$productEdition = 'lite'
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $Root "scripts/release-security.ps1")
Assert-AIArenaReleaseVersion -Version $Version
if (-not [string]::IsNullOrWhiteSpace($SigningPolicy) -and $SigningPolicy -notin @('Optional', 'Required', 'Disabled')) {
    throw "SigningPolicy must be Optional, Required, Disabled, or omitted."
}

$innoScript = Join-Path $Root "packaging/inno/ai-arena-wpf.iss"
$perUserMigrationScript = Join-Path $Root "packaging/inno/migrate-ai-arena-per-user.ps1"
$releaseDir = Join-Path $Root "dist/AI Arena - $Version"
$installerDir = Join-Path $Root "dist/installer/AI Arena - $Version"
$installer = Join-Path $installerDir "AI Arena Setup $Version.exe"
$changelog = Join-Path $installerDir "changelog.md"
$changes = Join-Path $installerDir "changes.txt"
$githubReleaseNotes = Join-Path $installerDir "github-release-notes.md"
$releaseManifest = Join-Path $releaseDir "release-manifest.txt"
$installerManifest = Join-Path $installerDir "release-manifest.txt"
$releaseChangelog = Join-Path $releaseDir "changelog.md"
$releaseChanges = Join-Path $releaseDir "changes.txt"
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
$xamlBaselineScript = Join-Path $Root "scripts/xaml-hardcoded-values.ps1"
$xamlBaselineTests = Join-Path $Root "scripts/tests/xaml-hardcoded-values.tests.ps1"
$installerMigrationTests = Join-Path $Root "scripts/tests/installer-migration.tests.ps1"
$userGuideContentScript = Join-Path $Root "scripts/user-guide-content.ps1"
$solutionFile = Join-Path $Root "AI Arena - WPF.sln"
$coreTests = Join-Path $Root "tests/AIArena.Tests/AIArena.Tests.csproj"
$wpfTests = Join-Path $Root "tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj"
$licenseFile = Join-Path $Root "LICENSE"
$noticeFile = Join-Path $Root "NOTICE.md"
$readmeFile = Join-Path $Root "README.md"
$readmeEmblem = Join-Path $Root "docs/assets/ai-arena-lite-emblem.png"
$brandAssets = @(
    [pscustomobject]@{ Label = 'authoritative Lite emblem'; Path = (Join-Path $Root 'src/AIArena.Wpf/Assets/ai-arena-emblem-lite-green.png'); Sha256 = '805B370FF6A6479C461A9D716486B0A56A2C23AE29790E74853159CFAA9BF525' },
    [pscustomobject]@{ Label = 'Lite icon PNG'; Path = (Join-Path $Root 'src/AIArena.Wpf/Assets/ai-arena-icon.png'); Sha256 = '64CCFFB9036BECD8A1F62692BCC8841CA9B51B92C21A362AB643D2E94955420A' },
    [pscustomobject]@{ Label = 'Lite Windows icon'; Path = (Join-Path $Root 'src/AIArena.Wpf/Assets/ai-arena-icon.ico'); Sha256 = 'CC38F295D83163793C2FD50D6954F9775F503EDB90935954CE5D62EB144168C0' },
    [pscustomobject]@{ Label = 'Lite Help icon'; Path = (Join-Path $Root 'src/AIArena.Wpf/Assets/ai-arena-guide-icon.png'); Sha256 = '225B8A8A2A8373409803B429537A6C24F04C880C8FA1420FAD0B7537C7E3C193' },
    [pscustomobject]@{ Label = 'Lite navigation emblem'; Path = (Join-Path $Root 'src/AIArena.Wpf/Assets/ai-arena-emblem-lite.png'); Sha256 = '9F24EC85E1DB814542BA2D7920D6D03F7266B8889D3E57289389395DC6943006' },
    [pscustomobject]@{ Label = 'Lite README emblem'; Path = $readmeEmblem; Sha256 = '34C8E449D9E8F2B20E82F3B26E16AECA97634E45272A6C276C6E256C4C61A89F' }
)
$userGuideFile = Join-Path $Root "docs/USER_GUIDE.md"
$helpContentRoot = Join-Path $Root "src/AIArena.Wpf/Help/Content"
$helpManifestFile = Join-Path $helpContentRoot "guide-manifest.json"
$releaseHelpContentRoot = Join-Path $releaseDir "Help/Content"
$releaseHelpManifest = Join-Path $releaseHelpContentRoot "guide-manifest.json"
$shortcutIconFile = Join-Path $Root "src/AIArena.Wpf/Assets/ai-arena-icon.ico"
$wpfProject = Join-Path $Root "src/AIArena.Wpf/AIArena.Wpf.csproj"
$coreProject = Join-Path $Root "src/AIArena.Core/AIArena.Core.csproj"
$installerCompileReceipt = $null
if ([string]::IsNullOrWhiteSpace($InstallerCompileReceiptPath)) {
    $InstallerCompileReceiptPath = Join-Path $Root ("artifacts/release-verification/installer-compile-{0}.json" -f $Version)
}

function Assert-PathExists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing ${Label}: $Path"
    }
}

function Get-HelpContentInventory {
    param([string]$Directory)

    $allowedExtensions = @('.json', '.md', '.png', '.jpg', '.jpeg', '.webp')
    $resolvedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd([char[]]@('\', '/'))
    $directoryPrefix = $resolvedDirectory + [IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File)
    $unsupported = @($files | Where-Object { $allowedExtensions -notcontains $_.Extension.ToLowerInvariant() })
    if ($unsupported.Count -gt 0) {
        throw "Help content contains unsupported packaged files: $($unsupported.FullName -join ', ')."
    }

    return @($files |
        ForEach-Object {
            $fullPath = [IO.Path]::GetFullPath($_.FullName)
            if (-not $fullPath.StartsWith($directoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Help content inventory escaped its root: $fullPath"
            }
            $fullPath.Substring($directoryPrefix.Length).Replace('\', '/')
        } |
        Sort-Object -CaseSensitive)
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
Assert-PathExists $releaseChanges "release changes file"
Assert-PathExists $releaseGithubReleaseNotes "release GitHub release notes"
Assert-PathExists $dependencyIndexScript "dependency index script"
Assert-PathExists $xamlBaselineScript "XAML hard-coded baseline script"
Assert-PathExists $xamlBaselineTests "XAML hard-coded baseline fixture tests"
Assert-PathExists $userGuideContentScript "User Guide content validation script"
Assert-PathExists $solutionFile "WPF solution"
Assert-PathExists $coreTests "core console test harness"
Assert-PathExists $wpfTests "WPF console test harness"
Assert-PathExists $licenseFile "licence file"
Assert-PathExists $noticeFile "notice file"
Assert-PathExists $readmeFile "readme"
Assert-PathExists $readmeEmblem "README Lite emblem"
foreach ($brandAsset in $brandAssets) {
    Assert-PathExists $brandAsset.Path $brandAsset.Label
    $actualBrandHash = (Get-FileHash -LiteralPath $brandAsset.Path -Algorithm SHA256).Hash
    if ($actualBrandHash -cne $brandAsset.Sha256) {
        throw "Lite brand asset hash drifted for $($brandAsset.Label): expected $($brandAsset.Sha256), got $actualBrandHash. Regenerate and review the complete asset family."
    }
}
Assert-PathExists $userGuideFile "user guide"
Assert-PathExists $helpContentRoot "structured Help content"
Assert-PathExists $helpManifestFile "structured Help manifest"
Assert-PathExists $releaseHelpContentRoot "published Help content"
Assert-PathExists $releaseHelpManifest "published Help manifest"
Assert-PathExists $shortcutIconFile "shortcut icon"
Assert-PathExists $wpfProject "WPF project"
Assert-PathExists $coreProject "core project"

if ([IO.Path]::GetFileName($releaseExe) -cne 'AI Arena.exe') {
    throw "Release executable filename drifted from the compatibility-stable AI Arena.exe name."
}
$releaseFileVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($releaseExe)
if ($releaseFileVersionInfo.ProductName -cne $productDisplayName) {
    throw "Release executable ProductName drifted: expected $productDisplayName, got '$($releaseFileVersionInfo.ProductName)'."
}
if ($releaseFileVersionInfo.FileDescription -cne $productShortDisplayName) {
    throw "Release executable FileDescription drifted: expected $productShortDisplayName, got '$($releaseFileVersionInfo.FileDescription)'."
}

$installerCompileReceipt = Test-AIArenaInstallerCompileReceipt `
    -RepositoryRoot $Root `
    -ReceiptPath $InstallerCompileReceiptPath `
    -InstallerPath $installer `
    -ReleaseInventoryPath $releaseChecksums `
    -InnoScriptPath $innoScript `
    -Version $Version `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SigningPolicy $SigningPolicy

& $userGuideContentScript -Check
$userGuideContentExitCode = $LASTEXITCODE
if ($userGuideContentExitCode -ne 0) {
    throw "User Guide content validation failed with exit code $userGuideContentExitCode."
}

$sourceHelpInventory = @(Get-HelpContentInventory -Directory $helpContentRoot)
$releaseHelpInventory = @(Get-HelpContentInventory -Directory $releaseHelpContentRoot)
$helpInventoryDelta = @(Compare-Object -ReferenceObject $sourceHelpInventory -DifferenceObject $releaseHelpInventory -CaseSensitive)
if ($sourceHelpInventory.Count -eq 0 -or $helpInventoryDelta.Count -gt 0) {
    throw "Published Help/Content inventory does not exactly match the structured source. Differences: $($helpInventoryDelta | Out-String)"
}
foreach ($relativePath in $sourceHelpInventory) {
    $sourceHelpHash = (Get-FileHash -LiteralPath (Join-Path $helpContentRoot $relativePath) -Algorithm SHA256).Hash
    $releaseHelpHash = (Get-FileHash -LiteralPath (Join-Path $releaseHelpContentRoot $relativePath) -Algorithm SHA256).Hash
    if ($sourceHelpHash -ne $releaseHelpHash) {
        throw "Published Help file differs from source: $relativePath"
    }
}

$helpManifest = Get-Content -LiteralPath $helpManifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$helpManifest.guideVersion -ne $Version) {
    throw "Help manifest guideVersion drifted: expected $Version."
}

& $dependencyIndexScript -Check
& $xamlBaselineScript -Check
& $xamlBaselineTests
$xamlBaselineTestExitCode = $LASTEXITCODE
if ($xamlBaselineTestExitCode -ne 0) {
    throw "XAML hard-coded baseline fixture tests failed with exit code $xamlBaselineTestExitCode."
}
& $installerMigrationTests
$installerMigrationTestExitCode = $LASTEXITCODE
if ($installerMigrationTestExitCode -ne 0) {
    throw "Installer scope-migration fixture tests failed with exit code $installerMigrationTestExitCode."
}

$solutionText = Get-Content -LiteralPath $solutionFile -Raw
if ($solutionText -notmatch [regex]::Escape("tests\AIArena.Tests\AIArena.Tests.csproj")) {
    throw "WPF solution does not include the core console test harness."
}
if ($solutionText -notmatch [regex]::Escape("tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj")) {
    throw "WPF solution does not include the WPF console test harness."
}

if ((-not [string]::IsNullOrWhiteSpace($VerificationReceiptPath)) `
    -ne (-not [string]::IsNullOrWhiteSpace($VerificationReceiptKey))) {
    throw 'VerificationReceiptPath and its ephemeral VerificationReceiptKey must be supplied together.'
}
if (-not [string]::IsNullOrWhiteSpace($VerificationReceiptPath)) {
    [void](Test-AIArenaReleaseVerificationReceipt `
        -RepositoryRoot $Root `
        -ReceiptPath $VerificationReceiptPath `
        -ReceiptKey $VerificationReceiptKey `
        -Configuration $Configuration `
        -ProjectPath @($coreTests, $wpfTests))
    Write-Host "Authenticated release harness receipt matches this pipeline run, source, configuration, runner, and test assemblies."
}
else {
    # Standalone sanity remains fail-closed: without the pipeline-internal
    # receipt and its ephemeral key it independently executes both harnesses.
    dotnet run --project $coreTests -c $Configuration --no-build --no-restore
    $coreTestExitCode = $LASTEXITCODE
    if ($coreTestExitCode -ne 0) {
        throw "Core console test harness failed with exit code $coreTestExitCode."
    }

    dotnet run --project $wpfTests -c $Configuration --no-build --no-restore
    $wpfTestExitCode = $LASTEXITCODE
    if ($wpfTestExitCode -ne 0) {
        throw "WPF console test harness failed with exit code $wpfTestExitCode."
    }
}

$scriptText = Get-Content -LiteralPath $innoScript -Raw
if ($scriptText -notmatch '#define MyAppName "AI Arena"') {
    throw "Installer compatibility identity drifted: expected MyAppName to remain AI Arena."
}
if ($scriptText -notmatch ('#define MyAppShortDisplayName "' + [regex]::Escape($productShortDisplayName) + '"')) {
    throw "Installer short display identity drifted: expected $productShortDisplayName."
}
if ($scriptText -notmatch ('#define MyAppDisplayName "' + [regex]::Escape($productDisplayName) + '"')) {
    throw "Installer display identity drifted: expected $productDisplayName."
}
if ($scriptText -notmatch '#define MyAppExeName "AI Arena\.exe"') {
    throw "Installer executable compatibility identity drifted: expected AI Arena.exe."
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
if ($scriptText -notmatch 'OutputBaseFilename=AI Arena Setup \{#MyAppVersion\}') {
    throw "Installer artifact filename drifted from the compatibility-stable AI Arena Setup name."
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
$hiddenWindowUses = [regex]::Matches($scriptText, '\bSW_HIDE\b')
if ($hiddenWindowUses.Count -ne 1 `
    -or $scriptText -notmatch '(?s)ExecAsOriginalUser\(.{0,700}\bSW_HIDE\b') {
    throw "Only the hash-pinned original-user migration helper may run hidden."
}
if ($scriptText -match 'schtasks|AI Arena SearXNG') {
    throw "Installer should not depend on the legacy scheduled-task SearXNG lifecycle."
}
if ($scriptText -notmatch '\[UninstallDelete\]' -or $scriptText -notmatch 'Type: filesandordirs; Name: "\{app\}\\searxng"') {
    throw "Installer should remove app-owned SearXNG runtime residue during uninstall."
}
if ($scriptText -notmatch 'StopBundledSearxng' -or $scriptText -notmatch 'ExecutablePath' -or $scriptText -notmatch '\{app\}\\searxng\\python' `
    -or $scriptText -notmatch '(?s)procedure StopBundledSearxng;.{0,1600}\bSW_SHOWNORMAL\b') {
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
if ($scriptText -notmatch '#define MyAppIconName "ai-arena-lite-icon\.ico"') {
    throw "Installer shortcut icon no longer has a Lite-specific cache identity."
}
if ($scriptText -notmatch 'Source: "\.\.\\\.\.\\src\\AIArena\.Wpf\\Assets\\ai-arena-icon\.ico"; DestDir: "\{app\}"; DestName: "\{#MyAppIconName\}"') {
    throw "Installer no longer installs the reviewed app icon under its Lite-specific shortcut name."
}
if ($scriptText -notmatch 'UninstallDisplayIcon=\{app\}\\\{#MyAppIconName\}') {
    throw "Installer uninstall metadata no longer uses the Lite-specific installed icon."
}
if ($scriptText -notmatch 'DefaultDirName=\{autopf\}\\AI Arena Lite') {
    throw "Installer no longer uses the fixed Program Files/AI Arena Lite machine directory."
}
if ($scriptText -notmatch 'DefaultGroupName=\{#MyAppShortDisplayName\}') {
    throw "Installer Start Menu group no longer uses the Lite display name."
}
if ($scriptText -notmatch 'DisableDirPage=yes') {
    throw "Installer must keep the machine install directory fixed so upgrades cannot fork into custom payload locations."
}
if ($scriptText -notmatch 'UsePreviousAppDir=no') {
    throw "Installer may reuse the previous per-user directory instead of the fixed machine directory."
}
if ($scriptText -notmatch 'UsePreviousGroup=no') {
    throw "Installer may retain the pre-Lite Start Menu group instead of adopting the Lite group."
}
if ($scriptText -notmatch 'ArchitecturesAllowed=x64compatible' `
    -or $scriptText -notmatch 'ArchitecturesInstallIn64BitMode=x64compatible') {
    throw "The win-x64 installer must require an x64-compatible OS and use 64-bit Program Files."
}
if ($scriptText -notmatch 'PrivilegesRequired=admin') {
    throw "The Program Files installer must use administrative machine-install mode."
}
if ($scriptText -notmatch 'Name: "\{autodesktop\}\\\{#MyAppShortDisplayName\}".*IconFilename: "\{app\}\\\{#MyAppIconName\}"') {
    throw "Machine-scope desktop shortcut no longer has an explicit icon."
}
if ($scriptText -notmatch 'Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"\s*(?:\r?\n)' `
    -or $scriptText -match 'Name: "desktopicon"[^\r\n]*Flags:\s*unchecked') {
    throw "Lite desktop shortcut must be selected by default during the per-user-to-machine migration."
}
if ($scriptText -notmatch 'Name: "\{group\}\\\{#MyAppShortDisplayName\}".*IconFilename: "\{app\}\\\{#MyAppIconName\}"') {
    throw "Start Menu shortcut no longer has an explicit icon."
}
if ($scriptText -notmatch 'Name: "\{group\}\\\{#MyAppShortDisplayName\} User Guide"; Filename: "\{app\}\\USER_GUIDE\.md"') {
    throw "Start Menu user guide shortcut is missing."
}
if ($scriptText -match '\{user(?:desktop|programs)\}' -or $scriptText -match '\{localappdata\}') {
    throw "Administrative installer must not directly read, write, or delete per-user shell or data locations."
}
if ($scriptText -match 'Type:\s*(?:files|filesandordirs);\s*Name:\s*"\{autodesktop\}\\AI Arena\.lnk"') {
    throw "Lite installer must never delete the public AI Arena desktop shortcut owned by a sibling branch."
}
if (-not (Test-Path -LiteralPath $perUserMigrationScript -PathType Leaf)) {
    throw "Per-user-to-machine installer migration helper is missing."
}
$migrationText = Get-Content -LiteralPath $perUserMigrationScript -Raw
$migrationSha256 = (Get-FileHash -LiteralPath $perUserMigrationScript -Algorithm SHA256).Hash
if ($scriptText -notmatch ('#define MyPerUserMigrationSha256 "' + [regex]::Escape($migrationSha256) + '"')) {
    throw "Per-user migration helper SHA-256 drifted from the Inno-script pin used by installer compile receipts."
}
if ($scriptText -notmatch 'Source: "migrate-ai-arena-per-user\.ps1"; Flags: dontcopy noencryption' `
    -or $scriptText -notmatch 'ExecAsOriginalUser\(' `
    -or $scriptText -notmatch "ExtractTemporaryFile\('migrate-ai-arena-per-user\.ps1'\)" `
    -or $scriptText -notmatch "GetSHA256OfFile\(MigrationScript\), '\{#MyPerUserMigrationSha256\}'" `
    -or $scriptText -notmatch 'ComputeHash\(\$bytes\)' `
    -or $scriptText -notmatch 'ScriptBlock\]::Create\(\$text\)' `
    -or $scriptText -notmatch '-ExecutionPolicy RemoteSigned' `
    -or $scriptText -notmatch 'SW_HIDE') {
    throw "Installer no longer hash-verifies and runs the exact per-user migration bytes under the original user token."
}
if ($migrationText -notmatch [regex]::Escape('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61}_is1') `
    -or $migrationText -notmatch [regex]::Escape("Join-Path `$env:LOCALAPPDATA 'Programs\AI Arena'") `
    -or $migrationText -notmatch "unins\[0-9\]\{3\}\\\.exe" `
    -or $migrationText -notmatch "'/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'") {
    throw "Per-user migration no longer validates and silently removes only the exact legacy AppId/default install."
}
$elevationGuardIndex = $migrationText.IndexOf('if (Test-AIArenaProcessElevated)', [StringComparison]::Ordinal)
$liveRegistryReadIndex = $migrationText.IndexOf("`$legacyUninstallKey = 'HKCU:\Software", [StringComparison]::Ordinal)
if ($elevationGuardIndex -lt 0 -or $liveRegistryReadIndex -le $elevationGuardIndex `
    -or $scriptText -notmatch '(?s)24:\s*Result :=.{0,500}do not use Run as administrator') {
    throw "Migration must reject an elevated helper token before reading HKCU or launching a user-writable legacy uninstaller."
}
if ($scriptText -match 'DelTree\([^\r\n]*\{localappdata\}' `
    -or $scriptText -match 'Also delete AI Arena - Lite saved sessions') {
    throw "Machine-scope uninstaller must preserve the compatibility-stable per-user AI Arena data root."
}
if ($scriptText -notmatch 'Name: "\{group\}\\Release Notes"; Filename: "\{app\}\\changes\.txt"') {
    throw "Start Menu release notes shortcut is missing."
}
if ($scriptText -notmatch 'Name: "\{group\}\\GitHub Releases"; Filename: "\{#MyReleaseUrl\}"') {
    throw "Start Menu GitHub releases shortcut is missing."
}

$projectText = Get-Content -LiteralPath $wpfProject -Raw
if ($projectText -notmatch '<AssemblyName>AI Arena</AssemblyName>') {
    throw "WPF assembly compatibility identity drifted; executable and pack-resource names must remain AI Arena."
}
if ($projectText -notmatch '<ApplicationIcon>Assets\\ai-arena-icon\.ico</ApplicationIcon>') {
    throw "WPF executable no longer embeds the reviewed Lite icon source."
}
if ($projectText -notmatch ('<Product>' + [regex]::Escape($productDisplayName) + '</Product>') `
    -or $projectText -notmatch ('<AssemblyTitle>' + [regex]::Escape($productShortDisplayName) + '</AssemblyTitle>')) {
    throw "WPF PE metadata declarations do not expose the full Lite product name and short assembly title."
}
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
if ($guideText -notmatch ('(?m)^# ' + [regex]::Escape($productShortDisplayName) + ' Help Center\r?$') `
    -or $guideText -notmatch [regex]::Escape($productDisplayName)) {
    throw "User guide does not identify the current Lite product and Help Center."
}
foreach ($requiredGuideSection in @(
    '## Quick Start: Your First Turn',
    '## Run Arena Mode',
    '## Match Setup',
    '## Agent Performance',
    '## Reference, Installation & Licensing'
)) {
    if ($guideText -notmatch [regex]::Escape($requiredGuideSection)) {
        throw "User guide missing required section: $requiredGuideSection"
    }
}

# The README download link is the front door. It is edited by hand during the
# version bump, so it can name a version the release never produced, and readers
# land on a tag that does not exist.
$readmeText = Get-Content -LiteralPath $readmeFile -Raw
if ($readmeText -notmatch ('(?m)^# ' + [regex]::Escape($productDisplayName) + '\r?$') `
    -or $readmeText -notmatch 'docs/assets/ai-arena-lite-emblem\.png') {
    throw "README does not expose the current Lite product name and emblem masthead."
}
$readmeDownload = [regex]::Match($readmeText, '\[Download\s+(?<label>[^\]]+)\]\(\s*(?<url>[^)\s]+)\s*\)')
if (-not $readmeDownload.Success) {
    throw "README no longer advertises a download link; the release version cannot be checked."
}
$readmeLabel = $readmeDownload.Groups['label'].Value.Trim()
if ($readmeLabel -ne $Version) {
    throw "README advertises download '$readmeLabel' but this release is $Version."
}
$expectedReleaseUrl = "https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases/tag/v$Version"
if ($readmeDownload.Groups['url'].Value.Trim() -ne $expectedReleaseUrl) {
    throw "README download link does not point at $expectedReleaseUrl."
}

$manifestText = Get-Content -LiteralPath $releaseManifest -Raw
$releaseChecksumsText = Get-Content -LiteralPath $releaseChecksums -Raw
$installerManifestText = Get-Content -LiteralPath $installerManifest -Raw
$releaseChangelogText = Get-Content -LiteralPath $releaseChangelog -Raw
$installerChangelogText = Get-Content -LiteralPath $changelog -Raw
$releaseChangesText = Get-Content -LiteralPath $releaseChanges -Raw
$installerChangesText = Get-Content -LiteralPath $changes -Raw
$releaseGithubReleaseNotesText = Get-Content -LiteralPath $releaseGithubReleaseNotes -Raw
$installerGithubReleaseNotesText = Get-Content -LiteralPath $githubReleaseNotes -Raw
$releaseExeHash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
if ($manifestText -notmatch 'AI Arena Release Manifest') {
    throw "Release manifest missing title."
}
if ($manifestText -notmatch ('(?m)^Product: ' + [regex]::Escape($productDisplayName) + '\r?$') `
    -or $manifestText -notmatch ('(?m)^Edition: ' + [regex]::Escape($productEdition) + '\r?$')) {
    throw "Release manifest does not identify the Lite product and edition."
}
if ($manifestText -notmatch ('Version: ' + [regex]::Escape($Version))) {
    throw "Release manifest version drifted."
}
if ($manifestText -notmatch '(?m)^Self-contained: True\r?$') {
    throw "Installer releases must be self-contained."
}

# A payload built before a later source commit ships under a tag whose tree it
# was never built from, and the checksums all still agree because they only ever
# described the payload. Compare the recorded source against the checkout.
$recordedCommit = [regex]::Match($manifestText, '(?m)^Source commit:\s*(?<value>\S+)\s*$')
$recordedSrcTree = [regex]::Match($manifestText, '(?m)^Source tree \(src\):\s*(?<value>.+?)\s*$')
if (-not $recordedCommit.Success -or -not $recordedSrcTree.Success) {
    throw "Release manifest does not record the source commit it was built from. Rebuild $Version with the current release scripts."
}
$builtCommit = $recordedCommit.Groups['value'].Value
$builtSrcTree = $recordedSrcTree.Groups['value'].Value
if ($builtCommit -eq 'unavailable' -or $builtSrcTree -eq 'unavailable') {
    throw "Release $Version was built outside a git checkout, so its source cannot be verified."
}
if ($builtSrcTree -like '*+dirty*') {
    throw "Release $Version was built from a modified src/ tree, so it matches no commit. Commit the changes and rebuild."
}
$currentSrcTree = (& git -C $Root rev-parse 'HEAD:src' 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($currentSrcTree)) {
    throw "Current src/ tree could not be read from git; release source cannot be verified."
}
$currentSrcTree = $currentSrcTree.Trim()
if ($currentSrcTree -ne $builtSrcTree) {
    throw ("Release $Version was built from src/ tree $builtSrcTree (commit $builtCommit), " +
        "but the checkout is now at $currentSrcTree. App source changed after the installer was built; " +
        "bump the version and rebuild so the release matches its tag.")
}
$dirtySrc = @(& git -C $Root status --porcelain -- src 2>$null | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$gitStatusExitCode = $LASTEXITCODE
if ($gitStatusExitCode -ne 0) {
    throw "Current src/ working-tree status could not be read from git; release source cannot be verified (exit code $gitStatusExitCode)."
}
if ($dirtySrc.Count -gt 0) {
    throw "src/ has uncommitted changes, so release $Version no longer matches the checkout."
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
if ($releaseChecksumsText -notmatch ('(?m)^' + [regex]::Escape($releaseExeHash) + '  AI Arena\.exe\r?$')) {
    throw "Release checksum inventory does not include the release executable hash."
}
if ($releaseChecksumsText -notmatch [regex]::Escape("searxng\python\pythonw.exe")) {
    throw "Release checksum inventory does not include the bundled SearXNG Python runtime."
}
if ($releaseChecksumsText -notmatch [regex]::Escape("searxng\settings.yml")) {
    throw "Release checksum inventory does not include the bundled SearXNG settings."
}
if ($releaseChecksumsText -notmatch [regex]::Escape("searxng\runtime\arena_searxng_wsgi.py")) {
    throw "Release checksum inventory does not include the AI Arena SearXNG JSON API boundary."
}
if ($releaseChecksumsText -notmatch [regex]::Escape("searxng\payload-inventory.json")) {
    throw "Release checksum inventory does not include the bundled SearXNG payload inventory."
}
$checksumInventoryHash = (Get-FileHash -LiteralPath $releaseChecksums -Algorithm SHA256).Hash.ToUpperInvariant()
$recordedChecksumInventoryHash = [regex]::Match($manifestText, '(?m)^Checksum inventory SHA256:\s*(?<value>[A-Fa-f0-9]{64})\s*$')
if (-not $recordedChecksumInventoryHash.Success `
    -or $recordedChecksumInventoryHash.Groups['value'].Value.ToUpperInvariant() -ne $checksumInventoryHash) {
    throw "Release manifest does not bind the canonical checksum inventory."
}
if ($installerManifestText -ne $manifestText) {
    throw "Installer release manifest copy does not match release manifest."
}
if ($installerChangelogText -ne $releaseChangelogText) {
    throw "Installer changelog copy does not match release changelog."
}
if ($installerChangesText -ne $releaseChangesText) {
    throw "Installer changes copy does not match release changes file."
}
if ($installerGithubReleaseNotesText -ne $releaseGithubReleaseNotesText) {
    throw "Installer GitHub release notes copy does not match release GitHub release notes."
}
foreach ($releaseBrandArtifact in @(
    [pscustomobject]@{ Label = 'release changelog'; Text = $releaseChangelogText },
    [pscustomobject]@{ Label = 'release changes file'; Text = $releaseChangesText },
    [pscustomobject]@{ Label = 'GitHub release notes'; Text = $releaseGithubReleaseNotesText }
)) {
    if ($releaseBrandArtifact.Text -notmatch ('(?m)^Product: ' + [regex]::Escape($productDisplayName) + '\r?$') `
        -or $releaseBrandArtifact.Text -notmatch ('(?m)^Edition: ' + [regex]::Escape($productEdition) + '\r?$')) {
        throw "$($releaseBrandArtifact.Label) does not identify the Lite product and edition."
    }
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
$releaseSigningEnabled = [bool](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'signingEnabled' -DefaultValue $false)
$installerSigningEnabled = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'signingEnabled' -DefaultValue $false)
$releaseCertificateThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'certificateThumbprint' -DefaultValue '')
$installerCertificateThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'certificateThumbprint' -DefaultValue '')
$releaseCertificateKeyAlgorithm = [string](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'certificateKeyAlgorithm' -DefaultValue '')
$installerCertificateKeyAlgorithm = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'certificateKeyAlgorithm' -DefaultValue '')
$releaseCertificateKeySize = [int](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'certificateKeySize' -DefaultValue 0)
$installerCertificateKeySize = [int](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'certificateKeySize' -DefaultValue 0)
$releaseCertificateChainTrusted = [bool](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'certificateChainTrusted' -DefaultValue $false)
$installerCertificateChainTrusted = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'certificateChainTrusted' -DefaultValue $false)
$releasePrivateKeyVerified = [bool](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'privateKeyVerified' -DefaultValue $false)
$installerPrivateKeyVerified = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'privateKeyVerified' -DefaultValue $false)
$releaseTimestampRequired = [bool](Get-AIArenaOptionalPropertyValue -InputObject $releaseSigning -Name 'timestampRequired' -DefaultValue $false)
$installerTimestampRequired = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerSigning -Name 'timestampRequired' -DefaultValue $false)
$recordedPolicy = [string]$releaseSigning.policy
if ($recordedPolicy -notin @('Optional', 'Required', 'Disabled') -or [string]$installerSigning.policy -ne $recordedPolicy) {
    throw "Release and installer signing policies are invalid or inconsistent."
}
if (-not [string]::IsNullOrWhiteSpace($SigningPolicy) -and $SigningPolicy -ne $recordedPolicy) {
    throw "Requested signing policy '$SigningPolicy' does not match recorded policy '$recordedPolicy'."
}
if ($releaseSigningEnabled -ne $installerSigningEnabled) {
    throw "Release and installer signing-enabled records are inconsistent."
}
Assert-AIArenaSigningPolicyState -Policy $recordedPolicy -SigningEnabled $releaseSigningEnabled
if ([string]$installerCompileReceipt.signing.policy -ne $recordedPolicy `
    -or [bool]$installerCompileReceipt.signing.enabled -ne $installerSigningEnabled `
    -or ($installerSigningEnabled -and [string]$installerCompileReceipt.signing.certificateThumbprint -ne [string]$installerSigning.certificateThumbprint)) {
    throw 'Installer compile receipt signing identity does not match the finalized signing reports.'
}
if ($releaseCertificateThumbprint -ne $installerCertificateThumbprint) {
    throw "Release and installer signing-certificate records are inconsistent."
}
if ($releaseCertificateKeyAlgorithm -ne $installerCertificateKeyAlgorithm `
    -or $releaseCertificateKeySize -ne $installerCertificateKeySize) {
    throw "Release and installer signing-key records are inconsistent."
}
if ($releaseTimestampRequired -ne $installerTimestampRequired) {
    throw "Release and installer timestamp requirements are inconsistent."
}

$releaseSignature = Get-AuthenticodeSignature -LiteralPath $releaseExe
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
$releaseArtifactRecord = @($releaseSigning.artifacts | Where-Object { $_.path -eq 'AI Arena.exe' })
$installerReleaseArtifactRecord = @($installerSigning.artifacts | Where-Object {
    $_.path -eq 'AI Arena.exe' -and $_.location -eq 'release'
})
$installerArtifactRecord = @($installerSigning.artifacts | Where-Object { $_.location -eq 'installer' })
if ($releaseArtifactRecord.Count -ne 1 -or $releaseArtifactRecord[0].status -ne $releaseSignature.Status.ToString()) {
    throw "Release executable signature status does not match the signing report."
}
if ($installerReleaseArtifactRecord.Count -ne 1 `
    -or $installerReleaseArtifactRecord[0].status -ne $releaseSignature.Status.ToString()) {
    throw "Installer report release-executable status does not match the artifact."
}
if ($installerArtifactRecord.Count -ne 1 -or $installerArtifactRecord[0].status -ne $installerSignature.Status.ToString()) {
    throw "Installer signature status does not match the signing report."
}
$releaseArtifactTimestampVerified = [bool](Get-AIArenaOptionalPropertyValue -InputObject $releaseArtifactRecord[0] -Name 'timestampVerified' -DefaultValue $false)
$installerReleaseArtifactTimestampVerified = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerReleaseArtifactRecord[0] -Name 'timestampVerified' -DefaultValue $false)
$installerArtifactTimestampVerified = [bool](Get-AIArenaOptionalPropertyValue -InputObject $installerArtifactRecord[0] -Name 'timestampVerified' -DefaultValue $false)
$releaseArtifactSignerThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $releaseArtifactRecord[0] -Name 'signerThumbprint' -DefaultValue '')
$installerReleaseArtifactSignerThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerReleaseArtifactRecord[0] -Name 'signerThumbprint' -DefaultValue '')
$installerArtifactSignerThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerArtifactRecord[0] -Name 'signerThumbprint' -DefaultValue '')
$releaseArtifactTimeStamperThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $releaseArtifactRecord[0] -Name 'timeStamperThumbprint' -DefaultValue '')
$installerReleaseArtifactTimeStamperThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerReleaseArtifactRecord[0] -Name 'timeStamperThumbprint' -DefaultValue '')
$installerArtifactTimeStamperThumbprint = [string](Get-AIArenaOptionalPropertyValue -InputObject $installerArtifactRecord[0] -Name 'timeStamperThumbprint' -DefaultValue '')
foreach ($signature in @($releaseSignature, $installerSignature)) {
    if ($signature.Status -notin @(
        [System.Management.Automation.SignatureStatus]::Valid,
        [System.Management.Automation.SignatureStatus]::NotSigned)) {
        throw "Release artifact has an unacceptable Authenticode status: $($signature.Status)."
    }
}
if ($releaseSigningEnabled -or $recordedPolicy -eq 'Required') {
    if ([string]::IsNullOrWhiteSpace($releaseCertificateThumbprint) `
        -or -not $releaseCertificateChainTrusted `
        -or -not $releasePrivateKeyVerified `
        -or -not $installerCertificateChainTrusted `
        -or -not $installerPrivateKeyVerified) {
        throw "Signing reports do not attest the selected certificate's trusted chain and usable private key."
    }
    if (-not $releaseTimestampRequired -or -not $installerTimestampRequired) {
        throw "Signed release reports must require RFC 3161 timestamps."
    }

    $releaseVerification = Assert-AIArenaAuthenticodeSignature `
        -Signature $releaseSignature `
        -ExpectedSignerThumbprint $releaseCertificateThumbprint `
        -RequireTimestamp `
        -Label 'Release executable'
    $installerVerification = Assert-AIArenaAuthenticodeSignature `
        -Signature $installerSignature `
        -ExpectedSignerThumbprint $releaseCertificateThumbprint `
        -RequireTimestamp `
        -Label 'Installer'
    if ($releaseCertificateKeyAlgorithm -ne $releaseVerification.SignerKeyAlgorithm `
        -or $releaseCertificateKeySize -ne $releaseVerification.SignerKeySize `
        -or $installerCertificateKeyAlgorithm -ne $installerVerification.SignerKeyAlgorithm `
        -or $installerCertificateKeySize -ne $installerVerification.SignerKeySize) {
        throw "Signing-report key metadata does not match the signed app and installer."
    }

    if (-not $releaseArtifactTimestampVerified `
        -or $releaseArtifactSignerThumbprint -ne $releaseVerification.SignerThumbprint `
        -or $releaseArtifactTimeStamperThumbprint -ne $releaseVerification.TimeStamperThumbprint) {
        throw "Release executable signer or timestamp does not match its signing report."
    }
    if (-not $installerReleaseArtifactTimestampVerified `
        -or $installerReleaseArtifactSignerThumbprint -ne $releaseVerification.SignerThumbprint `
        -or $installerReleaseArtifactTimeStamperThumbprint -ne $releaseVerification.TimeStamperThumbprint) {
        throw "Installer report release-executable signer or timestamp is inconsistent."
    }
    if (-not $installerArtifactTimestampVerified `
        -or $installerArtifactSignerThumbprint -ne $installerVerification.SignerThumbprint `
        -or $installerArtifactTimeStamperThumbprint -ne $installerVerification.TimeStamperThumbprint) {
        throw "Installer signer or timestamp does not match its signing report."
    }
}
else {
    if ($releaseSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned `
        -or $installerSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Signing is recorded as disabled, but a release artifact carries an unrecorded signature."
    }
    if ($releaseTimestampRequired `
        -or $installerTimestampRequired `
        -or $releaseArtifactTimestampVerified `
        -or $installerReleaseArtifactTimestampVerified `
        -or $installerArtifactTimestampVerified) {
        throw "Unsigned signing reports must not claim timestamp verification."
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

Write-Host "WPF release sanity passed for $productShortDisplayName $Version"
Write-Host $installer
