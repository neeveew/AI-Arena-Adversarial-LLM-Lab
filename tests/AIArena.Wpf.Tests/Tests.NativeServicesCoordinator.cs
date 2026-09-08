using System.IO;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static void NativeServicesCoordinatorRetainsUncertainIdentity()
    {
        var owned = Path.Combine(Path.GetTempPath(), "ai-arena-native-coordinator-" + Guid.NewGuid().ToString("N"));
        var client = new CoordinatorNativeClient();
        using var coordinator = new NativeServicesCoordinator(client, new ApplicationStatusCenter(), owned);
        try
        {
            Require(!Directory.Exists(owned), "Coordinator construction must not create directories");
            coordinator.ConnectAsync().GetAwaiter().GetResult();
            client.Submit = (_, _, _) => Task.FromException<NativeOperationSnapshot>(new NativeControlException("uncertain"));
            coordinator.CreateBundleAsync(coordinator.Destination).GetAwaiter().GetResult();
            Require(Directory.Exists(owned) && coordinator.HasPendingRequest && !coordinator.CanCreateBundle,
                "Explicit creation must create the owned output directory and preserve uncertain request identity");
            var originalKey = coordinator.PendingIdempotencyKey;
            var originalPath = coordinator.PendingDestination;
            coordinator.CreateBundleAsync(Path.Combine(owned, "duplicate.zip")).GetAwaiter().GetResult();
            Require(client.Requests.Count == 1, "A pending request must block creating duplicate work");

            client.Submit = (_, _, _) => Task.FromException<NativeOperationSnapshot>(new NativeControlException("authentication unavailable", false));
            coordinator.RetryPendingRequestAsync().GetAwaiter().GetResult();
            Require(coordinator.HasPendingRequest && coordinator.PendingIdempotencyKey == originalKey,
                "A later rejected retry does not disprove acceptance of the original uncertain request");
            client.Submit = (_, _, _) => Task.FromResult(client.Current);
            coordinator.RetryPendingRequestAsync().GetAwaiter().GetResult();
            Require(!coordinator.HasPendingRequest && coordinator.OperationId == client.Current.OperationId,
                "Recovered acceptance must retain the authoritative operation identifier");
            Require(client.Requests.Count == 3 && client.Requests.All(item => item.Key == originalKey && item.Path == originalPath),
                "Every explicit retry must retain the original destination and mutation key");
            Require(coordinator.LastRequestIdempotencyKey == originalKey && coordinator.LastRequestDestination == originalPath,
                "Integration receipts must retain the original nonsecret command identity after acceptance");
        }
        finally
        {
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
        }
    }

    private static void NativeServicesCoordinatorSeparatesObservationAndCancellation()
    {
        var owned = Path.Combine(Path.GetTempPath(), "ai-arena-native-observation-" + Guid.NewGuid().ToString("N"));
        var client = new CoordinatorNativeClient();
        using var coordinator = new NativeServicesCoordinator(client, new ApplicationStatusCenter(), owned);
        try
        {
            coordinator.ConnectAsync().GetAwaiter().GetResult();
            coordinator.CreateBundleAsync(coordinator.Destination).GetAwaiter().GetResult();
            var observation = coordinator.ObserveAsync();
            client.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            coordinator.StopObserving();
            observation.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(client.CancelCount == 0 && !coordinator.IsObserving && coordinator.Operation?.IsTerminal == false,
                "Stopping observation must close only the wait and leave native work active");

            // A real result can beat cancellation. A stale subsequent query must not undo it.
            File.WriteAllText(coordinator.LastRequestDestination, "fixture artifact");
            client.Cancel = (_, _, _) => Task.FromResult(client.Current with { State = "succeeded", Transition = 3 });
            client.Current = client.Current with { State = "running", Transition = 2 };
            coordinator.CancelOperationAsync().GetAwaiter().GetResult();
            Require(client.CancelCount == 1 && coordinator.Operation?.State == "succeeded" && coordinator.Operation.Transition == 3,
                "Observed completion must win a cancellation race and stale state must not regress it");
            Require(coordinator.ResultPath == coordinator.LastRequestDestination,
                "Succeeded evidence plus local existence may reveal the operator-selected artifact path");
            Require(client.LastCancelKey != coordinator.LastRequestIdempotencyKey,
                "Cancellation must use a separate command identity");
        }
        finally
        {
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
        }
    }

    private sealed class CoordinatorNativeClient : INativeServicesClient
    {
        public NativeOperationSnapshot Current = new(Guid.CreateVersion7().ToString(), "accepted", 1, "Accepted");
        public Func<string, string, CancellationToken, Task<NativeOperationSnapshot>>? Submit;
        public Func<string, string, CancellationToken, Task<NativeOperationSnapshot>>? Cancel;
        public readonly List<(string Path, string Key)> Requests = [];
        public readonly TaskCompletionSource WaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelCount;
        public string LastCancelKey = "";
        public Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NativeConnectionSnapshot("Healthy", "No models", "One operation", ["diagnostics.bundle.create"], true));
        public Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string idempotencyKey, CancellationToken cancellationToken)
        {
            Requests.Add((destination, idempotencyKey));
            return Submit?.Invoke(destination, idempotencyKey, cancellationToken) ?? Task.FromResult(Current);
        }
        public Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken) => Task.FromResult(Current);
        public async Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken)
        {
            WaitEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Current;
        }
        public Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken)
        {
            CancelCount++;
            LastCancelKey = idempotencyKey;
            return Cancel?.Invoke(operationId, idempotencyKey, cancellationToken) ?? Task.FromResult(Current);
        }
    }
}
