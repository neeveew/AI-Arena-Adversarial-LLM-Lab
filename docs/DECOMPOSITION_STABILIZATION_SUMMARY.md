# WPF Decomposition Stabilization Summary

Date: 2026-06-02

This summary captures the MainWindow decomposition and stabilization checkpoint after the WPF shell cleanup.

## What Changed

- Flattened the native WPF/shared .NET source layout into the current `src` structure.
- Removed obsolete planning docs and refreshed repository architecture docs.
- Moved saved-state, session template, checkpoint, provider settings, provider reachability, scenario generation, transcript search/export/mutation/view, transcript list, app settings, arena operation, news panel, custom match summary, scenario seed inspector, and session overview workflows out of `MainWindow.xaml.cs`.
- Consolidated common shell card and brush helpers.
- Moved Windows DWM/native chrome interop into `WindowChromeService` under `Platform/Windows/Theming`.
- Added `docs/MAINWINDOW_DECOMPOSITION.md` as the ownership map for the WPF shell.
- Refreshed `docs/DEPENDENCY_INDEX.md`.

## Current Shape

`MainWindow.xaml.cs` is now primarily a composition root. It wires services, timers, coordinators, and XAML event handlers, then delegates feature behavior to focused Shell coordinators.

The remaining MainWindow surface is intentionally thin:

- snapshot load/render orchestration
- coordinator construction and dependency injection
- compatibility wrappers for delegate signatures
- simple XAML event delegation
- process-level links such as releases/user guide launch

## Stabilization Coverage

The WPF smoke tests now cover coordinator helper contracts for:

- provider reachability popup state
- shell navigation theme selection
- app settings provider focus decisions
- transcript list retryable-turn selection
- transcript view preset normalization
- custom match summary fallback text
- scenario seed inspector metadata
- provider quick setup defaults
- news panel summary counts
- cross-coordinator render smoke state for offline provider, blank scenario, internet/news items, and retryable transcript turns

## Verification Used

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
dotnet run --project .\tests\AIArena.Tests\AIArena.Tests.csproj
dotnet run --project .\tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj
.\scripts\dependency-index.ps1 -Check
.\scripts\wpf-release-sanity.ps1 -Version "0.3.95-beta"
```

Additional startup smoke checks were run against:

- `src\AIArena.Wpf\bin\Debug\net10.0-windows\AI Arena.exe`
- `dist\AI Arena - 0.3.95-beta\AI Arena.exe`

## Recommended Next Work

- Start feature work from the coordinator map, not from `MainWindow.xaml.cs`.
- Keep new WPF behavior inside the owning coordinator or a new focused coordinator.
- Add pure helper coverage for display, filtering, status, and visibility decisions.
- Use release sanity before publishing installer builds.

## Progress Since Checkpoint

2026-06-03:

- Added the AI Collaborate navigation surface as a focused shell feature owned by `CollaborateCoordinator`.
- Added persisted AI Collaborate history through `CollaborateHistoryStore` under `%LOCALAPPDATA%\AI Arena\configs\collaborate-history.json`.
- Updated shell navigation to switch Collaborate-specific top bar, right rail, and left rail context.
- Updated generated Collaborate chat surfaces to refresh against active theme resources.
- Added selectable AI Collaborate rounds for visible multi-pass team refinement before final synthesis.
- Renamed the left-rail Transcript entry to AI Lab and moved Custom Match into the top-rail Match Setup flyout.
- Refreshed the generated dependency index after the new shell files landed.
