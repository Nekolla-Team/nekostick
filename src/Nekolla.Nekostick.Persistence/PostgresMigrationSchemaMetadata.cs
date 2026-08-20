namespace Nekolla.Nekostick.Persistence;

/// <summary>Defines the authored PostgreSQL schema objects and their catalog contracts.</summary>
internal static class PostgresMigrationSchemaMetadata
{
    internal static readonly string[] RequiredRelations =
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

    internal static readonly TableColumnCount[] RequiredTableColumnCounts =
    [
        new("configuration_revisions", 7),
        new("global_settings", 27),
        new("services", 15),
        new("extension_records", 7),
        new("nodes", 9),
        new("routes", 36),
        new("extension_settings", 7),
        new("port_leases", 9),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, 2)
    ];

    internal static readonly ColumnContract[] RequiredColumns =
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
        new("global_settings", "connect_timeout_milliseconds", "integer", false),
        new("global_settings", "http_activity_timeout_milliseconds", "integer", false),
        new("global_settings", "http_total_timeout_milliseconds", "integer", false),
        new("global_settings", "websocket_idle_timeout_milliseconds", "integer", false),
        new("global_settings", "client_ip_rate_queue_limit", "integer", true),
        new("global_settings", "client_ip_rate_rejection_behavior", "character varying(16)", true),
        new("global_settings", "client_ip_rate_replenishment_period_milliseconds", "integer", true),
        new("global_settings", "client_ip_rate_retry_after_behavior", "character varying(32)", true),
        new("global_settings", "client_ip_rate_token_limit", "bigint", true),
        new("global_settings", "client_ip_rate_tokens_per_period", "bigint", true),
        new("global_settings", "max_request_header_bytes", "bigint", false),
        new("global_settings", "request_read_timeout_milliseconds", "integer", false),
        new("global_settings", "proxy_max_retries", "integer", false),
        new("global_settings", "proxy_initial_retry_backoff_milliseconds", "integer", false),
        new("global_settings", "proxy_maximum_retry_backoff_milliseconds", "integer", false),
        new("global_settings", "proxy_retry_on_connection_failure", "boolean", false),
        new("global_settings", "proxy_retry_on_upstream_disconnect", "boolean", false),

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

        new("services", "owner_extension_id", "character varying(128)", true),

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
        new("routes", "client_ip_rate_queue_limit", "integer", true),
        new("routes", "client_ip_rate_rejection_behavior", "character varying(16)", true),
        new("routes", "client_ip_rate_replenishment_period_milliseconds", "integer", true),
        new("routes", "client_ip_rate_retry_after_behavior", "character varying(32)", true),
        new("routes", "client_ip_rate_token_limit", "bigint", true),
        new("routes", "client_ip_rate_tokens_per_period", "bigint", true),
        new("routes", "max_concurrent_requests", "integer", true),
        new("routes", "max_request_body_bytes", "bigint", true),
        new("routes", "max_request_header_bytes", "bigint", true),
        new("routes", "request_read_timeout_milliseconds", "integer", true),
        new("routes", "proxy_max_retries", "integer", true),
        new("routes", "proxy_initial_retry_backoff_milliseconds", "integer", true),
        new("routes", "proxy_maximum_retry_backoff_milliseconds", "integer", true),
        new("routes", "proxy_retry_on_connection_failure", "boolean", true),
        new("routes", "proxy_retry_on_upstream_disconnect", "boolean", true),

        new("routes", "owner_extension_id", "character varying(128)", true),

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

    internal static readonly int[] RequiredColumnOrdinals = CreateRequiredColumnOrdinals();

    internal static readonly ConstraintContract[] RequiredConstraints =
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

    internal static readonly CheckContract[] RequiredChecks =
    [
        new("configuration_revisions", "ck_configuration_revisions_id_uuid_v7"),
        new("configuration_revisions", "ck_configuration_revisions_singleton"),
        new("global_settings", "ck_global_settings_id_uuid_v7"),
        new("global_settings", "ck_global_settings_singleton"),
        new("global_settings", "ck_global_settings_port_range"),
        new("global_settings", "ck_global_settings_limits"),
        new("global_settings", "ck_global_settings_max_request_header_bytes"),
        new("global_settings", "ck_global_settings_max_request_body_bytes"),
        new("global_settings", "ck_global_settings_proxy_retries"),
        new("global_settings", "ck_global_settings_client_ip_rate_policy"),
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
        new("routes", "ck_routes_client_ip_rate_policy"),
        new("routes", "ck_routes_rewrite_metadata_json"),
        new("routes", "ck_routes_resource_limits"),
        new("routes", "ck_routes_proxy_retries"),
        new("extension_settings", "ck_extension_settings_id_uuid_v7"),
        new("extension_settings", "ck_extension_settings_schema_version"),
        new("extension_settings", "ck_extension_settings_json"),
        new("port_leases", "ck_port_leases_id_uuid_v7"),
        new("port_leases", "ck_port_leases_port")
    ];

    internal static readonly TableConstraintCount[] RequiredCheckCounts =
    [
        new("configuration_revisions", 2),
        new("global_settings", 10),
        new("services", 5),
        new("extension_records", 3),
        new("nodes", 3),
        new("routes", 10),
        new("extension_settings", 3),
        new("port_leases", 2),
        new(PersistenceDatabaseDefaults.MigrationHistoryTable, 0)
    ];

    internal static readonly IndexContract[] RequiredIndexes =
    [
        new("configuration_revisions", "ux_configuration_revisions_revision_key", true, "revision_key", 1, false, ""),
        new("routes", "ix_routes_enabled_matcher_type_priority", false, "enabled,matcher_type,priority", 3, false, ""),
        new("routes", "ix_routes_service_id", false, "service_id", 1, false, ""),
        new("routes", "ix_routes_owner_extension_id", false, "owner_extension_id", 1, false, ""),
        new("services", "ix_services_enabled", false, "enabled", 1, false, ""),
        new("services", "ix_services_owner_extension_id", false, "owner_extension_id", 1, false, ""),
        new("extension_records", "ux_extension_records_extension_id", true, "extension_id", 1, false, ""),
        new("extension_settings", "ux_extension_settings_extension_record_id", true, "extension_record_id", 1, false, ""),
        new("nodes", "ux_nodes_default_node_id_active", true, "node_id", 1, true, "%node_id%0%is_active%"),
        new("port_leases", "ux_port_leases_node_id_port", true, "node_id,port", 2, false, ""),
        new("port_leases", "ix_port_leases_service_id", false, "service_id", 1, false, "")
    ];

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

    internal sealed record TableColumnCount(string Table, int Count);

    internal sealed record ColumnContract(string Table, string Column, string Type, bool Nullable);

    internal sealed record ConstraintContract(
        string Table,
        string Name,
        string Type,
        string LocalColumns,
        string PrincipalTable,
        string PrincipalColumns);

    internal sealed record CheckContract(string Table, string Name);

    internal sealed record TableConstraintCount(string Table, int Count);

    internal sealed record IndexContract(
        string Table,
        string Name,
        bool IsUnique,
        string ColumnsCsv,
        int KeyCount,
        bool HasPredicate,
        string PredicatePattern);
}
