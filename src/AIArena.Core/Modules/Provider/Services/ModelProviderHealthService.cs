using AIArena.Core.Models;

namespace AIArena.Core.Providers;

public sealed class ModelProviderHealthService
{
    private readonly ModelProviderClient _client;

    public ModelProviderHealthService(HttpClient? httpClient = null)
    {
        _client = new ModelProviderClient(httpClient);
    }

    public async Task<ModelProviderHealth> CheckAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var result = await ListModelsAsync(config, cancellationToken);
        var ok = result.Ok && result.Models.Count > 0;
        var error = ok
            ? ""
            : string.IsNullOrWhiteSpace(result.Error) ? "Provider returned no advertised models." : result.Error;
        return new ModelProviderHealth(ok, ok ? "online" : "offline", result.BaseUrl, result.Models.Count, error, result.CheckedAt);
    }

    public async Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return await _client.ListModelsAsync(config, cancellationToken);
    }

    public async Task<ModelProviderTestResult> TestCompletionAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var testConfig = new ModelProviderConfig
        {
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            Timeout = config.Timeout,
            Temperature = 0,
            MaxOutputTokens = 16
        };
        var result = await _client.CompleteChatAsync(
            testConfig,
            [
                new ModelChatMessage("system", "You are a connectivity test."),
                new ModelChatMessage("user", "Reply with exactly: ok")
            ],
            cancellationToken);
        return new ModelProviderTestResult(result.Ok, result.BaseUrl, result.Model, result.Text, result.LatencyMs, result.Error, result.CheckedAt);
    }

    public static string NormalizeBaseUrl(string value) => ModelProviderClient.NormalizeBaseUrl(value);

    public static int CountModels(string json) => ModelProviderClient.CountModels(json);

    public static IReadOnlyList<string> ParseModelNames(string json) => ModelProviderClient.ParseModelNames(json);

    public static string ExtractAssistantContent(string json) => ModelProviderClient.ExtractAssistantContent(json);
}
