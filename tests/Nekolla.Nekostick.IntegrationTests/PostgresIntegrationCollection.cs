using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Serializes PostgreSQL integration tests that share an external server.</summary>
[CollectionDefinition(nameof(PostgresIntegrationDefinition), DisableParallelization = true)]
public sealed class PostgresIntegrationDefinition
{
}
