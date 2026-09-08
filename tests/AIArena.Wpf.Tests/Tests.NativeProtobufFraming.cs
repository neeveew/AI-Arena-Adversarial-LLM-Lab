using System.IO;
using AIArena.Wpf.Services;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

internal static partial class Program
{
    static void NativeProtobufFramingWritesCompatibleRequests()
    {
        using var output = new MemoryStream();
        NativeProtobufFraming.WriteAsync(output, new StringValue { Value = "hé" }, CancellationToken.None)
            .GetAwaiter().GetResult();
        // APB1, five-byte payload length in little endian, then StringValue
        // field 1 with three UTF-8 bytes. This fixture is independent of framing code.
        byte[] expected = [0x41, 0x50, 0x42, 0x31, 0x05, 0x00, 0x00, 0x00, 0x0A, 0x03, 0x68, 0xC3, 0xA9];
        Require(output.ToArray().SequenceEqual(expected),
            "Native requests must match the APB1, little-endian length, and protobuf wire contract exactly");

        foreach (var invalid in new[] { new StringValue(), new StringValue { Value = new string('x', 256 * 1024) } })
        {
            using var rejectedOutput = new MemoryStream();
            var error = NativeFramingExpect<NativeControlException>(() =>
                NativeProtobufFraming.WriteAsync(rejectedOutput, invalid, CancellationToken.None).GetAwaiter().GetResult());
            Require(!error.OutcomeUnknown && rejectedOutput.Length == 0,
                "Empty or oversized requests must fail before writing any bytes and report a known outcome");
        }

        using var boundaryOutput = new MemoryStream();
        var boundary = new StringValue { Value = new string('x', 256 * 1024 - 4) };
        NativeProtobufFraming.WriteAsync(boundaryOutput, boundary, CancellationToken.None).GetAwaiter().GetResult();
        Require(boundaryOutput.Length == 256 * 1024 + 8,
            "A protobuf request exactly at the 256 KiB payload limit must remain valid");
    }

    static void NativeProtobufFramingReadsFragmentedAdditiveResponses()
    {
        // StringValue("ok") plus future field 2 = 150. A second frame proves
        // one ReadAsync consumes exactly one response, even across short reads.
        byte[] first = [0x41, 0x50, 0x42, 0x31, 0x07, 0x00, 0x00, 0x00, 0x0A, 0x02, 0x6F, 0x6B, 0x10, 0x96, 0x01];
        byte[] second = [0x41, 0x50, 0x42, 0x31, 0x03, 0x00, 0x00, 0x00, 0x0A, 0x01, 0x78];
        using var fragmented = new NativeFragmentedReadStream([.. first, .. second]);
        var result = NativeProtobufFraming.ReadAsync(fragmented, StringValue.Parser, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(result.Value == "ok" && result.CalculateSize() == 7,
            "Fragmented protobuf responses must decode known content while retaining additive unknown fields");
        Require(fragmented.Position == first.Length && fragmented.ReadCalls > 2,
            "A response reader must handle fragmented headers and bodies without consuming the following frame");
        var next = NativeProtobufFraming.ReadAsync(fragmented, StringValue.Parser, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(next.Value == "x", "A subsequent APB1 response must remain independently readable");

        // Response allowance is 1 MiB, independently of the smaller request cap.
        var boundary = new StringValue { Value = new string('r', 1024 * 1024 - 4) }.ToByteArray();
        byte[] boundaryFrame = [0x41, 0x50, 0x42, 0x31, 0x00, 0x00, 0x10, 0x00, .. boundary];
        using var boundaryInput = new MemoryStream(boundaryFrame, writable: false);
        var boundaryResult = NativeProtobufFraming.ReadAsync(boundaryInput, StringValue.Parser, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(boundaryResult.Value.Length == 1024 * 1024 - 4,
            "A response exactly at 1 MiB must not be rejected by the smaller request limit");
    }

    static void NativeProtobufFramingRejectsInvalidResponses()
    {
        (string Name, byte[] Frame)[] invalidFrames =
        [
            ("legacy or invalid magic", [0x4A, 0x53, 0x4F, 0x4E, 0x04, 0x00, 0x00, 0x00]),
            ("empty payload", [0x41, 0x50, 0x42, 0x31, 0x00, 0x00, 0x00, 0x00]),
            ("payload above 1 MiB", [0x41, 0x50, 0x42, 0x31, 0x01, 0x00, 0x10, 0x00]),
            ("invalid protobuf length", [0x41, 0x50, 0x42, 0x31, 0x02, 0x00, 0x00, 0x00, 0x0A, 0x02])
        ];
        foreach (var (name, frame) in invalidFrames)
        {
            using var input = new MemoryStream(frame, writable: false);
            var error = NativeFramingExpect<NativeControlException>(() =>
                NativeProtobufFraming.ReadAsync(input, StringValue.Parser, CancellationToken.None).GetAwaiter().GetResult());
            Require(error.OutcomeUnknown, $"A rejected {name} response must preserve uncertainty about native execution");
        }

        byte[][] truncatedFrames =
        [
            [0x41, 0x50, 0x42],
            [0x41, 0x50, 0x42, 0x31, 0x04, 0x00, 0x00, 0x00, 0x0A, 0x02, 0x6F]
        ];
        foreach (var frame in truncatedFrames)
        {
            using var input = new NativeFragmentedReadStream(frame);
            NativeFramingExpect<EndOfStreamException>(() =>
                NativeProtobufFraming.ReadAsync(input, StringValue.Parser, CancellationToken.None).GetAwaiter().GetResult());
        }
    }

    static void NativeProtobufFramingPropagatesCallerCancellation()
    {
        foreach (var reading in new[] { true, false })
        {
            using var blocked = new NativeCancellationStream();
            using var cancellation = new CancellationTokenSource();
            Task pending = reading
                ? NativeProtobufFraming.ReadAsync(blocked, StringValue.Parser, cancellation.Token)
                : NativeProtobufFraming.WriteAsync(blocked, new StringValue { Value = "request" }, cancellation.Token);
            blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            cancellation.Cancel();
            var error = NativeFramingExpect<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
            Require(error.CancellationToken == cancellation.Token,
                $"A blocked native {(reading ? "read" : "write")} must propagate its caller cancellation token");
        }
    }

    private static TException NativeFramingExpect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Native framing should have rejected the operation with {typeof(TException).Name}.");
    }

    private sealed class NativeFragmentedReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int ReadCalls { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
        }
    }

    private sealed class NativeCancellationStream : Stream
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
