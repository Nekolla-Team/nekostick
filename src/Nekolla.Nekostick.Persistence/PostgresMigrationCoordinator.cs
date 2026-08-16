using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Serializes EF migration execution behind a fixed transaction-scoped advisory lock.</summary>
public sealed class PostgresMigrationCoordinator : IStartupDatabaseProbe
{
    private readonly string _connectionString;
    private readonly IMigrationSchemaValidator _schemaValidator;

    /// <summary>Creates a migration coordinator without enabling sensitive diagnostics.</summary>
    /// <param name="connectionString">The sensitive PostgreSQL connection string.</param>
    /// <param name="schemaValidator">The schema validator, or the PostgreSQL default.</param>
    public PostgresMigrationCoordinator(
        string connectionString,
        IMigrationSchemaValidator? schemaValidator = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _schemaValidator = schemaValidator ?? new PostgresMigrationSchemaValidator();
    }

    /// <inheritdoc />
    public async Task<StartupDatabaseResult> MigrateAndValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        try
        {
            await using var lockConnection = new NpgsqlConnection(_connectionString);
            await lockConnection.OpenAsync(cancellationToken);
            await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken);
            await using var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@lock_key);",
                lockConnection,
                lockTransaction);
            lockCommand.Parameters.Add("lock_key", NpgsqlDbType.Bigint).Value =
                PersistenceDatabaseDefaults.MigrationAdvisoryLockKey;
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);

            await dbContext.Database.MigrateAsync(cancellationToken);
            var validation = await _schemaValidator.ValidateAsync(dbContext, cancellationToken);
            if (!validation.IsValid)
            {
                await lockTransaction.RollbackAsync(cancellationToken);
                return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.SchemaValidationFailed);
            }

            await lockTransaction.CommitAsync(cancellationToken);
            return StartupDatabaseResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.MigrationFailed);
        }
        catch (DbException)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.AdvisoryLockUnavailable);
        }
        catch (Exception)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.MigrationFailed);
        }
    }
}
