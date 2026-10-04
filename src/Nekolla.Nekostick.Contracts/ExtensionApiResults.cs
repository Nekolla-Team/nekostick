using System.Diagnostics.CodeAnalysis;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies why publishing one extension event was rejected.</summary>
public enum ExtensionEventPublishFailureCode
{
    /// <summary>The event argument was <see langword="null" />.</summary>
    InvalidArgument,

    /// <summary>The bounded queue was full and dropped the newest event.</summary>
    QueueFull,

    /// <summary>The event queue had stopped accepting events.</summary>
    Unavailable
}

/// <summary>Contains the result of publishing one extension event.</summary>
/// <remarks>Successful publication returns a cached singleton; failures allocate a result subtype.</remarks>
public abstract class ExtensionEventPublishResult
{
    private protected ExtensionEventPublishResult()
    {
    }

    /// <summary>Gets whether the event was accepted by the queue.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionEventPublishResult Success { get; } = new SuccessResult();

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason publication was rejected.</param>
    /// <param name="detail">The required precise cause, exception information, or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionEventPublishResult Failure(
        ExtensionEventPublishFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionEventPublishFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionEventPublishResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected event publication.</summary>
public sealed class ExtensionEventPublishFailureResult : ExtensionEventPublishResult
{
    internal ExtensionEventPublishFailureResult(
        ExtensionEventPublishFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason event publication was rejected.</summary>
    public ExtensionEventPublishFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension event subscription was rejected.</summary>
public enum ExtensionEventSubscribeFailureCode
{
    /// <summary>The callback argument was <see langword="null" />.</summary>
    InvalidArgument,

    /// <summary>The event queue had stopped accepting subscriptions.</summary>
    Unavailable
}

/// <summary>Contains the result of subscribing to the extension event stream.</summary>
/// <remarks>Successful subscription returns a cached singleton; failures allocate a result subtype.</remarks>
public abstract class ExtensionEventSubscribeResult
{
    private protected ExtensionEventSubscribeResult()
    {
    }

    /// <summary>Gets whether the callback was accepted by the queue.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionEventSubscribeResult Success { get; } = new SuccessResult();

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason subscription was rejected.</param>
    /// <param name="detail">The required precise cause, exception information, or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionEventSubscribeResult Failure(
        ExtensionEventSubscribeFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionEventSubscribeFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionEventSubscribeResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected event subscription.</summary>
public sealed class ExtensionEventSubscribeFailureResult : ExtensionEventSubscribeResult
{
    internal ExtensionEventSubscribeFailureResult(
        ExtensionEventSubscribeFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason event subscription was rejected.</summary>
    public ExtensionEventSubscribeFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension handler or fallback registration was rejected.</summary>
public enum ExtensionRegistrationFailureCode
{
    /// <summary>A required registration argument or handler identifier was invalid.</summary>
    InvalidArgument,

    /// <summary>The identifier or fallback conflicts with an existing or tombstoned registration.</summary>
    Conflict,

    /// <summary>The requested handler or fallback was not registered when unregister was attempted.</summary>
    NotFound,

    /// <summary>The implementation does not support the requested registration operation.</summary>
    Unsupported
}

/// <summary>Contains the result of registering or unregistering one extension handler or fallback.</summary>
/// <remarks>Successful operations and unsupported failures return cached singletons; other failures allocate a result subtype.</remarks>
public abstract class ExtensionRegistrationResult
{
    private protected ExtensionRegistrationResult()
    {
    }

    /// <summary>Gets whether the registration operation succeeded.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionRegistrationResult Success { get; } = new SuccessResult();

    private static readonly ExtensionRegistrationResult s_unsupported =
        new ExtensionRegistrationFailureResult(
            ExtensionRegistrationFailureCode.Unsupported,
            new ExtensionErrorDetail("The registration operation is unsupported."));

    /// <summary>Gets the cached unsupported failure with constant detail.</summary>
    public static ExtensionRegistrationResult Unsupported => s_unsupported;

    private static readonly ExtensionRegistrationResult s_streamingUnsupported =
        new ExtensionRegistrationFailureResult(
            ExtensionRegistrationFailureCode.Unsupported,
            new ExtensionErrorDetail("Streaming handler registration is not supported by this IExtensionRegistration implementation."));

    internal static ExtensionRegistrationResult StreamingUnsupported => s_streamingUnsupported;

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason registration or unregistration was rejected.</param>
    /// <param name="detail">The required precise cause or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionRegistrationResult Failure(
        ExtensionRegistrationFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionRegistrationFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionRegistrationResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected registration operation.</summary>
public sealed class ExtensionRegistrationFailureResult : ExtensionRegistrationResult
{
    internal ExtensionRegistrationFailureResult(
        ExtensionRegistrationFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason registration or unregistration was rejected.</summary>
    public ExtensionRegistrationFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why a route subscription or hook registration was rejected.</summary>
public enum ExtensionRouteRegistrationFailureCode
{
    /// <summary>A required callback argument was invalid.</summary>
    InvalidArgument,

    /// <summary>The hook stage was not a supported trigger or return stage.</summary>
    InvalidStage,

    /// <summary>The maximum number of registrations for the generation was reached.</summary>
    LimitReached,

    /// <summary>The generation was retired or the event queue was unavailable.</summary>
    Unavailable,

    /// <summary>The route subscription and hook registration capability is unsupported.</summary>
    Unsupported
}

/// <summary>Contains the result of a route event subscription or hook registration.</summary>
/// <remarks>Successful registrations return a cached singleton; failures allocate a result subtype.</remarks>
public abstract class ExtensionRouteRegistrationResult
{
    private protected ExtensionRouteRegistrationResult()
    {
    }

    /// <summary>Gets whether the route registration succeeded.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionRouteRegistrationResult Success { get; } = new SuccessResult();

    private static readonly ExtensionRouteRegistrationResult s_unsupported =
        new ExtensionRouteRegistrationFailureResult(
            ExtensionRouteRegistrationFailureCode.Unsupported,
            new ExtensionErrorDetail("Route subscriptions and hook registrations are unsupported."));

    /// <summary>Gets the cached unsupported failure with constant detail.</summary>
    public static ExtensionRouteRegistrationResult Unsupported => s_unsupported;

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason the route registration was rejected.</param>
    /// <param name="detail">The required precise cause or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionRouteRegistrationResult Failure(
        ExtensionRouteRegistrationFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionRouteRegistrationFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionRouteRegistrationResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected route registration.</summary>
public sealed class ExtensionRouteRegistrationFailureResult : ExtensionRouteRegistrationResult
{
    internal ExtensionRouteRegistrationFailureResult(
        ExtensionRouteRegistrationFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the route registration was rejected.</summary>
    public ExtensionRouteRegistrationFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension contract export was rejected.</summary>
public enum ExtensionContractExportFailureCode
{
    /// <summary>The contract identifier or implementation argument was invalid.</summary>
    InvalidArgument,

    /// <summary>Contract startup exchange was closed or the registry was disposed.</summary>
    Unavailable,

    /// <summary>The contract identifier was not declared as an export.</summary>
    NotDeclared,

    /// <summary>The implementation type did not match the declared contract type identity.</summary>
    TypeMismatch,

    /// <summary>An implementation for this contract had already been exported.</summary>
    Conflict
}

/// <summary>Contains the result of exporting one declared extension contract.</summary>
/// <remarks>Successful exports return a cached singleton; failures allocate a result subtype.</remarks>
public abstract class ExtensionContractExportResult
{
    private protected ExtensionContractExportResult()
    {
    }

    /// <summary>Gets whether the implementation was exported.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionContractExportResult Success { get; } = new SuccessResult();

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason the export was rejected.</param>
    /// <param name="detail">The required precise cause, exception information, or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionContractExportResult Failure(
        ExtensionContractExportFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionContractExportFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionContractExportResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected contract export.</summary>
public sealed class ExtensionContractExportFailureResult : ExtensionContractExportResult
{
    internal ExtensionContractExportFailureResult(
        ExtensionContractExportFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the export was rejected.</summary>
    public ExtensionContractExportFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension contract import was rejected.</summary>
public enum ExtensionContractImportFailureCode
{
    /// <summary>The contract identifier argument was invalid.</summary>
    InvalidArgument,

    /// <summary>Contract startup exchange was closed or the registry was disposed.</summary>
    Unavailable,

    /// <summary>The contract identifier was not declared as an import.</summary>
    NotDeclared,

    /// <summary>The requested type did not match the declared contract type identity.</summary>
    TypeMismatch,

    /// <summary>The import's version range was not satisfied by the available export.</summary>
    VersionMismatch,

    /// <summary>No compatible provider implementation was available to resolve.</summary>
    ProviderUnavailable,

    /// <summary>The manifest-declared dependency is not currently satisfied; the accompanying <c>ExtensionErrorDetail</c> names the actual dependency state versus the requirement.</summary>
    DependencyUnsatisfied
}

/// <summary>Contains the result of importing one declared extension contract.</summary>
/// <typeparam name="TContract">The approved shared contract type.</typeparam>
/// <remarks>Import success allocates a payload-carrying subtype; failures allocate a failure subtype.</remarks>
public abstract class ExtensionContractImportResult<TContract>
    where TContract : class
{
    private protected ExtensionContractImportResult()
    {
    }

    /// <summary>Gets whether a compatible contract implementation was imported.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Creates a success subtype carrying the imported contract object.</summary>
    /// <param name="contract">The non-null imported implementation.</param>
    /// <returns>A newly allocated success subtype carrying <paramref name="contract" />, exposed through the result base type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract" /> is <see langword="null" />.</exception>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "The generic result factory keeps construction type-safe at the public contract boundary.")]
    public static ExtensionContractImportResult<TContract> Success(TContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new ExtensionContractImportSuccessResult<TContract>(contract);
    }

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason the import was rejected.</param>
    /// <param name="detail">The required precise cause, exception information, or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "The generic result factory keeps construction type-safe at the public contract boundary.")]
    public static ExtensionContractImportResult<TContract> Failure(
        ExtensionContractImportFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionContractImportFailureResult<TContract>(code, detail);
    }
}

/// <summary>Contains the reason and required detail for a rejected contract import.</summary>
/// <typeparam name="TContract">The approved shared contract type.</typeparam>
public sealed class ExtensionContractImportFailureResult<TContract> : ExtensionContractImportResult<TContract>
    where TContract : class
{
    internal ExtensionContractImportFailureResult(
        ExtensionContractImportFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the import was rejected.</summary>
    public ExtensionContractImportFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Contains an imported shared contract implementation.</summary>
/// <typeparam name="TContract">The approved shared contract type.</typeparam>
public sealed class ExtensionContractImportSuccessResult<TContract> : ExtensionContractImportResult<TContract>
    where TContract : class
{
    internal ExtensionContractImportSuccessResult(TContract contract)
    {
        Contract = contract;
    }

    /// <inheritdoc />
    public override bool Succeeded => true;

    /// <summary>Gets the imported contract implementation.</summary>
    public TContract Contract { get; }
}

/// <summary>Identifies why an extension task could not be started.</summary>
public enum ExtensionTaskStartFailureCode
{
    /// <summary>The task name or callback was invalid.</summary>
    InvalidTask,

    /// <summary>The maximum number of tracked tasks had already been reached.</summary>
    LimitReached,

    /// <summary>The extension task scheduler had stopped.</summary>
    Stopped
}

/// <summary>Contains the result of starting one tracked extension task.</summary>
/// <remarks>Successful starts return a cached singleton; failures allocate a result subtype.</remarks>
public abstract class ExtensionTaskStartResult
{
    private protected ExtensionTaskStartResult()
    {
    }

    /// <summary>Gets whether the task was accepted by the scheduler.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionTaskStartResult Success { get; } = new SuccessResult();

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason the task was not started.</param>
    /// <param name="detail">The required precise cause, exception information, or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionTaskStartResult Failure(
        ExtensionTaskStartFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionTaskStartFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionTaskStartResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected task start.</summary>
public sealed class ExtensionTaskStartFailureResult : ExtensionTaskStartResult
{
    internal ExtensionTaskStartFailureResult(
        ExtensionTaskStartFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the task was not started.</summary>
    public ExtensionTaskStartFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension reload could not be scheduled.</summary>
public enum ExtensionReloadScheduleFailureCode
{
    /// <summary>The extension identifier argument was invalid.</summary>
    InvalidArgument,

    /// <summary>The host does not support scheduling an extension reload.</summary>
    Unsupported,

    /// <summary>Extension configuration writes are disallowed.</summary>
    WritesDisallowed
}

/// <summary>Contains the result of scheduling one extension reload.</summary>
/// <remarks>Successful scheduling and fixed-cause failures return cached instances; other failures allocate a result subtype.</remarks>
public abstract class ExtensionReloadScheduleResult
{
    private protected ExtensionReloadScheduleResult()
    {
    }

    /// <summary>Gets whether the reload was accepted for scheduling.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Gets the cached success singleton; reading it allocates no result per successful call.</summary>
    public static ExtensionReloadScheduleResult Success { get; } = new SuccessResult();

    private static readonly ExtensionReloadScheduleResult s_invalidArgument =
        new ExtensionReloadScheduleFailureResult(
            ExtensionReloadScheduleFailureCode.InvalidArgument,
            new ExtensionErrorDetail("The extension identifier is invalid."));
    private static readonly ExtensionReloadScheduleResult s_unsupported =
        new ExtensionReloadScheduleFailureResult(
            ExtensionReloadScheduleFailureCode.Unsupported,
            new ExtensionErrorDetail("Extension reload scheduling is unsupported."));
    private static readonly ExtensionReloadScheduleResult s_writesDisallowed =
        new ExtensionReloadScheduleFailureResult(
            ExtensionReloadScheduleFailureCode.WritesDisallowed,
            new ExtensionErrorDetail("Extension configuration writes are disallowed."));

    /// <summary>Gets the cached invalid-argument failure with constant detail.</summary>
    public static ExtensionReloadScheduleResult InvalidArgument => s_invalidArgument;

    /// <summary>Gets the cached unsupported failure with constant detail.</summary>
    public static ExtensionReloadScheduleResult Unsupported => s_unsupported;

    /// <summary>Gets the cached writes-disallowed failure with constant detail.</summary>
    public static ExtensionReloadScheduleResult WritesDisallowed => s_writesDisallowed;

    /// <summary>Creates a failure result carrying the rejection code and required precise detail.</summary>
    /// <param name="code">The reason the reload was not scheduled.</param>
    /// <param name="detail">The required precise cause or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionReloadScheduleResult Failure(
        ExtensionReloadScheduleFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionReloadScheduleFailureResult(code, detail);
    }

    private sealed class SuccessResult : ExtensionReloadScheduleResult
    {
        /// <inheritdoc />
        public override bool Succeeded => true;
    }
}

/// <summary>Contains the reason and required detail for a rejected extension reload schedule.</summary>
public sealed class ExtensionReloadScheduleFailureResult : ExtensionReloadScheduleResult
{
    internal ExtensionReloadScheduleFailureResult(
        ExtensionReloadScheduleFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the reload was not scheduled.</summary>
    public ExtensionReloadScheduleFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Identifies why an extension endpoint lease could not be resolved.</summary>
public enum ExtensionEndpointResolutionFailureCode
{
    /// <summary>No active published endpoint lease is available to the caller for the service.</summary>
    NotFound,

    /// <summary>The Host endpoint runtime or snapshot accessor is unavailable.</summary>
    Unavailable,

    /// <summary>The endpoint-resolution capability is unsupported.</summary>
    Unsupported,

    /// <summary>The caller-owned endpoint lease has expired.</summary>
    Expired
}

/// <summary>Contains the result of resolving one caller-owned endpoint lease.</summary>
/// <remarks>Successful resolution allocates a payload-carrying subtype; cached failures are available for the standard resolution codes.</remarks>
public abstract class ExtensionEndpointResolutionResult
{
    private protected ExtensionEndpointResolutionResult()
    {
    }

    /// <summary>Gets whether a caller-owned endpoint lease was resolved.</summary>
    public abstract bool Succeeded { get; }

    private static readonly ExtensionEndpointResolutionResult s_notFound =
        new ExtensionEndpointResolutionFailureResult(
            ExtensionEndpointResolutionFailureCode.NotFound,
            new ExtensionErrorDetail("No active endpoint lease was found for the service."));
    private static readonly ExtensionEndpointResolutionResult s_unavailable =
        new ExtensionEndpointResolutionFailureResult(
            ExtensionEndpointResolutionFailureCode.Unavailable,
            new ExtensionErrorDetail("The endpoint runtime or snapshot accessor is unavailable."));
    private static readonly ExtensionEndpointResolutionResult s_unsupported =
        new ExtensionEndpointResolutionFailureResult(
            ExtensionEndpointResolutionFailureCode.Unsupported,
            new ExtensionErrorDetail("Endpoint resolution is unsupported."));
    private static readonly ExtensionEndpointResolutionResult s_expired =
        new ExtensionEndpointResolutionFailureResult(
            ExtensionEndpointResolutionFailureCode.Expired,
            new ExtensionErrorDetail("The endpoint lease has expired."));

    /// <summary>Gets the cached not-found failure with constant detail.</summary>
    public static ExtensionEndpointResolutionResult NotFound => s_notFound;

    /// <summary>Gets the cached unavailable failure with constant detail.</summary>
    public static ExtensionEndpointResolutionResult Unavailable => s_unavailable;

    /// <summary>Gets the cached unsupported failure with constant detail.</summary>
    public static ExtensionEndpointResolutionResult Unsupported => s_unsupported;

    /// <summary>Gets the cached expired failure with constant detail.</summary>
    public static ExtensionEndpointResolutionResult Expired => s_expired;

    /// <summary>Creates a success subtype carrying the resolved endpoint lease.</summary>
    /// <param name="lease">The resolved caller-owned endpoint lease.</param>
    /// <returns>A newly allocated success subtype carrying <paramref name="lease" />, exposed through the result base type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lease" /> is <see langword="null" />.</exception>
    public static ExtensionEndpointResolutionResult Success(ExtensionEndpointLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new ExtensionEndpointResolutionSuccessResult(lease);
    }

    /// <summary>Creates a failure result carrying the resolution code and required precise detail.</summary>
    /// <param name="code">The reason the endpoint could not be resolved.</param>
    /// <param name="detail">The required precise cause or additional context.</param>
    /// <returns>A newly allocated failure subtype carrying the required detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static ExtensionEndpointResolutionResult Failure(
        ExtensionEndpointResolutionFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new ExtensionEndpointResolutionFailureResult(code, detail);
    }
}

/// <summary>Contains the reason and required detail for a failed endpoint resolution.</summary>
public sealed class ExtensionEndpointResolutionFailureResult : ExtensionEndpointResolutionResult
{
    internal ExtensionEndpointResolutionFailureResult(
        ExtensionEndpointResolutionFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the endpoint lease could not be resolved.</summary>
    public ExtensionEndpointResolutionFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or extra context.</summary>
    public ExtensionErrorDetail Detail { get; }
}

/// <summary>Contains a successfully resolved caller-owned endpoint lease.</summary>
public sealed class ExtensionEndpointResolutionSuccessResult : ExtensionEndpointResolutionResult
{
    internal ExtensionEndpointResolutionSuccessResult(ExtensionEndpointLease lease)
    {
        Lease = lease;
    }

    /// <inheritdoc />
    public override bool Succeeded => true;

    /// <summary>Gets the resolved caller-owned endpoint lease.</summary>
    public ExtensionEndpointLease Lease { get; }
}
