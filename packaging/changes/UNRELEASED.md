# Unreleased

Changes after `0.4.147-beta`:

- Keep all Cast pressure and voice selections readable with compact values, visible field labels, current-value tooltips, and role-specific accessibility names. Share picker construction while retaining existing saved option IDs and ordering.
- Keep newly rendered Cast edit, color, lock, and style controls disabled during arena work. Restore them when work finishes and ignore busy style-selection events without writing session state.
- Bind Cast color popups to their opening session and source card. Share the ownership check across Apply, Reset, and presets; reject stale callbacks, close replaced or unloaded editors and editors interrupted by arena work, and keep one current popup. Accepted choices retain their original session through later view changes.
- Keep invalid color drafts open with inline accessible feedback. Retain attempted Apply, Reset, and preset values after a failed save, recover editing controls for retry, and block duplicate choices during a pending save. Close committed changes even if optional refresh fails; retired editors remain closed.
- Share session and control ownership across Match Setup voice, pressure, lock, text-edit, and color actions. Reject events from prior sessions, replaced cards, or detached controls before mutation; keep already accepted work directed to its original session. Release ownership registrations as controls unload and retire obsolete text-editor requests before showing a dialog.
