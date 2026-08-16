using System.Text.Json;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PhaseBPostgresContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises Phase B extension settings contracts against real PostgreSQL.</summary>
public sealed partial class PhaseBPostgresContractTests
{
    /// <summary>Verifies extension settings have independent versions and advance the global revision.</summary>
    [Fact]
    public async Task ExtensionSettingsReadWriteAndConflictUseSettingsVersions()
    {
        await using var test = await PhaseBPostgresContractTestScope.CreateAsync();
        var database = test.Database;
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var seed = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            CreateExtensionChangeSet(initial.Value),
            cancellationToken);
        Assert.True(seed.IsSuccess, seed.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, seed.NewVersion);

        var firstRead = await api.ReadExtensionSettingsAsync(ExtensionId, cancellationToken);
        Assert.True(firstRead.IsSuccess, firstRead.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(firstRead.Value);
        Assert.Equal(1, firstRead.Value!.SchemaVersion);
        Assert.Equal(1L, firstRead.Value!.Version);

        var update = new ExtensionSettingsConfiguration(
            ExtensionId,
            schemaVersion: 2,
            settingsJson: "{\"enabled\":false,\"limit\":10}",
            version: 1);
        var updateResult = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 1,
            settings: update,
            cancellationToken: cancellationToken);

        Assert.True(updateResult.IsSuccess, updateResult.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2L, updateResult.NewVersion);

        var secondRead = await api.ReadExtensionSettingsAsync(ExtensionId, cancellationToken);
        Assert.True(secondRead.IsSuccess, secondRead.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(secondRead.Value);
        Assert.Equal(2, secondRead.Value!.SchemaVersion);
        Assert.Equal(2L, secondRead.Value!.Version);
        using (var settingsDocument = JsonDocument.Parse(secondRead.Value!.SettingsJson))
        {
            Assert.False(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(10, settingsDocument.RootElement.GetProperty("limit").GetInt32());
        }

        var stale = await api.WriteExtensionSettingsAsync(
            ExtensionId,
            expectedVersion: 1,
            settings: new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 3,
                settingsJson: "{\"enabled\":true}",
                version: 1),
            cancellationToken: cancellationToken);

        Assert.False(stale.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.ConcurrencyConflict, stale.Errors.Single().Code);
        Assert.Null(stale.NewVersion);
        Assert.Equal(
            3L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
    }
}
