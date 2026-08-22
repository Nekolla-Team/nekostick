using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteEventsPipelineTests
{
    private static readonly int[] ExpectedRequestHookOrder = [1, 2];
    private static readonly int[] ExpectedReturnHookStatuses = [200, 201];

    [Fact]
    public async Task PublishRouteEventDeliversEveryRouteAndDoesNotWaitForSubscriber()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(900);
        var otherRouteId = RoutingTestData.Id(901);
        var entered = NewSignal();
        var delivered = NewSignal();
        var release = NewSignal();
        var deliveries = 0;
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.True(events.TrySubscribe(async (@event, _) =>
                {
                    if (@event.Type is not (ExtensionRouteEventTypes.Trigger or ExtensionRouteEventTypes.Return))
                    {
                        return;
                    }

                    var count = Interlocked.Increment(ref deliveries);
                    if (count == 1)
                    {
                        entered.SetResult();
                    }

                    if (count == 2)
                    {
                        delivered.SetResult();
                    }

                    await release.Task.ConfigureAwait(false);
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        Assert.Equal(1, generation.PublishRouteEvent(new ExtensionRouteEvent(
            otherRouteId,
            RoutingTestData.Id(902),
            ExtensionRouteEventStage.Trigger,
            Request("/other"))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, generation.PublishRouteEvent(new ExtensionRouteEvent(
            routeId,
            RoutingTestData.Id(903),
            ExtensionRouteEventStage.Trigger,
            Request("/matched"))));

        release.SetResult();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref deliveries));
    }

    [Fact]
    public async Task HooksRunInRegistrationOrderAndSeePriorRequestMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(904);
        var order = new List<int>();
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, _) =>
                {
                    order.Add(1);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/first")));
                }));
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, _) =>
                {
                    order.Add(context.Request.Path == "/first" ? 2 : -2);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/second")));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(905),
            ExtensionRouteEventStage.Trigger,
            Request("/initial"),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(ExpectedRequestHookOrder, order);
        Assert.Equal("/second", result.Request.Path);
    }

    [Fact]
    public async Task ReturnHooksRunSeriallyAndSeePriorResponseMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(915);
        var statuses = new List<int>();
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Return, (context, _) =>
                {
                    statuses.Add(context.Response!.StatusCode);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(201)));
                }));
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Return, (context, _) =>
                {
                    statuses.Add(context.Response!.StatusCode);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(202)));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(916),
            ExtensionRouteEventStage.Return,
            Request("/return"),
            new ExtensionRouteResponseSnapshot(200),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ExpectedReturnHookStatuses, statuses);
        Assert.Equal(202, result.Response!.StatusCode);
    }

    [Fact]
    public async Task InvalidLaterActionDoesNotExposeEarlierRequestMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(906);
        var original = Request("/original");
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (_, _) =>
                    ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/partial")))));
                Assert.True(events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (_, _) =>
                    ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(200)))));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(907),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task GlobalHookExecutesForUnownedRouteAndReceivesRouteIdentity()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var ownedRouteId = RoutingTestData.Id(906);
        var otherRouteId = RoutingTestData.Id(917);
        var executions = 0;
        var seenRouteId = Guid.Empty;
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (context, _) =>
                {
                    executions++;
                    seenRouteId = context.RouteId;
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, ownedRouteId);
        var result = await generation.DispatchRouteHooksAsync(
            otherRouteId,
            RoutingTestData.Id(918),
            ExtensionRouteEventStage.Trigger,
            Request("/other"),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, executions);
        Assert.Equal(otherRouteId, seenRouteId);
    }

    [Fact]
    public async Task InvalidStageFailsClosed()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(921);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(922),
            (ExtensionRouteEventStage)99,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task ExceptionAndCancellationFailClosedWithoutMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(908);
        var original = Request("/original");
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    throw new InvalidOperationException("unreachable");
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(909),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            cancellation.Token);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task CallbackTimeoutFailsClosedAndSuppressesLateResult()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(910);
        var lateResult = NewSignal();
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, token) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
                    lateResult.SetResult();
                    return new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/late"));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(911),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
        Assert.False(lateResult.Task.IsCompleted);
    }

    [Fact]
    public async Task RetirementClearsRegistrationsAndPreventsLateCallbackInfluence()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(912);
        var entered = NewSignal();
        var release = NewSignal();
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, _) =>
                {
                    entered.SetResult();
                    await release.Task.ConfigureAwait(false);
                    return new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/late"));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var dispatch = generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(913),
            ExtensionRouteEventStage.Trigger,
            Request("/original"),
            null,
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var retirement = generation.RetireAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.True(generation.IsRetiring);
        release.SetResult();

        var result = await dispatch;
        AssertFailClosed(result, Request("/original"));
        Assert.True(await retirement);
        Assert.False(factory.RouteEvents!.TryRegisterHook(
            ExtensionRouteEventStage.Trigger,
            (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))));
    }

    [Fact]
    public async Task RegistrationCapsRejectRegistrationsBeyondGenerationBounds()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(914);
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                for (var index = 0; index < ExtensionRouteHookLimits.MaximumHookRegistrations + 1; index++)
                {
                    Assert.Equal(
                        index < ExtensionRouteHookLimits.MaximumHookRegistrations,
                        events.TryRegisterHook(
                            ExtensionRouteEventStage.Trigger,
                            (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))));
                }

                for (var index = 0; index < ExtensionRouteHookLimits.MaximumSubscriptionRegistrations + 1; index++)
                {
                    Assert.Equal(
                        index < ExtensionRouteHookLimits.MaximumSubscriptionRegistrations,
                        events.TrySubscribe((_, _) => ValueTask.CompletedTask));
                }
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        _ = await PrepareAsync(manager, manifest, routeId);
    }

    private static async Task<ExtensionDispatchGeneration> PrepareAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest manifest,
        Guid routeId)
    {
        var settings = new ExtensionSettingsConfiguration(manifest.Id, 1, "{}", 0);
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                settings,
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

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return Assert.IsType<ExtensionManifest>(result.Manifest);
    }

    private static ExtensionRouteRequestSnapshot Request(string path) =>
        new("GET", path, host: "example.test");

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFailClosed(
        ExtensionRouteHookDispatchResult result,
        ExtensionRouteRequestSnapshot original)
    {
        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Equal(original.Path, result.Request.Path);
        Assert.Null(result.Response);
    }

    private sealed class RouteTestFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        internal Action<IExtensionRouteEvents>? Configure { get; init; }
        internal IExtensionRouteEvents? RouteEvents { get; private set; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            throw new NotSupportedException();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            RouteEvents = routeEvents;
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
