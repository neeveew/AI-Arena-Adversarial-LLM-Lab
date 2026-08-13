The Models surface deliberately separates provider lifecycle from app routing. Search and filters affect only the catalog; loaded models stay visible.

## Understand the four states

| State | Meaning | What it does not prove |
|---|---|---|
| **Available** | The provider advertises the model in its catalog | The model is resident or ready for a turn |
| **Loaded** | Provider-observed evidence reports a resident model process | Any Arena role is assigned to it |
| **Configured** | AI Arena stores behavior settings for that model identity | The provider has applied those settings to a live process |
| **Assigned** | A shared or explicit route points to the model | The model is loaded |

**Loaded Models (N)** is pinned above the catalog and is always rendered, including when empty. Up to three loaded rows display before that region gets its own scrollbar. **Available catalog (N)** is collapsible, starts expanded, remembers its fold state for the current app run, and retains the larger virtualized scrolling area.

Search and facets apply only to Available and load-state-unavailable entries. One selected-model ID is shared across both regions, so a confirmed lifecycle move preserves selection and details.

## Load and unload safely

For LM Studio native mode, select a row and use its state-driven **Load model** or **Unload model** action. AI Arena does not move the row just because an HTTP request returned successfully. It refreshes provider evidence and moves the row only after confirmation.

- **Awaiting confirmation** means the request outcome is not yet proven.
- **Unavailable** means residency cannot be observed; the catalog remains usable without inventing a load state.
- A failed or ambiguous lifecycle action does not change assignments.

Provider hardware placement, GPU offload, and process ownership remain provider concerns.

## Route with switches

The switches are independent targets with immediate causal save and rollback:

- **Default on:** the selected model becomes the shared provider model and unassigned Arena roles may inherit it.
- **Default off:** the shared model remains available to Agent Workspace, connection testing, and diagnostics, but Arena fallback is disabled.
- **Role on:** the selected Alpha–Theta or Narrator role gets an explicit route.
- **Role off:** that role becomes **Uses default** when fallback is on, or **Unassigned** when fallback is off.

An explicit role assignment remains explicit even when it points to the same model as Default. This distinction survives save, restart, fork, reset, profile round-trip, and Match Setup v4 import/export.

## Resolve assignment conflicts

Each target has one effective model. Turning the same role on for a different model transfers that explicit target atomically; two loaded models do not simultaneously serve one role. Other role assignments and residency remain unchanged. If a save fails or becomes stale, the switch rolls back to authoritative state and Status Center reports the outcome.

Multiple roles may intentionally share one model. One model may also be both Default and explicitly assigned to selected roles.

## Readiness when Default is off

Arena mode can run with fallback disabled only when every active participant that may be scheduled has an explicit model. Narrator, Collaborate roles, and role-based experiments likewise need resolvable routes for the operations that use them. Inherited targets become **Unassigned**; dormant temperature or output overrides are preserved for later use.

Changing assignment never triggers model load, unload, or reload.
