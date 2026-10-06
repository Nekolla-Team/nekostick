using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the structural PostgreSQL check-constraint validation gate.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresCheckConstraintValidationTests
{
    private const string PortConstraint = "ck_port_leases_port";
    private const string GenerationConstraint = "ck_port_leases_generation_id_uuid_v7";
    private const string GenerationIndex = "ux_port_leases_node_id_service_id_generation_id";

    /// <summary>Rejects a missing check and accepts the same named check after restoration.</summary>
    [Fact]
    public async Task MissingPortCheckIsRejectedAndRecreatedCheckIsAccepted()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, cancellationToken);
        Assert.True(migrated.IsSuccess, "initial migration failed");

        var initialValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.True(initialValidation.IsValid, "initial schema validation failed");
        Assert.Empty(initialValidation.MissingObjects);

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("port_leases")} " +
            $"DROP CONSTRAINT \"{PortConstraint}\";");

        var missingValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.False(missingValidation.IsValid, "missing port check was accepted");
        Assert.Contains($"check:{PortConstraint}", missingValidation.MissingObjects);

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("port_leases")} " +
            $"ADD CONSTRAINT \"{PortConstraint}\" " +
            "CHECK (port BETWEEN 1 AND 65535);");

        var restoredValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.True(restoredValidation.IsValid, "restored port check was rejected");
        Assert.Empty(restoredValidation.MissingObjects);
    }

    /// <summary>Rejects a missing generation UUID check and accepts it after restoration.</summary>
    [Fact]
    public async Task MissingGenerationUuidCheckIsRejectedAndRecreatedCheckIsAccepted()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, cancellationToken);
        Assert.True(migrated.IsSuccess, "initial migration failed");

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("port_leases")} " +
            $"DROP CONSTRAINT \"{GenerationConstraint}\";");

        var missingValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.False(missingValidation.IsValid, "missing generation UUID check was accepted");
        Assert.Contains($"check:{GenerationConstraint}", missingValidation.MissingObjects);

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("port_leases")} " +
            $"ADD CONSTRAINT \"{GenerationConstraint}\" " +
            "CHECK (substring(generation_id::text, 15, 1) = '7' AND " +
            "substring(generation_id::text, 20, 1) IN ('8', '9', 'a', 'b'));");

        var restoredValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.True(restoredValidation.IsValid, "restored generation UUID check was rejected");
        Assert.Empty(restoredValidation.MissingObjects);
    }

    /// <summary>Rejects a missing unique generation lease index and accepts its restoration.</summary>
    [Fact]
    public async Task MissingGenerationLeaseIndexIsRejectedAndRecreatedIndexIsAccepted()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, cancellationToken);
        Assert.True(migrated.IsSuccess, "initial migration failed");

        await database.ExecuteSchemaCommandAsync(
            $"DROP INDEX {database.QualifiedRelation(GenerationIndex)};");

        var missingValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.False(missingValidation.IsValid, "missing generation lease index was accepted");
        Assert.Contains($"index:{GenerationIndex}", missingValidation.MissingObjects);

        await database.ExecuteSchemaCommandAsync(
            $"CREATE UNIQUE INDEX \"{GenerationIndex}\" " +
            $"ON {database.QualifiedRelation("port_leases")} (node_id, service_id, generation_id);");

        var restoredValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.True(restoredValidation.IsValid, "restored generation lease index was rejected");
        Assert.Empty(restoredValidation.MissingObjects);
    }

    /// <summary>Rejects a missing required column through the column-shape validation path.</summary>
    [Fact]
    public async Task MissingRequiredColumnFailsColumnShapeValidation()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var migrated = await database.CreateMigrationCoordinator()
            .MigrateAndValidateAsync(context, cancellationToken);
        Assert.True(migrated.IsSuccess, "initial migration failed");

        var initialValidation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.True(initialValidation.IsValid, "initial schema validation failed");
        Assert.Empty(initialValidation.MissingObjects);

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("port_leases")} " +
            "DROP COLUMN \"updated_at\";");

        var validation = await database.CreateMigrationSchemaValidator()
            .ValidateAsync(context, cancellationToken);
        Assert.False(validation.IsValid, "missing required column was accepted");
        Assert.Contains("port_leases:column-count", validation.MissingObjects);
        Assert.Contains("port_leases:updated_at", validation.MissingObjects);
    }
}
