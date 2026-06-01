# AI Arena WPF Port Map

This map tracks the native WPF port against the Python beta app. Keep it close to the code so the WPF app can move forward without repeatedly re-discovering the same behavior.

## Status Key

- Done: implemented in WPF and using native or shared core services.
- Partial: visible or usable, but not complete.
- Pending: not started in WPF.
- Deferred: intentionally left out for now.

## Shell And Layout

| Feature | Status | Notes |
| --- | --- | --- |
| Native WPF desktop window | Done | No browser or WebView dependency. Window and executable use the AI Arena app icon; supported Windows builds get dark native chrome and a visible app border. |
| Independent transcript and arena panel scrolling | Done | WPF replaced paused WinUI 3 shell because WPF scrolling behaved correctly. Scrollbars are slim and theme-aware. |
| Left navigation | Partial | Arena, Custom Match, and News switch views. Export is a placeholder. Files is intentionally omitted. |
| Top health/status bar | Partial | Session, match, provider, model, turn count, Theme selector, and compact settings icon are visible from snapshots. Status text is left-aligned and trims before crowding right-side controls. |
| Debug controls | Done | Optional Settings -> Visuals toggle reveals a top-rail Debug dropdown for experimental diagnostics such as Decision Card and Style Fit cue chips. |
| Theme selector | Done | System, Dark Arena, Dark Green, Dark Blue, and High Contrast palettes apply immediately and persist to WPF-local settings. |
| 1.2 second snapshot refresh | Done | Refreshes selected session when snapshot timestamp changes. |
| Follow Chat | Done | Single follow toggle; transcript is newest-first and follow keeps the latest card at the top. |
| Themed confirmation dialogs | Done | WPF-owned modal dialogs replace classic system MessageBox confirmations, use the active palette, are draggable from the dialog body, and expose a compact top-right close button. |

## Session And Storage

| Feature | Status | Notes |
| --- | --- | --- |
| Session picker | Done | Lists `%LOCALAPPDATA%\AI Arena\sessions`. |
| Snapshot loading | Done | Read path is WPF-local through `SnapshotStore`. |
| Snapshot writes | Partial | Native writes now happen for operator turns, pin, delete, 1 TURN, targeted turns, retry, and session creation through shared core stores. |
| Event logging | Partial | Native actions append to `%LOCALAPPDATA%\AI Arena\logs\sessions\<session>\events.jsonl` through shared `EventLogStore`. |
| Checkpoints | Done | Session-scoped save/restore/delete under `%LOCALAPPDATA%\AI Arena\checkpoints\<session>`. Restore/delete use themed confirmation dialogs. |

## Transcript

| Feature | Status | Notes |
| --- | --- | --- |
| Transcript cards | Done | Newest-first framed cards with a compact header rail, agent-tinted output body, model name, generated/context token pills, and accent strip. Normal `ok` is hidden; error/pending/pinned remain visible. |
| Model reasoning expander | Done | Reads `metadata.reasoning_content`. |
| Copy message | Done | Copies public message text to clipboard. |
| Pin / unpin | Done | Updates snapshot message state through shared `TranscriptService`. |
| Delete message | Done | Removes matching message through shared `TranscriptService`. |
| Retry message | Done | Replaces the selected model card in place. Enabled only for the latest three Alpha/Beta/Gamma turns. Does not advance normal turn order. |
| News / external context cards | Done | Curated/news cards render with collapsible source and selection details when metadata is available. |

## Arena Controls

| Feature | Status | Notes |
| --- | --- | --- |
| 1 TURN | Done | Calls shared `TurnRunnerService`; updates transcript and live agents after completion. |
| Operator turn | Done | Writes a public operator message without advancing agent order. Operator input stays enabled during other operations. |
| Auto Chat | Partial | Runs repeated ordered turns through shared `TurnRunnerService`. Cadence is selected in the App Settings overlay. Active command buttons breathe while running. Pending internet approvals pause the loop until resolved. |
| Stop | Partial | Cancels the WPF auto loop and breathes only while a stoppable operation is active. |
| Reset | Done | Clears transcript, narration, turn count/order, agent private notes, and live statuses while preserving scenario, cast, locks, settings, and checkpoints. |
| Narrate Now | Done | Calls the narrator model and appends a visible narrator transcript card with reasoning metadata. Uses the same active-operation breathing affordance as other command buttons. |
| Curate News | Done | First asks the narrator/shared model for a compact search intent based on the current debate direction, fetches/ranks trusted RSS sources through `InternetToolService`, then injects one directly relevant sourced news/research transcript card with reasoning, plan, and source metadata. If no directly relevant source is found, it reports status without adding a transcript card. |
| Internet tool contract | Done | Shared core models define `web_search`, `rss_search`, `fetch_url`, and `summarize_sources` JSON requests/results. |
| Model-requested internet during 1 TURN | Partial | If enabled in App Settings, an agent may emit a valid internet tool JSON request. WPF injects an Internet transcript card, then asks the agent for the final public turn. Auto Chat uses the same turn runner; retry integration is intentionally disabled for internet requests. |
| Internet approval mode | Done | Optional setting pauses model-requested internet by injecting a pending Internet Approval card with Approve Once, Reject, Copy URL, and Delete actions. |
| Internet cache | Done | Successful internet tool results are cached in memory for 15 minutes by request and source settings. |
| Random Seed | Done | Native deterministic template generation with role pack, style, pressure, and absurdity controls; visibly refreshed cast roles/personas/voices; respects topic/global/agent/narrator locks, preserves transcript. |
| AI Choice | Partial | Calls configured narrator/shared model for match JSON, fills missing cast members, applies with locks, preserves transcript. Needs broader malformed-output recovery. |

## Live Agents

| Feature | Status | Notes |
| --- | --- | --- |
| Agent cards | Done | Name, status, model, persona, accent. |
| Private memory / prompt expander | Partial | Private notes are shown when present; full prompt preview is pending. |
| Per-agent one-turn play | Done | Targeted one-shot; does not advance normal turn order. |
| Active participant count | Done | App Settings exposes a 1/2/3 active-agent dropdown inside Model Provider and persists agent `active` flags. |

## Settings And Provider

| Feature | Status | Notes |
| --- | --- | --- |
| Settings summary | Done | Active participant count is controlled inside Model Provider, with provider, internet, context windows, and help grouped in collapsed sections. The top bar uses a compact settings icon. |
| Editable provider settings | Done | WPF can edit shared base URL, default model, per-role Alpha/Beta/Gamma/Narrator models, timeout, temperature, max output, internet toggle, internet mode, internet source scope, requester permissions, approval requirement, and max internet results. Advertised provider models refresh every 5 seconds while settings are open. |
| App Settings overlay | Done | Top-bar settings icon opens an 80% opacity overlapping settings panel instead of inserting settings into the Arena control flow. |
| WPF-local settings | Done | Theme preference is stored at `%LOCALAPPDATA%\AI Arena\configs\native-wpf-settings.json`. |
| Provider health test | Done | Calls configured OpenAI-compatible endpoint through shared provider service. |
| Provider editing | Done | WPF writes shared/native provider config and role-specific model overrides. Role models fall back to the shared default when unavailable. |
| Ollama/OpenAI-compatible provider support | Partial | Depends on compatible `/v1` endpoint and model config. |

## Custom Match / Arena Fine Tuning

| Feature | Status | Notes |
| --- | --- | --- |
| Scenario preview | Done | Read-only topic/global cards. |
| Cast preview | Done | Agent/persona cards with per-agent voice style selectors, visible non-default voice chips, and heuristic style-fit scoring in transcript/performance views. |
| Locks | Done | Topic/global/agent lock checkboxes persist to snapshot and are respected by native match generation. |
| Random seed generation | Done | Generates deterministic scenario/cast updates through shared native services while respecting locks. |
| Request AI to generate | Partial | AI Choice calls the configured narrator/shared model and fills missing cast members; malformed-output recovery still needs more coverage. |
| Checkpoints UI | Done | Lives under Custom Match before scenario/cast preview. |

## News, Files, Export

| Feature | Status | Notes |
| --- | --- | --- |
| News view | Partial | Read-only inspector lists transcript-backed curated news and internet-source items for the current session. |
| Export view | Pending | Left nav placeholder exists. |
| Files view | Deferred | User has separate plans, so it is intentionally omitted from WPF left nav. |

## Build And Packaging

| Feature | Status | Notes |
| --- | --- | --- |
| WPF preview build script | Done | `windows-wpf/scripts/build-wpf-preview.ps1` publishes to `dist/AI Arena WPF/AI Arena.exe` without touching Python beta installer folders. |

## Shared Core Services

| Service | Status | Used By |
| --- | --- | --- |
| `ModelProviderClient` | Partial | Provider test, turn running, narrator calls, and AI Choice through higher-level services. Captures OpenAI-compatible usage tokens and maps common provider failures to operator-friendly messages. |
| `TurnRunnerService` | Partial | 1 TURN, targeted agent turns, retry, and Auto Chat. Native participant prompts are role-blind: other participants are listed by public name only, while the selected agent receives only its own persona. |
| `TranscriptService` | Partial | Message creation, operator turns, pin, delete, retry, internet cards, and newest-first helpers. |
| `EventLogStore` | Partial | Native arena actions, settings, internet requests, sessions, checkpoints, and transcript mutations. |
| `SessionStore` | Partial | Snapshot reads/writes, session create/delete, and checkpoints. |
| `InternetToolContract` | Done | Parses and validates model-emitted internet tool JSON before any executor is allowed to run. |
| `InternetToolService` | Partial | Enforces internet mode/settings, logs tool attempts, caches results, and executes trusted RSS/direct URL requests through an injectable provider. RSS matching ignores filler terms and ranks title matches above snippet matches. Direct URL fetch performs lightweight title/canonical/date/readable-text extraction without a browser. |
