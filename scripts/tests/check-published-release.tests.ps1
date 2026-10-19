$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repositoryRoot 'scripts/check-published-release.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ai-arena-public-download-' + [Guid]::NewGuid().ToString('N'))
$script:passed = 0

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [string]$ExpectedMessage)
    $failure = $null
    try { $null = & $Action } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notlike $ExpectedMessage) {
        throw "$Name should fail with '$ExpectedMessage'; actual failure: '$failure'."
    }
    $script:passed++
}

function New-ReleaseFixture {
    return [pscustomobject]@{
        tag_name = 'v0.4.148-beta'
        draft = $false
        published_at = '2026-10-13T09:32:24Z'
        html_url = 'https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases/tag/v0.4.148-beta'
        assets = @([pscustomobject]@{
            name = 'AI.Arena.Setup.0.4.148-beta.exe'
            state = 'uploaded'
            size = 111608154
            browser_download_url = 'https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases/download/v0.4.148-beta/AI.Arena.Setup.0.4.148-beta.exe'
        })
    }
}

try {
    $null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'packaging/changes') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'src/AIArena.Wpf') -Force
    '<Project><PropertyGroup><Version>0.4.150-beta</Version></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'src/AIArena.Wpf/AIArena.Wpf.csproj')
    'Published release notes fixture.' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'packaging/changes/0.4.148-beta.txt')
    $readmePath = Join-Path $fixtureRoot 'README.md'
    $validReadme = 'Published beta: [Download 0.4.148-beta](https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases/tag/v0.4.148-beta) | [Release notes](packaging/changes/0.4.148-beta.txt)'
    Set-Content -LiteralPath $readmePath -Value $validReadme
    $download = Get-AIArenaPublicDownload -RepositoryRoot $fixtureRoot
    $asset = Assert-AIArenaPublicRelease -Download $download -Release (New-ReleaseFixture)
    if ($download.Version -ne '0.4.148-beta' -or $asset.size -ne 111608154) {
        throw 'A public 0.4.148 release must remain valid while source development is at 0.4.150.'
    }
    $script:passed++

    $readmeCases = @(
        @{ Name = 'Missing download'; Text = 'No download'; Error = '*exactly one versioned Download link*' },
        @{ Name = 'Duplicate download'; Text = "$validReadme`n$validReadme"; Error = '*exactly one versioned Download link*' },
        @{ Name = 'Invalid version'; Text = $validReadme.Replace('Download 0.4.148-beta', 'Download latest'); Error = '*version*is invalid*' },
        @{ Name = 'Label and tag mismatch'; Text = $validReadme.Replace('tag/v0.4.148', 'tag/v0.4.150'); Error = '*download link must point at*' },
        @{ Name = 'Other repository'; Text = $validReadme.Replace('neeveew/', 'someone-else/'); Error = '*download link must point at*' },
        @{ Name = 'Other host'; Text = $validReadme.Replace('https://github.com/', 'https://example.com/'); Error = '*download link must point at*' },
        @{ Name = 'Mismatched notes'; Text = $validReadme.Replace('changes/0.4.148', 'changes/0.4.150'); Error = '*existing release notes*' }
    )
    foreach ($case in $readmeCases) {
        Set-Content -LiteralPath $readmePath -Value $case.Text
        Assert-Rejected $case.Name { Get-AIArenaPublicDownload -RepositoryRoot $fixtureRoot } $case.Error
    }
    Set-Content -LiteralPath $readmePath -Value $validReadme
    Remove-Item -LiteralPath (Join-Path $fixtureRoot 'packaging/changes/0.4.148-beta.txt')
    Assert-Rejected 'Missing notes file' { Get-AIArenaPublicDownload -RepositoryRoot $fixtureRoot } '*existing release notes*'

    $release = New-ReleaseFixture
    $release.draft = $true
    Assert-Rejected 'Owner-visible draft' { Assert-AIArenaPublicRelease $download $release } '*not a published release*'
    $release = New-ReleaseFixture
    $release.published_at = $null
    Assert-Rejected 'Missing publication date' { Assert-AIArenaPublicRelease $download $release } '*not a published release*'
    $release = New-ReleaseFixture
    $release.tag_name = 'v0.4.150-beta'
    Assert-Rejected 'Wrong release tag' { Assert-AIArenaPublicRelease $download $release } '*does not match*'
    $release = New-ReleaseFixture
    $release.html_url = 'https://example.com/release'
    Assert-Rejected 'Wrong release page' { Assert-AIArenaPublicRelease $download $release } '*does not match*'
    $release = New-ReleaseFixture
    $release.assets = @()
    Assert-Rejected 'No installer' { Assert-AIArenaPublicRelease $download $release } '*exactly one*installer*'
    $release = New-ReleaseFixture
    $release.assets = @($release.assets[0], $release.assets[0])
    Assert-Rejected 'Ambiguous installer' { Assert-AIArenaPublicRelease $download $release } '*exactly one*installer*'
    $release = New-ReleaseFixture
    $release.assets[0].size = 0
    Assert-Rejected 'Empty installer' { Assert-AIArenaPublicRelease $download $release } '*installer is empty*'
    $release = New-ReleaseFixture
    $release.assets[0].state = 'starter'
    Assert-Rejected 'Incomplete upload' { Assert-AIArenaPublicRelease $download $release } '*installer is empty*'
    $release = New-ReleaseFixture
    $release.assets[0].browser_download_url = 'https://example.com/installer.exe'
    Assert-Rejected 'Wrong asset URL' { Assert-AIArenaPublicRelease $download $release } '*installer is empty*'

    Write-Host "Public download validation: $script:passed fixtures passed."
}
finally {
    # Only clean this test's uniquely named directory under the system temp root.
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notmatch '^ai-arena-public-download-[0-9a-f]{32}$') {
        throw 'Refusing to clean an unexpected fixture directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
