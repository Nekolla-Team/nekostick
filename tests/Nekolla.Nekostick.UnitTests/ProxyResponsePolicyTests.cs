using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Proxy;
using Xunit;

using ProxyHeaderRewriteConfiguration = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyResponsePolicyTests
{
    [Fact]
    public async Task OrdinaryResponseRewriteIsAppliedToTheFinalResponseHeaders()
    {
        using var proxyResponse = CreateResponse();
        var context = CreateContext();
        var transformer = CreateTransformer(
            new ProxyHeaderRewriteConfiguration(HeaderRewriteOperation.Set, "X-Policy", "set"));

        var transformed = await transformer.TransformResponseAsync(
            context,
            proxyResponse,
            CancellationToken.None);

        Assert.True(transformed);
        Assert.True(context.Response.Headers.TryGetValue("X-Policy", out var values));
        Assert.Equal(1, values.Count);
    }

    [Fact]
    public async Task FixedAndConnectionTokenHopByHopHeadersDoNotReachTheFinalResponse()
    {
        using var proxyResponse = CreateResponse();
        foreach (var name in new[]
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade"
        })
        {
            proxyResponse.Headers.TryAddWithoutValidation(name, ["hop"]);
        }

        proxyResponse.Headers.TryAddWithoutValidation("Connection", ["X-Dynamic-Hop"]);
        proxyResponse.Headers.TryAddWithoutValidation("X-Dynamic-Hop", ["dynamic"]);
        var context = CreateContext();

        await CreateTransformer().TransformResponseAsync(
            context,
            proxyResponse,
            CancellationToken.None);

        foreach (var name in new[]
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
            "X-Dynamic-Hop"
        })
        {
            Assert.False(
                context.Response.Headers.ContainsKey(name),
                $"Unexpected final response header: {name}");
        }
    }

    [Fact]
    public async Task ResponseRemoveSetAddRunsAgainstTheFinalHeaderCollection()
    {
        using var proxyResponse = CreateResponse();
        proxyResponse.Headers.TryAddWithoutValidation("X-Policy", ["upstream"]);
        var transformer = CreateTransformer(
            new ProxyHeaderRewriteConfiguration(HeaderRewriteOperation.Remove, "X-Policy"),
            new ProxyHeaderRewriteConfiguration(HeaderRewriteOperation.Set, "X-Policy", "set"),
            new ProxyHeaderRewriteConfiguration(HeaderRewriteOperation.Add, "X-Policy", "add"));
        var context = CreateContext();

        await transformer.TransformResponseAsync(context, proxyResponse, CancellationToken.None);

        Assert.True(context.Response.Headers.TryGetValue("X-Policy", out var values));
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public async Task SetCookieValuesRemainSeparateFinalHeaderValues()
    {
        using var proxyResponse = CreateResponse();
        proxyResponse.Headers.TryAddWithoutValidation(
            "Set-Cookie",
            ["first=one", "second=two"]);
        var context = CreateContext();

        await CreateTransformer().TransformResponseAsync(
            context,
            proxyResponse,
            CancellationToken.None);

        Assert.True(context.Response.Headers.TryGetValue("Set-Cookie", out var values));
        Assert.Equal(2, values.Count);
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Forwarded")]
    [InlineData("Host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("X-Forwarded-For")]
    public async Task ProtectedFramingAndIdentityRewritesAreRejected(string headerName)
    {
        using var proxyResponse = CreateResponse();
        var context = CreateContext();
        var transformer = CreateTransformer(
            new ProxyHeaderRewriteConfiguration(HeaderRewriteOperation.Set, headerName, "blocked"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transformer.TransformResponseAsync(context, proxyResponse, CancellationToken.None));
    }

    private static MicroserviceHttpTransformer CreateTransformer(
        params ProxyHeaderRewriteConfiguration[] responseRewrites)
    {
        var rewrites = responseRewrites.Length == 0
            ? ImmutableArray<ProxyHeaderRewriteConfiguration>.Empty
            : ImmutableArray.CreateRange(responseRewrites);
        var request = new MicroserviceProxyRequest(
            RoutingTestData.Id(500),
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(10),
                activityTimeout: TimeSpan.FromSeconds(30),
                httpTotalTimeout: TimeSpan.FromSeconds(100),
                websocketIdleTimeout: TimeSpan.FromSeconds(120)),
            responseHeaderRewrites: rewrites);
        return new MicroserviceHttpTransformer(request);
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent([])
        };
        return response;
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }
}
