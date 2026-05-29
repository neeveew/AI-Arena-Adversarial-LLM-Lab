# AI Arena

AI Arena is a native Windows WPF app backed by the shared .NET core library.
The previous dashboard stack has been removed from the active source tree.

![AI Arena screenshot](screenshot%2001.png)

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

## Release Helpers

```powershell
.\windows-wpf\scripts\build-wpf-preview.ps1
.\windows-wpf\scripts\build-wpf-release.ps1
.\scripts\wpf-release-sanity.ps1
```

The app stores sessions and settings under `%LOCALAPPDATA%\AI Arena Alpha\data`
for compatibility with existing local data.
