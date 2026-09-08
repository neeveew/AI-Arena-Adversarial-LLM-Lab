using System.Text;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void NativeModelCoordinatorConfirmsLoadAndObservesResidency()
    {
        var client = new ModelCoordinatorFixture();
        using var coordinator = new NativeModelInferenceCoordinator(client, new ApplicationStatusCenter());
        coordinator.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
        Require(coordinator.CanLoadModel && !coordinator.CanStartInference, "Unloaded native inventory should enable only load");
        client.Lifecycle = client.Lifecycle with { State = "waiting", ConfirmationRequired = true, PlanDigest = new string('a', 64), CapacitySampleId = Guid.CreateVersion7().ToString() };
        coordinator.LoadSelectedModelAsync().GetAwaiter().GetResult();
        Require(client.LoadCalls == 1 && client.ConfirmCalls.Count == 0 && coordinator.CanConfirmLoad && !coordinator.CanObserve,
            "Capacity waiting must require a separate explicit action without automatic confirmation or endless polling");
        Require(coordinator.SelectedModel?.Resident == false, "Load acceptance must not invent residency");
        var acknowledgement = client.Lifecycle;
        client.Lifecycle = client.Lifecycle with { Transition = 2, ConfirmationRequired = false, PlanDigest = "", CapacitySampleId = "" };
        coordinator.RefreshAsync().GetAwaiter().GetResult();
        Require(coordinator.CanConfirmLoad && coordinator.ModelOperation?.PlanDigest == acknowledgement.PlanDigest,
            "Waiting generic reads that omit metadata must preserve actionable acknowledgement confirmation evidence");
        client.Lifecycle = acknowledgement;
        client.ConfirmFailure = true;
        coordinator.ConfirmLoadAsync().GetAwaiter().GetResult();
        var uncertain = coordinator.PendingRequest!;
        client.ConfirmFailure = false;
        client.Lifecycle = client.Lifecycle with { State = "running", Transition = 3, ConfirmationRequired = false };
        coordinator.RetryPendingRequestAsync().GetAwaiter().GetResult();
        Require(client.ConfirmCalls.Count == 2 && client.ConfirmCalls.All(call => call.Key == uncertain.Key
            && call.OperationId == uncertain.OperationId && call.PlanDigest == uncertain.PlanDigest && call.CapacitySampleId == uncertain.CapacitySampleId),
            "Explicit capacity retry must retain original operation, plan, sample, and mutation key");
        Require(coordinator.SelectedModel?.Resident == false, "Running load must wait for observed residency");
        client.Resident = true;
        client.Lifecycle = client.Lifecycle with { State = "succeeded", Transition = 4 };
        coordinator.RefreshAsync().GetAwaiter().GetResult();
        Require(coordinator.SelectedModel?.Serviceable == true && coordinator.CanUnloadModel && !coordinator.HasPendingRequest,
            "Only refreshed native evidence should mark the model serviceable");

        client.Lifecycle = new(Guid.CreateVersion7().ToString(), "succeeded", 1, "Unload completed");
        client.FailInventory = true;
        coordinator.UnloadSelectedModelAsync().GetAwaiter().GetResult();
        Require(coordinator.ModelOperation?.IsTerminal == true && !coordinator.HasPendingRequest && !coordinator.CanUnloadModel,
            "Post-acceptance catalog failure must retain the known terminal receipt and block stale model actions");
    }

    static void NativeInferenceCoordinatorRetainsIdentityAndReplacesOutput()
    {
        var client = new ModelCoordinatorFixture { Resident = true };
        var status = new ApplicationStatusCenter();
        using var coordinator = new NativeModelInferenceCoordinator(client, status);
        coordinator.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
        coordinator.Prompt = "private fixture prompt";
        coordinator.MaximumTokens = 123;
        client.StartFailure = true;
        coordinator.StartInferenceAsync().GetAwaiter().GetResult();
        var pending = coordinator.PendingRequest!;
        coordinator.Prompt = "must not replace original";
        coordinator.MaximumTokens = 999;
        coordinator.StartInferenceAsync().GetAwaiter().GetResult();
        Require(client.Starts.Count == 1 && coordinator.Prompt == pending.Prompt && coordinator.MaximumTokens == pending.MaximumTokens,
            "Uncertain inference must lock model/prompt/options and block a second start");
        client.StartFailure = false;
        coordinator.RetryPendingRequestAsync().GetAwaiter().GetResult();
        coordinator.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
        Require(client.Starts.Count == 1 && coordinator.PendingRequest == pending && !coordinator.CanRetryPendingRequest,
            "Process-local uncertainty must retain exact inputs and block retry even after reconnect without a proven native process identity");
        using var accepted = new NativeModelInferenceCoordinator(client, status);
        accepted.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
        accepted.Prompt = "separate accepted fixture";
        accepted.StartInferenceAsync().GetAwaiter().GetResult();
        client.Generation = client.Generation with { Sequence = 2, Output = "Hello" };
        accepted.RefreshAsync().GetAwaiter().GetResult();
        client.Generation = client.Generation with { Sequence = 3, Output = "Hello world", State = "succeeded", IsTerminal = true };
        accepted.RefreshAsync().GetAwaiter().GetResult();
        Require(accepted.Output == "Hello world", "Accumulated output snapshots must replace rather than duplicate earlier text");
        client.Generation = client.Generation with { Sequence = 2, Output = "Hello", State = "running", IsTerminal = false };
        accepted.RefreshAsync().GetAwaiter().GetResult();
        Require(accepted.Output == "Hello world" && accepted.Generation?.State == "succeeded",
            "Stale output and nonterminal reads must not regress completed inference");
        Require(!status.AppStatus.Contains(pending.Prompt, StringComparison.Ordinal) && !status.AppStatus.Contains(accepted.Output, StringComparison.Ordinal),
            "Prompt and generated content must not enter application status summaries");
    }

    static void NativeInferenceCoordinatorSeparatesObservationAndCancellation()
    {
        var client = new ModelCoordinatorFixture { Resident = true };
        using var coordinator = new NativeModelInferenceCoordinator(client, new ApplicationStatusCenter());
        coordinator.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
        coordinator.Prompt = "fixture";
        coordinator.StartInferenceAsync().GetAwaiter().GetResult();
        client.BlockRead = true;
        var observing = coordinator.ObserveAsync();
        client.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        coordinator.StopObserving();
        observing.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Require(client.GenerationCancelCalls == 0 && client.OperationCancelCalls == 0 && coordinator.Generation?.IsTerminal == false,
            "Stopping local inference observation must never invoke either native cancellation API");
        client.BlockRead = false;
        client.CancelResult = client.Generation with { Sequence = 4, State = "succeeded", IsTerminal = true, Output = "Completed before cancellation" };
        coordinator.CancelAsync().GetAwaiter().GetResult();
        Require(client.GenerationCancelCalls == 1 && client.OperationCancelCalls == 0
            && coordinator.Generation?.State == "succeeded" && coordinator.Output == client.CancelResult.Output,
            "Dedicated generation cancellation must reconcile and preserve completion winning the race");
        Require(client.CancelKey != coordinator.LastRequest?.Key, "Cancel requires its own stable mutation identity");
        client.Resident = false;
        client.Lifecycle = new(Guid.CreateVersion7().ToString(), "succeeded", 1, "Unloaded");
        coordinator.UnloadSelectedModelAsync().GetAwaiter().GetResult();
        Require(coordinator.Output == client.CancelResult.Output, "Unloading a model must not erase its visible inference output");
    }

    static void NativeInferenceCoordinatorPreservesTerminalAndDisposesDuringCancel()
    {
        var client = new ModelCoordinatorFixture { Resident = true };
        using (var coordinator = new NativeModelInferenceCoordinator(client, new ApplicationStatusCenter()))
        {
            coordinator.SetConnectionAsync(client.Connection).GetAwaiter().GetResult();
            coordinator.Prompt = "fixture";
            coordinator.StartInferenceAsync().GetAwaiter().GetResult();
            client.CancelResult = client.Generation with { Sequence = 2, State = "succeeded", IsTerminal = true, Output = "Confirmed" };
            client.FailRead = true;
            coordinator.CancelAsync().GetAwaiter().GetResult();
            Require(coordinator.Generation?.State == "succeeded" && coordinator.Message.Contains("remains succeeded", StringComparison.Ordinal),
                "A failed follow-up read must not obscure an already confirmed cancellation-race completion");
        }
        var closingClient = new ModelCoordinatorFixture
        {
            Resident = true,
            CancelGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var closing = new NativeModelInferenceCoordinator(closingClient, new ApplicationStatusCenter());
        closing.SetConnectionAsync(closingClient.Connection).GetAwaiter().GetResult();
        closing.Prompt = "fixture";
        closing.StartInferenceAsync().GetAwaiter().GetResult();
        var cancellation = closing.CancelAsync();
        closing.Dispose();
        closingClient.CancelGate.SetResult(closingClient.Generation);
        cancellation.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }

    private sealed class ModelCoordinatorFixture : INativeServicesClient, INativeModelClient, INativeInferenceClient
    {
        public bool Resident;
        public bool ConfirmFailure;
        public bool StartFailure;
        public bool FailInventory;
        public bool BlockRead;
        public bool FailRead;
        public TaskCompletionSource<NativeGenerationSnapshot>? CancelGate;
        public int LoadCalls;
        public int GenerationCancelCalls;
        public int OperationCancelCalls;
        public string CancelKey = "";
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativeOperationSnapshot Lifecycle = new(Guid.CreateVersion7().ToString(), "running", 1, "Loading");
        public NativeGenerationSnapshot Generation = new(Guid.CreateVersion7().ToString(), "fixture", "running", false, 1, "", "");
        public NativeGenerationSnapshot? CancelResult;
        public readonly List<NativeModelMutation> ConfirmCalls = [];
        public readonly List<NativeModelMutation> Starts = [];
        public NativeConnectionSnapshot Connection = new("Healthy", "1 model", "Available",
            ["inference.models.state", "inference.models.preflight", "inference.models.load", "inference.models.eject", "inference.models.load.confirm",
                "operation.state", "operation.wait", "operation.cancel", "inference.generate.start", "inference.generate.read", "inference.generate.cancel"], false);
        public Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(Connection);
        public Task<NativeModelCatalog> GetModelsAsync(CancellationToken cancellationToken) => FailInventory
            ? Task.FromException<NativeModelCatalog>(new NativeControlException("fixture inventory unavailable"))
            : Task.FromResult(new NativeModelCatalog([new("fixture", Resident ? "ready" : "unloaded", Resident, Resident,
                "healthy", "cpu", "", "", null, false, false)], "fixture"));
        public Task<NativeModelPreflight> PreflightModelAsync(string deploymentId, CancellationToken cancellationToken) =>
            Task.FromResult(new NativeModelPreflight(deploymentId, "Fixture capacity estimate; explicit decision required.", true, new string('a', 64)));
        public Task<NativeOperationSnapshot> LoadModelAsync(string deploymentId, string key, CancellationToken cancellationToken)
        { LoadCalls++; return Task.FromResult(Lifecycle); }
        public Task<NativeOperationSnapshot> UnloadModelAsync(string deploymentId, string key, CancellationToken cancellationToken) => Task.FromResult(Lifecycle);
        public Task<NativeOperationSnapshot> ConfirmModelLoadAsync(string operationId, string planDigest, string capacitySampleId, string key, CancellationToken cancellationToken)
        {
            ConfirmCalls.Add(new("confirm", "fixture", key, OperationId: operationId, PlanDigest: planDigest, CapacitySampleId: capacitySampleId));
            return ConfirmFailure ? Task.FromException<NativeOperationSnapshot>(new NativeControlException("lost acknowledgement")) : Task.FromResult(Lifecycle);
        }
        public Task<NativeGenerationSnapshot> StartInferenceAsync(string deploymentId, string prompt, int maximumTokens, double temperature, string key, CancellationToken cancellationToken)
        {
            Starts.Add(new("generate", deploymentId, key, prompt, maximumTokens, temperature));
            return StartFailure ? Task.FromException<NativeGenerationSnapshot>(new NativeControlException("lost acknowledgement")) : Task.FromResult(Generation);
        }
        public async Task<NativeGenerationSnapshot> ReadInferenceAsync(string operationId, CancellationToken cancellationToken)
        {
            if (FailRead) throw new NativeControlException("fixture read unavailable");
            ReadEntered.TrySetResult();
            if (BlockRead) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Generation;
        }
        public Task<NativeGenerationSnapshot> CancelInferenceAsync(string operationId, string key, CancellationToken cancellationToken)
        { GenerationCancelCalls++; CancelKey = key; return CancelGate?.Task ?? Task.FromResult(CancelResult ?? Generation); }
        public Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken) => Task.FromResult(Lifecycle);
        public Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken) => Task.FromResult(Lifecycle);
        public Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string key, CancellationToken cancellationToken)
        { OperationCancelCalls++; return Task.FromResult(Lifecycle); }
    }
}
