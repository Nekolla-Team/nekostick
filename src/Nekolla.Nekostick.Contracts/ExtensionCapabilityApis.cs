using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the result category of an extension-owned service operation.</summary>
public enum ExtensionServiceOperationCode
{
    /// <summary>No operation result was assigned.</summary>
    None,

    /// <summary>The requested operation was accepted.</summary>
    Accepted,

    /// <summary>The requested service was not found or is not owned by the caller.</summary>
    NotFound,

    /// <summary>The operation conflicted with the current service state or version.</summary>
    Conflict,

    /// <summary>The operation is not supported for this service.</summary>
    Unsupported,

    /// <summary>The operation was cancelled before completion.</summary>
    Cancelled,

    /// <summary>The operation failed safely.</summary>
    Failed,

    /// <summary>The service was already stopped.</summary>
    AlreadyStopped,

    /// <summary>The operation was requested reentrantly from an active extension callback.</summary>
    Reentrant
}

/// <summary>Contains the safe result of an extension-owned service operation.</summary>
public sealed record ExtensionServiceOperationResult
{
    /// <summary>Creates a service operation result.</summary>
    /// <param name="succeeded">Whether the operation completed successfully; it must be <see langword="true" /> exactly when <paramref name="code" /> is <see cref="ExtensionServiceOperationCode.Accepted" />.</param>
    /// <param name="code">The stable result category; <see cref="ExtensionServiceOperationCode.Accepted" /> indicates success and every other code indicates failure.</param>
    /// <param name="serviceId">The affected service identifier.</param>
    /// <param name="detail">The required precise cause or additional context on failure; it must be <see langword="null" /> on success.</param>
    /// <exception cref="ArgumentException">The success flag is inconsistent with <paramref name="code" />, or a successful result includes <paramref name="detail" />.</exception>
    /// <exception cref="ArgumentNullException">The operation failed but <paramref name="detail" /> is <see langword="null" />.</exception>
    public ExtensionServiceOperationResult(
        bool succeeded,
        ExtensionServiceOperationCode code,
        Guid serviceId,
        ExtensionErrorDetail? detail = null)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (succeeded != (code == ExtensionServiceOperationCode.Accepted))
        {
            throw new ArgumentException("The service operation result is inconsistent.");
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
        Detail = detail;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable operation result category.</summary>
    public ExtensionServiceOperationCode Code { get; }

    /// <summary>Gets the affected service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the required precise cause or additional context for a failed operation, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? Detail { get; }
}
/// <summary>Provides owned route CRUD convenience operations for an extension.</summary>
/// <remarks>The host binds the caller identity; route targets are restricted to the caller's handlers and services.</remarks>
public interface IExtensionRouteApi
{
    /// <summary>Reads the caller's owned routes.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The immutable routes or safe errors.</returns>
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts or replaces one caller-owned route.</summary>
    /// <param name="expectedVersion">The caller's expected global configuration version.</param>
    /// <param name="route">The restricted route configuration.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically removes one caller-owned route.</summary>
    /// <param name="expectedVersion">The caller's expected global configuration version.</param>
    /// <param name="routeId">The caller-owned route identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid routeId,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides owned service CRUD and bounded service lifecycle operations.</summary>
/// <remarks>The host binds the caller identity and never exposes environment secrets, supervisor objects, or process handles.</remarks>
public interface IExtensionServiceApi
{
    /// <summary>Reads the caller's owned services without environment secrets.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The immutable services or safe errors.</returns>
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts or replaces one caller-owned service.</summary>
    /// <param name="expectedVersion">The caller's expected global configuration version.</param>
    /// <param name="service">The extension-safe service configuration.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionServiceConfiguration service,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically removes one caller-owned service.</summary>
    /// <param name="expectedVersion">The caller's expected global configuration version.</param>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests start of one caller-owned service.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result; domain failures are not thrown as host exceptions.</returns>
    ValueTask<ExtensionServiceOperationResult> StartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests stop of one caller-owned service.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result; domain failures are not thrown as host exceptions.</returns>
    ValueTask<ExtensionServiceOperationResult> StopAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests restart of one caller-owned service.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe operation result; domain failures are not thrown as host exceptions.</returns>
    ValueTask<ExtensionServiceOperationResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains a safe immutable published lease for an extension-owned service.</summary>
public sealed record ExtensionEndpointLease
{
    /// <summary>Creates an endpoint lease DTO.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="generationId">The UUID v7 service generation identifier.</param>
    /// <param name="port">The loopback port assigned by the host.</param>
    /// <param name="expiresAt">The UTC lease expiration time.</param>
    public ExtensionEndpointLease(Guid serviceId, Guid generationId, int port, DateTimeOffset expiresAt)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        GenerationId = IdentityValidation.RequireUuidV7(generationId, nameof(generationId));
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    /// <summary>Gets the caller-owned service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the UUID v7 service generation identifier.</summary>
    public Guid GenerationId { get; }

    /// <summary>Gets the host-assigned loopback port.</summary>
    public int Port { get; }

    /// <summary>Gets the UTC lease expiration time.</summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>Provides read-only access to the caller's published service endpoint leases.</summary>
public interface IExtensionEndpointApi
{
    /// <summary>Gets the current immutable endpoint lease snapshot.</summary>
    ImmutableArray<ExtensionEndpointLease> Current { get; }

    /// <summary>Resolves one caller-owned service endpoint lease.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A typed result containing the lease on success, or a failure code with its required precise detail.</returns>
    ValueTask<ExtensionEndpointResolutionResult> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies the result category of an extension self-lifecycle operation.</summary>
public enum ExtensionLifecycleOperationCode
{
    /// <summary>No operation result was assigned.</summary>
    None,

    /// <summary>The requested operation was accepted.</summary>
    Accepted,

    /// <summary>The extension was not found or is not available.</summary>
    NotFound,

    /// <summary>The operation conflicted with the current lifecycle state.</summary>
    Conflict,

    /// <summary>The operation is not supported for this extension.</summary>
    Unsupported,

    /// <summary>The operation was cancelled before completion.</summary>
    Cancelled,

    /// <summary>The operation failed safely.</summary>
    Failed,

    /// <summary>The extension was already stopped.</summary>
    AlreadyStopped,

    /// <summary>The operation was requested reentrantly from an active extension callback.</summary>
    Reentrant
}

/// <summary>Identifies the last safe failure category reported by extension lifecycle status.</summary>
public enum ExtensionLifecycleFailureCode
{
    /// <summary>No failure was recorded.</summary>
    None,

    /// <summary>A lifecycle argument was invalid.</summary>
    InvalidArgument,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>The extension was already stopped.</summary>
    AlreadyStopped,

    /// <summary>The extension was not loaded.</summary>
    ExtensionNotLoaded,

    /// <summary>The extension runtime was unavailable.</summary>
    RuntimeUnavailable,

    /// <summary>The extension manifest was invalid.</summary>
    ManifestInvalid,

    /// <summary>Loading the extension failed.</summary>
    LoadFailed,

    /// <summary>An extension lifecycle callback failed.</summary>
    LifecycleFailed,

    /// <summary>Stopping the extension failed.</summary>
    StopFailed,

    /// <summary>An extension handler failed.</summary>
    HandlerFailed,

    /// <summary>An extension callback failed.</summary>
    CallbackFailed,

    /// <summary>A shared contract operation conflicted.</summary>
    ContractConflict,

    /// <summary>Extension registration conflicted.</summary>
    RegistrationConflict,

    /// <summary>A replacement was preserved after candidate failure.</summary>
    ReplacementPreserved,

    /// <summary>Collectible context unload was not confirmed.</summary>
    AlcUnloadUnconfirmed
}

/// <summary>Contains safe status information for the calling extension.</summary>
public sealed record ExtensionLifecycleStatus
{
    /// <summary>Creates an extension lifecycle status DTO.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="version">The installed extension version text.</param>
    /// <param name="state">The public extension load state.</param>
    /// <param name="handlerCount">The number of registered handlers.</param>
    /// <param name="hasFallback">Whether the extension owns the fallback registration.</param>
    /// <param name="activeRequests">The number of active handler requests.</param>
    /// <param name="activeTasks">The number of active extension tasks.</param>
    /// <param name="failureCount">The bounded failure count.</param>
    /// <param name="droppedEvents">The number of dropped events.</param>
    /// <param name="lastFailure">The last safe failure category.</param>
    /// <param name="lastFailureDetail">The precise cause or context when available; it must be <see langword="null" /> when <paramref name="lastFailure" /> is <see cref="ExtensionLifecycleFailureCode.None" />. Other failure codes may have no detail because the runtime does not yet provide a cause for every failure path.</param>
    /// <exception cref="ArgumentException"><paramref name="lastFailure" /> is <see cref="ExtensionLifecycleFailureCode.None" /> but <paramref name="lastFailureDetail" /> is not <see langword="null" />.</exception>
    public ExtensionLifecycleStatus(
        string extensionId,
        string version,
        ExtensionLoadState state,
        int handlerCount,
        bool hasFallback,
        int activeRequests,
        int activeTasks,
        int failureCount,
        long droppedEvents,
        ExtensionLifecycleFailureCode lastFailure,
        ExtensionErrorDetail? lastFailureDetail = null)
    {
        ExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        Version = string.IsNullOrWhiteSpace(version)
            ? throw new ArgumentException("An extension version is required.", nameof(version))
            : version;
        ArgumentOutOfRangeException.ThrowIfNegative(handlerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(activeRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(activeTasks);
        ArgumentOutOfRangeException.ThrowIfNegative(failureCount);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedEvents);
        if (lastFailure == ExtensionLifecycleFailureCode.None && lastFailureDetail is not null)
        {
            throw new ArgumentException("A status with no failure cannot include failure detail.", nameof(lastFailureDetail));
        }

        State = state;
        HandlerCount = handlerCount;
        HasFallback = hasFallback;
        ActiveRequests = activeRequests;
        ActiveTasks = activeTasks;
        FailureCount = failureCount;
        DroppedEvents = droppedEvents;
        LastFailure = lastFailure;
        LastFailureDetail = lastFailureDetail;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the installed extension version text.</summary>
    public string Version { get; }

    /// <summary>Gets the public extension load state.</summary>
    public ExtensionLoadState State { get; }

    /// <summary>Gets the number of registered handlers.</summary>
    public int HandlerCount { get; }

    /// <summary>Gets whether the extension owns the fallback registration.</summary>
    public bool HasFallback { get; }

    /// <summary>Gets the number of active handler requests.</summary>
    public int ActiveRequests { get; }

    /// <summary>Gets the number of active extension tasks.</summary>
    public int ActiveTasks { get; }

    /// <summary>Gets the bounded failure count.</summary>
    public int FailureCount { get; }

    /// <summary>Gets the number of dropped events.</summary>
    public long DroppedEvents { get; }

    /// <summary>Gets the last safe failure category.</summary>
    public ExtensionLifecycleFailureCode LastFailure { get; }

    /// <summary>Gets the cause or context when the runtime provides one; it is null when no failure was recorded, and may be absent for other failures.</summary>
    public ExtensionErrorDetail? LastFailureDetail { get; }
}

/// <summary>Contains the safe result of an extension self-lifecycle operation.</summary>
public sealed record ExtensionLifecycleOperationResult
{
    /// <summary>Creates a lifecycle operation result.</summary>
    /// <param name="succeeded">Whether the operation completed successfully; it must be <see langword="true" /> exactly when <paramref name="code" /> is <see cref="ExtensionLifecycleOperationCode.Accepted" />.</param>
    /// <param name="code">The stable result category; <see cref="ExtensionLifecycleOperationCode.Accepted" /> indicates success and every other code indicates failure.</param>
    /// <param name="status">The resulting status when available.</param>
    /// <param name="detail">The required precise cause or additional context on failure; it must be <see langword="null" /> on success.</param>
    /// <exception cref="ArgumentException">The success flag is inconsistent with <paramref name="code" />, or a successful result includes <paramref name="detail" />.</exception>
    /// <exception cref="ArgumentNullException">The operation failed but <paramref name="detail" /> is <see langword="null" />.</exception>
    public ExtensionLifecycleOperationResult(
        bool succeeded,
        ExtensionLifecycleOperationCode code,
        ExtensionLifecycleStatus? status,
        ExtensionErrorDetail? detail = null)
    {
        if (succeeded != (code == ExtensionLifecycleOperationCode.Accepted))
        {
            throw new ArgumentException("The lifecycle operation result is inconsistent.");
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
        Status = status;
        Detail = detail;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable operation result category.</summary>
    public ExtensionLifecycleOperationCode Code { get; }

    /// <summary>Gets the resulting safe status when available.</summary>
    public ExtensionLifecycleStatus? Status { get; }

    /// <summary>Gets the required precise cause or additional context for a failed operation, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? Detail { get; }
}

/// <summary>Provides bridge-scoped self lifecycle observation and requests.</summary>
public interface IExtensionLifecycleApi
{
    /// <summary>Gets the current safe status, or <see langword="null" /> when unavailable.</summary>
    ExtensionLifecycleStatus? Status { get; }

    /// <summary>Requests a reload of the calling extension only.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result; reentrant calls return <c>Reentrant</c> without waiting.</returns>
    ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Requests an unload of the calling extension only.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result; reentrant calls return <c>Reentrant</c> without waiting.</returns>
    ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(
        CancellationToken cancellationToken = default);
}
