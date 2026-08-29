using System.Collections.Concurrent;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Tracks in-flight forwarding by stable service and endpoint port.</summary>
public interface IMicroserviceDrainTracker
{
    /// <summary>Begins tracking one forwarding lifetime.</summary>
    /// <param name="serviceId">The stable microservice identifier.</param>
    /// <param name="port">The endpoint port used by the forwarding lifetime.</param>
    /// <returns>A scope that decrements the in-flight count when disposed.</returns>
    IDisposable BeginTracking(Guid serviceId, int port);

    /// <summary>
    /// Waits for all forwarding lifetimes currently tracked for an endpoint to finish.
    /// A timeout completes normally so callers may proceed with shutdown.
    /// </summary>
    /// <param name="serviceId">The stable microservice identifier.</param>
    /// <param name="port">The endpoint port to drain.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">The cancellation token for the wait.</param>
    /// <returns>A task that completes when drained or when the timeout elapses.</returns>
    ValueTask WaitDrainedAsync(
        Guid serviceId,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides thread-safe in-flight tracking for service endpoint generations.</summary>
public sealed class MicroserviceDrainTracker : IMicroserviceDrainTracker
{
    private readonly ConcurrentDictionary<(Guid ServiceId, int Port), Slot> _slots = new();

    // Kept internal for focused unit tests; the production contract exposes no collection state.
    internal int TrackedSlotCount => _slots.Count;

    /// <inheritdoc />
    public IDisposable BeginTracking(Guid serviceId, int port)
    {
        var key = (serviceId, port);
        while (true)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new Slot(key);
                if (!_slots.TryAdd(key, slot))
                {
                    continue;
                }
            }

            lock (slot.Gate)
            {
                if (slot.Removed)
                {
                    continue;
                }

                // A completed signal belongs to a previous drain. A new generation of
                // tracking on this slot needs a fresh signal for a future waiter.
                if (slot.InFlightCount == 0)
                {
                    slot.DrainSignal = null;
                }

                checked
                {
                    slot.InFlightCount++;
                }

                return new TrackingLease(this, slot);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitDrainedAsync(
        Guid serviceId,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var key = (serviceId, port);
        if (!_slots.TryGetValue(key, out var slot))
        {
            return;
        }

        Task signal;
        lock (slot.Gate)
        {
            if (slot.Removed || slot.InFlightCount == 0)
            {
                if (!slot.Removed)
                {
                    RemoveSlot(slot);
                }

                return;
            }

            slot.WaiterCount++;
            signal = (slot.DrainSignal ??= CreateDrainSignal()).Task;
        }

        try
        {
            try
            {
                await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A bounded drain is best effort; the caller proceeds to stop the process.
            }
        }
        finally
        {
            lock (slot.Gate)
            {
                slot.WaiterCount--;
                if (slot.InFlightCount == 0 && slot.WaiterCount == 0)
                {
                    RemoveSlot(slot);
                }
            }
        }
    }

    private static TaskCompletionSource<bool> CreateDrainSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Release(Slot slot)
    {
        lock (slot.Gate)
        {
            slot.InFlightCount--;
            if (slot.InFlightCount != 0)
            {
                return;
            }

            if (slot.WaiterCount != 0)
            {
                (slot.DrainSignal ??= CreateDrainSignal()).TrySetResult(true);
            }
            else
            {
                RemoveSlot(slot);
            }
        }
    }

    private void RemoveSlot(Slot slot)
    {
        if (slot.Removed)
        {
            return;
        }

        slot.Removed = true;
        ((ICollection<KeyValuePair<(Guid ServiceId, int Port), Slot>>)_slots)
            .Remove(new KeyValuePair<(Guid ServiceId, int Port), Slot>(slot.Key, slot));
    }

    private sealed class Slot
    {
        internal Slot((Guid ServiceId, int Port) key)
        {
            Key = key;
        }

        internal readonly object Gate = new();
        internal readonly (Guid ServiceId, int Port) Key;
        internal int InFlightCount;
        internal int WaiterCount;
        internal bool Removed;
        internal TaskCompletionSource<bool>? DrainSignal;
    }

    private sealed class TrackingLease : IDisposable
    {
        private readonly MicroserviceDrainTracker _owner;
        private Slot? _slot;

        internal TrackingLease(MicroserviceDrainTracker owner, Slot slot)
        {
            _owner = owner;
            _slot = slot;
        }

        public void Dispose()
        {
            var slot = Interlocked.Exchange(ref _slot, null);
            if (slot is not null)
            {
                _owner.Release(slot);
            }
        }
    }
}
