# AI Arena Lite publication audit — 3 October 2026

The final **0.4.144-beta** installer passes the repaired production pipeline and
the publisher's independent packaging checks. The final verification and
dependency-security follow-up are recorded below. The original 0.4.142-beta
installer is **not cleared for publication**: it contains local build paths and
lacks the .NET dependency notices required by the repaired pipeline. The
intermediate 0.4.143-beta draft is also held because of its AnyIO dependency.

Audited source: `a5eab85d69b85c288717bd7f5e8fe0d1b8a71563`.
Application source tree: `3c5fd8e0ee7468f3ab7a02bae4fcb36d0f67ce6f`.
Original installer SHA-256:
`55D3CD0D58E7BCFCFAC87D1F59A52613FF3A5A71E3AE9A1B95EAC1BA96C013E0`.
The audit repairs change packaging and documentation, not application source.

| Area | Evidence and disposition |
| --- | --- |
| Source | Inspected 674 tracked files, 25,660,824 bytes. Credential-shaped matches were synthetic privacy-test inputs. A documentation example exposed a real checkout location; it now uses a relative command. |
| Original payload | Inspected 4,538 files, 355,519,664 bytes, including a scan inside the embedded Python standard-library archive. No confirmed personal API credentials, saved user sessions, private transcripts, crash dumps, or application logs were found. |
| Debug information | The three first-party portable PDBs contained 352 local document paths. The corresponding DLL CodeView records also contained checkout paths. Public builds now disable symbol publication and map source paths; vendor PDBs are removed from the consumer payload. |
| Python launchers | Eight pip-generated command launchers embedded the developer's Python installation path. They are removed. The application uses its own bundled interpreter with `-m granian`, so these launchers are unnecessary. |
| Test artifacts | Seven upstream Colorama unit-test source files were unnecessary in the runtime payload and are excluded. Source-repository tests remain available for development. Local agent configuration and scratch artifacts are explicitly ignored by Git. |
| .NET notices | The original distribution had no .NET dependency notice bundle. The repaired pipeline installs hash-pinned licence texts and upstream notices for 34 reviewed package/runtime versions, with copyright attributions and source references. This includes the nested Roslyn build-host dependency declarations. |
| Python notices | The 41 Python distributions retain their licence files and metadata. CPython's licence is included. A CA certificate bundle is an intentional public trust store, not a private key. |
| SearXNG | AGPL-3.0-or-later applies to the separate search service and Python gateway. The full verified upstream archive is now included, alongside editable gateway/shim source and configuration. Its SHA-256 is `B2A9F9836C6A916E3B0D4235DFB8B766D96285987A87B1B90C9B2EC61D45D7E9`. |
| Original licence | An explicit scope clause prevents AI Arena's no-derivatives terms from overriding third-party licences. The generated Windows search shim also explicitly uses AGPL-3.0-or-later. |
| Screenshots | Visually reviewed the README workspace image and four tracked product screenshots. They show the application, demonstration content and model names; no personal credentials or personal filesystem paths were apparent. |

The rebuilt candidate contains **4,574 files / 360,058,044 bytes**. It has no PDBs,
no first-party CodeView path records, and no detected personal paths, credential
patterns, or runtime test/cache directories. Its larger size mainly reflects the
included full SearXNG source and licence materials.

Initial pattern matches were reviewed rather than counted as confirmed secrets:
the source tests use synthetic credentials to check redaction, many apparent
private IP addresses were .NET version strings or upstream networking examples,
and a Google-shaped key is part of the pinned upstream YouTube search engine.
Binary matches produced only by stripping null bytes were checked against actual
UTF-8/UTF-16 decoding. None of those establishes an owner credential leak.

Validation completed: self-contained Windows x64 .NET publish; a new SearXNG
payload build from verified archives and hash-locked wheels; its import/gateway
probe; the compliance gate on all candidate files; independent binary CodeView
inspection and hashed file inventory; positive and negative compliance fixtures;
PowerShell syntax checks; and `git diff --check`. The gate rejects the original
distribution because it lacks reviewed dependency notices. The full app/UI test
suite and final installer compilation were not repeated during this packaging
audit; those remain part of the normal production rebuild.

The backend declarations reviewed are compatible with retaining a separate
licence for AI Arena's original work, subject to their notice and source-sharing
conditions. The managed dependencies primarily declare MIT, BSD-3-Clause or
Apache-2.0. SearXNG has the distinct AGPL obligation. It runs as a separate local
HTTP service; that separation supports treating it as a separately licensed
component, but is not a legal determination. The FSF explains that both the
communication mechanism and the semantics of integration matter in its
[aggregation guidance](https://www.gnu.org/licenses/gpl-faq.html#MereAggregation).
Apache's redistribution requirements include licence/notice retention, described
in [section 4](https://www.apache.org/licenses/LICENSE-2.0).

This is an engineering distribution audit, not a guarantee of copyright ownership
or a legal clearance certificate. It does not establish provenance for every
line of upstream code, reproduce every third-party binary, inspect historical
Git blobs, assess trademarks or patents, or re-audit the native C++ edition.
AngleSharp's package declares MIT but does not identify a repository commit; its
declared copyright and a pinned upstream MIT text are retained without claiming
a source-to-binary match. Visual authorship is based on the owner's statement,
not an independent chain-of-title investigation. Pattern scans cannot establish
that arbitrary unrecognised private data is absent.

Internal evidence is retained under `artifacts/release-audit-20261003/`, which is
excluded from Git and must not be uploaded with the release. Publication should
use a fresh production installer, its new checksums and source identity after
the repaired pipeline and ordinary release checks pass.

## Final production and dependency-security follow-up

The production rebuild first produced 0.4.143-beta, which passed the packaging
and application checks. GitHub's dependency alerts then identified three AnyIO
4.14.1 advisories, so that candidate remains an **unpublished draft**. A separate
0.4.144-beta build updates the optional bundled search service to AnyIO 4.14.2:

- [TLS hostname validation, critical](https://github.com/advisories/GHSA-82r6-8w77-94w6).
- [POSIX subprocess supplementary groups, high](https://github.com/advisories/GHSA-3w57-8xmc-8v26).
- [Process-pool stderr deadlock, moderate](https://github.com/advisories/GHSA-5p39-cfhj-2xmp).

The official `anyio-4.14.2-py3-none-any.whl` digest is
`9f505dda5ac9f0c8309b5e8bd445a8c2bf7246f3ce950121e45ea15bc41d1494`.
It retains the MIT licence and supports the bundled Python version. AnyIO is a
transitive dependency of the pinned SearXNG source: all 19 exact direct upstream
requirements and the pristine source archive remain unchanged. Added checks
verify the wheel and licence, reject a stale aggregate dependency-lock digest,
and validate the installed dependency inventory.

The 0.4.144-beta production pipeline completed successfully, including unfiltered
Core and WPF suites, authenticated verification receipt, guide/dependency-index/
XAML/migration checks, Inno Setup compilation and final compliance on **4,580
files**. The publisher independently rechecked the installer hash, both checksum
manifests and the final payload compliance gate.

- Source commit: `020da9ad9b818a4b0608c8da185f3de009c698c4`.
- Application source tree: `1d60e9ff7601eff70eddd2de5871e423c16dbae5`.
- Installer: `AI Arena Setup 0.4.144-beta.exe`, 111,556,189 bytes.
- Installer SHA-256: `98c359f280914c4ffb26906e7c4864337d119630ba6eeaf9697dcf1b235c6746`.
- Signing: unsigned under the existing Optional policy.

NuGet's current vulnerability check reported no known vulnerable packages in the
WPF project's resolved direct/transitive dependencies. Version-specific PyPI
metadata returned no known advisories for all 41 locked Python distributions
after the AnyIO update. These are dated feed checks, not proof that unknown
vulnerabilities are absent. Separate development-branch app fixes are excluded
from this release. Earlier audit scope and legal/provenance limitations continue
to apply.
