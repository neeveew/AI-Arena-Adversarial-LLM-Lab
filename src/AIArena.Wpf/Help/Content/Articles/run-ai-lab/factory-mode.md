Factory mode is for inspecting model behavior without AI Arena - Lite's scenario, persona, relationship, memory, tool, or narrator guidance. Provider selection, model routing, response tone, sampling, timeout, output limit, and context configuration still apply.

## Enable Factory mode

Open Match Setup and turn **Apply Match Setup to models** off. The setup remains saved; it is simply inactive for participant prompts.

Before the first participant runs, the Operator composer appears in the center of the transcript. Write a non-empty **Public** message, then choose:

- **Send and start Auto Chat** to save the opening message and begin repeated participant turns when the run prerequisites are satisfied.
- **Send only** to save the message without starting the agents. You can then use **1 Turn** to inspect a single response.

That message becomes the stable conversation root, and the same Operator composer returns to the right rail. Private and Narrator-routed drafts do not qualify as a root.

When the first Public message is the only missing run prerequisite, selecting **Auto Chat** focuses this composer. Other blockers, including a provider or model problem or an unresolved context failure, still need to be resolved before a run can start.

> [!WARNING] Factory mode never invents a starter prompt. Participant turns require a valid public Operator root; focusing the composer does not send its draft.

## Public group contract

Factory uses the `public_group_v1` contract:

1. The first eligible public Operator turn is anchored as the root.
2. Later requests reconstruct at most 50 whole public entries: the root plus the newest 49 eligible public Operator and successful participant turns.
3. Each participant's own earlier replies are sent as assistant history.
4. Peer replies and later Operator turns are attributed group input.
5. Adjacent logical entries that map to the same transport role are losslessly batched for strict chat templates.

The root, chronology, speaker attribution, and retained text remain intact. Older whole entries may be omitted at the fixed bound or under the context policy described below; included entries are never shortened. Retained IDs, counts, and a causal fingerprint make omissions inspectable and keep retries tied to the original conversation.

## What is excluded

Factory participant prompts omit:

- topic and global Match Setup rules;
- personas, voice styles, pressure, relationships, and private memory;
- Internet/tool instructions and tool rows;
- Narrator/repair guidance, reasoning metadata, and fallback prompts;
- System/error transcript rows and native conversation continuation.

System and error events remain visible in the app for diagnosis but do not enter model context. Narrate and Decision Card are unavailable because they inherently require Arena guidance.

## Context behavior

When the native server reports the model's loaded context allocation, Factory budgets the public conversation and response together with room for token-estimation uncertainty. **Strict** preserves the root-plus-49 selection. **Rolling 80%** can remove older whole entries while preserving the root, latest Operator direction, and latest successful participant reply. The request's output allowance can be reduced to fit, without changing saved settings.

If required text cannot fit with room for an answer, Arena explains the context limit and pauses Auto Chat before generation. Increase the loaded context or shorten the input. If runtime context is unavailable, Factory retains its existing 50-entry bound and reports any provider rejection. It never summarizes retained text or adds a synthetic prompt. Manual retries preserve the original retained conversation even if the loaded context later changes.

A completed reasoning-only response may receive one capability-supported reduced-reasoning retry. This uses the same model and exact public conversation; it adds no Factory guidance.

Switching Arena/Factory mode does not replace the anchored root. Resetting or starting/importing a clean session requires a new public Operator root. Forking preserves the routing policy and the applicable persisted state.

For fair model comparison, keep the same root and causal public history. Factory comparisons require the same privacy-safe causal-context profile; equal turn counts alone are insufficient.
