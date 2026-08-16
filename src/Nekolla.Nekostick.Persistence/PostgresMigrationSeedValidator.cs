using System.Data.Common;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Validates the required singleton seeds and EF migration history marker.</summary>
internal static class PostgresMigrationSeedValidator
{
    internal static async Task<List<string>> FindMissingSeedsAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var quotedSchema = PostgresDatabaseIdentifier.QuoteIdentifier(schema);
        command.CommandText = $"""
            SELECT
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."configuration_revisions"
                    WHERE id = CAST(@revision_id AS uuid)
                      AND revision_key = @revision_key
                      AND version >= 1
                ),
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."global_settings"
                    WHERE id = CAST(@settings_id AS uuid)
                      AND version >= 1
                ),
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."{PersistenceDatabaseDefaults.MigrationHistoryTable}"
                    WHERE RIGHT("MigrationId", LENGTH(@migration_suffix)) = @migration_suffix
                      AND NULLIF(BTRIM("MigrationId"), '') IS NOT NULL
                      AND NULLIF(BTRIM("ProductVersion"), '') IS NOT NULL
                );
            """;
        AddTextParameter(command, "revision_id", PersistenceDatabaseDefaults.SeedConfigurationRevisionId);
        AddTextParameter(command, "revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey);
        AddTextParameter(command, "settings_id", PersistenceDatabaseDefaults.SeedGlobalSettingsId);
        AddTextParameter(
            command,
            "migration_suffix",
            $"_{PersistenceDatabaseDefaults.InitialMigrationName}");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ["seed-query"];
        }

        var missing = new List<string>();
        if (!reader.GetBoolean(0))
        {
            missing.Add("configuration_revisions:global");
        }

        if (!reader.GetBoolean(1))
        {
            missing.Add("global_settings:singleton");
        }

        if (!reader.GetBoolean(2))
        {
            missing.Add("__EFMigrationsHistory:initial");
        }

        return missing;
    }

    private static void AddTextParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
