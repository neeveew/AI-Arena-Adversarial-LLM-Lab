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
    private readonly ProviderModelCatalogProjectionService catalogProjection = new();
    private readonly LmStudioModelCatalogService lmStudioCatalog;
    private readonly OllamaModelCatalogService ollamaCatalog = new();
    private readonly LlamaCppRuntimeService llamaCppRuntime = new();
    private readonly Dictionary<string, ProviderModelAssignmentProjection> assignmentsByModel =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderModelCatalogItem> catalogItemsByModel =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? lifecycleCancellation;
    private string lifecycleConnectionIdentity = "";
    private long refreshRunGeneration;
    private int refreshInFlight;
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
        LmStudioModelCatalogService? lmStudioCatalog = null)
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
    }

    public Task RefreshAsync(bool refreshCatalog, CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(refreshCatalog, heartbeat: false, cancellationToken);

    public Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref lifecycleRunning) != 0
            || Volatile.Read(ref refreshInFlight) != 0)
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
        if (heartbeat && Volatile.Read(ref refreshInFlight) != 0)
        {
            return;
        }

        var refreshRun = Interlocked.Increment(ref refreshRunGeneration);
        Interlocked.Exchange(ref refreshInFlight, 1);
        CancellationTokenSource? refreshOwner = null;
        try
        {
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
            preserveMissingRows: Volatile.Read(ref lifecycleRunning) != 0);

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

            if (Volatile.Read(ref refreshRunGeneration) == refreshRun)
            {
                Interlocked.Exchange(ref refreshInFlight, 0);
            }
        }
    }

    public async Task SaveAssignmentAsync(
        ProviderModelAssignmentChangedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        control.SetAssignmentState(change.ChangeId, ProviderAssignmentSaveState.Saving, "Saving…");
        if (!assignmentsByModel.TryGetValue(change.ModelId, out var assignment)
            || assignment.Targets.FirstOrDefault(target =>
                target.Id.Equals(change.TargetId, StringComparison.OrdinalIgnoreCase)) is not { } target)
        {
            control.SetAssignmentState(
                change.ChangeId,
                ProviderAssignmentSaveState.Failed,
                "The assignment changed; refresh and try again.");
            return;
        }

        ProviderModelAssignmentControlResult result;
        try
        {
            result = await providerConfiguration.SetModelAssignmentAsync(
                new ProviderModelAssignmentRequest(
                    target.Id,
                    change.ModelId,
                    change.IsAssigned,
                    assignment.ProviderFingerprint,
                    target.AssignmentFingerprint),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            control.SetAssignmentState(
                change.ChangeId,
                ProviderAssignmentSaveState.Failed,
                "Assignment was cancelled.");
            throw;
        }

        control.SetAssignmentState(
            change.ChangeId,
            result.Ok ? ProviderAssignmentSaveState.Saved : ProviderAssignmentSaveState.Failed,
            result.Message);
        await RefreshAsync(
            refreshCatalog: result.Ok && target.IsDefault,
            cancellationToken);
    }

    public async Task RunLifecycleAsync(
        ProviderModelLifecycleRequestedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref lifecycleRunning, 1, 0) != 0)
        {
            control.SetLifecycleState(
                change.OperationId,
                ProviderModelLifecycleActionState.Failed,
                "Another model residency request is already running.");
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
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
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
            result.CheckedAt);
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
            result.CheckedAt);
    }

    private void ApplyPresentation(
        ArenaSnapshot snapshot,
        ProviderModelCatalogSnapshot catalog,
        bool isRefreshing)
    {
        assignmentsByModel.Clear();
        catalogItemsByModel.Clear();
        var shared = SharedConfig(snapshot);
        var lmStudioLifecycle = ModelProviderApiModes.IsLmStudioNative(shared.ApiMode)
            && catalog.ResidencyEvidence != ProviderCatalogEvidenceState.Unavailable;
        var rows = new List<ProviderModelAssignmentPresentation>();
        foreach (var item in catalog.LoadedModels.Concat(catalog.AvailableModels))
        {
            catalogItemsByModel[item.Id] = item;
            var assignment = ProviderModelAssignmentProjectionService.Project(catalog.SessionId, snapshot, item.Id);
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
                LifecycleHelp: item.IsResidencyStale
                    ? "Showing the last confirmed grouping. Refresh before changing LM Studio residency."
                    : item.LoadState == ProviderModelLoadState.Loaded && !item.CanUnload
                        ? "LM Studio reports this model as loaded but did not provide an unloadable instance identifier. Refresh or manage it in LM Studio."
                        : lmStudioLifecycle
                            ? "LM Studio controls model residency. Assignments remain unchanged, and LM Studio chooses hardware placement."
                            : "Load and unload are available only when LM Studio native residency evidence is current.",
                IsResidencyStale: item.IsResidencyStale));
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
        var connectionState = isRefreshing
            ? ProviderConnectionState.Checking
            : catalogReady ? ProviderConnectionState.Online : ProviderConnectionState.Offline;
        control.ApplyPresentation(new ProviderModelAssignmentsPresentation(
            providerName,
            connectionState switch
            {
                ProviderConnectionState.Online => "Online",
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
                IsEnabled: !busy
                    && !(target.IsDefault && target.Assigned)
                    && !(selectedIsDefault && !target.IsDefault && target.InheritsDefault))).ToArray(),
            selected,
            IsRefreshing: isRefreshing,
            CanRefresh: !isRefreshing,
            CanOpenConnectionSettings: true,
            CanClose: true,
            CanAssign: !busy && rows.Count > 0,
            PresentationIdentity: catalog.ProviderFingerprint,
            ConnectionIdentity: ProviderModelCatalogProjectionService.ConnectionFingerprint(catalog.SessionId, shared),
            CanRunLifecycle: !busy && !isRefreshing && lmStudioLifecycle));
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
                ? "This is the current default. Choose another model to change the default."
                : "Make this model the default for targets without an explicit model assignment.";
        }

        if (selectedIsDefault && target.InheritsDefault)
        {
            return "This target already uses this model through Default. Choose another model to create an explicit assignment.";
        }

        return target.Assigned
            ? "Uncheck to return this target to the default model."
            : "Check to assign this model immediately.";
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
            && pair.First.SizeBytes == pair.Second.SizeBytes
            && pair.First.CapabilitySummary.Equals(pair.Second.CapabilitySummary, StringComparison.Ordinal)
            && pair.First.IsConfiguredOnly == pair.Second.IsConfiguredOnly
            && pair.First.IsResidencyStale == pair.Second.IsResidencyStale
            && pair.First.Aliases.SequenceEqual(pair.Second.Aliases, StringComparer.Ordinal));
    }

    private static ProviderModelCatalogSnapshot PreserveLastConfirmedResidency(
        ProviderModelCatalogSnapshot? previous,
        ProviderModelCatalogSnapshot candidate,
        bool preserveMissingRows)
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

        return candidate with
        {
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
            Status = $"LM Studio load-state confirmation is unavailable; showing the last confirmed grouping. {candidate.Status}"
        };
    }

    private static void AddIfPresent(ICollection<string> parts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }

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
