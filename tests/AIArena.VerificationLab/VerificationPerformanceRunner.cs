using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.VerificationLab;

internal sealed record VerificationMeasurement(
    string Metric,
    decimal Value,
    string Unit,
    string ThresholdKind,
    decimal Threshold,
    bool Passed);

internal sealed record VerificationMeasurementBundle(
    string Schema,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int HealthySoakRequests,
    int ProviderRestarts,
    IReadOnlyList<VerificationMeasurement> Measurements);

internal static class VerificationPerformanceRunner
{
    internal const string Schema = "ai_arena.verification_measurements.v1";
    private static readonly TimeSpan SoakTarget = TimeSpan.FromSeconds(3);
    private const int RestartCount = 3;

    public static async Task<int> RunAndWriteAsync(
        string outputPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath)
            || !Path.GetExtension(outputPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("FAIL verification measurements: invalid_output");
            return 2;
        }

        try
        {
            if (File.Exists(Path.GetFullPath(outputPath)))
            {
                await output.WriteLineAsync("FAIL verification measurements: measurement_exists");
                return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await output.WriteLineAsync("FAIL verification measurements: invalid_output");
            return 2;
        }

        VerificationMeasurementBundle bundle;
        try
        {
            bundle = await RunAsync(cancellationToken);
            await WriteAtomicAsync(outputPath, bundle, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("FAIL verification measurements: measurement_failed");
            return 1;
        }

        var passed = bundle.Measurements.Count(item => item.Passed);
        await output.WriteLineAsync($"PASS verification measurements: {passed}/{bundle.Measurements.Count}; soakRequests={bundle.HealthySoakRequests}; restarts={bundle.ProviderRestarts}");
        return passed == bundle.Measurements.Count ? 0 : 1;
    }

    internal static async Task<VerificationMeasurementBundle> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var process = Process.GetCurrentProcess();
        var healthySoakRequests = 0;
        long cancellationMilliseconds;
        long recoveryMilliseconds;

        await using (var provider = await ScriptedProviderHost.StartAsync(cancellationToken))
        using (var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            var client = new ModelProviderClient(httpClient);
            var config = Config(provider);
            var warmup = await client.CompleteChatAsync(config, Messages(), cancellationToken);
            Require(warmup.Ok, "warmup_failed");
            for (var index = 0; index < 16; index++)
            {
                var initializationProbe = await client.CompleteChatAsync(config, Messages(), cancellationToken);
                Require(initializationProbe.Ok, "initialization_probe_failed");
            }
            await Task.Delay(100, cancellationToken);
            process.Refresh();
            var initialHandles = process.HandleCount;

            provider.QueueFault(ScriptedProviderFault.Timeout);
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
                var watch = Stopwatch.StartNew();
                try
                {
                    _ = await client.CompleteChatAsync(Config(provider, timeoutSeconds: 10), Messages(), cancel.Token);
                    throw new InvalidOperationException("cancellation_not_observed");
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    watch.Stop();
                    cancellationMilliseconds = watch.ElapsedMilliseconds;
                }
            }

            provider.QueueFault(ScriptedProviderFault.Disconnect);
            var failed = await client.CompleteChatAsync(config, Messages(), cancellationToken);
            Require(!failed.Ok, "fault_not_observed");
            var recoveryWatch = Stopwatch.StartNew();
            var recovered = await client.CompleteChatAsync(config, Messages(), cancellationToken);
            recoveryWatch.Stop();
            Require(recovered.Ok, "fault_recovery_failed");
            recoveryMilliseconds = recoveryWatch.ElapsedMilliseconds;

            var soakWatch = Stopwatch.StartNew();
            while (soakWatch.Elapsed < SoakTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await client.CompleteChatAsync(config, Messages(), cancellationToken);
                Require(result.Ok, "soak_request_failed");
                healthySoakRequests++;
            }
            soakWatch.Stop();
            Require(healthySoakRequests >= 10, "soak_request_count_low");

            await Task.Delay(100, cancellationToken);
            process.Refresh();
            var handleGrowth = Math.Max(0, process.HandleCount - initialHandles);
            var peakWorkingSet = process.PeakWorkingSet64;
            var measurements = new List<VerificationMeasurement>
            {
                Maximum("cancellation-latency", cancellationMilliseconds, "milliseconds", 1_000),
                Maximum("fault-recovery-latency", recoveryMilliseconds, "milliseconds", 2_000),
                Maximum("handle-growth", handleGrowth, "handles", 64),
                Maximum("peak-working-set", peakWorkingSet, "bytes", 805_306_368),
                Minimum("soak-duration", (decimal)soakWatch.Elapsed.TotalMilliseconds, "milliseconds", 3_000)
            };

            await VerifyRestartCleanupAsync(cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            return new VerificationMeasurementBundle(
                Schema,
                startedAt,
                completedAt,
                healthySoakRequests,
                RestartCount,
                measurements);
        }
    }

    private static async Task VerifyRestartCleanupAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < RestartCount; index++)
        {
            await using var provider = await ScriptedProviderHost.StartAsync(cancellationToken);
            using var http = new HttpClient { BaseAddress = provider.BaseUri };
            using var response = await http.GetAsync("health", cancellationToken);
            Require(response.IsSuccessStatusCode, "restart_health_failed");
        }
    }

    private static ModelProviderConfig Config(ScriptedProviderHost provider, int timeoutSeconds = 5) => new()
    {
        BaseUrl = provider.BaseUri.AbsoluteUri,
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = "verification-measurement-token",
        Model = ScriptedProviderHost.PrimaryModel,
        Timeout = timeoutSeconds,
        Temperature = 0.25,
        MaxOutputTokens = 64
    };

    private static IReadOnlyList<ModelChatMessage> Messages() =>
    [
        new ModelChatMessage("system", "Run the bounded verification measurement."),
        new ModelChatMessage("user", "Return the deterministic local result.")
    ];

    private static VerificationMeasurement Maximum(
        string metric,
        decimal value,
        string unit,
        decimal threshold) =>
        new(metric, value, unit, "maximum", threshold, value <= threshold);

    private static VerificationMeasurement Minimum(
        string metric,
        decimal value,
        string unit,
        decimal threshold) =>
        new(metric, value, unit, "minimum", threshold, value >= threshold);

    private static async Task WriteAtomicAsync(
        string outputPath,
        VerificationMeasurementBundle bundle,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath))
        {
            throw new IOException("measurement_exists");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("measurement_parent_missing");
        }
        Directory.CreateDirectory(directory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        if (bytes.Length > 64 * 1024)
        {
            throw new InvalidDataException("measurement_oversize");
        }

        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16_384, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException(code);
        }
    }
}
