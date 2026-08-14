using System.Text;
using AIArena.Wpf;

internal static partial class Program
{
    private static void ControlPlaneBufferedReadPreservesProtocol()
    {
        var empty = ReadControlPlaneLine(new CountingFragmentStream([]));
        Require(empty.Value.Length == 0, "an empty control-plane stream should remain an empty request");
        Require(empty.Stream.ReadCalls == 1, "an empty control-plane stream should require one EOF read");

        var fragmentedPayload = Encoding.UTF8.GetBytes($"{new string('f', 8192)}\n");
        var fragmented = ReadControlPlaneLine(new CountingFragmentStream(fragmentedPayload, maxChunkBytes: 17));
        Require(fragmented.Value.Length == 8192, "fragmented control-plane input should be reassembled without loss");
        Require(fragmented.Stream.ReadCalls == 482, "an 8 KiB request fragmented into 17-byte chunks should require exactly 482 buffered reads");

        const string multibyteBody = "prefix🙂漢字suffix";
        var multibytePayload = Encoding.UTF8.GetBytes($"pre\rfix🙂漢字suffix\r\n");
        var multibyte = ReadControlPlaneLine(new CountingFragmentStream(multibytePayload, maxChunkBytes: 2));
        Require(multibyte.Value.Equals(multibyteBody, StringComparison.Ordinal), "UTF-8 code points split across reads should decode exactly and CR bytes should retain legacy stripping semantics");

        var exactLimitPayload = GC.AllocateUninitializedArray<byte>(AIArenaControlPlaneProtocol.MaxRequestBytes + 1);
        exactLimitPayload.AsSpan(0, AIArenaControlPlaneProtocol.MaxRequestBytes).Fill((byte)'x');
        exactLimitPayload[^1] = (byte)'\n';
        var exactLimit = ReadControlPlaneLine(new CountingFragmentStream(exactLimitPayload));
        Require(exactLimit.Value.Length == AIArenaControlPlaneProtocol.MaxRequestBytes, "a request exactly at the byte cap should remain valid");
        Require(exactLimit.Stream.ReadCalls == 65, "a 256 KiB request plus newline should require exactly 65 buffered reads");

        var overLimitPayload = GC.AllocateUninitializedArray<byte>(AIArenaControlPlaneProtocol.MaxRequestBytes + 2);
        overLimitPayload.AsSpan(0, AIArenaControlPlaneProtocol.MaxRequestBytes + 1).Fill((byte)'x');
        overLimitPayload[^1] = (byte)'\n';
        var overLimitStream = new CountingFragmentStream(overLimitPayload);
        try
        {
            _ = AIArenaControlPlaneHost.ReadBoundedLineAsync(overLimitStream, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException("a request one byte over the cap should fail");
        }
        catch (InvalidDataException ex)
        {
            Require(ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase), "limit failures should retain the stable error message");
        }
        Require(overLimitStream.ReadCalls == 65, "a request one byte over the cap should remain bounded to 65 buffered reads through its newline");

        var malformed = ReadControlPlaneLine(new CountingFragmentStream([(byte)0xC3, (byte)'\n']));
        Require(malformed.Value.Equals("\uFFFD", StringComparison.Ordinal), "malformed UTF-8 should retain replacement-fallback decoding semantics");

        var trailing = ReadControlPlaneLine(new CountingFragmentStream(Encoding.UTF8.GetBytes("first\nsecond\n")));
        Require(trailing.Value.Equals("first", StringComparison.Ordinal), "one connection should consume only its first request line and discard trailing input");
        Require(trailing.Stream.ReadCalls == 1, "trailing input already delivered with the first line should not trigger another read");

        var eof = ReadControlPlaneLine(new CountingFragmentStream(Encoding.UTF8.GetBytes("without-newline")));
        Require(eof.Value.Equals("without-newline", StringComparison.Ordinal), "EOF should complete a request that has no newline");
        Require(eof.Stream.ReadCalls == 2, "a non-empty request without newline should perform one payload read and one EOF read");

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            RequireControlPlaneReadCancellation(alreadyCancelled.Token, "pre-cancelled control-plane reads should remain cancelled");
        }

        using (var timedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            RequireControlPlaneReadCancellation(timedCancellation.Token, "a blocked control-plane read should observe its timeout token");
        }

        Console.WriteLine(
            "CONTROL_PLANE_READ_RECEIPT "
            + $"buffer={AIArenaControlPlaneHost.RequestReadBufferBytes} "
            + $"fragmented_bytes={fragmentedPayload.Length} fragmented_reads={fragmented.Stream.ReadCalls} "
            + $"exact_limit_bytes={exactLimitPayload.Length} exact_limit_reads={exactLimit.Stream.ReadCalls}");
    }

    private static (string Value, CountingFragmentStream Stream) ReadControlPlaneLine(CountingFragmentStream stream)
    {
        var value = AIArenaControlPlaneHost.ReadBoundedLineAsync(stream, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return (value, stream);
    }

    private static string SendRawControlRequest(string pipeName, byte[] payload)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.None);
        pipe.Connect(5000);
        pipe.Write(payload);
        pipe.Flush();
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        return reader.ReadLine() ?? "";
    }

    private static void RequireControlPlaneReadCancellation(CancellationToken cancellationToken, string message)
    {
        using var stream = new CancellationOnlyReadStream();
        try
        {
            _ = AIArenaControlPlaneHost.ReadBoundedLineAsync(stream, cancellationToken)
                .GetAwaiter()
                .GetResult();
            throw new InvalidOperationException(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Require(stream.ReadCalls == 1, "a cancelled control-plane read should not retry the stream");
        }
    }

    private sealed class CountingFragmentStream(byte[] payload, int maxChunkBytes = int.MaxValue) : Stream
    {
        private int position;

        public int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadInto(buffer.AsSpan(offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return ValueTask.FromResult(ReadInto(buffer.Span));
        }

        private int ReadInto(Span<byte> destination)
        {
            var count = Math.Min(Math.Min(destination.Length, maxChunkBytes), payload.Length - position);
            if (count <= 0)
            {
                return 0;
            }

            payload.AsSpan(position, count).CopyTo(destination);
            position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationOnlyReadStream : Stream
    {
        public int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
