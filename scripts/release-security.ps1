Set-StrictMode -Version Latest

$script:AIArenaCodeSigningEkuOid = '1.3.6.1.5.5.7.3.3'
$script:AIArenaTimestampingEkuOid = '1.3.6.1.5.5.7.3.8'

function Get-AIArenaOptionalPropertyValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$DefaultValue = $null
    )

    if ($null -eq $InputObject) {
        return $DefaultValue
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Assert-AIArenaSigningPolicyState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Optional', 'Required', 'Disabled')]
        [string]$Policy,
        [Parameter(Mandatory = $true)]
        [bool]$SigningEnabled
    )

    if ($Policy -eq 'Required' -and -not $SigningEnabled) {
        throw "Signing policy 'Required' cannot be recorded with signing disabled."
    }
    if ($Policy -eq 'Disabled' -and $SigningEnabled) {
        throw "Signing policy 'Disabled' cannot be recorded with signing enabled."
    }
}

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

function Normalize-AIArenaCertificateThumbprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint
    )

    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[A-F0-9]{40,128}$') {
        throw "Signing certificate thumbprint must contain 40 to 128 hexadecimal characters."
    }

    return $normalized
}

function Test-AIArenaCertificateEnhancedKeyUsage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)]
        [string]$Oid
    )

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -isnot [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            continue
        }

        foreach ($usage in $extension.EnhancedKeyUsages) {
            if ($usage.Value -eq $Oid) {
                return $true
            }
        }
    }

    return $false
}

function Test-AIArenaCertificateDigitalSignatureUsage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $keyUsageExtensions = @($Certificate.Extensions | Where-Object {
        $_ -is [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]
    })
    if ($keyUsageExtensions.Count -eq 0) {
        return $true
    }

    foreach ($extension in $keyUsageExtensions) {
        if (($extension.KeyUsages -band [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ne 0) {
            return $true
        }
    }

    return $false
}

function Find-AIArenaCodeSigningCertificates {
    [CmdletBinding()]
    param(
        [switch]$IncludeInvalid
    )

    $now = [DateTime]::Now
    foreach ($store in @(
        [pscustomobject]@{ Path = 'Cert:\CurrentUser\My'; Location = 'CurrentUser' },
        [pscustomobject]@{ Path = 'Cert:\LocalMachine\My'; Location = 'LocalMachine' }
    )) {
        foreach ($certificate in @(Get-ChildItem -LiteralPath $store.Path -ErrorAction SilentlyContinue)) {
            if ($certificate -isnot [System.Security.Cryptography.X509Certificates.X509Certificate2]) {
                continue
            }

            $hasCodeSigningEku = Test-AIArenaCertificateEnhancedKeyUsage `
                -Certificate $certificate `
                -Oid $script:AIArenaCodeSigningEkuOid
            $hasDigitalSignatureUsage = Test-AIArenaCertificateDigitalSignatureUsage -Certificate $certificate
            $timeValid = $certificate.NotBefore -le $now -and $certificate.NotAfter -gt $now
            $candidate = $certificate.HasPrivateKey `
                -and $hasCodeSigningEku `
                -and $hasDigitalSignatureUsage `
                -and $timeValid
            if ($IncludeInvalid.IsPresent -or $candidate) {
                [pscustomobject]@{
                    Certificate = $certificate
                    StoreLocation = $store.Location
                    Thumbprint = $certificate.Thumbprint
                    Subject = $certificate.Subject
                    NotBefore = $certificate.NotBefore
                    NotAfter = $certificate.NotAfter
                    HasPrivateKey = $certificate.HasPrivateKey
                    HasCodeSigningEku = $hasCodeSigningEku
                    HasDigitalSignatureUsage = $hasDigitalSignatureUsage
                    TimeValid = $timeValid
                    Candidate = $candidate
                }
            }
        }
    }
}

function Get-AIArenaCodeSigningCertificateRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint
    )

    $normalized = Normalize-AIArenaCertificateThumbprint -Thumbprint $Thumbprint
    return Find-AIArenaCodeSigningCertificates -IncludeInvalid |
        Where-Object { $_.Thumbprint -eq $normalized } |
        Select-Object -First 1
}

function Get-AIArenaCodeSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Thumbprint
    )

    $record = Get-AIArenaCodeSigningCertificateRecord -Thumbprint $Thumbprint
    if ($null -eq $record -or -not $record.Candidate) {
        return $null
    }

    return $record.Certificate
}

function Assert-AIArenaCertificateChain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
        if (-not $chain.Build($Certificate)) {
            $failures = @($chain.ChainStatus |
                ForEach-Object { "$($_.Status): $($_.StatusInformation.Trim())" } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $detail = if ($failures.Count -gt 0) { $failures -join '; ' } else { 'unknown chain error' }
            throw "The requested Authenticode certificate does not build to a trusted root: $detail"
        }
    }
    finally {
        $chain.Dispose()
    }
}

function Assert-AIArenaCertificatePrivateKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $challenge = [Text.Encoding]::UTF8.GetBytes("AI Arena Authenticode preflight $([Guid]::NewGuid().ToString('N'))")
    $rsaPrivate = $null
    $rsaPublic = $null
    try {
        $rsaPrivate = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($null -ne $rsaPrivate) {
            if ($rsaPrivate.KeySize -lt 2048) {
                throw "The requested Authenticode RSA key is only $($rsaPrivate.KeySize) bits; at least 2048 bits are required."
            }

            $rsaPublic = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
            $proof = $rsaPrivate.SignData(
                $challenge,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
            if ($null -eq $rsaPublic -or -not $rsaPublic.VerifyData(
                $challenge,
                $proof,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
                throw 'The requested Authenticode RSA private key failed its signing proof.'
            }

            return [pscustomobject]@{ Algorithm = 'RSA'; KeySize = $rsaPrivate.KeySize }
        }
    }
    catch {
        throw "The requested Authenticode private key is not usable for RSA signing: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $rsaPublic) {
            $rsaPublic.Dispose()
        }
        if ($null -ne $rsaPrivate) {
            $rsaPrivate.Dispose()
        }
    }

    $ecdsaPrivate = $null
    $ecdsaPublic = $null
    try {
        $ecdsaPrivate = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate)
        if ($null -ne $ecdsaPrivate) {
            if ($ecdsaPrivate.KeySize -lt 256) {
                throw "The requested Authenticode ECDSA key is only $($ecdsaPrivate.KeySize) bits; at least 256 bits are required."
            }

            $ecdsaPublic = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($Certificate)
            $proof = $ecdsaPrivate.SignData($challenge, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
            if ($null -eq $ecdsaPublic -or -not $ecdsaPublic.VerifyData(
                $challenge,
                $proof,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
                throw 'The requested Authenticode ECDSA private key failed its signing proof.'
            }

            return [pscustomobject]@{ Algorithm = 'ECDSA'; KeySize = $ecdsaPrivate.KeySize }
        }
    }
    catch {
        throw "The requested Authenticode private key is not usable for ECDSA signing: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $ecdsaPublic) {
            $ecdsaPublic.Dispose()
        }
        if ($null -ne $ecdsaPrivate) {
            $ecdsaPrivate.Dispose()
        }
    }

    throw 'The requested Authenticode certificate does not expose a supported RSA or ECDSA private key.'
}

function Get-AIArenaCertificatePublicKeyInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $rsa = $null
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
        if ($null -ne $rsa) {
            return [pscustomobject]@{ Algorithm = 'RSA'; KeySize = $rsa.KeySize }
        }
    }
    finally {
        if ($null -ne $rsa) {
            $rsa.Dispose()
        }
    }

    $ecdsa = $null
    try {
        $ecdsa = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($Certificate)
        if ($null -ne $ecdsa) {
            return [pscustomobject]@{ Algorithm = 'ECDSA'; KeySize = $ecdsa.KeySize }
        }
    }
    finally {
        if ($null -ne $ecdsa) {
            $ecdsa.Dispose()
        }
    }

    throw 'The Authenticode signer certificate does not expose a supported RSA or ECDSA public key.'
}

function Assert-AIArenaCodeSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$CertificateRecord
    )

    $certificate = $CertificateRecord.Certificate
    if ($null -eq $certificate) {
        throw 'The requested Authenticode certificate record has no certificate.'
    }
    if (-not $certificate.HasPrivateKey) {
        throw 'The requested Authenticode certificate does not have an accessible private key.'
    }
    if (-not (Test-AIArenaCertificateEnhancedKeyUsage -Certificate $certificate -Oid $script:AIArenaCodeSigningEkuOid)) {
        throw 'The requested Authenticode certificate is not valid for code signing.'
    }
    if (-not (Test-AIArenaCertificateDigitalSignatureUsage -Certificate $certificate)) {
        throw 'The requested Authenticode certificate key usage does not permit digital signatures.'
    }
    if ($certificate.NotAfter -le [DateTime]::Now) {
        throw "The requested Authenticode certificate expired on $($certificate.NotAfter.ToString('u'))."
    }
    if ($certificate.NotBefore -gt [DateTime]::Now) {
        throw "The requested Authenticode certificate is not valid until $($certificate.NotBefore.ToString('u'))."
    }

    Assert-AIArenaCertificateChain -Certificate $certificate
    $key = Assert-AIArenaCertificatePrivateKey -Certificate $certificate
    return [pscustomobject]@{
        Algorithm = $key.Algorithm
        KeySize = $key.KeySize
        ChainTrusted = $true
        PrivateKeyVerified = $true
    }
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
        $candidates = @(Find-AIArenaCodeSigningCertificates)
        $discovery = if ($candidates.Count -eq 0) {
            'No current code-signing certificate candidates were discovered.'
        }
        elseif ($candidates.Count -eq 1) {
            "One candidate was discovered in $($candidates[0].StoreLocation)\My: $($candidates[0].Thumbprint). Selection remains explicit."
        }
        else {
            "$($candidates.Count) candidates were discovered. Select one explicitly by thumbprint: $($candidates.Thumbprint -join ', ')."
        }
        if ($Policy -eq 'Required') {
            throw "Authenticode signing is required, but no certificate thumbprint was supplied. Set -SigningCertificateThumbprint or AIARENA_SIGNING_CERT_THUMBPRINT. $discovery"
        }

        return [pscustomobject]@{
            Policy = $Policy
            Enabled = $false
            Certificate = $null
            CertificateThumbprint = ""
            CertificateStoreLocation = ""
            SignToolPath = ""
            TimestampUrl = $TimestampUrl
            Reason = 'No signing certificate thumbprint was supplied. Run Find-AIArenaCodeSigningCertificates to inspect local candidates; selection remains explicit.'
        }
    }

    $certificateRecord = Get-AIArenaCodeSigningCertificateRecord -Thumbprint $CertificateThumbprint
    if ($null -eq $certificateRecord) {
        throw 'The requested Authenticode certificate was not found in CurrentUser\My or LocalMachine\My.'
    }
    $certificate = $certificateRecord.Certificate
    $certificatePreflight = Assert-AIArenaCodeSigningCertificate -CertificateRecord $certificateRecord

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
        CertificateStoreLocation = $certificateRecord.StoreLocation
        CertificateKeyAlgorithm = $certificatePreflight.Algorithm
        CertificateKeySize = $certificatePreflight.KeySize
        CertificateChainTrusted = $certificatePreflight.ChainTrusted
        PrivateKeyVerified = $certificatePreflight.PrivateKeyVerified
        SignToolPath = $resolvedSignTool
        TimestampUrl = $timestamp.AbsoluteUri
        Reason = 'Signing prerequisites are available.'
    }
}

function Test-AIArenaSigningPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$CertificateThumbprint,
        [string]$SignToolPath = "",
        [string]$TimestampUrl = 'http://timestamp.digicert.com'
    )

    $configuration = Resolve-AIArenaSigningConfiguration `
        -Policy Required `
        -CertificateThumbprint $CertificateThumbprint `
        -SignToolPath $SignToolPath `
        -TimestampUrl $TimestampUrl
    return [pscustomobject]@{
        Ready = $configuration.Enabled
        CertificateThumbprint = $configuration.CertificateThumbprint
        CertificateSubject = $configuration.Certificate.Subject
        CertificateStoreLocation = $configuration.CertificateStoreLocation
        CertificateNotAfter = $configuration.Certificate.NotAfter
        CertificateKeyAlgorithm = $configuration.CertificateKeyAlgorithm
        CertificateKeySize = $configuration.CertificateKeySize
        CertificateChainTrusted = $configuration.CertificateChainTrusted
        PrivateKeyVerified = $configuration.PrivateKeyVerified
        SignToolPath = $configuration.SignToolPath
        TimestampUrl = $configuration.TimestampUrl
    }
}

function Assert-AIArenaAuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Signature,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSignerThumbprint,
        [switch]$RequireTimestamp,
        [string]$Label = 'Authenticode artifact'
    )

    $expected = Normalize-AIArenaCertificateThumbprint -Thumbprint $ExpectedSignerThumbprint
    if ($Signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid `
        -or $null -eq $Signature.SignerCertificate) {
        throw "$Label does not have a valid Authenticode signature; status is $($Signature.Status)."
    }
    if ($Signature.SignerCertificate.Thumbprint -ne $expected) {
        throw "$Label was signed by $($Signature.SignerCertificate.Thumbprint), not the requested certificate $expected."
    }

    $timestampVerified = $false
    if ($RequireTimestamp.IsPresent) {
        if ($null -eq $Signature.TimeStamperCertificate) {
            throw "$Label does not have the required RFC 3161 timestamp countersignature."
        }
        if (-not (Test-AIArenaCertificateEnhancedKeyUsage `
            -Certificate $Signature.TimeStamperCertificate `
            -Oid $script:AIArenaTimestampingEkuOid)) {
            throw "$Label timestamp certificate is not valid for RFC 3161 timestamping."
        }
        $timestampVerified = $true
    }

    $publicKey = Get-AIArenaCertificatePublicKeyInfo -Certificate $Signature.SignerCertificate
    return [pscustomobject]@{
        Status = $Signature.Status.ToString()
        SignerThumbprint = $Signature.SignerCertificate.Thumbprint
        SignerKeyAlgorithm = $publicKey.Algorithm
        SignerKeySize = $publicKey.KeySize
        TimeStamperThumbprint = if ($null -ne $Signature.TimeStamperCertificate) {
            $Signature.TimeStamperCertificate.Thumbprint
        } else {
            $null
        }
        TimestampVerified = $timestampVerified
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
            Invoke-AIArenaNativeCommand `
                -FilePath $Configuration.SignToolPath `
                -ArgumentList @('verify', '/pa', '/all', '/v', '/tw', $fullPath) `
                -Label "SignTool verification of $fullPath"
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
        $verification = $null
        if ($Configuration.Enabled) {
            $verification = Assert-AIArenaAuthenticodeSignature `
                -Signature $signature `
                -ExpectedSignerThumbprint $Configuration.CertificateThumbprint `
                -RequireTimestamp `
                -Label $fullPath
        }

        $records += [pscustomobject]@{
            path = $fullPath
            status = $signature.Status.ToString()
            signerThumbprint = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
            timeStamperThumbprint = if ($null -ne $signature.TimeStamperCertificate) { $signature.TimeStamperCertificate.Thumbprint } else { $null }
            timestampVerified = if ($null -ne $verification) { [bool]$verification.TimestampVerified } else { $false }
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

function Write-AIArenaSha256ManifestEntries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath,
        [Parameter(Mandatory = $true)]
        [object[]]$Entries
    )

    $base = [IO.Path]::GetFullPath($BaseDirectory)
    $output = [IO.Path]::GetFullPath($OutputPath)
    Assert-AIArenaPathWithinDirectory -Path $output -Directory $base -Label 'Checksum manifest'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lines = foreach ($entry in @($Entries | Sort-Object RelativePath)) {
        $relative = ([string]$entry.RelativePath -replace '/', '\').TrimStart('\')
        Assert-AIArenaSha256 -Value ([string]$entry.Hash) -Label "Checksum for $relative"
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative -match '[\r\n]' -or -not $seen.Add($relative)) {
            throw "Cannot write an empty, duplicate, or line-breaking checksum path: $relative"
        }

        "$(([string]$entry.Hash).ToUpperInvariant())  $relative"
    }

    $temporary = "$output.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        # Windows PowerShell 5's Set-Content -Encoding UTF8 emits a BOM. Keep
        # checksum inventories byte-stable across Windows PowerShell and pwsh.
        $lineArray = @($lines)
        $manifestText = if ($lineArray.Count -gt 0) { ($lineArray -join "`n") + "`n" } else { '' }
        [IO.File]::WriteAllText($temporary, $manifestText, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $output -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
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
    $entries = @(Get-AIArenaSha256Entries -BaseDirectory $base -ExcludeRelativePath $exclusions)
    Write-AIArenaSha256ManifestEntries -BaseDirectory $base -OutputPath $output -Entries $entries
}

function Get-AIArenaReleaseVerificationSourceIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $commit = (& git -C $root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw 'Release verification could not resolve the current Git commit.'
    }

    $srcTree = (& git -C $root rev-parse 'HEAD:src' 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($srcTree)) {
        throw 'Release verification could not resolve the committed src tree.'
    }

    return [pscustomobject][ordered]@{
        Commit = $commit.Trim()
        SrcTree = $srcTree.Trim()
    }
}

function Get-AIArenaReleaseVerificationHarnesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration,
        [Parameter(Mandatory = $true)]
        [string[]]$ProjectPath
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    $harnesses = foreach ($project in $ProjectPath) {
        $fullProject = [IO.Path]::GetFullPath($project)
        if (-not $fullProject.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) `
            -or -not (Test-Path -LiteralPath $fullProject -PathType Leaf)) {
            throw "Release-verification project is missing or outside the repository: $project"
        }

        [xml]$projectXml = Get-Content -LiteralPath $fullProject -Raw
        $targetFrameworkNode = $projectXml.SelectSingleNode('//PropertyGroup/TargetFramework')
        $targetFramework = if ($null -ne $targetFrameworkNode) { ([string]$targetFrameworkNode.InnerText).Trim() } else { '' }
        if ([string]::IsNullOrWhiteSpace($targetFramework)) {
            throw "Release-verification project must declare one TargetFramework: $fullProject"
        }

        $assemblyNameNode = $projectXml.SelectSingleNode('//PropertyGroup/AssemblyName')
        $assemblyName = if ($null -ne $assemblyNameNode) { ([string]$assemblyNameNode.InnerText).Trim() } else { '' }
        if ([string]::IsNullOrWhiteSpace($assemblyName)) {
            $assemblyName = [IO.Path]::GetFileNameWithoutExtension($fullProject)
        }

        $assembly = Join-Path (Split-Path -Parent $fullProject) ("bin\{0}\{1}\{2}.dll" -f $Configuration, $targetFramework, $assemblyName)
        if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
            throw "Release-verification test assembly is missing: $assembly"
        }

        $relativeProject = $fullProject.Substring($rootPrefix.Length).Replace('\', '/')
        $file = Get-Item -LiteralPath $assembly
        [pscustomobject][ordered]@{
            project = $relativeProject
            targetFramework = $targetFramework
            assembly = $file.Name
            bytes = [long]$file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    }

    return @($harnesses | Sort-Object project)
}

function New-AIArenaReleaseVerificationKey {
    [CmdletBinding()]
    param()

    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        $generator.Dispose()
    }
}

function ConvertFrom-AIArenaReleaseVerificationKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReceiptKey
    )

    try {
        $bytes = [Convert]::FromBase64String($ReceiptKey)
    }
    catch {
        throw 'Release-verification receipt key must be valid Base64.'
    }
    if ($bytes.Length -ne 32) {
        throw 'Release-verification receipt key must contain exactly 256 bits.'
    }

    return $bytes
}

function Get-AIArenaHmacSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Key,
        [Parameter(Mandatory = $true)]
        [byte[]]$Value
    )

    $hmac = [Security.Cryptography.HMACSHA256]::new($Key)
    try {
        return $hmac.ComputeHash($Value)
    }
    finally {
        $hmac.Dispose()
    }
}

function Test-AIArenaByteArrayEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Left,
        [Parameter(Mandatory = $true)]
        [byte[]]$Right
    )

    if ($Left.Length -ne $Right.Length) {
        return $false
    }

    $difference = 0
    for ($index = 0; $index -lt $Left.Length; $index++) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }
    return $difference -eq 0
}

function Write-AIArenaUtf8NoBomJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath,
        [int]$Depth = 10
    )

    $output = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $output
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }

    $temporary = "$output.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $json = ($Value | ConvertTo-Json -Depth $Depth) + "`n"
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $output -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Invoke-AIArenaReleaseVerificationHarnesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration,
        [Parameter(Mandatory = $true)]
        [string[]]$ProjectPath,
        [string]$OutputPath = '',
        [string]$ReceiptKey = '',
        [string]$SourceCommit = '',
        [string]$SourceTree = ''
    )

    $writeReceipt = -not [string]::IsNullOrWhiteSpace($OutputPath)
    if ($writeReceipt -ne (-not [string]::IsNullOrWhiteSpace($ReceiptKey))) {
        throw 'Release-verification receipt output and its ephemeral key must be supplied together.'
    }
    if ($writeReceipt -and (Test-Path -LiteralPath $OutputPath)) {
        throw "Release-verification receipt output already exists: $OutputPath"
    }

    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or [string]::IsNullOrWhiteSpace($SourceTree)) {
        $source = Get-AIArenaReleaseVerificationSourceIdentity -RepositoryRoot $RepositoryRoot
        $SourceCommit = $source.Commit
        $SourceTree = $source.SrcTree
    }

    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnetFile = Get-Item -LiteralPath $dotnet.Source
    $runId = [Guid]::NewGuid().ToString('N')
    $executions = @()

    foreach ($project in $ProjectPath) {
        $fullProject = [IO.Path]::GetFullPath($project)
        if (-not $fullProject.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) `
            -or -not (Test-Path -LiteralPath $fullProject -PathType Leaf)) {
            throw "Release-verification project is missing or outside the repository: $project"
        }
        $relativeProject = $fullProject.Substring($rootPrefix.Length).Replace('\', '/')
        $arguments = @('run', '--project', $fullProject, '-c', $Configuration, '--no-restore')
        $startedUtc = [DateTime]::UtcNow
        & $dotnet.Source @arguments
        $exitCode = $LASTEXITCODE
        $completedUtc = [DateTime]::UtcNow
        if ($exitCode -ne 0) {
            throw "Release verification harness failed for $relativeProject with exit code $exitCode. No passed receipt was created."
        }

        $artifact = @(Get-AIArenaReleaseVerificationHarnesses `
            -RepositoryRoot $RepositoryRoot `
            -Configuration $Configuration `
            -ProjectPath @($fullProject))[0]
        $executions += [pscustomobject][ordered]@{
            project = $artifact.project
            targetFramework = $artifact.targetFramework
            assembly = $artifact.assembly
            bytes = $artifact.bytes
            sha256 = $artifact.sha256
            arguments = @('run', '--project', $artifact.project, '-c', $Configuration, '--no-restore')
            startedUtc = $startedUtc.ToString('o')
            completedUtc = $completedUtc.ToString('o')
            exitCode = [int]$exitCode
            outcome = 'passed'
        }
    }

    if (-not $writeReceipt) {
        return @($executions)
    }

    # This receipt authenticates hand-off between cooperating processes in one
    # release invocation. It is not a security boundary against malicious code
    # already running as the same local user, which can alter the scripts or
    # observe process memory. The local release account is a trusted boundary.
    $payload = [pscustomobject][ordered]@{
        schema = 'ai_arena.release_verification_payload.v2'
        formatVersion = 2
        runId = $runId
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        sourceCommit = $SourceCommit
        sourceTree = $SourceTree
        configuration = $Configuration
        runner = [pscustomobject][ordered]@{
            name = $dotnetFile.Name
            bytes = [long]$dotnetFile.Length
            sha256 = (Get-FileHash -LiteralPath $dotnetFile.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
        harnesses = @($executions | Sort-Object project)
    }
    $payloadJson = $payload | ConvertTo-Json -Depth 10 -Compress
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
    $keyBytes = ConvertFrom-AIArenaReleaseVerificationKey -ReceiptKey $ReceiptKey
    try {
        $authentication = Get-AIArenaHmacSha256 -Key $keyBytes -Value $payloadBytes
        $receipt = [pscustomobject][ordered]@{
            schema = 'ai_arena.release_verification_receipt.v2'
            formatVersion = 2
            trustBoundary = 'trusted-local-release-account'
            payloadBase64 = [Convert]::ToBase64String($payloadBytes)
            hmacSha256 = ([BitConverter]::ToString($authentication) -replace '-', '')
        }
        Write-AIArenaUtf8NoBomJson -Value $receipt -OutputPath $OutputPath
    }
    finally {
        [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    }

    return [IO.Path]::GetFullPath($OutputPath)
}

function Test-AIArenaReleaseVerificationReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$ReceiptPath,
        [Parameter(Mandatory = $true)]
        [string]$ReceiptKey,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration,
        [Parameter(Mandatory = $true)]
        [string[]]$ProjectPath,
        [string]$SourceCommit = '',
        [string]$SourceTree = ''
    )

    if (-not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {
        throw "Release-verification receipt is missing: $ReceiptPath"
    }
    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or [string]::IsNullOrWhiteSpace($SourceTree)) {
        $source = Get-AIArenaReleaseVerificationSourceIdentity -RepositoryRoot $RepositoryRoot
        $SourceCommit = $source.Commit
        $SourceTree = $source.SrcTree
    }

    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$receipt.schema -ne 'ai_arena.release_verification_receipt.v2' -or [int]$receipt.formatVersion -ne 2) {
        throw 'Release-verification receipt has an unsupported schema or format version.'
    }
    if ([string]$receipt.trustBoundary -ne 'trusted-local-release-account') {
        throw 'Release-verification receipt has an invalid trust-boundary declaration.'
    }

    try {
        $payloadBytes = [Convert]::FromBase64String([string]$receipt.payloadBase64)
        $recordedAuthentication = [byte[]]::new(32)
        $authenticationText = [string]$receipt.hmacSha256
        if ($authenticationText -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'invalid digest'
        }
        for ($index = 0; $index -lt 32; $index++) {
            $recordedAuthentication[$index] = [Convert]::ToByte($authenticationText.Substring($index * 2, 2), 16)
        }
    }
    catch {
        throw 'Release-verification receipt encoding is invalid.'
    }

    $keyBytes = ConvertFrom-AIArenaReleaseVerificationKey -ReceiptKey $ReceiptKey
    try {
        $expectedAuthentication = Get-AIArenaHmacSha256 -Key $keyBytes -Value $payloadBytes
        if (-not (Test-AIArenaByteArrayEqual -Left $recordedAuthentication -Right $expectedAuthentication)) {
            throw 'Release-verification receipt authentication failed; it was modified or did not come from this pipeline invocation.'
        }
    }
    finally {
        [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    }

    $payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
    if ([string]$payload.schema -ne 'ai_arena.release_verification_payload.v2' -or [int]$payload.formatVersion -ne 2) {
        throw 'Release-verification payload has an unsupported schema or format version.'
    }
    if ([string]$payload.sourceCommit -ne $SourceCommit -or [string]$payload.sourceTree -ne $SourceTree) {
        throw 'Release-verification receipt does not match the current commit and src tree.'
    }
    if ([string]$payload.configuration -ne $Configuration) {
        throw "Release-verification receipt configuration does not match $Configuration."
    }

    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnetFile = Get-Item -LiteralPath $dotnet.Source
    if ([string]$payload.runner.name -ne $dotnetFile.Name `
        -or [long]$payload.runner.bytes -ne [long]$dotnetFile.Length `
        -or [string]$payload.runner.sha256 -ne (Get-FileHash -LiteralPath $dotnetFile.FullName -Algorithm SHA256).Hash.ToUpperInvariant()) {
        throw 'Release-verification receipt runner identity is stale or invalid.'
    }

    $expected = @(Get-AIArenaReleaseVerificationHarnesses `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -ProjectPath $ProjectPath)
    $recorded = @($payload.harnesses | Sort-Object project)
    if ($recorded.Count -ne $expected.Count) {
        throw 'Release-verification receipt harness count does not match.'
    }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $actual = $recorded[$index]
        $wanted = $expected[$index]
        $wantedArguments = @('run', '--project', [string]$wanted.project, '-c', $Configuration, '--no-restore')
        $actualArguments = @($actual.arguments)
        if ([string]$actual.project -ne [string]$wanted.project `
            -or [string]$actual.targetFramework -ne [string]$wanted.targetFramework `
            -or [string]$actual.assembly -ne [string]$wanted.assembly `
            -or [long]$actual.bytes -ne [long]$wanted.bytes `
            -or [string]$actual.sha256 -ne [string]$wanted.sha256 `
            -or [int]$actual.exitCode -ne 0 `
            -or [string]$actual.outcome -ne 'passed' `
            -or ($actualArguments -join "`n") -ne ($wantedArguments -join "`n")) {
            throw "Release-verification receipt is stale or invalid for $($wanted.project)."
        }
    }

    return $true
}

function Get-AIArenaSha256FileIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Label is missing: $fullPath"
    }
    $file = Get-Item -LiteralPath $fullPath
    return [pscustomobject][ordered]@{
        name = $file.Name
        bytes = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

function Protect-AIArenaInstallerCompileDigest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Digest
    )

    if ($null -eq ('System.Security.Cryptography.ProtectedData' -as [type])) {
        Add-Type -AssemblyName System.Security
    }
    $entropy = [Text.Encoding]::UTF8.GetBytes('AI Arena installer compile receipt v1')
    return [Security.Cryptography.ProtectedData]::Protect(
        $Digest,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function Unprotect-AIArenaInstallerCompileDigest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$ProtectedDigest
    )

    if ($null -eq ('System.Security.Cryptography.ProtectedData' -as [type])) {
        Add-Type -AssemblyName System.Security
    }
    $entropy = [Text.Encoding]::UTF8.GetBytes('AI Arena installer compile receipt v1')
    return [Security.Cryptography.ProtectedData]::Unprotect(
        $ProtectedDigest,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function New-AIArenaInstallerCompileReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$ReleaseInventoryPath,
        [Parameter(Mandatory = $true)][string]$InnoScriptPath,
        [Parameter(Mandatory = $true)][string]$InnoCompilerPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][ValidateSet('Optional', 'Required', 'Disabled')][string]$SigningPolicy,
        [Parameter(Mandatory = $true)][bool]$SigningEnabled,
        [string]$SigningCertificateThumbprint = '',
        [string]$SourceCommit = '',
        [string]$SourceTree = ''
    )

    if ((Split-Path -Leaf ([IO.Path]::GetFullPath($InstallerPath))) -ne "AI Arena Setup $Version.exe") {
        throw 'Installer compile receipt target does not match the versioned installer name.'
    }
    if ((Split-Path -Leaf ([IO.Path]::GetFullPath($ReleaseInventoryPath))) -ne 'release-checksums.sha256') {
        throw 'Installer compile receipt must bind the canonical release-checksums.sha256 inventory.'
    }
    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or [string]::IsNullOrWhiteSpace($SourceTree)) {
        $source = Get-AIArenaReleaseVerificationSourceIdentity -RepositoryRoot $RepositoryRoot
        $SourceCommit = $source.Commit
        $SourceTree = $source.SrcTree
    }
    $compilerPath = [IO.Path]::GetFullPath($InnoCompilerPath)
    Assert-AIArenaTrustedExecutable -Path $compilerPath -Label 'Inno Setup compiler used for compile receipt'
    $compilerSignature = Get-AuthenticodeSignature -LiteralPath $compilerPath
    $payload = [pscustomobject][ordered]@{
        schema = 'ai_arena.installer_compile_payload.v1'
        formatVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        sourceCommit = $SourceCommit
        sourceTree = $SourceTree
        version = $Version
        configuration = $Configuration
        runtime = $Runtime
        signing = [pscustomobject][ordered]@{
            policy = $SigningPolicy
            enabled = $SigningEnabled
            certificateThumbprint = if ($SigningEnabled) { $SigningCertificateThumbprint } else { $null }
        }
        installer = Get-AIArenaSha256FileIdentity -Path $InstallerPath -Label 'Compiled installer'
        releaseInventory = Get-AIArenaSha256FileIdentity -Path $ReleaseInventoryPath -Label 'Release checksum inventory'
        inno = [pscustomobject][ordered]@{
            script = Get-AIArenaSha256FileIdentity -Path $InnoScriptPath -Label 'Inno Setup script'
            compiler = Get-AIArenaSha256FileIdentity -Path $compilerPath -Label 'Inno Setup compiler'
            compilerPath = $compilerPath
            compilerSignatureStatus = $compilerSignature.Status.ToString()
            compilerSignerThumbprint = if ($null -ne $compilerSignature.SignerCertificate) { $compilerSignature.SignerCertificate.Thumbprint } else { $null }
        }
    }
    $payloadJson = $payload | ConvertTo-Json -Depth 10 -Compress
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash($payloadBytes)
    }
    finally {
        $sha.Dispose()
    }
    # DPAPI makes stale/cross-account receipt substitution fail closed while
    # still allowing interrupted finalization to resume under the same release
    # account. As with the ephemeral harness receipt, malicious code already
    # running as that account is outside this local build-pipeline boundary.
    $protectedDigest = Protect-AIArenaInstallerCompileDigest -Digest $digest
    $receipt = [pscustomobject][ordered]@{
        schema = 'ai_arena.installer_compile_receipt.v1'
        formatVersion = 1
        trustBoundary = 'windows-current-user-release-account'
        payloadBase64 = [Convert]::ToBase64String($payloadBytes)
        protectedDigestBase64 = [Convert]::ToBase64String($protectedDigest)
    }
    Write-AIArenaUtf8NoBomJson -Value $receipt -OutputPath $OutputPath
    return [IO.Path]::GetFullPath($OutputPath)
}

function Test-AIArenaInstallerCompileReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ReceiptPath,
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$ReleaseInventoryPath,
        [Parameter(Mandatory = $true)][string]$InnoScriptPath,
        [string]$InnoCompilerPath = '',
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [string]$SigningPolicy = '',
        [string]$SourceCommit = '',
        [string]$SourceTree = ''
    )

    if (-not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {
        throw "Installer compile receipt is missing: $ReceiptPath"
    }
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$receipt.schema -ne 'ai_arena.installer_compile_receipt.v1' `
        -or [int]$receipt.formatVersion -ne 1 `
        -or [string]$receipt.trustBoundary -ne 'windows-current-user-release-account') {
        throw 'Installer compile receipt has an unsupported schema, format, or trust boundary.'
    }
    try {
        $payloadBytes = [Convert]::FromBase64String([string]$receipt.payloadBase64)
        $protectedDigest = [Convert]::FromBase64String([string]$receipt.protectedDigestBase64)
        $recordedDigest = Unprotect-AIArenaInstallerCompileDigest -ProtectedDigest $protectedDigest
    }
    catch {
        throw 'Installer compile receipt authentication could not be verified for the current Windows user.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedDigest = $sha.ComputeHash($payloadBytes)
    }
    finally {
        $sha.Dispose()
    }
    if (-not (Test-AIArenaByteArrayEqual -Left $recordedDigest -Right $expectedDigest)) {
        throw 'Installer compile receipt authentication failed; its payload was modified.'
    }
    $payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
    if ([string]$payload.schema -ne 'ai_arena.installer_compile_payload.v1' -or [int]$payload.formatVersion -ne 1) {
        throw 'Installer compile receipt payload has an unsupported schema or format.'
    }
    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or [string]::IsNullOrWhiteSpace($SourceTree)) {
        $source = Get-AIArenaReleaseVerificationSourceIdentity -RepositoryRoot $RepositoryRoot
        $SourceCommit = $source.Commit
        $SourceTree = $source.SrcTree
    }
    if ([string]$payload.sourceCommit -ne $SourceCommit -or [string]$payload.sourceTree -ne $SourceTree) {
        throw 'Installer compile receipt does not match the current commit and src tree.'
    }
    if ([string]$payload.version -ne $Version `
        -or [string]$payload.configuration -ne $Configuration `
        -or [string]$payload.runtime -ne $Runtime) {
        throw 'Installer compile receipt does not match the requested version, configuration, and runtime.'
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningPolicy) -and [string]$payload.signing.policy -ne $SigningPolicy) {
        throw 'Installer compile receipt does not match the requested signing policy.'
    }

    $installerIdentity = Get-AIArenaSha256FileIdentity -Path $InstallerPath -Label 'Compiled installer'
    $inventoryIdentity = Get-AIArenaSha256FileIdentity -Path $ReleaseInventoryPath -Label 'Release checksum inventory'
    $scriptIdentity = Get-AIArenaSha256FileIdentity -Path $InnoScriptPath -Label 'Inno Setup script'
    $compilerPath = if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) { [string]$payload.inno.compilerPath } else { [IO.Path]::GetFullPath($InnoCompilerPath) }
    $compilerIdentity = Get-AIArenaSha256FileIdentity -Path $compilerPath -Label 'Inno Setup compiler'
    $compilerSignature = Get-AuthenticodeSignature -LiteralPath $compilerPath
    foreach ($comparison in @(
        [pscustomobject]@{ Actual = $payload.installer; Expected = $installerIdentity; Label = 'installer bytes' },
        [pscustomobject]@{ Actual = $payload.releaseInventory; Expected = $inventoryIdentity; Label = 'release inventory' },
        [pscustomobject]@{ Actual = $payload.inno.script; Expected = $scriptIdentity; Label = 'Inno script' },
        [pscustomobject]@{ Actual = $payload.inno.compiler; Expected = $compilerIdentity; Label = 'Inno compiler' }
    )) {
        if ([string]$comparison.Actual.name -ne [string]$comparison.Expected.name `
            -or [long]$comparison.Actual.bytes -ne [long]$comparison.Expected.bytes `
            -or [string]$comparison.Actual.sha256 -ne [string]$comparison.Expected.sha256) {
            throw "Installer compile receipt is stale or invalid for $($comparison.Label)."
        }
    }
    if ([string]$payload.inno.compilerPath -ne [IO.Path]::GetFullPath($compilerPath) `
        -or [string]$payload.inno.compilerSignatureStatus -ne $compilerSignature.Status.ToString() `
        -or [string]$payload.inno.compilerSignerThumbprint -ne $(if ($null -ne $compilerSignature.SignerCertificate) { $compilerSignature.SignerCertificate.Thumbprint } else { '' })) {
        throw 'Installer compile receipt Inno compiler provenance is stale or invalid.'
    }

    return $payload
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
