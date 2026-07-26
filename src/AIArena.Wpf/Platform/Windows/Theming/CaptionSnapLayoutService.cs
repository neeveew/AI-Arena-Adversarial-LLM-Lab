using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AIArena.Wpf.Services;

/// <summary>
/// Restores the Windows 11 Snap Layouts flyout for the custom caption bar.
///
/// Windows only offers the flyout when a window answers WM_NCHITTEST with
/// HTMAXBUTTON, which also moves that button into the non-client area: WPF no
/// longer raises hover or click for it, so both are driven from window messages
/// here. Only the maximize button is claimed; minimize and close stay ordinary
/// client-area buttons.
/// </summary>
internal sealed class CaptionSnapLayoutService
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmMouseMove = 0x0200;
    private const int HtMaxButton = 9;

    /// <summary>Tag applied while the non-client pointer is over the button.</summary>
    internal const string HoverTag = "caption-hover";

    private readonly Window window;
    private readonly Button maximizeButton;
    private readonly Action toggleMaximize;
    private bool hovered;

    private CaptionSnapLayoutService(Window window, Button maximizeButton, Action toggleMaximize)
    {
        this.window = window;
        this.maximizeButton = maximizeButton;
        this.toggleMaximize = toggleMaximize;
    }

    /// <summary>
    /// Hooks the window when Snap Layouts is available. On earlier Windows
    /// versions the caller's ordinary WPF click handling remains in effect.
    /// </summary>
    public static void Attach(Window window, Button maximizeButton, Action toggleMaximize)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var service = new CaptionSnapLayoutService(window, maximizeButton, toggleMaximize);
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(service.WindowProc);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource initialized)
            {
                initialized.AddHook(service.WindowProc);
            }
        };
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    SetHovered(true);
                    handled = true;
                    return HtMaxButton;
                }

                SetHovered(false);
                return IntPtr.Zero;

            case WmNcMouseMove:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    SetHovered(true);
                }

                return IntPtr.Zero;

            case WmMouseMove:
            case WmNcMouseLeave:
                SetHovered(false);
                return IntPtr.Zero;

            case WmNcLeftButtonDown:
                // Swallow the press so Windows does not begin its own caption
                // drag; the release below performs the maximize toggle.
                if (wParam.ToInt32() == HtMaxButton)
                {
                    handled = true;
                }

                return IntPtr.Zero;

            case WmNcLeftButtonUp:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    SetHovered(false);
                    toggleMaximize();
                    handled = true;
                }

                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void SetHovered(bool value)
    {
        if (hovered == value)
        {
            return;
        }

        hovered = value;
        maximizeButton.Tag = value ? HoverTag : null;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!maximizeButton.IsVisible || PresentationSource.FromVisual(maximizeButton) is null)
        {
            return false;
        }

        // lParam packs signed screen coordinates; multi-monitor setups produce
        // negative values, so the halves must be read as signed shorts.
        var screenPoint = new Point(
            unchecked((short)(lParam.ToInt32() & 0xFFFF)),
            unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF)));

        var topLeft = maximizeButton.PointToScreen(new Point(0, 0));
        var bottomRight = maximizeButton.PointToScreen(
            new Point(maximizeButton.ActualWidth, maximizeButton.ActualHeight));

        return screenPoint.X >= topLeft.X
            && screenPoint.X < bottomRight.X
            && screenPoint.Y >= topLeft.Y
            && screenPoint.Y < bottomRight.Y;
    }
}
