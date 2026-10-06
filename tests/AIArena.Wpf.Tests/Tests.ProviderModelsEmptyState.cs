using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    static void ProviderModelsEmptyStateKeepsRecoveryActionsUniqueAndTruthful()
    {
        RunStaTest(() =>
        {
            foreach (var (width, height) in new[] { (760d, 500d), (520d, 420d), (1280d, 780d) })
            {
                var loading = ProviderModelsPinnedPresentation(0, 0, "") with
                {
                    IsRefreshing = true,
                    ConnectionState = ProviderConnectionState.Checking,
                    ConnectionStatus = "Refreshing",
                    CatalogStatus = "Loading provider models."
                };
                var control = HostProviderModelsOptimizationSurface(loading, width, height, out var host);
                var connectionRequests = 0;
                var refreshRequests = 0;
                control.ConnectionSettingsRequested += (_, _) => connectionRequests++;
                control.RefreshRequested += (_, _) => refreshRequests++;
                try
                {
                    FlushProviderModelsDispatcher(host);
                    CaptureProviderModelsInlinePreview(control, $"models-empty-loading-{width:0}x{height:0}.png");
                    var buttons = FindProviderModelsDescendants<Button>(control).ToArray();
                    var connection = buttons.Where(button => AutomationProperties.GetName(button)
                        == "Open provider connection settings").ToArray();
                    var refresh = buttons.Where(button => AutomationProperties.GetName(button)
                        == "Refresh provider models").ToArray();
                    var title = control.FindName("ModelsEmptyStateTitle") as TextBlock;
                    Require(connection.Length == 1 && refresh.Length == 1
                            && title?.Text == "Loading models…",
                        "The empty catalog duplicates header recovery actions or claims an empty result while discovery is running.");
                    Require(ProviderModelsElementFits(connection[0], control)
                            && ProviderModelsElementFits(refresh[0], control)
                            && connection[0].IsEnabled && !refresh[0].IsEnabled,
                        "Recovery controls are clipped or allow a second discovery while refreshing.");
                    InvokeProviderModelsEmptyAction(connection[0]);
                    FlushProviderModelsDispatcher(host);
                    Require(connectionRequests == 1 && refreshRequests == 0,
                        "The unique connection action did not route to the shared connection handler.");

                    control.ApplyPresentation(loading with
                    {
                        IsRefreshing = false,
                        ConnectionState = ProviderConnectionState.Offline,
                        ConnectionStatus = "Offline",
                        CatalogStatus = "No model servers responded."
                    });
                    FlushProviderModelsDispatcher(host);
                    CaptureProviderModelsInlinePreview(control, $"models-empty-offline-{width:0}x{height:0}.png");
                    var empty = (Border)control.FindName("ModelsEmptyState");
                    var message = (TextBlock)control.FindName("ModelsEmptyStateText");
                    Require(title!.Text == "No catalog models shown"
                            && !message.Text.Contains("Loading", StringComparison.Ordinal)
                            && ProviderModelsElementFits(title, empty)
                            && ProviderModelsElementFits(message, empty)
                            && refresh[0].IsEnabled,
                        "The completed empty-state message is stale or clipped in the compact catalog viewport.");
                    InvokeProviderModelsEmptyAction(refresh[0]);
                    FlushProviderModelsDispatcher(host);
                    Require(refreshRequests == 1 && connectionRequests == 1,
                        "The unique refresh action did not route exactly one request.");
                }
                finally { host.Close(); }
            }
        });
    }

    static void ProviderModelsEmptyStateClearsFiltersWithoutHidingLoadedModels()
    {
        RunStaTest(() =>
        {
            var control = HostProviderModelsOptimizationSurface(
                ProviderModelsPinnedPresentation(1, 4, ""), 760, 500, out var host);
            try
            {
                FlushProviderModelsDispatcher(host);
                control.SearchBox.Text = "not present";
                FlushProviderModelsSearchDebounce(host);
                var clear = FindProviderModelsDescendants<Button>(control).Single(button =>
                    AutomationProperties.GetName(button) == "Clear model search and filter");
                var empty = (Border)control.FindName("ModelsEmptyState");
                CaptureProviderModelsInlinePreview(control, "models-empty-filtered-760x500.png");
                Require(control.CatalogList.Items.Count == 0 && control.LoadedList.Items.Count == 1
                        && clear.IsVisible && ProviderModelsElementFits(clear, empty),
                    "The no-match recovery action is clipped or filtering hides the pinned loaded model.");
                InvokeProviderModelsEmptyAction(clear);
                FlushProviderModelsDispatcher(host);
                Require(control.CatalogList.Items.Count == 4 && control.LoadedList.Items.Count == 1
                        && control.SearchQuery.Length == 0 && control.SearchBox.IsKeyboardFocusWithin
                        && empty.Visibility == Visibility.Collapsed,
                    "Clearing empty-state filters did not restore the catalog and search focus.");
            }
            finally { host.Close(); }
        });
    }

    static void InvokeProviderModelsEmptyAction(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var action = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("The Models recovery action has no accessible Invoke pattern.");
        action.Invoke();
    }
}
