using System.Management;
using System.Runtime.InteropServices;

namespace AIArena.Wpf.Services;

public sealed class SystemTelemetryService
{
    internal static readonly TimeSpan GpuWmiQueryTimeout = TimeSpan.FromSeconds(2);
    private readonly NvidiaGpuProbeCache nvidiaGpuProbeCache;
    private readonly GpuAdapterProbeCache gpuAdapterProbeCache;
    private ulong? previousIdle;
    private ulong? previousKernel;
    private ulong? previousUser;

    public SystemTelemetryService()
        : this(new NvidiaGpuProbeCache(
            WindowsHardwareProbeService.DetectNvidiaGpus,
            static () => DateTimeOffset.UtcNow),
            new GpuAdapterProbeCache(
                WindowsHardwareProbeService.DetectWindowsGpus,
                static () => DateTimeOffset.UtcNow))
    {
    }

    internal SystemTelemetryService(NvidiaGpuProbeCache nvidiaGpuProbeCache)
        : this(
            nvidiaGpuProbeCache,
            new GpuAdapterProbeCache(
                WindowsHardwareProbeService.DetectWindowsGpus,
                static () => DateTimeOffset.UtcNow))
    {
    }

    internal SystemTelemetryService(
        NvidiaGpuProbeCache nvidiaGpuProbeCache,
        GpuAdapterProbeCache gpuAdapterProbeCache)
    {
        this.nvidiaGpuProbeCache = nvidiaGpuProbeCache;
        this.gpuAdapterProbeCache = gpuAdapterProbeCache;
    }

    public SystemTelemetrySample Sample()
    {
        var fast = SampleFast();
        var gpu = SampleGpu();
        return new SystemTelemetrySample(
            fast.CpuPercent,
            gpu.GpuPercent,
            gpu.VramUsedGb,
            fast.RamUsedGb,
            fast.RamPercent,
            fast.RamTotalGb,
            gpu.GpuName,
            gpu.VramTotalGb);
    }

    public SystemTelemetryFastSample SampleFast()
    {
        var memory = WindowsHardwareProbeService.SampleMemory();
        return new SystemTelemetryFastSample(
            SampleCpuPercent(),
            memory.UsedGb,
            memory.PercentUsed,
            memory.TotalGb);
    }

    public SystemTelemetryGpuSample SampleGpu()
    {
        var gpu = SampleNvidiaSmiGpu() ?? SampleWindowsGpuCounters();
        return new SystemTelemetryGpuSample(
            gpu.Percent,
            gpu.VramUsedGb,
            gpu.Name,
            gpu.VramTotalGb);
    }

    private double? SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        if (previousIdle is null || previousKernel is null || previousUser is null)
        {
            previousIdle = idle;
            previousKernel = kernel;
            previousUser = user;
            return null;
        }

        var idleDelta = idle - previousIdle.Value;
        var kernelDelta = kernel - previousKernel.Value;
        var userDelta = user - previousUser.Value;
        previousIdle = idle;
        previousKernel = kernel;
        previousUser = user;

        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return null;
        }

        return Math.Clamp((1d - (idleDelta / (double)total)) * 100d, 0, 100);
    }

    private GpuSnapshot? SampleNvidiaSmiGpu()
    {
        var gpus = nvidiaGpuProbeCache.Sample();
        if (gpus.Count == 0)
        {
            return null;
        }

        return new GpuSnapshot(
            Math.Clamp(gpus.Sum(gpu => gpu.UtilizationPercent ?? 0), 0, 100),
            gpus.Sum(gpu => gpu.VramUsedGb ?? 0),
            gpus.Any(gpu => gpu.VramTotalGb is > 0) ? gpus.Sum(gpu => gpu.VramTotalGb ?? 0) : null,
            FormatGpuName(gpus.Select(gpu => gpu.Name).ToArray()));
    }

    private GpuSnapshot SampleWindowsGpuCounters()
    {
        var adapters = gpuAdapterProbeCache.Sample();
        try
        {
            double utilization = 0;
            using (var engineSearcher = CreateGpuCounterSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                foreach (ManagementBaseObject engine in engineSearcher.Get())
                {
                    var name = engine["Name"]?.ToString() ?? "";
                    if (name.Contains("engtype_Security", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    utilization += WindowsHardwareProbeService.ToDouble(engine["UtilizationPercentage"]) ?? 0;
                }
            }

            double dedicatedUsageBytes = 0;
            using (var memorySearcher = CreateGpuCounterSearcher(
                "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
            {
                foreach (ManagementBaseObject adapterMemory in memorySearcher.Get())
                {
                    dedicatedUsageBytes += WindowsHardwareProbeService.ToDouble(adapterMemory["DedicatedUsage"]) ?? 0;
                }
            }

            return new GpuSnapshot(
                Math.Clamp(utilization, 0, 100),
                dedicatedUsageBytes > 0 ? dedicatedUsageBytes / 1024d / 1024d / 1024d : null,
                adapters.TotalVramGb,
                adapters.Name);
        }
        catch
        {
            return new GpuSnapshot(null, null, adapters.TotalVramGb, adapters.Name);
        }
    }

    private static ManagementObjectSearcher CreateGpuCounterSearcher(string query)
    {
        return new ManagementObjectSearcher(
            "root\\CIMV2",
            query,
            new EnumerationOptions
            {
                ReturnImmediately = false,
                Timeout = GpuWmiQueryTimeout
            });
    }

    internal static string? FormatGpuName(IReadOnlyCollection<string> names)
    {
        var uniqueNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return uniqueNames.Length switch
        {
            0 => null,
            1 => uniqueNames[0],
            _ => $"{uniqueNames.Length} GPUs"
        };
    }

    private static ulong ToUInt64(FileTime fileTime)
    {
        return ((ulong)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private sealed record GpuSnapshot(double? Percent, double? VramUsedGb, double? VramTotalGb, string? Name);
}

public sealed record SystemTelemetryFastSample(
    double? CpuPercent,
    double? RamUsedGb,
    double? RamPercent,
    double? RamTotalGb)
{
    public static SystemTelemetryFastSample Unavailable { get; } = new(null, null, null, null);
}

public sealed record SystemTelemetryGpuSample(
    double? GpuPercent,
    double? VramUsedGb,
    string? GpuName,
    double? VramTotalGb)
{
    public static SystemTelemetryGpuSample Unavailable { get; } = new(null, null, null, null);
}

public sealed record SystemTelemetrySample(
    double? CpuPercent,
    double? GpuPercent,
    double? VramUsedGb,
    double? RamUsedGb,
    double? RamPercent,
    double? RamTotalGb,
    string? GpuName,
    double? VramTotalGb);

internal sealed class NvidiaGpuProbeCache
{
    internal static readonly TimeSpan SuccessfulSampleLifetime = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FailedProbeRetryDelay = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<WindowsGpuProbe>> probe;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly object gate = new();
    private IReadOnlyList<WindowsGpuProbe>? cachedSample;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;
    private DateTimeOffset retryAt = DateTimeOffset.MinValue;

    public NvidiaGpuProbeCache(
        Func<IReadOnlyList<WindowsGpuProbe>> probe,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(utcNow);
        this.probe = probe;
        this.utcNow = utcNow;
    }

    public IReadOnlyList<WindowsGpuProbe> Sample()
    {
        lock (gate)
        {
            var now = utcNow();
            if (cachedSample is not null && now - cachedAt < SuccessfulSampleLifetime)
            {
                return cachedSample;
            }

            if (now < retryAt)
            {
                return Array.Empty<WindowsGpuProbe>();
            }

            IReadOnlyList<WindowsGpuProbe> detected;
            try
            {
                detected = probe();
            }
            catch
            {
                detected = Array.Empty<WindowsGpuProbe>();
            }

            if (detected.Count == 0)
            {
                cachedSample = null;
                retryAt = now + FailedProbeRetryDelay;
                return Array.Empty<WindowsGpuProbe>();
            }

            cachedSample = detected.ToArray();
            cachedAt = now;
            retryAt = DateTimeOffset.MinValue;
            return cachedSample;
        }
    }
}

internal sealed class GpuAdapterProbeCache
{
    internal static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<IReadOnlyList<WindowsGpuProbe>> probe;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly object gate = new();
    private GpuAdapterSnapshot? cachedEvidence;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;

    public GpuAdapterProbeCache(
        Func<IReadOnlyList<WindowsGpuProbe>> probe,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(utcNow);
        this.probe = probe;
        this.utcNow = utcNow;
    }

    public GpuAdapterSnapshot Sample()
    {
        lock (gate)
        {
            var now = utcNow();
            if (cachedEvidence is not null && now - cachedAt < EvidenceLifetime)
            {
                return cachedEvidence;
            }

            IReadOnlyList<WindowsGpuProbe> adapters;
            try
            {
                adapters = probe();
            }
            catch
            {
                adapters = Array.Empty<WindowsGpuProbe>();
            }

            var names = adapters
                .Select(adapter => adapter.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var totalGb = adapters.Sum(adapter => adapter.VramTotalGb ?? 0);
            cachedEvidence = new GpuAdapterSnapshot(
                SystemTelemetryService.FormatGpuName(names),
                totalGb > 0 ? totalGb : null);
            cachedAt = now;
            return cachedEvidence;
        }
    }
}

internal sealed record GpuAdapterSnapshot(string? Name, double? TotalVramGb);
