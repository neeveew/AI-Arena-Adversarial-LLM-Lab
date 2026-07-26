# Agentic App Research Notes

Updated: 2026-07-15

This note captures outside product patterns worth borrowing for AI Arena. It is intentionally implementation-facing: each idea should map to a concrete UI or workflow improvement.

## 2026-07-14 Research Decision

Current primary-source guidance converges on a durable, inspectable run model rather than unconstrained autonomy:

- OpenAI Agents SDK human-in-the-loop supports serializing run state, pausing on sensitive tool calls, approving or rejecting them, and resuming the original run. Sessions preserve history across those boundaries: https://openai.github.io/openai-agents-python/human_in_the_loop/
- OpenAI tracing treats a workflow as an end-to-end trace with nested spans for agents, model generations, tools, guardrails, and handoffs: https://openai.github.io/openai-agents-python/tracing/
- LangGraph persistence saves checkpoints per step to enable human review, memory, replay/time travel, and fault recovery: https://docs.langchain.com/oss/python/langgraph/persistence
- Anthropic's current architecture guidance recommends matching orchestration complexity to business value and choosing deliberately among sequential, parallel, and evaluator-optimizer patterns: https://resources.anthropic.com/building-effective-ai-agents
- User demand is visible in OpenHands' Ask/Plan mode request, where the desired handoff preserves the planning conversation and a durable plan before execution: https://github.com/All-Hands-AI/OpenHands/issues/10433
- Real failure reports show why checkpoint correctness matters: canceled streamed state can disappear before the next checkpoint, and nested approval workflows can lose intermediate results across resume boundaries: https://github.com/langchain-ai/langgraph/issues/5672 and https://github.com/langchain-ai/langgraph/issues/6792

Decision for AI Arena:

1. Build a visible runbook/task graph over the existing Agent stages, command approvals, work briefs, receipts, and checkpoints.
2. Give every step a stable ID, owner, status, dependency list, evidence/receipt link, and resume state.
3. Preserve a deterministic event/decision trace across stop, approval, restart, and retry boundaries.
4. Expose the same runbook operations through the local control plane so the UI and PowerShell share one command contract.
5. Keep multi-agent orchestration explicit and preset-driven; do not add opaque recursive autonomy merely to appear more agentic.

The first prerequisite shipped in the post-0.4.94 worktree: a self-describing control-plane capability catalog plus typed PowerShell controls for one-turn/narration/reset, transcript presets, right-rail state, and Internet state/toggle/diagnostics.

The next implementation applies the decision directly: Agent now maintains a workspace-bound durable runbook with stable plan/review/build/approval/execute/verify IDs, owners, dependency links, evidence, bounded checkpoints, restart interruption recovery, and PowerShell state/resume/checkpoint parity. Resume stages work for review instead of replaying a previously running step automatically.

## Sources Reviewed

- AutoGen Studio: https://microsoft.github.io/autogen/dev/user-guide/autogenstudio-user-guide/index.html
- AutoGen Studio 0.2 usage: https://microsoft.github.io/autogen/0.2/docs/autogen-studio/usage/
- Chatbot Arena / LMSYS: https://www.lmsys.org/blog/2023-05-03-arena/
- LMArena / Arena AI: https://arena.ai/
- CrewAI docs: https://docs.crewai.com/
- CrewAI observability: https://docs.crewai.com/en/observability/overview
- LangGraph overview: https://www.langchain.com/langgraph
- LangGraph Studio: https://www.langchain.com/blog/langgraph-studio-the-first-agent-ide
- LangGraph interrupts: https://docs.langchain.com/oss/python/langgraph/interrupts
- OpenAI Agents SDK guide: https://developers.openai.com/api/docs/guides/agents
- OpenAI agent evals: https://developers.openai.com/api/docs/guides/agent-evals
- OpenAI Agents SDK tracing: https://openai.github.io/openai-agents-python/tracing/
- LM Studio model download: https://lmstudio.ai/docs/app/basics/download-model
- LM Studio per-model defaults: https://lmstudio.ai/docs/app/advanced/per-model
- Ollama API: https://docs.ollama.com/api/introduction
- Ollama pull API: https://docs.ollama.com/api/pull
- OpenRouter model metadata: https://openrouter.ai/docs/guides/overview/models
- WPF 3D graphics overview: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/3-d-graphics-overview
- Social VR attention guidance: https://www.cs.umd.edu/~gsunlee/assets/pdf/2024MayISpeak.pdf
- Google voice/conversation focus principles: https://design.google/library/speaking-the-same-language-vui
- AWS Agentic Value Accelerator: https://github.com/aws-samples/sample-agentic-value-accelerator
- LLM Agent X Mission Control: https://github.com/llm-agent-x/llm-agent-x
- MCP Agent Builder Go: https://github.com/manishiitg/mcp-agent-builder-go
- MetaboCommand: https://github.com/zan-maker/metabocommand
- Dive into Claude Code: https://github.com/VILA-Lab/Dive-into-Claude-Code
- Crosslink: https://github.com/forecast-bio/crosslink
- Langflow: https://github.com/langflow-ai/langflow
- Flowise: https://github.com/FlowiseAI/Flowise
- Dify: https://github.com/langgenius/dify
- Awesome n8n Templates: https://github.com/enescingoz/awesome-n8n-templates
- Agent Composer Templates: https://github.com/ContextualAI/agent-composer
- Dynatrace AI agent instrumentation examples: https://github.com/dynatrace-oss/dynatrace-ai-agent-instrumentation-examples
- DeepEval / Confident AI: https://github.com/confident-ai/deepeval
- Agent Replay: https://github.com/agentreplay/agentreplay
- Langfuse: https://github.com/langfuse/langfuse
- SAP Agent Quality Inspect: https://github.com/SAP/agent-quality-inspect
- TraceRoot: https://github.com/traceroot-ai/traceroot
- Pydantic AI message history: https://pydantic.dev/docs/ai/core-concepts/message-history/
- LangChain production deep agents runtime: https://www.langchain.com/blog/runtime-behind-production-deep-agents
- AgentLens: https://github.com/dreadnode/agent-lens
- Taskade agentic workspaces: https://www.taskade.com/blog/agentic-workspaces
- OpenAI Codex CLI: https://developers.openai.com/codex/cli
- OpenAI Codex CLI features: https://developers.openai.com/codex/cli/features
- OpenAI Codex IDE quickstart: https://developers.openai.com/codex/quickstart
- Claude Code overview: https://docs.anthropic.com/en/docs/claude-code/overview
- Claude Code quickstart: https://docs.anthropic.com/en/docs/claude-code/quickstart
- Claude Code security: https://docs.anthropic.com/en/docs/claude-code/security
- Claude Code permissions: https://code.claude.com/docs/en/permissions
- Claude Code hooks: https://code.claude.com/docs/en/hooks
- Devin introduction: https://docs.devin.ai/get-started/devin-intro
- Devin session tools: https://docs.devin.ai/work-with-devin/devin-session-tools

## Borrowable Patterns

1. Visual team/workflow builder
   - AutoGen Studio emphasizes building teams, agents, tools, models, and stop conditions through visual/declarative controls.
   - AI Arena fit: extend Match Setup into a compact "team graph" view showing agents, narrator, relationship pressure, allowed tools, and termination rules.

2. Playground run control
   - Agent playgrounds make pause, stop, replay, and message flow visible during a run.
   - AI Arena fit: keep investing in replay/new-run history, add a run timeline with pause points, and expose "why this agent spoke next" metadata.

3. Roles, tasks, and process modes
   - CrewAI centers roles, tasks, and sequential/hierarchical/hybrid processes.
   - AI Arena fit: add Match Setup process presets such as tribunal, incident command, red-team gauntlet, consensus trap, and chaos lab with explicit role/task cards.

4. Human-in-the-loop checkpoints
   - OpenAI Agents SDK and CrewAI both treat human review as a first-class workflow control.
   - AI Arena fit: add operator checkpoint cards that can pause before tool use, final verdicts, model fallback, or high-risk consensus.

5. Observability and traces
   - Agent systems increasingly expose traces across model calls, tool calls, handoffs, guardrails, and audio.
   - AI Arena fit: upgrade existing telemetry into a turn trace inspector showing prompt window, selected model, latency, tokens, tool calls, narrator/TTS status, and fallback path.

6. Memory and knowledge surfaces
   - CrewAI and LangGraph-style apps treat memory/state as visible workflow ingredients.
   - AI Arena fit: make private notes, pinned notes, narrator context, and match memory inspectable from Match Setup and transcript side panels.

7. Exportable/replayable configurations
   - AutoGen Studio exports teams and configurations; CrewAI supports code-first and visual paths.
   - AI Arena fit: export a match setup as JSON/Markdown, import shared arena presets, and show a diff between current match and generated history.

8. Voice-first agent workflow
   - OpenAI Agents SDK explicitly treats voice workflows and speech traces as part of agent apps.
   - AI Arena fit: start with local narrator TTS, then add "speak selected card," per-role voice styles, and a speech activity indicator in the 3D arena.

9. Blind battle and benchmark modes
   - Arena-style tools make comparison engaging by anonymizing competitors, collecting preferences, and revealing identities after a decision.
   - AI Arena fit: add benchmark-oriented presets and role packs that keep rubrics, judging criteria, preference bias, latency/cost, and tie-breaks visible.

10. Checkpoint and interrupt language
   - LangGraph treats pause/resume checkpoints as durable workflow state rather than ad-hoc UI pauses.
   - AI Arena fit: make operator checkpoints, replay forks, and "new run from setup" feel like explicit workflow controls with visible state.

11. Trace and observability receipts
   - Agent tooling increasingly exposes model calls, tool calls, handoffs, retries, guardrails, cost, latency, and quality checks.
   - AI Arena fit: keep expanding diagnostics into trace receipts, run constraints, and setup readiness rather than hiding quality signals after a run.

12. Diegetic attention guidance in 3D conversation spaces
   - Social VR and conversational UI patterns make "who has the floor?" visible through gaze, highlighting, spatial focus, and concise status.
   - AI Arena fit: make AI World agents look toward the active speaker, strengthen speaker floor/halo cues, and add world status that names the speaker and listeners.

13. Native 3D scenes with simple, readable geometry
   - WPF Viewport3D is a practical native scene surface when geometry stays bounded and materials/meshes are reused.
   - AI Arena fit: invest in frozen/reused box-based scenery, lighting contrast, camera smoothing, and overlays before introducing heavier model pipelines.

## Applied In 0.4.18-beta

- Added Match Setup readiness and generation recipe summaries so configuration quality is visible before the run starts.
- Added benchmark, governance, and tool-ops role packs inspired by arena comparison, oversight, and observability workflows.
- Added relationship matrix draft patterns and new stances for fact-checking, amplification, de-escalation, and devil's advocate pressure.
- Added Setup Profile and Run Constraints preview cards to make seed, role pack, locks, relationship pressure, and replay history scan-friendly.
- Expanded local scenario pools with blind battle, checkpoint, trace, tool, governance, model-routing, and evaluator ideas.

## Applied In 0.4.19-beta

- Added JSON setup receipts for generated matches, borrowing from exportable team/configuration patterns in AutoGen Studio and CrewAI.
- Added lock-aware replay warnings so checkpoint and fork behavior is visible before mutating the current match.
- Expanded Recent history into a richer replay/export surface with cast previews, global rules, narrator briefs, newest-first ordering, and invalid-entry filtering.
- Made replay-to-new-run cleaner by clearing stale side-panel artifacts such as decision cards, attachments, and research items.
- Hardened packaged visual assets with embedded-resource fallbacks so guide and installer surfaces load consistently.

## Applied In 0.4.20-beta

- Prioritized AI World polish: richer 3D stage lighting, central console geometry, corner beacons, rails, and a stronger active-speaker floor ring.
- Added listener gaze so non-speaking robots turn toward the active speaker while the speaker presents toward the arena focus.
- Added AI World status copy that names the current speaker and summarizes listener focus.
- Smoothed same-session speaker camera handoffs while preserving snap-to-focus for first load, empty state, and session changes.
- Added Recent filtering, Copy Diff, and Rubric packets as a lightweight bridge toward importable setup specs and eval-first match review.

## Applied In 0.4.21-beta

- Added AI World lock, voice-style, and pressure-profile cues so match configuration is visible inside the 3D arena rather than only in setup forms.
- Added a distinct narrator booth treatment to make the narrator read as a separate ringside presence instead of another generic participant.
- Added richer activity props for tool use, internet sources, and error states, turning transcript metadata into readable in-world objects.
- Added speaker and turn headers inside speech bubbles so active dialogue scans better during follow-camera movement.
- Expanded inspector and event-chip detail for narrator identity, locks, voice cues, and pressure cues, guided by speaker-focus and conversational UI research.

## Applied In 0.4.22-beta

- Rotated from AI World to AI Collaborate and added Red Team mode, inspired by human-review and adversarial agent workflow patterns.
- Added Run Review packets after final answers, borrowing trace receipt ideas from agent observability tools: verdict, issue count, tokens, latency, models, payload size, outcome, and next action.
- Added copy and follow-up actions for Run Review so collaboration traces become actionable review artifacts instead of passive logs.
- Expanded Context Receipt to preview the run-review packet and added prompt-budget truncation warnings for oversized context.
- Indexed generated run-review text in Collaborate search and fixed history normalization so invalid recent entries cannot push valid older chats out of local history.

## Applied In 0.4.23-beta

- Rotated back to Match Setup and expanded Scenario Preview into a compact setup receipt with run shape, relationship map, lock plan, setup source, and run constraints.
- Added run-shape copy that makes the active cast handoff into the Narrator and the turn budget visible before starting a match.
- Added relationship-map filtering so disabled, inactive, neutral, self-targeting, and invalid draft rules no longer inflate setup summaries.
- Added lock-plan and setup-source cards so generation locks, seeds, and recent generation history are visible in the setup receipt.
- Tightened setup readiness warnings so an enabled relationship matrix with only invalid draft rules is called out before the run.

## Applied In 0.4.24-beta

- Rotated from Match Setup to AI Lab review and added Battle Review, borrowing arena/eval ideas from Chatbot Arena, AutoArena, Promptfoo, Inspect, and model comparison dashboards.
- Added local judge-style review packets with verdict, score, risk flags, leading voice, watch target, slowest turn, token totals, latency totals, model count, and speaker token share.
- Added Copy Packet and Copy Nudge actions so transcript review can produce shareable judge notes and immediate operator interventions.
- Updated Review preset behavior so it opens a fuller review cockpit with Battle Review, Decision Card, Turn Compare, Quality Timeline, Memory Notes, Auto Moderator, and style cue support.
- Kept the first implementation deterministic and local: no hidden model-as-judge call, no remote eval dependency, and no claims of universal model ranking.

## Applied In 0.4.25-beta

- Mutated the research lane from eval/arena tooling to operator intervention, suggested responses, slash-command style steering, human-in-the-loop approval patterns, and debate moderation playbooks.
- Added Operator Quick Intervention chips inspired by suggested responses, follow-up chips, route/mode controls, and moderator playbooks from agentic chat and debate tools.
- Generated quick interventions locally from transcript diagnostics, errors, and run state: evidence checks, repair prompts, consensus breakers, private role resets, rhetoric cooling, decision framing, narrator judgments, and next-step forcing.
- Made quick interventions stage editable Operator Turn text instead of sending immediately, preserving operator control and avoiding accidental public turns.
- Added route-aware staging so interventions can switch between Public, Private, and Narrator routes when appropriate.

## Applied In 0.4.26-beta

- Mutated the research lane from operator intervention chips to agent control planes, execution rails, internet metadata, runtime traceability, and audit ledgers.
- Explored human-in-the-loop approval dashboard patterns from AgentGate, JamJet, n8n, LangGraph/LangChain, Cloudflare Agents, and OpenAI Agents SDK patterns.
- Added structured internet request/result metadata with requester/tool/target labels, timestamps, source lists, and compact debug rendering for narrow right rails.
- Added copy/debug affordances for internet metadata so operator decisions and tool context are easier to audit and share.
- Logged internet request execution status while keeping transcript retry focused on model turns.

## Applied In 0.4.27-beta

- Mutated the research lane from approvals to run timelines, trace inspectors, replay/fork workflows, event audit trails, model-call observability, and recent-run comparison.
- Added a deterministic Run Trace section inside Battle Review, borrowing trace tree and waterfall ideas from AgenticLens, AgentLens, Agent Replay, VoltAgent, Langfuse, LangSmith, OpenAI Agents tracing, and OpenTelemetry-style span terminology.
- Built transcript-backed spans for agent model calls, narrator model calls, internet tool results, operator turns, and other transcript events without adding a remote observability dependency.
- Added trace metrics for span count, model call count, tool event count, issue markers, total tokens, total latency, and slowest span so Review mode doubles as a lightweight run inspector.
- Added issue flags for pending spans, rejected/error spans, empty text, slow turns, high-token turns, cached tool results, and source-backed tool events.
- Added Copy Trace output and a deterministic next trace action so a run can be shared as an audit packet or used as a baseline before replaying/forking.

## Applied In 0.4.28-beta

- Mutated the release lane from trace inspection to review handoff and export clarity, guided by artifact/replay patterns in agent observability and saved-run tools.
- Added a live export-scope preview in the top rail so transcript exports show whether they will include all messages or only the currently visible filtered/timeline scope.
- Added filtered export tooltip copy that names single-turn or turn-range scope, reducing accidental partial transcript exports during review work.
- Added safe fallback language for empty filtered results so operators know export will include the full transcript instead of producing a blank file.
- Routed the top-rail export icon contextually: AI Lab keeps transcript export, while AI Collaborate now exports the current collaboration chat.
- Added AI Collaborate Markdown export with prompts, final answers, memory notes, generated Run Review packets, and Team Trace metadata for each role step.
- Added trace metadata to chat exports: role label, model, status, token count, latency, error text, and step body so saved collaboration runs remain auditable outside the app.

## Applied In 0.4.29-beta

- Rotated the release lane from export/review handoff to saved-run and recent-chat actionability, guided by compact thread rows, rerun/fork flows, session replay, history search, and comparison/export patterns in agentic tools.
- Borrowed the strongest low-risk ideas from LangSmith thread metadata, LangGraph Studio fork/rerun workflows, Langfuse and Phoenix session navigation, ChatGPT history search/project organization, and W&B/Open WebUI comparison surfaces.
- Added Recent Collaboration list summaries so the rail reports saved count, visible search results, and capped recent lists instead of silently hiding older chats.
- Expanded recent row metadata with open-state and needs-review badges, richer tooltips, and accessibility names/help text/status for keyboard and assistive navigation.
- Added right-click actions for Open, Fork, Repeat prompt, Copy summary, Copy markdown, and Delete so saved collaboration runs become reusable work objects instead of passive history rows.
- Made Fork restore the saved exchanges as working context while saving the next reply as a new conversation, and made Repeat prompt stage the latest prompt in a clean draft while carrying forward memory notes.
- Added deterministic tests for recent list summaries, exported summaries/tooltips/automation text, fork behavior, repeat-prompt staging, and history-store preload paths.

## Applied In 0.4.30-beta

- Rotated the release lane back to Match Setup graph and setup-spec polish, guided by setup-as-object patterns in AutoGen Studio, LangGraph Studio fork/rerun flows, Dify/LangSmith run-history inspection, Promptfoo/Braintrust/W&B eval comparison, and visual builder patterns from Langflow, Flowise, n8n, and CrewAI Studio.
- Added Copy Setup and Copy JSON actions for the current match setup so operators can export the exact scenario, tuning, relationship pressure, cast, locks, and provider context they are about to run.
- Turned the Relationship Matrix into a lightweight pressure graph preview with live route chips, coverage counts, neutral-source warnings, incoming target hotspots, mutual-pair summaries, and invalid-rule warnings before saving.
- Added three draft patterns: Skeptic sweep, Paired crossfire, and Spotlight defense, extending the relationship matrix beyond simple rings and chains.
- Expanded Scenario Preview relationship and constraint copy to include pressure graph insight instead of only raw saved matrix text.
- Renamed the seed inspector source label from internal YOLO wording to Wild Seed and added deterministic tests for setup specs, graph topology, graph preview lines, new patterns, and XAML accessibility contracts.

## Applied In 0.4.31-beta

- Rotated from setup graph polish to preflight/readiness polish, guided by readiness checklist patterns from n8n, Dify variable inspection, LangSmith run debugging, and setup-as-object research.
- Split Match Setup readiness into blockers and warnings so missing topic/model/matrix state can block the setup while provider, persona, and narrator risks remain visible as nonblocking warnings.
- Added readiness badges for State, Agents, Provider, Personas, Narrator, Matrix, and History in the Match Setup header.
- Added provider-offline and provider-error readiness warnings that keep the reason visible before starting a run.
- Added blank active-agent persona and blank narrator persona warnings so generated or manually edited casts are easier to audit.
- Expanded readiness tooltips into run summary, blocker, and warning sections, and added tests for badge payloads, warning-only state, blocked state, and XAML accessibility.

## Applied In 0.4.32-beta

- Continued the preflight lane by making blocker and warning details visible in the Match Setup header instead of only in the readiness tooltip.
- Added a visible preflight checklist with Required rows for blockers, Advisory rows for warnings, and a ready-state all-clear row.
- Added richer readiness badge tooltips for provider, personas, narrator, relationship matrix, locks, and generation history.
- Added role-specific model awareness so a blank shared provider model does not block setup when every active agent has its own assigned model.
- Added a Locks badge showing whether generated setup changes will preserve current topic, global, narrator, or active cast fields.
- Reused the normalized relationship matrix plan for readiness counting so unknown stances, invalid targets, inactive agents, self-links, and neutral rules cannot drift from the editor's validation.
- Added tests for visible checklist payloads, role-specific model readiness, provider error tooltip detail, normalized matrix blockers, valid noisy matrix counts, and XAML checklist accessibility.

## Applied In 0.4.33-beta

- Rotated from Match Setup readiness back to AI Collaborate history triage, guided by mutated searches across GitHub/dev sources for debate arenas, agent history, eval dashboards, and model comparison surfaces.
- Borrowed the strongest low-risk ideas from AI Debate Arena's structured judge panel and tactical cards, Agent Discussion Arena's local-first debate history, Agent Arena's debate history/revisit flow, Microsoft's eval-guide lifecycle dashboards, Microsoft's AI Agent Evaluation Scenario Library, and GitHub Copilot's task-oriented model comparison guidance.
- Added saved-run health states for Recent Collaborations: Ready, Needs review, Needs answer, and No trace.
- Added searchable recent-run metadata for health state, metric summary, and model mix so the history rail behaves more like a lightweight eval log.
- Added Copy compare for saved Collaborate runs when another chat is open, producing a Markdown delta packet against the open chat.
- Included turn, trace-step, issue, token, latency, prompt, answer, memory-note, and model-count deltas plus model mix, latest prompt/answer snippets, and a deterministic recommendation in the compare packet.
- Expanded recent row tooltips and automation names with review state, model mix, and compare availability.
- Added deterministic tests for compare markdown, metric snapshots, health states, searchable metadata, compare availability, row tooltips, and automation text.

## Applied In 0.4.34-beta

- Rotated from AI Collaborate history to AI World live presentation and observability, guided by mutated searches for multi-agent visualizers, relationship graphs, agent observability dashboards, replay/debug surfaces, and simulation/test frameworks.
- Borrowed low-risk ideas from agents-ui relationship graphs and real-time metrics, AgentLens replay/debug traces, Claude multi-agent observability dashboards, Agents Observe live event streams, OpenSearch Agent Health trajectory comparison, and multi-agent behavior visualization/testing lists.
- Added an AI World pulse snapshot with active, thinking, alert, tool, internet, locked, speaking, speaker-turn, latest-turn, and latest token telemetry.
- Fixed AI World activity decay to use the latest transcript turn instead of scheduler `TurnIndex`, preventing stale tool/internet beacons from lingering when the next scheduled participant index is low.
- Updated the AI World status line to label scheduler position as next slot and include latest transcript turn, live event counts, token load, speaker, and watcher count.
- Added pulse tooltips/help text to the world status and camera badge so the HUD doubles as a compact observability surface.
- Expanded legend rows with speaker turn, last turn, token load, and compact event state.
- Expanded name-tag accessibility help/status with the same compact live telemetry.
- Expanded inspector last-message copy with transcript kind/status plus token detail.
- Added deterministic tests for pulse aggregation, scheduler-vs-chronology activity decay, status/legend/name-tag telemetry, inspector trace labels, and existing render/accessibility coverage.

## Applied In 0.4.35-beta

- Rotated from AI World presentation to operator control-plane polish, guided by mutated searches for mission-control UIs, checkpoints, persistent session handoffs, and permission/control-layer design.
- Borrowed low-risk ideas from AWS AVA control-plane language, LLM Agent X Mission Control pause/resume/redirect patterns, MCP Agent Builder human-in-loop patterns, MetaboCommand queue UIs, Dive into Claude Code permission gates, and Crosslink handoff notes.
- Added a route-aware Operator Turn meter that previews public transcript, private memory, or narrator request destinations alongside character and token estimates.
- Added deterministic Operator Draft receipts with route, destination, visibility, staged prompt, and next check so interventions can be audited before sending.
- Expanded Public, Private, and Narrator route hints with explicit visibility language and private target summaries.
- Added route labels to quick intervention hints and structured receipt tooltips/help text to every intervention chip.
- Added a Handoff intervention for durable decision, assumption, risk, and next-check notes before pausing or changing direction.
- Added tests for route normalization, receipt text, route-refresh behavior, tooltip content, automation help, new suggestion priority, and XAML accessibility contracts.

## Applied In 0.4.36-beta

- Rotated from operator control-plane polish back to Match Setup, guided by mutated searches for visual agent builders, template galleries, workflow marketplaces, reusable YAML/JSON templates, and scenario configuration systems.
- Borrowed low-risk ideas from Langflow template/use-case galleries, Flowise visual agent builders and template-gallery discussions, Dify workflow/template marketplaces, n8n workflow collections, and Agent Composer domain-specific template catalogs.
- Added a first-class Match Setup preset catalog with category, summary, best-use, risk, role pack, style, pressure, and persona mixer metadata.
- Expanded the preset gallery with Black-Box Audit, Approval Maze, Launch War Room, Template Forge, and Memory Handoff.
- Decorated preset picker items with rich tooltips and accessibility help generated from the catalog.
- Expanded generation recipe tooltips with preset category, best-use guidance, risk notes, and overall catalog category counts.
- Added deterministic preset receipts and catalog summaries so preset metadata can be reused in tests, tooltips, and exported setup artifacts.
- Added exact preset-match detection so generated setup specs, diffs, rubrics, and current setup copies can say whether a recipe maps to a named preset or is custom.
- Added deterministic tests for catalog integrity, new preset mappings, receipt text, match detection, exported setup metadata, and XAML preset availability.

## Applied In 0.4.37-beta

- Rotated from Match Setup back to AI Lab transcript review, guided by mutated searches for agent observability, run replay, trace inspection, eval packets, and production agent telemetry.
- Borrowed low-risk ideas from Dynatrace agent instrumentation examples, DeepEval, Agent Replay, Langfuse, SAP Agent Quality Inspect, and TraceRoot around severity summaries, issue grouping, and replayable trace packets.
- Added deterministic Run Trace triage that labels trace health as Clean, Review, Watch, or Repair based on pending spans, repair spans, slow spans, high-token spans, and other issue markers.
- Added a Focus line and Review Queue so Battle Review points operators to the exact turn, speaker, span kind, and flags to inspect first.
- Expanded Copy Trace output with triage summary, focus, and review queue details so trace packets can travel outside the app as audit artifacts.
- Added visible Run Trace triage UI inside Battle Review, including summary text, a triage metric chip, and compact-mode-aware Review Queue rows.
- Tightened pending-span counting so resolved traces no longer exaggerate severity.
- Added deterministic tests for severity, focus selection, issue categories, review queue grouping, clean trace fallback, copied trace text, and visible trace review copy.

## Applied In 0.4.38-beta

- Rotated from AI Lab trace triage back to AI Collaborate saved-run organization, guided by searches for persistent agent workspaces, conversation history, replayable agent trajectories, and production agent runtimes.
- Borrowed low-risk ideas from Pydantic AI message history, LangChain deep-agent runtime patterns, AgentLens multi-session trajectories, and Taskade-style persistent workspaces: saved runs should be filterable, resumable, and reviewable as first-class artifacts.
- Added left-rail quick filter chips for All, Ready, Review, No trace, Memory, and Compare-ready Recent Collaborations.
- Added tokenized saved-run search lenses such as #ready, #review, #answer, #notrace, #memory, #compare, #fast, #team, #critique, and #redteam, with aliases for needs-review, red-team, issues, and no-trace.
- Added saved-run facet summaries for ready, review, no-trace, memory, and compare-ready counts so history health is visible before searching.
- Added inferred run mode labels for Fast, Team Draft, Critique, Red Team, No trace, and Saved Run rows without changing the persisted history schema.
- Expanded recent-row metadata, tooltips, automation names, and copied summaries with inferred mode, review state, model mix, and compare availability.
- Added deterministic tests for filter parsing, facet counts, mode inference, tokenized search, compare filtering, visible quick chips, and copied summary metadata.

## Applied In 0.4.39-beta

- Rotated to a standalone software-creation Agent section, borrowing low-risk ideas from persistent agent workspaces, human-in-the-loop command approval, and developer-agent control planes.
- Added Agent as its own top-level left-rail destination between AI World and AI Collaborate, with independent left-rail context, top metrics, right rail, and center workspace.
- Added persisted workspace folder selection and visible working-directory receipts so software tasks and approved commands start from a selected project folder.
- Added Planner, Reviewer, and Builder model collaboration for software tasks, using the existing provider and role-model routing instead of a separate provider stack.
- Added prompt helpers for implementation planning, task breakdown, progress updates, and command proposals.
- Added Terminal and PowerShell command proposal previews with readable invocation, working directory, risk chips, and explicit Approve/Reject controls.
- Added command safety checks that block obvious parent-directory escapes and absolute paths outside the workspace, while flagging destructive, network/install, elevated, and long-running commands.
- Added approved command execution with stdout, stderr, exit code, timeout state, elapsed time, and working-directory output.
- Added deterministic tests for command preview gating, working-directory validation, destructive-risk detection, terminal output capture, top-level Agent navigation, and the Agent approval rail contract.

## Applied In 0.4.40-beta

- Inspected the standalone Agent failure mode where an app-writing request could end as prose instead of staged work.
- Borrowed low-risk patterns from Codex, Claude Code, and Devin: coding agents should make file edits/commands visible, require review for risky actions, expose shell output, and continue through verification instead of stopping at a chat answer.
- Strengthened Builder instructions so write/create/scaffold/build/test requests must end with a single fenced PowerShell command proposal or a safe read-only inspection command.
- Added Build App and Next Step prompt chips so the user can ask for app creation and terminal-output follow-up without hand-writing prompt boilerplate.
- Added command-source labels in the approval rail so staged commands show whether they came from Builder output, manual edits, rejection, or command-result follow-up.
- Expanded command proposal extraction beyond fenced blocks to prompt-style command lines, labeled Command/Run/PowerShell lines, and runnable inline-code bullets.
- Added automatic staging of Builder command proposals into the approval rail with shell selection, preview, risk chips, and explicit approval still required before execution.
- Added bounded file-change receipts after approved commands so the output reports created, modified, and deleted workspace files instead of only stdout/stderr.
- Fed command output plus file-change receipts into the Next Step prompt so the Agent can continue from actual execution results.
- Added deterministic tests for Builder command staging, command proposal extraction variants, approval provenance, and file-change receipt summaries.

## Applied In 0.4.41-beta

- Continued the Agent work-loop pass using Codex/Claude Code/Devin-style patterns: visible next actions, explicit verification, output control, and a clear distinction between planning text and actual workspace change.
- Added center action cards whenever Builder stages, blocks, or holds a command proposal so the next required action is visible outside the right rail.
- Added center command-result cards with exit state, file-change counts, changed-path previews, and a suggested next action.
- Added a Verify prompt chip for build/run/test or read-only inspection follow-up after app work.
- Added Copy Output and Copy Receipt actions to the terminal output rail, with accessibility metadata and disabled states until output/receipts exist.
- Broadened app-writing intent detection to catch setup, bootstrap, prototype, site, game, tool, UI, app, and application prompts.
- Added no-command warnings for action-style prompts when Builder returns prose without a runnable command proposal.
- Added no-change warnings when an action-style command exits successfully but the bounded file receipt shows no tracked workspace file changes.
- Changed command extraction to prefer the final labeled Command proposal fence before earlier runnable examples.
- Expanded fallback command recognition for inspection and tooling commands such as rg, git, node, bun, Get-Content, Select-String, Test-Path, Add-Content, Out-File, Copy-Item, and Move-Item.
- Added held-proposal cards when Builder proposes a command while the approval rail already contains one.
- Added deterministic tests for visible action cards, top-mode preview state, final proposal extraction, broader intent detection, inline inspection commands, Verify chip visibility, and copy action contracts.

## Applied In 0.4.42-beta

- Continued toward a supervised coding-agent workbench, borrowing from Codex inline plan approval and Devin-style progress visibility.
- Added a Work Loop card in the Agent right rail that shows Planner, Reviewer, and Builder phase rows with Pending, Running, Done, and Error states.
- Added phase summary updates for prompt start, Planner reading, Reviewer risk-checking, Builder synthesis, staged command, no-command warnings, completion, and cancellation.
- Added automation metadata for phase rows so role progress is inspectable and testable.
- Added Copy and Clear actions for the staged command proposal.
- Added a Use Held action that stages the latest Builder command proposal held while another command occupied the approval rail.
- Stored held Builder proposals instead of only reporting them in activity text.
- Added held-proposal refresh logic so the Use Held action is disabled until a held command exists and disabled during command execution.
- Added center cards when a held proposal is staged, preserving the visible approval flow.
- Updated command-control enablement so copy, clear, and use-held actions track running command state.
- Updated README, user guide, Windows app notes, decomposition map, and research notes for phase progress and command rail actions.
- Added deterministic tests for Work Loop phase summary, phase row host, Copy/Clear/Use Held command action contracts, and staged-command phase reporting.

## Applied In 0.4.43-beta

- Continued the Agent recovery pass using Codex approval-mode ideas, Devin Progress visibility, and Claude Code permission/hook patterns.
- Added a Build Evidence card that separates role progress from outcome evidence: workspace, command need, proposal, preview, command run, file changes, and verify/repair state.
- Added a Rescue prompt chip and automatic Rescue prompt staging when an app-building request returns prose without a runnable Builder command.
- Updated Builder instructions so app/site/game/UI/scaffold requests are judged by previewable commands and file-changing outcomes rather than code snippets alone.
- Added Ctrl+Enter sending for the Agent prompt box while preserving multiline Enter drafting.
- Held Builder command proposals that arrive while another approved command is running, instead of dropping them as a busy-state footnote.
- Broadened command extraction for XML command blocks, plain labeled fences, pwsh/cmd labels, "Run this command" labels, comments before the first command, npm create, and common PowerShell aliases.
- Tightened command preview boundaries to block parent-path writes, redirection above the workspace, and output options targeting `..\` paths.
- Strengthened Next Step recovery prompts after successful commands that changed no tracked files.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for Rescue, Build Evidence, and stricter working-directory preview validation.
- Added deterministic tests for rescue staging, Build Evidence rows, broader extraction formats, parent-path blocking, and expanded app-work intent detection.

## Applied In 0.4.44-beta

- Continued the supervised terminal-workbench pass using Devin shell-control patterns and Codex-style explicit approval before execution.
- Added a Stop Command action to the Agent command approval rail.
- Added cancellation-token plumbing from the WPF command rail into the command runner.
- Added explicit cancelled command results so user cancellation is not collapsed into timeout or generic failure.
- Captured terminal output and file-change receipts after cancellation, preserving partial edits already made before the process was killed.
- Added cancellation state to terminal output and latest-command context for follow-up prompts.
- Updated command result cards, command source text, and Build Evidence rows to treat cancelled commands as a distinct retry-smaller state.
- Preserved the chat Stop behavior for chat cancellation while using the right-rail Stop action for terminal cancellation.
- Added disabled-by-default Stop button accessibility metadata and danger styling.
- Added deterministic tests for process cancellation, partial pre-cancel file writes, Stop button contracts, and initialized disabled Stop state.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for command cancellation.

## Applied In 0.4.45-beta

- Continued the Agent autonomy pass using coding-agent trust-mode patterns from Codex-style approval modes, Claude Code permission levels, and Devin shell-session workflows.
- Added a session-scoped Approve All control to the Agent command approval rail so preview-ready commands can run automatically after the user explicitly opts in.
- Kept workspace validation authoritative under Approve All: blocked previews, parent-path writes, and absolute paths outside the selected workspace still stop before execution.
- Reset Approve All when Agent is cleared or the workspace changes, keeping trust scoped to the current session and project folder.
- Added automatic handoff from Builder command staging to approved terminal execution when Approve All is enabled, with visible activity and Build Evidence updates.
- Added file-snippet materialization for models that answer app-writing requests with named code blocks instead of runnable commands.
- Generated safe PowerShell write-files commands from extracted snippets using base64-encoded content, relative path validation, parent-path rejection, and directory creation under the workspace only.
- Added dispatcher marshaling for command-completion UI updates so auto-approved commands report output, receipts, and status reliably after asynchronous process execution.
- Updated global Agent status after command completion so auto-approved runs surface final exit/file-change state instead of the pre-run handoff text.
- Added deterministic tests for file-snippet extraction, generated write command preview, parent-path rejection, Approve All auto-run behavior, autonomy status text, and the XAML approval contract.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for session autonomy and file-snippet materialization.

## Applied In 0.4.46-beta

- Continued the autonomy pass using Claude Code auto-mode guidance, Codex approval/security patterns, Devin command-progress visibility, and aider's automatic lint/test repair loop as reference points.
- Added a session-scoped Auto Continue control beside Approve All in the Agent command rail.
- Auto Continue enables Approve All, then automatically asks the Agent team for the next command after command output and file-change receipts arrive.
- Added a bounded three-step follow-up budget with visible status text so autonomous loops cannot continue indefinitely.
- Added automatic follow-up prompts that include latest command output, file receipt context, success/failure state, and instructions to return exactly one next command or verification command.
- Cleared consumed command text before Auto Continue asks Builder for the next proposal, preventing stale commands from forcing the next proposal into the held queue.
- Paused Auto Continue when a preview blocks, Builder returns no command, a command is cancelled, Agent is cleared, the workspace changes, or the follow-up budget is spent.
- Kept Approve All's workspace validation active under Auto Continue, preserving parent-path and outside-workspace blocking.
- Made Auto Continue pause handling dispatcher-safe for command-completion continuations.
- Fixed Auto Continue toggling so turning the loop off does not opportunistically auto-run an already staged preview.
- Added deterministic tests proving Auto Continue runs a follow-up command, spends its budget, preserves Approve All state, and exposes the new XAML controls.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for bounded Auto Continue.

## Applied In 0.4.47-beta

- Continued the Agent control-loop pass using Devin command-history visibility, Claude Code resume/session controls, Codex slash-command session affordances, and aider command-history patterns as reference points.
- Added a Command History card to the Agent right rail with recent running, completed, and blocked command rows.
- Added Replay Last so the latest history command can be staged back through preview without bypassing workspace validation.
- Added Copy History so recent command status, workspace, exit code, receipts, and command bodies can be shared or audited.
- Fed recent command history into Planner/Reviewer/Builder prompts so follow-up work can see what already ran or blocked.
- Added bounded Auto Rescue under Approve All so prose-only app-building responses automatically retry with the Rescue command prompt instead of waiting for another user click.
- Expanded the Auto Continue follow-up budget from three to six steps for longer app-building loops while keeping an explicit visible limit.
- Added an Autonomy row to Build Evidence so manual, Approve All, Auto Rescue, and Auto Continue state are visible beside command need and file evidence.
- Refreshed command history after command cleanup so replay actions enable as soon as the command is fully idle.
- Preserved workspace validation for every replay, auto-rescued command, and auto-continued command.
- Added deterministic tests for command-history copy formatting, command-history replay readiness, Auto Rescue after prose-only app output, and the Agent XAML command-history contract.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for command history, replay, and Auto Rescue.

## Applied In 0.4.48-beta

- Continued the Agent handoff and self-verification pass using middle-path autonomy patterns from Devin session modes, Claude Code auto-mode/permissions, Cursor-style background-agent review needs, and command-history session tools.
- Added a capped workspace profile scan when an Agent workspace is selected.
- The workspace profile detects common project signals such as Node, .NET, Python, Rust, Go, static web, likely verify commands, key files, and git repository presence while skipping cache/dependency folders.
- Fed the workspace profile into Planner, Reviewer, and Builder prompts so the Agent can choose better first commands and verification commands without a broad exploratory scan.
- Added a compact work summary under Terminal Output after command completion.
- Added Copy Brief to export a handoff packet containing the original task, autonomy state, latest command result, workspace, file receipt, changed paths, bounded stdout/stderr, and recent command history.
- Added Stage Verify to prepare a verification prompt from the latest command output and generated work brief.
- Included changed-path previews directly in the work summary line so the right rail shows what changed without opening the full receipt.
- Included the generated work brief inside verification prompts so the next model pass can focus on actual artifacts and recent command history.
- Added deterministic tests for workspace profile detection, ignored dependency folders, package script hints, work brief formatting, changed-path summary lines, Stage Verify prompts, and the XAML brief controls.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for workspace profiling and Agent work briefs.

## Applied In 0.4.49-beta

- Continued the Agent safety pass using Claude Code auto-mode/permission-guard ideas, VS Code Copilot trust-and-safety guidance, Devin self-verification/testing preference patterns, and loop-health findings from the local sub-agent scan.
- Hardened command preview boundaries for scaffold and generator commands that can write outside the selected workspace.
- Blocked parent-path generator targets such as `dotnet new --output=..\Outside`, `npm create ..\Outside`, `npx create-react-app ..\Outside`, `pnpm create`, `yarn create`, `cargo new`, `ng new`, and `git clone ..\Outside`.
- Expanded output/destination option checks for `--out-dir`, `--outDir`, `--output-dir`, `--output-path`, `--directory`, `--dir`, `--dest`, `--destination`, `--target`, `--path`, `--working-directory`, `--prefix`, and `git -C`.
- Added forward-slash Windows absolute path detection so `C:/outside/path` is treated like `C:\outside\path`.
- Kept workspace-relative scaffold targets valid, including `dotnet new --output .\TinyApp` and `npm create vite@latest TinyApp`.
- Added an Auto Continue duplicate-command brake before auto-approval runs a repeated follow-up command.
- Added an Auto Continue no-change brake after two consecutive successful no-change command results for command-required app work.
- Loop guards now disable session auto-approval, pause Auto Continue, leave staged duplicate commands for manual review, and surface the reason in status and Build Evidence.
- Added deterministic tests for expanded scaffold/output/absolute-path blocking, allowed workspace-relative targets, duplicate-command loop pauses, repeated no-change loop pauses, history counts, and autonomy status.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for scaffold boundary checks and loop-guarded autonomy.

## Applied In 0.4.50-beta

- Continued the Agent artifact/autonomy pass using current coding-agent UX patterns from Devin-style app previews, Antigravity-style artifact verification, and desktop agent command/session controls.
- Allowed Approve All and Auto Continue to be armed while Agent is already thinking, so a user can opt into session autonomy after sending a task without leaving the next preview stalled.
- Added a final pending-preview autonomy check when an Agent chat turn exits, ensuring mid-run Approve All toggles are honored before the command rail idles.
- Clarified the Approve All on-state label and tooltip so the button reads as a live session mode rather than a one-off approval.
- Added generated artifact suggestions after file receipts identify likely Node, .NET, Python, Rust, Go, or static web outputs.
- Hardened artifact inference for nested apps such as `TinyApp/package.json`, `PyApp/pyproject.toml`, `Crate/Cargo.toml`, and `GoApp/go.mod`.
- Scoped suggested verification commands for nested projects, including `npm --prefix`, `python -m pytest .\App`, `cargo test --manifest-path`, and `go test ./App/...`.
- Added an Artifact row to Agent Build Evidence so likely generated outputs are visible beside workspace, proposal, preview, command, and file state.
- Threaded artifact suggestions into the work summary, Copy Brief handoff, and Stage Verify prompt as suggested preview commands.
- Reset stale artifact suggestions when the workspace becomes invalid or changes.
- Added deterministic tests for nested artifact suggestions, preview-valid suggested commands, deleted-only receipts, Stage Verify artifact handoff, and mid-run Approve All arming.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for artifact suggestions and more autonomous session controls.

## Applied In 0.4.51-beta

- Continued the Agent artifact-preview pass using Antigravity's "verify with artifacts, not logs" pattern, AgentClick-style artifact-level review actions, and "human on the loop" workflow guidance.
- Added a visible Use Artifact action in the Agent Terminal Output card.
- Use Artifact stages the latest generated artifact suggestion command into the existing approval rail instead of creating a separate execution path.
- Kept preview validation authoritative for artifact commands, so working-directory checks, risk chips, and blocked previews behave the same as Builder, replayed, and manually entered commands.
- Let existing Approve All behavior auto-run Use Artifact commands only after preview succeeds and session autonomy is active.
- Added artifact command provenance labels so staged commands identify themselves as Node, .NET, Python, Rust, Go, or static web artifact suggestions.
- Added action/warning center cards when artifact commands are staged or blocked.
- Added stale artifact protection that refuses to stage an artifact command when the suggested entry file no longer exists under the selected workspace.
- Added dynamic Use Artifact tooltips that show the latest generated artifact summary and suggested command.
- Changed static web artifact suggestions from a read-only file-presence check to a default-browser preview command using `Start-Process .\artifact.html`.
- Kept static preview commands approval-gated and risk-labeled through the existing PowerShell preview path.
- Added deterministic tests for Use Artifact availability, command staging, provenance labels, static web preview commands, and the new XAML action contract.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for direct artifact-command staging and static web previews.

## Applied In 0.4.52-beta

- Continued the Agent autonomy pass using the current official OpenAI Codex manual as a reference for approval policy, sandbox/workspace scope, app thread workflows, integrated-terminal validation, in-app browser previews, permissions, and subagent workflows.
- Reframed Approve All as a workspace-session autonomy contract instead of a vague one-off approval shortcut.
- Added a visible session-autonomy center card when Approve All is toggled, making the active trust scope and remaining guardrails visible in the main conversation.
- Expanded Approve All status text and accessibility help to state that working-directory preview validation, blocked previews, loop guards, cancellations, and workspace changes still stop or reset autonomy.
- Expanded Auto Continue status/help text to call out bounded follow-up steps, duplicate-command guards, no-change loop guards, blocked previews, and workspace-change pauses.
- Threaded the stronger workspace-session autonomy contract into Planner/Reviewer/Builder prompts and copied work briefs so follow-up model calls know what can run automatically and what still blocks.
- Fixed Use Artifact under Approve All by attaching artifact provenance before preview can trigger an automatic run.
- Added artifact verification result handling so successful preview or verification commands are treated as "no tracked file changes expected" instead of suspicious no-change app writes.
- Added artifact verification summaries to Build Evidence, work summaries, work briefs, command result cards, command source labels, and Auto Continue prompts.
- Added an artifact-check top mode so successful no-change artifact previews read as checked artifacts rather than generic "verify next" work.
- Cleared the command editor after approved commands finish, while preserving output, receipts, work briefs, and command history replay, so later Builder proposals do not stall behind stale text.
- Kept command history as the durable replay/audit surface for completed commands after the editor clears.
- Fixed the WPF artifact-verification test wait pattern so async command completion can resume on the STA dispatcher instead of deadlocking.
- Added deterministic tests for manual artifact verification, Approve All plus Use Artifact provenance, session-autonomy cards, workspace-session copy, blocked-preview status copy, completed-command editor cleanup, and artifact verification no-change handling.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for Codex-inspired session autonomy and artifact verification polish.

## Applied In 0.4.53-beta

- Compared the current Agent screen against Codex Desktop's conversation-first session layout and shifted the Agent surface away from a dense dashboard toward a live agent thread.
- Added Agent-specific XAML styles for a compact workspace strip, transparent conversation frame, centered message lane, bottom composer panel, composer prompt box, prompt chips, autonomy button, and lighter Agent right-rail cards.
- Moved Approve All and Auto Continue into the bottom composer beside the prompt chips so session autonomy is selectable at the point where the user starts work.
- Renamed the right-rail Work Loop surface to Progress and kept Planner, Reviewer, and Builder phase rows in the secondary rail, closer to Codex's progress panel pattern.
- Slimmed the workspace selector into a strip that keeps the active folder and boundary visible without dominating the Agent page.
- Updated Agent message rendering so ordinary status messages read more like transcript entries, user tasks align as compact bubbles, and action/result cards remain framed for review.
- Changed the Agent top mode copy from internal approval-gate wording to manual approval or full access session language.
- Added Stage Next/Repair/Retry behavior that builds a result-aware follow-up prompt from latest command output, work brief, artifact suggestion, and recommended next action.
- Added risky-preview protection under Approve All so destructive, install/network, elevated, and long-running previews pause for manual approval instead of auto-running.
- Treated successful build/test/read-only inspection commands as expected no-change results instead of false repair failures.
- Split static web artifact handling into preview-launched language instead of implying a file-content verification check.
- Fed result-aware next action, latest output, latest work brief, artifact suggestion, and artifact verification context into next-step prompts and Auto Continue handoffs.
- Added deterministic tests for the moved autonomy controls, result-aware Stage Next behavior, risky-preview pauses under Approve All, expected no-change verification commands, and static web preview labeling.
- Updated README, user guide, Windows app notes, decomposition map, and release notes for the Codex-inspired Agent layout and autonomy polish.

## Applied In 0.4.55-beta

- Hardened remaining WPF copy/export/open-file paths so clipboard contention, read-only export targets, and shell launch failures report clean status instead of crashing user workflows.
- Made event-log rotation retry briefly and fall back to appending when rotated files are temporarily locked by another process.
- Added regression coverage for locked event-log rotation, atomic read-only export replacement, and safe external process launch failures.

## Applied In 0.4.54-beta

- Superseded the failed-smoke 0.4.53-beta installer with a fresh 0.4.54-beta distribution after launch smoke exposed a WPF runtime resource-order crash.
- Removed forward `StaticResource` references from the new Agent composer/autonomy styles so the main window loads correctly at runtime, not just at compile time.
- Kept the 0.4.53-beta installer folder untouched and bumped the app/release version before rebuilding, preserving the installer immutability rule.
- Carried forward the Codex-inspired Agent layout, composer-level autonomy controls, risky-preview stop, result-aware Stage Next, and expected no-change verification improvements into the corrected release.

## Next Candidate Batches

- Recent Collaboration polish: user-managed tags, persisted mode/round metadata, per-role last-run inspectors, and side-by-side saved run selection.
- AI World polish: role-specific silhouettes, depth-aware overlays, multi-bubble stacks, denser 4-8 agent layouts, smoother scene reuse, and stronger spectator/narrator staging.
- Match Setup setup-object polish: importable setup specs, side-by-side current-vs-saved diffs, blind-mode toggles, recent runs for a setup, and richer loadout cards.
- Observability polish: turn trace drawer, event timeline, context/memory inspector, internet metadata inspector, model scoreboard, blind duel reveal flow, slash intervention palette, transcript-card intervention actions, and AI World camera/focus events.

## 2026-07 Scenario and Behavior Quality Pass

Primary sources reviewed:

- OpenAI, *A practical guide to building agents*: https://openai.com/business/guides-and-resources/a-practical-guide-to-building-ai-agents/
- Anthropic, *Building effective agents*: https://www.anthropic.com/engineering/building-effective-agents
- Anthropic, *Demystifying evals for AI agents*: https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents
- Anthropic, *Writing effective tools for AI agents*: https://www.anthropic.com/engineering/writing-tools-for-agents

Applied conclusions:

- Clear instructions should map each step to an observable action, so arena turns now require one concrete contribution rather than permitting position restatement.
- Robust routines should capture edge cases and failure branches, so every generated scenario now defines a good outcome, an unacceptable failure, an edge-case test, an actionable output, and unresolved uncertainty.
- Agent evaluation should examine the trajectory and not only final prose, so the closure rule asks agents to check success/failure criteria before convergence and PowerShell generation state exposes the complete global instruction plus a `qualityContractPresent` audit flag.
- Deterministic safeguards should repair common model omissions, so AI Choice and Current Topics generation append the contract when otherwise-valid model JSON omits it.
- Complexity should earn its place. This pass strengthens the existing single-turn orchestration and generation schema instead of adding another evaluator model call, keeping local-model latency and VRAM cost bounded.

## 2026-07 Collaborate Trajectory Review Pass

Primary sources reviewed:

- Anthropic, *Demystifying evals for AI agents*: https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents
- OpenAI Academy, *Builder Bootcamp: Agents*: https://academy.openai.com/en/public/clubs/builders-etkn1/events/builder-bootcamp-agents-tf1pr0zo5i
- OpenAI, *A practical guide to building agents*: https://openai.com/business/guides-and-resources/a-practical-guide-to-building-ai-agents/

Applied conclusions:

- Agent evaluation should preserve the complete trajectory, not only the final prose. `collaborate.review` now exposes the latest answer, deterministic verdict, aggregate metrics, and complete latest-turn trace to PowerShell.
- Review evidence should be available through the same automation surface as run control. `Get-AIArenaCollaborateReview` supports the newest saved run or an explicit conversation id and still receives the standard fresh post-command application state.
- Deterministic graders must not invent failures from lifecycle labels. Healthy `Restored.`, `Exported.`, and `Saved run.` outcomes are now treated as neutral success states; trace errors, missing traces, blank answers, interruptions, and model failures still require review.
- Control-plane families should remain auditable in isolation. Collaborate dispatch now lives in a dedicated handler rather than adding more cases to the main window switch.

## 2026-07-15 Agent Ecosystem and Control-Surface Audit

This batch separates direct demand evidence from product-direction inference. Vendor launches and protocol adoption show where the ecosystem is moving, but they do not prove that AI Arena users requested every feature below.

### Primary-source findings and evidence classification

Direct user-demand or behavioral signals:

- An OpenHands user explicitly requested a true Ask/Plan mode followed by an Execute mode, including a read-only planning boundary: https://github.com/All-Hands-AI/OpenHands/issues/10433. This is a direct feature-demand signal, not a representative survey.
- Anthropic reports that users approve about 93% of Claude Code permission prompts and presents sandboxing and auto mode as ways to reduce repetitive approval fatigue while retaining bounded controls: https://www.anthropic.com/engineering/claude-code-sandboxing and https://www.anthropic.com/engineering/claude-code-auto-mode. This is vendor-reported product telemetry and behavior, not AI Arena-specific feedback.

Official product, engineering, and standards signals used for roadmap inference:

- MCP's 2025-11-25 specification defines a client/server capability model covering tools, resources, prompts, discovery, authorization, standard input/output, and Streamable HTTP: https://modelcontextprotocol.io/docs/learn/architecture and https://modelcontextprotocol.io/specification/2025-11-25/basic.
- Microsoft documents native function invocation and several agent-orchestration patterns, including sequential, concurrent, handoff, group-chat, and evaluator flows: https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/function-calling/function-invocation and https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/agent-orchestration/.
- OpenAI's deep-research update emphasizes connected apps/MCP, trusted-site restriction, live progress, and the ability to interrupt and refine a run: https://openai.com/index/introducing-deep-research/.
- Anthropic's multi-agent research system describes an orchestrator-worker architecture, parallel search, and a dedicated citation pass: https://www.anthropic.com/engineering/multi-agent-research-system.
- Agent Skills formalizes portable, progressively disclosed capability packages rather than hardcoded prompt presets: https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills and https://agentskills.io/specification.
- MCP Tasks, A2A 1.0, and OpenTelemetry semantic conventions point toward durable jobs, interoperable remote agents, and standard telemetry: https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/tasks, https://github.com/a2aproject/A2A/releases, and https://github.com/open-telemetry/semantic-conventions/releases.
- Anthropic's long-running-agent and context-engineering guidance supports fresh-context evaluation, explicit progress artifacts, and deliberate context compaction: https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents and https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents.

The items in the second group are product inferences for AI Arena. They are strong interoperability and architecture signals, but they should be validated against local usage before being treated as user-demand rankings.

### Current coverage and gaps

| Area | Current AI Arena coverage | Material gap |
| --- | --- | --- |
| Agent execution | Durable runbook state, checkpoints, command preview/approval, receipts, bounded autonomy, workspace validation, history, and PowerShell state/control | No enforced read-only Plan mode, OS sandbox, provider-neutral structured tool-call loop, or general tool registry |
| Collaboration | Team Draft, Critique, and Red Team workflows with deterministic review evidence | Predominantly fixed/sequential orchestration; no adaptive DAG, parallel worker pool, handoff graph, or independent evaluator agent |
| Internet | Hardened search/fetch, ranking, page enrichment, sources, and citation-oriented output | Prompted JSON request convention rather than native provider tool calls; no durable research plan, query decomposition, claim ledger, interrupt/refine loop, or citation verifier |
| Automation | Broad Agent, Collaborate, Internet, scenario, settings, and state commands with fresh post-command state | No MCP client/server runtime, capability discovery, reusable task queue, A2A bridge, or standard OTLP/GenAI export |
| Reuse and memory | Roles, presets, workspace notes, histories, saved runs, and checkpoints | No portable Agent Skills library, semantic context compaction, provenance ledger, or restoreable multi-job context |

### Bounded foundation delivered by the current increment

The current increment improves three prerequisites:

- Portable, versioned Match Setup artifacts give generated and current setups a canonical exchange shape rather than leaving setup meaning trapped in visual controls.
- Scenario-audit corrections make active cast, relationships, rules, and the scenario quality contract report their actual state, so automation and later evaluators can rely on the same facts.
- Overlay consistency keeps setup and audit state coherent across the surfaces that expose it, reducing UI-only state divergence.

This is valuable schema, invariant, and control-surface groundwork. It is not completion of MCP or general tool-runtime work. It does not add an MCP client or server, remote capability discovery, MCP authorization, a provider-neutral native function-call loop, arbitrary tool execution, a portable skills runtime, the MCP Tasks lifecycle, A2A interoperability, or a general approval policy for external tools.

### Prioritized roadmap

Priority 0 — make tool use real, bounded, and observable:

1. Define one provider-neutral tool request/result contract and execution loop. Prefer provider-native structured function calls when available; keep a validated fallback for local models.
2. Add an MCP Capability Hub supporting standard input/output and Streamable HTTP, capability discovery, explicit trust scopes, credentials, per-tool policy, cancellation, receipts, and PowerShell parity.
3. Add an enforced Plan/Execute boundary: Plan is read-only; Execute uses workspace policy plus OS-backed containment where available. Keep the normal bounded mode enabled by default and make policy understandable in Settings.
4. Build a durable Research Run on top of the tool loop: research plan, parallel query lanes, trusted-domain controls, source/claim map, live progress, interruption/refinement, citation validation, export, and complete control-plane state.

Priority 1 — make agent work composable and durable:

5. Introduce selectable orchestration graphs (sequential, concurrent, handoff, debate, and evaluator) with budgets, stop conditions, and traceable transitions.
6. Add a portable Agent Skills library with manifests, progressive disclosure, provenance, trust review, versioning, and enable/disable controls.
7. Add a Task Center for queued, resumable, cancellable jobs with checkpoints, artifacts, recovery, and per-job budgets.
8. Add an Eval Lab with reusable datasets, independent fresh-context graders, trajectory checks, evidence gates, regressions, and side-by-side run comparison.
9. Add a Context Ledger with source provenance, semantic compaction, explicit restore points, and visibility into what each agent received.

Priority 2 — interoperate and export:

10. Add a policy-gated A2A bridge only after the local tool, identity, task, and trust models are stable.
11. Map internal traces to OpenTelemetry/GenAI semantic conventions and support OTLP export without replacing the useful in-app trace views.

Control-plane requirement for every roadmap item: UI and PowerShell must call the same application service/handler, every mutation must return authoritative fresh state, and no material capability may exist only as a visual control. The portable Match Setup schemas and corrected audit state are therefore the foundation for later tool and evaluation contracts, not a substitute for implementing them.

## 2026-07-15 Current Match Branching Pass

Primary sources reviewed:

- LangGraph describes checkpoint time travel as an undo/debug/audit mechanism and preserves the original history when a run is forked: https://docs.langchain.com/oss/python/langchain/frontend/time-travel
- Claude Code documents session branching as a way to try another approach while keeping the original session intact: https://code.claude.com/docs/en/sessions
- OpenHands users requested instant conversation forking because manually copying and rebuilding agent context is cumbersome: https://github.com/All-Hands-AI/OpenHands/issues/8560
- Codex SDK users requested a programmatic fork/backtrack API rather than approximate transcript replay: https://github.com/openai/codex/issues/4972
- Codex UX feedback asks for a top-level current-session fork and an obvious path back to the parent: https://github.com/openai/codex/issues/9499
- OpenHands' local conversation implementation keeps source events immutable and gives the fork independent identity/state: https://github.com/OpenHands/software-agent-sdk/blob/main/openhands-sdk/openhands/sdk/conversation/impl/local_conversation.py#L3362-L3518

Applied conclusions:

- AI Lab needed a safe branch primitive distinct from clean-session creation and in-place transcript retry. Fork Current Match now clones the authoritative complete persisted state, never rewrites its source, records direct-parent lineage, and creates an independently mutable session.
- Branching is a shared application workflow, not a UI-only shortcut. The Saved State button and `session.fork`/`New-AIArenaSessionFork` use the same exclusive mutation, audit, selection, and secret-free receipt path.
- Honest scope matters. This increment forks only the current persisted snapshot. It does not advertise arbitrary historical-turn time travel because AI Arena does not yet persist a complete snapshot of private notes, attachments, configuration, and other causal state at every turn.
- Collision-safe create-new semantics, parent navigation, busy refusal, source immutability, and restart-persisted lineage are required product behavior rather than incidental implementation details.
