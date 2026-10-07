using System.Text;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Streams three ordered SSE frames after the handler has already returned.</summary>
public sealed class FixtureSseStreamingHandler : IExtensionStreamingHandler
{
    private const string EventPrefix = "fixture-sse-";
    private const int EventCount = 3;
    private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public string HandlerId { get; }

    /// <summary>Creates an SSE streaming handler over one stable handler identifier.</summary>
    /// <param name="handlerId">The stable handler identifier registered by the fixture.</param>
    public FixtureSseStreamingHandler(string handlerId)
    {
        HandlerId = handlerId;
    }

    /// <inheritdoc />
    public ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        var body = new FixtureSseBodyStream();
        _ = ProduceAsync(body, cancellationToken);
        return ValueTask.FromResult(new ExtensionStreamingResponse(
            200,
            new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["text/event-stream"]),
                new KeyValuePair<string, IEnumerable<string>>("Cache-Control", ["no-cache"])
            },
            body));
    }

    private static async Task ProduceAsync(FixtureSseBodyStream body, CancellationToken cancellationToken)
    {
        // Yield first so the handler return is never delayed by frame production.
        await Task.Yield();
        try
        {
            for (var index = 1; index <= EventCount; index++)
            {
                if (index > 1)
                {
                    await Task.Delay(EventInterval, cancellationToken).ConfigureAwait(false);
                }

                await body.PublishAsync(
                        Encoding.UTF8.GetBytes($"data: {EventPrefix}{index}\n\n"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host stopped reading before the remaining frames were produced.
        }
        catch (ChannelClosedException)
        {
            // The host disposed the response stream before the producer finished.
        }
        finally
        {
            body.Complete();
        }
    }
}

/// <summary>Provides a single-writer, single-reader asynchronous frame stream backed by a channel.</summary>
internal sealed class FixtureSseBodyStream : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    private byte[]? _pending;
    private int _pendingOffset;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The fixture SSE body has no fixed length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("The fixture SSE body is not seekable.");
        set => throw new NotSupportedException("The fixture SSE body is not seekable.");
    }

    /// <summary>Publishes one frame to the next reader.</summary>
    internal ValueTask PublishAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(frame.ToArray(), cancellationToken);

    /// <summary>Completes the stream so the reader observes the end after the produced frames.</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_pending is { } pending && _pendingOffset < pending.Length)
            {
                var count = Math.Min(buffer.Length, pending.Length - _pendingOffset);
                pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
                _pendingOffset += count;
                return count;
            }

            if (_channel.Reader.TryRead(out var next))
            {
                _pending = next;
                _pendingOffset = 0;
                continue;
            }

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("The fixture SSE body stream is not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("The fixture SSE body stream has no fixed length.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The fixture SSE body stream is written through the fixture producer.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
        }

        base.Dispose(disposing);
    }
}
