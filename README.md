# AI Arena adversarial multi-agent LLM lab

AI Arena is a native Windows WPF app backed by the shared .NET core library.
The previous dashboard stack has been removed from the active source tree.

## Screenshots

![AI Arena transcript view](screenshot-transcript.png)

![AI Arena custom match view](screenshot-custom-match.png)

## Projects

- `windows-wpf/src/AIArena.Wpf` - native Windows app.
  - `Shell` - main window and app dialogs.
  - `UI` - WPF controls, view models, and visual helpers.
  - `Modules` - WPF-facing feature services and adapters.
  - `Platform/Windows` - settings, telemetry, and theming integrations.
  - `Assets` - icons and packaged visual assets.
- `shared-dotnet/src/AIArena.Core` - shared domain models and services.
  - `Modules/Arena` - turn running and arena snapshots.
  - `Modules/Provider` - OpenAI-compatible provider config, client, and health checks.
  - `Modules/Sessions` - data paths, event log, summaries, and session storage.
  - `Modules/Internet` - internet tool contracts, fetching, and curated news.
  - `Modules/Diagnostics`, `Modules/MatchGeneration`, `Modules/Narration`, `Modules/Transcript`, and `Modules/Avatars` - focused core features.
- `shared-dotnet/src/AIArena.Tests` - shared .NET smoke tests.
- `docs` and `windows-wpf/docs` - product notes, port map, and user-facing guides.

## Build

```powershell
dotnet build .\windows-wpf\src\AIArena.Wpf\AIArena.Wpf.csproj
```

## User Guide

The current user guide is maintained at `windows-wpf/docs/USER_GUIDE.md`.
It covers installation, provider setup, transcript tools, Custom Match,
memory notes, match quality timeline, agent performance popups, and licensing.

## Licence

AI Arena is distributed under the Shareable No-Derivatives Software Licence 1.0.
You may share the software in its original, unmodified form. Modified
redistribution requires written permission from Dominik Fiala.

## Release Helpers

```powershell
.\windows-wpf\scripts\build-wpf-preview.ps1
.\windows-wpf\scripts\build-wpf-release.ps1
.\scripts\dependency-index.ps1 -Check
.\scripts\wpf-release-sanity.ps1
```

The generated dependency map lives at `docs/DEPENDENCY_INDEX.md`. Rebuild it with
`.\scripts\dependency-index.ps1` after moving modules, services, project
references, packages, or packaged resources.

The app stores sessions and settings under `%LOCALAPPDATA%\AI Arena Alpha\data`
for compatibility with existing local data.
