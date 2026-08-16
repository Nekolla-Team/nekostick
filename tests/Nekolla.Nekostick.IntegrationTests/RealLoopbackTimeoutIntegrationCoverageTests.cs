using System.Collections.Immutable;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class RealLoopbackTimeoutIntegrationCoverageTests
{
    [Fact]
    public async Task DelayedResponseHeadersProduceGenericGatewayTimeoutWithTypedEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start(
            "delay",
            "--delay-ms",
            "500");
        await AssertFixtureReadyAsync(fixture, cancellationToken);

        var timeouts = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(2),
            httpActivityTimeout: TimeSpan.FromMilliseconds(80),
            httpTotalTimeout: TimeSpan.FromSeconds(2),
            webSocketIdleTimeout: TimeSpan.FromSeconds(2));
        var scenario = CreateScenario(fixture.Port, "/loopback-timeout-headers", timeouts);
        await AssertResolverAvailableAsync(scenario, cancellationToken);

        await using var host = await InProcessHostTargetServer.StartAsync(
            scenario.Holder,
            scenario.Resolver,
            cancellationToken);
        using var response = await host.Client.GetAsync(
            "/loopback-timeout-headers",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var stage = await host.WaitForStageAsync(cancellationToken);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, (int)response.StatusCode);
        Assert.Equal(HostTargetExecutionDisposition.GatewayTimeout, stage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.GatewayTimeout,
            stage.ProxyDisposition);
        Assert.Equal(ForwarderError.RequestTimedOut, stage.ForwarderErrorCategory);
    }

    [Fact]
    public async Task NormalHttpTotalDeadlineAndActiveStreamRemainDistinctAtTheHostBoundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeouts = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(2),
            httpActivityTimeout: TimeSpan.FromMilliseconds(400),
            httpTotalTimeout: TimeSpan.FromMilliseconds(160),
            webSocketIdleTimeout: TimeSpan.FromSeconds(2));

        await using var delayedFixture = InProcessFixtureServer.Start(
            "delay",
            "--delay-ms",
            "500");
        await AssertFixtureReadyAsync(delayedFixture, cancellationToken);
        var delayedScenario = CreateScenario(
            delayedFixture.Port,
            "/loopback-timeout-total",
            timeouts);
        await AssertResolverAvailableAsync(delayedScenario, cancellationToken);
        await using var delayedHost = await InProcessHostTargetServer.StartAsync(
            delayedScenario.Holder,
            delayedScenario.Resolver,
            cancellationToken);

        using var delayedResponse = await delayedHost.Client.GetAsync(
            "/loopback-timeout-total",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var delayedStage = await delayedHost.WaitForStageAsync(cancellationToken);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, (int)delayedResponse.StatusCode);
        Assert.Equal(
            HostTargetExecutionDisposition.GatewayTimeout,
            delayedStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.GatewayTimeout,
            delayedStage.ProxyDisposition);
        AssertAllowedCancellationForwarderError(delayedStage);

        await using var streamFixture = InProcessFixtureServer.Start(
            "stream",
            "--response-bytes",
            "64",
            "--response-pattern",
            "S",
            "--chunk-size",
            "1",
            "--chunk-delay-ms",
            "40",
            "--chunked");
        await AssertFixtureReadyAsync(streamFixture, cancellationToken);
        var streamScenario = CreateScenario(
            streamFixture.Port,
            "/loopback-timeout-stream",
            timeouts);
        await AssertResolverAvailableAsync(streamScenario, cancellationToken);
        await using var streamHost = await InProcessHostTargetServer.StartAsync(
            streamScenario.Holder,
            streamScenario.Resolver,
            cancellationToken);

        using var streamResponse = await streamHost.Client.GetAsync(
            "/loopback-timeout-stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var streamStage = await streamHost.WaitForStageAsync(cancellationToken);
        Assert.Equal(StatusCodes.Status200OK, (int)streamResponse.StatusCode);
        Assert.Equal(
            HostTargetExecutionDisposition.GatewayTimeout,
            streamStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.GatewayTimeout,
            streamStage.ProxyDisposition);
        AssertAllowedTotalDeadlineForwarderError(streamStage);
    }

    [Fact]
    public async Task ExternalClientCancellationProducesCancelledEvidenceWithoutGatewayTimeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start(
            "delay",
            "--delay-ms",
            "1000");
        await AssertFixtureReadyAsync(fixture, cancellationToken);

        var timeouts = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(2),
            httpActivityTimeout: TimeSpan.FromSeconds(5),
            httpTotalTimeout: TimeSpan.FromSeconds(5),
            webSocketIdleTimeout: TimeSpan.FromSeconds(2));
        var scenario = CreateScenario(fixture.Port, "/loopback-cancel", timeouts);
        await AssertResolverAvailableAsync(scenario, cancellationToken);
        await using var host = await InProcessHostTargetServer.StartAsync(
            scenario.Holder,
            scenario.Resolver,
            cancellationToken);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromMilliseconds(80));
        var requestCancelled = false;
        try
        {
            using var response = await host.Client.GetAsync(
                "/loopback-cancel",
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            requestCancelled = true;
        }

        var stage = await host.WaitForStageAsync(cancellationToken);
        Assert.True(requestCancelled);
        Assert.Equal(HostTargetExecutionDisposition.Cancelled, stage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Cancelled,
            stage.ProxyDisposition);
        AssertAllowedCancellationForwarderError(stage);
    }

    [Fact]
    public async Task WebSocketIdleTimeoutUsesItsIndependentPolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start("websocket");
        await AssertFixtureReadyAsync(fixture, cancellationToken);

        var timeouts = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(2),
            httpActivityTimeout: TimeSpan.FromSeconds(2),
            httpTotalTimeout: TimeSpan.FromSeconds(2),
            webSocketIdleTimeout: TimeSpan.FromMilliseconds(100));
        var scenario = CreateScenario(fixture.Port, "/loopback-websocket-idle", timeouts);
        await AssertResolverAvailableAsync(scenario, cancellationToken);
        await using var host = await InProcessHostTargetServer.StartAsync(
            scenario.Holder,
            scenario.Resolver,
            cancellationToken);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            CreateWebSocketAddress(host.Client, "/loopback-websocket-idle"),
            cancellationToken);

        var stage = await host.WaitForStageAsync(cancellationToken);
        Assert.Equal(HostTargetExecutionDisposition.GatewayTimeout, stage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.GatewayTimeout,
            stage.ProxyDisposition);
        Assert.Equal(ForwarderError.UpgradeActivityTimeout, stage.ForwarderErrorCategory);
    }

    [Fact]
    public async Task WebSocketDoesNotUseNormalHttpTotalDeadline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start(
            "websocket",
            "--ws-close-after-ms",
            "180");
        await AssertFixtureReadyAsync(fixture, cancellationToken);

        var timeouts = new ProxyTimeoutConfiguration(
            connectTimeout: TimeSpan.FromSeconds(2),
            httpActivityTimeout: TimeSpan.FromSeconds(2),
            httpTotalTimeout: TimeSpan.FromMilliseconds(80),
            webSocketIdleTimeout: TimeSpan.FromMilliseconds(500));
        var scenario = CreateScenario(fixture.Port, "/loopback-websocket-total", timeouts);
        await AssertResolverAvailableAsync(scenario, cancellationToken);
        await using var host = await InProcessHostTargetServer.StartAsync(
            scenario.Holder,
            scenario.Resolver,
            cancellationToken);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            CreateWebSocketAddress(host.Client, "/loopback-websocket-total"),
            cancellationToken);

        var stage = await host.WaitForStageAsync(cancellationToken);
        Assert.Equal(HostTargetExecutionDisposition.Handled, stage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            stage.ProxyDisposition);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(stage);
    }

    private static async Task AssertFixtureReadyAsync(
        InProcessFixtureServer fixture,
        CancellationToken cancellationToken)
    {
        var readiness = await fixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, readiness.Kind);
    }

    private static async Task AssertResolverAvailableAsync(
        LoopbackTimeoutScenario scenario,
        CancellationToken cancellationToken)
    {
        var stage = await HostIntegrationTestSupport.ProbeResolverAsync(
            scenario.Resolver,
            scenario.ServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, stage.Kind);
    }

    private static LoopbackTimeoutScenario CreateScenario(
        int fixturePort,
        string routePath,
        ProxyTimeoutConfiguration timeouts)
    {
        var serviceId = HostIntegrationTestSupport.NewId();
        var resolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
                serviceId,
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint(new Uri($"http://127.0.0.1:{fixturePort}/")))));
        var route = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            routePath,
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var holder = new HostConfigurationSnapshotHolder();
        var publication = HostIntegrationTestSupport.PublishSnapshot(
            holder,
            HostIntegrationTestSupport.CreateSnapshot(
                [route],
                [serviceId],
                proxyTimeouts: timeouts));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, publication.Kind);
        return new(serviceId, holder, resolver);
    }

    private static Uri CreateWebSocketAddress(HttpClient client, string path)
    {
        var address = client.BaseAddress!;
        return new UriBuilder(address)
        {
            Scheme = Uri.UriSchemeWs,
            Path = path
        }.Uri;
    }

    private static void AssertAllowedCancellationForwarderError(IntegrationStageEvidence stage)
    {
        var category = stage.ForwarderErrorCategory;
        Assert.True(category.HasValue);
        Assert.Contains(category.Value, AllowedCancellationForwarderErrors);
    }

    private static void AssertAllowedTotalDeadlineForwarderError(IntegrationStageEvidence stage)
    {
        var category = stage.ForwarderErrorCategory;
        Assert.True(category.HasValue);
        Assert.Contains(category.Value, AllowedTotalDeadlineForwarderErrors);
    }

    private static readonly ImmutableHashSet<ForwarderError> AllowedCancellationForwarderErrors =
        ImmutableHashSet.Create(
            ForwarderError.RequestCanceled,
            ForwarderError.RequestTimedOut);

    private static readonly ImmutableHashSet<ForwarderError> AllowedTotalDeadlineForwarderErrors =
        ImmutableHashSet.Create(
            ForwarderError.RequestCanceled,
            ForwarderError.RequestTimedOut,
            ForwarderError.ResponseBodyCanceled);

    private sealed record LoopbackTimeoutScenario(
        Guid ServiceId,
        HostConfigurationSnapshotHolder Holder,
        FixedEndpointResolver Resolver);
}
