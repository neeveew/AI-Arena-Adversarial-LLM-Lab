[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Update,
    [string]$BaselineRef = "",
    [string]$RepositoryRoot = ""
)

# Inventories hard-coded layout and type values in XAML. The committed baseline
# is a reduction-only ratchet keyed by relative file, property, normalized value,
# and count. That shape prevents a deletion in one file from financing a new
# literal elsewhere.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Check -and $Update) {
    throw "Choose either -Check or -Update, not both."
}
if (-not [string]::IsNullOrWhiteSpace($BaselineRef) -and -not $Check) {
    throw "-BaselineRef is valid only with -Check."
}
if (-not [string]::IsNullOrWhiteSpace($BaselineRef) -and $BaselineRef -notmatch '^[0-9a-fA-F]{40}$') {
    throw "-BaselineRef must be a full 40-character commit SHA."
}

$Root = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
} else {
    (Resolve-Path -LiteralPath $RepositoryRoot).Path
}
$sourceRoot = Join-Path $Root "src"
$baselineRelativePath = "docs/xaml-hardcoded-baseline.json"
$baselinePath = Join-Path $Root $baselineRelativePath
$trackedProperties = @('Margin', 'Padding', 'CornerRadius', 'FontSize')
$excludedPathPattern = '[\\/](bin|obj|dist|map|\.git)[\\/]'
$inventorySeparator = [char]31

function Test-AIArenaMarkupExtension {
    param([string]$Value)

    return $Value.TrimStart().StartsWith('{')
}

function Normalize-AIArenaXamlLiteral {
    param([string]$Value)

    $normalized = $Value.Trim()
    return [regex]::Replace($normalized, '\s*,\s*', ',')
}

function ConvertTo-AIArenaCount {
    param(
        $Value,
        [string]$Label,
        [switch]$Positive
    )

    $integralTypes = @(
        'System.Byte',
        'System.SByte',
        'System.Int16',
        'System.UInt16',
        'System.Int32',
        'System.UInt32',
        'System.Int64',
        'System.UInt64'
    )
    $typeName = if ($null -eq $Value) { '' } else { $Value.GetType().FullName }
    $valid = $integralTypes -contains $typeName
    $number = 0L
    if ($valid) {
        try {
            $number = [Convert]::ToInt64($Value, [Globalization.CultureInfo]::InvariantCulture)
        } catch {
            $valid = $false
        }
    }
    if (-not $valid -or
        $number -lt 0 -or
        $number -gt [int]::MaxValue -or
        ($Positive -and $number -eq 0)) {
        $expectation = if ($Positive) { 'a positive integer' } else { 'a non-negative integer' }
        $display = if ($null -eq $Value) { 'null' } else { [string]$Value }
        throw "$Label must be $expectation JSON number; found '$display'."
    }
    return [int]$number
}

function New-AIArenaInventoryKey {
    param([string]$Path, [string]$Property, [string]$Value)

    return [string]::Join([string]$inventorySeparator, @($Path, $Property, $Value))
}

function Split-AIArenaInventoryKey {
    param([string]$Key)

    return @($Key -split [regex]::Escape([string]$inventorySeparator), 3)
}

function Add-AIArenaInventoryEntry {
    param(
        [System.Collections.Generic.Dictionary[string, int]]$Counts,
        [System.Collections.IDictionary]$Properties,
        [string]$Path,
        [string]$Property,
        [string]$Value
    )

    if (Test-AIArenaMarkupExtension $Value) {
        return
    }

    $normalized = Normalize-AIArenaXamlLiteral $Value
    $key = New-AIArenaInventoryKey -Path $Path -Property $Property -Value $normalized
    if ($Counts.ContainsKey($key)) {
        $Counts[$key]++
    } else {
        $Counts.Add($key, 1)
    }
    $Properties[$Property] = [int]$Properties[$Property] + 1
}

function Get-AIArenaXamlLiteralInventory {
    param([string]$SourceRoot)

    $counts = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $properties = [ordered]@{}
    foreach ($property in $trackedProperties) {
        $properties[$property] = 0
    }

    $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -Filter "*.xaml" -File |
        Where-Object { $_.FullName -notmatch $excludedPathPattern } |
        Sort-Object FullName)

    foreach ($file in $files) {
        if (-not $file.FullName.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "XAML file escaped the repository root: $($file.FullName)"
        }
        $relativePath = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')

        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.IgnoreComments = $true

        $document = New-Object System.Xml.XmlDocument
        $document.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($file.FullName, $settings)
        try {
            $document.Load($reader)
        } catch {
            throw "Could not inventory $relativePath as XML: $($_.Exception.Message)"
        } finally {
            $reader.Dispose()
        }

        foreach ($node in $document.SelectNodes('//*')) {
            foreach ($attribute in $node.Attributes) {
                $property = @($attribute.LocalName -split '\.')[-1]
                if ($trackedProperties -contains $property) {
                    Add-AIArenaInventoryEntry `
                        -Counts $counts `
                        -Properties $properties `
                        -Path $relativePath `
                        -Property $property `
                        -Value $attribute.Value
                }
            }

            if ($node.LocalName -ne 'Setter') {
                continue
            }

            $propertyAttribute = $node.Attributes.GetNamedItem('Property')
            $valueAttribute = $node.Attributes.GetNamedItem('Value')
            $setterProperty = if ($null -ne $propertyAttribute) {
                @($propertyAttribute.Value -split '\.')[-1]
            } else {
                ''
            }
            if ($null -ne $propertyAttribute -and
                $null -ne $valueAttribute -and
                $trackedProperties -contains $setterProperty) {
                Add-AIArenaInventoryEntry `
                    -Counts $counts `
                    -Properties $properties `
                    -Path $relativePath `
                    -Property $setterProperty `
                    -Value $valueAttribute.Value
            }
        }
    }

    $total = 0
    foreach ($property in $trackedProperties) {
        $total += [int]$properties[$property]
    }

    return [pscustomobject]@{
        Counts = $counts
        Properties = $properties
        Total = $total
    }
}

function ConvertTo-AIArenaBaselinePayload {
    param($Inventory)

    $paths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($key in $Inventory.Counts.Keys) {
        $parts = Split-AIArenaInventoryKey $key
        [void]$paths.Add($parts[0])
    }

    $files = [ordered]@{}
    foreach ($path in $paths) {
        $propertyPayload = [ordered]@{}
        foreach ($property in $trackedProperties) {
            $values = [System.Collections.Generic.SortedDictionary[string, int]]::new([StringComparer]::Ordinal)
            foreach ($key in $Inventory.Counts.Keys) {
                $parts = Split-AIArenaInventoryKey $key
                if ($parts[0] -eq $path -and $parts[1] -eq $property) {
                    $values.Add($parts[2], [int]$Inventory.Counts[$key])
                }
            }
            if ($values.Count -eq 0) {
                continue
            }

            $valuePayload = [ordered]@{}
            foreach ($pair in $values.GetEnumerator()) {
                $valuePayload[$pair.Key] = $pair.Value
            }
            $propertyPayload[$property] = $valuePayload
        }
        if ($propertyPayload.Count -gt 0) {
            $files[$path] = $propertyPayload
        }
    }

    return [ordered]@{
        schemaVersion = 2
        note = 'Reduction-only inventory for hard-coded XAML layout and type values. Update explicitly with scripts/xaml-hardcoded-values.ps1 -Update.'
        generatedBy = 'scripts/xaml-hardcoded-values.ps1'
        trackedProperties = $trackedProperties
        properties = $Inventory.Properties
        total = $Inventory.Total
        files = $files
    }
}

function ConvertFrom-AIArenaBaselineSnapshot {
    param($Snapshot, [string]$Label)

    $schemaVersionProperty = $Snapshot.PSObject.Properties['schemaVersion']
    if ($null -eq $schemaVersionProperty) {
        if ($null -eq $Snapshot.PSObject.Properties['properties'] -or
            $null -eq $Snapshot.PSObject.Properties['total']) {
            throw "$Label is neither a legacy aggregate baseline nor schema version 2."
        }

        $properties = [ordered]@{}
        $total = 0
        foreach ($property in $trackedProperties) {
            $valueProperty = $Snapshot.properties.PSObject.Properties[$property]
            if ($null -eq $valueProperty) {
                throw "$Label is missing the $property aggregate."
            }
            $count = ConvertTo-AIArenaCount `
                -Value $valueProperty.Value `
                -Label "$Label $property aggregate"
            $properties[$property] = $count
            $total += $count
        }
        $declaredTotal = ConvertTo-AIArenaCount -Value $Snapshot.total -Label "$Label total"
        if ($total -ne $declaredTotal) {
            throw "$Label total does not match its property aggregates."
        }

        return [pscustomobject]@{
            Mode = 'Aggregate'
            Counts = $null
            Properties = $properties
            Total = $total
        }
    }

    $schemaVersion = ConvertTo-AIArenaCount `
        -Value $schemaVersionProperty.Value `
        -Label "$Label schemaVersion" `
        -Positive
    if ($schemaVersion -ne 2) {
        throw "$Label uses unsupported schema version $($schemaVersionProperty.Value)."
    }
    if ($null -eq $Snapshot.PSObject.Properties['files'] -or
        $null -eq $Snapshot.PSObject.Properties['properties'] -or
        $null -eq $Snapshot.PSObject.Properties['total'] -or
        $null -eq $Snapshot.PSObject.Properties['trackedProperties']) {
        throw "$Label schema version 2 is incomplete."
    }
    $declaredTrackedProperties = @($Snapshot.trackedProperties)
    if ($declaredTrackedProperties.Count -ne $trackedProperties.Count) {
        throw "$Label trackedProperties does not match the gate contract."
    }
    for ($index = 0; $index -lt $trackedProperties.Count; $index++) {
        if ([string]$declaredTrackedProperties[$index] -cne $trackedProperties[$index]) {
            throw "$Label trackedProperties does not match the gate contract."
        }
    }

    $counts = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $properties = [ordered]@{}
    foreach ($property in $trackedProperties) {
        $properties[$property] = 0
    }

    foreach ($fileProperty in $Snapshot.files.PSObject.Properties) {
        $path = $fileProperty.Name.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/') -or
            $path -match '^[A-Za-z]:' -or
            @($path.Split('/')) -contains '..') {
            throw "$Label contains a non-relative path: $path"
        }

        foreach ($propertyNode in $fileProperty.Value.PSObject.Properties) {
            $property = $propertyNode.Name
            if ($trackedProperties -notcontains $property) {
                throw "$Label tracks unsupported property $property in $path."
            }
            foreach ($valueNode in $propertyNode.Value.PSObject.Properties) {
                $count = ConvertTo-AIArenaCount `
                    -Value $valueNode.Value `
                    -Label "$Label count for $path $property '$($valueNode.Name)'" `
                    -Positive
                $key = New-AIArenaInventoryKey -Path $path -Property $property -Value $valueNode.Name
                if ($counts.ContainsKey($key)) {
                    throw "$Label contains a duplicate inventory entry for $path $property '$($valueNode.Name)'."
                }
                $counts.Add($key, $count)
                $properties[$property] = [int]$properties[$property] + $count
            }
        }
    }

    $total = 0
    foreach ($property in $trackedProperties) {
        $declaredProperty = $Snapshot.properties.PSObject.Properties[$property]
        if ($null -eq $declaredProperty) {
            throw "$Label is missing the $property aggregate."
        }
        $declaredCount = ConvertTo-AIArenaCount `
            -Value $declaredProperty.Value `
            -Label "$Label $property aggregate"
        if ($declaredCount -ne [int]$properties[$property]) {
            throw "$Label $property aggregate does not match its detailed inventory."
        }
        $total += [int]$properties[$property]
    }
    $declaredTotal = ConvertTo-AIArenaCount -Value $Snapshot.total -Label "$Label total"
    if ($total -ne $declaredTotal) {
        throw "$Label total does not match its detailed inventory."
    }

    return [pscustomobject]@{
        Mode = 'Detailed'
        Counts = $counts
        Properties = $properties
        Total = $total
    }
}

function Read-AIArenaBaselineFile {
    param([string]$Path, [string]$Label)

    try {
        $snapshot = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "Could not parse ${Label}: $($_.Exception.Message)"
    }
    return ConvertFrom-AIArenaBaselineSnapshot -Snapshot $snapshot -Label $Label
}

function Read-AIArenaBaselineAtGitRef {
    param([string]$Ref)

    $gitPath = "$Ref`:$baselineRelativePath"
    $content = @(& git -C $Root show $gitPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read $baselineRelativePath at ${Ref}: $($content -join [Environment]::NewLine)"
    }
    try {
        $json = ($content -join [Environment]::NewLine).TrimStart([char]0xFEFF)
        $snapshot = $json | ConvertFrom-Json
    } catch {
        throw "Could not parse $baselineRelativePath at ${Ref}: $($_.Exception.Message)"
    }
    return ConvertFrom-AIArenaBaselineSnapshot -Snapshot $snapshot -Label "$baselineRelativePath at $Ref"
}

function Compare-AIArenaInventories {
    param($Candidate, $Reference)

    $increases = New-Object System.Collections.Generic.List[string]
    $reductions = New-Object System.Collections.Generic.List[string]

    if ($Reference.Mode -eq 'Aggregate') {
        foreach ($property in $trackedProperties) {
            $actual = [int]$Candidate.Properties[$property]
            $allowed = [int]$Reference.Properties[$property]
            if ($actual -gt $allowed) {
                $increases.Add("$property : $actual, reference $allowed (+$($actual - $allowed))")
            } elseif ($actual -lt $allowed) {
                $reductions.Add("$property : $actual, reference $allowed (-$($allowed - $actual))")
            }
        }
        return [pscustomobject]@{ Increases = $increases; Reductions = $reductions }
    }

    $keys = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($key in $Candidate.Counts.Keys) {
        [void]$keys.Add($key)
    }
    foreach ($key in $Reference.Counts.Keys) {
        [void]$keys.Add($key)
    }

    foreach ($key in $keys) {
        $actual = if ($Candidate.Counts.ContainsKey($key)) { [int]$Candidate.Counts[$key] } else { 0 }
        $allowed = if ($Reference.Counts.ContainsKey($key)) { [int]$Reference.Counts[$key] } else { 0 }
        if ($actual -eq $allowed) {
            continue
        }

        $parts = Split-AIArenaInventoryKey $key
        $description = "$($parts[0]) :: $($parts[1])='$($parts[2])'"
        if ($actual -gt $allowed) {
            $increases.Add("$description : $actual, reference $allowed (+$($actual - $allowed))")
        } else {
            $reductions.Add("$description : $actual, reference $allowed (-$($allowed - $actual))")
        }
    }

    return [pscustomobject]@{ Increases = $increases; Reductions = $reductions }
}

function Format-AIArenaChanges {
    param([System.Collections.Generic.List[string]]$Changes)

    $limit = 25
    $visible = @($Changes | Select-Object -First $limit)
    $text = $visible -join [Environment]::NewLine
    if ($Changes.Count -gt $limit) {
        $text += [Environment]::NewLine + "... and $($Changes.Count - $limit) more."
    }
    return $text
}

function ConvertTo-AIArenaJsonString {
    param([string]$Value)

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($character in $Value.ToCharArray()) {
        $code = [int]$character
        switch ($code) {
            8 { [void]$builder.Append('\b'); continue }
            9 { [void]$builder.Append('\t'); continue }
            10 { [void]$builder.Append('\n'); continue }
            12 { [void]$builder.Append('\f'); continue }
            13 { [void]$builder.Append('\r'); continue }
            34 { [void]$builder.Append('\"'); continue }
            92 { [void]$builder.Append('\\'); continue }
        }
        if ($code -lt 32 -or $code -gt 126) {
            [void]$builder.Append(('\u{0:x4}' -f $code))
        } else {
            [void]$builder.Append($character)
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function ConvertTo-AIArenaBaselineJson {
    param($Payload)

    $builder = New-Object System.Text.StringBuilder
    function Add-JsonLine {
        param([int]$Indent, [string]$Text)

        [void]$builder.Append((' ' * $Indent))
        [void]$builder.Append($Text)
        [void]$builder.Append("`n")
    }

    Add-JsonLine 0 '{'
    Add-JsonLine 2 '"schemaVersion": 2,'
    Add-JsonLine 2 ('"note": ' + (ConvertTo-AIArenaJsonString $Payload.note) + ',')
    Add-JsonLine 2 ('"generatedBy": ' + (ConvertTo-AIArenaJsonString $Payload.generatedBy) + ',')
    $trackedJson = @($Payload.trackedProperties | ForEach-Object { ConvertTo-AIArenaJsonString ([string]$_) }) -join ', '
    Add-JsonLine 2 ('"trackedProperties": [' + $trackedJson + '],')
    Add-JsonLine 2 '"properties": {'
    for ($propertyIndex = 0; $propertyIndex -lt $trackedProperties.Count; $propertyIndex++) {
        $property = $trackedProperties[$propertyIndex]
        $suffix = if ($propertyIndex -lt $trackedProperties.Count - 1) { ',' } else { '' }
        Add-JsonLine 4 ((ConvertTo-AIArenaJsonString $property) + ': ' + [int]$Payload.properties[$property] + $suffix)
    }
    Add-JsonLine 2 '},'
    Add-JsonLine 2 ('"total": ' + [int]$Payload.total + ',')
    Add-JsonLine 2 '"files": {'

    $fileEntries = @($Payload.files.GetEnumerator())
    for ($fileIndex = 0; $fileIndex -lt $fileEntries.Count; $fileIndex++) {
        $fileEntry = $fileEntries[$fileIndex]
        Add-JsonLine 4 ((ConvertTo-AIArenaJsonString ([string]$fileEntry.Key)) + ': {')
        $propertyEntries = @($fileEntry.Value.GetEnumerator())
        for ($entryIndex = 0; $entryIndex -lt $propertyEntries.Count; $entryIndex++) {
            $propertyEntry = $propertyEntries[$entryIndex]
            Add-JsonLine 6 ((ConvertTo-AIArenaJsonString ([string]$propertyEntry.Key)) + ': {')
            $valueEntries = @($propertyEntry.Value.GetEnumerator())
            for ($valueIndex = 0; $valueIndex -lt $valueEntries.Count; $valueIndex++) {
                $valueEntry = $valueEntries[$valueIndex]
                $valueSuffix = if ($valueIndex -lt $valueEntries.Count - 1) { ',' } else { '' }
                Add-JsonLine 8 ((ConvertTo-AIArenaJsonString ([string]$valueEntry.Key)) + ': ' + [int]$valueEntry.Value + $valueSuffix)
            }
            $propertySuffix = if ($entryIndex -lt $propertyEntries.Count - 1) { ',' } else { '' }
            Add-JsonLine 6 ('}' + $propertySuffix)
        }
        $fileSuffix = if ($fileIndex -lt $fileEntries.Count - 1) { ',' } else { '' }
        Add-JsonLine 4 ('}' + $fileSuffix)
    }
    Add-JsonLine 2 '}'
    Add-JsonLine 0 '}'
    return $builder.ToString()
}

function Write-AIArenaBaselineAtomically {
    param([string]$Path, $Payload)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }

    $json = ConvertTo-AIArenaBaselineJson $Payload
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f (Split-Path -Leaf $Path), [Guid]::NewGuid().ToString('N'))
    $backupPath = $temporaryPath + '.bak'
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, $encoding)
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath)
        } else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Write-AIArenaInventorySummary {
    param($Inventory, [string]$Prefix)

    Write-Host $Prefix
    foreach ($property in $trackedProperties) {
        Write-Host ("  {0,-13} {1,4}" -f $property, $Inventory.Properties[$property])
    }
    Write-Host ("  {0,-13} {1,4}" -f 'total', $Inventory.Total)
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Source root not found: $sourceRoot"
}

$inventory = Get-AIArenaXamlLiteralInventory -SourceRoot $sourceRoot

if ($Update) {
    if (Test-Path -LiteralPath $baselinePath -PathType Leaf) {
        $existing = Read-AIArenaBaselineFile -Path $baselinePath -Label $baselineRelativePath
        $comparison = Compare-AIArenaInventories -Candidate $inventory -Reference $existing
        if ($comparison.Increases.Count -gt 0) {
            throw ("Refusing to increase or introduce XAML literals while updating the ratchet:" +
                [Environment]::NewLine + (Format-AIArenaChanges $comparison.Increases))
        }
    }

    $payload = ConvertTo-AIArenaBaselinePayload -Inventory $inventory
    Write-AIArenaBaselineAtomically -Path $baselinePath -Payload $payload
    Write-AIArenaInventorySummary -Inventory $inventory -Prefix "Updated XAML hard-coded baseline: $baselineRelativePath"
    return
}

if ($Check) {
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "XAML hard-coded baseline is missing: $baselineRelativePath. Create it explicitly with .\scripts\xaml-hardcoded-values.ps1 -Update."
    }

    $baseline = Read-AIArenaBaselineFile -Path $baselinePath -Label $baselineRelativePath
    if ($baseline.Mode -ne 'Detailed') {
        throw "XAML hard-coded baseline uses the legacy aggregate schema. Migrate it explicitly with .\scripts\xaml-hardcoded-values.ps1 -Update."
    }

    $comparison = Compare-AIArenaInventories -Candidate $inventory -Reference $baseline
    if ($comparison.Increases.Count -gt 0 -or $comparison.Reductions.Count -gt 0) {
        $sections = New-Object System.Collections.Generic.List[string]
        if ($comparison.Increases.Count -gt 0) {
            $sections.Add("Increased or new entries:`n$(Format-AIArenaChanges $comparison.Increases)")
        }
        if ($comparison.Reductions.Count -gt 0) {
            $sections.Add("Reduced or removed entries:`n$(Format-AIArenaChanges $comparison.Reductions)")
        }
        throw ("XAML hard-coded inventory does not match its committed baseline." +
            [Environment]::NewLine + ($sections -join ([Environment]::NewLine + [Environment]::NewLine)) +
            [Environment]::NewLine + "Review the change, then lock reductions with .\scripts\xaml-hardcoded-values.ps1 -Update.")
    }

    if (-not [string]::IsNullOrWhiteSpace($BaselineRef) -and $BaselineRef -notmatch '^0{40}$') {
        $reference = Read-AIArenaBaselineAtGitRef -Ref $BaselineRef
        $baseComparison = Compare-AIArenaInventories -Candidate $inventory -Reference $reference
        if ($baseComparison.Increases.Count -gt 0) {
            throw ("XAML hard-coded inventory grew relative to $BaselineRef`:" +
                [Environment]::NewLine + (Format-AIArenaChanges $baseComparison.Increases))
        }
        Write-Host "XAML hard-coded inventory did not grow relative to $BaselineRef."
    }

    Write-Host "XAML hard-coded inventory matches baseline: $($inventory.Total)"
    return
}

Write-AIArenaInventorySummary `
    -Inventory $inventory `
    -Prefix "XAML hard-coded inventory (report only; no files changed):"
