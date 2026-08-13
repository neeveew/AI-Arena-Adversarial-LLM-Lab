[CmdletBinding(DefaultParameterSetName = "Write")]
param(
    [Parameter(ParameterSetName = "Check")]
    [switch]$Check,

    [Parameter(ParameterSetName = "Write")]
    [switch]$Write,

    [string]$ManifestPath = "src/AIArena.Wpf/Help/Content/guide-manifest.json",
    [string]$OutputPath = "docs/USER_GUIDE.md"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedManifest = if ([IO.Path]::IsPathRooted($ManifestPath)) { $ManifestPath } else { Join-Path $repositoryRoot $ManifestPath }
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repositoryRoot $OutputPath }
$contentRoot = Split-Path -Parent $resolvedManifest
$maximumManifestBytes = 2 * 1024 * 1024
$maximumArticleBytes = 512 * 1024
$maximumArticles = 256
$middleDot = [char]0x00B7
$emDash = [char]0x2014
$allowedAppRoutes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'app/models',
    'app/settings/provider',
    'app/match-setup',
    'app/view/arena',
    'app/status-center',
    'app/view/agent',
    'app/view/collaborate',
    'app/view/experiment',
    'app/settings/debug',
    'app/settings/internet'
) | ForEach-Object { [void]$allowedAppRoutes.Add($_) }

function Assert-Guide([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "User Guide validation failed: $Message" }
}

function Required-Text([object]$Value, [string]$Path) {
    $text = [string]$Value
    Assert-Guide (-not [string]::IsNullOrWhiteSpace($text)) "$Path must be non-empty."
    return $text.Trim()
}

function Require-Unique([string[]]$Values, [string]$Path) {
    $duplicates = @($Values | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    Assert-Guide ($duplicates.Count -eq 0) "$Path contains duplicate values: $($duplicates -join ', ')."
}

function Test-SafeRelativeContentPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path)) { return $false }
    $normalized = $Path.Replace('\', '/')
    return $normalized -match '^Articles/[a-z0-9-]+/[a-z0-9-]+\.md$' -and $normalized -notmatch '(^|/)\.\.(/|$)'
}

function Validate-MarkdownLinks([string]$ArticleId, [string]$Text, [Collections.Generic.HashSet[string]]$ArticleIds) {
    $matches = [regex]::Matches($Text, '(?<!\!)\[[^\]]+\]\(([^)\s]+)(?:\s+"[^"]*")?\)')
    foreach ($match in $matches) {
        $target = $match.Groups[1].Value
        if ($target.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        if ($target.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($target -match '^help/([a-z0-9-]+)(?:#[a-z0-9-]+)?$') {
            Assert-Guide ($ArticleIds.Contains($Matches[1])) "article '$ArticleId' links to unknown article '$($Matches[1])'."
            continue
        }
        throw "User Guide validation failed: article '$ArticleId' has unsafe or unsupported link '$target'."
    }
}

Assert-Guide (Test-Path -LiteralPath $resolvedManifest -PathType Leaf) "Manifest not found: $resolvedManifest"
$manifestInfo = Get-Item -LiteralPath $resolvedManifest
Assert-Guide ($manifestInfo.Length -gt 0 -and $manifestInfo.Length -le $maximumManifestBytes) "Manifest must contain 1-$maximumManifestBytes bytes."
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw -Encoding UTF8 | ConvertFrom-Json

Assert-Guide ($manifest.schemaVersion -eq 'ai_arena.help_manifest.v1') "schemaVersion must be ai_arena.help_manifest.v1."
$guideVersion = Required-Text $manifest.guideVersion 'guideVersion'
$reviewedUtc = try { [datetimeoffset]::Parse([string]$manifest.reviewedUtc, [Globalization.CultureInfo]::InvariantCulture) } catch { $null }
Assert-Guide ($null -ne $reviewedUtc) "reviewedUtc must be an ISO date/time."

$groups = @($manifest.groups)
$articles = @($manifest.articles)
$journeys = @($manifest.journeys)
Assert-Guide ($groups.Count -gt 0) "groups must not be empty."
Assert-Guide ($articles.Count -gt 0) "articles must not be empty."
Assert-Guide ($articles.Count -le $maximumArticles) "articles must contain at most $maximumArticles items."
Assert-Guide ($journeys.Count -gt 0) "journeys must not be empty."

[string[]]$groupIds = @($groups | ForEach-Object { Required-Text $_.id 'groups[].id' })
[string[]]$articleIdsArray = @($articles | ForEach-Object { Required-Text $_.id 'articles[].id' })
[string[]]$journeyIds = @($journeys | ForEach-Object { Required-Text $_.id 'journeys[].id' })
Require-Unique $groupIds 'groups[].id'
Require-Unique $articleIdsArray 'articles[].id'
Require-Unique $journeyIds 'journeys[].id'

$groupIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$articleIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$groupIds | ForEach-Object { [void]$groupIdSet.Add($_) }
$articleIdsArray | ForEach-Object { [void]$articleIdSet.Add($_) }

foreach ($group in $groups) {
    $id = Required-Text $group.id 'groups[].id'
    Assert-Guide ($id -match '^[a-z0-9-]+$') "group id '$id' is not stable kebab-case."
    [void](Required-Text $group.title "group '$id'.title")
    [void](Required-Text $group.iconGlyph "group '$id'.iconGlyph")
    Assert-Guide ([int]$group.order -gt 0) "group '$id'.order must be positive."
}

foreach ($article in $articles) {
    $id = Required-Text $article.id 'articles[].id'
    Assert-Guide ($id -match '^[a-z0-9-]+$') "article id '$id' is not stable kebab-case."
    Assert-Guide ($groupIdSet.Contains([string]$article.groupId)) "article '$id' has unknown groupId '$($article.groupId)'."
    [void](Required-Text $article.title "article '$id'.title")
    [void](Required-Text $article.summary "article '$id'.summary")
    [void](Required-Text $article.iconGlyph "article '$id'.iconGlyph")
    [void](Required-Text $article.introducedVersion "article '$id'.introducedVersion")
    $reviewedVersion = Required-Text $article.reviewedVersion "article '$id'.reviewedVersion"
    Assert-Guide ($reviewedVersion -eq $guideVersion) "article '$id'.reviewedVersion must equal guideVersion '$guideVersion'."
    Assert-Guide ([int]$article.order -gt 0) "article '$id'.order must be positive."
    Assert-Guide (@($article.keywords).Count -gt 0) "article '$id'.keywords must not be empty."
    Assert-Guide (@($article.aliases).Count -gt 0) "article '$id'.aliases must not be empty."
    Assert-Guide ([string]$article.route -eq "help/$id") "article '$id'.route must be 'help/$id'."
    Assert-Guide (Test-SafeRelativeContentPath ([string]$article.contentFile)) "article '$id'.contentFile is unsafe or unsupported."

    foreach ($relatedId in @($article.relatedArticleIds)) {
        Assert-Guide ($articleIdSet.Contains([string]$relatedId)) "article '$id' has unknown related article '$relatedId'."
        Assert-Guide ([string]$relatedId -ne $id) "article '$id' cannot relate to itself."
    }
    foreach ($action in @($article.actions)) {
        [void](Required-Text $action.label "article '$id'.actions[].label")
        Assert-Guide ($allowedAppRoutes.Contains([string]$action.route)) "article '$id' has unsupported app action route '$($action.route)'."
    }

    $articlePath = Join-Path $contentRoot ([string]$article.contentFile)
    Assert-Guide (Test-Path -LiteralPath $articlePath -PathType Leaf) "article '$id' content is missing: $articlePath"
    $articleInfo = Get-Item -LiteralPath $articlePath
    Assert-Guide ($articleInfo.Length -gt 0 -and $articleInfo.Length -le $maximumArticleBytes) "article '$id' must contain 1-$maximumArticleBytes bytes."
    $articleText = (Get-Content -LiteralPath $articlePath -Raw -Encoding UTF8).Trim()
    Assert-Guide ($articleText.Length -ge 120) "article '$id' content is unexpectedly short."
    Validate-MarkdownLinks $id $articleText $articleIdSet
}

foreach ($journey in $journeys) {
    $id = Required-Text $journey.id 'journeys[].id'
    [void](Required-Text $journey.title "journey '$id'.title")
    [void](Required-Text $journey.summary "journey '$id'.summary")
    [void](Required-Text $journey.iconGlyph "journey '$id'.iconGlyph")
    Assert-Guide ([int]$journey.order -gt 0) "journey '$id'.order must be positive."
    $journeyArticles = @($journey.articleIds | ForEach-Object { [string]$_ })
    Assert-Guide ($journeyArticles.Count -gt 0) "journey '$id'.articleIds must not be empty."
    foreach ($articleId in $journeyArticles) {
        Assert-Guide ($articleIdSet.Contains($articleId)) "journey '$id' references unknown article '$articleId'."
    }
    Assert-Guide ($journeyArticles -contains [string]$journey.startArticleId) "journey '$id'.startArticleId must occur in articleIds."
}

foreach ($group in $groups) {
    Assert-Guide (@($articles | Where-Object groupId -eq $group.id).Count -gt 0) "group '$($group.id)' contains no articles."
}

$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('# AI Arena Help Center')
[void]$builder.AppendLine()
[void]$builder.AppendLine("> Generated from the versioned offline Help Center content. Do not edit this file directly; run ``scripts/user-guide-content.ps1 -Write``.")
[void]$builder.AppendLine('>')
[void]$builder.AppendLine("> Guide version: **$guideVersion** $middleDot Reviewed: **$($reviewedUtc.ToString('yyyy-MM-dd'))**")
[void]$builder.AppendLine()
[void]$builder.AppendLine('AI Arena is a native Windows app for structured conversations between local or OpenAI-compatible language models. The in-app Help Center and this full Markdown guide use the same article source.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('## Contents')
[void]$builder.AppendLine()

$orderedGroups = @($groups | Sort-Object @{ Expression = { [int]$_.order } }, @{ Expression = { [string]$_.id } })
foreach ($group in $orderedGroups) {
    [void]$builder.AppendLine("- **$($group.title)**")
    foreach ($article in @($articles | Where-Object groupId -eq $group.id | Sort-Object @{ Expression = { [int]$_.order } }, @{ Expression = { [string]$_.id } })) {
        [void]$builder.AppendLine("  - [$($article.title)](#article-$($article.id)) $emDash $($article.summary)")
    }
}

foreach ($group in $orderedGroups) {
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("# $($group.title)")
    foreach ($article in @($articles | Where-Object groupId -eq $group.id | Sort-Object @{ Expression = { [int]$_.order } }, @{ Expression = { [string]$_.id } })) {
        $articlePath = Join-Path $contentRoot ([string]$article.contentFile)
        $articleText = (Get-Content -LiteralPath $articlePath -Raw -Encoding UTF8).Trim()
        $articleText = [regex]::Replace($articleText, '\]\(help/([a-z0-9-]+)(#[a-z0-9-]+)?\)', {
            param($match)
            # The portable full guide gives every article a stable explicit anchor.
            # A cross-article heading fragment cannot be appended to that fragment
            # without producing an invalid double-fragment URL, so it safely lands
            # at the target article. The in-app renderer preserves the heading route.
            "](#article-$($match.Groups[1].Value))"
        })
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("<a id=`"article-$($article.id)`"></a>")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("## $($article.title)")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("*$($article.summary)*")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine($articleText)
    }
}

$generated = $builder.ToString().TrimEnd() + [Environment]::NewLine

if ($Check) {
    Assert-Guide (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) "Generated output is missing: $resolvedOutput"
    $current = Get-Content -LiteralPath $resolvedOutput -Raw -Encoding UTF8
    Assert-Guide ($current -ceq $generated) "Generated output is stale. Run scripts/user-guide-content.ps1 -Write."
    Write-Host "User Guide content is valid and synchronized: $($articles.Count) articles, $($groups.Count) groups, $($journeys.Count) journeys."
    exit 0
}

$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
[IO.File]::WriteAllText($resolvedOutput, $generated, [Text.UTF8Encoding]::new($false))
Write-Host "Generated $resolvedOutput from $($articles.Count) validated Help Center articles."
