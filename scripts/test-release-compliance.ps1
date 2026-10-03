param(
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [string]$NoticeManifestPath = (Join-Path $PSScriptRoot '../packaging/third-party/manifest.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-security.ps1')
$root = (Resolve-Path -LiteralPath $ReleaseDir).Path
$noticeRoot = Join-Path $root 'third-party'
if (-not (Test-Path -LiteralPath (Join-Path $noticeRoot 'manifest.json') -PathType Leaf)) {
    throw 'Missing reviewed dependency notices. Rebuild this release through the compliance pipeline.'
}
if ((Get-FileHash -LiteralPath (Join-Path $noticeRoot 'manifest.json')).Hash -ne (Get-FileHash -LiteralPath $NoticeManifestPath).Hash) {
    throw 'Installed dependency manifest differs from the reviewed manifest.'
}
$manifest = Get-Content -LiteralPath (Join-Path $noticeRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported dependency notice manifest.' }
$reviewed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $manifest.packages) {
    if (-not $reviewed.Add($package.package)) { throw "Duplicate package notice: $($package.package)" }
    if (@($package.files).Count -eq 0) { throw "Missing licence text: $($package.package)" }
    foreach ($entry in $package.files) {
        $file = [IO.Path]::GetFullPath((Join-Path $noticeRoot $entry.file))
        Assert-AIArenaPathWithinDirectory -Path $file -Directory $noticeRoot -Label 'Installed licence text'
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Installed licence hash mismatch: $($entry.file)"
        }
    }
}
$dependencyFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.deps.json')
if ($dependencyFiles.Count -eq 0) { throw 'No dependency inventory in release.' }
foreach ($deps in $dependencyFiles) {
    $data = Get-Content -LiteralPath $deps.FullName -Raw | ConvertFrom-Json
    foreach ($library in $data.libraries.PSObject.Properties) {
        if ($library.Value.type -notin @('package', 'runtimepack')) { continue }
        $name = $library.Name -replace '^runtimepack\.', ''
        if (-not $reviewed.Contains($name)) { throw "Dependency lacks reviewed licence coverage: $name" }
    }
}
foreach ($required in @('LICENSE', 'NOTICE.md', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Missing $required" }
}

$failures = [Collections.Generic.List[string]]::new()
$personalPath = '(?i)([A-Z]:[\\/]+AI Workspace[\\/]+Codex|[A-Z]:[\\/]+Users[\\/]+Cyber(?:[\\/]|$))'
$credentials = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|sk-(?:proj-|ant-)?[A-Za-z0-9_-]{24,}|(?:AKIA|ASIA)[A-Z0-9]{16})\b'
$checkout = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$currentUserRoot = [Environment]::GetFolderPath('UserProfile')
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName) -replace '\\', '/'
    if ($file.Extension -in @('.pdb', '.log', '.dmp', '.trx', '.sqlite', '.sqlite3', '.db', '.pfx', '.key') `
        -or $relative -match '(^|/)(\.claude|\.git|\.vs|TestResults|__pycache__|logs|data|artifacts|tests)/' `
        -or $file.Name -in @('.env', '.dev.vars', 'credentials.json', 'cookies.json')) {
        # SearXNG's upstream data/ contains required search-engine definitions.
        if ($relative -notmatch '^searxng/runtime/searx/data/') { $failures.Add("Unexpected artifact: $relative") }
    }
    if ($file.Extension -eq '.zip') { continue } # Pinned source/runtime archives verified separately below.
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($text in @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        if ($text -match $personalPath -or $text.IndexOf($checkout, [StringComparison]::OrdinalIgnoreCase) -ge 0 `
            -or ($currentUserRoot -and $text.IndexOf($currentUserRoot + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $failures.Add("Local build path: $relative")
        }
        if ($text -match $credentials) { $failures.Add("Credential pattern needs review: $relative") }
    }
}
if (Test-Path -LiteralPath (Join-Path $root 'searxng')) {
    $lock = Get-Content -LiteralPath (Join-Path $root 'searxng/UPSTREAM-LOCK.json') -Raw | ConvertFrom-Json
    $source = Join-Path $root 'searxng/source/searxng-upstream.zip'
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $lock.searxng.sha256) { throw 'SearXNG corresponding-source hash mismatch.' }
    if (Test-Path -LiteralPath (Join-Path $root 'searxng/runtime/site-packages/bin')) { $failures.Add('Unused pip console launchers remain.') }
}
if ($failures.Count -gt 0) { throw (($failures | Sort-Object -Unique) -join "`n") }
Write-Host "Release compliance gate passed: $($files.Count) files; reviewed dependency licences; no detected private paths, credential patterns, or release artifacts."
