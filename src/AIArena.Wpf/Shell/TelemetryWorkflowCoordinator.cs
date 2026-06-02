using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class TelemetryWorkflowCoordinator
{
    private const int HistoryLimit = 36;

    private readonly SystemTelemetryService systemTelemetryService = new();
    private readonly DispatcherTimer telemetryTimer;
    private readonly TextBlock cpuValueText;
    private readonly MetricSparklineControl cpuSparkline;
    private readonly TextBlock gpuValueText;
    private readonly TextBlock gpuDetailText;
    private readonly MetricSparklineControl gpuSparkline;
    private readonly TextBlock vramValueText;
    private readonly TextBlock vramDetailText;
    private readonly FrameworkElement vramUsageBar;
    private readonly TextBlock ramValueText;
    private readonly TextBlock ramDetailText;
    private readonly FrameworkElement ramUsageBar;
    private readonly Func<bool> isTelemetryDisplayed;
    private readonly Func<string, Brush> resourceBrush;
    private readonly List<double> cpuHistory = [];
    private readonly List<double> gpuHistory = [];
    private bool sampleInFlight;

    public TelemetryWorkflowCoordinator(
        TextBlock cpuValueText,
        MetricSparklineControl cpuSparkline,
        TextBlock gpuValueText,
        TextBlock gpuDetailText,
        MetricSparklineControl gpuSparkline,
        TextBlock vramValueText,
        TextBlock vramDetailText,
        FrameworkElement vramUsageBar,
        TextBlock ramValueText,
        TextBlock ramDetailText,
        FrameworkElement ramUsageBar,
        Func<bool> isTelemetryDisplayed,
        Func<string, Brush> resourceBrush)
    {
        this.cpuValueText = cpuValueText;
        this.cpuSparkline = cpuSparkline;
        this.gpuValueText = gpuValueText;
        this.gpuDetailText = gpuDetailText;
        this.gpuSparkline = gpuSparkline;
        this.vramValueText = vramValueText;
        this.vramDetailText = vramDetailText;
        this.vramUsageBar = vramUsageBar;
        this.ramValueText = ramValueText;
        this.ramDetailText = ramDetailText;
        this.ramUsageBar = ramUsageBar;
        this.isTelemetryDisplayed = isTelemetryDisplayed;
        this.resourceBrush = resourceBrush;

        telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        telemetryTimer.Tick += async (_, _) => await UpdateAsync();
    }

    public void UpdateTimerState()
    {
        if (isTelemetryDisplayed())
        {
            if (!telemetryTimer.IsEnabled)
            {
                _ = UpdateAsync();
                telemetryTimer.Start();
            }
        }
        else
        {
            telemetryTimer.Stop();
        }
    }

    public void Stop()
    {
        telemetryTimer.Stop();
    }

    private async Task UpdateAsync()
    {
        if (!isTelemetryDisplayed())
        {
            telemetryTimer.Stop();
            return;
        }

        if (sampleInFlight)
        {
            return;
        }

        sampleInFlight = true;
        SystemTelemetrySample sample;
        try
        {
            sample = await Task.Run(() => systemTelemetryService.Sample());
        }
        finally
        {
            sampleInFlight = false;
        }

        SetTelemetryTile(
            cpuValueText,
            cpuSparkline,
            cpuHistory,
            sample.CpuPercent,
            sample.CpuPercent.HasValue ? $"{sample.CpuPercent.Value:0}%" : "\u2014",
            resourceBrush("AlphaAccentBrush"));
        SetTelemetryTile(
            gpuValueText,
            gpuSparkline,
            gpuHistory,
            sample.GpuPercent,
            sample.GpuPercent.HasValue ? $"{sample.GpuPercent.Value:0}%" : "\u2014",
            resourceBrush("DeltaAccentBrush"));
        gpuDetailText.Text = !string.IsNullOrWhiteSpace(sample.GpuName)
            ? sample.GpuName
            : sample.GpuPercent.HasValue ? "Local GPU" : "Unavailable";
        SetTelemetryUsageBar(
            vramValueText,
            vramDetailText,
            vramUsageBar,
            sample.VramUsedGb,
            sample.VramTotalGb,
            sample.VramUsedGb.HasValue && sample.VramTotalGb is > 0
                ? (sample.VramUsedGb.Value / sample.VramTotalGb.Value) * 100d
                : null);
        SetTelemetryUsageBar(
            ramValueText,
            ramDetailText,
            ramUsageBar,
            sample.RamUsedGb,
            sample.RamTotalGb,
            sample.RamPercent);
    }

    private static void SetTelemetryTile(
        TextBlock valueText,
        MetricSparklineControl sparkline,
        List<double> history,
        double? graphValue,
        string displayValue,
        Brush accent)
    {
        valueText.Text = displayValue;
        valueText.Foreground = accent;
        sparkline.AccentBrush = accent;
        if (graphValue.HasValue)
        {
            history.Add(graphValue.Value);
            while (history.Count > HistoryLimit)
            {
                history.RemoveAt(0);
            }
        }

        sparkline.Values = history.ToArray();
    }

    private static void SetTelemetryUsageBar(
        TextBlock valueText,
        TextBlock detailText,
        FrameworkElement usageBar,
        double? usedGb,
        double? totalGb,
        double? percent)
    {
        if (usedGb.HasValue)
        {
            valueText.Text = $"{usedGb.Value:0.#} GB";
            detailText.Text = totalGb.HasValue ? $"/ {totalGb.Value:0.#} GB" : "";
        }
        else
        {
            valueText.Text = "\u2014";
            detailText.Text = "Unavailable";
        }

        var parentWidth = usageBar.Parent is FrameworkElement parent
            ? parent.ActualWidth
            : 0;
        usageBar.Width = percent.HasValue && parentWidth > 0
            ? parentWidth * Math.Clamp(percent.Value / 100d, 0, 1)
            : 0;
    }
}
