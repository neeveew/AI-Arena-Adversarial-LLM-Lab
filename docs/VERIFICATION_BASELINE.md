# Experimentation platform verification baseline

This baseline fixes the starting point for the Experimentation & Verification Platform batch. It is evidence for comparison, not a release seal.

## Source identity

- Product commit: `c37200bdc31c21d9552fe2f983b6c73d57bbbf85`
- Map commit: `91baabf`
- Product version: `0.4.127-beta` (unchanged)
- Captured: 2026-08-08
- Release, installer, tag, publication, and workflow surfaces: unchanged

## Required baseline gates

| Gate | Baseline result |
| --- | --- |
| Release solution build | Pass; six projects, zero warnings and zero errors |
| Core regression harness | Pass |
| WPF regression harness | Pass |
| Code-intelligence harness | Pass; 11/11 |
| Focused llama.cpp and evaluation QA | Pass; 21 tests |
| Dependency index freshness | Pass |
| XAML hard-coded-value fixtures | Pass |
| XAML inventory freshness | Pass; 484 findings |
| Release-security fixtures | Pass |
| Runtime-bundling policy | Pass; llama.cpp is not bundled |
| Release-app smoke launch | Pass |

The optional live llama.cpp readiness probe was unavailable because no endpoint was supplied. The baseline does not reinterpret that absence as a pass or as runtime coverage.

## Render evidence

The Release build was launched with no existing AI Arena process, driven through the local control plane, captured, and then the started process was stopped.

- Image dimensions: 2250 × 1290
- PNG size: 162,319 bytes
- SHA-256: `2601850236fc19b2f69ec96e70a92a8bf8f25a5a72df72b6ac08144e8768ae64`
- Inspection result: Dark Blue shell, setup-needed state, left navigation, transcript canvas, provider controls, performance panel, and Model Comparison & QA panel rendered without a visible layout fault.

The temporary screenshot path is intentionally not persisted because verification evidence must not expose machine-specific absolute paths. Future QA evidence records may retain the hash and a sanitized artifact-relative identifier.

## Evidence rules carried forward

- Observed results, inferred results, and unavailable evidence must remain distinct.
- A missing provider, history source, metric, screenshot, automation property, or live probe must never be converted to a success value.
- Evidence may not contain credentials, tokens, full prompts, transcript content, source content, or machine-specific absolute paths.
- Every final Quality Seal must identify its exact commit and tree fingerprint, validate its schemas, report all required gate outcomes, and be invalidated by a source-tree mismatch.
