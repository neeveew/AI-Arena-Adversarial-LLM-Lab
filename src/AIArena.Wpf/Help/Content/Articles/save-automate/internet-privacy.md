Internet access is optional and off/on under Settings. When enabled, eligible Arena and Collaborate work can use local search and safe public-page fetch as untrusted evidence.

## Search and fetch

- `web_search` queries the bundled/reconnected SearXNG backend and returns ranked, deduplicated sources.
- `fetch_url` retrieves one credential-free public HTTP or HTTPS page through bounded redirect, address, and response-size checks.
- Test Internet can verify the local search/fetch chain without an active session and while model Internet use is off.

Private, loopback, link-local, special-purpose, and unsafe redirect destinations are blocked for page fetching. Search/fetch content is untrusted and does not become a command or app action. Models are asked to connect factual claims to numbered sources when applicable.

## What stays local

The following are local unless you explicitly export or send them elsewhere:

- sessions, checkpoints, templates, app settings, and collaboration history;
- bounded unsent Operator, Agent, and AI Collaborate drafts protected for the current Windows user;
- bounded evaluation and Experiment Lab artifacts;
- Status Center's current-run history;
- Help Center content and search index;
- logs, caches, command receipts, and local exports.

## What can leave the machine

The configured provider receives prompts and selected context needed for an operation. A remote OpenAI-compatible endpoint is still remote even though AI Arena - Lite stores its own state locally. Explicit web search/fetch contacts SearXNG and selected public sites. Installer download or provider model download actions may also use the network when explicitly chosen.

An AI Collaborate document remains local while it is merely selected. Starting a provider-backed collaboration sends the bounded imported text included in that request, but AI Arena - Lite does not add the document's source path to the provider prompt, context receipt, or import failure status. Current-user protection for unsent drafts is an at-rest safeguard, not a reason to place credentials in a composer.

Never place secrets in scenarios, transcript messages, custom tones, exported setup files, or PowerShell command history. Provider API tokens are excluded from portable Match Setup, copied evidence, and normal control-plane state.

## Data minimization evidence

Comparison and QA exports use aggregate metrics, hashes, counts, compatibility profiles, and bounded receipts rather than transcript bodies or provider error bodies where raw content is not required. Missing evidence stays unavailable.

User-facing coded failure summaries and copied support details omit raw exception messages, local paths, prompts, and credentials. A stable `AA-<context>-<category>` code identifies the affected product area and failure class without making the hidden text safe to disclose elsewhere.

Use Internet off for a closed-book run. Turn it on only when external evidence is part of the experiment, and record that policy when comparing runs.
