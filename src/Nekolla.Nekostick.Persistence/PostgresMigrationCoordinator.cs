using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Validates the authored PostgreSQL schema contract and its singleton seed rows.</summary>
public sealed class PostgresMigrationSchemaValidator : IMigrationSchemaValidator
{
    private const int PostgreSqlIdentifierMaxBytes = 63;

    private readonly string _schema;

    private static readonly string[] RequiredRelations =
    [
        "configuration_revisions",
        "routes",
        "services",
        "global_settings",
        "extension_records",
        "extension_settings",
        "nodes",
        "port_leases",
        PersistenceDatabaseDefaults.MigrationHistoryTable
    ];

    private static readonly TableColumnCount[] RequiredTableColumnCounts =
    [
        new("configuration_revisions", 7),
        new("global_settings", 10),
        new("services", 14),
        new("extension_records", 7),
        new("nodes", 9),
        new("routes", 20),
        new("extension_settings", 7),
        new("port_leases", 9),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, 2)
    ];

    private static readonly ColumnContract[] RequiredColumns =
    [
        new("configuration_revisions", "id", "uuid", false),
        new("configuration_revisions", "revision_key", "character varying(16)", false),
        new("configuration_revisions", "committed_at", "timestamp with time zone", false),
        new("configuration_revisions", "committed_by", "character varying(128)", true),
        new("configuration_revisions", "created_at", "timestamp with time zone", false),
        new("configuration_revisions", "updated_at", "timestamp with time zone", false),
        new("configuration_revisions", "version", "bigint", false),

        new("global_settings", "id", "uuid", false),
        new("global_settings", "auto_port_range_start", "integer", false),
        new("global_settings", "auto_port_range_end", "integer", false),
        new("global_settings", "max_request_body_bytes", "bigint", false),
        new("global_settings", "max_concurrent_requests", "integer", false),
        new("global_settings", "configuration_poll_interval_seconds", "integer", false),
        new("global_settings", "trusted_proxy_cidrs_json", "jsonb", false),
        new("global_settings", "created_at", "timestamp with time zone", false),
        new("global_settings", "updated_at", "timestamp with time zone", false),
        new("global_settings", "version", "bigint", false),

        new("services", "id", "uuid", false),
        new("services", "enabled", "boolean", false),
        new("services", "file_name", "character varying(4096)", false),
        new("services", "argument_list_json", "jsonb", false),
        new("services", "working_directory", "character varying(4096)", false),
        new("services", "environment_json", "jsonb", false),
        new("services", "start_mode", "character varying(16)", false),
        new("services", "restart_policy", "character varying(16)", false),
        new("services", "health_check_type", "character varying(16)", false),
        new("services", "health_check_http_path", "character varying(2048)", true),
        new("services", "health_check_timeout_milliseconds", "integer", false),
        new("services", "created_at", "timestamp with time zone", false),
        new("services", "updated_at", "timestamp with time zone", false),
        new("services", "version", "bigint", false),

        new("extension_records", "id", "uuid", false),
        new("extension_records", "extension_id", "character varying(128)", false),
        new("extension_records", "installed_version", "character varying(128)", false),
        new("extension_records", "load_state", "character varying(32)", false),
        new("extension_records", "created_at", "timestamp with time zone", false),
        new("extension_records", "updated_at", "timestamp with time zone", false),
        new("extension_records", "version", "bigint", false),

        new("nodes", "id", "uuid", false),
        new("nodes", "node_id", "character varying(128)", false),
        new("nodes", "last_heartbeat_at", "timestamp with time zone", false),
        new("nodes", "last_configuration_version", "bigint", false),
        new("nodes", "runtime_state", "character varying(32)", false),
        new("nodes", "is_active", "boolean", false),
        new("nodes", "created_at", "timestamp with time zone", false),
        new("nodes", "updated_at", "timestamp with time zone", false),
        new("nodes", "version", "bigint", false),

        new("routes", "id", "uuid", false),
        new("routes", "enabled", "boolean", false),
        new("routes", "matcher_type", "character varying(32)", false),
        new("routes", "pattern", "character varying(4096)", false),
        new("routes", "host_patterns_json", "jsonb", false),
        new("routes", "methods_json", "jsonb", false),
        new("routes", "target_type", "character varying(32)", false),
        new("routes", "target_id", "character varying(4096)", false),
        new("routes", "service_id", "uuid", true),
        new("routes", "static_root_path", "character varying(4096)", true),
        new("routes", "extension_handler_id", "character varying(256)", true),
        new("routes", "priority", "integer", false),
        new("routes", "forwarding_mode", "character varying(16)", false),
        new("routes", "replace_template", "character varying(4096)", true),
        new("routes", "request_header_rewrites_json", "jsonb", false),
        new("routes", "response_header_rewrites_json", "jsonb", false),
        new("routes", "metadata_json", "jsonb", false),
        new("routes", "created_at", "timestamp with time zone", false),
        new("routes", "updated_at", "timestamp with time zone", false),
        new("routes", "version", "bigint", false),

        new("extension_settings", "id", "uuid", false),
        new("extension_settings", "extension_record_id", "uuid", false),
        new("extension_settings", "schema_version", "integer", false),
        new("extension_settings", "settings_json", "jsonb", false),
        new("extension_settings", "created_at", "timestamp with time zone", false),
        new("extension_settings", "updated_at", "timestamp with time zone", false),
        new("extension_settings", "version", "bigint", false),

        new("port_leases", "id", "uuid", false),
        new("port_leases", "node_id", "character varying(128)", false),
        new("port_leases", "port", "integer", false),
        new("port_leases", "service_id", "uuid", false),
        new("port_leases", "lease_expires_at", "timestamp with time zone", false),
        new("port_leases", "renewed_at", "timestamp with time zone", false),
        new("port_leases", "version", "bigint", false),
        new("port_leases", "created_at", "timestamp with time zone", false),
        new("port_leases", "updated_at", "timestamp with time zone", false),

        new(PersistenceDatabaseDefaults.MigrationHistoryTable, "MigrationId", "character varying(150)", false),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, "ProductVersion", "character varying(32)", false)
    ];

    private static readonly int[] RequiredColumnOrdinals = CreateRequiredColumnOrdinals();

    private static readonly ConstraintContract[] RequiredConstraints =
    [
        new("configuration_revisions", "pk_configuration_revisions", "p", "id", "", ""),
        new("global_settings", "pk_global_settings", "p", "id", "", ""),
        new("services", "pk_services", "p", "id", "", ""),
        new("extension_records", "pk_extension_records", "p", "id", "", ""),
        new("nodes", "pk_nodes", "p", "id", "", ""),
        new("nodes", "ak_nodes_node_id", "u", "node_id", "", ""),
        new("routes", "pk_routes", "p", "id", "", ""),
        new("routes", "fk_routes_services_service_id", "f", "service_id", "services", "id"),
        new("extension_settings", "pk_extension_settings", "p", "id", "", ""),
        new("extension_settings", "fk_extension_settings_extension_records_extension_record_id", "f", "extension_record_id", "extension_records", "id"),
        new("port_leases", "pk_port_leases", "p", "id", "", ""),
        new("port_leases", "fk_port_leases_nodes_node_id", "f", "node_id", "nodes", "node_id"),
        new("port_leases", "fk_port_leases_services_service_id", "f", "service_id", "services", "id"),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, "PK___EFMigrationsHistory", "p", "MigrationId", "", "")
    ];

    private static readonly CheckContract[] RequiredChecks =
    [
        new("configuration_revisions", "ck_configuration_revisions_id_uuid_v7"),
        new("configuration_revisions", "ck_configuration_revisions_singleton"),
        new("global_settings", "ck_global_settings_id_uuid_v7"),
        new("global_settings", "ck_global_settings_singleton"),
        new("global_settings", "ck_global_settings_port_range"),
        new("global_settings", "ck_global_settings_limits"),
        new("global_settings", "ck_global_settings_trusted_proxy_cidrs_json"),
        new("services", "ck_services_id_uuid_v7"),
        new("services", "ck_services_paths"),
        new("services", "ck_services_process_json"),
        new("services", "ck_services_enum_values"),
        new("services", "ck_services_health"),
        new("extension_records", "ck_extension_records_id_uuid_v7"),
        new("extension_records", "ck_extension_records_text"),
        new("extension_records", "ck_extension_records_load_state"),
        new("nodes", "ck_nodes_id_uuid_v7"),
        new("nodes", "ck_nodes_node_id"),
        new("nodes", "ck_nodes_versions"),
        new("routes", "ck_routes_id_uuid_v7"),
        new("routes", "ck_routes_pattern_length"),
        new("routes", "ck_routes_matcher_json"),
        new("routes", "ck_routes_enum_values"),
        new("routes", "ck_routes_target_reference"),
        new("routes", "ck_routes_forwarding_template"),
        new("routes", "ck_routes_rewrite_metadata_json"),
        new("extension_settings", "ck_extension_settings_id_uuid_v7"),
        new("extension_settings", "ck_extension_settings_schema_version"),
        new("extension_settings", "ck_extension_settings_json"),
        new("port_leases", "ck_port_leases_id_uuid_v7"),
        new("port_leases", "ck_port_leases_port")
    ];

    private static readonly TableConstraintCount[] RequiredCheckCounts =
    [
        new("configuration_revisions", 2),
        new("global_settings", 5),
        new("services", 5),
        new("extension_records", 3),
        new("nodes", 3),
        new("routes", 7),
        new("extension_settings", 3),
        new("port_leases", 2),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, 0)
    ];

    private static readonly IndexContract[] RequiredIndexes =
    [
        new("configuration_revisions", "ux_configuration_revisions_revision_key", true, "revision_key", 1, false, ""),
        new("routes", "ix_routes_enabled_matcher_type_priority", false, "enabled,matcher_type,priority", 3, false, ""),
        new("routes", "ix_routes_service_id", false, "service_id", 1, false, ""),
        new("services", "ix_services_enabled", false, "enabled", 1, false, ""),
        new("extension_records", "ux_extension_records_extension_id", true, "extension_id", 1, false, ""),
        new("extension_settings", "ux_extension_settings_extension_record_id", true, "extension_record_id", 1, false, ""),
        new("nodes", "ux_nodes_default_node_id_active", true, "node_id", 1, true, "%node_id%0%is_active%"),
        new("port_leases", "ux_port_leases_node_id_port", true, "node_id,port", 2, false, ""),
        new("port_leases", "ix_port_leases_service_id", false, "service_id", 1, false, "")
    ];

    /// <summary>Creates a validator for the canonical production schema.</summary>
    public PostgresMigrationSchemaValidator()
        : this(PersistenceDatabaseDefaults.Schema)
    {
    }

    /// <summary>Creates a validator for a controlled PostgreSQL schema.</summary>
    /// <param name="schema">The non-empty lowercase ASCII PostgreSQL schema identifier to validate.</param>
    public PostgresMigrationSchemaValidator(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (!IsValidSchemaIdentifier(schema))
        {
            throw new ArgumentException(
                "The schema must be a non-empty lowercase ASCII PostgreSQL identifier of no more than 63 bytes.",
                nameof(schema));
        }

        _schema = schema;
    }

    /// <inheritdoc />
    public async Task<SchemaValidationResult> ValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            var missingRelations = await FindMissingRelationsAsync(connection, cancellationToken);
            if (missingRelations.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingRelations);
            }

            var missingColumns = await FindMissingColumnsAsync(connection, cancellationToken);
            if (missingColumns.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingColumns);
            }

            var missingConstraints = await FindMissingConstraintsAsync(connection, cancellationToken);
            if (missingConstraints.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingConstraints);
            }

            var missingChecks = await FindMissingChecksAsync(connection, cancellationToken);
            if (missingChecks.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingChecks);
            }

            var missingIndexes = await FindMissingIndexesAsync(connection, cancellationToken);
            if (missingIndexes.Count != 0)
            {
                return SchemaValidationResult.Invalid(missingIndexes);
            }

            var missingSeeds = await FindMissingSeedsAsync(connection, cancellationToken);
            return missingSeeds.Count == 0
                ? SchemaValidationResult.Valid()
                : SchemaValidationResult.Invalid(missingSeeds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DbException)
        {
            return SchemaValidationResult.Invalid(["database"]);
        }
        catch (Exception)
        {
            return SchemaValidationResult.Invalid(["database"]);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<List<string>> FindMissingRelationsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relation.relation_name,
                   to_regclass(@schema || '.' || quote_ident(relation.relation_name)) IS NOT NULL
            FROM unnest(@relations::text[]) AS relation(relation_name);
            """;
        AddTextParameter(command, "schema", _schema);
        AddTextArrayParameter(command, "relations", RequiredRelations);

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

    private async Task<List<string>> FindMissingColumnsAsync(
        DbConnection connection,
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
        AddTextParameter(countCommand, "schema", _schema);
        AddTextArrayParameter(countCommand, "table_names", RequiredTableColumnCounts.Select(value => value.Table));
        AddIntegerArrayParameter(countCommand, "expected_counts", RequiredTableColumnCounts.Select(value => value.Count));

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
        AddTextParameter(shapeCommand, "schema", _schema);
        AddTextArrayParameter(shapeCommand, "table_names", RequiredColumns.Select(value => value.Table));
        AddTextArrayParameter(shapeCommand, "column_names", RequiredColumns.Select(value => value.Column));
        AddTextArrayParameter(shapeCommand, "expected_types", RequiredColumns.Select(value => value.Type));
        AddBooleanArrayParameter(shapeCommand, "expected_nullable", RequiredColumns.Select(value => value.Nullable));
        AddIntegerArrayParameter(shapeCommand, "expected_ordinals", RequiredColumnOrdinals);

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

    private async Task<List<string>> FindMissingConstraintsAsync(
        DbConnection connection,
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
        AddTextParameter(command, "schema", _schema);
        AddTextArrayParameter(command, "table_names", RequiredConstraints.Select(value => value.Table));
        AddTextArrayParameter(command, "constraint_names", RequiredConstraints.Select(value => value.Name));
        AddTextArrayParameter(command, "constraint_types", RequiredConstraints.Select(value => value.Type));
        AddTextArrayParameter(command, "local_columns", RequiredConstraints.Select(value => value.LocalColumns));
        AddTextArrayParameter(command, "principal_tables", RequiredConstraints.Select(value => value.PrincipalTable));
        AddTextArrayParameter(command, "principal_columns", RequiredConstraints.Select(value => value.PrincipalColumns));

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

    private async Task<List<string>> FindMissingChecksAsync(
        DbConnection connection,
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
        AddTextParameter(countCommand, "schema", _schema);
        AddTextArrayParameter(countCommand, "table_names", RequiredCheckCounts.Select(value => value.Table));
        AddIntegerArrayParameter(countCommand, "expected_counts", RequiredCheckCounts.Select(value => value.Count));

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
        AddTextParameter(command, "schema", _schema);
        AddTextArrayParameter(command, "table_names", RequiredChecks.Select(value => value.Table));
        AddTextArrayParameter(command, "constraint_names", RequiredChecks.Select(value => value.Name));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var contract = RequiredChecks.First(value =>
                value.Table == reader.GetString(0) && value.Name == reader.GetString(1));
            if (!reader.GetBoolean(2))
            {
                missing.Add($"check:{contract.Name}");
            }
        }

        return missing;
    }

    private async Task<List<string>> FindMissingIndexesAsync(
        DbConnection connection,
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
        AddTextParameter(command, "schema", _schema);
        AddTextArrayParameter(command, "table_names", RequiredIndexes.Select(value => value.Table));
        AddTextArrayParameter(command, "index_names", RequiredIndexes.Select(value => value.Name));
        AddBooleanArrayParameter(command, "is_unique", RequiredIndexes.Select(value => value.IsUnique));
        AddTextArrayParameter(command, "columns_csv", RequiredIndexes.Select(value => value.ColumnsCsv));
        AddIntegerArrayParameter(command, "key_counts", RequiredIndexes.Select(value => value.KeyCount));
        AddBooleanArrayParameter(command, "has_predicate", RequiredIndexes.Select(value => value.HasPredicate));
        AddTextArrayParameter(command, "predicate_patterns", RequiredIndexes.Select(value => value.PredicatePattern));

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

    private async Task<List<string>> FindMissingSeedsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var quotedSchema = QuoteIdentifier(_schema);
        command.CommandText = $"""
            SELECT
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."configuration_revisions"
                    WHERE id = CAST(@revision_id AS uuid)
                      AND revision_key = @revision_key
                      AND version >= 1
                ),
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."global_settings"
                    WHERE id = CAST(@settings_id AS uuid)
                      AND version >= 1
                ),
                EXISTS (
                    SELECT 1
                    FROM {quotedSchema}."{PersistenceDatabaseDefaults.MigrationHistoryTable}"
                    WHERE RIGHT("MigrationId", LENGTH(@migration_suffix)) = @migration_suffix
                      AND NULLIF(BTRIM("MigrationId"), '') IS NOT NULL
                      AND NULLIF(BTRIM("ProductVersion"), '') IS NOT NULL
                );
            """;
        AddTextParameter(command, "revision_id", PersistenceDatabaseDefaults.SeedConfigurationRevisionId);
        AddTextParameter(command, "revision_key", PersistenceDatabaseDefaults.GlobalRevisionKey);
        AddTextParameter(command, "settings_id", PersistenceDatabaseDefaults.SeedGlobalSettingsId);
        AddTextParameter(
            command,
            "migration_suffix",
            $"_{PersistenceDatabaseDefaults.InitialMigrationName}");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ["seed-query"];
        }

        var missing = new List<string>();
        if (!reader.GetBoolean(0))
        {
            missing.Add("configuration_revisions:global");
        }

        if (!reader.GetBoolean(1))
        {
            missing.Add("global_settings:singleton");
        }

        if (!reader.GetBoolean(2))
        {
            missing.Add("__EFMigrationsHistory:initial");
        }

        return missing;
    }

    private static bool IsValidSchemaIdentifier(string schema)
    {
        if (schema.Length == 0 || schema.Length > PostgreSqlIdentifierMaxBytes)
        {
            return false;
        }

        if (!IsLowerAsciiLetter(schema[0]) && schema[0] != '_')
        {
            return false;
        }

        return schema.Skip(1).All(character =>
            IsLowerAsciiLetter(character) || character is >= '0' and <= '9' || character == '_');
    }

    private static bool IsLowerAsciiLetter(char character) => character is >= 'a' and <= 'z';

    private static string QuoteIdentifier(string identifier) =>
        "\u0022" + identifier + "\u0022";

    private static int[] CreateRequiredColumnOrdinals()
    {
        var ordinals = new int[RequiredColumns.Length];
        var nextOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < RequiredColumns.Length; index++)
        {
            var table = RequiredColumns[index].Table;
            var ordinal = nextOrdinals.TryGetValue(table, out var previousOrdinal)
                ? previousOrdinal + 1
                : 1;
            nextOrdinals[table] = ordinal;
            ordinals[index] = ordinal;
        }

        return ordinals;
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

    private sealed record TableColumnCount(string Table, int Count);

    private sealed record ColumnContract(string Table, string Column, string Type, bool Nullable);

    private sealed record ConstraintContract(
        string Table,
        string Name,
        string Type,
        string LocalColumns,
        string PrincipalTable,
        string PrincipalColumns);

    private sealed record CheckContract(string Table, string Name);

    private sealed record TableConstraintCount(string Table, int Count);

    private sealed record IndexContract(
        string Table,
        string Name,
        bool IsUnique,
        string ColumnsCsv,
        int KeyCount,
        bool HasPredicate,
        string PredicatePattern);
}

/// <summary>Serializes EF migration execution behind a fixed transaction-scoped advisory lock.</summary>
public sealed class PostgresMigrationCoordinator : IStartupDatabaseProbe
{
    private readonly string _connectionString;
    private readonly IMigrationSchemaValidator _schemaValidator;

    /// <summary>Creates a migration coordinator without enabling sensitive diagnostics.</summary>
    /// <param name="connectionString">The sensitive PostgreSQL connection string.</param>
    /// <param name="schemaValidator">The schema validator, or the PostgreSQL default.</param>
    public PostgresMigrationCoordinator(
        string connectionString,
        IMigrationSchemaValidator? schemaValidator = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _schemaValidator = schemaValidator ?? new PostgresMigrationSchemaValidator();
    }

    /// <inheritdoc />
    public async Task<StartupDatabaseResult> MigrateAndValidateAsync(
        NekostickDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        try
        {
            await using var lockConnection = new NpgsqlConnection(_connectionString);
            await lockConnection.OpenAsync(cancellationToken);
            await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken);
            await using var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@lock_key);",
                lockConnection,
                lockTransaction);
            lockCommand.Parameters.Add("lock_key", NpgsqlDbType.Bigint).Value =
                PersistenceDatabaseDefaults.MigrationAdvisoryLockKey;
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);

            await dbContext.Database.MigrateAsync(cancellationToken);
            var validation = await _schemaValidator.ValidateAsync(dbContext, cancellationToken);
            if (!validation.IsValid)
            {
                await lockTransaction.RollbackAsync(cancellationToken);
                return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.SchemaValidationFailed);
            }

            await lockTransaction.CommitAsync(cancellationToken);
            return StartupDatabaseResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.MigrationFailed);
        }
        catch (DbException)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.AdvisoryLockUnavailable);
        }
        catch (Exception)
        {
            return StartupDatabaseResult.Failure(StartupDatabaseErrorCode.MigrationFailed);
        }
    }
}
