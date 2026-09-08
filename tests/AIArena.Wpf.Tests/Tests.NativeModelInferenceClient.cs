using AIArena.Native.Protocol;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void NativeModelClientProjectsLifecycleAndExactConfirmation()
    {
        const string plan = "fixture-plan-digest";
        const string sample = "fixture-capacity-sample";
        var calls = new List<Request>();
        var client = new NativeControlClient((request, _, _) =>
        {
            calls.Add(request.Clone());
            var response = ModelInferenceResponse(request);
            switch (request.ActionCase)
            {
                case Request.ActionOneofCase.Models:
                    response.Models = new Models { DefaultDeploymentId = "preview-model" };
                    response.Models.Deployments.Add(new ModelDeployment {
                        DeploymentId = "preview-model", State = "unloaded", Health = "unknown", ConfiguredTarget = "cpu",
                        LatestAdmission = new ModelAdmission { Status = "waiting", OperationId = NativeClientOperationId,
                            PlanDigest = plan, CapacitySampleId = sample } });
                    break;
                case Request.ActionOneofCase.ModelPreflight:
                    response.ModelPreflight = new ModelPreflight {
                        DeploymentId = "preview-model", Target = "cpu", Quality = "estimate", PlanDigest = plan,
                        RequiredRamBytes = 1570561408, RamCapacityKnown = true, AdmissibleRamBytes = 4294967296,
                        ConfirmationRequired = true };
                    break;
                case Request.ActionOneofCase.ModelLoad:
                    response.Operation = NativeClientOperation("waiting", 2, false);
                    response.Operation.ConfirmationRequired = true;
                    response.Operation.PlanDigest = plan;
                    response.Operation.CapacitySampleId = sample;
                    break;
                case Request.ActionOneofCase.ModelLoadConfirm:
                    response.Operation = NativeClientOperation("running", 3, false);
                    break;
                case Request.ActionOneofCase.ModelEject:
                    response.Operation = NativeClientOperation("succeeded", 4, true);
                    break;
                default: throw new InvalidOperationException();
            }
            return Task.FromResult(response);
        });
        var catalog = client.GetModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(catalog.DefaultDeploymentId == "preview-model" && catalog.Items.Count == 1 && !catalog.Items[0].Serviceable
            && catalog.Items[0].Target == "cpu" && catalog.Items[0].Admission?.CapacitySampleId == sample,
            "Inventory must preserve configured identity, observed residency and exact admission evidence.");
        var preflight = client.PreflightModelAsync("preview-model", CancellationToken.None).GetAwaiter().GetResult();
        Require(preflight.ConfirmationRequired && preflight.PlanDigest == plan && preflight.Summary.Contains("RAM"),
            "Preflight must retain the native capacity estimate and decision requirement.");
        var loaded = client.LoadModelAsync("preview-model", "load-key", CancellationToken.None).GetAwaiter().GetResult();
        Require(loaded.ConfirmationRequired && loaded.PlanDigest == plan && loaded.CapacitySampleId == sample
            && calls.All(call => call.ActionCase != Request.ActionOneofCase.ModelLoadConfirm),
            "Loading must retain confirmation evidence without submitting a capacity decision.");
        client.ConfirmModelLoadAsync(loaded.OperationId, loaded.PlanDigest, loaded.CapacitySampleId, "confirm-key", CancellationToken.None).GetAwaiter().GetResult();
        var confirmation = calls[^1];
        Require(confirmation.ModelLoadConfirm.OperationId == loaded.OperationId && confirmation.ModelLoadConfirm.PlanDigest == plan
            && confirmation.ModelLoadConfirm.CapacitySampleId == sample && confirmation.ModelLoadConfirm.Decision == "load_anyway"
            && confirmation.IdempotencyKey == "confirm-key", "Only explicit confirmation may send the exact native decision identity.");
        Require(client.UnloadModelAsync("preview-model", "eject-key", CancellationToken.None).GetAwaiter().GetResult().IsTerminal,
            "Ejection must project the native durable result.");
        var count = calls.Count;
        NativeClientExpect<NativeControlException>(() => client.ConfirmModelLoadAsync(loaded.OperationId, "", sample, "key", CancellationToken.None).GetAwaiter().GetResult());
        Require(calls.Count == count, "Missing decision evidence must fail before transmission.");
    }

    static void NativeInferenceClientEnforcesBoundsAndDedicatedSnapshots()
    {
        var calls = new List<Request>();
        var snapshot = new Generation { OperationId = NativeClientOperationId, DeploymentId = "preview-model",
            State = "running", Sequence = 9007199254740993, OutputText = "Hello π", Lifetime = "process" };
        var client = new NativeControlClient((request, _, _) =>
        {
            calls.Add(request.Clone());
            var response = ModelInferenceResponse(request);
            response.Generation = snapshot.Clone();
            return Task.FromResult(response);
        });
        var first = client.StartInferenceAsync("preview-model", "Public fixture", 64, 0.7, "start-key", CancellationToken.None).GetAwaiter().GetResult();
        var second = client.StartInferenceAsync("preview-model", "Public fixture", 64, 0.7, "start-key", CancellationToken.None).GetAwaiter().GetResult();
        Require(first.Sequence == 9007199254740993L && first.Output == "Hello π" && !first.IsTerminal
            && calls[0].IdempotencyKey == calls[1].IdempotencyKey && calls[0].GenerationStart.Equals(calls[1].GenerationStart)
            && calls[0].RequestId != calls[1].RequestId, "Explicit same-process retry must preserve args and key with independent request correlation.");
        snapshot.State = "cancel_requested"; snapshot.Sequence++;
        Require(!client.CancelInferenceAsync(first.OperationId, "cancel-key", CancellationToken.None).GetAwaiter().GetResult().IsTerminal
            && calls[^1].ActionCase == Request.ActionOneofCase.GenerationCancel,
            "Dedicated cancellation acknowledgement must not claim terminal cancellation.");
        snapshot.State = "succeeded"; snapshot.Terminal = true; snapshot.Sequence++; snapshot.OutputText = "Hello π, completed";
        var completed = client.ReadInferenceAsync(first.OperationId, CancellationToken.None).GetAwaiter().GetResult();
        Require(completed.State == "succeeded" && completed.Output == snapshot.OutputText && calls[^1].ActionCase == Request.ActionOneofCase.GenerationRead,
            "Dedicated snapshot reads must preserve completion-winning races and full replacement output.");
        var count = calls.Count;
        foreach (var prompt in new[] { "", new string('é', 8193), "\ud800" })
            NativeClientExpect<NativeControlException>(() => client.StartInferenceAsync("preview-model", prompt, 64, 0.7, "key", CancellationToken.None).GetAwaiter().GetResult());
        NativeClientExpect<NativeControlException>(() => client.StartInferenceAsync("preview-model", "fixture", 0, 0.7, "key", CancellationToken.None).GetAwaiter().GetResult());
        NativeClientExpect<NativeControlException>(() => client.StartInferenceAsync("preview-model", "fixture", 64, double.NaN, "key", CancellationToken.None).GetAwaiter().GetResult());
        Require(calls.Count == count, "Invalid Unicode and UTF-8 prompt/token/sampling bounds must reject before transmission.");
        foreach (var invalid in new Action<Generation>[] {
            value => value.OperationId = Guid.CreateVersion7().ToString(),
            value => value.Lifetime = "durable",
            value => value.Sequence = ulong.MaxValue,
            value => value.Terminal = false,
            value => value.OutputText = new string('x', 262145)
        })
        {
            var saved = snapshot.Clone();
            invalid(snapshot);
            NativeClientExpect<NativeControlException>(() => client.ReadInferenceAsync(first.OperationId, CancellationToken.None).GetAwaiter().GetResult());
            snapshot = saved;
        }
    }

    static void NativeClientReconcilesMissingStateCursor()
    {
        var calls = new List<Request>();
        var client = new NativeControlClient((request, _, _) =>
        {
            calls.Add(request.Clone());
            var response = NativeClientResponse(request);
            if (request.ActionCase == Request.ActionOneofCase.OperationState)
            {
                response.Operation = NativeClientOperation("running", 0, false);
                response.Operation.ClearTransitionOrdinal();
            }
            else
            {
                Require(request.ActionCase == Request.ActionOneofCase.OperationWait && request.OperationWait.TimeoutMilliseconds == 0
                    && request.OperationWait.AfterTransition == (ulong)Math.Max(0, calls.Count - 2),
                    "Missing state cursors must reconcile through immediate native observations.");
                response.Operation = NativeClientOperation("running", (ulong)Math.Min(2, calls.Count - 1), false);
                response.Operation.TimedOut = calls.Count == 4;
            }
            return Task.FromResult(response);
        });
        var observed = client.GetOperationAsync(NativeClientOperationId, CancellationToken.None).GetAwaiter().GetResult();
        Require(observed.State == "running" && observed.Transition == 2 && calls.Count == 4,
            "Reconciliation must drain actual native ordinals, not assign zero or invent a newer cursor.");
    }

    private static Response ModelInferenceResponse(Request request) => new()
    {
        RequestId = request.RequestId, CorrelationId = request.CorrelationId, Ok = true,
        Command = request.ActionCase switch
        {
            Request.ActionOneofCase.Models => "inference.models.state",
            Request.ActionOneofCase.ModelPreflight => "inference.models.preflight",
            Request.ActionOneofCase.ModelLoad => "inference.models.load",
            Request.ActionOneofCase.ModelEject => "inference.models.eject",
            Request.ActionOneofCase.ModelLoadConfirm => "inference.models.load.confirm",
            Request.ActionOneofCase.GenerationStart => "inference.generate.start",
            Request.ActionOneofCase.GenerationRead => "inference.generate.read",
            Request.ActionOneofCase.GenerationCancel => "inference.generate.cancel",
            _ => throw new InvalidOperationException("Unexpected model/inference wire command.")
        }
    };
}
