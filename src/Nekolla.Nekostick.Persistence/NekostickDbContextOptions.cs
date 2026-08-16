using Microsoft.EntityFrameworkCore;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Creates provider options with the fixed Nekostick migration-history location.</summary>
public static class NekostickDbContextOptions
{
    /// <summary>Configures PostgreSQL with the shared Nekostick migration-history location.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="connectionString">The sensitive PostgreSQL connection string.</param>
    /// <returns>The configured options builder.</returns>
    public static DbContextOptionsBuilder UseNekostickPostgres(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        }

        optionsBuilder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(
                PersistenceDatabaseDefaults.MigrationHistoryTable,
                PersistenceDatabaseDefaults.Schema));
        return optionsBuilder;
    }

    /// <summary>Builds PostgreSQL options without enabling sensitive logging.</summary>
    /// <param name="connectionString">The sensitive PostgreSQL connection string.</param>
    /// <returns>Configured EF Core options.</returns>
    public static DbContextOptions<NekostickDbContext> Create(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        }

        var builder = new DbContextOptionsBuilder<NekostickDbContext>();
        builder.UseNekostickPostgres(connectionString);
        return builder.Options;
    }
}
