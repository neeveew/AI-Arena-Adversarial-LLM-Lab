# Unreleased

Changes accumulated since the last published release, `0.4.134-beta`, are recorded
in `0.4.140-beta.txt` for the production installer. Local `0.4.138-beta` and
`0.4.139-beta` preview artifacts remain preserved with their original limitations.

## Pending

No additional product changes queued after the 0.4.140-beta release batch.

## Verification

Production build, fixture regression, dependency preflight, and packaged-app smoke
evidence is recorded under `artifacts/production-release/0.4.140-beta/`.
The standard installer pipeline writes authenticated harness and compiler receipts
under `artifacts/release-verification/` and validates the final release artifacts.

Historical development and local-preview verification remains under
`artifacts/settings-simplification/20260908/` and
`artifacts/installer/20260908-v0.4.139-beta/`. Those historical preview
results are not production release evidence. The 0.4.140-beta test fixtures isolate
synthetic keyboard state and popup resource initialization; the production
installer must pass the unchanged full verification gates.
