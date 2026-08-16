using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

using ContractHeaderRewrite = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;
using ContractHeaderRewriteOperation = Nekolla.Nekostick.Contracts.HeaderRewriteOperation;
using ProxyHeaderRewrite = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;
using ProxyHeaderRewriteOperation = Nekolla.Nekostick.Proxy.HeaderRewriteOperation;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class HostHeaderPolicyIntegrationTests
{
    [Fact]
    public async Task RealForwarderAppliesSafeHeaderPresencePolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start("echo");
        var readiness = await fixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, readiness.Kind);
        var serviceId = HostIntegrationTestSupport.NewId();
        var resolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
                serviceId,
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint(new Uri($"http://127.0.0.1:{fixture.Port}/")))));
        var routeRewrites = ImmutableArray.Create(
            new ContractHeaderRewrite(ContractHeaderRewriteOperation.Add, "X-Order", "add"),
            new ContractHeaderRewrite(ContractHeaderRewriteOperation.Set, "X-Order", "set"),
            new ContractHeaderRewrite(ContractHeaderRewriteOperation.Remove, "X-Order", null));
        var route = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/headers",
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact,
            requestHeaderRewrites: routeRewrites);
        var holder = new HostConfigurationSnapshotHolder();
        var publication = HostIntegrationTestSupport.PublishSnapshot(
            holder,
            HostIntegrationTestSupport.CreateSnapshot(
                [route],
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
        using var request = CreateHeaderRequest("/headers");
        using var response = await host.Client.SendAsync(request, cancellationToken);
        var disposition = await host.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(disposition);
        Assert.Equal(HostTargetExecutionDisposition.Handled, disposition.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            disposition.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        var presence = document.RootElement.GetProperty("headerPresence");
        Assert.True(presence.GetProperty("authorization").GetBoolean());
        Assert.True(presence.GetProperty("cookie").GetBoolean());
        Assert.False(presence.GetProperty("connection").GetBoolean());
        Assert.True(presence.GetProperty("transferEncoding").GetBoolean());
        Assert.False(presence.GetProperty("upgrade").GetBoolean());
        Assert.False(presence.GetProperty("xForwardedHost").GetBoolean());
        Assert.False(presence.GetProperty("xRealIp").GetBoolean());
        Assert.True(presence.GetProperty("xForwardedFor").GetBoolean());
        Assert.True(presence.GetProperty("xForwardedProto").GetBoolean());

        var trustedHolder = new HostConfigurationSnapshotHolder();
        var trustedPublication = HostIntegrationTestSupport.PublishSnapshot(
            trustedHolder,
            HostIntegrationTestSupport.CreateSnapshot(
                [route],
                [serviceId],
                ["127.0.0.1/32"]));
        Assert.Equal(IntegrationStageKind.SnapshotPublished, trustedPublication.Kind);
        var trustedResolverStage = await HostIntegrationTestSupport.ProbeResolverAsync(
            resolver,
            serviceId,
            cancellationToken);
        Assert.Equal(IntegrationStageKind.ResolverAvailable, trustedResolverStage.Kind);
        await using var trustedHost = await InProcessHostTargetServer.StartAsync(
            trustedHolder,
            resolver,
            cancellationToken);
        using var trustedRequest = CreateHeaderRequest("/headers", includeSensitiveHeaders: false);
        using var trustedResponse = await trustedHost.Client.SendAsync(trustedRequest, cancellationToken);
        var trustedDisposition = await trustedHost.WaitForStageAsync(cancellationToken);

        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(trustedDisposition);
        Assert.Equal(
            HostTargetExecutionDisposition.Handled,
            trustedDisposition.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            trustedDisposition.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)trustedResponse.StatusCode);
        var trustedResponseBody = await trustedResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        using var trustedDocument = JsonDocument.Parse(trustedResponseBody);
        var trustedPresence = trustedDocument.RootElement.GetProperty("headerPresence");
        Assert.True(trustedPresence.GetProperty("transferEncoding").GetBoolean());
        Assert.False(trustedPresence.GetProperty("connection").GetBoolean());
        Assert.False(trustedPresence.GetProperty("upgrade").GetBoolean());
        Assert.True(trustedPresence.GetProperty("xForwardedFor").GetBoolean());
        Assert.True(trustedPresence.GetProperty("xForwardedProto").GetBoolean());
        Assert.False(trustedPresence.GetProperty("xForwardedHost").GetBoolean());
        Assert.False(trustedPresence.GetProperty("xRealIp").GetBoolean());
    }

    private static HttpRequestMessage CreateHeaderRequest(
        string path,
        bool includeSensitiveHeaders = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent([0x44, 0x45, 0x2d, 0x48, 0x54, 0x54, 0x50])
        };
        request.Headers.Connection.Add("X-Dynamic-Hop");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("X-Dynamic-Hop", "present");
        request.Headers.Add("X-Forwarded-Host", "untrusted.example");
        request.Headers.Add("X-Real-IP", "198.51.100.10");
        request.Headers.Add("X-Forwarded-For", "198.51.100.10");
        if (includeSensitiveHeaders)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "integration-test");
            request.Headers.Add("Cookie", "session=integration-test");
        }

        return request;
    }

    [Fact]
    public async Task HeaderRewriteOrderingAndTrustedIdentityBoundaryAreObservableWithoutValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start("echo");
        var readiness = await fixture.WaitUntilReadyAsync(cancellationToken);
        Assert.Equal(IntegrationStageKind.FixtureReady, readiness.Kind);
        var serviceId = HostIntegrationTestSupport.NewId();
        var resolver = new FixedEndpointResolver(
            ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty.Add(
                serviceId,
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint(new Uri($"http://127.0.0.1:{fixture.Port}/")))));
        var route = HostIntegrationTestSupport.CreateRoute(
            HostIntegrationTestSupport.NewId(),
            "/header-stage",
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
        var holder = new HostConfigurationSnapshotHolder();
        var publication = HostIntegrationTestSupport.PublishSnapshot(
            holder,
            HostIntegrationTestSupport.CreateSnapshot([route], [serviceId]));
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
        using var forwardingResponse = await host.Client.GetAsync(
            "/header-stage",
            cancellationToken);
        var forwardingStage = await host.WaitForStageAsync(cancellationToken);
        HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(forwardingStage);
        Assert.Equal(
            HostTargetExecutionDisposition.Handled,
            forwardingStage.TargetDisposition);
        Assert.Equal(
            (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
            forwardingStage.ProxyDisposition);
        Assert.Equal(StatusCodes.Status200OK, (int)forwardingResponse.StatusCode);
        _ = await forwardingResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        var rewrites = ImmutableArray.Create(
            new ProxyHeaderRewrite(ProxyHeaderRewriteOperation.Add, "X-Order", "add"),
            new ProxyHeaderRewrite(ProxyHeaderRewriteOperation.Set, "X-Order", "set"),
            new ProxyHeaderRewrite(ProxyHeaderRewriteOperation.Remove, "X-Order"));
        var request = new MicroserviceProxyRequest(
            serviceId,
            "/",
            timeoutPolicy: CreateDefaultTimeoutPolicy(),
            requestHeaderRewrites: rewrites,
            trustedProxyPolicy: TrustedProxyPolicy.Empty);
        var context = HostIntegrationTestSupport.CreateContext(
            "/",
            cancellationToken: cancellationToken);
        context.Request.Headers["X-Order"] = "original";
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "http://integration.test/");

        await HostIntegrationTestSupport.TransformRequestAsync(context, request, proxyRequest);

        Assert.True(proxyRequest.Headers.GetValues("X-Order").SequenceEqual(["set", "add"]));

        var untrustedContext = HostIntegrationTestSupport.CreateContext(
            "/",
            cancellationToken: cancellationToken);
        untrustedContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        untrustedContext.Request.Headers["X-Forwarded-For"] = "198.51.100.10";
        using var untrustedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://integration.test/");
        await HostIntegrationTestSupport.TransformRequestAsync(
            untrustedContext,
            new MicroserviceProxyRequest(
                serviceId,
                "/",
                timeoutPolicy: CreateDefaultTimeoutPolicy(),
                trustedProxyPolicy: TrustedProxyPolicy.Empty),
            untrustedRequest);

        var untrustedValues = untrustedRequest.Headers.GetValues("X-Forwarded-For").ToArray();
        Assert.Single(untrustedValues);

        var trustedContext = HostIntegrationTestSupport.CreateContext(
            "/",
            cancellationToken: cancellationToken);
        trustedContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        trustedContext.Request.Headers["X-Forwarded-For"] = "198.51.100.10";
        using var trustedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://integration.test/");
        await HostIntegrationTestSupport.TransformRequestAsync(
            trustedContext,
            new MicroserviceProxyRequest(
                serviceId,
                "/",
                timeoutPolicy: CreateDefaultTimeoutPolicy(),
                trustedProxyPolicy: new TrustedProxyPolicy(["127.0.0.1/32"])),
            trustedRequest);

        var trustedValues = trustedRequest.Headers.GetValues("X-Forwarded-For").ToArray();
        Assert.Equal(2, trustedValues.Length);
        Assert.True(trustedRequest.Headers.Contains("Forwarded"));
        Assert.False(trustedRequest.Headers.Contains("X-Forwarded-Host"));
        Assert.False(trustedRequest.Headers.Contains("X-Real-IP"));
    }

    private static MicroserviceTimeoutPolicy CreateDefaultTimeoutPolicy() =>
        new(
            connectTimeout: TimeSpan.FromSeconds(10),
            activityTimeout: TimeSpan.FromSeconds(30),
            httpTotalTimeout: TimeSpan.FromSeconds(100),
            websocketIdleTimeout: TimeSpan.FromSeconds(120));
}
