using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>
/// Deterministic fault boundary around a provider client. No injected branch is
/// placed in ModelProviderClient, so production protocol parsing remains
/// unchanged. The decorator retains content-free observations only.
/// </summary>
public sealed class FaultInjectingModelProviderClient : IModelProviderClient, IStreamingModelProviderClient, IDisposable
{
    private const string SyntheticPartial = "[injected-partial]";
    private const string SyntheticMalformedChunk = "\uFFFD{";

    private readonly IModelProviderClient _inner;
    private readonly IStreamingModelProviderClient? _streamingInner;
    private readonly ArenaFaultProfileContract _profile;
    private readonly ArenaFaultInjectionOptions _options;
    private readonly ImmutableArray<ScheduledInjection> _schedule;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly object _scheduleSync = new();
    private readonly object _observationSync = new();
    private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, ArenaFaultObservation> _observations = new();
    private long _nextSequence = -1;
    private int _activeRequests;
    private int _maximumObservedConcurrency;
    private bool _disposed;

    public FaultInjectingModelProviderClient(
        IModelProviderClient inner,
        ArenaFaultProfileContract profile,
        ArenaFaultInjectionOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _streamingInner = inner as IStreamingModelProviderClient;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _options = options ?? new ArenaFaultInjectionOptions();
        ValidateOptions(_options);

        var validation = ArenaContractCodec.Validate(profile);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
        }

        _schedule =
        [
            .. profile.Injections
                .Where(item => item.Target is ArenaFaultTarget.Provider or ArenaFaultTarget.Network)
                .Select(item => new ScheduledInjection(item, SeededRank(profile.Seed, item.Id)))
                .OrderBy(item => item.Injection.AtSequence)
                .ThenBy(item => item.SeededRank)
                .ThenBy(item => item.Injection.Id, StringComparer.Ordinal)
        ];
        _concurrencyGate = new SemaphoreSlim(_options.MaximumConcurrentRequests, _options.MaximumConcurrentRequests);
    }

    public long ScheduledInvocationCount => Interlocked.Read(ref _nextSequence) + 1;

    public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

    public string ArmedProfileId => _profile.Id;

    public bool IsArmed => !Volatile.Read(ref _disposed);

    public static FaultInjectingModelProviderClient Arm(
        IModelProviderClient inner,
        ArenaFaultProfileContract profile,
        ArenaFaultInjectionOptions? options = null) =>
        new(inner, profile, options);

    public ImmutableArray<ArenaFaultObservation> SnapshotObservations()
    {
        lock (_observationSync)
        {
            return [.. _observations.Values];
        }
    }

    public void Disarm() => Dispose();

    public Task<ModelProviderModels> ListModelsAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ArenaProviderFaultOperation.ModelDiscovery,
            config,
            cancellationToken,
            () => _inner.ListModelsAsync(config, cancellationToken),
            InjectedModels);

    public Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ArenaProviderFaultOperation.ChatCompletion,
            config,
            cancellationToken,
            () => _inner.CompleteChatAsync(config, messages, cancellationToken),
            InjectedCompletion);

    public Task<ModelCompletionResult> CompleteChatStreamingAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ArenaProviderFaultOperation.StreamingChatCompletion,
            config,
            cancellationToken,
            () => _streamingInner is not null
                ? _streamingInner.CompleteChatStreamingAsync(config, messages, progress, cancellationToken)
                : CompleteWithNonStreamingFallbackAsync(config, messages, progress, cancellationToken),
            (fault, sequence) => InjectedStreamingCompletion(fault, sequence, config, progress));

    public void Dispose()
    {
        // Disarm is a lifecycle boundary, not cancellation. Requests that
        // entered before this write must be allowed to release the semaphore
        // and complete normally. SemaphoreSlim is therefore left for GC rather
        // than being disposed underneath an in-flight provider operation.
        Volatile.Write(ref _disposed, true);
    }

    private async Task<T> ExecuteAsync<T>(
        ArenaProviderFaultOperation operation,
        ModelProviderConfig config,
        CancellationToken cancellationToken,
        Func<Task<T>> innerCall,
        Func<ScheduledFault, long, T> injectedResult)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var sequence = Interlocked.Increment(ref _nextSequence);
        var scheduled = SelectFault(sequence);
        if (scheduled is not null)
        {
            try
            {
                if (scheduled.Injection.Kind == ArenaFaultKind.Timeout)
                {
                    if (scheduled.EffectiveDurationMilliseconds > 0)
                    {
                        await Task.Delay(scheduled.EffectiveDurationMilliseconds, cancellationToken).ConfigureAwait(false);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = injectedResult(scheduled, sequence);
                RecordObservation(scheduled, sequence, operation, ArenaFaultObservedOutcome.InjectedFailure);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordObservation(scheduled, sequence, operation, ArenaFaultObservedOutcome.CallerCancelled);
                throw;
            }
        }

        await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var active = Interlocked.Increment(ref _activeRequests);
        UpdateMaximumConcurrency(active);
        try
        {
            return await innerCall().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
            _concurrencyGate.Release();
        }
    }

    private ScheduledFault? SelectFault(long sequence)
    {
        lock (_scheduleSync)
        {
            foreach (var scheduled in _schedule)
            {
                var injection = scheduled.Injection;
                if (sequence < injection.AtSequence) continue;

                var occurrence = _occurrences.GetValueOrDefault(injection.Id);
                if (occurrence >= injection.MaxOccurrences) continue;
                if (!ShouldTrigger(_profile.Seed, injection.Id, sequence, injection.Intensity)) continue;

                occurrence++;
                _occurrences[injection.Id] = occurrence;
                return new ScheduledFault(
                    injection,
                    occurrence,
                    Math.Min(injection.DurationMilliseconds, _options.MaximumInjectedDelayMilliseconds));
            }
        }

        return null;
    }

    private static bool ShouldTrigger(string seed, string injectionId, long sequence, int intensity)
    {
        if (intensity >= 100) return true;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\n{injectionId}\n{sequence}"));
        var roll = ((bytes[0] << 8) | bytes[1]) % 100;
        return roll < intensity;
    }

    private static ulong SeededRank(string seed, string injectionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\n{injectionId}"));
        return ((ulong)bytes[0] << 56)
            | ((ulong)bytes[1] << 48)
            | ((ulong)bytes[2] << 40)
            | ((ulong)bytes[3] << 32)
            | ((ulong)bytes[4] << 24)
            | ((ulong)bytes[5] << 16)
            | ((ulong)bytes[6] << 8)
            | bytes[7];
    }

    private static ModelCompletionResult InjectedCompletion(ScheduledFault fault, long sequence) =>
        new(
            false,
            "",
            "",
            "",
            "",
            fault.Injection.Kind == ArenaFaultKind.Timeout ? fault.EffectiveDurationMilliseconds : 0,
            0,
            0,
            0,
            ErrorCode(fault.Injection.Kind, sequence),
            DateTimeOffset.UtcNow);

    private static ModelProviderModels InjectedModels(ScheduledFault fault, long sequence) =>
        new(false, "", [], ErrorCode(fault.Injection.Kind, sequence), DateTimeOffset.UtcNow);

    private static ModelCompletionResult InjectedStreamingCompletion(
        ScheduledFault fault,
        long sequence,
        ModelProviderConfig config,
        IProgress<string>? progress)
    {
        if (fault.Injection.Kind == ArenaFaultKind.MalformedStream)
        {
            progress?.Report(SyntheticMalformedChunk);
        }
        else if (fault.Injection.Kind == ArenaFaultKind.Interruption)
        {
            progress?.Report(SyntheticPartial);
        }

        return InjectedCompletion(fault, sequence) with { Model = config.Model };
    }

    private async Task<ModelCompletionResult> CompleteWithNonStreamingFallbackAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await _inner.CompleteChatAsync(config, messages, cancellationToken).ConfigureAwait(false);
        if (result.Ok && !string.IsNullOrEmpty(result.Text)) progress?.Report(result.Text);
        return result;
    }

    private static string ErrorCode(ArenaFaultKind kind, long sequence) =>
        $"Injected {FaultLabel(kind)} fault at provider sequence {sequence}.";

    private static string FaultLabel(ArenaFaultKind kind) => kind switch
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

    private void RecordObservation(
        ScheduledFault scheduled,
        long sequence,
        ArenaProviderFaultOperation operation,
        ArenaFaultObservedOutcome outcome)
    {
        var cause = new ArenaEvidenceAssertion(
            $"fault-cause:{StableSuffix(_profile.Id, scheduled.Injection.Id, sequence)}",
            ArenaEvidenceState.Observed,
            $"The {FaultLabel(scheduled.Injection.Kind)} fault was injected by the selected profile.",
            ReferenceId: _profile.Id);
        var recovery = new ArenaEvidenceAssertion(
            $"fault-recovery:{StableSuffix(_profile.Id, scheduled.Injection.Id, sequence)}",
            ArenaEvidenceState.Unavailable,
            "Recovery is not measured at the provider decorator boundary.",
            Limitation: outcome == ArenaFaultObservedOutcome.CallerCancelled
                ? "Caller cancellation pre-empted recovery observation."
                : "A higher-level retry or recovery runner must provide separate observed evidence.");
        var observation = new ArenaFaultObservation(
            _profile.Id,
            scheduled.Injection.Id,
            sequence,
            scheduled.Occurrence,
            scheduled.Injection.Kind,
            operation,
            outcome,
            cause,
            recovery);

        lock (_observationSync)
        {
            while (_observations.Count >= _options.MaximumRetainedObservations)
            {
                _observations.Remove(_observations.Keys.First());
            }
            _observations[sequence] = observation;
        }
    }

    private static string StableSuffix(string profileId, string injectionId, long sequence)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{profileId}\n{injectionId}\n{sequence}"));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private void UpdateMaximumConcurrency(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumObservedConcurrency);
            if (active <= current || Interlocked.CompareExchange(ref _maximumObservedConcurrency, active, current) == current) return;
        }
    }

    private static void ValidateOptions(ArenaFaultInjectionOptions options)
    {
        if (options.MaximumConcurrentRequests is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum concurrency must be from 1 through 256.");
        if (options.MaximumInjectedDelayMilliseconds is < 0 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum injected delay must be from 0 through 60000 milliseconds.");
        if (options.MaximumRetainedObservations is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Observation retention must be from 1 through 100000.");
    }

    private sealed record ScheduledInjection(ArenaFaultInjection Injection, ulong SeededRank);

    private sealed record ScheduledFault(
        ArenaFaultInjection Injection,
        int Occurrence,
        int EffectiveDurationMilliseconds);
}
