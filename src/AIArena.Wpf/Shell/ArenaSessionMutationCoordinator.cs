using System.Windows;
using System.Windows.Controls;
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
    private readonly ProviderSettingsCoordinator providerSettings;
    private readonly AgentRosterCoordinator agentRoster;
    private readonly TextBox providerBaseUrlText;
    private readonly ComboBox providerModelText;
    private readonly TextBox providerTimeoutText;
    private readonly TextBox providerTemperatureText;
    private readonly TextBox providerMaxOutputText;
    private readonly TextBox contextTranscriptWindowText;
    private readonly TextBox contextPrivateWindowText;
    private readonly TextBox contextNotesWindowText;
    private readonly CheckBox useInternetCheckBox;
    private readonly ComboBox internetModePicker;
    private readonly ComboBox internetSourceScopePicker;
    private readonly TextBox internetMaxResultsText;
    private readonly CheckBox allowParticipantInternetCheckBox;
    private readonly CheckBox allowNarratorInternetCheckBox;
    private readonly CheckBox requireInternetApprovalCheckBox;
    private readonly TextBlock providerTestStatus;
    private readonly Button resetButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<ThemePalette> theme;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<bool, Task> refreshProviderReachabilityAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    public ArenaSessionMutationCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ProviderSettingsCoordinator providerSettings,
        AgentRosterCoordinator agentRoster,
        TextBox providerBaseUrlText,
        ComboBox providerModelText,
        TextBox providerTimeoutText,
        TextBox providerTemperatureText,
        TextBox providerMaxOutputText,
        TextBox contextTranscriptWindowText,
        TextBox contextPrivateWindowText,
        TextBox contextNotesWindowText,
        CheckBox useInternetCheckBox,
        ComboBox internetModePicker,
        ComboBox internetSourceScopePicker,
        TextBox internetMaxResultsText,
        CheckBox allowParticipantInternetCheckBox,
        CheckBox allowNarratorInternetCheckBox,
        CheckBox requireInternetApprovalCheckBox,
        TextBlock providerTestStatus,
        Button resetButton,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isRenderingSnapshot,
        Func<ThemePalette> theme,
        Func<string?, Task> loadSessionsAsync,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Func<bool, Task> refreshProviderReachabilityAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerSettings = providerSettings;
        this.agentRoster = agentRoster;
        this.providerBaseUrlText = providerBaseUrlText;
        this.providerModelText = providerModelText;
        this.providerTimeoutText = providerTimeoutText;
        this.providerTemperatureText = providerTemperatureText;
        this.providerMaxOutputText = providerMaxOutputText;
        this.contextTranscriptWindowText = contextTranscriptWindowText;
        this.contextPrivateWindowText = contextPrivateWindowText;
        this.contextNotesWindowText = contextNotesWindowText;
        this.useInternetCheckBox = useInternetCheckBox;
        this.internetModePicker = internetModePicker;
        this.internetSourceScopePicker = internetSourceScopePicker;
        this.internetMaxResultsText = internetMaxResultsText;
        this.allowParticipantInternetCheckBox = allowParticipantInternetCheckBox;
        this.allowNarratorInternetCheckBox = allowNarratorInternetCheckBox;
        this.requireInternetApprovalCheckBox = requireInternetApprovalCheckBox;
        this.providerTestStatus = providerTestStatus;
        this.resetButton = resetButton;
        this.activeSession = activeSession;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.theme = theme;
        this.loadSessionsAsync = loadSessionsAsync;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.refreshProviderReachabilityAsync = refreshProviderReachabilityAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public async Task ApplySettingsAsync()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var session = await EnsureWritableSessionAsync("No writable session is available for settings.");
        if (session is null)
        {
            return;
        }

        var baseUrl = providerBaseUrlText.Text.Trim();
        var model = providerModelText.Text.Trim();
        providerSettings.SaveRoleModelDrafts();
        var alphaModel = providerSettings.RoleModel("alpha");
        var betaModel = providerSettings.RoleModel("beta");
        var gammaModel = providerSettings.RoleModel("gamma");
        var deltaModel = providerSettings.RoleModel("delta");
        var narratorModel = providerSettings.RoleModel("narrator");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            providerTestStatus.Text = "Base URL and model are required.";
            return;
        }

        if (!TryParseInt(providerTimeoutText, "Timeout must be a whole number.", out var timeout)
            || !TryParseDouble(providerTemperatureText, "Temperature must be a number.", out var temperature)
            || !TryParseInt(providerMaxOutputText, "Max output must be a whole number.", out var maxOutput)
            || !TryParseInt(contextTranscriptWindowText, "Transcript window must be a whole number.", out var transcriptWindow)
            || !TryParseInt(contextPrivateWindowText, "Private notes window must be a whole number.", out var privateWindow)
            || !TryParseInt(contextNotesWindowText, "Pinned notes window must be a whole number.", out var notesWindow)
            || !TryParseInt(internetMaxResultsText, "Internet results must be a whole number.", out var internetMaxResults))
        {
            return;
        }

        timeout = ClampTimeout(timeout);
        temperature = ClampTemperature(temperature);
        maxOutput = ClampMaxOutput(maxOutput);
        transcriptWindow = ClampContextWindow(transcriptWindow);
        privateWindow = ClampOptionalContextWindow(privateWindow);
        notesWindow = ClampOptionalContextWindow(notesWindow);
        internetMaxResults = ClampInternetMaxResults(internetMaxResults);
        var activeParticipants = agentRoster.ParseActiveParticipants();
        var internetMode = EffectiveInternetMode(
            useInternetCheckBox.IsChecked == true,
            ShellUiHelpers.SelectedComboTag(internetModePicker, "manual"));
        var useInternet = !internetMode.Equals("off", StringComparison.OrdinalIgnoreCase);
        var internetSourceScope = ShellUiHelpers.SelectedComboTag(internetSourceScopePicker, "trusted");

        await runArenaBusyAsync("Applying settings...", null, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id) ?? SessionStore.CreateDefaultSnapshot();
            CoreModelProviderConfig? existingShared = snapshot.Configs.TryGetValue("shared", out var existingConfig)
                ? existingConfig
                : null;

            snapshot.Configs["shared"] = new CoreModelProviderConfig
            {
                BaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl),
                Model = model,
                Timeout = timeout,
                Temperature = temperature,
                MaxOutputTokens = maxOutput,
                LastError = existingShared?.LastError ?? "",
                LastLatencyMs = existingShared?.LastLatencyMs ?? 0,
                LastTestOk = existingShared?.LastTestOk ?? false
            };
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "alpha", alphaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "beta", betaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "gamma", gammaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "delta", deltaModel, snapshot.Configs["shared"]);
            ProviderSettingsCoordinator.SaveRoleModelConfig(snapshot.Configs, "narrator", narratorModel, snapshot.Configs["shared"]);
            snapshot.Engine.TranscriptWindow = transcriptWindow;
            snapshot.Engine.PrivateWindow = privateWindow;
            snapshot.Engine.NotesWindow = notesWindow;
            snapshot.Engine.ModelRss.UseInternet = useInternet;
            snapshot.Engine.ModelRss.Mode = internetMode;
            snapshot.Engine.ModelRss.SourceScope = internetSourceScope;
            snapshot.Engine.ModelRss.MaxResults = internetMaxResults;
            snapshot.Engine.ModelRss.AllowParticipantRequests = allowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowModelRss = allowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowNarratorRequests = allowNarratorInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.RequireApproval = requireInternetApprovalCheckBox.IsChecked == true;
            AgentRosterService.EnsureParticipantCount(snapshot, Math.Max(activeParticipants, AgentRosterService.MinParticipants));
            for (var index = 0; index < snapshot.Engine.Agents.Count; index++)
            {
                snapshot.Engine.Agents[index].Active = AgentRosterService.IsParticipantId(snapshot.Engine.Agents[index].Id)
                    && index < activeParticipants;
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_settings_applied", new
            {
                snapshot.Configs["shared"].BaseUrl,
                snapshot.Configs["shared"].Model,
                snapshot.Configs["shared"].Timeout,
                snapshot.Configs["shared"].Temperature,
                snapshot.Configs["shared"].MaxOutputTokens,
                AlphaModel = alphaModel,
                BetaModel = betaModel,
                GammaModel = gammaModel,
                DeltaModel = deltaModel,
                NarratorModel = narratorModel,
                snapshot.Engine.ModelRss.UseInternet,
                snapshot.Engine.ModelRss.Mode,
                snapshot.Engine.ModelRss.SourceScope,
                snapshot.Engine.ModelRss.MaxResults,
                snapshot.Engine.ModelRss.AllowParticipantRequests,
                snapshot.Engine.ModelRss.AllowNarratorRequests,
                snapshot.Engine.ModelRss.RequireApproval,
                ActiveParticipants = activeParticipants
            });
            providerTestStatus.Text = "Settings applied.";
            await refreshActiveSessionAsync("Settings applied.");
            _ = refreshProviderReachabilityAsync(true);
        }, false);
    }

    public async Task ResetArenaAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Reset Arena",
            "Reset the current arena transcript and live state?\n\nScenario, cast, locks, provider settings, and checkpoints are preserved.",
            "Reset",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
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

    internal static string EffectiveInternetMode(bool useInternet, string selectedMode)
    {
        if (!useInternet)
        {
            return "off";
        }

        return selectedMode.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : selectedMode;
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

    internal static int ClampContextWindow(int value)
    {
        return Math.Clamp(value, 1, 60);
    }

    internal static int ClampOptionalContextWindow(int value)
    {
        return Math.Clamp(value, 0, 60);
    }

    internal static int ClampInternetMaxResults(int value)
    {
        return Math.Clamp(value, 1, 10);
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
