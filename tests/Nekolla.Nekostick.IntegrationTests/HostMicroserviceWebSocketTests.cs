using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class HostMicroserviceWebSocketTests
{
    [Fact]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "WebSocket failures are converted to the fixed target stage only.")]
    public async Task RealKestrelHostTargetAdapterUpgradesAndEchoesOneWebSocketMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = InProcessFixtureServer.Start("websocket");
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
            "/ws",
            new MicroserviceRouteTargetConfiguration(serviceId),
            ForwardingMode.Preserve,
            matcherType: RouteMatcherType.Exact);
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

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(
                IPAddress.Loopback,
                0,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1));
        builder.Services.AddMicroserviceProxy();
        builder.Services.AddSingleton<IMicroserviceEndpointResolver>(resolver);
        var app = builder.Build();
        app.UseWebSockets();
        var targetExecutor = HostIntegrationTestSupport.CreateHostTargetExecutor(
            app.Services.GetRequiredService<MicroserviceHttpExecutor>());
        var stageSignal = new TaskCompletionSource<IntegrationStageEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Run(async context =>
        {
            IntegrationStageEvidence disposition;
            try
            {
                disposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                    holder,
                    targetExecutor,
                    (DefaultHttpContext)context);
            }
            catch (Exception)
            {
                disposition = new(
                    IntegrationStageKind.TargetExecuted,
                    HostTargetExecutionDisposition.SafeFailure);
            }

            stageSignal.TrySetResult(disposition);
            if (disposition.TargetDisposition != HostTargetExecutionDisposition.Handled
                && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });

        try
        {
            await app.StartAsync(cancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            var address = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var websocketUri = new UriBuilder(
                Uri.UriSchemeWs,
                address.Host,
                address.Port,
                "/ws").Uri;
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(websocketUri, cancellationToken);

                byte[] payload = [0x44, 0x45, 0x2d, 0x57, 0x53];
                await client.SendAsync(
                    payload.AsMemory(),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken);
                var received = new byte[payload.Length];
                var receiveResult = await client.ReceiveAsync(received.AsMemory(), cancellationToken);

                Assert.Equal(WebSocketMessageType.Binary, receiveResult.MessageType);
                Assert.True(receiveResult.EndOfMessage);
                Assert.Equal(payload.Length, receiveResult.Count);
                Assert.True(payload.AsSpan().SequenceEqual(received));

                await client.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    null,
                    cancellationToken);
                Assert.Equal(WebSocketState.Closed, client.State);
                var stage = await stageSignal.Task.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken);
                HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(stage);
                Assert.Equal(HostTargetExecutionDisposition.Handled, stage.TargetDisposition);
                Assert.Equal(
                    (MicroserviceProxyExecutionDisposition?)MicroserviceProxyExecutionDisposition.Handled,
                    stage.ProxyDisposition);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                IntegrationStageEvidence stage;
                try
                {
                    stage = await stageSignal.Task.WaitAsync(
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    Assert.Fail("WebSocket target stage was not published.");
                    return;
                }

                HostIntegrationTestSupport.AssertNoForwarderErrorForHandled(stage);
                Assert.Equal(HostTargetExecutionDisposition.Handled, stage.TargetDisposition);
                Assert.Fail("WebSocket forwarding did not complete.");
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }
}
