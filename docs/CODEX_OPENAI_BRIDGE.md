# Codex OpenAI Bridge

`scripts/codex-openai-bridge.mjs` exposes a small local OpenAI-compatible API that lets AI Arena call the local Codex CLI as if it were a model provider.

## Start

```powershell
node "C:\AI Workspace\Codex\ai-arena\scripts\codex-openai-bridge.mjs"
```

Optional settings:

```powershell
$env:CODEX_BRIDGE_PORT = "8787"
$env:CODEX_BRIDGE_MODEL = "codex-local"
$env:CODEX_BRIDGE_REASONING_EFFORT = "low"
$env:CODEX_BRIDGE_WEB_SEARCH = "enabled"
$env:CODEX_BRIDGE_TOKEN = "local-secret"
$env:CODEX_BRIDGE_WORKDIR = "$env:TEMP\ai-arena-codex-bridge-workspace"
node "C:\AI Workspace\Codex\ai-arena\scripts\codex-openai-bridge.mjs"
```

The bridge defaults `CODEX_BRIDGE_REASONING_EFFORT` to `low`, the lowest reasoning effort that works with the current Codex Desktop/CLI tool set. The documented `minimal` level is rejected by this Codex path while built-in tools such as `image_gen` are attached. The bridge disables reasoning summaries and defaults `CODEX_BRIDGE_WEB_SEARCH` to `enabled` so Codex can use recent public web knowledge for live topic generation and debate. Set `CODEX_BRIDGE_WEB_SEARCH=disabled` before starting the bridge to turn that off.

## AI Arena Settings

Use the Model Provider panel:

- API mode: `OpenAI-compatible /v1`
- Provider base URL: `http://127.0.0.1:8787/v1`
- API token: blank, unless `CODEX_BRIDGE_TOKEN` is set
- Model: `codex-local`

## Endpoints

- `GET /v1/models`
- `POST /v1/chat/completions`
- `GET /api/v1/models`
- `POST /api/v1/chat`

Streaming requests use OpenAI-style server-sent events on `/v1/chat/completions` and LM Studio-style events on `/api/v1/chat`. If Codex emits token deltas, the bridge forwards them. If the current Codex CLI only emits a completed assistant message, the bridge streams that final message back in small chunks so AI Arena still receives a streaming response.

## Safety

The bridge runs Codex with a read-only filesystem sandbox, skips repo checks, ignores project rules, and uses a temporary working directory by default. Web search is available to Codex unless `CODEX_BRIDGE_WEB_SEARCH=disabled` is set. Do not expose the bridge outside localhost. If another local process should not be able to call it, set `CODEX_BRIDGE_TOKEN`.
