# Unreleased

Changes after `0.4.145-beta`:

- Preserve one completed AI Collaborate exchange when recent-chat refresh, response rendering, or status callbacks fail. Save the model outcome before presentation, retain its original success/failure state, and report a separate privacy-safe warning.
- Keep team generation running through optional trace-preview failures. Restore composer and workspace controls even when a later view refresh keeps failing; retain drafts when the primary history save fails.

Verification evidence for this development increment is under
`artifacts/bugfix-2026-10-06-collaborate-postsave/`. It remains outside the frozen
`0.4.145-beta` installer and will be batched into a future release.
