using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteResourceOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_requests",
                schema: "nekostick",
                table: "routes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "max_request_body_bytes",
                schema: "nekostick",
                table: "routes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "max_request_header_bytes",
                schema: "nekostick",
                table: "routes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "request_read_timeout_milliseconds",
                schema: "nekostick",
                table: "routes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_resource_limits",
                schema: "nekostick",
                table: "routes",
                sql: "(max_request_body_bytes IS NULL OR max_request_body_bytes BETWEEN 1 AND 31457280) AND (max_request_header_bytes IS NULL OR max_request_header_bytes BETWEEN 1 AND 32768) AND (max_concurrent_requests IS NULL OR max_concurrent_requests > 0) AND (request_read_timeout_milliseconds IS NULL OR request_read_timeout_milliseconds BETWEEN 1 AND 86400000)");

            migrationBuilder.Sql(
                """
                UPDATE "nekostick"."global_settings"
                SET "max_request_body_bytes" = 31457280
                WHERE "max_request_body_bytes" > 31457280;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_max_request_body_bytes",
                schema: "nekostick",
                table: "global_settings",
                sql: "max_request_body_bytes <= 31457280");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_resource_limits",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_max_request_body_bytes",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "max_concurrent_requests",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "max_request_body_bytes",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "max_request_header_bytes",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "request_read_timeout_milliseconds",
                schema: "nekostick",
                table: "routes");
        }
    }
}
