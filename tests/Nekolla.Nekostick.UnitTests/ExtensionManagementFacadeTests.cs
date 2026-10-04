using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionManagementFacadeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("\u0001")]
    public async Task ManagementWriteRejectsInvalidExtensionIdentifiers(string? extensionId)
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var facade = CreateFacade(
            manager,
            CreateSnapshot(),
            new SnapshotHostConfigApi(CreateSnapshot()));
        var result = await facade.EnableAsync(
            extensionId!,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, result.Errors.Single().Code);
    }

    [Fact]
    public async Task ManagementWriteRejectsExtensionIdentifiersLongerThan128Characters()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.DeleteRecordAsync(
            new string('x', 129),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, result.Errors.Single().Code);
    }
    [Fact]
    public async Task MissingPersistenceWriteCapabilityReturnsUnsupported()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot(new ExtensionRecordConfiguration(
            "managed.extension",
            "1.0.0",
            ExtensionLoadState.Disabled,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1));
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.EnableAsync(
            "managed.extension",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, result.Errors.Single().Code);
    }

    [Fact]
    public async Task SnapshotStorageFailureIsReturnedWithoutRemapping()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(
            manager,
            snapshot,
            new SnapshotHostConfigApi(new ConfigurationError(
                ConfigurationErrorCode.StorageUnavailable,
                "The host configuration snapshot store is unavailable.")));

        var result = await facade.ReloadAsync(
            "managed.extension",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.StorageUnavailable, result.Errors.Single().Code);
        Assert.Equal(
            "The host configuration snapshot store is unavailable.",
            result.Errors.Single().Message);
    }


    [Fact]
    public async Task LifecycleCallbackGuardRejectsAllManagementWritesAsUnsupported()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Lifecycle))
        {
            AssertUnsupported(await facade.EnableAsync(
                "managed.extension",
                TestContext.Current.CancellationToken));
            AssertUnsupported(await facade.DisableAsync(
                "managed.extension",
                TestContext.Current.CancellationToken));
            AssertUnsupported(await facade.ReloadAsync(
                "managed.extension",
                TestContext.Current.CancellationToken));
            AssertUnsupported(await facade.DeleteRecordAsync(
                "managed.extension",
                TestContext.Current.CancellationToken));
            var refresh = await facade.RequestRefreshAsync(TestContext.Current.CancellationToken);
            Assert.False(refresh.IsSuccess);
            Assert.Equal(ConfigurationErrorCode.Unsupported, refresh.Errors.Single().Code);
        }
    }

    [Fact]
    public async Task RouteCallbackGuardDoesNotApplyLifecycleManagementVeto()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        ConfigurationWriteResult result;
        using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route))
        {
            result = await facade.ReloadAsync(
                "missing.extension",
                TestContext.Current.CancellationToken);
        }

        Assert.False(result.IsSuccess);
        Assert.NotEqual(ConfigurationErrorCode.Unsupported, result.Errors.Single().Code);
        Assert.Equal(ConfigurationErrorCode.NotFound, result.Errors.Single().Code);
    }
    [Fact]
    public async Task RouteCallbackSelfReloadReturnsUnsupportedBeforePublication()
    {
        var extensionId = "route.self.reload." + Guid.NewGuid().ToString("N");
        using var fixture = TestExtensionDirectory.CreateJson(
            ExtensionManifestTestDefaults.Json.Replace(
                "fixture.extension.deterministic",
                extensionId,
                StringComparison.Ordinal));
        using var installed = InstalledExtensionDirectory.Create(fixture.RootPath);
        var snapshot = CreateSnapshot(new ExtensionRecordConfiguration(
            extensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1));
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var facade = CreateFacade(
            manager,
            snapshot,
            new SnapshotHostConfigApi(snapshot),
            callerExtensionId: extensionId,
            extensionsRootPath: installed.InstallRoot);

        using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route))
        {
            var result = await facade.ReloadAsync(
                extensionId,
                TestContext.Current.CancellationToken);
            Assert.False(result.IsSuccess);
            Assert.Equal(ConfigurationErrorCode.Unsupported, result.Errors.Single().Code);
        }
    }


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("\u0001")]
    public async Task ReloadSoonRejectsInvalidExtensionIdentifiers(string? extensionId)
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var failure = Assert.IsType<ExtensionReloadScheduleFailureResult>(facade.ReloadSoon(extensionId!));
        Assert.Equal(ExtensionReloadScheduleFailureCode.InvalidArgument, failure.Code);
        Assert.False(string.IsNullOrWhiteSpace(failure.Detail.Message));
    }

    [Fact]
    public async Task ReloadSoonRejectsWhenPublisherIsUnavailable()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var failure = Assert.IsType<ExtensionReloadScheduleFailureResult>(facade.ReloadSoon("valid.extension"));
        Assert.Equal(ExtensionReloadScheduleFailureCode.Unsupported, failure.Code);
        Assert.Contains("publisher", failure.Detail.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot reader", failure.Detail.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ReloadSoonRejectsWhenSnapshotReaderIsUnavailable()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        await using var publisher = CreatePublisher(manager);
        var facade = CreateFacade(
            manager,
            snapshot,
            new SnapshotHostConfigApi(snapshot),
            publisher: publisher);

        var failure = Assert.IsType<ExtensionReloadScheduleFailureResult>(facade.ReloadSoon("valid.extension"));
        Assert.Equal(ExtensionReloadScheduleFailureCode.Unsupported, failure.Code);
        Assert.Contains("snapshot reader", failure.Detail.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task ReloadSoonRejectsWhenConfigurationWritesAreDisallowed()
    {
        var snapshot = CreateSnapshot();
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostConfigurationPublisher>.Instance);
        var reader = new SnapshotReader(snapshot);
        var services = new SingleServiceProvider(new SnapshotHostConfigApi(snapshot), publisher, reader);
        var facade = new ExtensionManagementFacade(
            "caller.extension",
            new SingleScopeFactory(services),
            runtimeState,
            manager,
            services);

        var failure = Assert.IsType<ExtensionReloadScheduleFailureResult>(facade.ReloadSoon("valid.extension"));
        Assert.Equal(ExtensionReloadScheduleFailureCode.WritesDisallowed, failure.Code);
        Assert.Contains("valid.extension", failure.Detail.Message, StringComparison.Ordinal);
        Assert.Contains("configuration writes", failure.Detail.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReloadSoonAcceptsValidIdentifierWhenPublisherAndReaderAreAvailable()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        await using var publisher = CreatePublisher(manager);
        var reader = new SnapshotReader(snapshot);
        var facade = CreateFacade(
            manager,
            snapshot,
            new SnapshotHostConfigApi(snapshot),
            publisher: publisher,
            snapshotReader: reader);

        Assert.Same(ExtensionReloadScheduleResult.Success, facade.ReloadSoon("valid.extension"));
        await reader.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task ReloadSoonIsNotVetoedByRouteCallbackGuard() =>
        ReloadSoonIsNotVetoedByCallbackGuard(ExtensionCallbackKind.Route);

    [Fact]
    public Task ReloadSoonIsNotVetoedByLifecycleCallbackGuard() =>
        ReloadSoonIsNotVetoedByCallbackGuard(ExtensionCallbackKind.Lifecycle);

    private static async Task ReloadSoonIsNotVetoedByCallbackGuard(ExtensionCallbackKind callbackKind)
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        await using var publisher = CreatePublisher(manager);
        var reader = new SnapshotReader(snapshot);
        var facade = CreateFacade(
            manager,
            snapshot,
            new SnapshotHostConfigApi(snapshot),
            publisher: publisher,
            snapshotReader: reader);

        using (ExtensionCallbackGuard.Enter(callbackKind))
        {
            Assert.Same(ExtensionReloadScheduleResult.Success, facade.ReloadSoon("valid.extension"));
        }

        await reader.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReloadSoonEventuallyReplacesTheTargetGeneration()
    {
        var extensionId = "reload.soon." + Guid.NewGuid().ToString("N");
        using var fixture = TestExtensionDirectory.CreateJson(
            ExtensionManifestTestDefaults.Json.Replace(
                "fixture.extension.deterministic",
                extensionId,
                StringComparison.Ordinal));
        using var installed = InstalledExtensionDirectory.Create(fixture.RootPath);
        var discovered = ExtensionManifestDiscovery.Discover(fixture.RootPath);
        Assert.True(discovered.Succeeded, discovered.FailureCode.ToString());
        var settings = new ExtensionSettingsConfiguration(
            extensionId,
            schemaVersion: 1,
            settingsJson: JsonSerializer.Serialize(new { label = "reload-soon", handlerId = extensionId }),
            version: 1);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                discovered.Manifest!,
                settings,
                [extensionId],
                true)),
            previous: null,
            cancellationToken: cancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        Assert.NotNull(prepared.Preparation);
        var preparation = prepared.Preparation!;
        var ready = await preparation.ReadyToPublishAsync(cancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.NotNull(ready.Generation);
        var oldGeneration = ready.Generation!;
        Assert.True(await preparation.CompletePublicationAsync());

        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                extensionId,
                "1.0.0",
                ExtensionLoadState.Loaded,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1)),
            ImmutableArray.Create(settings));
        var holder = new HostConfigurationSnapshotHolder();
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(snapshot, oldGeneration));
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(
                skipExtensions: false,
                disableSupervisor: false,
                readOnly: true,
                extensionsRootPath: installed.InstallRoot),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostConfigurationPublisher>.Instance);
        var reader = new SnapshotReader(snapshot);
        var services = new SingleServiceProvider(new SnapshotHostConfigApi(snapshot), publisher, reader);
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(
                skipExtensions: false,
                disableSupervisor: false,
                readOnly: false,
                extensionsRootPath: installed.InstallRoot));
        runtimeState.MarkSnapshotAccepted();
        var facade = new ExtensionManagementFacade(
            extensionId,
            new SingleScopeFactory(services),
            runtimeState,
            manager,
            services);

        Assert.Same(ExtensionReloadScheduleResult.Success, facade.ReloadSoon(extensionId));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (ReferenceEquals(holder.RoutingSnapshot?.DispatchGeneration, oldGeneration) &&
            DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        Assert.NotSame(oldGeneration, holder.RoutingSnapshot?.DispatchGeneration);
    }

    [Fact]
    public async Task PublishTriggerTreatsSupersededAsAcceptedAndLogsGenuineRejection()
    {
        // Version 0 is semantically invalid, so the committed snapshot is version 2 and
        // the losing-race candidate is version 1.
        var committed = new HostConfigurationSnapshot(
            2,
            new GlobalSettingsConfiguration(version: 2),
            default,
            default,
            default,
            default);
        var older = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            default,
            default);
        var invalid = new HostConfigurationSnapshot(
            2,
            new GlobalSettingsConfiguration(
                version: 2,
                trustedProxyCidrs: ImmutableArray.Create("192.0.2.0/33")),
            default,
            default,
            default,
            default);
        await using var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(committed));
        var nodeOptions = new HostNodeOptions(skipExtensions: true, disableSupervisor: false, readOnly: false);
        var runtimeState = new HostRuntimeState(holder, nodeOptions);
        runtimeState.MarkSnapshotAccepted();
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            nodeOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostConfigurationPublisher>.Instance,
            runtimeState: runtimeState);
        var logger = new RecordingLogger();
        var staleServices = new SingleServiceProvider(
            new SnapshotHostConfigApi(committed),
            publisher,
            new SnapshotReader(older));
        var staleFacade = new ExtensionManagementFacade(
            "caller.extension",
            new SingleScopeFactory(staleServices),
            runtimeState,
            manager,
            staleServices,
            logger);

        await InvokePublishTriggerAsync(staleFacade, TestContext.Current.CancellationToken);

        Assert.True(runtimeState.ExtensionConfigurationWritesAllowed);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.EventId.Id == HostEventIds.ConfigurationSnapshotRejected.Id);

        var invalidServices = new SingleServiceProvider(
            new SnapshotHostConfigApi(committed),
            publisher,
            new SnapshotReader(invalid));
        var invalidFacade = new ExtensionManagementFacade(
            "caller.extension",
            new SingleScopeFactory(invalidServices),
            runtimeState,
            manager,
            invalidServices,
            logger);

        await InvokePublishTriggerAsync(invalidFacade, TestContext.Current.CancellationToken);

        Assert.False(runtimeState.ExtensionConfigurationWritesAllowed);
        Assert.Contains(
            logger.Entries,
            entry => entry.EventId.Id == HostEventIds.ConfigurationSnapshotRejected.Id &&
                entry.Message.Contains("Reason: PublishFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadOfMissingRecordReturnsNotFound()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot();
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.ReloadAsync(
            "missing.extension",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.NotFound, result.Errors.Single().Code);
    }

    [Fact]
    public async Task ReloadOfDisabledRecordReturnsValidation()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var snapshot = CreateSnapshot(new ExtensionRecordConfiguration(
            "managed.extension",
            "1.0.0",
            ExtensionLoadState.Disabled,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1));
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.ReloadAsync(
            "managed.extension",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, result.Errors.Single().Code);
    }

    [Fact]
    public async Task ListReportsRunningStatusAndNullManifestVersionWhenScanIsAbsent()
    {
        var extensionId = "list.extension." + Guid.NewGuid().ToString("N");
        var manifestJson = ExtensionManifestTestDefaults.Json.Replace(
            "fixture.extension.deterministic",
            extensionId,
            StringComparison.Ordinal);
        using var fixture = TestExtensionDirectory.CreateJson(manifestJson);
        var discovered = ExtensionManifestDiscovery.Discover(fixture.RootPath);
        Assert.True(discovered.Succeeded, discovered.FailureCode.ToString());

        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var loaded = await manager.LoadAsync(
            discovered.Manifest!,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(loaded.Succeeded, loaded.FailureCode.ToString());

        var snapshot = CreateSnapshot(new ExtensionRecordConfiguration(
            extensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1));
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
        var entry = Assert.Single(result.Value!);
        Assert.Equal(extensionId, entry.ExtensionId);
        Assert.True(entry.IsRunning);
        Assert.Null(entry.ManifestVersion);
    }

    [Fact]
    public async Task ListSurfacesExtensionReportedStatus()
    {
        var extensionId = "list.reported." + Guid.NewGuid().ToString("N");
        var manifestJson = ExtensionManifestTestDefaults.Json.Replace(
            "fixture.extension.deterministic",
            extensionId,
            StringComparison.Ordinal);
        using var fixture = TestExtensionDirectory.CreateJson(manifestJson);
        var discovered = ExtensionManifestDiscovery.Discover(fixture.RootPath);
        Assert.True(discovered.Succeeded, discovered.FailureCode.ToString());

        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var settings = new ExtensionSettingsConfiguration(
            extensionId,
            schemaVersion: 1,
            settingsJson: JsonSerializer.Serialize(new { reportStatus = "settings-unavailable" }),
            version: 1);
        var loaded = await manager.LoadAsync(
            discovered.Manifest!,
            settings,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(loaded.Succeeded, loaded.FailureCode.ToString());

        var snapshot = CreateSnapshot(new ExtensionRecordConfiguration(
            extensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1));
        var facade = CreateFacade(manager, snapshot, new SnapshotHostConfigApi(snapshot));

        var result = await facade.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
        var entry = Assert.Single(result.Value!);
        Assert.Equal(ExtensionStatusKind.Degraded, entry.ReportedStatusKind);
        Assert.Equal("settings-unavailable", entry.ReportedStatusCode);
    }

    private static ExtensionManagementFacade CreateFacade(
        ExtensionRuntimeManager manager,
        HostConfigurationSnapshot snapshot,
        IHostConfigApi hostConfig,
        string callerExtensionId = "caller.extension",
        HostConfigurationPublisher? publisher = null,
        IHostConfigurationSnapshotReader? snapshotReader = null,
        string? extensionsRootPath = null)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(
                skipExtensions: false,
                disableSupervisor: false,
                readOnly: false,
                extensionsRootPath: extensionsRootPath));
        runtimeState.MarkSnapshotAccepted();
        var services = new SingleServiceProvider(hostConfig, publisher, snapshotReader);
        return new ExtensionManagementFacade(
            callerExtensionId,
            new SingleScopeFactory(services),
            runtimeState,
            manager,
            services);
    }

    private static HostConfigurationPublisher CreatePublisher(ExtensionRuntimeManager manager)
    {
        var holder = new HostConfigurationSnapshotHolder();
        return new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostConfigurationPublisher>.Instance);
    }

    private sealed class SnapshotReader : IHostConfigurationSnapshotReader
    {
        private readonly HostConfigurationSnapshot snapshot;

        internal SnapshotReader(HostConfigurationSnapshot snapshot) =>
            this.snapshot = snapshot;

        internal TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadStarted.TrySetResult(true);
            return Task.FromResult(ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot));
        }
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        params ExtensionRecordConfiguration[] records) =>
        new(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            records.ToImmutableArray(),
            default);

    private static void AssertUnsupported(ConfigurationWriteResult result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, result.Errors.Single().Code);
    }

    private sealed class InstalledExtensionDirectory : IDisposable
    {
        private InstalledExtensionDirectory(string installRoot) => InstallRoot = installRoot;

        internal string InstallRoot { get; }

        internal static InstalledExtensionDirectory Create(string sourceRoot)
        {
            var installRoot = Path.Combine(
                Path.GetTempPath(),
                "nekostick-facade-" + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(installRoot, "fixture");
            Directory.CreateDirectory(directory);
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var targetPath = Path.Combine(directory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath);
            }

            return new InstalledExtensionDirectory(installRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(InstallRoot))
            {
                Directory.Delete(InstallRoot, recursive: true);
            }
        }
    }

    private sealed class SnapshotHostConfigApi : IHostConfigApi
    {
        private readonly ConfigurationReadResult<HostConfigurationSnapshot> snapshotResult;

        internal SnapshotHostConfigApi(HostConfigurationSnapshot snapshot) =>
            snapshotResult = ConfigurationReadResult<HostConfigurationSnapshot>.Success(snapshot);

        internal SnapshotHostConfigApi(ConfigurationError error) =>
            snapshotResult = ConfigurationReadResult<HostConfigurationSnapshot>.Failure(error);

        public HostApiVersion ApiVersion => HostApiVersion.Current;

        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshotResult);

        public ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(UnsupportedWrite());

        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.NotFound,
                    $"Extension settings for '{extensionId}' were not found.")));

        public ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
            string extensionId,
            long expectedVersion,
            ExtensionSettingsConfiguration settings,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(UnsupportedWrite());

        private static ConfigurationWriteResult UnsupportedWrite() =>
            ConfigurationWriteResult.Failure(new ConfigurationError(
                ConfigurationErrorCode.Unsupported,
                "The test host configuration API does not support writes."));
    }

    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider serviceProvider;

        internal SingleScopeFactory(IServiceProvider serviceProvider) =>
            this.serviceProvider = serviceProvider;

        public IServiceScope CreateScope() => new SingleScope(serviceProvider);
    }

    private sealed class SingleScope : IServiceScope
    {
        internal SingleScope(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
        }
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IHostConfigApi hostConfig;
        private readonly HostConfigurationPublisher? publisher;
        private readonly IHostConfigurationSnapshotReader? snapshotReader;

        internal SingleServiceProvider(
            IHostConfigApi hostConfig,
            HostConfigurationPublisher? publisher = null,
            IHostConfigurationSnapshotReader? snapshotReader = null)
        {
            this.hostConfig = hostConfig;
            this.publisher = publisher;
            this.snapshotReader = snapshotReader;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IHostConfigApi) ? hostConfig :
            serviceType == typeof(HostConfigurationPublisher) ? publisher :
            serviceType == typeof(IHostConfigurationSnapshotReader) ? snapshotReader :
            null;
    }
    private static async Task InvokePublishTriggerAsync(
        ExtensionManagementFacade facade,
        CancellationToken cancellationToken)
    {
        var method = typeof(ExtensionManagementFacade).GetMethod(
            "TriggerPublishAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(CancellationToken) },
            modifiers: null) ?? throw new InvalidOperationException("The facade publication trigger is unavailable.");
        var invocation = method.Invoke(facade, new object?[] { cancellationToken })
            ?? throw new InvalidOperationException("The facade publication trigger returned no value.");
        await (ValueTask)invocation;
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<(EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId, formatter(state, exception)));
    }

}
