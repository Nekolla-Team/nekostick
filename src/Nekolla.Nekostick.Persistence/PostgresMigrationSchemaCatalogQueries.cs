using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Queries PostgreSQL catalogs for the authored schema contract.</summary>
internal static class PostgresMigrationSchemaCatalogQueries
{
    internal static async Task<List<string>> FindMissingRelationsAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relation.relation_name,
                   to_regclass(@schema || '.' || quote_ident(relation.relation_name)) IS NOT NULL
            FROM unnest(@relations::text[]) AS relation(relation_name);
            """;
        AddTextParameter(command, "schema", schema);
        AddTextArrayParameter(command, "relations", PostgresMigrationSchemaMetadata.RequiredRelations);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var missing = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.GetBoolean(1))
            {
                missing.Add(reader.GetString(0));
            }
        }

        return missing;
    }

    internal static async Task<List<string>> FindMissingColumnsAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT expected.table_name,
                   COUNT(attribute.attnum) = expected.expected_count
            FROM unnest(@table_names::text[], @expected_counts::integer[])
                AS expected(table_name, expected_count)
            LEFT JOIN pg_catalog.pg_namespace AS schema_row
                ON schema_row.nspname = @schema
            LEFT JOIN pg_catalog.pg_class AS table_row
                ON table_row.relnamespace = schema_row.oid
               AND table_row.relname = expected.table_name
               AND table_row.relkind = 'r'
            LEFT JOIN pg_catalog.pg_attribute AS attribute
                ON attribute.attrelid = table_row.oid
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped
            GROUP BY expected.table_name, expected.expected_count;
            """;
        AddTextParameter(countCommand, "schema", schema);
        AddTextArrayParameter(
            countCommand,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredTableColumnCounts.Select(value => value.Table));
        AddIntegerArrayParameter(
            countCommand,
            "expected_counts",
            PostgresMigrationSchemaMetadata.RequiredTableColumnCounts.Select(value => value.Count));

        var missing = new List<string>();
        await using (var countReader = await countCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await countReader.ReadAsync(cancellationToken))
            {
                if (!countReader.GetBoolean(1))
                {
                    missing.Add($"{countReader.GetString(0)}:column-count");
                }
            }
        }

        await using var shapeCommand = connection.CreateCommand();
        shapeCommand.CommandText = """
            SELECT expected.table_name,
                   expected.column_name,
                   COALESCE(
                       table_row.oid IS NOT NULL
                       AND attribute.attname IS NOT NULL
                       AND pg_catalog.format_type(attribute.atttypid, attribute.atttypmod) = expected.expected_type
                       AND attribute.attnotnull = NOT expected.expected_nullable
                       AND attribute.attnum = expected.expected_ordinal,
                       FALSE)
            FROM unnest(
                    @table_names::text[],
                    @column_names::text[],
                    @expected_types::text[],
                    @expected_nullable::boolean[],
                    @expected_ordinals::integer[])
                AS expected(table_name, column_name, expected_type, expected_nullable, expected_ordinal)
            LEFT JOIN pg_catalog.pg_namespace AS schema_row
                ON schema_row.nspname = @schema
            LEFT JOIN pg_catalog.pg_class AS table_row
                ON table_row.relnamespace = schema_row.oid
               AND table_row.relname = expected.table_name
               AND table_row.relkind = 'r'
            LEFT JOIN pg_catalog.pg_attribute AS attribute
                ON attribute.attrelid = table_row.oid
               AND attribute.attname = expected.column_name
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped;
            """;
        AddTextParameter(shapeCommand, "schema", schema);
        AddTextArrayParameter(
            shapeCommand,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredColumns.Select(value => value.Table));
        AddTextArrayParameter(
            shapeCommand,
            "column_names",
            PostgresMigrationSchemaMetadata.RequiredColumns.Select(value => value.Column));
        AddTextArrayParameter(
            shapeCommand,
            "expected_types",
            PostgresMigrationSchemaMetadata.RequiredColumns.Select(value => value.Type));
        AddBooleanArrayParameter(
            shapeCommand,
            "expected_nullable",
            PostgresMigrationSchemaMetadata.RequiredColumns.Select(value => value.Nullable));
        AddIntegerArrayParameter(
            shapeCommand,
            "expected_ordinals",
            PostgresMigrationSchemaMetadata.RequiredColumnOrdinals);

        await using var shapeReader = await shapeCommand.ExecuteReaderAsync(cancellationToken);
        while (await shapeReader.ReadAsync(cancellationToken))
        {
            if (!shapeReader.GetBoolean(2))
            {
                missing.Add($"{shapeReader.GetString(0)}:{shapeReader.GetString(1)}");
            }
        }

        return missing;
    }

    internal static async Task<List<string>> FindMissingConstraintsAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT expected.constraint_name,
                   EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_constraint AS constraint_row
                       JOIN pg_catalog.pg_class AS table_row
                         ON table_row.oid = constraint_row.conrelid
                        AND table_row.relkind = 'r'
                       JOIN pg_catalog.pg_namespace AS schema_row
                         ON schema_row.oid = table_row.relnamespace
                        AND schema_row.nspname = @schema
                       LEFT JOIN pg_catalog.pg_class AS principal_table
                         ON principal_table.oid = constraint_row.confrelid
                       LEFT JOIN pg_catalog.pg_namespace AS principal_schema
                         ON principal_schema.oid = principal_table.relnamespace
                       WHERE table_row.relname = expected.table_name
                         AND constraint_row.conname = expected.constraint_name
                         AND constraint_row.contype::text = expected.constraint_type
                         AND constraint_row.convalidated
                         AND COALESCE((
                             SELECT string_agg(local_attribute.attname, ',' ORDER BY local_key.ordinality)
                             FROM unnest(constraint_row.conkey) WITH ORDINALITY AS local_key(attnum, ordinality)
                             JOIN pg_catalog.pg_attribute AS local_attribute
                               ON local_attribute.attrelid = constraint_row.conrelid
                              AND local_attribute.attnum = local_key.attnum
                         ), '') = expected.local_columns
                         AND (
                             expected.constraint_type <> 'f'
                             OR (
                                 constraint_row.confdeltype = 'r'
                                 AND constraint_row.confupdtype = 'a'
                                 AND principal_schema.nspname = @schema
                                 AND principal_table.relname = expected.principal_table
                                 AND COALESCE((
                                     SELECT string_agg(principal_attribute.attname, ',' ORDER BY principal_key.ordinality)
                                     FROM unnest(constraint_row.confkey) WITH ORDINALITY AS principal_key(attnum, ordinality)
                                     JOIN pg_catalog.pg_attribute AS principal_attribute
                                       ON principal_attribute.attrelid = constraint_row.confrelid
                                      AND principal_attribute.attnum = principal_key.attnum
                                 ), '') = expected.principal_columns
                             )
                         )
                   )
            FROM unnest(
                    @table_names::text[],
                    @constraint_names::text[],
                    @constraint_types::text[],
                    @local_columns::text[],
                    @principal_tables::text[],
                    @principal_columns::text[])
                AS expected(table_name, constraint_name, constraint_type, local_columns, principal_table, principal_columns);
            """;
        AddTextParameter(command, "schema", schema);
        AddTextArrayParameter(
            command,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.Table));
        AddTextArrayParameter(
            command,
            "constraint_names",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.Name));
        AddTextArrayParameter(
            command,
            "constraint_types",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.Type));
        AddTextArrayParameter(
            command,
            "local_columns",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.LocalColumns));
        AddTextArrayParameter(
            command,
            "principal_tables",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.PrincipalTable));
        AddTextArrayParameter(
            command,
            "principal_columns",
            PostgresMigrationSchemaMetadata.RequiredConstraints.Select(value => value.PrincipalColumns));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var missing = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.GetBoolean(1))
            {
                missing.Add($"constraint:{reader.GetString(0)}");
            }
        }

        return missing;
    }

    internal static async Task<List<string>> FindMissingChecksAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT expected.table_name,
                   COUNT(check_constraint.oid) = expected.expected_count
            FROM unnest(@table_names::text[], @expected_counts::integer[])
                AS expected(table_name, expected_count)
            LEFT JOIN pg_catalog.pg_namespace AS schema_row
                ON schema_row.nspname = @schema
            LEFT JOIN pg_catalog.pg_class AS table_row
                ON table_row.relnamespace = schema_row.oid
               AND table_row.relname = expected.table_name
               AND table_row.relkind = 'r'
            LEFT JOIN pg_catalog.pg_constraint AS check_constraint
                ON check_constraint.conrelid = table_row.oid
               AND check_constraint.contype = 'c'
            GROUP BY expected.table_name, expected.expected_count;
            """;
        AddTextParameter(countCommand, "schema", schema);
        AddTextArrayParameter(
            countCommand,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredCheckCounts.Select(value => value.Table));
        AddIntegerArrayParameter(
            countCommand,
            "expected_counts",
            PostgresMigrationSchemaMetadata.RequiredCheckCounts.Select(value => value.Count));

        var missing = new List<string>();
        await using (var countReader = await countCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await countReader.ReadAsync(cancellationToken))
            {
                if (!countReader.GetBoolean(1))
                {
                    missing.Add($"{countReader.GetString(0)}:check-count");
                }
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT expected.table_name,
                   expected.constraint_name,
                   EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_constraint AS constraint_row
                       JOIN pg_catalog.pg_class AS table_row
                         ON table_row.oid = constraint_row.conrelid
                        AND table_row.relkind = 'r'
                       JOIN pg_catalog.pg_namespace AS schema_row
                         ON schema_row.oid = table_row.relnamespace
                        AND schema_row.nspname = @schema
                       WHERE table_row.relname = expected.table_name
                         AND constraint_row.conname = expected.constraint_name
                         AND constraint_row.contype = 'c'
                         AND constraint_row.convalidated
                   )
            FROM unnest(@table_names::text[], @constraint_names::text[])
                AS expected(table_name, constraint_name);
            """;
        AddTextParameter(command, "schema", schema);
        AddTextArrayParameter(
            command,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredChecks.Select(value => value.Table));
        AddTextArrayParameter(
            command,
            "constraint_names",
            PostgresMigrationSchemaMetadata.RequiredChecks.Select(value => value.Name));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var contract = PostgresMigrationSchemaMetadata.RequiredChecks.First(value =>
                value.Table == reader.GetString(0) && value.Name == reader.GetString(1));
            if (!reader.GetBoolean(2))
            {
                missing.Add($"check:{contract.Name}");
            }
        }

        return missing;
    }

    internal static async Task<List<string>> FindMissingIndexesAsync(
        DbConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT expected.index_name,
                   EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_index AS index_row
                       JOIN pg_catalog.pg_class AS table_row
                         ON table_row.oid = index_row.indrelid
                        AND table_row.relkind = 'r'
                       JOIN pg_catalog.pg_namespace AS schema_row
                         ON schema_row.oid = table_row.relnamespace
                        AND schema_row.nspname = @schema
                       JOIN pg_catalog.pg_class AS index_class
                         ON index_class.oid = index_row.indexrelid
                        AND index_class.relkind = 'i'
                       WHERE table_row.relname = expected.table_name
                         AND index_class.relname = expected.index_name
                         AND index_row.indisunique = expected.is_unique
                         AND index_row.indisvalid
                         AND index_row.indisready
                         AND index_row.indnkeyatts = expected.key_count
                         AND index_row.indnatts = expected.key_count
                         AND COALESCE((
                             SELECT string_agg(attribute.attname, ',' ORDER BY index_key.ordinality)
                             FROM unnest(index_row.indkey) WITH ORDINALITY AS index_key(attnum, ordinality)
                             JOIN pg_catalog.pg_attribute AS attribute
                               ON attribute.attrelid = index_row.indrelid
                              AND attribute.attnum = index_key.attnum
                             WHERE index_key.ordinality <= index_row.indnkeyatts
                         ), '') = expected.columns_csv
                         AND (
                             (NOT expected.has_predicate AND index_row.indpred IS NULL)
                             OR (
                                 expected.has_predicate
                                 AND index_row.indpred IS NOT NULL
                                 AND pg_catalog.pg_get_expr(index_row.indpred, index_row.indrelid, TRUE)
                                     ILIKE expected.predicate_pattern
                             )
                         )
                   )
            FROM unnest(
                    @table_names::text[],
                    @index_names::text[],
                    @is_unique::boolean[],
                    @columns_csv::text[],
                     @key_counts::integer[],
                    @has_predicate::boolean[],
                    @predicate_patterns::text[])
                AS expected(table_name, index_name, is_unique, columns_csv, key_count, has_predicate, predicate_pattern);
            """;
        AddTextParameter(command, "schema", schema);
        AddTextArrayParameter(
            command,
            "table_names",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.Table));
        AddTextArrayParameter(
            command,
            "index_names",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.Name));
        AddBooleanArrayParameter(
            command,
            "is_unique",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.IsUnique));
        AddTextArrayParameter(
            command,
            "columns_csv",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.ColumnsCsv));
        AddIntegerArrayParameter(
            command,
            "key_counts",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.KeyCount));
        AddBooleanArrayParameter(
            command,
            "has_predicate",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.HasPredicate));
        AddTextArrayParameter(
            command,
            "predicate_patterns",
            PostgresMigrationSchemaMetadata.RequiredIndexes.Select(value => value.PredicatePattern));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var missing = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.GetBoolean(1))
            {
                missing.Add($"index:{reader.GetString(0)}");
            }
        }

        return missing;
    }

    private static void AddTextParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddTextArrayParameter(DbCommand command, string name, IEnumerable<string> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
        }

        command.Parameters.Add(parameter);
    }

    private static void AddIntegerArrayParameter(DbCommand command, string name, IEnumerable<int> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        }

        command.Parameters.Add(parameter);
    }

    private static void AddBooleanArrayParameter(DbCommand command, string name, IEnumerable<bool> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean;
        }

        command.Parameters.Add(parameter);
    }
}
