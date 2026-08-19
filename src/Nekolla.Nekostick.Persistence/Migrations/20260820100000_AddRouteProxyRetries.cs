using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRouteProxyRetries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "proxy_max_retries",
            schema: "nekostick",
            table: "routes",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "proxy_initial_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "routes",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "proxy_maximum_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "routes",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "proxy_retry_on_connection_failure",
            schema: "nekostick",
            table: "routes",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "proxy_retry_on_upstream_disconnect",
            schema: "nekostick",
            table: "routes",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_routes_proxy_retries",
            schema: "nekostick",
            table: "routes",
            sql: "(proxy_max_retries IS NULL AND proxy_initial_retry_backoff_milliseconds IS NULL AND proxy_maximum_retry_backoff_milliseconds IS NULL AND proxy_retry_on_connection_failure IS NULL AND proxy_retry_on_upstream_disconnect IS NULL) OR (proxy_max_retries IS NOT NULL AND proxy_initial_retry_backoff_milliseconds IS NOT NULL AND proxy_maximum_retry_backoff_milliseconds IS NOT NULL AND proxy_retry_on_connection_failure IS NOT NULL AND proxy_retry_on_upstream_disconnect IS NOT NULL AND proxy_max_retries BETWEEN 0 AND 10 AND proxy_initial_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_maximum_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_initial_retry_backoff_milliseconds <= proxy_maximum_retry_backoff_milliseconds)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_routes_proxy_retries",
            schema: "nekostick",
            table: "routes");

        migrationBuilder.DropColumn(
            name: "proxy_max_retries",
            schema: "nekostick",
            table: "routes");

        migrationBuilder.DropColumn(
            name: "proxy_initial_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "routes");

        migrationBuilder.DropColumn(
            name: "proxy_maximum_retry_backoff_milliseconds",
            schema: "nekostick",
            table: "routes");

        migrationBuilder.DropColumn(
            name: "proxy_retry_on_connection_failure",
            schema: "nekostick",
            table: "routes");

        migrationBuilder.DropColumn(
            name: "proxy_retry_on_upstream_disconnect",
            schema: "nekostick",
            table: "routes");
    }
}
