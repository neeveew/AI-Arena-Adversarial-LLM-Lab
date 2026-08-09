using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public enum ArenaExperimentExecutionAvailability
{
    Available,
    Unavailable
}

public enum ArenaExperimentExecutionDiagnosticSeverity
{
    Information,
    Error
}

public sealed record ArenaExperimentExecutionDiagnostic(
    string Code,
    ArenaExperimentExecutionDiagnosticSeverity Severity,
    string Summary);

/// <summary>
/// Content-free description of a persisted source session. It is suitable for
/// scenario-pack authoring without exposing the snapshot path or any session
/// content.
/// </summary>
public sealed record ArenaExperimentSessionSource(
    string MatchSetupReference,
    string SessionId,
    long PersistenceRevision,
    string SetupFingerprint);

public sealed record ArenaExperimentSessionSourceResolution(
    ArenaExperimentExecutionAvailability Availability,
    ArenaExperimentSessionSource? Source,
    ImmutableArray<ArenaExperimentExecutionDiagnostic> Diagnostics)
{
    public bool IsAvailable => Availability == ArenaExperimentExecutionAvailability.Available && Source is not null;
}

public sealed record ArenaResolvedRubricReference(
    string Id,
    string Version,
    string ContentFingerprint);

/// <summary>
/// Immutable, privacy-safe execution plan. Provider configurations, credentials,
/// snapshot paths, prompts, transcripts, and responses are deliberately absent.
/// </summary>
public sealed record ArenaExperimentExecutionPlan(
    string PlanFingerprint,
    ArenaExperimentContract Experiment,
    ArenaExperimentExpansion Expansion,
    string ScenarioPackId,
    string ScenarioPackFingerprint,
    string ScenarioId,
    string ScenarioVersion,
    string? BenchmarkPackId,
    string? BenchmarkPackFingerprint,
    string SourceSessionId,
    long SourcePersistenceRevision,
    string SourceSetupFingerprint,
    int TurnBudget,
    ImmutableArray<string> ProviderProfileIds,
    ImmutableArray<string> RubricIds,
    ImmutableArray<ArenaResolvedRubricReference> ResolvedRubrics,
    ImmutableArray<ArenaFaultProfileContract> FaultProfiles,
    string? BenchmarkCaseId);

public sealed record ArenaExperimentExecutionPlanResolution(
    ArenaExperimentExecutionAvailability Availability,
    ArenaExperimentExecutionPlan? Plan,
    ImmutableArray<ArenaExperimentExecutionDiagnostic> Diagnostics)
{
    public bool IsAvailable => Availability == ArenaExperimentExecutionAvailability.Available && Plan is not null;
}

/// <summary>
/// Bounded process-memory provider registry. The registry is never serialized by
/// the experimentation contracts and returns defensive copies so execution-time
/// transforms cannot mutate the configured profile.
/// </summary>
public sealed class ArenaExperimentProviderProfileRegistry
{
    public const int MaximumProfiles = 64;

    private readonly ImmutableDictionary<string, ModelProviderConfig> _profiles;
    private readonly ImmutableDictionary<string, string> _invalidProfiles;

    public ArenaExperimentProviderProfileRegistry(IReadOnlyDictionary<string, ModelProviderConfig> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count > MaximumProfiles)
        {
            throw new ArgumentOutOfRangeException(nameof(profiles), $"Provider profile count cannot exceed {MaximumProfiles}.");
        }

        var valid = ImmutableDictionary.CreateBuilder<string, ModelProviderConfig>(StringComparer.Ordinal);
        var invalid = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in profiles.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 160 || pair.Value is null)
            {
                throw new ArgumentException("Every provider profile requires a bounded non-empty identity and configuration.", nameof(profiles));
            }

            var validationCode = ValidateProfile(pair.Value);
            if (validationCode is null)
            {
                valid.Add(pair.Key, Copy(pair.Value));
            }
            else
            {
                invalid.Add(pair.Key, validationCode);
            }
        }

        _profiles = valid.ToImmutable();
        _invalidProfiles = invalid.ToImmutable();
    }

    public ImmutableArray<string> ProfileIds =>
        [.. _profiles.Keys.Concat(_invalidProfiles.Keys).OrderBy(item => item, StringComparer.Ordinal)];

    public bool Contains(string profileId) => _profiles.ContainsKey(profileId);

    internal bool TryGet(string profileId, out ModelProviderConfig config)
    {
        if (_profiles.TryGetValue(profileId, out var profile))
        {
            config = Copy(profile);
            return true;
        }

        config = new ModelProviderConfig();
        return false;
    }

    internal string? UnavailableReason(string profileId) =>
        _invalidProfiles.TryGetValue(profileId, out var code)
            ? code
            : _profiles.ContainsKey(profileId) ? null : "provider_profile_missing";

    internal string BehaviorFingerprint(string profileId)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
        {
            throw new KeyNotFoundException("Provider profile is unavailable.");
        }

        var descriptor = string.Join(
            "\n",
            profile.BaseUrl,
            profile.ApiMode,
            profile.Model,
            profile.Timeout.ToString(CultureInfo.InvariantCulture),
            profile.Temperature.ToString("R", CultureInfo.InvariantCulture),
            profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            profile.ContextLength.ToString(CultureInfo.InvariantCulture),
            profile.Reasoning,
            profile.NativeStatefulChat ? "1" : "0",
            profile.NativeIdleTtlSeconds.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    private static string? ValidateProfile(ModelProviderConfig profile)
    {
        if (profile.ApiMode is not (ModelProviderApiModes.OpenAiCompatible
            or ModelProviderApiModes.LmStudioNative
            or ModelProviderApiModes.OllamaNative
            or ModelProviderApiModes.LlamaCppNative))
        {
            return "provider_profile_api_mode_invalid";
        }
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "provider_profile_endpoint_invalid";
        }
        if (string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 1_024 || profile.Model.Any(char.IsControl))
        {
            return "provider_profile_model_invalid";
        }
        if (profile.Timeout is < 1 or > 3_600)
        {
            return "provider_profile_timeout_invalid";
        }
        if (!double.IsFinite(profile.Temperature) || profile.Temperature is < 0 or > 2)
        {
            return "provider_profile_temperature_invalid";
        }
        if (profile.MaxOutputTokens is < 1 or > 32_768)
        {
            return "provider_profile_output_limit_invalid";
        }
        if (profile.ContextLength is < 0 or > 1_048_576)
        {
            return "provider_profile_context_limit_invalid";
        }
        var reasoning = ModelProviderReasoningModes.Normalize(profile.Reasoning);
        if (!string.IsNullOrWhiteSpace(profile.Reasoning)
            && !reasoning.Equals(profile.Reasoning.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "provider_profile_reasoning_invalid";
        }
        if (profile.Extra is { Count: > 0 })
        {
            // Opaque extension values cannot be proven credential-free or
            // deterministically applied by the v1 experiment runtime.
            return "provider_profile_extensions_unsupported";
        }
        return null;
    }

    internal static ModelProviderConfig Copy(ModelProviderConfig source, string? apiToken = null) => new()
    {
        BaseUrl = source.BaseUrl,
        ApiMode = source.ApiMode,
        ApiToken = apiToken ?? source.ApiToken,
        Model = source.Model,
        Timeout = source.Timeout,
        Temperature = source.Temperature,
        MaxOutputTokens = source.MaxOutputTokens,
        ContextLength = source.ContextLength,
        Reasoning = source.Reasoning,
        NativeStatefulChat = source.NativeStatefulChat,
        NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
        PreviousResponseId = "",
        RequestInspectionContext = source.RequestInspectionContext,
        LastError = "",
        LastLatencyMs = 0,
        LastTestOk = false,
        Extra = null
    };
}

/// <summary>
/// Resolves pack, scenario, source-session, profile, and behavior-axis identity
/// before a provider call can be made. A missing or ambiguous dependency produces
/// an explicit unavailable result rather than a partially trusted plan.
/// </summary>
public sealed class ArenaExperimentExecutionResolver
{
    private static readonly IReadOnlySet<string> AllowedBehaviorParameters = new HashSet<string>(StringComparer.Ordinal)
    {
        "context_length",
        "max_output_tokens",
        "reasoning",
        "temperature",
        "timeout_seconds"
    };

    private readonly ArenaExperimentPackStore _packStore;
    private readonly SessionStore _sessionStore;
    private readonly ArenaExperimentProviderProfileRegistry _profiles;
    private readonly ImmutableDictionary<string, ArenaFaultProfileContract> _faultProfiles;
    private readonly ArenaRubricStore? _rubricStore;

    public ArenaExperimentExecutionResolver(
        ArenaExperimentPackStore packStore,
        SessionStore sessionStore,
        ArenaExperimentProviderProfileRegistry profiles,
        IReadOnlyDictionary<string, ArenaFaultProfileContract>? faultProfiles = null,
        ArenaRubricStore? rubricStore = null)
    {
        _packStore = packStore ?? throw new ArgumentNullException(nameof(packStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _rubricStore = rubricStore;
        var faultBuilder = ImmutableDictionary.CreateBuilder<string, ArenaFaultProfileContract>(StringComparer.Ordinal);
        foreach (var pair in (faultProfiles ?? new Dictionary<string, ArenaFaultProfileContract>())
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null
                || !pair.Key.Equals(pair.Value.Id, StringComparison.Ordinal)
                || !ArenaContractCodec.Validate(pair.Value).IsValid)
            {
                throw new ArgumentException("Fault-profile registry entries must match valid strict v1 contracts.", nameof(faultProfiles));
            }
            faultBuilder.Add(pair.Key, pair.Value);
        }
        _faultProfiles = faultBuilder.ToImmutable();
    }

    public async Task<ArenaExperimentSessionSourceResolution> ResolveSessionSourceAsync(
        string matchSetupReference,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<ArenaExperimentExecutionDiagnostic>();
        if (!TryParseSessionReference(matchSetupReference, out var sessionId))
        {
            diagnostics.Add(Error(
                "scenario_session_reference_invalid",
                "The scenario must use an explicit safe session:<id> match-setup reference."));
            return new(ArenaExperimentExecutionAvailability.Unavailable, null, Sort(diagnostics));
        }

        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            diagnostics.Add(Error(
                "scenario_session_unavailable",
                "The referenced source session is missing or unreadable."));
            return new(ArenaExperimentExecutionAvailability.Unavailable, null, Sort(diagnostics));
        }

        if (!snapshot.Engine.Agents.Any(agent => agent.Active))
        {
            diagnostics.Add(Error(
                "scenario_session_no_active_agents",
                "The referenced source session has no active agent to execute."));
            return new(ArenaExperimentExecutionAvailability.Unavailable, null, Sort(diagnostics));
        }

        var source = new ArenaExperimentSessionSource(
            $"session:{sessionId}",
            sessionId,
            Math.Max(0, snapshot.PersistenceRevision),
            SessionStore.SetupFingerprint(snapshot));
        return new(ArenaExperimentExecutionAvailability.Available, source, Sort(diagnostics));
    }

    public async Task<ArenaExperimentExecutionPlanResolution> ResolveAsync(
        ArenaExperimentContract experiment,
        string? selectedScenarioId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        var diagnostics = ImmutableArray.CreateBuilder<ArenaExperimentExecutionDiagnostic>();
        var validation = ArenaContractCodec.Validate(experiment);
        if (!validation.IsValid)
        {
            diagnostics.Add(Error(
                "experiment_contract_invalid",
                "The experiment does not satisfy the frozen v1 contract."));
            return Unavailable(diagnostics);
        }

        ArenaExperimentExpansion expansion;
        try
        {
            expansion = ExperimentExpander.Expand(experiment);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            diagnostics.Add(Error(
                "experiment_expansion_invalid",
                "The experiment matrix cannot be expanded within its deterministic safety bounds."));
            return Unavailable(diagnostics);
        }

        ValidateDimensions(experiment, diagnostics);
        foreach (var profileId in experiment.ProviderProfileIds)
        {
            if (_profiles.UnavailableReason(profileId) is { } reason)
            {
                diagnostics.Add(Error(reason, "A selected provider profile is missing or invalid in the process-memory registry."));
            }
        }
        var resolvedFaultProfiles = ImmutableArray.CreateBuilder<ArenaFaultProfileContract>();
        foreach (var faultProfileId in experiment.FaultProfileIds)
        {
            if (!_faultProfiles.TryGetValue(faultProfileId, out var faultProfile))
            {
                diagnostics.Add(Error(
                    "fault_profile_unavailable",
                    "A declared fault profile is absent from the bounded process-memory registry."));
            }
            else
            {
                resolvedFaultProfiles.Add(faultProfile);
                if (faultProfile.Injections.Any(injection =>
                    injection.Target is not (ArenaFaultTarget.Provider or ArenaFaultTarget.Network)))
                {
                    diagnostics.Add(Error(
                        "fault_profile_target_unsupported",
                        "The live experiment runtime currently supports provider and network fault targets only."));
                }
            }
        }
        var resolvedRubrics = ImmutableArray.CreateBuilder<ArenaResolvedRubricReference>();
        if (_rubricStore is null)
        {
            diagnostics.Add(Error(
                "rubric_store_unavailable",
                "Declared rubric requirements cannot be bound without the immutable rubric store."));
        }
        else
        {
            var rubricLoad = await _rubricStore.LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
            if (rubricLoad.Diagnostics.Any(item => item.Code == "artifact.duplicate_id"))
            {
                diagnostics.Add(Error(
                    "rubric_store_ambiguous",
                    "The immutable rubric store contains duplicate identities, so no rubric requirement can be bound safely."));
            }
            foreach (var rubricId in experiment.RubricIds)
            {
                var matches = rubricLoad.Artifacts.Where(item => item.Id.Equals(rubricId, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                {
                    diagnostics.Add(Error(
                        "rubric_contract_unavailable",
                        "A declared post-run rubric identity is missing or ambiguous in the immutable rubric store."));
                    continue;
                }

                var rubric = matches[0];
                var canonical = ArenaContractCodec.Serialize(rubric);
                resolvedRubrics.Add(new(
                    rubric.Id,
                    rubric.Version,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))));
            }
            if (resolvedRubrics.Count == experiment.RubricIds.Length)
            {
                diagnostics.Add(new(
                    "rubric_evaluation_required",
                    ArenaExperimentExecutionDiagnosticSeverity.Information,
                    "Exact immutable rubric identities are bound as explicit post-run evaluation requirements."));
            }
        }

        var scenarioLoad = await _packStore.LoadScenarioPacksAsync(cancellationToken).ConfigureAwait(false);
        if (scenarioLoad.Diagnostics.Any(item => item.Severity == ArenaArtifactDiagnosticSeverity.Error))
        {
            diagnostics.Add(new(
                "scenario_pack_store_invalid",
                ArenaExperimentExecutionDiagnosticSeverity.Information,
                "The scenario-pack store contains invalid artifacts; the exact requested identity is evaluated separately."));
        }
        var scenarioMatches = scenarioLoad.Artifacts
            .Where(item => item.Id.Equals(experiment.ScenarioPackId, StringComparison.Ordinal))
            .ToArray();
        var scenarioPack = scenarioMatches.Length == 1 ? scenarioMatches[0] : null;
        if (scenarioPack is null)
        {
            diagnostics.Add(Error(
                "scenario_pack_unavailable",
                "The experiment's exact scenario-pack identity is unavailable."));
            return Unavailable(diagnostics);
        }

        ArenaScenarioDefinition? scenario;
        if (string.IsNullOrWhiteSpace(selectedScenarioId))
        {
            scenario = scenarioPack.Scenarios.Length == 1 ? scenarioPack.Scenarios[0] : null;
            if (scenario is null)
            {
                diagnostics.Add(Error(
                    "scenario_selection_required",
                    "A scenario must be selected when the pack contains more than one definition."));
            }
        }
        else
        {
            scenario = scenarioPack.Scenarios.SingleOrDefault(item =>
                item.Id.Equals(selectedScenarioId, StringComparison.Ordinal));
            if (scenario is null)
            {
                diagnostics.Add(Error(
                    "scenario_selection_unavailable",
                    "The selected scenario is absent from the experiment's exact scenario pack."));
            }
        }

        ArenaBenchmarkPackContract? benchmarkPack = null;
        ArenaBenchmarkCase? benchmarkCase = null;
        if (experiment.BenchmarkPackId is not null)
        {
            var benchmarkLoad = await _packStore.LoadBenchmarkPacksAsync(cancellationToken).ConfigureAwait(false);
            if (benchmarkLoad.Diagnostics.Any(item => item.Severity == ArenaArtifactDiagnosticSeverity.Error))
            {
                diagnostics.Add(new(
                    "benchmark_pack_store_invalid",
                    ArenaExperimentExecutionDiagnosticSeverity.Information,
                    "The benchmark-pack store contains invalid artifacts; the exact requested identity is evaluated separately."));
            }
            var benchmarkMatches = benchmarkLoad.Artifacts
                .Where(item => item.Id.Equals(experiment.BenchmarkPackId, StringComparison.Ordinal))
                .ToArray();
            benchmarkPack = benchmarkMatches.Length == 1 ? benchmarkMatches[0] : null;
            if (benchmarkPack is null)
            {
                diagnostics.Add(Error(
                    "benchmark_pack_unavailable",
                    "The experiment's exact benchmark-pack identity is unavailable."));
            }
            else if (!benchmarkPack.ScenarioPackId.Equals(scenarioPack.Id, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "benchmark_scenario_pack_mismatch",
                    "The selected benchmark pack references a different scenario-pack identity."));
            }
            else if (scenario is not null
                && !benchmarkPack.Cases.Any(item => item.ScenarioId.Equals(scenario.Id, StringComparison.Ordinal)))
            {
                diagnostics.Add(Error(
                    "benchmark_scenario_unavailable",
                    "The selected scenario is absent from the exact benchmark pack."));
            }
            else if (scenario is not null)
            {
                var cases = benchmarkPack.Cases
                    .Where(item => item.ScenarioId.Equals(scenario.Id, StringComparison.Ordinal))
                    .ToArray();
                if (cases.Length != 1)
                {
                    diagnostics.Add(Error(
                        "benchmark_case_selection_ambiguous",
                        "The selected scenario must resolve to exactly one benchmark case."));
                }
                else
                {
                    benchmarkCase = cases[0];
                    if (benchmarkCase.Repetitions != experiment.Repetitions)
                    {
                        diagnostics.Add(Error(
                            "benchmark_repetition_mismatch",
                            "Experiment repetitions must match the selected benchmark case."));
                    }
                    if (benchmarkCase.RubricIds.Any(id => !experiment.RubricIds.Contains(id, StringComparer.Ordinal)))
                    {
                        diagnostics.Add(Error(
                            "benchmark_rubric_mismatch",
                            "Experiment rubric requirements do not cover the selected benchmark case."));
                    }
                    if (!benchmarkCase.RequiredProviderCapabilities.IsDefaultOrEmpty)
                    {
                        diagnostics.Add(Error(
                            "benchmark_provider_capability_unavailable",
                            "Provider capability requirements cannot be proven by the current profile registry."));
                    }
                }
            }
        }

        if (scenario is null || diagnostics.Any(item => item.Severity == ArenaExperimentExecutionDiagnosticSeverity.Error))
        {
            return Unavailable(diagnostics);
        }

        var sourceResolution = await ResolveSessionSourceAsync(scenario.MatchSetupReference, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(sourceResolution.Diagnostics);
        if (!sourceResolution.IsAvailable || sourceResolution.Source is null)
        {
            return Unavailable(diagnostics);
        }

        var source = sourceResolution.Source;
        if (!source.SetupFingerprint.Equals(scenario.SetupFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "scenario_setup_fingerprint_mismatch",
                "The referenced session no longer matches the scenario's setup fingerprint."));
            return Unavailable(diagnostics);
        }

        var turnBudget = Math.Min(experiment.TurnBudget, scenario.TurnBudget);
        if (turnBudget != experiment.TurnBudget)
        {
            diagnostics.Add(new(
                "experiment_turn_budget_bounded",
                ArenaExperimentExecutionDiagnosticSeverity.Information,
                "The scenario's lower turn budget bounds this experiment run."));
        }

        var planFingerprint = PlanFingerprint(
            expansion.ExperimentFingerprint,
            scenarioPack.ContentFingerprint,
            scenario,
            benchmarkPack,
            source,
            turnBudget,
            experiment.ProviderProfileIds.Select(id => (id, _profiles.BehaviorFingerprint(id))),
            resolvedRubrics,
            resolvedFaultProfiles);
        var plan = new ArenaExperimentExecutionPlan(
            planFingerprint,
            experiment,
            expansion,
            scenarioPack.Id,
            scenarioPack.ContentFingerprint,
            scenario.Id,
            scenario.Version,
            benchmarkPack?.Id,
            benchmarkPack?.ContentFingerprint,
            source.SessionId,
            source.PersistenceRevision,
            source.SetupFingerprint,
            turnBudget,
            experiment.ProviderProfileIds,
            experiment.RubricIds,
            resolvedRubrics
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToImmutableArray(),
            resolvedFaultProfiles
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToImmutableArray(),
            benchmarkCase?.Id);
        return new(ArenaExperimentExecutionAvailability.Available, plan, Sort(diagnostics));
    }

    internal static bool TryApplyBehaviorDimensions(
        ModelProviderConfig profile,
        ImmutableArray<ArenaExperimentVariantValue> values,
        out ModelProviderConfig config,
        out string failureCode)
    {
        var temperature = profile.Temperature;
        var maxOutputTokens = profile.MaxOutputTokens;
        var contextLength = profile.ContextLength;
        var reasoning = profile.Reasoning;
        var timeout = profile.Timeout;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.OrderBy(item => item.Parameter, StringComparer.Ordinal).ThenBy(item => item.DimensionId, StringComparer.Ordinal))
        {
            if (!AllowedBehaviorParameters.Contains(value.Parameter) || !seen.Add(value.Parameter))
            {
                config = new ModelProviderConfig();
                failureCode = "experiment_dimension_unsupported";
                return false;
            }

            switch (value.Parameter)
            {
                case "temperature":
                    if (!TryTemperature(value.Value, out temperature))
                    {
                        config = new ModelProviderConfig();
                        failureCode = "experiment_temperature_invalid";
                        return false;
                    }
                    break;
                case "max_output_tokens":
                    if (!TryInteger(value.Value, 1, 32_768, out maxOutputTokens))
                    {
                        config = new ModelProviderConfig();
                        failureCode = "experiment_output_limit_invalid";
                        return false;
                    }
                    break;
                case "context_length":
                    if (!TryInteger(value.Value, 0, 1_048_576, out contextLength))
                    {
                        config = new ModelProviderConfig();
                        failureCode = "experiment_context_limit_invalid";
                        return false;
                    }
                    break;
                case "reasoning":
                    var normalized = ModelProviderReasoningModes.Normalize(value.Value);
                    if (string.IsNullOrEmpty(normalized) || !normalized.Equals(value.Value, StringComparison.Ordinal))
                    {
                        config = new ModelProviderConfig();
                        failureCode = "experiment_reasoning_invalid";
                        return false;
                    }
                    reasoning = normalized;
                    break;
                case "timeout_seconds":
                    if (!TryInteger(value.Value, 1, 3_600, out timeout))
                    {
                        config = new ModelProviderConfig();
                        failureCode = "experiment_timeout_invalid";
                        return false;
                    }
                    break;
            }
        }

        config = new ModelProviderConfig
        {
            BaseUrl = profile.BaseUrl,
            ApiMode = profile.ApiMode,
            ApiToken = profile.ApiToken,
            Model = profile.Model,
            Timeout = timeout,
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            ContextLength = contextLength,
            Reasoning = reasoning,
            NativeStatefulChat = profile.NativeStatefulChat,
            NativeIdleTtlSeconds = profile.NativeIdleTtlSeconds
        };
        failureCode = "";
        return true;
    }

    private static void ValidateDimensions(
        ArenaExperimentContract experiment,
        ImmutableArray<ArenaExperimentExecutionDiagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dimension in experiment.Dimensions)
        {
            if (!AllowedBehaviorParameters.Contains(dimension.Parameter) || !seen.Add(dimension.Parameter))
            {
                diagnostics.Add(Error(
                    "experiment_dimension_unsupported",
                    "Experiment behavior axes must be unique and use the strict v1 allowlist."));
                continue;
            }

            var probe = new ModelProviderConfig
            {
                Model = "validation-probe",
                Timeout = 300,
                Temperature = 0.8,
                MaxOutputTokens = 1_024,
                ContextLength = 0
            };
            foreach (var value in dimension.Values)
            {
                if (!TryApplyBehaviorDimensions(
                    probe,
                    [new ArenaExperimentVariantValue(dimension.Id, dimension.Parameter, value)],
                    out _,
                    out var failureCode))
                {
                    diagnostics.Add(Error(
                        failureCode,
                        "An experiment behavior value is outside its strict bounded representation."));
                }
            }
        }
    }

    private static bool TryParseSessionReference(string value, out string sessionId)
    {
        const string prefix = "session:";
        sessionId = "";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = value[prefix.Length..];
        if (string.IsNullOrWhiteSpace(candidate)
            || !candidate.Equals(SessionStore.SafeSessionId(candidate), StringComparison.Ordinal)
            || candidate.Length > 96)
        {
            return false;
        }

        sessionId = candidate;
        return true;
    }

    private static bool TryTemperature(string value, out double result)
    {
        result = 0;
        return value.Equals(value.Trim(), StringComparison.Ordinal)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && double.IsFinite(result)
            && result is >= 0 and <= 2;
    }

    private static bool TryInteger(string value, int minimum, int maximum, out int result)
    {
        result = 0;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= minimum
            && result <= maximum
            && result.ToString(CultureInfo.InvariantCulture).Equals(value, StringComparison.Ordinal);
    }

    private static string PlanFingerprint(
        string experimentFingerprint,
        string scenarioPackFingerprint,
        ArenaScenarioDefinition scenario,
        ArenaBenchmarkPackContract? benchmark,
        ArenaExperimentSessionSource source,
        int turnBudget,
        IEnumerable<(string Id, string Fingerprint)> providerProfiles,
        IEnumerable<ArenaResolvedRubricReference> rubrics,
        IEnumerable<ArenaFaultProfileContract> faultProfiles)
    {
        var descriptor = new StringBuilder();
        descriptor.AppendLine(experimentFingerprint)
            .AppendLine(scenarioPackFingerprint)
            .AppendLine(scenario.Id)
            .AppendLine(scenario.Version)
            .AppendLine(benchmark?.ContentFingerprint ?? "")
            .AppendLine(source.SessionId)
            .AppendLine(source.PersistenceRevision.ToString(CultureInfo.InvariantCulture))
            .AppendLine(source.SetupFingerprint)
            .AppendLine(turnBudget.ToString(CultureInfo.InvariantCulture));
        foreach (var profile in providerProfiles.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            descriptor.Append("provider\0").Append(profile.Id).Append('\0').AppendLine(profile.Fingerprint);
        }
        foreach (var rubric in rubrics.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            descriptor.Append("rubric\0").Append(rubric.Id).Append('\0')
                .Append(rubric.Version).Append('\0').AppendLine(rubric.ContentFingerprint);
        }
        foreach (var fault in faultProfiles.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var canonical = ArenaContractCodec.Serialize(fault);
            descriptor.Append("fault\0").Append(fault.Id).Append('\0')
                .AppendLine(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString())));
    }

    private static ArenaExperimentExecutionPlanResolution Unavailable(
        ImmutableArray<ArenaExperimentExecutionDiagnostic>.Builder diagnostics) =>
        new(ArenaExperimentExecutionAvailability.Unavailable, null, Sort(diagnostics));

    private static ArenaExperimentExecutionDiagnostic Error(string code, string summary) =>
        new(code, ArenaExperimentExecutionDiagnosticSeverity.Error, summary);

    private static ImmutableArray<ArenaExperimentExecutionDiagnostic> Sort(
        ImmutableArray<ArenaExperimentExecutionDiagnostic>.Builder diagnostics) =>
        [.. diagnostics
            .Distinct()
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal)];
}

/// <summary>
/// Executes one matrix cell through the real session-fork and turn-runner path.
/// Only content-free observations are returned to ExperimentRunnerService.
/// </summary>
public sealed class ArenaExperimentCellExecutor : IArenaExperimentCellExecutor, IArenaExperimentExecutionPlanIdentity
{
    private readonly ArenaExperimentExecutionPlan _plan;
    private readonly ArenaExperimentProviderProfileRegistry _profiles;
    private readonly SessionStore _sessionStore;
    private readonly IModelProviderClient _providerClient;
    private readonly Func<int, string, CancellationToken, Task>? _turnCommittedObserver;

    public ArenaExperimentCellExecutor(
        ArenaExperimentExecutionPlan plan,
        ArenaExperimentProviderProfileRegistry profiles,
        SessionStore sessionStore,
        IModelProviderClient providerClient)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
    }

    internal ArenaExperimentCellExecutor(
        ArenaExperimentExecutionPlan plan,
        ArenaExperimentProviderProfileRegistry profiles,
        SessionStore sessionStore,
        IModelProviderClient providerClient,
        Func<int, string, CancellationToken, Task> turnCommittedObserver)
        : this(plan, profiles, sessionStore, providerClient)
    {
        _turnCommittedObserver = turnCommittedObserver
            ?? throw new ArgumentNullException(nameof(turnCommittedObserver));
    }

    public string PlanFingerprint => _plan.PlanFingerprint;

    public async Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
        ArenaExperimentCellExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PlanContains(context.Cell))
        {
            return ArenaExperimentCellExecutionResult.Failed(
                [FailureEvidence(context.TrialId, "execution_plan_mismatch")]);
        }
        if (!_profiles.TryGet(context.Cell.ProviderProfileId, out var profile))
        {
            return ArenaExperimentCellExecutionResult.Failed(
                [FailureEvidence(context.TrialId, "provider_profile_unavailable")]);
        }
        if (!ArenaExperimentExecutionResolver.TryApplyBehaviorDimensions(
            profile,
            context.Cell.Values,
            out var selectedConfig,
            out var dimensionFailure))
        {
            return ArenaExperimentCellExecutionResult.Failed(
                [FailureEvidence(context.TrialId, dimensionFailure)]);
        }

        var evidence = ImmutableArray.CreateBuilder<ArenaEvidenceAssertion>();
        evidence.Add(new ArenaEvidenceAssertion(
            ArenaExperimentRunPolicy.CreateExecutionPlanEvidenceId(context.TrialId),
            ArenaEvidenceState.Observed,
            "The trial used the resolved execution-plan identity.",
            $"plan:{_plan.PlanFingerprint}"));
        try
        {
            var targetId = ChildSessionId(context.TrialId);
            var tokenEmptyConfig = ArenaExperimentProviderProfileRegistry.Copy(selectedConfig, apiToken: "");
            var fork = await _sessionStore.ForkExperimentSessionAsync(
                _plan.SourceSessionId,
                targetId,
                _plan.SourcePersistenceRevision,
                _plan.SourceSetupFingerprint,
                _plan.Experiment.Id,
                tokenEmptyConfig,
                cancellationToken).ConfigureAwait(false);

            // The atomic experiment fork already contains its experiment identity
            // and token-empty provider setup when it becomes durable. Record that
            // ownership synchronously before the next cancellation point.
            evidence.Add(Observed(
                context.TrialId,
                "child_session",
                "An isolated child session was created for this trial.",
                $"session:{fork.TargetSessionId}"));
            if (!string.IsNullOrWhiteSpace(fork.BranchReceiptId))
            {
                evidence.Add(Observed(
                    context.TrialId,
                    "branch",
                    "The trial retained a branch receipt linked to its source revision.",
                    fork.BranchReceiptId));
            }

            var childGuard = new ArenaExperimentChildGuard(
                fork.TargetSessionId,
                _plan.Experiment.Id,
                _plan.SourceSessionId,
                _plan.SourcePersistenceRevision,
                _plan.SourceSetupFingerprint,
                fork.ChildSetupFingerprint);
            var committedRevision = fork.TargetPersistenceRevision;

            var credentialClient = new ExperimentCredentialProviderClient(_providerClient, selectedConfig.ApiToken);
            IModelProviderClient runtimeClient = credentialClient;
            var faultClients = new List<FaultInjectingModelProviderClient>();
            foreach (var faultProfile in _plan.FaultProfiles)
            {
                var faultClient = new FaultInjectingModelProviderClient(runtimeClient, faultProfile);
                faultClients.Add(faultClient);
                runtimeClient = faultClient;
            }
            var guardedClient = new ExperimentGuardedProviderClient(runtimeClient, _sessionStore, childGuard);
            runtimeClient = guardedClient;
            var eventLog = new EventLogStore(_sessionStore.DataRoot);
            using var internetTool = new InternetToolService(eventLogStore: eventLog);
            var turnRunner = new TurnRunnerService(
                runtimeClient,
                _sessionStore,
                eventLog,
                new TranscriptService(),
                internetTool);
            try
            {
                var completedTurns = 0;
                long promptTokens = 0;
                long completionTokens = 0;
                long totalTokens = 0;
                long latencyMilliseconds = 0;
                for (var turn = 0; turn < _plan.TurnBudget; turn++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    guardedClient.ExpectProviderRevision(checked(committedRevision + 1));
                    var result = await turnRunner.RunOneTurnAsync(fork.TargetSessionId, cancellationToken).ConfigureAwait(false);
                    committedRevision = await _sessionStore.ValidateExperimentChildAsync(
                        childGuard,
                        checked(committedRevision + 2),
                        cancellationToken).ConfigureAwait(false);
                    if (!result.Ok || !result.Executed || result.Completion is null || !result.Completion.Ok)
                    {
                        AppendFaultEvidence(evidence, context.TrialId, faultClients, _plan.FaultProfiles);
                        evidence.Add(FailureEvidence(
                            context.TrialId,
                            ClassifyProviderFailure(result.Completion?.Error ?? result.Error)));
                        evidence.Add(Observed(
                            context.TrialId,
                            "turn_progress",
                            $"{completedTurns} of {_plan.TurnBudget} configured turns completed before failure.",
                            context.TrialId));
                        return ArenaExperimentCellExecutionResult.Failed(evidence.ToImmutable());
                    }

                    completedTurns++;
                    if (_turnCommittedObserver is not null)
                    {
                        await _turnCommittedObserver(
                            completedTurns,
                            fork.TargetSessionId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    promptTokens += Math.Max(0, result.Completion.PromptTokens);
                    completionTokens += Math.Max(0, result.Completion.CompletionTokens);
                    totalTokens += Math.Max(0, result.Completion.TotalTokens);
                    latencyMilliseconds += Math.Max(0, result.Completion.LatencyMs);
                }

                AppendFaultEvidence(evidence, context.TrialId, faultClients, _plan.FaultProfiles);
                evidence.Add(Observed(
                    context.TrialId,
                    "turn_progress",
                    $"{completedTurns} of {_plan.TurnBudget} configured turns completed through the provider runtime.",
                    context.TrialId));
                if (promptTokens > 0 || completionTokens > 0 || totalTokens > 0 || latencyMilliseconds > 0)
                {
                    evidence.Add(Observed(
                        context.TrialId,
                        "provider_telemetry",
                        $"Adapter-exposed aggregate telemetry across {completedTurns} turn(s): prompt tokens {promptTokens}; generated tokens {completionTokens}; total tokens {totalTokens}; latency {latencyMilliseconds} ms.",
                        context.TrialId));
                }
                else
                {
                    evidence.Add(new ArenaEvidenceAssertion(
                        EvidenceId(context.TrialId, "provider_telemetry"),
                        ArenaEvidenceState.Unavailable,
                        "Adapter-exposed token and latency telemetry was unavailable for this completed trial.",
                        Limitation: "The provider adapter returned no positive telemetry values."));
                }

                return ArenaExperimentCellExecutionResult.Completed(evidence.ToImmutable());
            }
            finally
            {
                foreach (var faultClient in faultClients.AsEnumerable().Reverse())
                {
                    faultClient.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new ArenaExperimentCellExecutionCancelledException(evidence.ToImmutable(), cancellationToken);
        }
        catch (ArenaExperimentSourceChangedException)
        {
            evidence.Add(FailureEvidence(context.TrialId, "source_changed"));
            return ArenaExperimentCellExecutionResult.Interrupted("source_changed", evidence.ToImmutable());
        }
        catch (Exception exception) when (exception is ArenaExperimentChildDriftException or SnapshotConcurrencyException)
        {
            evidence.Add(FailureEvidence(context.TrialId, "child_state_changed"));
            return ArenaExperimentCellExecutionResult.Interrupted("child_state_changed", evidence.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            evidence.Add(FailureEvidence(context.TrialId, "session_persistence"));
            return ArenaExperimentCellExecutionResult.Failed(evidence.ToImmutable());
        }
        catch (Exception)
        {
            evidence.Add(FailureEvidence(context.TrialId, "runtime_failure"));
            return ArenaExperimentCellExecutionResult.Failed(evidence.ToImmutable());
        }
    }

    private bool PlanContains(ArenaExperimentCellPlan cell) =>
        cell.ExperimentId.Equals(_plan.Experiment.Id, StringComparison.Ordinal)
        && cell.ExperimentFingerprint.Equals(_plan.Expansion.ExperimentFingerprint, StringComparison.Ordinal)
        && _plan.ProviderProfileIds.Contains(cell.ProviderProfileId, StringComparer.Ordinal)
        && _plan.Expansion.Cells.Any(expected => expected == cell);

    private static string ChildSessionId(string trialId)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trialId)));
        return $"experiment-trial-{digest[..32]}";
    }

    private static ArenaEvidenceAssertion Observed(
        string trialId,
        string kind,
        string summary,
        string referenceId) =>
        new(EvidenceId(trialId, kind), ArenaEvidenceState.Observed, summary, referenceId);

    private static ArenaEvidenceAssertion FailureEvidence(string trialId, string classification) =>
        Observed(
            trialId,
            $"failure_{classification}",
            $"Experiment execution ended in the {classification.Replace('_', ' ')} classification.",
            trialId);

    private static void AppendFaultEvidence(
        ImmutableArray<ArenaEvidenceAssertion>.Builder evidence,
        string trialId,
        IEnumerable<FaultInjectingModelProviderClient> clients,
        IEnumerable<ArenaFaultProfileContract> profiles)
    {
        var observations = clients
            .SelectMany(client => client.SnapshotObservations())
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal)
            .ThenBy(item => item.Sequence)
            .ToArray();
        foreach (var observation in observations)
        {
            var cause = observation.CauseEvidence;
            evidence.Add(new ArenaEvidenceAssertion(
                EvidenceId(
                    trialId,
                    $"fault_{observation.ProfileId}_{observation.InjectionId}_{observation.Sequence}"),
                cause.State,
                cause.Summary,
                cause.ReferenceId,
                cause.Basis,
                cause.Limitation));
            var recovery = observation.RecoveryEvidence;
            evidence.Add(new ArenaEvidenceAssertion(
                EvidenceId(
                    trialId,
                    $"fault_recovery_{observation.ProfileId}_{observation.InjectionId}_{observation.Sequence}"),
                recovery.State,
                recovery.Summary,
                recovery.ReferenceId,
                recovery.Basis,
                recovery.Limitation));
        }

        var observedInjections = observations
            .Select(item => (item.ProfileId, item.InjectionId))
            .ToHashSet();
        foreach (var profile in profiles.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var injection in profile.Injections.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                if (observedInjections.Contains((profile.Id, injection.Id))) continue;
                var requiresStreaming = !FaultInjectingModelProviderClient.SupportsOperation(
                    ArenaProviderFaultOperation.ChatCompletion,
                    injection.Kind);
                evidence.Add(new ArenaEvidenceAssertion(
                    EvidenceId(trialId, $"fault_unexercised_{profile.Id}_{injection.Id}"),
                    ArenaEvidenceState.Unavailable,
                    $"The selected {FaultKindLabel(injection.Kind)} fault was not exercised by this experiment cell.",
                    ReferenceId: profile.Id,
                    Limitation: requiresStreaming
                        ? "Experiment cells use non-streaming provider completion; this fault requires a streaming completion boundary."
                        : "No compatible scheduled occurrence was observed during this experiment cell."));
            }
        }
    }

    private static string FaultKindLabel(ArenaFaultKind kind) => kind switch
    {
        ArenaFaultKind.Timeout => "timeout",
        ArenaFaultKind.Disconnect => "disconnect",
        ArenaFaultKind.MalformedStream => "malformed-stream",
        ArenaFaultKind.Saturation => "saturation",
        ArenaFaultKind.EmptyResponse => "empty-response",
        ArenaFaultKind.Interruption => "interruption",
        ArenaFaultKind.ContextPressure => "context-pressure",
        _ => "provider"
    };

    private static string EvidenceId(string trialId, string kind) =>
        $"evidence:{ExperimentExpander.Hash($"{trialId}\n{kind}")}";

    private static string ClassifyProviderFailure(string? error)
    {
        var value = error?.ToLowerInvariant() ?? "";
        if (value.Contains("timed out", StringComparison.Ordinal) || value.Contains("timeout", StringComparison.Ordinal))
        {
            return "provider_timeout";
        }
        if (value.Contains("queue full", StringComparison.Ordinal)
            || value.Contains("too many requests", StringComparison.Ordinal)
            || value.Contains("429", StringComparison.Ordinal)
            || value.Contains("saturat", StringComparison.Ordinal))
        {
            return "provider_saturation";
        }
        if (value.Contains("without assistant content", StringComparison.Ordinal)
            || value.Contains("empty", StringComparison.Ordinal))
        {
            return "provider_empty_response";
        }
        if (value.Contains("disconnect", StringComparison.Ordinal)
            || value.Contains("connection", StringComparison.Ordinal)
            || value.Contains("transport", StringComparison.Ordinal))
        {
            return "provider_transport";
        }
        if (value.Contains("http", StringComparison.Ordinal)
            || value.Contains("provider returned", StringComparison.Ordinal))
        {
            return "provider_rejected";
        }
        return "provider_failure";
    }

    private sealed class ExperimentGuardedProviderClient(
        IModelProviderClient inner,
        SessionStore sessionStore,
        ArenaExperimentChildGuard guard) : IModelProviderClient, IStreamingModelProviderClient
    {
        private long _expectedProviderRevision;

        internal void ExpectProviderRevision(long revision)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
            Volatile.Write(ref _expectedProviderRevision, revision);
        }

        public Task<ModelProviderModels> ListModelsAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken = default) =>
            inner.ListModelsAsync(config, cancellationToken);

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var expectedRevision = Volatile.Read(ref _expectedProviderRevision);
            if (expectedRevision < 1)
            {
                throw new ArenaExperimentChildDriftException();
            }
            using var providerLease = await sessionStore.AcquireExperimentProviderCallLeaseAsync(
                guard,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            return await inner.CompleteChatAsync(config, messages, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ModelCompletionResult> CompleteChatStreamingAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            var expectedRevision = Volatile.Read(ref _expectedProviderRevision);
            if (expectedRevision < 1)
            {
                throw new ArenaExperimentChildDriftException();
            }
            using var providerLease = await sessionStore.AcquireExperimentProviderCallLeaseAsync(
                guard,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            return inner is IStreamingModelProviderClient streaming
                ? await streaming.CompleteChatStreamingAsync(config, messages, progress, cancellationToken).ConfigureAwait(false)
                : await inner.CompleteChatAsync(config, messages, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ExperimentCredentialProviderClient(
        IModelProviderClient inner,
        string apiToken) : IModelProviderClient, IStreamingModelProviderClient
    {
        public Task<ModelProviderModels> ListModelsAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken = default) =>
            inner.ListModelsAsync(WithCredential(config), cancellationToken);

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default) =>
            inner.CompleteChatAsync(WithCredential(config), messages, cancellationToken);

        public Task<ModelCompletionResult> CompleteChatStreamingAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            if (inner is IStreamingModelProviderClient streaming)
            {
                return streaming.CompleteChatStreamingAsync(WithCredential(config), messages, progress, cancellationToken);
            }
            return inner.CompleteChatAsync(WithCredential(config), messages, cancellationToken);
        }

        private ModelProviderConfig WithCredential(ModelProviderConfig config) =>
            ArenaExperimentProviderProfileRegistry.Copy(config, apiToken);
    }
}
