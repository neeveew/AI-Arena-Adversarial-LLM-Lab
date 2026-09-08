using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
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
    internal sealed record ArenaResetCompletion(
        SnapshotSafetyCheckpointReceipt SafetyCheckpoint,
        string Outcome,
        bool EventRecorded);

    internal const int MinimumTimeoutSeconds = 1;
    internal const int MaximumTimeoutSeconds = 3600;
    internal const double MinimumTemperature = 0;
    internal const double MaximumTemperature = 2;
    internal const int MinimumMaxOutputTokens = 1;
    internal const int MaximumMaxOutputTokens = 32768;
    internal const int MinimumTranscriptWindow = 1;
    internal const int MinimumOptionalContextWindow = 0;
    internal const int MaximumContextWindow = 60;

    internal sealed record AdvancedSettingsValues(
        int TimeoutSeconds,
        double Temperature,
        int MaxOutputTokens,
        int TranscriptWindow,
        int PrivateWindow,
        int NotesWindow);

    internal sealed record AdvancedSettingsFieldError(
        string FieldKey,
        string AccessibleName,
        string Message);

    internal sealed record AdvancedSettingsValidationResult(
        AdvancedSettingsValues? Values,
        IReadOnlyList<AdvancedSettingsFieldError> Errors)
    {
        public bool IsValid => Values is not null && Errors.Count == 0;
    }

    private sealed record AdvancedSettingsField(
        string Key,
        string AccessibleName,
        string HelpText,
        TextBox TextBox);

    private static readonly DependencyProperty AdvancedSettingsValidationProbeProperty =
        DependencyProperty.RegisterAttached(
            "AdvancedSettingsValidationProbe",
            typeof(object),
            typeof(ArenaSessionMutationCoordinator),
            new PropertyMetadata(null));

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
    private readonly IReadOnlyList<AdvancedSettingsField> advancedSettingsFields;
    private readonly Dictionary<TextBox, BindingExpressionBase> advancedSettingsValidationBindings = [];
    private bool ownsAdvancedSettingsValidationStatus;

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
        advancedSettingsFields =
        [
            new(
                "timeout",
                "Model-call timeout",
                "Whole seconds allowed for one model call. Required range: 1 to 3600.",
                providerTimeoutText),
            new(
                "temperature",
                "Model temperature",
                "Sampling temperature. Required range: 0 to 2. Use a period as the decimal separator.",
                providerTemperatureText),
            new(
                "max-output",
                "Response token limit",
                "Whole-token response budget. Required range: 1 to 32768.",
                providerMaxOutputText),
            new(
                "transcript-window",
                "Transcript context window",
                "Recent public transcript turns available to context builders. Required range: 1 to 60.",
                contextTranscriptWindowText),
            new(
                "private-window",
                "Private notes context window",
                "Recent private-note turns retained per agent. Required range: 0 to 60.",
                contextPrivateWindowText),
            new(
                "notes-window",
                "Pinned notes context window",
                "Pinned or saved note turns available to context builders. Required range: 0 to 60.",
                contextNotesWindowText)
        ];
        InitializeAdvancedSettingsValidation();
    }

    public async Task<bool> ApplySettingsAsync()
    {
        if (isRenderingSnapshot())
        {
            return false;
        }

        var validation = ValidateAndPresentAdvancedSettings();
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            SetAdvancedSettingsValidationStatus(
                $"Advanced settings were not applied. {firstError.AccessibleName}: {firstError.Message}");
            FocusInvalidAdvancedSettingsField(firstError.FieldKey);
            return false;
        }

        var session = await EnsureWritableSessionAsync("No writable session is available for settings.");
        if (session is null)
        {
            return false;
        }

        var settings = validation.Values!;
        var timeout = settings.TimeoutSeconds;
        var temperature = settings.Temperature;
        var maxOutput = settings.MaxOutputTokens;
        var transcriptWindow = settings.TranscriptWindow;
        var privateWindow = settings.PrivateWindow;
        var notesWindow = settings.NotesWindow;

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
            ownsAdvancedSettingsValidationStatus = false;
            await refreshActiveSessionAsync("Session settings applied.");
        }, false);
        return saved;
    }

    internal static AdvancedSettingsValidationResult ValidateAdvancedSettings(
        string? timeoutText,
        string? temperatureText,
        string? maxOutputText,
        string? transcriptWindowText,
        string? privateWindowText,
        string? notesWindowText)
    {
        var errors = new List<AdvancedSettingsFieldError>();
        var timeout = ParseBoundedWholeNumber(
            timeoutText,
            MinimumTimeoutSeconds,
            MaximumTimeoutSeconds,
            "timeout",
            "Model-call timeout",
            $"Enter a whole number from {MinimumTimeoutSeconds} to {MaximumTimeoutSeconds} seconds.",
            errors);
        var temperature = ParseBoundedTemperature(temperatureText, errors);
        var maxOutput = ParseBoundedWholeNumber(
            maxOutputText,
            MinimumMaxOutputTokens,
            MaximumMaxOutputTokens,
            "max-output",
            "Response token limit",
            $"Enter a whole number from {MinimumMaxOutputTokens} to {MaximumMaxOutputTokens} tokens.",
            errors);
        var transcriptWindow = ParseBoundedWholeNumber(
            transcriptWindowText,
            MinimumTranscriptWindow,
            MaximumContextWindow,
            "transcript-window",
            "Transcript context window",
            $"Enter a whole number from {MinimumTranscriptWindow} to {MaximumContextWindow} turns.",
            errors);
        var privateWindow = ParseBoundedWholeNumber(
            privateWindowText,
            MinimumOptionalContextWindow,
            MaximumContextWindow,
            "private-window",
            "Private notes context window",
            $"Enter a whole number from {MinimumOptionalContextWindow} to {MaximumContextWindow} turns.",
            errors);
        var notesWindow = ParseBoundedWholeNumber(
            notesWindowText,
            MinimumOptionalContextWindow,
            MaximumContextWindow,
            "notes-window",
            "Pinned notes context window",
            $"Enter a whole number from {MinimumOptionalContextWindow} to {MaximumContextWindow} turns.",
            errors);

        return new AdvancedSettingsValidationResult(
            errors.Count == 0
                ? new AdvancedSettingsValues(
                    timeout,
                    temperature,
                    maxOutput,
                    transcriptWindow,
                    privateWindow,
                    notesWindow)
                : null,
            errors);
    }

    private static int ParseBoundedWholeNumber(
        string? text,
        int minimum,
        int maximum,
        string fieldKey,
        string accessibleName,
        string error,
        ICollection<AdvancedSettingsFieldError> errors)
    {
        if (int.TryParse(
                (text ?? "").Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value)
            && value >= minimum
            && value <= maximum)
        {
            return value;
        }

        errors.Add(new AdvancedSettingsFieldError(fieldKey, accessibleName, error));
        return 0;
    }

    private static double ParseBoundedTemperature(
        string? text,
        ICollection<AdvancedSettingsFieldError> errors)
    {
        if (double.TryParse(
                (text ?? "").Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && double.IsFinite(value)
            && value >= MinimumTemperature
            && value <= MaximumTemperature)
        {
            return value;
        }

        errors.Add(new AdvancedSettingsFieldError(
            "temperature",
            "Model temperature",
            $"Enter a finite number from {MinimumTemperature.ToString(CultureInfo.InvariantCulture)} to {MaximumTemperature.ToString(CultureInfo.InvariantCulture)}, using a period as the decimal separator."));
        return 0;
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
        var explicitModel = existingRole is not null
            && !string.IsNullOrWhiteSpace(existingRole.Model)
            && (existingRole.ExplicitModelAssignment
                || !existingRole.Model.Trim().Equals(previousShared.Model.Trim(), StringComparison.Ordinal))
            ? existingRole.Model
            : "";
        if (existingRole is not null && explicitModel.Length > 0
            && (!ProviderServerInventory.SameEndpoint(existingRole.BaseUrl, previousShared.BaseUrl)
                || !ProviderServerInventory.SameEndpoint(previousShared.BaseUrl, updatedShared.BaseUrl)))
        {
            // An explicit model stays attached to its server when unrelated
            // session settings or the shared default connection change.
            return;
        }
        ProviderConfigurationControlService.SaveRoleModelConfig(
            configs,
            role,
            explicitModel,
            updatedShared,
            temperatureOverride,
            maxOutputOverride,
            explicitAssignment: explicitModel.Length > 0);
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
            var completion = await ResetArenaWithSafetyCheckpointAndReportAsync(
                sessionStore,
                eventLogStore,
                session.Id,
                refreshActiveSessionAsync);
            if (completion is null)
            {
                setArenaRunStatus($"No snapshot found for session {session.Id}.");
            }
        }, false);
    }

    internal static async Task<ArenaResetCompletion?> ResetArenaWithSafetyCheckpointAndReportAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        string sessionId,
        Func<string, Task> refreshActiveSessionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventLogStore);
        ArgumentNullException.ThrowIfNull(refreshActiveSessionAsync);
        var safety = await ResetArenaWithSafetyCheckpointAsync(
            sessionStore,
            sessionId,
            cancellationToken);
        if (safety is null)
        {
            return null;
        }

        // The destructive replacement has committed. Event evidence is now
        // secondary: use non-cancellable completion semantics and always refresh
        // the live projection even when the evidence file cannot be appended.
        var evidence = await AppPostCommitEvidence.TryAppendAsync(
            eventLogStore,
            sessionId,
            "native_arena_reset",
            new
            {
                session = sessionId,
                safety_checkpoint_id = safety.Checkpoint.Id,
                safety_checkpoint_name = safety.Checkpoint.Name,
                protected_revision = safety.ProtectedRevision,
                replacement_revision = safety.ReplacementRevision
            },
            AppErrorContext.Arena);
        var outcome = evidence.AppendTo(
            $"Arena reset. Safety checkpoint: {safety.Checkpoint.Name}.");
        var refreshWarning = await AppPostCommitEvidence.TryCompleteAsync(
            () => refreshActiveSessionAsync(outcome),
            "the reset arena view could not be refreshed",
            AppErrorContext.Arena);
        outcome = AppPostCommitEvidence.AppendWarning(outcome, refreshWarning);
        return new ArenaResetCompletion(safety, outcome, evidence.Recorded);
    }

    internal static Task<SnapshotSafetyCheckpointReceipt?> ResetArenaWithSafetyCheckpointAsync(
        SessionStore sessionStore,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        return sessionStore.MutateSnapshotWithSafetyCheckpointAsync(
            sessionId,
            SnapshotSafetyCheckpointOperation.ArenaReset,
            subject: null,
            ResetArenaSnapshot,
            cancellationToken);
    }

    internal static ArenaSnapshot ResetArenaSnapshot(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.Narration.Clear();
        snapshot.Engine.TurnCount = 0;
        snapshot.Engine.TurnIndex = 0;
        snapshot.Engine.MatchEnded = false;
        snapshot.Engine.MatchEndedAt = null;
        snapshot.Engine.MatchEndReason = "";
        snapshot.Engine.LastError = "";
        snapshot.PendingModelConfigurationApplies.Clear();
        snapshot.Engine.Narrator.Status = "idle";
        snapshot.Engine.Narrator.LastError = "";
        foreach (var agent in snapshot.Engine.Agents)
        {
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
        }

        return snapshot;
    }

    internal static int ClampTimeout(int value)
    {
        return Math.Clamp(value, MinimumTimeoutSeconds, MaximumTimeoutSeconds);
    }

    internal static double ClampTemperature(double value)
    {
        return Math.Clamp(value, MinimumTemperature, MaximumTemperature);
    }

    internal static int ClampMaxOutput(int value)
    {
        return Math.Clamp(value, MinimumMaxOutputTokens, MaximumMaxOutputTokens);
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
        return Math.Clamp(value, MinimumTranscriptWindow, MaximumContextWindow);
    }

    internal static int ClampOptionalContextWindow(int value)
    {
        return Math.Clamp(value, MinimumOptionalContextWindow, MaximumContextWindow);
    }

    private void InitializeAdvancedSettingsValidation()
    {
        foreach (var field in advancedSettingsFields)
        {
            AutomationProperties.SetName(field.TextBox, field.AccessibleName);
            AutomationProperties.SetHelpText(field.TextBox, field.HelpText);
            AutomationProperties.SetItemStatus(field.TextBox, "");
            AutomationProperties.SetIsRequiredForForm(field.TextBox, true);
            field.TextBox.ToolTip = field.HelpText;
            BindingOperations.SetBinding(
                field.TextBox,
                AdvancedSettingsValidationProbeProperty,
                new Binding
                {
                    Source = field.TextBox,
                    Mode = BindingMode.OneWay
                });
            var validationBinding = BindingOperations.GetBindingExpressionBase(
                    field.TextBox,
                    AdvancedSettingsValidationProbeProperty)
                ?? throw new InvalidOperationException(
                    $"Could not initialize validation for {field.AccessibleName}.");
            advancedSettingsValidationBindings[field.TextBox] = validationBinding;
            field.TextBox.TextChanged += AdvancedSettingsField_TextChanged;
        }
    }

    private void AdvancedSettingsField_TextChanged(object sender, TextChangedEventArgs e)
    {
        var validation = ValidateAndPresentAdvancedSettings();
        if (isRenderingSnapshot())
        {
            return;
        }

        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            SetAdvancedSettingsValidationStatus(
                $"Fix advanced settings before Apply. {firstError.AccessibleName}: {firstError.Message}");
        }
        else if (ownsAdvancedSettingsValidationStatus)
        {
            providerTestStatus.Text = "Advanced settings values are valid and ready to apply.";
            ownsAdvancedSettingsValidationStatus = false;
        }
    }

    private AdvancedSettingsValidationResult ValidateAndPresentAdvancedSettings()
    {
        var validation = ValidateAdvancedSettings(
            providerTimeoutText.Text,
            providerTemperatureText.Text,
            providerMaxOutputText.Text,
            contextTranscriptWindowText.Text,
            contextPrivateWindowText.Text,
            contextNotesWindowText.Text);
        var errors = validation.Errors.ToDictionary(error => error.FieldKey, StringComparer.Ordinal);
        foreach (var field in advancedSettingsFields)
        {
            ApplyAdvancedSettingsFieldValidation(
                field,
                errors.TryGetValue(field.Key, out var error) ? error : null);
        }

        return validation;
    }

    private void ApplyAdvancedSettingsFieldValidation(
        AdvancedSettingsField field,
        AdvancedSettingsFieldError? error)
    {
        var validationBinding = advancedSettingsValidationBindings[field.TextBox];
        Validation.ClearInvalid(validationBinding);
        if (error is null)
        {
            AutomationProperties.SetHelpText(field.TextBox, field.HelpText);
            AutomationProperties.SetItemStatus(field.TextBox, "");
            field.TextBox.ToolTip = field.HelpText;
            return;
        }

        Validation.MarkInvalid(
            validationBinding,
            new ValidationError(
                new ExceptionValidationRule(),
                validationBinding,
                error.Message,
                null));
        var errorHelp = $"{field.HelpText} Error: {error.Message}";
        AutomationProperties.SetHelpText(field.TextBox, errorHelp);
        AutomationProperties.SetItemStatus(field.TextBox, $"Invalid: {error.Message}");
        field.TextBox.ToolTip = errorHelp;
    }

    private void FocusInvalidAdvancedSettingsField(string fieldKey)
    {
        var field = advancedSettingsFields.First(item => item.Key.Equals(fieldKey, StringComparison.Ordinal));
        field.TextBox.BringIntoView();
        field.TextBox.Focus();
        field.TextBox.SelectAll();
    }

    private void SetAdvancedSettingsValidationStatus(string value)
    {
        providerTestStatus.Text = value;
        ownsAdvancedSettingsValidationStatus = true;
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
}
