# Modern GUI Merge Readiness

Branch: `modern-gui-shell`  
Base reviewed: `master` at `1de40c1` / `v0.2.2`  
Status: **Ready for a review merge after one final visual smoke pass on the freshly rebuilt installer.**

## Commits Reviewed

- `5150091` Modernize custom match previews
- `14ae343` Polish news inspector and preview sanity
- `82772af` Polish checkpoints and preview changelog
- `dd439e9` Improve session export feedback
- `e321998` Polish empty states and transcript search
- `0ef7fcf` Polish modern transcript interactions
- `fe93107` Modernize modal dialogs
- `4bca1fd` Polish modern settings drawer
- `dd0bb3c` Add modern preview identity settings drawer and export
- `e53213a` Point WPF installer at modern preview
- `4954c8b` Add transcript toolbar presets and counts
- `fe48a93` Give transcript search an explicit visible template
- `22841b7` Fix transcript search readability
- `fc4d133` Polish modern shell navigation and filters
- `56b7d3c` Modernize sidebar overview and agents
- `5f3954d` Modernize transcript cards and filters
- `5d25afa` Start modern WPF shell layout

## Scope

- WPF shell, transcript cards, sidebar controls, settings drawer, dialogs, Custom Match, News/Internet inspector, checkpoint controls, search/filter UI, and export feedback.
- Preview packaging identity for side-by-side `AI Arena Modern Preview`.
- Preview release notes and sanity script.

## Risk Split

Low-risk UI-only:
- Layout restructuring and visual polish in `MainWindow.xaml`.
- Transcript card visuals, hover states, empty states, search highlighting, and inspector card formatting.
- Dialog, settings drawer, Custom Match, checkpoint, and news inspector presentation.

Medium-risk behavior-adjacent:
- Transcript filtering presets and search feedback affect which messages are visible and exported.
- Export now uses currently visible/filtered transcript messages and reports the target filename/path.
- Checkpoint controls now have improved status and confirmation text, while save/restore/delete calls remain the existing store APIs.
- Preview installer identity intentionally changed to side-by-side `AI Arena Modern Preview`.

Deferred:
- Prompt/persona cooperation tuning. The current branch does not change model behavior prompts for the rude/refusal issue.

## Verification

- `dotnet build "windows-wpf/src/AIArena.Wpf/AIArena.Wpf.csproj" -p:OutputPath="bin\CodexVerify\"` passed.
- `dotnet run --project "shared-dotnet/src/AIArena.Tests/AIArena.Tests.csproj"` passed after clearing the local compiler-server lock.
- `scripts/modern-preview-sanity.ps1` passed.
- Fresh installer compiled successfully:
  `dist/installer/AI Arena - 0.3.0-modern-preview/AI Arena Setup 0.3.0-modern-preview.exe`

## Merge Recommendation

Merge readiness is **green for a review merge**, with one practical caveat: run a final installed-app visual smoke pass from the freshly rebuilt installer because the branch is heavily UI-focused.
