Set-StrictMode -Version Latest

function Assert-AIArenaReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw "Invalid release version '$Version'. Expected a semantic version such as 0.4.89-beta."
    }
}

function Assert-AIArenaRuntimeIdentifier {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    if ($Runtime -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') {
        throw "Invalid runtime identifier '$Runtime'."
    }
}

function Assert-AIArenaSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($Value -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "$Label must be a 64-character SHA-256 digest."
    }
}

function Assert-AIArenaHttpsUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $parsed = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$parsed) `
        -or $parsed.Scheme -ne [Uri]::UriSchemeHttps `
        -or -not [string]::IsNullOrEmpty($parsed.UserInfo)) {
        throw "$Label must be an absolute HTTPS URI without user information."
    }
}

function Assert-AIArenaPathWithinDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Directory,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $fullDirectory + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escapes its expected directory: $fullPath"
    }
}

function Invoke-AIArenaNativeCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Assert-AIArenaTrustedExecutable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [string]$SignerSubjectPattern = ""
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Label was not found: $fullPath"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid `
        -or $null -eq $signature.SignerCertificate) {
        throw "$Label does not have a valid Authenticode signature: $fullPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($SignerSubjectPattern) `
        -and $signature.SignerCertificate.Subject -notmatch $SignerSubjectPattern) {
        throw "$Label was not signed by the expected publisher: $($signature.SignerCertificate.Subject)"
    }
}

function Get-AIArenaSignTool {
    [CmdletBinding()]
    param(
        [string]$ExplicitPath = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $fullPath = [IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "SignTool was not found at the supplied path: $fullPath"
        }
        Assert-AIArenaTrustedExecutable -Path $fullPath -Label 'SignTool' -SignerSubjectPattern '(^|,\s*)O=Microsoft Corporation(,|$)'
        return $fullPath
    }

    $command = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        Assert-AIArenaTrustedExecutable -Path $command.Source -Label 'SignTool' -SignerSubjectPattern '(^|,\s*)O=Microsoft Corporation(,|$)'
        return $command.Source
    }

    $kitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }

    $candidates = foreach ($kitRoot in $kitRoots) {
        Get-ChildItem -LiteralPath $kitRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' }
    }

    $selected = $candidates |
        Sort-Object { [version]($_.Directory.Parent.Name -replace '[^0-9.]', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not [string]::IsNullOrWhiteSpace($selected)) {
        Assert-AIArenaTrustedExecutable -Path $selected -Label 'SignTool' -SignerSubjectPattern '(^|,\s*)O=Microsoft Corporation(,|$)'
    }
    return $selected
}

function Get-AIArenaCodeSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint
    )

    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[A-F0-9]{40,128}$') {
        throw "Signing certificate thumbprint must contain 40 to 128 hexadecimal characters."
    }

    foreach ($storePath in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $certificate = Get-ChildItem -LiteralPath $storePath -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Thumbprint -eq $normalized `
                    -and $_.HasPrivateKey `
                    -and @($_.EnhancedKeyUsageList | Where-Object { [string]$_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
            } |
            Select-Object -First 1
        if ($null -ne $certificate) {
            return $certificate
        }
    }

    return $null
}

function Resolve-AIArenaSigningConfiguration {
    [CmdletBinding()]
    param(
        [ValidateSet('Optional', 'Required', 'Disabled')]
        [string]$Policy = 'Optional',
        [string]$CertificateThumbprint = "",
        [string]$SignToolPath = "",
        [string]$TimestampUrl = 'http://timestamp.digicert.com'
    )

    if ($Policy -eq 'Disabled') {
        return [pscustomobject]@{
            Policy = $Policy
            Enabled = $false
            Certificate = $null
            CertificateThumbprint = ""
            CertificateStoreLocation = ""
            SignToolPath = ""
            TimestampUrl = $TimestampUrl
            Reason = 'Signing was explicitly disabled.'
        }
    }

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        if ($Policy -eq 'Required') {
            throw 'Authenticode signing is required, but no certificate thumbprint was supplied. Set -SigningCertificateThumbprint or AIARENA_SIGNING_CERT_THUMBPRINT.'
        }

        return [pscustomobject]@{
            Policy = $Policy
            Enabled = $false
            Certificate = $null
            CertificateThumbprint = ""
            CertificateStoreLocation = ""
            SignToolPath = ""
            TimestampUrl = $TimestampUrl
            Reason = 'No signing certificate thumbprint was supplied.'
        }
    }

    $certificate = Get-AIArenaCodeSigningCertificate -Thumbprint $CertificateThumbprint
    if ($null -eq $certificate) {
        throw 'The requested Authenticode certificate was not found with an accessible private key in CurrentUser\My or LocalMachine\My.'
    }
    if ($certificate.NotAfter -le [DateTime]::Now) {
        throw "The requested Authenticode certificate expired on $($certificate.NotAfter.ToString('u'))."
    }
    if ($certificate.NotBefore -gt [DateTime]::Now) {
        throw "The requested Authenticode certificate is not valid until $($certificate.NotBefore.ToString('u'))."
    }

    $resolvedSignTool = Get-AIArenaSignTool -ExplicitPath $SignToolPath
    if ([string]::IsNullOrWhiteSpace($resolvedSignTool)) {
        throw 'Authenticode signing was requested, but signtool.exe was not found. Install the Windows SDK Signing Tools or pass -SignTool.'
    }

    $timestamp = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestamp) `
        -or $timestamp.Scheme -notin @([Uri]::UriSchemeHttp, [Uri]::UriSchemeHttps) `
        -or -not [string]::IsNullOrEmpty($timestamp.UserInfo)) {
        throw 'TimestampUrl must be an absolute HTTP or HTTPS URI without user information.'
    }

    return [pscustomobject]@{
        Policy = $Policy
        Enabled = $true
        Certificate = $certificate
        CertificateThumbprint = $certificate.Thumbprint
        CertificateStoreLocation = if ($certificate.PSParentPath -match 'Certificate::LocalMachine\\') { 'LocalMachine' } else { 'CurrentUser' }
        SignToolPath = $resolvedSignTool
        TimestampUrl = $timestamp.AbsoluteUri
        Reason = 'Signing prerequisites are available.'
    }
}

function Invoke-AIArenaAuthenticodeSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Configuration,
        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    $records = @()
    foreach ($item in $Path) {
        $fullPath = [IO.Path]::GetFullPath($item)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Signing target does not exist: $fullPath"
        }

        if ($Configuration.Enabled) {
            $arguments = @('sign')
            if ($Configuration.CertificateStoreLocation -eq 'LocalMachine') {
                $arguments += '/sm'
            }
            $arguments += @(
                '/sha1', $Configuration.CertificateThumbprint,
                '/fd', 'SHA256',
                '/tr', $Configuration.TimestampUrl,
                '/td', 'SHA256',
                '/v',
                $fullPath
            )
            Invoke-AIArenaNativeCommand -FilePath $Configuration.SignToolPath -ArgumentList $arguments -Label "Authenticode signing of $fullPath"
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
        if ($Configuration.Enabled -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode verification failed for $fullPath with status $($signature.Status)."
        }

        $records += [pscustomobject]@{
            path = $fullPath
            status = $signature.Status.ToString()
            signerThumbprint = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
            timeStamperThumbprint = if ($null -ne $signature.TimeStamperCertificate) { $signature.TimeStamperCertificate.Thumbprint } else { $null }
        }
    }

    return $records
}

function Get-AIArenaSha256Entries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [string[]]$ExcludeRelativePath = @()
    )

    $base = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $base -PathType Container)) {
        throw "Checksum base directory does not exist: $base"
    }

    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $ExcludeRelativePath) {
        [void]$excluded.Add(($relative -replace '/', '\').TrimStart('\'))
    }

    $entries = foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse) {
        $relative = $file.FullName.Substring($base.Length).TrimStart('\', '/')
        if ($excluded.Contains($relative)) {
            continue
        }
        if ($relative -match '[\r\n]') {
            throw "Cannot write a checksum entry for a path containing a line break: $relative"
        }

        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        [pscustomobject]@{
            RelativePath = $relative
            Hash = $hash.Hash.ToUpperInvariant()
            Length = $file.Length
        }
    }

    return @($entries | Sort-Object RelativePath)
}

function New-AIArenaSha256Manifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath,
        [string[]]$ExcludeRelativePath = @()
    )

    $base = [IO.Path]::GetFullPath($BaseDirectory)
    $output = [IO.Path]::GetFullPath($OutputPath)
    Assert-AIArenaPathWithinDirectory -Path $output -Directory $base -Label 'Checksum manifest'
    $relativeOutput = $output.Substring($base.TrimEnd('\', '/').Length).TrimStart('\', '/')
    $exclusions = @($ExcludeRelativePath) + $relativeOutput
    $lines = Get-AIArenaSha256Entries -BaseDirectory $base -ExcludeRelativePath $exclusions |
        ForEach-Object { "$($_.Hash)  $($_.RelativePath)" }

    $temporary = "$output.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        Set-Content -LiteralPath $temporary -Value $lines -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $output -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Test-AIArenaSha256Manifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [string[]]$ExcludeRelativePath = @(),
        [switch]$AllowPreamble
    )

    $base = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/')
    $manifest = [IO.Path]::GetFullPath($ManifestPath)
    Assert-AIArenaPathWithinDirectory -Path $manifest -Directory $base -Label 'Checksum manifest'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Checksum manifest does not exist: $manifest"
    }

    $relativeManifest = $manifest.Substring($base.Length).TrimStart('\', '/')
    $expected = Get-AIArenaSha256Entries -BaseDirectory $base -ExcludeRelativePath (@($ExcludeRelativePath) + $relativeManifest)
    $recorded = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -match '^([A-Fa-f0-9]{64})  (.+)$') {
            $relative = ($Matches[2] -replace '/', '\').TrimStart('\')
            if ($recorded.ContainsKey($relative)) {
                throw "Checksum manifest contains a duplicate path: $relative"
            }
            $recorded.Add($relative, $Matches[1].ToUpperInvariant())
        }
        elseif (-not $AllowPreamble -and -not [string]::IsNullOrWhiteSpace($line)) {
            throw "Checksum manifest contains an invalid line: $line"
        }
    }

    if ($recorded.Count -ne $expected.Count) {
        throw "Checksum manifest entry count mismatch. Expected $($expected.Count), found $($recorded.Count)."
    }

    foreach ($entry in $expected) {
        if (-not $recorded.TryGetValue($entry.RelativePath, [ref]$null)) {
            throw "Checksum manifest is missing: $($entry.RelativePath)"
        }
        if ($recorded[$entry.RelativePath] -ne $entry.Hash) {
            throw "Checksum mismatch for $($entry.RelativePath)."
        }
    }

    return $true
}
