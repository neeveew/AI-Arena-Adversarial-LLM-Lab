# AI Arena Lite: developer documentation

[Back to the front page](../README.md) · [Feature overview](OVERVIEW.md) · [User guide](USER_GUIDE.md)

Technical reference for the C# / WPF application. Run the commands below from the repository root. For installation without building, use the [Windows downloads page](https://aiarena.me/downloads/).

## Contents

- [PowerShell Control](#powershell-control)
- [Technical Overview](#technical-overview)
- [Source Layout](#source-layout)
- [Build](#build)
- [Release Helpers](#release-helpers)
- [Further reading](#further-reading)

## PowerShell Control

The local, token-authenticated WPF control plane is enabled by default. Its toggle is organized under **Settings -> Debug controls** with the other developer tools, but it remains independently enabled when **Allow debug controls** is off. Load the helper, then inspect or administer the active session's provider:

```powershell
. "$env:ProgramFiles\AI Arena Lite\ai-arena-control.ps1"

$provider = Get-AIArenaProvider
Set-AIArenaProviderConfig -BaseUrl "http://127.0.0.1:1234/v1" -ApiMode lmstudio_native -Model "google/gemma-4-e2b" -DefaultForUnassignedAgentsEnabled $true
Update-AIArenaProviderModels
$provider = Get-AIArenaProvider
$modelConfig = $provider.Data.ModelSettings | Where-Object Model -eq "google/gemma-4-e2b"
Set-AIArenaProviderModelConfig -Model $modelConfig.Model -ExpectedConfigurationIdentity $modelConfig.ConfigurationIdentity -ConfiguredContextWindow 32768 -HistoryPolicy rolling_80 -ResponseTone concise
Test-AIArenaProvider
```

Provider tokens use `SecureString`: `$token = Read-Host "Provider token" -AsSecureString`, then `Set-AIArenaProviderConfig -ApiToken $token`. Responses expose only whether a token is configured. `-DefaultForUnassignedAgentsEnabled $false` leaves the shared provider model configured but prevents unassigned Arena roles from silently falling back to it.

AI Arena - Lite can also capture its own WPF window:

```powershell
$capture = Save-AIArenaScreenshot
$capture.data | Format-List path, byteSize, pixelWidth, pixelHeight
$capture.state

Save-AIArenaScreenshot "reviews/after-provider-test.png"
Save-AIArenaScreenshot "C:\Screenshots\AI-Arena.png"
```

With no path, screenshots use `%LOCALAPPDATA%\AI Arena\exports\screenshots\AI-Arena-yyyyMMdd-HHmmss-fff.png`, or the equivalent `exports\screenshots` folder under `AI_ARENA_DATA_DIR`. Relative paths resolve under that folder; absolute paths are allowed. Paths must end in `.png`, and existing files are never overwritten.

## Technical Overview

- Native Windows WPF app.
- Shared .NET core library for arena logic, sessions, providers, diagnostics, internet tools, narration, transcript handling, match generation, and avatars.
- Shared provider client with automatically selected OpenAI-compatible and native adapters for LM Studio, Ollama, and llama.cpp.
- Automatic local server discovery and combined model catalogs, with serving endpoints preserved in model assignments and configuration. Providers control model execution, device placement, and GPU offload.
- A separate authenticated Protobuf client for the optional [Native Services](NATIVE_SERVICES.md) integration; the compatible AI Arena C++ app owns its processes and data.
- User data storage under `%LOCALAPPDATA%\AI Arena`, split into `configs`, `sessions`, `checkpoints`, `templates`, `exports`, `logs`, and `cache`.
- No dependency on a specific model host.
- No WebView/browser dashboard dependency in the active app.

## Source Layout

- `src/AIArena.Wpf` - native Windows app.
  - `Shell` - main window and app dialogs.
  - `UI` - WPF controls, view models, and visual helpers.
  - `Modules` - WPF-facing feature services and adapters.
  - `Platform/Windows` - settings, telemetry, and theming integrations.
  - `Assets` - icons and packaged visual assets.
- `src/AIArena.Core` - shared domain models and services.
  - `Modules/Arena` - turn running and arena snapshots.
  - `Modules/Provider` - OpenAI-compatible provider config, client, and health checks.
  - `Modules/Sessions` - data paths, event log, summaries, and session storage.
  - `Modules/Internet` - first-class web search and readable-page fetching.
  - `Modules/Diagnostics`, `Modules/MatchGeneration`, `Modules/Narration`, `Modules/Transcript`, and `Modules/Avatars` - focused core features.
- `tests/AIArena.Tests` - shared .NET smoke tests.
- `tests/AIArena.Wpf.Tests` - WPF app smoke tests.
- `docs` - product notes, dependency index, user-facing guides, and the WPF shell decomposition map.

## Build

```powershell
dotnet build .\src\AIArena.Wpf\AIArena.Wpf.csproj
```

For the focused local-provider and comparison QA gates, run:

```powershell
.\scripts\local-runtime-qa.ps1
```

The script builds the solution, runs the llama.cpp contract/runtime and arena-evaluation harnesses, and verifies that no `llama-server` or `llama-cli` binary is bundled under `src` or `packaging`. Supply `-LlamaBaseUrl http://127.0.0.1:8080/v1` to add an optional read-only loopback `/health` probe; without it, live readiness is reported as unavailable rather than assumed.

For local web search, a development run uses a valid `searxng` payload beside the executable or from the newest versioned folder under `dist`. Set `AIARENA_SEARXNG_PAYLOAD_DIR` to either a payload root or its parent to override discovery. Release builds create and package the payload automatically.

## Release Helpers

```powershell
.\scripts\build-wpf-preview.ps1
.\scripts\build-wpf-release.ps1
.\scripts\build-wpf-installer.ps1
.\scripts\dependency-index.ps1 -Check
.\scripts\wpf-release-sanity.ps1
```

Release builds are self-contained by default, so the installed app never depends on a machine-wide .NET runtime. They verify the pinned CPython and SearXNG archives in `packaging/upstream-lock.json` and all Windows CPython wheels in `packaging/searxng-requirements-lock.txt` before installation. They write a hashed SearXNG payload inventory plus `changelog.md`, `changes.txt`, `github-release-notes.md`, `release-checksums.sha256`, `release-manifest.txt`, and `release-signing.json` beside the published app. `build-wpf-installer.ps1` requires a self-contained payload, creates a fresh versioned installer folder, refuses to overwrite an existing installer distribution, compiles the setup executable, copies the release metadata beside it, emits `SHA256SUMS.txt`, and runs release sanity.

Set `AIARENA_RUN_LIVE_INTERNET_SMOKE=1` and point `AIARENA_SEARXNG_PAYLOAD_DIR` at a built release before running the WPF harness to include a real bundled-search and hardened public-fetch diagnostic.

Authenticode signing is optional by default. For a publishable signed build, configure a production code-signing certificate and run `build-wpf-installer.ps1 -SigningPolicy Required`; the build fails before publishing when its signing prerequisites are unavailable. See [Release integrity and signing](RELEASE_SECURITY.md) for certificate, SignTool, checksum, and upstream-lock guidance.

The generated dependency map lives at `docs/DEPENDENCY_INDEX.md`. Rebuild it with `.\scripts\dependency-index.ps1` after moving modules, services, project references, packages, or packaged resources.

## Further reading

- [PowerShell command reference](../CONTROLPLANE.md)
- [Windows app and stabilization checks](WINDOWS_APP.md)
- [Dependency index](DEPENDENCY_INDEX.md)
- [Release integrity and signing](RELEASE_SECURITY.md)
- [Software licence](../LICENSE)
