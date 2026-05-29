using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;

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
        var memory = SampleMemory();
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

    private static MemorySnapshot SampleMemory()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemorySnapshot(null, null, null);
        }

        var totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        var availableGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
        var usedGb = Math.Max(0, totalGb - availableGb);
        return new MemorySnapshot(usedGb, totalGb <= 0 ? null : (usedGb / totalGb) * 100d, totalGb);
    }

    private GpuSnapshot SampleGpu()
    {
        return SampleNvidiaSmiGpu() ?? SampleWindowsGpuCounters();
    }

    private static GpuSnapshot? SampleNvidiaSmiGpu()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ResolveNvidiaSmiPath(),
                Arguments = "--query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only; telemetry should never disturb the UI.
                }

                return null;
            }

            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var lines = outputTask.GetAwaiter().GetResult()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var names = new List<string>();
            double usedMb = 0;
            double totalMb = 0;
            double utilization = 0;
            foreach (var line in lines)
            {
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length < 4)
                {
                    continue;
                }

                names.Add(parts[0]);
                usedMb += ParseDouble(parts[1]) ?? 0;
                totalMb += ParseDouble(parts[2]) ?? 0;
                utilization += ParseDouble(parts[3]) ?? 0;
            }

            return names.Count == 0
                ? null
                : new GpuSnapshot(
                    Math.Clamp(utilization, 0, 100),
                    usedMb / 1024d,
                    totalMb > 0 ? totalMb / 1024d : null,
                    FormatGpuName(names));
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveNvidiaSmiPath()
    {
        var systemPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "nvidia-smi.exe");
        return File.Exists(systemPath) ? systemPath : "nvidia-smi";
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

                    utilization += ToDouble(engine["UtilizationPercentage"]) ?? 0;
                }
            }

            double dedicatedUsageBytes = 0;
            using (var memorySearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
            {
                foreach (ManagementBaseObject adapterMemory in memorySearcher.Get())
                {
                    dedicatedUsageBytes += ToDouble(adapterMemory["DedicatedUsage"]) ?? 0;
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
            var names = new List<string>();
            double totalBytes = 0;
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementBaseObject adapter in searcher.Get())
            {
                var name = adapter["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }

                totalBytes += ToDouble(adapter["AdapterRAM"]) ?? 0;
            }

            cachedGpuAdapters = new GpuAdapterSnapshot(
                FormatGpuName(names),
                totalBytes > 0 ? totalBytes / 1024d / 1024d / 1024d : null);
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

    private static double? ToDouble(object? value)
    {
        return value is null
            ? null
            : ParseDouble(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static ulong ToUInt64(FileTime fileTime)
    {
        return ((ulong)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private sealed record MemorySnapshot(double? UsedGb, double? PercentUsed, double? TotalGb);
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
