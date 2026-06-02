using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public sealed class ProviderAutoConfigureService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private readonly ModelProviderHealthService providerHealth;

    public ProviderAutoConfigureService(ModelProviderHealthService? providerHealth = null)
    {
        this.providerHealth = providerHealth ?? new ModelProviderHealthService();
    }

    public async Task<ProviderAutoConfigurePlan> DetectAsync(
        string currentProviderBaseUrl,
        string strategy,
        CancellationToken cancellationToken = default)
    {
        var hardware = await Task.Run(DetectHardware, cancellationToken);
        var candidates = CandidateBaseUrls(currentProviderBaseUrl);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await providerHealth.ListModelsAsync(new ModelProviderConfig
            {
                BaseUrl = candidate,
                Timeout = 5,
                Temperature = 0,
                MaxOutputTokens = 16
            }, cancellationToken);

            if (!result.Ok || result.Models.Count == 0)
            {
                continue;
            }

            var lmStudioNative = await SupportsLmStudioNativeApiAsync(result.BaseUrl, cancellationToken);
            return Recommend(
                result.BaseUrl,
                true,
                lmStudioNative,
                result.Models,
                hardware,
                strategy);
        }

        return Recommend(
            candidates[0],
            false,
            false,
            [],
            hardware,
            strategy);
    }

    public static ProviderAutoConfigurePlan Recommend(
        string providerBaseUrl,
        bool providerOnline,
        bool lmStudioNativeApi,
        IEnumerable<string> modelNames,
        HardwareProbe hardware,
        string strategy)
    {
        var selectedStrategy = NormalizeStrategy(strategy, hardware);
        var profiles = modelNames
            .Select(CreateModelProfile)
            .Where(profile => profile.IsChatCandidate)
            .OrderBy(profile => profile.EstimatedFootprintGb ?? double.MaxValue)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var warnings = new List<string>
        {
            "AI Arena can recommend a model spread; LM Studio controls final GPU placement and offload.",
            "Model footprint is estimated from model names when provider metadata is unavailable."
        };

        if (hardware.Gpus.Count == 0)
        {
            warnings.Add("No dedicated GPU was detected. Use a conservative setup or CPU-friendly models.");
        }
        else if (hardware.TotalVramGb is null)
        {
            warnings.Add("GPU VRAM could not be measured precisely. Recommendations use model size estimates.");
        }

        if (!providerOnline)
        {
            warnings.Add("Provider is offline or has no advertised models. Start LM Studio, then run Auto Configure again.");
            return new ProviderAutoConfigurePlan(
                providerBaseUrl,
                false,
                lmStudioNativeApi,
                selectedStrategy,
                hardware,
                [],
                "",
                [],
                PreloadPolicy(hardware, 0, lmStudioNativeApi),
                warnings);
        }

        if (profiles.Length == 0)
        {
            warnings.Add("The provider advertised models, but none looked like chat models.");
            return new ProviderAutoConfigurePlan(
                providerBaseUrl,
                true,
                lmStudioNativeApi,
                selectedStrategy,
                hardware,
                [],
                "",
                [],
                PreloadPolicy(hardware, 0, lmStudioNativeApi),
                warnings);
        }

        var uniqueBudget = UniqueModelBudget(hardware, selectedStrategy);
        var smallest = profiles[0];
        var strongest = StrongestUsableModel(profiles, hardware) ?? profiles[^1];
        var medium = profiles[Math.Clamp(profiles.Length / 2, 0, profiles.Length - 1)];
        var secondSmall = profiles.Length > 1 ? profiles[1] : smallest;
        var assignments = selectedStrategy switch
        {
            "low_vram" or "performance" or "conservative" => SingleModelAssignments(smallest, selectedStrategy),
            "max_variety" or "absurd_lab" => VarietyAssignments(profiles, uniqueBudget),
            _ => BalancedAssignments(strongest, medium, secondSmall, smallest, uniqueBudget)
        };

        var defaultModel = assignments.FirstOrDefault(item => item.Role.Equals("Alpha", StringComparison.OrdinalIgnoreCase))?.Model
            ?? assignments[0].Model;
        var uniqueModels = assignments.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (uniqueModels > uniqueBudget)
        {
            warnings.Add($"Recommended {uniqueModels} unique models, but this hardware looks safer with {uniqueBudget}.");
        }

        if (uniqueModels == 1)
        {
            warnings.Add("Single-model routing is recommended to avoid overloading limited VRAM.");
        }

        return new ProviderAutoConfigurePlan(
            providerBaseUrl,
            true,
            lmStudioNativeApi,
            selectedStrategy,
            hardware,
            profiles,
            defaultModel,
            assignments,
            PreloadPolicy(hardware, uniqueModels, lmStudioNativeApi),
            warnings);
    }

    private static IReadOnlyList<ModelAssignmentRecommendation> SingleModelAssignments(ModelProfile model, string strategy)
    {
        var reason = strategy switch
        {
            "performance" => "smallest advertised chat model for fast, stable turns",
            "low_vram" => "single shared model to protect limited VRAM",
            _ => "conservative single-model route"
        };
        return
        [
            new("Alpha", model.Name, reason),
            new("Beta", model.Name, reason),
            new("Gamma", model.Name, reason),
            new("Delta", model.Name, reason),
            new("Narrator", model.Name, "shared narrator to avoid another loaded model")
        ];
    }

    private static IReadOnlyList<ModelAssignmentRecommendation> BalancedAssignments(
        ModelProfile strongest,
        ModelProfile medium,
        ModelProfile secondSmall,
        ModelProfile smallest,
        int uniqueBudget)
    {
        if (uniqueBudget <= 1)
        {
            return SingleModelAssignments(smallest, "low_vram");
        }

        if (uniqueBudget == 2)
        {
            return
            [
                new("Alpha", strongest.Name, "strongest fitting model for opening pressure"),
                new("Beta", smallest.Name, "smaller counterweight keeps turns responsive"),
                new("Gamma", strongest.Name, "reuse strongest model instead of loading a third"),
                new("Delta", smallest.Name, "reuse smaller model for constraint checks"),
                new("Narrator", smallest.Name, "compact narrator")
            ];
        }

        return
        [
            new("Alpha", strongest.Name, "strongest fitting model for lead reasoning"),
            new("Beta", medium.Name, "middle-weight model for contrast"),
            new("Gamma", strongest.Name, "reuse strongest model for pressure symmetry"),
            new("Delta", secondSmall.Name, "smaller model for boundary testing"),
            new("Narrator", smallest.Name, "smallest model for summaries and observation")
        ];
    }

    private static IReadOnlyList<ModelAssignmentRecommendation> VarietyAssignments(IReadOnlyList<ModelProfile> profiles, int uniqueBudget)
    {
        if (uniqueBudget <= 1)
        {
            return SingleModelAssignments(profiles[0], "low_vram");
        }

        var byStrength = profiles
            .OrderByDescending(profile => profile.EstimatedFootprintGb ?? 0)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = byStrength
            .Take(Math.Min(Math.Max(uniqueBudget, 2), Math.Min(5, profiles.Count)))
            .ToArray();

        ModelProfile Pick(int index)
        {
            return selected[Math.Min(index, selected.Length - 1)];
        }

        return
        [
            new("Alpha", Pick(0).Name, "largest available perspective"),
            new("Beta", Pick(1).Name, "different model family or size when available"),
            new("Gamma", Pick(2).Name, "third model for disagreement pressure"),
            new("Delta", Pick(3).Name, "extra boundary-checking route"),
            new("Narrator", profiles[0].Name, "smallest model keeps narration cheap")
        ];
    }

    private static ModelProfile? StrongestUsableModel(IReadOnlyList<ModelProfile> profiles, HardwareProbe hardware)
    {
        var freeVram = hardware.FreeVramGb;
        if (freeVram is null or <= 0)
        {
            return profiles[^1];
        }

        var target = freeVram.Value * 0.8;
        return profiles
            .Where(profile => (profile.EstimatedFootprintGb ?? 0) <= target)
            .OrderByDescending(profile => profile.EstimatedFootprintGb ?? 0)
            .FirstOrDefault()
            ?? profiles[0];
    }

    private static int UniqueModelBudget(HardwareProbe hardware, string strategy)
    {
        if (strategy.Equals("low_vram", StringComparison.OrdinalIgnoreCase)
            || strategy.Equals("performance", StringComparison.OrdinalIgnoreCase)
            || strategy.Equals("conservative", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var totalVram = hardware.TotalVramGb;
        var gpuCount = hardware.Gpus.Count;
        var budget = totalVram switch
        {
            null => hardware.SystemRamTotalGb >= 48 ? 2 : 1,
            < 10 => 1,
            < 18 => 2,
            < 28 => 3,
            < 44 => 4,
            _ => 5
        };

        if (strategy.Equals("max_variety", StringComparison.OrdinalIgnoreCase)
            || strategy.Equals("absurd_lab", StringComparison.OrdinalIgnoreCase))
        {
            budget += gpuCount > 1 ? 1 : 0;
        }

        return Math.Clamp(budget, 1, 5);
    }

    private static string NormalizeStrategy(string strategy, HardwareProbe hardware)
    {
        var value = string.IsNullOrWhiteSpace(strategy) ? "balanced" : strategy.Trim().ToLowerInvariant();
        if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return value switch
            {
                "conservative" or "balanced" or "performance" or "max_variety" or "low_vram" or "absurd_lab" => value,
                _ => "balanced"
            };
        }

        return hardware.TotalVramGb switch
        {
            null => hardware.SystemRamTotalGb >= 48 ? "conservative" : "low_vram",
            < 10 => "low_vram",
            < 18 => "conservative",
            _ => "balanced"
        };
    }

    private static string PreloadPolicy(HardwareProbe hardware, int uniqueModels, bool lmStudioNativeApi)
    {
        var mode = lmStudioNativeApi ? "LM Studio load API available" : "chat warm-up only";
        if (uniqueModels <= 0)
        {
            return $"{mode}; no models selected yet.";
        }

        if (uniqueModels == 1)
        {
            return $"{mode}; safe to preload the shared model.";
        }

        var totalVram = hardware.TotalVramGb;
        return totalVram switch
        {
            null => $"{mode}; preload cautiously because VRAM is unknown.",
            < 18 => $"{mode}; preload one model at a time on this VRAM budget.",
            < 28 => $"{mode}; preload up to two unique models first.",
            _ => $"{mode}; selected models are likely safe to preload together."
        };
    }

    private static ModelProfile CreateModelProfile(string name)
    {
        var trimmed = name.Trim();
        var lower = trimmed.ToLowerInvariant();
        var isChat = !lower.Contains("embed", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains("rerank", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains("tts", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains("whisper", StringComparison.OrdinalIgnoreCase);
        var parameterB = EstimateParameterBillions(lower);
        var quantization = EstimateQuantization(lower);
        double? footprint = parameterB is null
            ? null
            : Math.Max(0.5, parameterB.Value * QuantizationFactor(quantization) + 0.7);
        var tier = footprint switch
        {
            null => "unknown",
            < 4 => "small",
            < 9 => "medium",
            < 20 => "large",
            _ => "huge"
        };

        return new ModelProfile(trimmed, parameterB, quantization, footprint, tier, isChat);
    }

    private static double? EstimateParameterBillions(string modelName)
    {
        var match = Regex.Match(modelName, @"(?<![a-z0-9])(\d+(?:\.\d+)?)\s*b(?![a-z])", RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (modelName.Contains("nano", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (modelName.Contains("mini", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("small", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (modelName.Contains("medium", StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        return null;
    }

    private static string EstimateQuantization(string modelName)
    {
        if (modelName.Contains("q2", StringComparison.OrdinalIgnoreCase))
        {
            return "Q2";
        }

        if (modelName.Contains("q3", StringComparison.OrdinalIgnoreCase))
        {
            return "Q3";
        }

        if (modelName.Contains("q4", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("gguf", StringComparison.OrdinalIgnoreCase))
        {
            return "Q4/gguf";
        }

        if (modelName.Contains("q5", StringComparison.OrdinalIgnoreCase))
        {
            return "Q5";
        }

        if (modelName.Contains("q8", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("int8", StringComparison.OrdinalIgnoreCase))
        {
            return "Q8";
        }

        if (modelName.Contains("fp16", StringComparison.OrdinalIgnoreCase)
            || modelName.Contains("f16", StringComparison.OrdinalIgnoreCase))
        {
            return "FP16";
        }

        return "estimated";
    }

    private static double QuantizationFactor(string quantization)
    {
        return quantization switch
        {
            "Q2" => 0.35,
            "Q3" => 0.48,
            "Q4/gguf" => 0.62,
            "Q5" => 0.72,
            "Q8" => 1.05,
            "FP16" => 2.05,
            _ => 0.85
        };
    }

    private static IReadOnlyList<string> CandidateBaseUrls(string currentProviderBaseUrl)
    {
        var values = new[]
            {
                currentProviderBaseUrl,
                "http://127.0.0.1:1234/v1",
                "http://localhost:1234/v1"
            }
            .Select(NormalizeProviderBaseUrl)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length > 0 ? values : ["http://127.0.0.1:1234/v1"];
    }

    private static string NormalizeProviderBaseUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:1234/v1"
            : value.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7].TrimEnd('/');
        }

        return ModelProviderHealthService.NormalizeBaseUrl(trimmed);
    }

    private static async Task<bool> SupportsLmStudioNativeApiAsync(string providerBaseUrl, CancellationToken cancellationToken)
    {
        var apiBase = NormalizeLmStudioApiBase(providerBaseUrl);
        try
        {
            using var response = await HttpClient.GetAsync(new Uri(new Uri(apiBase + "/"), "models"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string NormalizeLmStudioApiBase(string providerBaseUrl)
    {
        var trimmed = NormalizeProviderBaseUrl(providerBaseUrl).TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return $"{trimmed}/api/v1";
    }

    private static HardwareProbe DetectHardware()
    {
        var nvidia = DetectNvidiaGpus();
        var wmi = DetectWindowsGpus();
        var merged = new List<GpuDeviceInfo>(nvidia);
        foreach (var gpu in wmi)
        {
            if (!merged.Any(existing => SimilarGpuName(existing.Name, gpu.Name)))
            {
                merged.Add(gpu);
            }
        }

        var memory = DetectSystemMemory();
        return new HardwareProbe(merged, memory.TotalGb, memory.UsedGb);
    }

    private static IReadOnlyList<GpuDeviceInfo> DetectNvidiaGpus()
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

            var gpus = new List<GpuDeviceInfo>();
            foreach (var line in outputTask.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length < 5)
                {
                    continue;
                }

                double? usedGb = ParseDouble(parts[2]) is double usedMb ? usedMb / 1024d : null;
                double? totalGb = ParseDouble(parts[3]) is double totalMb ? totalMb / 1024d : null;
                var utilization = ParseDouble(parts[4]);
                gpus.Add(new GpuDeviceInfo(parts[1], "NVIDIA", totalGb, usedGb, utilization));
            }

            return gpus;
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<GpuDeviceInfo> DetectWindowsGpus()
    {
        try
        {
            var gpus = new List<GpuDeviceInfo>();
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
                gpus.Add(new GpuDeviceInfo(name.Trim(), VendorFromName(name), vramGb, null, null));
            }

            return gpus;
        }
        catch
        {
            return [];
        }
    }

    private static bool SimilarGpuName(string first, string second)
    {
        return first.Contains(second, StringComparison.OrdinalIgnoreCase)
            || second.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    private static string VendorFromName(string name)
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

    private static ProviderAutoMemorySnapshot DetectSystemMemory()
    {
        var status = new ProviderAutoMemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<ProviderAutoMemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new ProviderAutoMemorySnapshot(null, null);
        }

        var totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        var availableGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
        return new ProviderAutoMemorySnapshot(totalGb, Math.Max(0, totalGb - availableGb));
    }

    private static string ResolveNvidiaSmiPath()
    {
        var systemPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "nvidia-smi.exe");
        return File.Exists(systemPath) ? systemPath : "nvidia-smi";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort; auto configure should never leave the UI stuck.
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref ProviderAutoMemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderAutoMemoryStatusEx
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

    private sealed record ProviderAutoMemorySnapshot(double? TotalGb, double? UsedGb);
}

public sealed record ProviderAutoConfigurePlan(
    string ProviderBaseUrl,
    bool ProviderOnline,
    bool LmStudioNativeApi,
    string Strategy,
    HardwareProbe Hardware,
    IReadOnlyList<ModelProfile> Models,
    string DefaultModel,
    IReadOnlyList<ModelAssignmentRecommendation> Assignments,
    string PreloadGuidance,
    IReadOnlyList<string> Warnings);

public sealed record HardwareProbe(
    IReadOnlyList<GpuDeviceInfo> Gpus,
    double? SystemRamTotalGb,
    double? SystemRamUsedGb)
{
    public double? TotalVramGb => Gpus.Count == 0 || Gpus.All(gpu => gpu.VramTotalGb is null)
        ? null
        : Gpus.Sum(gpu => gpu.VramTotalGb ?? 0);

    public double? FreeVramGb => Gpus.Count == 0 || Gpus.All(gpu => gpu.VramTotalGb is null)
        ? null
        : Gpus.Sum(gpu => Math.Max(0, (gpu.VramTotalGb ?? 0) - (gpu.VramUsedGb ?? 0)));
}

public sealed record GpuDeviceInfo(
    string Name,
    string Vendor,
    double? VramTotalGb,
    double? VramUsedGb,
    double? UtilizationPercent);

public sealed record ModelProfile(
    string Name,
    double? ParameterBillions,
    string Quantization,
    double? EstimatedFootprintGb,
    string Tier,
    bool IsChatCandidate);

public sealed record ModelAssignmentRecommendation(string Role, string Model, string Reason);
