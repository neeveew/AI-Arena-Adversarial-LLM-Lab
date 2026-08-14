# AI Arena - Lite Help Center design and coverage contract

Status: implementation contract for the User Guide overhaul.

## Outcome

The in-app User Guide becomes a modeless, offline Help Center. It is a task-oriented companion to AI Arena - Lite rather than a second settings screen or a lightly styled Markdown viewer. A reader must be able to keep it open, follow a procedure in the app, return to the same article and scroll position, and use the external `USER_GUIDE.md` as the same content in a portable form.

The Help Center must never change provider configuration, model residency, routing, or session data merely because an article or help link was opened. Explicit article actions may only navigate to and focus a known app surface.

## Reader journeys

| Reader | First goal | Primary path | Advanced detail |
|---|---|---|---|
| First-time user | Complete one successful turn | Home -> Quick Start -> Models & Routing -> Arena mode | Provider troubleshooting |
| Returning operator | Find a changed control or recover a run | Search or contextual help -> focused task article | Sessions, recovery, diagnostics |
| Model evaluator | Run comparable evidence | Models & Routing -> Factory mode -> Model Comparison & QA | Context receipts and comparison identity |
| Agent user | Start safe workspace work | Agent workspace article | Runbooks, approval, command history, PowerShell |
| Advanced operator | Automate or diagnose behavior | Troubleshooting & Reference | Control plane, schema and migration references |

Essential instructions appear before implementation detail. Protocol hashes, wire formats, exhaustive command catalogs, and migrations belong in labelled advanced reference sections rather than the default task path.

## Information architecture

The manifest is authoritative. Each article has a stable ID, group, title, summary, search terms, icon, reviewed version, related articles, safe route, and content file.

1. **Start Here**: Home and Quick Start.
2. **Models & Routing**: Models & Assignments and per-model behavior.
3. **Run AI Lab**: Arena mode, Factory mode, Operator controls, Match Setup, Universal Status Center, and Model Comparison & QA.
4. **Other Workspaces**: Agent, AI Collaborate, Experiment Lab, and AI World/Debug.
5. **Save & Automate**: Sessions/setups, Internet/privacy, and PowerShell/control-plane guidance.
6. **Troubleshooting & Reference**: Context recovery, provider troubleshooting, licensing, and advanced reference.

Article titles may change without breaking contextual help. App code and cross-links use stable article IDs, never visible titles.

## Current coverage and migration map

| Product surface or contract | Target article | Current guide state | Required correction |
|---|---|---|---|
| Install through first turn | `quick-start` | Present, partly stale | Use switches, distinguish loaded from assigned, explain all-active routing and Factory input |
| Provider connection and test | `models-routing` | Split across Quick Start and Settings | Make connection, residency, routing, and behavior distinct concepts |
| Loaded/Available catalog | `models-routing` | Partial | Add pinned Loaded region, collapsible Available catalog, current-run fold state and confirmation evidence |
| Default/explicit routing | `models-routing` | Partial | Add Default off semantics, Explicit/Uses default/Unassigned labels, aliases and atomic owner transfer |
| Per-model context/history/tone | `model-behavior` | Buried in Settings | Explain configured/effective context, Strict/Rolling 80, receipts, tone versus persona/voice, reload |
| Arena run controls | `arena-mode`, `operator-controls` | Present | Match current readiness, Operator/Narrator behavior and rail order |
| Factory mode | `factory-mode` | Dense and buried | Give it a separate workflow with public root, strict history, causality and comparison rules |
| Context/output recovery | `context-recovery` | One dense paragraph | Separate typed outcomes and provide a truthful action decision table |
| Universal Status Center | `status-center` | Present, partly stale | Whole-card activation, four newest rows, left-opening history and collapsed-rail access |
| Match Setup | `match-setup` | Very long | Split core workflow from generators, matrices, portability and advanced reference |
| Model Comparison & QA | `model-comparison` | Present, dense | Lead with repeatable workflow and comparability requirements |
| Agent Performance | `model-comparison` | Present | Place after Operator Turn and cross-link evidence inspection |
| Agent workspace | `agent` | Extremely long | Split first task, permissions, runbooks, recovery and outputs into scannable sections |
| AI Collaborate | `collaborate` | Contains stale fallback implications | Explain explicit/default/unassigned routing consistently with Arena policy |
| Experiment Lab | `experiment-lab` | Present | Lead with registered features and move schema detail to advanced sections |
| AI World and Debug | `ai-world-debug` | Scattered | Explain gating, experimental status and how to return to supported workflows |
| Sessions, checkpoints, forks and setups | `sessions-setups` | Present | Clarify clean save versus fork, reset behavior, portability and local paths |
| Internet/SearXNG and privacy | `internet-privacy` | Present | Prioritize safe enable/test/use workflow, then network restrictions and evidence handling |
| Provider/llama.cpp troubleshooting | `provider-troubleshooting` | Present | Add decision paths and direct safe navigation actions |
| PowerShell/control plane | `powershell-control` | Embedded in Agent | Move exhaustive command material to dedicated advanced reference |
| Licensing/install storage | `licensing` | Present | Retain accurate offline/install/uninstall and local-data guidance |

## Window and interaction contract

- The window remains modeless, owned by MainWindow, single-instance, resizable, and confined to the monitor work area.
- Wide layout uses grouped navigation plus one readable article column. Medium and narrow layouts use a topic drawer without hiding search or article actions.
- The command bar contains Back, Forward, Home/User Guide, Search, overflow actions, and Close. The external guide action lives in overflow; there is no duplicate footer Close button.
- Search does not replace the article on every keystroke. It produces ranked results with topic, title, snippet, highlights, result count, keyboard selection, and an empty state.
- Back/Forward, breadcrumbs, Previous/Next, related topics, last article, and article scroll positions all use stable IDs.
- Escape closes the topmost Help Center layer first, then the window. Closing restores focus to the live launcher when possible.
- The existing shell F1 shortcut remains the shortcut reference. Contextual help may use explicit Help controls and Shift+F1 so no existing keyboard contract is silently replaced.

## Safe application routes

Only a fixed allow-list is accepted. Initial routes are `app/view/arena`, `app/models`, `app/settings/provider`, `app/match-setup`, `app/status-center`, `app/view/agent`, `app/view/collaborate`, `app/view/experiment`, `app/settings/debug`, and `app/settings/internet`.

Unknown routes, malformed article IDs, arbitrary processes, filesystem paths, script schemes, and non-HTTP external links fail safely. Article navigation and app navigation are separate operations. External HTTP/HTTPS links require an explicit user action and use the existing safe shell launcher.

## Accessibility contract

- Semantic article heading levels and a logical navigation-to-content reading order.
- Labelled search with result count announced politely only when it meaningfully changes.
- Visible shared focus treatment, predictable Tab/arrow behavior, and 44-DIP primary action targets.
- Icon plus visible text for state and destination; no colour-only meaning.
- Keyboard operation for search, topics, history, overflow, related articles and article actions.
- Dark, Light and Windows High Contrast support through shared Arena resources.
- Reflow and full reachability at 960/1500 DIP and 100/150/200 percent scaling.
- No claim of full assistive-technology compliance until keyboard, UI Automation and screen-reader verification is complete.

## Content freshness and release gate

The checked-in external guide must be deterministically generated from the structured manifest and article files. `-Check` mode fails when generated output differs, article IDs are duplicated, group references are invalid, content files are missing, related IDs do not resolve, routes are not allow-listed, required metadata is blank, or reviewed versions lag the declared guide version.

Every first-class product feature key and contextual-help registration must resolve to an article. Canonical control labels used by task instructions must be checked against source-owned labels or a small explicit label catalog. The release gate also validates internal links, packaged content assets, safe routes, search/index behavior, renderer behavior, responsive hosted layout, themes, keyboard/focus behavior, XAML literals, dependency inventory, full harnesses, and accepted visual evidence.

## Definition of done

A new user can reach a successful first turn using only the Help Center. Every first-class surface and critical blocked/recovery state has a stable searchable article. The in-app and external guide are one content source. Navigation is non-mutating, accessibility and scaling contracts are verified, packaged offline content resolves after installation, and the complete Release verification matrix is green.
