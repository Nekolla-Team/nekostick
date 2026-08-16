using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Proxy;
using Xunit;

using ContractHeaderRewrite = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;
using ContractHeaderRewriteOperation = Nekolla.Nekostick.Contracts.HeaderRewriteOperation;
using ProxyHeaderRewrite = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;
using ProxyHeaderRewriteOperation = Nekolla.Nekostick.Proxy.HeaderRewriteOperation;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HeaderRewriteTemplateTests
{
    [Theory]
    [InlineData("prefix{")]
    [InlineData("prefix}")]
    [InlineData("{unknown}")]
    [InlineData("{clientIp")]
    [InlineData("{clientIp}{unknown}")]
    public void SemanticValidationRejectsMalformedHeaderTemplates(string template)
    {
        var route = CreateRoute(
            RoutingTestData.Id(530),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    template)));

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(route)));
    }

    [Fact]
    public void SemanticValidationRejectsControlCharactersAndAcceptsExactTokens()
    {
        var controlRoute = CreateRoute(
            RoutingTestData.Id(531),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    "prefix\r\n")));
        var nulRoute = CreateRoute(
            RoutingTestData.Id(541),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    "prefix\0")));
        var validRoute = CreateRoute(
            RoutingTestData.Id(532),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    "{clientIp}/{path}/{method}/{host}")));

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(controlRoute)));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(nulRoute)));
        Assert.True(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(validRoute)));
    }

    [Fact]
    public void RemoveRequiresNullValueAndHostRewriteIsRequestSetOnly()
    {
        var removeWithValue = CreateRoute(
            RoutingTestData.Id(533),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Remove,
                    "X-Template",
                    "unexpected")));
        var requestHostSet = CreateRoute(
            RoutingTestData.Id(534),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "Host",
                    "{host}")));
        var requestHostAdd = CreateRoute(
            RoutingTestData.Id(535),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Add,
                    "Host",
                    "{host}")));
        var responseHostSet = CreateRoute(
            RoutingTestData.Id(536),
            responseRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "Host",
                    "{host}")));

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(removeWithValue)));
        Assert.True(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(requestHostSet)));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(requestHostAdd)));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(responseHostSet)));
    }

    [Theory]
    [InlineData(ContractHeaderRewriteOperation.Remove)]
    [InlineData(ContractHeaderRewriteOperation.Set)]
    [InlineData(ContractHeaderRewriteOperation.Add)]
    public void XRealIpRewriteIsRejectedAtSnapshotValidation(
        ContractHeaderRewriteOperation operation)
    {
        var value = operation == ContractHeaderRewriteOperation.Remove
            ? null
            : "{clientIp}";
        var route = CreateRoute(
            RoutingTestData.Id(542),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(operation, "X-Real-IP", value)));

        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(CreateSnapshot(route)));
    }

    [Fact]
    public void InvalidTemplateCandidateDoesNotReplacePublishedSnapshot()
    {
        var prior = CreateSnapshot();
        var invalid = CreateSnapshot(
            CreateRoute(
                RoutingTestData.Id(537),
                requestRewrites: ImmutableArray.Create(
                    new ContractHeaderRewrite(
                        ContractHeaderRewriteOperation.Set,
                        "X-Template",
                        "{unknown}"))));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(prior));
        Assert.False(holder.TryReplace(invalid));
        Assert.Same(prior, holder.Current);
    }

    [Fact]
    public void ExecutableRouteBuildCompilesValidatedRewriteTemplates()
    {
        var route = CreateRoute(
            RoutingTestData.Id(538),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    "{path}")));

        Assert.True(ExecutableRouteBuilder.TryBuild(
            CreateSnapshot(route),
            out var executableRoutes));
        Assert.Single(executableRoutes);
        Assert.Single(executableRoutes[route.Id].RequestHeaderRewrites);
    }

    [Fact]
    public void ExecutableRouteBuildRejectsAnInvalidTemplateBeforePublication()
    {
        var route = CreateRoute(
            RoutingTestData.Id(540),
            requestRewrites: ImmutableArray.Create(
                new ContractHeaderRewrite(
                    ContractHeaderRewriteOperation.Set,
                    "X-Template",
                    "{unknown}")));

        Assert.False(ExecutableRouteBuilder.TryBuild(
            CreateSnapshot(route),
            out var executableRoutes));
        Assert.Empty(executableRoutes);
    }

    [Fact]
    public async Task RequestTemplateExpansionUsesContextAndAssignsHostThroughTypedProperty()
    {
        var rewrites = ImmutableArray.Create(
            new ProxyHeaderRewrite(
                ProxyHeaderRewriteOperation.Set,
                "X-Expanded",
                "{clientIp}/{path}/{method}/{host}"),
            new ProxyHeaderRewrite(
                ProxyHeaderRewriteOperation.Set,
                "Host",
                "{host}"));
        var request = new MicroserviceProxyRequest(
            RoutingTestData.Id(539),
            "/matched",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(10),
                activityTimeout: TimeSpan.FromSeconds(30),
                httpTotalTimeout: TimeSpan.FromSeconds(100),
                websocketIdleTimeout: TimeSpan.FromSeconds(120)),
            requestHeaderRewrites: rewrites,
            headerExpansionContext: new RequestHeaderExpansionContext(
                "client",
                "/matched",
                "POST",
                "safe.example"));
        var transformer = new MicroserviceHttpTransformer(request);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/matched";
        context.Response.Body = new MemoryStream();
        using var proxyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "http://destination.invalid/matched");

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "http://destination.invalid",
            CancellationToken.None);

        Assert.True(proxyRequest.Headers.Contains("X-Expanded"));
        Assert.NotNull(proxyRequest.Headers.Host);
    }

    [Theory]
    [InlineData(ProxyHeaderRewriteOperation.Remove)]
    [InlineData(ProxyHeaderRewriteOperation.Set)]
    [InlineData(ProxyHeaderRewriteOperation.Add)]
    public async Task XRealIpRewriteIsRejectedByRuntimePolicy(
        ProxyHeaderRewriteOperation operation)
    {
        var value = operation == ProxyHeaderRewriteOperation.Remove
            ? null
            : "{clientIp}";
        var request = new MicroserviceProxyRequest(
            RoutingTestData.Id(543),
            "/matched",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(10),
                activityTimeout: TimeSpan.FromSeconds(30),
                httpTotalTimeout: TimeSpan.FromSeconds(100),
                websocketIdleTimeout: TimeSpan.FromSeconds(120)),
            requestHeaderRewrites: ImmutableArray.Create(
                new ProxyHeaderRewrite(operation, "X-Real-IP", value)),
            headerExpansionContext: new RequestHeaderExpansionContext(
                string.Empty,
                "/matched",
                "GET",
                string.Empty));
        var transformer = new MicroserviceHttpTransformer(request);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/matched";
        context.Response.Body = new MemoryStream();
        using var proxyRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://destination.invalid/matched");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transformer.TransformRequestAsync(
                context,
                proxyRequest,
                "http://destination.invalid",
                CancellationToken.None).AsTask());
    }

    private static HostConfigurationSnapshot CreateSnapshot(RouteConfiguration? route = null) =>
        new(
            1,
            new GlobalSettingsConfiguration(version: 1),
            route is null
                ? ImmutableArray<RouteConfiguration>.Empty
                : ImmutableArray.Create(route),
            default,
            default,
            default);

    private static RouteConfiguration CreateRoute(
        Guid id,
        ImmutableArray<ContractHeaderRewrite> requestRewrites = default,
        ImmutableArray<ContractHeaderRewrite> responseRewrites = default) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/template", default, default),
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestRewrites,
            responseRewrites,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
}
