using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void ProviderModelsInlineEditorKeepsQualifiedSelectionAndOneSharedEditor()
    {
        RunStaTest(() =>
        {
            var source = ProviderModelsConfigurationPresentation();
            var presentation = source with
            {
                Models = source.Models.Select((model, index) => model with
                {
                    Id = index == 0 ? "server-a/model-shared" : "server-b/model-shared",
                    DisplayName = "Shared Model",
                    ModelIdentifier = "publisher/shared-model",
                    ProviderName = index == 0 ? "LM Studio" : "Ollama"
                }).ToArray(),
                SelectedModelId = "server-a/model-shared"
            };
            var control = HostProviderModelsOptimizationSurface(presentation, 1500, 920, out var host);
            var changes = new List<ProviderModelConfigurationChangedEventArgs>();
            ProviderModelAssignmentChangedEventArgs? assignment = null;
            control.ConfigurationChanged += (_, args) => changes.Add(args);
            control.AssignmentChanged += (_, args) => assignment = args;
            try
            {
                FlushProviderModelsDispatcher(host);
                var sharedEditor = control.DetailSurface;
                var sharedContextInput = control.ContextWindowInput;
                AssertProviderModelsInlineEditor(control);
                Require(!control.AdvancedSettings.IsExpanded
                        && control.ContextWindowInput.IsVisible
                        && control.AssignmentTargets.IsVisible,
                    "context and assignments were hidden behind the advanced settings disclosure");
                Require(!control.SelectModel("Shared Model") && !control.SelectModel("model-shared"),
                    "an ambiguous display name or leaf alias selected one of two server-qualified models");
                Require(control.SelectModel("server-a/model-shared", focusConfiguration: true),
                    "the first server-qualified model could not reveal its context editor");
                FlushProviderModelsDispatcher(host);
                control.ContextWindowInput.Text = "511";
                Require(control.ContextWindowInput.IsKeyboardFocusWithin,
                    "the qualified-model fixture did not establish a focused unsaved draft");
                Require(control.SelectModel("server-b/model-shared", focusConfiguration: true),
                    "the second server-qualified model could not reveal its context editor");
                FlushProviderModelsDispatcher(host);
                AssertProviderModelsInlineEditor(control);
                Require(ReferenceEquals(control.DetailSurface, sharedEditor)
                        && ReferenceEquals(control.ContextWindowInput, sharedContextInput)
                        && control.ContextWindowInput.Text == "4096"
                        && changes.Count == 0,
                    "moving the shared editor reused the previous model draft or emitted a cross-model save");

                control.ContextWindowInput.Text = "16384";
                RaiseProviderModelsPreviewKey(control.ContextWindowInput, host, Key.Enter);
                Require(changes.Count == 1
                        && changes[0].ModelId == "server-b/model-shared"
                        && changes[0].ConfigurationIdentity == "config-available-v1"
                        && changes[0].ContextWindow == 16_384,
                    "the inline context save did not retain its selected server-qualified model and configuration identity");
                Require(control.SetConfigurationState(changes[0].ChangeId, ProviderModelConfigurationSaveState.Saved),
                    "the inline configuration save could not complete with its own operation identity");
                var alpha = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.AssignmentTargets)
                    .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Alpha", StringComparison.Ordinal));
                ToggleProviderModelsCheckBox(alpha);
                FlushProviderModelsDispatcher(host);
                Require(assignment is { ModelId: "server-b/model-shared", TargetId: "alpha", IsAssigned: true }
                        && control.SetAssignmentState(assignment.ChangeId, ProviderAssignmentSaveState.Saved),
                    "the inline assignment did not retain the selected server-qualified model");

                var selectedRow = control.CatalogList.SelectedItem;
                control.ApplyPresentation(presentation with
                {
                    Models = presentation.Models.Select(model => model.Id == "server-b/model-shared"
                        ? model with { Availability = ProviderModelAvailability.Loaded, CanLoad = false, CanUnload = true }
                        : model).ToArray(),
                    SelectedModelId = "server-b/model-shared",
                    PresentationIdentity = "inline-qualified-loaded"
                });
                FlushProviderModelsDispatcher(host);
                AssertProviderModelsInlineEditor(control);
                Require(ReferenceEquals(control.LoadedList.SelectedItem, selectedRow)
                        && ReferenceEquals(control.DetailSurface, sharedEditor)
                        && ReferenceEquals(control.ContextWindowInput, sharedContextInput),
                    "a confirmed residency move replaced the selected row or duplicated the shared editor");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsInlineEditorPreservesUnsentDraftsDuringPolling()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsConfigurationPresentation();
            var control = HostProviderModelsOptimizationSurface(presentation, 1500, 920, out var host);
            var changes = new List<ProviderModelConfigurationChangedEventArgs>();
            control.ConfigurationChanged += (_, args) => changes.Add(args);
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(control.SelectModel("loaded-config", focusConfiguration: true),
                    "the draft fixture could not reveal the loaded context editor");
                FlushProviderModelsDispatcher(host);
                var editorHost = control.InlineEditorHost;
                foreach (var draft in new[] { "511", "16384" })
                {
                    control.ContextWindowInput.Text = draft;
                    for (var refresh = 0; refresh < 3; refresh++)
                    {
                        control.ApplyPresentation(presentation with
                        {
                            PresentationIdentity = $"inline-draft-{draft}-{refresh}"
                        });
                        FlushProviderModelsDispatcher(host);
                    }
                    Require(control.ContextWindowInput.Text == draft
                            && control.ContextWindowInput.IsKeyboardFocusWithin
                            && ReferenceEquals(control.InlineEditorHost, editorHost)
                            && changes.Count == 0
                            && !control.HasPendingConfiguration,
                        $"stable polling replaced, defocused, or saved the unsent context draft '{draft}'");
                }

                control.ContextWindowInput.Text = "8192";
                control.AdvancedSettings.IsExpanded = true;
                control.ResponseToneSelector.SelectedIndex = 6;
                control.CustomToneInput.BringIntoView();
                FlushProviderModelsDispatcher(host);
                Require(control.CustomToneInput.Focus(),
                    "the custom-tone draft fixture could not focus its initially blank local input");
                control.CustomToneInput.Text = "An unfinished custom tone instruction.";
                control.ApplyPresentation(presentation with { PresentationIdentity = "inline-custom-tone-poll" });
                FlushProviderModelsDispatcher(host);
                Require(control.ResponseToneSelector.SelectedIndex == 6
                        && control.CustomToneInput.Text == "An unfinished custom tone instruction."
                        && control.CustomToneInput.IsEnabled
                        && control.CustomToneInput.IsKeyboardFocusWithin
                        && changes.Count == 0,
                    "stable polling discarded, disabled, defocused, or saved the unsent custom-tone draft");

                control.ApplyPresentation(presentation with
                {
                    Models = presentation.Models.Select(model => model.Id == "loaded-config"
                        ? model with
                        {
                            Configuration = model.Configuration! with
                            {
                                ContextWindow = 4_096,
                                ConfigurationIdentity = "config-loaded-replaced"
                            }
                        }
                        : model).ToArray(),
                    PresentationIdentity = "inline-draft-authority-replaced"
                });
                FlushProviderModelsDispatcher(host);
                Require(control.ContextWindowInput.Text == "4096" && changes.Count == 0,
                    "a changed configuration identity retained or submitted an obsolete unsent draft");
                AssertProviderModelsInlineEditor(control);
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsInlineEditorKeepsEditorArrowKeysWithinTheEditor()
    {
        RunStaTest(() =>
        {
            var control = HostProviderModelsConfigurationSurface(out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                foreach (var (modelId, key) in new[]
                         {
                             ("loaded-config", Key.Down),
                             ("publisher/available-alias", Key.Up)
                         })
                {
                    Require(control.SelectModel(modelId, focusConfiguration: true),
                        "the keyboard fixture could not reveal the selected inline editor");
                    FlushProviderModelsDispatcher(host);
                    Require(control.ContextWindowInput.IsKeyboardFocusWithin,
                        "the inline context field did not receive keyboard focus");
                    RaiseProviderModelsInlineKey(control.ContextWindowInput, host, key);
                    FlushProviderModelsDispatcher(host);
                    Require(control.SelectedModelId == modelId && control.ContextWindowInput.IsKeyboardFocusWithin,
                        "an arrow key inside context editing was intercepted as navigation between model lists");
                    control.AdvancedSettings.IsExpanded = true;
                    control.HistoryPolicySelector.BringIntoView();
                    FlushProviderModelsDispatcher(host);
                    Require(control.HistoryPolicySelector.Focus(),
                        "the inline history selector could not receive keyboard focus");
                    RaiseProviderModelsInlineKey(control.HistoryPolicySelector, host, key);
                    FlushProviderModelsDispatcher(host);
                    Require(control.SelectedModelId == modelId && control.HistoryPolicySelector.IsKeyboardFocusWithin,
                        "an arrow key inside the history selector moved selection to another model");
                }
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsInlineEditorSurvivesVirtualizedRowRecycling()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsOptimizationPresentation(256, "model-121");
            var control = HostProviderModelsOptimizationSurface(presentation, 1500, 820, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                var selected = control.CatalogList.SelectedItem
                    ?? throw new InvalidOperationException("the recycle fixture had no selected catalog row");
                var sharedEditor = control.DetailSurface;
                control.CatalogList.ScrollIntoView(selected);
                FlushProviderModelsDispatcher(host);
                AssertProviderModelsInlineEditor(control);
                var editorHost = control.InlineEditorHost;
                control.SearchBox.Focus();
                control.CatalogList.ScrollIntoView(control.CatalogList.Items[control.CatalogList.Items.Count - 1]);
                FlushProviderModelsDispatcher(host);
                var realized = Enumerable.Range(0, control.CatalogList.Items.Count)
                    .Count(index => control.CatalogList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem);
                Require(realized > 0 && realized < control.CatalogList.Items.Count / 2
                        && ReferenceEquals(control.InlineEditorHost, editorHost)
                        && control.CatalogList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem,
                    $"scrolling detached the expanded editor or prevented viewport-bounded peer realization (realized={realized}/{control.CatalogList.Items.Count}, sameHost={ReferenceEquals(control.InlineEditorHost, editorHost)}, hostNull={control.InlineEditorHost is null}, selectedContainer={control.CatalogList.ItemContainerGenerator.ContainerFromItem(selected)?.GetType().Name ?? "null"}, selectedId={control.SelectedModelId}, selectedFocus={control.DetailSurface.IsKeyboardFocusWithin})");
                var canceledCleanup = 0;
                for (var index = 0; index < control.CatalogList.Items.Count; index++)
                {
                    if (control.CatalogList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
                    {
                        continue;
                    }
                    var cleanup = new CleanUpVirtualizedItemEventArgs(control.CatalogList.Items[index], container);
                    control.CatalogList.RaiseEvent(cleanup);
                    if (cleanup.Cancel)
                    {
                        canceledCleanup++;
                        Require(ReferenceEquals(control.CatalogList.Items[index], selected),
                            "an unselected peer was retained outside the virtualized viewport");
                    }
                }
                Require(canceledCleanup == 1,
                    $"the Models list retained {canceledCleanup} containers instead of only its expanded selected row");
                var scroll = FindExperimentVisualDescendant<ScrollViewer>(control.CatalogList)!;
                var offsetAfterScroll = scroll.VerticalOffset;
                control.ApplyPresentation(presentation with { PresentationIdentity = "retained-editor-offscreen-poll" });
                FlushProviderModelsDispatcher(host);
                Require(Math.Abs(scroll.VerticalOffset - offsetAfterScroll) <= 1,
                    "a stable poll moved the viewport after scrolling the expanded selected row out of view");
                control.CatalogList.ScrollIntoView(selected);
                FlushProviderModelsDispatcher(host);
                AssertProviderModelsInlineEditor(control);
                Require(ReferenceEquals(control.DetailSurface, sharedEditor)
                        && ReferenceEquals(control.CatalogList.SelectedItem, selected)
                        && control.SelectedModelId == "model-121",
                    "returning to a recycled selected row duplicated its editor or lost its qualified selection");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsInlineEditorCollapsesWithoutLosingSelectionAcrossPolling()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsConfigurationPresentation();
            var control = HostProviderModelsOptimizationSurface(presentation, 1500, 920, out var host);
            var mutationEvents = 0;
            control.ConfigurationChanged += (_, _) => mutationEvents++;
            control.AssignmentChanged += (_, _) => mutationEvents++;
            control.LifecycleRequested += (_, _) => mutationEvents++;
            control.ConfigurationReloadRequested += (_, _) => mutationEvents++;
            try
            {
                FlushProviderModelsDispatcher(host);
                var sharedEditor = control.DetailSurface;
                var selectedRow = control.LoadedList.SelectedItem;
                AssertProviderModelsInlineEditor(control);
                Require(control.FocusCatalog(), "the selected model header could not receive focus before collapse");
                var container = control.LoadedList.ItemContainerGenerator.ContainerFromItem(selectedRow) as ListBoxItem
                    ?? throw new InvalidOperationException("the collapse fixture selected row was not realized");
                RaiseProviderModelsInlineKey(container, host, Key.Space);
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == "loaded-config"
                        && ReferenceEquals(control.LoadedList.SelectedItem, selectedRow)
                        && !FindProviderModelsDescendants<Border>(control).Contains(sharedEditor)
                        && mutationEvents == 0,
                    "Space on the selected model header did not collapse its editor without changing selection or mutating the model");
                control.ApplyPresentation(presentation with { PresentationIdentity = "inline-collapse-poll" });
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == "loaded-config"
                        && !FindProviderModelsDescendants<Border>(control).Contains(sharedEditor)
                        && mutationEvents == 0,
                    "stable provider polling reopened the collapsed model editor or changed its selection");
                Require(control.FocusCatalog(), "the collapsed model header could not receive focus before reopening");
                container = control.LoadedList.ItemContainerGenerator.ContainerFromItem(selectedRow) as ListBoxItem
                    ?? throw new InvalidOperationException("the collapsed model row was not realized");
                RaiseProviderModelsInlineKey(container, host, Key.Enter);
                FlushProviderModelsDispatcher(host);
                AssertProviderModelsInlineEditor(control);
                Require(ReferenceEquals(control.DetailSurface, sharedEditor)
                        && control.SelectedModelId == "loaded-config"
                        && mutationEvents == 0,
                    "Enter on the selected model header did not reopen the same shared editor without mutating the model");

                foreach (var shouldExpand in new[] { false, true })
                {
                    container = control.LoadedList.ItemContainerGenerator.ContainerFromItem(selectedRow) as ListBoxItem
                        ?? throw new InvalidOperationException("the pointer collapse fixture row was not realized");
                    var header = FindProviderModelsDescendants<TextBlock>(container)
                        .First(text => !control.DetailSurface.IsAncestorOf(text)
                            && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "DisplayName");
                    // WPF derives PreviewMouseLeftButtonDown from the tunneling
                    // PreviewMouseDown event on each element along this real route.
                    header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.PreviewMouseDownEvent
                    });
                    FlushProviderModelsDispatcher(host);
                    Require(FindProviderModelsDescendants<Border>(control).Contains(sharedEditor) == shouldExpand
                            && control.SelectedModelId == "loaded-config"
                            && ReferenceEquals(control.LoadedList.SelectedItem, selectedRow)
                            && mutationEvents == 0,
                        $"clicking the model header did not {(shouldExpand ? "reopen" : "collapse")} its shared editor without changing selection or mutating the model");
                }
                AssertProviderModelsInlineEditor(control);
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void AssertProviderModelsInlineEditor(ProviderModelAssignmentsControl control)
    {
        var editorHost = control.InlineEditorHost
            ?? throw new InvalidOperationException("the selected model has no attached inline editor host");
        var selectedList = control.LoadedList.SelectedItem is not null ? control.LoadedList : control.CatalogList;
        var selected = selectedList.SelectedItem
            ?? throw new InvalidOperationException("the inline editor has no corresponding selected row");
        var row = selectedList.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem
            ?? throw new InvalidOperationException("the selected inline editor row is not realized");
        var hostModelId = editorHost.DataContext?.GetType().GetProperty("Id")?.GetValue(editorHost.DataContext)?.ToString();
        Require(ReferenceEquals(editorHost.Content, control.DetailSurface)
                && hostModelId == control.SelectedModelId
                && FindProviderModelsDescendants<ContentControl>(row).Contains(editorHost)
                && FindProviderModelsDescendants<Border>(control).Count(border => ReferenceEquals(border, control.DetailSurface)) == 1
                && editorHost.ActualHeight > 0
                && control.DetailSurface.ActualWidth <= selectedList.ActualWidth + 1,
            "the selected model did not contain exactly one shared editor with the same qualified row identity");
        var editorTop = control.DetailSurface.TranslatePoint(new Point(), row).Y;
        var headerBottom = FindProviderModelsDescendants<TextBlock>(row)
            .Where(text => !control.DetailSurface.IsAncestorOf(text)
                && (BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path is "DisplayName" or "Metadata"))
            .Select(text => text.TranslatePoint(new Point(0, text.ActualHeight), row).Y)
            .DefaultIfEmpty(0)
            .Max();
        Require(headerBottom > 0 && editorTop >= headerBottom - 1,
            $"the inline editor was not arranged beneath its selected model header (editor {editorTop:0.#}, header {headerBottom:0.#} DIP)");
    }

    static void RaiseProviderModelsInlineKey(UIElement target, Window host, Key key)
    {
        var source = PresentationSource.FromVisual(host)
            ?? throw new InvalidOperationException("the inline editor keyboard fixture has no presentation source");
        foreach (var (previewEvent, bubblingEvent) in new[]
                 {
                     (Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent),
                     (Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent)
                 })
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = previewEvent
            };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                args.RoutedEvent = bubblingEvent;
                target.RaiseEvent(args);
            }
        }
    }

    static void AssertProviderModelsInlineElementReachable(
        ProviderModelAssignmentsControl control,
        Window host,
        FrameworkElement element)
    {
        element.BringIntoView();
        FlushProviderModelsDispatcher(host);
        var selectedList = control.LoadedList.SelectedItem is not null ? control.LoadedList : control.CatalogList;
        var scroll = FindExperimentVisualDescendant<ScrollViewer>(selectedList)
            ?? throw new InvalidOperationException("the selected model list has no scroll viewer");
        var bounds = element.TransformToAncestor(scroll)
            .TransformBounds(new Rect(new Point(), element.RenderSize));
        Require(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1
                && bounds.Left >= -1 && bounds.Right <= scroll.ViewportWidth + 1,
            $"inline editor action is unreachable in its model list ({bounds} within {scroll.ViewportWidth:0.#}x{scroll.ViewportHeight:0.#})");
    }
}
