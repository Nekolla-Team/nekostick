namespace Nekolla.Nekostick.Proxy;

/// <summary>Coordinates endpoint capture leases with atomic service-graph publication.</summary>
internal interface IMicroserviceAdmissionCoordinator
{
    /// <summary>Enters admission before capturing a service endpoint.</summary>
    /// <param name="serviceId">The service whose endpoint is being captured.</param>
    /// <param name="cancellationToken">The request-local cancellation token.</param>
    /// <returns>An idempotent lease protecting endpoint resolution and drain tracking.</returns>
    ValueTask<IMicroserviceAdmissionLease> EnterAsync(
        Guid serviceId,
        CancellationToken cancellationToken);

    /// <summary>Closes admission for a complete graph scope after earlier captures drain.</summary>
    /// <param name="serviceIds">The services whose endpoint captures must be suspended.</param>
    /// <param name="cancellationToken">The token for waiting to establish the suspension.</param>
    /// <returns>An idempotent scope that resumes admission when disposed.</returns>
    ValueTask<IMicroserviceAdmissionGraphSuspension> SuspendAsync(
        IReadOnlyCollection<Guid> serviceIds,
        CancellationToken cancellationToken);
}

/// <summary>Protects one endpoint capture until its drain-tracker lease is acquired.</summary>
internal interface IMicroserviceAdmissionLease : IDisposable
{
}

/// <summary>Holds graph admission closed until the committed view is ready.</summary>
internal interface IMicroserviceAdmissionGraphSuspension : IDisposable
{
}

/// <summary>Provides cancellable endpoint admission and serialized graph suspensions.</summary>
internal sealed class MicroserviceAdmissionCoordinator : IMicroserviceAdmissionCoordinator, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _captureCounts = new();
    private SuspensionState? _suspension;
    private SuspensionPermit? _suspensionPermit;
    private TaskCompletionSource<bool>? _suspensionPermitReleased;
    private bool _disposed;

    /// <summary>Creates a real graph-scoped admission coordinator.</summary>
    public MicroserviceAdmissionCoordinator()
    {
    }

    /// <inheritdoc />
    public ValueTask<IMicroserviceAdmissionLease> EnterAsync(
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task resumeTask;
        lock (_gate)
        {
            ThrowIfDisposedUnderLock();
            var suspension = _suspension;
            if (suspension is null || !suspension.ServiceIds.Contains(serviceId))
            {
                return ValueTask.FromResult<IMicroserviceAdmissionLease>(EnterUnderLock(serviceId));
            }

            resumeTask = suspension.Resume.Task;
        }

        return new ValueTask<IMicroserviceAdmissionLease>(
            WaitForAdmissionAsync(serviceId, resumeTask, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask<IMicroserviceAdmissionGraphSuspension> SuspendAsync(
        IReadOnlyCollection<Guid> serviceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        cancellationToken.ThrowIfCancellationRequested();
        var permit = await AcquireSuspensionPermitAsync(cancellationToken).ConfigureAwait(false);

        SuspensionState? suspension = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            suspension = new SuspensionState(new HashSet<Guid>(serviceIds));
            lock (_gate)
            {
                ThrowIfDisposedUnderLock();
                var activeCaptures = 0;
                foreach (var serviceId in suspension.ServiceIds)
                {
                    if (_captureCounts.TryGetValue(serviceId, out var count))
                    {
                        activeCaptures = checked(activeCaptures + count);
                    }
                }

                suspension.RemainingCaptures = activeCaptures;
                if (activeCaptures > 0)
                {
                    suspension.Drained = CreateSignal();
                }

                _suspension = suspension;
            }

            if (suspension.Drained is { } drained)
            {
                await drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposedUnderLock();
            }

            return new GraphSuspension(this, suspension, permit);
        }
        catch
        {
            if (suspension is null)
            {
                permit.Dispose();
            }
            else
            {
                Resume(suspension, permit);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SuspensionState? suspension;
        TaskCompletionSource<bool>? permitReleased;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            suspension = _suspension;
            permitReleased = _suspensionPermitReleased;
            _suspensionPermitReleased = null;
        }

        suspension?.Resume.TrySetResult(true);
        suspension?.Drained?.TrySetResult(true);
        permitReleased?.TrySetResult(true);
    }

    private async ValueTask<SuspensionPermit> AcquireSuspensionPermitAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task releasedTask;
            lock (_gate)
            {
                ThrowIfDisposedUnderLock();
                if (_suspensionPermit is null)
                {
                    var permit = new SuspensionPermit(this);
                    _suspensionPermit = permit;
                    return permit;
                }

                releasedTask = (_suspensionPermitReleased ??= CreateSignal()).Task;
            }

            await releasedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IMicroserviceAdmissionLease> WaitForAdmissionAsync(
        Guid serviceId,
        Task resumeTask,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ThrowIfDisposedUnderLock();
                var suspension = _suspension;
                if (suspension is null || !suspension.ServiceIds.Contains(serviceId))
                {
                    return EnterUnderLock(serviceId);
                }

                resumeTask = suspension.Resume.Task;
            }
        }
    }

    private AdmissionLease EnterUnderLock(Guid serviceId)
    {
        var lease = new AdmissionLease(this, serviceId);
        if (_captureCounts.TryGetValue(serviceId, out var count))
        {
            _captureCounts[serviceId] = checked(count + 1);
        }
        else
        {
            _captureCounts.Add(serviceId, 1);
        }

        return lease;
    }

    private void ReleaseCapture(Guid serviceId)
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (!_captureCounts.TryGetValue(serviceId, out var count))
            {
                throw new InvalidOperationException("An admission capture lease was released more than once.");
            }

            if (count == 1)
            {
                _captureCounts.Remove(serviceId);
            }
            else
            {
                _captureCounts[serviceId] = count - 1;
            }

            var suspension = _suspension;
            if (suspension is not null && suspension.ServiceIds.Contains(serviceId))
            {
                suspension.RemainingCaptures--;
                if (suspension.RemainingCaptures == 0)
                {
                    drained = suspension.Drained;
                }
            }
        }

        drained?.TrySetResult(true);
    }

    private void Resume(SuspensionState suspension, SuspensionPermit permit)
    {
        var resumed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_suspension, suspension))
            {
                _suspension = null;
                resumed = true;
            }
        }

        if (resumed)
        {
            suspension.Resume.TrySetResult(true);
        }

        permit.Dispose();
    }

    private void ReleaseSuspensionPermit(SuspensionPermit permit)
    {
        TaskCompletionSource<bool>? released = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_suspensionPermit, permit))
            {
                return;
            }

            _suspensionPermit = null;
            released = _suspensionPermitReleased;
            _suspensionPermitReleased = null;
        }

        released?.TrySetResult(true);
    }

    private void ThrowIfDisposedUnderLock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SuspensionState(HashSet<Guid> serviceIds)
    {
        internal HashSet<Guid> ServiceIds { get; } = serviceIds;
        internal TaskCompletionSource<bool> Resume { get; } = CreateSignal();
        internal TaskCompletionSource<bool>? Drained { get; set; }
        internal int RemainingCaptures { get; set; }
    }

    private sealed class AdmissionLease(
        MicroserviceAdmissionCoordinator owner,
        Guid serviceId) : IMicroserviceAdmissionLease
    {
        private MicroserviceAdmissionCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseCapture(serviceId);
    }

    private sealed class GraphSuspension(
        MicroserviceAdmissionCoordinator owner,
        SuspensionState suspension,
        SuspensionPermit permit) : IMicroserviceAdmissionGraphSuspension
    {
        private MicroserviceAdmissionCoordinator? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Resume(suspension, permit);
    }

    private sealed class SuspensionPermit(MicroserviceAdmissionCoordinator owner) : IDisposable
    {
        private MicroserviceAdmissionCoordinator? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseSuspensionPermit(this);
    }
}
