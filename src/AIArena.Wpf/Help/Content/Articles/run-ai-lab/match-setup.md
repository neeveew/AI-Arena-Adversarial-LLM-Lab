Match Setup defines the scenario and cast for Arena mode and stores the Arena/Factory policy. It opens as a wide AI Lab surface with grouped Generate, Tune, Agents, Recent, and Saved State controls.

## Build a setup

- **Generate:** Manual, deterministic Random Seed, provider-backed AI Choice, or experimental Wild Seed.
- **Tune:** role pack, style, pressure, absurdity, preset, and relationship matrix.
- **Agents:** resize the active cast from one to eight participants.
- **Recent:** replay or branch saved generated setups.
- **Saved State:** sessions, restore points, templates, fork, import, and export.

Scenario Preview shows the topic, global instruction, setup profile, run shape, relationship map, lock plan, source, and run constraints. Cast cards keep personas, locks, pressure, and voice styles visible.

## Read readiness correctly

Readiness separates **blockers** from **warnings**. Blockers can include an incomplete cast, missing Arena guidance, unresolved model routes, or an enabled relationship matrix with no valid active rule. Provider offline, blank personas, or missing optional evidence may appear as warnings depending on the selected mode and action.

The preflight checklist is authoritative for the current setup. A loaded model does not satisfy a missing route.

## Generate and lock

Random Seed is deterministic for the same seed, recipe, and active cast. AI Choice calls the configured Narrator route. Current Topics additionally requires enabled Internet search. Wild Seed uses a broader experimental generator.

Lock topic, global instruction, or cast fields that must survive regeneration. Recent history distinguishes deterministic seed replay from captured-output replay; a saved AI/web result can replay exactly without claiming a fresh call would regenerate it.

## Pressure, persona, voice, and tone

- **Persona** defines identity, expertise, incentives, and blind spots.
- **Pressure** changes how hard a participant challenges or supports.
- **Voice style** is a per-role expression contract.
- **Model tone** is configured on the Models surface and applies wherever that model is routed.

These layers are intentionally distinct so experiments can change one variable at a time.

## Relationship Matrix

Choose or edit a pattern, inspect the pressure graph, then apply it. Only active valid rules enter Arena prompts. Neutral, inactive, invalid, or out-of-cast targets are excluded and shown honestly in readiness.

## Portable setup

**Copy JSON** emits `ai_arena.match_setup.v4`. V4 contains the fallback policy, explicit/inherit assignment modes, model context/history/tone settings, scenario, cast, relationships, Internet policy, and non-secret provider configuration.

Import also accepts v2 and v3 with documented legacy defaults. It validates the complete package before writing and creates a clean collision-free session; it never replaces the current run. API tokens, absolute local paths, transcript history, private runtime content, and provider residency evidence are excluded.
