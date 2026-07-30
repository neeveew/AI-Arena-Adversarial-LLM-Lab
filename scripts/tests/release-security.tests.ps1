$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repositoryRoot 'scripts/release-security.ps1')

$fixtureId = [Guid]::NewGuid().ToString('N')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "ai-arena-release-security-$fixtureId"
$fixtureScript = Join-Path $fixtureRoot 'unsigned-fixture.ps1'
$signerSubject = "CN=AI Arena Release Security Fixture Signer $fixtureId"
$timestampSubject = "CN=AI Arena Release Security Fixture TSA $fixtureId"
$createdCertificates = [Collections.Generic.List[System.Security.Cryptography.X509Certificates.X509Certificate2]]::new()

function Require {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Require-Throws {
    param(
        [scriptblock]$Action,
        [string]$Pattern,
        [string]$Message
    )

    $failure = $null
    try {
        & $Action
    }
    catch {
        $failure = $_
    }

    Require ($null -ne $failure) $Message
    if (-not [string]::IsNullOrWhiteSpace($Pattern)) {
        Require ($failure.Exception.Message -match $Pattern) "$Message Actual error: $($failure.Exception.Message)"
    }
}

function Remove-FixtureCertificates {
    param(
        [System.Security.Cryptography.X509Certificates.StoreName]$StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]$StoreLocation,
        [string[]]$Thumbprint
    )

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, $StoreLocation)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($value in $Thumbprint) {
            if ([string]::IsNullOrWhiteSpace($value)) {
                continue
            }

            foreach ($certificate in @($store.Certificates | Where-Object { $_.Thumbprint -eq $value })) {
                $store.Remove($certificate)
            }
        }
    }
    finally {
        $store.Dispose()
    }
}

try {
    [void](New-Item -ItemType Directory -Path $fixtureRoot -Force)
    [IO.File]::WriteAllText(
        $fixtureScript,
        "'AI Arena unsigned Authenticode fixture'`r`n",
        [Text.UTF8Encoding]::new($false))

    $legacyUnsignedRecord = [pscustomobject]@{
        status = 'NotSigned'
    }
    Require (-not [bool](Get-AIArenaOptionalPropertyValue -InputObject $legacyUnsignedRecord -Name 'timestampVerified' -DefaultValue $false)) 'Legacy unsigned reports should default an absent timestamp attestation to false.'
    Require ([string](Get-AIArenaOptionalPropertyValue -InputObject $legacyUnsignedRecord -Name 'certificateKeyAlgorithm' -DefaultValue '') -eq '') 'Legacy unsigned reports should default an absent key algorithm to an empty value.'
    Require ([int](Get-AIArenaOptionalPropertyValue -InputObject $legacyUnsignedRecord -Name 'certificateKeySize' -DefaultValue 0) -eq 0) 'Legacy unsigned reports should default an absent key size to zero.'
    Assert-AIArenaSigningPolicyState -Policy Optional -SigningEnabled $false
    Assert-AIArenaSigningPolicyState -Policy Optional -SigningEnabled $true
    Assert-AIArenaSigningPolicyState -Policy Disabled -SigningEnabled $false
    Assert-AIArenaSigningPolicyState -Policy Required -SigningEnabled $true
    Require-Throws {
        Assert-AIArenaSigningPolicyState -Policy Required -SigningEnabled $false
    } 'cannot be recorded with signing disabled' 'Required policy must reject an unsigned report state.'
    Require-Throws {
        Assert-AIArenaSigningPolicyState -Policy Disabled -SigningEnabled $true
    } 'cannot be recorded with signing enabled' 'Disabled policy must reject a signed report state.'

    $disabled = Resolve-AIArenaSigningConfiguration -Policy Disabled
    Require (-not $disabled.Enabled) 'Disabled signing policy should remain disabled.'
    $optional = Resolve-AIArenaSigningConfiguration -Policy Optional
    Require (-not $optional.Enabled) 'Optional policy without an explicit thumbprint must not auto-select a certificate.'
    Require ($optional.Reason -match 'No signing certificate thumbprint was supplied') 'Optional discovery should explain that certificate selection remains explicit.'
    Require-Throws {
        Resolve-AIArenaSigningConfiguration -Policy Required
    } 'no certificate thumbprint was supplied' 'Required signing must reject an omitted thumbprint.'
    Require-Throws {
        Normalize-AIArenaCertificateThumbprint -Thumbprint 'not-a-thumbprint'
    } '40 to 128 hexadecimal' 'Malformed thumbprints must be rejected.'

    $unsignedRecords = @(Invoke-AIArenaAuthenticodeSigning -Configuration $disabled -Path @($fixtureScript))
    Require ($unsignedRecords.Count -eq 1) 'Disabled signing should still inventory exactly one target.'
    Require ($unsignedRecords[0].status -eq 'NotSigned') 'Disabled signing should report an unsigned PowerShell fixture.'
    Require (-not $unsignedRecords[0].timestampVerified) 'Unsigned fixtures must not claim timestamp verification.'

    if ($null -eq (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
        throw 'New-SelfSignedCertificate is required for the disposable signing preflight fixture.'
    }

    $signerCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $signerSubject `
        -FriendlyName "AI Arena release-security fixture signer $fixtureId" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -TextExtension @(
            '2.5.29.19={critical}{text}ca=0',
            "2.5.29.37={critical}{text}$script:AIArenaCodeSigningEkuOid"
        ) `
        -NotBefore ([DateTime]::Now.AddMinutes(-5)) `
        -NotAfter ([DateTime]::Now.AddDays(1))
    $createdCertificates.Add($signerCertificate)

    $timestampCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $timestampSubject `
        -FriendlyName "AI Arena release-security fixture TSA $fixtureId" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -TextExtension @(
            '2.5.29.19={critical}{text}ca=0',
            "2.5.29.37={critical}{text}$script:AIArenaTimestampingEkuOid"
        ) `
        -NotBefore ([DateTime]::Now.AddMinutes(-5)) `
        -NotAfter ([DateTime]::Now.AddDays(1))
    $createdCertificates.Add($timestampCertificate)

    $record = Get-AIArenaCodeSigningCertificateRecord -Thumbprint $signerCertificate.Thumbprint
    Require ($null -ne $record) 'Disposable signer should be discoverable by thumbprint.'
    Require ($record.StoreLocation -eq 'CurrentUser') 'Disposable signer should report its CurrentUser store.'
    Require ($record.Candidate) 'Disposable signer should satisfy the discovery candidate contract.'
    $keyProof = Assert-AIArenaCertificatePrivateKey -Certificate $signerCertificate
    Require ($keyProof.Algorithm -eq 'RSA' -and $keyProof.KeySize -ge 2048) 'Disposable signer should prove its RSA private key without exporting it.'
    Require-Throws {
        Assert-AIArenaCodeSigningCertificate -CertificateRecord $record
    } 'trusted root' 'An untrusted development certificate must be blocked by production preflight.'
    Require-Throws {
        Test-AIArenaSigningPreflight -CertificateThumbprint $signerCertificate.Thumbprint
    } 'trusted root' 'The public signing-preflight command must reject an untrusted development certificate.'

    $validSignature = [pscustomobject]@{
        Status = [System.Management.Automation.SignatureStatus]::Valid
        SignerCertificate = $signerCertificate
        TimeStamperCertificate = $timestampCertificate
    }
    $signatureVerification = Assert-AIArenaAuthenticodeSignature `
        -Signature $validSignature `
        -ExpectedSignerThumbprint $signerCertificate.Thumbprint `
        -RequireTimestamp `
        -Label 'Disposable signature fixture'
    Require ($signatureVerification.TimestampVerified) 'Valid fixture signature should attest timestamp verification.'
    Require ($signatureVerification.SignerThumbprint -eq $signerCertificate.Thumbprint) 'Signature verification should preserve the expected signer.'
    Require ($signatureVerification.SignerKeyAlgorithm -eq 'RSA' -and $signatureVerification.SignerKeySize -ge 2048) 'Signature verification should attest the signer public-key strength.'

    Require-Throws {
        Assert-AIArenaAuthenticodeSignature `
            -Signature $validSignature `
            -ExpectedSignerThumbprint $timestampCertificate.Thumbprint `
            -RequireTimestamp `
            -Label 'Wrong-signer fixture'
    } 'not the requested certificate' 'A valid signature from the wrong signer must be rejected.'

    $missingTimestamp = [pscustomobject]@{
        Status = [System.Management.Automation.SignatureStatus]::Valid
        SignerCertificate = $signerCertificate
        TimeStamperCertificate = $null
    }
    Require-Throws {
        Assert-AIArenaAuthenticodeSignature `
            -Signature $missingTimestamp `
            -ExpectedSignerThumbprint $signerCertificate.Thumbprint `
            -RequireTimestamp `
            -Label 'Missing-timestamp fixture'
    } 'required RFC 3161 timestamp' 'A valid but untimestamped signature must be rejected.'

    Write-Host 'Release-security fixture tests passed.'
}
finally {
    $thumbprints = @($createdCertificates | ForEach-Object { $_.Thumbprint })
    Remove-FixtureCertificates `
        -StoreName My `
        -StoreLocation CurrentUser `
        -Thumbprint $thumbprints

    foreach ($certificate in $createdCertificates) {
        $certificate.Dispose()
    }

    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolvedFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if (-not $resolvedFixtureRoot.StartsWith(
            $resolvedTemp + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixture cleanup path escaped the temporary directory: $resolvedFixtureRoot"
        }
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
