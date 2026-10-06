using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises migration, validation, and repeat-start behavior against PostgreSQL.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PersistenceMigrationTests
{
    /// <summary>Verifies the initial migration remains structured for test-only SQL routing.</summary>
    [Fact]
    public Task InitialMigrationContainsNoRawSqlOperations()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var context = new NekostickDbContext(
            NekostickDbContextOptions.Create("Host=unused"));
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var initialMigration = migrationsAssembly.Migrations.Single(
            pair => pair.Key.EndsWith(
                $"_{PersistenceDatabaseDefaults.InitialMigrationName}",
                StringComparison.Ordinal));
        var migration = migrationsAssembly.CreateMigration(
            initialMigration.Value,
            context.Database.ProviderName!);

        Assert.DoesNotContain(migration.UpOperations, operation => operation is SqlOperation);
        return Task.CompletedTask;
    }

    /// <summary>Verifies complete migration and idempotent subsequent startup probing.</summary>
    [Fact]
    public async Task MigrationCreatesSchemaAndSeedsThenRepeatProbeIsIdempotent()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var relationalOptions = context.GetService<IDbContextOptions>()
            .Extensions
            .OfType<RelationalOptionsExtension>()
            .Single();
        Assert.Equal(
            PersistenceDatabaseDefaults.MigrationHistoryTable,
            relationalOptions.MigrationsHistoryTableName);
        Assert.Equal(database.Schema, relationalOptions.MigrationsHistoryTableSchema);

        var coordinator = database.CreateMigrationCoordinator();
        var first = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);

        var migrationEvidence = await database.ReadMigrationEvidenceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(9L, migrationEvidence.RequiredRelationCount);
        Assert.Equal(1L, migrationEvidence.InitialMigrationHistoryRows);
        var validation = await database.CreateMigrationSchemaValidator().ValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("database", validation.MissingObjects);
        Assert.True(
            first.IsSuccess,
            $"initial migration coordinator failed: {string.Join(',', validation.MissingObjects)}");
        Assert.True(validation.IsValid);
        Assert.Empty(validation.MissingObjects);
        Assert.Equal(1, await context.ConfigurationRevisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.GlobalSettings.CountAsync(TestContext.Current.CancellationToken));
        var migrationHistoryRows = await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {database.QualifiedRelation(PersistenceDatabaseDefaults.MigrationHistoryTable)} " +
            "WHERE RIGHT(\"MigrationId\", LENGTH(@migration_suffix)) = @migration_suffix " +
            "AND BTRIM(\"MigrationId\") <> '' AND BTRIM(\"ProductVersion\") <> '';",
            new NpgsqlParameter
            {
                ParameterName = "migration_suffix",
                Value = $"_{PersistenceDatabaseDefaults.InitialMigrationName}"
            });
        Assert.Equal(1L, migrationHistoryRows);

        await using var secondContext = database.CreateContext();
        var second = await coordinator.MigrateAndValidateAsync(
            secondContext,
            TestContext.Current.CancellationToken);

        Assert.True(second.IsSuccess, "repeat migration coordinator failed");
        var secondValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(secondContext, TestContext.Current.CancellationToken);
        Assert.True(secondValidation.IsValid);
        Assert.Empty(secondValidation.MissingObjects);
        Assert.Equal(1, await secondContext.ConfigurationRevisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await secondContext.GlobalSettings.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies a legacy over-ceiling body value is normalized during the real latest migration.</summary>
    [Fact]
    public async Task LegacyOverCeilingBodyValueIsNormalizedBeforeLatestMigration()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var predecessorContext = database.CreateContext())
        {
            await predecessorContext.Database.MigrateAsync(
                "20260818230804_AddRequestLimitsAndRatePolicies",
                cancellationToken);
        }

        await database.ExecuteSchemaCommandAsync(
            $"UPDATE {database.QualifiedRelation("global_settings")} " +
            "SET max_request_body_bytes = @max_body_bytes " +
            "WHERE id = @id;",
            new NpgsqlParameter("max_body_bytes", NpgsqlDbType.Bigint)
            {
                Value = 31457281L
            },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
            });

        await using var latestContext = database.CreateContext();
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(latestContext, cancellationToken);

        Assert.True(migrated.IsSuccess, migrated.Error?.Message);
        var validation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(latestContext, cancellationToken);
        Assert.True(validation.IsValid);
        Assert.Empty(validation.MissingObjects);
        Assert.Equal(
            31457280L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT max_request_body_bytes FROM {database.QualifiedRelation("global_settings")} " +
                "WHERE id = @id;",
                new NpgsqlParameter("id", NpgsqlDbType.Uuid)
                {
                    Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
                }));

        var checkViolation = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteSchemaCommandAsync(
                $"UPDATE {database.QualifiedRelation("global_settings")} " +
                "SET max_request_body_bytes = @max_body_bytes " +
                "WHERE id = @id;",
                new NpgsqlParameter("max_body_bytes", NpgsqlDbType.Bigint)
                {
                    Value = 31457281L
                },
                new NpgsqlParameter("id", NpgsqlDbType.Uuid)
                {
                    Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
                }));
        Assert.Equal("23514", checkViolation.SqlState);

        await using var repeatContext = database.CreateContext();
        var repeated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(repeatContext, cancellationToken);
        Assert.True(repeated.IsSuccess, repeated.Error?.Message);
    }

    /// <summary>Verifies the generation migration backfills each legacy lease from its UUID v7 row ID without changing lease data.</summary>
    [Fact]
    public async Task GenerationMigrationBackfillsLegacyLeasesFromIdsAndPreservesLiveLeaseData()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var predecessorContext = database.CreateContext())
        {
            await predecessorContext.Database.MigrateAsync(
                "20261003052213_WidenCommittedBy",
                cancellationToken);
        }

        var nodeRecordId = Guid.CreateVersion7();
        var nodeId = $"lease-backfill-{Guid.NewGuid():N}";
        var serviceId = Guid.CreateVersion7();
        var leaseId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddHours(-1);
        var renewedAt = now.AddMinutes(-5);
        var updatedAt = now.AddMinutes(-4);
        var expiresAt = now.AddHours(1);
        const int port = 26_500;
        const long version = 17;

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("nodes")}
                (id, node_id, last_heartbeat_at, last_configuration_version, runtime_state,
                 is_active, created_at, updated_at, version)
            VALUES (@id, @node_id, @now, 1, 'ready', TRUE, @now, @now, 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = nodeRecordId },
            new NpgsqlParameter("node_id", NpgsqlDbType.Varchar) { Value = nodeId },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("services")}
                (id, enabled, file_name, argument_list_json, working_directory, environment_json,
                 start_mode, restart_policy, health_check_type, health_check_timeout_milliseconds,
                 health_check_http_path, created_at, updated_at, version)
            VALUES (@id, TRUE, 'fixture', '[]'::jsonb, '/fixture', jsonb_build_object(),
                    'Eager', 'Never', 'Process', 1000, NULL, @now, @now, 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = serviceId },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("port_leases")}
                (id, node_id, port, service_id, lease_expires_at, renewed_at, version, created_at, updated_at)
            VALUES (@id, @node_id, @port, @service_id, @expires_at, @renewed_at, @version, @created_at, @updated_at);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = leaseId },
            new NpgsqlParameter("node_id", NpgsqlDbType.Varchar) { Value = nodeId },
            new NpgsqlParameter("port", NpgsqlDbType.Integer) { Value = port },
            new NpgsqlParameter("service_id", NpgsqlDbType.Uuid) { Value = serviceId },
            new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = expiresAt },
            new NpgsqlParameter("renewed_at", NpgsqlDbType.TimestampTz) { Value = renewedAt },
            new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = version },
            new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = createdAt },
            new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = updatedAt });

        await using var migratedContext = database.CreateContext();
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(migratedContext, cancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        var backfilledGenerationId = await database.ExecuteScalarAsync<Guid>(
            $"SELECT generation_id FROM {database.QualifiedRelation("port_leases")} WHERE id = @id;",
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = leaseId });
        Assert.Equal(leaseId, backfilledGenerationId);

        var leaseDataPreserved = await database.ExecuteScalarAsync<bool>(
            $"""
            SELECT generation_id = id
               AND node_id = @node_id
               AND port = @port
               AND service_id = @service_id
               AND lease_expires_at = @expires_at
               AND renewed_at = @renewed_at
               AND version = @version
               AND created_at = @created_at
               AND updated_at = @updated_at
            FROM {database.QualifiedRelation("port_leases")}
            WHERE id = @id;
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = leaseId },
            new NpgsqlParameter("node_id", NpgsqlDbType.Varchar) { Value = nodeId },
            new NpgsqlParameter("port", NpgsqlDbType.Integer) { Value = port },
            new NpgsqlParameter("service_id", NpgsqlDbType.Uuid) { Value = serviceId },
            new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = expiresAt },
            new NpgsqlParameter("renewed_at", NpgsqlDbType.TimestampTz) { Value = renewedAt },
            new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = version },
            new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = createdAt },
            new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = updatedAt });
        Assert.True(leaseDataPreserved, "legacy lease fields changed during generation backfill");

        var invalidGenerationViolation = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteSchemaCommandAsync(
                $"UPDATE {database.QualifiedRelation("port_leases")} SET generation_id = @generation_id WHERE id = @id;",
                new NpgsqlParameter("generation_id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = leaseId }));
        Assert.Equal("23514", invalidGenerationViolation.SqlState);
    }

    /// <summary>Verifies rollback succeeds when no node/service pair has multiple leases.</summary>
    [Fact]
    public async Task GenerationDownMigrationSucceedsWithoutMultipleServiceLeases()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var migrationContext = database.CreateContext())
        {
            var migrated = await database.CreateMigrationCoordinator()
                .MigrateAndValidateAsync(migrationContext, cancellationToken);
            Assert.True(migrated.IsSuccess, migrated.Error?.Message);
        }

        await using (var rollbackContext = database.CreateContext())
        {
            await rollbackContext.Database.MigrateAsync(
                "20261003052213_WidenCommittedBy",
                cancellationToken);
        }

        var generationColumnCount = await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns " +
            "WHERE table_schema = @schema AND table_name = 'port_leases' AND column_name = 'generation_id';",
            new NpgsqlParameter("schema", NpgsqlDbType.Varchar) { Value = database.Schema });
        Assert.Equal(0L, generationColumnCount);

        var generationIndexCount = await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pg_indexes " +
            "WHERE schemaname = @schema AND tablename = 'port_leases' " +
            "AND indexname = @index_name;",
            new NpgsqlParameter("schema", NpgsqlDbType.Varchar) { Value = database.Schema },
            new NpgsqlParameter("index_name", NpgsqlDbType.Varchar)
            {
                Value = "ux_port_leases_node_id_service_id_generation_id"
            });
        Assert.Equal(0L, generationIndexCount);

        var migrationHistoryRows = await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {database.QualifiedRelation(PersistenceDatabaseDefaults.MigrationHistoryTable)} " +
            "WHERE \"MigrationId\" = @migration_id;",
            new NpgsqlParameter("migration_id", NpgsqlDbType.Varchar)
            {
                Value = "20261006090000_AddPortLeaseGenerations"
            });
        Assert.Equal(0L, migrationHistoryRows);
    }

    /// <summary>Rejects rollback when any node/service pair has multiple lease rows and preserves them.</summary>
    [Fact]
    public async Task GenerationDownMigrationRejectsMultipleServiceLeasesWithoutChangingSchemaOrData()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var migrationContext = database.CreateContext())
        {
            var migrated = await database.CreateMigrationCoordinator()
                .MigrateAndValidateAsync(migrationContext, cancellationToken);
            Assert.True(migrated.IsSuccess, migrated.Error?.Message);
        }

        var nodeRecordId = Guid.CreateVersion7();
        var nodeId = $"lease-rollback-{Guid.NewGuid():N}";
        var serviceId = Guid.CreateVersion7();
        var firstLeaseId = Guid.CreateVersion7();
        var secondLeaseId = Guid.CreateVersion7();
        var firstGenerationId = Guid.CreateVersion7();
        var secondGenerationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var createdAt = now.AddHours(-5);
        var renewedAt = now.AddHours(-4);
        var updatedAt = now.AddHours(-3);
        var firstExpiresAt = now.AddHours(-2);
        var secondExpiresAt = now.AddHours(-1);
        const int firstPort = 26_600;
        const int secondPort = 26_601;
        const long firstVersion = 11;
        const long secondVersion = 13;

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("nodes")}
                (id, node_id, last_heartbeat_at, last_configuration_version, runtime_state,
                 is_active, created_at, updated_at, version)
            VALUES (@id, @node_id, @now, 1, 'ready', TRUE, @now, @now, 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = nodeRecordId },
            new NpgsqlParameter("node_id", NpgsqlDbType.Varchar) { Value = nodeId },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("services")}
                (id, enabled, file_name, argument_list_json, working_directory, environment_json,
                 start_mode, restart_policy, health_check_type, health_check_timeout_milliseconds,
                 health_check_http_path, created_at, updated_at, version)
            VALUES (@id, TRUE, 'fixture', '[]'::jsonb, '/fixture', jsonb_build_object(),
                    'Eager', 'Never', 'Process', 1000, NULL, @now, @now, 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = serviceId },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("port_leases")}
                (id, node_id, port, service_id, generation_id, lease_expires_at,
                 renewed_at, version, created_at, updated_at)
            VALUES
                (@first_id, @node_id, @first_port, @service_id, @first_generation,
                 @first_expires_at, @renewed_at, @first_version, @created_at, @updated_at),
                (@second_id, @node_id, @second_port, @service_id, @second_generation,
                 @second_expires_at, @renewed_at, @second_version, @created_at, @updated_at);
            """,
            new NpgsqlParameter("first_id", NpgsqlDbType.Uuid) { Value = firstLeaseId },
            new NpgsqlParameter("second_id", NpgsqlDbType.Uuid) { Value = secondLeaseId },
            new NpgsqlParameter("node_id", NpgsqlDbType.Varchar) { Value = nodeId },
            new NpgsqlParameter("first_port", NpgsqlDbType.Integer) { Value = firstPort },
            new NpgsqlParameter("second_port", NpgsqlDbType.Integer) { Value = secondPort },
            new NpgsqlParameter("service_id", NpgsqlDbType.Uuid) { Value = serviceId },
            new NpgsqlParameter("first_generation", NpgsqlDbType.Uuid) { Value = firstGenerationId },
            new NpgsqlParameter("second_generation", NpgsqlDbType.Uuid) { Value = secondGenerationId },
            new NpgsqlParameter("first_expires_at", NpgsqlDbType.TimestampTz) { Value = firstExpiresAt },
            new NpgsqlParameter("second_expires_at", NpgsqlDbType.TimestampTz) { Value = secondExpiresAt },
            new NpgsqlParameter("renewed_at", NpgsqlDbType.TimestampTz) { Value = renewedAt },
            new NpgsqlParameter("first_version", NpgsqlDbType.Bigint) { Value = firstVersion },
            new NpgsqlParameter("second_version", NpgsqlDbType.Bigint) { Value = secondVersion },
            new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = createdAt },
            new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = updatedAt });

        Exception rollbackFailure;
        await using (var rollbackContext = database.CreateContext())
        {
            rollbackFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                rollbackContext.Database.MigrateAsync(
                    "20261003052213_WidenCommittedBy",
                    cancellationToken));
        }

        var postgresFailure = FindPostgresException(rollbackFailure);
        Assert.NotNull(postgresFailure);
        var postgresException = postgresFailure!;
        Assert.Equal("23514", postgresException.SqlState);
        Assert.Contains(
            "Cannot drop generation_id",
            postgresException.MessageText,
            StringComparison.Ordinal);

        await using var verificationContext = database.CreateContext();
        var validation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(verificationContext, cancellationToken);
        Assert.True(validation.IsValid, "failed rollback changed the generation-aware schema");
        Assert.Empty(validation.MissingObjects);

        var leases = await verificationContext.PortLeases.AsNoTracking()
            .Where(value => value.NodeId == nodeId && value.ServiceId == serviceId)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, leases.Count);
        var firstLease = Assert.Single(leases, value => value.Id == firstLeaseId);
        var secondLease = Assert.Single(leases, value => value.Id == secondLeaseId);
        Assert.Equal(firstGenerationId, firstLease.GenerationId);
        Assert.Equal(secondGenerationId, secondLease.GenerationId);
        Assert.Equal(firstPort, firstLease.Port);
        Assert.Equal(secondPort, secondLease.Port);
        Assert.Equal(firstVersion, firstLease.Version);
        Assert.Equal(secondVersion, secondLease.Version);
        Assert.Equal(firstExpiresAt, firstLease.LeaseExpiresAt);
        Assert.Equal(secondExpiresAt, secondLease.LeaseExpiresAt);
        Assert.Equal(nodeId, firstLease.NodeId);
        Assert.Equal(serviceId, firstLease.ServiceId);
        Assert.Equal(createdAt, firstLease.CreatedAt);
        Assert.Equal(renewedAt, firstLease.RenewedAt);
        Assert.Equal(updatedAt, firstLease.UpdatedAt);
        Assert.Equal(nodeId, secondLease.NodeId);
        Assert.Equal(serviceId, secondLease.ServiceId);
        Assert.Equal(createdAt, secondLease.CreatedAt);
        Assert.Equal(renewedAt, secondLease.RenewedAt);
        Assert.Equal(updatedAt, secondLease.UpdatedAt);

        var migrationHistoryRows = await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {database.QualifiedRelation(PersistenceDatabaseDefaults.MigrationHistoryTable)} " +
            "WHERE \"MigrationId\" = @migration_id;",
            new NpgsqlParameter("migration_id", NpgsqlDbType.Varchar)
            {
                Value = "20261006090000_AddPortLeaseGenerations"
            });
        Assert.Equal(1L, migrationHistoryRows);
    }

    /// <summary>Verifies the database rejects UUIDs with a non-v7 version or invalid RFC variant.</summary>
    [Fact]
    public async Task DatabaseRejectsUuidV4AndInvalidVariant()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        foreach (var invalidId in new[]
        {
            Guid.Parse("018f0f00-0000-4000-8000-000000000010"),
            Guid.Parse("018f0f00-0000-7000-0000-000000000011")
        })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteSchemaCommandAsync(
                $"""
                INSERT INTO {database.QualifiedRelation("services")} 
                    (id, enabled, file_name, argument_list_json, working_directory, environment_json,
                     start_mode, restart_policy, health_check_type, health_check_timeout_milliseconds,
                     created_at, updated_at, version)
                VALUES (@id, TRUE, 'fixture', '[]'::jsonb, '/fixture', jsonb_build_object(),
                        'Eager', 'Never', 'Process', 1000, now(), now(), 1);
                """,
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = invalidId }));

            Assert.Equal("23514", exception.SqlState);
        }
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

}
