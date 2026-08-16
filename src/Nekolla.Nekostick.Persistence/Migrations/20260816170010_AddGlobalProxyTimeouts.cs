using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalProxyTimeouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "connect_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: false,
                defaultValue: 10000);

            migrationBuilder.AddColumn<int>(
                name: "http_activity_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30000);

            migrationBuilder.AddColumn<int>(
                name: "http_total_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: false,
                defaultValue: 100000);

            migrationBuilder.AddColumn<int>(
                name: "websocket_idle_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings",
                type: "integer",
                nullable: false,
                defaultValue: 120000);

            migrationBuilder.UpdateData(
                schema: "nekostick",
                table: "global_settings",
                keyColumn: "id",
                keyValue: new Guid("018f0f00-0000-7000-8000-000000000002"),
                columns: new[] { "connect_timeout_milliseconds", "http_activity_timeout_milliseconds", "http_total_timeout_milliseconds", "websocket_idle_timeout_milliseconds" },
                values: new object[] { 10000, 30000, 100000, 120000 });

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_proxy_timeouts",
                schema: "nekostick",
                table: "global_settings",
                sql: "connect_timeout_milliseconds BETWEEN 1 AND 86400000 AND http_activity_timeout_milliseconds BETWEEN 1 AND 86400000 AND http_total_timeout_milliseconds BETWEEN 1 AND 86400000 AND websocket_idle_timeout_milliseconds BETWEEN 1 AND 86400000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_proxy_timeouts",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "connect_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "http_activity_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "http_total_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.DropColumn(
                name: "websocket_idle_timeout_milliseconds",
                schema: "nekostick",
                table: "global_settings");
        }
    }
}
