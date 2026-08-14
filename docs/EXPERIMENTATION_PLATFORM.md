# AI Arena - Lite Experimentation & Verification Platform

The experimentation platform turns an Arena setup into a repeatable local experiment without changing the product's privacy boundary. Experiments, evaluation, verification, and repair evidence remain on the user's machine. Provider credentials, complete prompts, transcript bodies, private memories, raw provider responses, and machine-specific absolute paths are excluded from aggregate evidence.

This document defines the product boundary for the ten-feature batch. A feature is not considered complete merely because its UI renders: its contract, persistence, migration, cancellation, privacy, and verification gates must also pass.

## Architecture boundary

Core owns versioned contracts, validation, canonical fingerprints, expansion, persistence, evidence availability, and headless execution. WPF owns presentation and explicit user approval. The Verification Lab owns deterministic provider behavior, real loopback transport, isolated app launches, black-box control, rendering evidence, and the seal runner.

Protocol-specific behavior stays in the existing provider client. Controlled failures are applied by a provider decorator or by the loopback scripted provider, not by production protocol branches.

## Versioned contracts

| Schema | Purpose |
| --- | --- |
| `ai_arena.experiment.v1` | Matrix definition, bounded parallelism, repetitions, scenario/provider/rubric/fault references, and deterministic cell identity |
| `ai_arena.experiment_run.v1` | Durable cell state, attempts, trial retention, deterministic variant identity, interruption, and restart normalization |
| `ai_arena.branch.v1` | A branch receipt with parent revision, transcript cursor, setup fingerprint, memory revision, and child session identity |
| `ai_arena.rubric.v1` | Weighted criteria and evaluator kinds; deterministic, human, and model-judge observations remain separate |
| `ai_arena.rubric_result.v1` | Finalized criterion observations, weighted normalized scores, blind-pair reveal, disagreement, and unavailable evidence |
| `ai_arena.claim_ledger.v1` | Claims, assertion provenance, source references, contradictions, review state, and evidence availability |
| `ai_arena.memory_trace.v1` | Structured agent memory with visibility, origin, revision, expiry, correction, branch, and source provenance |
| `ai_arena.scenario_pack.v1` | Versioned scenarios with invariant references, deterministic seeds, migration provenance, and duplicate-resistant content fingerprints |
| `ai_arena.benchmark_pack.v1` | Repeat-trial benchmark cases joining scenarios, rubrics, provider capabilities, and required evidence |
| `ai_arena.route_proposal.v1` | Proposal-only role/model routes tied to a setup fingerprint, with sample counts and per-component observed/inferred/unavailable evidence for normalized scores, hardware, capabilities, and policy constraints |
| `ai_arena.route_application_receipt.v1` | Immutable evidence that a separately authorized operator approval was applied to an exact proposal and setup; optimizers cannot create this receipt |
| `ai_arena.fault_profile.v1` | Deterministic fault triggers and bounded timeout, disconnect, malformed stream, saturation, empty-response, interruption, and context-pressure outcomes |
| `ai_arena.qa_evidence.v1` | Product and nested-Map source identity, seal-manifest identity, toolchain, gates, timings, resource measurements, provenance-bound artifact hashes, limitations, inspection state, and verdict |

Unknown or missing required fields are rejected where a safety decision depends on the payload. Supported older schemas are migrated explicitly; unsupported schemas remain unchanged and report a diagnostic. Fingerprints use canonical, secret-free content and do not depend on display names or capture timestamps. Historical `ai_arena.qa_seal_manifest.v1` remains frozen with its original closed gate, schema, provenance, metric, and artifact requirements so existing V1 evidence can still be decoded and validated without silently acquiring later gates. Current producers and acceptance use `ai_arena.qa_seal_manifest.v2`, which preserves that authority and additionally requires executable v0 pack-migration proof plus the complete Experiment Lab feature-surface render matrix. Unknown manifest versions fail closed.

## Evidence states

Every measurement declares one of these states:

- `observed`: directly produced by the application, provider response, operating system, test harness, or user action named by the evidence.
- `inferred`: derived from observed inputs by a named deterministic rule. Inference is never presented as a measured fact.
- `unavailable`: the source did not provide trustworthy evidence, the probe was not run, or privacy policy forbids retention.

Gate outcomes use `pass`, `fail`, `partial`, `blocked`, or `unavailable`. Required gates may not be sealed when unavailable. Optional live-provider coverage can remain unavailable, but must be disclosed and cannot be counted as deterministic or live coverage.

## Feature behavior

### Experiment Matrix Runner

The runner expands sorted axes into stable cells, combines each cell with a repetition number, and derives a deterministic cell key. It enforces experiment and provider concurrency limits, records terminal trials, marks runs that were active during shutdown as interrupted, resumes only missing or retry-approved cells, and never deduplicates separately captured repeat trials.

The exact matrix definition is also stored as bounded canonical `ai_arena.experiment.v1` JSON under the local experimentation root. Its lifecycle is `draft`, `running`, `interrupted`, `completed`, or `cancelled`; per-cell run files remain the authority for attempts, trial IDs, child sessions, and evidence. Execution holds a cross-process coordinator-owner lease for the complete Running transition. A new coordinator can acquire that lease only after the prior owner exits, atomically changes an abandoned Running definition to Interrupted, restores the latest representable definition into Matrix Runner, and starts zero cells until **Approve one retry of terminal cells** is explicitly checked. An approved Interrupted or Cancelled continuation preserves already-Completed cells, retries interrupted/failed/cancelled cells, and fills missing cells; this closes the crash window where every cell completed before the definition-level completion receipt without issuing duplicate provider calls. Approval on an intentionally Completed definition remains an explicit full repeat. A definition containing multiple axes, fault profiles, branch IDs, an unknown behavior axis, or any other value the v1 UI cannot reproduce exactly remains stored but unselected instead of being partially reconstructed. Definitions are count/byte bounded, privacy validated, atomically replaced, and never contain provider credentials, source text, transcripts, or absolute paths.

Live execution is authorized through `ArenaExperimentExecutionResolver`, not directly from presentation state. A runnable scenario must use an exact `session:<id>` reference, match the persisted source setup fingerprint, resolve one scenario-pack identity and (when selected) one unambiguous benchmark case, and find every selected provider and fault profile in bounded process-memory registries. Unknown provider protocols, missing profiles, unsupported benchmark capabilities, source drift, and unknown or invalid behavior dimensions remain explicitly unavailable. The v1 behavior allowlist is `temperature`, `max_output_tokens`, `context_length`, `reasoning`, and `timeout_seconds`; values are parsed invariantly and range-checked before any provider call. Rubric identities remain explicit post-run evaluation requirements rather than being silently treated as executed scores.

`ArenaExperimentCellExecutor` forks the persisted source once per trial, verifies the forked revision and setup identity, annotates the branch receipt with the experiment identity, clears role-specific routing, and installs one token-free shared configuration. The selected credential exists only in the process-memory profile registry and is injected at the provider call boundary. Configured turns then run through the real `TurnRunnerService` and provider adapter. Run artifacts retain stable trial, child-session, branch, turn-count, fault, and adapter-exposed aggregate telemetry observations only; they never retain prompts, transcript text, response/reasoning bodies, raw errors, credentials, or absolute paths. Caller cancellation is propagated to the runner and preserves any child/branch identity observed before cancellation. Resume reads durable terminal cells and makes no duplicate provider call or child session without explicit retry approval.

### Conversation Fork Lab

Branches are isolated sessions. A historical branch projects the transcript, narration, and provenance-bearing memory to the selected cursor and omits unprovable legacy memory, decisions, research, attachments, and later derived state. Existing snapshots do not retain cursor-scoped provider, persona, steering, relationship, or active-cast history, so an older-cursor branch explicitly retains the current replayable setup and records `historical-setup-projection-unavailable` evidence instead of pretending that setup is historical. A branch receipt identifies the cursor and revisions, but aggregate evidence does not contain transcript or memory text.

### Scenario and Benchmark Packs

Packs are distinct from personal scenario templates. They carry a stable schema, deterministic seed, normalized Match Setup reference, invariants, optional rubric and fault-profile references, and migration provenance. Import refuses content duplicates even when display names differ.

Migration is explicit and limited to the closed `ai_arena.scenario_pack.v0` and `ai_arena.benchmark_pack.v0` wire shapes. Those legacy shapes contain schema, stable IDs, name/version, and their behavior/evidence collections; they cannot supply a v1 creation time, content fingerprint, or migration claim. The import action hashes the exact source bytes with SHA-256, records source schema/version and a fixed migrator version, uses one injected UTC migration instant, recomputes the current behavior fingerprint, validates the complete v1 contract, and only then attempts the duplicate-safe write. The source bytes and selected absolute path are not persisted. Unknown or duplicate JSON members at any nesting level, unsupported schemas, malformed/oversize input, non-UTC time, secret/source-content values, invalid references, and a second attempt to migrate canonical v1 are rejected rather than guessed. Ordinary v1 decoding never migrates implicitly.

### Rubric & Judge Studio

Rubrics are immutable by version. Editing creates a new version and preserves existing results. Durable blind pairwise judging is a two-step write: an opaque A/B mapping commitment is saved before the judge submits, then the process-memory judge view shows both bounded transcript contents under opaque tokens with stable identities hidden. A receipt opens that exact commitment and fingerprints the exact evaluator result; identities appear only after receipt and result persistence succeed. A caller-supplied reveal without both artifacts remains rejected. Cancelling leaves the already-written commitment as an explicitly unclaimed audit artifact, and an app restart cannot resume its process-memory concealed view. Human scores, deterministic diagnostics, and model-judge opinions remain separate observations. Disagreement and unavailable criteria remain visible.

The result record is intentionally a companion schema rather than an extension of `ai_arena.rubric.v1`: the frozen rubric schema defines reusable criteria, while results are immutable observations made later. **Run provider judge** sends one selected transcript message through that message speaker's effective configured provider route with a strict criterion JSON contract. An available score is accepted only when the exact inferred evaluator result references an immutable receipt for the matching succeeded physical request trace; prompt, response, endpoint, response ID, and credentials are retained only as hashes or omitted. A typed model score without this binding remains rejected. Model-judge criterion evidence is always `inferred` or `unavailable`; only the provider-attempt receipt itself is observed. Rubric and result stores are byte/count bounded, canonical, atomic, and fail closed on corrupt or private artifacts.

### Evidence and Claim Ledger

The ledger links bounded claim text to a stable message reference, claimant, sources, contradictions, and reviewer actions. Model-extracted claims are labelled as model assertions. Source presence is not proof of correctness, and confidence asserted by a model is not converted into measured confidence.

Dialogue message IDs are used directly when contract-safe; legacy messages receive a deterministic hashed fallback. Tool and reviewer evidence is retained by artifact reference only. Claim updates are monotonic at the persistence boundary: an update may append claims, sources, contradictions, and reviewers, but cannot silently erase or rewrite existing audit evidence.

### Context and Prompt Inspector

The inspector observes the exact final request passed to the provider after continuation, fallback, Internet context, memory selection, and runtime configuration transformations. It explains omitted context and reports token counts only when the tokenizer/provider supplied evidence. Secrets and private memory are redacted according to viewer scope.

The WPF surface is registered in Experiment Lab under the stable key `context-prompt-inspector`. Requests are grouped by correlation id and expose phase, model, transport, physical attempt, outcome, exact outbound-body SHA-256 and byte count, explicit token evidence state, the bounded redacted payload preview, role transformations, and context/omission explanations. Clear removes only the bounded process-memory trace store; it does not modify sessions. Provider endpoints, credentials, private paths, and scoped-memory contents never enter status or automation evidence.

### Agent Memory Debugger

Memory entries record origin, visibility, source, creation and revision times, expiry, corrections, superseded entries, and branch identity. Legacy string notes migrate as `legacy_unknown`. Prompt selection includes only active, unexpired, scope-appropriate memory; branch-local memory cannot leak to siblings.

The WPF surface is registered separately under `agent-memory-debugger`. It starts default-deny: no private values or private-value counts appear until the operator explicitly selects one agent. The list then remains scoped to that owner and can filter active, expired, superseded, or all records. Add, Correct, and Expire all reload the active snapshot and call `StructuredMemoryService` before persistence; a correction appends a linked record, expiry retains lifecycle evidence, and a storage or concurrency failure never claims that a change was applied. `legacy_unknown` always says that original provenance is unprovable.

### Fault-Injection Lab

Fault profiles use deterministic seeded, sequence-ordered triggers with bounded occurrences, injected delay, concurrency, and observation retention. A public provider decorator is the arm/disarm boundary for WPF and control-plane coordinators; production protocol parsing remains untouched. It covers timeout, disconnect, malformed stream, saturation, empty response, interruption, and context pressure. The lab records content-free injected-cause evidence separately from recovery evidence, which remains `unavailable` until a higher-level recovery runner observes it. Caller cancellation remains distinguishable from an injected timeout. A malformed stream emits no assistant delta; an interruption emits one valid partial delta, and neither is promoted to an accepted completion.

### Model Routing Optimizer

The optimizer is a pure Core service that accepts bounded normalized sample evidence and produces a proposal, never an automatic route mutation or application receipt. Comparisons require one exact setup fingerprint and at least two samples for both the current and candidate model. A sample must belong to the current resolved execution-plan fingerprint, and its score must name the latest trial ID; an attempt-agnostic run ID or an older attempt cannot be reused after a retry. Each score component and hardware/capability/policy constraint carries its own evidence state; unavailable evidence cannot carry a numeric score or satisfaction claim. A compatible completed chat trial establishes only trial-scoped capability evidence: it does not establish the current machine's hardware. Only observed comparable scores plus a separately bound current observed hardware constraint and observed, satisfied capability constraints can produce `proposed`. Inferred, missing, mismatched, or single-sample inputs produce `insufficient_evidence` and explicitly say that no optimization is claimed. Applying a proposal remains a separate explicit-approval action through the existing provider-routing boundary.

### In-App QA Inspector

The inspector is the user-facing view of the same evidence emitted by automation. It offers selectable suites, live gate state, before/after renders, in-process WPF structure/focus results, performance/resource measurements, fault recovery, focused tests, and a privacy-safe copy report. It does not create a second definition of pass/fail. Acceptance stays disabled until every referenced screenshot has been explicitly previewed successfully. The resulting reviewed manifest is bound to the exact evidence-file hash, tested tree, and artifact IDs/hashes; refresh or any evidence change clears that review state.

Feature-surface screenshots are ordinary referenced screenshots for inspection purposes. The inspector discovers them from the bundle rather than from a hard-coded count, so all 120 cells from a two-pass feature run must be opened successfully before in-app acceptance can be enabled.

## Verification Lab

The scripted provider supports model discovery, non-streaming and streaming chat, telemetry, bounded request capture, and controlled failures over real loopback HTTP. Verification launches use a temporary `AI_ARENA_DATA_DIR`, unique control-plane identity, and isolated artifacts. Each run cleans up only processes and directories it created.

The Verification Lab also executes a real experiment matrix end to end: strict scenario resolution, source fork, token-free child routing, loopback `ModelProviderClient` turns, repeat trials, durable run persistence, no-op resume, invalid-axis refusal, classified HTTP failure, caller cancellation, branch isolation, and aggregate-evidence privacy. This is a named manifest check, increases the reported check count, and its loopback calls contribute to the retained request-evidence fingerprint.

Applicable matrices cover:

- Dark Blue, Light, and High Contrast;
- narrow and standard widths;
- supported off-screen raster-density scales at the fixed `960×640` and `1500×960` DIP viewports;
- programmatic in-process WPF focus traversal and privacy-safe visual-tree identities;
- normal/reduced motion-preference plumbing;
- cancellation, restart, interruption, transport failure, and recovery;
- bounded concurrency, sustained runs, memory growth, handle growth, and cleanup.

The global compatibility matrix and the Experiment Lab feature matrix are separate proofs. In every clean pass, the feature matrix uses the authenticated local control plane to read the exact ten-key registry, select each key, wait for its real refresh to finish, and then capture all ten features at Dark Blue, Light, and High Contrast and at `960` and `1500` DIP. That is 60 linked screenshot/automation cells per pass and 120 for the required two-pass seal. Each cell binds the selected feature and `ready` refresh state to its canonical UI state, requires `ExperimentLabPanel` plus a closed key-specific visible content identity, proves a programmatic focus round trip, and links exact artifact IDs, hashes, source-tree provenance, theme, viewport, and raster density. A missing key, failed refresh, stale selection, absent feature marker, truncated tree, focus mismatch, duplicate linkage, or changed artifact fails closed. This matrix proves each registered feature surface actually rendered; it does not replace the global raster-density and reduced-motion coverage.

Screenshots and WPF structure trees are referenced by bundle-relative identifier and SHA-256. Their provenance records the tested tree fingerprint, capture time, theme, viewport in DIP, raster-density scale, app-observed canonical UI state, linked structure tree, and optional baseline. The app derives that canonical state from the visible surface roots, dialog state, selected view, theme, viewport, process-only motion preference, and raster scale; the caller-supplied expectation is only an assertion and a mismatch fails capture. Stable required control identities must exist as rendered nodes with non-zero bounds, effective opacity, and material viewport intersection. Every feature cell contains exactly its registered feature root and no other registered feature root, its keyboard cycle stays beneath that root, and same-theme/same-width screenshots for different features must be materially distinct. Same-theme/width feature screenshots are compared only within the intersection of both rendered feature-root bounds and both `ExperimentLabPanel` bounds when that intersection covers at least 10% of the viewport: only fully contained physical `128×128` pooled bins count, at least 2% of those bins must have a maximum RGB delta of at least 8, the masked mean channel delta must be at least 2, and the whole decoded-image hashes must still differ. Per-feature cross-theme pairs use the same root-bounded predicate; only the global 36-cell UI theme matrix remains whole-window. The capture also reports the physical display scale as context, but the matrix does not exercise physical or per-monitor DPI.

The compatibility fields and gate IDs still contain `Dpi`, `keyboard`, or `automation` in v1. Their evidence boundary is narrower: `RenderDpiScale` means off-screen raster density; focus movement is programmatic WPF traversal rather than OS keyboard input; the tree is an in-process WPF visual-tree snapshot rather than an external UI Automation query; and motion evidence proves preference plumbing, not animation playback over time. Source fingerprints are checked before and after builds, passes, and authoritative validation, but the run is not executed from an immutable worktree; a transient edit-and-restore between those bounded checks is not cryptographically excluded. These four unavailable boundaries are frozen manifest limitations that become accepted only with the final inspection attestation. PNG evidence allows only `IHDR`, `IDAT`, and `IEND`, verifies CRC, exact zlib-member boundaries, filter reconstruction, dimensions, full-window opacity, and material all-pixel/area-averaged variation. It rejects uniform or near-uniform pixels, sparse/transparent captures, hidden metadata, and visually insignificant cross-theme differences.

Historical `ai_arena.ui_structure_evidence.v1` remains bound to its original 12-field node shape and generic Experiment Lab state so genuine V1 seal bundles keep their original meaning. Current V2 seals produce `ai_arena.ui_structure_evidence.v2`: every privacy-safe node identity is unique, repeated template names and redacted automation IDs receive a bounded visible-tree ordinal suffix, and each node carries the fail-closed geometry, effective-opacity, viewport-intersection, and rendered-state fields required by feature evidence. A V1 seal cannot claim V2 structure evidence, and a V2 seal cannot downgrade to the legacy shape.

## Quality Seal

`scripts/qa-seal.ps1` is the single orchestration entry point. Its ignored local bundle records source commit and tree fingerprint, toolchain, every gate, test counts and durations, sanitized log and render hashes, schema/migration results, performance measurements, optional live coverage, accepted limitations, and a verdict. Repository fingerprints use explicit ordinal case-insensitive path ordering and deduplication with Git path quoting disabled, so Windows PowerShell, PowerShell 7, and the compiled currentness validator produce the same value.

Schema migration evidence is executable rather than declarative: a bounded required gate runs the Core pack-store fixture with the exact scenario/benchmark migration test selected. That fixture directly invokes both named v0-to-v1 migrators and verifies exact-source provenance, canonical v1 decoding, privacy rejection, deterministic identity, and duplicate behavior. Only a passing invocation may set `migratedFromSchema` for the two v1 pack schema checks.

Only the exact material-render failures `bundle.feature_matrix_feature_render` and `bundle.feature_matrix_theme_render` may enter the blocked-fallback path. Any `current.*`, privacy, path, read, hash, missing, UTF-8, schema, PNG, migration, mixed, unknown, or validator failure deletes the invalid run manifest without moving the original capture. For an allowed material-render rejection, the orchestrator builds a fresh ordinary-validator-compatible `blocked` contract with zero clean full passes; it never mutates the rejected candidate. It fails the affected rendered-UI, matrix, feature-surface, and QA-schema claims, transactionally moves their original logs plus all screenshots, structure trees, and matrix documents to an owner-marked ignored sibling under `artifacts/qa-rejected/<opaque-id>`, and binds every downgraded claim to one bounded, sorted, privacy-safe rejection log. The three frozen UI limitation log identities are regenerated with unavailable-boundary-only bytes, source-stability stays unchanged, and valid colliding non-UI or performance claims receive narrowly scoped replacement artifacts. The blocked bundle exposes no inspectable visual closure and cannot be accepted or sealed.

A `sealed` verdict requires:

1. the tested tree still matches the recorded fingerprint;
2. all required gates passed and none are unavailable;
3. screenshots and baselines match the tested source and expected matrix;
4. evidence contains no secrets, transcript/source content, or private absolute paths;
5. the Release app launched and rendered successfully;
6. two clean full passes completed;
7. every rendered screenshot was reviewed through the exact-hash manifest, or a direct CLI invocation included the explicit `-AttestReviewedVisuals` human attestation; and
8. the unavailable OS-interaction, physical-DPI, temporal-animation, and sampled-source-boundary limitations were explicitly accepted with their frozen wording and evidence references.

Without explicit user attestation, automation may produce `partial` or `blocked` evidence, but never `sealed`. In-app acceptance proves that every exact-hash screenshot was opened by the inspector; direct CLI acceptance records only the human's explicit attestation and does not claim that the script observed the pixels. A seal authorizes review of a release candidate; it does not authorize a version bump, installer, tag, push, or publication.
