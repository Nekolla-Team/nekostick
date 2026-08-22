using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteHeaderSafetyTests
{
    [Fact]
    public async Task ProtectedRequestHeaderReplacementLeavesRequestUntouched()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var routeId = RoutingTestData.Id(940);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                    ExtensionRouteHookAction.ReplaceRequest,
                    new ExtensionRouteRequestSnapshot(
                        "POST",
                        "/replacement",
                        host: "example.test",
                        headers: [new("Connection", ["close"])],
                        body: Encoding.UTF8.GetBytes("new"))))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, fixture.RootPath, routeId);
        var context = CreateContext("/original", "example.test", Encoding.UTF8.GetBytes("old"));
        context.Request.Headers["X-Original"] = "present";
        var originalBody = context.Request.Body;

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.True(session!.Cancelled);
        Assert.Equal("/original", context.Request.Path.Value);
        Assert.Equal("present", context.Request.Headers["X-Original"].ToString());
        Assert.False(context.Request.Headers.ContainsKey("Connection"));
        Assert.Same(originalBody, context.Request.Body);
        Assert.Equal("old", await ReadBodyAsync(context.Request.Body));
    }

    [Fact]
    public async Task ValidRequestReplacementAppliesPathHeadersAndBody()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var routeId = RoutingTestData.Id(944);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                    ExtensionRouteHookAction.ReplaceRequest,
                    new ExtensionRouteRequestSnapshot(
                        "POST",
                        "/replacement",
                        queryString: "changed=1",
                        host: "example.test",
                        headers: [new("X-Replaced", ["yes"])],
                        body: Encoding.UTF8.GetBytes("new"))))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, fixture.RootPath, routeId);
        var context = CreateContext("/original", "example.test", Encoding.UTF8.GetBytes("old"));

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.Equal("POST", context.Request.Method);
        Assert.Equal("/replacement", context.Request.Path.Value);
        Assert.Equal("?changed=1", context.Request.QueryString.Value);
        Assert.Equal("yes", context.Request.Headers["X-Replaced"].ToString());
        Assert.Equal(3, context.Request.ContentLength);
        Assert.Equal("new", await ReadBodyAsync(context.Request.Body));
        await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Deferred,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Content-Length", "1")]
    [InlineData("Connection", "close")]
    public async Task ConflictingResponseReplacementFailsClosedWithoutPartialMutation(
        string headerName,
        string headerValue)
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var routeId = RoutingTestData.Id(headerName == "Connection" ? 941 : 942);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                    ExtensionRouteHookAction.ReplaceResponse,
                    response: new ExtensionRouteResponseSnapshot(
                        StatusCodes.Status202Accepted,
                        [new(headerName, [headerValue])],
                        Encoding.UTF8.GetBytes("body"))))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, fixture.RootPath, routeId);
        var context = CreateContext("/original", "example.test", []);
        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session!.Cancelled);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-Original"] = "yes";
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes("body"),
            TestContext.Current.CancellationToken);
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Cancelled, result);
        Assert.Equal(499, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers);
        Assert.Equal("", await ReadBodyAsync(context.Response.Body));
    }

    [Fact]
    public async Task ValidResponseReplacementAppliesAtomically()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var routeId = RoutingTestData.Id(943);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                    ExtensionRouteHookAction.ReplaceResponse,
                    response: new ExtensionRouteResponseSnapshot(
                        StatusCodes.Status202Accepted,
                        [
                            new("Content-Length", ["2"]),
                            new("X-Replaced", ["yes"])
                        ],
                        Encoding.UTF8.GetBytes("ok"))))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, fixture.RootPath, routeId);
        var context = CreateContext("/original", "example.test", []);
        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session!.Cancelled);

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes("old"),
            TestContext.Current.CancellationToken);
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal("2", context.Response.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("yes", context.Response.Headers["X-Replaced"].ToString());
        Assert.Equal("ok", await ReadBodyAsync(context.Response.Body));
    }

    private static async Task<ExtensionDispatchGeneration> PrepareAsync(
        ExtensionRuntimeManager manager,
        string rootPath,
        Guid routeId)
    {
        var manifestResult = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
        var manifest = Assert.IsType<ExtensionManifest>(manifestResult.Manifest);
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                new ExtensionSettingsConfiguration(manifest.Id, 1, "{}", 0),
                ["fixture.handler"],
                routeIds: [routeId])),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        var preparation = Assert.IsType<ExtensionGenerationPreparation>(prepared.Preparation);
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());
        return Assert.IsType<ExtensionDispatchGeneration>(ready.Generation);
    }

    private static HostRoutingSnapshot CreateRoutingSnapshot(
        Guid routeId,
        ExtensionDispatchGeneration generation)
    {
        var route = RoutingTestData.CreateRoute(routeId, RouteMatcherType.Exact, "/original");
        var configuration = RoutingTestData.CreateSnapshot(1, ImmutableArray.Create(route));
        return new HostRoutingSnapshot(
            configuration,
            RoutingTestData.Build(route),
            ImmutableDictionary<Guid, ExecutableRoute>.Empty,
            generation,
            ImmutableDictionary<Guid, string?>.Empty);
    }

    private static RouteMatch CreateMatch(Guid routeId) =>
        RoutingTestData.Build(RoutingTestData.CreateRoute(routeId, RouteMatcherType.Exact, "/original"))
            .Match(new RouteMatchInput("/original", "example.test", "GET"))
            .Match!;

    private static DefaultHttpContext CreateContext(
        string path,
        string host,
        byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(Stream body)
    {
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RouteTestFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        internal Action<IExtensionRouteEvents>? Configure { get; init; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            throw new NotSupportedException();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            Configure?.Invoke(routeEvents);
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
}
