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

public sealed partial class ExtensionRuntimeTests
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


}
