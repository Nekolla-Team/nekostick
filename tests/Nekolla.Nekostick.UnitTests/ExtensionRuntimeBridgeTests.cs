using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
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
    Assert.Contains("api=1.3.2", body, StringComparison.Ordinal);
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
}
