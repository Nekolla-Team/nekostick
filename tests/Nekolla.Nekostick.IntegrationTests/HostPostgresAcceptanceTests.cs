using System.Collections.Concurrent;
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
using Nekolla.Nekostick.Supervision;
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
    private static readonly string[] CandidateRootLaunchArguments =
    [
        "--root",
        "new-root"
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

    /// <summary>Proves a candidate PostgreSQL lease does not replace the ready endpoint before health succeeds.</summary>
    [Fact]
    public async Task HostGenerationLeaseSwapKeepsOldEndpointUntilCandidateIsHealthy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await GenerationLeasePostgresHarness.CreateAsync(cancellationToken);

        await harness.StartInitialGenerationAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var oldLease = Assert.Single(await harness.ReadLeaseRowsAsync(cancellationToken));
        var oldEndpoint = Assert.Single(harness.EndpointPublisher.Current).Value;
        var oldProcess = Assert.Single(harness.ProcessExecutor.StartedProcesses);
        Assert.Equal(oldLease.GenerationId, oldEndpoint.GenerationId);
        Assert.Equal(oldLease.Port, oldEndpoint.Port);
        Assert.Equal(1L, oldLease.Version);
        Assert.True(UuidV7.IsVersion7(oldLease.GenerationId));

        var candidateSnapshot = await harness.WriteCandidateSnapshotAsync(cancellationToken);
        var candidateStartup = harness.LifecycleManager
            .EnsureReadyAsync(candidateSnapshot, harness.ServiceId, cancellationToken)
            .AsTask();
        var candidatePort = await harness.HealthProbe.CandidateProbeEntered.Task.WaitAsync(cancellationToken);

        Assert.False(candidateStartup.IsCompleted);
        var leasesDuringWarmup = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(2, leasesDuringWarmup.Length);
        var oldLeaseDuringWarmup = leasesDuringWarmup.Single(value => value.GenerationId == oldLease.GenerationId);
        var candidateLease = leasesDuringWarmup.Single(value => value.GenerationId != oldLease.GenerationId);
        Assert.Equal(oldLease, oldLeaseDuringWarmup);
        Assert.Equal(harness.ServiceId, candidateLease.ServiceId);
        Assert.Equal(oldLease.NodeId, candidateLease.NodeId);
        Assert.NotEqual(oldLease.Id, candidateLease.Id);
        Assert.NotEqual(oldLease.GenerationId, candidateLease.GenerationId);
        Assert.NotEqual(oldLease.Port, candidateLease.Port);
        Assert.Equal(candidatePort, candidateLease.Port);
        Assert.Equal(1L, candidateLease.Version);
        Assert.True(UuidV7.IsVersion7(candidateLease.GenerationId));
        Assert.True(candidateLease.LeaseExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(harness.ProcessExecutor.IsRunning(oldProcess.InstanceId));
        Assert.DoesNotContain(oldProcess.InstanceId, harness.ProcessExecutor.StoppedProcessInstances);

        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        var warmingEndpoint = Assert.Single(harness.EndpointPublisher.Current).Value;
        Assert.Equal(oldEndpoint, warmingEndpoint);
        Assert.NotEqual(candidateLease.GenerationId, warmingEndpoint.GenerationId);

        harness.HealthProbe.ReleaseCandidate(HealthObservationStatus.Healthy);
        var candidateReadiness = await candidateStartup.WaitAsync(cancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, candidateReadiness.Status);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var activeLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        var activeLease = Assert.Single(activeLeases);
        Assert.Equal(candidateLease, activeLease);
        Assert.DoesNotContain(activeLeases, value => value.GenerationId == oldLease.GenerationId);
        Assert.Equal(oldProcess.InstanceId, Assert.Single(harness.ProcessExecutor.StoppedProcessInstances));
        Assert.False(harness.ProcessExecutor.IsRunning(oldProcess.InstanceId));

        var candidateProcess = Assert.Single(
            harness.ProcessExecutor.StartedProcesses,
            value => value.InstanceId != oldProcess.InstanceId);
        Assert.True(harness.ProcessExecutor.IsRunning(candidateProcess.InstanceId));
        Assert.True(harness.LifecycleManager.TryGet(harness.ServiceId, out var runtimeSnapshot));
        Assert.Equal(candidateSnapshot.Version, runtimeSnapshot.ConfigurationVersion);
        Assert.Equal(ExtensionServiceLifecycleState.Running, runtimeSnapshot.LifecycleState);
        Assert.Equal(ExtensionServiceHealthState.Healthy, runtimeSnapshot.Health);
        Assert.Equal(candidateProcess.ProcessId, runtimeSnapshot.ProcessId);
        Assert.True(runtimeSnapshot.ProcessInstanceId.HasValue);
        Assert.Equal(candidateProcess.InstanceId, runtimeSnapshot.ProcessInstanceId.Value);

        var candidateEndpoint = Assert.Single(harness.EndpointPublisher.Current).Value;
        Assert.Equal(candidateLease.GenerationId, candidateEndpoint.GenerationId);
        Assert.Equal(candidateLease.Port, candidateEndpoint.Port);
        Assert.Equal(candidateLease.LeaseExpiresAt, candidateEndpoint.ExpiresAt);
    }

    /// <summary>Proves a cancelled candidate health observation releases only its own PostgreSQL lease.</summary>
    [Fact]
    public async Task HostGenerationLeaseSwapCancellationKeepsOldGenerationAndEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await GenerationLeasePostgresHarness.CreateAsync(cancellationToken);

        await harness.StartInitialGenerationAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var oldLease = Assert.Single(await harness.ReadLeaseRowsAsync(cancellationToken));
        var oldEndpoint = Assert.Single(harness.EndpointPublisher.Current).Value;
        var oldProcess = Assert.Single(harness.ProcessExecutor.StartedProcesses);

        var candidateSnapshot = await harness.WriteCandidateSnapshotAsync(cancellationToken);
        var candidateStartup = harness.LifecycleManager
            .EnsureReadyAsync(candidateSnapshot, harness.ServiceId, cancellationToken)
            .AsTask();
        var candidatePort = await harness.HealthProbe.CandidateProbeEntered.Task.WaitAsync(cancellationToken);

        Assert.False(candidateStartup.IsCompleted);
        var leasesDuringWarmup = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(2, leasesDuringWarmup.Length);
        var persistedOldLease = leasesDuringWarmup.Single(value => value.GenerationId == oldLease.GenerationId);
        var candidateLease = leasesDuringWarmup.Single(value => value.GenerationId != oldLease.GenerationId);
        Assert.Equal(oldLease, persistedOldLease);
        Assert.Equal(oldLease.NodeId, candidateLease.NodeId);
        Assert.Equal(harness.ServiceId, candidateLease.ServiceId);
        Assert.NotEqual(oldLease.GenerationId, candidateLease.GenerationId);
        Assert.NotEqual(oldLease.Port, candidateLease.Port);
        Assert.Equal(candidatePort, candidateLease.Port);
        Assert.Equal(1L, candidateLease.Version);

        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        Assert.Equal(oldEndpoint, Assert.Single(harness.EndpointPublisher.Current).Value);
        Assert.True(harness.ProcessExecutor.IsRunning(oldProcess.InstanceId));
        Assert.DoesNotContain(oldProcess.InstanceId, harness.ProcessExecutor.StoppedProcessInstances);

        harness.HealthProbe.ReleaseCandidate(HealthObservationStatus.Cancelled);
        var candidateReadiness = await candidateStartup.WaitAsync(cancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Unavailable, candidateReadiness.Status);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var remainingLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(oldLease, Assert.Single(remainingLeases));
        var candidateProcess = Assert.Single(
            harness.ProcessExecutor.StartedProcesses,
            value => value.InstanceId != oldProcess.InstanceId);
        Assert.Contains(candidateProcess.InstanceId, harness.ProcessExecutor.StoppedProcessInstances);
        Assert.DoesNotContain(oldProcess.InstanceId, harness.ProcessExecutor.StoppedProcessInstances);
        Assert.False(harness.ProcessExecutor.IsRunning(candidateProcess.InstanceId));
        Assert.True(harness.ProcessExecutor.IsRunning(oldProcess.InstanceId));

        var retainedEndpoint = Assert.Single(harness.EndpointPublisher.Current).Value;
        Assert.Equal(oldEndpoint, retainedEndpoint);
        Assert.NotEqual(candidateLease.GenerationId, retainedEndpoint.GenerationId);
    }
    /// <summary>Proves a three-service graph remains on one committed PostgreSQL view until every candidate is healthy.</summary>
    [Fact]
    public async Task HostGenerationLeaseGraphKeepsCommittedViewUntilAllCandidatesAreHealthy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await GenerationLeasePostgresHarness.CreateAsync(
            cancellationToken,
            dependencyChain: true);

        await harness.StartInitialGraphAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var serviceIds = harness.ServiceIds;
        var aId = serviceIds[0];
        var bId = serviceIds[1];
        var cId = serviceIds[2];
        var oldLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(3, oldLeases.Length);
        var oldLeasesByService = oldLeases.ToDictionary(value => value.ServiceId);
        var oldProcesses = harness.ProcessExecutor.StartedProcesses;
        Assert.Equal(3, oldProcesses.Length);
        var oldProcessesByService = oldProcesses.ToDictionary(value => value.ServiceId);
        var oldView = Assert.IsType<HostServiceCommittedGraphView>(harness.EndpointPublisher.CommittedView);
        Assert.Equal(harness.InitialSnapshot.Version, oldView.ConfigurationVersion);
        Assert.Equal(3, oldView.Services.Count);
        Assert.Equal(3, oldView.Endpoints.Count);
        Assert.Equal(3, oldView.RuntimeSnapshots.Count);
        Assert.Equal(oldView.Endpoints, harness.EndpointPublisher.Current);

        foreach (var oldLease in oldLeases)
        {
            var endpoint = oldView.Endpoints[oldLease.ServiceId];
            var service = oldView.Services[oldLease.ServiceId];
            Assert.Equal(oldLease.GenerationId, endpoint.GenerationId);
            Assert.Equal(oldLease.Port, endpoint.Port);
            Assert.Equal(endpoint, service.Endpoint);
            Assert.Equal(oldLease.GenerationId, service.GenerationId);
        }
        Assert.Equal(oldLeasesByService[aId].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            oldView.Services[aId].ResolvedEnvironment["SELF_PORT"]);
        Assert.Equal("old-root", oldView.Services[aId].ResolvedEnvironment["ROOT_VALUE"]);
        Assert.Equal("old-root", oldView.Services[bId].ResolvedEnvironment["ROOT_VALUE"]);
        Assert.Equal(oldLeasesByService[aId].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            oldView.Services[bId].ResolvedEnvironment["UPSTREAM_PORT"]);
        Assert.Equal("old-root", oldView.Services[cId].ResolvedEnvironment["ROOT_VALUE"]);
        Assert.Equal(oldLeasesByService[bId].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            oldView.Services[cId].ResolvedEnvironment["UPSTREAM_PORT"]);

        var oldBBinding = oldView.Services[bId].DependencyBindings[aId];
        Assert.Equal(oldLeasesByService[aId].GenerationId, oldBBinding.GenerationId);
        Assert.Equal(oldLeasesByService[aId].Port, oldBBinding.Port);
        Assert.Equal(oldView.Services[aId].ServiceVersion, oldBBinding.ServiceVersion);
        Assert.Equal("old-root", oldBBinding.ResolvedEnvironment["ROOT_VALUE"]);
        var oldCBinding = oldView.Services[cId].DependencyBindings[bId];
        Assert.Equal(oldLeasesByService[bId].GenerationId, oldCBinding.GenerationId);
        Assert.Equal(oldLeasesByService[bId].Port, oldCBinding.Port);
        Assert.Equal(oldView.Services[bId].ServiceVersion, oldCBinding.ServiceVersion);
        Assert.Equal("old-root", oldCBinding.ResolvedEnvironment["ROOT_VALUE"]);
        Assert.All(oldProcesses, value => Assert.True(harness.ProcessExecutor.IsRunning(value.InstanceId)));
        await AssertCommittedGraphReadsAsync(harness, oldView, cancellationToken);

        var candidateSnapshot = await harness.WriteCandidateGraphSnapshotAsync(cancellationToken);
        Assert.Equal(
            checked(harness.InitialSnapshot.Services.Single(value => value.Id == aId).Version + 1),
            candidateSnapshot.Services.Single(value => value.Id == aId).Version);
        Assert.Equal(
            harness.InitialSnapshot.Services.Single(value => value.Id == bId).Version,
            candidateSnapshot.Services.Single(value => value.Id == bId).Version);
        Assert.Equal(
            harness.InitialSnapshot.Services.Single(value => value.Id == cId).Version,
            candidateSnapshot.Services.Single(value => value.Id == cId).Version);
        Assert.All(candidateSnapshot.Services, value => Assert.True(value.Enabled));

        var graphReconcile = harness.LifecycleManager.ReconcileAsync(candidateSnapshot, cancellationToken);
        var candidateAPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(aId, cancellationToken);
        var candidateALeaseWhileBlocked = Assert.Single(
            await harness.ReadLeaseRowsAsync(cancellationToken),
            value => value.ServiceId == aId && value.GenerationId != oldLeasesByService[aId].GenerationId);
        Assert.Equal(candidateAPort, candidateALeaseWhileBlocked.Port);
        harness.HealthProbe.ReleaseCandidate(aId, HealthObservationStatus.Healthy);

        var candidateBPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(bId, cancellationToken);
        var candidateBLeaseWhileBlocked = Assert.Single(
            await harness.ReadLeaseRowsAsync(cancellationToken),
            value => value.ServiceId == bId && value.GenerationId != oldLeasesByService[bId].GenerationId);
        Assert.Equal(candidateBPort, candidateBLeaseWhileBlocked.Port);
        harness.HealthProbe.ReleaseCandidate(bId, HealthObservationStatus.Healthy);

        var candidateCPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(cId, cancellationToken);
        Assert.False(graphReconcile.IsCompleted);
        var leasesDuringWarmup = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(6, leasesDuringWarmup.Length);
        var candidateLeasesByService = serviceIds.ToDictionary(
            serviceId => serviceId,
            serviceId => Assert.Single(
                leasesDuringWarmup,
                value => value.ServiceId == serviceId &&
                    value.GenerationId != oldLeasesByService[serviceId].GenerationId));
        Assert.Equal(candidateAPort, candidateLeasesByService[aId].Port);
        Assert.Equal(candidateBPort, candidateLeasesByService[bId].Port);
        Assert.Equal(candidateCPort, candidateLeasesByService[cId].Port);
        Assert.Equal(6, leasesDuringWarmup.Select(value => value.Port).Distinct().Count());
        foreach (var oldLease in oldLeases)
        {
            Assert.Equal(
                oldLease,
                Assert.Single(leasesDuringWarmup, value => value.GenerationId == oldLease.GenerationId));
        }

        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        Assert.Same(oldView, harness.EndpointPublisher.CommittedView);
        Assert.Equal(oldView.Endpoints, harness.EndpointPublisher.Current);
        await AssertCommittedGraphReadsAsync(harness, oldView, cancellationToken);
        Assert.All(oldProcesses, value => Assert.True(harness.ProcessExecutor.IsRunning(value.InstanceId)));
        Assert.DoesNotContain(oldProcesses, value => harness.ProcessExecutor.StoppedProcessInstances.Contains(value.InstanceId));

        var candidateAProcess = FindProcessForLease(harness, candidateLeasesByService[aId]);
        var candidateBProcess = FindProcessForLease(harness, candidateLeasesByService[bId]);
        var candidateCProcess = FindProcessForLease(harness, candidateLeasesByService[cId]);
        Assert.Equal(CandidateRootLaunchArguments, candidateAProcess.LaunchSpecification.Arguments);
        Assert.Equal("new-root", candidateAProcess.LaunchSpecification.Environment.Values["ROOT_VALUE"]);
        Assert.Equal("new-root", candidateBProcess.LaunchSpecification.Environment.Values["ROOT_VALUE"]);
        Assert.Equal(
            candidateLeasesByService[aId].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidateBProcess.LaunchSpecification.Environment.Values["UPSTREAM_PORT"]);
        Assert.Equal("new-root", candidateCProcess.LaunchSpecification.Environment.Values["ROOT_VALUE"]);
        Assert.Equal(
            candidateLeasesByService[bId].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidateCProcess.LaunchSpecification.Environment.Values["UPSTREAM_PORT"]);

        harness.HealthProbe.ReleaseCandidate(cId, HealthObservationStatus.Healthy);
        await graphReconcile.WaitAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        var activeLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(3, activeLeases.Length);
        Assert.Equal(
            candidateLeasesByService.Values.OrderBy(value => value.ServiceId),
            activeLeases.OrderBy(value => value.ServiceId));
        Assert.DoesNotContain(activeLeases, value => oldLeases.Any(old => old.GenerationId == value.GenerationId));

        var committedView = Assert.IsType<HostServiceCommittedGraphView>(harness.EndpointPublisher.CommittedView);
        Assert.NotSame(oldView, committedView);
        Assert.Equal(candidateSnapshot.Version, committedView.ConfigurationVersion);
        Assert.Equal(3, committedView.Services.Count);
        Assert.Equal(3, committedView.Endpoints.Count);
        Assert.Equal(3, committedView.RuntimeSnapshots.Count);
        Assert.Equal(committedView.Endpoints, harness.EndpointPublisher.Current);
        foreach (var serviceId in serviceIds)
        {
            var lease = candidateLeasesByService[serviceId];
            var service = committedView.Services[serviceId];
            Assert.Equal(lease.GenerationId, service.GenerationId);
            Assert.Equal(lease.Port, service.Endpoint.Port);
            Assert.Equal(committedView.Endpoints[serviceId], service.Endpoint);
            Assert.Equal(candidateSnapshot.Services.Single(value => value.Id == serviceId).Version, service.ServiceVersion);
            Assert.Equal("new-root", service.ResolvedEnvironment["ROOT_VALUE"]);
            Assert.Equal(committedView.RuntimeSnapshots[serviceId], service.Runtime);
            Assert.Equal(candidateSnapshot.Version, service.Runtime.ConfigurationVersion);
            Assert.Equal(ExtensionServiceLifecycleState.Running, service.Runtime.LifecycleState);
            Assert.Equal(ExtensionServiceHealthState.Healthy, service.Runtime.Health);
        }

        var bBinding = committedView.Services[bId].DependencyBindings[aId];
        Assert.Equal(candidateLeasesByService[aId].GenerationId, bBinding.GenerationId);
        Assert.Equal(candidateLeasesByService[aId].Port, bBinding.Port);
        Assert.Equal(committedView.Services[aId].ServiceVersion, bBinding.ServiceVersion);
        Assert.Equal("new-root", bBinding.ResolvedEnvironment["ROOT_VALUE"]);
        var cBinding = committedView.Services[cId].DependencyBindings[bId];
        Assert.Equal(candidateLeasesByService[bId].GenerationId, cBinding.GenerationId);
        Assert.Equal(candidateLeasesByService[bId].Port, cBinding.Port);
        Assert.Equal(committedView.Services[bId].ServiceVersion, cBinding.ServiceVersion);
        Assert.Equal("new-root", cBinding.ResolvedEnvironment["ROOT_VALUE"]);
        await AssertCommittedGraphReadsAsync(harness, committedView, cancellationToken);

        var oldReleaseOrder = harness.SuccessfulLeaseReleases
            .Where(value => oldLeases.Any(old => old.ServiceId == value.ServiceId && old.GenerationId == value.GenerationId))
            .Select(value => (value.ServiceId, value.GenerationId))
            .ToArray();
        Assert.Equal(
            new[]
            {
                (cId, oldLeasesByService[cId].GenerationId),
                (bId, oldLeasesByService[bId].GenerationId),
                (aId, oldLeasesByService[aId].GenerationId)
            },
            oldReleaseOrder);
        Assert.Equal(
            new[]
            {
                oldProcessesByService[cId].InstanceId,
                oldProcessesByService[bId].InstanceId,
                oldProcessesByService[aId].InstanceId
            },
            harness.ProcessExecutor.StoppedProcessInstances);
        Assert.All(oldProcesses, value => Assert.False(harness.ProcessExecutor.IsRunning(value.InstanceId)));
        Assert.All(candidateLeasesByService.Keys, serviceId =>
            Assert.True(harness.ProcessExecutor.IsRunning(FindProcessForLease(harness, candidateLeasesByService[serviceId]).InstanceId)));

        var persistedAfterCommit = await harness.ReadPersistedSnapshotAsync(cancellationToken);
        Assert.Equal(
            harness.InitialSnapshot.Services.Single(value => value.Id == bId).Version,
            persistedAfterCommit.Services.Single(value => value.Id == bId).Version);
        Assert.Equal(
            harness.InitialSnapshot.Services.Single(value => value.Id == cId).Version,
            persistedAfterCommit.Services.Single(value => value.Id == cId).Version);
    }

    /// <summary>Proves a candidate B health failure rolls back the complete candidate prefix without publishing it.</summary>
    [Fact]
    public async Task HostGenerationLeaseGraphBHealthFailureRollsBackCandidatesWithoutPublishingPartialState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await GenerationLeasePostgresHarness.CreateAsync(
            cancellationToken,
            dependencyChain: true);

        await harness.StartInitialGraphAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        var serviceIds = harness.ServiceIds;
        var aId = serviceIds[0];
        var bId = serviceIds[1];
        var oldLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        var oldLeasesByService = oldLeases.ToDictionary(value => value.ServiceId);
        var oldProcesses = harness.ProcessExecutor.StartedProcesses;
        var oldView = Assert.IsType<HostServiceCommittedGraphView>(harness.EndpointPublisher.CommittedView);
        var candidateSnapshot = await harness.WriteCandidateGraphSnapshotAsync(cancellationToken);
        var graphReconcile = harness.LifecycleManager.ReconcileAsync(candidateSnapshot, cancellationToken);

        var candidateAPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(aId, cancellationToken);
        harness.HealthProbe.ReleaseCandidate(aId, HealthObservationStatus.Healthy);
        var candidateBPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(bId, cancellationToken);
        Assert.False(graphReconcile.IsCompleted);
        var leasesDuringFailure = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(5, leasesDuringFailure.Length);
        var candidateLeasesByService = new[] { aId, bId }.ToDictionary(
            serviceId => serviceId,
            serviceId => Assert.Single(
                leasesDuringFailure,
                value => value.ServiceId == serviceId &&
                    value.GenerationId != oldLeasesByService[serviceId].GenerationId));
        Assert.Equal(candidateAPort, candidateLeasesByService[aId].Port);
        Assert.Equal(candidateBPort, candidateLeasesByService[bId].Port);

        await harness.PublishDatabaseEndpointsAsync(cancellationToken);

        Assert.Same(oldView, harness.EndpointPublisher.CommittedView);
        Assert.Equal(oldView.Endpoints, harness.EndpointPublisher.Current);
        await AssertCommittedGraphReadsAsync(harness, oldView, cancellationToken);
        Assert.All(oldProcesses, value => Assert.True(harness.ProcessExecutor.IsRunning(value.InstanceId)));

        harness.HealthProbe.ReleaseCandidate(
            bId,
            HealthObservationStatus.Unhealthy,
            observedAt: DateTimeOffset.MaxValue);
        await graphReconcile.WaitAsync(cancellationToken);
        await AssertOldGraphRetainedAsync(harness, oldView, oldLeases, oldProcesses, cancellationToken);

        var candidateProcesses = harness.ProcessExecutor.StartedProcesses
            .Where(value => value.ServiceId == aId || value.ServiceId == bId)
            .Where(value => !oldProcesses.Any(old => old.InstanceId == value.InstanceId))
            .ToArray();
        Assert.Equal(2, candidateProcesses.Length);
        Assert.All(candidateProcesses, value => Assert.Contains(value.InstanceId, harness.ProcessExecutor.StoppedProcessInstances));
        Assert.Equal(5, harness.ProcessExecutor.StartedProcesses.Length);
        Assert.Equal(
            new[]
            {
                (bId, candidateLeasesByService[bId].GenerationId),
                (aId, candidateLeasesByService[aId].GenerationId)
            },
            harness.SuccessfulLeaseReleases.Select(value => (value.ServiceId, value.GenerationId)).ToArray());
    }

    /// <summary>Proves cancelling candidate C rolls back the already-ready candidate prefix without a mixed graph.</summary>
    [Fact]
    public async Task HostGenerationLeaseGraphCandidateCancellationRollsBackWithoutPublishingPartialState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await GenerationLeasePostgresHarness.CreateAsync(
            cancellationToken,
            dependencyChain: true);

        await harness.StartInitialGraphAsync(cancellationToken);
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        var serviceIds = harness.ServiceIds;
        var aId = serviceIds[0];
        var bId = serviceIds[1];
        var cId = serviceIds[2];
        var oldLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        var oldLeasesByService = oldLeases.ToDictionary(value => value.ServiceId);
        var oldProcesses = harness.ProcessExecutor.StartedProcesses;
        var oldView = Assert.IsType<HostServiceCommittedGraphView>(harness.EndpointPublisher.CommittedView);
        var candidateSnapshot = await harness.WriteCandidateGraphSnapshotAsync(cancellationToken);
        var graphReconcile = harness.LifecycleManager.ReconcileAsync(candidateSnapshot, cancellationToken);

        var candidateAPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(aId, cancellationToken);
        harness.HealthProbe.ReleaseCandidate(aId, HealthObservationStatus.Healthy);
        var candidateBPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(bId, cancellationToken);
        harness.HealthProbe.ReleaseCandidate(bId, HealthObservationStatus.Healthy);
        var candidateCPort = await harness.HealthProbe.WaitForCandidateProbeEnteredAsync(cId, cancellationToken);
        Assert.False(graphReconcile.IsCompleted);
        var leasesDuringCancellation = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(6, leasesDuringCancellation.Length);
        var candidateLeasesByService = serviceIds.ToDictionary(
            serviceId => serviceId,
            serviceId => Assert.Single(
                leasesDuringCancellation,
                value => value.ServiceId == serviceId &&
                    value.GenerationId != oldLeasesByService[serviceId].GenerationId));
        Assert.Equal(candidateAPort, candidateLeasesByService[aId].Port);
        Assert.Equal(candidateBPort, candidateLeasesByService[bId].Port);
        Assert.Equal(candidateCPort, candidateLeasesByService[cId].Port);

        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        Assert.Same(oldView, harness.EndpointPublisher.CommittedView);
        await AssertCommittedGraphReadsAsync(harness, oldView, cancellationToken);

        harness.HealthProbe.ReleaseCandidate(cId, HealthObservationStatus.Cancelled);
        await graphReconcile.WaitAsync(cancellationToken);
        await AssertOldGraphRetainedAsync(harness, oldView, oldLeases, oldProcesses, cancellationToken);
        Assert.False(cancellationToken.IsCancellationRequested);

        var candidateProcesses = harness.ProcessExecutor.StartedProcesses
            .Where(value => !oldProcesses.Any(old => old.InstanceId == value.InstanceId))
            .ToArray();
        Assert.Equal(3, candidateProcesses.Length);
        Assert.All(candidateProcesses, value => Assert.Contains(value.InstanceId, harness.ProcessExecutor.StoppedProcessInstances));
        Assert.Equal(
            new[]
            {
                (cId, candidateLeasesByService[cId].GenerationId),
                (bId, candidateLeasesByService[bId].GenerationId),
                (aId, candidateLeasesByService[aId].GenerationId)
            },
            harness.SuccessfulLeaseReleases.Select(value => (value.ServiceId, value.GenerationId)).ToArray());
    }

    private static RecordedGenerationProcess FindProcessForLease(
        GenerationLeasePostgresHarness harness,
        PersistedPortLeaseSnapshot lease) =>
        Assert.Single(
            harness.ProcessExecutor.StartedProcesses,
            value => value.ServiceId == lease.ServiceId &&
                value.LaunchSpecification.Environment.Values["SELF_PORT"] ==
                    lease.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task AssertCommittedGraphReadsAsync(
        GenerationLeasePostgresHarness harness,
        HostServiceCommittedGraphView view,
        CancellationToken cancellationToken)
    {
        Assert.Equal(view.Services.Count, view.Endpoints.Count);
        Assert.Equal(view.Services.Count, view.RuntimeSnapshots.Count);
        foreach (var pair in view.Services)
        {
            Assert.Equal(pair.Value.Endpoint, view.Endpoints[pair.Key]);
            Assert.Equal(pair.Value.Runtime, view.RuntimeSnapshots[pair.Key]);
        }

        var lifecycleQueries = harness.LifecycleManager.ReadCurrent();
        var registryQueries = harness.RuntimeRegistry.ReadCurrent();
        Assert.Equal(view.RuntimeSnapshots.Count, lifecycleQueries.Length);
        Assert.Equal(view.RuntimeSnapshots.Count, registryQueries.Length);
        foreach (var pair in view.RuntimeSnapshots)
        {
            Assert.Equal(pair.Value, Assert.Single(lifecycleQueries, value => value.ServiceId == pair.Key));
            Assert.Equal(pair.Value, Assert.Single(registryQueries, value => value.ServiceId == pair.Key));
        }

        var endpointResolver = new HostServiceEndpointResolver(harness.EndpointPublisher);
        foreach (var pair in view.Endpoints)
        {
            var resolution = await endpointResolver.ResolveAsync(pair.Key, cancellationToken);
            Assert.True(resolution.IsAvailable);
            Assert.NotNull(resolution.Endpoint);
            Assert.Equal(pair.Value.Port, resolution.Endpoint!.BaseUri.Port);
        }
    }

    private static async Task AssertOldGraphRetainedAsync(
        GenerationLeasePostgresHarness harness,
        HostServiceCommittedGraphView oldView,
        ImmutableArray<PersistedPortLeaseSnapshot> oldLeases,
        RecordedGenerationProcess[] oldProcesses,
        CancellationToken cancellationToken)
    {
        await harness.PublishDatabaseEndpointsAsync(cancellationToken);
        Assert.Same(oldView, harness.EndpointPublisher.CommittedView);
        Assert.Equal(oldView.Endpoints, harness.EndpointPublisher.Current);
        await AssertCommittedGraphReadsAsync(harness, oldView, cancellationToken);
        var remainingLeases = await harness.ReadLeaseRowsAsync(cancellationToken);
        Assert.Equal(oldLeases.OrderBy(value => value.ServiceId), remainingLeases.OrderBy(value => value.ServiceId));
        Assert.Equal(3, remainingLeases.Length);
        Assert.All(oldProcesses, value => Assert.True(harness.ProcessExecutor.IsRunning(value.InstanceId)));
        Assert.DoesNotContain(oldProcesses, value => harness.ProcessExecutor.StoppedProcessInstances.Contains(value.InstanceId));
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

    private static async Task InvokeEndpointPublicationTickAsync(
        HostServiceEndpointPublicationService publicationService,
        CancellationToken cancellationToken)
    {
        var publishMethod = typeof(HostServiceEndpointPublicationService).GetMethod(
            "PublishAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(publishMethod);

        var invocation = publishMethod!.Invoke(
            publicationService,
            new object[] { cancellationToken });
        await Assert.IsAssignableFrom<Task>(invocation);
    }

    private sealed class GenerationLeasePostgresHarness : IAsyncDisposable
    {
        private readonly PostgresConfigurationTestScope _scope;
        private readonly NekostickDbContext _leaseContext;
        private readonly EfPortLeaseStore _persistenceLeaseStore;
        private readonly RecordingPostgresLeaseStore _recordingLeaseStore;
        private readonly HostServiceRuntimeRegistry _runtimeRegistry;
        private readonly HostServiceEndpointPublicationService _endpointPublicationService;
        private readonly EfHostConfigurationSnapshotReader _snapshotReader;
        private readonly HostConfigurationSnapshotHolder _snapshotHolder;
        private readonly HostRuntimeState _runtimeState;
        private readonly ImmutableArray<Guid> _serviceIds;
        private readonly string _nodeId;
        private readonly string _dataDirectory;
        private int _disposed;
        private GenerationLeasePostgresHarness(
            PostgresConfigurationTestScope scope,
            NekostickDbContext leaseContext,
            EfPortLeaseStore persistenceLeaseStore,
            RecordingPostgresLeaseStore recordingLeaseStore,
            HostServiceRuntimeRegistry runtimeRegistry,
            HostServiceEndpointPublicationService endpointPublicationService,
            EfHostConfigurationSnapshotReader snapshotReader,
            HostConfigurationSnapshotHolder snapshotHolder,
            HostRuntimeState runtimeState,
            HostConfigurationSnapshot initialSnapshot,
            ImmutableArray<Guid> serviceIds,
            string nodeId,
            HostServiceLifecycleManager lifecycleManager,
            HostServiceEndpointSnapshotPublisher endpointPublisher,
            RecordingGenerationProcessExecutor processExecutor,
            ControlledGenerationHealthProbe healthProbe,
            string dataDirectory)
        {
            _scope = scope;
            _leaseContext = leaseContext;
            _persistenceLeaseStore = persistenceLeaseStore;
            _recordingLeaseStore = recordingLeaseStore;
            _runtimeRegistry = runtimeRegistry;
            _endpointPublicationService = endpointPublicationService;
            _snapshotReader = snapshotReader;
            _snapshotHolder = snapshotHolder;
            _runtimeState = runtimeState;
            InitialSnapshot = initialSnapshot;
            _serviceIds = serviceIds;
            _nodeId = nodeId;
            LifecycleManager = lifecycleManager;
            EndpointPublisher = endpointPublisher;
            ProcessExecutor = processExecutor;
            HealthProbe = healthProbe;
            _dataDirectory = dataDirectory;
        }

        internal HostConfigurationSnapshot InitialSnapshot { get; }
        internal ImmutableArray<Guid> ServiceIds => _serviceIds;
        internal Guid ServiceId => _serviceIds[0];
        internal HostServiceLifecycleManager LifecycleManager { get; }
        internal HostServiceEndpointSnapshotPublisher EndpointPublisher { get; }
        internal HostServiceRuntimeRegistry RuntimeRegistry => _runtimeRegistry;
        internal (Guid ServiceId, Guid GenerationId, int Port)[] SuccessfulLeaseReleases =>
            _recordingLeaseStore.SuccessfulReleases;
        internal RecordingGenerationProcessExecutor ProcessExecutor { get; }
        internal ControlledGenerationHealthProbe HealthProbe { get; }

        internal static async Task<GenerationLeasePostgresHarness> CreateAsync(
            CancellationToken cancellationToken,
            bool dependencyChain = false)
        {
            var scope = await PostgresConfigurationTestScope.CreateAsync();
            NekostickDbContext? leaseContext = null;
            EfPortLeaseStore? persistenceLeaseStore = null;
            HostServiceLifecycleManager? lifecycleManager = null;
            HostServiceRuntimeRegistry? runtimeRegistry = null;
            HostServiceEndpointPublicationService? endpointPublicationService = null;
            string? dataDirectory = null;
            try
            {
                var initialRead = await scope.Api.ReadSnapshotAsync(cancellationToken);
                Assert.True(initialRead.IsSuccess, initialRead.Errors.FirstOrDefault()?.Message);
                Assert.NotNull(initialRead.Value);
                var initialSnapshot = initialRead.Value!;
                var serviceIds = dependencyChain
                    ? ImmutableArray.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7())
                    : ImmutableArray.Create(Guid.CreateVersion7());

                dataDirectory = Path.Combine(
                    "/tmp",
                    "nekostick-generation-lease-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataDirectory);
                var executablePath = Path.Combine(dataDirectory, "fixture-service");
                await File.WriteAllBytesAsync(executablePath, Array.Empty<byte>(), cancellationToken);

                var createdAt = DateTimeOffset.UtcNow;
                var services = dependencyChain
                    ? CreateGenerationLeaseChainServices(serviceIds, executablePath, createdAt)
                    : ImmutableArray.Create(CreateGenerationLeaseService(
                        serviceIds[0],
                        executablePath,
                        Path.GetDirectoryName(executablePath)!,
                        createdAt,
                        createdAt,
                        version: 0,
                        argumentList: ImmutableArray<string>.Empty));
                var seedWrite = await scope.Api.WriteSnapshotAsync(
                    initialSnapshot.Version,
                    new ConfigurationChangeSet(
                        initialSnapshot.GlobalSettings,
                        initialSnapshot.Routes,
                        initialSnapshot.Services.AddRange(services),
                        initialSnapshot.ExtensionRecords,
                        initialSnapshot.ExtensionSettings),
                    cancellationToken);
                Assert.True(seedWrite.IsSuccess, seedWrite.Errors.FirstOrDefault()?.Message);
                Assert.Equal(checked(initialSnapshot.Version + 1), seedWrite.NewVersion);

                var snapshotReader = new EfHostConfigurationSnapshotReader(
                    new TestDbContextFactory(scope.Database));
                var seedRead = await snapshotReader.ReadCompleteAsync(cancellationToken);
                Assert.True(seedRead.IsSuccess, seedRead.Errors.FirstOrDefault()?.Message);
                Assert.NotNull(seedRead.Value);
                var persistedSnapshot = seedRead.Value!;
                Assert.Equal(seedWrite.NewVersion, persistedSnapshot.Version);
                Assert.True(persistedSnapshot.Version > 0);
                Assert.Equal(serviceIds.Length, persistedSnapshot.Services.Length);
                foreach (var serviceId in serviceIds)
                {
                    var persistedService = Assert.Single(persistedSnapshot.Services, value => value.Id == serviceId);
                    Assert.True(persistedService.Enabled);
                    Assert.Equal(1L, persistedService.Version);
                }

                var nodeId = "generation-lease-" + Guid.NewGuid().ToString("N");
                var runtimeOptions = new HostRuntimeOptions(
                    IntegrationTestBoundary.RequirePostgresConnectionString(),
                    nodeId,
                    readOnly: false);
                await using (var nodeContext = scope.Database.CreateContext())
                {
                    var now = DateTimeOffset.UtcNow;
                    nodeContext.Nodes.Add(new Node
                    {
                        Id = Guid.CreateVersion7(),
                        NodeId = nodeId,
                        LastHeartbeatAt = now,
                        LastConfigurationVersion = persistedSnapshot.Version,
                        RuntimeState = "ready",
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Version = 1
                    });
                    await nodeContext.SaveChangesAsync(cancellationToken);

                    var persistedNode = await nodeContext.Nodes.AsNoTracking()
                        .SingleAsync(value => value.NodeId == nodeId, cancellationToken);
                    Assert.True(persistedNode.IsActive);
                    Assert.Equal(persistedSnapshot.Version, persistedNode.LastConfigurationVersion);
                }

                var snapshotHolder = new HostConfigurationSnapshotHolder();
                Assert.True(snapshotHolder.TryReplace(persistedSnapshot));
                var nodeOptions = new HostNodeOptions(
                    skipExtensions: true,
                    disableSupervisor: true,
                    readOnly: false,
                    dataDirectory: dataDirectory!);
                var runtimeState = new HostRuntimeState(snapshotHolder, nodeOptions);
                runtimeState.MarkSnapshotAccepted();

                var processExecutor = new RecordingGenerationProcessExecutor();
                var healthProbe = new ControlledGenerationHealthProbe(serviceIds);
                var endpointPublisher = new HostServiceEndpointSnapshotPublisher();
                runtimeRegistry = new HostServiceRuntimeRegistry();
                leaseContext = scope.Database.CreateContext();
                persistenceLeaseStore = new EfPortLeaseStore(
                    leaseContext,
                    new FixedLeaseTimeProvider(DateTimeOffset.UtcNow));
                var adapter = new HostPortLeaseStoreAdapter(persistenceLeaseStore, runtimeState);
                var recordingLeaseStore = new RecordingPostgresLeaseStore(adapter);
                lifecycleManager = new HostServiceLifecycleManager(
                    processExecutor,
                    healthProbe,
                    recordingLeaseStore,
                    snapshotHolder,
                    endpointPublisher,
                    runtimeState,
                    runtimeOptions,
                    NullLogger<HostServiceLifecycleManager>.Instance,
                    new Nekolla.Nekostick.Proxy.MicroserviceDrainTracker(),
                    nodeOptions,
                    runtimeManager: null,
                    runtimeRegistry: runtimeRegistry,
                    admissionCoordinator: new Nekolla.Nekostick.Proxy.MicroserviceAdmissionCoordinator());
                endpointPublicationService = new HostServiceEndpointPublicationService(
                    new TestDbContextFactory(scope.Database),
                    lifecycleManager,
                    runtimeOptions);

                return new GenerationLeasePostgresHarness(
                    scope,
                    leaseContext,
                    persistenceLeaseStore,
                    recordingLeaseStore,
                    runtimeRegistry,
                    endpointPublicationService,
                    snapshotReader,
                    snapshotHolder,
                    runtimeState,
                    persistedSnapshot,
                    serviceIds,
                    nodeId,
                    lifecycleManager,
                    endpointPublisher,
                    processExecutor,
                    healthProbe,
                    dataDirectory);
            }
            catch
            {
                if (lifecycleManager is not null)
                {
                    await lifecycleManager.StopAsync(CancellationToken.None);
                }

                endpointPublicationService?.Dispose();
                if (runtimeRegistry is not null)
                {
                    await runtimeRegistry.DisposeAsync();
                }

                if (persistenceLeaseStore is not null)
                {
                    await persistenceLeaseStore.DisposeAsync();
                }

                if (leaseContext is not null)
                {
                    await leaseContext.DisposeAsync();
                }

                await scope.DisposeAsync();
                if (dataDirectory is not null && Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }

                throw;
            }
        }

        internal async Task StartInitialGenerationAsync(CancellationToken cancellationToken)
        {
            var readiness = await LifecycleManager.EnsureReadyAsync(
                InitialSnapshot,
                ServiceId,
                cancellationToken);
            Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        }
        internal async Task StartInitialGraphAsync(CancellationToken cancellationToken)
        {
            await LifecycleManager.ReconcileAsync(InitialSnapshot, cancellationToken);
            foreach (var serviceId in ServiceIds)
            {
                Assert.True(LifecycleManager.TryGet(serviceId, out var runtimeSnapshot));
                Assert.Equal(ExtensionServiceLifecycleState.Running, runtimeSnapshot.LifecycleState);
                Assert.Equal(ExtensionServiceHealthState.Healthy, runtimeSnapshot.Health);
            }
        }

        internal async Task<HostConfigurationSnapshot> WriteCandidateGraphSnapshotAsync(
            CancellationToken cancellationToken)
        {
            var currentRead = await _scope.Api.ReadSnapshotAsync(cancellationToken);
            Assert.True(currentRead.IsSuccess, currentRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(currentRead.Value);
            var current = currentRead.Value!;
            var currentRoot = current.Services.Single(value => value.Id == ServiceIds[0]);
            var updatedRoot = CreateGenerationLeaseService(
                currentRoot.Id,
                currentRoot.FileName,
                currentRoot.WorkingDirectory,
                currentRoot.CreatedAt,
                currentRoot.UpdatedAt.AddSeconds(1),
                currentRoot.Version,
                ImmutableArray.Create("--root", "new-root"),
                ImmutableDictionary<string, string>.Empty
                    .Add("ROOT_VALUE", "new-root")
                    .Add("SELF_PORT", "${PORT}"));
            var changes = new ConfigurationChangeSet(
                current.GlobalSettings,
                current.Routes,
                current.Services.Select(value => value.Id == currentRoot.Id ? updatedRoot : value).ToImmutableArray(),
                current.ExtensionRecords,
                current.ExtensionSettings);
            var write = await _scope.Api.WriteSnapshotAsync(current.Version, changes, cancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
            Assert.Equal(checked(current.Version + 1), write.NewVersion);

            var candidateRead = await _snapshotReader.ReadCompleteAsync(cancellationToken);
            Assert.True(candidateRead.IsSuccess, candidateRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(candidateRead.Value);
            var candidate = candidateRead.Value!;
            Assert.Equal(write.NewVersion, candidate.Version);
            foreach (var serviceId in ServiceIds)
            {
                var currentService = current.Services.Single(value => value.Id == serviceId);
                var candidateService = candidate.Services.Single(value => value.Id == serviceId);
                Assert.Equal(
                    serviceId == currentRoot.Id ? checked(currentService.Version + 1) : currentService.Version,
                    candidateService.Version);
            }

            Assert.True(_snapshotHolder.TryReplace(candidate));
            _runtimeState.MarkSnapshotAccepted();
            return candidate;
        }

        internal async Task<HostConfigurationSnapshot> ReadPersistedSnapshotAsync(
            CancellationToken cancellationToken)
        {
            var read = await _snapshotReader.ReadCompleteAsync(cancellationToken);
            Assert.True(read.IsSuccess, read.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(read.Value);
            return read.Value!;
        }

        internal async Task<HostConfigurationSnapshot> WriteCandidateSnapshotAsync(
            CancellationToken cancellationToken)
        {
            var currentRead = await _scope.Api.ReadSnapshotAsync(cancellationToken);
            Assert.True(currentRead.IsSuccess, currentRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(currentRead.Value);
            var current = currentRead.Value!;
            var currentService = current.Services.Single(value => value.Id == ServiceId);
            var updatedService = CreateGenerationLeaseService(
                currentService.Id,
                currentService.FileName,
                currentService.WorkingDirectory,
                currentService.CreatedAt,
                currentService.UpdatedAt.AddSeconds(1),
                currentService.Version,
                currentService.ArgumentList.Add("candidate"));
            var changes = new ConfigurationChangeSet(
                current.GlobalSettings,
                current.Routes,
                current.Services.Select(value => value.Id == ServiceId ? updatedService : value).ToImmutableArray(),
                current.ExtensionRecords,
                current.ExtensionSettings);
            var write = await _scope.Api.WriteSnapshotAsync(current.Version, changes, cancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
            Assert.Equal(checked(current.Version + 1), write.NewVersion);

            var candidateRead = await _snapshotReader.ReadCompleteAsync(cancellationToken);
            Assert.True(candidateRead.IsSuccess, candidateRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(candidateRead.Value);
            var candidate = candidateRead.Value!;
            Assert.Equal(write.NewVersion, candidate.Version);
            Assert.Equal(
                checked(currentService.Version + 1),
                candidate.Services.Single(value => value.Id == ServiceId).Version);
            Assert.True(_snapshotHolder.TryReplace(candidate));
            _runtimeState.MarkSnapshotAccepted();
            return candidate;
        }

        internal async Task<ImmutableArray<PersistedPortLeaseSnapshot>> ReadLeaseRowsAsync(
            CancellationToken cancellationToken)
        {
            var serviceIds = _serviceIds.ToArray();
            await using var context = _scope.Database.CreateContext();
            var leases = await context.PortLeases.AsNoTracking()
                .Where(value => value.NodeId == _nodeId && serviceIds.Contains(value.ServiceId))
                .OrderBy(value => value.ServiceId)
                .ThenBy(value => value.GenerationId)
                .ToListAsync(cancellationToken);
            return leases.Select(PersistedPortLeaseSnapshot.FromEntity).ToImmutableArray();
        }

        internal Task PublishDatabaseEndpointsAsync(CancellationToken cancellationToken) =>
            InvokeEndpointPublicationTickAsync(_endpointPublicationService, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await LifecycleManager.StopAsync(CancellationToken.None);
            }
            finally
            {
                _endpointPublicationService.Dispose();
                await _runtimeRegistry.DisposeAsync();
                await _persistenceLeaseStore.DisposeAsync();
                await _leaseContext.DisposeAsync();
                await _scope.DisposeAsync();
                if (Directory.Exists(_dataDirectory))
                {
                    Directory.Delete(_dataDirectory, recursive: true);
                }
            }
        }

        private static ImmutableArray<ServiceConfiguration> CreateGenerationLeaseChainServices(
            ImmutableArray<Guid> serviceIds,
            string executablePath,
            DateTimeOffset createdAt)
        {
            var aId = serviceIds[0];
            var bId = serviceIds[1];
            var workingDirectory = Path.GetDirectoryName(executablePath)!;
            static string Remote(string name, Guid serviceId) =>
                string.Concat("${", name, "@", serviceId.ToString("D"), "}");

            var a = CreateGenerationLeaseService(
                aId,
                executablePath,
                workingDirectory,
                createdAt,
                createdAt,
                version: 0,
                argumentList: ImmutableArray.Create("--root", "old-root"),
                environment: ImmutableDictionary<string, string>.Empty
                    .Add("ROOT_VALUE", "old-root")
                    .Add("SELF_PORT", "${PORT}"));
            var bRoot = Remote("ROOT_VALUE", aId);
            var bPort = Remote("SELF_PORT", aId);
            var b = CreateGenerationLeaseService(
                bId,
                executablePath,
                workingDirectory,
                createdAt,
                createdAt,
                version: 0,
                argumentList: ImmutableArray.Create("--root", bRoot),
                environment: ImmutableDictionary<string, string>.Empty
                    .Add("ROOT_VALUE", bRoot)
                    .Add("UPSTREAM_PORT", bPort)
                    .Add("SELF_PORT", "${PORT}"));
            var cRoot = Remote("ROOT_VALUE", bId);
            var cPort = Remote("SELF_PORT", bId);
            var c = CreateGenerationLeaseService(
                serviceIds[2],
                executablePath,
                workingDirectory,
                createdAt,
                createdAt,
                version: 0,
                argumentList: ImmutableArray.Create("--root", cRoot),
                environment: ImmutableDictionary<string, string>.Empty
                    .Add("ROOT_VALUE", cRoot)
                    .Add("UPSTREAM_PORT", cPort)
                    .Add("SELF_PORT", "${PORT}"));
            return ImmutableArray.Create(a, b, c);
        }

        private static ServiceConfiguration CreateGenerationLeaseService(
            Guid serviceId,
            string executablePath,
            string workingDirectory,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            long version,
            ImmutableArray<string> argumentList,
            ImmutableDictionary<string, string>? environment = null) =>
            new(
                serviceId,
                enabled: true,
                fileName: executablePath,
                argumentList: argumentList,
                workingDirectory: workingDirectory,
                environment: environment ?? ImmutableDictionary<string, string>.Empty,
                startMode: ServiceStartMode.Eager,
                restartPolicy: Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
                healthCheck: new ServiceHealthCheckConfiguration(
                    ServiceHealthCheckType.Process,
                    httpPath: null,
                    timeout: TimeSpan.FromSeconds(1)),
                createdAt: createdAt,
                updatedAt: updatedAt,
                version: version);

    }

    private sealed record PersistedPortLeaseSnapshot(
        Guid Id,
        string NodeId,
        Guid ServiceId,
        Guid GenerationId,
        int Port,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset RenewedAt,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static PersistedPortLeaseSnapshot FromEntity(Nekolla.Nekostick.Persistence.Entities.PortLease lease) =>
            new(
                lease.Id,
                lease.NodeId,
                lease.ServiceId,
                lease.GenerationId,
                lease.Port,
                lease.LeaseExpiresAt,
                lease.RenewedAt,
                lease.Version,
                lease.CreatedAt,
                lease.UpdatedAt);
    }

    private sealed class RecordingPostgresLeaseStore : IPortLeaseStore
    {
        private readonly IPortLeaseStore _inner;
        private readonly ConcurrentQueue<RecordedLeaseOperation> _operations = new();
        private long _sequence;

        internal RecordingPostgresLeaseStore(IPortLeaseStore inner) =>
            _inner = inner;

        internal (Guid ServiceId, Guid GenerationId, int Port)[] SuccessfulReleases => _operations
            .OrderBy(value => value.Sequence)
            .Where(value => value.Intent.Kind == PortLeaseIntentKind.Release &&
                value.Result.Status == PortLeaseOperationStatus.Applied)
            .Select(value => (
                ServiceId: value.Intent.Release!.ServiceId,
                GenerationId: value.Intent.Release!.GenerationId,
                Port: value.Intent.Release!.Port))
            .ToArray();

        public async ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.ApplyAsync(intent, cancellationToken).ConfigureAwait(false);
            _operations.Enqueue(new RecordedLeaseOperation(
                Interlocked.Increment(ref _sequence),
                intent,
                result));
            return result;
        }

        private sealed record RecordedLeaseOperation(
            long Sequence,
            PortLeaseIntent Intent,
            PortLeaseOperationResult Result);
    }

    private sealed record RecordedGenerationProcess(
        Guid ServiceId,
        ProcessInstanceId InstanceId,
        int ProcessId,
        ProcessLaunchSpecification LaunchSpecification);

    private sealed class RecordingGenerationProcessExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        private readonly ConcurrentDictionary<ProcessInstanceId, RecordedGenerationProcess> _running = new();
        private readonly ConcurrentQueue<RecordedGenerationProcess> _started = new();
        private readonly ConcurrentQueue<ProcessInstanceId> _stopped = new();
        private int _nextProcessId = 31_000;

        internal RecordedGenerationProcess[] StartedProcesses => _started.ToArray();
        internal ProcessInstanceId[] StoppedProcessInstances => _stopped.ToArray();

        internal bool IsRunning(ProcessInstanceId instanceId) => _running.ContainsKey(instanceId);
        bool IProcessLiveness.IsRunning(Guid serviceId) =>
            _running.Values.Any(value => value.ServiceId == serviceId);

        bool IProcessLiveness.IsRunning(Guid serviceId, ProcessInstanceId instanceId) =>
            _running.TryGetValue(instanceId, out var process) && process.ServiceId == serviceId;

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = new RecordedGenerationProcess(
                specification.ServiceId,
                new ProcessInstanceId(Guid.CreateVersion7()),
                Interlocked.Increment(ref _nextProcessId),
                specification);
            if (!_running.TryAdd(process.InstanceId, process))
            {
                throw new InvalidOperationException("The recording executor produced a duplicate process identity.");
            }

            _started.Enqueue(process);
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted,
                process.InstanceId,
                process.ProcessId,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var process in _running.Values.Where(value => value.ServiceId == serviceId))
            {
                Stop(process.InstanceId);
            }

            return ValueTask.FromResult(StoppedResult());
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            ProcessInstanceId instanceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stop(instanceId);
            return ValueTask.FromResult(StoppedResult());
        }

        private void Stop(ProcessInstanceId instanceId)
        {
            if (_running.TryRemove(instanceId, out _))
            {
                _stopped.Enqueue(instanceId);
            }
        }

        private static ProcessOperationResult StoppedResult() => new(
            ProcessOperationStatus.Completed,
            ServiceStateReasonCode.StopCompleted);
    }

    private sealed class ControlledGenerationHealthProbe : IServiceHealthProbe
    {
        private readonly ImmutableArray<Guid> _serviceIds;
        private readonly ConcurrentDictionary<Guid, int> _initialPorts = new();
        private readonly ConcurrentDictionary<Guid, CandidateProbeBarrier> _candidateBarriers = new();
        private int _attempt;

        internal ControlledGenerationHealthProbe(ImmutableArray<Guid> serviceIds) =>
            _serviceIds = serviceIds;

        internal TaskCompletionSource<int> CandidateProbeEntered => GetCandidateBarrier(_serviceIds[0]).Entered;

        internal Task<int> WaitForCandidateProbeEnteredAsync(
            Guid serviceId,
            CancellationToken cancellationToken) =>
            GetCandidateBarrier(serviceId).Entered.Task.WaitAsync(cancellationToken);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            var port = request.Endpoint!.Value.Port;
            var initialPort = _initialPorts.GetOrAdd(request.ServiceId, port);
            var observedAt = DateTimeOffset.UtcNow;
            HealthObservationStatus status;
            if (port == initialPort)
            {
                status = HealthObservationStatus.Healthy;
            }
            else
            {
                var barrier = GetCandidateBarrier(request.ServiceId);
                barrier.Entered.TrySetResult(port);
                var outcome = await barrier.Outcome.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                status = outcome.Status;
                observedAt = outcome.ObservedAt ?? DateTimeOffset.UtcNow;
            }

            return new HealthObservationResult(
                request.ServiceId,
                status,
                observedAt,
                TimeSpan.Zero,
                Interlocked.Increment(ref _attempt));
        }

        internal void ReleaseCandidate(HealthObservationStatus status) =>
            ReleaseCandidate(_serviceIds[0], status);

        internal void ReleaseCandidate(
            Guid serviceId,
            HealthObservationStatus status,
            DateTimeOffset? observedAt = null) =>
            GetCandidateBarrier(serviceId).Outcome.TrySetResult(new CandidateProbeOutcome(status, observedAt));

        private CandidateProbeBarrier GetCandidateBarrier(Guid serviceId) =>
            _candidateBarriers.GetOrAdd(serviceId, static _ => new CandidateProbeBarrier());

        private sealed class CandidateProbeBarrier
        {
            internal TaskCompletionSource<int> Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<CandidateProbeOutcome> Outcome { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record CandidateProbeOutcome(
            HealthObservationStatus Status,
            DateTimeOffset? ObservedAt);
    }

    private sealed class FixedLeaseTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        internal FixedLeaseTimeProvider(DateTimeOffset now) => _now = now.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _now;
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
