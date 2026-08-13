Context, Arena history, and response tone belong to a model identity. Every Arena role routed to that identity uses the same saved model behavior. These settings do not assign a model or define a persona.

## Configured versus effective context

- **Provider default** stores no numeric override. AI Arena cannot truthfully calculate a bounded history budget from it.
- An explicit **Context window** accepts 512–1,048,576 tokens and becomes AI Arena's configured budget.
- **Effective context** is provider-observed residency evidence when the provider exposes it. It may differ from the configured value until a loaded process is replaced.

The provider tokenizer and server remain authoritative. AI Arena's token estimates and receipts are planning evidence, not a promise that every provider will count identically.

## Choose an Arena history policy

| Policy | Behavior | Best for |
|---|---|---|
| **Strict** | Keeps the exact eligible Arena history and surfaces a context failure if it cannot fit | Reproducibility and no omission |
| **Rolling 80%** | Keeps the newest whole eligible entries within an estimated 80% input budget while reserving output space | Long-running Arena conversations |
| **Chaptered — Coming later** | Visible for planning but not currently selectable | Future durable chapter boundaries |

Rolling 80% never trims an included entry and never invents a hidden summary. Every attempted turn can record privacy-safe evidence including estimated tokens, included and omitted entry counts, the retained boundary, and a content fingerprint.

Rolling 80% is saved but inactive with **Provider default**, because there is no honest numeric budget. Set an explicit context window to activate bounded selection.

> [!TIP] Factory mode has a separate `public_group_v1` history contract and ignores the Arena Strict/Rolling 80 setting.

## Configure model tone

Tone options are **Default**, **Neutral**, **Concise**, **Analytical**, **Creative**, **Direct**, and **Custom**. A custom tone is a single normalized instruction of at most 240 characters.

Tone is model-level response guidance. It is deliberately separate from:

- **Persona**, which defines an Arena participant's identity, expertise, incentives, and blind spots;
- **Voice style**, which is a per-role presentation constraint such as Scientific or Executive brief;
- speech synthesis voice, which changes audio playback rather than generated text.

Factory mode keeps sampling and model-level tone but omits Arena persona, voice, pressure, relationship, and private-memory guidance.

## Apply a changed context to a loaded model

Saving behavior is not a residency operation. When LM Studio reports a loaded process with a different effective context—or when an explicit override returns to Provider default—the action becomes **Reload to apply**.

Reload performs an explicit unload/load sequence. Success is shown only after provider evidence confirms the replacement process. A definite rejection is **Failed**; an ambiguous post-request result is **Unconfirmed**. Provider default never produces an invented numeric effective-context claim.

Before changing context during a comparison, save or fork the run. A configuration change can make later causal inputs non-comparable with an earlier baseline.
