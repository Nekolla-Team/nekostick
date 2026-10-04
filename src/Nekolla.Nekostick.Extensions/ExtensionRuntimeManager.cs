using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Describes the safe outcome of one extension handler dispatch.</summary>
public enum ExtensionInvocationState
{
    /// <summary>The handler returned a response.</summary>
    Handled,

    /// <summary>No fallback or handler handled the request.</summary>
    NotHandled,

    /// <summary>The requested extension target is unavailable.</summary>
    Unavailable,

    /// <summary>The extension callback failed safely.</summary>
    Failed
}

/// <summary>Contains a framework-neutral handler dispatch result.</summary>
public sealed class ExtensionInvocationResult
{
    private ExtensionInvocationResult(
        ExtensionInvocationState state,
        ExtensionHandlerResponse? response,
        ExtensionErrorDetail? failureDetail)
    {
        State = state;
        Response = response;
        FailureDetail = failureDetail;
    }

    /// <summary>Gets the dispatch outcome.</summary>
    public ExtensionInvocationState State { get; }

    /// <summary>Gets the response when the callback handled the request.</summary>
    public ExtensionHandlerResponse? Response { get; }

    /// <summary>Gets the precise failure cause, or <see langword="null" /> when no failure occurred.</summary>
    public ExtensionErrorDetail? FailureDetail { get; }

    /// <summary>Gets a safe not-handled result.</summary>
    public static ExtensionInvocationResult NotHandled { get; } =
        new(ExtensionInvocationState.NotHandled, null, null);

    internal static ExtensionInvocationResult Unavailable(ExtensionErrorDetail detail) =>
        new(ExtensionInvocationState.Unavailable, null, detail);

    internal static ExtensionInvocationResult Handled(ExtensionHandlerResponse response) =>
        new(ExtensionInvocationState.Handled, response, null);

    internal static ExtensionInvocationResult Failed(ExtensionErrorDetail detail) =>
        new(ExtensionInvocationState.Failed, null, detail);
}

/// <summary>Contains one safe extension runtime operation result.</summary>
public sealed class ExtensionRuntimeOperationResult
{
    private ExtensionRuntimeOperationResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionRuntimeStatus? status,
        ExtensionErrorDetail? failureDetail)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Status = status;
        FailureDetail = failureDetail;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the non-sensitive operation category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the resulting safe status when available.</summary>
    public ExtensionRuntimeStatus? Status { get; }

    /// <summary>Gets the precise operation failure cause, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? FailureDetail { get; }

    internal static ExtensionRuntimeOperationResult Success(ExtensionRuntimeStatus status) =>
        new(true, ExtensionFailureCode.None, status, null);

    internal static ExtensionRuntimeOperationResult Failure(
        ExtensionFailureCode code,
        ExtensionErrorDetail failureDetail,
        ExtensionRuntimeStatus? status = null) =>
        new(false, code, status, failureDetail);
    internal static ExtensionRuntimeOperationResult Failure(
        ExtensionFailureCode code,
        string failureMessage,
        ExtensionRuntimeStatus? status = null) =>
        Failure(code, new ExtensionErrorDetail(failureMessage), status);
}

/// <summary>Exposes safe observable state for one loaded extension.</summary>
public sealed record ExtensionRuntimeStatus
{
    /// <summary>Creates a safe runtime status.</summary>
    public ExtensionRuntimeStatus(
        string extensionId,
        string version,
        ExtensionLoadState state,
        int handlerCount,
        bool hasFallback,
        int activeRequests,
        int activeTasks,
        int failureCount,
        long droppedEvents,
        ExtensionFailureCode lastFailure,
        ExtensionStatusKind? reportedStatusKind = null,
        string? reportedStatusCode = null,
        ExtensionErrorDetail? lastFailureDetail = null)
    {
        ExtensionId = extensionId;
        Version = version;
        State = state;
        HandlerCount = handlerCount;
        HasFallback = hasFallback;
        ActiveRequests = activeRequests;
        ActiveTasks = activeTasks;
        FailureCount = failureCount;
        DroppedEvents = droppedEvents;
        LastFailure = lastFailure;
        ReportedStatusKind = reportedStatusKind;
        ReportedStatusCode = reportedStatusCode;
        LastFailureDetail = lastFailureDetail;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the loaded semantic version text.</summary>
    public string Version { get; }

    /// <summary>Gets the public extension state.</summary>
    public ExtensionLoadState State { get; }

    /// <summary>Gets the number of registered handlers.</summary>
    public int HandlerCount { get; }

    /// <summary>Gets whether this extension owns the fallback.</summary>
    public bool HasFallback { get; }

    /// <summary>Gets the number of active handler calls.</summary>
    public int ActiveRequests { get; }

    /// <summary>Gets the number of active tracked tasks.</summary>
    public int ActiveTasks { get; }

    /// <summary>Gets the number of failures in the rolling window.</summary>
    public int FailureCount { get; }

    /// <summary>Gets the number of newest events dropped by the bounded queue.</summary>
    public long DroppedEvents { get; }

    /// <summary>Gets the last safe failure category.</summary>
    public ExtensionFailureCode LastFailure { get; }
    /// <summary>Gets the precise cause of the latest runtime failure, when available.</summary>
    public ExtensionErrorDetail? LastFailureDetail { get; }

    /// <summary>Gets the latest status kind reported by the extension, or <see langword="null" /> when none was reported.</summary>
    public ExtensionStatusKind? ReportedStatusKind { get; }

    /// <summary>Gets the latest status code reported by the extension, or <see langword="null" /> when none was reported.</summary>
    public string? ReportedStatusCode { get; }
}

/// <summary>Runs explicit extension load, unload, reload, and handler operations.</summary>
public sealed partial class ExtensionRuntimeManager : IAsyncDisposable
{
    internal static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly CollectibleExtensionLoader _loader;
    private readonly HostApiVersion _hostApiVersion;
    private readonly ExtensionContractCatalog _contractCatalog;
    private readonly IExtensionCapabilityFactory? _capabilityFactory;
    private readonly ILogger? _logger;
    private readonly ExtensionLogThrottle _requestLogThrottle = new();
    private readonly string _dataDirectory;
    private readonly Dictionary<string, ExtensionInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionDispatchTurnstile> _turnstiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerBinding> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _dispatchLifetime = new();
    private HandlerBinding? _fallback;
    private bool _disposed;

    /// <summary>Creates an explicit-only runtime manager for one host API version and catalog.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <param name="contractCatalog">The immutable host-owned shared contract catalog.</param>
    /// <param name="capabilityFactory">The optional host-owned factory for extension capabilities.</param>
    /// <param name="logger">The optional host logger for lifecycle events.</param>
    /// <param name="dataDirectory">The host-configured directory exposed to API 1.3.2 extensions.</param>
    public ExtensionRuntimeManager(
        HostApiVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null,
        IExtensionCapabilityFactory? capabilityFactory = null,
        ILogger? logger = null,
        string? dataDirectory = null)
    {
        _hostApiVersion = hostApiVersion;
        _contractCatalog = contractCatalog ?? ExtensionContractCatalog.CreateDefault(logger);
        _capabilityFactory = capabilityFactory;
        _logger = logger;
        _dataDirectory = dataDirectory ?? string.Empty;
        _loader = new CollectibleExtensionLoader(
            new SemVersion(hostApiVersion.Major, hostApiVersion.Minor, hostApiVersion.Patch),
            _contractCatalog,
            logger);
    }
    /// <summary>Gets the host API version injected for this runtime manager.</summary>
    public HostApiVersion ApiVersion => _hostApiVersion;
}
