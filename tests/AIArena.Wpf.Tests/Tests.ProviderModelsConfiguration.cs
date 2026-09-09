using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderModelsConfigurationHostsResponsiveAccessibleContract()
    {
        RunStaTest(() =>
        {
            var control = HostProviderModelsConfigurationSurface(out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                control.AdvancedSettings.IsExpanded = true;
                FlushProviderModelsDispatcher(host);

                var targets = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.AssignmentTargets)
                    .ToArray();
                var targetNames = targets
                    .Select(AutomationProperties.GetName)
                    .ToArray();
                Require(control.AssignmentGridColumns == 3
                        && targetNames.Length == 6
                        && targetNames[0].EndsWith("Default for unassigned agents", StringComparison.Ordinal)
                        && targetNames[1].EndsWith("Narrator", StringComparison.Ordinal)
                        && targetNames[2].EndsWith("Alpha", StringComparison.Ordinal)
                        && targetNames[3].EndsWith("Beta", StringComparison.Ordinal)
                        && targetNames[4].EndsWith("Gamma", StringComparison.Ordinal)
                        && targetNames[5].EndsWith("Delta", StringComparison.Ordinal),
                    "the expanded model editor did not render Default, Narrator, then the active roster in an ordered three-column grid");

                var firstRowTop = targets[0].TranslatePoint(new Point(), control.AssignmentTargets).Y;
                var secondRowTop = targets[3].TranslatePoint(new Point(), control.AssignmentTargets).Y;
                Require(targets.Take(3).All(target =>
                            Math.Abs(target.TranslatePoint(new Point(), control.AssignmentTargets).Y - firstRowTop) < 1)
                        && targets.Skip(3).All(target =>
                            Math.Abs(target.TranslatePoint(new Point(), control.AssignmentTargets).Y - secondRowTop) < 1)
                        && secondRowTop > firstRowTop,
                    "the first six assignment switches did not form two ordered rows of three");
                var defaultLabels = FindProviderModelsDescendants<TextBlock>(targets[0])
                    .Select(text => text.Text)
                    .ToArray();
                Require(defaultLabels.Contains("Default")
                        && !defaultLabels.Contains("Default for unassigned agents")
                        && targets.All(target => target.MinHeight >= 32
                            && target.FocusVisualStyle is not null
                            && new CheckBoxAutomationPeer(target).GetPattern(PatternInterface.Toggle) is IToggleProvider),
                    "assignment switches did not preserve the compact Default label, 32-DIP desktop target, focus ring, and UIA TogglePattern contract");

                Require(control.CustomToneInput.MaxLength == 240
                        && control.ContextWindowInput.MinHeight >= 32
                        && control.HistoryPolicySelector.MinHeight >= 32
                        && control.ResponseToneSelector.MinHeight >= 32
                        && control.ConfigurationReloadAction.MinHeight >= 32,
                    "model configuration inputs did not preserve their bounded custom-tone and accessible target-size contracts");
                var chaptered = control.HistoryPolicySelector.Items[2];
                Require(chaptered.ToString()?.Contains("Coming later", StringComparison.Ordinal) == true
                        && chaptered.GetType().GetProperty("IsEnabled")?.GetValue(chaptered) is false,
                    "Chaptered history was not visibly present as a disabled future option");
                Require(control.ConfiguredContextEvidence.Text == "Configured: 8,192 tokens"
                        && control.EffectiveContextEvidence.Text.Contains("4,096 tokens", StringComparison.Ordinal)
                        && control.EffectiveContextEvidence.ToolTip?.ToString()?.Contains("LM Studio runtime", StringComparison.Ordinal) == true
                        && AutomationProperties.GetHelpText(control.EffectiveContextEvidence).Contains("LM Studio runtime", StringComparison.Ordinal),
                    "the expanded model editor did not distinguish configured context from provider-observed effective evidence");

                control.SearchBox.Text = "loaded only";
                Require(control.SelectModel("available-alias", focusConfiguration: true),
                    "the public model-selection seam did not resolve a unique provider-path leaf alias");
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == "publisher/available-alias"
                        && control.CatalogExpander.IsExpanded
                        && string.IsNullOrEmpty(control.SearchQuery)
                        && control.CatalogList.SelectedItem is not null
                        && control.ContextWindowInput.IsKeyboardFocusWithin,
                    $"alias selection did not expand and reveal the Available model, preserve selection, and focus Context window (selected={control.SelectedModelId}, expanded={control.CatalogExpander.IsExpanded}, search='{control.SearchQuery}', catalogSelected={control.CatalogList.SelectedItem is not null}, contextFocus={control.ContextWindowInput.IsKeyboardFocusWithin})");
                Require(!control.SelectModel("missing-model"),
                    "the model-selection seam reported success for an unknown model identity");

                foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
                {
                    var theme = ThemePalette.Resolve(themeId);
                    ApplyExperimentSurfaceTheme(control, theme);
                    FlushProviderModelsDispatcher(host);
                    Require(ExperimentBrushMatches(control.DetailSurface.Background, theme.Input)
                            && control.ContextWindowInput.FocusVisualStyle is not null
                            && control.HistoryPolicySelector.FocusVisualStyle is not null
                            && control.ResponseToneSelector.FocusVisualStyle is not null,
                        $"model configuration controls lost shared theme or focus evidence under {themeId}");
                }

                host.Width = 960;
                FlushProviderModelsDispatcher(host);
                var availableAssignmentWidth = ((FrameworkElement)control.AssignmentTargets.Parent).ActualWidth;
                Require(control.UsesCompactLayout
                        && control.AssignmentGridColumns == (availableAssignmentWidth >= 600 ? 3 : availableAssignmentWidth >= 380 ? 2 : 1),
                    "the 960-DIP inline surface did not size assignment columns to their available width");

                host.Width = 1500;
                control.LayoutTransform = new ScaleTransform(2, 2);
                FlushProviderModelsDispatcher(host);
                Require(control.UsesCompactLayout
                        && control.AssignmentGridColumns == 1
                        && control.DetailBody is not ScrollViewer
                        && control.WorkspaceViewportElement is not ScrollViewer,
                    "the 200% layout did not collapse assignments to one column within the scrolling model list");
                AssertProviderModelsInlineEditor(control);
                AssertProviderModelsInlineElementReachable(control, host, control.ConfigurationReloadAction);
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void ProviderModelsConfigurationSavesRollsBackAndReloadsCausally()
    {
        RunStaTest(() =>
        {
            var control = HostProviderModelsConfigurationSurface(out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                control.AdvancedSettings.IsExpanded = true;
                FlushProviderModelsDispatcher(host);
                ProviderModelConfigurationChangedEventArgs? change = null;
                ProviderModelConfigurationReloadRequestedEventArgs? reload = null;
                control.ConfigurationChanged += (_, args) => change = args;
                control.ConfigurationReloadRequested += (_, args) => reload = args;

                control.ContextWindowInput.Text = "511";
                Require(control.ConfigurationValidation.Visibility == Visibility.Visible
                        && control.ConfigurationValidation.Text.Contains("512", StringComparison.Ordinal)
                        && !control.HasPendingConfiguration,
                    "context validation did not reject a value below the supported minimum without mutating state");

                control.ContextWindowInput.Text = "16384";
                RaiseProviderModelsPreviewKey(control.ContextWindowInput, host, System.Windows.Input.Key.Enter);
                FlushProviderModelsDispatcher(host);
                Require(change is not null
                        && change.ModelId == "loaded-config"
                        && change.ContextWindow == 16_384
                        && change.HistoryPolicy == ProviderModelHistoryPolicy.Strict
                        && change.ResponseTone == ProviderModelResponseTone.Default
                        && change.ConfigurationIdentity == "config-loaded-v1"
                        && control.HasPendingConfiguration,
                    "valid context editing did not raise one fully attributed optimistic configuration event");
                var failedChange = change
                    ?? throw new InvalidOperationException("Configuration event was not captured.");
                Require(control.CatalogList.IsEnabled
                        && control.LoadedList.IsEnabled
                        && control.SearchBox.IsEnabled
                        && control.DetailBody.IsEnabled
                        && !control.ContextWindowInput.IsEnabled
                        && !control.LifecycleAction.IsEnabled,
                    "configuration saving disabled browsing or left a competing model mutation enabled");
                Require(!control.SetConfigurationState(
                            Guid.NewGuid(),
                            ProviderModelConfigurationSaveState.Saved,
                            "stale")
                        && control.SetConfigurationState(
                            failedChange.ChangeId,
                            ProviderModelConfigurationSaveState.Failed,
                            "persistence rejected the edit")
                        && !control.HasPendingConfiguration
                        && control.ContextWindowInput.Text == "8192"
                        && control.ConfigurationStatus.Text.StartsWith("Failed:", StringComparison.Ordinal),
                    "configuration completion did not reject stale IDs or roll failed optimistic state back causally");

                change = null;
                control.ContextWindowInput.Text = "16384";
                RaiseProviderModelsPreviewKey(control.ContextWindowInput, host, System.Windows.Input.Key.Enter);
                FlushProviderModelsDispatcher(host);
                Require(change is not null
                        && control.SetConfigurationState(
                            change.ChangeId,
                            ProviderModelConfigurationSaveState.Saved,
                            "Model configuration saved.")
                        && control.ConfigurationReloadAction.IsEnabled
                        && control.ConfigurationReloadAction.Content?.ToString() == "Reload to apply",
                    "a saved loaded-context edit did not expose an explicit Reload-to-apply state");

                var reloadPeer = new ButtonAutomationPeer(control.ConfigurationReloadAction);
                var invoke = reloadPeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                Require(invoke is not null,
                    "the Reload-to-apply button did not expose UIA InvokePattern");
                invoke!.Invoke();
                FlushProviderModelsDispatcher(host);
                Require(reload is not null
                        && reload.ModelId == "loaded-config"
                        && reload.ConfigurationIdentity == "config-loaded-v1"
                        && control.HasPendingConfigurationReload
                        && control.CatalogList.IsEnabled
                        && control.LoadedList.IsEnabled
                        && control.SearchBox.IsEnabled
                        && !control.LifecycleAction.IsEnabled
                        && !control.ContextWindowInput.IsEnabled,
                    "configuration reload did not raise a causal operation while preserving browsing and blocking conflicting mutation controls");
                var reloadReceipt = reload
                    ?? throw new InvalidOperationException("Configuration reload event was not captured.");
                Require(!control.SetConfigurationReloadState(
                            Guid.NewGuid(),
                            ProviderModelConfigurationReloadState.Succeeded,
                            "stale")
                        && control.SetConfigurationReloadState(
                            reloadReceipt.OperationId,
                            ProviderModelConfigurationReloadState.Running,
                            "Reloading with 16,384 tokens")
                        && control.SetConfigurationReloadState(
                            reloadReceipt.OperationId,
                            ProviderModelConfigurationReloadState.Succeeded,
                            "LM Studio confirmed 16,384 tokens")
                        && !control.HasPendingConfigurationReload
                        && !control.ConfigurationReloadAction.IsEnabled
                        && control.ConfigurationReloadAction.Content?.ToString() == "Configuration is active",
                    "configuration reload completion did not enforce operation identity or clear Reload-to-apply exactly once");

                control.ProviderDefaultContextToggle.IsChecked = true;
                FlushProviderModelsDispatcher(host);
                Require(control.HasPendingConfiguration,
                    "the explicit Provider default context toggle did not raise an immediate configuration change");
                Require(change is not null
                        && change.ContextWindow == 0
                        && control.SetConfigurationState(
                            change.ChangeId,
                            ProviderModelConfigurationSaveState.Saved,
                            "Provider default saved"),
                    "Provider default context did not serialize as the unambiguous zero sentinel");

                change = null;
                control.HistoryPolicySelector.SelectedIndex = 1;
                FlushProviderModelsDispatcher(host);
                Require(change is { HistoryPolicy: ProviderModelHistoryPolicy.Rolling80Percent }
                        && control.SetConfigurationState(
                            change.ChangeId,
                            ProviderModelConfigurationSaveState.Saved,
                            "Rolling history saved"),
                    "Rolling 80% history did not publish and resolve as an immediate causal configuration change");

                change = null;
                control.ResponseToneSelector.SelectedIndex = 6;
                FlushProviderModelsDispatcher(host);
                Require(change is null
                        && control.CustomToneInput.IsEnabled
                        && control.CustomToneInput.Visibility == Visibility.Visible
                        && control.ConfigurationValidation.Visibility == Visibility.Visible,
                    "choosing Custom tone did not reveal an editable input and hold the invalid blank draft locally");
                control.CustomToneInput.Text = "Measured, concise, and evidence-led.";
                Require(control.CustomToneInput.Focus() && control.SearchBox.Focus(),
                    "the custom tone input could not participate in keyboard focus navigation");
                FlushProviderModelsDispatcher(host);
                Require(change is
                        {
                            HistoryPolicy: ProviderModelHistoryPolicy.Rolling80Percent,
                            ResponseTone: ProviderModelResponseTone.Custom,
                            CustomTone: "Measured, concise, and evidence-led."
                        }
                        && control.SetConfigurationState(
                            change.ChangeId,
                            ProviderModelConfigurationSaveState.Saved,
                            "Custom tone saved"),
                    "a valid custom tone did not save immediately with the selected history policy");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static ProviderModelAssignmentsControl HostProviderModelsConfigurationSurface(out Window host)
    {
        var control = new ProviderModelAssignmentsControl();
        AttachArenaPresentationResources(control);
        ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
        control.ApplyPresentation(ProviderModelsConfigurationPresentation());
        host = new Window
        {
            Content = control,
            Width = 1500,
            Height = 920,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Show();
        return control;
    }

    static void ProviderModelsConfigurationReloadIdentitySwitchesStayCausal()
    {
        RunStaTest(() =>
        {
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            var original = ProviderModelsConfigurationPresentation(
                requiresReload: true,
                connectionIdentity: "reload-origin");
            control.ApplyPresentation(original);
            var host = new Window
            {
                Content = control,
                Width = 1300,
                Height = 800,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };
            host.Show();
            try
            {
                ProviderModelConfigurationReloadRequestedEventArgs? request = null;
                control.ConfigurationReloadRequested += (_, args) => request = args;
                control.ConfigurationReloadAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(request is not null && control.HasPendingConfigurationReload,
                    "reload preflight fixture did not establish a pending causal request");

                control.ApplyPresentation(original with
                {
                    ConnectionIdentity = "reload-replacement-before-post",
                    PresentationIdentity = "reload-replacement-before-post"
                });
                var beforePostState = AutomationProperties.GetItemStatus(control.ConfigurationReloadAction);
                var beforePostHelp = AutomationProperties.GetHelpText(control.ConfigurationReloadAction);
                Require(!control.HasPendingConfigurationReload
                        && beforePostState == "Failed"
                        && beforePostHelp
                            .Contains("before a configuration reload request was sent", StringComparison.OrdinalIgnoreCase),
                    $"a connection switch before the first provider mutation was reported as an unknown outcome (state={beforePostState}, help='{beforePostHelp}')");

                control.ApplyPresentation(original);
                request = null;
                control.ConfigurationReloadAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(request is not null
                        && control.MarkConfigurationReloadMutationStarted(request.OperationId),
                    "reload fixture could not mark the first provider mutation causally");
                control.ApplyPresentation(original with
                {
                    ConnectionIdentity = "reload-replacement-after-post",
                    PresentationIdentity = "reload-replacement-after-post"
                });
                Require(!control.HasPendingConfigurationReload
                        && AutomationProperties.GetItemStatus(control.ConfigurationReloadAction) == "Unconfirmed"
                        && AutomationProperties.GetHelpText(control.ConfigurationReloadAction)
                            .Contains("after the reload request started", StringComparison.OrdinalIgnoreCase),
                    "a connection switch after the first provider mutation was reinterpreted as a definite failure");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static ProviderModelAssignmentsPresentation ProviderModelsConfigurationPresentation(
        bool requiresReload = false,
        string connectionIdentity = "models-configuration-connection-v1")
    {
        var configuration = new ProviderModelConfigurationPresentation(
            ContextWindow: 8_192,
            EffectiveContextWindow: 4_096,
            ContextEvidence: "LM Studio runtime reported this loaded instance.",
            HistoryPolicy: ProviderModelHistoryPolicy.Strict,
            ResponseTone: ProviderModelResponseTone.Default,
            CanEdit: true,
            ChapteredAvailable: false,
            RequiresReload: requiresReload,
            ConfigurationIdentity: "config-loaded-v1",
            Status: "Ready to configure.");
        var models = new[]
        {
            new ProviderModelAssignmentPresentation(
                "loaded-config",
                "Loaded Config Model",
                ProviderModelAvailability.Loaded,
                "Loaded",
                "publisher · Q4 · 8k configured context",
                ["default"],
                "Provider-observed loaded model.",
                CanUnload: true,
                LifecycleHelp: "LM Studio reports this model loaded.",
                Configuration: configuration),
            new ProviderModelAssignmentPresentation(
                "publisher/available-alias",
                "Available Alias Model",
                ProviderModelAvailability.Available,
                "Available",
                "publisher · Q4 · provider catalog",
                [],
                "Provider-advertised model.",
                CanLoad: true,
                LifecycleHelp: "LM Studio reports this model available.",
                Configuration: configuration with
                {
                    ContextWindow = 4_096,
                    EffectiveContextWindow = 0,
                    ContextEvidence = "Effective context is unavailable until the model is loaded.",
                    ConfigurationIdentity = "config-available-v1"
                })
        };
        var targets = new[]
        {
            new ProviderAssignmentTargetPresentation("gamma", "Gamma"),
            new ProviderAssignmentTargetPresentation("alpha", "Alpha"),
            new ProviderAssignmentTargetPresentation("narrator", "Narrator"),
            new ProviderAssignmentTargetPresentation(
                "default",
                "Default for unassigned agents",
                "Fallback route for otherwise unassigned agents.",
                AssignedState: ProviderTargetAssignmentState.Default,
                IsDefault: true),
            new ProviderAssignmentTargetPresentation("delta", "Delta"),
            new ProviderAssignmentTargetPresentation("beta", "Beta")
        };
        var presentation = new ProviderModelAssignmentsPresentation(
            "LM Studio",
            "Online",
            ProviderConnectionState.Online,
            "1 loaded, 1 available.",
            models,
            targets,
            "loaded-config",
            CanAssign: true,
            PresentationIdentity: "models-configuration-host-v1",
            CanRunLifecycle: true,
            ConnectionIdentity: connectionIdentity);
        return presentation;
    }
}
