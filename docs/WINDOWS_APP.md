# AI Arena - WPF

This folder contains the native WPF AI Arena app.

The current WPF shell includes:

- native Windows desktop app
- independent transcript and Arena scroll panels with slim theme-aware scrollbars
- native session loading and atomic writes under `%LOCALAPPDATA%\AI Arena\sessions`
- session picker for available snapshots
- 1.2 second refresh for snapshots changed by another process
- compact top bar with session/provider status, theme selector, and settings icon
- default-on, first-class Agent left-rail navigation surface with an independent **Settings -> Agent workspace** visibility toggle and no Debug dependency, plus a Codex-inspired centered conversation lane, bottom composer, visible workspace-session Full Access autonomy, plus-menu popup for prompt presets/session controls, collapsed Workspace and Advanced drawers, persisted project folder, capped workspace project profiling, Planner/Reviewer/Builder software chat, compact right-rail Progress/Build Evidence/Outputs cards, app-building prompts, next-step, verify, rescue follow-up prompts, center action/result/warning/session-autonomy cards, file-snippet materialization, generated artifact suggestions, fenced/XML/JSON command proposal extraction for local-model replies, bounded Auto Rescue for prose-only app replies, loop-guarded Auto Continue follow-up loops, and command proposal workflow
- Agent Advanced drawer with Terminal/PowerShell command approval, working-directory preview validation, parent-path/scaffold target/output option/absolute path/dynamic write-path blocking, risk chips, command-source labels, approve/stop/reject actions, Auto Continue tuning, risky-preview manual stops under autonomy, command history replay/copy, copy/clear/use-held/use-artifact command actions, completed-command editor cleanup, copyable output/receipt/work-brief actions, Stage Next/Repair/Retry and Stage Verify handoffs with suggested preview commands, stdout/stderr capture, exit/timeout/cancel status, changed-path previews, expected no-change verification handling, and file-change receipts
- AI Collaborate navigation surface with classic chat, team-draft/fast/critique modes, selectable collaboration rounds, Team Debate role cards, and provider/model status
- persisted AI Collaborate chat history with left-rail quick filters, tokenized search, inferred mode metadata, health badges, fork, repeat prompt, copy, compare-to-open, and delete actions
- native dark title bar/app border on supported Windows builds
- settings overlay with collapsed sections, a normal default-on Agent workspace visibility toggle, Debug-gated/default-off AI World controls, and active participant count under Model Provider
- provider health test through the configured OpenAI-compatible endpoint
- first native `1 TURN` path through the shared core service layer
- operator turn injection from WPF, kept available during other arena operations
- transcript copy, pin/unpin, delete, model reasoning display, generated/context token pills, and agent-tinted framed transcript cards
- newest-first transcript rendering with follow-chat pinned to the latest card
- transcript retry as a targeted one-shot by the original agent
- Battle Review Run Trace triage with severity, focus, categorized issue counts, review queue, and copyable trace packets
- per-agent one-shot turns that do not advance the normal turn order
- Auto Chat loop with Stop cancellation
- Auto Chat cadence selector in the App Settings overlay
- active command buttons use subtle breathing feedback during operations
- Reset clears transcript/live turn state while preserving scenario, cast, settings, and checkpoints
- top settings icon opens a translucent roll-down overlay above the Arena panel
- editable WPF App Settings for active participants, provider URL, default model, per-role Alpha/Beta/Gamma/Narrator models, timeout, temperature, max output, Internet Access, and context windows
- top-bar Theme selector with System, Dark Green, Green, Dark Blue, and High Contrast palettes
- persisted WPF-local settings in `%LOCALAPPDATA%\AI Arena\configs\native-wpf-settings.json`
- persisted Agent workspace path in `%LOCALAPPDATA%\AI Arena\configs\native-wpf-settings.json`
- persisted AI Collaborate history in `%LOCALAPPDATA%\AI Arena\configs\collaborate-history.json`
- Match Setup flyout inside AI Lab for scenario, cast, preset gallery metadata, readiness badges, visible preflight checklist, setup receipts, pressure graph preview, and lock status
- Match Setup lock toggles for topic, global, and cast members
- Random Seed match generation with categorized presets, rich preset tooltips, visibly refreshed cast roles, and personas
- AI Choice match generation through the configured narrator/shared model, with fallback cast completion
- Current Topics generation through live Internet search plus the configured narrator/shared model, saved as captured-output replayable history
- portable Match Setup v2 JSON export/import with secret-free provider settings, atomic clean-session creation, fingerprints, and PowerShell parity
- Match Setup checkpoint save/restore/delete, clean-session copy, full-state current-run fork with direct-parent navigation, and relationship pressure graph controls
- Narrate Now model call into the transcript with reasoning metadata
- Internet Access gives agents and narrator general SearXNG-backed web search plus exact public-page fetching when current or external facts matter, with an independent Test Internet diagnostic, bounded source enrichment, domain-aware ranking, and numbered citation context
- hidden internet metadata on final transcript messages for debug and export
- readable-page extraction and a bounded, session-scoped internet tool cache
- role-blind native participant prompting: agents see their own persona, but other participants only by public name
- friendly provider errors for common unreachable, timeout, and unreadable-response cases
- create/switch/delete session controls
- themed, draggable confirmation dialogs before AI Choice and destructive session/checkpoint operations
- WPF-native empty states for transcript, scenario, and cast views
- live agent cards refreshed from the selected session snapshot
- Operator command deck with public/private/narrator routing, draft receipts, scope gates, and handoff interventions
- optional Debug-gated AI World 3D arena with animated agents, live pulse telemetry, speaker-follow camera, minimap, inspector, legend, and speech bubbles
- dark AI Arena layout direction
- no WebView or browser UI
- no dependency on the archived WinUI project
- shared .NET services from `src/AIArena.Core`

User-facing feature guidance is tracked in `docs/USER_GUIDE.md`.

## Source Layout

- `src/AIArena.Wpf/Shell` - main window composition root, focused workflow coordinators, and dialogs.
- `src/AIArena.Wpf/UI` - controls, view models, avatar helpers, and compact visual widgets.
- `src/AIArena.Wpf/Modules` - WPF app services grouped by feature area.
- `src/AIArena.Wpf/Platform/Windows` - Windows-specific settings, telemetry, and theme plumbing.
- `src/AIArena.Wpf/Assets` - app icons and packaged visual assets.

`docs/MAINWINDOW_DECOMPOSITION.md` tracks the WPF shell coordinator map. `MainWindow.xaml.cs` should stay a composition root for services, timers, and XAML event delegation; feature behavior should live in the Shell coordinators or platform helpers.

## Build

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
```

## Run

Open `AI Arena - WPF.sln` in Visual Studio and run `AIArena.Wpf`, or run the project from the command line:

```powershell
dotnet run --project .\src\AIArena.Wpf\AIArena.Wpf.csproj
```

During development, local search discovers a valid SearXNG payload beside the executable, through `AIARENA_SEARXNG_PAYLOAD_DIR`, or in the newest versioned release folder under `dist`. An installed release uses its own packaged payload.

## Preview Build

```powershell
.\scripts\build-wpf-preview.ps1
```

The preview executable is written to `dist\AI Arena WPF\AI Arena.exe`.

## Versioned Release Build

```powershell
.\scripts\build-wpf-release.ps1 -Version "0.3.42-beta" -Changes "Updated user guide"
```

The self-contained release executable, private .NET Desktop Runtime, and `changes.txt` are written to `dist\AI Arena - <version>\`. Use `-SelfContained:$false` only for deliberate developer-only framework-dependent probes; installer builds reject that mode.

## Stabilization Checks

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
dotnet run --project .\tests\AIArena.Tests\AIArena.Tests.csproj
dotnet run --project .\tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj
.\scripts\dependency-index.ps1 -Check
.\scripts\wpf-release-sanity.ps1 -Version "0.3.95-beta"
```

For shell refactors, also smoke-launch both the debug executable and the current release executable long enough to catch startup-time XAML/event wiring failures.
