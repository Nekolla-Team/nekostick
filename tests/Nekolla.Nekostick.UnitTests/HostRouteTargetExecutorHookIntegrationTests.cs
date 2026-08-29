using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteTargetExecutorHookIntegrationTests
{
    private const string ExtensionId = "route.hook.integration.extension";
    private const string HandlerId = "route.echo";

    [Fact]
    public async Task ExtensionHandlerTargetReceivesTriggerMutationAndReturnsHookResponse()
    {
        var routeId = RoutingTestData.Id(980);
        var hooks = new HookFactory("/extension-hooked", 201, "extension-return");
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: hooks);
        using var extension = StagedExtension.Create();
        var generation = await PrepareGenerationAsync(manager, extension.Manifest, routeId);
        await using var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateSnapshot(
                CreateRoute(routeId, "/extension", new ExtensionHandlerRouteTargetConfiguration(HandlerId))),
            generation));

        using var services = CreateProxyServices(new RecordingForwarder());
        var target = new HostRouteTargetExecutor(services.GetRequiredService<MicroserviceHttpExecutor>());
        var result = await DispatchAsync(holder, target, "/extension");

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        Assert.Equal("extension-return", result.Body);
        Assert.Equal("/extension-hooked|trigger", hooks.ReturnInputBody);
        Assert.Equal(StatusCodes.Status200OK, hooks.ReturnInputStatus);
        Assert.Equal(1, hooks.TriggerCalls);
        Assert.Equal(1, hooks.ReturnCalls);
    }

    [Fact]
    public async Task MicroserviceTargetReceivesTriggerMutationAndReturnsHookResponse()
    {
        var routeId = RoutingTestData.Id(981);
        var serviceId = RoutingTestData.Id(982);
        var hooks = new HookFactory("/microservice-hooked", 202, "microservice-return");
        var forwarder = new RecordingForwarder();
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: hooks);
        using var extension = StagedExtension.Create();
        var generation = await PrepareGenerationAsync(manager, extension.Manifest, routeId);
        await using var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateSnapshot(CreateRoute(routeId, "/microservice", new MicroserviceRouteTargetConfiguration(serviceId)), serviceId),
            generation));

        using var services = CreateProxyServices(forwarder, serviceId);
        var target = new HostRouteTargetExecutor(services.GetRequiredService<MicroserviceHttpExecutor>());
        var result = await DispatchAsync(holder, target, "/microservice");

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal("microservice-return", result.Body);
        Assert.Equal("/microservice-hooked", forwarder.Path);
        Assert.Equal("trigger", forwarder.HookHeader);
        Assert.Equal("proxy:/microservice-hooked|trigger", hooks.ReturnInputBody);
        Assert.Equal(1, hooks.TriggerCalls);
        Assert.Equal(1, hooks.ReturnCalls);
    }
    [Fact]
    public void RouteSessionRestorationReattachesOriginalBodyAndDisposesBuffer()
    {
        using var original = new MemoryStream();
        using var buffer = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = buffer;
        var session = new HostRouteEventSession(
            generation: null!,
            routeId: RoutingTestData.Id(987),
            correlationId: RoutingTestData.Id(988),
            request: new ExtensionRouteRequestSnapshot("GET", "/restore", host: "integration.test"),
            hasHooks: true)
        {
            OriginalResponseBody = original,
            ResponseBuffer = buffer
        };

        HostRouteEvents.RestoreResponseBody(context, session);

        Assert.Same(original, context.Response.Body);
        Assert.Null(session.ResponseBuffer);
        Assert.Throws<ObjectDisposedException>(() => buffer.WriteByte(1));
    }

    private static async Task<ExtensionDispatchGeneration> PrepareGenerationAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest manifest,
        Guid routeId)
    {
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                new ExtensionSettingsConfiguration(ExtensionId, 1, "{}", 1),
                [HandlerId],
                routeIds: [routeId])),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        var preparation = Assert.IsType<ExtensionGenerationPreparation>(prepared.Preparation);
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());
        return Assert.IsType<ExtensionDispatchGeneration>(ready.Generation);
    }

    private static async Task<DispatchResult> DispatchAsync(
        HostConfigurationSnapshotHolder holder,
        HostRouteTargetExecutor target,
        string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("integration.test");
        context.Request.Path = path;
        context.Request.ContentLength = 0;
        context.Response.Body = new MemoryStream();
        var dispatcher = new HostRouteDispatcher(
            new HostRoutingSnapshotAccessor(holder),
            NoOpRouteFallbackDispatcher.Instance,
            target);
        await dispatcher.DispatchAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return new DispatchResult(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static HostConfigurationSnapshot CreateSnapshot(RouteConfiguration route, Guid? serviceId = null) =>
        new(
            1,
            new GlobalSettingsConfiguration(version: 1),
            [route],
            serviceId is null ? ImmutableArray<ServiceConfiguration>.Empty : ImmutableArray.Create(CreateService(serviceId.Value)),
            ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    ExtensionId, "1.0.0", ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
                new ExtensionRecordConfiguration(
                    HandlerId, "1.0.0", ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1)),
            ImmutableArray.Create(new ExtensionSettingsConfiguration(ExtensionId, 1, "{}", 1)));

    private static ServiceConfiguration CreateService(Guid serviceId) =>
        new(
            serviceId,
            true,
            "/bin/true",
            ImmutableArray<string>.Empty,
            "/tmp",
            ImmutableDictionary<string, string>.Empty,
            ServiceStartMode.Lazy,
            ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(ServiceHealthCheckType.Process, null, TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    private static RouteConfiguration CreateRoute(Guid routeId, string path, RouteTargetConfiguration target) =>
        new(
            routeId,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, path, [], []),
            target,
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    private static ServiceProvider CreateProxyServices(RecordingForwarder forwarder, Guid? serviceId = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpForwarder>(forwarder);
        services.AddSingleton<IMicroserviceEndpointResolver>(
            serviceId is null ? new FixedEndpointResolver() : new FixedEndpointResolver(serviceId.Value));
        services.AddSingleton<MicroserviceHttpInvokerPool>();
        services.AddSingleton<IMicroserviceDrainTracker, MicroserviceDrainTracker>();
        services.AddSingleton<MicroserviceHttpExecutor>();
        return services.BuildServiceProvider();
    }

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return Assert.IsType<ExtensionManifest>(result.Manifest);
    }

    private readonly record struct DispatchResult(int StatusCode, string Body);

    private sealed class HookFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        private readonly string _triggerPath;
        private readonly int _returnStatus;
        private readonly string _returnBody;

        internal HookFactory(string triggerPath, int returnStatus, string returnBody)
        {
            _triggerPath = triggerPath;
            _returnStatus = returnStatus;
            _returnBody = returnBody;
        }

        internal int TriggerCalls { get; private set; }
        internal int ReturnCalls { get; private set; }
        internal int ReturnInputStatus { get; private set; }
        internal string? ReturnInputBody { get; private set; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            throw new NotSupportedException();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            Assert.True(routeEvents.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (context, _) =>
                {
                    TriggerCalls++;
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        new ExtensionRouteRequestSnapshot(
                            context.Request.Method,
                            _triggerPath,
                            context.Request.QueryString,
                            context.Request.Host,
                            [new KeyValuePair<string, IEnumerable<string>>("X-Hook", ["trigger"])],
                            context.Request.Body.ToArray(),
                            context.Request.IsHttps)));
                }));
            Assert.True(routeEvents.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (context, _) =>
                {
                    ReturnCalls++;
                    ReturnInputStatus = context.Response!.StatusCode;
                    ReturnInputBody = Encoding.UTF8.GetString(context.Response.Body.AsSpan());
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(
                            _returnStatus,
                            [new KeyValuePair<string, IEnumerable<string>>("X-Hook-Return", ["return"])],
                            Encoding.UTF8.GetBytes(_returnBody))));
                }));
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                unsupported.Supervisor,
                routeEvents,
                unsupported.LogWriter);
        }
    }

    public sealed class RouteHookIntegrationEntrypoint : IExtensionEntry
    {
        public RouteHookIntegrationEntrypoint(IExtensionHostBridge host)
        {
        }

        public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
        {
            if (!context.Registration.TryRegisterHandler(new EchoHandler()))
            {
                throw new InvalidOperationException("The route hook integration handler could not be registered.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class EchoHandler : IExtensionHandler
    {
        public string HandlerId => HandlerIdValue;
        private const string HandlerIdValue = "route.echo";

        public ValueTask<ExtensionHandlerResponse> HandleAsync(
            ExtensionHandlerRequest request,
            CancellationToken cancellationToken)
        {
            var hook = request.Headers.TryGetValue("X-Hook", out var values)
                ? string.Join(',', values)
                : "missing";
            var body = Encoding.UTF8.GetBytes($"{request.Path}|{hook}");
            return ValueTask.FromResult(new ExtensionHandlerResponse(
                200,
                [new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["text/plain"])],
                body));
        }
    }

    private sealed class StagedExtension : IDisposable
    {
        private StagedExtension(string rootPath, ExtensionManifest manifest)
        {
            RootPath = rootPath;
            Manifest = manifest;
        }

        internal string RootPath { get; }
        internal ExtensionManifest Manifest { get; }

        internal static StagedExtension Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "nekostick-route-hooks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var assembly = typeof(RouteHookIntegrationEntrypoint).Assembly;
            var assemblyName = assembly.GetName().Name ?? throw new InvalidOperationException();
            File.Copy(assembly.Location, Path.Combine(root, assemblyName + ".dll"));
            var contractsName = typeof(IExtensionEntrypoint).Assembly.GetName().Name ?? throw new InvalidOperationException();
            File.Copy(typeof(IExtensionEntrypoint).Assembly.Location, Path.Combine(root, contractsName + ".dll"));
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                $"{{\"schemaVersion\":1,\"id\":\"{ExtensionId}\",\"version\":\"1.0.0\",\"entryAssembly\":\"{assemblyName}.dll\",\"entryType\":\"{typeof(RouteHookIntegrationEntrypoint).FullName}\",\"dependencies\":[],\"requiredHostApiVersion\":\">=1.3.0\"}}");
            return new StagedExtension(root, Discover(root));
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedEndpointResolver : IMicroserviceEndpointResolver
    {
        private readonly Guid? _serviceId;
        internal FixedEndpointResolver(Guid? serviceId = null) => _serviceId = serviceId;

        public ValueTask<MicroserviceEndpointResolution> ResolveAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_serviceId is null || _serviceId == serviceId
                ? MicroserviceEndpointResolution.Available(new MicroserviceEndpoint("http://127.0.0.1"))
                : MicroserviceEndpointResolution.Unavailable);
    }

    private sealed class RecordingForwarder : IHttpForwarder
    {
        internal string? Path { get; private set; }
        internal string? HookHeader { get; private set; }

        public async ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            Path = httpContext.Request.Path.Value;
            HookHeader = httpContext.Request.Headers["X-Hook"].ToString();
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "text/plain";
            await httpContext.Response.WriteAsync($"proxy:{Path}|{HookHeader}");
            return ForwarderError.None;
        }
    }
}
