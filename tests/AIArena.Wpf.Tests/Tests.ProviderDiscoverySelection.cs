using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderDiscoveryKeepsCurrentServerWithoutGuessingBetweenOthers()
    {
        var lmStudio = new ProviderConnectionDiscoveryResult(true, "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "LM Studio", 25, "", "");
        var ollama = new ProviderConnectionDiscoveryResult(true, "http://127.0.0.1:11434/v1", ModelProviderApiModes.OllamaNative, "Ollama", 4, "", "");
        var unavailable = new ProviderConnectionDiscoveryResult(false, "http://127.0.0.1:8080/v1", "", "", 0, "", "offline");
        ProviderConnectionDiscoveryResult[] found = [lmStudio, ollama];
        Require(ReferenceEquals(ProviderDiscoverySelection.PreferredServer(found, ollama.BaseUrl), ollama),
            "finding LM Studio must not replace an already configured Ollama default");
        Require(ReferenceEquals(ProviderDiscoverySelection.PreferredServer(found, "http://localhost:1234/v1"), lmStudio),
            "localhost aliases should resolve the same already configured server");
        Require(ProviderDiscoverySelection.PreferredServer(found, "http://127.0.0.1:9999/v1") is null,
            "multiple running servers must not produce an arbitrary replacement for an unavailable default");
        Require(ReferenceEquals(ProviderDiscoverySelection.PreferredServer([ollama, unavailable], "http://127.0.0.1:9999/v1"), ollama),
            "one available server can be selected when the former default is unavailable");
        Require(ProviderDiscoverySelection.PreferredServer([unavailable], unavailable.BaseUrl) is null,
            "an unavailable endpoint must not become a connection choice");
        Require(ProviderDiscoverySelection.PreferredServer([ollama], lmStudio.BaseUrl, "gemma-on-lmstudio") is null,
            "a saved default model must not silently move to the only available different server without evidence that it exists there");
        Require(ReferenceEquals(ProviderDiscoverySelection.PreferredServer([ollama], lmStudio.BaseUrl, "  "), ollama),
            "an unassigned shared default may use the only available server");
        Require(ReferenceEquals(ProviderDiscoverySelection.PreferredServer(found, lmStudio.BaseUrl, "gemma-on-lmstudio"), lmStudio),
            "a configured default model should keep its current server when that server is available");
        Require(found.Length == 2 && found.Contains(lmStudio) && found.Contains(ollama),
            "choosing a shared default must leave every discovered server available to Models");
    }

    static void ProviderDiscoveryKeepsCredentialsWithinTheSavedOrigin()
    {
        const string saved = "https://provider.example:8443/arena/v1";
        const string credential = "test-only-server-credential";
        Require(ProviderDiscoverySelection.CredentialFor(saved, saved, credential) == credential,
            "the saved endpoint should retain its configured authentication");
        foreach (var target in new[]
        {
            "http://provider.example:8443/arena/v1",
            "https://provider.example:8444/arena/v1",
            "https://other.example:8443/arena/v1",
            "https://provider.example:8443/other/v1"
        })
        {
            Require(ProviderDiscoverySelection.CredentialFor(target, saved, credential).Length == 0,
                "automatic discovery must not transfer a saved credential to another origin or proxy route");
        }
        Require(ProviderDiscoverySelection.CredentialFor("http://localhost:1234/v1", "http://127.0.0.1:1234/v1", credential).Length == 0,
            "inventory aliases must not expand the origin allowed to receive a saved credential");
    }
}
