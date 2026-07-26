using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public sealed class OllamaModelPullService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly HttpClient httpClient;

    public OllamaModelPullService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<OllamaModelPullResult> PullAsync(
        string providerBaseUrl,
        string model,
        string apiToken = "",
        CancellationToken cancellationToken = default)
    {
        var normalizedModel = model.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            return OllamaModelPullResult.Failed("", "Model ID is required.");
        }

        try
        {
            var endpoint = new Uri(new Uri(ModelProviderClient.NormalizeOllamaApiBase(providerBaseUrl) + "/"), "pull");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = normalizedModel,
                    stream = false
                })
            };
            ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return OllamaModelPullResult.Failed(normalizedModel, FriendlyBody(body, response.ReasonPhrase));
            }

            return ParseResponse(body, normalizedModel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return OllamaModelPullResult.Failed(normalizedModel, FriendlyException(ex));
        }
    }

    public static OllamaModelPullResult ParseResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var status = FirstString(root, "status", "state");
        var error = LmStudioJsonMessageExtractor.ExtractMessage(root, "error", "message", "detail", "reason");
        var digest = FirstString(root, "digest");
        var completed = FirstLong(root, "completed", "completed_bytes", "downloaded", "downloaded_bytes");
        var total = FirstLong(root, "total", "total_bytes", "total_size_bytes");
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "success" : status.Trim();
        var ok = !normalizedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(error);
        var detail = ok
            ? FormatDetail(normalizedStatus, digest, completed, total)
            : (string.IsNullOrWhiteSpace(error) ? "Ollama reported pull failure." : error);
        return new OllamaModelPullResult(ok, model, normalizedStatus, detail, digest, completed, total, ok ? "" : detail);
    }

    private static string FormatDetail(string status, string digest, long completed, long total)
    {
        var parts = new List<string>
        {
            $"Status: {status}"
        };
        if (!string.IsNullOrWhiteSpace(digest))
        {
            parts.Add($"digest {digest}");
        }

        if (completed > 0 && total > 0)
        {
            parts.Add($"{FormatBytes(completed)} / {FormatBytes(total)}");
        }
        else if (total > 0)
        {
            parts.Add(FormatBytes(total));
        }

        return string.Join(", ", parts);
    }

    private static string FriendlyBody(string body, string? reason)
    {
        var error = LmStudioJsonMessageExtractor.ExtractMessage(body, "error", "message", "detail");
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(reason) ? "Ollama pull request failed." : reason.Trim();
    }

    private static string FriendlyException(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return "Ollama pull request timed out.";
        }

        if (ex is UriFormatException)
        {
            return "Invalid Ollama native API URL.";
        }

        return ex.Message;
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string apiToken)
    {
        var token = apiToken.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? "";
            }
        }

        return "";
    }

    private static long FirstLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.#} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes} B";
    }
}

public sealed record OllamaModelPullResult(
    bool Ok,
    string Model,
    string Status,
    string Detail,
    string Digest,
    long CompletedBytes,
    long TotalBytes,
    string Error)
{
    public static OllamaModelPullResult Failed(string model, string error)
    {
        return new OllamaModelPullResult(false, model, "failed", error, "", 0, 0, error);
    }
}
