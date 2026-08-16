using System.Collections.Immutable;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class HostMicroserviceForwardingTests
{
    [Fact]
    public async Task RealForwarderResolvesServiceAndPreservesOrStripsPathsAndQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start("echo");
        var readiness = await fixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, readiness.Kind);
        var serviceId = HostIntegrationTestSupport.NewId();
        var endpoint = new MicroserviceEndpoint(
            new Uri($"http://127.0.0.1:{fixture.Port}/"));
        var resolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
                serviceId,
                MicroserviceEndpointResolution.Available(endpoint)));
        var preserveRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/preserve",
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var stripRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/strip",
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Strip);
        var holder = new HostConfigurationSnapshotHolder();
        var publication = HostIntegrationTestSupport.PublishSnapshot(
            holder,
            HostIntegrationTestSupport.CreateSnapshot(
                [preserveRoute, stripRoute],
                [serviceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, publication.Kind);
        var resolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            resolver,
            serviceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, resolverStage.Kind);
        await using var host = await InProcessHostTargetServer.StartAsync(
            holder,
            resolver,
            cancellationToken);

        using var preserve = await host.Client.GetAsync(
            "/preserve?first&empty=",
            cancellationToken);
        var preserveStage = await host.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(preserveStage);
        Assert.Equal(HostTargetExecutionDisposition.Handled, preserveStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            preserveStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)preserve.StatusCode);
        await AssertEchoAsync(
            preserve,
            expectedPath: "/preserve",
            expectedParameterCount: 2,
            cancellationToken);

        using var strip = await host.Client.GetAsync(
            "/strip/item?first&empty=",
            cancellationToken);
        var stripStage = await host.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(stripStage);
        Assert.Equal(HostTargetExecutionDisposition.Handled, stripStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            stripStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)strip.StatusCode);
        await AssertEchoAsync(
            strip,
            expectedPath: "/item",
            expectedParameterCount: 2,
            cancellationToken);
    }

    [Fact]
    public async Task RealForwarderProvidesStreamingResponseAndSafeUpstreamFailureMapping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var streamFixture = InProcessFixtureServer.Start(
            "stream",
            "--response-bytes",
            "4096",
            "--response-pattern",
            "S",
            "--chunk-size",
            "97",
            "--chunked");
        var streamReadiness = await streamFixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, streamReadiness.Kind);
        var streamServiceId = HostIntegrationTestSupport.NewId();
        var streamResolver = CreateResolver(streamServiceId, streamFixture.Port);
        var streamRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/stream",
            new MicroserviceRouteTargetConfiguration(streamServiceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var streamHolder = new HostConfigurationSnapshotHolder();
        var streamPublication = HostIntegrationTestSupport.PublishSnapshot(
            streamHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [streamRoute],
                [streamServiceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, streamPublication.Kind);
        var streamResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            streamResolver,
            streamServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, streamResolverStage.Kind);
        await using var streamHost = await InProcessHostTargetServer.StartAsync(
            streamHolder,
            streamResolver,
            cancellationToken);
        using var streamResponse = await streamHost.Client.GetAsync(
            "/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var streamStage = await streamHost.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(streamStage);
        Assert.Equal(HostTargetExecutionDisposition.Handled, streamStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            streamStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)streamResponse.StatusCode);
        Assert.True(streamResponse.Content.Headers.ContentType?.MediaType is not null
            && streamResponse.Content.Headers.ContentType.MediaType.AsSpan().SequenceEqual(
                "application/octet-stream".AsSpan()));
        var body = await streamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(4096, body.Length);
        Assert.True(body.All(value => value == (byte)'S'));

        await using var failFixture = InProcessFixtureServer.Start(
            "fail",
            "--status-code",
            "503");
        var failReadiness = await failFixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, failReadiness.Kind);
        var failServiceId = HostIntegrationTestSupport.NewId();
        var failResolver = CreateResolver(failServiceId, failFixture.Port);
        var failRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/fail",
            new MicroserviceRouteTargetConfiguration(failServiceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var failHolder = new HostConfigurationSnapshotHolder();
        var failPublication = HostIntegrationTestSupport.PublishSnapshot(
            failHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [failRoute],
                [failServiceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, failPublication.Kind);
        var failResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            failResolver,
            failServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, failResolverStage.Kind);
        await using var failHost = await InProcessHostTargetServer.StartAsync(
            failHolder,
            failResolver,
            cancellationToken);
        using var failResponse = await failHost.Client.GetAsync("/fail", cancellationToken);
        var failStage = await failHost.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(failStage);
        Assert.Equal(HostTargetExecutionDisposition.Handled, failStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            failStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (int)failResponse.StatusCode);
        _ = await failResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    [Fact]
    public async Task DelayCancellationAndResolverOutcomesRemainTypedAndSafe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var delayFixture = InProcessFixtureServer.Start(
            "delay",
            "--delay-ms",
            "200");
        var delayReadiness = await delayFixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, delayReadiness.Kind);
        var delayServiceId = HostIntegrationTestSupport.NewId();
        var delayResolver = CreateResolver(delayServiceId, delayFixture.Port);
        var delayRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/delay",
            new MicroserviceRouteTargetConfiguration(delayServiceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var delayHolder = new HostConfigurationSnapshotHolder();
        var delayPublication = HostIntegrationTestSupport.PublishSnapshot(
            delayHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [delayRoute],
                [delayServiceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, delayPublication.Kind);
        var delayResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            delayResolver,
            delayServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, delayResolverStage.Kind);
        await using var delayHost = await InProcessHostTargetServer.StartAsync(
            delayHolder,
            delayResolver,
            cancellationToken);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        var delayAborted = false;
        try
        {
            using var delayResponse = await delayHost.Client.GetAsync(
                "/delay",
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            _ = await delayResponse.Content.ReadAsByteArrayAsync(requestCancellation.Token);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            delayAborted = true;
        }

        Assert.True(delayAborted);
        var delayStage = await delayHost.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertSafeForwarderErrorEvidence(delayStage);
        Assert.True(delayStage.TargetDisposition is
            HostTargetExecutionDisposition.Cancelled or
            HostTargetExecutionDisposition.GatewayTimeout);

        var unavailableServiceId = HostIntegrationTestSupport.NewId();
        var unavailableResolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty);
        var unavailableRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/unavailable",
            new MicroserviceRouteTargetConfiguration(unavailableServiceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var unavailableHolder = new HostConfigurationSnapshotHolder();
        var unavailablePublication = HostIntegrationTestSupport.PublishSnapshot(
            unavailableHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [unavailableRoute],
                [unavailableServiceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, unavailablePublication.Kind);
        var unavailableResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            unavailableResolver,
            unavailableServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverUnavailable, unavailableResolverStage.Kind);
        using var resolverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resolverCancellation.Cancel();
        var cancelledResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            unavailableResolver,
            unavailableServiceId,
            resolverCancellation.Token);
        Assert.Equal(IntegrationStageKind.ResolverCancelled, cancelledResolverStage.Kind);
        await using var unavailableHost = await InProcessHostTargetServer.StartAsync(
            unavailableHolder,
            unavailableResolver,
            cancellationToken);
        using var unavailableResponse = await unavailableHost.Client.GetAsync(
            "/unavailable",
            cancellationToken);
        var unavailableStage = await unavailableHost.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertSafeForwarderErrorEvidence(unavailableStage);
        Assert.Equal(
            HostTargetExecutionDisposition.Unavailable,
            unavailableStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Unavailable,
            unavailableStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (int)unavailableResponse.StatusCode);
        _ = await unavailableResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        var badServiceId = HostIntegrationTestSupport.NewId();
        var badResolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
                badServiceId,
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint(new Uri("http://127.0.0.1:1/")))));
        var badRoute = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/bad-upstream",
            new MicroserviceRouteTargetConfiguration(badServiceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var badHolder = new HostConfigurationSnapshotHolder();
        var badPublication = HostIntegrationTestSupport.PublishSnapshot(
            badHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [badRoute],
                [badServiceId]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, badPublication.Kind);
        var badResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            badResolver,
            badServiceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, badResolverStage.Kind);
        await using var badHost = await InProcessHostTargetServer.StartAsync(
            badHolder,
            badResolver,
            cancellationToken);
        using var badResponse = await badHost.Client.GetAsync(
            "/bad-upstream",
            cancellationToken);
        var badStage = await badHost.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertSafeForwarderErrorEvidence(badStage);
        Assert.Equal(
            HostTargetExecutionDisposition.BadGateway,
            badStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.BadGateway,
            badStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status502BadGateway, (int)badResponse.StatusCode);
        _ = await badResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static FixedEndpointResolver CreateResolver(Guid serviceId, int port) =>
        new(ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
            serviceId,
            MicroserviceEndpointResolution.Available(
                new MicroserviceEndpoint(new Uri($"http://127.0.0.1:{port}/")))));

    private static async Task AssertEchoAsync(
        HttpResponseMessage response,
        string expectedPath,
        int expectedParameterCount,
        CancellationToken cancellationToken)
    {
        Assert.True(response.Content.Headers.ContentType?.MediaType is not null
            && response.Content.Headers.ContentType.MediaType.AsSpan().SequenceEqual(
                "application/json".AsSpan()));
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var actualPath = root.GetProperty("path").GetString();
        Assert.True(actualPath is not null && actualPath.AsSpan().SequenceEqual(expectedPath.AsSpan()));
        var query = root.GetProperty("query");
        Assert.True(query.GetProperty("present").GetBoolean());
        Assert.Equal(expectedParameterCount, query.GetProperty("parameterCount").GetInt32());
        Assert.True(query.GetProperty("hasEmptyValue").GetBoolean());
        Assert.False(query.GetProperty("hasPercentEncoding").GetBoolean());
    }
}
