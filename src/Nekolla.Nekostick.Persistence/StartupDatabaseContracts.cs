using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Identifies a safe startup database failure.</summary>
public enum StartupDatabaseErrorCode
{
    /// <summary>The lock connection could not be opened or locked.</summary>
    AdvisoryLockUnavailable,

    /// <summary>EF migration execution failed.</summary>
    MigrationFailed,

    /// <summary>The authored schema contract or required seed rows are invalid.</summary>
    SchemaValidationFailed,

    /// <summary>The database probe could not execute.</summary>
    DatabaseUnavailable
}

/// <summary>Contains a fixed startup error without exception details or secrets.</summary>
public sealed record StartupDatabaseError
{
    /// <summary>Creates a safe startup database error.</summary>
    /// <param name="code">The stable failure category.</param>
    public StartupDatabaseError(StartupDatabaseErrorCode code)
    {
        Code = code;
        Message = code switch
        {
            StartupDatabaseErrorCode.AdvisoryLockUnavailable => "The database migration lock is unavailable.",
            StartupDatabaseErrorCode.MigrationFailed => "Database migration failed.",
            StartupDatabaseErrorCode.SchemaValidationFailed => "Database schema validation failed.",
            StartupDatabaseErrorCode.DatabaseUnavailable => "The database is unavailable.",
            _ => "Database startup failed."
        };
    }

    /// <summary>Gets the stable failure category.</summary>
    public StartupDatabaseErrorCode Code { get; }

    /// <summary>Gets the fixed safe message.</summary>
    public string Message { get; }
}

/// <summary>Contains a safe migration and validation result.</summary>
public sealed class StartupDatabaseResult
{
    private StartupDatabaseResult()
    {
        IsSuccess = true;
    }

    private StartupDatabaseResult(StartupDatabaseError error) => Error = error;

    /// <summary>Gets whether migration and validation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the safe failure when startup preparation failed.</summary>
    public StartupDatabaseError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    /// <returns>A successful startup result.</returns>
    public static StartupDatabaseResult Success() => new();

    /// <summary>Creates a failed result.</summary>
    /// <param name="code">The safe failure category.</param>
    /// <returns>A failed startup result.</returns>
    public static StartupDatabaseResult Failure(StartupDatabaseErrorCode code) =>
        new(new StartupDatabaseError(code));
}

/// <summary>Contains the result of checking the required PostgreSQL schema.</summary>
public sealed record SchemaValidationResult
{
    private SchemaValidationResult(bool isValid, ImmutableArray<string> missingObjects)
    {
        IsValid = isValid;
        MissingObjects = missingObjects;
    }

    /// <summary>Gets whether the authored schema contract and seed rows are valid.</summary>
    public bool IsValid { get; }

    /// <summary>Gets safe fixed labels for invalid schema contract elements or seed markers.</summary>
    public ImmutableArray<string> MissingObjects { get; }

    /// <summary>Creates a valid validation result.</summary>
    /// <returns>A valid result.</returns>
    public static SchemaValidationResult Valid() =>
        new(true, ImmutableArray<string>.Empty);

    /// <summary>Creates an invalid validation result.</summary>
    /// <param name="missingObjects">Safe fixed object labels.</param>
    /// <returns>An invalid result.</returns>
    public static SchemaValidationResult Invalid(IEnumerable<string> missingObjects) =>
        new(false, missingObjects.ToImmutableArray());
}

/// <summary>Validates schema objects, migration history, and singleton seeds.</summary>
public interface IMigrationSchemaValidator
{
    /// <summary>Checks the complete persistence schema.</summary>
    /// <param name="dbContext">The PostgreSQL-backed context.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe validation result.</returns>
    Task<SchemaValidationResult> ValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Coordinates migration under the process-wide PostgreSQL advisory lock.</summary>
public interface IStartupDatabaseProbe
{
    /// <summary>Runs migration and validates the complete schema before startup continues.</summary>
    /// <param name="dbContext">The context used by EF migrations.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe fail-closed startup result.</returns>
    Task<StartupDatabaseResult> MigrateAndValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains the safe revision data needed by status and doctor.</summary>
public sealed record ConfigurationRevisionStatus
{
    /// <summary>Creates a revision status value.</summary>
    /// <param name="version">The committed configuration version.</param>
    /// <param name="committedAt">The UTC commit timestamp.</param>
    public ConfigurationRevisionStatus(long version, DateTimeOffset committedAt)
    {
        Version = version;
        CommittedAt = committedAt.ToUniversalTime();
    }

    /// <summary>Gets the committed configuration version.</summary>
    public long Version { get; }

    /// <summary>Gets the UTC commit timestamp.</summary>
    public DateTimeOffset CommittedAt { get; }
}

/// <summary>Reads only the current configuration revision for safe diagnostics.</summary>
public interface IConfigurationRevisionReader
{
    /// <summary>Reads the singleton revision without business configuration CRUD.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe revision value or a stable configuration error.</returns>
    Task<ConfigurationReadResult<ConfigurationRevisionStatus>> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the singleton revision through EF without exposing sensitive JSON.</summary>
public sealed class EfConfigurationRevisionReader : IConfigurationRevisionReader
{
    private readonly NekostickDbContext _dbContext;

    /// <summary>Creates a revision reader.</summary>
    /// <param name="dbContext">The context to query.</param>
    public EfConfigurationRevisionReader(NekostickDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <inheritdoc />
    public async Task<ConfigurationReadResult<ConfigurationRevisionStatus>> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var revision = await _dbContext.ConfigurationRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.RevisionKey == PersistenceDatabaseDefaults.GlobalRevisionKey,
                    cancellationToken);
            return revision is null
                ? ConfigurationReadResult<ConfigurationRevisionStatus>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound))
                : ConfigurationReadResult<ConfigurationRevisionStatus>.Success(
                    new ConfigurationRevisionStatus(revision.Version, revision.CommittedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ConfigurationReadResult<ConfigurationRevisionStatus>.Failure(
                new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));
        }
    }
}

/// <summary>Provides a safe exception for hosts that choose exception-based startup flow.</summary>
public sealed class PersistenceStartupException : Exception
{
    /// <summary>Creates an exception with only a fixed safe message.</summary>
    /// <param name="code">The safe startup failure category.</param>
    public PersistenceStartupException(StartupDatabaseErrorCode code)
        : base(new StartupDatabaseError(code).Message)
    {
        Code = code;
    }

    /// <summary>Gets the stable startup failure category.</summary>
    public StartupDatabaseErrorCode Code { get; }
}
