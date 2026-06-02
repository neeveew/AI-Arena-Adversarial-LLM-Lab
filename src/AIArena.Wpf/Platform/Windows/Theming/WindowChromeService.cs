using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIArena.Wpf.Services;

internal static class WindowChromeService
{
    public static void ApplyNativeChromeColor(Window window)
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

        var darkGray = ColorRef(0x20, 0x20, 0x20);
        var borderGray = ColorRef(0x58, 0x58, 0x58);
        var white = ColorRef(0xFF, 0xFF, 0xFF);
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.BorderColor, ref borderGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref darkGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref white, Marshal.SizeOf<int>());
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
