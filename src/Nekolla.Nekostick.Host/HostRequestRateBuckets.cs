using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

internal interface IHostRequestAdmissionClock
{
    DateTimeOffset UtcNow { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemHostRequestAdmissionClock : IHostRequestAdmissionClock
{
    internal static readonly SystemHostRequestAdmissionClock Instance = new();

    private SystemHostRequestAdmissionClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

internal sealed class HostRequestAdmissionState
{
    private readonly object _routeConcurrencyGate = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _routeConcurrency = [];

    internal HostRequestAdmissionState(int maxConcurrentRequests, IHostRequestAdmissionClock clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRequests);

        Concurrency = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        RateBuckets = new HostRequestRateBucketRegistry(clock);
    }

    internal SemaphoreSlim Concurrency { get; }

    internal HostRequestRateBucketRegistry RateBuckets { get; }

    internal SemaphoreSlim GetRouteConcurrency(Guid routeId, int maxConcurrentRequests)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRequests);
        lock (_routeConcurrencyGate)
        {
            if (_routeConcurrency.TryGetValue(routeId, out var existing))
            {
                return existing;
            }

            var created = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
            _routeConcurrency.Add(routeId, created);
            return created;
        }
    }
}

internal readonly record struct HostRateBucketAcquireResult(bool Acquired, bool Cancelled)
{
    internal static HostRateBucketAcquireResult Accepted() => new(true, false);

    internal static HostRateBucketAcquireResult Rejected() => new(false, false);

    internal static HostRateBucketAcquireResult Canceled() => new(false, true);
}

/// <summary>Owns bounded per-snapshot, per-policy, per-client token buckets.</summary>
internal sealed class HostRequestRateBucketRegistry
{
    private const int MaximumPartitions = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostRequestRateBucketPartition> _partitions =
        new(StringComparer.Ordinal);
    private readonly IHostRequestAdmissionClock _clock;

    internal HostRequestRateBucketRegistry(IHostRequestAdmissionClock clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    internal ValueTask<HostRateBucketAcquireResult> AcquireAsync(
        string scope,
        string identity,
        ClientIpRatePolicyConfiguration policy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentNullException.ThrowIfNull(policy);

        HostRequestRateBucketPartition partition;
        lock (_gate)
        {
            if (!_partitions.TryGetValue(scope, out partition!))
            {
                if (_partitions.Count >= MaximumPartitions)
                {
                    RemoveOneFullPartitionLocked();
                }

                if (_partitions.Count >= MaximumPartitions)
                {
                    return ValueTask.FromResult(HostRateBucketAcquireResult.Rejected());
                }

                partition = new HostRequestRateBucketPartition(policy, _clock);
                _partitions.Add(scope, partition);
            }
        }

        return partition.AcquireAsync(identity, cancellationToken);
    }

    private void RemoveOneFullPartitionLocked()
    {
        var now = _clock.UtcNow;
        foreach (var pair in _partitions)
        {
            if (pair.Value.IsEvictable(now))
            {
                _partitions.Remove(pair.Key);
                return;
            }
        }
    }
}

internal sealed class HostRequestRateBucketPartition
{
    private const int MaximumClientBuckets = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostRequestRateBucket> _buckets =
        new(StringComparer.Ordinal);
    private readonly ClientIpRatePolicyConfiguration _policy;
    private readonly IHostRequestAdmissionClock _clock;

    internal HostRequestRateBucketPartition(
        ClientIpRatePolicyConfiguration policy,
        IHostRequestAdmissionClock clock)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal bool IsEvictable(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _buckets.Values.All(bucket => bucket.IsEvictable(now));
        }
    }

    internal ValueTask<HostRateBucketAcquireResult> AcquireAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        HostRequestRateBucket bucket;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(identity, out bucket!))
            {
                if (_buckets.Count >= MaximumClientBuckets)
                {
                    RemoveOneFullBucketLocked();
                }

                if (_buckets.Count >= MaximumClientBuckets)
                {
                    return ValueTask.FromResult(HostRateBucketAcquireResult.Rejected());
                }

                bucket = new HostRequestRateBucket(_policy, _clock);
                _buckets.Add(identity, bucket);
            }
        }

        return bucket.AcquireAsync(cancellationToken);
    }

    private void RemoveOneFullBucketLocked()
    {
        var now = _clock.UtcNow;
        foreach (var pair in _buckets)
        {
            if (pair.Value.IsEvictable(now))
            {
                _buckets.Remove(pair.Key);
                return;
            }
        }
    }
}

internal sealed class HostRequestRateBucket
{
    private sealed class Waiter
    {
    }

    private readonly object _gate = new();
    private readonly IHostRequestAdmissionClock _clock;
    private readonly Queue<Waiter> _waiters = new();
    private readonly ClientIpRatePolicyConfiguration _policy;
    private DateTimeOffset _lastRefill;
    private long _tokens;

    internal HostRequestRateBucket(
        ClientIpRatePolicyConfiguration policy,
        IHostRequestAdmissionClock clock)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastRefill = clock.UtcNow;
        _tokens = policy.TokenLimit;
    }

    internal bool IsEvictable(DateTimeOffset now)
    {
        lock (_gate)
        {
            RefillLocked(now);
            return _waiters.Count == 0 && _tokens == _policy.TokenLimit;
        }
    }

    internal async ValueTask<HostRateBucketAcquireResult> AcquireAsync(
        CancellationToken cancellationToken)
    {
        Waiter? waiter = null;
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                var now = _clock.UtcNow;
                RefillLocked(now);
                if (_tokens > 0 && (_waiters.Count == 0 || ReferenceEquals(_waiters.Peek(), waiter)))
                {
                    if (waiter is not null)
                    {
                        RemoveWaiterLocked(waiter);
                    }

                    _tokens--;
                    return HostRateBucketAcquireResult.Accepted();
                }

                if (waiter is null)
                {
                    if (_policy.RejectionBehavior == RateLimitRejectionBehavior.Reject ||
                        _waiters.Count >= _policy.QueueLimit)
                    {
                        return HostRateBucketAcquireResult.Rejected();
                    }

                    waiter = new Waiter();
                    _waiters.Enqueue(waiter);
                }

                delay = DelayUntilNextTokenLocked(now);
            }

            try
            {
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RemoveWaiter(waiter);
                return HostRateBucketAcquireResult.Canceled();
            }
        }
    }

    private void RemoveWaiter(Waiter? waiter)
    {
        if (waiter is null)
        {
            return;
        }

        lock (_gate)
        {
            RemoveWaiterLocked(waiter);
        }
    }

    private void RemoveWaiterLocked(Waiter waiter)
    {
        var retained = new Queue<Waiter>(_waiters.Count);
        while (_waiters.Count > 0)
        {
            var current = _waiters.Dequeue();
            if (!ReferenceEquals(current, waiter))
            {
                retained.Enqueue(current);
            }
        }

        while (retained.Count > 0)
        {
            _waiters.Enqueue(retained.Dequeue());
        }
    }

    private void RefillLocked(DateTimeOffset now)
    {
        var elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var periods = elapsed.Ticks / _policy.ReplenishmentPeriod.Ticks;
        if (periods <= 0)
        {
            return;
        }

        var replenished = periods > long.MaxValue / _policy.TokensPerPeriod
            ? long.MaxValue
            : periods * _policy.TokensPerPeriod;
        _tokens = replenished >= _policy.TokenLimit - _tokens
            ? _policy.TokenLimit
            : _tokens + replenished;
        _lastRefill = _lastRefill.AddTicks(periods * _policy.ReplenishmentPeriod.Ticks);
    }

    private TimeSpan DelayUntilNextTokenLocked(DateTimeOffset now)
    {
        var remaining = _policy.ReplenishmentPeriod - (now - _lastRefill);
        return remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : remaining;
    }
}
