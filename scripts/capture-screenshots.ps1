<#
.SYNOPSIS
    Regenerates the repository screenshots from a running AI Arena build.

.DESCRIPTION
    Drives AI Arena over the PowerShell control plane and writes one PNG per shot.
    Running this after a GUI change keeps README and docs imagery from drifting
    away from the app.

    Each shot runs in its own app session. The app prints a "Screenshot saved"
    receipt into its status line after every capture, so reusing one session
    would bake that receipt into every later screenshot.

    The caller's persisted theme is restored when the last session closes.

.EXAMPLE
    .\scripts\capture-screenshots.ps1
    .\scripts\capture-screenshots.ps1 -Configuration Release -OutputDirectory docs\assets
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$OutputDirectory = '',
    [string]$RestoreTheme = 'dark-blue',
    [int]$StartupTimeoutSeconds = 40
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repoRoot
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
if (-not (Test-Path $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

$exe = Join-Path $repoRoot "src\AIArena.Wpf\bin\$Configuration\net10.0-windows\AI Arena.exe"
if (-not (Test-Path $exe)) {
    throw "AI Arena build not found at $exe. Build the $Configuration configuration first."
}

. (Join-Path $repoRoot 'scripts\ai-arena-control.ps1')

# Each shot: file name, theme, and the setup applied before capturing.
$shots = @(
    @{ Name = 'screenshot-transcript.png';   Theme = 'dark-blue'; Setup = { Select-AIArenaView 'arena' | Out-Null } }
    @{ Name = 'screenshot-custom-match.png'; Theme = 'dark-blue'; Setup = { Open-AIArenaMatchSetup | Out-Null } }
    @{ Name = 'screenshot-light.png';        Theme = 'light';     Setup = { Select-AIArenaView 'arena' | Out-Null } }
)

function Wait-AIArenaReady([int]$timeoutSeconds) {
    # The control plane pipe only answers once the shell has finished loading.
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        try {
            $null = Invoke-AIArena status -TimeoutMs 2000
            return $true
        } catch {
            continue
        }
    }
    return $false
}

foreach ($shot in $shots) {
    $process = Start-Process -FilePath $exe -PassThru
    try {
        if (-not (Wait-AIArenaReady $StartupTimeoutSeconds)) {
            throw "AI Arena did not answer the control plane within $StartupTimeoutSeconds seconds."
        }

        $null = Set-AIArenaTheme $shot.Theme
        Start-Sleep -Milliseconds 600
        & $shot.Setup
        Start-Sleep -Milliseconds 900

        $target = Join-Path $outputRoot $shot.Name
        if (Test-Path $target) {
            Remove-Item $target -Force
        }

        $capture = Save-AIArenaScreenshot $target
        if (-not $capture.ok) {
            throw "Screenshot failed for $($shot.Name): $($capture.message)"
        }

        "{0}  ({1:N0} bytes)" -f $shot.Name, (Get-Item $target).Length
    } finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }
}

# The theme is persisted, so leave the caller's preference in place.
$process = Start-Process -FilePath $exe -PassThru
try {
    if (Wait-AIArenaReady $StartupTimeoutSeconds) {
        $null = Set-AIArenaTheme $RestoreTheme
    }
} finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

"Screenshots written to $outputRoot"
