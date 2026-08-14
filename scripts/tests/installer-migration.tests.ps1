$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$migrationScript = Join-Path $repositoryRoot 'packaging\inno\migrate-ai-arena-per-user.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ai-arena-installer-migration-{0}" -f [Guid]::NewGuid().ToString('N'))
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$expectedDirectory = 'C:\Users\Fixture\AppData\Local\Programs\AI Arena'
$validUninstaller = Join-Path $expectedDirectory 'unins000.exe'
$previousTestMode = $env:AI_ARENA_INSTALLER_MIGRATION_TEST_MODE

function Require-Equal {
    param(
        [int]$Actual,
        [int]$Expected,
        [string]$Label
    )

    if ($Actual -ne $Expected) {
        throw "$Label returned $Actual; expected $Expected."
    }
}

function Invoke-MigrationFixture {
    param(
        [hashtable]$Fixture,
        [string]$Name
    )

    $path = Join-Path $testRoot "$Name.json"
    $Fixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8
    & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $migrationScript -TestFixturePath $path
    return $LASTEXITCODE
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $env:AI_ARENA_INSTALLER_MIGRATION_TEST_MODE = '1'

    $baseFixture = @{
        elevated = $false
        found = $true
        expectedDirectory = $expectedDirectory
        installLocation = "$expectedDirectory\"
        uninstallString = '"' + $validUninstaller + '"'
        uninstallerExists = $true
        uninstallExitCode = 0
        registryRemoved = $true
    }

    Require-Equal (Invoke-MigrationFixture (@{
        elevated = $false
        found = $false
        expectedDirectory = $expectedDirectory
        installLocation = ''
        uninstallString = ''
        uninstallerExists = $false
        uninstallExitCode = 0
        registryRemoved = $false
    }) 'absent') 0 'Absent legacy install'
    Require-Equal (Invoke-MigrationFixture $baseFixture 'valid') 0 'Validated default install'

    $elevated = $baseFixture.Clone()
    $elevated.elevated = $true
    Require-Equal (Invoke-MigrationFixture $elevated 'elevated') 24 'Elevated helper token'

    $customDirectory = $baseFixture.Clone()
    $customDirectory.installLocation = 'D:\Custom\AI Arena'
    Require-Equal (Invoke-MigrationFixture $customDirectory 'custom-directory') 20 'Custom legacy directory'

    $untrustedCommand = $baseFixture.Clone()
    $untrustedCommand.uninstallString = '"C:\Windows\System32\cmd.exe" /c exit 0'
    Require-Equal (Invoke-MigrationFixture $untrustedCommand 'untrusted-command') 21 'Untrusted uninstall command'

    $missingUninstaller = $baseFixture.Clone()
    $missingUninstaller.uninstallerExists = $false
    Require-Equal (Invoke-MigrationFixture $missingUninstaller 'missing-uninstaller') 22 'Missing legacy uninstaller'

    $failedUninstall = $baseFixture.Clone()
    $failedUninstall.uninstallExitCode = 1
    Require-Equal (Invoke-MigrationFixture $failedUninstall 'failed-uninstall') 23 'Failed legacy uninstall'

    $staleRegistry = $baseFixture.Clone()
    $staleRegistry.registryRemoved = $false
    Require-Equal (Invoke-MigrationFixture $staleRegistry 'stale-registry') 23 'Legacy registry key left behind'

    $env:AI_ARENA_INSTALLER_MIGRATION_TEST_MODE = '0'
    Require-Equal (Invoke-MigrationFixture $baseFixture 'test-mode-guard') 90 'Fixture mode without explicit test guard'

    & $powershell -NoLogo -NoProfile -NonInteractive -Command 'exit 0'
    if ($LASTEXITCODE -ne 0) {
        throw "Installer migration fixtures could not restore a successful native exit state."
    }
    Write-Host 'Installer migration fixture tests passed.'
}
finally {
    if ($null -eq $previousTestMode) {
        Remove-Item Env:\AI_ARENA_INSTALLER_MIGRATION_TEST_MODE -ErrorAction SilentlyContinue
    }
    else {
        $env:AI_ARENA_INSTALLER_MIGRATION_TEST_MODE = $previousTestMode
    }

    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTestRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot).StartsWith('ai-arena-installer-migration-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
