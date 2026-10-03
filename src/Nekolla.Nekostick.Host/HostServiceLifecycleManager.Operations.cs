using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using ContractHealthKind = Nekolla.Nekostick.Contracts.ServiceHealthCheckType;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using DomainHealthKind = Nekolla.Nekostick.Domain.ServiceHealthCheckKind;
using DomainRestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy;

namespace Nekolla.Nekostick.Host;

public sealed partial class HostServiceLifecycleManager
{
    private (ServiceSupervisor Supervisor, ImmutableDictionary<string, string> ResolvedEnvironment) CreateSupervisor(
        ServiceConfiguration service,
        int port,
        int attemptNumber,
        PortLease? initialLease = null,
        DateTimeOffset? initialLeaseNow = null)
    {
        var dynamicValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HOST"] = "127.0.0.1"
        };
        var expandedEnvironment = ServiceLaunchTemplate.ExpandEnvironment(
            service.Environment,
            dynamicValues,
            ResolveRemoteEnvironment);
        var resolvedEnvironment = expandedEnvironment.ToImmutableDictionary(
            value => value.Key,
            value => value.Value,
            StringComparer.Ordinal);
        var context = new ServiceTemplateContext(
            expandedEnvironment,
            ResolveRemoteEnvironment,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PORT"] = dynamicValues["PORT"]
            });
        var arguments = ServiceLaunchTemplate.ExpandArguments(service.ArgumentList, context);
        var launch = new ProcessLaunchSpecification(
            service.Id,
            ServicePathResolver.Resolve(_dataDirectory, service.FileName),
            ServicePathResolver.Resolve(_dataDirectory, service.WorkingDirectory),
            arguments,
            new ProcessEnvironment(expandedEnvironment),
            attemptNumber: attemptNumber);
        var healthDefinition = new HealthCheckDefinition(
            service.HealthCheck.Type switch
            {
                ContractHealthKind.Process => DomainHealthKind.Process,
                ContractHealthKind.Tcp => DomainHealthKind.Tcp,
                ContractHealthKind.Http => DomainHealthKind.Http,
                _ => DomainHealthKind.Tcp
            },
            service.HealthCheck.Timeout,
            service.HealthCheck.HttpPath);
        var healthRequest = new ServiceHealthProbeRequest(
            service.Id,
            healthDefinition,
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, port));
        var leaseRequest = new PortLeaseRequest(
            _nodeId,
            service.Id,
            port,
            LeasePolicy.TimeToLive);
        var supervisor = new ServiceSupervisor(
            _processExecutor,
            _healthProbe,
            _leaseStore,
            launch,
            healthRequest,
            leaseRequest,
            HealthPolicy,
            restartPolicy: service.RestartPolicy switch
            {
                ContractRestartPolicy.Never => DomainRestartPolicy.Never,
                ContractRestartPolicy.Always => DomainRestartPolicy.Always,
                _ => DomainRestartPolicy.OnFailure
            },
            stopGracePeriod: StopGracePeriod,
            now: initialLeaseNow,
            initialLease: initialLease,
            logger: _logger);
        return (supervisor, resolvedEnvironment);
    }
    private async Task ReleaseAutomaticLeaseBestEffortAsync(
        PortLeaseRequest request,
        PortLease lease)
    {
        if (lease.NodeId != request.NodeId || lease.ServiceId != request.ServiceId)
        {
            return;
        }

        try
        {
            await _leaseStore.ApplyAsync(
                PortLeaseIntent.ReleaseLease(
                    new PortLeaseRelease(request.NodeId, request.ServiceId, lease.Port, lease.Version)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(ReleaseAutomaticLeaseBestEffortAsync));
        }
    }
    private bool IsStopping => Volatile.Read(ref _stopping) != 0;

    private static bool IsCurrentGeneration(ServiceSlot slot, ServiceGeneration generation)
    {
        lock (slot.Gate)
        {
            return ReferenceEquals(slot.Active, generation);
        }
    }

    private async Task HandleProcessExitAsync(
        Guid serviceId,
        ProcessInstanceId instanceId,
        bool successfulExit,
        DateTimeOffset exitedAt,
        int? exitCode = null)
    {
        if (!_slots.TryGetValue(serviceId, out var slot))
        {
            return;
        }

        ServiceGeneration? generation;
        RetiringGenerationState? retiring = null;
        var isStarting = false;
        lock (slot.Gate)
        {
            generation = slot.Active;
            if (generation is null || generation.Supervisor.ActiveProcessInstance != instanceId)
            {
                generation = slot.Starting;
                isStarting = generation is not null && generation.Supervisor.ActiveProcessInstance == instanceId;
                if (!isStarting)
                {
                    return;
                }
            }

            if (generation is null)
            {
                return;
            }

            if (!isStarting)
            {
                _retiringGenerations.TryGetValue(generation, out retiring);
            }

            generation.Ready = false;
            if (isStarting)
            {
                generation.ProcessExitCode = exitCode;
            }
        }
        if (!_serviceLogBufferRegistry.HasOutputTap)
        {
            _serviceLogBufferRegistry.OnGenerationExited(
                serviceId,
                instanceId,
                generation.Supervisor.StartAttemptNumber,
                exitCode,
                exitedAt);
        }


        if (successfulExit)
        {
            HostLogMessages.ServiceExitedSuccessfully(_logger, serviceId);
        }
        else
        {
            HostLogMessages.ServiceExitedUnexpectedly(_logger, serviceId);
        }

        PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "unavailable");
        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        if (IsStopping)
        {
            return;
        }

        if (isStarting)
        {
            SupervisorOperationResult startingResult;
            lock (slot.Gate)
            {
                if (IsStopping || !ReferenceEquals(slot.Starting, generation) ||
                    generation.Supervisor.ActiveProcessInstance != instanceId)
                {
                    return;
                }

                startingResult = generation.Supervisor.RecordProcessExit(successfulExit, exitedAt);
                generation.ProcessExitRecorded = true;
                generation.ProcessExitCode = exitCode;
            }

            if (_snapshotHolder.Current is { } snapshot)
            {
                PublishRuntimeFailure(
                    slot,
                    snapshot,
                    generation.Configuration,
                    generation,
                    startingResult.Snapshot,
                    ExtensionServiceLifecycleState.Failed,
                    ExtensionServiceFailureStage.ProcessExit,
                    ExtensionServiceFailureCode.ProcessExited,
                    exitCode);
            }
            return;
        }

        if (retiring is not null)
        {
            if (TryClaimRetiringProcessExit(retiring))
            {
                try
                {
                    await generation.Supervisor.AcknowledgeProcessExitAsync().ConfigureAwait(false);
                    generation.Lease = null;
                }
                finally
                {
                    retiring.ProcessExitAcknowledged.TrySetResult(true);
                }
            }

            return;
        }

        SupervisorOperationResult result;
        lock (slot.Gate)
        {
            if (IsStopping || !ReferenceEquals(slot.Active, generation) ||
                generation.Supervisor.ActiveProcessInstance != instanceId)
            {
                return;
            }

            result = generation.Supervisor.RecordProcessExit(successfulExit, exitedAt);
            generation.ProcessExitRecorded = true;
            generation.ProcessExitCode = exitCode;
        }

        var willRestart = result.Restart is { ShouldRestart: true, NotBefore: { } };
        var notBefore = result.Restart?.NotBefore;
        var cleanExit = successfulExit && exitCode == 0;
        PublishRuntimeSnapshot(
            generation,
            result.Snapshot,
            willRestart
                ? ExtensionServiceLifecycleState.Waiting
                : cleanExit
                    ? ExtensionServiceLifecycleState.Stopped
                    : ExtensionServiceLifecycleState.Failed,
            cleanExit ? ExtensionServiceFailureStage.None : ExtensionServiceFailureStage.ProcessExit,
            cleanExit ? ExtensionServiceFailureCode.None : ExtensionServiceFailureCode.ProcessExited,
            processExitCode: exitCode,
            retryAt: notBefore,
            preserveServiceVersion: true);

        if (!willRestart || notBefore is null)
        {
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, CancellationToken.None).ConfigureAwait(false);
            PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "stopped");
            lock (slot.Gate)
            {
                if (ReferenceEquals(slot.Active, generation))
                {
                    slot.Active = null;
                }
            }

            await PublishReadyEndpointsAsync().ConfigureAwait(false);
            return;
        }

        if (!IsStopping)
        {
            HostLogMessages.ServiceRestartScheduled(_logger, serviceId);
            PublishServiceState(generation.Configuration.Id, generation.SnapshotVersion, "restarting");
            await RestartAfterAsync(slot, generation, notBefore.Value, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ObserveReadyHealthAsync(CancellationToken cancellationToken)
    {
        foreach (var slot in _slots.Values)
        {
            if (IsStopping || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ServiceGeneration? generation;
            bool hasStartingCandidate;
            lock (slot.Gate)
            {
                generation = slot.Active;
                hasStartingCandidate = slot.Starting is { } starting && !ReferenceEquals(starting, generation);
            }
            if (generation is null || !generation.Ready)
            {
                continue;
            }

            SupervisorOperationResult result;
            try
            {
                result = await generation.Supervisor.ObserveHealthAsync(
                    generation.HealthRetryState,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                HostLogMessages.FailureDetails(_logger, exception, nameof(ObserveReadyHealthAsync));
                continue;
            }

            var decision = result.Health;
            if (decision is null)
            {
                lock (slot.Gate)
                {
                    if (IsStopping || !ReferenceEquals(slot.Active, generation))
                    {
                        continue;
                    }
                }
                generation.LastHealthProbeReason = result.Snapshot.Reason;

                PublishRuntimeSnapshot(
                    generation,
                    result.Snapshot,
                    preserveFailure: true,
                    preserveServiceVersion: true,
                    preserveLastProbe: hasStartingCandidate);
                continue;
            }

            var withdraw = false;
            var terminal = false;
            lock (slot.Gate)
            {
                if (IsStopping || !ReferenceEquals(slot.Active, generation) || !generation.Ready)
                {
                    continue;
                }

                generation.HealthRetryState = decision.NextState;
                generation.LastHealthProbeReason = result.Snapshot.Reason;
                if (decision.Action == HealthRetryAction.Healthy)
                {
                    if (result.Lease is { } lease && !lease.IsExpired(DateTimeOffset.UtcNow))
                    {
                        generation.Lease = lease;
                        generation.Ready = true;
                    }
                    else
                    {
                        generation.Ready = false;
                        withdraw = true;
                    }
                }
                else if (decision.Action is HealthRetryAction.Failed or HealthRetryAction.TimedOut)
                {
                    terminal = true;
                }
            }
            if (terminal)
            {
                var failureCode = result.Snapshot.LastHealthObservation is { } observation
                    ? MapProbeFailure(observation.Status, result.Snapshot.Reason)
                    : ExtensionServiceFailureCode.HealthCheckFailed;
                PublishRuntimeSnapshot(
                    generation,
                    result.Snapshot,
                    ExtensionServiceLifecycleState.Failed,
                    ExtensionServiceFailureStage.HealthProbe,
                    failureCode == ExtensionServiceFailureCode.None
                        ? ExtensionServiceFailureCode.HealthCheckFailed
                        : failureCode,
                    preserveServiceVersion: true);
            }
            else
            {
                PublishRuntimeSnapshot(
                    generation,
                    result.Snapshot,
                    preserveFailure: true,
                    preserveServiceVersion: true,
                    preserveLastProbe: hasStartingCandidate);
            }

            if (withdraw)
            {
                PublishServiceState(
                    generation.Configuration.Id,
                    generation.SnapshotVersion,
                    "unavailable");
                await PublishReadyEndpointsAsync().ConfigureAwait(false);
            }

            if (terminal)
            {
                await HandleTerminalHealthAsync(slot, generation).ConfigureAwait(false);
            }
        }
    }




    private async Task WithdrawAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        if (!_slots.TryGetValue(serviceId, out var slot))
        {
            return;
        }

        ServiceGeneration? generation;
        lock (slot.Gate)
        {
            generation = slot.Active;
            slot.Active = null;
            slot.Startup = null;
        }
        if (generation is not null)
        {
            PublishRuntimeSnapshot(
                generation,
                lifecycleState: ExtensionServiceLifecycleState.Stopping,
                preserveServiceVersion: true);

        }
        if (generation is not null)
        {
            await StopOrReleaseGenerationAfterExitAsync(slot, generation, cancellationToken).ConfigureAwait(false);
            PublishServiceState(
                generation.Configuration.Id,
                generation.SnapshotVersion,
                "stopped");
            var currentSnapshot = _snapshotHolder.Current;
            var currentService = currentSnapshot?.Services.FirstOrDefault(value => value.Id == serviceId);
            if (currentSnapshot is not null && currentService is { Enabled: true } &&
                IsServiceEnabledForSnapshot(currentSnapshot, serviceId))
            {
                PublishConfiguredRuntimeState(
                    currentSnapshot,
                    currentService,
                    ExtensionServiceLifecycleState.Stopped);
            }
        }

        await PublishReadyEndpointsAsync().ConfigureAwait(false);
        SynchronizePublishedRuntimeConfiguration();
    }

    private async Task StopGenerationAsync(
        ServiceSlot slot,
        ServiceGeneration generation,
        CancellationToken cancellationToken)
    {
        generation.Ready = false;
        RemoveRuntimeEnvironment(generation);
        HostLogMessages.ServiceStopped(_logger, generation.Configuration.Id);
        try
        {
            await generation.Supervisor.StopAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HostLogMessages.LifecycleBackgroundCancelled(
                _logger,
                nameof(StopGenerationAsync),
                generation.Configuration.Id);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, nameof(StopGenerationAsync));
        }
    }

    /// <inheritdoc />
    public async Task PublishVerifiedEndpointsAsync(
        IReadOnlyList<HostServiceEndpointLease> dbLeases)
    {
        ArgumentNullException.ThrowIfNull(dbLeases);
        await _publicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
                return;
            }

            // The fresh-identity/stale-DB-read asymmetry is intentional; the unconditional 1s authoritative republish bounds omissions.
            var readyIdentities = GetActiveReadyLeases(DateTimeOffset.UtcNow)
                .Select(static lease => (lease.ServiceId, lease.Port))
                .ToHashSet();
            var verifiedLeases = new List<HostServiceEndpointLease>(dbLeases.Count);
            foreach (var lease in dbLeases)
            {
                if (lease is not null && readyIdentities.Contains((lease.ServiceId, lease.Port)))
                {
                    verifiedLeases.Add(lease);
                }
            }

            _endpointPublisher.Publish(verifiedLeases);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private ImmutableArray<PortLease> GetActiveReadyLeases(DateTimeOffset now)
    {
        var leases = ImmutableArray.CreateBuilder<PortLease>();
        foreach (var slot in _slots.Values)
        {
            lock (slot.Gate)
            {
                if (slot.Active is not { Ready: true, Lease: { } lease } || lease.IsExpired(now))
                {
                    continue;
                }

                leases.Add(lease);
            }
        }

        return leases.ToImmutable();
    }

    private async Task PublishReadyEndpointsAsync()
    {
        await _publicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                _endpointPublisher.Publish(Array.Empty<HostServiceEndpointLease>());
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var serviceOwners = _snapshotHolder.RoutingSnapshot?.ServiceOwners;
            var leases = new List<HostServiceEndpointLease>();
            foreach (var lease in GetActiveReadyLeases(now))
            {
                if (serviceOwners is null || !serviceOwners.TryGetValue(lease.ServiceId, out var owner))
                {
                    continue;
                }

                leases.Add(new HostServiceEndpointLease(lease.ServiceId, lease.Port, lease.ExpiresAt, owner));
            }

            _endpointPublisher.Publish(leases);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private sealed class ServiceSlot
    {
        internal readonly object Gate = new();
        internal ServiceGeneration? Active;
        internal ServiceGeneration? Starting;
        internal Task<HostServiceReadinessResult>? Startup;
        internal long StartupGeneration;
        internal int StartAttemptNumber;

        internal int ReserveStartAttemptNumber()
        {
            lock (Gate)
            {
                if (StartAttemptNumber < int.MaxValue)
                {
                    StartAttemptNumber++;
                }

                return StartAttemptNumber;
            }
        }

        internal void ObserveStartAttemptNumber(int attemptNumber)
        {
            lock (Gate)
            {
                if (attemptNumber > StartAttemptNumber)
                {
                    StartAttemptNumber = attemptNumber;
                }
            }
        }
    }

    private sealed class ServiceGeneration
    {
        private volatile PortLease? _lease;
        private volatile bool _ready;
        private volatile bool _processExitRecorded;
        private readonly object _restartAttemptsGate = new();
        private RestartAttemptState _lastPublishedRestartAttempts = RestartAttemptState.Empty;

        internal ServiceGeneration(
            ServiceConfiguration configuration,
            ServiceSupervisor supervisor,
            PortLease? lease,
            long snapshotVersion,
            HealthRetryState healthRetryState,
            string? ownerExtensionId,
            ImmutableDictionary<string, string> resolvedEnvironment,
            bool ready = true)
        {
            Configuration = configuration;
            Supervisor = supervisor;
            _lease = lease;
            SnapshotVersion = snapshotVersion;
            HealthRetryState = healthRetryState;
            OwnerExtensionId = ownerExtensionId;
            ResolvedEnvironment = resolvedEnvironment;
            _ready = ready;
        }

        internal ServiceConfiguration Configuration { get; }
        internal ServiceSupervisor Supervisor { get; }
        internal PortLease? Lease
        {
            get => _lease;
            set => _lease = value;
        }
        internal long SnapshotVersion { get; }
        internal HealthRetryState HealthRetryState { get; set; }
        internal ServiceStateReasonCode LastHealthProbeReason { get; set; }
        internal string? OwnerExtensionId { get; }
        internal ImmutableDictionary<string, string> ResolvedEnvironment { get; }
        internal bool Ready
        {
            get => _ready;
            set => _ready = value;
        }
        internal bool ProcessExitRecorded
        {
            get => _processExitRecorded;
            set => _processExitRecorded = value;
        }
        internal int? ProcessExitCode { get; set; }
        internal int RecordRestartAttemptDelta(RestartAttemptState current)
        {
            if (current.Attempts == 0 || current.LastAttemptAt is not { } attemptAt)
            {
                return 0;
            }

            lock (_restartAttemptsGate)
            {
                var previous = _lastPublishedRestartAttempts;
                if (previous.LastAttemptAt is { } previousAttemptAt && attemptAt <= previousAttemptAt)
                {
                    return 0;
                }

                var increment = previous.LastAttemptAt is null || current.WindowStartedAt != previous.WindowStartedAt
                    ? current.Attempts
                    : Math.Max(0, current.Attempts - previous.Attempts);
                _lastPublishedRestartAttempts = current;
                return increment;
            }
        }


    }
}
