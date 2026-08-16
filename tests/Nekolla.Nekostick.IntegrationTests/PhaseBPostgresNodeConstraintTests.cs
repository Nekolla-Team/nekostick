using Npgsql;
using NpgsqlTypes;
using Nekolla.Nekostick.Persistence.Entities;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the Phase B PostgreSQL node constraint contract.</summary>
public sealed partial class PhaseBPostgresContractTests
{
    /// <summary>Verifies the active default node uniqueness guard rejects a second active registration.</summary>
    [Fact]
    public async Task SecondActiveDefaultNodeRegistrationIsRejected()
    {
        await using var test = await PhaseBPostgresContractTestScope.CreateAsync();
        var database = test.Database;
        var context = test.Context;

        var firstNode = new Node
        {
            Id = Guid.CreateVersion7(),
            NodeId = "0",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastConfigurationVersion = 1,
            RuntimeState = "ready",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        context.Nodes.Add(firstNode);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var secondNodeId = Guid.CreateVersion7();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteSchemaCommandAsync(
            $"""
            INSERT INTO {database.QualifiedRelation("nodes")}
                (id, node_id, last_heartbeat_at, last_configuration_version, runtime_state,
                 is_active, created_at, updated_at, version)
            VALUES (@id, '0', now(), 1, 'ready', TRUE, now(), now(), 1);
            """,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = secondNodeId }));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal(
            1L,
            await database.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {database.QualifiedRelation("nodes")} " +
                "WHERE node_id = '0' AND is_active;"));
    }
}
