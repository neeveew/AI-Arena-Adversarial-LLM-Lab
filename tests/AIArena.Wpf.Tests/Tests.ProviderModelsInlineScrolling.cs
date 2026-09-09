using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void ProviderModelsInlineEditorRoutesWheelToItsOwningModelList()
    {
        RunStaTest(() =>
        {
            foreach (var selectedId in new[] { "catalog-00", "loaded-00" })
            {
                var original = ProviderModelsPinnedPresentation(12, 60, selectedId);
                var presentation = original with
                {
                    Models = original.Models.Select(model => model with
                    {
                        Configuration = new ProviderModelConfigurationPresentation(
                            ContextWindow: 8_192,
                            EffectiveContextWindow: 4_096,
                            ConfigurationIdentity: model.Id + "-configuration")
                    }).ToArray()
                };
                var control = HostProviderModelsOptimizationSurface(presentation, 1500, 780, out var host);
                var mutationEvents = 0;
                control.ConfigurationChanged += (_, _) => mutationEvents++;
                control.AssignmentChanged += (_, _) => mutationEvents++;
                control.LifecycleRequested += (_, _) => mutationEvents++;
                control.ConfigurationReloadRequested += (_, _) => mutationEvents++;
                try
                {
                    FlushProviderModelsDispatcher(host);
                    Require(control.SelectModel(selectedId), "the wheel fixture could not expand its selected model");
                    control.AdvancedSettings.IsExpanded = true;
                    FlushProviderModelsDispatcher(host);
                    if (selectedId == "catalog-00")
                    {
                        CaptureProviderModelsInlinePreview(control, "models-inline-available-wide.png");
                        host.Width = 960;
                        FlushProviderModelsDispatcher(host);
                        CaptureProviderModelsInlinePreview(control, "models-inline-available-compact.png");
                        host.Width = 1500;
                        FlushProviderModelsDispatcher(host);
                    }
                    var selectedList = selectedId.StartsWith("loaded", StringComparison.Ordinal)
                        ? control.LoadedList : control.CatalogList;
                    var otherList = ReferenceEquals(selectedList, control.LoadedList)
                        ? control.CatalogList : control.LoadedList;
                    var scroll = FindExperimentVisualDescendant<ScrollViewer>(selectedList)
                        ?? throw new InvalidOperationException("the selected list has no scroll viewer");
                    var otherScroll = FindExperimentVisualDescendant<ScrollViewer>(otherList)
                        ?? throw new InvalidOperationException("the peer list has no scroll viewer");
                    Require(scroll.ScrollableHeight > 100,
                        "the wheel fixture did not create meaningful scrolling room");
                    var contextLabel = FindProviderModelsDescendants<TextBlock>(control.DetailSurface)
                        .First(text => !control.AdvancedSettings.IsAncestorOf(text)
                            && text.Text.StartsWith("Context window", StringComparison.Ordinal));
                    var assignment = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.AssignmentTargets).First();
                    var metadata = FindProviderModelsDescendants<TextBlock>(control.AdvancedSettings)
                        .Single(text => text.Name == "SelectedModelMetadataText");
                    var editor = control.DetailSurface;
                    var editorHost = control.InlineEditorHost;
                    foreach (var target in new FrameworkElement[] { contextLabel, control.ContextWindowInput, assignment, metadata, control.HistoryPolicySelector })
                    {
                        target.BringIntoView();
                        FlushProviderModelsDispatcher(host);
                        if (ReferenceEquals(target, control.HistoryPolicySelector))
                        {
                            Require(control.HistoryPolicySelector.Focus() && !control.HistoryPolicySelector.IsDropDownOpen,
                                "the wheel fixture could not focus the closed history selector");
                            FlushProviderModelsDispatcher(host);
                        }
                        var choiceBefore = control.HistoryPolicySelector.SelectedIndex;
                        var otherOffset = otherScroll.VerticalOffset;
                        var before = scroll.VerticalOffset;
                        RaiseProviderModelsInlineWheel(target, -120);
                        FlushProviderModelsDispatcher(host);
                        var afterDown = scroll.VerticalOffset;
                        Require(afterDown > before,
                            $"wheel down over {target.GetType().Name}/{target.Name} did not advance {selectedId}'s model list ({before:0.#} to {afterDown:0.#})");
                        RaiseProviderModelsInlineWheel(target, 120);
                        FlushProviderModelsDispatcher(host);
                        Require(scroll.VerticalOffset < afterDown
                                && Math.Abs(otherScroll.VerticalOffset - otherOffset) <= 1
                                && control.SelectedModelId == selectedId
                                && ReferenceEquals(control.DetailSurface, editor)
                                && ReferenceEquals(control.InlineEditorHost, editorHost)
                                && control.HistoryPolicySelector.SelectedIndex == choiceBefore
                                && mutationEvents == 0,
                            "wheel reversal moved the wrong list, changed selection, detached the editor, or mutated model settings");
                    }
                    AssertProviderModelsInlineEditor(control);
                }
                finally
                {
                    host.Close();
                }
            }
        });
    }

    static void ProviderModelsInlineEditorKeepsContextVisibleAndEditsServerDefault()
    {
        RunStaTest(() =>
        {
            var source = ProviderModelsConfigurationPresentation();
            var presentation = source with
            {
                Models = source.Models.Select(model => model with
                {
                    Configuration = model.Configuration! with
                    {
                        ContextWindow = 0,
                        EffectiveContextWindow = model.Availability == ProviderModelAvailability.Loaded ? 16_384 : 0
                    }
                }).ToArray()
            };
            var control = HostProviderModelsOptimizationSurface(presentation, 1500, 920, out var host);
            var changes = new List<ProviderModelConfigurationChangedEventArgs>();
            control.ConfigurationChanged += (_, args) => changes.Add(args);
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(!control.AdvancedSettings.IsExpanded
                        && control.ContextWindowInput.IsVisible && control.ContextWindowInput.IsEnabled
                        && control.ContextWindowInput.Text == "16384"
                        && control.ProviderDefaultContextToggle.IsChecked == true
                        && control.ConfiguredContextEvidence.Text == "Using server default"
                        && control.EffectiveContextEvidence.Text == "Active: 16,384 tokens"
                        && control.ConfiguredContextEvidence.IsVisible && control.EffectiveContextEvidence.IsVisible
                        && !control.AdvancedSettings.IsAncestorOf(control.ContextWindowInput)
                        && !control.AdvancedSettings.IsAncestorOf(control.ConfiguredContextEvidence)
                        && !control.AdvancedSettings.IsAncestorOf(control.EffectiveContextEvidence),
                    "server-default context did not show its reported active value in an editable field outside Advanced");
                Require(control.ContextWindowInput.MinHeight is >= 32 and <= 36
                        && control.ContextWindowInput.ActualHeight <= 40,
                    $"the primary context input did not use the compact desktop target size (actual={control.ContextWindowInput.ActualHeight:0.##}, min={control.ContextWindowInput.MinHeight:0.##}, padding={control.ContextWindowInput.Padding}, font={control.ContextWindowInput.FontSize:0.##}, token={control.TryFindResource("Arena.Target.Minimum")})");
                Require(control.SelectModel("loaded-config", focusConfiguration: true),
                    "the context fixture could not focus the loaded model");
                FlushProviderModelsDispatcher(host);
                control.ContextWindowInput.Text = "32768";
                Require(control.ProviderDefaultContextToggle.IsChecked == false && changes.Count == 0,
                    "typing a context override did not leave server-default mode locally before committing");
                control.ApplyPresentation(presentation with { PresentationIdentity = "server-default-draft-poll" });
                FlushProviderModelsDispatcher(host);
                Require(control.ContextWindowInput.Text == "32768"
                        && control.ProviderDefaultContextToggle.IsChecked == false
                        && control.ContextWindowInput.IsKeyboardFocusWithin && changes.Count == 0,
                    "polling discarded or prematurely saved a typed server-default override");
                RaiseProviderModelsPreviewKey(control.ContextWindowInput, host, Key.Enter);
                Require(changes.Count == 1 && changes[0].ModelId == "loaded-config"
                        && changes[0].ContextWindow == 32_768
                        && control.SetConfigurationState(changes[0].ChangeId, ProviderModelConfigurationSaveState.Failed, "Rejected by fixture")
                        && control.ProviderDefaultContextToggle.IsChecked == true
                        && control.ContextWindowInput.Text == "16384",
                    "a typed context override did not commit causally and roll back to the reported server-default value on failure");
                control.ProviderDefaultContextToggle.IsChecked = false;
                Require(changes.Count == 2 && changes[1].ContextWindow == 16_384
                        && control.SetConfigurationState(changes[1].ChangeId, ProviderModelConfigurationSaveState.Saved),
                    "turning off server default ignored the known effective context and seeded an arbitrary minimum");
                Require(control.SelectModel("publisher/available-alias"),
                    "the unknown-context fixture could not select its available model");
                FlushProviderModelsDispatcher(host);
                Require(control.ProviderDefaultContextToggle.IsChecked == true && control.ContextWindowInput.IsEnabled,
                    "a model without active context evidence hid its editable server-default field");
                control.ProviderDefaultContextToggle.IsChecked = false;
                Require(changes.Count == 3 && changes[2].ContextWindow == 4_096
                        && control.ContextWindowInput.Text == "4096"
                        && control.SetConfigurationState(changes[2].ChangeId, ProviderModelConfigurationSaveState.Saved),
                    "turning off an unknown server default did not seed the bounded 4,096-token fallback");
                AssertProviderModelsInlineEditor(control);
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void RaiseProviderModelsInlineWheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent
        };
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Mouse.MouseWheelEvent;
            target.RaiseEvent(args);
        }
    }

    static void CaptureProviderModelsInlinePreview(ProviderModelAssignmentsControl control, string fileName)
    {
        var output = Environment.GetEnvironmentVariable("AIARENA_MODEL_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }
        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(control.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(control.ActualHeight)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }
}
