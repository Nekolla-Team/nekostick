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
        Assert.Equal(PersistenceDatabaseDefaults.Schema, relationalOptions.MigrationsHistoryTableSchema);

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
}
