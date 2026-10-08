using System.Windows;
using System.Windows.Controls;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void SectionNavigationRevealsEditorAndPreservesLaterReaderScroll()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var height in new[] { 350.0, 700.0 })
            {
                using var fixture = new SectionNavigationFixture(height);
                Require(fixture.First.TranslatePoint(new Point(), fixture.Viewport).Y > fixture.Viewport.ViewportHeight,
                    "The editor must start below the viewport in this regression.");
                fixture.Navigation.Reveal(fixture.First, fixture.First);
                PumpLifecycleUi();
                fixture.AssertVisibleAndFocused(fixture.First, fixture.FirstField);
                fixture.Viewport.ScrollToVerticalOffset(fixture.Viewport.VerticalOffset - 50);
                PumpLifecycleUi();
                var readerOffset = fixture.Viewport.VerticalOffset;
                fixture.FirstField.Text = "Unsent edit retained";
                fixture.Viewport.UpdateLayout();
                PumpLifecycleUi();
                Require(Math.Abs(readerOffset - fixture.Viewport.VerticalOffset) < 1
                        && fixture.FirstField.Text == "Unsent edit retained",
                    "Later layout or draft edits must not repeat navigation and reset the reader's scroll.");
            }
        }));
    }

    static void SectionNavigationOwnsLatestFocusAndRejectsObsoleteRequests()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new SectionNavigationFixture(350);
            fixture.Navigation.Reveal(fixture.First, fixture.FirstField);
            fixture.Navigation.Reveal(fixture.Second, fixture.SecondField);
            PumpLifecycleUi();
            fixture.AssertVisibleAndFocused(fixture.Second, fixture.SecondField);
            var offset = fixture.Viewport.VerticalOffset;

            fixture.Navigation.Reveal(fixture.First, fixture.FirstField);
            fixture.Navigation.Cancel();
            PumpLifecycleUi();
            Require(fixture.SecondField.IsKeyboardFocusWithin && fixture.Viewport.VerticalOffset == offset,
                "A canceled request must leave the new selection and reader position intact.");

            fixture.Navigation.Reveal(fixture.First, fixture.FirstField);
            fixture.Active = false;
            PumpLifecycleUi();
            Require(fixture.SecondField.IsKeyboardFocusWithin && fixture.Viewport.VerticalOffset == offset,
                "A closed/inactive page must reject queued focus and scrolling.");
            fixture.Active = true;

            fixture.Navigation.Reveal(fixture.First, fixture.FirstField);
            fixture.Content.Children.Remove(fixture.First);
            PumpLifecycleUi();
            Require(fixture.SecondField.IsKeyboardFocusWithin,
                "A detached target must not steal focus or throw during queued navigation.");
            fixture.Content.Children.Add(fixture.First);
            fixture.Viewport.UpdateLayout();
            fixture.FirstField.GotKeyboardFocus += (_, _) =>
                fixture.Navigation.Reveal(fixture.Second, fixture.SecondField);
            fixture.Navigation.Reveal(fixture.First, fixture.FirstField);
            PumpLifecycleUi();
            fixture.AssertVisibleAndFocused(fixture.Second, fixture.SecondField);
        }));
    }

    private sealed class SectionNavigationFixture : IDisposable
    {
        private readonly Window host;
        internal ScrollViewer Viewport { get; }
        internal StackPanel Content { get; } = new();
        internal StackPanel First { get; } = new();
        internal StackPanel Second { get; } = new();
        internal TextBox FirstField { get; } = new() { Height = 32, Text = "First editor" };
        internal TextBox SecondField { get; } = new() { Height = 32, Text = "Second editor" };
        internal bool Active { get; set; } = true;
        internal SectionViewportNavigation Navigation { get; }

        internal SectionNavigationFixture(double height)
        {
            First.Children.Add(new TextBlock { Text = "First section" });
            First.Children.Add(FirstField);
            Second.Children.Add(new TextBlock { Text = "Second section" });
            Second.Children.Add(SecondField);
            Content.Children.Add(new TextBox { Height = 32, Text = "Unrelated header input" });
            Content.Children.Add(new Border { Height = 1000 });
            Content.Children.Add(First);
            Content.Children.Add(new Border { Height = 200 });
            Content.Children.Add(Second);
            Content.Children.Add(new Border { Height = 500 });
            Viewport = new ScrollViewer
            {
                Height = height, Content = Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            host = new Window { Width = 800, Height = height + 80, Content = Viewport, ShowInTaskbar = false };
            Navigation = new SectionViewportNavigation(Viewport, () => Active);
            host.Show();
            host.Activate();
            Viewport.UpdateLayout();
        }

        internal void AssertVisibleAndFocused(FrameworkElement target, TextBox field)
        {
            var top = target.TranslatePoint(new Point(), Viewport).Y;
            var fieldTop = field.TranslatePoint(new Point(), Viewport).Y;
            Require(top >= -1 && top < Viewport.ViewportHeight / 4
                    && fieldTop >= 0 && fieldTop + field.ActualHeight <= Viewport.ViewportHeight
                    && field.IsKeyboardFocusWithin,
                $"The chosen editor must be visible and focused (top {top}; field top {fieldTop}; offset {Viewport.VerticalOffset}).");
        }

        public void Dispose()
        {
            Navigation.Cancel();
            host.Close();
        }
    }
}
