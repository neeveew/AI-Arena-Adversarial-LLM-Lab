# Release integrity and signing

AI Arena's Windows release pipeline treats downloaded runtimes, package contents, and distributable files as separate integrity boundaries.

## Upstream lock

`packaging/upstream-lock.json` pins the exact CPython embeddable archive and SearXNG source revision used by the bundled local-search payload. Each archive has a reviewed SHA-256 digest. `packaging/searxng-requirements-lock.txt` additionally pins every Windows CPython wheel, including transitive dependencies, by version and SHA-256. `build-searxng-payload.ps1` verifies existing cached archives and every new download before extraction, runs pip in isolated `--require-hashes` mode against the official PyPI index, attests that resolved wheel URLs use the official PyPI file host, rejects non-HTTPS sources, and fails closed on any mismatch.

To update an upstream, download the new fixed artifact from its official HTTPS location, calculate its SHA-256 independently, review the upstream revision and resolved Windows wheels, then update the version, URL, digest, and dependency lock together. Do not reuse a digest from an untrusted mirror.

## Generated inventories and checksums

The SearXNG payload includes `payload-inventory.json`. It records:

- the pinned Python, SearXNG, and Granian versions;
- the verified upstream archive URLs and digests;
- installed Python distribution names and versions; and
- every payload file's relative path, byte length, and SHA-256 digest, excluding the inventory itself.

The bundled `settings.yml` is an Arena-specific private sidecar profile: it binds only to loopback, exposes JSON search output rather than a general local search UI, disables metrics and auxiliary lookups, and caps outbound request timeouts, redirects, retries, and connection pools. It inherits the engine catalog from the pinned SearXNG revision instead of maintaining a brittle `keep_only` engine list; Arena performs its own source ranking and diversity pass.

The release folder includes `release-checksums.sha256`, `release-manifest.txt`, and `release-signing.json`. The installer distribution includes `SHA256SUMS.txt` and `installer-signing.json`. Release sanity recomputes every recorded digest, checks for missing or unexpected files, validates the payload inventory against the upstream lock, and enforces the recorded signing policy.

## Authenticode policy

The release and installer builders accept `-SigningPolicy Optional`, `Required`, or `Disabled`.

- `Optional` is the default. With no certificate configured, the build remains unsigned and records that fact. If a certificate is configured, signing or verification failure stops the build.
- `Required` fails during preflight unless a usable, trusted certificate and SignTool are available. Both `AI Arena.exe` and the installer must verify as valid under the Windows Authenticode policy and carry an RFC 3161 timestamp.
- `Disabled` intentionally skips signing and records the policy.

For a production signed build, install a trusted Authenticode code-signing certificate with an accessible private key into `CurrentUser\My` or `LocalMachine\My`. Install the Windows SDK Signing Tools and Inno Setup 6, ensure the timestamp service is reachable, then run:

```powershell
. ./scripts/release-security.ps1

# Discovery is read-only and never auto-selects a certificate.
Find-AIArenaCodeSigningCertificates |
    Format-Table StoreLocation, Thumbprint, Subject, NotAfter

$env:AIARENA_SIGNING_CERT_THUMBPRINT = "YOUR_CERTIFICATE_THUMBPRINT"
Test-AIArenaSigningPreflight `
    -CertificateThumbprint $env:AIARENA_SIGNING_CERT_THUMBPRINT

./scripts/build-wpf-installer.ps1 -Version 0.4.89-beta -SigningPolicy Required
```

Certificate selection remains explicit even when discovery finds exactly one candidate, so an unrelated development certificate can never silently sign a release. Preflight verifies the code-signing EKU, digital-signature key usage, validity dates, trusted Windows chain, RSA/ECDSA key strength, and a live private-key signing proof without exporting the key. It also discovers the newest x64 Windows SDK `signtool.exe` and verifies the tool's Microsoft Authenticode signature before use. The installer builder likewise requires a valid Authenticode signature on the selected Inno Setup compiler.

Signing uses SHA-256 for the file digest and RFC 3161 timestamp digest. Immediately after each signing operation, SignTool verifies every embedded signature under the Windows Authenticode policy and warns on a missing timestamp; the release helper independently requires the requested signer thumbprint and a timestamp certificate with the time-stamping EKU. Release sanity repeats those checks against both the app and installer and rejects:

- a valid signature from a different certificate;
- a missing, invalid, or unrecorded timestamp;
- disagreement between release and installer signing reports; and
- any signed artifact when the reports say signing was disabled.

Use `-SignTool` to pass an explicit trusted executable path or `-TimestampUrl` to select another RFC 3161 timestamp service. Never put a PFX password on a command line or commit a private key, certificate password, or signing token.

The focused fixture uses short-lived, non-exported certificates in `CurrentUser\My`, confirms that their keys are usable, confirms that an untrusted development chain is rejected for production, and removes the certificates in `finally`. It never writes a certificate to a trusted-root store:

```powershell
./scripts/tests/release-security.tests.ps1
```

Before publishing, run release sanity with the same policy:

```powershell
./scripts/wpf-release-sanity.ps1 -Version 0.4.89-beta -SigningPolicy Required
```
