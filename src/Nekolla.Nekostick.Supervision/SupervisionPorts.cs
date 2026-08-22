using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies one opaque process execution generation.</summary>
public readonly record struct ProcessInstanceId
{
    /// <summary>Creates an opaque process instance identifier.</summary>
    /// <param name="value">The non-empty identifier value.</param>
    public ProcessInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A process instance identifier is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the opaque identifier. It is not a process or group handle.</summary>
    public Guid Value { get; }
}

/// <summary>Identifies a safe result from a future process executor.</summary>
public enum ProcessOperationStatus
{
    /// <summary>The requested process operation was accepted.</summary>
    Accepted,

    /// <summary>The process operation completed with no running process.</summary>
    Completed,

    /// <summary>The process operation was rejected.</summary>
    Rejected,

    /// <summary>The process operation was cancelled.</summary>
    Cancelled,

    /// <summary>The process operation failed without exposing process output.</summary>
    Failed
}

/// <summary>Contains a safe process operation result with no process handle or output.</summary>
public sealed record ProcessOperationResult
{
    /// <summary>Creates a process operation result.</summary>
    /// <param name="status">The fixed status.</param>
    /// <param name="reason">The safe reason code.</param>
    /// <param name="instanceId">The opaque process generation, when a start was accepted.</param>
    /// <param name="processId">The operating-system process ID, when safely known.</param>
    /// <param name="startedAt">The executor-established UTC process start instant, when known.</param>
    public ProcessOperationResult(
        ProcessOperationStatus status,
        ServiceStateReasonCode reason,
        ProcessInstanceId? instanceId = null,
        int? processId = null,
        DateTimeOffset? startedAt = null)
    {
        if (processId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        Status = status;
        Reason = reason;
        InstanceId = instanceId;
        ProcessId = processId;
        StartedAt = startedAt?.ToUniversalTime();
    }

    /// <summary>Gets the fixed process operation status.</summary>
    public ProcessOperationStatus Status { get; }

    /// <summary>Gets the safe operation reason.</summary>
    public ServiceStateReasonCode Reason { get; }

    /// <summary>Gets the opaque process generation when a start was accepted.</summary>
    public ProcessInstanceId? InstanceId { get; }

    /// <summary>Gets the operating-system process ID when safely known.</summary>
    public int? ProcessId { get; }

    /// <summary>Gets the executor-established UTC process start instant when safely known.</summary>
    public DateTimeOffset? StartedAt { get; }
}

/// <summary>Contains one safe observation for a tracked process generation exit.</summary>
public sealed record ProcessExitObservation
{
    /// <summary>Creates a process exit observation without exposing process details.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="instanceId">The opaque process generation identifier.</param>
    /// <param name="successfulExit">Whether the helper exited successfully.</param>
    /// <param name="exitedAt">The UTC exit timestamp.</param>
    public ProcessExitObservation(
        Guid serviceId,
        ProcessInstanceId instanceId,
        bool successfulExit,
        DateTimeOffset exitedAt)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ServiceId = serviceId;
        InstanceId = instanceId;
        SuccessfulExit = successfulExit;
        ExitedAt = exitedAt.ToUniversalTime();
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the opaque process generation identifier.</summary>
    public ProcessInstanceId InstanceId { get; }

    /// <summary>Gets whether the helper exited successfully.</summary>
    public bool SuccessfulExit { get; }

    /// <summary>Gets the UTC exit timestamp.</summary>
    public DateTimeOffset ExitedAt { get; }
}

/// <summary>Defines the narrow generation-specific process exit observation boundary.</summary>
public interface IProcessExitObserver
{
    /// <summary>Subscribes to safe observations for tracked process generation exits.</summary>
    /// <param name="observer">The callback that receives each exit observation.</param>
    /// <returns>A subscription that removes the callback when disposed.</returns>
    IDisposable Subscribe(Action<ProcessExitObservation> observer);
}

/// <summary>Defines bounded cleanup for all processes owned by an executor.</summary>
public interface IProcessExecutorCleanup
{
    /// <summary>Signals and reaps every currently owned process generation.</summary>
    /// <param name="gracePeriod">The bounded graceful-stop period.</param>
    /// <param name="cancellationToken">The caller token, which does not cancel owned-process cleanup.</param>
    /// <returns>A task that completes after bounded cleanup attempts finish.</returns>
    ValueTask CleanupAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default);
}



/// <summary>Defines the narrow process execution boundary for later Host binding.</summary>
public interface IProcessExecutor
{
    /// <summary>Requests a process start without exposing a process handle.</summary>
    /// <param name="specification">The validated POSIX launch specification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe operation result.</returns>
    ValueTask<ProcessOperationResult> StartAsync(
        ProcessLaunchSpecification specification,
        CancellationToken cancellationToken = default);

    /// <summary>Requests a process-group stop through a future POSIX adapter.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="gracePeriod">The SIGTERM grace period before a future adapter may force termination.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe operation result.</returns>
    ValueTask<ProcessOperationResult> StopAsync(
        Guid serviceId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);
}

/// <summary>Extends process execution with generation-specific ownership.</summary>
public interface IProcessInstanceExecutor : IProcessExecutor
{
    /// <summary>Stops exactly the identified process generation.</summary>
    /// <param name="instanceId">The opaque process generation identifier.</param>
    /// <param name="gracePeriod">The bounded SIGTERM grace period.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe operation result.</returns>
    ValueTask<ProcessOperationResult> StopAsync(
        ProcessInstanceId instanceId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);
}


/// <summary>Contains a validated health probe request.</summary>
public sealed record ServiceHealthProbeRequest
{
    /// <summary>Creates a health probe request without performing I/O.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="definition">The domain health-check definition.</param>
    /// <param name="endpoint">The loopback endpoint required by TCP and HTTP checks.</param>
    public ServiceHealthProbeRequest(
        Guid serviceId,
        HealthCheckDefinition definition,
        LoopbackEndpoint? endpoint = null)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentNullException.ThrowIfNull(definition);
        if ((definition.Kind is ServiceHealthCheckKind.Tcp or ServiceHealthCheckKind.Http) && !endpoint.HasValue)
        {
            throw new ArgumentException("A loopback endpoint is required for this health check.", nameof(endpoint));
        }

        ServiceId = serviceId;
        Definition = definition;
        Endpoint = endpoint;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the immutable health-check definition.</summary>
    public HealthCheckDefinition Definition { get; }

    /// <summary>Gets the optional validated loopback endpoint.</summary>
    public LoopbackEndpoint? Endpoint { get; }
}

/// <summary>Defines the narrow health observation boundary for later Host binding.</summary>
public interface IServiceHealthProbe
{
    /// <summary>Performs one future health observation.</summary>
    /// <param name="request">The validated probe request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe result containing no endpoint, command, environment, or process output.</returns>
    ValueTask<HealthObservationResult> ProbeAsync(
        ServiceHealthProbeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Documents the intentionally deferred persistence integration boundary.</summary>
public static class SupervisionIntegrationNotes
{
    /// <summary>Gets the fixed integration note for the persistence adapter.</summary>
    public const string DeferredBinding =
        "Persistence lease binding remains deferred to Host wiring; concrete process and health adapters own bounded runtime I/O and publish only fixed safe results.";
}
