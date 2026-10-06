using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;
using Nekolla.Nekostick.Proxy;
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
        ServiceRuntimeSnapshot? snapshot = null,
        bool databaseUnavailableProvenance = false)
    {
        ServiceId = serviceId;
        ConfigurationVersion = configurationVersion;
        Status = status;
        Snapshot = snapshot;
        DatabaseUnavailableProvenance = databaseUnavailableProvenance;
    }

    internal bool DatabaseUnavailableProvenance { get; }

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

    /// <summary>Retries a service whose executable is currently unavailable.</summary>
    ValueTask<ConfigurationWriteResult> ResumeAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Restarts one local service generation without changing global configuration.</summary>
    ValueTask<ConfigurationWriteResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates service generations, leases, health, restart handoff, and endpoint publication.</summary>
public sealed partial class HostServiceLifecycleManager : BackgroundService, IHostServiceLifecycleCoordinator, IHostServiceEndpointAuthority
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SupervisorStopBound = StopGracePeriod + TimeSpan.FromSeconds(6);
    private static readonly PortLeasePolicy LeasePolicy = PortLeasePolicy.Default;
    private static readonly HealthRetryPolicy HealthPolicy = HealthRetryPolicy.Default;
    private static readonly RestartBackoffPolicy EagerStartupBackoff = RestartBackoffPolicy.Default;

    private readonly IProcessExecutor _processExecutor;
    private readonly IServiceHealthProbe _healthProbe;
    private readonly IPortLeaseStore _leaseStore;
    private readonly HostConfigurationSnapshotHolder _snapshotHolder;
    private readonly HostServiceEndpointSnapshotPublisher _endpointPublisher;
    private readonly HostRuntimeState _runtimeState;
    private readonly HostRuntimeOptions _options;
    private readonly IMicroserviceDrainTracker _drainTracker;
    private readonly IMicroserviceAdmissionCoordinator _admissionCoordinator;
    private readonly ExtensionRuntimeManager? _runtimeManager;
    private readonly HostServiceRuntimeRegistry _runtimeRegistry;
    private readonly HostServiceLogBufferRegistry _serviceLogBufferRegistry;
    private readonly ILogger _logger;
    private readonly NodeIdentifier _nodeId;
    private readonly ConcurrentDictionary<ServiceGeneration, RetiringGenerationState> _retiringGenerations = new();
    private readonly ConcurrentDictionary<Guid, ServiceSlot> _slots = new();
    private readonly ConcurrentDictionary<Guid, Guid> _startupDependencyWaits = new();
    private readonly ConcurrentDictionary<PortLeaseReleaseKey, PortLease> _pendingLeaseReleases = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _leaseLifecycleGates = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly string _dataDirectory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private IDisposable? _processExitSubscription;
    private int _stopping;
    private readonly record struct PortLeaseReleaseKey(string NodeId, Guid ServiceId, Guid GenerationId, int Port, long Version);
    internal HostServiceLogBufferRegistry ServiceLogBufferRegistry => _serviceLogBufferRegistry;

    private sealed record ServiceDependencyBinding(
        Guid ServiceId,
        long ServiceVersion,
        Guid GenerationId,
        ImmutableDictionary<string, string> ResolvedEnvironment,
        DependencyLeaseIdentity LeaseIdentity,
        string? OwnerExtensionId);

    private readonly record struct DependencyLeaseIdentity(
        NodeIdentifier NodeId,
        Guid ServiceId,
        Guid GenerationId,
        int Port);

    private HostConfigurationSnapshot LatestSnapshot(HostConfigurationSnapshot snapshot) =>
        _snapshotHolder.Current is { } latest && latest.Version >= snapshot.Version
            ? latest
            : snapshot;

    private bool TryCaptureDependencyBinding(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        long expectedServiceVersion,
        out ServiceDependencyBinding? binding)
    {
        lock (_lifecycleGate)
        {
            var latest = LatestSnapshot(snapshot);
            var configuredService = latest.Services.FirstOrDefault(value => value.Id == serviceId);
            var view = _endpointPublisher.CommittedView;
            if (configuredService is null ||
                !configuredService.Enabled ||
                configuredService.Version != expectedServiceVersion ||
                !IsServiceEnabledForSnapshot(latest, serviceId) ||
                !view.Services.TryGetValue(serviceId, out var committed) ||
                committed.ServiceVersion != configuredService.Version ||
                !string.Equals(
                    committed.Endpoint.OwnerExtensionId,
                    GetServiceOwner(serviceId),
                    StringComparison.Ordinal) ||
                !committed.Endpoint.IsActive(DateTimeOffset.UtcNow))
            {
                binding = null;
                return false;
            }

            var leaseIdentity = new DependencyLeaseIdentity(
                _nodeId,
                serviceId,
                committed.GenerationId,
                committed.Endpoint.Port);
            binding = new ServiceDependencyBinding(
                serviceId,
                committed.ServiceVersion,
                committed.GenerationId,
                committed.ResolvedEnvironment,
                leaseIdentity,
                committed.Endpoint.OwnerExtensionId);
            return true;
        }
    }

    private bool AreDependencyBindingsCurrent(
        HostConfigurationSnapshot latest,
        ImmutableDictionary<Guid, ServiceDependencyBinding> bindings,
        out Guid unavailableDependencyId)
    {
        unavailableDependencyId = Guid.Empty;
        if (bindings.IsEmpty)
        {
            return true;
        }

        var view = _endpointPublisher.CommittedView;
        var now = DateTimeOffset.UtcNow;
        foreach (var binding in bindings.Values)
        {
            unavailableDependencyId = binding.ServiceId;
            var configuredService = latest.Services.FirstOrDefault(value => value.Id == binding.ServiceId);
            if (configuredService is null ||
                !configuredService.Enabled ||
                configuredService.Version != binding.ServiceVersion ||
                !IsServiceEnabledForSnapshot(latest, binding.ServiceId) ||
                !string.Equals(
                    GetServiceOwner(binding.ServiceId),
                    binding.OwnerExtensionId,
                    StringComparison.Ordinal) ||
                !view.Services.TryGetValue(binding.ServiceId, out var committed) ||
                committed.ServiceVersion != binding.ServiceVersion ||
                committed.GenerationId != binding.GenerationId ||
                !ReferenceEquals(committed.ResolvedEnvironment, binding.ResolvedEnvironment) ||
                !string.Equals(
                    committed.Endpoint.OwnerExtensionId,
                    binding.OwnerExtensionId,
                    StringComparison.Ordinal) ||
                !committed.Endpoint.IsActive(now) ||
                new DependencyLeaseIdentity(
                    _nodeId,
                    binding.ServiceId,
                    committed.GenerationId,
                    committed.Endpoint.Port) != binding.LeaseIdentity)
            {
                return false;
            }
        }

        unavailableDependencyId = Guid.Empty;
        return true;
    }


    private string? GetServiceOwner(Guid serviceId) =>
        _snapshotHolder.RoutingSnapshot?.ServiceOwners.TryGetValue(serviceId, out var owner) == true
            ? owner
            : null;

    private bool IsValidDependencyLease(
        PortLease lease,
        Guid serviceId,
        Guid generationId,
        DateTimeOffset now) =>
        lease.NodeId == _nodeId &&
        lease.ServiceId == serviceId &&
        lease.GenerationId == generationId &&
        lease.Port is >= 1 and <= 65535 &&
        !lease.IsExpired(now);

    private static DependencyLeaseIdentity GetDependencyLeaseIdentity(PortLease lease) =>
        new(lease.NodeId, lease.ServiceId, lease.GenerationId, lease.Port);


    private bool WouldCreateStartupDependencyCycle(Guid serviceId, Guid dependencyId)
    {
        var visited = new HashSet<Guid>();
        var current = dependencyId;
        for (var hop = 0; hop < 1024; hop++)
        {
            if (current == serviceId)
            {
                return true;
            }

            if (!visited.Add(current) ||
                !_startupDependencyWaits.TryGetValue(current, out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

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
        IMicroserviceDrainTracker drainTracker,
        HostNodeOptions nodeOptions,
        ExtensionRuntimeManager? runtimeManager = null)
        : this(
            processExecutor,
            healthProbe,
            leaseStore,
            snapshotHolder,
            endpointPublisher,
            runtimeState,
            options,
            logger,
            drainTracker,
            nodeOptions,
            runtimeManager,
            null,
            null)
    {
    }

    /// <summary>Creates the Host lifecycle composition service with an explicit runtime registry.</summary>
    internal HostServiceLifecycleManager(
        IProcessExecutor processExecutor,
        IServiceHealthProbe healthProbe,
        IPortLeaseStore leaseStore,
        HostConfigurationSnapshotHolder snapshotHolder,
        HostServiceEndpointSnapshotPublisher endpointPublisher,
        HostRuntimeState runtimeState,
        HostRuntimeOptions options,
        ILogger<HostServiceLifecycleManager> logger,
        IMicroserviceDrainTracker drainTracker,
        HostNodeOptions nodeOptions,
        ExtensionRuntimeManager? runtimeManager,
        HostServiceRuntimeRegistry? runtimeRegistry,
        IMicroserviceAdmissionCoordinator? admissionCoordinator = null)
    {
        _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        _snapshotHolder = snapshotHolder ?? throw new ArgumentNullException(nameof(snapshotHolder));
        _endpointPublisher = endpointPublisher ?? throw new ArgumentNullException(nameof(endpointPublisher));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _drainTracker = drainTracker ?? throw new ArgumentNullException(nameof(drainTracker));
        _dataDirectory = nodeOptions?.DataDirectory ?? throw new ArgumentNullException(nameof(nodeOptions));
        _runtimeManager = runtimeManager;
        _runtimeRegistry = runtimeRegistry ?? new HostServiceRuntimeRegistry();
        _runtimeRegistry.AttachCommittedViewPublisher(_endpointPublisher);
        _admissionCoordinator = admissionCoordinator ?? new MicroserviceAdmissionCoordinator();
        _serviceLogBufferRegistry = new HostServiceLogBufferRegistry(options, processExecutor as PosixProcessExecutor);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nodeId = new NodeIdentifier(options.NodeId);
        if (processExecutor is IProcessExitObserver observer)
        {
            try
            {
                _processExitSubscription = observer.Subscribe(HandleProcessExitObservation);
            }
            catch (Exception exception)
            {
                HostLogMessages.LifecycleBackgroundFailed(
                    _logger,
                    exception,
                    "ProcessExitSubscription",
                    Guid.Empty);
                _processExitSubscription = null;
            }
        }
    }
    private void SynchronizePublishedRuntimeConfiguration(HostConfigurationSnapshot? publishedSnapshot = null)
    {
        var snapshot = publishedSnapshot ?? _snapshotHolder.Current;
        if (snapshot is null)
        {
            return;
        }

        var enabledServices = snapshot.Services
            .Where(service => service.Enabled && IsServiceEnabledForSnapshot(snapshot, service.Id))
            .Select(static service => service.Id)
            .ToImmutableHashSet();
        var lifecycleWork = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var pair in _slots)
        {
            lock (pair.Value.Gate)
            {
                if (pair.Value.Active is not null || pair.Value.Starting is not null || pair.Value.Startup is not null || pair.Value.GraphPreparation)
                {
                    lifecycleWork.Add(pair.Key);
                }
            }
        }

        _runtimeRegistry.SynchronizeConfiguration(
            snapshot,
            enabledServices,
            lifecycleWork.ToImmutableHashSet(),
            _snapshotHolder.RoutingSnapshot?.ServiceOwners,
            DateTimeOffset.UtcNow);
        _serviceLogBufferRegistry.SynchronizeConfiguration(snapshot, enabledServices);
    }

    /// <inheritdoc />
    public ValueTask<HostServiceReadinessResult> EnsureReadyAsync(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        CancellationToken cancellationToken = default) =>
        EnsureReadyWithGraphRefreshAsync(snapshot, serviceId, cancellationToken);

    private async ValueTask<HostServiceReadinessResult> EnsureReadyAsync(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        ImmutableHashSet<Guid> dependencyChain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping)
        {
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
        }
        if (dependencyChain.Contains(serviceId))
        {
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Unavailable);
        }

        var service = snapshot.Services.FirstOrDefault(value => value.Id == serviceId);
        if (service is null || !service.Enabled || !IsServiceEnabledForSnapshot(snapshot, serviceId))
        {
            await WithdrawAsync(serviceId, cancellationToken).ConfigureAwait(false);
            return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Disabled);
        }

        var slot = _slots.GetOrAdd(serviceId, static _ => new ServiceSlot());
        Task<HostServiceReadinessResult>? startup = null;
        HostServiceReadinessResult? immediateResult = null;
        long operationId;
        lock (_lifecycleGate)
        {
            if (IsStopping)
            {
                return new(serviceId, snapshot.Version, HostServiceReadinessStatus.Cancelled);
            }

            var startupGate = _runtimeState.ObserveNewServiceGate();
            lock (slot.Gate)
            {
                if (slot.Active is { Ready: true } active &&
                    active.Configuration.Version == service.Version &&
                    active.Lease is { } lease && !lease.IsExpired(DateTimeOffset.UtcNow))
                {
                    immediateResult = new(
                        serviceId,
                        snapshot.Version,
                        HostServiceReadinessStatus.Ready,
                        active.Supervisor.Snapshot);
                }
                else if (slot.Active is { Ready: false } waiting &&
                    waiting.Configuration.Version == service.Version &&
                    waiting.Supervisor.Snapshot.ObservedLifecycle == ServiceLifecycleState.Waiting)
                {
                    var waitingState = waiting.Supervisor.Snapshot;
                    immediateResult = new(
                        serviceId,
                        snapshot.Version,
                        HostServiceReadinessStatus.Unavailable,
                        waitingState,
                        databaseUnavailableProvenance:
                            waitingState.Reason == ServiceStateReasonCode.DatabaseUnavailable);
                }
                else if (!startupGate.NewServicesAllowed)
                {
                    immediateResult = new(
                        serviceId,
                        snapshot.Version,
                        HostServiceReadinessStatus.DatabaseUnavailable,
                        databaseUnavailableProvenance: startupGate.DatabaseUnavailableGateObserved);
                }
                else
                {
                    if (slot.Startup is null)
                    {
                        slot.StartupGeneration = service.Version;
                        startup = StartOrSwitchAsync(slot, snapshot, service, dependencyChain);
                    }
                    else
                    {
                        startup = slot.Startup;
                    }
                }

                operationId = slot.StartupOperationId;
            }
        }

        if (immediateResult is not null)
        {
            TrackEagerStartupResult(slot, snapshot, service, immediateResult, operationId);
            return immediateResult;
        }

        HostServiceReadinessResult result;
        try
        {
            result = await startup!.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                result = new(
                    serviceId,
                    snapshot.Version,
                    HostServiceReadinessStatus.Ready,
                    active.Supervisor.Snapshot);
            }
        }

        if (slot.StartupGeneration != service.Version)
        {
            return await EnsureReadyAsync(snapshot, serviceId, dependencyChain, cancellationToken).ConfigureAwait(false);
        }

        TrackEagerStartupResult(slot, snapshot, service, result, operationId);
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

            await RetryWaitingServicesAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
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
        (Guid ServiceId, DateTimeOffset RetryAt)[] retryDeadlines;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _stopping, 1);
            _shutdownCts.Cancel();

            var pending = new List<(ServiceSlot Slot, Task<HostServiceReadinessResult> Startup)>();
            var pendingRetryDeadlines = new List<(Guid ServiceId, DateTimeOffset RetryAt)>();
            foreach (var slotPair in _slots)
            {
                var slot = slotPair.Value;
                lock (slot.Gate)
                {
                    if (slot.EagerStartupRetry is { } retry)
                    {
                        slot.EagerStartupRetry = null;
                        pendingRetryDeadlines.Add((slotPair.Key, retry.RetryAt));
                    }

                    if (slot.Startup is { } startup)
                    {
                        pending.Add((slot, startup));
                    }
                }
            }

            startups = pending.ToArray();
            retryDeadlines = pendingRetryDeadlines.ToArray();
        }

        foreach (var retry in retryDeadlines)
        {
            _runtimeRegistry.ClearRetryAt(retry.ServiceId, retry.RetryAt);
        }

        await QuiesceStartupsAsync(startups, _logger).ConfigureAwait(false);
        await _graphTransactionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _graphTransactionGate.Release();
        _serviceLogBufferRegistry.TerminateAll(ExtensionServiceLogTerminationReason.HostShutdown);
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
                var stopped = await StopOrReleaseGenerationAfterExitAsync(
                    slot,
                    generation,
                    stopCts.Token).ConfigureAwait(false);
                var processStopped = generation.ProcessExitRecorded && generation.Supervisor.ActiveProcessInstance is null ||
                    stopped.ProcessStopped;
                if (processStopped)
                {
                    PublishServiceState(
                        generation.Configuration.Id,
                        generation.SnapshotVersion,
                        "stopped");
                }
            }
            catch (Exception exception)
            {
                // Continue stopping all owned generations and the executor below.
                HostLogMessages.LifecycleShutdownCleanupSkipped(
                    _logger,
                    exception,
                    nameof(StopOrReleaseGenerationAfterExitAsync));
            }
        }
        foreach (var serviceId in _pendingLeaseReleases.Keys.Select(static key => key.ServiceId).Distinct().ToArray())
        {
            var leaseGate = LeaseLifecycleGate(serviceId);
            await leaseGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = await RetryPendingLeaseReleasesAsync(
                    serviceId,
                    null,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                leaseGate.Release();
            }
        }

        if (_processExecutor is IProcessExecutorCleanup cleanup)
        {
            try
            {
                await cleanup.CleanupAsync(StopGracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Cleanup is best effort; endpoint publication remains fail-closed.
                HostLogMessages.LifecycleShutdownCleanupSkipped(_logger, exception, "ProcessExecutorCleanup");
            }
        }
        try
        {
            _serviceLogBufferRegistry.Dispose();
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleShutdownCleanupSkipped(_logger, exception, "DisposeServiceLogBufferRegistry");
        }

        try
        {
            _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleShutdownCleanupSkipped(_logger, exception, "PublishEmptyEndpoints");
        }

        try
        {
            Interlocked.Exchange(ref _processExitSubscription, null)?.Dispose();
        }
        catch (Exception exception)
        {
            HostLogMessages.LifecycleShutdownCleanupSkipped(_logger, exception, "DisposeProcessExitSubscription");
        }

        _retiringGenerations.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task QuiesceStartupsAsync(
        (ServiceSlot Slot, Task<HostServiceReadinessResult> Startup)[] startups,
        ILogger logger)
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
                catch (Exception exception)
                {
                    // Individual startup failures are already recorded on their readiness results.
                    HostLogMessages.LifecycleShutdownCleanupSkipped(logger, exception, nameof(QuiesceStartupsAsync));
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
        await RefreshChangedActiveGraphAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var latest = LatestSnapshot(snapshot);
        if (latest.Version > snapshot.Version)
        {
            return;
        }

        snapshot = latest;
        var configured = snapshot.Services
            .Where(value => value.Enabled && IsServiceEnabledForSnapshot(snapshot, value.Id))
            .ToImmutableDictionary(value => value.Id);
        SynchronizePublishedRuntimeConfiguration(snapshot);
        foreach (var slotPair in _slots)
        {
            if (!configured.ContainsKey(slotPair.Key))
            {
                await WithdrawAsync(slotPair.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        SynchronizePublishedRuntimeConfiguration(snapshot);

        var eagerServices = configured.Values
            .Where(value => value.StartMode == ContractStartMode.Eager)
            .ToArray();
        var pending = eagerServices.ToDictionary(value => value.Id);
        var batchIds = pending.Keys.ToImmutableHashSet();
        var dependencies = eagerServices.ToDictionary(
            value => value.Id,
            value => ServiceLaunchTemplate.ExtractDependencies(
                    value.ArgumentList.Cast<string?>().Concat(value.Environment.Values))
                .Where(batchIds.Contains)
                .ToImmutableHashSet());

        while (pending.Count > 0)
        {
            var layer = pending.Values
                .Where(value => dependencies[value.Id].All(dependencyId => !pending.ContainsKey(dependencyId)))
                .ToArray();
            if (layer.Length == 0)
            {
                layer = pending.Values.ToArray();
                pending.Clear();
            }
            else
            {
                foreach (var service in layer)
                {
                    pending.Remove(service.Id);
                }
            }

            var starts = layer
                .Select(value => EnsureReadyAsync(snapshot, value.Id, cancellationToken).AsTask())
                .ToArray();
            await Task.WhenAll(starts).ConfigureAwait(false);
        }
        await PublishReadyEndpointsAsync().ConfigureAwait(false);

    }

    private void TrackEagerStartupResult(
        ServiceSlot slot,
        HostConfigurationSnapshot snapshot,
        ServiceConfiguration service,
        HostServiceReadinessResult result,
        long operationId)
    {
        if (service.StartMode != ContractStartMode.Eager || IsStopping)
        {
            return;
        }

        if (!result.DatabaseUnavailableProvenance)
        {
            ClearEagerStartupRetry(slot, service.Id, snapshot.Version, service.Version, operationId);
            return;
        }

        var currentSnapshot = _snapshotHolder.Current;
        var currentService = currentSnapshot?.Services.FirstOrDefault(value => value.Id == service.Id);
        if (currentSnapshot is null ||
            currentSnapshot.Version != snapshot.Version ||
            currentService is null ||
            currentService.Version != service.Version ||
            !currentService.Enabled ||
            currentService.StartMode != ContractStartMode.Eager ||
            !IsServiceEnabledForSnapshot(currentSnapshot, service.Id))
        {
            ClearEagerStartupRetry(slot, service.Id, snapshot.Version, service.Version, operationId);
            return;
        }

        if (_runtimeRegistry.TryGet(service.Id, out var runtimeSnapshot) &&
            runtimeSnapshot.ConfigurationVersion == snapshot.Version &&
            runtimeSnapshot.FailureCode != ExtensionServiceFailureCode.None &&
            runtimeSnapshot.FailureCode != ExtensionServiceFailureCode.RuntimeUnavailable &&
            runtimeSnapshot.FailureCode != ExtensionServiceFailureCode.DependencyUnavailable)
        {
            ClearEagerStartupRetry(slot, service.Id, snapshot.Version, service.Version, operationId);
            return;
        }

        if (!_runtimeRegistry.HasCurrentEnabledEntry(service.Id, snapshot.Version, service.Version))
        {
            SynchronizePublishedRuntimeConfiguration();
        }

        lock (slot.Gate)
        {
            if (IsStopping ||
                slot.StartupOperationId != operationId ||
                slot.Active is not null ||
                slot.Starting is not null)
            {
                return;
            }

            var attempt = 1;
            if (slot.EagerStartupRetry is { } previous)
            {
                if (previous.SnapshotVersion > snapshot.Version ||
                    previous.SnapshotVersion == snapshot.Version && previous.ServiceVersion > service.Version ||
                    previous.OperationId > operationId)
                {
                    return;
                }

                if (previous.SnapshotVersion == snapshot.Version &&
                    previous.ServiceVersion == service.Version)
                {
                    if (previous.OperationId == operationId)
                    {
                        return;
                    }

                    attempt = previous.Attempt < int.MaxValue
                        ? previous.Attempt + 1
                        : int.MaxValue;
                }
            }

            var retryAt = DateTimeOffset.UtcNow.Add(EagerStartupBackoff.GetBaseDelay(attempt));
            var retry = new EagerStartupRetryState(
                snapshot.Version,
                service.Version,
                attempt,
                retryAt,
                operationId);
            slot.EagerStartupRetry = retry;
            _runtimeRegistry.SetRetryAt(service.Id, snapshot.Version, service.Version, retry.RetryAt);
        }
    }

    private void ClearEagerStartupRetry(
        ServiceSlot slot,
        Guid serviceId,
        long snapshotVersion,
        long serviceVersion,
        long operationId)
    {
        lock (slot.Gate)
        {
            if (slot.StartupOperationId == operationId &&
                slot.EagerStartupRetry is { } retry &&
                retry.SnapshotVersion == snapshotVersion &&
                retry.ServiceVersion == serviceVersion &&
                retry.OperationId <= operationId)
            {
                slot.EagerStartupRetry = null;
                _runtimeRegistry.ClearRetryAt(serviceId, retry.RetryAt);
            }
        }
    }

    private void ClearEagerStartupRetry(
        ServiceSlot slot,
        Guid serviceId,
        EagerStartupRetryState retry)
    {
        lock (slot.Gate)
        {
            if (slot.EagerStartupRetry == retry)
            {
                slot.EagerStartupRetry = null;
                _runtimeRegistry.ClearRetryAt(serviceId, retry.RetryAt);
            }
        }
    }

    private bool TryGetCurrentEagerRetryService(
        HostConfigurationSnapshot? snapshot,
        Guid serviceId,
        EagerStartupRetryState retry,
        out ServiceConfiguration service)
    {
        service = null!;
        if (snapshot is null || snapshot.Version != retry.SnapshotVersion)
        {
            return false;
        }

        service = snapshot.Services.FirstOrDefault(value =>
            value.Id == serviceId &&
            value.Version == retry.ServiceVersion &&
            value.Enabled &&
            value.StartMode == ContractStartMode.Eager)!;
        return service is not null && IsServiceEnabledForSnapshot(snapshot, serviceId);
    }

    private async Task RetryEagerStartupForSlotAsync(
        ServiceSlot slot,
        Guid serviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsStopping || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var snapshot = _snapshotHolder.Current;
        ServiceConfiguration? service = null;
        EagerStartupRetryState retry;
        lock (_lifecycleGate)
        {
            lock (slot.Gate)
            {
                if (slot.EagerStartupRetry is not { } pending)
                {
                    return;
                }

                retry = pending;
                if (!TryGetCurrentEagerRetryService(snapshot, serviceId, retry, out var currentService))
                {
                    if (slot.EagerStartupRetry == pending)
                    {
                        slot.EagerStartupRetry = null;
                        _runtimeRegistry.ClearRetryAt(serviceId, pending.RetryAt);
                    }

                    return;
                }

                if (slot.Active is not null)
                {
                    slot.EagerStartupRetry = null;
                    _runtimeRegistry.ClearRetryAt(serviceId, pending.RetryAt);
                    return;
                }

                if (slot.Starting is not null ||
                    slot.Startup is not null ||
                    !_runtimeState.NewServicesAllowed ||
                    retry.RetryAt > now)
                {
                    return;
                }

                service = currentService;
            }
        }

        if (snapshot is null || service is null || IsStopping || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var latestSnapshot = _snapshotHolder.Current;
        if (!TryGetCurrentEagerRetryService(latestSnapshot, service.Id, retry, out var latestService))
        {
            ClearEagerStartupRetry(slot, serviceId, retry);
            return;
        }

        try
        {
            var result = await EnsureReadyAsync(latestSnapshot!, latestService.Id, cancellationToken).ConfigureAwait(false);
            if (result.Status == HostServiceReadinessStatus.Cancelled && cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            ClearEagerStartupRetry(slot, serviceId, retry);
            HostLogMessages.FailureDetails(_logger, exception, nameof(RetryEagerStartupForSlotAsync));
        }
    }

    internal async Task RetryWaitingServicesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var slotPair in _slots)
        {
            if (IsStopping || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var slot = slotPair.Value;
            Task<HostServiceReadinessResult>? retryTask = null;
            HostConfigurationSnapshot? snapshot = _snapshotHolder.Current;
            lock (_lifecycleGate)
            {
                lock (slot.Gate)
                {
                    var generation = slot.Active;
                    var current = generation?.Supervisor.Snapshot;
                    if (slot.Startup is null &&
                        generation is not null &&
                        !generation.Ready &&
                        current is { ObservedLifecycle: ServiceLifecycleState.Waiting } &&
                        current.Deadline is { } deadline &&
                        deadline.IsReached(now) &&
                        snapshot is not null &&
                        _runtimeState.NewServicesAllowed &&
                        snapshot.Services.Any(value =>
                            value.Id == generation.Configuration.Id &&
                            value.Version == generation.Configuration.Version &&
                            value.Enabled) &&
                        IsServiceEnabledForSnapshot(snapshot, generation.Configuration.Id))
                    {
                        slot.StartupGeneration = generation.Configuration.Version;
                        retryTask = RetryWaitingGenerationAsync(slot, generation, snapshot, cancellationToken);
                        slot.Startup = retryTask;
                    }
                }
            }

            if (retryTask is not null)
            {
                try
                {
                    await retryTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    HostLogMessages.FailureDetails(_logger, exception, nameof(RetryWaitingServicesAsync));
                }
                finally
                {
                    lock (slot.Gate)
                    {
                        if (ReferenceEquals(slot.Startup, retryTask))
                        {
                            slot.Startup = null;
                        }
                    }
                    SynchronizePublishedRuntimeConfiguration();
                }
            }

            await RetryEagerStartupForSlotAsync(
                slot,
                slotPair.Key,
                now,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HostServiceReadinessResult> RetryWaitingGenerationAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        Task<SupervisorOperationResult>? startTask = null;
        var latest = snapshot;
        var generationIsCurrent = false;
        var dependencyBindingsCurrent = true;
        var unavailableDependencyId = Guid.Empty;
        lock (_lifecycleGate)
        {
            latest = LatestSnapshot(snapshot);
            var currentService = latest.Services.FirstOrDefault(value => value.Id == generation.Configuration.Id);
            var stillConfigured = currentService is { Enabled: true } &&
                currentService.Version == generation.Configuration.Version &&
                IsServiceEnabledForSnapshot(latest, generation.Configuration.Id);
            lock (slot.Gate)
            {
                generationIsCurrent = !IsStopping &&
                    ReferenceEquals(slot.Active, generation) &&
                    !generation.Ready &&
                    stillConfigured;
            }

            if (generationIsCurrent)
            {
                dependencyBindingsCurrent = AreDependencyBindingsCurrent(
                    latest,
                    generation.DependencyBindings,
                    out unavailableDependencyId);
                if (dependencyBindingsCurrent)
                {
                    PublishRuntimeSnapshot(generation, lifecycleState: ExtensionServiceLifecycleState.Starting);
                    startTask = StartSupervisorWithPendingLeaseCleanupAsync(
                        generation,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }
            }
        }

        if (generationIsCurrent && !dependencyBindingsCurrent)
        {
            HostLogMessages.ServiceDependencyUnsatisfied(
                _logger,
                generation.Configuration.Id,
                latest.Version,
                unavailableDependencyId);
            PublishRuntimeFailure(
                slot,
                latest,
                generation.Configuration,
                generation,
                generation.Supervisor.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.Spawn,
                ExtensionServiceFailureCode.DependencyUnavailable);
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            return new(
                generation.Configuration.Id,
                latest.Version,
                HostServiceReadinessStatus.Unavailable,
                generation.Supervisor.Snapshot);
        }

        if (startTask is null)
        {
            return new(
                generation.Configuration.Id,
                latest.Version,
                HostServiceReadinessStatus.Cancelled,
                generation.Supervisor.Snapshot);
        }

        SupervisorOperationResult started;
        try
        {
            started = await startTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(generation.Configuration.Id, snapshot.Version, HostServiceReadinessStatus.Cancelled, generation.Supervisor.Snapshot);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(RetryWaitingGenerationAsync));
            var failedState = generation.Supervisor.Snapshot;
            var failureCode = MapFailureCode(failedState.Reason);
            PublishRuntimeSnapshot(
                generation,
                failedState,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                failureCode == ExtensionServiceFailureCode.None
                    ? ExtensionServiceFailureCode.Unknown
                    : failureCode);
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            return new(generation.Configuration.Id, snapshot.Version, HostServiceReadinessStatus.Unavailable, generation.Supervisor.Snapshot);
        }
        slot.ObserveStartAttemptNumber(generation.Supervisor.StartAttemptNumber);

        if (started.Reason == ServiceStateReasonCode.MissingHostEnvironment &&
            started.FailureMessage is { } placeholder)
        {
            HostLogMessages.ServiceLaunchMissingHostEnvironment(
                _logger,
                generation.Configuration.Id,
                snapshot.Version,
                placeholder);
        }
        if (started.Status == SupervisorOperationStatus.Cancelled && cancellationToken.IsCancellationRequested)
        {
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        if (started.Reason == ServiceStateReasonCode.DatabaseUnavailable)
        {
            _runtimeState.MarkDatabaseUnavailable();
            PublishRuntimeSnapshot(
                generation,
                started.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.Spawn,
                ExtensionServiceFailureCode.RuntimeUnavailable,
                retryAt: started.Snapshot.Deadline?.At);
            return new(
                generation.Configuration.Id,
                snapshot.Version,
                HostServiceReadinessStatus.DatabaseUnavailable,
                started.Snapshot,
                databaseUnavailableProvenance: true);
        }


        if (started.Snapshot.ObservedLifecycle == ServiceLifecycleState.Waiting)
        {
            var failureCode = MapFailureCode(started.Reason);
            PublishRuntimeSnapshot(
                generation,
                started.Snapshot,
                ExtensionServiceLifecycleState.Waiting,
                ExtensionServiceFailureStage.Spawn,
                failureCode == ExtensionServiceFailureCode.None
                    ? ExtensionServiceFailureCode.Unknown
                    : failureCode,
                retryAt: started.Snapshot.Deadline?.At);
            PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "waiting");
            return new(
                generation.Configuration.Id,
                snapshot.Version,
                HostServiceReadinessStatus.Unavailable,
                started.Snapshot);
        }

        if (started.Status != SupervisorOperationStatus.Applied || generation.Supervisor.Lease is null)
        {
            if (started.Reason == ServiceStateReasonCode.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
                return new(
                    generation.Configuration.Id,
                    snapshot.Version,
                    HostServiceReadinessStatus.DatabaseUnavailable,
                    started.Snapshot,
                    databaseUnavailableProvenance: true);
            }

            var failureCode = MapFailureCode(started.Reason);
            PublishRuntimeSnapshot(
                generation,
                started.Snapshot,
                ExtensionServiceLifecycleState.Failed,
                ExtensionServiceFailureStage.Spawn,
                failureCode == ExtensionServiceFailureCode.None
                    ? ExtensionServiceFailureCode.StartRejected
                    : failureCode);
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            return new(
                generation.Configuration.Id,
                snapshot.Version,
                HostServiceReadinessStatus.Unavailable,
                started.Snapshot);
        }
        if (!_serviceLogBufferRegistry.HasOutputTap &&
            generation.Supervisor.ActiveProcessInstance is { } processInstanceId)
        {
            _serviceLogBufferRegistry.OnGenerationStarted(
                generation.Configuration.Id,
                processInstanceId,
                generation.Supervisor.StartAttemptNumber,
                started.Snapshot.ChangedAt);
        }

        (PortLease Lease, HealthRetryState Retry)? healthy;
        try
        {
            healthy = await WaitForHealthyAsync(slot, snapshot, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            throw;
        }

        if (healthy is not { } ready)
        {
            var failedState = generation.Supervisor.Snapshot;
            bool processExited;
            int? processExitCode;
            lock (slot.Gate)
            {
                processExited = generation.ProcessExitRecorded;
                processExitCode = generation.ProcessExitCode;
            }

            var failureCode = processExited
                ? ExtensionServiceFailureCode.ProcessExited
                : failedState.LastHealthObservation is { } observation
                    ? MapProbeFailure(observation.Status, failedState.Reason)
                    : ExtensionServiceFailureCode.HealthTimeout;
            if (failureCode == ExtensionServiceFailureCode.None)
            {
                failureCode = ExtensionServiceFailureCode.HealthTimeout;
            }

            PublishRuntimeSnapshot(
                generation,
                failedState,
                ExtensionServiceLifecycleState.Failed,
                processExited ? ExtensionServiceFailureStage.ProcessExit : ExtensionServiceFailureStage.HealthProbe,
                failureCode,
                processExitCode: processExitCode);
            await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new(
                generation.Configuration.Id,
                snapshot.Version,
                HostServiceReadinessStatus.Unavailable,
                generation.Supervisor.Snapshot);
        }

        var readyCandidateIsCurrent = false;
        var readyDependencyBindingsCurrent = true;
        var readyUnavailableDependencyId = Guid.Empty;
        var promoted = false;
        lock (_lifecycleGate)
        {
            latest = LatestSnapshot(snapshot);
            var currentService = latest.Services.FirstOrDefault(value => value.Id == generation.Configuration.Id);
            var stillConfigured = currentService is { Enabled: true } &&
                currentService.Version == generation.Configuration.Version &&
                IsServiceEnabledForSnapshot(latest, generation.Configuration.Id);
            lock (slot.Gate)
            {
                readyCandidateIsCurrent = !IsStopping &&
                    ReferenceEquals(slot.Active, generation) &&
                    !generation.Ready &&
                    stillConfigured;
            }

            if (readyCandidateIsCurrent)
            {
                readyDependencyBindingsCurrent = AreDependencyBindingsCurrent(
                    latest,
                    generation.DependencyBindings,
                    out readyUnavailableDependencyId);
                if (readyDependencyBindingsCurrent)
                {
                    lock (slot.Gate)
                    {
                        if (!IsStopping && ReferenceEquals(slot.Active, generation))
                        {
                            generation.Lease = ready.Lease;
                            generation.HealthRetryState = ready.Retry;
                            generation.Ready = true;
                            promoted = true;
                        }
                        else
                        {
                            readyCandidateIsCurrent = false;
                        }
                    }
                }
            }
        }

        if (!promoted)
        {
            if (readyCandidateIsCurrent && !readyDependencyBindingsCurrent)
            {
                HostLogMessages.ServiceDependencyUnsatisfied(
                    _logger,
                    generation.Configuration.Id,
                    latest.Version,
                    readyUnavailableDependencyId);
                PublishRuntimeFailure(
                    slot,
                    latest,
                    generation.Configuration,
                    generation,
                    generation.Supervisor.Snapshot,
                    ExtensionServiceLifecycleState.Waiting,
                    ExtensionServiceFailureStage.Spawn,
                    ExtensionServiceFailureCode.DependencyUnavailable);
                await WithdrawFailedWaitingGenerationAsync(slot, generation).ConfigureAwait(false);
                return new(
                    generation.Configuration.Id,
                    latest.Version,
                    HostServiceReadinessStatus.Unavailable,
                    generation.Supervisor.Snapshot);
            }

            return new(
                generation.Configuration.Id,
                latest.Version,
                HostServiceReadinessStatus.Cancelled,
                generation.Supervisor.Snapshot);
        }

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        HostLogMessages.ServiceReady(_logger, generation.Configuration.Id, generation.SnapshotVersion);
        PublishRuntimeSnapshot(generation, lifecycleState: ExtensionServiceLifecycleState.Running);
        PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "ready");
        return new(
            generation.Configuration.Id,
            snapshot.Version,
            HostServiceReadinessStatus.Ready,
            generation.Supervisor.Snapshot);
    }

    private async Task WithdrawFailedWaitingGenerationAsync(ServiceSlot slot, ServiceGeneration generation)
    {
        lock (slot.Gate)
        {
            generation.Ready = false;
            if (ReferenceEquals(slot.Active, generation))
            {
                slot.Active = null;
            }
        }

        await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "stopped");
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
        if (!_pendingLeaseReleases.IsEmpty)
        {
            var pendingServiceIds = _pendingLeaseReleases.Keys
                .Select(static key => key.ServiceId)
                .Distinct()
                .ToArray();
            foreach (var serviceId in pendingServiceIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var leaseGate = LeaseLifecycleGate(serviceId);
                // A lifecycle tick must not queue ahead of a generation's start or stop operation.
                if (!leaseGate.Wait(0, cancellationToken))
                {
                    continue;
                }

                try
                {
                    _ = await RetryPendingLeaseReleasesAsync(
                        serviceId,
                        null,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    leaseGate.Release();
                }
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
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
            if (result.LeaseOwnershipLost)
            {
                lock (slot.Gate)
                {
                    if (!ReferenceEquals(slot.Active, generation))
                    {
                        continue;
                    }

                    generation.Ready = false;
                }

                PublishRuntimeSnapshot(
                    generation,
                    result.Snapshot,
                    ExtensionServiceLifecycleState.Failed,
                    ExtensionServiceFailureStage.Spawn,
                    ExtensionServiceFailureCode.PortLeaseUnavailable,
                    preserveServiceVersion: true);
                lock (slot.Gate)
                {
                    if (ReferenceEquals(slot.Active, generation))
                    {
                        slot.Active = null;
                    }
                }

                await StopOrReleaseGenerationAfterExitAsync(
                    slot,
                    generation,
                    CancellationToken.None).ConfigureAwait(false);
                PublishRuntimeSnapshot(
                    generation,
                    generation.Supervisor.Snapshot,
                    ExtensionServiceLifecycleState.Failed,
                    ExtensionServiceFailureStage.Spawn,
                    ExtensionServiceFailureCode.PortLeaseUnavailable,
                    preserveServiceVersion: true);
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "stopped");
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }
            else if (result.Reason == ServiceStateReasonCode.DatabaseUnavailable)
            {
                _runtimeState.MarkDatabaseUnavailable();
            }
            else if (result.Status != SupervisorOperationStatus.Applied || result.Lease is null)
            {
                generation.Ready = false;
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
