using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

internal static class IntegrationTestBoundary
{
    internal const string TestConnectionStringEnvironmentVariable = "NEKOSTICK_TEST_PG";

    internal static string RequirePostgresConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(TestConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip(
                "NEKOSTICK_TEST_PG is not set; PostgreSQL integration test skipped.");
        }

        return connectionString!;
    }
}
