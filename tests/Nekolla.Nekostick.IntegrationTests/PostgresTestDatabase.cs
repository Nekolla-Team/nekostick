using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Contains non-sensitive evidence about the isolated migration result.</summary>
internal sealed record MigrationEvidence(
    long RequiredRelationCount,
    long InitialMigrationHistoryRows);

/// <summary>Owns one isolated PostgreSQL schema for an integration test.</summary>
internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private static readonly string[] RequiredMigrationRelations =
    [
        "configuration_revisions",
        "routes",
        "services",
        "global_settings",
        "extension_records",
        "extension_settings",
        "nodes",
        "port_leases",
        PersistenceDatabaseDefaults.MigrationHistoryTable
    ];

    private readonly string connectionString;
    private int disposed;

    private PostgresTestDatabase(string connectionString, string schema)
    {
        this.connectionString = connectionString;
        Schema = schema;
    }

    /// <summary>Gets the unique sanitized schema name owned by this test.</summary>
    internal string Schema { get; }

    /// <summary>Creates a schema-isolated PostgreSQL test database.</summary>
    /// <param name="connectionString">The secret PostgreSQL connection string.</param>
    /// <returns>An initialized test database scope.</returns>
    internal static async Task<PostgresTestDatabase> CreateAsync(string connectionString)
    {
        var database = new PostgresTestDatabase(connectionString, CreateSchemaName());
        try
        {
            await database.CreateSchemaAsync();
            return database;
        }
        catch
        {
            await database.TryDropSchemaAsync();
            throw;
        }
    }

    /// <summary>Creates a real EF context routed to this test schema.</summary>
    /// <returns>A PostgreSQL-backed EF context.</returns>
    internal NekostickDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<NekostickDbContext>();
        var isolatedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = Schema
        }.ConnectionString;
        optionsBuilder.UseNekostickPostgres(isolatedConnectionString);
        optionsBuilder.ReplaceService<ISqlGenerationHelper, TestSchemaSqlGenerationHelper>();
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, TestSchemaMigrationsSqlGenerator>();
        return new NekostickDbContext(optionsBuilder.Options);
    }

    /// <summary>Creates a migration coordinator scoped to the owned schema.</summary>
    /// <param name="schemaValidator">An optional decorator or replacement validator.</param>
    /// <returns>A coordinator using the owned schema when no validator is supplied.</returns>
    internal PostgresMigrationCoordinator CreateMigrationCoordinator(
        IMigrationSchemaValidator? schemaValidator = null) =>
        new(
            connectionString,
            schemaValidator ?? new PostgresMigrationSchemaValidator(Schema));

    /// <summary>Creates a schema validator scoped to the owned schema.</summary>
    /// <returns>A validator targeting the generated test schema.</returns>
    internal PostgresMigrationSchemaValidator CreateMigrationSchemaValidator() =>
        new(Schema);

    /// <summary>Reads fixed, non-sensitive evidence from the generated migration schema.</summary>
    /// <param name="cancellationToken">The bounded database operation token.</param>
    /// <returns>Required relation and initial migration history row counts.</returns>
    internal async Task<MigrationEvidence> ReadMigrationEvidenceAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var relationCommand = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM (
                VALUES
                    (@relation_0),
                    (@relation_1),
                    (@relation_2),
                    (@relation_3),
                    (@relation_4),
                    (@relation_5),
                    (@relation_6),
                    (@relation_7),
                    (@relation_8)
            ) AS required(relation_name)
            WHERE pg_catalog.to_regclass(required.relation_name) IS NOT NULL;
            """,
            connection);
        for (var index = 0; index < RequiredMigrationRelations.Length; index++)
        {
            relationCommand.Parameters.Add(
                new NpgsqlParameter($"relation_{index}", NpgsqlTypes.NpgsqlDbType.Text)
                {
                    Value = QualifiedRelation(RequiredMigrationRelations[index])
                });
        }

        var requiredRelationCount =
            (long)(await relationCommand.ExecuteScalarAsync(cancellationToken))!;

        var historyRelation = QualifiedRelation(PersistenceDatabaseDefaults.MigrationHistoryTable);
        await using var historyExistsCommand = new NpgsqlCommand(
            "SELECT pg_catalog.to_regclass(@relation_name) IS NOT NULL;",
            connection);
        historyExistsCommand.Parameters.Add(
            new NpgsqlParameter("relation_name", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = historyRelation
            });
        var historyExists =
            (bool)(await historyExistsCommand.ExecuteScalarAsync(cancellationToken))!;
        if (!historyExists)
        {
            return new MigrationEvidence(requiredRelationCount, 0);
        }

        await using var historyCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {historyRelation} " +
            "WHERE RIGHT(\"MigrationId\", LENGTH(@migration_suffix)) = @migration_suffix;",
            connection);
        historyCommand.Parameters.Add(
            new NpgsqlParameter("migration_suffix", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = $"_{PersistenceDatabaseDefaults.InitialMigrationName}"
            });
        var initialMigrationHistoryRows =
            (long)(await historyCommand.ExecuteScalarAsync(cancellationToken))!;
        return new MigrationEvidence(requiredRelationCount, initialMigrationHistoryRows);
    }

    /// <summary>Gets a relation quoted with the owned schema and a fixed relation name.</summary>
    /// <param name="relation">A fixed relation name supplied by the test source.</param>
    /// <returns>A safely quoted qualified relation.</returns>
    internal string QualifiedRelation(string relation) =>
        $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(relation)}";

    /// <summary>Executes a test-only command with parameters and a fixed qualified relation.</summary>
    /// <param name="commandText">SQL assembled only from fixed test text and quoted identifiers.</param>
    /// <param name="parameters">Parameters for all values.</param>
    internal async Task ExecuteSchemaCommandAsync(
        string commandText,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one scalar value using fixed test SQL and parameters.</summary>
    /// <typeparam name="T">The expected scalar type.</typeparam>
    /// <param name="commandText">SQL assembled only from fixed test text and quoted identifiers.</param>
    /// <param name="parameters">Parameters for all values.</param>
    /// <returns>The scalar value returned by PostgreSQL.</returns>
    internal async Task<T> ExecuteScalarAsync<T>(
        string commandText,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Waits until PostgreSQL reports a second connection waiting on the advisory lock.</summary>
    /// <param name="cancellationToken">The bounded observation token.</param>
    internal async Task WaitForAdvisoryLockWaiterAsync(CancellationToken cancellationToken)
    {
        while (!await HasAdvisoryLockWaiterAsync(cancellationToken))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>Confirms the migration lock is held by another real connection.</summary>
    /// <param name="cancellationToken">The database operation token.</param>
    /// <returns><see langword="true"/> when the transaction-scoped lock is unavailable.</returns>
    internal async Task<bool> IsMigrationLockHeldAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT NOT pg_try_advisory_xact_lock(@lock_key);",
            connection,
            transaction);
        command.Parameters.Add("lock_key", NpgsqlTypes.NpgsqlDbType.Bigint).Value =
            PersistenceDatabaseDefaults.MigrationAdvisoryLockKey;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await DropSchemaAsync();
    }

    private static string CreateSchemaName() => $"nekostick_it_{Guid.NewGuid():N}";

    private async Task CreateSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE SCHEMA {QuoteIdentifier(Schema)};",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS {QuoteIdentifier(Schema)} CASCADE;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task TryDropSchemaAsync()
    {
        try
        {
            await DropSchemaAsync();
        }
        catch
        {
            // Preserve the setup failure without emitting the secret connection string.
        }
    }

    private async Task<bool> HasAdvisoryLockWaiterAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND wait_event = 'advisory'
                  AND query ILIKE '%advisory%'
            );
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Any(character =>
                !IsAsciiLetter(character) &&
                !IsAsciiDigit(character) &&
                character != '_'))
        {
            throw new ArgumentException("The PostgreSQL identifier is not a sanitized test identifier.", nameof(identifier));
        }

        return $"\"{identifier}\"";
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
