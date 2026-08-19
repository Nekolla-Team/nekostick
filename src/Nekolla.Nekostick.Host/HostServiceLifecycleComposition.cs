using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;
using ContractHealthKind = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using ContractStartMode = Nekolla.Nekostick.Contracts.ServiceStartMode;
using DomainHealthKind = Nekolla.Nekostick.Domain.ServiceHealthCheckKind;
using DomainRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Host;

/// <summary>Describes the fixed outcome of a Host lifecycle readiness request.</summary>
public enum HostServiceReadinessStatus
{
    /// <summary>The service has a ready active generation.</summary>
    Ready,
    /// <summary>The service could not become ready.</summary>
    Unavailable,
    /// <summary>The service is disabled or absent.</summary>
    Disabled,
    /// <summary>The database currently prevents new service work.</summary>
    DatabaseUnavailable,
    /// <summary>The readiness operation was cancelled.</summary>
    Cancelled
}

/// <summary>Contains a safe result from a Host service lifecycle request.</summary>
public sealed record HostServiceReadinessResult
{
    internal HostServiceReadinessResult(
        Guid serviceId,
        long configurationVersion,
        HostServiceReadinessStatus status,
        ServiceRuntimeSnapshot? snapshot = null)
    {
        ServiceId = serviceId;
        ConfigurationVersion = configurationVersion;
        Status = status;
        Snapshot = snapshot;
    }

    /// <summary>Gets the requested service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the immutable configuration generation used by the request.</summary>
    public long ConfigurationVersion { get; }

    /// <summary>Gets the fixed readiness status.</summary>
    public HostServiceReadinessStatus Status { get; }

    /// <summary>Gets the immutable lifecycle snapshot, when one is available.</summary>
    public ServiceRuntimeSnapshot? Snapshot { get; }

    /// <summary>Gets whether the service has a ready, active lease.</summary>
    public bool IsReady => Status == HostServiceReadinessStatus.Ready;
}

/// <summary>Composes request-triggered Lazy and background Eager service lifecycle work.</summary>
public interface IHostServiceLifecycleCoordinator
{
    /// <summary>Ensures one immutable service generation is ready without storage access.</summary>
    ValueTask<HostServiceReadinessResult> EnsureReadyAsync(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates service generations, leases, health, restart handoff, and endpoint publication.</summary>
public sealed partial class HostServiceLifecycleManager : BackgroundService, IHostServiceLifecycleCoordinator
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SupervisorStopBound = StopGracePeriod + TimeSpan.FromSeconds(6);
    private static readonly PortLeasePolicy LeasePolicy = PortLeasePolicy.Default;
    private static readonly HealthRetryPolicy HealthPolicy = HealthRetryPolicy.Default;

    private readonly IProcessExecutor _processExecutor;
    private readonly IServiceHealthProbe _healthProbe;
    private readonly IPortLeaseStore _leaseStore;
    private readonly HostConfigurationSnapshotHolder _snapshotHolder;
    private readonly HostServiceEndpointSnapshotPublisher _endpointPublisher;
    private readonly HostRuntimeState _runtimeState;
    private readonly HostRuntimeOptions _options;
    private readonly ExtensionRuntimeManager? _runtimeManager;
    private readonly NodeIdentifier _nodeId;
    private readonly ConcurrentDictionary<Guid, ServiceSlot> _slots = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private IDisposable? _processExitSubscription;
    private int _stopping;
    /// <summary>Creates the Host lifecycle composition service.</summary>
    public HostServiceLifecycleManager(
        IProcessExecutor processExecutor,
        IServiceHealthProbe healthProbe,
        IPortLeaseStore leaseStore,
        HostConfigurationSnapshotHolder snapshotHolder,
        HostServiceEndpointSnapshotPublisher endpointPublisher,
        HostRuntimeState runtimeState,
        HostRuntimeOptions options,
        ExtensionRuntimeManager? runtimeManager = null)
    {
        _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _endpointPublisher = endpointPublisher ?? throw new ArgumentNullException(nameof(endpointPublisher));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runtimeManager = runtimeManager;
        _nodeId = new NodeIdentifier(options.NodeId);
        if (processExecutor is IProcessExitObserver observer)
        {
            try
            {
                _processExitSubscription = observer.Subscribe(HandleProcessExitObservation);
            }
            catch
            {
                _processExitSubscription = null;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<HostServiceReadinessResult> EnsureReadyAsync(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping)
        {
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        var service = snapshot.Services.FirstOrDefault(value => value.Id == serviceId);
        if (service is null || !service.Enabled)
        {
            await WithdrawAsync(serviceId, cancellationToken).ConfigureAwait(false);
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Disabled);
        }

        var slot = _slots.GetOrAdd(serviceId, static _ => new ServiceSlot());
        Task<HostServiceReadinessResult> startup;
        lock (_lifecycleGate)
        {
            if (IsStopping)
            {
                return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            lock (slot.Gate)
            {
                if (slot.Active is { Ready: true } active &&
                    active.Configuration.Version == service.Version &&
                    active.Lease is { } lease && !lease.IsExpired(DateTimeOffset.UtcNow))
                {
                    return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Ready, active.Supervisor.Snapshot);
                }

                if (!_runtimeState.NewServicesAllowed)
                {
                    return new(serviceId, snapshot.Version, HostServiceReadinessStatus.DatabaseUnavailable);
                }

                if (slot.Startup is null)
                {
                    slot.StartupGeneration = service.Version;
                    slot.Startup = StartOrSwitchAsync(slot, snapshot, service);
                }

                startup = slot.Startup!;
            }
        }

        HostServiceReadinessResult result;
        try
        {
            result = await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        if (IsStopping)
        {
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }

        lock (slot.Gate)
        {
            if (slot.Active is { Ready: true } active &&
                active.Lease is { } lease && !lease.IsExpired(DateTimeOffset.UtcNow) &&
                _runtimeState.Status.DatabaseAvailable)
            {
                return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Ready, active.Supervisor.Snapshot);
            }
        }

        if (slot.StartupGeneration != service.Version)
        {
            return await EnsureReadyAsync(snapshot, serviceId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Records a safe process-exit observation and schedules bounded restart handoff.</summary>
    public void NotifyProcessExit(Guid serviceId, bool successfulExit) =>
        _ = NotifyProcessExitAsync(serviceId, successfulExit);

    /// <summary>Completes the process-exit handoff, including any bounded restart refusal or attempt.</summary>
    internal Task NotifyProcessExitAsync(Guid serviceId, bool successfulExit) =>
        HandleProcessExitAsync(serviceId, null, successfulExit, DateTimeOffset.UtcNow);

    private void HandleProcessExitObservation(ProcessExitObservation observation) =>
        _ = HandleProcessExitAsync(
            observation.ServiceId,
            observation.InstanceId,
            observation.SuccessfulExit,
            observation.ExitedAt);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long publishedVersion = -1;
        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var snapshot = _snapshotHolder.Current;
            if (snapshot is not null && snapshot.Version != publishedVersion)
            {
                publishedVersion = snapshot.Version;
                await ReconcileAsync(snapshot, stoppingToken).ConfigureAwait(false);
            }

            await RenewLeasesAsync(stoppingToken).ConfigureAwait(false);
            await ObserveReadyHealthAsync(stoppingToken).ConfigureAwait(false);
            await PublishReadyEndpointsAsync().ConfigureAwait(false);
        }
    }
    /// <summary>Stops all active service generations and withdraws published endpoints.</summary>
    /// <param name="cancellationToken">The shutdown cancellation token.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        (ServiceSlot Slot, Task<HostServiceReadinessResult> Startup)[] startups;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _stopping, 1);
            _shutdownCts.Cancel();

            var pending = new List<(ServiceSlot Slot, Task<HostServiceReadinessResult> Startup)>();
            foreach (var slot in _slots.Values)
            {
                lock (slot.Gate)
                {
                    if (slot.Startup is { } startup)
                    {
                        pending.Add((slot, startup));
                    }
                }
            }

            startups = pending.ToArray();
        }

        await QuiesceStartupsAsync(startups).ConfigureAwait(false);
        foreach (var slot in _slots.Values)
        {
            ServiceGeneration? generation;
            lock (slot.Gate) generation = slot.Active;
            if (generation is null)
            {
                continue;
            }

            try
            {
                using var stopCts = new CancellationTokenSource(SupervisorStopBound);
                await StopGenerationAsync(slot, generation, stopCts.Token).ConfigureAwait(false);
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "stopped");
            }
            catch
            {
                // Continue stopping all owned generations and the executor below.
            }
        }

        if (_processExecutor is IProcessExecutorCleanup cleanup)
        {
            try
            {
                await cleanup.CleanupAsync(StopGracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup is best effort; endpoint publication remains fail-closed.
            }
        }

        try
        {
            _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
        }
        catch
        {
        }

        try
        {
            Interlocked.Exchange(ref _processExitSubscription, null)?.Dispose();
        }
        catch
        {
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task QuiesceStartupsAsync(
        (ServiceSlot Slot, Task<HostServiceReadinessResult> Startup)[] startups)
    {
        if (startups.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(startups.Select(value => value.Startup));
        try
        {
            var completed = await Task.WhenAny(
                all,
                Task.Delay(SupervisorStopBound)).ConfigureAwait(false);
            if (ReferenceEquals(completed, all))
            {
                try
                {
                    await all.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (var (slot, startup) in startups)
            {
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Startup, startup))
                    {
                        slot.Startup = null;
                    }
                }
            }
        }
    }

    internal async Task ReconcileAsync(HostConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var configured = snapshot.Services.Where(value => value.Enabled).ToImmutableDictionary(value => value.Id);
        foreach (var slotPair in _slots)
        {
            if (!configured.ContainsKey(slotPair.Key))
            {
                await WithdrawAsync(slotPair.Key, cancellationToken).ConfigureAwait(false);
            }
        }

        var eagerStarts = configured.Values
            .Where(value => value.StartMode == ContractStartMode.Eager)
            .Select(value => EnsureReadyAsync(snapshot, value.Id, cancellationToken).AsTask())
            .ToArray();
        await Task.WhenAll(eagerStarts).ConfigureAwait(false);
    }

    internal async Task RenewLeasesAsync(CancellationToken cancellationToken)
    {
        foreach (var slot in _slots.Values)
        {
            ServiceGeneration? generation;
            lock (slot.Gate) generation = slot.Active;
            if (generation is null || !generation.Ready)
            {
                continue;
            }

            if (!_runtimeState.NewLeasesAllowed)
            {
                continue;
            }

            var result = await generation.Supervisor.RenewLeaseAsync(
                DateTimeOffset.UtcNow,
                LeasePolicy,
                cancellationToken).ConfigureAwait(false);
            if (result.Status != SupervisorOperationStatus.Applied || result.Lease is null)
            {
                generation.Ready = false;
                _runtimeState.MarkDatabaseUnavailable();
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "unavailable");
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }
            else
            {
                generation.Lease = result.Lease;
            }
        }
    }

    private async Task<HostServiceReadinessResult> StartOrSwitchAsync(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service)
    {
        try
        {
            if (IsStopping)
            {
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            if (!_runtimeState.NewServicesAllowed)
            {
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.DatabaseUnavailable);
            }

            var candidate = await StartGenerationAsync(snapshot, service, _shutdownCts.Token).ConfigureAwait(false);
            if (candidate is null)
            {
                if (!IsStopping)
                {
                    PublishServiceState(service.Id, snapshot.Version, "unavailable");
                }

                return IsStopping
                    ? new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled)
                    : new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
            }

            ServiceGeneration? old = null;
            var accepted = false;
            lock (_lifecycleGate)
            {
                if (!IsStopping)
                {
                    lock (slot.Gate)
                    {
                        old = slot.Active;
                        slot.Active = candidate;
                    }

                    accepted = true;
                }
            }

            if (!accepted)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(service.Id, snapshot.Version, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            PublishServiceState(service.Id, candidate.SnapshotVersion, "ready");
            if (old is not null && !ReferenceEquals(old, candidate))
            {
                await StopGenerationAsync(slot, old, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(old.Configuration.Id, old.SnapshotVersion, "stopped");
            }

            if (IsStopping)
            {
                await StopGenerationAsync(slot, candidate, CancellationToken.None).ConfigureAwait(false);
                PublishServiceState(service.Id, candidate.SnapshotVersion, "stopped");
                return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Ready, candidate.Supervisor.Snapshot);
        }
        catch (OperationCanceledException)
        {
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        catch
        {
            PublishServiceState(service.Id, snapshot.Version, "unavailable");
            return new(service.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable);
        }
        finally
        {
            lock (slot.Gate)
            {
                slot.Startup = null;
            }
        }
    }

    private async Task<ServiceGeneration?> StartGenerationAsync(
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        CancellationToken cancellationToken)
    {
        var rangeStart = snapshot.GlobalSettings.AutoPortRangeStart;
        var rangeEnd = snapshot.GlobalSettings.AutoPortRangeEnd;
        if (rangeStart < 1 || rangeEnd > 65535 || rangeStart > rangeEnd)
        {
            return null;
        }

        var request = PortLeaseRequest.Automatic(
            _nodeId,
            service.Id,
            LeasePolicy.TimeToLive,
            rangeStart,
            rangeEnd);
        var now = DateTimeOffset.UtcNow;
        PortLeaseOperationResult leaseResult;
        try
        {
            leaseResult = await _leaseStore.ApplyAsync(
                PortLeaseIntent.Acquire(request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            _runtimeState.MarkDatabaseUnavailable();
            return null;
        }

        var acquired = leaseResult.Lease;
        if (leaseResult.Status != PortLeaseOperationStatus.Applied || acquired is null)
        {
            if (leaseResult.Status == PortLeaseOperationStatus.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
            }

            return null;
        }

        if (IsStopping ||
            acquired.NodeId != request.NodeId ||
            acquired.ServiceId != request.ServiceId ||
            acquired.Port < rangeStart ||
            acquired.Port > rangeEnd ||
            acquired.IsExpired(now))
        {
            await ReleaseAutomaticLeaseBestEffortAsync(request, acquired).ConfigureAwait(false);
            return null;
        }

        ServiceSupervisor? supervisor = null;
        Task<SupervisorOperationResult>? startTask = null;
        lock (_lifecycleGate)
        {
            if (!IsStopping)
            {
                try
                {
                    supervisor = CreateSupervisor(service, acquired.Port, acquired, now);
                    startTask = supervisor.StartAsync(now, _shutdownCts.Token).AsTask();
                }
                catch
                {
                    startTask = null;
                }
            }
        }

        if (supervisor is null)
        {
            await ReleaseAutomaticLeaseBestEffortAsync(request, acquired).ConfigureAwait(false);
            return null;
        }

        if (startTask is null)
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        SupervisorOperationResult started;
        try
        {
            started = await startTask.ConfigureAwait(false);
        }
        catch
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (started.Status != SupervisorOperationStatus.Applied || supervisor.Lease is null)
        {
            if (started.Reason == ServiceStateReasonCode.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
            }

            return null;
        }

        if (IsStopping)
        {
            await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var retry = HealthRetryState.Start(service.Id, DateTimeOffset.UtcNow, HealthPolicy.StartupTimeout);
        while (true)
        {
            var observationAt = DateTimeOffset.UtcNow;
            var health = await supervisor.ObserveHealthAsync(retry, observationAt, cancellationToken).ConfigureAwait(false);
            var decision = health.Health;
            if (decision?.Action == HealthRetryAction.Healthy && supervisor.Lease is { } readyLease && !readyLease.IsExpired(observationAt))
            {
                if (IsStopping)
                {
                    await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                return new ServiceGeneration(service, supervisor, readyLease, snapshot.Version, decision.NextState);
            }

            if (decision is null || decision.Action is HealthRetryAction.Cancelled or HealthRetryAction.Failed or HealthRetryAction.TimedOut)
            {
                await supervisor.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            retry = decision.NextState;
            if (decision.NextAttemptAt is { } next)
            {
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }


    private void PublishServiceState(Guid serviceId, long version, string state)
    {
        HostCoreEventPublisher.Publish(
            _runtimeManager,
            ExtensionCoreEventKind.ServiceStateChanged,
            new
            {
                serviceId,
                version,
                state
            });
    }
}
