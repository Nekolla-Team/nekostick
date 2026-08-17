using System.Collections.Immutable;
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

        Assert.True(await publisher.PublishAsync(snapshot, TestContext.Current.CancellationToken));

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
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifest,
                    newSettings,
                    [FixtureExtensionId])),
            oldGeneration,
            TestContext.Current.CancellationToken);
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

        var followUp = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifest,
                    newSettings,
                    [FixtureExtensionId])),
            ready.Generation,
            TestContext.Current.CancellationToken);
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

        Assert.True(await publisher.PublishAsync(replacement, TestContext.Current.CancellationToken));
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

        Assert.True(await publisher.PublishAsync(snapshot, TestContext.Current.CancellationToken));

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
        Assert.True(await publisher.PublishAsync(snapshot, TestContext.Current.CancellationToken));

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
            Settings(FixtureExtensionId, "failed", handlerFails: true),
            previous: null);
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

    private static async Task<ExtensionDispatchGeneration> PrepareAndPublishGenerationAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest? manifest,
        ExtensionSettingsConfiguration? settings,
        ExtensionDispatchGeneration? previous,
        bool includeFallback = false)
    {
        var desired = manifest is null
            ? ImmutableArray<ExtensionRuntimeDescriptor>.Empty
            : ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifest,
                    settings,
                    [FixtureExtensionId],
                    includeFallback));
        var prepared = await manager.PrepareGenerationAsync(
            desired,
            previous,
            TestContext.Current.CancellationToken);
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
        string path)
    {
        var context = CreateContext(path);
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

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("integration.test");
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
        bool registerFallback = false,
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
                registerFallback
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

    private static RouteConfiguration CreateRoute(
        Guid id,
        string pattern,
        RouteTargetConfiguration target) =>
        new(
            id,
            enabled: true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                pattern,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target,
            priority: 0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version: 1);

    private readonly record struct DispatchResult(int StatusCode, string Body);

    private sealed class ThrowingResponseStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() => throw new IOException("response write failure");
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("response write failure"));
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("response write failure");
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("response write failure"));
    }
}
