using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nekostick");

            migrationBuilder.CreateTable(
                name: "configuration_revisions",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    committed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_revisions", x => x.id);
                    table.CheckConstraint("ck_configuration_revisions_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_configuration_revisions_singleton", "id = '018f0f00-0000-7000-8000-000000000001'::uuid AND revision_key = 'global'");
                });

            migrationBuilder.CreateTable(
                name: "extension_records",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    extension_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    installed_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    load_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extension_records", x => x.id);
                    table.CheckConstraint("ck_extension_records_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_extension_records_load_state", "load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading')");
                    table.CheckConstraint("ck_extension_records_text", "length(extension_id) BETWEEN 1 AND 128 AND length(installed_version) BETWEEN 1 AND 128");
                });

            migrationBuilder.CreateTable(
                name: "global_settings",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    auto_port_range_start = table.Column<int>(type: "integer", nullable: false),
                    auto_port_range_end = table.Column<int>(type: "integer", nullable: false),
                    max_request_body_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_concurrent_requests = table.Column<int>(type: "integer", nullable: false),
                    configuration_poll_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    trusted_proxy_cidrs_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_global_settings", x => x.id);
                    table.CheckConstraint("ck_global_settings_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_global_settings_limits", "max_request_body_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0");
                    table.CheckConstraint("ck_global_settings_port_range", "auto_port_range_start BETWEEN 1 AND 65535 AND auto_port_range_end BETWEEN 1 AND 65535 AND auto_port_range_start <= auto_port_range_end");
                    table.CheckConstraint("ck_global_settings_singleton", "id = '018f0f00-0000-7000-8000-000000000002'::uuid");
                    table.CheckConstraint("ck_global_settings_trusted_proxy_cidrs_json", "jsonb_typeof(trusted_proxy_cidrs_json) = 'array' AND octet_length(trusted_proxy_cidrs_json::text) <= 262144");
                });

            migrationBuilder.CreateTable(
                name: "nodes",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    runtime_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nodes", x => x.id);
                    table.UniqueConstraint("ak_nodes_node_id", x => x.node_id);
                    table.CheckConstraint("ck_nodes_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_nodes_node_id", "length(node_id) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_nodes_versions", "last_configuration_version >= 0 AND length(runtime_state) BETWEEN 1 AND 32");
                });

            migrationBuilder.CreateTable(
                name: "services",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    file_name = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    argument_list_json = table.Column<string>(type: "jsonb", nullable: false),
                    working_directory = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    environment_json = table.Column<string>(type: "jsonb", nullable: false),
                    start_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    restart_policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    health_check_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    health_check_http_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    health_check_timeout_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_services", x => x.id);
                    table.CheckConstraint("ck_services_enum_values", "start_mode IN ('Eager', 'Lazy') AND restart_policy IN ('Never', 'OnFailure', 'Always') AND health_check_type IN ('Process', 'Tcp', 'Http')");
                    table.CheckConstraint("ck_services_health", "health_check_timeout_milliseconds > 0 AND ((health_check_type = 'Http' AND health_check_http_path IS NOT NULL) OR (health_check_type <> 'Http' AND health_check_http_path IS NULL))");
                    table.CheckConstraint("ck_services_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_services_paths", "length(file_name) BETWEEN 1 AND 4096 AND length(working_directory) BETWEEN 1 AND 4096");
                    table.CheckConstraint("ck_services_process_json", "jsonb_typeof(argument_list_json) = 'array' AND jsonb_typeof(environment_json) = 'object' AND octet_length(argument_list_json::text) <= 1048576 AND octet_length(environment_json::text) <= 1048576");
                });

            migrationBuilder.CreateTable(
                name: "extension_settings",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    extension_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extension_settings", x => x.id);
                    table.CheckConstraint("ck_extension_settings_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_extension_settings_json", "jsonb_typeof(settings_json) IS NOT NULL AND octet_length(settings_json::text) <= 1048576");
                    table.CheckConstraint("ck_extension_settings_schema_version", "schema_version >= 0");
                    table.ForeignKey(
                        name: "fk_extension_settings_extension_records_extension_record_id",
                        column: x => x.extension_record_id,
                        principalSchema: "nekostick",
                        principalTable: "extension_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "port_leases",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    renewed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_port_leases", x => x.id);
                    table.CheckConstraint("ck_port_leases_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_port_leases_port", "port BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_port_leases_nodes_node_id",
                        column: x => x.node_id,
                        principalSchema: "nekostick",
                        principalTable: "nodes",
                        principalColumn: "node_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_port_leases_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "nekostick",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                schema: "nekostick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    matcher_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pattern = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    host_patterns_json = table.Column<string>(type: "jsonb", nullable: false),
                    methods_json = table.Column<string>(type: "jsonb", nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    static_root_path = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    extension_handler_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    forwarding_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    replace_template = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    request_header_rewrites_json = table.Column<string>(type: "jsonb", nullable: false),
                    response_header_rewrites_json = table.Column<string>(type: "jsonb", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routes", x => x.id);
                    table.CheckConstraint("ck_routes_enum_values", "matcher_type IN ('Exact', 'ExactCaseInsensitive', 'Prefix', 'PrefixCaseInsensitive', 'Regex') AND target_type IN ('Microservice', 'StaticFile', 'ExtensionHandler') AND forwarding_mode IN ('Preserve', 'Strip', 'Replace')");
                    table.CheckConstraint("ck_routes_forwarding_template", "(forwarding_mode = 'Replace' AND replace_template IS NOT NULL AND length(replace_template) <= 4096) OR (forwarding_mode <> 'Replace' AND replace_template IS NULL)");
                    table.CheckConstraint("ck_routes_id_uuid_v7", "substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_routes_matcher_json", "jsonb_typeof(host_patterns_json) = 'array' AND jsonb_typeof(methods_json) = 'array' AND octet_length(host_patterns_json::text) <= 262144 AND octet_length(methods_json::text) <= 262144");
                    table.CheckConstraint("ck_routes_pattern_length", "length(pattern) BETWEEN 1 AND 4096");
                    table.CheckConstraint("ck_routes_rewrite_metadata_json", "jsonb_typeof(request_header_rewrites_json) = 'array' AND jsonb_typeof(response_header_rewrites_json) = 'array' AND jsonb_typeof(metadata_json) = 'object' AND octet_length(request_header_rewrites_json::text) <= 1048576 AND octet_length(response_header_rewrites_json::text) <= 1048576 AND octet_length(metadata_json::text) <= 1048576");
                    table.CheckConstraint("ck_routes_target_reference", "(target_type = 'Microservice' AND service_id IS NOT NULL AND target_id = service_id::text AND static_root_path IS NULL AND extension_handler_id IS NULL) OR (target_type = 'StaticFile' AND service_id IS NULL AND target_id = static_root_path AND static_root_path IS NOT NULL AND extension_handler_id IS NULL) OR (target_type = 'ExtensionHandler' AND service_id IS NULL AND target_id = extension_handler_id AND static_root_path IS NULL AND extension_handler_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_routes_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "nekostick",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "nekostick",
                table: "configuration_revisions",
                columns: new[] { "id", "committed_at", "committed_by", "created_at", "revision_key", "updated_at", "version" },
                values: new object[] { new Guid("018f0f00-0000-7000-8000-000000000001"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "system", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "global", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1L });

            migrationBuilder.InsertData(
                schema: "nekostick",
                table: "global_settings",
                columns: new[] { "id", "auto_port_range_end", "auto_port_range_start", "configuration_poll_interval_seconds", "created_at", "max_concurrent_requests", "max_request_body_bytes", "trusted_proxy_cidrs_json", "updated_at", "version" },
                values: new object[] { new Guid("018f0f00-0000-7000-8000-000000000002"), 29999, 20000, 30, new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1024, 31457280L, "[]", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1L });

            migrationBuilder.CreateIndex(
                name: "ux_configuration_revisions_revision_key",
                schema: "nekostick",
                table: "configuration_revisions",
                column: "revision_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_extension_records_extension_id",
                schema: "nekostick",
                table: "extension_records",
                column: "extension_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_extension_settings_extension_record_id",
                schema: "nekostick",
                table: "extension_settings",
                column: "extension_record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_nodes_default_node_id_active",
                schema: "nekostick",
                table: "nodes",
                column: "node_id",
                unique: true,
                filter: "node_id = '0' AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_port_leases_service_id",
                schema: "nekostick",
                table: "port_leases",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ux_port_leases_node_id_port",
                schema: "nekostick",
                table: "port_leases",
                columns: new[] { "node_id", "port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_enabled_matcher_type_priority",
                schema: "nekostick",
                table: "routes",
                columns: new[] { "enabled", "matcher_type", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_routes_service_id",
                schema: "nekostick",
                table: "routes",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_services_enabled",
                schema: "nekostick",
                table: "services",
                column: "enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_revisions",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "extension_settings",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "global_settings",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "port_leases",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "routes",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "extension_records",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "nodes",
                schema: "nekostick");

            migrationBuilder.DropTable(
                name: "services",
                schema: "nekostick");
        }
    }
}
