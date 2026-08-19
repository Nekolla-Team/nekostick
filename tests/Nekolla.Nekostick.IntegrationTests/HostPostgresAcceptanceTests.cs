using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the PostgreSQL-backed Host publication and node-session boundaries.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class HostPostgresAcceptanceTests
{
    private const long DefaultNodeActivityAdvisoryLockKey = 0x4E454B4E4F444530L;

    /// <summary>Verifies invalid persisted CIDRs are rejected before a snapshot publication.</summary>
    [Fact]
    public async Task HostSnapshotReaderRejectsInvalidPersistedCidrWithoutPublishingIt()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);

        var reader = new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database));
        var initial = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var published = new HostConfigurationSnapshotHolder();
        Assert.True(published.TryReplace(initial.Value!));

        await database.ExecuteSchemaCommandAsync(
            $"UPDATE {database.QualifiedRelation("global_settings")} " +
            "SET trusted_proxy_cidrs_json = @json WHERE id = @id;",
            new NpgsqlParameter("json", NpgsqlDbType.Jsonb)
            {
                Value = "[\"10.0.0.0/33\"]"
            },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
            });

        var rejected = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Null(rejected.Value);
        Assert.Equal(ConfigurationErrorCode.Validation, rejected.Errors.Single().Code);
        Assert.Same(initial.Value, published.Current);
    }

    /// <summary>Verifies the host snapshot reader preserves route resource overrides from persistence.</summary>
    [Fact]
    public async Task HostSnapshotReaderPreservesRouteResourceOverrides()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var serviceId = Guid.CreateVersion7();
        var routeId = Guid.CreateVersion7();
        await using (var apiContext = database.CreateContext())
        await using (var api = new EfHostConfigApi(apiContext))
        {
            var initial = await api.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(initial.Value);

            var write = await api.WriteSnapshotAsync(
                initial.Value!.Version,
                CreateRouteChangeSet(
                    initial.Value!,
                    serviceId,
                    routeId,
                    maxRequestBodyBytes: 1024 * 1024,
                    maxRequestHeaderBytes: 16 * 1024,
                    maxConcurrentRequests: 16,
                    requestReadTimeout: TimeSpan.FromSeconds(5)),
                TestContext.Current.CancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
        }

        var reader = new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database));
        var snapshot = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsSuccess, snapshot.Errors.FirstOrDefault()?.Message);
        var route = Assert.Single(snapshot.Value!.Routes);
        Assert.Equal(1024 * 1024, route.MaxRequestBodyBytes);
        Assert.Equal(16 * 1024, route.MaxRequestHeaderBytes);
        Assert.Equal(16, route.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromSeconds(5), route.RequestReadTimeout);
    }

    /// <summary>Verifies invalid persisted route matchers are rejected before publication.</summary>
    [Fact]
    public async Task HostSnapshotReaderRejectsInvalidPersistedMatcherWithoutPublishingIt()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var serviceId = Guid.CreateVersion7();
        var routeId = Guid.CreateVersion7();
        await using (var apiContext = database.CreateContext())
        await using (var api = new EfHostConfigApi(apiContext))
        {
            var initial = await api.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(initial.Value);

            var write = await api.WriteSnapshotAsync(
                initial.Value!.Version,
                CreateRouteChangeSet(initial.Value!, serviceId, routeId),
                TestContext.Current.CancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
        }

        var reader = new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database));
        var initialPublished = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(initialPublished.IsSuccess, initialPublished.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initialPublished.Value);

        var published = new HostConfigurationSnapshotHolder();
        Assert.True(published.TryReplace(initialPublished.Value!));

        await database.ExecuteSchemaCommandAsync(
            $"UPDATE {database.QualifiedRelation("routes")} SET pattern = @pattern WHERE id = @id;",
            new NpgsqlParameter
            {
                ParameterName = "pattern",
                NpgsqlDbType = NpgsqlDbType.Text,
                Value = "relative"
            },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = routeId
            });

        var rejected = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Null(rejected.Value);
        Assert.Equal(ConfigurationErrorCode.Validation, rejected.Errors.Single().Code);
        Assert.Same(initialPublished.Value, published.Current);
    }

    /// <summary>Verifies the real session lease rejects a second node-0 session and checks its exact key.</summary>
    [Fact]
    public async Task DefaultNodeSessionLeaseRejectsSecondSessionAndRequiresExactAdvisoryKey()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        await using var firstConnection = new NpgsqlConnection(connectionString);
        await using var secondConnection = new NpgsqlConnection(connectionString);
        await using var firstLease = new PostgresHostNodeActivityLease(options);
        await using var secondLease = new PostgresHostNodeActivityLease(options);
        var cancellationToken = TestContext.Current.CancellationToken;

        await firstConnection.OpenAsync(cancellationToken);
        await secondConnection.OpenAsync(cancellationToken);
        await firstLease.AcquireAsync(firstConnection, cancellationToken);
        await firstLease.EnsureHeldAsync(cancellationToken);

        var secondAcquireFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
            secondLease.AcquireAsync(secondConnection, cancellationToken));
        Assert.Equal("HostNodeAlreadyActiveException", secondAcquireFailure.GetType().Name);

        await ReleaseExactLockAndHoldUnrelatedLockAsync(firstConnection, cancellationToken);

        var lostLeaseFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
            firstLease.EnsureHeldAsync(cancellationToken));
        Assert.Equal("HostNodeActivityLostException", lostLeaseFailure.GetType().Name);

        await firstLease.DisposeAsync();
        await secondLease.AcquireAsync(secondConnection, cancellationToken);
        await secondLease.EnsureHeldAsync(cancellationToken);
    }

    /// <summary>Verifies Host registration writes a heartbeat while its session lease is held.</summary>
    [Fact]
    public async Task HostNodeRegistrationHeartbeatHoldsLeaseAndSecondRegistrationFailsSafely()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        HostConfigurationSnapshot snapshot;
        await using (var apiContext = database.CreateContext())
        await using (var api = new EfHostConfigApi(apiContext))
        {
            var result = await api.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
            snapshot = result.Value!;
        }

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        Assert.True(snapshotHolder.TryReplace(snapshot));
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var firstRuntimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        var secondRuntimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        var firstFactory = new TestDbContextFactory(database);
        var secondFactory = new TestDbContextFactory(database);
        var firstService = new HostNodeRegistrationService(
            firstFactory,
            snapshotHolder,
            firstRuntimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance);
        var secondService = new HostNodeRegistrationService(
            secondFactory,
            snapshotHolder,
            secondRuntimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstStarted = false;

        try
        {
            await firstService.StartAsync(cancellationToken);
            firstStarted = true;
            await WaitForNodeHeartbeatAsync(database, cancellationToken);

            try
            {
                var secondStartFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                    secondService.StartAsync(cancellationToken));
                Assert.Equal("HostNodeAlreadyActiveException", secondStartFailure.GetType().Name);
            }
            finally
            {
                await secondService.StopAsync(CancellationToken.None);
            }

            Assert.Equal(
                1L,
                await database.ExecuteScalarAsync<long>(
                    $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} " +
                    "WHERE node_id = '0' AND is_active;"));
        }
        finally
        {
            if (firstStarted)
            {
                await firstService.StopAsync(CancellationToken.None);
            }
        }
    }

    private static async Task MigrateAsync(
        PostgresTestDatabase database,
        NekostickDbContext context)
    {
        var result = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static ConfigurationChangeSet CreateRouteChangeSet(
        HostConfigurationSnapshot snapshot,
        Guid serviceId,
        Guid routeId,
        long? maxRequestBodyBytes = null,
        long? maxRequestHeaderBytes = null,
        int? maxConcurrentRequests = null,
        TimeSpan? requestReadTimeout = null)
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ServiceConfiguration(
            serviceId,
            enabled: true,
            fileName: "/usr/bin/host-acceptance-fixture",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty,
            startMode: ServiceStartMode.Eager,
            restartPolicy: ServiceRestartPolicy.Never,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: now,
            updatedAt: now,
            version: 0);
        var route = new RouteConfiguration(
            routeId,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/host-acceptance",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: new MicroserviceRouteTargetConfiguration(serviceId),
            priority: 1,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, replaceTemplate: null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: now,
            updatedAt: now,
            version: 0,
            maxRequestBodyBytes: maxRequestBodyBytes,
            maxRequestHeaderBytes: maxRequestHeaderBytes,
            maxConcurrentRequests: maxConcurrentRequests,
            requestReadTimeout: requestReadTimeout);

        return new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }

    private static async Task ReleaseExactLockAndHoldUnrelatedLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var unlockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_unlock(@lock_key);",
                         connection))
        {
            unlockCommand.Parameters.Add(
                new NpgsqlParameter("lock_key", NpgsqlDbType.Bigint)
                {
                    Value = DefaultNodeActivityAdvisoryLockKey
                });
            Assert.True((bool)(await unlockCommand.ExecuteScalarAsync(cancellationToken))!);
        }

        await using var unrelatedLockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_lock(@lock_key);",
            connection);
        unrelatedLockCommand.Parameters.Add(
            new NpgsqlParameter("lock_key", NpgsqlDbType.Bigint)
            {
                Value = DefaultNodeActivityAdvisoryLockKey + 1
            });
        await unrelatedLockCommand.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task WaitForNodeHeartbeatAsync(
        PostgresTestDatabase database,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var count = await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} " +
                "WHERE node_id = '0' AND is_active;");
            if (count == 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail("The Host node heartbeat was not persisted within the bounded acceptance-test window.");
    }

    private sealed class TestDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        private readonly PostgresTestDatabase database;

        internal TestDbContextFactory(PostgresTestDatabase database) =>
            this.database = database;

        public NekostickDbContext CreateDbContext() => database.CreateContext();

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(database.CreateContext());
        }
    }
}
