using System.ComponentModel;
using System.IO;
using System.Windows;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>Process-only presentation of work owned by the separate native app.</summary>
internal sealed class NativeServicesCoordinator : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(40);
    private readonly INativeServicesClient client;
    private readonly ApplicationStatusCenter statusCenter;
    private readonly CancellationTokenSource lifetime = new();
    private NativeServicesWindow? window;
    private CancellationTokenSource? observation;
    private NativeConnectionSnapshot? connection;
    private NativeOperationSnapshot? operation;
    private PendingBundle? pending;
    private ApplicationStatusReceipt bundleReceipt;
    private string cancelKey = "";
    private string destination;
    private string requestedDestination = "";
    private bool connecting;
    private bool submitting;
    private bool reconciling;
    private bool cancelling;
    private bool disposed;

    internal NativeServicesCoordinator(
        INativeServicesClient client,
        ApplicationStatusCenter statusCenter,
        string outputDirectory)
    {
        this.client = client;
        this.statusCenter = statusCenter;
        Models = new NativeModelInferenceCoordinator(client, statusCenter);
        OutputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        destination = Path.Combine(OutputDirectory, $"native-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public NativeModelInferenceCoordinator Models { get; }
    public string OutputDirectory { get; }
    public string Destination
    {
        get => destination;
        set
        {
            if (!CanChooseDestination || destination == value) return;
            destination = value;
            Notify();
        }
    }

    public string HealthSummary => Safe(connection?.HealthSummary, "Not connected.");
    public string ModelsSummary => Safe(connection?.ModelsSummary, "Model information is unavailable.");
    public string OperationsSummary => Safe(connection?.OperationsSummary, "Operation information is unavailable.");
    public string Message { get; private set; } = "Connect to the running native app to inspect its services.";
    public string OperationId => Safe(operation?.OperationId, "");
    public string OperationSummary => Safe(operation?.Summary, pending is null ? "No bundle requested." : "Submission is awaiting confirmation.");
    public string OperationState => operation?.State switch
    {
        "queued" => "Queued",
        "running" => "Running",
        "cancel_requested" => "Cancellation requested",
        "succeeded" => "Completed",
        "partial" => "Partially completed",
        "blocked" => "Blocked",
        "failed" => "Failed",
        "canceled" => "Canceled",
        "interrupted" => "Interrupted",
        null => pending is null ? "Ready" : "Unconfirmed",
        _ => "In progress"
    };
    private double? ObservedProgress => operation?.Progress is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;
    public double Progress => ObservedProgress ?? 0;
    public bool ProgressIsIndeterminate => IsObserving && ObservedProgress is null;
    // The operation protocol does not return a path. Confirm only the exact
    // operator-selected file locally after the native app reports success.
    public string ResultPath => operation?.State == "succeeded" && File.Exists(requestedDestination)
        ? requestedDestination : "";
    public string LastRequestIdempotencyKey { get; private set; } = "";
    public string LastRequestDestination => requestedDestination;
    public NativeOperationSnapshot? Operation => operation;
    public bool HasPendingRequest => pending is not null;
    public string PendingDestination => pending?.Destination ?? "";
    public string PendingIdempotencyKey => pending?.Key ?? "";
    public bool IsObserving => observation is not null;
    public bool CanConnect => !disposed && !connecting;
    public bool CanChooseDestination => !disposed && !submitting && !reconciling && !cancelling && pending is null && operation?.IsTerminal != false;
    public bool CanCreateBundle => CanChooseDestination && connection?.CanCreateDiagnosticBundle == true;
    public bool CanRetryPendingRequest => !disposed && !submitting && pending is not null;
    public bool CanReconcile => !disposed && !reconciling && operation is not null;
    public bool CanObserve => !disposed && !IsObserving && operation?.IsTerminal == false;
    public bool CanCancel => !disposed && !cancelling && operation?.CanCancel == true;

    public void Show(Window owner)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (window is not null)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }

        window = new NativeServicesWindow(owner, this);
        window.Closed += WindowClosed;
        window.Show();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        if (window is not null) window.Closed -= WindowClosed;
        window = null;
        StopObserving();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConnect) return;
        connecting = true;
        var receipt = statusCenter.Begin("native.connection", "Native services", "Connecting to native services");
        Message = "Checking the running native app…";
        Notify();
        using var request = CreateRequest(cancellationToken, RequestTimeout);
        try
        {
            connection = await client.ConnectAsync(request.Token).WaitAsync(request.Token);
            if (disposed) return;
            Message = pending is not null
                ? "Connected. The earlier submission is unconfirmed. Use Retry same request to recover it."
                : operation?.IsTerminal == false
                    ? "Connected. Refresh the current operation status to check its outcome."
                    : connection.CanCreateDiagnosticBundle
                        ? "Connected. Choose a new ZIP file to collect diagnostics."
                        : "Connected. Diagnostic bundle creation is unavailable from this native app.";
            statusCenter.Complete(receipt, "Native services connected");
            await Models.SetConnectionAsync(connection, request.Token);
        }
        catch (Exception)
        {
            connection = null;
            await Models.SetConnectionAsync(null);
            if (!disposed)
            {
                Message = "Native services could not be reached. Open the native app and try Connect / Refresh again.";
                statusCenter.MarkUnconfirmed(receipt, "Native connection unavailable", Message);
            }
        }
        finally
        {
            connecting = false;
            Notify();
        }
    }

    public async Task CreateBundleAsync(string newDestination, CancellationToken cancellationToken = default)
    {
        if (!CanCreateBundle) return;
        EnsureOwnedOutputFolder(newDestination);
        if (!TryNewDestination(newDestination, out var fullPath))
        {
            Message = "Choose an absolute path to a new .zip file in an existing folder. Existing files cannot be replaced.";
            Notify();
            return;
        }

        StopObserving();
        operation = null;
        cancelKey = "";
        requestedDestination = destination = fullPath;
        LastRequestIdempotencyKey = Guid.CreateVersion7().ToString();
        pending = new PendingBundle(fullPath, LastRequestIdempotencyKey);
        bundleReceipt = statusCenter.Begin("native.diagnostics", "Native services", "Requesting a diagnostic bundle");
        await SubmitPendingAsync(cancellationToken);
    }

    public Task RetryPendingRequestAsync(CancellationToken cancellationToken = default) =>
        CanRetryPendingRequest ? SubmitPendingAsync(cancellationToken) : Task.CompletedTask;

    private async Task SubmitPendingAsync(CancellationToken cancellationToken)
    {
        if (pending is not { } requestIdentity || submitting || disposed) return;
        submitting = true;
        Message = "Waiting for the native app to confirm the bundle request…";
        UpdateBundleStatus("Confirming diagnostic bundle request");
        Notify();
        using var request = CreateRequest(cancellationToken, RequestTimeout);
        try
        {
            var snapshot = await client.CreateDiagnosticBundleAsync(
                requestIdentity.Destination, requestIdentity.Key, request.Token).WaitAsync(request.Token);
            if (disposed) return;
            ValidateSnapshot(snapshot);
            ApplyOperation(snapshot);
            pending = null;
            if (!snapshot.IsTerminal) Message = "The native app accepted the bundle. Observe progress or refresh its status.";
        }
        catch (NativeControlException ex) when (!ex.OutcomeUnknown)
        {
            if (!disposed)
            {
                if (requestIdentity.HadUncertainAttempt)
                {
                    // A rejection of this retry cannot disprove acceptance of the
                    // earlier attempt. Its original request identity stays locked.
                    Message = "This retry was declined, but the earlier submission is still unconfirmed. Keep the same request and retry after reconnecting.";
                    statusCenter.MarkUnconfirmed(bundleReceipt, "Earlier diagnostic bundle submission unconfirmed", Message);
                }
                else
                {
                    pending = null;
                    Message = "The native app declined the bundle request. Refresh the connection and check the chosen destination.";
                    statusCenter.Fail(bundleReceipt, "Diagnostic bundle request declined", Message, blocked: true);
                }
            }
        }
        catch (Exception)
        {
            if (!disposed)
            {
                pending = requestIdentity with { HadUncertainAttempt = true };
                Message = "The submission outcome is unknown. Retry same request to recover it; the destination stays locked to avoid a duplicate bundle.";
                statusCenter.MarkUnconfirmed(bundleReceipt, "Diagnostic bundle submission unconfirmed", Message);
            }
        }
        finally
        {
            submitting = false;
            Notify();
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!CanReconcile || operation is not { } expected) return;
        reconciling = true;
        Notify();
        try
        {
            await ReadLatestOperationAsync(expected.OperationId, cancellationToken);
        }
        finally
        {
            reconciling = false;
            Notify();
        }
    }

    private async Task ReadLatestOperationAsync(string expectedId, CancellationToken cancellationToken = default)
    {
        if (disposed) return;
        using var request = CreateRequest(cancellationToken, RequestTimeout);
        try
        {
            var snapshot = await client.GetOperationAsync(expectedId, request.Token).WaitAsync(request.Token);
            if (disposed) return;
            ApplyOperation(snapshot, expectedId);
            if (operation?.IsTerminal == false)
                Message = operation.State == "cancel_requested"
                    ? "Cancellation is requested. The native app has not confirmed a final outcome yet."
                    : "The latest operation status is confirmed. Observe progress for further updates.";
        }
        catch (Exception)
        {
            if (!disposed && operation?.IsTerminal != true)
            {
                Message = "The latest operation status could not be confirmed. The native app may still be working. Refresh status to reconcile.";
                statusCenter.MarkUnconfirmed(bundleReceipt, "Diagnostic bundle status unconfirmed", Message);
            }
        }
    }
    public async Task ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanObserve || operation is not { } initial) return;
        using var localObservation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        observation = localObservation;
        Message = "Observing progress. The native app owns this operation.";
        UpdateBundleStatus("Observing native diagnostic bundle");
        Notify();
        try
        {
            while (!localObservation.IsCancellationRequested && operation?.IsTerminal == false)
            {
                var previousTransition = operation.Transition;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(localObservation.Token);
                request.CancelAfter(ObservationTimeout);
                var snapshot = await client.WaitOperationAsync(initial.OperationId, previousTransition, request.Token).WaitAsync(request.Token);
                if (disposed || localObservation.IsCancellationRequested) break;
                ApplyOperation(snapshot, initial.OperationId);
                if (operation?.IsTerminal == false && operation.Transition <= previousTransition)
                    await Task.Delay(250, localObservation.Token);
            }
        }
        catch (OperationCanceledException) when (localObservation.IsCancellationRequested)
        {
            if (!disposed && operation?.IsTerminal == false && ReferenceEquals(observation, localObservation))
                MarkObservationStopped();
        }
        catch (Exception)
        {
            if (!disposed && operation?.IsTerminal == false && ReferenceEquals(observation, localObservation))
            {
                Message = "Progress updates stopped before the outcome was confirmed. Refresh status to check the native operation.";
                statusCenter.MarkUnconfirmed(bundleReceipt, "Diagnostic bundle observation interrupted", Message);
            }
        }
        finally
        {
            if (ReferenceEquals(observation, localObservation)) observation = null;
            Notify();
        }
    }

    public void StopObserving()
    {
        var active = observation;
        observation = null;
        active?.Cancel();
        if (!disposed && active is not null && operation?.IsTerminal == false) MarkObservationStopped();
        Notify();
    }

    private void MarkObservationStopped()
    {
        Message = "Observation stopped. The native operation was not canceled. Refresh status to check its outcome.";
        statusCenter.MarkUnconfirmed(bundleReceipt, "Diagnostic bundle observation stopped", Message);
    }

    public async Task CancelOperationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCancel || operation is not { } expected) return;
        cancelling = true;
        cancelKey = string.IsNullOrEmpty(cancelKey) ? Guid.CreateVersion7().ToString() : cancelKey;
        Message = "Requesting cancellation from the native app. The operation may finish first.";
        UpdateBundleStatus("Requesting native diagnostic bundle cancellation");
        Notify();
        using var request = CreateRequest(cancellationToken, RequestTimeout);
        try
        {
            var snapshot = await client.CancelOperationAsync(expected.OperationId, cancelKey, request.Token).WaitAsync(request.Token);
            if (!disposed) ApplyOperation(snapshot, expected.OperationId);
        }
        catch (Exception)
        {
            if (!disposed && operation?.IsTerminal != true)
            {
                Message = "Cancellation is unconfirmed. Checking the native operation for its latest status…";
                statusCenter.MarkUnconfirmed(bundleReceipt, "Native cancellation unconfirmed", Message);
            }
        }
        finally
        {
            // Local request cancellation does not prove native cancellation. Read back
            // independently, even after an acknowledgement or a racing success.
            if (!disposed) await ReadLatestOperationAsync(expected.OperationId);
            cancelling = false;
            Notify();
        }
    }

    private void ApplyOperation(NativeOperationSnapshot snapshot, string? expectedId = null)
    {
        ValidateSnapshot(snapshot);
        if (expectedId is not null && snapshot.OperationId != expectedId)
            throw new InvalidDataException("Operation identity mismatch.");
        if (operation is { } current)
        {
            if (current.OperationId != snapshot.OperationId || snapshot.Transition < current.Transition) return;
            if (current.IsTerminal && !snapshot.IsTerminal) return;
            if (snapshot.Transition == current.Transition && snapshot.State != current.State) return;
            if (current.IsTerminal && current == snapshot) return;
        }

        operation = snapshot;
        Message = snapshot.State switch
        {
            "succeeded" => string.IsNullOrEmpty(ResultPath)
                ? "The native app reports completion, but the chosen ZIP file could not be found locally. Check the destination before using the result."
                : "The native app reports completion, and the chosen ZIP file exists at the result path below.",
            "partial" => "The native app produced a partial result. Review the operation details before using the bundle.",
            "blocked" => "The native app blocked this operation. Review its details and choose a new request when ready.",
            "failed" => "The native operation failed. Review its details before making another request.",
            "canceled" => "The native app confirmed cancellation.",
            "interrupted" => "The native app reports that the operation was interrupted.",
            "cancel_requested" => "Cancellation was requested. Waiting for the native app to confirm the final outcome.",
            _ => "The native app is preparing the diagnostic bundle."
        };
        UpdateBundleStatus(OperationState + ": native diagnostic bundle", Safe(snapshot.Summary), ObservedProgress);
        switch (snapshot.State)
        {
            case "succeeded":
                statusCenter.Complete(bundleReceipt, "Native diagnostic bundle completed");
                break;
            case "canceled":
                statusCenter.Cancel(bundleReceipt, "Native diagnostic bundle canceled");
                break;
            case "blocked":
            case "failed":
                statusCenter.Fail(bundleReceipt, "Native diagnostic bundle " + snapshot.State, Safe(snapshot.Summary), blocked: snapshot.State == "blocked");
                break;
            case "partial":
            case "interrupted":
                statusCenter.MarkUnconfirmed(bundleReceipt, "Native diagnostic bundle " + snapshot.State, Safe(snapshot.Summary));
                break;

        }
        if (snapshot.IsTerminal) StopObserving();
        Notify();
    }

    private void UpdateBundleStatus(string summary, string? detail = null, double? progress = null)
    {
        // Unconfirmed is terminal for an application-status receipt. A later
        // authoritative observation therefore starts a fresh receipt generation.
        if (!statusCenter.Update(bundleReceipt, summary, detail, progress))
            bundleReceipt = statusCenter.Begin("native.diagnostics", "Native services", summary, detail, progress);
    }
    private static void ValidateSnapshot(NativeOperationSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.OperationId) || snapshot.OperationId.Length > 200 || snapshot.Transition < 0)
            throw new InvalidDataException("Invalid operation snapshot.");
    }

    private CancellationTokenSource CreateRequest(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        request.CancelAfter(timeout);
        return request;
    }

    private void EnsureOwnedOutputFolder(string candidate)
    {
        try
        {
            if (Path.IsPathFullyQualified(candidate)
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), OutputDirectory, StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(OutputDirectory);
        }
        catch (Exception)
        {
            // The ordinary destination validation presents a bounded failure.
        }
    }
    private static bool TryNewDestination(string value, out string fullPath)
    {
        fullPath = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                || !Path.GetExtension(value).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = Path.GetFullPath(value);
            return !File.Exists(fullPath) && !Directory.Exists(fullPath) && Directory.Exists(Path.GetDirectoryName(fullPath));
        }
        catch (Exception) { return false; }
    }

    private static string Safe(string? value, string fallback = "Details unavailable.") =>
        AppErrorPresenter.RedactAndBound(value, 600, fallback);

    private void Notify()
    {
        if (!disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopObserving();
        Models.Dispose();
        lifetime.Cancel();
        if (window is not null)
        {
            window.Closed -= WindowClosed;
            window.Close();
            window = null;
        }
        lifetime.Dispose();
    }

    private sealed record PendingBundle(string Destination, string Key, bool HadUncertainAttempt = false);
}
