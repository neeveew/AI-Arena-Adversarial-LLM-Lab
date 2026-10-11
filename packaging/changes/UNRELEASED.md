# Unreleased

Changes after `0.4.147-beta`:

- Keep all Cast pressure and voice selections readable with compact values, visible field labels, current-value tooltips, and role-specific accessibility names. Share picker construction while retaining existing saved option IDs and ordering.
- Keep newly rendered Cast edit, color, lock, and style controls disabled during arena work. Restore them when work finishes and ignore busy style-selection events without writing session state.
- Bind Cast color popups to their opening session and source card. Share the ownership check across Apply, Reset, and presets; reject stale callbacks, close replaced or unloaded editors and editors interrupted by arena work, and keep one current popup. Accepted choices retain their original session through later view changes.
