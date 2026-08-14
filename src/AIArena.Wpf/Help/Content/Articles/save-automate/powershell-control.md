AI Arena - Lite exposes a local authenticated PowerShell control plane for smoke tests and repeatable automation. Its established `AIArena` commands, `AI_ARENA_*` environment variables, pipe, token filename, and data path remain unchanged for compatibility; installed binaries now live machine-wide under `Program Files\AI Arena Lite`.

## Enable and load

PowerShell control is enabled by default under **Settings → Debug controls** and is independent of the visual Debug master switch.

```powershell
. "$env:ProgramFiles\AI Arena Lite\ai-arena-control.ps1"
Get-AIArenaCapabilities
Invoke-AIArena status
```

Every authenticated response includes an authoritative post-command `state`, including normal command errors. Authentication failures do not expose app state.

## Common read and navigation commands

```powershell
Get-AIArenaProvider
Get-AIArenaSession
Get-AIArenaInternet
Select-AIArenaView arena
Set-AIArenaRightRail show
Save-AIArenaScreenshot
```

Navigation commands use the same app coordinators and focus paths as UI actions. They do not reproduce a second, divergent state machine.

## Provider and routing

```powershell
Set-AIArenaProviderConfig `
  -BaseUrl "http://127.0.0.1:1234/v1" `
  -ApiMode lmstudio_native `
  -Model "publisher/model" `
  -ContextLength 32768 `
  -DefaultForUnassignedAgentsEnabled $false

Update-AIArenaProviderModels
Test-AIArenaProvider
```

Only supplied fields change. Explicit `$false`, numeric `0`, and empty role values are preserved. An empty role route inherits only while fallback is enabled; otherwise it is unassigned. A nonblank route remains explicit even when equal to the shared model.

Use `Read-Host -AsSecureString` when setting a token so plaintext is not placed in command history.

## Arena and Agent

```powershell
Invoke-AIArenaArena turn
Invoke-AIArenaArena operator.send -Prompt "Ask for stronger evidence." -Route public
Invoke-AIArenaAgent send -Prompt "Inspect this workspace and propose a verified fix."
Invoke-AIArenaAgent state
```

Destructive or mutating operations preserve UI confirmation contracts. Reset requires `-ConfirmReset`; command execution still passes through Agent preview/workspace validation and approval rules.

## Portable setup and evidence

```powershell
Export-AIArenaMatchSetup ".\portable-match.json"
Import-AIArenaMatchSetup ".\portable-match.json" -Name "portable-review"
Export-AIArena transcript
Export-AIArena receipts
Watch-AIArena events
```

The full command and schema reference is installed as `CONTROLPLANE.md`. Treat that reference and `Get-AIArenaCapabilities` as authoritative for exact command availability in the installed version.
