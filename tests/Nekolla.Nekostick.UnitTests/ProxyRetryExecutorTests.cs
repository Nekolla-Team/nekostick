using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyRetryExecutorTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("01900000-0000-0000-0000-000000000701");

    [Fact]
    public async Task BodylessConnectionFailureIsRetriedWithFreshForwarderAttempt()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());
        var context = CreateContext();
        var request = CreateRequest(new ProxyRetryConfiguration(
            maxRetries: 1,
            initialBackoff: TimeSpan.FromMilliseconds(1),
            maximumBackoff: TimeSpan.FromMilliseconds(1)));

        var result = await executor.ExecuteAsync(
            context,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, result.Disposition);
        Assert.Equal(2, forwarder.CallCount);
    }

    [Fact]
    public async Task BodyBearingRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());
        var context = CreateContext();
        context.Request.ContentLength = 3;
        context.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ChunkedRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new IOException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());
        var context = CreateContext();
        context.Request.Headers["Transfer-Encoding"] = "chunked";

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task WebSocketRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());
        var context = CreateContext();
        context.Request.Headers["Connection"] = "Upgrade";
        context.Request.Headers["Upgrade"] = "websocket";

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ZeroRetryPolicyMakesOneAttempt()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(ProxyRetryConfiguration.Default),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ExhaustedRetriesMapToBadGateway()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(new ProxyRetryConfiguration(
                maxRetries: 2,
                initialBackoff: TimeSpan.FromMilliseconds(1),
                maximumBackoff: TimeSpan.FromMilliseconds(1))),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(3, forwarder.CallCount);
    }

    [Fact]
    public async Task ExternalCancellationDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var forwarder = new CancelingForwarder(cancellation);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker());

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 2)),
            cancellation.Token);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Cancelled, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task AttemptTelemetryContainsOnlySafeFields()
    {
        const string sensitive = "sensitive-request-marker";
        var routeId = Guid.Parse("01900000-0000-0000-0000-000000000702");
        var logger = new CapturingLogger();
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            new MicroserviceDrainTracker(),
            logger);
        var context = CreateContext();
        context.Request.Path = "/" + sensitive;
        context.Request.Headers["X-Sensitive"] = sensitive;

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(ProxyRetryConfiguration.Default, routeId),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        var entry = Assert.Single(logger.Entries, value => value.EventId.Id == 2001);
        var detail = Assert.Single(logger.Entries, value => value.EventId.Id == 2002);
        Assert.Equal(routeId, entry.Fields["RouteId"]);
        Assert.Equal(ServiceId, entry.Fields["ServiceId"]);
        Assert.Equal(1, entry.Fields["Attempt"]);
        Assert.DoesNotContain(sensitive, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitive,
            string.Join("|", entry.Fields.Values.Select(value => value?.ToString())),
            StringComparison.Ordinal);
        Assert.NotNull(detail.Message);
        Assert.DoesNotContain(sensitive, detail.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitive,
            string.Join("|", detail.Fields.Values.Select(value => value?.ToString())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetryBackoffIsJitteredAndCapped()
    {
        var policy = new ProxyRetryConfiguration(
            maxRetries: 4,
            initialBackoff: TimeSpan.FromMilliseconds(200),
            maximumBackoff: TimeSpan.FromSeconds(2));

        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            MicroserviceHttpExecutor.CalculateRetryDelay(policy, 1, 0));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            MicroserviceHttpExecutor.CalculateRetryDelay(policy, 4, 1));
    }


    [Fact]
    public async Task SuspendedRequestResolvesNewEndpointOnceAfterResume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var oldRequestCount = 0;
        var newRequestCount = 0;
        await using var oldServer = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref oldRequestCount);
                return context.Response.WriteAsync("old", context.RequestAborted);
            },
            cancellationToken);
        await using var newServer = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref newRequestCount);
                return context.Response.WriteAsync("new", context.RequestAborted);
            },
            cancellationToken);
        var resolver = new MutableEndpointResolver(oldServer.Endpoint);
        var coordinator = new MicroserviceAdmissionCoordinator();
        var tracker = new MicroserviceDrainTracker();
        using var forwardingServices = CreateForwarderServices();
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwardingServices.GetRequiredService<IHttpForwarder>(),
            resolver,
            pool,
            tracker,
            admissionCoordinator: coordinator);
        using var suspension = await coordinator.SuspendAsync(
            new[] { ServiceId },
            cancellationToken);
        var context = CreateTransportContext();
        var execution = executor.ExecuteAsync(
            context,
            CreateRequest(ProxyRetryConfiguration.Default),
            cancellationToken).AsTask();

        Assert.False(execution.IsCompleted);
        Assert.Equal(0, resolver.CallCount);
        resolver.SetEndpoint(newServer.Endpoint);
        suspension.Dispose();

        var result = await execution.WaitAsync(cancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, result.Disposition);
        Assert.Equal("new", ReadResponse(context));
        Assert.Equal(0, Volatile.Read(ref oldRequestCount));
        Assert.Equal(1, Volatile.Read(ref newRequestCount));
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task RequestAbortedDuringAdmissionWaitDoesNotResolveSendOrReadBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var requestCount = 0;
        await using var server = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref requestCount);
                return context.Response.WriteAsync("unexpected", context.RequestAborted);
            },
            cancellationToken);
        var resolver = new MutableEndpointResolver(server.Endpoint);
        var coordinator = new MicroserviceAdmissionCoordinator();
        using var forwardingServices = CreateForwarderServices();
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwardingServices.GetRequiredService<IHttpForwarder>(),
            resolver,
            pool,
            new MicroserviceDrainTracker(),
            admissionCoordinator: coordinator);
        using var suspension = await coordinator.SuspendAsync(
            new[] { ServiceId },
            cancellationToken);
        using var requestAborted = new CancellationTokenSource();
        using var body = new CountingReadStream(Encoding.UTF8.GetBytes("body"));
        var context = CreateTransportContext("POST", body);
        context.RequestAborted = requestAborted.Token;
        var execution = executor.ExecuteAsync(
            context,
            CreateRequest(ProxyRetryConfiguration.Default),
            cancellationToken).AsTask();

        Assert.False(execution.IsCompleted);
        requestAborted.Cancel();

        var result = await execution.WaitAsync(cancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Cancelled, result.Disposition);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(0, Volatile.Read(ref requestCount));
        var stillWaiting = coordinator.EnterAsync(ServiceId, cancellationToken).AsTask();
        Assert.False(stillWaiting.IsCompleted);
        suspension.Dispose();
        using var releasedAdmission = await stillWaiting.WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task TotalTimeoutDuringAdmissionWaitDoesNotResolveSendOrReadBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var requestCount = 0;
        await using var server = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref requestCount);
                return context.Response.WriteAsync("unexpected", context.RequestAborted);
            },
            cancellationToken);
        var resolver = new MutableEndpointResolver(server.Endpoint);
        var coordinator = new MicroserviceAdmissionCoordinator();
        using var forwardingServices = CreateForwarderServices();
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwardingServices.GetRequiredService<IHttpForwarder>(),
            resolver,
            pool,
            new MicroserviceDrainTracker(),
            admissionCoordinator: coordinator);
        using var suspension = await coordinator.SuspendAsync(
            new[] { ServiceId },
            cancellationToken);
        using var body = new CountingReadStream(Encoding.UTF8.GetBytes("body"));
        var context = CreateTransportContext("POST", body);
        var execution = executor.ExecuteAsync(
            context,
            CreateRequestWithTotalTimeout(TimeSpan.FromMilliseconds(100)),
            cancellationToken).AsTask();


        var result = await execution.WaitAsync(cancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.GatewayTimeout, result.Disposition);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(0, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task CaptureLeaseProtectsDrainTrackingAcrossGraphSuspension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var oldRequestReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequestCount = 0;
        var newRequestCount = 0;
        await using var oldServer = await LoopbackService.StartAsync(
            async context =>
            {
                Interlocked.Increment(ref oldRequestCount);
                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                oldRequestReceived.TrySetResult(await reader.ReadToEndAsync());
                await releaseOldResponse.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("old", context.RequestAborted);
            },
            cancellationToken);
        await using var newServer = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref newRequestCount);
                return context.Response.WriteAsync("new", context.RequestAborted);
            },
            cancellationToken);
        var resolver = new CaptureBarrierEndpointResolver(oldServer.Endpoint);
        var coordinator = new MicroserviceAdmissionCoordinator();
        var tracker = new BlockingBeginDrainTracker(new MicroserviceDrainTracker());
        using var forwardingServices = CreateForwarderServices();
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwardingServices.GetRequiredService<IHttpForwarder>(),
            resolver,
            pool,
            tracker,
            admissionCoordinator: coordinator);
        using var oldBody = new CountingReadStream(Encoding.UTF8.GetBytes("payload"));
        var oldContext = CreateTransportContext("POST", oldBody);
        var retryPolicy = new ProxyRetryConfiguration(
            maxRetries: 1,
            initialBackoff: TimeSpan.FromMilliseconds(1),
            maximumBackoff: TimeSpan.FromMilliseconds(1));
        var oldExecution = executor.ExecuteAsync(
            oldContext,
            CreateRequest(retryPolicy),
            cancellationToken).AsTask();

        try
        {
            await resolver.FirstResolutionStarted.WaitAsync(cancellationToken);
            var suspensionTask = coordinator.SuspendAsync(
                new[] { ServiceId },
                cancellationToken).AsTask();
            Assert.False(suspensionTask.IsCompleted);
            resolver.ReleaseFirstResolution();
            await tracker.BeginTrackingStarted.WaitAsync(cancellationToken);
            Assert.False(suspensionTask.IsCompleted);
            tracker.ReleaseBeginTracking();
            using var suspension = await suspensionTask.WaitAsync(cancellationToken);
            resolver.SetEndpoint(newServer.Endpoint);

            Assert.Equal("payload", await oldRequestReceived.Task.WaitAsync(cancellationToken));
            var drainTask = tracker.WaitDrainedAsync(
                ServiceId,
                oldServer.Endpoint.Port,
                Timeout.InfiniteTimeSpan,
                cancellationToken).AsTask();
            Assert.False(drainTask.IsCompleted);
            var newContext = CreateTransportContext();
            var newExecution = executor.ExecuteAsync(
                newContext,
                CreateRequest(ProxyRetryConfiguration.Default),
                cancellationToken).AsTask();
            Assert.False(newExecution.IsCompleted);
            Assert.Equal(1, resolver.CallCount);

            suspension.Dispose();
            var newResult = await newExecution.WaitAsync(cancellationToken);

            Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, newResult.Disposition);
            Assert.Equal("new", ReadResponse(newContext));
            Assert.Equal(1, Volatile.Read(ref newRequestCount));
            Assert.False(oldExecution.IsCompleted);

            releaseOldResponse.TrySetResult(true);
            var oldResult = await oldExecution.WaitAsync(cancellationToken);
            await drainTask.WaitAsync(cancellationToken);

            Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, oldResult.Disposition);
            Assert.Equal("old", ReadResponse(oldContext));
            Assert.Equal("payload", Encoding.UTF8.GetString(oldBody.Bytes));
            Assert.Equal(1, Volatile.Read(ref oldRequestCount));
            Assert.Equal(1, Volatile.Read(ref newRequestCount));
            Assert.Equal(2, resolver.CallCount);
        }
        finally
        {
            tracker.ReleaseBeginTracking();
            releaseOldResponse.TrySetResult(true);
        }
    }


    [Fact]
    public async Task ForwardingStartedBeforeSuspensionStaysOnOldEndpointWhileNewRequestsUsePublishedEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var oldRequestReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequestCount = 0;
        var newRequestCount = 0;
        await using var oldServer = await LoopbackService.StartAsync(
            async context =>
            {
                Interlocked.Increment(ref oldRequestCount);
                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                oldRequestReceived.TrySetResult(await reader.ReadToEndAsync());
                await releaseOldResponse.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("old", context.RequestAborted);
            },
            cancellationToken);
        await using var newServer = await LoopbackService.StartAsync(
            context =>
            {
                Interlocked.Increment(ref newRequestCount);
                return context.Response.WriteAsync("new", context.RequestAborted);
            },
            cancellationToken);
        var resolver = new MutableEndpointResolver(oldServer.Endpoint);
        var coordinator = new MicroserviceAdmissionCoordinator();
        var tracker = new MicroserviceDrainTracker();
        using var forwardingServices = CreateForwarderServices();
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwardingServices.GetRequiredService<IHttpForwarder>(),
            resolver,
            pool,
            tracker,
            admissionCoordinator: coordinator);
        using var body = new CountingReadStream(Encoding.UTF8.GetBytes("already-sent"));
        var oldContext = CreateTransportContext("POST", body);
        var retryPolicy = new ProxyRetryConfiguration(
            maxRetries: 1,
            initialBackoff: TimeSpan.FromMilliseconds(1),
            maximumBackoff: TimeSpan.FromMilliseconds(1));
        var oldExecution = executor.ExecuteAsync(
            oldContext,
            CreateRequest(retryPolicy),
            cancellationToken).AsTask();

        try
        {
            Assert.Equal("already-sent", await oldRequestReceived.Task.WaitAsync(cancellationToken));
            using var suspension = await coordinator.SuspendAsync(
                new[] { ServiceId },
                cancellationToken);
            resolver.SetEndpoint(newServer.Endpoint);
            var newContext = CreateTransportContext();
            var newExecution = executor.ExecuteAsync(
                newContext,
                CreateRequest(ProxyRetryConfiguration.Default),
                cancellationToken).AsTask();
            Assert.False(newExecution.IsCompleted);
            Assert.Equal(1, resolver.CallCount);

            suspension.Dispose();
            var newResult = await newExecution.WaitAsync(cancellationToken);

            Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, newResult.Disposition);
            Assert.Equal("new", ReadResponse(newContext));
            Assert.False(oldExecution.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref oldRequestCount));
            Assert.Equal(1, Volatile.Read(ref newRequestCount));

            releaseOldResponse.TrySetResult(true);
            var oldResult = await oldExecution.WaitAsync(cancellationToken);

            Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, oldResult.Disposition);
            Assert.Equal("old", ReadResponse(oldContext));
            Assert.Equal(1, Volatile.Read(ref oldRequestCount));
            Assert.Equal(1, Volatile.Read(ref newRequestCount));
            Assert.Equal(2, resolver.CallCount);
        }
        finally
        {
            releaseOldResponse.TrySetResult(true);
        }
    }
 
    [Fact]
    public async Task OverlappingGraphSuspensionsCannotResumeEachOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new MicroserviceAdmissionCoordinator();
        var first = await coordinator.SuspendAsync(new[] { ServiceId }, cancellationToken);
        var unrelatedAdmission = coordinator.EnterAsync(Guid.NewGuid(), cancellationToken);
        Assert.True(unrelatedAdmission.IsCompletedSuccessfully);
        using var unrelatedLease = await unrelatedAdmission;
        var secondTask = coordinator.SuspendAsync(
            new[] { ServiceId },
            cancellationToken).AsTask();

        Assert.False(secondTask.IsCompleted);
        first.Dispose();
        var second = await secondTask.WaitAsync(cancellationToken);
        first.Dispose();
        var waitingAdmission = coordinator.EnterAsync(ServiceId, cancellationToken).AsTask();
        Assert.False(waitingAdmission.IsCompleted);

        second.Dispose();
        using var admission = await waitingAdmission.WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task CoordinatorDisposeWakesWaitersAndKeepsLeaseAndScopeDisposalIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var coordinator = new MicroserviceAdmissionCoordinator();
        using var existingCapture = await coordinator.EnterAsync(Guid.NewGuid(), cancellationToken);
        var blockedServiceId = Guid.NewGuid();
        using var suspension = await coordinator.SuspendAsync(
            new[] { blockedServiceId },
            cancellationToken);
        var waitingAdmission = coordinator.EnterAsync(blockedServiceId, cancellationToken).AsTask();

        Assert.False(waitingAdmission.IsCompleted);
        coordinator.Dispose();
        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => waitingAdmission.WaitAsync(cancellationToken));
        existingCapture.Dispose();
        existingCapture.Dispose();
        suspension.Dispose();
        suspension.Dispose();
    }
 
    private static ServiceProvider CreateForwarderServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpForwarder();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateTransportContext(
        string method = "GET",
        CountingReadStream? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("proxy.test");
        context.Request.Path = "/request";
        context.Request.ContentLength = body?.Length ?? 0;
        context.Request.Body = body is null ? Stream.Null : body;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponse(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private static MicroserviceProxyRequest CreateRequestWithTotalTimeout(TimeSpan totalTimeout) =>
        new(
            ServiceId,
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(2),
                activityTimeout: TimeSpan.FromSeconds(5),
                httpTotalTimeout: totalTimeout,
                websocketIdleTimeout: TimeSpan.FromSeconds(23)));

    private static MicroserviceProxyRequest CreateRequest(
        ProxyRetryConfiguration retryPolicy,
        Guid? routeId = null) =>
        new(
            ServiceId,
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(2),
                activityTimeout: TimeSpan.FromSeconds(5),
                httpTotalTimeout: TimeSpan.FromSeconds(17),
                websocketIdleTimeout: TimeSpan.FromSeconds(23)),
            retryPolicy: retryPolicy,
            routeId: routeId);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.ContentLength = 0;
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

    private sealed class SequenceForwarder : IHttpForwarder
    {
        private readonly (ForwarderError Error, Exception? Exception)[] _results;
        private int _next;

        internal SequenceForwarder(
            params (ForwarderError Error, Exception? Exception)[] results) =>
            _results = results;

        internal int CallCount { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            CallCount++;
            var result = _results[Math.Min(_next++, _results.Length - 1)];
            httpContext.Features.Set<IForwarderErrorFeature>(
                new ErrorFeature(result.Error, result.Exception));
            return ValueTask.FromResult(result.Error);
        }
    }

    private sealed class CancelingForwarder : IHttpForwarder
    {
        private readonly CancellationTokenSource _cancellation;

        internal CancelingForwarder(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        internal int CallCount { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            CallCount++;
            _cancellation.Cancel();
            throw new OperationCanceledException();
        }
    }

    private sealed class ErrorFeature : IForwarderErrorFeature
    {
        internal ErrorFeature(ForwarderError error, Exception? exception)
        {
            Error = error;
            Exception = exception;
        }

        public ForwarderError Error { get; }

        public Exception? Exception { get; }
    }

    private sealed class CapturingLogger : ILogger<MicroserviceHttpExecutor>
    {
        internal List<CapturedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    fields[value.Key] = value.Value;
                }
            }

            Entries.Add(new CapturedEntry(
                logLevel,
                eventId,
                formatter(state, exception),
                fields));
        }
    }

    private sealed record CapturedEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed class MutableEndpointResolver : IMicroserviceEndpointResolver
    {
        private MicroserviceEndpointResolution _current;
        private int _callCount;

        internal MutableEndpointResolver(Uri endpoint) =>
            _current = MicroserviceEndpointResolution.Available(new MicroserviceEndpoint(endpoint));

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void SetEndpoint(Uri endpoint) =>
            Volatile.Write(
                ref _current,
                MicroserviceEndpointResolution.Available(new MicroserviceEndpoint(endpoint)));

        public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(Volatile.Read(ref _current));
        }
    }

    private sealed class CaptureBarrierEndpointResolver : IMicroserviceEndpointResolver
    {
        private readonly TaskCompletionSource<bool> _firstResolutionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstResolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MicroserviceEndpointResolution _current;
        private int _callCount;

        internal CaptureBarrierEndpointResolver(Uri endpoint) =>
            _current = MicroserviceEndpointResolution.Available(new MicroserviceEndpoint(endpoint));

        internal Task FirstResolutionStarted => _firstResolutionStarted.Task;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void SetEndpoint(Uri endpoint) =>
            Volatile.Write(
                ref _current,
                MicroserviceEndpointResolution.Available(new MicroserviceEndpoint(endpoint)));

        internal void ReleaseFirstResolution() => _releaseFirstResolution.TrySetResult(true);

        public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = Volatile.Read(ref _current);
            if (Interlocked.Increment(ref _callCount) != 1)
            {
                return ValueTask.FromResult(captured);
            }

            return new ValueTask<MicroserviceEndpointResolution>(
                ResolveFirstAsync(captured, cancellationToken));
        }

        private async Task<MicroserviceEndpointResolution> ResolveFirstAsync(
            MicroserviceEndpointResolution captured,
            CancellationToken cancellationToken)
        {
            _firstResolutionStarted.TrySetResult(true);
            await _releaseFirstResolution.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return captured;
        }
    }

    private sealed class LoopbackService : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LoopbackService(WebApplication app, Uri endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        internal Uri Endpoint { get; }

        internal static async Task<LoopbackService> StartAsync(
            RequestDelegate requestHandler,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(
                    IPAddress.Loopback,
                    0,
                    listenOptions => listenOptions.Protocols = HttpProtocols.Http1));
            var app = builder.Build();
            app.Run(requestHandler);
            await app.StartAsync(cancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            var address = new Uri(
                server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return new LoopbackService(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly MemoryStream _inner;
        private int _bytesRead;

        internal CountingReadStream(byte[] bytes)
        {
            _bytes = bytes;
            _inner = new MemoryStream(bytes, writable: false);
        }

        internal byte[] Bytes => _bytes;

        internal int BytesRead => Volatile.Read(ref _bytesRead);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override int ReadByte()
        {
            var value = _inner.ReadByte();
            if (value >= 0)
            {
                Interlocked.Increment(ref _bytesRead);
            }

            return value;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BlockingBeginDrainTracker : IMicroserviceDrainTracker
    {
        private readonly IMicroserviceDrainTracker _inner;
        private readonly TaskCompletionSource<bool> _beginTrackingStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBeginTracking =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BlockingBeginDrainTracker(IMicroserviceDrainTracker inner) => _inner = inner;

        internal Task BeginTrackingStarted => _beginTrackingStarted.Task;

        internal void ReleaseBeginTracking() => _releaseBeginTracking.TrySetResult(true);

        public IDisposable BeginTracking(Guid serviceId, int port)
        {
            _beginTrackingStarted.TrySetResult(true);
            _releaseBeginTracking.Task.GetAwaiter().GetResult();
            return _inner.BeginTracking(serviceId, port);
        }

        public ValueTask WaitDrainedAsync(
            Guid serviceId,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            _inner.WaitDrainedAsync(serviceId, port, timeout, cancellationToken);
    }
}
