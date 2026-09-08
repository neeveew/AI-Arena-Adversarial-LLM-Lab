using AIArena.Core.Models;
using AIArena.Wpf;

internal static partial class Program
{
    static void SessionGenerationSettingsPreserveExplicitProviderRoutes()
    {
        var previousShared = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = ModelProviderApiModes.LmStudioNative,
            Model = "shared", Temperature = 0.7, MaxOutputTokens = 1024
        };
        var updatedShared = new ModelProviderConfig
        {
            BaseUrl = previousShared.BaseUrl, ApiMode = previousShared.ApiMode,
            Model = "shared", Temperature = 0.5, MaxOutputTokens = 2048
        };
        var ollamaRole = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:11434/v1", ApiMode = ModelProviderApiModes.OllamaNative,
            ApiToken = "test-only-ollama-credential", Model = "shared", ExplicitModelAssignment = true,
            Temperature = 0.7, MaxOutputTokens = 1024, ContextLength = 8192,
            Reasoning = "high", NativeIdleTtlSeconds = 600
        };
        var lmStudioRole = new ModelProviderConfig
        {
            BaseUrl = previousShared.BaseUrl, ApiMode = previousShared.ApiMode,
            Model = "alpha-model", ExplicitModelAssignment = true,
            Temperature = 0.7, MaxOutputTokens = 1024
        };
        var configs = new Dictionary<string, ModelProviderConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = updatedShared, ["alpha"] = lmStudioRole, ["beta"] = ollamaRole
        };
        ArenaSessionMutationCoordinator.RefreshRoleInheritedGenerationDefaults(configs, "alpha", previousShared, updatedShared);
        ArenaSessionMutationCoordinator.RefreshRoleInheritedGenerationDefaults(configs, "beta", previousShared, updatedShared);
        Require(ReferenceEquals(configs["beta"], ollamaRole),
            "ordinary generation tuning must preserve the entire explicit configuration on another server, even when the model identifier equals the shared model");
        Require(configs["alpha"].BaseUrl == previousShared.BaseUrl && configs["alpha"].Model == "alpha-model"
            && configs["alpha"].Temperature == 0.5 && configs["alpha"].MaxOutputTokens == 2048,
            "an explicit model on the unchanged shared server should still inherit generation defaults it has not overridden");

        var differentDefault = new ModelProviderConfig
        {
            BaseUrl = ollamaRole.BaseUrl, ApiMode = ollamaRole.ApiMode, Model = "new-default",
            Temperature = 0.3, MaxOutputTokens = 4096
        };
        configs["alpha"] = lmStudioRole;
        ArenaSessionMutationCoordinator.RefreshRoleInheritedGenerationDefaults(configs, "alpha", previousShared, differentDefault);
        Require(ReferenceEquals(configs["alpha"], lmStudioRole),
            "changing the default server must not move an explicitly assigned model away from its original server");
    }
}
