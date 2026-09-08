using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    // Explicit opt-in: the coordinating task owns the native process, deployment and output folder.
    private static int RunNativeInferenceIntegration(string[] arguments) =>
        RunNativeInferenceIntegrationAsync(arguments).GetAwaiter().GetResult();

    private static async Task<int> RunNativeInferenceIntegrationAsync(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "--native-inference-integration"
            || !Path.IsPathFullyQualified(arguments[1]) || string.IsNullOrWhiteSpace(arguments[2]))
        {
            Console.Error.WriteLine("Usage: --native-inference-integration <absolute-owned-output-dir> <deployment-id>");
            return 2;
        }

        string outputDirectory;
        FileStream receiptStream;
        try
        {
            outputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[1]));
            if (outputDirectory == Path.GetPathRoot(outputDirectory)) throw new IOException();
            // Do not follow a junction into an unrelated output directory.
            for (var directory = new DirectoryInfo(outputDirectory); directory is not null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            Directory.CreateDirectory(outputDirectory);
            receiptStream = new FileStream(Path.Combine(outputDirectory, "wpf-native-inference-receipt.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("A new receipt could not be reserved in the supplied owned output directory; no native request was sent.");
            return 2;
        }

        const string successPrompt = "Reply with exactly: Native inference is working.";
        const string cancellationPrompt = "Write a detailed numbered list of 200 practical programming tips. Explain each tip in several sentences and continue until the list is complete.";
        const double fixtureTemperature = 0.7;
        var deploymentId = arguments[2];
        var phases = new List<NativeInferenceIntegrationPhase>();
        NativeInferenceIntegrationPhase? currentPhase = null;
        var currentKind = "";
        var previousRequestKey = "";
        string? fixturePrompt = null;
        var fixtureMaximumTokens = 0;
        var fixtureOwned = false;
        var inferenceSucceeded = false;
        var ejectSucceeded = false;
        var receiptWritten = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var coordinator = new NativeServicesCoordinator(new NativeControlClient(), new ApplicationStatusCenter(), outputDirectory);
        var child = coordinator.Models;
        await using var receipt = receiptStream;

        void Capture()
        {
            if (currentPhase is not { } phase || child.LastRequest is not { } request
                || request.Kind != currentKind || request.DeploymentId != deploymentId || request.Key == previousRequestKey) return;
            if (currentKind == "generate" && (request.Prompt != fixturePrompt || request.MaximumTokens != fixtureMaximumTokens
                || request.Temperature != fixtureTemperature)) return;
            phase.IdempotencyKey = request.Key;
            if (currentKind == "generate" && child.Generation is { } generated)
            {
                phase.OperationId = generated.OperationId;
                phase.State = generated.State;
                phase.Terminal = generated.IsTerminal;
                phase.Sequence = generated.Sequence;
                phase.ErrorCode = string.IsNullOrEmpty(generated.ErrorCode) ? null
                    : generated.ErrorCode.Length <= 128 && generated.ErrorCode.All(character =>
                        char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
                        ? generated.ErrorCode : "unrecognized_error";
                var output = Encoding.UTF8.GetBytes(generated.Output);
                phase.OutputUtf8Bytes = output.Length;
                phase.OutputSha256 = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
                if (phase.ObservedNonterminalOutput.HasValue && output.Length > 0 && !generated.IsTerminal)
                    phase.ObservedNonterminalOutput = true;
            }
            else if (currentKind != "generate" && child.ModelOperation is { } operation)
            {
                phase.OperationId = operation.OperationId;
                phase.State = operation.State;
                phase.Terminal = operation.IsTerminal;
                phase.TransitionOrdinal = operation.Transition;
            }
        }

        async Task InvokeAsync(Func<Task> action)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { await action().WaitAsync(deadline.Token); }
            finally { Capture(); }
            deadline.Token.ThrowIfCancellationRequested();
        }

        void RequireFixture(bool condition, string safeReason)
        {
            if (!condition) throw new NativeInferenceIntegrationException(safeReason);
        }

        void CheckCapacityDecision()
        {
            RequireFixture(!child.CanConfirmLoad && child.ModelOperation?.ConfirmationRequired != true,
                "The native load requires an explicit capacity decision; this fixture does not confirm it.");
        }

        void ReportPhase()
        {
            Capture();
            if (currentPhase is { } phase)
                Console.WriteLine(JsonSerializer.Serialize(new { phase = phase.Phase, state = phase.State, terminal = phase.Terminal,
                    operationId = phase.OperationId, outputUtf8Bytes = phase.OutputUtf8Bytes, errorCode = phase.ErrorCode }));
            Console.Out.Flush();
        }

        async Task FinishLifecycleAsync(bool resident)
        {
            RequireFixture(child.ModelOperation is not null && !child.HasPendingRequest,
                "The native lifecycle request has no confirmed operation receipt.");
            while (true)
            {
                CheckCapacityDecision();
                if (child.CanObserve) await InvokeAsync(() => child.ObserveAsync(deadline.Token));
                CheckCapacityDecision();
                await InvokeAsync(() => child.RefreshAsync(deadline.Token));
                CheckCapacityDecision();
                if (child.ModelOperation is { IsTerminal: true } operation)
                {
                    RequireFixture(operation.State == "succeeded", "The native lifecycle operation did not succeed.");
                    if (child.SelectedModel is { } selected && selected.DeploymentId == deploymentId
                        && selected.Resident == resident && (!resident || selected.Serviceable)) return;
                }
                await Task.Delay(175, deadline.Token);
            }
        }

        async Task StartFixtureAsync(string phaseName, string prompt, int maximumTokens, bool cancel)
        {
            child.Prompt = prompt;
            child.MaximumTokens = maximumTokens;
            RequireFixture(child.CanStartInference, "The selected native deployment is not ready for the controlled inference fixture.");
            previousRequestKey = child.LastRequest?.Key ?? "";
            fixturePrompt = prompt;
            fixtureMaximumTokens = maximumTokens;
            currentKind = "generate";
            currentPhase = new(phaseName, "inference.generate.start",
                new { deploymentId, prompt, maximumTokens, temperature = fixtureTemperature })
            { ObservedNonterminalOutput = cancel ? false : null };
            phases.Add(currentPhase);
            await InvokeAsync(() => child.StartInferenceAsync(deadline.Token));
            RequireFixture(child.LastRequest is { Kind: "generate" } request && request.DeploymentId == deploymentId
                && request.Prompt == prompt && request.MaximumTokens == maximumTokens && request.Temperature == fixtureTemperature,
                "The coordinator did not retain the exact controlled inference fixture arguments.");
            RequireFixture(child.Generation is not null && !child.HasPendingRequest,
                "The native inference request has no confirmed generation receipt.");

            while (true)
            {
                Capture();
                var generated = child.Generation!;
                if (cancel && !generated.IsTerminal && Encoding.UTF8.GetByteCount(generated.Output) > 0)
                {
                    RequireFixture(child.CanCancel, "The streaming fixture could not request native cancellation.");
                    // Keep the original start key and args in the receipt for independent idempotent replay.
                    await InvokeAsync(() => child.CancelAsync(deadline.Token));
                    break;
                }
                if (generated.IsTerminal)
                {
                    RequireFixture(!cancel && generated.State == "succeeded" && !string.IsNullOrWhiteSpace(generated.Output),
                        cancel ? "The long fixture completed before nonterminal output and cancellation were observed."
                            : "The short fixture did not produce confirmed successful public output.");
                    ReportPhase();
                    return;
                }
                await Task.Delay(175, deadline.Token);
                await InvokeAsync(() => child.RefreshAsync(deadline.Token));
            }

            while (child.Generation?.IsTerminal != true)
            {
                await Task.Delay(175, deadline.Token);
                await InvokeAsync(() => child.RefreshAsync(deadline.Token));
            }
            RequireFixture(child.Generation is { State: "canceled" } && currentPhase?.ObservedNonterminalOutput == true,
                "The streaming fixture did not end in confirmed cancellation after nonterminal output.");
            ReportPhase();
        }

        try
        {
            await InvokeAsync(() => coordinator.ConnectAsync(deadline.Token));
            var matches = child.Items.Where(item => item.DeploymentId == deploymentId).ToArray();
            RequireFixture(matches.Length == 1, "The exact controlled deployment was not uniquely discovered.");
            child.SelectedModel = matches[0];
            RequireFixture(child.SelectedModel?.DeploymentId == deploymentId && !matches[0].Resident && !matches[0].Serviceable
                && !matches[0].Historical && matches[0].State == "unloaded" && child.CanLoadModel,
                "The controlled deployment must begin freshly unloaded with native lifecycle capabilities available.");
            fixtureOwned = true;
            previousRequestKey = child.LastRequest?.Key ?? "";
            currentKind = "load";
            currentPhase = new("load", "inference.models.load", new { deploymentId });
            phases.Add(currentPhase);
            await InvokeAsync(() => child.LoadSelectedModelAsync(deadline.Token));
            await FinishLifecycleAsync(resident: true);
            ReportPhase();

            await StartFixtureAsync("inference-success", successPrompt, 64, cancel: false);
            await StartFixtureAsync("inference-cancel", cancellationPrompt, 1024, cancel: true);
            inferenceSucceeded = true;
        }
        catch (NativeInferenceIntegrationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            ReportPhase();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Native inference integration ended before all outcomes were confirmed; inspect the partial receipt.");
            ReportPhase();
        }
        finally
        {
            // Eject only our initially unloaded fixture, and only after confirmed work permits another mutation.
            if (fixtureOwned && !deadline.IsCancellationRequested && child.SelectedModel?.DeploymentId == deploymentId && child.CanUnloadModel)
            {
                previousRequestKey = child.LastRequest?.Key ?? "";
                currentKind = "unload";
                currentPhase = new("eject", "inference.models.eject", new { deploymentId });
                phases.Add(currentPhase);
                try
                {
                    await InvokeAsync(() => child.UnloadSelectedModelAsync(deadline.Token));
                    await FinishLifecycleAsync(resident: false);
                    ejectSucceeded = true;
                }
                catch (Exception) { Console.Error.WriteLine("The fixture's final ejection was not confirmed; inspect native state before another run."); }
                ReportPhase();
            }
            coordinator.Dispose();
            try
            {
                await JsonSerializer.SerializeAsync(receipt, new { schemaVersion = 1, success = inferenceSucceeded && ejectSucceeded, deploymentId, phases },
                    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                await receipt.FlushAsync();
                receiptWritten = true;
            }
            catch (Exception) { Console.Error.WriteLine("The reserved integration receipt could not be fully written."); }
        }
        return inferenceSucceeded && ejectSucceeded && receiptWritten ? 0 : 1;
    }

    private sealed class NativeInferenceIntegrationPhase(string phase, string command, object args)
    {
        public string Phase { get; } = phase;
        public string Command { get; } = command;
        public string OperationId { get; set; } = "";
        public string State { get; set; } = "unconfirmed";
        public bool Terminal { get; set; }
        public long? TransitionOrdinal { get; set; }
        public long? Sequence { get; set; }
        public string? ErrorCode { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public object Args { get; } = args;
        public int? OutputUtf8Bytes { get; set; }
        public string? OutputSha256 { get; set; }
        public bool? ObservedNonterminalOutput { get; set; }
    }

    private sealed class NativeInferenceIntegrationException(string safeMessage) : Exception(safeMessage) { }
}
