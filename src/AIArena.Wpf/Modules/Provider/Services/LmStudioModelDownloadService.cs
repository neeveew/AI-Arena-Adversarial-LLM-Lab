using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Wpf.Services;

public sealed class LmStudioModelDownloadService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private readonly HttpClient httpClient;

    public LmStudioModelDownloadService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<LmStudioModelDownloadResult> StartDownloadAsync(
        string providerBaseUrl,
        string model,
        string quantization = "",
        string apiMode = ModelProviderApiModes.LmStudioNative,
        string apiToken = "",
        CancellationToken cancellationToken = default)
    {
        var normalizedModel = model.Trim();
        var normalizedQuantization = NormalizeQuantization(quantization);
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            return LmStudioModelDownloadResult.Failed("", normalizedQuantization, "", "Model ID is required.");
        }

        if (!ModelProviderApiModes.Normalize(apiMode).Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
        {
            return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, "", "Model download uses LM Studio's native /api/v1/models/download endpoint. Switch API mode to LM Studio native.");
        }

        try
        {
            var endpoint = new Uri(new Uri(LmStudioModelCatalogService.NormalizeLmStudioApiBase(providerBaseUrl) + "/"), "models/download");
            var payload = new Dictionary<string, object>
            {
                ["model"] = normalizedModel
            };
            if (!string.IsNullOrWhiteSpace(normalizedQuantization))
            {
                payload["quantization"] = normalizedQuantization;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, "", FriendlyBody(body, response.ReasonPhrase));
            }

            return ParseStartResponse(body, normalizedModel, normalizedQuantization);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, "", FriendlyException(ex));
        }
    }

    public async Task<LmStudioModelDownloadResult> GetStatusAsync(
        string providerBaseUrl,
        string jobId,
        string model = "",
        string quantization = "",
        string apiToken = "",
        CancellationToken cancellationToken = default)
    {
        var normalizedJobId = jobId.Trim();
        var normalizedModel = model.Trim();
        var normalizedQuantization = NormalizeQuantization(quantization);
        if (string.IsNullOrWhiteSpace(normalizedJobId))
        {
            return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, "", "Download job ID is required.");
        }

        try
        {
            var endpoint = new Uri(
                new Uri(LmStudioModelCatalogService.NormalizeLmStudioApiBase(providerBaseUrl) + "/"),
                $"models/download/status/{Uri.EscapeDataString(normalizedJobId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ApplyAuthorization(request, apiToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, normalizedJobId, FriendlyBody(body, response.ReasonPhrase));
            }

            return ParseStatusResponse(body, normalizedModel, normalizedQuantization, normalizedJobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return LmStudioModelDownloadResult.Failed(normalizedModel, normalizedQuantization, normalizedJobId, FriendlyException(ex));
        }
    }

    public static LmStudioModelDownloadResult ParseStartResponse(string json, string model, string quantization)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var status = FirstString(root, "status", "state");
        var jobId = FirstString(root, "job_id", "jobId", "id");
        var responseModel = FirstString(root, "model", "model_key", "key");
        var error = LmStudioJsonMessageExtractor.ExtractMessage(root, "error", "message", "detail", "reason");
        var effectiveModel = string.IsNullOrWhiteSpace(responseModel) ? model : responseModel;
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "started" : status.Trim();
        var isComplete = IsCompleteStatus(normalizedStatus);
        var ok = !normalizedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(error);
        if (ok && !isComplete && string.IsNullOrWhiteSpace(jobId))
        {
            const string missingJob = "LM Studio accepted the download but did not return a job_id to track.";
            return new LmStudioModelDownloadResult(false, effectiveModel, quantization, "", "failed", missingJob, true, missingJob);
        }

        var detail = ok
            ? FormatDetail(normalizedStatus, jobId, root)
            : (string.IsNullOrWhiteSpace(error) ? "LM Studio reported download failure." : error);
        return new LmStudioModelDownloadResult(ok, effectiveModel, quantization, jobId, normalizedStatus, detail, isComplete, ok ? "" : detail);
    }

    public static LmStudioModelDownloadResult ParseStatusResponse(string json, string model, string quantization, string jobId)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var status = FirstString(root, "status", "state");
        var responseModel = FirstString(root, "model", "model_key", "key");
        var error = LmStudioJsonMessageExtractor.ExtractMessage(root, "error", "message", "detail", "reason");
        var effectiveModel = string.IsNullOrWhiteSpace(responseModel) ? model : responseModel;
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
        var isComplete = IsCompleteStatus(normalizedStatus);
        var ok = !normalizedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(error);
        var detail = ok
            ? FormatDetail(normalizedStatus, jobId, root)
            : (string.IsNullOrWhiteSpace(error) ? "LM Studio reported download failure." : error);
        return new LmStudioModelDownloadResult(ok, effectiveModel, quantization, jobId, normalizedStatus, detail, isComplete, ok ? "" : detail);
    }

    private static string FormatDetail(string status, string jobId, JsonElement root)
    {
        var percent = FirstDouble(root, "progress", "progress_percent", "percent");
        if (percent is > 0 and <= 1)
        {
            percent *= 100;
        }

        var downloaded = FirstLong(root, "downloaded_bytes", "bytes_downloaded", "downloaded");
        var total = FirstLong(root, "total_size_bytes", "total_bytes", "bytes_total", "total");
        var parts = new List<string>
        {
            $"Status: {status}"
        };
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            parts.Add($"job {jobId}");
        }

        if (percent > 0)
        {
            parts.Add($"{percent:0.#}%");
        }
        else if (downloaded > 0 && total > 0)
        {
            parts.Add($"{(downloaded * 100d / total):0.#}%");
        }

        if (downloaded > 0 && total > 0)
        {
            parts.Add($"{FormatBytes(downloaded)} / {FormatBytes(total)}");
        }

        var etaSeconds = FirstDouble(root, "eta_seconds", "eta");
        if (etaSeconds > 0)
        {
            parts.Add($"ETA {TimeSpan.FromSeconds(etaSeconds):mm\\:ss}");
        }

        return string.Join(", ", parts);
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

    private static bool IsCompleteStatus(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("already_downloaded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("downloaded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("ready", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("done", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeQuantization(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
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

    private static double FirstDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var result))
            {
                return result;
            }
        }

        return 0;
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

    private static string FriendlyBody(string body, string? reason)
    {
        var error = LmStudioJsonMessageExtractor.ExtractMessage(body, "error", "message", "detail");
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(reason) ? "LM Studio request failed." : reason.Trim();
    }

    private static string FriendlyException(Exception ex)
    {
        return ex is TaskCanceledException
            ? "LM Studio download request timed out."
            : ex.Message;
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string apiToken)
    {
        var token = apiToken.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }
}

public sealed record LmStudioModelDownloadResult(
    bool Ok,
    string Model,
    string Quantization,
    string JobId,
    string Status,
    string Detail,
    bool IsComplete,
    string Error)
{
    public static LmStudioModelDownloadResult Failed(string model, string quantization, string jobId, string error)
    {
        return new LmStudioModelDownloadResult(false, model, quantization, jobId, "failed", error, true, error);
    }
}
