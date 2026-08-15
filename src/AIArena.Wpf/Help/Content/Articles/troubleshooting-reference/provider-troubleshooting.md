Provider Online, Model Loaded, and Arena Ready are different claims. Diagnose them in order.

## Connection checklist

1. Confirm the provider process is running.
2. Match connection type to API: OpenAI-compatible `/v1`, LM Studio native `/api/v1`, Ollama native `/api`, or llama.cpp native `/v1`.
3. Verify host and port.
4. Test the connection from Provider Settings.
5. Refresh Models and verify the exact provider model ID.
6. Confirm provider-observed residency where lifecycle evidence is supported.
7. Confirm the scheduled Arena role resolves to an explicit route or enabled Default fallback.

Do not infer routing from residency or residency from configuration.

## Disabled Auto Chat or 1 Turn

Move keyboard focus to the disabled control or inspect its accessible help. Typical causes are:

- provider offline or busy;
- scheduled role **Unassigned** because Default fallback is off;
- match already busy or ended;
- Factory mode missing its public Operator root;
- a required model operation still awaiting confirmation.

The Universal Status Center may identify the latest failure, but Models, Provider Settings, and the transcript remain authoritative for their own state.

## Timeout or slow generation

- try 1 Turn before Auto Chat;
- reduce output limit;
- choose a smaller/faster model;
- reduce context or use Rolling 80% for Arena history;
- increase timeout when the provider is healthy but slow;
- inspect provider hardware placement and queue state.

GPU telemetry can be unavailable while chat still works. Missing telemetry is not a provider failure.

## LM Studio lifecycle

A successful load/unload HTTP response is provisional until the native catalog confirms residency. **Awaiting confirmation** keeps the last confirmed row. Do not repeat an ambiguous mutation until refresh resolves its state.

If changed context differs from the live process, use Reload to apply and verify the new effective evidence.

## llama.cpp

AI Arena - Lite does not download, launch, stop, or replace a user-owned `llama-server`. Inspect/Reconnect capability-detects optional health, props, slots, router models, and lifecycle endpoints. **Not reported** means the server did not expose that field; it does not prove chat is broken.

`Retry-After` is delay guidance, not proof that a provider did no work. Compatible, LM Studio, Ollama, and llama.cpp routes are replayed only when an explicitly configured end-to-end idempotency contract can reuse one key and the exact payload; a requested delay beyond the five-second local policy is surfaced instead of shortened. Loopback location and busy/loading text are not replay proof on their own. After any 2xx acceptance, partial or incomplete stream, ambiguous send/read failure, timeout, or cancellation, AI Arena - Lite preserves available evidence and never replays the completion automatically.

## Credentials

Tokens are stored through provider settings and excluded from normal status, exports, portable setups, and control-plane state. Remove credentials from URLs; use the token field instead.
