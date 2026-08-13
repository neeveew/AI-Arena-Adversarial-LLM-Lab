AI Arena reports generation failures as typed outcomes whenever evidence supports one. Read the transcript card and Status Center before acting; different failures require different recovery.

## Recovery decision table

| Outcome | What is known | First safe actions |
|---|---|---|
| **Input context limit** | The provider rejected the causal prompt as too large | Increase configured context and reload; choose a larger model; fork; skip the turn; end the match |
| **Output limit reached** | A partial response exists and generation stopped at the output cap | Continue from the preserved partial response; increase output for future turns |
| **Native state exhausted/lost** | Provider-native conversation state cannot continue reliably | Reload/reconnect as offered; fork or reset if the causal continuation cannot be proven |
| **Capacity/busy** | Provider rejected before acceptance because no slot/capacity was available | Wait, inspect provider residency, then retry the same evidenced action |
| **Timeout/disconnect/malformed stream** | Transport failed; acceptance/replay safety depends on phase | Follow the typed retry state; never assume an accepted stream is safe to replay |
| **Unknown/unconfirmed** | The post-request state cannot be proved | Refresh provider evidence; avoid duplicate mutation; preserve the run and fork if needed |

## Input context limit

Strict history keeps exact eligible content and fails rather than omitting it. Rolling 80% can discard the oldest **whole** eligible Arena entries until the newest history fits an estimated 80% input budget, reserving response space. It never clips an included message and never creates a hidden summary.

Rolling 80% requires an explicit numeric configured context. With Provider default, the app cannot truthfully calculate 80%, so the policy remains saved but inactive.

Factory mode ignores Rolling 80%. Its whole-entry public group contract is separate and provider rejection remains authoritative.

## Output limit

The partial response remains visible and attributed. **Continue** creates a new causal request based on that partial output; it does not pretend the original response completed. **Increase output** changes future allowance and may require repeating/forking when exact comparison matters.

## Increase and reload

Changing the configured context does not change the already loaded provider process. For a loaded LM Studio model, use **Reload to apply** and wait for provider-observed effective-context confirmation. An unconfirmed reload is not success.

## Choose fork, skip, reset, or end

- **Fork:** preserve current evidence and explore a different model, configuration, or continuation.
- **Skip turn:** record an intentional scheduler decision and continue with the next role.
- **Reset:** clear the active transcript/live run; use only when that loss is intended.
- **End match:** durably stop run actions while preserving the completed/failed record.

For a comparison run, fork before changing model, context, history policy, root prompt, or recovery path. Those changes can alter causal compatibility.
