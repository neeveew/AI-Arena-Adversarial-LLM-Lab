# WPF Smoke Automation Plan

Goal: turn the current manual visual smoke pass into a repeatable release checklist with small automated checks around the native WPF build.

## Phase 1: Scripted Artifact Checks

- Keep `scripts/wpf-release-sanity.ps1` for stable WPF releases.
- Keep `scripts/modern-preview-sanity.ps1` for side-by-side preview builds.
- Verify release exe, installer exe, changes file, installer identity, AppId, and absence of loose installer exes.

## Phase 2: Launch Smoke

- Add a script option that starts `AI Arena.exe`, waits briefly, verifies the main window title, then exits the process.
- Run against the unpacked release folder first, not the installed copy.
- Keep this optional so CI/dev machines without desktop access can skip it.

## Phase 3: UI Flow Checklist

Automate where practical, then keep the rest as a short manual checklist:

- Navigate Transcript, Custom Match, News, and Settings.
- Open and close Settings drawer.
- Trigger reset confirmation and cancel it.
- Type into Operator Turn without sending.
- Check transcript search text visibility and clear button.
- Verify provider status can render online/offline.
- Open checkpoint controls and confirm buttons are enabled only when valid.

## Phase 4: Behavior-Level Tests

- Add tests for export scope labeling and visible-message export.
- Add tests for prompt construction and operator-priority rules before changing agent behavior.
- Add tests for checkpoint save/restore/delete status paths where possible in shared services.

## Release Gate

Before tagging a WPF beta:

- `dotnet build` passes.
- Shared .NET tests pass.
- Dependency index check passes.
- WPF release sanity script passes.
- One installed-app visual smoke pass is completed.
