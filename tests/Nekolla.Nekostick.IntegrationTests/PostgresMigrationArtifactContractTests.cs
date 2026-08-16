using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Checks the committed delivery migration artifact without PostgreSQL.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresMigrationArtifactContractTests
{
    private static readonly string[] RequiredArtifactIdentifiers =
    [
        "configuration_revisions",
        "global_settings",
        "routes",
        "services",
        "extension_records",
        "extension_settings",
        "nodes",
        "port_leases",
        PersistenceDatabaseDefaults.MigrationHistoryTable,
        PersistenceDatabaseDefaults.SeedConfigurationRevisionId,
        PersistenceDatabaseDefaults.SeedGlobalSettingsId,
        PersistenceDatabaseDefaults.InitialMigrationName
    ];

    private static readonly string[] ForbiddenConnectionMarkers =
    [
        "NEKOSTICK_TEST_PG",
        "NEKOSTICK_CONNECTION_STRING",
        "Host=",
        "Server=",
        "Port=",
        "Database=",
        "User ID=",
        "Username=",
        "Password=",
        "postgres://",
        "postgresql://"
    ];

    /// <summary>Verifies the delivery script contains the complete initial schema contract.</summary>
    [Fact]
    public Task DeliveryMigrationArtifactContainsCompleteInitialSchema()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        var artifactPath = FindMigrationArtifact();
        Assert.True(artifactPath is not null, "migration artifact was not found");
        var artifact = File.ReadAllText(artifactPath!);
        Assert.True(!string.IsNullOrWhiteSpace(artifact), "migration artifact is empty");

        foreach (var identifier in RequiredArtifactIdentifiers)
        {
            Assert.True(
                artifact.Contains(identifier, StringComparison.Ordinal),
                "migration artifact is missing a required identifier");
        }

        Assert.True(
            artifact.Contains("CREATE SCHEMA", StringComparison.OrdinalIgnoreCase),
            "migration artifact does not create the delivery schema");
        Assert.True(
            artifact.Contains(PersistenceDatabaseDefaults.Schema, StringComparison.Ordinal),
            "migration artifact does not name the delivery schema");
        Assert.True(
            artifact.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase),
            "migration artifact does not create delivery tables");
        Assert.True(
            artifact.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase),
            "migration artifact is not idempotent");

        foreach (var marker in ForbiddenConnectionMarkers)
        {
            Assert.False(
                artifact.Contains(marker, StringComparison.OrdinalIgnoreCase),
                "migration artifact contains a connection marker");
        }

        return Task.CompletedTask;
    }

    private static string? FindMigrationArtifact()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Nekolla.Nekostick.Persistence",
                "Migrations",
                "NekostickDbContext.migrations.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
