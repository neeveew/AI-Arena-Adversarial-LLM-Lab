# AI Arena WPF Control Plane

AI Arena WPF exposes a local control plane for scripts, smoke tests, and automation tools. It uses the WPF-specific local named pipe `ai-arena-wpf-control`, is enabled by default, and authenticates every request with a per-run token stored as `ai-arena-wpf-control-<user>.token` in the current user's temporary directory. The WPF namespace prevents collisions with other AI Arena implementations that may be running at the same time.

This root document is the authoritative command reference for the WPF application. Its command tables cover every entry in `AIArenaControlCapabilityCatalog`; automated tests reject undocumented commands.

## Settings

1. Open AI Arena.
2. Open Settings -> PowerShell Control.
3. Use the PowerShell control plane toggle to enable or disable automation. It is on by default and does not require Debug controls.

The PowerShell helper lives at:

```powershell
. "$env:LOCALAPPDATA\Programs\AI Arena\ai-arena-control.ps1"
```

That is the default installer location. If you chose a custom installation folder, load `ai-arena-control.ps1` from that folder. From a source checkout, load `scripts\ai-arena-control.ps1` instead.

Every authenticated command returns the same response envelope, including validation or confirmation failures:

- `ok`, `status`, and `message` report command outcome.
- `data` contains the command-specific result.
- `state` always contains the current view, theme, session, arena status, Internet state, right-rail state, provider readiness/model, Agent status, Agent runbook ID/status, and Collaborate status. Authentication failures do not expose app state.

`-TimeoutMs` bounds both the named-pipe connection and the wait for the command response. Provider diagnostics use longer typed-wrapper defaults; callers can still choose a stricter bound.

```powershell
$result = Invoke-AIArenaArena turn
$result.message
$result.data
$result.state
```

## Core Commands

| Command | PowerShell example | Notes |
| --- | --- | --- |
| `capabilities` | `Get-AIArenaCapabilities` | Returns every supported command, category, argument contract, and destructive-action flag. |
| `status` | `Invoke-AIArena status` | Returns the full app snapshot. |
| `snapshot` | `Invoke-AIArena snapshot` | Same structured snapshot as `status`. |
| `events.watch` | `Watch-AIArena events` | Streams JSON event lines until disconnected. |
| `app.screenshot` | `Save-AIArenaScreenshot` | Saves the current AI Arena window visual as PNG and returns its absolute path, byte size, pixel dimensions, and current app state. Optional `.png` path. |
| `provider.state` | `Get-AIArenaProvider` | Returns non-secret provider configuration, readiness, role routing, health timestamps, and advertised models. |
| `provider.config.set` | `Set-AIArenaProviderConfig -BaseUrl "http://127.0.0.1:1234/v1" -ApiMode lmstudio_native -Model "google/gemma-4-e2b"` | Atomically updates one or more active-session provider fields. Optional `-RefreshModels`. |
| `provider.model.set` | `Set-AIArenaProviderModel "google/gemma-4-e2b"` | Sets the shared provider model and all role-specific models. Optional `-RefreshModels`. |
| `provider.test` | `Test-AIArenaProvider [-AllRoles]` | Runs a real completion probe and persists readiness, latency, and error state. `-AllRoles` tests every distinct effective role model. |
| `provider.models.refresh` | `Update-AIArenaProviderModels` | Force-refreshes and returns the advertised model catalog. |

## Screenshots

`Save-AIArenaScreenshot` captures the AI Arena main window, including panels rendered inside that window. Separate native WPF popup windows (for example an open View or Debug menu) are not composited into the PNG. With no path, the app creates a timestamped file at `%LOCALAPPDATA%\AI Arena\exports\screenshots\AI-Arena-yyyyMMdd-HHmmss-fff.png`. When `AI_ARENA_DATA_DIR` is set, the equivalent `exports\screenshots` directory under that data root is used instead.

An optional relative `.png` path resolves beneath the same screenshots directory; an absolute `.png` path is also accepted. Existing files are never overwritten. The response includes the resolved absolute path, PNG byte size, pixel width and height in `data`, plus the standard fresh application `state` attached to every authenticated command. A polite in-app receipt shows the captured filename and full path without moving keyboard focus.

```powershell
# App-selected timestamped destination.
$capture = Save-AIArenaScreenshot
$capture.data.path

# Relative to AI Arena's screenshots directory.
Save-AIArenaScreenshot "provider/after-refresh.png"

# Explicit absolute destination.
Save-AIArenaScreenshot "C:\Screenshots\AI-Arena-provider.png"
```

## Provider control

`Set-AIArenaProviderConfig` only sends parameters that were explicitly supplied, so omitted fields are preserved. Explicit `$false`, numeric `0`, and empty role-model strings are retained rather than being mistaken for missing values. An empty role-model value clears that role's override so it inherits the shared model. `-RefreshModels` alone is not a configuration change; use `Update-AIArenaProviderModels` for a refresh-only operation.

Supported configuration fields and bounds:

- `-ApiMode`: `openai_compatible`, `lmstudio_native`, or `ollama_native`.
- `-TimeoutSeconds`: 1-3600.
- `-Temperature`: 0-2.
- `-MaxOutputTokens`: 1-32768.
- `-ContextLength`: 0-1048576, where 0 uses the provider default.
- `-Reasoning`: `default`, `off`, `low`, `medium`, `high`, or `on`.
- `-NativeIdleTtlSeconds`: 0-86400.
- Role routing: `-AlphaModel`, `-BetaModel`, `-GammaModel`, `-DeltaModel`, and `-NarratorModel`.

Provider credentials use `SecureString`. The wrapper converts the value only while serializing the local authenticated request and clears the unmanaged BSTR in a `finally` block. The credential is never returned in command data, state, or events; `provider.state` reports only whether a token is configured.

```powershell
$providerToken = Read-Host "Provider API token" -AsSecureString
Set-AIArenaProviderConfig -ApiToken $providerToken

# Remove a stored provider credential without placing plaintext in command history.
Set-AIArenaProviderConfig -ClearApiToken
```

`provider.config.set` returns the changed field names and refreshed provider state. `provider.test` returns the probe outcome, endpoint, model, reply, latency, error, check time, and refreshed state. `provider.models.refresh` returns the endpoint, API mode, check time, model count, model names, error, and refreshed provider state. A diagnostic command can complete successfully while its command-specific `data.ok` is false, which means the provider or model test failed rather than the control-plane protocol.

## Navigation

| Command | PowerShell example | Args |
| --- | --- | --- |
| `navigation.select` | `Invoke-AIArena navigation.select -View agent` | `view`: `arena`, `agent`, `collaborate`, `world`, `custom-match`, `settings`, `provider` |
| `navigation.theme.set` | `Invoke-AIArena navigation.theme.set -Theme "Dark Arena"` | `theme` or `themeId` |
| `navigation.provider.focus` | `Invoke-AIArena navigation.provider.focus` | Optional `baseUrl`, `model` |
| `navigation.rail.set` | `Set-AIArenaRightRail show` | `state`: `show`, `hide`, `toggle` |
| `view.preset.set` | `Set-AIArenaViewPreset diagnostics` | `preset`: `focused`, `diagnostics`, `compact`, `review` |

## Match Setup and settings

| Command | PowerShell example | Notes |
| --- | --- | --- |
| `match.setup.state` | `Get-AIArenaMatchSetup` | Returns overlay visibility, selected section, return view, session, match type, scenario, active cast, and busy state. |
| `match.setup.open` | `Open-AIArenaMatchSetup matrix` | Closes Settings and transient shell flyouts, then opens `scenario`, `cast`, `matrix`, or `saved` while preserving the workspace to return to. |
| `match.setup.close` | `Close-AIArenaMatchSetup` | Closes Match Setup using the same return/focus path as the UI. |
| `match.setup.export` | `Export-AIArenaMatchSetup ".\review.json"` | Returns the exact active setup as `ai_arena.match_setup.v2` JSON and optionally writes it as UTF-8. Provider API tokens and runtime history are excluded. |
| `match.setup.import` | `Import-AIArenaMatchSetup ".\review.json" -Name review-copy` | Validates JSON from `args.json` or a local `.json` `args.path`, creates a collision-free clean session, and selects it. It never overwrites the active run. |
| `match.roster.set` | `Set-AIArenaMatchRoster 6` | Resizes the active cast to 1-8 agents through the same bounded session-save, event-log, busy-state, and refresh path as Match Setup. |
| `match.matrix.state` | `Get-AIArenaMatchMatrix` | Returns enabled state, active-agent count, and every normalized source/target/stance link. |
| `match.matrix.set` | `Set-AIArenaMatchMatrix evidence_ladder` | Atomically applies a named relationship pattern; use `off` to disable and clear it. Busy and invalid mutations leave the session unchanged. |
| `match.generation.state` | `Get-AIArenaMatchGeneration` | Returns the active generation recipe, full global instruction, complete-contract `qualityContractPresent` flag, and up to 20 history receipts with `seedDeterministic` and `replayMode`. |
| `match.generate.random` | `New-AIArenaMatch random -Style technical -Seed AUDIT-1` | Local generation; supports `style`, `intensity`, `rolePack`, `absurdity`, and a seed deterministic for the same recipe and active cast. |
| `match.generate.ai` | `New-AIArenaMatch ai -Prompt "A hard deployment decision"` | Uses the configured narrator model; supports `rolePack`, `intensity`, `absurdity`, and `prompt`. |
| `match.generate.current` | `New-AIArenaMatch current -Query "latest AI safety regulation"` | Uses Internet evidence plus the configured narrator model. Internet must be enabled for the active session. |
| `match.generate.wild` | `New-AIArenaMatch wild -ConfirmWild` | Bolder local generation. The protocol requires `confirm=true`; the wrapper requires `-ConfirmWild`. |
| `match.replay` | `Invoke-AIArenaMatchReplay <history-id>` | Reapplies a generated setup while preserving the current transcript. |
| `match.replay.new` | `Invoke-AIArenaMatchReplay <history-id> -NewSession` | Creates and selects a clean comparison session from history. |
| `settings.state` | `Get-AIArenaSettings` | Returns overlay state and the visible navigation, debug, transcript, and voice preferences. Secrets are excluded. |
| `settings.update` | `Set-AIArenaSettings -AgentWorkspaceEnabled $false` | Atomically persists supported transcript, review, voice, and optional-surface preferences, then reapplies the visible UI. The `agentWorkspaceEnabled` preference defaults to true and is independent of Debug; only enabling `worldEnabled` requires master Debug controls. |
| `settings.open` | `Open-AIArenaSettings "internet"` | Opens Settings; optional query filters and expands matching sections. |
| `settings.search` | `Search-AIArenaSettings "voice"` | Updates the live Settings filter. Pass an empty string to clear it. |
| `settings.close` | `Close-AIArenaSettings` | Closes Settings using the normal focus-restoration path. |

Every authenticated command response includes a fresh post-command `state` summary, including failures. It reports the selected view and theme, session and arena status, Internet state, rail visibility, Match Setup and Settings state, provider/model state, Agent/runbook state, and Collaborate state. Command-specific detail remains in `data`.

`match.setup.open` and `navigation.select -View custom-match` share the same overlay transition. Both dismiss Settings, provider health, transcript search, View, Debug, diagnostic detail, generation help, Agent composer controls, and Agent performance detail before showing Match Setup. Their successful post-command state therefore reports `View = custom-match`, `MatchSetupOpen = true`, and `SettingsOpen = false`; closing Match Setup still returns to the workspace that was active before the transition.

Portable Match Setup packages contain exact scenario text, generator recipe, active cast personas/styles/colors, narrator behavior, locks, normalized relationship links, context windows, Internet policy, and non-secret provider configuration. API-token fields are omitted, and credentials, query strings, or fragments embedded in provider URLs are stripped. The canonical setup fingerprint excludes metadata such as the source session name. Import starts from a clean runtime: transcript, narration, attachments, research items, decision card, and generation history are not copied. A local provider token is reused only when the imported endpoint and API mode exactly match the trusted active-session configuration; otherwise the token is cleared and the receipt reports a warning. Import is unavailable while the arena is busy.

```powershell
$export = Export-AIArenaMatchSetup ".\portable-review.json"
$export.data.state.fingerprint

$import = Import-AIArenaMatchSetup ".\portable-review.json" -Name "portable-review"
$import.data.receipt.warnings
$import.state.sessionId

# Inline JSON is also accepted; the helper transports it through a short-lived local file.
Import-AIArenaMatchSetup -Json $export.data.state.json -Name "inline-review"
```

The helper removes that temporary JSON file after the synchronous import response, avoiding the named pipe's smaller request-envelope limit.

## Agent

| Command | PowerShell example | Args |
| --- | --- | --- |
| `agent.state` | `Invoke-AIArena agent.state` | Full Agent state. |
| `agent.command.state` | `Invoke-AIArena agent.command.state` | Command rail state. |
| `agent.work.brief` | `Invoke-AIArena agent.work.brief` | Latest work brief and evidence summary. |
| `agent.build.evidence` | `Invoke-AIArena agent.build.evidence` | Build evidence-focused state. |
| `agent.outputs` | `Invoke-AIArena agent.outputs` | Output/artifact summary. |
| `agent.runbook.state` | `Get-AIArenaRunbook` | Durable run ID, objective, six stable steps, owners, dependencies, statuses, evidence, and bounded checkpoints. |
| `agent.runbook.resume` | `Resume-AIArenaRunbook` | Stages a resume prompt for the first incomplete step without silently rerunning completed work. |
| `agent.runbook.checkpoint` | `Add-AIArenaRunbookCheckpoint "Reviewed build output" -Kind review` | Appends an operator-authored durable checkpoint. |
| `agent.workspace.set` | `Set-AIArenaWorkspace "C:\AI Workspace\AI Arena Workspace"` | `path` |
| `agent.send` | `Invoke-AIArena agent.send -Prompt "Build a demo app"` | `prompt` |
| `agent.command.stage` | `Set-AIArenaAgentCommand "Get-ChildItem"` | `command`, optional `shell`: `PowerShell` or `Terminal` |
| `agent.approve` | `Invoke-AIArena agent.approve` | Requests approval of the staged command. |
| `agent.reject` | `Invoke-AIArena agent.reject` | Rejects the staged command. |
| `agent.stop` | `Invoke-AIArena agent.stop` | Stops active Agent work. |
| `agent.stage.next` | `Invoke-AIArena agent.stage.next` | Stages a next-step prompt. |
| `agent.stage.verify` | `Invoke-AIArena agent.stage.verify` | Stages a verify prompt. |
| `agent.stage.artifact` | `Invoke-AIArena agent.stage.artifact` | Stages the latest artifact command. |

Convenience wrapper:

```powershell
Invoke-AIArenaAgent send -Prompt "Create a small app"
Invoke-AIArenaAgent approve
```

## Sessions and checkpoints

| Command | PowerShell example | Notes |
| --- | --- | --- |
| `session.state` | `Get-AIArenaSession` | Lists every saved session and the active session's checkpoints. |
| `session.select` | `Select-AIArenaSession default` | Loads a saved session by `id`. |
| `session.create` | `New-AIArenaSession "experiment-2"` | Copies the active setup into a clean session and selects it. Transcript and live run state are reset in the copy. |
| `session.fork` | `New-AIArenaSessionFork [-Name "alternate-path"]` | Atomically copies the complete persisted current match into a collision-free independent branch and selects it. The source is never rewritten; omitting `-Name` creates `<source>-fork-t<turn>` with a numeric suffix when needed. |
| `session.checkpoint.create` | `New-AIArenaCheckpoint "before provider change"` | Saves the active session's complete state. The name is optional. |
| `session.checkpoint.restore` | `Restore-AIArenaCheckpoint <checkpoint-id>` | Restores transcript, cast, locks, provider settings, notes, diagnostics, and turn order. The protocol requires `confirm=true`; the wrapper supplies it only after `ShouldProcess`. Use `-Confirm:$false` only for deliberate automation. |

`session.fork` is additive and therefore requires no destructive confirmation. Its receipt contains only source/fork IDs, persistence revisions, copied turn/message/narration/active-agent/generation-history counts, and fork time; it never includes prompts, transcript text, provider URLs, or credentials. `session.state` exposes the active branch's direct-parent lineage and whether that parent still exists. Forking is refused while the arena is busy. This command branches the exact current persisted state; it does not claim arbitrary historical-turn time travel.

The other commands return the refreshed session/checkpoint inventory in `data`; every command also returns the uniform fresh app summary in `state`.

## Arena

| Command | PowerShell example | Args |
| --- | --- | --- |
| `arena.start` | `Invoke-AIArenaArena start` | Starts Auto Chat and returns immediately. |
| `arena.stop` | `Invoke-AIArenaArena stop` | Stops Auto Chat. |
| `arena.turn` | `Invoke-AIArenaArena turn` | Runs one model-driven turn and waits for completion. |
| `arena.narrate` | `Invoke-AIArenaArena narrate` | Runs the narrator and waits for completion. |
| `arena.reset` | `Invoke-AIArenaArena reset -ConfirmReset` | Clears transcript/live state. Explicit `confirm=true` is required; setup and checkpoints are preserved. |
| `arena.operator.send` | `Invoke-AIArenaArena operator.send -Prompt "Push on the weak assumption" -Route public` | `prompt`, optional `route`: `public`, `private`, `narrator`. Unknown routes are rejected without sending or changing the draft. |

## Internet

| Command | PowerShell example | Notes |
| --- | --- | --- |
| `internet.state` | `Get-AIArenaInternet` | Returns enabled state, active session, backend status, and latest diagnostic. |
| `internet.set` | `Set-AIArenaInternet $true` | Enables or disables Internet for the active session and persists it. |
| `internet.test` | `Test-AIArenaInternet` | Tests bundled/local search plus a safe public HTTPS fetch. |

## Collaborate

| Command | PowerShell example | Args |
| --- | --- | --- |
| `collaborate.state` | `Invoke-AIArenaCollaborate state` | Returns Collaborate state. |
| `collaborate.review` | `Get-AIArenaCollaborateReview` | Returns the newest saved run review, final answer, metrics, and full latest-turn trace. Pass `-Id` to inspect a specific saved run. |
| `collaborate.send` | `Invoke-AIArenaCollaborate send -Prompt "Compare these options"` | `prompt` |
| `collaborate.stop` | `Invoke-AIArenaCollaborate stop` | Stops the active collaboration. |
| `collaborate.fork` | `Invoke-AIArenaCollaborate fork` | Optional saved conversation `id`; defaults to newest saved run. |
| `collaborate.repeat` | `Invoke-AIArenaCollaborate repeat` | Optional saved conversation `id`; defaults to newest saved run. |

## Exports

| Command | PowerShell example | Notes |
| --- | --- | --- |
| `export.transcript` | `Export-AIArena transcript` | Returns transcript Markdown in the response payload. |
| `export.session` | `Export-AIArena session` | Returns a structured session snapshot. |
| `export.receipts` | `Export-AIArena receipts` | Returns Agent evidence, outputs, Collaborate status, and provider readiness. |

## Events

The event stream emits line-delimited JSON.

Most shell events are reported whichever way the change happened. Opening Match
Setup with `F2` or the top bar emits the same `shell.overlay.changed` as the
`match.setup.open` command, and the same holds for navigation, the right rail,
the theme, the transcript view preset, the Internet setting and diagnostic, and
the arena run loop. Watching the stream therefore shows a person using the app,
not only an operator driving it from a script. Earlier versions published these
from the command handlers alone, so anything done by hand was invisible.

Each change is still reported once, not once per route, and the publishers are
gated on the state actually changing - resizing the window does not emit a run
of `navigation.rail.changed`.

Some events remain command-only because they describe a request that arrives
with the command rather than shell state: `agent.command.staged`,
`agent.command.approved`, `agent.command.rejected`, `agent.prompt.sent`,
`agent.prompt.staged`, `agent.stop.requested`, `agent.runbook.resumed`,
`agent.runbook.checkpointed`, `agent.workspace.changed`, `arena.operator.sent`,
`arena.reset.completed`, `match.generation.changed`,
`session.saved-state.changed`, and `navigation.provider.focused`. The equivalent
UI gestures report themselves through the transcript and the Agent workspace
instead.

Current event types:

- `events.connected`
- `status.changed`
- `message.added`
- `command.staged`
- `command.running`
- `command.completed`
- `file.receipt.captured`
- `artifact.detected`
- `loop.guard.paused`
- `provider.online`
- `provider.offline`
- `provider.config.changed`
- `provider.test.completed`
- `provider.models.refreshed`
- `navigation.changed`
- `navigation.theme.changed`
- `navigation.provider.focused`
- `navigation.rail.changed`
- `view.preset.changed`
- `match.setup.exported`
- `match.setup.imported`
- `session.saved-state.changed`
- `shell.overlay.changed`
- `match.roster.changed`
- `match.matrix.changed`
- `match.generation.changed`
- `agent.workspace.changed`
- `agent.prompt.sent`
- `agent.command.approved`
- `agent.command.rejected`
- `agent.stop.requested`
- `agent.prompt.staged`
- `agent.runbook.started`
- `agent.runbook.resumed`
- `agent.runbook.checkpointed`
- `arena.run.started`
- `arena.run.stopped`
- `arena.turn.completed`
- `arena.narration.completed`
- `arena.reset.completed`
- `arena.operator.sent`
- `internet.changed`
- `internet.test.completed`
- `collaborate.prompt.sent`
- `collaborate.stop.requested`
- `collaborate.forked`
- `collaborate.repeated`
- `control.enabled`

## Smoke Test

```powershell
. "$env:LOCALAPPDATA\Programs\AI Arena\ai-arena-control.ps1"
Invoke-AIArena status
Save-AIArenaScreenshot
Get-AIArenaCapabilities
Invoke-AIArena navigation.select -View agent
Set-AIArenaRightRail show
Set-AIArenaViewPreset diagnostics
Open-AIArenaMatchSetup saved
Set-AIArenaMatchRoster 6
Set-AIArenaMatchMatrix evidence_ladder
New-AIArenaMatch random -Style technical -Seed CONTROL-SMOKE
$history = Get-AIArenaMatchGeneration
Invoke-AIArenaMatchReplay $history.data.history[0].id -NewSession
Close-AIArenaMatchSetup
Open-AIArenaSettings "internet"
Set-AIArenaSettings -CompactTranscript $true -FollowTranscript $true -TopStripMode diagnostics
Close-AIArenaSettings
Get-AIArenaSession
New-AIArenaCheckpoint "control-plane smoke"
Get-AIArenaInternet
Invoke-AIArena navigation.provider.focus
Get-AIArenaProvider
Update-AIArenaProviderModels
Test-AIArenaProvider
Export-AIArena session
Watch-AIArena events
```
