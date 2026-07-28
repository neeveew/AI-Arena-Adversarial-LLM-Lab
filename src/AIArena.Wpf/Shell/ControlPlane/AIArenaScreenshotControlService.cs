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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                () => CaptureSettledVisualAsync(targetPath, cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken);
            return await captureTask;
        }

        return await CaptureSettledVisualAsync(targetPath, cancellationToken);
    }

    private async Task<AIArenaScreenshotControlResult> CaptureSettledVisualAsync(
        string targetPath,
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
        PrimeRetainedVisuals(cancellationToken);
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

        return CaptureCore(targetPath, cancellationToken);
    }

    private void PrimeRetainedVisuals(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dpi = VisualTreeHelper.GetDpi(window);
        var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
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
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
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
            error = $"Invalid screenshot path: {ex.Message}";
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
    private RenderTargetBitmap RenderWithOpenDialogs(int pixelWidth, int pixelHeight, DpiScale dpi)
    {
        var main = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        main.Render(window);

        var dialogs = OpenDialogs();
        if (dialogs.Count == 0)
        {
            main.Freeze();
            return main;
        }

        main.Freeze();
        var composite = new DrawingVisual();
        using (var drawing = composite.RenderOpen())
        {
            drawing.DrawImage(main, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            foreach (var dialog in dialogs)
            {
                var dialogWidth = (int)Math.Ceiling(dialog.ActualWidth * dpi.DpiScaleX);
                var dialogHeight = (int)Math.Ceiling(dialog.ActualHeight * dpi.DpiScaleY);
                if (dialogWidth <= 0 || dialogHeight <= 0)
                {
                    continue;
                }

                var dialogBitmap = new RenderTargetBitmap(
                    dialogWidth,
                    dialogHeight,
                    dpi.PixelsPerInchX,
                    dpi.PixelsPerInchY,
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
                        (dialogOrigin.X - ownerOrigin.X) / dpi.DpiScaleX,
                        (dialogOrigin.Y - ownerOrigin.Y) / dpi.DpiScaleY,
                        dialog.ActualWidth,
                        dialog.ActualHeight));
            }
        }

        var composed = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
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

    private AIArenaScreenshotControlResult CaptureCore(string targetPath, CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var pixelWidth = checked((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            var pixelHeight = checked((int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return Failure("not_available", "The AI Arena window has no renderable area.", targetPath);
            }

            if (pixelWidth > MaximumDimension
                || pixelHeight > MaximumDimension
                || (long)pixelWidth * pixelHeight > MaximumPixels)
            {
                return Failure(
                    "not_available",
                    $"The AI Arena window is too large to capture safely ({pixelWidth}x{pixelHeight}).",
                    targetPath);
            }

            var bitmap = RenderWithOpenDialogs(pixelWidth, pixelHeight, dpi);

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
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite: false);
            tempPath = null;
            var info = new FileInfo(targetPath);
            var capturedAt = DateTimeOffset.Now;
            return new AIArenaScreenshotControlResult(
                true,
                "",
                $"AI Arena screenshot saved to {targetPath}",
                targetPath,
                info.Length,
                pixelWidth,
                pixelHeight,
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
            return Failure("screenshot_failed", $"AI Arena screenshot failed: {ex.Message}", targetPath);
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

    private static bool IsWithinRoot(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static AIArenaScreenshotControlResult Failure(string code, string message, string path = "")
    {
        return new AIArenaScreenshotControlResult(false, code, message, path, 0, 0, 0, null);
    }
}
