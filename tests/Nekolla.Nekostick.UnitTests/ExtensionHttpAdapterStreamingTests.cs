using System.Text;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.Host;

public sealed class ExtensionHttpAdapterStreamingTests
{
    [Fact]
    public async Task CreateStreamingRequestAsyncReturnsNullWhenContentLengthExceedsLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        context.Request.ContentLength = 1025;
        context.Request.Body = new MemoryStream(new byte[1024]);

        var request = await ExtensionHttpAdapter.CreateStreamingRequestAsync(
            context,
            maxBodyBytes: 1024,
            readTimeout: TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(request);
    }

    [Fact]
    public async Task StreamingRequestGuardEnforcesMaxBytesAndAdapterReadFails()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        context.Request.ContentLength = 5;
        context.Request.Body = new OneByteAtATimeStream(new byte[10]);

        var request = await ExtensionHttpAdapter.CreateStreamingRequestAsync(
            context,
            maxBodyBytes: 5,
            readTimeout: TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        var body = request!.BodyStream;

        var buffer = new byte[16];
        var total = 0;
        int read;
#pragma warning disable CA2022
        while ((read = await body.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken)) > 0)
#pragma warning restore CA2022
        {
            total += read;
            if (total >= 5)
            {
                break;
            }
        }

        Assert.Equal(5, total);

        await Assert.ThrowsAsync<ExtensionRequestBodyLimitExceededException>(
            async () =>
            {
#pragma warning disable CA2022
                await body.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
#pragma warning restore CA2022
            });

        await body.DisposeAsync();
    }

    [Fact]
    public async Task StreamingRequestGuardEnforcesReadTimeout()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        context.Request.Body = new SlowStream(tcs.Task);

        var request = await ExtensionHttpAdapter.CreateStreamingRequestAsync(
            context,
            maxBodyBytes: 1024,
            readTimeout: TimeSpan.FromMilliseconds(50),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        var body = request!.BodyStream;

        await Assert.ThrowsAsync<ExtensionRequestReadTimeoutException>(
            async () =>
            {
                var buffer = new byte[16];
#pragma warning disable CA2022
                await body.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
#pragma warning restore CA2022
            });

        tcs.TrySetResult(0);
        await body.DisposeAsync();
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncCopiesFromCurrentPositionAndDisposesStream()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var bodyText = "hello streaming";
        var body = new MemoryStream(Encoding.UTF8.GetBytes(bodyText));

        var response = new ExtensionStreamingResponse(
            200,
            PlainTextHeaders,
            body);

        var written = await ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            TestContext.Current.CancellationToken);

        Assert.True(written);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.Headers["Content-Type"].ToString());
        context.Response.Body.Position = 0;
        using (var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true))
        {
            var copied = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal(bodyText, copied);
        }

        Assert.Throws<ObjectDisposedException>(() => body.Length);
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncReadsEmptyBodyWhenResponseStreamAtEnd()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var body = new MemoryStream(Encoding.UTF8.GetBytes("ignored"));
        body.Position = body.Length;

        var response = new ExtensionStreamingResponse(
            204,
            PlainTextHeaders,
            body);

        var written = await ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            TestContext.Current.CancellationToken);

        Assert.True(written);
        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncSetsHeadersBeforeCopySoFailureReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var body = new ThrowingReadStream();

        var response = new ExtensionStreamingResponse(
            418,
            CustomHeaders,
            body);

        var written = await ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            TestContext.Current.CancellationToken);

        Assert.False(written);
        Assert.Equal(418, context.Response.StatusCode);
        Assert.Equal("before-commit", context.Response.Headers["X-Custom"].ToString());
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncHonorsCancellationDuringCopy()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var body = new CancellationTokenStream(cts.Token);

        var response = new ExtensionStreamingResponse(
            200,
            Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
            body);

        cts.Cancel();
        var written = await ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            cts.Token);

        Assert.False(written);
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncCommitsHeadersBeforeAnyBodyBytes()
    {
        var context = new DefaultHttpContext();
        var clientBody = new RecordingResponseBody();
        context.Response.Body = clientBody;
        var producer = new GatedFrameStream(new[] { Encoding.UTF8.GetBytes("data: first\n\n") });

        var response = new ExtensionStreamingResponse(200, SseHeaders, producer);

        var writeTask = ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            TestContext.Current.CancellationToken).AsTask();

        // The producer is still blocked on its first gate, so no body byte can exist yet:
        // status, headers, and the commit flush must already be observable by the client.
        await clientBody.HeadersCommitted.WaitAsync(LivenessTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.Headers["Content-Type"].ToString());
        Assert.Equal(0, clientBody.BytesWritten);
        Assert.False(writeTask.IsCompleted);

        producer.ReleaseAll();
        Assert.True(await writeTask);
        Assert.Equal("data: first\n\n", Encoding.UTF8.GetString(clientBody.Snapshot()));
    }

    [Fact]
    public async Task WriteStreamingResponseAsyncDeliversFramesBeforeProducerCompletes()
    {
        var context = new DefaultHttpContext();
        var clientBody = new RecordingResponseBody();
        context.Response.Body = clientBody;
        var firstFrame = Encoding.UTF8.GetBytes("data: first\n\n");
        var secondFrame = Encoding.UTF8.GetBytes("data: second\n\n");
        var producer = new GatedFrameStream(new[] { firstFrame, secondFrame });

        var response = new ExtensionStreamingResponse(200, SseHeaders, producer);

        var writeTask = ExtensionHttpAdapter.WriteStreamingResponseAsync(
            context,
            response,
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(writeTask.IsCompleted);
        Assert.Equal(0, clientBody.BytesWritten);

        // Only the first frame is released: it must reach the client while the producer
        // still blocks on the second frame, so delivery does not wait for stream end.
        producer.Release(0);
        await clientBody.FirstFrameFlushed.WaitAsync(LivenessTimeout, TestContext.Current.CancellationToken);
        Assert.False(writeTask.IsCompleted);
        Assert.Equal(firstFrame, clientBody.Snapshot());

        producer.Release(1);
        Assert.True(await writeTask);

        var expected = new byte[firstFrame.Length + secondFrame.Length];
        firstFrame.CopyTo(expected, 0);
        secondFrame.CopyTo(expected, firstFrame.Length);
        Assert.Equal(expected, clientBody.Snapshot());
    }

    private static readonly KeyValuePair<string, IEnumerable<string>>[] PlainTextHeaders =
    {
        new("Content-Type", new[] { "text/plain" })
    };

    private static readonly KeyValuePair<string, IEnumerable<string>>[] CustomHeaders =
    {
        new("X-Custom", new[] { "before-commit" })
    };

    private static readonly KeyValuePair<string, IEnumerable<string>>[] SseHeaders =
    {
        new("Content-Type", new[] { "text/event-stream" })
    };

    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(10);

    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public OneByteAtATimeStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = _data[_position];
            _position++;
            return ValueTask.FromResult(1);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SlowStream : Stream
    {
        private readonly Task _delay;

        public SlowStream(Task delay) => _delay = delay;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _delay.WaitAsync(cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new IOException("read failed");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationTokenStream : Stream
    {
        private readonly CancellationToken _token;

        public CancellationTokenStream(CancellationToken token) => _token = token;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            _token.ThrowIfCancellationRequested();
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Emits each frame only once its gate is released, letting tests observe earlier frames
    // while later frames are still blocked inside the producer.
    private sealed class GatedFrameStream : Stream
    {
        private readonly byte[][] _frames;
        private readonly TaskCompletionSource[] _gates;
        private int _frameIndex;
        private int _frameOffset;

        public GatedFrameStream(byte[][] frames)
        {
            _frames = frames;
            _gates = frames
                .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        }

        public void Release(int frameIndex) => _gates[frameIndex].TrySetResult();

        public void ReleaseAll()
        {
            foreach (var gate in _gates)
            {
                gate.TrySetResult();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var index = _frameIndex;
            if (index >= _frames.Length)
            {
                return 0;
            }

            await _gates[index].Task.WaitAsync(cancellationToken);
            var frame = _frames[index];
            var count = Math.Min(frame.Length - _frameOffset, buffer.Length);
            frame.AsSpan(_frameOffset, count).CopyTo(buffer.Span);
            if (_frameOffset + count == frame.Length)
            {
                _frameIndex = index + 1;
                _frameOffset = 0;
            }
            else
            {
                _frameOffset += count;
            }

            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Captures what the client-side sink observes and signals when headers are committed or
    // body bytes are delivered, independent of the adapter's internal buffer sizes.
    private sealed class RecordingResponseBody : Stream
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _headersCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstFrameFlushed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _buffer = new byte[256];
        private int _length;

        public Task HeadersCommitted => _headersCommitted.Task;
        public Task FirstFrameFlushed => _firstFrameFlushed.Task;

        public int BytesWritten
        {
            get
            {
                lock (_sync)
                {
                    return _length;
                }
            }
        }

        public byte[] Snapshot()
        {
            lock (_sync)
            {
                return _buffer.AsSpan(0, _length).ToArray();
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_length == 0)
                {
                    // A flush with no body byte produced yet is the header commit.
                    _headersCommitted.TrySetResult();
                }
                else
                {
                    _firstFrameFlushed.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_length + buffer.Length > _buffer.Length)
                {
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + buffer.Length));
                }

                buffer.Span.CopyTo(_buffer.AsSpan(_length));
                _length += buffer.Length;
            }

            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
