param(
    [string]$DownloadDir = '',
    [string]$PayloadDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'scripts/release-security.ps1')
if ([string]::IsNullOrWhiteSpace($DownloadDir)) { $DownloadDir = Join-Path $repo 'artifacts/downloads' }
$lockPath = Join-Path $repo 'packaging/upstream-lock.json'
$upstream = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$requirementsPath = Join-Path $repo $upstream.pythonDependencies.lockFile
$requirements = Get-Content -LiteralPath $requirementsPath -Raw
$wheelName = 'anyio-4.14.2-py3-none-any.whl'
# Published at https://pypi.org/pypi/anyio/4.14.2/json. This is the minimal
# patched release for GHSA-82r6-8w77-94w6 and GHSA-5p39-cfhj-2xmp.
$wheelHash = '9F505DDA5AC9F0C8309B5E8BD445A8C2BF7246F3CE950121E45EA15BC41D1494'
# Official PyPI wheel for the GHSA-g6x2-hccm-hh4m Windows device-path fix.
$werkzeugWheelName = 'werkzeug-3.1.9-py3-none-any.whl'
$werkzeugWheelHash = '6392E50C78460BA618E5B21F08A71F59C99CE99CDC6CF6E3DD7E6CCCA8754FAB'

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-ZipText($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    Require ($null -ne $entry) "Archive entry missing: $Name"
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

Require ((Get-FileHash -LiteralPath $requirementsPath -Algorithm SHA256).Hash -eq $upstream.pythonDependencies.sha256) 'Dependency lock hash must match the reviewed upstream manifest.'
Require ($requirements -match ('(?im)^anyio==4\.14\.2 --hash=sha256:' + $wheelHash + '\r?$')) 'The payload must lock the verified AnyIO 4.14.2 wheel instead of the vulnerable 4.14.1 release.'
Require (@($requirements -split '\r?\n' | Where-Object { $_ -match '^anyio==' }).Count -eq 1) 'AnyIO must have exactly one locked version.'
Require ($requirements -match ('(?im)^werkzeug==3\.1\.9 --hash=sha256:' + $werkzeugWheelHash + '\r?$')) 'The payload must lock the verified Werkzeug 3.1.9 security wheel.'
Require (@($requirements -split '\r?\n' | Where-Object { $_ -match '^werkzeug==' }).Count -eq 1) 'Werkzeug must have exactly one locked version.'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourcePath = Join-Path $DownloadDir "searxng-$($upstream.searxng.revision).zip"
Require ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -eq $upstream.searxng.sha256) 'The inspected SearXNG archive must be the verified pristine upstream source.'
$archive = [IO.Compression.ZipFile]::OpenRead($sourcePath)
try {
    $sourceRequirements = Read-ZipText $archive "searxng-$($upstream.searxng.revision)/requirements.txt"
    $serverRequirements = Read-ZipText $archive "searxng-$($upstream.searxng.revision)/requirements-server.txt"
} finally { $archive.Dispose() }
$covered = 0
foreach ($line in $sourceRequirements -split '\r?\n') {
    if ($line -notmatch '^([A-Za-z0-9._-]+)(\[[^]]+\])?==([^\s#]+)$') { continue }
    $name = $Matches[1] -replace '[._-]+', '[-_.]'
    $extras = [regex]::Escape($Matches[2])
    $version = [regex]::Escape($Matches[3])
    Require ($requirements -match "(?im)^$name$extras==$version --hash=sha256:") "A direct upstream requirement is absent from the lock: $line"
    $covered++
}
Require ($covered -eq 19) 'Review this fixture when the pinned upstream direct requirements change.'
Require ($sourceRequirements -notmatch '(?im)^anyio\b') 'AnyIO is transitive in this reviewed upstream revision; an upstream pin change needs explicit review.'
Require ($sourceRequirements -notmatch '(?im)^werkzeug\b') 'Werkzeug is transitive in this reviewed upstream revision; an upstream pin change needs explicit review.'
Require ($serverRequirements -match ('(?m)^granian==' + [regex]::Escape($upstream.granian.version) + '\r?$')) 'The upstream server requirement must still match the pinned Granian version.'

$wheelPath = Join-Path $DownloadDir $wheelName
Require ((Get-FileHash -LiteralPath $wheelPath -Algorithm SHA256).Hash -eq $wheelHash) 'The downloaded wheel must match the official PyPI digest.'
$wheel = [IO.Compression.ZipFile]::OpenRead($wheelPath)
try {
    $metadata = Read-ZipText $wheel 'anyio-4.14.2.dist-info/METADATA'
    $licence = Read-ZipText $wheel 'anyio-4.14.2.dist-info/licenses/LICENSE'
    Require ($metadata -match '(?m)^Version: 4\.14\.2\r?$') 'The verified wheel must contain the expected distribution version.'
    Require ($licence -match 'Permission is hereby granted') 'The AnyIO MIT licence text must remain bundled.'
} finally { $wheel.Dispose() }

$werkzeugWheelPath = Join-Path $DownloadDir $werkzeugWheelName
Require ((Get-FileHash -LiteralPath $werkzeugWheelPath -Algorithm SHA256).Hash -eq $werkzeugWheelHash) 'The Werkzeug wheel must match the official PyPI digest.'
$werkzeugWheel = [IO.Compression.ZipFile]::OpenRead($werkzeugWheelPath)
try {
    $werkzeugMetadata = Read-ZipText $werkzeugWheel 'werkzeug-3.1.9.dist-info/METADATA'
    $werkzeugLicence = Read-ZipText $werkzeugWheel 'werkzeug-3.1.9.dist-info/licenses/LICENSE.txt'
    Require ($werkzeugMetadata -match '(?m)^Version: 3\.1\.9\r?$') 'The Werkzeug wheel must contain the expected distribution version.'
    Require ($werkzeugMetadata -match '(?m)^License-Expression: BSD-3-Clause\r?$') 'Review changed Werkzeug licence metadata before updating the dependency.'
    Require ($werkzeugLicence -match 'Redistribution and use in source and binary forms') 'The Werkzeug BSD licence text must remain bundled.'
} finally { $werkzeugWheel.Dispose() }

# Exercise the real builder's fail-closed digest gate without downloading or
# replacing any installed payload. A stale manifest must fail before mutation.
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts/searxng-security-fixture-' + [Guid]::NewGuid().ToString('N'))))
Assert-AIArenaPathWithinDirectory -Path $fixtureRoot -Directory (Join-Path $repo 'artifacts') -Label 'SearXNG security fixture'
try {
    $fixturePayload = Join-Path $fixtureRoot 'searxng'
    New-Item -ItemType Directory -Path $fixturePayload -Force | Out-Null
    $sentinel = Join-Path $fixturePayload 'preserve.txt'
    'existing payload' | Set-Content -LiteralPath $sentinel
    $tampered = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    $tampered.pythonDependencies.sha256 = '0' * 64
    $tamperedPath = Join-Path $fixtureRoot 'stale-upstream-lock.json'
    $tampered | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tamperedPath
    $failure = ''
    try { & (Join-Path $repo 'scripts/build-searxng-payload.ps1') -OutputDir $fixtureRoot -UpstreamLockPath $tamperedPath }
    catch { $failure = $_.Exception.Message }
    Require ($failure -match 'dependency lock failed SHA-256 verification') "The real payload builder did not reject the stale digest: $failure"
    Require (Test-Path -LiteralPath $sentinel) 'Rejected dependency inputs must not replace an existing payload.'
} finally {
    Assert-AIArenaPathWithinDirectory -Path $fixtureRoot -Directory (Join-Path $repo 'artifacts') -Label 'SearXNG security fixture cleanup'
    Require ((Split-Path -Leaf $fixtureRoot) -match '^searxng-security-fixture-[a-f0-9]{32}$') 'Refuse cleanup outside the unique fixture.'
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

if (-not [string]::IsNullOrWhiteSpace($PayloadDir)) {
    $payload = (Resolve-Path -LiteralPath $PayloadDir).Path
    $inventory = Get-Content -LiteralPath (Join-Path $payload 'payload-inventory.json') -Raw | ConvertFrom-Json
    $anyio = @($inventory.packages | Where-Object name -eq 'anyio')
    Require ($anyio.Count -eq 1 -and $anyio[0].version -eq '4.14.2' -and $anyio[0].archiveSha256 -eq $wheelHash) 'The built payload inventory must report the verified AnyIO 4.14.2 wheel.'
    Require (([Uri]$anyio[0].archiveUrl).DnsSafeHost -eq 'files.pythonhosted.org') 'The installed wheel must originate from official PyPI.'
    $werkzeug = @($inventory.packages | Where-Object name -eq 'werkzeug')
    Require ($werkzeug.Count -eq 1 -and $werkzeug[0].version -eq '3.1.9' -and $werkzeug[0].archiveSha256 -eq $werkzeugWheelHash) 'The payload inventory must report the verified Werkzeug 3.1.9 wheel.'
    Require (([Uri]$werkzeug[0].archiveUrl).DnsSafeHost -eq 'files.pythonhosted.org') 'The Werkzeug wheel must originate from official PyPI.'
    Require ((Get-Content -LiteralPath (Join-Path $payload 'runtime/site-packages/werkzeug-3.1.9.dist-info/licenses/LICENSE.txt') -Raw) -ceq $werkzeugLicence) 'The installed Werkzeug licence must match the verified wheel.'
    Invoke-AIArenaNativeCommand -FilePath (Join-Path $payload 'python/python.exe') -ArgumentList @('-B', (Join-Path $PSScriptRoot 'searxng-windows-paths.tests.py'), $payload) -Label 'Bundled Windows path and gateway regressions'
    Require ((Get-FileHash -LiteralPath (Join-Path $payload 'source/searxng-upstream.zip')).Hash -eq $upstream.searxng.sha256) 'The payload must retain the pristine corresponding-source archive.'
    Require ((Get-Content -LiteralPath (Join-Path $payload 'runtime/requirements.txt') -Raw) -ceq $sourceRequirements) 'No upstream requirements patch or exception is needed for this transitive update.'
    Require ((Get-Content -LiteralPath (Join-Path $payload 'PYTHON-REQUIREMENTS-LOCK.txt') -Raw) -ceq $requirements) 'The installed dependency lock must match the reviewed lock.'
    Require ((Get-Content -LiteralPath (Join-Path $payload 'runtime/site-packages/anyio-4.14.2.dist-info/licenses/LICENSE') -Raw) -ceq $licence) 'The installed AnyIO licence must match the verified wheel.'
    Require (Test-Path -LiteralPath (Join-Path $payload 'SEARXNG-SOURCE-OFFER.txt')) 'The corresponding-source offer must remain in the payload.'
    foreach ($entry in $inventory.files) {
        $file = [IO.Path]::GetFullPath((Join-Path $payload $entry.path))
        Assert-AIArenaPathWithinDirectory -Path $file -Directory $payload -Label 'Payload inventory file'
        Require ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $entry.sha256) "Payload inventory hash mismatch: $($entry.path)"
    }
}
Write-Host "SearXNG dependency security fixtures passed: $covered pristine upstream pins, verified AnyIO/Werkzeug wheels and licences, stale digest rejection, and optional payload path/gateway/inventory checks."
