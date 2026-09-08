using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderServerPickerUsesCurrentSavedCredentials()
    {
        const string address = "http://127.0.0.1:1234/v1";
        var inventory = new ProviderServerInventory();
        inventory.ReplaceDetectedServers([new ModelProviderConfig
        {
            BaseUrl = address, ApiMode = ModelProviderApiModes.LmStudioNative, ApiToken = "old-cached-credential"
        }]);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        foreach (var currentToken in new[] { "rotated-credential", "" })
        {
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = address, ApiMode = ModelProviderApiModes.OpenAiCompatible, ApiToken = currentToken
            };
            var available = inventory.CaptureServers(snapshot);
            Require(ProviderDiscoverySelection.CredentialForSelection(available, address) == currentToken,
                "selecting a cached server must resolve a rotated or cleared token from the current saved configuration");
            Require(available.Single().ApiMode == ModelProviderApiModes.LmStudioNative,
                "refreshing selected server credentials must retain its detected native adapter");
            Require(ProviderDiscoverySelection.CredentialForSelection(available, "http://localhost:1234/v1").Length == 0,
                "selecting an origin alias must not expand the saved credential scope");
        }
    }

    static void ProviderNonConnectionSavesKeepUnconnectedDraftsOutOfConfiguration()
    {
        RunStaTest(() =>
        {
            foreach (var draftAddress in new[] { "", "http://127.0.0.1:9876/v1" })
            {
                using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.LmStudioNative, persistChanges: true);
                var snapshot = fixture.Store.LoadSnapshotAsync(fixture.SessionA.Id).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("The persisted fixture session must exist before applying its connection.");
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:4321/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
                    ApiToken = "saved-connection-credential", Model = "fixture-model", NativeIdleTtlSeconds = 60
                };
                fixture.ApplySnapshot(snapshot);
                fixture.TextBox("providerBaseUrlText").Text = draftAddress;
                fixture.Password.Password = "unconnected-draft-credential";
                fixture.TextBox("providerNativeIdleTtlText").Text = "720";
                PumpProviderModelsRefresh(async () =>
                {
                    await fixture.Coordinator.ProviderNativeOptionsCommittedAsync();
                    var saved = (await fixture.Store.LoadSnapshotAsync(fixture.SessionA.Id))!.Configs[ModelProviderRouting.SharedConfigKey];
                    Require(saved.BaseUrl == "http://127.0.0.1:4321/v1"
                        && saved.ApiMode == ModelProviderApiModes.LmStudioNative
                        && saved.ApiToken == "saved-connection-credential"
                        && saved.NativeIdleTtlSeconds == 720,
                        "saving native options must update those options while retaining the connected endpoint and credential, even with an empty address draft");

                    fixture.ComboBox("providerModelText").Text = "replacement-model";
                    await fixture.Coordinator.ProviderModelCommittedAsync();
                    saved = (await fixture.Store.LoadSnapshotAsync(fixture.SessionA.Id))!.Configs[ModelProviderRouting.SharedConfigKey];
                    Require(saved.Model == "replacement-model" && saved.BaseUrl == "http://127.0.0.1:4321/v1"
                        && saved.ApiToken == "saved-connection-credential",
                        "an unrelated model-routing save must not connect or authenticate using the custom connection draft");
                });
            }
        });
    }
}
