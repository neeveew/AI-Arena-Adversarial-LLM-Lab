using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed record AIArenaMatchSetupPackageState(
    string SessionId,
    string Schema,
    string Name,
    string Fingerprint,
    int CastCount,
    int RelationshipCount,
    bool InternetEnabled,
    string Json)
{
    public bool FactoryMode { get; init; }
}

internal sealed record AIArenaMatchSetupPackageReceipt(
    string Operation,
    string SourceSessionId,
    string TargetSessionId,
    string Fingerprint,
    IReadOnlyList<string> Warnings);

internal sealed record AIArenaMatchSetupPackageResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaMatchSetupPackageState? State,
    AIArenaMatchSetupPackageReceipt? Receipt);

/// <summary>
/// Owns portable Match Setup export/import. Imports always create a clean session,
/// never overwrite the active run, and never serialize provider API tokens.
/// </summary>
internal sealed class MatchSetupPortabilityService
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string?, CancellationToken, Task> loadSessionsAsync;
    private readonly SemaphoreSlim importGate = new(1, 1);
    private readonly SemaphoreSlim? arenaOperationLock;
    private readonly Func<IReadOnlyList<WpfProviderModelSettings>> portableModelSettings;

    public MatchSetupPortabilityService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        SemaphoreSlim? arenaOperationLock = null,
        Func<IReadOnlyList<WpfProviderModelSettings>>? portableModelSettings = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.loadSessionsAsync = loadSessionsAsync;
        this.arenaOperationLock = arenaOperationLock;
        this.portableModelSettings = portableModelSettings ?? (() => []);
    }

    public async Task<AIArenaMatchSetupPackageResult> ExportAsync(CancellationToken cancellationToken = default)
    {
        var session = activeSession();
        if (session is null)
        {
            return Failure("not_available", "No active session is available to export.");
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        if (snapshot is null)
        {
            return Failure("not_available", $"Session '{session.Id}' has no snapshot to export.");
        }

        var package = MatchSetupPackageCodec.FromSnapshot(session.Id, snapshot, portableModelSettings());
        var validation = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(package));
        if (!validation.Ok || validation.Package is null)
        {
            return Failure("invalid_setup", $"The active Match Setup cannot be exported as a portable package. {validation.Message}");
        }

        var state = MatchSetupPackageCodec.ToState(session.Id, validation.Package);
        return Success(
            "Exported the active Match Setup as a portable JSON package. Provider API tokens and runtime group history were excluded.",
            state,
            new AIArenaMatchSetupPackageReceipt("export", session.Id, "", state.Fingerprint, validation.Warnings));
    }

    public async Task<AIArenaMatchSetupPackageResult> ImportAsync(
        string json,
        string requestedName,
        CancellationToken cancellationToken = default)
    {
        if (isArenaBusy())
        {
            return Failure("not_available", "Match Setup import is unavailable while the arena is busy.");
        }

        await importGate.WaitAsync(cancellationToken);
        var arenaLockTaken = false;
        try
        {
            if (arenaOperationLock is not null)
            {
                await arenaOperationLock.WaitAsync(cancellationToken);
                arenaLockTaken = true;
            }

            return await ImportCoreAsync(json, requestedName, cancellationToken);
        }
        finally
        {
            if (arenaLockTaken)
            {
                arenaOperationLock!.Release();
            }

            importGate.Release();
        }
    }

    private async Task<AIArenaMatchSetupPackageResult> ImportCoreAsync(
        string json,
        string requestedName,
        CancellationToken cancellationToken)
    {
        if (isArenaBusy())
        {
            return Failure("not_available", "Match Setup import is unavailable while the arena is busy.");
        }

        var source = activeSession();
        if (source is null)
        {
            return Failure("not_available", "No active session is available as the trusted provider baseline.");
        }

        var parsed = MatchSetupPackageCodec.Parse(json);
        if (!parsed.Ok || parsed.Package is null)
        {
            return Failure(parsed.ErrorCode, parsed.Message);
        }

        var sourceSnapshot = await sessionStore.LoadSnapshotAsync(source.Id, cancellationToken);
        if (sourceSnapshot is null)
        {
            return Failure("not_available", $"Session '{source.Id}' has no snapshot to use as a provider baseline.");
        }

        var sessions = await sessionStore.ListSessionsAsync(SessionListingDetail.Identity, cancellationToken);
        var targetSessionId = UniqueSessionId(requestedName, parsed.Package.Metadata.Name, sessions.Select(item => item.Id));
        var target = SessionStore.CreateDefaultSnapshot();
        target.Configs.Clear();
        foreach (var (key, config) in sourceSnapshot.Configs)
        {
            target.Configs[key] = CloneConfig(config);
        }

        var apply = MatchSetupPackageCodec.Apply(parsed.Package, target, sourceSnapshot.Configs);
        if (!apply.Ok)
        {
            return Failure("invalid_package", apply.Message);
        }

        var created = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (await sessionStore.TryCreateSessionAsync(targetSessionId, target, cancellationToken))
            {
                created = true;
                break;
            }

            sessions = await sessionStore.ListSessionsAsync(SessionListingDetail.Identity, cancellationToken);
            targetSessionId = UniqueSessionId(requestedName, parsed.Package.Metadata.Name, sessions.Select(item => item.Id));
        }
        if (!created)
        {
            return Failure("conflict", "A collision-free session id could not be reserved; retry the import.");
        }

        var importedPortableSettings = parsed.Package.Setup.ModelSettings
            .Select(setting => new WpfProviderModelSettings
            {
                Model = setting.Model,
                ConfiguredContextWindow = setting.ConfiguredContextWindow,
                HistoryPolicy = setting.HistoryPolicy,
                ResponseTone = setting.ResponseTone,
                CustomTone = setting.CustomTone,
                PendingApply = setting.PendingApply
            })
            .ToArray();
        var importedPackage = MatchSetupPackageCodec.FromSnapshot(targetSessionId, target, importedPortableSettings);
        var state = MatchSetupPackageCodec.ToState(targetSessionId, importedPackage);
        var warnings = parsed.Warnings.Concat(apply.Warnings).Distinct(StringComparer.Ordinal).ToList();
        if (state.FactoryMode)
        {
            warnings.Add(
                "Factory public group history is runtime state and was not imported. Send a public Operator turn to establish a new conversation root before running participants.");
        }
        try
        {
            await eventLogStore.AppendAsync(targetSessionId, "control_match_setup_imported", new
            {
                sourceSessionId = source.Id,
                targetSessionId,
                state.Schema,
                state.Fingerprint,
                warnings
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("The clean session was created, but its import audit event could not be written.");
        }
        try
        {
            await loadSessionsAsync(targetSessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("The clean session was created, but it could not be selected automatically.");
        }

        var message = warnings.Count == 0
            ? $"Imported Match Setup into clean session '{targetSessionId}'."
            : $"Imported Match Setup into clean session '{targetSessionId}' with {warnings.Count} warning(s).";
        return Success(
            message,
            state,
            new AIArenaMatchSetupPackageReceipt("import", source.Id, targetSessionId, state.Fingerprint, warnings.ToArray()));
    }

    public async Task<AIArenaMatchSetupPackageResult> ImportFileAsync(
        string path,
        string requestedName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("missing_argument", "match.setup.import requires args.json or args.path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                "invalid_path",
                AppErrorPresenter.Present(ex, AppErrorContext.FileTransfer).DisplayText);
        }

        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("invalid_path", "Match Setup import path must end in .json.");
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return Failure("not_found", "The Match Setup package was not found at the selected location.");
            }

            if (file.Length > MatchSetupPackageCodec.MaxPackageBytes)
            {
                return Failure("invalid_package", $"Match Setup package exceeds the {MatchSetupPackageCodec.MaxPackageBytes:N0}-byte limit.");
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return await ImportAsync(json, requestedName, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "read_failed",
                AppErrorPresenter.Present(ex, AppErrorContext.FileTransfer).DisplayText);
        }
    }

    private static string UniqueSessionId(string requestedName, string packageName, IEnumerable<string> existingIds)
    {
        var preferred = string.IsNullOrWhiteSpace(requestedName)
            ? string.IsNullOrWhiteSpace(packageName) ? "imported-setup" : $"{packageName}-import"
            : requestedName;
        var root = SessionStore.SafeSessionId(preferred);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "imported-setup";
        }

        var existing = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(root))
        {
            return root;
        }

        for (var suffix = 2; suffix <= 999; suffix++)
        {
            var candidate = SessionStore.SafeSessionId($"{root}-{suffix}");
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return SessionStore.SafeSessionId($"{root}-{Guid.NewGuid():N}");
    }

    private static ModelProviderConfig CloneConfig(ModelProviderConfig config) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        ExplicitModelAssignment = config.ExplicitModelAssignment
    };

    private static AIArenaMatchSetupPackageResult Success(
        string message,
        AIArenaMatchSetupPackageState state,
        AIArenaMatchSetupPackageReceipt receipt) => new(true, "", message, state, receipt);

    private static AIArenaMatchSetupPackageResult Failure(string errorCode, string message) =>
        new(false, errorCode, message, null, null);
}

internal static class MatchSetupPackageCodec
{
    public const string Schema = "ai_arena.match_setup.v4";
    public const string LegacySchemaV3 = "ai_arena.match_setup.v3";
    public const string LegacySchema = "ai_arena.match_setup.v2";
    public const string FactoryConversationContract = FactoryConversationService.ContractVersion;
    public const int MaxPackageBytes = 512 * 1024;
    private const int MaxPackageChars = 512 * 1024;
    private const int MaxTextChars = 20_000;
    private const int MaxShortTextChars = 512;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonOptions) { WriteIndented = false };

    internal sealed record ParseResult(
        bool Ok,
        string ErrorCode,
        string Message,
        MatchSetupPackage? Package,
        IReadOnlyList<string> Warnings);

    internal sealed record ApplyResult(bool Ok, string Message, IReadOnlyList<string> Warnings);

    public static MatchSetupPackage FromSnapshot(
        string sessionId,
        ArenaSnapshot snapshot,
        IReadOnlyList<WpfProviderModelSettings>? portableModelSettings = null)
    {
        var activeAgents = snapshot.Engine.Agents
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .OrderBy(agent => AgentRosterService.ParticipantOrder(agent.Id))
            .Select(agent => new MatchSetupAgentPackage
            {
                Id = agent.Id.Trim().ToLowerInvariant(),
                Name = agent.Name,
                Persona = agent.Persona,
                VoiceStyle = agent.VoiceStyle,
                PressureProfile = agent.PressureProfile,
                AccentColor = AgentAccentService.NormalizeColor(agent.AccentColor)
            })
            .ToList();
        var activeIds = activeAgents.Select(agent => agent.Id).ToArray();
        var relationshipPlan = MatchSetupCoordinator.BuildRivalryMatrixPlan(
            snapshot.Engine.RivalryMatrix.Links.Select(link => new Models.RivalryMatrixItem(link.Source, link.Target, link.Stance)),
            activeIds);
        var locks = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "topic", "global", "narrator" }.Concat(activeIds))
        {
            locks[key] = snapshot.MatchLocks.TryGetValue(key, out var locked) && locked;
        }

        var providers = new SortedDictionary<string, MatchSetupProviderPackage>(StringComparer.OrdinalIgnoreCase);
        var sharedModel = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var sharedConfig)
            ? PortableModelIdentifier(sharedConfig.Model)
            : "";
        foreach (var (key, config) in snapshot.Configs
                     .Where(item => IsSupportedProviderKey(item.Key, activeIds))
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var resolvedConfig = ModelRuntimeSettingsRegistry.Resolve(snapshot, config);
            var normalizedKey = key.Trim().ToLowerInvariant();
            var assignmentMode = normalizedKey.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                ? MatchSetupProviderAssignmentModes.Inherit
                : config.ExplicitModelAssignment
                  || !string.IsNullOrWhiteSpace(config.Model)
                  && !config.Model.Trim().Equals(sharedModel, StringComparison.Ordinal)
                    ? MatchSetupProviderAssignmentModes.Explicit
                    : MatchSetupProviderAssignmentModes.Inherit;
            providers[normalizedKey] = new MatchSetupProviderPackage
            {
                BaseUrl = SanitizeProviderBaseUrl(config.BaseUrl),
                ApiMode = ModelProviderApiModes.Normalize(config.ApiMode),
                Model = assignmentMode.Equals(MatchSetupProviderAssignmentModes.Explicit, StringComparison.Ordinal)
                    || normalizedKey.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                        ? PortableModelIdentifier(config.Model)
                        : sharedModel,
                TimeoutSeconds = ArenaSessionMutationCoordinator.ClampTimeout(config.Timeout),
                Temperature = ArenaSessionMutationCoordinator.ClampTemperature(config.Temperature),
                MaxOutputTokens = ArenaSessionMutationCoordinator.ClampMaxOutput(config.MaxOutputTokens),
                ContextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(config.ContextLength),
                ConfiguredContextWindow = NormalizeConfiguredContextWindow(resolvedConfig.ConfiguredContextWindow),
                HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(resolvedConfig.HistoryPolicy),
                ResponseTone = ModelResponseTones.NormalizeResponseTone(resolvedConfig.ResponseTone),
                CustomTone = ModelResponseTones.NormalizeResponseTone(resolvedConfig.ResponseTone) == ModelResponseTones.Custom
                    ? ModelResponseTones.NormalizeCustomTone(resolvedConfig.CustomTone)
                    : "",
                Reasoning = ModelProviderReasoningModes.Normalize(config.Reasoning),
                NativeStatefulChat = config.NativeStatefulChat,
                NativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(config.NativeIdleTtlSeconds),
                AssignmentMode = assignmentMode
            };
        }

        var portableSettingsByIdentity = new SortedDictionary<string, MatchSetupModelSettingsPackage>(StringComparer.Ordinal);
        void AddPortableModelSettings(
            string model,
            int configuredContextWindow,
            string historyPolicy,
            string responseTone,
            string customTone,
            bool pendingApply)
        {
            var safeModel = PortableModelIdentifier(model);
            if (!IsPortableModelIdentifier(safeModel))
            {
                return;
            }

            var identity = ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
            {
                BaseUrl = sharedConfig?.BaseUrl ?? ModelProviderDefaults.BaseUrl,
                ApiMode = sharedConfig?.ApiMode ?? ModelProviderApiModes.OpenAiCompatible,
                Model = safeModel
            });
            var normalizedTone = ModelResponseTones.NormalizeResponseTone(responseTone);
            var existingPending = portableSettingsByIdentity.TryGetValue(identity, out var existing)
                && existing.PendingApply;
            portableSettingsByIdentity[identity] = new MatchSetupModelSettingsPackage
            {
                Model = safeModel,
                ConfiguredContextWindow = NormalizeConfiguredContextWindow(configuredContextWindow),
                HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(historyPolicy),
                ResponseTone = normalizedTone,
                CustomTone = normalizedTone == ModelResponseTones.Custom
                    ? ModelResponseTones.NormalizeCustomTone(customTone)
                    : "",
                PendingApply = pendingApply || existingPending
            };
        }

        foreach (var config in snapshot.Configs.Values.Where(config => !string.IsNullOrWhiteSpace(config.Model)))
        {
            var resolved = ModelRuntimeSettingsRegistry.Resolve(snapshot, config);
            AddPortableModelSettings(
                config.Model,
                resolved.ConfiguredContextWindow,
                resolved.HistoryPolicy,
                resolved.ResponseTone,
                resolved.CustomTone,
                snapshot.PendingModelConfigurationApplies.Contains(ModelRuntimeSettingsRegistry.Identity(config)));
        }
        foreach (var setting in (portableModelSettings ?? []).Take(256))
        {
            AddPortableModelSettings(
                setting.Model,
                setting.ConfiguredContextWindow,
                setting.HistoryPolicy,
                setting.ResponseTone,
                setting.CustomTone,
                setting.PendingApply);
        }

        return new MatchSetupPackage
        {
            Metadata = new MatchSetupMetadataPackage { Name = sessionId },
            Setup = new MatchSetupDefinitionPackage
            {
                MatchType = snapshot.MatchType,
                FactoryMode = snapshot.Engine.FactoryMode,
                DefaultForUnassignedAgentsEnabled = snapshot.Engine.DefaultForUnassignedAgentsEnabled,
                Scenario = new MatchSetupScenarioPackage
                {
                    Topic = snapshot.Engine.Steering.Topic,
                    Global = snapshot.Engine.Steering.Global
                },
                Generation = new MatchSetupGenerationPackage
                {
                    ScenarioStyle = snapshot.ScenarioGenerator.Style,
                    ScenarioSeed = snapshot.ScenarioGenerator.Seed,
                    Intensity = snapshot.ScenarioGenerator.Intensity,
                    RolePack = snapshot.ScenarioGenerator.RolePack,
                    Absurdity = snapshot.ScenarioGenerator.Absurdity,
                    ApplyOnReset = snapshot.ScenarioGenerator.ApplyOnReset,
                    PersonaStyle = snapshot.PersonaRandomizer.Style,
                    PersonaSeed = snapshot.PersonaRandomizer.Seed,
                    PersonaApplyOnReset = snapshot.PersonaRandomizer.ApplyOnReset
                },
                Cast = activeAgents,
                Narrator = new MatchSetupNarratorPackage
                {
                    Persona = snapshot.Engine.Narrator.Persona,
                    VoiceStyle = snapshot.Engine.Narrator.VoiceStyle,
                    AccentColor = AgentAccentService.NormalizeColor(snapshot.Engine.Narrator.AccentColor),
                    Cadence = Math.Clamp(snapshot.Engine.Narrator.Cadence, 0, 1000),
                    InspectPrivateNotes = snapshot.Engine.Narrator.InspectPrivateNotes
                },
                Locks = locks,
                Relationship = new MatchSetupRelationshipPackage
                {
                    Enabled = snapshot.Engine.RivalryMatrix.Enabled,
                    Links = relationshipPlan.Links.Select(link => new MatchSetupRelationshipLinkPackage
                    {
                        Source = link.Source,
                        Target = link.Target,
                        Stance = link.Stance
                    }).ToList()
                },
                Context = new MatchSetupContextPackage
                {
                    TranscriptWindow = Math.Clamp(snapshot.Engine.TranscriptWindow, 1, 60),
                    PrivateWindow = Math.Clamp(snapshot.Engine.PrivateWindow, 0, 60),
                    NotesWindow = Math.Clamp(snapshot.Engine.NotesWindow, 0, 60)
                },
                Internet = new MatchSetupInternetPackage
                {
                    Enabled = snapshot.Engine.Internet.UseInternet,
                    MaxResults = Math.Clamp(snapshot.Engine.Internet.MaxResults, 1, 10),
                    SourceFreshnessMinutes = Math.Clamp(snapshot.Engine.Internet.SourceFreshnessMinutes, 1, 1440)
                },
                Providers = providers,
                ModelSettings = portableSettingsByIdentity.Values.ToList()
            }
        };
    }

    public static string Serialize(MatchSetupPackage package) => JsonSerializer.Serialize(package, JsonOptions);

    public static AIArenaMatchSetupPackageState ToState(string sessionId, MatchSetupPackage package)
    {
        var json = Serialize(package);
        return new AIArenaMatchSetupPackageState(
            sessionId,
            package.Schema,
            package.Metadata.Name,
            Fingerprint(package),
            package.Setup.Cast.Count,
            package.Setup.Relationship.Links.Count,
            package.Setup.Internet.Enabled,
            json)
        {
            FactoryMode = package.Setup.FactoryMode
        };
    }

    public static string Fingerprint(MatchSetupPackage package)
    {
        var setup = JsonSerializer.Serialize(package.Setup, CanonicalJsonOptions);
        var canonical = package.Setup.FactoryMode
            ? $"factory-conversation-contract|{FactoryConversationContract}\n{setup}"
            : setup;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static ParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("missing_argument", "A portable Match Setup JSON package is required.");
        }

        if (json.Length > MaxPackageChars)
        {
            return Invalid("invalid_package", $"Match Setup package exceeds the {MaxPackageChars:N0}-character limit.");
        }

        MatchSetupPackage? package;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (TryFindDuplicateProperty(document.RootElement, "$", out var duplicatePath))
            {
                return Invalid("invalid_json", $"Match Setup JSON contains a duplicate property at {duplicatePath}.");
            }

            package = document.RootElement.Deserialize<MatchSetupPackage>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return Invalid(
                "invalid_json",
                AppErrorPresenter.Present(ex, AppErrorContext.FileTransfer).DisplayText);
        }

        if (package is null)
        {
            return Invalid("invalid_package", "Match Setup JSON did not contain a package.");
        }

        var legacyV2 = string.Equals(package.Schema, LegacySchema, StringComparison.Ordinal);
        var legacyV3 = string.Equals(package.Schema, LegacySchemaV3, StringComparison.Ordinal);
        if (!legacyV2 && !legacyV3 && !string.Equals(package.Schema, Schema, StringComparison.Ordinal))
        {
            return Invalid(
                "unsupported_schema",
                $"Unsupported Match Setup schema '{package.Schema}'. Expected '{Schema}' or legacy '{LegacySchemaV3}'/'{LegacySchema}'.");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        if (legacyV2)
        {
            UpgradeLegacyV2(package);
            warnings.Add(
                "Legacy Match Setup v2 was upgraded to v4. Default-for-unassigned remains enabled, role assignment modes were inferred, and model behavior keeps strict history with the legacy context window.");
        }
        else if (legacyV3)
        {
            UpgradeLegacyV3(package);
            warnings.Add(
                "Legacy Match Setup v3 was upgraded to v4. Model behavior keeps strict history with the legacy context window and default response tone.");
        }
        Validate(package, errors, warnings);
        if (errors.Count > 0)
        {
            return Invalid("invalid_package", string.Join(" ", errors));
        }

        return new ParseResult(true, "", "Match Setup package validated.", package, warnings);
    }

    private static bool TryFindDuplicateProperty(JsonElement element, string path, out string duplicatePath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                {
                    duplicatePath = propertyPath;
                    return true;
                }

                if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicatePath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDuplicateProperty(item, $"{path}[{index++}]", out duplicatePath))
                {
                    return true;
                }
            }
        }

        duplicatePath = "";
        return false;
    }

    public static ApplyResult Apply(
        MatchSetupPackage package,
        ArenaSnapshot target,
        IReadOnlyDictionary<string, ModelProviderConfig> trustedConfigs)
    {
        var errors = new List<string>();
        var validationWarnings = new List<string>();
        Validate(package, errors, validationWarnings);
        if (errors.Count > 0)
        {
            return new ApplyResult(false, string.Join(" ", errors), validationWarnings);
        }

        var setup = package.Setup;
        target.MatchType = setup.MatchType.Trim();
        target.Engine.FactoryMode = setup.FactoryMode;
        target.Engine.DefaultForUnassignedAgentsEnabled = setup.DefaultForUnassignedAgentsEnabled;
        target.Engine.Steering.Topic = setup.Scenario.Topic;
        target.Engine.Steering.Global = setup.Scenario.Global;
        target.ScenarioGenerator.Style = setup.Generation.ScenarioStyle;
        target.ScenarioGenerator.Seed = setup.Generation.ScenarioSeed;
        target.ScenarioGenerator.Intensity = setup.Generation.Intensity;
        target.ScenarioGenerator.RolePack = setup.Generation.RolePack;
        target.ScenarioGenerator.Absurdity = setup.Generation.Absurdity;
        target.ScenarioGenerator.ApplyOnReset = setup.Generation.ApplyOnReset;
        target.PersonaRandomizer.Style = setup.Generation.PersonaStyle;
        target.PersonaRandomizer.Seed = setup.Generation.PersonaSeed;
        target.PersonaRandomizer.Intensity = setup.Generation.Intensity;
        target.PersonaRandomizer.RolePack = setup.Generation.RolePack;
        target.PersonaRandomizer.Absurdity = setup.Generation.Absurdity;
        target.PersonaRandomizer.ApplyOnReset = setup.Generation.PersonaApplyOnReset;

        var desiredIds = setup.Cast.Select(agent => agent.Id.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        target.Engine.Agents.RemoveAll(agent => AgentRosterService.IsParticipantId(agent.Id) && !desiredIds.Contains(agent.Id));
        foreach (var definition in setup.Cast.OrderBy(agent => AgentRosterService.ParticipantOrder(agent.Id)))
        {
            var id = definition.Id.Trim().ToLowerInvariant();
            var agent = target.Engine.Agents.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                agent = AgentRosterService.CreateDefaultAgent(id);
                target.Engine.Agents.Add(agent);
            }

            agent.Name = definition.Name.Trim();
            agent.Persona = definition.Persona;
            agent.VoiceStyle = definition.VoiceStyle;
            agent.PressureProfile = definition.PressureProfile;
            agent.AccentColor = AgentAccentService.NormalizeColor(definition.AccentColor);
            agent.Active = true;
            agent.Status = "waiting";
            agent.PrivateNotes.Clear();
        }

        target.Engine.Agents.Sort((left, right) => AgentRosterService.ParticipantOrder(left.Id).CompareTo(AgentRosterService.ParticipantOrder(right.Id)));
        target.Engine.Narrator.Persona = setup.Narrator.Persona;
        target.Engine.Narrator.VoiceStyle = setup.Narrator.VoiceStyle;
        target.Engine.Narrator.AccentColor = AgentAccentService.NormalizeColor(setup.Narrator.AccentColor);
        target.Engine.Narrator.Cadence = Math.Clamp(setup.Narrator.Cadence, 0, 1000);
        target.Engine.Narrator.InspectPrivateNotes = setup.Narrator.InspectPrivateNotes;
        target.Engine.Narrator.Status = "idle";
        target.Engine.Narrator.LastError = "";

        target.MatchLocks.Clear();
        foreach (var key in new[] { "topic", "global", "narrator" }.Concat(desiredIds).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            target.MatchLocks[key] = setup.Locks.TryGetValue(key, out var locked) && locked;
        }

        var plan = MatchSetupCoordinator.BuildRivalryMatrixPlan(
            setup.Relationship.Links.Select(link => new Models.RivalryMatrixItem(link.Source, link.Target, link.Stance)),
            desiredIds);
        target.Engine.RivalryMatrix.Enabled = setup.Relationship.Enabled;
        target.Engine.RivalryMatrix.Links.Clear();
        target.Engine.RivalryMatrix.Links.AddRange(plan.Links.Select(link => new RivalryLink
        {
            Source = link.Source,
            Target = link.Target,
            Stance = link.Stance
        }));

        target.Engine.TranscriptWindow = Math.Clamp(setup.Context.TranscriptWindow, 1, 60);
        target.Engine.PrivateWindow = Math.Clamp(setup.Context.PrivateWindow, 0, 60);
        target.Engine.NotesWindow = Math.Clamp(setup.Context.NotesWindow, 0, 60);
        target.Engine.Internet.UseInternet = setup.Internet.Enabled;
        target.Engine.Internet.MaxResults = Math.Clamp(setup.Internet.MaxResults, 1, 10);
        target.Engine.Internet.SourceFreshnessMinutes = Math.Clamp(setup.Internet.SourceFreshnessMinutes, 1, 1440);

        var warnings = validationWarnings.ToList();
        target.Configs.Clear();
        var importedSharedModel = setup.Providers.TryGetValue(ModelProviderRouting.SharedConfigKey, out var sharedDefinition)
            ? sharedDefinition?.Model?.Trim() ?? ""
            : trustedConfigs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var trustedShared)
                ? trustedShared.Model.Trim()
                : "";
        foreach (var (key, definition) in setup.Providers.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            trustedConfigs.TryGetValue(key, out var trusted);
            var normalizedMode = ModelProviderApiModes.Normalize(definition.ApiMode);
            var canReuseToken = trusted is not null
                && SameEndpoint(trusted.BaseUrl, definition.BaseUrl)
                && ModelProviderApiModes.Normalize(trusted.ApiMode).Equals(normalizedMode, StringComparison.OrdinalIgnoreCase);
            if (trusted is not null && !string.IsNullOrWhiteSpace(trusted.ApiToken) && !canReuseToken)
            {
                warnings.Add($"Provider token for '{key}' was cleared because the imported endpoint or API mode changed.");
            }

            var explicitAssignment = !key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                && definition.AssignmentMode.Equals(MatchSetupProviderAssignmentModes.Explicit, StringComparison.Ordinal);
            target.Configs[key] = new ModelProviderConfig
            {
                BaseUrl = definition.BaseUrl.Trim(),
                ApiMode = normalizedMode,
                ApiToken = canReuseToken ? trusted!.ApiToken : "",
                Model = explicitAssignment ? definition.Model.Trim() : importedSharedModel,
                Timeout = ArenaSessionMutationCoordinator.ClampTimeout(definition.TimeoutSeconds),
                Temperature = ArenaSessionMutationCoordinator.ClampTemperature(definition.Temperature),
                MaxOutputTokens = ArenaSessionMutationCoordinator.ClampMaxOutput(definition.MaxOutputTokens),
                ContextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(definition.ContextLength),
                ConfiguredContextWindow = NormalizeConfiguredContextWindow(definition.ConfiguredContextWindow),
                HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(definition.HistoryPolicy),
                ResponseTone = ModelResponseTones.NormalizeResponseTone(definition.ResponseTone),
                CustomTone = ModelResponseTones.NormalizeCustomTone(definition.CustomTone),
                Reasoning = ModelProviderReasoningModes.Normalize(definition.Reasoning),
                NativeStatefulChat = definition.NativeStatefulChat,
                NativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(definition.NativeIdleTtlSeconds),
                ExplicitModelAssignment = explicitAssignment
            };
        }

        if (!target.Configs.ContainsKey("shared"))
        {
            target.Configs["shared"] = trustedConfigs.TryGetValue("shared", out var shared)
                ? new ModelProviderConfig
                {
                    BaseUrl = shared.BaseUrl,
                    ApiMode = shared.ApiMode,
                    ApiToken = shared.ApiToken,
                    Model = shared.Model,
                    Timeout = shared.Timeout,
                    Temperature = shared.Temperature,
                    MaxOutputTokens = shared.MaxOutputTokens,
                    ContextLength = shared.ContextLength,
                    ConfiguredContextWindow = shared.ConfiguredContextWindow,
                    HistoryPolicy = shared.HistoryPolicy,
                    ResponseTone = shared.ResponseTone,
                    CustomTone = shared.CustomTone,
                    Reasoning = shared.Reasoning,
                    NativeStatefulChat = shared.NativeStatefulChat,
                    NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds,
                    ExplicitModelAssignment = false
                }
                : new ModelProviderConfig();
            warnings.Add("The package had no shared provider definition; the trusted local shared provider was retained.");
        }

        target.ModelSettings.Clear();
        target.PendingModelConfigurationApplies.Clear();
        foreach (var config in target.Configs.Values.Where(config => !string.IsNullOrWhiteSpace(config.Model)))
        {
            var registered = ModelRuntimeSettingsRegistry.Register(
                target,
                config,
                NormalizeConfiguredContextWindow(config.ConfiguredContextWindow),
                ModelHistoryPolicies.NormalizeHistoryPolicy(config.HistoryPolicy),
                ModelResponseTones.NormalizeResponseTone(config.ResponseTone),
                config.CustomTone);
        }
        var importedShared = target.Configs[ModelProviderRouting.SharedConfigKey];
        foreach (var setting in setup.ModelSettings)
        {
            var registered = ModelRuntimeSettingsRegistry.Register(
                target,
                new ModelProviderConfig
                {
                    BaseUrl = importedShared.BaseUrl,
                    ApiMode = importedShared.ApiMode,
                    Model = setting.Model
                },
                NormalizeConfiguredContextWindow(setting.ConfiguredContextWindow),
                ModelHistoryPolicies.NormalizeHistoryPolicy(setting.HistoryPolicy),
                ModelResponseTones.NormalizeResponseTone(setting.ResponseTone),
                setting.CustomTone);
            if (setting.PendingApply)
            {
                target.PendingModelConfigurationApplies.Add(registered.ModelIdentity);
            }
        }
        target.ModelSettingsVersion = ModelRuntimeSettingsRegistry.CurrentSchemaVersion;
        ModelRuntimeSettingsRegistry.Normalize(target);

        target.GenerationHistory.Clear();
        target.Engine.Messages.Clear();
        target.Engine.Narration.Clear();
        target.Engine.Attachments.Clear();
        target.Engine.ResearchItems.Clear();
        target.Engine.TurnCount = 0;
        target.Engine.TurnIndex = 0;
        target.Engine.MatchEnded = false;
        target.Engine.MatchEndedAt = null;
        target.Engine.MatchEndReason = "";
        target.Engine.LastError = "";
        target.Engine.DecisionCard.Text = "";
        target.Engine.DecisionCard.UpdatedAt = 0;
        target.Engine.DecisionCard.InternetRequest = null;
        target.Engine.DecisionCard.InternetResult = null;
        return new ApplyResult(true, "Match Setup package applied.", warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void Validate(MatchSetupPackage package, List<string> errors, List<string> warnings)
    {
        package.Metadata ??= new MatchSetupMetadataPackage();
        package.Setup ??= new MatchSetupDefinitionPackage();
        var setup = package.Setup;
        setup.Scenario ??= new MatchSetupScenarioPackage();
        setup.Generation ??= new MatchSetupGenerationPackage();
        setup.Cast ??= [];
        setup.Narrator ??= new MatchSetupNarratorPackage();
        setup.Locks ??= new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        setup.Relationship ??= new MatchSetupRelationshipPackage();
        setup.Relationship.Links ??= [];
        setup.Context ??= new MatchSetupContextPackage();
        setup.Internet ??= new MatchSetupInternetPackage();
        setup.Providers ??= new SortedDictionary<string, MatchSetupProviderPackage>(StringComparer.OrdinalIgnoreCase);
        setup.ModelSettings ??= [];
        package.Metadata.Name ??= "";
        setup.MatchType ??= "";
        setup.Scenario.Topic ??= "";
        setup.Scenario.Global ??= "";
        setup.Generation.ScenarioStyle ??= "";
        setup.Generation.ScenarioSeed ??= "";
        setup.Generation.Intensity ??= "";
        setup.Generation.RolePack ??= "";
        setup.Generation.Absurdity ??= "";
        setup.Generation.PersonaStyle ??= "";
        setup.Generation.PersonaSeed ??= "";
        setup.Narrator.Persona ??= "";
        setup.Narrator.VoiceStyle ??= "";
        setup.Narrator.AccentColor ??= "";
        foreach (var agent in setup.Cast.Where(agent => agent is not null))
        {
            agent.Id ??= "";
            agent.Name ??= "";
            agent.Persona ??= "";
            agent.VoiceStyle ??= "";
            agent.PressureProfile ??= "";
            agent.AccentColor ??= "";
        }
        foreach (var link in setup.Relationship.Links.Where(link => link is not null))
        {
            link.Source ??= "";
            link.Target ??= "";
            link.Stance ??= "";
        }
        foreach (var provider in setup.Providers.Values.Where(provider => provider is not null))
        {
            provider.BaseUrl ??= "";
            provider.ApiMode ??= "";
            provider.Model ??= "";
            provider.Reasoning ??= "";
            provider.AssignmentMode ??= "";
            provider.HistoryPolicy ??= "";
            provider.ResponseTone ??= "";
            provider.CustomTone ??= "";
        }

        RequireText("setup.matchType", setup.MatchType, 1, 64, errors);
        CheckText("setup.scenario.topic", setup.Scenario.Topic, MaxTextChars, errors);
        CheckText("setup.scenario.global", setup.Scenario.Global, MaxTextChars, errors);
        CheckText("metadata.name", package.Metadata.Name, 128, errors);
        if (!setup.FactoryMode)
        {
            WarnIfBlank("Scenario topic is blank; the imported setup will remain blocked until a topic is added.", setup.Scenario.Topic, warnings);
            WarnIfBlank("Scenario global instruction is blank; the imported setup will remain blocked until run guidance is added.", setup.Scenario.Global, warnings);
        }
        foreach (var (name, value) in new[]
                 {
                     ("scenarioStyle", setup.Generation.ScenarioStyle),
                     ("scenarioSeed", setup.Generation.ScenarioSeed),
                     ("intensity", setup.Generation.Intensity),
                     ("rolePack", setup.Generation.RolePack),
                     ("absurdity", setup.Generation.Absurdity),
                     ("personaStyle", setup.Generation.PersonaStyle),
                     ("personaSeed", setup.Generation.PersonaSeed)
                 })
        {
            CheckText($"setup.generation.{name}", value, MaxShortTextChars, errors);
        }

        if (setup.Cast.Count is < AgentRosterService.MinParticipants or > AgentRosterService.MaxParticipants)
        {
            errors.Add($"setup.cast must contain {AgentRosterService.MinParticipants} to {AgentRosterService.MaxParticipants} active participants.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agent, index) in setup.Cast.Select((agent, index) => (agent, index)))
        {
            if (agent is null)
            {
                errors.Add($"setup.cast[{index}] must be an object.");
                continue;
            }

            var id = agent.Id?.Trim().ToLowerInvariant() ?? "";
            if (!AgentRosterService.IsParticipantId(id))
            {
                errors.Add($"setup.cast[{index}].id '{agent.Id}' is not a supported participant id.");
            }
            else if (!seen.Add(id))
            {
                errors.Add($"setup.cast contains duplicate participant '{id}'.");
            }

            CheckText($"setup.cast[{index}].name", agent.Name, 128, errors);
            CheckText($"setup.cast[{index}].persona", agent.Persona, MaxTextChars, errors);
            CheckText($"setup.cast[{index}].voiceStyle", agent.VoiceStyle, MaxShortTextChars, errors);
            CheckText($"setup.cast[{index}].pressureProfile", agent.PressureProfile, MaxShortTextChars, errors);
            CheckText($"setup.cast[{index}].accentColor", agent.AccentColor, 32, errors);
            WarnIfBlank($"Participant '{id}' has a blank name.", agent.Name, warnings);
            if (!setup.FactoryMode)
            {
                WarnIfBlank($"Participant '{id}' has a blank persona.", agent.Persona, warnings);
            }
            if (!string.IsNullOrWhiteSpace(agent.AccentColor) && string.IsNullOrWhiteSpace(AgentAccentService.NormalizeColor(agent.AccentColor)))
            {
                errors.Add($"setup.cast[{index}].accentColor must be a six-digit hexadecimal color.");
            }
        }

        var expectedIds = AgentRosterService.ParticipantIds.Take(setup.Cast.Count).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (seen.Count == setup.Cast.Count && !seen.SetEquals(expectedIds))
        {
            errors.Add("setup.cast participant ids must be contiguous from alpha through the selected roster size.");
        }

        CheckText("setup.narrator.persona", setup.Narrator.Persona, MaxTextChars, errors);
        CheckText("setup.narrator.voiceStyle", setup.Narrator.VoiceStyle, MaxShortTextChars, errors);
        CheckText("setup.narrator.accentColor", setup.Narrator.AccentColor, 32, errors);
        if (!setup.FactoryMode)
        {
            WarnIfBlank("Narrator persona is blank.", setup.Narrator.Persona, warnings);
        }
        else
        {
            var blankArenaGuidance = FactoryModeBlankArenaGuidance(setup);
            if (blankArenaGuidance.Count > 0)
            {
                var verb = blankArenaGuidance.Count == 1 ? "does" : "do";
                warnings.Add(
                    $"Factory mode does not send Arena-only guidance to participant models. Blank {string.Join(", ", blankArenaGuidance)} {verb} not block this import; complete the missing guidance before switching to Arena mode.");
            }
        }
        if (!string.IsNullOrWhiteSpace(setup.Narrator.AccentColor) && string.IsNullOrWhiteSpace(AgentAccentService.NormalizeColor(setup.Narrator.AccentColor)))
        {
            errors.Add("setup.narrator.accentColor must be a six-digit hexadecimal color.");
        }
        if (setup.Narrator.Cadence is < 0 or > 1000)
        {
            errors.Add("setup.narrator.cadence must be between 0 and 1000.");
        }

        var allowedLockKeys = new[] { "topic", "global", "narrator" }.Concat(seen).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLockKeys = allowedLockKeys.Where(key => !setup.Locks.ContainsKey(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingLockKeys.Length > 0)
        {
            errors.Add($"setup.locks is missing required keys: {string.Join(", ", missingLockKeys)}.");
        }
        var unsupportedLockKeys = setup.Locks.Keys.Where(key => !allowedLockKeys.Contains(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unsupportedLockKeys.Length > 0)
        {
            errors.Add($"setup.locks contains unsupported keys: {string.Join(", ", unsupportedLockKeys)}.");
        }

        if (setup.Relationship.Links.Any(link => link is null))
        {
            errors.Add("setup.relationship.links cannot contain null entries.");
        }
        var relationshipSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (link, index) in setup.Relationship.Links.Select((link, index) => (link, index)))
        {
            if (link is null)
            {
                continue;
            }

            var source = link.Source.Trim().ToLowerInvariant();
            var target = link.Target.Trim().ToLowerInvariant();
            var stance = MatchSetupCoordinator.NormalizeRivalryStance(link.Stance);
            if (!seen.Contains(source) || !seen.Contains(target))
            {
                errors.Add($"setup.relationship.links[{index}] must reference active cast ids.");
            }
            else if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"setup.relationship.links[{index}] cannot target its own source.");
            }
            else if (stance.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"setup.relationship.links[{index}].stance is unsupported or neutral; omit neutral rules instead.");
            }
            else if (!relationshipSources.Add(source))
            {
                errors.Add($"setup.relationship.links contains more than one rule for source '{source}'.");
            }
        }
        var plan = MatchSetupCoordinator.BuildRivalryMatrixPlan(
            setup.Relationship.Links
                .Where(link => link is not null)
                .Select(link => new Models.RivalryMatrixItem(link.Source, link.Target, link.Stance)),
            seen);
        if (plan.SkippedInvalidRules > 0)
        {
            errors.Add($"setup.relationship.links contains {plan.SkippedInvalidRules} invalid, duplicate, self-targeting, or neutral rule(s).");
        }
        if (setup.Relationship.Enabled && plan.Links.Count == 0)
        {
            warnings.Add("Relationship pressure is enabled without an active rule; Match Setup will keep this visible as a readiness blocker.");
        }

        if (setup.Context.TranscriptWindow is < 1 or > 60
            || setup.Context.PrivateWindow is < 0 or > 60
            || setup.Context.NotesWindow is < 0 or > 60)
        {
            errors.Add("setup.context windows must be transcript 1-60, private 0-60, and notes 0-60.");
        }
        if (setup.Internet.MaxResults is < 1 or > 10
            || setup.Internet.SourceFreshnessMinutes is < 1 or > 1440)
        {
            errors.Add("setup.internet must use maxResults 1-10 and sourceFreshnessMinutes 1-1440.");
        }

        var sharedProviderModel = setup.Providers.TryGetValue(ModelProviderRouting.SharedConfigKey, out var sharedProvider)
            ? sharedProvider?.Model?.Trim() ?? ""
            : "";
        foreach (var (key, provider) in setup.Providers)
        {
            if (provider is null)
            {
                errors.Add($"setup.providers.{key} must be an object.");
                continue;
            }

            if (!IsSupportedProviderKey(key, seen))
            {
                errors.Add($"setup.providers contains unsupported role key '{key}'.");
                continue;
            }
            if (!key.Equals(key.ToLowerInvariant(), StringComparison.Ordinal))
            {
                errors.Add($"setup.providers role key '{key}' must use canonical lowercase.");
            }

            CheckText($"setup.providers.{key}.baseUrl", provider.BaseUrl, 2048, errors);
            CheckText($"setup.providers.{key}.model", provider.Model, MaxShortTextChars, errors);
            CheckText($"setup.providers.{key}.reasoning", provider.Reasoning, 32, errors);
            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"setup.providers.{key}.baseUrl must be an absolute HTTP or HTTPS URL.");
            }
            else if (!string.IsNullOrWhiteSpace(endpoint.UserInfo)
                     || !string.IsNullOrWhiteSpace(endpoint.Query)
                     || !string.IsNullOrWhiteSpace(endpoint.Fragment))
            {
                errors.Add($"setup.providers.{key}.baseUrl cannot contain embedded credentials, a query, or a fragment.");
            }
            if (double.IsNaN(provider.Temperature) || double.IsInfinity(provider.Temperature))
            {
                errors.Add($"setup.providers.{key}.temperature must be a finite number.");
            }
            else if (provider.Temperature is < 0 or > 2)
            {
                errors.Add($"setup.providers.{key}.temperature must be between 0 and 2.");
            }
            if (provider.TimeoutSeconds is < 1 or > 3600)
            {
                errors.Add($"setup.providers.{key}.timeoutSeconds must be between 1 and 3600.");
            }
            if (provider.MaxOutputTokens is < 1 or > 32768)
            {
                errors.Add($"setup.providers.{key}.maxOutputTokens must be between 1 and 32768.");
            }
            if (provider.ContextLength is < 0 or > 1048576)
            {
                errors.Add($"setup.providers.{key}.contextLength must be between 0 and 1048576.");
            }
            if (provider.ConfiguredContextWindow != 0
                && provider.ConfiguredContextWindow is < 512 or > 1048576)
            {
                errors.Add($"setup.providers.{key}.configuredContextWindow must be 0 (provider default) or between 512 and 1048576.");
            }
            if (ModelHistoryPolicies.NormalizeHistoryPolicy(provider.HistoryPolicy) != provider.HistoryPolicy.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.providers.{key}.historyPolicy must be strict, rolling_80, or chaptered.");
            }
            if (ModelResponseTones.NormalizeResponseTone(provider.ResponseTone) != provider.ResponseTone.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.providers.{key}.responseTone must be default, neutral, concise, analytical, creative, direct, or custom.");
            }
            if (provider.CustomTone.Length > ModelResponseTones.MaximumCustomToneCharacters
                || provider.CustomTone.Any(char.IsControl))
            {
                errors.Add($"setup.providers.{key}.customTone must be at most {ModelResponseTones.MaximumCustomToneCharacters} characters and contain no control characters.");
            }
            if (provider.ResponseTone.Equals(ModelResponseTones.Custom, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(ModelResponseTones.NormalizeCustomTone(provider.CustomTone)))
            {
                errors.Add($"setup.providers.{key}.customTone is required when responseTone is custom.");
            }
            if (provider.NativeIdleTtlSeconds is < 0 or > 86400)
            {
                errors.Add($"setup.providers.{key}.nativeIdleTtlSeconds must be between 0 and 86400.");
            }
            if (provider.ApiMode is not (ModelProviderApiModes.OpenAiCompatible
                or ModelProviderApiModes.LmStudioNative
                or ModelProviderApiModes.OllamaNative
                or ModelProviderApiModes.LlamaCppNative))
            {
                errors.Add($"setup.providers.{key}.apiMode must be openai_compatible, lmstudio_native, ollama_native, or llamacpp_native.");
            }
            if (!string.IsNullOrWhiteSpace(provider.Reasoning)
                && ModelProviderReasoningModes.Normalize(provider.Reasoning) != provider.Reasoning.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.providers.{key}.reasoning must be off, low, medium, high, on, or blank.");
            }
            if (!MatchSetupProviderAssignmentModes.IsSupported(provider.AssignmentMode))
            {
                errors.Add($"setup.providers.{key}.assignmentMode must be explicit or inherit.");
            }
            else if (key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                     && !provider.AssignmentMode.Equals(MatchSetupProviderAssignmentModes.Inherit, StringComparison.Ordinal))
            {
                errors.Add("setup.providers.shared.assignmentMode must be inherit.");
            }
            else if (!key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                     && provider.AssignmentMode.Equals(MatchSetupProviderAssignmentModes.Explicit, StringComparison.Ordinal)
                     && string.IsNullOrWhiteSpace(provider.Model))
            {
                errors.Add($"setup.providers.{key}.model must be non-empty when assignmentMode is explicit.");
            }
            else if (!key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                     && provider.AssignmentMode.Equals(MatchSetupProviderAssignmentModes.Inherit, StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(provider.Model)
                     && !provider.Model.Trim().Equals(sharedProviderModel, StringComparison.Ordinal))
            {
                errors.Add($"setup.providers.{key}.model must be blank or match the shared model when assignmentMode is inherit.");
            }
        }

        if (setup.ModelSettings.Count > 256)
        {
            errors.Add("setup.modelSettings cannot contain more than 256 entries.");
        }
        if (setup.ModelSettings.Any(setting => setting is null))
        {
            errors.Add("setup.modelSettings cannot contain null entries.");
        }
        var portableSettingIdentities = new HashSet<string>(StringComparer.Ordinal);
        var portableShared = setup.Providers.TryGetValue(ModelProviderRouting.SharedConfigKey, out var portableSharedProvider)
            ? portableSharedProvider
            : null;
        foreach (var (setting, index) in setup.ModelSettings.Select((setting, index) => (setting, index)))
        {
            if (setting is null)
            {
                continue;
            }
            setting.Model ??= "";
            setting.HistoryPolicy ??= "";
            setting.ResponseTone ??= "";
            setting.CustomTone ??= "";
            if (!IsPortableModelIdentifier(setting.Model))
            {
                errors.Add($"setup.modelSettings[{index}].model must be a safe non-path model identifier.");
                continue;
            }
            if (setting.ConfiguredContextWindow != 0
                && setting.ConfiguredContextWindow is < 512 or > 1048576)
            {
                errors.Add($"setup.modelSettings[{index}].configuredContextWindow must be 0 or between 512 and 1048576.");
            }
            if (ModelHistoryPolicies.NormalizeHistoryPolicy(setting.HistoryPolicy) != setting.HistoryPolicy.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.modelSettings[{index}].historyPolicy is unsupported.");
            }
            if (ModelResponseTones.NormalizeResponseTone(setting.ResponseTone) != setting.ResponseTone.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.modelSettings[{index}].responseTone is unsupported.");
            }
            if (setting.CustomTone.Length > ModelResponseTones.MaximumCustomToneCharacters
                || setting.CustomTone.Any(char.IsControl)
                || setting.ResponseTone.Equals(ModelResponseTones.Custom, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(ModelResponseTones.NormalizeCustomTone(setting.CustomTone)))
            {
                errors.Add($"setup.modelSettings[{index}].customTone is invalid.");
            }
            if (portableShared is not null)
            {
                var identity = ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
                {
                    BaseUrl = portableShared.BaseUrl,
                    ApiMode = portableShared.ApiMode,
                    Model = setting.Model
                });
                if (!portableSettingIdentities.Add(identity))
                {
                    errors.Add("setup.modelSettings contains duplicate canonical model identities.");
                }
            }
        }


        var canonicalSettings = new Dictionary<string, MatchSetupProviderPackage>(StringComparer.Ordinal);
        foreach (var provider in setup.Providers.Values.Where(provider => provider is not null && !string.IsNullOrWhiteSpace(provider.Model)))
        {
            var identity = ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
            {
                BaseUrl = provider.BaseUrl,
                ApiMode = provider.ApiMode,
                Model = provider.Model
            });
            if (canonicalSettings.TryGetValue(identity, out var prior)
                && (prior.ConfiguredContextWindow != provider.ConfiguredContextWindow
                    || !prior.HistoryPolicy.Equals(provider.HistoryPolicy, StringComparison.Ordinal)
                    || !prior.ResponseTone.Equals(provider.ResponseTone, StringComparison.Ordinal)
                    || !prior.CustomTone.Equals(provider.CustomTone, StringComparison.Ordinal)))
            {
                errors.Add("setup.providers contains conflicting runtime settings for the same provider model identity.");
                break;
            }
            canonicalSettings[identity] = provider;
        }
        if (portableShared is not null)
        {
            foreach (var setting in setup.ModelSettings.Where(setting => setting is not null))
            {
                var identity = ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
                {
                    BaseUrl = portableShared.BaseUrl,
                    ApiMode = portableShared.ApiMode,
                    Model = setting.Model
                });
                if (canonicalSettings.TryGetValue(identity, out var provider)
                    && (provider.ConfiguredContextWindow != setting.ConfiguredContextWindow
                        || !provider.HistoryPolicy.Equals(setting.HistoryPolicy, StringComparison.Ordinal)
                        || !provider.ResponseTone.Equals(setting.ResponseTone, StringComparison.Ordinal)
                        || !provider.CustomTone.Equals(setting.CustomTone, StringComparison.Ordinal)))
                {
                    errors.Add("setup.modelSettings conflicts with routed provider settings for the same canonical model.");
                    break;
                }
            }
        }
    }

    private static void UpgradeLegacyV2(MatchSetupPackage package)
    {
        package.Schema = Schema;
        package.Setup ??= new MatchSetupDefinitionPackage();
        package.Setup.DefaultForUnassignedAgentsEnabled = true;
        // v2 had no portable per-model registry. Do not reinterpret v4-only
        // members grafted onto a legacy envelope as historical settings.
        package.Setup.ModelSettings = [];
        package.Setup.Providers ??= new SortedDictionary<string, MatchSetupProviderPackage>(StringComparer.OrdinalIgnoreCase);
        var sharedModel = package.Setup.Providers.TryGetValue(ModelProviderRouting.SharedConfigKey, out var shared)
            ? shared?.Model?.Trim() ?? ""
            : "";
        foreach (var (key, provider) in package.Setup.Providers)
        {
            if (provider is null)
            {
                continue;
            }

            var model = provider.Model?.Trim() ?? "";
            provider.AssignmentMode = !key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                                      && model.Length > 0
                                      && !model.Equals(sharedModel, StringComparison.Ordinal)
                ? MatchSetupProviderAssignmentModes.Explicit
                : MatchSetupProviderAssignmentModes.Inherit;
            UpgradeLegacyProviderSettings(provider);
        }
    }

    private static void UpgradeLegacyV3(MatchSetupPackage package)
    {
        package.Schema = Schema;
        package.Setup ??= new MatchSetupDefinitionPackage();
        // v3 likewise predates the canonical per-model settings registry.
        package.Setup.ModelSettings = [];
        package.Setup.Providers ??= new SortedDictionary<string, MatchSetupProviderPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in package.Setup.Providers.Values.Where(provider => provider is not null))
        {
            UpgradeLegacyProviderSettings(provider);
        }
    }

    private static void UpgradeLegacyProviderSettings(MatchSetupProviderPackage provider)
    {
        provider.ConfiguredContextWindow = NormalizeConfiguredContextWindow(provider.ContextLength);
        provider.HistoryPolicy = ModelHistoryPolicies.Strict;
        provider.ResponseTone = ModelResponseTones.Default;
        provider.CustomTone = "";
    }

    private static int NormalizeConfiguredContextWindow(int value) => value == 0
        ? 0
        : Math.Clamp(value, ModelRuntimeSettingsRegistry.MinimumConfiguredContextWindow, ModelRuntimeSettingsRegistry.MaximumConfiguredContextWindow);

    private static string PortableModelIdentifier(string? value) =>
        ProviderModelCatalogProjectionService.SafeModelIdentifier(value ?? "").Trim();

    private static bool IsPortableModelIdentifier(string? value)
    {
        var normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaxShortTextChars
            && !normalized.Any(char.IsControl)
            && !Path.IsPathRooted(normalized)
            && !(Uri.TryCreate(normalized, UriKind.Absolute, out _));
    }

    private static bool IsSupportedProviderKey(string key, IEnumerable<string> participantIds)
    {
        return key.Equals("shared", StringComparison.OrdinalIgnoreCase)
            || key.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            || participantIds.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameEndpoint(string left, string right)
    {
        if (!Uri.TryCreate(left?.Trim(), UriKind.Absolute, out var leftEndpoint)
            || !Uri.TryCreate(right?.Trim(), UriKind.Absolute, out var rightEndpoint)
            || !SafeProviderUri(leftEndpoint)
            || !SafeProviderUri(rightEndpoint))
        {
            return false;
        }

        return leftEndpoint.Scheme.Equals(rightEndpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            && leftEndpoint.IdnHost.Equals(rightEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            && leftEndpoint.Port == rightEndpoint.Port
            && leftEndpoint.AbsolutePath.TrimEnd('/').Equals(rightEndpoint.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static bool SafeProviderUri(Uri endpoint) =>
        (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrWhiteSpace(endpoint.UserInfo)
        && string.IsNullOrWhiteSpace(endpoint.Query)
        && string.IsNullOrWhiteSpace(endpoint.Fragment);

    private static string SanitizeProviderBaseUrl(string? value)
    {
        var raw = value?.Trim() ?? "";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return raw;
        }

        var builder = new UriBuilder(endpoint)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static void RequireText(string path, string? value, int min, int max, List<string> errors)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < min || length > max)
        {
            errors.Add($"{path} must contain {min} to {max:N0} characters.");
        }
    }

    private static void CheckText(string path, string? value, int max, List<string> errors)
    {
        if ((value?.Length ?? 0) > max)
        {
            errors.Add($"{path} exceeds the {max:N0}-character limit.");
        }
    }

    private static void WarnIfBlank(string warning, string? value, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            warnings.Add(warning);
        }
    }

    private static IReadOnlyList<string> FactoryModeBlankArenaGuidance(MatchSetupDefinitionPackage setup)
    {
        var blank = new List<string>();
        if (string.IsNullOrWhiteSpace(setup.Scenario.Topic))
        {
            blank.Add("scenario topic");
        }

        if (string.IsNullOrWhiteSpace(setup.Scenario.Global))
        {
            blank.Add("global instruction");
        }

        var blankPersonas = setup.Cast.Count(agent => agent is not null && string.IsNullOrWhiteSpace(agent.Persona));
        if (blankPersonas > 0)
        {
            blank.Add(blankPersonas == 1 ? "participant persona" : $"{blankPersonas} participant personas");
        }

        if (string.IsNullOrWhiteSpace(setup.Narrator.Persona))
        {
            blank.Add("narrator persona");
        }

        return blank;
    }

    private static ParseResult Invalid(string code, string message) => new(false, code, message, null, []);
}

internal sealed class MatchSetupPackage
{
    public string Schema { get; set; } = MatchSetupPackageCodec.Schema;
    public MatchSetupMetadataPackage Metadata { get; set; } = new();
    public MatchSetupDefinitionPackage Setup { get; set; } = new();
}

internal sealed class MatchSetupMetadataPackage
{
    public string Name { get; set; } = "";
}

internal sealed class MatchSetupDefinitionPackage
{
    public string MatchType { get; set; } = "balanced";
    public bool FactoryMode { get; set; }
    public bool DefaultForUnassignedAgentsEnabled { get; set; } = true;
    public MatchSetupScenarioPackage Scenario { get; set; } = new();
    public MatchSetupGenerationPackage Generation { get; set; } = new();
    public List<MatchSetupAgentPackage> Cast { get; set; } = [];
    public MatchSetupNarratorPackage Narrator { get; set; } = new();
    public SortedDictionary<string, bool> Locks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MatchSetupRelationshipPackage Relationship { get; set; } = new();
    public MatchSetupContextPackage Context { get; set; } = new();
    public MatchSetupInternetPackage Internet { get; set; } = new();
    public SortedDictionary<string, MatchSetupProviderPackage> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MatchSetupModelSettingsPackage> ModelSettings { get; set; } = [];
}

internal sealed class MatchSetupModelSettingsPackage
{
    public string Model { get; set; } = "";
    public int ConfiguredContextWindow { get; set; }
    public string HistoryPolicy { get; set; } = ModelHistoryPolicies.Strict;
    public string ResponseTone { get; set; } = ModelResponseTones.Default;
    public string CustomTone { get; set; } = "";
    public bool PendingApply { get; set; }
}

internal sealed class MatchSetupScenarioPackage
{
    public string Topic { get; set; } = "";
    public string Global { get; set; } = "";
}

internal sealed class MatchSetupGenerationPackage
{
    public string ScenarioStyle { get; set; } = "";
    public string ScenarioSeed { get; set; } = "";
    public string Intensity { get; set; } = "";
    public string RolePack { get; set; } = "";
    public string Absurdity { get; set; } = "";
    public bool ApplyOnReset { get; set; }
    public string PersonaStyle { get; set; } = "";
    public string PersonaSeed { get; set; } = "";
    public bool PersonaApplyOnReset { get; set; }
}

internal sealed class MatchSetupAgentPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Persona { get; set; } = "";
    public string VoiceStyle { get; set; } = "";
    public string PressureProfile { get; set; } = "";
    public string AccentColor { get; set; } = "";
}

internal sealed class MatchSetupNarratorPackage
{
    public string Persona { get; set; } = "";
    public string VoiceStyle { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public int Cadence { get; set; }
    public bool InspectPrivateNotes { get; set; }
}

internal sealed class MatchSetupRelationshipPackage
{
    public bool Enabled { get; set; }
    public List<MatchSetupRelationshipLinkPackage> Links { get; set; } = [];
}

internal sealed class MatchSetupRelationshipLinkPackage
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Stance { get; set; } = "neutral";
}

internal sealed class MatchSetupContextPackage
{
    public int TranscriptWindow { get; set; } = 30;
    public int PrivateWindow { get; set; } = 12;
    public int NotesWindow { get; set; } = 8;
}

internal sealed class MatchSetupInternetPackage
{
    public bool Enabled { get; set; }
    public int MaxResults { get; set; } = 5;
    public int SourceFreshnessMinutes { get; set; } = 20;
}

internal sealed class MatchSetupProviderPackage
{
    public string BaseUrl { get; set; } = "http://localhost:1234";
    public string ApiMode { get; set; } = ModelProviderApiModes.OpenAiCompatible;
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 1024;
    public int ContextLength { get; set; }
    public int ConfiguredContextWindow { get; set; }
    public string HistoryPolicy { get; set; } = ModelHistoryPolicies.Strict;
    public string ResponseTone { get; set; } = ModelResponseTones.Default;
    public string CustomTone { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public bool NativeStatefulChat { get; set; } = true;
    public int NativeIdleTtlSeconds { get; set; }
    public string AssignmentMode { get; set; } = MatchSetupProviderAssignmentModes.Inherit;
}

internal static class MatchSetupProviderAssignmentModes
{
    public const string Explicit = "explicit";
    public const string Inherit = "inherit";

    public static bool IsSupported(string value) => value is Explicit or Inherit;
}
