using System.Windows.Threading;

namespace AIArena.Wpf;

internal static class AIArenaControlDispatcher
{
    internal static async Task<T> InvokeAsync<T>(
        Dispatcher dispatcher,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (dispatcher.CheckAccess())
        {
            return await action();
        }

        return await dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken).Task.Unwrap();
    }
}
