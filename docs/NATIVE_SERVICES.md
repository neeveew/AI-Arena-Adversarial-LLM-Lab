# Native Services

Open **Settings → Native Services → Open Native Services**, then **Connect / Refresh**.
The separately running AI Arena native app must support the Protobuf control
transport in the same Windows user and interactive sign-in session.

## Models and inference

The **Models & inference** tab lists deployments configured by the native app.
Choose a deployment and select **Load model**. The client inspects the native load
plan first. If capacity admission needs a decision, review the displayed evidence
and use the separate **Load anyway** action; no capacity warning is confirmed
automatically. Residency and serviceability come from refreshed native inventory.
**Unload model** requests native ejection of the selected resident deployment.

Once the selected model is serviceable, enter a prompt and choose **Start
inference**. The default output limit is 256 tokens. The response field shows the
latest accumulated public-text snapshot and supports selection/copy. Snapshot
updates replace earlier text; status distinguishes running, successful, failed,
and canceled results. **Stop observing** ends only local updates; **Cancel
operation** invokes the dedicated native inference cancellation API and reads back
its state. Model/inference and diagnostic operations retain separate receipts.

Prompts are bounded to 16 KiB UTF-8, inference output to 256 KiB, and configured
output limits to 1–4096 tokens. Native inference results live in the native
process. A restart or unavailable result remains unconfirmed; it is not reported
as successful or canceled. The model/prompt/options stay locked after an uncertain
submission. Durable model requests can use **Retry same request** with their original arguments and key. An unconfirmed inference start cannot be retried in this preview without proof of the same native process; inspect native state before another submission.

## Diagnostic bundles

The window shows native health, model inventory, and operation counts. Choose a
new ZIP destination and select **Create bundle** to collect version, health,
operation diagnostics, and allowlisted sanitized logs. Progress shows observed operation transitions; no loading
percentage is inferred. The requested path appears as a result only after the
native app reports success and that file exists locally.

**Stop observing** stops local updates. **Cancel operation** sends a distinct
native cancellation request and queries the resulting state; completion may win
that race. Closing the window only stops observation. Reopening it in the same
WPF process retains the operation receipt so **Refresh status** can reconcile it.

If submission ends without a receipt, **Retry same request** preserves its exact
arguments and idempotency key. The destination remains locked while the first
submission is unresolved. Pending receipts currently live in the WPF process;
after restarting WPF, inspect existing native operations before submitting again.

The client never changes WPF model configuration, replaces an engine, launches
the native app, or accesses its database. Each app owns a separate data profile.

## Maintainer contract

- C++ owns `protocols/ai_arena_native.proto`; the WPF copy is a generation input.
- `scripts/generate-native-protocol.ps1 -ProtocPath <protoc.exe>` copies the
  canonical sibling-repository schema and generates checked-in C# with official
  protoc 36.1. Runtime dependency: `Google.Protobuf` 3.36.1. Ordinary builds do not
  require protoc or the C++ checkout.
- Each connection uses the existing SID/logon-derived native pipe and current
  protected token, with `APB1` + little-endian uint32 length + Protobuf payload.
  Requests are capped at 256 KiB and responses at 1 MiB before allocation. There
  is one request/response per connection. Existing JSON/PowerShell clients keep
  their own framing and remain unchanged.
- The native adapter maps typed messages into existing commands. Protobuf bytes
  are never an idempotency fingerprint. Correlation IDs are canonical UUIDv7;
  explicit mutation retries retain the original key and semantic arguments.
- The native coordinating task owns its test process. `AI_ARENA_DATA_DIR`
  separates data profiles but does not create a second native endpoint per logon.
  The protected token root comes from Windows' Local Application Data known
  folder, matching the native host; overriding `LOCALAPPDATA` cannot relocate it.

## Focused verification

Build the WPF test project, then set `AIARENA_TEST_FILTER=native services` and run
its console executable. Tests use fixtures; they never connect to a real native
instance or read its token.

Against an explicitly owned running native instance, the opt-in test entry
`AIArena.Wpf.Tests.exe --native-services-integration <absolute-owned-output-directory> [--cancel]`
uses the production client and coordinator. Local receipts contain the exact
nonsecret mutation key/arguments, operation ID, transitions, and final state for
comparison with native PowerShell `Get-AIArenaOperation` and same-key replay.
No authentication token is emitted. Cancellation mode succeeds only for observed
`canceled`; exit 3 means the operation reached another terminal state first.

## Recorded live integration

The production C# client/coordinator and generated C++ transport passed an owned
live check after the KnownFolder resolver fix, using a synthetic 15 MiB log
fixture. Both client runs exited 0:

- Completion: `01a079d0-6c8e-78be-9534-6f0f2a38f699` moved from `accepted` to
  `succeeded` and produced a real diagnostic ZIP.
- Cancellation: `01a079d0-6f64-7d76-b0fe-8d58610fb968` moved from `accepted` to
  `canceled` with no result ZIP.

Existing JSON/PowerShell queries agreed with both terminal states. Replaying each
original mutation key with the same semantic arguments over JSON returned the
original operation without starting another background job. The owned native
instance closed cleanly. Local evidence is in the sibling C++ checkout under
`artifacts/integration/protobuf/live-03/complete-parity.json` and
`artifacts/integration/protobuf/live-03/cancel-parity.json`.

The opt-in `AIArena.Wpf.Tests.exe --native-inference-integration
<absolute-owned-output-directory> <deployment-id>` harness requires a freshly
configured, unloaded native fixture. It loads that deployment, checks one short
successful inference, observes nonempty output while a second generation is still
running before canceling it, then ejects the model. It never confirms a capacity
warning. The create-new `wpf-native-inference-receipt.json` records original
mutation keys, exact public fixture arguments, operation states, output UTF-8
lengths and SHA-256 hashes. It does not log response content. Model lifecycle
parity uses `operation.state`; process-local inference parity uses
`inference.generate.read`.

The real C# model/inference workflow passed against the owned CPU fixture in the
native checkout's `artifacts/integration/protobuf/inference-live-02`: load and
ejection succeeded, the short generation succeeded with 121 UTF-8 output bytes at
sequence 66, and the second generation canceled with 4 bytes at sequence 5 after
nonterminal output had been observed. Both the C# runner and the native app exited
0; native shutdown was normal. The WPF-focused suite also passed all 21 registered
Native Services checks, including standard/compact rendered controls.

The final native `artifacts/integration/protobuf/inference-live-04/inference-parity.json`
check also passed: Protobuf and JSON agreed on lifecycle states/cursors and
inference output hashes, byte counts, and sequences. Replaying the original start
keys after model ejection returned the same operation IDs and retained outputs;
cancellation followed observed nonterminal text. Both processes exited 0 normally.

## Production installer

The `0.4.140-beta` Lite installer includes the Native Services client and offers
bundled SearXNG as an optional installation component. Native Services requires a
separately installed, compatible AI Arena C++ app running under the same Windows
logon. The Lite installer does not include or start that native app.

## Historical local preview

The local unsigned `0.4.138-beta` preview payload is under `artifacts/native-services-preview/20260907-v0.4.138-beta/wpf`.

After the focused checks and the owned live workflow pass, run
`scripts/build-native-services-preview.ps1 -OutputDirectory <new-absolute-path-under-artifacts>`.
This produces a fresh unsigned, self-contained Windows x64 payload with offline
Help, portable control documentation, and checksums. It preserves the declared
product version and creates no installer. The compatible native app is packaged
separately. The optional SearXNG payload is omitted from this preview; its
`PREVIEW.md` describes that limit and the required separate writable data profiles.

That local preview also passed one isolated startup/normal-close smoke: its owned WPF control namespace became ready, normal close returned exit 0, and no forced cleanup was needed. Checksum verification confirmed startup did not modify the payload.
