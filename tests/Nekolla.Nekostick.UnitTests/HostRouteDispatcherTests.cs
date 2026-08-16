using System.Text;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteDispatcherTests
{
    [Fact]
    public async Task MissingSnapshotReturnsOnlyGeneric503AndDoesNotFallback()
    {
        var fallback = new RecordingFallbackDispatcher(false);
        var result = await DispatchAsync(null, CreateContext("/anything", "example.test"), fallback);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Service unavailable.", result.Body);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task InvalidRequestReturnsOnlyGeneric400AndDoesNotFallback()
    {
        var snapshot = CreateRoutingSnapshot(
            RoutingTestData.CreateRoute(RoutingTestData.Id(210), RouteMatcherType.Exact, "/anything"));
        var fallback = new RecordingFallbackDispatcher(false);
        var result = await DispatchAsync(snapshot, CreateContext("/bad%2", "example.test"), fallback);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("Bad request.", result.Body);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task MatchedRouteReturnsGeneric503WithoutInvokingFallbackOrTarget()
    {
        var snapshot = CreateRoutingSnapshot(
            RoutingTestData.CreateRoute(RoutingTestData.Id(211), RouteMatcherType.Exact, "/matched"));
        var fallback = new RecordingFallbackDispatcher(false);
        var result = await DispatchAsync(snapshot, CreateContext("/matched", "example.test"), fallback);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Service unavailable.", result.Body);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task HostlessRouteTreatsEmptyHttpHostAsAUsableMatch()
    {
        var snapshot = CreateRoutingSnapshot(
            RoutingTestData.CreateRoute(RoutingTestData.Id(212), RouteMatcherType.Exact, "/hostless"));
        var fallback = new RecordingFallbackDispatcher(false);
        var result = await DispatchAsync(snapshot, CreateContext("/hostless", string.Empty), fallback);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Service unavailable.", result.Body);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task NoMatchDeclineReturnsOnlyGeneric404WithTheSafeReasonBoundary()
    {
        var snapshot = CreateRoutingSnapshot(
            RoutingTestData.CreateRoute(RoutingTestData.Id(213), RouteMatcherType.Exact, "/known"));
        var fallback = new RecordingFallbackDispatcher(false);
        var result = await DispatchAsync(snapshot, CreateContext("/missing", "example.test"), fallback);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Not found.", result.Body);
        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(RouteNoMatchReason.NoRoute, fallback.LastReason);
    }

    [Fact]
    public async Task FallbackExceptionIsConvertedToOnlyGeneric404()
    {
        var snapshot = CreateRoutingSnapshot();
        var fallback = new ThrowingFallbackDispatcher();
        var result = await DispatchAsync(snapshot, CreateContext("/missing", "example.test"), fallback);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Not found.", result.Body);
        Assert.Equal(1, fallback.CallCount);
    }

    private static HostRoutingSnapshot CreateRoutingSnapshot(params RouteConfiguration[] routes)
    {
        var configuration = RoutingTestData.CreateSnapshot(
            1,
            routes.Length == 0
                ? System.Collections.Immutable.ImmutableArray<RouteConfiguration>.Empty
                : System.Collections.Immutable.ImmutableArray.CreateRange(routes));
        return new HostRoutingSnapshot(configuration, RoutingTestData.Build(routes));
    }

    private static DefaultHttpContext CreateContext(string path, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<DispatchResult> DispatchAsync(
        HostRoutingSnapshot? snapshot,
        DefaultHttpContext context,
        IRouteFallbackDispatcher fallback)
    {
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            fallback);

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
        internal FixedSnapshotAccessor(HostRoutingSnapshot? current) => Current = current;

        public HostRoutingSnapshot? Current { get; }
    }

    private sealed class RecordingFallbackDispatcher : IRouteFallbackDispatcher
    {
        private readonly bool _handled;

        internal RecordingFallbackDispatcher(bool handled) => _handled = handled;

        internal int CallCount { get; private set; }
        internal RouteNoMatchReason? LastReason { get; private set; }

        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason)
        {
            CallCount++;
            LastReason = reason;
            return ValueTask.FromResult(_handled);
        }
    }

    private sealed class ThrowingFallbackDispatcher : IRouteFallbackDispatcher
    {
        internal int CallCount { get; private set; }

        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason)
        {
            CallCount++;
            throw new InvalidOperationException("fallback failure");
        }
    }
}
