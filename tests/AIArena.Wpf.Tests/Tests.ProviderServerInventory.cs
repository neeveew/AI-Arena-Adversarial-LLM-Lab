using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderServerInventoryMergesDetectedAndConfiguredServers()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs.Clear();
        snapshot.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.OpenAiCompatible,
            ApiToken = "shared-inventory-secret", Model = "same-model"
        };
        snapshot.Configs["alpha"] = new ModelProviderConfig
        {
            BaseUrl = "https://private.example.test:9443/proxy/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = "role-inventory-secret", Model = "same-model", ExplicitModelAssignment = true
        };
        var inventory = new ProviderServerInventory();
        inventory.ReplaceDetectedServers([
            new ModelProviderConfig { BaseUrl = "http://127.0.0.1:1234/api/v1", ApiMode = ModelProviderApiModes.LmStudioNative },
            new ModelProviderConfig { BaseUrl = "http://127.0.0.1:11434/v1", ApiMode = ModelProviderApiModes.OllamaNative }
        ]);
        var captured = inventory.CaptureServers(snapshot);
        Require(captured.Count == 3, "detected servers should merge with persisted private role endpoints without duplicating compatible/native aliases");
        var studio = captured.Single(config => new Uri(config.BaseUrl).Port == 1234);
        Require(studio.ApiMode == ModelProviderApiModes.LmStudioNative && studio.ApiToken == "shared-inventory-secret"
                && studio.Model == "same-model", "positive detected capabilities should retain the same endpoint's existing credential and model configuration");
        Require(captured.Single(config => new Uri(config.BaseUrl).Port == 11434).ApiToken.Length == 0,
            "a newly discovered local server must never inherit another server's credential");
        Require(captured.Single(config => new Uri(config.BaseUrl).Host == "private.example.test").ApiToken == "role-inventory-secret",
            "configured private role connections must remain usable when anonymous discovery cannot see them");
        Require(snapshot.Configs["shared"].ApiMode == ModelProviderApiModes.OpenAiCompatible,
            "inventory projection must not mutate persisted shared routing");
        inventory.ReplaceDetectedServers([new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = "stale-discovery-secret"
        }]);
        snapshot.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = "", Model = "same-model"
        };
        Require(inventory.CaptureServers(snapshot).Single(config => new Uri(config.BaseUrl).Port == 1234).ApiToken.Length == 0,
            "clearing a persisted server token must immediately override an older discovery credential");
        inventory.ReplaceDetectedServers([]);
        Require(inventory.CaptureServers(snapshot).Count == 2,
            "a discovery replacement must preserve persisted role and shared server connections");
    }

    static void ProviderServerInventoryQualifiesModelsAndRestrictsCredentialOrigins()
    {
        var lm = new ModelProviderConfig { BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative };
        var compatible = new ModelProviderConfig { BaseUrl = "http://localhost:1234/api/v1", ApiMode = ModelProviderApiModes.OpenAiCompatible };
        var ollama = new ModelProviderConfig { BaseUrl = "http://127.0.0.1:11434/v1", ApiMode = ModelProviderApiModes.OllamaNative };
        Require(ProviderServerInventory.ServerIdentity(lm) == ProviderServerInventory.ServerIdentity(compatible),
            "native discovery and compatible URLs for the same local server must share physical identity");
        Require(ProviderServerInventory.ModelIdentity(lm, "same-model") != ProviderServerInventory.ModelIdentity(ollama, "same-model"),
            "identical model names on different servers must have distinct inventory row identities");
        Require(ProviderServerInventory.ModelIdentity(lm, "raw/model-id") == ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
            { BaseUrl = lm.BaseUrl, ApiMode = lm.ApiMode, Model = "raw/model-id" }),
            "qualified model identity should reuse the existing runtime settings identity without changing the actual provider model ID");
        Require(ProviderServerInventory.SameEndpoint(lm.BaseUrl, compatible.BaseUrl)
                && !ProviderServerInventory.SameOrigin(lm.BaseUrl, compatible.BaseUrl)
                && !ProviderServerInventory.SameOrigin(lm.BaseUrl, ollama.BaseUrl),
            "physical-server alias matching must stay separate from stricter credential-origin matching");
        Require(!ProviderServerInventory.SameEndpoint("https://host.test/one/v1", "https://host.test/two/v1"),
            "different proxy base paths must remain separate provider endpoints");
    }
}