using System.Windows.Threading;

namespace AIArena.Wpf;

internal sealed class DispatcherDebouncer : IDisposable
{
    private readonly DispatcherTimer timer;
    private readonly Action action;
    private bool disposed;

    public DispatcherDebouncer(
        Dispatcher dispatcher,
        TimeSpan delay,
        Action action,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "The debounce delay must be positive.");
        }

        this.action = action;
        timer = new DispatcherTimer(priority, dispatcher)
        {
            Interval = delay
        };
        timer.Tick += Timer_Tick;
    }

    public bool IsPending => !disposed && timer.IsEnabled;

    public void Schedule()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        timer.Stop();
        timer.Start();
    }

    public void Flush()
    {
        if (disposed || !timer.IsEnabled)
        {
            return;
        }

        timer.Stop();
        action();
    }

    public void Cancel()
    {
        if (!disposed)
        {
            timer.Stop();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= Timer_Tick;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        Flush();
    }
}
