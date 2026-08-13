Debug controls expose optional experimental surfaces without changing the default Arena workflow. Enable them under **Settings → Visuals → Allow debug controls**.

## AI World

Enable **Debug → AI World (3D)** to reveal the Transcript/World selector in AI Lab. AI World visualizes live session agents as robots with name tags, speech bubbles, status beacons, a minimap, an inspector, and compact telemetry.

Camera controls include Follow, Free, Overview, orbit/pan/zoom, agent cycling, and optional cinematic motion. Keyboard shortcuts are exposed in each control's accessible help; Escape closes the active inspector.

AI World reads current session state. It does not create an independent conversation, alter model prompts, or replace transcript evidence. Turning the feature or master Debug setting off returns to Transcript.

## Other debug tools

- **Decision Card:** optional compact Narrator-generated operator summary in Arena mode.
- **Style Fit:** heuristic cues for visible voice-style adherence; not a formal model-quality score.
- **Voice drift enforcement:** strengthens future Arena voice reminders while enabled.
- **Experiment Lab:** advanced local evidence, fault, inspection, and QA tools.

Narrator-dependent debug generation is unavailable in Factory mode because Factory omits Arena guidance.

## Accessibility and motion

World cues repeat state in text/help rather than relying on glow or color. Use Overview or Transcript when motion, depth, or dense live animation makes inspection harder. System High Contrast and app theme settings remain authoritative.

PowerShell control is organized with developer settings but is independent of the visual Debug master switch. Hiding debug UI does not silently disable already configured local control-plane policy.
