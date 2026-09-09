$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$generatorSource = Join-Path $repositoryRoot 'scripts/dependency-index.ps1'
$enginePath = (Get-Process -Id $PID).Path
$gitPath = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$fixtureName = 'ai-arena-dependency-index-' + [Guid]::NewGuid().ToString('N')
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $temporaryBase $fixtureName))
$indexPath = Join-Path $fixtureRoot 'docs/DEPENDENCY_INDEX.md'
$utf8 = New-Object Text.UTF8Encoding($false)
$fixtureCreated = $false

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-FixtureFile {
    param([string]$RelativePath, [string]$Text)
    $targetPath = Join-Path $fixtureRoot $RelativePath
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force)
    [IO.File]::WriteAllText($targetPath, $Text, $utf8)
}

function Invoke-NativeFixture {
    param([string]$Executable, [string[]]$Arguments)
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $Executable @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output -join [Environment]::NewLine
    }
}

function Invoke-Index {
    param([switch]$Check)
    $arguments = @('-NoProfile')
    if ([IO.Path]::GetFileNameWithoutExtension($enginePath) -ieq 'powershell') {
        $arguments += @('-ExecutionPolicy', 'Bypass')
    }
    $arguments += @('-File', (Join-Path $fixtureRoot 'run-index.ps1'))
    if ($Check) { $arguments += '-Check' }
    return Invoke-NativeFixture -Executable $enginePath -Arguments $arguments
}

function Get-IndexBytes {
    return [Convert]::ToBase64String([IO.File]::ReadAllBytes($indexPath))
}

$projectXml = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
$sourcePath = 'src/AIArena.Core/Modules/Fixture/Services/FixtureService.cs'
$testPath = 'tests/AIArena.Tests/FixtureTestClient.cs'
$sourceText = @'
namespace Fixture;
public interface IFixtureStore { }
public sealed class FixtureService(IFixtureStore store) { }
'@
$testText = @'
namespace Fixture.Tests;
public sealed class FixtureTestClient { }
'@

try {
    Require (-not (Test-Path -LiteralPath $fixtureRoot)) 'Refusing to reuse a pre-existing fixture directory.'
    [void](New-Item -ItemType Directory -Path $fixtureRoot)
    $fixtureCreated = $true
    [void](New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts'))
    Copy-Item -LiteralPath $generatorSource -Destination (Join-Path $fixtureRoot 'scripts/dependency-index.ps1')

    # Keep the generator unchanged. A fixture-local clock lets regenerated
    # document bytes compare exactly without stripping generated content.
    Write-FixtureFile 'run-index.ps1' @'
param([switch]$Check)
$ErrorActionPreference = 'Stop'
function Get-Date { return [datetime]::new(2020, 1, 2, 3, 4, 5) }
& (Join-Path $PSScriptRoot 'scripts/dependency-index.ps1') -Check:$Check
'@
    Write-FixtureFile '.gitignore' @'
artifacts/
.workspace/
**/bin/
**/obj/
'@
    Write-FixtureFile 'src/AIArena.Core/AIArena.Core.csproj' $projectXml
    Write-FixtureFile 'tests/AIArena.Tests/AIArena.Tests.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../src/AIArena.Core/AIArena.Core.csproj" /></ItemGroup>
</Project>
'@
    Write-FixtureFile $sourcePath $sourceText
    Write-FixtureFile $testPath $testText

    $initialize = Invoke-NativeFixture $gitPath @('-c', 'core.excludesFile=', '-C', $fixtureRoot, 'init', '--quiet')
    Require ($initialize.ExitCode -eq 0) "Could not initialize isolated fixture checkout: $($initialize.Output)"
    $generate = Invoke-Index
    Require ($generate.ExitCode -eq 0) "Initial dependency index generation failed: $($generate.Output)"
    Require (Test-Path -LiteralPath $indexPath -PathType Leaf) 'Generator did not create its default output.'
    $baselineBytes = Get-IndexBytes
    $baselineText = [IO.File]::ReadAllText($indexPath)
    foreach ($expected in @('FixtureService', 'IFixtureStore', 'FixtureTestClient',
        'src/AIArena.Core/AIArena.Core.csproj', 'tests/AIArena.Tests/AIArena.Tests.csproj')) {
        Require ($baselineText.Contains($expected)) "Baseline omitted durable source or project: $expected"
    }
    Require ($baselineText -notmatch [regex]::Escape($fixtureRoot)) 'Index leaked the fixture absolute path.'
    $initialCheck = Invoke-Index -Check
    Require ($initialCheck.ExitCode -eq 0) "Fresh index failed -Check: $($initialCheck.Output)"
    Require ((Get-IndexBytes) -ceq $baselineBytes) 'Initial -Check rewrote the generated index.'

    Write-FixtureFile 'artifacts/design/archived-preview/Preview.cs' @'
public sealed class FixtureService { }
public sealed class ArchivedPreviewService { }
'@
    Write-FixtureFile 'ArchivedRoot.cs' 'public sealed class RootArchiveService { }'
    foreach ($buildDirectory in @('src/AIArena.Core/bin', 'src/AIArena.Core/obj',
        'tests/AIArena.Tests/bin', 'tests/AIArena.Tests/obj')) {
        Write-FixtureFile "$buildDirectory/Generated.cs" 'public sealed class GeneratedOnlyService { }'
        Write-FixtureFile "$buildDirectory/Generated.csproj" $projectXml
    }

    $nestedRoot = Join-Path $fixtureRoot '.workspace/nested-checkout'
    Write-FixtureFile '.workspace/nested-checkout/src/AIArena.Core/AIArena.Core.csproj' $projectXml
    Write-FixtureFile '.workspace/nested-checkout/tests/AIArena.Tests/AIArena.Tests.csproj' $projectXml
    Write-FixtureFile '.workspace/nested-checkout/src/AIArena.Core/Modules/Fixture/Services/Archived.cs' @'
public sealed class FixtureService { }
public sealed class NestedArchiveService { }
'@
    Write-FixtureFile '.workspace/nested-checkout/tests/AIArena.Tests/ArchivedTests.cs' 'public sealed class NestedArchiveTestClient { }'
    $nestedInitialize = Invoke-NativeFixture $gitPath @('-c', 'core.excludesFile=', '-C', $nestedRoot, 'init', '--quiet')
    Require ($nestedInitialize.ExitCode -eq 0) "Could not initialize the nested fixture checkout: $($nestedInitialize.Output)"
    $ignored = Invoke-NativeFixture $gitPath @('-c', 'core.excludesFile=', '-C', $fixtureRoot,
        'check-ignore', '--no-index', '.workspace/nested-checkout/src/AIArena.Core/Modules/Fixture/Services/Archived.cs')
    Require ($ignored.ExitCode -eq 0) "Nested checkout source is not actually ignored: $($ignored.Output)"

    $excludedCheck = Invoke-Index -Check
    Require ($excludedCheck.ExitCode -eq 0) "Archived/root/build/nested source made the durable index stale: $($excludedCheck.Output)"
    Require ((Get-IndexBytes) -ceq $baselineBytes) '-Check modified bytes after excluded sources were added.'
    $regenerate = Invoke-Index
    Require ($regenerate.ExitCode -eq 0) "Regeneration with excluded sources failed: $($regenerate.Output)"
    Require ((Get-IndexBytes) -ceq $baselineBytes) 'Excluded sources changed regenerated dependency index bytes.'

    # Mutate an existing durable file rather than only adding a new path.
    Write-FixtureFile $sourcePath ($sourceText + [Environment]::NewLine + 'public sealed class AddedDurableService { }')
    $stale = Invoke-Index -Check
    Require ($stale.ExitCode -ne 0 -and $stale.Output -match 'Dependency index is stale') "Durable source mutation did not fail -Check as stale: $($stale.Output)"
    Require ((Get-IndexBytes) -ceq $baselineBytes) 'Failed -Check rewrote the stale dependency index.'
    $refresh = Invoke-Index
    Require ($refresh.ExitCode -eq 0) "Durable source regeneration failed: $($refresh.Output)"
    Require ((Get-IndexBytes) -cne $baselineBytes) 'A new durable type did not change generated index bytes.'
    $updatedText = [IO.File]::ReadAllText($indexPath)
    Require ($updatedText.Contains('AddedDurableService')) 'Regenerated index omitted the new durable type.'
    foreach ($excluded in @('ArchivedPreviewService', 'RootArchiveService', 'GeneratedOnlyService',
        'NestedArchiveService', 'NestedArchiveTestClient', 'nested-checkout', 'Generated.csproj')) {
        Require (-not $updatedText.Contains($excluded)) "Excluded source or project entered the refreshed index: $excluded"
    }
    $finalCheck = Invoke-Index -Check
    Require ($finalCheck.ExitCode -eq 0) "Refreshed durable index failed -Check: $($finalCheck.Output)"
    Write-Host 'PASS: dependency indexing ignores archived/nested/generated C# and detects durable source changes.'
}
finally {
    if ($fixtureCreated) {
        # Resolve the exact GUID-named directory and verify its parent before
        # recursively deleting anything. No production path is a cleanup target.
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\', '/')
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryBase).TrimEnd('\', '/')
        $resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedFixture)).TrimEnd('\', '/')
        $ownsFixture = [string]::Equals($resolvedParent, $resolvedTemporary, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedFixture) -ceq $fixtureName -and
            $fixtureName -match '^ai-arena-dependency-index-[a-f0-9]{32}$'
        Require $ownsFixture 'Refusing to clean a path outside the owned temporary fixture.'
        if (Test-Path -LiteralPath $resolvedFixture -PathType Container) {
            Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
        }
    }
}
