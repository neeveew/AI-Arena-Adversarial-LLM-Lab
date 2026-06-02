using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace AIArena.Wpf.Services;

public static class WindowsHardwareProbeService
{
    public static WindowsMemorySnapshot SampleMemory()
    {
        var status = new WindowsMemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<WindowsMemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new WindowsMemorySnapshot(null, null);
        }

        var totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        var availableGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
        return new WindowsMemorySnapshot(totalGb, Math.Max(0, totalGb - availableGb));
    }

    public static IReadOnlyList<WindowsGpuProbe> DetectNvidiaGpus()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ResolveNvidiaSmiPath(),
                Arguments = "--query-gpu=index,name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return [];
            }

            if (process.ExitCode != 0)
            {
                return [];
            }

            var gpus = new List<WindowsGpuProbe>();
            foreach (var line in outputTask.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length < 5)
                {
                    continue;
                }

                double? usedGb = ParseDouble(parts[2]) is double usedMb ? usedMb / 1024d : null;
                double? totalGb = ParseDouble(parts[3]) is double totalMb ? totalMb / 1024d : null;
                gpus.Add(new WindowsGpuProbe(parts[1], "NVIDIA", totalGb, usedGb, ParseDouble(parts[4])));
            }

            return gpus;
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<WindowsGpuProbe> DetectWindowsGpus()
    {
        try
        {
            var gpus = new List<WindowsGpuProbe>();
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementBaseObject adapter in searcher.Get())
            {
                var name = adapter["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                double? vramGb = ToDouble(adapter["AdapterRAM"]) is double bytes && bytes > 0
                    ? bytes / 1024d / 1024d / 1024d
                    : null;
                gpus.Add(new WindowsGpuProbe(name.Trim(), VendorFromName(name), vramGb, null, null));
            }

            return gpus;
        }
        catch
        {
            return [];
        }
    }

    public static string ResolveNvidiaSmiPath()
    {
        var systemPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "nvidia-smi.exe");
        return File.Exists(systemPath) ? systemPath : "nvidia-smi";
    }

    public static double? ToDouble(object? value)
    {
        return value is null
            ? null
            : ParseDouble(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    public static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    public static string VendorFromName(string name)
    {
        if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("geforce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("rtx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gtx", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (name.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (name.Contains("intel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("arc", StringComparison.OrdinalIgnoreCase)
            || name.Contains("iris", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return "unknown";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only; hardware probing should never disturb the UI.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref WindowsMemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsMemoryStatusEx
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
}

public sealed record WindowsMemorySnapshot(double? TotalGb, double? UsedGb)
{
    public double? PercentUsed => TotalGb is > 0 && UsedGb is double used ? used / TotalGb.Value * 100d : null;
}

public sealed record WindowsGpuProbe(
    string Name,
    string Vendor,
    double? VramTotalGb,
    double? VramUsedGb,
    double? UtilizationPercent);
