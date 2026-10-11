using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf;

internal static partial class Program
{
    static void CastControlsKeepEverySelectedStyleReadable()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var clipped = new List<string>();
            foreach (var width in new[] { 340.0, 460.0 })
            {
                using var fixture = new MatchSettingFixture { Rendering = true };
                var card = fixture.Coordinator.CreateLockCard("alpha", "Alpha", "Test persona",
                    Brushes.Black, Brushes.Cyan, false, "default", "default");
                var host = new Window { Width = width, Height = 360, Content = card, ShowInTaskbar = false };
                try
                {
                    AttachArenaPresentationResources(host);
                    host.Resources[typeof(ComboBox)] = host.FindResource("Arena.ComboBox");
                    host.Show();
                    PumpLifecycleUi();
                    foreach (var picker in CastControlDescendants<ComboBox>(card))
                    {
                        var caption = AutomationProperties.GetLabeledBy(picker) as TextBlock;
                        Require(caption is not null && (caption.Text == "Voice" || caption.Text == "Pressure")
                                && AutomationProperties.GetName(picker) == $"Alpha {caption.Text.ToLowerInvariant()}",
                            "Compact values must retain a visible label and an accessible role-specific name.");
                        var options = caption!.Text == "Voice" ? RoleStyleCatalog.VoiceStyleOptions : RoleStyleCatalog.AgentPressureOptions;
                        Require(picker.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag)
                                .SequenceEqual(options.Select(option => option.Tag)),
                            "Compact presentation must retain every original saved option identity and its ordering.");
                        foreach (var item in picker.Items.Cast<ComboBoxItem>())
                        {
                            picker.SelectedItem = item;
                            host.UpdateLayout();
                            var presenter = CastControlDescendants<ContentPresenter>(picker)
                                .Single(element => element.Name == "SelectionPresenter");
                            var label = Convert.ToString(item.Content, CultureInfo.CurrentCulture) ?? "";
                            Require(AutomationProperties.GetHelpText(picker).Contains($"Current: {label}.", StringComparison.Ordinal)
                                    && Equals(picker.ToolTip, AutomationProperties.GetHelpText(picker)),
                                "Tooltip and accessible help must track the selected value.");
                            var text = new FormattedText(label, CultureInfo.CurrentUICulture, picker.FlowDirection,
                                new Typeface(picker.FontFamily, picker.FontStyle, picker.FontWeight, picker.FontStretch),
                                picker.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(picker).PixelsPerDip);
                            Require(presenter.ActualWidth > 0, "The production selection presenter must be realized.");
                            if (text.WidthIncludingTrailingWhitespace > presenter.ActualWidth + 0.5)
                                clipped.Add($"{width}: {label} needs {text.WidthIncludingTrailingWhitespace:F1}, has {presenter.ActualWidth:F1}");
                        }
                    }
                    Require(PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!.PersistenceRevision == 1,
                        "Rendering all saved style options must not mutate the session.");
                }
                finally { host.Close(); }
            }
            Require(clipped.Count == 0, "Cast style values were clipped: " + string.Join("; ", clipped));
        }));
    }

    static void CastControlsInheritBusyStateWhenRebuilt()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new MatchSettingFixture { Busy = true };
            var card = fixture.Coordinator.CreateLockCard("alpha", "Alpha", "Test persona",
                Brushes.Black, Brushes.Cyan, false, "default", "default");
            card.Measure(new Size(400, 400));
            card.Arrange(new Rect(0, 0, 400, 400));
            var controls = CastControlDescendants<Control>(card)
                .Where(control => control is ComboBox or Button or CheckBox).ToArray();
            Require(controls.Count(control => control is ComboBox) == 2, "Both role-style pickers must be present.");
            Require(controls.All(control => !control.IsEnabled),
                "Rebuilding Cast while the arena is busy must not introduce enabled mutation controls.");
            foreach (var picker in controls.OfType<ComboBox>()) picker.SelectedIndex = 1;
            PumpDocumentImportTask(fixture.LastOperation);
            Require(PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!.PersistenceRevision == 1,
                "A busy programmatic selection must not start a role-setting write.");
            fixture.Busy = false;
            fixture.Coordinator.UpdateBusyState(false);
            Require(controls.All(control => control.IsEnabled), "All rebuilt Cast controls must recover after the operation ends.");
        }));
    }

    private static IEnumerable<T> CastControlDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in CastControlDescendants<T>(child)) yield return descendant;
        }
    }
}
