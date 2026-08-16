using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

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
    public ProcessOperationResult(ProcessOperationStatus status, ServiceStateReasonCode reason)
    {
        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the fixed process operation status.</summary>
    public ProcessOperationStatus Status { get; }

    /// <summary>Gets the safe operation reason.</summary>
    public ServiceStateReasonCode Reason { get; }
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

/// <summary>Documents the intentionally deferred runtime integration boundaries.</summary>
public static class SupervisionIntegrationNotes
{
    /// <summary>Gets the fixed integration note for future adapters.</summary>
    public const string DeferredBinding =
        "Persistence and supervisor executor binding are deferred to Host wiring; this assembly performs no process, socket, database, HTTP, or file I/O.";
}
