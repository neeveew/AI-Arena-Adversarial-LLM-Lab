using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderModelsOptimizationPreservesIncrementalCatalogContinuity()
    {
        RunStaTest(() =>
        {
            var initial = ProviderModelsOptimizationPresentation(256, "model-121");
            var control = HostProviderModelsOptimizationSurface(initial, 1500, 820, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(control.LoadedList.Items.Count == 64
                        && control.CatalogList.Items.Count == 192,
                    "the hosted Models surface did not split the bounded 256 rows into pinned Loaded and catalog regions");
                Require(control.UsesIncrementalLiveShaping,
                    "the hosted Models surface did not enable incremental live sorting, grouping, and filtering");

                var movedRow = ProviderModelsOptimizationRow(control, "model-116");
                var selectedRow = control.CatalogList.SelectedItem
                    ?? throw new InvalidOperationException("the 256-row catalog did not select its requested stable row");
                Require(ProviderModelsOptimizationRowId(selectedRow) == "model-121",
                    "the 256-row catalog selected the wrong stable row");

                control.CatalogList.ScrollIntoView(selectedRow);
                FlushProviderModelsDispatcher(host);
                Require(control.FocusCatalog(),
                    "the hosted 256-row catalog could not establish keyboard focus before refresh");
                FlushProviderModelsDispatcher(host);

                var scrollViewer = FindExperimentVisualDescendant<ScrollViewer>(control.CatalogList)
                    ?? throw new InvalidOperationException("the hosted catalog did not expose its scroll viewer");
                var offsetBefore = scrollViewer.VerticalOffset;
                Require(offsetBefore > 0,
                    "the hosted 256-row catalog did not establish a non-zero scroll position for continuity evidence");
                var viewportAnchorBefore = ProviderModelsOptimizationViewportAnchor(control);
                var selectedContainerBefore = control.CatalogList.ItemContainerGenerator.ContainerFromItem(selectedRow);
                Require(selectedContainerBefore is ListBoxItem,
                    "the selected stable row was not realized before the incremental residency refresh");

                var changes = new List<NotifyCollectionChangedAction>();
                if (control.CatalogList.ItemsSource is not INotifyCollectionChanged notifier)
                {
                    throw new InvalidOperationException("the catalog view did not expose collection-change evidence");
                }

                NotifyCollectionChangedEventHandler changed = (_, args) => changes.Add(args.Action);
                notifier.CollectionChanged += changed;
                try
                {
                    var refreshedModels = initial.Models.Select(model =>
                        model.Id == "model-116"
                            ? model with
                            {
                                Availability = ProviderModelAvailability.Available,
                                Status = "Available",
                                CanLoad = true,
                                CanUnload = false
                            }
                            : model).ToArray();
                    control.ApplyPresentation(initial with
                    {
                        Models = refreshedModels,
                        PresentationIdentity = "models-optimization-256-v2"
                    });
                    FlushProviderModelsDispatcher(host);
                }
                finally
                {
                    notifier.CollectionChanged -= changed;
                }

                var movedRowAfter = ProviderModelsOptimizationRow(control, "model-116");
                var selectedContainerAfter = control.CatalogList.ItemContainerGenerator.ContainerFromItem(selectedRow);
                Require(ReferenceEquals(movedRow, movedRowAfter),
                    "a one-row residency move replaced the keyed row object instead of updating it in place");
                Require(ReferenceEquals(control.CatalogList.SelectedItem, selectedRow)
                        && control.SelectedModelId == "model-121",
                    "a peer residency move changed the selected model or selected-row identity");
                Require(control.CatalogList.IsKeyboardFocusWithin,
                    "a peer residency move dropped catalog keyboard focus");
                var viewportAnchorAfter = ProviderModelsOptimizationViewportAnchor(control);
                Require(viewportAnchorAfter.Id == viewportAnchorBefore.Id
                        && Math.Abs(viewportAnchorAfter.RelativeTop - viewportAnchorBefore.RelativeTop) <= 1,
                    $"a peer residency move changed the top viewport anchor from {viewportAnchorBefore.Id}@{viewportAnchorBefore.RelativeTop:0.##} to {viewportAnchorAfter.Id}@{viewportAnchorAfter.RelativeTop:0.##}");
                Require(ReferenceEquals(selectedContainerBefore, selectedContainerAfter),
                    "a peer residency move regenerated the unaffected selected container");
                Require(!changes.Contains(NotifyCollectionChangedAction.Reset),
                    "a one-row residency move reset the entire catalog collection");
                Require(changes.Count <= 4,
                    $"a one-row residency move published {changes.Count} collection changes instead of a bounded live move");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsOptimizationDebouncesSearchAndFiltersCatalogFacets()
    {
        RunStaTest(() =>
        {
            var presentation = ProviderModelsFacetPresentation();
            var control = HostProviderModelsOptimizationSurface(presentation, 1280, 760, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                var publishedQueries = new List<string>();
                control.SearchChanged += (_, args) => publishedQueries.Add(args.Query);

                foreach (var query in new[]
                         {
                             "s", "sp", "spe", "spec", "speci", "special", "special ",
                             "special m", "special mo", "special model"
                         })
                {
                    control.SearchBox.Text = query;
                }

                FlushProviderModelsSearchDebounce(host);
                Require(publishedQueries.SequenceEqual(["special model"]),
                    $"10 rapid search edits published {publishedQueries.Count} SearchChanged events instead of one final query");
                Require(control.CatalogList.Items.Count == 1
                        && control.LoadedList.Items.Count == 3
                        && control.FilteredCount.Text == "1 of 9",
                    "the debounced final query did not publish the correct result count");

                control.SearchBox.Clear();
                FlushProviderModelsSearchDebounce(host);
                Require(control.FacetFilter.Items.Count == 5,
                    "the Models facet selector did not expose all five available-catalog facets");

                var expectedCounts = new[] { 9, 4, 3, 6, 5 };
                var expectedNames = new[]
                {
                    "All catalog", "Available", "Assigned", "Unassigned", "State unavailable"
                };
                for (var index = 0; index < expectedCounts.Length; index++)
                {
                    control.FacetFilter.SelectedIndex = index;
                    FlushProviderModelsDispatcher(host);
                    var option = control.FacetFilter.SelectedItem
                        ?? throw new InvalidOperationException($"facet {index} had no selected option");
                    var displayName = option.GetType().GetProperty("DisplayName")?.GetValue(option)?.ToString();
                    Require(displayName == expectedNames[index],
                        $"facet {index} exposed '{displayName}' instead of '{expectedNames[index]}'");
                    Require(control.CatalogList.Items.Count == expectedCounts[index],
                        $"facet {expectedNames[index]} showed {control.CatalogList.Items.Count} rows instead of {expectedCounts[index]}");
                    var expectedResultText = expectedCounts[index] == 9
                        ? "9 catalog models"
                        : $"{expectedCounts[index]} of 9";
                    Require(control.FilteredCount.Text == expectedResultText,
                        $"facet {expectedNames[index]} reported '{control.FilteredCount.Text}' instead of '{expectedResultText}'");
                }
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsOptimizationExposesGroupsPartialEvidenceAndAssignmentStates()
    {
        RunStaTest(() =>
        {
            var partial = ProviderModelsFacetPresentation() with
            {
                ConnectionStatus = "Partial",
                ConnectionState = ProviderConnectionState.Partial,
                CatalogStatus = "256 of 300 models shown; 44 omitted by the bounded provider catalog.",
                PresentationIdentity = "models-optimization-partial-v1"
            };
            var control = HostProviderModelsOptimizationSurface(partial, 1280, 760, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                var groups = CollectionViewSource.GetDefaultView(control.CatalogList.ItemsSource)
                    .Groups?.Cast<CollectionViewGroup>().ToArray() ?? [];
                Require(groups.Length == 2
                        && groups.Select(group => group.Name?.ToString()).SequenceEqual(
                            ["Available Models", "Load State Unavailable"])
                        && groups.Select(group => group.ItemCount).SequenceEqual([4, 5])
                        && control.LoadedList.Items.Count == 3,
                    "the Models surface did not expose pinned Loaded and counted Available catalog groups");

                var catalogPeer = new ExpanderAutomationPeer(control.CatalogExpander);
                var catalogPattern = catalogPeer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                    ?? throw new InvalidOperationException("the Available catalog did not expose ExpandCollapsePattern");
                Require(catalogPattern.ExpandCollapseState == ExpandCollapseState.Expanded,
                    "the Available catalog did not start expanded");
                catalogPattern.Collapse();
                FlushProviderModelsDispatcher(host);
                Require(catalogPattern.ExpandCollapseState == ExpandCollapseState.Collapsed
                        && control.LoadedList.IsVisible,
                    "collapsing the Available catalog also hid the mandatory Loaded region");
                catalogPattern.Expand();
                FlushProviderModelsDispatcher(host);

                Require(control.CatalogAlert.Visibility == Visibility.Visible
                        && control.CatalogStatus.Text.Contains("44 omitted", StringComparison.Ordinal)
                        && AutomationProperties.GetItemStatus(control.CatalogStatus) == "Partial"
                        && AutomationProperties.GetHelpText(control.CatalogSummary).Contains("44 omitted", StringComparison.Ordinal),
                    "partial catalog evidence did not expose the omission warning visually and through UI Automation");

                var assignmentChecks = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface)
                    .ToArray();
                Require(assignmentChecks.Length == 4,
                    $"the selected model exposed {assignmentChecks.Length} assignment states instead of four");
                var states = assignmentChecks
                    .Select(AutomationProperties.GetItemStatus)
                    .ToHashSet(StringComparer.Ordinal);
                var unassignedRow = FindProviderModelsDescendants<ListBoxItem>(control.LoadedList)
                    .Single(item => AutomationProperties.GetName(item)
                        .StartsWith("Catalog model 01", StringComparison.Ordinal));
                unassignedRow.IsSelected = true;
                FlushProviderModelsDispatcher(host);
                foreach (var checkBox in FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface))
                {
                    states.Add(AutomationProperties.GetItemStatus(checkBox));
                }
                Require(states.SetEquals(["Default", "Explicit", "Uses default", "Unassigned"]),
                    "assignment controls did not distinguish Default, Explicit, inherited-default, and Unassigned states through UI Automation");
                unassignedRow = FindProviderModelsDescendants<ListBoxItem>(control.LoadedList)
                    .Single(item => AutomationProperties.GetName(item)
                        .StartsWith("Special model 00", StringComparison.Ordinal));
                unassignedRow.IsSelected = true;
                FlushProviderModelsDispatcher(host);
                foreach (var checkBox in FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.DetailSurface))
                {
                    var status = AutomationProperties.GetItemStatus(checkBox);
                    var visualLabels = FindProviderModelsDescendants<TextBlock>(checkBox)
                        .Select(text => text.Text)
                        .ToArray();
                    Require(visualLabels.Contains(status),
                        $"assignment state '{status}' was not rendered beside its checkbox");
                }
                Require(control.SelectedAssignmentSummary.Text.Contains("Default also covers Beta", StringComparison.Ordinal),
                    "the selected default model did not explain inherited routing in its summary");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsOptimizationReflowsAtWideCompactAndTwoHundredPercent()
    {
        RunStaTest(() =>
        {
            var control = HostProviderModelsOptimizationSurface(
                ProviderModelsFacetPresentation(),
                1500,
                820,
                out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                Require(!control.UsesCompactLayout
                        && control.DetailSurface.ActualWidth is >= 340 and <= 380
                        && control.MasterSurface.ActualWidth > control.DetailSurface.ActualWidth * 2,
                    "the 1500-DIP Models surface did not preserve its catalog-first wide layout");

                host.Width = 960;
                FlushProviderModelsDispatcher(host);
                Require(control.UsesCompactLayout
                        && Grid.GetRow(control.MasterSurface) == 0
                        && Grid.GetColumnSpan(control.MasterSurface) == 3
                        && Grid.GetRow(control.DetailSurface) == 2
                        && Grid.GetColumnSpan(control.DetailSurface) == 3,
                    "the 960-DIP Models surface did not stack the catalog and details panes");

                host.Width = 1500;
                control.LayoutTransform = new ScaleTransform(2, 2);
                FlushProviderModelsDispatcher(host);
                Require(control.ActualWidth <= 750
                        && control.UsesCompactLayout
                        && Grid.GetRow(control.DetailSurface) == 2
                        && control.WorkspaceScroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                        && control.WorkspaceScroller.ScrollableHeight > 0,
                    $"the 2x layout transform did not reflow through the compact policy (content width {control.ActualWidth:0.#} DIP)");

                control.WorkspaceScroller.ScrollToEnd();
                var assignmentScroller = FindProviderModelsDescendants<ScrollViewer>(control.DetailSurface)
                    .Single(scroll => AutomationProperties.GetName(scroll) == "Assignment targets");
                assignmentScroller.ScrollToEnd();
                FlushProviderModelsDispatcher(host);
                var lastAssignment = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.AssignmentTargets)
                    .Last();
                var targetBounds = lastAssignment.TransformToAncestor(control.WorkspaceScroller)
                    .TransformBounds(new Rect(0, 0, lastAssignment.ActualWidth, lastAssignment.ActualHeight));
                Require(targetBounds.Top >= -1
                        && targetBounds.Bottom <= control.WorkspaceScroller.ViewportHeight + 1,
                    $"the bottom assignment target was not reachable at 200% ({targetBounds.Top:0.#}..{targetBounds.Bottom:0.#} of {control.WorkspaceScroller.ViewportHeight:0.#})");

                control.LayoutTransform = Transform.Identity;
                FlushProviderModelsDispatcher(host);
                Require(!control.UsesCompactLayout
                        && Grid.GetColumn(control.DetailSurface) == 2,
                    "the Models surface did not restore its wide layout after removing the 2x transform");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static ProviderModelAssignmentsControl HostProviderModelsOptimizationSurface(
        ProviderModelAssignmentsPresentation presentation,
        double width,
        double height,
        out Window host)
    {
        var control = new ProviderModelAssignmentsControl();
        AttachArenaPresentationResources(control);
        ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
        control.ApplyPresentation(presentation);
        host = new Window
        {
            Content = control,
            Width = width,
            Height = height,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Show();
        return control;
    }

    static ProviderModelAssignmentsPresentation ProviderModelsOptimizationPresentation(
        int modelCount,
        string selectedModelId)
    {
        var models = Enumerable.Range(0, modelCount)
            .Select(index =>
            {
                var availability = (index % 4) switch
                {
                    0 => ProviderModelAvailability.Loaded,
                    3 => ProviderModelAvailability.Unavailable,
                    _ => ProviderModelAvailability.Available
                };
                return new ProviderModelAssignmentPresentation(
                    $"model-{index:000}",
                    $"Catalog model {index:000}",
                    availability,
                    availability switch
                    {
                        ProviderModelAvailability.Loaded => "Loaded",
                        ProviderModelAvailability.Available => "Available",
                        _ => "Load state unavailable"
                    },
                    $"publisher-{index % 7} · Q4 · {8 + index % 8}k context",
                    index % 5 == 0 ? ["default"] : [],
                    "Provider-observed model metadata.",
                    CanLoad: availability == ProviderModelAvailability.Available,
                    CanUnload: availability == ProviderModelAvailability.Loaded,
                    LifecycleHelp: "LM Studio provider-observed residency evidence.");
            })
            .ToArray();
        return new ProviderModelAssignmentsPresentation(
            "LM Studio",
            "Online",
            ProviderConnectionState.Online,
            $"{modelCount} models shown.",
            models,
            [new ProviderAssignmentTargetPresentation("default", "Default")],
            selectedModelId,
            PresentationIdentity: "models-optimization-256-v1",
            CanRunLifecycle: true,
            ConnectionIdentity: "models-optimization-connection-v1");
    }

    static ProviderModelAssignmentsPresentation ProviderModelsFacetPresentation()
    {
        var models = new List<ProviderModelAssignmentPresentation>();
        AddModels(ProviderModelAvailability.Loaded, assigned: true, count: 2);
        AddModels(ProviderModelAvailability.Loaded, assigned: false, count: 1);
        AddModels(ProviderModelAvailability.Available, assigned: true, count: 1);
        AddModels(ProviderModelAvailability.Available, assigned: false, count: 3);
        AddModels(ProviderModelAvailability.Unavailable, assigned: true, count: 2);
        AddModels(ProviderModelAvailability.Unavailable, assigned: false, count: 3);

        return new ProviderModelAssignmentsPresentation(
            "LM Studio",
            "Online",
            ProviderConnectionState.Online,
            "3 loaded, 4 available, 5 load state unavailable.",
            models,
            [
                new ProviderAssignmentTargetPresentation(
                    "default", "Default", AssignmentState: ProviderTargetAssignmentState.Default, IsDefault: true),
                new ProviderAssignmentTargetPresentation(
                    "alpha", "Alpha", AssignmentState: ProviderTargetAssignmentState.Explicit),
                new ProviderAssignmentTargetPresentation(
                    "beta", "Beta", AssignmentState: ProviderTargetAssignmentState.InheritsDefault),
                new ProviderAssignmentTargetPresentation(
                    "gamma", "Gamma", AssignmentState: ProviderTargetAssignmentState.Unassigned)
            ],
            "facet-00",
            PresentationIdentity: "models-optimization-facets-v1",
            CanRunLifecycle: true,
            ConnectionIdentity: "models-optimization-connection-v1");

        void AddModels(ProviderModelAvailability availability, bool assigned, int count)
        {
            for (var offset = 0; offset < count; offset++)
            {
                var index = models.Count;
                var special = index is 0 or 3;
                var assignedIds = index == 0
                    ? new[] { "default", "alpha" }
                    : assigned
                        ? new[] { "alpha" }
                        : [];
                models.Add(new ProviderModelAssignmentPresentation(
                    $"facet-{index:00}",
                    special ? $"Special model {index:00}" : $"Catalog model {index:00}",
                    availability,
                    availability switch
                    {
                        ProviderModelAvailability.Loaded => "Loaded",
                        ProviderModelAvailability.Available => "Available",
                        _ => "Load state unavailable"
                    },
                    $"publisher-{index % 3} · Q4 · {8 + index}k context",
                    assignedIds,
                    "Provider-observed model metadata.",
                    CanLoad: availability == ProviderModelAvailability.Available,
                    CanUnload: availability == ProviderModelAvailability.Loaded,
                    LifecycleHelp: "LM Studio provider-observed residency evidence."));
            }
        }
    }

    static object ProviderModelsOptimizationRow(
        ProviderModelAssignmentsControl control,
        string id) => control.LoadedList.Items.Cast<object>()
            .Concat(control.CatalogList.Items.Cast<object>())
            .Single(row => string.Equals(
                ProviderModelsOptimizationRowId(row),
                id,
                StringComparison.Ordinal));

    static (string Id, double RelativeTop) ProviderModelsOptimizationViewportAnchor(
        ProviderModelAssignmentsControl control)
    {
        var candidates = control.CatalogList.Items.Cast<object>()
            .Select(row => (Row: row, Container: control.CatalogList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem))
            .Where(item => item.Container is not null && item.Container.ActualHeight > 0)
            .Select(item =>
            {
                var bounds = item.Container!.TransformToAncestor(control.CatalogList)
                    .TransformBounds(new Rect(0, 0, item.Container.ActualWidth, item.Container.ActualHeight));
                return (item.Row, Bounds: bounds);
            })
            .Where(item => item.Bounds.Bottom > 0 && item.Bounds.Top < control.CatalogList.ActualHeight)
            .OrderBy(item => item.Bounds.Top)
            .ToArray();
        var first = candidates.FirstOrDefault();
        if (first.Row is null)
        {
            throw new InvalidOperationException("the hosted catalog did not expose a realized viewport anchor");
        }

        return (ProviderModelsOptimizationRowId(first.Row), first.Bounds.Top);
    }

    static string ProviderModelsOptimizationRowId(object row) =>
        row.GetType().GetProperty("Id")?.GetValue(row)?.ToString() ?? "";
}
