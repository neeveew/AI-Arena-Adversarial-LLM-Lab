# Unreleased

Changes after `0.4.144-beta`:

- Preserve saved turn, narrator, operator, decision-card, and recovery outcomes when activity logging, refresh, or speech fails afterward. Show a warning alongside the saved result instead of reporting a failed action or inviting duplicate sends.
- Serialize transcript pin and delete actions with other arena operations and shutdown. Record successful edits only after the snapshot is saved, and describe the actual saved pin state.
- Preserve whitespace and indentation in compatible provider multipart completions and streamed content parts.
- Handle multiple LM Studio message blocks consistently during streaming and completion, preserving their text and accepted partial output if the server fails.
- Keep empty model-catalog actions reachable by scrolling when a compact window leaves too little vertical space.
- Resolve conflicting legacy retry memories from retained evidence without rewriting their history. Preserve manual corrections, migrated summaries, unrelated facts, and records outside the bounded notes editor.
- Reject inherited test filters during release verification, and compare generated guide content consistently across LF and CRLF checkouts.

## Verification

The isolated application changes passed 383 Core tests and 629 WPF tests. Evidence
is in the attached development worktree under
`artifacts/bugfix-2026-10-03-next/`. The changes are integrated onto the current
primary source, retaining the security and packaging repairs from `0.4.144-beta`.
After the additional native message-boundary repair, all 384 Core tests passed
and the WPF test project built without warnings or errors; evidence is under
`artifacts/bugfix-2026-10-03-native-boundaries/`.

Integrated security fixtures, hosted Models/transcript previews, and isolated
full-window smoke evidence are under `artifacts/bugfix-2026-10-03-integration/`.
The window smoke covers Models and Arena at 960x640 and 1500x960 and verifies a
clean shutdown. After the compact empty-catalog adjustment, the responsive Models
check and WPF build passed and the four window captures were repeated. These
checks do not run real model completions or certify a release.

The published `0.4.144-beta` installer remains unchanged. Its versioned notes,
checksums, and authenticated verification receipts describe that exact release;
they do not certify subsequent source changes. A future installer requires a new
version and a fresh complete release pipeline.
