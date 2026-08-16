using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyOutboundIdentityBoundaryTests
{
    private static readonly StringValues PresenceOnly = new(string.Empty);

    [Fact]
    public async Task TrustedValidChainSharesEffectiveClientWithTemplateAndForwardingIdentity()
    {
        var capture = await ExecuteIdentityScenario(
            IPAddress.Parse("192.0.2.10"),
            new TrustedProxyPolicy(["192.0.2.0/24"]),
            "198.51.100.7, 192.0.2.20",
            "for=198.51.100.7, for=192.0.2.20");

        AssertSharedEffectiveClient(capture, expectedChainLength: 3);
    }

    [Fact]
    public async Task UntrustedPeerIgnoresSuppliedChainForTemplateAndForwardingIdentity()
    {
        var capture = await ExecuteIdentityScenario(
            IPAddress.Parse("203.0.113.10"),
            new TrustedProxyPolicy(["192.0.2.0/24"]),
            "198.51.100.7",
            "for=198.51.100.7");

        AssertSharedEffectiveClient(capture, expectedChainLength: 1);
    }

    [Fact]
    public async Task MalformedTrustedChainFallsBackToDirectPeerForTemplateAndForwardingIdentity()
    {
        var capture = await ExecuteIdentityScenario(
            IPAddress.Parse("192.0.2.10"),
            new TrustedProxyPolicy(["192.0.2.0/24"]),
            "malformed",
            null);

        AssertSharedEffectiveClient(capture, expectedChainLength: 1);
    }

    [Fact]
    public async Task FinalInvokerBoundaryContainsOnlyPolicyAuthorizedIdentityHeaderNames()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Path = "/";
        context.Request.Headers["X-Forwarded-Host"] = PresenceOnly;
        context.Request.Headers["X-Real-IP"] = PresenceOnly;
        context.Request.Headers["X-Forwarded-For"] = PresenceOnly;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();

        using var outboundRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(CreateDestinationPrefix() + "/"));
        var transformer = new MicroserviceHttpTransformer(
            new MicroserviceProxyRequest(
                RoutingTestData.Id(520),
                "/",
                new MicroserviceTimeoutPolicy(
                    connectTimeout: TimeSpan.FromSeconds(10),
                    activityTimeout: TimeSpan.FromSeconds(30),
                    httpTotalTimeout: TimeSpan.FromSeconds(100),
                    websocketIdleTimeout: TimeSpan.FromSeconds(120))));
        var handler = new HeaderPresenceHandler();
        using var invoker = new HttpMessageInvoker(handler);

        await transformer.TransformRequestAsync(
            context,
            outboundRequest,
            CreateDestinationPrefix(),
            CancellationToken.None);
        using var response = await invoker.SendAsync(outboundRequest, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(handler.Contains("X-Forwarded-Host"));
        Assert.False(handler.Contains("X-Real-IP"));
        Assert.True(handler.Contains("X-Forwarded-For"));
        Assert.True(handler.Contains("Forwarded"));
        Assert.True(handler.Contains("X-Forwarded-Proto"));
    }

    private static string CreateDestinationPrefix() =>
        Uri.UriSchemeHttp + Uri.SchemeDelimiter + IPAddress.Loopback;

    private static async Task<IdentityCaptureHandler> ExecuteIdentityScenario(
        IPAddress remoteAddress,
        TrustedProxyPolicy trustedProxyPolicy,
        string? xForwardedFor,
        string? forwarded)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Path = "/";
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Response.Body = new MemoryStream();
        if (xForwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = xForwardedFor;
        }

        if (forwarded is not null)
        {
            context.Request.Headers["Forwarded"] = forwarded;
        }

        var effectiveClientIdentity = MicroserviceHttpTransformer
            .ResolveEffectiveClientIdentity(context, trustedProxyPolicy);
        var request = new MicroserviceProxyRequest(
            RoutingTestData.Id(521),
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(10),
                activityTimeout: TimeSpan.FromSeconds(30),
                httpTotalTimeout: TimeSpan.FromSeconds(100),
                websocketIdleTimeout: TimeSpan.FromSeconds(120)),
            requestHeaderRewrites: ImmutableArray.Create(
                new HeaderRewriteConfiguration(
                    HeaderRewriteOperation.Set,
                    "X-Effective-Client",
                    "{clientIp}")),
            trustedProxyPolicy: trustedProxyPolicy,
            headerExpansionContext: new RequestHeaderExpansionContext(
                effectiveClientIdentity.ClientIp,
                "/",
                "GET",
                string.Empty,
                effectiveClientIdentity));
        var transformer = new MicroserviceHttpTransformer(request);
        var handler = new IdentityCaptureHandler();
        using var invoker = new HttpMessageInvoker(handler);
        using var outboundRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(CreateDestinationPrefix() + "/"));

        await transformer.TransformRequestAsync(
            context,
            outboundRequest,
            CreateDestinationPrefix(),
            CancellationToken.None);
        using var response = await invoker.SendAsync(outboundRequest, CancellationToken.None);
        return handler;
    }

    private static void AssertSharedEffectiveClient(
        IdentityCaptureHandler capture,
        int expectedChainLength)
    {
        Assert.NotNull(capture.TemplateClient);
        Assert.NotNull(capture.XForwardedFor);
        Assert.NotNull(capture.Forwarded);

        var forwardedFor = capture.XForwardedFor!
            .Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(expectedChainLength, forwardedFor.Length);
        Assert.Equal(capture.TemplateClient, forwardedFor[0]);
        Assert.Contains("for=" + capture.TemplateClient, capture.Forwarded!);
    }

    private sealed class HeaderPresenceHandler : HttpMessageHandler
    {
        private readonly HashSet<string> _headerNames = new(StringComparer.OrdinalIgnoreCase);

        internal bool Contains(string name) => _headerNames.Contains(name);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
            {
                _headerNames.Add(header.Key);
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    _headerNames.Add(header.Key);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class IdentityCaptureHandler : HttpMessageHandler
    {
        internal string? TemplateClient { get; private set; }
        internal string? XForwardedFor { get; private set; }
        internal string? Forwarded { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            TemplateClient = ReadSingle(request, "X-Effective-Client");
            XForwardedFor = ReadJoined(request, "X-Forwarded-For");
            Forwarded = ReadJoined(request, "Forwarded");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static string? ReadSingle(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? values.SingleOrDefault()
                : null;

        private static string? ReadJoined(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? string.Join(',', values)
                : null;
    }
}
