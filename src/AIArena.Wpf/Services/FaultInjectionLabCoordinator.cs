using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf.Services;

/// <summary>
/// Process-only lifecycle for the Fault-Injection Lab. This coordinator runs a
/// real provider probe through the Core decorator, but never stores a provider
/// configuration, credential, prompt, response, or raw provider error.
/// </summary>
public sealed class FaultInjectionLabCoordinator : IDisposable
{
    private readonly FaultInjectionLabControl control;
    private readonly IModelProviderClient? providerClient;
    private readonly Func<ModelProviderConfig?>? configProvider;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private readonly object lifecycleSync = new();
    private FaultInjectingModelProviderClient? armedClient;
    private CancellationTokenSource? activeProbeCancellation;
    private bool disposed;

    public FaultInjectionLabCoordinator(
        FaultInjectionLabControl control,
        IModelProviderClient? providerClient = null,
        Func<ModelProviderConfig?>? configProvider = null,
        TimeProvider? timeProvider = null)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        this.providerClient = providerClient;
        this.configProvider = configProvider;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        control.Initialize(this);
        var connected = IsConnected;
        control.SetLifecycle(connected, armed: false, busy: false);
        control.SetStatus(
            connected
                ? "Ready to arm a bounded process-only fault profile."
                : "Fault injection is unavailable until a provider boundary is connected.",
            connected
                ? "The lab will retain content-free observations only."
                : "Connect an IModelProviderClient and a current configuration delegate when registering this feature.");
    }

    internal bool IsArmed => armedClient?.IsArmed == true;
    internal bool IsConnected => providerClient is not null && configProvider is not null;
    internal ArenaFaultProfileContract? ArmedProfile { get; private set; }

    public async Task ArmAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await actionGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected)
            {
                control.SetStatus("Fault injection is unavailable until a provider boundary is connected.");
                return;
            }

            control.SetLifecycle(connected: true, armed: false, busy: true);
            ArenaFaultProfileContract profile;
            ArenaFaultInjectionOptions options;
            try
            {
                (profile, options) = BuildProfile(control.ReadInput(), timeProvider.GetUtcNow());
            }
            catch (FaultLabInputException exception)
            {
                control.SetStatus(AppErrorPresenter.Present(
                    exception,
                    AppErrorContext.FaultLab,
                    AppErrorCategory.InvalidData).DisplayText);
                return;
            }

            armedClient?.Disarm();
            armedClient = FaultInjectingModelProviderClient.Arm(providerClient!, profile, options);
            ArmedProfile = profile;
            control.SetObservations([]);
            control.SetStatus($"Armed {FaultLabel(profile.Injections[0].Kind)} at provider sequence {profile.Injections[0].AtSequence}.");
        }
        finally
        {
            control.SetLifecycle(IsConnected, IsArmed, busy: false);
            actionGate.Release();
        }
    }

    public async Task RunProbeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await actionGate.WaitAsync(cancellationToken);
        CancellationTokenSource? localCancellation = null;
        try
        {
            if (armedClient is null || !armedClient.IsArmed || configProvider is null)
            {
                control.SetStatus("Arm a fault profile before running a probe.");
                return;
            }

            ModelProviderConfig? config;
            try
            {
                config = configProvider();
            }
            catch
            {
                config = null;
            }
            if (config is null || string.IsNullOrWhiteSpace(config.Model))
            {
                control.SetStatus("The current provider configuration is unavailable; no probe was sent.");
                return;
            }

            localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (lifecycleSync)
            {
                activeProbeCancellation = localCancellation;
            }
            control.SetLifecycle(connected: true, armed: true, busy: true);
            control.SetStatus("Running one bounded provider probe through the armed decorator…");

            var kind = ArmedProfile?.Injections[0].Kind ?? ArenaFaultKind.Timeout;
            if (kind is ArenaFaultKind.MalformedStream or ArenaFaultKind.Interruption)
            {
                _ = await armedClient.CompleteChatStreamingAsync(
                    config,
                    [new ModelChatMessage("user", "Fault laboratory probe.")],
                    progress: null,
                    localCancellation.Token);
            }
            else
            {
                _ = await armedClient.CompleteChatAsync(
                    config,
                    [new ModelChatMessage("user", "Fault laboratory probe.")],
                    localCancellation.Token);
            }

            var observations = armedClient.SnapshotObservations();
            RenderObservations(observations);
            control.SetStatus(observations.IsEmpty
                ? $"Provider probe completed; no scheduled fault fired at sequence {armedClient.ScheduledInvocationCount - 1}."
                : $"Recorded {observations.Length} content-free injected-cause observation(s); recovery remains separately classified.");
        }
        catch (OperationCanceledException) when (localCancellation?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
        {
            RenderObservations(armedClient?.SnapshotObservations() ?? []);
            control.SetStatus("The fault probe was cancelled; any observed cause remains listed separately from recovery.");
        }
        catch (ObjectDisposedException)
        {
            control.SetStatus("The profile was disarmed before a new probe could start.");
        }
        catch
        {
            RenderObservations(armedClient?.SnapshotObservations() ?? []);
            control.SetStatus("The provider probe failed outside the injected-cause boundary; private error content was not retained.");
        }
        finally
        {
            lock (lifecycleSync)
            {
                if (ReferenceEquals(activeProbeCancellation, localCancellation)) activeProbeCancellation = null;
            }
            localCancellation?.Dispose();
            control.SetLifecycle(IsConnected, IsArmed, busy: false);
            actionGate.Release();
        }
    }

    public async Task DisarmAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancellationTokenSource? inFlight;
        lock (lifecycleSync)
        {
            inFlight = activeProbeCancellation;
        }
        try
        {
            inFlight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await actionGate.WaitAsync(cancellationToken);
        try
        {
            armedClient?.Disarm();
            armedClient = null;
            ArmedProfile = null;
            control.SetLifecycle(IsConnected, armed: false, busy: false);
            control.SetStatus("Fault profile disarmed. Existing content-free observations remain visible until the next arm action.");
        }
        finally
        {
            actionGate.Release();
        }
    }

    internal static (ArenaFaultProfileContract Profile, ArenaFaultInjectionOptions Options) BuildProfile(
        FaultLabInput input,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (createdAtUtc == default || createdAtUtc.Offset != TimeSpan.Zero)
            throw new FaultLabInputException("Fault profile time must be UTC.");
        var seed = input.Seed.Trim();
        if (seed.Length is < 1 or > 96 || seed.Any(char.IsControl))
            throw new FaultLabInputException("Seed must contain 1–96 printable characters.");
        var sequence = ParseInt(input.AtSequence, "Start sequence", 0, 1_000_000);
        var duration = ParseInt(input.DurationMilliseconds, "Duration", 0, 5_000);
        var intensity = ParseInt(input.Intensity, "Intensity", 1, 100);
        var occurrences = ParseInt(input.MaxOccurrences, "Max occurrences", 1, 100);
        var concurrency = ParseInt(input.MaximumConcurrency, "Maximum concurrency", 1, 16);
        var suffix = StableSuffix(input.Kind, seed, sequence, duration, intensity, occurrences, concurrency);
        var injection = new ArenaFaultInjection(
            $"fault:{suffix}",
            input.Kind == ArenaFaultKind.Disconnect ? ArenaFaultTarget.Network : ArenaFaultTarget.Provider,
            input.Kind,
            sequence,
            duration,
            intensity,
            occurrences,
            "Fail only inside the bounded local fault-laboratory provider boundary.");
        var profile = new ArenaFaultProfileContract(
            ArenaContractSchemas.FaultProfile,
            $"fault-profile:{suffix}",
            createdAtUtc,
            $"Local {FaultLabel(input.Kind)} profile",
            seed,
            [injection],
            []);
        if (!ArenaContractCodec.Validate(profile).IsValid)
            throw new FaultLabInputException("The fault profile is invalid and was not armed.");
        return (profile, new ArenaFaultInjectionOptions(
            MaximumConcurrentRequests: concurrency,
            MaximumInjectedDelayMilliseconds: 5_000,
            MaximumRetainedObservations: 256));
    }

    private void RenderObservations(ImmutableArray<ArenaFaultObservation> observations) =>
        control.SetObservations(observations
            .OrderByDescending(item => item.Sequence)
            .Take(256)
            .Select(ObservationItem));

    internal static FaultObservationItem ObservationItem(ArenaFaultObservation item)
    {
        var effect = EffectLabel(item.InjectedEffect);
        var effectText = item.ObservedOutcome == ArenaFaultObservedOutcome.CallerCancelledBeforeEffect
            ? $"Effect not observed: caller cancellation pre-empted the scheduled {FaultLabel(item.InjectedCause)} condition."
            : $"Effect: {effect}.";
        return new(
            $"Sequence {item.Sequence} · occurrence {item.Occurrence} · {FaultLabel(item.InjectedCause)}",
            effectText,
            $"Cause: {item.CauseEvidence.State} at the {OperationLabel(item.Operation)} boundary.",
            $"Recovery: {item.RecoveryEvidence.State} — no higher-level recovery measurement is claimed.");
    }

    private static int ParseInt(string value, string label, int minimum, int maximum)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
            throw new FaultLabInputException($"{label} must be from {minimum} through {maximum}.");
        return parsed;
    }

    private static string StableSuffix(params object[] values)
    {
        var text = string.Join("\n", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..8]);
    }

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

    private static string OperationLabel(ArenaProviderFaultOperation operation) => operation switch
    {
        ArenaProviderFaultOperation.ModelDiscovery => "model discovery",
        ArenaProviderFaultOperation.ChatCompletion => "chat completion",
        ArenaProviderFaultOperation.StreamingChatCompletion => "streaming chat completion",
        _ => "provider"
    };

    private static string EffectLabel(ArenaFaultInjectedEffect effect) => effect switch
    {
        ArenaFaultInjectedEffect.CallerCancelledBeforeEffect => "caller cancelled before the scheduled effect",
        ArenaFaultInjectedEffect.TimeoutElapsed => "bounded timeout elapsed",
        ArenaFaultInjectedEffect.ConnectionDropped => "connection dropped",
        ArenaFaultInjectedEffect.MalformedStreamRejected => "malformed stream rejected before assistant progress",
        ArenaFaultInjectedEffect.CapacityRejected => "provider capacity rejected the request",
        ArenaFaultInjectedEffect.EmptyCompletion => "empty completion rejected",
        ArenaFaultInjectedEffect.StreamInterrupted => "partial stream interrupted",
        ArenaFaultInjectedEffect.ContextLimitRejected => "context limit rejected the request",
        _ => "provider failure injected"
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (lifecycleSync)
        {
            try
            {
                activeProbeCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        armedClient?.Disarm();
        armedClient = null;
    }
}

internal sealed class FaultLabInputException(string message) : Exception(message);
