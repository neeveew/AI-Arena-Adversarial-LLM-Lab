using System.Windows.Threading;

namespace AIArena.Wpf;

internal interface IProviderModelsHeartbeatTimer : IDisposable
{
    event EventHandler? Tick;

    TimeSpan Interval { get; }

    bool IsEnabled { get; }

    void Start();

    void Stop();
}

internal sealed class DispatcherProviderModelsHeartbeatTimer : IProviderModelsHeartbeatTimer
{
    private readonly DispatcherTimer timer;

    public DispatcherProviderModelsHeartbeatTimer(
        Dispatcher dispatcher,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval
        };
    }

    public event EventHandler? Tick
    {
        add => timer.Tick += value;
        remove => timer.Tick -= value;
    }

    public TimeSpan Interval => timer.Interval;

    public bool IsEnabled => timer.IsEnabled;

    public void Start() => timer.Start();

    public void Stop() => timer.Stop();

    public void Dispose() => timer.Stop();
}

/// <summary>
/// Owns the effective-visibility lifetime of the provider Models heartbeat.
/// Provider work remains supplied by the shell so this type cannot outlive or
/// bypass the application's tracked-operation boundary.
/// </summary>
internal sealed class ProviderModelsHeartbeatController : IDisposable
{
    private readonly IProviderModelsHeartbeatTimer timer;
    private readonly Func<CancellationToken, Task> heartbeat;
    private CancellationTokenSource? visibilityCancellation;
    private bool effectivelyVisible;
    private bool tickRunning;
    private bool disposed;
    private long visibilityGeneration;

    public ProviderModelsHeartbeatController(
        Dispatcher dispatcher,
        TimeSpan interval,
        Func<CancellationToken, Task> heartbeat)
        : this(
            new DispatcherProviderModelsHeartbeatTimer(dispatcher, interval),
            heartbeat)
    {
    }

    internal ProviderModelsHeartbeatController(
        IProviderModelsHeartbeatTimer timer,
        Func<CancellationToken, Task> heartbeat)
    {
        this.timer = timer ?? throw new ArgumentNullException(nameof(timer));
        this.heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        timer.Tick += Timer_Tick;
    }

    internal TimeSpan Interval => timer.Interval;

    internal bool IsTimerRunning => timer.IsEnabled;

    internal bool HasInFlightHeartbeat => tickRunning;

    internal long VisibilityGeneration => visibilityGeneration;

    internal bool IsDisposed => disposed;

    public void SetEffectivelyVisible(bool visible)
    {
        if (disposed || visible == effectivelyVisible)
        {
            return;
        }

        effectivelyVisible = visible;
        if (!visible)
        {
            StopCurrentGeneration();
            return;
        }

        visibilityCancellation = new CancellationTokenSource();
        visibilityGeneration++;
        timer.Start();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        effectivelyVisible = false;
        StopCurrentGeneration();
        timer.Tick -= Timer_Tick;
        timer.Dispose();
    }

    private void StopCurrentGeneration()
    {
        timer.Stop();
        visibilityCancellation?.Cancel();
        visibilityCancellation?.Dispose();
        visibilityCancellation = null;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        var generationCancellation = visibilityCancellation;
        if (disposed
            || !effectivelyVisible
            || tickRunning
            || generationCancellation is null)
        {
            return;
        }

        var generationToken = generationCancellation.Token;
        tickRunning = true;
        try
        {
            await heartbeat(generationToken);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            // Hiding or closing the surface owns this cancellation.
        }
        finally
        {
            tickRunning = false;
        }
    }
}
