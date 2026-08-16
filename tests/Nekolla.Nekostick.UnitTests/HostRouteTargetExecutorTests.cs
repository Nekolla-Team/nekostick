using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteTargetExecutorTests
{
    [Theory]
    [InlineData((int)RouteTargetExecutionResult.Deferred)]
    [InlineData((int)RouteTargetExecutionResult.Unavailable)]
    [InlineData((int)RouteTargetExecutionResult.SafeFailure)]
    public async Task NonHandledTargetResultReturnsOnlyGeneric503AndDoesNotFallback(
        int executionResultValue)
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(470),
            RouteMatcherType.Exact,
            "/selected");
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor((RouteTargetExecutionResult)executionResultValue);

        var result = await DispatchAsync(
            snapshot,
            CreateContext("/selected", "example.test", TestContext.Current.CancellationToken),
            fallback,
            executor);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Service unavailable.", result.Body);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task ExecutorExceptionReturnsOnlyGeneric503WithoutLeakingDetails()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(471),
            RouteMatcherType.Exact,
            "/private-route");
        var requestPath = "/private-route";
        var requestHost = "private.example.test";
        var routeId = route.Id.ToString("D");
        var targetRoot = Path.GetTempPath();
        var exceptionMessage = $"executor failure {routeId} {targetRoot} {requestPath} {requestHost}";
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor(RouteTargetExecutionResult.SafeFailure)
        {
            ExceptionToThrow = new InvalidOperationException(exceptionMessage)
        };

        var result = await DispatchAsync(
            snapshot,
            CreateContext(requestPath, requestHost, TestContext.Current.CancellationToken),
            fallback,
            executor);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Service unavailable.", result.Body);
        Assert.DoesNotContain(exceptionMessage, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(routeId, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(targetRoot, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(requestPath, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(requestHost, result.Body, StringComparison.Ordinal);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task HandledTargetResponseIsPreservedByDispatcher()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(472),
            RouteMatcherType.Exact,
            "/handled");
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor(
            RouteTargetExecutionResult.Handled,
            StatusCodes.Status202Accepted,
            "controlled response");

        var result = await DispatchAsync(
            snapshot,
            CreateContext("/handled", "example.test", TestContext.Current.CancellationToken),
            fallback,
            executor);

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal("controlled response", result.Body);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task SelectedRoutePassesOneCoherentSnapshotRouteAndRequestToken()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(473),
            RouteMatcherType.Exact,
            "/coherent");
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor(RouteTargetExecutionResult.Deferred);
        using var requestCancellation = new CancellationTokenSource();
        var context = CreateContext("/coherent", "example.test", requestCancellation.Token);

        var result = await DispatchAsync(snapshot, context, fallback, executor);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Same(context, executor.LastContext);
        Assert.Same(snapshot, executor.LastSnapshot);
        Assert.Same(snapshot.Configuration, executor.LastSnapshot!.Configuration);
        Assert.Same(snapshot.Matcher, executor.LastSnapshot.Matcher);
        Assert.NotNull(executor.LastMatch);
        Assert.Equal(route.Id, executor.LastMatch!.RouteId);
        Assert.Equal(requestCancellation.Token, executor.LastCancellationToken);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task NoMatchUsesExisting404FallbackBoundaryWithoutInvokingExecutor()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(474),
            RouteMatcherType.Exact,
            "/known");
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor(RouteTargetExecutionResult.Handled);

        var result = await DispatchAsync(
            snapshot,
            CreateContext("/missing", "example.test", TestContext.Current.CancellationToken),
            fallback,
            executor);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Not found.", result.Body);
        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(RouteNoMatchReason.NoRoute, fallback.LastReason);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task InvalidRequestKeepsExisting400BoundaryWithoutInvokingExecutorOrFallback()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(475),
            RouteMatcherType.Exact,
            "/known");
        var snapshot = CreateRoutingSnapshot(route);
        var fallback = new RecordingFallbackDispatcher();
        var executor = new RecordingTargetExecutor(RouteTargetExecutionResult.Handled);

        var result = await DispatchAsync(
            snapshot,
            CreateContext("/bad%2", "example.test", TestContext.Current.CancellationToken),
            fallback,
            executor);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("Bad request.", result.Body);
        Assert.Equal(0, fallback.CallCount);
        Assert.Equal(0, executor.CallCount);
    }

    private static HostRoutingSnapshot CreateRoutingSnapshot(params RouteConfiguration[] routes)
    {
        var routeSet = ImmutableArray.CreateRange(routes);
        var configuration = RoutingTestData.CreateSnapshot(1, routeSet);
        var matcher = RoutingTestData.Build(routes);
        return new HostRoutingSnapshot(configuration, matcher);
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string host,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.RequestAborted = requestAborted;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<DispatchResult> DispatchAsync(
        HostRoutingSnapshot snapshot,
        DefaultHttpContext context,
        IRouteFallbackDispatcher fallback,
        IRouteTargetExecutor executor)
    {
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            fallback,
            executor);

        await dispatcher.DispatchAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return new DispatchResult(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private readonly record struct DispatchResult(int StatusCode, string Body);

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot current) => Current = current;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class RecordingTargetExecutor : IRouteTargetExecutor
    {
        private readonly int? _responseStatusCode;
        private readonly string? _responseBody;

        internal RecordingTargetExecutor(
            RouteTargetExecutionResult result,
            int? responseStatusCode = null,
            string? responseBody = null)
        {
            Result = result;
            _responseStatusCode = responseStatusCode;
            _responseBody = responseBody;
        }

        internal RouteTargetExecutionResult Result { get; }
        internal Exception? ExceptionToThrow { get; init; }
        internal int CallCount { get; private set; }
        internal HttpContext? LastContext { get; private set; }
        internal HostRoutingSnapshot? LastSnapshot { get; private set; }
        internal RouteMatch? LastMatch { get; private set; }
        internal CancellationToken LastCancellationToken { get; private set; }

        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastContext = context;
            LastSnapshot = snapshot;
            LastMatch = match;
            LastCancellationToken = cancellationToken;

            var exception = ExceptionToThrow;
            if (exception is not null)
            {
                throw exception;
            }

            if (_responseBody is not null)
            {
                context.Response.StatusCode = _responseStatusCode ?? StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(_responseBody, cancellationToken);
            }

            return Result;
        }
    }

    private sealed class RecordingFallbackDispatcher : IRouteFallbackDispatcher
    {
        internal int CallCount { get; private set; }
        internal RouteNoMatchReason? LastReason { get; private set; }

        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason)
        {
            CallCount++;
            LastReason = reason;
            return ValueTask.FromResult(false);
        }
    }
}
