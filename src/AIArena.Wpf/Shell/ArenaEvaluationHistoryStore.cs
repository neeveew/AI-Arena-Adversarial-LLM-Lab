using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal static class ArenaEvaluationHistorySchemas
{
    public const string History = "ai_arena.evaluation_history.v1";
}

internal sealed record ArenaEvaluationHistoryEntry(
    ArenaEvaluationRecord Evaluation,
    ArenaRuntimeQaReport? Qa,
    bool IsBaseline);

internal sealed record ArenaEvaluationHistorySnapshot(
    string Schema,
    IReadOnlyList<ArenaEvaluationHistoryEntry> Entries)
{
    public ArenaEvaluationHistoryEntry? BaselineFor(string scenarioFingerprint)
    {
        return Entries.FirstOrDefault(entry =>
            entry.IsBaseline
            && entry.Evaluation.ScenarioFingerprint.Equals(scenarioFingerprint, StringComparison.Ordinal));
    }
}

/// <summary>
/// Stores bounded aggregate evaluation evidence. Transcript bodies and raw
/// provider responses never enter this contract; the only replay content is
/// the existing secret-free Match Setup package.
/// </summary>
internal sealed class ArenaEvaluationHistoryStore
{
    internal const int MaximumEntries = 48;
    internal const int MaximumHistoryBytes = 4 * 1024 * 1024;
    private const int MaximumReplayPackageBytes = MatchSetupPackageCodec.MaxPackageBytes;
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex SafeGateIdRegex = new(
        @"^[a-z0-9][a-z0-9.-]{0,95}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveTextRegex = new(
        @"(?ix)(?:https?://|\b(?:api[_\s-]?key|access[_\s-]?token|authorization|bearer|client[_\s-]?secret|password|refresh[_\s-]?token)\b|\bsk-(?:proj-)?[A-Za-z0-9_-]{12,}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly ArenaEvaluationService evaluationService;
    private readonly object pathLock;

    public ArenaEvaluationHistoryStore(
        string? historyPath = null,
        ArenaEvaluationService? evaluationService = null)
    {
        HistoryPath = string.IsNullOrWhiteSpace(historyPath)
            ? NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "arena-evaluation-history.json")
            : Path.GetFullPath(historyPath);
        this.evaluationService = evaluationService ?? new ArenaEvaluationService();
        pathLock = PathLocks.GetOrAdd(HistoryPath, static _ => new object());
    }

    public string HistoryPath { get; }

    public string LastLoadWarning { get; private set; } = "";

    public ArenaEvaluationHistorySnapshot Load()
    {
        lock (pathLock)
        {
            return LoadCore(recoverCorrupt: true);
        }
    }

    public ArenaEvaluationHistorySnapshot Save(
        ArenaEvaluationRecord evaluation,
        ArenaRuntimeQaReport? qa = null,
        bool setBaseline = false)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var normalizedEvaluation = evaluationService.NormalizeForStorage(evaluation)
            ?? throw new InvalidOperationException("Evaluation evidence is invalid or does not match its replay package.");
        var normalizedQa = NormalizeQa(qa);
        if (qa is not null && normalizedQa is null)
        {
            throw new InvalidOperationException("Runtime QA evidence contains unsupported or unsafe fields.");
        }

        if (Encoding.UTF8.GetByteCount(normalizedEvaluation.PortableSetupJson) > MaximumReplayPackageBytes)
        {
            throw new InvalidOperationException("Evaluation replay package exceeds the bounded history limit.");
        }

        lock (pathLock)
        {
            var current = LoadCore(recoverCorrupt: true);
            var previous = current.Entries.FirstOrDefault(entry =>
                entry.Evaluation.RunId.Equals(normalizedEvaluation.RunId, StringComparison.Ordinal));
            var makeBaseline = setBaseline || previous?.IsBaseline == true;
            var entries = current.Entries
                .Where(entry => !entry.Evaluation.RunId.Equals(normalizedEvaluation.RunId, StringComparison.Ordinal))
                .Select(entry => setBaseline
                    && entry.Evaluation.ScenarioFingerprint.Equals(normalizedEvaluation.ScenarioFingerprint, StringComparison.Ordinal)
                        ? entry with { IsBaseline = false }
                        : entry)
                .Append(new ArenaEvaluationHistoryEntry(normalizedEvaluation, normalizedQa, makeBaseline))
                .OrderByDescending(entry => entry.Evaluation.CapturedAt)
                .ThenBy(entry => entry.Evaluation.RunId, StringComparer.Ordinal)
                .ToList();
            entries = NormalizeEntries(entries).ToList();
            var file = BoundForWrite(entries);
            var json = JsonSerializer.Serialize(file, JsonOptions);
            JsonFileRecovery.WriteTextReplacing(HistoryPath, json);
            LastLoadWarning = "";
            return Snapshot(file.Entries);
        }
    }

    private ArenaEvaluationHistorySnapshot LoadCore(bool recoverCorrupt)
    {
        LastLoadWarning = "";
        if (!File.Exists(HistoryPath))
        {
            return Snapshot([]);
        }

        try
        {
            var info = new FileInfo(HistoryPath);
            if (info.Length > MaximumHistoryBytes)
            {
                LastLoadWarning = $"Evaluation history exceeds the {MaximumHistoryBytes:N0}-byte limit and was left unchanged.";
                return Snapshot([]);
            }

            var json = File.ReadAllText(HistoryPath);
            if (Encoding.UTF8.GetByteCount(json) > MaximumHistoryBytes)
            {
                LastLoadWarning = $"Evaluation history exceeds the {MaximumHistoryBytes:N0}-byte limit and was left unchanged.";
                return Snapshot([]);
            }

            var file = JsonSerializer.Deserialize<ArenaEvaluationHistoryFile>(json, JsonOptions)
                ?? new ArenaEvaluationHistoryFile();
            if (!file.Schema.Equals(ArenaEvaluationHistorySchemas.History, StringComparison.Ordinal))
            {
                throw new JsonException($"Unsupported evaluation history schema '{file.Schema}'.");
            }

            return Snapshot(NormalizeEntries(file.Entries));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LastLoadWarning = recoverCorrupt
                ? JsonFileRecovery.BackupCorruptFile(HistoryPath, "Evaluation history", ex)
                : $"Evaluation history is invalid: {ex.Message}";
            return Snapshot([]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadWarning = $"Evaluation history could not be read and was left unchanged: {ex.Message}";
            return Snapshot([]);
        }
    }

    private IReadOnlyList<ArenaEvaluationHistoryEntry> NormalizeEntries(
        IEnumerable<ArenaEvaluationHistoryEntry>? entries)
    {
        if (entries is null)
        {
            return [];
        }

        var normalized = new List<ArenaEvaluationHistoryEntry>();
        foreach (var entry in entries.Where(entry => entry is not null))
        {
            var evaluation = evaluationService.NormalizeForStorage(entry.Evaluation);
            if (evaluation is null
                || Encoding.UTF8.GetByteCount(evaluation.PortableSetupJson) > MaximumReplayPackageBytes)
            {
                continue;
            }

            var qa = NormalizeQa(entry.Qa);
            if (entry.Qa is not null && qa is null)
            {
                continue;
            }

            normalized.Add(new ArenaEvaluationHistoryEntry(evaluation, qa, entry.IsBaseline));
        }

        var baselineScenarios = new HashSet<string>(StringComparer.Ordinal);
        var bounded = normalized
            .GroupBy(entry => entry.Evaluation.RunId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entry => entry.Evaluation.CapturedAt)
                .First())
            .OrderByDescending(entry => entry.Evaluation.CapturedAt)
            .ThenBy(entry => entry.Evaluation.RunId, StringComparer.Ordinal)
            .Select(entry => entry.IsBaseline
                && !baselineScenarios.Add(entry.Evaluation.ScenarioFingerprint)
                    ? entry with { IsBaseline = false }
                    : entry)
            .ToList();
        while (bounded.Count > MaximumEntries)
        {
            RemoveOldest(bounded);
        }

        return bounded.ToArray();
    }

    private ArenaEvaluationHistoryFile BoundForWrite(List<ArenaEvaluationHistoryEntry> entries)
    {
        while (entries.Count > MaximumEntries)
        {
            RemoveOldest(entries);
        }

        while (true)
        {
            var file = new ArenaEvaluationHistoryFile
            {
                Entries = entries.ToList()
            };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= MaximumHistoryBytes)
            {
                return file;
            }

            if (entries.Count <= 1)
            {
                throw new InvalidOperationException("A single evaluation record exceeds the bounded history file limit.");
            }

            RemoveOldest(entries);
        }
    }

    private static void RemoveOldest(List<ArenaEvaluationHistoryEntry> entries)
    {
        var index = entries.FindLastIndex(entry => !entry.IsBaseline);
        if (index < 0)
        {
            index = entries.Count - 1;
        }

        entries.RemoveAt(index);
    }

    private static ArenaRuntimeQaReport? NormalizeQa(ArenaRuntimeQaReport? qa)
    {
        if (qa is null || qa.Gates is null || qa.Gates.Count > 64)
        {
            return null;
        }

        var gates = new List<ArenaRuntimeQaGate>(qa.Gates.Count);
        foreach (var gate in qa.Gates)
        {
            if (gate is null
                || !SafeGateIdRegex.IsMatch(gate.Id ?? "")
                || !SafeGateStatus(gate.Status)
                || !SafeText(gate.Explanation, 512)
                || !SafeText(gate.Evidence, 256))
            {
                return null;
            }

            gates.Add(gate with
            {
                Explanation = CollapseWhitespace(gate.Explanation),
                Evidence = CollapseWhitespace(gate.Evidence)
            });
        }

        if (qa.OverallReadiness is not ("ready" or "partial" or "blocked")
            || !SafeText(qa.Summary, 512))
        {
            return null;
        }

        var passed = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Pass);
        var warnings = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Warn);
        var failed = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Fail);
        var unavailable = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Unavailable);
        return new ArenaRuntimeQaReport(
            qa.OverallReadiness,
            qa.OverallReadiness.Equals("ready", StringComparison.Ordinal),
            passed,
            warnings,
            failed,
            unavailable,
            CollapseWhitespace(qa.Summary),
            gates);
    }

    private static bool SafeGateStatus(string status)
    {
        return status is ArenaQaGateStatuses.Pass
            or ArenaQaGateStatuses.Warn
            or ArenaQaGateStatuses.Fail
            or ArenaQaGateStatuses.Unavailable;
    }

    private static bool SafeText(string? value, int maximumLength)
    {
        return value is not null
            && value.Length <= maximumLength
            && !SensitiveTextRegex.IsMatch(value);
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static ArenaEvaluationHistorySnapshot Snapshot(
        IEnumerable<ArenaEvaluationHistoryEntry> entries)
    {
        return new ArenaEvaluationHistorySnapshot(
            ArenaEvaluationHistorySchemas.History,
            Array.AsReadOnly(entries.ToArray()));
    }

    private sealed class ArenaEvaluationHistoryFile
    {
        public string Schema { get; set; } = ArenaEvaluationHistorySchemas.History;

        public List<ArenaEvaluationHistoryEntry> Entries { get; set; } = [];
    }
}
