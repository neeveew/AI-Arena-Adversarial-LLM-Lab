param(
    [string]$OutputPath = "docs/DEPENDENCY_INDEX.md",
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $Root $OutputPath
}

function Get-RelativePath {
    param([string]$Path)

    $rootFullPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootFullPath = $rootFullPath + [System.IO.Path]::DirectorySeparatorChar
    $pathFullPath = [System.IO.Path]::GetFullPath($Path)
    $rootUri = [System.Uri]::new($rootFullPath)
    $pathUri = [System.Uri]::new($pathFullPath)
    if (-not $rootUri.IsBaseOf($pathUri)) {
        return $pathFullPath.Replace('\', '/')
    }

    $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
    return $relative.Replace('\', '/')
}

function Format-ListValue {
    param([string[]]$Values)

    [string[]]$items = @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($items.Length -eq 0) {
        return "-"
    }

    return ($items | Sort-Object -Unique) -join ", "
}

function Get-ModuleName {
    param([string]$RelativePath)

    if ($RelativePath -match '^src/AIArena\.Core/Modules/([^/]+)/') {
        return "Core/$($Matches[1])"
    }

    if ($RelativePath -match '^src/AIArena\.Wpf/Modules/([^/]+)/') {
        return "WPF/$($Matches[1])"
    }

    if ($RelativePath -match '^src/AIArena\.Wpf/Platform/Windows/([^/]+)/') {
        return "WPF/Platform.$($Matches[1])"
    }

    if ($RelativePath -match '^src/AIArena\.Wpf/UI/([^/]+)/') {
        return "WPF/UI.$($Matches[1])"
    }

    if ($RelativePath -match '^src/AIArena\.Wpf/Shell/') {
        return "WPF/Shell"
    }

    if ($RelativePath -match '^tests/AIArena\.Tests/') {
        return "Tests"
    }

    return "Unmapped"
}

function Get-ConstructorDependencies {
    param(
        [string]$ClassName,
        [string]$SourceText
    )

    $escapedClassName = [regex]::Escape($ClassName)
    $matches = [regex]::Matches($SourceText, "(?s)(?:public|internal|private)?\s+$escapedClassName\s*\((.*?)\)")
    $dependencies = [System.Collections.Generic.List[string]]::new()

    foreach ($match in $matches) {
        $parameters = $match.Groups[1].Value
        foreach ($parameter in $parameters -split ',') {
            $clean = ($parameter -replace '\s+', ' ').Trim()
            if ([string]::IsNullOrWhiteSpace($clean)) {
                continue
            }

            $clean = $clean -replace '^(?:this|in|ref|out|params)\s+', ''
            if ($clean -match '^([A-Za-z_][A-Za-z0-9_.<>?,\[\]]+)\s+[A-Za-z_][A-Za-z0-9_]*') {
                $typeName = $Matches[1].TrimEnd('?')
                if ($typeName -notin @('string', 'int', 'bool', 'double', 'decimal', 'float', 'long', 'short', 'byte', 'char', 'object')) {
                    $dependencies.Add($typeName)
                }
            }
        }
    }

    return $dependencies | Sort-Object -Unique
}

$excludedPathPattern = '[\\/](bin|obj|dist|map|\.git)[\\/]'
$projects = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter "*.csproj" -File |
    Where-Object { $_.FullName -notmatch $excludedPathPattern } |
    Sort-Object FullName)

$projectRows = @(foreach ($project in $projects) {
    [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw
    $projectDir = Split-Path -Parent $project.FullName
    $projectReferenceNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='ProjectReference']"))
    $packageReferenceNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']"))
    $resourceNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='Resource']"))
    $targetFrameworkNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='TargetFramework']"))
    $outputTypeNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='OutputType']"))

    $projectReferences = foreach ($reference in $projectReferenceNodes) {
        if ($reference.Include) {
            $absoluteReference = Join-Path $projectDir $reference.Include
            Get-RelativePath ([System.IO.Path]::GetFullPath($absoluteReference))
        }
    }

    $packageReferences = foreach ($reference in $packageReferenceNodes) {
        if ($reference.Include) {
            $version = if ($reference.Version) { $reference.Version } else { "floating" }
            "$($reference.Include)@$version"
        }
    }

    $resources = foreach ($resource in $resourceNodes) {
        if ($resource.Include) {
            $resource.Include
        }
    }

    [pscustomobject]@{
        Name = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        Path = Get-RelativePath $project.FullName
        TargetFramework = if ($targetFrameworkNodes.Count -gt 0) { $targetFrameworkNodes[0].InnerText } else { "-" }
        OutputType = if ($outputTypeNodes.Count -gt 0) { $outputTypeNodes[0].InnerText } else { "Library" }
        ProjectReferences = @($projectReferences)
        PackageReferences = @($packageReferences)
        Resources = @($resources)
    }
})

$sourceFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter "*.cs" -File |
    Where-Object { $_.FullName -notmatch $excludedPathPattern } |
    Sort-Object FullName)

$typeRows = [System.Collections.Generic.List[object]]::new()
$typePattern = '(?m)^\s*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|readonly)\s+)*(class|record(?:\s+class|\s+struct)?|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)'

foreach ($file in $sourceFiles) {
    $relativePath = Get-RelativePath $file.FullName
    $sourceText = Get-Content -LiteralPath $file.FullName -Raw
    $moduleName = Get-ModuleName $relativePath
    $typeMatches = [regex]::Matches($sourceText, $typePattern)

    foreach ($match in $typeMatches) {
        $kind = ($match.Groups[1].Value -replace '\s+', ' ').Trim()
        $typeName = $match.Groups[2].Value
        $isServiceLike = ($kind -in @("class", "interface", "record class")) -and
            $typeName -match '(Service|Store|Client|Mapper|Selector|Config|Provider|Settings|Palette)$'

        $dependencies = if ($kind -eq "class" -or $kind -eq "record class") {
            @(Get-ConstructorDependencies -ClassName $typeName -SourceText $sourceText)
        } else {
            @()
        }

        $typeRows.Add([pscustomobject]@{
            Module = $moduleName
            Kind = $kind
            Name = $typeName
            Path = $relativePath
            IsServiceLike = [bool]$isServiceLike
            ConstructorDependencies = $dependencies
        })
    }
}

$duplicateTypes = @($typeRows |
    Group-Object Name |
    Where-Object { $_.Count -gt 1 } |
    Sort-Object Name)

$duplicateServices = @($typeRows |
    Where-Object IsServiceLike |
    Group-Object Name |
    Where-Object { $_.Count -gt 1 } |
    Sort-Object Name)

$moduleRows = @($typeRows |
    Group-Object Module |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject]@{
            Module = $_.Name
            Types = $_.Count
            Services = @($_.Group | Where-Object IsServiceLike).Count
            Files = @($_.Group.Path | Sort-Object -Unique).Count
        }
    })

$serviceRows = @($typeRows |
    Where-Object IsServiceLike |
    Sort-Object Module, Name)

$lines = [System.Collections.Generic.List[string]]::new()
$generatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz")

$lines.Add("# Dependency Index")
$lines.Add("")
$lines.Add("Generated by ``scripts/dependency-index.ps1`` on $generatedAt.")
$lines.Add("")
$lines.Add("## Project Graph")
$lines.Add("")
$lines.Add("| Project | Target | Output | Project references | Packages | Resources |")
$lines.Add("| --- | --- | --- | --- | --- | --- |")
foreach ($project in $projectRows) {
    $lines.Add("| ``$($project.Path)`` | $($project.TargetFramework) | $($project.OutputType) | $(Format-ListValue $project.ProjectReferences) | $(Format-ListValue $project.PackageReferences) | $($project.Resources.Count) |")
}

$lines.Add("")
$lines.Add("## Module Inventory")
$lines.Add("")
$lines.Add("| Module | Types | Service-like types | Files |")
$lines.Add("| --- | ---: | ---: | ---: |")
foreach ($module in $moduleRows) {
    $lines.Add("| $($module.Module) | $($module.Types) | $($module.Services) | $($module.Files) |")
}

$lines.Add("")
$lines.Add("## Service Index")
$lines.Add("")
$lines.Add("| Module | Type | Constructor dependencies | Path |")
$lines.Add("| --- | --- | --- | --- |")
foreach ($service in $serviceRows) {
    $dependencyText = Format-ListValue $service.ConstructorDependencies
    $lines.Add("| $($service.Module) | ``$($service.Name)`` | $dependencyText | ``$($service.Path)`` |")
}

$lines.Add("")
$lines.Add("## Duplicate Type Names")
$lines.Add("")
if ($duplicateTypes.Count -eq 0) {
    $lines.Add("No duplicate type names detected.")
} else {
    $lines.Add("| Type | Locations |")
    $lines.Add("| --- | --- |")
    foreach ($duplicate in $duplicateTypes) {
        $locations = ($duplicate.Group | Sort-Object Path | ForEach-Object { "``$($_.Path)``" }) -join "<br>"
        $lines.Add("| ``$($duplicate.Name)`` | $locations |")
    }
}

$lines.Add("")
$lines.Add("## Duplicate Service-like Names")
$lines.Add("")
if ($duplicateServices.Count -eq 0) {
    $lines.Add("No duplicate service-like type names detected.")
} else {
    $lines.Add("| Type | Locations |")
    $lines.Add("| --- | --- |")
    foreach ($duplicate in $duplicateServices) {
        $locations = ($duplicate.Group | Sort-Object Path | ForEach-Object { "``$($_.Path)``" }) -join "<br>"
        $lines.Add("| ``$($duplicate.Name)`` | $locations |")
    }
}

$lines.Add("")
$lines.Add("## Notes")
$lines.Add("")
$lines.Add("- Service-like types are classes, interfaces, or record classes whose names end with ``Service``, ``Store``, ``Client``, ``Mapper``, ``Selector``, ``Config``, ``Provider``, ``Settings``, or ``Palette``.")
$lines.Add("- Constructor dependencies are a static source scan. Manually created dependencies inside method bodies are intentionally not treated as injected dependencies.")
$lines.Add("- Re-run with ``.\scripts\dependency-index.ps1 -Check`` before a release gate; it fails when the checked-in index is stale.")

$document = ($lines -join [Environment]::NewLine) + [Environment]::NewLine

if ($Check) {
    if (-not (Test-Path -LiteralPath $resolvedOutputPath)) {
        throw "Dependency index is missing: $resolvedOutputPath"
    }

    # Line endings have to be normalized as well as the timestamp. .gitattributes
    # checks this file out as LF while the document above is built with
    # Environment.NewLine, so on Windows a freshly cloned or freshly pulled tree
    # compared CRLF against LF and reported a stale index that was byte-identical
    # in content. It stayed hidden for as long as the generating checkout was also
    # the one being gated.
    $existing = Get-Content -LiteralPath $resolvedOutputPath -Raw
    $normalizedExisting = ((($existing -replace "`r`n", "`n") -replace '(?m)^Generated by .*$', 'Generated by <timestamp>.')).Trim()
    $normalizedNew = ((($document -replace "`r`n", "`n") -replace '(?m)^Generated by .*$', 'Generated by <timestamp>.')).Trim()

    if ($normalizedExisting -ne $normalizedNew) {
        throw "Dependency index is stale. Run .\scripts\dependency-index.ps1 and commit the updated $OutputPath."
    }

    Write-Host "Dependency index is current: $OutputPath"
    return
}

$outputDir = Split-Path -Parent $resolvedOutputPath
if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Set-Content -LiteralPath $resolvedOutputPath -Value $document -Encoding UTF8
Write-Host "Wrote dependency index: $resolvedOutputPath"
