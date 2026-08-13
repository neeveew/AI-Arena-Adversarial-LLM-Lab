Experiment Lab hosts evidence-backed advanced tools. It is hidden until **Settings → Visuals → Allow debug controls** is enabled. Missing or unsupported evidence is shown as unavailable rather than synthesized.

## Available tools

| Tool | Purpose |
|---|---|
| **Matrix Runner** | Expand a versioned experiment into deterministic provider/dimension/repetition cells with bounded concurrency and restart state |
| **Conversation Fork** | Create a cursor-scoped child and conservatively project transcript, narration, and provable memory |
| **Scenario Packs** | Import size-bounded, schema-checked versioned scenarios and benchmarks |
| **Rubric Studio** | Keep human, deterministic, provider-judge, blind, and unavailable observations distinct |
| **Claim Ledger** | Link bounded claims, sources, contradictions, reviewers, and transitions to stable messages |
| **Context & Prompt Inspector** | Show process-memory request hashes, byte counts, redacted previews, transformations, omissions, retries, and token evidence |
| **Agent Memory Debugger** | Inspect one selected agent's private memory with provenance, revision, expiry, and correction evidence |
| **Fault-Injection Lab** | Arm one bounded process-only timeout, disconnect, malformed stream, saturation, empty response, interruption, or context-pressure probe |
| **Routing Optimizer** | Propose only routes supported by compatible completed evidence and current-hardware observations |
| **In-App QA Inspector** | Inspect local Quality Seal bundles, run allowlisted suites, preview captures, and create privacy-safe review evidence |

## Important boundaries

- Matrix retry approval is one-shot and preserves already completed cells.
- Conversation Fork cannot invent historical provider/setup checkpoints that were never stored; omissions are explicit.
- Provider judge results require a matching succeeded request receipt and strict result shape.
- Fault injection records injected cause separately from observed recovery.
- Routing proposals require explicit approval and a final concurrency check.
- QA Inspector does not replace human visual review and cannot claim physical-display or OS interaction evidence from off-screen rendering alone.

Experiment Lab sends data to a provider only for an explicitly started provider-backed operation such as a matrix cell, judge, or probe. Selecting a feature or opening its local evidence does not initiate a model call.

For a normal model comparison, start with [Performance, Comparison & QA](help/model-comparison). Use Experiment Lab when you need versioned repetitions, specialized rubrics, faults, or deeper causal inspection.
