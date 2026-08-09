using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using Microsoft.Win32;

namespace AIArena.Wpf.Services;

internal sealed record ExperimentMatrixPreviewResult(
    ArenaExperimentContract Contract,
    ArenaExperimentExpansion Expansion,
    ImmutableArray<string> PreviewRows);

internal sealed record ExperimentExecutionBinding(
    ArenaExperimentExecutionPlan Plan,
    ArenaExperimentProviderProfileRegistry Profiles,
    ImmutableArray<ArenaExperimentExecutionDiagnostic> Diagnostics);

internal sealed record ExperimentExecutionResolution(
    ExperimentExecutionBinding? Binding,
    ImmutableArray<string> ProviderProfileIds,
    ImmutableArray<ArenaExperimentExecutionDiagnostic> Diagnostics)
{
    public bool IsAvailable => Binding is not null;
}

internal sealed record ExperimentForkCursorItem(
    string MessageId,
    int MessageIndex,
    int Turn,
    string SpeakerId,
    DialogueMessage Message)
{
    public string DisplayText => $"#{MessageIndex + 1} · turn {Turn} · {SpeakerId} · {MessageId}";
    public override string ToString() => MessageId;
}

internal sealed record ExperimentPackItem(
    string Kind,
    string Id,
    string Version,
    IArenaVersionedContract Contract)
{
    public string DisplayText => $"{Kind} · {Id} · v{Version}";
    public override string ToString() => $"{Kind}:{Id}";
}

internal sealed record ExperimentBenchmarkSelection(
    string DisplayText,
    string? BenchmarkPackId,
    string? ScenarioPackId,
    ImmutableArray<string> RubricIds)
{
    public override string ToString() => DisplayText;
}

internal sealed record ExperimentRubricItem(ArenaRubricContract Contract)
{
    public string DisplayText => $"{Contract.Name} · v{Contract.Version} · {Contract.Id}";
    public override string ToString() => Contract.Id;
}

internal sealed record ExperimentLedgerItem(ArenaClaimLedgerContract Contract)
{
    public string DisplayText => $"{Contract.Id} · {Contract.Claims.Length} claim(s)";
    public override string ToString() => Contract.Id;
}

internal sealed record ExperimentClaimItem(ArenaClaim Claim)
{
    public string DisplayText => $"{Claim.Id} · {Claim.Status} · message {Claim.MessageId}";
    public override string ToString() => Claim.Id;
}

internal interface IExperimentLabFileDialogService
{
    string? OpenJson();
    string? SaveJson(string suggestedFileName);
}

internal sealed class ExperimentLabFileDialogService : IExperimentLabFileDialogService
{
    public string? OpenJson()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "AI Arena JSON artifacts (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveJson(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "AI Arena JSON artifacts (*.json)|*.json",
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

/// <summary>
/// One deterministic behavior boundary for the Experiment Lab UI and a future
/// experiment.* control-plane adapter. Matrix execution crosses the same strict
/// Core resolver and isolated-session executor boundary as non-visual callers.
/// </summary>
internal sealed partial class ExperimentLabCoordinator : IDisposable
{
    private sealed class ProviderJudgeActionLifetime
    {
        private int commitStarted;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool CommitStarted => Volatile.Read(ref commitStarted) != 0;
        internal bool TryBeginCommit() => Interlocked.CompareExchange(ref commitStarted, 1, 0) == 0;
    }

    private const int MaximumPreviewRows = 120;
    private const int MaximumHistoryRows = 250;
    private const int MaximumImportedBytes = ArenaExperimentPackCodec.DefaultMaximumPackBytes;
    private readonly ExperimentLabControl control;
    private readonly SessionStore sessionStore;
    private readonly IModelProviderClient providerClient;
    private readonly Func<string?> activeSessionId;
    private readonly Func<string, CancellationToken, Task> loadSession;
    private readonly ArenaExperimentPackStore packStore;
    private readonly ExperimentRunStore runStore;
    private readonly ExperimentDefinitionStore definitionStore;
    private readonly ArenaRubricStore rubricStore;
    private readonly ArenaClaimLedgerStore claimStore;
    private readonly ProviderRequestTraceStore? providerRequestTraces;
    private readonly ArenaRubricService rubricService = new();
    private readonly ArenaClaimLedgerService claimService = new();
    private readonly TimeProvider timeProvider;
    private readonly IExperimentLabFileDialogService dialogs;
    private readonly Func<string, CancellationToken, Task>? featureRefreshOverride;
    private readonly Func<CancellationToken, Task>? providerJudgePreCommitOverride;
    private readonly Func<string, CancellationToken, Task<ImmutableArray<ExperimentForkCursorItem>>>? forkCursorLoadOverride;
    private readonly Dictionary<string, Func<CancellationToken, Task>> registeredFeatureRefreshes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private CancellationTokenSource? featureSelectionRefreshCancellation;
    private Task featureSelectionRefreshTask = Task.CompletedTask;
    private string? featureSelectionRefreshKey;
    private long featureSelectionRefreshGeneration;
    private CancellationTokenSource? matrixRunCancellation;
    private ProviderJudgeActionLifetime? providerJudgeLifetime;
    private ArenaBlindPairwiseSession? pendingBlindSession;
    private ArenaBlindPairwiseFinalization? pendingBlindFinalization;
    private ArenaRubricContract? pendingBlindRubric;
    private ArenaExperimentContract? restoredMatrixDefinition;
    private bool matrixDefinitionRestoreAttempted;
    private string? matrixDefinitionRestoreReceipt;
    private string claimSessionId = "";
    private long claimSessionGeneration;
    private bool disposed;

    public ExperimentLabCoordinator(
        ExperimentLabControl control,
        SessionStore sessionStore,
        IModelProviderClient providerClient,
        Func<string?> activeSessionId,
        Func<string, CancellationToken, Task> loadSession,
        string dataRoot,
        TimeProvider? timeProvider = null,
        IExperimentLabFileDialogService? dialogs = null,
        Func<string, CancellationToken, Task>? featureRefreshOverride = null,
        ProviderRequestTraceStore? providerRequestTraces = null,
        Func<CancellationToken, Task>? providerJudgePreCommitOverride = null,
        Func<string, CancellationToken, Task<ImmutableArray<ExperimentForkCursorItem>>>? forkCursorLoadOverride = null)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        this.activeSessionId = activeSessionId ?? throw new ArgumentNullException(nameof(activeSessionId));
        this.loadSession = loadSession ?? throw new ArgumentNullException(nameof(loadSession));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = Path.Combine(Path.GetFullPath(dataRoot), "experimentation");
        packStore = new(root);
        runStore = new(root);
        definitionStore = new(root);
        rubricStore = new(root);
        claimStore = new(root);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.dialogs = dialogs ?? new ExperimentLabFileDialogService();
        this.featureRefreshOverride = featureRefreshOverride;
        this.providerRequestTraces = providerRequestTraces;
        this.providerJudgePreCommitOverride = providerJudgePreCommitOverride;
        this.forkCursorLoadOverride = forkCursorLoadOverride;
    }

    internal void NotifyActiveSessionChanged(string? sessionId)
    {
        var normalized = sessionId?.Trim() ?? "";
        if (normalized.Equals(claimSessionId, StringComparison.Ordinal))
        {
            return;
        }

        claimSessionId = normalized;
        Interlocked.Increment(ref claimSessionGeneration);
        control.SetClaimMessages([]);
        control.SetClaimLedgers([]);
        control.SetClaims([]);
        control.SetClaimStatus(string.IsNullOrEmpty(normalized)
            ? "Claim provenance is unavailable until a session is loaded."
            : "Session changed; refresh Claims before selecting message provenance.");
    }

    internal async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await RefreshMatrixCoreAsync(cancellationToken).ConfigureAwait(true);
        await RefreshForkCursorsCoreAsync(cancellationToken).ConfigureAwait(true);
        await RefreshPacksCoreAsync(cancellationToken).ConfigureAwait(true);
        await RefreshRubricsCoreAsync(cancellationToken).ConfigureAwait(true);
        await RefreshClaimsCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    internal Task RefreshFeatureAsync(string key, CancellationToken cancellationToken = default)
    {
        if (featureRefreshOverride is not null)
        {
            return featureRefreshOverride(key, cancellationToken);
        }

        Task? builtIn = key switch
        {
            "matrix" => RefreshMatrixCoreAsync(cancellationToken),
            "fork" => RefreshForkCursorsCoreAsync(cancellationToken),
            "packs" => RefreshPacksCoreAsync(cancellationToken),
            "rubrics" => RefreshRubricsCoreAsync(cancellationToken),
            "claims" => RefreshClaimsCoreAsync(cancellationToken),
            _ => null
        };
        return builtIn
            ?? (registeredFeatureRefreshes.TryGetValue(key, out var refresh)
                ? refresh(cancellationToken)
                : Task.CompletedTask);
    }

    internal void RegisterFeatureRefresh(string key, Func<CancellationToken, Task> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(refresh);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!registeredFeatureRefreshes.TryAdd(key, refresh))
        {
            throw new InvalidOperationException($"Experiment Lab feature refresh '{key}' is already registered.");
        }
    }

    internal IRoutingEvidenceSource CreateRoutingEvidenceSource() =>
        new PersistedRoutingEvidenceSource(runStore, rubricStore, packStore, BuildRoutingEvidenceContextAsync, timeProvider);

    private async Task<PersistedRoutingEvidenceContext?> BuildRoutingEvidenceContextAsync(
        CancellationToken cancellationToken)
    {
        ExperimentMatrixPreviewResult preview;
        try
        {
            // Capture every visual value before the first await. The evidence
            // source can resume off the dispatcher, but it never reaches back
            // into the visual tree after this point.
            preview = BuildMatrixPreview(control.ReadMatrixInput(), UtcNow());
        }
        catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException)
        {
            return null;
        }

        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var resolution = await ResolveMatrixExecutionAsync(preview.Contract, cancellationToken).ConfigureAwait(false);
        if (resolution.Binding is not { } binding)
        {
            return null;
        }

        // Reload after resolution and require the exact revision named by the
        // immutable plan. A concurrent setup edit therefore makes routing
        // evidence unavailable instead of mixing two session generations.
        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.PersistenceRevision != binding.Plan.SourcePersistenceRevision)
        {
            return null;
        }

        ArenaExperimentProviderProfileRegistry profiles;
        try
        {
            profiles = BuildProviderProfileRegistry(snapshot);
        }
        catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException)
        {
            return null;
        }

        var currentProfiles = new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Configs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            currentProfiles.Add(ProviderProfileId(pair.Key, currentProfiles.Keys), pair.Value);
        }

        var providerModels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profileId in binding.Plan.ProviderProfileIds)
        {
            if (!profiles.Contains(profileId)
                || !currentProfiles.TryGetValue(profileId, out var profile)
                || string.IsNullOrWhiteSpace(profile.Model))
            {
                return null;
            }
            providerModels.Add(profileId, profile.Model.Trim());
        }

        var route = snapshot.Engine.Agents
            .Where(agent => agent.Active && !string.IsNullOrWhiteSpace(agent.Id))
            .OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .Concat(snapshot.Engine.Agents
                .Where(agent => !agent.Active && !string.IsNullOrWhiteSpace(agent.Id))
                .OrderBy(agent => agent.Id, StringComparer.Ordinal))
            .Select(agent => (Agent: agent, Config: ModelProviderRouting.Resolve(snapshot, agent.Id, out _)))
            .FirstOrDefault(item => item.Config is { Model: { Length: > 0 } });
        if (route.Agent is null || route.Config is null)
        {
            return null;
        }

        // Hardware and task constraints are deliberately absent until an
        // observed constraint source exists. Empty evidence is preferable to
        // a plausible but invented compatibility claim.
        var constraints = providerModels.Values
            .Append(route.Config.Model)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                model => model,
                _ => (IReadOnlyList<ArenaRouteConstraintEvidence>)Array.Empty<ArenaRouteConstraintEvidence>(),
                StringComparer.Ordinal);
        return new(
            binding.Plan.Experiment,
            binding.Plan.SourceSetupFingerprint,
            binding.Plan.PlanFingerprint,
            binding.Plan.ScenarioId,
            route.Agent.Id,
            route.Config.Model,
            providerModels,
            constraints);
    }

    internal Task DebugFeatureSelectionRefreshTask => Volatile.Read(ref featureSelectionRefreshTask);

    internal void RequestFeatureSelectionRefresh(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (disposed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref featureSelectionRefreshGeneration);
        var previousKey = Interlocked.Exchange(ref featureSelectionRefreshKey, key);
        if (!string.IsNullOrWhiteSpace(previousKey)
            && !string.Equals(previousKey, key, StringComparison.Ordinal))
        {
            control.SetFeatureRefreshSummary(previousKey, "superseded");
        }
        control.SetFeatureRefreshSummary(key, "refreshing");
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref featureSelectionRefreshCancellation, cancellation);
        CancelSafely(previous);
        var refresh = RefreshFeatureSelectionSafelyAsync(key, generation, cancellation);
        Interlocked.Exchange(ref featureSelectionRefreshTask, refresh);
    }

    internal static ExperimentMatrixPreviewResult BuildMatrixPreview(ExperimentMatrixInput input, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        var title = RequiredText(input.Title, "Matrix title", 120);
        var scenarioPackId = RequiredId(input.ScenarioPackId, "Scenario pack ID");
        var benchmarkPackId = OptionalId(input.BenchmarkPackId, "Benchmark pack ID");
        var providers = ParseReferences(input.ProviderProfileIds, "Provider profile IDs", 32);
        var rubricIds = ParseOptionalReferences(input.RubricIds, "Rubric IDs", 64);
        var dimensions = ParseDimensions(input.DimensionParameter, input.DimensionValues);
        var repetitions = ParseBoundedInt(input.Repetitions, "Repetitions", 1, 100);
        var turnBudget = ParseBoundedInt(input.TurnBudget, "Turn budget", 1, 100);
        var maxParallelism = ParseBoundedInt(input.MaxParallelism, "Parallelism", 1, 32);
        RequireUtc(createdAtUtc);
        var dimensionIdentity = string.Join(
            ";",
            dimensions.Select(dimension => $"{dimension.Parameter}={string.Join(',', dimension.Values)}"));
        var identity = string.Join("\n", title, scenarioPackId, benchmarkPackId, string.Join(',', providers), string.Join(',', rubricIds), dimensionIdentity, repetitions, turnBudget, maxParallelism);
        var contract = new ArenaExperimentContract(
            ArenaContractSchemas.Experiment,
            StableId("experiment", identity),
            createdAtUtc,
            title,
            ArenaExperimentStatus.Draft,
            scenarioPackId,
            benchmarkPackId,
            providers,
            rubricIds,
            [],
            dimensions,
            repetitions,
            turnBudget,
            maxParallelism,
            [],
            []);
        var validation = ArenaContractCodec.Validate(contract);
        if (!validation.IsValid)
        {
            throw new ExperimentLabInputException(DiagnosticSummary(validation.Issues.Select(item => item.Code), "Matrix contract is invalid"));
        }

        return BuildMatrixPreview(contract);
    }

    internal static ExperimentMatrixPreviewResult BuildMatrixPreview(ArenaExperimentContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var validation = ArenaContractCodec.Validate(contract);
        if (!validation.IsValid)
        {
            throw new ExperimentLabInputException(DiagnosticSummary(validation.Issues.Select(item => item.Code), "Matrix contract is invalid"));
        }

        var expansion = ExperimentExpander.Expand(contract);
        var rows = expansion.Cells.Take(MaximumPreviewRows).Select(cell =>
        {
            var dimensions = string.Join(", ", cell.Values.Select(value => $"{value.Parameter}={value.Value}"));
            return $"{cell.RunId} · {cell.ProviderProfileId} · {dimensions} · repetition {cell.Repetition + 1}";
        }).ToImmutableArray();
        if (expansion.Cells.Length > rows.Length)
        {
            rows = rows.Add($"… {expansion.Cells.Length - rows.Length} additional cell(s) omitted from this bounded preview.");
        }

        return new(contract, expansion, rows);
    }

    internal Task<ArenaExperimentRunLoadResult> ListRunHistoryAsync(CancellationToken cancellationToken = default) =>
        runStore.LoadAllAsync(cancellationToken);

    internal Task<ArenaExperimentDefinitionLoadResult> ListExperimentDefinitionsAsync(CancellationToken cancellationToken = default) =>
        definitionStore.LoadAllAsync(cancellationToken);

    internal static ArenaExperimentProviderProfileRegistry BuildProviderProfileRegistry(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Configs.Count > ArenaExperimentProviderProfileRegistry.MaximumProfiles)
        {
            throw new ExperimentLabInputException(
                $"The active session contains more than {ArenaExperimentProviderProfileRegistry.MaximumProfiles} provider profiles; execution is unavailable.");
        }

        var profiles = new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Configs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var profileId = ProviderProfileId(pair.Key, profiles.Keys);
            profiles.Add(profileId, pair.Value);
        }
        return new ArenaExperimentProviderProfileRegistry(profiles);
    }

    internal async Task<ExperimentExecutionResolution> ResolveMatrixExecutionAsync(
        ArenaExperimentContract contract,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UnavailableExecution(
                "active_session_unavailable",
                "Load a persisted session before resolving provider profiles.");
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return UnavailableExecution(
                "active_session_unavailable",
                "The active persisted session is unavailable.");
        }

        ArenaExperimentProviderProfileRegistry profiles;
        try
        {
            profiles = BuildProviderProfileRegistry(snapshot);
        }
        catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException)
        {
            return UnavailableExecution(
                "provider_registry_invalid",
                "The active session provider profiles cannot form a bounded execution registry.");
        }

        var resolver = new ArenaExperimentExecutionResolver(packStore, sessionStore, profiles, rubricStore: rubricStore);
        var resolved = await resolver.ResolveAsync(contract, cancellationToken: cancellationToken).ConfigureAwait(false);
        var binding = resolved.IsAvailable && resolved.Plan is not null
            ? new ExperimentExecutionBinding(resolved.Plan, profiles, resolved.Diagnostics)
            : null;
        return new(binding, profiles.ProfileIds, resolved.Diagnostics);
    }

    internal async Task ReconcileMatrixSourcesAsync(CancellationToken cancellationToken = default)
    {
        // Once a durable definition has been restored, its exact references are
        // authoritative. Refreshing source registries may make execution
        // unavailable, but must never rewrite the matrix into a different
        // experiment before its lifecycle/retry decision is made.
        var preserveDurableInput = restoredMatrixDefinition is not null;
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            control.ReconcileMatrixProviderProfiles([], preserveDurableInput);
        }
        else
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(true);
            if (snapshot is null)
            {
                control.ReconcileMatrixProviderProfiles([], preserveDurableInput);
            }
            else
            {
                try
                {
                    control.ReconcileMatrixProviderProfiles(
                        BuildProviderProfileRegistry(snapshot).ProfileIds,
                        preserveDurableInput);
                }
                catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException)
                {
                    control.ReconcileMatrixProviderProfiles([], preserveDurableInput);
                }
            }
        }

        var scenarios = await ListScenarioPacksAsync(cancellationToken).ConfigureAwait(true);
        var benchmarks = await ListBenchmarkPacksAsync(cancellationToken).ConfigureAwait(true);
        var rubrics = await ListRubricsAsync(cancellationToken).ConfigureAwait(true);
        ReconcileMatrixPacks(scenarios.Artifacts, benchmarks.Artifacts, preserveDurableInput);
        control.ReconcileMatrixRubricIds(
            [.. rubrics.Artifacts.Select(item => item.Id)],
            preserveDurableInput);
    }

    internal async Task<ImmutableArray<ExperimentForkCursorItem>> ListForkCursorsAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionId()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        return await LoadForkCursorsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<ExperimentForkCursorItem>> LoadForkCursorsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (forkCursorLoadOverride is not null)
        {
            return await forkCursorLoadOverride(sessionId, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return [];
        }

        return [.. snapshot.Engine.Messages.Select((message, index) => new ExperimentForkCursorItem(
            DialogueMessageIdentity.Resolve(message, index),
            index,
            Math.Max(0, message.Turn),
            SafeSpeakerId(message),
            message))];
    }

    internal async Task<SessionForkResult> ForkAndLoadAsync(
        string cursorMessageId,
        string? targetSessionId,
        CancellationToken cancellationToken = default)
    {
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ExperimentLabInputException("Load a session before creating a historical fork.");
        }

        var target = string.IsNullOrWhiteSpace(targetSessionId)
            ? null
            : RequiredId(targetSessionId, "Child session ID");
        var result = await sessionStore.ForkSessionAtCursorAsync(
            sessionId,
            RequiredId(cursorMessageId, "Transcript cursor"),
            target,
            cancellationToken).ConfigureAwait(false);
        await loadSession(result.TargetSessionId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal Task<ArenaArtifactLoadResult<ArenaScenarioPackContract>> ListScenarioPacksAsync(CancellationToken cancellationToken = default) =>
        packStore.LoadScenarioPacksAsync(cancellationToken);

    internal Task<ArenaArtifactLoadResult<ArenaBenchmarkPackContract>> ListBenchmarkPacksAsync(CancellationToken cancellationToken = default) =>
        packStore.LoadBenchmarkPacksAsync(cancellationToken);

    internal async Task<ArenaScenarioPackContract> CreateScenarioPackAsync(
        ExperimentPackInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ExperimentLabInputException("Load and persist a session before creating a replayable scenario pack.");
        }
        var safeSessionId = RequiredId(sessionId, "Active session ID");
        var resolver = new ArenaExperimentExecutionResolver(
            packStore,
            sessionStore,
            new ArenaExperimentProviderProfileRegistry(new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal)));
        var sourceResolution = await resolver.ResolveSessionSourceAsync($"session:{safeSessionId}", cancellationToken).ConfigureAwait(false);
        if (!sourceResolution.IsAvailable || sourceResolution.Source is null)
        {
            throw new ExperimentLabInputException(DiagnosticSummary(
                sourceResolution.Diagnostics.Select(item => item.Code),
                "Active session setup evidence is unavailable"));
        }
        return CreateScenarioPack(input, sourceResolution.Source);
    }

    internal ArenaScenarioPackContract CreateScenarioPack(
        ExperimentPackInput input,
        ArenaExperimentSessionSource source)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        var name = RequiredText(input.Name, "Pack name", 120);
        var version = RequiredReference(input.Version, "Pack version", 32);
        var turnBudget = ParseBoundedInt(input.TurnBudget, "Turn budget", 1, 100);
        var created = UtcNow();
        var packId = StableId("scenario-pack", $"{name}\n{version}\n{turnBudget}");
        var invariantId = StableId("invariant", $"{packId}\nturn-budget");
        var scenarioId = StableId("scenario", $"{packId}\nlocal");
        ImmutableArray<ArenaScenarioInvariant> invariants =
        [new(invariantId, "rule:turn-budget", "Configured turn budget remains bounded.", true)];
        ImmutableArray<ArenaScenarioDefinition> scenarios =
        [new(
            scenarioId,
            version,
            name,
            source.MatchSetupReference,
            source.SetupFingerprint,
            "seed:local",
            turnBudget,
            ["local"],
            [invariantId],
            [])];
        var contract = new ArenaScenarioPackContract(
            ArenaContractSchemas.ScenarioPack,
            packId,
            created,
            name,
            version,
            ArenaExperimentFingerprints.ScenarioPackContent(invariants, scenarios),
            null,
            invariants,
            scenarios,
            []);
        RequireValid(contract, "Scenario pack");
        return contract;
    }

    internal ArenaBenchmarkPackContract CreateBenchmarkPack(
        ExperimentPackInput input,
        ArenaScenarioPackContract scenarioPack,
        ImmutableArray<string> rubricIds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scenarioPack);
        var name = RequiredText(input.Name, "Pack name", 120);
        var version = RequiredReference(input.Version, "Pack version", 32);
        if (rubricIds.IsDefaultOrEmpty)
        {
            throw new ExperimentLabInputException("Create and persist at least one rubric before creating a benchmark pack.");
        }
        var rubrics = rubricIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var packId = StableId("benchmark-pack", $"{name}\n{version}\n{scenarioPack.Id}\n{string.Join(',', rubrics)}");
        ImmutableArray<ArenaBenchmarkCase> cases = [new(
            StableId("case", $"{packId}\n{scenarioPack.Scenarios[0].Id}"),
            scenarioPack.Scenarios[0].Id,
            rubrics,
            1,
            [],
            [])];
        var contract = new ArenaBenchmarkPackContract(
            ArenaContractSchemas.BenchmarkPack,
            packId,
            UtcNow(),
            name,
            version,
            ArenaExperimentFingerprints.BenchmarkPackContent(scenarioPack.Id, cases),
            null,
            scenarioPack.Id,
            cases,
            []);
        RequireValid(contract, "Benchmark pack");
        return contract;
    }

    internal async Task<ArenaArtifactWriteResult<ArenaScenarioPackContract>> SaveScenarioPackAsync(
        ArenaScenarioPackContract pack,
        CancellationToken cancellationToken = default) =>
        await packStore.SaveScenarioPackAsync(pack, cancellationToken).ConfigureAwait(false);

    internal async Task<ArenaArtifactWriteResult<ArenaBenchmarkPackContract>> SaveBenchmarkPackAsync(
        ArenaBenchmarkPackContract pack,
        CancellationToken cancellationToken = default) =>
        await packStore.SaveBenchmarkPackAsync(pack, cancellationToken).ConfigureAwait(false);

    internal ArenaRubricContract CreateRubric(ExperimentRubricInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = RequiredText(input.Name, "Rubric name", 120);
        var version = RequiredReference(input.Version, "Rubric version", 32);
        var criterion = RequiredText(input.Criterion, "Criterion", 120);
        var id = StableId("rubric", $"{name}\n{version}\n{criterion}");
        var contract = new ArenaRubricContract(
            ArenaContractSchemas.Rubric,
            id,
            UtcNow(),
            name,
            version,
            [
                new("evaluator:blind", ArenaRubricEvaluatorKind.BlindPairwise, null),
                new("evaluator:deterministic", ArenaRubricEvaluatorKind.Deterministic, "profile:deterministic-v1"),
                new("evaluator:human", ArenaRubricEvaluatorKind.Human, null),
                new("evaluator:model", ArenaRubricEvaluatorKind.ModelJudge, "profile:model-judge-v1")
            ],
            [new("criterion:primary", criterion, "Operator-defined bounded evaluation criterion.", 1m, 0m, 5m)],
            []);
        RequireValid(contract, "Rubric");
        return contract;
    }

    internal Task<ArenaArtifactWriteResult<ArenaRubricContract>> SaveRubricContractAsync(
        ArenaRubricContract rubric,
        CancellationToken cancellationToken = default) =>
        rubricStore.SaveRubricAsync(rubric, cancellationToken);

    internal Task<ArenaArtifactLoadResult<ArenaRubricContract>> ListRubricsAsync(CancellationToken cancellationToken = default) =>
        rubricStore.LoadRubricsAsync(cancellationToken);

    internal Task<ArenaArtifactLoadResult<ArenaRubricEvaluationResultContract>> ListRubricResultsAsync(CancellationToken cancellationToken = default) =>
        rubricStore.LoadResultsAsync(cancellationToken);

    internal ArenaRubricEvaluationResultContract CreateRubricObservation(
        ArenaRubricContract rubric,
        ExperimentRubricObservationInput input)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(input);
        var source = ParseRubricSource(input.Source);
        var subjectA = RequiredId(input.SubjectA, "Subject A reference");
        var subjectB = input.Pairwise ? RequiredId(input.SubjectB, "Subject B reference") : null;
        var unavailable = input.Unavailable || input.Preference.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);
        decimal? scoreA = unavailable ? null : ParseBoundedDecimal(input.Score, "Score A", 0m, 5m);
        decimal? scoreB = unavailable || !input.Pairwise ? null : ParseBoundedDecimal(input.ScoreB, "Score B", 0m, 5m);
        if (input.Pairwise)
        {
            throw new ExperimentLabInputException("Blind pairwise judgment requires a two-step concealed judge view. This single-step form will not claim the subjects were hidden.");
        }
        if (source == ArenaRubricJudgmentSource.Deterministic && !unavailable)
        {
            throw new ExperimentLabInputException("Typed scores are human observations. Deterministic scores require a measured diagnostic artifact; mark evidence unavailable until one is supplied.");
        }
        if (source == ArenaRubricJudgmentSource.ModelJudge && !unavailable)
        {
            throw new ExperimentLabInputException("Model-judge scores require an actual provider judgment artifact. This form can only record that model-judge evidence is unavailable.");
        }

        var created = UtcNow();
        var evidenceSuffix = StableHash($"{rubric.Id}\n{source}\n{subjectA}\n{subjectB}\n{created.Ticks}")[..20];
        var criterionEvidence = EvidenceFor(source, unavailable, $"evidence:criterion-{evidenceSuffix}", subjectA);
        var provenance = EvidenceFor(source, unavailable, $"provenance:result-{evidenceSuffix}", subjectA);

        var evaluatorId = source switch
        {
            ArenaRubricJudgmentSource.Human => "evaluator:human",
            ArenaRubricJudgmentSource.Deterministic => "evaluator:deterministic",
            ArenaRubricJudgmentSource.ModelJudge => "evaluator:model",
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        var submission = new ArenaRubricJudgmentSubmission(
            StableId("result", $"{rubric.Id}\n{subjectA}\n{source}\n{created.Ticks}"),
            evaluatorId,
            source,
            source == ArenaRubricJudgmentSource.Human ? "reviewer:local" : null,
            null,
            [new("criterion:primary", scoreA, null, criterionEvidence)],
            provenance);
        return rubricService.CreateSingleSubjectResult(
            rubric,
            StableId("evaluation", $"{rubric.Id}\n{subjectA}\n{source}\n{created.Ticks}"),
            subjectA,
            [submission],
            created,
            created.AddTicks(1),
            []);
    }

    internal Task<ArenaArtifactWriteResult<ArenaRubricEvaluationResultContract>> SaveRubricResultAsync(
        ArenaRubricEvaluationResultContract result,
        CancellationToken cancellationToken = default) =>
        rubricStore.SaveResultAsync(result, cancellationToken);

    internal async Task<ArenaModelJudgeFinalization> RunModelJudgeAsync(
        ArenaRubricContract rubric,
        string subjectReferenceId,
        CancellationToken cancellationToken = default)
    {
        var finalization = await CreateModelJudgeFinalizationAsync(rubric, subjectReferenceId, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        await PersistModelJudgeFinalizationAsync(finalization).ConfigureAwait(true);
        return finalization;
    }

    private async Task<ArenaModelJudgeFinalization> CreateModelJudgeFinalizationAsync(
        ArenaRubricContract rubric,
        string subjectReferenceId,
        CancellationToken cancellationToken)
    {
        if (providerRequestTraces is null)
            throw new ExperimentLabInputException("Provider request tracing is not attached to Judge Studio; no model score was recorded.");
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ExperimentLabInputException("Open a session before running a model judge.");
        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(true)
            ?? throw new ExperimentLabInputException("The active session snapshot is unavailable.");
        var message = snapshot.Engine.Messages.SingleOrDefault(item => item.MessageId == subjectReferenceId)
            ?? throw new ExperimentLabInputException("Model judging requires Subject A to be an exact stable transcript message ID in the active session.");
        var provider = ModelProviderRouting.Resolve(snapshot, message.SpeakerId, out _);
        if (provider is null)
            throw new ExperimentLabInputException("The judged message speaker has no effective configured provider.");
        var evaluator = rubric.Evaluators.SingleOrDefault(item => item.Kind == ArenaRubricEvaluatorKind.ModelJudge)
            ?? throw new ExperimentLabInputException("The selected rubric has no eligible model-judge evaluator.");
        var created = UtcNow();
        var evaluationId = StableId("evaluation", $"{rubric.Id}\n{subjectReferenceId}\n{evaluator.Id}\n{created.Ticks}");
        var judge = new ArenaModelJudgeService(providerClient, providerRequestTraces, timeProvider);
        return await judge.JudgeSingleSubjectAsync(
            rubric,
            evaluator.Id,
            evaluationId,
            subjectReferenceId,
            message.Text,
            provider,
            cancellationToken).ConfigureAwait(true);
    }

    private async Task PersistModelJudgeFinalizationAsync(ArenaModelJudgeFinalization finalization)
    {
        // Once a provider completion has yielded an immutable finalization, finish
        // the bounded local receipt-first write without cancellation.
        var receiptWrite = await rubricStore.SaveModelJudgeReceiptAsync(finalization.Receipt, CancellationToken.None).ConfigureAwait(true);
        if (!receiptWrite.Succeeded)
            throw new InvalidDataException("The model completed, but its immutable provider-attempt receipt could not be saved; no score was persisted.");
        var resultWrite = await rubricStore.SaveResultAsync(finalization.Result, CancellationToken.None).ConfigureAwait(true);
        if (!resultWrite.Succeeded)
            throw new InvalidDataException("The provider receipt was saved, but the bound rubric result failed cross-artifact validation.");
    }

    internal async Task<ArenaBlindPairwiseCommitmentContract> BeginBlindPairwiseAsync(
        ArenaRubricContract rubric,
        string firstSubjectReferenceId,
        string secondSubjectReferenceId,
        CancellationToken cancellationToken = default)
    {
        if (pendingBlindSession is not null)
            throw new ExperimentLabInputException("Finish or cancel the current concealed comparison before beginning another.");
        if (rubric.Criteria.Length != 1)
            throw new ExperimentLabInputException("This bounded Judge Studio flow requires a rubric version with exactly one criterion.");
        var firstId = RequiredId(firstSubjectReferenceId, "Subject A reference");
        var secondId = RequiredId(secondSubjectReferenceId, "Subject B reference");
        if (firstId == secondId)
            throw new ExperimentLabInputException("Blind comparison subjects must differ.");
        var sessionId = activeSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ExperimentLabInputException("Open a session before beginning a blind comparison.");
        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(true)
            ?? throw new ExperimentLabInputException("The active session snapshot is unavailable.");
        static DialogueMessage ResolveMessage(ArenaSnapshot snapshot, string messageId)
        {
            var matches = snapshot.Engine.Messages.Where(item => item.MessageId == messageId).Take(2).ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new ExperimentLabInputException("Each blind subject must resolve to exactly one stable transcript message in the active session.");
        }
        var firstMessage = ResolveMessage(snapshot, firstId);
        var secondMessage = ResolveMessage(snapshot, secondId);
        var seed = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var created = UtcNow();
        var evaluationId = StableId("evaluation", $"{rubric.Id}\n{firstId}\n{secondId}\n{seed}");
        var session = rubricService.BeginBlindPairwise(
            rubric,
            "evaluator:blind",
            evaluationId,
            firstId,
            secondId,
            seed,
            created);
        var write = await rubricStore.SaveBlindCommitmentAsync(session.Commitment, cancellationToken).ConfigureAwait(true);
        if (!write.Succeeded)
            throw new InvalidDataException("The concealed mapping commitment could not be persisted; judgment remains disabled.");
        var judgeView = session.CreateJudgeView(firstId, firstMessage.Text, secondId, secondMessage.Text);
        pendingBlindSession = session;
        pendingBlindRubric = rubric;
        pendingBlindFinalization = null;
        control.ShowBlindJudgeView(judgeView);
        control.SetRubricStatus($"Concealed A/B commitment saved · {session.Commitment.Id}. Judge the displayed content; identities remain hidden until submit succeeds.");
        return session.Commitment;
    }

    internal async Task<ArenaBlindPairwiseFinalization> SubmitBlindPairwiseAsync(
        string scoreAValue,
        string scoreBValue,
        string preferenceValue,
        bool unavailable,
        CancellationToken cancellationToken = default)
    {
        var session = pendingBlindSession
            ?? throw new ExperimentLabInputException("Begin and persist a concealed comparison before submitting a judgment.");
        var rubric = pendingBlindRubric
            ?? throw new InvalidOperationException("The pending blind rubric is unavailable.");
        var criterion = rubric.Criteria.Single();
        if (pendingBlindFinalization is null)
        {
            var preference = unavailable || preferenceValue.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? ArenaPairwisePreference.Unavailable
                : ParsePreference(preferenceValue);
            var scoresUnavailable = unavailable || preference == ArenaPairwisePreference.Unavailable;
            decimal? scoreA = scoresUnavailable ? null : ParseBoundedDecimal(scoreAValue, "Opaque subject A score", criterion.MinimumScore, criterion.MaximumScore);
            decimal? scoreB = scoresUnavailable ? null : ParseBoundedDecimal(scoreBValue, "Opaque subject B score", criterion.MinimumScore, criterion.MaximumScore);
            var observedAt = UtcNow();
            var resultId = StableId("result", $"{session.View.EvaluationId}\nevaluator:blind");
            ArenaEvidenceAssertion CriterionEvidence() => scoresUnavailable
                ? new(
                    StableId("evidence", $"{resultId}\n{criterion.Id}"),
                    ArenaEvidenceState.Unavailable,
                    "The blind reviewer supplied no criterion score.",
                    Limitation: "Reviewer marked the concealed comparison unavailable.")
                : new(
                    StableId("evidence", $"{resultId}\n{criterion.Id}"),
                    ArenaEvidenceState.Observed,
                    "A local reviewer scored the persisted concealed A/B view.",
                    session.Commitment.Id);
            var provenance = scoresUnavailable
                ? new ArenaEvidenceAssertion(
                    StableId("provenance", resultId),
                    ArenaEvidenceState.Unavailable,
                    "Blind reviewer provenance was unavailable.",
                    Limitation: "Reviewer submitted an unavailable judgment.")
                : new ArenaEvidenceAssertion(
                    StableId("provenance", resultId),
                    ArenaEvidenceState.Observed,
                    "A local reviewer submitted the persisted concealed A/B view.",
                    session.Commitment.Id);
            pendingBlindFinalization = session.FinalizeWithReceipt(
                new ArenaRubricJudgmentSubmission(
                    resultId,
                    "evaluator:blind",
                    ArenaRubricJudgmentSource.Human,
                    "reviewer:local",
                    preference,
                    [new(criterion.Id, scoreA, scoreB, CriterionEvidence())],
                    provenance),
                observedAt,
                []);
        }

        var finalization = pendingBlindFinalization;
        var receiptWrite = await rubricStore.SaveBlindReceiptAsync(finalization.Receipt, cancellationToken).ConfigureAwait(true);
        if (!receiptWrite.Succeeded)
            throw new InvalidDataException("The blind judgment receipt could not be persisted. The concealed view remains open for a safe retry.");
        var resultWrite = await rubricStore.SaveResultAsync(finalization.Result, cancellationToken).ConfigureAwait(true);
        if (!resultWrite.Succeeded)
            throw new InvalidDataException("The blind receipt was saved, but its exact result failed validation. Submit again to retry the same immutable result.");
        var reveal = finalization.Result.BlindReveal!;
        pendingBlindSession = null;
        pendingBlindFinalization = null;
        pendingBlindRubric = null;
        control.ResetBlindJudgeView();
        control.SetRubricStatus($"Blind result saved · A = {reveal.LabelAReferenceId} · B = {reveal.LabelBReferenceId} · receipt {finalization.Receipt.Id}.");
        return finalization;
    }

    internal void CancelBlindPairwise()
    {
        if (pendingBlindFinalization is not null)
        {
            control.SetRubricStatus("The judgment was already committed in memory. Submit again to persist the same receipt and result; cancellation cannot rewrite it.");
            return;
        }
        var hadPending = pendingBlindSession is not null;
        pendingBlindSession = null;
        pendingBlindRubric = null;
        control.ResetBlindJudgeView();
        control.SetRubricStatus(hadPending
            ? "Concealed comparison cancelled. Its pre-judgment commitment remains as an unclaimed audit artifact; no result was recorded."
            : "No concealed comparison is pending.");
    }

    internal Task<ArenaArtifactLoadResult<ArenaClaimLedgerContract>> ListClaimLedgersAsync(CancellationToken cancellationToken = default) =>
        claimStore.LoadAllAsync(cancellationToken);

    internal ArenaClaimLedgerContract CreateClaimLedger(ExperimentClaimLedgerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var experimentId = RequiredId(input.ExperimentId, "Experiment ID");
        var branchId = RequiredId(input.BranchId, "Branch ID");
        var created = UtcNow();
        return claimService.Create(
            StableId("ledger", $"{experimentId}\n{branchId}\n{created.Ticks}"),
            experimentId,
            branchId,
            created);
    }

    internal Task<ArenaArtifactWriteResult<ArenaClaimLedgerContract>> SaveClaimLedgerAsync(
        ArenaClaimLedgerContract ledger,
        CancellationToken cancellationToken = default) =>
        claimStore.SaveAsync(ledger, cancellationToken);

    internal ArenaClaimLedgerContract AddClaim(
        ArenaClaimLedgerContract ledger,
        ExperimentForkCursorItem message,
        string summary)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(message);
        var boundedSummary = RequiredText(summary, "Claim summary", 1024);
        var suffix = StableHash($"{ledger.Id}\n{message.MessageId}\n{boundedSummary}")[..20];
        var source = ArenaClaimLedgerService.ToolEvidence(
            $"evidence:message-{suffix}",
            message.MessageId,
            "Operator linked a stable transcript message reference.");
        return claimService.AddClaim(
            ledger,
            message.Message,
            message.SpeakerId,
            boundedSummary,
            0.5m,
            new($"provenance:claim-{suffix}", ArenaEvidenceState.Observed, "Operator entered a bounded claim summary.", message.MessageId),
            [source],
            message.MessageIndex);
    }

    internal ArenaClaimLedgerContract ReviewClaim(
        ArenaClaimLedgerContract ledger,
        string claimId,
        ArenaClaimStatus status)
    {
        var suffix = StableHash($"{ledger.Id}\n{claimId}\n{status}\n{UtcNow().Ticks}")[..20];
        return claimService.ReviewClaim(
            ledger,
            claimId,
            status,
            "reviewer:local",
            new($"review:{suffix}", ArenaEvidenceState.Observed, "Operator recorded a claim review decision.", claimId));
    }

    internal ArenaClaimLedgerContract LinkContradiction(
        ArenaClaimLedgerContract ledger,
        string firstClaimId,
        string secondClaimId)
    {
        var suffix = StableHash($"{ledger.Id}\n{firstClaimId}\n{secondClaimId}\n{UtcNow().Ticks}")[..20];
        return claimService.LinkContradiction(
            ledger,
            firstClaimId,
            secondClaimId,
            "reviewer:local",
            new($"review:{suffix}", ArenaEvidenceState.Observed, "Operator linked contradictory claim references.", firstClaimId));
    }

    private ExperimentMatrixPreviewResult BuildCurrentMatrixPreview()
    {
        var generated = BuildMatrixPreview(control.ReadMatrixInput(), UtcNow());
        if (restoredMatrixDefinition is not null
            && string.Equals(restoredMatrixDefinition.Id, generated.Contract.Id, StringComparison.Ordinal)
            && string.Equals(
                ArenaExperimentFingerprints.Experiment(restoredMatrixDefinition),
                generated.Expansion.ExperimentFingerprint,
                StringComparison.Ordinal))
        {
            return BuildMatrixPreview(restoredMatrixDefinition);
        }
        return generated;
    }

    private async Task EnsureMatrixDefinitionRestoredAsync(CancellationToken cancellationToken)
    {
        if (matrixDefinitionRestoreAttempted)
        {
            return;
        }

        ArenaExperimentDefinitionLoadResult loaded;
        var ownerActive = false;
        try
        {
            await using var executionLease = await definitionStore.AcquireExecutionLeaseAsync(cancellationToken).ConfigureAwait(true);
            loaded = await definitionStore.RecoverInterruptedAfterRestartAsync(
                executionLease,
                UtcNow(),
                cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            loaded = await definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(true);
            ownerActive = true;
        }

        // An active owner is only a passive observation. It must not suppress a
        // later owner-held recovery after that process exits.
        matrixDefinitionRestoreAttempted = !ownerActive;
        ApplyLoadedMatrixDefinition(
            loaded,
            selectDefinition: restoredMatrixDefinition is null || MatrixInputMatches(restoredMatrixDefinition),
            ownerActive);
    }

    private void ApplyLoadedMatrixDefinition(
        ArenaExperimentDefinitionLoadResult loaded,
        bool selectDefinition,
        bool ownerActive)
    {
        var representable = loaded.Definitions
            .Where(CanRepresentInMatrixUi)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenBy(DefinitionRestorePriority)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (representable is not null)
        {
            restoredMatrixDefinition = representable;
            if (selectDefinition)
            {
                control.SetMatrixDefinition(representable);
                control.SetMatrixPreview(BuildMatrixPreview(representable).PreviewRows);
            }
            var normalized = loaded.Diagnostics.Any(item =>
                item.Code == "experiment_definition.restart_normalized"
                && item.RelativePath.Equals(DefinitionRelativePath(representable.Id), StringComparison.Ordinal));
            matrixDefinitionRestoreReceipt = ownerActive
                ? $"A Matrix Runner owner is active; observed durable matrix {representable.Id} without restart normalization. Recovery remains pending."
                : normalized
                ? $"Recovered abandoned Running matrix state; restored newest matrix {representable.Id}, which requires explicit retry approval when interrupted."
                : $"Restored durable matrix {representable.Id} · {representable.Status.ToString().ToLowerInvariant()}.";
        }
        else if (loaded.Definitions.Length > 0)
        {
            matrixDefinitionRestoreReceipt = "Stored matrix definitions use dimensions this v1 Matrix Runner cannot represent; they remain untouched.";
        }

        if (loaded.Diagnostics.Length > 0
            && !loaded.Diagnostics.Any(item => item.Code == "experiment_definition.restart_normalized"))
        {
            matrixDefinitionRestoreReceipt = DiagnosticSummary(
                loaded.Diagnostics.Select(item => item.Code),
                matrixDefinitionRestoreReceipt ?? "Definition store loaded with diagnostics");
        }
    }

    private bool MatrixInputMatches(ArenaExperimentContract definition)
    {
        try
        {
            var generated = BuildMatrixPreview(control.ReadMatrixInput(), definition.CreatedAtUtc);
            return string.Equals(generated.Contract.Id, definition.Id, StringComparison.Ordinal)
                && string.Equals(
                    generated.Expansion.ExperimentFingerprint,
                    ArenaExperimentFingerprints.Experiment(definition),
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool CanRepresentInMatrixUi(ArenaExperimentContract definition)
    {
        if (definition.Dimensions.Length > 1
            || !definition.FaultProfileIds.IsEmpty
            || !definition.BranchIds.IsEmpty
            || definition.Dimensions.Any(item => item.Parameter is not
                ("temperature" or "max_output_tokens" or "context_length" or "reasoning" or "timeout_seconds")))
        {
            return false;
        }

        try
        {
            var dimension = definition.Dimensions.SingleOrDefault();
            var input = new ExperimentMatrixInput(
                definition.Title,
                definition.ScenarioPackId,
                string.Join(", ", definition.ProviderProfileIds),
                dimension?.Parameter ?? "",
                dimension is null ? "" : string.Join(", ", dimension.Values),
                definition.Repetitions.ToString(CultureInfo.InvariantCulture),
                definition.TurnBudget.ToString(CultureInfo.InvariantCulture),
                definition.MaxParallelism.ToString(CultureInfo.InvariantCulture),
                definition.BenchmarkPackId ?? "",
                string.Join(", ", definition.RubricIds));
            var rebuilt = BuildMatrixPreview(input, definition.CreatedAtUtc);
            return string.Equals(rebuilt.Contract.Id, definition.Id, StringComparison.Ordinal)
                && string.Equals(
                    rebuilt.Expansion.ExperimentFingerprint,
                    ArenaExperimentFingerprints.Experiment(definition),
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ExperimentLabInputException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static int DefinitionRestorePriority(ArenaExperimentContract definition) => definition.Status switch
    {
        ArenaExperimentStatus.Interrupted => 0,
        ArenaExperimentStatus.Running => 1,
        ArenaExperimentStatus.Draft => 2,
        _ => 3
    };

    private static string DefinitionRelativePath(string experimentId)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(experimentId)));
        return $"experiment-definitions/{digest}.json";
    }

    private static ArenaExperimentStatus ResolveExperimentLifecycle(
        ArenaExperimentExpansion expansion,
        ArenaExperimentRunnerResult result,
        string planFingerprint)
    {
        if (result.WasCancelled)
        {
            return ArenaExperimentStatus.Cancelled;
        }

        var planned = expansion.Cells.Select(item => item.CellKey).ToHashSet(StringComparer.Ordinal);
        var relevant = result.Runs.Where(item => planned.Contains(item.CellKey)).ToArray();
        return relevant.Length == planned.Count
               && relevant.All(item =>
                   ArenaExperimentRunPolicy.IsTerminal(item.State)
                   && ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(item, planFingerprint))
            ? ArenaExperimentStatus.Completed
            : ArenaExperimentStatus.Running;
    }

    internal static bool ShouldRetryTerminalCell(
        ArenaExperimentStatus definitionStatus,
        ArenaExperimentRunState runState) =>
        definitionStatus == ArenaExperimentStatus.Completed
        || runState != ArenaExperimentRunState.Completed;

    internal async Task ValidateMatrixAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        await ReconcileMatrixSourcesAsync(cancellationToken).ConfigureAwait(true);
        await EnsureMatrixDefinitionRestoredAsync(cancellationToken).ConfigureAwait(true);
        var preview = BuildCurrentMatrixPreview();
        var definitionWrite = await definitionStore.SaveAsync(
            preview.Contract with { Status = ArenaExperimentStatus.Draft },
            cancellationToken: cancellationToken).ConfigureAwait(true);
        if (!definitionWrite.Succeeded || definitionWrite.Artifact is null)
        {
            throw new ExperimentLabInputException(DiagnosticSummary(
                definitionWrite.Diagnostics.Select(item => item.Code),
                "Matrix draft could not be persisted"));
        }
        restoredMatrixDefinition = definitionWrite.Artifact;
        matrixDefinitionRestoreReceipt = null;
        preview = BuildMatrixPreview(definitionWrite.Artifact);
        control.SetMatrixPreview(preview.PreviewRows);
        var execution = await ResolveMatrixExecutionAsync(preview.Contract, cancellationToken).ConfigureAwait(true);
        control.ReconcileMatrixProviderProfiles(
            execution.ProviderProfileIds,
            restoredMatrixDefinition is not null);
        var diagnosticText = DiagnosticSummary(execution.Diagnostics.Select(item => item.Code),
            execution.IsAvailable ? "Execution resolved" : "Execution unavailable");
        control.SetMatrixExecutionAvailability(execution.IsAvailable, diagnosticText);
        control.SetMatrixStatus(
            $"Valid {definitionWrite.Artifact.Status.ToString().ToLowerInvariant()} definition · {preview.Expansion.Variants.Length} variant(s) · {preview.Expansion.Cells.Length} cell(s) · fingerprint {preview.Expansion.ExperimentFingerprint[..12]}. {diagnosticText}");
        await RefreshMatrixCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetMatrixStatus);

    internal async Task ExecuteMatrixAsync()
    {
        await actionGate.WaitAsync().ConfigureAwait(true);
        var cancellation = new CancellationTokenSource();
        ArenaExperimentContract? activeDefinition = null;
        matrixRunCancellation = cancellation;
        control.SetMatrixRunning(true);
        try
        {
            await ReconcileMatrixSourcesAsync(cancellation.Token).ConfigureAwait(true);
            await using var definitionExecutionLease = await definitionStore
                .AcquireExecutionLeaseAsync(cancellation.Token)
                .ConfigureAwait(true);
            var preserveExplicitEdits = restoredMatrixDefinition is not null
                && !MatrixInputMatches(restoredMatrixDefinition);
            var recoveredDefinitions = await definitionStore.RecoverInterruptedAfterRestartAsync(
                definitionExecutionLease,
                UtcNow(),
                cancellation.Token).ConfigureAwait(true);
            matrixDefinitionRestoreAttempted = true;
            ApplyLoadedMatrixDefinition(
                recoveredDefinitions,
                selectDefinition: !preserveExplicitEdits,
                ownerActive: false);
            var preview = BuildCurrentMatrixPreview();
            control.SetMatrixPreview(preview.PreviewRows);
            var execution = await ResolveMatrixExecutionAsync(preview.Contract, cancellation.Token).ConfigureAwait(true);
            control.ReconcileMatrixProviderProfiles(
                execution.ProviderProfileIds,
                restoredMatrixDefinition is not null);
            if (execution.Binding is null)
            {
                var unavailable = DiagnosticSummary(
                    execution.Diagnostics.Select(item => item.Code),
                    "Execution unavailable");
                control.SetMatrixExecutionAvailability(false, unavailable);
                control.SetMatrixStatus(unavailable);
                return;
            }

            var binding = execution.Binding;
            control.SetMatrixExecutionAvailability(
                true,
                DiagnosticSummary(binding.Diagnostics.Select(item => item.Code), "Execution resolved"));
            var executor = new ArenaExperimentCellExecutor(
                binding.Plan,
                binding.Profiles,
                sessionStore,
                providerClient);
            var runner = new ExperimentRunnerService(executor, timeProvider);
            var retryApproved = control.ConsumeMatrixRetryApproval();
            var retryScopeStatus = preview.Contract.Status;
            var definitionWrite = await definitionStore.SaveForExecutionAsync(
                definitionExecutionLease,
                preview.Contract with { Status = ArenaExperimentStatus.Running },
                retryApproved,
                cancellation.Token).ConfigureAwait(true);
            if (definitionWrite.Succeeded && definitionWrite.Artifact is not null)
            {
                activeDefinition = definitionWrite.Artifact;
                restoredMatrixDefinition = activeDefinition;
            }
            else if (!retryApproved
                     && definitionWrite.Diagnostics.Any(item =>
                         item.Code == "experiment_definition.retry_approval_required"))
            {
                matrixDefinitionRestoreReceipt = null;
                control.SetMatrixStatus(
                    $"Retry approval required · durable definition {preview.Contract.Status.ToString().ToLowerInvariant()} was unchanged · zero cells started.");
                return;
            }
            else
            {
                throw new ExperimentLabInputException(DiagnosticSummary(
                    definitionWrite.Diagnostics.Select(item => item.Code),
                    "Matrix definition could not enter Running"));
            }
            matrixDefinitionRestoreReceipt = null;
            control.SetMatrixStatus(
                $"Running {binding.Plan.Expansion.Cells.Length} resolved cell(s) in isolated child sessions"
                + (retryApproved ? " with one explicitly approved retry for every terminal cell" : "")
                + ". Cancel remains available.");
            var result = await runner.RunAsync(
                binding.Plan.Experiment,
                binding.Plan.Expansion,
                runStore,
                new ArenaExperimentRunnerOptions(
                    MaximumParallelism: binding.Plan.Experiment.MaxParallelism,
                    RetryApproved: retryApproved
                        ? run => ShouldRetryTerminalCell(retryScopeStatus, run.State)
                            || !ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(run, binding.Plan.PlanFingerprint)
                        : null),
                cancellation.Token).ConfigureAwait(true);
            var lifecycle = ResolveExperimentLifecycle(
                preview.Expansion,
                result,
                binding.Plan.PlanFingerprint);
            var lifecycleWrite = await definitionStore.SaveForExecutionAsync(
                definitionExecutionLease,
                activeDefinition with { Status = lifecycle },
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            if (lifecycleWrite.Succeeded && lifecycleWrite.Artifact is not null)
            {
                restoredMatrixDefinition = lifecycleWrite.Artifact;
                activeDefinition = lifecycleWrite.Artifact;
            }
            else
            {
                throw new ExperimentLabInputException(DiagnosticSummary(
                    lifecycleWrite.Diagnostics.Select(item => item.Code),
                    "Matrix lifecycle receipt could not be persisted"));
            }
            var plannedCellKeys = preview.Expansion.Cells
                .Select(item => item.CellKey)
                .ToHashSet(StringComparer.Ordinal);
            var terminal = result.Runs
                .Where(item => plannedCellKeys.Contains(item.CellKey))
                .GroupBy(item => item.State)
                .OrderBy(item => item.Key)
                .Select(item => $"{item.Key.ToString().ToLowerInvariant()} {item.Count()}")
                .ToArray();
            var diagnosticCodes = result.Diagnostics.Select(item => item.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(12)
                .ToArray();
            var receipt = $"Run receipt · planned {result.PlannedCells} · eligible {result.EligibleCells} · started {result.StartedCells}"
                + (terminal.Length == 0 ? "" : $" · {string.Join(" · ", terminal)}")
                + (result.WasCancelled ? " · cancelled" : "")
                + $" · durable definition {activeDefinition.Status.ToString().ToLowerInvariant()}"
                + (diagnosticCodes.Length == 0 ? "." : $" · diagnostic code(s): {string.Join(", ", diagnosticCodes)}.");
            control.SetMatrixStatus(receipt);
            await RefreshMatrixCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ExperimentLabInputException exception)
        {
            control.SetMatrixStatus(exception.Message);
        }
        catch (OperationCanceledException)
        {
            if (activeDefinition?.Status == ArenaExperimentStatus.Running)
            {
                try
                {
                    await using var cancellationOwner = await definitionStore
                        .AcquireExecutionLeaseAsync(CancellationToken.None)
                        .ConfigureAwait(true);
                    var cancelled = await definitionStore.SaveForExecutionAsync(
                        cancellationOwner,
                        activeDefinition with { Status = ArenaExperimentStatus.Cancelled },
                        cancellationToken: CancellationToken.None).ConfigureAwait(true);
                    if (cancelled.Succeeded && cancelled.Artifact is not null)
                    {
                        restoredMatrixDefinition = cancelled.Artifact;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Another owner became active after this operation unwound.
                    // Leave Running untouched; that owner or restart recovery is
                    // now responsible for the durable lifecycle.
                }
            }
            control.SetMatrixStatus("Matrix cancellation was requested; durable run history records every observed terminal state.");
            await RefreshMatrixCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            control.SetMatrixStatus($"Matrix execution failed safely ({exception.GetType().Name}); no provider result or evidence was invented.");
            await RefreshMatrixCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(matrixRunCancellation, cancellation))
            {
                matrixRunCancellation = null;
            }
            cancellation.Dispose();
            control.SetMatrixRunning(false);
            actionGate.Release();
        }
    }

    internal void CancelMatrix()
    {
        var cancellation = matrixRunCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }
        cancellation.Cancel();
        control.SetMatrixStatus("Cancellation requested; running cells will transition through the durable runner.");
    }

    internal async Task RefreshRunHistoryAsync() =>
        await ExecuteUiAsync(RefreshMatrixCoreAsync, control.SetMatrixStatus);

    internal async Task RefreshForkCursorsAsync() =>
        await ExecuteUiAsync(RefreshForkCursorsCoreAsync, control.SetForkStatus);

    internal async Task CreateForkAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        if (control.SelectedForkCursor is not ExperimentForkCursorItem cursor)
        {
            throw new ExperimentLabInputException("Select a stable transcript cursor before forking.");
        }

        var result = await ForkAndLoadAsync(cursor.MessageId, control.ForkTargetId, cancellationToken).ConfigureAwait(true);
        control.SetForkStatus(FormatForkReceipt(result));
        await RefreshForkCursorsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetForkStatus);

    internal static string FormatForkReceipt(SessionForkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var setupNote = result.HistoricalSetupProjectionUnavailable
            ? " · current setup retained because cursor-scoped setup history is unavailable"
            : "";
        return $"Forked and loaded 1 child session · cursor {result.CursorMessageId} · {result.MessageCount} retained message(s) · {result.ExcludedMemoryEntryCount} memory entry/entries omitted ({result.UnprojectableMemoryEntryCount} unprojectable){setupNote}.";
    }

    internal async Task SaveScenarioPackAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var pack = await CreateScenarioPackAsync(control.ReadPackInput(), cancellationToken).ConfigureAwait(true);
        var result = await SaveScenarioPackAsync(pack, cancellationToken).ConfigureAwait(true);
        control.SetPackStatus(WriteReceipt("scenario pack", result.Disposition, result.Diagnostics));
        if (result.Succeeded)
        {
            control.SetMatrixScenarioPackId(pack.Id);
        }
        await RefreshPacksCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetPackStatus);

    internal async Task SaveBenchmarkPackAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var scenarios = await ListScenarioPacksAsync(cancellationToken).ConfigureAwait(true);
        var selected = (control.SelectedPack as ExperimentPackItem)?.Contract as ArenaScenarioPackContract
            ?? scenarios.Artifacts.FirstOrDefault()
            ?? throw new ExperimentLabInputException("Save or import a valid scenario pack before creating a benchmark pack.");
        var rubrics = await ListRubricsAsync(cancellationToken).ConfigureAwait(true);
        var pack = CreateBenchmarkPack(control.ReadPackInput(), selected, [.. rubrics.Artifacts.Select(item => item.Id)]);
        var result = await SaveBenchmarkPackAsync(pack, cancellationToken).ConfigureAwait(true);
        control.SetPackStatus(WriteReceipt("benchmark pack", result.Disposition, result.Diagnostics));
        await RefreshPacksCoreAsync(cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            control.SelectMatrixBenchmark(pack.Id);
        }
    }, control.SetPackStatus);

    internal async Task ImportPackAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var path = dialogs.OpenJson();
        if (string.IsNullOrWhiteSpace(path))
        {
            control.SetPackStatus("Import cancelled; the local pack store was unchanged.");
            return;
        }

        var bytes = await ReadBoundedFileAsync(path, MaximumImportedBytes, cancellationToken).ConfigureAwait(true);
        string? importedScenarioId = null;
        string? importedBenchmarkId = null;
        string? importReceipt = null;
        var scenario = ArenaExperimentPackCodec.DecodeScenarioPack(bytes, "imports/selected.json");
        if (scenario.Succeeded && scenario.Pack is not null)
        {
            var write = await SaveScenarioPackAsync(scenario.Pack, cancellationToken).ConfigureAwait(true);
            importReceipt = PackImportReceipt("scenario pack import", write, migration: null);
            if (write.Succeeded)
            {
                importedScenarioId = scenario.Pack.Id;
            }
        }
        else
        {
            var benchmark = ArenaExperimentPackCodec.DecodeBenchmarkPack(bytes, "imports/selected.json");
            if (benchmark.Succeeded && benchmark.Pack is not null)
            {
                var write = await SaveBenchmarkPackAsync(benchmark.Pack, cancellationToken).ConfigureAwait(true);
                importReceipt = PackImportReceipt("benchmark pack import", write, migration: null);
                if (write.Succeeded)
                {
                    importedBenchmarkId = benchmark.Pack.Id;
                }
            }
            else
            {
                var migratedAtUtc = UtcNow();
                var migratedScenario = ArenaExperimentPackCodec.MigrateScenarioPackV0(
                    bytes,
                    "imports/selected.json",
                    migratedAtUtc);
                if (migratedScenario.Succeeded && migratedScenario.Pack is not null)
                {
                    var write = await SaveScenarioPackAsync(migratedScenario.Pack, cancellationToken).ConfigureAwait(true);
                    importReceipt = PackImportReceipt(
                        "scenario pack import",
                        write,
                        migratedScenario.Pack.Migration);
                    if (write.Succeeded)
                    {
                        importedScenarioId = migratedScenario.Pack.Id;
                    }
                }
                else
                {
                    var migratedBenchmark = ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                        bytes,
                        "imports/selected.json",
                        migratedAtUtc);
                    if (!migratedBenchmark.Succeeded || migratedBenchmark.Pack is null)
                    {
                        var codes = scenario.Diagnostics
                            .Concat(benchmark.Diagnostics)
                            .Concat(migratedScenario.Diagnostics)
                            .Concat(migratedBenchmark.Diagnostics)
                            .Select(item => item.Code);
                        throw new ExperimentLabInputException(DiagnosticSummary(codes, "Pack import was rejected"));
                    }

                    var write = await SaveBenchmarkPackAsync(migratedBenchmark.Pack, cancellationToken).ConfigureAwait(true);
                    importReceipt = PackImportReceipt(
                        "benchmark pack import",
                        write,
                        migratedBenchmark.Pack.Migration);
                    if (write.Succeeded)
                    {
                        importedBenchmarkId = migratedBenchmark.Pack.Id;
                    }
                }
            }
        }

        await RefreshPacksCoreAsync(cancellationToken).ConfigureAwait(true);
        if (importedScenarioId is not null)
        {
            control.SetMatrixScenarioPackId(importedScenarioId);
        }
        if (importedBenchmarkId is not null)
        {
            control.SelectMatrixBenchmark(importedBenchmarkId);
        }
        if (!string.IsNullOrWhiteSpace(importReceipt))
        {
            control.SetPackStatus(importReceipt);
        }
    }, control.SetPackStatus);

    internal async Task ExportSelectedPackAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        if (control.SelectedPack is not ExperimentPackItem selected)
        {
            throw new ExperimentLabInputException("Select a stored pack before exporting.");
        }

        var target = dialogs.SaveJson($"{selected.Kind}-{SafeFileName(selected.Id)}.json");
        if (string.IsNullOrWhiteSpace(target))
        {
            control.SetPackStatus("Export cancelled; no file was written.");
            return;
        }

        var json = selected.Contract switch
        {
            ArenaScenarioPackContract scenario => ArenaContractCodec.Serialize(scenario),
            ArenaBenchmarkPackContract benchmark => ArenaContractCodec.Serialize(benchmark),
            _ => throw new InvalidDataException("Unsupported pack type.")
        };
        await File.WriteAllTextAsync(target, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(true);
        control.SetPackStatus($"Exported 1 canonical {selected.Kind} artifact. The persisted receipt contains no absolute path or source content.");
    }, control.SetPackStatus);

    internal async Task RefreshPacksAsync() =>
        await ExecuteUiAsync(RefreshPacksCoreAsync, control.SetPackStatus);

    internal async Task SaveRubricAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var rubric = CreateRubric(control.ReadRubricInput());
        var result = await SaveRubricContractAsync(rubric, cancellationToken).ConfigureAwait(true);
        control.SetRubricStatus(WriteReceipt("rubric version", result.Disposition, result.Diagnostics));
        await RefreshRubricsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetRubricStatus);

    internal async Task RecordRubricObservationAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        if (control.SelectedRubric is not ExperimentRubricItem selected)
        {
            throw new ExperimentLabInputException("Create or select a rubric version before recording an observation.");
        }
        var result = CreateRubricObservation(selected.Contract, control.ReadRubricObservationInput());
        var write = await SaveRubricResultAsync(result, cancellationToken).ConfigureAwait(true);
        var source = result.HumanResults.Length > 0 ? "human"
            : result.DeterministicResults.Length > 0 ? "deterministic"
            : "model-judge";
        var evidenceState = result.HumanResults.Concat(result.DeterministicResults).Concat(result.ModelJudgeResults)
            .SelectMany(item => item.Criteria).All(item => item.Evidence.State == ArenaEvidenceState.Unavailable)
            ? "unavailable"
            : source;
        control.SetRubricStatus($"{WriteReceipt("rubric result", write.Disposition, write.Diagnostics)} Evidence partition: {evidenceState}.");
        await RefreshRubricsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetRubricStatus);

    internal async Task RunProviderJudgeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var lifetime = new ProviderJudgeActionLifetime();
        if (Interlocked.CompareExchange(ref providerJudgeLifetime, lifetime, null) is not null)
        {
            lifetime.Cancellation.Dispose();
            control.SetRubricStatus("A provider judge is already active. Cancel or wait for it before starting another.");
            return;
        }

        var enteredGate = false;
        control.SetProviderJudgeRunning(true);
        control.SetRubricStatus("Provider judge running. Cancel remains available until the receipt-first evidence commit begins.");
        try
        {
            await actionGate.WaitAsync(lifetime.Cancellation.Token).ConfigureAwait(true);
            enteredGate = true;
            if (control.SelectedRubric is not ExperimentRubricItem selected)
                throw new ExperimentLabInputException("Create or select a rubric version before running the provider judge.");
            var input = control.ReadRubricObservationInput();
            if (input.Pairwise)
                throw new ExperimentLabInputException("Use the concealed human workflow for pairwise judging; provider judging currently accepts one transcript message.");
            if (!input.Source.Equals("model", StringComparison.OrdinalIgnoreCase))
                throw new ExperimentLabInputException("Choose Model judge as the evidence source before invoking the provider.");
            if (input.Unavailable)
                throw new ExperimentLabInputException("Clear Evidence unavailable before invoking the provider judge.");
            var finalization = await CreateModelJudgeFinalizationAsync(
                selected.Contract,
                RequiredId(input.SubjectA, "Subject A reference"),
                lifetime.Cancellation.Token).ConfigureAwait(true);
            if (providerJudgePreCommitOverride is not null)
                await providerJudgePreCommitOverride(lifetime.Cancellation.Token).ConfigureAwait(true);
            lifetime.Cancellation.Token.ThrowIfCancellationRequested();
            if (!lifetime.TryBeginCommit())
                throw new InvalidOperationException("Provider judge commit boundary was already entered.");
            // Close the race where Cancel set the token immediately before the
            // interlocked commit transition. Once this check passes, Cancel and
            // Dispose observe CommitStarted and cannot interrupt local evidence.
            lifetime.Cancellation.Token.ThrowIfCancellationRequested();
            control.SetProviderJudgeCommitting();
            control.SetRubricStatus("Provider judgment finalized; committing receipt-first local evidence. Cancellation is now closed.");
            await PersistModelJudgeFinalizationAsync(finalization).ConfigureAwait(true);
            var score = finalization.Result.ModelJudgeResults.Single().WeightedScoreA;
            await RefreshRubricsCoreAsync(CancellationToken.None).ConfigureAwait(true);
            control.SetRubricStatus($"Saved provider-bound model opinion · normalized score {score?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable"} · receipt {finalization.Receipt.Id}.");
        }
        catch (ExperimentLabInputException exception)
        {
            control.SetRubricStatus(exception.Message);
        }
        catch (OperationCanceledException) when (lifetime.Cancellation.IsCancellationRequested)
        {
            control.SetRubricStatus("Provider judge cancelled before evidence commit; no receipt or rubric result was persisted.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            control.SetRubricStatus($"Provider judge failed safely ({exception.GetType().Name}); no unproved model score was persisted.");
        }
        finally
        {
            if (enteredGate) actionGate.Release();
            if (ReferenceEquals(Interlocked.CompareExchange(ref providerJudgeLifetime, null, lifetime), lifetime))
            {
                control.SetProviderJudgeRunning(false);
            }
            lifetime.Cancellation.Dispose();
        }
    }

    internal void CancelProviderJudge()
    {
        var lifetime = Volatile.Read(ref providerJudgeLifetime);
        if (lifetime is null)
        {
            control.SetRubricStatus("No provider judge is active.");
            return;
        }
        if (lifetime.CommitStarted)
        {
            control.SetRubricStatus("Provider judgment is committing receipt-first local evidence and can no longer be cancelled.");
            return;
        }
        control.SetRubricStatus("Cancelling provider judge…");
        CancelSafely(lifetime.Cancellation);
    }

    internal async Task BeginBlindPairwiseAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        if (control.SelectedRubric is not ExperimentRubricItem selected)
            throw new ExperimentLabInputException("Create or select a rubric version before beginning a concealed comparison.");
        var input = control.ReadRubricObservationInput();
        if (!input.Pairwise)
            throw new ExperimentLabInputException("Enable Blind pairwise comparison before beginning the concealed view.");
        if (!input.Source.Equals("human", StringComparison.OrdinalIgnoreCase))
            throw new ExperimentLabInputException("The visible two-step workflow currently accepts a human reviewer only. Model pairwise judging remains unavailable rather than being mislabeled.");
        await BeginBlindPairwiseAsync(
            selected.Contract,
            input.SubjectA,
            input.SubjectB,
            cancellationToken).ConfigureAwait(true);
    }, control.SetRubricStatus);

    internal async Task SubmitBlindPairwiseAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var input = control.ReadRubricObservationInput();
        if (!input.Source.Equals("human", StringComparison.OrdinalIgnoreCase))
            throw new ExperimentLabInputException("Only the local human reviewer can submit this concealed view.");
        var finalization = await SubmitBlindPairwiseAsync(
            input.Score,
            input.ScoreB,
            input.Preference,
            input.Unavailable,
            cancellationToken).ConfigureAwait(true);
        await RefreshRubricsCoreAsync(cancellationToken).ConfigureAwait(true);
        var reveal = finalization.Result.BlindReveal!;
        control.SetRubricStatus($"Blind result saved · A = {reveal.LabelAReferenceId} · B = {reveal.LabelBReferenceId} · receipt {finalization.Receipt.Id}.");
    }, control.SetRubricStatus);

    internal async Task RefreshRubricsAsync() =>
        await ExecuteUiAsync(RefreshRubricsCoreAsync, control.SetRubricStatus);

    internal async Task CreateClaimLedgerAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var ledger = CreateClaimLedger(control.ReadClaimLedgerInput());
        var result = await SaveClaimLedgerAsync(ledger, cancellationToken).ConfigureAwait(true);
        control.SetClaimStatus(WriteReceipt("claim ledger", result.Disposition, result.Diagnostics));
        await RefreshClaimsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetClaimStatus);

    internal async Task RefreshClaimsAsync() =>
        await ExecuteUiAsync(RefreshClaimsCoreAsync, control.SetClaimStatus);

    internal async Task RefreshSelectedClaimsAsync()
    {
        if (control.SelectedLedger is ExperimentLedgerItem ledger)
        {
            control.SetClaims(ledger.Contract.Claims.Select(item => (object)new ExperimentClaimItem(item)));
        }
        else
        {
            control.SetClaims([]);
        }
        await Task.CompletedTask;
    }

    internal async Task AddClaimAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var sessionId = activeSessionId()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because no session is loaded.");
        }
        NotifyActiveSessionChanged(sessionId);
        var sessionGeneration = Volatile.Read(ref claimSessionGeneration);
        var ledger = await LoadSelectedLedgerAsync(cancellationToken).ConfigureAwait(true);
        if (control.SelectedClaimMessage is not ExperimentForkCursorItem message)
        {
            throw new ExperimentLabInputException("Select a stable transcript message before adding a claim.");
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(true);
        if (snapshot is null
            || sessionGeneration != Volatile.Read(ref claimSessionGeneration)
            || !sessionId.Equals(activeSessionId()?.Trim(), StringComparison.Ordinal))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because the active session changed.");
        }
        if (snapshot.BranchReceipt is not { } branch
            || !LedgerMatchesBranch(ledger, branch, sessionId))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because the selected ledger does not belong to the active session branch.");
        }

        var resolved = snapshot.Engine.Messages
            .Select((candidate, index) => new ExperimentForkCursorItem(
                DialogueMessageIdentity.Resolve(candidate, index),
                index,
                Math.Max(0, candidate.Turn),
                SafeSpeakerId(candidate),
                candidate))
            .SingleOrDefault(candidate => candidate.MessageId.Equals(message.MessageId, StringComparison.Ordinal));
        if (resolved is null)
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because the selected message is not in the active session.");
        }

        var advanced = AddClaim(ledger, resolved, control.ClaimSummary);
        if (sessionGeneration != Volatile.Read(ref claimSessionGeneration)
            || !sessionId.Equals(activeSessionId()?.Trim(), StringComparison.Ordinal))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because the active session changed before append.");
        }
        var write = await SaveClaimLedgerAsync(advanced, cancellationToken).ConfigureAwait(true);
        control.SetClaimStatus(WriteReceipt("claim append", write.Disposition, write.Diagnostics));
        await RefreshClaimsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetClaimStatus);

    internal async Task ReviewSelectedClaimAsync(string statusText) => await ExecuteUiAsync(async cancellationToken =>
    {
        var ledger = await LoadSelectedLedgerAsync(cancellationToken).ConfigureAwait(true);
        if (control.SelectedClaims.SingleOrDefault() is not ExperimentClaimItem claim)
        {
            throw new ExperimentLabInputException("Select exactly one claim to review.");
        }
        var status = statusText.Equals("supported", StringComparison.OrdinalIgnoreCase)
            ? ArenaClaimStatus.Supported
            : ArenaClaimStatus.Unavailable;
        var advanced = ReviewClaim(ledger, claim.Claim.Id, status);
        var write = await SaveClaimLedgerAsync(advanced, cancellationToken).ConfigureAwait(true);
        control.SetClaimStatus(WriteReceipt("claim review", write.Disposition, write.Diagnostics));
        await RefreshClaimsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetClaimStatus);

    internal async Task LinkSelectedContradictionAsync() => await ExecuteUiAsync(async cancellationToken =>
    {
        var selected = control.SelectedClaims.OfType<ExperimentClaimItem>().ToArray();
        if (selected.Length != 2)
        {
            throw new ExperimentLabInputException("Select exactly two claims to link as contradictory.");
        }
        var ledger = await LoadSelectedLedgerAsync(cancellationToken).ConfigureAwait(true);
        var advanced = LinkContradiction(ledger, selected[0].Claim.Id, selected[1].Claim.Id);
        var write = await SaveClaimLedgerAsync(advanced, cancellationToken).ConfigureAwait(true);
        control.SetClaimStatus(WriteReceipt("contradiction review", write.Disposition, write.Diagnostics));
        await RefreshClaimsCoreAsync(cancellationToken).ConfigureAwait(true);
    }, control.SetClaimStatus);

    private async Task RefreshMatrixCoreAsync(CancellationToken cancellationToken)
    {
        await ReconcileMatrixSourcesAsync(cancellationToken).ConfigureAwait(true);
        await EnsureMatrixDefinitionRestoredAsync(cancellationToken).ConfigureAwait(true);
        var history = await ListRunHistoryAsync(cancellationToken).ConfigureAwait(true);
        var rows = history.Runs
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(MaximumHistoryRows)
            .Select(item => $"{item.Id} · {item.State} · attempt {item.Attempts} · {item.TrialIds.Length} trial reference(s)")
            .ToArray();
        control.SetRunHistory(rows.Length == 0 ? ["No persisted experiment runs."] : rows);
        if (history.Diagnostics.Length > 0)
        {
            control.SetMatrixStatus(DiagnosticSummary(history.Diagnostics.Select(item => item.Code), "Run history loaded with diagnostics"));
        }
        else if (!string.IsNullOrWhiteSpace(matrixDefinitionRestoreReceipt))
        {
            control.SetMatrixStatus(matrixDefinitionRestoreReceipt);
        }
    }

    private async Task RefreshForkCursorsCoreAsync(CancellationToken cancellationToken)
    {
        NotifyActiveSessionChanged(activeSessionId());
        var sessionId = claimSessionId;
        var sessionGeneration = Volatile.Read(ref claimSessionGeneration);
        var cursors = string.IsNullOrEmpty(sessionId)
            ? ImmutableArray<ExperimentForkCursorItem>.Empty
            : await LoadForkCursorsAsync(sessionId, cancellationToken).ConfigureAwait(true);
        if (sessionGeneration != Volatile.Read(ref claimSessionGeneration)
            || !sessionId.Equals(activeSessionId()?.Trim() ?? "", StringComparison.Ordinal))
        {
            return;
        }
        control.SetForkSession(string.IsNullOrEmpty(sessionId) ? "No session loaded." : sessionId);
        control.SetForkCursors(cursors.Cast<object>());
        control.SetClaimMessages(cursors.Cast<object>());
        control.SetForkStatus(cursors.Length == 0
            ? "No stable transcript cursors are available in the current session."
            : $"Loaded {cursors.Length} stable transcript cursor(s); message text remains outside the selector receipt.");
    }

    private async Task RefreshPacksCoreAsync(CancellationToken cancellationToken)
    {
        var scenarios = await ListScenarioPacksAsync(cancellationToken).ConfigureAwait(true);
        var benchmarks = await ListBenchmarkPacksAsync(cancellationToken).ConfigureAwait(true);
        ReconcileMatrixPacks(
            scenarios.Artifacts,
            benchmarks.Artifacts,
            restoredMatrixDefinition is not null);
        var items = scenarios.Artifacts.Select(item => new ExperimentPackItem("scenario", item.Id, item.Version, item))
            .Concat(benchmarks.Artifacts.Select(item => new ExperimentPackItem("benchmark", item.Id, item.Version, item)))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Cast<object>()
            .ToArray();
        control.SetPacks(items);
        var diagnostics = scenarios.Diagnostics.Concat(benchmarks.Diagnostics).ToArray();
        control.SetPackStatus(diagnostics.Length == 0
            ? $"Loaded {scenarios.Artifacts.Length} scenario and {benchmarks.Artifacts.Length} benchmark pack(s)."
            : DiagnosticSummary(diagnostics.Select(item => item.Code), "Pack store loaded with diagnostics"));
    }

    private void ReconcileMatrixPacks(
        ImmutableArray<ArenaScenarioPackContract> scenarios,
        ImmutableArray<ArenaBenchmarkPackContract> benchmarks,
        bool preserveCurrentInput = false)
    {
        var currentInput = control.ReadMatrixInput();
        var benchmarkItems = new List<object>
        {
            new ExperimentBenchmarkSelection("Scenario only (no benchmark)", null, null, [])
        };
        benchmarkItems.AddRange(benchmarks
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => (object)new ExperimentBenchmarkSelection(
                $"{item.Name} · v{item.Version} · {item.Id}",
                item.Id,
                item.ScenarioPackId,
                [.. item.Cases.SelectMany(value => value.RubricIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])));
        control.SetMatrixBenchmarks(benchmarkItems);
        if (preserveCurrentInput)
        {
            control.SelectMatrixBenchmark(currentInput.BenchmarkPackId);
            // Replacing the ComboBox item source raises SelectionChanged. Put
            // the durable references back after that presentation-only event.
            control.SetMatrixScenarioPackId(currentInput.ScenarioPackId);
            control.SetMatrixRubricIds(currentInput.RubricIds);
        }

        var currentScenarioId = currentInput.ScenarioPackId;
        if (!preserveCurrentInput
            && !scenarios.Any(item => item.Id.Equals(currentScenarioId, StringComparison.Ordinal))
            && scenarios.OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault() is { } first)
        {
            control.SetMatrixScenarioPackId(first.Id);
        }
    }

    private async Task RefreshRubricsCoreAsync(CancellationToken cancellationToken)
    {
        var rubrics = await ListRubricsAsync(cancellationToken).ConfigureAwait(true);
        var results = await ListRubricResultsAsync(cancellationToken).ConfigureAwait(true);
        control.SetRubrics(rubrics.Artifacts.Select(item => (object)new ExperimentRubricItem(item)));
        control.SetRubricResults(results.Artifacts
            .OrderByDescending(item => item.FinalizedAtUtc)
            .Take(MaximumHistoryRows)
            .Select(item => $"{item.Id} · human {item.HumanResults.Length} · deterministic {item.DeterministicResults.Length} · model {item.ModelJudgeResults.Length} · disagreement {item.Disagreements.Length}"));
        var diagnostics = rubrics.Diagnostics.Concat(results.Diagnostics).ToArray();
        control.SetRubricStatus(diagnostics.Length == 0
            ? $"Loaded {rubrics.Artifacts.Length} rubric version(s) and {results.Artifacts.Length} separated result(s)."
            : DiagnosticSummary(diagnostics.Select(item => item.Code), "Rubric store loaded with diagnostics"));
    }

    private async Task RefreshClaimsCoreAsync(CancellationToken cancellationToken)
    {
        NotifyActiveSessionChanged(activeSessionId());
        var sessionId = claimSessionId;
        var sessionGeneration = Volatile.Read(ref claimSessionGeneration);
        var ledgers = await ListClaimLedgersAsync(cancellationToken).ConfigureAwait(true);
        var cursors = await ListForkCursorsAsync(cancellationToken).ConfigureAwait(true);
        var snapshot = string.IsNullOrEmpty(sessionId)
            ? null
            : await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(true);
        if (sessionGeneration != Volatile.Read(ref claimSessionGeneration)
            || !sessionId.Equals(activeSessionId()?.Trim() ?? "", StringComparison.Ordinal))
        {
            return;
        }
        var compatibleLedgers = snapshot?.BranchReceipt is { } branch
            ? ledgers.Artifacts.Where(item => LedgerMatchesBranch(item, branch, sessionId)).ToArray()
            : Array.Empty<ArenaClaimLedgerContract>();
        control.SetClaimLedgers(compatibleLedgers.Select(item => (object)new ExperimentLedgerItem(item)));
        await RefreshSelectedClaimsAsync().ConfigureAwait(true);
        control.SetClaimMessages(cursors.Cast<object>());
        control.SetClaimStatus(ledgers.Diagnostics.Length == 0
            ? $"Loaded {ledgers.Artifacts.Length} monotonic claim ledger(s)."
            : DiagnosticSummary(ledgers.Diagnostics.Select(item => item.Code), "Claim ledger store loaded with diagnostics"));
    }

    private async Task<ArenaClaimLedgerContract> LoadSelectedLedgerAsync(CancellationToken cancellationToken)
    {
        var sessionId = activeSessionId()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because no session is loaded.");
        }
        NotifyActiveSessionChanged(sessionId);
        if (control.SelectedLedger is not ExperimentLedgerItem selected)
        {
            throw new ExperimentLabInputException("Create or select a claim ledger first.");
        }
        var loaded = await ListClaimLedgersAsync(cancellationToken).ConfigureAwait(false);
        var ledger = loaded.Artifacts.FirstOrDefault(item => item.Id == selected.Contract.Id)
            ?? throw new ExperimentLabInputException("The selected ledger changed on disk; refresh and select it again.");
        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.BranchReceipt is not { } branch
            || !LedgerMatchesBranch(ledger, branch, sessionId)
            || !sessionId.Equals(activeSessionId()?.Trim(), StringComparison.Ordinal))
        {
            throw new ExperimentLabInputException("Claim provenance is unavailable because the selected ledger does not belong to the active session branch.");
        }
        return ledger;
    }

    private static bool LedgerMatchesBranch(
        ArenaClaimLedgerContract ledger,
        ArenaBranchContract branch,
        string sessionId) =>
        !string.IsNullOrWhiteSpace(ledger.ExperimentId)
        && branch.ChildSessionId.Equals(sessionId, StringComparison.Ordinal)
        && ledger.BranchId.Equals(branch.Id, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(branch.ExperimentId)
            || ledger.ExperimentId.Equals(branch.ExperimentId, StringComparison.Ordinal));

    private async Task ExecuteUiAsync(Func<CancellationToken, Task> action, Action<string> setStatus)
    {
        await actionGate.WaitAsync().ConfigureAwait(true);
        control.SetBusy(true);
        try
        {
            await action(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ExperimentLabInputException exception)
        {
            setStatus(exception.Message);
        }
        catch (OperationCanceledException)
        {
            setStatus("Action cancelled; persisted Experiment Lab artifacts were not rewritten.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            setStatus($"Action failed safely ({exception.GetType().Name}); no provider result or evidence was invented.");
        }
        finally
        {
            control.SetBusy(false);
            actionGate.Release();
        }
    }

    private async Task RefreshFeatureSelectionSafelyAsync(
        string key,
        long generation,
        CancellationTokenSource cancellation)
    {
        var enteredGate = false;
        try
        {
            await actionGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            enteredGate = true;
            if (!IsCurrentFeatureSelectionRefresh(key, generation, cancellation))
            {
                return;
            }

            await OnControlDispatcherAsync(async () =>
            {
                control.SetBusy(true);
                await RefreshFeatureAsync(key, cancellation.Token).ConfigureAwait(true);
            }, cancellation.Token).ConfigureAwait(false);
            if (IsCurrentFeatureSelectionRefresh(key, generation, cancellation))
            {
                await OnControlDispatcherAsync(() =>
                    {
                        if (IsCurrentFeatureSelectionRefresh(key, generation, cancellation))
                        {
                            control.SetFeatureRefreshSummary(key, "ready");
                        }
                    })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer selection or coordinator disposal superseded this refresh.
        }
        catch (Exception exception)
        {
            if (IsCurrentFeatureSelectionRefresh(key, generation, cancellation))
            {
                await OnControlDispatcherAsync(() =>
                    {
                        if (!IsCurrentFeatureSelectionRefresh(key, generation, cancellation))
                        {
                            return;
                        }

                        SetFeatureRefreshStatus(
                            key,
                            $"Feature refresh failed safely ({exception.GetType().Name}); persisted Experiment Lab evidence was not rewritten.");
                        control.SetFeatureRefreshSummary(key, "refresh-failed");
                    })
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (enteredGate)
            {
                await OnControlDispatcherAsync(() => control.SetBusy(false)).ConfigureAwait(false);
                actionGate.Release();
            }

            Interlocked.CompareExchange(ref featureSelectionRefreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private bool IsCurrentFeatureSelectionRefresh(
        string key,
        long generation,
        CancellationTokenSource cancellation) =>
        !disposed
        && !cancellation.IsCancellationRequested
        && generation == Volatile.Read(ref featureSelectionRefreshGeneration)
        && ReferenceEquals(Volatile.Read(ref featureSelectionRefreshCancellation), cancellation)
        && string.Equals(Volatile.Read(ref featureSelectionRefreshKey), key, StringComparison.Ordinal);

    private Task OnControlDispatcherAsync(Action action)
    {
        if (control.Dispatcher.HasShutdownStarted || control.Dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return control.Dispatcher.InvokeAsync(action).Task;
    }

    private Task OnControlDispatcherAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (control.Dispatcher.HasShutdownStarted || control.Dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return control.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken)
            .Task
            .Unwrap();
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or AggregateException)
        {
            // Cancellation is a best-effort UI/lifetime signal. A completed
            // source or hostile callback cannot escape visible Cancel/Dispose.
        }
    }

    private void SetFeatureRefreshStatus(string key, string status)
    {
        switch (key)
        {
            case "matrix":
                control.SetMatrixStatus(status);
                break;
            case "fork":
                control.SetForkStatus(status);
                break;
            case "packs":
                control.SetPackStatus(status);
                break;
            case "rubrics":
                control.SetRubricStatus(status);
                break;
            case "claims":
                control.SetClaimStatus(status);
                break;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Interlocked.Increment(ref featureSelectionRefreshGeneration);
        Volatile.Write(ref featureSelectionRefreshKey, null);
        CancelSafely(Interlocked.Exchange(ref featureSelectionRefreshCancellation, null));
        matrixRunCancellation?.Cancel();
        var judgeLifetime = Volatile.Read(ref providerJudgeLifetime);
        if (judgeLifetime is { CommitStarted: false }) CancelSafely(judgeLifetime.Cancellation);
        pendingBlindSession = null;
        pendingBlindFinalization = null;
        pendingBlindRubric = null;
    }

    private static ExperimentExecutionResolution UnavailableExecution(string code, string summary) =>
        new(
            null,
            [],
            [new ArenaExperimentExecutionDiagnostic(
                code,
                ArenaExperimentExecutionDiagnosticSeverity.Error,
                summary)]);

    private static string ProviderProfileId(string configurationKey, IEnumerable<string> existingIds)
    {
        var source = (configurationKey ?? "").Trim().ToLowerInvariant();
        var token = Regex.Replace(source, "[^a-z0-9._-]+", "-", RegexOptions.CultureInvariant).Trim('-');
        if (string.IsNullOrWhiteSpace(token))
        {
            token = StableHash(source)[..16];
        }
        token = token[..Math.Min(token.Length, 140)];
        var candidate = $"provider:{token}";
        var existing = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(candidate))
        {
            return candidate;
        }
        var suffix = StableHash(source)[..12];
        token = token[..Math.Min(token.Length, 140 - suffix.Length - 1)];
        return $"provider:{token}-{suffix}";
    }

    private DateTimeOffset UtcNow()
    {
        var value = timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }

    private static ArenaEvidenceAssertion EvidenceFor(
        ArenaRubricJudgmentSource source,
        bool unavailable,
        string id,
        string referenceId)
    {
        if (unavailable)
        {
            return new(id, ArenaEvidenceState.Unavailable, "Evidence was unavailable.", Limitation: "No trustworthy observation was supplied for this criterion.");
        }
        if (source == ArenaRubricJudgmentSource.ModelJudge)
        {
            throw new InvalidOperationException("Available model-judge evidence must be created by the provider-backed judge workflow.");
        }
        return new(id, ArenaEvidenceState.Observed, "Local reviewer or deterministic observation was recorded.", referenceId);
    }

    private static ArenaRubricJudgmentSource ParseRubricSource(string value) => value.Trim().ToLowerInvariant() switch
    {
        "human" => ArenaRubricJudgmentSource.Human,
        "deterministic" => ArenaRubricJudgmentSource.Deterministic,
        "model" => ArenaRubricJudgmentSource.ModelJudge,
        _ => throw new ExperimentLabInputException("Choose human, deterministic, or model-judge evidence.")
    };

    private static ArenaPairwisePreference ParsePreference(string value) =>
        Enum.TryParse<ArenaPairwisePreference>(value, true, out var parsed)
            ? parsed
            : throw new ExperimentLabInputException("Choose A, B, Tie, or Unavailable for the blind preference.");

    private static string SafeSpeakerId(DialogueMessage message)
    {
        var candidate = string.IsNullOrWhiteSpace(message.SpeakerId) ? message.Speaker : message.SpeakerId;
        var normalized = Regex.Replace((candidate ?? "speaker").Trim().ToLowerInvariant(), "[^a-z0-9._:-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "speaker:unknown" : normalized.Length <= 160 ? normalized : StableId("speaker", normalized);
    }

    private static ImmutableArray<string> ParseReferences(string value, string label, int maximum)
    {
        var values = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => RequiredReference(item, label, 160))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (values.Length == 0 || values.Length > maximum)
        {
            throw new ExperimentLabInputException($"{label} requires 1-{maximum} unique values.");
        }
        return values;
    }

    private static ImmutableArray<string> ParseOptionalReferences(string value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        return ParseReferences(value, label, maximum);
    }

    private static ImmutableArray<ArenaExperimentDimension> ParseDimensions(string parameter, string valuesText)
    {
        var normalizedParameter = (parameter ?? "").Trim();
        if (normalizedParameter.Length == 0)
        {
            return [];
        }
        if (normalizedParameter is not ("temperature" or "max_output_tokens" or "context_length" or "reasoning" or "timeout_seconds"))
        {
            throw new ExperimentLabInputException("Experiment dimension is outside the executable v1 allowlist.");
        }

        var rawValues = (valuesText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawValues.Length is 0 or > 128)
        {
            throw new ExperimentLabInputException("Executable dimension requires 1-128 bounded values.");
        }
        var values = rawValues.Select(value => NormalizeDimensionValue(normalizedParameter, value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return [new($"dimension:{normalizedParameter.Replace('_', '-')}", normalizedParameter, values)];
    }

    private static string NormalizeDimensionValue(string parameter, string value)
    {
        var normalized = value.Trim();
        switch (parameter)
        {
            case "temperature":
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                    || !double.IsFinite(temperature) || temperature is < 0 or > 2)
                {
                    throw new ExperimentLabInputException("Temperature dimension values must be finite numbers from 0 through 2.");
                }
                return temperature == 0d ? "0" : temperature.ToString("R", CultureInfo.InvariantCulture);
            case "max_output_tokens":
                return CanonicalDimensionInteger(normalized, "Max output token", 1, 32_768);
            case "context_length":
                return CanonicalDimensionInteger(normalized, "Context length", 0, 1_048_576);
            case "timeout_seconds":
                return CanonicalDimensionInteger(normalized, "Timeout", 1, 3_600);
            case "reasoning":
                var reasoning = ModelProviderReasoningModes.Normalize(normalized);
                if (string.IsNullOrWhiteSpace(reasoning) || !reasoning.Equals(normalized, StringComparison.Ordinal))
                {
                    throw new ExperimentLabInputException("Reasoning dimension values must use a supported lowercase provider mode.");
                }
                return reasoning;
            default:
                throw new ExperimentLabInputException("Experiment dimension is outside the executable v1 allowlist.");
        }
    }

    private static string CanonicalDimensionInteger(string value, string label, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum
            || !parsed.ToString(CultureInfo.InvariantCulture).Equals(value, StringComparison.Ordinal))
        {
            throw new ExperimentLabInputException($"{label} dimension values must be canonical integers from {minimum} through {maximum}.");
        }
        return value;
    }

    private static string RequiredText(string value, string label, int maximum)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length is 0 || normalized.Length > maximum)
        {
            throw new ExperimentLabInputException($"{label} requires 1-{maximum} characters.");
        }
        return normalized;
    }

    private static string RequiredReference(string value, string label, int maximum)
    {
        var normalized = RequiredText(value, label, maximum);
        if (normalized.Any(char.IsControl))
        {
            throw new ExperimentLabInputException($"{label} contains unsupported control characters.");
        }
        return normalized;
    }

    private static string RequiredId(string value, string label)
    {
        var normalized = (value ?? "").Trim();
        if (!Regex.IsMatch(normalized, "^[a-z0-9][a-z0-9._:-]{0,159}$", RegexOptions.CultureInvariant))
        {
            throw new ExperimentLabInputException($"{label} must be a lowercase stable ID using letters, digits, dot, underscore, colon, or hyphen.");
        }
        return normalized;
    }

    private static string? OptionalId(string value, string label) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredId(value, label);

    private static int ParseBoundedInt(string value, string label, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
        {
            throw new ExperimentLabInputException($"{label} must be from {minimum} through {maximum}.");
        }
        return parsed;
    }

    private static decimal ParseBoundedDecimal(string value, string label, decimal minimum, decimal maximum)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
        {
            throw new ExperimentLabInputException($"{label} must be from {minimum} through {maximum}.");
        }
        return parsed;
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ExperimentLabInputException("Experiment timestamps must be non-default UTC values.");
        }
    }

    private static void RequireValid(IArenaVersionedContract contract, string label)
    {
        var validation = ArenaContractCodec.Validate(contract);
        if (!validation.IsValid)
        {
            throw new ExperimentLabInputException(DiagnosticSummary(validation.Issues.Select(item => item.Code), $"{label} is invalid"));
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedFileAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > maximumBytes)
        {
            throw new ExperimentLabInputException($"Pack import requires 1-{maximumBytes} bytes.");
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static string WriteReceipt(string kind, ArenaArtifactWriteDisposition disposition, IEnumerable<ArenaArtifactDiagnostic> diagnostics)
    {
        var codes = diagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var action = disposition switch
        {
            ArenaArtifactWriteDisposition.Written => "Saved",
            ArenaArtifactWriteDisposition.Duplicate => "Already stored",
            _ => "Rejected"
        };
        return codes.Length == 0
            ? $"{action} 1 {kind}; receipt contains a relative artifact reference only."
            : $"{action} 1 {kind}; diagnostic code(s): {string.Join(", ", codes)}.";
    }

    private static string PackImportReceipt<T>(
        string kind,
        ArenaArtifactWriteResult<T> write,
        ArenaPackMigrationProvenance? migration)
        where T : class, IArenaVersionedContract
    {
        var receipt = WriteReceipt(kind, write.Disposition, write.Diagnostics);
        var durableMigration = write.Artifact switch
        {
            ArenaScenarioPackContract scenario => scenario.Migration,
            ArenaBenchmarkPackContract benchmark => benchmark.Migration,
            _ => migration
        };
        if (durableMigration is null)
        {
            return receipt;
        }

        return $"{receipt} Explicit migration receipt · {durableMigration.SourceSchema} {durableMigration.SourceVersion}"
            + $" · source SHA-256 {durableMigration.SourceContentFingerprint[..12]}…"
            + $" · {durableMigration.MigratorVersion} · {durableMigration.MigratedAtUtc:O}.";
    }

    private static string DiagnosticSummary(IEnumerable<string> codes, string prefix)
    {
        var bounded = codes.Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        return bounded.Length == 0 ? prefix + "." : $"{prefix}: {string.Join(", ", bounded)}.";
    }

    private static string StableId(string prefix, string value) =>
        $"{prefix}:{StableHash(value)[..24]}";

    private static string StableHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string SafeFileName(string value)
    {
        var safe = Regex.Replace(value, "[^a-zA-Z0-9._-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "artifact" : safe[..Math.Min(safe.Length, 80)];
    }
}

internal sealed class ExperimentLabInputException : Exception
{
    public ExperimentLabInputException(string message) : base(message)
    {
    }
}
