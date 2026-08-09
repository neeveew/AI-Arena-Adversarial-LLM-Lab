using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ExperimentLabExpandsMatricesDeterministicallyAndHonestly()
    {
        var at = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var input = new ExperimentMatrixInput(
            "Bounded local comparison",
            "scenario-pack:local",
            "provider:z, provider:a, provider:a",
            "temperature",
            "0.7, 0.2",
            "2",
            "4",
            "2",
            RubricIds: "rubric:test");
        var first = ExperimentLabCoordinator.BuildMatrixPreview(input, at);
        var second = ExperimentLabCoordinator.BuildMatrixPreview(input, at);
        Require(
            ArenaContractCodec.Serialize(first.Contract) == ArenaContractCodec.Serialize(second.Contract),
            "matrix contract changed between identical previews");
        Require(first.Expansion.ExperimentFingerprint == second.Expansion.ExperimentFingerprint, "matrix fingerprint was nondeterministic");
        Require(
            first.Expansion.Cells.Select(item => (item.CellKey, item.RunId)).SequenceEqual(
                second.Expansion.Cells.Select(item => (item.CellKey, item.RunId))),
            "matrix cells were nondeterministic");
        Require(first.Expansion.Variants.Length == 4 && first.Expansion.Cells.Length == 8, "provider/dimension/repetition Cartesian expansion was wrong");
        Require(first.Contract.ProviderProfileIds.SequenceEqual(["provider:a", "provider:z"]), "provider identities were not canonicalized");
        Require(first.Contract.Dimensions.Single().Parameter == "temperature"
                && first.Contract.Dimensions.Single().Values.SequenceEqual(["0.2", "0.7"]),
            "executable dimension values were not canonicalized");
        var equivalent = ExperimentLabCoordinator.BuildMatrixPreview(input with
        {
            ProviderProfileIds = " provider:a, provider:z, provider:a ",
            DimensionParameter = " temperature ",
            DimensionValues = "0.700, 0.20, 0.2"
        }, at);
        Require(equivalent.Contract.Id == first.Contract.Id
                && equivalent.Expansion.ExperimentFingerprint == first.Expansion.ExperimentFingerprint
                && equivalent.Expansion.Cells.Select(item => item.CellKey).SequenceEqual(first.Expansion.Cells.Select(item => item.CellKey)),
            "equivalent normalized matrix input produced duplicate logical experiments/cells");
        RequireExperimentThrows<ExperimentLabInputException>(
            () => ExperimentLabCoordinator.BuildMatrixPreview(input with { MaxParallelism = "33" }, at),
            "matrix accepted unsafe parallelism");
        Require(!ExperimentLabCoordinator.ShouldRetryTerminalCell(
                    ArenaExperimentStatus.Interrupted,
                    ArenaExperimentRunState.Completed)
                && !ExperimentLabCoordinator.ShouldRetryTerminalCell(
                    ArenaExperimentStatus.Cancelled,
                    ArenaExperimentRunState.Completed)
                && ExperimentLabCoordinator.ShouldRetryTerminalCell(
                    ArenaExperimentStatus.Interrupted,
                    ArenaExperimentRunState.Interrupted)
                && ExperimentLabCoordinator.ShouldRetryTerminalCell(
                    ArenaExperimentStatus.Cancelled,
                    ArenaExperimentRunState.Failed)
                && ExperimentLabCoordinator.ShouldRetryTerminalCell(
                    ArenaExperimentStatus.Completed,
                    ArenaExperimentRunState.Completed),
            "status-aware retry scope did not preserve Completed cells for continuation or allow an intentional full repeat");

        var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ExperimentLabControl.xaml"));
        Require(
            xaml.Contains("x:Name=\"ExecuteMatrixButton\"", StringComparison.Ordinal)
            && xaml.Contains("IsEnabled=\"False\"", StringComparison.Ordinal)
            && xaml.Contains("x:Name=\"CancelMatrixButton\"", StringComparison.Ordinal)
            && xaml.Contains("isolated child sessions", StringComparison.Ordinal),
            "matrix surface did not expose the honest resolver-gated run and cancel path");
    }

    static void ExperimentLabRegistersOnlyBackedAccessibleFeatures()
    {
        Require(
            !ExperimentLabControl.UsesCompactLayout(960)
            && ExperimentLabControl.UsesCompactLayout(700)
            && !ExperimentLabControl.UsesCompactLayout(double.NaN),
            "Experiment Lab responsive tier was not deterministic");

        RunStaTest(() =>
        {
            var control = new ExperimentLabControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            var keys = control.RegisteredFeatures.Select(item => item.Key).ToArray();
            Require(keys.SequenceEqual(["matrix", "fork", "packs", "rubrics", "claims"]), "Experiment Lab exposed missing or unavailable feature placeholders");
            Require(control.RegisteredFeatures.All(item => item.Content is not null && !string.IsNullOrWhiteSpace(item.HelpText)), "registered feature lacks content or accessible help");
            Require(AutomationProperties.GetName(control) == "Experiment Lab workspace", "Experiment Lab root automation name changed");
            control.ApplyResponsiveLayout(compact: true);
            control.ApplyResponsiveLayout(compact: false);
            var host = new Window
            {
                Content = control,
                Width = 960,
                Height = 640,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };
            host.Show();
            try
            {
                host.Activate();
                host.UpdateLayout();
                foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
                {
                    var theme = ThemePalette.Resolve(themeId);
                    ApplyExperimentSurfaceTheme(control, theme);
                    AssertExperimentSurfaceThemeChrome(control, host, theme);
                }
                Require(control.FocusFeatureSelector(), "feature selector was not keyboard focusable when hosted");
            }
            finally
            {
                control.MatrixDimensionParameterPicker.IsDropDownOpen = false;
                host.Close();
            }
            AssertQaArtifactThemeChrome();
            AssertPaletteAwareFocusVisualContract();
            RequireExperimentThrows<InvalidOperationException>(
                () => control.RegisterFeature(control.RegisteredFeatures[0]),
                "duplicate feature key was accepted");
        });
    }

    static void AssertExperimentSurfaceThemeChrome(
        ExperimentLabControl control,
        Window host,
        ThemePalette theme)
    {
        control.MatrixBenchmarkPicker.IsEnabled = false;
        control.ExecuteMatrixButton.IsEnabled = false;
        control.MatrixRetryApprovedCheckBox.IsEnabled = false;
        control.MatrixDimensionParameterPicker.IsDropDownOpen = true;
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

        var picker = control.MatrixDimensionParameterPicker;
        var popup = RequireExperimentTemplatePart<Popup>(picker, "PART_Popup");
        Require(popup.IsOpen
                && popup.Child is Border dropDown
                && ExperimentBrushMatches(dropDown.Background, theme.Input)
                && ExperimentBrushMatches(dropDown.BorderBrush, theme.Border),
            $"Experiment dimension popup did not consume the {theme.Id} input palette");

        var selectedComboItem = picker.Items[1] as ComboBoxItem
            ?? throw new InvalidOperationException("Experiment dimension selection was not a ComboBoxItem.");
        var selectedComboChrome = RequireExperimentTemplatePart<Border>(selectedComboItem, "ItemChrome");
        Require(ExperimentBrushMatches(selectedComboChrome.Background, theme.Primary),
            $"Experiment dimension item did not consume the {theme.Id} selected palette");

        var benchmarkToggle = RequireExperimentTemplatePart<ToggleButton>(control.MatrixBenchmarkPicker, "DropDownToggle");
        var benchmarkChrome = RequireExperimentTemplatePart<Border>(benchmarkToggle, "ToggleChrome");
        Require(ExperimentBrushMatches(benchmarkChrome.Background, theme.Disabled)
                && ExperimentBrushMatches(benchmarkChrome.BorderBrush, theme.DisabledBorder)
                && ExperimentBrushMatches(control.MatrixBenchmarkPicker.Foreground, theme.DisabledText),
            $"Disabled benchmark picker did not consume the {theme.Id} disabled palette");

        var runButtonChrome = RequireExperimentTemplatePart<Border>(control.ExecuteMatrixButton, "Chrome");
        Require(ExperimentBrushMatches(runButtonChrome.Background, theme.Disabled)
                && ExperimentBrushMatches(runButtonChrome.BorderBrush, theme.DisabledBorder)
                && ExperimentBrushMatches(control.ExecuteMatrixButton.Foreground, theme.DisabledText),
            $"Disabled matrix action did not consume the {theme.Id} disabled palette");

        var retryBox = RequireExperimentTemplatePart<Border>(control.MatrixRetryApprovedCheckBox, "Box");
        Require(ExperimentBrushMatches(retryBox.Background, theme.Disabled)
                && ExperimentBrushMatches(retryBox.BorderBrush, theme.DisabledBorder)
                && ExperimentBrushMatches(control.MatrixRetryApprovedCheckBox.Foreground, theme.DisabledText),
            $"Disabled retry approval did not consume the {theme.Id} disabled palette");

        var featureViewport = RequireExperimentTemplatePart<Border>(control.FeatureSelector, "ListViewport");
        var featureScrollHost = RequireExperimentTemplatePart<ScrollViewer>(control.FeatureSelector, "ScrollHost");
        var selectedFeature = control.FeatureSelector.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
            ?? throw new InvalidOperationException("Experiment feature selector did not realize its selected item.");
        var selectedFeatureChrome = RequireExperimentTemplatePart<Border>(selectedFeature, "ItemChrome");
        Require(ExperimentBrushMatches(featureViewport.Background, Colors.Transparent)
                && ExperimentBrushMatches(featureScrollHost.Background, Colors.Transparent)
                && ExperimentBrushMatches(control.FeatureSelectorFrame.Background, theme.Panel)
                && ExperimentBrushMatches(selectedFeatureChrome.Background, theme.NavActive)
                && ExperimentBrushMatches(selectedFeatureChrome.BorderBrush, theme.PrimaryBorder),
            $"Experiment feature selector fell through to native list chrome under {theme.Id}");

        picker.IsDropDownOpen = false;
        control.MatrixBenchmarkPicker.IsEnabled = true;
        FocusExperimentControl(control.MatrixBenchmarkPicker, host, theme.Id);
        AssertExperimentFocusChrome(
            RequireExperimentTemplatePart<Border>(benchmarkToggle, "ToggleChrome"),
            theme,
            "matrix benchmark picker");

        FocusExperimentControl(control.ValidateMatrixButton, host, theme.Id);
        AssertExperimentFocusChrome(
            RequireExperimentTemplatePart<Border>(control.ValidateMatrixButton, "Chrome"),
            theme,
            "primary matrix action");

        control.MatrixRetryApprovedCheckBox.IsEnabled = true;
        FocusExperimentControl(control.MatrixRetryApprovedCheckBox, host, theme.Id);
        AssertExperimentFocusChrome(
            RequireExperimentTemplatePart<Border>(control.MatrixRetryApprovedCheckBox, "Box"),
            theme,
            "matrix retry approval");

        FocusExperimentControl(control.FeatureSelector, host, theme.Id);
        AssertExperimentFocusChrome(featureViewport, theme, "feature list", theme.Panel);
        FocusExperimentControl(selectedFeature, host, theme.Id);
        AssertExperimentFocusChrome(selectedFeatureChrome, theme, "feature list item");
    }

    static void AssertQaArtifactThemeChrome()
    {
        var control = new InAppQaInspectorControl();
        AttachArenaPresentationResources(control);
        ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
        foreach (var index in Enumerable.Range(0, 600))
        {
            control.GateList.Items.Add($"gate:theme-regression:{index}");
            control.ArtifactList.Items.Add($"artifact:theme-regression:{index}");
        }
        control.GateList.MaxHeight = 180;
        control.ArtifactList.MaxHeight = 180;
        control.ArtifactList.SelectedIndex = 0;
        var host = new Window
        {
            Content = control,
            Width = 960,
            Height = 700,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Show();
        try
        {
            host.Activate();
            control.EvidenceTabs.SelectedIndex = 1;
            host.UpdateLayout();
            AssertExperimentListVirtualization(control.GateList, host, "QA gate inventory");
            control.EvidenceTabs.SelectedIndex = 3;
            host.UpdateLayout();
            AssertExperimentListVirtualization(control.ArtifactList, host, "QA artifact inventory");
            foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
            {
                var theme = ThemePalette.Resolve(themeId);
                ApplyExperimentSurfaceTheme(control, theme);
                host.UpdateLayout();
                var viewport = RequireExperimentTemplatePart<Border>(control.ArtifactList, "ListViewport");
                var scrollHost = RequireExperimentTemplatePart<ScrollViewer>(control.ArtifactList, "ScrollHost");
                var selectedArtifact = control.ArtifactList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                    ?? throw new InvalidOperationException("QA artifact inventory did not realize its selected item.");
                var selectedArtifactChrome = RequireExperimentTemplatePart<Border>(selectedArtifact, "ItemChrome");
                Require(ExperimentBrushMatches(viewport.Background, Colors.Transparent)
                        && ExperimentBrushMatches(scrollHost.Background, Colors.Transparent)
                        && ExperimentBrushMatches(control.ArtifactListPane.Background, theme.Card)
                        && ExperimentBrushMatches(selectedArtifactChrome.Background, theme.NavActive)
                        && ExperimentBrushMatches(selectedArtifactChrome.BorderBrush, theme.PrimaryBorder),
                    $"QA artifact inventory fell through to native list chrome under {theme.Id}");

                FocusExperimentControl(control.ArtifactList, host, theme.Id);
                AssertExperimentFocusChrome(viewport, theme, "QA artifact list", theme.Card);
                FocusExperimentControl(selectedArtifact, host, theme.Id);
                AssertExperimentFocusChrome(selectedArtifactChrome, theme, "QA artifact list item");
            }
        }
        finally
        {
            host.Close();
        }
    }

    static void AssertExperimentListVirtualization(ListBox list, Window host, string label)
    {
        list.ApplyTemplate();
        var declaredPanel = list.ItemsPanel.LoadContent();
        var realizedPanel = FindExperimentVisualDescendant<VirtualizingStackPanel>(list);
        var initialRealizedCount = Enumerable.Range(0, list.Items.Count)
            .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem);
        Require(declaredPanel is VirtualizingStackPanel
                && realizedPanel is not null
                && VirtualizingPanel.GetIsVirtualizing(list)
                && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
                && list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem
                && initialRealizedCount > 0
                && initialRealizedCount < list.Items.Count,
            $"{label} did not retain a realized recycling VirtualizingStackPanel");

        var lastIndex = list.Items.Count - 1;
        list.SelectedIndex = lastIndex;
        list.ScrollIntoView(list.Items[lastIndex]);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));
        var lastContainer = list.ItemContainerGenerator.ContainerFromIndex(lastIndex) as ListBoxItem;
        var scrolledRealizedCount = Enumerable.Range(0, list.Items.Count)
            .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem);
        Require(lastContainer is not null
                && list.SelectedIndex == lastIndex
                && ReferenceEquals(list.SelectedItem, list.Items[lastIndex])
                && lastContainer.Focus()
                && lastContainer.IsKeyboardFocusWithin
                && list.IsKeyboardFocusWithin
                && scrolledRealizedCount > 0
                && scrolledRealizedCount < list.Items.Count,
            $"{label} did not preserve bounded realization, selection, and item focus after scrolling to the final row");

        list.SelectedIndex = 0;
        list.ScrollIntoView(list.Items[0]);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));
        Require(list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem
                && list.SelectedIndex == 0,
            $"{label} did not preserve selection while returning to the first virtualized row");
    }

    static void FocusExperimentControl(Control control, Window host, string themeId)
    {
        var label = string.IsNullOrWhiteSpace(control.Name)
            ? control.GetType().Name
            : control.Name;
        Require(control.FocusVisualStyle is not null,
            $"{label} lost its focus visual under {themeId}");
        Require(control.Focus(),
            $"{label} was not keyboard focusable under {themeId}");
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));
        Require(control.IsKeyboardFocusWithin,
            $"{label} did not retain keyboard focus under {themeId}");
    }

    static void AssertExperimentFocusChrome(
        Border chrome,
        ThemePalette theme,
        string label,
        Color? effectiveBackground = null)
    {
        var focus = ExperimentBrushColor(chrome.BorderBrush, $"{label} focus border");
        var background = effectiveBackground
            ?? ExperimentBrushColor(chrome.Background, $"{label} background");
        var contrast = ExperimentContrastRatio(focus, background);
        Require(focus == theme.PrimaryBorder && contrast >= 3.0,
            $"{label} focus under {theme.Id} was not palette-bound or 3:1 contrasting (actual {contrast:F2}:1)");
    }

    static void AssertPaletteAwareFocusVisualContract()
    {
        var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Theming/DesignTokens.xaml"));
        var start = xaml.IndexOf("x:Key=\"Arena.FocusVisual\"", StringComparison.Ordinal);
        var focusVisual = start >= 0 ? xaml[start..] : "";
        Require(focusVisual.Contains("PrimaryBorderBrush", StringComparison.Ordinal)
                && !focusVisual.Contains("BorderBrush=\"{DynamicResource Arena.Brush.FocusRing", StringComparison.Ordinal),
            "shared Arena focus visual did not use the active palette border brush");
    }

    static void AttachArenaPresentationResources(FrameworkElement element)
    {
        var assemblyName = typeof(ExperimentLabControl).Assembly.GetName().Name
            ?? throw new InvalidOperationException("WPF assembly name is unavailable.");
        foreach (var relativePath in new[]
                 {
                     "UI/Theming/ThemeBrushes.xaml",
                     "UI/Theming/DesignTokens.xaml",
                     "UI/Theming/ControlStyles.xaml"
                 })
        {
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/{assemblyName};component/{relativePath}", UriKind.Relative)
            });
        }
    }

    static void ApplyExperimentSurfaceTheme(FrameworkElement element, ThemePalette theme)
    {
        static SolidColorBrush ThemeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        element.Resources["AppBackgroundBrush"] = ThemeBrush(theme.AppBackground);
        element.Resources["TopBarBrush"] = ThemeBrush(theme.TopBar);
        element.Resources["PanelBrush"] = ThemeBrush(theme.Panel);
        element.Resources["CardBrush"] = ThemeBrush(theme.Card);
        element.Resources["InputBrush"] = ThemeBrush(theme.Input);
        element.Resources["ControlBorderBrush"] = ThemeBrush(theme.Border);
        element.Resources["TextBrush"] = ThemeBrush(theme.Text);
        element.Resources["MutedTextBrush"] = ThemeBrush(theme.MutedText);
        element.Resources["SecondaryTextBrush"] = ThemeBrush(theme.MutedText);
        element.Resources["PrimaryBrush"] = ThemeBrush(theme.Primary);
        element.Resources["PrimaryBorderBrush"] = ThemeBrush(theme.PrimaryBorder);
        element.Resources["AccentBrush"] = ThemeBrush(theme.PrimaryBorder);
        element.Resources["AssistBrush"] = ThemeBrush(theme.Assist);
        element.Resources["AssistBorderBrush"] = ThemeBrush(theme.AssistBorder);
        element.Resources["DangerBrush"] = ThemeBrush(theme.Danger);
        element.Resources["DangerBorderBrush"] = ThemeBrush(theme.DangerBorder);
        element.Resources["DangerTextBrush"] = ThemeBrush(theme.DangerText);
        element.Resources["DisabledBrush"] = ThemeBrush(theme.Disabled);
        element.Resources["DisabledBorderBrush"] = ThemeBrush(theme.DisabledBorder);
        element.Resources["DisabledTextBrush"] = ThemeBrush(theme.DisabledText);
        element.Resources["HoverBorderBrush"] = ThemeBrush(theme.HoverBorder);
        element.Resources["NavHoverBrush"] = ThemeBrush(theme.NavHover);
        element.Resources["NavActiveBrush"] = ThemeBrush(theme.NavActive);
        element.Resources["NavPressedBrush"] = ThemeBrush(theme.NavPressed);
        element.Resources["PressedPrimaryBrush"] = ThemeBrush(theme.PressedPrimary);
        element.Resources["OverlayBrush"] = ThemeBrush(theme.Overlay);
    }

    static T RequireExperimentTemplatePart<T>(Control control, string name)
        where T : DependencyObject
    {
        control.ApplyTemplate();
        return control.Template.FindName(name, control) as T
            ?? throw new InvalidOperationException(
                $"{control.GetType().Name} did not realize required template part '{name}'.");
    }

    static bool ExperimentBrushMatches(Brush? brush, Color expected) =>
        brush is SolidColorBrush solid && solid.Color == expected;

    static T? FindExperimentVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var descendant = FindExperimentVisualDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    static Color ExperimentBrushColor(Brush? brush, string label)
    {
        if (brush is SolidColorBrush solid)
        {
            return solid.Color;
        }

        throw new InvalidOperationException($"{label} was not a solid theme brush.");
    }

    static double ExperimentContrastRatio(Color first, Color second)
    {
        var firstLuminance = ExperimentRelativeLuminance(first);
        var secondLuminance = ExperimentRelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    static double ExperimentRelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R))
            + (0.7152 * Channel(color.G))
            + (0.0722 * Channel(color.B));
    }

    static void ExperimentLabControlPlaneExposesOnlySafeRegisteredFeatureState()
    {
        RunStaTest(() =>
        {
            var control = new ExperimentLabControl();
            var externalFeatures = new (string Key, string Title)[]
            {
                ("context-prompt-inspector", "Context & Prompt Inspector"),
                ("agent-memory-debugger", "Agent Memory Debugger"),
                ("fault-injection", "Fault-Injection Lab"),
                ("routing-optimizer", "Routing Optimizer"),
                ("in-app-qa-inspector", "In-App QA Inspector")
            };
            foreach (var feature in externalFeatures)
            {
                control.RegisterFeature(new ExperimentLabFeatureRegistration(
                    feature.Key,
                    feature.Title,
                    "SECRET_PROMPT_SUMMARY",
                    "C:\\private\\provider-token.txt",
                    new System.Windows.Controls.TextBlock { Text = "SECRET_MEMORY_CONTENT" }));
            }

            var initial = control.ReadControlPlaneState();
            Require(initial.Features.Count == 10, "control-plane state did not expose all ten registered production feature identities");
            Require(initial.Features.All(item => item.Registered && item.Selectable && !item.Busy), "idle registered features were not reported as registered, selectable, and idle");
            Require(initial.Features.Select(item => item.Status).ToHashSet(StringComparer.Ordinal).SetEquals(["selected", "registered"]), "feature status escaped its bounded control-plane vocabulary");

            var json = AIArenaControlPlaneProtocol.Serialize(initial);
            Require(!json.Contains("SECRET_", StringComparison.Ordinal)
                    && !json.Contains("provider-token", StringComparison.Ordinal)
                    && !json.Contains("Summary", StringComparison.Ordinal)
                    && !json.Contains("HelpText", StringComparison.Ordinal)
                    && !json.Contains("Content", StringComparison.Ordinal),
                "Experiment Lab control state leaked feature content, help, or registration implementation detail");

            var selectedBeforeInvalid = initial.SelectedKey;
            Require(!control.TrySelectRegisteredFeature("not-registered", out _)
                    && control.SelectedFeatureKey == selectedBeforeInvalid,
                "control-plane selection accepted or applied an unregistered feature key");
            Require(control.TrySelectRegisteredFeature("in-app-qa-inspector", out var changed)
                    && changed
                    && control.SelectedFeatureKey == "in-app-qa-inspector",
                "control-plane selection did not select an exact registered feature key");

            control.SetBusy(true);
            var busy = control.ReadControlPlaneState();
            Require(busy.Busy
                    && busy.Features.Single(item => item.Key == "in-app-qa-inspector").Busy
                    && busy.Features.Count(item => item.Busy) == 1
                    && busy.Features.All(item => item.Registered && !item.Selectable)
                    && busy.Features.Single(item => item.Busy).Status == "busy",
                "control-plane state did not distinguish registered surfaces from temporarily blocked selection");
            control.SetBusy(false);
        });

        var adapter = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs"));
        Require(adapter.Contains("case AIArenaControlCommands.ExperimentState:", StringComparison.Ordinal)
                && adapter.Contains("case AIArenaControlCommands.ExperimentFeatureSelect:", StringComparison.Ordinal)
                && adapter.Contains("TrySelectRegisteredFeature(key, out var changed)", StringComparison.Ordinal)
                && adapter.Contains("ExperimentLab.RequestFeatureSelectionRefresh(key.Trim())", StringComparison.Ordinal)
                && adapter.Contains("OpenExperimentLabForControlPlaneSelection();", StringComparison.Ordinal)
                && adapter.Contains("await refresh.WaitAsync(cancellationToken)", StringComparison.Ordinal),
            "Experiment Lab control-plane commands did not use the real allowlisted selection and contained refresh boundary");
        var selectionCaseStart = adapter.IndexOf("case AIArenaControlCommands.ExperimentFeatureSelect:", StringComparison.Ordinal);
        var navigationCaseStart = adapter.IndexOf("case AIArenaControlCommands.NavigationSelect:", selectionCaseStart, StringComparison.Ordinal);
        var selectionCase = adapter[selectionCaseStart..navigationCaseStart];
        Require(!selectionCase.Contains("ExecuteMatrixAsync", StringComparison.Ordinal)
                && !selectionCase.Contains("RunFaultProbe", StringComparison.Ordinal)
                && !selectionCase.Contains("ApplyRoute", StringComparison.Ordinal),
            "Experiment Lab feature selection gained an experiment mutation or execution path");
    }

    static void ExperimentLabFeatureSelectionContainsRefreshFailuresAndStaleCompletions()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-experiment-selection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var control = new ExperimentLabControl();
                var packRefreshCalls = 0;
                var staleRefreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseStaleRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    new SessionStore(root),
                    new FixedCollaborateModelClient("unused"),
                    () => null,
                    (_, _) => Task.CompletedTask,
                    root,
                    featureRefreshOverride: async (key, _) =>
                    {
                        if (key == "packs")
                        {
                            packRefreshCalls++;
                            if (packRefreshCalls == 1)
                            {
                                await Task.Yield();
                                throw new IOException("Injected pack-store read failure.");
                            }

                            staleRefreshStarted.TrySetResult(true);
                            await releaseStaleRefresh.Task;
                            throw new InvalidDataException("Injected stale pack-store failure.");
                        }

                        if (key == "rubrics")
                        {
                            control.SetRubricStatus("Latest rubric selection refreshed.");
                        }
                    });
                control.Initialize(coordinator);

                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var dispatcherFailures = 0;
                System.Windows.Threading.DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
                {
                    dispatcherFailures++;
                    args.Handled = true;
                };
                dispatcher.UnhandledException += handler;
                try
                {
                    control.FeatureSelector.SelectedItem = control.RegisteredFeatures.Single(item => item.Key == "packs");
                    RunExperimentDispatcherTask(() => coordinator.DebugFeatureSelectionRefreshTask);
                    Require(
                        control.PackStatusText.Text == "Feature refresh failed safely (IOException); persisted Experiment Lab evidence was not rewritten.",
                        "current feature refresh failure was not reported honestly at the selected feature boundary");
                    Require(control.ReadControlPlaneState().Features.Single(item => item.Key == "packs").Status == "refresh-failed",
                        "control-plane state claimed readiness after a contained feature refresh failure");
                    Require(dispatcherFailures == 0, "feature refresh failure escaped through the WPF dispatcher");

                    control.FeatureSelector.SelectedItem = control.RegisteredFeatures.Single(item => item.Key == "rubrics");
                    RunExperimentDispatcherTask(() => coordinator.DebugFeatureSelectionRefreshTask);
                    control.FeatureSelector.SelectedItem = control.RegisteredFeatures.Single(item => item.Key == "packs");
                    RunExperimentDispatcherTask(() => staleRefreshStarted.Task);
                    control.FeatureSelector.SelectedItem = control.RegisteredFeatures.Single(item => item.Key == "rubrics");
                    releaseStaleRefresh.TrySetResult(true);
                    RunExperimentDispatcherTask(() => coordinator.DebugFeatureSelectionRefreshTask);

                    Require(control.RubricStatusText.Text == "Latest rubric selection refreshed.", "latest feature refresh did not win after overlap");
                    Require(control.ReadControlPlaneState().Features.Single(item => item.Key == "packs").Status == "superseded",
                        "superseded feature refresh remained falsely pending");
                    Require(
                        !control.PackStatusText.Text.Contains(nameof(InvalidDataException), StringComparison.Ordinal),
                        "superseded feature refresh overwrote status with a stale failure");
                    Require(dispatcherFailures == 0, "superseded feature refresh escaped through the WPF dispatcher");
                }
                finally
                {
                    dispatcher.UnhandledException -= handler;
                    releaseStaleRefresh.TrySetResult(true);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabCoordinatorPersistsForkPackRubricAndClaimEvidence()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-experiment-lab-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var store = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = "test-token-never-persisted-by-experiments",
                    Model = "local-test-model",
                    Timeout = 30,
                    Temperature = 0.7,
                    MaxOutputTokens = 128,
                    ContextLength = 4096,
                    Reasoning = ""
                };
                snapshot.Engine.Messages =
                [
                    new DialogueMessage { MessageId = "message:first", Turn = 1, SpeakerId = "agent:alpha", Speaker = "Alpha", Text = "Private first transcript text.", CreatedAt = 1 },
                    new DialogueMessage { MessageId = "message:second", Turn = 2, SpeakerId = "agent:beta", Speaker = "Beta", Text = "Private second transcript text.", CreatedAt = 2 }
                ];
                snapshot.Engine.TurnCount = 2;
                store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
                var currentSession = "active";
                var loadedChild = "";
                var clock = new FixedExperimentTimeProvider(new DateTimeOffset(2035, 2, 3, 4, 5, 6, TimeSpan.Zero));
                var control = new ExperimentLabControl();
                var modelClient = new FixedCollaborateModelClient("experiment reply");
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    store,
                    modelClient,
                    () => currentSession,
                    (sessionId, _) =>
                    {
                        loadedChild = sessionId;
                        currentSession = sessionId;
                        return Task.CompletedTask;
                    },
                    root,
                    clock);
                control.Initialize(coordinator);

                var cursors = coordinator.ListForkCursorsAsync().GetAwaiter().GetResult();
                Require(cursors.Length == 2 && !cursors.Any(item => item.DisplayText.Contains("Private", StringComparison.Ordinal)), "fork selector leaked transcript content or lost stable cursors");
                var fork = coordinator.ForkAndLoadAsync(cursors[1].MessageId, "active-fork-test").GetAwaiter().GetResult();
                Require(fork.TargetSessionId == loadedChild && store.LoadSnapshotAsync(loadedChild).GetAwaiter().GetResult() is not null, "historical fork was not created and loaded through the coordinator boundary");
                var countReceipt = ExperimentLabCoordinator.FormatForkReceipt(fork with
                {
                    ExcludedMemoryEntryCount = 2,
                    UnprojectableMemoryEntryCount = 2,
                    HistoricalSetupProjectionUnavailable = true
                });
                Require(countReceipt.Contains("2 memory entry/entries omitted (2 unprojectable)", StringComparison.Ordinal)
                        && !countReceipt.Contains("4 memory", StringComparison.Ordinal),
                    "fork receipt double-counted unprojectable entries inside the omitted total");

                var packInput = new ExperimentPackInput("Local verification", "1.0.0", "4");
                var scenario = coordinator.CreateScenarioPackAsync(packInput).GetAwaiter().GetResult();
                Require(coordinator.SaveScenarioPackAsync(scenario).GetAwaiter().GetResult().Succeeded, "scenario pack was not persisted");
                Require(coordinator.ListScenarioPacksAsync().GetAwaiter().GetResult().Artifacts.Single().ContentFingerprint == scenario.ContentFingerprint, "scenario pack did not reload canonically");

                var executionRubric = coordinator.CreateRubric(new ExperimentRubricInput("Execution rubric", "1.0.0", "Completes the bounded turn"));
                Require(coordinator.SaveRubricContractAsync(executionRubric).GetAwaiter().GetResult().Succeeded, "execution rubric was not persisted");
                var runnableBenchmark = coordinator.CreateBenchmarkPack(packInput, scenario, [executionRubric.Id]);
                Require(runnableBenchmark.Cases.Single().RequiredProviderCapabilities.IsEmpty, "locally authored benchmark invented an unprovable provider capability");
                Require(coordinator.SaveBenchmarkPackAsync(runnableBenchmark).GetAwaiter().GetResult().Succeeded, "runnable benchmark was not persisted");
                RunExperimentDispatcherTask(() => coordinator.ReconcileMatrixSourcesAsync());
                control.SelectMatrixBenchmark(runnableBenchmark.Id);
                var firstDraft = control.ReadMatrixInput();
                Require(firstDraft.ScenarioPackId == scenario.Id
                        && firstDraft.ProviderProfileIds == "provider:shared"
                        && firstDraft.BenchmarkPackId == runnableBenchmark.Id
                        && firstDraft.RubricIds == executionRubric.Id,
                    "fresh Matrix Runner did not reconcile its first executable draft from canonical stores");
                var firstPreview = ExperimentLabCoordinator.BuildMatrixPreview(firstDraft, clock.GetUtcNow());
                var firstResolution = coordinator.ResolveMatrixExecutionAsync(firstPreview.Contract).GetAwaiter().GetResult();
                Require(firstResolution.IsAvailable, "locally authored benchmark did not resolve on the first validation attempt");
                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                var firstRunHistory = coordinator.ListRunHistoryAsync().GetAwaiter().GetResult();
                Require(firstRunHistory.Runs.Length == firstPreview.Expansion.Cells.Length
                        && modelClient.CompleteCalls > 0,
                    "first Matrix Run did not use the real isolated executor and durable runner path");
                var serializedRun = string.Join('\n', firstRunHistory.Runs.Select(item => ArenaContractCodec.Serialize(item)));
                Require(!serializedRun.Contains("test-token-never-persisted-by-experiments", StringComparison.Ordinal),
                    "durable Matrix Run history persisted a provider credential");
                var callsBeforeNoApproval = modelClient.CompleteCalls;
                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                Require(modelClient.CompleteCalls == callsBeforeNoApproval
                        && coordinator.ListRunHistoryAsync().GetAwaiter().GetResult().Runs.All(item => item.Attempts == 1),
                    "terminal cells retried without explicit operator approval");
                control.SetMatrixRetryApproval(true);
                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                var retriedHistory = coordinator.ListRunHistoryAsync().GetAwaiter().GetResult();
                Require(modelClient.CompleteCalls > callsBeforeNoApproval
                        && retriedHistory.Runs.All(item => item.Attempts == 2 && item.TrialIds.Length == 2),
                    "explicit matrix retry did not preserve prior evidence and create a new attempt/trial");
                Require(!control.ConsumeMatrixRetryApproval(), "one-shot retry approval remained armed after execution");
                var runtimeOnly = store.LoadSnapshotAsync(currentSession).GetAwaiter().GetResult()!;
                runtimeOnly.Engine.Messages.Add(new DialogueMessage { MessageId = "message:runtime-only", Turn = 3, SpeakerId = "agent:alpha", Speaker = "Alpha", Text = "Runtime-only message.", CreatedAt = 3 });
                runtimeOnly.Engine.TurnCount++;
                store.SaveSnapshotAsync(runtimeOnly, currentSession).GetAwaiter().GetResult();
                var sameSetup = coordinator.CreateScenarioPackAsync(packInput).GetAwaiter().GetResult();
                Require(sameSetup.ContentFingerprint == scenario.ContentFingerprint, "runtime-only state changed replayable setup identity");
                var behaviorChange = store.LoadSnapshotAsync(currentSession).GetAwaiter().GetResult()!;
                behaviorChange.Engine.Agents[0].Persona += " changed";
                store.SaveSnapshotAsync(behaviorChange, currentSession).GetAwaiter().GetResult();
                var changedSetup = coordinator.CreateScenarioPackAsync(packInput).GetAwaiter().GetResult();
                Require(changedSetup.ContentFingerprint != scenario.ContentFingerprint
                        && changedSetup.Scenarios[0].SetupFingerprint != scenario.Scenarios[0].SetupFingerprint,
                    "behavior-bearing setup change reused scenario content identity");
                Require(scenario.Scenarios.Single().MatchSetupReference == $"session:{fork.TargetSessionId}", "scenario pack did not persist an explicit resolvable session reference");
                RequireExperimentThrows<ExperimentLabInputException>(
                    () => coordinator.CreateBenchmarkPack(packInput, scenario, []),
                    "benchmark invented a rubric reference when no rubric was available");
                var benchmark = coordinator.CreateBenchmarkPack(packInput with { Version = "1.0.1" }, scenario, ["rubric:test"]);
                Require(coordinator.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult().Succeeded, "benchmark pack was not persisted");

                var rubric = coordinator.CreateRubric(new ExperimentRubricInput("Grounded response", "1.0.0", "Supported quality"));
                Require(coordinator.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded, "rubric version was not persisted");
                var human = coordinator.CreateRubricObservation(rubric, new(
                    "human", "4", "", "subject:a", "", false, false, "A"));
                var unavailable = coordinator.CreateRubricObservation(rubric, new(
                    "model", "0", "", "subject:a", "", true, false, "Unavailable"));
                Require(human.HumanResults.Length == 1 && human.DeterministicResults.IsEmpty && human.ModelJudgeResults.IsEmpty, "human observation crossed result partitions");
                Require(unavailable.ModelJudgeResults.Single().WeightedScoreA is null
                        && unavailable.ModelJudgeResults.Single().Criteria.Single().Evidence.State == ArenaEvidenceState.Unavailable,
                    "unavailable model evidence was converted into a numeric claim");
                RequireExperimentThrows<ExperimentLabInputException>(
                    () => coordinator.CreateRubricObservation(rubric, new(
                        "deterministic", "5", "", "subject:a", "", false, false, "A")),
                    "typed score was mislabeled as a deterministic measurement");
                RequireExperimentThrows<ExperimentLabInputException>(
                    () => coordinator.CreateRubricObservation(rubric, new(
                        "model", "5", "", "subject:a", "", false, false, "A")),
                    "typed score was mislabeled as an invoked model-judge result");
                RequireExperimentThrows<ExperimentLabInputException>(
                    () => coordinator.CreateRubricObservation(rubric, new(
                        "human", "4", "4", "subject:a", "subject:b", false, true, "A")),
                    "single-step form claimed a blind pairwise judgment");

                var blindService = new ArenaRubricService();
                var blind = blindService.BeginBlindPairwise(
                    rubric,
                    "evaluation:concealed",
                    "subject:alpha",
                    "subject:beta",
                    "seed:concealed",
                    clock.GetUtcNow());
                var concealedJson = JsonSerializer.Serialize(blind.View);
                Require(!concealedJson.Contains("subject:alpha", StringComparison.Ordinal)
                        && !concealedJson.Contains("subject:beta", StringComparison.Ordinal),
                    "judge-facing pairwise view revealed subject mapping before finalize");
                var blindResult = blind.Finalize(
                    [new(
                        "result:concealed",
                        "evaluator:blind",
                        ArenaRubricJudgmentSource.Human,
                        "reviewer:local",
                        ArenaPairwisePreference.A,
                        [new("criterion:primary", 4m, 2m, new(
                            "evidence:concealed", ArenaEvidenceState.Observed, "Reviewer captured a concealed preference.", "artifact:concealed-view"))],
                        new("provenance:concealed", ArenaEvidenceState.Observed, "Reviewer submitted the concealed view.", "artifact:concealed-view"))],
                    clock.GetUtcNow().AddTicks(1),
                    []);
                Require(blindResult.BlindReveal is not null
                        && blindResult.SubjectReferenceIds.ToHashSet(StringComparer.Ordinal).SetEquals(["subject:alpha", "subject:beta"]),
                    "finalized pairwise result did not reveal its stable mapping after judgment");
                Require(coordinator.SaveRubricResultAsync(human).GetAwaiter().GetResult().Succeeded
                        && coordinator.SaveRubricResultAsync(unavailable).GetAwaiter().GetResult().Succeeded,
                    "separated rubric results were not persisted");

                var ledger = coordinator.CreateClaimLedger(new("experiment:local", "branch:current"));
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded, "claim ledger was not persisted");
                ledger = coordinator.AddClaim(ledger, cursors[0], "First bounded claim.");
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded, "first claim was not appended");
                ledger = coordinator.AddClaim(ledger, cursors[1], "Second bounded claim.");
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded, "second claim was not appended");
                ledger = coordinator.LinkContradiction(ledger, ledger.Claims[0].Id, ledger.Claims[1].Id);
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded, "contradiction was not persisted monotonically");
                var persistedLedger = coordinator.ListClaimLedgersAsync().GetAwaiter().GetResult().Artifacts.Single();
                Require(persistedLedger.Claims.All(item => item.Status == ArenaClaimStatus.Contradicted), "contradiction did not update both claim endpoints");
                var json = ArenaContractCodec.Serialize(persistedLedger);
                Require(!json.Contains("Private first transcript text", StringComparison.Ordinal)
                        && !json.Contains("Private second transcript text", StringComparison.Ordinal)
                        && !json.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "ledger persisted transcript content or an absolute path");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabRejectsClaimPickerAcrossSessionChange()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-claim-session-generation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var sessions = new SessionStore(root);
                ArenaSnapshot Source(string messageId, string text) => new()
                {
                    Engine = new EngineSnapshot
                    {
                        Agents = SessionStore.CreateDefaultSnapshot().Engine.Agents,
                        Messages =
                        [
                            new DialogueMessage
                            {
                                MessageId = messageId,
                                Turn = 1,
                                SpeakerId = "agent:alpha",
                                Speaker = "Alpha",
                                Text = text,
                                CreatedAt = 1
                            }
                        ],
                        TurnCount = 1
                    }
                };
                sessions.SaveSnapshotAsync(Source("message:a", "private A provenance"), "source-a").GetAwaiter().GetResult();
                sessions.SaveSnapshotAsync(Source("message:b", "private B provenance"), "source-b").GetAwaiter().GetResult();
                var branchA = sessions.ForkSessionAsync("source-a", "branch-a").GetAwaiter().GetResult();
                var branchB = sessions.ForkSessionAsync("source-b", "branch-b").GetAwaiter().GetResult();
                var activeSession = branchA.TargetSessionId;
                var control = new ExperimentLabControl();
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => activeSession,
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(new DateTimeOffset(2038, 1, 2, 3, 4, 5, TimeSpan.Zero)));
                control.Initialize(coordinator);
                coordinator.NotifyActiveSessionChanged(activeSession);

                var branchASnapshot = sessions.LoadSnapshotAsync(activeSession).GetAwaiter().GetResult()!;
                var ledger = coordinator.CreateClaimLedger(new(
                    "experiment:claim-session-generation",
                    branchASnapshot.BranchReceipt!.Id));
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded,
                    "session-generation claim ledger could not be persisted");
                RunExperimentDispatcherTask(coordinator.RefreshClaimsAsync);
                var staleMessage = (ExperimentForkCursorItem)control.SelectedClaimMessage!;
                ledger = coordinator.AddClaim(ledger, staleMessage, "Original A claim.");
                Require(coordinator.SaveClaimLedgerAsync(ledger).GetAwaiter().GetResult().Succeeded,
                    "original session A claim could not be persisted");
                RunExperimentDispatcherTask(coordinator.RefreshClaimsAsync);
                var staleLedger = (ExperimentLedgerItem)control.SelectedLedger!;
                var beforeSwitchJson = ArenaContractCodec.Serialize(
                    coordinator.ListClaimLedgersAsync().GetAwaiter().GetResult().Artifacts.Single(item => item.Id == ledger.Id));

                activeSession = branchB.TargetSessionId;
                coordinator.NotifyActiveSessionChanged(activeSession);
                Require(control.SelectedClaimMessage is null && control.SelectedLedger is null,
                    "session notification did not synchronously clear cached claim provenance state");
                RunExperimentDispatcherTask(coordinator.RefreshClaimsAsync);
                Require(control.SelectedLedger is null,
                    "Claims refresh repopulated a session A ledger while session B was active");

                // Reinsert the stale objects to prove AddClaim independently
                // re-resolves both message and branch against the active session.
                control.SetClaimLedgers([staleLedger]);
                control.SetClaimMessages([staleMessage]);
                control.ClaimSummaryText.Text = "Must remain unavailable.";
                RunExperimentDispatcherTask(coordinator.AddClaimAsync);
                control.SetClaimLedgers([staleLedger]);
                control.SetClaims([new ExperimentClaimItem(ledger.Claims.Single())]);
                RunExperimentDispatcherTask(() => coordinator.ReviewSelectedClaimAsync("supported"));

                var copiedSessionId = "branch-a-copied-as-b";
                var copiedBranchA = sessions.LoadSnapshotAsync(branchA.TargetSessionId).GetAwaiter().GetResult()!;
                copiedBranchA.PersistenceRevision = 0;
                sessions.SaveSnapshotAsync(copiedBranchA, copiedSessionId).GetAwaiter().GetResult();
                activeSession = copiedSessionId;
                coordinator.NotifyActiveSessionChanged(activeSession);
                RunExperimentDispatcherTask(coordinator.RefreshClaimsAsync);
                Require(control.SelectedLedger is null,
                    "a copied A branch receipt exposed its ledger under a different active session identity");
                control.SetClaimLedgers([staleLedger]);
                control.SetClaims([new ExperimentClaimItem(ledger.Claims.Single())]);
                RunExperimentDispatcherTask(() => coordinator.ReviewSelectedClaimAsync("supported"));

                var staleCursorLoadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseStaleCursorLoad = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                activeSession = branchA.TargetSessionId;
                var staleControl = new ExperimentLabControl();
                using (var staleCoordinator = new ExperimentLabCoordinator(
                    staleControl,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => activeSession,
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(new DateTimeOffset(2038, 1, 2, 3, 4, 5, TimeSpan.Zero)),
                    forkCursorLoadOverride: async (sessionId, token) =>
                    {
                        if (sessionId.Equals(branchA.TargetSessionId, StringComparison.Ordinal))
                        {
                            staleCursorLoadStarted.TrySetResult(true);
                            await releaseStaleCursorLoad.Task.WaitAsync(token);
                            return [staleMessage];
                        }
                        return [];
                    }))
                {
                    staleControl.Initialize(staleCoordinator);
                    staleCoordinator.NotifyActiveSessionChanged(activeSession);
                    RunExperimentDispatcherTask(async () =>
                    {
                        var staleRefresh = staleCoordinator.RefreshForkCursorsAsync();
                        await staleCursorLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        activeSession = branchB.TargetSessionId;
                        staleCoordinator.NotifyActiveSessionChanged(activeSession);
                        releaseStaleCursorLoad.TrySetResult(true);
                        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(2));
                    });
                    Require(staleControl.SelectedForkCursor is null
                            && staleControl.SelectedClaimMessage is null
                            && staleControl.ForkCursorPicker.Items.Count == 0
                            && staleControl.ClaimMessagePicker.Items.Count == 0,
                        "an in-flight A cursor refresh repopulated claim provenance after switching to B");
                }
                var persisted = coordinator.ListClaimLedgersAsync().GetAwaiter().GetResult().Artifacts
                    .Single(item => item.Id == ledger.Id);
                Require(ArenaContractCodec.Serialize(persisted) == beforeSwitchJson
                        && control.ClaimStatusText.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                        && !ArenaContractCodec.Serialize(persisted).Contains("Must remain unavailable", StringComparison.Ordinal),
                    "session B accepted stale session A add/review provenance into its claim ledger");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabRestoresNewestRepresentableDefinition()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-restore-order-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2038, 2, 3, 4, 5, 6, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                sessions.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "active").GetAwaiter().GetResult();
                ExperimentMatrixPreviewResult Definition(string title, DateTimeOffset createdAt) =>
                    ExperimentLabCoordinator.BuildMatrixPreview(
                        new(
                            title,
                            "scenario-pack:restore-order",
                            "provider:shared",
                            "temperature",
                            "0.2",
                            "1",
                            "1",
                            "1",
                            "",
                            "rubric:restore-order"),
                        createdAt);

                var definitions = new ExperimentDefinitionStore(Path.Combine(root, "experimentation"));
                var old = Definition("D1 old interrupted", at);
                Require(definitions.SaveAsync(old.Contract).GetAwaiter().GetResult().Succeeded,
                    "D1 draft could not be persisted");
                var oldOwner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Require(definitions.SaveForExecutionAsync(
                            oldOwner,
                            old.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                        "D1 could not enter Running");
                }
                finally
                {
                    oldOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                var newerDraft = Definition("D2 newer draft", at.AddHours(1));
                Require(definitions.SaveAsync(newerDraft.Contract).GetAwaiter().GetResult().Succeeded,
                    "D2 draft could not be persisted");
                var draftControl = new ExperimentLabControl();
                using (var draftCoordinator = new ExperimentLabCoordinator(
                    draftControl,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddHours(2))))
                {
                    draftControl.Initialize(draftCoordinator);
                    RunExperimentDispatcherTask(draftCoordinator.RefreshRunHistoryAsync);
                    Require(draftControl.ReadMatrixInput().Title == "D2 newer draft"
                            && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions
                                .Single(item => item.Id == old.Contract.Id).Status == ArenaExperimentStatus.Interrupted
                            && !draftControl.MatrixStatusText.Text.Contains(
                                "Recovered abandoned Running matrix state",
                                StringComparison.Ordinal),
                        "older recovered D1 either pinned restoration or was misreported as the selected newer D2 Draft");
                }

                var newerAbandoned = Definition("D3 newest abandoned running", at.AddHours(3));
                Require(definitions.SaveAsync(newerAbandoned.Contract).GetAwaiter().GetResult().Succeeded,
                    "D3 draft could not be persisted");
                var newestOwner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Require(definitions.SaveForExecutionAsync(
                            newestOwner,
                            newerAbandoned.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                        "D3 could not enter Running");
                }
                finally
                {
                    newestOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var runningControl = new ExperimentLabControl();
                using var runningCoordinator = new ExperimentLabCoordinator(
                    runningControl,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddHours(4)));
                runningControl.Initialize(runningCoordinator);
                RunExperimentDispatcherTask(runningCoordinator.RefreshRunHistoryAsync);
                Require(runningControl.ReadMatrixInput().Title == "D3 newest abandoned running"
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions
                            .Single(item => item.Id == newerAbandoned.Contract.Id).Status == ArenaExperimentStatus.Interrupted
                        && runningControl.MatrixStatusText.Text.Contains(
                            "Recovered abandoned Running matrix state",
                            StringComparison.Ordinal),
                    "newest representable abandoned Running definition was not recovered and selected");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabRestoresInterruptedMatrixOnlyAfterApproval()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-restart-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2036, 2, 3, 4, 5, 6, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                var source = SessionStore.CreateDefaultSnapshot();
                source.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = "restart-test-token-never-persisted",
                    Model = "local-restart-model",
                    Timeout = 30,
                    Temperature = 0.4,
                    MaxOutputTokens = 96,
                    ContextLength = 4096
                };
                sessions.SaveSnapshotAsync(source, "active").GetAwaiter().GetResult();
                sessions.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "prior-child").GetAwaiter().GetResult();

                ExperimentMatrixPreviewResult preview;
                using (var firstCoordinator = new ExperimentLabCoordinator(
                    new ExperimentLabControl(),
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at)))
                {
                    var scenario = firstCoordinator.CreateScenarioPackAsync(
                        new("Restart scenario", "1.0.0", "3")).GetAwaiter().GetResult();
                    Require(firstCoordinator.SaveScenarioPackAsync(scenario).GetAwaiter().GetResult().Succeeded,
                        "restart scenario pack could not be persisted");
                    var rubric = firstCoordinator.CreateRubric(new("Restart rubric", "1.0.0", "Completes the cell"));
                    Require(firstCoordinator.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded,
                        "restart rubric could not be persisted");
                    var benchmark = firstCoordinator.CreateBenchmarkPack(
                        new("Restart benchmark", "1.0.0", "3"),
                        scenario,
                        [rubric.Id]);
                    Require(firstCoordinator.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult().Succeeded,
                        "restart benchmark could not be persisted");
                    var input = new ExperimentMatrixInput(
                        "Restart matrix",
                        scenario.Id,
                        "provider:shared",
                        "temperature",
                        "0.2, 0.7, 0.9",
                        "1",
                        "3",
                        "2",
                        benchmark.Id,
                        rubric.Id);
                    preview = ExperimentLabCoordinator.BuildMatrixPreview(input, at);
                }

                var experimentRoot = Path.Combine(root, "experimentation");
                var definitions = new ExperimentDefinitionStore(experimentRoot);
                Require(definitions.SaveAsync(preview.Contract).GetAwaiter().GetResult().Succeeded,
                    "restart matrix draft could not be persisted");
                var definitionOwner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Require(definitions.SaveForExecutionAsync(
                            definitionOwner,
                            preview.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                        "restart matrix could not enter Running before simulated process exit");
                }
                finally
                {
                    definitionOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var firstCell = preview.Expansion.Cells[0];
                var priorCompleted = new ArenaExperimentRunContract(
                    ArenaContractSchemas.ExperimentRun,
                    firstCell.RunId,
                    at,
                    firstCell.ExperimentId,
                    firstCell.ExperimentFingerprint,
                    firstCell.VariantFingerprint,
                    firstCell.Repetition,
                    firstCell.CellKey,
                    ArenaExperimentRunState.Completed,
                    1,
                    at,
                    ["trial:prior"],
                    null,
                    [new(
                        "evidence:prior-child",
                        ArenaEvidenceState.Observed,
                        "A prior isolated child session was created.",
                        "prior-child")]);
                var interruptedCell = preview.Expansion.Cells[1];
                var priorInterrupted = new ArenaExperimentRunContract(
                    ArenaContractSchemas.ExperimentRun,
                    interruptedCell.RunId,
                    at,
                    interruptedCell.ExperimentId,
                    interruptedCell.ExperimentFingerprint,
                    interruptedCell.VariantFingerprint,
                    interruptedCell.Repetition,
                    interruptedCell.CellKey,
                    ArenaExperimentRunState.Running,
                    1,
                    at,
                    ["trial:interrupted-prior"],
                    null,
                    [new(
                        "evidence:interrupted-prior",
                        ArenaEvidenceState.Observed,
                        "A prior interrupted child session was created.",
                        "prior-child")]);
                var restartRuns = new ExperimentRunStore(experimentRoot);
                Require(restartRuns.SaveAsync(priorCompleted).GetAwaiter().GetResult().Succeeded
                        && restartRuns.SaveAsync(priorInterrupted).GetAwaiter().GetResult().Succeeded,
                    "prior Completed and Running cells could not be persisted");

                var restartedControl = new ExperimentLabControl();
                var restartedProvider = new FixedCollaborateModelClient("restart completion");
                using var restartedCoordinator = new ExperimentLabCoordinator(
                    restartedControl,
                    sessions,
                    restartedProvider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddMinutes(1)));
                restartedControl.Initialize(restartedCoordinator);
                RunExperimentDispatcherTask(restartedCoordinator.RefreshRunHistoryAsync);
                var restoredInput = restartedControl.ReadMatrixInput();
                Require(restoredInput.Title == "Restart matrix"
                        && restoredInput.ScenarioPackId == preview.Contract.ScenarioPackId
                        && restoredInput.BenchmarkPackId == preview.Contract.BenchmarkPackId
                        && restoredInput.ProviderProfileIds == "provider:shared"
                        && restoredInput.DimensionValues == "0.2, 0.7, 0.9",
                    "new coordinator did not reconstruct the durable matrix definition");
                Require(definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                        == ArenaExperimentStatus.Interrupted
                        && restartedControl.MatrixStatusText.Text.Contains("requires explicit retry approval", StringComparison.Ordinal),
                    "new coordinator did not normalize and explain the abandoned matrix definition");

                RunExperimentDispatcherTask(restartedCoordinator.ExecuteMatrixAsync);
                Require(restartedProvider.CompleteCalls == 0
                        && restartedControl.MatrixStatusText.Text.Contains("Retry approval required", StringComparison.Ordinal)
                        && restartedControl.MatrixStatusText.Text.Contains("zero cells started", StringComparison.Ordinal)
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Interrupted,
                    "restored Interrupted matrix ran or claimed completion without approval");

                restartedControl.SetMatrixRetryApproval(true);
                RunExperimentDispatcherTask(restartedCoordinator.ExecuteMatrixAsync);
                var resumedRuns = new ExperimentRunStore(experimentRoot).LoadAllAsync(at.AddMinutes(2)).GetAwaiter().GetResult().Runs
                    .Where(item => item.ExperimentId == preview.Contract.Id)
                    .OrderBy(item => item.CellKey, StringComparer.Ordinal)
                    .ToArray();
                var resumedPrior = resumedRuns.Single(item => item.CellKey == firstCell.CellKey);
                var resumedInterrupted = resumedRuns.Single(item => item.CellKey == interruptedCell.CellKey);
                Require(restartedProvider.CompleteCalls > 0
                        && resumedRuns.Length == preview.Expansion.Cells.Length
                        && resumedPrior.Attempts == 2
                        && resumedPrior.TrialIds.Contains("trial:prior", StringComparer.Ordinal)
                        && resumedPrior.TrialIds.Length == 2
                        && resumedPrior.Evidence.Any(item => item.Id == "evidence:prior-child" && item.ReferenceId == "prior-child")
                        && resumedInterrupted.Attempts == 2
                        && resumedInterrupted.TrialIds.Contains("trial:interrupted-prior", StringComparer.Ordinal)
                        && resumedInterrupted.TrialIds.Length == 2
                        && resumedInterrupted.Evidence.Any(item => item.Id == "evidence:interrupted-prior")
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Completed,
                    $"approved new-coordinator resume failed to re-establish the current plan for mismatched Completed/Interrupted cells, lost prior evidence, or skipped missing cells; provider calls {restartedProvider.CompleteCalls}; runs {string.Join(" | ", resumedRuns.Select(item => $"{item.CellKey}:{item.State}:attempts={item.Attempts}:trials={string.Join(',', item.TrialIds)}"))}");
                var persistedDefinitions = string.Join('\n', Directory
                    .EnumerateFiles(Path.Combine(experimentRoot, "experiment-definitions"), "*.json")
                    .Select(File.ReadAllText));
                Require(!persistedDefinitions.Contains(root, StringComparison.OrdinalIgnoreCase)
                        && !persistedDefinitions.Contains("restart-test-token-never-persisted", StringComparison.Ordinal),
                    "durable matrix definition leaked a path or provider credential");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabRechecksRecoveryAfterObservedOwnerExits()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-owner-handoff-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            ExperimentDefinitionStore.ExecutionLease? originalOwner = null;
            try
            {
                var at = new DateTimeOffset(2036, 2, 3, 5, 5, 6, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                var source = SessionStore.CreateDefaultSnapshot();
                source.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = "owner-handoff-token-never-persisted",
                    Model = "local-owner-handoff-model",
                    Timeout = 30,
                    Temperature = 0.4,
                    MaxOutputTokens = 96,
                    ContextLength = 4096
                };
                sessions.SaveSnapshotAsync(source, "active").GetAwaiter().GetResult();

                ExperimentMatrixPreviewResult preview;
                using (var seed = new ExperimentLabCoordinator(
                    new ExperimentLabControl(),
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at)))
                {
                    var scenario = seed.CreateScenarioPackAsync(
                        new("Owner handoff scenario", "1.0.0", "2")).GetAwaiter().GetResult();
                    Require(seed.SaveScenarioPackAsync(scenario).GetAwaiter().GetResult().Succeeded,
                        "owner-handoff scenario could not be persisted");
                    var rubric = seed.CreateRubric(new("Owner handoff rubric", "1.0.0", "Completes the cell"));
                    Require(seed.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded,
                        "owner-handoff rubric could not be persisted");
                    preview = ExperimentLabCoordinator.BuildMatrixPreview(
                        new(
                            "Owner handoff matrix",
                            scenario.Id,
                            "provider:shared",
                            "temperature",
                            "0.4",
                            "1",
                            "2",
                            "1",
                            "",
                            rubric.Id),
                        at);
                }

                var experimentRoot = Path.Combine(root, "experimentation");
                var definitions = new ExperimentDefinitionStore(experimentRoot);
                Require(definitions.SaveAsync(preview.Contract).GetAwaiter().GetResult().Succeeded,
                    "owner-handoff draft could not be persisted");
                originalOwner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                Require(definitions.SaveForExecutionAsync(
                        originalOwner,
                        preview.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                    "original owner could not persist Running");

                var control = new ExperimentLabControl();
                var provider = new FixedCollaborateModelClient("owner handoff completion");
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    provider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddMinutes(1)));
                control.Initialize(coordinator);
                RunExperimentDispatcherTask(coordinator.RefreshRunHistoryAsync);
                Require(control.MatrixStatusText.Text.Contains("owner is active", StringComparison.OrdinalIgnoreCase)
                        && control.MatrixStatusText.Text.Contains("Recovery remains pending", StringComparison.Ordinal)
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Running,
                    "passive coordinator observation normalized or forgot an active owner's definition");

                originalOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                originalOwner = null;
                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                Require(provider.CompleteCalls == 0
                        && control.MatrixStatusText.Text.Contains("Retry approval required", StringComparison.Ordinal)
                        && control.MatrixStatusText.Text.Contains("zero cells started", StringComparison.Ordinal)
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Interrupted,
                    "owner release allowed Running-to-Running execution without owner-held restart normalization and approval");
            }
            finally
            {
                originalOwner?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabPreservesRestoredReferencesAcrossSourceDrift()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-source-drift-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2036, 2, 3, 5, 25, 6, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                var source = SessionStore.CreateDefaultSnapshot();
                source.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = "source-drift-token-never-persisted",
                    Model = "local-source-drift-model",
                    Timeout = 30,
                    Temperature = 0.4,
                    MaxOutputTokens = 96,
                    ContextLength = 4096
                };
                sessions.SaveSnapshotAsync(source, "active").GetAwaiter().GetResult();

                ExperimentMatrixPreviewResult preview;
                string scenarioId;
                string benchmarkId;
                string retainedRubricId;
                using (var seed = new ExperimentLabCoordinator(
                    new ExperimentLabControl(),
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at)))
                {
                    var scenario = seed.CreateScenarioPackAsync(
                        new("Source drift scenario", "1.0.0", "2")).GetAwaiter().GetResult();
                    Require(seed.SaveScenarioPackAsync(scenario).GetAwaiter().GetResult().Succeeded,
                        "source-drift scenario could not be persisted");
                    var retainedRubric = seed.CreateRubric(new("Retained rubric", "1.0.0", "Retained criterion"));
                    var addedRubric = seed.CreateRubric(new("Added benchmark rubric", "1.0.0", "Added criterion"));
                    Require(seed.SaveRubricContractAsync(retainedRubric).GetAwaiter().GetResult().Succeeded
                            && seed.SaveRubricContractAsync(addedRubric).GetAwaiter().GetResult().Succeeded,
                        "source-drift rubrics could not be persisted");
                    var benchmark = seed.CreateBenchmarkPack(
                        new("Source drift benchmark", "1.0.0", "2"),
                        scenario,
                        [retainedRubric.Id, addedRubric.Id]);
                    Require(seed.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult().Succeeded,
                        "source-drift benchmark could not be persisted");
                    scenarioId = scenario.Id;
                    benchmarkId = benchmark.Id;
                    retainedRubricId = retainedRubric.Id;
                    preview = ExperimentLabCoordinator.BuildMatrixPreview(
                        new(
                            "Source drift matrix",
                            scenario.Id,
                            "provider:missing-after-restart",
                            "temperature",
                            "0.4",
                            "1",
                            "2",
                            "1",
                            benchmark.Id,
                            retainedRubric.Id),
                        at);
                }

                var experimentRoot = Path.Combine(root, "experimentation");
                var definitions = new ExperimentDefinitionStore(experimentRoot);
                Require(definitions.SaveAsync(preview.Contract).GetAwaiter().GetResult().Succeeded,
                    "source-drift draft could not be persisted");
                var owner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Require(definitions.SaveForExecutionAsync(
                            owner,
                            preview.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                        "source-drift definition could not enter Running");
                }
                finally
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var control = new ExperimentLabControl();
                var provider = new FixedCollaborateModelClient("must not be called");
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    provider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddMinutes(1)));
                control.Initialize(coordinator);
                RunExperimentDispatcherTask(coordinator.RefreshRunHistoryAsync);
                RunExperimentDispatcherTask(coordinator.RefreshPacksAsync);
                var restored = control.ReadMatrixInput();
                Require(restored.ScenarioPackId == scenarioId
                        && restored.BenchmarkPackId == benchmarkId
                        && restored.ProviderProfileIds == "provider:missing-after-restart"
                        && restored.RubricIds == retainedRubricId,
                    "source reconciliation rewrote exact restored provider/pack/rubric references");

                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                var after = control.ReadMatrixInput();
                Require(provider.CompleteCalls == 0
                        && after.ScenarioPackId == scenarioId
                        && after.BenchmarkPackId == benchmarkId
                        && after.ProviderProfileIds == "provider:missing-after-restart"
                        && after.RubricIds == retainedRubricId
                        && control.MatrixStatusText.Text.Contains("Execution unavailable", StringComparison.Ordinal)
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Interrupted,
                    "missing/drifted sources changed or ran the restored definition instead of failing unavailable");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabClosesCompletedCrashWindowWithoutDuplicateTrials()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-completed-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2036, 2, 3, 6, 5, 6, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                var source = SessionStore.CreateDefaultSnapshot();
                source.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = "completed-crash-token-never-persisted",
                    Model = "local-completed-crash-model",
                    Timeout = 30,
                    Temperature = 0.4,
                    MaxOutputTokens = 96,
                    ContextLength = 4096
                };
                sessions.SaveSnapshotAsync(source, "active").GetAwaiter().GetResult();

                ExperimentMatrixPreviewResult preview;
                string planFingerprint;
                using (var firstCoordinator = new ExperimentLabCoordinator(
                    new ExperimentLabControl(),
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at)))
                {
                    var scenario = firstCoordinator.CreateScenarioPackAsync(
                        new("Completed crash scenario", "1.0.0", "3")).GetAwaiter().GetResult();
                    Require(firstCoordinator.SaveScenarioPackAsync(scenario).GetAwaiter().GetResult().Succeeded,
                        "completed-crash scenario pack could not be persisted");
                    var rubric = firstCoordinator.CreateRubric(
                        new("Completed crash rubric", "1.0.0", "Completes the cell"));
                    Require(firstCoordinator.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded,
                        "completed-crash rubric could not be persisted");
                    preview = ExperimentLabCoordinator.BuildMatrixPreview(
                        new(
                            "Completed crash matrix",
                            scenario.Id,
                            "provider:shared",
                            "temperature",
                            "0.2, 0.7",
                            "1",
                            "3",
                            "2",
                            "",
                            rubric.Id),
                        at);
                    var resolution = firstCoordinator.ResolveMatrixExecutionAsync(preview.Contract)
                        .GetAwaiter().GetResult();
                    Require(resolution.Binding is not null,
                        "completed-crash execution plan could not be resolved");
                    planFingerprint = resolution.Binding!.Plan.PlanFingerprint;
                }

                var experimentRoot = Path.Combine(root, "experimentation");
                var definitions = new ExperimentDefinitionStore(experimentRoot);
                Require(definitions.SaveAsync(preview.Contract).GetAwaiter().GetResult().Succeeded,
                    "completed-crash draft could not be persisted");
                var owner = definitions.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Require(definitions.SaveForExecutionAsync(
                            owner,
                            preview.Contract with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult().Succeeded,
                        "completed-crash definition could not enter Running");
                }
                finally
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var runs = new ExperimentRunStore(experimentRoot);
                foreach (var cell in preview.Expansion.Cells)
                {
                    var trialId = ArenaExperimentRunPolicy.CreateTrialId(cell.CellKey, 1);
                    var completed = new ArenaExperimentRunContract(
                        ArenaContractSchemas.ExperimentRun,
                        cell.RunId,
                        at,
                        cell.ExperimentId,
                        cell.ExperimentFingerprint,
                        cell.VariantFingerprint,
                        cell.Repetition,
                        cell.CellKey,
                        ArenaExperimentRunState.Completed,
                        1,
                        at,
                        [trialId],
                        null,
                        [new(
                            ArenaExperimentRunPolicy.CreateExecutionPlanEvidenceId(trialId),
                            ArenaEvidenceState.Observed,
                            "The cell completed under the currently resolved execution plan.",
                            $"plan:{planFingerprint}")]);
                    Require(runs.SaveAsync(completed).GetAwaiter().GetResult().Succeeded,
                        "completed-crash cell could not be persisted");
                }
                var before = string.Join('\n', runs.LoadAllAsync(at.AddMinutes(1)).GetAwaiter().GetResult().Runs
                    .Where(item => item.ExperimentId == preview.Contract.Id)
                    .OrderBy(item => item.CellKey, StringComparer.Ordinal)
                    .Select(item => ArenaContractCodec.Serialize(item)));

                var control = new ExperimentLabControl();
                var provider = new FixedCollaborateModelClient("must not be called");
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    provider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddMinutes(2)));
                control.Initialize(coordinator);
                RunExperimentDispatcherTask(coordinator.RefreshRunHistoryAsync);
                Require(definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                        == ArenaExperimentStatus.Interrupted,
                    "completed-crash definition was not made restart-safe before continuation");

                control.SetMatrixRetryApproval(true);
                RunExperimentDispatcherTask(coordinator.ExecuteMatrixAsync);
                var after = string.Join('\n', runs.LoadAllAsync(at.AddMinutes(3)).GetAwaiter().GetResult().Runs
                    .Where(item => item.ExperimentId == preview.Contract.Id)
                    .OrderBy(item => item.CellKey, StringComparer.Ordinal)
                    .Select(item => ArenaContractCodec.Serialize(item)));
                Require(provider.CompleteCalls == 0
                        && before == after
                        && control.MatrixStatusText.Text.Contains("eligible 0", StringComparison.Ordinal)
                        && control.MatrixStatusText.Text.Contains("started 0", StringComparison.Ordinal)
                        && definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Completed,
                    $"approved crash recovery repeated completed trials or failed to close the durable lifecycle; "
                    + $"providerCalls={provider.CompleteCalls}; unchanged={before == after}; "
                    + $"status={control.MatrixStatusText.Text}; "
                    + $"definition={definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabLeavesUnsupportedDurableMatrixUntouched()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-unsupported-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2036, 2, 4, 5, 6, 7, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                sessions.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "active").GetAwaiter().GetResult();
                var preview = ExperimentLabCoordinator.BuildMatrixPreview(
                    new(
                        "Stored matrix with unsupported execution inputs",
                        "scenario-pack:unsupported-restore",
                        "provider:shared",
                        "temperature",
                        "0.3",
                        "1",
                        "3",
                        "1",
                        "",
                        "rubric:unsupported-restore"),
                    at);
                var unsupported = preview.Contract with
                {
                    FaultProfileIds = ["fault-profile:unsupported-restore"],
                    BranchIds = ["branch:unsupported-restore"]
                };
                var definitions = new ExperimentDefinitionStore(Path.Combine(root, "experimentation"));
                Require(definitions.SaveAsync(unsupported).GetAwaiter().GetResult().Succeeded,
                    "unsupported durable definition fixture could not be persisted");
                var before = ArenaContractCodec.Serialize(
                    definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single());

                var control = new ExperimentLabControl();
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at.AddMinutes(1)));
                control.Initialize(coordinator);
                RunExperimentDispatcherTask(coordinator.RefreshRunHistoryAsync);

                var after = ArenaContractCodec.Serialize(
                    definitions.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single());
                Require(control.ReadMatrixInput().Title == "Local comparison"
                        && control.MatrixStatusText.Text.Contains("cannot represent", StringComparison.Ordinal)
                        && before == after
                        && after.Contains("fault-profile:unsupported-restore", StringComparison.Ordinal)
                        && after.Contains("branch:unsupported-restore", StringComparison.Ordinal),
                    "unsupported stored definition was partially selected, rewritten, or not explained");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabImportsLegacyPacksWithMigrationReceipt()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-pack-migration-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2036, 3, 4, 5, 6, 7, TimeSpan.Zero);
                var sessions = new SessionStore(root);
                sessions.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "active").GetAwaiter().GetResult();
                var sourceControl = new ExperimentLabControl();
                using var sourceCoordinator = new ExperimentLabCoordinator(
                    sourceControl,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at));
                var source = sourceCoordinator.CreateScenarioPackAsync(
                    new("Legacy import", "0.7.0", "3")).GetAwaiter().GetResult();
                var legacy = JsonNode.Parse(ArenaContractCodec.Serialize(source))!.AsObject();
                legacy["schema"] = ArenaExperimentPackCodec.ScenarioPackV0Schema;
                legacy.Remove("createdAtUtc");
                legacy.Remove("contentFingerprint");
                legacy.Remove("migration");
                var legacyBytes = Encoding.UTF8.GetBytes(legacy.ToJsonString());
                var importPath = Path.Combine(root, "selected-v0.json");
                File.WriteAllBytes(importPath, legacyBytes);

                var control = new ExperimentLabControl();
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    sessions,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at),
                    new FixedExperimentFileDialogs(importPath));
                control.Initialize(coordinator);
                RunExperimentDispatcherTask(coordinator.ImportPackAsync);
                var sourceSha = Convert.ToHexStringLower(SHA256.HashData(legacyBytes));
                Require(control.PackStatusText.Text.Contains("Explicit migration receipt", StringComparison.Ordinal)
                        && control.PackStatusText.Text.Contains(ArenaExperimentPackCodec.ScenarioPackV0Schema, StringComparison.Ordinal)
                        && control.PackStatusText.Text.Contains(sourceSha[..12], StringComparison.Ordinal)
                        && control.PackStatusText.Text.Contains(ArenaExperimentPackCodec.ScenarioPackV0MigratorVersion, StringComparison.Ordinal)
                        && !control.PackStatusText.Text.Contains(importPath, StringComparison.OrdinalIgnoreCase),
                    $"UI import did not show a privacy-safe explicit migration receipt: {control.PackStatusText.Text}");
                var stored = coordinator.ListScenarioPacksAsync().GetAwaiter().GetResult().Artifacts.Single();
                Require(stored.Migration?.SourceContentFingerprint == sourceSha
                        && stored.ContentFingerprint == ArenaExperimentFingerprints.ScenarioPackContent(stored.Invariants, stored.Scenarios),
                    "UI import did not persist validated migration provenance and recomputed identity");
                RunExperimentDispatcherTask(coordinator.ImportPackAsync);
                Require(control.PackStatusText.Text.StartsWith("Already stored", StringComparison.Ordinal)
                        && coordinator.ListScenarioPacksAsync().GetAwaiter().GetResult().Artifacts.Length == 1,
                    "repeated migrated UI import created a duplicate artifact");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabShellNavigationPreservesExistingSurfaceContracts()
    {
        var rail = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml"));
        var window = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
        var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
        Require(rail.Contains("ExperimentLabNavButtonElement", StringComparison.Ordinal)
                && rail.Contains("AutomationProperties.Name=\"Open Experiment Lab\"", StringComparison.Ordinal)
                && rail.Contains("ExperimentLeftRailContextPanelElement", StringComparison.Ordinal),
            "primary navigation or Experiment Lab left chrome is missing");
        Require(window.Contains("x:Name=\"ExperimentLabPanel\"", StringComparison.Ordinal)
                && window.Contains("x:Name=\"ExperimentRightRailPanel\"", StringComparison.Ordinal)
                && window.Contains("ExperimentLabNavigationRequested=\"ExperimentLabNavButton_Click\"", StringComparison.Ordinal),
            "Experiment Lab center/right shell chrome is not wired");
        Require(source.Contains("_activeShellSurface = ShellSurface.ExperimentLab", StringComparison.Ordinal)
                && source.Contains("case ShellSurface.ExperimentLab:", StringComparison.Ordinal)
                && source.Contains("ShowExperimentLabPanel();", StringComparison.Ordinal),
            "Experiment Lab navigation or Match Setup return path is incomplete");
        var state = ShellCommandState.For(ShellSurface.ExperimentLab);
        Require(!state.ShowMatchSetup && !state.ShowSearch && !state.ShowExport && !state.ShowView,
            "Experiment Lab inherited misleading transcript-only commands");
    }

    static void ExperimentLabProductionCompositionSharesObservedProviderAndRegistersEverySurface()
    {
        var traces = new ProviderRequestTraceStore();
        using var http = new HttpClient(new TestHttpMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"observed"}}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
                System.Text.Encoding.UTF8,
                "application/json")
        }));
        var provider = MainWindow.CreateObservedModelProviderClient(traces, http);
        var completion = provider.CompleteChatAsync(
            new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                Model = "model:observed",
                Timeout = 2
            },
            [new ModelChatMessage("user", "bounded production wiring probe")]).GetAwaiter().GetResult();
        var trace = traces.Snapshot().Single();
        Require(
            completion.Ok
            && trace.Outcome == "succeeded"
            && trace.Model == "model:observed"
            && trace.PromptTokens.Value == 3
            && trace.CompletionTokens.Value == 2,
            "the production provider factory did not feed the same bounded observer consumed by Context & Prompt Inspector");

        RunStaTest(() =>
        {
            var host = new ExperimentLabControl();
            var inspection = new AgentInspectionLabControl();
            var fault = new FaultInjectionLabControl();
            var routing = new ModelRoutingOptimizerControl();
            var qa = new InAppQaInspectorControl();
            MainWindow.RegisterProductionExperimentFeatures(host, inspection, fault, routing, qa);
            var expected = new[]
            {
                AgentInspectionLabControl.PromptInspectorFeatureKey,
                AgentInspectionLabControl.MemoryDebuggerFeatureKey,
                "fault-injection",
                "routing-optimizer",
                "in-app-qa-inspector"
            };
            Require(
                host.RegisteredFeatures.Select(item => item.Key).TakeLast(expected.Length).SequenceEqual(expected, StringComparer.Ordinal),
                "production Experiment Lab registration omitted or reordered a backed feature");
            foreach (var key in expected)
            {
                var feature = host.RegisteredFeatures.Single(item => item.Key == key);
                host.FeatureSelector.SelectedItem = feature;
                Require(feature.Content.Visibility == Visibility.Visible, $"production feature '{key}' is registered but not navigable");
            }
        });

        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            FindWorkspaceFile("src/AIArena.Wpf/AIArena.Wpf.csproj"))))!;
        Require(
            MainWindow.ResolveQaRepositoryRoot(
                Path.Combine(repositoryRoot, "src", "AIArena.Wpf", "bin", "Release", "net10.0-windows"))
                .Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase),
            "QA Inspector production root resolution did not require and find the real repository markers");

        var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
        Require(
            source.Contains("_providerRequestTraceStore = new ProviderRequestTraceStore();", StringComparison.Ordinal)
            && source.Contains("CreateObservedModelProviderClient(_providerRequestTraceStore)", StringComparison.Ordinal)
            && source.Contains("providerRequestTraces: _providerRequestTraceStore", StringComparison.Ordinal)
            && source.Contains("_agentInspectionLabCoordinator = new AgentInspectionLabCoordinator(", StringComparison.Ordinal)
            && source.Contains("_providerRequestTraceStore,", StringComparison.Ordinal)
            && source.Contains("_arenaOperationLock,", StringComparison.Ordinal)
            && source.Contains("() => _arenaBusy,", StringComparison.Ordinal)
            && source.Contains("() => _arenaRunCoordinator?.IsAutoChatRunning == true,", StringComparison.Ordinal)
            && source.Contains("action => ArenaOperations.TrackAsync(action),", StringComparison.Ordinal)
            && source.Contains("(sessionId, cancellationToken) => LoadSessionsAsync(sessionId, cancellationToken)", StringComparison.Ordinal)
            && source.Contains("RegisterProductionExperimentRefreshes();", StringComparison.Ordinal)
            && source.Contains("_agentInspectionLabCoordinator.Dispose();", StringComparison.Ordinal)
            && source.Contains("_inAppQaInspectorCoordinator.Dispose();", StringComparison.Ordinal),
            "MainWindow no longer owns the shared observer, guarded route-application refresh lifecycle, provider-backed Judge Studio binding, and disposal boundaries used by the production lab composition");
    }

    static void ExperimentLabProviderJudgePersistsTracedInferredEvidence()
    {
        var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ExperimentLabControl.xaml"));
        Require(xaml.Contains("Run provider judge", StringComparison.Ordinal)
                && xaml.Contains("request-bound receipt", StringComparison.Ordinal)
                && xaml.Contains("message speaker's effective configured provider route", StringComparison.Ordinal),
            "Judge Studio does not expose or explain its provider-backed evidence action");

        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-provider-judge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2035, 3, 4, 5, 6, 7, TimeSpan.Zero);
                var store = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Configs.Clear();
                snapshot.Configs["shared"] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge:shared",
                    Timeout = 30,
                    MaxOutputTokens = 256
                };
                snapshot.Configs["alpha"] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge:alphabetical-first",
                    Timeout = 30,
                    MaxOutputTokens = 256
                };
                snapshot.Configs["beta"] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge:subject-route",
                    Timeout = 30,
                    MaxOutputTokens = 256
                };
                snapshot.Engine.Messages =
                [
                    new DialogueMessage
                    {
                        MessageId = "message:judge-subject",
                        SpeakerId = "beta",
                        Speaker = "Beta",
                        Text = "A bounded response to evaluate.",
                        Turn = 1,
                        CreatedAt = 1
                    }
                ];
                store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
                var traces = new ProviderRequestTraceStore();
                var control = new ExperimentLabControl();
                var providerClient = new WpfTracedJudgeProvider(traces, at);
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    store,
                    providerClient,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at),
                    providerRequestTraces: traces);
                control.Initialize(coordinator);
                var rubric = coordinator.CreateRubric(new("Provider judge", "1.0.0", "Bounded quality"));
                Require(coordinator.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded,
                    "provider judge rubric could not be saved");
                var finalization = coordinator.RunModelJudgeAsync(
                    rubric,
                    "message:judge-subject").GetAwaiter().GetResult();
                Require(finalization.Result.ModelJudgeResults.Single().WeightedScoreA == 0.8m
                        && finalization.Result.HumanResults.IsEmpty
                        && finalization.Result.DeterministicResults.IsEmpty
                        && providerClient.LastModel == "judge:subject-route"
                        && traces.Snapshot().Single().Model == "judge:subject-route",
                    "provider judge crossed source partitions, calculated the wrong score, or ignored the exact subject speaker route");
                var restarted = new ArenaRubricStore(Path.Combine(root, "experimentation"));
                Require(restarted.LoadModelJudgeReceiptsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == finalization.Receipt.Id
                        && restarted.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == finalization.Result.Id,
                    "provider receipt and its exact result did not survive WPF coordinator restart");

                using var untraced = new ExperimentLabCoordinator(
                    new ExperimentLabControl(),
                    store,
                    new WpfTracedJudgeProvider(traces, at),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at));
                RequireExperimentThrows<ExperimentLabInputException>(
                    () => untraced.RunModelJudgeAsync(rubric, "message:judge-subject").GetAwaiter().GetResult(),
                    "Judge Studio persisted a model score without its shared provider trace boundary");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabBlindJudgeConcealsIdentitiesUntilDurableSubmit()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-blind-judge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2035, 4, 5, 6, 7, 8, TimeSpan.Zero);
                var store = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Engine.Messages =
                [
                    new DialogueMessage
                    {
                        MessageId = "message:blind-alpha",
                        SpeakerId = "agent:alpha",
                        Speaker = "Alpha",
                        Text = "ALPHA_CONTENT_SENTINEL: bounded first candidate.",
                        Turn = 1,
                        CreatedAt = 1
                    },
                    new DialogueMessage
                    {
                        MessageId = "message:blind-beta",
                        SpeakerId = "agent:beta",
                        Speaker = "Beta",
                        Text = "BETA_CONTENT_SENTINEL: bounded second candidate.",
                        Turn = 2,
                        CreatedAt = 2
                    }
                ];
                store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
                var control = new ExperimentLabControl();
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    store,
                    new FixedCollaborateModelClient("unused"),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at));
                control.Initialize(coordinator);
                var rubric = coordinator.CreateRubric(new("Blind judge", "1.0.0", "Bounded quality"));
                Require(coordinator.SaveRubricContractAsync(rubric).GetAwaiter().GetResult().Succeeded,
                    "blind Judge Studio rubric could not be saved");
                ArenaBlindPairwiseCommitmentContract commitment = null!;
                RunExperimentDispatcherTask(async () => commitment = await coordinator.BeginBlindPairwiseAsync(
                    rubric,
                    "message:blind-alpha",
                    "message:blind-beta"));
                var evidenceStore = new ArenaRubricStore(Path.Combine(root, "experimentation"));
                Require(evidenceStore.LoadBlindCommitmentsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == commitment.Id
                        && evidenceStore.LoadBlindReceiptsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty
                        && evidenceStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty,
                    "blind UI enabled judgment before persisting the commitment or invented a premature result");
                Require(control.BlindJudgeSummary.Contains("ALPHA_CONTENT_SENTINEL", StringComparison.Ordinal)
                        && control.BlindJudgeSummary.Contains("BETA_CONTENT_SENTINEL", StringComparison.Ordinal),
                    "concealed judge view omitted one candidate's actual bounded content");
                Require(!control.BlindJudgeSummary.Contains("message:blind-alpha", StringComparison.Ordinal)
                        && !control.BlindJudgeSummary.Contains("message:blind-beta", StringComparison.Ordinal)
                        && !control.BlindIdentityInputsVisible
                        && !control.RubricStatus.Contains("message:blind-alpha", StringComparison.Ordinal)
                        && !control.RubricStatus.Contains("message:blind-beta", StringComparison.Ordinal),
                    "blind UI revealed stable identities before judgment persistence");

                ArenaBlindPairwiseFinalization finalization = null!;
                RunExperimentDispatcherTask(async () => finalization = await coordinator.SubmitBlindPairwiseAsync("4", "2", "A", false));
                Require(control.BlindIdentityInputsVisible
                        && string.IsNullOrEmpty(control.BlindJudgeSummary)
                        && control.RubricStatus.Contains("message:blind-alpha", StringComparison.Ordinal)
                        && control.RubricStatus.Contains("message:blind-beta", StringComparison.Ordinal),
                    "blind identities did not reveal only after receipt and result persistence");
                Require(evidenceStore.LoadBlindReceiptsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == finalization.Receipt.Id
                        && evidenceStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == finalization.Result.Id,
                    "blind UI did not persist the receipt/result pair before reveal");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void ExperimentLabProviderJudgeCancellationOwnsItsLifetime()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-provider-judge-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2035, 5, 6, 7, 8, 9, TimeSpan.Zero);
                var store = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Configs["judge"] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge:blocking",
                    Timeout = 30,
                    MaxOutputTokens = 256
                };
                snapshot.Engine.Messages =
                [
                    new DialogueMessage
                    {
                        MessageId = "message:cancel-judge",
                        SpeakerId = "agent:alpha",
                        Speaker = "Alpha",
                        Text = "Bounded provider cancellation candidate.",
                        Turn = 1,
                        CreatedAt = 1
                    }
                ];
                store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
                var evidenceStore = new ArenaRubricStore(Path.Combine(root, "experimentation"));
                using var rubricCoordinator = new ExperimentLabCoordinator(
                    new ExperimentLabControl(), store, new FixedCollaborateModelClient("unused"),
                    () => "active", (_, _) => Task.CompletedTask, root, new FixedExperimentTimeProvider(at));
                var rubric = rubricCoordinator.CreateRubric(new("Cancelable judge", "1.0.0", "Bounded quality"));
                Require(evidenceStore.SaveRubricAsync(rubric).GetAwaiter().GetResult().Succeeded,
                    "cancel judge rubric was not saved");

                var provider = new BlockingJudgeProvider(throwOnCancellationCallback: true);
                var traces = new ProviderRequestTraceStore();
                var control = new ExperimentLabControl();
                using var coordinator = new ExperimentLabCoordinator(
                    control,
                    store,
                    provider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at),
                    providerRequestTraces: traces);
                control.Initialize(coordinator);
                control.SetRubrics([new ExperimentRubricItem(rubric)]);
                control.SetProviderJudgeInput("model", "message:cancel-judge");
                RunExperimentDispatcherTask(async () =>
                {
                    var run = coordinator.RunProviderJudgeAsync();
                    await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Require(control.ProviderJudgeRunning && control.ProviderJudgeCancelAvailable && !control.ProviderJudgeRunAvailable,
                        "provider judge did not expose an enabled cancel control while locking its run action");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    coordinator.CancelProviderJudge();
                    await run.WaitAsync(TimeSpan.FromSeconds(2));
                    watch.Stop();
                    Require(watch.Elapsed < TimeSpan.FromSeconds(2) && provider.CancellationObserved.Task.IsCompleted,
                        "provider judge cancellation did not return promptly through the provider token");
                });
                Require(!control.ProviderJudgeRunning && !control.ProviderJudgeCancelAvailable && control.ProviderJudgeRunAvailable
                        && control.RubricStatus.Contains("cancelled before evidence commit", StringComparison.OrdinalIgnoreCase),
                    "provider judge UI did not return to its enabled non-stale state after cancellation");
                Require(evidenceStore.LoadModelJudgeReceiptsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty
                        && evidenceStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty,
                    "cancelled provider judge persisted a receipt or result");

                var disposeProvider = new BlockingJudgeProvider(throwOnCancellationCallback: true);
                var disposeControl = new ExperimentLabControl();
                var disposeCoordinator = new ExperimentLabCoordinator(
                    disposeControl,
                    store,
                    disposeProvider,
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at),
                    providerRequestTraces: new ProviderRequestTraceStore());
                disposeControl.Initialize(disposeCoordinator);
                disposeControl.SetRubrics([new ExperimentRubricItem(rubric)]);
                disposeControl.SetProviderJudgeInput("model", "message:cancel-judge");
                RunExperimentDispatcherTask(async () =>
                {
                    var run = disposeCoordinator.RunProviderJudgeAsync();
                    await disposeProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    disposeCoordinator.Dispose();
                    await run.WaitAsync(TimeSpan.FromSeconds(2));
                });
                Require(disposeProvider.CancellationObserved.Task.IsCompleted
                        && evidenceStore.LoadModelJudgeReceiptsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty
                        && evidenceStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty,
                    "coordinator disposal did not cancel the provider judge without persistence");

                var preCommitReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releasePreCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var preCommitTraces = new ProviderRequestTraceStore();
                var preCommitControl = new ExperimentLabControl();
                using var preCommitCoordinator = new ExperimentLabCoordinator(
                    preCommitControl,
                    store,
                    new WpfTracedJudgeProvider(preCommitTraces, at),
                    () => "active",
                    (_, _) => Task.CompletedTask,
                    root,
                    new FixedExperimentTimeProvider(at),
                    providerRequestTraces: preCommitTraces,
                    providerJudgePreCommitOverride: async _ =>
                    {
                        preCommitReached.TrySetResult(true);
                        await releasePreCommit.Task;
                    });
                preCommitControl.Initialize(preCommitCoordinator);
                preCommitControl.SetRubrics([new ExperimentRubricItem(rubric)]);
                preCommitControl.SetProviderJudgeInput("model", "message:cancel-judge");
                RunExperimentDispatcherTask(async () =>
                {
                    var run = preCommitCoordinator.RunProviderJudgeAsync();
                    await preCommitReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Require(preCommitControl.ProviderJudgeCancelAvailable,
                        "provider judge stopped advertising cancellation before the evidence commit boundary");
                    preCommitCoordinator.CancelProviderJudge();
                    releasePreCommit.TrySetResult(true);
                    await run.WaitAsync(TimeSpan.FromSeconds(2));
                });
                Require(!preCommitControl.ProviderJudgeRunning
                        && !preCommitControl.ProviderJudgeCancelAvailable
                        && preCommitControl.ProviderJudgeRunAvailable
                        && preCommitControl.RubricStatus.Contains("cancelled before evidence commit", StringComparison.OrdinalIgnoreCase),
                    "pre-commit cancellation left stale provider-judge UI state");
                Require(evidenceStore.LoadModelJudgeReceiptsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty
                        && evidenceStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.IsEmpty,
                    "cancellation after provider finalization but before commit persisted evidence");

                preCommitControl.SetProviderJudgeRunning(true);
                preCommitControl.SetProviderJudgeCommitting();
                Require(!preCommitControl.ProviderJudgeRunning
                        && !preCommitControl.ProviderJudgeCancelAvailable
                        && !preCommitControl.ProviderJudgeRunAvailable,
                    "commit presentation continued to advertise cancellation or another provider run");
                preCommitControl.SetProviderJudgeRunning(false);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private sealed class FixedExperimentFileDialogs(string importPath) : IExperimentLabFileDialogService
    {
        public string? OpenJson() => importPath;

        public string? SaveJson(string suggestedFileName) => null;
    }

    private sealed class FixedExperimentTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class WpfTracedJudgeProvider(ProviderRequestTraceStore traces, DateTimeOffset observedAt) : IModelProviderClient
    {
        internal string LastModel { get; private set; } = "";

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", observedAt));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            LastModel = config.Model;
            var context = config.RequestInspectionContext ?? throw new InvalidOperationException("judge trace context was absent");
            traces.ObserveRequest(new ProviderRequestTrace(
                $"request:{context.CorrelationId[..16]}",
                observedAt,
                context.CorrelationId,
                context.Phase,
                config.ApiMode,
                "wpf_test_chat",
                config.Model,
                false,
                false,
                1,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("wpf-judge-payload"))),
                17,
                "{\"redacted\":true}",
                false,
                [],
                context.Explanations,
                ProviderTokenEvidence.Unavailable("not supplied"),
                ProviderTokenEvidence.Unavailable("not supplied"),
                ProviderTokenEvidence.Unavailable("not supplied"),
                "succeeded"));
            return Task.FromResult(new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                "{\"criteria\":[{\"criterionId\":\"criterion:primary\",\"score\":4}]}",
                "",
                12,
                10,
                5,
                15,
                "",
                observedAt,
                ResponseId: "response:wpf-judge"));
        }
    }

    private sealed class BlockingJudgeProvider(bool throwOnCancellationCallback = false) : IModelProviderClient
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            using var hostileRegistration = throwOnCancellationCallback
                ? cancellationToken.Register(static () => throw new InvalidOperationException("Injected hostile cancellation callback."))
                : default;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Blocking provider unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }
        }
    }

    private static void RequireExperimentThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void RunExperimentDispatcherTask(Func<Task> action)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        var frame = new System.Windows.Threading.DispatcherFrame();
        Exception? failure = null;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
            action().ContinueWith(task =>
            {
                if (task.Exception is not null)
                {
                    failure = task.Exception.GetBaseException();
                }
                frame.Continue = false;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
