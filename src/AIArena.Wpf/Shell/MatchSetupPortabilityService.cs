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
    string Json);

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

    public MatchSetupPortabilityService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        SemaphoreSlim? arenaOperationLock = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.loadSessionsAsync = loadSessionsAsync;
        this.arenaOperationLock = arenaOperationLock;
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

        var package = MatchSetupPackageCodec.FromSnapshot(session.Id, snapshot);
        var validation = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(package));
        if (!validation.Ok || validation.Package is null)
        {
            return Failure("invalid_setup", $"The active Match Setup cannot be exported as a portable package. {validation.Message}");
        }

        var state = MatchSetupPackageCodec.ToState(session.Id, validation.Package);
        return Success(
            "Exported the active Match Setup as a portable JSON package. Provider API tokens were excluded.",
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

        var sessions = await sessionStore.ListSessionsAsync(cancellationToken);
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

            sessions = await sessionStore.ListSessionsAsync(cancellationToken);
            targetSessionId = UniqueSessionId(requestedName, parsed.Package.Metadata.Name, sessions.Select(item => item.Id));
        }
        if (!created)
        {
            return Failure("conflict", "A collision-free session id could not be reserved; retry the import.");
        }

        var importedPackage = MatchSetupPackageCodec.FromSnapshot(targetSessionId, target);
        var state = MatchSetupPackageCodec.ToState(targetSessionId, importedPackage);
        var warnings = parsed.Warnings.Concat(apply.Warnings).Distinct(StringComparer.Ordinal).ToList();
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
            return Failure("invalid_path", $"Match Setup path is invalid: {ex.Message}");
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
                return Failure("not_found", $"Match Setup package was not found: {fullPath}");
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
            return Failure("read_failed", $"Match Setup package could not be read: {ex.Message}");
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
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds
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
    public const string Schema = "ai_arena.match_setup.v2";
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

    public static MatchSetupPackage FromSnapshot(string sessionId, ArenaSnapshot snapshot)
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
        foreach (var (key, config) in snapshot.Configs
                     .Where(item => IsSupportedProviderKey(item.Key, activeIds))
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            providers[key.Trim().ToLowerInvariant()] = new MatchSetupProviderPackage
            {
                BaseUrl = SanitizeProviderBaseUrl(config.BaseUrl),
                ApiMode = ModelProviderApiModes.Normalize(config.ApiMode),
                Model = config.Model,
                TimeoutSeconds = ArenaSessionMutationCoordinator.ClampTimeout(config.Timeout),
                Temperature = ArenaSessionMutationCoordinator.ClampTemperature(config.Temperature),
                MaxOutputTokens = ArenaSessionMutationCoordinator.ClampMaxOutput(config.MaxOutputTokens),
                ContextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(config.ContextLength),
                Reasoning = ModelProviderReasoningModes.Normalize(config.Reasoning),
                NativeStatefulChat = config.NativeStatefulChat,
                NativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(config.NativeIdleTtlSeconds)
            };
        }

        return new MatchSetupPackage
        {
            Metadata = new MatchSetupMetadataPackage { Name = sessionId },
            Setup = new MatchSetupDefinitionPackage
            {
                MatchType = snapshot.MatchType,
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
                Providers = providers
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
            json);
    }

    public static string Fingerprint(MatchSetupPackage package)
    {
        var canonical = JsonSerializer.Serialize(package.Setup, CanonicalJsonOptions);
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
            return Invalid("invalid_json", $"Match Setup JSON is invalid: {ex.Message}");
        }

        if (package is null)
        {
            return Invalid("invalid_package", "Match Setup JSON did not contain a package.");
        }

        if (!string.Equals(package.Schema, Schema, StringComparison.Ordinal))
        {
            return Invalid("unsupported_schema", $"Unsupported Match Setup schema '{package.Schema}'. Expected '{Schema}'.");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
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

            target.Configs[key] = new ModelProviderConfig
            {
                BaseUrl = definition.BaseUrl.Trim(),
                ApiMode = normalizedMode,
                ApiToken = canReuseToken ? trusted!.ApiToken : "",
                Model = definition.Model.Trim(),
                Timeout = ArenaSessionMutationCoordinator.ClampTimeout(definition.TimeoutSeconds),
                Temperature = ArenaSessionMutationCoordinator.ClampTemperature(definition.Temperature),
                MaxOutputTokens = ArenaSessionMutationCoordinator.ClampMaxOutput(definition.MaxOutputTokens),
                ContextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(definition.ContextLength),
                Reasoning = ModelProviderReasoningModes.Normalize(definition.Reasoning),
                NativeStatefulChat = definition.NativeStatefulChat,
                NativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(definition.NativeIdleTtlSeconds)
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
                    Reasoning = shared.Reasoning,
                    NativeStatefulChat = shared.NativeStatefulChat,
                    NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds
                }
                : new ModelProviderConfig();
            warnings.Add("The package had no shared provider definition; the trusted local shared provider was retained.");
        }

        target.GenerationHistory.Clear();
        target.Engine.Messages.Clear();
        target.Engine.Narration.Clear();
        target.Engine.Attachments.Clear();
        target.Engine.ResearchItems.Clear();
        target.Engine.TurnCount = 0;
        target.Engine.TurnIndex = 0;
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
        }

        RequireText("setup.matchType", setup.MatchType, 1, 64, errors);
        CheckText("setup.scenario.topic", setup.Scenario.Topic, MaxTextChars, errors);
        CheckText("setup.scenario.global", setup.Scenario.Global, MaxTextChars, errors);
        CheckText("metadata.name", package.Metadata.Name, 128, errors);
        WarnIfBlank("Scenario topic is blank; the imported setup will remain blocked until a topic is added.", setup.Scenario.Topic, warnings);
        WarnIfBlank("Scenario global instruction is blank; the imported setup will remain blocked until run guidance is added.", setup.Scenario.Global, warnings);
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
            WarnIfBlank($"Participant '{id}' has a blank persona.", agent.Persona, warnings);
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
        WarnIfBlank("Narrator persona is blank.", setup.Narrator.Persona, warnings);
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
            if (provider.NativeIdleTtlSeconds is < 0 or > 86400)
            {
                errors.Add($"setup.providers.{key}.nativeIdleTtlSeconds must be between 0 and 86400.");
            }
            if (provider.ApiMode is not (ModelProviderApiModes.OpenAiCompatible or ModelProviderApiModes.LmStudioNative or ModelProviderApiModes.OllamaNative))
            {
                errors.Add($"setup.providers.{key}.apiMode must be openai_compatible, lmstudio_native, or ollama_native.");
            }
            if (!string.IsNullOrWhiteSpace(provider.Reasoning)
                && ModelProviderReasoningModes.Normalize(provider.Reasoning) != provider.Reasoning.Trim().ToLowerInvariant())
            {
                errors.Add($"setup.providers.{key}.reasoning must be off, low, medium, high, on, or blank.");
            }
        }
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
    public MatchSetupScenarioPackage Scenario { get; set; } = new();
    public MatchSetupGenerationPackage Generation { get; set; } = new();
    public List<MatchSetupAgentPackage> Cast { get; set; } = [];
    public MatchSetupNarratorPackage Narrator { get; set; } = new();
    public SortedDictionary<string, bool> Locks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MatchSetupRelationshipPackage Relationship { get; set; } = new();
    public MatchSetupContextPackage Context { get; set; } = new();
    public MatchSetupInternetPackage Internet { get; set; } = new();
    public SortedDictionary<string, MatchSetupProviderPackage> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public string Reasoning { get; set; } = "";
    public bool NativeStatefulChat { get; set; } = true;
    public int NativeIdleTtlSeconds { get; set; }
}
