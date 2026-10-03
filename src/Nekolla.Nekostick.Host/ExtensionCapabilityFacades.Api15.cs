using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

internal sealed class ExtensionServiceLogFacade :
    IExtensionServiceOutputApi,
    IExtensionServiceOutputCleanup,
    IExtensionServiceLogCleanup
{
    private readonly string _extensionId;
    private readonly HostRuntimeState _runtimeState;
    private readonly ExtensionServiceOutputFacade _serviceOutput;
    private readonly HostServiceLogBufferRegistry? _bufferRegistry;
    private readonly IHostServiceRuntimeSnapshotAccessor? _runtimeAccessor;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<IDisposable> _resources = [];
    private readonly List<IDisposable> _pendingDisposals = [];
    private bool _disposed;
    private Task? _disposeTask;

    internal ExtensionServiceLogFacade(
        string extensionId,
        HostRuntimeState runtimeState,
        HostServiceLogBufferRegistry? bufferRegistry,
        IHostServiceRuntimeSnapshotAccessor? runtimeAccessor,
        ExtensionServiceOutputFacade serviceOutput,
        ILogger? logger = null)
    {
        _extensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _bufferRegistry = bufferRegistry;
        _runtimeAccessor = runtimeAccessor;
        _logger = logger ?? NullLogger.Instance;
        _serviceOutput = serviceOutput ?? throw new ArgumentNullException(nameof(serviceOutput));
    }

    public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default) =>
        _serviceOutput.OpenStreamAsync(serviceId, stream, cancellationToken);

    public async ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        IExtensionServiceLogSink sink,
        long? sinceSequence = null,
        CancellationToken cancellationToken = default)
    {
        if (!UuidV7.IsVersion7(serviceId))
        {
            return SubscriptionFailure(Guid.CreateVersion7(), ExtensionServiceLogCode.InvalidArgument);
        }

        if (sink is null || sinceSequence is < 0)
        {
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.InvalidArgument);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Cancelled);
        }

        if (IsDisposed())
        {
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Failed);
        }

        if (_bufferRegistry is null)
        {
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Unsupported);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentState = ExtensionServiceLifecycleState.Unknown;
            if (_runtimeAccessor is { } runtimeAccessor &&
                runtimeAccessor.TryGet(serviceId, out var snapshot))
            {
                currentState = snapshot.LifecycleState;
            }

            if (!IsConfigured(serviceId))
            {
                return SubscriptionFailure(serviceId, ExtensionServiceLogCode.NotFound);
            }

            var buffer = _bufferRegistry.GetOrCreate(serviceId);
            var code = buffer.TrySubscribe(
                sinceSequence,
                currentState,
                sink,
                MarkDetached,
                Untrack,
                out var subscription);
            if (code != ExtensionServiceLogCode.Subscribed || subscription is null)
            {
                return SubscriptionFailure(
                    serviceId,
                    code == ExtensionServiceLogCode.Subscribed ? ExtensionServiceLogCode.Failed : code);
            }

            if (!TryTrack(subscription))
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Failed);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Cancelled);
            }

            subscription.Start();
            return new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                serviceId,
                subscription);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Cancelled);
        }
        catch (Exception exception)
        {
            LogCapabilityFailure(exception, nameof(SubscribeAsync), serviceId);
            return SubscriptionFailure(serviceId, ExtensionServiceLogCode.Failed);
        }
    }

    public void DetachAll()
    {
        IDisposable[] resources;
        lock (_gate)
        {
            _disposed = true;
            resources = SnapshotResourcesLocked();
        }
        _serviceOutput.DetachAll();

        foreach (var resource in resources)
        {
            try
            {
                if (resource is HostServiceLogSubscription subscription)
                {
                    subscription.TerminateForExtensionUnload();
                    subscription.Dispose();
                }
                else
                {
                    resource.Dispose();
                }
            }
            catch (Exception exception)
            {
                LogCapabilityFailure(exception, nameof(DetachAll), null);
            }
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
            resources = SnapshotResourcesLocked();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeResourcesAsync(resources, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeResourcesAsync(
        IDisposable[] resources,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            await Task.WhenAll(
                    resources.Select(DisposeResourceAsync)
                        .Append(_serviceOutput.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
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
            if (resource is HostServiceLogSubscription subscription)
            {
                subscription.TerminateForExtensionUnload();
                subscription.Start();
                await subscription.DeliveryTask.ConfigureAwait(false);
            }
            else if (resource is IAsyncDisposable asynchronous)
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

    private IDisposable[] SnapshotResourcesLocked() =>
        _resources.Concat(_pendingDisposals).Distinct().ToArray();

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

    private static ExtensionServiceLogSubscriptionResult SubscriptionFailure(
        Guid serviceId,
        ExtensionServiceLogCode code) =>
        new(false, code, serviceId, null);
}
