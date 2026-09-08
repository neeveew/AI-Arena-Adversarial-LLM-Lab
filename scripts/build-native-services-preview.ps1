[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repoRoot 'scripts\release-security.ps1')

if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    throw 'OutputDirectory must be an absolute path to a new directory beneath this repository''s artifacts folder.'
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$outputFull = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
Assert-AIArenaPathWithinDirectory -Path $outputFull -Directory $artifactsRoot -Label 'Native Services preview output'

function Assert-PreviewAncestorsAreOrdinaryDirectories {
    param([Parameter(Mandatory = $true)][string]$Path)

    # GetFullPath removes traversal segments; rejecting existing reparse points
    # keeps a junction or symlink from redirecting the guarded path elsewhere.
    $ancestor = $Path
    while (-not [string]::IsNullOrEmpty($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            $entry = Get-Item -LiteralPath $ancestor -Force
            if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Preview output must use ordinary directory ancestors: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

Assert-PreviewAncestorsAreOrdinaryDirectories -Path $outputFull
if (Test-Path -LiteralPath $outputFull) {
    throw "Preview output already exists. Choose a new directory; no payload will be deleted or replaced: $outputFull"
}

$project = Join-Path $repoRoot 'src\AIArena.Wpf\AIArena.Wpf.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
$informationalVersion = [string]$projectXml.Project.PropertyGroup.InformationalVersion
$fileVersion = [string]$projectXml.Project.PropertyGroup.FileVersion
if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($informationalVersion) -or [string]::IsNullOrWhiteSpace($fileVersion)) {
    throw 'The WPF project must declare its existing version metadata before producing a preview.'
}

$copyFiles = @(
    @{ Source = 'LICENSE'; Destination = 'LICENSE' },
    @{ Source = 'NOTICE.md'; Destination = 'NOTICE.md' },
    @{ Source = 'docs\USER_GUIDE.md'; Destination = 'USER_GUIDE.md' },
    @{ Source = 'CONTROLPLANE.md'; Destination = 'CONTROLPLANE.md' },
    @{ Source = 'scripts\ai-arena-control.ps1'; Destination = 'ai-arena-control.ps1' },
    @{ Source = 'docs\NATIVE_SERVICES.md'; Destination = 'NATIVE_SERVICES.md' }
)
foreach ($copy in $copyFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $copy.Source) -PathType Leaf)) {
        throw "Required preview documentation is missing: $($copy.Source)"
    }
}
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
$git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
$sourceCommit = (& $git.Source -C $repoRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'Could not capture the source commit for this preview.'
}
$sourceChanges = @(& $git.Source -C $repoRoot status --porcelain=v1 --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Could not capture the source checkout status for this preview.' }
$sourceDirty = $sourceChanges.Count -gt 0
$sourceCapturedUtc = [DateTime]::UtcNow.ToString('o')

# Claim a fresh directory without -Force. Failures leave this new output in
# place for inspection; this script never cleans existing output or dist.
[void](New-Item -ItemType Directory -Path $outputFull -ErrorAction Stop)
Assert-PreviewAncestorsAreOrdinaryDirectories -Path $outputFull
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $outputFull,
    '-p:PublishSingleFile=false',
    '-p:UseAppHost=true'
)
# This helper checks LASTEXITCODE. No release wrapper, installer, signing,
# version override, application launch, or native-service probe is invoked.
Invoke-AIArenaNativeCommand -FilePath $dotnet.Source -ArgumentList $publishArgs -Label 'Unsigned Native Services preview publish'

$requiredFiles = @(
    'AI Arena.exe', 'AI Arena.dll', 'AI Arena.deps.json', 'AI Arena.runtimeconfig.json',
    'AIArena.Core.dll', 'AIArena.CodeIntelligence.dll', 'Google.Protobuf.dll',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
    'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll',
    'Help\Content\guide-manifest.json'
)
foreach ($relative in $requiredFiles) {
    $required = Join-Path $outputFull $relative
    if (-not (Test-Path -LiteralPath $required -PathType Leaf) -or (Get-Item -LiteralPath $required).Length -le 0) {
        throw "The preview is missing a required nonempty runtime or content file: $relative"
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $outputFull 'AI Arena.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeProperties = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
if ($runtimeProperties -notcontains 'includedFrameworks' -or $runtimeProperties -contains 'frameworks' -or $runtimeProperties -contains 'framework') {
    throw 'The preview must be self-contained: expected includedFrameworks and no external framework references.'
}
$frameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { [string]$_.name })
if ($frameworkNames -notcontains 'Microsoft.NETCore.App' -or $frameworkNames -notcontains 'Microsoft.WindowsDesktop.App') {
    throw 'The preview runtime configuration does not include both .NET and Windows Desktop frameworks.'
}

$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outputFull 'AI Arena.exe'))
if ($publishedVersion.FileVersion -ne $fileVersion -or ($publishedVersion.ProductVersion -split '\+', 2)[0] -ne $informationalVersion) {
    throw 'Published executable version metadata differs from the declared WPF project metadata.'
}
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $outputFull 'AI Arena.exe')
if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
    throw "Expected an unsigned preview executable; observed Authenticode status: $($signature.Status)"
}

$sourceHelpRoot = Join-Path $repoRoot 'src\AIArena.Wpf\Help\Content'
$helpExtensions = @('.json', '.md', '.png', '.jpg', '.jpeg', '.webp')
$helpFiles = @(Get-ChildItem -LiteralPath $sourceHelpRoot -File -Recurse | Where-Object { $helpExtensions -contains $_.Extension.ToLowerInvariant() })
if ($helpFiles.Count -eq 0) { throw 'No offline Help content was found in the source project.' }
foreach ($helpFile in $helpFiles) {
    $relative = $helpFile.FullName.Substring($sourceHelpRoot.Length).TrimStart('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $outputFull "Help\Content\$relative") -PathType Leaf)) {
        throw "The preview is missing published offline Help content: $relative"
    }
}
if (Test-Path -LiteralPath (Join-Path $outputFull 'searxng')) {
    throw 'This preview intentionally omits the optional SearXNG payload; an unexpected payload was published.'
}

foreach ($copy in $copyFiles) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $copy.Source) -Destination (Join-Path $outputFull $copy.Destination)
}
$previewLines = @(
    '# AI Arena Native Services preview',
    '',
    'This is an unsigned Windows x64 preview payload using the declared application version. A published release and installer are separate steps.',
    '',
    "Project version: $version",
    "Executable file version: $($publishedVersion.FileVersion)",
    "Executable informational version: $($publishedVersion.ProductVersion)",
    "Source commit: $sourceCommit",
    "Source checkout dirty: $($sourceDirty.ToString().ToLowerInvariant())",
    "Modified/untracked status entries: $($sourceChanges.Count)",
    "Source state captured UTC: $sourceCapturedUtc",
    "Payload completed UTC: $([DateTime]::UtcNow.ToString('o'))",
    'Configuration: Release; runtime: win-x64; self-contained: true.',
    'Authenticode: NotSigned. No signing or installer generation was performed.',
    '',
    'Extract the whole payload and open AI Arena.exe. The .NET Desktop Runtime is included; keep the supplied files together.',
    '',
    'Native Services requires a separately running, compatible AI Arena native app supporting the current Protobuf control contract in the same Windows user and interactive sign-in session. This payload neither includes nor launches that app. See NATIVE_SERVICES.md for the supported workflow and contract.',
    '',
    'Use separate writable data profiles for WPF and the native app. Set AI_ARENA_DATA_DIR separately for each process to different directories; do not point either app at the other app''s profile. A separate binary folder alone does not isolate saved data. Profile overrides do not create another native control endpoint in the same sign-in session.',
    '',
    'SearXNG is not bundled in this preview. Built-in local web search therefore requires a separately available compatible payload; Native Services does not require SearXNG.',
    '',
    'This script checked the published runtime, version, offline Help files, and required dependencies. It did not launch either app, call native services, run test harnesses, or certify live model behavior. Any separate verification evidence belongs to the accompanying handoff.',
    '',
    'SHA256SUMS.txt inventories every payload file except the checksum manifest itself. LICENSE and NOTICE.md retain the existing distribution terms.'
)
[IO.File]::WriteAllLines((Join-Path $outputFull 'PREVIEW.md'), $previewLines, [Text.UTF8Encoding]::new($false))
New-AIArenaSha256Manifest -BaseDirectory $outputFull -OutputPath (Join-Path $outputFull 'SHA256SUMS.txt')

Write-Output "Unsigned Native Services preview created: $outputFull"
Write-Output "Project version: $version; source commit: $sourceCommit; dirty: $sourceDirty"
Write-Output 'Payload checks passed. No application launch, native probe, installer, or signing step was run.'
