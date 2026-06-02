using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIArena.Wpf.Services;

internal static class WindowChromeService
{
    public static void ApplyNativeChromeColor(Window window)
    {
        ApplyNativeChromeColor(
            window,
            ColorRef(0x58, 0x58, 0x58),
            ColorRef(0x20, 0x20, 0x20),
            ColorRef(0xFF, 0xFF, 0xFF));
    }

    public static void ApplySubtleNativeChromeColor(Window window)
    {
        ApplyNativeChromeColor(
            window,
            ColorRef(0x12, 0x1B, 0x26),
            ColorRef(0x0B, 0x12, 0x1B),
            ColorRef(0xFF, 0xFF, 0xFF));
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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int attributeValue, int attributeSize);

    private enum DwmWindowAttribute
    {
        BorderColor = 34,
        CaptionColor = 35,
        TextColor = 36
    }
}
