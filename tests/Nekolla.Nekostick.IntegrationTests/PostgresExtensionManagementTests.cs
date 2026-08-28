using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using ContractExtensionLoadState = Nekolla.Nekostick.Contracts.ExtensionLoadState;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises PostgreSQL extension management persistence transitions.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresExtensionManagementTests
{

    [Fact]
    public async Task DisabledStateCanBeWrittenAgainWithoutChangingItsMeaning()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                "management.noop.extension",
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);

        var noOp = await test.Api.SetExtensionLoadStateAsync(
            "management.noop.extension",
            expectedRecordVersion: 1,
            state: ContractExtensionLoadState.Disabled,
            cancellationToken: cancellationToken);
        Assert.True(noOp.IsSuccess, noOp.Errors.FirstOrDefault()?.Message);

        var snapshot = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(snapshot.IsSuccess, snapshot.Errors.FirstOrDefault()?.Message);
        var record = Assert.Single(snapshot.Value!.ExtensionRecords);
        Assert.Equal(ContractExtensionLoadState.Disabled, record.LoadState);
        Assert.Equal(2L, record.RecordVersion);
    }
    [Fact]
    public async Task SetExtensionLoadStateEnforcesWhitelistAndOptimisticRecordVersions()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var now = DateTimeOffset.UtcNow;
        var bootstrap = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                "management.state.extension",
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);

        var disabledToLoaded = await test.Api.SetExtensionLoadStateAsync(
            "management.state.extension",
            expectedRecordVersion: 1,
            state: ContractExtensionLoadState.Loaded,
            cancellationToken: cancellationToken);
        Assert.True(disabledToLoaded.IsSuccess, disabledToLoaded.Errors.FirstOrDefault()?.Message);

        var loadedToStopped = await test.Api.SetExtensionLoadStateAsync(
            "management.state.extension",
            expectedRecordVersion: 2,
            state: ContractExtensionLoadState.Stopped,
            cancellationToken: cancellationToken);
        Assert.False(loadedToStopped.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, loadedToStopped.Errors.Single().Code);

        var loadedToDisabled = await test.Api.SetExtensionLoadStateAsync(
            "management.state.extension",
            expectedRecordVersion: 2,
            state: ContractExtensionLoadState.Disabled,
            cancellationToken: cancellationToken);
        Assert.True(loadedToDisabled.IsSuccess, loadedToDisabled.Errors.FirstOrDefault()?.Message);

        var disabledToLoadedAgain = await test.Api.SetExtensionLoadStateAsync(
            "management.state.extension",
            expectedRecordVersion: 3,
            state: ContractExtensionLoadState.Loaded,
            cancellationToken: cancellationToken);
        Assert.True(disabledToLoadedAgain.IsSuccess, disabledToLoadedAgain.Errors.FirstOrDefault()?.Message);

        await using var failedStateContext = test.Database.CreateContext();
        await using var failedStateApi = new EfHostConfigApi(failedStateContext);
        await test.Database.ExecuteSchemaCommandAsync(
            $"UPDATE {test.Database.QualifiedRelation("extension_records")} " +
            "SET load_state = @state WHERE extension_id = @extension_id;",
            new NpgsqlParameter("state", NpgsqlDbType.Text) { Value = "Failed" },
            new NpgsqlParameter("extension_id", NpgsqlDbType.Text) { Value = "management.state.extension" });
        var failedToLoaded = await failedStateApi.SetExtensionLoadStateAsync(
            "management.state.extension",
            expectedRecordVersion: 4,
            state: ContractExtensionLoadState.Loaded,
            cancellationToken: cancellationToken);
        Assert.True(failedToLoaded.IsSuccess, failedToLoaded.Errors.FirstOrDefault()?.Message);

        var final = await failedStateApi.ReadSnapshotAsync(cancellationToken);
        Assert.True(final.IsSuccess, final.Errors.FirstOrDefault()?.Message);
        var record = Assert.Single(final.Value!.ExtensionRecords);
        Assert.Equal(ContractExtensionLoadState.Loaded, record.LoadState);
        Assert.Equal(5L, record.RecordVersion);
    }

    [Fact]
    public async Task UpdateExtensionInstalledVersionValidatesSemVerAndClassifiesRecordConflicts()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var now = DateTimeOffset.UtcNow;
        var bootstrap = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                "management.version.extension",
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);

        var invalidVersion = await test.Api.UpdateExtensionInstalledVersionAsync(
            "management.version.extension",
            expectedRecordVersion: 1,
            newVersion: "1.0",
            cancellationToken: cancellationToken);
        Assert.False(invalidVersion.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, invalidVersion.Errors.Single().Code);

        var stale = await test.Api.UpdateExtensionInstalledVersionAsync(
            "management.version.extension",
            expectedRecordVersion: 0,
            newVersion: "1.1.0",
            cancellationToken: cancellationToken);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, stale.Errors.Single().Code);

        var updated = await test.Api.UpdateExtensionInstalledVersionAsync(
            "management.version.extension",
            expectedRecordVersion: 1,
            newVersion: "1.2.3",
            cancellationToken: cancellationToken);
        Assert.True(updated.IsSuccess, updated.Errors.FirstOrDefault()?.Message);
        Assert.Equal(3L, updated.NewVersion);

        var snapshot = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(snapshot.IsSuccess, snapshot.Errors.FirstOrDefault()?.Message);
        var record = Assert.Single(snapshot.Value!.ExtensionRecords);
        Assert.Equal("1.2.3", record.Version);
        Assert.Equal(2L, record.RecordVersion);
    }

    [Fact]
    public async Task DeleteExtensionRecordCascadeRemovesOwnedRowsAndBumpsRevision()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        const string extensionId = "management.delete.extension";
        var now = DateTimeOffset.UtcNow;
        var bootstrap = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                extensionId,
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);

        var serviceId = Guid.CreateVersion7();
        var routeId = Guid.CreateVersion7();
        var owned = new EfExtensionOwnedConfigurationApi(test.Api);
        var ownedSnapshot = await owned.ReadOwnedAsync(extensionId, cancellationToken);
        Assert.True(ownedSnapshot.IsSuccess, ownedSnapshot.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(ownedSnapshot.Value);
        var service = new ExtensionServiceConfiguration(
            serviceId,
            enabled: true,
            fileName: "/usr/bin/management-delete-service",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ServiceRestartPolicy.Never,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: now,
            updatedAt: now,
            version: 0);
        var route = new ExtensionRouteConfiguration(
            routeId,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/management-delete",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionServiceRouteTarget(serviceId),
            priority: 1);
        var apply = await owned.ApplyOwnedAsync(
            extensionId,
            ownedSnapshot.Value!.Version,
            new ExtensionConfigurationChangeSet(
                ImmutableArray.Create(route),
                ImmutableArray<Guid>.Empty,
                ImmutableArray.Create(service),
                ImmutableArray<Guid>.Empty,
                new ExtensionSettingsConfiguration(extensionId, 1, "{}", 0)),
            cancellationToken: cancellationToken);
        Assert.True(apply.IsSuccess, apply.Errors.FirstOrDefault()?.Message);

        var nodeId = "management-delete-node";
        var nodeUuid = Guid.CreateVersion7();
        await using (var context = test.Database.CreateContext())
        {
            context.Nodes.Add(new Node
            {
                Id = nodeUuid,
                NodeId = nodeId,
                LastHeartbeatAt = now,
                LastConfigurationVersion = apply.NewVersion!.Value,
                RuntimeState = "running",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            });
            context.ServiceRuntimes.Add(new ServiceRuntime
            {
                NodeId = nodeId,
                ServiceId = serviceId,
                Lifecycle = Nekolla.Nekostick.Domain.ServiceLifecycleState.Running,
                Health = Nekolla.Nekostick.Domain.ServiceHealthState.Healthy,
                RestartCount = 0,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            });
            context.PortLeases.Add(new PortLease
            {
                Id = Guid.CreateVersion7(),
                NodeId = nodeId,
                Port = 35123,
                ServiceId = serviceId,
                LeaseExpiresAt = now.AddMinutes(5),
                RenewedAt = now,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        var beforeDelete = await test.Api.ReadSnapshotAsync(cancellationToken);
        Assert.True(beforeDelete.IsSuccess, beforeDelete.Errors.FirstOrDefault()?.Message);
        var persistedRecord = Assert.Single(beforeDelete.Value!.ExtensionRecords);
        var stale = await test.Api.DeleteExtensionRecordCascadeAsync(
            extensionId,
            expectedRecordVersion: persistedRecord.RecordVersion - 1,
            cancellationToken: cancellationToken);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, stale.Errors.Single().Code);

        var deleted = await test.Api.DeleteExtensionRecordCascadeAsync(
            extensionId,
            persistedRecord.RecordVersion,
            cancellationToken);
        Assert.True(deleted.IsSuccess, deleted.Errors.FirstOrDefault()?.Message);
        Assert.Equal(beforeDelete.Value.Version + 1, deleted.NewVersion);

        await using var verify = test.Database.CreateContext();
        Assert.Equal(0, await verify.ExtensionRecords.CountAsync(
            value => value.ExtensionId == extensionId,
            cancellationToken));
        Assert.Equal(0, await verify.ExtensionSettings.CountAsync(cancellationToken));
        Assert.Equal(0, await verify.Services.CountAsync(
            value => value.OwnerExtensionId == extensionId,
            cancellationToken));
        Assert.Equal(0, await verify.ServiceRuntimes.CountAsync(
            value => value.ServiceId == serviceId,
            cancellationToken));
        Assert.Equal(0, await verify.PortLeases.CountAsync(
            value => value.ServiceId == serviceId,
            cancellationToken));
        Assert.Equal(1, await verify.Nodes.CountAsync(
            value => value.NodeId == nodeId,
            cancellationToken));
    }

    [Fact]
    public async Task FacadeEnableAndDisableRoundTripPersistsStateAndVersions()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        using var fixture = InstalledFixtureDirectory.Create(
            "management.facade.roundtrip." + Guid.NewGuid().ToString("N"),
            "1.0.0");
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await ReadSnapshotAsync(test.Database, cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        var now = DateTimeOffset.UtcNow;
        var seeded = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                fixture.ExtensionId,
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(seeded.IsSuccess, seeded.Errors.FirstOrDefault()?.Message);

        var snapshot = await ReadSnapshotAsync(test.Database, cancellationToken);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var (facade, provider) = CreateFacade(test.Database, snapshot.Value!, manager, fixture.ExtensionId + ".caller");
        await using (provider)
        {
            var enabled = await facade.EnableAsync(fixture.ExtensionId, cancellationToken);
            Assert.True(enabled.IsSuccess, enabled.Errors.FirstOrDefault()?.Message);
            var loaded = await ReadSnapshotAsync(test.Database, cancellationToken);
            Assert.Equal(ContractExtensionLoadState.Loaded, Assert.Single(loaded.Value!.ExtensionRecords).LoadState);

            var disabled = await facade.DisableAsync(fixture.ExtensionId, cancellationToken);
            Assert.True(disabled.IsSuccess, disabled.Errors.FirstOrDefault()?.Message);
            var final = await ReadSnapshotAsync(test.Database, cancellationToken);
            var record = Assert.Single(final.Value!.ExtensionRecords);
            Assert.Equal(ContractExtensionLoadState.Disabled, record.LoadState);
            Assert.Equal(3L, record.RecordVersion);
        }
    }
    [Fact]
    public async Task WritableRecordlessPublisherPersistsAndLoadsDiscoveredExtensionAsLoaded()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        using var fixture = InstalledFixtureDirectory.Create(
            "management.publisher.first-run." + Guid.NewGuid().ToString("N"),
            "1.0.0");
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await ReadSnapshotAsync(test.Database, cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.Empty(initial.Value!.ExtensionRecords);

        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var holder = new HostConfigurationSnapshotHolder();
        var factory = new TestDbContextFactory(test.Database);
        await using var publisher = new HostConfigurationPublisher(
            holder,
            manager,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance,
            factory);

        Assert.True(
            await publisher.PublishAsync(initial.Value, cancellationToken: cancellationToken));

        var status = manager.GetStatus(fixture.ExtensionId);
        Assert.NotNull(status);
        Assert.Equal(ExtensionLoadState.Loaded, status!.State);
        var persisted = await ReadSnapshotAsync(test.Database, cancellationToken);
        var record = Assert.Single(persisted.Value!.ExtensionRecords,
            value => value.ExtensionId == fixture.ExtensionId);
        Assert.Equal(ContractExtensionLoadState.Loaded, record.LoadState);
    }

    [Fact]
    public async Task RouteCallbackSelfDisableCompletesWithoutARegisteredPublisher()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        using var fixture = InstalledFixtureDirectory.Create(
            "management.facade.self-disable." + Guid.NewGuid().ToString("N"),
            "1.0.0");
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await ReadSnapshotAsync(test.Database, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var seeded = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                fixture.ExtensionId,
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(seeded.IsSuccess, seeded.Errors.FirstOrDefault()?.Message);
        var snapshot = await ReadSnapshotAsync(test.Database, cancellationToken);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var (facade, provider) = CreateFacade(test.Database, snapshot.Value!, manager, fixture.ExtensionId);
        await using (provider)
        {
            var enabled = await facade.EnableAsync(fixture.ExtensionId, cancellationToken);
            Assert.True(enabled.IsSuccess, enabled.Errors.FirstOrDefault()?.Message);
            var loaded = await ReadSnapshotAsync(test.Database, cancellationToken);
            var loadedRecord = Assert.Single(loaded.Value!.ExtensionRecords);
            Assert.Equal(ContractExtensionLoadState.Loaded, loadedRecord.LoadState);

            using (ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route))
            {
                var disableTask = facade.DisableAsync(fixture.ExtensionId, cancellationToken).AsTask();
                var disabled = await disableTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                Assert.True(disabled.IsSuccess, disabled.Errors.FirstOrDefault()?.Message);
            }
        }

        var final = await ReadSnapshotAsync(test.Database, cancellationToken);
        Assert.Equal(ContractExtensionLoadState.Disabled, Assert.Single(final.Value!.ExtensionRecords).LoadState);
    }


    [Fact]
    public async Task FacadeDeleteRemovesRecordWhenItsManifestIsAbsent()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var initial = await ReadSnapshotAsync(test.Database, cancellationToken);
        var extensionId = "management.facade.delete." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var seeded = await test.Api.PersistDiscoveredExtensionRecordsAsync(
            ContractExtensionLoadState.Disabled,
            initial.Value!.Version,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                extensionId,
                "1.0.0",
                ContractExtensionLoadState.Disabled,
                now,
                now,
                0)),
            cancellationToken);
        Assert.True(seeded.IsSuccess, seeded.Errors.FirstOrDefault()?.Message);
        var snapshot = await ReadSnapshotAsync(test.Database, cancellationToken);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var (facade, provider) = CreateFacade(test.Database, snapshot.Value!, manager, "management.facade.delete.caller");
        await using (provider)
        {
            var deleted = await facade.DeleteRecordAsync(extensionId, cancellationToken);
            Assert.True(deleted.IsSuccess, deleted.Errors.FirstOrDefault()?.Message);
        }

        var final = await ReadSnapshotAsync(test.Database, cancellationToken);
        Assert.Empty(final.Value!.ExtensionRecords);
    }

    [Fact]
    public async Task FacadeRefreshAddsDiscoveryDisabledThenUpdatesItsInstalledVersion()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        using var fixture = InstalledFixtureDirectory.Create(
            "management.facade.refresh." + Guid.NewGuid().ToString("N"),
            "1.0.0");
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = await ReadSnapshotAsync(test.Database, cancellationToken);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var (facade, provider) = CreateFacade(test.Database, snapshot.Value!, manager, "management.facade.refresh.caller");
        await using (provider)
        {
            var added = await facade.RequestRefreshAsync(cancellationToken);
            Assert.True(added.IsSuccess, added.Errors.FirstOrDefault()?.Message);
            Assert.Contains(fixture.ExtensionId, added.Value!.Added);
            var first = await ReadSnapshotAsync(test.Database, cancellationToken);
            var firstRecord = Assert.Single(first.Value!.ExtensionRecords,
                value => value.ExtensionId == fixture.ExtensionId);
            Assert.Equal("1.0.0", firstRecord.Version);
            Assert.Equal(ContractExtensionLoadState.Disabled, firstRecord.LoadState);

            fixture.WriteManifestVersion("1.1.0");
            var changed = await facade.RequestRefreshAsync(cancellationToken);
            Assert.True(changed.IsSuccess, changed.Errors.FirstOrDefault()?.Message);
            Assert.Contains(fixture.ExtensionId, changed.Value!.VersionUpdated);
        }

        var final = await ReadSnapshotAsync(test.Database, cancellationToken);
        var record = Assert.Single(final.Value!.ExtensionRecords,
            value => value.ExtensionId == fixture.ExtensionId);
        Assert.Equal("1.1.0", record.Version);
    }

    private static async Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
        PostgresTestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = database.CreateContext();
        await using var api = new EfHostConfigApi(context);
        return await api.ReadSnapshotAsync(cancellationToken);
    }

    private static (ExtensionManagementFacade Facade, ServiceProvider Provider) CreateFacade(
        PostgresTestDatabase database,
        HostConfigurationSnapshot snapshot,
        ExtensionRuntimeManager manager,
        string callerExtensionId)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtimeState.MarkSnapshotAccepted();
        var provider = CreateManagementProvider(database);
        return (
            new ExtensionManagementFacade(
                callerExtensionId,
                provider.GetRequiredService<IServiceScopeFactory>(),
                runtimeState,
                manager,
                provider),
            provider);
    }

    private static ServiceProvider CreateManagementProvider(PostgresTestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped<EfHostConfigApi>();
        services.AddScoped<IHostConfigApi>(provider =>
            provider.GetRequiredService<EfHostConfigApi>());
        return services.BuildServiceProvider();
    }

    private sealed class InstalledFixtureDirectory : IDisposable
    {
        private readonly string directory;

        private InstalledFixtureDirectory(string directory, string extensionId)
        {
            this.directory = directory;
            ExtensionId = extensionId;
        }

        internal string ExtensionId { get; }

        internal static InstalledFixtureDirectory Create(string extensionId, string version)
        {
            var installRoot = Path.Combine(AppContext.BaseDirectory, "extensions");
            Directory.CreateDirectory(installRoot);
            var directory = Path.Combine(installRoot, "management-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.Copy(
                typeof(FixtureEntrypoint).Assembly.Location,
                Path.Combine(directory, "Fixtures.Extension.dll"));
            File.Copy(
                typeof(IExtensionEntrypoint).Assembly.Location,
                Path.Combine(directory, "Nekolla.Nekostick.Contracts.dll"));
            var fixture = new InstalledFixtureDirectory(directory, extensionId);
            fixture.WriteManifestVersion(version);
            return fixture;
        }

        internal void WriteManifestVersion(string version) =>
            File.WriteAllText(
                Path.Combine(directory, "manifest.json"),
                "{\n" +
                "  \"schemaVersion\": 1,\n" +
                $"  \"id\": \"{ExtensionId}\",\n" +
                "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
                $"  \"version\": \"{version}\",\n" +
                "  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n" +
                "  \"dependencies\": [],\n" +
                "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
                "}");

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        private readonly PostgresTestDatabase database;

        internal TestDbContextFactory(PostgresTestDatabase database) => this.database = database;

        public NekostickDbContext CreateDbContext() => database.CreateContext();

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(database.CreateContext());
        }
    }


}
