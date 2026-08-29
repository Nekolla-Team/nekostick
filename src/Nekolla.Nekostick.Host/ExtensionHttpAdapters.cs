using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Converts ASP.NET requests and extension responses at the stable ABI boundary.</summary>
internal static class ExtensionHttpAdapter
{
    internal static async ValueTask<ExtensionHandlerRequest?> CreateRequestAsync(
        HttpContext context,
        long maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maxBodyBytes <= 0)
        {
            return null;
        }

        var headers = context.Request.Headers
            .Select(static pair => new KeyValuePair<string, IEnumerable<string>>(
                pair.Key,
                pair.Value.ToArray().Select(static value => value!)));
        byte[] body;
        try
        {
            if (context.Request.ContentLength is long contentLength && contentLength > maxBodyBytes)
            {
                return null;
            }

            context.Request.EnableBuffering();
            var stream = context.Request.Body;
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            await using var buffer = new MemoryStream();
            var temporary = new byte[16 * 1024];
            var total = 0L;
            while (true)
            {
                var read = await stream.ReadAsync(temporary.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBodyBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(temporary.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            body = buffer.ToArray();
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var path = context.Request.PathBase.Add(context.Request.Path).Value;
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        path += context.Request.QueryString.Value;
        try
        {
            return new ExtensionHandlerRequest(
                context.Request.Method,
                path,
                headers,
                body,
                context.Request.IsHttps);
        }
        catch
        {
            return null;
        }
    }

    internal static async ValueTask<bool> WriteResponseAsync(
        HttpContext context,
        ExtensionHandlerResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return false;
        }

        try
        {
            context.Response.Headers.Clear();
            context.Response.StatusCode = response.StatusCode;
            foreach (var pair in response.Headers)
            {
                context.Response.Headers.Append(
                    pair.Key,
                    new StringValues(pair.Value.ToArray()));
            }

            if (!response.Body.IsDefaultOrEmpty)
            {
                await context.Response.Body
                    .WriteAsync(response.Body.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
        catch
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
    }

    internal static async ValueTask<ExtensionStreamingRequest?> CreateStreamingRequestAsync(
        HttpContext context,
        long maxBodyBytes,
        TimeSpan readTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maxBodyBytes <= 0)
        {
            return null;
        }

        var headers = context.Request.Headers
            .Select(static pair => new KeyValuePair<string, IEnumerable<string>>(
                pair.Key,
                pair.Value.ToArray().Select(static value => value!)));
        Stream bodyStream;
        try
        {
            if (context.Request.ContentLength is long contentLength && contentLength > maxBodyBytes)
            {
                return null;
            }

            bodyStream = new HostExtensionRequestBodyGuard(
                context.Request.Body,
                maxBodyBytes,
                readTimeout,
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var path = context.Request.PathBase.Add(context.Request.Path).Value;
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        path += context.Request.QueryString.Value;
        try
        {
            return new ExtensionStreamingRequest(
                context.Request.Method,
                path,
                headers,
                bodyStream,
                context.Request.IsHttps);
        }
        catch
        {
            await bodyStream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    internal static async ValueTask<bool> WriteStreamingResponseAsync(
        HttpContext context,
        ExtensionStreamingResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return false;
        }

        var bodyStream = response.BodyStream;
        var headersSet = false;
        try
        {
            context.Response.Headers.Clear();
            context.Response.StatusCode = response.StatusCode;
            foreach (var pair in response.Headers)
            {
                context.Response.Headers.Append(
                    pair.Key,
                    new StringValues(pair.Value.ToArray()));
            }

            headersSet = true;

            if (bodyStream is null)
            {
                return true;
            }

            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await bodyStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (headersSet)
            {
                context.Abort();
            }

            return false;
        }
        catch
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
        finally
        {
            bodyStream?.Dispose();
        }
    }
}

/// <summary>Applies the route/global body bound and read deadline to an extension streaming request body.</summary>
internal sealed class HostExtensionRequestBodyGuard : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly DateTimeOffset _deadline;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private long _readBytes;
    private int _completed;
    private int _disposed;

    internal HostExtensionRequestBodyGuard(
        Stream inner,
        long maximumBytes,
        TimeSpan readTimeout,
        CancellationToken requestAborted)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);

        _maximumBytes = maximumBytes;
        _deadline = DateTimeOffset.UtcNow + readTimeout;
        _requestAborted = requestAborted;
    }

    private readonly CancellationToken _requestAborted;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _completed) != 0)
        {
            return 0;
        }

        var remaining = _deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new ExtensionRequestReadTimeoutException();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _requestAborted,
            _disposeCancellation.Token);
        using var timeoutCancellation = new CancellationTokenSource();
        var readTask = _inner.ReadAsync(buffer, linked.Token).AsTask();
        var timeoutTask = Task.Delay(remaining, timeoutCancellation.Token);
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
                    // The deadline owns this cancellation; surface the configured read-timeout result.
                }

                throw new ExtensionRequestReadTimeoutException();
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
                throw new ExtensionRequestBodyLimitExceededException();
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

/// <summary>Invokes the sole staged extension fallback for safe 404 candidates.</summary>
internal sealed class ExtensionRouteFallbackDispatcher : ILeasedRouteFallbackDispatcher
{
    public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason) =>
        ValueTask.FromResult(false);

    public async ValueTask<bool> TryDispatchAsync(
        HttpContext context,
        RouteNoMatchReason reason,
        HostRoutingSnapshotLease publicationLease)
    {
        var fallbackReason = reason switch
        {
            RouteNoMatchReason.NoRoute => ExtensionFallbackReason.NoRoute,
            RouteNoMatchReason.HostMismatch => ExtensionFallbackReason.HostMismatch,
            RouteNoMatchReason.MethodMismatch => ExtensionFallbackReason.MethodMismatch,
            RouteNoMatchReason.StaticNotFound => ExtensionFallbackReason.StaticNotFound,
            RouteNoMatchReason.StaticIndexMissing => ExtensionFallbackReason.StaticIndexMissing,
            _ => (ExtensionFallbackReason?)null
        };
        if (fallbackReason is null || publicationLease.DispatchLease is null)
        {
            return false;
        }

        var request = await ExtensionHttpAdapter.CreateRequestAsync(
                context,
                publicationLease.Snapshot.Configuration.GlobalSettings.MaxRequestBodyBytes,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (request is null)
        {
            return false;
        }

        var result = await publicationLease.DispatchLease
            .HandleFallbackAsync(request, fallbackReason.Value, context.RequestAborted)
            .ConfigureAwait(false);
        if (result.State != ExtensionInvocationState.Handled || result.Response is null)
        {
            return false;
        }

        return await ExtensionHttpAdapter.WriteResponseAsync(
                context,
                result.Response,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
