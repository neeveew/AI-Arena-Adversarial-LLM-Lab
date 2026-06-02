param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\AIArena.Wpf\AIArena.Wpf.csproj"
$output = Join-Path $repoRoot "dist\AI Arena WPF"

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
    throw "Expected preview executable was not created: $exe"
}

Write-Host "WPF preview build created:"
Write-Host $exe
