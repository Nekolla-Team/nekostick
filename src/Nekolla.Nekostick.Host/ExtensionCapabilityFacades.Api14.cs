using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

internal sealed class ExtensionServiceOutputFacade : IExtensionServiceOutputApi, IExtensionServiceOutputCleanup
{
    private readonly string _extensionId;
    private readonly HostRuntimeState _runtimeState;
    private readonly PosixProcessExecutor? _executor;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<IDisposable> _resources = [];
    private readonly List<IDisposable> _pendingDisposals = [];
    private bool _disposed;
    private Task? _disposeTask;

    internal ExtensionServiceOutputFacade(
        string extensionId,
        HostRuntimeState runtimeState,
        PosixProcessExecutor? executor,
        ILogger? logger = null)
    {
        _extensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _executor = executor;
        _logger = logger ?? NullLogger.Instance;
    }

    public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default)
    {
        if (!UuidV7.IsVersion7(serviceId))
        {
            return OpenFailure(Guid.CreateVersion7(), ExtensionServiceOutputCode.NotFound);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }

        if (!TryMapStream(stream, out var processStream))
        {
            return OpenFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }

        if (!IsConfigured(serviceId))
        {
            return OpenFailure(serviceId, ExtensionServiceOutputCode.NotFound);
        }

        if (IsDisposed())
        {
            return OpenFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }

        if (_executor is null)
        {
            return OpenFailure(serviceId, ExtensionServiceOutputCode.Unsupported);
        }

        try
        {
            if (!_executor.TryOpenOutputStream(serviceId, processStream, out var output) || output is null)
            {
                return OpenFailure(serviceId, ExtensionServiceOutputCode.NotRunning);
            }

            var trackedOutput = new TrackingStream(this, output);
            if (!TryTrack(trackedOutput))
            {
                trackedOutput.Dispose();
                return OpenFailure(serviceId, ExtensionServiceOutputCode.Failed);
            }

            return ValueTask.FromResult(new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                serviceId,
                trackedOutput));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(OpenStreamAsync), serviceId);
            return OpenFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }
    }

    public async ValueTask<ExtensionServiceOutputSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        IExtensionServiceOutputSink sink,
        CancellationToken cancellationToken = default)
    {
        if (!UuidV7.IsVersion7(serviceId))
        {
            return SubscriptionFailure(Guid.CreateVersion7(), ExtensionServiceOutputCode.NotFound);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sink is null || !TryMapStream(stream, out var processStream))
        {
            return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured(serviceId))
        {
            return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.NotFound);
        }

        if (IsDisposed())
        {
            return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }

        if (_executor is null)
        {
            return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.Unsupported);
        }

        try
        {
            var adapter = new OutputSinkAdapter(serviceId, stream, sink, _logger);
            var subscription = _executor.TrySubscribeOutput(serviceId, processStream, adapter);
            if (subscription is null)
            {
                return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.NotRunning);
            }

            var resultSubscription = new ExtensionServiceOutputSubscription(
                subscription,
                _logger,
                MarkDetached,
                Untrack);
            if (!TryTrack(resultSubscription))
            {
                await resultSubscription.DisposeAsync().ConfigureAwait(false);
                return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.Failed);
            }

            return new ExtensionServiceOutputSubscriptionResult(
                true,
                ExtensionServiceOutputCode.Opened,
                serviceId,
                resultSubscription);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(SubscribeAsync), serviceId);
            return SubscriptionFailure(serviceId, ExtensionServiceOutputCode.Failed);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?> completion;
        IDisposable[] resources;
        lock (_gate)
        {
            if (_disposeTask is { } existing)
            {
                return new ValueTask(existing);
            }

            _disposed = true;
            resources = SnapshotResources();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeResourcesAsync(resources, completion);
        return new ValueTask(completion.Task);
    }

    public void DetachAll()
    {
        IDisposable[] resources;
        lock (_gate)
        {
            resources = SnapshotResources();
        }

        foreach (var resource in resources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                LogCapabilityFailure(exception, nameof(DetachAll), null);
            }
        }
    }

    private async Task DisposeResourcesAsync(
        IDisposable[] resources,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            await Task.WhenAll(resources.Select(DisposeResourceAsync)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(DisposeAsync), null);
        }
        finally
        {
            foreach (var resource in resources)
            {
                Untrack(resource);
            }

            completion.TrySetResult(null);
        }
    }

    private async Task DisposeResourceAsync(IDisposable resource)
    {
        try
        {
            if (resource is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                resource.Dispose();
            }
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(DisposeAsync), null);
        }
        finally
        {
            Untrack(resource);
        }
    }


    private bool TryTrack(IDisposable resource)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _resources.Add(resource);
            return true;
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private void MarkDetached(IDisposable resource)
    {
        lock (_gate)
        {
            _resources.Remove(resource);
            if (!_pendingDisposals.Contains(resource))
            {
                _pendingDisposals.Add(resource);
            }
        }
    }

    private void Untrack(IDisposable resource)
    {
        lock (_gate)
        {
            _resources.Remove(resource);
            _pendingDisposals.Remove(resource);
        }
    }

    private IDisposable[] SnapshotResources()
    {
        lock (_gate)
        {
            return _resources.Concat(_pendingDisposals).Distinct().ToArray();
        }
    }

    private void LogCapabilityFailure(Exception exception, string operation, Guid? serviceId)
    {
        try
        {
            HostLogMessages.ExtensionCapabilityReadFailed(
                _logger,
                exception,
                operation,
                _extensionId,
                serviceId);
        }
        catch
        {
        }
    }

    private bool IsConfigured(Guid serviceId)
    {
        var snapshot = _runtimeState.CurrentSnapshot;
        if (snapshot is null)
        {
            return false;
        }

        foreach (var service in snapshot.Services)
        {
            if (service.Id == serviceId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMapStream(
        ExtensionServiceOutputStream stream,
        out ProcessOutputStream processStream)
    {
        switch (stream)
        {
            case ExtensionServiceOutputStream.Stdout:
                processStream = ProcessOutputStream.Stdout;
                return true;
            case ExtensionServiceOutputStream.Stderr:
                processStream = ProcessOutputStream.Stderr;
                return true;
            default:
                processStream = default;
                return false;
        }
    }

    private static ValueTask<ExtensionServiceOutputStreamResult> OpenFailure(
        Guid serviceId,
        ExtensionServiceOutputCode code) =>
        ValueTask.FromResult(new ExtensionServiceOutputStreamResult(false, code, serviceId, null));

    private static ExtensionServiceOutputSubscriptionResult SubscriptionFailure(
        Guid serviceId,
        ExtensionServiceOutputCode code) =>
        new(false, code, serviceId, null);


    private sealed class TrackingStream : Stream
    {
        private readonly ExtensionServiceOutputFacade _owner;
        private readonly Stream _inner;
        private int _disposed;

        internal TrackingStream(ExtensionServiceOutputFacade owner, Stream inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
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
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void WriteByte(byte value) => _inner.WriteByte(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _inner.Dispose();
                }
                catch (Exception exception)
                {
                    _owner.LogCapabilityFailure(exception, nameof(Stream.Dispose), null);
                }
                finally
                {
                    _owner.Untrack(this);
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    if (_inner is IAsyncDisposable asynchronous)
                    {
                        await asynchronous.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _inner.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    _owner.LogCapabilityFailure(exception, nameof(Stream.DisposeAsync), null);
                }
                finally
                {
                    _owner.Untrack(this);
                }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class OutputSinkAdapter : IProcessOutputChunkSink
    {
        private readonly Guid _serviceId;
        private readonly ExtensionServiceOutputStream _stream;
        private readonly IExtensionServiceOutputSink _sink;
        private readonly ILogger _logger;

        internal OutputSinkAdapter(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            IExtensionServiceOutputSink sink,
            ILogger logger)
        {
            _serviceId = serviceId;
            _stream = stream;
            _sink = sink;
            _logger = logger;
        }

        public void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp)
        {
            try
            {
                var outputChunk = new ExtensionServiceOutputChunk(
                    _serviceId,
                    _stream,
                    timestamp,
                    chunk.ToArray());
                using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.ServiceOutput))
                {
                    _sink.OnChunk(outputChunk);
                }
            }
            catch (Exception exception)
            {
                LogSinkFailure(exception, nameof(OnChunk));
            }
        }

        public void OnCompleted(ProcessOutputCompletion completion)
        {
            try
            {
                var reason = completion switch
                {
                    ProcessOutputCompletion.Completed => ExtensionServiceOutputCompletionReason.ProcessExited,
                    ProcessOutputCompletion.Faulted => ExtensionServiceOutputCompletionReason.Faulted,
                    ProcessOutputCompletion.Teardown => ExtensionServiceOutputCompletionReason.HostTeardown,
                    _ => ExtensionServiceOutputCompletionReason.Faulted
                };
                using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.ServiceOutput))
                {
                    _sink.OnCompleted(reason);
                }
            }
            catch (Exception exception)
            {
                LogSinkFailure(exception, nameof(OnCompleted));
            }
        }

        public void OnDropped(long byteCount)
        {
            try
            {
                using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.ServiceOutput))
                {
                    _sink.OnDropped(byteCount);
                }
            }
            catch (Exception exception)
            {
                LogSinkFailure(exception, nameof(OnDropped));
            }
        }

        private void LogSinkFailure(Exception exception, string operation)
        {
            try
            {
                HostLogMessages.ProcessOutputSinkFailure(_logger, exception, operation);
            }
            catch
            {
            }
        }
    }

    private sealed class ExtensionServiceOutputSubscription : IExtensionServiceOutputSubscription
    {
        private readonly IDisposable _underlying;
        private readonly ILogger _logger;
        private readonly Action<IDisposable> _onDetached;
        private readonly Action<IDisposable> _onQuiesced;
        private int _disposed;
        private int _detached;
        private int _quiesced;

        internal ExtensionServiceOutputSubscription(
            IDisposable underlying,
            ILogger logger,
            Action<IDisposable> onDetached,
            Action<IDisposable> onQuiesced)
        {
            _underlying = underlying;
            _logger = logger;
            _onDetached = onDetached;
            _onQuiesced = onQuiesced;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var asynchronous = _underlying is IAsyncDisposable;
            try
            {
                _underlying.Dispose();
            }
            catch (Exception exception)
            {
                LogDisposeFailure(exception, nameof(Dispose));
            }
            finally
            {
                if (asynchronous)
                {
                    MarkDetached();
                    _ = AwaitQuiescenceAsync((IAsyncDisposable)_underlying);
                }
                else
                {
                    MarkQuiesced();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            var firstDispose = Interlocked.Exchange(ref _disposed, 1) == 0;
            try
            {
                if (_underlying is IAsyncDisposable asynchronous)
                {
                    MarkDetached();
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (firstDispose)
                {
                    _underlying.Dispose();
                }
            }
            catch (Exception exception)
            {
                LogDisposeFailure(exception, nameof(DisposeAsync));
            }
            finally
            {
                MarkQuiesced();
            }
        }

        private async Task AwaitQuiescenceAsync(IAsyncDisposable asynchronous)
        {
            try
            {
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogDisposeFailure(exception, nameof(DisposeAsync));
            }
            finally
            {
                MarkQuiesced();
            }
        }

        private void MarkDetached()
        {
            if (Interlocked.Exchange(ref _detached, 1) == 0)
            {
                _onDetached(this);
            }
        }

        private void MarkQuiesced()
        {
            if (Interlocked.Exchange(ref _quiesced, 1) == 0)
            {
                _onQuiesced(this);
            }
        }

        private void LogDisposeFailure(Exception exception, string operation)
        {
            try
            {
                HostLogMessages.ProcessOutputSinkCleanupFailure(_logger, exception, operation);
            }
            catch
            {
            }
        }
    }
}
