namespace AIArena.Wpf.Services;

/// <summary>Optional synchronous observers cannot interrupt an operation or each other.</summary>
internal static class ObserverNotification
{
    internal static Exception? TryPublish<T>(Action<T> observer, T value)
    {
        try
        {
            observer(value);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static void Publish<T>(Action<T>? observers, T value)
    {
        if (observers is null) return;
        foreach (var observer in observers.GetInvocationList())
            TryPublish((Action<T>)observer, value);
    }
}
