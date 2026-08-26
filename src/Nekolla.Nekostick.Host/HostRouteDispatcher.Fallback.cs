using Nekolla.Nekostick.Contracts;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

internal sealed partial class HostRouteDispatcher
{
    private async Task<bool> DrainRequestBodyAsync(
        HttpContext context,
        HostRequestAdmissionContext admissionContext,
        Guid? routeId = null,
        RouteTargetType? targetType = null)
    {
        try
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteDispatch.DrainRequestBody");
            var failure = admissionContext.Failure ?? HostRequestAdmission.TryGetProtocolFailure(exception);
            if (failure is not null)
            {
                await WriteAdmissionFailureAsync(context, failure.Value, routeId, targetType);
            }
            else
            {
                await WriteResponseAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ServiceUnavailableMessage);
            }

            return false;
        }
    }

    private async Task DispatchFallbackOrNotFoundAsync(
        HttpContext context,
        RouteMatchResult result,
        HostRoutingSnapshotLease publicationLease,
        HostRequestAdmissionContext admissionContext)
    {
        HostRequestPreparation preparation;
        try
        {
            preparation = _admission.PrepareRequest(publicationLease.Snapshot, context, admissionContext);
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteDispatch.FallbackPreparation");
            await WriteResponseAsync(context, StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage);
            return;
        }

        if (preparation.Rejection is { } preparationRejection)
        {
            await WriteAdmissionFailureAsync(context, preparationRejection);
            return;
        }

        using (preparation.BodyLease)
        {
            var reason = result.NoMatchReason ?? RouteNoMatchReason.NoRoute;
            var handled = await TryInvokeFallbackAsync(
                    context,
                    reason,
                    publicationLease)
                .ConfigureAwait(false);

            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            if (admissionContext.Failure is { } failure)
            {
                await WriteAdmissionFailureAsync(context, failure);
                return;
            }

            if (!handled && !context.Response.HasStarted)
            {
                if (!await DrainRequestBodyAsync(context, admissionContext).ConfigureAwait(false))
                {
                    return;
                }

                if (admissionContext.Failure is { } declinedFailure)
                {
                    await WriteAdmissionFailureAsync(context, declinedFailure);
                    return;
                }

                await WriteResponseAsync(context, StatusCodes.Status404NotFound, NotFoundMessage);
            }
        }
    }

    private async Task<bool> TryInvokeFallbackAsync(
        HttpContext context,
        RouteNoMatchReason reason,
        HostRoutingSnapshotLease publicationLease)
    {
        try
        {
            return _fallbackDispatcher is ILeasedRouteFallbackDispatcher leasedDispatcher
                ? await leasedDispatcher.TryDispatchAsync(context, reason, publicationLease)
                : await _fallbackDispatcher.TryDispatchAsync(context, reason);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteDispatch.Fallback");
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
    }
}
