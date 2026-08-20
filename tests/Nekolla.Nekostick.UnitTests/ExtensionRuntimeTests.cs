using System.Linq;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionRuntimeTests
{
    [Fact]
    public async Task ExplicitLoadServesHandlerAndFallbackThenUnloads()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var load = await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "loaded", registerFallback: true),
            TestContext.Current.CancellationToken);

        Assert.True(load.Succeeded);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
        var handler = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, handler.State);
        Assert.Equal(200, handler.Response!.StatusCode);
        Assert.Equal("loaded:started", Body(handler));

        var fallback = await manager.HandleFallbackAsync(
            new ExtensionHandlerRequest("GET", "/missing"),
            ExtensionFallbackReason.NoRoute,
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, fallback.State);
        Assert.Equal("loaded:NoRoute", Body(fallback));

        var unload = await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken);
        Assert.True(unload.Succeeded);
        Assert.Null(manager.GetStatus(manifest.Id));
        var unavailable = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Unavailable, unavailable.State);
        var noFallback = await manager.HandleFallbackAsync(
            new ExtensionHandlerRequest("GET", "/missing"),
            ExtensionFallbackReason.NoRoute,
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.NotHandled, noFallback.State);
    }

    [Fact]
    public async Task ReloadStartsCandidateBeforeSwitchAndInvokesPreviousStoppedHook()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "old"),
            TestContext.Current.CancellationToken)).Succeeded);

        var replacement = await manager.ReloadAsync(
            manifest,
            Settings(manifest.Id, label: "new"),
            TestContext.Current.CancellationToken);

        Assert.True(replacement.Succeeded);
        Assert.Equal("new:previous-stopped", Body(await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CandidateStartFailurePreservesThePreviousServingHandler()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "old"),
            TestContext.Current.CancellationToken)).Succeeded);

        var replacement = await manager.ReloadAsync(
            manifest,
            Settings(manifest.Id, label: "candidate", startFails: true),
            TestContext.Current.CancellationToken);

        Assert.False(replacement.Succeeded);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed, replacement.FailureCode);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
        Assert.Equal("old:started", Body(await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task OldStopFailureExplicitlyVerifiesRestoredServingState()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "old", stopFails: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var replacement = await manager.ReloadAsync(
            manifest,
            Settings(manifest.Id, label: "candidate"),
            TestContext.Current.CancellationToken);

        Assert.False(replacement.Succeeded);
        Assert.Equal(ExtensionFailureCode.StopFailed, replacement.FailureCode);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
        Assert.Equal("old:started", Body(await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task PreviousStoppedFailurePreservesThePreviousHandler()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "old"),
            TestContext.Current.CancellationToken)).Succeeded);

        var replacement = await manager.ReloadAsync(
            manifest,
            Settings(manifest.Id, label: "candidate", previousStoppedFails: true),
            TestContext.Current.CancellationToken);

        Assert.False(replacement.Succeeded);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed, replacement.FailureCode);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
        Assert.Equal("old:started", Body(await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ConflictingHandlerAndFallbackOwnersAreRejected()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson("first.extension"));
        using var replacement = TestExtensionDirectory.CreateJson(RuntimeManifestJson("second.extension"));
        var firstManifest = Discover(fixture.RootPath);
        var secondManifest = Discover(replacement.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            firstManifest,
            Settings(firstManifest.Id, label: "first", registerFallback: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var conflict = await manager.LoadAsync(
            secondManifest,
            Settings(secondManifest.Id, label: "second", handlerId: "fixture.second", registerFallback: true),
            TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(ExtensionFailureCode.FallbackConflict, conflict.FailureCode);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(firstManifest.Id)!.State);
        Assert.Null(manager.GetStatus(secondManifest.Id));
    }

    [Fact]
    public async Task ConflictingHandlerOwnersAreRejected()
    {
        using var firstDirectory = TestExtensionDirectory.CreateJson(RuntimeManifestJson("first.handler.extension"));
        using var secondDirectory = TestExtensionDirectory.CreateJson(RuntimeManifestJson("second.handler.extension"));
        var firstManifest = Discover(firstDirectory.RootPath);
        var secondManifest = Discover(secondDirectory.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            firstManifest,
            Settings(firstManifest.Id),
            TestContext.Current.CancellationToken)).Succeeded);
        var conflict = await manager.LoadAsync(
            secondManifest,
            Settings(secondManifest.Id),
            TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(ExtensionFailureCode.HandlerConflict, conflict.FailureCode);
        Assert.Null(manager.GetStatus(secondManifest.Id));
    }

    [Theory]
    [InlineData("duplicateHandler")]
    [InlineData("duplicateFallback")]
    public async Task DuplicateRegistrationsInsideOneEntrypointAreRejected(string duplicateOption)
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var result = await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                label: "duplicate",
                registerFallback: duplicateOption == "duplicateFallback",
                duplicateOption: duplicateOption),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed, result.FailureCode);
        Assert.Null(manager.GetStatus(manifest.Id));
    }

    [Fact]
    public async Task TenHandlerFailuresInTheRollingWindowAutoStopTheExtension()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, handlerFails: true),
            TestContext.Current.CancellationToken)).Succeeded);

        for (var failure = 0; failure < 10; failure++)
        {
            var result = await manager.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/failure"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionInvocationState.Failed, result.State);
        }

        Assert.Null(manager.GetStatus(manifest.Id));
        var unavailable = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/failure"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Unavailable, unavailable.State);
    }

    [Fact]
    public async Task FailureThresholdPublishesFailedStateToOtherServingExtension()
    {
        using var observerFixture = TestExtensionDirectory.CreateJson(
            RuntimeManifestJson(id: "fixture.extension.observer"));
        using var failingFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var observerManifest = Discover(observerFixture.RootPath);
        var failingManifest = Discover(failingFixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            observerManifest,
            Settings(
                observerManifest.Id,
                handlerId: "observer.handler",
                publishCoreEvents: true,
                eventCount: 3),
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.LoadAsync(
            failingManifest,
            Settings(
                failingManifest.Id,
                handlerId: "failing.handler",
                handlerFails: true),
            TestContext.Current.CancellationToken)).Succeeded);

        for (var failure = 0; failure < 10; failure++)
        {
            var result = await manager.HandleAsync(
                "failing.handler",
                new ExtensionHandlerRequest("GET", "/failure"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionInvocationState.Failed, result.State);
        }

        var observerResult = await manager.HandleAsync(
            "observer.handler",
            new ExtensionHandlerRequest("GET", "/events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, observerResult.State);
        var body = Body(observerResult);
        Assert.Contains("\"state\":\"Failed\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"extensionId\":\"{failingManifest.Id}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedTaskStopCancelsTheTrackedFixtureTask()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, startTask: true),
            TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(1, manager.GetStatus(manifest.Id)!.ActiveTasks);
        var unload = await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken);

        Assert.True(unload.Succeeded);
        Assert.Null(manager.GetStatus(manifest.Id));
    }

    [Fact]
    public async Task OrderedEventsAreDeliveredInOrder()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, publishOrderedEvents: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var result = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/events"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.Equal("event-0,event-1,event-2", Body(result));
    }

    [Fact]
    public async Task BoundedEventQueueTracksNewestDropsIncludingLifecycleEvent()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, publishBoundedEvents: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var status = manager.GetStatus(manifest.Id)!;
        Assert.Equal(2, status.DroppedEvents);
        Assert.Equal(ExtensionFailureCode.EventQueueFull, status.LastFailure);
        Assert.Equal(0, status.ActiveRequests);
    }

    [Fact]
    public async Task StartupTypedContractExchangeSucceedsAndUnloadsWithTheGeneration()
    {
        using var fixture = TestExtensionDirectory.CreateJson(TypedContractManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var load = await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, typedContractExchange: true),
            TestContext.Current.CancellationToken);

        Assert.True(load.Succeeded, load.FailureCode.ToString());
        Assert.True((await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task CoreEventsFanOutInOrderOnlyWhileTheExtensionIsServing()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                publishCoreEvents: true,
                eventCount: Enum.GetValues<ExtensionCoreEventKind>().Length + 1),
            TestContext.Current.CancellationToken)).Succeeded);

        var kinds = Enum.GetValues<ExtensionCoreEventKind>();
        foreach (var kind in kinds)
        {
            Assert.Equal(
                1,
                manager.PublishCoreEvent(new ExtensionCoreEvent(kind, 1, $"payload-{kind}")));
        }

        var result = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/core-events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var loadedPayload = JsonSerializer.Serialize(new
        {
            extensionId = manifest.Id,
            version = manifest.Version.ToString(),
            state = ExtensionLoadState.Loaded.ToString()
        });
        Assert.Equal(
            string.Join(',', new[] { loadedPayload }.Concat(kinds.Select(kind => $"payload-{kind}"))),
            Body(result));

        Assert.True((await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(
            0,
            manager.PublishCoreEvent(new ExtensionCoreEvent(
                ExtensionCoreEventKind.ExtensionStateChanged,
                1,
                "after-unload")));
    }


    [Fact]
    public async Task ReloadPublishesExtensionStateEventsThroughServingCandidateQueue()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, label: "old", publishCoreEvents: true, eventCount: 1),
            TestContext.Current.CancellationToken)).Succeeded);

        var replacement = await manager.ReloadAsync(
            manifest,
            Settings(manifest.Id, label: "replacement", publishCoreEvents: true, eventCount: 1),
            TestContext.Current.CancellationToken);
        Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());

        var result = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/reload-events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var body = Body(result);
        Assert.Contains("\"state\":\"Loaded\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Stopped\"", body, StringComparison.Ordinal);
    }
[Fact]
public async Task BridgeAddsCapabilitiesWhilePreservingLegacySettingsAndSafeUnsupportedResults()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(manifest.Id, label: "bridge", verifyBridgeCapabilities: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/bridge"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    var body = Body(result);
    Assert.Contains("api=1.2.0", body, StringComparison.Ordinal);
    Assert.Contains($"legacy={manifest.Id}:1:0", body, StringComparison.Ordinal);
    Assert.Contains("properties=True", body, StringComparison.Ordinal);
    Assert.Contains($"lifecycle={manifest.Id}:Discovered", body, StringComparison.Ordinal);
    Assert.Contains("configRead=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("configApply=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("fullRead=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("fullReplace=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("settingsRead=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("settingsWrite=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("routeRead=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("routeRemove=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("serviceRead=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("serviceRemove=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("serviceStart=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("serviceStop=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("serviceRestart=Unsupported", body, StringComparison.Ordinal);
    Assert.Contains("endpoints=0", body, StringComparison.Ordinal);
    Assert.Contains("endpointResolve=null", body, StringComparison.Ordinal);
    Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
}

[Fact]
public async Task Api10HostStillLoadsTheFixtureThroughTheLegacyBridge()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(new HostApiVersion(1, 0, 0));

    var load = await manager.LoadAsync(
        manifest,
        Settings(manifest.Id, label: "api10"),
        TestContext.Current.CancellationToken);

    Assert.True(load.Succeeded, load.FailureCode.ToString());
    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/api10"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Equal("api10:started", Body(result));
}

[Fact]
public async Task OwnedUnregisterCompletesCurrentCallbacksAndTombstonesFutureDispatch()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "unregister",
            registerFallback: true,
            unregisterHandlerOnInvocation: true,
            unregisterFallbackOnInvocation: true,
            reregisterHandlerAfterUnregister: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var activeHandler = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/first"),
        TestContext.Current.CancellationToken);
    Assert.Equal(ExtensionInvocationState.Handled, activeHandler.State);
    Assert.Contains("handler-unregister=True", Body(activeHandler), StringComparison.Ordinal);
    Assert.Contains("handler-reregister=False", Body(activeHandler), StringComparison.Ordinal);

    var futureHandler = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/second"),
        TestContext.Current.CancellationToken);
    Assert.Equal(ExtensionInvocationState.Unavailable, futureHandler.State);

    var activeFallback = await manager.HandleFallbackAsync(
        new ExtensionHandlerRequest("GET", "/missing"),
        ExtensionFallbackReason.NoRoute,
        TestContext.Current.CancellationToken);
    Assert.Equal(ExtensionInvocationState.Handled, activeFallback.State);
    Assert.Contains("fallback-unregister=True", Body(activeFallback), StringComparison.Ordinal);

    var futureFallback = await manager.HandleFallbackAsync(
        new ExtensionHandlerRequest("GET", "/missing"),
        ExtensionFallbackReason.NoRoute,
        TestContext.Current.CancellationToken);
    Assert.Equal(ExtensionInvocationState.NotHandled, futureFallback.State);

    var status = manager.GetStatus(manifest.Id)!;
    Assert.Equal(0, status.HandlerCount);
    Assert.False(status.HasFallback);
}

[Fact]
public async Task UnregisterIsOwnerScopedForHandlersAndFallbacks()
{
    using var firstFixture = TestExtensionDirectory.CreateJson(
        RuntimeManifestJson("first.unregister.extension"));
    using var secondFixture = TestExtensionDirectory.CreateJson(
        RuntimeManifestJson("second.unregister.extension"));
    var firstManifest = Discover(firstFixture.RootPath);
    var secondManifest = Discover(secondFixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

    Assert.True((await manager.LoadAsync(
        firstManifest,
        Settings(
            firstManifest.Id,
            label: "first-owner",
            handlerId: "fixture.first",
            registerFallback: true),
        TestContext.Current.CancellationToken)).Succeeded);
    Assert.True((await manager.LoadAsync(
        secondManifest,
        Settings(
            secondManifest.Id,
            label: "second-owner",
            handlerId: "fixture.second",
            attemptUnregisterHandlerId: "fixture.first",
            attemptUnregisterFallback: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var secondResult = await manager.HandleAsync(
        "fixture.second",
        new ExtensionHandlerRequest("GET", "/second"),
        TestContext.Current.CancellationToken);
    Assert.Equal(ExtensionInvocationState.Handled, secondResult.State);
    Assert.Contains("handler-unregister=False", Body(secondResult), StringComparison.Ordinal);
    Assert.Contains("fallback-unregister=False", Body(secondResult), StringComparison.Ordinal);

    Assert.Equal(
        ExtensionInvocationState.Handled,
        (await manager.HandleAsync(
            "fixture.first",
            new ExtensionHandlerRequest("GET", "/first"),
            TestContext.Current.CancellationToken)).State);
    Assert.Equal(
        ExtensionInvocationState.Handled,
        (await manager.HandleFallbackAsync(
            new ExtensionHandlerRequest("GET", "/missing"),
            ExtensionFallbackReason.NoRoute,
            TestContext.Current.CancellationToken)).State);
}

[Fact]
public async Task StartLifecycleRequestsReturnReentrantResultsAndLeaveTheExtensionLoaded()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

    var load = await manager.LoadAsync(
        manifest,
        Settings(manifest.Id, label: "start-lifecycle", requestLifecycleFromStart: true),
        TestContext.Current.CancellationToken);

    Assert.True(load.Succeeded, load.FailureCode.ToString());
    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/start-lifecycle"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "start-lifecycle=reload=Reentrant;unload=Reentrant;",
        Body(result),
        StringComparison.Ordinal);
    Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
}

[Fact]
public async Task PreviousStoppedLifecycleRequestsReturnReentrantResultsAndLeaveTheReplacementLoaded()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(manifest.Id, label: "old"),
        TestContext.Current.CancellationToken)).Succeeded);

    var replacement = await manager.ReloadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "previous-stopped-lifecycle",
            requestLifecycleFromPreviousStopped: true),
        TestContext.Current.CancellationToken);

    Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());
    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/previous-stopped-lifecycle"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "previous-stopped-lifecycle=reload=Reentrant;unload=Reentrant;",
        Body(result),
        StringComparison.Ordinal);
    Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
}

[Fact]
public async Task StopLifecycleRequestsReturnReentrantResultsAndUnloadCleanly()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    using var observationListener = new TcpListener(IPAddress.Loopback, 0);
    observationListener.Start();

    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "stop-lifecycle",
            requestLifecycleFromStop: true,
            lifecycleObservationPort: ((IPEndPoint)observationListener.LocalEndpoint).Port),
        TestContext.Current.CancellationToken)).Succeeded);

    var observationClientTask = observationListener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
    var unload = await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken);

    Assert.True(unload.Succeeded, unload.FailureCode.ToString());
    Assert.Equal(ExtensionFailureCode.None, unload.FailureCode);
    using var observationClient = await observationClientTask;
    var observationStream = observationClient.GetStream();
    var length = new byte[1];
    await observationStream.ReadExactlyAsync(length, TestContext.Current.CancellationToken);
    var payload = new byte[length[0]];
    await observationStream.ReadExactlyAsync(payload, TestContext.Current.CancellationToken);
    var observation = Encoding.UTF8.GetString(payload);
    Assert.StartsWith(
        "reload=Reentrant;unload=Reentrant;state=",
        observation,
        StringComparison.Ordinal);
    Assert.NotEqual("reload=Reentrant;unload=Reentrant;state=", observation);
    Assert.Null(manager.GetStatus(manifest.Id));
}

[Fact]
public async Task ConcurrentUnregisterTombstonesFutureDispatchesAndRejectsReregistration()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();

    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "concurrent-unregister",
            unregisterHandlerOnInvocation: true,
            reregisterHandlerAfterUnregister: true,
            unregisterBarrierPort: ((IPEndPoint)listener.LocalEndpoint).Port),
        TestContext.Current.CancellationToken)).Succeeded);

    var held = manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/held"),
        TestContext.Current.CancellationToken).AsTask();
    using var barrierClient = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
    var barrierStream = barrierClient.GetStream();
    var entered = new byte[1];
    await barrierStream.ReadExactlyAsync(entered, TestContext.Current.CancellationToken);

    var futureStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var futureDispatches = Enumerable.Range(0, 32)
        .Select(index => Task.Run(async () =>
        {
            await futureStart.Task.ConfigureAwait(false);
            return await manager.HandleAsync(
                    "fixture.handler",
                    new ExtensionHandlerRequest("GET", $"/future-{index}"),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }))
        .ToArray();
    futureStart.SetResult(true);
    var futureResults = await Task.WhenAll(futureDispatches);

    Assert.False(held.IsCompleted);
    Assert.All(
        futureResults,
        result => Assert.Equal(ExtensionInvocationState.Unavailable, result.State));

    await barrierStream.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
    var activeResult = await held;

    Assert.Equal(ExtensionInvocationState.Handled, activeResult.State);
    Assert.Contains("handler-unregister=True", Body(activeResult), StringComparison.Ordinal);
    Assert.Contains("handler-reregister=False", Body(activeResult), StringComparison.Ordinal);
    Assert.Equal(0, manager.GetStatus(manifest.Id)!.HandlerCount);
    Assert.Equal(
        ExtensionInvocationState.Unavailable,
        (await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/after"),
            TestContext.Current.CancellationToken)).State);
}

[Fact]
public async Task HandlerLifecycleRequestsReturnReentrantResultsImmediately()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(manifest.Id, label: "handler-lifecycle", requestLifecycleFromHandler: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/lifecycle"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "handler-lifecycle=reload=Reentrant;unload=Reentrant",
        Body(result),
        StringComparison.Ordinal);
    Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
}

[Fact]
public async Task FallbackLifecycleRequestsReturnReentrantResultsImmediately()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "fallback-lifecycle",
            registerFallback: true,
            requestLifecycleFromFallback: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var result = await manager.HandleFallbackAsync(
        new ExtensionHandlerRequest("GET", "/missing"),
        ExtensionFallbackReason.NoRoute,
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "fallback-lifecycle=reload=Reentrant;unload=Reentrant",
        Body(result),
        StringComparison.Ordinal);
    Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(manifest.Id)!.State);
}

[Fact]
public async Task TaskLifecycleRequestsReturnReentrantResultsImmediately()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "task-lifecycle",
            startTask: true,
            requestLifecycleFromTask: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/lifecycle"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "task-lifecycle=reload=Reentrant;unload=Reentrant",
        Body(result),
        StringComparison.Ordinal);
}

[Fact]
public async Task EventLifecycleRequestsReturnReentrantResultsImmediately()
{
    using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
    var manifest = Discover(fixture.RootPath);
    await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
    Assert.True((await manager.LoadAsync(
        manifest,
        Settings(
            manifest.Id,
            label: "event-lifecycle",
            publishCoreEvents: true,
            eventCount: 1,
            requestLifecycleFromEvent: true),
        TestContext.Current.CancellationToken)).Succeeded);

    var result = await manager.HandleAsync(
        "fixture.handler",
        new ExtensionHandlerRequest("GET", "/lifecycle"),
        TestContext.Current.CancellationToken);

    Assert.Equal(ExtensionInvocationState.Handled, result.State);
    Assert.Contains(
        "event-lifecycle=reload=Reentrant;unload=Reentrant",
        Body(result),
        StringComparison.Ordinal);
}

    [Fact]
    public void LoaderRejectsContractCatalogEntriesInsideExtensionRoots()
    {
        using var fixture = TestExtensionDirectory.CreateJson(TypedContractManifestJson());
        var manifest = Discover(fixture.RootPath);
        var contractsAssembly = typeof(IExtensionLogger).Assembly;
        var catalog = new ExtensionContractCatalog(
            [new ExtensionContractCatalogEntry(
                contractsAssembly.GetName().FullName!,
                Path.Combine(fixture.RootPath, "Nekolla.Nekostick.Contracts.dll"))]);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0), catalog);

        var result = loader.Load(manifest);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.ContractCatalogUnavailable, result.FailureCode);
    }

    [Fact]
    public void LoaderRejectsEntryTypesOutsideThePublicEntrypointAbi()
    {
        using var fixture = TestExtensionDirectory.CreateJson(
            RuntimeManifestJson(entryType: typeof(KnownFixtureService).FullName!));
        var manifest = Discover(fixture.RootPath);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));

        var result = loader.Load(manifest);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.EntryTypeNotCompatible, result.FailureCode);
    }

    [Fact]
    public void SuccessfulCollectibleLoadHasAReachableBoundedUnloadResult()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var unload = LoadAndUnloadCollectibleExtension(manifest);

        Assert.True(unload.Succeeded);
        Assert.Equal(ExtensionRuntimeState.Unloaded, unload.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ExtensionUnloadResult LoadAndUnloadCollectibleExtension(ExtensionManifest manifest)
    {
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));
        var loaded = loader.Load(manifest);

        Assert.True(loaded.Succeeded, loaded.FailureCode.ToString());
        Assert.NotNull(loaded.Handle);
        return loaded.Handle!.Unload();
    }

    private static string Body(ExtensionInvocationResult result) =>
        Encoding.UTF8.GetString(result.Response!.Body.AsSpan());

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }

    private static ExtensionSettingsConfiguration Settings(
        string extensionId,
        string label = "fixture",
        string handlerId = "fixture.handler",
        bool startFails = false,
        bool stopFails = false,
        bool previousStoppedFails = false,
        bool handlerFails = false,
        bool registerFallback = false,
        string? duplicateOption = null,
        bool startTask = false,
        bool publishOrderedEvents = false,
        bool publishBoundedEvents = false,
        bool publishCoreEvents = false,
        bool typedContractExchange = false,
        int eventCount = 3,
        bool verifyBridgeCapabilities = false,
        bool requestLifecycleFromHandler = false,
        bool requestLifecycleFromFallback = false,
        bool requestLifecycleFromTask = false,
        bool requestLifecycleFromEvent = false,
        bool unregisterHandlerOnInvocation = false,
        bool unregisterFallbackOnInvocation = false,
        bool reregisterHandlerAfterUnregister = false,
        string? attemptUnregisterHandlerId = null,
        bool attemptUnregisterFallback = false,
        bool includeFallbackCount = false,
        bool requestLifecycleFromStart = false,
        bool requestLifecycleFromPreviousStopped = false,
        bool requestLifecycleFromStop = false,
        int lifecycleObservationPort = 0,
        int unregisterBarrierPort = 0)
    {
        var json = JsonSerializer.Serialize(new
        {
            label,
            handlerId,
            startFails,
            stopFails,
            previousStoppedFails,
            handlerFails,
            registerFallback,
            duplicateHandler = duplicateOption == "duplicateHandler",
            duplicateFallback = duplicateOption == "duplicateFallback",
            startTask,
            publishOrderedEvents,
            publishBoundedEvents,
            publishCoreEvents,
            typedContractExchange,
            eventCount,
            verifyBridgeCapabilities,
            requestLifecycleFromHandler,
            requestLifecycleFromFallback,
            requestLifecycleFromTask,
            requestLifecycleFromEvent,
            unregisterHandlerOnInvocation,
            unregisterFallbackOnInvocation,
            reregisterHandlerAfterUnregister,
            attemptUnregisterHandlerId,
            attemptUnregisterFallback,
            includeFallbackCount,
            requestLifecycleFromStart,
            requestLifecycleFromPreviousStopped,
            requestLifecycleFromStop,
            lifecycleObservationPort,
            unregisterBarrierPort
        });
        return new ExtensionSettingsConfiguration(extensionId, 1, json, 0);
    }

    private static string TypedContractManifestJson()
    {
        var assemblyIdentity = JsonSerializer.Serialize(typeof(IExtensionLogger).Assembly.GetName().FullName);
        var typeIdentity = JsonSerializer.Serialize(typeof(IExtensionLogger).FullName);
        var baseManifest = RuntimeManifestJson();
        return baseManifest[..^1] +
            ",\n  \"exports\": [{\"contractId\": \"fixture.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": " +
            assemblyIdentity + ", \"typeIdentity\": " + typeIdentity + "}],\n" +
            "  \"imports\": [{\"contractId\": \"fixture.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": " +
            assemblyIdentity + ", \"typeIdentity\": " + typeIdentity + "}]\n}";
    }

    private static string RuntimeManifestJson(
        string id = "fixture.extension.deterministic",
        string version = "1.0.0",
        string? entryType = null)
    {
        return "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"id\": " + JsonSerializer.Serialize(id) + ",\n" +
            "  \"version\": " + JsonSerializer.Serialize(version) + ",\n" +
            "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
            "  \"entryType\": " + JsonSerializer.Serialize(
                entryType ?? typeof(FixtureEntrypoint).FullName!) + ",\n" +
            "  \"dependencies\": [],\n" +
            "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
            "}";
    }
}
