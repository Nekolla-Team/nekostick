using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Proxy;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.UnitTests;

public sealed class MicroserviceTimeoutExecutorTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("01900000-0000-7000-8000-000000000611");

    [Fact]
    public async Task ForwarderRequestCancellationMapsToTypedCancellation()
    {
        var forwarder = new RecordingForwarder(ForwarderError.RequestCanceled);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var request = CreateRequest();

        var result = await executor.ExecuteAsync(
            CreateContext(),
            request,
            CancellationToken.None);

        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            result.Disposition);
    }

    [Fact]
    public void OwnedTotalFirstCauseWinsOverLaterExternalCancellation()
    {
        var tracker = new MicroserviceCancellationCauseTracker();
        tracker.MarkOwnedTotal();
        tracker.MarkExternal();

        var result = MicroserviceHttpExecutor.MapForwarderError(
            ForwarderError.RequestCanceled,
            tracker.FirstCause);

        Assert.Equal(MicroserviceCancellationCause.OwnedTotal, tracker.FirstCause);
        Assert.Equal(
            MicroserviceProxyExecutionDisposition.GatewayTimeout,
            result.Disposition);
        Assert.Equal(
            MicroserviceProxyExecutionDisposition.GatewayTimeout,
            MicroserviceHttpExecutor.ResultForCancellation(tracker.FirstCause).Disposition);
    }

    [Fact]
    public void ExternalFirstCauseWinsOverLaterOwnedTotalCancellation()
    {
        var tracker = new MicroserviceCancellationCauseTracker();
        tracker.MarkExternal();
        tracker.MarkOwnedTotal();

        var result = MicroserviceHttpExecutor.MapForwarderError(
            ForwarderError.RequestCanceled,
            tracker.FirstCause);

        Assert.Equal(MicroserviceCancellationCause.External, tracker.FirstCause);
        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            result.Disposition);
        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            MicroserviceHttpExecutor.ResultForCancellation(tracker.FirstCause).Disposition);
    }

    [Theory]
    [InlineData(ForwarderError.RequestTimedOut)]
    [InlineData(ForwarderError.UpgradeActivityTimeout)]
    public async Task ForwarderTimeoutErrorsMapToTypedGatewayTimeout(ForwarderError error)
    {
        var forwarder = new RecordingForwarder(error);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(
            MicroserviceProxyExecutionDisposition.GatewayTimeout,
            result.Disposition);
    }

    [Theory]
    [InlineData(ForwarderError.RequestCanceled)]
    [InlineData(ForwarderError.RequestBodyCanceled)]
    [InlineData(ForwarderError.ResponseBodyCanceled)]
    [InlineData(ForwarderError.UpgradeRequestCanceled)]
    [InlineData(ForwarderError.UpgradeResponseCanceled)]
    public async Task ForwarderCancellationErrorsMapToTypedCancellation(ForwarderError error)
    {
        var forwarder = new RecordingForwarder(error);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            result.Disposition);
    }

    [Theory]
    [InlineData(ForwarderError.Request)]
    [InlineData(ForwarderError.RequestCreation)]
    public async Task ForwarderRequestErrorsMapToTypedBadGateway(ForwarderError error)
    {
        var forwarder = new RecordingForwarder(error);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(
            MicroserviceProxyExecutionDisposition.BadGateway,
            result.Disposition);
    }

    [Fact]
    public async Task CallerCancellationTakesTheTypedCancellationPath()
    {
        var forwarder = new RecordingForwarder(ForwarderError.RequestCanceled);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(),
            cancellation.Token);

        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            result.Disposition);
    }

    [Fact]
    public void RequestAbortedCancellationMarksTheExternalFirstCause()
    {
        using var requestAborted = new CancellationTokenSource();
        using var scope = new MicroserviceCancellationScope(
            new MicroserviceCancellationInputs
            {
                CallerCancellation = CancellationToken.None,
                RequestAborted = requestAborted.Token
            },
            TimeSpan.FromMinutes(1),
            isWebSocket: false);

        requestAborted.Cancel();

        var result = MicroserviceHttpExecutor.MapForwarderError(
            ForwarderError.RequestCanceled,
            scope.FirstCause);

        Assert.Equal(MicroserviceCancellationCause.External, scope.FirstCause);
        Assert.Equal(
            MicroserviceProxyExecutionDisposition.Cancelled,
            result.Disposition);
    }

    [Fact]
    public void WebSocketCancellationScopeHasNoNormalOwnedTotalSource()
    {
        using var scope = new MicroserviceCancellationScope(
            new MicroserviceCancellationInputs
            {
                CallerCancellation = CancellationToken.None,
                RequestAborted = CancellationToken.None
            },
            TimeSpan.FromMinutes(1),
            isWebSocket: true);

        Assert.False(scope.HasOwnedTotalSource);
        Assert.Equal(MicroserviceCancellationCause.None, scope.FirstCause);
    }

    [Fact]
    public async Task NormalHttpForwardingUsesThePolicyActivityTimeout()
    {
        var forwarder = new RecordingForwarder(ForwarderError.RequestCanceled);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var policy = new MicroserviceTimeoutPolicy(
            connectTimeout: TimeSpan.FromSeconds(2),
            activityTimeout: TimeSpan.FromSeconds(13),
            httpTotalTimeout: TimeSpan.FromSeconds(29),
            websocketIdleTimeout: TimeSpan.FromSeconds(31));

        await executor.ExecuteAsync(
            CreateContext(),
            new MicroserviceProxyRequest(ServiceId, "/", policy),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(13), forwarder.ActivityTimeout!.Value);
    }

    private static MicroserviceProxyRequest CreateRequest() =>
        new(
            ServiceId,
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(2),
                activityTimeout: TimeSpan.FromSeconds(5),
                httpTotalTimeout: TimeSpan.FromSeconds(17),
                websocketIdleTimeout: TimeSpan.FromSeconds(23)));

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedEndpointResolver : IMicroserviceEndpointResolver
    {
        public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint("http://127.0.0.1")));
    }

    private sealed class RecordingForwarder : IHttpForwarder
    {
        internal RecordingForwarder(ForwarderError result) => Result = result;

        internal ForwarderError Result { get; }

        internal TimeSpan? ActivityTimeout { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            ActivityTimeout = requestConfig.ActivityTimeout;
            return ValueTask.FromResult(Result);
        }
    }
}
