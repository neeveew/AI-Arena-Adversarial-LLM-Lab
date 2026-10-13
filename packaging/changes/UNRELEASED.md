# Unreleased

Changes after `0.4.149-beta`:

- Bound optional post-save activity-log waits using the same shared 500 ms policy as provider workflows. A held diagnostic lease can no longer hide completed changes or delay checkpoint-restore refresh; pending writes are cancelled, atomic writes already underway are observed without replay, and saved outcomes retain a safe evidence warning.
- Preserve saved roster and relationship changes when activity logging, view refresh, or optional feedback fails. Share session-aware completion handling, keep the committed result under late cancellation and session switches, and retain authoritative failures as failures.
