using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PostgresConfigurationContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises PostgreSQL bootstrap extension-record persistence contracts.</summary>
public sealed partial class PostgresConfigurationContractTests
{
    /// <summary>Verifies bootstrap records are persisted once before extension settings are created.</summary>
    [Fact]
    public async Task DiscoveredExtensionRecordsPersistOnceThenCreateInitialSettings()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        Assert.Empty(initial.Value!.ExtensionRecords);
        Assert.Empty(initial.Value.ExtensionSettings);

        var record = new ExtensionRecordConfiguration(
            ExtensionId,
            version: "1.2.3",
            loadState: ExtensionLoadState.Loaded,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            recordVersion: 0);
        var records = ImmutableArray.Create(record);

        var bootstrap = await api.PersistDiscoveredExtensionRecordsAsync(
            initial.Value.Version,
            records,
            cancellationToken);

        Assert.True(bootstrap.IsSuccess, bootstrap.Errors.FirstOrDefault()?.Message);
        Assert.Equal(initial.Value.Version + 1, bootstrap.NewVersion);
        var bootstrapRevision = bootstrap.NewVersion!.Value;

        var persisted = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(persisted.IsSuccess, persisted.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(persisted.Value);
        Assert.Equal(bootstrapRevision, persisted.Value!.Version);
        var persistedRecord = Assert.Single(persisted.Value.ExtensionRecords);
        Assert.Equal(ExtensionId, persistedRecord.ExtensionId);
        Assert.Equal("1.2.3", persistedRecord.Version);
        Assert.Equal(ExtensionLoadState.Loaded, persistedRecord.LoadState);
        Assert.Equal(1L, persistedRecord.RecordVersion);
        Assert.Empty(persisted.Value.ExtensionSettings);

        var missingSettings = await api.ReadExtensionSettingsAsync(
            ExtensionId,
            cancellationToken);
        Assert.False(missingSettings.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.NotFound, missingSettings.Errors.Single().Code);

        var staleBootstrap = await api.PersistDiscoveredExtensionRecordsAsync(
            initial.Value.Version,
            records,
            cancellationToken);

        Assert.False(staleBootstrap.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, staleBootstrap.Errors.Single().Code);
        Assert.Null(staleBootstrap.NewVersion);

        var repeatedBootstrap = await api.PersistDiscoveredExtensionRecordsAsync(
            bootstrapRevision,
            records,
            cancellationToken);

        Assert.True(repeatedBootstrap.IsSuccess, repeatedBootstrap.Errors.FirstOrDefault()?.Message);
        Assert.Equal(bootstrapRevision, repeatedBootstrap.NewVersion);

        var unchanged = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(unchanged.IsSuccess, unchanged.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(unchanged.Value);
        Assert.Equal(bootstrapRevision, unchanged.Value!.Version);
        Assert.Single(unchanged.Value.ExtensionRecords);
        Assert.Empty(unchanged.Value.ExtensionSettings);

        var settingsWrite = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 0,
            settings: new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 2,
                settingsJson: "{\"enabled\":true,\"limit\":7}",
                version: 0),
            cancellationToken: cancellationToken);

        Assert.True(settingsWrite.IsSuccess, settingsWrite.Errors.FirstOrDefault()?.Message);
        Assert.Equal(1L, settingsWrite.NewVersion);

        var settings = await api.ReadExtensionSettingsAsync(
            ExtensionId,
            cancellationToken);
        Assert.True(settings.IsSuccess, settings.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(settings.Value);
        Assert.Equal(ExtensionId, settings.Value!.ExtensionId);
        Assert.Equal(2, settings.Value.SchemaVersion);
        Assert.Equal(1L, settings.Value.Version);
        using (var settingsDocument = JsonDocument.Parse(settings.Value.SettingsJson))
        {
            Assert.True(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(7, settingsDocument.RootElement.GetProperty("limit").GetInt32());
        }

        var afterSettings = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(afterSettings.IsSuccess, afterSettings.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterSettings.Value);
        Assert.Equal(bootstrapRevision + 1, afterSettings.Value!.Version);
        Assert.Single(afterSettings.Value.ExtensionRecords);
        Assert.Single(afterSettings.Value.ExtensionSettings);
        Assert.Equal(1L, afterSettings.Value.ExtensionSettings.Single().Version);

        var repeatedSettingsWrite = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 0,
            settings: new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 2,
                settingsJson: "{\"enabled\":false,\"limit\":99}",
                version: 0),
            cancellationToken: cancellationToken);

        Assert.False(repeatedSettingsWrite.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, repeatedSettingsWrite.Errors.Single().Code);
        Assert.Null(repeatedSettingsWrite.NewVersion);

        var final = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(final.IsSuccess, final.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(final.Value);
        Assert.Equal(bootstrapRevision + 1, final.Value!.Version);
        Assert.Single(final.Value.ExtensionRecords);
        Assert.Single(final.Value.ExtensionSettings);
        Assert.Equal(1L, final.Value.ExtensionSettings.Single().Version);
    }

    /// <summary>Verifies concurrent bootstrap writers converge with a retryable conflict for the loser.</summary>
    [Fact]
    public async Task ConcurrentDiscoveredExtensionRecordBootstrapsConflictAndConverge()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        await using var secondContext = test.Database.CreateContext();
        await using var secondApi = new EfHostConfigApi(secondContext);
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var now = DateTimeOffset.UtcNow;
        var records = ImmutableArray.Create(new ExtensionRecordConfiguration(
            ExtensionId,
            version: "1.2.3",
            loadState: ExtensionLoadState.Loaded,
            createdAt: now,
            updatedAt: now,
            recordVersion: 0));
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ConfigurationWriteResult> Persist(EfHostConfigApi contender) => PersistCore(contender);
        async Task<ConfigurationWriteResult> PersistCore(EfHostConfigApi contender)
        {
            await startGate.Task.WaitAsync(cancellationToken);
            return await contender.PersistDiscoveredExtensionRecordsAsync(
                initial.Value!.Version,
                records,
                cancellationToken);
        }

        var firstTask = Persist(api);
        var secondTask = Persist(secondApi);
        startGate.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, static result => result.IsSuccess);
        var loser = results.Single(static result => !result.IsSuccess);
        var loserError = loser.Errors.Single();
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, loserError.Code);

        var winner = results.Single(static result => result.IsSuccess);
        var persisted = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(persisted.IsSuccess, persisted.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(persisted.Value);
        Assert.Equal(winner.NewVersion, persisted.Value!.Version);
        var record = Assert.Single(persisted.Value.ExtensionRecords);
        Assert.Equal(ExtensionId, record.ExtensionId);
        Assert.Equal(1L, record.RecordVersion);
        Assert.Empty(persisted.Value.ExtensionSettings);
    }
}
