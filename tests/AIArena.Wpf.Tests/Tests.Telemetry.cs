using AIArena.Wpf.Services;

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
}
