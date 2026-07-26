# AI Arena Handoff Notes

AI Arena is a .NET 10 WPF desktop app for adversarial multi-agent LLM lab sessions, local-provider workflows, an Agent software-building workspace, AI World, and AI Collaborate. The solution is `AI Arena - WPF.sln`.

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

Also add `packaging/changes/<version>.txt`, refresh the dependency index with `scripts/dependency-index.ps1`, then build the installer:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-wpf-installer.ps1 -Version <version>
```

Installer distributions are always self-contained and include the required .NET Desktop Runtime.

Test-only changes and behavior-identical internal refactors do not need an installer.

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

## Current Guardrails

- Prompt golden tests live in `tests/AIArena.Tests/Goldens`. Prompt text changes should intentionally review and update goldens.
- Session snapshot API tokens are protected at rest through `SessionStore.ProtectSecret` in the WPF host.
- Transcript rendering is virtualized through `TranscriptListBox`; Agent and Collaborate chat surfaces remain non-virtualized by design.

## Refactor Direction

Large structural refactors such as full MVVM, DI migration, xUnit migration, or finishing `AgentWorkspaceCoordinator` decomposition should be planned as multi-increment efforts. The next reasonable decomposition step is a workspace scanning service extracted from Agent workspace logic.
