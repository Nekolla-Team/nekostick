using System.Net;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyResponseUpgradeTests
{
    [Fact]
    public async Task SwitchingProtocolsResponseKeepsWebSocketUpgradeHeaders()
    {
        using var proxyResponse = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols);
        proxyResponse.Headers.TryAddWithoutValidation("Connection", ["Upgrade"]);
        proxyResponse.Headers.TryAddWithoutValidation("Upgrade", ["websocket"]);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers["Connection"] = "Upgrade";
        context.Request.Headers["Upgrade"] = "websocket";
        context.Response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        context.Response.Headers["Connection"] = "Upgrade";
        context.Response.Headers["Upgrade"] = "websocket";
        context.Response.Body = new MemoryStream();

        var request = new MicroserviceProxyRequest(
            RoutingTestData.Id(501),
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(10),
                activityTimeout: TimeSpan.FromSeconds(30),
                httpTotalTimeout: TimeSpan.FromSeconds(100),
                websocketIdleTimeout: TimeSpan.FromSeconds(120)));
        var transformer = new MicroserviceHttpTransformer(request);
        var transformed = await transformer.TransformResponseAsync(
            context,
            proxyResponse,
            CancellationToken.None);

        Assert.True(transformed);
        Assert.Equal(StatusCodes.Status101SwitchingProtocols, context.Response.StatusCode);
        Assert.True(context.Response.Headers.TryGetValue("Connection", out var connection));
        Assert.True(context.Response.Headers.TryGetValue("Upgrade", out var upgrade));
        Assert.Equal(1, connection.Count);
        Assert.Equal(1, upgrade.Count);
    }
}
