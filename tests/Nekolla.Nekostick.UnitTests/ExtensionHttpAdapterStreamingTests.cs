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

    private static readonly KeyValuePair<string, IEnumerable<string>>[] PlainTextHeaders =
    {
        new("Content-Type", new[] { "text/plain" })
    };

    private static readonly KeyValuePair<string, IEnumerable<string>>[] CustomHeaders =
    {
        new("X-Custom", new[] { "before-commit" })
    };

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
}
