using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the Phase B configuration contracts against real PostgreSQL.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PhaseBPostgresContractTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000010");

    private static readonly Guid RouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000011");

    private static readonly Guid MissingServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000012");

    private const string ExtensionId = "phase-b-extension";

    /// <summary>Verifies a complete snapshot round-trips every configuration collection.</summary>
    [Fact]
    public async Task CompleteSnapshotReadAndWriteRoundTripsAllCollections()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        Assert.Equal(1L, initial.Value!.Version);
        Assert.Empty(initial.Value!.Routes);
        Assert.Empty(initial.Value!.Services);
        Assert.Empty(initial.Value!.ExtensionRecords);
        Assert.Empty(initial.Value!.ExtensionSettings);

        var changes = CreateCompleteChangeSet(initial.Value!);
        var write = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, write.NewVersion);

        var current = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(current.IsSuccess, current.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(current.Value);
        Assert.Equal(2L, current.Value!.Version);
        Assert.Equal(21000, current.Value!.GlobalSettings.AutoPortRangeStart);
        Assert.Equal(22000, current.Value!.GlobalSettings.AutoPortRangeEnd);
        Assert.Equal(
            ImmutableArray.Create("127.0.0.1/32"),
            current.Value!.GlobalSettings.TrustedProxyCidrs);

        var service = Assert.Single(current.Value!.Services);
        Assert.Equal(ServiceId, service.Id);
        Assert.Equal("/usr/bin/phase-b-fixture", service.FileName);
        Assert.Equal("enabled", service.Environment["PHASE_B_MODE"]);
        Assert.Equal(1L, service.Version);

        var route = Assert.Single(current.Value!.Routes);
        Assert.Equal(RouteId, route.Id);
        var routeTarget = Assert.IsType<MicroserviceRouteTargetConfiguration>(route.Target);
        Assert.Equal(ServiceId, routeTarget.ServiceId);
        Assert.Equal(1L, route.Version);

        var extension = Assert.Single(current.Value!.ExtensionRecords);
        Assert.Equal(ExtensionId, extension.ExtensionId);
        Assert.Equal("1.2.3", extension.Version);
        Assert.Equal(1L, extension.RecordVersion);

        var settings = Assert.Single(current.Value!.ExtensionSettings);
        Assert.Equal(ExtensionId, settings.ExtensionId);
        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal(1L, settings.Version);
        using var settingsDocument = JsonDocument.Parse(settings.SettingsJson);
        Assert.True(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
    }

    /// <summary>Verifies stale global versions are rejected without changing the committed snapshot.</summary>
    [Fact]
    public async Task SnapshotWriteRejectsOptimisticVersionConflict()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var changes = CreateGlobalOnlyChangeSet(initial.Value!, 2048);

        var firstWrite = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);
        Assert.True(firstWrite.IsSuccess, firstWrite.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, firstWrite.NewVersion);

        var staleWrite = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.False(staleWrite.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, staleWrite.Errors.Single().Code);
        Assert.Null(staleWrite.NewVersion);

        var current = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(current.IsSuccess, current.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(current.Value);
        Assert.Equal(2L, current.Value!.Version);
        Assert.Equal(2048, current.Value!.GlobalSettings.MaxConcurrentRequests);
    }

    /// <summary>Verifies a bad reference rejects the complete batch without partial rows or revision changes.</summary>
    [Fact]
    public async Task InvalidReferenceRollsBackTheCompleteSnapshotBatch()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var service = CreateService(ServiceId, version: 0);
        var route = CreateRoute(MissingServiceId, version: 0);
        var changes = new ConfigurationChangeSet(
            CreateGlobalSettings(initial.Value!.GlobalSettings.Version, 23000),
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        var write = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.False(write.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, write.Errors.Single().Code);
        Assert.Null(write.NewVersion);

        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("global_settings")};"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("services")};"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("routes")};"));
    }

    /// <summary>Verifies every committed snapshot mutation advances the global revision exactly once.</summary>
    [Fact]
    public async Task CommittedSnapshotMutationsIncrementTheGlobalRevision()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var first = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateGlobalOnlyChangeSet(initial.Value, 2048),
            cancellationToken);
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, first.NewVersion);

        var afterFirst = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(afterFirst.IsSuccess, afterFirst.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterFirst.Value);

        var second = await api.WriteSnapshotAsync(
            afterFirst.Value!.Version,
            CreateGlobalOnlyChangeSet(afterFirst.Value, 4096),
            cancellationToken);
        Assert.True(second.IsSuccess, second.Errors.FirstOrDefault()?.Message);
        Assert.Equal(3L, second.NewVersion);

        Assert.Equal(
            3L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
    }

    /// <summary>Verifies the PostgreSQL notification carries the committed revision on the contract channel.</summary>
    [Fact]
    public async Task SnapshotWritePublishesCommittedRevisionNotification()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        await using var listener = new NpgsqlConnection(connectionString);
        var cancellationToken = TestContext.Current.CancellationToken;
        await listener.OpenAsync(cancellationToken);
        await using (var listenCommand = new NpgsqlCommand(
                         "LISTEN nekostick_config_changed;",
                         listener))
        {
            await listenCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var notification = new TaskCompletionSource<NpgsqlNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotification(object? _, NpgsqlNotificationEventArgs args) =>
            notification.TrySetResult(args);

        listener.Notification += OnNotification;
        using var listenerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenerCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var waitTask = listener.WaitAsync(listenerCancellation.Token);
        try
        {
            var initial = await api.ReadSnapshotAsync(cancellationToken);
            Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(initial.Value);

            var write = await api.WriteSnapshotAsync(
                initial.Value!.Version,
                CreateGlobalOnlyChangeSet(initial.Value, 2048),
                cancellationToken);
            Assert.True(write.IsSuccess, write.Errors.FirstOrDefault()?.Message);
            Assert.Equal(2L, write.NewVersion);

            var received = await notification.Task.WaitAsync(listenerCancellation.Token);
            Assert.Equal("nekostick_config_changed", received.Channel);
            Assert.Equal("2", received.Payload);
            await waitTask;
        }
        finally
        {
            listenerCancellation.Cancel();
            try
            {
                await waitTask;
            }
            catch (OperationCanceledException)
            {
                // The bounded listener is intentionally canceled during cleanup.
            }

            listener.Notification -= OnNotification;
        }
    }

    /// <summary>Verifies extension settings have independent versions and advance the global revision.</summary>
    [Fact]
    public async Task ExtensionSettingsReadWriteAndConflictUseSettingsVersions()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);
        await using var api = new EfHostConfigApi(context);
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var seed = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateExtensionChangeSet(initial.Value),
            cancellationToken);
        Assert.True(seed.IsSuccess, seed.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, seed.NewVersion);

        var firstRead = await api.ReadExtensionSettingsAsync(ExtensionId, cancellationToken);
        Assert.True(firstRead.IsSuccess, firstRead.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(firstRead.Value);
        Assert.Equal(1, firstRead.Value!.SchemaVersion);
        Assert.Equal(1L, firstRead.Value!.Version);

        var update = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 2,
            settingsJson: "{\"enabled\":false,\"limit\":10}",
            version: 1);
        var updateResult = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 1,
            settings: update,
            cancellationToken: cancellationToken);

        Assert.True(updateResult.IsSuccess, updateResult.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, updateResult.NewVersion);

        var secondRead = await api.ReadExtensionSettingsAsync(ExtensionId, cancellationToken);
        Assert.True(secondRead.IsSuccess, secondRead.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(secondRead.Value);
        Assert.Equal(2, secondRead.Value!.SchemaVersion);
        Assert.Equal(2L, secondRead.Value!.Version);
        using (var settingsDocument = JsonDocument.Parse(secondRead.Value!.SettingsJson))
        {
            Assert.False(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(10, settingsDocument.RootElement.GetProperty("limit").GetInt32());
        }

        var stale = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 1,
            settings: new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 3,
                settingsJson: "{\"enabled\":true}",
                version: 1),
            cancellationToken: cancellationToken);

        Assert.False(stale.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, stale.Errors.Single().Code);
        Assert.Null(stale.NewVersion);
        Assert.Equal(
            3L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
    }

    /// <summary>Verifies the active default node uniqueness guard rejects a second active registration.</summary>
    [Fact]
    public async Task SecondActiveDefaultNodeRegistrationIsRejected()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        await MigrateAsync(database, context);

        var firstNode = new Node
        {
            Id = Guid.CreateVersion7(),
            NodeId = "0",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastConfigurationVersion = 1,
            RuntimeState = "ready",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        context.Nodes.Add(firstNode);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondNodeId = Guid.CreateVersion7();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("nodes")}
                (id, node_id, last_heartbeat_at, last_configuration_version, runtime_state,
                 is_active, created_at, updated_at, version)
            VALUES (@id, '0', now(), 1, 'ready', TRUE, now(), now(), 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = secondNodeId }));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} " +
                "WHERE node_id = '0' AND is_active;"));
    }

    private static async Task MigrateAsync(
        PostgresTestDatabase database,
        NekostickDbContext context)
    {
        var result = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static ConfigurationChangeSet CreateCompleteChangeSet(
        HostConfigurationSnapshot snapshot)
    {
        var service = CreateService(ServiceId, version: 0);
        var route = CreateRoute(ServiceId, version: 0);
        var now = DateTimeOffset.UtcNow;
        var extension = new ExtensionRecordConfiguration(
            ExtensionId,
            "1.2.3",
            ExtensionLoadState.Loaded,
            now,
            now,
            recordVersion: 0);
        var settings = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 2,
            settingsJson: "{\"enabled\":true,\"limit\":3}",
            version: 0);

        return new ConfigurationChangeSet(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, 2048),
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray.Create(extension),
            ImmutableArray.Create(settings));
    }

    private static ConfigurationChangeSet CreateExtensionChangeSet(
        HostConfigurationSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var extension = new ExtensionRecordConfiguration(
            ExtensionId,
            "1.0.0",
            ExtensionLoadState.Discovered,
            now,
            now,
            recordVersion: 0);
        var settings = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 1,
            settingsJson: "{\"enabled\":true,\"limit\":1}",
            version: 0);

        return new ConfigurationChangeSet(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, 1024),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray.Create(extension),
            ImmutableArray.Create(settings));
    }

    private static ConfigurationChangeSet CreateGlobalOnlyChangeSet(
        HostConfigurationSnapshot snapshot,
        int maxConcurrentRequests) =>
        new(
            CreateGlobalSettings(snapshot.GlobalSettings.Version, maxConcurrentRequests),
            snapshot.Routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings);

    private static GlobalSettingsConfiguration CreateGlobalSettings(
        long version,
        int maxConcurrentRequests) =>
        new(
            version,
            autoPortRangeStart: 21000,
            autoPortRangeEnd: 22000,
            maxRequestBodyBytes: 30 * 1024 * 1024,
            maxConcurrentRequests: maxConcurrentRequests,
            configurationPollInterval: TimeSpan.FromSeconds(30),
            trustedProxyCidrs: ImmutableArray.Create("127.0.0.1/32"));

    private static ServiceConfiguration CreateService(Guid id, long version) =>
        new(
            id,
            enabled: true,
            fileName: "/usr/bin/phase-b-fixture",
            argumentList: ImmutableArray.Create("--integration"),
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty
                .Add("PHASE_B_MODE", "enabled"),
            startMode: ServiceStartMode.Eager,
            restartPolicy: ServiceRestartPolicy.Always,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: version);

    private static RouteConfiguration CreateRoute(Guid serviceId, long version) =>
        new(
            RouteId,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/phase-b",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: new MicroserviceRouteTargetConfiguration(serviceId),
            priority: 10,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, replaceTemplate: null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: version);
}
