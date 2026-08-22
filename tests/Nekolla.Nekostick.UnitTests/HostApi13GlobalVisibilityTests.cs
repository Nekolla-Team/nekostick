using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostApi13GlobalVisibilityTests
{
    [Fact]
    public async Task SupervisorReadsHostWideSnapshotsAndLooksUpAnyServiceId()
    {
        var firstId = RoutingTestData.Id(980);
        var secondId = RoutingTestData.Id(981);
        var accessor = new SnapshotAccessor(
            [CreateSnapshot(firstId, 4101), CreateSnapshot(secondId, 4102)]);
        var forwarding = new MicroserviceForwardingTelemetry();
        using var active = forwarding.Begin(secondId);
        var supervisor = new ExtensionSupervisorFacade(accessor, forwarding);

        var all = await supervisor.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(all.IsSuccess);
        Assert.Equal([firstId, secondId], all.Value!.Select(snapshot => snapshot.ServiceId));

        var selected = await supervisor.GetAsync(secondId, TestContext.Current.CancellationToken);
        Assert.True(selected.IsSuccess);
        Assert.NotNull(selected.Value);
        Assert.Equal(secondId, selected.Value!.ServiceId);
        Assert.Equal(1, selected.Value.ForwardedRequestCount);
        Assert.Equal(1, selected.Value.ActiveForwardedRequestCount);

        var absent = await supervisor.GetAsync(
            RoutingTestData.Id(982),
            TestContext.Current.CancellationToken);
        Assert.True(absent.IsSuccess);
        Assert.Null(absent.Value);
    }

    private static HostServiceRuntimeSnapshot CreateSnapshot(Guid serviceId, int processId)
    {
        var now = DateTimeOffset.UtcNow;
        return new HostServiceRuntimeSnapshot(
            serviceId: serviceId,
            configurationVersion: 1,
            processId: processId,
            processInstanceId: null,
            startedAt: now,
            lastUpdatedAt: now,
            lastHealthAt: now,
            lifecycleState: ExtensionServiceLifecycleState.Running,
            healthState: ExtensionServiceHealthState.Healthy);
    }

    private sealed class SnapshotAccessor : IHostServiceRuntimeSnapshotAccessor
    {
        private readonly ImmutableArray<HostServiceRuntimeSnapshot> _snapshots;

        internal SnapshotAccessor(ImmutableArray<HostServiceRuntimeSnapshot> snapshots) =>
            _snapshots = snapshots;

        public ImmutableArray<HostServiceRuntimeSnapshot> ReadCurrent() => _snapshots;

        public bool TryGet(Guid serviceId, out HostServiceRuntimeSnapshot snapshot)
        {
            snapshot = _snapshots.FirstOrDefault(value => value.ServiceId == serviceId)!;
            return snapshot is not null;
        }
    }
}

public sealed class ExtensionApi13CrossExtensionVisibilityTests
{
    [Fact]
    public async Task GlobalRouteRegistrationsObserveRoutesPreparedForAnotherExtension()
    {
        const string firstId = "global.first.extension";
        const string secondId = "global.second.extension";
        using var firstFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(firstId));
        using var secondFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(secondId));
        var firstManifest = Discover(firstFixture.RootPath);
        var secondManifest = Discover(secondFixture.RootPath);
        var firstRouteId = RoutingTestData.Id(983);
        var secondRouteId = RoutingTestData.Id(984);
        var hookRoute = NewSignal<Guid>();
        var eventRoute = NewSignal<Guid>();
        var factory = new GlobalRouteFactory(firstId, hookRoute, eventRoute);

        await using var manager = new ExtensionRuntimeManager(
            HostApiVersion.Current,
            capabilityFactory: factory);
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    firstManifest,
                    Settings(firstId, "first.handler"),
                    ["first.handler"],
                    routeIds: [firstRouteId]),
                new ExtensionRuntimeDescriptor(
                    secondManifest,
                    Settings(secondId, "second.handler"),
                    ["second.handler"],
                    routeIds: [secondRouteId])),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        var preparation = Assert.IsType<ExtensionGenerationPreparation>(prepared.Preparation);
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());
        var generation = Assert.IsType<ExtensionDispatchGeneration>(ready.Generation);

        var hookResult = await generation.DispatchRouteHooksAsync(
            secondRouteId,
            RoutingTestData.Id(985),
            ExtensionRouteEventStage.Trigger,
            new ExtensionRouteRequestSnapshot("GET", "/cross-extension", host: "example.test"),
            response: null,
            TestContext.Current.CancellationToken);
        Assert.True(hookResult.Succeeded);
        Assert.Equal(
            secondRouteId,
            await hookRoute.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Equal(1, generation.PublishRouteEvent(new ExtensionRouteEvent(
            secondRouteId,
            RoutingTestData.Id(986),
            ExtensionRouteEventStage.Trigger,
            new ExtensionRouteRequestSnapshot("GET", "/cross-extension", host: "example.test"))));
        Assert.Equal(
            secondRouteId,
            await eventRoute.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    private static string RuntimeManifestJson(string id) =>
        "{\n" +
        "  \"schemaVersion\": 1,\n" +
        "  \"id\": " + JsonSerializer.Serialize(id) + ",\n" +
        "  \"version\": \"1.0.0\",\n" +
        "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
        "  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n" +
        "  \"dependencies\": [],\n" +
        "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
        "}";

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return Assert.IsType<ExtensionManifest>(result.Manifest);
    }

    private static ExtensionSettingsConfiguration Settings(string id, string handlerId) =>
        new(id, 1, JsonSerializer.Serialize(new { handlerId }), 0);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class GlobalRouteFactory(
        string observingExtensionId,
        TaskCompletionSource<Guid> hookRoute,
        TaskCompletionSource<Guid> eventRoute) :
        IExtensionCapabilityFactory,
        IExtensionCapabilityFactoryRouteEvents
    {
        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            UnsupportedExtensionCapabilities.Create();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            if (string.Equals(extensionId, observingExtensionId, StringComparison.Ordinal))
            {
                Assert.True(routeEvents.TryRegisterHook(
                    ExtensionRouteEventStage.Trigger,
                    (context, _) =>
                    {
                        hookRoute.TrySetResult(context.RouteId);
                        return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                    }));
                Assert.True(routeEvents.TrySubscribe((@event, _) =>
                {
                    if (@event.Type == ExtensionRouteEventTypes.Trigger)
                    {
                        using var payload = JsonDocument.Parse(@event.PayloadJson);
                        eventRoute.TrySetResult(payload.RootElement.GetProperty("RouteId").GetGuid());
                    }

                    return ValueTask.CompletedTask;
                }));
            }

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
