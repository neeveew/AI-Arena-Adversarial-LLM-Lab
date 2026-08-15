using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIArena.Core.Persistence;

namespace AIArena.Wpf;

internal sealed record AIArenaScreenshotControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    string Path,
    long ByteSize,
    int PixelWidth,
    int PixelHeight,
    double ViewportWidthDip,
    double ViewportHeightDip,
    double RenderDpiScale,
    double DisplayDpiScaleX,
    double DisplayDpiScaleY,
    bool RenderDpiOverride,
    DateTimeOffset? CapturedAt);

/// <summary>
/// Captures the live AI Arena WPF visual without screen scraping or shell tools.
/// Writes are atomic, relative paths stay under the app screenshot folder, and
/// existing files are never overwritten by the control-plane command.
/// </summary>
internal sealed class AIArenaScreenshotControlService
{
    private const long MaximumPixels = 100_000_000;
    private const int MaximumDimension = 16_384;
    internal const int MinimumRenderedFramesBeforeCapture = 4;
    internal const int RenderedFramesAfterWarmup = 2;
    internal static readonly double[] SupportedQaRenderDpiScales = [1.0, 1.5, 2.0];
    internal static readonly TimeSpan RenderSettleTimeout = TimeSpan.FromSeconds(2);
    private readonly Window window;
    private readonly string screenshotsRoot;

    public AIArenaScreenshotControlService(Window window, string dataRoot)
    {
        this.window = window;
        screenshotsRoot = System.IO.Path.Combine(NativeDataPaths.ExportsRoot(dataRoot), "screenshots");
    }

    public async Task<AIArenaScreenshotControlResult> CaptureAsync(
        string? requestedPath,
        CancellationToken cancellationToken = default,
        double? renderDpiScale = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRenderDpiScale = 0d;
        if (renderDpiScale.HasValue
            && !TryNormalizeQaRenderDpiScale(renderDpiScale.Value, out normalizedRenderDpiScale))
        {
            return Failure(
                "invalid_argument",
                "args.renderDpiScale must be one of 1.0, 1.5, or 2.0.");
        }
        if (renderDpiScale.HasValue)
        {
            renderDpiScale = normalizedRenderDpiScale;
        }

        if (!TryResolvePath(requestedPath, out var targetPath, out var pathError))
        {
            return Failure("invalid_argument", pathError);
        }

        if (File.Exists(targetPath))
        {
            return Failure("already_exists", $"Screenshot target already exists: {targetPath}", targetPath);
        }

        if (!window.Dispatcher.CheckAccess())
        {
            var captureTask = await window.Dispatcher.InvokeAsync(
                () => CaptureSettledVisualAsync(targetPath, renderDpiScale, cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken);
            return await captureTask;
        }

        return await CaptureSettledVisualAsync(targetPath, renderDpiScale, cancellationToken);
    }

    internal static bool IsSupportedQaRenderDpiScale(double scale)
    {
        return TryNormalizeQaRenderDpiScale(scale, out _);
    }

    internal static bool TryNormalizeQaRenderDpiScale(double scale, out double normalized)
    {
        normalized = 0;
        if (!double.IsFinite(scale))
        {
            return false;
        }

        foreach (var candidate in SupportedQaRenderDpiScales)
        {
            if (Math.Abs(candidate - scale) < 0.0001)
            {
                normalized = candidate;
                return true;
            }
        }

        return false;
    }

    private async Task<AIArenaScreenshotControlResult> CaptureSettledVisualAsync(
        string targetPath,
        double? renderDpiScale,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateVisualTree(window);
        window.UpdateLayout();

        // Navigation commands can complete before newly revealed controls have
        // passed through their Loaded/binding/render cascade. Drain queued work,
        // then observe four real composition turns: visibility/template commit,
        // Loaded callbacks, work scheduled by Loaded, and one stable rendered
        // frame. This stays event-driven instead of guessing with a timed sleep.
        await window.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle,
            cancellationToken);
        InvalidateVisualTree(window);
        window.UpdateLayout();
        await WaitForRenderedFramesAsync(MinimumRenderedFramesBeforeCapture, cancellationToken);

        // A rendering callback can enqueue another binding/layout operation.
        // Drain that follow-up work and commit one final layout/render pass so
        // the bitmap cannot land between navigation's last two visual states.
        await window.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle,
            cancellationToken);
        InvalidateVisualTree(window);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Render,
            cancellationToken);

        // WPF can leave newly revealed retained visuals without drawing content
        // for the first RenderTargetBitmap traversal after a large visibility
        // switch. Prime that traversal without saving it, then give invalidated
        // descendants two composition turns to publish their drawing content.
        // The following CaptureCore call is therefore the stable render, not the
        // cache-warming render that can otherwise contain large black regions.
        PrimeRetainedVisuals(renderDpiScale, cancellationToken);
        InvalidateVisualTree(window);
        window.UpdateLayout();
        await WaitForRenderedFramesAsync(RenderedFramesAfterWarmup, cancellationToken);
        await window.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle,
            cancellationToken);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Render,
            cancellationToken);

        return CaptureCore(targetPath, renderDpiScale, cancellationToken);
    }

    private void PrimeRetainedVisuals(double? renderDpiScale, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var displayDpi = VisualTreeHelper.GetDpi(window);
        var renderDpi = RenderDpi(displayDpi, renderDpiScale);
        var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth * renderDpi.DpiScaleX));
        var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight * renderDpi.DpiScaleY));
        if (pixelWidth <= 0
            || pixelHeight <= 0
            || pixelWidth > MaximumDimension
            || pixelHeight > MaximumDimension
            || (long)pixelWidth * pixelHeight > MaximumPixels)
        {
            return;
        }

        var warmup = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            renderDpi.PixelsPerInchX,
            renderDpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        warmup.Render(window);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void InvalidateVisualTree(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is FrameworkElement element)
            {
                element.ApplyTemplate();
                element.InvalidateVisual();
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private async Task WaitForRenderedFramesAsync(int frameCount, CancellationToken cancellationToken)
    {
        if (frameCount <= 0 || !window.IsVisible || window.WindowState == WindowState.Minimized)
        {
            return;
        }

        var remaining = frameCount;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler rendering = null!;
        rendering = (_, _) =>
        {
            if (--remaining <= 0)
            {
                completion.TrySetResult(true);
            }
        };

        CompositionTarget.Rendering += rendering;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            window.InvalidateVisual();
            await completion.Task.WaitAsync(RenderSettleTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Some remote/minimized desktop sessions do not produce composition
            // callbacks. UpdateLayout plus the dispatcher render turn remain a
            // valid fallback instead of turning a useful capture into a failure.
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }
    }

    internal bool TryResolvePath(string? requestedPath, out string path, out string error)
    {
        path = "";
        error = "";
        try
        {
            var root = System.IO.Path.GetFullPath(screenshotsRoot);
            var value = requestedPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                value = $"AI-Arena-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            }

            if (string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(value)))
            {
                value += ".png";
            }

            if (!System.IO.Path.GetExtension(value).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                error = "args.path must name a PNG file.";
                return false;
            }

            var isRelative = !System.IO.Path.IsPathRooted(value);
            var fullPath = System.IO.Path.GetFullPath(isRelative ? System.IO.Path.Combine(root, value) : value);
            if (isRelative && !IsWithinRoot(fullPath, root))
            {
                error = "A relative screenshot path cannot leave the app screenshot directory.";
                return false;
            }

            if (fullPath.Length > 32_000)
            {
                error = "args.path is too long.";
                return false;
            }

            path = fullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = AppErrorPresenter.Present(ex, AppErrorContext.ControlPlane).DisplayText;
            return false;
        }
    }

    /// <summary>
    /// Renders the window, then any dialog open over it.
    ///
    /// RenderTargetBitmap walks one visual tree, and a dialog is a separate
    /// window with its own. Capturing the main window alone therefore produced
    /// an image showing no dialog at all while one was plainly on screen, which
    /// is worse than an obviously failed capture: it quietly reports a state the
    /// app is not in, and anyone verifying against it draws the wrong conclusion.
    /// </summary>
    private RenderTargetBitmap RenderWithOpenDialogs(
        int pixelWidth,
        int pixelHeight,
        DpiScale renderDpi,
        DpiScale displayDpi)
    {
        var main = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            renderDpi.PixelsPerInchX,
            renderDpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        main.Render(window);

        var dialogs = OpenDialogs();
        main.Freeze();
        var composite = new DrawingVisual();
        using (var drawing = composite.RenderOpen())
        {
            // Evidence represents the full WPF window, not an alpha-masked
            // sprite. Compositing first onto an opaque canvas prevents a sparse
            // visual tree or an unpainted Window background from producing a
            // mostly transparent PNG that looks blank in ordinary viewers.
            drawing.DrawRectangle(
                Brushes.Black,
                null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawImage(main, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            foreach (var dialog in dialogs)
            {
                var dialogWidth = (int)Math.Ceiling(dialog.ActualWidth * renderDpi.DpiScaleX);
                var dialogHeight = (int)Math.Ceiling(dialog.ActualHeight * renderDpi.DpiScaleY);
                if (dialogWidth <= 0 || dialogHeight <= 0)
                {
                    continue;
                }

                var dialogBitmap = new RenderTargetBitmap(
                    dialogWidth,
                    dialogHeight,
                    renderDpi.PixelsPerInchX,
                    renderDpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                dialogBitmap.Render(dialog);
                dialogBitmap.Freeze();

                // Offset via screen coordinates rather than Left and Top. A
                // maximized window reports its restore bounds in Left and Top,
                // not where it actually sits, so subtracting them placed the
                // dialog wrongly - and only ever while maximized, which is the
                // state least likely to be checked.
                var ownerOrigin = window.PointToScreen(new Point(0, 0));
                var dialogOrigin = dialog.PointToScreen(new Point(0, 0));
                drawing.DrawImage(
                    dialogBitmap,
                    new Rect(
                        (dialogOrigin.X - ownerOrigin.X) / displayDpi.DpiScaleX,
                        (dialogOrigin.Y - ownerOrigin.Y) / displayDpi.DpiScaleY,
                        dialog.ActualWidth,
                        dialog.ActualHeight));
            }
        }

        var composed = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            renderDpi.PixelsPerInchX,
            renderDpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        composed.Render(composite);
        composed.Freeze();
        return composed;
    }

    private List<Window> OpenDialogs()
    {
        var dialogs = new List<Window>();
        if (Application.Current is null)
        {
            return dialogs;
        }

        foreach (var candidate in Application.Current.Windows.OfType<Window>())
        {
            if (!ReferenceEquals(candidate, window)
                && candidate.IsVisible
                && ReferenceEquals(candidate.Owner, window)
                && candidate.ActualWidth > 0
                && candidate.ActualHeight > 0)
            {
                dialogs.Add(candidate);
            }
        }

        return dialogs;
    }

    private AIArenaScreenshotControlResult CaptureCore(
        string targetPath,
        double? renderDpiScale,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            window.UpdateLayout();
            var displayDpi = VisualTreeHelper.GetDpi(window);
            var renderDpi = RenderDpi(displayDpi, renderDpiScale);
            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth * renderDpi.DpiScaleX));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight * renderDpi.DpiScaleY));
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return Failure("not_available", "The AI Arena - Lite window has no renderable area.", targetPath);
            }

            if (pixelWidth > MaximumDimension
                || pixelHeight > MaximumDimension
                || (long)pixelWidth * pixelHeight > MaximumPixels)
            {
                return Failure(
                    "not_available",
                    $"The AI Arena - Lite window is too large to capture safely ({pixelWidth}x{pixelHeight}).",
                    targetPath);
            }

            var bitmap = RenderWithOpenDialogs(pixelWidth, pixelHeight, renderDpi, displayDpi);

            var directory = System.IO.Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failure("invalid_argument", "Screenshot path has no parent directory.", targetPath);
            }

            Directory.CreateDirectory(directory);
            tempPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var encodedPng = new MemoryStream();
            encoder.Save(encodedPng);
            encodedPng.Position = 0;
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                WriteEssentialPng(encodedPng, stream);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite: false);
            tempPath = null;
            var info = new FileInfo(targetPath);
            var capturedAt = DateTimeOffset.UtcNow;
            return new AIArenaScreenshotControlResult(
                true,
                "",
                $"AI Arena - Lite screenshot saved to {targetPath}",
                targetPath,
                info.Length,
                pixelWidth,
                pixelHeight,
                Math.Round(window.ActualWidth, 2, MidpointRounding.AwayFromZero),
                Math.Round(window.ActualHeight, 2, MidpointRounding.AwayFromZero),
                Math.Round(renderDpi.DpiScaleX, 3, MidpointRounding.AwayFromZero),
                Math.Round(displayDpi.DpiScaleX, 3, MidpointRounding.AwayFromZero),
                Math.Round(displayDpi.DpiScaleY, 3, MidpointRounding.AwayFromZero),
                renderDpiScale.HasValue,
                capturedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or NotSupportedException)
        {
            var presentation = AppErrorPresenter.Present(ex, AppErrorContext.ControlPlane);
            return Failure("screenshot_failed", presentation.DisplayText, targetPath);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A cleanup failure must not replace the capture result.
                }
            }
        }
    }

    /// <summary>
    /// WPF's PNG encoder adds colour-profile and physical-density metadata that is
    /// both unnecessary for QA evidence and capable of carrying unreviewed text.
    /// Preserve the encoder's lossless image data while emitting only the PNG
    /// chunks required to decode it. The authoritative evidence validator applies
    /// the same allow-list and independently validates CRCs and decoded pixels.
    /// </summary>
    private static void WriteEssentialPng(Stream source, Stream destination)
    {
        ReadOnlySpan<byte> expectedSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        Span<byte> signature = stackalloc byte[8];
        source.ReadExactly(signature);
        if (!signature.SequenceEqual(expectedSignature))
        {
            throw new InvalidDataException("The WPF screenshot encoder returned an invalid PNG signature.");
        }

        destination.Write(signature);
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        Span<byte> chunkHeader = stackalloc byte[8];
        var transferBuffer = new byte[64 * 1024];

        while (!sawEnd)
        {
            source.ReadExactly(chunkHeader);
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            if (length > int.MaxValue)
            {
                throw new InvalidDataException("The WPF screenshot encoder returned an oversized PNG chunk.");
            }

            var chunkType = chunkHeader[4..8];
            var isHeader = chunkType.SequenceEqual("IHDR"u8);
            var isImageData = chunkType.SequenceEqual("IDAT"u8);
            var isEnd = chunkType.SequenceEqual("IEND"u8);
            var keep = isHeader || isImageData || isEnd;

            if (isHeader)
            {
                if (sawHeader || sawImageData || length != 13)
                {
                    throw new InvalidDataException("The WPF screenshot encoder returned an invalid PNG header order.");
                }

                sawHeader = true;
            }
            else if (isImageData)
            {
                if (!sawHeader || sawEnd)
                {
                    throw new InvalidDataException("The WPF screenshot encoder returned PNG image data out of order.");
                }

                sawImageData = true;
            }
            else if (isEnd)
            {
                if (!sawHeader || !sawImageData || length != 0)
                {
                    throw new InvalidDataException("The WPF screenshot encoder returned an invalid PNG end marker.");
                }

                sawEnd = true;
            }

            if (keep)
            {
                destination.Write(chunkHeader);
            }

            CopyOrSkipPngBytes(source, destination, checked((int)length + 4), keep, transferBuffer);
        }

        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException("The WPF screenshot encoder returned data after the PNG end marker.");
        }
    }

    private static void CopyOrSkipPngBytes(
        Stream source,
        Stream destination,
        int byteCount,
        bool copy,
        byte[] buffer)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            source.ReadExactly(buffer.AsSpan(0, count));
            if (copy)
            {
                destination.Write(buffer, 0, count);
            }

            remaining -= count;
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static DpiScale RenderDpi(DpiScale displayDpi, double? renderDpiScale)
    {
        return renderDpiScale is { } scale
            ? new DpiScale(scale, scale)
            : displayDpi;
    }

    private static AIArenaScreenshotControlResult Failure(string code, string message, string path = "")
    {
        return new AIArenaScreenshotControlResult(
            false,
            code,
            message,
            path,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            null);
    }
}
