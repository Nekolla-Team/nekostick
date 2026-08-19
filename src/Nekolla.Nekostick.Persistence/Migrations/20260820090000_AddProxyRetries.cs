using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProxyRetries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "proxy_max_retries",
            schema: "nekostick",
            table: "global_settings",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "proxy_initial_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "global_settings",
            type: "integer",
            nullable: false,
            defaultValue: 200);

        migrationBuilder.AddColumn<int>(
            name: "proxy_maximum_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "global_settings",
            type: "integer",
            nullable: false,
            defaultValue: 2000);

        migrationBuilder.AddColumn<bool>(
            name: "proxy_retry_on_connection_failure",
            schema: "nekostick",
            table: "global_settings",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "proxy_retry_on_upstream_disconnect",
            schema: "nekostick",
            table: "global_settings",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_global_settings_proxy_retries",
            schema: "nekostick",
            table: "global_settings",
            sql: "proxy_max_retries BETWEEN 0 AND 10 AND proxy_initial_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_maximum_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_initial_retry_backoff_milliseconds <= proxy_maximum_retry_backoff_milliseconds");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_global_settings_proxy_retries",
            schema: "nekostick",
            table: "global_settings");

        migrationBuilder.DropColumn(
            name: "proxy_max_retries",
            schema: "nekostick",
            table: "global_settings");

        migrationBuilder.DropColumn(
            name: "proxy_initial_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "global_settings");

        migrationBuilder.DropColumn(
            name: "proxy_maximum_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "global_settings");

        migrationBuilder.DropColumn(
            name: "proxy_retry_on_connection_failure",
            schema: "nekostick",
            table: "global_settings");

        migrationBuilder.DropColumn(
            name: "proxy_retry_on_upstream_disconnect",
            schema: "nekostick",
            table: "global_settings");
    }
}
