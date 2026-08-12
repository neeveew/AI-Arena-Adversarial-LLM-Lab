using System.Globalization;
using System.IO;
using System.Net.Http;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Joins transient provider catalog evidence to durable per-target routing. The
/// control never owns credentials, provider calls, or persistence, and a late
/// catalog response cannot overwrite a newer session/provider presentation.
/// </summary>
internal sealed class ProviderModelsSurfaceCoordinator : IDisposable
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly ProviderModelAssignmentsControl control;
    private readonly SessionStore sessionStore;
    private readonly ProviderConfigurationControlService providerConfiguration;
    private readonly ModelProviderHealthService providerHealth;
    private readonly ModelPreloadService modelLifecycle;
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly ApplicationStatusCenter? statusCenter;
    private readonly ProviderModelCatalogProjectionService catalogProjection = new();
    private readonly LmStudioModelCatalogService lmStudioCatalog;
    private readonly OllamaModelCatalogService ollamaCatalog = new();
    private readonly LlamaCppRuntimeService llamaCppRuntime = new();
    private readonly Dictionary<string, ProviderModelAssignmentProjection> assignmentsByModel =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderModelCatalogItem> catalogItemsByModel =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, WpfProviderModelSettings> profileSettingsByModel =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource disposalCancellation = new();
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? lifecycleCancellation;
    private string lifecycleConnectionIdentity = "";
    private string currentConnectionIdentity = "";
    private string currentSessionId = "";
    private int lifecycleRunning;
    private bool disposed;

    public ProviderModelsSurfaceCoordinator(
        ProviderModelAssignmentsControl control,
        SessionStore sessionStore,
        ProviderConfigurationControlService providerConfiguration,
        ModelProviderHealthService providerHealth,
        ModelPreloadService modelLifecycle,
        SemaphoreSlim arenaOperationLock,
        Func<SessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        LmStudioModelCatalogService? lmStudioCatalog = null,
        ApplicationStatusCenter? statusCenter = null)
    {
        this.control = control;
        this.sessionStore = sessionStore;
        this.providerConfiguration = providerConfiguration;
        this.providerHealth = providerHealth;
        this.modelLifecycle = modelLifecycle;
        this.arenaOperationLock = arenaOperationLock;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.lmStudioCatalog = lmStudioCatalog ?? new LmStudioModelCatalogService();
        this.statusCenter = statusCenter;
    }

    public Task RefreshAsync(bool refreshCatalog, CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(refreshCatalog, heartbeat: false, cancellationToken);

    public IReadOnlyList<WpfProviderModelSettings> CaptureProfileModelSettings() =>
        profileSettingsByModel.Values
            .OrderBy(setting => setting.Model, StringComparer.OrdinalIgnoreCase)
            .Select(setting => new WpfProviderModelSettings
            {
                Model = setting.Model,
                ModelIdentity = setting.ModelIdentity,
                ConfiguredContextWindow = setting.ConfiguredContextWindow,
                HistoryPolicy = setting.HistoryPolicy,
                ResponseTone = setting.ResponseTone,
                CustomTone = setting.CustomTone,
                PendingApply = setting.PendingApply
            })
            .ToArray();

    public Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref lifecycleRunning) != 0)
        {
            return Task.CompletedTask;
        }

        return RefreshCoreAsync(refreshCatalog: true, heartbeat: true, cancellationToken);
    }

    private async Task RefreshCoreAsync(
        bool refreshCatalog,
        bool heartbeat,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            disposalCancellation.Token);
        var observedGeneration = catalogProjection.Current?.Generation ?? -1;
        var gateTaken = heartbeat
            ? await refreshGate.WaitAsync(0, gateCancellation.Token)
            : await WaitForRefreshGateAsync(gateCancellation.Token);
        if (!gateTaken)
        {
            return;
        }

        if (disposed)
        {
            refreshGate.Release();
            throw new ObjectDisposedException(nameof(ProviderModelsSurfaceCoordinator));
        }

        cancellationToken = gateCancellation.Token;

        CancellationTokenSource? refreshOwner = null;
        try
        {
        // If another catalog refresh completed while this manual request was
        // waiting, reuse its authoritative evidence instead of issuing a
        // duplicate provider request. A stale or rejected refresh does not
        // advance the published generation, so the waiting request still runs.
        if (!heartbeat
            && refreshCatalog
            && catalogProjection.Current is { } coalesced
            && coalesced.Generation != observedGeneration)
        {
            refreshCatalog = false;
        }

        var session = activeSession();
        if (session is null)
        {
            CancelLifecycleForConnectionChange();
            catalogProjection.Invalidate();
            assignmentsByModel.Clear();
            catalogItemsByModel.Clear();
            control.ApplyPresentation(EmptyPresentation("No active session is available."));
            return;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        if (snapshot is null)
        {
            CancelLifecycleForConnectionChange();
            catalogProjection.Invalidate();
            assignmentsByModel.Clear();
            catalogItemsByModel.Clear();
            control.ApplyPresentation(EmptyPresentation("The active session could not be loaded."));
            return;
        }

        if (heartbeat
            && !ModelProviderApiModes.IsLmStudioNative(SharedConfig(snapshot).ApiMode))
        {
            return;
        }

        var shared = SharedConfig(snapshot);
        var providerFingerprint = ProviderModelCatalogProjectionService.ProviderFingerprint(session.Id, snapshot);
        var connectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(session.Id, shared);
        if (Volatile.Read(ref lifecycleRunning) != 0
            && lifecycleConnectionIdentity.Length > 0
            && !lifecycleConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
        {
            lifecycleCancellation?.Cancel();
        }

        var current = catalogProjection.Current;
        if (!refreshCatalog
            && current is not null
            && current.SessionId.Equals(session.Id, StringComparison.Ordinal)
            && current.ProviderFingerprint.Equals(providerFingerprint, StringComparison.Ordinal))
        {
            ApplyPresentation(snapshot, current, isRefreshing: false);
            return;
        }

        var previousRefresh = refreshCancellation;
        refreshOwner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation = refreshOwner;
        previousRefresh?.Cancel();
        previousRefresh?.Dispose();
        var refreshToken = refreshOwner.Token;
        var lease = catalogProjection.BeginRefresh(session.Id, snapshot);
        var retained = current is not null
            && current.SessionId.Equals(lease.SessionId, StringComparison.Ordinal)
            && current.ProviderFingerprint.Equals(lease.ProviderFingerprint, StringComparison.Ordinal)
                ? current
                : ProviderModelCatalogProjectionService.FromCompatible(
                    lease,
                    [],
                    catalogAvailable: false,
                    error: "Loading provider models…",
                    configuredModel: shared.Model,
                    checkedAt: DateTimeOffset.Now);
        if (!heartbeat)
        {
            ApplyPresentation(snapshot, retained, isRefreshing: true);
        }

        ProviderModelCatalogSnapshot candidate;
        try
        {
            candidate = await LoadCatalogAsync(lease, shared, refreshToken);
        }
        catch (OperationCanceledException) when (refreshOwner.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            candidate = ProviderModelCatalogProjectionService.FromCompatible(
                lease,
                [],
                catalogAvailable: false,
                error: $"Model refresh failed safely ({exception.GetType().Name}).",
                configuredModel: shared.Model,
                checkedAt: DateTimeOffset.Now);
        }

        candidate = PreserveLastConfirmedResidency(
            current,
            candidate,
            preserveMissingRows: Volatile.Read(ref lifecycleRunning) != 0,
            control.SelectedModelId);

        refreshToken.ThrowIfCancellationRequested();
        var active = activeSession();
        var latest = active is null
            ? null
            : await sessionStore.LoadSnapshotAsync(active.Id, refreshToken);
        if (active is null
            || latest is null
            || !active.Id.Equals(lease.SessionId, StringComparison.Ordinal)
            || !ProviderModelCatalogProjectionService.ProviderFingerprint(active.Id, latest)
                .Equals(lease.ProviderFingerprint, StringComparison.Ordinal)
            || !catalogProjection.TryPublish(lease, candidate, out var published))
        {
            return;
        }

        if (!heartbeat
            || control.HasUnconfirmedLifecycleReceipt
            || !CatalogEvidenceEquivalent(current, published))
        {
            ApplyPresentation(latest, published, isRefreshing: false);
        }
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, refreshOwner))
            {
                refreshCancellation = null;
                refreshOwner?.Dispose();
            }

            refreshGate.Release();
        }
    }

    private async Task<bool> WaitForRefreshGateAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        return true;
    }

    public async Task SaveAssignmentAsync(
        ProviderModelAssignmentChangedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        var statusReceipt = BeginStatus(
            $"models.assignment.{change.ChangeId:N}",
            $"Saving {AssignmentTargetName(change.TargetId)} for {ModelLabel(change.ModelId)}…",
            change.ModelId);
        control.SetAssignmentState(change.ChangeId, ProviderAssignmentSaveState.Saving, "Saving…");
        if (!assignmentsByModel.TryGetValue(change.ModelId, out var assignment)
            || assignment.Targets.FirstOrDefault(target =>
                target.Id.Equals(change.TargetId, StringComparison.OrdinalIgnoreCase)) is not { } target)
        {
            control.SetAssignmentState(
                change.ChangeId,
                ProviderAssignmentSaveState.Failed,
                "The assignment changed; refresh and try again.");
            FailStatus(statusReceipt, "Assignment was not saved.", "The assignment changed; refresh and try again.");
            return;
        }

        ProviderModelAssignmentControlResult result;
        try
        {
            var equivalentModelIds = catalogItemsByModel.TryGetValue(change.ModelId, out var catalogItem)
                ? catalogItem.Aliases
                : [change.ModelId];
            result = await providerConfiguration.SetModelAssignmentAsync(
                new ProviderModelAssignmentRequest(
                    target.Id,
                    change.ModelId,
                    change.IsAssigned,
                    assignment.ProviderFingerprint,
                    target.AssignmentFingerprint,
                    equivalentModelIds),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            control.SetAssignmentState(
                change.ChangeId,
                ProviderAssignmentSaveState.Failed,
                "Assignment was cancelled.");
            CancelStatus(statusReceipt, "Assignment save was cancelled.");
            throw;
        }

        control.SetAssignmentState(
            change.ChangeId,
            result.Ok ? ProviderAssignmentSaveState.Saved : ProviderAssignmentSaveState.Failed,
            result.Message);
        if (result.Ok)
        {
            CompleteStatus(
                statusReceipt,
                change.IsAssigned
                    ? $"{AssignmentTargetName(change.TargetId)} now uses {ModelLabel(change.ModelId)}."
                    : $"{AssignmentTargetName(change.TargetId)} was removed from {ModelLabel(change.ModelId)}.",
                result.Message);
        }
        else
        {
            FailStatus(statusReceipt, $"Could not update {AssignmentTargetName(change.TargetId)}.", result.Message);
        }
        await RefreshAsync(
            refreshCatalog: result.Ok && target.IsDefault,
            cancellationToken);
    }

    public async Task SaveConfigurationAsync(
        ProviderModelConfigurationChangedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        var statusReceipt = BeginStatus(
            $"models.configuration.{change.ChangeId:N}",
            $"Saving configuration for {ModelLabel(change.ModelId)}…",
            change.ModelId);
        control.SetConfigurationState(
            change.ChangeId,
            ProviderModelConfigurationSaveState.Saving,
            "Saving model configuration…");
        if (!catalogItemsByModel.TryGetValue(change.ModelId, out var item))
        {
            control.SetConfigurationState(
                change.ChangeId,
                ProviderModelConfigurationSaveState.Failed,
                "The selected model changed; refresh and try again.");
            FailStatus(statusReceipt, "Model configuration was not saved.", "The selected model changed; refresh and try again.");
            return;
        }

        ProviderModelConfigurationControlResult result;
        try
        {
            result = await providerConfiguration.SetModelConfigurationAsync(
                new ProviderModelConfigurationRequest(
                    change.ModelId,
                    change.ContextWindow,
                    HistoryPolicyWire(change.HistoryPolicy),
                    ResponseToneWire(change.ResponseTone),
                    change.CustomTone,
                    change.ConfigurationIdentity,
                    item.Aliases,
                    ContextApplyRequired: item.LoadState == ProviderModelLoadState.Loaded
                        && !item.IsResidencyStale),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            control.SetConfigurationState(
                change.ChangeId,
                ProviderModelConfigurationSaveState.Failed,
                "Model configuration save was cancelled.");
            CancelStatus(statusReceipt, "Model configuration save was cancelled.");
            throw;
        }

        control.SetConfigurationState(
            change.ChangeId,
            result.Ok ? ProviderModelConfigurationSaveState.Saved : ProviderModelConfigurationSaveState.Failed,
            result.Message);
        if (result.Ok)
        {
            CompleteStatus(statusReceipt, $"Configuration saved for {ModelLabel(change.ModelId)}.", result.Message);
        }
        else
        {
            FailStatus(statusReceipt, $"Could not save configuration for {ModelLabel(change.ModelId)}.", result.Message);
        }
        await RefreshAsync(refreshCatalog: false, cancellationToken);
    }

    public async Task RunConfigurationReloadAsync(
        ProviderModelConfigurationReloadRequestedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        var statusReceipt = BeginStatus(
            $"models.configuration-reload.{change.OperationId:N}",
            $"Reloading {ModelLabel(change.ModelId)} to apply configuration…",
            change.ModelId);
        if (Interlocked.CompareExchange(ref lifecycleRunning, 1, 0) != 0)
        {
            control.SetConfigurationReloadState(
                change.OperationId,
                ProviderModelConfigurationReloadState.Failed,
                "Another model residency request is already running.");
            FailStatus(statusReceipt, "Model reload could not start.", "Another model residency request is already running.");
            return;
        }

        var state = ProviderModelConfigurationReloadState.Failed;
        var message = "Could not reload the selected model configuration.";
        var lockTaken = false;
        var requestStarted = false;
        var requestAccepted = false;
        var mutationOutcomeUnknown = false;
        var unloadAccepted = false;
        var loadAttempted = false;
        var startingCatalog = catalogProjection.Current;
        catalogItemsByModel.TryGetValue(change.ModelId, out var startingItem);
        control.SetConfigurationReloadState(
            change.OperationId,
            ProviderModelConfigurationReloadState.Running,
            "Reloading the model in LM Studio…");

        lifecycleCancellation?.Cancel();
        lifecycleCancellation?.Dispose();
        var lifecycleOwner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifecycleCancellation = lifecycleOwner;
        var lifecycleToken = lifecycleOwner.Token;
        try
        {
            if (startingCatalog is null
                || startingItem is null
                || startingItem.LoadState != ProviderModelLoadState.Loaded
                || startingItem.IsResidencyStale)
            {
                message = "Loaded-model evidence changed. Refresh and try again.";
                return;
            }
            if (isArenaBusy())
            {
                message = "Model residency cannot change while the arena is running.";
                return;
            }
            lockTaken = await arenaOperationLock.WaitAsync(0, lifecycleToken);
            if (!lockTaken)
            {
                message = "Another arena or provider operation is active. Try again when it finishes.";
                return;
            }

            var session = activeSession();
            var snapshot = session is null
                ? null
                : await sessionStore.LoadSnapshotAsync(session.Id, lifecycleToken);
            if (session is null || snapshot is null)
            {
                message = "The active session is unavailable. Refresh and try again.";
                return;
            }

            var shared = SharedConfig(snapshot);
            var connectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(session.Id, shared);
            var providerFingerprint = ProviderModelCatalogProjectionService.ProviderFingerprint(session.Id, snapshot);
            var configuration = ProviderConfigurationControlService.CaptureModelConfiguration(
                session.Id,
                snapshot,
                change.ModelId,
                startingItem.Aliases);
            if (!ModelProviderApiModes.IsLmStudioNative(shared.ApiMode)
                || !providerFingerprint.Equals(startingCatalog.ProviderFingerprint, StringComparison.Ordinal)
                || !configuration.ConfigurationIdentity.Equals(change.ConfigurationIdentity, StringComparison.Ordinal))
            {
                message = "The LM Studio connection, session, or model configuration changed. Refresh and try again.";
                return;
            }
            lifecycleConnectionIdentity = connectionIdentity;
            refreshCancellation?.Cancel();
            _ = catalogProjection.BeginRefresh(session.Id, snapshot);
            void OnMutationStarting()
            {
                lifecycleToken.ThrowIfCancellationRequested();
                var currentSession = activeSession();
                if (currentSession is null || !currentSession.Id.Equals(session.Id, StringComparison.Ordinal))
                {
                    throw new ProviderLifecycleContextChangedException();
                }
                if (!control.MarkConfigurationReloadMutationStarted(change.OperationId))
                {
                    throw new ProviderLifecycleContextChangedException();
                }
                requestStarted = true;
            }

            var unload = await modelLifecycle.UnloadAsync(
                shared.BaseUrl,
                [change.ModelId],
                shared.ApiMode,
                shared.ApiToken,
                lifecycleToken,
                mutationStarting: OnMutationStarting);
            mutationOutcomeUnknown = unload.Any(result => result.MutationOutcomeUnknown);
            unloadAccepted = unload.All(result => !result.IsFailure);
            if (!unloadAccepted)
            {
                state = mutationOutcomeUnknown
                    ? ProviderModelConfigurationReloadState.Unconfirmed
                    : ProviderModelConfigurationReloadState.Failed;
                message = mutationOutcomeUnknown
                    ? "The LM Studio unload began but ended without a definite outcome. Refresh to verify residency and effective context."
                    : "LM Studio rejected the unload required to apply this context configuration; the prior loaded instance remains authoritative.";
            }
            else
            {
                loadAttempted = true;
                var load = await modelLifecycle.PreloadAsync(
                    shared.BaseUrl,
                    [change.ModelId],
                    shared.ApiMode,
                    shared.ApiToken,
                    configuration.ConfiguredContextWindow,
                    shared.NativeIdleTtlSeconds,
                    lifecycleToken,
                    requireCatalogMatch: true,
                    mutationStarting: OnMutationStarting);
                mutationOutcomeUnknown = load.Any(result => result.MutationOutcomeUnknown);
                requestAccepted = load.All(result => !result.IsFailure);
                if (!requestAccepted)
                {
                    state = mutationOutcomeUnknown
                        ? ProviderModelConfigurationReloadState.Unconfirmed
                        : ProviderModelConfigurationReloadState.Failed;
                    message = mutationOutcomeUnknown
                        ? "LM Studio accepted the unload, but the replacement load ended without a definite outcome. Refresh to verify whether the model is loaded."
                        : "LM Studio unloaded the prior instance but rejected the replacement load. The model is now unloaded; use Load model to retry.";
                }
            }

            await RefreshAsync(refreshCatalog: true, lifecycleToken);
            var confirmedCatalog = catalogProjection.Current;
            var confirmedItem = confirmedCatalog?.Models.FirstOrDefault(item =>
                item.Id.Equals(change.ModelId, StringComparison.Ordinal));
            var usesProviderDefault = configuration.ConfiguredContextWindow == 0;
            var expectedContext = !usesProviderDefault && startingItem.MaximumContextLength is int maximum && maximum > 0
                ? Math.Min(configuration.ConfiguredContextWindow, maximum)
                : configuration.ConfiguredContextWindow;
            var confirmedLoaded = confirmedCatalog is not null
                && confirmedCatalog.ProviderFingerprint.Equals(startingCatalog.ProviderFingerprint, StringComparison.Ordinal)
                && confirmedItem is { LoadState: ProviderModelLoadState.Loaded, IsResidencyStale: false };
            var confirmed = requestAccepted
                && confirmedLoaded
                && (usesProviderDefault || confirmedItem!.EffectiveContextLength == expectedContext);
            var confirmedUnloadedAfterRejectedLoad = unloadAccepted
                && loadAttempted
                && !requestAccepted
                && confirmedCatalog is not null
                && confirmedCatalog.ProviderFingerprint.Equals(startingCatalog.ProviderFingerprint, StringComparison.Ordinal)
                && confirmedItem is { LoadState: ProviderModelLoadState.NotLoaded, IsResidencyStale: false };
            if (confirmed)
            {
                var clearedApplyIntent = await providerConfiguration
                    .ConfirmModelConfigurationAppliedUnderLockAsync(
                        change.ModelId,
                        startingItem.Aliases,
                        change.ConfigurationIdentity,
                        lifecycleToken);
                if (clearedApplyIntent)
                {
                    state = ProviderModelConfigurationReloadState.Succeeded;
                    message = usesProviderDefault
                        ? "LM Studio confirmed the model was reloaded without an explicit context override. Routing assignments were unchanged."
                        : $"LM Studio confirmed {expectedContext:N0} active context. Routing assignments were unchanged.";
                }
                else
                {
                    state = ProviderModelConfigurationReloadState.Unconfirmed;
                    message = "LM Studio confirmed the loaded instance, but the saved configuration changed before apply intent could be cleared. Refresh and verify.";
                }
            }
            else if (requestAccepted)
            {
                state = ProviderModelConfigurationReloadState.Unconfirmed;
                message = "LM Studio accepted the reload, but effective context evidence is not yet confirmed.";
            }
            else if (mutationOutcomeUnknown)
            {
                state = ProviderModelConfigurationReloadState.Unconfirmed;
            }
            else if (confirmedUnloadedAfterRejectedLoad)
            {
                state = ProviderModelConfigurationReloadState.Failed;
                message = "LM Studio confirmed the prior instance was unloaded, but rejected the replacement load. The model is currently unloaded; use Load model to retry.";
            }
        }
        catch (ProviderLifecycleContextChangedException)
        {
            state = requestStarted
                ? ProviderModelConfigurationReloadState.Unconfirmed
                : ProviderModelConfigurationReloadState.Failed;
            message = requestStarted
                ? "The provider or session changed after reload began. Refresh to verify effective context."
                : "The provider or session changed before the reload request was sent.";
        }
        catch (OperationCanceledException) when (lifecycleOwner.IsCancellationRequested)
        {
            state = requestStarted
                ? ProviderModelConfigurationReloadState.Unconfirmed
                : ProviderModelConfigurationReloadState.Failed;
            message = requestStarted
                ? "The reload was interrupted after it began. Refresh to verify effective context."
                : "The configuration reload was cancelled.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            state = requestStarted
                ? ProviderModelConfigurationReloadState.Unconfirmed
                : ProviderModelConfigurationReloadState.Failed;
            message = requestStarted
                ? "The LM Studio reload ended without confirmed effective-context evidence."
                : $"The LM Studio reload could not start safely ({exception.GetType().Name}).";
        }
        finally
        {
            if (lockTaken)
            {
                arenaOperationLock.Release();
            }
            if (ReferenceEquals(lifecycleCancellation, lifecycleOwner))
            {
                lifecycleCancellation = null;
                lifecycleOwner.Dispose();
            }
            lifecycleConnectionIdentity = "";
            Interlocked.Exchange(ref lifecycleRunning, 0);
            control.SetConfigurationReloadState(change.OperationId, state, message);
            CompleteReloadStatus(statusReceipt, state, change.ModelId, message);
        }
    }

    public async Task RunLifecycleAsync(
        ProviderModelLifecycleRequestedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        var statusReceipt = BeginStatus(
            $"models.lifecycle.{change.OperationId:N}",
            change.Load
                ? $"Loading {ModelLabel(change.ModelId)}…"
                : $"Unloading {ModelLabel(change.ModelId)}…",
            change.ModelId);
        if (Interlocked.CompareExchange(ref lifecycleRunning, 1, 0) != 0)
        {
            control.SetLifecycleState(
                change.OperationId,
                ProviderModelLifecycleActionState.Failed,
                "Another model residency request is already running.");
            FailStatus(statusReceipt, "Model residency request could not start.", "Another model residency request is already running.");
            return;
        }

        var action = change.Load ? "load" : "unload";
        var confirmedAction = change.Load
            ? "Model loaded in LM Studio."
            : "Model unloaded from LM Studio.";
        var lifecycleState = ProviderModelLifecycleActionState.Failed;
        var lifecycleMessage = $"Could not {action} the selected model.";
        var lockTaken = false;
        var requestStarted = false;
        var requestAccepted = false;
        var startingCatalog = catalogProjection.Current;
        catalogItemsByModel.TryGetValue(change.ModelId, out var startingItem);

        control.SetLifecycleState(
            change.OperationId,
            ProviderModelLifecycleActionState.Running,
            change.Load ? "Asking LM Studio to load the model…" : "Asking LM Studio to unload the model…");

        lifecycleCancellation?.Cancel();
        lifecycleCancellation?.Dispose();
        var lifecycleOwner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifecycleCancellation = lifecycleOwner;
        var lifecycleToken = lifecycleOwner.Token;
        try
        {
            if (startingCatalog is null
                || startingItem is null
                || (change.Load ? !startingItem.CanLoad : !startingItem.CanUnload))
            {
                lifecycleMessage = "The selected model state changed. Refresh the catalog and try again.";
                return;
            }

            if (isArenaBusy())
            {
                lifecycleMessage = "Model residency cannot change while the arena is running.";
                return;
            }

            lockTaken = await arenaOperationLock.WaitAsync(0, lifecycleToken);
            if (!lockTaken)
            {
                lifecycleMessage = "Another arena or provider operation is active. Try again when it finishes.";
                return;
            }

            if (isArenaBusy())
            {
                lifecycleMessage = "Model residency cannot change while the arena is running.";
                return;
            }

            var session = activeSession();
            var snapshot = session is null
                ? null
                : await sessionStore.LoadSnapshotAsync(session.Id, lifecycleToken);
            if (session is null || snapshot is null)
            {
                lifecycleMessage = "The active session is unavailable. Refresh and try again.";
                return;
            }

            var shared = SharedConfig(snapshot);
            var providerFingerprint = ProviderModelCatalogProjectionService.ProviderFingerprint(session.Id, snapshot);
            var connectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(session.Id, shared);
            if (!ModelProviderApiModes.IsLmStudioNative(shared.ApiMode)
                || !providerFingerprint.Equals(startingCatalog.ProviderFingerprint, StringComparison.Ordinal))
            {
                lifecycleMessage = "The LM Studio connection or session changed. Refresh and try again.";
                return;
            }

            lifecycleConnectionIdentity = connectionIdentity;

            refreshCancellation?.Cancel();
            // Supersede any in-flight heartbeat without clearing the last
            // confirmed catalog. The pending action must retain its selected
            // row until the post-action native refresh proves a new state.
            _ = catalogProjection.BeginRefresh(session.Id, snapshot);
            void OnMutationStarting()
            {
                lifecycleToken.ThrowIfCancellationRequested();
                var currentSession = activeSession();
                if (currentSession is null
                    || !currentSession.Id.Equals(session.Id, StringComparison.Ordinal))
                {
                    throw new ProviderLifecycleContextChangedException();
                }

                if (!control.MarkLifecycleMutationStarted(change.OperationId))
                {
                    throw new ProviderLifecycleContextChangedException();
                }

                requestStarted = true;
            }

            var results = change.Load
                ? await modelLifecycle.PreloadAsync(
                    shared.BaseUrl,
                    [change.ModelId],
                    shared.ApiMode,
                    shared.ApiToken,
                    shared.ContextLength,
                    shared.NativeIdleTtlSeconds,
                    lifecycleToken,
                    requireCatalogMatch: true,
                    mutationStarting: OnMutationStarting)
                : await modelLifecycle.UnloadAsync(
                    shared.BaseUrl,
                    [change.ModelId],
                    shared.ApiMode,
                    shared.ApiToken,
                    lifecycleToken,
                    mutationStarting: OnMutationStarting);
            var failures = results.Where(result => result.IsFailure).ToArray();
            var outcomeUnknown = results.Any(result => result.MutationOutcomeUnknown);
            requestAccepted = failures.Length == 0;
            if (!requestAccepted)
            {
                lifecycleMessage = $"LM Studio did not accept the {action} request. Refresh the catalog or inspect provider diagnostics.";
            }

            await RefreshAsync(refreshCatalog: true, lifecycleToken);
            var confirmedCatalog = catalogProjection.Current;
            var confirmedItem = confirmedCatalog?.Models.FirstOrDefault(item =>
                item.Id.Equals(change.ModelId, StringComparison.Ordinal));
            var confirmed = confirmedCatalog is not null
                && confirmedCatalog.ProviderFingerprint.Equals(startingCatalog.ProviderFingerprint, StringComparison.Ordinal)
                && confirmedItem is not null
                && !confirmedItem.IsResidencyStale
                && (change.Load
                    ? confirmedItem.LoadState == ProviderModelLoadState.Loaded
                    : confirmedItem.LoadState == ProviderModelLoadState.NotLoaded);
            if (confirmed)
            {
                lifecycleState = ProviderModelLifecycleActionState.Succeeded;
                lifecycleMessage = $"{confirmedAction} Routing assignments were unchanged.";
            }
            else if (requestAccepted)
            {
                lifecycleState = ProviderModelLifecycleActionState.Unconfirmed;
                lifecycleMessage = $"LM Studio accepted the {action} request, but its catalog has not confirmed the new state.";
            }
            else if (outcomeUnknown)
            {
                lifecycleState = ProviderModelLifecycleActionState.Unconfirmed;
                lifecycleMessage = "The LM Studio request started but ended without a definite outcome. Refresh to verify the load state.";
            }
        }
        catch (ProviderLifecycleContextChangedException)
        {
            lifecycleState = requestStarted
                ? ProviderModelLifecycleActionState.Unconfirmed
                : ProviderModelLifecycleActionState.Failed;
            lifecycleMessage = requestStarted
                ? "The provider or session changed after a lifecycle request started. The LM Studio load state is unknown; refresh to verify it."
                : "The provider or session changed before the request; no lifecycle request was sent.";
        }
        catch (OperationCanceledException) when (lifecycleOwner.IsCancellationRequested)
        {
            lifecycleState = requestStarted
                ? ProviderModelLifecycleActionState.Unconfirmed
                : ProviderModelLifecycleActionState.Failed;
            lifecycleMessage = requestStarted
                ? "The request was interrupted; the LM Studio load state is unknown. Refresh to verify it."
                : "The model residency request was cancelled.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            lifecycleState = requestStarted
                ? ProviderModelLifecycleActionState.Unconfirmed
                : ProviderModelLifecycleActionState.Failed;
            lifecycleMessage = requestStarted
                ? "The LM Studio request ended without confirmed load-state evidence. Refresh to verify it."
                : $"The LM Studio request could not start safely ({exception.GetType().Name}).";
        }
        finally
        {
            if (lockTaken)
            {
                arenaOperationLock.Release();
            }

            if (ReferenceEquals(lifecycleCancellation, lifecycleOwner))
            {
                lifecycleCancellation = null;
                lifecycleOwner.Dispose();
            }

            lifecycleConnectionIdentity = "";
            Interlocked.Exchange(ref lifecycleRunning, 0);
            control.SetLifecycleState(change.OperationId, lifecycleState, lifecycleMessage);
            CompleteLifecycleStatus(statusReceipt, lifecycleState, change.ModelId, change.Load, lifecycleMessage);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposalCancellation.Cancel();
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
        lifecycleCancellation?.Cancel();
        lifecycleCancellation?.Dispose();
        lifecycleCancellation = null;
    }

    private async Task<ProviderModelCatalogSnapshot> LoadCatalogAsync(
        ProviderModelCatalogRefreshLease lease,
        ModelProviderConfig shared,
        CancellationToken cancellationToken)
    {
        var mode = ModelProviderApiModes.Normalize(shared.ApiMode);
        if (ModelProviderApiModes.IsLmStudioNative(mode))
        {
            var catalog = await lmStudioCatalog.TryLoadAsync(shared.BaseUrl, shared.ApiToken, cancellationToken);
            if (catalog.Ok)
            {
                return ProviderModelCatalogProjectionService.FromLmStudio(
                    lease,
                    catalog,
                    shared.Model,
                    DateTimeOffset.Now);
            }

            return await LoadCompatibleFallbackAsync(lease, shared, catalog.Error, cancellationToken);
        }

        if (ModelProviderApiModes.IsOllamaNative(mode))
        {
            var catalog = await ollamaCatalog.TryLoadAsync(shared.BaseUrl, shared.ApiToken, cancellationToken);
            if (catalog.Ok)
            {
                return ProviderModelCatalogProjectionService.FromOllama(
                    lease,
                    catalog,
                    shared.Model,
                    DateTimeOffset.Now);
            }

            return ProviderModelCatalogProjectionService.FromOllama(
                lease,
                catalog,
                shared.Model,
                DateTimeOffset.Now);
        }

        if (ModelProviderApiModes.IsLlamaCppNative(mode))
        {
            var runtime = await llamaCppRuntime.InspectAsync(shared, cancellationToken);
            if (runtime.Available && runtime.Models.Count > 0)
            {
                return ProviderModelCatalogProjectionService.FromLlamaCpp(
                    lease,
                    runtime,
                    shared.Model);
            }

            return await LoadCompatibleFallbackAsync(lease, shared, runtime.Error, cancellationToken);
        }

        var result = await providerHealth.ListModelsAsync(shared, cancellationToken);
        return ProviderModelCatalogProjectionService.FromCompatible(
            lease,
            result.Models,
            result.Ok,
            result.Error,
            shared.Model,
            result.CheckedAt,
            result.OmittedModelCount);
    }

    private async Task<ProviderModelCatalogSnapshot> LoadCompatibleFallbackAsync(
        ProviderModelCatalogRefreshLease lease,
        ModelProviderConfig shared,
        string nativeError,
        CancellationToken cancellationToken)
    {
        var compatible = CloneForCompatibleDiscovery(shared);
        var result = await providerHealth.ListModelsAsync(compatible, cancellationToken);
        var error = string.Join(
            " ",
            new[] { nativeError, result.Error }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));
        return ProviderModelCatalogProjectionService.FromCompatible(
            lease,
            result.Models,
            result.Ok,
            error,
            shared.Model,
            result.CheckedAt,
            result.OmittedModelCount);
    }

    private void ApplyPresentation(
        ArenaSnapshot snapshot,
        ProviderModelCatalogSnapshot catalog,
        bool isRefreshing)
    {
        assignmentsByModel.Clear();
        catalogItemsByModel.Clear();
        profileSettingsByModel.Clear();
        var shared = SharedConfig(snapshot);
        currentSessionId = catalog.SessionId;
        currentConnectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(catalog.SessionId, shared);
        var lmStudioLifecycle = ModelProviderApiModes.IsLmStudioNative(shared.ApiMode)
            && catalog.ResidencyEvidence != ProviderCatalogEvidenceState.Unavailable;
        var assignmentBatch = ProviderModelAssignmentProjectionService.CreateBatch(
            catalog.SessionId,
            snapshot);
        var rows = new List<ProviderModelAssignmentPresentation>();
        foreach (var item in catalog.LoadedModels.Concat(catalog.AvailableModels))
        {
            catalogItemsByModel[item.Id] = item;
            var assignment = assignmentBatch.Project(item.Id, item.Aliases);
            var configuration = ProviderConfigurationControlService.CaptureModelConfiguration(
                catalog.SessionId,
                snapshot,
                item.Id,
                item.Aliases);
            if (HasRegisteredModelSetting(snapshot, shared, item))
            {
                profileSettingsByModel[item.Id] = new WpfProviderModelSettings
                {
                    Model = item.Id,
                    ModelIdentity = ModelRuntimeSettingsRegistry.Identity(ConfigForModel(shared, item.Id)),
                    ConfiguredContextWindow = configuration.ConfiguredContextWindow,
                    HistoryPolicy = configuration.HistoryPolicy,
                    ResponseTone = configuration.ResponseTone,
                    CustomTone = configuration.CustomTone,
                    PendingApply = HasPendingConfigurationApply(snapshot, shared, item)
                };
            }
            var effectiveContext = item.LoadState == ProviderModelLoadState.Loaded
                                   && !item.IsResidencyStale
                ? item.EffectiveContextLength.GetValueOrDefault()
                : 0;
            var expectedContext = configuration.ConfiguredContextWindow > 0
                                  && item.MaximumContextLength is int maximum
                                  && maximum > 0
                ? Math.Min(configuration.ConfiguredContextWindow, maximum)
                : configuration.ConfiguredContextWindow;
            var requiresReload = lmStudioLifecycle
                && item.LoadState == ProviderModelLoadState.Loaded
                && !item.IsResidencyStale
                && (HasPendingConfigurationApply(snapshot, shared, item)
                    || (expectedContext > 0 && effectiveContext != expectedContext));
            var contextEvidence = ConfigurationContextEvidence(
                ContextEvidence(item, catalog.ResidencyEvidence, effectiveContext),
                configuration.ConfiguredContextWindow,
                configuration.HistoryPolicy);
            assignmentsByModel[item.Id] = assignment;
            rows.Add(new ProviderModelAssignmentPresentation(
                item.Id,
                item.DisplayName,
                item.LoadState switch
                {
                    ProviderModelLoadState.Loaded => ProviderModelAvailability.Loaded,
                    ProviderModelLoadState.NotLoaded => ProviderModelAvailability.Available,
                    _ => ProviderModelAvailability.Unavailable
                },
                ModelStatus(item, catalog.ResidencyEvidence),
                ModelMetadata(item),
                assignment.Targets.Where(target => target.Assigned).Select(target => target.Id).ToArray(),
                ModelAutomationHelp(item, catalog.ResidencyEvidence),
                CanLoad: lmStudioLifecycle && item.CanLoad,
                CanUnload: lmStudioLifecycle && item.CanUnload,
                LifecycleHelp: LifecycleHelpFor(item, lmStudioLifecycle),
                IsResidencyStale: item.IsResidencyStale,
                Configuration: new ProviderModelConfigurationPresentation(
                    configuration.ConfiguredContextWindow,
                    effectiveContext,
                    contextEvidence,
                    HistoryPolicyPresentation(configuration.HistoryPolicy),
                    ResponseTonePresentation(configuration.ResponseTone),
                    configuration.CustomTone,
                    CanEdit: !isArenaBusy(),
                    ChapteredAvailable: false,
                    RequiresReload: requiresReload,
                    ConfigurationIdentity: configuration.ConfigurationIdentity,
                    Status: ConfigurationStatus(
                        requiresReload,
                        configuration.ConfiguredContextWindow,
                        configuration.HistoryPolicy))));
        }

        var selected = control.SelectedModelId;
        if (!rows.Any(row => row.Id.Equals(selected, StringComparison.Ordinal)))
        {
            selected = rows.FirstOrDefault(row =>
                    row.Id.Equals(catalog.ConfiguredModel, StringComparison.OrdinalIgnoreCase))?.Id
                ?? rows.FirstOrDefault()?.Id
                ?? "";
        }

        var selectedAssignment = selected.Length > 0
            && assignmentsByModel.TryGetValue(selected, out var projected)
                ? projected
                : ProviderModelAssignmentProjection.Empty(selected);
        var selectedIsDefault = selectedAssignment.Targets.Any(target => target.IsDefault && target.Assigned);
        var busy = isArenaBusy();
        var providerName = ProviderName(shared.ApiMode);
        var catalogReady = catalog.CatalogEvidence != ProviderCatalogEvidenceState.Unavailable;
        var catalogPartial = catalog.CatalogEvidence == ProviderCatalogEvidenceState.Partial
            || catalog.ResidencyEvidence == ProviderCatalogEvidenceState.Partial
            || catalog.OmittedModelCount > 0;
        var connectionState = isRefreshing
            ? ProviderConnectionState.Checking
            : !catalogReady
                ? ProviderConnectionState.Offline
                : catalogPartial
                    ? ProviderConnectionState.Partial
                    : ProviderConnectionState.Online;
        control.ApplyPresentation(new ProviderModelAssignmentsPresentation(
            providerName,
            connectionState switch
            {
                ProviderConnectionState.Online => "Online",
                ProviderConnectionState.Partial => "Partial evidence",
                ProviderConnectionState.Checking => "Refreshing",
                _ => "Unavailable"
            },
            connectionState,
            isRefreshing
                ? $"Refreshing… {catalog.Status}"
                : lmStudioLifecycle
                    ? $"{catalog.Status} Auto-checking every 5 seconds while Models is open."
                    : catalog.Status,
            rows,
            selectedAssignment.Targets.Select(target => new ProviderAssignmentTargetPresentation(
                target.Id,
                target.IsDefault ? "Default for unassigned agents" : target.DisplayName,
                AssignmentHelp(target, selectedIsDefault),
                IsEnabled: !busy,
                AssignmentState: target.IsDefault && target.Assigned
                    ? ProviderTargetAssignmentState.Default
                    : target.Assigned
                        ? ProviderTargetAssignmentState.Explicit
                        : selectedIsDefault && target.InheritsDefault
                            ? ProviderTargetAssignmentState.InheritsDefault
                            : ProviderTargetAssignmentState.Unassigned,
                AssignedState: target.IsDefault
                    ? ProviderTargetAssignmentState.Default
                    : ProviderTargetAssignmentState.Explicit,
                ClearedState: selectedIsDefault && !target.IsDefault
                    ? ProviderTargetAssignmentState.InheritsDefault
                    : ProviderTargetAssignmentState.Unassigned,
                IsDefault: target.IsDefault)).ToArray(),
            selected,
            IsRefreshing: isRefreshing,
            CanRefresh: !isRefreshing,
            CanOpenConnectionSettings: true,
            CanClose: true,
            CanAssign: !busy && rows.Count > 0,
            PresentationIdentity: catalog.ProviderFingerprint,
            ConnectionIdentity: currentConnectionIdentity,
            CanRunLifecycle: !busy && !isRefreshing && lmStudioLifecycle));
    }

    private ApplicationStatusReceipt BeginStatus(string key, string summary, string modelId) =>
        statusCenter?.Begin(
            key,
            "Models",
            summary,
            navigationTarget: $"models:{Uri.EscapeDataString(modelId)}",
            identity: StatusIdentity()) ?? default;

    private ApplicationStatusIdentity StatusIdentity() => new(currentSessionId, currentConnectionIdentity);

    private void CompleteStatus(ApplicationStatusReceipt receipt, string summary, string detail)
    {
        if (!receipt.IsEmpty)
        {
            statusCenter?.Complete(receipt, summary, detail);
        }
    }

    private void FailStatus(ApplicationStatusReceipt receipt, string summary, string detail)
    {
        if (!receipt.IsEmpty)
        {
            statusCenter?.Fail(receipt, summary, detail);
        }
    }

    private void CancelStatus(ApplicationStatusReceipt receipt, string summary)
    {
        if (!receipt.IsEmpty)
        {
            statusCenter?.Cancel(receipt, summary);
        }
    }

    private void CompleteLifecycleStatus(
        ApplicationStatusReceipt receipt,
        ProviderModelLifecycleActionState state,
        string modelId,
        bool load,
        string detail)
    {
        if (receipt.IsEmpty)
        {
            return;
        }

        var action = load ? "load" : "unload";
        switch (state)
        {
            case ProviderModelLifecycleActionState.Succeeded:
                statusCenter?.Complete(receipt, $"{ModelLabel(modelId)} {action} confirmed.", detail);
                break;
            case ProviderModelLifecycleActionState.Unconfirmed:
                statusCenter?.MarkUnconfirmed(receipt, $"{ModelLabel(modelId)} {action} is unconfirmed.", detail);
                break;
            default:
                statusCenter?.Fail(receipt, $"Could not {action} {ModelLabel(modelId)}.", detail);
                break;
        }
    }

    private void CompleteReloadStatus(
        ApplicationStatusReceipt receipt,
        ProviderModelConfigurationReloadState state,
        string modelId,
        string detail)
    {
        if (receipt.IsEmpty)
        {
            return;
        }

        switch (state)
        {
            case ProviderModelConfigurationReloadState.Succeeded:
                statusCenter?.Complete(receipt, $"{ModelLabel(modelId)} configuration applied.", detail);
                break;
            case ProviderModelConfigurationReloadState.Unconfirmed:
                statusCenter?.MarkUnconfirmed(receipt, $"{ModelLabel(modelId)} reload is unconfirmed.", detail);
                break;
            default:
                statusCenter?.Fail(receipt, $"Could not reload {ModelLabel(modelId)}.", detail);
                break;
        }
    }

    private string ModelLabel(string modelId) =>
        catalogItemsByModel.TryGetValue(modelId, out var item)
            ? item.DisplayName
            : modelId;

    private static string AssignmentTargetName(string targetId) =>
        targetId.Equals("shared", StringComparison.OrdinalIgnoreCase)
            ? "Default"
            : targetId.Length == 0
                ? "Assignment"
                : char.ToUpperInvariant(targetId[0]) + targetId[1..];

    internal static string LifecycleHelpFor(
        ProviderModelCatalogItem item,
        bool lmStudioLifecycle)
    {
        if (item.IsResidencyStale)
        {
            return lmStudioLifecycle
                ? "Showing the last confirmed grouping. Refresh before changing LM Studio residency."
                : "Showing the last confirmed provider grouping. Refresh before relying on model residency.";
        }

        if (item.LoadState == ProviderModelLoadState.Loaded && !item.CanUnload)
        {
            return lmStudioLifecycle
                ? "LM Studio reports this model as loaded but did not provide an unloadable instance identifier. Refresh or manage it in LM Studio."
                : "The provider reports this model as loaded, but this connection does not expose an unload action.";
        }

        return lmStudioLifecycle
            ? "LM Studio controls model residency. Assignments remain unchanged, and LM Studio chooses hardware placement."
            : "Load and unload controls are unavailable for this provider evidence.";
    }

    private static ProviderModelAssignmentsPresentation EmptyPresentation(string status) => new(
        "Provider",
        "Unavailable",
        ProviderConnectionState.Unknown,
        status,
        [],
        [],
        CanRefresh: false,
        CanAssign: false);

    private static ModelProviderConfig SharedConfig(ArenaSnapshot snapshot) =>
        snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var shared)
            ? shared
            : new ModelProviderConfig();

    private static ModelProviderConfig ConfigForModel(ModelProviderConfig shared, string model) => new()
    {
        BaseUrl = shared.BaseUrl,
        ApiMode = shared.ApiMode,
        Model = model
    };

    private static bool HasRegisteredModelSetting(
        ArenaSnapshot snapshot,
        ModelProviderConfig shared,
        ProviderModelCatalogItem item) =>
        item.Aliases.Append(item.Id).Any(alias =>
            snapshot.ModelSettings.ContainsKey(
                ModelRuntimeSettingsRegistry.Identity(ConfigForModel(shared, alias))));

    private static bool HasPendingConfigurationApply(
        ArenaSnapshot snapshot,
        ModelProviderConfig shared,
        ProviderModelCatalogItem item)
    {
        var aliases = item.Aliases.Append(item.Id)
            .Concat(snapshot.Configs.Values
                .Where(config => !string.IsNullOrWhiteSpace(config.Model)
                    && item.Aliases.Append(item.Id).Any(alias =>
                        alias.Equals(config.Model, StringComparison.OrdinalIgnoreCase)
                        || ProviderModelCatalogProjectionService.SafeModelIdentifier(alias)
                            .Equals(ProviderModelCatalogProjectionService.SafeModelIdentifier(config.Model), StringComparison.OrdinalIgnoreCase)))
                .Select(config => config.Model));
        return ModelRuntimeSettingsRegistry.HasPendingConfigurationApply(
            snapshot,
            aliases.Distinct(StringComparer.OrdinalIgnoreCase).Select(alias => ConfigForModel(shared, alias)));
    }

    private static ModelProviderConfig CloneForCompatibleDiscovery(ModelProviderConfig source) => new()
    {
        BaseUrl = source.BaseUrl,
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        ApiToken = source.ApiToken,
        Model = source.Model,
        Timeout = Math.Clamp(source.Timeout, 1, 30),
        Temperature = 0,
        MaxOutputTokens = 16
    };

    private static string ProviderName(string mode) => ModelProviderApiModes.Normalize(mode) switch
    {
        ModelProviderApiModes.LmStudioNative => "LM Studio",
        ModelProviderApiModes.OllamaNative => "Ollama",
        ModelProviderApiModes.LlamaCppNative => "llama.cpp",
        _ => "Compatible provider"
    };

    private static string ModelStatus(
        ProviderModelCatalogItem item,
        ProviderCatalogEvidenceState residencyEvidence)
    {
        if (item.IsConfiguredOnly)
        {
            return "Configured";
        }

        if (item.IsResidencyStale)
        {
            return item.LoadState == ProviderModelLoadState.Loaded
                ? "Last confirmed loaded · current state unavailable"
                : "Last confirmed available · current state unavailable";
        }

        return item.LoadState switch
        {
            ProviderModelLoadState.Loaded => "Loaded",
            ProviderModelLoadState.NotLoaded => "Available",
            _ => "Load state unavailable"
        };
    }

    private static string ModelMetadata(ProviderModelCatalogItem item)
    {
        var parts = new List<string>();
        AddIfPresent(parts, item.Publisher);
        AddIfPresent(parts, item.Quantization);
        if (item.ContextLength is int context && context > 0)
        {
            parts.Add($"{context.ToString("N0", CultureInfo.InvariantCulture)} context");
        }

        if (item.SizeBytes is long size && size > 0)
        {
            parts.Add($"{size / 1024d / 1024d / 1024d:0.##} GiB");
        }

        AddIfPresent(parts, item.CapabilitySummary);
        return parts.Count == 0 ? "No additional metadata reported." : string.Join(" · ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ModelAutomationHelp(
        ProviderModelCatalogItem item,
        ProviderCatalogEvidenceState residencyEvidence) =>
        $"{ModelStatus(item, residencyEvidence)}. {ModelMetadata(item)} Assignment changes routing only; hardware placement remains with the provider.";

    private static string AssignmentHelp(
        ProviderModelAssignmentTarget target,
        bool selectedIsDefault)
    {
        if (target.IsDefault)
        {
            return target.Assigned
                ? "Turn off to leave unassigned Arena roles without a model. Agent Workspace and provider diagnostics keep using the shared provider model."
                : "Turn on to use this model for Arena roles without an explicit assignment.";
        }

        if (selectedIsDefault && target.InheritsDefault)
        {
            return "This target currently uses this model through Default. Turn on to preserve it as an explicit assignment when Default is off.";
        }

        return target.Assigned
            ? "Turn off to remove this explicit assignment and use Default when it is enabled."
            : "Turn on to assign this model immediately.";
    }

    private static bool CatalogEvidenceEquivalent(
        ProviderModelCatalogSnapshot? first,
        ProviderModelCatalogSnapshot second)
    {
        if (first is null
            || !first.ProviderFingerprint.Equals(second.ProviderFingerprint, StringComparison.Ordinal)
            || first.CatalogEvidence != second.CatalogEvidence
            || first.ResidencyEvidence != second.ResidencyEvidence
            || !first.ConfiguredModel.Equals(second.ConfiguredModel, StringComparison.Ordinal)
            || first.ConfiguredModelMissing != second.ConfiguredModelMissing
            || first.OmittedModelCount != second.OmittedModelCount
            || !first.Status.Equals(second.Status, StringComparison.Ordinal)
            || first.Models.Count != second.Models.Count)
        {
            return false;
        }

        return first.Models.Zip(second.Models).All(pair =>
            pair.First.Id.Equals(pair.Second.Id, StringComparison.Ordinal)
            && pair.First.DisplayName.Equals(pair.Second.DisplayName, StringComparison.Ordinal)
            && pair.First.LoadState == pair.Second.LoadState
            && pair.First.CanLoad == pair.Second.CanLoad
            && pair.First.CanUnload == pair.Second.CanUnload
            && pair.First.Publisher.Equals(pair.Second.Publisher, StringComparison.Ordinal)
            && pair.First.Quantization.Equals(pair.Second.Quantization, StringComparison.Ordinal)
            && pair.First.ContextLength == pair.Second.ContextLength
            && pair.First.MaximumContextLength == pair.Second.MaximumContextLength
            && pair.First.EffectiveContextLength == pair.Second.EffectiveContextLength
            && pair.First.SizeBytes == pair.Second.SizeBytes
            && pair.First.CapabilitySummary.Equals(pair.Second.CapabilitySummary, StringComparison.Ordinal)
            && pair.First.IsConfiguredOnly == pair.Second.IsConfiguredOnly
            && pair.First.IsResidencyStale == pair.Second.IsResidencyStale
            && pair.First.Aliases.SequenceEqual(pair.Second.Aliases, StringComparer.Ordinal));
    }

    internal static ProviderModelCatalogSnapshot PreserveLastConfirmedResidency(
        ProviderModelCatalogSnapshot? previous,
        ProviderModelCatalogSnapshot candidate,
        bool preserveMissingRows,
        string selectedModelId = "")
    {
        if (previous is null
            || !previous.SessionId.Equals(candidate.SessionId, StringComparison.Ordinal)
            || !previous.ProviderFingerprint.Equals(candidate.ProviderFingerprint, StringComparison.Ordinal))
        {
            return candidate;
        }

        var previousById = previous.Models
            .Where(item => item.LoadState != ProviderModelLoadState.Unavailable)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var revised = new List<ProviderModelCatalogItem>();
        var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preserved = false;
        foreach (var item in candidate.Models)
        {
            presentIds.Add(item.Id);
            if (item.LoadState == ProviderModelLoadState.Unavailable
                && previousById.TryGetValue(item.Id, out var confirmed))
            {
                revised.Add(item with
                {
                    LoadState = confirmed.LoadState,
                    CanLoad = false,
                    CanUnload = false,
                    IsResidencyStale = true
                });
                preserved = true;
            }
            else
            {
                revised.Add(item);
            }
        }

        if (preserveMissingRows || candidate.ResidencyEvidence == ProviderCatalogEvidenceState.Unavailable)
        {
            foreach (var confirmed in previousById.Values.Where(item => !presentIds.Contains(item.Id)))
            {
                revised.Add(confirmed with
                {
                    CanLoad = false,
                    CanUnload = false,
                    IsResidencyStale = true
                });
                preserved = true;
            }
        }

        if (!preserved)
        {
            return candidate;
        }

        var displayLimit = ProviderModelCatalogProjectionService.MaximumModelCount;
        var omittedByMergedLimit = Math.Max(0, revised.Count - displayLimit);
        if (omittedByMergedLimit > 0)
        {
            var selected = revised.FirstOrDefault(item =>
                item.Id.Equals(selectedModelId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
            revised = revised.Take(displayLimit).ToList();
            if (selected is not null
                && revised.All(item => !item.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var replacementIndex = revised.FindLastIndex(item =>
                    !item.Id.Equals(candidate.ConfiguredModel, StringComparison.OrdinalIgnoreCase));
                if (replacementIndex >= 0)
                {
                    revised[replacementIndex] = selected;
                }
            }
        }

        var mergedOmittedCount = candidate.OmittedModelCount + omittedByMergedLimit;
        var mergedLimitStatus = omittedByMergedLimit > 0
            ? $" {omittedByMergedLimit} merged catalog entries were omitted by the {displayLimit}-model display limit."
            : "";

        return candidate with
        {
            CatalogEvidence = omittedByMergedLimit > 0
                && candidate.CatalogEvidence == ProviderCatalogEvidenceState.Ready
                    ? ProviderCatalogEvidenceState.Partial
                    : candidate.CatalogEvidence,
            LoadedModels = revised
                .Where(item => item.LoadState == ProviderModelLoadState.Loaded)
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AvailableModels = revised
                .Where(item => item.LoadState != ProviderModelLoadState.Loaded)
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            OmittedModelCount = mergedOmittedCount,
            Status = $"Provider load-state confirmation is unavailable; showing the last confirmed grouping.{mergedLimitStatus} {candidate.Status}"
        };
    }

    private static void AddIfPresent(ICollection<string> parts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }

    private static string ContextEvidence(
        ProviderModelCatalogItem item,
        ProviderCatalogEvidenceState residencyEvidence,
        int effectiveContext)
    {
        if (item.IsResidencyStale || residencyEvidence == ProviderCatalogEvidenceState.Unavailable)
        {
            return "Effective context is unavailable because current residency evidence is unavailable.";
        }
        if (item.LoadState != ProviderModelLoadState.Loaded)
        {
            return item.MaximumContextLength is int maximum && maximum > 0
                ? $"Provider reported: up to {maximum:N0} advertised context. Active context is unknown while unloaded."
                : "Unknown: active context is not available while the model is unloaded.";
        }
        return effectiveContext > 0
            ? $"Provider reported: {effectiveContext:N0} active context."
            : "Unknown: the provider reports this model loaded but did not report active context.";
    }

    internal static string ConfigurationStatus(
        bool requiresReload,
        int configuredContextWindow,
        string historyPolicy)
    {
        if (requiresReload)
        {
            return "Saved. Reload this loaded model to apply its context window.";
        }

        return configuredContextWindow == 0
               && ModelHistoryPolicies.NormalizeHistoryPolicy(historyPolicy) == ModelHistoryPolicies.Rolling80
            ? "Rolling 80% is saved but inactive because Provider default does not reveal a usable context budget. Set an explicit context window to activate bounded history; provider rejection remains authoritative."
            : "Configuration is active for future Arena calls.";
    }

    internal static string ConfigurationContextEvidence(
        string contextEvidence,
        int configuredContextWindow,
        string historyPolicy) =>
        configuredContextWindow == 0
        && ModelHistoryPolicies.NormalizeHistoryPolicy(historyPolicy) == ModelHistoryPolicies.Rolling80
            ? $"{contextEvidence} Rolling 80% cannot budget history until an explicit configured context window is set."
            : contextEvidence;

    private static ProviderModelHistoryPolicy HistoryPolicyPresentation(string value) =>
        ModelHistoryPolicies.NormalizeHistoryPolicy(value) switch
        {
            ModelHistoryPolicies.Rolling80 => ProviderModelHistoryPolicy.Rolling80Percent,
            ModelHistoryPolicies.Chaptered => ProviderModelHistoryPolicy.Chaptered,
            _ => ProviderModelHistoryPolicy.Strict
        };

    private static string HistoryPolicyWire(ProviderModelHistoryPolicy value) => value switch
    {
        ProviderModelHistoryPolicy.Rolling80Percent => ModelHistoryPolicies.Rolling80,
        ProviderModelHistoryPolicy.Chaptered => ModelHistoryPolicies.Chaptered,
        _ => ModelHistoryPolicies.Strict
    };

    private static ProviderModelResponseTone ResponseTonePresentation(string value) =>
        ModelResponseTones.NormalizeResponseTone(value) switch
        {
            ModelResponseTones.Neutral => ProviderModelResponseTone.Neutral,
            ModelResponseTones.Concise => ProviderModelResponseTone.Concise,
            ModelResponseTones.Analytical => ProviderModelResponseTone.Analytical,
            ModelResponseTones.Creative => ProviderModelResponseTone.Creative,
            ModelResponseTones.Direct => ProviderModelResponseTone.Direct,
            ModelResponseTones.Custom => ProviderModelResponseTone.Custom,
            _ => ProviderModelResponseTone.Default
        };

    private static string ResponseToneWire(ProviderModelResponseTone value) => value switch
    {
        ProviderModelResponseTone.Neutral => ModelResponseTones.Neutral,
        ProviderModelResponseTone.Concise => ModelResponseTones.Concise,
        ProviderModelResponseTone.Analytical => ModelResponseTones.Analytical,
        ProviderModelResponseTone.Creative => ModelResponseTones.Creative,
        ProviderModelResponseTone.Direct => ModelResponseTones.Direct,
        ProviderModelResponseTone.Custom => ModelResponseTones.Custom,
        _ => ModelResponseTones.Default
    };

    private void CancelLifecycleForConnectionChange()
    {
        if (Volatile.Read(ref lifecycleRunning) != 0)
        {
            lifecycleCancellation?.Cancel();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class ProviderLifecycleContextChangedException : InvalidOperationException
    {
    }
}
