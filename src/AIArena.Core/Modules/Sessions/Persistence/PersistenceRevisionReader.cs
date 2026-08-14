using System.Buffers;
using System.Text.Json;

namespace AIArena.Core.Persistence;

/// <summary>
/// Reads the optimistic-concurrency revision without materializing the snapshot.
/// The complete document is still validated before the revision is trusted.
/// </summary>
internal static class PersistenceRevisionReader
{
    internal const int InitialBufferBytes = 64 * 1024;
    // Snapshot messages can legitimately contain multi-megabyte text (the
    // persistence privacy suite exercises 4 MiB). Keep that path streamed,
    // but cap exceptional single-token retention before using the legacy
    // seekable fallback.
    internal const int MaximumBufferedTokenBytes = 8 * 1024 * 1024;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    internal static async ValueTask<long> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The persistence revision stream must be readable.", nameof(stream));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var initialPosition = stream.CanSeek ? stream.Position : -1;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        var logicalCapacity = Math.Min(buffer.Length, InitialBufferBytes);
        var buffered = 0;
        var preambleResolved = false;
        var finalBlock = false;
        var state = new JsonReaderState(ReaderOptions);
        var sawRootToken = false;
        var rootIsObject = false;
        var awaitingRevisionValue = false;
        long revision = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!finalBlock && buffered < logicalCapacity)
                {
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(buffered, logicalCapacity - buffered), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        finalBlock = true;
                    }
                    else
                    {
                        buffered += read;
                    }
                }

                if (!preambleResolved)
                {
                    if (buffered < 3 && !finalBlock)
                    {
                        continue;
                    }

                    if (buffered >= 3
                        && buffer[0] == 0xEF
                        && buffer[1] == 0xBB
                        && buffer[2] == 0xBF)
                    {
                        buffer.AsSpan(3, buffered - 3).CopyTo(buffer);
                        buffered -= 3;
                    }

                    preambleResolved = true;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), finalBlock, state);
                var cancellationProbe = 0;
                while (reader.Read())
                {
                    if ((++cancellationProbe & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (!sawRootToken)
                    {
                        sawRootToken = true;
                        rootIsObject = reader.TokenType == JsonTokenType.StartObject;
                    }

                    if (rootIsObject
                        && reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 1)
                    {
                        awaitingRevisionValue = reader.ValueTextEquals("persistence_revision"u8);
                        continue;
                    }

                    if (awaitingRevisionValue)
                    {
                        revision = reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var parsedRevision)
                            ? Math.Max(0, parsedRevision)
                            : 0;
                        awaitingRevisionValue = false;
                    }
                }

                var consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                if (consumed > 0)
                {
                    buffer.AsSpan(consumed, buffered - consumed).CopyTo(buffer);
                    buffered -= consumed;
                }

                if (finalBlock)
                {
                    if (!sawRootToken)
                    {
                        throw new JsonException("The persistence snapshot contains no JSON value.");
                    }

                    if (!rootIsObject)
                    {
                        throw new InvalidOperationException("The persistence snapshot root must be a JSON object.");
                    }

                    return revision;
                }

                if (buffered < logicalCapacity)
                {
                    continue;
                }

                if (logicalCapacity >= MaximumBufferedTokenBytes)
                {
                    if (initialPosition < 0)
                    {
                        throw new JsonException(
                            "A non-seekable persistence stream contains a JSON token larger than the bounded scan buffer.");
                    }

                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = null!;
                    stream.Position = initialPosition;
                    using var document = await JsonDocument
                        .ParseAsync(stream, DocumentOptions, cancellationToken)
                        .ConfigureAwait(false);
                    return RevisionFromDocument(document);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var nextCapacity = Math.Min(logicalCapacity * 2, MaximumBufferedTokenBytes);
                var larger = ArrayPool<byte>.Shared.Rent(nextCapacity);
                buffer.AsSpan(0, buffered).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                buffer = larger;
                logicalCapacity = nextCapacity;
            }
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    private static long RevisionFromDocument(JsonDocument document) =>
        document.RootElement.TryGetProperty("persistence_revision", out var revision)
        && revision.ValueKind == JsonValueKind.Number
        && revision.TryGetInt64(out var value)
            ? Math.Max(0, value)
            : 0;
}
