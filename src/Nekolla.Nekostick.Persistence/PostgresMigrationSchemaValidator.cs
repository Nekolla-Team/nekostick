using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Validates the authored PostgreSQL schema contract and its singleton seed rows.</summary>
public sealed class PostgresMigrationSchemaValidator : IMigrationSchemaValidator
{
    private readonly string _schema;
    private readonly ILogger _logger;

    /// <summary>Creates a validator for the canonical production schema.</summary>
    public PostgresMigrationSchemaValidator()
        : this(PersistenceDatabaseDefaults.Schema)
    {
    }

    /// <summary>Creates a validator for a controlled PostgreSQL schema.</summary>
    /// <param name="schema">The non-empty lowercase ASCII PostgreSQL schema identifier to validate.</param>
    /// <param name="logger">The optional persistence logger.</param>
    public PostgresMigrationSchemaValidator(string schema, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (!PostgresDatabaseIdentifier.IsValidSchemaIdentifier(schema))
        {
            throw new ArgumentException(
                "The schema must be a non-empty lowercase ASCII PostgreSQL identifier of no more than 63 bytes.",
                nameof(schema));
        }

        _schema = schema;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<SchemaValidationResult> ValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            var missingRelations = await PostgresMigrationSchemaCatalogQueries.FindMissingRelationsAsync(
                connection,
                _schema,
                cancellationToken);
            if (missingRelations.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingRelations);
            }

            var missingColumns = await PostgresMigrationSchemaCatalogQueries.FindMissingColumnsAsync(
                connection,
                _schema,
                cancellationToken);
            if (missingColumns.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingColumns);
            }

            var missingConstraints = await PostgresMigrationSchemaCatalogQueries.FindMissingConstraintsAsync(
                connection,
                _schema,
                cancellationToken);
            if (missingConstraints.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingConstraints);
            }

            var missingChecks = await PostgresMigrationSchemaCatalogQueries.FindMissingChecksAsync(
                connection,
                _schema,
                cancellationToken);
            if (missingChecks.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingChecks);
            }

            var missingIndexes = await PostgresMigrationSchemaCatalogQueries.FindMissingIndexesAsync(
                connection,
                _schema,
                cancellationToken);
            if (missingIndexes.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingIndexes);
            }

            var missingSeeds = await PostgresMigrationSeedValidator.FindMissingSeedsAsync(
                connection,
                _schema,
                cancellationToken);
            return missingSeeds.Count == 0
                ? SchemaValidationResult.Valid()
                : SchemaValidationResult.Invalid(missingSeeds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceLogMessages.OperationCancelled(_logger, "ValidateSchema", _schema);
            throw;
        }
        catch (DbException exception)
        {
            PersistenceLogMessages.StartupFailed(_logger, exception, "ValidateSchema", _schema);
            return SchemaValidationResult.Invalid(["database"]);
        }
        catch (Exception exception)
        {
            PersistenceLogMessages.StartupFailed(_logger, exception, "ValidateSchema", _schema);
            return SchemaValidationResult.Invalid(["database"]);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }
}
