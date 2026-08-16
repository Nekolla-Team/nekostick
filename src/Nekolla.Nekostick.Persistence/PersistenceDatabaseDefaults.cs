namespace Nekolla.Nekostick.Persistence;

/// <summary>Defines fixed PostgreSQL names and values shared by runtime and migrations.</summary>
public static class PersistenceDatabaseDefaults
{
    /// <summary>The PostgreSQL schema owned by Nekostick.</summary>
    public const string Schema = "nekostick";

    /// <summary>The EF migration history table.</summary>
    public const string MigrationHistoryTable = "__EFMigrationsHistory";

    /// <summary>The stable logical name of the generated initial migration.</summary>
    public const string InitialMigrationName = "InitialPersistence";

    /// <summary>The PostgreSQL check expression required for every persisted UUID identifier.</summary>
    public const string UuidV7CheckConstraintSql =
        "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')";

    /// <summary>The sole configuration revision key.</summary>
    public const string GlobalRevisionKey = "global";

    /// <summary>The only seed revision UUID, which is UUID version 7.</summary>
    public const string SeedConfigurationRevisionId = "018f0f00-0000-7000-8000-000000000001";

    /// <summary>The only seed global settings UUID, which is UUID version 7.</summary>
    public const string SeedGlobalSettingsId = "018f0f00-0000-7000-8000-000000000002";

    /// <summary>The fixed transaction-scoped advisory lock key.</summary>
    public const long MigrationAdvisoryLockKey = 0x4E454B4F53544943L;

    /// <summary>The required design-time and startup connection-string environment variable.</summary>
    public const string ConnectionStringEnvironmentVariable = "NEKOSTICK_CONNECTION_STRING";

    /// <summary>The maximum JSON document size accepted by core persistence.</summary>
    public const int MaxJsonBytes = 1024 * 1024;

    /// <summary>The maximum route matcher pattern length.</summary>
    public const int MaxRoutePatternLength = 4096;
}
