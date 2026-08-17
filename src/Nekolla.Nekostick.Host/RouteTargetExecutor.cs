using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Describes the safe disposition of a matched-route target attempt.</summary>
internal enum RouteTargetExecutionResult
{
    /// <summary>The executor did not complete the response and the host must defer safely.</summary>
    Deferred,

    /// <summary>The executor owns a completed response.</summary>
    Handled,

    /// <summary>The selected target is currently unavailable.</summary>
    Unavailable,

    /// <summary>The selected handler failed before writing a response.</summary>
    InternalServerError,

    /// <summary>The executor encountered a failure that is safe to expose only as a generic 503.</summary>
    SafeFailure,

    /// <summary>The request could not be executed safely and maps to a generic 400.</summary>
    BadRequest,

    /// <summary>The selected static target was not found.</summary>
    NotFound,

    /// <summary>The selected target was rejected by an access boundary.</summary>
    Forbidden,

    /// <summary>The selected upstream could not produce a safe response.</summary>
    BadGateway,

    /// <summary>The selected upstream exceeded its safe time budget.</summary>
    GatewayTimeout,

    /// <summary>The request was cancelled and must not receive an appended response.</summary>
    Cancelled
}

/// <summary>Executes a selected route target without performing route lookup or snapshot loading.</summary>
internal interface IRouteTargetExecutor
{
    /// <summary>
    /// Attempts execution for the selected route using the one snapshot that selected it.
    /// Implementations must not load storage, rebuild the matcher, or perform another lookup.
    /// </summary>
    ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken);
}

/// <summary>Executes a target while retaining the exact publication selected by the matcher.</summary>
internal interface ILeasedRouteTargetExecutor : IRouteTargetExecutor
{
    ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        HostRoutingSnapshotLease publicationLease,
        CancellationToken cancellationToken);
}

/// <summary>Defers every target until a target-specific executor is registered.</summary>
internal sealed class NoOpRouteTargetExecutor : IRouteTargetExecutor
{
    internal static readonly NoOpRouteTargetExecutor Instance = new();

    private NoOpRouteTargetExecutor()
    {
    }

    public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch match,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RouteTargetExecutionResult.Deferred);
}
