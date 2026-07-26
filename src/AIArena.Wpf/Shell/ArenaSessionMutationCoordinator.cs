using System.Windows;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreModelProviderConfig = AIArena.Core.Models.ModelProviderConfig;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class ArenaSessionMutationCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly TextBox providerTimeoutText;
    private readonly TextBox providerTemperatureText;
    private readonly TextBox providerMaxOutputText;
    private readonly TextBox contextTranscriptWindowText;
    private readonly TextBox contextPrivateWindowText;
    private readonly TextBox contextNotesWindowText;
    private readonly TextBlock providerTestStatus;
    private readonly Button resetButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<ThemePalette> theme;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    public ArenaSessionMutationCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TextBox providerTimeoutText,
        TextBox providerTemperatureText,
        TextBox providerMaxOutputText,
        TextBox contextTranscriptWindowText,
        TextBox contextPrivateWindowText,
        TextBox contextNotesWindowText,
        TextBlock providerTestStatus,
        Button resetButton,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isRenderingSnapshot,
        Func<ThemePalette> theme,
        Func<string?, Task> loadSessionsAsync,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerTimeoutText = providerTimeoutText;
        this.providerTemperatureText = providerTemperatureText;
        this.providerMaxOutputText = providerMaxOutputText;
        this.contextTranscriptWindowText = contextTranscriptWindowText;
        this.contextPrivateWindowText = contextPrivateWindowText;
        this.contextNotesWindowText = contextNotesWindowText;
        this.providerTestStatus = providerTestStatus;
        this.resetButton = resetButton;
        this.activeSession = activeSession;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.theme = theme;
        this.loadSessionsAsync = loadSessionsAsync;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public async Task<bool> ApplySettingsAsync()
    {
        if (isRenderingSnapshot())
        {
            return false;
        }

        var session = await EnsureWritableSessionAsync("No writable session is available for settings.");
        if (session is null)
        {
            return false;
        }

        if (!TryParseInt(providerTimeoutText, "Timeout must be a whole number.", out var timeout)
            || !TryParseDouble(providerTemperatureText, "Temperature must be a number.", out var temperature)
            || !TryParseInt(providerMaxOutputText, "Response limit must be a whole number.", out var maxOutput)
            || !TryParseInt(contextTranscriptWindowText, "Transcript window must be a whole number.", out var transcriptWindow)
            || !TryParseInt(contextPrivateWindowText, "Private notes window must be a whole number.", out var privateWindow)
            || !TryParseInt(contextNotesWindowText, "Pinned notes window must be a whole number.", out var notesWindow))
        {
            return false;
        }

        var requestedTimeout = timeout;
        var requestedTemperature = temperature;
        var requestedMaxOutput = maxOutput;
        var requestedTranscriptWindow = transcriptWindow;
        var requestedPrivateWindow = privateWindow;
        var requestedNotesWindow = notesWindow;
        timeout = ClampTimeout(timeout);
        temperature = ClampTemperature(temperature);
        maxOutput = ClampMaxOutput(maxOutput);
        transcriptWindow = ClampContextWindow(transcriptWindow);
        privateWindow = ClampOptionalContextWindow(privateWindow);
        notesWindow = ClampOptionalContextWindow(notesWindow);
        var clampNotes = new List<string>();
        if (timeout != requestedTimeout)
        {
            providerTimeoutText.Text = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"timeout adjusted to {timeout}s (allowed 1-3600)");
        }

        if (Math.Abs(temperature - requestedTemperature) > 0.0001)
        {
            providerTemperatureText.Text = temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"temperature adjusted to {temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} (allowed 0-2)");
        }

        if (maxOutput != requestedMaxOutput)
        {
            providerMaxOutputText.Text = maxOutput.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"max output adjusted to {maxOutput} tokens (allowed 1-32768)");
        }

        if (transcriptWindow != requestedTranscriptWindow)
        {
            contextTranscriptWindowText.Text = transcriptWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"transcript window adjusted to {transcriptWindow} turns (allowed 1-60)");
        }

        if (privateWindow != requestedPrivateWindow)
        {
            contextPrivateWindowText.Text = privateWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"private notes window adjusted to {privateWindow} turns (allowed 0-60)");
        }

        if (notesWindow != requestedNotesWindow)
        {
            contextNotesWindowText.Text = notesWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clampNotes.Add($"pinned notes window adjusted to {notesWindow} turns (allowed 0-60)");
        }

        if (clampNotes.Count > 0)
        {
            providerTestStatus.Text = $"Out-of-range values were corrected: {string.Join("; ", clampNotes)}.";
        }

        var saved = false;
        await runArenaBusyAsync("Applying session settings...", null, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id) ?? SessionStore.CreateDefaultSnapshot();
            CoreModelProviderConfig? existingShared = snapshot.Configs.TryGetValue("shared", out var existingConfig)
                ? existingConfig
                : null;
            var persistedShared = existingShared ?? new CoreModelProviderConfig();
            var updatedShared = AppliedSharedProviderConfig(
                existingShared,
                persistedShared.BaseUrl,
                persistedShared.ApiMode,
                persistedShared.ApiToken,
                persistedShared.Model,
                timeout,
                temperature,
                maxOutput,
                persistedShared.ContextLength,
                persistedShared.Reasoning,
                persistedShared.NativeStatefulChat,
                persistedShared.NativeIdleTtlSeconds);
            snapshot.Configs["shared"] = updatedShared;
            foreach (var role in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
            {
                RefreshRoleInheritedGenerationDefaults(snapshot.Configs, role, persistedShared, updatedShared);
            }

            snapshot.Engine.TranscriptWindow = transcriptWindow;
            snapshot.Engine.PrivateWindow = privateWindow;
            snapshot.Engine.NotesWindow = notesWindow;

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            saved = true;
            await eventLogStore.AppendAsync(session.Id, "session_call_context_settings_applied", new
            {
                updatedShared.Timeout,
                updatedShared.Temperature,
                updatedShared.MaxOutputTokens,
                TranscriptWindow = transcriptWindow,
                PrivateWindow = privateWindow,
                NotesWindow = notesWindow
            });
            providerTestStatus.Text = "Advanced model-call and context-window settings applied.";
            await refreshActiveSessionAsync("Session settings applied.");
        }, false);
        return saved;
    }

    internal static void RefreshRoleInheritedGenerationDefaults(
        IDictionary<string, CoreModelProviderConfig> configs,
        string role,
        CoreModelProviderConfig previousShared,
        CoreModelProviderConfig updatedShared)
    {
        configs.TryGetValue(role, out var existingRole);
        double? temperatureOverride = existingRole is not null
            && Math.Abs(existingRole.Temperature - previousShared.Temperature) > 0.0001
                ? existingRole.Temperature
                : null;
        int? maxOutputOverride = existingRole is not null
            && existingRole.MaxOutputTokens != previousShared.MaxOutputTokens
                ? existingRole.MaxOutputTokens
                : null;
        ProviderSettingsCoordinator.SaveRoleModelConfig(
            configs,
            role,
            existingRole?.Model ?? "",
            updatedShared,
            temperatureOverride,
            maxOutputOverride);
    }

    public async Task ResetArenaAsync()
    {
        await ResetArenaAsync(requireConfirmation: true);
    }

    public async Task ControlResetArenaAsync()
    {
        await ResetArenaAsync(requireConfirmation: false);
    }

    private async Task ResetArenaAsync(bool requireConfirmation)
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        if (requireConfirmation && !ConfirmDialog.Show(
                owner,
                theme(),
                "Reset Arena",
                "Reset the current arena transcript and live state?\n\nScenario, cast, locks, provider settings, and checkpoints are preserved.",
                "Reset",
                tone: ConfirmDialogTone.Danger))
        {
            setArenaRunStatus("Reset cancelled.");
            return;
        }

        await runArenaBusyAsync("Resetting arena...", resetButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                setArenaRunStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Narration.Clear();
            snapshot.Engine.TurnCount = 0;
            snapshot.Engine.TurnIndex = 0;
            snapshot.Engine.LastError = "";
            snapshot.Engine.Narrator.Status = "idle";
            snapshot.Engine.Narrator.LastError = "";
            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.Status = "waiting";
                agent.PrivateNotes.Clear();
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_arena_reset", new { session = session.Id });
            await refreshActiveSessionAsync("Arena reset.");
        }, false);
    }

    internal static int ClampTimeout(int value)
    {
        return Math.Clamp(value, 1, 3600);
    }

    internal static double ClampTemperature(double value)
    {
        return Math.Clamp(value, 0, 2);
    }

    internal static int ClampMaxOutput(int value)
    {
        return Math.Clamp(value, 1, 32768);
    }

    internal static int ClampProviderContextLength(int value)
    {
        return Math.Clamp(value, 0, 1048576);
    }

    internal static int ClampProviderNativeIdleTtlSeconds(int value)
    {
        return Math.Clamp(value, 0, 86400);
    }

    internal static CoreModelProviderConfig AppliedSharedProviderConfig(
        CoreModelProviderConfig? existingShared,
        string baseUrl,
        string apiMode,
        string apiToken,
        string model,
        int timeout,
        double temperature,
        int maxOutput,
        int contextLength,
        string reasoning,
        bool nativeStatefulChat,
        int nativeIdleTtlSeconds)
    {
        var normalizedBaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl);
        var normalizedApiMode = ModelProviderApiModes.Normalize(apiMode);
        var normalizedContextLength = ClampProviderContextLength(contextLength);
        var normalizedReasoning = ModelProviderReasoningModes.Normalize(reasoning);
        var normalizedNativeIdleTtlSeconds = ClampProviderNativeIdleTtlSeconds(nativeIdleTtlSeconds);
        var providerReadinessChanged = existingShared is null
            || ProviderSettingsCoordinator.ProviderReadinessChanged(
                existingShared,
                normalizedBaseUrl,
                normalizedApiMode,
                apiToken,
                model,
                normalizedContextLength,
                normalizedReasoning,
                nativeStatefulChat,
                normalizedNativeIdleTtlSeconds);

        return new CoreModelProviderConfig
        {
            BaseUrl = normalizedBaseUrl,
            ApiMode = normalizedApiMode,
            ApiToken = apiToken,
            Model = model,
            Timeout = ClampTimeout(timeout),
            Temperature = ClampTemperature(temperature),
            MaxOutputTokens = ClampMaxOutput(maxOutput),
            ContextLength = normalizedContextLength,
            Reasoning = normalizedReasoning,
            NativeStatefulChat = nativeStatefulChat,
            NativeIdleTtlSeconds = normalizedNativeIdleTtlSeconds,
            LastError = providerReadinessChanged ? "" : existingShared!.LastError,
            LastLatencyMs = providerReadinessChanged ? 0 : existingShared!.LastLatencyMs,
            LastTestOk = !providerReadinessChanged && existingShared!.LastTestOk,
            Extra = existingShared?.Extra
        };
    }

    internal static int ClampContextWindow(int value)
    {
        return Math.Clamp(value, 1, 60);
    }

    internal static int ClampOptionalContextWindow(int value)
    {
        return Math.Clamp(value, 0, 60);
    }

    private async Task<CoreSessionSummary?> EnsureWritableSessionAsync(string missingSessionStatus)
    {
        var session = activeSession();
        if (session is not null)
        {
            return session;
        }

        await sessionStore.EnsureDefaultSessionAsync();
        await loadSessionsAsync("default");
        session = activeSession();
        if (session is null)
        {
            providerTestStatus.Text = missingSessionStatus;
        }

        return session;
    }

    private bool TryParseInt(TextBox textBox, string error, out int value)
    {
        if (int.TryParse(textBox.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        providerTestStatus.Text = error;
        return false;
    }

    private bool TryParseOptionalInt(TextBox textBox, string error, out int value)
    {
        var text = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        providerTestStatus.Text = error;
        return false;
    }

    private bool TryParseDouble(TextBox textBox, string error, out double value)
    {
        if (double.TryParse(textBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        providerTestStatus.Text = error;
        return false;
    }
}
