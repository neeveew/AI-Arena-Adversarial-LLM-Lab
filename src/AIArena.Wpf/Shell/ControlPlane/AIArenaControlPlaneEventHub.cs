namespace AIArena.Wpf;

internal sealed class AIArenaControlPlaneEventHub : IAIArenaControlEventSource
{
    private readonly object gate = new();
    private readonly List<Action<AIArenaControlEvent>> subscribers = [];

    public IDisposable Subscribe(Action<AIArenaControlEvent> onEvent)
    {
        if (onEvent is null)
        {
            throw new ArgumentNullException(nameof(onEvent));
        }

        lock (gate)
        {
            subscribers.Add(onEvent);
        }

        return new Subscription(this, onEvent);
    }

    public void Publish(string type, string message, object? data = null)
    {
        Publish(new AIArenaControlEvent(type, DateTimeOffset.Now, message, data));
    }

    public void Publish(AIArenaControlEvent controlEvent)
    {
        Action<AIArenaControlEvent>[] snapshot;
        lock (gate)
        {
            snapshot = subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber(controlEvent);
            }
            catch (ObjectDisposedException)
            {
                Unsubscribe(subscriber);
            }
        }
    }

    private void Unsubscribe(Action<AIArenaControlEvent> onEvent)
    {
        lock (gate)
        {
            subscribers.Remove(onEvent);
        }
    }

    private sealed class Subscription(AIArenaControlPlaneEventHub owner, Action<AIArenaControlEvent> onEvent) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.Unsubscribe(onEvent);
        }
    }
}
