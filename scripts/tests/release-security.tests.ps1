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

    $inventoryRoot = Join-Path $fixtureRoot 'inventory'
    [void](New-Item -ItemType Directory -Path $inventoryRoot -Force)
    [IO.File]::WriteAllText((Join-Path $inventoryRoot 'alpha.txt'), 'alpha', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $inventoryRoot 'beta.txt'), 'beta', [Text.UTF8Encoding]::new($false))
    $inventoryPath = Join-Path $inventoryRoot 'release-checksums.sha256'
    $inventoryEntries = @(Get-AIArenaSha256Entries -BaseDirectory $inventoryRoot -ExcludeRelativePath @('release-checksums.sha256'))
    Write-AIArenaSha256ManifestEntries -BaseDirectory $inventoryRoot -OutputPath $inventoryPath -Entries $inventoryEntries
    $manifestBytes = [IO.File]::ReadAllBytes($inventoryPath)
    Require (-not ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF)) 'Checksum manifests must be UTF-8 without a BOM under Windows PowerShell 5.'
    $expectedManifestLines = @($inventoryEntries | Sort-Object RelativePath | ForEach-Object { "$($_.Hash.ToUpperInvariant())  $($_.RelativePath)" })
    $expectedManifestBytes = [Text.Encoding]::UTF8.GetBytes(($expectedManifestLines -join "`n") + "`n")
    Require ([Convert]::ToBase64String($manifestBytes) -eq [Convert]::ToBase64String($expectedManifestBytes)) 'Checksum manifest bytes must exactly match the canonical LF-delimited UTF-8 encoding.'
    Require ((Get-Content -LiteralPath $inventoryPath -First 1) -eq $expectedManifestLines[0]) 'Checksum manifest first line must begin directly with its SHA-256 digest.'
    Require ([bool](Test-AIArenaSha256Manifest -BaseDirectory $inventoryRoot -ManifestPath $inventoryPath)) 'A canonical precomputed inventory should verify.'
    [IO.File]::AppendAllText((Join-Path $inventoryRoot 'alpha.txt'), '-corrupt', [Text.UTF8Encoding]::new($false))
    Require-Throws {
        Test-AIArenaSha256Manifest -BaseDirectory $inventoryRoot -ManifestPath $inventoryPath
    } 'Checksum mismatch' 'A payload mutation must fail the independent inventory verification.'

    $receiptRoot = Join-Path $fixtureRoot 'receipt-repository'
    $coreProject = Join-Path $receiptRoot 'tests/CoreHarness/CoreHarness.csproj'
    $wpfProject = Join-Path $receiptRoot 'tests/WpfHarness/WpfHarness.csproj'
    foreach ($project in @($coreProject, $wpfProject)) {
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $project) -Force)
    }
    $projectText = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    [IO.File]::WriteAllText($coreProject, $projectText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($wpfProject, $projectText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $coreProject) 'Program.cs'), 'return 0;', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $wpfProject) 'Program.cs'), 'return 0;', [Text.UTF8Encoding]::new($false))
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
    foreach ($project in @($coreProject, $wpfProject)) {
        & $dotnet.Source build $project -c Release --nologo
        Require ($LASTEXITCODE -eq 0) "Disposable verification harness failed to build: $project"
    }
    $coreAssembly = Join-Path (Split-Path -Parent $coreProject) 'bin/Release/net10.0/CoreHarness.dll'
    $wpfAssembly = Join-Path (Split-Path -Parent $wpfProject) 'bin/Release/net10.0/WpfHarness.dll'
    $receiptPath = Join-Path $fixtureRoot 'release-verification.json'
    $fixtureCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $fixtureTree = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $receiptKey = New-AIArenaReleaseVerificationKey
    [void](Invoke-AIArenaReleaseVerificationHarnesses `
        -RepositoryRoot $receiptRoot `
        -OutputPath $receiptPath `
        -ReceiptKey $receiptKey `
        -Configuration Release `
        -ProjectPath @($coreProject, $wpfProject) `
        -SourceCommit $fixtureCommit `
        -SourceTree $fixtureTree)
    $validReceiptBytes = [IO.File]::ReadAllBytes($receiptPath)
    Require ([bool](Test-AIArenaReleaseVerificationReceipt `
        -RepositoryRoot $receiptRoot `
        -ReceiptPath $receiptPath `
        -ReceiptKey $receiptKey `
        -Configuration Release `
        -ProjectPath @($coreProject, $wpfProject) `
        -SourceCommit $fixtureCommit `
        -SourceTree $fixtureTree)) 'An exact pipeline receipt should verify.'
    Require-Throws {
        Test-AIArenaReleaseVerificationReceipt -RepositoryRoot $receiptRoot -ReceiptPath $receiptPath -ReceiptKey $receiptKey -Configuration Release -ProjectPath @($coreProject, $wpfProject) -SourceCommit (('c' * 40) -join '') -SourceTree $fixtureTree
    } 'current commit and src tree' 'A stale source identity must reject the receipt.'
    Require-Throws {
        Test-AIArenaReleaseVerificationReceipt -RepositoryRoot $receiptRoot -ReceiptPath $receiptPath -ReceiptKey $receiptKey -Configuration Debug -ProjectPath @($coreProject, $wpfProject) -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'configuration' 'A configuration mismatch must reject the receipt before using another build.'
    [IO.File]::AppendAllText($coreAssembly, 'changed', [Text.UTF8Encoding]::new($false))
    Require-Throws {
        Test-AIArenaReleaseVerificationReceipt -RepositoryRoot $receiptRoot -ReceiptPath $receiptPath -ReceiptKey $receiptKey -Configuration Release -ProjectPath @($coreProject, $wpfProject) -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'stale or invalid' 'A changed test assembly must reject the receipt.'
    & $dotnet.Source build $coreProject -c Release --nologo --no-restore
    Require ($LASTEXITCODE -eq 0) 'Disposable core verification harness failed to restore its assembly.'
    $forged = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $forgedPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$forged.payloadBase64)) | ConvertFrom-Json
    $forgedPayload.harnesses[0].outcome = 'failed'
    $forged.payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($forgedPayload | ConvertTo-Json -Depth 10 -Compress)))
    Write-AIArenaUtf8NoBomJson -Value $forged -OutputPath $receiptPath
    Require-Throws {
        Test-AIArenaReleaseVerificationReceipt -RepositoryRoot $receiptRoot -ReceiptPath $receiptPath -ReceiptKey $receiptKey -Configuration Release -ProjectPath @($coreProject, $wpfProject) -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'authentication failed' 'A modified harness outcome must fail authentication before it can be trusted.'
    [IO.File]::WriteAllBytes($receiptPath, $validReceiptBytes)
    Require-Throws {
        Test-AIArenaReleaseVerificationReceipt -RepositoryRoot $receiptRoot -ReceiptPath $receiptPath -ReceiptKey (New-AIArenaReleaseVerificationKey) -Configuration Release -ProjectPath @($coreProject, $wpfProject) -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'authentication failed' 'A receipt forged without this pipeline invocation key must fail closed.'

    $failedReceiptPath = Join-Path $fixtureRoot 'failed-release-verification.json'
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $coreProject) 'Program.cs'), 'return 7;', [Text.UTF8Encoding]::new($false))
    & $dotnet.Source build $coreProject -c Release --nologo --no-restore
    Require ($LASTEXITCODE -eq 0) 'Disposable failing harness failed to build.'
    Require-Throws {
        Invoke-AIArenaReleaseVerificationHarnesses -RepositoryRoot $receiptRoot -OutputPath $failedReceiptPath -ReceiptKey (New-AIArenaReleaseVerificationKey) -Configuration Release -ProjectPath @($coreProject) -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'No passed receipt was created' 'A non-zero harness process result must not be representable as a passed receipt.'
    Require ($LASTEXITCODE -eq 7) 'The failing harness fixture must preserve its exact native process exit code until the failure assertions finish.'
    Require (-not (Test-Path -LiteralPath $failedReceiptPath)) 'A failed harness execution must leave no reusable receipt file.'
    # This native failure is deliberate and fully asserted above. Follow it with
    # an asserted native success so both call-invoked and dot-sourced hosts see a
    # successful automatic exit code; direct assignment would be script-scoped.
    & $dotnet.Source --version | Out-Null
    Require ($LASTEXITCODE -eq 0) 'The native exit-code reset probe must succeed.'

    $compileFixtureRoot = Join-Path $fixtureRoot 'installer-compile'
    [void](New-Item -ItemType Directory -Path $compileFixtureRoot -Force)
    $compiledInstaller = Join-Path $compileFixtureRoot 'AI Arena Setup 9.9.9-test.exe'
    $compileInventory = Join-Path $compileFixtureRoot 'release-checksums.sha256'
    $compileInnoScript = Join-Path $compileFixtureRoot 'fixture.iss'
    $compileCompiler = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    [IO.File]::WriteAllBytes($compiledInstaller, [byte[]](10, 20, 30, 40, 50))
    [IO.File]::WriteAllText($compileInventory, ('A' * 64) + '  AI Arena.exe' + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($compileInnoScript, '#define MyAppVersion "9.9.9-test"', [Text.UTF8Encoding]::new($false))
    $compileReceiptPath = Join-Path $compileFixtureRoot 'installer-compile-receipt.json'
    [void](New-AIArenaInstallerCompileReceipt `
        -RepositoryRoot $receiptRoot `
        -OutputPath $compileReceiptPath `
        -InstallerPath $compiledInstaller `
        -ReleaseInventoryPath $compileInventory `
        -InnoScriptPath $compileInnoScript `
        -InnoCompilerPath $compileCompiler `
        -Version '9.9.9-test' `
        -Configuration Release `
        -Runtime win-x64 `
        -SigningPolicy Disabled `
        -SigningEnabled $false `
        -SourceCommit $fixtureCommit `
        -SourceTree $fixtureTree)
    $validCompileReceiptBytes = [IO.File]::ReadAllBytes($compileReceiptPath)
    [void](Test-AIArenaInstallerCompileReceipt `
        -RepositoryRoot $receiptRoot `
        -ReceiptPath $compileReceiptPath `
        -InstallerPath $compiledInstaller `
        -ReleaseInventoryPath $compileInventory `
        -InnoScriptPath $compileInnoScript `
        -Version '9.9.9-test' `
        -Configuration Release `
        -Runtime win-x64 `
        -SigningPolicy Disabled `
        -SourceCommit $fixtureCommit `
        -SourceTree $fixtureTree)
    [IO.File]::AppendAllText($compiledInstaller, 'replacement', [Text.UTF8Encoding]::new($false))
    Require-Throws {
        Test-AIArenaInstallerCompileReceipt -RepositoryRoot $receiptRoot -ReceiptPath $compileReceiptPath -InstallerPath $compiledInstaller -ReleaseInventoryPath $compileInventory -InnoScriptPath $compileInnoScript -Version '9.9.9-test' -Configuration Release -Runtime win-x64 -SigningPolicy Disabled -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'installer bytes' 'A replaced installer must be rejected by its post-compile receipt.'
    [IO.File]::WriteAllBytes($compiledInstaller, [byte[]](10, 20, 30, 40, 50))
    [IO.File]::AppendAllText($compileInventory, 'stale', [Text.UTF8Encoding]::new($false))
    Require-Throws {
        Test-AIArenaInstallerCompileReceipt -RepositoryRoot $receiptRoot -ReceiptPath $compileReceiptPath -InstallerPath $compiledInstaller -ReleaseInventoryPath $compileInventory -InnoScriptPath $compileInnoScript -Version '9.9.9-test' -Configuration Release -Runtime win-x64 -SigningPolicy Disabled -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'release inventory' 'A stale release inventory must reject installer resume.'
    [IO.File]::WriteAllText($compileInventory, ('A' * 64) + '  AI Arena.exe' + "`n", [Text.UTF8Encoding]::new($false))
    $forgedCompileReceipt = Get-Content -LiteralPath $compileReceiptPath -Raw | ConvertFrom-Json
    $forgedCompilePayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$forgedCompileReceipt.payloadBase64)) | ConvertFrom-Json
    $forgedCompilePayload.version = '9.9.10-test'
    $forgedCompileReceipt.payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($forgedCompilePayload | ConvertTo-Json -Depth 10 -Compress)))
    Write-AIArenaUtf8NoBomJson -Value $forgedCompileReceipt -OutputPath $compileReceiptPath
    Require-Throws {
        Test-AIArenaInstallerCompileReceipt -RepositoryRoot $receiptRoot -ReceiptPath $compileReceiptPath -InstallerPath $compiledInstaller -ReleaseInventoryPath $compileInventory -InnoScriptPath $compileInnoScript -Version '9.9.9-test' -Configuration Release -Runtime win-x64 -SigningPolicy Disabled -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'authentication failed' 'A forged installer compile receipt must fail its current-user proof.'
    [IO.File]::WriteAllBytes($compileReceiptPath, $validCompileReceiptBytes)
    Require-Throws {
        Test-AIArenaInstallerCompileReceipt -RepositoryRoot $receiptRoot -ReceiptPath (Join-Path $compileFixtureRoot 'missing.json') -InstallerPath $compiledInstaller -ReleaseInventoryPath $compileInventory -InnoScriptPath $compileInnoScript -Version '9.9.9-test' -Configuration Release -Runtime win-x64 -SigningPolicy Disabled -SourceCommit $fixtureCommit -SourceTree $fixtureTree
    } 'receipt is missing' 'Resume finalization must fail closed when its post-compile receipt is missing.'

    $installerPipelineText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts/build-wpf-installer.ps1') -Raw
    $releasePipelineText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts/build-wpf-release.ps1') -Raw
    $sanityPipelineText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts/wpf-release-sanity.ps1') -Raw
    Require ($installerPipelineText -match '(?s)Invoke-AIArenaAuthenticodeSigning.+New-AIArenaInstallerCompileReceipt') 'The exact installer receipt must be emitted only after compile and any Authenticode mutation have succeeded.'
    Require ($installerPipelineText -match '(?s)ResumeFinalization.+Test-AIArenaInstallerCompileReceipt') 'Resume finalization must validate the exact post-compile receipt before copying release artifacts.'
    Require ($sanityPipelineText.Contains('Test-AIArenaInstallerCompileReceipt')) 'Release sanity must independently validate installer compile provenance.'
    Require ($releasePipelineText.Contains('Invoke-AIArenaReleaseVerificationHarnesses') -and -not $releasePipelineText.Contains('New-AIArenaReleaseVerificationReceipt')) 'Release receipt creation must be inseparable from the function that observes harness process results.'
    Require ($sanityPipelineText.Contains('VerificationReceiptPath and its ephemeral VerificationReceiptKey must be supplied together.')) 'Standalone sanity must not accept an unauthenticated harness receipt.'

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

    Require ($LASTEXITCODE -eq 0) "Successful fixture completion must not leak a native process failure; found exit code $LASTEXITCODE."
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
