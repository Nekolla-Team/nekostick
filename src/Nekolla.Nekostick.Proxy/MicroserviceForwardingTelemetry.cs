using System.Collections.Generic;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Contains one atomically published logical forwarding counter snapshot.</summary>
public readonly record struct MicroserviceForwardingSnapshot(
    long ForwardedRequestCount,
    long ActiveForwardedRequestCount);

/// <summary>Counts logical microservice forwarding requests by service.</summary>
/// <remarks>Retries remain one logical request; the active count is released by the request scope.</remarks>
public interface IMicroserviceForwardingTelemetry
{
    /// <summary>Begins one logical forwarding request for the service.</summary>
    IDisposable Begin(Guid serviceId);

    /// <summary>Reads one consistent counter snapshot for the service.</summary>
    MicroserviceForwardingSnapshot Read(Guid serviceId);
}

/// <summary>Provides lock-consistent request counters for Host runtime telemetry.</summary>
/// <remarks>Idle counters are evicted under bounded retention pressure after their final request scope is released. If all retained counters are active, new services are deliberately untracked and their reads return the default snapshot.</remarks>
public sealed class MicroserviceForwardingTelemetry : IMicroserviceForwardingTelemetry
{
    private const int MaximumRetainedCounters = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Counter> _counters = new();

    /// <inheritdoc />
    public IDisposable Begin(Guid serviceId)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        Counter? counter;
        lock (_gate)
        {
            if (!_counters.TryGetValue(serviceId, out counter))
            {
                EvictIdleCounterIfNeeded();
                if (_counters.Count >= MaximumRetainedCounters)
                {
                    return UntrackedLease.Instance;
                }

                counter = new Counter();
                _counters.Add(serviceId, counter);
            }

            checked
            {
                counter.Total++;
                counter.Active++;
            }
        }

        return new Lease(this, counter);
    }

    /// <inheritdoc />
    public MicroserviceForwardingSnapshot Read(Guid serviceId)
    {
        if (serviceId == Guid.Empty)
        {
            return default;
        }

        lock (_gate)
        {
            return _counters.TryGetValue(serviceId, out var counter)
                ? new MicroserviceForwardingSnapshot(counter.Total, counter.Active)
                : default;
        }
    }

    private void EvictIdleCounterIfNeeded()
    {
        if (_counters.Count < MaximumRetainedCounters)
        {
            return;
        }

        Guid? idleServiceId = null;
        foreach (var pair in _counters)
        {
            if (pair.Value.Active == 0)
            {
                idleServiceId = pair.Key;
                break;
            }
        }

        if (idleServiceId is { } serviceId)
        {
            _counters.Remove(serviceId);
        }
    }

    private void Release(Counter counter)
    {
        lock (_gate)
        {
            if (counter.Active > 0)
            {
                counter.Active--;
            }
        }
    }

    private sealed class Counter
    {
        internal long Total;
        internal long Active;
    }
    private sealed class UntrackedLease : IDisposable
    {
        internal static readonly UntrackedLease Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly MicroserviceForwardingTelemetry _owner;
        private Counter? _counter;

        internal Lease(MicroserviceForwardingTelemetry owner, Counter counter)
        {
            _owner = owner;
            _counter = counter;
        }

        public void Dispose()
        {
            var counter = Interlocked.Exchange(ref _counter, null);
            if (counter is not null)
            {
                _owner.Release(counter);
            }
        }
    }
}

/// <summary>Provides a no-op counter source for direct executor construction without Host composition.</summary>
internal sealed class EmptyMicroserviceForwardingTelemetry : IMicroserviceForwardingTelemetry
{
    internal static readonly EmptyMicroserviceForwardingTelemetry Instance = new();

    public IDisposable Begin(Guid serviceId) => EmptyLease.Instance;

    public MicroserviceForwardingSnapshot Read(Guid serviceId) => default;

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();
        public void Dispose()
        {
        }
    }
}
