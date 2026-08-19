using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationStateTests
{
    private static readonly Guid RouteId =
        Guid.Parse("018f3a52-4cde-7abc-8def-0123456789ab");

    [Fact]
    public void SemanticValidatorRejectsInvalidPersistedTrustedProxyCidr()
    {
        var snapshot = CreateSnapshot(
            globalSettings: new GlobalSettingsConfiguration(
                version: 1,
                trustedProxyCidrs: ImmutableArray.Create("192.0.2.0/33")));

        Assert.True(HostConfigurationSnapshotValidator.IsComplete(snapshot));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    [Fact]
    public void SemanticValidatorRejectsInvalidPersistedRouteMatcher()
    {
        var snapshot = CreateSnapshot(
            routes: ImmutableArray.Create(
                CreateRoute(
                    new RouteMatcherConfiguration(
                        RouteMatcherType.Exact,
                        "/api*",
                        default,
                        default),
                    new ForwardingConfiguration(ForwardingMode.Preserve, null))));

        Assert.True(HostConfigurationSnapshotValidator.IsComplete(snapshot));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    [Fact]
    public void SemanticValidatorRejectsInvalidPersistedRouteForwarding()
    {
        var snapshot = CreateSnapshot(
            routes: ImmutableArray.Create(
                CreateRoute(
                    new RouteMatcherConfiguration(
                        RouteMatcherType.Prefix,
                        "/api*",
                        default,
                        default),
                    new ForwardingConfiguration(ForwardingMode.Strip, null))));

        Assert.True(HostConfigurationSnapshotValidator.IsComplete(snapshot));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    [Fact]
    public void SemanticValidatorRejectsInvalidPersistedExtensionSemVer()
    {
        var snapshot = CreateSnapshot(
            extensionRecords: ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    "sample.extension",
                    "1.0",
                    ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    1)));

        Assert.True(HostConfigurationSnapshotValidator.IsComplete(snapshot));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
    }

    [Fact]
    public void SemanticValidatorRequiresRouteResourceOverridesToTightenGlobalLimits()
    {
        var globalSettings = new GlobalSettingsConfiguration(
            version: 1,
            maxRequestBodyBytes: 1024 * 1024,
            maxRequestHeaderBytes: 4096,
            maxConcurrentRequests: 4,
            requestReadTimeout: TimeSpan.FromSeconds(2));
        var matcher = new RouteMatcherConfiguration(RouteMatcherType.Exact, "/resource", default, default);
        var forwarding = new ForwardingConfiguration(ForwardingMode.Preserve, null);
        var inherited = CreateSnapshot(
            globalSettings: globalSettings,
            routes: ImmutableArray.Create(CreateRoute(matcher, forwarding)));
        var tightened = CreateSnapshot(
            globalSettings: globalSettings,
            routes: ImmutableArray.Create(CreateRoute(
                matcher,
                forwarding,
                maxRequestBodyBytes: 512 * 1024,
                maxRequestHeaderBytes: 2048,
                maxConcurrentRequests: 2,
                requestReadTimeout: TimeSpan.FromSeconds(1))));
        var invalidOverrides = new[]
        {
            CreateRoute(matcher, forwarding, maxRequestBodyBytes: 1024 * 1024 + 1),
            CreateRoute(matcher, forwarding, maxRequestHeaderBytes: 4097),
            CreateRoute(matcher, forwarding, maxConcurrentRequests: 5),
            CreateRoute(matcher, forwarding, requestReadTimeout: TimeSpan.FromSeconds(3))
        };

        Assert.True(HostConfigurationSemanticValidator.TryValidateSnapshot(inherited));
        Assert.True(HostConfigurationSemanticValidator.TryValidateSnapshot(tightened));
        foreach (var route in invalidOverrides)
        {
            var snapshot = CreateSnapshot(
                globalSettings: globalSettings,
                routes: ImmutableArray.Create(route));

            Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot));
        }
    }

    [Fact]
    public void HolderDoesNotPublishSemanticallyInvalidCandidateAndPreservesPriorSnapshot()
    {
        var prior = CreateSnapshot(4);
        var invalidCandidate = CreateSnapshot(
            5,
            new GlobalSettingsConfiguration(
                version: 5,
                trustedProxyCidrs: ImmutableArray.Create("192.0.2.0/33")));
        var holder = new HostConfigurationSnapshotHolder();

        Assert.True(holder.TryReplace(prior));
        Assert.True(HostConfigurationSnapshotValidator.IsComplete(invalidCandidate));
        Assert.False(HostConfigurationSemanticValidator.TryValidateSnapshot(invalidCandidate));
        Assert.False(holder.TryReplace(invalidCandidate));

        Assert.Same(prior, holder.Current);
        Assert.Equal(4L, holder.Current!.Version);
    }

    [Fact]
    public async Task RefreshKeepsPriorSnapshotReadableAndClosesCapabilitiesWhenStorageIsUnavailable()
    {
        var prior = CreateSnapshot(4);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(prior));

        var nodeOptions = new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false);
        var runtimeState = new HostRuntimeState(holder, nodeOptions);
        runtimeState.MarkSnapshotAccepted();

        var revisionReader = new UnavailableRevisionReader();
        var runtimeManager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var publisher = new HostConfigurationPublisher(
            holder,
            runtimeManager,
            nodeOptions,
            NullLogger<HostConfigurationPublisher>.Instance);
        var service = new HostConfigurationRefreshService(
            holder,
            new UnusedSnapshotReader(),
            new OneShotConfigurationChangeSignal(),
            runtimeState,
            new RevisionScopeFactory(revisionReader),
            new HostRuntimeOptions("synthetic-storage", "test-node", readOnly: false),
            publisher,
            NullLogger<HostConfigurationRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await revisionReader.Observed.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            var status = runtimeState.Status;
            Assert.Same(prior, holder.Current);
            Assert.True(status.SnapshotAvailable);
            Assert.False(status.DatabaseAvailable);
            Assert.False(status.ConfigurationValid);
            Assert.False(status.ConfigurationWritesAllowed);
            Assert.False(status.NewLeasesAllowed);
            Assert.False(status.NewServicesAllowed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await publisher.DisposeAsync();
        }
    }

    [Fact]
    public void RuntimeWithoutSnapshotRemainsFailClosedWhenStorageIsUnavailable()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));

        runtimeState.MarkDatabaseUnavailable();

        var status = runtimeState.Status;
        Assert.Null(holder.Current);
        Assert.False(status.SnapshotAvailable);
        Assert.False(status.DatabaseAvailable);
        Assert.False(status.ConfigurationValid);
        Assert.False(status.ConfigurationWritesAllowed);
        Assert.False(status.NewLeasesAllowed);
        Assert.False(status.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Unready, status.Readiness);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        long version = 1,
        GlobalSettingsConfiguration? globalSettings = null,
        ImmutableArray<RouteConfiguration> routes = default,
        ImmutableArray<ExtensionRecordConfiguration> extensionRecords = default) =>
        new(
            version,
            globalSettings ?? new GlobalSettingsConfiguration(version: version),
            routes,
            default,
            extensionRecords,
            default);

    private static RouteConfiguration CreateRoute(
        RouteMatcherConfiguration matcher,
        ForwardingConfiguration forwarding,
        long? maxRequestBodyBytes = null,
        long? maxRequestHeaderBytes = null,
        int? maxConcurrentRequests = null,
        TimeSpan? requestReadTimeout = null) =>
        new(
            RouteId,
            true,
            matcher,
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            0,
            forwarding,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            maxRequestBodyBytes: maxRequestBodyBytes,
            maxRequestHeaderBytes: maxRequestHeaderBytes,
            maxConcurrentRequests: maxConcurrentRequests,
            requestReadTimeout: requestReadTimeout);

    private sealed class UnavailableRevisionReader : IConfigurationRevisionReader
    {
        public TaskCompletionSource<bool> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ConfigurationReadResult<ConfigurationRevisionStatus>> ReadCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            Observed.TrySetResult(true);
            return Task.FromResult(
                ConfigurationReadResult<ConfigurationRevisionStatus>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.StorageUnavailable)));
        }
    }

    private sealed class UnusedSnapshotReader : IHostConfigurationSnapshotReader
    {
        public Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.StorageUnavailable)));
    }

    private sealed class OneShotConfigurationChangeSignal : IConfigurationChangeSignal
    {
        private int _firstHint = 1;

        public Task WaitForHintAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _firstHint, 0) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RevisionScopeFactory : IServiceScopeFactory
    {
        private readonly IConfigurationRevisionReader _revisionReader;

        public RevisionScopeFactory(IConfigurationRevisionReader revisionReader) =>
            _revisionReader = revisionReader;

        public IServiceScope CreateScope() => new RevisionScope(_revisionReader);
    }

    private sealed class RevisionScope : IServiceScope
    {
        public RevisionScope(IConfigurationRevisionReader revisionReader) =>
            ServiceProvider = new RevisionServiceProvider(revisionReader);

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
        }
    }

    private sealed class RevisionServiceProvider : IServiceProvider
    {
        private readonly IConfigurationRevisionReader _revisionReader;

        public RevisionServiceProvider(IConfigurationRevisionReader revisionReader) =>
            _revisionReader = revisionReader;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IConfigurationRevisionReader) ? _revisionReader : null;
    }
}
