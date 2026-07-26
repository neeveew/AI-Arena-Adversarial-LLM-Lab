# AI Arena User Guide

AI Arena is a native Windows app for running structured conversations between multiple local or OpenAI-compatible language models. The models can debate, collaborate, test ideas, inspect tradeoffs, or free-roam around a topic while you guide the match as the operator.

The app is designed for local experimentation with model behavior. You can create a scenario, tune the cast, assign models per participant, run one turn at a time, let the match continue automatically, inject operator turns, ask the narrator to summarize, let participants search and fetch internet sources, inspect diagnostics, and save or export the transcript.

## Quick Start

1. Install AI Arena from the versioned setup file.
2. Start LM Studio or another OpenAI-compatible provider.
3. Open Settings, then **Models & provider**.
4. Choose a provider preset, press **Use preset**, and select a **Default model**.
5. Press **Test connection**.
6. Open **Custom connection** only when you need to change the connection type, server address, or access token. A standard LM Studio OpenAI-compatible address is `http://127.0.0.1:1234/v1`.
7. Optionally use **Model recommendations** to scan local hardware and recommend a multi-model role spread.
8. Open AI Lab, then use Match Setup in the top rail to choose a preset, role pack, style, pressure, absurdity level, AI Choice, or Wild Seed.
9. Close Match Setup and run 1 TURN or AUTO CHAT.
10. Open Agent, choose a workspace folder, and preview commands before approval when using the software workspace.

## Main Concepts

- Alpha, Beta, Gamma, and Delta are the participant agents.
- The Narrator is separate from the agents. It can summarize, frame, or guide without becoming Alpha, Beta, Gamma, or Delta.
- The Operator is you. Operator messages are public instructions injected into the transcript.
- Agent is a separate software workspace for planning and command-gated project work.
- Each participant has its own persona and model assignment.
- Agents see public transcript context, but their private persona and memory notes are handled separately.
- Sessions and AI Collaborate chat history are saved locally under your Windows user profile.

## App Layout

The left rail contains the app identity, navigation, and contextual status. AI Lab, Agent, and AI Collaborate are first-class navigation surfaces. Agent is visible by default and can be shown or hidden independently under **Settings -> Agent workspace**; this does not require Debug controls. In AI Lab the rail shows session overview and live agent status. In Agent it shows the active working directory and software team roles. In AI Collaborate it shows recent collaboration chats and team roles.

The center area contains the active page:

- AI Lab: the arena transcript, diagnostics, filters, timeline, memory notes, compare tools, and the Match Setup flyout.
- Match Setup: a wide AI Lab flyout for scenario preview, cast preview, locks, per-agent voice styles, pressure controls, grouped generation controls, generation history, and checkpoint/session tools.
- Agent: workspace-scoped software chat with a Codex-like centered conversation lane, bottom composer, visible session Full Access control, plus-menu popup for prompt presets/session controls, collapsed Workspace/Advanced drawers for deeper tuning, workspace profiling, planning, review, app-building and verification prompts, visible Planner/Reviewer/Builder progress, compact Outputs summaries, staged command proposals, action/result cards, generated artifact suggestions, Auto Rescue, loop-guarded Auto Continue autonomy, command history/replay, approved terminal output, and file-change receipts.
- AI Collaborate: classic AI chat where Alpha, Beta, Gamma, and Narrator collaborate on the operator's prompt.

The right rail contains:

- Arena Controls.
- Agent Performance.
- Operator Turn.

In AI Collaborate, the right rail changes to Collaborate controls: mode, provider, and team model assignments.

In Agent, the right rail changes to Progress, Build Evidence, Outputs, Agent activity, and a collapsed Advanced drawer for command approval, terminal output, and command history.

The top rail adapts to the active workspace. AI Lab shows Match Setup, transcript search/export, and View. AI Collaborate shows collaboration search/export without transcript-only View controls. Agent hides transcript-only commands. Provider details, Help, the optional Debug menu, the right-rail toggle, and Settings remain available where relevant.

At narrower window sizes, opening the right rail reveals it as a drawer over the workspace instead of squeezing the center content. Hide it again with the same top-rail toggle.

## Top Rail

- Match: current match style.
- Provider: online/offline provider state.
- Current turn: next scheduled participant.
- Turns: transcript turn count.
- The second status line shows the current idle/run state. When idle, it names the next participant, selected model, and provider state.
- Match Setup: opens the wide AI Lab setup flyout for scenario, cast, lock, generation, and saved-state controls.
- Search icon: opens a draggable search popup. In AI Lab it searches transcript text, speakers, models, and sources. In AI Collaborate it uses collaboration-oriented placeholder text and recent searches.
- Export icon: in AI Lab, exports the current transcript scope to Markdown; in AI Collaborate, exports the current chat with run reviews and team trace details.
- View menu: applies transcript presets and toggles Compact transcript, Turn compare, Quality timeline, Battle review, Memory notes, and Auto-scroll.
- Debug menu: appears only when Settings -> Visuals -> Allow debug controls is enabled. It holds experimental transcript helpers such as Decision card and Style fit.
- Gear icon: opens Settings.

Theme selection now lives under **Settings -> Visuals** with avatar and transcript-strip preferences. System keeps the selected palette while automatically adopting the High Contrast palette when Windows high contrast is enabled.

## AI Lab

AI Lab is the main arena workspace. It contains the transcript stream plus diagnostics, filters, timeline, Battle Review, memory notes, compare tools, and the top-rail Match Setup flyout.

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

Use the export icon in the top rail. In AI Lab, export writes a Markdown transcript containing turn metadata, speaker names, model names, token/context stats, latency, message text, reasoning, and internet metadata when available.

The export status text beside the icon previews the scope. With no filters it shows the full transcript count. If filters or timeline selection are active, it shows the visible/all message count and the tooltip names the selected turn range. If a filter currently shows no transcript messages, export falls back to the full transcript.

In AI Collaborate, the same icon switches to chat export. It writes a Markdown chat containing prompts, final answers, memory notes, Run Review packets, and Team Trace entries with model, status, token, latency, and error metadata.

### Compact Transcript

Open View, then enable Compact transcript. This reduces card spacing and body height for smaller screens or dense review sessions.

### Turn Compare

Open View, then enable Turn compare. The app shows a compare panel above the transcript.

Use Compare on transcript cards to select two turns. The compare panel shows token, context, and latency deltas, plus side-by-side content.

### Match Quality Timeline

Open View, then enable Quality timeline. The timeline scores recent match quality using the discourse diagnostics.

Click a timeline bar to filter the transcript to that turn. Click the selected bar again, or Clear, to remove the filter.

### Battle Review

Open View, then enable Battle review, or choose the Review preset. Battle Review creates a local judge-style packet from transcript telemetry and discourse diagnostics.

The packet shows a verdict, score, leading voice, watch target, risk flags, model count, token total, latency total, slowest turn, per-speaker token share, and a recommended next action. It also includes Run Trace: ordered spans with model/tool counts, issue markers, slowest span, token/latency totals, triage severity, top focus, category counts, and a review queue for the exact turns to inspect. Copy packet copies the review note, Copy trace copies the run trace packet with triage details, and Copy nudge copies an operator intervention prompt based on the current flags.

### Agent Memory Notes

Open View, then enable Memory notes. Memory notes are private per-agent notes stored in the session snapshot and used by model context windows.

The memory panel lets you refresh, edit, clear individual agent notes, or clear all notes. Notes can also update after successful agent turns.

### Debug Controls

Open Settings -> Visuals, then enable Allow debug controls. The top rail shows a Debug menu for experimental helpers. Decision card shows a compact narrator-generated operator summary above the transcript. Style fit shows optional cue chips for constrained voice styles. Review mode can also surface Decision card and Style fit as part of the review cockpit. Voice drift enforcement injects stricter per-turn voice reminders into agent prompts while Debug is enabled. AI World is also Debug-gated and remains off by default. PowerShell control and the default-on Agent workspace are normal Settings features and are not gated by Debug controls.

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

Auto Chat stops when you press Pause or when a provider error occurs.

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

## AI World

AI World is an experimental 3D arena view for the active AI Lab session and is hidden by default. To opt in, enable **Settings -> Visuals -> Allow debug controls**, then turn on **Debug -> AI World (3D)**. This reveals the **Transcript | World** selector beside Match Setup. Turning AI World off immediately returns to Transcript and hides the selector; disabling master debug controls does the same. When explicitly enabled, AI World shows active agents as animated robots with name tags, speech bubbles, status beacons, a minimap, and an inspector.

When an agent is speaking or thinking, the camera can follow that agent and the other agents turn toward the speaker. The status line is a live pulse: active agents, next scheduled slot, latest transcript turn, message count, thinking/alert/tool/internet/lock counts, latest token load, active speaker, and watcher count. Speaker focus is reinforced with a brighter floor ring, attention halo, stage lighting, and a central arena console. Speech bubbles include speaker and turn headers so active dialogue is easier to scan.

Camera controls:

- FOLLOW: follow the current speaker or selected agent.
- FREE: keep the camera where you move it.
- OVERVIEW: pull back to see the whole arena.
- Cinematic: add gentle camera motion during Auto Chat.

Mouse and keyboard:

- Drag to orbit the camera.
- Shift-drag or Shift + arrow keys to pan.
- Mouse wheel, plus, or minus zooms.
- F or Home returns to speaker follow.
- O opens overview.
- N and P cycle selected agents.
- C toggles cinematic camera.
- Esc closes the inspector.

AI World uses live session data. Tool activity, internet sources, errors, thinking state, lock state, voice style, pressure profile, last-message telemetry, public summary, and private notes appear as compact world cues. The bottom legend and name tags include recent turn and token telemetry, while the inspector shows last-message kind/status plus prompt, completion, and total tokens. The narrator has a distinct booth-like identity treatment when present.

## Agent

Agent is a default-on, first-class software-creation workspace in the left rail. It is separate from AI Collaborate. Use **Settings -> Agent workspace** to show or hide its navigation entry independently; the setting does not depend on Allow debug controls.

Agent uses its own software roles: Planner, Reviewer, and Builder. These roles are isolated from AI Lab scenarios, cast personas, Alpha/Beta/Gamma/Delta participant roles, and Narrator behavior. Agent model calls use the shared provider model for coding work, so arena role packs and scenario tuning do not change the coding workflow.

Choose a workspace folder from the collapsed Workspace drawer. The active path is shown in the page header and left rail, and command previews use that folder as the working directory. The selected path is saved in WPF settings. Agent builds a capped workspace profile from common project files such as `package.json`, `.sln`, `.csproj`, `pyproject.toml`, `Cargo.toml`, `go.mod`, and `index.html`; Planner, Reviewer, and Builder receive that profile so they can choose better first commands and verification steps without scanning large dependency folders.

The chat area sends software tasks to a small Agent team:

- Planner turns requests into implementation plans, file targets, risks, and next steps.
- Reviewer checks assumptions, tests, safety, and command risk.
- Builder synthesizes the plan into concrete code suggestions and command proposals. For app-writing or scaffolding requests, Builder is expected to stage a first runnable command instead of ending with prose only.

The bottom composer keeps the software prompt box and visible Full Access autonomy control in the main path. The plus button opens a Codex-like controls popup for prompt chips and deeper session controls. Prompt chips stage common software prompts for planning, task breakdowns, progress updates, command proposals, app creation, next-step follow-up after terminal output, verification, and rescue. Use Verify when the next natural move is a build, run, smoke test, or read-only inspection command. Use Rescue when a software request needs one runnable command rather than more explanation.

When Builder stages a command, the center conversation also adds an action card so the next step is visible even if the right rail is not where your eyes are. Agent accepts fenced command blocks, XML-style command tags, and JSON `shell`/`command` objects, which helps smaller local models produce stageable commands without needing cloud-style formatting. If an app-writing prompt returns prose without a command, Agent shows a warning that the app has not been written yet and stages a Rescue prompt that requires a previewable command proposal. With Full Access enabled, Agent can spend a bounded Auto Rescue retry to send that Rescue prompt automatically instead of waiting for another click.

The Agent right rail tracks progress, evidence, outputs, and activity first. Open Advanced for command approval, terminal output, and command history:

- Progress shows Planner, Reviewer, and Builder phase status while the model team is running.
- Build Evidence shows whether the workspace is ready, a command is required, a proposal exists, preview is ready, a command ran, files changed, a likely artifact was detected, and the next action is verify or repair.
- Choose Terminal or PowerShell.
- Enter a command.
- Press Preview to see the shell, command, working directory, and risk chips.
- Press Approve to run only the command that was previewed.
- Press Full Access in the composer to let preview-ready Agent commands run automatically for the current workspace session. You can arm it before sending a task or while Agent is already thinking, so the next staged preview can run without another click. This reduces babysitting during app-building loops, but it does not bypass preview validation; blocked commands, destructive commands, install/network commands, elevated commands, long-running previews, parent-path writes, outside-workspace paths, loop guards, cancellations, and workspace changes still stop or reset autonomy. Full Access also arms bounded Auto Rescue attempts for prose-only app replies, and the center chat adds a visible session-autonomy card when you turn it on. Full Access turns off when Agent is cleared or the workspace changes.
- Open the plus menu and press Auto Continue to let Agent ask for the next command after command output and file receipts arrive. Auto Continue enables Full Access, spends a visible follow-up budget, clears consumed command text before each next proposal, and pauses when the budget is spent, a preview blocks, a risky preview needs manual approval, a command is cancelled, Agent is cleared, the workspace changes, Builder repeats the same command, or an app-building loop produces repeated unexpected no-change commands.
- Press Stop to cancel the active command. Agent keeps the terminal output it captured, marks the run as cancelled, and still produces a file-change receipt for any partial workspace edits.
- Press Reject to discard the preview.
- Use Copy to copy the staged command, Clear to reset the command rail, or Use Held to stage the latest Builder proposal that arrived while another command was already in the rail.

Commands run from the selected workspace folder. The approval rail labels whether the command was staged by Builder, converted from Builder file snippets, edited manually, replayed from history, rejected, produced by a follow-up loop, or staged from a generated artifact suggestion. If Builder answers an app request with named file/code blocks instead of a command, Agent can convert those snippets into one previewable PowerShell write-files command. The preview blocks obvious attempts to move above the workspace, parent-path writes such as `..\outside.txt`, scaffold/generator targets above the workspace such as `dotnet new --output=..\Outside`, `npm create ..\Outside`, `git -C ..`, output/destination/prefix options above the workspace, or absolute paths outside it, including `C:/...` forms. It also flags destructive, install/network, elevated, and long-running commands for review. Under Full Access, those high-risk previews stay staged for manual approval instead of auto-running. Output shows the command, working directory, exit code, timeout state, cancellation state, stdout, stderr, a compact work summary with changed-path preview, and a bounded file-change receipt with created, modified, and deleted workspace files. After an approved command finishes, Agent clears the command editor so the next Builder proposal is not held behind stale text; command history, output, receipts, and work briefs keep the executed command available for replay or audit. When file receipts look like generated app artifacts, Agent suggests a preview or verification command for Node, .NET, Python, Rust, Go, or static web projects, including nested apps such as `TinyApp/package.json`. Use Artifact stages that latest suggestion into the same approval rail; if Full Access is on, the staged preview follows the normal auto-run path unless it is a long-running static web preview that needs manual approval. Successful artifact preview or verification commands are treated as "no file changes expected" instead of no-change repair failures. Static web artifacts use a default-browser `Start-Process` preview after approval and are labeled as previews launched rather than file-content verification; stale/missing artifact files are refused before staging. Copy Output copies the full terminal panel, Copy Receipt copies just the file-change receipt, Copy Brief copies a handoff packet with task/autonomy/result/files/history/artifact suggestion, Stage Next/Repair/Retry stages a result-aware follow-up from the latest output, and Stage Verify prepares a verification prompt from the latest work brief and suggested preview command.

Command History records recent running, completed, and blocked Agent commands. Replay Last stages the latest command back through preview instead of bypassing validation. Copy History copies recent commands with statuses, workspace path, exit code, and file-change summaries.

After a command runs, Agent adds a result card with exit state, file-change counts, a few changed paths, and the suggested next action. It also generates a work brief that includes the original task, session autonomy state, latest command status, workspace, file receipt, bounded stdout/stderr, changed paths, recent command history, any generated artifact suggestion, and any artifact verification result. Next-step and verification prompts include that brief so the next model call can focus on the actual artifacts. If an app-writing command exits successfully but no tracked workspace files changed, Agent treats that as suspicious and suggests a repair follow-up instead of implying the app is finished, except for successful artifact preview, verification, build, test, and read-only inspection commands where no workspace edits are expected.

### PowerShell Control

AI Arena includes a local PowerShell control plane for smoke tests, scripting, and hands-on automation. It is enabled by default, uses a local named pipe, and requires the per-run token written to the current user's app-data directory.

This is a normal application setting, not a Debug feature. Disabling Debug controls does not disable PowerShell automation.

Enable it in the app:

1. Open Settings -> PowerShell Control.
2. Leave PowerShell control plane enabled, or turn it off when local automation is not wanted.

Load the helper script in PowerShell:

```powershell
. "$env:LOCALAPPDATA\Programs\AI Arena\ai-arena-control.ps1"
```

Basic checks:

```powershell
Get-AIArenaCapabilities
Invoke-AIArena status
Get-AIArenaProvider
Select-AIArenaView agent
Invoke-AIArenaAgent state
```

Inspect, update, and verify the active session's provider without opening Settings:

```powershell
$provider = Get-AIArenaProvider
$provider.data | Format-List baseUrl, apiMode, model, online, lastLatencyMs, advertisedModelCount

Set-AIArenaProviderConfig `
    -BaseUrl "http://127.0.0.1:1234/v1" `
    -ApiMode lmstudio_native `
    -Model "google/gemma-4-e2b" `
    -ContextLength 32768 `
    -NativeStatefulChat $true

Update-AIArenaProviderModels
$test = Test-AIArenaProvider
$test.data
```

Only supplied provider fields change; explicit `$false`, numeric `0`, and empty role-model values are preserved. An empty role model clears that override and restores shared-model inheritance. To save a credential without placing plaintext in command history, use `$providerToken = Read-Host "Provider token" -AsSecureString`, then `Set-AIArenaProviderConfig -ApiToken $providerToken`. Provider responses and events expose only whether a token is configured. `Set-AIArenaProviderModel` remains the shortcut for assigning one model to the shared route and every arena role.

Capture the current WPF window from PowerShell:

```powershell
$capture = Save-AIArenaScreenshot
$capture.data | Format-List path, byteSize, pixelWidth, pixelHeight
$capture.state

Save-AIArenaScreenshot "reviews/after-provider-test.png"
Save-AIArenaScreenshot "C:\Screenshots\AI-Arena.png"
```

Without `-Path`, AI Arena writes `%LOCALAPPDATA%\AI Arena\exports\screenshots\AI-Arena-yyyyMMdd-HHmmss-fff.png`, or the equivalent `exports\screenshots` directory under `AI_ARENA_DATA_DIR`. Relative `.png` paths resolve beneath that screenshot directory; absolute `.png` paths are accepted. Existing files are never overwritten. The response returns the resolved absolute path, PNG byte size, pixel width and height in `data`, plus the standard fresh application `state`.

Navigate Match Setup and Settings without recreating their UI behavior in a script:

```powershell
Open-AIArenaMatchSetup matrix
$setup = Get-AIArenaMatchSetup
Set-AIArenaMatchRoster 6
Set-AIArenaMatchMatrix evidence_ladder
$matrix = Get-AIArenaMatchMatrix
Export-AIArenaMatchSetup ".\portable-match.json"
Import-AIArenaMatchSetup ".\portable-match.json" -Name "portable-review"
Close-AIArenaMatchSetup

Open-AIArenaSettings "internet"
$settings = Get-AIArenaSettings
Search-AIArenaSettings "voice"
Set-AIArenaSettings -CompactTranscript $true -BattleReview $true -VoiceEnabled $false
Close-AIArenaSettings
```

Match Setup remembers the originating workspace, and both overlays use the same close and focus-restoration paths as mouse or keyboard navigation. `Set-AIArenaMatchRoster` accepts 1-8 active agents and refuses changes while the arena is busy. `Set-AIArenaMatchMatrix` accepts round-robin challenge, mutual rivals, evidence ladder, support chain, de-escalation ring, devil's triangle, skeptic sweep, paired crossfire, spotlight defense, or `off`. `Export-AIArenaMatchSetup` produces a secret-free `ai_arena.match_setup.v2` package with a stable setup fingerprint. `Import-AIArenaMatchSetup` validates the package and creates a clean, collision-free session rather than replacing the active run. Runtime transcript/history is omitted. Provider tokens are never exported; URL-embedded credentials, queries, and fragments are stripped; and a local token is retained on import only for an exactly matching trusted endpoint and API mode. Settings state excludes provider tokens and other secrets. `Set-AIArenaSettings` accepts transcript following/compact mode, `diagnostics|telemetry|hidden` top-strip mode, Turn Compare, Match Timeline, Battle Review, Memory Notes, Decision Card, Auto Moderator, Style Fit, Internet details, voice, World, and Agent workspace toggles. The PowerShell setting is named `agentWorkspaceEnabled`; it defaults to true and can be changed independently of Debug. Only enabling World requires master Debug controls to be enabled in the UI.

Every PowerShell response includes an authoritative post-command `state` snapshot—even after an error—while `data` contains the deeper result for that command. This lets scripts verify where the app actually ended up instead of assuming a command succeeded visually.

Generate and replay complete setups from PowerShell:

```powershell
New-AIArenaMatch random -Style technical -Intensity sharp -Seed DEMO-01
New-AIArenaMatch ai -Prompt "Design a difficult architecture tradeoff"
New-AIArenaMatch current -Query "latest AI regulation and safety news"
New-AIArenaMatch wild -RolePack absurd_lab -ConfirmWild

$generation = Get-AIArenaMatchGeneration
$generation.data.qualityContractPresent
$generation.data.globalInstruction
$id = $generation.data.history[0].id
Invoke-AIArenaMatchReplay $id
Invoke-AIArenaMatchReplay $id -NewSession
```

Every newly generated setup carries a compact quality contract: define a good outcome, name an unacceptable failure, test an edge case, and finish with an actionable output plus unresolved uncertainty. Match Setup shows this as a **Criteria: Auditable** readiness badge; legacy or manually edited scenarios without it show a nonblocking **Criteria: Basic** warning. Agent turns must make one observable contribution—evidence, assumption test, option comparison, constraint, action, or synthesis—and check the contract before endorsing closure. `Get-AIArenaMatchGeneration` exposes both the full instruction and `qualityContractPresent` so scripted evaluations can verify this behavior directly.

Random and Wild modes are local and `seed_deterministic`: the same seed, recipe, and active cast reproduce the same setup. AI Choice calls the configured narrator model. Current Topics performs a real Internet search and then calls the narrator model, so Internet Access must be enabled. AI Choice and Current Topics are `captured_output_replayable`: their saved output replays exactly, but their synthetic seed labels do not promise that a fresh model/web call would regenerate the same setup. Each PowerShell history item and generation receipt exposes `seedDeterministic` and `replayMode`; receipts also return the saved `historyId` immediately. Same-session replay preserves the transcript; `-NewSession` creates a clean comparison run and selects it. Every result contains a generation receipt, refreshed history, and the uniform app state.

Every authenticated call, including a rejected argument or missing reset confirmation, includes a consistent `$result.state` summary alongside command-specific `$result.data`, so scripts can always inspect the current view, session, arena, Internet, right rail, provider, Agent, and Collaborate status after an action. Authentication failures do not expose app state.

Saved sessions and recovery points use the same storage path from the UI and PowerShell:

```powershell
$saved = Get-AIArenaSession
$saved.data.sessions | Format-Table id, active, messageCount, checkpointCount
New-AIArenaSession "comparison-run"
$branch = New-AIArenaSessionFork
$branch.data | Format-List sourceSessionId, forkSessionId, turnCount, messageCount
$namedBranch = New-AIArenaSessionFork "alternate-path"
$checkpoint = New-AIArenaCheckpoint "before model change"
Restore-AIArenaCheckpoint $checkpoint.data.checkpoints[0].id
Select-AIArenaSession default
```

`New-AIArenaSession` copies only the current setup into a clean run. `New-AIArenaSessionFork` instead preserves the complete persisted current match—transcript, narration, private notes, research/source metadata, attachments, provider configuration, generation history, locks, and next-turn position—while resetting only transient thinking/error statuses. It creates a collision-free independent branch, selects it, and leaves the parent snapshot byte-for-byte and revision unchanged. Omit the name for `<source>-fork-t<turn>` naming. The Saved State surface shows `Forked from <parent> at turn N` and provides **Open parent** when it still exists.

Current-match forking is additive and needs no confirmation, but it is refused while the arena is busy. It branches the authoritative current snapshot, not an arbitrary old transcript turn. Historical-turn branching will require exact per-turn snapshots so later private notes and configuration are never misrepresented as past state. Fork receipts contain lineage IDs, revisions, counts, and time only; provider credentials and transcript content are not returned.

Checkpoint restore replaces the active session state, so the protocol requires explicit confirmation and the PowerShell wrapper uses `ShouldProcess`. Each result also includes the uniform fresh app-state summary.

### Durable Agent Runbook

Each Agent task creates a persisted runbook in the right rail with one stable run ID and six auditable steps: `plan`, `review`, `build`, `approval`, `execute`, and `verify`. Every row shows its owner and status. Model phase completion, command approval/rejection, execution, file receipts, verification, cancellation, and restart interruption update the same runbook rather than a separate progress model.

Runbooks are workspace-bound. Changing the Agent workspace starts with an empty runbook, while reopening the same workspace restores its current run. A step that was `Running` when the app stopped is restored as `Blocked` with an interruption checkpoint; it is never silently rerun. Use PowerShell to inspect, resume, or annotate it:

```powershell
$runbook = Get-AIArenaRunbook
$runbook.data.steps | Format-Table sequence, id, owner, status
$runbook.data.checkpoints | Format-Table id, kind, summary

Add-AIArenaRunbookCheckpoint "Reviewed the generated files" -Kind review
Resume-AIArenaRunbook
```

Resume stages an editable prompt for the first incomplete step. Command preview and approval rules remain unchanged.

Control the visible shell and transcript layout:

```powershell
Set-AIArenaRightRail show
Set-AIArenaViewPreset diagnostics
Select-AIArenaView arena
```

Control an arena run and Internet access:

```powershell
Get-AIArenaInternet
Set-AIArenaInternet $true
Test-AIArenaInternet
Invoke-AIArenaArena turn
Invoke-AIArenaArena narrate
```

Arena reset is intentionally non-interactive but requires an explicit destructive-action flag:

```powershell
Invoke-AIArenaArena reset -ConfirmReset
```

Use the set-all provider-model shortcut and select the Agent workspace:

```powershell
Set-AIArenaProviderModel "google/gemma-4-e2b"
Set-AIArenaWorkspace "C:\AI Workspace\Local LLM"
```

Ask the model-driven Agent to build something:

```powershell
Invoke-AIArenaAgent send -Prompt "Create a small calculator app in this workspace."
Invoke-AIArenaAgent state
Invoke-AIArenaAgent approve
```

`agent.send` asks Planner/Reviewer/Builder to respond. If Builder stages a valid command, `agent.approve` runs that staged command through the same preview and workspace-boundary checks used by the UI. If no command is staged, inspect state and build evidence:

```powershell
Invoke-AIArenaAgent build.evidence
Invoke-AIArenaAgent command.state
Invoke-AIArenaAgent work.brief
```

You can also stage a known command into the normal Agent approval rail. This is useful for debugging, scripted demos, or recovering when a local model produces prose instead of a runnable proposal. It still uses Agent preview validation and still needs approval unless Full Access is active.

```powershell
Set-AIArenaAgentCommand "Get-ChildItem" -Shell PowerShell
Invoke-AIArenaAgent command.state
Invoke-AIArenaAgent approve
```

For multi-line writes, pass a PowerShell command string. The staged command should write inside the selected workspace:

```powershell
$command = @'
New-Item -ItemType Directory -Force -Path .\demo-app | Out-Null
Set-Content -Path .\demo-app\index.html -Encoding UTF8 -Value "<!doctype html><title>Demo</title><h1>Hello from AI Arena</h1>"
'@

Set-AIArenaAgentCommand $command -Shell PowerShell
Invoke-AIArenaAgent approve
Invoke-AIArenaAgent outputs
```

Stage follow-up prompts from the latest command output:

```powershell
Invoke-AIArenaAgent stage.next
Invoke-AIArenaAgent stage.verify
Invoke-AIArenaAgent stage.artifact
```

Watch live app events as JSON:

```powershell
Watch-AIArena events
```

Export useful state:

```powershell
Export-AIArena session
Export-AIArena transcript
Export-AIArena receipts
```

Useful navigation and shell helpers:

```powershell
Select-AIArenaView arena
Select-AIArenaView agent
Select-AIArenaView collaborate
Set-AIArenaTheme "Dark Blue"
Invoke-AIArenaArena start
Invoke-AIArenaArena stop
Invoke-AIArenaArena operator.send -Prompt "Ask for stronger evidence." -Route public
Invoke-AIArenaCollaborate send -Prompt "Compare these implementation options."
```

The complete command map is documented in the repository-root `CONTROLPLANE.md`.

## Operator Turn

Operator Turn can inject a public message into the transcript, write private memory guidance to selected agents, or ask the Narrator for a public referee answer. Public turns do not advance the normal participant turn order.

Use this to clarify a topic, add constraints, correct the match, ask a question, or steer the agents without resetting the session.

The draft meter shows character count, estimated tokens, and the current route. Hover the meter or route hint for an Operator Draft receipt with destination, visibility, prompt text, and the next check to perform after sending.

Quick intervention chips appear above the Operator Turn editor. They stage editable prompts such as evidence requests, consensus breakers, private role resets, narrator judgments, repair prompts, handoff notes, and next-step framing. The suggestions are local and deterministic: they are generated from the current transcript diagnostics, error state, and run status. Clicking a chip changes the route when needed, for example private role reset or narrator judgment.

## AI Collaborate

AI Collaborate is a classic chat surface where the app asks a small team of model roles to work together on your prompt.

Use AI Collaborate from the left rail. The center chat shows your prompt, a clearly labeled Final Answer, a Run Review packet, and a collapsible Team Debate. Team Debate groups visible role cards by round so Alpha, Beta, Gamma, and Narrator contributions stay distinct from the final answer.

Modes:

- Fast: asks the Narrator for a direct answer.
- Team Draft: asks Alpha, Beta, and Gamma for draft work, then asks the Narrator to synthesize.
- Critique: asks Alpha to draft, Beta to critique, Gamma to refine, and the Narrator to synthesize.
- Red Team: asks Alpha to propose, Beta to attack, Gamma to harden the answer, and the Narrator to synthesize.

Rounds controls how many visible team passes run before the final answer. Fast always uses one direct round. Team Draft, Critique, and Red Team can run preset rounds or a typed value from 1 to 12, with later rounds focused on concise refinement, critique, evidence checks, hardening, and clearer next steps.

Run Review appears under each final answer. It summarizes trace health, issue count, token use, latency, model mix, payload size, outcome, and next action. Use Copy to copy the review packet, or Use to stage a follow-up prompt from the review.

PowerShell can inspect the same evidence without reopening the chat. `Get-AIArenaCollaborateReview` returns the latest saved final answer, deterministic review verdict, aggregate metrics, and every latest-turn trace step; use `-Id` for a specific saved collaboration. As with all authenticated control-plane commands, the response also includes the app's fresh post-command `State` snapshot.

The Receipt button beside the prompt previews what the run will send: run plan, prompt size, prior chat count, review packet expectation, and added document, calculation, or memory context. The prompt budget line warns when added context will be truncated before prompting.

Team roles:

- Alpha drafts practical options and tradeoffs.
- Beta critiques assumptions, risks, and weak conclusions.
- Gamma maps evidence, uncertainty, and what would change the answer.
- Narrator produces the final answer.

The provider and model assignments come from the active session settings. If a role-specific model is configured, that role uses it; otherwise it falls back to the shared provider model.

Recent Collaborations in the left rail reopen old AI Collaborate chats, including prompts, final answers, Run Review, and Team Debate cards. The rail shows saved count, health facets, quick filter chips, compact run metadata, inferred mode, model mix, open state, and review state. Use the chips to filter All, Ready, Review, No trace, Memory, or Compare-ready chats. Search can match prompt text, answers, traces, memory notes, model mix, health state, inferred mode, metrics, and generated run-review text; it also accepts lenses such as `#ready`, `#review`, `#answer`, `#notrace`, `#memory`, `#compare`, `#fast`, `#team`, `#critique`, and `#redteam`. The top-rail export icon saves the open collaboration as Markdown with its review packet and trace metadata. Right-click a recent collaboration to Open, Fork, Repeat prompt, Copy summary, Copy markdown, Copy compare, or Delete. Copy compare appears when another collaboration is open and copies a Markdown delta packet with turn, trace, issue, token, latency, model, prompt, answer, and memory-note differences. Fork keeps the saved exchanges as context but saves the next reply as a new chat. Repeat prompt stages the latest prompt in a clean draft while carrying forward memory notes. NEW starts a blank chat while keeping saved history.

AI Collaborate history is stored locally under `%LOCALAPPDATA%\AI Arena\configs\collaborate-history.json`.

## Match Setup

Match Setup controls the scenario and cast from a wide flyout inside AI Lab. The top console is grouped into Generate, Tune, Agents, and Recent so setup controls stay compact. The header shows setup readiness, readiness badges, Copy Setup, Copy JSON, and Import JSON actions. Tune shows a recipe summary so you can see the selected pack, style, pressure, persona mixer, preset category, best-use guidance, and risk notes before generating.

- Generate chooses how to create a setup: Manual, Random Seed, AI Choice, or Wild Seed.
- Tune chooses the role pack, scenario style, debate pressure, and absurdity level.
- Agents resizes the active cast for duel, classic, council, swarm, or custom runs.
- Recent replays, forks, or copies previously generated setups stored in the current session. The selected entry shows cast size, setup summary, and lock warnings when the current match may preserve locked fields during replay.

Each group has a small `?` help button for an in-place explanation.

Scenario Preview includes:

- Topic.
- Global instruction.
- Setup Profile.
- Run Shape.
- Relationship Map.
- Lock Plan.
- Setup Source.
- Run Constraints.

Run Shape shows the active cast handoff into the Narrator and the turn budget. Relationship Map shows active relationship pressure only, filtering out inactive, neutral, or invalid draft rules. Lock Plan names setup fields that generation should preserve. Setup Source shows scenario/persona seed provenance and recent generated setup history.

Copy Setup copies the current match setup as a readable brief with readiness, topic, global rules, recipe, preset match, run shape, relationship map, locks, cast, narrator, and provider context. Copy JSON copies the exact setup as a portable `ai_arena.match_setup.v2` package. Import JSON reads that format from the clipboard, validates all fields before writing anything, then creates and selects a clean session. It never replaces the current run. Packages carry scenario, generation tuning, cast, narrator, locks, relationship pressure, context, Internet policy, and non-secret provider settings; API tokens and runtime transcript/history are excluded.

Readiness separates blockers from warnings. Blockers include too few active agents, missing topic or global rules, no shared provider model or complete active role-model assignment, or an enabled relationship matrix with no active normalized rules. Warnings include provider offline/error state, blank active agent personas, and a blank narrator persona. The header badges summarize State, Agents, Provider, Personas, Narrator, Matrix, Locks, and History. A visible preflight checklist lists required blockers and advisory warnings directly under the badges so issues are not hidden in the tooltip.

Cast Preview includes:

- Alpha.
- Beta.
- Gamma.
- Delta.
- Narrator.

Each item can be edited and locked. Locked cards use a golden border and lock glyph. Locked content is preserved when generating new seeds.

Cast cards also include compact Pressure and Voice dropdowns. Pressure profiles change how hard each agent pushes the debate: Calm, Assertive, Contrarian, Evidence-first, Risk-first, Concise, Expansive, or Chaos. Use this to make one model stabilize the discussion while another challenges assumptions, demands evidence, or expands the mechanism.

Voice styles are reinforced as per-turn contracts that stay separate from the persona, such as Scientific, Legal / Policy, Plain language, Idioms, Cute, Poetic, Socratic, Bullet-only, Skeptical, Executive brief, Evidence ledger, No analogies, Hedge uncertainty, Bark-only, and Science gibberish. Use them to test whether a model can preserve reasoning quality while speaking in a constrained style.

Transcript cards and Agent Performance detail also show the active Voice chip for non-default styles, so constrained turns remain visible while reviewing a match.

AI Arena also scores non-default voices with a lightweight Style Fit meter. This is experimental, so the transcript and Turn Compare cue chips only appear when Settings -> Visuals -> Allow debug controls is enabled and Debug -> Style fit is turned on. Agent Performance keeps the fuller style cue details. Scores are heuristic: strong cues mean the response visibly followed the selected style, partial cues mean some markers were present, and low cues mean the style was not very visible. If a model drifts too quickly, enable Debug -> Voice drift enforcement to add stronger style reminders to future turns.

### Random Seed

Random Seed generates a deterministic scenario and cast for the same seed, recipe, and active cast. Use Preset for quick setups such as Hostile review, Evidence trial, Consensus trap, Chaos room, One-line mayhem, Model duel, Red-team gauntlet, Tool reliability trial, Governance board, Weird science panel, Product trust room, Black-Box Audit, Approval Maze, Launch War Room, Template Forge, or Memory Handoff. Hover a preset to see its category, best use, risk note, and exact tuning recipe. Selecting any individual generation dropdown returns the preset to Manual.

Use Role pack to choose Auto, Balanced, Red team, Scientific review, Technical architecture, Safety audit, Legal / policy, Incident response, Product risk, Benchmark duel, Governance board, Tool ops, or Absurd lab. Absurd lab draws from a seeded-shuffle 50+ role library and assigns each generated role its own expertise leak, useful function, voice constraint, reasoning distortion, and blind spot. Use Style to choose Auto, Balanced, Adversarial, Technical, Scientific, Research, Product, Safety, Philosophical, Legal, Creative, Red-team, or Incident. Use Pressure to choose Normal, Sharp, Spicy, Chaos, or One-line. Use Absurdity to mix expertise, voice constraints, and reasoning distortions. Locked topic, global instruction, or cast members are preserved.

Absurdity levels:

- Grounded keeps voices and roles practical.
- Odd adds stylized but still useful voice constraints.
- Absurd creates visible mismatches such as a technical expert speaking through a strange expression layer.
- Maximum pushes the Persona Mixer into stress-test territory.

Intensity changes the pressure applied to the generated setup:

- Normal keeps the scenario practical and controlled.
- Sharp increases visible disagreement and assumption testing.
- Spicy adds hidden incentives, uncomfortable tradeoffs, and weaker evidence.
- Chaos adds partial information and unstable constraints that agents must stabilize before converging.
- One-line asks each agent for one high-signal sentence per turn, useful for testing short-form debate and chat-room style payloads.

Absurd Lab cast cards show a small `?` inspector button when the generated persona contains extra role metadata. The card preview stays compact; use the inspector or hover the persona text to inspect constraints such as absurd function, expertise leak, expression constraint, and reasoning distortion without expanding the row.

### AI Choice

AI Choice asks the configured model to generate a scenario and cast. Click AI Choice in Match Setup to open the topic prompt, then enter a subject, current issue, or theme; leave it blank to let the model choose freely. Locked fields are preserved, and missing cast members are filled automatically.

### Wild Seed

Wild Seed generates a more experimental scenario seed. It can touch the topic, global instruction, cast personas, and the way the simulation is described to the models, while respecting locks. The narrator is not treated as a normal Wild Seed cast target.

### Seed Inspector

The seed inspector shows the source, scenario seed, scenario style, intensity when available, persona seed, and persona style. Use it to understand what kind of generated setup you are running.

The generation history picker keeps recent Random Seed, AI Choice, Current Topics, and Wild Seed setups. Use the filter to narrow Recent to one generation type. Replay restores the selected generated setup without making another model call, while preserving the transcript. New Run creates a clean comparison run from the selected setup. Copy Seed copies deterministic Random/Wild seeds; AI Choice and Current Topics entries copy the replay id because their complete captured setup—not a reproducible model/web seed—is stored in the session snapshot. Copy Brief copies a readable setup summary, Copy Spec copies a JSON setup receipt with tuning, preset matches, seeds, determinism class, replay mode, scenario details, cast preview, current replay lock impact, diff flags, and rubric checks. Copy Diff copies a current-vs-generated setup review, and Rubric copies an eval-style scorecard for judging the match.

### Relationship Matrix

The Relationship Matrix adds one private pressure rule per active participant. Use a draft pattern such as Round-robin challenge, Mutual rivals, Evidence ladder, Support chain, De-escalation ring, Devil's triangle, Skeptic sweep, Paired crossfire, or Spotlight defense to fill the matrix quickly, then press Apply Matrix to save it. The pressure graph preview updates while you edit targets, stances, patterns, and Enable state. It shows active edges, coverage, neutral sources, target hotspots, mutual pairs, and invalid-rule warnings before you save. Stances include Challenge, Support, Steelman, Cross-examine, Rival, Fact-check, Amplify, De-escalate, and Devil's advocate.

## Sessions, Checkpoints, and Templates

Sessions are saved locally under `%LOCALAPPDATA%\AI Arena\sessions`. Use the session controls to create, switch, or delete sessions.

Use **Fork current run** in Match Setup > Saved State when you want to preserve the complete current run and explore a different next turn. AI Arena switches to the new branch immediately, labels its direct parent and branch turn, and keeps **Open parent** available while that parent session exists. Forks are independent: later turns or edits in a branch do not alter its parent. A normal Session **Save** remains the clean-session path when you want the same setup without the transcript or live run state.

Restore points save the current transcript, cast, locks, provider settings, and arena state under `%LOCALAPPDATA%\AI Arena\checkpoints`. Use them before risky edits or long Auto Chat runs.

Scenario templates save match framing, cast, locks, participants, and model assignments for reuse under `%LOCALAPPDATA%\AI Arena\templates`.

App settings and AI Collaborate history are saved under `%LOCALAPPDATA%\AI Arena\configs`. Exports, logs, and cache files have their own folders under the same AI Arena data root.

## Settings

Settings are grouped into collapsible sections.

### Models & provider

The primary setup path stays short: choose a provider, choose the default model, then press **Test connection**. The app refreshes advertised models while Settings is open, and you can type a model identifier manually when it is not advertised.

Optional controls are grouped separately:

- **Custom connection**: connection type, server address, and optional access token. OpenAI-compatible, LM Studio native, and Ollama native connections are supported.
- **Saved setups**: save or reuse provider and role-routing configurations. Access tokens are never included.
- **Role routing**: assign Alpha, Beta, Gamma, Delta, and Narrator individually, add per-role temperature or response-limit overrides, test role-model access, or make every role follow the default model again.
- **Model recommendations**: scans GPU/RAM and provider models, then recommends a conservative, balanced, performance, max-variety, low-VRAM, or Absurd Lab spread. **Use recommendation** saves it to the current session. LM Studio still controls final GPU offload and device placement.
- **Local model tools**: preload, unload, download, or pull models when using an LM Studio or Ollama native connection.
- **Advanced model calls**: timeout, temperature, response token limit, provider context, reasoning, idle unload, and LM Studio stateful-chat behavior.

The footer's **Save session changes** action saves model-call, context, and internet changes to the current session. Appearance and Agent workspace preferences save immediately.

### Auto Chat

Controls Auto Chat cadence and related run behavior.

### Agent workspace

The Agent workspace toggle shows or hides the default-on Agent entry in the left rail. It is an independent navigation preference, not a Debug feature, and saves immediately.

### Visuals

Controls visual theme behavior, avatars, top strip mode, and related shell preferences. Allow debug controls gates experimental features such as AI World, which stays off by default; it does not gate Agent workspace visibility.

### Internet Access

- Use internet: enables app-level internet tooling. When on, agents and narrator can use local search and fetch whenever current or external facts would support their claims. When off, the app blocks internet tooling.
- Local search starts or reconnects to the bundled SearXNG backend automatically. The status under the toggle shows whether search is ready.
- `web_search` accepts normal keywords, quoted phrases, or questions. `fetch_url` retrieves the exact public HTTP or HTTPS page requested, follows safe redirects, and extracts readable text.
- Test Internet works without an active arena session and even while Internet Access is off. It starts or reconnects to local search, runs a real JSON search, fetches a safe public HTTPS page, and reports latency, result and engine counts, payload revision/path, and repair guidance.

Search can narrow results with a language code and a `day`, `month`, or `year` time range. AI Arena ranks and deduplicates the returned sources, favors independent domains and useful excerpts, and enriches a small bounded set of top pages. Search and fetch results are treated as untrusted evidence and fed back into the same agent turn. Supported factual claims should use numbered citations such as `[1]`; the transcript shows the final natural reply, while source details stay available in message metadata for debug/export.

Public-page fetching accepts only credential-free HTTP or HTTPS URLs. It blocks local/private/special-purpose network destinations, revalidates every redirect and DNS result, pins connections to validated public addresses, and caps redirects and decompressed response size. Exact page fetching does not crawl the rest of a site.

There is no separate feed or briefing workflow. Current-event questions use the same general web search and page-fetch path as every other internet request.

### Context Windows

Controls transcript, private, and memory note windows used by prompt construction. The current context summary helps confirm what will be sent to models.

### Help / About

Shows the app description, author, code credit, licence summary, and copyright notice.

## Licensing

AI Arena is distributed under the Shareable No-Derivatives Software Licence 1.0.

Copyright (c) 2026 Dominik Fiala.

You may share AI Arena freely in its original, unmodified form. You may use it privately. You may not distribute edited, modified, forked, patched, rebuilt, or derivative versions without written permission from Dominik Fiala.

The installer shows the licence during setup and installs the self-contained app to `%LOCALAPPDATA%\Programs\AI Arena` by default, which avoids both a separate .NET installation and the normal administrator prompt for clean per-user installs. You can still choose a different install directory during setup. `LICENSE`, `NOTICE.md`, release notes, the release manifest, and this user guide are installed beside the app. The Start Menu folder includes shortcuts for the app, user guide, release notes, and GitHub releases. Saved settings, sessions, checkpoints, templates, exports, logs, and cache files remain separate under `%LOCALAPPDATA%\AI Arena` unless you choose to delete leftover data during uninstall.

For an automated silent full install, pass `/TYPE=full /SEARXNGLICENSE=accept` to acknowledge the optional SearXNG AGPL licence explicitly. Without that exact flag, a silent full install stops rather than assuming acceptance. The compact app-only installation does not install SearXNG and does not require the flag.

## Provider Troubleshooting

If LM Studio or another provider is closed, the app may show a provider unreachable message.

Check:

- LM Studio server is running.
- A model is loaded.
- The base URL and port are correct.
- The selected connection type matches the provider: OpenAI-compatible `/v1`, LM Studio native `/api/v1`, or Ollama native `/api`.
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
- Lock scenario or cast fields before trying Random Seed, AI Choice, or Wild Seed.
- Turn Internet Access on when you want agents and narrator to use local search/fetch, and off when the run should stay offline.
- Use Memory notes to preserve durable agent-specific facts.
- Use Quality timeline to spot drift or evidence weakness.
- Use Agent Performance popups to inspect slow or noisy participants.
- Save a restore point before a long run.
