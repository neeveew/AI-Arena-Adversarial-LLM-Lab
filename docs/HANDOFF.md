# AI Arena - Lite Handoff Notes

AI Arena - Lite: Adversarial LLM Lab is a .NET 10 WPF desktop app for adversarial multi-agent LLM lab sessions, local-provider workflows, an Agent software-building workspace, AI World, and AI Collaborate. The compatibility-stable solution name is `AI Arena - WPF.sln`.

The codebase is intentionally coordinator-heavy imperative WPF code-behind rather than MVVM. Prefer bounded coordinator/helper changes that match the existing shell structure unless a dedicated refactor pass is planned.

## Projects

- `src/AIArena.Core` contains provider clients, turn running, persistence, transcript, narration, internet, and match-generation services.
- `src/AIArena.Wpf` contains the WPF shell, coordinators, dialogs, controls, settings, and release UI.
- Tests are custom console runners, not xUnit.

## Release Discipline

User-facing changes ship with a version bump and installer. Update:

- `src/AIArena.Wpf/AIArena.Wpf.csproj`
- `packaging/inno/ai-arena-wpf.iss`
- `scripts/build-wpf-release.ps1`
- `scripts/build-wpf-installer.ps1`
- `scripts/wpf-release-sanity.ps1`

Development work is intentionally batched between releases. Keep the declared
product version and README download link on the latest published release while
changes accumulate in `packaging/changes/UNRELEASED.md`. Do not create a new
version or installer for every small change. When the repository owner decides
the batch is substantial enough to publish, promote the verified unreleased
bullets into `packaging/changes/<version>.txt`, bump the build version surfaces,
and build the release. The publisher tags and publishes the approved build,
then advances the README links after checking public availability. If a build
is held as a draft, the README stays on the previous public release.

Also add `packaging/changes/<version>.txt`, refresh the dependency index with `scripts/dependency-index.ps1`, then build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-wpf-installer.ps1 -Version <version>
```

Installer distributions are always self-contained and include the required .NET Desktop Runtime.

Test-only changes and behavior-identical internal refactors do not need an installer.

Keep the README download and release-notes links on the last published release when bumping the source version. A draft is not publicly downloadable, even when the owner's GitHub credentials can see it. The publisher updates the README only after publishing the release with its installer and verifying public access using `scripts/check-published-release.ps1`.

`wpf-release-sanity.ps1` checks the README link's format and matching release notes independently of the build version. It retains the payload source-identity checks: the build stamps `Source commit:` and `Source tree (src):` into `release-manifest.txt`, and an installer built before a later `src/` commit is refused. If app source lands after the build, bump the version and rebuild; the build scripts deliberately refuse to overwrite an existing versioned distribution.

The `Build and test` workflow checks the advertised release and installer on every run, including daily. It fails for drafts, missing installers, or broken public links; a newer source version alone is not a publication failure.

## Verification

Before shipping a production change:

```powershell
dotnet build "AI Arena - WPF.sln" --no-restore
dotnet run --project tests\AIArena.Tests\AIArena.Tests.csproj --no-build
dotnet run --project tests\AIArena.Wpf.Tests\AIArena.Wpf.Tests.csproj --no-build
powershell -ExecutionPolicy Bypass -File scripts\wpf-release-sanity.ps1 -Version <version>
```

Smoke-launch the built exe for production changes. The automated tests do not prove the rendered UI looks right.

Use `dotnet build-server shutdown` or force a rebuild if incremental builds appear to run stale test assemblies.

## Known Pitfalls

- Thinking models can return empty public content when reasoning consumes the token budget. Use larger token budgets and retry with `Reasoning = "off"`.
- WPF compiled XAML can apply `IsChecked="True"` after checked handlers are attached during `InitializeComponent`. Settings-save handlers need readiness/suppression guards.
- Top-level-statement `Program.cs` files cannot be split across files until converted to an explicit partial `Program` with `Main`.
- `using static` is useful for extracting static helpers with minimal call-site churn.
- Avoid flaky STA render tests that depend on virtualized WPF containers being realized in headless `Measure/Arrange`.
- Do not run the payload's bundled `searxng/python/python.exe` against a built release in `dist`. Importing any module writes `__pycache__` into the payload, which then fails the release checksum manifest with a file-count mismatch. The build strips those directories, so removing them restores the payload. The same applies to launching the app from a `dist` payload directory rather than an installed copy.

## Current Guardrails

- Prompt golden tests live in `tests/AIArena.Tests/Goldens`. Prompt text changes should intentionally review and update goldens.
- Session snapshot API tokens are protected at rest through `SessionStore.ProtectSecret` in the WPF host.
- Arena readiness has one visible owner in `ApplicationStatusCenter`; compatibility text must bypass the legacy live mirror, while disabled controls retain accessible prerequisite help.
- Transcript rendering is virtualized through `TranscriptListBox`; Agent and Collaborate conversations use `VirtualizingConversationPanel` with reader-preserving follow-live and an accessible Jump action.
- AI Collaborate document import is asynchronous, cancellable, extension/encoding checked, path-private, and bounded by candidate, retained-document, per-document, and combined tool-context limits.
- The six Advanced settings numeric fields must pass `ArenaSessionMutationCoordinator` validation before Apply; invalid drafts remain visible and use WPF plus UI Automation error state rather than silent clamping.
- A genuine active-session change stops current and still-starting voice playback before loading the new snapshot; same-session refreshes do not.
- `ComposerDraftStore` keeps bounded Operator, Agent, and Collaborate drafts under current-user Windows protection, scopes them to durable identities, merges cross-process updates, and clears only an unchanged successfully consumed draft.
- Session and checkpoint deletion uses bounded seven-day Trash receipts and Undo. Whole-snapshot Reset, template apply, and checkpoint restore require an automatic durable safety checkpoint when live state would be replaced.
- User-facing workflow failures should pass through `AppErrorPresenter` so visible and copied details stay bounded, redacted, and identified by a finite stable support code.
- Provider completion replay requires an explicitly configured end-to-end idempotency-key capability, same-authority response evidence, the identical payload/key, and a retry delay within policy. Never infer replay safety from loopback location, busy text, missing tokens, timeout, cancellation, or a partially accepted stream.

## Refactor Direction

Large structural refactors such as full MVVM, DI migration, xUnit migration, or finishing `AgentWorkspaceCoordinator` decomposition should be planned as multi-increment efforts.

The workspace scanning service this section used to nominate is done: `WorkspaceScannerService` has existed since 0.4.114-beta.

The open structural imbalance is that `AIArena.Core` carries roughly 13k lines against 70k in `AIArena.Wpf`, so most domain logic is only reachable through STA WPF tests. Lifting provider and arena logic out of the shell into Core is the next reasonable increment.
