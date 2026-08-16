AI Collaborate asks a small role team to work on one prompt and then presents a labelled Final Answer, Run Review, and collapsible Team Debate.

## Choose a mode

- **Fast:** Narrator answers directly in one round.
- **Team Draft:** Alpha, Beta, and Gamma draft before Narrator synthesis.
- **Critique:** Alpha drafts, Beta critiques, Gamma refines, Narrator synthesizes.
- **Red Team:** proposal, attack, hardening, then synthesis.

Team modes support a bounded round count. Later rounds focus on refinement rather than repeating the initial answer.

## Check routing before sending

Each role uses its explicit assignment when one exists. An unassigned role may use the shared model only when **Default for unassigned agents** is enabled. With fallback off, every participating role—including Narrator—must have an explicit route or the run remains blocked.

Turning the fallback policy off does not clear the shared provider model used by Agent, diagnostics, or connection testing.

## Inspect what will be sent

Use **Receipt** beside the prompt to preview the run plan, prompt size, prior chat count, added context, and whether a review packet is expected. The prompt budget warns when added context will be truncated before a provider call.

Run Review reports trace health, issues, tokens, latency, model mix, payload size, outcome, and next action. Team Debate keeps role contributions grouped by round so they remain distinct from Final Answer.

## Add bounded document context

**Add documents** accepts `.txt`, `.md`, `.markdown`, `.csv`, `.json`, and `.log` text. AI Collaborate processes at most 32 candidates from one selection, retains at most five documents and 6,000 characters from each, and bounds the combined document, calculation, and memory tool context to 12,000 characters. UTF-8, UTF-16, and UTF-32 byte-order marks are handled explicitly. Selecting the same normalized path again updates that document instead of creating another copy.

Import reads asynchronously and can be cancelled. Documents already imported remain available, while the current incomplete file is not added. Mixed selections keep valid documents and report skipped files with bounded, path-free guidance. Imported text is reference data rather than instructions unless the latest request explicitly says otherwise.

## Read and draft without losing context

The conversation follows live additions only while the view is near the newest message. If you scroll back, it preserves the historical position and text selection, counts unseen messages, and exposes an accessible **Jump to latest** action.

Unsent prompt text is protected for the current Windows user and scoped to the active session incarnation, collaboration, and mode. A restart or return to that scope restores it. Failure, cancellation, or an edit made while a request is running does not clear the newer text; only the exact unchanged draft associated with a successful answer and history save is removed. Control-plane sends preserve the visible draft.

## Reuse collaboration history

Recent Collaborations can reopen, search, filter, fork, repeat, compare, export, copy, or delete local chats. **Fork** retains exchanges as context but saves the next reply as a new collaboration. **Repeat prompt** stages the latest prompt in a clean draft while retaining intended notes. **New** starts blank without deleting history.

Search covers prompts, answers, traces, notes, model mix, health, mode, metrics, and review text. Export produces Markdown with the final answer, review packet, and trace metadata.

History remains local under the compatibility path `%LOCALAPPDATA%\AI Arena\configs\collaborate-history.json`. Provider requests still send the prompt and selected context to the configured provider; local storage does not make a remote provider private.
