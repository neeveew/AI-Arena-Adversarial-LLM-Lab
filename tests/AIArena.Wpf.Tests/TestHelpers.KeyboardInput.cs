using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

internal static partial class Program
{
    private sealed record HostedSpaceKeyReceipt(bool DownHandled, bool UpHandled, int ClickCount, string Diagnostic);

    private static HostedSpaceKeyReceipt PressHostedSpaceKey(ButtonBase button, Window host)
    {
        Require(button.Focus() && button.IsKeyboardFocused,
            "Hosted production button could not receive keyboard focus.");
        var source = PresentationSource.FromVisual(button) ?? PresentationSource.FromVisual(host)
            ?? throw new InvalidOperationException("Hosted production button has no presentation source.");
        // ButtonBase reads Keyboard.Modifiers and Mouse.LeftButton from the
        // calling thread's Win32 key-state table, not from KeyEventArgs. Keep
        // our synthetic key gesture coherent even while the desktop user clicks
        // or holds Alt elsewhere. SetKeyboardState affects only this thread;
        // restore the exact prior table, and never pump/await while it is set.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setkeyboardstate
        // https://github.com/dotnet/wpf/blob/release/10.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs
        using var input = new FixtureThreadKeyboardState();
        var clicks = 0;
        RoutedEventHandler countClick = (_, _) => clicks++;
        button.AddHandler(ButtonBase.ClickEvent, countClick, handledEventsToo: true);
        try
        {
            if (button.IsMouseCaptured)
                button.ReleaseMouseCapture();
            input.Apply(spaceDown: true);
            var before = HostedButtonInputState(button);
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            button.RaiseEvent(down);
            var afterDown = HostedButtonInputState(button);
            Require(down.Handled && button.IsPressed,
                $"Production Space key-down did not press the button (before: {before}; after: {afterDown}; handled={down.Handled}).");

            input.Apply(spaceDown: false);
            var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
            {
                RoutedEvent = Keyboard.KeyUpEvent
            };
            button.RaiseEvent(up);
            var diagnostic = $"before: {before}; down handled={down.Handled}: {afterDown}; up handled={up.Handled}: {HostedButtonInputState(button)}; clicks={clicks}";
            Require(up.Handled && clicks == 1 && !button.IsPressed,
                $"Production Space key-up did not release and click exactly once ({diagnostic}).");
            return new HostedSpaceKeyReceipt(down.Handled, up.Handled, clicks, diagnostic);
        }
        finally
        {
            button.RemoveHandler(ButtonBase.ClickEvent, countClick);
            if (button.IsMouseCaptured)
                button.ReleaseMouseCapture();
        }
    }

    private static string HostedButtonInputState(ButtonBase button) =>
        $"focus={button.IsKeyboardFocused}, capture={button.IsMouseCaptured}, pressed={button.IsPressed}, modifiers={Keyboard.Modifiers}, mouse={Mouse.LeftButton}, focused={Keyboard.FocusedElement?.GetType().Name}";

    private sealed class FixtureThreadKeyboardState : IDisposable
    {
        private readonly byte[] original = Read();
        private bool disposed;

        internal static byte[] Read()
        {
            var state = new byte[256];
            if (!GetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return state;
        }

        internal void Apply(bool spaceDown = false, bool leftMouseDown = false, bool altDown = false)
        {
            var state = new byte[256];
            state[0x20] = spaceDown ? (byte)0x80 : (byte)0;
            state[0x01] = leftMouseDown ? (byte)0x80 : (byte)0;
            state[0x12] = state[0xA4] = altDown ? (byte)0x80 : (byte)0;
            if (!SetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!SetKeyboardState(original)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState([Out] byte[] state);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKeyboardState(byte[] state);
    }

    static void HostedSpaceKeyboardFixtureIsolatesThreadInput()
    {
        RunStaTest(() =>
        {
            var button = new Button { Content = "Production ButtonBase keyboard path", Width = 240, Height = 48 };
            var host = new Window { Content = button, Width = 360, Height = 160, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Opacity = 0, Left = -10000, Top = -10000 };
            host.Show();
            host.Activate();
            FlushProviderModelsDispatcher(host);
            try
            {
                Require(button.Focus(), "Reproduction button could not receive focus.");
                var source = PresentationSource.FromVisual(host)!;
                using var ambient = new FixtureThreadKeyboardState();
                var clicks = 0;
                button.Click += (_, _) => clicks++;
                ambient.Apply(spaceDown: true, leftMouseDown: true);
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent };
                button.RaiseEvent(down);
                ambient.Apply(leftMouseDown: true);
                var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent };
                button.RaiseEvent(up);
                Require(down.Handled && up.Handled && clicks == 0 && Mouse.LeftButton == MouseButtonState.Pressed,
                    $"Thread-local held mouse did not reproduce the original handled-Space-without-click failure ({HostedButtonInputState(button)}; clicks={clicks}).");
                button.ReleaseMouseCapture();

                ambient.Apply(leftMouseDown: true, altDown: true);
                var expectedAmbient = FixtureThreadKeyboardState.Read();
                var receipt = PressHostedSpaceKey(button, host);
                Require(receipt.DownHandled && receipt.UpHandled && receipt.ClickCount == 1 && clicks == 1,
                    "Isolated Space input did not use the real WPF button keyboard path exactly once.");
                Require(FixtureThreadKeyboardState.Read().SequenceEqual(expectedAmbient),
                    "Successful keyboard dispatch did not restore the exact prior thread-local input state.");
                try
                {
                    using var interrupted = new FixtureThreadKeyboardState();
                    interrupted.Apply(spaceDown: true);
                    throw new InvalidOperationException("injected fixture failure");
                }
                catch (InvalidOperationException error) when (error.Message == "injected fixture failure") { }
                Require(FixtureThreadKeyboardState.Read().SequenceEqual(expectedAmbient),
                    "An interrupted keyboard fixture did not restore the exact prior thread-local input state.");
                Console.WriteLine($"keyboard isolation proof: original handled key pair clicks=0 with thread mouse held; isolated production key pair clicks={receipt.ClickCount}; exact thread state restored on success and exception");
            }
            finally { host.Close(); }
        });
    }
}
