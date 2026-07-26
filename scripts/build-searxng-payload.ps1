param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [string]$DownloadDir = "",
    [string]$UpstreamLockPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $repoRoot "scripts\release-security.ps1")

if ([string]::IsNullOrWhiteSpace($UpstreamLockPath)) {
    $UpstreamLockPath = Join-Path $repoRoot "packaging\upstream-lock.json"
}
$upstreamLockFull = (Resolve-Path -LiteralPath $UpstreamLockPath).Path
$upstreamLock = Get-Content -LiteralPath $upstreamLockFull -Raw | ConvertFrom-Json
if ($upstreamLock.schemaVersion -ne 1) {
    throw "Unsupported upstream lock schema in ${upstreamLockFull}: $($upstreamLock.schemaVersion)"
}

$PythonVersion = [string]$upstreamLock.python.version
$PythonUrl = [string]$upstreamLock.python.url
$PythonSha256 = [string]$upstreamLock.python.sha256
$SearxngRevision = [string]$upstreamLock.searxng.revision
$SearxngUrl = [string]$upstreamLock.searxng.url
$SearxngSha256 = [string]$upstreamLock.searxng.sha256
$GranianVersion = [string]$upstreamLock.granian.version
$GranianUrl = [string]$upstreamLock.granian.url
$DependencyLockRelative = [string]$upstreamLock.pythonDependencies.lockFile
$DependencyLockSha256 = [string]$upstreamLock.pythonDependencies.sha256

if ($PythonVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Pinned Python version is invalid: $PythonVersion"
}
if ($SearxngRevision -notmatch '^[A-Fa-f0-9]{40}$') {
    throw "Pinned SearXNG revision must be a full 40-character commit hash."
}
if ($GranianVersion -notmatch '^\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.]+)?$') {
    throw "Pinned Granian version is invalid: $GranianVersion"
}
Assert-AIArenaHttpsUri -Value $PythonUrl -Label 'Pinned Python URL'
Assert-AIArenaHttpsUri -Value $SearxngUrl -Label 'Pinned SearXNG URL'
Assert-AIArenaHttpsUri -Value $GranianUrl -Label 'Pinned Granian URL'
Assert-AIArenaSha256 -Value $PythonSha256 -Label 'Pinned Python archive hash'
Assert-AIArenaSha256 -Value $SearxngSha256 -Label 'Pinned SearXNG archive hash'
Assert-AIArenaSha256 -Value $DependencyLockSha256 -Label 'Pinned Python dependency-lock hash'
if (-not $PythonUrl.EndsWith("/python-$PythonVersion-embed-amd64.zip", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Pinned Python URL does not match pinned version $PythonVersion."
}
if ($SearxngUrl -notmatch [regex]::Escape($SearxngRevision)) {
    throw "Pinned SearXNG URL does not contain pinned revision $SearxngRevision."
}
if ($DependencyLockRelative -ne 'packaging/searxng-requirements-lock.txt' `
    -or $upstreamLock.pythonDependencies.platform -ne 'win_amd64' `
    -or $upstreamLock.pythonDependencies.pythonAbi -ne 'cp311') {
    throw "Pinned Python dependency lock must target packaging/searxng-requirements-lock.txt for CPython 3.11 on Windows x64."
}
$dependencyLockFull = [IO.Path]::GetFullPath((Join-Path $repoRoot ($DependencyLockRelative -replace '/', '\')))
Assert-AIArenaPathWithinDirectory -Path $dependencyLockFull -Directory (Join-Path $repoRoot 'packaging') -Label 'Python dependency lock'
if (-not (Test-Path -LiteralPath $dependencyLockFull -PathType Leaf)) {
    throw "Reviewed Python dependency lock does not exist: $dependencyLockFull"
}
$arenaGatewaySource = Join-Path $repoRoot 'packaging\arena_searxng_wsgi.py'
if (-not (Test-Path -LiteralPath $arenaGatewaySource -PathType Leaf)) {
    throw "AI Arena SearXNG API boundary source does not exist: $arenaGatewaySource"
}
$actualDependencyLockHash = (Get-FileHash -LiteralPath $dependencyLockFull -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualDependencyLockHash -ne $DependencyLockSha256.ToUpperInvariant()) {
    throw "Reviewed Python dependency lock failed SHA-256 verification. Expected $DependencyLockSha256, found $actualDependencyLockHash."
}

if ([string]::IsNullOrWhiteSpace($DownloadDir)) {
    $DownloadDir = Join-Path $repoRoot "artifacts\downloads"
}

$outputFull = [IO.Path]::GetFullPath($OutputDir)
if (-not (Test-Path -LiteralPath $outputFull -PathType Container)) {
    throw "Release output directory does not exist: $outputFull"
}

$finalizedReleaseMarkers = @(
    'release-manifest.txt',
    'release-checksums.sha256',
    'release-signing.json'
) | ForEach-Object { Join-Path $outputFull $_ }
$existingFinalizedReleaseMarkers = @($finalizedReleaseMarkers | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
if ($existingFinalizedReleaseMarkers.Count -gt 0) {
    throw "Refusing to mutate a finalized release directory. Build the SearXNG payload before release manifests, or use a separate smoke-test output: $outputFull"
}

$payloadDir = [IO.Path]::GetFullPath((Join-Path $outputFull "searxng"))
Assert-AIArenaPathWithinDirectory -Path $payloadDir -Directory $outputFull -Label 'SearXNG payload directory'
$pythonDir = Join-Path $payloadDir "python"
$runtimeDir = Join-Path $payloadDir "runtime"
$sitePackagesDir = Join-Path $runtimeDir "site-packages"
$workRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\searxng-build"))
Assert-AIArenaPathWithinDirectory -Path $workRoot -Directory (Join-Path $repoRoot "artifacts") -Label 'SearXNG work root'
$workDir = Join-Path $workRoot ("build-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString('N'))
Assert-AIArenaPathWithinDirectory -Path $workDir -Directory $workRoot -Label 'SearXNG work directory'
$sourceExtractDir = Join-Path $workDir "source"
$downloadFull = [IO.Path]::GetFullPath($DownloadDir)

if (Test-Path -LiteralPath $payloadDir) {
    Remove-Item -LiteralPath $payloadDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $downloadFull, $pythonDir, $runtimeDir, $sitePackagesDir, $sourceExtractDir | Out-Null

$pythonZip = Join-Path $downloadFull "python-$PythonVersion-embed-amd64.zip"
$sourceZip = Join-Path $downloadFull "searxng-$SearxngRevision.zip"

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label does not exist: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "$Label failed SHA-256 verification. Expected $ExpectedSha256, found $actual. Remove the untrusted file before retrying."
    }
}

function Save-VerifiedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-AIArenaHttpsUri -Value $Uri -Label "$Label URL"
    Assert-AIArenaSha256 -Value $ExpectedSha256 -Label "$Label expected hash"
    if (Test-Path -LiteralPath $Path) {
        Assert-FileHash -Path $Path -ExpectedSha256 $ExpectedSha256 -Label "Cached $Label"
        Write-Host "Verified cached $Label"
        return
    }

    $temporary = "$Path.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Downloading $Label from $Uri"
        Invoke-WebRequest -Uri $Uri -OutFile $temporary -UseBasicParsing -MaximumRedirection 5
        Assert-FileHash -Path $temporary -ExpectedSha256 $ExpectedSha256 -Label "Downloaded $Label"
        Move-Item -LiteralPath $temporary -Destination $Path
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-SafeArchiveTarget {
    param(
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $normalized = $EntryName -replace '/', '\'
    if ([string]::IsNullOrWhiteSpace($normalized) `
        -or [IO.Path]::IsPathRooted($normalized) `
        -or $normalized.Contains(':') `
        -or ($normalized -split '\\') -contains '..') {
        throw "Archive contains an unsafe entry path: $EntryName"
    }

    $destinationFull = [IO.Path]::GetFullPath($DestinationPath)
    $target = [IO.Path]::GetFullPath((Join-Path $destinationFull $normalized))
    Assert-AIArenaPathWithinDirectory -Path $target -Directory $destinationFull -Label "Archive entry '$EntryName'"
    return $target
}

function Assert-ArchiveEntryIsRegular {
    param([Parameter(Mandatory = $true)]$Entry)

    $unixFileType = ($Entry.ExternalAttributes -shr 16) -band 0xF000
    if ($unixFileType -eq 0xA000) {
        throw "Archive contains a symbolic link, which is not allowed: $($Entry.FullName)"
    }
}

function Expand-ZipArchiveSafely {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            Assert-ArchiveEntryIsRegular -Entry $entry
            $target = Get-SafeArchiveTarget -DestinationPath $DestinationPath -EntryName $entry.FullName
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                continue
            }

            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Expand-SearxngSourceArchiveSafely {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $topLevels = @($archive.Entries |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.FullName) } |
            ForEach-Object { ($_.FullName -split '/', 2)[0] } |
            Select-Object -Unique)
        if ($topLevels.Count -ne 1 -or $topLevels[0] -notmatch '^searxng-[A-Fa-f0-9]{40}$') {
            throw "SearXNG source archive has an unexpected top-level layout."
        }

        foreach ($entry in $archive.Entries) {
            $parts = $entry.FullName -split '/', 2
            if ($parts.Count -lt 2 -or $parts[0] -ne $topLevels[0]) {
                continue
            }

            $relative = $parts[1]
            if ([string]::IsNullOrWhiteSpace($relative)) {
                continue
            }

            $include = $relative.StartsWith("searx/", [StringComparison]::Ordinal) `
                -or $relative.StartsWith("searxng_extra/", [StringComparison]::Ordinal) `
                -or $relative -eq "requirements.txt" `
                -or $relative -eq "requirements-server.txt" `
                -or $relative -eq "setup.py" `
                -or $relative -eq "manage" `
                -or $relative -eq "LICENSE"
            if (-not $include) {
                continue
            }

            Assert-ArchiveEntryIsRegular -Entry $entry
            $target = Get-SafeArchiveTarget -DestinationPath $DestinationPath -EntryName $entry.FullName
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                continue
            }

            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
}

Save-VerifiedDownload -Uri $PythonUrl -Path $pythonZip -ExpectedSha256 $PythonSha256 -Label "CPython $PythonVersion embeddable archive"
Save-VerifiedDownload -Uri $SearxngUrl -Path $sourceZip -ExpectedSha256 $SearxngSha256 -Label "SearXNG $SearxngRevision source archive"

Expand-ZipArchiveSafely -ZipPath $pythonZip -DestinationPath $pythonDir
if (-not (Test-Path -LiteralPath (Join-Path $pythonDir "python.exe")) -or -not (Test-Path -LiteralPath (Join-Path $pythonDir "pythonw.exe"))) {
    throw "Bundled Python runtime extraction failed: python.exe and pythonw.exe were not found in $pythonDir"
}
Expand-SearxngSourceArchiveSafely -ZipPath $sourceZip -DestinationPath $sourceExtractDir

$sourceRoot = Get-ChildItem -LiteralPath $sourceExtractDir -Directory | Select-Object -First 1
if ($null -eq $sourceRoot) {
    throw "SearXNG source archive did not contain a source directory."
}

foreach ($name in @("searx", "searxng_extra")) {
    $source = Join-Path $sourceRoot.FullName $name
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "SearXNG source archive is missing required directory: $name"
    }
    Copy-Item -LiteralPath $source -Destination $runtimeDir -Recurse
}

foreach ($name in @("requirements.txt", "requirements-server.txt", "setup.py", "manage")) {
    $source = Join-Path $sourceRoot.FullName $name
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination $runtimeDir
    }
}

Copy-Item -LiteralPath $arenaGatewaySource -Destination (Join-Path $runtimeDir 'arena_searxng_wsgi.py')

Copy-Item -LiteralPath (Join-Path $sourceRoot.FullName "LICENSE") -Destination (Join-Path $payloadDir "LICENSE")
Copy-Item -LiteralPath $upstreamLockFull -Destination (Join-Path $payloadDir "UPSTREAM-LOCK.json")
Copy-Item -LiteralPath $dependencyLockFull -Destination (Join-Path $payloadDir "PYTHON-REQUIREMENTS-LOCK.txt")

@'
python311.zip
.
..\runtime
..\runtime\site-packages
import site
'@ | Set-Content -LiteralPath (Join-Path $pythonDir "python311._pth") -Encoding ASCII

@'
from collections import namedtuple
_Pw = namedtuple('struct_passwd', 'pw_name pw_passwd pw_uid pw_gid pw_gecos pw_dir pw_shell')
def getpwuid(uid):
    return _Pw('windows', 'x', uid, 0, 'windows', '', '')
'@ | Set-Content -LiteralPath (Join-Path $runtimeDir "pwd.py") -Encoding ASCII

$py = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $py) {
    throw "Python launcher py.exe was not found. Install Python 3.11 to build the bundled SearXNG payload."
}

Invoke-AIArenaNativeCommand -FilePath $py.Source -ArgumentList @('-3.11', '--version') -Label 'Python 3.11 prerequisite check'
$sourceRequirementsPath = Join-Path $runtimeDir "requirements.txt"
$dependencyLockText = Get-Content -LiteralPath $dependencyLockFull -Raw
$lockedRequirements = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($lockedLine in $dependencyLockText -split '\r?\n') {
    if ($lockedLine -notmatch '^\s*([A-Za-z0-9._-]+)(?:\[[^]]+\])?==([^\s#]+)\s+--hash=sha256:[A-Fa-f0-9]{64}\s*$') {
        continue
    }
    $lockedName = $Matches[1] -replace '[._-]+', '-'
    [void]$lockedRequirements.Add("$lockedName==$($Matches[2])")
}
foreach ($requirement in Get-Content -LiteralPath $sourceRequirementsPath) {
    if ($requirement -notmatch '^\s*([A-Za-z0-9._-]+)(?:\[[^]]+\])?==([^\s#]+)') {
        continue
    }
    $requiredName = $Matches[1] -replace '[._-]+', '-'
    $requiredVersion = $Matches[2]
    if (-not $lockedRequirements.Contains("$requiredName==$requiredVersion")) {
        throw "Python dependency lock does not cover SearXNG requirement ${requiredName}==${requiredVersion}."
    }
}
if (-not $lockedRequirements.Contains("granian==$GranianVersion")) {
    throw "Python dependency lock does not cover Granian $GranianVersion."
}

$pipReport = Join-Path $workDir 'pip-install-report.json'
$pipArguments = @(
    '-3.11', '-m', 'pip', '--isolated', 'install',
    '--disable-pip-version-check',
    '--no-input',
    '--no-compile',
    '--no-warn-script-location',
    '--only-binary=:all:',
    '--index-url', 'https://pypi.org/simple',
    '--require-hashes',
    '--report', $pipReport,
    '--target', $sitePackagesDir,
    '-r', $dependencyLockFull
)
Invoke-AIArenaNativeCommand -FilePath $py.Source -ArgumentList $pipArguments -Label 'Bundled SearXNG dependency installation'

@'
# AI Arena's private, app-managed SearXNG profile. Engine definitions remain
# inherited from the pinned upstream revision; Arena owns result ranking.
use_default_settings: true
general:
  instance_name: "AI Arena Local Search"
  debug: false
  enable_metrics: false
search:
  autocomplete: ""
  favicon_resolver: ""
  formats:
    - json
server:
  port: 8081
  bind_address: "127.0.0.1"
  base_url: false
  limiter: false
  public_instance: false
  secret_key: "ai-arena-private-local-search"
  image_proxy: false
  method: "GET"
outgoing:
  request_timeout: 4.0
  max_request_timeout: 6.0
  pool_connections: 32
  pool_maxsize: 16
  keepalive_expiry: 5.0
  max_redirects: 5
  retries: 1
  enable_http2: true
  verify: true
valkey:
  url: false
'@ | Set-Content -LiteralPath (Join-Path $payloadDir "settings.yml") -Encoding UTF8

@"
SearXNG source offer

This installer bundles SearXNG under AGPL-3.0-or-later.
Upstream source: https://github.com/searxng/searxng
Bundled source revision: $SearxngRevision
Bundled source archive: $SearxngUrl
Bundled source archive SHA-256: $SearxngSha256
Bundled dependency server: granian $GranianVersion
Bundled Python dependency lock SHA-256: $DependencyLockSha256
Bundled Python runtime: CPython $PythonVersion embeddable Windows build
Bundled Python archive: $PythonUrl
Bundled Python archive SHA-256: $PythonSha256

AI Arena exposes the bundled service through its project-owned JSON API boundary.
The boundary is shipped as source under AGPL-3.0-or-later.
Boundary source: packaging/arena_searxng_wsgi.py in the AI Arena source tree.

The corresponding SearXNG source for this bundled revision is available from:
$SearxngUrl
"@ | Set-Content -LiteralPath (Join-Path $payloadDir "SEARXNG-SOURCE-OFFER.txt") -Encoding UTF8

Get-ChildItem -LiteralPath $payloadDir -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
Get-ChildItem -LiteralPath $payloadDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq ".pyc" -or $_.Extension -eq ".pyo" } |
    Remove-Item -Force

$runtimeLiteral = ConvertTo-Json $runtimeDir -Compress
$sitePackagesLiteral = ConvertTo-Json $sitePackagesDir -Compress
$settingsLiteral = ConvertTo-Json (Join-Path $payloadDir "settings.yml") -Compress
$importProbe = @"
import os
import sys
sys.path.insert(0, $runtimeLiteral)
sys.path.insert(0, $sitePackagesLiteral)
os.environ['SEARXNG_SETTINGS_PATH'] = $settingsLiteral
import searx
import granian
import arena_searxng_wsgi
assert 'default_doi_resolver' in searx.settings
assert searx.settings['search']['formats'] == ['json']
assert searx.settings['general']['enable_metrics'] is False
assert searx.settings['outgoing']['request_timeout'] == 4.0
assert searx.settings['outgoing']['max_request_timeout'] == 6.0
assert searx.settings['outgoing']['pool_connections'] == 32
assert searx.settings['outgoing']['pool_maxsize'] == 16
assert searx.settings['outgoing']['max_redirects'] == 5

downstream_calls = []
def downstream(environ, start_response):
    downstream_calls.append((environ.get('PATH_INFO'), environ.get('QUERY_STRING'), environ.get('REQUEST_METHOD')))
    start_response('200 OK', [('Content-Type', 'application/json')])
    return [b'{"results":[]}']

arena_searxng_wsgi._searx_application = downstream

def probe_gateway(path, query='', method='GET'):
    response_status = []
    body = b''.join(arena_searxng_wsgi.application(
        {'PATH_INFO': path, 'QUERY_STRING': query, 'REQUEST_METHOD': method},
        lambda status, _headers: response_status.append(status),
    ))
    return response_status[0], body

status, _body = probe_gateway('/search', 'q=ai%20arena&format=json')
assert status == '200 OK'
assert downstream_calls == [('/search', 'q=ai%20arena&format=json', 'GET')]
assert probe_gateway('/', 'q=ai%20arena&format=json')[0].startswith('404 ')
assert probe_gateway('/rss.xsl')[0].startswith('404 ')
assert probe_gateway('/search/', 'q=ai%20arena&format=json')[0].startswith('404 ')
assert probe_gateway('/search', 'q=ai%20arena&format=rss')[0].startswith('403 ')
assert probe_gateway('/search', 'q=ai%20arena&format=json&format=rss')[0].startswith('403 ')
assert probe_gateway('/search', 'q=ai%20arena&format=json', 'POST')[0].startswith('405 ')
assert len(downstream_calls) == 1
print('SearXNG payload import probe ok')
"@

$probeFile = Join-Path $workDir "probe.py"
Set-Content -LiteralPath $probeFile -Value $importProbe -Encoding UTF8
$payloadPython = Join-Path $pythonDir "python.exe"
Invoke-AIArenaNativeCommand -FilePath $payloadPython -ArgumentList @($probeFile) -Label 'Bundled SearXNG import probe'

Get-ChildItem -LiteralPath $payloadDir -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
Get-ChildItem -LiteralPath $payloadDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq ".pyc" -or $_.Extension -eq ".pyo" } |
    Remove-Item -Force

$preInventorySize = Get-ChildItem -LiteralPath $payloadDir -Recurse -File | Measure-Object Length -Sum
Set-Content -LiteralPath (Join-Path $payloadDir "payload-manifest.txt") -Encoding UTF8 -Value @(
    "AI Arena bundled SearXNG payload",
    "SearXNG revision: $SearxngRevision",
    "SearXNG source SHA-256: $SearxngSha256",
    "Python: $PythonVersion",
    "Python archive SHA-256: $PythonSha256",
    "Granian: $GranianVersion",
    "Python dependency lock SHA-256: $DependencyLockSha256",
    "Files before inventory: $($preInventorySize.Count)",
    "Bytes before inventory: $($preInventorySize.Sum)"
)

$pipInstallReport = Get-Content -LiteralPath $pipReport -Raw | ConvertFrom-Json
$pythonPackages = foreach ($install in $pipInstallReport.install | Sort-Object { $_.metadata.name }) {
    $packageUrl = [string]$install.download_info.url
    $packageSha256 = [string]$install.download_info.archive_info.hashes.sha256
    Assert-AIArenaHttpsUri -Value $packageUrl -Label "Python package URL for $($install.metadata.name)"
    $packageUri = [Uri]$packageUrl
    if ($packageUri.DnsSafeHost -ne 'files.pythonhosted.org') {
        throw "Python package $($install.metadata.name) was not resolved from the official PyPI file host: $packageUrl"
    }
    Assert-AIArenaSha256 -Value $packageSha256 -Label "Python package archive hash for $($install.metadata.name)"
    [ordered]@{
        ecosystem = 'PyPI'
        name = [string]$install.metadata.name
        version = [string]$install.metadata.version
        requested = [bool]$install.requested
        archiveUrl = $packageUrl
        archiveSha256 = $packageSha256.ToUpperInvariant()
    }
}
$installedDistributionCount = @(Get-ChildItem -LiteralPath $sitePackagesDir -Directory -Filter '*.dist-info').Count
if ($pythonPackages.Count -ne $installedDistributionCount) {
    throw "Pip report listed $($pythonPackages.Count) distributions, but payload contains $installedDistributionCount dist-info directories."
}

$inventoryPath = Join-Path $payloadDir 'payload-inventory.json'
$payloadFiles = foreach ($file in Get-ChildItem -LiteralPath $payloadDir -File -Recurse | Where-Object { $_.FullName -ne $inventoryPath } | Sort-Object FullName) {
    $relative = $file.FullName.Substring($payloadDir.Length).TrimStart('\', '/')
    [ordered]@{
        path = $relative
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

$inventory = [ordered]@{
    format = 'AI Arena payload inventory'
    formatVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    inventoryExcludes = @('payload-inventory.json')
    upstreamLock = [ordered]@{
        path = 'UPSTREAM-LOCK.json'
        sha256 = (Get-FileHash -LiteralPath $upstreamLockFull -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    dependencyLock = [ordered]@{
        path = 'PYTHON-REQUIREMENTS-LOCK.txt'
        platform = [string]$upstreamLock.pythonDependencies.platform
        pythonAbi = [string]$upstreamLock.pythonDependencies.pythonAbi
        sha256 = $DependencyLockSha256.ToUpperInvariant()
    }
    payload = [ordered]@{
        searxngRevision = $SearxngRevision
        pythonVersion = $PythonVersion
        granianVersion = $GranianVersion
    }
    upstreamArchives = @(
        [ordered]@{ name = 'CPython embeddable Windows runtime'; version = $PythonVersion; url = $PythonUrl; sha256 = $PythonSha256.ToUpperInvariant() },
        [ordered]@{ name = 'SearXNG source'; version = $SearxngRevision; url = $SearxngUrl; sha256 = $SearxngSha256.ToUpperInvariant() }
    )
    packages = @($pythonPackages)
    files = @($payloadFiles)
}

$inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inventoryPath -Encoding UTF8

$size = Get-ChildItem -LiteralPath $payloadDir -Recurse -File | Measure-Object Length -Sum
Remove-Item -LiteralPath $workDir -Recurse -Force
Write-Host "SearXNG payload created:"
Write-Host $payloadDir
Write-Host ("Files: {0}" -f $size.Count)
Write-Host ("Bytes: {0}" -f $size.Sum)
