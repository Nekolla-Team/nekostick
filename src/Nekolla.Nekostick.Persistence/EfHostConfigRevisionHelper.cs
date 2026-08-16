using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Owns revision arithmetic, post-commit notification, and write error helpers.</summary>
internal sealed class EfHostConfigRevisionHelper
{
    private const string ConfigurationChangedChannel = "nekostick_config_changed";
    internal const string Committer = "host-config-api";
    private readonly NekostickDbContext _dbContext;

    internal EfHostConfigRevisionHelper(NekostickDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    internal async Task PublishConfigurationChangedAsync(long version)
    {
        try
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await _dbContext.Database.OpenConnectionAsync(CancellationToken.None);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_notify(@channel, @payload);";
            var channel = command.CreateParameter();
            channel.ParameterName = "channel";
            channel.DbType = System.Data.DbType.String;
            channel.Value = ConfigurationChangedChannel;
            command.Parameters.Add(channel);
            var payload = command.CreateParameter();
            payload.ParameterName = "payload";
            payload.DbType = System.Data.DbType.String;
            payload.Value = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.Add(payload);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Polling the singleton revision is the durable notification fallback.
        }
    }

    internal static long IncrementVersion(long version) =>
        version == long.MaxValue
            ? throw new HostConfigurationSemanticValidator.ConfigurationValidationException()
            : checked(version + 1);

    internal static Guid NewUuidV7() => Guid.CreateVersion7();

    internal static ConfigurationWriteResult ValidationWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation));

    internal static ConfigurationWriteResult ConflictWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));

    internal static ConfigurationWriteResult StorageWriteFailure() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.StorageUnavailable));

    internal static bool IsTransactionConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
}
