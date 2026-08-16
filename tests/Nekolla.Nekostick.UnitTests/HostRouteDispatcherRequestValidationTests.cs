using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteDispatcherRequestValidationTests
{
    [Fact]
    public void PureMatcherRejectsNonemptyMalformedHostWithoutParserDetails()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(RoutingTestData.Id(360), RouteMatcherType.Exact, "/known"));

        var result = snapshot.Match(
            new RouteMatchInput("/known", "invalid host marker", "GET"));

        Assert.Equal(RouteMatchStatus.InvalidRequest, result.Status);
        Assert.Equal(PathNormalizationErrorCode.InvalidHost, result.InvalidRequestCode);
        Assert.Null(result.Match);
        Assert.Empty(result.RegexTimeoutRouteIds);
    }

    [Fact]
    public async Task DispatcherReturnsOnlyGeneric400ForMalformedHostAndSkipsFallback()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(361),
            RouteMatcherType.Exact,
            "/known");
        var matcherBuild = RouteMatchSnapshotBuilder.Build(new[] { route });
        var matcher = matcherBuild.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");
        var snapshot = new HostRoutingSnapshot(
            RoutingTestData.CreateSnapshot(1, ImmutableArray.Create(route)),
            matcher);
        var fallback = new RecordingFallbackDispatcher();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/known";
        context.Request.Host = new HostString("invalid host marker");
        context.Response.Body = new MemoryStream();

        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            fallback,
            NullLogger.Instance);
        await dispatcher.DispatchAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("Bad request.", body);
        Assert.DoesNotContain("invalid host marker", body, StringComparison.Ordinal);
        Assert.Equal(0, fallback.CallCount);
    }

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot current) => Current = current;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class RecordingFallbackDispatcher : IRouteFallbackDispatcher
    {
        internal int CallCount { get; private set; }

        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason)
        {
            CallCount++;
            return ValueTask.FromResult(false);
        }
    }
}
