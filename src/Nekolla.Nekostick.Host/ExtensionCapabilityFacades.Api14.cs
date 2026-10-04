using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

internal sealed class ExtensionServiceOutputFacade : IExtensionServiceOutputCleanup
{
    private readonly string _extensionId;
    private readonly HostRuntimeState _runtimeState;
    private readonly PosixProcessExecutor? _executor;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<IDisposable> _resources = [];
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
            return OpenFailure(
                Guid.CreateVersion7(),
                ExtensionServiceOutputCode.NotFound,
                $"The serviceId argument '{serviceId}' is not a valid UUIDv7 identifier.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }

        if (!TryMapStream(stream, out var processStream))
        {
            return OpenFailure(
                serviceId,
                ExtensionServiceOutputCode.Unsupported,
                $"The stream argument '{stream}' is unsupported; the value must be Stdout or Stderr.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }

        if (!IsConfigured(serviceId, out var configurationUnavailable))
        {
            return configurationUnavailable
                ? OpenFailure(
                    serviceId,
                    ExtensionServiceOutputCode.Failed,
                    $"The current host service configuration snapshot is unavailable; output for service '{serviceId}' cannot be opened.")
                : OpenFailure(
                    serviceId,
                    ExtensionServiceOutputCode.NotFound,
                    $"Service '{serviceId}' was not found in the current host configuration.");
        }

        if (IsDisposed())
        {
            return OpenFailure(
                serviceId,
                ExtensionServiceOutputCode.Failed,
                $"The host service-output facade has been disposed and cannot open a stream for service '{serviceId}'.");
        }

        if (_executor is null)
        {
            return OpenFailure(
                serviceId,
                ExtensionServiceOutputCode.Unsupported,
                $"Service output streaming is unavailable because the host process executor is not available for service '{serviceId}'.");
        }

        try
        {
            if (!_executor.TryOpenOutputStream(serviceId, processStream, out var output) || output is null)
            {
                return OpenFailure(
                    serviceId,
                    ExtensionServiceOutputCode.NotRunning,
                    $"No live or retained {stream} output stream is available for service '{serviceId}'.");
            }

            var trackedOutput = new TrackingStream(this, output);
            if (!TryTrack(trackedOutput))
            {
                trackedOutput.Dispose();
                return OpenFailure(
                    serviceId,
                    ExtensionServiceOutputCode.Failed,
                    $"The host service-output facade was disposed before the output stream for service '{serviceId}' could be registered.");
            }

            return ValueTask.FromResult(new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                serviceId,
                trackedOutput,
                null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ExtensionServiceOutputStreamResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(OpenStreamAsync), serviceId);
            return OpenFailure(
                serviceId,
                ExtensionServiceOutputCode.Failed,
                $"Opening an output stream for service '{serviceId}' failed because a host operation raised {exception.GetType().Name}.");
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


    private void Untrack(IDisposable resource)
    {
        lock (_gate)
        {
            _resources.Remove(resource);
        }
    }

    private IDisposable[] SnapshotResources()
    {
        lock (_gate)
        {
            return _resources.ToArray();
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

    private bool IsConfigured(Guid serviceId, out bool configurationUnavailable)
    {
        var snapshot = _runtimeState.CurrentSnapshot;
        configurationUnavailable = snapshot is null;
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
        ExtensionServiceOutputCode code,
        string message) =>
        ValueTask.FromResult(
            new ExtensionServiceOutputStreamResult(
                false,
                code,
                serviceId,
                null,
                new ExtensionErrorDetail(message)));


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

}

internal sealed class ExtensionServiceRuntimeStateFacade :
    IExtensionServiceRuntimeStateApi,
    IExtensionServiceRuntimeStateCleanup
{
    private readonly string _extensionId;
    private readonly IHostServiceRuntimeSnapshotAccessor _runtimeAccessor;
    private readonly IHostServiceRuntimeStateSource _stateSource;
    private readonly Func<HostServiceRuntimeSnapshot, ExtensionServiceRuntimeSnapshot> _toContract;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<IDisposable> _resources = [];
    private readonly List<IDisposable> _pendingDisposals = [];
    private bool _disposed;
    private Task? _disposeTask;

    internal ExtensionServiceRuntimeStateFacade(
        string extensionId,
        IHostServiceRuntimeSnapshotAccessor runtimeAccessor,
        IHostServiceRuntimeStateSource stateSource,
        Func<HostServiceRuntimeSnapshot, ExtensionServiceRuntimeSnapshot> toContract,
        ILogger? logger = null)
    {
        _extensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _runtimeAccessor = runtimeAccessor ?? throw new ArgumentNullException(nameof(runtimeAccessor));
        _stateSource = stateSource ?? throw new ArgumentNullException(nameof(stateSource));
        _toContract = toContract ?? throw new ArgumentNullException(nameof(toContract));
        _logger = logger ?? NullLogger.Instance;
    }

    public async ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeStatesAsync(
        IExtensionServiceRuntimeStateSink sink,
        CancellationToken cancellationToken = default)
    {
        if (sink is null)
        {
            return SubscriptionFailure(
                ExtensionServiceRuntimeStateSubscriptionCode.InvalidArgument,
                "The sink argument is required; its value was null.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed())
        {
            return SubscriptionFailure(
                ExtensionServiceRuntimeStateSubscriptionCode.Failed,
                "The host runtime-state subscription facade has been disposed and cannot create a subscription.");
        }

        try
        {
            _runtimeAccessor.ReadCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = new RuntimeStateSinkAdapter(this, sink, _toContract);
            var underlying = _stateSource.Subscribe(adapter.OnStateChanged);
            var subscription = new ExtensionServiceRuntimeStateSubscription(underlying, this);
            if (!TryTrack(subscription))
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                return SubscriptionFailure(
                    ExtensionServiceRuntimeStateSubscriptionCode.Failed,
                    "The runtime-state subscription could not be registered because the host facade was disposed before it was tracked.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ExtensionServiceRuntimeStateSubscriptionResult(
                true,
                ExtensionServiceRuntimeStateSubscriptionCode.Subscribed,
                subscription,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(SubscribeStatesAsync), null);
            return SubscriptionFailure(
                ExtensionServiceRuntimeStateSubscriptionCode.Failed,
                $"The host could not create a service runtime-state subscription because its provider raised {exception.GetType().Name}.");
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
        foreach (var resource in SnapshotResources())
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
        await Task.WhenAll(resources.Select(DisposeResourceAsync)).ConfigureAwait(false);
        foreach (var resource in resources)
        {
            Untrack(resource);
        }

        completion.TrySetResult(null);
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

    private static ExtensionServiceRuntimeStateSubscriptionResult SubscriptionFailure(
        ExtensionServiceRuntimeStateSubscriptionCode code,
        string message) =>
        new(false, code, null, new ExtensionErrorDetail(message));

    private sealed class RuntimeStateSinkAdapter
    {
        private readonly ExtensionServiceRuntimeStateFacade _owner;
        private readonly IExtensionServiceRuntimeStateSink _sink;
        private readonly Func<HostServiceRuntimeSnapshot, ExtensionServiceRuntimeSnapshot> _toContract;

        internal RuntimeStateSinkAdapter(
            ExtensionServiceRuntimeStateFacade owner,
            IExtensionServiceRuntimeStateSink sink,
            Func<HostServiceRuntimeSnapshot, ExtensionServiceRuntimeSnapshot> toContract)
        {
            _owner = owner;
            _sink = sink;
            _toContract = toContract;
        }

        internal void OnStateChanged(HostServiceRuntimeStateChange change)
        {
            try
            {
                var snapshot = change.Snapshot is { } current
                    ? _toContract(current)
                    : null;
                var contractChange = new ExtensionServiceRuntimeStateChange(
                    change.ServiceId,
                    change.Sequence,
                    change.Kind,
                    snapshot,
                    change.IsInitialSnapshot,
                    change.OwnerExtensionId);
                using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.ServiceRuntimeState))
                {
                    _sink.OnStateChanged(contractChange);
                }
            }
            catch (Exception exception)
            {
                _owner.LogCapabilityFailure(exception, nameof(OnStateChanged), change.ServiceId);
            }
        }
    }

    private sealed class ExtensionServiceRuntimeStateSubscription : IExtensionServiceRuntimeStateSubscription
    {
        private readonly IDisposable _underlying;
        private readonly ExtensionServiceRuntimeStateFacade _owner;
        private int _disposed;
        private int _detached;
        private int _quiesced;

        internal ExtensionServiceRuntimeStateSubscription(
            IDisposable underlying,
            ExtensionServiceRuntimeStateFacade owner)
        {
            _underlying = underlying;
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _underlying.Dispose();
            }
            catch (Exception exception)
            {
                _owner.LogCapabilityFailure(exception, nameof(Dispose), null);
            }
            finally
            {
                if (_underlying is IAsyncDisposable asynchronous)
                {
                    MarkDetached();
                    _ = AwaitQuiescenceAsync(asynchronous);
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
                _owner.LogCapabilityFailure(exception, nameof(DisposeAsync), null);
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
                _owner.LogCapabilityFailure(exception, nameof(DisposeAsync), null);
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
                _owner.MarkDetached(this);
            }
        }

        private void MarkQuiesced()
        {
            if (Interlocked.Exchange(ref _quiesced, 1) == 0)
            {
                _owner.Untrack(this);
            }
        }
    }
}
