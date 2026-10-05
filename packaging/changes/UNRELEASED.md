# Unreleased

Changes after `0.4.144-beta`:

- Preserve saved turn, narrator, operator, decision-card, and recovery outcomes when activity logging, refresh, or speech fails afterward. Show a warning alongside the saved result instead of reporting a failed action or inviting duplicate sends.
- Serialize transcript pin and delete actions with other arena operations and shutdown. Record successful edits only after the snapshot is saved, and describe the actual saved pin state.
- Preserve whitespace and indentation in compatible provider multipart completions and streamed content parts.
- Handle multiple LM Studio message blocks consistently during streaming and completion, preserving their text and accepted partial output if the server fails.
- Keep empty model-catalog actions reachable by scrolling when a compact window leaves too little vertical space.
- Use each assigned model's saved server, adapter, credentials, context, and request options in AI Collaborate. Preserve distinct-server fallback even when both servers advertise the same model name, and recognize legacy assignments by their complete connection identity.
- Share provider request copying and response-tone handling across Arena, Agent, and AI Collaborate. Agent retains its saved shared-provider options and zero temperature; Collaborate applies the assigned model's saved tone.
- Share bounded reasoning-only recovery across Arena, Agent, and AI Collaborate. Workspace calls use fresh server capabilities, support one off/low reduction without changing saved preferences or prompts, and honor cancellation and terminal failure states.
- Fit workspace output allowance against the configured and observed loaded context. Reject prompts that cannot leave useful answer space before calling the model, preserving the original input and avoiding a fallback that would bypass the context failure.
- Preserve failed public partial answers in Agent and Collaborate output and trace history. Prevent fallback from replacing accepted partial output across app workflows, and reject late Agent streaming updates after their card is removed.
- Retain accepted Agent streaming text when Stop cancels the call or the provider throws. Mark it as a partial response, preserve the draft, and keep unfinished commands out of the approval flow. Freeze acceptance before cancellation so late provider output cannot change the retained result or revive Writing.
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

The subsequent workspace-routing repairs reproduced four failures before the
change and passed all 384 Core and 633 WPF tests afterward. The rebuilt app also
passed the isolated Models/Arena window smoke and clean shutdown. Evidence is
under `artifacts/bugfix-2026-10-05-routing/`.

The shared recovery and context-budget increment passed all 385 Core and 639 WPF
tests. Regression coverage includes asynchronous buffered/streaming recovery,
off/low capability handling, cancellation, terminal failures, retained public
partials, late streaming callbacks, and output fitting without input loss.
Startup, Models/Arena window captures, and clean shutdown passed in an isolated
profile. Evidence is under `artifacts/bugfix-2026-10-05-recovery/`.

The Agent stream-interruption repair passed all 387 Core and 641 WPF tests. Its
Stop matrix covers standard and virtualized panels, queued and visible deltas,
providers that honor or ignore cancellation, actual settings save/reload, draft
retention, and disabled partial command staging. The rendered partial cards were
reviewed in both layouts, and the rebuilt app passed isolated startup/shutdown
smoke. Evidence is under `artifacts/bugfix-2026-10-06-stream-stop/`.

The published `0.4.144-beta` installer remains unchanged. Its versioned notes,
checksums, and authenticated verification receipts describe that exact release;
they do not certify subsequent source changes. A future installer requires a new
version and a fresh complete release pipeline.
