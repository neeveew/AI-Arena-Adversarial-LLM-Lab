# Unreleased

Changes after `0.4.145-beta`:

- Preserve one completed AI Collaborate exchange when recent-chat refresh, response rendering, or status callbacks fail. Save the model outcome before presentation, retain its original success/failure state, and report a separate privacy-safe warning.
- Keep team generation running through optional trace-preview failures. Restore composer and workspace controls even when a later view refresh keeps failing; retain drafts when the primary history save fails.
- Preserve memory-note drafts and restore the previous notes and chat record if saving fails. Save before updating tool/sidebar views, retain newer drafts, and share privacy-safe presentation warnings with the send workflow.
- Recover controls and the original prompt when required transcript setup fails, without starting model work or writing a fake exchange. Optional recent-chat refresh failures allow the requested run to continue.
- Isolate failing status-view subscribers so owned operation receipts still return, later subscribers receive updates, and Agent/Collaborate calls preserve their real outcomes. Coalesce a privacy-safe status warning without recursively notifying failed listeners.
- Deliver status snapshots in revision order through nested and concurrent publication. Bound notification bursts, retain the latest complete state, and reject stale queued updates or events from an old status-card binding.

Verification evidence for this development increment is under
`artifacts/bugfix-2026-10-06-collaborate-postsave/`. It remains outside the frozen
`0.4.145-beta` installer and will be batched into a future release.

Memory/preflight verification is under
`artifacts/bugfix-2026-10-06-collaborate-workflow/`.

Shared status-observer verification is under
`artifacts/bugfix-2026-10-06-status-observers/`.

Status-ordering verification is under
`artifacts/bugfix-2026-10-06-status-ordering/`.
