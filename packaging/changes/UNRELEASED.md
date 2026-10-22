# Unreleased

Changes after `0.4.150-beta`:

- Reduce temporary allocations while validating saved experiment artifacts by reusing decoded property names and avoiding absolute-path matching for values without path separators. Privacy, duplicate-member, and canonical-format checks remain unchanged.
- Bind Live Agents controls to the session that rendered them and reject retired controls or queued Pause/Resume/Solo changes after a session switch. Keep Solo from muting the remaining cast when its target disappears, and share saved-change completion so optional logging or refresh failures cannot hide a durable change. Preserve control reuse during updates within the same session.
