# AI Arena User Guide

AI Arena is a native Windows app for running structured conversations between multiple local or OpenAI-compatible language models. The models can debate, collaborate, test ideas, inspect tradeoffs, or free-roam around a topic while you guide the match as the operator.

The app is designed for local experimentation with model behavior. You can create a scenario, tune the cast, assign models per participant, run one turn at a time, let the match continue automatically, inject operator turns, ask the narrator to summarize, curate internet context, inspect diagnostics, and save or export the transcript.

## Quick Start

1. Install AI Arena from the versioned setup file.
2. Start LM Studio or another OpenAI-compatible provider.
3. Open Settings, then Model Provider.
4. Set Provider base URL, usually `http://127.0.0.1:1234/v1` for LM Studio.
5. Pick a Default model from the dropdown, or type one manually.
6. Optionally assign different models to Alpha, Beta, Gamma, Delta, and Narrator.
7. Press Test Provider.
8. Open Custom Match and choose a Random Seed style/intensity, AI Choice, or YOLO.
9. Return to Transcript and run 1 TURN or AUTO CHAT.

## Main Concepts

- Alpha, Beta, Gamma, and Delta are the participant agents.
- The Narrator is separate from the agents. It can summarize, frame, or curate without becoming Alpha, Beta, Gamma, or Delta.
- The Operator is you. Operator messages are public instructions injected into the transcript.
- Each participant has its own persona and model assignment.
- Agents see public transcript context, but their private persona and memory notes are handled separately.
- Sessions are saved locally under your Windows user profile.

## App Layout

The left rail contains the app identity, navigation, session overview, and live agent status. The live agent cards show the current/waiting/thinking/idle state and can run individual agent turns.

The center area contains the active page:

- Transcript: the arena conversation, diagnostics, filters, timeline, memory notes, and compare tools.
- Custom Match: scenario preview, cast preview, locks, seed generation, and checkpoint/session tools.

The right rail contains:

- Arena Controls.
- Agent Performance.
- Operator Turn.

The top rail contains match status, provider status, current turn, turn count, export, search, View menu, theme picker, and Settings.

## Top Rail

- Match: current match style.
- Provider: online/offline provider state.
- Current turn: next scheduled participant.
- Turns: transcript turn count.
- Search icon: opens a draggable transcript search popup. Escape closes it.
- Export icon: exports the currently visible transcript messages to Markdown.
- View menu: toggles Compact transcript, Turn compare, Quality timeline, Memory notes, and Auto-scroll.
- Theme: changes the app palette.
- Gear icon: opens Settings.

## Transcript

The transcript renders newest-first. Each message card shows:

- Turn number.
- Speaker and role.
- Model name.
- Latency and generated token count.
- Context size.
- Public message text.
- Optional model reasoning.
- Actions such as copy, retry, compare, pin, and delete.

The transcript filter row can show or hide System, Agents, Narrator, and Operator messages. The turn dropdown filters by all turns or specific turn ranges.

### Search

Use the magnifying glass in the top rail. Search matches transcript text, speakers, models, and internet/source fields.

The popup is draggable by its top handle. The X button clears the search when text is present, or closes the popup when the search is empty.

### Export

Use the export icon in the top rail. Export writes a Markdown transcript containing turn metadata, speaker names, model names, token/context stats, latency, message text, reasoning, and internet metadata when available.

If filters or timeline selection are active, export follows the currently visible transcript scope.

### Compact Transcript

Open View, then enable Compact transcript. This reduces card spacing and body height for smaller screens or dense review sessions.

### Turn Compare

Open View, then enable Turn compare. The app shows a compare panel above the transcript.

Use Compare on transcript cards to select two turns. The compare panel shows token, context, and latency deltas, plus side-by-side content.

### Match Quality Timeline

Open View, then enable Quality timeline. The timeline scores recent match quality using the discourse diagnostics.

Click a timeline bar to filter the transcript to that turn. Click the selected bar again, or Clear, to remove the filter.

### Agent Memory Notes

Open View, then enable Memory notes. Memory notes are private per-agent notes stored in the session snapshot and used by model context windows.

The memory panel lets you refresh, edit, clear individual agent notes, or clear all notes. Notes can also update after successful agent turns.

## Discourse Diagnostics

The diagnostic strip appears above the transcript and tracks:

- Friction.
- Consensus.
- Role drift.
- Unsupported claims.
- Evidence pressure.
- Narrative heat.

These diagnostics are visual aids. They help you notice whether the match is too harmonious, too cold, evidence-starved, drifting from roles, or producing unsupported claims.

Diagnostic and telemetry work is designed to run only when the relevant visual panels are displayed.

## Arena Controls

- AUTO CHAT: runs repeated arena turns.
- 1 TURN: runs one scheduled participant turn.
- NARRATE: asks the narrator to add a public narrator turn.
- PAUSE: stops Auto Chat or a stoppable operation.
- RESET: clears the transcript/live turn state while preserving scenario, cast, settings, and checkpoints.
- NEWS: curates a relevant outside item when internet is enabled.

Auto Chat stops when you press Pause, when a provider error occurs, or when an internet approval request is waiting for your decision.

## Agent Performance

Agent Performance cards show each participant's activity:

- Status.
- Model.
- Turn count.
- Output tokens.
- Average latency.
- Context size.
- Activity bars.

Click a performance card to open a compact detail popup. The popup shows persona preview, memory count, recent turns, latency, context, tokens, failures, web usage, and activity bars.

## Operator Turn

Operator Turn injects your public message into the transcript. It does not advance the normal participant turn order.

Use this to clarify a topic, add constraints, correct the match, ask a question, or steer the agents without resetting the session.

## Custom Match

Custom Match controls the scenario and cast.

Scenario Preview includes:

- Topic.
- Global instruction.

Cast Preview includes:

- Alpha.
- Beta.
- Gamma.
- Delta.
- Narrator.

Each item can be edited and locked. Locked cards use a golden border and lock glyph. Locked content is preserved when generating new seeds.

### Random Seed

Random Seed generates a deterministic scenario and cast. Use the style picker to choose Auto, Balanced, Adversarial, Technical, Scientific, Research, Product, Safety, Philosophical, Legal, Creative, Red-team, or Incident. Use the intensity picker to choose Normal, Sharp, Spicy, or Chaos. Locked topic, global instruction, or cast members are preserved.

Intensity changes the pressure applied to the generated setup:

- Normal keeps the scenario practical and controlled.
- Sharp increases visible disagreement and assumption testing.
- Spicy adds hidden incentives, uncomfortable tradeoffs, and weaker evidence.
- Chaos adds partial information and unstable constraints that agents must stabilize before converging.

### AI Choice

AI Choice asks the configured model to generate a scenario and cast. Locked fields are preserved, and missing cast members are filled automatically.

### YOLO

YOLO generates a more experimental scenario seed. It can touch the topic, global instruction, cast personas, and the way the simulation is described to the models, while respecting locks. The narrator is not treated as a normal YOLO cast target.

### Seed Inspector

The seed inspector shows the source, scenario seed, scenario style, intensity when available, persona seed, and persona style. Use it to understand what kind of generated setup you are running.

## Sessions, Checkpoints, and Templates

Sessions are saved locally under `%LOCALAPPDATA%\AI Arena\sessions`. Use the session controls to create, switch, or delete sessions.

Restore points save the current transcript, cast, locks, provider settings, and arena state under `%LOCALAPPDATA%\AI Arena\checkpoints`. Use them before risky edits or long Auto Chat runs.

Scenario templates save match framing, cast, locks, participants, and model assignments for reuse under `%LOCALAPPDATA%\AI Arena\templates`.

App settings are saved under `%LOCALAPPDATA%\AI Arena\configs`. Exports, logs, and cache files have their own folders under the same AI Arena data root.

## Settings

Settings are grouped into collapsible sections.

### Model Provider

- Provider base URL: OpenAI-compatible endpoint.
- Default model: model applied when selected.
- Participant model dropdowns: assign Alpha, Beta, Gamma, Delta, and Narrator individually.
- Manual entry: type a model name if it is not advertised by the provider.
- Preload selected models: asks the provider to load selected models when supported.
- Timeout: maximum provider wait time.
- Temperature: generation randomness.
- Max output: generated token limit.
- Test Provider: sends a small test request.

The app refreshes advertised provider models while Settings is open.

### Auto Chat

Controls Auto Chat cadence and related run behavior.

### Visuals

Controls visual theme behavior, avatars, top strip mode, and related shell preferences.

### News / Internet

- Use internet: enables internet tooling.
- Mode: controls manual or model-requested internet behavior.
- Source scope: trusted sources or open web.
- Requester permissions: participants and narrator can be allowed or blocked.
- Require approval: pauses model-requested internet as approval cards.
- Max results: limits internet result count.

When approval is required, the app shows an Internet Approval card with approve/reject actions.

### Context Windows

Controls transcript, private, and memory note windows used by prompt construction. The current context summary helps confirm what will be sent to models.

### Help / About

Shows the app description, author, code credit, licence summary, and copyright notice.

## Licensing

AI Arena is distributed under the Shareable No-Derivatives Software Licence 1.0.

Copyright (c) 2026 Dominik Fiala.

You may share AI Arena freely in its original, unmodified form. You may use it privately. You may not distribute edited, modified, forked, patched, rebuilt, or derivative versions without written permission from Dominik Fiala.

The installer shows the licence during setup and installs the app to `%LOCALAPPDATA%\AI Arena` by default, which avoids the normal administrator prompt for clean per-user installs. You can still choose a different install directory during setup. `LICENSE`, `NOTICE.md`, release notes, the release manifest, and this user guide are installed beside the app. The Start Menu folder includes shortcuts for the app, user guide, release notes, and GitHub releases. Saved settings, sessions, checkpoints, templates, exports, logs, and cache files use named folders under `%LOCALAPPDATA%\AI Arena` unless you choose to delete leftover data during uninstall.

## Provider Troubleshooting

If LM Studio or another provider is closed, the app may show a provider unreachable message.

Check:

- LM Studio server is running.
- A model is loaded.
- The base URL and port are correct.
- The provider exposes an OpenAI-compatible `/v1` API.
- The chosen model name exactly matches the provider model list.

If a model times out:

- Use a smaller model.
- Reduce context window sizes.
- Lower max output.
- Increase timeout.
- Stop Auto Chat and test with 1 TURN.

If GPU telemetry is unavailable, the app can still run. Model execution depends on your provider, not on AI Arena being tied to a specific GPU vendor.

## Practical Tips

- Start with 1 TURN after changing models or settings.
- Use Auto Chat once the cast behaves correctly.
- Lock scenario or cast fields before trying Random Seed, AI Choice, or YOLO.
- Keep internet approval enabled while testing model-requested browsing.
- Use Memory notes to preserve durable agent-specific facts.
- Use Quality timeline to spot drift or evidence weakness.
- Use Agent Performance popups to inspect slow or noisy participants.
- Save a restore point before a long run.
