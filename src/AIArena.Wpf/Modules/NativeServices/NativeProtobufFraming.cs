using System.Buffers.Binary;
using System.IO;
using Google.Protobuf;

namespace AIArena.Wpf.Services;

/// <summary>One APB1 frame per request/response. Bounds are checked before allocation.</summary>
internal static class NativeProtobufFraming
{
    internal const int MaximumRequestBytes = 256 * 1024;
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "APB1"u8;

    internal static async Task WriteAsync(Stream stream, IMessage message, CancellationToken cancellationToken)
    {
        var size = message.CalculateSize();
        if (size <= 0 || size > MaximumRequestBytes)
            throw new NativeControlException("The native request exceeded the supported size.", outcomeUnknown: false);

        var payload = message.ToByteArray();
        var header = new byte[8];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, MessageParser<T> parser, CancellationToken cancellationToken)
        where T : IMessage<T>
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new NativeControlException("The native app returned an unsupported frame.");

        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (size == 0 || size > MaximumResponseBytes)
            throw new NativeControlException("The native response exceeded the supported size.");

        var payload = new byte[(int)size];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            throw new NativeControlException("The native app returned an unreadable response.");
        }
    }
}

internal sealed class NativeControlException : Exception
{
    public NativeControlException(string message, bool outcomeUnknown = true) : base(message)
    {
        OutcomeUnknown = outcomeUnknown;
    }

    public bool OutcomeUnknown { get; }
}
