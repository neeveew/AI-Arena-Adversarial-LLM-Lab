using System.Text;
using AIArena.Native.Protocol;

namespace AIArena.Wpf.Services;

internal sealed partial class NativeControlClient
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<NativeModelCatalog> GetModelsAsync(CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { Models = new ModelsRequest() }, "inference.models.state", cancellationToken);
        if (response.ResultCase != Response.ResultOneofCase.Models)
            throw new NativeControlException("The native app did not return model inventory.");
        var catalog = response.Models;
        var items = new List<NativeModelOption>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in catalog.Deployments.Take(256))
        {
            ValidateDeploymentId(model.DeploymentId);
            if (!ids.Add(model.DeploymentId) || model.Serviceable && !model.Resident
                || model.HasProgressFraction && (!double.IsFinite(model.ProgressFraction) || model.ProgressFraction is < 0 or > 1)
                || model.OperationId.Length != 0 && !IsUuidV7(model.OperationId))
                throw new NativeControlException("The native model inventory contains inconsistent state.");
            NativeModelAdmission? admission = null;
            if (model.LatestAdmission is { } decision)
            {
                admission = new NativeModelAdmission(BoundedField(decision.Status), BoundedField(decision.OperationId),
                    BoundedField(decision.PlanDigest), BoundedField(decision.CapacitySampleId));
            }
            items.Add(new NativeModelOption(model.DeploymentId, BoundedField(model.State), model.Resident, model.Serviceable,
                BoundedField(model.Health), BoundedField(model.HasResidentTarget ? model.ResidentTarget :
                    model.HasConfiguredTarget ? model.ConfiguredTarget : "unknown"),
                model.OperationId, BoundedField(model.Phase), model.HasProgressFraction ? model.ProgressFraction : null,
                model.NeedsAttention, model.LifecycleHistorical, admission));
        }
        if (catalog.DefaultDeploymentId.Length != 0) ValidateDeploymentId(catalog.DefaultDeploymentId);
        return new NativeModelCatalog(items, catalog.DefaultDeploymentId, catalog.Deployments.Count > 256);
    }

    public async Task<NativeModelPreflight> PreflightModelAsync(string deploymentId, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { ModelPreflight = new ModelPreflightRequest { DeploymentId = ValidateDeploymentId(deploymentId) } },
            "inference.models.preflight", cancellationToken);
        if (response.ResultCase != Response.ResultOneofCase.ModelPreflight || response.ModelPreflight.DeploymentId != deploymentId)
            throw new NativeControlException("The native model load plan did not match the requested deployment.");
        var plan = response.ModelPreflight;
        var summary = $"Native {BoundedField(plan.Target)} capacity estimate ({BoundedField(plan.Quality)}): "
            + $"RAM required {MiB(plan.RequiredRamBytes)}, available {(plan.RamCapacityKnown ? MiB(plan.AdmissibleRamBytes) : "unknown")}; "
            + $"VRAM required {MiB(plan.RequiredVramBytes)}, available {(plan.VramCapacityKnown ? MiB(plan.AdmissibleVramBytes) : "unknown")}. "
            + (plan.ConfirmationRequired ? "An explicit capacity decision may be required when loading." : $"Admission: {BoundedField(plan.AdmissionStatus)}.");
        return new NativeModelPreflight(deploymentId, summary, plan.ConfirmationRequired, BoundedField(plan.PlanDigest));
    }

    public async Task<NativeOperationSnapshot> LoadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { ModelLoad = new ModelLoadRequest { DeploymentId = ValidateDeploymentId(deploymentId) },
            IdempotencyKey = ValidateKey(idempotencyKey) }, "inference.models.load", cancellationToken);
        return ProjectOperation(response);
    }

    public async Task<NativeOperationSnapshot> UnloadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { ModelEject = new ModelEjectRequest { DeploymentId = ValidateDeploymentId(deploymentId) },
            IdempotencyKey = ValidateKey(idempotencyKey) }, "inference.models.eject", cancellationToken);
        return ProjectOperation(response);
    }

    public async Task<NativeOperationSnapshot> ConfirmModelLoadAsync(string operationId, string planDigest, string capacitySampleId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ValidateConfirmationEvidence(true, operationId, planDigest, capacitySampleId);
        var response = await CallAsync(new Request { ModelLoadConfirm = new ModelLoadConfirmRequest {
            OperationId = ValidateOperationId(operationId), PlanDigest = planDigest, CapacitySampleId = capacitySampleId, Decision = "load_anyway" },
            IdempotencyKey = ValidateKey(idempotencyKey) }, "inference.models.load.confirm", cancellationToken);
        return ProjectOperation(response, operationId);
    }

    public async Task<NativeGenerationSnapshot> StartInferenceAsync(string deploymentId, string prompt, int maximumTokens, double temperature, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt) || Utf8Length(prompt) > 16 * 1024 || maximumTokens is < 1 or > 4096
            || !double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new NativeControlException("Use a prompt up to 16 KiB, 1–4096 maximum tokens, and temperature from 0 to 2.", false);
        var response = await CallAsync(new Request { GenerationStart = new GenerationStartRequest {
            DeploymentId = ValidateDeploymentId(deploymentId), Prompt = prompt, MaximumTokens = (uint)maximumTokens, Temperature = temperature },
            IdempotencyKey = ValidateKey(idempotencyKey) }, "inference.generate.start", cancellationToken);
        return ProjectGeneration(response, expectedDeployment: deploymentId);
    }

    public async Task<NativeGenerationSnapshot> ReadInferenceAsync(string operationId, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { GenerationRead = new GenerationReadRequest { OperationId = ValidateOperationId(operationId) } },
            "inference.generate.read", cancellationToken);
        return ProjectGeneration(response, operationId);
    }

    public async Task<NativeGenerationSnapshot> CancelInferenceAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { GenerationCancel = new GenerationCancelRequest { OperationId = ValidateOperationId(operationId) },
            IdempotencyKey = ValidateKey(idempotencyKey) }, "inference.generate.cancel", cancellationToken);
        // Cancellation acceptance is not inferred to be a completed cancellation.
        return ProjectGeneration(response, operationId);
    }

    private static NativeGenerationSnapshot ProjectGeneration(Response response, string? expectedId = null, string? expectedDeployment = null)
    {
        if (response.ResultCase != Response.ResultOneofCase.Generation)
            throw new NativeControlException("The native app did not return an inference snapshot.");
        var result = response.Generation;
        var terminal = result.State is "succeeded" or "failed" or "canceled";
        if (!IsUuidV7(result.OperationId) || expectedId is not null && result.OperationId != expectedId
            || expectedDeployment is not null && result.DeploymentId != expectedDeployment
            || result.State is not ("running" or "cancel_requested" or "succeeded" or "failed" or "canceled")
            || result.Terminal != terminal || result.Sequence is 0 or > long.MaxValue
            || result.Lifetime != "process" || Utf8Length(result.OutputText) > 256 * 1024)
            throw new NativeControlException("The native app returned inconsistent inference evidence. Refresh its state.");
        ValidateDeploymentId(result.DeploymentId);
        return new NativeGenerationSnapshot(result.OperationId, result.DeploymentId, result.State, terminal,
            (long)result.Sequence, result.OutputText, BoundedField(result.ErrorCode));
    }

    private static string ValidateDeploymentId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new NativeControlException("The native deployment identifier is invalid.", false);
        return value;
    }

    private static void ValidateConfirmationEvidence(bool required, string operationId, string planDigest, string capacitySampleId)
    {
        if (required && (!IsUuidV7(operationId) || string.IsNullOrWhiteSpace(planDigest) || string.IsNullOrWhiteSpace(capacitySampleId)
            || planDigest.Length > 256 || capacitySampleId.Length > 256 || planDigest.Any(char.IsControl) || capacitySampleId.Any(char.IsControl)))
            throw new NativeControlException("The native capacity decision is missing its exact operation, plan, or capacity sample.");
    }

    private static string BoundedField(string value)
    {
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new NativeControlException("The native app returned unsupported model or inference metadata.");
        return value;
    }

    private static int Utf8Length(string value)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new NativeControlException("The native text contains invalid Unicode.", false); }
    }

    private static string MiB(ulong bytes) => $"{bytes / (1024d * 1024d):N1} MiB";
}
