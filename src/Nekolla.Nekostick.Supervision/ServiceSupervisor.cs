using System.Threading;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies the safe outcome of one supervisor operation.</summary>
public enum SupervisorOperationStatus
{
    /// <summary>The operation completed and its requested effect was applied.</summary>
    Applied,
    /// <summary>The operation was rejected by validation or policy.</summary>
    Rejected,
    /// <summary>The operation could not proceed because a required resource was unavailable.</summary>
    Unavailable,
    /// <summary>The operation conflicted with another operation or lease.</summary>
    Conflict,
    /// <summary>The operation was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The operation failed while communicating with an adapter.</summary>
    Failed
}

/// <summary>Contains the fixed outcome and resulting state of one supervisor operation.</summary>
public sealed record SupervisorOperationResult
{
    /// <summary>Creates a supervisor operation result.</summary>
    /// <param name="status">The fixed operation status.</param>
    /// <param name="reason">The safe reason code describing the outcome.</param>
    /// <param name="snapshot">The immutable lifecycle snapshot after the operation.</param>
    /// <param name="lease">The lease associated with the operation, when available.</param>
    /// <param name="restart">The restart plan, when one was produced.</param>
    /// <param name="health">The health retry decision, when one was produced.</param>
    public SupervisorOperationResult(
        SupervisorOperationStatus status,
        ServiceStateReasonCode reason,
        ServiceRuntimeSnapshot snapshot,
        PortLease? lease = null,
        RestartPlan? restart = null,
        HealthRetryDecision? health = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Status = status;
        Reason = reason;
        Snapshot = snapshot;
        Lease = lease;
        Restart = restart;
        Health = health;
    }

    /// <summary>Gets the fixed operation status.</summary>
    public SupervisorOperationStatus Status { get; }

    /// <summary>Gets the safe reason code describing the outcome.</summary>
    public ServiceStateReasonCode Reason { get; }

    /// <summary>Gets the immutable lifecycle snapshot after the operation.</summary>
    public ServiceRuntimeSnapshot Snapshot { get; }

    /// <summary>Gets the lease associated with the operation, when available.</summary>
    public PortLease? Lease { get; }

    /// <summary>Gets the restart plan, when one was produced.</summary>
    public RestartPlan? Restart { get; }

    /// <summary>Gets the health retry decision, when one was produced.</summary>
    public HealthRetryDecision? Health { get; }
}

/// <summary>
/// Coordinates one service's process, health, restart, and node-owned lease operations.
/// The adapters perform I/O; this type publishes only immutable snapshots and fixed codes.
/// </summary>
public sealed partial class ServiceSupervisor : IAsyncDisposable
{
    private readonly IProcessExecutor processExecutor;
    private readonly IServiceHealthProbe healthProbe;
    private readonly IPortLeaseStore leaseStore;
    private readonly ProcessLaunchSpecification launchSpecification;
    private readonly ServiceHealthProbeRequest healthRequest;
    private readonly PortLeaseRequest leaseRequest;
    private readonly HealthRetryPolicy healthPolicy;
    private readonly RestartBackoffPolicy restartBackoff;
    private readonly IRestartJitter restartJitter;
    private readonly ServiceRestartPolicy restartPolicy;
    private readonly TimeSpan stopGracePeriod;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private ServiceRuntimeSnapshot snapshot;
    private PortLease? lease;
    private bool initialLeasePending;
    private ProcessInstanceHolder? processInstance;

    /// <summary>Creates a deterministic supervisor for one validated service definition.</summary>
    /// <param name="processExecutor">The process adapter used to start and stop the service.</param>
    /// <param name="healthProbe">The health adapter used to observe the service.</param>
    /// <param name="leaseStore">The persistence boundary used to acquire and release the port lease.</param>
    /// <param name="launchSpecification">The immutable process launch specification.</param>
    /// <param name="healthRequest">The immutable health probe request.</param>
    /// <param name="leaseRequest">The immutable port lease request.</param>
    /// <param name="healthPolicy">The bounded health retry policy, or the default policy when omitted.</param>
    /// <param name="restartBackoff">The restart backoff policy, or the default policy when omitted.</param>
    /// <param name="restartJitter">The restart jitter provider, or a deterministic no-jitter provider when omitted.</param>
    /// <param name="restartPolicy">The policy governing process restarts.</param>
    /// <param name="stopGracePeriod">The maximum graceful stop duration, or five seconds when omitted.</param>
    /// <param name="now">The construction time used to validate the optional initial lease.</param>
    /// <param name="initialLease">The optional validated lease acquired by the Host before construction.</param>
    public ServiceSupervisor(
        IProcessExecutor processExecutor,
        IServiceHealthProbe healthProbe,
        IPortLeaseStore leaseStore,
        ProcessLaunchSpecification launchSpecification,
        ServiceHealthProbeRequest healthRequest,
        PortLeaseRequest leaseRequest,
        HealthRetryPolicy? healthPolicy = null,
        RestartBackoffPolicy? restartBackoff = null,
        IRestartJitter? restartJitter = null,
        ServiceRestartPolicy restartPolicy = ServiceRestartPolicy.OnFailure,
        TimeSpan? stopGracePeriod = null,
        DateTimeOffset? now = null,
        PortLease? initialLease = null)
    {
        this.processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
        this.healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        this.leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        this.launchSpecification = launchSpecification ?? throw new ArgumentNullException(nameof(launchSpecification));
        this.healthRequest = healthRequest ?? throw new ArgumentNullException(nameof(healthRequest));
        this.leaseRequest = leaseRequest ?? throw new ArgumentNullException(nameof(leaseRequest));
        if (launchSpecification.ServiceId != healthRequest.ServiceId || launchSpecification.ServiceId != leaseRequest.ServiceId)
        {
            throw new ArgumentException("Supervisor inputs must identify one service.");
        }
        if ((healthRequest.Definition.Kind is ServiceHealthCheckKind.Tcp or ServiceHealthCheckKind.Http) &&
            healthRequest.Endpoint is { } endpoint && endpoint.Port != leaseRequest.Port)
        {
            throw new ArgumentException("The health endpoint must use the leased service port.", nameof(healthRequest));
        }
        var initialNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (initialLease is not null &&
            (initialLease.NodeId != leaseRequest.NodeId ||
             initialLease.ServiceId != leaseRequest.ServiceId ||
             initialLease.Port != leaseRequest.Port ||
             initialLease.IsExpired(initialNow)))
        {
            throw new ArgumentException("The initial lease does not match the supervisor request or is expired.", nameof(initialLease));
        }

        lease = initialLease;
        initialLeasePending = initialLease is not null;

        healthPolicy = healthPolicy ?? HealthRetryPolicy.Default;
        restartBackoff = restartBackoff ?? RestartBackoffPolicy.Default;
        restartJitter = restartJitter ?? new NoRestartJitter();
        if (stopGracePeriod is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopGracePeriod.Value, TimeSpan.Zero);
        }

        this.healthPolicy = healthPolicy;
        this.restartBackoff = restartBackoff;
        this.restartJitter = restartJitter;
        this.restartPolicy = restartPolicy;
        this.stopGracePeriod = stopGracePeriod ?? TimeSpan.FromSeconds(5);
        snapshot = ServiceStateTransition.CreateInitial(
            launchSpecification.ServiceId,
            DesiredServiceState.Stopped,
            initialNow);
    }

    /// <summary>Gets the latest immutable lifecycle snapshot.</summary>
    public ServiceRuntimeSnapshot Snapshot => Volatile.Read(ref snapshot);

    /// <summary>Gets the latest lease only when it has been safely acquired.</summary>
    public PortLease? Lease => Volatile.Read(ref lease);

    /// <summary>Changes desired state without performing adapter I/O.</summary>
    /// <param name="desired">The new desired lifecycle state.</param>
    /// <param name="now">The transition timestamp.</param>
    /// <returns>The resulting immutable lifecycle snapshot.</returns>
    public ServiceRuntimeSnapshot SetDesiredState(DesiredServiceState desired, DateTimeOffset now) =>
        Exchange(ServiceStateTransition.SetDesiredState(Snapshot, desired, now));
    /// <summary>Serializes process start with other lifecycle operations.</summary>
    /// <param name="now">The operation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    public async ValueTask<SupervisorOperationResult> StartAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (initialLeasePending)
            {
                initialLeasePending = false;
                await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            }

            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Snapshot);
        }

        try
        {
            return await StartCoreAsync(now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }


    /// <summary>Acquires the node-owned lease, then starts the process.</summary>
    /// <param name="now">The operation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async ValueTask<SupervisorOperationResult> StartCoreAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var current = Snapshot;
        if (current.Desired != DesiredServiceState.Running)
        {
            current = Exchange(ServiceStateTransition.SetDesiredState(current, DesiredServiceState.Running, now));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (initialLeasePending)
            {
                initialLeasePending = false;
                await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            }

            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Exchange(ServiceStateTransition.RecordStartCancelled(current, now)));
        }

        var heldLease = Volatile.Read(ref lease);
        var initialLeaseForStart = initialLeasePending;
        initialLeasePending = false;
        if (initialLeaseForStart && (heldLease is null || heldLease.IsExpired(now)))
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(
                SupervisorOperationStatus.Rejected,
                heldLease is null ? ServiceStateReasonCode.PortLeaseUnavailable : ServiceStateReasonCode.PortLeaseExpired,
                Exchange(ServiceStateTransition.RecordStartResult(Snapshot, false, now)));
        }

        PortLeaseOperationResult leaseResult;
        if (heldLease is not null &&
            heldLease.NodeId == leaseRequest.NodeId &&
            heldLease.ServiceId == leaseRequest.ServiceId &&
            heldLease.Port == leaseRequest.Port &&
            !heldLease.IsExpired(now))
        {
            leaseResult = new PortLeaseOperationResult(PortLeaseOperationStatus.Applied, heldLease);
        }
        else
        {
            if (heldLease is not null)
            {
                await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                leaseResult = await leaseStore.ApplyAsync(PortLeaseIntent.Acquire(leaseRequest), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Exchange(ServiceStateTransition.RecordStartCancelled(Snapshot, now)));
            }
            catch
            {
                return Result(SupervisorOperationStatus.Failed, ServiceStateReasonCode.DatabaseUnavailable, Snapshot);
            }
        }

        var returnedLease = leaseResult.Lease;
        var usableLease = leaseResult.Status == PortLeaseOperationStatus.Applied &&
            returnedLease is not null &&
            returnedLease.NodeId == leaseRequest.NodeId &&
            returnedLease.ServiceId == leaseRequest.ServiceId &&
            returnedLease.Port == leaseRequest.Port &&
            !returnedLease.IsExpired(now);
        if (!usableLease)
        {
            var reason = leaseResult.Status switch
            {
                PortLeaseOperationStatus.Conflict => ServiceStateReasonCode.PortLeaseConflict,
                PortLeaseOperationStatus.Cancelled => ServiceStateReasonCode.Cancelled,
                PortLeaseOperationStatus.DatabaseUnavailable => ServiceStateReasonCode.DatabaseUnavailable,
                _ => ServiceStateReasonCode.PortLeaseUnavailable
            };
            var status = leaseResult.Status switch
            {
                PortLeaseOperationStatus.Conflict => SupervisorOperationStatus.Conflict,
                PortLeaseOperationStatus.Cancelled => SupervisorOperationStatus.Cancelled,
                PortLeaseOperationStatus.Rejected => SupervisorOperationStatus.Rejected,
                _ => SupervisorOperationStatus.Unavailable
            };
            return Result(
                status,
                reason,
                Exchange(ServiceStateTransition.RecordStartResult(Snapshot, false, now)));
        }

        Volatile.Write(ref lease, leaseResult.Lease);
        var deadline = new ServiceDeadline(ServiceDeadlineKind.StartupHealth, now.ToUniversalTime().Add(healthPolicy.StartupTimeout));
        Exchange(ServiceStateTransition.RecordStartRequested(Snapshot, deadline, now));
        ProcessOperationResult processResult;
        try
        {
            processResult = await processExecutor.StartAsync(launchSpecification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Exchange(ServiceStateTransition.RecordStartCancelled(Snapshot, now)));
        }
        catch
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(SupervisorOperationStatus.Failed, ServiceStateReasonCode.StartRejected, Exchange(ServiceStateTransition.RecordStartResult(Snapshot, false, now)));
        }

        var accepted = processResult.Status is ProcessOperationStatus.Accepted;
        RememberProcessInstance(processResult, accepted);
        var next = Exchange(ServiceStateTransition.RecordStartResult(Snapshot, accepted, now));
        if (!accepted)
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
        }

        return Result(
            accepted ? SupervisorOperationStatus.Applied : SupervisorOperationStatus.Rejected,
            accepted ? ServiceStateReasonCode.StartAccepted : processResult.Reason,
            next,
            accepted ? Lease : null);
    }

    /// <summary>Serializes graceful process stop with other lifecycle operations.</summary>
    /// <param name="now">The operation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    public async ValueTask<SupervisorOperationResult> StopAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (initialLeasePending)
            {
                initialLeasePending = false;
                await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            }

            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Snapshot);
        }

        try
        {
            return await StopCoreAsync(now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Requests a graceful process stop and releases the lease after it completes.</summary>
    /// <param name="now">The operation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    private async ValueTask<SupervisorOperationResult> StopCoreAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requested = Exchange(ServiceStateTransition.RecordStopRequested(Snapshot, now));
        ProcessOperationResult result;
        try
        {
            result = await StopProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, requested);
        }
        catch
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(SupervisorOperationStatus.Failed, ServiceStateReasonCode.StopRequested, requested);
        }

        if (result.Status is ProcessOperationStatus.Cancelled)
        {
            await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, requested);
        }

        await ReleaseLeaseBestEffort(CancellationToken.None).ConfigureAwait(false);
        var stopped = Exchange(ServiceStateTransition.RecordStopped(Snapshot, now));
        return Result(
            result.Status is ProcessOperationStatus.Completed or ProcessOperationStatus.Accepted
                ? SupervisorOperationStatus.Applied
                : SupervisorOperationStatus.Failed,
            result.Status is ProcessOperationStatus.Completed or ProcessOperationStatus.Accepted
                ? ServiceStateReasonCode.StopCompleted
                : result.Reason,
            stopped);
    }

    /// <summary>Runs one bounded health observation and applies the immutable transition.</summary>
    /// <param name="retryState">The current health retry state.</param>
    /// <param name="now">The observation timestamp.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    public async ValueTask<SupervisorOperationResult> ObserveHealthAsync(
        HealthRetryState retryState,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (retryState.ServiceId != launchSpecification.ServiceId)
        {
            throw new ArgumentException("The health retry state belongs to another service.", nameof(retryState));
        }
        var publishedLease = Volatile.Read(ref lease);
        HealthObservationResult observation;
        ServiceStateReasonCode? leaseFailure = null;
        var usableLease = publishedLease is not null &&
            publishedLease.NodeId == leaseRequest.NodeId &&
            publishedLease.ServiceId == leaseRequest.ServiceId &&
            publishedLease.Port == leaseRequest.Port &&
            !publishedLease.IsExpired(now);
        if (!usableLease)
        {
            if (publishedLease is not null)
            {
                Interlocked.CompareExchange(ref lease, null, publishedLease);
            }

            leaseFailure = publishedLease is null ||
                publishedLease.NodeId != leaseRequest.NodeId ||
                publishedLease.ServiceId != leaseRequest.ServiceId ||
                publishedLease.Port != leaseRequest.Port
                ? ServiceStateReasonCode.PortLeaseUnavailable
                : ServiceStateReasonCode.PortLeaseExpired;
            observation = new HealthObservationResult(
                launchSpecification.ServiceId,
                HealthObservationStatus.Unavailable,
                now,
                TimeSpan.Zero,
                retryState.Attempt);
        }
        else
        {
            try
            {
                observation = await healthProbe.ProbeAsync(healthRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                observation = new HealthObservationResult(launchSpecification.ServiceId, HealthObservationStatus.Cancelled, now, TimeSpan.Zero, retryState.Attempt);
            }
            catch
            {
                observation = new HealthObservationResult(launchSpecification.ServiceId, HealthObservationStatus.Unavailable, now, TimeSpan.Zero, retryState.Attempt);
            }
        }

        var decision = healthPolicy.Decide(retryState, observation, now, cancellationToken);
        var next = Exchange(ServiceStateTransition.RecordHealthObservation(Snapshot, observation, healthPolicy.FailureThreshold, now));
        var status = leaseFailure is not null
            ? SupervisorOperationStatus.Unavailable
            : decision.Action switch
            {
                HealthRetryAction.Healthy or HealthRetryAction.Retry => SupervisorOperationStatus.Applied,
                HealthRetryAction.Cancelled => SupervisorOperationStatus.Cancelled,
                _ => SupervisorOperationStatus.Failed
            };
        return Result(status, leaseFailure ?? decision.Reason, next, leaseFailure is null ? Lease : null, health: decision);
    }


}

