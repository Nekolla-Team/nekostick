using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteConcurrencyLeaseTests
{
    [Theory]
    [InlineData((int)RouteTargetExecutionResult.Handled)]
    [InlineData((int)RouteTargetExecutionResult.Cancelled)]
    [InlineData((int)RouteTargetExecutionResult.BadRequest)]
    public async Task RouteConcurrencyPermitIsReleasedAfterHandledCancelledAndTargetFailureResults(
        int resultValue)
    {
        var fixture = CreateFixture(new ResultExecutor((RouteTargetExecutionResult)resultValue));

        await fixture.Dispatcher.DispatchAsync(CreateContext());

        await AssertRoutePermitAvailableAsync(fixture);
    }

    [Fact]
    public async Task RouteConcurrencyPermitIsReleasedAfterPreparationRejection()
    {
        var fixture = CreateFixture(new ResultExecutor(RouteTargetExecutionResult.Handled), maxBodyBytes: 3);
        var context = CreateContext(new byte[] { 1, 2, 3, 4 });

        await fixture.Dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        await AssertRoutePermitAvailableAsync(fixture);
    }

    [Fact]
    public async Task RouteConcurrencyPermitIsReleasedAfterTargetException()
    {
        var fixture = CreateFixture(new ThrowingExecutor());

        await fixture.Dispatcher.DispatchAsync(CreateContext());

        await AssertRoutePermitAvailableAsync(fixture);
    }

    [Fact]
    public async Task RouteConcurrencyPermitIsReleasedAfterRequestCancellation()
    {
        var target = new CancelFirstTargetExecutor();
        var fixture = CreateFixture(target);
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext();
        context.RequestAborted = cancellation.Token;

        var pending = fixture.Dispatcher.DispatchAsync(context);
        await target.Started;
        cancellation.Cancel();
        await pending;

        await AssertRoutePermitAvailableAsync(fixture);
    }

    [Fact]
    public async Task RouteConcurrencyPermitIsReleasedAfterResponseStartedAbort()
    {
        var fixture = CreateFixture(new ResultExecutor(RouteTargetExecutionResult.BadRequest));
        var context = CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        await fixture.Dispatcher.DispatchAsync(context);

        Assert.True(lifetime.Aborted);
        await AssertRoutePermitAvailableAsync(fixture);
    }

    private static async Task AssertRoutePermitAvailableAsync(Fixture fixture)
    {
        var admitted = await fixture.Admission.TryAcquireRouteAsync(
            fixture.Snapshot,
            fixture.Match,
            CreateContext());
        Assert.NotNull(admitted.Lease);
        Assert.Null(admitted.Rejection);
        admitted.Lease!.Dispose();
    }

    private static Fixture CreateFixture(IRouteTargetExecutor target, long maxBodyBytes = 8)
    {
        var route = new RouteConfiguration(
            RoutingTestData.Id(930),
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/selected", default, default),
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            maxRequestBodyBytes: maxBodyBytes,
            maxConcurrentRequests: 1);
        var configuration = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(
                version: 1,
                maxRequestBodyBytes: 8,
                maxConcurrentRequests: 4,
                configurationPollInterval: TimeSpan.FromSeconds(1)),
            ImmutableArray.Create(route),
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var snapshot = new HostRoutingSnapshot(configuration, RoutingTestData.Build(route));
        var result = snapshot.Matcher.Match(new RouteMatchInput("/selected", "example.test", "GET"));
        var admission = new HostRequestAdmission();
        return new Fixture(
            snapshot,
            Assert.IsType<RouteMatch>(result.Match),
            admission,
            new HostRouteDispatcher(
                new FixedSnapshotAccessor(snapshot),
                NoOpRouteFallbackDispatcher.Instance,
                target,
                admission,
                NullLogger.Instance));
    }

    private static DefaultHttpContext CreateContext(byte[]? body = null)
    {
        var content = body ?? [];
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "GET";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/selected";
        context.Request.Body = new MemoryStream(content);
        context.Request.ContentLength = content.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedSnapshotAccessor(HostRoutingSnapshot snapshot) : IHostRoutingSnapshotAccessor
    {
        public HostRoutingSnapshot Current { get; } = snapshot;
    }

    private sealed class ResultExecutor(RouteTargetExecutionResult result) : IRouteTargetExecutor
    {
        public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }

    private sealed class ThrowingExecutor : IRouteTargetExecutor
    {
        public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed record Fixture(
        HostRoutingSnapshot Snapshot,
        RouteMatch Match,
        HostRequestAdmission Admission,
        HostRouteDispatcher Dispatcher);
}
