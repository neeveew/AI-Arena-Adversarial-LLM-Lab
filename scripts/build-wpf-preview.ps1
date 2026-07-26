param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $repoRoot "scripts\release-security.ps1")
$project = Join-Path $repoRoot "src\AIArena.Wpf\AIArena.Wpf.csproj"
$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$output = [IO.Path]::GetFullPath((Join-Path $distRoot "AI Arena WPF"))
Assert-AIArenaPathWithinDirectory -Path $output -Directory $distRoot -Label 'Preview publish output'
if ((Split-Path -Leaf $output) -ne 'AI Arena WPF') {
    throw "Preview output leaf is not the expected fixed directory."
}

if (Test-Path -LiteralPath $output) {
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
    throw "Expected preview executable was not created: $exe"
}

Write-Host "WPF preview build created:"
Write-Host $exe
