using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Focuses on advisory-lock coordination and safe schema failure behavior.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresMigrationCoordinatorTests
{
    /// <summary>Verifies two real coordinator connections serialize behind the advisory lock.</summary>
    [Fact]
    public async Task TwoRealCoordinatorsSerializeBehindObservableAdvisoryLockBarrier()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();

        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstValidator = new BlockingSchemaValidator(
            database.CreateMigrationSchemaValidator(),
            firstEntered,
            releaseFirst);
        var secondValidator = new OrderedSchemaValidator(
            database.CreateMigrationSchemaValidator(),
            releaseFirst,
            secondEntered);

        var firstTask = database.CreateMigrationCoordinator(firstValidator)
            .MigrateAndValidateAsync(firstContext, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.True(await database.IsMigrationLockHeldAsync(TestContext.Current.CancellationToken));

        var secondTask = database.CreateMigrationCoordinator(secondValidator)
            .MigrateAndValidateAsync(secondContext, TestContext.Current.CancellationToken);
        await database.WaitForAdvisoryLockWaiterAsync(TestContext.Current.CancellationToken);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        await secondEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Verifies a missing singleton seed becomes a fixed schema-validation failure.</summary>
    [Fact]
    public async Task MissingSeedReturnsSafeSchemaValidationFailure()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var coordinator = database.CreateMigrationCoordinator();

        var migrated = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        await database.ExecuteSchemaCommandAsync(
            $"DELETE FROM {database.QualifiedRelation("global_settings")} WHERE id = @id;",
            new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId)
            });

        var validation = await database.CreateMigrationSchemaValidator().ValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(validation.IsValid);
        Assert.Contains("global_settings:singleton", validation.MissingObjects);

        var result = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            StartupDatabaseErrorCode.SchemaValidationFailed,
            result.Error?.Code);
        Assert.Equal("Database schema validation failed.", result.Error?.Message);
        Assert.NotNull(result.Error);
    }

    /// <summary>Verifies a missing required table becomes a fixed schema-validation failure.</summary>
    [Fact]
    public async Task MissingRequiredTableReturnsSafeSchemaValidationFailure()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var coordinator = database.CreateMigrationCoordinator();

        var migrated = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        await database.ExecuteSchemaCommandAsync(
            $"DROP TABLE {database.QualifiedRelation("routes")} CASCADE;");

        var validation = await database.CreateMigrationSchemaValidator().ValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(validation.IsValid);
        Assert.Contains("routes", validation.MissingObjects);

        var result = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            StartupDatabaseErrorCode.SchemaValidationFailed,
            result.Error?.Code);
        Assert.Equal("Database schema validation failed.", result.Error?.Message);
        Assert.False(result.Error!.Message.Contains(connectionString, StringComparison.Ordinal));
        Assert.NotNull(result.Error);
    }

    /// <summary>Verifies a missing authored check constraint fails the exact schema probe.</summary>
    [Fact]
    public async Task MissingRequiredCheckConstraintReturnsSafeSchemaValidationFailure()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var coordinator = database.CreateMigrationCoordinator();

        var migrated = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        await database.ExecuteSchemaCommandAsync(
            $"ALTER TABLE {database.QualifiedRelation("services")} " +
            "DROP CONSTRAINT \"ck_services_id_uuid_v7\";");

        var validation = await database.CreateMigrationSchemaValidator().ValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(validation.IsValid);
        Assert.Contains("check:ck_services_id_uuid_v7", validation.MissingObjects);

        var result = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            StartupDatabaseErrorCode.SchemaValidationFailed,
            result.Error?.Code);
        Assert.Equal("Database schema validation failed.", result.Error?.Message);
        Assert.False(result.Error!.Message.Contains(connectionString, StringComparison.Ordinal));
    }

    /// <summary>Verifies a missing authored index fails the exact schema probe.</summary>
    [Fact]
    public async Task MissingRequiredIndexReturnsSafeSchemaValidationFailure()
    {
        var connectionString = IntegrationTestBoundary.RequirePostgresConnectionString();
        await using var database = await PostgresTestDatabase.CreateAsync(connectionString);
        await using var context = database.CreateContext();
        var coordinator = database.CreateMigrationCoordinator();

        var migrated = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.True(migrated.IsSuccess, migrated.Error?.Message);

        await database.ExecuteSchemaCommandAsync(
            $"DROP INDEX {database.QualifiedRelation("ix_routes_service_id")};");

        var validation = await database.CreateMigrationSchemaValidator().ValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(validation.IsValid);
        Assert.Contains("index:ix_routes_service_id", validation.MissingObjects);

        var result = await coordinator.MigrateAndValidateAsync(
            context,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            StartupDatabaseErrorCode.SchemaValidationFailed,
            result.Error?.Code);
        Assert.Equal("Database schema validation failed.", result.Error?.Message);
        Assert.False(result.Error!.Message.Contains(connectionString, StringComparison.Ordinal));
    }

    private sealed class BlockingSchemaValidator : IMigrationSchemaValidator
    {
        private readonly IMigrationSchemaValidator inner;
        private readonly TaskCompletionSource entered;
        private readonly TaskCompletionSource release;

        internal BlockingSchemaValidator(
            IMigrationSchemaValidator inner,
            TaskCompletionSource entered,
            TaskCompletionSource release)
        {
            this.inner = inner;
            this.entered = entered;
            this.release = release;
        }

        public async Task<SchemaValidationResult> ValidateAsync(
            NekostickDbContext dbContext,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.ValidateAsync(dbContext, cancellationToken);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class OrderedSchemaValidator : IMigrationSchemaValidator
    {
        private readonly IMigrationSchemaValidator inner;
        private readonly TaskCompletionSource release;
        private readonly TaskCompletionSource entered;

        internal OrderedSchemaValidator(
            IMigrationSchemaValidator inner,
            TaskCompletionSource release,
            TaskCompletionSource entered)
        {
            this.inner = inner;
            this.release = release;
            this.entered = entered;
        }

        public async Task<SchemaValidationResult> ValidateAsync(
            NekostickDbContext dbContext,
            CancellationToken cancellationToken = default)
        {
            if (!release.Task.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Migration validators overlapped before the first lock holder released.");
            }

            entered.TrySetResult();
            return await inner.ValidateAsync(dbContext, cancellationToken);
        }
    }
}
