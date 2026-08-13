This path proves the complete chain from provider connection to one successful AI Lab turn. Do not start with Auto Chat: one evidenced turn is easier to diagnose.

## 1. Start a compatible provider

Start LM Studio, Ollama, your own `llama-server`, or another OpenAI-compatible service. AI Arena connects to the service; it does not silently start or replace a user-owned provider process.

## 2. Configure and test the connection

1. Open **Settings → Provider connection**.
2. Choose the matching preset and select **Use preset**.
3. Open **Custom connection** only when the API mode, base address, or token differs.
4. Select **Test connection**.

Common local addresses are `http://127.0.0.1:1234/v1` for an LM Studio OpenAI-compatible server and `http://127.0.0.1:8080/v1` for a typical user-started llama.cpp server. The connection type must match the provider API.

## 3. Load a model when the provider supports residency

1. Open **Models** from the AI Lab top rail.
2. Expand **Available catalog** if it is folded.
3. Select a model and choose **Load model**.
4. Wait for provider-observed confirmation that the row moved to **Loaded Models**.

> [!WARNING] Loaded is not assigned. Residency only says the provider reports a live model process. It does not route Alpha, Narrator, Agent, or any other role.

## 4. Assign the model

In the selected model's **Assign to** area, turn on one of these routing choices:

- **Default** enables the shared fallback for Arena roles without explicit routes.
- An **Alpha–Theta** or **Narrator** switch creates an explicit role assignment.

The visible state tells the truth: **Default**, **Explicit**, **Uses default**, or **Unassigned**. Assignment saves immediately and never loads or unloads a model.

## 5. Prepare AI Lab

1. Open **Match Setup**.
2. Keep **Apply Match Setup to models** on for Arena mode.
3. Choose a preset or enter a topic and global instruction.
4. Resolve every blocking readiness item. Warnings may remain when you understand them.
5. Close Match Setup.

For Factory mode, turn Match Setup application off and send a non-empty **Public Operator** turn before running a participant.

## 6. Run one turn

Select **1 Turn** under Arena Controls. The button is ready only when all of these are true:

- the provider is reachable;
- the scheduled role resolves to a model;
- the match is not busy or ended;
- Arena mode has usable setup guidance, or Factory mode has its public Operator root.

Watch the transcript card and Status Center. A successful card shows the speaker and, when available, model, latency, tokens, and throughput. If the button is disabled, keyboard focus or hover exposes the actual missing prerequisite.

## 7. Continue deliberately

After one successful turn, use **Auto Chat** for repeated scheduled turns. Use **Pause** to stop. Save a checkpoint before a long or expensive run.

### Success check

You are ready when one non-error participant card exists in the transcript and Status Center reports the Arena operation as succeeded. Provider Online plus Model Loaded alone is not proof of routing readiness.

:::details Why the first run uses 1 Turn
One turn isolates the provider request, scheduled role, routing decision, prompt policy, and response receipt. Auto Chat repeats the same chain and can make the first missing prerequisite harder to identify. After the single-turn evidence is healthy, Auto Chat is the efficient next step.
:::
