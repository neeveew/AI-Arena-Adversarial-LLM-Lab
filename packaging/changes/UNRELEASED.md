# Unreleased

Changes after `0.4.146-beta`:

- Update bundled Werkzeug to the verified 3.1.9 security wheel, fixing Windows device-path validation (GHSA-g6x2-hccm-hh4m). Retain pristine SearXNG source and dependency licences, and verify device-path rejection and the gateway route boundary against the bundled runtime.
- Keep background checkpoint refreshes from replacing newer Sessions or Templates feedback. Reject pending list results and errors after checkpoint state is cleared, while current failures remain visible and successful loading preserves the selected checkpoint and typed name.
- Bring the chosen Match Setup editor into view after layout. Keep the latest section and keyboard focus through rapid navigation, ignore closed or detached targets, and preserve subsequent reader scrolling. Saved State opens at its editor fields with normal-height action buttons and a concise loaded-session message.
- Keep Cast role names readable by separating their wrapping title row from actions, removing redundant identity chips, and retaining full-title tooltips.
