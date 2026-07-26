using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


sealed class DisposalTrackingProcess : System.Diagnostics.Process
{
    public bool DisposeCalled { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCalled = true;
        }

        base.Dispose(disposing);
    }
}

sealed class CountingReadStream(int length) : Stream
{
    private int remaining = length;

    public int BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = Math.Min(count, remaining);
        Array.Fill(buffer, (byte)'x', offset, read);
        remaining -= read;
        BytesRead += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public List<string> AuthorizationHeaders { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null)
        {
            Requests.Add(request.RequestUri);
        }

        AuthorizationHeaders.Add(request.Headers.TryGetValues("Authorization", out var values)
            ? string.Join(",", values)
            : "");

        Bodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return respond(request);
    }
}

sealed class CancellationBlockingHttpMessageHandler : HttpMessageHandler
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Canceled provider request unexpectedly resumed.");
    }
}

internal sealed class FixedCollaborateModelClient(string text) : IModelProviderClient
{
    public int CompleteCalls { get; private set; }

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        CompleteCalls++;
        return Task.FromResult(new ModelCompletionResult(
            true,
            config.BaseUrl,
            config.Model,
            text,
            "",
            1,
            0,
            0,
            0,
            "",
            DateTimeOffset.Now));
    }
}

internal sealed class SequentialAgentModelClient(params string[] texts) : IModelProviderClient
{
    private int index;

    public int CompleteCalls { get; private set; }
    public List<ModelProviderConfig> CompletedConfigs { get; } = [];
    public List<IReadOnlyList<ModelChatMessage>> CompletedMessages { get; } = [];

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        CompletedConfigs.Add(config);
        CompletedMessages.Add(messages);
        var text = texts.Length == 0
            ? ""
            : texts[Math.Min(index, texts.Length - 1)];
        index++;
        CompleteCalls++;
        return Task.FromResult(new ModelCompletionResult(
            true,
            config.BaseUrl,
            config.Model,
            text,
            "",
            1,
            0,
            0,
            text.Length / 4,
            "",
            DateTimeOffset.Now));
    }
}

internal sealed class DelayedSequentialAgentModelClient(TimeSpan delay, params string[] texts) : IModelProviderClient
{
    private int index;

    public int CompleteCalls { get; private set; }

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public async Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        var text = texts.Length == 0
            ? ""
            : texts[Math.Min(index, texts.Length - 1)];
        index++;
        CompleteCalls++;
        return new ModelCompletionResult(
            true,
            config.BaseUrl,
            config.Model,
            text,
            "",
            1,
            0,
            0,
            text.Length / 4,
            "",
            DateTimeOffset.Now);
    }
}

internal sealed class CancellationBlockingModelClient : IModelProviderClient
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public async Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable model continuation");
    }
}

internal sealed class ThrowingCancellationModelClient : IModelProviderClient
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public async Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        using var throwingRegistration = cancellationToken.Register(
            () => throw new InvalidOperationException("simulated cancellation callback failure"));
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable model continuation");
    }
}

internal sealed class ThrowingCollaborateModelClient(string message) : IModelProviderClient
{
    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class ModelMapCollaborateModelClient(IReadOnlyDictionary<string, ModelCompletionResult> responses) : IModelProviderClient
{
    public List<string> CompletedModels { get; } = [];

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, responses.Keys.ToArray(), "", DateTimeOffset.Now));
    }

    public Task<ModelCompletionResult> CompleteChatAsync(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        CompletedModels.Add(config.Model);
        return responses.TryGetValue(config.Model, out var result)
            ? Task.FromResult(result)
            : Task.FromResult(new ModelCompletionResult(
                false,
                config.BaseUrl,
                config.Model,
                "",
                "",
                0,
                0,
                0,
                0,
                $"Unexpected model: {config.Model}",
                DateTimeOffset.Now));
    }
}

internal class RecordingCollaborateHistoryStore : CollaborateHistoryStore
{
    public int SaveCalls { get; protected set; }

    public IReadOnlyList<CollaborateHistoryConversation> LastConversations { get; protected set; } = [];

    public IReadOnlyList<CollaborateHistoryConversation> LoadConversations { get; set; } = [];

    public RecordingCollaborateHistoryStore()
        : base(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json"))
    {
    }

    public override IReadOnlyList<CollaborateHistoryConversation> Load()
    {
        return LoadConversations;
    }

    public override void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
    {
        SaveCalls++;
        LastConversations = conversations.ToArray();
    }
}

internal sealed class ThrowingCollaborateHistoryStore(string message) : RecordingCollaborateHistoryStore
{
    public override void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
    {
        SaveCalls++;
        LastConversations = conversations.ToArray();
        throw new IOException(message);
    }
}
