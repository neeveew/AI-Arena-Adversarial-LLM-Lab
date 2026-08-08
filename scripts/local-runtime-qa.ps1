[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$LlamaBaseUrl = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'AI Arena.slnx'
$wpfTests = Join-Path $repositoryRoot 'tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj'
$coreTests = Join-Path $repositoryRoot 'tests\AIArena.Tests\AIArena.Tests.csproj'
$results = [System.Collections.Generic.List[object]]::new()

function Add-QaResult {
    param(
        [Parameter(Mandatory)] [string]$Gate,
        [Parameter(Mandatory)] [string]$Status,
        [Parameter(Mandatory)] [string]$Evidence
    )

    $results.Add([pscustomobject]@{
        Gate = $Gate
        Status = $Status
        Evidence = $Evidence
    })
}

function Invoke-DotNetGate {
    param(
        [Parameter(Mandatory)] [string]$Gate,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [string]$TestFilter = ""
    )

    $previousFilter = $env:AIARENA_TEST_FILTER
    try {
        if ([string]::IsNullOrWhiteSpace($TestFilter)) {
            Remove-Item Env:AIARENA_TEST_FILTER -ErrorAction SilentlyContinue
        }
        else {
            $env:AIARENA_TEST_FILTER = $TestFilter
        }

        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            Add-QaResult $Gate 'fail' "dotnet exited with code $LASTEXITCODE."
            throw "QA gate '$Gate' failed."
        }

        Add-QaResult $Gate 'pass' 'Command completed with exit code 0.'
    }
    finally {
        if ($null -eq $previousFilter) {
            Remove-Item Env:AIARENA_TEST_FILTER -ErrorAction SilentlyContinue
        }
        else {
            $env:AIARENA_TEST_FILTER = $previousFilter
        }
    }
}

function Assert-NoBundledLlamaRuntime {
    $scannedRoots = @(
        (Join-Path $repositoryRoot 'src'),
        (Join-Path $repositoryRoot 'packaging')
    )
    $forbidden = @(
        foreach ($root in $scannedRoots) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                continue
            }

            Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction Stop |
                Where-Object {
                    $_.Name -match '^(llama-server|llama-cli)(\.exe|\.dll|\.so|\.dylib)?$'
                }
        }
    )

    if ($forbidden.Count -gt 0) {
        $paths = $forbidden | ForEach-Object {
            [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
        }
        Add-QaResult 'runtime-not-bundled' 'fail' ("Bundled llama.cpp runtime files: " + ($paths -join ', '))
        throw 'A user-installed llama.cpp runtime must not be added to the application or installer payload.'
    }

    Add-QaResult 'runtime-not-bundled' 'pass' 'No llama-server or llama-cli binary is present under src or packaging.'
}

function Invoke-OptionalLlamaProbe {
    param([AllowEmptyString()] [string]$BaseUrl = "")

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        Add-QaResult 'live-llama-readiness' 'unavailable' 'No -LlamaBaseUrl was supplied; deterministic fixture tests remain authoritative for this run.'
        return
    }

    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl.Trim(), [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or
        -not $uri.IsLoopback) {
        Add-QaResult 'live-llama-readiness' 'fail' 'The optional QA probe accepts only an absolute loopback HTTP(S) URL.'
        throw 'Use a loopback llama.cpp address such as http://127.0.0.1:8080/v1.'
    }

    $builder = [UriBuilder]::new($uri)
    $builder.Query = ''
    $builder.Fragment = ''
    $builder.UserName = ''
    $builder.Password = ''
    $path = $builder.Path.TrimEnd('/')
    if ($path.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
        $path = $path.Substring(0, $path.Length - 3).TrimEnd('/')
    }
    $builder.Path = "$path/health"

    try {
        $response = Invoke-RestMethod -Method Get -Uri $builder.Uri -TimeoutSec 10
        $status = if ($response.status) { [string]$response.status } else { 'HTTP success' }
        Add-QaResult 'live-llama-readiness' 'pass' "Read-only /health probe returned $status."
    }
    catch {
        Add-QaResult 'live-llama-readiness' 'fail' ("Read-only /health probe failed: " + $_.Exception.Message)
        throw
    }
}

try {
    if (-not $SkipBuild) {
        Invoke-DotNetGate 'solution-build' @('build', $solutionPath, '--no-restore', '-c', 'Release')
    }
    else {
        Add-QaResult 'solution-build' 'unavailable' 'Skipped by request; focused harnesses use the existing Release build output.'
    }

    Invoke-DotNetGate 'core-llama-contracts' @('run', '--project', $coreTests, '--no-build', '-c', 'Release') 'llama.cpp'
    Invoke-DotNetGate 'wpf-llama-runtime' @('run', '--project', $wpfTests, '--no-build', '-c', 'Release') 'llama.cpp'
    Invoke-DotNetGate 'arena-evaluation' @('run', '--project', $wpfTests, '--no-build', '-c', 'Release') 'evaluation'
    Assert-NoBundledLlamaRuntime
    Invoke-OptionalLlamaProbe $LlamaBaseUrl
}
finally {
    $results | Format-Table -AutoSize -Wrap
}

if ($results.Where({ $_.Status -eq 'fail' }).Count -gt 0) {
    exit 1
}
