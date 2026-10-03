$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$gate = Join-Path $repo 'scripts/test-release-compliance.ps1'
$fixture = Join-Path $repo ('artifacts/release-compliance-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'third-party') -Force | Out-Null
function Expect-Failure([string]$Pattern) {
    $message = ''
    try { & $gate -ReleaseDir $fixture -NoticeManifestPath (Join-Path $fixture 'third-party/manifest.json') } catch { $message = $_.Exception.Message }
    if (-not $message -or $message -notmatch $Pattern) { throw "Expected rejection: $Pattern; got: $message" }
}
# A tiny independent fixture verifies the gate's rejection behavior without
# starting the app or touching user data, credentials, or installed releases.
foreach ($name in @('LICENSE', 'NOTICE.md', 'THIRD-PARTY-NOTICES.md')) {
    'Synthetic release-compliance fixture.' | Set-Content -LiteralPath (Join-Path $fixture $name)
}
$licence = Join-Path $fixture 'third-party/fixture-licence.txt'
'Synthetic licence text.' | Set-Content -LiteralPath $licence
$manifest = @{schemaVersion=1; packages=@(@{package='Fixture/1.0.0';files=@(@{file='fixture-licence.txt';sha256=(Get-FileHash $licence).Hash})})}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $fixture 'third-party/manifest.json')
$deps = Join-Path $fixture 'fixture.deps.json'
'{"libraries":{"Fixture/1.0.0":{"type":"package"}}}' | Set-Content $deps
& $gate -ReleaseDir $fixture -NoticeManifestPath (Join-Path $fixture 'third-party/manifest.json')

$probe = Join-Path $fixture 'probe.txt'
('C:' + '\Users\Cyber\private.txt') | Set-Content $probe
Expect-Failure 'Local build path'
('ghp_' + ('X' * 36)) | Set-Content $probe
Expect-Failure 'Credential pattern'
'clean' | Set-Content $probe

'{"libraries":{"Unreviewed/2.0.0":{"type":"package"}}}' | Set-Content $deps
Expect-Failure 'lacks reviewed licence'
'{"libraries":{"Fixture/1.0.0":{"type":"package"}}}' | Set-Content $deps
'tampered licence' | Set-Content $licence
Expect-Failure 'licence hash mismatch'
'Synthetic licence text.' | Set-Content $licence
$pdb = Join-Path $fixture 'app.pdb'
'Synthetic debug symbol.' | Set-Content $pdb
Expect-Failure 'Unexpected artifact'
Remove-Item -LiteralPath $pdb
& $gate -ReleaseDir $fixture -NoticeManifestPath (Join-Path $fixture 'third-party/manifest.json')
Write-Host 'Release compliance fixtures passed (clean input, build path, credential, unreviewed dependency, tampered licence, debug symbol).'
