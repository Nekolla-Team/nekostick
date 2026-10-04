namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the lifecycle stage where a service failure occurred.</summary>
public enum ExtensionServiceFailureStage
{
    /// <summary>No failure is currently recorded.</summary>
    None,

    /// <summary>The service failed before its process started.</summary>
    Spawn,

    /// <summary>A health probe failed or timed out.</summary>
    HealthProbe,

    /// <summary>The service process exited unexpectedly.</summary>
    ProcessExit
}

/// <summary>Identifies a safe, machine-readable service failure reason.</summary>
public enum ExtensionServiceFailureCode
{
    /// <summary>No failure reason is currently recorded.</summary>
    None,

    /// <summary>The process executor rejected the start request.</summary>
    StartRejected,

    /// <summary>The process launch specification could not be created.</summary>
    InvalidLaunchSpecification,

    /// <summary>A required host environment placeholder was unavailable.</summary>
    MissingHostEnvironment,

    /// <summary>The service executable is not currently available.</summary>
    ExecutableMissing,

    /// <summary>A required startup dependency is unavailable.</summary>
    DependencyUnavailable,

    /// <summary>The process port lease is unavailable.</summary>
    PortLeaseUnavailable,

    /// <summary>The runtime store is unavailable.</summary>
    RuntimeUnavailable,

    /// <summary>A health probe reported failure.</summary>
    HealthCheckFailed,

    /// <summary>A health probe exceeded its permitted duration.</summary>
    HealthTimeout,

    /// <summary>The service process exited unexpectedly.</summary>
    ProcessExited,

    /// <summary>The configured restart policy prevents another restart.</summary>
    RestartPolicyDisabled,

    /// <summary>The configured restart-attempt limit was reached.</summary>
    RestartLimitReached,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>The failure does not match a more specific code.</summary>
    Unknown
}

/// <summary>Identifies the result of a service health probe.</summary>
public enum ExtensionServiceProbeResult
{
    /// <summary>No probe result is available.</summary>
    Unknown,

    /// <summary>The health check succeeded.</summary>
    Healthy,

    /// <summary>The health check completed and reported failure.</summary>
    Unhealthy,

    /// <summary>The health check exceeded its permitted duration.</summary>
    TimedOut,

    /// <summary>The health check was cancelled.</summary>
    Cancelled,

    /// <summary>The health check could not be performed.</summary>
    Unavailable
}

/// <summary>Contains bounded details for one service health probe.</summary>
/// <remarks>
/// The target is safe diagnostic text. Probe transport failure messages are truncated to 512 characters before publication;
/// raw exception payloads from internal faults and process output are never included.
/// </remarks>
public sealed record ExtensionServiceProbeSnapshot
{
    /// <summary>Creates an immutable health probe snapshot.</summary>
    /// <param name="observedAt">The UTC time at which the probe completed.</param>
    /// <param name="result">The fixed probe outcome.</param>
    /// <param name="target">The loopback address or safe process target, when available.</param>
    /// <param name="failureCode">The safe failure code, or <see cref="ExtensionServiceFailureCode.None" />.</param>
    /// <param name="errorMessage">
    /// A bounded safe diagnostic; transport failure messages are truncated to 512 characters, and internal fault exception payloads are excluded.
    /// </param>
    public ExtensionServiceProbeSnapshot(
        DateTimeOffset observedAt,
        ExtensionServiceProbeResult result,
        string? target = null,
        ExtensionServiceFailureCode failureCode = ExtensionServiceFailureCode.None,
        string? errorMessage = null)
    {
        if (target is { Length: > 512 })
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        if (errorMessage is { Length: > 1024 })
        {
            throw new ArgumentOutOfRangeException(nameof(errorMessage));
        }

        ObservedAt = observedAt.ToUniversalTime();
        Result = result;
        Target = target;
        FailureCode = failureCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>Gets the UTC probe completion time.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets the fixed probe outcome.</summary>
    public ExtensionServiceProbeResult Result { get; }

    /// <summary>Gets the safe probe target, when available.</summary>
    public string? Target { get; }

    /// <summary>Gets the machine-readable probe failure code.</summary>
    public ExtensionServiceFailureCode FailureCode { get; }

    /// <summary>
    /// Gets bounded probe diagnostics; transport details are truncated to 512 characters,
    /// and internal fault exception payloads are excluded.
    /// </summary>
    public string? ErrorMessage { get; }
}

/// <summary>Identifies the kind of runtime-state notification.</summary>
public enum ExtensionServiceRuntimeStateChangeKind
{
    /// <summary>A service snapshot is available.</summary>
    Snapshot,

    /// <summary>The service is no longer configured.</summary>
    Removed
}

/// <summary>Contains one ordered runtime-state notification.</summary>
public sealed record ExtensionServiceRuntimeStateChange
{
    /// <summary>Creates an immutable runtime-state notification.</summary>
    /// <param name="serviceId">The stable service identifier.</param>
    /// <param name="sequence">The monotonically increasing node-local sequence number.</param>
    /// <param name="kind">The notification kind.</param>
    /// <param name="snapshot">The runtime snapshot, or null when the service was removed.</param>
    /// <param name="isInitialSnapshot">Whether the notification belongs to the initial replay.</param>
    /// <param name="ownerExtensionId">The service owner when known.</param>
    public ExtensionServiceRuntimeStateChange(
        Guid serviceId,
        long sequence,
        ExtensionServiceRuntimeStateChangeKind kind,
        ExtensionServiceRuntimeSnapshot? snapshot,
        bool isInitialSnapshot,
        string? ownerExtensionId = null)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (kind == ExtensionServiceRuntimeStateChangeKind.Snapshot && snapshot?.ServiceId != serviceId ||
            kind == ExtensionServiceRuntimeStateChangeKind.Removed && snapshot is not null)
        {
            throw new ArgumentException("The runtime-state notification is inconsistent.", nameof(snapshot));
        }

        Sequence = sequence;
        Kind = kind;
        Snapshot = snapshot;
        IsInitialSnapshot = isInitialSnapshot;
        OwnerExtensionId = ownerExtensionId ?? snapshot?.OwnerExtensionId;
    }

    /// <summary>Gets the stable service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the node-local monotonic sequence number.</summary>
    public long Sequence { get; }

    /// <summary>Gets the notification kind.</summary>
    public ExtensionServiceRuntimeStateChangeKind Kind { get; }

    /// <summary>Gets the current service snapshot, or null when the service was removed.</summary>
    public ExtensionServiceRuntimeSnapshot? Snapshot { get; }

    /// <summary>Gets whether this notification is part of the initial snapshot replay.</summary>
    public bool IsInitialSnapshot { get; }
    /// <summary>Gets the owning extension identifier when known.</summary>
    public string? OwnerExtensionId { get; }
}

/// <summary>Receives immutable runtime-state snapshots and removal notifications.</summary>
public interface IExtensionServiceRuntimeStateSink
{
    /// <summary>Receives one ordered runtime-state notification.</summary>
    /// <param name="change">The immutable snapshot or removal notification.</param>
    void OnStateChanged(ExtensionServiceRuntimeStateChange change);
}

/// <summary>Represents an extension-owned runtime-state subscription.</summary>
/// <remarks>
/// <see cref="IDisposable.Dispose" /> detaches the subscription on a best-effort basis; one callback may still be
/// in flight. <see cref="IAsyncDisposable.DisposeAsync" /> additionally awaits callback quiescence. It MUST NOT be
/// synchronously waited on from within a callback of the same subscription.
/// </remarks>
public interface IExtensionServiceRuntimeStateSubscription : IDisposable, IAsyncDisposable
{
}

/// <summary>Identifies the outcome of a runtime-state subscription request.</summary>
public enum ExtensionServiceRuntimeStateSubscriptionCode
{
    /// <summary>The subscription was created.</summary>
    Subscribed,

    /// <summary>The capability is unavailable for the negotiated API version.</summary>
    Unsupported,

    /// <summary>The sink or subscription request was invalid.</summary>
    InvalidArgument,

    /// <summary>The subscription could not be created.</summary>
    Failed
}

/// <summary>Contains the safe result of a runtime-state subscription request.</summary>
public sealed record ExtensionServiceRuntimeStateSubscriptionResult
{
    /// <summary>Creates a runtime-state subscription result.</summary>
    /// <param name="succeeded">Whether the subscription was created.</param>
    /// <param name="code">The fixed result code.</param>
    /// <param name="subscription">The subscription handle when successful.</param>
    /// <param name="detail">The required precise failure cause, or <see langword="null" /> on success.</param>
    public ExtensionServiceRuntimeStateSubscriptionResult(
        bool succeeded,
        ExtensionServiceRuntimeStateSubscriptionCode code,
        IExtensionServiceRuntimeStateSubscription? subscription,
        ExtensionErrorDetail? detail)
    {
        if (succeeded != (code == ExtensionServiceRuntimeStateSubscriptionCode.Subscribed) ||
            succeeded != (subscription is not null))
        {
            throw new ArgumentException("The runtime-state subscription result is inconsistent.");
        }

        if (succeeded && detail is not null)
        {
            throw new ArgumentException("A successful result cannot include error detail.", nameof(detail));
        }

        if (!succeeded && detail is null)
        {
            throw new ArgumentNullException(nameof(detail), "An unsuccessful result must include error detail.");
        }

        Succeeded = succeeded;
        Code = code;
        Subscription = subscription;
        Detail = detail;
    }

    /// <summary>Gets whether the subscription was created.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the fixed result code.</summary>
    public ExtensionServiceRuntimeStateSubscriptionCode Code { get; }

    /// <summary>Gets the subscription handle when the request succeeded.</summary>
    public IExtensionServiceRuntimeStateSubscription? Subscription { get; }

    /// <summary>Gets the precise failure cause, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? Detail { get; }

}

/// <summary>Provides node-local subscriptions to service runtime-state changes.</summary>
/// <remarks>
/// Notifications are delivered in sequence order. Pending snapshots for a service may be coalesced to the latest
/// snapshot while a callback is blocked; removal notifications are never coalesced away.
/// </remarks>
public interface IExtensionServiceRuntimeStateApi
{
    /// <summary>Subscribes to runtime-state changes and receives the current snapshots first.</summary>
    /// <param name="sink">The receiver for initial snapshots and subsequent changes.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result containing the caller-owned subscription when created.</returns>
    ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeStatesAsync(
        IExtensionServiceRuntimeStateSink sink,
        CancellationToken cancellationToken = default);
}
