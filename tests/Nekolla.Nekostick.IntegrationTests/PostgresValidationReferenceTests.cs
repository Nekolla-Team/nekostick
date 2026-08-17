using System.Collections.Immutable;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;
using Xunit;
using static Nekolla.Nekostick.IntegrationTests.PostgresConfigurationContractTestData;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises PostgreSQL configuration validation and reference contracts.</summary>
public sealed partial class PostgresConfigurationContractTests
{
    /// <summary>Verifies a bad reference rejects the complete batch without partial rows or revision changes.</summary>
    [Fact]
    public async Task InvalidReferenceRollsBackTheCompleteSnapshotBatch()
    {
        await using var test = await PostgresConfigurationTestScope.CreateAsync();
        var database = test.Database;
        var api = test.Api;
        var cancellationToken = TestContext.Current.CancellationToken;

        var initial = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var service = CreateService(ServiceId, version: 0);
        var route = CreateRoute(MissingServiceId, version: 0);
        var changes = new ConfigurationChangeSet(
            CreateGlobalSettings(initial.Value!.GlobalSettings.Version, 23000),
            ImmutableArray.Create(route),
            ImmutableArray.Create(service),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        var write = await api.WriteSnapshotAsync(
            initial.Value!.Version,
            changes,
            cancellationToken);

        Assert.False(write.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Validation, write.Errors.Single().Code);
        Assert.Null(write.NewVersion);

        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("configuration_revisions")} " +
                "WHERE revision_key = @revision_key;",
                new NpgsqlParameter("revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey)));
        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT version FROM {database.QualifiedRelation("global_settings")};"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("services")};"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("routes")};"));
    }

}
