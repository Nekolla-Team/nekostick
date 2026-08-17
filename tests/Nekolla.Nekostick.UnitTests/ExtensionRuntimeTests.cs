using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    public async Task BoundedEventQueueDropsExactlyTheNewestEventAtCapacity1024()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(manifest.Id, publishBoundedEvents: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var status = manager.GetStatus(manifest.Id)!;
        Assert.Equal(1, status.DroppedEvents);
        Assert.Equal(0, status.ActiveRequests);
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

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }

    private static string Body(ExtensionInvocationResult result)
    {
        Assert.NotNull(result.Response);
        return Encoding.UTF8.GetString(result.Response!.Body.ToArray());
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
        bool publishBoundedEvents = false)
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
            eventCount = 3
        });
        return new ExtensionSettingsConfiguration(extensionId, 1, json, 0);
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
