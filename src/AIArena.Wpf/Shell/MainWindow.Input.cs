using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIArena.Wpf;

public partial class MainWindow
{
    // Driving the app from a script stopped at the keyboard. Everything else
    // reaches the shell over the pipe, but a chord or a piece of typing had to be
    // simulated at the operating-system level, and simulated input goes to
    // whichever window Windows currently considers foreground - not necessarily
    // this one. A background caller could therefore type into somebody else's
    // application without noticing.
    //
    // These route the same chords and the same text through the app's own
    // handlers, on the UI thread, with no focus involved at all.

    internal (bool Ok, string ErrorCode, string Message, object State) ControlSendKey(
        string? keyText,
        string? modifiersText)
    {
        if (!TryParseShortcutKey(keyText, out var key))
        {
            return (false, "invalid_argument", $"Unrecognized key '{keyText}'.", BuildInputState(false));
        }

        var (control, shift, alt) = ParseModifiers(modifiersText);
        var handled = TryHandleShellShortcut(key, control, shift, alt);
        var chord = DescribeChord(key, control, shift, alt);

        // An unhandled chord is reported rather than treated as a failure: asking
        // whether a key does anything is a legitimate question, and a caller that
        // wants a hard failure can check handled itself.
        return (
            true,
            "",
            handled ? $"{chord} handled." : $"{chord} is not bound.",
            BuildInputState(handled));
    }

    internal (bool Ok, string ErrorCode, string Message, object State) ControlTypeText(
        string? target,
        string? text)
    {
        var value = text ?? "";
        var box = ResolveTextTarget(target);
        if (box is null)
        {
            var reason = string.IsNullOrWhiteSpace(target)
                ? "No text field currently has focus, and no args.target was given."
                : $"No text field named '{target}' is available.";
            return (false, "not_available", reason, BuildInputState(false));
        }

        // A field the reader could not type into should not accept typing from a
        // script either. Setting Text works regardless of either flag, so
        // without this a caller could fill a box that is disabled because an
        // operation is running, and be told it succeeded.
        if (!box.IsEnabled)
        {
            return (false, "not_available", $"{box.Name} is disabled.", BuildInputState(false));
        }

        if (box.IsReadOnly)
        {
            return (false, "not_available", $"{box.Name} is read-only.", BuildInputState(false));
        }

        // Setting Text raises TextChanged, which is what the app's own handlers
        // listen to, so this behaves like typing rather than poking at state.
        box.Text = value;
        box.CaretIndex = box.Text.Length;
        return (true, "", $"Typed {value.Length} characters into {box.Name}.", BuildInputState(true));
    }

    private object BuildInputState(bool handled)
    {
        return new
        {
            handled,
            surface = SelectedControlPlaneView(),
            focused = (Keyboard.FocusedElement as FrameworkElement)?.Name ?? ""
        };
    }

    private TextBox? ResolveTextTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Keyboard.FocusedElement as TextBox;
        }

        return FindName(target.Trim()) as TextBox;
    }

    /// <summary>
    /// Accepts what a person would write: "F2", "k", "ctrl" chords are handled
    /// separately, and a bare digit maps onto the D-keys so "1" means Ctrl+1
    /// rather than failing on an enum name nobody would guess.
    /// </summary>
    internal static bool TryParseShortcutKey(string? keyText, out Key key)
    {
        key = Key.None;
        var trimmed = (keyText ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            trimmed = "D" + trimmed;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out key) && key != Key.None;
    }

    internal static (bool Control, bool Shift, bool Alt) ParseModifiers(string? modifiersText)
    {
        var control = false;
        var shift = false;
        var alt = false;
        foreach (var part in (modifiersText ?? "").Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    control = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
            }
        }

        return (control, shift, alt);
    }

    internal static string DescribeChord(Key key, bool control, bool shift, bool alt)
    {
        var parts = new List<string>();
        if (control)
        {
            parts.Add("Ctrl");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
