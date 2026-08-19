using System.Buffers;

namespace Nekolla.Nekostick.Host;

/// <summary>Applies one request's body-size and read-deadline contract to every body read.</summary>
internal sealed class HostRequestBodyGuard : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly DateTimeOffset _deadline;
    private readonly CancellationToken _requestAborted;
    private readonly HostRequestAdmissionContext _admissionContext;
    private readonly IHostRequestAdmissionClock _clock;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private long _readBytes;
    private int _completed;
    private int _disposed;

    internal HostRequestBodyGuard(
        Stream inner,
        long maximumBytes,
        TimeSpan readTimeout,
        HostRequestAdmissionContext admissionContext,
        IHostRequestAdmissionClock clock,
        CancellationToken requestAborted)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);

        _maximumBytes = maximumBytes;
        _deadline = (clock ?? throw new ArgumentNullException(nameof(clock))).UtcNow + readTimeout;
        _clock = clock;
        _requestAborted = requestAborted;
        _admissionContext = admissionContext ?? throw new ArgumentNullException(nameof(admissionContext));
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = ReadAsync(rented.AsMemory(0, buffer.Length), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _completed) != 0)
        {
            return 0;
        }

        var remaining = _deadline - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ThrowReadTimeout();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _requestAborted,
            _disposeCancellation.Token);
        using var timeoutCancellation = new CancellationTokenSource();
        var readTask = _inner.ReadAsync(buffer, linked.Token).AsTask();
        var timeoutTask = _clock.DelayAsync(remaining, timeoutCancellation.Token).AsTask();
        try
        {
            if (await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false) != readTask)
            {
                linked.Cancel();
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The deadline owns this cancellation; surface the configured request-read result.
                }

                ThrowReadTimeout();
            }

            int read;
            try
            {
                read = await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _requestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
                throw;
            }

            if (read == 0)
            {
                Interlocked.Exchange(ref _completed, 1);
                return 0;
            }

            var total = checked(Interlocked.Add(ref _readBytes, read));
            if (total > _maximumBytes)
            {
                var failure = new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestBody);
                _admissionContext.RecordFailure(failure);
                throw new HostRequestBodyLimitExceededException();
            }

            return read;
        }
        finally
        {
            timeoutCancellation.Cancel();
            if (!readTask.IsCompleted)
            {
                linked.Cancel();
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ThrowReadTimeout()
    {
        var failure = new HostRequestAdmissionFailure(HostRequestAdmissionFailureKind.RequestReadTimeout);
        _admissionContext.RecordFailure(failure);
        throw new HostRequestReadTimeoutException();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

internal sealed class HostRequestBodyLimitExceededException : IOException
{
}

internal sealed class HostRequestReadTimeoutException : OperationCanceledException
{
}
