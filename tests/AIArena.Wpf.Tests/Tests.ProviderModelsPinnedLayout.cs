using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderModelsPinnedLayoutKeepsLoadedVisibleAndCatalogDisclosureStable()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsPinnedPresentation(loadedCount: 6, catalogCount: 40, "catalog-00");
            var control = HostProviderModelsPinnedSurface(presentation, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                var loadedScroll = FindExperimentVisualDescendant<ScrollViewer>(control.LoadedList)
                    ?? throw new InvalidOperationException("the pinned Loaded region did not expose its independent scroll viewer");
                var catalogScroll = FindExperimentVisualDescendant<ScrollViewer>(control.CatalogList)
                    ?? throw new InvalidOperationException("the Available catalog did not expose its independent scroll viewer");
                Require(control.LoadedList.Items.Count == 6
                        && control.CatalogList.Items.Count == 40
                        && control.LoadedList.MaxHeight == 174
                        && loadedScroll.ScrollableHeight > 0
                        && catalogScroll.ScrollableHeight > 0,
                    "the Models surface did not bound Loaded to three visible rows above an independently scrolling catalog");

                var peer = new ExpanderAutomationPeer(control.CatalogExpander);
                var disclosure = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                    ?? throw new InvalidOperationException("the Available catalog did not expose UIA ExpandCollapsePattern");
                Require(disclosure.ExpandCollapseState == ExpandCollapseState.Expanded,
                    "the Available catalog did not start expanded");
                disclosure.Collapse();
                FlushProviderModelsDispatcher(host);
                control.ApplyPresentation(presentation with
                {
                    PresentationIdentity = "pinned-layout-v2"
                });
                FlushProviderModelsDispatcher(host);
                Require(disclosure.ExpandCollapseState == ExpandCollapseState.Collapsed
                        && control.LoadedList.IsVisible,
                    "a provider refresh reset the catalog disclosure or hid Loaded models");

                control.SearchBox.Text = "catalog model 039";
                FlushProviderModelsSearchDebounce(host);
                Require(control.LoadedList.Items.Count == 6
                        && control.CatalogList.Items.Count == 1
                        && control.FilteredCount.Text == "1 of 40",
                    "catalog search filtered the mandatory Loaded region or reported the wrong catalog denominator");
                disclosure.Expand();
                FlushProviderModelsDispatcher(host);
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsPinnedLayoutMovesSelectionAndUsesAccessibleSwitches()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsPinnedPresentation(loadedCount: 2, catalogCount: 4, "catalog-00");
            var control = HostProviderModelsPinnedSurface(presentation, out var host);
            ProviderModelAssignmentChangedEventArgs? assignment = null;
            control.AssignmentChanged += (_, args) => assignment = args;
            try
            {
                FlushProviderModelsDispatcher(host);
                var selectedRow = control.CatalogList.SelectedItem
                    ?? throw new InvalidOperationException("the selected catalog model was unavailable");
                var movedModels = presentation.Models.Select(model => model.Id == "catalog-00"
                    ? model with
                    {
                        Availability = ProviderModelAvailability.Loaded,
                        Status = "Loaded",
                        CanLoad = false,
                        CanUnload = true,
                        AssignedTargetIds = ["default"]
                    }
                    : model).ToArray();
                control.ApplyPresentation(presentation with
                {
                    Models = movedModels,
                    PresentationIdentity = "pinned-move-v2"
                });
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == "catalog-00"
                        && ReferenceEquals(control.LoadedList.SelectedItem, selectedRow)
                        && !control.CatalogList.Items.Cast<object>().Contains(selectedRow)
                        && control.FocusCatalog()
                        && control.LoadedList.IsKeyboardFocusWithin,
                    "a confirmed catalog-to-Loaded move lost row identity, selection, or keyboard focus");

                var defaultSwitch = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface)
                    .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Default", StringComparison.Ordinal));
                var togglePeer = new CheckBoxAutomationPeer(defaultSwitch);
                var togglePattern = togglePeer.GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("the Default assignment switch did not expose UIA TogglePattern");
                Require(defaultSwitch.IsChecked == true
                        && defaultSwitch.IsEnabled
                        && defaultSwitch.MinHeight >= 44
                        && AutomationProperties.GetItemStatus(defaultSwitch) == "Default"
                        && AutomationProperties.GetHelpText(defaultSwitch).Contains("Turn off", StringComparison.Ordinal),
                    "the active Default route was not exposed as an enabled, truthful assignment switch");

                togglePattern.Toggle();
                FlushProviderModelsDispatcher(host);
                Require(assignment is not null
                        && assignment.TargetId == "default"
                        && !assignment.IsAssigned
                        && control.HasPendingAssignment
                        && control.SearchBox.IsEnabled
                        && control.LoadedList.IsEnabled
                        && control.CatalogList.IsEnabled
                        && control.CatalogExpander.IsEnabled
                        && FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface)
                            .All(toggle => !toggle.IsEnabled),
                    "turning off Default did not publish one causal request while keeping catalog inspection available");
                Require(control.SetAssignmentState(assignment!.ChangeId, ProviderAssignmentSaveState.Saved)
                        && defaultSwitch.IsChecked == false
                        && AutomationProperties.GetItemStatus(defaultSwitch) == "Unassigned"
                        && AutomationProperties.GetHelpText(defaultSwitch).Contains("Turn on", StringComparison.Ordinal),
                    "the saved Default-off state did not remain visibly and accessibly unassigned");

                control.LoadedList.SelectedIndex = control.LoadedList.Items.Count - 1;
                control.LoadedList.ScrollIntoView(control.LoadedList.SelectedItem);
                FlushProviderModelsDispatcher(host);
                Require(control.FocusCatalog(), "the last Loaded row could not receive keyboard focus");
                var loadedContainer = control.LoadedList.ItemContainerGenerator.ContainerFromItem(
                    control.LoadedList.SelectedItem) as ListBoxItem
                    ?? throw new InvalidOperationException("the last Loaded row did not realize for keyboard navigation");
                RaiseProviderModelsPreviewKey(loadedContainer, host, System.Windows.Input.Key.Down);
                FlushProviderModelsDispatcher(host);
                Require(control.CatalogList.IsKeyboardFocusWithin
                        && ReferenceEquals(control.CatalogList.SelectedItem, control.CatalogList.Items[0]),
                    "Down from the last Loaded row did not enter the first Available catalog row");
                var catalogContainer = control.CatalogList.ItemContainerGenerator.ContainerFromItem(
                    control.CatalogList.SelectedItem) as ListBoxItem
                    ?? throw new InvalidOperationException("the first catalog row did not realize for reverse keyboard navigation");
                RaiseProviderModelsPreviewKey(catalogContainer, host, System.Windows.Input.Key.Up);
                FlushProviderModelsDispatcher(host);
                Require(control.LoadedList.IsKeyboardFocusWithin
                        && control.LoadedList.SelectedIndex == control.LoadedList.Items.Count - 1,
                    "Up from the first Available catalog row did not return to the last Loaded row");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static ProviderModelAssignmentsControl HostProviderModelsPinnedSurface(
        ProviderModelAssignmentsPresentation presentation,
        out Window host)
    {
        var control = new ProviderModelAssignmentsControl();
        AttachArenaPresentationResources(control);
        ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
        control.ApplyPresentation(presentation);
        host = new Window
        {
            Content = control,
            Width = 1280,
            Height = 780,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Show();
        return control;
    }

    static ProviderModelAssignmentsPresentation ProviderModelsPinnedPresentation(
        int loadedCount,
        int catalogCount,
        string selectedModelId)
    {
        var loaded = Enumerable.Range(0, loadedCount).Select(index =>
            new ProviderModelAssignmentPresentation(
                $"loaded-{index:00}",
                $"Loaded model {index:00}",
                ProviderModelAvailability.Loaded,
                "Loaded",
                "publisher · Q4 · 16k context",
                [],
                CanUnload: true));
        var catalog = Enumerable.Range(0, catalogCount).Select(index =>
            new ProviderModelAssignmentPresentation(
                $"catalog-{index:00}",
                $"Catalog model {index:000}",
                index % 4 == 3 ? ProviderModelAvailability.Unavailable : ProviderModelAvailability.Available,
                index % 4 == 3 ? "Load state unavailable" : "Available",
                "publisher · Q4 · 16k context",
                [],
                CanLoad: index % 4 != 3));
        var models = loaded.Concat(catalog).ToArray();
        return new ProviderModelAssignmentsPresentation(
            "LM Studio",
            "Online",
            ProviderConnectionState.Online,
            $"{loadedCount} loaded, {catalogCount} catalog models.",
            models,
            [
                new ProviderAssignmentTargetPresentation(
                    "default",
                    "Default",
                    IsEnabled: true,
                    AssignedState: ProviderTargetAssignmentState.Default,
                    IsDefault: true),
                new ProviderAssignmentTargetPresentation("alpha", "Alpha")
            ],
            selectedModelId,
            PresentationIdentity: "pinned-layout-v1",
            CanRunLifecycle: true,
            ConnectionIdentity: "pinned-layout-connection-v1");
    }
}
