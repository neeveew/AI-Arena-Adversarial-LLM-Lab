Arena Controls run the match. Operator Turn changes or supplements its public and private causal input.

## Arena Controls

| Control | Result |
|---|---|
| **Auto Chat** | Repeats scheduled participant turns until paused, ended, or blocked |
| **1 Turn** | Runs one scheduled participant turn |
| **Narrate** | Adds a public Narrator turn in Arena mode |
| **Speak** | Reads eligible visible content using configured speech behavior |
| **Pause** | Requests the current repeatable/stoppable operation to stop |
| **Reset** | Clears transcript/live turn state while preserving setup, routing policy, and saved configuration |

Disabled controls expose their current prerequisite in accessible help. Common blockers are provider offline, scheduled role unassigned, match busy/ended, or an unresolved context failure. In Factory mode, when only the first Public message is missing, **Auto Chat** focuses the opening composer instead of starting a run.

Voice playback is bound to the active session. Loading a genuinely different session stops current or still-starting speech before that session is read and rendered; a same-session background refresh does not interrupt playback.

## Live responses

Agent and Narrator turns appear immediately and fill in as the server sends their answer. The same card becomes the saved transcript entry when the response finishes. The card starts with **Waiting for response…**. When the server reports its activity, it shows **Loading model…**, **Reading context…**, or **Thinking…**; **Writing…** appears when public answer text arrives. Internal tool requests and reasoning stay separate from the public answer. Servers that do not report a stage leave the waiting label in place.

Stopping or interrupting a response leaves any received text labeled as an unsaved partial response. It is not passed to the next agent as a completed turn. Starting another response replaces that preview. A server that does not stream still returns its answer in the same waiting card.

If a completed response contains reasoning but no public answer, Arena can make one **Retrying with reduced reasoning…** attempt when the server advertises a supported setting. It uses the same model and conversation and saves only the final turn. A second empty result is explained instead of repeatedly retrying.

You can scroll through earlier messages during generation without token updates pulling you away. **Follow Chat** follows new cards when you are already at the top of the transcript.

## Operator routes

- **Public:** writes an attributed Operator message to the shared transcript. It does not advance normal participant order.
- **Private:** writes guidance only to the selected participants' private memory path.
- **Narrator:** asks Narrator for a public referee/synthesis answer in Arena mode.

The draft meter shows characters, estimated tokens, and destination. Quick intervention chips stage editable suggestions from current diagnostics and error state; selecting a chip may change the route when its purpose requires private correction or Narrator judgment.

Unsent Operator text is protected for the current Windows user and restored independently for each durable session incarnation and Public, Private, or Narrator route. A successful send clears only the exact unchanged text it sent, so a failed request or an edit made while sending remains available.

## Factory rules

Only a non-empty Public Operator turn can establish Factory's stable group root. Before that message exists, the Operator composer appears in the center of the transcript. **Send and start Auto Chat** saves the Public message first, then starts the agents if the same session is still ready. **Send only** saves it without starting a run. The composer then returns to the right rail with the usual Operator controls.

Selecting **Auto Chat** focuses the opening composer when that message is the only missing prerequisite. It never sends an unsent draft automatically. Failed sends retain the draft; a session or mode change during sending prevents the combined action from starting Auto Chat in a different context.

Narrator routing is unavailable in Factory mode. After the root exists, later Public Operator turns join group history but do not silently replace the root.

## Reset, fork, skip, or end

- **Skip turn** records an intentional scheduler decision after a blocked participant.
- **Reset** first creates a safety restore point when live state would be replaced, then starts the active session's arena state over and requires a new Factory root.
- **Fork** preserves an independent branch for a different recovery or next turn.
- **End match** durably disables run actions until reset or fork.

Use a fork when evidence from the current run must remain intact. Use reset only when clearing the active run is the intended operation.
