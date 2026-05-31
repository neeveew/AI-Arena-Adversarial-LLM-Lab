# AI Arena - WPF

This folder contains the native WPF AI Arena app.

The current WPF shell includes:

- native Windows desktop app
- independent transcript and Arena scroll panels with slim theme-aware scrollbars
- read-only loading from `%LOCALAPPDATA%\AI Arena\sessions`
- session picker for available snapshots
- 1.2 second read-only snapshot refresh
- compact top bar with session/provider status, theme selector, and settings icon
- native dark title bar/app border on supported Windows builds
- settings overlay with collapsed sections and active participant count under Model Provider
- provider health test through the configured OpenAI-compatible endpoint
- first native `1 TURN` path through the shared core service layer
- operator turn injection from WPF, kept available during other arena operations
- transcript copy, pin/unpin, delete, model reasoning display, generated/context token pills, and agent-tinted framed transcript cards
- newest-first transcript rendering with follow-chat pinned to the latest card
- transcript retry as a targeted one-shot by the original agent
- per-agent one-shot turns that do not advance the normal turn order
- Auto Chat loop with Stop cancellation
- Auto Chat cadence selector in the App Settings overlay
- active command buttons use subtle breathing feedback during operations
- Reset clears transcript/live turn state while preserving scenario, cast, settings, and checkpoints
- top settings icon opens a translucent roll-down overlay above the Arena panel
- editable WPF App Settings for active participants, provider URL, default model, per-role Alpha/Beta/Gamma/Narrator models, timeout, temperature, max output, internet mode, internet source scope, requester permissions, approval requirement, and max internet results
- top-bar Theme selector with System, Dark Arena, Dark Green, Dark Blue, and High Contrast palettes
- persisted WPF-local settings in `%LOCALAPPDATA%\AI Arena\configs\native-wpf-settings.json`
- Custom Match read view for scenario, cast, and lock status
- Custom Match lock toggles for topic, global, and cast members
- Random Seed match generation with visibly refreshed cast roles and personas
- AI Choice match generation through the configured narrator/shared model, with fallback cast completion
- Custom Match checkpoint save, restore, and delete controls
- Narrate Now model call into the transcript with reasoning metadata
- Curate News injects one narrator-curated news/research transcript card when internet is enabled
- shared internet tool contract for future model-requested `web_search`, `rss_search`, `fetch_url`, and source summarization
- model-requested internet tools during native `1 TURN`, gated by App Settings and shown as visible Internet transcript cards
- lightweight URL text extraction and 15-minute internet tool cache
- optional internet approval mode that pauses model-requested requests as pending transcript cards
- role-blind native participant prompting: agents see their own persona, but other participants only by public name
- friendly provider errors for common unreachable, timeout, and unreadable-response cases
- create/switch/delete session controls
- themed, draggable confirmation dialogs before AI Choice and destructive session/checkpoint operations
- WPF-native empty states for transcript, scenario, and cast views
- live agent cards refreshed from the selected session snapshot
- dark AI Arena layout direction
- no WebView or browser UI
- no dependency on the archived WinUI project
- shared .NET services from `..\shared-dotnet\src\AIArena.Core`

Feature status is tracked in `docs/WPF_PORT_MAP.md`.

User-facing feature guidance is tracked in `docs/USER_GUIDE.md`.

## Source Layout

- `src/AIArena.Wpf/Shell` - main window and dialogs.
- `src/AIArena.Wpf/UI` - controls, view models, avatar helpers, and compact visual widgets.
- `src/AIArena.Wpf/Modules` - WPF app services grouped by feature area.
- `src/AIArena.Wpf/Platform/Windows` - Windows-specific settings, telemetry, and theme plumbing.
- `src/AIArena.Wpf/Assets` - app icons and packaged visual assets.

## Build

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
```

## Run

Open `AI Arena - WPF.sln` in Visual Studio and run `AIArena.Wpf`, or run the project from the command line:

```powershell
dotnet run --project .\src\AIArena.Wpf\AIArena.Wpf.csproj
```

## Preview Build

```powershell
.\scripts\build-wpf-preview.ps1
```

The preview executable is written to `..\dist\AI Arena WPF\AI Arena.exe`.

## Versioned Release Build

```powershell
.\scripts\build-wpf-release.ps1 -Version "0.3.42-beta" -Changes "Updated user guide"
```

The release executable and `changes.txt` are written to `..\dist\AI Arena - <version>\`.
