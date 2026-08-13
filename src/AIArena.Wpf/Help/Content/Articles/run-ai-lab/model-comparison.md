Agent Performance explains one participant's observed run. Model Comparison & QA evaluates whether two aggregate runs are comparable and whether required evidence is complete.

## Agent Performance

Each card can show status, route/model, turn count, output tokens, average latency, context, and activity. Open a card for persona preview, memory count, recent turns, failures, web usage, voice/style cues, and available telemetry.

Missing measurements remain unavailable. They are never converted to zero.

## Capture a fair baseline

1. Prepare and run the scenario you want to test.
2. Open **Model Comparison & QA** and choose **Capture baseline**.
3. Preserve the Match Setup and causal input.
4. Change only the model or provider route being evaluated.
5. Repeat the run and choose **Compare current**.

Comparison requires the same model-neutral scenario fingerprint. Factory and mixed-mode comparisons additionally require the same privacy-safe causal-context profile. Matching turn counts do not prove matching input.

## Read comparison evidence

Compatible runs can compare completed/failed turns, latency, generated tokens, throughput, and locally derived quality evidence. Each metric includes sample counts and a text-plus-icon status.

Factory and mixed runs retain observed runtime metrics. Arena-only voice, role-drift, or Battle Review claims remain unavailable where setup guidance was not consistently applied.

**Copy evidence** exports privacy-safe aggregate JSON. **Copy replay setup** exports the exact secret-free setup needed to repeat configuration; public Factory history remains separate runtime state.

## Run QA

QA checks setup identity, replay package, minimum turn sample, provider/transcript errors, stuck thinking, telemetry completeness, quality evidence, optional Internet evidence, and baseline comparability.

- **Ready:** every required gate passes.
- **Partial:** no required gate fails, but required evidence is warned or unavailable.
- **Blocked:** a required gate fails.

Optional recovery probes remain unavailable until actually run. Once a probe is supplied, its failure is real blocking evidence rather than an inferred pass.

Use separate sessions or forks for candidate runs. Do not edit a baseline in place and assume the earlier evidence still describes it.
