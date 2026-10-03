using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

internal sealed record HostServiceRuntimeStateChange(
    long Sequence,
    Guid ServiceId,
    ExtensionServiceRuntimeStateChangeKind Kind,
    HostServiceRuntimeSnapshot? Snapshot,
    bool IsInitialSnapshot,
    string? OwnerExtensionId = null);

internal interface IHostServiceRuntimeStateSource
{
    IDisposable Subscribe(Action<HostServiceRuntimeStateChange> observer);
}

internal sealed class HostServiceRuntimeRegistry :
    IHostServiceRuntimeSnapshotAccessor,
    IHostServiceRuntimeStateSource,
    IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RuntimeEntry> _entries = [];
    private readonly HashSet<RuntimeStateSubscription> _subscriptions = [];
    private long _sequence;
    private bool _disposed;

    public ImmutableArray<HostServiceRuntimeSnapshot> ReadCurrent()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(static entry => entry.Sequence)
                .Select(static entry => entry.Snapshot)
                .ToImmutableArray();
        }
    }

    public bool TryGet(Guid serviceId, out HostServiceRuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(serviceId, out var entry))
            {
                snapshot = entry.Snapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    internal void Publish(
        HostServiceRuntimeSnapshot snapshot,
        long serviceVersion,
        bool enabled,
        bool preserveServiceVersion = false,
        int restartCountIncrement = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(restartCountIncrement);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (preserveServiceVersion && _entries.TryGetValue(snapshot.ServiceId, out var current))
            {
                serviceVersion = current.ServiceVersion;
            }

            PublishLocked(snapshot, serviceVersion, enabled, restartCountIncrement);
        }
    }

    internal void SynchronizeConfiguration(
        HostConfigurationSnapshot configuration,
        IReadOnlySet<Guid> enabledServices,
        IReadOnlySet<Guid> lifecycleWork,
        IReadOnlyDictionary<Guid, string?>? serviceOwners,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(enabledServices);
        ArgumentNullException.ThrowIfNull(lifecycleWork);
        now = now.ToUniversalTime();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var configuredIds = new HashSet<Guid>();
            foreach (var service in configuration.Services)
            {
                configuredIds.Add(service.Id);
                var enabled = service.Enabled && enabledServices.Contains(service.Id);
                var owner = serviceOwners is not null && serviceOwners.TryGetValue(service.Id, out var ownerId)
                    ? ownerId
                    : null;

                _entries.TryGetValue(service.Id, out var current);
                if (current is not null)
                {
                    if (lifecycleWork.Contains(service.Id))
                    {
                        continue;
                    }

                    if (enabled && current.Enabled &&
                        current.Snapshot.LifecycleState != ExtensionServiceLifecycleState.Disabled)
                    {
                        if (current.ServiceVersion != service.Version ||
                            current.Snapshot.ConfigurationVersion != configuration.Version ||
                            !string.Equals(current.Snapshot.OwnerExtensionId, owner, StringComparison.Ordinal))
                        {
                            var updated = current.Snapshot.WithConfigurationVersion(configuration.Version, owner, now);
                            PublishLocked(updated, service.Version, enabled);
                        }

                        continue;
                    }

                    if (!enabled && !current.Enabled && current.Snapshot.LifecycleState == ExtensionServiceLifecycleState.Disabled &&
                        string.Equals(current.Snapshot.OwnerExtensionId, owner, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                var lifecycle = enabled
                    ? ExtensionServiceLifecycleState.Stopped
                    : ExtensionServiceLifecycleState.Disabled;
                var configuredSnapshot = new HostServiceRuntimeSnapshot(
                    service.Id,
                    configuration.Version,
                    null,
                    null,
                    null,
                    now,
                    current?.Snapshot.LastHealthAt,
                    lifecycle,
                    ExtensionServiceHealthState.Unknown,
                    owner,
                    lastProbe: enabled ? current?.Snapshot.LastProbe : null,
                    restartCount: current?.RestartCount ?? 0,
                    stateEnteredAt: now);
                PublishLocked(configuredSnapshot, service.Version, enabled);
            }

            foreach (var serviceId in _entries.Keys.ToArray())
            {
                if (!configuredIds.Contains(serviceId) && !lifecycleWork.Contains(serviceId))
                {
                    RemoveLocked(serviceId);
                }
            }
        }
    }

    public IDisposable Subscribe(Action<HostServiceRuntimeStateChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new RuntimeStateSubscription(observer, RemoveSubscription);
        lock (_gate)
        {
            ThrowIfDisposed();
            _subscriptions.Add(subscription);
            foreach (var entry in _entries.Values.OrderBy(static value => value.Sequence))
            {
                subscription.Enqueue(new HostServiceRuntimeStateChange(
                    entry.Sequence,
                    entry.Snapshot.ServiceId,
                    ExtensionServiceRuntimeStateChangeKind.Snapshot,
                    entry.Snapshot,
                    IsInitialSnapshot: true,
                    OwnerExtensionId: entry.Snapshot.OwnerExtensionId));
            }

            subscription.Start();
        }

        return subscription;
    }


    public async ValueTask DisposeAsync()
    {
        RuntimeStateSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void PublishLocked(
        HostServiceRuntimeSnapshot snapshot,
        long serviceVersion,
        bool enabled,
        int restartCountIncrement = 0)
    {
        _entries.TryGetValue(snapshot.ServiceId, out var current);
        var cumulativeRestartCount = current?.RestartCount ?? snapshot.RestartCount;
        cumulativeRestartCount = checked(cumulativeRestartCount + restartCountIncrement);
        if (snapshot.RestartCount != cumulativeRestartCount)
        {
            snapshot = snapshot.WithRestartCount(cumulativeRestartCount);
        }

        var stateEnteredAt = current is not null &&
            current.Snapshot.LifecycleState == snapshot.LifecycleState
                ? current.Snapshot.StateEnteredAt
                : snapshot.LastUpdatedAt;
        if (snapshot.StateEnteredAt != stateEnteredAt)
        {
            snapshot = snapshot.WithStateEnteredAt(stateEnteredAt);
        }

        if (current is not null &&
            current.ServiceVersion == serviceVersion &&
            current.Enabled == enabled &&
            current.Snapshot == snapshot)
        {
            return;
        }

        var sequence = checked(++_sequence);
        _entries[snapshot.ServiceId] = new RuntimeEntry(
            sequence,
            serviceVersion,
            enabled,
            snapshot,
            cumulativeRestartCount);
        var change = new HostServiceRuntimeStateChange(
            sequence,
            snapshot.ServiceId,
            ExtensionServiceRuntimeStateChangeKind.Snapshot,
            snapshot,
            IsInitialSnapshot: false,
            OwnerExtensionId: snapshot.OwnerExtensionId);
        foreach (var subscription in _subscriptions)
        {
            subscription.Enqueue(change);
        }
    }

    private void RemoveLocked(Guid serviceId)
    {
        if (!_entries.TryGetValue(serviceId, out var entry))
        {
            return;
        }

        _entries.Remove(serviceId);
        var change = new HostServiceRuntimeStateChange(
            checked(++_sequence),
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Removed,
            null,
            IsInitialSnapshot: false,
            OwnerExtensionId: entry.Snapshot.OwnerExtensionId);
        foreach (var subscription in _subscriptions)
        {
            subscription.Enqueue(change);
        }
    }

    private void RemoveSubscription(RuntimeStateSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }


    private sealed record RuntimeEntry(
        long Sequence,
        long ServiceVersion,
        bool Enabled,
        HostServiceRuntimeSnapshot Snapshot,
        int RestartCount);

    // Pending snapshots coalesce by service ID; ordered removal records are never discarded.
    private sealed class RuntimeStateSubscription : IDisposable, IAsyncDisposable
    {
        private readonly Action<HostServiceRuntimeStateChange> _observer;
        private readonly Action<RuntimeStateSubscription> _onDisposed;
        private readonly object _changesGate = new();
        private readonly LinkedList<HostServiceRuntimeStateChange> _changes = new();
        private readonly Dictionary<Guid, LinkedListNode<HostServiceRuntimeStateChange>> _pendingSnapshots = [];
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _deliveryTask;
        private int _disposed;

        internal RuntimeStateSubscription(
            Action<HostServiceRuntimeStateChange> observer,
            Action<RuntimeStateSubscription> onDisposed)
        {
            _observer = observer;
            _onDisposed = onDisposed;
        }

        internal void Enqueue(HostServiceRuntimeStateChange change)
        {
            lock (_changesGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (_pendingSnapshots.Remove(change.ServiceId, out var pendingSnapshot))
                {
                    if (pendingSnapshot.Value.IsInitialSnapshot && change.Kind == ExtensionServiceRuntimeStateChangeKind.Snapshot)
                    {
                        change = change with { IsInitialSnapshot = true };
                    }

                    _changes.Remove(pendingSnapshot);
                }

                var wasEmpty = _changes.Count == 0;
                var node = _changes.AddLast(change);
                if (change.Kind == ExtensionServiceRuntimeStateChangeKind.Snapshot)
                {
                    _pendingSnapshots[change.ServiceId] = node;
                }

                if (wasEmpty)
                {
                    _signal.Release();
                }
            }
        }

        internal void Start()
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                _deliveryTask = Task.Run(DeliverAsync);
                return;
            }

            using (ExecutionContext.SuppressFlow())
            {
                _deliveryTask = Task.Run(DeliverAsync);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _onDisposed(this);
            lock (_changesGate)
            {
                _changes.Clear();
                _pendingSnapshots.Clear();
            }

            _stop.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (_deliveryTask is { } deliveryTask)
            {
                try
                {
                    await deliveryTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }

            _stop.Dispose();
        }

        private async Task DeliverAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                    while (TryDequeue(out var change))
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            return;
                        }

                        try
                        {
                            _observer(change);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private bool TryDequeue(out HostServiceRuntimeStateChange change)
        {
            lock (_changesGate)
            {
                if (_changes.First is not { } node)
                {
                    change = null!;
                    return false;
                }

                _changes.RemoveFirst();
                if (node.Value.Kind == ExtensionServiceRuntimeStateChangeKind.Snapshot &&
                    _pendingSnapshots.TryGetValue(node.Value.ServiceId, out var pendingSnapshot) &&
                    ReferenceEquals(pendingSnapshot, node))
                {
                    _pendingSnapshots.Remove(node.Value.ServiceId);
                }

                change = node.Value;
                return true;
            }
        }
    }
}
