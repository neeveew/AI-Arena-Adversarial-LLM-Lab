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

## Reuse collaboration history

Recent Collaborations can reopen, search, filter, fork, repeat, compare, export, copy, or delete local chats. **Fork** retains exchanges as context but saves the next reply as a new collaboration. **Repeat prompt** stages the latest prompt in a clean draft while retaining intended notes. **New** starts blank without deleting history.

Search covers prompts, answers, traces, notes, model mix, health, mode, metrics, and review text. Export produces Markdown with the final answer, review packet, and trace metadata.

History remains local under the compatibility path `%LOCALAPPDATA%\AI Arena\configs\collaborate-history.json`. Provider requests still send the prompt and selected context to the configured provider; local storage does not make a remote provider private.
