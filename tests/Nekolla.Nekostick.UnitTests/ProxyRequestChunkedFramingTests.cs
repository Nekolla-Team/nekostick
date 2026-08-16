using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyRequestChunkedFramingTests
{
    [Fact]
    public async Task ChunkedStreamingContentIsPreservedAndCanonicalizedAfterRequestPolicy()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/upload";
        context.Request.Headers["Connection"] = "X-Dynamic-Hop";
        context.Request.Headers["Upgrade"] = "websocket";
        context.Response.Body = new MemoryStream();

        using var content = new StreamContent(new MemoryStream());
        using var proxyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "http://destination.invalid/upload")
        {
            Content = content
        };
        proxyRequest.Headers.TransferEncodingChunked = true;
        proxyRequest.Headers.TryAddWithoutValidation("Connection", ["X-Dynamic-Hop"]);
        proxyRequest.Headers.TryAddWithoutValidation("X-Dynamic-Hop", ["dynamic"]);
        proxyRequest.Headers.TryAddWithoutValidation("Upgrade", ["websocket"]);
        var originalContent = proxyRequest.Content;
        var transformer = new MicroserviceHttpTransformer(
            new MicroserviceProxyRequest(
                RoutingTestData.Id(510),
                "/upload",
                new MicroserviceTimeoutPolicy(
                    connectTimeout: TimeSpan.FromSeconds(10),
                    activityTimeout: TimeSpan.FromSeconds(30),
                    httpTotalTimeout: TimeSpan.FromSeconds(100),
                    websocketIdleTimeout: TimeSpan.FromSeconds(120))));

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "http://destination.invalid",
            CancellationToken.None);

        Assert.Same(originalContent, proxyRequest.Content);
        Assert.True(proxyRequest.Headers.TransferEncodingChunked == true);
        Assert.True(proxyRequest.Headers.TryGetValues("Transfer-Encoding", out var transferEncoding));
        Assert.Single(transferEncoding);
        Assert.False(proxyRequest.Headers.Contains("Connection"));
        Assert.False(proxyRequest.Headers.Contains("X-Dynamic-Hop"));
        Assert.False(proxyRequest.Headers.Contains("Upgrade"));
    }

    [Fact]
    public async Task RequestRewritesRouteContentAndOrdinaryHeadersByHeaderFamily()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/upload";
        context.Response.Body = new MemoryStream();

        using var content = new StreamContent(new MemoryStream());
        content.Headers.TryAddWithoutValidation("Content-Type", ["initial"]);
        using var proxyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "http://destination.invalid/upload")
        {
            Content = content
        };
        proxyRequest.Headers.TryAddWithoutValidation("X-Request-Policy", ["initial"]);
        var rewrites = ImmutableArray.Create(
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Remove, "Content-Type"),
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Set, "Content-Type", "set"),
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Add, "Content-Type", "add"),
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Remove, "X-Request-Policy"),
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Set, "X-Request-Policy", "set"),
            new HeaderRewriteConfiguration(HeaderRewriteOperation.Add, "X-Request-Policy", "add"));
        var transformer = new MicroserviceHttpTransformer(
            new MicroserviceProxyRequest(
                RoutingTestData.Id(511),
                "/upload",
                new MicroserviceTimeoutPolicy(
                    connectTimeout: TimeSpan.FromSeconds(10),
                    activityTimeout: TimeSpan.FromSeconds(30),
                    httpTotalTimeout: TimeSpan.FromSeconds(100),
                    websocketIdleTimeout: TimeSpan.FromSeconds(120)),
                requestHeaderRewrites: rewrites));

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "http://destination.invalid",
            CancellationToken.None);

        Assert.True(content.Headers.TryGetValues("Content-Type", out var contentValues));
        Assert.Equal(2, contentValues.Count());
        Assert.True(proxyRequest.Headers.TryGetValues("X-Request-Policy", out var requestValues));
        Assert.Equal(2, requestValues.Count());
    }

    [Fact]
    public async Task RequestProtectedRewriteRemainsRejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/upload";
        context.Response.Body = new MemoryStream();

        using var content = new StreamContent(new MemoryStream());
        using var proxyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "http://destination.invalid/upload")
        {
            Content = content
        };
        var transformer = new MicroserviceHttpTransformer(
            new MicroserviceProxyRequest(
                RoutingTestData.Id(512),
                "/upload",
                new MicroserviceTimeoutPolicy(
                    connectTimeout: TimeSpan.FromSeconds(10),
                    activityTimeout: TimeSpan.FromSeconds(30),
                    httpTotalTimeout: TimeSpan.FromSeconds(100),
                    websocketIdleTimeout: TimeSpan.FromSeconds(120)),
                requestHeaderRewrites: ImmutableArray.Create(
                    new HeaderRewriteConfiguration(
                        HeaderRewriteOperation.Set,
                        "Content-Length",
                        "blocked"))));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transformer.TransformRequestAsync(
                context,
                proxyRequest,
                "http://destination.invalid",
                CancellationToken.None).AsTask());
    }
}
