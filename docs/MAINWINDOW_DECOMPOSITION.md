# MainWindow Decomposition Map

This map tracks the WPF shell coordinator split so future changes can land in the right place without re-growing `MainWindow.xaml.cs`.

## Current Shape

`MainWindow` is still the composition root for the desktop shell. It should own:

- constructor wiring for services, coordinators, timers, and XAML controls
- top-level snapshot rendering orchestration
- app-wide busy state and operation locking
- shell-level event handlers that delegate to focused coordinators
- WPF chrome/window integrations

Feature behavior should live in focused coordinators under `src/AIArena.Wpf/Shell`.

## Coordinator Ownership

| Coordinator | Owns |
| --- | --- |
| `SavedStateWorkflowCoordinator` | saved states, session templates, checkpoints |
| `SessionForkWorkflowService` | atomic current-match branching, direct-parent audit receipts, exclusive-operation coordination, and branch selection shared by WPF and PowerShell |
| `TranscriptSearchCoordinator` | transcript search popup, filters, drag behavior |
| `TranscriptExportCoordinator` | transcript export/copy workflows |
| `TranscriptInsightCoordinator` | turn compare and timeline filter state |
| `TranscriptActionCoordinator` | transcript action button creation and busy state |
| `TranscriptMutationCoordinator` | delete, pin, and retry-adjacent transcript mutation rules |
| `TranscriptCardRenderer` | individual transcript card rendering |
| `TranscriptAdjunctCoordinator` | decision cards, diagnostics adjuncts, internet source cards, auto-moderator panels |
| `TranscriptViewCoordinator` | transcript visual settings, presets, dashboard layout, debug/view menus |
| `TranscriptListCoordinator` | transcript list orchestration, ready card, empty-search state, panel ordering |
| `AgentBoardCoordinator` | active agent board rendering and agent-turn actions |
| `AgentMemoryCoordinator` | private notes and memory-note panel workflows |
| `AgentPerformanceCoordinator` | participant performance panel and detail popup |
| `AgentRosterCoordinator` | active participant count controls |
| `AgentWorkspaceCoordinator` | standalone Agent workspace, Codex-inspired centered thread/composer surface, plus-menu prompt/session controls, collapsed Workspace/Advanced drawers, workspace path persistence, capped workspace profiling, software chat orchestration, Planner/Reviewer/Builder progress rows, Build Evidence and Outputs rows, fenced/XML/JSON command proposal staging for local-model replies, completed-command rail cleanup, file-snippet materialization, generated artifact suggestions, Use Artifact staging, artifact preview/check result handling, rescue prompt recovery, Auto Rescue retries, held proposal flow, action/result/warning/session-autonomy cards, command preview/workspace-session auto-approval with risky-preview manual stops, command history/replay, work brief copy, Stage Next/Repair/Retry and Stage Verify handoffs, expected no-change verification handling, loop-guarded Auto Continue, active command cancellation, terminal output copy actions, and file-change receipts |
| `ArenaOperationCoordinator` | app-wide arena busy state, operation locking, control enable state, breathing buttons |
| `ArenaRunCoordinator` | one-turn, auto-chat, narrator, retry, approval-resume run workflows |
| `ArenaSessionMutationCoordinator` | reset/apply session settings and core snapshot mutation helpers |
| `SessionOverviewCoordinator` | top bar, overview metrics, provider status summaries |
| `ProviderSettingsCoordinator` | provider settings, model routing, auto-configure, preload workflows |
| `ProviderReachabilityCoordinator` | provider health popup, refresh timer, shared provider status persistence |
| `ProviderQuickSetupCoordinator` | ready-state provider setup card |
| `AppSettingsCoordinator` | app settings visibility, provider settings navigation, model refresh timer, gear animation |
| `ShellNavigationCoordinator` | theme application, AI Lab/AI World/Agent/AI Collaborate navigation, Match Setup flyout visibility, settings panel visibility, and section chrome switching |
| `CollaborateCoordinator` | AI Collaborate chat flow, rounds-based role orchestration, markdown rendering, recent chat restore/delete, generated theme refresh |
| `TelemetryWorkflowCoordinator` | system telemetry widgets and timer state |
| `DiagnosticsWorkflowCoordinator` | diagnostics dashboard, sparkline values, detail popup |
| `MatchQualityTimelineCoordinator` | transcript quality timeline panel |
| `ScenarioWorkflowCoordinator` | Random Seed, AI Choice, Current Topics, Wild Seed generation, determinism-aware history and replay controls |
| `ScenarioSeedInspectorCoordinator` | scenario/persona seed metadata chips |
| `CustomMatchSummaryCoordinator` | Match Setup scenario topic/global and cast/narrator preview cards |
| `MatchLockCoordinator` | lock/edit controls, voice style and pressure pickers |
| `MatchSetupCoordinator` | rivalry matrix rendering and persistence |
| `MatchSetupPortabilityService` | validated secret-free Match Setup v2 export, atomic clean-session import, fingerprints and receipts shared by UI and PowerShell |
| `InternetWorkflowCoordinator` | internet enablement and local-search health |
| `OperatorTurnCoordinator` | operator route, template, private target, and send workflows |

## Platform Helpers

| Helper | Owns |
| --- | --- |
| `WindowChromeService` | Windows DWM caption, border, and text color interop |
| `CollaborateHistoryStore` | persisted AI Collaborate chat history under the app config data root |

## Remaining MainWindow Surface

The remaining `MainWindow` code is mostly composition-root glue:

- constructor wiring for services, coordinators, timers, and XAML controls
- snapshot load/render orchestration across coordinators
- thin XAML event handlers that delegate to coordinators
- small status wrappers used by coordinator delegates
- `RunArenaBusyAsync`, `SetArenaBusy`, and `OpenModelProviderSettings` compatibility wrappers for existing delegate signatures

## Guardrails

- Keep `MainWindow` as the composition root, not as a feature service.
- Put pure formatting or visibility rules on the owning coordinator as `internal static` helpers and cover them in `tests/AIArena.Wpf.Tests`.
- Prefer constructor-injected delegates over reaching back into `MainWindow`.
- Keep XAML event handlers thin, startup-safe, and tolerant of controls firing during `InitializeComponent`.
- When extracting WPF UI factories, preserve existing `ShellCardFactory`, `TranscriptActionCoordinator`, and theme brush helpers instead of recreating card/button styles.

## Verification Pattern

For each decomposition slice, run:

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
dotnet run --project .\tests\AIArena.Tests\AIArena.Tests.csproj
dotnet run --project .\tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj
git diff --check
```

Then smoke-launch the built WPF executable long enough to catch startup-time XAML/event wiring failures.
