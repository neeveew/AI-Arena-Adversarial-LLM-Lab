param([Parameter(Mandatory = $true)][string]$ReleaseDir)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'release-security.ps1')
$output = (Resolve-Path -LiteralPath $ReleaseDir).Path
foreach ($marker in @('release-manifest.txt', 'release-checksums.sha256')) {
    if (Test-Path -LiteralPath (Join-Path $output $marker)) {
        throw 'Refusing to change a finalized release; rebuild into a fresh directory.'
    }
}

# Vendor PDBs are also unnecessary in the consumer installer. Keep symbols in
# private build outputs; never patch compiled binaries to conceal build paths.
foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pdb') {
    Assert-AIArenaPathWithinDirectory -Path $file.FullName -Directory $output -Label 'Published debug symbol'
    Remove-Item -LiteralPath $file.FullName
}

$source = Join-Path $repoRoot 'packaging/third-party'
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported dependency notice manifest.' }
$destination = Join-Path $output 'third-party'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$lines = @(
    '# Third-party components', '',
    'These components retain their own licences. AI Arena''s no-derivatives licence does not restrict the rights granted by them.', '',
    'This inventory includes the .NET runtime, NuGet dependencies and Roslyn build-host dependencies. Some packages contribute build tools rather than installed application code.', '',
    '| Component / version | Licence | Attribution | Licence files |',
    '| --- | --- | --- | --- |'
)
foreach ($package in $manifest.packages) {
    if (@($package.files).Count -eq 0) { throw "No licence files for $($package.package)." }
    $links = foreach ($entry in $package.files) {
        $inputFile = [IO.Path]::GetFullPath((Join-Path $source $entry.file))
        $outputFile = [IO.Path]::GetFullPath((Join-Path $destination $entry.file))
        Assert-AIArenaPathWithinDirectory -Path $inputFile -Directory $source -Label 'Licence input'
        Assert-AIArenaPathWithinDirectory -Path $outputFile -Directory $destination -Label 'Licence output'
        if ((Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Licence text differs from the reviewed hash: $($entry.file)"
        }
        New-Item -ItemType Directory -Path (Split-Path $outputFile -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $inputFile -Destination $outputFile
        '[{0}](third-party/{1})' -f ([IO.Path]::GetFileName($entry.file)), $entry.file
    }
    $attribution = (@($package.copyright, $package.authors) | Where-Object { $_ }) -join '; '
    $lines += '| {0} | {1} | {2} | {3} |' -f $package.package, $package.license, ($attribution -replace '\|', '/'), ($links -join ', ')
}
$lines += @('',
    'The SearXNG service and its local JSON gateway use AGPL-3.0-or-later. See searxng/LICENSE and searxng/SEARXNG-SOURCE-OFFER.txt. Its full pinned upstream source is bundled in searxng/source/searxng-upstream.zip.', '',
    'CPython and Python dependency notices remain under searxng/python and searxng/runtime/site-packages/*dist-info. The exact Python dependency inventory is searxng/payload-inventory.json.', '',
    'AngleSharp''s package declares MIT but does not identify a source commit. Its package copyright and the upstream MIT text are retained; this is not a claim of reproducible source-to-binary provenance.')
Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination (Join-Path $destination 'manifest.json')
$lines | Set-Content -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES.md') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'NOTICE.md') -Destination $output
Write-Host "Prepared reviewed notices for $(@($manifest.packages).Count) package versions."
