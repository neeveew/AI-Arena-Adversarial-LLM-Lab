using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed record AIArenaGenerationHistoryControlItem(
    string Id,
    string Kind,
    string Label,
    string Style,
    string Intensity,
    string RolePack,
    string Absurdity,
    string Seed,
    bool SeedDeterministic,
    string ReplayMode,
    DateTimeOffset CreatedAt);

internal sealed record AIArenaMatchGenerationControlState(
    string SessionId,
    string MatchType,
    string Scenario,
    string GlobalInstruction,
    bool QualityContractPresent,
    string Style,
    string Intensity,
    string RolePack,
    string Absurdity,
    string Seed,
    bool InternetEnabled,
    IReadOnlyList<AIArenaGenerationHistoryControlItem> History);

internal sealed record AIArenaMatchGenerationReceipt(
    string Operation,
    string Label,
    string Seed,
    string Style,
    string Intensity,
    string SessionId,
    string HistoryId,
    bool SeedDeterministic,
    string ReplayMode);

internal sealed record AIArenaMatchGenerationControlData(
    AIArenaMatchGenerationControlState State,
    AIArenaMatchGenerationReceipt? Receipt);

internal sealed record AIArenaMatchGenerationControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaMatchGenerationControlData Data);

internal sealed record AIArenaMatchGenerationOptions(
    string Style = "",
    string Intensity = "",
    string RolePack = "",
    string Absurdity = "",
    string Seed = "",
    string Prompt = "",
    string Query = "");

/// <summary>
/// Headless match-generation facade shared by PowerShell automation and the same native
/// generation engine used by Match Setup. It owns validation and auditable receipts,
/// while the host supplies busy-state and session-refresh boundaries.
/// </summary>
internal sealed class ScenarioGenerationControlService
{
    private const int MaxInputLength = 500;
    private readonly MatchGenerationService matchGeneration;
    private readonly SessionStore sessionStore;
    private readonly Func<WpfSettings> settings;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<string, Func<CancellationToken, Task>, Task> runBusyAsync;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;
    private readonly Func<string?, CancellationToken, Task> loadSessionsAsync;

    public ScenarioGenerationControlService(
        MatchGenerationService matchGeneration,
        SessionStore sessionStore,
        Func<WpfSettings> settings,
        Func<CoreSessionSummary?> activeSession,
        Func<string, Func<CancellationToken, Task>, Task> runBusyAsync,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        Func<string?, CancellationToken, Task> loadSessionsAsync)
    {
        this.matchGeneration = matchGeneration;
        this.sessionStore = sessionStore;
        this.settings = settings;
        this.activeSession = activeSession;
        this.runBusyAsync = runBusyAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.loadSessionsAsync = loadSessionsAsync;
    }

    public async Task<AIArenaMatchGenerationControlState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = activeSession()?.Id ?? "";
        var snapshot = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        return ToControlState(sessionId, snapshot);
    }

    public async Task<AIArenaMatchGenerationControlResult> GenerateAsync(
        string operation,
        AIArenaMatchGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var normalizedOperation = NormalizeOperation(operation);
        if (normalizedOperation is null)
        {
            return await FailureAsync(
                "invalid_argument",
                "Generation mode must be random, ai, current, or wild.",
                cancellationToken);
        }

        var inputError = ValidateOptions(options);
        if (inputError is not null)
        {
            return await FailureAsync("invalid_argument", inputError, cancellationToken);
        }

        var session = activeSession();
        if (session is null)
        {
            return await FailureAsync("not_available", "No active session is available for generation.", cancellationToken);
        }

        var defaults = settings();
        var style = ValueOrDefault(options.Style, defaults.RandomSeedStyle, "auto");
        var intensity = ValueOrDefault(options.Intensity, defaults.RandomSeedIntensity, "normal");
        var rolePack = ValueOrDefault(options.RolePack, defaults.RandomSeedRolePack, "auto");
        var absurdity = ValueOrDefault(options.Absurdity, defaults.RandomSeedAbsurdity, "grounded");
        MatchGenerationResult? result = null;
        var operationStarted = false;
        var status = normalizedOperation switch
        {
            "ai" => "Generating AI Choice match...",
            "current" => "Generating Current Topics match...",
            "wild" => "Generating Wild Seed match...",
            _ => "Generating Random Seed match..."
        };

        await runBusyAsync(status, async operationCancellationToken =>
        {
            operationStarted = true;
            result = normalizedOperation switch
            {
                "ai" => await matchGeneration.GenerateAiChoiceAsync(
                    session.Id,
                    rolePack,
                    intensity,
                    absurdity,
                    options.Prompt,
                    operationCancellationToken),
                "current" => await matchGeneration.GenerateCurrentTopicsSeedAsync(
                    session.Id,
                    rolePack,
                    intensity,
                    absurdity,
                    options.Query,
                    operationCancellationToken),
                "wild" => await matchGeneration.GenerateYoloSeedAsync(
                    session.Id,
                    rolePack,
                    intensity,
                    absurdity,
                    EmptyToNull(options.Seed),
                    operationCancellationToken),
                _ => await matchGeneration.GenerateRandomSeedAsync(
                    session.Id,
                    style,
                    intensity,
                    rolePack,
                    absurdity,
                    EmptyToNull(options.Seed),
                    operationCancellationToken)
            };

            var completion = result.Ok
                ? $"{GenerationLabel(normalizedOperation)} generated: {result.Label}."
                : $"{GenerationLabel(normalizedOperation)} failed: {result.Error}";
            await refreshActiveSessionAsync(completion, operationCancellationToken);
        });

        if (result is null)
        {
            var code = operationStarted ? "operation_incomplete" : "not_available";
            var incompleteMessage = operationStarted
                ? "Generation stopped before producing a result. Inspect the returned arena state and status."
                : "Generation did not start because the arena is busy.";
            return await FailureAsync(code, incompleteMessage, cancellationToken);
        }

        var state = await CaptureAsync(cancellationToken);
        if (!result.Ok)
        {
            return Failure("generation_failed", result.Error, state);
        }

        var historyItem = state.History.FirstOrDefault();
        var seedDeterministic = historyItem?.SeedDeterministic
            ?? ScenarioAuditPolicy.IsSeedDeterministic(normalizedOperation, result.Seed);
        var receipt = new AIArenaMatchGenerationReceipt(
            normalizedOperation,
            result.Label,
            result.Seed,
            result.Style,
            result.Intensity,
            state.SessionId,
            historyItem?.Id ?? "",
            seedDeterministic,
            historyItem?.ReplayMode ?? ScenarioAuditPolicy.ReplayMode(normalizedOperation, result.Seed));
        return Success($"{GenerationLabel(normalizedOperation)} generated: {result.Label}.", state, receipt);
    }

    public async Task<AIArenaMatchGenerationControlResult> ReplayAsync(
        string historyId,
        bool newSession,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(historyId))
        {
            return await FailureAsync("missing_argument", "Match replay requires args.id.", cancellationToken);
        }

        var session = activeSession();
        if (session is null)
        {
            return await FailureAsync("not_available", "No active session is available for replay.", cancellationToken);
        }

        MatchGenerationResult? result = null;
        var operationStarted = false;
        var operation = newSession ? "replay.new" : "replay";
        await runBusyAsync(newSession ? "Creating replay session..." : "Replaying generated match...", async operationCancellationToken =>
        {
            operationStarted = true;
            result = newSession
                ? await matchGeneration.ReplayGenerationToNewSessionAsync(session.Id, historyId.Trim(), operationCancellationToken)
                : await matchGeneration.ReplayGenerationAsync(session.Id, historyId.Trim(), operationCancellationToken);
            if (!result.Ok)
            {
                await refreshActiveSessionAsync($"Replay failed: {result.Error}", operationCancellationToken);
                return;
            }

            if (newSession)
            {
                await loadSessionsAsync(result.Label, operationCancellationToken);
            }
            else
            {
                await refreshActiveSessionAsync($"Replayed generated match: {result.Label}.", operationCancellationToken);
            }
        });

        if (result is null)
        {
            var code = operationStarted ? "operation_incomplete" : "not_available";
            var incompleteMessage = operationStarted
                ? "Replay stopped before producing a result. Inspect the returned arena state and status."
                : "Replay did not start because the arena is busy.";
            return await FailureAsync(code, incompleteMessage, cancellationToken);
        }

        var state = await CaptureAsync(cancellationToken);
        if (!result.Ok)
        {
            return Failure("replay_failed", result.Error, state);
        }

        var historyItem = state.History.FirstOrDefault();
        var seedDeterministic = historyItem?.SeedDeterministic
            ?? ScenarioAuditPolicy.IsSeedDeterministic("", result.Seed);
        var receipt = new AIArenaMatchGenerationReceipt(
            operation,
            result.Label,
            result.Seed,
            result.Style,
            result.Intensity,
            state.SessionId,
            historyId,
            seedDeterministic,
            historyItem?.ReplayMode ?? ScenarioAuditPolicy.ReplayMode("", result.Seed));
        var message = newSession
            ? $"Created replay session: {result.Label}."
            : $"Replayed generated match: {result.Label}.";
        return Success(message, state, receipt);
    }

    private async Task<AIArenaMatchGenerationControlResult> FailureAsync(
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        return Failure(errorCode, message, await CaptureAsync(cancellationToken));
    }

    private static AIArenaMatchGenerationControlState ToControlState(string sessionId, ArenaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return new AIArenaMatchGenerationControlState(sessionId, "", "", "", false, "", "", "", "", "", false, []);
        }

        var history = snapshot.GenerationHistory
            .OrderByDescending(item => item.CreatedAt)
            .Take(20)
            .Select(item => new AIArenaGenerationHistoryControlItem(
                item.Id,
                item.Kind,
                item.Label,
                item.Style,
                item.Intensity,
                item.RolePack,
                item.Absurdity,
                item.ScenarioSeed,
                ScenarioAuditPolicy.IsSeedDeterministic(item.Kind, item.ScenarioSeed),
                ScenarioAuditPolicy.ReplayMode(item.Kind, item.ScenarioSeed),
                SafeCreatedAt(item.CreatedAt)))
            .ToArray();
        return new AIArenaMatchGenerationControlState(
            sessionId,
            snapshot.MatchType,
            snapshot.Engine.Steering.Topic,
            snapshot.Engine.Steering.Global,
            ScenarioAuditPolicy.HasCompleteQualityContract(snapshot.Engine.Steering.Global),
            snapshot.ScenarioGenerator.Style,
            snapshot.ScenarioGenerator.Intensity,
            snapshot.ScenarioGenerator.RolePack,
            snapshot.ScenarioGenerator.Absurdity,
            snapshot.ScenarioGenerator.Seed,
            snapshot.Engine.Internet.UseInternet,
            history);
    }

    private static string? ValidateOptions(AIArenaMatchGenerationOptions options)
    {
        foreach (var (name, value) in new[]
        {
            ("style", options.Style),
            ("intensity", options.Intensity),
            ("rolePack", options.RolePack),
            ("absurdity", options.Absurdity),
            ("seed", options.Seed),
            ("prompt", options.Prompt),
            ("query", options.Query)
        })
        {
            if ((value ?? "").Length > MaxInputLength)
            {
                return $"args.{name} exceeds the {MaxInputLength}-character limit.";
            }
        }

        // Length was all this checked, so an unrecognised style or intensity got
        // through and the generator quietly substituted its default: asking for
        // a misspelled style produced a balanced match, regenerated the session,
        // and answered "Random Seed generated". Generation is destructive - it
        // replaces the scenario and cast - so a typo has to be refused, not
        // reinterpreted.
        if (!MatchGenerationService.TryNormalizeStyle(options.Style, out _))
        {
            return $"args.style is not a known style: '{options.Style}'.";
        }

        if (!MatchGenerationService.TryNormalizeIntensity(options.Intensity, out _))
        {
            return $"args.intensity is not a known intensity: '{options.Intensity}'.";
        }

        if (!MatchGenerationService.TryNormalizeRolePack(options.RolePack, out _))
        {
            return $"args.rolePack is not a known role pack: '{options.RolePack}'.";
        }

        if (!MatchGenerationService.TryNormalizeAbsurdity(options.Absurdity, out _))
        {
            return $"args.absurdity is not a known absurdity level: '{options.Absurdity}'.";
        }

        return null;
    }

    private static DateTimeOffset SafeCreatedAt(double createdAt)
    {
        if (double.IsNaN(createdAt) || double.IsInfinity(createdAt))
        {
            return DateTimeOffset.UnixEpoch;
        }

        var seconds = (long)Math.Clamp(
            createdAt,
            DateTimeOffset.MinValue.ToUnixTimeSeconds(),
            DateTimeOffset.MaxValue.ToUnixTimeSeconds());
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string? NormalizeOperation(string operation)
    {
        return (operation ?? "").Trim().ToLowerInvariant() switch
        {
            "random" => "random",
            "ai" or "ai-choice" or "choice" => "ai",
            "current" or "current-topics" or "topics" => "current",
            "wild" or "yolo" => "wild",
            _ => null
        };
    }

    private static string GenerationLabel(string operation) => operation switch
    {
        "ai" => "AI Choice",
        "current" => "Current Topics",
        "wild" => "Wild Seed",
        _ => "Random Seed"
    };

    private static string ValueOrDefault(string value, string configured, string fallback) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : fallback;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AIArenaMatchGenerationControlResult Success(
        string message,
        AIArenaMatchGenerationControlState state,
        AIArenaMatchGenerationReceipt receipt) =>
        new(true, "", message, new AIArenaMatchGenerationControlData(state, receipt));

    private static AIArenaMatchGenerationControlResult Failure(
        string errorCode,
        string message,
        AIArenaMatchGenerationControlState state) =>
        new(false, errorCode, message, new AIArenaMatchGenerationControlData(state, null));
}
