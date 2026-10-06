using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

internal sealed partial class HostRouteDispatcher
{
    private async Task WriteAdmissionFailureAsync(
        HttpContext context,
        HostRequestAdmissionFailure failure,
        Guid? routeId = null,
        RouteTargetType? targetType = null,
        HostRoutingSnapshot? snapshot = null,
        string? ownerExtensionId = null)
    {
        if (failure.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            await WriteGenericServiceUnavailableAsync(
                context,
                HostGenericUnavailableReason.UnclassifiedAdmissionFailure,
                snapshot,
                routeId,
                targetType,
                ownerExtensionId);
            return;
        }

        LogAdmissionRejection(failure, routeId, targetType);
        await WriteResponseAsync(context, failure.StatusCode, failure.Message, failure.RetryAfterSeconds);
    }

    private async Task<bool> WriteGenericServiceUnavailableAsync(
        HttpContext context,
        HostGenericUnavailableReason reason,
        HostRoutingSnapshot? snapshot = null,
        Guid? routeId = null,
        RouteTargetType? targetType = null,
        string? ownerExtensionId = null)
    {
        var wroteResponse = await WriteResponseAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            ServiceUnavailableMessage);
        if (!wroteResponse)
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            var activityTraceId = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
                ? activity.TraceId.ToString()
                : null;
            HostLogMessages.GenericServiceUnavailable(
                _logger,
                reason,
                StatusCodes.Status503ServiceUnavailable,
                context.TraceIdentifier,
                activityTraceId,
                snapshot?.Configuration.Version,
                snapshot?.DispatchGeneration?.GenerationId,
                routeId,
                targetType,
                ownerExtensionId);
        }

        return true;
    }

    private async Task<bool> WriteResponseAsync(
        HttpContext context,
        int statusCode,
        string message,
        int? retryAfterSeconds = null)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return false;
        }

        context.Response.Headers.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        if (retryAfterSeconds is > 0)
        {
            context.Response.Headers["Retry-After"] =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            await context.Response.WriteAsync(message, context.RequestAborted);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
        catch (Exception exception)
        {
            HostLogMessages.FailureDetails(_logger, exception, "RouteDispatch.ResponseWrite");
            if (context.Response.HasStarted)
            {
                context.Abort();
            }

            return false;
        }
    }
}
