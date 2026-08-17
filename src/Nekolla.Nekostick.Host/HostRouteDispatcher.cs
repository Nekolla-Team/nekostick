using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Provides the sole no-match fallback extension point for the Host route boundary.</summary>
internal interface IRouteFallbackDispatcher
{
    ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason);
}

/// <summary>Dispatches a fallback while holding the selected snapshot publication lease.</summary>
internal interface ILeasedRouteFallbackDispatcher : IRouteFallbackDispatcher
{
    ValueTask<bool> TryDispatchAsync(
        HttpContext context,
        RouteNoMatchReason reason,
        HostRoutingSnapshotLease publicationLease);
}

/// <summary>Declines every no-match fallback without loading any target or extension.</summary>
internal sealed class NoOpRouteFallbackDispatcher : IRouteFallbackDispatcher
{
    internal static readonly NoOpRouteFallbackDispatcher Instance = new();

    private NoOpRouteFallbackDispatcher()
    {
    }

    public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason) =>
        ValueTask.FromResult(false);
}

/// <summary>Dispatches HTTP requests through one immutable Host route snapshot.</summary>
internal sealed class HostRouteDispatcher
{
    private const string BadRequestMessage = "Bad request.";
    private const string NotFoundMessage = "Not found.";
    private const string ForbiddenMessage = "Forbidden.";
    private const string BadGatewayMessage = "Bad gateway.";
    private const string GatewayTimeoutMessage = "Gateway timeout.";
    private const string ServiceUnavailableMessage = "Service unavailable.";

    private readonly IHostRoutingSnapshotAccessor _snapshotAccessor;
    private readonly IRouteFallbackDispatcher _fallbackDispatcher;
    private readonly IRouteTargetExecutor _targetExecutor;
    private readonly ILogger _logger;

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher)
        : this(
            snapshotAccessor,
            fallbackDispatcher,
            NoOpRouteTargetExecutor.Instance,
            NullLogger.Instance)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        ILogger logger)
        : this(snapshotAccessor, fallbackDispatcher, NoOpRouteTargetExecutor.Instance, logger)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        IRouteTargetExecutor targetExecutor)
        : this(snapshotAccessor, fallbackDispatcher, targetExecutor, NullLogger.Instance)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        ILogger logger,
        IRouteTargetExecutor targetExecutor)
        : this(snapshotAccessor, fallbackDispatcher, targetExecutor, logger)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        IRouteTargetExecutor targetExecutor,
        ILogger logger)
    {
        _snapshotAccessor = snapshotAccessor ?? throw new ArgumentNullException(nameof(snapshotAccessor));
        _fallbackDispatcher = fallbackDispatcher ?? throw new ArgumentNullException(nameof(fallbackDispatcher));
        _targetExecutor = targetExecutor ?? throw new ArgumentNullException(nameof(targetExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task DispatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var publicationLease = TryAcquireSnapshotLease();
        if (publicationLease is null)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }

        var snapshot = publicationLease.Snapshot;
        RouteMatchResult result;
        try
        {
            var input = new RouteMatchInput(
                HostRequestPathAdapter.GetPath(context),
                GetHostValue(context),
                context.Request.Method);
            result = snapshot.Matcher.Match(input);
        }
        catch (Exception)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }

        LogRegexTimeouts(result);

        switch (result.Status)
        {
            case RouteMatchStatus.InvalidRequest:
                await WriteResponseAsync(context, StatusCodes.Status400BadRequest, BadRequestMessage);
                return;

            case RouteMatchStatus.NoMatch:
                await DispatchFallbackOrNotFoundAsync(context, result, publicationLease);
                return;

            case RouteMatchStatus.Matched:
                await DispatchMatchedRouteAsync(context, snapshot, result.Match, publicationLease);
                return;

            default:
                await WriteResponseAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ServiceUnavailableMessage);
                return;
        }
    }

    private HostRoutingSnapshotLease? TryAcquireSnapshotLease() =>
        _snapshotAccessor is IHostRoutingSnapshotLeaseAccessor leaseAccessor
            ? leaseAccessor.TryAcquireLease()
            : HostRoutingSnapshotLease.Capture(_snapshotAccessor.Current);

    private async Task DispatchMatchedRouteAsync(
        HttpContext context,
        HostRoutingSnapshot snapshot,
        RouteMatch? match,
        HostRoutingSnapshotLease publicationLease)
    {
        if (match is null)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }
        RouteTargetExecutionResult executionResult;

        try
        {
            executionResult = _targetExecutor is ILeasedRouteTargetExecutor leasedExecutor
                ? await leasedExecutor.ExecuteAsync(
                    context,
                    snapshot,
                    match,
                    publicationLease,
                    context.RequestAborted)
                : await _targetExecutor.ExecuteAsync(
                    context,
                    snapshot,
                    match,
                    context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            executionResult = RouteTargetExecutionResult.Cancelled;
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            executionResult = RouteTargetExecutionResult.SafeFailure;
        }

        if (executionResult == RouteTargetExecutionResult.Handled)
        {
            return;
        }

        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        if (executionResult == RouteTargetExecutionResult.Cancelled)
        {
            return;
        }

        var (statusCode, message) = executionResult switch
        {
            RouteTargetExecutionResult.BadRequest =>
                (StatusCodes.Status400BadRequest, BadRequestMessage),
            RouteTargetExecutionResult.NotFound =>
                (StatusCodes.Status404NotFound, NotFoundMessage),
            RouteTargetExecutionResult.Forbidden =>
                (StatusCodes.Status403Forbidden, ForbiddenMessage),
            RouteTargetExecutionResult.BadGateway =>
                (StatusCodes.Status502BadGateway, BadGatewayMessage),
            RouteTargetExecutionResult.GatewayTimeout =>
                (StatusCodes.Status504GatewayTimeout, GatewayTimeoutMessage),
            RouteTargetExecutionResult.InternalServerError =>
                (StatusCodes.Status500InternalServerError, "Internal server error."),
            _ => (StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage)
        };
        await WriteResponseAsync(context, statusCode, message);
    }

    private void LogRegexTimeouts(RouteMatchResult result)
    {
        if (result.RegexTimeoutRouteIds.IsDefaultOrEmpty)
        {
            return;
        }

        var routeIds = result.RegexTimeoutRouteIds.Distinct().ToArray();
        HostLogMessages.RouteRegexEvaluationTimedOut(_logger, routeIds, routeIds.Length);
    }

    private static string? GetHostValue(HttpContext context)
    {
        var host = context.Request.Host.Value;
        return string.IsNullOrEmpty(host) ? null : host;
    }

    private async Task DispatchFallbackOrNotFoundAsync(
        HttpContext context,
        RouteMatchResult result,
        HostRoutingSnapshotLease publicationLease)
    {
        var reason = result.NoMatchReason ?? RouteNoMatchReason.NoRoute;
        if (reason != RouteNoMatchReason.NoRoute &&
            _fallbackDispatcher is ILeasedRouteFallbackDispatcher)
        {
            await WriteResponseAsync(context, StatusCodes.Status404NotFound, NotFoundMessage);
            return;
        }

        var handled = false;
        try
        {
            handled = _fallbackDispatcher is ILeasedRouteFallbackDispatcher leasedDispatcher
                ? await leasedDispatcher.TryDispatchAsync(context, reason, publicationLease)
                : await _fallbackDispatcher.TryDispatchAsync(context, reason);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return;
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            handled = false;
        }

        if (!handled && !context.Response.HasStarted)
        {
            await WriteResponseAsync(context, StatusCodes.Status404NotFound, NotFoundMessage);
        }
    }

    private static async Task WriteResponseAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Headers.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        try
        {
            await context.Response.WriteAsync(message, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
        }
    }
}

/// <summary>Extracts only the origin-form path needed by the pure route matcher.</summary>
internal static class HostRequestPathAdapter
{
    internal static string? GetPath(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (TryGetOriginFormPath(rawTarget, out var path))
        {
            return path;
        }

        return context.Request.Path.Value;
    }

    private static bool TryGetOriginFormPath(string? rawTarget, out string? path)
    {
        path = null;
        if (string.IsNullOrEmpty(rawTarget) || rawTarget[0] != '/')
        {
            return false;
        }

        var queryIndex = rawTarget.IndexOf('?');
        path = queryIndex < 0 ? rawTarget : rawTarget[..queryIndex];
        return path.Length > 0;
    }
}
