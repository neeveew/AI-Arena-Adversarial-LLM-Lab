using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AIArena.Wpf.Services;

internal static class WindowChromeService
{
    /// <summary>
    /// Paints the DWM frame from the active palette so the system border and
    /// caption match the shell in every theme, light ones included.
    /// </summary>
    public static void ApplyThemeChromeColor(Window window, ThemePalette theme)
    {
        ApplyNativeChromeColor(
            window,
            ColorRef(theme.Border),
            ColorRef(theme.TopBar),
            ColorRef(theme.Text));
    }

    /// <summary>Frame colors for secondary windows, which sit on the panel surface.</summary>
    public static void ApplySubtleThemeChromeColor(Window window, ThemePalette theme)
    {
        ApplyNativeChromeColor(
            window,
            ColorRef(theme.PrimaryBorder),
            ColorRef(theme.Panel),
            ColorRef(theme.Text));
    }

    /// <summary>
    /// Subtle frame colors for windows that carry their own copy of the theme
    /// resources rather than a palette reference.
    /// </summary>
    public static void ApplySubtleThemeChromeColor(Window window)
    {
        ApplyNativeChromeColor(
            window,
            ColorRef(ResourceColor(window, "PrimaryBorderBrush", Color.FromRgb(0x12, 0x1B, 0x26))),
            ColorRef(ResourceColor(window, "PanelBrush", Color.FromRgb(0x0B, 0x12, 0x1B))),
            ColorRef(ResourceColor(window, "TextBrush", Colors.White)));
    }

    private static Color ResourceColor(Window window, string key, Color fallback)
    {
        return window.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
    }

    private static void ApplyNativeChromeColor(Window window, int borderColor, int captionColor, int textColor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.BorderColor, ref borderColor, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref captionColor, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref textColor, Marshal.SizeOf<int>());
    }

    internal static int ColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    internal static int ColorRef(Color color)
    {
        return ColorRef(color.R, color.G, color.B);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int attributeValue, int attributeSize);

    private enum DwmWindowAttribute
    {
        BorderColor = 34,
        CaptionColor = 35,
        TextColor = 36
    }
}
