using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestLimitsAndRatePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_limits",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.AddColumn<int>(
                name: "client_ip_rate_queue_limit",
                schema: "nekostick",
                table: "routes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_ip_rate_rejection_behavior",
                schema: "nekostick",
                table: "routes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "client_ip_rate_replenishment_period_milliseconds",
                schema: "nekostick",
                table: "routes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_ip_rate_retry_after_behavior",
                schema: "nekostick",
                table: "routes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "client_ip_rate_token_limit",
                schema: "nekostick",
                table: "routes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "client_ip_rate_tokens_per_period",
                schema: "nekostick",
                table: "routes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "client_ip_rate_queue_limit",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_ip_rate_rejection_behavior",
                schema: "nekostick",
                table: "global_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "client_ip_rate_replenishment_period_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_ip_rate_retry_after_behavior",
                schema: "nekostick",
                table: "global_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "client_ip_rate_token_limit",
                schema: "nekostick",
                table: "global_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "client_ip_rate_tokens_per_period",
                schema: "nekostick",
                table: "global_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "max_request_header_bytes",
                schema: "nekostick",
                table: "global_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "request_read_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                schema: "nekostick",
                table: "global_settings",
                keyColumn: "id",
                keyValue: new Guid("018f0f00-0000-7000-8000-000000000002"),
                columns: new[] { "client_ip_rate_queue_limit", "client_ip_rate_rejection_behavior", "client_ip_rate_replenishment_period_milliseconds", "client_ip_rate_retry_after_behavior", "client_ip_rate_token_limit", "client_ip_rate_tokens_per_period", "max_request_header_bytes", "request_read_timeout_milliseconds" },
                values: new object[] { null, null, null, null, null, null, 32768L, 30000 });

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_limits",
                schema: "nekostick",
                table: "global_settings",
                sql: "max_request_body_bytes > 0 AND max_request_header_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0 AND request_read_timeout_milliseconds BETWEEN 1 AND 86400000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_max_request_header_bytes",
                schema: "nekostick",
                table: "global_settings",
                sql: "max_request_header_bytes <= 32768");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_limits",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_max_request_header_bytes",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_queue_limit",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_rejection_behavior",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_replenishment_period_milliseconds",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_retry_after_behavior",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_token_limit",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_tokens_per_period",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_queue_limit",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_rejection_behavior",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_replenishment_period_milliseconds",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_retry_after_behavior",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_token_limit",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "client_ip_rate_tokens_per_period",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "max_request_header_bytes",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "request_read_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_limits",
                schema: "nekostick",
                table: "global_settings",
                sql: "max_request_body_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0");
        }
    }
}
