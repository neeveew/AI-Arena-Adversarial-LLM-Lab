using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIArena.VerificationLab;

internal enum ScriptedProviderFault
{
    None,
    Timeout,
    Disconnect,
    MalformedStream,
    Saturation,
    Empty,
    HttpError
}

/// <summary>
/// Content-free request evidence. Request bodies and credentials are never
/// retained; the body hash permits deterministic equality checks without
/// exposing prompts or transcript content.
/// </summary>
internal sealed record ScriptedRequestCapture(
    int Sequence,
    string Method,
    string Path,
    int BodyLength,
    string BodySha256,
    string ModelEvidence,
    int MessageCount,
    bool Streaming,
    bool AuthorizationSupplied,
    ScriptedProviderFault Fault);

internal sealed class ScriptedProviderHost : IAsyncDisposable
{
    internal const int MaximumRequestBodyBytes = 256 * 1024;
    internal const int MaximumRetainedCaptures = 128;
    internal const string PrimaryModel = "scripted-model-q4_k_m.gguf";
    internal const string SecondaryModel = "scripted-model-small.gguf";
    internal const string CompletionText = "Deterministic verification response.";
    internal const string ReasoningText = "Deterministic verification reasoning.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly WebApplication _application;
    private readonly ConcurrentQueue<ScriptedProviderFault> _faults = new();
    private readonly object _captureLock = new();
    private readonly List<ScriptedRequestCapture> _captures = [];
    private int _requestSequence;
    private int _droppedCaptureCount;
    private int _rejectedOversizeRequestCount;

    private ScriptedProviderHost(WebApplication application, Uri baseUri)
    {
        _application = application;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; private set; }
    public int DroppedCaptureCount => Volatile.Read(ref _droppedCaptureCount);
    public int RejectedOversizeRequestCount => Volatile.Read(ref _rejectedOversizeRequestCount);

    public IReadOnlyList<ScriptedRequestCapture> Captures
    {
        get
        {
            lock (_captureLock)
            {
                return _captures.ToArray();
            }
        }
    }

    public static async Task<ScriptedProviderHost> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(ScriptedProviderHost).Assembly.GetName().Name,
            EnvironmentName = "Verification"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        var placeholder = new ScriptedProviderHost(application, new Uri("http://127.0.0.1"));
        placeholder.MapEndpoints();

        await application.StartAsync(cancellationToken);
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = addresses?
            .SingleOrDefault(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(address))
        {
            await application.DisposeAsync();
            throw new InvalidOperationException("Kestrel did not publish a loopback HTTP address.");
        }

        placeholder.BaseUri = new Uri(address.EndsWith('/') ? address : address + "/");
        return placeholder;
    }

    public void QueueFault(ScriptedProviderFault fault, int occurrences = 1)
    {
        if (fault == ScriptedProviderFault.None)
        {
            throw new ArgumentOutOfRangeException(nameof(fault), "Queue only an actual fault.");
        }

        if (occurrences < 1 || occurrences > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences));
        }

        for (var index = 0; index < occurrences; index++)
        {
            _faults.Enqueue(fault);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _application.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposal must not strand the verification process.
        }

        await _application.DisposeAsync();
    }

    private void MapEndpoints()
    {
        _application.MapGet("/", async context =>
        {
            await CaptureAsync(context, consumeFault: false);
            await WriteJsonAsync(context, new
            {
                status = "ok",
                server = "llama.cpp scripted verification provider",
                version = "qa-v1"
            });
        });

        _application.MapGet("/health", async context =>
        {
            await CaptureAsync(context, consumeFault: false);
            await WriteJsonAsync(context, new { status = "ok" });
        });

        _application.MapGet("/models", WriteModelsAsync);
        _application.MapGet("/v1/models", WriteModelsAsync);

        _application.MapGet("/props", async context =>
        {
            await CaptureAsync(context, consumeFault: false);
            await WriteJsonAsync(context, new
            {
                default_generation_settings = new { n_ctx = 8192 },
                total_slots = 2,
                build_info = "scripted llama.cpp qa-v1",
                sleeping = false
            });
        });

        _application.MapGet("/slots", async context =>
        {
            await CaptureAsync(context, consumeFault: false);
            await WriteJsonAsync(context, new object[]
            {
                new
                {
                    id = 0,
                    is_processing = false,
                    n_ctx = 8192,
                    next_token = new { n_decoded = 24 },
                    timings = new { predicted_per_second = 42.5 }
                },
                new
                {
                    id = 1,
                    is_processing = false,
                    n_ctx = 8192,
                    next_token = new { n_decoded = 0 },
                    timings = new { predicted_per_second = 0.0 }
                }
            });
        });

        _application.MapPost("/v1/chat/completions", HandleChatAsync);
    }

    private async Task WriteModelsAsync(HttpContext context)
    {
        await CaptureAsync(context, consumeFault: false);
        await WriteJsonAsync(context, new
        {
            @object = "list",
            data = new object[]
            {
                new
                {
                    id = PrimaryModel,
                    @object = "model",
                    owned_by = "llamacpp",
                    status = new
                    {
                        value = "loaded",
                        args = new[] { "--ctx-size", "8192", "--parallel", "2", "--n-gpu-layers", "99" }
                    },
                    meta = new { n_ctx = 8192, size_bytes = 4_000_000_000L, parameter_count = 7_000_000_000L }
                },
                new
                {
                    id = SecondaryModel,
                    @object = "model",
                    owned_by = "llamacpp",
                    status = new { value = "unloaded", args = Array.Empty<string>() },
                    meta = new { n_ctx = 4096, size_bytes = 2_000_000_000L, parameter_count = 3_000_000_000L }
                }
            }
        });
    }

    private async Task HandleChatAsync(HttpContext context)
    {
        ScriptedRequestCapture request;
        try
        {
            request = await CaptureAsync(context, consumeFault: true);
        }
        catch (ScriptedProviderRequestTooLargeException)
        {
            await WriteJsonAsync(
                context,
                new { error = new { message = "scripted request exceeded bounded body limit", code = "request_too_large" } },
                StatusCodes.Status413PayloadTooLarge);
            return;
        }
        switch (request.Fault)
        {
            case ScriptedProviderFault.Timeout:
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }

                return;
            case ScriptedProviderFault.Disconnect:
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = request.Streaming ? "text/event-stream" : "application/json";
                await context.Response.StartAsync(CancellationToken.None);
                context.Abort();
                return;
            case ScriptedProviderFault.MalformedStream:
                if (request.Streaming)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync("data: {not-json}\n\ndata: [DONE]\n\n", context.RequestAborted);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{not-json", context.RequestAborted);
                return;
            case ScriptedProviderFault.Saturation:
                await WriteJsonAsync(
                    context,
                    new { error = new { message = "scripted queue full", code = "queue_full" } },
                    StatusCodes.Status503ServiceUnavailable);
                return;
            case ScriptedProviderFault.Empty:
                if (request.Streaming)
                {
                    await WriteEmptyStreamAsync(context);
                }
                else
                {
                    await WriteCompletionAsync(context, request, "", "");
                }

                return;
            case ScriptedProviderFault.HttpError:
                await WriteJsonAsync(
                    context,
                    new { error = new { message = "scripted HTTP failure", code = "verification_failure" } },
                    StatusCodes.Status422UnprocessableEntity);
                return;
            case ScriptedProviderFault.None:
            default:
                if (request.Streaming)
                {
                    await WriteCompletionStreamAsync(context, request);
                }
                else
                {
                    await WriteCompletionAsync(context, request, CompletionText, ReasoningText);
                }

                return;
        }
    }

    private async Task<ScriptedRequestCapture> CaptureAsync(HttpContext context, bool consumeFault)
    {
        if (context.Request.ContentLength is > MaximumRequestBodyBytes)
        {
            Interlocked.Increment(ref _rejectedOversizeRequestCount);
            throw new ScriptedProviderRequestTooLargeException();
        }

        byte[] body;
        await using (var stream = new MemoryStream(Math.Min(
            MaximumRequestBodyBytes,
            checked((int)(context.Request.ContentLength ?? 0)))))
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (read == 0) break;
                if (stream.Length + read > MaximumRequestBodyBytes)
                {
                    Interlocked.Increment(ref _rejectedOversizeRequestCount);
                    throw new ScriptedProviderRequestTooLargeException();
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
            body = stream.ToArray();
        }

        var model = "";
        var messageCount = 0;
        var streaming = false;
        if (body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
                {
                    model = modelElement.GetString() ?? "";
                }

                if (root.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
                {
                    messageCount = messagesElement.GetArrayLength();
                }

                if (root.TryGetProperty("stream", out var streamElement)
                    && streamElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    streaming = streamElement.GetBoolean();
                }
            }
            catch (JsonException)
            {
                // Malformed input is captured only as length and hash.
            }
        }

        var fault = consumeFault && _faults.TryDequeue(out var queued)
            ? queued
            : ScriptedProviderFault.None;
        var capture = new ScriptedRequestCapture(
            Interlocked.Increment(ref _requestSequence),
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            body.Length,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
            SafeModelEvidence(model),
            messageCount,
            streaming,
            context.Request.Headers.ContainsKey("Authorization"),
            fault);
        lock (_captureLock)
        {
            if (_captures.Count < MaximumRetainedCaptures)
            {
                _captures.Add(capture);
            }
            else
            {
                Interlocked.Increment(ref _droppedCaptureCount);
            }
        }

        return capture;
    }

    private static Task WriteJsonAsync(HttpContext context, object payload, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), context.RequestAborted);
    }

    private static Task WriteCompletionAsync(
        HttpContext context,
        ScriptedRequestCapture request,
        string content,
        string reasoning)
    {
        return WriteJsonAsync(context, new
        {
            id = "chatcmpl-scripted",
            @object = "chat.completion",
            created = 0,
            model = string.IsNullOrWhiteSpace(request.ModelEvidence) ? PrimaryModel : request.ModelEvidence,
            choices = new object[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content, reasoning_content = reasoning },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 },
            timings = new { predicted_per_second = 42.5, time_to_first_token_ms = 12 }
        });
    }

    private static async Task WriteCompletionStreamAsync(HttpContext context, ScriptedRequestCapture request)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        await WriteSseAsync(context, new
        {
            id = "chatcmpl-scripted-stream",
            model = string.IsNullOrWhiteSpace(request.ModelEvidence) ? PrimaryModel : request.ModelEvidence,
            choices = new object[]
            {
                new { index = 0, delta = new { role = "assistant", reasoning_content = ReasoningText }, finish_reason = (string?)null }
            }
        });
        foreach (var piece in new[] { "Deterministic ", "verification ", "response." })
        {
            await WriteSseAsync(context, new
            {
                id = "chatcmpl-scripted-stream",
                model = string.IsNullOrWhiteSpace(request.ModelEvidence) ? PrimaryModel : request.ModelEvidence,
                choices = new object[]
                {
                    new { index = 0, delta = new { content = piece }, finish_reason = (string?)null }
                }
            });
        }

        await WriteSseAsync(context, new
        {
            id = "chatcmpl-scripted-stream",
            model = string.IsNullOrWhiteSpace(request.ModelEvidence) ? PrimaryModel : request.ModelEvidence,
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 },
            timings = new { predicted_per_second = 42.5, time_to_first_token_ms = 12 }
        });
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteEmptyStreamAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        await WriteSseAsync(context, new
        {
            id = "chatcmpl-scripted-empty",
            model = PrimaryModel,
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 7, completion_tokens = 0, total_tokens = 7 }
        });
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
    }

    private static async Task WriteSseAsync(HttpContext context, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static string SafeModelEvidence(string model)
    {
        var trimmed = model.Trim();
        if (trimmed.Length is > 0 and <= 96
            && trimmed.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.'))
        {
            return trimmed;
        }

        if (trimmed.Length == 0)
        {
            return "";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
        return $"model-sha256-{hash[..16]}";
    }

    private sealed class ScriptedProviderRequestTooLargeException : Exception
    {
    }
}
