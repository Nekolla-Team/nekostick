using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
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

internal sealed partial class HostRouteDispatcher
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
    private readonly HostRequestAdmission _admission;
    private readonly ILogger _logger;

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher)
        : this(
            snapshotAccessor,
            fallbackDispatcher,
            NoOpRouteTargetExecutor.Instance,
            new HostRequestAdmission(),
            NullLogger.Instance)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        ILogger logger)
        : this(
            snapshotAccessor,
            fallbackDispatcher,
            NoOpRouteTargetExecutor.Instance,
            new HostRequestAdmission(),
            logger)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        IRouteTargetExecutor targetExecutor)
        : this(
            snapshotAccessor,
            fallbackDispatcher,
            targetExecutor,
            new HostRequestAdmission(),
            NullLogger.Instance)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        ILogger logger,
        IRouteTargetExecutor targetExecutor)
        : this(snapshotAccessor, fallbackDispatcher, targetExecutor, new HostRequestAdmission(), logger)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        IRouteTargetExecutor targetExecutor,
        ILogger logger)
        : this(snapshotAccessor, fallbackDispatcher, targetExecutor, new HostRequestAdmission(), logger)
    {
    }

    internal HostRouteDispatcher(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        IRouteFallbackDispatcher fallbackDispatcher,
        IRouteTargetExecutor targetExecutor,
        HostRequestAdmission admission,
        ILogger logger)
    {
        _snapshotAccessor = snapshotAccessor ?? throw new ArgumentNullException(nameof(snapshotAccessor));
        _fallbackDispatcher = fallbackDispatcher ?? throw new ArgumentNullException(nameof(fallbackDispatcher));
        _targetExecutor = targetExecutor ?? throw new ArgumentNullException(nameof(targetExecutor));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
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
        var admissionContext = HostRequestAdmission.CreateContext();
        HostGlobalAdmissionResult globalAdmission;
        try
        {
            globalAdmission = await _admission.TryAcquireGlobalAsync(snapshot, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }

        if (globalAdmission.Cancelled)
        {
            return;
        }

        if (globalAdmission.Rejection is { } globalRejection)
        {
            await WriteAdmissionFailureAsync(context, globalRejection);
            return;
        }

        var concurrencyLease = globalAdmission.Lease;
        if (concurrencyLease is null)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }

        using (concurrencyLease)
        {
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
                    await DispatchFallbackOrNotFoundAsync(context, result, publicationLease, admissionContext);
                    return;

                case RouteMatchStatus.Matched:
                    await DispatchMatchedRouteAsync(
                        context,
                        snapshot,
                        result.Match,
                        publicationLease,
                        admissionContext);
                    return;

                default:
                    await WriteResponseAsync(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        ServiceUnavailableMessage);
                    return;
            }
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
        HostRoutingSnapshotLease publicationLease,
        HostRequestAdmissionContext admissionContext)
    {
        if (match is null)
        {
            await WriteResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableMessage);
            return;
        }

        var routeConfiguration = snapshot.ExecutableRoutes.TryGetValue(match.RouteId, out var executable)
            ? executable.Configuration
            : null;
        HostRouteAdmissionResult routeAdmission;
        try
        {
            routeAdmission = await _admission.TryAcquireRouteAsync(snapshot, match, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            await WriteResponseAsync(context, StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage);
            return;
        }

        if (routeAdmission.Cancelled)
        {
            return;
        }

        if (routeAdmission.Rejection is { } routeRejection)
        {
            await WriteAdmissionFailureAsync(
                context,
                routeRejection,
                match.RouteId,
                routeConfiguration?.Target.Type ?? match.Target?.Type);
            return;
        }

        using var routeConcurrencyLease = routeAdmission.Lease;
        HostRequestPreparation preparation;
        try
        {
            preparation = _admission.PrepareRequest(
                snapshot,
                context,
                admissionContext,
                routeConfiguration);
        }
        catch (Exception)
        {
            await WriteResponseAsync(context, StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage);
            return;
        }

        if (preparation.Rejection is { } preparationRejection)
        {
            await WriteAdmissionFailureAsync(
                context,
                preparationRejection,
                match.RouteId,
                routeConfiguration?.Target.Type ?? match.Target?.Type);
            return;
        }

        using (preparation.BodyLease)
        {
            if (match.Target?.Type == RouteTargetType.StaticFile &&
                !await DrainRequestBodyAsync(
                    context,
                    admissionContext,
                    match.RouteId,
                    routeConfiguration?.Target.Type ?? match.Target?.Type).ConfigureAwait(false))
            {
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
            catch (Exception exception)
            {
                if (!context.Response.HasStarted &&
                    HostRequestAdmission.TryGetProtocolFailure(exception) is { } protocolFailure)
                {
                    admissionContext.RecordFailure(protocolFailure);
                }

                executionResult = RouteTargetExecutionResult.SafeFailure;
            }

            if (admissionContext.Failure is { } failure)
            {
                await WriteAdmissionFailureAsync(
                    context,
                    failure,
                    match.RouteId,
                    routeConfiguration?.Target.Type ?? match.Target?.Type);
                return;
            }

            if (executionResult is RouteTargetExecutionResult.StaticNotFound or RouteTargetExecutionResult.StaticIndexMissing)
            {
                var fallbackReason = executionResult == RouteTargetExecutionResult.StaticNotFound
                    ? RouteNoMatchReason.StaticNotFound
                    : RouteNoMatchReason.StaticIndexMissing;
                var handled = await TryInvokeFallbackAsync(
                        context,
                        fallbackReason,
                        publicationLease)
                    .ConfigureAwait(false);

                if (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }

                if (admissionContext.Failure is { } fallbackFailure)
                {
                    await WriteAdmissionFailureAsync(
                        context,
                        fallbackFailure,
                        match.RouteId,
                        routeConfiguration?.Target.Type ?? match.Target?.Type);
                    return;
                }

                if (handled || context.Response.HasStarted)
                {
                    LogMatchedTargetOutcome(
                        routeConfiguration,
                        match,
                        executionResult,
                        context.Response.StatusCode);
                    return;
                }

                var wroteFallbackResponse = await WriteResponseAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    NotFoundMessage);
                if (wroteFallbackResponse)
                {
                    LogMatchedTargetOutcome(
                        routeConfiguration,
                        match,
                        executionResult,
                        context.Response.StatusCode);
                }

                return;
            }

            if (executionResult == RouteTargetExecutionResult.Handled)
            {
                LogMatchedTargetOutcome(
                    routeConfiguration,
                    match,
                    executionResult,
                    context.Response.StatusCode);
                return;
            }

            if (context.Response.HasStarted)
            {
                LogMatchedTargetOutcome(
                    routeConfiguration,
                    match,
                    executionResult,
                    context.Response.StatusCode);
                context.Abort();
                return;
            }

            if (executionResult == RouteTargetExecutionResult.Cancelled)
            {
                LogMatchedTargetOutcome(
                    routeConfiguration,
                    match,
                    executionResult,
                    context.Response.StatusCode);
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
            var wroteResponse = await WriteResponseAsync(context, statusCode, message);
            if (wroteResponse)
            {
                LogMatchedTargetOutcome(
                    routeConfiguration,
                    match,
                    executionResult,
                    context.Response.StatusCode);
            }
        }
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
