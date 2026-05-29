param(
    [string]$Version = "0.2.0-beta",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$Force,
    [string[]]$Changes = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repoRoot "windows-wpf\src\AIArena.Wpf\AIArena.Wpf.csproj"
$output = Join-Path $repoRoot "dist\AI Arena - $Version"
$changesPath = Join-Path $output "changes.txt"

if ((Test-Path $output) -and -not $Force) {
    throw "Versioned output already exists: $output. Choose a new version or pass -Force."
}

if ((Test-Path $output) -and $Force) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

$publishArgs = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $output,
    "-p:PublishSingleFile=false",
    "-p:UseAppHost=true"
)

if ($SelfContained) {
    $publishArgs += "--self-contained"
    $publishArgs += "true"
} else {
    $publishArgs += "--self-contained"
    $publishArgs += "false"
}

dotnet @publishArgs

$exe = Join-Path $output "AI Arena.exe"
if (-not (Test-Path $exe)) {
    throw "Expected WPF executable was not created: $exe"
}

if ($Changes.Count -eq 0) {
    $Changes = @(
        "WPF beta build $Version",
        "See git history for detailed changes."
    )
}

$changeLines = @(
    "AI Arena $Version",
    "Built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "",
    "Changes:"
) + ($Changes | ForEach-Object { "- $_" })

Set-Content -LiteralPath $changesPath -Value $changeLines -Encoding UTF8

Write-Host "WPF release build created:"
Write-Host $output
Write-Host $exe
Write-Host $changesPath
