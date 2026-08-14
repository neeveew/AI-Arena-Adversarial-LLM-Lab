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

    private readonly SystemTelemetryService systemTelemetryService;
    private readonly TelemetryCadenceController cadence;
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
    private readonly FrameworkElement? vramUsageTrack;
    private readonly FrameworkElement? ramUsageTrack;
    private readonly Func<bool> isTelemetryDisplayed;
    private readonly Func<string, Brush> resourceBrush;
    private readonly BoundedMetricHistory cpuHistory = new(HistoryLimit);
    private readonly BoundedMetricHistory gpuHistory = new(HistoryLimit);
    private double? lastVramPercent;
    private double? lastRamPercent;
    private bool usageTrackHandlersAttached;

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
        : this(
            cpuValueText,
            cpuSparkline,
            gpuValueText,
            gpuDetailText,
            gpuSparkline,
            vramValueText,
            vramDetailText,
            vramUsageBar,
            ramValueText,
            ramDetailText,
            ramUsageBar,
            isTelemetryDisplayed,
            resourceBrush,
            new SystemTelemetryService(),
            TimeProvider.System)
    {
    }

    internal TelemetryWorkflowCoordinator(
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
        Func<string, Brush> resourceBrush,
        SystemTelemetryService systemTelemetryService,
        TimeProvider timeProvider)
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
        this.systemTelemetryService = systemTelemetryService;
        vramUsageTrack = vramUsageBar.Parent as FrameworkElement;
        ramUsageTrack = ramUsageBar.Parent as FrameworkElement;
        if (vramUsageTrack is not null)
        {
            vramUsageTrack.SizeChanged += OnUsageTrackSizeChanged;
            usageTrackHandlersAttached = true;
        }
        if (ramUsageTrack is not null && !ReferenceEquals(ramUsageTrack, vramUsageTrack))
        {
            ramUsageTrack.SizeChanged += OnUsageTrackSizeChanged;
            usageTrackHandlersAttached = true;
        }

        telemetryTimer = new DispatcherTimer(DispatcherPriority.Background, cpuValueText.Dispatcher)
        {
            Interval = TelemetryCadenceController.FastInterval
        };
        telemetryTimer.Tick += OnTelemetryTick;

        cadence = new TelemetryCadenceController(
            timeProvider,
            cancellationToken => Task.Run(systemTelemetryService.SampleFast, cancellationToken),
            cancellationToken => Task.Run(systemTelemetryService.SampleGpu, cancellationToken),
            (sample, cancellationToken) => PublishOnDispatcherAsync(
                () => ApplyFastSample(sample),
                cancellationToken),
            (sample, cancellationToken) => PublishOnDispatcherAsync(
                () => ApplyGpuSample(sample),
                cancellationToken));
    }

    internal TelemetryCadenceReceipt DebugReceipt => cadence.Receipt;

    internal void DebugApplyFastSample(SystemTelemetryFastSample sample) => ApplyFastSample(sample);

    internal void DebugApplyGpuSample(SystemTelemetryGpuSample sample) => ApplyGpuSample(sample);

    public void UpdateTimerState()
    {
        var visible = isTelemetryDisplayed();
        var changed = cadence.SetVisible(visible);
        if (!visible)
        {
            telemetryTimer.Stop();
            return;
        }

        if (!telemetryTimer.IsEnabled)
        {
            telemetryTimer.Start();
        }

        if (changed)
        {
            Observe(cadence.PulseAsync());
        }
    }

    public void Stop()
    {
        telemetryTimer.Stop();
        cadence.SetVisible(false);
        if (usageTrackHandlersAttached)
        {
            if (vramUsageTrack is not null)
            {
                vramUsageTrack.SizeChanged -= OnUsageTrackSizeChanged;
            }
            if (ramUsageTrack is not null && !ReferenceEquals(ramUsageTrack, vramUsageTrack))
            {
                ramUsageTrack.SizeChanged -= OnUsageTrackSizeChanged;
            }
            usageTrackHandlersAttached = false;
        }
    }

    internal void RefreshTheme()
    {
        var cpuAccent = resourceBrush("AlphaAccentBrush");
        var gpuAccent = resourceBrush("DeltaAccentBrush");
        SetAccent(cpuValueText, cpuSparkline, cpuAccent);
        SetAccent(gpuValueText, gpuSparkline, gpuAccent);
    }

    internal Task RefreshAsync()
    {
        cadence.Refresh();
        return cadence.PulseAsync();
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        Observe(cadence.PulseAsync());
    }

    private Task PublishOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return cpuValueText.Dispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
            },
            DispatcherPriority.Background,
            cancellationToken).Task;
    }

    private void ApplyFastSample(SystemTelemetryFastSample sample)
    {
        var presentation = TelemetryPresentation.FromFast(sample);
        SetTelemetryTile(
            cpuValueText,
            cpuSparkline,
            cpuHistory,
            sample.CpuPercent,
            presentation.CpuValue,
            resourceBrush("AlphaAccentBrush"));
        lastRamPercent = sample.RamPercent;
        SetTelemetryUsageBar(
            ramValueText,
            ramDetailText,
            ramUsageBar,
            presentation.Ram,
            sample.RamPercent);
    }

    private void ApplyGpuSample(SystemTelemetryGpuSample sample)
    {
        var presentation = TelemetryPresentation.FromGpu(sample);
        SetTelemetryTile(
            gpuValueText,
            gpuSparkline,
            gpuHistory,
            sample.GpuPercent,
            presentation.GpuValue,
            resourceBrush("DeltaAccentBrush"));
        SetText(gpuDetailText, presentation.GpuDetail);
        lastVramPercent = presentation.VramPercent;
        SetTelemetryUsageBar(
            vramValueText,
            vramDetailText,
            vramUsageBar,
            presentation.Vram,
            presentation.VramPercent);
    }

    private static void SetTelemetryTile(
        TextBlock valueText,
        MetricSparklineControl sparkline,
        BoundedMetricHistory history,
        double? graphValue,
        string displayValue,
        Brush accent)
    {
        SetText(valueText, displayValue);
        SetAccent(valueText, sparkline, accent);
        if (graphValue.HasValue && history.AddIfChanged(graphValue.Value))
        {
            sparkline.Values = history.Snapshot();
        }
    }

    private static void SetAccent(TextBlock valueText, MetricSparklineControl sparkline, Brush accent)
    {
        if (!ReferenceEquals(valueText.Foreground, accent))
        {
            valueText.Foreground = accent;
        }

        if (!ReferenceEquals(sparkline.AccentBrush, accent))
        {
            sparkline.AccentBrush = accent;
        }
    }

    private static void SetTelemetryUsageBar(
        TextBlock valueText,
        TextBlock detailText,
        FrameworkElement usageBar,
        TelemetryUsagePresentation presentation,
        double? percent)
    {
        SetText(valueText, presentation.Value);
        SetText(detailText, presentation.Detail);

        SetUsageBarWidth(usageBar, percent);
    }

    private static void SetUsageBarWidth(FrameworkElement usageBar, double? percent)
    {
        var parentWidth = usageBar.Parent is FrameworkElement parent
            ? parent.ActualWidth
            : 0;
        var width = percent.HasValue && parentWidth > 0
            ? parentWidth * Math.Clamp(percent.Value / 100d, 0, 1)
            : 0;
        if (!AreClose(usageBar.Width, width))
        {
            usageBar.Width = width;
        }
    }

    private void OnUsageTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetUsageBarWidth(vramUsageBar, lastVramPercent);
        SetUsageBarWidth(ramUsageBar, lastRamPercent);
    }

    private static void SetText(TextBlock textBlock, string value)
    {
        if (!string.Equals(textBlock.Text, value, StringComparison.Ordinal))
        {
            textBlock.Text = value;
        }
    }

    private static bool AreClose(double left, double right)
    {
        return left.Equals(right) || Math.Abs(left - right) < 0.01;
    }

    private static async void Observe(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Sampling and publishing failures are represented in the telemetry state.
        }
    }
}

internal sealed class TelemetryCadenceController
{
    internal static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ActiveGpuInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan StableGpuInterval = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private readonly Func<CancellationToken, Task<SystemTelemetryFastSample>> sampleFastAsync;
    private readonly Func<CancellationToken, Task<SystemTelemetryGpuSample>> sampleGpuAsync;
    private readonly Func<SystemTelemetryFastSample, CancellationToken, Task> publishFastAsync;
    private readonly Func<SystemTelemetryGpuSample, CancellationToken, Task> publishGpuAsync;
    private readonly HashSet<CancellationTokenSource> retiredSources = [];
    private readonly Dictionary<CancellationTokenSource, int> inFlightBySource = [];
    private CancellationTokenSource lifecycleSource = new();
    private DateTimeOffset nextFastAt = DateTimeOffset.MaxValue;
    private DateTimeOffset nextGpuAt = DateTimeOffset.MaxValue;
    private SystemTelemetryFastSample? lastPublishedFast;
    private SystemTelemetryGpuSample? lastPublishedGpu;
    private SystemTelemetryGpuSample? lastObservedGpu;
    private long generation;
    private bool visible;
    private int fastInFlight;
    private int gpuInFlight;
    private int fastSamples;
    private int gpuSamples;
    private int fastPublishes;
    private int gpuPublishes;
    private int fastOverlapSuppressions;
    private int gpuOverlapSuppressions;
    private int actualOverlapCount;
    private int maxFastConcurrency;
    private int maxGpuConcurrency;
    private int cancellationCount;
    private int failureCount;

    public TelemetryCadenceController(
        TimeProvider timeProvider,
        Func<CancellationToken, Task<SystemTelemetryFastSample>> sampleFastAsync,
        Func<CancellationToken, Task<SystemTelemetryGpuSample>> sampleGpuAsync,
        Func<SystemTelemetryFastSample, CancellationToken, Task> publishFastAsync,
        Func<SystemTelemetryGpuSample, CancellationToken, Task> publishGpuAsync)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.sampleFastAsync = sampleFastAsync ?? throw new ArgumentNullException(nameof(sampleFastAsync));
        this.sampleGpuAsync = sampleGpuAsync ?? throw new ArgumentNullException(nameof(sampleGpuAsync));
        this.publishFastAsync = publishFastAsync ?? throw new ArgumentNullException(nameof(publishFastAsync));
        this.publishGpuAsync = publishGpuAsync ?? throw new ArgumentNullException(nameof(publishGpuAsync));
    }

    public TelemetryCadenceReceipt Receipt
    {
        get
        {
            lock (gate)
            {
                return new TelemetryCadenceReceipt(
                    fastSamples,
                    gpuSamples,
                    fastPublishes,
                    gpuPublishes,
                    fastOverlapSuppressions,
                    gpuOverlapSuppressions,
                    actualOverlapCount,
                    maxFastConcurrency,
                    maxGpuConcurrency,
                    cancellationCount,
                    failureCount);
            }
        }
    }

    internal int RetiredLifecycleCount
    {
        get
        {
            lock (gate)
            {
                return retiredSources.Count;
            }
        }
    }

    public bool SetVisible(bool value)
    {
        lock (gate)
        {
            if (visible == value)
            {
                return false;
            }

            visible = value;
            ReplaceLifecycleLocked();
            lastPublishedFast = null;
            lastPublishedGpu = null;
            lastObservedGpu = null;
            if (visible)
            {
                var now = timeProvider.GetUtcNow();
                nextFastAt = now;
                nextGpuAt = now;
            }
            else
            {
                nextFastAt = DateTimeOffset.MaxValue;
                nextGpuAt = DateTimeOffset.MaxValue;
            }

            return true;
        }
    }

    public void Refresh()
    {
        lock (gate)
        {
            if (!visible)
            {
                return;
            }

            ReplaceLifecycleLocked();
            lastPublishedFast = null;
            lastPublishedGpu = null;
            lastObservedGpu = null;
            var now = timeProvider.GetUtcNow();
            nextFastAt = now;
            nextGpuAt = now;
        }
    }

    public Task PulseAsync()
    {
        var startFast = false;
        var startGpu = false;
        long currentGeneration;
        CancellationTokenSource currentSource;
        CancellationToken cancellationToken;

        lock (gate)
        {
            if (!visible)
            {
                return Task.CompletedTask;
            }

            var now = timeProvider.GetUtcNow();
            currentGeneration = generation;
            currentSource = lifecycleSource;
            cancellationToken = currentSource.Token;
            if (now >= nextFastAt)
            {
                if (fastInFlight == 0)
                {
                    fastInFlight++;
                    maxFastConcurrency = Math.Max(maxFastConcurrency, fastInFlight);
                    actualOverlapCount += fastInFlight > 1 ? 1 : 0;
                    nextFastAt = now + FastInterval;
                    startFast = true;
                    TrackWorkLocked(currentSource);
                }
                else
                {
                    fastOverlapSuppressions++;
                }
            }

            if (now >= nextGpuAt)
            {
                if (gpuInFlight == 0)
                {
                    gpuInFlight++;
                    maxGpuConcurrency = Math.Max(maxGpuConcurrency, gpuInFlight);
                    actualOverlapCount += gpuInFlight > 1 ? 1 : 0;
                    startGpu = true;
                    TrackWorkLocked(currentSource);
                }
                else
                {
                    gpuOverlapSuppressions++;
                }
            }
        }

        var fastTask = startFast
            ? RunFastAsync(currentGeneration, currentSource, cancellationToken)
            : null;
        var gpuTask = startGpu
            ? RunGpuAsync(currentGeneration, currentSource, cancellationToken)
            : null;
        return (fastTask, gpuTask) switch
        {
            (not null, not null) => Task.WhenAll(fastTask, gpuTask),
            (not null, null) => fastTask,
            (null, not null) => gpuTask,
            _ => Task.CompletedTask
        };
    }

    private async Task RunFastAsync(
        long requestGeneration,
        CancellationTokenSource lifecycle,
        CancellationToken cancellationToken)
    {
        try
        {
            SystemTelemetryFastSample sample;
            try
            {
                sample = await sampleFastAsync(cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    fastSamples++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    cancellationCount++;
                }

                return;
            }
            catch
            {
                sample = SystemTelemetryFastSample.Unavailable;
                lock (gate)
                {
                    fastSamples++;
                    failureCount++;
                }
            }

            var shouldPublish = false;
            lock (gate)
            {
                if (CanPublishLocked(requestGeneration, cancellationToken)
                    && !Equals(lastPublishedFast, sample))
                {
                    shouldPublish = true;
                }
            }

            if (shouldPublish)
            {
                await PublishFastSafelyAsync(sample, requestGeneration, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CompleteWork(lifecycle, fast: true);
        }
    }

    private async Task RunGpuAsync(
        long requestGeneration,
        CancellationTokenSource lifecycle,
        CancellationToken cancellationToken)
    {
        try
        {
            SystemTelemetryGpuSample sample;
            try
            {
                sample = await sampleGpuAsync(cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    gpuSamples++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    cancellationCount++;
                }

                return;
            }
            catch
            {
                sample = SystemTelemetryGpuSample.Unavailable;
                lock (gate)
                {
                    gpuSamples++;
                    failureCount++;
                }
            }

            var shouldPublish = false;
            lock (gate)
            {
                if (CanPublishLocked(requestGeneration, cancellationToken))
                {
                    var delay = TelemetryGpuCadence.NextDelay(lastObservedGpu, sample);
                    lastObservedGpu = sample;
                    nextGpuAt = timeProvider.GetUtcNow() + delay;
                    if (!Equals(lastPublishedGpu, sample))
                    {
                        shouldPublish = true;
                    }
                }
            }

            if (shouldPublish)
            {
                await PublishGpuSafelyAsync(sample, requestGeneration, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CompleteWork(lifecycle, fast: false);
        }
    }

    private async Task PublishFastSafelyAsync(
        SystemTelemetryFastSample sample,
        long requestGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await publishFastAsync(sample, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (CanPublishLocked(requestGeneration, cancellationToken))
                {
                    lastPublishedFast = sample;
                    fastPublishes++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                cancellationCount++;
            }
        }
        catch
        {
            lock (gate)
            {
                failureCount++;
            }
        }
    }

    private async Task PublishGpuSafelyAsync(
        SystemTelemetryGpuSample sample,
        long requestGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await publishGpuAsync(sample, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (CanPublishLocked(requestGeneration, cancellationToken))
                {
                    lastPublishedGpu = sample;
                    gpuPublishes++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                cancellationCount++;
            }
        }
        catch
        {
            lock (gate)
            {
                failureCount++;
            }
        }
    }

    private bool CanPublishLocked(long requestGeneration, CancellationToken cancellationToken)
    {
        return visible
            && generation == requestGeneration
            && !cancellationToken.IsCancellationRequested;
    }

    private void TrackWorkLocked(CancellationTokenSource source)
    {
        inFlightBySource.TryGetValue(source, out var count);
        inFlightBySource[source] = count + 1;
    }

    private void CompleteWork(CancellationTokenSource source, bool fast)
    {
        lock (gate)
        {
            if (fast)
            {
                fastInFlight--;
            }
            else
            {
                gpuInFlight--;
            }

            if (inFlightBySource.TryGetValue(source, out var sourceCount))
            {
                if (sourceCount <= 1)
                {
                    inFlightBySource.Remove(source);
                    if (retiredSources.Remove(source))
                    {
                        source.Dispose();
                    }
                }
                else
                {
                    inFlightBySource[source] = sourceCount - 1;
                }
            }
        }
    }

    private void ReplaceLifecycleLocked()
    {
        generation++;
        var retired = lifecycleSource;
        lifecycleSource = new CancellationTokenSource();
        retired.Cancel();
        if (!inFlightBySource.ContainsKey(retired))
        {
            retired.Dispose();
        }
        else
        {
            retiredSources.Add(retired);
        }
    }
}

internal static class TelemetryGpuCadence
{
    public static TimeSpan NextDelay(
        SystemTelemetryGpuSample? previous,
        SystemTelemetryGpuSample current)
    {
        if (Equals(current, SystemTelemetryGpuSample.Unavailable)
            || Equals(previous, current))
        {
            return TelemetryCadenceController.StableGpuInterval;
        }

        return TelemetryCadenceController.ActiveGpuInterval;
    }
}

internal sealed class BoundedMetricHistory
{
    private readonly double[] values;
    private int start;
    private int count;

    public BoundedMetricHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        values = new double[capacity];
    }

    public int Count => count;

    public bool AddIfChanged(double value)
    {
        if (count > 0 && values[(start + count - 1) % values.Length].Equals(value))
        {
            return false;
        }

        if (count < values.Length)
        {
            values[(start + count) % values.Length] = value;
            count++;
        }
        else
        {
            values[start] = value;
            start = (start + 1) % values.Length;
        }

        return true;
    }

    public double[] Snapshot()
    {
        var snapshot = new double[count];
        for (var index = 0; index < count; index++)
        {
            snapshot[index] = values[(start + index) % values.Length];
        }

        return snapshot;
    }
}

internal static class TelemetryPresentation
{
    public static TelemetryFastPresentation FromFast(SystemTelemetryFastSample sample)
    {
        return new TelemetryFastPresentation(
            sample.CpuPercent.HasValue ? $"{sample.CpuPercent.Value:0}%" : "\u2014",
            Usage(sample.RamUsedGb, sample.RamTotalGb));
    }

    public static TelemetryGpuPresentation FromGpu(SystemTelemetryGpuSample sample)
    {
        var detail = !string.IsNullOrWhiteSpace(sample.GpuName)
            ? sample.GpuName
            : sample.GpuPercent.HasValue ? "Local GPU" : "Unavailable";
        var percent = sample.VramUsedGb.HasValue && sample.VramTotalGb is > 0
            ? (double?)(sample.VramUsedGb.Value / sample.VramTotalGb.Value * 100d)
            : null;
        return new TelemetryGpuPresentation(
            sample.GpuPercent.HasValue ? $"{sample.GpuPercent.Value:0}%" : "\u2014",
            detail,
            Usage(sample.VramUsedGb, sample.VramTotalGb),
            percent);
    }

    public static TelemetryUsagePresentation Usage(double? usedGb, double? totalGb)
    {
        return usedGb.HasValue
            ? new TelemetryUsagePresentation(
                $"{usedGb.Value:0.#} GB",
                totalGb.HasValue ? $"/ {totalGb.Value:0.#} GB" : "")
            : new TelemetryUsagePresentation("\u2014", "Unavailable");
    }
}

internal readonly record struct TelemetryFastPresentation(
    string CpuValue,
    TelemetryUsagePresentation Ram);

internal readonly record struct TelemetryGpuPresentation(
    string GpuValue,
    string GpuDetail,
    TelemetryUsagePresentation Vram,
    double? VramPercent);

internal readonly record struct TelemetryUsagePresentation(string Value, string Detail);

internal readonly record struct TelemetryCadenceReceipt(
    int FastSamples,
    int GpuSamples,
    int FastPublishes,
    int GpuPublishes,
    int FastOverlapSuppressions,
    int GpuOverlapSuppressions,
    int ActualOverlapCount,
    int MaxFastConcurrency,
    int MaxGpuConcurrency,
    int Cancellations,
    int Failures);
