# Unreleased

The bundled dependency security update since `0.4.143-beta` is recorded in
`0.4.144-beta.txt` for the corrected installer and publisher handoff. It retains
the audited packaging from `0.4.143-beta` and application stability improvements
from `0.4.142-beta`. Earlier artifacts retain their original verification evidence.

## Verification

Development regression evidence for the retained application changes is recorded
under `artifacts/bugfix-2026-10-03/`. Packaging audit evidence is recorded under
`artifacts/release-audit-20261003/`. The AnyIO security fixture and payload smoke
evidence are under `artifacts/security-update-0.4.144/`. The standard installer
pipeline runs the complete Core and WPF harnesses, records authenticated verification and compiler
receipts under `artifacts/release-verification/`, and checks the finished release
and installer artifacts. Packaging logs for this release are under
`artifacts/production-release/0.4.144-beta/`.
