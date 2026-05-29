# AI Arena User Guide

AI Arena is a native Windows app where multiple local or OpenAI-compatible language models take part in a structured conversation. The models can debate, collaborate, test ideas, or freely explore a topic while you guide the arena as the operator.

The app is designed for local experimentation with model behaviour. You can choose the cast, run one turn at a time, let the arena continue automatically, inject your own messages, ask for narration, curate outside context, and save checkpoints as the match evolves.

## Main Ideas

- The arena has up to three active participants: Alpha, Beta, and Gamma.
- Each participant has its own role/persona, but participants are not shown each other's hidden roles. They only see public transcript text.
- The Narrator is separate from the participants. It can summarize or steer the public transcript without joining as Alpha, Beta, or Gamma.
- The Operator is you. Operator turns can be added at any time, even while another operation is running.
- Sessions are saved locally under your Windows user profile.

## Top Bar

- Session: shows the currently loaded session.
- Match: shows the current match style or mode.
- Provider: shows whether the configured model provider is online or offline.
- Model: shows the current shared/default model.
- Turns: shows the number of transcript turns in the current session.
- Theme: changes the app colour palette.
- Settings icon: opens App Settings.

## Transcript

The transcript shows the newest message at the top.

Each card has:

- A coloured accent showing who produced the turn.
- A header with turn number, speaker, model, generated token count, context size, and useful status flags.
- A body with the public message.
- Buttons for Copy, Pin, Retry, and Delete.

Generated and context stats mean:

- Generated: how long the model took and how many output tokens it produced.
- Context: how many prompt/input tokens were sent to the model.
- Error: shown only when something failed.
- Pinned: shown when a card is pinned.

Model reasoning appears in a collapsible section when the provider returns reasoning content.

## Arena Controls

### Auto Chat

Runs repeated arena turns automatically using the active participant order. Auto Chat stops when you press Stop, when a provider error occurs, or when an internet approval request is waiting for your decision.

### 1 Turn

Runs exactly one turn for the next active participant.

### Stop

Cancels Auto Chat or another stoppable arena operation.

### Reset

Clears the current transcript and live turn state. Scenario, cast, locks, provider settings, and checkpoints are preserved.

## Assist

### Narrate Now

Asks the Narrator to add a concise public note about the current exchange.

### Curate News

Uses the configured internet/news settings to find one relevant outside item and inject it into the transcript when a suitable source is found.

## Quick Match Generation

### Random Seed

Generates a deterministic scenario and cast from a seed. Locked fields are preserved.

### AI Choice

Asks the configured model to create a scenario and cast. Locked fields are preserved, and missing cast members are filled automatically.

## Operator Turn

Use Operator Turn to add your own public message to the transcript. This does not advance the participant order. You can inject operator messages at any time.

## Sessions

- The session dropdown switches between saved sessions.
- Create / Switch creates a new session or switches to an existing one with the typed name.
- Delete Session removes the selected session after confirmation.

## Custom Match

Custom Match shows the current topic, global instruction, and cast.

Locks prevent Random Seed or AI Choice from changing selected parts:

- Topic lock preserves the topic.
- Global lock preserves the global instruction.
- Agent locks preserve individual cast members.

Checkpoints let you save, restore, or delete named versions of the current session state.

## News View

The News view lists transcript-backed news and internet tool cards. It is useful for inspecting what outside context was fetched, why it was selected, and which sources were used.

## App Settings

Settings are grouped into collapsible sections.

### Auto Chat

- Cadence: controls how quickly Auto Chat runs repeated turns.

### Model Provider

- Active participants: chooses whether 1, 2, or 3 agents participate in the arena.
- Provider base URL: OpenAI-compatible endpoint, such as `http://127.0.0.1:1234/v1`.
- Default model: shared model used globally unless a role has its own model.
- Role model: optional model override for Alpha, Beta, Gamma, or Narrator.
- Clear: removes the selected role override so it falls back to the default model.
- Timeout: maximum time to wait for the provider.
- Temperature: controls model randomness.
- Max output: maximum generated tokens requested from the provider.
- Test Provider: sends a small test completion to verify the provider.

Advertised provider models refresh every 5 seconds while settings are open. You can also type a model name manually.

### News / Internet

- Use internet: enables internet tools.
- Mode:
  - Off: models cannot request internet.
  - Manual: only operator/manual actions use internet.
  - Model requested: models can request internet when allowed.
  - Auto: reserved for more automated behaviour.
- Source scope:
  - Trusted sources only: limits tools to configured/trusted sources.
  - Open web allowed: allows direct URL fetches and broader web access.
- Allow participant requests: Alpha/Beta/Gamma may ask for internet.
- Allow narrator requests: Narrator may ask for internet.
- Require approval: internet requests pause as approval cards until you approve or reject them.
- Max results: limits internet result count.

When approval is required, the app shows an Internet Approval card with Approve Once, Reject, Copy URL, and Delete actions.

### Theme

Changes the app colour palette. The selected theme is saved locally for the WPF app.

### Help / About

Shows a short application description and credits.

## Provider Troubleshooting

If LM Studio or another provider is closed, the app may show:

`Provider unreachable at http://127.0.0.1:1234/v1. Start LM Studio server or check the base URL.`

Check:

- LM Studio server is running.
- The model is loaded.
- The base URL and port are correct.
- The endpoint exposes an OpenAI-compatible `/v1` API.

If the model times out, reduce context size, use a smaller model, lower max output, or increase timeout.

## Practical Tips

- Use 1 Turn when testing a new setup.
- Use Auto Chat after the cast and topic are behaving well.
- Pin important turns before experimenting.
- Save checkpoints before major match changes.
- Keep internet approval on when testing model-requested browsing.
- If a model starts repeating or acting as another participant, reset the match or retry the turn after adjusting the topic or cast.
