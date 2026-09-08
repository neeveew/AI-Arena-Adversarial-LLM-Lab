using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIArena.Core.Providers;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderErrorsShareBoundedCredentialPolicy()
    {
        const string token = "fixture/credential+with-equal=";
        foreach (var value in new[]
        {
            "Provider rejected " + token,
            "Provider rejected " + Uri.EscapeDataString(token),
            "Authorization: Bearer unknown-fixture",
            "Failure at https://fixture-user:fixture-pass@example.test/v1",
            new string('x', 350) + token
        })
        {
            var safe = ProviderErrorSanitizer.Sanitize(value, token);
            Require(safe == ProviderErrorSanitizer.SensitiveError, "Credentials must be detected before display truncation");
            Require(ProviderConfigurationControlService.SanitizeError(value, token) == safe,
                "Settings and Core must share the same provider disclosure policy");
        }
        Require(ProviderErrorSanitizer.Sanitize("  model\n unavailable ") == "model unavailable", "Safe errors must remain useful");
        Require(ProviderErrorSanitizer.Sanitize(new string('x', 500)).Length <= ProviderErrorSanitizer.MaximumLength,
            "All provider errors must have a bounded display size");
        Require(!ProviderErrorSanitizer.Sanitize(new string('x', 70_000) + token, token).Contains(token),
            "Oversized bodies must fail closed without scanning or displaying a truncated credential");
        Require(ProviderErrorSanitizer.Endpoint("https://fixture-user:fixture-pass@example.test/v1?token=secret#secret") == "https://example.test/v1",
            "Endpoint labels must omit user info, query and fragments");
    }

    static void ProviderDownloadAndPullSanitizeHttpAndStructuredFailures()
    {
        const string token = "fixture-download-secret";
        var error = new string('x', 385) + token;
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.OK })
        {
            var body = JsonSerializer.Serialize(new { status = "failed", error = new { message = error } });
            // JSON escapes must be decoded before applying the same disclosure policy.
            body = body.Replace("fixture", "\\u0066ixture", StringComparison.Ordinal);
            using var http = new HttpClient(new TestHttpMessageHandler(_ => new HttpResponseMessage(status)
                { Content = new StringContent(body) }));
            var downloadService = new LmStudioModelDownloadService(http);
            var download = downloadService.StartDownloadAsync("http://127.0.0.1:1234", "model", apiToken: token).GetAwaiter().GetResult();
            var poll = downloadService.GetStatusAsync("http://127.0.0.1:1234", "job", apiToken: token).GetAwaiter().GetResult();
            var pull = new OllamaModelPullService(http).PullAsync("http://127.0.0.1:11434", "model", token).GetAwaiter().GetResult();
            foreach (var message in new[] { download.Error, download.Detail, poll.Error, poll.Detail, pull.Error, pull.Detail })
                Require(message == ProviderErrorSanitizer.SensitiveError,
                    "HTTP errors and HTTP-success terminal failures must hide the entire credential before shortening");
            Require(!download.Ok && !poll.Ok && !pull.Ok, "Structured provider failures must remain failures");
        }
        Require(ProviderHttpHelpers.FriendlyError("", "Bearer fixture-secret", "failure", token) == ProviderErrorSanitizer.SensitiveError,
            "A provider-supplied HTTP reason phrase must be sanitized too");
    }
    static void ProviderErrorPolicyPreservesSuccessfulDigestAndJobMetadata()
    {
        var digest = "sha256:" + new string('a', 64);
        var pull = OllamaModelPullService.ParseResponse(JsonSerializer.Serialize(new { status = "success", digest }), "model");
        var download = LmStudioModelDownloadService.ParseStartResponse(JsonSerializer.Serialize(new
            { status = "running", job_id = new string('a', 32) }), "model", "");
        Require(pull.Ok && pull.Digest == digest && string.IsNullOrEmpty(pull.Error) && !pull.Detail.Contains("error", StringComparison.OrdinalIgnoreCase),
            "Success metadata such as content digests must not be interpreted as provider errors");
        Require(download.Ok && download.JobId == new string('a', 32) && string.IsNullOrEmpty(download.Error) && !download.Detail.Contains("error", StringComparison.OrdinalIgnoreCase),
            "An opaque accepted job ID must stay usable even if it resembles a credential");
    }
}
