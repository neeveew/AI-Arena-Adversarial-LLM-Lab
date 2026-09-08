# AI Arena Lite: full feature overview

[Back to the front page](../README.md) · [Download for Windows](https://aiarena.me/downloads/) · [Step-by-step user guide](USER_GUIDE.md)

This page contains the detailed workspace descriptions, feature catalogue, and provider setup information. For a guided first run, start with the [Quick Start tutorial](USER_GUIDE.md#article-quick-start).

## Workspaces

AI Arena - Lite is not a chatbot and not just a model comparison board. It is a local multi-agent LLM lab where agents can debate, collaborate, converge, drift, overclaim, challenge assumptions, and be steered by an operator.

The left rail exposes three first-class workspaces by default:

- **AI Lab** runs structured adversarial matches with live agents, operator turns, diagnostics, performance inspection, and transcript tooling.
- **Agent** is a default-on software-creation workspace with a Codex-inspired centered thread, bottom composer, visible session Approve All control, compact Progress/Build Evidence/Outputs rail, workspace and advanced controls tucked into collapsed drawers, model collaboration, staged command proposals, a native .NET Solution Doctor for project-correct build/test/run guidance, deterministic C#/MSBuild/NuGet root-cause evidence, and approval-gated repair proposals with before/after verification, plus an opt-in Roslyn Impact Explorer for definitions, callers/callees, inheritance, dependency neighborhoods, likely affected files/features, and evidence-labelled focused tests. Auto Rescue, loop-guarded Auto Continue, generated artifact suggestions, terminal output capture, command history/replay, work briefs, and file-change receipts remain available. Its left-rail entry can be shown or hidden independently under **Settings -> Agent workspace**; it is not gated by Debug.
- **AI Collaborate** runs a classic collaborative AI chat where Alpha, Beta, Gamma, and Narrator work together across configurable rounds to produce a final answer.

**AI World** is a separate experimental 3D view for AI Lab. It is hidden and off by default and is available only through the Debug controls.

You create the cast, assign models, personas, voices, and pressure profiles, inject public operator turns, and let a separate narrator observe, summarize, or synthesize. The app includes discourse diagnostics for friction, consensus, role drift, unsupported claims, evidence pressure, and narrative heat.

It is built for local experimentation with model behavior, multi-agent debate, AI collaboration, red-team style reasoning, prompt/cast design, and AI discourse analysis.

## Contents

- [Why This Exists](#why-this-exists)
- [Key Features](#key-features)
- [Quick Start](#quick-start)
- [What Makes It Different](#what-makes-it-different)
- [Requirements](#requirements)
- [Provider Setup](#provider-setup)
- [Safety And Limitations](#safety-and-limitations)
- [Licence](#licence)

## Why This Exists

Most LLM tools are designed to produce a final answer. AI Arena - Lite is designed to observe the process.

The interesting part is often not the final response, but what happens before it: disagreement, role drift, narrative collapse, unsupported certainty, evidence grounding, consensus formation, and operator-induced correction.

AI Arena - Lite makes those dynamics visible. The friction strip, narrator layer, memory notes, timeline, and performance inspector help you watch agents form or resist consensus under pressure.

## Key Features

- AI Lab for structured adversarial multi-agent matches with Battle Review run scoring and review packets.
- Default-on Agent left-rail workspace for software tasks with Planner/Reviewer/Builder collaboration, persisted project folders, capped workspace profiling, an explicitly triggered Roslyn Impact Explorer with relative-only symbol/dependency evidence and honest focused-test recommendations, a native .NET Solution Doctor that distinguishes executable test harnesses from test-SDK projects, project-correct Build/Test/Run plans, separately approved Restore actions, deterministic project-cycle/framework/package findings, structured CS/MSB/NU/NETSDK diagnostics and test totals, command-proven package-downgrade and test-discovery findings, narrowed repair retries, a Codex-like centered conversation lane, bottom composer, visible Approve All autonomy, a plus-menu popup for prompt presets/session controls, collapsed Workspace/Advanced drawers for deeper tuning, compact Progress/Build Evidence/Outputs rail, command preview, approval-gated or workspace-session-auto-approved Terminal/PowerShell execution, generated artifact suggestions, Auto Rescue for prose-only app replies, tolerant fenced/XML/JSON command extraction for local-model replies, bounded Auto Continue loops with duplicate/no-change guards, risky-preview manual stops, command history/replay, copied work briefs, Stage Next/Repair/Retry and Stage Verify handoffs, stdout/stderr capture, command-source labels, app file-snippet materialization, outside-workspace blocking, and file-change receipts. Show or hide it independently in **Settings -> Agent workspace** without enabling Debug.
- Durable Agent runbooks with stable plan/review/build/approval/execute/verify steps, workspace-bound persistence, evidence-linked checkpoints, interruption-safe restart recovery, and PowerShell inspect/resume/checkpoint commands.
- AI Collaborate for collaborative multi-agent chat with final synthesis, Red Team mode, and per-run review packets.
- Context-aware top-rail export for AI Lab transcripts and AI Collaborate chats.
- Surface-aware top commands that hide transcript-only Search, Export, and View actions outside the workspaces they control.
- Match Setup returns to the workspace and keyboard focus that opened it; Escape closes it without clearing transcript filters.
- Top-rail **Models** combines searchable catalogs from running servers, with observed loaded models first and each model identified by its server. LM Studio and Ollama can contribute models at the same time; assignments and per-model settings retain the serving connection even when model names match. Provider inventories are bounded to 4 MiB and 1,024 source entries before the 256-row display projection; omitted entries are reported as Partial evidence. Supported native residency is rechecked every five seconds while Models is open. Selecting a model exposes its context window, Arena history policy, response tone, available **Load model** or **Unload model** action, and immediate routing assignments. Saving model behavior never changes routing or residency; a loaded LM Studio model offers a separate confirmed reload when its context must change.
- Narrow-window right rail opens as an overlay drawer so the main workspace keeps its usable width.
- Default-on, token-authenticated local PowerShell control plane with a normal Settings toggle, typed commands for navigation, secret-free portable Match Setup export/import, cast sizing/relationship patterns/generation/history/replay, searchable and safely mutable Settings, full-state current-match forks with parent lineage, saved-session and checkpoint recovery, provider/Agent/Collaborate state, Collaborate run-review/trace inspection, arena turn/narration/reset, Internet state/toggle/diagnostics, self-screenshots, exports, live events, and an authoritative post-command state snapshot on every response.
- Configurable AI Collaborate rounds for deeper team drafting, critique, and hardening passes.
- Persistent recent collaborations with quick filters, search tokens, run metadata, mode/health badges, fork, repeat prompt, copy, compare-to-open, and delete actions.
- Distinct Alpha, Beta, Gamma, and Narrator debate cards before the final answer.
- Alpha, Beta, Gamma, and Delta participant agents.
- Separate Narrator layer for observation and public narration.
- Public, private, and narrator Operator interventions with route-aware draft meters and receipt tooltips.
- Operator quick intervention chips for evidence checks, consensus breaking, private role resets, narrator judgments, repairs, scope gates, handoff notes, and next-step framing.
- Per-agent personas, model assignments, voice styles, pressure profiles, and absurd persona-mixer constraints.
- Optional debug voice/style cue chips and voice drift enforcement for constrained agents.
- Automatic discovery of running LM Studio, Ollama, llama.cpp, and other OpenAI-compatible servers. AI Arena selects supported chat and model-management APIs from server evidence, remembers the last session and configuration, and keeps custom addresses and tokens under **Advanced**.
- Provider-specific model load and unload controls appear when the server supports them. Residency changes require server confirmation; missing measurements remain unavailable. Model execution and device placement stay with the server you run.
- Optional [Native Services](NATIVE_SERVICES.md) for model management, inference, and diagnostic bundles through a separately running compatible AI Arena C++ app. Each app keeps its own writable data profile.
- Conservative completion retries: up to two retries require an eligible transient rejection, an explicitly configured end-to-end idempotency-key contract, the identical request and key, and same-authority response evidence. Accepted, partial, ambiguous, timed-out, or cancelled completions are never automatically replayed.
- AI Lab right-rail **Model Comparison & QA** for capturing a baseline, comparing another model against the same model-neutral Match Setup and, for Factory runs, the same privacy-safe public-group context fingerprint, copying exact secret-free replay JSON and aggregate-only evidence, and reporting runtime QA as ready, partial, or blocked with unavailable evidence called out explicitly.
- Wide Match Setup flyout for scenario framing, readiness badges, preset-gallery metadata, run-shape preview, pressure graph preview, copyable setup receipts, personas, locks, checkpoints, sessions, and operator controls. Its per-match **Apply Match Setup to models** toggle can enter Factory mode, where the latest eligible public Operator turn present when Factory first runs anchors a continuous public group conversation: each participant sees that root, its own earlier public replies, attributed peer replies, and later public Operator turns without receiving Match Setup behavior guidance. For strict chat templates, adjacent logical entries that map to the same role are losslessly batched into one transport block; chronology, attribution, retained text, and logical entry counts remain unchanged.
- Random Seed presets with category, best-use, risk, and exact preset-match metadata, plus role-pack, pressure, style, absurdity controls, AI Choice, Wild Seed, and replayable generation history.
- Scenario and cast locks for controlled regeneration.
- Local sessions, restore points, and scenario templates.
- Discourse diagnostics: friction, consensus, role drift, unsupported claims, evidence pressure, and narrative heat.
- Agent memory notes stored per session.
- Turn compare mode for side-by-side transcript inspection.
- Match quality timeline with click-to-filter.
- Battle Review packets with verdict, score, risk flags, speaker share, token/latency totals, Run Trace triage, review queues, copyable judge notes, trace packets, and operator nudges.
- Agent performance cards with detail popups.
- Optional Debug-gated AI World 3D arena with speaker-follow camera, live pulse telemetry, speaker gaze/focus cues, narrator identity props, lock/voice/activity cues, minimap, inspector, chat bubbles, and richer stage lighting.
- AI Lab transcript search, filters, compact mode, reasoning display, retry, delete, export scope preview, and Markdown export.
- AI Collaborate Markdown export with prompts, final answers, Run Reviews, memory notes, and team trace metadata.
- Relationship Matrix pressure graph with coverage, hotspots, mutual-pair insight, draft patterns, and copyable current setup JSON.
- Match Setup preflight separates blockers from warnings, shows a visible checklist, and badges provider, persona, narrator, matrix, lock, agent, and history state.
- First-class SearXNG-backed web search and exact readable-page fetching, with an in-app Test Internet diagnostic, bounded source enrichment, domain-aware ranking, untrusted-evidence isolation, and numbered citation context.
- Native Windows/WPF interface.

## Quick Start

1. Download and run the latest beta installer from the
   [GitHub releases page](https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases).
2. Start LM Studio, Ollama, your own `llama-server`, or another OpenAI-compatible server. You can run more than one at the same time.
3. Open **Models** in the AI Lab top rail. AI Arena discovers running local servers and combines their model catalogs automatically.
4. Select a model. If the server supports loading and reports it unloaded, choose **Load model** and wait for the server to confirm its state.
5. Under **Assign to**, turn on **Default** for Arena roles without an explicit model, or select individual agents and Narrator. Assignments save immediately and keep the model's serving connection; different agents can use different servers.
6. Open **Match Setup** to tune the scenario and cast, or create a setup with Random Seed, AI Choice, or Wild Seed.
7. Run **1 Turn** to check the configured match, then use **Auto Chat** to continue. Watch the transcript and adjust the scenario or send Operator turns as needed.
8. Open **Agent** for team software work inside a selected project folder with approved command execution, or **AI Collaborate** for a synthesized team answer.

Use **Settings → Provider connection → Find servers** to refresh discovery. For a remote server or an uncommon port, expand **Advanced**, enter its **Server address** and optional token, then choose **Connect to address**. The app detects the supported server APIs. It remembers your last session and configuration automatically.

## What Makes It Different

- challenge or reinforce each other;
- drift away from assigned roles;
- collapse into confident but unsupported narratives;
- converge on shared assumptions;
- respond to operator corrections;
- behave differently under different personas, models, or context windows.

## Requirements

- Windows x64.
- The self-contained installer includes the required .NET Desktop Runtime; no separate .NET installation is needed.
- LM Studio, Ollama, a user-run `llama-server`, or another OpenAI-compatible provider.
- Local models are optional depending on your provider setup.
- SearXNG is an optional bundled installer component for local web search.
- The optional [Native Services](NATIVE_SERVICES.md) feature requires a separately running compatible AI Arena C++ app and separate writable data profiles.

Model execution depends on the provider you connect to.

## Provider Setup

Start the model servers you want to use. AI Arena discovers local servers at startup and when you open **Models**, then selects the supported chat and model-management APIs automatically. LM Studio, Ollama, and other servers can serve models to the same arena at the same time.

Discovery checks the saved address and these standard local ports:

| Server | Typical address |
| --- | --- |
| LM Studio | `http://127.0.0.1:1234/v1` |
| Ollama | `http://127.0.0.1:11434/v1` |
| llama.cpp | `http://127.0.0.1:8080/v1` |
| Other compatible servers | `http://127.0.0.1:8000/v1` |

Server type is detected from API responses; the port alone does not determine it. Use **Find servers** under **Settings → Provider connection** to refresh discovery. For another address, open **Advanced**, enter the address and optional access token, then select **Connect to address**.

Choose models and assign them to agents in **Models**. Each assignment retains its server, and identical model names from different servers remain distinct. **Default** supplies the shared model for roles without explicit assignments. Your last session, connections, assignments, and per-model settings are remembered automatically.

Load and unload controls appear only when supported by the serving provider. For llama.cpp, chat uses `/v1/chat/completions`; model lifecycle controls require detected router support. Start and configure `llama-server` yourself. The provider owns model execution, device placement, and GPU offload.

If a provider is offline, AI Arena can still open sessions and display local data. Turns assigned to that provider need it to become reachable again.

## Safety And Limitations

- LLM outputs may be false, incomplete, or misleading.
- Discourse diagnostics are heuristic. They do not verify factual correctness.
- Internet/source use should be reviewed by the operator.
- Model behavior depends heavily on the provider, model, prompt, context window, and local hardware.
- This is a beta app for experimentation, not a correctness oracle.

## Licence

AI Arena - Lite is distributed under the Shareable No-Derivatives Software Licence 1.0.

You may share AI Arena - Lite freely in its original, unmodified form. You may use it privately. You may not distribute edited, modified, forked, patched, rebuilt, or derivative versions without written permission from Dominik Fiala.

For automation, source layout, builds, and packaging, see the [developer documentation](DEVELOPMENT.md).
