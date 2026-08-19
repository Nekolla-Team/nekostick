using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRequestKestrelLoopbackTests
{
    [Fact]
    public async Task RealKestrelRejectsOversizedHeadersBeforeDispatcher()
    {
        var dispatcher = CreateDispatcher(CreateSettings(), NoOpRouteTargetExecutor.Instance);
        await using var host = await LoopbackHost.StartAsync(
            dispatcher,
            maximumBodyBytes: 64,
            maximumHeaderBytes: 128,
            webSockets: false,
            TestContext.Current.CancellationToken);

        var response = await SendRawAsync(
            host.Port,
            $"GET /limited HTTP/1.1\r\nHost: example.test\r\nX-Large: {new string('a', 256)}\r\n\r\n",
            TestContext.Current.CancellationToken);

        Assert.StartsWith("HTTP/1.1 431", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealKestrelMapsItsStaticBodyCeilingTo413()
    {
        var dispatcher = CreateDispatcher(CreateSettings(maximumBodyBytes: 3), NoOpRouteTargetExecutor.Instance);
        await using var host = await LoopbackHost.StartAsync(
            dispatcher,
            maximumBodyBytes: 3,
            maximumHeaderBytes: 1024,
            webSockets: false,
            TestContext.Current.CancellationToken);
        using var content = new ByteArrayContent([1, 2, 3, 4]);
        using var response = await host.Client.PostAsync(
            "/limited",
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ChunkedBodyUsesTheDynamicSnapshotLimitOnRealKestrel()
    {
        var dispatcher = CreateDispatcher(
            CreateSettings(maximumBodyBytes: 3),
            NoOpRouteTargetExecutor.Instance);
        await using var host = await LoopbackHost.StartAsync(
            dispatcher,
            maximumBodyBytes: 64,
            maximumHeaderBytes: 1024,
            webSockets: false,
            TestContext.Current.CancellationToken);
        using var content = new StreamContent(new NonSeekableReadStream([1, 2, 3, 4]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/limited")
        {
            Content = content
        };
        request.Headers.TransferEncodingChunked = true;
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RealKestrelAppliesChunkedSnapshotLimitsToDynamicTargets()
    {
        RouteTargetConfiguration[] targets =
        [
            new MicroserviceRouteTargetConfiguration(Guid.CreateVersion7()),
            new ExtensionHandlerRouteTargetConfiguration("test.handler")
        ];

        foreach (var routeTarget in targets)
        {
            var target = new DrainingTargetExecutor();
            var dispatcher = CreateDispatcher(
                CreateSettings(maximumBodyBytes: 3),
                target,
                routeTarget);
            await using var host = await LoopbackHost.StartAsync(
                dispatcher,
                maximumBodyBytes: 64,
                maximumHeaderBytes: 1024,
                webSockets: false,
                TestContext.Current.CancellationToken);
            using var content = new StreamContent(new NonSeekableReadStream([1, 2, 3, 4]));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/limited")
            {
                Content = content
            };
            request.Headers.TransferEncodingChunked = true;
            using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal(1, target.CallCount);
        }
    }

    [Fact]
    public async Task WebSocketFramesDoNotConsumeAdditionalHandshakeTokens()
    {
        var policy = new ClientIpRatePolicyConfiguration(
            tokenLimit: 1,
            tokensPerPeriod: 1,
            replenishmentPeriod: TimeSpan.FromHours(1),
            queueLimit: 0,
            rejectionBehavior: RateLimitRejectionBehavior.Reject,
            retryAfterBehavior: RateLimitRetryAfterBehavior.None);
        var target = new WebSocketTrackingTargetExecutor();
        var dispatcher = CreateDispatcher(CreateSettings(clientIpRatePolicy: policy), target);
        await using var host = await LoopbackHost.StartAsync(
            dispatcher,
            maximumBodyBytes: 64,
            maximumHeaderBytes: 1024,
            webSockets: true,
            TestContext.Current.CancellationToken);
        using var socket = new ClientWebSocket();
        var webSocketUri = new UriBuilder(Uri.UriSchemeWs, "127.0.0.1", host.Port, "/limited").Uri;

        await socket.ConnectAsync(webSocketUri, TestContext.Current.CancellationToken);
        await socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes("one")),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
        await target.FirstFrame.WaitAsync(TestContext.Current.CancellationToken);
        await socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes("two")),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
        await target.SecondFrame.WaitAsync(TestContext.Current.CancellationToken);

        var rejectedHandshake = await SendRawAsync(
            host.Port,
            "GET /limited HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, target.CallCount);
        Assert.StartsWith("HTTP/1.1 429", rejectedHandshake, StringComparison.Ordinal);
        await socket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "complete",
            TestContext.Current.CancellationToken);
    }
    private static HostRouteDispatcher CreateDispatcher(
        GlobalSettingsConfiguration settings,
        IRouteTargetExecutor target,
        RouteTargetConfiguration? routeTarget = null)
    {
        var route = new RouteConfiguration(
            id: Guid.CreateVersion7(),
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/limited",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: routeTarget ?? new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            priority: 0,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1);
        var configuration = new HostConfigurationSnapshot(
            version: 1,
            globalSettings: settings,
            routes: ImmutableArray.Create(route),
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var build = RouteMatchSnapshotBuilder.Build(configuration.Routes);
        var snapshot = new HostRoutingSnapshot(
            configuration,
            build.Snapshot ?? throw new InvalidOperationException("The route must compile."));
        return new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static GlobalSettingsConfiguration CreateSettings(
        long maximumBodyBytes = GlobalSettingsConfiguration.HardMaximumRequestBodyBytes,
        ClientIpRatePolicyConfiguration? clientIpRatePolicy = null) =>
        new(
            version: 1,
            maxRequestBodyBytes: maximumBodyBytes,
            maxConcurrentRequests: 8,
            configurationPollInterval: TimeSpan.FromSeconds(1),
            clientIpRatePolicy: clientIpRatePolicy);

    private static async Task<string> SendRawAsync(
        int port,
        string request,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = client.GetStream();
        var payload = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var response = new StringBuilder();
        var buffer = new byte[1024];
        while (response.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            response.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        return response.ToString();
    }

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot snapshot) => Current = snapshot;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream _inner;

        internal NonSeekableReadStream(byte[] content) => _inner = new MemoryStream(content);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DrainingTargetExecutor : IRouteTargetExecutor
    {
        internal int CallCount { get; private set; }

        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await context.Request.Body.CopyToAsync(Stream.Null, cancellationToken);
            return RouteTargetExecutionResult.Handled;
        }
    }

    private sealed class WebSocketTrackingTargetExecutor : IRouteTargetExecutor
    {
        private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);
        internal Task FirstFrame => _firstFrame.Task;
        internal Task SecondFrame => _secondFrame.Task;

        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return RouteTargetExecutionResult.BadRequest;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            var frames = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "complete",
                        cancellationToken);
                    return RouteTargetExecutionResult.Handled;
                }

                frames++;
                if (frames == 1)
                {
                    _firstFrame.TrySetResult();
                }
                else if (frames == 2)
                {
                    _secondFrame.TrySetResult();
                }
            }
        }
    }

    private sealed class LoopbackHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private LoopbackHost(WebApplication application, HttpClient client, int port)
        {
            _application = application;
            Client = client;
            Port = port;
        }

        internal HttpClient Client { get; }
        internal int Port { get; }

        internal static async Task<LoopbackHost> StartAsync(
            HostRouteDispatcher dispatcher,
            long maximumBodyBytes,
            int maximumHeaderBytes,
            bool webSockets,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = maximumBodyBytes;
                options.Limits.MaxRequestHeadersTotalSize = maximumHeaderBytes;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    listenOptions.Protocols = HttpProtocols.Http1);
            });
            var application = builder.Build();
            if (webSockets)
            {
                application.UseWebSockets();
            }

            application.Run(dispatcher.DispatchAsync);
            try
            {
                await application.StartAsync(cancellationToken);
                var address = new Uri(application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single());
                return new LoopbackHost(
                    application,
                    new HttpClient { BaseAddress = new UriBuilder(Uri.UriSchemeHttp, address.Host, address.Port).Uri },
                    address.Port);
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _application.StopAsync(CancellationToken.None);
            }
            finally
            {
                Client.Dispose();
                await _application.DisposeAsync();
            }
        }
    }
}
