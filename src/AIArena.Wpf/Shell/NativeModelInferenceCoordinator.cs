using System.ComponentModel;
using System.IO;
using System.Text;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>Independent model/inference receipts; native execution and provider routing stay native-owned.</summary>
internal sealed class NativeModelInferenceCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly INativeServicesClient operations;
    private readonly INativeModelClient? models;
    private readonly INativeInferenceClient? inference;
    private readonly ApplicationStatusCenter statusCenter;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? observation;
    private NativeConnectionSnapshot? connection;
    private NativeOperationSnapshot? operation;
    private NativeGenerationSnapshot? generation;
    private NativeModelMutation? pending;
    private NativeModelOption? selectedModel;
    private ApplicationStatusReceipt receipt;
    private string cancelKey = "";
    private string prompt = "";
    private string retainedOutput = "";
    private string preflightSummary = "";
    private int maximumTokens = 256;
    private bool submitting;
    private bool refreshing;
    private bool cancelling;
    private bool preparing;
    private bool catalogFresh;
    private bool confirmationDecisionAccepted;
    private bool disposed;

    internal NativeModelInferenceCoordinator(INativeServicesClient client, ApplicationStatusCenter statusCenter)
    {
        operations = client;
        models = client as INativeModelClient;
        inference = client as INativeInferenceClient;
        this.statusCenter = statusCenter;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<NativeModelOption> Items { get; private set; } = [];
    public NativeModelOption? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (!CanSelectModel || value?.DeploymentId == selectedModel?.DeploymentId) return;
            selectedModel = value is null ? null : Items.FirstOrDefault(item => item.DeploymentId == value.DeploymentId);
            preflightSummary = "";
            Notify();
        }
    }
    public string Prompt
    {
        get => prompt;
        set { if (CanEditPrompt && prompt != value) { prompt = value; Notify(); } }
    }
    public int MaximumTokens
    {
        get => maximumTokens;
        set { if (CanEditPrompt && maximumTokens != value) { maximumTokens = value; Notify(); } }
    }
    public string Output => generation?.Output ?? retainedOutput;
    public string Message { get; private set; } = "Connect to inspect configured native models.";
    public string ModelSummary => selectedModel is not { } model
        ? (Items.Count == 0 ? "No configured native deployments are available. Configure a model in the native app, then refresh." : "Choose a native deployment.")
        : Safe($"{model.DeploymentId}: {model.State}; resident: {model.Resident}; serviceable: {model.Serviceable}; health: {model.Health}; target: {model.Target}."
            + (model.Historical ? " Lifecycle evidence is historical." : "")
            + (string.IsNullOrEmpty(preflightSummary) ? "" : "\n" + preflightSummary));
    public string OperationId => generation?.OperationId ?? operation?.OperationId ?? "";
    public string OperationSummary => generation is { } generated
        ? $"Inference: {generated.State.Replace('_', ' ')}; update {generated.Sequence}; {generated.Output.Length} output characters."
        : operation is { } lifecycle ? Safe(lifecycle.Summary) : pending is not null ? "Request outcome is unconfirmed." : "No native model operation requested.";
    public string ConfirmationSummary => CanConfirmLoad
        ? "The native app requires a capacity decision. " + preflightSummary + " Select Load anyway only if you accept the native model load risk."
        : "";
    public NativeModelMutation? LastRequest { get; private set; }
    public NativeModelMutation? PendingRequest => pending;
    public NativeOperationSnapshot? ModelOperation => operation;
    public NativeGenerationSnapshot? Generation => generation;
    public bool HasPendingRequest => pending is not null;
    public bool IsObserving => observation is not null;
    private bool IsActive => operation?.IsTerminal == false || generation?.IsTerminal == false;
    private bool IsBusy => submitting || refreshing || cancelling || preparing;
    private bool CanMutate => catalogFresh && !disposed && !IsBusy && pending is null && !IsActive;
    private bool Supports(string command) => connection?.Commands.Contains(command, StringComparer.Ordinal) == true;
    private bool HasLifecycle => Supports("operation.state") && Supports("operation.wait") && Supports("operation.cancel");
    public bool CanSelectModel => CanMutate;
    public bool CanEditPrompt => CanMutate;
    public bool CanLoadModel => CanMutate && models is not null && HasLifecycle
        && Supports("inference.models.load") && Supports("inference.models.preflight") && selectedModel is { Resident: false };
    public bool CanUnloadModel => CanMutate && models is not null && HasLifecycle
        && Supports("inference.models.eject") && selectedModel is { Resident: true };
    public bool CanStartInference => CanMutate && inference is not null && selectedModel is { Serviceable: true }
        && Supports("inference.generate.start") && Supports("inference.generate.read") && Supports("inference.generate.cancel")
        && !string.IsNullOrWhiteSpace(prompt) && Encoding.UTF8.GetByteCount(prompt) <= 16 * 1024 && maximumTokens is >= 1 and <= 4096;
    public bool CanRetryPendingRequest => !disposed && !IsBusy && pending is not null && pending.Kind != "generate";
    public bool CanRefresh => !disposed && !refreshing && models is not null && Supports("inference.models.state");
    public bool CanObserve => !disposed && !IsObserving && IsActive && operation?.ConfirmationRequired != true;
    public bool CanCancel => !disposed && !cancelling && !submitting && pending is null
        && (generation?.CanCancel == true || operation?.CanCancel == true);
    public bool CanConfirmLoad => !disposed && !confirmationDecisionAccepted && !IsBusy && pending is null && models is not null
        && Supports("inference.models.load.confirm") && operation is { IsTerminal: false, ConfirmationRequired: true }
        && !string.IsNullOrEmpty(operation.PlanDigest) && !string.IsNullOrEmpty(operation.CapacitySampleId);

    internal async Task SetConnectionAsync(NativeConnectionSnapshot? snapshot, CancellationToken cancellationToken = default)
    {
        connection = snapshot;
        if (snapshot is null)
        {
            catalogFresh = false;
            Message = "Native connection is unavailable. Reconnect before requesting new work; existing receipts are retained.";
            Notify();
            return;
        }
        if (CanRefresh) await RefreshAsync(cancellationToken);
        else { Message = "Model controls are unavailable from this native app."; Notify(); }
    }

    public async Task LoadSelectedModelAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadModel || selectedModel is not { } selected || models is null) return;
        preparing = true;
        Message = "Checking the selected model's native load plan…";
        Notify();
        var ready = false;
        using (var request = Request(cancellationToken))
        {
            try
            {
                var preflight = await models.PreflightModelAsync(selected.DeploymentId, request.Token).WaitAsync(request.Token);
                if (preflight.DeploymentId != selected.DeploymentId) throw new InvalidDataException();
                preflightSummary = preflight.Summary;
                ready = !disposed;
            }
            catch (Exception) { if (!disposed) Message = "The native load plan could not be confirmed. Refresh the model state and try again."; }
            finally { preparing = false; Notify(); }
        }
        if (ready && CanLoadModel && selectedModel?.DeploymentId == selected.DeploymentId)
            await BeginAsync(new("load", selected.DeploymentId, NewKey()), cancellationToken);
    }

    public Task UnloadSelectedModelAsync(CancellationToken cancellationToken = default) =>
        CanUnloadModel && selectedModel is { } selected
            ? BeginAsync(new("unload", selected.DeploymentId, NewKey()), cancellationToken) : Task.CompletedTask;

    public Task StartInferenceAsync(CancellationToken cancellationToken = default) =>
        CanStartInference && selectedModel is { } selected
            ? BeginAsync(new("generate", selected.DeploymentId, NewKey(), prompt, maximumTokens, 0.7), cancellationToken) : Task.CompletedTask;

    public Task ConfirmLoadAsync(CancellationToken cancellationToken = default) =>
        CanConfirmLoad && operation is { } current
            ? BeginAsync(new("confirm", LastRequest?.DeploymentId ?? selectedModel?.DeploymentId ?? "", NewKey(),
                OperationId: current.OperationId, PlanDigest: current.PlanDigest, CapacitySampleId: current.CapacitySampleId), cancellationToken)
            : Task.CompletedTask;

    private async Task BeginAsync(NativeModelMutation mutation, CancellationToken cancellationToken)
    {
        StopObserving();
        if (mutation.Kind != "confirm") { operation = null; confirmationDecisionAccepted = false; }
        if (mutation.Kind != "generate") catalogFresh = false;
        generation = null;
        if (mutation.Kind == "generate") retainedOutput = "";
        cancelKey = "";
        LastRequest = pending = mutation;
        receipt = statusCenter.Begin("native.models", "Native services", ActionLabel(mutation.Kind) + " requested");
        await SubmitPendingAsync(cancellationToken);
    }

    public Task RetryPendingRequestAsync(CancellationToken cancellationToken = default) =>
        CanRetryPendingRequest ? SubmitPendingAsync(cancellationToken) : Task.CompletedTask;

    private async Task SubmitPendingAsync(CancellationToken cancellationToken)
    {
        if (disposed || submitting || pending is not { } mutation) return;
        submitting = true;
        Message = "Waiting for the native app to confirm the request…";
        RunningStatus(ActionLabel(mutation.Kind) + " submission");
        Notify();
        using var request = Request(cancellationToken);
        try
        {
            if (mutation.Kind == "generate")
            {
                var result = await inference!.StartInferenceAsync(mutation.DeploymentId, mutation.Prompt,
                    mutation.MaximumTokens, mutation.Temperature, mutation.Key, request.Token).WaitAsync(request.Token);
                if (disposed) return;
                ApplyGeneration(result, null, mutation.DeploymentId);
            }
            else
            {
                var task = mutation.Kind switch
                {
                    "load" => models!.LoadModelAsync(mutation.DeploymentId, mutation.Key, request.Token),
                    "unload" => models!.UnloadModelAsync(mutation.DeploymentId, mutation.Key, request.Token),
                    "confirm" => models!.ConfirmModelLoadAsync(mutation.OperationId, mutation.PlanDigest, mutation.CapacitySampleId, mutation.Key, request.Token),
                    _ => throw new InvalidOperationException()
                };
                var result = await task.WaitAsync(request.Token);
                if (disposed) return;
                if (mutation.Kind == "confirm") confirmationDecisionAccepted = true;
                ApplyOperation(result, mutation.Kind == "confirm" ? mutation.OperationId : null);
            }
            pending = null;

        }
        catch (NativeControlException exception) when (!exception.OutcomeUnknown && !mutation.HadUncertainAttempt)
        {
            if (!disposed)
            {
                pending = null;
                Message = "The native app declined this request. Refresh its state before trying again.";
                statusCenter.Fail(receipt, "Native request declined", Message, blocked: true);
            }
        }
        catch (Exception)
        {
            if (!disposed)
            {
                pending = mutation with { HadUncertainAttempt = true };
                Message = mutation.Kind == "generate"
                    ? "Inference submission is unconfirmed. Model and prompt remain locked. Inspect the native app before another submission; this preview cannot safely retry without proving the same native process."
                    : "The request outcome is unconfirmed. Retry same request to recover its original identity; model and prompt remain locked.";
                statusCenter.MarkUnconfirmed(receipt, "Native request unconfirmed", Message);
            }
        }
        finally { submitting = false; Notify(); }
        if (!disposed && pending is null && mutation.Kind == "confirm" && confirmationDecisionAccepted)
        {
            try
            {
                var current = await operations.GetOperationAsync(mutation.OperationId, request.Token).WaitAsync(request.Token);
                ApplyOperation(current, mutation.OperationId);
            }
            catch (Exception) { Unconfirmed("The capacity decision was accepted, but its latest operation state is unavailable. Refresh to reconcile it."); }
        }
        // Acceptance is already confirmed. A failed inventory refresh must not
        // turn that known receipt back into an uncertain submission.
        if (!disposed && pending is null && operation?.IsTerminal == true)
        {
            try { await RefreshCatalogAsync(request.Token); }
            catch (Exception) { Unconfirmed("The operation is confirmed, but model inventory could not be refreshed. Refresh before another model action."); }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh) return;
        refreshing = true;
        Notify();
        using var request = Request(cancellationToken);
        try
        {
            if (generation is { } generated && inference is not null)
                ApplyGeneration(await inference.ReadInferenceAsync(generated.OperationId, request.Token).WaitAsync(request.Token), generated.OperationId, generated.DeploymentId);
            else if (operation is { } current)
                ApplyOperation(await operations.GetOperationAsync(current.OperationId, request.Token).WaitAsync(request.Token), current.OperationId);
            await RefreshCatalogAsync(request.Token);
            if (!disposed && !IsActive && pending is null && generation is null && operation is null)
                Message = Items.Count == 0 ? "No native deployments are configured." : "Native model inventory refreshed. Choose a model to load or inspect.";
        }
        catch (Exception) { Unconfirmed("Native state could not be confirmed. Refresh again; an unavailable process-local inference result is not proof of completion."); }
        finally { refreshing = false; Notify(); }
    }

    private async Task RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        if (models is null || !Supports("inference.models.state")) return;
        catalogFresh = false;
        var catalog = await models.GetModelsAsync(cancellationToken).WaitAsync(cancellationToken);
        if (disposed) return;
        var selectedId = selectedModel?.DeploymentId;
        Items = catalog.Items;
        catalogFresh = true;
        selectedModel = Items.FirstOrDefault(item => item.DeploymentId == selectedId)
            ?? (selectedId is null ? Items.FirstOrDefault(item => item.DeploymentId == catalog.DefaultDeploymentId) ?? Items.FirstOrDefault() : null);
        if (catalog.Truncated) Message = "The native model list is partial. Only the first 256 deployments are shown.";
        Notify();
    }

    public async Task ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanObserve) return;
        var expectedId = OperationId;
        var deploymentId = generation?.DeploymentId;
        using var local = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        observation = local;
        Message = "Observing native progress. Stop observing leaves the native work running.";
        RunningStatus("Observing native " + (generation is null ? "model operation" : "inference"));
        Notify();
        try
        {
            while (!local.IsCancellationRequested && IsActive && OperationId == expectedId)
            {
                using var request = Request(local.Token, TimeSpan.FromSeconds(40));
                if (deploymentId is not null)
                {
                    var result = await inference!.ReadInferenceAsync(expectedId, request.Token).WaitAsync(request.Token);
                    if (disposed || local.IsCancellationRequested) break;
                    ApplyGeneration(result, expectedId, deploymentId);
                    if (!result.IsTerminal) await Task.Delay(200, local.Token);
                }
                else
                {
                    var cursor = operation!.Transition;
                    var result = await operations.WaitOperationAsync(expectedId, cursor, request.Token).WaitAsync(request.Token);
                    if (disposed || local.IsCancellationRequested) break;
                    ApplyOperation(result, expectedId);
                    if (result.IsTerminal) await RefreshCatalogAsync(request.Token);
                    if (result.ConfirmationRequired) break;
                    if (!result.IsTerminal && result.Transition <= cursor) await Task.Delay(250, local.Token);
                }
            }
        }
        catch (OperationCanceledException) when (local.IsCancellationRequested) { }
        catch (Exception) { if (ReferenceEquals(observation, local)) Unconfirmed("Native observation stopped before the outcome was confirmed. Refresh to reconcile the original request."); }
        finally
        {
            if (ReferenceEquals(observation, local))
            {
                observation = null;
                if (local.IsCancellationRequested && IsActive && !disposed)
                    Unconfirmed("Observation stopped. The native work was not canceled. Refresh to reconcile it.");
            }
            Notify();
        }
    }

    public void StopObserving()
    {
        var current = observation;
        observation = null;
        current?.Cancel();
        if (current is not null && IsActive && !disposed)
            Unconfirmed("Observation stopped. The native work was not canceled. Refresh to reconcile it.");
        Notify();
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCancel) return;
        var expectedId = OperationId;
        var deploymentId = generation?.DeploymentId;
        cancelling = true;
        cancelKey = string.IsNullOrEmpty(cancelKey) ? NewKey() : cancelKey;
        Message = "Requesting native cancellation; completion may finish first.";
        RunningStatus("Native cancellation requested");
        Notify();
        using var request = Request(cancellationToken);
        try
        {
            if (deploymentId is not null)
                ApplyGeneration(await inference!.CancelInferenceAsync(expectedId, cancelKey, request.Token).WaitAsync(request.Token), expectedId, deploymentId);
            else
                ApplyOperation(await operations.CancelOperationAsync(expectedId, cancelKey, request.Token).WaitAsync(request.Token), expectedId);
        }
        catch (Exception) { Unconfirmed("Cancellation is unconfirmed. Checking the latest native state…"); }
        finally
        {
            // Reconcile independently even when the local cancellation request timed out.
            if (!disposed)
            {
            using var reconcile = Request(CancellationToken.None);
            try
            {
                if (!disposed && deploymentId is not null)
                    ApplyGeneration(await inference!.ReadInferenceAsync(expectedId, reconcile.Token).WaitAsync(reconcile.Token), expectedId, deploymentId);
                else if (!disposed)
                {
                    ApplyOperation(await operations.GetOperationAsync(expectedId, reconcile.Token).WaitAsync(reconcile.Token), expectedId);
                    await RefreshCatalogAsync(reconcile.Token);
                }
            }
            catch (Exception) { Unconfirmed("Cancellation outcome could not be confirmed. Refresh the original native request."); }
            }
            cancelling = false;
            Notify();
        }
    }

    private void ApplyOperation(NativeOperationSnapshot result, string? expectedId)
    {
        if (disposed) return;
        if (!NativeControlClient.IsUuidV7(result.OperationId) || result.Transition < 0
            || expectedId is not null && result.OperationId != expectedId) throw new InvalidDataException();
        if (operation is { } current)
        {
            if (current.OperationId != result.OperationId || result.Transition < current.Transition || current.IsTerminal && !result.IsTerminal) return;
            if (current.Transition == result.Transition && current.State != result.State) return;
            // Older generic operation replies omit confirmation metadata. Keep
            // waiting acknowledgement evidence until the lifecycle state advances
            // or the user has explicitly submitted an accepted decision.
            if (!confirmationDecisionAccepted && current.ConfirmationRequired && !result.ConfirmationRequired
                && current.State == "waiting" && result.State == "waiting")
                result = result with { ConfirmationRequired = true, PlanDigest = current.PlanDigest, CapacitySampleId = current.CapacitySampleId };
        }
        operation = result;
        Message = result.ConfirmationRequired && !result.IsTerminal
            ? "The native load plan needs your explicit capacity decision before loading can continue."
            : result.IsTerminal ? "Native model operation " + result.State.Replace('_', ' ') + ". Refreshing observed residency."
            : "Native model operation " + result.State.Replace('_', ' ') + ".";
        PublishState(result.State, result.IsTerminal);
        Notify();
    }

    private void ApplyGeneration(NativeGenerationSnapshot result, string? expectedId, string deploymentId)
    {
        if (disposed) return;
        if (!NativeControlClient.IsUuidV7(result.OperationId) || result.Sequence < 0 || result.DeploymentId != deploymentId
            || expectedId is not null && result.OperationId != expectedId || Encoding.UTF8.GetByteCount(result.Output) > 256 * 1024)
            throw new InvalidDataException();
        if (generation is { } current)
        {
            if (current.OperationId != result.OperationId || result.Sequence < current.Sequence || current.IsTerminal && !result.IsTerminal) return;
            if (current.Sequence == result.Sequence && (current.State != result.State || current.Output != result.Output)) return;
        }
        // The native service sends full accumulated snapshots, never incremental chunks.
        generation = result;
        retainedOutput = result.Output;
        Message = result.IsTerminal ? "Native inference " + result.State.Replace('_', ' ') + "."
            : "Native inference is running. Output below is the latest observed public text.";
        PublishState(result.State, result.IsTerminal);
        Notify();
    }

    private void PublishState(string state, bool terminal)
    {
        var label = generation is null ? "Native model operation" : "Native inference";
        RunningStatus(label + ": " + state.Replace('_', ' '));
        if (!terminal) return;
        switch (state)
        {
            case "succeeded": statusCenter.Complete(receipt, label + " completed"); break;
            case "canceled": statusCenter.Cancel(receipt, label + " canceled"); break;
            case "blocked": case "failed": statusCenter.Fail(receipt, label + " " + state, blocked: state == "blocked"); break;
            default: statusCenter.MarkUnconfirmed(receipt, label + " " + state); break;
        }
    }
    private void RunningStatus(string summary)
    {
        if (!statusCenter.Update(receipt, summary)) receipt = statusCenter.Begin("native.models", "Native services", summary);
    }
    private void Unconfirmed(string message)
    {
        if (disposed) return;
        if (!IsActive && pending is null && (generation?.IsTerminal == true || operation?.IsTerminal == true))
        {
            var confirmedState = generation?.State ?? operation!.State;
            Message = "The last native result remains " + confirmedState.Replace('_', ' ') + ". Refreshed state is unavailable.";
            Notify();
            return;
        }
        Message = message;
        if (IsActive || pending is not null) statusCenter.MarkUnconfirmed(receipt, "Native result unconfirmed", message);
        Notify();
    }
    private CancellationTokenSource Request(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        request.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        return request;
    }
    private static string ActionLabel(string kind) => kind switch { "load" => "Native model load", "unload" => "Native model unload", "confirm" => "Native load decision", _ => "Native inference" };
    private static string NewKey() => Guid.CreateVersion7().ToString("D");
    private static string Safe(string value) => AppErrorPresenter.RedactAndBound(value, 1600, "Model details unavailable.");
    private void Notify() { if (!disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopObserving();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

internal sealed record NativeModelMutation(string Kind, string DeploymentId, string Key,
    string Prompt = "", int MaximumTokens = 256, double Temperature = 0.7,
    string OperationId = "", string PlanDigest = "", string CapacitySampleId = "", bool HadUncertainAttempt = false);
