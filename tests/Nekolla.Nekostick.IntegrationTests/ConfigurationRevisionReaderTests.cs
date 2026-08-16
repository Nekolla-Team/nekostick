using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Focuses on safe configuration revision reads over PostgreSQL.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class ConfigurationRevisionReaderTests
{
    /// <summary>Verifies revision reads omit sensitive configuration JSON.</summary>
    [Fact]
    public async Task RevisionReaderReturnsMetadataWithoutSensitiveJson()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var coordinator = database.CreateMigrationCoordinator();
        var migrated = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        const string sensitiveJson = "[\"integration-secret-value\"]";
        await database.ExecuteSchemaCommandAsync(
            $"UPDATE {database.QualifiedRelation("global_settings")} " +
            "SET trusted_proxy_cidrs_json = @json WHERE id = @id;",
            new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = sensitiveJson },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
            });

        var result = await new EfConfigurationRevisionReader(context)
            .ReadCurrentAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value!.Version);
        var serializedValue = JsonSerializer.Serialize(result.Value);
        Assert.False(serializedValue.Contains(sensitiveJson, StringComparison.Ordinal));
        Assert.False(serializedValue.Contains("TrustedProxyCidrsJson", StringComparison.Ordinal));
    }

    /// <summary>Verifies an unavailable PostgreSQL connection returns storage-unavailable.</summary>
    [Fact]
    public async Task UnavailableConnectionCollapsesToStorageUnavailable()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        var unreachable = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1,
            Pooling = false
        };

        await using var context = new NekostickDbContext(
            NekostickDbContextOptions.Create(unreachable.ConnectionString));
        var result = await new EfConfigurationRevisionReader(context)
            .ReadCurrentAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(ConfigurationErrorCode.StorageUnavailable, result.Errors[0].Code);
        Assert.Equal(
            "Configuration storage is unavailable.",
            result.Errors[0].Message);
    }
}
