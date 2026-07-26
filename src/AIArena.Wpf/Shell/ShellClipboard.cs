using System.Runtime.InteropServices;
using System.Windows;

namespace AIArena.Wpf;

internal static class ShellClipboard
{
    public static bool TrySetText(string text, Action<string>? setText = null)
    {
        try
        {
            (setText ?? Clipboard.SetText)(text);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException)
        {
            return false;
        }
    }

    public static bool TryGetText(out string text, Func<string>? getText = null)
    {
        try
        {
            text = (getText ?? Clipboard.GetText)();
            return !string.IsNullOrWhiteSpace(text);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException)
        {
            text = "";
            return false;
        }
    }
}
