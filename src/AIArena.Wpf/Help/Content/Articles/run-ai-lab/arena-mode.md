Arena mode applies the saved Match Setup to every participant request. Use it for debates, structured review, scenario testing, persona interaction, narrated synthesis, and tool-assisted research.

## What a participant receives

Depending on the active setup and role, an Arena request can include the topic, global rules, persona, voice and pressure contracts, relationship guidance, eligible public transcript, private memory for that participant, Internet/tool instructions, response tone, and generation settings.

Agent roles see public transcript context but do not see another participant's private memory. Narrator is a separate route and does not become Alpha, Beta, Gamma, or Delta.

## Workspace layout

- **Top rail:** match/provider/current-turn state, Match Setup, Models, transcript Search/Export, View, Help, and Settings.
- **Left rail:** workspace navigation, session details, and live agents.
- **Center:** newest-first transcript and optional diagnostics/review tools.
- **Right rail:** Arena Controls, Universal Status Center, Operator Turn, Agent Performance, then Model Comparison & QA.

At narrow widths the right rail opens as a drawer instead of shrinking the transcript. The same top-rail control closes it.

## Run deliberately

1. Check Match Setup readiness and routing.
2. Select **1 Turn** to run the next scheduled participant.
3. Inspect the resulting transcript card and Status Center entry.
4. Use **Auto Chat** only after one turn behaves as expected.
5. Select **Pause** to stop repeated turns.

The transcript card keeps actions such as Copy, Speak, Pin, Retry, and Delete visible. Delivery detail may include model, latency, time to first token, tokens, throughput, status, voice, and provider response ID. Unavailable measurements are omitted rather than shown as false zeros.

## Search, review, and export

Top-rail Search matches transcript text, speakers, models, and source fields. Export writes the current visible scope to Markdown and includes available message and delivery evidence.

The View menu controls compact transcript, turn comparison, quality timeline, Battle Review, memory notes, and auto-scroll. These are review surfaces: changing a view does not mutate provider routing or prompt history.

## No silent changes

AI Arena - Lite does not silently switch models, shorten an included history entry, invent a summary, retry an accepted streaming request, or convert missing telemetry to zero. A typed failure card pauses the relevant run and offers only recovery actions that preserve the causal record.

If a match is ended, Auto Chat, 1 Turn, Narrate, and per-agent run actions remain disabled until Reset or Fork establishes a new runnable state.
