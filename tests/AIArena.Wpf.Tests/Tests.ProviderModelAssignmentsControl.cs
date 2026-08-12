using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderModelAssignmentsControlHostsResponsiveAccessibleContract()
    {
        RunStaTest(() =>
        {
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            control.ApplyPresentation(ProviderModelsPresentation(modelCount: 240));
            var host = new Window
            {
                Content = control,
                Width = 1500,
                Height = 860,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            host.Show();
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(!control.UsesCompactLayout
                        && Grid.GetRow(control.MasterSurface) == 0
                        && Grid.GetColumn(control.MasterSurface) == 0
                        && Grid.GetRow(control.DetailSurface) == 0
                        && Grid.GetColumn(control.DetailSurface) == 2
                        && control.FacetFilter.Text == "All catalog",
                    "1500-DIP hosted Models surface did not preserve the wide master/detail layout");
                var paneWidth = control.MasterSurface.ActualWidth + control.DetailSurface.ActualWidth;
                var masterShare = paneWidth <= 0 ? 0 : control.MasterSurface.ActualWidth / paneWidth;
                Require(masterShare is >= 0.74 and <= 0.80
                        && control.DetailSurface.ActualWidth <= 380,
                    $"wide Models surface did not preserve the catalog-first fixed detail rail (master share {masterShare:P1}, detail {control.DetailSurface.ActualWidth:0.#} DIP)");

                var view = CollectionViewSource.GetDefaultView(control.CatalogList.ItemsSource);
                var groups = view.Groups?.Cast<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray() ?? [];
                Require(groups.SequenceEqual(["Available Models", "Load State Unavailable"])
                        && control.LoadedList.Items.Count == 80
                        && control.CatalogList.Items.Count == 160,
                    "provider models were not split into pinned Loaded and grouped Available catalog regions");
                Require(VirtualizingPanel.GetIsVirtualizing(control.CatalogList)
                        && VirtualizingPanel.GetIsVirtualizingWhenGrouping(control.CatalogList)
                        && VirtualizingPanel.GetVirtualizationMode(control.CatalogList) == VirtualizationMode.Recycling
                        && ScrollViewer.GetCanContentScroll(control.CatalogList)
                        && ScrollViewer.GetHorizontalScrollBarVisibility(control.CatalogList) == ScrollBarVisibility.Disabled,
                    "grouped model inventory weakened recycling virtualization or introduced horizontal scrolling");
                control.CatalogList.ApplyTemplate();
                var realizedPanel = FindExperimentVisualDescendant<VirtualizingStackPanel>(control.CatalogList);
                var realizedCount = Enumerable.Range(0, control.CatalogList.Items.Count)
                    .Count(index => control.CatalogList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem);
                Require(realizedPanel is not null && realizedCount > 0 && realizedCount < control.CatalogList.Items.Count,
                    "hosted model inventory did not retain bounded realization while grouped");

                Require(AutomationProperties.GetName(control) == "Models and assignments"
                        && !control.Focusable
                        && AutomationProperties.GetName(control.LoadedList) == "Loaded provider models"
                        && AutomationProperties.GetName(control.CatalogList) == "Available provider model catalog"
                        && AutomationProperties.GetName(control.MasterSurface) == "Provider models master list"
                        && AutomationProperties.GetName(control.DetailSurface) == "Selected model residency and assignment details"
                        && AutomationProperties.GetLiveSetting(control.CatalogStatus) == AutomationLiveSetting.Polite
                        && AutomationProperties.GetLiveSetting(control.LifecycleStatus) == AutomationLiveSetting.Polite,
                    "Models surface did not expose its master/detail and live-status automation contract");
                Require(control.LifecycleAction.Content?.ToString() == "Unload model"
                        && control.LifecycleAction.IsEnabled
                        && AutomationProperties.GetName(control.LifecycleAction).StartsWith("Unload ", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(control.LifecycleAction))
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Ready"
                        && control.LifecycleAction.FocusVisualStyle is not null
                        && ProviderModelsElementFits(control.LifecycleAction, control.DetailSurface),
                    "selected loaded model did not expose a bounded accessible Unload action");
                control.SearchBox.Text = "unavailable-002";
                FlushProviderModelsSearchDebounce(host);
                var unavailableContainer = control.CatalogList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                Require(control.CatalogList.Items.Count == 1
                        && control.SelectedModelId == "unavailable-002"
                        && !control.LifecycleAction.IsEnabled
                        && control.LifecycleAction.Content?.ToString() == "Load state unavailable"
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Unavailable"
                        && unavailableContainer is not null
                        && AutomationProperties.GetName(unavailableContainer).Contains("Load state unavailable", StringComparison.OrdinalIgnoreCase)
                        && AutomationProperties.GetItemStatus(unavailableContainer) == "Load state unavailable",
                    "load-state-unavailable group did not retain a non-actionable accessible row contract");
                control.SearchBox.Clear();
                FlushProviderModelsSearchDebounce(host);
                control.CatalogList.SelectedItem = control.CatalogList.Items.Cast<object>()
                    .Single(item => item.GetType().GetProperty("Id")?.GetValue(item)?.ToString() == "available-001");
                FlushProviderModelsDispatcher(host);
                Require(control.AssignmentTargets.Items.Count == 3,
                    "Models surface did not materialize dynamic assignment targets");
                var assignmentCheckBoxes = FindProviderModelsDescendants<CheckBox>(control.DetailSurface).ToArray();
                Require(assignmentCheckBoxes.Length == 3
                        && assignmentCheckBoxes.All(checkBox =>
                            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(checkBox))
                            && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(checkBox))
                            && checkBox.FocusVisualStyle is not null),
                    "dynamic assignment checkboxes did not retain accessible names, help, and visible focus");
                var assignmentTargetsPeer = UIElementAutomationPeer.CreatePeerForElement(control.AssignmentTargets)
                    ?? new FrameworkElementAutomationPeer(control.AssignmentTargets);
                Require(assignmentTargetsPeer.GetChildren() is { Count: > 0 },
                    "assignment target automation group did not expose its realized controls");

                foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
                {
                    var theme = ThemePalette.Resolve(themeId);
                    ApplyExperimentSurfaceTheme(control, theme);
                    FlushProviderModelsDispatcher(host);
                    Require(ExperimentBrushMatches(control.MasterSurface.Background, theme.Panel)
                            && ExperimentBrushMatches(control.DetailSurface.Background, theme.Panel)
                            && ExperimentBrushMatches(control.ConnectionStatusSurface.BorderBrush, theme.StatusSuccess)
                            && ExperimentBrushMatches(control.ConnectionStatusLabel.Foreground, theme.StatusSuccess)
                            && control.CatalogList.FocusVisualStyle is not null,
                        $"Models surface fell through shared panel or focus styling under {themeId}");
                }

                SystemMotionPreferences.SetQaAnimationsEnabledOverride(false);
                try
                {
                    host.Width = 960;
                    FlushProviderModelsDispatcher(host);
                    Require(control.UsesCompactLayout
                            && Grid.GetRow(control.MasterSurface) == 0
                            && Grid.GetColumnSpan(control.MasterSurface) == 3
                            && Grid.GetRow(control.DetailSurface) == 2
                            && Grid.GetColumnSpan(control.DetailSurface) == 3,
                        "960-DIP hosted Models surface did not stack master before detail under reduced motion");
                    Require(ProviderModelsElementFits(control.LifecycleAction, control.DetailSurface),
                        "stacked Models lifecycle action clipped outside the compact detail pane");
                    var scrollHost = RequireExperimentTemplatePart<ScrollViewer>(control.CatalogList, "ScrollHost");
                    Require(scrollHost.ViewportWidth > 0
                            && scrollHost.ExtentWidth <= scrollHost.ViewportWidth + 1,
                        "stacked Models surface introduced avoidable horizontal overflow");
                }
                finally
                {
                    SystemMotionPreferences.ClearQaOverride();
                }

                host.Width = 1500;
                FlushProviderModelsDispatcher(host);
                Require(!control.UsesCompactLayout,
                    "host-window viewport listener did not restore 72/28 layout after returning to 1500 DIP");
                Require(control.FocusSearch() && control.SearchBox.IsKeyboardFocusWithin,
                    "Models search was not keyboard focusable");
                Require(control.FocusCatalog()
                        && (control.CatalogList.IsKeyboardFocusWithin || control.LoadedList.IsKeyboardFocusWithin),
                    "selected model inventory region was not keyboard focusable");
            }
            finally
            {
                host.Close();
                SystemMotionPreferences.ClearQaOverride();
            }

            var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ProviderModelAssignmentsControl.xaml"));
            var codeBehind = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ProviderModelAssignmentsControl.xaml.cs"));
            Require(!xaml.Contains("Storyboard", StringComparison.Ordinal)
                    && !xaml.Contains("Animation", StringComparison.Ordinal)
                    && !xaml.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                    && !xaml.Contains("Yi Coder", StringComparison.OrdinalIgnoreCase)
                    && !codeBehind.Contains("Yi Coder", StringComparison.OrdinalIgnoreCase),
                "Models surface introduced layout-affecting motion, hardware-placement UI, or a product mock model literal");
            Require(ProviderModelAssignmentsControl.UsesCompactLayoutAt(960, 960)
                    && !ProviderModelAssignmentsControl.UsesCompactLayoutAt(1000, 1500)
                    && ProviderModelAssignmentsControl.UsesCompactLayoutAt(1000, 1500, contentIsScaled: true)
                    && ProviderModelAssignmentsControl.UsesCompactLayoutAt(960)
                    && !ProviderModelAssignmentsControl.UsesCompactLayoutAt(1500),
                "Models responsive policy did not honor either the shell viewport or transformed content width");
        });
    }

    static void ProviderModelAssignmentsControlRaisesImmediateTruthfulEvents()
    {
        RunStaTest(() =>
        {
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            control.ApplyPresentation(ProviderModelsPresentation(modelCount: 2));
            var host = new Window
            {
                Content = control,
                Width = 1300,
                Height = 760,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            ProviderModelAssignmentChangedEventArgs? assignment = null;
            var refreshCount = 0;
            var connectionCount = 0;
            var closeCount = 0;
            string? searchQuery = null;
            control.AssignmentChanged += (_, args) => assignment = args;
            control.RefreshRequested += (_, _) => refreshCount++;
            control.ConnectionSettingsRequested += (_, _) => connectionCount++;
            control.CloseRequested += (_, _) => closeCount++;
            control.SearchChanged += (_, args) => searchQuery = args.Query;

            host.Show();
            try
            {
                FlushProviderModelsDispatcher(host);
                control.RefreshAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                control.ConnectionAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                control.CloseAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(refreshCount == 1 && connectionCount == 1 && closeCount == 1,
                    "Models surface did not forward refresh, connection, and close requests exactly once");
                RaiseProviderModelsPreviewKey(control.SearchBox, host, Key.F5);
                RaiseProviderModelsPreviewKey(control.SearchBox, host, Key.Escape);
                Require(refreshCount == 2 && closeCount == 2,
                    "Models surface did not expose refresh and close through its keyboard contract");

                control.SearchBox.Text = "available";
                FlushProviderModelsSearchDebounce(host);
                Require(searchQuery == "available" && control.CatalogList.Items.Count == 1,
                    "Models search did not filter locally and publish its exact query");
                control.SearchBox.Clear();
                FlushProviderModelsSearchDebounce(host);
                control.CatalogList.SelectedItem = control.CatalogList.Items.Cast<object>()
                    .Single(item => item.GetType().GetProperty("Id")?.GetValue(item)?.ToString() == "available-001");
                FlushProviderModelsDispatcher(host);

                var alpha = FindProviderModelsDescendants<CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox).EndsWith("Alpha", StringComparison.Ordinal));
                ToggleProviderModelsCheckBox(alpha);
                Require(assignment is not null,
                    "assignment checkbox did not publish an immediate request");
                var alphaAssignment = assignment!;
                Require(alphaAssignment.ModelId == "available-001"
                        && alphaAssignment.TargetId == "alpha"
                        && alphaAssignment.IsAssigned,
                    $"assignment checkbox published the wrong route ({alphaAssignment.ModelId}, {alphaAssignment.TargetId}, {alphaAssignment.IsAssigned})");
                Require(control.HasPendingAssignment
                        && control.SearchBox.IsEnabled
                        && control.CatalogList.IsEnabled
                        && control.LoadedList.IsEnabled
                        && control.AssignmentStatus.Text.StartsWith("Saving", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.AssignmentStatus) == "Saving",
                    $"assignment checkbox did not expose truthful Saving state ({control.HasPendingAssignment}, {control.SearchBox.IsEnabled}, '{control.AssignmentStatus.Text}', '{AutomationProperties.GetItemStatus(control.AssignmentStatus)}')");
                Require(control.SetAssignmentState(alphaAssignment.ChangeId, ProviderAssignmentSaveState.Saved)
                        && alpha.IsChecked == true
                        && !control.HasPendingAssignment
                        && control.SearchBox.IsEnabled
                        && control.AssignmentStatus.Text.StartsWith("Saved", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.AssignmentStatus) == "Saved",
                    "successful assignment resolution did not retain the checked value and publish Saved state");
                Require(alpha.DataContext?.GetType().GetProperty("AssignmentState")?.GetValue(alpha.DataContext)?.ToString() == "Explicit",
                    "checking an agent route did not expose the optimistic Explicit assignment state");
                var light = ThemePalette.Resolve("light");
                ApplyExperimentSurfaceTheme(control, light);
                FlushProviderModelsDispatcher(host);
                Require(ExperimentBrushMatches(control.AssignmentStatusSurface.BorderBrush, light.StatusSuccess)
                        && ExperimentBrushMatches(control.AssignmentStatus.Foreground, light.StatusSuccess),
                    "Saved assignment state did not follow the active theme's semantic success brushes");
                Require(!control.SetAssignmentState(Guid.NewGuid(), ProviderAssignmentSaveState.Saved),
                    "stale assignment completion mutated the current Models surface");

                assignment = null;
                var defaultTarget = FindProviderModelsDescendants<CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox).EndsWith("Default", StringComparison.Ordinal));
                ToggleProviderModelsCheckBox(defaultTarget);
                Require(assignment is not null
                        && assignment.TargetId == "default"
                        && assignment.IsAssigned
                        && defaultTarget.DataContext?.GetType().GetProperty("AssignmentState")?.GetValue(defaultTarget.DataContext)?.ToString() == "Default",
                    "checking Default did not expose the optimistic Default state while saving");
                Require(control.SetAssignmentState(
                            assignment!.ChangeId,
                            ProviderAssignmentSaveState.Failed,
                            "Rollback the transition fixture")
                        && defaultTarget.IsChecked == false,
                    "the Default transition fixture did not roll back causally");

                assignment = null;
                FlushProviderModelsDispatcher(host);
                var beta = FindProviderModelsDescendants<CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox).EndsWith("Beta", StringComparison.Ordinal));
                ToggleProviderModelsCheckBox(beta);
                Require(assignment is not null && control.HasPendingAssignment,
                    "second assignment did not establish a new causal save boundary");
                Require(control.SetAssignmentState(
                            assignment!.ChangeId,
                            ProviderAssignmentSaveState.Failed,
                            "Provider rejected assignment")
                        && beta.IsChecked == false
                        && !control.HasPendingAssignment
                        && control.AssignmentStatus.Text.StartsWith("Failed:", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.AssignmentStatus) == "Failed",
                    "failed assignment did not roll back the optimistic checkbox and publish Failed state");
                var highContrast = ThemePalette.Resolve("high-contrast");
                ApplyExperimentSurfaceTheme(control, highContrast);
                FlushProviderModelsDispatcher(host);
                Require(ExperimentBrushMatches(control.AssignmentStatusSurface.BorderBrush, highContrast.DangerBorder)
                        && ExperimentBrushMatches(control.AssignmentStatus.Foreground, highContrast.DangerText),
                    "Failed assignment state did not follow the active theme's semantic danger brushes");

                assignment = null;
                var staleBeta = FindProviderModelsDescendants<CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox).EndsWith("Beta", StringComparison.Ordinal));
                ToggleProviderModelsCheckBox(staleBeta);
                Require(assignment is not null && control.HasPendingAssignment,
                    "provider identity regression did not establish a pending assignment");
                var staleChangeId = assignment!.ChangeId;
                control.ApplyPresentation(ProviderModelsPresentation(modelCount: 2) with
                {
                    PresentationIdentity = "provider-test-v2",
                    SelectedModelId = control.SelectedModelId
                });
                Require(control.HasPendingAssignment
                        && control.SetAssignmentState(
                            staleChangeId,
                            ProviderAssignmentSaveState.Saving,
                            "Saving after catalog refresh…")
                        && control.AssignmentStatus.Text.Contains("catalog refresh", StringComparison.OrdinalIgnoreCase),
                    "catalog/default-model fingerprint churn cancelled a valid pending assignment on the same connection");

                control.ApplyPresentation(ProviderModelsPresentation(modelCount: 2) with
                {
                    PresentationIdentity = "provider-test-v3",
                    ConnectionIdentity = "connection-test-v2",
                    SelectedModelId = control.SelectedModelId
                });
                Require(!control.HasPendingAssignment
                        && !control.SetAssignmentState(staleChangeId, ProviderAssignmentSaveState.Saved)
                        && control.AssignmentStatus.Text.Contains("provider or session changed", StringComparison.OrdinalIgnoreCase),
                    "provider/session replacement accepted a stale assignment completion");

                var aliasPresentation = ProviderModelsPresentation(modelCount: 2);
                control.ApplyPresentation(aliasPresentation with
                {
                    Models = aliasPresentation.Models.Select(model => model.Id == "loaded-000"
                        ? model with { AssignedTargetIds = ["default", "alpha"] }
                        : model).ToArray(),
                    SelectedModelId = "loaded-000",
                    PresentationIdentity = "provider-test-alias-clear"
                });
                FlushProviderModelsDispatcher(host);
                assignment = null;
                var aliasAlpha = FindProviderModelsDescendants<CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox).EndsWith("Alpha", StringComparison.Ordinal));
                Require(aliasAlpha.IsChecked == true && aliasAlpha.IsEnabled,
                    "an explicit alias-equivalent route was not actionable on the selected Default row");
                ToggleProviderModelsCheckBox(aliasAlpha);
                Require(assignment is not null
                        && !assignment.IsAssigned
                        && aliasAlpha.DataContext?.GetType().GetProperty("AssignmentState")?.GetValue(aliasAlpha.DataContext)?.ToString() == "InheritsDefault",
                    "clearing an alias-equivalent explicit route did not immediately expose Uses default");
                Require(control.SetAssignmentState(assignment!.ChangeId, ProviderAssignmentSaveState.Saved)
                        && aliasAlpha.IsChecked == false
                        && AutomationProperties.GetItemStatus(aliasAlpha) == "Uses default",
                    "the cleared alias route did not retain inherited-default state after save");

                control.ApplyPresentation(ProviderModelsPresentation(modelCount: 2) with
                {
                    IsRefreshing = true,
                    CatalogStatus = "Refreshing provider model evidence.",
                    Models = []
                });
                Require(!control.RefreshAction.IsEnabled
                        && control.CatalogList.IsEnabled
                        && control.LoadedList.IsEnabled
                        && control.CatalogList.Items.Count == 1
                        && control.LoadedList.Items.Count == 1
                        && control.AssignmentTargets.Items.Count == 3
                        && FindProviderModelsDescendants<CheckBox>(control.DetailSurface).All(checkBox => !checkBox.IsEnabled)
                        && control.AssignmentStatus.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                        && AutomationProperties.GetItemStatus(control.CatalogStatus) == "Refreshing",
                    "catalog refresh disabled inspection, enabled stale assignments, or replaced loaded content with a misleading empty state");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelAssignmentsControlTransfersExclusiveTargetsOptimistically()
    {
        RunStaTest(() =>
        {
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            var baseline = ProviderModelsPresentation(modelCount: 3);
            var original = baseline with
            {
                Models = baseline.Models.Select(model => model.Id == "loaded-000"
                    ? model with { AssignedTargetIds = ["default", "alpha"] }
                    : model with { AssignedTargetIds = [] }).ToArray(),
                SelectedModelId = "available-001"
            };
            control.ApplyPresentation(original);
            var host = new Window
            {
                Content = control,
                Width = 1300,
                Height = 760,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            ProviderModelAssignmentChangedEventArgs? assignment = null;
            var assignmentEventCount = 0;
            control.AssignmentChanged += (_, args) =>
            {
                assignment = args;
                assignmentEventCount++;
            };

            IEnumerable<object> Rows() => control.LoadedList.Items.Cast<object>()
                .Concat(control.CatalogList.Items.Cast<object>());
            object Row(string id) => Rows().Single(row => string.Equals(
                row.GetType().GetProperty("Id")?.GetValue(row)?.ToString(),
                id,
                StringComparison.Ordinal));
            bool IsAssigned(string modelId, string targetId) =>
                Row(modelId).GetType().GetProperty("AssignedTargetIds")?.GetValue(Row(modelId))
                    is IEnumerable<string> assigned
                && assigned.Contains(targetId, StringComparer.Ordinal);
            int OwnerCount(string targetId) => Rows().Count(row =>
                row.GetType().GetProperty("AssignedTargetIds")?.GetValue(row)
                    is IEnumerable<string> assigned
                && assigned.Contains(targetId, StringComparer.Ordinal));
            void Select(string id)
            {
                var row = Row(id);
                var list = control.LoadedList.Items.Contains(row)
                    ? control.LoadedList
                    : control.CatalogList;
                list.SelectedItem = row;
                FlushProviderModelsDispatcher(host);
            }
            ProviderAssignmentCheckBox Target(string displayName) =>
                FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox)
                        .EndsWith(displayName, StringComparison.Ordinal));

            host.Show();
            try
            {
                FlushProviderModelsDispatcher(host);

                // Success: ownership moves immediately and publishes one persistence request.
                var alpha = Target("Alpha");
                ToggleProviderModelsCheckBox(alpha);
                Require(assignment is { ModelId: "available-001", TargetId: "alpha", IsAssigned: true }
                        && assignmentEventCount == 1,
                    "an exclusive Alpha move did not publish exactly one assignment request");
                Require(OwnerCount("alpha") == 1
                        && !IsAssigned("loaded-000", "alpha")
                        && IsAssigned("available-001", "alpha"),
                    "the pending Alpha move exposed duplicate or stale model ownership");
                Require(control.AssignmentStatus.Text.Contains(
                            "Moving Alpha from Loaded model 000 to Available model 001",
                            StringComparison.Ordinal)
                        && FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface)
                            .All(checkBox => !checkBox.IsEnabled),
                    "the pending Alpha move did not use truthful transfer copy or disable mutation switches");
                Require(control.SetAssignmentState(
                            assignment!.ChangeId,
                            ProviderAssignmentSaveState.Saved,
                            "Agent model assignment saved.")
                        && OwnerCount("alpha") == 1
                        && IsAssigned("available-001", "alpha")
                        && control.AssignmentStatus.Text.Contains("Moved Alpha", StringComparison.Ordinal),
                    "a successful exclusive Alpha move did not retain its single destination");

                // Failure restores the exact pre-request owners.
                control.ApplyPresentation(original);
                FlushProviderModelsDispatcher(host);
                assignment = null;
                Select("available-001");
                ToggleProviderModelsCheckBox(Target("Alpha"));
                Require(OwnerCount("alpha") == 1 && IsAssigned("available-001", "alpha"),
                    "the failure fixture did not establish an optimistic single-owner move");
                Require(control.SetAssignmentState(
                            assignment!.ChangeId,
                            ProviderAssignmentSaveState.Failed,
                            "Conflict")
                        && OwnerCount("alpha") == 1
                        && IsAssigned("loaded-000", "alpha")
                        && !IsAssigned("available-001", "alpha"),
                    "a failed exclusive Alpha move did not restore every affected row");

                // A same-connection refresh updates rollback authority while the overlay
                // remains duplicate-free. Here an external writer moves Alpha to model C.
                control.ApplyPresentation(original);
                FlushProviderModelsDispatcher(host);
                assignment = null;
                Select("available-001");
                ToggleProviderModelsCheckBox(Target("Alpha"));
                var staleChangeId = assignment!.ChangeId;
                var externalAuthority = original with
                {
                    PresentationIdentity = "provider-test-external-authority",
                    Models = original.Models.Select(model => model with
                    {
                        AssignedTargetIds = model.Id switch
                        {
                            "loaded-000" => ["default"],
                            "unavailable-002" => ["alpha"],
                            _ => []
                        }
                    }).ToArray()
                };
                control.ApplyPresentation(externalAuthority);
                Require(control.HasPendingAssignment
                        && OwnerCount("alpha") == 1
                        && IsAssigned("available-001", "alpha"),
                    "a same-connection refresh leaked its external owner through the pending overlay");
                Require(control.SetAssignmentState(
                            staleChangeId,
                            ProviderAssignmentSaveState.Failed,
                            "Concurrent route update")
                        && OwnerCount("alpha") == 1
                        && IsAssigned("unavailable-002", "alpha")
                        && !IsAssigned("loaded-000", "alpha")
                        && !IsAssigned("available-001", "alpha"),
                    "failure did not restore the newest same-connection assignment authority");

                // A replacement connection discards the old overlay and stands on its own.
                control.ApplyPresentation(original);
                FlushProviderModelsDispatcher(host);
                assignment = null;
                Select("available-001");
                ToggleProviderModelsCheckBox(Target("Alpha"));
                var replacedChangeId = assignment!.ChangeId;
                control.ApplyPresentation(externalAuthority with
                {
                    ConnectionIdentity = "connection-test-replacement"
                });
                Require(!control.HasPendingAssignment
                        && !control.SetAssignmentState(replacedChangeId, ProviderAssignmentSaveState.Saved)
                        && OwnerCount("alpha") == 1
                        && IsAssigned("unavailable-002", "alpha"),
                    "a connection replacement retained or rolled old-session optimistic ownership into the new session");

                // With no prior owner this is an ordinary assignment, not a move.
                var noPriorOwner = original with
                {
                    PresentationIdentity = "provider-test-no-prior-owner",
                    ConnectionIdentity = original.ConnectionIdentity,
                    Models = original.Models.Select(model => model with
                    {
                        AssignedTargetIds = model.Id == "loaded-000" ? ["default"] : []
                    }).ToArray()
                };
                control.ApplyPresentation(noPriorOwner);
                FlushProviderModelsDispatcher(host);
                assignment = null;
                Select("available-001");
                ToggleProviderModelsCheckBox(Target("Alpha"));
                Require(OwnerCount("alpha") == 1
                        && IsAssigned("available-001", "alpha")
                        && control.AssignmentStatus.Text.StartsWith("Saving assignment", StringComparison.Ordinal)
                        && !control.AssignmentStatus.Text.Contains("Moving", StringComparison.Ordinal),
                    "a no-prior-owner assignment was mislabeled as a transfer");
                control.SetAssignmentState(
                    assignment!.ChangeId,
                    ProviderAssignmentSaveState.Failed,
                    "Fixture reset");

                // Default moves are exclusive without disturbing explicit agent routes.
                control.ApplyPresentation(original);
                FlushProviderModelsDispatcher(host);
                assignment = null;
                Select("available-001");
                ToggleProviderModelsCheckBox(Target("Default"));
                Require(assignment is { TargetId: "default", IsAssigned: true }
                        && OwnerCount("default") == 1
                        && IsAssigned("available-001", "default")
                        && IsAssigned("loaded-000", "alpha")
                        && control.AssignmentStatus.Text.Contains(
                            "Changing Default from Loaded model 000 to Available model 001",
                            StringComparison.Ordinal),
                    "the Default move was not exclusive or disturbed an explicit Alpha route");
                Require(Target("Alpha").DataContext?.GetType().GetProperty("AssignmentState")
                            ?.GetValue(Target("Alpha").DataContext)?.ToString() == "Unassigned",
                    "the new Default row falsely labeled Alpha as inherited while another model explicitly owned Alpha");
                Require(control.SetAssignmentState(
                            assignment!.ChangeId,
                            ProviderAssignmentSaveState.Failed,
                            "Fixture rollback")
                        && IsAssigned("loaded-000", "default")
                        && IsAssigned("loaded-000", "alpha")
                        && !IsAssigned("available-001", "default"),
                    "a failed Default move did not restore Default without disturbing explicit routes");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelAssignmentsControlHostsCausalLmStudioLifecycleAction()
    {
        RunStaTest(() =>
        {
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            var dark = ThemePalette.Resolve("dark-blue");
            ApplyExperimentSurfaceTheme(control, dark);
            control.ApplyPresentation(ProviderModelsPresentation(modelCount: 80));
            var host = new Window
            {
                Content = control,
                Width = 1500,
                Height = 860,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            ProviderModelLifecycleRequestedEventArgs? lifecycle = null;
            var lifecycleRequestCount = 0;
            control.LifecycleRequested += (_, args) =>
            {
                lifecycle = args;
                lifecycleRequestCount++;
            };

            host.Show();
            try
            {
                FlushProviderModelsDispatcher(host);
                control.LoadedList.SelectedIndex = 0;
                FlushProviderModelsDispatcher(host);
                var scrollHost = RequireExperimentTemplatePart<ScrollViewer>(control.CatalogList, "ScrollHost");
                Require(scrollHost.ScrollableHeight > 0,
                    "hosted Models catalog did not expose enough vertical content for the pending-scroll regression");
                var groupsBeforeRequest = CollectionViewSource.GetDefaultView(control.CatalogList.ItemsSource)
                    .Groups?.Cast<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray() ?? [];

                // ButtonBase.Click is the routed action raised by the normal pointer path.
                control.LifecycleAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(lifecycle is not null
                        && lifecycleRequestCount == 1
                        && lifecycle.ModelId == "loaded-000"
                        && !lifecycle.Load,
                    "selected loaded model did not publish one exact Unload request");
                var unloadOperation = lifecycle!;
                Require(control.HasPendingLifecycle
                        && control.SelectedModelId == "loaded-000"
                        && control.LifecycleAction.Content?.ToString() == "Unloading…"
                        && control.SearchBox.IsEnabled
                        && control.CatalogList.IsEnabled
                        && !control.RefreshAction.IsEnabled
                        && control.LifecycleProgress.Visibility == Visibility.Visible
                        && AutomationProperties.GetItemStatus(control.AssignmentStatus) == "Unavailable"
                        && FindProviderModelsDescendants<CheckBox>(control.DetailSurface).All(checkBox => !checkBox.IsEnabled)
                        && groupsBeforeRequest.SequenceEqual(
                            CollectionViewSource.GetDefaultView(control.CatalogList.ItemsSource)
                                .Groups?.Cast<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray() ?? []),
                    "pending lifecycle request disabled catalog inspection, enabled conflicting controls, or moved groups optimistically");

                var selectedContainer = control.CatalogList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                    ?? throw new InvalidOperationException("selected model container was not realized for pending scrolling");
                var offsetBeforeWheel = scrollHost.VerticalOffset;
                if (Mouse.PrimaryDevice is { } mouseDevice)
                {
                    selectedContainer.RaiseEvent(new MouseWheelEventArgs(
                        mouseDevice,
                        Environment.TickCount,
                        -120)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent
                    });
                }
                else
                {
                    // Headless full-harness runs can legitimately have no primary mouse device.
                    // Exercise the same enabled ScrollViewer contract without fabricating input.
                    scrollHost.ScrollToVerticalOffset(offsetBeforeWheel + 48);
                }
                FlushProviderModelsDispatcher(host);
                Require(scrollHost.VerticalOffset > offsetBeforeWheel,
                    "mouse-wheel scrolling stopped while LM Studio lifecycle work was pending");

                control.CatalogList.SelectedIndex = control.CatalogList.Items.Count - 1;
                Require(control.FocusCatalog(),
                    "model catalog did not retain keyboard focus while LM Studio lifecycle work was pending");
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId != unloadOperation.ModelId
                        && control.CatalogList.IsKeyboardFocusWithin
                        && control.LifecycleAction.Content?.ToString() != "Unloading…"
                        && !control.LifecycleAction.IsEnabled
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Unavailable"
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Running"
                        && control.LifecycleStatus.Text.Contains("Loaded model 000", StringComparison.Ordinal)
                        && control.LifecycleProgress.Visibility == Visibility.Collapsed
                        && FindProviderModelsDescendants<CheckBox>(control.DetailSurface).All(checkBox => !checkBox.IsEnabled),
                    "keyboard catalog inspection hid the original pending model or re-enabled conflicting actions");
                control.LoadedList.SelectedIndex = 0;
                control.LoadedList.ScrollIntoView(control.LoadedList.SelectedItem);
                FlushProviderModelsDispatcher(host);
                Require(control.SetLifecycleState(
                            unloadOperation.OperationId,
                            ProviderModelLifecycleActionState.Running,
                            "LM Studio is unloading the selected model.")
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Running"
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Running"
                        && ExperimentBrushMatches(control.LifecycleStatusSurface.BorderBrush, dark.StatusInfo)
                        && ExperimentBrushMatches(control.LifecycleStatus.Foreground, dark.StatusInfo),
                    "Running lifecycle state was not exposed through themed live status evidence");

                Require(control.SetLifecycleState(
                            unloadOperation.OperationId,
                            ProviderModelLifecycleActionState.Succeeded,
                            "LM Studio completed the unload request.")
                        && !control.HasPendingLifecycle
                        && control.SearchBox.IsEnabled
                        && control.CatalogList.IsEnabled
                        && control.LifecycleAction.IsEnabled
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded",
                    "successful lifecycle resolution did not restore interaction and retain truthful status");
                var light = ThemePalette.Resolve("light");
                ApplyExperimentSurfaceTheme(control, light);
                FlushProviderModelsDispatcher(host);
                Require(ExperimentBrushMatches(control.LifecycleStatusSurface.BorderBrush, light.StatusSuccess)
                        && ExperimentBrushMatches(control.LifecycleStatus.Foreground, light.StatusSuccess),
                    "Succeeded lifecycle state did not follow Light theme semantic success brushes");
                Require(!control.SetLifecycleState(
                            Guid.NewGuid(),
                            ProviderModelLifecycleActionState.Failed,
                            "stale"),
                    "unrelated lifecycle completion mutated the resolved Models surface");

                var afterUnload = ProviderModelsPresentation(modelCount: 2);
                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available",
                            CanLoad = true,
                            CanUnload = false
                        }
                        : model).ToArray()
                });
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == "loaded-000"
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && AutomationProperties.GetName(control.LifecycleAction).StartsWith("Load ", StringComparison.Ordinal),
                    "refreshed unloaded evidence did not preserve selection and switch the single action to Load");

                lifecycle = null;
                var lifecyclePeer = new ButtonAutomationPeer(control.LifecycleAction);
                var invokeProvider = lifecyclePeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                    ?? throw new InvalidOperationException("LM Studio lifecycle button did not expose UIA Invoke.");
                invokeProvider.Invoke();
                FlushProviderModelsDispatcher(host);
                Require(lifecycle is not null
                        && lifecycle.Load
                        && lifecycle.ModelId == "loaded-000"
                        && control.HasPendingLifecycle,
                    "UIA Invoke did not publish the selected model's Load request");
                var failedLoad = lifecycle!;
                Require(control.SetLifecycleState(
                            failedLoad.OperationId,
                            ProviderModelLifecycleActionState.Failed,
                            "LM Studio rejected the load request.")
                        && !control.HasPendingLifecycle
                        && control.LifecycleStatus.Text.StartsWith("Failed:", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Failed",
                    "failed lifecycle resolution did not restore the control with explicit failure evidence");
                var highContrast = ThemePalette.Resolve("high-contrast");
                ApplyExperimentSurfaceTheme(control, highContrast);
                FlushProviderModelsDispatcher(host);
                Require(ExperimentBrushMatches(control.LifecycleStatusSurface.BorderBrush, highContrast.DangerBorder)
                        && ExperimentBrushMatches(control.LifecycleStatus.Foreground, highContrast.DangerText),
                    "Failed lifecycle state did not follow High Contrast semantic danger brushes");

                lifecycle = null;
                InvokeProviderModelsButtonByKeyboard(control.LifecycleAction, host);
                FlushProviderModelsDispatcher(host);
                Require(lifecycle is not null && lifecycle.Load && control.HasPendingLifecycle,
                    "Space did not invoke the focused LM Studio lifecycle button");
                var unconfirmedLoad = lifecycle!;
                Require(control.SetLifecycleState(
                            unconfirmedLoad.OperationId,
                            ProviderModelLifecycleActionState.Unconfirmed,
                            "LM Studio accepted the load request, but residency is not yet confirmed.")
                        && !control.HasPendingLifecycle
                        && !control.LifecycleStatus.Text.StartsWith("Failed:", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation"
                        && !control.LifecycleAction.IsEnabled
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Unconfirmed"
                        && ExperimentBrushMatches(control.LifecycleStatusSurface.BorderBrush, highContrast.StatusWarning)
                        && ExperimentBrushMatches(control.LifecycleStatus.Foreground, highContrast.StatusWarning),
                    "accepted but unconfirmed lifecycle evidence was rendered as failure or left actionable");

                control.ApplyPresentation(afterUnload with
                {
                    Models = [],
                    SelectedModelId = "",
                    PresentationIdentity = "provider-test-routing-v2",
                    CatalogStatus = "LM Studio returned an empty authoritative catalog."
                });
                FlushProviderModelsDispatcher(host);
                var emptyCatalogGroups = CollectionViewSource.GetDefaultView(control.CatalogList.ItemsSource)
                    .Groups?.Cast<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray() ?? [];
                var pinnedReceipt = control.CatalogList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                Require(control.CatalogList.Items.Count == 1
                        && emptyCatalogGroups.SequenceEqual(["Load State Unavailable"])
                        && pinnedReceipt is not null
                        && AutomationProperties.GetName(pinnedReceipt).Contains("Awaiting confirmation", StringComparison.OrdinalIgnoreCase)
                        && AutomationProperties.GetItemStatus(pinnedReceipt) == "Awaiting confirmation"
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation"
                        && control.SelectedModelId == "loaded-000",
                    "an authoritative empty catalog dropped the pinned unconfirmed lifecycle receipt");

                control.ApplyPresentation(afterUnload with
                {
                    Models = [],
                    SelectedModelId = "",
                    PresentationIdentity = "provider-test-routing-v3",
                    CatalogStatus = "LM Studio still returned an empty authoritative catalog."
                });
                FlushProviderModelsDispatcher(host);
                Require(control.CatalogList.Items.Count == 1
                        && control.SelectedModelId == "loaded-000"
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation"
                        && control.LifecycleStatus.Text.Contains("not yet confirmed", StringComparison.OrdinalIgnoreCase),
                    "a repeated empty catalog lost or reinterpreted the unconfirmed lifecycle receipt after routing identity changed");

                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available",
                            CanLoad = true,
                            CanUnload = false,
                            IsResidencyStale = false
                        }
                        : model).ToArray(),
                    PresentationIdentity = "provider-test-routing-v4"
                });
                FlushProviderModelsDispatcher(host);
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleStatus.Text.StartsWith("Request not yet confirmed.", StringComparison.Ordinal)
                        && !control.LifecycleStatus.Text.StartsWith("Failed:", StringComparison.Ordinal)
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleAction.IsEnabled
                        && control.RefreshAction.IsEnabled,
                    "authoritative opposite-state evidence left the request blocked forever or reinterpreted it as failure");

                var lateHeartbeatConfirmedLoad = ProviderModelsPresentation(modelCount: 2);
                control.ApplyPresentation(lateHeartbeatConfirmedLoad with
                {
                    PresentationIdentity = "provider-test-routing-v4-late"
                });
                FlushProviderModelsDispatcher(host);
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal)
                        && control.LifecycleStatus.Text.Contains("is loaded", StringComparison.Ordinal)
                        && control.LifecycleAction.Content?.ToString() == "Unload model"
                        && control.LifecycleAction.IsEnabled,
                    "late desired residency evidence did not reconcile the original receipt after an observed opposite state");

                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available",
                            CanLoad = true,
                            CanUnload = false,
                            IsResidencyStale = false
                        }
                        : model).ToArray(),
                    PresentationIdentity = "provider-test-routing-v4-retry"
                });
                FlushProviderModelsDispatcher(host);

                lifecycle = null;
                control.LifecycleAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(lifecycle is not null && lifecycle.Load && control.HasPendingLifecycle,
                    "opposite-state evidence did not expose a working Load retry path");
                var retryLoad = lifecycle!;
                Require(control.SetLifecycleState(
                            retryLoad.OperationId,
                            ProviderModelLifecycleActionState.Unconfirmed,
                            "Retry accepted; waiting for authoritative loaded evidence."),
                    "retry Load did not establish a new unconfirmed receipt");

                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available · revalidating",
                            CanLoad = true,
                            CanUnload = false,
                            IsResidencyStale = true
                        }
                        : model).ToArray(),
                    IsRefreshing = true,
                    PresentationIdentity = "provider-test-routing-v5",
                    CatalogStatus = "Refreshing LM Studio residency evidence."
                });
                FlushProviderModelsDispatcher(host);
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation",
                    "revalidating stale availability incorrectly resolved a retried lifecycle outcome");

                var heartbeatConfirmedLoad = ProviderModelsPresentation(modelCount: 2);
                control.ApplyPresentation(heartbeatConfirmedLoad with
                {
                    PresentationIdentity = "provider-test-routing-v6"
                });
                FlushProviderModelsDispatcher(host);
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal)
                        && control.LifecycleStatus.Text.Contains("is loaded", StringComparison.Ordinal)
                        && control.LifecycleAction.Content?.ToString() == "Unload model"
                        && control.LifecycleAction.IsEnabled,
                    "later authoritative heartbeat evidence did not reconcile the retained desired loaded outcome");

                lifecycle = null;
                InvokeProviderModelsButtonByKeyboard(control.LifecycleAction, host);
                FlushProviderModelsDispatcher(host);
                Require(lifecycle is not null && !lifecycle.Load && control.HasPendingLifecycle,
                    "keyboard did not invoke Unload after heartbeat reconciliation");
                var staleOperation = lifecycle!;
                control.ApplyPresentation(heartbeatConfirmedLoad with
                {
                    PresentationIdentity = "provider-test-v2",
                    ConnectionIdentity = "connection-test-v2"
                });
                Require(!control.HasPendingLifecycle
                        && !control.SetLifecycleState(
                            staleOperation.OperationId,
                            ProviderModelLifecycleActionState.Succeeded,
                            "must be rejected")
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Failed"
                        && control.LifecycleStatus.Text.Contains("provider or session changed", StringComparison.OrdinalIgnoreCase),
                    "a provider/session replacement before mutation did not reject the stale lifecycle operation definitively");

                lifecycle = null;
                control.LifecycleAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(lifecycle is not null
                        && !lifecycle.Load
                        && control.MarkLifecycleMutationStarted(lifecycle.OperationId),
                    "the control did not record the causal boundary immediately before lifecycle mutation");
                var startedOperation = lifecycle!;
                control.ApplyPresentation(heartbeatConfirmedLoad with
                {
                    PresentationIdentity = "provider-test-v3",
                    ConnectionIdentity = "connection-test-v3"
                });
                FlushProviderModelsDispatcher(host);
                Require(!control.HasPendingLifecycle
                        && !control.SetLifecycleState(
                            startedOperation.OperationId,
                            ProviderModelLifecycleActionState.Failed,
                            "must be rejected")
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleStatus.Text.Contains("load state is unknown", StringComparison.OrdinalIgnoreCase)
                        && !control.LifecycleStatus.Text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase),
                    "a provider/session replacement after mutation started was reinterpreted as a definite failure");

                control.ApplyPresentation(heartbeatConfirmedLoad with
                {
                    Models = heartbeatConfirmedLoad.Models
                        .Where(model => model.Id != "loaded-000")
                        .ToArray(),
                    SelectedModelId = "available-001",
                    PresentationIdentity = "provider-test-v3-without-original-model",
                    ConnectionIdentity = "connection-test-v3"
                });
                FlushProviderModelsDispatcher(host);
                Require(control.CatalogList.Items.Count == 1
                        && control.SelectedModelId == "available-001"
                        && control.LifecycleAction.Content?.ToString() == "Load model",
                    "an orphan lifecycle receipt was pinned into a different connection's catalog");

                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available",
                            CanLoad = true,
                            CanUnload = false,
                            IsResidencyStale = false
                        }
                        : model).ToArray(),
                    PresentationIdentity = "provider-test-v2-returned",
                    ConnectionIdentity = "connection-test-v2"
                });
                FlushProviderModelsDispatcher(host);
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal)
                        && control.LifecycleStatus.Text.Contains("is unloaded", StringComparison.Ordinal)
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleAction.IsEnabled
                        && !control.HasUnconfirmedLifecycleReceipt,
                    "returning to the original connection did not reconcile the post-mutation orphan from authoritative desired residency");

                var unavailable = ProviderModelsPresentation(modelCount: 2);
                control.ApplyPresentation(unavailable with
                {
                    Models = unavailable.Models.Select(model => model.Id == "loaded-000"
                        ? model with { CanLoad = false, CanUnload = false, LifecycleHelp = "Residency evidence unavailable." }
                        : model).ToArray(),
                    PresentationIdentity = "provider-test-v3"
                });
                FlushProviderModelsDispatcher(host);
                Require(!control.LifecycleAction.IsEnabled
                        && control.LifecycleAction.Content?.ToString() == "Load state unavailable"
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Unavailable"
                        && AutomationProperties.GetHelpText(control.LifecycleAction).Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                    "missing residency capability exposed an actionable or unexplained lifecycle command");

                control.ApplyPresentation(afterUnload with
                {
                    Models = afterUnload.Models.Select(model => model.Id == "loaded-000"
                        ? model with
                        {
                            Availability = ProviderModelAvailability.Available,
                            Status = "Available",
                            CanLoad = true,
                            CanUnload = false
                        }
                        : model).ToArray(),
                    PresentationIdentity = "provider-test-v4",
                    CanRunLifecycle = false
                });
                FlushProviderModelsDispatcher(host);
                Require(!control.LifecycleAction.IsEnabled
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && AutomationProperties.GetItemStatus(control.LifecycleAction) == "Unavailable"
                        && AutomationProperties.GetHelpText(control.LifecycleAction).Contains("another provider operation", StringComparison.OrdinalIgnoreCase),
                    "known Load capability was not disabled and explained when lifecycle execution was globally unavailable");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static ProviderModelAssignmentsPresentation ProviderModelsPresentation(int modelCount)
    {
        var models = Enumerable.Range(0, modelCount)
            .Select(index =>
            {
                var availability = (index % 3) switch
                {
                    0 => ProviderModelAvailability.Loaded,
                    1 => ProviderModelAvailability.Available,
                    _ => ProviderModelAvailability.Unavailable
                };
                var prefix = availability switch
                {
                    ProviderModelAvailability.Loaded => "loaded",
                    ProviderModelAvailability.Available => "available",
                    _ => "unavailable"
                };
                return new ProviderModelAssignmentPresentation(
                    $"{prefix}-{index:000}",
                    $"{char.ToUpperInvariant(prefix[0])}{prefix[1..]} model {index:000}",
                    availability,
                    availability switch
                    {
                        ProviderModelAvailability.Loaded => "Loaded",
                        ProviderModelAvailability.Available => "Available",
                        _ => "Load state unavailable"
                    },
                    availability == ProviderModelAvailability.Loaded
                        ? "Q4 · 8k context"
                        : "Advertised by provider",
                    index == 0 ? ["default"] : Array.Empty<string>(),
                    "Provider-observed model metadata.",
                    CanLoad: availability == ProviderModelAvailability.Available,
                    CanUnload: availability == ProviderModelAvailability.Loaded,
                    LifecycleHelp: availability == ProviderModelAvailability.Unavailable
                        ? "LM Studio residency evidence is unavailable."
                        : "LM Studio provider-observed residency evidence.");
            })
            .ToArray();
        return new ProviderModelAssignmentsPresentation(
            "Local provider",
            "Online",
            ProviderConnectionState.Online,
            $"{models.Count(model => model.Availability == ProviderModelAvailability.Loaded)} loaded, "
                + $"{models.Count(model => model.Availability == ProviderModelAvailability.Available)} available, "
                + $"{models.Count(model => model.Availability == ProviderModelAvailability.Unavailable)} load state unavailable.",
            models,
            [
                new ProviderAssignmentTargetPresentation(
                    "default",
                    "Default",
                    "Used by targets without an explicit route.",
                    AssignedState: ProviderTargetAssignmentState.Default,
                    IsDefault: true),
                new ProviderAssignmentTargetPresentation("alpha", "Alpha", "Durable Alpha routing target."),
                new ProviderAssignmentTargetPresentation("beta", "Beta", "Durable Beta routing target.")
            ],
            "loaded-000",
            PresentationIdentity: "provider-test-v1",
            CanRunLifecycle: true,
            ConnectionIdentity: "connection-test-v1");
    }

    static IEnumerable<T> FindProviderModelsDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var descendant in FindProviderModelsDescendants<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }

    static void FlushProviderModelsDispatcher(Window host)
    {
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));
        host.UpdateLayout();
    }

    static void FlushProviderModelsSearchDebounce(Window host)
    {
        System.Threading.Thread.Sleep(220);
        FlushProviderModelsDispatcher(host);
    }

    static void ToggleProviderModelsCheckBox(CheckBox checkBox)
    {
        var peer = new CheckBoxAutomationPeer(checkBox);
        var provider = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new InvalidOperationException("Assignment checkbox did not expose the UIA Toggle pattern.");
        provider.Toggle();
    }

    static bool ProviderModelsElementFits(FrameworkElement element, FrameworkElement ancestor)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(new Point(), element.RenderSize));
        return bounds.Left >= -1
            && bounds.Top >= -1
            && bounds.Right <= ancestor.ActualWidth + 1
            && bounds.Bottom <= ancestor.ActualHeight + 1;
    }

    static void InvokeProviderModelsButtonByKeyboard(Button button, Window host)
    {
        Require(button.Focus(), "LM Studio lifecycle button could not receive keyboard focus.");
        var source = PresentationSource.FromVisual(host)
            ?? throw new InvalidOperationException("Hosted Models surface had no presentation source.");
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        button.RaiseEvent(down);
        var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        };
        button.RaiseEvent(up);
    }

    static void RaiseProviderModelsPreviewKey(UIElement target, Window host, Key key)
    {
        var source = PresentationSource.FromVisual(host)
            ?? throw new InvalidOperationException("Hosted Models surface had no presentation source.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        Require(args.Handled, $"Models surface did not handle the {key} keyboard shortcut.");
    }
}
