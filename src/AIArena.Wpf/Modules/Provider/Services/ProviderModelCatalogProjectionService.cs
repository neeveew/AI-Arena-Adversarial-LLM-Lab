using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;

namespace AIArena.Wpf.Services;

/// <summary>
/// Projects provider-specific catalog evidence into one privacy-safe contract.
/// Fetching remains with the existing provider services; a refresh lease prevents
/// a late result from a previous session or provider identity from being published.
/// </summary>
internal sealed partial class ProviderModelCatalogProjectionService
{
    internal const int MaximumModelCount = 256;
    private const int MaximumIdentifierLength = 192;
    private const int MaximumStatusLength = 512;
    private readonly object sync = new();
    private long generation;
    private ProviderModelCatalogRefreshLease? activeLease;
    private ProviderModelCatalogSnapshot? current;

    public ProviderModelCatalogSnapshot? Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public ProviderModelCatalogRefreshLease BeginRefresh(string sessionId, ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalizedSessionId = (sessionId ?? "").Trim();
        lock (sync)
        {
            var lease = new ProviderModelCatalogRefreshLease(
                ++generation,
                normalizedSessionId,
                ProviderFingerprint(normalizedSessionId, snapshot));
            activeLease = lease;
            return lease;
        }
    }

    public ProviderModelCatalogRefreshLease BeginRefresh(string sessionId, ModelProviderConfig sharedConfig)
    {
        ArgumentNullException.ThrowIfNull(sharedConfig);
        var normalizedSessionId = (sessionId ?? "").Trim();
        lock (sync)
        {
            var lease = new ProviderModelCatalogRefreshLease(
                ++generation,
                normalizedSessionId,
                ProviderFingerprint(normalizedSessionId, sharedConfig));
            activeLease = lease;
            return lease;
        }
    }

    public bool TryPublish(
        ProviderModelCatalogRefreshLease lease,
        ProviderModelCatalogSnapshot candidate,
        out ProviderModelCatalogSnapshot published)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            if (activeLease is null
                || activeLease.Generation != lease.Generation
                || !activeLease.SessionId.Equals(lease.SessionId, StringComparison.Ordinal)
                || !activeLease.ProviderFingerprint.Equals(lease.ProviderFingerprint, StringComparison.Ordinal)
                || candidate.Generation != lease.Generation
                || !candidate.SessionId.Equals(lease.SessionId, StringComparison.Ordinal)
                || !candidate.ProviderFingerprint.Equals(lease.ProviderFingerprint, StringComparison.Ordinal))
            {
                published = current ?? candidate;
                return false;
            }

            current = candidate;
            published = candidate;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            generation++;
            activeLease = null;
            current = null;
        }
    }

    internal static string ProviderFingerprint(string sessionId, ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
            ? configured
            : new ModelProviderConfig();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendConnectionFingerprintValues(hash, sessionId, shared);
        AppendFingerprintValue(hash, shared.Model.Trim());
        AppendFingerprintValue(hash, snapshot.Engine.DefaultForUnassignedAgentsEnabled ? "1" : "0");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ProviderFingerprint(string sessionId, ModelProviderConfig sharedConfig)
    {
        ArgumentNullException.ThrowIfNull(sharedConfig);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendConnectionFingerprintValues(hash, sessionId, sharedConfig);
        AppendFingerprintValue(hash, sharedConfig.Model.Trim());
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ConnectionFingerprint(string sessionId, ModelProviderConfig sharedConfig)
    {
        ArgumentNullException.ThrowIfNull(sharedConfig);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendConnectionFingerprintValues(hash, sessionId, sharedConfig);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static ProviderModelCatalogSnapshot FromLmStudio(
        ProviderModelCatalogRefreshLease lease,
        LmStudioModelCatalog source,
        string configuredModel,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Ok)
        {
            return Build(
                lease,
                [],
                ProviderCatalogEvidenceState.Unavailable,
                ProviderCatalogEvidenceState.Unavailable,
                configuredModel,
                source.Error.Length == 0 ? "LM Studio model list unavailable." : source.Error,
                checkedAt);
        }

        var chatModels = source.ChatModels;
        var items = chatModels.Select(model =>
        {
            var loadState = !model.HasResidencyEvidence
                ? ProviderModelLoadState.Unavailable
                : model.Loaded
                    ? ProviderModelLoadState.Loaded
                    : ProviderModelLoadState.NotLoaded;
            var hasUnloadableInstance = model.LoadedInstances.Any(instance =>
                !string.IsNullOrWhiteSpace(instance.Id));
            return new ProviderModelCatalogItem(
                SafeModelIdentifier(model.PreferredIdentifier),
                SafeModelIdentifier(model.DisplayTitle),
                loadState,
                CanLoad: model.HasResidencyEvidence && !model.Loaded,
                CanUnload: model.HasResidencyEvidence && model.Loaded && hasUnloadableInstance,
                SafeText(model.Publisher, MaximumIdentifierLength),
                SafeText(model.QuantizationName, MaximumIdentifierLength),
                model.LoadedContextLength ?? model.MaxContextLength,
                model.SizeBytes,
                LmStudioCapabilitySummary(model),
                SafeAliases(model.Aliases),
                MaximumContextLength: model.MaxContextLength,
                EffectiveContextLength: model.Loaded ? model.LoadedContextLength : null);
        }).ToArray();
        var evidenceCount = chatModels.Count(model => model.HasResidencyEvidence);
        var catalogEvidence = source.OmittedModelCount > 0
            ? ProviderCatalogEvidenceState.Partial
            : ProviderCatalogEvidenceState.Ready;
        var residencyEvidence = source.OmittedModelCount > 0
            ? evidenceCount > 0
                ? ProviderCatalogEvidenceState.Partial
                : ProviderCatalogEvidenceState.Unavailable
            : evidenceCount == chatModels.Count
                ? ProviderCatalogEvidenceState.Ready
                : evidenceCount > 0
                    ? ProviderCatalogEvidenceState.Partial
                    : ProviderCatalogEvidenceState.Unavailable;
        var loadedCount = items.Count(item => item.LoadState == ProviderModelLoadState.Loaded);
        var availableCount = items.Count(item => item.LoadState == ProviderModelLoadState.NotLoaded);
        var unavailableCount = items.Count(item => item.LoadState == ProviderModelLoadState.Unavailable);
        var statusParts = new List<string>
        {
            $"{loadedCount} loaded",
            $"{availableCount} available"
        };
        if (unavailableCount > 0)
        {
            statusParts.Add($"{unavailableCount} load state unavailable");
        }

        return Build(
            lease,
            items,
            catalogEvidence,
            residencyEvidence,
            configuredModel,
            string.Join("; ", statusParts) + ".",
            checkedAt,
            source.OmittedModelCount);
    }

    internal static ProviderModelCatalogSnapshot FromOllama(
        ProviderModelCatalogRefreshLease lease,
        OllamaModelCatalog source,
        string configuredModel,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Ok)
        {
            return Build(
                lease,
                [],
                ProviderCatalogEvidenceState.Unavailable,
                ProviderCatalogEvidenceState.Unavailable,
                configuredModel,
                source.Error.Length == 0 ? "Ollama model list unavailable." : source.Error,
                checkedAt);
        }

        var residencyEvidence = source.RunningModelsOk
            ? ProviderCatalogEvidenceState.Ready
            : ProviderCatalogEvidenceState.Unavailable;
        var items = source.Models.Select(model =>
        {
            var loadState = source.RunningModelsOk
                ? model.Loaded ? ProviderModelLoadState.Loaded : ProviderModelLoadState.NotLoaded
                : ProviderModelLoadState.Unavailable;
            return new ProviderModelCatalogItem(
                SafeModelIdentifier(model.PreferredIdentifier),
                SafeModelIdentifier(model.PreferredIdentifier),
                loadState,
                CanLoad: source.RunningModelsOk && !model.Loaded,
                CanUnload: source.RunningModelsOk && model.Loaded,
                SafeText(model.Family, MaximumIdentifierLength),
                SafeText(model.QuantizationLevel, MaximumIdentifierLength),
                model.ContextLength,
                model.SizeBytes,
                OllamaCapabilitySummary(model),
                SafeAliases(model.Aliases),
                EffectiveContextLength: model.Loaded ? model.ContextLength : null);
        }).ToArray();
        var status = source.RunningModelsOk
            ? $"{items.Count(item => item.LoadState == ProviderModelLoadState.Loaded)} loaded; {items.Count(item => item.LoadState != ProviderModelLoadState.Loaded)} available."
            : source.RunningModelsError.Length == 0
                ? "Available models loaded; running-model evidence unavailable."
                : $"Available models loaded; running-model evidence unavailable: {source.RunningModelsError}";
        return Build(
            lease,
            items,
            ProviderCatalogEvidenceState.Ready,
            residencyEvidence,
            configuredModel,
            status,
            checkedAt,
            source.OmittedModelCount);
    }

    internal static ProviderModelCatalogSnapshot FromLlamaCpp(
        ProviderModelCatalogRefreshLease lease,
        LlamaCppRuntimeSnapshot source,
        string configuredModel)
    {
        ArgumentNullException.ThrowIfNull(source);
        var catalogAvailable = source.Available && source.Models.Count > 0;
        if (!catalogAvailable)
        {
            return Build(
                lease,
                [],
                ProviderCatalogEvidenceState.Unavailable,
                ProviderCatalogEvidenceState.Unavailable,
                configuredModel,
                source.Error.Length == 0 ? "llama.cpp model inventory unavailable." : source.Error,
                source.CheckedAt);
        }

        var residencyAvailable = source.RouterMode || source.Capabilities.OpenAiModels;
        var items = source.Models.Select(model =>
        {
            var loadState = residencyAvailable && model.Loaded.HasValue
                ? model.Loaded == true ? ProviderModelLoadState.Loaded : ProviderModelLoadState.NotLoaded
                : ProviderModelLoadState.Unavailable;
            return new ProviderModelCatalogItem(
                SafeModelIdentifier(model.Id),
                SafeModelIdentifier(model.Id),
                loadState,
                CanLoad: source.Capabilities.ModelLifecycle && model.Loaded == false,
                CanUnload: source.Capabilities.ModelLifecycle && model.Loaded == true,
                "llama.cpp",
                SafeText(model.Quantization, MaximumIdentifierLength),
                model.ContextLength,
                model.ModelSizeBytes,
                LlamaCppCapabilitySummary(model),
                SafeAliases([model.Id]),
                EffectiveContextLength: model.Loaded == true ? model.ContextLength : null);
        }).ToArray();
        return Build(
            lease,
            items,
            ProviderCatalogEvidenceState.Ready,
            !residencyAvailable ? ProviderCatalogEvidenceState.Unavailable
                : source.Models.All(model => model.Loaded.HasValue) ? ProviderCatalogEvidenceState.Ready : ProviderCatalogEvidenceState.Partial,
            configuredModel,
            source.RouterMode ? "llama.cpp router inventory available." : "llama.cpp live-model inventory available.",
            source.CheckedAt,
            source.OmittedModelCount);
    }

    internal static ProviderModelCatalogSnapshot FromCompatible(
        ProviderModelCatalogRefreshLease lease,
        IReadOnlyList<string> advertisedModels,
        bool catalogAvailable,
        string error,
        string configuredModel,
        DateTimeOffset checkedAt,
        int additionalOmittedModelCount = 0)
    {
        ArgumentNullException.ThrowIfNull(advertisedModels);
        var items = catalogAvailable
            ? advertisedModels.Select(model => new ProviderModelCatalogItem(
                    SafeModelIdentifier(model),
                    SafeModelIdentifier(model),
                    ProviderModelLoadState.Unavailable,
                    CanLoad: false,
                    CanUnload: false,
                    "",
                    "",
                    null,
                    null,
                    "Provider-advertised model; load state unavailable.",
                    SafeAliases([model])))
                .ToArray()
            : [];
        return Build(
            lease,
            items,
            catalogAvailable ? ProviderCatalogEvidenceState.Ready : ProviderCatalogEvidenceState.Unavailable,
            ProviderCatalogEvidenceState.Unavailable,
            configuredModel,
            catalogAvailable
                ? "Provider models available; load state unavailable."
                : error.Length == 0 ? "Provider model list unavailable." : error,
            checkedAt,
            additionalOmittedModelCount);
    }

    internal static string SafeModelIdentifier(string value)
    {
        var normalized = SafeText(value, MaximumIdentifierLength);
        if (normalized.Length == 0)
        {
            return "";
        }

        if (Path.IsPathRooted(normalized)
            || Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var fileName = Path.GetFileName(normalized.Replace('/', Path.DirectorySeparatorChar));
            return SafeText(string.IsNullOrWhiteSpace(fileName) ? "local model" : fileName, MaximumIdentifierLength);
        }

        return normalized;
    }

    private static ProviderModelCatalogSnapshot Build(
        ProviderModelCatalogRefreshLease lease,
        IReadOnlyList<ProviderModelCatalogItem> sourceItems,
        ProviderCatalogEvidenceState catalogEvidence,
        ProviderCatalogEvidenceState residencyEvidence,
        string configuredModel,
        string status,
        DateTimeOffset checkedAt,
        int additionalOmittedModelCount = 0)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var normalized = Deduplicate(sourceItems);
        var configured = (configuredModel ?? "").Trim();
        var matchingConfigured = normalized.FirstOrDefault(item => Matches(item, configured));
        var configuredMissing = configured.Length > 0 && matchingConfigured is null;
        var sourceLimit = configuredMissing ? MaximumModelCount - 1 : MaximumModelCount;
        var retained = normalized.Take(sourceLimit).ToList();
        if (matchingConfigured is not null && !retained.Contains(matchingConfigured))
        {
            if (retained.Count == MaximumModelCount)
            {
                retained.RemoveAt(retained.Count - 1);
            }

            retained.Add(matchingConfigured);
        }

        var retainedSourceCount = retained.Count;
        if (configuredMissing)
        {
            var safeConfigured = SafeModelIdentifier(configured);
            if (safeConfigured.Length > 0)
            {
                retained.Add(new ProviderModelCatalogItem(
                    safeConfigured,
                    safeConfigured,
                    ProviderModelLoadState.Unavailable,
                    CanLoad: false,
                    CanUnload: false,
                    "",
                    "",
                    null,
                    null,
                    "Current selection; not present in the latest provider catalog.",
                    [safeConfigured],
                    IsConfiguredOnly: true));
            }
        }

        var loaded = retained
            .Where(item => item.LoadState == ProviderModelLoadState.Loaded)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var available = retained
            .Where(item => item.LoadState != ProviderModelLoadState.Loaded)
            .OrderByDescending(item => item.IsConfiguredOnly)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var omittedModelCount = Math.Max(0, additionalOmittedModelCount)
            + Math.Max(0, normalized.Count - retainedSourceCount);
        var effectiveCatalogEvidence = omittedModelCount > 0
            && catalogEvidence == ProviderCatalogEvidenceState.Ready
                ? ProviderCatalogEvidenceState.Partial
                : catalogEvidence;
        var effectiveResidencyEvidence = omittedModelCount > 0
            && residencyEvidence == ProviderCatalogEvidenceState.Ready
                ? ProviderCatalogEvidenceState.Partial
                : residencyEvidence;
        var safeStatus = omittedModelCount > 0
            ? $"{omittedModelCount} additional catalog entries omitted by safety limits. {status}"
            : status;
        return new ProviderModelCatalogSnapshot(
            lease.Generation,
            lease.SessionId,
            lease.ProviderFingerprint,
            effectiveCatalogEvidence,
            effectiveResidencyEvidence,
            loaded,
            available,
            matchingConfigured?.Id ?? SafeModelIdentifier(configured),
            configuredMissing,
            omittedModelCount,
            SafeStatus(safeStatus),
            checkedAt);
    }

    private static IReadOnlyList<ProviderModelCatalogItem> Deduplicate(
        IReadOnlyList<ProviderModelCatalogItem> sourceItems)
    {
        var retained = new List<ProviderModelCatalogItem>();
        var aliasOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sets = new DisjointSet();
        foreach (var source in sourceItems)
        {
            var id = SafeModelIdentifier(source.Id);
            if (id.Length == 0)
            {
                continue;
            }

            var aliases = SafeAliases(source.Aliases.Append(id));
            var item = source with
            {
                Id = id,
                DisplayName = SafeModelIdentifier(source.DisplayName.Length == 0 ? id : source.DisplayName),
                Publisher = SafeText(source.Publisher, MaximumIdentifierLength),
                Quantization = SafeText(source.Quantization, MaximumIdentifierLength),
                CapabilitySummary = SafeText(source.CapabilitySummary, MaximumStatusLength),
                Aliases = aliases
            };
            var itemIndex = retained.Count;
            retained.Add(item);
            sets.Add();
            foreach (var alias in aliases)
            {
                if (aliasOwner.TryGetValue(alias, out var ownerIndex))
                {
                    sets.Union(itemIndex, ownerIndex);
                }
                else
                {
                    aliasOwner[alias] = itemIndex;
                }
            }
        }

        var membersByRoot = new Dictionary<int, List<int>>();
        for (var index = 0; index < retained.Count; index++)
        {
            var root = sets.Find(index);
            if (!membersByRoot.TryGetValue(root, out var members))
            {
                members = [];
                membersByRoot[root] = members;
            }

            members.Add(index);
        }

        var deduplicated = new List<ProviderModelCatalogItem>(membersByRoot.Count);
        foreach (var members in membersByRoot.Values.OrderBy(group => group[0]))
        {
            // Provider inventories are not required to retain a stable source order.
            // Choose and merge each alias component by its safe identifier so the row
            // key remains stable across heartbeats that advertise equivalent aliases in
            // a different order. The Models surface reconciles rows by this identifier.
            var orderedMembers = members
                .OrderBy(member => retained[member].Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => retained[member].Id, StringComparer.Ordinal)
                .ToArray();
            var merged = retained[orderedMembers[0]];
            var mergedAliases = new List<string>();
            var observedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in orderedMembers)
            {
                if (member != orderedMembers[0])
                {
                    merged = Merge(merged, retained[member]);
                }

                foreach (var alias in retained[member].Aliases)
                {
                    if (observedAliases.Add(alias))
                    {
                        mergedAliases.Add(alias);
                    }
                }
            }

            deduplicated.Add(merged with { Aliases = mergedAliases });
        }

        return deduplicated
            .OrderByDescending(item => item.LoadState == ProviderModelLoadState.Loaded)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ProviderModelCatalogItem Merge(ProviderModelCatalogItem first, ProviderModelCatalogItem second)
    {
        var loadState = first.LoadState == ProviderModelLoadState.Loaded || second.LoadState == ProviderModelLoadState.Loaded
            ? ProviderModelLoadState.Loaded
            : first.LoadState == ProviderModelLoadState.NotLoaded || second.LoadState == ProviderModelLoadState.NotLoaded
                ? ProviderModelLoadState.NotLoaded
                : ProviderModelLoadState.Unavailable;
        var evidenceSource = LoadEvidenceRank(second.LoadState) > LoadEvidenceRank(first.LoadState)
            ? second
            : first;
        var evidenceFallback = ReferenceEquals(evidenceSource, first) ? second : first;
        return first with
        {
            DisplayName = Prefer(first.DisplayName, second.DisplayName, first.Id),
            LoadState = loadState,
            CanLoad = loadState != ProviderModelLoadState.Loaded && (first.CanLoad || second.CanLoad),
            CanUnload = loadState == ProviderModelLoadState.Loaded && (first.CanUnload || second.CanUnload),
            Publisher = Prefer(first.Publisher, second.Publisher),
            Quantization = Prefer(evidenceSource.Quantization, evidenceFallback.Quantization),
            ContextLength = evidenceSource.ContextLength ?? evidenceFallback.ContextLength,
            SizeBytes = evidenceSource.SizeBytes ?? evidenceFallback.SizeBytes,
            CapabilitySummary = Prefer(evidenceSource.CapabilitySummary, evidenceFallback.CapabilitySummary),
            Aliases = first.Aliases,
            IsConfiguredOnly = first.IsConfiguredOnly && second.IsConfiguredOnly,
            IsResidencyStale = evidenceSource.IsResidencyStale
        };
    }

    private static int LoadEvidenceRank(ProviderModelLoadState state) => state switch
    {
        ProviderModelLoadState.Loaded => 2,
        ProviderModelLoadState.NotLoaded => 1,
        _ => 0
    };

    private sealed class DisjointSet
    {
        private readonly List<int> parents = [];
        private readonly List<int> sizes = [];

        public void Add()
        {
            parents.Add(parents.Count);
            sizes.Add(1);
        }

        public int Find(int item)
        {
            var root = item;
            while (parents[root] != root)
            {
                root = parents[root];
            }

            while (parents[item] != item)
            {
                var parent = parents[item];
                parents[item] = root;
                item = parent;
            }

            return root;
        }

        public void Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot == secondRoot)
            {
                return;
            }

            if (sizes[firstRoot] < sizes[secondRoot])
            {
                (firstRoot, secondRoot) = (secondRoot, firstRoot);
            }

            parents[secondRoot] = firstRoot;
            sizes[firstRoot] += sizes[secondRoot];
        }
    }

    private static bool Matches(ProviderModelCatalogItem item, string model)
    {
        if (model.Length == 0)
        {
            return false;
        }

        var safeModel = SafeModelIdentifier(model);
        return item.Id.Equals(safeModel, StringComparison.OrdinalIgnoreCase)
            || item.Aliases.Any(alias => alias.Equals(safeModel, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SafeAliases(IEnumerable<string> aliases)
    {
        return aliases
            .Select(SafeModelIdentifier)
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static string LmStudioCapabilitySummary(LmStudioModelInfo model)
    {
        var parts = new List<string>();
        if (model.TrainedForToolUse)
        {
            parts.Add("tools");
        }

        if (model.Vision)
        {
            parts.Add("vision");
        }

        if (model.ReasoningOptions.Count > 0 || model.ReasoningDefault.Length > 0)
        {
            parts.Add("reasoning");
        }

        if (model.MaxContextLength is int context && context > 0)
        {
            parts.Add($"{context} ctx");
        }

        return parts.Count == 0 ? "LM Studio chat model." : string.Join(" / ", parts);
    }

    private static string OllamaCapabilitySummary(OllamaModelInfo model)
    {
        var parts = new[]
        {
            model.ParameterSize,
            model.QuantizationLevel,
            model.Family,
            model.Format,
            model.ContextLength is int context && context > 0 ? $"{context} ctx" : ""
        };
        var observed = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return observed.Length == 0 ? "Ollama model." : string.Join(" / ", observed);
    }

    private static string LlamaCppCapabilitySummary(LlamaCppRuntimeModel model)
    {
        var parts = new[]
        {
            model.Quantization,
            model.ContextLength is int context && context > 0 ? $"{context} ctx" : "",
            model.ParallelSlots is int slots && slots > 0 ? $"{slots} slots" : ""
        };
        var observed = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return observed.Length == 0 ? "llama.cpp model." : string.Join(" / ", observed);
    }

    private static string Prefer(string first, string second, string fallback = "")
    {
        if (!string.IsNullOrWhiteSpace(first) && !first.Equals(fallback, StringComparison.OrdinalIgnoreCase))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? first : second;
    }

    internal static string SafeStatusForDisplay(string value, string apiToken = "")
    {
        var normalized = ProviderConfigurationControlService.SanitizeError(value, apiToken);
        normalized = InstanceIdentifierRegex().Replace(normalized, "$1[redacted]");
        normalized = HttpUrlRegex().Replace(normalized, "[remote URL]");
        normalized = FileUriRegex().Replace(normalized, "[local path]");
        normalized = QuotedAbsolutePathRegex().Replace(normalized, "[local path]");
        normalized = WindowsPathRegex().Replace(normalized, "[local path]");
        normalized = UnixPathRegex().Replace(normalized, "[local path]");
        return SafeText(normalized, MaximumStatusLength);
    }

    private static string SafeStatus(string value) => SafeStatusForDisplay(value);

    private static string SafeText(string value, int maximumLength)
    {
        var normalized = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maximumLength)
        {
            return normalized;
        }

        return normalized[..(maximumLength - 12)].TrimEnd()
            + "...#"
            + ShortFingerprint(normalized);
    }

    private static string ShortFingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    private static void AppendConnectionFingerprintValues(
        IncrementalHash hash,
        string sessionId,
        ModelProviderConfig sharedConfig)
    {
        AppendFingerprintValue(hash, (sessionId ?? "").Trim());
        AppendFingerprintValue(hash, sharedConfig.BaseUrl.Trim().TrimEnd('/'));
        AppendFingerprintValue(hash, ModelProviderApiModes.Normalize(sharedConfig.ApiMode));
        AppendFingerprintValue(hash, sharedConfig.ApiToken);
    }

    private static void AppendFingerprintValue(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    [GeneratedRegex(@"(?i)file:///?[^\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex FileUriRegex();

    [GeneratedRegex("""(?i)(\b"?instance(?:_id)?"?\s*[:=]\s*"?)[^"\s,;}\]]+""", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdentifierRegex();

    [GeneratedRegex("""(?i)\bhttps?://[^\s<>"']+""", RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex("""(?i)["'](?:[A-Z]:[\\/]|\\\\|/)[^"'\r\n]+["']""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedAbsolutePathRegex();

    [GeneratedRegex("""(?i)(?:[A-Z]:[\\/]|\\\\)[^"'\r\n,;|]+""", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("""(?<![:/\w])/(?=[A-Za-z0-9._~-])[^"'\r\n,;|]+""", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();
}
