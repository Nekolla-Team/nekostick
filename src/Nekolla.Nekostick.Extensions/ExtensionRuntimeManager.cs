using System.Collections.Immutable;
using System.Text.Json;
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
    private ExtensionInvocationResult(ExtensionInvocationState state, ExtensionHandlerResponse? response)
    {
        State = state;
        Response = response;
    }

    /// <summary>Gets the dispatch outcome.</summary>
    public ExtensionInvocationState State { get; }

    /// <summary>Gets the response when the callback handled the request.</summary>
    public ExtensionHandlerResponse? Response { get; }

    /// <summary>Gets a safe unavailable result.</summary>
    public static ExtensionInvocationResult Unavailable { get; } =
        new(ExtensionInvocationState.Unavailable, null);

    /// <summary>Gets a safe not-handled result.</summary>
    public static ExtensionInvocationResult NotHandled { get; } =
        new(ExtensionInvocationState.NotHandled, null);

    internal static ExtensionInvocationResult Handled(ExtensionHandlerResponse response) =>
        new(ExtensionInvocationState.Handled, response);

    internal static ExtensionInvocationResult Failed =>
        new(ExtensionInvocationState.Failed, null);
}

/// <summary>Contains one safe extension runtime operation result.</summary>
public sealed class ExtensionRuntimeOperationResult
{
    private ExtensionRuntimeOperationResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionRuntimeStatus? status)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Status = status;
    }

    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the non-sensitive operation category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the resulting safe status when available.</summary>
    public ExtensionRuntimeStatus? Status { get; }

    internal static ExtensionRuntimeOperationResult Success(ExtensionRuntimeStatus status) =>
        new(true, ExtensionFailureCode.None, status);

    internal static ExtensionRuntimeOperationResult Failure(
        ExtensionFailureCode code,
        ExtensionRuntimeStatus? status = null) =>
        new(false, code, status);
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
        ExtensionFailureCode lastFailure)
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
    private readonly Dictionary<string, ExtensionInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerBinding> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _dispatchLifetime = new();
    private HandlerBinding? _fallback;
    private bool _disposed;

    /// <summary>Creates an explicit-only runtime manager for one host API version and catalog.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility checks.</param>
    /// <param name="contractCatalog">The immutable host-owned shared contract catalog.</param>
    /// <param name="capabilityFactory">The optional host-owned factory for extension capabilities.</param>
    public ExtensionRuntimeManager(
        HostApiVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null,
        IExtensionCapabilityFactory? capabilityFactory = null)
    {
        _hostApiVersion = hostApiVersion;
        _contractCatalog = contractCatalog ?? ExtensionContractCatalog.CreateDefault();
        _capabilityFactory = capabilityFactory;
        _loader = new CollectibleExtensionLoader(
            new SemVersion(hostApiVersion.Major, hostApiVersion.Minor, hostApiVersion.Patch),
            _contractCatalog);
    }
}
