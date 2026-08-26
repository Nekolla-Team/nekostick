using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nekolla.Nekostick.Contracts;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class MicroserviceHttpExecutor
{
    private static bool CanReplayRequest(HttpContext context, bool isWebSocket)
    {
        if (isWebSocket || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return false;
        }

        if (context.Request.ContentLength is 0)
        {
            return true;
        }

        if (context.Request.ContentLength is not null)
        {
            return false;
        }

        var bodyDetection = context.Features.Get<IHttpRequestBodyDetectionFeature>();
        return bodyDetection is not null && !bodyDetection.CanHaveBody;
    }

    private static bool IsWebSocketRequest(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            return true;
        }

        return context.Request.Headers.TryGetValue("Upgrade", out var values)
            && values.Any(value => string.Equals(value, "websocket", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsRetryableForwarderFailure(
        ForwarderError error,
        Exception? exception,
        ProxyRetryConfiguration policy)
    {
        if (error != ForwarderError.Request)
        {
            return false;
        }

        return exception switch
        {
            HttpRequestException => policy.RetryOnConnectionFailure,
            IOException => policy.RetryOnUpstreamDisconnect,
            _ => false
        };
    }

    private static bool IsRetryableException(
        Exception exception,
        ProxyRetryConfiguration policy) =>
        exception switch
        {
            HttpRequestException => policy.RetryOnConnectionFailure,
            IOException => policy.RetryOnUpstreamDisconnect,
            _ => false
        };

    private static MicroserviceProxyFailureStage ClassifyForwarderFailure(
        ForwarderError error,
        Exception? exception)
    {
        if (error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout)
        {
            return MicroserviceProxyFailureStage.Timeout;
        }

        if (error is ForwarderError.RequestCanceled
            or ForwarderError.RequestBodyCanceled
            or ForwarderError.ResponseBodyCanceled
            or ForwarderError.UpgradeRequestCanceled
            or ForwarderError.UpgradeResponseCanceled)
        {
            return MicroserviceProxyFailureStage.Cancellation;
        }

        if (error == ForwarderError.Request)
        {
            return exception is IOException
                ? MicroserviceProxyFailureStage.UpstreamDisconnect
                : MicroserviceProxyFailureStage.Connection;
        }

        return error is ForwarderError.ResponseHeaders
            or ForwarderError.ResponseBodyClient
            or ForwarderError.ResponseBodyDestination
            or ForwarderError.UpgradeResponseClient
            or ForwarderError.UpgradeResponseDestination
            ? MicroserviceProxyFailureStage.Response
            : MicroserviceProxyFailureStage.Request;
    }

    private static MicroserviceProxyFailureStage ClassifyException(Exception exception) =>
        exception switch
        {
            HttpRequestException => MicroserviceProxyFailureStage.Connection,
            IOException => MicroserviceProxyFailureStage.UpstreamDisconnect,
            OperationCanceledException => MicroserviceProxyFailureStage.Cancellation,
            _ => MicroserviceProxyFailureStage.Unknown
        };

    private static async ValueTask DelayBeforeRetryAsync(
        ProxyRetryConfiguration policy,
        int attempt,
        CancellationToken cancellationToken)
    {
        var delay = CalculateRetryDelay(policy, attempt, Random.Shared.NextDouble());
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    internal static TimeSpan CalculateRetryDelay(
        ProxyRetryConfiguration policy,
        int retryNumber,
        double jitter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retryNumber);

        var exponential = policy.InitialBackoff.TotalMilliseconds * Math.Pow(2, retryNumber - 1);
        var capped = Math.Min(exponential, policy.MaximumBackoff.TotalMilliseconds);
        var jitterFactor = Math.Clamp(jitter, 0, 1);
        var jittered = capped + ((capped * 0.25) * jitterFactor);
        return TimeSpan.FromMilliseconds(Math.Min(policy.MaximumBackoff.TotalMilliseconds, jittered));
    }

    private void LogFailure(
        MicroserviceProxyRequest request,
        int attempt,
        MicroserviceProxyFailureStage stage,
        long startedAt,
        Exception? exception = null)
    {
        var elapsed = Math.Max(0, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        MicroserviceProxyTelemetry.AttemptFailed(
            _logger,
            request.RouteId,
            request.ServiceId,
            attempt,
            stage,
            elapsed,
            exception);
    }

    internal static MicroserviceProxyExecutionResult MapForwarderError(
        ForwarderError error,
        MicroserviceCancellationCause firstCause)
    {
        if (firstCause == MicroserviceCancellationCause.External)
        {
            return MicroserviceProxyExecutionResult.For(MicroserviceProxyExecutionDisposition.Cancelled);
        }

        if (firstCause == MicroserviceCancellationCause.OwnedTotal)
        {
            return MicroserviceProxyExecutionResult.For(MicroserviceProxyExecutionDisposition.GatewayTimeout);
        }

        return error switch
        {
            ForwarderError.None => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Handled),
            ForwarderError.Request => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.BadGateway),
            ForwarderError.RequestCreation => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.BadGateway),
            ForwarderError.RequestTimedOut => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.GatewayTimeout),
            ForwarderError.UpgradeActivityTimeout => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.GatewayTimeout),
            ForwarderError.RequestCanceled => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            ForwarderError.RequestBodyCanceled => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            ForwarderError.ResponseBodyCanceled => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            ForwarderError.UpgradeRequestCanceled => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            ForwarderError.UpgradeResponseCanceled => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            _ => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.BadGateway)
        };
    }

    internal static MicroserviceProxyExecutionResult ResultForCancellation(
        MicroserviceCancellationCause firstCause) =>
        firstCause switch
        {
            MicroserviceCancellationCause.OwnedTotal => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.GatewayTimeout),
            MicroserviceCancellationCause.External => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled),
            _ => MicroserviceProxyExecutionResult.For(
                MicroserviceProxyExecutionDisposition.Cancelled)
        };
}
