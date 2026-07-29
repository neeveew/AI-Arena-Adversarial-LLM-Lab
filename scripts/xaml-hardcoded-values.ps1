param(
    [switch]$Check
)

# Counts hard-coded layout and type values in XAML and holds the total to a
# baseline, so the backlog can be worked down without quietly growing again.
#
# The app ships a design-token vocabulary in UI/Theming/DesignTokens.xaml, but
# adoption is partial: the spacing scale in particular has token definitions and
# almost no references, while these four properties carry hundreds of literal
# values. A literal is anything whose value is not a markup extension, so
# Margin="8,0" counts and Margin="{StaticResource Arena.Space.2}" does not.
#
# This is a ratchet, not a gate on perfection. Going below the baseline is fine
# and is reported; going above it fails.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sourceRoot = Join-Path $Root "src"
$baselinePath = Join-Path $Root "docs/xaml-hardcoded-baseline.json"
$trackedProperties = @('Margin', 'Padding', 'CornerRadius', 'FontSize')
$excludedPathPattern = '[\\/](bin|obj|dist|map|\.git)[\\/]'

function Get-AIArenaXamlLiteralCounts {
    param([string]$SourceRoot, [string[]]$Properties)

    $alternation = ($Properties -join '|')
    $counts = [ordered]@{}
    foreach ($property in $Properties) {
        $counts[$property] = 0
    }

    $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -Filter "*.xaml" -File |
        Where-Object { $_.FullName -notmatch $excludedPathPattern } |
        Sort-Object FullName)

    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw

        # Setter elements carry the property in an attribute rather than in the
        # element name, and Property is not always the first attribute:
        # <Setter TargetName="ThumbChrome" Property="Margin" Value="4,2" />
        # So match the tag, then read its attributes independently of order.
        foreach ($tag in [regex]::Matches($text, '<Setter\b[^>]*>')) {
            $property = [regex]::Match($tag.Value, "\bProperty=`"($alternation)`"")
            $value = [regex]::Match($tag.Value, "\bValue=`"([^`"]*)`"")
            if ($property.Success -and $value.Success -and -not (Test-AIArenaMarkupExtension $value.Groups[1].Value)) {
                $counts[$property.Groups[1].Value]++
            }
        }

        foreach ($match in [regex]::Matches($text, "\b($alternation)=`"([^`"]*)`"")) {
            if (-not (Test-AIArenaMarkupExtension $match.Groups[2].Value)) {
                $counts[$match.Groups[1].Value]++
            }
        }
    }

    return $counts
}

function Test-AIArenaMarkupExtension {
    param([string]$Value)
    # {StaticResource ...}, {TemplateBinding ...}, {Binding ...} are references,
    # not literals. Everything else is a value written into the markup.
    return $Value.TrimStart().StartsWith('{')
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Source root not found: $sourceRoot"
}

$counts = Get-AIArenaXamlLiteralCounts -SourceRoot $sourceRoot -Properties $trackedProperties
$total = 0
foreach ($property in $trackedProperties) {
    $total += $counts[$property]
}

if (-not $Check) {
    $payload = [ordered]@{
        note = 'Ratchet baseline for hard-coded XAML layout and type values. Regenerate with scripts/xaml-hardcoded-values.ps1 after reducing a count.'
        generatedBy = 'scripts/xaml-hardcoded-values.ps1'
        properties = $counts
        total = $total
    }
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $baselinePath -Encoding UTF8
    Write-Host "Wrote XAML hard-coded baseline: docs/xaml-hardcoded-baseline.json"
    foreach ($property in $trackedProperties) {
        Write-Host ("  {0,-13} {1,4}" -f $property, $counts[$property])
    }
    Write-Host ("  {0,-13} {1,4}" -f 'total', $total)
    return
}

if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
    throw "XAML hard-coded baseline is missing: docs/xaml-hardcoded-baseline.json. Run .\scripts\xaml-hardcoded-values.ps1 to create it."
}

$baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
$regressions = New-Object System.Collections.Generic.List[string]
foreach ($property in $trackedProperties) {
    $allowed = [int]$baseline.properties.$property
    $actual = [int]$counts[$property]
    if ($actual -gt $allowed) {
        $regressions.Add("  $property : $actual, baseline $allowed (+$($actual - $allowed))")
    }
}

if ($regressions.Count -gt 0) {
    throw ("XAML gained hard-coded values that the design tokens already cover:" +
        [Environment]::NewLine + ($regressions -join [Environment]::NewLine) + [Environment]::NewLine +
        "Use a token from UI/Theming/DesignTokens.xaml, or run .\scripts\xaml-hardcoded-values.ps1 to move the baseline deliberately.")
}

$baselineTotal = [int]$baseline.total
if ($total -lt $baselineTotal) {
    Write-Host "XAML hard-coded values are below baseline: $total, was $baselineTotal (-$($baselineTotal - $total))."
    Write-Host "Run .\scripts\xaml-hardcoded-values.ps1 to lock the improvement in."
    return
}

Write-Host "XAML hard-coded values are at baseline: $total"
