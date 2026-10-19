param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Shared with release sanity: building a new version must not advertise an
# unpublished download. Only the publisher advances this link after publication.
function Get-AIArenaPublicDownload {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $readme = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'README.md') -Raw
    $links = [regex]::Matches($readme, '\[Download\s+(?<version>[^\]]+)\]\(\s*(?<url>[^)\s]+)\s*\)')
    if ($links.Count -ne 1) {
        throw 'README must contain exactly one versioned Download link.'
    }

    $version = $links[0].Groups['version'].Value.Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw "README download version '$version' is invalid."
    }
    $repository = 'neeveew/AI-Arena-Adversarial-LLM-Lab'
    $releaseUrl = "https://github.com/$repository/releases/tag/v$version"
    if ($links[0].Groups['url'].Value -cne $releaseUrl) {
        throw "README download link must point at $releaseUrl."
    }

    $notesPath = "packaging/changes/$version.txt"
    $notes = [regex]::Matches($readme, '\[Release notes\]\(\s*(?<path>[^)\s]+)\s*\)')
    if ($notes.Count -ne 1 -or $notes[0].Groups['path'].Value -cne $notesPath -or
        -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $notesPath) -PathType Leaf)) {
        throw "README must link to the existing release notes at $notesPath."
    }

    $assetName = "AI.Arena.Setup.$version.exe"
    return [pscustomobject]@{
        Version = $version
        Repository = $repository
        Tag = "v$version"
        ReleaseUrl = $releaseUrl
        AssetName = $assetName
        AssetUrl = "https://github.com/$repository/releases/download/v$version/$assetName"
    }
}

function Assert-AIArenaPublicRelease {
    param(
        [Parameter(Mandatory = $true)]$Download,
        [Parameter(Mandatory = $true)]$Release
    )

    # Owner credentials can see drafts; a successful authenticated API call is
    # not evidence that visitors can download the release.
    if ($Release.draft -isnot [bool] -or $Release.draft -ne $false -or
        [string]::IsNullOrWhiteSpace([string]$Release.published_at)) {
        throw "$($Download.Tag) is not a published release (draft or missing publication date)."
    }
    if ($Release.tag_name -cne $Download.Tag -or $Release.html_url -cne $Download.ReleaseUrl) {
        throw 'GitHub release does not match the README download link.'
    }
    $assets = @($Release.assets | Where-Object { $_.name -ceq $Download.AssetName })
    if ($assets.Count -ne 1) {
        throw "Published release must contain exactly one $($Download.AssetName) installer."
    }
    $asset = $assets[0]
    if ($asset.state -cne 'uploaded' -or $asset.size -le 0 -or
        $asset.browser_download_url -cne $Download.AssetUrl) {
        throw 'Published installer is empty, unfinished, or has an unexpected download URL.'
    }
    return $asset
}

if ($MyInvocation.InvocationName -ne '.') {
    $download = Get-AIArenaPublicDownload -RepositoryRoot $Root
    Write-Host "README advertises $($download.Tag). Source build versions may be newer."
    $releaseJson = & gh api "repos/$($download.Repository)/releases/tags/$($download.Tag)"
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot read the advertised release $($download.Tag) from GitHub."
    }
    $release = ($releaseJson -join "`n") | ConvertFrom-Json
    $asset = Assert-AIArenaPublicRelease -Download $download -Release $release

    # No Authorization header or GitHub token: exercise the public links using
    # HEAD requests, without downloading the 100+ MB installer on every run.
    foreach ($url in @($download.ReleaseUrl, $download.AssetUrl)) {
        $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 45 -MaximumRedirection 5
        if ($response.StatusCode -ne 200) {
            throw "Public URL returned HTTP $($response.StatusCode): $url"
        }
        if ($url -ceq $download.AssetUrl -and
            [long]($response.Headers['Content-Length'] | Select-Object -First 1) -ne [long]$asset.size) {
            throw 'Public installer size does not match GitHub release metadata.'
        }
    }
    Write-Host "$($download.Tag) and $($download.AssetName) are publicly available ($($asset.size) bytes)."
}
