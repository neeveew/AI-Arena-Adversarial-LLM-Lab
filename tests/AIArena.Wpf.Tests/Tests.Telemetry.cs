using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static partial class Program
{
    static void NvidiaTelemetryProbeCacheBoundsProcessLaunches()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var successfulProbeCalls = 0;
        var successfulCache = new NvidiaGpuProbeCache(
            () =>
            {
                successfulProbeCalls++;
                return
                [
                    new WindowsGpuProbe("Test GPU", "NVIDIA", 12, 4, 40)
                ];
            },
            () => now);

        Require(successfulCache.Sample().Count == 1, "the initial NVIDIA telemetry sample should invoke the probe");
        now += NvidiaGpuProbeCache.SuccessfulSampleLifetime - TimeSpan.FromMilliseconds(1);
        Require(successfulCache.Sample().Count == 1, "a fresh NVIDIA telemetry sample should be reused");
        Require(successfulProbeCalls == 1, "fresh telemetry should not launch another NVIDIA probe process");

        now += TimeSpan.FromMilliseconds(1);
        Require(successfulCache.Sample().Count == 1, "an expired NVIDIA telemetry sample should be refreshed");
        Require(successfulProbeCalls == 2, "expired telemetry should launch exactly one replacement probe");

        var failedProbeCalls = 0;
        var failedCache = new NvidiaGpuProbeCache(
            () =>
            {
                failedProbeCalls++;
                return Array.Empty<WindowsGpuProbe>();
            },
            () => now);

        Require(failedCache.Sample().Count == 0, "an unavailable NVIDIA probe should return no GPUs");
        now += NvidiaGpuProbeCache.FailedProbeRetryDelay - TimeSpan.FromMilliseconds(1);
        Require(failedCache.Sample().Count == 0, "a failed NVIDIA probe should stay in its retry cooldown");
        Require(failedProbeCalls == 1, "the retry cooldown should suppress repeated failed process launches");

        now += TimeSpan.FromMilliseconds(1);
        Require(failedCache.Sample().Count == 0, "an unavailable NVIDIA probe may retry after its cooldown");
        Require(failedProbeCalls == 2, "the failed NVIDIA probe should retry exactly once when its cooldown expires");
    }

    static void TelemetrySplitCadenceBoundsExpensiveProbesForSixtySeconds()
    {
        var startedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTelemetryTimeProvider(startedAt);
        var fastCalls = 0;
        var gpuCalls = 0;
        var nvidiaCalls = 0;
        var wmiCounterCalls = 0;
        var adapterCalls = 0;
        var fastDispatcherPublishes = 0;
        var gpuDispatcherPublishes = 0;
        var nvidia = new NvidiaGpuProbeCache(
            () =>
            {
                nvidiaCalls++;
                return Array.Empty<WindowsGpuProbe>();
            },
            clock.GetUtcNow);
        var adapters = new GpuAdapterProbeCache(
            () =>
            {
                adapterCalls++;
                return [new WindowsGpuProbe("Stable test adapter", "test", 16, null, null)];
            },
            clock.GetUtcNow);
        var controller = new TelemetryCadenceController(
            clock,
            _ =>
            {
                fastCalls++;
                return Task.FromResult(new SystemTelemetryFastSample(
                    fastCalls % 100,
                    8 + (fastCalls % 4),
                    40 + (fastCalls % 5),
                    32));
            },
            _ =>
            {
                gpuCalls++;
                nvidia.Sample();
                wmiCounterCalls++;
                var adapter = adapters.Sample();
                return Task.FromResult(new SystemTelemetryGpuSample(
                    25,
                    4,
                    adapter.Name,
                    adapter.TotalVramGb));
            },
            (_, _) =>
            {
                fastDispatcherPublishes++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                gpuDispatcherPublishes++;
                return Task.CompletedTask;
            });

        Require(controller.SetVisible(true), "showing telemetry should arm an immediate fast and GPU sample");
        var baselineAllocated = MeasureLegacyTelemetryProjectionAllocations();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var second = 0; second <= 60; second++)
        {
            clock.UtcNow = startedAt.AddSeconds(second);
            controller.PulseAsync().GetAwaiter().GetResult();
        }

        var optimizedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var receipt = controller.Receipt;
        Require(fastCalls == 61 && receipt.FastSamples == 61,
            "CPU and RAM should retain one-second responsiveness across the sixty-second window");
        Require(gpuCalls == 13 && receipt.GpuSamples == 13,
            "stable GPU evidence should adapt from the three-second initial cadence to five-second probes");
        Require(wmiCounterCalls == 13,
            "WMI GPU counters should run only on the slower GPU cadence");
        Require(nvidiaCalls == 2,
            "the NVIDIA failure cooldown should bound process attempts beneath the GPU sampling cadence");
        Require(adapterCalls == 1,
            "static GPU adapter evidence should be reused across every dynamic counter sample");
        Require(fastDispatcherPublishes == 61 && receipt.FastPublishes == 61,
            "changed CPU and RAM evidence should publish at the responsive cadence");
        Require(gpuDispatcherPublishes == 1 && receipt.GpuPublishes == 1,
            "unchanged GPU evidence should not repeatedly write the dispatcher-bound UI");
        Require(receipt.ActualOverlapCount == 0
                && receipt.MaxFastConcurrency == 1
                && receipt.MaxGpuConcurrency == 1,
            "split sampling should never overlap a probe with another probe in its cadence");

        Console.WriteLine(
            "TELEMETRY_RECEIPT window=60s "
            + "baseline_fast=61 baseline_gpu=61 baseline_wmi=61 baseline_nvidia=3 baseline_gpu_ui=61 "
            + $"optimized_fast={receipt.FastSamples} optimized_gpu={receipt.GpuSamples} "
            + $"optimized_wmi={wmiCounterCalls} optimized_nvidia={nvidiaCalls} adapters={adapterCalls} "
            + $"fast_ui={receipt.FastPublishes} gpu_ui={receipt.GpuPublishes} "
            + $"baseline_projection_allocated_bytes={baselineAllocated} optimized_engine_allocated_bytes={optimizedAllocated} "
            + $"overlap={receipt.ActualOverlapCount}");
    }

    static void TelemetryCadenceCancelsStaleWorkAndPreventsOverlap()
    {
        var clock = new MutableTelemetryTimeProvider(
            new DateTimeOffset(2026, 8, 14, 13, 0, 0, TimeSpan.Zero));
        var firstFast = new TaskCompletionSource<SystemTelemetryFastSample>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGpu = new TaskCompletionSource<SystemTelemetryGpuSample>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCalls = 0;
        var gpuCalls = 0;
        var publishCount = 0;
        CancellationToken firstFastToken = default;
        CancellationToken firstGpuToken = default;
        var controller = new TelemetryCadenceController(
            clock,
            token =>
            {
                fastCalls++;
                if (fastCalls == 1)
                {
                    firstFastToken = token;
                    return firstFast.Task;
                }

                return Task.FromResult(new SystemTelemetryFastSample(42, 8, 50, 16));
            },
            token =>
            {
                gpuCalls++;
                if (gpuCalls == 1)
                {
                    firstGpuToken = token;
                    return firstGpu.Task;
                }

                return Task.FromResult(new SystemTelemetryGpuSample(35, 3, "Test GPU", 12));
            },
            (_, _) =>
            {
                publishCount++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                publishCount++;
                return Task.CompletedTask;
            });

        controller.SetVisible(true);
        var stalePulse = controller.PulseAsync();
        clock.UtcNow += TimeSpan.FromSeconds(10);
        controller.PulseAsync().GetAwaiter().GetResult();
        var duringOverlap = controller.Receipt;
        Require(fastCalls == 1 && gpuCalls == 1,
            "a timer re-entry must not launch a second fast or GPU probe while either cadence is in flight");
        Require(duringOverlap.FastOverlapSuppressions == 1
                && duringOverlap.GpuOverlapSuppressions == 1
                && duringOverlap.ActualOverlapCount == 0,
            "due re-entry should be suppressed and recorded without actual overlap");

        Require(controller.SetVisible(false), "hiding telemetry should advance its lifecycle");
        Require(firstFastToken.IsCancellationRequested && firstGpuToken.IsCancellationRequested,
            "hiding telemetry should cancel both in-flight cadence tokens");
        for (var refresh = 0; refresh < 32; refresh++)
        {
            controller.SetVisible(true);
            controller.Refresh();
            controller.SetVisible(false);
        }
        Require(controller.RetiredLifecycleCount == 1,
            "repeated lifecycle changes retained cancellation sources that had never owned work");
        firstFast.SetResult(new SystemTelemetryFastSample(99, 9, 60, 16));
        firstGpu.SetResult(new SystemTelemetryGpuSample(99, 9, "Stale GPU", 12));
        stalePulse.GetAwaiter().GetResult();
        Require(publishCount == 0,
            "samples completing after telemetry is hidden must not publish stale UI state");
        controller.PulseAsync().GetAwaiter().GetResult();
        Require(fastCalls == 1 && gpuCalls == 1,
            "hidden telemetry should remain entirely idle");

        controller.SetVisible(true);
        controller.PulseAsync().GetAwaiter().GetResult();
        Require(fastCalls == 2 && gpuCalls == 2 && publishCount == 2,
            "showing telemetry again should take and publish fresh samples for both cadences");
        controller.Refresh();
        controller.PulseAsync().GetAwaiter().GetResult();
        Require(fastCalls == 3 && gpuCalls == 3 && publishCount == 4,
            "an explicit refresh should invalidate equality state and publish fresh evidence");
        Require(controller.Receipt.ActualOverlapCount == 0,
            "hidden, shown, and refreshed generations must all preserve the no-overlap invariant");
        Require(controller.RetiredLifecycleCount == 0,
            "completed stale telemetry work retained its canceled lifecycle source");
    }

    static void TelemetryFailurePresentationAndSparklineHistoryStayStable()
    {
        var clock = new MutableTelemetryTimeProvider(
            new DateTimeOffset(2026, 8, 14, 14, 0, 0, TimeSpan.Zero));
        SystemTelemetryFastSample? publishedFast = null;
        SystemTelemetryGpuSample? publishedGpu = null;
        var controller = new TelemetryCadenceController(
            clock,
            _ => Task.FromException<SystemTelemetryFastSample>(new InvalidOperationException("fast failure")),
            _ => Task.FromException<SystemTelemetryGpuSample>(new InvalidOperationException("GPU failure")),
            (sample, _) =>
            {
                publishedFast = sample;
                return Task.CompletedTask;
            },
            (sample, _) =>
            {
                publishedGpu = sample;
                return Task.CompletedTask;
            });

        controller.SetVisible(true);
        controller.PulseAsync().GetAwaiter().GetResult();
        Require(Equals(publishedFast, SystemTelemetryFastSample.Unavailable)
                && Equals(publishedGpu, SystemTelemetryGpuSample.Unavailable),
            "probe failures should become deterministic unavailable samples rather than escaping the timer callback");
        Require(controller.Receipt.Failures == 2,
            "each failed cadence should be visible in the deterministic receipt");

        var unavailableFast = TelemetryPresentation.FromFast(SystemTelemetryFastSample.Unavailable);
        var unavailableGpu = TelemetryPresentation.FromGpu(SystemTelemetryGpuSample.Unavailable);
        Require(unavailableFast.CpuValue == "\u2014"
                && unavailableFast.Ram.Value == "\u2014"
                && unavailableFast.Ram.Detail == "Unavailable",
            "failed CPU and RAM evidence should use the compact unavailable labels");
        Require(unavailableGpu.GpuValue == "\u2014"
                && unavailableGpu.GpuDetail == "Unavailable"
                && unavailableGpu.Vram.Value == "\u2014"
                && unavailableGpu.Vram.Detail == "Unavailable",
            "failed GPU and VRAM evidence should use the compact unavailable labels");
        var staticAdapter = TelemetryPresentation.FromGpu(
            new SystemTelemetryGpuSample(null, null, "Cached adapter", 16));
        Require(staticAdapter.GpuDetail == "Cached adapter" && staticAdapter.GpuValue == "\u2014",
            "cached static adapter evidence should remain visible when dynamic counters are unavailable");
        var unnamedActiveSample = new SystemTelemetryGpuSample(25, 4, null, 16);
        var unnamedActive = TelemetryPresentation.FromGpu(unnamedActiveSample);
        Require(unnamedActive.GpuDetail == "Local GPU" && unnamedActive.VramPercent == 25,
            "valid dynamic evidence should retain the local fallback label and VRAM percentage");

        var history = new BoundedMetricHistory(36);
        for (var value = 0; value < 40; value++)
        {
            Require(history.AddIfChanged(value), "each changed metric should extend its sparkline history");
        }

        Require(!history.AddIfChanged(39), "duplicate metric values should not invalidate the sparkline");
        var snapshot = history.Snapshot();
        Require(history.Count == 36 && snapshot.Length == 36 && snapshot[0] == 4 && snapshot[^1] == 39,
            "sparkline history should remain ordered and bounded without shifting a list on every sample");
        Require(TelemetryGpuCadence.NextDelay(null, unnamedActiveSample) == TelemetryCadenceController.ActiveGpuInterval
                && TelemetryGpuCadence.NextDelay(unnamedActiveSample, unnamedActiveSample) == TelemetryCadenceController.StableGpuInterval,
            "GPU cadence should be responsive to changed evidence and relax when evidence is stable");

        var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
        var mainWindow = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
        var coordinator = ReadWorkspaceFile("src/AIArena.Wpf/Shell/TelemetryWorkflowCoordinator.cs");
        Require(xaml.Contains("TelemetryCpuSparkline", StringComparison.Ordinal)
                && xaml.Contains("AccentBrush=\"{DynamicResource AlphaAccentBrush}\"", StringComparison.Ordinal)
                && xaml.Contains("AccentBrush=\"{DynamicResource DeltaAccentBrush}\"", StringComparison.Ordinal),
            "telemetry sparklines should retain dynamic theme resources");
        Require(mainWindow.Contains("_telemetryWorkflowCoordinator?.RefreshTheme();", StringComparison.Ordinal),
            "runtime theme changes should explicitly refresh telemetry accents even when samples are unchanged");
        Require(!coordinator.Contains("DoubleAnimation", StringComparison.Ordinal)
                && !coordinator.Contains("BeginAnimation", StringComparison.Ordinal),
            "telemetry updates should remain static-rendered and reduced-motion safe");
        Require(SystemTelemetryService.GpuWmiQueryTimeout <= TimeSpan.FromSeconds(2),
            "GPU WMI sampling lost its bounded query timeout");
        var hardwareProbe = ReadWorkspaceFile(
            "src/AIArena.Wpf/Platform/Windows/Telemetry/WindowsHardwareProbeService.cs");
        Require(hardwareProbe.Contains("Timeout = SystemTelemetryService.GpuWmiQueryTimeout", StringComparison.Ordinal),
            "static GPU adapter discovery bypassed the bounded WMI query timeout");

        RunStaTest(() =>
        {
            var vramBar = new Border { Width = 0, Height = 4 };
            var ramBar = new Border { Width = 0, Height = 4 };
            var vramTrack = new Grid { Width = 200, Height = 4, Children = { vramBar } };
            var ramTrack = new Grid { Width = 200, Height = 4, Children = { ramBar } };
            var layout = new StackPanel { Children = { vramTrack, ramTrack } };
            var host = new Window
            {
                Width = 500,
                Height = 300,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = layout
            };
            var workflow = new TelemetryWorkflowCoordinator(
                new TextBlock(),
                new MetricSparklineControl(),
                new TextBlock(),
                new TextBlock(),
                new MetricSparklineControl(),
                new TextBlock(),
                new TextBlock(),
                vramBar,
                new TextBlock(),
                new TextBlock(),
                ramBar,
                () => false,
                _ => Brushes.DodgerBlue,
                new SystemTelemetryService(),
                TimeProvider.System);
            try
            {
                host.Show();
                host.UpdateLayout();
                workflow.DebugApplyGpuSample(new SystemTelemetryGpuSample(25, 4, "Test GPU", 16));
                workflow.DebugApplyFastSample(new SystemTelemetryFastSample(10, 8, 50, 16));
                Require(Math.Abs(vramBar.Width - 50) < 0.01 && Math.Abs(ramBar.Width - 100) < 0.01,
                    "telemetry usage bars did not reflect the initial track width");

                vramTrack.Width = 400;
                ramTrack.Width = 400;
                host.UpdateLayout();
                Require(Math.Abs(vramBar.Width - 100) < 0.01 && Math.Abs(ramBar.Width - 200) < 0.01,
                    "telemetry usage bars stayed stale after a resize with unchanged samples");
            }
            finally
            {
                workflow.Stop();
                host.Close();
            }
        });
    }

    static void GpuAdapterTelemetryCacheReusesStaticEvidence()
    {
        var now = new DateTimeOffset(2026, 8, 14, 15, 0, 0, TimeSpan.Zero);
        var calls = 0;
        var cache = new GpuAdapterProbeCache(
            () =>
            {
                calls++;
                return
                [
                    new WindowsGpuProbe("Test GPU", "test", 8, null, null),
                    new WindowsGpuProbe("test gpu", "test", 8, null, null)
                ];
            },
            () => now);

        var first = cache.Sample();
        now += GpuAdapterProbeCache.EvidenceLifetime - TimeSpan.FromMilliseconds(1);
        var cached = cache.Sample();
        Require(ReferenceEquals(first, cached) && calls == 1,
            "static adapter evidence should reuse the same immutable projection throughout its lifetime");
        Require(first.Name == "Test GPU" && first.TotalVramGb == 16,
            "static adapter projection should deduplicate names while preserving reported capacity evidence");

        now += TimeSpan.FromMilliseconds(1);
        var refreshed = cache.Sample();
        Require(!ReferenceEquals(first, refreshed) && calls == 2,
            "static adapter evidence should refresh once at the explicit cache boundary");

        var failedCalls = 0;
        var failed = new GpuAdapterProbeCache(
            () =>
            {
                failedCalls++;
                throw new InvalidOperationException("adapter unavailable");
            },
            () => now);
        Require(failed.Sample().Name is null && failed.Sample().TotalVramGb is null && failedCalls == 1,
            "failed static detection should also be cached instead of hammering WMI");
    }

    private sealed class MutableTelemetryTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static long MeasureLegacyTelemetryProjectionAllocations()
    {
        var cpuHistory = new List<double>();
        var gpuHistory = new List<double>();
        var sink = 0;
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var sampleIndex = 0; sampleIndex <= 60; sampleIndex++)
        {
            var sample = new SystemTelemetrySample(
                sampleIndex % 100,
                25,
                4,
                8 + (sampleIndex % 4),
                40 + (sampleIndex % 5),
                32,
                "Stable test adapter",
                16);
            cpuHistory.Add(sample.CpuPercent!.Value);
            gpuHistory.Add(sample.GpuPercent!.Value);
            while (cpuHistory.Count > 36)
            {
                cpuHistory.RemoveAt(0);
            }

            while (gpuHistory.Count > 36)
            {
                gpuHistory.RemoveAt(0);
            }

            var cpuValues = cpuHistory.ToArray();
            var gpuValues = gpuHistory.ToArray();
            var cpuText = $"{sample.CpuPercent.Value:0}%";
            var gpuText = $"{sample.GpuPercent.Value:0}%";
            var ramText = $"{sample.RamUsedGb!.Value:0.#} GB";
            var vramText = $"{sample.VramUsedGb!.Value:0.#} GB";
            sink += cpuValues.Length
                    + gpuValues.Length
                    + cpuText.Length
                    + gpuText.Length
                    + ramText.Length
                    + vramText.Length;
        }

        GC.KeepAlive(sink);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
