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
        RouteTargetType? targetType = null)
    {
        LogAdmissionRejection(failure, routeId, targetType);
        await WriteResponseAsync(context, failure.StatusCode, failure.Message, failure.RetryAfterSeconds);
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
