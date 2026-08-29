using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostExtensionEvidenceTests
{
    private const string FixtureExtensionId = "fixture.extension.deterministic";
    private static readonly SemaphoreSlim OutputExtensionGate = new(1, 1);
    [Fact]
    public async Task ReadOnlyRecordlessPublisherDoesNotBootstrapOrLoadDiscoveredExtension()
    {
        await OutputExtensionGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var fixture = TestExtensionDirectory.CreateJson();
            using var staged = StagedHostExtensionDirectory.Create(fixture.RootPath);
            await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
            var holder = new HostConfigurationSnapshotHolder();
            await using var publisher = new HostConfigurationPublisher(
                holder,
                manager,
                new HostNodeOptions(
                    skipExtensions: false,
                    disableSupervisor: false,
                    readOnly: true,
                    extensionsRootPath: staged.InstallRoot),
                NullLogger<HostConfigurationPublisher>.Instance);
            var snapshot = CreatePublisherSnapshot(1, ImmutableArray<ExtensionRecordConfiguration>.Empty);

            Assert.True(await publisher.PublishAsync(
                snapshot,
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Same(snapshot, holder.Current);
            Assert.Empty(snapshot.ExtensionRecords);
            Assert.Null(manager.GetStatus(FixtureExtensionId));
        }
        finally
        {
            OutputExtensionGate.Release();
        }
    }


    [Fact]
    public async Task ForcedPublisherReloadStartsReplacementAndStopsPreviousInstance()
    {
        await OutputExtensionGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var fixture = TestExtensionDirectory.CreateJson();
            using var staged = StagedHostExtensionDirectory.Create(fixture.RootPath);
            var manifest = Discover(fixture.RootPath);
            await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
            var settings = Settings(FixtureExtensionId, "forced-reload");
            var oldGeneration = await PrepareAndPublishGenerationAsync(
                manager,
                manifest,
                settings,
                previous: null);
            var holder = new HostConfigurationSnapshotHolder();
            var first = CreateExtensionSnapshot(
                1,
                CreateRoute(
                    RoutingTestData.Id(850),
                    "/forced-reload",
                    new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
                FixtureExtensionId,
                settings);
            Assert.True(holder.TryReplace(first, oldGeneration));

            await using var publisher = new HostConfigurationPublisher(
                holder,
                manager,
                new HostNodeOptions(
                    skipExtensions: false,
                    disableSupervisor: false,
                    readOnly: false,
                    extensionsRootPath: staged.InstallRoot),
                NullLogger<HostConfigurationPublisher>.Instance);
            var reloaded = await publisher.RequestExtensionReloadAsync(
                first,
                FixtureExtensionId,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                HostConfigurationPublisher.ExtensionReloadPublicationStatus.Published,
                reloaded.Status);
            Assert.Equal(first.Version, reloaded.CommittedVersion);

            var status = manager.GetStatus(FixtureExtensionId);
            Assert.NotNull(status);
            Assert.Equal(ExtensionLoadState.Loaded, status!.State);
            var handled = await holder.RoutingSnapshot!.DispatchGeneration!.HandleAsync(
                FixtureExtensionId,
                new ExtensionHandlerRequest("GET", "/forced-reload"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionInvocationState.Handled, handled.State);
            var body = Encoding.UTF8.GetString(handled.Response!.Body.AsSpan());
            Assert.Equal("forced-reload:previous-stopped", body);
            Assert.NotSame(oldGeneration, holder.RoutingSnapshot!.DispatchGeneration);
        }
        finally
        {
            OutputExtensionGate.Release();
        }
    }

    [Fact]
    public async Task ForcedReloadFallbackReusesPriorGenerationAndStillDeliversPublicationEvents()
    {
        await OutputExtensionGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
        var extensionId = "reuse.extension." + Guid.NewGuid().ToString("N");
        using var fixture = TestExtensionDirectory.CreateJson(
            ExtensionManifestTestDefaults.Json.Replace(
                "fixture.extension.deterministic",
                extensionId,
                StringComparison.Ordinal));
        using var staged = StagedHostExtensionDirectory.Create(fixture.RootPath);
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var settings = Settings(extensionId, "reuse", publishCoreEvents: true, eventCount: 1);
        var oldGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            settings,
            previous: null,
            requestedHandlerId: extensionId);
        var holder = new HostConfigurationSnapshotHolder();
        var first = CreateExtensionSnapshot(
            1,
            CreateRoute(
                RoutingTestData.Id(851),
                "/reuse",
                new ExtensionHandlerRouteTargetConfiguration(extensionId)),
            extensionId,
            settings);
        Assert.True(holder.TryReplace(first, oldGeneration));

        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(
                skipExtensions: false,
                disableSupervisor: false,
                readOnly: false,
                extensionsRootPath: staged.InstallRoot),
            NullLogger<HostConfigurationPublisher>.Instance);
        var failingSettings = Settings(
            extensionId,
            "reuse",
            startFails: true,
            publishCoreEvents: true,
            eventCount: 1);
        var second = CreateExtensionSnapshot(
            2,
            CreateRoute(
                RoutingTestData.Id(851),
                "/reuse-v2",
                new ExtensionHandlerRouteTargetConfiguration(extensionId),
                routeVersion: 2),
            extensionId,
            failingSettings);

        var fallbackReload = await publisher.RequestExtensionReloadAsync(
            second,
            extensionId,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            HostConfigurationPublisher.ExtensionReloadPublicationStatus.Failed,
            fallbackReload.Status);
        Assert.Same(oldGeneration, holder.RoutingSnapshot!.DispatchGeneration);

        var handled = await holder.RoutingSnapshot!.DispatchGeneration!.HandleAsync(
            extensionId,
            new ExtensionHandlerRequest("GET", "/reuse"),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, handled.State);
        var body = Encoding.UTF8.GetString(handled.Response!.Body.AsSpan());
        Assert.Contains("state", body, StringComparison.Ordinal);
        Assert.Contains("applied", body, StringComparison.Ordinal);
        }
        finally
        {
            OutputExtensionGate.Release();
        }
    }
    [Fact]
    public async Task ForcedReloadReportsTargetUnavailableWhenLatestSnapshotDisablesTarget()
    {
        var extensionId = "target-unavailable." + Guid.NewGuid().ToString("N");
        var initial = CreatePublisherSnapshot(
            1,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                extensionId,
                "1.0.0",
                ExtensionLoadState.Loaded,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1)));
        var latest = CreatePublisherSnapshot(
            2,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                extensionId,
                "1.0.0",
                ExtensionLoadState.Disabled,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                2)));
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await using var publisher = new HostConfigurationPublisher(
            new HostConfigurationSnapshotHolder(),
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance,
            snapshotReader: new LatestSnapshotReader(latest));

        var publication = await publisher.RequestExtensionReloadAsync(
            initial,
            extensionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            HostConfigurationPublisher.ExtensionReloadPublicationStatus.TargetUnavailable,
            publication.Status);
        Assert.Equal(0, publication.CommittedVersion);
    }



    [Fact]
    public async Task PersistedExtensionRecordSnapshotBootstrapsValidOutputFixture()
    {
        await OutputExtensionGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var fixture = TestExtensionDirectory.CreateJson();
            using var staged = StagedHostExtensionDirectory.Create(fixture.RootPath);
            await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
            var holder = new HostConfigurationSnapshotHolder();
            await using var publisher = new HostConfigurationPublisher(
                holder,
                manager,
                new HostNodeOptions(
                    skipExtensions: false,
                    disableSupervisor: false,
                    readOnly: false,
                    extensionsRootPath: staged.InstallRoot),
                NullLogger<HostConfigurationPublisher>.Instance);
            var record = new ExtensionRecordConfiguration(
                FixtureExtensionId,
                "1.0.0",
                ExtensionLoadState.Loaded,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1);
            var snapshot = CreatePublisherSnapshot(
                1,
                ImmutableArray.Create(record));

            Assert.True(await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Same(snapshot, holder.Current);
            Assert.Single(holder.Current!.ExtensionRecords);
            Assert.Equal(ExtensionLoadState.Loaded, holder.Current.ExtensionRecords[0].LoadState);
            var status = manager.GetStatus(FixtureExtensionId);
            Assert.NotNull(status);
            Assert.Equal(ExtensionLoadState.Loaded, status!.State);
        }
        finally
        {
            OutputExtensionGate.Release();
        }
    }

    [Theory]
    [InlineData(ExtensionLoadState.Discovered)]
    [InlineData(ExtensionLoadState.Stopped)]
    [InlineData(ExtensionLoadState.Disabled)]
    [InlineData(ExtensionLoadState.Failed)]
    public async Task NonLoadedExtensionRecordDoesNotBootstrapOutputFixture(
        ExtensionLoadState loadState)
    {
        await OutputExtensionGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var fixture = TestExtensionDirectory.CreateJson();
            using var staged = StagedHostExtensionDirectory.Create(fixture.RootPath);
            await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
            var holder = new HostConfigurationSnapshotHolder();
            await using var publisher = new HostConfigurationPublisher(
                holder,
                manager,
                new HostNodeOptions(
                    skipExtensions: false,
                    disableSupervisor: false,
                    readOnly: false,
                    extensionsRootPath: staged.InstallRoot),
                NullLogger<HostConfigurationPublisher>.Instance);
            var record = new ExtensionRecordConfiguration(
                FixtureExtensionId,
                "1.0.0",
                loadState,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1);
            var snapshot = CreatePublisherSnapshot(
                1,
                ImmutableArray.Create(record),
                ImmutableArray.Create(Settings(FixtureExtensionId, "non-loaded")));

            Assert.True(await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Same(snapshot, holder.Current);
            Assert.Single(holder.Current!.ExtensionRecords);
            Assert.Equal(loadState, holder.Current.ExtensionRecords[0].LoadState);
            Assert.Null(manager.GetStatus(FixtureExtensionId));
        }
        finally
        {
            OutputExtensionGate.Release();
        }
    }

    [Fact]
    public async Task PublisherCompletesPrepareReadyExchangeAndCompleteForNormalSnapshot()
    {
        var holder = new HostConfigurationSnapshotHolder();
        await using var publisher = new HostConfigurationPublisher(
            holder,
            new ExtensionRuntimeManager(HostApiVersion.Current),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);
        var snapshot = CreateSnapshot(
            1,
            CreateRoute(
                RoutingTestData.Id(800),
                "/normal",
                new StaticFileRouteTargetConfiguration(Path.GetTempPath())));

        Assert.True(await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(snapshot, holder.Current);
        Assert.NotNull(holder.RoutingSnapshot);
        Assert.NotNull(holder.RoutingSnapshot!.DispatchGeneration);
        Assert.Empty(holder.RoutingSnapshot.DispatchGeneration!.Bindings);
        Assert.Equal(
            RouteMatchStatus.Matched,
            holder.RoutingSnapshot.Matcher.Match(
                new RouteMatchInput("/normal", "integration.test", "GET")).Status);
    }

    [Fact]
    public async Task AcceptedSnapshotAndRouteChangesReachServingExtensionCoreEventQueue()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var settings = Settings(
            FixtureExtensionId,
            "core-events",
            publishCoreEvents: true,
            eventCount: 3);
        var holder = new HostConfigurationSnapshotHolder();
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);
        var routeId = RoutingTestData.Id(805);
        var first = CreateExtensionSnapshot(
            1,
            CreateRoute(
                routeId,
                "/core-events",
                new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
            FixtureExtensionId,
            settings);
        var second = CreateExtensionSnapshot(
            2,
            CreateRoute(
                routeId,
                "/core-events-v2",
                new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId),
                routeVersion: 2),
            FixtureExtensionId,
            settings);

        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            settings,
            previous: null);
        Assert.True(holder.TryReplace(first, generation));
        Assert.True(await publisher.PublishAsync(second, cancellationToken: TestContext.Current.CancellationToken));
        var status = manager.GetStatus(FixtureExtensionId);
        Assert.NotNull(status);
        Assert.Equal(1, status!.HandlerCount);
        Assert.Equal(ExtensionLoadState.Loaded, status.State);

        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());
        var dispatch = await DispatchAsync(holder, targetExecutor, "/core-events-v2");
        Assert.Equal(StatusCodes.Status200OK, dispatch.StatusCode);
        var body = dispatch.Body;
        Assert.Contains("\"state\":\"Loaded\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"applied\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"changed\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublisherEmitsSettingsEventsOnlyForChangedAddedAndRemovedEntries()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var runtimeSettings = Settings(
            FixtureExtensionId,
            "settings-events",
            publishCoreEvents: true,
            eventCount: 8);
        Assert.True((await manager.LoadAsync(
            manifest,
            runtimeSettings,
            TestContext.Current.CancellationToken)).Succeeded);

        var holder = new HostConfigurationSnapshotHolder();
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);
        var record = new ExtensionRecordConfiguration(
            FixtureExtensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
        var oldSettings = Settings(FixtureExtensionId, "old", settingsVersion: 1);
        var sameSettings = Settings(FixtureExtensionId, "old", settingsVersion: 1);
        var changedSettings = Settings(FixtureExtensionId, "new", settingsVersion: 2);
        var initial = CreatePublisherSnapshot(1, [record], [oldSettings]);
        var same = CreatePublisherSnapshot(2, [record], [sameSettings]);
        var changed = CreatePublisherSnapshot(3, [record], [changedSettings]);
        var noSettings = CreatePublisherSnapshot(
            4,
            [record],
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var added = CreatePublisherSnapshot(5, [record], [oldSettings]);
        var removed = CreatePublisherSnapshot(
            6,
            [record],
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        InvokePublishSnapshotEvents(publisher, same, initial);
        InvokePublishSnapshotEvents(publisher, changed, same);
        InvokePublishSnapshotEvents(publisher, added, noSettings);
        InvokePublishSnapshotEvents(publisher, removed, initial);

        var result = await manager.HandleAsync(
            FixtureExtensionId,
            new ExtensionHandlerRequest("GET", "/settings-events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var body = Encoding.UTF8.GetString(result.Response!.Body.AsSpan());
        var settingsPayload = $"{{\"extensionId\":\"{FixtureExtensionId}\"}}";
        Assert.Equal(3, body.Split(settingsPayload, StringSplitOptions.None).Length - 1);
        Assert.Contains("{\"version\":2,\"state\":\"applied\"}", body, StringComparison.Ordinal);
        Assert.Contains("{\"version\":3,\"state\":\"applied\"}", body, StringComparison.Ordinal);
        Assert.Contains("{\"version\":5,\"state\":\"applied\"}", body, StringComparison.Ordinal);
        Assert.Contains("{\"version\":6,\"state\":\"applied\"}", body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ReadyHandoffRetainsNewGenerationAsManagerOwner()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var oldSettings = Settings(FixtureExtensionId, "old");
        var oldGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            oldSettings,
            previous: null);

        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateExtensionSnapshot(
                1,
                CreateRoute(
                    RoutingTestData.Id(808),
                    "/extension",
                    new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
                FixtureExtensionId,
                oldSettings),
            oldGeneration));

        var newSettings = Settings(FixtureExtensionId, "new");
        var prepared = await manager.PrepareGenerationAsync(ImmutableArray.Create(
            new ExtensionRuntimeDescriptor(
                manifest,
                newSettings,
                [FixtureExtensionId])), oldGeneration, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        Assert.NotNull(prepared.Preparation);
        var preparation = prepared.Preparation!;
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.NotNull(ready.Generation);

        var replacement = CreateExtensionSnapshot(
            2,
            CreateRoute(
                RoutingTestData.Id(809),
                "/extension",
                new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
            FixtureExtensionId,
            newSettings);
        Assert.True(holder.TryReplace(replacement, ready.Generation));
        Assert.True(await preparation.CompletePublicationAsync());

        var followUp = await manager.PrepareGenerationAsync(ImmutableArray.Create(
            new ExtensionRuntimeDescriptor(
                manifest,
                newSettings,
                [FixtureExtensionId])), ready.Generation, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(followUp.Succeeded, followUp.FailureCode.ToString());
        Assert.NotNull(followUp.Preparation);
        await followUp.Preparation!.AbortAsync();
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task ChangedSettingsWithLocalDiscoveryFailureDoesNotReusePriorGeneration()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var oldSettings = Settings(FixtureExtensionId, "old");
        var oldGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            oldSettings,
            previous: null);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateExtensionSnapshot(
                1,
                CreateRoute(
                    RoutingTestData.Id(810),
                    "/normal",
                    new StaticFileRouteTargetConfiguration(Path.GetTempPath())),
                FixtureExtensionId,
                oldSettings,
                recordVersion: 1),
            oldGeneration));

        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);
        var newSettings = Settings(
            FixtureExtensionId,
            "new",
            schemaVersion: 2,
            settingsVersion: 2);
        var replacement = CreateExtensionSnapshot(
            2,
            CreateRoute(
                RoutingTestData.Id(811),
                "/normal",
                new StaticFileRouteTargetConfiguration(Path.GetTempPath())),
            FixtureExtensionId,
            newSettings,
            recordVersion: 2);

        Assert.True(await publisher.PublishAsync(replacement, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Same(replacement, holder.Current);
        Assert.NotSame(oldGeneration, holder.RoutingSnapshot!.DispatchGeneration);
        Assert.Empty(holder.RoutingSnapshot.DispatchGeneration!.Bindings);
        Assert.Equal(
            RouteMatchStatus.Matched,
            holder.RoutingSnapshot.Matcher.Match(
                new RouteMatchInput("/normal", "integration.test", "GET")).Status);
    }

    [Fact]
    public async Task MissingLocalLoadedExtensionDoesNotBlockHostSnapshotPublication()
    {
        var extensionId = "missing.extension." + Guid.NewGuid().ToString("N");
        var snapshot = CreateSnapshot(
            1,
            CreateRoute(
                RoutingTestData.Id(801),
                "/normal",
                new StaticFileRouteTargetConfiguration(Path.GetTempPath())),
            CreateRoute(
                RoutingTestData.Id(802),
                "/extension",
                new ExtensionHandlerRouteTargetConfiguration(extensionId)),
            extensionId);
        var holder = new HostConfigurationSnapshotHolder();
        await using var publisher = new HostConfigurationPublisher(
            holder,
            new ExtensionRuntimeManager(HostApiVersion.Current),
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);

        Assert.True(await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(snapshot, holder.Current);
        Assert.NotNull(holder.RoutingSnapshot);
        Assert.NotNull(holder.RoutingSnapshot!.DispatchGeneration);
        Assert.Empty(holder.RoutingSnapshot.DispatchGeneration!.Bindings);
        Assert.Equal(
            RouteMatchStatus.Matched,
            holder.RoutingSnapshot.Matcher.Match(
                new RouteMatchInput("/normal", "integration.test", "GET")).Status);
    }

    [Fact]
    public async Task SkipExtensionsPublishesNormalRoutesButMakesExtensionTargetUnavailable()
    {
        var snapshot = CreateSnapshot(
            1,
            CreateRoute(
                RoutingTestData.Id(803),
                "/normal",
                new StaticFileRouteTargetConfiguration(Path.GetTempPath())),
            CreateRoute(
                RoutingTestData.Id(804),
                "/extension",
                new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
            FixtureExtensionId);
        var holder = new HostConfigurationSnapshotHolder();
        await using var publisher = new HostConfigurationPublisher(
            holder,
            new ExtensionRuntimeManager(HostApiVersion.Current),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);
        Assert.True(await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());
        var dispatch = await DispatchAsync(holder, targetExecutor, "/extension");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, dispatch.StatusCode);
        Assert.Equal("Service unavailable.", dispatch.Body);
        Assert.Equal(
            RouteMatchStatus.Matched,
            holder.RoutingSnapshot!.Matcher.Match(
                new RouteMatchInput("/normal", "integration.test", "GET")).Status);
    }

    [Fact]
    public async Task ReplacingSnapshotDoesNotReleaseOldGenerationUntilRequestLeaseIsReleased()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var oldSettings = Settings(FixtureExtensionId, "old");
        var oldGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            oldSettings,
            previous: null);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateSnapshot(
                1,
                CreateRoute(
                    RoutingTestData.Id(805),
                    "/extension",
                    new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
                FixtureExtensionId),
            oldGeneration));

        await using var oldLease = holder.TryAcquireRoutingLease()
            ?? throw new Xunit.Sdk.XunitException("The old routing generation did not accept a lease.");
        Assert.NotNull(oldLease.DispatchLease);

        var newSettings = Settings(FixtureExtensionId, "new");
        var newGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            newSettings,
            oldGeneration);
        var replacement = CreateSnapshot(
            2,
            CreateRoute(
                RoutingTestData.Id(806),
                "/extension",
                new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
            FixtureExtensionId);
        Assert.True(holder.TryReplace(replacement, newGeneration));

        var retirement = oldGeneration.RetireAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.True(oldGeneration.IsRetiring);
        Assert.False(retirement.IsCompleted);

        oldLease.Dispose();
        Assert.True(await retirement);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task ExtensionHandledResponseIsPreservedThroughHttpContextDispatcher()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            Settings(FixtureExtensionId, "handled"),
            previous: null);
        var holder = CreatePublishedHolder(generation);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());

        var dispatch = await DispatchAsync(holder, targetExecutor, "/extension");

        Assert.Equal(StatusCodes.Status200OK, dispatch.StatusCode);
        Assert.Equal("handled:started", dispatch.Body);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableExtensionTargetMapsTo503ThroughHttpContextDispatcher()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var emptyGeneration = await PrepareAndPublishGenerationAsync(
            manager,
            manifest: null,
            settings: null,
            previous: null);
        var holder = CreatePublishedHolder(emptyGeneration);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());

        var dispatch = await DispatchAsync(holder, targetExecutor, "/extension");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, dispatch.StatusCode);
        Assert.Equal("Service unavailable.", dispatch.Body);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task ExtensionHandlerFailureMapsTo500ThroughHttpContextDispatcher()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            Settings(
                FixtureExtensionId,
                "failed",
                handlerFails: true,
                registerFallback: true),
            previous: null,
            includeFallback: true);
        var holder = CreatePublishedHolder(generation);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());

        var dispatch = await DispatchAsync(holder, targetExecutor, "/extension");

        Assert.Equal(StatusCodes.Status500InternalServerError, dispatch.StatusCode);
        Assert.Equal("Internal server error.", dispatch.Body);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task ExtensionResponseWriteFailureMapsTo500AtDispatcherBoundary()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            Settings(FixtureExtensionId, "write-failure"),
            previous: null);
        var holder = CreatePublishedHolder(generation);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());
        var context = CreateContext("/extension");
        context.Response.Body = new ThrowingResponseStream();
        var dispatcher = new HostRouteDispatcher(
            new HostRoutingSnapshotAccessor(holder),
            new ExtensionRouteFallbackDispatcher(),
            targetExecutor);

        await dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task NoRouteAndDeclinedFallbackMapTo404ExactlyAtDispatcherBoundary()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest: null,
            settings: null,
            previous: null);
        var holder = CreatePublishedHolder(generation);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());

        var dispatch = await DispatchAsync(holder, targetExecutor, "/missing");

        Assert.Equal(StatusCodes.Status404NotFound, dispatch.StatusCode);
        Assert.Equal("Not found.", dispatch.Body);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task LoadedFixtureFallbackHandlesOnlyTheNoRouteCandidate()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAndPublishGenerationAsync(
            manager,
            manifest,
            Settings(FixtureExtensionId, "fallback", registerFallback: true),
            previous: null,
            includeFallback: true);
        var holder = CreatePublishedHolder(generation);
        using var services = CreateProxyServices();
        var targetExecutor = new HostRouteTargetExecutor(
            services.GetRequiredService<MicroserviceHttpExecutor>());

        var dispatch = await DispatchAsync(holder, targetExecutor, "/missing");

        Assert.Equal(StatusCodes.Status404NotFound, dispatch.StatusCode);
        Assert.Equal("fallback:NoRoute", dispatch.Body);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task FallbackReceivesHostMethodAndStatic404Reasons()
    {
        var staticRoot = Path.Combine(
            Path.GetTempPath(),
            "nekostick-fallback-taxonomy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staticRoot, "empty-directory"));
        try
        {
            using var fixture = TestExtensionDirectory.CreateJson();
            var manifest = Discover(fixture.RootPath);
            await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
            var generation = await PrepareAndPublishGenerationAsync(
                manager,
                manifest,
                Settings(FixtureExtensionId, "fallback-taxonomy", registerFallback: true),
                previous: null,
                includeFallback: true);
            var holder = new HostConfigurationSnapshotHolder();
            Assert.True(holder.TryReplace(
                CreateSnapshot(
                    1,
                    CreateRoute(
                        RoutingTestData.Id(820),
                        "/host-only",
                        new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId),
                        ImmutableArray.Create("other.test"),
                        ImmutableArray<string>.Empty),
                    CreateRoute(
                        RoutingTestData.Id(821),
                        "/method-only",
                        new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId),
                        ImmutableArray<string>.Empty,
                        ImmutableArray.Create("POST")),
                    CreateRoute(
                        RoutingTestData.Id(822),
                        "/missing-file",
                        new StaticFileRouteTargetConfiguration(staticRoot)),
                    CreateRoute(
                        RoutingTestData.Id(823),
                        "/empty-directory",
                        new StaticFileRouteTargetConfiguration(staticRoot)),
                    FixtureExtensionId),
                generation));

            using var services = CreateProxyServices();
            var targetExecutor = new HostRouteTargetExecutor(
                services.GetRequiredService<MicroserviceHttpExecutor>());

            var noRoute = await DispatchAsync(holder, targetExecutor, "/not-configured");
            var hostMismatch = await DispatchAsync(holder, targetExecutor, "/host-only");
            var methodMismatch = await DispatchAsync(holder, targetExecutor, "/method-only");
            var staticNotFound = await DispatchAsync(holder, targetExecutor, "/missing-file");
            var staticIndexMissing = await DispatchAsync(holder, targetExecutor, "/empty-directory");

            Assert.Equal((StatusCodes.Status404NotFound, "fallback-taxonomy:NoRoute"),
                (noRoute.StatusCode, noRoute.Body));
            Assert.Equal((StatusCodes.Status404NotFound, "fallback-taxonomy:HostMismatch"),
                (hostMismatch.StatusCode, hostMismatch.Body));
            Assert.Equal((StatusCodes.Status404NotFound, "fallback-taxonomy:MethodMismatch"),
                (methodMismatch.StatusCode, methodMismatch.Body));
            Assert.Equal((StatusCodes.Status404NotFound, "fallback-taxonomy:StaticNotFound"),
                (staticNotFound.StatusCode, staticNotFound.Body));
            Assert.Equal((StatusCodes.Status404NotFound, "fallback-taxonomy:StaticIndexMissing"),
                (staticIndexMissing.StatusCode, staticIndexMissing.Body));

            await holder.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(staticRoot))
            {
                Directory.Delete(staticRoot, recursive: true);
            }
        }
    }

    private static async Task<ExtensionDispatchGeneration> PrepareAndPublishGenerationAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest? manifest,
        ExtensionSettingsConfiguration? settings,
        ExtensionDispatchGeneration? previous,
        bool includeFallback = false,
        string? requestedHandlerId = null)
    {
        var desired = manifest is null
            ? ImmutableArray<ExtensionRuntimeDescriptor>.Empty
            : ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifest,
                    settings,
                    [requestedHandlerId ?? FixtureExtensionId],
                    includeFallback));
        var prepared = await manager.PrepareGenerationAsync(desired, previous, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        Assert.NotNull(prepared.Preparation);
        var preparation = prepared.Preparation!;
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.NotNull(ready.Generation);
        Assert.True(await preparation.CompletePublicationAsync());
        return ready.Generation!;
    }

    private static HostConfigurationSnapshotHolder CreatePublishedHolder(
        ExtensionDispatchGeneration generation)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(
            CreateSnapshot(
                1,
                CreateRoute(
                    RoutingTestData.Id(807),
                    "/extension",
                    new ExtensionHandlerRouteTargetConfiguration(FixtureExtensionId)),
                FixtureExtensionId),
            generation));
        return holder;
    }

    private static async Task<DispatchResult> DispatchAsync(
        HostConfigurationSnapshotHolder holder,
        HostRouteTargetExecutor targetExecutor,
        string path,
        string method = "GET",
        string host = "integration.test")
    {
        var context = CreateContext(path, method, host);
        var dispatcher = new HostRouteDispatcher(
            new HostRoutingSnapshotAccessor(holder),
            new ExtensionRouteFallbackDispatcher(),
            targetExecutor);
        await dispatcher.DispatchAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return new DispatchResult(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string method = "GET",
        string host = "integration.test")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        return context;
    }


    private static ServiceProvider CreateProxyServices()
    {
        var services = new ServiceCollection();
        services.AddMicroserviceProxy();
        return services.BuildServiceProvider();
    }

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }

    private static ExtensionSettingsConfiguration Settings(
        string extensionId,
        string label,
        bool handlerFails = false,
        bool startFails = false,
        bool registerFallback = false,
        bool publishCoreEvents = false,
        int eventCount = 3,
        int schemaVersion = 1,
        long settingsVersion = 1) =>
        new(
            extensionId,
            schemaVersion,
            settingsJson: JsonSerializer.Serialize(new
            {
                label,
                handlerId = extensionId,
                handlerFails,
                startFails,
                registerFallback,
                publishCoreEvents,
                eventCount
            }),
            version: settingsVersion);

    private static HostConfigurationSnapshot CreateExtensionSnapshot(
        long version,
        RouteConfiguration route,
        string extensionId,
        ExtensionSettingsConfiguration settings,
        long recordVersion = 1) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            ImmutableArray.Create(route),
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    extensionId,
                    "1.0.0",
                    ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    recordVersion)),
            ImmutableArray.Create(settings));

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        params object[] values)
    {
        var routes = values.OfType<RouteConfiguration>().ToImmutableArray();
        var extensionId = values.OfType<string>().SingleOrDefault();
        var records = extensionId is null
            ? ImmutableArray<ExtensionRecordConfiguration>.Empty
            : ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    extensionId,
                    "1.0.0",
                    ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    1));
        var settings = extensionId is null
            ? ImmutableArray<ExtensionSettingsConfiguration>.Empty
            : ImmutableArray.Create(Settings(extensionId, "snapshot"));
        return new HostConfigurationSnapshot(
            version,
            new GlobalSettingsConfiguration(version: version),
            routes,
            ImmutableArray<ServiceConfiguration>.Empty,
            records,
            settings);
    }
    private static HostConfigurationSnapshot CreatePublisherSnapshot(
        long version,
        ImmutableArray<ExtensionRecordConfiguration> records,
        ImmutableArray<ExtensionSettingsConfiguration> settings = default) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            records,
            settings);

    private static void InvokePublishSnapshotEvents(
        HostConfigurationPublisher publisher,
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshot? previous)
    {
        var method = typeof(HostConfigurationPublisher).GetMethod(
            "PublishSnapshotEvents",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(publisher, [snapshot, previous]);
    }

    private sealed class StagedHostExtensionDirectory : IDisposable
    {
        private StagedHostExtensionDirectory(string installRoot) => InstallRoot = installRoot;

        internal string InstallRoot { get; }

        internal static StagedHostExtensionDirectory Create(string sourceRoot)
        {
            var installRoot = Path.Combine(
                Path.GetTempPath(),
                "nekostick-evidence-" + Guid.NewGuid().ToString("N"));
            var rootPath = Path.Combine(installRoot, "fixture");

            try
            {
                Directory.CreateDirectory(rootPath);
                foreach (var sourcePath in Directory.EnumerateFiles(
                    sourceRoot,
                    "*",
                    SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                    var targetPath = Path.Combine(rootPath, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(sourcePath, targetPath);
                }

                return new StagedHostExtensionDirectory(installRoot);
            }
            catch
            {
                if (Directory.Exists(installRoot))
                {
                    Directory.Delete(installRoot, recursive: true);
                }

                throw;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(InstallRoot))
            {
                Directory.Delete(InstallRoot, recursive: true);
            }
        }
    }

    private readonly record struct DispatchResult(int StatusCode, string Body);
    private static RouteConfiguration CreateRoute(
        Guid id,
        string pattern,
        RouteTargetConfiguration target,
        ImmutableArray<string>? hostPatterns = null,
        ImmutableArray<string>? methods = null,
        long routeVersion = 1) =>
        new(
            id,
            enabled: true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                pattern,
                hostPatterns ?? ImmutableArray<string>.Empty,
                methods ?? ImmutableArray<string>.Empty),
            target,
            priority: 0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version: routeVersion);
    private sealed class ThrowingResponseStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Response write deliberately failed.");

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Response write deliberately failed."));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Response write deliberately failed."));
    }
    private sealed class LatestSnapshotReader : IHostConfigurationSnapshotReader
    {
        private readonly HostConfigurationSnapshot snapshot;

        internal LatestSnapshotReader(HostConfigurationSnapshot snapshot) =>
            this.snapshot = snapshot;

        public Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot));
    }

}
