using System.Collections.Immutable;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the PostgreSQL-backed Host publication and node-session boundaries.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class HostPostgresAcceptanceTests
{
    private const long DefaultNodeActivityAdvisoryLockKey = 0x4E454B4E4F444530L;
    private static readonly string[] EagerFixtureArguments =
    [
        "--mode",
        "echo",
        "--listen-address",
        "127.0.0.1",
        "--port",
        "$PORT"
    ];

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

    /// <summary>Verifies an extension-owned handler route keeps persisted snapshots readable.</summary>
    [Fact]
    public async Task HostSnapshotReaderAcceptsExtensionOwnedHandlerRoutes()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        const string extensionId = "sample.controller";
        const string handlerId = "sample.controller.management";
        await using (var apiContext = database.CreateContext())
        await using (var api = new EfHostConfigApi(apiContext))
        {
            var initial = await api.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(initial.Value);

            var now = DateTimeOffset.UtcNow;
            var bootstrap = await api.PersistDiscoveredExtensionRecordsAsync(
                initial.Value!.Version,
                ImmutableArray.Create(new ExtensionRecordConfiguration(
                    extensionId, "1.0.0", Nekolla.Nekostick.Contracts.ExtensionLoadState.Loaded, now, now, 0)),
                TestContext.Current.CancellationToken);
            Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);

            var owned = new EfExtensionOwnedConfigurationApi(api);
            var ownedRead = await owned.ReadOwnedAsync(extensionId, TestContext.Current.CancellationToken);
            Assert.True(ownedRead.IsSuccess, ownedRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(ownedRead.Value);

            var route = new ExtensionRouteConfiguration(
                Guid.CreateVersion7(),
                true,
                new RouteMatcherConfiguration(
                    RouteMatcherType.Prefix,
                    "/controller76480110",
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty),
                new Nekolla.Nekostick.Contracts.ExtensionHandlerRouteTarget(handlerId),
                int.MaxValue);
            var apply = await owned.ApplyOwnedAsync(
                extensionId,
                ownedRead.Value!.Version,
                new ExtensionConfigurationChangeSet(
                    ImmutableArray.Create(route),
                    ImmutableArray<Guid>.Empty,
                    ImmutableArray<ExtensionServiceConfiguration>.Empty,
                    ImmutableArray<Guid>.Empty,
                    new ExtensionSettingsConfiguration(extensionId, 1, "{}", 0)),
                handlerIsOwned: static id => string.Equals(id, handlerId, StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(apply.IsSuccess, apply.Errors.FirstOrDefault()?.Message);
        }

        var reader = new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database));
        var snapshot = await reader.ReadCompleteAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsSuccess, snapshot.Errors.FirstOrDefault()?.Message);
        var persistedRoute = Assert.Single(snapshot.Value!.Routes);
        var target = Assert.IsType<ExtensionHandlerRouteTargetConfiguration>(persistedRoute.Target);
        Assert.Equal(handlerId, target.HandlerId);
        Assert.Equal(int.MaxValue, persistedRoute.Priority);
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
        var secondTerminationState = new HostTerminationState();
        var secondService = new HostNodeRegistrationService(
            secondFactory,
            snapshotHolder,
            secondRuntimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance,
            terminationState: secondTerminationState);
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
                Assert.Equal(1, secondTerminationState.ExitCode);
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

    /// <summary>Verifies initial identity registration precedes the accepted snapshot heartbeat.</summary>
    [Fact]
    public async Task InitialRegistrationPersistsBeforeSnapshotAndStartReusesTheHeldSession()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var runtimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: true, readOnly: false));
        var factory = new TestDbContextFactory(database);
        var activityLease = new TrackingHostNodeActivityLease(options);
        await using var service = new HostNodeRegistrationService(
            factory,
            snapshotHolder,
            runtimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance,
            activityLease: activityLease);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(snapshotHolder.Current);
        await service.EnsureInitialRegistrationAsync(cancellationToken);

        Assert.Null(snapshotHolder.Current);
        Assert.False(runtimeState.IsReady);
        Assert.Equal(HostReadinessState.Unready, runtimeState.Status.Readiness);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, activityLease.AcquireCount);
        await using (var nodeContext = database.CreateContext())
        {
            var initialNode = await nodeContext.Nodes.AsNoTracking()
                .SingleAsync(value => value.NodeId == options.NodeId, cancellationToken);
            Assert.True(initialNode.IsActive);
            Assert.Equal(0L, initialNode.LastConfigurationVersion);
            Assert.Equal("registered", initialNode.RuntimeState);
            Assert.Equal(1L, initialNode.Version);
            Assert.True(initialNode.CreatedAt <= initialNode.LastHeartbeatAt);
            Assert.True(initialNode.UpdatedAt >= initialNode.LastHeartbeatAt);
        }

        HostConfigurationSnapshot snapshot;
        await using (var snapshotContext = database.CreateContext())
        await using (var api = new EfHostConfigApi(snapshotContext))
        {
            var result = await api.ReadSnapshotAsync(cancellationToken);
            Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
            snapshot = result.Value!;
        }

        Assert.True(snapshotHolder.TryReplace(snapshot));
        runtimeState.MarkSnapshotAccepted();
        await service.StartAsync(cancellationToken);
        await WaitForNodeHeartbeatVersionAsync(
            database,
            options.NodeId,
            snapshot.Version,
            cancellationToken);

        await using (var nodeContext = database.CreateContext())
        {
            var heartbeatNode = await nodeContext.Nodes.AsNoTracking()
                .SingleAsync(value => value.NodeId == options.NodeId, cancellationToken);
            Assert.Equal(snapshot.Version, heartbeatNode.LastConfigurationVersion);
            Assert.Equal("ready", heartbeatNode.RuntimeState);
        }

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, activityLease.AcquireCount);
    }

    /// <summary>Fails worker startup when the initial activity lease has been lost.</summary>
    [Fact]
    public async Task InitialRegistrationLeaseLossBeforeStartFailsAndCleansUp()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var runtimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: true, readOnly: false));
        var factory = new TestDbContextFactory(database);
        var activityLease = new TrackingHostNodeActivityLease(options);
        await using var service = new HostNodeRegistrationService(
            factory,
            snapshotHolder,
            runtimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance,
            activityLease: activityLease);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.EnsureInitialRegistrationAsync(cancellationToken);
        factory.LastConnection!.Close();

        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(cancellationToken));

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, activityLease.AcquireCount);
        Assert.True(activityLease.IsDisposed);
        Assert.Equal(ConnectionState.Closed, factory.LastConnection.State);
        await using (var verifyContext = database.CreateContext())
        {
            var node = await verifyContext.Nodes.AsNoTracking()
                .SingleAsync(value => value.NodeId == options.NodeId, cancellationToken);
            Assert.Equal(1L, node.Version);
            Assert.Equal(0L, node.LastConfigurationVersion);
            Assert.Equal("registered", node.RuntimeState);
        }

        await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(options, cancellationToken);
    }

    /// <summary>Ensures an initial registration preserves the last accepted configuration version.</summary>
    [Fact]
    public async Task InitialRegistrationPreservesExistingConfigurationVersionWithoutASnapshot()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var previousUpdateAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset persistedCreatedAt;
        await using (var seedContext = database.CreateContext())
        {
            seedContext.Nodes.Add(new Node
            {
                Id = Guid.CreateVersion7(),
                NodeId = "0",
                LastHeartbeatAt = previousUpdateAt,
                LastConfigurationVersion = 37,
                RuntimeState = "ready",
                IsActive = false,
                CreatedAt = previousUpdateAt,
                UpdatedAt = previousUpdateAt,
                Version = 5
            });
            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            persistedCreatedAt = await seedContext.Nodes.AsNoTracking()
                .Where(value => value.NodeId == "0")
                .Select(value => value.CreatedAt)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var runtimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        var activityLease = new TrackingHostNodeActivityLease(options);
        await using var service = new HostNodeRegistrationService(
            new TestDbContextFactory(database),
            snapshotHolder,
            runtimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance,
            activityLease: activityLease);

        await service.EnsureInitialRegistrationAsync(TestContext.Current.CancellationToken);

        await using var verifyContext = database.CreateContext();
        var node = await verifyContext.Nodes.AsNoTracking()
            .SingleAsync(value => value.NodeId == options.NodeId, TestContext.Current.CancellationToken);
        Assert.Equal(37L, node.LastConfigurationVersion);
        Assert.Equal("registered", node.RuntimeState);
        Assert.True(node.IsActive);
        Assert.Equal(6L, node.Version);
        Assert.Equal(persistedCreatedAt, node.CreatedAt);
        Assert.True(node.LastHeartbeatAt > previousUpdateAt);
        Assert.True(node.UpdatedAt > previousUpdateAt);
    }

    /// <summary>Verifies failed initial persistence rolls back and releases the default-node session.</summary>
    [Fact]
    public async Task InitialRegistrationFailureBeforeCommitReleasesConnectionAndActivityLease()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);
        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("nodes")} " +
            "ADD CONSTRAINT \"reject_test_node_registration\" CHECK (node_id <> '0');");

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var runtimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: true, readOnly: false));
        var factory = new TestDbContextFactory(database);
        var activityLease = new TrackingHostNodeActivityLease(options);
        await using var service = new HostNodeRegistrationService(
            factory,
            snapshotHolder,
            runtimeState,
            options,
            NullLogger<HostNodeRegistrationService>.Instance,
            activityLease: activityLease);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            service.EnsureInitialRegistrationAsync(TestContext.Current.CancellationToken));

        Assert.Null(snapshotHolder.Current);
        Assert.False(runtimeState.IsReady);
        Assert.True(activityLease.IsDisposed);
        Assert.Equal(ConnectionState.Closed, factory.LastConnection!.State);
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} WHERE node_id = '0';"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("port_leases")};"));
        await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(options, TestContext.Current.CancellationToken);
    }

    /// <summary>Verifies disposing the pre-run DI owner releases its committed session lease.</summary>
    [Fact]
    public async Task DisposingDiProviderBeforeRunReleasesInitialRegistrationResources()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var migrationContext = database.CreateContext();
        await MigrateAsync(database, migrationContext);

        var snapshotHolder = new HostConfigurationSnapshotHolder();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var runtimeState = new HostRuntimeState(
            snapshotHolder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: true, readOnly: false));
        var factory = new TestDbContextFactory(database);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IHostConfigurationSnapshotAccessor>(snapshotHolder);
        services.AddSingleton(runtimeState);
        services.AddSingleton<IDbContextFactory<NekostickDbContext>>(factory);
        services.AddSingleton<IHostNodeActivityLease>(_ => new PostgresHostNodeActivityLease(options));
        services.AddSingleton<HostNodeRegistrationService>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<HostNodeRegistrationService>());
        var provider = services.BuildServiceProvider();
        var providerDisposed = false;

        try
        {
            var registration = provider.GetRequiredService<HostNodeRegistrationService>();
            var hostedRegistration = Assert.Single(provider.GetServices<IHostedService>()
                .OfType<HostNodeRegistrationService>());
            Assert.Same(registration, hostedRegistration);

            await registration.EnsureInitialRegistrationAsync(TestContext.Current.CancellationToken);
            Assert.Null(snapshotHolder.Current);
            Assert.Equal(ConnectionState.Open, factory.LastConnection!.State);
            Assert.Equal(
                1L,
                await database.ExecuteScalarAsync<long>(
                    $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} " +
                    "WHERE node_id = '0' AND is_active;"));

            await provider.DisposeAsync();
            providerDisposed = true;

            Assert.Equal(ConnectionState.Closed, factory.LastConnection.State);
            await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(
                options,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!providerDisposed)
            {
                await provider.DisposeAsync();
            }
        }
    }

    /// <summary>Proves a delayed initial registration commits before an eager fixture becomes ready.</summary>
    [Fact]
    public async Task ProgramRunStartsEagerFixtureAfterDelayedNodeRegistration()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using (var migrationContext = database.CreateContext())
        {
            await MigrateAsync(database, migrationContext);
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var (listenPort, servicePort) = GetDistinctFreeTcpPorts();
        await SetFixturePortRangeAsync(database, servicePort, cancellationToken);
        var serviceId = await SeedEagerFixtureServiceAsync(database, cancellationToken);
        await AddDelayedNodeRegistrationTriggerAsync(database);
        var dataDirectory = CreateProgramDataDirectory();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);

        try
        {
            var command = CreateRunCommand(connectionString, dataDirectory, listenPort, disableSupervisor: false);
            await using var host = StartProgramRun(command, database, cancellationToken);

            await host.ApplicationStarted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var runtimeAccessor = await host.RuntimeSnapshotAccessor.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);
            await WaitForEagerFixtureReadyAsync(
                database,
                serviceId,
                options.NodeId,
                runtimeAccessor,
                cancellationToken);
            Assert.True(
                host.InitialSnapshotObservedRegisteredNode,
                "The initial snapshot reader did not observe the committed, not-yet-ready node registration.");
            await AssertNodeRegistrationDelayObservedAsync(database, options.NodeId);
            await AssertDefaultNodeActivityLeaseIsHeldAsync(options, cancellationToken);
            Assert.False(host.Completion.IsCompleted);

            await host.StopAsync();
            await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(options, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    /// <summary>Proves supervisor-disabled Run still registers the node and holds its activity lease.</summary>
    [Fact]
    public async Task ProgramRunWithSupervisorDisabledKeepsNodeLeaseWithoutStartingEagerFixture()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using (var migrationContext = database.CreateContext())
        {
            await MigrateAsync(database, migrationContext);
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var (listenPort, servicePort) = GetDistinctFreeTcpPorts();
        await SetFixturePortRangeAsync(database, servicePort, cancellationToken);
        var serviceId = await SeedEagerFixtureServiceAsync(database, cancellationToken);
        var fixtureExecutable = RequireFixtureExecutable();
        var fixtureProcessIdsBeforeRun = GetRunningFixtureProcessIds(fixtureExecutable);
        await AddDelayedNodeRegistrationTriggerAsync(database);
        var dataDirectory = CreateProgramDataDirectory();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);

        try
        {
            var command = CreateRunCommand(connectionString, dataDirectory, listenPort, disableSupervisor: true);
            await using var host = StartProgramRun(command, database, cancellationToken);

            await host.ApplicationStarted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await WaitForNodeReadyAsync(database, options.NodeId, cancellationToken);
            Assert.True(
                host.InitialSnapshotObservedRegisteredNode,
                "The initial snapshot reader did not observe the committed, not-yet-ready node registration.");
            await AssertNodeRegistrationDelayObservedAsync(database, options.NodeId);
            await AssertDefaultNodeActivityLeaseIsHeldAsync(options, cancellationToken);
            await AssertTcpPortUnavailableAsync(servicePort, cancellationToken);

            await using (var verifyContext = database.CreateContext())
            {
                Assert.False(await verifyContext.PortLeases.AsNoTracking()
                    .AnyAsync(value => value.ServiceId == serviceId && value.NodeId == options.NodeId, cancellationToken));
            }
            Assert.Empty(GetRunningFixtureProcessIds(fixtureExecutable).Except(fixtureProcessIdsBeforeRun));

            Assert.False(host.Completion.IsCompleted);
            await host.StopAsync();
            await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(options, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    /// <summary>Proves registration failure prevents snapshot reading, publication, and eager service startup.</summary>
    [Fact]
    public async Task ProgramRunNodeRegistrationFailureStopsBeforeSnapshotAndReleasesLease()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using (var migrationContext = database.CreateContext())
        {
            await MigrateAsync(database, migrationContext);
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var (listenPort, servicePort) = GetDistinctFreeTcpPorts();
        await SetFixturePortRangeAsync(database, servicePort, cancellationToken);
        var serviceId = await SeedEagerFixtureServiceAsync(database, cancellationToken);
        var fixtureExecutable = RequireFixtureExecutable();
        var fixtureProcessIdsBeforeRun = GetRunningFixtureProcessIds(fixtureExecutable);
        var nodeRelation = database.QualifiedRelation("nodes");
        var registrationFunction = database.QualifiedRelation("reject_initial_node_registration");
        await database.ExecuteSchemaCommandAsync(
            $"""
            CREATE FUNCTION {registrationFunction}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF NEW.node_id = '0' THEN
                    RAISE EXCEPTION 'Initial node registration rejected by acceptance test.'
                        USING ERRCODE = '23514',
                              CONSTRAINT = 'ck_test_reject_default_node';
                END IF;
                RETURN NEW;
            END;
            $body$;
            """);
        await database.ExecuteSchemaCommandAsync(
            $"CREATE TRIGGER reject_initial_node_registration BEFORE INSERT ON {nodeRelation} " +
            $"FOR EACH ROW EXECUTE FUNCTION {registrationFunction}();");
        var dataDirectory = CreateProgramDataDirectory();
        var options = new HostRuntimeOptions(connectionString, "0", readOnly: false);
        var command = CreateRunCommand(connectionString, dataDirectory, listenPort, disableSupervisor: false);
        var snapshotReader = new NodeRegistrationObservingSnapshotReader(
            new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database)),
            database);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var completion = Program.ExecuteWithServiceConfigurationAsync(
                command,
                services => ConfigurePostgresTestServices(
                    services,
                    database,
                    snapshotReader,
                    applicationStarted: null),
                shutdown.Token);

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => completion);
            var postgresFailure = FindPostgresException(failure);
            Assert.NotNull(postgresFailure);
            Assert.Equal("23514", postgresFailure!.SqlState);
            Assert.Equal("ck_test_reject_default_node", postgresFailure!.ConstraintName);
            Assert.Equal(0, snapshotReader.ReadCount);

            await using (var verifyContext = database.CreateContext())
            {
                Assert.False(await verifyContext.Nodes.AsNoTracking()
                    .AnyAsync(value => value.NodeId == options.NodeId, cancellationToken));
                Assert.False(await verifyContext.PortLeases.AsNoTracking()
                    .AnyAsync(value => value.ServiceId == serviceId && value.NodeId == options.NodeId, cancellationToken));
            }

            await AssertTcpPortUnavailableAsync(listenPort, cancellationToken);
            await AssertTcpPortUnavailableAsync(servicePort, cancellationToken);
            await AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(options, cancellationToken);
            Assert.Empty(GetRunningFixtureProcessIds(fixtureExecutable).Except(fixtureProcessIdsBeforeRun));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static RunningProgramHost StartProgramRun(
        CliCommand command,
        PostgresTestDatabase database,
        CancellationToken cancellationToken)
    {
        var snapshotReader = new NodeRegistrationObservingSnapshotReader(
            new EfHostConfigurationSnapshotReader(new TestDbContextFactory(database)),
            database);
        var applicationStarted =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeSnapshotAccessor = command.RunOptions.DisableSupervisor
            ? null
            : new TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = Program.ExecuteWithServiceConfigurationAsync(
            command,
            services => ConfigurePostgresTestServices(
                services,
                database,
                snapshotReader,
                applicationStarted,
                runtimeSnapshotAccessor),
            shutdown.Token);
        return new RunningProgramHost(
            shutdown,
            completion,
            snapshotReader,
            applicationStarted,
            runtimeSnapshotAccessor);
    }

    private static void ConfigurePostgresTestServices(
        IServiceCollection services,
        PostgresTestDatabase database,
        NodeRegistrationObservingSnapshotReader snapshotReader,
        TaskCompletionSource<bool>? applicationStarted,
        TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor>? runtimeSnapshotAccessor = null)
    {
        services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<NekostickDbContext>>(
            new TestDbContextFactory(database)));
        services.Replace(ServiceDescriptor.Scoped<NekostickDbContext>(
            _ => database.CreateContext()));
        services.Replace(ServiceDescriptor.Singleton<IStartupDatabaseProbe>(
            database.CreateMigrationCoordinator()));
        services.Replace(ServiceDescriptor.Singleton<IHostConfigurationSnapshotReader>(snapshotReader));
        if (applicationStarted is not null)
        {
            services.AddSingleton<IHostedService>(serviceProvider => new ApplicationStartedSignal(
                serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                applicationStarted));
        }

        if (runtimeSnapshotAccessor is not null)
        {
            services.AddSingleton<IHostedService>(serviceProvider => new HostServiceRuntimeAccessorCapture(
                serviceProvider.GetRequiredService<IHostServiceRuntimeSnapshotAccessor>(),
                runtimeSnapshotAccessor));
        }
    }

    private static CliCommand CreateRunCommand(
        string connectionString,
        string dataDirectory,
        int listenPort,
        bool disableSupervisor)
    {
        var arguments = new List<string>
        {
            "run",
            "--connection-string",
            connectionString,
            "--listen-address",
            "127.0.0.1",
            "--listen-port",
            listenPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--node-id",
            "0",
            "--data-directory",
            dataDirectory,
            "--skip-extensions"
        };
        if (disableSupervisor)
        {
            arguments.Add("--disable-supervisor");
        }

        var result = CliCommandParser.Parse(arguments, ImmutableDictionary<string, string?>.Empty);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Command!;
    }

    private static (int ListenPort, int ServicePort) GetDistinctFreeTcpPorts()
    {
        using var listenListener = new TcpListener(IPAddress.Loopback, 0);
        using var serviceListener = new TcpListener(IPAddress.Loopback, 0);
        listenListener.Start();
        serviceListener.Start();
        return (
            ((IPEndPoint)listenListener.LocalEndpoint).Port,
            ((IPEndPoint)serviceListener.LocalEndpoint).Port);
    }

    private static async Task SetFixturePortRangeAsync(
        PostgresTestDatabase database,
        int port,
        CancellationToken cancellationToken)
    {
        await using var context = database.CreateContext();
        var settings = await context.GlobalSettings.SingleAsync(cancellationToken);
        settings.AutoPortRangeStart = port;
        settings.AutoPortRangeEnd = port;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.Version++;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Guid> SeedEagerFixtureServiceAsync(
        PostgresTestDatabase database,
        CancellationToken cancellationToken)
    {
        var fixturePath = RequireFixtureExecutable();
        var now = DateTimeOffset.UtcNow;
        var service = new Service
        {
            Id = Guid.CreateVersion7(),
            Enabled = true,
            FileName = fixturePath,
            ArgumentListJson = JsonSerializer.Serialize(EagerFixtureArguments),
            WorkingDirectory = Path.GetDirectoryName(fixturePath)!,
            EnvironmentJson = "{}",
            StartMode = ServiceStartPolicy.Eager,
            RestartPolicy = Nekolla.Nekostick.Domain.ServiceRestartPolicy.Never,
            HealthCheckType = ServiceHealthCheckKind.Http,
            HealthCheckHttpPath = "/fixture/health",
            HealthCheckTimeoutMilliseconds = 3000,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        await using var context = database.CreateContext();
        context.Services.Add(service);
        await context.SaveChangesAsync(cancellationToken);
        return service.Id;
    }

    private static string RequireFixtureExecutable()
    {
        var fileName = OperatingSystem.IsWindows()
            ? "Fixtures.Microservice.exe"
            : "Fixtures.Microservice";
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var solutionFile = Path.Combine(solutionRoot, "Nekolla.Nekostick.slnx");
        if (!File.Exists(solutionFile))
        {
            throw new InvalidOperationException(
                $"The integration-test output directory must resolve to the repository solution root; expected '{solutionFile}'.");
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(solutionRoot, "tests", "Fixtures.Microservice", "bin", "Debug", "net10.0", fileName),
            Path.Combine(solutionRoot, "tests", "Fixtures.Microservice", "bin", "Release", "net10.0", fileName)
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "The compiled Fixtures.Microservice executable is required. Build " +
            "tests/Fixtures.Microservice/Fixtures.Microservice.csproj as part of the integration-test build. " +
            $"Expected one of: {string.Join(", ", candidates)}.");
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }

    private static HashSet<int> GetRunningFixtureProcessIds(string fixtureExecutable)
    {
        var expectedExecutable = Path.GetFullPath(fixtureExecutable);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is not null &&
                        string.Equals(Path.GetFullPath(executable), expectedExecutable, pathComparison))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException or NotSupportedException or
                        UnauthorizedAccessException)
                {
                    // Inaccessible or exiting unrelated processes do not affect fixture-process checks.
                }
            }
        }

        return processIds;
    }

    private static string CreateProgramDataDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "nekostick-run-" + Guid.NewGuid().ToString("N"));

    private static async Task AddDelayedNodeRegistrationTriggerAsync(PostgresTestDatabase database)
    {
        var evidence = database.QualifiedRelation("node_registration_delay_evidence");
        var function = database.QualifiedRelation("delay_initial_node_registration");
        await database.ExecuteSchemaCommandAsync(
            $"CREATE TABLE {evidence} (node_id text PRIMARY KEY, started_at timestamptz NOT NULL, completed_at timestamptz NOT NULL);");
        await database.ExecuteSchemaCommandAsync(
            $"""
            CREATE FUNCTION {function}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $body$
            DECLARE
                insertion_started_at timestamptz;
            BEGIN
                IF NEW.node_id = '0' THEN
                    insertion_started_at := clock_timestamp();
                    PERFORM pg_sleep(1.25);
                    INSERT INTO {evidence} (node_id, started_at, completed_at)
                    VALUES (NEW.node_id, insertion_started_at, clock_timestamp());
                END IF;
                RETURN NEW;
            END;
            $body$;
            """);
        await database.ExecuteSchemaCommandAsync(
            $"CREATE TRIGGER delay_initial_node_registration BEFORE INSERT ON {database.QualifiedRelation("nodes")} " +
            $"FOR EACH ROW EXECUTE FUNCTION {function}();");
    }

    private static async Task AssertNodeRegistrationDelayObservedAsync(
        PostgresTestDatabase database,
        string nodeId)
    {
        var delay = await database.ExecuteScalarAsync<TimeSpan>(
            $"SELECT completed_at - started_at FROM {database.QualifiedRelation("node_registration_delay_evidence")} " +
            "WHERE node_id = @node_id;",
            new NpgsqlParameter("node_id", NpgsqlDbType.Text)
            {
                Value = nodeId
            });
        Assert.True(delay >= TimeSpan.FromSeconds(1), $"The node insert trigger did not delay registration: {delay}.");
    }

    private static async Task WaitForNodeReadyAsync(
        PostgresTestDatabase database,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var context = database.CreateContext();
            var node = await context.Nodes.AsNoTracking()
                .SingleOrDefaultAsync(value => value.NodeId == nodeId, cancellationToken);
            if (node is { IsActive: true, RuntimeState: "ready" })
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail("The real Host Run path did not persist a ready node within the bounded acceptance-test window.");
    }

    private static async Task WaitForEagerFixtureReadyAsync(
        PostgresTestDatabase database,
        Guid serviceId,
        string nodeId,
        IHostServiceRuntimeSnapshotAccessor runtimeAccessor,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var hasReadyRuntime = runtimeAccessor.TryGet(serviceId, out var runtime) &&
                runtime.LifecycleState == ExtensionServiceLifecycleState.Running &&
                runtime.Health == ExtensionServiceHealthState.Healthy &&
                runtime.ProcessId is not null &&
                runtime.ProcessInstanceId is not null;
            await using var context = database.CreateContext();
            var node = await context.Nodes.AsNoTracking()
                .SingleOrDefaultAsync(value => value.NodeId == nodeId, cancellationToken);
            var lease = await context.PortLeases.AsNoTracking()
                .SingleOrDefaultAsync(value => value.NodeId == nodeId && value.ServiceId == serviceId, cancellationToken);
            if (node is { IsActive: true, RuntimeState: "ready" } &&
                hasReadyRuntime &&
                lease is not null && lease.LeaseExpiresAt > DateTimeOffset.UtcNow)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail("The real Host Run path did not make the eager fixture ready with an active port lease.");
    }


    private static async Task AssertTcpPortUnavailableAsync(
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        var failure = await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken));
        Assert.Equal(SocketError.ConnectionRefused, failure.SocketErrorCode);
    }

    private static async Task AssertDefaultNodeActivityLeaseIsHeldAsync(
        HostRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await using var competingLease = new PostgresHostNodeActivityLease(options);
        await connection.OpenAsync(cancellationToken);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            competingLease.AcquireAsync(connection, cancellationToken));
        Assert.Equal("HostNodeAlreadyActiveException", failure.GetType().Name);
    }

    private sealed class HostServiceRuntimeAccessorCapture : IHostedService
    {
        private readonly IHostServiceRuntimeSnapshotAccessor _runtimeAccessor;
        private readonly TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor> _completion;

        internal HostServiceRuntimeAccessorCapture(
            IHostServiceRuntimeSnapshotAccessor runtimeAccessor,
            TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor> completion)
        {
            _runtimeAccessor = runtimeAccessor;
            _completion = completion;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _completion.TrySetResult(_runtimeAccessor);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ApplicationStartedSignal : IHostedService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly TaskCompletionSource<bool> _completion;
        private CancellationTokenRegistration _registration;

        internal ApplicationStartedSignal(
            IHostApplicationLifetime lifetime,
            TaskCompletionSource<bool> completion)
        {
            _lifetime = lifetime;
            _completion = completion;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _registration = _lifetime.ApplicationStarted.Register(() => _completion.TrySetResult(true));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _registration.Dispose();
            return Task.CompletedTask;
        }
    }

    private sealed class NodeRegistrationObservingSnapshotReader : IHostConfigurationSnapshotReader
    {
        private readonly IHostConfigurationSnapshotReader _inner;
        private readonly PostgresTestDatabase _database;
        private int _readCount;
        private int _initialReadObservedRegisteredNode;

        internal NodeRegistrationObservingSnapshotReader(
            IHostConfigurationSnapshotReader inner,
            PostgresTestDatabase database)
        {
            _inner = inner;
            _database = database;
        }

        internal bool InitialReadObservedRegisteredNode =>
            Volatile.Read(ref _initialReadObservedRegisteredNode) == 1;

        internal int ReadCount => Volatile.Read(ref _readCount);

        public async Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadCompleteAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                var registeredCount = await _database.ExecuteScalarAsync<long>(
                    $"SELECT COUNT(*) FROM {_database.QualifiedRelation("nodes")} " +
                    "WHERE node_id = '0' AND is_active AND runtime_state = 'registered' " +
                    "AND last_configuration_version = 0;");
                Volatile.Write(ref _initialReadObservedRegisteredNode, registeredCount == 1 ? 1 : 0);
            }

            return await _inner.ReadCompleteAsync(cancellationToken);
        }
    }

    private sealed class RunningProgramHost : IAsyncDisposable
    {
        private readonly Task<int> _completion;
        private readonly NodeRegistrationObservingSnapshotReader _snapshotReader;
        private readonly TaskCompletionSource<bool> _applicationStarted;
        private readonly TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor>? _runtimeSnapshotAccessor;
        private CancellationTokenSource? _shutdown;

        internal RunningProgramHost(
            CancellationTokenSource shutdown,
            Task<int> completion,
            NodeRegistrationObservingSnapshotReader snapshotReader,
            TaskCompletionSource<bool> applicationStarted,
            TaskCompletionSource<IHostServiceRuntimeSnapshotAccessor>? runtimeSnapshotAccessor)
        {
            _shutdown = shutdown;
            _completion = completion;
            _snapshotReader = snapshotReader;
            _applicationStarted = applicationStarted;
            _runtimeSnapshotAccessor = runtimeSnapshotAccessor;
        }

        internal Task<int> Completion => _completion;

        internal Task ApplicationStarted => _applicationStarted.Task;

        internal Task<IHostServiceRuntimeSnapshotAccessor> RuntimeSnapshotAccessor =>
            _runtimeSnapshotAccessor?.Task ??
            throw new InvalidOperationException("The supervisor runtime accessor was not captured.");

        internal bool InitialSnapshotObservedRegisteredNode =>
            _snapshotReader.InitialReadObservedRegisteredNode;

        internal async Task StopAsync()
        {
            var shutdown = Interlocked.Exchange(ref _shutdown, null);
            if (shutdown is null)
            {
                return;
            }

            shutdown.Cancel();
            try
            {
                await _completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                shutdown.Dispose();
            }
        }

        public ValueTask DisposeAsync() => new(StopAsync());
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
            restartPolicy: Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
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

    private static async Task WaitForNodeHeartbeatVersionAsync(
        PostgresTestDatabase database,
        string nodeId,
        long acceptedVersion,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var heartbeatObserved = await database.ExecuteScalarAsync<bool>(
                $"SELECT version >= 2 AND last_configuration_version = @configuration_version " +
                $"FROM {database.QualifiedRelation("nodes")} WHERE node_id = @node_id;",
                new NpgsqlParameter("configuration_version", NpgsqlDbType.Bigint)
                {
                    Value = acceptedVersion
                },
                new NpgsqlParameter("node_id", NpgsqlDbType.Text)
                {
                    Value = nodeId
                });
            if (heartbeatObserved)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail("The accepted node heartbeat version was not persisted within the bounded acceptance-test window.");
    }

    private static async Task AssertDefaultNodeActivityLeaseCanBeAcquiredAsync(
        HostRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await using var lease = new PostgresHostNodeActivityLease(options);
        await connection.OpenAsync(cancellationToken);
        await lease.AcquireAsync(connection, cancellationToken);
        await lease.EnsureHeldAsync(cancellationToken);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        private readonly PostgresTestDatabase database;
        private int _createCount;

        internal TestDbContextFactory(PostgresTestDatabase database) =>
            this.database = database;

        internal int CreateCount => Volatile.Read(ref _createCount);

        internal DbConnection? LastConnection { get; private set; }

        public NekostickDbContext CreateDbContext() => CreateContext();

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateContext());
        }

        private NekostickDbContext CreateContext()
        {
            var context = database.CreateContext();
            LastConnection = context.Database.GetDbConnection();
            Interlocked.Increment(ref _createCount);
            return context;
        }
    }

    private sealed class TrackingHostNodeActivityLease : IHostNodeActivityLease
    {
        private readonly PostgresHostNodeActivityLease _inner;
        private int _acquireCount;
        private int _disposed;

        internal TrackingHostNodeActivityLease(HostRuntimeOptions options) =>
            _inner = new PostgresHostNodeActivityLease(options);

        internal int AcquireCount => Volatile.Read(ref _acquireCount);

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public async Task AcquireAsync(
            DbConnection connection,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCount);
            await _inner.AcquireAsync(connection, cancellationToken);
        }

        public Task EnsureHeldAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureHeldAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await _inner.DisposeAsync();
        }
    }
}
