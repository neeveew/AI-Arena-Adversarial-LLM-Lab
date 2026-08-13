This article collects stable installation, accessibility, data-path, and licence reference. Feature-specific behavior belongs in the task articles so the guide stays searchable.

## Keyboard and accessibility

- `F1` opens the keyboard shortcut reference.
- `Shift+F1` opens contextual Help for the current surface when a route is available.
- `Ctrl+F` focuses Help search while the Help Center is active.
- `Escape` closes transient Help UI or the Help Center and restores focus to its launcher.
- Tab and Shift+Tab follow semantic reading order; arrow keys move within supported navigation lists.
- Toggle switches operate with Space and expose UI Automation Toggle state.

Controls use visible labels, non-color status text, focus rings, and 44-DIP targets. The Help Center reflows for narrow widths and 100–200% scaling and follows Dark, Light, System, and Windows High Contrast behavior.

## Installation

The self-contained installer targets `%LOCALAPPDATA%\Programs\AI Arena` by default and does not require a separate .NET installation. Installed supporting files include `LICENSE`, `NOTICE.md`, release notes, release manifest, `CONTROLPLANE.md`, and the generated full user guide.

App data is separate under `%LOCALAPPDATA%\AI Arena` by default. Uninstalling the program does not imply deleting saved sessions and configs unless that separate removal is explicitly selected.

## Optional SearXNG component

The compact app-only installation does not install SearXNG. A silent full install must explicitly accept its licence with `/TYPE=full /SEARXNGLICENSE=accept`; the installer does not infer acceptance.

## AI Arena licence

AI Arena is distributed under the **Shareable No-Derivatives Software Licence 1.0**.

Copyright © 2026 Dominik Fiala.

You may share the original, unmodified software and use it privately. You may not distribute edited, patched, rebuilt, forked, or derivative versions without written permission from Dominik Fiala. The installed `LICENSE` file is authoritative when this summary and the full terms differ.

## About this Help Center

Help content is packaged as versioned offline articles with stable IDs. The installed `USER_GUIDE.md` is generated from those same article assets for external reading; it is not a second independently edited copy.
