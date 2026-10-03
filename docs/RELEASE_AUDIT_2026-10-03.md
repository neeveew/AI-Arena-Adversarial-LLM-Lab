# AI Arena Lite publication audit — 3 October 2026

The original 0.4.142-beta installer is **not cleared for publication**. Its
packaged application contains local build paths and lacks the .NET dependency
notices now required by the repaired release pipeline. A fresh candidate payload
passes the new checks. The final installer must be rebuilt through that pipeline;
the candidate is verification evidence, not a replacement release.

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
