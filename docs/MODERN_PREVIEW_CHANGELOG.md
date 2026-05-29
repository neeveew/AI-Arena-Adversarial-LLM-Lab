# AI Arena Modern Preview Changelog

Version: 0.3.0-modern-preview

## What changed

- Introduced the modern three-panel WPF shell with left navigation, transcript workspace, right arena controls, and bottom status.
- Polished transcript cards with agent-tinted headers, generated/context stats, hover states, search match markers, and framed reasoning/details panels.
- Modernized settings, reset, confirmation, and internet approval dialogs.
- Added clearer provider reachability feedback and model/provider status surfaces.
- Improved transcript search, empty states, export status, and checkpoint controls.

## What to test

- Start, stop, reset, one-turn, auto-chat, narrator, and operator workflows.
- Provider online/offline changes with LM Studio opened and closed.
- Transcript search, filters, reasoning/details expansion, retry, pin, delete, and export.
- Checkpoint save, restore, and delete.
- Settings drawer behavior, model selection, internet approval, and theme switching.

## Known rough edges

- This is a visual preview branch; some deeper workflows may still feel closer to the older layout.
- The installer is side-by-side as AI Arena Modern Preview and does not replace stable AI Arena.
- Provider health depends on the configured local provider URL.

## Rollback

Use the stable AI Arena install from the Start Menu, or uninstall AI Arena Modern Preview from Windows Apps if you want to remove only the preview.
