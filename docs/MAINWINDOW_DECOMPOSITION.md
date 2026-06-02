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
| `TranscriptSearchCoordinator` | transcript search popup, filters, drag behavior |
| `TranscriptExportCoordinator` | transcript export/copy workflows |
| `TranscriptInsightCoordinator` | turn compare and timeline filter state |
| `TranscriptActionCoordinator` | transcript action button creation and busy state |
| `TranscriptMutationCoordinator` | delete, pin, and retry-adjacent transcript mutation rules |
| `TranscriptCardRenderer` | individual transcript card rendering |
| `TranscriptAdjunctCoordinator` | decision cards, diagnostics adjuncts, news inspector cards, auto-moderator panels |
| `TranscriptViewCoordinator` | transcript visual settings, presets, dashboard layout, debug/view menus |
| `TranscriptListCoordinator` | transcript list orchestration, ready card, empty-search state, panel ordering |
| `NewsPanelCoordinator` | internet/news panel filtering, summaries, fallback/empty state |
| `AgentBoardCoordinator` | active agent board rendering and agent-turn actions |
| `AgentMemoryCoordinator` | private notes and memory-note panel workflows |
| `AgentPerformanceCoordinator` | participant performance panel and detail popup |
| `AgentRosterCoordinator` | active participant count controls |
| `ArenaOperationCoordinator` | app-wide arena busy state, operation locking, control enable state, breathing buttons |
| `ArenaRunCoordinator` | one-turn, auto-chat, narrator, retry, approval-resume run workflows |
| `ArenaSessionMutationCoordinator` | reset/apply session settings and core snapshot mutation helpers |
| `SessionOverviewCoordinator` | top bar, overview metrics, provider status summaries |
| `ProviderSettingsCoordinator` | provider settings, model routing, auto-configure, preload workflows |
| `ProviderReachabilityCoordinator` | provider health popup, refresh timer, shared provider status persistence |
| `ProviderQuickSetupCoordinator` | ready-state provider setup card |
| `AppSettingsCoordinator` | app settings visibility, provider settings navigation, model refresh timer, gear animation |
| `ShellNavigationCoordinator` | theme application, navigation, settings panel visibility |
| `TelemetryWorkflowCoordinator` | system telemetry widgets and timer state |
| `DiagnosticsWorkflowCoordinator` | diagnostics dashboard, sparkline values, detail popup |
| `MatchQualityTimelineCoordinator` | transcript quality timeline panel |
| `ScenarioWorkflowCoordinator` | random seed, AI choice, YOLO generation, generation history |
| `ScenarioSeedInspectorCoordinator` | scenario/persona seed metadata chips |
| `CustomMatchSummaryCoordinator` | scenario topic/global and cast/narrator preview cards |
| `MatchLockCoordinator` | lock/edit controls, voice style and pressure pickers |
| `MatchSetupCoordinator` | rivalry matrix rendering and persistence |
| `InternetWorkflowCoordinator` | internet settings, curated news, approval dialogs |
| `OperatorTurnCoordinator` | operator route, template, private target, and send workflows |

## Platform Helpers

| Helper | Owns |
| --- | --- |
| `WindowChromeService` | Windows DWM caption, border, and text color interop |

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
