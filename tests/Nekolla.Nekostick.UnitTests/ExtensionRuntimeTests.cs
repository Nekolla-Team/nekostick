using System.Collections.Immutable;
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
    public async Task SlowStartTimeoutIsClassifiedAsLifecycleFailed()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var load = new CollectibleExtensionLoader(new SemVersion(1, 0, 0)).Load(manifest);
        Assert.True(load.Succeeded, load.FailureCode.ToString());

        var instance = new ExtensionInstance(
            manifest,
            load.Handle!,
            HostApiVersion.Current,
            Settings(manifest.Id, startDelayMilliseconds: 1_000),
            static (_, _, _) => ExtensionContractProviderResolution.Failure(
                new ExtensionErrorDetail("The runtime test does not configure a contract provider.")),
            ImmutableDictionary<string, SemVersion>.Empty.Add(manifest.Id, manifest.Version),
            capabilityFactory: null);
        try
        {
            Assert.False(await instance.StartAsync(
                reloading: false,
                timeout: TimeSpan.FromMilliseconds(20),
                cancellationToken: CancellationToken.None));
            Assert.Equal(ExtensionFailureCode.LifecycleFailed, instance.GetStatus().LastFailure);
        }
        finally
        {
            await instance.AbortAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task OldStopFailureHonestlyMarksTheStoppedGeneration()
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
        // The stop pipeline already ran; a state flip would resurrect a zombie.
        Assert.Equal(ExtensionLoadState.Stopped, manager.GetStatus(manifest.Id)!.State);
        var dispatch = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Unavailable, dispatch.State);
    }

    [Fact]
    public async Task PreviousStoppedFailureHonestlyMarksTheStoppedGeneration()
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
        Assert.Equal(ExtensionLoadState.Stopped, manager.GetStatus(manifest.Id)!.State);
        var dispatch = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Unavailable, dispatch.State);
    }

    [Fact]
    public async Task AbortedStagedCommitHonestlyMarksTheStoppedPreviousGeneration()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var initial = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                Settings(manifest.Id, label: "old"),
                ["fixture.handler"],
                true)),
            previous: null,
            cancellationToken: cancellationToken);
        Assert.True(initial.Succeeded, initial.FailureCode.ToString());
        Assert.NotNull(initial.Preparation);
        var initialReady = await initial.Preparation!.ReadyToPublishAsync(cancellationToken);
        Assert.True(initialReady.Succeeded, initialReady.FailureCode.ToString());
        Assert.True(await initial.Preparation.CompletePublicationAsync());
        var initialInstance = initialReady.Generation!.Contexts.Single().Instance;

        var replacement = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                Settings(manifest.Id, label: "candidate", previousStoppedFails: true),
                ["fixture.handler"],
                true)),
            previous: initialReady.Generation,
            cancellationToken: cancellationToken);
        Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());
        Assert.NotNull(replacement.Preparation);

        // The stop and the OnPreviousStoppedAsync hook are deferred behind the Host handoff,
        // so a failing hook can no longer abort a staged commit that already drained the
        // previous generation; the failure stays recorded on the candidate instead.
        var ready = await replacement.Preparation!.ReadyToPublishAsync(cancellationToken);

        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await replacement.Preparation.CompletePublicationAsync());
        var candidateInstance = ready.Generation!.Contexts.Single().Instance;
        Assert.Equal(ExtensionLoadState.Stopped, initialInstance.GetStatus().State);
        Assert.Equal(ExtensionLoadState.Loaded, candidateInstance.GetStatus().State);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed, candidateInstance.GetStatus().LastFailure);
        Assert.NotNull(candidateInstance.GetStatus().LastFailureDetail);

        var dispatch = await ready.Generation!.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, dispatch.State);
        Assert.Equal("candidate:started", Body(dispatch));
    }

    [Fact]
    public async Task SuspendedRequestDuringPublicationWindowResolvesToReplacementInstance()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var oldGeneration = await PublishStagedGenerationAsync(
            manager,
            manifest,
            Settings(manifest.Id, label: "old"),
            previous: null,
            cancellationToken);
        var replacement = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                Settings(manifest.Id, label: "new"),
                ["fixture.handler"],
                true)),
            previous: oldGeneration,
            cancellationToken: cancellationToken);
        Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());
        var preparation = replacement.Preparation!;

        var ready = await preparation.ReadyToPublishAsync(cancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());

        // The handoff window is open: the turnstile is suspended and the previous instance is
        // still alive but draining. The request parks on the suspended turnstile instead of
        // failing fast against the draining instance and resolves to the replacement once the
        // publication completes.
        var pending = oldGeneration.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);

        Assert.True(await preparation.CompletePublicationAsync());
        var result = await pending;

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.StartsWith("new:", Body(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbortedPublicationResumesTurnstileOntoLivePreviousInstance()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var oldGeneration = await PublishStagedGenerationAsync(
            manager,
            manifest,
            Settings(manifest.Id, label: "old"),
            previous: null,
            cancellationToken);
        var oldInstance = oldGeneration.Contexts.Single().Instance;

        var replacement = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                Settings(manifest.Id, label: "candidate"),
                ["fixture.handler"],
                true)),
            previous: oldGeneration,
            cancellationToken: cancellationToken);
        Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());
        var preparation = replacement.Preparation!;

        // ReadyToPublishAsync suspends the turnstile but no longer stops the previous
        // instance; aborting the commit must lift the suspension back onto that still-live
        // instance instead of a stopped generation.
        var ready = await preparation.ReadyToPublishAsync(cancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        await preparation.AbortAsync();

        Assert.Equal(ExtensionLoadState.Loaded, oldInstance.GetStatus().State);
        var dispatch = await oldGeneration.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, dispatch.State);
        Assert.Equal("old:started", Body(dispatch));
    }

    [Fact]
    public async Task SameGenerationRebindWaitsWithinEntryBudgetThenResolves()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var replacement = await PublishStagedGenerationAsync(
            manager,
            manifest,
            Settings(manifest.Id, label: "rebind"),
            previous: null,
            cancellationToken);
        var context = replacement.Contexts.Single();
        var waits = new List<TimeSpan>();
        ExtensionDispatchGeneration? current = null;
        var generation = new ExtensionDispatchGeneration(
            generationId: 0,
            handlers: ImmutableDictionary<string, ExtensionDispatchBinding>.Empty.Add(
                "fixture.handler",
                new ExtensionDispatchBinding(context, context.Instance.Handlers["fixture.handler"], null)),
            fallback: null,
            contexts: ImmutableArray.Create(context),
            bindings: ImmutableArray<ExtensionGenerationBindingStatus>.Empty,
            owner: new object(),
            enterDispatch: static (_, _) => new ValueTask<ExtensionInstance?>((ExtensionInstance?)null),
            currentGeneration: () => current,
            isEntrySuspended: static _ => true,
            waitForEntryResolution: (_, timeout, _) =>
            {
                waits.Add(timeout);
                current = replacement;
                return new ValueTask<bool>(true);
            });
        current = generation;

        var result = await generation.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.Equal("rebind:started", Body(result));
        var waited = Assert.Single(waits);
        Assert.True(waited > TimeSpan.Zero);
        Assert.True(waited <= ExtensionDispatchTurnstile.EntryTimeout);
    }

    [Fact]
    public async Task SameGenerationRebindExhaustingEntryBudgetReturnsUnavailable()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var published = await PublishStagedGenerationAsync(
            manager,
            manifest,
            Settings(manifest.Id, label: "rebind"),
            previous: null,
            cancellationToken);
        var context = published.Contexts.Single();
        var waits = new List<TimeSpan>();
        var enterAttempts = 0;
        ExtensionDispatchGeneration? current = null;
        var generation = new ExtensionDispatchGeneration(
            generationId: 0,
            handlers: ImmutableDictionary<string, ExtensionDispatchBinding>.Empty.Add(
                "fixture.handler",
                new ExtensionDispatchBinding(context, context.Instance.Handlers["fixture.handler"], null)),
            fallback: null,
            contexts: ImmutableArray.Create(context),
            bindings: ImmutableArray<ExtensionGenerationBindingStatus>.Empty,
            owner: new object(),
            enterDispatch: (_, _) =>
            {
                enterAttempts++;
                return new ValueTask<ExtensionInstance?>((ExtensionInstance?)null);
            },
            currentGeneration: () => current,
            isEntrySuspended: static _ => true,
            waitForEntryResolution: (_, timeout, _) =>
            {
                waits.Add(timeout);
                return new ValueTask<bool>(false);
            });
        current = generation;

        var result = await generation.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);

        Assert.Equal(ExtensionInvocationState.Unavailable, result.State);
        Assert.NotNull(result.FailureDetail);
        Assert.Contains("entry timeout budget", result.FailureDetail!.Message, StringComparison.Ordinal);
        Assert.Equal(1, enterAttempts);
        var waited = Assert.Single(waits);
        Assert.True(waited > TimeSpan.Zero);
        Assert.True(waited <= ExtensionDispatchTurnstile.EntryTimeout);
    }

    [Fact]
    public async Task DeferredStopFailureHonestlyMarksStoppedPrevious()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var oldGeneration = await PublishStagedGenerationAsync(
            manager,
            manifest,
            Settings(manifest.Id, label: "old", stopFails: true),
            previous: null,
            cancellationToken);
        var oldInstance = oldGeneration.Contexts.Single().Instance;

        var replacement = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                Settings(manifest.Id, label: "candidate"),
                ["fixture.handler"],
                true)),
            previous: oldGeneration,
            cancellationToken: cancellationToken);
        Assert.True(replacement.Succeeded, replacement.FailureCode.ToString());
        var preparation = replacement.Preparation!;

        // The stop is deferred behind the Host handoff: a stop failure no longer fails the
        // ready phase, it stays honest at completion instead of resurrecting the previous
        // generation or failing the committed publication.
        var ready = await preparation.ReadyToPublishAsync(cancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());

        Assert.Equal(ExtensionLoadState.Stopped, oldInstance.GetStatus().State);
        Assert.Equal(ExtensionFailureCode.StopFailed, oldInstance.GetStatus().LastFailure);

        var dispatch = await ready.Generation!.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/fixture"),
            cancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, dispatch.State);
        Assert.Equal("candidate:previous-stopped", Body(dispatch));
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
        Assert.Equal(ExtensionFailureCode.HandlerConflict, result.FailureCode);
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
    public async Task TargetedCoreEventsReachOnlyTheSelectedServingOwner()
    {
        const string firstId = "first.target.extension";
        const string secondId = "second.target.extension";
        using var firstFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(firstId));
        using var secondFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(secondId));
        var firstManifest = Discover(firstFixture.RootPath);
        var secondManifest = Discover(secondFixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            firstManifest,
            Settings(firstId, publishCoreEvents: true, eventCount: 3),
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.LoadAsync(
            secondManifest,
            Settings(
                secondId,
                handlerId: "second.target.handler",
                publishCoreEvents: true,
                eventCount: 2),
            TestContext.Current.CancellationToken)).Succeeded);

        var payload = JsonSerializer.Serialize(new { extensionId = firstId });
        Assert.Equal(
            1,
            manager.PublishCoreEvent(
                new ExtensionCoreEvent(
                    ExtensionCoreEventKind.ExtensionSettingsChanged,
                    1,
                    payload),
                firstId));

        var firstResult = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/targeted"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, firstResult.State);
        Assert.Contains(payload, Body(firstResult), StringComparison.Ordinal);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var secondResult = await manager.HandleAsync(
            "second.target.handler",
            new ExtensionHandlerRequest("GET", "/targeted"),
            timeout.Token);
        Assert.Equal(ExtensionInvocationState.Failed, secondResult.State);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(secondId)!.State);
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

    private static async Task<ExtensionDispatchGeneration> PublishStagedGenerationAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest manifest,
        ExtensionSettingsConfiguration settings,
        ExtensionDispatchGeneration? previous,
        CancellationToken cancellationToken)
    {
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                settings,
                ["fixture.handler"],
                true)),
            previous,
            cancellationToken: cancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        var preparation = Assert.IsType<ExtensionGenerationPreparation>(prepared.Preparation);
        var ready = await preparation.ReadyToPublishAsync(cancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());
        return Assert.IsType<ExtensionDispatchGeneration>(ready.Generation);
    }


}
