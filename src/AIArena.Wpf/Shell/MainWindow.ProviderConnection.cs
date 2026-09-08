using System.Windows;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow
{
    private readonly ProviderConnectionDiscoveryService _providerConnectionDiscovery = new();
    private readonly ProviderServerInventory _providerServerInventory = new();
    private readonly SemaphoreSlim _providerDiscoveryGate = new(1, 1);
    private readonly List<DetectedProviderServer> _detectedProviderServers = [];
    private CancellationTokenSource? _providerDiscoveryCancellation;
    private string? _detectedProviderSessionId;
    private bool _providerDiscoveryPending;
    private bool _isUpdatingDetectedProviderServers;
    private long _providerDiscoveryVersion;

    private Task DiscoverProviderServersAsync(CancellationToken cancellationToken) =>
        RunProviderServerDiscoveryAsync(null, cancellationToken);

    private Task ConnectCustomProviderAsync(CancellationToken cancellationToken) =>
        RunProviderServerDiscoveryAsync(new ProviderServerConnectionRequest(
            _activeSession?.Id ?? "", ProviderBaseUrlText.Text.Trim(), ProviderApiTokenBox.Password, UsesDraft: true), cancellationToken);

    private async void ConnectCustomProviderButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedBackgroundOperationSafelyAsync("custom server connection", ConnectCustomProviderAsync);

    private async void ProviderServerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingDetectedProviderServers || _isRenderingSnapshot
            || ProviderServerPicker.SelectedItem is not DetectedProviderServer server
            || !string.Equals(_detectedProviderSessionId, _activeSession?.Id, StringComparison.Ordinal)) return;

        var request = new ProviderServerConnectionRequest(
            _detectedProviderSessionId!, server.Result.BaseUrl, server.Config.ApiToken, UsesDraft: false);
        await RunTrackedBackgroundOperationSafelyAsync(
            "server connection", cancellationToken => RunProviderServerDiscoveryAsync(request, cancellationToken));
    }

    private void CancelProviderServerDiscovery()
    {
        _providerDiscoveryVersion++;
        _providerDiscoveryCancellation?.Cancel();
        _providerDiscoveryPending = false;
        _detectedProviderSessionId = null;
        _detectedProviderServers.Clear();
        _providerServerInventory.ReplaceDetectedServers([]);
        _isUpdatingDetectedProviderServers = true;
        try
        {
            ProviderServerPicker.ItemsSource = null;
            ProviderServerPicker.Visibility = Visibility.Collapsed;
        }
        finally { _isUpdatingDetectedProviderServers = false; }
    }

    private async Task RunProviderServerDiscoveryAsync(
        ProviderServerConnectionRequest? requested, CancellationToken cancellationToken)
    {
        if (_shutdownInProgress || _activeSession is null) return;
        if (requested is not null)
        {
            _providerDiscoveryCancellation?.Cancel();
            await _providerDiscoveryGate.WaitAsync(cancellationToken);
        }
        else if (!await _providerDiscoveryGate.WaitAsync(0, cancellationToken))
        {
            _providerDiscoveryPending = true;
            return;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _providerDiscoveryCancellation = operation;
        var token = operation.Token;
        var version = ++_providerDiscoveryVersion;
        var testWasEnabled = TestProviderButton.IsEnabled;
        var customWasEnabled = ConnectCustomProviderButton.IsEnabled;
        var pickerWasEnabled = ProviderServerPicker.IsEnabled;
        ProviderOperationContext? context = null;
        var draftAddress = ProviderBaseUrlText.Text;
        var draftToken = ProviderApiTokenBox.Password;
        try
        {
            TestProviderButton.IsEnabled = false;
            ConnectCustomProviderButton.IsEnabled = false;
            ProviderServerPicker.IsEnabled = false;
            var sessionId = _activeSession?.Id;
            if (sessionId is null || requested is not null && requested.SessionId != sessionId) return;
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(sessionId, token);
            if (snapshot is null || _activeSession?.Id != sessionId || version != _providerDiscoveryVersion) return;
            var shared = snapshot.Configs.GetValueOrDefault(ModelProviderRouting.SharedConfigKey) ?? new ModelProviderConfig();
            context = new ProviderOperationContext(sessionId, shared);
            if (requested is { UsesDraft: false })
            {
                requested = requested with
                {
                    ApiToken = ProviderDiscoverySelection.CredentialForSelection(
                        _providerServerInventory.CaptureServers(snapshot), requested.Address)
                };
            }
            ProviderPresetStatusText.Text = requested is null ? "Finding running model servers..." : "Connecting to the server...";

            IReadOnlyList<ProviderConnectionDiscoveryResult> found;
            ProviderConnectionDiscoveryResult? preferred;
            if (requested is null)
            {
                var scan = await _providerConnectionDiscovery.DiscoverLocalAsync(context.BaseUrl, context.ApiToken, token);
                if (!await ProviderDiscoveryIsCurrentAsync(context, version, token)) return;
                found = scan.Servers;
                preferred = ProviderDiscoverySelection.PreferredServer(found, context.BaseUrl, shared.Model);
                _detectedProviderServers.Clear();
                foreach (var server in found)
                {
                    _detectedProviderServers.Add(new DetectedProviderServer(server,
                        ProviderDiscoverySelection.CredentialFor(server.BaseUrl, context.BaseUrl, context.ApiToken)));
                }
                if (found.Count == 0) ProviderPresetStatusText.Text = scan.Summary;
            }
            else
            {
                var result = await _providerConnectionDiscovery.DiscoverAsync(requested.Address, requested.ApiToken, token);
                if (!await ProviderDiscoveryIsCurrentAsync(context, version, token)
                    || requested.UsesDraft && !ProviderConnectionDraftMatches(requested.Address, requested.ApiToken)) return;
                if (!result.Available)
                {
                    ProviderPresetStatusText.Text = result.Error;
                    return;
                }
                _detectedProviderServers.RemoveAll(server => ProviderServerInventory.SameEndpoint(server.Result.BaseUrl, result.BaseUrl));
                _detectedProviderServers.Add(new DetectedProviderServer(result, requested.ApiToken));
                found = _detectedProviderServers.Select(server => server.Result).ToArray();
                preferred = result;
            }

            _detectedProviderSessionId = context.SessionId;
            _providerServerInventory.ReplaceDetectedServers(_detectedProviderServers.Select(server => server.Config).ToArray());
            UpdateDetectedProviderServerPicker(context.BaseUrl);
            if (preferred is not null
                && (requested is not null || ProviderConnectionDraftMatches(draftAddress, draftToken)))
            {
                var selected = _detectedProviderServers.First(server => server.Result == preferred);
                if (!ProviderDiscoverySelection.ConnectionMatches(context, selected.Config))
                {
                    var patch = new AIArenaProviderConfigurationPatch(
                        BaseUrl: selected.Config.BaseUrl,
                        ApiMode: selected.Config.ApiMode,
                        ApiToken: selected.Config.ApiToken.Length == 0 ? null : selected.Config.ApiToken,
                        ClearApiToken: selected.Config.ApiToken.Length == 0,
                        Model: null, TimeoutSeconds: null, Temperature: null, MaxOutputTokens: null,
                        ContextLength: null, Reasoning: null, NativeStatefulChat: null, NativeIdleTtlSeconds: null,
                        RoleModels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), RefreshModels: true);
                    var applied = await _providerConfigurationControlService.ApplyAsync(patch, token, context,
                        expectedSourceConfig: requested is { UsesDraft: false } ? selected.Config : null);
                    if (!applied.Ok)
                    {
                        if (version == _providerDiscoveryVersion && _activeSession?.Id == context.SessionId)
                            ProviderPresetStatusText.Text = applied.Message;
                        return;
                    }
                }
                if (version != _providerDiscoveryVersion || _activeSession?.Id != context.SessionId) return;
                UpdateDetectedProviderServerPicker(selected.Config.BaseUrl);
            }

            if (_providerModelsSurfaceCoordinator is not null && ProviderModelsPanel.Visibility == Visibility.Visible)
                await _providerModelsSurfaceCoordinator.RefreshAsync(refreshCatalog: true, token);
            RefreshDetectedProviderServersPresentation(discoveryCompleted: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            if (version == _providerDiscoveryVersion && context?.SessionId == _activeSession?.Id)
            {
                var safeError = ProviderErrorSanitizer.Sanitize(exception.Message, requested?.ApiToken);
                ProviderPresetStatusText.Text = ProviderErrorSanitizer.Sanitize(safeError, context?.ApiToken);
            }
        }
        finally
        {
            _providerDiscoveryCancellation = null;
            TestProviderButton.IsEnabled = testWasEnabled;
            ConnectCustomProviderButton.IsEnabled = customWasEnabled;
            ProviderServerPicker.IsEnabled = pickerWasEnabled;
            _providerDiscoveryGate.Release();
            if (_providerDiscoveryPending && !_shutdownInProgress)
            {
                _providerDiscoveryPending = false;
                _ = RunTrackedBackgroundOperationSafelyAsync("server discovery", DiscoverProviderServersAsync);
            }
        }
    }

    private bool ProviderConnectionDraftMatches(string address, string apiToken) =>
        string.Equals(ProviderBaseUrlText.Text.Trim(), address.Trim(), StringComparison.Ordinal)
        && string.Equals(ProviderApiTokenBox.Password, apiToken, StringComparison.Ordinal);

    private async Task<bool> ProviderDiscoveryIsCurrentAsync(
        ProviderOperationContext context, long version, CancellationToken cancellationToken)
    {
        if (version != _providerDiscoveryVersion || _activeSession?.Id != context.SessionId) return false;
        var snapshot = await _coreSessionStore.LoadSnapshotAsync(context.SessionId, cancellationToken);
        return snapshot is not null && version == _providerDiscoveryVersion && _activeSession?.Id == context.SessionId
            && context.Matches(new ProviderOperationContext(context.SessionId,
                snapshot.Configs.GetValueOrDefault(ModelProviderRouting.SharedConfigKey) ?? new ModelProviderConfig()));
    }

    private void UpdateDetectedProviderServerPicker(string currentAddress)
    {
        _isUpdatingDetectedProviderServers = true;
        try
        {
            ProviderServerPicker.DisplayMemberPath = nameof(DetectedProviderServer.Label);
            ProviderServerPicker.ItemsSource = _detectedProviderServers.ToArray();
            ProviderServerPicker.SelectedItem = _detectedProviderServers.FirstOrDefault(server =>
                ProviderServerInventory.SameEndpoint(server.Result.BaseUrl, currentAddress));
            ProviderServerPicker.Visibility = _detectedProviderServers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _isUpdatingDetectedProviderServers = false; }
    }

    private void RefreshDetectedProviderServersPresentation(bool discoveryCompleted = false)
    {
        if (_detectedProviderSessionId != _activeSession?.Id || _detectedProviderServers.Count == 0
            || _providerDiscoveryCancellation is not null && !discoveryCompleted) return;
        ProviderPresetStatusText.Text = ProviderDiscoverySelection.InventorySummary(
            _detectedProviderServers.Select(server => server.Result).ToArray());
    }

    private sealed record ProviderServerConnectionRequest(string SessionId, string Address, string ApiToken, bool UsesDraft)
    {
        public override string ToString() => "Provider server connection request";
    }

    private sealed class DetectedProviderServer(ProviderConnectionDiscoveryResult result, string apiToken)
    {
        public ProviderConnectionDiscoveryResult Result { get; } = result;
        public ModelProviderConfig Config { get; } = new() { BaseUrl = result.BaseUrl, ApiMode = result.ApiMode, ApiToken = apiToken };
        public string Label => $"{Result.ProviderName} · {new Uri(Result.BaseUrl).Authority} · {Result.CatalogCount} models";
        public override string ToString() => Label;
    }
}

internal static class ProviderDiscoverySelection
{
    internal static ProviderConnectionDiscoveryResult? PreferredServer(
        IReadOnlyList<ProviderConnectionDiscoveryResult> servers, string currentAddress, string? configuredModel = null)
    {
        var available = servers.Where(server => server.Available).ToArray();
        return available.FirstOrDefault(server => ProviderServerInventory.SameEndpoint(server.BaseUrl, currentAddress))
            ?? (available.Length == 1 && string.IsNullOrWhiteSpace(configuredModel) ? available[0] : null);
    }

    internal static string CredentialFor(string foundAddress, string savedAddress, string savedToken) =>
        ProviderServerInventory.SameEndpoint(foundAddress, savedAddress)
        && ProviderServerInventory.SameOrigin(foundAddress, savedAddress) ? savedToken : "";

    internal static string CredentialForSelection(IReadOnlyList<ModelProviderConfig> currentServers, string selectedAddress) =>
        currentServers.FirstOrDefault(server => ProviderServerInventory.SameEndpoint(server.BaseUrl, selectedAddress)
            && ProviderServerInventory.SameOrigin(server.BaseUrl, selectedAddress))?.ApiToken ?? "";

    internal static bool ConnectionMatches(ProviderOperationContext context, ModelProviderConfig config) =>
        context.Matches(new ProviderOperationContext(context.SessionId, config));

    internal static string InventorySummary(IReadOnlyList<ProviderConnectionDiscoveryResult> servers) => servers.Count switch
    {
        0 => "No running model servers found. Start a server, or enter its address in Advanced.",
        1 => $"{servers[0].ProviderName} is available. {servers[0].CatalogCount} models in Models.",
        _ => $"{servers.Count} servers available: {string.Join(", ", servers.Select(server => server.ProviderName).Distinct())}. Use models from all of them in Models."
    };
}
