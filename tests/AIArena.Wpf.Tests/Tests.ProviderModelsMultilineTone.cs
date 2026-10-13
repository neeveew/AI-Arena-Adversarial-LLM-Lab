using System.Windows;
using System.Windows.Controls;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void ProviderModelsMultilineToneRemainsCompact() => RunStaTest(() =>
    {
        WithMultilineToneEditors((control, host, modelId) =>
        {
            var input = control.CustomToneInput;
            var scroll = FindExperimentVisualDescendant<ScrollViewer>(input)!;
            CaptureProviderModelsInlinePreview(control, $"tone-{modelId}-{host.Width:0}.png");
            Console.WriteLine($"multiline tone layout receipt: model={modelId}; viewport={host.Width:0}x{host.Height:0}; "
                + $"chars={input.Text.Length}; field_height={input.ActualHeight:0.##}; inner_overflow={scroll.ScrollableHeight:0.##}");
            Require(input.Text.Length <= input.MaxLength && input.ActualHeight <= 160 && scroll.ScrollableHeight > 100,
                $"A valid multiline draft consumed the model editor instead of a compact scrollable field (height {input.ActualHeight:0.##}, overflow {scroll.ScrollableHeight:0.##}).");
            input.Text = "Measured, concise, and evidence-led.";
            FlushProviderModelsDispatcher(host);
            Require(input.ActualHeight <= 80 && scroll.ScrollableHeight <= 1,
                "Replacing the long draft with an ordinary short instruction must shrink the field and remove its inner overflow.");
        });
    });

    static void ProviderModelsMultilineToneScrollChainsAtItsBoundaries() => RunStaTest(() =>
    {
        WithMultilineToneEditors((control, host, modelId) =>
        {
            var input = control.CustomToneInput;
            var inner = FindExperimentVisualDescendant<ScrollViewer>(input)!;
            var selectedList = modelId.StartsWith("loaded", StringComparison.Ordinal) ? control.LoadedList : control.CatalogList;
            var otherList = ReferenceEquals(selectedList, control.LoadedList) ? control.CatalogList : control.LoadedList;
            var outer = FindExperimentVisualDescendant<ScrollViewer>(selectedList)!;
            var other = FindExperimentVisualDescendant<ScrollViewer>(otherList)!;
            Require(inner.ScrollableHeight > 100 && outer.ScrollableHeight > 200,
                $"The production multiline editor has no bounded inner scroll (height {input.ActualHeight:0.##}, overflow {inner.ScrollableHeight:0.##}).");
            var changes = 0;
            control.ConfigurationChanged += (_, _) => changes++;
            control.AssignmentChanged += (_, _) => changes++;
            var draft = input.Text;
            var editor = control.InlineEditorHost;
            var otherOffset = other.VerticalOffset;
            Require(input.Focus(), "The multiline draft could not receive keyboard focus.");
            FlushProviderModelsDispatcher(host);
            inner.ScrollToVerticalOffset(inner.ScrollableHeight / 2);
            FlushProviderModelsDispatcher(host);
            var parentBefore = outer.VerticalOffset;
            var innerBefore = inner.VerticalOffset;
            RaiseProviderModelsInlineWheel(input, -120);
            FlushProviderModelsDispatcher(host);
            Require(inner.VerticalOffset > innerBefore && Math.Abs(outer.VerticalOffset - parentBefore) <= 1,
                "Wheel scrolling in the middle of a long draft must move the text, not the surrounding model list.");
            inner.ScrollToBottom();
            FlushProviderModelsDispatcher(host);
            parentBefore = outer.VerticalOffset;
            RaiseProviderModelsInlineWheel(input, -120);
            FlushProviderModelsDispatcher(host);
            Require(outer.VerticalOffset > parentBefore && Math.Abs(inner.VerticalOffset - inner.ScrollableHeight) <= 1,
                "Wheel down at the draft's bottom boundary must continue through the owning model list.");
            inner.ScrollToTop();
            FlushProviderModelsDispatcher(host);
            parentBefore = outer.VerticalOffset;
            RaiseProviderModelsInlineWheel(input, 120);
            FlushProviderModelsDispatcher(host);
            Require(outer.VerticalOffset < parentBefore && inner.VerticalOffset <= 1
                    && Math.Abs(other.VerticalOffset - otherOffset) <= 1 && input.IsKeyboardFocusWithin
                    && input.Text == draft && control.SelectedModelId == modelId
                    && ReferenceEquals(control.InlineEditorHost, editor) && changes == 0,
                "Wheel up at the top boundary must move only the owning list while preserving the focused draft and selection.");
        });
    });

    private static void WithMultilineToneEditors(Action<ProviderModelAssignmentsControl, Window, string> action)
    {
        foreach (var modelId in new[] { "catalog-00", "loaded-00" })
        foreach (var width in new[] { 960d, 1500d })
        {
            var original = ProviderModelsPinnedPresentation(12, 60, modelId);
            var presentation = original with
            {
                Models = original.Models.Select(model => model with
                {
                    Configuration = new ProviderModelConfigurationPresentation(
                        ContextWindow: 8_192, EffectiveContextWindow: 4_096,
                        ResponseTone: ProviderModelResponseTone.Custom, CustomTone: "Measured tone.",
                        ConfigurationIdentity: model.Id + "-configuration")
                }).ToArray()
            };
            var control = HostProviderModelsOptimizationSurface(presentation, width, 780, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(control.SelectModel(modelId), "The selected model did not expand.");
                control.AdvancedSettings.IsExpanded = true;
                FlushProviderModelsDispatcher(host);
                var input = control.CustomToneInput;
                Require(input.IsVisible && input.IsEnabled, "The custom tone field is not available in the production editor.");
                input.Text = string.Join('\n', Enumerable.Repeat("a", 45));
                input.BringIntoView();
                FlushProviderModelsDispatcher(host);
                action(control, host, modelId);
            }
            finally { host.Close(); }
        }
    }
}
