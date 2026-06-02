using System.Management;
using System.Runtime.InteropServices;

namespace AIArena.Wpf.Services;

public sealed class SystemTelemetryService
{
    private ulong? previousIdle;
    private ulong? previousKernel;
    private ulong? previousUser;
    private GpuAdapterSnapshot? cachedGpuAdapters;
    private DateTime cachedGpuAdaptersAt = DateTime.MinValue;

    public SystemTelemetrySample Sample()
    {
        var cpu = SampleCpuPercent();
        var memory = WindowsHardwareProbeService.SampleMemory();
        var gpu = SampleGpu();
        return new SystemTelemetrySample(
            cpu,
            gpu.Percent,
            gpu.VramUsedGb,
            memory.UsedGb,
            memory.PercentUsed,
            memory.TotalGb,
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

    private GpuSnapshot SampleGpu()
    {
        return SampleNvidiaSmiGpu() ?? SampleWindowsGpuCounters();
    }

    private static GpuSnapshot? SampleNvidiaSmiGpu()
    {
        var gpus = WindowsHardwareProbeService.DetectNvidiaGpus();
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
        var adapters = GetGpuAdapters();
        try
        {
            double utilization = 0;
            using (var engineSearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
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
            using (var memorySearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
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

    private GpuAdapterSnapshot GetGpuAdapters()
    {
        if (cachedGpuAdapters is not null && DateTime.UtcNow - cachedGpuAdaptersAt < TimeSpan.FromMinutes(5))
        {
            return cachedGpuAdapters;
        }

        try
        {
            var gpus = WindowsHardwareProbeService.DetectWindowsGpus();
            var names = gpus.Select(gpu => gpu.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            var totalGb = gpus.Sum(gpu => gpu.VramTotalGb ?? 0);

            cachedGpuAdapters = new GpuAdapterSnapshot(
                FormatGpuName(names),
                totalGb > 0 ? totalGb : null);
            cachedGpuAdaptersAt = DateTime.UtcNow;
            return cachedGpuAdapters;
        }
        catch
        {
            cachedGpuAdapters = new GpuAdapterSnapshot(null, null);
            cachedGpuAdaptersAt = DateTime.UtcNow;
            return cachedGpuAdapters;
        }
    }

    private static string? FormatGpuName(IReadOnlyCollection<string> names)
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
    private sealed record GpuAdapterSnapshot(string? Name, double? TotalVramGb);
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
