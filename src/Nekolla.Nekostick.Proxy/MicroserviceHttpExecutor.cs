using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Describes a non-sensitive microservice forwarding disposition.</summary>
public enum MicroserviceProxyExecutionDisposition
{
    /// <summary>YARP handled the response on the current HTTP context.</summary>
    Handled,

    /// <summary>No endpoint was available for the service.</summary>
    Unavailable,

    /// <summary>The request or proxy policy was invalid.</summary>
    BadRequest,

    /// <summary>The destination could not produce a safe response.</summary>
    BadGateway,

    /// <summary>The forwarding activity exceeded its safe time budget.</summary>
    GatewayTimeout,

    /// <summary>The forwarding operation was cancelled.</summary>
    Cancelled
}

/// <summary>Contains only the safe result category of one forwarding operation.</summary>
public sealed class MicroserviceProxyExecutionResult
{
    private MicroserviceProxyExecutionResult(MicroserviceProxyExecutionDisposition disposition)
    {
        Disposition = disposition;
    }

    /// <summary>Gets the safe execution disposition.</summary>
    public MicroserviceProxyExecutionDisposition Disposition { get; }

    /// <summary>Gets whether YARP already handled the response.</summary>
    public bool IsHandled => Disposition == MicroserviceProxyExecutionDisposition.Handled;

    internal static MicroserviceProxyExecutionResult For(
        MicroserviceProxyExecutionDisposition disposition) => new(disposition);

    /// <summary>Returns a non-sensitive result representation.</summary>
    public override string ToString() => $"MicroserviceProxyExecutionResult:{Disposition}";
}

internal enum MicroserviceCancellationCause
{
    None,
    External,
    OwnedTotal
}

internal sealed class MicroserviceCancellationCauseTracker
{
    private int _cause;

    internal MicroserviceCancellationCause FirstCause =>
        (MicroserviceCancellationCause)Volatile.Read(ref _cause);

    internal void MarkExternal() => Interlocked.CompareExchange(
        ref _cause,
        (int)MicroserviceCancellationCause.External,
        (int)MicroserviceCancellationCause.None);

    internal void MarkExternalIfCanceled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            MarkExternal();
        }
    }

    internal void MarkOwnedTotal() => Interlocked.CompareExchange(
        ref _cause,
        (int)MicroserviceCancellationCause.OwnedTotal,
        (int)MicroserviceCancellationCause.None);
}

internal readonly struct MicroserviceCancellationInputs
{
    internal CancellationToken CallerCancellation { get; init; }

    internal CancellationToken RequestAborted { get; init; }
}

internal sealed class MicroserviceCancellationScope : IDisposable
{
    private readonly MicroserviceCancellationCauseTracker _causeTracker;
    private readonly CancellationTokenSource? _ownedTotal;
    private readonly Timer? _ownedTotalTimer;
    private readonly CancellationTokenSource _linkedCancellation;
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly CancellationTokenRegistration _requestAbortedRegistration;

    internal MicroserviceCancellationScope(
        MicroserviceCancellationInputs inputs,
        TimeSpan httpTotalTimeout,
        bool isWebSocket)
    {
        var callerCancellation = inputs.CallerCancellation;
        var requestAborted = inputs.RequestAborted;
        _causeTracker = new MicroserviceCancellationCauseTracker();
        _causeTracker.MarkExternalIfCanceled(callerCancellation);
        _causeTracker.MarkExternalIfCanceled(requestAborted);
        _callerRegistration = callerCancellation.Register(
            static state => ((MicroserviceCancellationCauseTracker)state!).MarkExternal(),
            _causeTracker);
        _requestAbortedRegistration = requestAborted.Register(
            static state => ((MicroserviceCancellationCauseTracker)state!).MarkExternal(),
            _causeTracker);

        _ownedTotal = isWebSocket ? null : new CancellationTokenSource();
        _ownedTotalTimer = _ownedTotal is null
            ? null
            : new Timer(
                static state => ((OwnedTotalTimeoutState)state!).Fire(),
                new OwnedTotalTimeoutState(_causeTracker, _ownedTotal),
                httpTotalTimeout,
                Timeout.InfiniteTimeSpan);
        _linkedCancellation = _ownedTotal is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                requestAborted)
            : CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                requestAborted,
                _ownedTotal.Token);
        OperationToken = _linkedCancellation.Token;
    }

    internal CancellationToken OperationToken { get; }

    internal MicroserviceCancellationCause FirstCause => _causeTracker.FirstCause;

    internal bool HasOwnedTotalSource => _ownedTotal is not null;

    public void Dispose()
    {
        _ownedTotalTimer?.Dispose();
        _linkedCancellation.Dispose();
        _ownedTotal?.Dispose();
        _requestAbortedRegistration.Dispose();
        _callerRegistration.Dispose();
    }

    private sealed class OwnedTotalTimeoutState
    {
        private readonly MicroserviceCancellationCauseTracker _causeTracker;
        private readonly CancellationTokenSource _ownedTotal;

        internal OwnedTotalTimeoutState(
            MicroserviceCancellationCauseTracker causeTracker,
            CancellationTokenSource ownedTotal)
        {
            _causeTracker = causeTracker;
            _ownedTotal = ownedTotal;
        }

        internal void Fire()
        {
            _causeTracker.MarkOwnedTotal();
            try
            {
                _ownedTotal.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>Executes one safe microservice request through YARP's forwarder.</summary>
public sealed class MicroserviceHttpExecutor
{
    private readonly IHttpForwarder _forwarder;
    private readonly IMicroserviceEndpointResolver _endpointResolver;
    private readonly MicroserviceHttpInvokerPool _invokerPool;
    private readonly ILogger<MicroserviceHttpExecutor> _logger;

    /// <summary>Creates an executor with shared YARP transport dependencies.</summary>
    /// <param name="forwarder">The YARP forwarder.</param>
    /// <param name="endpointResolver">The endpoint resolver.</param>
    /// <param name="invokerPool">The bounded timeout-keyed HTTP invoker pool.</param>
    /// <param name="logger">The safe structured proxy logger.</param>
    public MicroserviceHttpExecutor(
        IHttpForwarder forwarder,
        IMicroserviceEndpointResolver endpointResolver,
        MicroserviceHttpInvokerPool invokerPool,
        ILogger<MicroserviceHttpExecutor>? logger = null)
    {
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        _invokerPool = invokerPool ?? throw new ArgumentNullException(nameof(invokerPool));
        _logger = logger ?? NullLogger<MicroserviceHttpExecutor>.Instance;
    }

    /// <summary>Resolves and forwards one request without exposing destination details.</summary>
    /// <param name="httpContext">The current ASP.NET request context.</param>
    /// <param name="request">The immutable proxy request.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe typed disposition for host status mapping.</returns>
    public async ValueTask<MicroserviceProxyExecutionResult> ExecuteAsync(
        HttpContext httpContext,
        MicroserviceProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(request);

        var timeoutPolicy = request.TimeoutPolicy;
        var retryPolicy = request.RetryPolicy;
        var isWebSocket = IsWebSocketRequest(httpContext);
        using var cancellationScope = new MicroserviceCancellationScope(
            new MicroserviceCancellationInputs
            {
                CallerCancellation = cancellationToken,
                RequestAborted = httpContext.RequestAborted
            },
            timeoutPolicy.HttpTotalTimeout,
            isWebSocket);
        var operationToken = cancellationScope.OperationToken;
        MicroserviceEndpointResolution? resolution;
        try
        {
            resolution = await _endpointResolver
                .ResolveAsync(request.ServiceId, operationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ResultForCancellation(cancellationScope.FirstCause);
        }
        catch (Exception)
        {
            return MicroserviceProxyExecutionResult.For(MicroserviceProxyExecutionDisposition.Unavailable);
        }

        if (resolution is null
            || !resolution.IsAvailable
            || resolution.Endpoint is null)
        {
            return MicroserviceProxyExecutionResult.For(MicroserviceProxyExecutionDisposition.Unavailable);
        }

        var originalPath = httpContext.Request.Path;
        var originalPathBase = httpContext.Request.PathBase;
        var canReplay = retryPolicy.MaxRetries > 0 && CanReplayRequest(httpContext, isWebSocket);
        var startedAt = Stopwatch.GetTimestamp();
        var attempt = 0;
        try
        {
            httpContext.Request.PathBase = PathString.Empty;
            httpContext.Request.Path = request.ForwardedPath;

            while (true)
            {
                attempt++;
                try
                {
                    var error = await SendAttemptAsync(
                            httpContext,
                            request,
                            resolution.Endpoint.DestinationPrefix,
                            timeoutPolicy,
                            isWebSocket,
                            operationToken)
                        .ConfigureAwait(false);
                    if (error == ForwarderError.None)
                    {
                        return MicroserviceProxyExecutionResult.For(
                            MicroserviceProxyExecutionDisposition.Handled);
                    }

                    var errorFeature = httpContext.GetForwarderErrorFeature();
                    var failureStage = ClassifyForwarderFailure(error, errorFeature?.Exception);
                    var canRetry = canReplay
                        && !httpContext.Response.HasStarted
                        && cancellationScope.FirstCause == MicroserviceCancellationCause.None
                        && attempt <= retryPolicy.MaxRetries
                        && IsRetryableForwarderFailure(
                            error,
                            errorFeature?.Exception,
                            retryPolicy);
                    LogFailure(request, attempt, failureStage, startedAt);
                    if (!canRetry)
                    {
                        return MapForwarderError(error, cancellationScope.FirstCause);
                    }

                    await DelayBeforeRetryAsync(retryPolicy, attempt, operationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ResultForCancellation(cancellationScope.FirstCause);
                }
                catch (InvalidOperationException)
                {
                    LogFailure(request, attempt, MicroserviceProxyFailureStage.Request, startedAt);
                    return MicroserviceProxyExecutionResult.For(
                        MicroserviceProxyExecutionDisposition.BadRequest);
                }
                catch (Exception exception)
                {
                    var failureStage = ClassifyException(exception);
                    var canRetry = canReplay
                        && !httpContext.Response.HasStarted
                        && cancellationScope.FirstCause == MicroserviceCancellationCause.None
                        && attempt <= retryPolicy.MaxRetries
                        && IsRetryableException(exception, retryPolicy);
                    LogFailure(request, attempt, failureStage, startedAt);
                    if (!canRetry)
                    {
                        return MicroserviceProxyExecutionResult.For(
                            MicroserviceProxyExecutionDisposition.BadGateway);
                    }

                    await DelayBeforeRetryAsync(retryPolicy, attempt, operationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            httpContext.Request.Path = originalPath;
            httpContext.Request.PathBase = originalPathBase;
        }
    }

    private async ValueTask<ForwarderError> SendAttemptAsync(
        HttpContext httpContext,
        MicroserviceProxyRequest request,
        string destinationPrefix,
        MicroserviceTimeoutPolicy timeoutPolicy,
        bool isWebSocket,
        CancellationToken operationToken)
    {
        if (!_invokerPool.TryAcquire(timeoutPolicy.ConnectTimeout, out var lease)
            || lease is null)
        {
            return ForwarderError.NoAvailableDestinations;
        }

        using (lease)
        {
            var requestConfig = new ForwarderRequestConfig
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                ActivityTimeout = isWebSocket
                    ? timeoutPolicy.WebSocketIdleTimeout
                    : timeoutPolicy.ActivityTimeout
            };
            var transformer = new MicroserviceHttpTransformer(request, operationToken);
            return await _forwarder.SendAsync(
                    httpContext,
                    destinationPrefix,
                    lease.Invoker,
                    requestConfig,
                    transformer,
                    operationToken)
                .ConfigureAwait(false);
        }
    }

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
        long startedAt)
    {
        var elapsed = Math.Max(0, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        MicroserviceProxyTelemetry.AttemptFailed(
            _logger,
            request.RouteId,
            request.ServiceId,
            attempt,
            stage,
            elapsed);
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
