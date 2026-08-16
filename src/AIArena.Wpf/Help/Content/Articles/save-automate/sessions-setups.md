AI Arena - Lite stores different kinds of state for different jobs. Choose the smallest operation that preserves the evidence you need.

## Choose a save operation

| Operation | Preserves | Use it when |
|---|---|---|
| **Session** | Current durable setup and run state | Returning to normal work later |
| **Clean session** | Setup without transcript/live run | Starting a fresh comparison |
| **Fork current run** | Complete persisted current match in an independent child | Exploring another next turn without changing the parent |
| **Conversation Fork** | Cursor-scoped public history plus conservative provable projection | Branching from an older transcript point |
| **Restore point** | Restorable snapshot of the active run | Protecting state before risky edits or long Auto Chat |
| **Scenario template** | Reusable framing, cast, locks, participants, and routes | Repeating a scenario design |
| **Match Setup JSON** | Portable secret-free setup and routing policy | Reproducing configuration on another clean session |
| **Transcript export** | Readable messages and available delivery/source evidence | Review or sharing |

## Fork semantics

**Fork current run** is additive: it creates and selects a collision-free child, preserves transcript, narration, private notes, source metadata, attachments, provider configuration, generation history, locks, routing policy, and next-turn position, and leaves the parent unchanged. Transient thinking/error status is reset.

Experiment Lab's cursor-scoped Conversation Fork truncates later public history and projects private memory conservatively. When historical setup evidence is unavailable, it preserves the current replayable setup and reports that limitation rather than guessing.

Reset is not a substitute for a fork. Reset clears the active arena transcript/live turn state and requires a new Factory root.

## Trash and Undo

Deleting a saved session or restore point requires confirmation and moves it to local **Trash** instead of erasing it immediately. Saved State exposes **Undo** for the most recent successful move. Trash keeps up to 64 items for at most seven days; an expired, missing, invalid, or name-colliding item is not restored under a different identity.

After a committed move or Undo, Saved State refreshes its authoritative list. A failure to refresh the projection or append secondary activity evidence is reported as a warning and does not misdescribe an already committed move or restore as failed.

## Automatic safety restore points

Arena Reset, applying a scenario template, and restoring an existing restore point can replace the whole live snapshot. When a live snapshot would be superseded, AI Arena - Lite first commits an automatic safety restore point for its exact authoritative revision. The replacement proceeds only after that safety point is durable; if it cannot be created, the destructive mutation does not run.

The completion status identifies the automatic safety point and the operation it protects. A compatibility recovery with no prior live snapshot has no state to checkpoint and reports that limitation instead of inventing one.

## Match Setup v4 portability

`ai_arena.match_setup.v4` includes:

- scenario, tuning, cast, narrator, locks, and relationships;
- Default fallback enabled/disabled policy;
- every role's explicit/inherit assignment mode;
- per-model configured context, Arena history, tone, and custom tone;
- non-secret provider and Internet policy.

Import validates all fields before writing and creates a new clean session. V2/V3 imports receive documented legacy defaults. Tokens, URL credentials/queries/fragments, absolute paths, provider residency, transcript, and runtime private history are excluded.

## Local storage

Sessions, checkpoints, templates, configs, exports, logs, and caches remain stored in separate folders under the compatibility path `%LOCALAPPDATA%\AI Arena` unless `AI_ARENA_DATA_DIR` changes the data root. Uninstall data removal is a separate explicit choice.

Before a model evaluation, create separate baseline and candidate sessions. Keep the portable setup and causal input fixed; a session name or equal turn count alone does not prove comparability.
