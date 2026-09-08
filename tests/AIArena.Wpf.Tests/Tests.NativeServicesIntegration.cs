using System.IO;
using System.Text.Json;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    // Explicit opt-in only. The native coordinating task owns the running native instance.
    private static int RunNativeServicesIntegration(string[] arguments)
    {
        return RunNativeServicesIntegrationAsync(arguments).GetAwaiter().GetResult();
    }

    private static async Task<int> RunNativeServicesIntegrationAsync(string[] arguments)
    {
        if (arguments.Length is < 2 or > 3 || !Path.IsPathFullyQualified(arguments[1])
            || arguments.Length == 3 && arguments[2] != "--cancel")
        {
            Console.Error.WriteLine("Usage: --native-services-integration <owned-output-directory> [--cancel]");
            return 2;
        }
        var outputDirectory = Path.GetFullPath(arguments[1]);
        Directory.CreateDirectory(outputDirectory);
        var client = new NativeIntegrationDiagnosticClient(new NativeControlClient());
        using var coordinator = new NativeServicesCoordinator(client, new ApplicationStatusCenter(), outputDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancelRequested = false;
        var events = new List<object>();
        void Record(string phase)
        {
            var receipt = new
            {
                phase,
                operationId = coordinator.OperationId,
                state = coordinator.Operation?.State,
                transitionOrdinal = coordinator.Operation?.Transition,
                idempotencyKey = coordinator.LastRequestIdempotencyKey,
                command = "diagnostics.bundle.create",
                args = new
                {
                    destination = coordinator.LastRequestDestination,
                    categories = new[] { "version", "health", "operations", "logs" },
                    maximumBytes = 32 * 1024 * 1024,
                    maximumMembers = 1000
                },
                cancelRequested,
                terminal = coordinator.Operation?.IsTerminal == true,
                resultPath = coordinator.ResultPath,
                resultExists = !string.IsNullOrWhiteSpace(coordinator.ResultPath) && File.Exists(coordinator.ResultPath),
                submissionUnconfirmed = coordinator.HasPendingRequest
            };
            events.Add(receipt);
            Console.WriteLine(JsonSerializer.Serialize(receipt));
            Console.Out.Flush();
        }
        var receiptPath = Path.Combine(outputDirectory, "wpf-native-receipt-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await coordinator.ConnectAsync(deadline.Token);
            if (!coordinator.CanCreateBundle)
            {
                var failure = new
                {
                    phase = "connection-failed",
                    failureStage = client.ConnectionFailureStage ?? "workflow-capability-unavailable",
                    message = AppErrorPresenter.RedactAndBound(coordinator.Message, 600, "Native connection unavailable."),
                    clientDetail = client.ConnectionFailureDetail
                };
                events.Add(failure);
                Console.Error.WriteLine(JsonSerializer.Serialize(failure));
                return 1;
            }
            await coordinator.CreateBundleAsync(coordinator.Destination, deadline.Token);
            Record("accepted");
            if (coordinator.Operation is null) return 1;

            if (arguments.Length == 3 && coordinator.CanCancel)
            {
                cancelRequested = true;
                await coordinator.CancelOperationAsync(deadline.Token);
                Record("cancel-reconciled");
            }
            if (coordinator.CanObserve) await coordinator.ObserveAsync(deadline.Token);
            await coordinator.ReconcileAsync();
            Record("final");
            if (coordinator.Operation?.IsTerminal != true) return 1;
            if (arguments.Length == 3)
                return cancelRequested && coordinator.Operation.State == "canceled" ? 0 : 3;
            return coordinator.Operation.State == "succeeded" && File.Exists(coordinator.ResultPath) ? 0 : 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Native integration did not finish with confirmed evidence; inspect the local receipt before retrying.");
            Record("unconfirmed");
            return 1;
        }
        finally
        {
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { receiptPath }));
        }
    }
    // Observation-only wrapper around the same production client. It preserves
    // a safe connection failure before the presentation coordinator handles it.
    private sealed class NativeIntegrationDiagnosticClient(INativeServicesClient inner) : INativeServicesClient
    {
        public string? ConnectionFailureStage { get; private set; }
        public string? ConnectionFailureDetail { get; private set; }

        public async Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ConnectAsync(cancellationToken);
            }
            catch (NativeControlException exception)
            {
                ConnectionFailureStage = "native-client-transport-or-contract";
                // NativeControlException messages are local fixed guidance; raw
                // native errors, request objects and tokens are never retained.
                ConnectionFailureDetail = AppErrorPresenter.RedactAndBound(exception.Message, 600, "Native client rejected the connection.");
                throw;
            }
            catch (OperationCanceledException)
            {
                ConnectionFailureStage = "connection-timeout-or-canceled";
                ConnectionFailureDetail = "Connection observation ended before the workflow was discovered.";
                throw;
            }
            catch (Exception exception)
            {
                ConnectionFailureStage = "connection-local-failure";
                ConnectionFailureDetail = exception.GetType().Name;
                throw;
            }
        }

        public Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string idempotencyKey, CancellationToken cancellationToken) =>
            inner.CreateDiagnosticBundleAsync(destination, idempotencyKey, cancellationToken);
        public Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken) =>
            inner.GetOperationAsync(operationId, cancellationToken);
        public Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken) =>
            inner.WaitOperationAsync(operationId, afterTransition, cancellationToken);
        public Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken) =>
            inner.CancelOperationAsync(operationId, idempotencyKey, cancellationToken);
    }
}
