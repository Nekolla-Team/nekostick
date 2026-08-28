using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;
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
        ILogger<HostServiceLifecycleManager> logger,
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
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        if (service is null || !service.Enabled || !IsServiceEnabledForSnapshot(snapshot, serviceId))
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
                    startup = StartOrSwitchAsync(slot, snapshot, service);
                }
                else
                {
                    startup = slot.Startup!;
                }
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
                active.Configuration.Version == service.Version &&
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
        var configured = snapshot.Services
            .Where(value => value.Enabled && IsServiceEnabledForSnapshot(snapshot, value.Id))
            .ToImmutableDictionary(value => value.Id);
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
    internal async Task StopOwnedServicesAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return;
        }

        var serviceIds = _snapshotHolder.RoutingSnapshot?.ServiceOwners
            .Where(pair => string.Equals(pair.Value, extensionId, StringComparison.Ordinal))
            .Select(static pair => pair.Key)
            .ToArray() ?? Array.Empty<Guid>();
        foreach (var serviceId in serviceIds)
        {
            await WithdrawAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsServiceEnabledForSnapshot(
        HostConfigurationSnapshot snapshot,
        Guid serviceId)
    {
        if (_snapshotHolder.RoutingSnapshot?.ServiceOwners.TryGetValue(serviceId, out var owner) != true ||
            owner is null)
        {
            return true;
        }

        var record = snapshot.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, owner, StringComparison.Ordinal));
        // Only a durable Disabled record gates; owners without records (host-attributed or pre-discovery) stay enabled.
        return record?.LoadState != Nekolla.Nekostick.Contracts.ExtensionLoadState.Disabled;
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

            var snapshot = _snapshotHolder.Current;
            if (snapshot is null || !IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id))
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

}
