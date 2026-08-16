Agent is a first-class software workspace for planning, reviewing, building, and verifying work inside a selected folder. It is separate from AI Lab participants and AI Collaborate roles.

Agent uses the shared provider model even when **Default for unassigned agents** is off. Arena personas, relationships, and fallback policy do not route the Agent team.

## Start safely

1. Open **Agent** from the left rail.
2. Choose a trusted workspace folder.
3. Review the bounded workspace profile and any Solution Doctor findings.
4. Enter a concrete software task.
5. Inspect Planner, Reviewer, and Builder output.
6. Preview any staged command before approval.
7. Read terminal output and the file-change receipt.
8. Stage and run an appropriate verification command.

The software roles are Planner, Reviewer, and Builder. They do not reuse Alpha/Beta/Gamma personas.

## Command safety boundary

Commands run from the selected workspace. Preview shows shell, exact command, working directory, origin, and risk. Outside-workspace paths, parent traversal, destructive actions, install/network work, elevation, long-running previews, and other high-risk cases block or require explicit review.

**Full Access** can automatically run preview-ready low-risk Agent commands for the current workspace session. It does not bypass validation. Workspace change or Agent clear resets it, and risky or blocked previews still stop for manual action.

**Auto Continue** spends a bounded follow-up budget and stops on repeated commands, no-change loops, cancellation, risk, blocked preview, workspace change, or exhausted budget.

## Evidence surfaces

- **Progress:** live Planner, Reviewer, and Builder phases.
- **Runbook:** durable plan, review, build, approval, execute, and verify steps under one run ID.
- **Build Evidence:** readiness, proposal, command/result, file changes, artifacts, diagnostics, and test evidence.
- **Outputs:** compact artifact and result summaries.
- **Activity:** current model/command work.
- **Advanced:** approval rail, terminal output, and command history.

A process success without expected file changes is treated as suspicious for a build task. Read-only, build, test, and preview commands may legitimately change no files.

## Read live output without losing your place

Agent conversation rows are virtualized so long threads do not require every card to remain rendered. While the view is near the newest message it follows live additions. If you scroll back, Agent preserves the historical position and text selection, counts unseen messages, and shows an accessible **Jump to latest** action instead of snapping away from what you are reading.

## Solution Doctor and Impact Explorer

Solution Doctor reports only bounded evidence it can prove from project files and command output. Repairs produce a proposed diff, require explicit approval, and become stale if the source hash changes. Verification remains a separate approved action.

Impact Explorer is opt-in for trusted .NET solutions because loading a solution evaluates MSBuild project files. Static relationships and test suggestions are not runtime coverage claims.

## Restart and recovery

Runbooks are workspace-bound and persisted without source bodies or secrets. A step that was Running when the app stopped restores as Blocked with an interruption checkpoint; it is never silently rerun. Resume stages an editable prompt for the first incomplete step.

An unsent Agent composer draft is stored separately under a normalized, hashed workspace identity and protected for the current Windows user. Returning to that workspace or restarting the app restores the draft. A failed or cancelled request, or text edited while a request is running, remains available; only the exact unchanged draft associated with a successful action is cleared. A control-plane request does not replace or clear the visible draft.

Use Status Center for the cross-app outcome, then return to Agent for authoritative command output, receipts, and verification.
