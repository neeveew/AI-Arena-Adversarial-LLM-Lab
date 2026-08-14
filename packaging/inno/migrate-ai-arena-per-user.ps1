[CmdletBinding()]
param(
    [string]$TestFixturePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-AIArenaLegacyMigrationPlan {
    param(
        [bool]$Found,
        [string]$InstallLocation,
        [string]$UninstallString,
        [string]$ExpectedDirectory
    )

    if (-not $Found) {
        return [pscustomobject]@{ ExitCode = 0; Uninstaller = '' }
    }

    try {
        $expected = [IO.Path]::GetFullPath($ExpectedDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $actual = [IO.Path]::GetFullPath($InstallLocation).TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    catch {
        return [pscustomobject]@{ ExitCode = 20; Uninstaller = '' }
    }

    if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ ExitCode = 20; Uninstaller = '' }
    }

    $match = [regex]::Match($UninstallString, '^\s*"(?<path>[^"]+)"(?:\s.*)?$')
    if (-not $match.Success) {
        return [pscustomobject]@{ ExitCode = 21; Uninstaller = '' }
    }

    try {
        $uninstaller = [IO.Path]::GetFullPath($match.Groups['path'].Value)
        $uninstallerName = [IO.Path]::GetFileName($uninstaller)
        $uninstallerDirectory = [IO.Path]::GetDirectoryName($uninstaller)
    }
    catch {
        return [pscustomobject]@{ ExitCode = 21; Uninstaller = '' }
    }

    if (-not $uninstallerDirectory.Equals($expected, [StringComparison]::OrdinalIgnoreCase) -or
        $uninstallerName -notmatch '^unins[0-9]{3}\.exe$') {
        return [pscustomobject]@{ ExitCode = 21; Uninstaller = '' }
    }

    return [pscustomobject]@{ ExitCode = 0; Uninstaller = $uninstaller }
}

function Test-AIArenaProcessElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not [string]::IsNullOrWhiteSpace($TestFixturePath)) {
    if ($env:AI_ARENA_INSTALLER_MIGRATION_TEST_MODE -ne '1') {
        exit 90
    }

    try {
        $fixture = Get-Content -LiteralPath $TestFixturePath -Raw | ConvertFrom-Json
        if ([bool]$fixture.elevated) {
            exit 24
        }
        $plan = Resolve-AIArenaLegacyMigrationPlan `
            -Found ([bool]$fixture.found) `
            -InstallLocation ([string]$fixture.installLocation) `
            -UninstallString ([string]$fixture.uninstallString) `
            -ExpectedDirectory ([string]$fixture.expectedDirectory)
        if ($plan.ExitCode -ne 0) {
            exit $plan.ExitCode
        }
        if (-not [bool]$fixture.found) {
            exit 0
        }
        if (-not [bool]$fixture.uninstallerExists) {
            exit 22
        }
        if ([int]$fixture.uninstallExitCode -ne 0 -or -not [bool]$fixture.registryRemoved) {
            exit 23
        }
        exit 0
    }
    catch {
        exit 91
    }
}

if (Test-AIArenaProcessElevated) {
    exit 24
}

try {
    $legacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61}_is1'
    $found = Test-Path -LiteralPath $legacyUninstallKey -PathType Container
    $legacy = if ($found) { Get-ItemProperty -LiteralPath $legacyUninstallKey } else { $null }
    $plan = Resolve-AIArenaLegacyMigrationPlan `
        -Found $found `
        -InstallLocation $(if ($found) { [string]$legacy.InstallLocation } else { '' }) `
        -UninstallString $(if ($found) { [string]$legacy.UninstallString } else { '' }) `
        -ExpectedDirectory (Join-Path $env:LOCALAPPDATA 'Programs\AI Arena')
    if ($plan.ExitCode -ne 0) {
        exit $plan.ExitCode
    }
    if (-not $found) {
        exit 0
    }
    if (-not (Test-Path -LiteralPath $plan.Uninstaller -PathType Leaf)) {
        exit 22
    }
    $process = Start-Process -FilePath $plan.Uninstaller `
        -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $legacyUninstallKey -PathType Container)) {
        exit 23
    }

    exit 0
}
catch {
    exit 23
}
