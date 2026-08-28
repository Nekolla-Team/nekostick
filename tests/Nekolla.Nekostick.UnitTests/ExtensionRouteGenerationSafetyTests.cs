using System.Collections.Immutable;
using System.Diagnostics;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionRouteGenerationSafetyTests
{
    [Fact]
    public async Task SynchronouslyBlockingHookReturnsByTheHardDeadlineAndFailsClosed()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(940);
        var dispatchedRouteId = RoutingTestData.Id(941);
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        var factory = new RouteFactory
        {
            Configure = events => Assert.True(events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (context, _) =>
                {
                    entered.TrySetResult(context.RouteId);
                    release.Task.GetAwaiter().GetResult();
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/late")));
                }))
        };

        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var started = Stopwatch.GetTimestamp();
        var dispatch = generation.DispatchRouteHooksAsync(
            dispatchedRouteId,
            RoutingTestData.Id(942),
            ExtensionRouteEventStage.Trigger,
            Request("/original"),
            null,
            TestContext.Current.CancellationToken).AsTask();
        var observedRouteId = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var result = await dispatch;
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Equal("/original", result.Request.Path);
        Assert.Equal(dispatchedRouteId, observedRouteId);
        Assert.True(elapsed <= TimeSpan.FromMilliseconds(500), $"Hook exceeded deadline: {elapsed}.");

        release.SetResult();
    }


    [Fact]
    public async Task RetiringOldReusedGenerationDoesNotClearNewGenerationRouteDelivery()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(942);
        var hookCalls = 0;
        var eventCalls = 0;
        var factory = new RouteFactory
        {
            Configure = events =>
            {
                Assert.True(events.TryRegisterHook(
                    ExtensionRouteEventStage.Trigger,
                    (_, _) =>
                    {
                        Interlocked.Increment(ref hookCalls);
                        return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                    }));
                Assert.True(events.TrySubscribe((@event, _) =>
                {
                    if (@event.Type is ExtensionRouteEventTypes.Trigger or ExtensionRouteEventTypes.Return)
                    {
                        Interlocked.Increment(ref eventCalls);
                    }

                    return ValueTask.CompletedTask;
                }));
            }
        };

        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var first = await PrepareAsync(manager, manifest, routeId);
        var second = await PrepareAsync(manager, manifest, routeId, first);
        Assert.Single(second.Contexts);
        Assert.True(ReferenceEquals(first.Contexts.Single(), second.Contexts.Single()), "context was replaced");

        Assert.True(await first.RetireAsync(TestContext.Current.CancellationToken));

        var dispatchedRouteId = RoutingTestData.Id(943);
        var hookResult = await second.DispatchRouteHooksAsync(
            dispatchedRouteId,
            RoutingTestData.Id(944),
            ExtensionRouteEventStage.Trigger,
            Request("/reused"),
            null,
            TestContext.Current.CancellationToken);
        Assert.True(hookResult.Succeeded);
        Assert.Equal(1, Volatile.Read(ref hookCalls));
        Assert.True(second.PublishRouteEvent(new ExtensionRouteEvent(
            dispatchedRouteId,
            RoutingTestData.Id(945),
            ExtensionRouteEventStage.Trigger,
            Request("/reused"))) == 1, "published recipients");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref eventCalls));
    }

    [Fact]
    public async Task PublicationAfterRetirementAdmissionCannotEnqueueAnOrdinaryEvent()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(945);
        var dispatchedRouteId = RoutingTestData.Id(946);
        var factory = new RouteFactory
        {
            Configure = events => Assert.True(events.TrySubscribe((_, _) => ValueTask.CompletedTask))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var retirement = generation.RetireAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.True(generation.IsRetiring);
        var published = generation.PublishRouteEvent(new ExtensionRouteEvent(
            dispatchedRouteId,
            RoutingTestData.Id(947),
            ExtensionRouteEventStage.Trigger,
            Request("/retiring")));


        Assert.Equal(0, published);
        Assert.True(await retirement);
    }

    [Fact]
    public async Task Api12DoesNotGiveRouteAwareFactoryARegistrationSurface()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(947);
        var factory = new RouteFactory();
        await using var manager = new ExtensionRuntimeManager(
            new HostApiVersion(1, 2, 0),
            capabilityFactory: factory);

        var generation = await PrepareAsync(manager, manifest, routeId);

        Assert.False(factory.RouteAwareFactoryCalled);
        Assert.True(factory.LegacyFactoryCalled);
        Assert.False(generation.HasRouteObservers(routeId));
        Assert.Equal(0, generation.PublishRouteEvent(new ExtensionRouteEvent(
            routeId,
            RoutingTestData.Id(948),
            ExtensionRouteEventStage.Trigger,
            Request("/legacy"))));
    }

    private static async Task<ExtensionDispatchGeneration> PrepareAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest manifest,
        Guid routeId,
        ExtensionDispatchGeneration? previous = null)
    {
        var prepared = await manager.PrepareGenerationAsync(ImmutableArray.Create(new ExtensionRuntimeDescriptor(
            manifest,
            new ExtensionSettingsConfiguration(manifest.Id, 1, "{}", 0),
            ["fixture.handler"],
            routeIds: [routeId])), previous, cancellationToken: TestContext.Current.CancellationToken);
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

    private sealed class RouteFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        internal Action<IExtensionRouteEvents>? Configure { get; init; }
        internal bool RouteAwareFactoryCalled { get; private set; }
        internal bool LegacyFactoryCalled { get; private set; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned)
        {
            LegacyFactoryCalled = true;
            return UnsupportedExtensionCapabilities.Create();
        }

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            RouteAwareFactoryCalled = true;
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
