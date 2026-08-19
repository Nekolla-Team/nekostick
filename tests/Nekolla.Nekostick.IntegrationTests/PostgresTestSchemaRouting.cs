using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.IntegrationTests;

#pragma warning disable EF1001 // Test-only Npgsql SQL-generation extension points.

/// <summary>Omits the canonical production schema from generated test SQL.</summary>
internal sealed class TestSchemaSqlGenerationHelper : NpgsqlSqlGenerationHelper
{
    /// <summary>Creates the test-only SQL generation helper.</summary>
    /// <param name="dependencies">The relational SQL generation dependencies.</param>
    public TestSchemaSqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override string DelimitIdentifier(string name, string? schema) =>
        IsCanonicalSchema(schema)
            ? base.DelimitIdentifier(name, null)
            : base.DelimitIdentifier(name, schema);

    /// <inheritdoc />
    public override void DelimitIdentifier(StringBuilder builder, string name, string? schema)
    {
        base.DelimitIdentifier(builder, name, IsCanonicalSchema(schema) ? null : schema);
    }

    private static bool IsCanonicalSchema(string? schema) =>
        string.Equals(schema, PersistenceDatabaseDefaults.Schema, StringComparison.Ordinal);
}

/// <summary>Suppresses only canonical schema DDL already owned by the test fixture.</summary>
internal sealed class TestSchemaMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
    /// <summary>Creates the test-only migrations SQL generator.</summary>
    /// <param name="dependencies">The migrations SQL generator dependencies.</param>
    /// <param name="npgsqlSingletonOptions">The configured Npgsql singleton options.</param>
    public TestSchemaMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, npgsqlSingletonOptions)
    {
    }

    /// <inheritdoc />
    protected override void Generate(
        SqlOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        var routedOperation = new SqlOperation
        {
            Sql = operation.Sql.Replace(
                "\"nekostick\".",
                string.Empty,
                StringComparison.Ordinal),
            SuppressTransaction = operation.SuppressTransaction
        };
        base.Generate(routedOperation, model, builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        EnsureSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        if (IsCanonicalSchema(operation.Name))
        {
            return;
        }

        base.Generate(operation, model, builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        if (IsCanonicalSchema(operation.Name))
        {
            return;
        }

        base.Generate(operation, model, builder);
    }

    private static bool IsCanonicalSchema(string name) =>
        string.Equals(name, PersistenceDatabaseDefaults.Schema, StringComparison.Ordinal);
}

#pragma warning restore EF1001
