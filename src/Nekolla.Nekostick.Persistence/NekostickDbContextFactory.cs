using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Provides safe EF design-time context creation from one environment variable.</summary>
public sealed class NekostickDbContextFactory : IDesignTimeDbContextFactory<NekostickDbContext>
{
    /// <summary>Creates a design-time context using the required environment variable.</summary>
    /// <param name="args">Design-time arguments, which are intentionally not interpreted.</param>
    /// <returns>A PostgreSQL-backed context.</returns>
    public NekostickDbContext CreateDbContext(string[] args)
    {
        _ = args;
        var connectionString = Environment.GetEnvironmentVariable(
            PersistenceDatabaseDefaults.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The required environment variable 'NEKOSTICK_CONNECTION_STRING' is missing.");
        }

        return new NekostickDbContext(NekostickDbContextOptions.Create(connectionString));
    }
}
