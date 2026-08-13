The Universal Status Center is the one compact cross-app activity surface. In AI Lab it sits directly below Arena Controls, before Operator Turn, so warnings do not push primary controls or editors around.

## Compact view

The box shows at most four meaningful rows with the newest on top. Rows use icon plus text, never color alone, and identify the source, state, summary, and relative time. Routine healthy heartbeats stay quiet; unresolved blockers remain visible; an empty center reads **Ready**.

Sources include Arena, Agent, Collaborate, Models, provider, save, export, copy, and other app operations.

## History dashboard

Select the box or a row to open a compact dashboard to its left. Use filters for **All**, **Running**, **Warnings**, or **Failed**. Select an entry for bounded detail, progress, and any available destination action.

**Clear completed** removes only clearable completed entries. It cannot hide running work or unresolved warnings and failures. History is bounded to the current app run and is not serialized into sessions.

## Destination actions

When an entry identifies a safe destination, its action returns to that app surface—for example Models or Provider Settings. Navigation does not retry the operation, change routing, clear an error, or mutate a session.

## Use status as evidence

Treat the status state and the affected surface together:

1. Read the typed state: Running, Succeeded, Warning, or Failed.
2. Open details for the specific operation identity.
3. Navigate to the source surface if offered.
4. Confirm authoritative state there before retrying.

The compact status view reports outcomes; it is not a replacement for transcript receipts, provider residency evidence, command output, or saved-state confirmation.
