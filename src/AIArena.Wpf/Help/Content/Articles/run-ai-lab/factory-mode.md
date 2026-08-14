Factory mode is for inspecting model behavior without AI Arena - Lite's scenario, persona, relationship, memory, tool, or narrator guidance. Provider selection, model routing, response tone, sampling, timeout, output limit, and context configuration still apply.

## Enable Factory mode

Open Match Setup and turn **Apply Match Setup to models** off. The setup remains saved; it is simply inactive for participant prompts.

Before the first participant runs, send a non-empty **Public** Operator turn. That message becomes the stable conversation root. Private and Narrator-routed drafts do not qualify.

> [!WARNING] Factory mode never invents a starter prompt. 1 Turn, Auto Chat, and per-agent actions remain disabled until a valid public Operator root exists.

## Public group contract

Factory uses the `public_group_v1` contract:

1. The first eligible public Operator turn is anchored as the root.
2. Later requests reconstruct at most 50 whole public entries: the root plus the newest 49 eligible public Operator and successful participant turns.
3. Each participant's own earlier replies are sent as assistant history.
4. Peer replies and later Operator turns are attributed group input.
5. Adjacent logical entries that map to the same transport role are losslessly batched for strict chat templates.

The root, chronology, speaker attribution, retained text, and logical entry counts do not change. Older whole entries may be omitted at the fixed bound; included entries are never shortened. Omission evidence remains inspectable.

## What is excluded

Factory participant prompts omit:

- topic and global Match Setup rules;
- personas, voice styles, pressure, relationships, and private memory;
- Internet/tool instructions and tool rows;
- Narrator/repair guidance, reasoning metadata, and fallback prompts;
- System/error transcript rows and native conversation continuation.

System and error events remain visible in the app for diagnosis but do not enter model context. Narrate and Decision Card are unavailable because they inherently require Arena guidance.

## Context behavior

Factory ignores the model's Arena Strict/Rolling 80 history setting. If retained whole public entries exceed provider context, AI Arena - Lite surfaces the provider rejection and pauses Auto Chat. It does not summarize, trim, switch models, or add a synthetic prompt.

Switching Arena/Factory mode does not replace the anchored root. Resetting or starting/importing a clean session requires a new public Operator root. Forking preserves the routing policy and the applicable persisted state.

For fair model comparison, keep the same root and causal public history. Factory comparisons require the same privacy-safe causal-context profile; equal turn counts alone are insufficient.
